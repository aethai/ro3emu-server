using System.Globalization;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server;

/// <summary>Character progression, skills, gear, cards, seasons, mounts, pets, life jobs, and their GM ops.</summary>
internal static class CharacterComposition
{
    public static void AddCharacter(IServiceCollection services)
    {
        void AddLifecycle<T>() where T : class, IRoleLifecycle =>
            services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<T>());

        services.AddSingleton<CharacterService>();
        services.AddSingleton<CharacterInventoryService>();
        services.AddSingleton<CharacterStatsService>();
        services.AddSingleton<SkillService>();
        services.AddSingleton<FunctionService>();
        services.AddSingleton<RewardService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<AwardService>();
        services.AddSingleton<WildAfkService>();
        services.AddSingleton<LifeJobService>();
        services.AddSingleton<GatheringService>();
        services.AddSingleton<GuideService>();
        services.AddSingleton<CardService>();
        services.AddSingleton<CardEffects>();
        services.AddSingleton<TalentProcs>();
        services.AddSingleton<PassiveSkills>();
        services.AddSingleton<RidingEffects>();
        services.AddSingleton<StealthEffects>();
        services.AddSingleton<SeasonService>();
        services.AddSingleton<SeasonPageService>();
        services.AddSingleton<SeasonRefineService>();
        services.AddSingleton<SeasonTalentService>();
        services.AddSingleton<MountService>();
        services.AddSingleton<CareerSpecialService>();
        services.AddSingleton<WardrobeService>();
        services.AddSingleton<TotemEffects>();
        services.AddSingleton<NightCycleService>();
        services.AddSingleton<TotemService>();
        services.AddSingleton<GenreService>();
        services.AddSingleton<PowerService>();
        services.AddSingleton<EmblemService>();
        services.AddSingleton<EquipmentService>();
        services.AddSingleton<GrowthClassService>();
        services.AddSingleton<ParagonService>();
        services.AddSingleton<FlyingDragonService>();
        services.AddSingleton<TitleService>();
        services.AddSingleton<FirstHeartService>();
        services.AddSingleton<AppraisalService>();
        services.AddSingleton<EnchantService>();
        services.AddSingleton<PetFollowerService>();
        services.AddSingleton<PetService>();

