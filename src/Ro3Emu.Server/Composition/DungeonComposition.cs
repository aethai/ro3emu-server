using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server;

/// <summary>Dungeons, dungeon matchmaking, instances, Endless Tower, Valhalla, and dungeon GM commands.</summary>
internal static class DungeonComposition
{
    public static void AddDungeon(IServiceCollection services)
    {
        void AddLifecycle<T>() where T : class, IRoleLifecycle =>
            services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<T>());

        services.AddSingleton<ReconstructedDungeons>();
        services.AddSingleton<DungeonMatchService>();
        services.AddSingleton<InstanceService>();
        services.AddSingleton<DungeonCatalog>();
        services.AddSingleton<DungeonObligationStore>();
        services.AddSingleton<DungeonService>();
        services.AddSingleton<TowerService>();
        services.AddSingleton<ValhallaService>();

        AddLifecycle<TowerService>();
        AddLifecycle<ValhallaService>();
        AddLifecycle<DungeonService>();
        AddLifecycle<DungeonMatchService>();
        AddLifecycle<InstanceService>();
    }

    public static void WireDungeonTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        var dungeons = app.Services.GetRequiredService<DungeonService>();
        // A committed dungeon clear counts toward quest target 101018 (story steps such as Payon Forest, the airship).
        dungeons.Completed.Subscribe(
            (s, scene, _) => questTasks.OnDungeonClearedAsync(s, scene),
            name: "DungeonComposition.TaskDungeonCleared");
        dungeons.MultiBossCleared.Subscribe(questTasks.OnTenBossClearedAsync); // 106602
    }

    public static void WireDungeonRunAndTowerTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<DungeonService>().RunTracked.Subscribe(questTasks.OnDungeonRun);
        app.Services.GetRequiredService<TowerService>().Settled.Subscribe(questTasks.OnTowerSettled); // 101007, 101010
    }

    public static void WireDungeonConnectHook(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<DungeonService>().MultiBossCleared.Subscribe(questTasks.OnConnectBossClearedAsync); // 107005
    }

    public static void RegisterDungeonGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var data = app.Services.GetRequiredService<GameData>();
        var match = app.Services.GetRequiredService<DungeonMatchService>();
        var dungeons = app.Services.GetRequiredService<DungeonService>();

        gm.Register("dungeonsolo", "on|off (test aid, this role session only)", async (s, a) =>
        {
            if (a.Length != 1 || a[0].ToLowerInvariant() is not ("on" or "off")) throw new ArgumentException("on|off");
            // Changing a test policy cancels any outstanding queue/vote before the next Enter Challenge.
            await match.LeaveRoleAsync(s, notifySelf: true);
            s.DungeonSoloMatch = a[0].Equals("on", StringComparison.OrdinalIgnoreCase);
            await GmCommands.ReplyAsync(s, $"Dungeon solo TEST AID {(s.DungeonSoloMatch ? "ON" : "OFF")} for this role session. "
                + "Click Enter Challenge again. This is not original matchmaking behavior.");
        });
        gm.Register("dungeon", "<sceneEntry> (test aid: the client's 30400 Enter for a solo story dungeon)", async (s, a) =>
        {
            if (a.Length != 1 || !long.TryParse(a[0], out long scene)) throw new ArgumentException("<sceneEntry>");
            // The same entry the client's story step sends; admission, loading and settlement stay the server's own.
            await dungeons.EnterAsync(s, new EnterDungeonReq { SceneEntry = scene });
            await GmCommands.ReplyAsync(s, $"Dungeon {scene}: entry requested (test aid).");
        });

        gm.RegisterOp(GmCommands.OpEnterDungeon, (s, args) => EnterDungeonAsync(data, dungeons, s, GmCommands.L(args[0])));
        gm.RegisterOp(GmCommands.OpDungeonCmd, async (s, args) =>
        {
            switch (args[0].ToLowerInvariant())
            {
                case "enter":
                    return GmCommands.Arg(args, 1) is { Length: > 0 } id
                        ? await EnterDungeonAsync(data, dungeons, s, GmCommands.L(id))
                        : "Enter needs a dungeon id: use GmEnterDungeon.";
                case "leave":
                    if (s.InstanceId == 0) return "Not in a dungeon.";
                    await dungeons.LeaveAsync(s);
                    return "Dungeon left.";
                default:
                    throw new ArgumentException("enter|leave");
            }
        });
    }

    private static async Task<string> EnterDungeonAsync(GameData data, DungeonService dungeons, GameSession s, long dungeon)
    {
        if (data.Row("DungeonConfig", dungeon) is null) return $"No dungeon {dungeon}.";
        await dungeons.EnterAsync(s, new EnterDungeonReq { SceneEntry = dungeon });
        return $"Dungeon {dungeon}: entry requested; the 30400 reply carries the result.";
    }
}
