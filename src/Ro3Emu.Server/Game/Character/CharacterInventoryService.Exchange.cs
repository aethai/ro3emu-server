using System.Globalization;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Maps;

using AttributedGood = Ro3Emu.Server.Game.CharacterService.AttributedGood;
using FirstCardSightPlan = Ro3Emu.Server.Game.CharacterService.FirstCardSightPlan;

namespace Ro3Emu.Server.Game;

public sealed partial class CharacterInventoryService
{
    // ---- bag: remove, pay and exchange ----

    /// <summary>Removes up to <paramref name="count"/> of an item id from the bag (GM).</summary>
    public async Task<string> RemoveItemAsync(GameSession session, int entry, int count)
    {
        using var _ = await LockAsync(session);
        var progress = CloneProgress(session.Progress!);
        if (data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency)
        {
            long previous = progress.Currency.GetValueOrDefault(entry);
            long next = Math.Max(0, previous - count);
            progress.Currency[entry] = next;
            await CommitCharacterAsync(session, "gm-remove", progress: progress,
                costs: previous > next ? [(entry, previous - next)] : [],
                goods: next > previous ? [(entry, checked((int)(next - previous)))] : []);
            await SyncItemsAsync(session);
            if (next != previous) foreach (var handler in StateChanged.Handlers) handler(session);
            return previous > next
                ? $"Removed {(previous - next).ToString("N0", CultureInfo.InvariantCulture)} {data.ItemName(entry)}; {next.ToString("N0", CultureInfo.InvariantCulture)} left."
                : $"{data.ItemName(entry)}: {next}";
        }
        var staged = session.Items.Select(i => i.Clone()).ToList();
        int stacks = 0;
        long consumed = 0;
        foreach (var item in staged.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).ToArray())
        {
            if (count <= 0) break;
            int take = Math.Min(count, item.Stack);
            count -= take;
            consumed += take;
            item.Stack -= take;
            if (item.Stack == 0) staged.Remove(item);
            stacks++;
        }
        await CommitCharacterAsync(session, "gm-remove", nextItems: staged,
            costs: consumed > 0 ? [(entry, consumed)] : []);
        await SyncItemsAsync(session);
        if (stacks > 0) foreach (var handler in StateChanged.Handlers) handler(session);
        return consumed > 0 ? $"Removed {consumed.ToString("N0", CultureInfo.InvariantCulture)} {data.ItemName(entry)}." : $"No {data.ItemName(entry)} to remove.";
    }

    /// <summary>Owned amount of a currency, or of an item in the bag (not worn or socketed).</summary>
    public long CountOf(GameSession session, int entry) =>
        data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency
            ? session.Progress!.Currency.GetValueOrDefault(entry)
            : session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).Sum(i => (long)i.Stack);

    /// <summary>
    /// Why <see cref="ExchangeAsync"/> would refuse a cost: a short currency is 192 "Insufficient Currency", a short bag
    /// item 154 "Not enough required items".
    /// </summary>
    public int ShortageOf(GameSession session, IReadOnlyList<(int Entry, long Count)> cost)
    {
        foreach (var g in cost.GroupBy(c => c.Entry))
        {
            long need = g.Sum(c => c.Count);
            if (need < 0) return ErrorCodes.Parameter;
            if (CountOf(session, g.Key) >= need) continue;
            return data.Row("ItemConfig", g.Key) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency ? ErrorCodes.CurrencyShort : ErrorCodes.ItemsShort;
        }
        return ErrorCodes.ItemsShort;
    }

    /// <summary>
    /// Bounded 83550 payment path. Caller holds <see cref="Lock"/>. Stage cost-only bag/currency changes, then commit
    /// those rows and the next 5050 blob in one SQLite transaction; publish in-memory balances only after commit.
    /// Other bag exchanges use the transaction in <see cref="ExchangeLockedAsync"/>.
    /// </summary>
    internal bool PayTalentLocked(GameSession session, IReadOnlyList<(int Entry, long Count)> cost, DBSeasonTalent next)
    {
        var need = cost.GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: g.Sum(c => c.Count))).ToList();
        if (need.Any(c => c.Count <= 0 || CountOf(session, c.Entry) < c.Count)) return false;
        var currency = new Dictionary<int, long>(session.Progress!.Currency);
        var stacks = new Dictionary<long, int>();
        var removed = new HashSet<long>();
        foreach (var (entry, count) in need)
        {
            if (data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency)
            {
                currency[entry] = currency.GetValueOrDefault(entry) - count;
                continue;
            }
            long left = count;
            foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).OrderBy(i => i.Stack))
            {
                if (left == 0) break;
                int take = (int)Math.Min(left, item.Stack);
                int rest = item.Stack - take;
                if (rest == 0) removed.Add(item.Guid);
                else stacks[item.Guid] = rest;
                left -= take;
            }
            if (left != 0) return false;
        }

        roles.CommitTalentPurchase(session.Role!.Id, next.ToByteArray(), currency, stacks, removed, itemStore, session.Progress);
        session.Progress.Currency.Clear();
        foreach (var (entry, count) in currency) session.Progress.Currency[entry] = count;
        foreach (var item in session.Items.ToList())
        {
            if (removed.Contains(item.Guid)) session.Items.Remove(item);
            else if (stacks.TryGetValue(item.Guid, out int stack)) item.Stack = stack;
        }
        return true;
    }

    /// <summary>
    /// Pays costs and grants goods in one transaction. With <see cref="ExchangeRequest.Persist"/>, caller state joins
    /// the same commit. Auxiliary ItemGained hooks and packets follow commit. Returns null without mutation for a short payment.
    /// </summary>
    public async Task<List<InvItem>?> ExchangeAsync(GameSession session, ExchangeRequest request)
    {
        using var _ = await LockAsync(session);
        return await ExchangeLockedAsync(session, request);
    }

    /// <summary>Shorthand for a plain cost/goods exchange without extra persistence hooks.</summary>
    public Task<List<InvItem>?> ExchangeAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost,
        IReadOnlyList<(int Entry, int Count)> goods) =>
        ExchangeAsync(session, new ExchangeRequest { Cost = cost, Goods = goods });

    /// <summary>
    /// ExchangeAsync with StateLock already held. Costs, grants and an optional caller mutation share one transaction;
    /// in-memory payment state is restored on failure, before any sync.
    /// Card operations reserve their target (<see cref="ExchangeRequest.ReservedGuid"/>) from material consumption and
    /// return refunded copies with their recorded binding (<see cref="ExchangeRequest.AttributedGoods"/>).
    /// Dungeon <see cref="ExchangeRequest.BoundGoods"/> use normal item generation while preserving DropGroupConfig binding.
    /// Virtual mail rewards 100/101 use <see cref="ExchangeRequest.BaseExp"/>/<see cref="ExchangeRequest.JobExp"/>:
    /// stage progression before payment, persist it with the mailbox claim, and publish only after commit.
    /// </summary>
    internal async Task<List<InvItem>?> ExchangeLockedAsync(GameSession session, ExchangeRequest request) =>
        await ExchangeOwnedCoreAsync(session, RequireOwnedRole(session), request);

    private sealed class ExchangeStaging
    {
        public required GameSession Session;
        public required ExchangeRequest Request;
        public required Ownership.AttachmentLease? Attachment;
        public required CharacterService.LifeItemExchangeScope? LifeUse;
        public required List<(int Entry, long Count)> Need;
        public required List<(InvItem Item, int Count)> Picked;
        public required RoleProgress NextProgress;
        public required RoleProgress LiveProgress;
        public required List<InvItem> OriginalItems;
        public required InvItem[] OriginalItemReferences;
        public required long[] OriginalNewItems;
        public required RoleProgress BeforeProgress;
        public required InvItem[] BeforeItems;
        public required List<InvItem> StagedItems;
        public required RoleProgress StagedProgress;
        public required (int Level, long Exp, int JobLevel, long JobExp) OriginalProgress;
        public required Romsg.GetRewardNotify Reward;
        public readonly List<InvItem> Granted = [];
        public readonly List<ShownGood> Shown = [];
        public readonly List<(int Entry, int Count)> PostCommitHooks = [];
        public ItemAutoDecomposeTipsNtf Decomposed = new();
        public RoleItemPlanBuilder Builder = null!;
        public int ItemReceipt;
        public FirstCardSightPlan? CardSight;
        public DBEquipInfo? PrunedPlans;
    }

    /// <summary>Checked owned Core for the complete payment, grant and optional caller plan.</summary>
    private async Task<List<InvItem>?> ExchangeOwnedCoreAsync(GameSession session, Ownership.OwnerContext context,
        ExchangeRequest request)
    {
        context.AssertRole(session.Role!.Id);
        context.BindPresentation(session);
        var lifeUse = LifeItemExchange.Value;
        if (lifeUse is null || !lifeUse.Active || !ReferenceEquals(lifeUse.Session, session) || lifeUse.Committed) lifeUse = null;
        if (request.BaseExp < 0 || request.JobExp < 0) throw new ArgumentOutOfRangeException(nameof(request));
        var nextProgress = CloneProgress(session.Progress!);
        if (request.BaseExp > 0 || request.JobExp > 0) ApplyExp(nextProgress, request.BaseExp, request.JobExp);
        if (!TryValidateExchangeCosts(session, request, out var need, out var picked)) return null;
        if (request.ItemUpdate is { } update && !request.AsyncCommit)
        {
            ApplyItemExchange(session, need, picked, update);
            if (request.Sync) await SyncItemsAsync(session);
            return [];
        }
        var p = session.Progress!;
        var originalItems = session.Items;
        var staging = new ExchangeStaging
        {
            Session = session, Request = request, Attachment = session.Attachment, LifeUse = lifeUse,
            Need = need, Picked = picked, NextProgress = nextProgress, LiveProgress = p,
            OriginalItems = originalItems, OriginalItemReferences = [.. originalItems],
            OriginalNewItems = [.. session.Profile.NewItems],
            BeforeProgress = CloneProgress(p), BeforeItems = [.. originalItems.Select(item => item.Clone())],
            StagedItems = [.. originalItems.Select(item => item.Clone())], StagedProgress = CloneProgress(p),
            OriginalProgress = (p.Level, p.Exp, p.JobLevel, p.JobExp),
            Reward = new Romsg.GetRewardNotify { TargetUid = session.Role!.Id },
        };
        StageExchangeMutation(staging);
        await CommitExchangeAsync(staging);
        if (request.RewardSettlement?.DeferPresentation == true)
            request.RewardSettlement.Presentation = () => PresentExchangeAsync(staging);
        else await PresentExchangeAsync(staging);
        return staging.Granted;
    }

    private bool TryValidateExchangeCosts(GameSession session, ExchangeRequest request,
        out List<(int Entry, long Count)> need, out List<(InvItem Item, int Count)> picked)
    {
        long reservedGuid = request.ReservedGuid;
        var needList = request.Cost.GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: g.Sum(c => c.Count))).ToList();
        var pickedList = new List<(InvItem Item, int Count)>();
        need = needList;
        picked = pickedList;
        long Available(int entry) => CountOf(session, entry) -
            (session.Items.Any(i => i.Guid == reservedGuid && i.Grid == 0 && i.Entry == entry
                && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)) ? 1 : 0);
        if (needList.Any(c => c.Count < 0 || Available(c.Entry) < c.Count)) return false;
        foreach (var (guid, count) in request.Instances ?? new Dictionary<long, int>())
        {
            if (session.Items.FirstOrDefault(i => i.Guid == guid && i.Grid == 0) is not { } item
                || EmblemService.IsReserved(session, item) || TotemService.IsReserved(session, item)
                || count < 1 || count > item.Stack - (guid == reservedGuid ? 1 : 0)) return false;
            pickedList.Add((item, count));
        }
        // Explicit GUID costs and entry costs share the same bag: reserve both before consuming either.
        if (needList.Any(c => Available(c.Entry) - pickedList.Where(p => p.Item.Entry == c.Entry).Sum(p => (long)p.Count) < c.Count))
            return false;
        if (request.ItemUpdate is { } update)
        {
            if ((!request.AsyncCommit && request.Persist != null) || request.Goods.Count != 0
                || request.AttributedGoods is { Count: > 0 } || request.BoundGoods is { Count: > 0 }
                || request.ExactGoods is { Count: > 0 } || request.CurrencyGoods is { Count: > 0 }
                || request.BaseExp != 0 || request.JobExp != 0
                || needList.Any(c => data.Row("ItemConfig", c.Entry) is { } r && GameData.Int(r, "_iType") == ItemTypeCurrency))
                throw new InvalidOperationException("An item update requires an item-only exchange.");
            if (!session.Items.Contains(update.Item) || pickedList.Any(selected => selected.Item.Guid == update.Item.Guid)
                || needList.Any(c => c.Entry == update.Item.Entry && c.Count > 0)) return false;
        }
        return true;
    }

    private void StageExchangeMutation(ExchangeStaging s)
    {
        var session = s.Session;
        var request = s.Request;
        var stagedPicked = s.Picked.Select(choice =>
            (s.StagedItems.Single(item => item.Guid == choice.Item.Guid), choice.Count)).ToList();
        session.Items = s.StagedItems;
        session.Progress = s.StagedProgress;
        try
        {
            PayExchange(session, s.Need, stagedPicked, request.ReservedGuid, persist: false);
            request.StageItems?.Invoke(s.StagedItems);
            if (request.ItemUpdate is { } stagedUpdate)
                s.StagedItems.Single(item => item.Guid == stagedUpdate.Item.Guid).Attrs = new(stagedUpdate.Attrs);
            StageExchangeGoods(s);
            if (request.RewardSettlement?.AutoDecompose == true)
            {
                s.Decomposed = StageAutoDecomposeAward(session, s.StagedItems, s.StagedProgress, s.Granted,
                    s.BeforeItems.ToDictionary(i => i.Guid, i => i.Stack), s.PostCommitHooks);
                s.Granted.RemoveAll(item => !s.StagedItems.Contains(item));
            }
            // Tower clear goods retain their existing non-auto-decompose rule but join this kill's one commit.
            request.RewardSettlement?.BeforeAdditionalGoods?.Invoke();
            foreach (var (entry, count) in request.RewardSettlement?.AdditionalGoods ?? [])
            {
                foreach (var item in AddItemCore(session, entry, count, out _, persist: false, notifyItemGained: false))
                    s.Granted.Add(item);
                if (!IsCurrency(entry)) s.PostCommitHooks.Add((entry, count));
            }
            request.StageAfterGoods?.Invoke(s.StagedItems);
            var builder = roles.NewTransaction();
            s.ItemReceipt = builder.ItemDelta(session.Role!.Id, s.BeforeItems, s.StagedItems);
            builder.Progress(session.Role.Id, s.StagedProgress);
            request.Persist?.Invoke(builder);
            s.LifeUse?.Persist(builder);
            s.CardSight = StageFirstCardSight?.Invoke(session, [.. s.PostCommitHooks.Select(g => g.Entry)],
                request.RewardSettlement?.LibraryMonster ?? 0);
            s.CardSight?.Persist(builder);
            if (builder.HasCharacterFamilyEconomy && (s.PrunedPlans = PrunedEquipmentPlans(session, s.StagedItems)) != null)
                builder.RoleBlob(session.Role.Id, "equip_plans", s.PrunedPlans.ToByteArray());
            request.RewardSettlement?.EnlistAfterCards?.Invoke(builder);
            if (request.RewardSettlement != null && request.Economy != null)
                throw new InvalidOperationException("Reward settlement already supplies its economy envelope.");
            if (request.Economy != null) builder.WithEconomy(request.Economy);
            if (request.RewardSettlement != null)
                builder.WithEconomy(RewardMutation(session, request.RewardSettlement, s.BeforeProgress, s.StagedProgress,
                    s.Need, [.. request.Goods, .. (request.BoundGoods ?? []).Select(g => (g.Entry, g.Count)),
                        .. request.RewardSettlement.AdditionalGoods],
                    request.BaseExp, request.JobExp, s.Decomposed));
            s.Builder = builder;
        }
        finally
        {
            session.Items = s.OriginalItems;
            session.Progress = s.LiveProgress;
        }
    }

    private void StageExchangeGoods(ExchangeStaging s)
    {
        var session = s.Session;
        var request = s.Request;
        var regularGoods = request.Goods.Select(g => (g.Entry, g.Count, Bind: 0))
            .Concat(request.BoundGoods ?? []).Where(g => g.Count > 0);
        foreach (var (entry, count, bind) in regularGoods)
        {
            var pieces = AddItemCore(session, entry, count, out bool currency, persist: false, notifyItemGained: false, bind: bind);
            s.Granted.AddRange(pieces);
            if (!currency) s.PostCommitHooks.Add((entry, count));
            s.Shown.Add(new ShownGood(entry, count, bind, IsEquipment(entry) ? pieces : []));
            s.Reward.RewardType.Add(1);
        }
        foreach (var grant in request.AttributedGoods ?? [])
        {
            foreach (var item in AddAttributedCore(session, grant, persist: false)) s.Granted.Add(item);
            if (grant.Count > 0 && data.Row("ItemConfig", grant.Entry) is { } row && GameData.Int(row, "_iType") != ItemTypeCurrency)
                s.PostCommitHooks.Add((grant.Entry, grant.Count));
        }
        foreach (var source in request.ExactGoods ?? [])
        {
            var row = data.Row("ItemConfig", source.Entry)!.Value;
            int pack = ConfigPack(data, row);
            var item = source.Clone();
            item.Guid = 0; // (inferred) claimed copy has a fresh bag identity.
            item.Grid = 0;
            item.Pack = pack;
            item.Sort = NextSort(session, pack);
            session.Items.Add(item);
            s.Granted.Add(item);
            s.PostCommitHooks.Add((item.Entry, item.Stack));
            s.Shown.Add(new ShownGood(item.Entry, item.Stack, item.BindType, [item]));
        }
        foreach (var (entry, amount) in request.CurrencyGoods ?? new Dictionary<int, long>())
            s.StagedProgress.Currency[entry] = checked(s.StagedProgress.Currency.GetValueOrDefault(entry) + amount);
        if (request.BaseExp > 0 || request.JobExp > 0)
        {
            (s.StagedProgress.Level, s.StagedProgress.Exp, s.StagedProgress.JobLevel, s.StagedProgress.JobExp) =
                (s.NextProgress.Level, s.NextProgress.Exp, s.NextProgress.JobLevel, s.NextProgress.JobExp);
            // Past the level cap the EXP fed the Astral Level of the staged copy only (ApplyExp): a kill at 99 gave none.
            (s.StagedProgress.ParagonLevel, s.StagedProgress.ParagonExp) = (s.NextProgress.ParagonLevel, s.NextProgress.ParagonExp);
        }
    }

    private void PublishExchangeSnapshot(ExchangeStaging s, object?[] receipts)
    {
        var session = s.Session;
        var request = s.Request;
        try
        {
            ItemSnapshotWritePlan.ApplyGuids(s.StagedItems, (long[])receipts[s.ItemReceipt]!);
            // Apply the complete committed snapshot without replacing surviving item objects held by callers.
            var surviving = s.OriginalItems.ToDictionary(item => item.Guid);
            var mapped = new Dictionary<InvItem, InvItem>(ReferenceEqualityComparer.Instance);
            s.OriginalItems.Clear();
            foreach (var item in s.StagedItems)
            {
                if (surviving.TryGetValue(item.Guid, out var live))
                {
                    CopyItem(live, item);
                    mapped[item] = live;
                    s.OriginalItems.Add(live);
                }
                else { mapped[item] = item; s.OriginalItems.Add(item); }
            }
            for (int i = 0; i < s.Granted.Count; i++) s.Granted[i] = mapped[s.Granted[i]];
            CopyProgress(s.LiveProgress, s.StagedProgress);
            if (request.BaseExp > 0 || request.JobExp > 0)
                foreach (var credited in ExpCredited.Handlers) credited(session, request.BaseExp, request.JobExp);
            if (request.RewardSettlement != null) request.RewardSettlement.Committed = true;
            foreach (var item in s.Granted) session.Profile.NewItems.Add(item.Guid);
            s.CardSight?.Apply();
            if (s.PrunedPlans != null) session.Profile.EquipmentPlans = s.PrunedPlans;
            if (s.LifeUse != null)
            {
                s.LifeUse.Committed = true;
                s.LifeUse.Publish();
            }
            // Publish the owner's committed in-memory state before any notification can yield or fail.
            request.Committed?.Invoke();
            request.AppliedBeforeNotify?.Invoke();
        }
        catch
        {
            // A failed Apply cannot undo SQL. Restore the previous bag/progress view; the async
            // commit adapter quarantines the owner before any other operation can use that view.
            s.OriginalItems.Clear();
            for (int i = 0; i < s.OriginalItemReferences.Length; i++)
            {
                CopyItem(s.OriginalItemReferences[i], s.BeforeItems[i]);
                s.OriginalItems.Add(s.OriginalItemReferences[i]);
            }
            CopyProgress(s.LiveProgress, s.BeforeProgress);
            session.Profile.NewItems.Clear();
            foreach (long guid in s.OriginalNewItems) session.Profile.NewItems.Add(guid);
            throw;
        }
    }

    private async Task CommitExchangeAsync(ExchangeStaging s)
    {
        var session = s.Session;
        var request = s.Request;
        try
        {
            // The specialized item-use scope also carries Mount/Wardrobe cooldowns. Those
            // converted family envelopes enlist bag_state; ordinary Life exchanges stay guarded.
            if (request.AsyncCommit && s.LifeUse != null && !s.Builder.HasAsyncFamilyEconomy)
                throw new InvalidOperationException("Life exchange is not an asynchronous owner family.");
            // Converted family envelopes, reward settlements, and owned grants use owned admission. Shops opt the original
            // NPC receipt shape into that path with asyncCommit; other legacy callers retain the sync adapter.
            if (s.Builder.HasEconomy && !s.Builder.HasAsyncFamilyEconomy && !request.AsyncCommit
                && request.RewardSettlement == null && request.Economy == null)
            {
                var receipts = roles.Commit(s.Builder, "character/exchange");
                try { PublishExchangeSnapshot(s, receipts); }
                catch (Exception error)
                {
                    // SQL has committed. The restored live snapshot must not admit another command or cleanup save.
                    if (session.Owner is { } owner) owner.State = Ownership.RoleOwnerState.RecoveryRequired;
                    session.CancelPendingCharacterIntents();
                    session.AbortForRecovery();
                    log.LogError(error, "Committed NPC purchase publication failed; role {Role} requires recovery", session.Role!.Id);
                    throw;
                }
            }
            else await roles.CommitAsync(s.Builder, "character/exchange", receipts => PublishExchangeSnapshot(s, receipts));
        }
        catch (EconomyOutcomeUnknownException error)
        {
            // Economy invariant: neither snapshot is safe to publish/save if durable receipt recovery is unavailable.
            if (session.Owner is { } owner) owner.State = Ownership.RoleOwnerState.RecoveryRequired;
            session.CancelPendingCharacterIntents();
            session.AbortForRecovery();
            log.LogError(error, "NPC purchase {Operation} has an unresolved durable outcome; role {Role} requires recovery",
                error.OperationId, session.Role!.Id);
            throw;
        }
    }

    private async Task PresentExchangeAsync(ExchangeStaging s)
    {
        var session = s.Session;
        var request = s.Request;
        if (s.Attachment?.Cancelled == true || !ReferenceEquals(session.Attachment, s.Attachment)) return;
        // A hook may persist auxiliary state on another connection. A hook failure cannot undo the committed grant.
        foreach (var (entry, count) in s.PostCommitHooks)
            foreach (var handler in ItemGained.Handlers)
                try { handler(session, entry, count); }
                catch (Exception e) { log.LogError(e, "ItemGained hook failed after committed exchange for item {Entry}", entry); }
        if (request.Sync) await SyncItemsAsync(session); // else the caller sends 5009 (ItemsChangedAsync) when it fits
        if (s.Decomposed.DecomposeItemInfo.Count > 0)
            await session.SendGameAsync(EquipmentService.ItemAutoDecomposeNotice, s.Decomposed);
        // Built after commit: only then does a new piece carry the bag instance id the quick-equip offer needs.
        foreach (var good in s.Shown) s.Reward.Rewards.AddRange(GainedInfos(s.Granted, good));
        foreach (var (entry, amount) in request.CurrencyGoods ?? new Dictionary<int, long>()) AddVirtualReward(s.Reward, entry, amount);
        if (s.Reward.Rewards.Count > 0 && s.Reward.RewardType.Count == 0) s.Reward.RewardType.Add(1);
        if (request.BaseExp > 0 || request.JobExp > 0)
        {
            if (request.RewardSettlement?.CommandKind == "monster-kill")
            {
                var floatReward = new Romsg.GetRewardNotify { TargetUid = session.Role!.Id, RewardType = { 1 } };
                AddVirtualReward(floatReward, BaseExpItem, request.BaseExp);
                AddVirtualReward(floatReward, JobExpItem, request.JobExp);
                await session.SendGameAsync(GetRewardNotify, floatReward);
            }
            if (s.LiveProgress.Level != s.OriginalProgress.Level || s.LiveProgress.JobLevel != s.OriginalProgress.JobLevel)
                await AfterLevelChangeAsync(session, persist: false);
            else await SyncAsync(session, RoleBaseInfoSync, BuildBaseInfo(session));
            AddVirtualReward(s.Reward, BaseExpItem, request.BaseExp);
            AddVirtualReward(s.Reward, JobExpItem, request.JobExp);
            if (s.Reward.RewardType.Count == 0) s.Reward.RewardType.Add(1);
        }
        if (request.Popup && s.Reward.Rewards.Count > 0) await session.SendGameAsync(GetRewardNotify, s.Reward);
        // LifeJob publishes its committed life/restriction state after Exchange returns. Its ItemsChangedAsync
        // runs this first notification once those fields agree with the already committed bag and cooldown.
        if (s.LifeUse != null) s.LifeUse.PendingStateChanged = [.. StateChanged.Handlers];
        else if (!QuietKillReward(s)) foreach (var handler in StateChanged.Handlers) handler(session);
        else if (s.LiveProgress.ParagonLevel != s.BeforeProgress.ParagonLevel || s.LiveProgress.ParagonExp != s.BeforeProgress.ParagonExp)
            foreach (var handler in character.AstralChanged.Handlers) handler(session);
    }

    /// <summary>
    /// A monster kill's reward that changed nothing a <see cref="CharacterService.StateChanged"/> listener reads beyond the
    /// Astral Level (<see cref="CharacterService.AstralChanged"/>): no level or job level change, no equipment plan pruned, nothing auto-decomposed, and no card or watched entry
    /// (<see cref="CharacterService.WatchStateEntries"/>) among the goods. Every kill raised the event, so eleven services
    /// re-read the role (quests, activities, achievements, power...) about nine times a second under load (#484). A level
    /// change still reaches them through ProgressHandlers and the event (AfterLevelChangeAsync above).
    /// </summary>
    private bool QuietKillReward(ExchangeStaging s)
    {
        var request = s.Request;
        if (request.RewardSettlement?.CommandKind != "monster-kill" || s.PrunedPlans != null
            || s.Decomposed.DecomposeItemInfo.Count > 0 || request.ItemUpdate != null || request.StageItems != null
            || request.StageAfterGoods != null || s.LiveProgress.Level != s.OriginalProgress.Level
            || s.LiveProgress.JobLevel != s.OriginalProgress.JobLevel)
            return false;
        foreach (var item in s.Granted) if (Watched(item.Entry)) return false;
        foreach (var (entry, _) in request.Goods) if (Watched(entry)) return false;
        foreach (var (entry, _, _) in request.BoundGoods ?? []) if (Watched(entry)) return false;
        foreach (var item in request.ExactGoods ?? []) if (Watched(item.Entry)) return false;
        foreach (var good in request.AttributedGoods ?? []) if (Watched(good.Entry)) return false;
        if (request.CurrencyGoods is { } currencies) foreach (var (entry, _) in currencies) if (Watched(entry)) return false;
        // A currency among these lands in Progress.Currency, not in Granted.
        foreach (var (entry, _) in request.RewardSettlement.AdditionalGoods) if (Watched(entry)) return false;
        foreach (var (entry, _) in s.Need) if (Watched(entry)) return false;
        return true;

        // AchievementService's card quality reads cards in the bag too.
        bool Watched(int entry) => character.WatchesStateEntry(entry) || IsCard(entry);
    }

    /// <summary>One granted good of an exchange's 31322 notice, with the bag pieces that describe it.</summary>
    private readonly record struct ShownGood(int Entry, int Count, int Bind, List<InvItem> Pieces);

    /// <summary>
    /// A good's 31322 entries, as the drop notice's (RewardService.DropInfos): the client hands ManifestTypeFloat infos
    /// to FastUseManager.AddFastUse (LC_TipHelper.PopRewardTip), and LC_EquipmentHelper.CanFastEquip offers to wear a
    /// piece only when it has an instance id, an appraised kEquipInfo and an iScore. So each committed piece is
    /// described as the bag item itself; the rest (stackables, pieces auto-decomposed) stays entry and count.
    /// </summary>
    private IEnumerable<ItemBaseInfo> GainedInfos(List<InvItem> granted, ShownGood good)
    {
        int left = good.Count;
        foreach (var piece in good.Pieces.Where(piece => granted.Any(item => ReferenceEquals(item, piece))))
        {
            left -= piece.Stack;
            yield return ItemInfo(piece);
        }
        if (left > 0) yield return ItemInfo(new InvItem { Entry = good.Entry, Stack = left, BindType = good.Bind });
    }

    private void PayExchange(GameSession session, List<(int Entry, long Count)> need, List<(InvItem Item, int Count)> picked,
        long reservedGuid = 0, bool persist = true)
    {
        var changed = new List<InvItem>();
        var removed = new List<InvItem>();
        foreach (var (item, count) in picked) TakeFromStack(session, item, count, changed, removed, persist: persist);
        foreach (var (entry, count) in need)
        {
            if (count == 0) continue;
            if (data.Row("ItemConfig", entry) is { } row && GameData.Int(row, "_iType") == ItemTypeCurrency)
            {
                session.Progress!.Currency[entry] -= count;
                continue;
            }
            long left = count;
            foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i))
                .OrderBy(i => i.Guid == reservedGuid).ThenBy(i => i.Stack).ToList())
            {
                if (left <= 0) break;
                int take = (int)Math.Min(left, item.Stack - (item.Guid == reservedGuid ? 1 : 0));
                if (take == 0) continue;
                left -= take;
                TakeFromStack(session, item, take, changed, removed, persist: persist);
            }
            if (left != 0) throw new InvalidOperationException("Exchange cost changed after validation.");
        }
    }

    /// <summary>Return paid copies with their recorded bind/type, stacking only identical base copies.</summary>
    private List<InvItem> AddAttributedCore(GameSession session, AttributedGood grant, bool persist = true)
    {
        if (grant.Count <= 0) return [];
        var row = data.Row("ItemConfig", grant.Entry)!.Value;
        if (GameData.Int(row, "_iType") == ItemTypeCurrency)
        {
            session.Progress!.Currency[grant.Entry] = session.Progress.Currency.GetValueOrDefault(grant.Entry) + grant.Count;
            return [];
        }
        int pack = ConfigPack(data, row);
        int limit = (int)Math.Max(1, GameData.Int(row, "_iStackLimit"));
        int remaining = grant.Count;
        var changed = new List<InvItem>();
        foreach (var item in session.Items.Where(i => i.Grid == 0 && !i.IsLocked && i.Enchantments.Count == 0
            && i.Entry == grant.Entry && i.BindType == grant.BindType
            && i.BindExpire == grant.BindExpire && i.Stack < limit && (i.StringAttrs?.Count ?? 0) == 0
            && (i.Attrs?.GetValueOrDefault(530) ?? 0) == grant.CardType
            && (i.Attrs == null || i.Attrs.All(a => (a.Key is 501 or 502 or 530) && a.Value == (a.Key == 530 ? grant.CardType : 0)))))
        {
            int add = Math.Min(remaining, limit - item.Stack);
            item.Stack += add;
            remaining -= add;
            if (persist) itemStore.Update(item);
            changed.Add(item);
            if (remaining == 0) break;
        }
        while (remaining > 0)
        {
            int count = Math.Min(remaining, limit);
            var item = new InvItem
            {
                Entry = grant.Entry, Stack = count, Pack = pack, Sort = NextSort(session, pack),
                BindType = grant.BindType, BindExpire = grant.BindExpire,
                Attrs = grant.CardType == 0 ? null : new Dictionary<int, long>
                { [501] = 0, [502] = 0, [530] = grant.CardType },
            };
            if (persist) itemStore.Insert(session.Role!.Id, item);
            session.Items.Add(item);
            changed.Add(item);
            remaining -= count;
        }
        return changed;
    }

    private void ApplyItemExchange(GameSession session, IReadOnlyList<(int Entry, long Count)> cost,
        IReadOnlyList<(InvItem Item, int Count)> picked, (InvItem Item, Dictionary<int, long> Attrs) update)
    {
        var taken = picked.ToDictionary(p => p.Item.Guid, p => p.Count);
        foreach (var (entry, count) in cost)
        {
            long left = count;
            foreach (var item in session.Items.Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i)).OrderBy(i => i.Stack))
            {
                if (left == 0) break;
                int used = taken.GetValueOrDefault(item.Guid);
                int take = (int)Math.Min(left, item.Stack - used);
                if (take == 0) continue;
                taken[item.Guid] = used + take;
                left -= take;
            }
        }
        var changed = new List<InvItem>();
        foreach (var (guid, count) in taken)
        {
            var item = session.Items.Single(i => i.Guid == guid);
            changed.Add(Copy(item, item.Stack - count, item.Attrs));
        }
        changed.Add(Copy(update.Item, update.Item.Stack, update.Attrs));
        itemStore.ApplyChanges(session.Role!.Id, changed);
        // No session mutation occurs if any SQL statement or the commit fails.
        foreach (var item in changed)
        {
            var original = session.Items.Single(i => i.Guid == item.Guid);
            if (item.Stack == 0) session.Items.Remove(original);
            else { original.Stack = item.Stack; original.Attrs = item.Attrs; }
        }

        static InvItem Copy(InvItem item, int stack, Dictionary<int, long>? attrs)
        {
            var copy = item.Clone();
            copy.Stack = stack;
            copy.Attrs = attrs;
            return copy;
        }
    }

    /// <summary>
    /// Pet exchange under the caller's StateLock: stage on copied items, then atomically persist the bag, currencies
    /// and next DBPetInfo before publishing any packets (inferred transaction invariant).
    /// The ordinary exchange overload retains its existing locking/sync behavior. Pet goods are configured items.
    /// </summary>
    internal Task<List<InvItem>?> ExchangeAsync(GameSession session, IReadOnlyList<(int Entry, long Count)> cost,
        IReadOnlyList<(int Entry, int Count)> goods, DBPetInfo nextPets)
    {
        var need = cost.GroupBy(c => c.Entry).Select(g => (Entry: g.Key, Count: checked(g.Sum(c => c.Count)))).ToList();
        if (need.Any(c => c.Count < 0 || data.Row("ItemConfig", c.Entry) == null || CountOf(session, c.Entry) < c.Count)
            || goods.Any(g => g.Count <= 0 || data.Row("ItemConfig", g.Entry) == null))
            return Task.FromResult<List<InvItem>?>(null);
        var before = session.Items;
        var currency = session.Progress!.Currency.ToDictionary();
        session.Items = [.. before.Select(i => i.Clone())]; // every per-instance field survives the staging
        var granted = new List<InvItem>();
        try
        {
            foreach (var (entry, count) in need)
            {
                if (GameData.Int(data.Row("ItemConfig", entry)!.Value, "_iType") == ItemTypeCurrency)
                    session.Progress.Currency[entry] = session.Progress.Currency.GetValueOrDefault(entry) - count;
                else
                {
                    long left = count;
                    var stacks = session.Items
                        .Where(i => i.Grid == 0 && i.Entry == entry && !EmblemService.IsReserved(session, i) && !TotemService.IsReserved(session, i))
                        .OrderBy(i => i.Stack).ToList();
                    foreach (var item in stacks)
                    {
                        if (left == 0) break;
                        int take = (int)Math.Min(left, item.Stack);
                        TakeFromStack(session, item, take, [], [], persist: false);
                        left -= take;
                    }
                }
            }
            foreach (var (entry, count) in goods)
                granted.AddRange(AddItemCore(session, entry, count, out _, persist: false));
            roles.SavePetExchange(session.Role!.Id, nextPets.ToByteArray(), session.Progress.Currency, before, session.Items, session.Progress);
        }
        catch
        {
            session.Items = before;
            session.Progress.Currency.Clear();
            foreach (var (id, count) in currency) session.Progress.Currency[id] = count;
            throw;
        }
        session.Pets = nextPets;
        foreach (var item in granted) session.Profile.NewItems.Add(item.Guid);
        foreach (var (entry, count) in goods)
            foreach (var handler in ItemGained.Handlers) handler(session, entry, count);
        return Task.FromResult<List<InvItem>?>(granted);
    }
}
