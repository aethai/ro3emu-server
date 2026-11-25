using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Server;

/// <summary>Territory Wars, Royal City siege, escort carts, faction world, and territory GM commands.</summary>
internal static class TerritoryComposition
{
    public static void AddTerritory(IServiceCollection services)
    {
        services.AddSingleton<TerritoryWarsClock>();
        services.AddSingleton<TerritoryWarsService>();
        services.AddSingleton<FactionWorldService>();
        services.AddSingleton<WarMachineService>();
        services.AddSingleton<SiegeService>();
        services.AddSingleton<ICombatRule>(sp => sp.GetRequiredService<TerritoryWarsService>());
        services.AddSingleton<TerritoryEscortService>();
        services.AddSingleton<ICombatRule>(sp => sp.GetRequiredService<TerritoryEscortService>());
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<TerritoryWarsService>());
        services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<TerritoryEscortService>());
    }

    public static void WireTerritoryCore(WebApplication app)
    {
        var territory = app.Services.GetRequiredService<TerritoryWarsService>();
        territory.BindFactionWorld(app.Services.GetRequiredService<FactionWorldService>());
        territory.BindMachines(app.Services.GetRequiredService<WarMachineService>());
        territory.BindSiege(app.Services.GetRequiredService<SiegeService>());
        var escort = app.Services.GetRequiredService<TerritoryEscortService>();
        escort.OwnerOf = territory.OwnerOf;
        escort.GuildPoint = territory.GuildPoint;
        territory.GuildEscortScore = (guild, round) => escort.GetGuildEscortScore(guild, round);
        var ranks = app.Services.GetRequiredService<RankService>();
        territory.GloryBoardRank = role => ranks.RankOfAsync(RankService.BattleGlory, role);
        app.Services.GetRequiredService<GuildService>().ItemDataOf = escort.GuildItems;
    }

    public static void WireTerritoryTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        var territory = app.Services.GetRequiredService<TerritoryWarsService>();
        var escort = app.Services.GetRequiredService<TerritoryEscortService>();

        territory.SiegeSettled.Subscribe(questTasks.OnSiegeSettled); // 106611, 106605, 106606
        questTasks.BattleMeritPoints = territory.GloryPoints; // 106610
        questTasks.FactionCamp = territory.WorldCampOf; // the Faction Quest (TaskService.FactionQuest.cs)
        questTasks.FactionRivals = territory.WorldRivals; // 101023
        territory.CampChanged.Subscribe(questTasks.OnFactionCampChanged);
        territory.CrystalCollected.Subscribe(questTasks.OnFactionCrystalCollected); // 107102
        GuildComposition.WireGuildCaravanAward(app);
        territory.SiegeSettled.Subscribe(
            (role, _, _) => questTasks.OnGuildEventJoined(role, TaskService.GuildEvent.RoyalCity),
            name: "TerritoryComposition.RoyalCityJoined"); // 106010
        territory.BattleSettled.Subscribe(questTasks.OnTerritoryBattleSettled); // 106010 Stronghold / Regional Capital War
        territory.BattleSettled.Subscribe(
            (role, _) => questTasks.OnGuildWarScored(role),
            name: "TerritoryComposition.GuildWarScored"); // 106609
        questTasks.GuildWarPoints = territory.GuildWarPoints; // 106609
        territory.SiegeTracked.Subscribe(questTasks.OnDungeonRun);
        questTasks.DungeonRunValue = territory.SiegeTrackerValue;
        escort.CartStarted.Subscribe(
            s => questTasks.OnGuildEventJoined(s, TaskService.GuildEvent.Escort),
            name: "TerritoryComposition.EscortJoined"); // 106010 Escort
    }

    public static void RegisterTerritoryGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var territory = app.Services.GetRequiredService<TerritoryWarsService>();

        gm.Register("siege", "open|close|state|prepare|objects|seed <city> <attackGuild> <defenseGuild> (test aid)", async (s, a) =>
            await GmCommands.ReplyAsync(s, await territory.SiegeGmAsync(a)));
        gm.Register("twpoint", "<point> unready|ready|complete [readySeconds] [owner|self] (test aid: 30129 stronghold gameplay point)", async (s, a) =>
            await GmCommands.ReplyAsync(s, await territory.ScenePointGmAsync(s, a)));
    }
}
