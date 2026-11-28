using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterService
{
    // ---- login ----

    /// <summary>Loads the role's state into the session (role select); <see cref="FinishLoadAsync"/> follows.</summary>
    public void Load(GameSession session)
    {
        var role = session.Role!;
        session.Season.SeasonTalent = null; // role switching must not leak the previous role's attribute source
        session.Season.SeasonRefine = null;
        session.Season.SeasonPage = null;
        session.SeasonState = null;
        session.Tasks = null; // task gates must likewise wait for the selected role's task load
        session.Instances.Dungeons = null; // progression hooks must wait for this role's 5062 state
        session.Instances.GuildChallenge = null; // a pending clear must never see the previous role's entitlement cache
        session.Instances.DungeonSyncMinute = 0;
        var progress = roles.LoadProgress(role.Id);
        session.Progress = progress;
        session.Profile.GmAllFunctionsOpen = roles.LoadGmState(role.Id) is { Length: > 0 } gm
            && JsonSerializer.Deserialize<GmCommands.RoleGmState>(gm)?.AllFunctionsOpen == true; // GM panel openallfunc
        session.Pets = null; // The next role's pets load before 5056; never reuse a previous role's stat contributions.
        session.Items = itemStore.Load(role.Id);
        session.Profile.BagState = new RoleBagState(roles.LoadBagState(role.Id));
        session.Combat.ItemCds.Clear();
        session.Combat.ItemGroupCds.Clear();
        long nowMs = session.NowMs;
        foreach (var (id, end) in session.Profile.BagState.Info.ItemCds?.Data ?? new CdData().Data)
            if (id is > 0 and <= int.MaxValue && end > nowMs) session.Combat.ItemCds[(int)id] = end;
        foreach (var (id, end) in session.Profile.BagState.Info.ShareCds?.Data ?? new CdData().Data)
            if (id is > 0 and <= int.MaxValue && end > nowMs) session.Combat.ItemGroupCds[(int)id] = end;
        session.Profile.StatPlans = new StatPlanState(roles.LoadStatPlans(role.Id));
        LoadStatPlans(session);
        MigrateEquipmentInstances(session);
        MigrateBagPacks(session);
        MigrateAcquireTimeBoundItems(session);
        session.Profile.EquipLevels = roles.LoadEquipLevels(role.Id) is { } levels ? System.Text.Json.JsonSerializer.Deserialize<EquipLevels>(levels) ?? new() : new();
        LoadEquipmentPlans(session);
        UnequipUnwearable(session);
        session.Profile.AutoDecompose = roles.LoadAutoDecompose(role.Id) is { } setting ? AutoDecomposeSetting.Parser.ParseFrom(setting) : new();
        session.Synced.Clear();
        session.SyncedPacked.Clear();
        session.Profile.SentPropertyIds.Clear();
        session.Profile.NewItems.Clear();
        session.Profile.SyncVersions.Clear();
        // Stats are current before the selection awaits anything: the loop may tick this session meanwhile.
        Refresh(session);
    }

    /// <summary>GM panel openallfunc: every function counts as open for the role, now and at its later logins.</summary>
    internal void OpenAllFunctions(GameSession session)
    {
        roles.SaveGmState(session.Role!.Id, JsonSerializer.SerializeToUtf8Bytes(new GmCommands.RoleGmState { AllFunctionsOpen = true }));
        session.Profile.GmAllFunctionsOpen = true;
    }

    /// <summary>The rest of <see cref="Load"/>: a new role's starter kit.</summary>
    public async Task FinishLoadAsync(GameSession session)
    {
        var role = session.Role!;
        var progress = session.Progress!;
        // Without the exported ItemConfig nothing is known about items: no kit yet (it comes on a later login).
        if (!progress.StarterKit && data.Row("ItemConfig", ZenyId) != null)
        {
            // The starter kit (its items and the progress flag and zeny) is one asynchronous transaction; an insert
            // per item plus a progress write blocked the loop four times per new role. The selection's reservation
            // holds the role's other paths meanwhile. Item-gained hooks follow the commit, as before.
            var before = session.Items.Select(i => i.Clone()).ToList();
            var gained = new List<(int Entry, int Count)>();
            foreach (var (entry, count) in StarterKit)
            {
                AddItemCore(session, entry, count, out bool currency, persist: false, notifyItemGained: false);
                if (!currency) gained.Add((entry, count));
            }
            progress.StarterKit = true;
            var tx = roles.NewTransaction();
            int receipt = tx.ItemDelta(role.Id, before, session.Items);
            tx.Progress(role.Id, progress);
            // The kit and its Zeny enter the economy ledger as starter grants (EconomyReason.StarterGrant), so the
            // panel's duplication check sees where a new character's first items came from.
            tx.WithEconomy(FamilyEconomy(session, StarterKitEconomyKind, [], StarterKit,
                new { Operation = "starter-kit", Items = session.Items, Progress = progress }));
            var items = session.Items;
            await roles.CommitAsync(tx, "character/starter-kit", receipts =>
            {
                var guids = (long[])receipts[receipt]!;
                for (int i = 0; i < items.Count; i++) items[i].Guid = guids[i];
            });
            foreach (var (entry, count) in gained) foreach (var handler in ItemGained.Handlers) handler(session, entry, count);
            Refresh(session);
        }
    }

    /// <summary>
    /// (inferred) Legacy gear was always advertised as appraised and could share a stack/roll. Preserve its flag,
    /// original GUID, grid and existing rolls; extra copies retain those rolls but get separate bag GUIDs. All
    /// changes commit together before login sync, so restart cannot lose or duplicate the old stack's pieces.
    /// </summary>
    /// <summary>
    /// Worn gear the role may not wear (CharacterInventoryService.WearError: level, class, sex), put on before the server
    /// checked it or by a job change since, goes to the bag at login, even past its capacity rather than lost; the current
    /// plan forgets it, the other plans keep it and 31413 refuses them until the role qualifies (owner, 27 Nov 2026).
    /// </summary>
    private void UnequipUnwearable(GameSession session)
    {
        if (session.Role is not { } role) return;
        var off = session.Items.Where(i => i.Grid > 0 && i.Pack == 2 && IsEquipment(i.Entry))
            .Select(i => (Item: i, Error: _inventory.WearError(i.Entry, session.Progress!, role.Sex)))
            .Where(x => x.Error != 0).ToList();
        if (off.Count == 0) return;
        long sort = session.Items.Where(i => i.Grid == 0 && i.Pack == 2).Select(i => i.Sort).DefaultIfEmpty().Max();
        var moved = new Dictionary<long, InvItem>();
        var plans = session.Profile.EquipmentPlans.Clone();
        var current = plans.EquipPlans[plans.CurrentEquipPlan];
        var builder = roles.NewTransaction();
        foreach (var (worn, error) in off)
        {
            var item = worn.Clone();
            current.ItemsEquipGrid.Remove(worn.Grid);
            item.Grid = 0;
            item.Sort = ++sort;
            moved[item.Guid] = item;
            builder.ItemUpdate(item);
            LogUnwearableMoved(log, role.Name, worn.Guid, worn.Entry, worn.Grid, error);
        }
        roles.SaveEquipPlans(role.Id, plans.ToByteArray(), builder);
        roles.Commit(builder, "character/unequip-unwearable");
        session.Items = [.. session.Items.Select(i => moved.GetValueOrDefault(i.Guid, i))];
        session.Profile.EquipmentPlans = plans;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} cannot wear {Item} ({Entry}) in grid {Grid}: {Error}; moved to the bag")]
    private static partial void LogUnwearableMoved(ILogger logger, string name, long item, int entry, int grid, int error);

    private void MigrateEquipmentInstances(GameSession session)
    {
        var oldItems = session.Items.Where(i => IsEquipment(i.Entry) && (i.Stack > 1 || i.Attrs == null
            || !i.Attrs.ContainsKey(AttrAppraise) || EquipmentAffixes.NeedsLegacyEntries(i, data))).ToList();
        if (oldItems.Count == 0) return;
        var replacements = new Dictionary<long, InvItem>();
        var additions = new List<InvItem>();
        var sorts = session.Items.Where(i => i.Grid == 0).GroupBy(i => i.Pack).ToDictionary(g => g.Key, g => g.Max(i => i.Sort));
        foreach (var old in oldItems)
        {
            var item = old.Clone();
            if (item.Attrs == null || !item.Attrs.ContainsKey(AttrAppraise))
            {
                item.Attrs ??= RollAttrs(item.Entry) ?? [];
                item.Attrs[AttrAppraise] = 1;
            }
            if (EquipmentAffixes.NeedsLegacyEntries(item, data)) EquipmentAffixes.Appraise(item, data);
            if (item.Stack > 1) item.Stack = 1;
            replacements[item.Guid] = item;
            for (int n = 1; n < old.Stack; n++)
            {
                var copy = item.Clone();
                copy.Guid = 0;
                copy.Grid = 0;
                copy.Sort = sorts[copy.Pack] = sorts.GetValueOrDefault(copy.Pack) + 1;
                additions.Add(copy);
            }
        }
        var builder = roles.NewTransaction();
        foreach (var item in replacements.Values) builder.ItemUpdate(item);
        var inserted = additions.Select(item => builder.Add(new ItemWritePlan(ItemWriteKind.Insert,
            role: session.Role!.Id, items: [item]))).ToArray();
        var receipts = roles.Commit(builder, "character/equipment-migration");
        for (int i = 0; i < additions.Count; i++) additions[i].Guid = ((long[])receipts[inserted[i]]!)[0];
        session.Items = [.. session.Items.Select(i => replacements.GetValueOrDefault(i.Guid, i)), .. additions];
    }

    /// <summary>
    /// (inferred) An _iTimeBoundEnum 1 instance obtained while the end was not stored has none, and the client shows it as
    /// expired (AcquireExpiry). It was never usable, so it gets one full duration from this login; the stored end makes
    /// this happen once.
    /// </summary>
    private void MigrateAcquireTimeBoundItems(GameSession session)
    {
        var stamped = new Dictionary<long, InvItem>();
        foreach (var old in session.Items.Where(i => i.BindExpire == 0))
            if (AcquireExpiry(old.Entry) is var end and not 0)
            {
                var item = old.Clone();
                item.BindExpire = end;
                stamped[item.Guid] = item;
            }
        if (stamped.Count == 0) return;
        var builder = roles.NewTransaction();
        foreach (var item in stamped.Values) builder.ItemUpdate(item);
        roles.Commit(builder, "character/time-bound-migration");
        session.Items = [.. session.Items.Select(i => stamped.GetValueOrDefault(i.Guid, i))];
    }

    /// <summary>Full syncs of the role datasets, after role select (5066 must precede 5009: currency merges into it).</summary>
    public async Task SendLoginSyncAsync(GameSession session)
    {
        await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
        await SendStatPlanAsync(session);
        var bag = BuildItems(session);
        if (bag.PackInfo.Count == 0) log.LogWarning("{Name} has an empty bag; the client may not build it", session.Role!.Name);
        await SyncAsync(session, ItemsSync, bag);
        if (CareerPackCapacity(session) is var cartSize and > 0)
        {
            await session.SendGameAsync(CareerSpecialSizeChange, new NotifyCareerSpecialPackSizeChange { Size = cartSize });
            session.Profile.BagState.LastNotifiedCartSize = cartSize;
        }
        await SyncAsync(session, EquipSync, BuildEquip(session));
    }

    /// <summary>Keep 5066 power fields in step with 5005 and the 31502/31503 notifications.</summary>
    internal Task SyncBaseInfoAsync(GameSession session) => SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
}
