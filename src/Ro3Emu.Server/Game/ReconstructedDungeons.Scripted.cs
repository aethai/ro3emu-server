using System.Globalization;
using System.Text.Json;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Scripted story scenes, with or without fights (Reconstructed/scripted-scenes.json; its "contract" section cites the client
/// code for every event used here). The original server's scene script is not in the client: each scene is rebuilt as
/// steps, each naming the DungeonQuestConfig quest the tracker shows, the scene actors shown during it, an optional
/// run-time story the client plays, and the client events that complete it. Nothing is keyed by a task id: a step
/// completes on any listed event (interaction, dungeon message, region, point or plot end).
/// </summary>
public sealed partial class ReconstructedDungeons
{
    public enum TriggerKind { Interaction, Message, Region, Point, Plot }

    /// <summary>A client event that completes (or, under "ignore", deliberately does not complete) a step.</summary>
    public sealed record StepTrigger(TriggerKind Kind, long Id, float Radius = 0);

    /// <summary>A scene actor: an NPCConfig id at a scene-editor point, with the buffs (BuffConfig ids) it carries.
    /// <paramref name="Dx"/>/<paramref name="Dz"/> (metres) place it beside the point when the scene has no point of its own
    /// for it (inferred placement).</summary>
    public sealed record SceneActor(int NpcId, long Point, IReadOnlyList<int> Buffs, float Dx = 0, float Dz = 0);

    /// <summary>
    /// One scripted step. <paramref name="Story"/> is a PlotInterpretation id the client plays at the step's start
    /// (0: none); <paramref name="TeleportAfter"/> a scene-editor point the participants are moved to when it
    /// completes (0: none). <paramref name="Goal"/> is the tracker's "(done/total)" while the step runs.
    /// <paramref name="Fight"/> are monsters spawned at the step's start: the step also waits until every one is
    /// defeated (a story dungeon that mixes fights with scripted parts). <paramref name="All"/>: every completing event
    /// is needed, not any one ("Have a Chitchat with them"). <paramref name="Cannon"/>: an actor's interaction seats its
    /// user on a cannon of the client's own gameplay, which fires its skill at the step's fight (DungeonService.Cannon).
    /// <paramref name="Seek"/>: the step is a hide-and-seek game
    /// whose hiding places are its Point triggers; walking there does not complete it, winning the game does.
    /// <paramref name="Companions"/>: the actors marked "companion", shown as allied fighters (CompanionUnit) instead of
    /// plain scene actors; they are not in <paramref name="Actors"/>.
    /// <paramref name="React"/>: what the step's speaker says, and the effect shown, when it begins, when an ignored event
    /// arrives and when it completes.
    /// </summary>
    public sealed record ScriptedStep(string Stage, int Quest, (int Done, int Total) Goal, IReadOnlyList<SceneActor> Actors,
        long Story, IReadOnlyList<StepTrigger> Complete, IReadOnlyList<StepTrigger> Ignore, long TeleportAfter,
        IReadOnlyList<Spawn> Fight, bool All, Cannon? Cannon = null,
        Seek? Seek = null, Shelter? Shelter = null, Reaction? React = null)
    {
        public IReadOnlyList<SceneActor> Companions { get; init; } = [];

        /// <summary>A scene-editor guide route (m_kGuidePoints) the actor a player used to complete the step walks, the
        /// participants with it, before <see cref="TeleportAfter"/> and the next step (0: none; inferred).</summary>
        public long RideAfter { get; init; }

        /// <summary>A scene-editor region the seated player drives the ride's carrier to after its route, held in the seat
        /// until it is reached; 0: the ride ends where its route ends (inferred, play-tested).</summary>
        public long DriveAfter { get; init; }

        /// <summary>The ride's carrier walks its route alone: the participants stay where they stand instead of going with it
        /// (inferred, play-tested).</summary>
        public bool RideAlone { get; init; }

        /// <summary>The ride's carrier speed in percent of its own (UnitConfig._fSpeed), for its route and the drive (inferred, play-tested).</summary>
        public int RideSpeedPercent { get; init; } = 100;

        /// <summary>Milliseconds the step stays on after it is completed, before the next step or the settlement (0: none;
        /// inferred): a bubble of the actors it showed is still being read, or a defeated enemy is still falling.</summary>
        public int HoldAfterMs { get; init; }

        /// <summary>The scene editor guide routes (m_kGuidePoints) shown while the step runs (DungeonService.GuideWays); null: the
        /// scene does not name them per step.</summary>
        public IReadOnlyList<long>? Guides { get; init; }

        /// <summary>The client's steerable slide while the step runs (DungeonService.Slide; null: none).</summary>
        public Slide? Slide { get; init; }
    }

    /// <summary>
    /// A slide (inferred speeds): the client moves the player downhill at <paramref name="DownCm"/> cm/s and steers it sideways at
    /// <paramref name="SideCm"/> cm/s, through the Camera row <paramref name="Camera"/> (0: the client's own). The player
    /// carries the BuffConfig row <paramref name="Buff"/> (0: none) from its start. When the step completes the slide stops
    /// and the player plays the SkillConfig row <paramref name="EndSkill"/>'s animation (0: none).
    /// </summary>
    public sealed record Slide(int SideCm, int DownCm, int EndSkill, int Camera = 0, int Buff = 0);

    /// <summary>A step's cannons: the actor's <paramref name="Interaction"/> seats its user, who fires the skill <paramref name="Fire"/>.</summary>
    public sealed record Cannon(long Interaction, int Fire);

    /// <summary>
    /// A step's reactions (inferred, Louie's 3120703 shell game: his lines and the right/wrong Bell effects are client rows
    /// that nothing in the client sends). <paramref name="Speaker"/> is the step's actor whose uid the lines carry;
    /// <paramref name="Start"/> ChatConfig rows go out one after another when the step begins, each when the previous one's
    /// _iTime has run out. <paramref name="Ignored"/> answers an event the step ignores, <paramref name="Complete"/> the
    /// event that completes it.
    /// </summary>
    public sealed record Reaction(int Speaker, IReadOnlyList<int> Start, Response? Ignored, Response? Complete);

    /// <summary>
    /// One reaction: the ChatConfig row <paramref name="Say"/> (0: none) and the BuffConfig row <paramref name="Buff"/>
    /// (0: none) put on the step's <paramref name="On"/> actor that stands at the point of the actor the player used. A
    /// completing reaction holds the next step (or the settlement) until its line's _iTime has run out.
    /// </summary>
    public sealed record Response(int Say, int Buff, int On);

    /// <summary>
    /// Cover from a boss's sweeping attack (inferred, the story Pharaoh's 3120702 after the raid Pharaoh's "Pharaoh is
    /// unleashing Cleansing Light! Quickly, hide behind the Pyramid!"): every <paramref name="EveryMs"/> while the fight's
    /// <paramref name="Boss"/> lives, the <paramref name="Tip"/> ChatConfig row goes out and the boss holds its ground for
    /// <paramref name="WarnMs"/>; then <paramref name="Skill"/> hits every participant not in the shadow of the
    /// <paramref name="Cover"/> actor: behind it as seen from the boss, within <paramref name="Width"/> metres of that line
    /// and <paramref name="Depth"/> metres past the cover.
    /// </summary>
    public sealed record Shelter(int Boss, int Cover, int Tip, long Skill, int EveryMs, int WarnMs, float Width, float Depth);

    /// <summary>
    /// A hide-and-seek game (inferred, Moonlight Flower's 3120802): the step's first actor opens with its
    /// <paramref name="Opening"/> bubbles; each round warns (<paramref name="Round"/>), counts down
    /// (<paramref name="Countdown"/>, a CombatCountdownTip row whose time is the count) and looks. Every participant must
    /// then stand at one of the step's points that this game has not searched yet. A found participant restarts the game
    /// (<paramref name="Found"/>, a bubble); a passed round searches the points within <paramref name="SearchedRadius"/>
    /// of each hiding place (<paramref name="Longer"/>), and the last of <paramref name="Rounds"/> wins
    /// (<paramref name="Won"/>), completing the step. Every message is a ChatConfig row sent as 1022.
    /// </summary>
    public sealed record Seek(int Rounds, float SearchedRadius, IReadOnlyList<int> Opening, int Round, int Countdown, int Longer,
        int Won, int Found);

    /// <summary>
    /// A scene. <paramref name="Unplayed"/> are DungeonQuestConfig quests of the scene that no step shows: the branch
    /// that shows them cannot be reached in the reconstruction (each has its reason in the json, "unplayed").
    /// </summary>
    public sealed record ScriptedScene(long Scene, string Name, string Confidence, IReadOnlyList<ScriptedStep> Steps,
        IReadOnlyList<int> Unplayed, long ContinuesTo = 0, bool LeavesAfterClear = false)
    {
        /// <summary>Milliseconds from the clear to the entry into the scene that continues it (0: DungeonService.ContinueSeconds).</summary>
        public int ContinueAfterMs { get; init; }
    }

    private readonly Dictionary<long, ScriptedScene> _scripted = [];

    public IReadOnlyCollection<ScriptedScene> AllScripted => _scripted.Values;

    /// <summary>The scripted reconstruction of a scene, when the switch is on.</summary>
    public ScriptedScene? Scripted(long scene) => Enabled && _scripted.TryGetValue(scene, out var s) ? s : null;

    private void LoadScriptedScenes()
    {
        using var stream = typeof(ReconstructedDungeons).Assembly.GetManifestResourceStream("Ro3Emu.Server.Reconstructed.scripted-scenes.json")
            ?? throw new InvalidOperationException("Embedded Reconstructed/scripted-scenes.json is missing.");
        using var doc = JsonDocument.Parse(stream);
        foreach (var entry in doc.RootElement.GetProperty("scenes").EnumerateObject())
        {
            long scene = long.Parse(entry.Name, CultureInfo.InvariantCulture);
            var steps = entry.Value.GetProperty("steps").EnumerateArray().Select(Step).ToArray();
            if (steps.Length == 0) throw new InvalidOperationException($"Scripted scene {scene} has no steps.");
            var unplayed = entry.Value.TryGetProperty("unplayed", out var u)
                ? u.EnumerateArray().Select(q => q.GetProperty("quest").GetInt32()).ToArray() : [];
            _scripted[scene] = new ScriptedScene(scene, entry.Value.GetProperty("name").GetString() ?? "",
                entry.Value.GetProperty("confidence").GetString() ?? "", steps, unplayed,
                entry.Value.TryGetProperty("continuesTo", out var next) ? next.GetInt64() : 0,
                entry.Value.TryGetProperty("leavesAfterClear", out var leaves) && leaves.GetBoolean())
            {
                ContinueAfterMs = entry.Value.TryGetProperty("continueAfterMs", out var after) ? after.GetInt32() : 0,
            };
        }
    }

    private static ScriptedStep Step(JsonElement s)
    {
        var goal = s.TryGetProperty("goal", out var g) ? (g[0].GetInt32(), g[1].GetInt32()) : (0, 1);
        var listed = s.TryGetProperty("actors", out var a) ? a.EnumerateArray().ToArray() : [];
        bool IsCompanion(JsonElement x) => x.TryGetProperty("companion", out var c) && c.GetBoolean();
        var actors = listed.Where(x => !IsCompanion(x)).Select(Actor).ToArray();
        var complete = Triggers(s, "complete");
        var fight = s.TryGetProperty("fight", out var f)
            ? f.EnumerateArray().Select(x => new Spawn(x.TryGetProperty("point", out var p) ? p.GetInt64() : 0, x.GetProperty("npc").GetInt32(),
                x.TryGetProperty("route", out var r) ? r.GetInt64() : 0)).ToArray()
            : [];
        if (complete.Count == 0 && fight.Length == 0)
            throw new InvalidOperationException($"Scripted step '{s.GetProperty("stage").GetString()}' cannot complete.");
        return new ScriptedStep(s.GetProperty("stage").GetString() ?? "", s.GetProperty("quest").GetInt32(), goal, actors,
            s.TryGetProperty("story", out var story) ? story.GetInt64() : 0, complete, Triggers(s, "ignore"),
            s.TryGetProperty("after", out var after) && after.TryGetProperty("teleport", out var to) ? to.GetInt64() : 0,
            fight, s.TryGetProperty("all", out var all) && all.GetBoolean(),
            s.TryGetProperty("cannon", out var cannon)
                ? new Cannon(cannon.GetProperty("interaction").GetInt64(), cannon.GetProperty("fire").GetInt32())
                : null,
            s.TryGetProperty("seek", out var seek)
                ? new Seek(seek.GetProperty("rounds").GetInt32(), seek.GetProperty("searchedRadius").GetSingle(),
                    [.. seek.GetProperty("opening").EnumerateArray().Select(v => v.GetInt32())], seek.GetProperty("round").GetInt32(),
                    seek.GetProperty("countdown").GetInt32(), seek.GetProperty("longer").GetInt32(), seek.GetProperty("won").GetInt32(),
                    seek.GetProperty("found").GetInt32())
                : null,
            s.TryGetProperty("shelter", out var shelter)
                ? new Shelter(shelter.GetProperty("boss").GetInt32(), shelter.GetProperty("cover").GetInt32(), shelter.GetProperty("tip").GetInt32(),
                    shelter.GetProperty("skill").GetInt64(), shelter.GetProperty("everyMs").GetInt32(), shelter.GetProperty("warnMs").GetInt32(),
                    shelter.GetProperty("width").GetSingle(), shelter.GetProperty("depth").GetSingle())
                : null,
            s.TryGetProperty("react", out var react) ? ParseReaction(react) : null)
        {
            Companions = [.. listed.Where(IsCompanion).Select(Actor)],
            RideAfter = s.TryGetProperty("after", out var rideAfter) && rideAfter.TryGetProperty("ride", out var ride) ? ride.GetInt64() : 0,
            DriveAfter = s.TryGetProperty("after", out var driveAfter) && driveAfter.TryGetProperty("drive", out var drive) ? drive.GetInt64() : 0,
            RideAlone = s.TryGetProperty("after", out var aloneAfter) && aloneAfter.TryGetProperty("alone", out var alone) && alone.GetBoolean(),
            RideSpeedPercent = s.TryGetProperty("after", out var speedAfter) && speedAfter.TryGetProperty("speed", out var rideSpeed) ? rideSpeed.GetInt32() : 100,
            HoldAfterMs = s.TryGetProperty("after", out var holdAfter) && holdAfter.TryGetProperty("hold", out var hold) ? hold.GetInt32() : 0,
            Guides = s.TryGetProperty("guides", out var guides)
                ? [.. guides.GetProperty("routes").EnumerateArray().Select(v => v.GetInt64())]
                : null,
            Slide = s.TryGetProperty("slide", out var slide)
                ? new Slide(slide.GetProperty("side").GetInt32(), slide.GetProperty("down").GetInt32(),
                    slide.TryGetProperty("endSkill", out var endSkill) ? endSkill.GetInt32() : 0,
                    slide.TryGetProperty("camera", out var camera) ? camera.GetInt32() : 0,
                    slide.TryGetProperty("buff", out var buff) ? buff.GetInt32() : 0)
                : null,
        };
    }

    private static SceneActor Actor(JsonElement x) => new(x.GetProperty("npc").GetInt32(), x.GetProperty("point").GetInt64(),
        x.TryGetProperty("buffs", out var b) ? b.EnumerateArray().Select(v => v.GetInt32()).ToArray() : [],
        x.TryGetProperty("offset", out var o) ? o[0].GetSingle() : 0, x.TryGetProperty("offset", out o) ? o[1].GetSingle() : 0);

    private static Reaction ParseReaction(JsonElement r) => new(r.GetProperty("speaker").GetInt32(),
        r.TryGetProperty("start", out var start) ? [.. start.EnumerateArray().Select(v => v.GetInt32())] : [],
        r.TryGetProperty("ignored", out var ignored) ? ParseResponse(ignored) : null,
        r.TryGetProperty("complete", out var complete) ? ParseResponse(complete) : null);

    private static Response ParseResponse(JsonElement r) => new(
        r.TryGetProperty("say", out var say) ? say.GetInt32() : 0,
        r.TryGetProperty("buff", out var buff) ? buff.GetInt32() : 0,
        r.TryGetProperty("on", out var on) ? on.GetInt32() : 0);

    private static IReadOnlyList<StepTrigger> Triggers(JsonElement step, string field)
    {
        if (!step.TryGetProperty(field, out var list)) return [];
        var triggers = new List<StepTrigger>();
        foreach (var t in list.EnumerateArray())
        {
            var kinds = Enum.GetValues<TriggerKind>().Where(k => t.TryGetProperty(k.ToString().ToLowerInvariant(), out _)).ToArray();
            if (kinds.Length != 1) throw new InvalidOperationException($"A scripted trigger names {kinds.Length} kinds: {t}");
            var kind = kinds[0];
            triggers.Add(new StepTrigger(kind, t.GetProperty(kind.ToString().ToLowerInvariant()).GetInt64(),
                t.TryGetProperty("radius", out var r) ? r.GetSingle() : 0));
        }
        return triggers;
    }
}
