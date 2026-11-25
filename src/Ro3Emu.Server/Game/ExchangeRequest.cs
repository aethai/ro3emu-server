using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Accounts.Persistence;
using Ro3Emu.Server.Economy;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Parameters for one atomic bag/currency exchange (<see cref="CharacterService.ExchangeAsync(GameSession, ExchangeRequest)"/>
/// and <see cref="CharacterService.ExchangeLockedAsync"/>).
/// </summary>
public sealed record ExchangeRequest
{
    public IReadOnlyList<(int Entry, long Count)> Cost { get; init; } = [];
    public IReadOnlyList<(int Entry, int Count)> Goods { get; init; } = [];
    public IReadOnlyDictionary<long, int>? Instances { get; init; }
    public bool Popup { get; init; } = true;
    public bool Sync { get; init; } = true;
    public Action<RoleItemPlanBuilder>? Persist { get; init; }
    internal long ReservedGuid { get; init; }
    internal IReadOnlyList<CharacterService.AttributedGood>? AttributedGoods { get; init; }
    internal (InvItem Item, Dictionary<int, long> Attrs)? ItemUpdate { get; init; }
    internal IReadOnlyList<(int Entry, int Count, int Bind)>? BoundGoods { get; init; }
    internal IReadOnlyList<InvItem>? ExactGoods { get; init; }
    public IReadOnlyDictionary<int, long>? CurrencyGoods { get; init; }
    public long BaseExp { get; init; }
    public long JobExp { get; init; }
    public Action? Committed { get; init; }
    internal bool AsyncCommit { get; init; }
    internal Action? AppliedBeforeNotify { get; init; }
    public RewardSettlement? RewardSettlement { get; init; }
    internal Action<List<InvItem>>? StageItems { get; init; }
    internal Action<List<InvItem>>? StageAfterGoods { get; init; }
    public EconomyMutation? Economy { get; init; }
}
