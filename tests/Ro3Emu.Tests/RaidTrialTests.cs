using Google.Protobuf;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Raid boss trials (Reconstructed/raid-trials.json, DungeonService.RaidTrials), inferred as a whole: each raid is
/// entered alone (the !dungeonsolo test aid), its battle started and a trial started with the !raidtrial test aid, on the
/// real exported data with all content open (as the realm runs) and the boss specials on.</summary>
public sealed class RaidTrialTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-raid-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private Task OnLoop(Func<Task> work) => Service<GameLoop>().InvokeAsync(work);

    public async Task InitializeAsync()
    {
        var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.SeasonStart = "2026-10-08";
            o.OpenContent.Enabled = true;
            o.ScriptedScenePacePercent = 10;
            o.HttpPort = httpPort;
            o.MicroGatePort = microPort;
            o.GameGatePort = gamePort;
        }, TimeProvider.System);
        SureHitRolls.Install(_app);
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{httpPort}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id;
        await bot.EnterWorldAsync(id);
        return bot;
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    private async Task Gm(BotClient bot, string command)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={bot.Account}", new StringContent(command))).EnsureSuccessStatusCode();
    }

    private GameSession Session(BotClient bot) => Service<SessionRegistry>().All()
        .Single(s => s.Account == bot.Account && s.Role != null);

    private static EnterDungeonReq Request(int multi, int mark, long scene) => new()
    {
        SceneEntry = scene,
        InitData = new DungeonInitData { MultiDungeonInitData = new MultiDungeonInitData { MultiDungeonId = multi, MarkId = mark } },
    };

    private static async Task<long> Load(BotClient bot, long scene)
    {
        var sw = await Next(bot, MsgId.SwitchSceneLine, SwitchSceneLineResp.Parser, m => m.SceneEntry == scene);
        await bot.SendGameAsync(MsgId.SceneLoaded, new SceneLoadedReq { SceneId = scene });
        Assert.Equal(0, (await Next(bot, MsgId.SceneLoaded, SceneLoadedResp.Parser, m => m.SceneId == scene)).Result);
        await Next(bot, DungeonService.BaseInfo, DungeonBaseInfoNotify.Parser, m => m.DungeonId == sw.SceneID);
        return sw.SceneID;
    }

    private async Task<MonsterUnit> BossIn(long instance, int npc)
    {
        for (int i = 0; i < 150; i++)
        {
            var actor = await Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits()
                .OfType<MonsterUnit>().FirstOrDefault(m => m.InstanceId == instance && m.Template.NpcId == npc && !m.Dead)));
            if (actor != null) return actor;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Boss {npc} did not spawn");
    }

    // Every reference of every trial resolves in the client data: the bosses are raid bosses, their scene has the points
    // and regions, the tips, colours, buffs, NPCs and skills exist, a destructible object can be a monster, an
    // interaction belongs to its NPC, and a breaker or a forced cast is in the boss's own set.
    [ClientDataFact]
    public void EveryRaidTrialResolvesInTheClientData()
    {
        var data = Service<GameData>();
        var raids = Service<ReconstructedDungeons>().AllRaids;
        Assert.NotEmpty(raids);
        foreach (var raid in raids)
            foreach (int boss in raid.Bosses)
            {
                var rows = data.Table("TenGameplayBossConfig").Values.Where(r => GameData.Int(r, "_iBoss") == boss).ToArray();
                Assert.True(rows.Length > 0, $"{raid.Key}: {boss} is no raid boss");
                var book = Service<MonsterService>().Template(boss)?.Skills;
                foreach (long scene in rows.Select(r => GameData.Int(r, "_iDungeon")).Distinct())
                {
                    var map = Service<WorldData>().Map(scene)!;
                    foreach (var trial in raid.Trials)
                    {
                        string where = $"{raid.Key} '{trial.Name}' ({boss} in {scene})";
                        Assert.All(trial.Tips.Concat(trial.TipByCount.Values), tip => Assert.True(data.Row("ChatConfig", tip) != null, $"{where}: tip {tip}"));
                        Assert.All(trial.Colors.Append(trial.MarkBuff).Append(trial.Carry.Buff).Where(b => b != 0),
                            b => Assert.True(data.Row("BuffConfig", b) != null, $"{where}: buff {b}"));
                        foreach (var outcome in new[] { trial.Fail, trial.Success }.OfType<ReconstructedDungeons.TrialOutcome>())
                        {
                            Assert.True(outcome.Skill == 0 || data.Row("SkillConfig", outcome.Skill) != null, $"{where}: skill {outcome.Skill}");
                            Assert.True(outcome.Stagger == 0 || data.Row("SkillConfig", outcome.Stagger) != null, $"{where}: stagger {outcome.Stagger}");
                            Assert.True(outcome.Buff == 0 || data.Row("BuffConfig", outcome.Buff) != null, $"{where}: buff {outcome.Buff}");
                            Assert.True(outcome.Say == 0 || data.Row("ChatConfig", outcome.Say) != null, $"{where}: say {outcome.Say}");
                        }
                        Assert.True(trial.Show == 0 || data.Row("SkillConfig", trial.Show) != null, $"{where}: show {trial.Show}");
                        foreach (long skill in trial.Breakers.Concat(trial.Casts.Select(c => c.Skill)))
                            Assert.True(book?.Skills.Any(k => k.Id == skill) == true, $"{where}: {skill} is not in the boss's set");
                        foreach (var o in trial.Objects)
                        {
                            Assert.True(o.Npcs.Count == 1 || o.Npcs.Count == trial.Colors.Count, $"{where}: one NPC per colour");
                            foreach (int npc in o.Npcs)
                            {
                                Assert.True(data.Row("NPCConfig", npc) != null, $"{where}: NPC {npc}");
                                if (o.Monster) Assert.True(Service<MonsterService>().Template(npc) != null, $"{where}: {npc} cannot be a monster");
                                if (o.Use != ReconstructedDungeons.TrialUse.None)
                                    Assert.NotEmpty(Service<DungeonCatalog>().Interactions(npc));
                            }
                            Assert.All(o.Buffs.Append(o.MarkBuff).Where(b => b != 0), b => Assert.True(data.Row("BuffConfig", b) != null, $"{where}: buff {b}"));
                            Assert.All(o.Place.Points, p => Assert.True(map.Points.ContainsKey(p), $"{where}: point {p}"));
                            Assert.All(o.Place.Regions, r => Assert.True(map.Regions.ContainsKey(r), $"{where}: region {r}"));
                            Assert.True(o.Place.Group == 0 || map.DungeonGroups.ContainsKey(o.Place.Group), $"{where}: group {o.Place.Group}");
                        }
                    }
                }
            }
    }

    [Fact]
    public void ATrialLaneLiesAheadOfItsObject()
    {
        var unit = new DungeonService.SceneActorUnit
        {
            Uid = 1, SceneEntry = 1, X = 0, Z = 0, Face = 0, NpcId = 1, Point = 0, EntityType = 4, Camp = 0, Name = "",
        };
        var spec = new ReconstructedDungeons.TrialObject([1], new ReconstructedDungeons.TrialPlace([], [], 0, false, false, false, 0, 0, 0, 0, 0),
            [], false, 0, false, ReconstructedDungeons.TrialUse.None, false, 0, 0, (40, 6));
        var lane = new DungeonService.TrialUnit { Unit = unit, Spec = spec };
        Assert.True(DungeonService.InTrialArea(lane, 3, 0, 20)); // ahead along its facing (0 degrees: +z)
        Assert.True(DungeonService.InTrialArea(lane, 3, 2.9f, 39));
        Assert.False(DungeonService.InTrialArea(lane, 3, 3.5f, 20)); // beside the lane
        Assert.False(DungeonService.InTrialArea(lane, 3, 0, -10)); // behind it
        var circle = new DungeonService.TrialUnit { Unit = unit, Spec = spec with { Lane = null } };
        Assert.True(DungeonService.InTrialArea(circle, 3, 2, 2));
        Assert.False(DungeonService.InTrialArea(circle, 3, 3, 3));
    }

    /// <summary>A raid entered alone (test aid), its battle started and its boss fighting the player beside it.</summary>
    private async Task<(long Instance, MonsterUnit Boss)> RaidFight(BotClient bot, int multi, int mark, long scene, int boss)
    {
        await Gm(bot, "!lvl 99");
        await Gm(bot, "!dungeonsolo on");
        await bot.SendGameAsync(DungeonService.Enter, Request(multi, mark, scene));
        Assert.Equal(0, (await Next(bot, DungeonService.Enter, EnterDungeonRsp.Parser)).Result);
        long instance = await Load(bot, scene);
        if (DungeonService.StartBattleScenes.Contains(scene))
        {
            await bot.SendGameAsync(DungeonService.ModuleButton, new DungeonModuleButtonReq { ButtonId = 1 });
            Assert.Equal(0, (await Next(bot, DungeonService.ModuleButton, DungeonModuleButtonResp.Parser)).Result);
        }
        var actor = await BossIn(instance, boss);
        await OnLoop(async () =>
        {
            // The boss's own blows are not under test: they barely scratch, so the trials' hits are what the player feels.
            actor.Template = actor.Template with { Atk = 1, Matk = 1 };
            await Service<SceneService>().TeleportInMapAsync(Session(bot), new MapPoint(actor.X + 3, actor.Y, actor.Z, actor.Face));
            Service<MonsterService>().Engage(actor, Session(bot));
        });
        return (instance, actor);
    }

    private Task<T[]> InRoom<T>(long instance, Func<T, bool>? match = null) where T : WorldUnit =>
        Service<GameLoop>().InvokeAsync(() => Task.FromResult(Service<World>().AllUnits().OfType<T>()
            .Where(u => u.InstanceId == instance && (match == null || match(u))).ToArray()));

    private async Task<T[]> UntilInRoom<T>(long instance, Func<T, bool> match, int atLeast = 1) where T : WorldUnit
    {
        for (int i = 0; i < 150; i++)
        {
            var found = await InRoom(instance, match);
            if (found.Length >= atLeast) return found;
            await Task.Delay(20);
        }
        throw new TimeoutException("The trial's objects did not appear");
    }

    private Task<int> BuffOf(BotClient bot, IReadOnlyList<int> among) => Service<GameLoop>().InvokeAsync(() =>
        Task.FromResult(Session(bot).Buffs.Select(b => b.ConfigId).FirstOrDefault(among.Contains)));

    private static Task<SystemBoardCastMsgNtf> Tip(BotClient bot, int chat) =>
        Next(bot, 1022, SystemBoardCastMsgNtf.Parser, m => m.ChatId == chat);

    private static Task<SkillHitInfo> MechanicHit(BotClient bot, long skill) => Next(bot, CombatService.SkillHit, SkillHitInfo.Parser,
        h => h.SkillId == skill && h.Dsts.Any(d => d.DstID == (long)bot.Self!.Uuid));

    private long MaxHp(BotClient bot) => Service<CharacterService>().Stats(Session(bot))[101];

    // Isti's Ocean Concerto: a note for the player, bubbles of the notes in play; taking one of your note gives Bubble
    // Movement, each one left at the end costs everyone 10% of their maximum HP.
    [ClientDataFact]
    public async Task IstisConcertoRewardsTheMatchingBubbleAndPunishesTheOnesLeft()
    {
        int[] notes = [14720201, 14720501, 14720601, 14720701];
        int[] bubbles = [14720013, 14720014, 14720015, 14720016];
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 1, 3020101, 14720026);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4110011);
        int note = await BuffOf(alice, notes);
        Assert.NotEqual(0, note);
        int mine = bubbles[Array.IndexOf(notes, note)];
        var shown = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => bubbles.Contains(u.NpcId), 4);
        Assert.Equal(4, shown.Length); // two per player, at least four
        Assert.All(shown, b => Assert.Equal(mine, b.NpcId)); // alone, every bubble is of the note she holds
        var first = shown[0];
        await Gm(alice, FormattableString.Invariant($"!actors walk {first.X} {first.Z}"));
        Assert.Equal(14720301, await BuffOf(alice, [14720301])); // Bubble Movement
        Assert.Equal(3, (await InRoom<DungeonService.SceneActorUnit>(instance, u => bubbles.Contains(u.NpcId))).Length);
        long max = MaxHp(alice);
        var hit = await MechanicHit(alice, 14721101); // three bubbles reach the orb: 30%
        Assert.Equal(max * 30 / 100, hit.Dsts.Single().Damage);
        Assert.Empty(await InRoom<DungeonService.SceneActorUnit>(instance, u => bubbles.Contains(u.NpcId)));
        Assert.Equal(0, await BuffOf(alice, notes)); // the note ends with the concerto
    }

    private Task WalkTo(BotClient bot, WorldUnit at) => Gm(bot, FormattableString.Invariant($"!actors walk {at.X} {at.Z}"));

    // Nasha's dance test: a dancer in a circle when the 15 s run out keeps up (Perfect State and her praise).
    [ClientDataFact]
    public async Task NashasDancerInTheCircleKeepsUp()
    {
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 2, 3020102, 14740100);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4618);
        Assert.Equal("2", (await Tip(alice, 4611)).ParamList[1].Params[0]); // 15 s at the test pace: the window's seconds
        var circle = Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 14740002));
        Assert.Equal(4, circle.EntityType); // a trigger, as NPCConfig types it
        await WalkTo(alice, circle);
        await Tip(alice, 4616); // "someone who can keep up with my steps"
        Assert.Equal(14742501, await BuffOf(alice, [14742501])); // Perfect State
        Assert.Empty(await InRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 14740002));
    }

    // Ilmo's Beat Prison: the notes are picked up from their NPCs; every circle lit by its note raises the barrier.
    [ClientDataFact]
    public async Task IlmosBeatPrisonIsSolvedByLightingEveryCircleWithItsNote()
    {
        int[] pickups = [14760001, 14760002, 14760003, 14760004], circles = [14760005, 14760006, 14760007, 14760008];
        int[] notes = [14760001, 14760101, 14760201, 14760301];
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 3, 3020103, 14761100);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4606);
        var shown = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => circles.Contains(u.NpcId), 2);
        var notesShown = await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => pickups.Contains(u.NpcId), 4);
        // Walking into a circle without its note lights nothing.
        await WalkTo(alice, shown[0]);
        Assert.Equal(2, (await InRoom<DungeonService.SceneActorUnit>(instance, u => circles.Contains(u.NpcId))).Length);
        foreach (var circle in shown)
        {
            int color = Array.IndexOf(circles, circle.NpcId);
            var pickup = notesShown.Single(n => n.NpcId == pickups[color]);
            await WalkTo(alice, pickup);
            long interaction = Service<DungeonCatalog>().Interactions(pickup.NpcId).Single();
            await alice.SendGameAsync(SceneService.NpcInteraction, new NpcInteractionReq
            { NpcUid = pickup.Uid, InteractionId = (int)interaction, SceneConfigId = 3020103 });
            Assert.Equal(0, (await Next(alice, SceneService.NpcInteraction, NpcInteractionRsp.Parser, r => r.Req.NpcUid == pickup.Uid)).Result);
            for (int i = 0; i < 100 && await BuffOf(alice, notes) != notes[color]; i++) await Task.Delay(20);
            Assert.Equal(notes[color], await BuffOf(alice, notes));
            await WalkTo(alice, circle);
        }
        for (int i = 0; i < 100 && await BuffOf(alice, [14761401]) == 0; i++) await Task.Delay(20);
        Assert.Equal(14761401, await BuffOf(alice, [14761401])); // Green Energy Barrier
        Assert.Empty(await InRoom<DungeonService.SceneActorUnit>(instance, u => circles.Contains(u.NpcId) || pickups.Contains(u.NpcId)));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == 14769000);
    }

    // Mirko: a lone tester gets one safe zone (the two-zone line); leaving it empty lets the beat explode. Then the
    // instruments of her colour stand at the edge; left alive, each plays its full-field AOE.
    [ClientDataFact]
    public async Task MirkosEmptySafeZoneAndLiveInstrumentsCostHp()
    {
        await using var alice = await InWorld("Alice");
        var (instance, _) = await RaidFight(alice, 100101, 4, 3020108, 14780007);
        long max = MaxHp(alice);
        await Gm(alice, "!raidtrial 1");
        await Tip(alice, 4622);
        Assert.Single(await UntilInRoom<DungeonService.SceneActorUnit>(instance, u => u.NpcId == 14780006));
        Assert.Equal(max * 50 / 100, (await MechanicHit(alice, 14782101)).Dsts.Single().Damage);

        await Gm(alice, "!raidtrial 2");
        await Tip(alice, 4607);
        int note = await BuffOf(alice, [14788001, 14788101]);
        int horn = note == 14788001 ? 14780014 : 14780015;
        var instruments = await UntilInRoom<MonsterUnit>(instance, m => m.Template.NpcId is 14780014 or 14780015, 2);
        Assert.All(instruments, m => Assert.Equal(horn, m.Template.NpcId)); // alone: both of her colour
        Assert.All(instruments, m => Assert.Equal(0, m.Template.SpeedCm)); // they stand
        var aoe = await MechanicHit(alice, 14783501);
        Assert.Equal(max * 40 / 100, aoe.Dsts.Single().Damage); // two left: 2 x 20%
        Assert.Empty(await InRoom<MonsterUnit>(instance, m => m.Template.NpcId is 14780014 or 14780015 && !m.Dead));
    }
}
