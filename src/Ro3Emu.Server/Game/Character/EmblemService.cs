using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Soul Echo: generated itemInfos 1001-1100, saved 5005 plans,
/// 82422/82423 deployment and 82424 feeding. Worn instances remain in ordinary pack 5 with Grid=0;
/// itemInfos 10001/10002 and the active plan are two projections of the same selection.
/// </summary>
public sealed partial class EmblemService
{
    public const uint PlanSync = 5005, SetUp = 82422, SetDown = 82423, Upgrade = 82424;
    public const int Function = 55; // FunctionConfig: Soul Echo
    // Five physical slots: UI_Sys_SoulDetailsWidgetCtrl.lua:94-110 builds five widgets (:663-670 bounds the position).
    private const int FirstSlot = 1, LastSlot = 5, Source = 10001, Position = 10002;
    private const int Level = 1001, Exp = 1002, TotalExp = 1003;
    private static readonly IReadOnlyDictionary<int, long> MaterialXp = new Dictionary<int, long>
    {
        [1257000] = 100, [1257001] = 1000, [1257002] = 10000,
    };

    public sealed class EmblemState
    {
        public Dictionary<int, Plan> Plans { get; set; } = [];
        public int CurrentPlan { get; set; }
        public long HistorySeasonPower { get; set; }
        /// <summary>The role's published and best power total (PowerService); null before its first publish.</summary>
        public PowerService.Record? Power { get; set; }
    }

    public sealed class Plan
    {
        public Dictionary<int, long> Slots { get; set; } = [];
        public string Name { get; set; } = "";
    }

    private readonly GameData _data;
    private readonly CharacterService _character;
    private readonly FunctionService _functions;
    private readonly SeasonService _seasons;
    private readonly RoleStore _roles;
    private readonly ILogger<EmblemService> _log;

    /// <summary>After a committed 82424 upgrade: the levels it gained (quest target 317002). Listeners post their work.</summary>
    public SyncHook<GameSession, int> Upgraded { get; } = new("EmblemService.Upgraded");

    public EmblemService(GameData data, CharacterService character, FunctionService functions, SeasonService seasons,
        RoleStore roles, ILogger<EmblemService> log)
    {
        this._data = data;
        this._character = character;
        this._functions = functions;
        this._seasons = seasons;
        this._roles = roles;
        this._log = log;
        character.EmblemAttrFactory = GenerateAttrs;
        character.ExtraAttributes.Subscribe(ResonanceAttributes);
    }

    /// <summary>Set by PowerService: publish 31502/31503/5066 after a Soul Echo change (the caller holds the lock).</summary>
    internal Func<GameSession, Task>? PublishPower { get; set; }

