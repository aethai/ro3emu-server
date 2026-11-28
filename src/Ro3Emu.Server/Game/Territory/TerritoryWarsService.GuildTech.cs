namespace Ro3Emu.Server.Game;

public sealed partial class TerritoryWarsService
{
    /// <summary>GuildTechService.SyncAsync: republishes a member's stats when its Guild War Technology playing method changed.</summary>
    internal Func<GameSession, Task> GuildTechSync { get; set; } = _ => Task.CompletedTask;

    /// <summary>The Guild War Technology playing method of a member (GuildTechService.PlayingMethods): a running siege or
    /// stronghold battle the member is in and fights for. Preparation and settlement count as outside the battle (inferred:
    /// text 10960000024 says "During Stronghold Battles ... and Siege Battles").</summary>
    internal long GuildTechMethod(GameSession s)
    {
        if (s.Role is not { } role) return 0;
        if (s.Instances.SiegeBattle is { } siege && s.InstanceId == siege.Room.Id && SiegeActive(siege)
            && siege.State.Players.ContainsKey(role.Id)) return GuildTechService.SiegeBattle;
        if (s.Instances.TerritoryBattle is { } battle && s.InstanceId == battle.Room.Id && Active(battle)
            && battle.State.Players.ContainsKey(role.Id)) return GuildTechService.StrongholdBattle;
        return 0;
    }
}
