using System.Text.Json;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>Invented content: guardian admission and lifetime checks run in public CI without a client export.</summary>
public sealed class GuardianPetTests
{
    private const int Wrapper = 99001, Child = 99002, OtherChild = 99003, Buff = 99004, Pet = 99;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "ro3emu-guardian-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;
        public GameSession Session => Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public GameLoop Loop => Host.Service<GameLoop>();
        public CharacterService Character => Host.Service<CharacterService>();
        public CombatService Combat => Host.Service<CombatService>();

        public async Task Start()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            Write("SkillConfig", new Dictionary<string, object>
            {
                [Wrapper.ToString()] = new
                {
                    _iID = Wrapper, _iSkillID = 99, _iMode = 1, _iSystemType = 8, _iCD = 30000,
                    _iCdModeGroup = -1, _iAutoUse = 1, _kAutoLearnSkill = new[] { OtherChild, Child },
                    _kLogicData = new[] { 199 }, // the second candidate is the actual guardian, not an extra proc
                },
                [Child.ToString()] = new
                {
                    _iID = Child, _iSkillID = 199, _iSystemType = 41, _iActionTime = 300,
                    _kScriptLogic = "PetSkill_2_2_QiaoKeHou", _kBuff1 = new[] { new[] { Buff, 10000, 5000 } },
                },
                [OtherChild.ToString()] = new
                {
                    _iID = OtherChild, _iSkillID = 299, _iSystemType = 41,
                    _kScriptLogic = "UnrecoveredGuardianProc",
                },
            });
            Write("BuffConfig", new Dictionary<string, object>
            {
                [Buff.ToString()] = new { _iID = Buff, _iBuffID = 399, _iTime = 5000, _kChangeAttri = new[] { new[] { 103, 17 } } },
            });
            Write("PetBaseConfig", new Dictionary<string, object> { [Pet.ToString()] = new { _iID = Pet } });
            Write("PetStarConfig", new[]
            {
                new { _iPetID = Pet, _iStar = 1, _iStage = 0, _iProtectSkill = Wrapper,
                    _kFightSkill = new[] { new[] { 103, 13 } }, _kAssistSkill = new[] { new[] { 103, 7 } } },
            });
            Write("PetSetLimitConfig", new Dictionary<string, object>
            {
                ["1"] = new { _iID = 1, _iCondition1 = 1, _iCondition2 = 1,
                    _iFightAttrAddition = 10000, _iAssistAttrAddition = 10000 },
            });
            Write("LC_ConditionList", new Dictionary<string, object>
            {
                ["1"] = new { m_kSubConditionList = new[] { new { m_kData = new[] { 1002, 33 } } } },
            });
            Host = await SyntheticHost.StartAsync(o => o.GameDataDir = root);
        }

        private void Write(string name, object value) =>
            File.WriteAllText(Path.Combine(root, "config", name + ".json"), JsonSerializer.Serialize(value));

        public Task Equip() => Loop.InvokeAsync(async () =>
        {
            Session.Progress!.Level = 40;
            Session.Pets = new DBPetInfo { CurrentPlan = 0 };
            Session.Pets.ListPet[Pet] = new PetData { PetId = Pet, Star = 1 };
            Session.Pets.SetupPlans[0] = new DBPetSetUpPlan { SetUpPets = { [1] = Pet } };
            Session.Pets.SetupPlans[1] = new DBPetSetUpPlan();
            await Character.RefreshStatsAsync(Session);
        });

        public async ValueTask DisposeAsync()
        {
            if (Host != null) await Host.DisposeAsync();
            Directory.Delete(root, true);
        }
    }

    private static async Task<CastSkillRsp> Reply(BotClient bot)
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(5));
        return CastSkillRsp.Parser.ParseFrom(packet.Body);
    }

    [Fact]
    public async Task WrapperResolvesPrimaryChildAndConcurrentRequestsShareOneCooldown()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        long before = f.Character.Stats(f.Session)[103];
        var request = new CastSkillReq { SkillId = Wrapper, TargetId = f.Session.Role!.Id };
        await bot.SendGameAsync(CombatService.UseSkill, request);
        await bot.SendGameAsync(CombatService.UseSkill, request);
        var replies = new[] { await Reply(bot), await Reply(bot) };
        Assert.Single(replies, r => r.Result == 0);
        Assert.Single(replies, r => r.Result == ErrorCodes.SkillCooldown);
        var animation = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.ExtraUseInSkill), TimeSpan.FromSeconds(5));
        var extra = UseInSkillInfo.Parser.ParseFrom(animation.Body);
        Assert.Equal(Child, extra.SkillID);
        Assert.Equal(Pet, extra.PetConfigId);
        await bot.Inbox.WaitAsync(e => e.IsGame(BuffService.BuffUpdate), TimeSpan.FromSeconds(5));
        Assert.Single(f.Session.Buffs, b => b.ConfigId == Buff);
        Assert.Equal(before + 17, f.Character.Stats(f.Session)[103]);
        Assert.Null(f.Session.SkillCast);
    }

    [Fact]
    public async Task OnlyAnOwnedPetInAnUnlockedFightSlotCanCastOrContributeFightStats()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        long deployed = f.Character.Stats(f.Session)[103];
        async Task Refused(int id = Wrapper)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = id, TargetId = f.Session.Role!.Id });
            Assert.Equal(ErrorCodes.SkillNotLearned, (await Reply(bot)).Result);
        }
        await Refused(Child);
        await f.Loop.InvokeAsync(async () =>
        {
            var plan = f.Session.Pets!.SetupPlans[0];
            plan.SetUpPets.Clear();
            plan.SupportPets[1] = Pet;
            await f.Character.RefreshStatsAsync(f.Session);
        });
        Assert.Equal(deployed - 13 + 7, f.Character.Stats(f.Session)[103]);
        await Refused();
        await f.Loop.InvokeAsync(async () =>
        {
            var plan = f.Session.Pets!.SetupPlans[0];
            plan.SupportPets.Clear();
            plan.SetUpPets[99] = Pet; // invalid persisted slot must not grant bonuses or guardian admission
            await f.Character.RefreshStatsAsync(f.Session);
        });
        Assert.Equal(deployed - 13, f.Character.Stats(f.Session)[103]);
        await Refused();
        await f.Loop.InvokeAsync(() =>
        {
            f.Session.Pets!.SetupPlans[0].SetUpPets[1] = Pet;
            f.Session.Pets.ListPet.Clear();
            return Task.CompletedTask;
        });
        await Refused();
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("unequip")]
    [InlineData("star")]
    [InlineData("scene")]
    public async Task PendingEffectIsDiscardedWhenItsSourceChanges(string change)
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Combat.CastAsync(f.Session, new CastSkillReq { SkillId = Wrapper, TargetId = f.Session.Role!.Id });
            switch (change)
            {
                case "plan": f.Session.Pets!.CurrentPlan = 1; break;
                case "unequip": f.Session.Pets!.SetupPlans[0].SetUpPets.Clear(); break;
                case "star": f.Session.Pets!.ListPet[Pet].Star++; break;
                case "scene": f.Session.SceneGeneration++; break;
            }
        });
        Assert.Equal(0, (await Reply(bot)).Result);
        await Task.Delay(600);
        await f.Loop.InvokeAsync(() =>
        {
            Assert.DoesNotContain(f.Session.Buffs, b => b.ConfigId == Buff);
            return Task.CompletedTask;
        });
    }
}