    /// <summary>Load before 5009 and ENTITYSELF; roll missing old instance attrs and repair stale active refs (inferred).</summary>
    public async Task LoadAsync(GameSession session)
    {
        var saved = _roles.LoadEmblems(session.Role!.Id);
        var next = saved == null ? new EmblemState() : JsonSerializer.Deserialize<EmblemState>(saved) ?? new EmblemState();
        var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
        var afterItems = beforeItems.Select(i => i.Clone()).ToList();
        bool changed = saved == null;
        // Plan key 0 is an inferred default. Empty grids are initialized by LC_DAL_Knapsack.InitGridInfo:1239-1281,
        // called before processing the initial 5009 at :134-137; LC_DAL_Emblem.IsSchemeEmpty accepts an empty plan.
        for (int index = 0; index < PlanCount; index++)
            if (!next.Plans.ContainsKey(index)) { next.Plans[index] = new Plan(); changed = true; }
        if (!next.Plans.ContainsKey(next.CurrentPlan))
        {
            next.CurrentPlan = next.Plans.ContainsKey(0) ? 0 : next.Plans.Keys.Min();
            changed = true;
        }
        foreach (var item in afterItems.Where(i => IsEmblem(_data, i.Entry)))
        {
            // Pre-feature grants followed ItemConfig._iBagTab=1. The client resolves Soul Echo instances in pack 5.
            if (item.Grid == 0 && (item.Pack != 5 || item.Sort <= 0))
            {
                long nextSort = afterItems.Where(i => i.Grid == 0 && i.Pack == 5).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1;
                item.Pack = 5;
                item.Sort = nextSort;
                changed = true;
            }
            if (item.Attrs == null || !item.Attrs.TryGetValue(Level, out long reached))
            {
                item.Attrs = GenerateAttrs(item.Entry);
                changed = true;
            }
            // Echoes levelled before random marks were rolled hold 0 in their lit slots.
            else if (AddMarks(item.Attrs, item.Entry, (int)reached)) changed = true;
        }
        var active = next.Plans[next.CurrentPlan];
        var seen = new HashSet<long>();
        foreach (var (slot, guid) in active.Slots.OrderBy(p => p.Key).ToList())
        {
            if (slot is < FirstSlot or > LastSlot || !seen.Add(guid)
                || !afterItems.Any(i => i.Guid == guid && i.Grid == 0 && IsEmblem(_data, i.Entry)))
            {
                active.Slots.Remove(slot);
                changed = true;
            }
        }
        foreach (var item in afterItems.Where(i => IsEmblem(_data, i.Entry)))
        {
            int slot = active.Slots.FirstOrDefault(p => p.Value == item.Guid).Key;
            item.Attrs ??= [];
            if (item.Attrs.GetValueOrDefault(Source) == (slot == 0 ? 0 : 2)
                && item.Attrs.GetValueOrDefault(Position) == slot) continue;
            item.Attrs[Source] = slot == 0 ? 0 : 2;
            item.Attrs[Position] = slot;
            changed = true;
        }
        long current = PlanPower(afterItems, active);
        if (current > next.HistorySeasonPower) { next.HistorySeasonPower = current; changed = true; }
        if (changed)
        {
            var builder = _roles.NewTransaction();
            builder.ItemDelta(session.Role.Id, beforeItems, afterItems);
            builder.RoleBlob(session.Role.Id, "emblems", JsonSerializer.SerializeToUtf8Bytes(next));
            builder.WithEconomy(_character.FamilyEconomy(session, "cd13b/emblem-login-reconcile", [], [],
                new { CurrentPlan = next.CurrentPlan, Power = current }));
            await _roles.CommitAsync(builder, "emblem/login-reconcile", _ =>
            {
                PublishItems(session, afterItems);
                session.Profile.Emblems = next;
                session.Profile.EmblemPower = current;
                session.Profile.EmblemPlanSwitchUtc = default;
            });
        }
        else
        {
            session.Profile.Emblems = next;
            session.Profile.EmblemPower = current;
            session.Profile.EmblemPlanSwitchUtc = default;
        }
    }

    /// <summary>5005 only: PowerService publishes the power once every power owner is loaded (RoleSystems).</summary>
    public async Task LoginAsync(GameSession session)
    {
        session.Profile.EmblemPower = PlanPower(session, Active(session)).SeasonPower;
        await SyncAsync(session);
    }

    private static Plan Active(GameSession session) => session.Profile.Emblems.Plans[session.Profile.Emblems.CurrentPlan];

    private static EmblemState CopyState(EmblemState state) =>
        JsonSerializer.Deserialize<EmblemState>(JsonSerializer.SerializeToUtf8Bytes(state))!;

    private static void PublishItems(GameSession session, IReadOnlyList<InvItem> staged)
    {
        var existing = session.Items.ToDictionary(i => i.Guid);
        session.Items.Clear();
        foreach (var item in staged)
        {
            if (!existing.TryGetValue(item.Guid, out var live)) { session.Items.Add(item); continue; }
            live.Stack = item.Stack;
            live.Pack = item.Pack;
            live.Sort = item.Sort;
            live.Grid = item.Grid;
            live.Attrs = item.Attrs;
            session.Items.Add(live);
        }
    }

    /// <summary>The plan's item scores plus its reached Echo Resonance stages' season power (StagePower).</summary>
    private long PlanPower(IReadOnlyList<InvItem> inventory, Plan plan)
    {
        var worn = plan.Slots.Values.Distinct().Select(guid => inventory.FirstOrDefault(i => i.Guid == guid)).OfType<InvItem>().ToList();
        return worn.Sum(Score) + StagePower(worn, _data);
    }

    /// <summary>
    /// The Soul Echo plan power of a role from its committed "emblems" blob and item rows, for roles that are not
    /// online (leaderboards). Slots that do not resolve to an owned instance count 0, as in <see cref="PlanPower"/>.
    /// </summary>
    internal long StoredPower(byte[]? emblems, IReadOnlyList<InvItem> inventory)
    {
        if (emblems == null || JsonSerializer.Deserialize<EmblemState>(emblems) is not { } state
            || !state.Plans.TryGetValue(state.CurrentPlan, out var plan)) return 0;
        return PlanPower(inventory, plan);
    }

