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
    // ---- bag: grants and award application ----

    /// <summary>Adds items (or a currency, or exp for the virtual items 100/101) and tells the client.</summary>
    public async Task<string> AddItemAsync(GameSession session, int entry, int count)
    {
        if (data.Row("ItemConfig", entry) is not { } row) throw new ArgumentException("unknown item");
        if (entry == BaseExpItem) return await AddExpAsync(session, count, 0);
        if (entry == JobExpItem) return await AddExpAsync(session, 0, count);
        using var _ = await LockAsync(session);
        count = Math.Clamp(count, 1, GameData.Int(row, "_iType") == ItemTypeCurrency ? int.MaxValue : 999);
        // An exchange keeps the live InvItem instances (the award path would replace them).
        await ExchangeLockedAsync(session, new ExchangeRequest
        {
            Goods = [(entry, count)],
            Persist = tx => tx.WithEconomy(FamilyEconomy(session, "cd13a/character-gm-grant", [], [(entry, count)],
                new { Entry = entry, Count = count })),
        });
        return $"+{count} {data.ItemName(entry)} ({entry})";
    }

    /// <summary>Loot: several items / currencies at once, one bag sync, marked new (no float: the drop notify shows them).</summary>
    public Task GrantAsync(GameSession session, IReadOnlyList<(int Entry, int Count)> items, Func<bool>? stillOwner = null) =>
        GrantBoundAsync(session, [.. items.Select(i => (i.Entry, i.Count, Bind: 0))], stillOwner);

    /// <summary>Quest grants preserve AwardConfig's bind flag; bound and unbound stacks remain separate.</summary>
    public async Task GrantBoundAsync(GameSession session, IReadOnlyList<(int Entry, int Count, int Bind)> items,
        Func<bool>? stillOwner = null)
    {
        if (stillOwner?.Invoke() == false) return;
        using var held = await LockIntentAsync(session, stillOwner);
        if (held == null) return;
        if (stillOwner?.Invoke() == false) return;
        var plan = new AwardPlan(0, 0, items.Where(x => x.Count > 0)
            .Select(x => new AwardGrant(x.Entry, x.Count, x.Bind)));
        if (plan.Grants.Count == 0) return;
        var source = new OwnedAwardSource("cd13e/grant", $"grant/{session.Role!.Id}/{Guid.NewGuid():N}",
            new { Version = 1, Grants = items });
        if (!await ApplyAwardAsync(session, plan, stateLockHeld: true, ownedAward: source,
            suppressRewardNotice: true))
            throw new InvalidOperationException("Owned bound grant was not committed.");
    }

    /// <summary>
    /// A stage reward grant (31178/31175, any type) is staged in copies and committed with its claim blob on one SQLite connection. No
    /// session mutation or success packet precedes commit; a failed transaction leaves the old state intact.
    /// </summary>
    public async Task<bool> GrantDailyStageAsync(GameSession session, IReadOnlyList<(int Entry, int Count, int Bind)> goods,
        byte[] previousDaily, byte[] nextDaily, DBTaskInfo? nextTasks = null, byte[]? expectedTasks = null, long questExp = 0)
    {
        using var held = await LockAsync(session);
        if (session.Activities.Daily == null || !JsonSerializer.SerializeToUtf8Bytes(session.Activities.Daily).AsSpan().SequenceEqual(previousDaily)
            || expectedTasks != null && (session.Tasks == null || !session.Tasks.ToByteArray().AsSpan().SequenceEqual(expectedTasks)))
            return false;
        var before = JsonSerializer.Deserialize<DailyActivityService.DailyState>(previousDaily);
        var after = JsonSerializer.Deserialize<DailyActivityService.DailyState>(nextDaily);
        if (before?.Claims == null || after?.Claims == null || before.DailyEpoch != after.DailyEpoch ||
            after.Claims.Except(before.Claims).Order().ToArray() is not { Length: > 0 } stages ||
            before.Claims.Except(after.Claims).Any() || questExp < 0 ||
            goods.Any(g => g.Count <= 0 || data.Row("ItemConfig", g.Entry) == null)) return false;
        var plan = new AwardPlan(0, 0, goods.Select(g => new AwardGrant(g.Entry, g.Count, g.Bind)));
        // One stage (31178) or a batch (31175): the batch's source names all of its stages.
        object outcome = stages is [int stage]
            ? new { Version = 1, Stage = stage, Epoch = after.DailyEpoch, Grants = goods, QuestExp = questExp }
            : new { Version = 1, Stages = stages, Epoch = after.DailyEpoch, Grants = goods, QuestExp = questExp };
        var source = new OwnedAwardSource("cd13e/daily-stage",
            $"daily-stage/{session.Role!.Id}/{after.DailyEpoch}/{string.Join(",", stages)}", outcome);
        try
        {
            return await ApplyAwardAsync(session, plan, taskBaseExp: questExp,
                deliveredTaskState: nextTasks?.ToByteArray(), expectedTasks: expectedTasks,
                expectedDaily: previousDaily, nextDaily: nextDaily, stateLockHeld: true, ownedAward: source);
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            log.LogError(error, "Daily stage transaction failed for {Role}", session.Role?.Id);
            return false;
        }
    }
    /// <summary>
    /// Applies an already resolved source award and optional costs/task boundary as one SQLite transaction
    ///. False means a cost was short and nothing changed. A source must supply
    /// prospective deliveredTaskState to make its once-only completion durable with the grant.
    /// </summary>
    public async Task<bool> ApplyAwardAsync(GameSession session, AwardPlan plan, long taskBaseExp = 0,
        IReadOnlyList<(int Entry, int Count)>? costs = null, byte[]? deliveredTaskState = null,
        byte[]? taskRuntimeState = null, int manifestType = 1, byte[]? expectedDaily = null,
        byte[]? nextDaily = null, byte[]? expectedTasks = null, Action<RoleItemPlanBuilder>? persist = null,
        bool stateLockHeld = false, Action? committed = null, EconomyMutation? familyEconomy = null,
        OwnedAwardSource? ownedAward = null, bool suppressRewardNotice = false, IReadOnlyList<int>? rewardIndexes = null)
    {
        using var _ = stateLockHeld ? null : await LockAsync(session);
        var context = RequireOwnedRole(session);
        return await ApplyAwardCoreAsync(session, context, plan, taskBaseExp, costs, deliveredTaskState,
            taskRuntimeState, manifestType, expectedDaily, nextDaily, expectedTasks, persist, committed,
            familyEconomy, ownedAward, suppressRewardNotice, rewardIndexes);
    }

    private sealed class AwardStaging
    {
        public required GameSession Session;
        public required long RoleId;
        public required AwardPlan Plan;
        public required RoleProgress OldProgress;
        public required RoleProgress StagedProgress;
        public required HashSet<long> OldItemGuids;
        public required List<InvItem> Items;
        public required List<(int Entry, long Count)> Expense;
        public required Romsg.GetRewardNotify Notification;
        public readonly List<(int Entry, int Count)> Gained = [];
        public readonly List<(int Row, InvItem Item)> GearRows = [];
        public readonly HashSet<InvItem> Received = [];
        public long BaseExp;
        public long JobExp;
        public int OldLevel;
        public int OldJob;
        public ItemAutoDecomposeTipsNtf Decomposed = new();
        public FirstCardSightPlan? CardSight;
        public DBTaskInfo? NextTasks;
        public TaskRuntimeState? NextRuntime;
        public DailyActivityService.DailyState? NextDailyState;
        public byte[]? DeliveredTaskState;
        public byte[]? TaskRuntimeState;
        public byte[]? ExpectedDaily;
        public byte[]? NextDaily;
        public byte[]? ExpectedTasks;
        public byte[]? PersistedTasks;
        public int ManifestType;
        public bool SuppressRewardNotice;
        public IReadOnlyList<int>? RewardIndexes;
        public Action<RoleItemPlanBuilder>? Persist;
        public Action? Committed;
        public EconomyMutation? FamilyEconomy;
        public OwnedAwardSource? OwnedAward;
    }

    /// <summary>Checked owned Core: stage copied award values, commit, apply before notices.</summary>
    private async Task<bool> ApplyAwardCoreAsync(GameSession session, Ownership.OwnerContext context, AwardPlan plan,
        long taskBaseExp, IReadOnlyList<(int Entry, int Count)>? costs, byte[]? deliveredTaskState,
        byte[]? taskRuntimeState, int manifestType, byte[]? expectedDaily, byte[]? nextDaily,
        byte[]? expectedTasks, Action<RoleItemPlanBuilder>? persist, Action? committed,
        EconomyMutation? familyEconomy, OwnedAwardSource? ownedAward, bool suppressRewardNotice,
        IReadOnlyList<int>? rewardIndexes = null)
    {
        long roleId = session.Role!.Id;
        context.AssertRole(roleId);
        context.BindPresentation(session);
        var attachment = session.Attachment;
        ArgumentOutOfRangeException.ThrowIfNegative(taskBaseExp);
        // PBManifestType.ManifestTypeRunRing (18) needs the slots LC_DAL_Reward uses to stop the wheel.
        if (manifestType is < 1 or > 19 or 15 || manifestType == TaskService.ManifestTypeRunRing && rewardIndexes is not { Count: > 0 })
            throw new ArgumentOutOfRangeException(nameof(manifestType));
        if ((expectedDaily == null) != (nextDaily == null)) throw new ArgumentException("Daily award requires before and after state");
        if (expectedTasks != null && deliveredTaskState == null) throw new ArgumentException("Task comparison requires next task state");
        if (expectedDaily != null && (session.Activities.Daily == null
                || !JsonSerializer.SerializeToUtf8Bytes(session.Activities.Daily).AsSpan().SequenceEqual(expectedDaily))) return false;
        if (expectedTasks != null && (session.Tasks == null || !session.Tasks.ToByteArray().AsSpan().SequenceEqual(expectedTasks))) return false;
        var old = session.Progress ?? throw new InvalidOperationException("award requires role progress");
        var staging = new AwardStaging
        {
            Session = session, RoleId = roleId, Plan = plan, OldProgress = old, StagedProgress = CloneProgress(old),
            OldItemGuids = [.. session.Items.Select(i => i.Guid)],
            Items = [.. session.Items.Select(i => i.Clone())],
            Expense = [.. (costs ?? []).GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: g.Sum(c => (long)c.Count)))],
            Notification = new Romsg.GetRewardNotify { TargetUid = roleId },
            BaseExp = taskBaseExp,
            NextTasks = deliveredTaskState == null ? null : DBTaskInfo.Parser.ParseFrom(deliveredTaskState),
            NextRuntime = taskRuntimeState == null ? null : JsonSerializer.Deserialize<TaskRuntimeState>(taskRuntimeState)
                ?? throw new InvalidOperationException("invalid task runtime award state"),
            NextDailyState = nextDaily == null ? null : JsonSerializer.Deserialize<DailyActivityService.DailyState>(nextDaily)
                ?? throw new InvalidOperationException("invalid daily award state"),
            DeliveredTaskState = deliveredTaskState, TaskRuntimeState = taskRuntimeState,
            ExpectedDaily = expectedDaily, NextDaily = nextDaily, ExpectedTasks = expectedTasks,
            ManifestType = manifestType, SuppressRewardNotice = suppressRewardNotice, RewardIndexes = rewardIndexes,
            Persist = persist, Committed = committed, FamilyEconomy = familyEconomy, OwnedAward = ownedAward,
        };
        if (!DeductAwardCosts(staging)) return false;
        StageAwardGrants(staging);
        if (!await CommitAwardAsync(staging)) return false;
        if (attachment?.Cancelled == true || !ReferenceEquals(session.Attachment, attachment)) return true;
        await PresentAwardAsync(staging);
        return true;
    }

    private bool DeductAwardCosts(AwardStaging s)
    {
        foreach (var (entry, count) in s.Expense)
        {
            if (count < 0 || data.Row("ItemConfig", entry) == null) throw new InvalidOperationException($"invalid award cost {entry} x{count}");
            long owned = IsCurrency(entry) ? s.StagedProgress.Currency.GetValueOrDefault(entry)
                : s.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(s.Session, i)
                    && !TotemService.IsReserved(s.Session, i)).Sum(i => (long)i.Stack);
            if (owned < count) return false;
        }
        foreach (var (entry, count) in s.Expense)
        {
            if (IsCurrency(entry)) { s.StagedProgress.Currency[entry] -= count; continue; }
            long remaining = count;
            var stacks = s.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(s.Session, i)
                && !TotemService.IsReserved(s.Session, i)).OrderBy(i => i.Stack).ToList();
            foreach (var item in stacks)
            {
                if (remaining == 0) break;
                int take = (int)Math.Min(remaining, item.Stack);
                item.Stack -= take;
                remaining -= take;
                if (item.Stack == 0) s.Items.Remove(item);
            }
        }
        return true;
    }

    private void StageAwardGrants(AwardStaging s)
    {
        var beforeGrants = s.Items.Where(i => i.Guid > 0).ToDictionary(i => i.Guid, i => i.Stack);
        foreach (var grant in s.Plan.Grants)
        {
            if (grant.Entry <= 0 || grant.Count <= 0 || data.Row("ItemConfig", grant.Entry) == null
                || grant.BindType is < 0 or > 2 || grant.ExpireTimeStamp < 0
                || grant.BindType == 2 && grant.ExpireTimeStamp == 0)
                throw new InvalidOperationException($"invalid award grant {grant.Entry}");
            if ((grant.Entry is BaseExpItem or JobExpItem) && (grant.BindType != 0 || grant.ExpireTimeStamp != 0))
                throw new InvalidOperationException("bound or expiring virtual EXP award");
            if (grant.Entry == BaseExpItem) { s.BaseExp = checked(s.BaseExp + grant.Count); continue; }
            if (grant.Entry == JobExpItem) { s.JobExp = checked(s.JobExp + grant.Count); continue; }
            if (IsCurrency(grant.Entry)) StageAwardCurrencyGrant(s, grant);
            else if (StageAwardItemGrant(s, grant)) continue;
            s.Notification.Rewards.Add(ItemInfo(new InvItem
            {
                Entry = grant.Entry, Stack = grant.Count,
                BindType = grant.BindType, ExpireTimeStamp = grant.ExpireTimeStamp,
                Attrs = s.Items.LastOrDefault(i => i.Entry == grant.Entry)?.Attrs
            }));
        }
        // Quest award auto returns are staged alongside the source grant, costs, tasks and daily state.
        // Other ApplyAward callers have no quest delivery boundary and retain their standalone behavior.
        s.Decomposed = s.DeliveredTaskState == null && s.OwnedAward?.CommandKind != "cd13e/grant" ? new ItemAutoDecomposeTipsNtf()
            : StageAutoDecomposeAward(s.Session, s.Items, s.StagedProgress, s.Received, beforeGrants, s.Gained);
        s.JobExp = checked(s.JobExp + JobExpFromBase(s.BaseExp));
        AddVirtualReward(s.Notification, BaseExpItem, s.BaseExp);
        AddVirtualReward(s.Notification, JobExpItem, s.JobExp);
        s.OldLevel = s.StagedProgress.Level;
        s.OldJob = s.StagedProgress.JobLevel;
        if (s.BaseExp > 0 || s.JobExp > 0) ApplyExp(s.StagedProgress, s.BaseExp, s.JobExp);
        // TaskService can hold unsaved in-memory objective progress. Compare that snapshot above, then
        // use the persisted blob as the SQLite CAS baseline for this single award transaction.
        s.PersistedTasks = s.ExpectedTasks == null ? null : roles.LoadTasks(s.RoleId);
        s.CardSight = StageFirstCardSight?.Invoke(s.Session, [.. s.Gained.Select(g => g.Entry)], 0);
    }

    private void StageAwardCurrencyGrant(AwardStaging s, AwardGrant grant)
    {
        var currencyRow = data.Row("ItemConfig", grant.Entry)!.Value;
        if (grant.ExpireTimeStamp != 0 || grant.BindType == 2
            || grant.BindType == 1 && (!currencyRow.TryGetProperty("_iTrade", out var trade) || GameData.Number(trade) != 0))
            throw new InvalidOperationException("unsupported bound or expiring currency award");
        // (inferred) DBItemsInfo.currency has only an aggregate amount, so permanent bind does not partition the balance.
        s.StagedProgress.Currency[grant.Entry] = checked(s.StagedProgress.Currency.GetValueOrDefault(grant.Entry) + grant.Count);
    }

    private bool StageAwardItemGrant(AwardStaging s, AwardGrant grant)
    {
        var row = data.Row("ItemConfig", grant.Entry)!.Value;
        int stackLimit = GameData.Int(row, "_iType") == ItemTypeEquip ? 1 : (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
        int pack = ConfigPack(data, row);
        int left = grant.Count;
        var created = new List<InvItem>();
        foreach (var item in s.Items.Where(i => CanStackGrant(i, grant.Entry, stackLimit, grant.BindType, grant.ExpireTimeStamp)))
        {
            int add = Math.Min(left, stackLimit - item.Stack);
            item.Stack += add;
            left -= add;
            s.Received.Add(item);
            if (left == 0) break;
        }
        while (left > 0)
        {
            int stack = Math.Min(left, stackLimit);
            var item = new InvItem
            {
                Entry = grant.Entry, Stack = stack, Pack = pack,
                Sort = s.Items.Where(i => i.Grid == 0 && i.Pack == pack).Select(i => i.Sort).DefaultIfEmpty(0).Max() + 1,
                BindType = grant.BindType, BindExpire = grant.ExpireTimeStamp != 0 ? grant.ExpireTimeStamp : AcquireExpiry(grant.Entry),
                Attrs = TotemAttrFactory?.Invoke(grant.Entry) ?? EmblemAttrFactory?.Invoke(grant.Entry) ?? RollAttrs(grant.Entry)
            };
            s.Items.Add(item);
            created.Add(item);
            s.Received.Add(item);
            left -= stack;
        }
        s.Gained.Add((grant.Entry, grant.Count));
        if (!IsEquipment(grant.Entry)) return false;
        // Each gear instance may roll distinct attributes; a single aggregate popup would misstate them.
        // GUIDs are assigned at commit; the row is rebuilt then (quick-equip prompt needs itemGuid).
        foreach (var item in created)
        {
            s.GearRows.Add((s.Notification.Rewards.Count, item));
            s.Notification.Rewards.Add(ItemInfo(item));
        }
        return true;
    }

    private static void PublishAwardState(AwardStaging s)
    {
        s.Session.Progress = s.StagedProgress;
        s.Session.Items = s.Items;
        if (s.NextTasks != null) s.Session.Tasks = s.NextTasks;
        if (s.NextRuntime != null) s.Session.Activities.TaskRuntime = s.NextRuntime;
        if (s.NextDailyState != null)
        {
            s.Session.Activities.Daily = s.NextDailyState;
            s.Session.Activities.DailyRows = DBDailyActivityInfo.Parser.ParseFrom(s.NextDailyState.Rows);
        }
        s.CardSight?.Apply();
        s.Committed?.Invoke();
    }

    private async Task<bool> CommitAwardAsync(AwardStaging s)
    {
        long roleId = s.RoleId;
        if (s.OwnedAward != null)
        {
            if (s.FamilyEconomy != null) throw new InvalidOperationException("Award cannot carry two economy envelopes.");
            var economy = EconomyForOwnedAward(s.Session, s.OwnedAward, s.Plan, s.OldProgress, s.StagedProgress, s.Expense,
                s.BaseExp, s.JobExp, s.Decomposed);
            var builder = roles.NewTransaction();
            int itemReceipt = builder.ItemDelta(roleId, s.Session.Items, s.Items);
            builder.Add(new RoleAwardStatePlan(roleId, s.StagedProgress, s.DeliveredTaskState, s.TaskRuntimeState,
                s.ExpectedDaily, s.NextDaily, s.PersistedTasks, s.ExpectedTasks != null));
            s.Persist?.Invoke(builder);
            s.CardSight?.Persist(builder);
            builder.WithEconomy(economy);
            try
            {
                await roles.CommitAsync(builder, "character/owned-award", receipts =>
                {
                    ItemSnapshotWritePlan.ApplyGuids(s.Items, (long[])receipts[itemReceipt]!);
                    s.StagedProgress.SourceRevision = builder.CommittedVector(roleId, RevisionStore.Progress);
                    PublishAwardState(s);
                });
            }
            catch (RolePlanConflictException) { return false; }
            return true;
        }
        if (s.FamilyEconomy != null)
        {
            // Pet-lottery economy seam. Ordinary award/EXP families retain their own receipt boundary.
            if (s.FamilyEconomy.CommandKind != "cd13b/pet-lottery" || s.DeliveredTaskState != null ||
                s.TaskRuntimeState != null || s.ExpectedDaily != null || s.ExpectedTasks != null || s.BaseExp != 0 || s.JobExp != 0)
                throw new InvalidOperationException("The pet lottery envelope cannot enlist unrelated award boundaries.");
            var builder = roles.NewTransaction();
            int itemReceipt = builder.ItemDelta(roleId, s.Session.Items, s.Items);
            builder.Progress(roleId, s.StagedProgress);
            s.Persist?.Invoke(builder);
            s.CardSight?.Persist(builder);
            builder.WithEconomy(s.FamilyEconomy);
            await roles.CommitAsync(builder, "cd13b/pet-lottery", receipts =>
            {
                ItemSnapshotWritePlan.ApplyGuids(s.Items, (long[])receipts[itemReceipt]!);
                PublishAwardState(s);
            });
            return true;
        }
        bool escrowAward = false;
        try
        {
            if (!await roles.SaveAwardStateAsync(roleId, s.StagedProgress, s.DeliveredTaskState, s.TaskRuntimeState,
                s.Items, builder =>
                {
                    s.Persist?.Invoke(builder);
                    s.CardSight?.Persist(builder);
                    escrowAward = builder.HasEscrowEconomy;
                }, s.ExpectedDaily, s.NextDaily, s.PersistedTasks, s.ExpectedTasks != null)) return false;
        }
        catch (EconomyOutcomeUnknownException error) when (escrowAward) { QuarantineAward(s.Session, roleId, error); throw; }
        try { PublishAwardState(s); }
        catch (Exception error) when (escrowAward) { QuarantineAward(s.Session, roleId, error); throw; }
        return true;
    }

    private void QuarantineAward(GameSession session, long roleId, Exception error)
    {
        if (session.Owner is { } owner) owner.State = Ownership.RoleOwnerState.RecoveryRequired;
        session.CancelPendingCharacterIntents();
        session.AbortForRecovery();
        log.LogError(error, "Committed E03 award publication is unresolved; role {Role} requires recovery", roleId);
    }

    private async Task PresentAwardAsync(AwardStaging s)
    {
        var session = s.Session;
        if (s.OwnedAward?.CommandKind is "cd13e/grant" or "cd13e/gm-grant")
            foreach (var item in s.Received.Where(i => i.Guid > 0 && s.Items.Contains(i))) session.Profile.NewItems.Add(item.Guid);
        else
            foreach (var item in s.Items.Where(i => i.Guid > 0 && !s.OldItemGuids.Contains(i.Guid))) session.Profile.NewItems.Add(item.Guid);
        if (s.Expense.Count > 0 || s.Plan.Grants.Any(g => g.Entry is not (BaseExpItem or JobExpItem))) await SyncItemsAsync(session);
        if (s.BaseExp > 0 || s.JobExp > 0)
        {
            await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
            if (s.StagedProgress.Level != s.OldLevel || s.StagedProgress.JobLevel != s.OldJob)
            {
                await SendStatPlanAsync(session);
                await SendStatsAsync(session);
                foreach (var handler in ProgressHandlers.Handlers) await handler(session);
            }
        }
        // A wheel (rewardIndexes) waits for its notice to stop and unlock, even when nothing showable was granted.
        if (!s.SuppressRewardNotice && (s.Notification.Rewards.Count > 0 || s.RewardIndexes is { Count: > 0 }))
        {
            foreach (var (row, item) in s.GearRows)
                if (item.Guid > 0 && session.Items.Contains(item)) s.Notification.Rewards[row] = ItemInfo(item);
            s.Notification.RewardType.Add(s.ManifestType);
            if (s.RewardIndexes != null) s.Notification.RewardIndexes.Add(s.RewardIndexes);
            await session.SendGameAsync(GetRewardNotify, s.Notification);
        }
        if (s.Decomposed.DecomposeItemInfo.Count > 0)
            await session.SendGameAsync(EquipmentService.ItemAutoDecomposeNotice, s.Decomposed);
        foreach (var (entry, count) in s.Gained) foreach (var handler in ItemGained.Handlers) handler(session, entry, count);
        foreach (var handler in ItemsAwarded.Handlers) handler(session, s.Gained);
        foreach (var handler in StateChanged.Handlers) handler(session);
    }

    internal void ApplyExp(RoleProgress p, long baseExp, long jobExp)
    {
        p.Exp = checked(p.Exp + baseExp);
        for (long need; p.Level < MaxLevel && (need = BaseExpNeed(p.Level)) > 0 && p.Exp >= need;)
        {
            p.Exp -= need;
            p.Level++;
        }
        if (p.Level >= MaxLevel)
        {
            // Past the cap Base EXP feeds the Astral Level (CharacterService.Paragon) instead of being dropped.
            long overflow = p.Exp;
            p.Exp = 0;
            if (overflow > 0) AddParagonExp(p, overflow);
        }
        p.JobExp = checked(p.JobExp + jobExp);
        SkipFreeJobLevels(p);
        int maxJob = MaxJobLevel(p.Career);
        for (long need; p.JobLevel < maxJob && (need = JobExpNeed(p.Career, p.JobLevel)) > 0 && p.JobExp >= need;)
        {
            p.JobExp -= need;
            p.JobLevel++;
        }
        if (p.JobLevel >= maxJob) p.JobExp = Math.Min(p.JobExp, Math.Max(0, JobExpNeed(p.Career, p.JobLevel)));
    }

    internal void AddVirtualReward(Romsg.GetRewardNotify notification, int entry, long amount)
    {
        while (amount > 0)
        {
            int chunk = (int)Math.Min(amount, int.MaxValue);
            notification.Rewards.Add(ItemInfo(new InvItem { Entry = entry, Stack = chunk }));
            amount -= chunk;
        }
    }

    internal bool IsCurrency(int entry) => data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency;

    internal static RoleProgress CloneProgress(RoleProgress source)
    {
        var result = new RoleProgress
        {
            SourceRevision = source.SourceRevision, Career = source.Career, Level = source.Level, JobLevel = source.JobLevel,
            Exp = source.Exp, JobExp = source.JobExp, BonusPoints = source.BonusPoints, StarterKit = source.StarterKit,
            ParagonLevel = source.ParagonLevel, ParagonExp = source.ParagonExp
        };
        Array.Copy(source.Stats, result.Stats, source.Stats.Length);
        foreach (var (key, value) in source.Currency) result.Currency[key] = value;
        foreach (var (key, value) in source.Skills) result.Skills[key] = value;
        foreach (var (key, value) in source.SkillBar) result.SkillBar[key] = value;
        // Never saved, but a staged or preview copy must still know the skills its equipment grants: SkillService.Build
        // prunes the bar and the Auto-Battle lists against the copy, and dropped a card skill as unlearned (#571).
        foreach (var (key, value) in source.GrantedSkills) result.GrantedSkills[key] = value;
        return result;
    }

    internal static void CopyProgress(RoleProgress target, RoleProgress source)
    {
        target.SourceRevision = source.SourceRevision;
        target.Career = source.Career; target.Level = source.Level; target.JobLevel = source.JobLevel;
        target.Exp = source.Exp; target.JobExp = source.JobExp; target.BonusPoints = source.BonusPoints;
        target.StarterKit = source.StarterKit;
        target.ParagonLevel = source.ParagonLevel; target.ParagonExp = source.ParagonExp;
        Array.Copy(source.Stats, target.Stats, source.Stats.Length);
        target.Currency.Clear(); foreach (var (key, value) in source.Currency) target.Currency[key] = value;
        target.Skills.Clear(); foreach (var (key, value) in source.Skills) target.Skills[key] = value;
        target.SkillBar.Clear(); foreach (var (key, value) in source.SkillBar) target.SkillBar[key] = value;
    }

    internal static void CopyItem(InvItem target, InvItem source)
    {
        target.Entry = source.Entry; target.Stack = source.Stack; target.Pack = source.Pack;
        target.Sort = source.Sort; target.Grid = source.Grid; target.IsLocked = source.IsLocked;
        target.Attrs = source.Attrs; target.StringAttrs = source.StringAttrs;
        target.BindType = source.BindType; target.BindExpire = source.BindExpire;
        target.Enchantments = source.Enchantments;
    }

    /// <summary>Adds to the state and the database only; returns the instances that changed (new or restacked).</summary>
    internal List<InvItem> AddItemCore(GameSession session, int entry, int count, out bool currency,
        bool persist = true, bool notifyItemGained = true, int bind = 0)
    {
        var changed = new List<InvItem>();
        var row = data.Row("ItemConfig", entry)!.Value;
        currency = GameData.Int(row, "_iType") == ItemTypeCurrency;
        var p = session.Progress!;
        if (currency)
        {
            p.Currency[entry] = Math.Max(0, p.Currency.GetValueOrDefault(entry) + count);
            return changed;
        }
        bool firstSightCandidate = persist && notifyItemGained && count > 0;
        int gainedCount = count;
        int pack = ConfigPack(data, row);
        // (inferred) Even unappraised gear (some ItemConfig rows allow a stack of 2) gets one GUID per piece:
        // rolled stats, appraisal and enchantments belong to the instance and must never merge with another roll.
        int stackLimit = GameData.Int(row, "_iType") == ItemTypeEquip ? 1 : (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
        foreach (var item in session.Items.Where(i => CanStackGrant(i, entry, stackLimit, bind)))
        {
            if (count == 0) break;
            int add = Math.Min(count, stackLimit - item.Stack);
            item.Stack += add;
            count -= add;
            if (persist) itemStore.Update(item);
            changed.Add(item);
        }
        while (count > 0)
        {
            int stack = Math.Min(count, stackLimit);
            var item = new InvItem
            {
                Entry = entry, Stack = stack, Pack = pack, Sort = NextSort(session, pack),
                Attrs = TotemAttrFactory?.Invoke(entry) ?? EmblemAttrFactory?.Invoke(entry) ?? RollAttrs(entry), BindType = bind,
                BindExpire = AcquireExpiry(entry)
            };
            if (persist) itemStore.Insert(session.Role!.Id, item);
            session.Items.Add(item);
            changed.Add(item);
            count -= stack;
        }
        // The durable item row must precede legacy observers. If a process dies before a card
        // observer saves 5057, CardService reconciles first sight from the committed bag on login.
        if (firstSightCandidate) foreach (var handler in ItemGained.Handlers) handler(session, entry, gainedCount);
        return changed;
    }

    /// <summary>(inferred) Plain rewards stack only onto plain, unlocked instances with the same permanent binding.</summary>
    internal static bool CanStackGrant(InvItem item, int entry, int limit, int bind, long bindExpire = 0) =>
        item.Grid == 0 && !item.IsLocked && item.Entry == entry && item.Stack < limit
        && (item.Attrs?.Count ?? 0) == 0 && (item.StringAttrs?.Count ?? 0) == 0 && item.Enchantments.Count == 0
        && item.BindType == bind && item.BindExpire == bindExpire;
}
