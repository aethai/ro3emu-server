using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class WorldCoreService
{
    /// <summary>eObjectState.eObjectStateFly (43), set directly (not buff-driven, like eObjectStateBattling in
    /// CombatService): the client enters flight only once LC_UnitBase.CheckFlyMask sees this bit.</summary>
    private const long FlyBit = 1L << 42;

    // FlyBuffTag and MapConfig._iFly are consumed by the client's flight availability checks. A flying mount's take-off
    // (MountFlightSwitchAsync) grants its landing skill's id as a buff tagged 3100000, the same chain any other source of
    // this tag would use; there is no separate mount-only path here.
    private bool CanFly(GameSession s) => Ready(s) && !s.Dead
        && _data.Row("MapConfig", s.SceneEntry) is { } map && GameData.Int(map, "_iFly") == 1
        && s.Buffs.Any(b => b.ExpireMs > Now && _data.Row("BuffConfig", b.ConfigId) is { } row
            && row.TryGetProperty("_kBuffCategory", out var tags) && tags.ValueKind == JsonValueKind.Array
            && tags.EnumerateArray().Any(t => GameData.Number(t) == 3100000));

    // A flight a mount's take-off started lasts only while that mount is ridden: a rider who gets off in the air (or
    // swaps the System-slot mount) is put down by the heartbeat tick (inferred).
    private bool MountFlightLost(GameSession s) =>
        s.WorldState.MountFlightBuff != 0 && MountService.FlySkills(_data, s)?.Down != s.WorldState.MountFlightBuff;

    /// <summary>A day's worth: long enough that only landing (or the buff falling off some other way) ends it.</summary>
    private const int FlyBuffMs = 24 * 60 * 60 * 1000;

    /// <summary>
    /// CombatService.MountFlightSwitch, at _kLogicData[1] ms into a flying mount's take-off or landing cast: the one
    /// place mount flight starts or ends. MountService.FlySkills names the pair and CombatService.GrantedSkill lets only
    /// the direction matching the current flight state through. A take-off grants the flight buff and eObjectStateFly
    /// (the client's LC_UnitSpecialStatus.CheckFlyMask, 1 &lt;&lt; (43 - 1)), a landing takes both away; then the rider is
    /// carried by a 31112 to the end <see cref="MountFlight.Plan"/> picks (the client's aimed point when plausible), the
    /// climb the client needs to reach its flight mesh before WASD moves it (and it sends 31084) at all. Conditions
    /// 86000556/86000558 [2009,1] CheckSceneFlyable (CombatService.SkillConditions) refuse the take-off on a map whose
    /// _kRiderForbidden[3] forbids flying mounts; CanFly's _iFly check still gates the flight moves.
    /// </summary>
    private async Task MountFlightSwitchAsync(GameSession s, int skillId, Vec3? aimed)
    {
        if (s.Role is not { } role || MountService.FlySkills(_data, s) is not { } fly) return;
        bool takeOff = skillId == fly.Up;
        if (takeOff == s.WorldState.Flying || !takeOff && skillId != fly.Down) return;
        var start = s.Position.Clone();
        if (MountFlight.Plan(_data, skillId, takeOff, _worldData.Map(s.SceneEntry), start, aimed) is not { } move) return;
        if (takeOff)
        {
            await _buffs.AddAsync(s, fly.Down, FlyBuffMs, role.Id);
            s.WorldState.Flying = true;
            s.WorldState.MountFlightBuff = fly.Down;
            s.WorldState.LastFlyMs = Now;
            await SetFlyBitAsync(s, true);
        }
        else
        {
            s.WorldState.Flying = false;
            await EndMountFlightAsync(s);
        }
        // As a skill's caster move (CombatService.MoveCasterAsync): the server is at the end at once, and a walk report
        // that predates the move is dropped (RoleSessionService.WalkAsync, SkillCarry).
        s.CarryBySkill(start, move.End, Now, move.MoveMs);
        s.LastWalkUtc = DateTime.UtcNow;
        s.Walking = false;
        await ToSelfAndViewersAsync(s, CombatService.SkillMove, new SkillStartMoveNotify
        {
            EntityID = (ulong)role.Id, StartPos = start, EndPos = move.End.Clone(), SkillConfigId = skillId, EffectId = move.EffectId,
            StartTimestampMs = Now, MoveType = MountFlight.Move3D, Face = s.Face, MoveTimeMs = move.MoveMs,
        });
        await _world.MoveAsync(s, new WalkReq { Pos = move.End.Clone(), Dir = s.Face, Face = s.Face, BStop = true }, forceTeleport: true);
    }

    /// <summary>
    /// Every landing path (the skill, 31083, a free fall, the forced landing on the heartbeat tick) and leaving the scene
    /// ends here, so a mount's flight buff and the status bit never outlive the authoritative Flying flag, even when the
    /// mount that granted the buff is no longer ridden.
    /// </summary>
    private async Task EndMountFlightAsync(GameSession s)
    {
        int granted = s.WorldState.MountFlightBuff;
        s.WorldState.MountFlightBuff = 0;
        foreach (var buff in s.Buffs.Where(b => granted != 0 && b.ConfigId == granted).ToList()) await _buffs.RemoveAsync(s, buff);
        await SetFlyBitAsync(s, false);
    }

    private async Task SetFlyBitAsync(GameSession s, bool flying)
    {
        long next = flying ? s.StatusBits | FlyBit : s.StatusBits & ~FlyBit;
        if (next == s.StatusBits) return;
        s.StatusBits = next;
        await ToSelfAndViewersAsync(s, CombatService.AoiAttrChange, new AOIAttrChange
        { EntityUuid = (ulong)s.Role!.Id, StateInfo = new AoiObjStateInfo { Data = new CommonBigMask { Values = { (ulong)s.StatusBits } } } });
    }

    /// <summary>
    /// Wing use effects (ActionConfig 101 JumpSameMap / 102 JumpOtherMap, _iEXECHost 1; ItemConfig 1002001 Fly Wing and
    /// 1002000 Butterfly Wing). The Fly Wing moves the player to a random spot of the current map (inferred: one of the
    /// map's authored points or monster spawn points on walkable ground reachable from the map's arrivals, so it never
    /// lands outside the play area, MapWorld.RandomSpots); the Butterfly Wing goes to the picked map's Kafra arrival, else its born point (inferred).
    /// </summary>
    private async Task<bool> WingJumpAsync(GameSession s, long scene)
    {
        if (_worldData.Map(scene) is not { } map) return false;
        if (scene != s.SceneEntry) return await this._scene.EnterSceneAsync(s, scene, map.KafraArrival);
        var spots = await _worldData.RandomSpotsAsync(map, scene);
        if (spots.Count == 0 || s.SceneEntry != scene) return false;
        await this._scene.TeleportInMapAsync(s, spots[Random.Shared.Next(spots.Count)]);
        return true;
    }

    /// <summary>31083 toggles authoritative flight only with the configured buff and a valid map.</summary>
    public async Task FlySwitchAsync(GameSession s, FlySwitchReq req)
    {
        if (!Ready(s) || s.Dead || !float.IsFinite(req.Height) || req.Height < 0 || req.Height > 50
            || req.IsFly && (!CanFly(s) || s.WorldState.Flying))
        { await ErrorAsync(s, FlySwitch, ErrorCodes.Parameter); return; }
        if (!req.IsFly)
        {
            if (s.WorldState.Flying) await LandAsync(s, s.Position, s.Face);
            return;
        }
        // (inferred) Height is a desired offset; cap each takeoff to the same one-second flight budget.
        var position = s.Position.Clone();
        position.Y += Math.Min(req.Height, FlightSpeed(s));
        var map = _worldData.Map(s.SceneEntry);
        if (map == null || position.Y > map.Grounded(new MapPoint(position.X, position.Y, position.Z, s.Face)).Y + 50)
        { await ErrorAsync(s, FlySwitch, ErrorCodes.Parameter); return; }
        s.WorldState.Flying = true;
        s.WorldState.LastFlyMs = Now;
        await MoveFlightAsync(s, position, s.Face, new Vec3(), true, MovementTransitionType.MoveTransFlyUp);
    }

    private float FlightSpeed(GameSession s) => _data.Table("PublicParamConfig").TryGetValue("MountFlyMaxSpeed", out var value)
        ? Math.Max(1, (float)(GameData.Number(value) ?? s.Speed) / 100) : Math.Max(1, s.Speed / 100f);

    /// <summary>31084: AStarAgentNew sends position plus a three-dimensional direction, not a ground yaw. The distance is
    /// held to the flight speed's budget (WalkAllowance, as walks are); a move beyond it is put back and counted. The
    /// height is judged from <see cref="MountFlight.Floor"/>, so a flight over ground nobody walks on (water, cliffs)
    /// is not put back.</summary>
    public async Task FlyMoveAsync(GameSession s, FlyMoveReq req)
    {
        float speed = FlightSpeed(s);
        var map = _worldData.Map(s.SceneEntry);
        float? floor = Finite(req.Pos) && map != null ? MountFlight.Floor(map, req.Pos) : null;
        if (!s.WorldState.Flying || !CanFly(s) || !Finite(req.Pos) || !Finite(req.Dir) || !float.IsFinite(req.Face)
            || Distance(new Vec3(), req.Dir) > 2
            || floor is not { } under || req.Pos.Y < under - 1 || req.Pos.Y > under + MountFlight.MaxHeight)
        { await ErrorAsync(s, FlyMove, ErrorCodes.Parameter); await CorrectFlightAsync(s); return; }
        float distance = Distance(s.Position, req.Pos);
        if (!s.FlightAllowance.TrySpend(DateTime.UtcNow, speed, distance))
        {
            _scene.Cheats.Refused(s, CheatMonitor.Flight, $"scene {s.SceneEntry} {distance:F1} m beyond the flight budget at {speed:F1} m/s");
            await ErrorAsync(s, FlyMove, ErrorCodes.Parameter); await CorrectFlightAsync(s); return;
        }
        s.WorldState.LastFlyMs = Now;
        await MoveFlightAsync(s, req.Pos, req.Face, req.Dir, req.BStop, MovementTransitionType.MoveTransNormal);
    }

    public async Task FreeFallAsync(GameSession s, FreeFallJumpReq req)
    {
        if (!Ready(s) || s.Dead || !s.WorldState.Flying || !Finite(req.ToPos) || !float.IsFinite(req.Face)
            || Distance(s.Position, req.ToPos) > 50 || req.ToPos.Y > s.Position.Y + 1
            || MonsterService.Distance(s.Position.X, s.Position.Z, req.ToPos.X, req.ToPos.Z) > 3)
        { await ErrorAsync(s, FreeFall, ErrorCodes.Parameter); return; }
        await LandAsync(s, req.ToPos, req.Face);
    }

    private async Task LandAsync(GameSession s, Vec3 requested, float face)
    {
        var map = _worldData.Map(s.SceneEntry);
        if (map == null) return;
        var at = map.Grounded(new MapPoint(requested.X, requested.Y, requested.Z, face));
        if (!map.IsWalkable(at.X, at.Y, at.Z)) { await ErrorAsync(s, FreeFall, ErrorCodes.Parameter); return; }
        s.WorldState.Flying = false;
        await EndMountFlightAsync(s);
        await MoveFlightAsync(s, new Vec3 { X = at.X, Y = at.Y, Z = at.Z }, face, new Vec3(), true, MovementTransitionType.MoveTransLanding);
        // FixLandNtf instructs the client to end the special landing state; immediate landing has zero remaining time.
        await ToSelfAndViewersAsync(s, FixLand, new FixLandNtf { Uuid = s.Role!.Id, FixLandTime = 0 });
    }

    private async Task CorrectFlightAsync(GameSession s)
    {
        if (s.Role == null) return;
        await s.SendGameAsync(GameAoiSink.MovementChange, new MovementChangeNtf
        { Uid = s.Role.Id, TransType = MovementTransitionType.MoveTransForceTeleport, MovementInfo = new MovementInfo { Pos = s.Position.Clone(), Face = s.Face, ServerTime = Now } });
    }

    private async Task MoveFlightAsync(GameSession s, Vec3 pos, float face, Vec3 direction, bool stop, MovementTransitionType transition)
    {
        var move = new MovementInfo { Pos = pos.Clone(), Face = face, ServerTime = Now, MoveType = MovementType.MoveTypeNone };
        float length = Distance(new Vec3(), direction);
        if (!stop && length > 0.0001f)
        {
            float speed = FlightSpeed(s);
            move.MoveType = MovementType.MoveTypeMove; move.MoveDuration = 1000;
            move.MovementMove = new MovementMove { StartVelocity = new Vec3 { X = direction.X / length * speed, Y = direction.Y / length * speed, Z = direction.Z / length * speed }, AccVelocity = new Vec3() };
        }
        s.PreviousPosition = s.Position; s.Position = pos.Clone(); s.Face = face; s.LastWalkUtc = DateTime.UtcNow; s.Walking = false;
        _combat.PlayerMoved(s);
        var notify = new MovementChangeNtf { Uid = s.Role!.Id, TransType = transition, MovementInfo = move };
        await s.SendGameAsync(GameAoiSink.MovementChange, notify);
        await _world.MoveAsync(s, new WalkReq { Pos = pos.Clone(), Face = face, BStop = true, SpeedRate = 1 }, movement: notify);
    }
}
