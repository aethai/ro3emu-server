using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The leash on invented fixture rows: AIConfig _iUnFightParam bounds where a monster may chase, so an attacker who hits
/// it from range just beyond that circle is still fought; a fight lost for good heals the monster on its return only
/// when its AI's _iUnFightBUFF lists the full-heal buff.
/// </summary>
public sealed class MonsterLeashTests
{
    private const int Healing = 16329201, Keeping = 16329202, HealingAi = 1990201, KeepingAi = 1990202;
    private const float Reach = 3f, LeashMetres = 10f;
    private const float PlayerX = 150f, PlayerZ = 186f;
    private const int MaxHpAttribute = 101, HpAttribute = 2000, SpeedUp = 901501;
    private const long LastingHp = 1_000_000;

    [Fact] // a ranged attacker 12 m from its home, outside the 10 m circle but in reach from its edge
    public async Task AMonsterHitFromJustBeyondItsLeashFightsOnWithoutResetting()
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(Healing, LeashMetres + 2);
        long half = await f.Read(() =>
        {
            monster.Hp = monster.Template.MaxHp / 2;
            f.Monsters.Engage(monster, f.Session);
            return monster.Hp;
        });
        await f.HitAsync(monster); // it walks up to the player and swings
        Assert.True(await f.Read(() => ReferenceEquals(monster.Target, f.Session) && !monster.Returning));
        Assert.Equal(half, await f.Read(() => monster.Hp));
    }

    [Theory] // a player it cannot reach from inside its circle: it gives up, healed only by an AI listing the buff
    [InlineData(Healing, true)]
    [InlineData(Keeping, false)]
    public async Task AMonsterThatGivesUpHealsOnlyWhenItsAiListsTheHealBuff(int npc, bool heals)
    {
        await using var f = await Fixture.StartAsync();
        var monster = await f.SpawnAsync(npc, LeashMetres + Reach + 5);
        Assert.Equal(heals, monster.Template.HealsOnReturn);
        long half = await f.Read(() =>
        {
            monster.Hp = monster.Template.MaxHp / 2;
            f.Monsters.Engage(monster, f.Session);
            return monster.Hp;
        });
        await f.Until(() => monster.Target == null && !monster.Returning);
        Assert.Null(await f.Read(() => monster.Target));
        Assert.Equal(heals ? monster.Template.MaxHp : half, await f.Read(() => monster.Hp));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-leash-" + Guid.NewGuid().ToString("N"));
        private SyntheticHost _host = null!;
        private BotClient _bot = null!;

        public GameSession Session => _host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public GameLoop Loop => _host.Service<GameLoop>();
        public MonsterService Monsters => _host.Service<MonsterService>();

        public static async Task<Fixture> StartAsync()
        {
            var f = new Fixture();
            f.CopyAndWriteData();
            f._host = await SyntheticHost.StartAsync(o => o.GameDataDir = f._root);
            f._bot = await f._host.InWorldAsync("Leashed");
            await f.Loop.InvokeAsync(async () =>
            {
                f.Session.Position.X = PlayerX;
                f.Session.Position.Y = 0;
                f.Session.Position.Z = PlayerZ;
                // The fixture role has no HP (no attribute rows) and would fall at the first hit; the fight must last.
                var character = f._host.Service<CharacterService>();
                character.DynamicProperties.Add(_ => [(MaxHpAttribute, LastingHp), (HpAttribute, LastingHp)]);
                await character.RefreshStatsAsync(f.Session);
            });
            return f;
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
            npcs[Key(Healing)] = Npc(Healing, HealingAi);
            npcs[Key(Keeping)] = Npc(Keeping, KeepingAi);
            File.WriteAllText(Path.Combine(_root, "config", "NPCConfig.json"), npcs.ToJsonString());
            Write("AIConfig", new Dictionary<string, object>
            {
                [Key(HealingAi)] = Ai(HealingAi, [MonsterService.ResetHealBuff, SpeedUp]),
                [Key(KeepingAi)] = Ai(KeepingAi, [SpeedUp]),
            });
        }

        private static JsonNode? Npc(int id, int ai) => JsonSerializer.SerializeToNode(new
        {
            _iID = id, _iNPCType = 1, _iLevel = 50, _iAttribute = 900001, _iCamp = 2, _iSpeed = 800, _iAI = ai,
        });

        private static object Ai(int id, long[] buffs) => new
        {
            _iID = id, _iChaseDistance = (int)(LeashMetres * 100), _iUnFightType = new[] { 1 },
            _iUnFightParam = new[] { (int)(LeashMetres * 100) }, _iUnFightBUFF = buffs,
        };

        private void Write(string table, object rows) =>
            File.WriteAllText(Path.Combine(_root, "config", table + ".json"), JsonSerializer.Serialize(rows));

        private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);

        /// <summary>The fixture monster <paramref name="away"/> m east of the player, its home where it spawns.</summary>
        public Task<MonsterUnit> SpawnAsync(int npc, float away) => Loop.InvokeAsync(async () =>
        {
            var template = Monsters.Template(npc) ?? throw new InvalidOperationException("No fixture monster.");
            var at = new MapPoint(PlayerX + away, 0, PlayerZ, 0);
            return await Monsters.SpawnSingleAsync(Session.SceneEntry, npc, at, Session.InstanceId,
                    customTemplate: template with { AttackRange = Reach, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Fixture monster could not spawn.");
        });

        public Task<T> Read<T>(Func<T> read) => Loop.InvokeAsync(() => Task.FromResult(read()));

        public async Task Until(Func<bool> done, int seconds = 10)
        {
            var end = DateTime.UtcNow.AddSeconds(seconds);
            while (!await Read(done))
            {
                if (DateTime.UtcNow > end) return;
                await Task.Delay(50);
            }
        }

        /// <summary>The monster's normal attack lands on the player (31101 from it).</summary>
        public Task<BotEvent> HitAsync(MonsterUnit m) => _bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
            && SkillHitInfo.Parser.ParseFrom(e.Body).SrcID == m.Uid, TimeSpan.FromSeconds(10));

        public async ValueTask DisposeAsync()
        {
            await _bot.DisposeAsync();
            await _host.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }
}
