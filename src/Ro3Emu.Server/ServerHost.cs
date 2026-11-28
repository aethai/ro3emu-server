using Ro3Emu.Server.Accounts;

namespace Ro3Emu.Server;

/// <summary>Builds the whole emulator (HTTP + both gates); used by Program and by end-to-end tests.</summary>
public static class ServerHost
{
    public static WebApplication Create(string[] args, Action<ServerOptions>? configure = null, TimeProvider? clock = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        var options = Resolve(builder.Configuration, configure);
        // Fail fast, before any service is built, if an offline restore of this data dir did not finish.
        DatabaseRestore.ThrowIfUnfinished(options.DataDir);
        // Kestrel stops first and waits for every HTTP connection (a half-sent request holds it for the 30 s header timeout),
        // and the gates, loop and database stop only after it. The reaper aborts what is left a moment after stop begins.
        var httpConnections = new HttpConnectionReaper();

        CoreComposition.AddCoreInfrastructure(builder, options, clock, httpConnections);
        CharacterComposition.AddCharacter(builder.Services);
        CombatComposition.AddCombat(builder.Services);
        ActivitiesComposition.AddActivities(builder.Services);
        GuildComposition.AddGuild(builder.Services);
        MarketComposition.AddMarket(builder.Services);
        DungeonComposition.AddDungeon(builder.Services);
        TerritoryComposition.AddTerritory(builder.Services);
        SocialComposition.AddSocial(builder.Services, options);
        PanelComposition.AddPanel(builder.Services, options);

        var app = builder.Build();
        CoreComposition.WireCore(app, options, httpConnections);
        CharacterComposition.WireCharacterCore(app);
        SocialComposition.WireChatChannelRule(app);
        CoreComposition.MapServerEndpoints(app);
        WireFeatures(app);
        RegisterGmModules(app);
        PanelComposition.WirePanel(app, options);
        CoreComposition.LogStartup(app, options, builder.Environment.EnvironmentName);
        return app;
    }

    private static void WireFeatures(WebApplication app)
    {
        CharacterComposition.WireCharacterHooks(app);
        ActivitiesComposition.WireActivities(app);
        GuildComposition.WireGuildEvents(app);
        CharacterComposition.WireWildAfk(app);
        SocialComposition.WireTeamService(app);
        ActivitiesComposition.WireMvp(app);
        TerritoryComposition.WireTerritoryCore(app);
        GuildComposition.WireGuildLeague(app);
        CombatComposition.WireCombat(app);
        CoreComposition.WireWorld(app);
        CharacterComposition.WireCharacterTaskHooks(app);
        DungeonComposition.WireDungeonTaskHooks(app);
        GuildComposition.WireGuildTaskHooks(app);
        TerritoryComposition.WireTerritoryTaskHooks(app);
        DungeonComposition.WireDungeonRunAndTowerTaskHooks(app);
        CharacterComposition.WireCharacterProgressionTaskHooks(app);
        ActivitiesComposition.WireActivityTaskHooks(app);
        GuildComposition.WireGuildSocialHooks(app);
        MarketComposition.WireMarketTaskHooks(app);
        SocialComposition.WireSocialTaskAndAchievementHooks(app);
        CharacterComposition.WireCharacterMountTaskHook(app);
        CoreComposition.WireReconnectAndCrypto(app);
        CharacterComposition.WireCharacterEffects(app);
    }

    private static void RegisterGmModules(WebApplication app)
    {
        CoreComposition.RegisterWorldGm(app);
        CharacterComposition.RegisterCharacterGm(app);
        CombatComposition.RegisterCombatGm(app);
        ActivitiesComposition.RegisterActivitiesGm(app);
        GuildComposition.RegisterGuildGm(app);
        MarketComposition.RegisterMarketGm(app);
        DungeonComposition.RegisterDungeonGm(app);
        TerritoryComposition.RegisterTerritoryGm(app);
        SocialComposition.RegisterSocialGm(app);
    }

    /// <summary>The options a server started with these arguments would use (offline tools such as --restore-backup).</summary>
    public static ServerOptions ResolveOptions(string[] args) =>
        Resolve(WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory }).Configuration, null);

    private static ServerOptions Resolve(IConfiguration configuration, Action<ServerOptions>? configure)
    {
        var options = configuration.GetSection("Ro3Emu").Get<ServerOptions>() ?? new ServerOptions();
        configure?.Invoke(options);
        if (options.MeditationRecoveryPercent is < 100 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(configuration), options.MeditationRecoveryPercent,
                "Ro3Emu:MeditationRecoveryPercent must be from 100 (the original) to 1000.");
        if (options.ChatCooldowns.Any(c => c.Value is < 0 or > 3600))
            throw new ArgumentOutOfRangeException(nameof(configuration), "Ro3Emu:ChatCooldowns takes 0 to 3600 seconds per channel.");
        // An empty GM list means everyone only on a server that only this machine reaches, and not on a host started
        // as Production on purpose (docs/guide/gm-access.md). Only an explicit environment counts: the host's own
        // default is also "Production", and tests and local play set none.
        options.GmOpenWhenEmpty ??= !ExplicitProduction(configuration) && options.LocalOnly();
        // A relative data dir is relative to the repository root (the folder holding Ro3Emu.slnx), else to the binary.
        if (!Path.IsPathRooted(options.DataDir)) options.DataDir = Path.GetFullPath(Path.Combine(RepositoryRoot(), options.DataDir));
        return options;
    }

    /// <summary>ASPNETCORE_ENVIRONMENT, DOTNET_ENVIRONMENT or --environment set to Production (not the host default).</summary>
    internal static bool ExplicitProduction(IConfiguration configuration) =>
        string.Equals(configuration[HostDefaults.EnvironmentKey], Environments.Production, StringComparison.OrdinalIgnoreCase);

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Ro3Emu.slnx"))) return dir.FullName;
        }
        return AppContext.BaseDirectory;
    }
}
