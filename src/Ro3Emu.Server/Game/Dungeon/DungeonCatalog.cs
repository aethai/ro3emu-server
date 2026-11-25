using System.Text.Json;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;
using Romsg;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Client dungeon joins: DungeonEntranceConfig -> TenGameplayConfig._kBoss[markId][2] ->
/// TenGameplayBossConfig._iDungeon -> DungeonConfig/map/editor (Lua_CFG_TenGameplayConfig.lua:107-129).
/// Mark is the one-based array position, not tuple[1] (the fourth raid tuple also says 3).
/// </summary>
public sealed class DungeonCatalog(GameData data, WorldData worldData, MonsterService monsters,
    SeasonService seasons, IOptions<ServerOptions> options, ReconstructedDungeons? reconstructed = null)
{
    public sealed record Spawn(int NpcId, MapPoint Point, IReadOnlyList<(float X, float Y, float Z)>? Route = null);
    /// <summary>
    /// A wave; <paramref name="Quest"/> is the DungeonQuestConfig quest it plays (0: none known). A scripted step
    /// (<paramref name="Step"/>, ReconstructedDungeons.Scripted) has no monsters: it completes on its client events.
    /// </summary>
    /// <summary>The scene a scripted scene continues into after its clear, 0 when none.</summary>
    public long ScriptedContinuation(long scene) => reconstructed?.Scripted(scene)?.ContinuesTo ?? 0;

    /// <summary>Milliseconds a scripted scene waits after its clear before the scene that continues it (0: the default).</summary>
    public int ScriptedContinueAfterMs(long scene) => reconstructed?.Scripted(scene)?.ContinueAfterMs ?? 0;

    /// <summary>Whether a scripted scene returns the player to the field after its clear.</summary>
    public bool ScriptedLeavesAfterClear(long scene) => reconstructed?.Scripted(scene)?.LeavesAfterClear ?? false;

    public sealed record Wave(int Number, IReadOnlyList<Spawn> Spawns, int Quest = 0, ReconstructedDungeons.ScriptedStep? Step = null);
    /// <summary><paramref name="Layer"/>: a Valhalla Trials run (HeroicSpiritLayerConfig row), see ValhallaService.</summary>
    public sealed record Plan(JsonElement Config, JsonElement? Multi, JsonElement? Boss, int Mark,
        int MinPlayers, int MaxPlayers, IReadOnlyList<Wave> Waves, JsonElement? Layer = null)
    {
        public long LayerId => Layer is { } row ? new HeroicSpiritLayerConfig(row).Id : 0;
        public long SceneEntry => new DungeonConfig(Config).Id;
        public int MultiId => Multi is { } row ? (int)new TenGameplayConfig(row).Id : 0;
        public int DurationMs => (int)Math.Clamp(new DungeonConfig(Config).LimitTime, 1000, int.MaxValue);
        /// <summary>A Hidden Realm run: the random entry (TenGameplayConfig _iIsRandomDungeon) this boss was drawn for.</summary>
        public RandomOrigin? Origin { get; init; }
        /// <summary>The entry whose rewards, allowance and record the run counts against: the Hidden Realm's own row when
        /// drawn from it (inferred: its boss row 4300099 carries the awards and _iMatchID), else the fought boss's.</summary>
        public JsonElement? RewardMulti => Origin?.Multi ?? Multi;
        public JsonElement? RewardBoss => Origin?.Boss ?? Boss;
        public int RewardMark => Origin?.Mark ?? Mark;
        public int RewardMultiId => RewardMulti is { } row ? (int)new TenGameplayConfig(row).Id : 0;
        public DungeonInitData Init(long teamId = 0, long instanceId = 0)
        {
            var init = LayerId != 0
                // The client never reads it (LC_DunGeon_Valhalla); filled as the proto defines it (inferred).
                ? new DungeonInitData { HeroicSpiritDungeonInitData = new HeroicSpiritDungeonInitData { ChallengeId = LayerId } }
                : MultiId == 0 ? new DungeonInitData()
                : new DungeonInitData
                {
                    MultiDungeonInitData = new MultiDungeonInitData
                    { MultiDungeonId = MultiId, MarkId = Mark, TeamID = teamId, DungeonId = instanceId }
                };
            if (Origin is { } origin && init.MultiDungeonInitData != null)
                init.MultiDungeonInitData.Origin = new MultiDungeonInfo { MultiDungeonId = new TenGameplayConfig(origin.Multi).Id, MarkId = origin.Mark };
            return init;
        }
    }

    public sealed record RandomOrigin(JsonElement Multi, JsonElement Boss, int Mark);

    public IEnumerable<JsonElement> MultiRows => data.Table("TenGameplayConfig").Values
        .Where(r => TowerService.FitsPlayBook(r, seasons.PlayBook));

    public JsonElement? Boss(JsonElement multi, int mark)
    {
        var bosses = new TenGameplayConfig(multi).Boss;
        if (mark < 1 || mark > bosses.Length) return null;
        var tuple = bosses[mark - 1];
        return tuple.Length >= 2 ? data.Row("TenGameplayBossConfig", tuple[1]) : null;
    }

    public static int Marks(JsonElement multi) => new TenGameplayConfig(multi).Boss.Length;

    public JsonElement? Refresh(JsonElement multi)
    {
        long group = new TenGameplayConfig(multi).Group;
        return TenGameplayRefreshTimeConfig.All(data).Where(r => r.GroupId == group).Select(r => (JsonElement?)r.Row).FirstOrDefault();
    }

    /// <summary>Group/family accounting is (inferred); Normal and Hard raw counters are distinct in the Lua DAL.</summary>
    public static string AllowanceKey(JsonElement multi, int mark)
    {
        var cfg = new TenGameplayConfig(multi);
        return $"{cfg.Group}:{cfg.Boss[mark - 1][2]}:{cfg.Difficulty}";
    }

    /// <summary>Lua_CFG_TenGameplayConfig:169-204 matches _iType, not _iRelation. Lowest-id tie order is (inferred).</summary>
    public JsonElement? HardCounterpart(JsonElement multi)
    {
        long type = new TenGameplayConfig(multi).Type;
        return MultiRows.Select(r => new TenGameplayConfig(r))
            .Where(r => r.Type == type && r.Difficulty == 2)
            .OrderBy(r => r.Id).Select(r => (JsonElement?)r.Row).FirstOrDefault();
    }

    public Plan? Resolve(EnterDungeonReq req)
    {
        if (DungeonConfig.Get(data, req.SceneEntry) is not { } config
            || worldData.Map(req.SceneEntry) is not { Born: not null }) return null;
        // Separate owners handle tower, PvP, AFK and activity init branches; never accept forged init data.
        var init = req.InitData?.Clone() ?? new DungeonInitData();
        var selected = init.MultiDungeonInitData;
        init.MultiDungeonInitData = null;
        if (init.CalculateSize() != 0 || req.Param != null && req.Param.CalculateSize() != 0) return null;
        JsonElement? multi = null, boss = null;
        int mark = 0, min = 1, max = (int)config.RoleCntLimit;
        long mode = config.GamePlayType, kind = config.DungeonType;
        if (selected != null)
        {
            multi = MultiRows.Where(r => new TenGameplayConfig(r).Id == selected.MultiDungeonId).Select(r => (JsonElement?)r).FirstOrDefault();
            if (multi == null || selected.IsMatch || selected.NeedCreateTeam || selected.Origin != null) return null;
            mark = selected.MarkId;
            boss = Boss(multi.Value, mark);
            if (boss == null || new TenGameplayBossConfig(boss.Value).Dungeon != req.SceneEntry) return null;
            var limits = new TenGameplayConfig(multi.Value).Limit;
            min = limits.Length > 0 ? (int)limits[0] : 1;
            max = Math.Min(max, limits.Length > 1 ? (int)limits[1] : max);
        }
        // A scripted scene may also be a DanceParty (26: the opening dance, 1010601), which the client runs with the
        // plain lc_dungeon_base like an ordinary dungeon (GamePlayConfig.lua:136-140).
        else if (kind is not (1 or 2) || mode is not (6 or 24) && !(mode == 26 && reconstructed?.Scripted(req.SceneEntry) != null)
            || TenGameplayBossConfig.All(data).Any(r => r.Dungeon == req.SceneEntry)) return null;
        // A Journey scene (24) rebuilt as a scripted scene plays its steps instead of the bare point respawns.
        var waves = mode == 24 && selected == null && reconstructed?.Scripted(req.SceneEntry) == null
            ? JourneySpawns(config) : Spawns(config, boss, worldData.Map(req.SceneEntry)!);
        return max >= min && waves.Count > 0 ? new Plan(config.Row, multi, boss, mark, min, max, waves) : null;
    }

    /// <summary>
    /// Whale's NpcLevelPointRespawnConfig supplies per-difficulty templates at authored editor points.
    /// Camp 2 whale/platform actors and the post-boss NPC are not enemies.
    /// (inferred) Defeating the explicit target completes this stage; the hidden KunBenBoss_GVE server script
    /// is not exported. A map without an authored combat target is refused, never auto-cleared.
    /// </summary>
    internal Plan? GuildChallenge(long scene, int limit)
    {
        if (DungeonConfig.Get(data, scene) is not { GamePlayType: 19 } config
            || worldData.Map(scene) is not { Born: not null }) return null;
        string editor = config.ResSceneEditorName;
        if (SceneEditorFile(editor) is not { } path) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("m_kMonsterSpawnPoints", out var points)) return null;
        int difficulty = GuildChallengeConfig.All(data).Where(r => r.DungeonId.Contains(scene)).Select(r => (int)r.Level).Single();
        var spawns = new List<Spawn>();
        foreach (var binding in NpcLevelPointRespawnConfig.All(data)
            // (inferred) The matching level selects the difficulty's boss. Level 0 is an alternate legacy
            // easy template; level -1 contains script-controlled mechanisms/post-boss NPCs, not an initial wave.
            .Where(r => r.Mapid == scene && r.Level == difficulty)
            .OrderBy(r => r.Respawnpointid))
        {
            int id = (int)binding.Npcid;
            string pointId = binding.Respawnpointid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!points.TryGetProperty(pointId, out var point) || GameData.Int(point, "monsterType") != 0
                || NPCConfig.Get(data, id) is not { NPCType: 1, Camp: 3, NoSelected: 0 }
                || monsters.Template(id) == null) continue;
            var p = point.GetProperty("position");
            spawns.Add(new(id, new MapPoint(WorldNpc.Float(p, "x"), WorldNpc.Float(p, "y"), WorldNpc.Float(p, "z"), WorldNpc.Float(point, "direction"))));
        }
        // (inferred) Recommended Players is advisory; no configured minimum was recovered. Solo entry is allowed.
        return spawns.Count == 0 ? null : new Plan(config.Row, null, null, 0, 1,
            Math.Min(limit, (int)config.RoleCntLimit), [new Wave(1, spawns)]);
    }

    /// <summary>
    /// One boss at a uniquely named "BOSS" point, else at the first ungrouped server spawn point (inferred: the
    /// encounter scripts that placed bosses are not in the client). 3020101 has no ungrouped point but authors one
    /// exact "BOSS" point (93, group 90).
    /// </summary>
    private IReadOnlyList<Wave> BossWave(int npc, JsonElement[] serverPoints, Func<JsonElement, MapPoint> place)
    {
        var tagged = serverPoints.Where(p => p.TryGetProperty("name", out var label)
            && label.ValueKind == JsonValueKind.String && string.Equals(label.GetString(), "BOSS", StringComparison.OrdinalIgnoreCase)).ToArray();
        var point = tagged.Length == 1 ? tagged[0] : serverPoints.FirstOrDefault(p => GameData.Int(p, "GroupId") == 0);
        return point.ValueKind == JsonValueKind.Object && monsters.Template(npc) != null
            ? [new Wave(1, [new Spawn(npc, place(point))])] : [];
    }

    /// <summary>
    /// A Valhalla Trials layer (HeroicSpiritLayerConfig): its dungeon (_iGamePlayType 23) with the layer's boss,
    /// _kMonsterPreview[0], as the only enemy, placed as <see cref="BossWave"/> does (inferred: the client names the
    /// boss but not its spawn point; every spawn point of these scenes has npcId 0). Solo: the panel challenges alone,
    /// although DungeonConfig allows 10 (inferred).
    /// </summary>
    internal Plan? Valhalla(JsonElement layerRow)
    {
        var layer = new HeroicSpiritLayerConfig(layerRow);
        long scene = layer.DungeonId;
        if (DungeonConfig.Get(data, scene) is not { GamePlayType: 23 } config
            || worldData.Map(scene) is not { Born: not null } || layer.MonsterPreview.Length == 0) return null;
        string editor = config.ResSceneEditorName;
        if (SceneEditorFile(editor) is not { } path) return null;
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("m_kMonsterSpawnPoints", out var points) || points.ValueKind != JsonValueKind.Object) return null;
        var serverPoints = points.EnumerateObject().Select(p => p.Value.Clone())
            .Where(p => GameData.Int(p, "monsterType") == 0 && p.TryGetProperty("position", out _))
            .OrderBy(p => GameData.Int(p, "Id")).ToArray();
        var waves = BossWave((int)layer.MonsterPreview[0], serverPoints, p =>
        {
            var v = p.GetProperty("position");
            return new MapPoint(WorldNpc.Float(v, "x"), WorldNpc.Float(v, "y"), WorldNpc.Float(v, "z"), WorldNpc.Float(p, "direction"));
        });
        return waves.Count == 0 ? null : new Plan(config.Row, null, null, 0, 1, 1, waves, layerRow);
    }

    /// <summary>Journey action 407 maps to the authored NPC/map/point relation, not a guessed scene actor.</summary>
    private IReadOnlyList<Wave> JourneySpawns(DungeonConfig config)
    {
        long scene = config.Id;
        if (!JourneyActConfig.All(data).Any(r => r.Action is { ValueKind: JsonValueKind.Array } actions
            && actions.GetArrayLength() > 0 && actions[0].ValueKind == JsonValueKind.Array
            && actions[0].GetArrayLength() >= 3 && GameData.Number(actions[0][0]) == 407
            && GameData.Number(actions[0][1]) == scene)) return [];
        if (worldData.Map(scene) is not { } map) return [];
        var rows = NpcLevelPointRespawnConfig.All(data)
            .Where(r => r.Mapid == scene && r.Npcid > 0).OrderBy(r => r.Id).ToArray();
        var spawns = new List<Spawn>();
        foreach (var row in rows)
        {
            long pointId = row.Respawnpointid;
            int npcId = (int)row.Npcid;
            if (!map.Points.TryGetValue(pointId, out var point) || monsters.Template(npcId) == null) return [];
            spawns.Add(new Spawn(npcId, point));
        }
        return spawns.Count > 0 ? [new Wave(1, spawns)] : [];
    }

    private string? SceneEditorFile(string editor) =>
        DataFiles.Find(Path.Combine(options.Value.GameDataDir ?? options.Value.DataDir, "sceneeditor"), editor + ".json");

    /// <summary>
    /// Scene-editor points carry positions, not a recovered server encounter script. The selected multi-boss at
    /// a uniquely named BOSS point, else the first ungrouped server spawn point, and sequential groups of
    /// explicit server NPCs in story maps, are
    /// (inferred). Client-only monsterType=1 actors and zero-template mechanic points are never spawned as enemies.
    /// NpcLevelPointRespawnConfig supplies NPC ids for otherwise zero-template editor points. The one exported
    /// NpcLevelRegionRespawnConfig scene supplies a weighted monster group and region geometry; evenly spreading
    /// its configured count along authored inner points is (inferred) until the server placement algorithm is known.
    /// No phase, wave delay, hidden reward or boss mechanic is fabricated from DungeonPhaseConfig's UI descriptions.
    /// </summary>
    private IReadOnlyList<Wave> Spawns(DungeonConfig config, JsonElement? boss, MapWorld map)
    {
        string editor = config.ResSceneEditorName;
        if (string.IsNullOrEmpty(editor) || SceneEditorFile(editor) is not { } path) return [];
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("m_kMonsterSpawnPoints", out var points) || points.ValueKind != JsonValueKind.Object) return [];
        var serverPoints = points.EnumerateObject().Select(p => p.Value)
            .Where(p => GameData.Int(p, "monsterType") == 0 && p.TryGetProperty("position", out _))
            .OrderBy(p => GameData.Int(p, "Id")).ToArray();
        MapPoint Point(JsonElement p)
        {
            var v = p.GetProperty("position");
            return new MapPoint(WorldNpc.Float(v, "x"), WorldNpc.Float(v, "y"), WorldNpc.Float(v, "z"), WorldNpc.Float(p, "direction"));
        }
        // A scripted story scene without a fight (inferred, see ReconstructedDungeons.Scripted): steps, not monsters.
        if (boss == null && reconstructed?.Scripted(config.Id) is { } scripted)
            return ScriptedWaves(scripted, map);
        // A reconstructed story encounter (inferred, see ReconstructedDungeons) replaces the unresolvable
        // server-only templates: client-known monsters at authored points, one wave per quest stage.
        if (boss == null && reconstructed?.For(config.Id) is { } rebuilt)
        {
            var waves = new List<Wave>();
            foreach (var wave in rebuilt.Waves)
            {
                var spawns = new List<Spawn>();
                foreach (var s in wave.Spawns)
                {
                    if (!points.TryGetProperty(s.Point.ToString(System.Globalization.CultureInfo.InvariantCulture), out var p)
                        || GameData.Int(p, "monsterType") != 0 || monsters.Template(s.NpcId) == null) return [];
                    spawns.Add(new Spawn(s.NpcId, Point(p)));
                }
                waves.Add(new Wave(waves.Count + 1, spawns, wave.Quest));
            }
            return waves;
        }
        if (boss is { } b) return BossWave((int)new TenGameplayBossConfig(b).Boss, serverPoints, Point);
        var explicitPoints = serverPoints.Where(p => GameData.Int(p, "npcId") > 0).ToArray();
        if (explicitPoints.Any(p => monsters.Template((int)GameData.Int(p, "npcId")) == null)) return [];
        if (explicitPoints.Length > 0)
            return [.. explicitPoints.GroupBy(p => GameData.Int(p, "GroupId")).OrderBy(g => g.Key)
                .Select((g, i) => new Wave(i + 1, [.. g.Select(p => new Spawn((int)GameData.Int(p, "npcId"), Point(p)))]))];

        var mapped = new List<Spawn>();
        foreach (var row in NpcLevelPointRespawnConfig.All(data).Where(r => r.Mapid == config.Id))
        {
            int npc = (int)row.Npcid;
            if (monsters.Template(npc) == null) continue; // exported rows also name noncombat scene actors
            string id = row.Respawnpointid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!points.TryGetProperty(id, out var point) || GameData.Int(point, "monsterType") != 0) return [];
            mapped.Add(new Spawn(npc, Point(point)));
        }

        foreach (var row in NpcLevelRegionRespawnConfig.All(data).Where(r => r.Mapid == config.Id))
        {
            int regionId = (int)row.Respawnregionid;
            if (!doc.RootElement.TryGetProperty("m_kRegins", out var regions)
                || !regions.TryGetProperty(regionId.ToString(System.Globalization.CultureInfo.InvariantCulture), out var region)
                || !region.TryGetProperty("innerPoints", out var inner) || inner.ValueKind != JsonValueKind.Object)
                return [];
            var inside = inner.EnumerateObject().OrderBy(p => int.TryParse(p.Name, out int n) ? n : int.MaxValue)
                .Select(p => p.Value).ToArray();
            if (inside.Length == 0 || row.Npcrespawngroup.Length == 0) return [];
            foreach (long groupId in row.Npcrespawngroup)
            {
                if (NpcRespawnGroupConfig.Get(data, groupId) is not { } group || group.Npcgroup.Length != 1)
                    return []; // multi-NPC weight selection is unproven
                int npc = (int)group.Npcgroup[0];
                int count = (int)group.Byweightnum;
                if (monsters.Template(npc) == null || count is < 1 or > 100) return [];
                // Region inner points are authored positions. Interpolation is (inferred); all samples remain
                // on an authored segment, and the map grounds them against its navigation layer at spawn.
                for (int i = 0; i < count; i++)
                {
                    double t = count == 1 ? 0 : (double)i * (inside.Length - 1) / (count - 1);
                    int a = (int)t, next = Math.Min(a + 1, inside.Length - 1);
                    double f = t - a;
                    float Lerp(string axis) => (float)(WorldNpc.Float(inside[a], axis) * (1 - f) + WorldNpc.Float(inside[next], axis) * f);
                    mapped.Add(new Spawn(npc, map.Grounded(new MapPoint(Lerp("x"), Lerp("y"), Lerp("z"),
                        WorldNpc.Float(region, "monsterDirection")))));
                }
            }
        }
        return mapped.Count > 0 ? [new Wave(1, mapped)] : [];
    }

    /// <summary>
    /// A scripted scene's steps (ReconstructedDungeons.Scripted): one wave without monsters per step. Every reference
    /// must resolve in the client data and the scene's map, or the scene is refused like any unresolved encounter:
    /// the quests the tracker shows, the actors' NPCConfig rows, points and buffs, the companions' rows (each one able to
    /// fight beside the player), the stories, regions and points, and the interactions and dungeon messages the step's
    /// actors can actually send.
    /// </summary>
    private IReadOnlyList<Wave> ScriptedWaves(ReconstructedDungeons.ScriptedScene scene, MapWorld map)
    {
        var waves = new List<Wave>();
        foreach (var step in scene.Steps)
        {
            if (!DungeonQuestConfig.All(data).Any(q => q.DungeonId == scene.Scene && q.QuestId == step.Quest)) return [];
            foreach (var actor in step.Actors)
                if (NPCConfig.Get(data, actor.NpcId) is not { NPCType: 1 or 2 }
                    || !map.Points.ContainsKey(actor.Point)
                    || actor.Buffs.Any(b => BuffConfig.Get(data, b) is not { BuffId: > 0 })) return [];
            // A companion is a client NPC that can fight beside the player (CompanionUnit.Build); it carries no buffs.
            if (step.Companions.Any(c => !map.Points.ContainsKey(c.Point) || c.Buffs.Count > 0
                    || CompanionUnit.Build(data, monsters, c.NpcId) == null)) return [];
            if (step.Story != 0 && PlotInterpretation.Get(data, step.Story) == null) return [];
            if (step.TeleportAfter != 0 && !map.Points.ContainsKey(step.TeleportAfter)) return [];
            if (step.HoldAfterMs is < 0 or > 60000) return [];
            // A ride needs its route, an actor that walks (a UnitConfig speed) and an actor interaction that completes the step.
            if (step.RideAfter != 0 && (!map.GuidePaths.ContainsKey(step.RideAfter)
                || !step.Complete.Any(t => t.Kind is ReconstructedDungeons.TriggerKind.Interaction or ReconstructedDungeons.TriggerKind.Message)
                || !step.Actors.Any(a => NPCConfig.Get(data, a.NpcId) is { } n && UnitConfig.Get(data, n.UnitId) is { Speed: > 0 }))) return [];
            foreach (var t in step.Complete.Concat(step.Ignore))
            {
                bool known = t.Kind switch
                {
                    ReconstructedDungeons.TriggerKind.Interaction => step.Actors.Any(a => Interactions(a.NpcId).Contains(t.Id)),
                    ReconstructedDungeons.TriggerKind.Message => step.Actors.Any(a => Interactions(a.NpcId).Any(i => DungeonMessage(i) == t.Id)),
                    ReconstructedDungeons.TriggerKind.Region => map.Regions.ContainsKey(t.Id),
                    ReconstructedDungeons.TriggerKind.Point => map.Points.ContainsKey(t.Id) && t.Radius > 0,
                    ReconstructedDungeons.TriggerKind.Plot => PlotInterpretation.Get(data, t.Id) != null,
                    _ => false,
                };
                if (!known) return [];
            }
            if (step.Seek is { } seek && (step.Actors.Count == 0 || seek.Rounds <= 0 || seek.SearchedRadius < 0
                    || step.Complete.Count == 0 || step.Complete.Any(t => t.Kind != ReconstructedDungeons.TriggerKind.Point)
                    || seek.Opening.Append(seek.Round).Append(seek.Countdown).Append(seek.Longer).Append(seek.Won).Append(seek.Found)
                        .Any(chat => ChatConfig.Get(data, chat) == null))) return [];
            if (step.Shelter is { } shelter && (!step.Fight.Any(f => f.NpcId == shelter.Boss) || !step.Actors.Any(a => a.NpcId == shelter.Cover)
                    || ChatConfig.Get(data, shelter.Tip) == null || SkillConfig.Get(data, shelter.Skill) == null
                    || shelter.EveryMs <= shelter.WarnMs || shelter.WarnMs < 0 || shelter.Width <= 0 || shelter.Depth <= 0)) return [];
            if (step.React is { } react && !ReactionResolves(step, react)) return [];
            if (step.Cannon is { } cannon && (step.Fight.Count == 0 || SkillConfig.Get(data, cannon.Fire) == null
                    || !step.Actors.Any(a => Interactions(a.NpcId).Contains(cannon.Interaction)))) return [];
            var fight = new List<Spawn>();
            foreach (var spawn in step.Fight)
            {
                if (monsters.Template(spawn.NpcId) == null) return [];
                if (spawn.Route != 0)
                {
                    // A flying unit starts where its patrol starts (DungeonService.FlyAsync).
                    if (!map.PatrolPaths.TryGetValue(spawn.Route, out var route)) return [];
                    fight.Add(new Spawn(spawn.NpcId, new MapPoint(route[0].X, route[0].Y, route[0].Z, 0), route));
                    continue;
                }
                if (!map.Points.TryGetValue(spawn.Point, out var at)) return [];
                fight.Add(new Spawn(spawn.NpcId, at));
            }
            waves.Add(new Wave(waves.Count + 1, fight, step.Quest, step));
        }
        // An unplayed quest is one of the scene's own quests that no step shows.
        if (scene.Unplayed.Any(q => scene.Steps.Any(s => s.Quest == q)
            || !DungeonQuestConfig.All(data).Any(r => r.DungeonId == scene.Scene && r.QuestId == q))) return [];
        return waves;
    }

    /// <summary>A step's reactions: its speaker and every reacting actor are actors of the step, its lines ChatConfig rows
    /// and its effects BuffConfig rows of a buff group.</summary>
    private bool ReactionResolves(ReconstructedDungeons.ScriptedStep step, ReconstructedDungeons.Reaction react)
    {
        bool Shown(int npc) => step.Actors.Any(a => a.NpcId == npc);
        bool Line(int chat) => data.Row("ChatConfig", chat) != null;
        bool Resolves(ReconstructedDungeons.Response? r) => r == null
            || (r.Say == 0 || Line(r.Say)) && (r.Buff == 0
                || Shown(r.On) && Data.Config.BuffConfig.Get(data, r.Buff) is { BuffId: > 0 });
        return Shown(react.Speaker) && react.Start.All(Line) && Resolves(react.Ignored) && Resolves(react.Complete);
    }

    /// <summary>
    /// The lair's four fights, in task order (TaskConfig 9000101/103/105/107 "Defeat the Marionette / Wraith /
    /// Doppelganger / Wind Ghost"): the elite (its NpcLevelPointRespawnConfig point is 1号..4号精英怪, elite 1 to 4), the
    /// scene-editor region the preceding chase ends in (406 傀儡娃娃, 407 恶灵, 408 死灵, 409 风魔巫师 任务区域: the
    /// elite's "task region") and its m_kGroups (118 第一波怪 "first wave" to 121 "fourth wave"; the third wave is
    /// 120, 280 and 281). Joining them by their names is (inferred); every id is client data.
    /// </summary>
    internal static readonly (int Elite, long Region, int[] Groups)[] DraculaFights =
        [(4114025, 406, [118]), (4114027, 407, [119]), (4114029, 408, [120, 280, 281]), (4114031, 409, [121])];
    /// <summary>Region 410 德古拉任务区域 (Dracula's task region), where the last chase ends and Dracula fights.</summary>
    internal const long DraculaRegion = 410;
    /// <summary>The four "Search" bushes (6000018-6000021) and the "Unlock" cage (6000023, condition 7000039: the key
    /// buff 4121301), NPCConfig type 2 bound to points 186/200/228/250/293 (NpcLevelPointRespawnConfig, NotGroundBorn).</summary>
    internal static readonly int[] DraculaBushes = [4114033, 4114034, 4114035, 4114036];
    internal const int DraculaCage = 4114038, DraculaUnlock = 6000023;

    /// <summary>
    /// Dracula's Lair (DungeonConfig 3010401, gameplay 31 DraculaWedding) for DraculaWeddingService: one wave per dungeon
    /// task of the lair (TaskConfig 9000100-9000110: _iDungeonID 3010401, a _kPreTask chain alternating "chase Dracula"
    /// and "defeat ..."). A chase is a scripted step that completes when a participant walks into the next foe's task
    /// region (<see cref="DraculaFights"/>); a fight spawns the elite at its point with its wave groups, each point's
    /// monster bound by NpcLevelPointRespawnConfig (Deviruchi, Whisper, Nightmare, Ghoul). Dracula
    /// (PublicParamConfig.DraculaNpcConfigId 4114015) fights at the centre of region 410 (inferred: his own point 127 is
    /// beside the entrance, where the lair story 3003 shows him taking the bride). The last step shows the bushes and the
    /// cage and completes on the cage's Unlock. The mines, falling rocks and bats of the chases (4114004-4114014,
    /// unselectable, AI 25000009) need the original server script and are not spawned. Null when any reference is missing.
    /// </summary>
    internal Plan? Dracula(long scene)
    {
        if (DungeonConfig.Get(data, scene) is not { GamePlayType: 31 } config
            || worldData.Map(scene) is not { Born: not null } map) return null;
        var bound = NpcLevelPointRespawnConfig.All(data).Where(r => r.Mapid == scene)
            .GroupBy(r => r.Respawnpointid).ToDictionary(g => g.Key, g => (int)g.First().Npcid);
        Spawn? At(long point) => bound.TryGetValue(point, out int npc) && map.Points.TryGetValue(point, out var p)
            && monsters.Template(npc) != null ? new Spawn(npc, p) : null;
        ReconstructedDungeons.ScriptedStep Chase(long region) => new($"chase to region {region}", 0, (0, 1), [], 0,
            [new(ReconstructedDungeons.TriggerKind.Region, region)], [], 0, [], false);
        var waves = new List<Wave>();
        foreach (var (elite, region, groups) in DraculaFights)
        {
            if (!map.Regions.ContainsKey(region)) return null;
            waves.Add(new Wave(waves.Count + 1, [], 0, Chase(region)));
            var spawns = new List<Spawn>();
            long elitePoint = bound.Where(p => p.Value == elite).Select(p => p.Key).SingleOrDefault();
            if (At(elitePoint) is not { } boss) return null;
            spawns.Add(boss);
            foreach (int group in groups)
            {
                if (!map.DungeonGroups.TryGetValue(group, out var members) || members.Count == 0) return null;
                foreach (var member in members)
                    if (At(member.Id) is { } minion) spawns.Add(minion);
                    else return null;
            }
            waves.Add(new Wave(waves.Count + 1, spawns));
        }
        int dracula = (int)(Data.Config.PublicParams.Of(data).DraculaNpcConfigId ?? 0);
        if (!map.Regions.TryGetValue(DraculaRegion, out var lair) || monsters.Template(dracula) == null) return null;
        waves.Add(new Wave(waves.Count + 1, [], 0, Chase(DraculaRegion)));
        waves.Add(new Wave(waves.Count + 1, [new Spawn(dracula, map.Grounded(new MapPoint(lair.X, lair.Y, lair.Z, lair.Direction)))]));
        var actors = new List<ReconstructedDungeons.SceneActor>();
        foreach (int npc in DraculaBushes.Append(DraculaCage))
        {
            long point = bound.Where(p => p.Value == npc).Select(p => p.Key).SingleOrDefault();
            if (!map.Points.ContainsKey(point) || NPCConfig.Get(data, npc) is not { NPCType: 2 }
                || Interactions(npc).Count == 0) return null;
            actors.Add(new(npc, point, []));
        }
        if (!Interactions(DraculaCage).Contains(DraculaUnlock)) return null;
        waves.Add(new Wave(waves.Count + 1, [], 0, new ReconstructedDungeons.ScriptedStep("find the key", 0, (0, 1), actors, 0,
            [new(ReconstructedDungeons.TriggerKind.Interaction, DraculaUnlock)],
            [.. DraculaBushes.SelectMany(Interactions).Select(i => new ReconstructedDungeons.StepTrigger(
                ReconstructedDungeons.TriggerKind.Interaction, i))], 0, [], false)));
        return new Plan(config.Row, null, null, 0, 1, (int)config.RoleCntLimit, waves);
    }

    /// <summary>A scene-editor point (m_kAllPoints) of the scene's map.</summary>
    /// <summary>The reconstructions (raid trials, scripted scenes), when registered.</summary>
    internal ReconstructedDungeons? Reconstructed => reconstructed;

    /// <summary>A scene's map (points, regions, dungeon point groups).</summary>
    internal MapWorld? Map(long scene) => worldData.Map(scene);

    public MapPoint? ScenePoint(long scene, long point) =>
        worldData.Map(scene)?.Points.TryGetValue(point, out var p) == true ? p : null;

    /// <summary>The guide routes (m_kGuidePoints) of the scene's scene editor file.</summary>
    public IReadOnlyList<long> GuideRoutes(long scene) => worldData.Map(scene)?.GuideRoutes ?? [];

    /// <summary>The points of a guide route of the scene (m_kGuidePoints), or null when it has none.</summary>
    public IReadOnlyList<(float X, float Y, float Z)>? GuidePath(long scene, long route) =>
        worldData.Map(scene)?.GuidePaths.TryGetValue(route, out var path) == true ? path : null;

    /// <summary>A scene-editor region (m_kRegins) of the scene's map.</summary>
    public MapRegion? SceneRegion(long scene, long region) =>
        worldData.Map(scene)?.Regions.TryGetValue(region, out var r) == true ? r : null;

    /// <summary>An NPCConfig row's _iInteractID list (the interactions the client offers on that unit).</summary>
    public IReadOnlyList<long> Interactions(int npcId) =>
        NPCConfig.Get(data, npcId) is { } npc ? npc.InteractId.Where(v => v > 0).ToArray() : [];

    /// <summary>
    /// The dungeon message an interaction sends: its NPCInteractConfig _iEXECAction ["210", id, ...]. ActionConfig 210
    /// SendDungeonMessage is server-hosted (_iEXECHost 1): the client never runs it, the dungeon script receives it.
    /// </summary>
    public long? DungeonMessage(long interactionId)
    {
        if (NPCInteractConfig.Get(data, interactionId)?.EXECAction is not { ValueKind: JsonValueKind.Array } actions)
            return null;
        foreach (var action in actions.EnumerateArray())
            if (action.ValueKind == JsonValueKind.Array && action.GetArrayLength() >= 2
                && GameData.Number(action[0]) == 210 && GameData.Number(action[1]) is { } id) return (long)id;
        return null;
    }
}
