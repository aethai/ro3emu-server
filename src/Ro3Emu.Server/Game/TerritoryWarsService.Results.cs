using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    private SceneStatisticsInfo Statistics(BattleState r)
    {
        var scene = new SceneStatisticsInfo { SceneId = r.StatisticsId, DungeonId = (int)r.Scene };
        foreach (var p in r.Players.Values)
            scene.RoleStatistics[p.Id] = new RoleStatisticsInfo
            {
                BaseInfo = new StatisticsRoleBaseInfo
                {
                    Id = p.Id, Area = 1, Name = p.Name, Job = p.Job, GuildId = p.Guild,
                    RewardData = new StatisticsAwardCheckData { PlayerLevel = p.Level, Job = p.Job }
                },
                TotalDamage = p.Damage, TotalBeDamage = p.Taken, TotalCure = p.Healed, Totalscore = p.Score,
                // EStatisticsModuleEnum keys the personal record reads
                // (UI_Sys_TerritoryWars_PushUp_Achievements_ListWidgetCtrl, UI_Sys_TerritoryWarsReportListWidgetCtrl).
                ExtraData =
                {
                    [1001001] = new ExtraValueData { LongValue = p.Damage }, // Damage
                    [1001002] = new ExtraValueData { LongValue = p.Taken }, // TakeDamage
                    [1001003] = new ExtraValueData { LongValue = p.Healed }, // heal
                    [1001004] = new ExtraValueData { LongValue = p.Kills }, // KillPlayer
                    [1001005] = new ExtraValueData { LongValue = p.Assists }, // AssistKill
                    [1021002] = new ExtraValueData { LongValue = p.MachineKills }, // DestroyMachinery
                    [1021003] = new ExtraValueData { LongValue = p.MachineDamage }, // MachineryDamage
                    [1031001] = new ExtraValueData { LongValue = p.Score }, // TerritoryWarPersonIntegral (inferred: the score)
                },
            };
        foreach (long guild in r.Guilds)
            if (_guildStore.Get(guild) is { } g)
                scene.GuildStatistics[guild] = new GuildStatisticsInfo
                {
                    GuildId = guild, GuildName = g.Name,
                    GuildIcon = new GuildHeadIcon { IconType = g.Icon, BackgroundType = g.Background }
                };
        return scene;
    }
    private DungeonStatisticsInfoWithDungeon StatisticsReport(BattleState r) => new()
    {
        StatisticsId = r.StatisticsId, DungeonId = (int)r.Scene, StatisticsTime = (r.Settled ? r.End : _clock.Now) * 1000,
        SceneStatistics = { [r.StatisticsId] = Statistics(r) }
    };

    /// <summary>
    /// The last crystal owner wins the capital/siege (GVGConfig help). Point-war score ranks capture/hold work;
    /// equal scores retain the incumbent, then guild id gives a stable order (inferred tie policy).
    /// Ownership, report and per-role reward outboxes commit together before any client is notified.
    /// </summary>
    private async Task SettleAsync(int key)
    {
        if (state.Battles[key].Settled) return;
        var next = Copy(state); var r = next.Battles[key];
        long incumbent = r.Points[0].Owner;
        long winner = r.Round >= 2 ? incumbent : r.Scores.Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value).ThenByDescending(x => x.Key == incumbent).ThenBy(x => x.Key).Select(x => x.Key).FirstOrDefault();
        if (winner == 0 && r.Guilds.Contains(incumbent)) winner = incumbent;
        r.Winner = winner; r.Settled = true;
        AssignTerritoryOwner(next, r.Zone, winner);
        // MG_Dungeon cause enums: this settlement is triggered by the scheduled stage timeout;
        // LC_DAL_TerritoryWars determines each recipient's victory from winEntityId, not the round number.
        var result = new DungeonResultInfo
        {
            FirstResultType = 1, SecondResultType = 1, Result = winner == 0 ? 0 : 1,
            WinEntityId = winner, StatisticsId = r.StatisticsId, DungeonId = (int)r.Scene, StatisticsTime = r.End * 1000,
            DungeonStartTimestamp = r.Start * 1000, SceneStatistics = { [r.StatisticsId] = Statistics(r) }
        };
        r.Result = result.ToByteArray();
        if (!next.Results.TryGetValue(r.Zone, out var list)) next.Results[r.Zone] = list = [];
        list.Add(r.Result);
        if (list.Count > 20) list.RemoveRange(0, list.Count - 20); // bounded history retention (inferred).
        var roleChanges = new Dictionary<long, RoleState>();
        int rank = 0;
        foreach (var p in r.Players.Values.Where(p => p.Loaded).OrderByDescending(p => p.Score).ThenByDescending(p => p.Damage).ThenBy(p => p.Id))
        {
            rank++;
            var role = LoadRole(p.Id);
            string awardKey = $"{_clock.Epoch}:{r.Round}:{r.Zone}";
            if (role.Paid.Contains(awardKey) || role.Pending.ContainsKey(awardKey)) continue;
            role.Honor += p.Score; role.Valor += p.Kills;
            var recipient = _sessions.ByRole(p.Id)
                ?? new GameSession(null!, null!) { Role = _roles.Get(p.Id), Progress = _roles.LoadProgress(p.Id) };
            var goods = new List<Grant>();
            if (recipient.Role != null)
            {
                var pp = Data.Config.PublicParams.Of(_data);
                AddAward(goods, (p.Guild == winner ? pp.TerritoryStrongHoldOccupyAuctionAward : pp.TerritoryStrongHoldJoinAuctionAward) ?? 1001006, recipient);
                // Settlement tables are rank/qualification awards. Roll once here and persist the exact goods.
                AddRankAward(goods, "SiegeSettlementPrimaryAward", rank, recipient);
                // SiegeSettlementHonorAward is the Battle Merit season reward by board place (the client's
                // BattleFeatsSettlementPanel and Season Honor widgets), mailed once per season by BattleGlorySeason;
                // paying it per battle as well would pay it twice.
                if (p.Guild == winner && r.Round >= 2) AddRankAward(goods, "SiegeSettlementSeniorAward", rank, recipient);
            }
            role.Pending[awardKey] = [.. goods.GroupBy(x => (x.Entry, x.Bind)).Select(g => new Grant(g.Key.Entry, checked(g.Sum(x => x.Count)), g.Key.Bind))];
            roleChanges[p.Id] = role;
        }
        var transaction = _accounts.NewTransaction();
        Save(next, transaction);
        foreach (var (id, role) in roleChanges) SaveRole(id, role, transaction);
        string settlementKey = $"{_clock.Epoch}:{r.Round}:{r.Zone}";
        var awards = roleChanges.ToDictionary(pair => pair.Key, pair => pair.Value.Pending[settlementKey]);
        if (TerritoryWarsEconomy.PendingSource(_data, _loop.NowMs, awards, "battle-settle", $"territory-battle-settle:{next.Epoch}:{r.Round}:{r.Zone}") is { } reward)
            transaction.WithEventEconomy(reward);
        await _roles.CommitAsync(transaction, "territory/settle");
        PublishCommitted(next);
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && roleChanges.ContainsKey(s.Role.Id)), s =>
        {
            if (s.Role is { } current && roleChanges.TryGetValue(current.Id, out var role)) s.TerritoryWars = role;
            return Task.CompletedTask;
        });
        await RoleFanout.EachAsync(_sessions.All().Where(s => s.Role != null && r.Players.ContainsKey(s.Role.Id)), async s =>
        {
            try
            {
                await DeliverAsync(s); await SyncSiegeAsync(s);
                await Send(s, ResultNotify, new TerritoryWarsDungeonResultNotify { CommonResultInfo = result });
                if (s.TerritoryBattle is { } b && b.Key == key) await Send(s, 30401, BaseInfo(b));
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or System.Net.Sockets.SocketException)
            { _log.LogDebug(e, "Territory settlement delivery deferred for disconnected role {Role}", s.Role?.Id); }
        });
        foreach (var p in r.Players.Values.Where(p => p.Loaded))
            foreach (var handler in BattleSettled.Handlers) handler(p.Id, r.Round);
        // The battle's war machines end with it, outside the territory gate (their owners' slots are the guild's).
        if (_battles.GetValueOrDefault(key) is { } room)
            _loop.PostLegacy(async () => { await EndPlatformsAsync(room); await EndWarMachinesAsync(room.Room); });
        await PublishAsync();
    }
    internal void AddRankAward(List<Grant> grants, string table, int rank, GameSession s)
    {
        foreach (var row in _data.Table(table).Values)
        {
            if (row.TryGetProperty("_kRank", out var range) && range.ValueKind == JsonValueKind.Array && range.GetArrayLength() >= 2
                && (rank < GameData.Number(range[0]) || rank > GameData.Number(range[1]))) continue;
            if (row.TryGetProperty("_kMultiverseArray", out var mv) && mv.ValueKind == JsonValueKind.Array
                && mv.GetArrayLength() > 0 && !mv.EnumerateArray().Any(v => GameData.Number(v) == _clock.PlayBook)) continue;
            AddAward(grants, GameData.Int(row, "_iAwardGroupID"), s); break;
        }
    }
    internal void AddAward(List<Grant> grants, long group, GameSession s)
    {
        long id = group;
        if (_data.Row("MultiverseGroupConfig", group) is { } mg)
        { id = GameData.Int(mg, $"_iValue_{_clock.PlayBook}"); if (id == 0) id = GameData.Int(mg, "_iValue_default"); }
        if (_data.Row("AwardConfig", id) is not { } raw) return;
        var row = raw.ValueKind == JsonValueKind.Array ? raw[0] : raw;
        if (row.TryGetProperty("_kAward", out var fixedItems) && fixedItems.ValueKind == JsonValueKind.Array)
            foreach (var t in fixedItems.EnumerateArray())
                if (t.ValueKind == JsonValueKind.Array && t.GetArrayLength() >= 2 && GameData.Number(t[1]) is > 0)
                    grants.Add(new Grant((int)(GameData.Number(t[0]) ?? 0), (int)(GameData.Number(t[1]) ?? 0), t.GetArrayLength() > 2 ? (int)(GameData.Number(t[2]) ?? 0) : 0));
        grants.AddRange(_rewards.RollAwardBound(id, s).Select(g => new Grant(g.Entry, g.Count, g.Bind)));
    }

    /// <summary>Shared dungeon report IDs return the same real counters as 83015; a role may query its own reports.</summary>
    public async Task<bool> SharedAsync(GameSession s, uint id, ReadOnlyMemory<byte> body)
    {
        if (s.Role == null) return false;
        if (id == 30114)
        {
            var req = DungeonStatisticsInfoWithDungeonReq.Parser.ParseFrom(body.Span);
            var records = state.Battles.Values.Where(r => req.StaisticsIds.Contains(r.StatisticsId)).ToArray();
            if (records.Length == 0) return false;
            var msg = new DungeonStatisticsInfoWithDungeonResp();
            if (req.StaisticsIds.Count > 20 || records.Length != req.StaisticsIds.Distinct().Count() || records.Any(r => !r.Players.ContainsKey(s.Role.Id))) msg.Result = ErrorCodes.NoPermission;
            else msg.DungeonStatistics.AddRange(records.Select(StatisticsReport));
            await Send(s, id, msg); return true;
        }
        if (s.TerritoryBattle is not { } b) return false;
        if (id == 30106) return true; // ClientDungeonEvent cannot author a capture, score or final result.
        if (id == 30117) await Send(s, id, new GetDungeonStatisticsInDungeonResp { DungeonStatistics = StatisticsReport(b.State) });
        else if (id == 30116)
        {
            var report = StatisticsReport(b.State);
            var msg = new DungeonStatisticsModuleRsp { StatisticsId = report.StatisticsId, DungeonId = report.DungeonId, StatisticsTime = report.StatisticsTime };
            msg.SceneStatistics.Add(report.SceneStatistics); await Send(s, id, msg);
        }
        else if (id == 30110)
        {
            var msg = new DungeonStatisticsRes(); var map = new DungeonStatisticsInfo { DungeonMapId = b.State.Scene };
            foreach (var p in b.State.Players.Values) map.DungeonRolesInfo.Add(new DungeonRoleInfo
            {
                RoleBase = new ObjScoreBaseInfo { Id = p.Id, Name = p.Name, Job = p.Job, Area = 1 }, TotalDamage = p.Damage,
                TotalBeDamage = p.Taken, TotalCure = p.Healed, Totalscore = p.Score
            });
            msg.DungeonRolesInfo[b.State.StatisticsId] = map; await Send(s, id, msg);
        }
        else return false;
        return true;
    }
}
