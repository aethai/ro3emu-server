using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Tests;

/// <summary>Each map serializes its own AOI transitions; a send stuck on one map does not hold the others.</summary>
public sealed class WorldDispatchTests
{
    /// <summary>Enters to <see cref="Stuck"/> wait until <see cref="Unblock"/>.</summary>
    private sealed class BlockingSink : IAoiSink
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? Stuck { get; set; }
        public List<string> Events { get; } = [];
        public void Unblock() => _gate.TrySetResult();

        public async Task EnterAsync(GameSession viewer, GameSession subject)
        {
            if (viewer.Role!.Name == Stuck) await _gate.Task;
            lock (Events) Events.Add($"{viewer.Role.Name}+{subject.Role!.Name}");
        }

        public Task MoveAsync(GameSession viewer, GameSession subject, WalkReq walk) => Task.CompletedTask;
        public Task TurnAsync(GameSession viewer, GameSession subject, float face) => Task.CompletedTask;
        public Task LeaveAsync(GameSession viewer, GameSession subject) => Task.CompletedTask;
        public Task UnitsChangedAsync(GameSession viewer, IReadOnlyList<WorldUnit> entered, IReadOnlyList<long> left) => Task.CompletedTask;
        public Task UnitMoveAsync(GameSession viewer, MovementChangeNtf movement) => Task.CompletedTask;
    }

    private readonly BlockingSink _sink = new();
    private readonly World _world;
    private int _nextId = 1;

    public WorldDispatchTests() => _world = new World(_sink, new GameLoop(NullLogger<GameLoop>.Instance), NullLogger<World>.Instance);

    private GameSession Player(string name, int scene)
    {
        var channel = new PhonestChannel(new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp), NullLogger.Instance);
        return new GameSession(channel, null!)
        {
            Role = new Role(_nextId++, "acc", 1, name, 1, 1000, 1, 0, 0, 0, scene, 100, 0, 100, 0),
            SceneEntry = scene,
            Position = new Vec3 { X = 100, Z = 100 },
        };
    }

    [Fact]
    public async Task AStuckSendHoldsItsOwnMapOnly()
    {
        var a = Player("A", 10111);
        await _world.JoinAsync(a);
        _sink.Stuck = "A";
        var stuck = _world.JoinAsync(Player("B", 10111));       // A's view of B waits on A's socket
        Assert.False(stuck.IsCompleted);

        var sameMap = _world.JoinAsync(Player("C", 10111));
        await _world.JoinAsync(Player("D", 10112));             // another map goes through
        await _world.JoinAsync(Player("E", 10112));
        Assert.Contains("E+D", _sink.Events);
        Assert.False(sameMap.IsCompleted);                      // the same map still waits its turn

        _sink.Unblock();
        await stuck.WaitAsync(TimeSpan.FromSeconds(5));
        await sameMap.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("A+B", _sink.Events);
        Assert.Contains("C+A", _sink.Events);
    }

    [Fact]
    public async Task AMapChangeLeavesTheOldMapAndJoinsTheNew()
    {
        var a = Player("A", 10111);
        var b = Player("B", 10111);
        await _world.JoinAsync(a);
        await _world.JoinAsync(b);
        Assert.Contains(b, a.Visible);

        await _world.LeaveAsync(b);
        b.SceneEntry = 10112;
        var c = Player("C", 10112);
        await _world.JoinAsync(c);
        await _world.JoinAsync(b);

        Assert.DoesNotContain(b, a.Visible);
        Assert.Contains(b, c.Visible);
        Assert.Contains(c, b.Visible);
        Assert.Single(_world.PlayersIn(10111));
        Assert.Equal(2, _world.PlayersIn(10112).Count);
    }
}
