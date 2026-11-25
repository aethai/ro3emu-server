using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server;

/// <summary>Guilds, guild events, adventure groups, guild league, voice, and guild GM commands.</summary>
internal static class GuildComposition
{
    public static void AddGuild(IServiceCollection services)
    {
        void AddLifecycle<T>() where T : class, IRoleLifecycle =>
            services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<T>());

        services.AddSingleton<GuildStore>();
        services.AddSingleton<GuildHomeService>();
        services.AddSingleton<GuildCampfireService>();
        services.AddSingleton<Game.Messaging.INpcInteractionHandler>(sp => sp.GetRequiredService<GuildCampfireService>());
        services.AddSingleton<GuildHuntingService>();
        services.AddSingleton<GuildChallengeStore>();
        services.AddSingleton<GuildChallengeService>();
        services.AddSingleton<DraculaWeddingService>();
        services.AddSingleton<GuildService>();
        services.AddSingleton<GuildWartimeService>();
        services.AddSingleton<GuildVoiceService>();
        services.AddSingleton<AdventureGroupStore>();
        services.AddSingleton<AdventureGroupService>();
        services.AddSingleton<GuildCaravanService>();
        services.AddSingleton<GuildLeagueCalendar>();
        services.AddSingleton<GuildLeagueService>();
        services.AddSingleton<ICombatRule>(sp => sp.GetRequiredService<GuildLeagueService>());

        AddLifecycle<GuildService>();
        AddLifecycle<AdventureGroupService>();
        AddLifecycle<GuildCaravanService>();
        AddLifecycle<GuildChallengeService>();
    }

    public static void WireGuildEvents(WebApplication app)
    {
        app.Services.GetRequiredService<GuildService>().BindWartime(app.Services.GetRequiredService<GuildWartimeService>());
        app.Services.GetRequiredService<GuildHomeService>();
        app.Services.GetRequiredService<GuildCampfireService>();
        app.Services.GetRequiredService<GuildHuntingService>(); // boss kills, damage, home entry and the 5840 guard
        app.Services.GetRequiredService<GuildChallengeService>();
        app.Services.GetRequiredService<DraculaWeddingService>(); // home scene hook, lair tasks and the !dracula aid
        app.Services.GetRequiredService<AdventureGroupService>(); // creation-instance NPCs, group quest gate, scene hook
        app.Services.GetRequiredService<GuildCaravanService>(); // 05:00 settlement also covers offline orders
    }

    public static void WireGuildLeague(WebApplication app)
    {
        app.Services.GetRequiredService<GuildLeagueService>(); // 30410-30412 hooks, battlefield scene/combat hooks
    }

    public static void WireGuildTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        var league = app.Services.GetRequiredService<GuildLeagueService>();
        app.Services.GetRequiredService<GuildChallengeService>().Participated.Subscribe(questTasks.OnGveJoined); // 103014
        league.MatchCommitted.Subscribe(questTasks.OnGuildLeagueMatch); // 106603, 106019
    }

    public static void WireGuildCaravanAward(WebApplication app)
    {
        app.Services.GetRequiredService<TaskService>().FactionGuildAward = app.Services.GetRequiredService<GuildCaravanService>().PayFactionAward;
    }

    public static void WireGuildSocialHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        var campfire = app.Services.GetRequiredService<GuildCampfireService>();
        var guild = app.Services.GetRequiredService<GuildService>();
        var league = app.Services.GetRequiredService<GuildLeagueService>();
        var guildVoice = app.Services.GetRequiredService<GuildVoiceService>();
        var loop = app.Services.GetRequiredService<GameLoop>();
        var world = app.Services.GetRequiredService<Maps.World>();

        campfire.Joined.Subscribe(questTasks.OnCampfireJoined); // 106018
        guild.MembershipChanged.Subscribe(questTasks.OnStateChanged); // 104007
        campfire.Joined.Subscribe(questTasks.OnBonfireJoined); // 103013
        league.MatchCommitted.Subscribe(questTasks.OnGuildLeagueFought); // 103015
        guild.MembershipChanged.Subscribe(guildVoice.OnMembershipChanged);
        guild.GuildIdChanged.Subscribe(
            s => loop.Post(() => world.ReshowPlayerAsync(s)),
            name: "GuildComposition.ReshowPlayerOnGuildIdChanged");
        guild.RightsChanged.Subscribe(guildVoice.OnRightsChanged);
    }

    public static void RegisterGuildGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var data = app.Services.GetRequiredService<GameData>();
        var guilds = app.Services.GetRequiredService<GuildStore>();
        var functions = app.Services.GetRequiredService<FunctionService>();
        var guildService = app.Services.GetRequiredService<GuildService>();
        var challenge = app.Services.GetRequiredService<GuildChallengeService>();
        var hunting = app.Services.GetRequiredService<GuildHuntingService>();
        var league = app.Services.GetRequiredService<GuildLeagueService>();

        gm.Register("league", "register | fill | status (Guild League test aid: register the current or next tournament now; "
            + "fill your guild's elite roster now; show it)", league.GmAsync);

        gm.Register("guildchallenge", "open|close (test aid)", async (s, a) =>
        {
            if (a.Length != 1 || a[0] is not ("open" or "close")) throw new ArgumentException("Use !guildchallenge open|close");
            await challenge.TestWindowAsync(a[0] == "open");
            await GmCommands.ReplyAsync(s, a[0] == "open" ? "Guild Challenge test window open for 90 minutes; close or restart restores the real schedule."
                : "Guild Challenge test window closed; real schedule restored.");
        });
        gm.Register("guildhunt", "open|close (test aid)", async (s, a) =>
        {
            if (a.Length != 1 || a[0] is not ("open" or "close")) throw new ArgumentException("Use !guildhunt open|close");
            hunting.TestWindow(a[0] == "open");
            await GmCommands.ReplyAsync(s, a[0] == "open" ? "Hunting Challenge test window open: candidates now, combat in 2 minutes."
                : "Hunting Challenge test window removed; real schedule restored.");
        });

        gm.RegisterOp(GmCommands.OpGuild, async (s, args) =>
        {
            string sub = args[0].ToLowerInvariant();
            if (sub is "leaguematch" or "guildleague" or "loadleaguedebugjson")
                return $"GmOpGuild {sub}: an original league debug hook the emulator does not have; see !league register|fill|status.";
            if (sub != "create" || args.Length < 2 || args[1].Length == 0) throw new ArgumentException("create,<guild name>");
            string name = string.Join(',', args[1..]);
            if (guilds.ForRole(s.Role!.Id) is { } own) return $"Already in guild {own.Name} ({own.Id}).";
            if (!functions.IsOpen(s, GmCommands.GuildFunction)) return "The guild function is not open for this role yet.";
            var icons = GuildIconConfig.All(data).Where(i => i.CreateShow != 0 && i.DefaultUnlock == 1).ToList();
            var icon = new GuildHeadIcon
            {
                BackgroundType = icons.Where(i => i.Type == 1).Select(i => i.Id).FirstOrDefault(),
                IconType = icons.Where(i => i.Type == 2).Select(i => i.Id).FirstOrDefault(),
            };
            await guildService.CreateInstantAsync(s, new GuildCreateReq { Name = name, Icon = icon });
            return guilds.ForRole(s.Role.Id) is { } created
                ? $"Guild {created.Name} ({created.Id}) created."
                : $"Guild {name} not created; the 32210 reply carries the reason.";
        });
    }
}
