using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>The typed config views (Data/Config), their readers and the startup check of the game data.</summary>
public sealed class ConfigViewTests
{
    private static readonly double[] Fraction = [1.5];
    private static readonly int[] AwardEntry = [2, 1, 0];

    private static readonly ConfigTableInfo Probe = new("ProbeConfig", false, false,
    [
        new("_iCount", ConfigKind.Integral, false),
        new("_kName", ConfigKind.Text, false),
        new("_kList", ConfigKind.Ints, true),
    ]);

    private sealed class DataDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "ro3emu-config-" + Guid.NewGuid().ToString("N"));

        public DataDir(bool export, params (string Table, object Rows)[] tables)
        {
            Directory.CreateDirectory(Path.Combine(Root, "config"));
            if (export) File.WriteAllText(Path.Combine(Root, GameData.ExportManifest), "{}");
            foreach (var (table, rows) in tables)
                File.WriteAllText(Path.Combine(Root, "config", table + ".json"), JsonSerializer.Serialize(rows));
        }

        public GameData Data => Load(Root);

        public void Dispose() => Directory.Delete(Root, true);
    }

    private static GameData Load(string root) =>
        new(Options.Create(new ServerOptions { GameDataDir = root }), NullLogger<GameData>.Instance);

    private static IReadOnlyList<string> Errors(DataDir dir, bool strict) =>
        ConfigValidator.Validate(dir.Data, [Probe], strict);

    [Fact]
    public void AClientExportMustCarryEveryDeclaredTableAndField()
    {
        using (var empty = new DataDir(export: true))
            Assert.Equal(["ProbeConfig: the table is missing or empty"], Errors(empty, strict: true));
        using var typo = new DataDir(export: true,
            ("ProbeConfig", new[] { new { _iID = 1, _iCuont = 3, _kName = "a" } }));
        Assert.Equal(["ProbeConfig._iCount: missing in 1 of 1 rows (first: 1)"], Errors(typo, strict: true));
        Assert.Empty(Errors(typo, strict: false)); // a hand-made data set may leave fields out
    }

    [Fact]
    public void EveryPresentValueMustHaveItsFieldsKind()
    {
        using var dir = new DataDir(export: false, ("ProbeConfig", new object[]
        {
            new { _iID = 1, _iCount = 3, _kName = "a", _kList = new { } }, // Lua's empty list
            new { _iID = 2, _iCount = "3", _kName = "b" },
            new { _iID = 3, _iCount = 3, _kName = 7, _kList = Fraction },
        }));
        Assert.Equal(
        [
            "ProbeConfig._iCount: row 2 holds string \"3\", not an integer",
            "ProbeConfig._kName: row 3 holds number 7, not a text",
            "ProbeConfig._kList: row 3 holds array [1.5], not a list of integers",
        ], Errors(dir, strict: false));
    }

    [Fact]
    public void ACheckFailureNamesEveryMismatch()
    {
        using var dir = new DataDir(export: true);
        var failure = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Check(dir.Data, [Probe], strict: true));
        Assert.Contains("ProbeConfig: the table is missing or empty", failure.Message);
    }

    [Fact]
    public void ReadersKeepLargeIdsExactAndReadEmptyLuaListsAsEmpty()
    {
        var row = JsonDocument.Parse("""
            { "_iBig": 9007199254740993, "_iFrac": 2.9, "_fRate": "1.25", "_kNone": {}, "_kRows": [[1, 2], {}],
              "_kPairs": [[101, 5], [102, 7.5, 1]], "_kNames": ["a", 3] }
            """).RootElement;
        Assert.Equal(9007199254740993, ConfigValue.Integral(row, "_iBig"));
        Assert.Equal(9007199254740993, GameData.Int(row, "_iBig"));
        Assert.Equal(2, ConfigValue.Integral(row, "_iFrac"));
        Assert.Equal(1.25, ConfigValue.Number(row, "_fRate"));
        Assert.Empty(ConfigValue.Ints(row, "_kNone"));
        Assert.Equal([[1, 2], []], ConfigValue.IntRows(row, "_kRows"));
        Assert.Equal([(101L, 5.0), (102L, 7.5)], ConfigValue.Pairs(row, "_kPairs"));
        Assert.Equal(["a", ""], ConfigValue.Texts(row, "_kNames"));
        Assert.Equal(0, ConfigValue.Integral(row, "_iAbsent"));
        Assert.Null(ConfigValue.Raw(row, "_iAbsent"));
    }

    [Fact]
    public void AGroupedTableChecksEachEntryAndGetReturnsTheGroup()
    {
        using var dir = new DataDir(export: true, ("AwardConfig", new Dictionary<string, object>
        {
            ["7"] = new object[]
            {
                new { _iID = 7, _kAward = new[] { AwardEntry } },
                new { _iID = 7, _kAward = new { } },
            },
        }));
        var data = dir.Data;
        var award = AwardConfig.Info with
        {
            Fields = [.. AwardConfig.Info.Fields.Where(f => f.Name is "_iID" or "_kAward")],
        };
        Assert.Empty(ConfigValidator.Validate(data, [award], strict: true));
        var group = AwardConfig.Get(data, 7);
        Assert.Equal(2, group.Count);
        Assert.Equal([[2, 1, 0]], group[0].Award);
        Assert.Empty(group[1].Award);
        Assert.Empty(AwardConfig.Get(data, 8));
    }

    [Fact]
    public void EveryViewIsListedForTheStartupCheck()
    {
        var names = ConfigTables.All.Select(t => t.Name).ToArray();
        Assert.Equal(names.Distinct().Count(), names.Length);
        Assert.Contains(SkillConfig.Info, ConfigTables.All);
        Assert.Contains(PublicParams.Info, ConfigTables.All);
        Assert.Contains(GuildSettings.Info, ConfigTables.All);
        Assert.All(ConfigTables.All, t => Assert.NotEmpty(t.Fields));
    }

    [Fact]
    public void KeyedConstantsDistinguishAbsentFromZeroAndValidateKind()
    {
        var probe = new ConfigTableInfo("KeyedProbe", false, false,
        [
            new("Zero", ConfigKind.Integral, false),
            new("Missing", ConfigKind.Integral, false),
            new("BadKind", ConfigKind.Integral, false),
        ], Keyed: true, ValueField: "_kValue");

        using var dir = new DataDir(export: true, ("KeyedProbe", new Dictionary<string, object>
        {
            ["Zero"] = new { _kValue = 0 },
            ["BadKind"] = new { _kValue = "oops" },
        }));

        var table = dir.Data.Table("KeyedProbe");
        Assert.Equal(0L, ConfigValue.IntegralConstant(table, "Zero", "_kValue"));
        Assert.Null(ConfigValue.IntegralConstant(table, "Missing", "_kValue"));
        Assert.Equal(
        [
            "KeyedProbe.Missing._kValue: missing",
            "KeyedProbe.BadKind._kValue: holds string \"oops\", not an integer",
        ], ConfigValidator.Validate(dir.Data, [probe], strict: true));
        Assert.Equal(
        [
            "KeyedProbe.BadKind._kValue: holds string \"oops\", not an integer",
        ], ConfigValidator.Validate(dir.Data, [probe], strict: false));
    }

    [Fact]
    public void MissingKeyedConstantWarnsAtStartupWhileWrongKindRemainsFatal()
    {
        var probe = new ConfigTableInfo("KeyedProbe", false, false,
        [
            new("Present", ConfigKind.Integral, false),
            new("DroppedInNewClient", ConfigKind.Integral, false),
        ], Keyed: true, ValueField: "_kValue");

        using var missingOnly = new DataDir(export: true, ("KeyedProbe", new Dictionary<string, object>
        {
            ["Present"] = new { _kValue = 42 },
        }));
        var warnings = ConfigValidator.Check(missingOnly.Data, [probe], strict: true, failOnMissingConstants: false);
        Assert.Equal(["KeyedProbe.DroppedInNewClient._kValue: missing"], warnings);

        using var wrongKind = new DataDir(export: true, ("KeyedProbe", new Dictionary<string, object>
        {
            ["Present"] = new { _kValue = "not-an-int" },
        }));
        var failure = Assert.Throws<ConfigValidationException>(
            () => ConfigValidator.Check(wrongKind.Data, [probe], strict: true, failOnMissingConstants: false));
        Assert.Contains("KeyedProbe.Present._kValue", failure.Message);
    }

    [Fact]
    public async Task SkillServiceTreatsZeroPublicParamsAsFallbackDefaults()
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-skill-zero-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            string ppPath = Path.Combine(root, "config", "PublicParamConfig.json");
            Dictionary<string, JsonElement> pp = File.Exists(ppPath)
                ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(ppPath))!
                : [];
            var zero = JsonSerializer.SerializeToElement(0);
            pp["QueueNum"] = zero;
            pp["SchemeNameNumMax"] = zero;
            pp["SchemeCD"] = zero;
            File.WriteAllText(ppPath, JsonSerializer.Serialize(pp));

            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            var session = host.Service<Ro3Emu.Server.Game.SessionRegistry>().All()
                .Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
            var skills = host.Service<Ro3Emu.Server.Game.SkillService>();
            var loop = host.Service<Ro3Emu.Server.Game.GameLoop>();

            // SchemeCD=0 must fall back to 3 s, so switching immediately after a switch returns Cooldown.
            int cdError = await loop.InvokeAsync(() =>
            {
                session.Combat.SkillPlanSwitchUtc = DateTime.UtcNow;
                return Task.FromResult(skills.PlanSwitchCooldownError(session));
            });
            Assert.Equal(Ro3Emu.Server.Game.ErrorCodes.Cooldown, cdError);

            // SchemeNameNumMax=0 must fall back to 8 runes, so a 4-rune name succeeds.
            await alice.SendGameAsync(Ro3Emu.Server.Game.SkillService.RenamePlan,
                new Romsg.SkillPlanChangeNameReq { Plan = 0, Name = "Main" });
            var renamed = Romsg.SkillPlanChangeNameResp.Parser.ParseFrom((await alice.Inbox.WaitAsync(
                e => e.IsGame(Ro3Emu.Server.Game.SkillService.RenamePlan), TimeSpan.FromSeconds(5))).Body);
            Assert.Equal(0, renamed.Result);
            Assert.Equal("Main", renamed.Name);

            // QueueNum=0 must fall back to 12 slots, so index 0 passes the slot-range check (failing on unlearned skill, not Parameter).
            await alice.SendGameAsync(Ro3Emu.Server.Game.SkillService.QueueUpdate,
                new Romsg.SkillQueueUpdateReq { Plan = 0, QueueIndex = 0, OpType = (Romsg.SkillLoadOperation)1, Info = new Romsg.SkillData { SkillId = 0, SkillLevel = 1 } });
            var queued = Romsg.SkillQueueUpdateResp.Parser.ParseFrom((await alice.Inbox.WaitAsync(
                e => e.IsGame(Ro3Emu.Server.Game.SkillService.QueueUpdate), TimeSpan.FromSeconds(5))).Body);
            Assert.Equal(Ro3Emu.Server.Game.ErrorCodes.SkillNotLearned, queued.Result);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch (IOException) { } }
    }

    [ClientDataFact]
    public void TheClientExportMatchesEveryDeclaredView()
    {
        var data = Load(Path.Combine(ClientData.RepoRoot, "data"));
        Assert.Empty(ConfigValidator.Validate(data, ConfigTables.All, strict: true));
    }

    // Each client field name written as a string outside Data/Config is a read the startup check cannot see. The
    // count only goes down: lower it when a change removes such reads, never raise it.
    private const int FieldNamesOutsideViews = 2223;
    private const int KeyValueTableReadsOutsideViews = 0;
    private static readonly string Obj = Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar;

    [Fact]
    public void ClientFieldNamesLiveInTheConfigViews()
    {
        var source = Path.Combine(ClientData.RepoRoot, "src", "Ro3Emu.Server");
        var config = Path.Combine(source, "Data", "Config") + Path.DirectorySeparatorChar;
        var pattern = new Regex("\"(?:_[a-z]|m_[a-z])[A-Z][A-Za-z0-9_]*\"");
        int count = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(config, StringComparison.Ordinal)
                && !path.Contains(Obj, StringComparison.Ordinal))
            .Sum(path => pattern.Count(File.ReadAllText(path)));
        Assert.Equal(FieldNamesOutsideViews, count);
    }

    [Fact]
    public void KeyValueConfigTablesLiveInConstantViews()
    {
        var source = Path.Combine(ClientData.RepoRoot, "src", "Ro3Emu.Server");
        var config = Path.Combine(source, "Data", "Config") + Path.DirectorySeparatorChar;
        var pattern = new Regex("""\.Table\("(?:PublicParamConfig|GuildSettingConfig|ActivityConstant|MultiverseConstant|GVGConfig)"\)""");
        int count = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.StartsWith(config, StringComparison.Ordinal)
                && !path.Contains(Obj, StringComparison.Ordinal))
            .Sum(path => pattern.Count(File.ReadAllText(path)));
        Assert.Equal(KeyValueTableReadsOutsideViews, count);
    }
}