        AddLifecycle<CharacterService>();
        AddLifecycle<SkillService>();
        AddLifecycle<FunctionService>();
        AddLifecycle<TaskService>();
        AddLifecycle<GuideService>();
        AddLifecycle<CardService>();
        AddLifecycle<EmblemService>();
        AddLifecycle<SeasonService>();
        AddLifecycle<SeasonPageService>();
        AddLifecycle<SeasonRefineService>();
        AddLifecycle<SeasonTalentService>();
        AddLifecycle<PetService>();
        AddLifecycle<FlyingDragonService>();
        AddLifecycle<GrowthClassService>();
        AddLifecycle<ParagonService>();
        AddLifecycle<TitleService>();
        AddLifecycle<FirstHeartService>();
        AddLifecycle<LifeJobService>();
        AddLifecycle<GatheringService>();
        AddLifecycle<WildAfkService>();
        AddLifecycle<MountService>();
        AddLifecycle<CareerSpecialService>();
        AddLifecycle<WardrobeService>();
        AddLifecycle<TotemService>();
        AddLifecycle<GenreService>();
        AddLifecycle<PowerService>();
    }

    public static void WireCharacterCore(WebApplication app)
    {
        // Mount energy meters (EnergyConfig._iBelongMount): BuffService cannot take MountService (MountService -> SceneService -> BuffService).
        app.Services.GetRequiredService<MountService>().Changed.Subscribe(app.Services.GetRequiredService<BuffService>().MountsChangedAsync);
        app.Services.GetRequiredService<CharacterService>().ExpCredited.Subscribe(app.Services.GetRequiredService<CheatMonitor>().ExpGained);
        // Stats memoize across short tick windows on hot combat/ticker paths (CharacterService.Stats).
        var loop = app.Services.GetRequiredService<GameLoop>();
        var character = app.Services.GetRequiredService<CharacterService>();
        character.BindSubsystems(
            app.Services.GetRequiredService<CharacterInventoryService>(),
            app.Services.GetRequiredService<CharacterStatsService>());
        character.WorkSerial = () => loop.WorkSerial;
        character.TickSerial = () => loop.TickSerial;
    }

    public static void WireCharacterHooks(WebApplication app)
    {
        app.Services.GetRequiredService<RewardService>();
        app.Services.GetRequiredService<TaskService>(); // hooks kills / level / job changes
    }

    public static void WireWildAfk(WebApplication app)
    {
        app.Services.GetRequiredService<WildAfkService>(); // field drop accounting and Bonus Time ticker
    }

    public static void WireCharacterTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<SkillService>().Learned.Subscribe(app.Services.GetRequiredService<CharacterService>().NotifyCareerCapacityAsync);
        app.Services.GetRequiredService<TotemService>().Combined.Subscribe(questTasks.OnTotemCombined); // target 315004
    }

    public static void WireCharacterProgressionTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<EmblemService>().Upgraded.Subscribe(questTasks.OnEchoUpgraded); // 317002
    }

    public static void WireCharacterMountTaskHook(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<MountService>().RideChanged.Subscribe(
            s => { questTasks.OnStateChanged(s); return Task.CompletedTask; },
            name: "CharacterComposition.MountRideChanged"); // 101021
    }

    public static void WireCharacterEffects(WebApplication app)
    {
        app.Services.GetRequiredService<CardEffects>(); // socketed-card passives and procs (sockets, scene loads, hits)
        app.Services.GetRequiredService<TalentProcs>(); // profession talent procs (cast resolutions)
        app.Services.GetRequiredService<PassiveSkills>(); // learned passives' procs, shields and reactions
        app.Services.GetRequiredService<RidingEffects>(); // a ridden mount's buffs and the riding passives
        app.Services.GetRequiredService<StealthEffects>(); // hiding and detection attributes, reveals, Ruwach
    }

    public static void RegisterCharacterGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var data = app.Services.GetRequiredService<GameData>();
        var character = app.Services.GetRequiredService<CharacterService>();
        var skills = app.Services.GetRequiredService<SkillService>();
        var equipment = app.Services.GetRequiredService<EquipmentService>();
        var rewards = app.Services.GetRequiredService<RewardService>();
        var seasons = app.Services.GetRequiredService<SeasonService>();
        var emblems = app.Services.GetRequiredService<EmblemService>();
        var careerSpecial = app.Services.GetRequiredService<CareerSpecialService>();
        var seasonTalent = app.Services.GetRequiredService<SeasonTalentService>();
        var wardrobe = app.Services.GetRequiredService<WardrobeService>();

        gm.Register("fieldexp", "[set <minLevel> <maxLevel> <percent> [...] | clear | reset] (in memory, until the next start)", async (s, a) =>
        {
            string sub = a.Length == 0 ? "" : a[0].ToLowerInvariant();
            if (sub == "set") rewards.SetFieldExpRules(FieldExpRates.Parse(a[1..]));
            else if (sub == "clear") rewards.SetFieldExpRules([]);
            else if (sub == "reset") rewards.ResetFieldExpRules();
            else if (a.Length != 0) throw new ArgumentException("set | clear | reset");
            await GmCommands.ReplyAsync(s, "Field EXP rates: " + FieldExpRates.Describe(rewards.FieldExpRules));
        });
        gm.Register("season", "[playbook]", async (s, a) =>
            await GmCommands.ReplyAsync(s, a.Length == 0 ? seasons.Describe() : seasons.OpenContent.Enabled
                ? "OpenContent keeps playbook 101 (the only playbook with its own content); switching would hide content"
                : await seasons.SetPlayBookAsync(GmCommands.I(a[0]))));
        gm.Register("seasonnext", "<playbook> <yyyy-MM-dd>", async (s, a) =>
        {
            if (a.Length != 2 || !DateTime.TryParseExact(a[1], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var start)) throw new ArgumentException("Use !seasonnext <playbook> <yyyy-MM-dd>");
            await GmCommands.ReplyAsync(s, seasons.OpenContent.Enabled ? "OpenContent: there is no season rollover"
                : await seasons.AdvanceCycleAsync(GmCommands.I(a[0]), start));
        });

        gm.RegisterOp(GmCommands.OpAllSkillsMax, (s, args) =>
            skills.GmLearnAllAsync(s, GmCommands.Arg(args, 0) is not "0"));
        gm.RegisterOp(GmCommands.OpStrengthen, (s, args) => EquipLevelAsync(equipment, s, refine: false, args));
        gm.RegisterOp(GmCommands.OpRefine, (s, args) => EquipLevelAsync(equipment, s, refine: true, args));
        gm.RegisterOp(GmCommands.OpDropPool, async (s, args) =>
        {
            long pool = GmCommands.L(args[0]);
            if (data.Row("DropPoolConfig", pool) is null) return $"No drop pool {pool}.";
            var rolled = rewards.RollPoolBound(pool, s.Progress!);
            if (rolled.Count == 0) return $"Drop pool {pool} dropped nothing.";
            await character.GrantBoundAsync(s, rolled);
            return $"Drop pool {pool}: {gm.FormatGoods(rolled.Select(g => (g.Entry, g.Count)))}.";
        });
        gm.RegisterOp(GmCommands.OpJobProfessSkill, (s, args) =>
            skills.GmProfessionSkillsAsync(s, gm.Profession(args[0])));
        gm.RegisterOp(GmCommands.OpSetSkillWheel, (s, args) =>
            skills.GmSkillBarAsync(s, [.. args.Select(GmCommands.L)]));
        gm.RegisterOp(GmCommands.OpSkillQueue, (s, args) =>
            skills.GmSkillQueueAsync(s, [.. args.Select(GmCommands.I)]));
        gm.RegisterOp(GmCommands.OpCreateLvEmblem, (s, args) =>
            emblems.GmCreateAsync(s, GmCommands.I(args[0]), args.Length > 1 ? GmCommands.I(args[1]) : 0));
        gm.RegisterOp(GmCommands.OpEquipMount, (s, args) =>
            careerSpecial.GmLeaseAsync(s, GmCommands.L(args[0]), mount: true, ride: args.Length > 1 && args[1] == "1"));
        gm.RegisterOp(GmCommands.OpEquipPendant, (s, args) =>
            careerSpecial.GmLeaseAsync(s, GmCommands.L(args[0]), mount: false, ride: false));
        gm.RegisterOp(GmCommands.OpSeasonTalent, async (s, args) =>
            args.Length > 1 && args[0].Equals("upgradetree", StringComparison.OrdinalIgnoreCase)
                ? await seasonTalent.GmUpgradeTreeAsync(s, GmCommands.L(args[1]))
                : $"GmSeasonTalent {string.Join(',', args)}: only upgradetree <tree> is known (the presets' one subcommand).");
        gm.RegisterOp(GmCommands.OpWarRobe, async (s, args) =>
            args.Length > 0 && args[0].Equals("lightupallappearance", StringComparison.OrdinalIgnoreCase)
                ? await wardrobe.GmLightUpAllAsync(s)
                : $"GMWarRobe {string.Join(',', args)}: only lightupallappearance is known (the presets' one subcommand).");
    }

    private static async Task<string> EquipLevelAsync(EquipmentService equipment, GameSession s, bool refine, string[] args)
    {
        int slot = GmCommands.I(args[0]), level = GmCommands.Arg(args, 1) is { Length: > 0 } text ? GmCommands.I(text) : 1;
        if (slot is < 1 or > 6) throw new ArgumentException("slot 1 weapon, 2 armor, 3 cloak, 4 shoes, 5 left / 6 right accessory");
        int[] grids = slot == 1 ? [1, 2] : [slot + 1];
        return await equipment.GmSetLevelAsync(s, grids, level, refine)
            ?? $"No {(refine ? "refine" : "strengthen")} level {level} for slot {slot}.";
    }
}
