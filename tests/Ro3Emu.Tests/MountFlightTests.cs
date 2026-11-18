using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>
/// The climb and descent of a flying mount's take-off/landing (MountFlight.Plan) on invented tables and an invented nav
/// grid, so CI runs them: the 31112 names the row's _kLogicData displacement, ends on the client's aimed point when it is
/// plausible, and falls back to the row's own height or the ground below when it is not.
/// </summary>
public sealed class MountFlightTests : IDisposable
{
    private const int TakeOff = 9100101, Landing = 9100201, Delayed = 9100301, NoMove = 9100401;
    private const int Rise = 910012101, Descent = 910022101, Animation = 910032101;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-mount-flight-" + Guid.NewGuid().ToString("N"));
    private readonly GameData _data;
    private readonly MapWorld _map;

    public MountFlightTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "config"));
        Directory.CreateDirectory(Path.Combine(_root, "skills"));
        Write(Path.Combine("config", "SkillConfig.json"), new Dictionary<string, object>
        {
            [$"{TakeOff}"] = new { _iID = TakeOff, _kLogicData = new[] { 1, 0, Rise, 900 } },
            [$"{Landing}"] = new { _iID = Landing, _kLogicData = new[] { 2, 0, Descent, 900 } },
            [$"{Delayed}"] = new { _iID = Delayed, _kLogicData = new[] { 3, 700, Rise, 900 } },
            [$"{NoMove}"] = new { _iID = NoMove, _kLogicData = new[] { 4, 0, Animation, 900 } },
        });
        Write(Path.Combine("skills", "Lua_Config_EffectData.json"), new Dictionary<string, object>
        {
            [$"{Rise}"] = new { m_kPoint = new { m_bMovement = true, m_fHeight = 12, m_iMovementTime = 800 } },
            [$"{Descent}"] = new { m_kPoint = new { m_bMovement = true, m_fHeight = -12, m_iMovementTime = 800 } },
            [$"{Animation}"] = new { m_kPoint = new { m_fDuration = 1, m_kAni = "Invented" } },
        });
        _data = new GameData(Options.Create(new ServerOptions { GameDataDir = _root }), NullLogger<GameData>.Instance);
        // x 0-9 is walkable ground at 2 m; x 10-19 a lake nobody walks on, recorded at 7 m.
        _map = new MapWorld { SceneEntry = 1, Born = new MapPoint(2, 2, 2, 0), Nav = Grid(20, 10, x => x < 10 ? (true, 2f) : (false, 7f)) };
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private void Write(string file, object rows) => File.WriteAllText(Path.Combine(_root, file), JsonSerializer.Serialize(rows));

    private static Vec3 At(float x, float y, float z) => new() { X = x, Y = y, Z = z };

    [Fact]
    public void ATakeOffClimbsToTheAimedPointOnTheRowsDisplacement()
    {
        var move = MountFlight.Plan(_data, TakeOff, takeOff: true, _map, At(3, 2, 3), At(4, 30, 3));
        Assert.NotNull(move);
        Assert.Equal((Rise, 800, 0), (move.Value.EffectId, move.Value.MoveMs, move.Value.DelayMs));
        Assert.Equal((4f, 30f, 3f), (move.Value.End.X, move.Value.End.Y, move.Value.End.Z));
        Assert.Equal(700, MountFlight.Plan(_data, Delayed, takeOff: true, _map, At(3, 2, 3), At(4, 30, 3))!.Value.DelayMs);
    }

    [Theory]
    [InlineData(25, 30, 3)] // farther than MountFlight.Reach
    [InlineData(4, 60, 3)] // above the flight band (floor 2 + 50)
    [InlineData(4, 0, 3)] // below the rider
    [InlineData(4, 2.5f, 3)] // no climb: about the rider's own spot
    public void AnImplausibleAimFallsBackToTheRowsOwnHeight(float x, float y, float z)
    {
        var move = MountFlight.Plan(_data, TakeOff, takeOff: true, _map, At(3, 2, 3), At(x, y, z));
        Assert.Equal((3f, 14f, 3f), (move!.Value.End.X, move.Value.End.Y, move.Value.End.Z));
        Assert.Equal(14f, MountFlight.Plan(_data, TakeOff, takeOff: true, _map, At(3, 2, 3), null)!.Value.End.Y);
    }

    [Fact]
    public void ALandingAlwaysEndsOnWalkableGround()
    {
        var aimed = MountFlight.Plan(_data, Landing, takeOff: false, _map, At(3, 30, 3), At(5, 2.4f, 4));
        Assert.Equal((Descent, 5f, 2f, 4f), (aimed!.Value.EffectId, aimed.Value.End.X, aimed.Value.End.Y, aimed.Value.End.Z));
        // Aimed into the lake: the ground below the rider instead.
        var below = MountFlight.Plan(_data, Landing, takeOff: false, _map, At(3, 30, 3), At(12, 7, 3));
        Assert.Equal((3f, 2f, 3f), (below!.Value.End.X, below.Value.End.Y, below.Value.End.Z));
        // Over the lake, aimed into it: the nearest shore, never a landing that does nothing.
        var shore = MountFlight.Plan(_data, Landing, takeOff: false, _map, At(15, 30, 3), At(16, 7, 3));
        Assert.Equal((9.5f, 2f), (shore!.Value.End.X, shore.Value.End.Y));
        // The client's spot can lie far off (SearchWalkableAround up to 100): taken when it is ground.
        var far = MountFlight.Plan(_data, Landing, takeOff: false, _map, At(19, 30, 9), At(1, 2, 1));
        Assert.Equal((1f, 2f, 1f), (far!.Value.End.X, far.Value.End.Y, far.Value.End.Z));
    }

    [Fact]
    public void ASwitchWhoseRowNamesNoDisplacementMovesNobody()
    {
        Assert.Null(MountFlight.Plan(_data, NoMove, takeOff: true, _map, At(3, 2, 3), At(4, 30, 3)));
        Assert.Null(MountFlight.Plan(_data, 9100999, takeOff: true, _map, At(3, 2, 3), At(4, 30, 3)));
    }

    [Fact]
    public void AFlyerIsMeasuredFromTheGroundOrTheCellsOwnHeightOverWater()
    {
        Assert.Equal(2f, MountFlight.Floor(_map, At(3, 30, 3)));
        Assert.Equal(7f, MountFlight.Floor(_map, At(15, 30, 3)));
        Assert.Null(MountFlight.Floor(_map, At(40, 30, 3)));
    }

    /// <summary>A one-block nav grid in the client's format (see <see cref="NavGrid"/>), one layer per cell.</summary>
    private NavGrid Grid(int width, int height, Func<int, (bool Walkable, float Height)> cell)
    {
        const int header = 48, block = 30;
        var bytes = new byte[header + 16 + block * block * 4];
        BitConverter.TryWriteBytes(bytes.AsSpan(20), width);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), height);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), block);
        BitConverter.TryWriteBytes(bytes.AsSpan(header), header + 16);
        for (int x = 0; x < width; x++)
            for (int z = 0; z < height; z++)
            {
                int at = header + 16 + (z * block + x) * 4;
                var (walkable, h) = cell(x);
                if (walkable) bytes[at] = 0x80;
                BitConverter.TryWriteBytes(bytes.AsSpan(at + 2), (short)(h * 100));
            }
        var path = Path.Combine(_root, "grid.bytes");
        File.WriteAllBytes(path, bytes);
        return NavGrid.Load(path);
    }
}
