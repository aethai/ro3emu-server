using System.Globalization;
using System.Runtime.CompilerServices;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild War Technology, the Institute's page of the Guild Technology window (FunctionConfig 1340). The client asks for the
/// levels with 32304 when the page opens and buys the next level of a group with 32305
/// (LC_GuildServer.RequestGuildTech / RequestLevelUpGuildTech); both answers carry the whole group-to-level map, which
/// LC_DAL_Guild._updateQueryTechInfo shallow-copies into GuildTechInfo. A level costs Guild Supplies (_iCostMoney
/// [126, n]) and needs its _iLimit condition (an Institute level, LC_GuildHelper.GetTechStatus). Text 10960000024: the
/// levels reset every season, and in Stronghold, Convoy Escort and Siege Battles they add to every member's stats.
/// </summary>
public sealed class GuildTechService
{
    public const uint Query = 32304, Operate = 32305; // GUILD_QUERY_TECH, GUILD_OPERATE_TECH
    public const int Function = 1340; // FunctionConfig Function_GuildTech, "Guild Technology - Technology"
    /// <summary>GuildWarTechConfig._iPlayingMethodID values; the mapping comes from the rows' description 13350100000
    /// "Active in Guild Stronghold Battles, Escort Battles, and Siege Battles" and their order (inferred).</summary>
    public const long StrongholdBattle = 1000, EscortBattle = 1001, SiegeBattle = 1002;
    private const long SuppliesItem = 126; // ItemConfig 126, Guild Supplies (GuildStore.State.Materials)
    private const long AttributeOp = 1; // SpecialEffectConfig op [1, attribute, value]

    private readonly GuildService _guilds;
    private readonly GuildStore _store;
    private readonly SeasonService _seasons;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly GameData _data;
    private readonly Lazy<Dictionary<(long Group, long Level), GuildWarTechConfig>> _rows;
    private readonly Lazy<long[]> _groups;

    /// <summary>The playing method a member is fighting in right now (one of the constants above), 0 for none. The
    /// territory services answer for their battles.</summary>
    public OrderedHook<Func<GameSession, long>> PlayingMethods { get; } = new("GuildTechService.PlayingMethods");

    public GuildTechService(GuildService guilds, GuildStore store, SeasonService seasons, CharacterService character,
        FunctionService functions, GameData data, SceneService scene)
    {
        _guilds = guilds;
        _store = store;
        _seasons = seasons;
        _character = character;
        _functions = functions;
        _data = data;
        _rows = new(() => GuildWarTechConfig.All(data).ToDictionary(r => (r.Group, r.Level)));
        _groups = new(() => [.. GuildWarTechConfig.All(data).Select(r => r.Group).Distinct().Order()]);
        functions.GuildBuildingLevel = BuildingLevel;
        character.ExtraAttributes.Subscribe(Attributes);
        character.GuildTechKey = MemoKey;
        // A map load is where a member enters or leaves a battle; a guild change swaps the level map.
        scene.Loaded.Subscribe(SyncAsync);
        guilds.GuildIdChanged.Subscribe(s =>
        {
            if (guilds.Loop.Phase == GameLoopPhase.Accepting) guilds.Loop.Post(() => SyncAsync(s));
        }, name: "GuildTechService.SyncOnGuildIdChanged");
    }

    private long? BuildingLevel(GameSession session, long type) =>
        session.Role != null && _store.ForRole(session.Role.Id) is { } g && g.Buildings.TryGetValue(type, out long level) ? level : null;

    private long Season => _seasons.StartTime;

    /// <summary>A group's level this season.</summary>
    internal long Level(GuildStore.State g, long group) => g.TechSeason == Season ? g.TechLevels.GetValueOrDefault(group) : 0;

    /// <summary>Every group with its level, zeros included: the client copies the map over its previous one, so a group left
    /// out would keep a level from another guild or season.</summary>
    private GuildTechInfo Info(GuildStore.State g)
    {
        var info = new GuildTechInfo();
        foreach (long group in _groups.Value) info.TechLevel[group] = Level(g, group);
        return info;
    }

