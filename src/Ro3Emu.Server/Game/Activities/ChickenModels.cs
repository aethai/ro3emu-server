using System.Text.Json;
using Romsg;
using static Ro3Emu.Server.Game.ChickenRules;

namespace Ro3Emu.Server.Game;

internal sealed record ZoneEvent(long AtMs, int VarId, long[] Values);

internal sealed record ZoneShrink(long StartMs, long EndMs, float FromX, float FromZ, float ToX, float ToZ,
    float FromRadius, float ToRadius, int Circle);

internal sealed class ReviveChannel
{
    public required GameSession Reviver { get; init; }
    public required long StartMs { get; init; }
    public required float X { get; init; }
    public required float Z { get; init; }
    public required GameSession[] Targets { get; init; }
}

internal sealed class ChickenShop
{
    public required ChickenNpcUnit Unit { get; init; }
    /// <summary>Drawn commodity id -> its ShopCommodityConfig row.</summary>
    public Dictionary<long, JsonElement> Goods { get; } = [];
    public Dictionary<long, long> Sold { get; } = [];
}

internal sealed class AdventureOffer
{
    public required long OperateId { get; init; }
    public required int PoolId { get; init; }
    public required int ChooseCount { get; init; }
    public required long EndSec { get; init; }
    public int RefreshCount;
    public long[] Cards = [];
}

/// <summary>One Survivor Battle match's in-scene state.</summary>
internal sealed class ChickenRun
{
    public int Stage = StageNone;
    public long StageStartMs, StageEndMs, BattleStartMs, BattleEndMs;
    public required Random Rng;
    public Dictionary<GameSession, ChickenPlayer> Players { get; } = new(ReferenceEqualityComparer.Instance);
    public List<ChickenChestUnit> Chests { get; } = [];
    public List<ZoneEvent> Zone { get; } = [];
    public List<ZoneShrink> Shrinks { get; } = [];
    public int ZonePublished = -1;
    public List<int> EliminatedFactions { get; } = [];
    public List<ReviveChannel> Channels { get; } = [];
    public int PickOrder;
    public long NextGuid = 1;
    /// <summary>Landing follow: follower role -> followed role, and invite cooldowns (inviter, invitee) -> end in seconds.</summary>
    public Dictionary<long, long> Follow { get; } = [];
    public Dictionary<(long Inviter, long Invitee), long> InviteCdSec { get; } = [];
    public long LastTickMs;
    public HashSet<long> TagDropped { get; } = [];
    public List<ChickenShop> Shops { get; } = [];
    /// <summary>Magic Dice still standing, and the encounter offers' operate ids.</summary>
    public List<ChickenNpcUnit> Dice { get; } = [];
    public long NextOperateId;
}

/// <summary>One Survivor Battle participant's match state (5036 data and settlement statistics).</summary>
internal sealed class ChickenPlayer
{
    public required int TeamIndex;
    public int Career;
    public int LandX = UnchosenX, LandZ = UnchosenZ, PickOrder;
    public bool LandChosen;
    public Dictionary<long, ChickenItem> Pack { get; } = [];
    public SortedDictionary<int, long> SkillBar { get; } = [];
    public SortedDictionary<int, long> EquipBar { get; } = [];
    public SortedDictionary<int, int> ItemBar { get; } = [];
    public Dictionary<long, long> IdentityCards { get; } = [];
    public Dictionary<int, long> ItemCd { get; } = [];
    public RoundRoleStatus State = RoundRoleStatus.InWar;
    public int FreeRelifeLeft = FreeRelifeCount;
    public long RelifeUntilMs;
    public float RelifeX, RelifeZ;
    public int Resurrections, TagDrops;
    public bool GaveUp;
    public long Damage, Injury, Cure, Assists, Deaths, Help, Rescued, Revived;
    public long EliminatedMs;
    public Dictionary<GameSession, long> LastHitBy { get; } = new(ReferenceEqualityComparer.Instance);
    public List<(long PerTick, long UntilMs)> Heals { get; } = [];
    public long EmojiAtSec;
    public long Watching, AutoWatchAtMs, WatchSwitchMs;
    public Dictionary<int, long> UsedSubType { get; } = [];
    public HashSet<long> OpenedCrates { get; } = [];
    public Dictionary<int, long> EquipQuality { get; } = [];
    public Dictionary<long, long> Bought { get; } = [];
    public Dictionary<long, long> CastFamilies { get; } = [];
    public Dictionary<long, AdventureOffer> Offers { get; } = [];
    public List<long> Cards { get; } = [];
    public long EventTriggers;
}
