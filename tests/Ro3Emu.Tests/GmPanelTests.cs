using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The client's GM panel (30300) on ops the emulator does not handle: a single op's refusal names the op, and a preset
/// (GMCombinationConfig) runs what it can and summarizes the steps it skipped. Preset 900 in the synthetic data is invented.
/// </summary>
public sealed class GmPanelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>A started host with one bot in the world; disposing removes the data directory.</summary>
    private sealed class Host(WebApplication app, BotClient alice, string dataDir) : IAsyncDisposable
    {
        public static async Task<Host> StartAsync()
        {
            string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-gmpanel-" + Guid.NewGuid().ToString("N"));
            var (httpPort, microPort, gamePort) = TestPorts.FreePorts();
            var app = ServerHost.Create([], o =>
            {
                o.DataDir = dataDir;
                o.GameDataDir = SyntheticData.Dir;
                o.HttpPort = httpPort;
                o.MicroGatePort = microPort;
                o.GameGatePort = gamePort;
            });
            await app.StartAsync();
            var alice = new BotClient($"http://127.0.0.1:{httpPort}", File.ReadAllText(Path.Combine(dataDir, "gate_rsa_public.pem")));
            await alice.LoginAsync("alice", "pw");
            await alice.EnterGateAsync();
            await alice.EnterWorldAsync((await alice.CreateRoleAsync("Alice")).RoleBaseInfo.Id);
            return new Host(app, alice, dataDir);
        }

        public async Task<string> PanelReplyAsync(GeneralGMProcReq req)
        {
            await alice.SendGameAsync(GmCommands.GeneralProcGm, req);
            return GeneralGMProcRsp.Parser.ParseFrom((await alice.Inbox.WaitAsync(e => e.IsGame(GmCommands.GeneralProcGm), Wait)).Body).ErrInfo;
        }

        public async ValueTask DisposeAsync()
        {
            await alice.DisposeAsync();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ARefusedOpIsNamedInTheReply()
    {
        await using var host = await Host.StartAsync();
        Assert.Equal("GM op 4 GmOpStrengthen is not implemented by the emulator.",
            await host.PanelReplyAsync(new GeneralGMProcReq { GmType = GmOpEnum.GmOpStrengthen, ParamString = "1,5" }));
    }

    [Fact]
    public async Task AnOpNumberTheEnumDoesNotDefineIsRefusedWithoutAName()
    {
        await using var host = await Host.StartAsync();
        Assert.Equal("GM op 4242 is not implemented by the emulator.",
            await host.PanelReplyAsync(new GeneralGMProcReq { GmType = (GmOpEnum)4242, ParamString = "" }));
    }

    [Fact]
    public async Task APresetRunsWhatItCanAndSummarizesTheSkippedSteps()
    {
        // Preset 900: GmClearPack (handled), GmOpStrengthen twice and "251" (GMWarRobe, named by number) not handled,
        // and one name that is no op at all.
        await using var host = await Host.StartAsync();
        string reply = await host.PanelReplyAsync(new GeneralGMProcReq { GmListId = 900 });
        var lines = reply.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal("NoSuchOp: unknown op", lines[1]);
        Assert.Equal("Skipped 3 steps the emulator does not implement: GmOpStrengthen x2, GMWarRobe x1", lines[2]);
    }

    [Fact]
    public async Task APresetWithOnlyHandledStepsHasNoSkippedLine()
    {
        await using var host = await Host.StartAsync();
        string reply = await host.PanelReplyAsync(new GeneralGMProcReq { GmListId = 901 });
        Assert.DoesNotContain("Skipped", reply);
    }
}
