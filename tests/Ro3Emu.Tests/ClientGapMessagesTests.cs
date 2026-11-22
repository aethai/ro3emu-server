using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Messages the real client sends that the emulator used to drop: 83271 POPUPIMAGE (the login notice popup, which only
/// ever got the unrouted empty reply) and 32807 QUERY_CLIMBOWER_FIRST_PASS, which the client asks from its role screen
/// before any role exists.
/// </summary>
public sealed class ClientGapMessagesTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(8);

    private static async Task<T> Next<T>(BotClient bot, uint msgId, MessageParser<T> parser, Func<T, bool>? match = null)
        where T : IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(msgId) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    /// <summary>The banner file the service reads, in the realm's data dir.</summary>
    private static string Banners(SyntheticHost host) => Path.Combine(host.DataDir, PopupImageService.FileName);

    [Fact]
    public async Task TheLoginPopupServesTheBannersForTheRequestingClient()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = await host.InWorldAsync("Popup");

        // No campaign file: a valid, empty answer (a missing popup, never a waiting request).
        await bot.SendGameAsync(MsgId.PopupImage, new PopupImageReq { Language = "en", Channel = "TBT_HMT_PC" });
        Assert.Empty((await Next(bot, MsgId.PopupImage, PopupImageRes.Parser)).Images);

        File.WriteAllText(Banners(host), """
            [
              { "id": 7, "title": "Season Pass", "imageUrl": "https://example.test/pass.png",
                "language": "en", "channel": "TBT_HMT_PC", "startTime": "2026-11-20 00:00:00", "rank": 2 },
              { "id": 8, "title": "Mobile notice", "imageUrl": "https://example.test/mobile.png",
                "language": "en", "channel": "TBT_HMT_MOBILE", "rank": 1 }
            ]
            """);

        // Only the banner for this channel, in rank order, with its image.
        await bot.SendGameAsync(MsgId.PopupImage, new PopupImageReq { Language = "en", Channel = "TBT_HMT_PC" });
        var served = Assert.Single((await Next(bot, MsgId.PopupImage, PopupImageRes.Parser)).Images);
        Assert.Equal(7, served.PopImageId);
        Assert.Equal("Season Pass", served.Title);
        Assert.Equal(2, served.Rank);
        Assert.Equal("https://example.test/pass.png", served.ImageInfo.ImageUrl);
        Assert.Equal("en", served.ImageInfo.Language);
    }

    [Fact]
    public async Task EditingTheBannerFileNeedsNoRestart()
    {
        await using var host = await SyntheticHost.StartAsync();
        File.WriteAllText(Banners(host), """
            [ { "id": 1, "title": "First", "imageUrl": "https://example.test/1.png" } ]
            """);
        await using var bot = await host.InWorldAsync("PopupEdit");
        await bot.SendGameAsync(MsgId.PopupImage, new PopupImageReq { Language = "en", Channel = "TBT_HMT_PC" });
        Assert.Single((await Next(bot, MsgId.PopupImage, PopupImageRes.Parser)).Images);

        // A campaign change during the day: the service re-reads the file on its next request.
        File.WriteAllText(Banners(host), """
            [
              { "id": 1, "title": "First", "imageUrl": "https://example.test/1.png" },
              { "id": 2, "title": "Second", "imageUrl": "https://example.test/2.png" }
            ]
            """);
        await bot.SendGameAsync(MsgId.PopupImage, new PopupImageReq { Language = "en", Channel = "TBT_HMT_PC" });
        var both = await Next(bot, MsgId.PopupImage, PopupImageRes.Parser, r => r.Images.Count == 2);
        Assert.Equal([1L, 2L], both.Images.Select(i => i.PopImageId));
    }

    [Fact]
    public async Task TheTowerFirstPassRangeIsAnsweredOnTheRoleScreen()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var bot = host.NewBot();
        await bot.LoginAsync("lobby", "pw");
        await bot.EnterGateAsync();

        // No role yet (the role screen): the client's own request used to be refused unread.
        await bot.SendGameAsync(TowerService.FirstPassRange, new QueryClimbTowerFirstPassRangeReq { MinLayer = 1, MaxLayer = 10 });
        var response = await Next(bot, TowerService.FirstPassRange, QueryClimbTowerFirstPassRangeResp.Parser);
        Assert.Equal(0, response.Result);
        Assert.Empty(response.Results);
    }
}
