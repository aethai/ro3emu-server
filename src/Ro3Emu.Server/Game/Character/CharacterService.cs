using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Character progression, bag and equipment: the AUTO_SYNC datasets 5066 (level/exp), 5067
/// (stat points), 5009 (bag, currencies), 5007 (equipment), the property lists the character window shows, and the
/// requests that change them. Numbers come from the client's own tables (AttriBaseLvConfig, AttributeConversionConfig,
/// BaseLvConfig, JobLvConfig, BasePointConfig, EquipConfig).
/// </summary>
public sealed partial class CharacterService(RoleStore roles, ItemStore itemStore, GameData data, ILogger<CharacterService> log)
{
    public const uint RoleBaseInfoSync = 5066;
    public const uint BaseAttrSync = 5067;
    public const uint ItemsSync = 5009;
    public const uint EquipSync = 5007;
    public const uint OnePropertyChange = 31008;
    public const uint AoiAttrChange = 31048;
    public const uint EntityBaseChange = 31049;
    public const uint GetRewardNotify = 31322;
    public const uint ItemUse = 31323;
    public const uint PackItems = 31301;
    public const uint EquipItemOpr = 31400;
    public const uint AllocateAttrPoints = 31500;
    public const uint ResetAttrPoints = 31501;

    /// <summary>Free stat points of a new character (RO: 48), before BaseLvConfig._iStatusPoint per level.</summary>
    public const int StartingStatPoints = 48;
    public const int MaxStat = 99;

    /// <summary>Stat-point and equipment plans are 0-based (the equipment window labels plan key k "Build k+1").</summary>
    private const int StatPlan = 0;

    internal const int ItemTypeCurrency = 1;
    internal const int ItemTypeEquip = 5;
    internal const int ZenyId = ItemId.Zeny;
    internal const int BaseExpItem = ItemId.BaseExp;
    internal const int JobExpItem = ItemId.JobExp;

    /// <summary>ItemAttrTypeEnum keys: 3 = appraised (equipment can be worn only when 1), 10001 = set-up source.</summary>
    internal const int AttrAppraise = 3;
    internal const int AttrSetUpSource = 10001;

    /// <summary>Assigned by EmblemService: per-instance generated itemInfos for ItemConfig type 8/subtype 800.</summary>
    public Func<int, Dictionary<int, long>?>? EmblemAttrFactory { get; set; }

    /// <summary>Totem instance attributes, generated once for each acquired GUID (MG_Pack itemInfos 220..270).</summary>
    public Func<int, Dictionary<int, long>?>? TotemAttrFactory { get; set; }

    /// <summary>Transactional collection consumables. Called with StateLock held after common item validation.</summary>
    public OrderedHook<Func<GameSession, ItemUseReq, InvItem, JsonElement, int, Task<int?>>> SpecialItemUse { get; } = new("CharacterService.SpecialItemUse");

    /// <summary>EntityBase.equipInfo holds the item ids of equipment grids 1..7 (MainHand, ViceHand, Armor, ...).</summary>
    internal const int EquipInfoSlots = 7;

    /// <summary>Property ids (AttributeConfig): base stats 1..6, derived 101..116, current HP/SP and level.</summary>
    internal const int MaxHp = 101, MaxSp = 102, Atk = 103, Matk = 104, Speed = 113, BaseAspd = 115, Hp = 2000, Sp = 2001, Lv = 2002;

    public const uint EquipSwitchPlan = CharacterInventoryService.EquipSwitchPlan;
    public const uint EquipChangePlanName = CharacterInventoryService.EquipChangePlanName;
    public const int EquipmentPlanCount = CharacterInventoryService.EquipmentPlanCount;
    internal const int ErrUseTimes = CharacterInventoryService.ErrUseTimes;
    internal const int ErrLevelNeed = CharacterInventoryService.ErrLevelNeed;
    internal const int ErrJobNeed = CharacterInventoryService.ErrJobNeed;
    internal const int ErrSexNeed = CharacterInventoryService.ErrSexNeed;

    public sealed record OwnedAwardSource(string CommandKind, string SourceKey, object Outcome);
    internal readonly record struct AttributedGood(int Entry, int Count, int BindType, long BindExpire, int CardType);

    private CharacterInventoryService _inventory = null!;
    private CharacterStatsService _stats = null!;
    internal CharacterInventoryService Inventory => _inventory;
    internal void BindSubsystems(CharacterInventoryService inventory, CharacterStatsService stats) { _inventory = inventory; _stats = stats; }

