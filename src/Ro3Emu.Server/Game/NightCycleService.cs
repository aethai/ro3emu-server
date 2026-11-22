using System.Text;
using Microsoft.Extensions.Options;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The optional night look (<see cref="ServerOptions.NightCycle"/>, docs/systems/night-cycle.md). The client hook
/// (Ro3Emu.ClientHooks NightCycle) draws the dark layer by the player's own PC clock; the server only switches it on and
/// says where the player is, with the client command message 77012 (the one of the GM's <c>!client</c>):
/// <list type="bullet">
/// <item><c>time on</c>, once per session: the layer may show.</item>
/// <item><c>time place outdoor|indoor</c>, on every change of place: a town or a field is outdoor; everything else
/// (instances, dungeon floors and caves, guild war, training ground, territory war, a map without a MapConfig row) is
/// indoor and never gets the layer. (inferred: the original game has no night.)</item>
/// </list>
/// Without the option the service does nothing and the client never shows the layer.
/// </summary>
public sealed class NightCycleService(IOptions<ServerOptions> options, GameData data, SessionRegistry sessions, GameLoop loop) : IGameTicker
{
    /// <summary>MapConfig _iSceneType enum_SceneType (Lua_GameDefine): MainCity 0, OutSide 1.</summary>
    private const long MainCityScene = 0, OutSideScene = 1;

    internal const string EnableCommand = "time on";
    private static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(1);
    private TimeSpan _nextCheck;
    private readonly Dictionary<GameSession, bool> _told = [];

    /// <summary>A town or a field, entered outside any instance.</summary>
    internal static bool IsOutdoor(long instanceId, long? sceneType) => instanceId == 0 && sceneType is MainCityScene or OutSideScene;

    internal static string PlaceCommand(bool outdoor) => "time place " + (outdoor ? "outdoor" : "indoor");

    public void Tick(GameTime time)
    {
        if (!options.Value.NightCycle || time.Now < _nextCheck) return;
        _nextCheck = time.Now + CheckEvery;
        HashSet<GameSession> seen = [];
        foreach (var session in sessions.All())
        {
            if (session.Role == null) continue;
            seen.Add(session);
            if (session.SceneLoading) continue;
            bool outdoor = IsOutdoor(session.InstanceId, MapConfig.Get(data, session.SceneEntry)?.SceneType);
            bool first = !_told.TryGetValue(session, out bool was);
            if (!first && was == outdoor) continue;
            _told[session] = outdoor;
            loop.Post(() => TellAsync(session, first, outdoor));
        }
        foreach (var gone in _told.Keys.Where(k => !seen.Contains(k)).ToList()) _told.Remove(gone);
    }

    private static async Task TellAsync(GameSession session, bool first, bool outdoor)
    {
        if (first) await session.SendGameRawAsync(GmCommands.ClientDiagnosticsMsg, Encoding.UTF8.GetBytes(EnableCommand));
        await session.SendGameRawAsync(GmCommands.ClientDiagnosticsMsg, Encoding.UTF8.GetBytes(PlaceCommand(outdoor)));
    }
}
