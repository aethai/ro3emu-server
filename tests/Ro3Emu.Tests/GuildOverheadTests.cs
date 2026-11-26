using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class GuildOverheadTests : IAsyncLifetime
{
    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-guild-overhead-" + Guid.NewGuid().ToString("N"));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private WebApplication _app = null!;
    private string _http = "";

    public async Task InitializeAsync()
    {
        var (web, micro, gate) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dataDir;
            o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = web;
            o.MicroGatePort = micro;
            o.GameGatePort = gate;
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{web}";
    }

    public async Task DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }

    private async Task<BotClient> InWorld(string name, bool create = true)
    {
        var bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dataDir, "gate_rsa_public.pem")));
        await bot.LoginAsync(name.ToLowerInvariant(), "pw");
        await bot.EnterGateAsync();
        long id = create ? (await bot.CreateRoleAsync(name)).RoleBaseInfo.Id : bot.Roles.Single().Id;
        await bot.EnterWorldAsync(id);
        await WorldSettle.StallsAsync(_app.Services);
        return bot;
    }

    private async Task Level(string name)
    {
        using var client = new HttpClient();
        (await client.PostAsync($"{_http}/ro3emu/gm?account={name.ToLowerInvariant()}", Operator.Command("!lvl 21"))).EnsureSuccessStatusCode();
    }

    private static async Task<T> Next<T>(BotClient bot, uint id, Google.Protobuf.MessageParser<T> parser, Func<T, bool>? match = null)
        where T : Google.Protobuf.IMessage<T>
    {
        var e = await bot.Inbox.WaitAsync(e => e.IsGame(id) && (match == null || match(parser.ParseFrom(e.Body))), Wait);
        return parser.ParseFrom(e.Body);
    }

    [ClientDataFact]
    public async Task EntityBaseCarriesGuildIdOnLoginAndForViewers()
    {
        await using var founder = await InWorld("OverheadFounder");
        await Level("OverheadFounder");
        await founder.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Emblem",
            Announce = "",
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(founder, GuildService.Create, GuildCreateResp.Parser)).Result);
        var full = await Next(founder, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0);
        long guildId = full.Guid;
        long founderRoleId = founder.Roles.Single().Id;
        await founder.DisposeAsync();

        await using var relog = await InWorld("OverheadFounder", create: false);
        Assert.Equal(guildId, relog.Self!.GuildId);

        await using var viewer = await InWorld("OverheadViewer");
        var enter = await Next(viewer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            m => m.Bases.Any(b => b.Uuid == (ulong)founderRoleId));
        var seen = enter.Bases.Single(b => b.Uuid == (ulong)founderRoleId);
        Assert.Equal(guildId, seen.GuildId);
    }

    [ClientDataFact]
    public async Task CreatingAndLeavingGuildReshowsPlayerToViewers()
    {
        await using var actor = await InWorld("ReshowActor");
        await Level("ReshowActor");
        long actorRoleId = actor.Roles.Single().Id;
        await using var viewer = await InWorld("ReshowViewer");

        var initial = await Next(viewer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            m => m.Bases.Any(b => b.Uuid == (ulong)actorRoleId));
        Assert.Equal(0, initial.Bases.Single(b => b.Uuid == (ulong)actorRoleId).GuildId);

        await actor.SendGameAsync(GuildService.Create, new GuildCreateReq
        {
            Name = "Reshow",
            Announce = "",
            Icon = new GuildHeadIcon { BackgroundType = 100101, IconType = 200101 },
        });
        Assert.Equal(0, (await Next(actor, GuildService.Create, GuildCreateResp.Parser)).Result);
        var full = await Next(actor, GuildService.Full, GuildFullInfoNtf.Parser, m => m.Guid > 0);
        long guildId = full.Guid;

        var afterCreate = await Next(viewer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            m => m.Bases.Any(b => b.Uuid == (ulong)actorRoleId && b.GuildId == guildId));
        Assert.Equal(guildId, afterCreate.Bases.Single(b => b.Uuid == (ulong)actorRoleId).GuildId);

        await actor.SendGameAsync(GuildService.Leave, new GuildMemberLeaveReq());
        Assert.Equal(0, (await Next(actor, GuildService.Leave, GuildMemberLeaveResp.Parser)).Result);

        var afterLeave = await Next(viewer, GameAoiSink.SyncAoiView, AoIViewChangeData.Parser,
            m => m.Bases.Any(b => b.Uuid == (ulong)actorRoleId && b.GuildId == 0));
        Assert.Equal(0, afterLeave.Bases.Single(b => b.Uuid == (ulong)actorRoleId).GuildId);
    }

    [Fact]
    public void GuildOverheadAllMapsIsOffByDefault() => Assert.False(new ServerOptions().GuildOverheadAllMaps);

    [ClientDataFact]
    public async Task LoginSendsGuildOverheadCommandOnlyWhenOptionIsEnabled()
    {
        var opts = _app.Services.GetRequiredService<IOptions<ServerOptions>>().Value;
        Assert.False(opts.GuildOverheadAllMaps);

        await using (var defaultBot = await InWorld("OverheadDefault"))
        {
            Assert.DoesNotContain(defaultBot.Inbox.Snapshot(),
                p => p.IsGame(GmCommands.ClientDiagnosticsMsg)
                    && Encoding.UTF8.GetString(p.Body) == GuildService.GuildOverheadEnableCommand);
        }

        opts.GuildOverheadAllMaps = true;
        try
        {
            await using var enabledBot = await InWorld("OverheadAllMaps");
            var cmd = await enabledBot.Inbox.WaitAsync(
                p => p.IsGame(GmCommands.ClientDiagnosticsMsg)
                    && Encoding.UTF8.GetString(p.Body) == GuildService.GuildOverheadEnableCommand,
                Wait);
            Assert.Equal(GuildService.GuildOverheadEnableCommand, Encoding.UTF8.GetString(cmd.Body));
        }
        finally
        {
            opts.GuildOverheadAllMaps = false;
        }
    }
}
