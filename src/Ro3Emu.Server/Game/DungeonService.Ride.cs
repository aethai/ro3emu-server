using Romsg;
using Kind = Ro3Emu.Server.Game.ReconstructedDungeons.TriggerKind;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted ride (ScriptedStep.RideAfter): the actor a player used to complete a step walks a guide route of the scene
/// editor (m_kGuidePoints) and the participants move with it, before the step's teleport and the next step. The client
/// has no ride rule: the route and the scene's camera animation are data without a trigger, and the original's server
/// script is not shipped. So (inferred) the carrier walks the route leg by leg at its own UnitConfig._fSpeed as a monster
/// walks, a seated player goes with it on its seat, a player who is not seated walks there itself every tick
/// (transType 3 ServerControl, (proven) LC_Scene_Sync.lua _syncUnitMoveState: the local player's controlled unit obeys
/// it in every scene, unlike ForceTeleport which a surface scene ignores), and the camera animation is not played. The
/// carrier stays where the route ends (a later step that shows it at the same point finds it there). A ride that is
/// alone moves only the carrier.
/// </summary>
public sealed partial class DungeonService
{
    private const int RideTickMs = 100;

    /// <summary>(inferred) The carrier waits this long after seating a rider, while the client plays the seat's enter animation.</summary>
    private const int RideStartDelayMs = 1500;

    /// <summary>(inferred) The rider's client plays the seat's exit animation for this long after being put down.</summary>
    private const int RideExitDelayMs = 2500;

    /// <summary>(inferred) A carrier that walks alone waits for the 'Try to Ride' animation of the interaction (skill 5362901: 2367 ms).</summary>
    private const int RideAloneDelayMs = 2400;

    /// <summary>
    /// An actor interaction that seats its user (NPCInteractConfig _iStatusID of a vehicle status, the Deniro's 5300005)
    /// puts the player in the actor's seat, the client's own gameplay state; the actor puts them down when its ride ends,
    /// or when they leave the scene, or at once through the client's exit button.
    /// </summary>
    private async Task SeatRiderAsync(Attempt a, GameSession s, SceneActorUnit actor, int interaction)
    {
        // Only the interaction the current step waits on seats anyone: the client may send any of an actor's interactions.
        if (!_vehicles.IsRide(interaction) || !StepWaitsOn(a, interaction)) return;
        long token = await _vehicles.SeatOnSceneActorAsync(s, actor.Uid, actor.NpcId, interaction);
        if (token != 0) a.Riders[s] = token;
    }

    /// <summary>The ride is over: the actor puts its riders down.</summary>
    private async Task PutDownRidersAsync(Attempt a)
    {
        foreach (var (s, token) in a.Riders.ToList()) await _vehicles.PutDownFromSceneActorAsync(s, token);
        a.Riders.Clear();
    }

    /// <summary>Whether the running step is completed by this actor interaction, directly or by its dungeon message.</summary>
    private bool StepWaitsOn(Attempt a, long interaction) => !a.StepDone && Step(a) is { } step
        && step.Complete.Any(t => t.Kind == Kind.Interaction && t.Id == interaction
            || t.Kind == Kind.Message && _catalog.DungeonMessage(interaction) == t.Id);

    private sealed record Ride(IReadOnlyList<(float X, float Y, float Z)> Path, SceneActorUnit Carrier, float Length,
        int Generation, long StartMs, double DurationMs)
    {
        /// <summary>Metres along the route the last tick moved the riders to; -1 before the first tick.</summary>
        public float Walked { get; set; } = -1;

        /// <summary>The route leg the carrier was last told to walk, -1 before the first.</summary>
        public int Leg { get; set; } = -1;

        /// <summary>The region the rider drives the carrier to after the route (ScriptedStep.DriveAfter), 0: none.</summary>
        public long DriveTo { get; init; }

        /// <summary>The carrier's speed for this ride in cm/s (ScriptedStep.RideSpeedPercent), 0: its own.</summary>
        public long SpeedCm { get; init; }

        /// <summary>The carrier walks alone: nobody is moved with it (ScriptedStep.RideAlone).</summary>
        public bool Alone { get; init; }
    }

    /// <summary>
    /// A ride after its route: the seated driver steers the carrier (its client controls the vehicle, LC_PassengerComponent
    /// ChangeControlUnit) until someone reaches Region; then Arrived runs.
    /// </summary>
    internal sealed record Drive(SceneActorUnit Carrier, long Region, Func<Task> Arrived, int Generation);

