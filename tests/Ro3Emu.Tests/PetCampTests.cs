using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>The pet faction (camp) bonus on invented tables (PetCampRules); the real-content check needs the export.</summary>
public sealed class PetCampTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-petcamp-" + Guid.NewGuid().ToString("N"));
    private const int Red1 = 801, Red2 = 802, Red3 = 803, Blue1 = 811, Blue2 = 812;
    private const int RedCamp = 2, BlueCamp = 3;
    private static readonly int[] AtkChange = [AttributeId.Atk, 10];
    private static readonly int[] HpChange = [AttributeId.MaxHp, 40];
    private static readonly int[] PercentAtkBonus = [1039, 2000]; // +20% on the pet's own Atk (1039's _iPreAttri)
    private static readonly int[] UnlockCondition = [1002, 33];
    private static readonly int[] AllPets = [Red1, Red2, Red3, Blue1, Blue2];
    private static readonly int[] TwoAndTwo = [Red1, Red2, Blue1, Blue2];
    private static readonly int[] ThreeRed = [Red1, Red2, Red3, Blue1];
    private static readonly int[] TwoRedAndUnknown = [Red1, Red2, 0, 999];

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private static GameData Data(string root) =>
        new(Options.Create(new ServerOptions { GameDataDir = root }), NullLogger<GameData>.Instance);

    private string Copy()
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Config("PublicParamConfig")))!;
        parameters["PetCampLimitNum"] = JsonSerializer.SerializeToElement(3);
        parameters["PetCampDestinyAttribute"] = JsonSerializer.SerializeToElement(new[] { PercentAtkBonus });
        File.WriteAllText(Config("PublicParamConfig"), JsonSerializer.Serialize(parameters));
        Write("AttributeConfig", new Dictionary<string, object> { ["1039"] = new { _iID = 1039, _iPreAttri = AttributeId.Atk } });
        Write("PetBaseConfig", new Dictionary<string, object>
        {
            [Red1.ToString(CultureInfo.InvariantCulture)] = new { _iID = Red1, _iCamp = RedCamp }, [Red2.ToString(CultureInfo.InvariantCulture)] = new { _iID = Red2, _iCamp = RedCamp },
            [Red3.ToString(CultureInfo.InvariantCulture)] = new { _iID = Red3, _iCamp = RedCamp }, [Blue1.ToString(CultureInfo.InvariantCulture)] = new { _iID = Blue1, _iCamp = BlueCamp },
            [Blue2.ToString(CultureInfo.InvariantCulture)] = new { _iID = Blue2, _iCamp = BlueCamp },
        });
        Write("PetStarConfig", AllPets.Select(id => new
        {
            _iPetID = id, _iStar = 1, _iStage = 0, _kFightSkill = new[] { AtkChange, HpChange }, _kAssistSkill = new[] { AtkChange },
        }).ToArray());
        Write("PetSetLimitConfig", Enumerable.Range(1, 5).ToDictionary(slot => slot.ToString(CultureInfo.InvariantCulture), slot => (object)new
        {
            _iID = slot, _iCondition1 = 1, _iCondition2 = 1, _iFightAttrAddition = 10000, _iAssistAttrAddition = 10000,
        }));
        Write("LC_ConditionList", new Dictionary<string, object>
        {
            ["1"] = new { m_kSubConditionList = new[] { new { m_kData = UnlockCondition } } },
        });
        return _root;
    }

    private string Config(string table) => Path.Combine(_root, "config", table + ".json");

    private void Write(string table, object rows) => File.WriteAllText(Config(table), JsonSerializer.Serialize(rows));

    [Fact]
    public void ACampActivatesAtTheLimitAndScalesOnlyThePreAttributeOfItsBonus()
    {
        var data = Data(Copy());
        Assert.Equal(0, PetCampRules.ActiveCamp(data, TwoAndTwo));
        Assert.Equal(RedCamp, PetCampRules.ActiveCamp(data, ThreeRed));
        Assert.Equal(0, PetCampRules.ActiveCamp(data, TwoRedAndUnknown));
        Assert.Equal(1.2, PetCampRules.Scale(data, AttributeId.Atk), 6);
        Assert.Equal(1.0, PetCampRules.Scale(data, AttributeId.MaxHp), 6);
    }

    [Fact]
    public async Task ThreeDeployedPetsOfOneCampRaiseTheirOwnContributionOnly()
    {
        await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = Copy());
        await using var bot = await host.InWorldAsync("Camper");
        var loop = host.Service<GameLoop>();
        var character = host.Service<CharacterService>();
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        async Task<long> AtkWith(DBPetSetUpPlan plan) => await loop.InvokeAsync(async () =>
        {
            session.Progress!.Level = 40;
            var pets = new DBPetInfo { CurrentPlan = 0 };
            foreach (int id in AllPets) pets.ListPet[id] = new PetData { PetId = id, Star = 1 };
            pets.SetupPlans[0] = plan;
            session.Pets = pets;
            await character.RefreshStatsAsync(session);
            return character.Stats(session)[AttributeId.Atk];
        });

        long none = await AtkWith(new DBPetSetUpPlan());
        long mixed = await AtkWith(new DBPetSetUpPlan { SetUpPets = { [1] = Red1, [2] = Red2, [3] = Blue1, [4] = Blue2 } });
        long red = await AtkWith(new DBPetSetUpPlan { SetUpPets = { [1] = Red1, [2] = Red2, [3] = Red3, [4] = Blue1 }, SupportPets = { [1] = Red3 } });
        Assert.Equal(none + 40, mixed);
        // Three red pets at 10 * 1.2 each, the blue one at 10, the support copy unscaled at 10.
        Assert.Equal(none + 36 + 10 + 10, red);
    }

    [ClientDataFact]
    public void TheClientsFactionBonusScalesDeployedLifeAtThreePetsOfOneCamp()
    {
        var data = Data(Path.Combine(ClientData.RepoRoot, "data"));
        var byCamp = data.Table("PetBaseConfig").Values.GroupBy(r => GameData.Int(r, "_iCamp"))
            .ToDictionary(g => g.Key, g => g.Select(r => (int)GameData.Int(r, "_iID")).ToArray());
        int[] first = byCamp[1], second = byCamp[2];
        Assert.Equal(1, PetCampRules.ActiveCamp(data, [first[0], first[1], first[2], second[0]]));
        Assert.Equal(0, PetCampRules.ActiveCamp(data, [first[0], first[1], second[0], second[1]]));
        Assert.True(PetCampRules.Scale(data, AttributeId.MaxHp) > 1);
        Assert.Equal(1.0, PetCampRules.Scale(data, AttributeId.Atk), 6);
    }
}
