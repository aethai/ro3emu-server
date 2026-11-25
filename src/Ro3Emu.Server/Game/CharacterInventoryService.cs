using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Bag, equipment, item use, exchanges, and economy envelopes extracted from <see cref="CharacterService"/>.
/// </summary>
public sealed partial class CharacterInventoryService(
    CharacterService character,
    RoleStore roles,
    ItemStore itemStore,
    GameData data,
    SeasonService seasons,
    ILogger<CharacterInventoryService> log)
{
    private const uint RoleBaseInfoSync = CharacterService.RoleBaseInfoSync;
    private const uint ItemsSync = CharacterService.ItemsSync;
    private const uint EquipSync = CharacterService.EquipSync;
    private const uint EntityBaseChange = CharacterService.EntityBaseChange;
    private const uint GetRewardNotify = CharacterService.GetRewardNotify;
    private const uint ItemUse = CharacterService.ItemUse;
    private const uint PackItems = CharacterService.PackItems;
    private const uint EquipItemOpr = CharacterService.EquipItemOpr;
    private const uint ItemsCdNotify = CharacterService.ItemsCdNotify;
    private const int ItemTypeCurrency = CharacterService.ItemTypeCurrency;
    private const int ItemTypeEquip = CharacterService.ItemTypeEquip;
    private const int BaseExpItem = CharacterService.BaseExpItem;
    private const int JobExpItem = CharacterService.JobExpItem;
    private const int AttrAppraise = CharacterService.AttrAppraise;
    private const int AttrSetUpSource = CharacterService.AttrSetUpSource;
    private const int EquipInfoSlots = CharacterService.EquipInfoSlots;
    private const int MaxHp = CharacterService.MaxHp;
    private const int MaxSp = CharacterService.MaxSp;
    private const int Hp = CharacterService.Hp;
    private const int Sp = CharacterService.Sp;
    private const int MaxStat = CharacterService.MaxStat;
    private const int StatPlanCount = CharacterService.StatPlanCount;

    private Func<int, Dictionary<int, long>?>? EmblemAttrFactory => character.EmblemAttrFactory;
    private OrderedHook<Func<GameSession, ItemUseReq, InvItem, JsonElement, int, Task<int?>>> SpecialItemUse => character.SpecialItemUse;
    private SyncHook<GameSession, int, int> ItemGained => character.ItemGained;
    private SyncHook<GameSession, int, int> ItemUsed => character.ItemUsed;
    private SyncHook<GameSession> StateChanged => character.StateChanged;
    private AsyncHook<GameSession> WornGearChanged => character.WornGearChanged;
    private AsyncHook<GameSession> ProgressHandlers => character.ProgressHandlers;
    private SyncHook<GameSession, long, long> ExpCredited => character.ExpCredited;
    private Func<GameSession, IReadOnlyList<int>, int, CharacterService.FirstCardSightPlan?>? StageFirstCardSight => character.StageFirstCardSight;
    private Func<GameSession, InvItem, JsonElement, int, Task<int?>>? LifeItemUse => character.LifeItemUse;
    private Func<GameSession, int, int, Task>? GiveBuff => character.GiveBuff;
    private Func<GameSession, bool>? IsItemUseForbidden => character.IsItemUseForbidden;
    private Func<GameSession, long, Task<bool>>? WingJump => character.WingJump;
    private Func<GameSession, InvItem, IReadOnlyList<(int Entry, long Count)>>? AutoDecomposeReturns => character.AutoDecomposeReturns;
    private Func<GameSession, ItemUseReq, Action<RoleItemPlanBuilder>, Action, Task<int>>? StartTreasureMap => character.StartTreasureMap;
    private TimeProvider UseClock => character.UseClock;

    private static AsyncLocal<CharacterService.LifeItemExchangeScope?> LifeItemExchange => CharacterService.LifeItemExchange;

    private static Task SyncAsync(GameSession session, uint msgId, IMessage now, Action<IMessage>? prepare = null) =>
        CharacterService.SyncAsync(session, msgId, now, prepare);
    private Task SyncItemsAsync(GameSession session, Func<bool>? stillOwner = null) => character.SyncItemsAsync(session, stillOwner);
    private Task SendStatsAsync(GameSession session, bool showChange = false) => character.SendStatsAsync(session, showChange);
    private Task SendStatPlanAsync(GameSession session) => character.SendStatPlanAsync(session);
    private void Refresh(GameSession session) => character.Refresh(session);
    private StatSheet Stats(GameSession session) => character.Stats(session);
    private Task SetHpSpAsync(GameSession session, long? hp, long? sp) => character.SetHpSpAsync(session, hp, sp);
    private Task ToSelfAndViewersAsync(GameSession session, uint msgId, IMessage body) => character.ToSelfAndViewersAsync(session, msgId, body);
    private static long[] EquipInfo(GameSession session) => CharacterStatsService.EquipInfo(session);
    private static RoleBaseInfo BuildBaseInfo(GameSession session) => CharacterService.BuildBaseInfo(session);
    private DBEquipInfo BuildEquip(GameSession session) => character.BuildEquip(session);
    private DBItemsInfo BuildItems(GameSession session) => character.BuildItems(session);
    private static bool ResendItemsWholeIfNeeded(GameSession session, DBItemsInfo now) => CharacterService.ResendItemsWholeIfNeeded(session, now);
    private static Task<IDisposable> LockAsync(GameSession session, IReadOnlyList<Ownership.ReservationKey>? additionalKeys = null,
        CancellationToken cancellation = default) => CharacterService.LockAsync(session, additionalKeys, cancellation);
    private static Ownership.OwnerContext RequireOwnedRole(GameSession session) => CharacterService.RequireOwnedRole(session);
    private static RoleProgress CloneProgress(RoleProgress source) => CharacterService.CloneProgress(source);
    private static void CopyProgress(RoleProgress target, RoleProgress source) => CharacterService.CopyProgress(target, source);
    private static void CopyItem(InvItem target, InvItem source) => CharacterService.CopyItem(target, source);
    private static bool CanStackGrant(InvItem item, int entry, int limit, int bind, long bindExpire = 0) =>
        CharacterService.CanStackGrant(item, entry, limit, bind, bindExpire);
    private List<InvItem> AddItemCore(GameSession session, int entry, int count, out bool currency,
        bool persist = true, bool notifyItemGained = true, int bind = 0) =>
        character.AddItemCore(session, entry, count, out currency, persist, notifyItemGained, bind);
    private void AddVirtualReward(Romsg.GetRewardNotify notification, int entry, long amount) =>
        character.AddVirtualReward(notification, entry, amount);
    private void ApplyExp(RoleProgress p, long baseExp, long jobExp) => character.ApplyExp(p, baseExp, jobExp);
    private bool IsCurrency(int entry) => character.IsCurrency(entry);
    private long BaseExpNeed(int level) => character.BaseExpNeed(level);
    private long JobExpNeed(int career, int jobLevel) => character.JobExpNeed(career, jobLevel);
    private int StatCost(int value) => character.StatCost(value);
    private int TotalPoints(RoleProgress p) => character.TotalPoints(p);
    private PlanBaseProperty CurrentPlan(RoleProgress p, PlanBaseProperty? saved = null) => character.CurrentPlan(p, saved);
    private int UsedPoints(RoleProgress p) => character.UsedPoints(p);
    private static long NextVersion(GameSession session, uint msgId) => CharacterService.NextVersion(session, msgId);
    private Task AfterLevelChangeAsync(GameSession session, Func<bool>? stillOwner = null, bool persist = true, bool levelUp = false) =>
        character.AfterLevelChangeAsync(session, stillOwner, persist, levelUp);
}
