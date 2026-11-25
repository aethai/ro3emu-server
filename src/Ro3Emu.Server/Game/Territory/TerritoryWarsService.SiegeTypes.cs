using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    public const uint SiegeBaseQuery = 44402, SiegeHistoryQuery = 44403, SiegeResultNotify = 44404;
    public const uint SiegeToolAdd = 32275;

    /// <summary>After a committed siege settlement, per loaded participant: role, personal score, whether its guild
    /// won (quest target 106611). Listeners post their work.</summary>
    public SyncHook<long, long, bool> SiegeSettled { get; } = new("TerritoryWarsService.SiegeSettled");
    /// <summary>After a committed territory battle settlement, per loaded participant: role and round (1 Stronghold,
    /// 2 and later Regional Capital). Listeners post their work.</summary>
    public SyncHook<long, int> BattleSettled { get; } = new("TerritoryWarsService.BattleSettled");

    public sealed class SiegeRunState
    {
        public int City { get; set; }
        public long Start { get; set; }
        public long End { get; set; }
        public long Attack { get; set; }
        public long Defense { get; set; }
        public long StatisticsId { get; set; }
        public byte[] AttackSnapshot { get; set; } = [];
        public byte[] DefenseSnapshot { get; set; } = [];
        public long CrystalHp { get; set; }
        public Dictionary<int, long> ObjectHp { get; set; } = [];
        public List<int> Destroyed { get; set; } = [];
        public int Stage { get; set; }
        public long StageStart { get; set; }
        public long StageAdded { get; set; }
        public long LastAdded { get; set; }
        public long PrepareEnd { get; set; }
        public Dictionary<int, long[]> Refresh { get; set; } = [];
        public List<int> PlacedLadders { get; set; } = [];
        public long SettledAt { get; set; }
        public bool Settled { get; set; }
        public long Winner { get; set; }
        public Dictionary<long, Participant> Players { get; set; } = [];
        public byte[]? Result { get; set; }
    }

    public sealed class SiegeRun(TerritoryWarsService owner, string key, InstanceService.Room room)
    {
        public string Key { get; } = key;
        public string Epoch { get; } = owner.state.Epoch;
        public InstanceService.Room Room { get; } = room;
        public SiegeRunState State => owner._seasonCleanupState is { } retiring && retiring.Epoch == Epoch
            ? retiring.SiegeRuns.GetValueOrDefault(Key)
              ?? throw new InvalidOperationException("Retiring territory siege state is missing.")
            : owner.state.Epoch == Epoch && owner.state.SiegeRuns.TryGetValue(Key, out var current) ? current
              : throw new InvalidOperationException("Territory siege has no active or retiring epoch state.");
        public MonsterUnit? Crystal { get; set; }
        internal Dictionary<int, MonsterUnit> Objects { get; } = [];
        internal Dictionary<int, long> SentHp { get; } = [];
        internal Dictionary<long, SiegeBagUnit> Bags { get; } = [];
        internal CommandBoard Command { get; } = new();
        public bool Spawning { get; set; }
        internal Dictionary<long, Dictionary<long, long>> Hits { get; } = [];
    }

    internal sealed class SiegeBagUnit : WorldUnit
    {
        public required int NpcId { get; init; }
        public required long Camp { get; init; }
        public MonsterTemplate? Template { get; init; }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2, EntityCamp = Camp, Face = Face,
                State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
                MovementInfo = new MovementInfo
                {
                    Pos = new Vec3 { X = X, Y = Y, Z = Z }, Face = Face, ServerTime = NowMs,
                    MoveType = MovementType.MoveTypeNone,
                },
            };
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            return entity;
        }
    }
}
