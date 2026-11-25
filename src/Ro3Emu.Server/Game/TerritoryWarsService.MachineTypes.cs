using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    public const uint NpcSkillCd = 32705;
    public const uint SetMark = 30407, MarkNotify = 30408, GatherMark = 30431, CommandMark = 30436, CommandMarkNotify = 30437;
    public const uint CommandSkillNotify = 30439, TeleportInvite = 30118, TeleportResponse = 30119, TeleportClear = 30120,
        TeleportCount = 30122;
    internal const int BlessingOfLight = 4909001, BattleCommand = 4909101, DefendTheGlory = 4909201, EmergencyCall = 4909301;
    /// <summary>LC_ConditionDefine CheckGVGPermission.</summary>
    internal const long GvgPermission = 2202;
    /// <summary>LC_ConditionList sub-condition types of the enter condition (4900001): CheckBuilder, CheckCamp, and
    /// 12001 the gameplay status, inverted (not already in one).</summary>
    internal const long CheckBuilder = 2201, CheckCamp = 2200, InStatus = 12001;
    /// <summary>DirtyDataConfig 4900014 GVGCannonBulletRestNum and 4900006 CameraFollowBullet_ListLong.</summary>
    internal const long BulletRestNum = 4900014, CameraFollowBullet = 4900006;
    /// <summary>enum_CustomData BelongGuild (Lua_CustomDataDefine): a unit's guild when its head info has none.</summary>
    internal const long BelongGuild = 104;
    /// <summary>enum_CustomData InteractionProgress, InteractionCurrentProgress, InteractionRate (Lua_CustomDataDefine).</summary>
    internal const long InteractionProgress = 10000, InteractionCurrentProgress = 10001, InteractionRate = 10002;

    internal enum WarKind { Siege, Territory }

    internal static long CampOf(GameSession s) => s.EventFaction > 0 ? s.EventFaction : 1;

    internal static CustomData CannonLong(long id, long value) =>
        new() { Id = id, CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value } };

    /// <summary>Max HP, current HP and level, as a monster's entity carries them.</summary>
    internal static void AddMachineHealth(EntityBase entity, MonsterUnit unit)
    {
        foreach (var (idx, value) in new (long, long)[]
                 { (101, unit.Template.MaxHp), (2000, unit.Dead ? 0 : unit.Hp), (2002, unit.Template.Level) })
            entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
    }

    /// <summary>A machine's guild on its entity: guildId (EntityBase 59) and custom data 104 BelongGuild.</summary>
    internal static void AddMachineGuild(EntityBase entity, long guild)
    {
        if (guild == 0) return;
        entity.GuildId = guild;
        entity.CustomDatas.Add(CannonLong(BelongGuild, guild));
    }

    /// <summary>The camp of a cannon or vehicle, or null for any other unit.</summary>
    internal static long? MachineCamp(MonsterUnit m) => m switch
    {
        CannonUnit cannon => cannon.Camp,
        VehicleUnit vehicle => vehicle.Camp,
        _ => null,
    };

    /// <summary>The guild of a cannon or vehicle, or null for any other unit.</summary>
    internal static long? MachineGuild(MonsterUnit m) => m switch
    {
        CannonUnit cannon => cannon.Guild,
        VehicleUnit vehicle => vehicle.Guild,
        _ => null,
    };

    /// <summary>A built cannon (NPCConfig _iNPCType 2, sub type 102).</summary>
    internal sealed class CannonUnit : MonsterUnit
    {
        public required int NpcId { get; init; }
        public required long Builder { get; init; }
        public required long Camp { get; init; }
        public required long Guild { get; init; }
        public required string Name { get; init; }
        public required long ConfigId { get; init; }
        public List<BuffInfo> Buffs { get; } = [];
        public GameSession? Operator { get; set; }
        public long Bullets { get; set; }
        public long CdStartMs { get; set; }
        public long ReadyMs { get; set; }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 2, Name = Name, EntityCamp = Camp,
                Owner = Builder, Face = Face, State = Dead ? EntityState.Dead : EntityState.Alive,
                NotifyType = EntityBase.Types.NotifyType.All,
                MovementInfo = new MovementInfo
                {
                    Pos = Pos, Face = Face, ServerTime = NowMs, MoveType = MovementType.MoveTypeNone
                },
            };
            AddMachineHealth(entity, this);
            if (Buffs.Count > 0) entity.BuffData = new AoIBuffInfo { AddBuffs = { Buffs.Select(b => b.Clone()) } };
            entity.CustomDatas.Add(CannonLong(BulletRestNum, Bullets));
            AddMachineGuild(entity, Guild);
            return entity;
        }
    }

    /// <summary>A war vehicle: its owner (the member who used the item), camp, guild, seats and the gunners' aim.</summary>
    internal sealed class VehicleUnit : SeatedVehicle
    {
        public required long Guild { get; init; }

        public override EntityBase Entity()
        {
            var entity = SeatedEntity();
            AddMachineHealth(entity, this);
            AddMachineGuild(entity, Guild);
            return entity;
        }
    }

    /// <summary>A cannon bullet in flight (NPCConfig _iNPCType 10): its entity carries the whole ballistic move.</summary>
    internal sealed class CannonBullet : WorldUnit
    {
        public required int NpcId { get; init; }
        public required long Shooter { get; init; }
        public required long Camp { get; init; }
        public required Vec3 Velocity { get; init; }
        public required float Gravity { get; init; }
        public required long LaunchMs { get; init; }
        public required int FlightMs { get; init; }
        public required int EntityType { get; init; }

        public Vec3 At(double t) => new()
        {
            X = (float)(X + Velocity.X * t), Y = (float)(Y + Velocity.Y * t - Gravity * t * t / 2), Z = (float)(Z + Velocity.Z * t),
        };

        public override EntityBase Entity() => new()
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = Shooter, EntityCamp = Camp,
            Face = Face, State = EntityState.Alive, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo
            {
                Pos = new Vec3 { X = X, Y = Y, Z = Z }, Face = Face, ServerTime = LaunchMs, MoveDuration = FlightMs,
                MoveType = MovementType.MoveTypeMove,
                MovementMove = new MovementMove { StartVelocity = Velocity.Clone(), AccVelocity = new Vec3 { Y = -Gravity } },
            },
        };
    }

    /// <summary>A repair zone on the ground: NPCConfig 4901010 of the member's camp until <see cref="EndMs"/>.</summary>
    internal sealed class RepairZoneUnit : WorldUnit
    {
        public required int NpcId { get; init; }
        public required int EntityType { get; init; }
        public required long Owner { get; init; }
        public required long Camp { get; init; }
        public required long Guild { get; init; }
        public required float Radius { get; init; }
        public required long BornMs { get; init; }
        public required long EndMs { get; init; }

        public override EntityBase Entity() => new()
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Owner = Owner, AiMasterEntityId = (ulong)Owner,
            EntityCamp = Camp, GuildId = Guild, State = EntityState.Alive, BornTimeMs = BornMs, Face = Face, NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = NowMs, MoveType = MovementType.MoveTypeNone },
        };
    }

    /// <summary>A platform's ring: the guild activating it (or, for the defenders' platform, the guild allowed to), and
    /// the progress its entity carries.</summary>
    internal sealed class PlatformRing : WorldUnit
    {
        public required int PointId { get; init; }
        public required int NpcId { get; init; }
        public MonsterTemplate? Template { get; init; }
        public long BornMs { get; init; }
        public long Guild { get; set; }
        public long Total { get; init; }
        public long Progress { get; set; }
        public long Rate { get; set; }
        public HashSet<GameSession> Activators { get; } = [];

        public IEnumerable<CustomData> Data()
        {
            yield return CannonLong(BelongGuild, Guild);
            yield return CannonLong(InteractionProgress, Total);
            yield return CannonLong(InteractionCurrentProgress, Progress);
            yield return CannonLong(InteractionRate, Rate);
        }

        public override EntityBase Entity()
        {
            var entity = new EntityBase
            {
                Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = 1, Face = Face, State = EntityState.Alive,
                BornTimeMs = BornMs, NotifyType = EntityBase.Types.NotifyType.All, EntityCamp = Template?.Camp ?? 1, GuildId = Guild,
                MovementInfo = new MovementInfo { Pos = Pos, Face = Face, ServerTime = NowMs, MoveType = MovementType.MoveTypeNone },
            };
            entity.CustomDatas.AddRange(Data());
            if (Template is { } t)
                foreach (var (idx, value) in new (long, long)[] { (101, t.MaxHp), (2000, t.MaxHp), (2002, t.Level) })
                    entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
            return entity;
        }
    }

    /// <summary>One battle's marks, by side (guild) or by party.</summary>
    internal sealed class CommandBoard
    {
        public Dictionary<long, Vec3?[]> CommanderMarks { get; } = [];
        public Dictionary<long, Vec3?[]> TeamMarks { get; } = [];
        public Dictionary<long, long[]> CommandMarks { get; } = [];
        public Dictionary<long, CommandSkillState> Skills { get; } = [];
    }

    /// <summary>One side's command skills: the charges and cooldowns, and its open Emergency Call.</summary>
    internal sealed class CommandSkillState
    {
        public Dictionary<int, DungeonCommandSkillInfo> Skills { get; } = [];
        public long CommonCdStart { get; set; }
        public long CommonCdEnd { get; set; }
        public EmergencyCallState? Call { get; set; }
    }

    internal sealed class EmergencyCallState
    {
        public long Inviter { get; init; }
        public Vec3 Origin { get; init; } = new();
        public long Until { get; init; }
        public HashSet<long> Invited { get; } = [];
        public HashSet<long> Accepted { get; } = [];
    }
}
