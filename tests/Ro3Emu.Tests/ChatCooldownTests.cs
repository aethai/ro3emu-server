using System.Text;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// #632 Ro3Emu:ChatCooldowns on the synthetic data (the fixture's invented channels 1-3): the login sends the client hook
/// the listed channels over 77012, nothing when the option is empty, and a channel the client has not stops the start.
/// </summary>
public sealed class ChatCooldownTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static bool IsCooldownLine(Ro3Emu.Bot.BotEvent e) =>
        e.IsGame(GmCommands.ClientDiagnosticsMsg) && Encoding.UTF8.GetString(e.Body).StartsWith("chatcd", StringComparison.Ordinal);

    [Fact]
    public async Task LoginSendsTheConfiguredChannelCooldowns()
    {
        await using var host = await SyntheticHost.StartAsync(o => { o.ChatCooldowns[3] = 1; o.ChatCooldowns[2] = 0; });
        await using var alice = await host.InWorldAsync("CooldownAlice");
        var line = await alice.Inbox.WaitAsync(IsCooldownLine, Wait);
        Assert.Equal("chatcd 2=0,3=1", Encoding.UTF8.GetString(line.Body));
    }

    [Fact]
    public async Task WithoutTheOptionTheClientKeepsItsOwnCooldowns()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("CooldownBob");
        Assert.DoesNotContain(alice.Inbox.Snapshot(), IsCooldownLine);
        Assert.Null(ChatService.CooldownCommand(host.Service<GameData>(), new Dictionary<int, int>()));
    }

    [Fact]
    public async Task AChannelTheClientDoesNotHaveIsRefused()
    {
        await using var host = await SyntheticHost.StartAsync();
        var error = Assert.Throws<InvalidOperationException>(() =>
            ChatService.CooldownCommand(host.Service<GameData>(), new Dictionary<int, int> { [12] = 0 }));
        Assert.Contains("12", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3601)]
    public void ACooldownOutsideItsRangeIsRefused(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerHost.ResolveOptions([$"--Ro3Emu:ChatCooldowns:2={seconds}"]));
}
