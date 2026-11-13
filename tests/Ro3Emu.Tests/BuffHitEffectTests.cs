using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented content: a friendly cast with a Buff1 and no damage shows its hit effect through a zero-value 31101,
/// and only when its show range holds a hit entry that draws something.</summary>
public sealed class BuffHitEffectTests
{
    // The scripts are the ones CombatService.ShowsBuffHit lists; every id and value below is invented.
    private const int Drawn = 88101, Flash = 88401, NoEntry = 88201, Undrawn = 88301, Buff = 88001, Meter = 88;
    private const int DrawnHit = 881002, FlashHit = 884003;
    private static readonly int[][] BuffTuple = [[Buff, 10000, 5000]];
    private static readonly int[] Range = [600], HitEnergy = [Meter, 5];

    private static object Row(int id, string script, int show) => new
    {
        _iID = id, _iSkillID = id / 100, _iLevel = 1, _iMaxLevel = 1, _iMode = 0, _iBasicType = 1, _kScriptLogic = script,
        _iScriptShow = show, _kBuff1 = BuffTuple, _kRange = Range, _iTargetMax = 3, _kHitRecoverEnergy = HitEnergy,
    };

    private static object Template() => new
    {
        m_fTotalTime = 0.3, m_kAtkPoints = new[] { 0 },
        m_kSelectInfo = new[] { new { m_kSelectType = 2, m_kSelectSubType = 0 } }, // friendly, the caster included
    };

    private static void PrepareData(string root)
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        void Write(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
            File.WriteAllText(Path.Combine(root, path), JsonSerializer.Serialize(value));
        }
        Write("config/SkillConfig.json", new Dictionary<string, object>
        {
            [$"{Drawn}"] = Row(Drawn, "Blessing", 8810), [$"{NoEntry}"] = Row(NoEntry, "Angelus", 8820),
            [$"{Undrawn}"] = Row(Undrawn, "Suffragium", 8830), [$"{Flash}"] = Row(Flash, "Magnificat", 8840),
        });
        Write("config/BuffConfig.json", new Dictionary<string, object> { [$"{Buff}"] = new { _iID = Buff, _iBuffID = 880, _iTime = 5000 } });
        Write("config/EnergyConfig.json", new Dictionary<string, object> { [$"{Meter}"] = new { _iID = Meter, _iLimitValue = 100 } });
        foreach (int show in new[] { 8810, 8820, 8830, 8840 }) Write($"skills/skill_{show}.json", new Dictionary<string, object> { [$"{show}"] = Template() });
        Write("skills/Lua_Config_EffectData.json", new Dictionary<string, object>
        {
            [$"{DrawnHit}"] = new { m_kDamage = new { m_kActionDamge = new { m_kGfxList = new[] { new { m_kGfxName = "fx_synthetic_glow.prefab" } } } } },
            ["882001"] = new { m_kPoint = new { m_kAni = "SyntheticCast" } }, // no hit entry in 882000-882099
            ["883004"] = new { m_kDamage = new { m_kActionDamge = new { m_bNotEnqueue = false } } }, // a hit entry that draws nothing
            [$"{FlashHit}"] = new { m_kDamage = new { m_kActionDamge = new { m_kFresnelEffectData = new { m_iFresnelEffectId = 7 } } } },
        });
    }

    private sealed record Cast(BotClient Alice, BotClient Bob, GameSession A, GameSession B, long SessionId, long EnergyBefore);

    /// <summary>Alice casts <paramref name="skill"/> with Bob beside her, both in her party, and waits until her buff lands on both.</summary>
    private static async Task Run(int skill, Func<Cast, Task> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "ro3emu-buffhit-" + Guid.NewGuid().ToString("N"));
        try
        {
            PrepareData(root);
            await using var host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
            await using var alice = await host.InWorldAsync("Alice");
            await using var bob = await host.InWorldAsync("Bob");
            var sessions = host.Service<SessionRegistry>();
            var a = sessions.All().Single(s => s.Role?.Id == (long)alice.Self!.Uuid);
            var b = sessions.All().Single(s => s.Role?.Id == (long)bob.Self!.Uuid);
            var combat = host.Service<CombatService>();
            combat.SameParty = (_, _) => true;
            await host.Service<GameLoop>().InvokeAsync(() =>
            {
                b.Position = a.Position.Clone();
                a.Energy[Meter] = new PlayerEnergyState { Value = 10 };
                a.GmCastSkill = skill;
                return Task.CompletedTask;
            });
            await alice.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = skill, TargetPos = a.Position.Clone(), Face = a.Face });
            var accepted = CastSkillRsp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(5))).Body);
            Assert.Equal(0, accepted.Result);
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (!(a.Buffs.Any(x => x.ConfigId == Buff) && b.Buffs.Any(x => x.ConfigId == Buff)) && DateTime.UtcNow < deadline) await Task.Delay(25);
            Assert.Contains(a.Buffs, x => x.ConfigId == Buff);
            Assert.Contains(b.Buffs, x => x.ConfigId == Buff);
            await test(new Cast(alice, bob, a, b, accepted.SessionId, 10));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static SkillHitInfo[] Hits(BotClient bot, long session) =>
        [.. bot.Inbox.Snapshot().Where(e => e.IsGame(CombatService.SkillHit)).Select(e => SkillHitInfo.Parser.ParseFrom(e.Body)).Where(h => h.SessionId == session)];

    [Theory]
    [InlineData(Drawn, DrawnHit)] // a prefab in m_kGfxList
    [InlineData(Flash, FlashHit)] // a fresnel flash only
    public async Task ADrawnHitEntryShowsOnEachBuffedUnitAsAZeroDamageShare(int skill, int showPoint) => await Run(skill, async c =>
    {
        var hit = await c.Bob.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit) && SkillHitInfo.Parser.ParseFrom(e.Body).SessionId == c.SessionId,
            TimeSpan.FromSeconds(5));
        var shown = SkillHitInfo.Parser.ParseFrom(hit.Body);
        Assert.Equal(showPoint, shown.ShowPoint);
        Assert.Equal([c.A.Role!.Id, c.B.Role!.Id], shown.Dsts.Select(d => d.DstID).Order());
        Assert.All(shown.Dsts, d =>
        {
            Assert.Equal(DamageType.DamageShare, d.DamageType);
            Assert.Equal(0L, d.Damage);
        });
        await Task.Delay(300);
        Assert.Single(Hits(c.Alice, c.SessionId)); // the caster sees it too, once
        Assert.DoesNotContain(c.Alice.Inbox.Snapshot(), e => e.IsGame(CombatService.DamageStatistics)); // nothing recorded as damage or healing
        Assert.Equal(c.EnergyBefore, c.A.Energy[Meter].Value); // no energy recovered on hit
    });

    [Theory]
    [InlineData(NoEntry)] // the range has no hit entry: HitEffect would fall back to a struck unit's flinch
    [InlineData(Undrawn)] // the range's hit entry draws nothing
    public async Task NoDrawnHitEntrySendsNoHit(int skill) => await Run(skill, async c =>
    {
        await Task.Delay(300);
        Assert.Empty(Hits(c.Alice, c.SessionId));
        Assert.Empty(Hits(c.Bob, c.SessionId));
    });
}
