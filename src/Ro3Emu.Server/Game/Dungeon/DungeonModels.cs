using Romsg;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

internal sealed class RaidRun(ReconstructedDungeons.RaidScript script)
{
    public ReconstructedDungeons.RaidScript Script { get; } = script;
    public long EngagedAtMs { get; set; }
    public long[] NextDueMs { get; } = new long[script.Trials.Count];
    public HashSet<(int Trial, int Hp)> HpFired { get; } = [];
    public TrialRun? Active { get; set; }
    public long QuietUntilMs { get; set; }
}

internal sealed class TrialRun
{
    public required ReconstructedDungeons.RaidTrial Trial { get; init; }
    public required int Index { get; init; }
    public required MonsterUnit Boss { get; init; }
    public required long StartedAtMs { get; init; }
    public required long EndsAtMs { get; init; }
    public required int Players { get; init; }
    public long ShowSession { get; set; }
    public List<TrialUnit> Objects { get; } = [];
    public HashSet<long> Marked { get; } = [];
    /// <summary>The players a sweeping object has caught (and hit) in this trial.</summary>
    public HashSet<GameSession> Swept { get; } = [];
    public bool Over { get; set; }
}

internal sealed class TrialUnit
{
    public required WorldUnit Unit { get; init; }
    public required ReconstructedDungeons.TrialObject Spec { get; init; }
    public int Color { get; init; } = -1;
    public bool Chosen { get; init; }
    public bool Gone { get; set; }
    public bool Pickup => Spec.Use != ReconstructedDungeons.TrialUse.None;
    /// <summary>How far along its lane a sweeping object has got (metres); its unit stays at the lane's start.</summary>
    public float Travelled { get; set; }
}

/// <summary>
/// A scene actor: NPCConfig _iNPCType 1 is created by the client as a monster unit (entity type 1, with its HP
/// and level so the head bar renders), 2 as an NPC (entity type 2, which TriggerNpcInteract looks up by config id).
/// Its buffs travel in the entity (AoIBuffInfo) and in 31104/31105 when a later step changes them.
/// </summary>
internal sealed class SceneActorUnit : WorldUnit
{
    public required int NpcId { get; init; }
    /// <summary>The scene-editor point it stands at; a shell game's shuffle moves a Bell to another (ScriptedDungeonService.Shuffle).</summary>
    public required long Point { get; set; }
    public required int EntityType { get; init; }
    public required long Camp { get; init; }
    public required string Name { get; init; }
    public MonsterTemplate? Template { get; init; }
    public long BornMs { get; init; }
    public List<BuffInfo> Buffs { get; } = [];

    /// <summary>Attribute 113 of an actor that can carry a rider: its own walking speed in cm/s, which the client moves it at.</summary>
    public long CarrySpeed { get; set; }

    public override EntityBase Entity()
    {
        var entity = new EntityBase
        {
            Uuid = (ulong)Uid, EntityTemplateID = (ulong)NpcId, EntityType = EntityType, Name = Name, EntityCamp = Camp,
            // An NPC (type 2) is shaped exactly like the server's world NPCs (SceneService.NpcEntity: portals, Kafras,
            // whose 32700 the live client sends): no born time, so it never enters a born state. A monster-type actor
            // keeps it, as MonsterUnit does.
            Face = Face, State = EntityState.Alive, BornTimeMs = EntityType == 2 ? 0 : BornMs,
            NotifyType = EntityBase.Types.NotifyType.All,
            MovementInfo = new MovementInfo
            {
                Pos = Pos, Face = Face, ServerTime = NowMs,
                MoveType = MovementType.MoveTypeNone
            },
        };
        if (Template is { } t)
            foreach (var (idx, value) in new (long, long)[] { (AttributeId.MaxHp, t.MaxHp), (AttributeId.Hp, t.MaxHp), (2002, t.Level), (AttributeId.Speed, t.SpeedCm),
                         (102, t.MaxSp), (AttributeId.Sp, t.MaxSp) })
                entity.Property.Add(new AoIProperty { Idx = idx, Data = value, BaseData = value });
        if (CarrySpeed > 0) entity.Property.Add(new AoIProperty { Idx = AttributeId.Speed, Data = CarrySpeed, BaseData = CarrySpeed });
        if (Buffs.Count > 0) entity.BuffData = new AoIBuffInfo { AddBuffs = { Buffs.Select(b => b.Clone()) } };
        return entity;
    }
}

/// <summary>
/// A ride after its route: the seated driver steers the carrier (its client controls the vehicle, LC_PassengerComponent
/// ChangeControlUnit) until someone reaches Region; then Arrived runs.
/// </summary>
internal sealed record Drive(SceneActorUnit Carrier, long Region, Func<Task> Arrived, int Generation);

/// <summary>
/// Mode-specific hooks for special dungeon runs (Guild Challenge, Dracula's Lair) so <see cref="DungeonService"/>
/// and <see cref="DungeonService.Attempt"/> stay free of feature-specific fields.
/// </summary>
internal interface IDungeonMode
{
    bool SuppressesStandardKillObligation => false;
    bool WaitsForManualStart => false;
    bool SuppressesDungeonReturn => false;
    bool ClearsSceneActorsOnSettle => false;
    int TrackerStep(DungeonService.Attempt attempt) => attempt.Settled ? TaskService.RunOver : attempt.Wave;
    void PopulateSnapshot(DungeonService.Attempt attempt, DungeonBaseInfoNotify snapshot) { }
    void PopulateWaveEvent(DungeonService.Attempt attempt, DungeonEventNotify change) { }
    Task OnWaveSpawnedAsync(DungeonService.Attempt attempt) => Task.CompletedTask;
    Task<bool> TryHandleClientEventAsync(GameSession session, ClientDungeonEvent req) => Task.FromResult(false);
    Task OnPlayerLeftAsync(DungeonService.Attempt attempt, GameSession session) => Task.CompletedTask;
    Task SettleAsync(DungeonService.Attempt attempt, bool won);
    int InteractionError(DungeonService.Attempt attempt, GameSession session, int npcId) => 0;
    Task OnInteractedAsync(DungeonService.Attempt attempt, GameSession session, int npcId) => Task.CompletedTask;
}