    private int Access(GameSession s, GuildStore.State? g) => g == null ? ErrorCodes.NotInGuild
        : g.Status != (int)GuildStatus.Normal || !_functions.IsOpen(s, Function) ? ErrorCodes.FeatureUnavailable : 0;

    public Task QueryAsync(GameSession s) => _guilds.RunGuildAsync(s, [], [], () =>
    {
        var g = _guilds.Own(s);
        int code = Access(s, g);
        return s.SendGameAsync(Query, new QueryGuildTechRes { Result = code, TechInfo = code == 0 ? Info(g!) : null });
    });

    public Task OperateAsync(GameSession s, OperateGuildTechReq req) => _guilds.RunGuildAsync(s, [], [], () => OperateCoreAsync(s, req));

    private async Task OperateCoreAsync(GameSession s, OperateGuildTechReq req)
    {
        var g = _guilds.Own(s);
        long role = s.Role?.Id ?? 0;
        int code = Access(s, g);
        long price = 0;
        if (code == 0) code = Check(s, g!, role, req, out price);
        if (code == 0 && !await BuyAsync(s, g!, role, req, price)) code = ErrorCodes.GuildMissing;
        await s.SendGameAsync(Operate, new OperateGuildTechRes { Result = code, TechInfo = code == 0 ? Info(g!) : null });
        if (code == 0) await PublishAsync(s, g!);
    }

    /// <summary>The refusals in the order the client's own checks show them: the max level (541), the Institute (542),
    /// the supplies (535). Spending needs the Building Level-up right (GuildPermissionConfig 11): the client does not check
    /// a right and no research right is exported (inferred).</summary>
    private int Check(GameSession s, GuildStore.State g, long role, OperateGuildTechReq req, out long price)
    {
        price = 0;
        if (!_guilds.HasRight(g, role, GuildRight.UpgradeBuilding)) return ErrorCodes.GuildPermission;
        long current = Level(g, req.TechId);
        if (!_rows.Value.TryGetValue((req.TechId, current + 1), out var row))
            return _rows.Value.ContainsKey((req.TechId, current)) && current > 0 ? ErrorCodes.GuildTechMax : ErrorCodes.Parameter;
        if (req.Level != current + 1) return ErrorCodes.Parameter;
        if (!_functions.ConditionMet(s, row.Limit)) return ErrorCodes.GuildTechLocked;
        if (row.CostMoney is not [SuppliesItem, > 0 and var cost]) return ErrorCodes.Parameter; // (inferred) no other currency is exported
        price = cost;
        return g.Materials < price ? ErrorCodes.GuildSupplies : 0;
    }

    private async Task<bool> BuyAsync(GameSession s, GuildStore.State g, long role, OperateGuildTechReq req, long price)
    {
        long season = Season;
        var levels = g.TechSeason == season ? new Dictionary<long, long>(g.TechLevels) : [];
        levels[req.TechId] = req.Level;
        (var oldLevels, long oldSeason, long oldMaterials) = (g.TechLevels, g.TechSeason, g.Materials);
        (g.TechLevels, g.TechSeason, g.Materials) = (levels, season, g.Materials - price);
        string source = string.Create(CultureInfo.InvariantCulture, $"{g.Id}:{season}:{req.TechId}:{req.Level}");
        var economy = new EconomyEnvelope(Guid.NewGuid().ToString("N"), s.Account!, role, "guild-tech", source,
            "guild-tech:" + source, _guilds.NowMs(),
            [new(new(EconomyAssetKind.GuildResource, EconomyDenominations.GuildMaterials),
                new(EconomyOwnerKind.Guild, g.Id.ToString(CultureInfo.InvariantCulture)), EconomyCompartment.GuildTreasury,
                -price, EconomyLegKind.Sink, EconomyReason.GuildResourceSpend)], []);
        if (await _guilds.SaveGuildAsync(g, economy: economy)) return true;
        (g.TechLevels, g.TechSeason, g.Materials) = (oldLevels, oldSeason, oldMaterials);
        return false;
    }

