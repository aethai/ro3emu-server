using Ro3Emu.Server.Game;

namespace Ro3Emu.Server;

/// <summary>Daily, timed, festival, MVP, training, and battle activities plus their GM test aids.</summary>
internal static class ActivitiesComposition
{
    public static void AddActivities(IServiceCollection services)
    {
        void AddLifecycle<T>() where T : class, IRoleLifecycle =>
            services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<T>());

        services.AddSingleton<DailyActivityService>();
        services.AddSingleton<TestingSwitches>();
        services.AddSingleton<ActivityClock>();
        services.AddSingleton<NewActivityService>();
        services.AddSingleton<TimedEventService>();
        services.AddSingleton<TimedTodoSchedule>();
        services.AddSingleton<PronteraDefenseService>();
        services.AddSingleton<TimedBattleService>();
        services.AddSingleton<ChickenBattleService>();
        services.AddSingleton<ChickenEncounterService>();
        services.AddSingleton<RiftBattleService>();
        services.AddSingleton<ICombatRule>(sp => sp.GetRequiredService<TimedBattleService>());
        services.AddSingleton<FestivalService>();
        services.AddSingleton<RankRewardService>();
        services.AddSingleton<TrainingService>();
        services.AddSingleton<LotteryService>();
        services.AddSingleton<MvpService>();
        services.AddSingleton<PraiseService>();
        services.AddSingleton<CasualBattleService>();
        services.AddSingleton<ICombatRule>(sp => sp.GetRequiredService<CasualBattleService>());
        services.AddSingleton<MonsterHuntService>();

        AddLifecycle<DailyActivityService>();
        AddLifecycle<MvpService>();
        AddLifecycle<MonsterHuntService>();
        AddLifecycle<NewActivityService>();
        AddLifecycle<TimedEventService>();
        AddLifecycle<PronteraDefenseService>();
        AddLifecycle<TimedBattleService>();
        AddLifecycle<CasualBattleService>();
        AddLifecycle<FestivalService>();
        AddLifecycle<TrainingService>();
        AddLifecycle<LotteryService>();
    }

    public static void WireActivities(WebApplication app)
    {
        app.Services.GetRequiredService<DailyActivityService>(); // quest settlement and online rollover
        app.Services.GetRequiredService<NewActivityService>(); // activity clocks and reward state
        app.Services.GetRequiredService<TimedEventService>();
        app.Services.GetRequiredService<PronteraDefenseService>();
        var timedBattles = app.Services.GetRequiredService<TimedBattleService>();
        timedBattles.BindChicken(app.Services.GetRequiredService<ChickenBattleService>());
        timedBattles.BindChickenEncounters(app.Services.GetRequiredService<ChickenEncounterService>());
        timedBattles.BindRift(app.Services.GetRequiredService<RiftBattleService>());
        app.Services.GetRequiredService<CasualBattleService>();
        app.Services.GetRequiredService<FestivalService>();
        app.Services.GetRequiredService<TrainingService>();
    }

    public static void WireMvp(WebApplication app)
    {
        app.Services.GetRequiredService<MvpService>(); // field MVP spawns and timers
    }

    public static void WireActivityTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<TrainingService>().ChallengeSettled.Subscribe(questTasks.OnDummyChallengeSettled); // 210006
    }

    public static void RegisterActivitiesGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var sessions = app.Services.GetRequiredService<SessionRegistry>();
        var battles = app.Services.GetRequiredService<TimedBattleService>();
        var rifts = app.Services.GetRequiredService<RiftBattleService>();
        var mvps = app.Services.GetRequiredService<MvpService>();
        var testing = app.Services.GetRequiredService<TestingSwitches>();
        var daily = app.Services.GetRequiredService<DailyActivityService>();
        var dungeons = app.Services.GetRequiredService<DungeonService>();

        gm.Register("rift", "| new | party [min] | <star 1-6> [boss name] (Rift Crisis test aid: status, new round, "
            + "smallest party, or a stone next to you)", rifts.RiftGmAsync);

        gm.Register("chicken", "shape <players> <team>|shape reset|skip|rank <n> (Survivor Battle test aids)", async (s, a) =>
        {
            if (a.Length == 3 && a[0] == "shape" && int.TryParse(a[1], out int players) && int.TryParse(a[2], out int team)
                && team > 0 && players > team && players % team == 0)
            {
                battles.ChickenTestShape = (players, team);
                await GmCommands.ReplyAsync(s, $"Survivor Battle TEST AID: {players} entrants in teams of {team} (in memory).");
            }
            else if (a.Length == 2 && a[0] == "shape" && a[1] == "reset")
            {
                battles.ChickenTestShape = null;
                await GmCommands.ReplyAsync(s, "Survivor Battle shape from the config again.");
            }
            else if (a.Length == 1 && a[0] == "skip") await GmCommands.ReplyAsync(s, await battles.SkipStageAsync(s));
            else if (a.Length == 2 && a[0] == "rank" && int.TryParse(a[1], out int rank))
                await GmCommands.ReplyAsync(s, await battles.SetChickenRankAsync(s, rank));
            else throw new ArgumentException("shape <players> <team> (players a multiple of team, more than one team) | shape reset | skip | rank <n>");
        });

        gm.Register("mvp", "[spawn|where|goto|weaken <cfg>]", async (s, a) =>
        {
            string reply = await mvps.GmAsync(a);
            if (a.Length >= 2 && a[0] == "goto" && int.TryParse(a[1], out _))
            {
                var where = (await mvps.GmAsync(["where", a[1]])).Split(' ');
                if (where.Length == 3 && long.TryParse(where[0], out long map))
                {
                    await gm.WarpAsync(s, map, GmCommands.F(where[1]) + 3, GmCommands.F(where[2]));
                    reply = "Warped next to MVP " + a[1];
                }
            }
            await GmCommands.ReplyAsync(s, reply);
        });

        // Ro3Emu:Testing (TEST AIDS, not original): To-Do activities open at any hour, rewards that never run out.
        gm.Register("testing", "[alwaysopen on|off | unlimited on|off | reset] (test aids, in memory until the next start)", async (s, args) =>
        {
            string sub = args.Length == 0 ? "" : args[0].ToLowerInvariant();
            bool? on = args.Length == 2 ? args[1].ToLowerInvariant() switch { "on" => true, "off" => false, _ => null } : null;
            if (sub == "alwaysopen" && on is { } open) testing.AlwaysOpen = open;
            else if (sub == "unlimited" && on is { } unlimited) testing.UnlimitedRewards = unlimited;
            else if (sub == "reset" && args.Length == 1) testing.Reset();
            else if (args.Length != 0) throw new ArgumentException("alwaysopen on|off | unlimited on|off | reset");
            if (args.Length != 0)
            {
                await RoleFanout.EachAsync(sessions.All().Where(p => p.Role != null && p.DailyReady), async p =>
                {
                    await daily.SyncEventsAsync(p);
                    await dungeons.SyncAsync(p);
                });
            }
            await GmCommands.ReplyAsync(s, $"Testing: {testing.Describe()} (TEST AIDS, not original behaviour; in memory until the next start).");
        });
    }
}