    internal Func<long>? WorkSerial { get; set; }
    internal Func<long>? TickSerial { get; set; }
    public Func<GameSession, long>? TitleOf { get; set; }
    public OrderedHook<Func<GameSession, IEnumerable<(long Id, double Value)>>> ExtraAttributes { get; } = new("CharacterService.ExtraAttributes");
    /// <summary>What the Guild War Technology attributes depend on (GuildTechService.MemoKey): the playing method they count
    /// for and the guild's level map, compared by reference. Part of the stats memo's key.</summary>
    public Func<GameSession, (long Method, object? Levels)>? GuildTechKey { get; set; }
    public Func<GameSession, IReadOnlyList<(long Id, double Value)>?>? MatchAttributes { get; set; }
    public OrderedHook<Func<GameSession, IEnumerable<(int Id, long Value)>>> DynamicProperties { get; } = new("CharacterService.DynamicProperties");
    public Func<GameSession, int, int, Task>? GiveBuff { get; set; }
    public AsyncHook<GameSession> WornGearChanged { get; } = new("CharacterService.WornGearChanged");
    public Func<GameSession, bool>? IsItemUseForbidden { get; set; }
    public Func<GameSession, long, Task<bool>>? WingJump { get; set; }
    internal Func<GameSession, InvItem, IReadOnlyList<(int Entry, long Count)>>? AutoDecomposeReturns { get; set; }
    internal Func<GameSession, ItemUseReq, Action<RoleItemPlanBuilder>, Action, Task<int>>? StartTreasureMap { get; set; }
    internal Func<GameSession, MonsterUnit, Ownership.ReservationKey[]>? GetKillTaskParticipantKeys { get; set; }
    internal Action<GameSession, MonsterUnit, RewardSettlement>? StageKillTaskObligations { get; set; }
    internal TimeProvider UseClock { get; set; } = TimeProvider.System;

    /// <summary>What a new character finds in the bag: Beginner Potions, a Novice Dagger, a Cotton Shirt, some Zeny.</summary>
    internal static readonly (int Entry, int Count)[] StarterKit = [(1000000, 20), (2010121, 1), (2550120, 1), (ZenyId, 1000)];

    /// <summary>The ledger receipt of a starter kit (CharacterFamilyEconomy: its goods are StarterGrant sources). A
    /// character with the kit and no such receipt got it before the kit was recorded (PanelBackend's duplication check).</summary>
    internal const string StarterKitEconomyKind = "cd13a/character-starter-kit";

    // ---- helpers ----

    internal void Save(GameSession session) => roles.SaveProgress(session.Role!.Id, session.Progress!);

    /// <summary>An item entered the bag: entry and the count gained (card collection first sight, achievement counters).</summary>
    public SyncHook<GameSession, int, int> ItemGained { get; } = new("CharacterService.ItemGained");

    /// <summary>The items of a committed award (quest reward, drop, gift). A GM grant or a shop exchange does not pass here.</summary>
    public SyncHook<GameSession, IReadOnlyList<(int Entry, int Count)>> ItemsAwarded { get; } = new("CharacterService.ItemsAwarded");
    internal sealed record FirstCardSightPlan(Action<RoleItemPlanBuilder> Persist, Action Apply);
    /// <summary>CardService enlists first sight in owned award/exchange commits and, for a kill, the slain monster's Codex kill (NPC id, 0 none).</summary>
    internal Func<GameSession, IReadOnlyList<int>, int, FirstCardSightPlan?>? StageFirstCardSight { get; set; }

    /// <summary>After a successful 31323 consume and its effects; quest engine queues objective evaluation.</summary>
    public SyncHook<GameSession, int, int> ItemUsed { get; } = new("CharacterService.ItemUsed");

    /// <summary>LifeJobService owns action 607/315 item transactions; null means ordinary item effects.</summary>
    internal Func<GameSession, InvItem, JsonElement, int, Task<int?>>? LifeItemUse { get; set; }

    internal static readonly AsyncLocal<LifeItemExchangeScope?> LifeItemExchange = new();

    internal sealed class LifeItemExchangeScope(GameSession session, Action<RoleItemPlanBuilder> persist, Action publish)
    {
        public GameSession Session { get; } = session;
        public Action<RoleItemPlanBuilder> Persist { get; } = persist;
        public Action Publish { get; } = publish;
        public bool Active { get; set; } = true;
        public bool Committed { get; set; }
        public Action<GameSession>[]? PendingStateChanged { get; set; }
    }