    /// <summary>The other members' pages and the supplies counter follow the purchase; a member fighting in one of the
    /// level's playing methods gets the new stats at once.</summary>
    private async Task PublishAsync(GameSession buyer, GuildStore.State g)
    {
        await _guilds.PushBase(g, new GuildBaseInfoChangeNtf
        {
            Tp = GuildBaseInfoChangeNtf.Types.ChangeType.ChangeResource, Resource = GuildService.Resource(g)
        });
        var update = new QueryGuildTechRes { TechInfo = Info(g) };
        foreach (var member in g.Members.Values)
        {
            if (_guilds.Online(member.RoleId) is not { Role: not null, Progress: not null } peer) continue;
            if (!ReferenceEquals(peer, buyer)) await peer.SendGameAsync(Query, update);
            if (PlayingMethod(peer) is not (not 0 and var method)) continue;
            Counted(peer).Value = (method, g.TechLevels);
            await _character.RefreshStatsAsync(peer);
        }
    }

    /// <summary>The <see cref="MemoKey"/> each session's published stats last counted; gone with the session.</summary>
    private readonly ConditionalWeakTable<GameSession, StrongBox<(long Method, object? Levels)>> _counted = [];

    private StrongBox<(long Method, object? Levels)> Counted(GameSession s) => _counted.GetValue(s, _ => new());

    /// <summary>Republishes a member's stats when what the levels count for changed since the last time: a battle started
    /// or ended, the member entered or left one, changed guild, or the season turned. The territory clocks call it each
    /// second for the members of their battles, the escort on its stage changes; every map load and guild change too.</summary>
    public Task SyncAsync(GameSession s)
    {
        if (s.Role == null || s.Progress == null || s.SceneLoading) return Task.CompletedTask;
        var key = MemoKey(s);
        var counted = Counted(s);
        if (counted.Value.Method == key.Method && ReferenceEquals(counted.Value.Levels, key.Levels)) return Task.CompletedTask;
        counted.Value = key;
        return _character.RefreshStatsAsync(s);
    }

    /// <summary>What <see cref="Attributes"/> reads: the playing method and the guild's level map (replaced on every
    /// purchase), both zero / null without levels this season. The stats memo keys on it, so a battle starting, a season
    /// turning or a guild change takes effect in the next sheet, before any republish.</summary>
    internal (long Method, object? Levels) MemoKey(GameSession s) =>
        s.Role != null && _store.ForRole(s.Role.Id) is { } g && g.TechSeason == Season && g.TechLevels.Count != 0
            && PlayingMethod(s) is not 0 and var method ? (method, g.TechLevels) : default;

    internal long PlayingMethod(GameSession session)
    {
        foreach (var method in PlayingMethods.Handlers)
            if (method(session) is var id and not 0) return id;
        return 0;
    }

    /// <summary>The levels' attributes while the member fights in a playing method the level lists. A row holds the level's
    /// whole bonus (STR Bonus: 10 at level 1, 50 at level 5). The "Test Buff" and "Test Skill" rows' buff and skill
    /// effects (ops 2 and 6) are not applied.</summary>
    private IEnumerable<(long Id, double Value)> Attributes(GameSession session)
    {
        if (MemoKey(session) is not (not 0 and var method, Dictionary<long, long> levels)) return [];
        List<(long, double)> attributes = [];
        foreach (var (group, level) in levels)
        {
            if (!_rows.Value.TryGetValue((group, level), out var row) || !row.PlayingMethods.Contains(method)) continue;
            foreach (long id in row.SpecialEffects)
                foreach (var op in SpecialEffectConfig.Get(_data, id)?.SpecialEffect ?? [])
                    if (op is [AttributeOp, var attribute, var value]) attributes.Add((attribute, value));
        }
        return attributes;
    }
}
