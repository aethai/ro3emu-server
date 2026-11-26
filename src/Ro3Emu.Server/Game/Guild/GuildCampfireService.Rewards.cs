using System.Text.Json;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Bonfire Party's personal rewards besides the prize draw. From the client data:
/// <list type="bullet">
/// <item>The Party Blessing: CampFireAffixNewConfig action 7 (phase 0, the whole event) [[2], [award group 170001, Base
/// EXP by level]] first runs _iDelayTime (30 s) after the event starts, then every _iInterval (30 s), _iTriggerTime
/// (9999) times. The To-Do widget (UI_Sys_Main_TeamUp_GuildTinderTaskWidgetBaseCtrl) shows UIWords 110184 "Earn plenty of
/// EXP continuously in the guild territory." with CampFirePlayerInfo.bubblesRewardCount against
/// PublicParamConfig.CampfireBubbleRewardLimit (20): 110180 "Reward Count: n/20", then 110181 "Reward Count limit
/// reached". Stage text 12520000002: "Enjoy the Bonfire EXP blessing while waiting for the raffle to begin".</item>
/// <item>The final blow: action 6 on a monster's death (_iTriggerType 1, _iParams [10000, NPC]) is [[final-blow group],
/// [others' group], [0]]; phases 2-4 give the killer of their fruit monster 170006 and the others nothing (-1). The
/// earlier CampFireAction rows and CampFireAffix 602/603 ("Grant Boss Final Blow Reward", "Grant Boss Damage Reward
/// (excluding the Final Blow)") name the two shares.</item>
/// </list>
/// Inferred: action 7's [2] names every participant, as the [2] that opens every phase's action 5 draw; a participant is
/// an eligible member loaded in the guild home at the tick (110184: "in the guild territory"); the count is per event
/// day and a tick the server misses (a stall, a clock jump) is not paid later; _iParams[0] is the trigger's chance in
/// 10000ths; the others' share of action 6 (-1 in every exported row) is not paid.
/// </summary>
public sealed partial class GuildCampfireService
{
    internal const int BlessingAction = 7, KillAwardAction = 6, MonsterDeathTrigger = 1;
    private const int ChanceScale = 10000;

    /// <summary>Room id: the event day and the last Party Blessing tick paid in it.</summary>
    private readonly Dictionary<long, (long Day, long Tick)> _blessed = [];

    private CampFireAffixNewConfig? Blessing() =>
        CampFireAffixNewConfig.All(_data).FirstOrDefault(r => r.ActionType == BlessingAction) is { Row.ValueKind: JsonValueKind.Object } row
            ? row : null;

    private int BlessingLimit() => (int)Math.Clamp(PublicParams.Of(_data).CampfireBubbleRewardLimit ?? 0, 0, int.MaxValue);

    /// <summary>The blessing tick due at <paramref name="second"/>: 1 at _iDelayTime, one more each _iInterval (0 before).</summary>
    private long BlessingTick(CampFireAffixNewConfig row, long second)
    {
        long elapsed = (second - StartTime()) * 1000 - row.DelayTime;
        if (elapsed < 0) return 0;
        long tick = row.Interval > 0 ? elapsed / row.Interval + 1 : 1;
        return Math.Min(tick, Math.Max(1, row.TriggerTime));
    }

    /// <summary>Tick: each live room's due blessing, once per room, day and tick.</summary>
    private void RunBlessings(IEnumerable<(long Guild, InstanceService.Room Room)> live, int stage, long day, long second)
    {
        if (!Running(stage) || Blessing() is not { } row) return;
        long tick = BlessingTick(row, second);
        if (tick <= 0) return;
        foreach (var (guild, room) in live)
        {
            if (_blessed.TryGetValue(room.Id, out var paid) && paid.Day == day && paid.Tick >= tick) continue;
            _blessed[room.Id] = (day, tick);
            _loop.TrackLegacyDetached("campfire blessing", () => BlessAsync(guild, room, row));
        }
    }