    /// <summary>After a committed inventory, equipment or stat change; listeners defer any grant work. A monster kill's
    /// reward raises it only when it touched what a listener reads (CharacterInventoryService.QuietKillReward).</summary>
    public SyncHook<GameSession> StateChanged { get; } = new("CharacterService.StateChanged");

    /// <summary>Only the Astral Level (ParagonLevel/ParagonExp) moved: EXP past the level cap from a kill or a party share.
    /// <see cref="StateChanged"/> would wake every listener for it; only ParagonService reads it.</summary>
    public SyncHook<GameSession> AstralChanged { get; } = new("CharacterService.AstralChanged");

    private readonly List<Func<IEnumerable<long>>> _stateEntrySources = [];
    private readonly Lock _stateEntriesLock = new();
    private volatile HashSet<int>? _stateEntries;

    /// <summary>
    /// Items and currencies a <see cref="StateChanged"/> listener reads beyond levels and equipment: quest collect and
    /// submit targets, condition item counts, daily activity points, pass EXP and tokens, vigor, guild currencies. Each
    /// listener names its own when it subscribes (read from the tables on first use); a kill reward that grants one of
    /// them raises the event.
    /// </summary>
    internal void WatchStateEntries(Func<IEnumerable<long>> entries)
    {
        lock (_stateEntriesLock)
        {
            _stateEntrySources.Add(entries);
            _stateEntries = null; // a listener that subscribes late (GuildWartimeService at the first login) is counted too
        }
    }

    internal bool WatchesStateEntry(int entry)
    {
        var entries = _stateEntries;
        if (entries == null)
            lock (_stateEntriesLock)
                entries = _stateEntries ??= [.. _stateEntrySources.SelectMany(source => source())
                    .Where(e => e is > 0 and <= int.MaxValue).Select(e => (int)e)];
        return entries.Contains(entry);
    }

    /// <summary>For CardService: item, stat and look updates after socket changes (caller holds the lock).</summary>
    internal async Task ItemsChangedAsync(GameSession session, bool stats)
    {
        var lifeUse = LifeItemExchange.Value;
        if (lifeUse is { Active: true, Committed: true, PendingStateChanged: { } pending }
            && ReferenceEquals(lifeUse.Session, session))
        {
            lifeUse.PendingStateChanged = null;
            foreach (var handler in pending) handler(session);
        }
        await SyncItemsAsync(session);
        if (stats) await SendStatsAsync(session);
        foreach (var handler in StateChanged.Handlers) handler(session);
    }

    internal ItemBaseInfo ItemInfoOf(InvItem item) => _inventory.ItemInfo(item);

    /// <summary>5007 after an equipped instance changes; caller holds StateLock.</summary>
    internal Task EquipmentChangedAsync(GameSession session) => SyncAsync(session, EquipSync, BuildEquip(session));

    internal static long NextBagSort(GameSession session, int pack) => CharacterInventoryService.NextSort(session, pack);

    internal static Task<IDisposable> Lock(GameSession session) => LockAsync(session);
    internal static Task<IDisposable> Lock(GameSession session, IReadOnlyList<Ownership.ReservationKey> additionalKeys)
        => LockAsync(session, additionalKeys: additionalKeys);
    internal static Task<IDisposable> Lock(GameSession session, Ownership.OwnerContext context)
    { context.AssertRole(session.Role!.Id); return LockAsync(session); }

    internal static Ownership.OwnerContext RequireOwnedRole(GameSession session)
    {
        var context = session.Owners?.Current ?? throw new InvalidOperationException("Character Core requires an active owner capability.");
        context.AssertRole(session.Role!.Id);
        return context;
    }

    /// <summary>Installed by SeasonTalentService without a CharacterService constructor cycle.</summary>
    internal Func<GameSession, IEnumerable<(long Id, double Value)>>? TalentAttributes { get; set; }

    /// <summary>Runs after level / job level / job changes (SkillService resends skill points and the normal attack).</summary>
    public AsyncHook<GameSession> ProgressHandlers { get; } = new("CharacterService.ProgressHandlers");

    // ---- subsystem facades (CharacterStatsService & CharacterInventoryService) ----

