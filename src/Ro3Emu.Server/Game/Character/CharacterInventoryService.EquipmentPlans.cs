using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterInventoryService
{
    public const uint EquipSwitchPlan = 31413, EquipChangePlanName = 31414;

    // (inferred) Three initially available, empty, zero-based plans; the original slot entitlement is not recovered.
    // Name limit: PublicParamConfig.SchemeNameNumMax = 8, used by the common scheme rename UI.
    public const int EquipmentPlanCount = 3;
    private const int EquipmentPlanNameLimit = 8;

    /// <summary>enum_EquipGridType MainHand / ViceHand and enum_EquipPosType TwoHand (ItemConfig._iEquipPart).</summary>
    internal const int MainHandGrid = 1, ViceHandGrid = 2, TwoHandPart = 1;

    /// <summary>Stage 31413's validated grid move for a Genre composite; caller persists all owners together.</summary>
    internal int StageEquipmentPlan(DBEquipInfo state, List<InvItem> stagedItems, int index, RoleProgress progress, int sex,
        bool replaceCurrent = false)
    {
        if (!state.EquipPlans.TryGetValue(index, out var target)) return ErrorCodes.Parameter;
        if (index == state.CurrentEquipPlan && !replaceCurrent) return 0;
        if (PlanPlacements(target, stagedItems, progress, sex, out var incoming) is var error and not 0) return error;
        var outgoing = stagedItems.Where(i => i.Grid > 0 && i.Pack == 2 && i.Grid <= 8).Where(i => !incoming.ContainsKey(i.Guid)).ToList();
        int bag = stagedItems.Count(i => i.Grid == 0 && i.Pack == 2);
        int projected = bag + outgoing.Count - incoming.Values.Count(i => i.Item.Grid == 0);
        if (projected > PackCapacity(2) && projected > bag) return ErrorCodes.BagFull;
        if (!replaceCurrent || index != state.CurrentEquipPlan)
        {
            var old = state.EquipPlans[state.CurrentEquipPlan];
            old.ItemsEquipGrid.Clear();
            foreach (var item in stagedItems.Where(i => i.Grid > 0 && i.Pack == 2 && i.Grid <= 8)) old.ItemsEquipGrid[item.Grid] = item.Guid;
        }
        long sort = stagedItems.Where(i => i.Pack == 2).Select(i => i.Sort).DefaultIfEmpty().Max();
        foreach (var item in outgoing) { item.Grid = 0; item.Sort = ++sort; }
        foreach (var (item, grid) in incoming.Values) item.Grid = grid;
        state.CurrentEquipPlan = index;
        return 0;
    }

    /// <summary>
    /// The gear a saved plan places, by guid, validated as 31400 would equip each piece. The client lists a two-handed
    /// weapon (enum_EquipPosType.TwoHand) in both weapon grids of a plan: LC_SchemeMatchHelper.PutOnEquip (lua:502-599)
    /// writes its guid into itemsEquipGrid[MainHand] and [ViceHand], and PutOffEquip clears both when they hold the same
    /// guid; LC_DAL_Equipment's 31400 take-on (lua:994-1044) does the same in EquipGearDataMap. That repeat in grid 2 is
    /// the grid 1 weapon, not a second placement, so it is skipped; the weapon itself sits in grid 1 (its only grid).
    /// </summary>
    private int PlanPlacements(DBEquipPlan plan, IReadOnlyList<InvItem> items, RoleProgress progress, int sex,
        out Dictionary<long, (InvItem Item, int Grid)> incoming)
    {
        incoming = [];
        foreach (var (grid, guid) in plan.ItemsEquipGrid)
        {
            var item = items.FirstOrDefault(i => i.Guid == guid);
            if (item == null) return ErrorCodes.ItemMissing;
            if (!IsEquipment(item.Entry)) return ErrorCodes.NotEquipment;
            if (item.Grid < 0 || item.Pack != 2 || item.Stack != 1) return ErrorCodes.EquipNotInBag;
            if (WearError(item.Entry, progress, sex) is var wear and not 0) return wear;
            if (grid == ViceHandGrid && IsTwoHandRepeat(plan, guid, item)) continue;
            if (!GridsFor(item.Entry, progress).Contains(grid) || !incoming.TryAdd(guid, (item, grid))) return ErrorCodes.EquipSlot;
        }
        return incoming.Values.Any(i => i.Grid == MainHandGrid && PartOf(i.Item.Entry) == TwoHandPart)
            && incoming.Values.Any(i => i.Grid == ViceHandGrid) ? ErrorCodes.EquipSlot : 0;
    }

    /// <summary>Grid 2 of a plan holding the two-handed weapon of its grid 1 (see <see cref="PlanPlacements"/>).</summary>
    private bool IsTwoHandRepeat(DBEquipPlan plan, long guid, InvItem item) =>
        plan.ItemsEquipGrid.TryGetValue(MainHandGrid, out long main) && main == guid && PartOf(item.Entry) == TwoHandPart;

    /// <summary>Migrate existing worn gear into plan 0, keep names and inactive GUID references.</summary>
    internal void LoadEquipmentPlans(GameSession session)
    {
        var saved = roles.LoadEquipPlans(session.Role!.Id);
        session.Profile.EquipmentPlans = saved != null ? DBEquipInfo.Parser.ParseFrom(saved) : new DBEquipInfo();
        var state = session.Profile.EquipmentPlans;
        for (int index = 0; index < EquipmentPlanCount; index++)
            if (!state.EquipPlans.ContainsKey(index)) state.EquipPlans[index] = new DBEquipPlan();
        if (!state.EquipPlans.ContainsKey(state.CurrentEquipPlan)) state.CurrentEquipPlan = 0;
        state.Inited = true;
        RememberEquipment(session);
        PruneEquipmentPlans(session);
        roles.SaveEquipPlans(session.Role.Id, state.ToByteArray());
    }

    /// <summary>31400 edits the selected plan; other plans retain references to the same instances (inferred).</summary>
    private static void RememberEquipment(GameSession session)
    {
        var plan = session.Profile.EquipmentPlans.EquipPlans[session.Profile.EquipmentPlans.CurrentEquipPlan];
        plan.ItemsEquipGrid.Clear();
        foreach (var item in Equipped(session)) plan.ItemsEquipGrid[item.Grid] = item.Guid;
    }

    /// <summary>Consumed/deleted instances leave all plans; a plan never owns a second copy (inferred invariant).</summary>
    internal static bool PruneEquipmentPlans(GameSession session)
    {
        var owned = session.Items.Select(i => i.Guid).ToHashSet();
        bool changed = false;
        foreach (var plan in session.Profile.EquipmentPlans.EquipPlans.Values)
            foreach (var grid in plan.ItemsEquipGrid.Where(p => !owned.Contains(p.Value)).Select(p => p.Key).ToArray())
                changed |= plan.ItemsEquipGrid.Remove(grid);
        return changed;
    }

    internal Task SyncEquipmentAsync(GameSession session) => SyncAsync(session, EquipSync, BuildEquip(session));

    internal async Task EquipmentAppearanceChangedAsync(GameSession session)
    {
        Refresh(session);
        var look = new EntityBase { Uuid = (ulong)session.Role!.Id, NotifyType = EntityBase.Types.NotifyType.Equip };
        look.EquipInfo.Add(EquipInfo(session));
        await ToSelfAndViewersAsync(session, EntityBaseChange, look);
        await SendStatsAsync(session);
        foreach (var handler in WornGearChanged.Handlers) await handler(session);
    }

    /// <summary>
    /// 31413 / 5007: validate the whole target before moving any GUID. Outgoing gear returns to
    /// 5009, target gear leaves it, then the current index, model and stats change. Movement/order are (inferred).
    /// Cards and upgrade levels remain on their grids. Selecting the current plan is an idempotent success (inferred).
    /// </summary>
    public async Task SwitchEquipmentPlanAsync(GameSession session, EquipSwitchPlanReq req)
    {
        using var _ = await LockAsync(session);
        var state = session.Profile.EquipmentPlans;
        int result = state.EquipPlans.TryGetValue(req.TargetIndex, out var target) ? 0 : ErrorCodes.Parameter;
        var incoming = new Dictionary<long, (InvItem Item, int Grid)>();
        // Only the incoming gear is checked: a role may always switch away from gear it can no longer wear.
        if (result == 0) result = PlanPlacements(target!, session.Items, session.Progress!, session.Role?.Sex ?? 0, out incoming);
        if (result == 0 && req.TargetIndex != state.CurrentEquipPlan)
        {
            var outgoing = Equipped(session).Where(i => !incoming.ContainsKey(i.Guid)).ToList();
            int oldBagSize = session.Items.Count(i => i.Grid == 0 && i.Pack == 2);
            int newBagSize = oldBagSize + outgoing.Count - incoming.Values.Count(i => i.Item.Grid == 0);
            // (inferred) A switch may not add cells beyond capacity; a swap that reduces an already overfull bag is allowed.
            if (newBagSize > PackCapacity(2) && newBagSize > oldBagSize) result = ErrorCodes.BagFull;
            else
            {
                var next = state.Clone();
                var staged = session.Items.Select(i => i.Clone()).ToList();
                var oldPlan = next.EquipPlans[next.CurrentEquipPlan];
                oldPlan.ItemsEquipGrid.Clear();
                foreach (var worn in Equipped(session)) oldPlan.ItemsEquipGrid[worn.Grid] = worn.Guid;
                foreach (var old in outgoing)
                {
                    var moved = staged.Single(i => i.Guid == old.Guid);
                    moved.Grid = 0;
                    moved.Sort = staged.Where(i => i.Grid == 0 && i.Pack == moved.Pack)
                        .Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1;
                }
                foreach (var (selected, grid) in incoming.Values) staged.Single(i => i.Guid == selected.Guid).Grid = grid;
                next.CurrentEquipPlan = req.TargetIndex;
                await CommitCharacterAsync(session, "equip-plan-switch", nextItems: staged,
                    blobs: new Dictionary<string, byte[]> { ["equip_plans"] = next.ToByteArray() },
                    apply: () => session.Profile.EquipmentPlans = next);
                state = next;
                await SyncItemsAsync(session);
                await SyncEquipmentAsync(session);
            }
        }
        await session.SendGameAsync(EquipSwitchPlan, new EquipSwitchPlanResp { Result = result, CurrentPlan = state.CurrentEquipPlan });
        if (result == 0)
        {
            await EquipmentAppearanceChangedAsync(session);
            foreach (var handler in StateChanged.Handlers) handler(session);
        }
    }

    /// <summary>31414 has no name in the response: persist it and send the 5007 planName increment.</summary>
    public async Task RenameEquipmentPlanAsync(GameSession session, EquipChangePlanNameReq req)
    {
        using var _ = await LockAsync(session);
        // (inferred) Reject blank/control text; count Unicode scalars, preserving the supplied name exactly.
        int result = !session.Profile.EquipmentPlans.EquipPlans.ContainsKey(req.TargetIndex)
            || string.IsNullOrWhiteSpace(req.Name) || req.Name.Any(char.IsControl)
            || req.Name.EnumerateRunes().Count() > EquipmentPlanNameLimit ? ErrorCodes.Parameter : 0;
        if (result == 0)
        {
            var next = session.Profile.EquipmentPlans.Clone();
            next.EquipPlans[req.TargetIndex].PlanName = req.Name;
            await CommitCharacterAsync(session, "equip-plan-rename",
                blobs: new Dictionary<string, byte[]> { ["equip_plans"] = next.ToByteArray() },
                apply: () => session.Profile.EquipmentPlans = next);
            await SyncEquipmentAsync(session);
        }
        await session.SendGameAsync(EquipChangePlanName, new EquipChangePlanNameResp { Result = result });
    }
}
