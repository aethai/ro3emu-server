using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>The world around the role: scenes, combat targets, MVPs, world interaction, revival and the territory war.</summary>
public sealed class WorldMessages(
    Maps.World world,
    CombatService combat,
    SceneService scene,
    MvpService mvps,
    TowerService tower,
    DungeonService dungeons,
    WorldCoreService worldCore,
    WorldCommonService common,
    WorldInteractionService interaction,
    TimedBattleService timedBattles,
    FestivalService festival,
    GuildHomeService guildHome,
    DraculaWeddingService dracula,
    TrainingService training,
    TerritoryWarsService territory,
    TerritoryEscortService escort,
    GuildLeagueService league,
    CasualBattleService casual) : IMessageModule
{
    private const uint ChangeViewCfg = 30007, SyncMiniMapStatus = 31043, SceneGetLineInfo = 70009;

    public void Map(MessageRoutes routes)
    {
        MapInterceptors(routes);
        MapWorldCore(routes);
        MapDungeonsAndCombat(routes);
    }

    private void MapInterceptors(MessageRoutes routes)
    {
        routes.AdmitEconomic(id => TerritoryWarsService.IsOwnerRequest(id) || TerritoryEscortService.IsOwnerRequest(id));
        routes.AdmitEconomic(territory.IsSharedOwnerRequest);
        routes.AdmitEconomic(escort.IsSharedOwnerRequest);

        // Siege, territory battle and an ordinary dungeon answer the same statistics requests; escort cargo and
        // territory pickups are NPCs behind the ordinary interaction.
        static bool DungeonStatistics(uint id) => id is 30106 or 30110 or 30114 or 30116 or 30117;
        static bool NpcInteraction(uint id) => id == SceneService.NpcInteraction;
        routes.Intercept("territory", TerritoryWarsService.IsOwnerRequest, territory.HandleAsync);
        routes.Intercept("escort", TerritoryEscortService.IsOwnerRequest, escort.HandleAsync);
        routes.Intercept("territory/siege", DungeonStatistics, territory.SiegeSharedAsync);
        routes.Intercept("guildLeague", DungeonStatistics, league.StatisticsAsync);
        routes.Intercept("territory/battle", DungeonStatistics, territory.SharedAsync);
        routes.Intercept("escort/actors", id => id is 32287 || NpcInteraction(id), escort.HandleSharedAsync); // guild goods, cargo NPCs
        routes.Intercept("territory/world", NpcInteraction, territory.WorldSharedAsync);
        // A siege cannon is an NPC of the siege room; its operator's buttons are 32703.
        routes.Intercept("territory/cannon", id => NpcInteraction(id) || id == WorldInteractionService.Action,
            territory.CannonSharedAsync);
        // So are the siege's bag NPCs, which open the war bag.
        routes.Intercept("territory/siege-bag", NpcInteraction, territory.SiegeBagSharedAsync);
        // Mock Battle's supply boxes are NPCs of its room.
        routes.Intercept("casual/supply", NpcInteraction, casual.SupplySharedAsync);
        // A Madogear rider's buttons, seat changes and facing (after the territory's own riders).
        routes.Intercept("casual/madogear", id => id is WorldInteractionService.Action or WorldInteractionService.SeatChange
            or WorldInteractionService.VehicleSync, casual.MadogearSharedAsync);
        // A war vehicle is an NPC of the siege room too; its riders' buttons, seat changes and facing follow it.
        routes.Intercept("territory/vehicle", id => NpcInteraction(id) || id is WorldInteractionService.Action
            or WorldInteractionService.SeatChange or WorldInteractionService.VehicleSync, territory.VehicleSharedAsync);
    }

    private void MapWorldCore(MessageRoutes routes)
    {
        routes.Map<GMCommandTransmitReq>(WorldCommonService.GmTransmit, "common", RouteAdmission.Default, common.TransmitAsync);
        routes.Map<InspectCommandReq>(WorldCommonService.Inspect, "common", RouteAdmission.Default, common.InspectAsync);
        routes.Map(WorldCommonService.OnlineStatusHeart, "common", RouteAdmission.Default, common.OnlineHeartAsync);
        routes.Map<GMProcReq>(WorldCommonService.LegacyGmProc, "common", RouteAdmission.Default, common.LegacyProcAsync);
        routes.Map<UseInteractActionReq>(WorldInteractionService.Action, "interaction", RouteAdmission.Role, interaction.UseActionAsync);
        routes.Map<ChangeVehicleSeatReq>(WorldInteractionService.SeatChange, "interaction", RouteAdmission.Role, interaction.ChangeSeatAsync);
        routes.Map<SyncVehicleReq>(WorldInteractionService.VehicleSync, "interaction", RouteAdmission.Role, interaction.SyncVehicleAsync);
        routes.Map<GetPlayerInSceneDetailReq>(WorldCoreService.PlayerDetail, "worldCore", RouteAdmission.Default, worldCore.DetailAsync);
        routes.Map<SceneQueuingReq>(WorldCoreService.SceneQueue, "worldCore", RouteAdmission.Default, worldCore.QueueAsync);
        routes.Map<SceneQueuingCancel>(WorldCoreService.QueueCancel, "worldCore", RouteAdmission.Default, worldCore.CancelQueueAsync);
        routes.Map<QueryCurrentNpcReq>(WorldCoreService.QueryNpc, "worldCore", RouteAdmission.Default, worldCore.QueryNpcAsync);
        routes.Map<SyncAoIUnviewReq>(WorldCoreService.AoiUnview, "worldCore", RouteAdmission.Default, worldCore.UnviewAsync);
        routes.Map<MonitorCustomDataReq>(WorldCoreService.MonitorCustom, "worldCore", RouteAdmission.Default, worldCore.MonitorAsync);
        routes.Map<ScenePerformaceWatchReq>(WorldCoreService.PerformanceWatch, "worldCore", RouteAdmission.Default, worldCore.WatchAsync);
        routes.Map<StuckEscapeReq>(WorldCoreService.StuckEscape, "worldCore", RouteAdmission.Default, worldCore.EscapeAsync);
        routes.Map(WorldCoreService.RecoverLeave, "worldCore", RouteAdmission.Default, worldCore.RecoverAsync);
        routes.Map<EntityReliveHelpReq>(WorldCoreService.ReliveHelp, "worldCore", RouteAdmission.Default, worldCore.HelpAsync);
        routes.Map<EntityReliveInviteAcceptReq>(WorldCoreService.ReliveAccept, "worldCore", RouteAdmission.Default, worldCore.AcceptReviveAsync);
        routes.Map(WorldCoreService.ReliveItem, "worldCore", RouteAdmission.Role, worldCore.ItemReviveAsync);
        routes.Map<GuildVoiceTypeDownMessageReq>(WorldCoreService.TypeDown, "worldCore", RouteAdmission.Role, worldCore.TypeDownAsync);
        routes.Map<FlySwitchReq>(WorldCoreService.FlySwitch, "worldCore", RouteAdmission.Role, worldCore.FlySwitchAsync);
        routes.Map<FlyMoveReq>(WorldCoreService.FlyMove, "worldCore", RouteAdmission.Role, worldCore.FlyMoveAsync);
        routes.Map<FreeFallJumpReq>(WorldCoreService.FreeFall, "worldCore", RouteAdmission.Role, worldCore.FreeFallAsync);
        routes.Map<PlayerAssetReq>(MsgId.PlayerAsset, "worldCore", RouteAdmission.Default, worldCore.AssetsAsync);
    }

    private void MapDungeonsAndCombat(MessageRoutes routes)
    {
        routes.Map<EnterDungeonReq>(DungeonService.Enter, "territory", RouteAdmission.Default, async (session, dungeonRequest) =>
        {
            if (await territory.EnterAsync(session, dungeonRequest) || await territory.SiegeEnterAsync(session, dungeonRequest)
                || await league.TryEnterAsync(session, dungeonRequest)) return;
            if (!await training.TryEnterAsync(session, dungeonRequest) && !await festival.TryEnterAsync(session, dungeonRequest)
                && !await timedBattles.TryEnterOnePieceAsync(session, dungeonRequest) && !await guildHome.TryEnterViaDungeonAsync(session, dungeonRequest)
                && !await dracula.TryEnterAsync(session, dungeonRequest))
                await dungeons.EnterAsync(session, dungeonRequest);
        });
        routes.Map<DungeonWatchChangeTargetReq>(DraculaWeddingService.Watch, "territory", RouteAdmission.Default, dracula.WatchAsync);
        routes.Map(TowerService.LeaveDungeon, "territory", RouteAdmission.Default, async session =>
        {
            if (await territory.LeaveAsync(session) || await league.LeaveAsync(session)) return;
            if (session.Instances.TowerAttempt != null) await tower.LeaveAsync(session);
            else await dungeons.LeaveAsync(session);
        });
        routes.Map<ChangeViewCfgReq>(ChangeViewCfg, "world", RouteAdmission.Default, async (session, view) =>
        {
            // Graphics quality / visible-count settings: accepted as asked (the AOI keeps its own limits).
            await session.SendGameAsync(ChangeViewCfg, new ChangeViewCfgResp
            {
                Result = 0, ViewMaxCount = view.ViewMaxCount, NameBoardMaxCount = view.NameBoardMaxCount, LodHighCount = view.LodHighCount,
            });
            await world.SetViewLimitAsync(session, view.ViewMaxCount);
            await worldCore.ViewAsync(session, view.NameBoardMaxCount);
        });
        routes.Map<MiniMapStatus>(SyncMiniMapStatus, "worldCore", RouteAdmission.Role, worldCore.MiniMapAsync);
        routes.Map<GetSceneLineInfoReq>(SceneGetLineInfo, "scene", RouteAdmission.Default, scene.LineInfoAsync);
        routes.Map<SwitchSceneLineReq>(MsgId.SwitchSceneLine, "scene", RouteAdmission.Default, scene.SwitchLineAsync);
        routes.Map(MvpService.BriefInfo, "mvps", RouteAdmission.Economic, mvps.BriefAsync);
        routes.Map(MvpService.AwardNum, "mvps", RouteAdmission.Economic, mvps.AwardNumAsync);
        routes.Map<MVPReportRankReq>(MvpService.ReportRank, "mvps", RouteAdmission.Economic, mvps.ReportAsync);
        routes.Map<ChangeFaceReq>(MsgId.ChangeFace, "world", RouteAdmission.Role, async (session, req) =>
        {
            if (session.Role != null)
            {
                session.Face = req.Angle;
                await world.TurnAsync(session, session.Face);
            }
        });
        routes.Map<LockEnemyReq>(CombatService.LockEnemy, "combat", RouteAdmission.Role, combat.Lock);
        routes.Map<SkillInterruptReq>(CombatService.SkillInterrupt, "combat", RouteAdmission.Role, combat.InterruptAsync);
        routes.Map<BossAllSkillShowCdReq>(CombatService.BossAllSkillCds, "combat", RouteAdmission.Default, combat.BossCooldownSnapshotAsync);
        routes.Map<BattleTrainingGroundOpReq>(CombatService.TrainingOperation, "combat", RouteAdmission.Default, combat.TrainingOperationAsync);
        routes.Map<EntityReliveReq>(CombatService.Relive, "territory", RouteAdmission.Default, async (session, req) =>
        {
            if (await timedBattles.TryRefuseReliveAsync(session)) return; // Survivor Battle revives with 82661
            if (await territory.ReviveAsync(session, req, combat)) return;
            if (await league.ReviveAsync(session)) return;
            if (await casual.ReviveAsync(session, req, combat)) return;
            if (session.DungeonAttempt != null) await dungeons.ReviveAsync(session, req);
            else await combat.ReviveAsync(session, req);
        });
        routes.Map<SceneTeleportReq>(SceneService.SceneTeleport, "scene", RouteAdmission.Role, scene.SceneTeleportAsync);
        routes.Map<ClientCustomJumpMapReq>(SceneService.CustomJumpMap, "scene", RouteAdmission.Default, scene.CustomJumpAsync);
    }
}
