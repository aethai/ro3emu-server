using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryEscortService
{
    /// <summary>GuildTechService.SyncAsync: republishes a member's stats when its Guild War Technology playing method changed.</summary>
    internal Func<GameSession, Task> GuildTechSync { get; set; } = _ => Task.CompletedTask;

    /// <summary>The Guild War Technology playing method of a member (GuildTechService.PlayingMethods): a guild member on
    /// an escort map of the main line while the escort runs, the players <see cref="Hostile"/> lets fight (inferred).</summary>
    internal long GuildTechMethod(GameSession s) =>
        Playing && !_leavingEscort.ContainsKey(s) && EscortLine(s) && OnEscortMap(s) && Own(s) != null
            ? GuildTechService.EscortBattle : 0;

    private HashSet<long>? _escortMaps;

    private bool OnEscortMap(GameSession s) =>
        (_escortMaps ??= [.. EscortPointAreaConfig.All(_data).Select(row => row.MapId)]).Contains(s.SceneEntry);
}
