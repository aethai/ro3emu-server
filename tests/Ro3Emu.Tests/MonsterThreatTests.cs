using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// Unit tests for MMORPG aggro / threat mechanics and Provoke/Taunt behavior (Issue #537).
/// Verifies that monsters do not switch targets on every damage hit, that Provoke establishes high threat
/// and locks aggro, that threat requires a threshold to pull away, and that TopHatredBuff is applied and transferred.
/// </summary>
public sealed class MonsterThreatTests
{
    private const int ThreatBossId = 16329301;
    private const int ThreatBossAi = 1990301;
    private const int TopBuffId = 14723001;
    private const float PlayerX = 150f, PlayerZ = 186f;

    [Fact]
    public async Task InitialHitEstablishesTargetAndThreat()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync(ThreatBossId);

        await f.Loop.InvokeAsync(() =>
        {
            f.Monsters.AddThreat(monster, f.Tank, 1000L);
            return Task.CompletedTask;
        });

        Assert.Equal(1000L, monster.ThreatTable[f.Tank].Value);
        Assert.Same(f.Tank, monster.Target);
        Assert.Same(f.Tank, monster.TopThreatSession);
    }

    [Fact]
    public async Task SecondPlayerLowerDamageDoesNotStealAggro()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync(ThreatBossId);

        await f.Loop.InvokeAsync(() =>
        {
            // Tank establishes 5,000 threat
            f.Monsters.AddThreat(monster, f.Tank, 5000L);
            // Healer deals 500 damage (10% of tank's threat)
            f.Monsters.AddThreat(monster, f.Healer, 500L);
            return Task.CompletedTask;
        });

        // Boss must remain firmly locked onto the Tank, NOT switch to Healer (Issue #537)
        Assert.Same(f.Tank, monster.Target);
        Assert.Same(f.Tank, monster.TopThreatSession);
        Assert.Equal(5000L, monster.ThreatTable[f.Tank].Value);
        Assert.Equal(500L, monster.ThreatTable[f.Healer].Value);
    }

    [Fact]
    public async Task ProvokeOrTauntGrantsMassiveThreatAndLocksAggro()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync(ThreatBossId);

        await f.Loop.InvokeAsync(() =>
        {
            // Healer deals 2,000 initial damage
            f.Monsters.AddThreat(monster, f.Healer, 2000L);
            Assert.Same(f.Healer, monster.Target);

            // Tank casts Provoke/Taunt
            f.Monsters.AddThreat(monster, f.Tank, 10000L, isTaunt: true);
            return Task.CompletedTask;
        });

        // Tank now has highest threat and monster targets Tank
        Assert.Same(f.Tank, monster.Target);
        Assert.Same(f.Tank, monster.TopThreatSession);
        Assert.True(monster.ThreatTable[f.Tank].Value >= 50000L);

        await f.Loop.InvokeAsync(() =>
        {
            // Healer deals more damage (e.g. 5,000)
            f.Monsters.AddThreat(monster, f.Healer, 5000L);
            return Task.CompletedTask;
        });

        // Aggro stays locked on the Tank
        Assert.Same(f.Tank, monster.Target);
        Assert.Same(f.Tank, monster.TopThreatSession);
    }

    [Fact]
    public async Task DamageExceedingHysteresisThresholdPullsAggro()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync(ThreatBossId);

        await f.Loop.InvokeAsync(() =>
        {
            // Tank has 1,000 threat
            f.Monsters.AddThreat(monster, f.Tank, 1000L);
            // DPS does 1,050 damage (within 10% margin, doesn't pull aggro)
            f.Monsters.AddThreat(monster, f.Healer, 1050L);
            return Task.CompletedTask;
        });

        Assert.Same(f.Tank, monster.Target);

        await f.Loop.InvokeAsync(() =>
        {
            // DPS does another 500 damage (total 1,550 > 1,100), pulls aggro!
            f.Monsters.AddThreat(monster, f.Healer, 500L);
            return Task.CompletedTask;
        });

        Assert.Same(f.Healer, monster.Target);
        Assert.Same(f.Healer, monster.TopThreatSession);
    }

    [Fact]
    public async Task TopThreatDeathFallsBackToNextHighestThreat()
    {
        await using var f = await ThreatFixture.StartAsync();
        var monster = await f.SpawnAsync(ThreatBossId);

        await f.Loop.InvokeAsync(() =>
        {
            f.Monsters.AddThreat(monster, f.Tank, 10000L);
            f.Monsters.AddThreat(monster, f.Healer, 3000L);
            // Simulate Tank death
            f.Tank.Dead = true;
            // Next fight tick checks target validity and falls back
            f.Monsters.Tick(monster, f.Host.Service<World>().Map(monster.WorldScene));
            return Task.CompletedTask;
        });

        // Boss must switch to Healer (the next highest threat player)
        Assert.Same(f.Healer, monster.Target);
    }

    private sealed class ThreatFixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-threat-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;
        private BotClient _tankBot = null!;
        private BotClient _healerBot = null!;

        public GameSession Tank => Host.Service<SessionRegistry>().All().First(s => s.Role?.Name == "Tank");
        public GameSession Healer => Host.Service<SessionRegistry>().All().First(s => s.Role?.Name == "Healer");
        public GameLoop Loop => Host.Service<GameLoop>();
        public MonsterService Monsters => Host.Service<MonsterService>();

        public static async Task<ThreatFixture> StartAsync()
        {
            var f = new ThreatFixture();
            f.CopyAndWriteData();
            f.Host = await SyntheticHost.StartAsync(o => o.GameDataDir = f._root);
            f._tankBot = await f.Host.InWorldAsync("Tank");
            f._healerBot = await f.Host.InWorldAsync("Healer");

            await f.Loop.InvokeAsync(async () =>
            {
                f.Tank.Position.X = PlayerX;
                f.Tank.Position.Y = 0;
                f.Tank.Position.Z = PlayerZ;

                f.Healer.Position.X = PlayerX + 2;
                f.Healer.Position.Y = 0;
                f.Healer.Position.Z = PlayerZ;

                var character = f.Host.Service<CharacterService>();
                character.DynamicProperties.Subscribe(_ => [(101, 1_000_000L), (2000, 1_000_000L)]);
                await character.RefreshStatsAsync(f.Tank);
                await character.RefreshStatsAsync(f.Healer);
            });
            return f;
        }

        public async Task<MonsterUnit> SpawnAsync(int npcId)
        {
            var template = Monsters.Template(npcId) ?? throw new InvalidOperationException("No fixture monster.");
            var at = new MapPoint(PlayerX + 3, 0, PlayerZ, 0);
            return await Monsters.SpawnSingleAsync(Tank.SceneEntry, npcId, at, Tank.InstanceId,
                customTemplate: template with { AttackRange = 3f, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Fixture monster could not spawn.");
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
            npcs[Key(ThreatBossId)] = Npc(ThreatBossId, ThreatBossAi, TopBuffId);
            File.WriteAllText(Path.Combine(_root, "config", "NPCConfig.json"), npcs.ToJsonString());

            Dictionary<string, object> aiConfig = [];
            aiConfig[Key(ThreatBossAi)] = Ai(ThreatBossAi);
            Write("AIConfig", aiConfig);
        }

        private static JsonNode? Npc(int id, int ai, int topBuff) => JsonSerializer.SerializeToNode(new
        {
            _iID = id, _iNPCType = 1, _iLevel = 50, _iAttribute = 900001, _iCamp = 2, _iSpeed = 800, _iAI = ai,
            _iNPCSubType = 4, // MVP / Boss
            _kTopHatredBuff = (long[])[topBuff],
            _kSkills = (long[])[5003301],
            _iAttackTickTime = 1000
        });

        private static object Ai(int id) => new
        {
            _iID = id, _iRoamDis = 0, _iChaseDistance = 3000, _iViewRange = 2000, _iIsSurround = 0,
            _iBattleCallType = 0, _iBattleCallResponse = 0, _iUnFightType = 1, _iUnFightParam = 3000,
            _iUnFightBUFF = (long[])[MonsterService.ResetHealBuff],
            _iHatredType = 1, _iHatredDeclineSpeed = 1000, _iHatredDeclineTime = 5000, _iMinHatredDecline = 500
        };

        private void Write(string table, object rows) =>
            File.WriteAllText(Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows));

        private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

        public async ValueTask DisposeAsync()
        {
            await _tankBot.DisposeAsync();
            await _healerBot.DisposeAsync();
            await Host.DisposeAsync();
            try { Directory.Delete(_root, true); } catch { /* best effort */ }
        }
    }
}
