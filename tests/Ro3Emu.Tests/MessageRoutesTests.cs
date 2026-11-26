using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Tests;

/// <summary>Audit F4.1: one owner per game message id, checked when the table is built.</summary>
public sealed class MessageRoutesTests
{
    private static Task Noop(GameSession s, uint id, ReadOnlyMemory<byte> b) => Task.CompletedTask;

    [Fact]
    public void ADuplicateIdOrOverlappingRangeIsRefusedWhenTheTableIsBuilt()
    {
        var routes = new MessageRoutes();
        routes.Map(100, "a", Noop);
        routes.MapRange(200, 210, "b", Noop);
        Assert.Throws<InvalidOperationException>(() => routes.Map(100, "c", Noop));
        Assert.Throws<InvalidOperationException>(() => routes.Map(205, "c", Noop));
        Assert.Throws<InvalidOperationException>(() => routes.MapRange(95, 105, "c", Noop));
        Assert.Throws<InvalidOperationException>(() => routes.MapRange(210, 220, "c", Noop));
        Assert.True(routes.TryGet(205, out var range) && range.Owner == "b");
        Assert.False(routes.TryGet(300, out _));
    }

    [Fact]
    public async Task RouteDeclaredAdmissionAndTypedSyncHandlersWork()
    {
        var routes = new MessageRoutes();
        int syncCalls = 0;
        long receivedSlot = -1;
        routes.Map(1001, "role", RouteAdmission.Role, _ => syncCalls++);
        routes.Map<EquipSwitchPlanReq>(1002, "role", RouteAdmission.Role, (_, req) => receivedSlot = req.TargetIndex);
        routes.Map(1003, "econ", RouteAdmission.Economic, _ => { });
        routes.Map(1004, "rt", RouteAdmission.Runtime, _ => { }, runtimeOrigin: "custom/rt");
        routes.Map(1005, "cond", RouteAdmission.Role, _ => { }, economicWhen: (_, _, b) => b.Length > 0);

        var session = new GameSession(null!, null!)
        {
            Account = "acc",
            Role = new Ro3Emu.Server.Accounts.Role(42, "acc", 0, "Hero", 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
        };

        await routes.DispatchAsync(session, 1001, ReadOnlyMemory<byte>.Empty, Noop);
        await routes.DispatchAsync(session, 1002, new EquipSwitchPlanReq { TargetIndex = 2 }.ToByteArray(), Noop);
        Assert.Equal(1, syncCalls);
        Assert.Equal(2, receivedSlot);

        var roleAdmit = routes.Admit(session, 1001, ReadOnlyMemory<byte>.Empty, _ => false);
        Assert.Equal(AdmissionMode.Session, roleAdmit.Mode);
        Assert.Equal([Ro3Emu.Server.Game.Ownership.ReservationKey.Role(42)], roleAdmit.Keys);

        Assert.Equal(AdmissionMode.Economic, routes.Admit(session, 1003, ReadOnlyMemory<byte>.Empty, _ => false).Mode);
        var rtAdmit = routes.Admit(session, 1004, ReadOnlyMemory<byte>.Empty, _ => false);
        Assert.Equal(AdmissionMode.Runtime, rtAdmit.Mode);
        Assert.Equal("custom/rt", rtAdmit.Origin);

        Assert.Equal(AdmissionMode.Session, routes.Admit(session, 1005, ReadOnlyMemory<byte>.Empty, _ => false).Mode);
        Assert.Equal(AdmissionMode.Economic, routes.Admit(session, 1005, new byte[] { 1 }, _ => false).Mode);
    }

    [Fact]
    public async Task TheServersTableHasEveryFormerSwitchArmOnce()
    {
        var (hp, mp, gp) = TestPorts.FreePorts();
        string dataDir = Path.Combine(Path.GetTempPath(), "ro3emu-routes-" + Guid.NewGuid().ToString("N"));
        var app = ServerHost.Create([], o =>
        {
            o.DataDir = dataDir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = hp; o.MicroGatePort = mp; o.GameGatePort = gp;
        });
        try
        {
            var routes = app.Services.GetRequiredService<GameMessageHandler>().Routes; // throws on any duplicate
            Assert.True(routes.Exact.Count >= 347, $"{routes.Exact.Count} exact routes");
            Assert.True(routes.TryGet(MsgId.SceneLoaded, out var loaded) && loaded.Owner == "scene");
            Assert.True(routes.TryGet(31183, out var wardrobe) && wardrobe.Owner == "wardrobe");
            Assert.True(routes.TryGet(82365, out var totem) && totem.Owner == "totems");
            Assert.Equal(totem.From, totem.To);
            Assert.True(routes.TryGet(82813, out var genre) && genre.Owner == "genre");
            Assert.Equal(genre.From, genre.To);
        }
        finally
        {
            await app.DisposeAsync();
            try { Directory.Delete(dataDir, true); } catch (IOException) { }
        }
    }
}
