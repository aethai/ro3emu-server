using System.Globalization;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Http;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Micro;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server;

/// <summary>Core server infrastructure, HTTP endpoints, messaging modules, world services, and startup diagnostics.</summary>
internal static partial class CoreComposition
{
    public static void AddCoreInfrastructure(WebApplicationBuilder builder, ServerOptions options, TimeProvider? clock, HttpConnectionReaper httpConnections)
    {
        var services = builder.Services;
        // Stage G: the default host on Windows also writes Warning and above to the Windows Event Log, synchronously on
        // the logging thread - the game loop included (a quarter of a walk's cost at 500 players). The console logger
        // queues to a background writer; the metrics provider registered below stays.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        // One server per data dir (C/D 19): taken before anything opens the database, released when the app is disposed.
        services.AddSingleton(_ => RealmLock.Acquire(options.DataDir));
        services.AddSingleton(Options.Create(options));
        services.AddSingleton(clock ?? TimeProvider.System);
        services.AddSingleton<GameCalendar>();
        // GM and Admin accounts need two-factor authentication on a public panel unless the operator says otherwise.
        options.Panel.RequireAdmin2FA ??= !options.LocalOnly();
        // Loopback for local play and tests; the web panel (docs/guide/panel.md) on its own port, routed by PanelSite.Map.
        builder.WebHost.UseUrls(options.Panel.Enabled
            ? [$"http://{options.ListenAddress()}:{options.HttpPort}", PanelComposition.PanelUrl(options)]
            : [$"http://{options.ListenAddress()}:{options.HttpPort}"]);
        builder.WebHost.ConfigureKestrel(k => k.ConfigureEndpointDefaults(l => Microsoft.AspNetCore.Connections.ConnectionBuilderExtensions.Use(l, httpConnections.Track)));

        // First hosted service: everything after it (loop, gates, HTTP) starts with the game data already parsed.
        if (options.PreloadGameData) services.AddHostedService<GameDataPreload>();
        services.AddSingleton<Diagnostics.ServerMetrics>();
        services.AddSingleton<Diagnostics.SessionCensus>();
        services.AddSingleton<ILoggerProvider, Diagnostics.MetricsLoggerProvider>();
        // Recent log lines for bug reports (FeedbackService); kept out of the pipeline when reports keep no context.
        services.AddSingleton<Diagnostics.RecentLogBuffer>();
        if (options.Feedback.ContextLines > 0)
            services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<Diagnostics.RecentLogBuffer>());
        services.AddSingleton<GameLoop>();
        services.AddSingleton<Deadlines>();
        // Starts before the loop: by then the host has built every hosted service and, through them, every ticker.
        services.AddHostedService<GameLoopTickers>();
        services.AddHostedService(sp => sp.GetRequiredService<GameLoop>());
        services.AddSingleton<AccountStore>();
        services.AddSingleton<RoleStore>();
        services.AddSingleton<Game.Ownership.OwnerRegistry>();
        services.AddSingleton<Game.Ownership.RoleHandover>();
        services.AddSingleton<ItemStore>();
        AddWorldAndGate(services, options);
    }

    private static void AddWorldAndGate(IServiceCollection services, ServerOptions options)
    {
        services.AddSingleton<TrafficLog>();
        services.AddSingleton<MsgCatalog>();
        services.AddSingleton<IAoiSink, GameAoiSink>();
        services.AddSingleton<World>();
        services.AddSingleton<WorldData>();
        services.AddSingleton<SceneService>();
        services.AddSingleton<WorldEffectsService>();
        services.AddSingleton<WorldCoreService>();
        services.AddSingleton<WorldCommonService>();
        services.AddSingleton<WorldInteractionService>();
        services.AddSingleton<GmCommands>();
        services.AddSingleton<GameData>();
        services.AddSingleton<SessionRegistry>();
        services.AddSingleton<GateAuthRegistry>();
        services.AddSingleton<ReconnectService>();
        // A singleton too: the panel's server control lists backups and makes one on demand.
        services.AddSingleton<DatabaseBackup>();
        services.AddHostedService(sp => sp.GetRequiredService<DatabaseBackup>());
        services.AddSingleton<CheatMonitor>();
        services.AddHostedService(sp => sp.GetRequiredService<CheatMonitor>());
        services.AddSingleton<ReceiptRetention>();
        services.AddHostedService(sp => sp.GetRequiredService<ReceiptRetention>());
        services.AddSingleton<Game.Persistence.PositionPersistence>();
        services.AddSingleton<Game.Persistence.SettingsPersistence>();
        services.AddSingleton<Game.Persistence.WildAfkPersistence>();

        services.AddSingleton<IRoleLifecycle, WorldRoleLifecycle>();
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<SceneService>());
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<WorldCoreService>());
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<WorldInteractionService>());
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<Game.Persistence.SettingsPersistence>());
        services.AddSingleton<RoleSystems>();
        services.AddSingleton<RoleSessionService>();

        // Message modules in this order: it is the order of their interceptors (World before Economy before Character).
        services.AddSingleton<Game.Messaging.IMessageModule, CoreMessages>();
        services.AddSingleton<Game.Messaging.IMessageModule, WorldMessages>();
        services.AddSingleton<Game.Messaging.IMessageModule, EconomyMessages>();
        services.AddSingleton<Game.Messaging.IMessageModule, CharacterMessages>();
        services.AddSingleton<Game.Messaging.IMessageModule, SocialMessages>();
        services.AddSingleton<Game.Messaging.IMessageModule, ActivitiesMessages>();
        services.AddSingleton<Game.Messaging.IMessageModule, AdventureGroupMessages>();
        services.AddSingleton<Game.Messaging.IMessageModule, Voice.VoiceTicketService>();
        services.AddSingleton<Game.Messaging.IMessageModule>(sp => sp.GetRequiredService<GuildVoiceService>());
        services.AddSingleton<Game.Messaging.IMessageModule>(sp => sp.GetRequiredService<TavernService>());
        services.AddSingleton<GameMessageHandler>();
        services.AddSingleton(sp => GateCrypto.LoadOrCreate(options.DataDir, sp.GetRequiredService<ILogger<GateCrypto>>()));
        services.AddSingleton(_ => OperatorToken.LoadOrCreate(options.DataDir, options.OperatorToken));

        services.AddSingleton<IMicroService, LoginService>();
        services.AddSingleton<IMicroService, ClusterRecommendService>();
        services.AddSingleton<IMicroService, PhonestGateService>();
        services.AddSingleton<IMicroService, PlatformProxyService>();
        services.AddSingleton<MicroSessions>();
        services.AddSingleton<IMicroService, NameService>();
        services.AddSingleton<MicroServiceRouter>();
        services.AddHostedService<MicroGateServer>();
        services.AddHostedService<GameGateServer>();
        // Hosted services stop in reverse order: close ordinary admission before either gate begins teardown.
        services.AddHostedService<GameLoopQuiesceService>();
        // Stops before the quiesce: roles waiting for a reconnect are logged out while cleanup is still admitted.
        services.AddHostedService<ReconnectShutdownService>();

        services.AddSingleton<GiftCodeLimiter>();
        services.AddSingleton<GiftCodeService>();
    }

    public static void WireCore(WebApplication app, ServerOptions options, HttpConnectionReaper httpConnections)
    {
        app.Lifetime.ApplicationStopping.Register(() => httpConnections.AbortAfter(HttpConnectionReaper.Grace));
        app.Services.GetRequiredService<RealmLock>();
        // After the realm lock, so a second server on the same data dir never rewrites the running one's token; before
        // the ports open, so a tool started with the server finds the file.
        app.Services.GetRequiredService<OperatorToken>();
        app.Services.GetRequiredService<Game.Ownership.RoleHandover>()
            .Configure(session => app.Services.GetRequiredService<RoleSessionService>().CleanupRoleAsync(session),
                session => app.Services.GetRequiredService<RoleSessionService>().CleanupReservation(session));
        app.Services.GetRequiredService<Diagnostics.ServerMetrics>().Owners = app.Services.GetRequiredService<Game.Ownership.OwnerRegistry>();
        PanelComposition.MapPanel(app, options);
    }

    public static void MapServerEndpoints(WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsPost(context.Request.Method) && app.Services.GetRequiredService<GameLoop>().Phase != GameLoopPhase.Accepting)
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            if (app.Logger.IsEnabled(LogLevel.Debug)) app.Logger.LogDebug("HTTP {Method} {Path}{Query}", context.Request.Method, context.Request.Path, context.Request.QueryString);
            await next();
        });
        SdkEndpoints.Map(app);
        GiftCodeEndpoints.Map(app);
        Diagnostics.MetricsEndpoints.Map(app);
        OpsEndpoints.Map(app);
        AccountEndpoints.Map(app);
        // The web panel's public address for launchers that joined (Ro3Emu:Panel:PublicUrl); 404 without one.
        app.MapGet("/ro3emu/panel", (IOptions<ServerOptions> options) =>
            options.Value.Panel is { Enabled: true, PublicUrl: { Length: > 0 } url } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https"
                ? Results.Json(new { url = uri.ToString().TrimEnd('/') })
                : Results.NotFound());
        // Public status for the launcher's online pill: only a count, never account or character names. Read from the
        // loop's census, so anyone polling it adds no work to the loop.
        app.MapGet("/ro3emu/status", (Diagnostics.SessionCensus census) =>
            Results.Json(new { online = true, players = census.Current.Roles.Count }));
        app.MapFallback((HttpContext context) =>
        {
            if (app.Logger.IsEnabled(LogLevel.Debug)) app.Logger.LogDebug("HTTP unhandled {Method} {Path}", context.Request.Method, context.Request.Path);
            return Results.NotFound();
        });
    }

    public static void WireWorld(WebApplication app)
    {
        app.Services.GetRequiredService<WorldEffectsService>(); // scene/monster effect hooks before clients connect
        app.Services.GetRequiredService<WorldCoreService>();
        app.Services.GetRequiredService<WorldInteractionService>();
    }

    public static void WireReconnectAndCrypto(WebApplication app)
    {
        app.Services.GetRequiredService<ReconnectService>(); // hooks the handover and registers !netdrop
        // Create the key before the first client connects, so ClientPatch can read the public half.
        app.Services.GetRequiredService<GateCrypto>();
    }

    public static void RegisterWorldGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var sessions = app.Services.GetRequiredService<SessionRegistry>();
        var scene = app.Services.GetRequiredService<SceneService>();
        var world = app.Services.GetRequiredService<World>();
        var worldData = app.Services.GetRequiredService<WorldData>();

        // GmFlyToTarget [target id]: next to a player (role id), a monster or other world unit, or an NPC.
        gm.RegisterOp(GmCommands.OpFlyToTarget, async (s, args) =>
        {
            long uid = GmCommands.L(args[0]);
            if (uid == s.Role!.Id) return "That is you.";
            (RuntimeMapKey Map, long Scene, float X, float Y, float Z)? target = null;
            if (sessions.All().FirstOrDefault(o => o.Role?.Id == uid) is { } player)
                target = (player.RuntimeMap, player.SceneEntry, player.Position.X, player.Position.Y, player.Position.Z);
            else if (world.AllUnits().FirstOrDefault(u => u.Uid == uid) is { } unit)
                target = (unit.RuntimeMap, unit.SceneEntry, unit.X, unit.Y, unit.Z);
            else if (worldData.Npc(uid) is { } npc)
                target = (RuntimeMapKey.Public(npc.SceneEntry, s.LineNumber), npc.SceneEntry, npc.X, npc.Y, npc.Z);
            if (target is not { } t) return $"No entity {uid}.";
            if (t.Map == s.RuntimeMap) await scene.TeleportInMapAsync(s, new MapPoint(t.X, t.Y, t.Z, s.Face));
            else if (t.Map.Kind == RuntimeMapKind.PublicLine) await gm.WarpAsync(s, t.Scene, t.X, t.Z);
            else return $"Entity {uid} is in a private instance of {t.Scene}.";
            return string.Create(CultureInfo.InvariantCulture, $"Flew to {uid}: scene {t.Scene} at {t.X:F1} {t.Z:F1}.");
        });
    }

    public static void LogStartup(WebApplication app, ServerOptions options, string environmentName)
    {
        if (options.Panel.Enabled)
        {
            string panelUrl = PanelComposition.PanelUrl(options);
            LogPanel(app.Logger, panelUrl);
            LogPanelTwoFactor(app.Logger, options.Panel.RequireAdmin2FA == true ? "required" : "not required");
            LogPanelAdmins(app.Logger, options);
        }
        app.Logger.LogInformation("Data dir {DataDir}", options.DataDir);
        LogGmPolicy(app.Logger, options, environmentName);
        if (!OperatingSystem.IsWindows() && Environment.IsPrivilegedProcess) LogRunningAsRoot(app.Logger);
        LogTesting(app.Logger, options.Testing);
        if (options.RefuseUnknownAccounts && !(options.Panel.Enabled && options.Panel.Registration)) LogNoSignUp(app.Logger);
        // D1 inventory across many short-lived servers (a whole test suite): each appends its loop waits on stop.
        if (Environment.GetEnvironmentVariable("RO3EMU_LOOPWAIT_DUMP") is { Length: > 0 } dump)
            app.Lifetime.ApplicationStopped.Register(() => Diagnostics.LoopWaitDump.Append(dump, app.Services.GetRequiredService<AccountStore>().AccessMetrics));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web panel on {Url}")]
    private static partial void LogPanel(ILogger logger, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Web panel: two-factor authentication for GM and Admin accounts {State} (Ro3Emu:Panel:RequireAdmin2FA)")]
    private static partial void LogPanelTwoFactor(ILogger logger, string state);

    /// <summary>Says at startup who has GM in game, loudly when it is everyone on a public address or nobody.</summary>
    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! The server is running as root: a hole in it would own the whole machine. Run it as an ordinary user (docs/guide/hosting.md).")]
    private static partial void LogRunningAsRoot(ILogger logger);

    private static void LogGmPolicy(ILogger logger, ServerOptions options, string environment)
    {
        if (options.GmAccounts.Length > 0)
        {
            logger.LogInformation("GM accounts: {Accounts}", string.Join(", ", options.GmAccounts));
            return;
        }
        bool local = options.LocalOnly();
        string where = local ? "local only" : $"reachable from other machines: listens on {options.ListenAddress()}, public host {options.PublicHost}";
        if (options.GmOpenWhenEmpty == false)
        {
            logger.LogWarning("!!! Ro3Emu:GmAccounts is empty, so NO account has GM commands in game " +
                "(environment {Environment}, {Where}): an empty list means everyone only on a local server. " +
                "Set Ro3Emu__GmAccounts__0=<account> (docs/guide/gm-access.md). The loopback /ro3emu/gm tool " +
                "still works.", environment, where);
            return;
        }
        // Open on a server others reach only by an explicit Ro3Emu:GmOpenWhenEmpty. A plain local start has no
        // environment set, and the host then calls itself Production, so the name alone says nothing.
        if (!local)
            logger.LogWarning("!!! Ro3Emu:GmAccounts is empty and GM is open: EVERY account can use GM commands in game " +
                "(environment {Environment}, {Where}, Ro3Emu:GmOpenWhenEmpty set). For a public server list the GM " +
                "accounts: Ro3Emu__GmAccounts__0=<account> (docs/guide/gm-access.md).", environment, where);
        else
            logger.LogInformation("GM: every account (Ro3Emu:GmAccounts is empty; local or test server)");
    }

    /// <summary>Says at startup who is a configured Admin of the web panel, loudly when nobody is.</summary>
    private static void LogPanelAdmins(ILogger logger, ServerOptions options)
    {
        var admins = options.GmAccounts.Concat(options.Panel.Roles
            .Where(r => Ro3Emu.Panel.PanelRoles.Parse(r.Value) == Ro3Emu.Panel.PanelRole.Admin).Select(r => r.Key))
            .Distinct(StringComparer.Ordinal).ToList();
        if (admins.Count > 0) LogPanelAdminList(logger, admins);
        else if (StaffRoles.OpenGmAdmin(options)) LogPanelAdminsOpen(logger);
        else LogPanelNoAdmin(logger);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Web panel Admins: {Accounts}")]
    private static partial void LogPanelAdminList(ILogger logger, IReadOnlyList<string> accounts);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Web panel Admins: every account (GmAccounts is empty and GM is open on a local panel)")]
    private static partial void LogPanelAdminsOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Web panel: no Admin in the configuration. Set " +
        "Ro3Emu__GmAccounts__0=<account> (docs/guide/panel.md); Admins given on the panel's Staff roles page keep " +
        "their role.")]
    private static partial void LogPanelNoAdmin(ILogger logger);

    /// <summary>Ro3Emu:Testing switches are test aids, never the original game: say so loudly while one is on.</summary>
    private static void LogTesting(ILogger logger, TestingOptions testing)
    {
        if (testing.AlwaysOpen) LogAlwaysOpen(logger);
        if (testing.UnlimitedRewards) LogUnlimitedRewards(logger);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Ro3Emu:Testing:AlwaysOpen is ON: every To-Do activity is open at " +
        "any hour, its occurrence repeating all day. TEST AID, not original behaviour; turn it off for a real realm " +
        "(docs/guide/hosting.md).")]
    private static partial void LogAlwaysOpen(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Ro3Emu:Testing:UnlimitedRewards is ON: dungeon, MVP, Commendation " +
        "Vault and Dodo's Request reward counts never run out. TEST AID, not original behaviour; turn it off for a real realm " +
        "(docs/guide/hosting.md).")]
    private static partial void LogUnlimitedRewards(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "!!! Ro3Emu:RefuseUnknownAccounts is on but the web panel's sign-up is " +
        "off: no new account can be created. Turn on Ro3Emu:Panel:Enabled and Ro3Emu:Panel:Registration (docs/guide/hosting.md).")]
    private static partial void LogNoSignUp(ILogger logger);
}