    /// <summary>One blessing: the award group to each participant below the limit, the count committed with it.</summary>
    private async Task BlessAsync(long guild, InstanceService.Room room, CampFireAffixNewConfig row)
    {
        long group = row.ActionParams is [_, [var award, ..], ..] ? award : 0;
        int limit = BlessingLimit();
        if (room.Closed || group <= 0 || limit <= 0) return;
        using var scope = await EnterWithGuildGateAsync("campfire/blessing", guild,
            room.ActiveMembers.Select(m => m.Session.Role?.Id ?? 0));
        try
        {
            var state = Read(guild);
            foreach (var s in Participants(guild, room))
            {
                if (s.Role is not { Id: var role }) continue;
                int count = state.Blessings.GetValueOrDefault(role);
                if (count >= limit) continue;
                state.Blessings[role] = count + 1;
                if (await PayAsync(s, guild, "guild-campfire-blessing", $"guild-campfire/blessing/{guild}/{Today()}/{role}/{count + 1}",
                        [group], new { Guild = guild, Group = group, Count = count + 1 }, state))
                    await PlayerInfoAsync(s);
                else state.Blessings[role] = count;
            }
        }
        finally { Gate(guild).Release(); }
    }

    /// <summary>The eligible members of the guild loaded in its home room.</summary>
    private GameSession[] Participants(long guild, InstanceService.Room room) => [.. room.ActiveMembers
        .Select(m => m.Session)
        .Where(s => s.Role != null && s.InstanceId == room.Id && s.SceneEntry == Dungeon && !s.SceneLoading
            && GuildId(s) == guild && Eligible(s))];

    /// <summary>Action 6 of the stage for the dying NPC: its final-blow group, when the trigger's chance hits.</summary>
    private long KillAward(int stage, long npc)
    {
        var row = CampFireAffixNewConfig.All(_data).FirstOrDefault(r => r.Phase == stage && r.ActionType == KillAwardAction
            && r.TriggerType == MonsterDeathTrigger && r.Params is [_, var id, ..] && id == npc);
        if (row.Row.ValueKind != JsonValueKind.Object || row.Params is not [var chance, ..]
            || Random.Shared.Next(ChanceScale) >= chance) return 0;
        return row.ActionParams is [[var group, ..], ..] && group > 0 ? group : 0;
    }

    /// <summary>The final-blow reward of a fruit kill, inside the kill's guild gate.</summary>
    private async Task PayKillAwardAsync(GameSession killer, long guild, int stage, MonsterUnit monster)
    {
        long group = KillAward(stage, monster.Template.NpcId);
        if (group <= 0 || killer.Role is not { } role) return;
        await PayAsync(killer, guild, "guild-campfire-kill", $"guild-campfire/kill/{guild}/{monster.Uid}/{role.Id}",
            [group], new { Guild = guild, Stage = stage, Group = group, Monster = monster.Template.NpcId });
    }

    /// <summary>Pays <paramref name="groups"/> at once; a changed campfire state commits in the same transaction.</summary>
    private async Task<bool> PayAsync(GameSession s, long guild, string kind, string source, long[] groups, object outcome,
        StateData? state = null)
    {
        var grants = groups.SelectMany(g => _awards.Resolve(s, g).Grants).ToArray();
        byte[]? bytes = state == null ? null : JsonSerializer.SerializeToUtf8Bytes(state);
        return await _character.ApplyAwardAsync(s, new AwardPlan(0, 0, grants), persist: tx =>
        {
            if (bytes != null) tx.Add(new GuildCampfireStatePlan(guild, bytes));
            if (grants.Any(g => g.Entry is not (ItemId.BaseExp or ItemId.JobExp)))
                tx.WithEventEconomy(EventEconomy.ForRole(_data, s, kind, source, [], grants, outcome));
        });
    }
}
