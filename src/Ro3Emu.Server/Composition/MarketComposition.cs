using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Server;

/// <summary>Shops, item boxes, exchange trade, stalls, auctions, and auction GM commands.</summary>
internal static class MarketComposition
{
    public static void AddMarket(IServiceCollection services)
    {
        void AddLifecycle<T>() where T : class, IRoleLifecycle =>
            services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<T>());

        services.AddSingleton<AuctionStore>();
        services.AddSingleton<AuctionService>();
        services.AddHostedService(sp => sp.GetRequiredService<AuctionService>());
        services.AddSingleton<MarketStore>();
        services.AddSingleton<IStartupInitializer>(sp => sp.GetRequiredService<MarketStore>());
        services.AddSingleton<ShopService>();
        services.AddSingleton<ItemBoxService>();
        services.AddSingleton<TradeService>();
        services.AddSingleton<StallsService>();

        AddLifecycle<ShopService>();
        AddLifecycle<AuctionService>();
        AddLifecycle<TradeService>();
        AddLifecycle<StallsService>();
    }

    public static void WireMarketTaskHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<TradeService>().Bought.Subscribe(questTasks.OnExchangeBought); // 103002
        app.Services.GetRequiredService<AuctionService>().GuildBid.Subscribe(questTasks.OnGuildAuctionBid); // 103003
    }

    public static void RegisterMarketGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var guilds = app.Services.GetRequiredService<GuildStore>();
        var auctions = app.Services.GetRequiredService<AuctionService>();
        var loop = app.Services.GetRequiredService<GameLoop>();

        // stage 0 = full-clear shortcut to the final stage's real drop pool, not the preview-only base award.
        gm.Register("guildauction", "[challengeId=1001] [stage=0]", async (s, a) =>
        {
            var guild = guilds.ForRole(s.Role!.Id) ?? throw new ArgumentException("Join a guild first");
            int challenge = a.Length > 0 ? GmCommands.I(a[0]) : 1001, stage = a.Length > 1 ? GmCommands.I(a[1]) : 0;
            int code = await auctions.PublishGuildChallengeAsync(
                "gm:" + loop.NowMs, guild.Id, challenge, stage, [s.Role.Id]);
            await GmCommands.ReplyAsync(s, code == 0 ? $"guild auction published (challenge {challenge}, stage {stage})" : $"guild auction failed: {code}");
        });
        // Test aids, not original behaviour: system World lots, and Event lots with the GM as the dividend participant.
        gm.Register("worldauction", "open [itemId...] | close (test aid)", async (s, a) =>
            await GmCommands.ReplyAsync(s, await auctions.WorldGmAsync(a)));
        gm.Register("actauction", "<3001|3002|3003> open [itemId...] | close (test aid)", async (s, a) =>
            await GmCommands.ReplyAsync(s, await auctions.EventGmAsync(s.Role!.Id, a)));
    }
}
