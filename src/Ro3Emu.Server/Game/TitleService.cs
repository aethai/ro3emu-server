using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Title ranks (function 14, TitleConfig): 5031 AUTO_SYNC DBTitleInfo and 43102 TITLE_LEVEL. The original never opened
/// function 14 (base level 9999) and no client code hosts its widget; OpenContent opens it and
/// Ro3Emu.ClientHooks.TitleButton puts UI_Sys_Role_TitleWidget in the character window.
/// <list type="bullet">
/// <item>43102 (UI_Sys_Role_TitleWidgetCtrl.OnClickUpgrade, an empty request) claims the next rank: the row with the
/// smallest _kIndex above the current one (rank 1 "Beginner" from 0). The widget checks _iPower against
/// maxHistoryPower, _iLev against the base level and _kSpend against the bag before sending; the server checks the
/// same and takes _kSpend.</item>
/// <item>The new rank goes out as a 5031 increment: LC_DAL_Title.UpgradeUI plays the upgrade animation, whose end
/// event is what clears the widget's request lock (a full sync would leave it locked). 43102's reply is not read by
/// the client (its dataset TitleLevel has no handler) but is sent as the proto has it.</item>
/// <item>The rank adds _iPowerAdd to the titleScore power (PowerService.TitleId) and is the role's
/// RolePublicBaseInfo.title. _kAttr is [[101,0],[103,0],[104,0]] in every row, so no stat changes.</item>
/// </list>
/// Inferred: the Crests (_kSpend items 1250801-1250808) have no source in the client data; a boss kill drops one at
/// ServerOptions.TitleCrestPercent, of the tier its level reaches (see CrestDrops). The PvP damage counter by title
/// level (TitleBuffConfig, Formula_ATitleDamageAdd) is CombatService's TitleDamage.
/// </summary>
public sealed class TitleService
{
    public const uint Sync = 5031, Level = 43102;
    public const int Function = 14;

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly RoleStore _roles;
    private readonly ServerOptions _options;
    private readonly ILogger<TitleService> _log;
    private List<TitleConfig>? _ranks;

    public TitleService(GameData data, CharacterService character, FunctionService functions, RoleStore roles,
        PowerService power, RewardService rewards, GmCommands gm,
        Microsoft.Extensions.Options.IOptions<ServerOptions> options, ILogger<TitleService> log)
    {
        this._data = data; this._character = character; this._functions = functions; this._roles = roles;
        this._options = options.Value; this._log = log;
        power.TitleId = s => s.TitleInfo?.TitleId ?? 0;
        character.TitleOf = s => s.TitleInfo?.TitleId ?? 0;
        character.HeadTitleOf = HeadTitle;
        character.ExtraAttributes.Add(Attributes);
        rewards.ExtraKillDrops.Add(CrestDrops);
        gm.Register("title", "set <rank> | info (title rank test aid)", GmAsync);
    }

    /// <summary>TitleConfig rows in _kIndex order.</summary>
    private List<TitleConfig> Ranks() => _ranks ??=
        [.. _data.Table("TitleConfig").Values.Select(r => new TitleConfig(r)).Where(r => r.Id > 0).OrderBy(r => r.Index)];