    /// <summary>
    /// Whether a rider stays seated: the step's ride is driven on to a region (DriveAfter) not reached yet. (inferred) The
    /// original's script is not shipped, so holding the driver in the seat until that region (the client's own exit button,
    /// VehicleSeatConfig 5300005 _kGameplayBtnId 26, is refused) is a guess that keeps the ride from being left halfway (play-tested).
    /// </summary>
    private static bool RideHolds(GameSession s) => Active(s) is { } a && a.Riders.ContainsKey(s)
        && Step(a) is { DriveAfter: not 0 } && !a.Room.Closed;

    /// <summary>The carrier's walking speed in metres per second: UnitConfig._fSpeed (cm/s) of its NPC's _iUnitID, 0 if none.</summary>
    private float RideSpeed(SceneActorUnit carrier) =>
        NPCConfig.Get(_data, carrier.NpcId) is { } npc && UnitConfig.Get(_data, npc.UnitId) is { } unit ? (float)unit.Speed / 100f : 0f;

    /// <summary>The horizontal length of a route in metres, as the monsters' walks count distance.</summary>
    internal static float RideLength(IReadOnlyList<(float X, float Y, float Z)> path)
    {
        float length = 0;
        for (int i = 1; i < path.Count; i++) length += MonsterService.Distance(path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z);
        return length;
    }

    /// <summary>The position and heading after <paramref name="metres"/> along a route (clamped to its ends).</summary>
    internal static (float X, float Y, float Z, float Face) RideAt(IReadOnlyList<(float X, float Y, float Z)> path, float metres)
    {
        float left = Math.Max(0, metres);
        for (int i = 1; i < path.Count; i++)
        {
            var (a, b) = (path[i - 1], path[i]);
            float segment = MonsterService.Distance(a.X, a.Z, b.X, b.Z);
            float face = MonsterService.Angle(a.X, a.Z, b.X, b.Z);
            if (left <= segment || i == path.Count - 1)
            {
                float t = segment <= 0 ? 1 : Math.Min(1, left / segment);
                return (a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, face);
            }
            left -= segment;
        }
        return (path[0].X, path[0].Y, path[0].Z, 0);
    }

    /// <summary>The route leg at <paramref name="metres"/>: its index and its two points.</summary>
    internal static (int Leg, (float X, float Y, float Z) From, (float X, float Y, float Z) To) RideLeg(
        IReadOnlyList<(float X, float Y, float Z)> path, float metres)
    {
        float left = Math.Max(0, metres);
        for (int i = 1; i < path.Count; i++)
        {
            float segment = MonsterService.Distance(path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z);
            if (left < segment || i == path.Count - 1) return (i - 1, path[i - 1], path[i]);
            left -= segment;
        }
        return (0, path[0], path[0]);
    }

