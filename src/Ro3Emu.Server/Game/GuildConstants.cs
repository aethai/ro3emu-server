namespace Ro3Emu.Server.Game;

/// <summary>GuildPermissionConfig._iID values checked by GuildService.HasRight.</summary>
internal static class GuildRight
{
    internal const int ManageOfficial = 1;
    internal const int ReviewApplications = 2;
    internal const int Recruit = 7;
    internal const int EditInfo = 9;
    internal const int UpgradeBuilding = 11;
    internal const int EscortMarket = 16;
    internal const int LeagueElite = 20;
    internal const int BlackMarket = 25;
}

/// <summary>GuildPositionConfig._iID values for guild member ranks.</summary>
internal static class GuildOfficial
{
    internal const int Leader = 1;
    internal const int Member = 20;
}