/// <summary>
/// Adds the loop's fixed tickers, in the order it ticks them, just before the loop starts. Each used to add itself
/// from its constructor; resolving them any earlier would change the order the services subscribe to each other's
/// events. Map instances and the visibility rebalancer join later as the world opens maps.
/// </summary>
internal sealed class GameLoopTickers(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var loop = services.GetRequiredService<GameLoop>();
        void Add<T>() where T : IGameTicker => loop.Add(services.GetRequiredService<T>());
        Add<Deadlines>();
        Add<MonsterService>();
        Add<BuffService>();
        Add<CombatService>();
        Add<SeasonService>();
        Add<MountService>();
        Add<RidingEffects>();
        Add<StealthEffects>();
        Add<PassiveSkills>();
        Add<TeamService>();
        Add<DailyActivityService>();
        Add<TimedEventService>();
        Add<PronteraDefenseService>();
        Add<TimedBattleService>();
        Add<CasualBattleService>();
        Add<FestivalService>();
        Add<RankRewardService>();
        Add<TrainingService>();
        Add<GuildHomeService>();
        Add<GuildCampfireService>();
        Add<GuildHuntingService>();
        Add<GuildChallengeService>();
        Add<DraculaWeddingService>();
        Add<AdventureGroupService>();
        Add<GuildCaravanService>();
        Add<WildAfkService>();
        Add<MvpService>();
        Add<TerritoryWarsService>();
        Add<TerritoryEscortService>();
        Add<GuildLeagueService>();
        Add<WorldCoreService>();
        Add<EmojiService>();
        Add<AchievementService>();
        Add<FirstHeartService>();
        Add<DungeonMatchService>();
        Add<GatheringService>();
        Add<TotemEffects>();
        Add<NightCycleService>();
        Add<Game.Persistence.PositionPersistence>();
        Add<Game.Persistence.SettingsPersistence>();
        Add<Game.Persistence.WildAfkPersistence>();
        Add<Diagnostics.SessionCensus>();
        Add<ShopService>(); // 05:00 purchase-limit rollover
        Add<ItemBoxService>(); // award boxes; 05:00 item use-time rollover
        Add<PowerService>(); // throttled 31502/31503 republish of changed power
        Add<Voice.VoiceRooms>(); // VAD silence timeout every tick, team-membership sweep every second
        Add<GuildVoiceService>(); // guild voice channels: drops the offline and those no longer in the guild, every second
        Add<TavernService>(); // Tavern rooms: time in a room, offline members, mutes and tags, every second
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class GameLoopQuiesceService(GameLoop loop) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) { loop.Quiesce(); return Task.CompletedTask; }
}

/// <summary>Tracks the open HTTP connections so a stop that Kestrel is still draining can abort them after a short grace.</summary>
internal sealed class HttpConnectionReaper
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Microsoft.AspNetCore.Connections.ConnectionContext, byte> _open = new();

    public async Task Track(Microsoft.AspNetCore.Connections.ConnectionContext connection, Func<Task> next)
    {
        _open[connection] = 0;
        try { await next(); }
        finally { _open.TryRemove(connection, out _); }
    }

    public void AbortAfter(TimeSpan grace) => _ = Task.Delay(grace).ContinueWith(_ =>
    {
        foreach (var connection in _open.Keys) connection.Abort();
    }, TaskScheduler.Default);
}