    /// <summary>
    /// Starts the step's ride when it has one and the player used an actor that can carry (a route with at least two
    /// points, a positive speed). <paramref name="arrived"/> then runs when the route is walked; false means no ride
    /// starts and the caller goes on at once.
    /// </summary>
    private bool TryStartRide(Attempt a, ReconstructedDungeons.ScriptedStep step, SceneActorUnit? carrier, Func<Task> arrived)
    {
        if (step.RideAfter == 0 || carrier == null || _catalog.GuidePath(a.Plan.SceneEntry, step.RideAfter) is not { } path)
        {
            if (step.RideAfter != 0)
            {
                bool routeFound = carrier != null && _catalog.GuidePath(a.Plan.SceneEntry, step.RideAfter) != null;
                LogRideNotStartedCarrier(a.Plan.SceneEntry, step.RideAfter, carrier?.NpcId ?? 0, routeFound);
            }
            return false;
        }
        float speed = RideSpeed(carrier) * step.RideSpeedPercent / 100f, length = RideLength(path);
        if (speed <= 0 || length <= 0)
        {
            LogRideNotStartedSpeed(a.Plan.SceneEntry, step.RideAfter, speed, length);
            return false;
        }
        TimeSpan delay = a.Riders.Count > 0 ? Paced(RideStartDelayMs) : step.RideAlone ? Paced(RideAloneDelayMs) : TimeSpan.Zero;
        var ride = new Ride(path, carrier, length, a.StepGeneration, MonsterService.NowMs + (long)delay.TotalMilliseconds,
            Paced((int)(length / speed * 1000)).TotalMilliseconds)
        {
            DriveTo = step.DriveAfter, SpeedCm = step.RideSpeedPercent != 100 ? (long)MathF.Round(speed * 100) : 0, Alone = step.RideAlone,
        };
        long delayMs = (long)delay.TotalMilliseconds;
        var (routeStart, routeEnd) = (path[0], path[^1]);
        LogRideStarts(a.Plan.SceneEntry, step.RideAfter, delayMs, length, speed, step.RideAlone, carrier.NpcId, carrier.X, carrier.Z,
            routeStart.X, routeStart.Z, routeEnd.X, routeEnd.Z);
        // A carrier that walks alone offers nothing on its way (the feeding it has just had would still show its button).
        if (step.RideAlone) _loop.PostLegacy(() => SetActorBuffsAsync(carrier, []), "dungeon ride");
        _loop.ScheduleLegacy(delay, () => RideTickAsync(a, ride, arrived), "dungeon ride");
        return true;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Scripted scene {Scene} ride: moving unit {Npc} failed")]
    private partial void LogRideMoveFailed(Exception e, long scene, int npc);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scripted scene {Scene} ride {Route} not started: carrier {Carrier}, route found {Found}")]
    private partial void LogRideNotStartedCarrier(long scene, long route, int carrier, bool found);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scripted scene {Scene} ride {Route} not started: speed {Speed} m/s, length {Length} m")]
    private partial void LogRideNotStartedSpeed(long scene, long route, float speed, float length);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scripted scene {Scene} ride {Route} starts in {Delay} ms: {Length:0.0} m at {Speed:0.0} m/s, alone {Alone}, "
        + "carrier {Carrier} at {X:0.0},{Z:0.0}, route from {FromX:0.0},{FromZ:0.0} to {ToX:0.0},{ToZ:0.0}")]
    private partial void LogRideStarts(long scene, long route, long delay, float length, float speed, bool alone, int carrier, float x, float z,
        float fromX, float fromZ, float toX, float toZ);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scripted scene {Scene} carrier {Carrier} shown again at {X:0.0},{Z:0.0} as {Uid}")]
    private partial void LogCarrierShownAgain(long scene, int carrier, float x, float z, long uid);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scripted scene {Scene} ride ended: carrier {Carrier} at {X:0.0},{Z:0.0}")]
    private partial void LogRideEnded(long scene, int carrier, float x, float z);

    /// <summary>The route's points past <paramref name="from"/> and before <paramref name="to"/> metres, in route order.</summary>
    internal static IEnumerable<(float X, float Y, float Z, float Face)> RideCorners(IReadOnlyList<(float X, float Y, float Z)> path,
        float from, float to)
    {
        float reached = 0;
        for (int i = 0; i < path.Count; i++)
        {
            if (i > 0) reached += MonsterService.Distance(path[i - 1].X, path[i - 1].Z, path[i].X, path[i].Z);
            if (reached > from && reached < to) yield return RideAt(path, reached);
        }
    }

    /// <summary>Tells the viewers the carrier's walking speed in cm/s (attribute 113), when it is not that already; 0: its own.</summary>
    private async Task SetCarrierSpeedAsync(SceneActorUnit carrier, long speedCm)
    {
        if (speedCm <= 0 || carrier.CarrySpeed == speedCm) return;
        carrier.CarrySpeed = speedCm;
        await _world.ToUnitViewersAsync(carrier, CombatService.AoiAttrChange, new AOIAttrChange
        {
            EntityUuid = (ulong)carrier.Uid,
            Property = { new AoIProperty { Idx = AttributeId.Speed, Data = speedCm, BaseData = speedCm } },
        });
    }

    /// <summary>
    /// A step that is driven but has no route (ScriptedStep.DriveAfter without RideAfter): the seated player drives the
    /// carrier from where it stands until someone reaches the region, then <paramref name="arrived"/> runs. False when
    /// the step has none or nobody is seated, and the caller goes on at once.
    /// </summary>
    private async Task<bool> TryStartDriveAsync(Attempt a, ReconstructedDungeons.ScriptedStep step, SceneActorUnit? carrier,
        Func<Task> arrived)
    {
        if (step.DriveAfter == 0 || step.RideAfter != 0 || carrier == null || !a.Riders.Keys.Any(_vehicles.IsSeatedOnSceneActor)) return false;
        if (step.RideSpeedPercent != 100) await SetCarrierSpeedAsync(carrier, (long)MathF.Round(RideSpeed(carrier) * step.RideSpeedPercent));
        a.Driving = new Drive(carrier, step.DriveAfter, arrived, a.StepGeneration);
        return true;
    }

    /// <summary>The carrier leaves and enters again at its place with a new UID, as a scene actor of the next step would.</summary>
    private async Task ReshowCarrierAsync(Attempt a, SceneActorUnit old)
    {
        int at = a.SceneActors.IndexOf(old);
        if (at < 0 || a.Settled || a.Room.Closed) return;
        var unit = new SceneActorUnit
        {
            Uid = SceneActorUidBase | Interlocked.Increment(ref _sceneActorSerial), SceneEntry = old.SceneEntry, InstanceId = old.InstanceId,
            X = old.X, Y = old.Y, Z = old.Z, Face = old.Face, NpcId = old.NpcId, Point = old.Point, EntityType = old.EntityType,
            Camp = old.Camp, Name = old.Name, Template = old.Template, BornMs = MonsterService.NowMs, CarrySpeed = old.CarrySpeed,
        };
        a.RetiredActors.Add(old.Uid);
        await _instances.RemovePropAsync(a.Room, old);
        if (await _instances.AddPropAsync(a.Room, unit)) a.SceneActors[at] = unit;
        else a.SceneActors.RemoveAt(at);
        LogCarrierShownAgain(a.Plan.SceneEntry, unit.NpcId, unit.X, unit.Z, unit.Uid);
    }

    private async Task RideTickAsync(Attempt a, Ride ride, Func<Task> arrived)
    {
        if (a.Settled || a.Room.Closed || a.StepGeneration != ride.Generation) return;
        // The ride's own speed, before the first move: the client walks the carrier, and its driver drives it, at attribute 113.
        if (ride.Walked < 0) await SetCarrierSpeedAsync(ride.Carrier, ride.SpeedCm);
        double done = Math.Min(1.0, (MonsterService.NowMs - ride.StartMs) / Math.Max(1.0, ride.DurationMs));
        float walked = (float)(done * ride.Length);
        // The position follows the wall clock, so a late tick (a loaded loop) jumps ahead: the riders still pass every
        // point of the route walked since the last tick instead of being moved straight across its corners.
        foreach (var corner in RideCorners(ride.Path, ride.Walked, walked)) await MoveRidersAsync(a, ride, corner);
        ride.Walked = walked;
        await MoveRidersAsync(a, ride, RideAt(ride.Path, walked));
        await WalkLegAsync(a, ride, walked);
        if (done >= 1)
        {
            LogRideEnded(a.Plan.SceneEntry, ride.Carrier.NpcId, ride.Carrier.X, ride.Carrier.Z);
            // A carrier that walked alone is shown again where it parked: the client keeps an NPC's interaction range at the place it
            // appeared (inferred), and the next step's interaction would only be offered at the start.
            if (ride.Alone) await ReshowCarrierAsync(a, ride.Carrier);
            // A driven ride: the route's end hands the carrier to its seated driver, who goes on until the region.
            if (ride.DriveTo != 0 && a.Riders.Keys.Any(_vehicles.IsSeatedOnSceneActor))
            {
                a.Driving = new Drive(ride.Carrier, ride.DriveTo, arrived, ride.Generation);
                return;
            }
            // The step's end removes the carrier: not while a rider's client still plays the seat's exit animation, which reads
            // the vehicle (LC_PassengerComponent._detachFromVehicle), or its next scene change stops at the loading screen.
            bool seated = a.Riders.Count > 0;
            await PutDownRidersAsync(a);
            if (seated) _loop.ScheduleLegacy(Paced(RideExitDelayMs), arrived, "dungeon ride end");
            else await arrived();
        }
        else _loop.ScheduleLegacy(Paced(RideTickMs), () => RideTickAsync(a, ride, arrived), "dungeon ride");
    }

    /// <summary>
    /// A participant's accepted walk while a ride is driven: the carrier is where its seated driver is (the driver's client
    /// moves it and reports its own walk, as VehicleSeats.MovedAsync), and the drive ends when anyone reaches its region.
    /// </summary>
    private async Task DroveAsync(Attempt a, GameSession s)
    {
        if (a.Driving is not { } drive || a.Settled || a.Room.Closed || a.StepGeneration != drive.Generation) return;
        bool driver = a.Riders.ContainsKey(s) && _vehicles.IsSeatedOnSceneActor(s);
        if (driver) (drive.Carrier.X, drive.Carrier.Y, drive.Carrier.Z, drive.Carrier.Face) = (s.Position.X, s.Position.Y, s.Position.Z, s.Face);
        // A driver's report can land past a narrow region it crossed since the last one, so its leg is tested too.
        if (_catalog.SceneRegion(a.Plan.SceneEntry, drive.Region) is not { } region || !Reached(s, region.Contains, driver)) return;
        a.Driving = null;
        bool seated = a.Riders.Count > 0;
        await PutDownRidersAsync(a);
        if (seated) _loop.ScheduleLegacy(Paced(RideExitDelayMs), drive.Arrived, "dungeon ride end");
        else await drive.Arrived();
    }

    private async Task MoveRidersAsync(Attempt a, Ride ride, (float X, float Y, float Z, float Face) at)
    {
        ride.Carrier.X = at.X; ride.Carrier.Y = at.Y; ride.Carrier.Z = at.Z; ride.Carrier.Face = at.Face;
        if (ride.Alone) return;
        // A move that fails for one unit must not strand the step: the ride goes on and arrives.
        foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
        {
            try
            {
                // A seated rider's client moves it with the seat, so a forced position would only shake it.
                if (a.Riders.ContainsKey(member) && _vehicles.IsSeatedOnSceneActor(member))
                {
                    member.PreviousPosition = member.Position;
                    member.Position = new Vec3 { X = at.X, Y = at.Y, Z = at.Z };
                }
                else await WalkRiderAsync(member, at);
            }
            catch (Exception e) { LogRideMoveFailed(e, a.Plan.SceneEntry, 0); }
        }
    }

    /// <summary>
    /// A not-seated rider is not the client's controlled vehicle, so its own client must be told to walk there itself.
    /// (proven) transType 3 ServerControl calls the controlled unit's SetMoveTargetPos in every scene, the one
    /// mechanism among transType 1/2/3 that a surface scene does not ignore (LC_Scene_Sync.lua, _syncUnitMoveState);
    /// a per-tick instant reposition (ResetPos/ForceTeleport) is what made the rider stagger. Other viewers are not
    /// this player's controlled unit, so they are told as if the rider had walked there, the ordinary multiplayer sync.
    /// </summary>
    private async Task WalkRiderAsync(GameSession member, (float X, float Y, float Z, float Face) at)
    {
        var pos = new Vec3 { X = at.X, Y = at.Y, Z = at.Z };
        await member.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
        {
            Uid = member.Role!.Id, TransType = MovementTransitionType.MoveTransServerControl,
            MovementInfo = new MovementInfo { Pos = pos, Face = at.Face, ServerTime = MonsterService.NowMs },
        });
        member.PreviousPosition = member.Position;
        member.Position = pos;
        member.Face = at.Face;
        await _world.MoveAsync(member, new WalkReq { Pos = pos, Face = at.Face, BStop = false, SpeedRate = 1 });
    }

    /// <summary>Tells the viewers to walk the leg the carrier has reached, as a monster's walk is sent (MonsterService).</summary>
    private async Task WalkLegAsync(Attempt a, Ride ride, float walked)
    {
        var (leg, from, to) = RideLeg(ride.Path, walked);
        if (leg == ride.Leg) return;
        ride.Leg = leg;
        float metres = MonsterService.Distance(from.X, from.Z, to.X, to.Z);
        float speed = (float)(ride.Length / Math.Max(1.0, ride.DurationMs) * 1000);
        float face = MonsterService.Angle(from.X, from.Z, to.X, to.Z);
        try
        {
            // A driver's client controls its vehicle and ignores a walk for it; transType 3 is the order it obeys:
            // SetMoveTargetPos to the given position at the unit's own speed (LC_Scene_Sync._syncUnitMoveState).
            await _world.MoveUnitAsync(ride.Carrier, a.Riders.Keys.Any(_vehicles.IsSeatedOnSceneActor)
                ? new MovementChangeNtf
                {
                    Uid = ride.Carrier.Uid, TransType = MovementTransitionType.MoveTransServerControl,
                    MovementInfo = new MovementInfo { Pos = new Vec3 { X = to.X, Y = to.Y, Z = to.Z }, Face = face, ServerTime = MonsterService.NowMs },
                }
                : new MovementChangeNtf
                {
                    Uid = ride.Carrier.Uid, TransType = MovementTransitionType.MoveTransNormal,
                    MovementInfo = new MovementInfo
                    {
                        Pos = new Vec3 { X = from.X, Y = from.Y, Z = from.Z }, ServerTime = MonsterService.NowMs,
                        MoveDuration = (int)Math.Max(1, metres / speed * 1000), Face = face, MoveType = MovementType.MoveTypeWalk,
                        MovementWalk = new MovementWalk { Dir = face, Speed = speed, Acc = 0, EndHeight = to.Y },
                    },
                });
        }
        catch (Exception e) { LogRideMoveFailed(e, a.Plan.SceneEntry, ride.Carrier.NpcId); }
    }
}
