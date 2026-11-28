using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// 82209 friend gifts on the synthetic data: only the gift panel's items (FriendlyItemConfig, the fixture's invented
/// 3990201) may be gifted, and a gift the sender cannot pay leaves no record, intimacy or receipt.
/// </summary>
public sealed class FriendGiftTests
{
    private const long Gift = 3990201;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<int> GiveAsync(BotClient bot, long to, long item)
    {
        await bot.SendGameAsync(FriendExtraService.GiveGift, new GiveFriendGiftReq { RoleId = to, ItemId = item, Count = 1 });
        return GiveFriendGiftResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(FriendExtraService.GiveGift), Wait)).Body).Result;
    }

    private static long Scalar(SyntheticHost host, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = host.Service<AccountStore>().DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task OnlyGiftItemsGoAndAShortGiftLeavesNothing()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("ShortAlice");
        await using var bob = await host.InWorldAsync("ShortBob");
        long a = (long)alice.Self!.Uuid, b = (long)bob.Self!.Uuid;
        await alice.MicroAsync("FriendService", "apply", new() { ["Uid"] = a.ToString(), ["Target"] = b.ToString() });
        await bob.MicroAsync("FriendService", "accept", new() { ["Uid"] = b.ToString(), ["Applicant"] = a.ToString() });

        Assert.Equal(ErrorCodes.Parameter, await GiveAsync(alice, b, 3990014)); // an item the gift panel does not offer
        Assert.Equal(ErrorCodes.ItemsShort, await GiveAsync(alice, b, Gift)); // offered, but Alice has none
        var friends = host.Service<FriendStore>();
        Assert.Empty(friends.LoadMetadata(a).SendGiftRecords);
        Assert.Empty(friends.LoadMetadata(b).ReceiveGiftRecords);
        Assert.Equal(0, friends.Intimacy(a, b));
        Assert.Equal(0, Scalar(host, "SELECT COUNT(*) FROM operation_receipt WHERE command_kind='friend-gift'"));
    }
}
