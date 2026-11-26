using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// A monster's ele_reduce (AttriMonsterConfig attribute 236) on invented fixture rows: the element advantage of a hit
/// on it shrinks by its share, and at 10000 the hit lands as a neutral one (the Phantom Realm bosses' "Element Counter
/// Immunity"). A disadvantaged hit keeps its penalty.
/// </summary>
public sealed class ElementCounterTests
{
    private const int Immune = 16329201, Plain = 16329202, Half = 16329203;
    private const int ImmuneAttri = 990201, PlainAttri = 990202, HalfAttri = 990203;
    private const int DefenderElement = 4, StrongElement = 5, WeakElement = 3;
    private const int StrongSkill = 9930001, WeakSkill = 9930002, NeutralSkill = 9930003;
    private const int Advantage = 4000, Penalty = -2000;
    private const int EleReduce = 236;

    [Fact]
    public void CounterResistanceTakesItsShareOffAnAdvantageOnly()
    {
        Assert.Equal(4000, DamageAttributes.CounteredElement(4000, 0));
        Assert.Equal(2000, DamageAttributes.CounteredElement(4000, 5000));
        Assert.Equal(0, DamageAttributes.CounteredElement(4000, 10000));
        Assert.Equal(0, DamageAttributes.CounteredElement(4000, 30000));
        Assert.Equal(4000, DamageAttributes.CounteredElement(4000, -10000));
        Assert.Equal(-2000, DamageAttributes.CounteredElement(-2000, 10000));
    }

    [Fact]
    public async Task AMonstersEleReduceTakesTheElementAdvantageOffAHitOnIt()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-elecounter-" + Guid.NewGuid().ToString("N"));
        try
        {
            WriteData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            var monsters = host.Service<MonsterService>();
            var combat = host.Service<CombatService>();
            var data = host.Service<GameData>();
            Assert.Equal(10000, monsters.Template(Immune)!.ElementCounterResistance);
            Assert.Equal(0, monsters.Template(Plain)!.ElementCounterResistance);

            double Hit(int npc, int skill) => combat.BaseDamage(new CombatService.Fighter(1000, 0, 0, 0, 50, 1),
                combat.MonsterFighter(Unit(npc, monsters.Template(npc)!)), data.Row("SkillConfig", skill)!.Value, 0, out _);

            double neutral = Hit(Plain, NeutralSkill);
            Assert.Equal(neutral * 1.4, Hit(Plain, StrongSkill), 6);
            Assert.Equal(neutral * 1.2, Hit(Half, StrongSkill), 6);
            Assert.Equal(neutral, Hit(Immune, StrongSkill), 6);
            Assert.Equal(neutral * 0.8, Hit(Immune, WeakSkill), 6);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
        }
    }

    private static MonsterUnit Unit(int npc, MonsterTemplate template) => new()
    {
        Uid = npc, Template = template,
        Group = new SpawnGroup(-1, false, 1, 0, [], []), Home = new SpawnPoint(-1, 0, 0, 0, 0, npc),
    };

    private static void WriteData(string root)
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        var npcs = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "NPCConfig.json")))!.AsObject();
        npcs[Key(Immune)] = Npc(Immune, ImmuneAttri);
        npcs[Key(Plain)] = Npc(Plain, PlainAttri);
        npcs[Key(Half)] = Npc(Half, HalfAttri);
        File.WriteAllText(Path.Combine(root, "config", "NPCConfig.json"), npcs.ToJsonString());
        var attri = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "config", "AttriMonsterConfig.json")))!.AsObject();
        attri[Key(ImmuneAttri)] = Attri(ImmuneAttri, 10000);
        attri[Key(PlainAttri)] = Attri(PlainAttri, 0);
        attri[Key(HalfAttri)] = Attri(HalfAttri, 5000);
        File.WriteAllText(Path.Combine(root, "config", "AttriMonsterConfig.json"), attri.ToJsonString());
        File.WriteAllText(Path.Combine(root, "config", "EleConfig.json"), JsonSerializer.Serialize(new[]
        {
            new Dictionary<string, int> { ["_iID"] = DefenderElement, ["_iWater"] = Advantage, ["_iEarth"] = Penalty },
        }));
        File.WriteAllText(Path.Combine(root, "config", "SkillConfig.json"), JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [Key(StrongSkill)] = Skill(StrongSkill, StrongElement),
            [Key(WeakSkill)] = Skill(WeakSkill, WeakElement),
            [Key(NeutralSkill)] = Skill(NeutralSkill, 1),
        }));
    }

    private static JsonNode? Npc(int id, int attribute) => JsonSerializer.SerializeToNode(new
    {
        _iID = id, _iNPCType = 1, _iLevel = 40, _iAttribute = attribute, _iElement = DefenderElement, _iCamp = 2, _iSpeed = 0,
    });

    private static JsonNode? Attri(int id, int eleReduce) => JsonSerializer.SerializeToNode(new
    {
        _iID = id, _kAttri = new int[][] { [101, 50000], [105, 300], [EleReduce, eleReduce] },
    });

    private static object Skill(int id, int element) => new
    {
        _iID = id, _iDmgType = 1, _iEleType = element, _kDamageParam1 = new[] { 10000 }, _kDamageParam2 = new[] { 0 },
    };

    private static string Key(long id) => id.ToString(CultureInfo.InvariantCulture);
}