    public void Refresh(GameSession session) => _stats.Refresh(session);
    internal RoleDetailInfo DetailInfo(GameSession session) => _stats.DetailInfo(session);
    internal void ReuseStatsInThisWork(GameSession session) => _stats.ReuseStatsInThisWork(session);
    public StatSheet Stats(GameSession session) => _stats.Stats(session);
    public Task RefreshStatsAsync(GameSession session) => _stats.RefreshStatsAsync(session);
    public Task SetHpSpAsync(GameSession session, long? hp, long? sp) => _stats.SetHpSpAsync(session, hp, sp);
    internal static void AddGroupedConversion(Dictionary<int, double> raw, int grouped, Data.Config.AttributeConversionConfig conversion) =>
        CharacterStatsService.AddGroupedConversion(raw, grouped, conversion);
    internal StatSheet ApplySpeedOverflow(Dictionary<int, double> raw, StatSheet sheet) => _stats.ApplySpeedOverflow(raw, sheet);
    internal Task SendStatsAsync(GameSession session, bool showChange = false) => _stats.SendStatsAsync(session, showChange);
    internal Task ToSelfAndViewersAsync(GameSession session, uint msgId, IMessage body) => _stats.ToSelfAndViewersAsync(session, msgId, body);

    public Task<string> ClearBagAsync(GameSession session) => _inventory.ClearBagAsync(session);
    public Task UseItemAsync(GameSession session, ItemUseReq req) => _inventory.UseItemAsync(session, req);
    internal Task<int> AutoUseItemAsync(GameSession session, long guid) => _inventory.AutoUseItemAsync(session, guid);
    public Task SortAsync(GameSession session, ArrangeItemReq req) => _inventory.SortAsync(session, req);
    internal static int ConfigPack(GameData data, JsonElement row) => CharacterInventoryService.ConfigPack(data, row);
    internal int PlainGrantError(GameSession session, IReadOnlyList<(int Entry, int Count)> goods,
        IReadOnlyList<InvItem>? exactGoods = null, IReadOnlyDictionary<int, long>? currencyGoods = null,
        IReadOnlyList<(int Entry, int Count, int Bind)>? boundGoods = null) =>
        _inventory.PlainGrantError(session, goods, exactGoods, currencyGoods, boundGoods);
    internal ItemBaseInfo? EquipmentInfo(InvItem item) => _inventory.EquipmentInfo(item);
    internal static void ApplyStagedItems(GameSession session, List<InvItem> staged, long[] ids) =>
        CharacterInventoryService.ApplyStagedItems(session, staged, ids);
    internal EconomyMutation EconomyForOwnedExchange(GameSession session, string kind, string sourceKey,
        IReadOnlyList<(int Entry, long Count)> costs, IReadOnlyList<(int Entry, int Count)> goods, object outcome) =>
        _inventory.EconomyForOwnedExchange(session, kind, sourceKey, costs, goods, outcome);
    internal Func<Task> CaptureEconomicItemsNotice(GameSession session, bool publishStateChanged = true) =>
        _inventory.CaptureEconomicItemsNotice(session, publishStateChanged);
    internal static Func<Task> CaptureEconomicSync(GameSession session, uint id, IMessage snapshot) =>
        CharacterInventoryService.CaptureEconomicSync(session, id, snapshot);
    internal JsonElement? StrengthenRow(int grid, int level) => _inventory.StrengthenRow(grid, level);
    internal JsonElement? RefineRow(int grid, int level) => _inventory.RefineRow(grid, level);
    internal int Resonance(GameSession session, string table) => _inventory.Resonance(session, table);
    internal void SaveEquipLevels(GameSession session) => _inventory.SaveEquipLevels(session);
    internal Task EquipLevelsChangedAsync(GameSession session, bool persist = true) => _inventory.EquipLevelsChangedAsync(session, persist);
    public bool IsCard(int entry) => _inventory.IsCard(entry);
    public static (int Grid, int Socket) CardSocket(InvItem card) => CharacterInventoryService.CardSocket(card);
    public static int CardGrid(int grid, int socket) => CharacterInventoryService.CardGrid(grid, socket);
    internal static bool IsAppraised(InvItem item) => CharacterInventoryService.IsAppraised(item);
    internal bool IsDualWieldOffHand(GameSession session, int grid) => _inventory.IsDualWieldOffHand(session, grid);
    public Task EquipAsync(GameSession session, EquipItemOprReq req) => _inventory.EquipAsync(session, req);
    internal int StageEquipmentPlan(DBEquipInfo state, List<InvItem> stagedItems, int index, RoleProgress progress, int sex,
        bool replaceCurrent = false) => _inventory.StageEquipmentPlan(state, stagedItems, index, progress, sex, replaceCurrent);
    internal Task SyncEquipmentAsync(GameSession session) => _inventory.SyncEquipmentAsync(session);
    public Task SwitchEquipmentPlanAsync(GameSession session, EquipSwitchPlanReq req) => _inventory.SwitchEquipmentPlanAsync(session, req);
    public Task RenameEquipmentPlanAsync(GameSession session, EquipChangePlanNameReq req) => _inventory.RenameEquipmentPlanAsync(session, req);
    public Task<string> RemoveItemAsync(GameSession session, int entry, int count) => _inventory.RemoveItemAsync(session, entry, count);
    public long CountOf(GameSession session, int entry) => _inventory.CountOf(session, entry);
    public int ShortageOf(GameSession session, IReadOnlyList<(int Entry, long Count)> cost) => _inventory.ShortageOf(session, cost);
    internal bool PayTalentLocked(GameSession session, IReadOnlyList<(int Entry, long Count)> cost, DBSeasonTalent next) =>
        _inventory.PayTalentLocked(session, cost, next);
    public Task<List<InvItem>?> ExchangeAsync(GameSession session, ExchangeRequest request) => _inventory.ExchangeAsync(session, request);
    public Task<List<InvItem>?> ExchangeAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost,
        IReadOnlyList<(int Entry, int Count)> goods) => _inventory.ExchangeAsync(session, cost, goods);
    internal Task<List<InvItem>?> ExchangeLockedAsync(GameSession session, ExchangeRequest request) => _inventory.ExchangeLockedAsync(session, request);
    internal Task<List<InvItem>?> ExchangeAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost,
        IReadOnlyList<(int Entry, int Count)> goods, DBPetInfo nextPets) => _inventory.ExchangeAsync(session, cost, goods, nextPets);
    internal EconomyMutation FamilyEconomy(GameSession session, string kind, IReadOnlyList<(int Entry, long Count)> costs,
        IReadOnlyList<(int Entry, int Count)> goods, object outcome) => _inventory.FamilyEconomy(session, kind, costs, goods, outcome);
    internal Task GenreAppearanceChangedAsync(GameSession session) => _inventory.GenreAppearanceChangedAsync(session);
    internal int StageStatPlan(BaseAttrInfo state, RoleProgress progress, int index) => _inventory.StageStatPlan(state, progress, index);
    internal Task SyncStatPlansAsync(GameSession session) => _inventory.SyncStatPlansAsync(session);
    internal static RoleProgress GenreCloneProgress(RoleProgress p) => CharacterInventoryService.GenreCloneProgress(p);
    internal Task<string> SetProfessionStatsAsync(GameSession session, long profession) => _inventory.SetProfessionStatsAsync(session, profession);
    internal void RegisterItemAction(int action, Func<ItemActionUse, Task<int>> handler, bool takesParam = false,
        Func<GameSession, Ownership.ReservationKey[]>? keys = null) => _inventory.RegisterItemAction(action, handler, takesParam, keys);
    internal bool ItemUseWidens(GameSession session, ReadOnlyMemory<byte> body) => _inventory.ItemUseWidens(session, body);
    internal Task<bool> CommitItemActionAsync(ItemActionUse use, Action<RoleItemPlanBuilder>? persist, Action? committed,
        object outcome, string kind = CharacterFamilyEconomy.ItemActionKind) =>
        _inventory.CommitItemActionAsync(use, persist, committed, outcome, kind);
    internal Task<bool> CommitSharedItemActionAsync(ItemActionUse use, string kind, Action<RoleItemPlanBuilder> persist,
        Action? committed) =>
        _inventory.CommitSharedItemActionAsync(use, kind, persist, committed);
    internal Task SyncUseTimesAsync(GameSession session) => _inventory.SyncUseTimesAsync(session);
    internal bool HasUseTimes(GameSession session) => _inventory.HasUseTimes(session);
    internal bool IsAwardBox(JsonElement row) => _inventory.IsAwardBox(row);
    internal bool IsChoiceBox(JsonElement row) => _inventory.IsChoiceBox(row);
    internal GetRewardNotify BoxRewardNotify(GameSession session, IReadOnlyList<(int Entry, int Count, int Bind)> goods,
        IReadOnlyList<InvItem> granted, int manifestType) => _inventory.BoxRewardNotify(session, goods, granted, manifestType);
    internal EconomyMutation ProgressionEconomy(GameSession session, string kind, IReadOnlyList<(int Entry, long Count)> costs,
        IReadOnlyList<(int Entry, int Count)> goods, object outcome, string? sourceKey = null) =>
        _inventory.ProgressionEconomy(session, kind, costs, goods, outcome, sourceKey);
    internal bool CanUseItemEffects(JsonElement row) => _inventory.CanUseItemEffects(row);
    internal Task<int> UseItemCoreAsync(GameSession session, ItemUseReq req, bool reply) => _inventory.UseItemCoreAsync(session, req, reply);
    internal string RewardRealmId => _inventory.RewardRealmId;
    internal Task<bool> RewardSourceCommittedAsync(string sourceKey) => _inventory.RewardSourceCommittedAsync(sourceKey);
    internal static bool IsTreasureMapUse(JsonElement row) => CharacterInventoryService.IsTreasureMapUse(row);
    internal ItemBaseInfo ItemInfo(InvItem item) => _inventory.ItemInfo(item);
    internal bool IsEquipment(int entry) => _inventory.IsEquipment(entry);
    internal Dictionary<int, long>? RollAttrs(int entry) => _inventory.RollAttrs(entry);
    internal long AcquireExpiry(int entry) => _inventory.AcquireExpiry(entry);
    internal void MigrateBagPacks(GameSession session) => _inventory.MigrateBagPacks(session);
    internal void LoadEquipmentPlans(GameSession session) => _inventory.LoadEquipmentPlans(session);
    internal static bool PruneEquipmentPlans(GameSession session) => CharacterInventoryService.PruneEquipmentPlans(session);
    internal static IEnumerable<InvItem> Equipped(GameSession session) => CharacterInventoryService.Equipped(session);
    internal DBPackInfo FullPack(IEnumerable<InvItem> items) => _inventory.FullPack(items);
    internal int PackCapacity(int pack) => _inventory.PackCapacity(pack);
    internal Dictionary<int, int> CurrentUseTimes(DBItemsInfo bag) => _inventory.CurrentUseTimes(bag);
    internal static long NextSort(GameSession session, int pack) => CharacterInventoryService.NextSort(session, pack);
    internal ItemAutoDecomposeTipsNtf StageAutoDecomposeAward(GameSession session, List<InvItem> items, RoleProgress progress,
        IEnumerable<InvItem> received, IReadOnlyDictionary<long, int> before, List<(int Entry, int Count)> gained) =>
        _inventory.StageAutoDecomposeAward(session, items, progress, received, before, gained);
    internal EconomyMutation EconomyForOwnedAward(GameSession session, OwnedAwardSource source, AwardPlan plan,
        RoleProgress before, RoleProgress after, IReadOnlyList<(int Entry, long Count)> costs,
        long baseExp, long jobExp, ItemAutoDecomposeTipsNtf conversion) =>
        _inventory.EconomyForOwnedAward(session, source, plan, before, after, costs, baseExp, jobExp, conversion);
    internal EconomyMutation RewardMutation(GameSession session, RewardSettlement settlement, RoleProgress before,
        RoleProgress after, IReadOnlyList<(int Entry, long Count)> costs, IReadOnlyList<(int Entry, int Count)> goods,
        long baseExp, long jobExp, ItemAutoDecomposeTipsNtf conversion) =>
        _inventory.RewardMutation(session, settlement, before, after, costs, goods, baseExp, jobExp, conversion);
    internal Task CommitCharacterAsync(GameSession session, string operation, List<InvItem>? nextItems = null,
        RoleProgress? progress = null, IReadOnlyDictionary<string, byte[]>? blobs = null, Action? apply = null,
        IReadOnlyList<(int Entry, long Count)>? costs = null, IReadOnlyList<(int Entry, int Count)>? goods = null) =>
        _inventory.CommitCharacterAsync(session, operation, nextItems, progress, blobs, apply, costs, goods);
    internal Task CommitBagAsync(GameSession session, string operation, DBItemsInfo next) => _inventory.CommitBagAsync(session, operation, next);
    internal Task CommitStatsAsync(GameSession session, string operation, RoleProgress progress, BaseAttrInfo plans) =>
        _inventory.CommitStatsAsync(session, operation, progress, plans);
}

/// <summary>Per equipment grid (1..7) levels, kept with the role: strengthen, refine and refine luck (1/10000).</summary>
public sealed class EquipLevels
{
    public Dictionary<int, int> Strength { get; set; } = [];
    public Dictionary<int, int> Refine { get; set; } = [];
    public Dictionary<int, int> Luck { get; set; } = [];
}
