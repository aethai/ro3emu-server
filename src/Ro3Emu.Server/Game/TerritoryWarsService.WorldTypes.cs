using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    public const uint ScenePointNotify = 30129;
    public const uint GlorySync = 5001, GloryRankReward = 83400;
    public const int GloryFunction = 7205, GloryCurrency = 15, GlorySeasonMail = 1090;
    internal const long StrongholdScene = 3030101;
    internal const long WorldPickupUidBase = 1L << 45;
    internal const long WorldPickupUidEnd = 1L << 46;
    internal const int ScenePointNpc = 4905010;
    internal const long GloryAssistWindowMs = 10_000;

    /// <summary>The role's camp is set or loaded (TaskService: the Faction Quest's sub-task tier). Listeners post.</summary>
    public SyncHook<GameSession> CampChanged { get; } = new("TerritoryWarsService.CampChanged");

    /// <summary>A Crystalline Ore cluster (4910101-4910103) was collected and committed (TaskService: 107102).
    /// Listeners post their work: the character lock and the territory gate are held.</summary>
    public SyncHook<GameSession> CrystalCollected { get; } = new("TerritoryWarsService.CrystalCollected");

    /// <summary>A loaded siege participant's dungeon task tracker changed (TaskService.OnDungeonRun): scene, instance,
    /// step. Listeners post their work.</summary>
    public SyncHook<GameSession, long, long, int> SiegeTracked { get; } = new("TerritoryWarsService.SiegeTracked");

    /// <summary>The role's place on leaderboard 2102 (RankService.RankOfAsync), 0 unlisted; set by the host.</summary>
    public Func<long, Task<int>>? GloryBoardRank { get; set; }

    internal Action? BeforeWorldPickupApplyForTest;
    internal Action? BeforeWorldDeathApplyForTest;

    /// <summary>Persisted together with the player's currency deduction; a dropped supply actor survives a restart.</summary>
    public sealed class WorldDrop
    {
        public long Id { get; set; }
        public long Scene { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public int Amount { get; set; }
    }

    /// <summary>Durable spawn-cycle identity and earliest configured respawn for a static supply slot.</summary>
    public sealed class WorldPickupOccurrence
    {
        public long Ordinal { get; set; }
        public long NextEligible { get; set; }
    }

    /// <summary>One GlobalScenePointData (MG_Dungeon.proto:362-367).</summary>
    public sealed class ScenePoint
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public int Status { get; set; }
        public long ReadyTime { get; set; }
        public long Owner { get; set; }
    }

    /// <summary>The marker actor at a gameplay point: NPCType 1, carrying its template HP so it is not shown dead.</summary>
    internal sealed class ScenePointUnit : WorldUnit
    {
        public required int PointId { get; init; }
        public MonsterTemplate? Template { get; init; }
        public long BornMs { get; init; }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = ScenePointNpc, EntityType = 1, Face = Face, State = EntityState.Alive,
                BornTimeMs = BornMs, NotifyType = EntityBase.Types.NotifyType.All, EntityCamp = Template?.Camp ?? 1,
                MovementInfo = new MovementInfo
                {
                    Pos = Pos, Face = Face, ServerTime = NowMs,
                    MoveType = MovementType.MoveTypeNone
                },
            };
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            return entity;
        }
    }

    /// <summary>One season's Battle Merit of a role.</summary>
    public sealed class GloryState
    {
        public string Epoch { get; set; } = "";
        public long Points { get; set; }
        public long Rank { get; set; } = 1;
        public HashSet<long> Claimed { get; set; } = [];
        public int Day { get; set; }
        public long DayGain { get; set; }
        /// <summary>Today's meetings per "kind:other role" (the KillDif / DeathDif count).</summary>
        public Dictionary<string, int> Counts { get; set; } = [];
    }

    internal enum GloryKind { Kill = 1, Assist = 2, Death = 3 }

    public sealed class AuctionRound
    {
        public int Round { get; set; }
        public long FinalEnd { get; set; }
        public bool Settled { get; set; }
        public Dictionary<int, AuctionZone> Zones { get; set; } = [];
        public Dictionary<long, AutoBidPlan> Auto { get; set; } = [];
    }
    public sealed class AuctionZone
    {
        public int Id { get; set; }
        public int Seats { get; set; }
        public List<BidEntry> Entries { get; set; } = [];
        public HashSet<long> Bidded { get; set; } = [];
    }
    public sealed class BidEntry
    {
        public long GuildId { get; set; }
        public long Amount { get; set; }
        public long Time { get; set; }
        public bool Occupier { get; set; }
        public bool Legacy { get; set; }
        public long EscortScore { get; set; }
    }
    public sealed class AutoBidPlan
    {
        public long Creator { get; set; }
        public int Zone { get; set; }
        public long Initial { get; set; }
        public long Maximum { get; set; }
        public int Style { get; set; }
        public int State { get; set; }
        public long Next { get; set; }
    }

    internal sealed record PanelBattle(int Zone, int Round, long Start, long End, IReadOnlyDictionary<long, long> Scores, int Players);
    internal sealed record PanelSiegeCity(int City, int Zone, long Attack, long Defense, int Stage, bool Settled, long Winner);
    internal sealed record PanelView(TerritoryWarsClock.Stage Current, IReadOnlyList<TerritoryWarsClock.Stage> Stages, long Now,
        IReadOnlyDictionary<int, long> Owners, IReadOnlyList<PanelBattle> Battles, (long Start, long End)? SiegeWindow,
        IReadOnlyList<PanelSiegeCity> Cities);
}
