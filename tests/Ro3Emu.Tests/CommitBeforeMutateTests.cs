using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class CommitBeforeMutateTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task GuildRenameDoesNotPublishNewNameWhileSqliteCommitIsInFlight()
    {
        await using var server = await TestServer.StartSyntheticAsync();
        await using var bot = await server.InWorldAsync("Leader", account: "renameleader");
        var session = server.Session(bot);
        long roleId = session.Role!.Id;

        var accounts = server.Service<AccountStore>();
        var guilds = server.Service<GuildStore>();
        var guildService = server.Service<GuildService>();

        // Seed a committed guild owned by the logged-in leader on synthetic data.
        var initial = new GuildStore.State
        {
            Name = "OldGld",
            Announce = "Original",
            CreateTime = server.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Status = (int)GuildStatus.Normal,
        };
        initial.Members[roleId] = new GuildStore.Member
        {
            RoleId = roleId,
            Name = "Leader",
            Level = 40,
            Career = 1000,
            EnterTime = initial.CreateTime,
            OfflineTime = -1,
            Official = GuildOfficial.Leader,
        };
        bool seeded = false;
        await server.Loop.InvokeAsync(() => guildService.RunGuildAsync(session, [], [], async () =>
        {
            seeded = await guildService.SaveGuildAsync(initial);
        }));
        Assert.True(seeded);
        long guildId = initial.Id;
        Assert.Equal("OldGld", guilds.Get(guildId)!.Name);
        Assert.Equal("OldGld", await server.Loop.InvokeAsync(() => Task.FromResult(session.Guild?.GuildName)));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "guild/ordinary-save") return;
            entered.Set();
            if (!release.Wait(Wait)) throw new TimeoutException("guild rename commit barrier timed out");
        };

        try
        {
            await bot.SendGameAsync(GuildService.Edit, new GuildEditReq
            {
                GuildId = guildId,
                Tp = (GuildEditReq.Types.EditType)2,
                Name = "NewGld",
            });

            Assert.True(await Task.Run(() => entered.Wait(Wait)), "Expected rename commit to reach SQLite barrier.");

            // While the SQLite commit is still in flight, both the durable store and live session view must still report OldGld.
            Assert.Equal("OldGld", guilds.Get(guildId)!.Name);
            Assert.Equal("OldGld", await server.Loop.InvokeAsync(() => Task.FromResult(session.Guild?.GuildName)));

            release.Set();
            var reply = await TestServer.NextAsync(bot, GuildService.Edit, GuildEditResp.Parser, timeout: Wait);
            Assert.Equal(0, reply.Result);
            Assert.Equal("NewGld", reply.Name);
            Assert.Equal("NewGld", guilds.Get(guildId)!.Name);
            Assert.Equal("NewGld", await server.Loop.InvokeAsync(() => Task.FromResult(session.Guild?.GuildName)));
        }
        finally
        {
            release.Set();
            accounts.BeforeCommitForTest = null;
        }
    }

    [Fact]
    public async Task GuildEditDoesNotReplySuccessOrBroadcastWhenCommitRefuses()
    {
        await using var server = await TestServer.StartSyntheticAsync();
        await using var bot = await server.InWorldAsync("Editor", account: "editfail");
        var session = server.Session(bot);
        long roleId = session.Role!.Id;

        var accounts = server.Service<AccountStore>();
        var guilds = server.Service<GuildStore>();
        var guildService = server.Service<GuildService>();

        var initial = new GuildStore.State
        {
            Name = "StableGuild",
            Announce = "CommittedNotice",
            CreateTime = server.Clock.GetUtcNow().ToUnixTimeMilliseconds(),
            Status = (int)GuildStatus.Normal,
        };
        initial.Members[roleId] = new GuildStore.Member
        {
            RoleId = roleId,
            Name = "Editor",
            Level = 40,
            Career = 1000,
            EnterTime = initial.CreateTime,
            OfflineTime = -1,
            Official = GuildOfficial.Leader,
        };
        bool seeded = false;
        await server.Loop.InvokeAsync(() => guildService.RunGuildAsync(session, [], [], async () =>
        {
            seeded = await guildService.SaveGuildAsync(initial);
        }));
        Assert.True(seeded);
        long guildId = initial.Id;

        accounts.BeforeCommitForTest = label =>
        {
            if (label == "guild/ordinary-save")
                throw new SqliteException("injected constraint refusal", 19, 2067);
        };

        try
        {
            await bot.SendGameAsync(GuildService.Edit, new GuildEditReq
            {
                GuildId = guildId,
                Tp = (GuildEditReq.Types.EditType)1,
                Announce = "UncommittedNotice",
            });

            var reply = await TestServer.NextAsync(bot, GuildService.Edit, GuildEditResp.Parser, timeout: Wait);
            Assert.NotEqual(0, reply.Result);
            Assert.Equal("CommittedNotice", reply.Announce);
            Assert.Equal("CommittedNotice", guilds.Get(guildId)!.Announce);
        }
        finally
        {
            accounts.BeforeCommitForTest = null;
        }
    }

    [ClientDataFact]
    public async Task CardLibraryThemeSwitchDoesNotPublishBeforeCommit()
    {
        await using var server = await TestServer.StartClientAsync();
        await using var bot = await server.InWorldAsync("ThemeTester", account: "themebot");
        var session = server.Session(bot);

        var accounts = server.Service<AccountStore>();
        int initialTheme = await server.Loop.InvokeAsync(() => Task.FromResult(session.Profile.Library!.OverviewTheme));
        int targetTheme = initialTheme == 1 ? 2 : 1;
        if (targetTheme == 2)
            await server.GmAsync(bot, "!item 118 2000");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        accounts.BeforeCommitForTest = label =>
        {
            if (label != "card/switch-theme") return;
            entered.Set();
            if (!release.Wait(Wait)) throw new TimeoutException("card theme commit barrier timed out");
        };

        try
        {
            await bot.SendGameAsync(CardService.LibrarySwitchTheme, new LibrarySwitchThemeReq { ThemeId = targetTheme });
            Assert.True(await Task.Run(() => entered.Wait(Wait)), "Expected card theme commit to reach SQLite barrier.");

            // Live session library must still reflect initialTheme while SQLite commit is in flight.
            Assert.Equal(initialTheme, await server.Loop.InvokeAsync(() => Task.FromResult(session.Profile.Library!.OverviewTheme)));

            release.Set();
            var reply = await TestServer.NextAsync(bot, CardService.LibrarySwitchTheme, LibrarySwitchThemeResp.Parser, timeout: Wait);
            Assert.Equal(0, reply.Result);
            Assert.Equal(targetTheme, reply.ThemeId);
            Assert.Equal(targetTheme, await server.Loop.InvokeAsync(() => Task.FromResult(session.Profile.Library!.OverviewTheme)));
        }
        finally
        {
            release.Set();
            accounts.BeforeCommitForTest = null;
        }
    }
}