    private long StageHistory(EmblemState state, IReadOnlyList<InvItem> inventory)
    {
        long current = PlanPower(inventory, state.Plans[state.CurrentPlan]);
        state.HistorySeasonPower = Math.Max(state.HistorySeasonPower, current);
        return current;
    }

    private async Task CommitEmblemsAsync(GameSession session, string label, EmblemState next,
        IReadOnlyList<InvItem> beforeItems, IReadOnlyList<InvItem> afterItems,
        IReadOnlyList<(int Entry, long Count)> costs, object outcome, Action? applied = null)
    {
        long newPower = StageHistory(next, afterItems);
        var builder = _roles.NewTransaction();
        builder.ItemDelta(session.Role!.Id, beforeItems, afterItems);
        builder.RoleBlob(session.Role.Id, "emblems", JsonSerializer.SerializeToUtf8Bytes(next));
        builder.WithEconomy(_character.FamilyEconomy(session, "cd13b/" + label, costs, [], outcome));
        await _roles.CommitAsync(builder, "emblem/" + label, _ =>
        {
            PublishItems(session, afterItems);
            session.Profile.Emblems = next;
            session.Profile.EmblemPower = newPower;
            applied?.Invoke();
        });
    }

    /// <summary>Check that a Build preset references distinct, owned Soul Echo instances before selection.</summary>
    internal int ValidateGenrePlan(GameSession session, EmblemState state, int index)
    {
        if (!state.Plans.TryGetValue(index, out var plan)) return ErrorCodes.Parameter;
        var distinct = new HashSet<long>();
        foreach (var (slot, guid) in plan.Slots)
        {
            if (slot is < FirstSlot or > LastSlot || !distinct.Add(guid)) return ErrorCodes.Parameter;
            var item = session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0);
            if (item == null || !IsEmblem(_data, item.Entry)) return ErrorCodes.ItemMissing;
            if (!SeasonValid(item.Entry)) return ErrorCodes.FeatureUnavailable;
        }
        state.CurrentPlan = index;
        return 0;
    }

    internal PowerUnit GenrePower(GameSession session, EmblemState state) => state.Plans.TryGetValue(state.CurrentPlan, out var plan)
        ? PlanPower(session, plan) : new PowerUnit();

    internal async Task SyncGenrePlanAsync(GameSession session, long previousPower)
    {
        await SyncAsync(session);
        await PowerAsync(session, previousPower);
    }

    /// <summary>All active, extant GUIDs; the plan is authoritative for stat input.</summary>
    public static IEnumerable<InvItem> Worn(GameSession session)
    {
        if (!session.Profile.Emblems.Plans.TryGetValue(session.Profile.Emblems.CurrentPlan, out var plan)) yield break;
        foreach (var guid in plan.Slots.Values.Distinct())
            if (session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0) is { } item) yield return item;
    }

    /// <summary>
    /// Echo Resonance (残响共鸣): lit marks (itemInfos 1021+, up to the entry's _kInitialMark count) of the worn echoes,
    /// counted per mark id (LC_EmblemHelper.GetAllMarkEquipedNum, LC_ItemProtoParse.lua:1055-1100), reach the
    /// EmblemMarkConfig stage whose _iMarkNum they meet (3/6/10, LC_SoulHelper.GetLevelByMarkNum). Mark id → stage.
    /// </summary>
    internal static Dictionary<long, long> Resonances(GameSession session, GameData data) => Resonances(Worn(session), data);

    internal static Dictionary<long, long> Resonances(IEnumerable<InvItem> worn, GameData data)
    {
        var marks = MarkCounts(worn, data);
        var stages = new Dictionary<long, long>();
        foreach (var row in EmblemMarkConfig.All(data))
        {
            long mark = row.EmblemMarkId, stage = row.MarkStage;
            if (stage > 0 && marks.GetValueOrDefault(mark) >= row.MarkNum && stage > stages.GetValueOrDefault(mark))
                stages[mark] = stage;
        }
        return stages;
    }

    /// <summary>Lit marks of one id on the active plan's echoes (LC_SpecialEffectHelper._getEmblemMarkCount).</summary>
    internal static int MarkCount(GameSession session, long mark, GameData data) =>
        MarkCounts(Worn(session), data).GetValueOrDefault(mark);

    /// <summary>Lit marks per mark id over the given echoes (LC_EmblemHelper.GetAllMarkEquipedNum).</summary>
    private static Dictionary<long, int> MarkCounts(IEnumerable<InvItem> worn, GameData data)
    {
        var marks = new Dictionary<long, int>();
        foreach (var item in worn)
        {
            int lit = EmblemConfig.Get(data, item.Entry) is { } row ? row.InitialMark.Length : 0;
            for (int i = 0; i < lit && item.Attrs?.TryGetValue(1021 + i, out long mark) == true; i++)
                marks[mark] = marks.GetValueOrDefault(mark) + 1;
        }
        return marks;
    }

    public static bool IsReserved(GameSession session, InvItem item) =>
        item.Attrs?.GetValueOrDefault(Source) == 2 || session.Profile.Emblems.Plans.Values.Any(p => p.Slots.ContainsValue(item.Guid));

    public static bool IsEmblem(GameData data, int entry) =>
        ItemConfig.Get(data, entry) is { Type: 8, SubType: 800 };

    /// <summary>Client item parser's paired contiguous primary/subattribute ranges (LC_ItemProtoParse.lua:957-1054).</summary>
    public static IEnumerable<(int Id, double Value)> Attributes(InvItem item, GameData data)
    {
        var a = item.Attrs;
        if (a == null) yield break;
        for (int i = 0; i < 10 && a.TryGetValue(1031 + i, out long id) && a.TryGetValue(1041 + i, out long value); i++)
            yield return ((int)id, value);
        // Subattribute keys hold config row IDs, not AttributeConfig IDs.
        for (int i = 0; i < 20 && a.TryGetValue(1051 + i, out long rowId) && a.TryGetValue(1071 + i, out long value); i++)
            if (EmblemSubAttriGroupConfig.Get(data, rowId) is { SubAttriId: > 0 and var id })
                yield return ((int)id, value);
    }

    private DBEmblemInfo Build(GameSession session)
    {
        var info = new DBEmblemInfo { CurrentPlan = session.Profile.Emblems.CurrentPlan, Inited = true };
        foreach (var (index, saved) in session.Profile.Emblems.Plans)
        {
            var plan = new DBEmblemPlan { PlanName = saved.Name, Power = PlanPower(session, saved) };
            foreach (var (slot, guid) in saved.Slots) plan.SetUpEmblem[slot] = guid;
            info.EmblePlans[index] = plan;
        }
        return info;
    }

    private Task SyncAsync(GameSession session) => CharacterService.SyncAsync(session, PlanSync, Build(session));

    private PowerUnit PlanPower(GameSession session, Plan plan) => new() { SeasonPower = PlanPower(session.Items, plan) };

    private long Score(InvItem item)
    {
        long score = EmblemConfig.Get(_data, item.Entry)?.SeasonPower ?? 0;
        int quality = Quality(item.Entry), level = (int)(item.Attrs?.GetValueOrDefault(Level) ?? 0);
        score += LevelRow(quality, level)?.SeasonPower ?? 0;
        for (int i = 0; i < 20 && item.Attrs?.TryGetValue(1051 + i, out long rowId) == true
            && item.Attrs.TryGetValue(1071 + i, out _); i++)
            if (EmblemSubAttriGroupConfig.Get(_data, rowId) is { } sub) score += sub.SeasonPower;
        for (int i = 0; i < 10 && item.Attrs?.TryGetValue(1091 + i, out long specialId) == true; i++)
            if (SpecialEffectConfig.Get(_data, specialId) is { } special) score += special.Power;
        // Item score sums the configured base, level, subrow, special power (LC_EmblemHelper.GetEmblemScore).
        // The plan adds its reached mark stages on top (PlanPower, StagePower). No _kSpecialAttribute generation
        // pool exists here; preexisting specials can score but their combat evaluator is outside this slice.
        return score;
    }

    /// <summary>The Soul Echo part changed: PowerService republishes the whole package (31502, 31503, 5066).</summary>
    private async Task PowerAsync(GameSession session, long previous)
    {
        session.Profile.EmblemPower = PlanPower(session, Active(session)).SeasonPower;
        if (PublishPower != null) await PublishPower(session);
    }

    /// <summary>One accepted change: 5009, 5005, final properties/AOI, power, then 5066 (packet order inferred).</summary>
    private async Task ChangedAsync(GameSession session, long previous, bool inventory)
    {
        if (inventory) await _character.ItemsChangedAsync(session, stats: false);
        await SyncAsync(session);
        await _character.RefreshStatsAsync(session);
        await PowerAsync(session, previous);
    }

    private int Gate(GameSession session) => session.Tasks != null && _data.Row("FunctionConfig", 55) != null
        && TaskService.IsDelivered(session, 10103040) && _functions.IsOpen(session, Function) ? 0 : ErrorCodes.FunctionLocked;

    /// <summary>82422: validate slot, owned generated item, current season, and active-plan uniqueness first.</summary>
    public async Task SetUpAsync(GameSession session, SetUpEmblemReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var plan = Active(session);
        var item = session.Items.FirstOrDefault(i => i.Guid == req.EmblemGuid && i.Grid == 0);
        int code = Gate(session);
        if (code == 0) code = req.EmblemPos is < FirstSlot or > LastSlot ? ErrorCodes.EmblemSlot
            : item == null || !IsEmblem(_data, item.Entry) ? ErrorCodes.EmblemInvalid
            : !SeasonValid(item.Entry) ? ErrorCodes.EmblemOtherSeason
            : plan.Slots.ContainsValue(req.EmblemGuid) ? ErrorCodes.EmblemAlreadyDeployed : 0;
        long oldGuid = code == 0 ? plan.Slots.GetValueOrDefault(req.EmblemPos) : 0;
        if (code == 0)
        {
            long previous = session.Profile.EmblemPower;
            var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
            var afterItems = beforeItems.Select(i => i.Clone()).ToList();
            var next = CopyState(session.Profile.Emblems);
            if (oldGuid != 0 && afterItems.FirstOrDefault(i => i.Guid == oldGuid) is { } old)
            {
                old.Attrs ??= []; old.Attrs[Source] = 0; old.Attrs[Position] = 0;
            }
            var deployed = afterItems.Single(i => i.Guid == req.EmblemGuid);
            deployed.Attrs ??= GenerateAttrs(deployed.Entry) ?? [];
            deployed.Attrs[Source] = 2; deployed.Attrs[Position] = req.EmblemPos;
            next.Plans[next.CurrentPlan].Slots[req.EmblemPos] = deployed.Guid;
            await CommitEmblemsAsync(session, "emblem-setup", next, beforeItems, afterItems, [],
                new { req.EmblemPos, req.EmblemGuid, OldEmblemGuid = oldGuid });
            await ChangedAsync(session, previous, inventory: true);
        }
        await session.SendGameAsync(SetUp, new SetUpEmblemResp { Result = code, EmblemPos = req.EmblemPos, EmblemGuid = req.EmblemGuid, OldEmblemGuid = oldGuid });
        _log.LogInformation("{Name} deploys Soul Echo {Guid} in {Slot}: {Code}", session.Role!.Name, req.EmblemGuid, req.EmblemPos, code);
    }

    /// <summary>82423: remove the selected slot and clear the item's matching setup attributes.</summary>
    public async Task SetDownAsync(GameSession session, SetDownEmblemReq req)
    {
        using var _ = await CharacterService.Lock(session);
        var plan = Active(session);
        int code = Gate(session);
        if (code == 0) code = req.EmblemPos is < FirstSlot or > LastSlot ? ErrorCodes.EmblemSlot
            : !plan.Slots.ContainsKey(req.EmblemPos) ? ErrorCodes.EmblemSlotEmpty : 0;
        long guid = code == 0 ? plan.Slots[req.EmblemPos] : 0;
        if (code == 0)
        {
            long previous = session.Profile.EmblemPower;
            var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
            var afterItems = beforeItems.Select(i => i.Clone()).ToList();
            var next = CopyState(session.Profile.Emblems);
            next.Plans[next.CurrentPlan].Slots.Remove(req.EmblemPos);
            if (afterItems.FirstOrDefault(i => i.Guid == guid) is { } item)
            {
                item.Attrs ??= []; item.Attrs[Source] = 0; item.Attrs[Position] = 0;
            }
            await CommitEmblemsAsync(session, "emblem-setdown", next, beforeItems, afterItems, [],
                new { req.EmblemPos, EmblemGuid = guid });
            await ChangedAsync(session, previous, inventory: true);
        }
        await session.SendGameAsync(SetDown, new SetDownEmblemResp { Result = code, EmblemPos = req.EmblemPos, EmblemGuid = guid });
    }

    /// <summary>82424: validate every selection, then serialize payment and update under StateLock (inferred rounding).</summary>
    public async Task UpgradeAsync(GameSession session, UpgradeEmblemReq req)
    {
        using var _ = await CharacterService.Lock(session);
        int code = Gate(session);
        var target = session.Items.FirstOrDefault(i => i.Guid == req.EmblemGuid && i.Grid == 0);
        if (code == 0) code = target == null || !IsEmblem(_data, target.Entry) ? ErrorCodes.EmblemInvalid
            : !SeasonValid(target.Entry) ? ErrorCodes.EmblemOtherSeason : 0;
        int quality = target == null ? 0 : Quality(target.Entry);
        int level = (int)(target?.Attrs?.GetValueOrDefault(Level) ?? 0);
        int cap = EmblemLvConfig.All(_data).Where(r => r.Quality == quality).Select(r => (int)r.Lv).DefaultIfEmpty(0).Max();
        if (code == 0 && (cap == 0 || level >= cap)) code = ErrorCodes.EmblemCannotLevel;
        var ids = req.ConsumeEmblemGuids.ToList();
        if (code == 0 && (ids.Count != ids.Distinct().Count() || ids.Contains(req.EmblemGuid))) code = ErrorCodes.EmblemInvalid;
        var feed = new List<InvItem>();
        if (code == 0)
            foreach (var guid in ids)
            {
                var item = session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0);
                if (item == null || !IsEmblem(_data, item.Entry) || IsReserved(session, item) || !SeasonValid(item.Entry))
                { code = ErrorCodes.EmblemInvalid; break; }
                feed.Add(item);
            }
        long xp = 0;
        if (code == 0)
        {
            try
            {
                foreach (var (entry, count) in req.ConsumeItems)
                {
                    if (!MaterialXp.TryGetValue(entry, out long value) || count <= 0) { code = ErrorCodes.Parameter; break; }
                    xp = checked(xp + value * count);
                }
            }
            catch (OverflowException) { code = ErrorCodes.Parameter; }
            if (code == 0 && req.ConsumeItems.Any(p => _character.CountOf(session, p.Key) < p.Value)) code = ErrorCodes.ItemsShort;
        }
        if (code == 0)
        {
            try
            {
                foreach (var item in feed)
                {
                    int q = Quality(item.Entry), lv = (int)(item.Attrs?.GetValueOrDefault(Level) ?? 0);
                    long baseXp = q switch { 2 or 3 => 100, 4 => 300, 5 or 6 => 1000, _ => 0 };
                    long rate = lv <= 5 ? 10000 : lv <= 10 ? 9000 : lv <= 15 ? 8000 : 7000;
                    // Client has no rounding step; integral feed XP floors each consumed item (inferred).
                    xp = checked(xp + baseXp + checked((item.Attrs?.GetValueOrDefault(TotalExp) ?? 0) * rate) / 10000);
                }
            }
            catch (OverflowException) { code = ErrorCodes.Parameter; }
            if (xp <= 0) code = ErrorCodes.Parameter;
        }
        Dictionary<int, long>? upgradedAttrs = null;
        if (code == 0)
        {
            try
            {
                // Compute the whole new item state before payment; no roll/overflow can fail after item removal.
                upgradedAttrs = target!.Attrs == null ? GenerateAttrs(target.Entry) ?? [] : new(target.Attrs);
                long remaining = checked(upgradedAttrs.GetValueOrDefault(Exp) + xp);
                int next = level;
                while (next < cap && LevelRow(quality, next + 1) is { } row && remaining >= row.Exp)
                {
                    remaining -= row.Exp;
                    next++;
                    AddPrimaryLevel(upgradedAttrs, target.Entry);
                }
                upgradedAttrs[Level] = next;
                upgradedAttrs[Exp] = remaining; // retain cap remainder as SimulateAddExp preview (inferred server policy)
                upgradedAttrs[TotalExp] = checked(upgradedAttrs.GetValueOrDefault(TotalExp) + xp); // full feed XP, no refund (inferred)
                AddMarks(upgradedAttrs, target.Entry, next);
                AddSubattrs(upgradedAttrs, target.Entry, next);
            }
            catch (OverflowException) { code = ErrorCodes.Parameter; }
        }
        if (code == 0)
        {
            var beforeItems = session.Items.Select(i => i.Clone()).ToArray();
            var afterItems = beforeItems.Select(i => i.Clone()).ToList();
            foreach (var consumed in feed)
            {
                var copy = afterItems.Single(i => i.Guid == consumed.Guid);
                copy.Stack--;
                if (copy.Stack == 0) afterItems.Remove(copy);
            }
            foreach (var (entry, count) in req.ConsumeItems)
            {
                long left = count;
                foreach (var material in afterItems.Where(i => i.Grid == 0 && i.Entry == entry
                    && session.Items.FirstOrDefault(source => source.Guid == i.Guid) is { } source
                    && !IsReserved(session, source) && !TotemService.IsReserved(session, source))
                    .OrderBy(i => i.Stack).ToList())
                {
                    if (left == 0) break;
                    int take = (int)Math.Min(left, material.Stack);
                    material.Stack -= take;
                    left -= take;
                    if (material.Stack == 0) afterItems.Remove(material);
                }
                if (left != 0) { code = ErrorCodes.ItemsShort; break; }
            }
            if (code == 0)
            {
                afterItems.Single(i => i.Guid == req.EmblemGuid).Attrs = upgradedAttrs!;
                var costs = req.ConsumeItems.Select(p => (Entry: p.Key, Count: (long)p.Value))
                    .Concat(feed.Select(i => (Entry: i.Entry, Count: 1L)))
                    .GroupBy(p => p.Entry).Select(g => (Entry: g.Key, Count: g.Sum(p => p.Count))).ToArray();
                var next = CopyState(session.Profile.Emblems);
                long previous = session.Profile.EmblemPower;
                await CommitEmblemsAsync(session, "emblem-upgrade", next, beforeItems, afterItems, costs,
                    new
                    {
                        req.EmblemGuid, Materials = req.ConsumeItems.OrderBy(p => p.Key).ToArray(),
                        Feeds = feed.Select(i => new { i.Guid, i.Entry }).OrderBy(i => i.Guid).ToArray(),
                        AddedExperience = xp, Attributes = upgradedAttrs
                    });
                await ChangedAsync(session, previous, inventory: true);
                int gained = (int)upgradedAttrs!.GetValueOrDefault(Level) - level;
                foreach (var handler in Upgraded.Handlers)
                    try { handler(session, gained); }
                    catch (Exception e) { _log.LogError(e, "Soul Echo upgrade hook failed for {Role}", session.Role?.Id); }
            }
        }
        // No excess-XP refund, so gainItems is empty (inferred).
        await session.SendGameAsync(Upgrade, new UpgradeEmblemResp { Result = code, EmblemGuid = req.EmblemGuid });
    }

    private int Quality(int entry) => (int)(ItemConfig.Get(_data, entry)?.Quality ?? 0);

    private EmblemLvConfig? LevelRow(int quality, int level) =>
        EmblemLvConfig.All(_data).Where(r => r.Quality == quality && r.Lv == level).Cast<EmblemLvConfig?>().FirstOrDefault();

    private bool SeasonValid(int entry) => ItemConfig.Get(_data, entry) is not { } row || SeasonValuesValid(row.MultiverseArray);

    /// <summary>Initial rolls use configured inclusive ranges; weighted unique-attribute subrow selection is inferred.</summary>
    private Dictionary<int, long>? GenerateAttrs(int entry)
    {
        if (!IsEmblem(_data, entry) || EmblemConfig.Get(_data, entry) is not { } row) return null;
        var attrs = new Dictionary<int, long> { [Level] = 0, [Exp] = 0, [TotalExp] = 0 };
        int slot = 0;
        foreach (var pair in row.PrimaryAttribute)
        {
            if (slot >= 10 || pair.Length < 3) break;
            attrs[1031 + slot] = pair[0];
            attrs[1041 + slot] = Roll(pair);
            slot++;
        }
        AddMarks(attrs, entry, 0);
        AddSubattrs(attrs, entry, 0);
        return attrs;
    }

    private static long Roll(long[] pair)
    {
        long min = pair[1], max = pair[2];
        return max <= min ? min : Random.Shared.NextInt64(min, max + 1);
    }

    /// <summary>Roll every _kPrimaryAttributeLvUp range once per reached level (inferred frequency).</summary>
    private void AddPrimaryLevel(Dictionary<int, long> attrs, int entry)
    {
        if (EmblemConfig.Get(_data, entry) is not { } row) return;
        int slot = 0;
        foreach (var pair in row.PrimaryAttributeLvUp)
        {
            if (slot >= 10 || pair.Length < 3) break;
            if (attrs.GetValueOrDefault(1031 + slot) == pair[0]) attrs[1041 + slot] = attrs.GetValueOrDefault(1041 + slot) + Roll(pair);
            slot++;
        }
    }

    /// <summary>
    /// Lights the _kInitialMark slots the level reached; true when that changed the item. A slot's mark 0 is a random
    /// element, rolled once: UI_Sys_SoulUpgradePanelCtrl (:1147-1149, PlayChaosMarkUpgradeSound :1202-1277) animates
    /// such a slot as random and then reads its real mark off the upgraded item, and the help text says each lit element is random.
    /// </summary>
    private bool AddMarks(Dictionary<int, long> attrs, int entry, int level)
    {
        if (EmblemConfig.Get(_data, entry) is not { } row) return false;
        bool changed = false;
        int slot = 0;
        foreach (var pair in row.InitialMark)
        {
            if (slot >= 10 || pair.Length < 2) break;
            if (pair[0] > level) break;
            long mark = pair[1];
            if (mark == 0) mark = attrs.GetValueOrDefault(1021 + slot) is var kept and > 0 ? kept : RollMark();
            if (!attrs.TryGetValue(1021 + slot, out long held) || held != mark)
            {
                attrs[1021 + slot] = mark;
                changed = true;
            }
            slot++;
        }
        return changed;
    }

    /// <summary>Uniform over the elements EmblemMarkConfig has stages for (inferred: the client holds no weights).</summary>
    private long RollMark()
    {
        var pool = EmblemMarkConfig.All(_data).Where(r => r.MarkStage > 0 && SeasonRowsValid(r.MultiverseArray))
            .Select(r => r.EmblemMarkId).Distinct().ToArray();
        return pool.Length == 0 ? 0 : pool[Random.Shared.Next(pool.Length)];
    }

    /// <summary>Positive-weight rows selected without replacement by actual attribute ID (inferred selection).</summary>
    private void AddSubattrs(Dictionary<int, long> attrs, int entry, int level)
    {
        if (EmblemConfig.Get(_data, entry) is not { } config || config.SubAttributeNum.Length == 0) return;
        int wanted = 0;
        foreach (var pair in config.SubAttributeNum)
            if (pair.Length >= 2 && pair[0] <= level) wanted += (int)pair[1];
        long group = config.SubAttributeGroup;
        if (group == 0) return; // config has no pool/slot directive; do not fill from another group.
        int existing = Enumerable.Range(0, 20).TakeWhile(i => attrs.ContainsKey(1051 + i) && attrs.ContainsKey(1071 + i)).Count();
        var usedIds = Enumerable.Range(0, existing).Select(i => EmblemSubAttriGroupConfig.Get(_data, attrs[1051 + i]))
            .Where(r => r != null).Select(r => r!.Value.SubAttriId).ToHashSet();
        while (existing < Math.Min(20, wanted))
        {
            var pool = EmblemSubAttriGroupConfig.All(_data)
                .Where(r => r.Group == group && r.Weight > 0 && !usedIds.Contains(r.SubAttriId) && SeasonRowsValid(r.MultiverseArray))
                .ToList();
            long total = pool.Sum(r => r.Weight);
            if (total <= 0) break;
            long roll = Random.Shared.NextInt64(total);
            var picked = pool[^1];
            foreach (var candidate in pool)
            {
                roll -= candidate.Weight;
                if (roll < 0) { picked = candidate; break; }
            }
            long rowId = picked.Id, id = picked.SubAttriId;
            attrs[1051 + existing] = rowId;
            long min = picked.Min, max = picked.Max;
            attrs[1071 + existing] = max <= min ? min : Random.Shared.NextInt64(min, max + 1);
            usedIds.Add(id);
            existing++;
        }
    }

    private bool SeasonValuesValid(long[] values) =>
        values.Length == 0 || values.Contains(0) || values.Length == 2 && values[1] == -1 && _seasons.PlayBook >= values[0] || values.Contains(_seasons.PlayBook);

    private bool SeasonRowsValid(JsonElement? multiverse) =>
        multiverse is not { ValueKind: JsonValueKind.Array } list
        || SeasonValuesValid([.. list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0))]);
}
