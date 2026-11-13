using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// A scripted step's steerable slide (ScriptedStep.Slide, the Ant Queen's Nest egg passage). The client owns the movement:
/// custom data Player_SlideSpeed, then Player_SlideState 1, switches the local player to LC_Movement_Controller_Slide, which
/// moves it down the steepest slope and steers it with the joystick (_calcSteepestDownhillDir, _tickSlideJoystickMove) and
/// reports its position every 0.1 s; state 0 switches it back (Controller_Slide.OnCustomData, _tryApplySlideData,
/// _applySlideData). The ids are the client's enum_CustomData (Lua_CustomDataDefine). The speeds come from the scene's json
/// (inferred: the original's are not in the client).
/// </summary>
public sealed partial class DungeonService
{
    /// <summary>enum_CustomData.Player_SlideState: 1 enters the slide (once the speed is known), 0 leaves it.</summary>
    internal const long SlideStateData = 5400001;

    /// <summary>enum_CustomData.Player_SlideSpeed: [sideSpeed, downSpeed] in cm/s (the client divides by 100).</summary>
    internal const long SlideSpeedData = 5400003;

    /// <summary>enum_CustomData.Player_SlideCameraId: the Camera row the slide switches to (Controller_Slide._applySlideData).</summary>
    internal const long SlideCameraData = 5400005;

    /// <summary>(inferred) How long a slide's buff is given for: longer than any slide; a scene change or death clears it first.</summary>
    private const int SlideBuffMs = 120_000;

    /// <summary>Set by WorldCoreService: stores a custom data value on the player and sends 31018 to it and its viewers.</summary>
    internal Func<GameSession, CustomData, Task>? SetCustomData { get; set; }

    /// <summary>
    /// The player is on a slide this server started. Its client moves it faster than it walks, so the walk check lets it
    /// through (SceneService.WalkOnRide); PublicParamConfig.IgnoreVerifyMaps lists 1000201 and 1000301.
    /// </summary>
    internal static bool Sliding(GameSession s) =>
        s.WorldState.CustomData.TryGetValue(SlideStateData, out var d) && d.CustomParam?.LongParam == 1;

    private async Task StartSlideAsync(Attempt a, ReconstructedDungeons.Slide slide)
    {
        if (SetCustomData is not { } set) return;
        foreach (var member in Present(a).Where(m => a.Loaded.Contains(m) && !m.SceneLoading))
        {
            // Speed first: the client enters the slide on state 1 only once it has a speed (_tryApplySlideData).
            await set(member, new CustomData
            {
                Id = SlideSpeedData,
                CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.ListLong, ListLongParam = { slide.SideCm, slide.DownCm } },
            });
            if (slide.Camera != 0) await set(member, LongData(SlideCameraData, slide.Camera));
            await set(member, SlideState(1));
            if (slide.Buff != 0) await _buffs.AddAsync(member, slide.Buff, SlideBuffMs, member.Role!.Id);
        }
    }

    /// <summary>The slide's step is complete (its checkpoint): each sliding participant stops and plays the end animation.</summary>
    private async Task EndSlideAsync(Attempt a, ReconstructedDungeons.Slide slide)
    {
        foreach (var member in Present(a).Where(Sliding).ToList())
        {
            await StopSlideAsync(member);
            if (slide.EndSkill != 0) await _combat.PlaySkillAnimationAsync(member, slide.EndSkill);
        }
    }

    /// <summary>
    /// Ends a player's slide (also when it leaves the scene first), then drops the stored values: WorldCoreService sends a
    /// player's stored custom data again on every scene load (31071), which would start the slide in the next scene.
    /// </summary>
    private async Task StopSlideAsync(GameSession s)
    {
        if (!Sliding(s)) return;
        // WorldCoreService.SetCustomAsync refuses a player that is not ready (no role, no progress, a scene loading).
        if (SetCustomData is { } set && s.Role != null && s.Progress != null && !s.SceneLoading) await set(s, SlideState(0));
        s.WorldState.CustomData.Remove(SlideStateData);
        s.WorldState.CustomData.Remove(SlideSpeedData);
        s.WorldState.CustomData.Remove(SlideCameraData);
    }

    /// <summary>The most points a sliding leg is tested at (inferred bound: a leg longer than this many metres is sampled sparser).</summary>
    private const int SlideLegSamples = 100;

    /// <summary>
    /// Whether the participant's walk reached a region. A sliding client reports every 0.1 s (Controller_Slide
    /// m_fSlideSyncInterval), so a fast slide can pass a narrow region (the 5 m deep end of the Ant Queen's passage) between
    /// two reports: for a sliding player the leg since the last report is tested every metre too. A seated driver
    /// (<paramref name="testLeg"/>) is as fast: the Deniro at 150% crosses the 6 m deep end region of the nest between two reports.
    /// </summary>
    private static bool Reached(GameSession s, Func<float, float, bool> inside, bool testLeg = false)
    {
        if (inside(s.Position.X, s.Position.Z)) return true;
        if (!testLeg && !Sliding(s)) return false;
        var from = s.PreviousPosition;
        float dx = s.Position.X - from.X, dz = s.Position.Z - from.Z;
        int steps = Math.Min(SlideLegSamples, (int)MathF.Ceiling(MathF.Sqrt(dx * dx + dz * dz)));
        for (int i = 1; i < steps; i++)
            if (inside(from.X + dx * i / steps, from.Z + dz * i / steps)) return true;
        return false;
    }

    private static CustomData SlideState(long state) => LongData(SlideStateData, state);

    private static CustomData LongData(long id, long value) => new()
    {
        Id = id,
        CustomParam = new CustomParam { Tp = CustomParam.Types.ParamType.Long, LongParam = value },
    };
}
