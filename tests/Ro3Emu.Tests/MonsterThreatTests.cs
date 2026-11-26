using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Monster hatred (MonsterService.Hatred) on the synthetic data set: damage engages an idle monster, another player takes
/// the target only past HatredExceedValue, Provoke engages and holds, a heal earns its share, entries decline over the
/// time that passed and are dropped when stale or far (the target's never), and the top hatred buff follows the target.
/// Every value is invented.
/// </summary>
public sealed class MonsterThreatTests
{
    private const int BossNpc = 16329301;
    private const int BossAi = 16329302;
    private const int TopBuff = 99632901;
    private const int ExceedBp = 2000;
    private const int HealBp = 4000;
    private const int ClearTimeMs = 8000;
    private const int ClearDistanceM = 25;
    private const int DeclineBp = 2500;
    private const int DeclineMs = 2000;
    private const int MinDecline = 300;
    private const float PlayerX = 150f, PlayerZ = 186f;

    [Fact]
    public async Task DamageOnAnIdleMonsterEngagesIt()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();

        await f.OnLoopAsync(() => f.Monsters.AddHatred(monster, f.Tank, 1000));

        Assert.Same(f.Tank, monster.Target);
        Assert.Equal(1000, monster.ThreatTable[f.Tank].Value);
        Assert.True(monster.NextSwingMs > 0, "the fight starts through Engage");
    }

    [Fact]
    public async Task AnotherPlayerTakesTheTargetOnlyPastTheExceedMargin()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();

        await f.OnLoopAsync(() =>
        {
            f.Monsters.AddHatred(monster, f.Tank, 1000);
            f.Monsters.AddHatred(monster, f.Healer, 1000 * (10000 + ExceedBp) / 10000); // equal to the margin: not past it
        });
        Assert.Same(f.Tank, monster.Target);

        await f.OnLoopAsync(() => f.Monsters.AddHatred(monster, f.Healer, 1));
        Assert.Same(f.Healer, monster.Target);
    }

    [Fact]
    public async Task ProvokeOnAnIdleMonsterEntersTheFightThroughEngage()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();
        long now = 0;

        await f.OnLoopAsync(() =>
        {
            now = f.Server.Loop.NowMs;
            f.Monsters.Provoke(monster, f.Tank);
        });

        Assert.Same(f.Tank, monster.Target);
        Assert.InRange(monster.NextSwingMs, now + 1, long.MaxValue - 1);
        Assert.Equal(MonsterService.ProvokeHatredCushion, monster.ThreatTable[f.Tank].Value);
    }

    [Fact]
    public async Task ProvokeTakesTheTargetAndHoldsIt()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();

        await f.OnLoopAsync(() =>
        {
            f.Monsters.AddHatred(monster, f.Healer, 2000);
            f.Monsters.Provoke(monster, f.Tank);
        });
        Assert.Same(f.Tank, monster.Target);
        Assert.Equal(2000 + MonsterService.ProvokeHatredCushion, monster.ThreatTable[f.Tank].Value);

        await f.OnLoopAsync(() => f.Monsters.AddHatred(monster, f.Healer, MonsterService.ProvokeHatredCushion));
        Assert.Same(f.Tank, monster.Target);
    }

    [Fact]
    public async Task AHealEarnsItsShareOnTheMonstersThatHateTheHealedPlayer()
    {
        await using var f = await ThreatFixture.StartAsync();
        var fighting = await f.SpawnAsync();
        var idle = await f.SpawnAsync();

        await f.OnLoopAsync(() =>
        {
            f.Monsters.AddHatred(fighting, f.Tank, 1000);
            f.Monsters.AddHealHatred(f.Healer, f.Tank, 500);
        });

        Assert.Equal(500L * HealBp / 10000, fighting.ThreatTable[f.Healer].Value);
        Assert.Same(f.Tank, fighting.Target);
        Assert.Empty(idle.ThreatTable);
        Assert.Null(idle.Target);
    }

    [Fact]
    public async Task HatredDeclinesByTheTimeThatPassed()
    {
        await using var f = await ThreatFixture.StartAsync();
        var template = f.Monsters.Template(BossNpc)!;
        Assert.Equal((DeclineBp, DeclineMs, MinDecline), (template.HatredDeclineSpeed, template.HatredDeclineTimeMs, template.MinHatredDecline));

        var once = new MonsterUnit.ThreatRecord { Value = 10000 };
        MonsterService.Decline(template, once, DeclineMs - 1);
        Assert.Equal(10000, once.Value); // no step due yet
        MonsterService.Decline(template, once, 2 * DeclineMs + 100);
        Assert.Equal(5625, once.Value); // two steps of 25 %: 10000, 7500, 5625

        var stepwise = new MonsterUnit.ThreatRecord { Value = 10000 };
        for (long now = 0; now <= 2 * DeclineMs + 100; now += 50) MonsterService.Decline(template, stepwise, now);
        Assert.Equal(once.Value, stepwise.Value); // the tick rate does not matter

        var small = new MonsterUnit.ThreatRecord { Value = 200 };
        MonsterService.Decline(template, small, DeclineMs);
        Assert.Equal(0, small.Value); // the minimum step takes it to 0, never up to it
    }

    [Fact]
    public async Task AStaleEntryIsDroppedButTheTargetsIsNot()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();

        await f.OnLoopAsync(() =>
        {
            f.Monsters.AddHatred(monster, f.Tank, 1000);
            f.Monsters.AddHatred(monster, f.Healer, 500);
            long now = f.Server.Loop.NowMs;
            monster.ThreatTable[f.Tank].LastHitMs = now - ClearTimeMs;
            monster.ThreatTable[f.Healer].LastHitMs = now - ClearTimeMs;
            f.Monsters.Tick(monster, f.Server.Service<World>().Map(monster.WorldScene));
        });

        Assert.Same(f.Tank, monster.Target);
        Assert.True(monster.ThreatTable.ContainsKey(f.Tank));
        Assert.False(monster.ThreatTable.ContainsKey(f.Healer));
    }

    [Fact]
    public async Task AFarEntryIsDroppedButTheTargetWithinItsLeashIsNot()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();

        await f.OnLoopAsync(() =>
        {
            f.Monsters.AddHatred(monster, f.Tank, 1000);
            f.Monsters.AddHatred(monster, f.Healer, 500);
            // Both beyond HateClearDistance (25 m) from the monster, within its leash (30 m around its home).
            f.Tank.Position.X = monster.X + ClearDistanceM + 2;
            f.Healer.Position.X = monster.X - ClearDistanceM - 2;
            f.Monsters.Tick(monster, f.Server.Service<World>().Map(monster.WorldScene));
        });

        Assert.Same(f.Tank, monster.Target);
        Assert.True(monster.ThreatTable.ContainsKey(f.Tank));
        Assert.False(monster.ThreatTable.ContainsKey(f.Healer));
    }

    [Fact]
    public async Task LosingItsTargetItTurnsToTheMostHated()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();

        await f.OnLoopAsync(() =>
        {
            f.Monsters.AddHatred(monster, f.Tank, 10000);
            f.Monsters.AddHatred(monster, f.Healer, 3000);
            f.Tank.Dead = true;
            f.Monsters.Tick(monster, f.Server.Service<World>().Map(monster.WorldScene));
            f.Tank.Dead = false;
        });

        Assert.Same(f.Healer, monster.Target);
        Assert.False(monster.ThreatTable.ContainsKey(f.Tank));
    }

    [Fact]
    public async Task TheTopHatredBuffFollowsTheTarget()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync();
        bool Holds(GameSession s) => s.Buffs.Any(b => b.ConfigId == TopBuff && b.SourceId == monster.Uid);

        await f.OnLoopAsync(() => f.Monsters.AddHatred(monster, f.Tank, 1000));
        await f.Server.UntilAsync(() => Holds(f.Tank), "the target carries the buff");

        await f.OnLoopAsync(() => f.Monsters.AddHatred(monster, f.Healer, 2000));
        await f.Server.UntilAsync(() => Holds(f.Healer) && !Holds(f.Tank), "the buff moves to the new target");

        await f.OnLoopAsync(() => f.Monsters.KillAsync(monster));
        await f.Server.UntilAsync(() => !Holds(f.Healer), "a dead monster's buff is gone");
    }

    private sealed class ThreatFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-threat-" + Guid.NewGuid().ToString("N"));
        private BotClient _tankBot = null!;
        private BotClient _healerBot = null!;

        public TestServer Server { get; private set; } = null!;
        public GameSession Tank => Server.Service<SessionRegistry>().All().First(s => s.Role?.Name == "Tank");
        public GameSession Healer => Server.Service<SessionRegistry>().All().First(s => s.Role?.Name == "Healer");
        public MonsterService Monsters => Server.Service<MonsterService>();

        public Task OnLoopAsync(Action work) => Server.OnLoopAsync(work);
        public Task OnLoopAsync(Func<Task> work) => Server.OnLoopAsync(work);

        public static async Task<ThreatFixture> StartAsync()
        {
            var f = new ThreatFixture();
            f.CopyAndWriteData();
            f.Server = await TestServer.StartSyntheticAsync(o => o.GameDataDir = f._root);
            f._tankBot = await f.Server.InWorldAsync("Tank");
            f._healerBot = await f.Server.InWorldAsync("Healer");
            await f.Server.OnLoopAsync(async () =>
            {
                f.Tank.Position.X = PlayerX;
                f.Tank.Position.Y = 0;
                f.Tank.Position.Z = PlayerZ;
                f.Healer.Position.X = PlayerX + 2;
                f.Healer.Position.Y = 0;
                f.Healer.Position.Z = PlayerZ;
                // The fixture roles have no HP of their own and would fall at the monster's first swing.
                var character = f.Server.Service<CharacterService>();
                character.DynamicProperties.Subscribe(_ => [(101, 1_000_000L), (2000, 1_000_000L)]);
                await character.RefreshStatsAsync(f.Tank);
                await character.RefreshStatsAsync(f.Healer);
            });
            return f;
        }

        public async Task<MonsterUnit> SpawnAsync()
        {
            var template = Monsters.Template(BossNpc) ?? throw new InvalidOperationException("No fixture monster.");
            var at = new MapPoint(PlayerX + 3, 0, PlayerZ, 0);
            MonsterUnit? monster = null;
            await Server.OnLoopAsync(async () => monster = await Monsters.SpawnSingleAsync(Tank.SceneEntry, BossNpc, at,
                Tank.InstanceId, customTemplate: template with { AttackRange = 3f, RoamDistance = 0 }));
            return monster ?? throw new InvalidOperationException("Fixture monster could not spawn.");
        }

        private void CopyAndWriteData()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            var npcs = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "config", "NPCConfig.json")))!.AsObject();
            npcs[Key(BossNpc)] = JsonSerializer.SerializeToNode(new
            {
                _iID = BossNpc, _iNPCType = 1, _iNPCSubType = 4, _iLevel = 50, _iAttribute = 900001, _iCamp = 2, _iSpeed = 800,
                _iAI = BossAi, _kTopHatredBuff = new[] { TopBuff },
            });
            File.WriteAllText(Path.Combine(_root, "config", "NPCConfig.json"), npcs.ToJsonString());

            var publicParams = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "config", "PublicParamConfig.json")))!.AsObject();
            publicParams["HatredExceedValue"] = ExceedBp;
            publicParams["HealthHatredScale"] = HealBp;
            publicParams["HateClearDistance"] = ClearDistanceM * 100;
            publicParams["HateClearTime"] = ClearTimeMs;
            File.WriteAllText(Path.Combine(_root, "config", "PublicParamConfig.json"), publicParams.ToJsonString());

            Write("AIConfig", new Dictionary<string, object>
            {
                [Key(BossAi)] = new
                {
                    _iID = BossAi, _iChaseDistance = 3000, _iViewRange = 2000, _iUnFightType = new[] { 1 }, _iUnFightParam = new[] { 3000 },
                    _iHatredType = 1, _iHatredDeclineSpeed = DeclineBp, _iHatredDeclineTime = DeclineMs, _iMinHatredDecline = MinDecline,
                },
            });
            Write("BuffConfig", new Dictionary<string, object>
            {
                [Key(TopBuff)] = new { _iID = TopBuff, _iBuffID = TopBuff / 100, _iTime = -1, _iStackType = 4, _iDeadClear = 1 },
            });
        }

        private void Write(string table, object rows) =>
            File.WriteAllText(Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows));

        private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            await _tankBot.DisposeAsync();
            await _healerBot.DisposeAsync();
            await Server.DisposeAsync();
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