    /// <summary>
    /// The role's title on the profile card (RolePublicBaseInfo.headtitle), which the card prints in its "Title" block
    /// (UI_Sys_Personal_InfoWidget Pan_Designation/HeadTitle), "Titleless" when it is 0. It is a NewbieTitlesConfig row,
    /// and the export's ids 1-7 are the Growth Manual titles. Our Title rank is what a player earns here, so the rank takes
    /// the slot: the client hook Ro3Emu.ClientHooks.ProfileTitle renames the seven rows' texts with the names of ranks 1-7
    /// (the same TitleConfig _iTitleName texts the Title panel shows), and a rank above seven shows the seventh. With no
    /// rank the highest Growth Manual title stands, as before.
    /// </summary>
    private long HeadTitle(GameSession s)
    {
        long rank = s.TitleInfo?.TitleId ?? 0;
        if (rank > 0 && _data.Row("TitleConfig", rank) != null)
            return Math.Min(rank, _data.Table("NewbieTitlesConfig").Count);
        if (s.Tasks == null) return 0; // unloaded tasks read as "everything delivered" in TaskService.IsDelivered
        long best = 0, lev = -1;
        foreach (var row in _data.Table("NewbieTitlesConfig").Values)
        {
            var title = new NewbieTitlesConfig(row);
            if (title.Lev <= lev || !TaskService.IsDelivered(s, title.TaskId)) continue;
            (best, lev) = (title.Id, title.Lev);
        }
        return best;
    }

    /// <summary>
    /// (inferred) The rank's flat HP (101), PATK (103) and MATK (104). TitleConfig._kAttr is [[101,0],[103,0],[104,0]] in
    /// all 21 rows of this export, so the client's rank panel prints +0 for the three and the rank changes nothing at all;
    /// the rank's _iPowerAdd is the only real number in the row, so the bonus is derived from it
    /// (Ro3Emu:Title:HpScale, Ro3Emu:Title:AttackScale). A row whose _kAttr carries a non-zero value wins untouched.
    /// Read straight from the row: constructing a TitleConfig needs every field, and the export's placeholder rows do not
    /// all carry them (a stats pass runs for every login).
    /// </summary>
    private IEnumerable<(long Id, double Value)> Attributes(GameSession s)
    {
        if (s.TitleInfo is not { } info || s.Progress == null || info.TitleId == 0) yield break;
        if (_data.Row("TitleConfig", info.TitleId) is not { } row) yield break;
        var configured = new List<(long Id, double Value)>();
        if (row.TryGetProperty("_kAttr", out var list) && list.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var pair in list.EnumerateArray())
                if (pair.ValueKind == System.Text.Json.JsonValueKind.Array && pair.GetArrayLength() >= 2
                    && GameData.Number(pair[1]) is { } value && value != 0)
                    configured.Add(((long)(GameData.Number(pair[0]) ?? 0), value));
        if (configured.Count > 0)
        {
            foreach (var entry in configured) yield return entry;
            yield break;
        }
        long power = GameData.Int(row, "_iPowerAdd");
        if (power == 0) yield break;
        yield return (101, Math.Round(power * _options.TitleHpScale));
        yield return (103, Math.Round(power * _options.TitleAttackScale));
        yield return (104, Math.Round(power * _options.TitleAttackScale));
    }

    /// <summary>The rank after <paramref name="current"/> (the first one from 0).</summary>
    private TitleConfig? Next(long current)
    {
        var ranks = Ranks();
        int at = ranks.FindIndex(r => r.Id == current);
        int next = at < 0 ? 0 : at + 1; // in _kIndex order; an unknown or missing rank starts from the first
        return next < ranks.Count ? ranks[next] : null;
    }

    public void Load(GameSession s)
    {
        var bytes = _roles.LoadTitle(s.Role!.Id);
        s.TitleInfo = bytes is { Length: > 0 } ? DBTitleInfo.Parser.ParseFrom(bytes) : new DBTitleInfo();
    }

    public Task LoginAsync(GameSession s) => s.TitleInfo == null ? Task.CompletedTask
        : CharacterService.SyncAsync(s, Sync, s.TitleInfo.Clone());

    public async Task LevelAsync(GameSession s)
    {
        int result;
        long title;
        using (await CharacterService.Lock(s))
        {
            result = await LevelLockedAsync(s);
            title = s.TitleInfo?.TitleId ?? 0;
        }
        await s.SendGameAsync(Level, new TitleLevelRsp { Result = result, TitleId = title });
    }

    private async Task<int> LevelLockedAsync(GameSession s)
    {
        if (s.TitleInfo == null || s.Progress == null) return ErrorCodes.Unknown;
        if (!_functions.IsOpen(s, Function)) return ErrorCodes.FunctionLocked;
        if (Next(s.TitleInfo.TitleId) is not { } next) return ErrorCodes.Parameter;
        if (s.Progress.Level < next.Lev) return ErrorCodes.LevelTooLow;
        if (PowerService.Total(PowerService.History(s)) < next.Power) return ErrorCodes.Parameter;
        var cost = (next.Spend ?? []).Where(p => p.Length >= 2 && p[1] > 0).Select(p => ((int)p[0], p[1])).ToList();
        var saved = new DBTitleInfo { TitleId = next.Id };
        byte[] bytes = saved.ToByteArray();
        long role = s.Role!.Id;
        var paid = await _character.ExchangeLockedAsync(s, cost, [], popup: false,
            persist: tx =>
            {
                tx.RoleBlob(role, "title", bytes);
                tx.WithEconomy(_character.FamilyEconomy(s, "cd13a/character-title", cost, [], new { Title = next.Id }));
            },
            committed: () => s.TitleInfo = saved);
        if (paid == null) return ErrorCodes.ItemsShort;
        await CharacterService.SyncAsync(s, Sync, s.TitleInfo.Clone()); // an increment after the login's full sync
        await _character.RefreshStatsAsync(s);
        return 0;
    }

    /// <summary>
    /// (inferred) A boss (NPCConfig _iNPCSubType 3, 4 or 6) drops a Crest at ServerOptions.TitleCrestPercent to a role
    /// whose title function is open: the tier of the boss's level (Journey below 50, then one tier per 10 levels, Sun
    /// 90-94, Heroes 95-99, the Hero Token from 100), matching the ranks that spend them.
    /// </summary>
    private IEnumerable<(int Entry, int Count)> CrestDrops(GameSession s, MonsterUnit monster)
    {
        if (s.Progress == null || !_functions.IsOpen(s, Function)) yield break;
        if (_data.Row("NPCConfig", monster.Template.NpcId) is not { } row) yield break;
        bool boss = new NPCConfig(row).NPCSubType is 3 or 4 or 6;
        int chance = boss ? _options.TitleCrestPercent : _options.TitleCrestFieldPercent;
        if (chance <= 0 || Random.Shared.Next(100) >= chance) yield break;
        int level = monster.Template.Level;
        int crest = level switch
        {
            < 50 => 1250801,
            < 60 => 1250802,
            < 70 => 1250803,
            < 80 => 1250804,
            < 90 => 1250805,
            < 95 => 1250806,
            < 100 => 1250807,
            _ => 1250808,
        };
        if (_data.Row("ItemConfig", crest) == null) yield break;
        _log.LogInformation("Title Crest {Item} dropped for role {Role} from npc {Npc} level {Level} (boss {Boss}, {Chance} %)",
            crest, s.Role!.Id, monster.Template.NpcId, level, boss, chance);
        yield return (crest, 1);
    }

    private async Task GmAsync(GameSession s, string[] a)
    {
        if (a.Length >= 2 && a[0] == "set" && long.TryParse(a[1], out long rank) && s.TitleInfo != null)
        {
            var saved = new DBTitleInfo { TitleId = rank };
            _roles.SaveTitle(s.Role!.Id, saved.ToByteArray());
            s.TitleInfo = saved;
            await CharacterService.SyncAsync(s, Sync, saved.Clone());
        }
        var next = Next(s.TitleInfo?.TitleId ?? 0);
        await GmCommands.ReplyAsync(s, $"title {s.TitleInfo?.TitleId ?? 0}, next {next?.Id ?? 0} (level {next?.Lev ?? 0}, power {next?.Power ?? 0}), "
            + $"history power {PowerService.Total(PowerService.History(s))}, open {_functions.IsOpen(s, Function)}");
    }
}
