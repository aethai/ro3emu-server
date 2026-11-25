using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The login, role and session messages, the quest messages and the stubs of features that are not served. How
/// they are admitted: role creation under its account, the session's own continuations as runtime, the few that
/// change only the sender's role under that role.
/// </summary>
public sealed class CoreMessages(
    RoleSessionService roleSessions,
    ChatService chat,
    BuffService buffs,
    GmCommands gm,
    SceneService scene,
    TaskService tasks,
    DailyActivityService daily,
    TeamService teams,
    GuildService guilds,
    DungeonService dungeons,
    Ownership.RoleHandover handover,
    Micro.RankService ranks) : IMessageModule
{
    private const uint EquipGetRank = 31470;
    private const string OwnedContinuation = "dispatcher/owned continuation";

    public void Map(MessageRoutes routes)
    {
        // What the client sends before its role is in the world; anything else is refused until then.
        // NW_NetworkManager pings with HEARTBEAT from the moment it connects, before the account signs in.
        routes.AllowFrom(SessionStage.Connected, id => id == MsgId.Heartbeat);
        // LC_LoginManager on the role screen: LOGIN_ROLE_CREATE, LOGIN_ROLE_SELECT, and UPLOAD_CLIENT_SDK_INFO.
        routes.AllowFrom(SessionStage.RoleScreen,
            id => id is MsgId.LoginRoleCreate or MsgId.LoginRoleSelect or MsgId.UploadClientSdkInfo);
        MapSession(routes);
        MapTasks(routes);
        MapStubs(routes);
    }

    private void MapSession(MessageRoutes routes)
    {
        routes.Map<AccountLoginCreateRoleReq>(MsgId.LoginRoleCreate, "handler", RouteAdmission.Account, roleSessions.CreateRoleAsync);
        routes.Map<AccountLoginSelectRoleReq>(MsgId.LoginRoleSelect, "handler", RouteAdmission.Runtime, roleSessions.SelectRoleAsync, runtimeOrigin: OwnedContinuation);
        routes.Map<WalkReq>(MsgId.Walk, "handler", RouteAdmission.Role, roleSessions.WalkAsync);
        routes.Map(MsgId.SaveSetting, "handler", RouteAdmission.Role, (session, _, body) =>
            roleSessions.SaveSettingAsync(session, SaveSettingReq.Parser.ParseFrom(body.Span), body));
        routes.Map(MsgId.Logout, "handover", RouteAdmission.Runtime, async session =>
        {
            await handover.Begin(session);
            await session.SendGameAsync(MsgId.Logout, new LogoutResp());
        }, runtimeOrigin: OwnedContinuation);
        routes.Map(MsgId.PreSwitchRole, "handler", RouteAdmission.Runtime,
            session => session.SendGameAsync(MsgId.PreSwitchRole, new PreSwitchRoleResp { Result = 0 }),
            runtimeOrigin: OwnedContinuation);
        routes.Map(MsgId.ClientEnterMainUi, "handler", RouteAdmission.Role,
            session => session.SendGameAsync(MsgId.ClientEnterMainUi, new ClientEnterRes { NewLv = 0 }));
        routes.Map(MsgId.UploadClientSdkInfo, "handler", RouteAdmission.Runtime, _ => { },
            runtimeOrigin: "dispatcher/ignored report");
        routes.Map<ClientRemoveBuffReq>(BuffService.ClientRemoveBuff, "buffs", RouteAdmission.Role, buffs.RemoveByRequestAsync);
        routes.Map<PlayerConfirmImmuneDeadReq>(BuffService.ImmuneDeathConfirm, "buffs", RouteAdmission.Default, buffs.ConfirmImmuneDeathAsync);
        routes.Map<GeneralGMProcReq>(GmCommands.GeneralProcGm, "gm", RouteAdmission.Default, gm.PanelAsync);
        // The menu's Feedback button (Ro3Emu.ClientHooks.FeedbackButton): the report text as UTF-8.
        routes.Map(FeedbackService.SubmitMsg, "feedback", RouteAdmission.Role, async (session, _, body) =>
        {
            if (session.Role == null) return;
            var text = System.Text.Encoding.UTF8.GetString(body.Span[..Math.Min(body.Length, 16 * 1024)]);
            await chat.Feedback.SubmitAsync(session, text, "button");
        });
        routes.Map<SceneLoadedReq>(MsgId.SceneLoaded, "scene", RouteAdmission.Runtime, async (session, loadedReq) =>
        {
            var loadedRole = session.Role;
            var loadedAttachment = session.Attachment;
            long sceneGeneration = session.SceneGeneration;
            session.WorldState.NameBoardLimit = Math.Min(loadedReq.NameBoardMaxCount, 1000);
            await scene.SceneLoadedAsync(session, loadedReq);
            using var scope = session.Owners is { } loadedOwners ? await roleSessions.EnterScenePublicationAsync(loadedOwners, session) : null;
            if (!ReferenceEquals(session.Role, loadedRole) || !ReferenceEquals(session.Attachment, loadedAttachment)
                || session.SceneGeneration != sceneGeneration) return;
            await teams.OnEnteredAsync(session);
            session.GuildOnlinePublished = true; // undo even if the publication throws after saving
            await guilds.OnlineChangedAsync(session, true);
            await guilds.SceneGuildIdAsync(session);
        }, runtimeOrigin: OwnedContinuation);
        routes.Map(MsgId.Heartbeat, "daily", RouteAdmission.Runtime, async session =>
        {
            async Task OwnAsync() { await daily.ReconcileWindowsAsync(session); await dungeons.ReconcileAsync(session); }
            if (session.Owners is { } owners && session.Role is { } role)
                await owners.RunSessionAsync("heartbeat",
                    [Ownership.ReservationKey.Role(role.Id), Ownership.ReservationKey.Lifecycle(role.Id)], session, OwnAsync);
            else await OwnAsync();
            await guilds.ReconcileOnHeartbeatAsync();
            await session.SendGameAsync(MsgId.Heartbeat, new Pong { ServerTimeMs = roleSessions.NowMs() });
        }, runtimeOrigin: OwnedContinuation);
    }

    private void MapTasks(MessageRoutes routes)
    {
        routes.Map<TaskResetReq>(TaskService.TaskReset, "tasks", RouteAdmission.Default, tasks.ResetAsync);
        routes.Map<TaskNpcChatReq>(TaskService.TaskNpcChat, "tasks", RouteAdmission.Default, tasks.NpcChatAsync);
        routes.Map<FinishPYDTaskReq>(TaskService.TaskFinishPyd, "tasks", RouteAdmission.Default, tasks.FinishPydAsync);
        routes.Map<SelectTaskQuestBranchReq>(TaskService.TaskSelectQuestBranch, "tasks", RouteAdmission.Default, tasks.SelectBranchAsync);
        routes.Map<LuckyTurnReq>(TaskService.TaskLuckyTurn, "tasks", RouteAdmission.Default, tasks.LuckyTurnAsync);
        routes.Map<TaskTrackTeleportReq>(TaskService.TaskTrackTeleport, "tasks", RouteAdmission.Default, tasks.TrackTeleportAsync);
        routes.Map<ClientNpcInteractReq>(SceneService.ClientNpcInteract, "tasks", RouteAdmission.Default, tasks.OnDialogAsync);
        routes.Map<ClientEnterAreaReq>(SceneService.ClientEnterArea, "tasks", RouteAdmission.Default, async (session, area) =>
        {
            await tasks.OnAreaAsync(session, area);
            // A region whose action is server-hosted (613 EnterDungeon_Sever) is the server's to run.
            if (scene.ServerHostedRegion(session, area.AreaId) is { } region) await dungeons.EnterFromRegionAsync(session, region);
        });
        routes.Map<TriggerTaskEventReq>(TaskService.TriggerTaskEvent, "tasks", RouteAdmission.Default, tasks.OnClientInteractAsync);
        routes.Map<PlotActingEndReq>(TaskService.PlotActingEnd, "tasks", RouteAdmission.Default, async (session, plotEnd) =>
        {
            await tasks.OnPlotEndAsync(session, plotEnd);
            await dungeons.PlotEndedAsync(session, plotEnd.PlotId); // a scripted scene's story
        });
        routes.Map<PlotActingTriggerReq>(DungeonService.PlotSceneEvent, "tasks", RouteAdmission.Default,
            (session, req) => dungeons.PlotEndedAsync(session, req.PlotId));
        routes.Map(SceneService.DialogueActionTrigger, "handler", RouteAdmission.Default, _ => { });
        routes.Map(TaskService.ExitCutSceneState, "tasks", RouteAdmission.Default, tasks.ExitCutSceneAsync);
        routes.Map<TaskTrackReq>(TaskService.TaskTrack, "tasks", RouteAdmission.Default, tasks.TrackAsync);
        routes.Map(TaskService.GoodsStack, "tasks", RouteAdmission.Default, tasks.GoodsStackAsync);
        routes.Map<TaskFollowReq>(TaskService.TaskFollow, "tasks", RouteAdmission.Default, tasks.FollowAsync);
        routes.Map<TaskAcceptReq>(TaskService.TaskAccept, "tasks", RouteAdmission.Default, tasks.AcceptAsync);
        routes.Map<TaskDeliveryReq>(TaskService.TaskDelivery, "tasks", RouteAdmission.Default, tasks.DeliverAsync);
        routes.Map<TaskGiveUpReq>(TaskService.TaskGiveUp, "tasks", RouteAdmission.Default, tasks.GiveUpAsync);
    }

    private void MapStubs(MessageRoutes routes)
    {
        routes.Map(GuildChallengeService.Notify, "handler", RouteAdmission.Default, _ => { });
        routes.Map(WorldCoreService.TeleportObject, "handler", RouteAdmission.Default, (session, msgId, _) =>
        {
            if (scene.MovesOnSurface(session.SceneEntry)) return Task.CompletedTask;
            return WorldCoreService.ErrorAsync(session, msgId, ErrorCodes.Parameter);
        });
        routes.Map(LifeJobService.CheckCollect, "handler", RouteAdmission.Role,
            session => session.SendGameAsync(LifeJobService.CheckCollect, new CheckCanCollectNtf { Result = ErrorCodes.Parameter }));
        routes.Map(DungeonService.Result, "handler", RouteAdmission.Default, _ => { });
        routes.Map(DungeonService.ResultNotify, "handler", RouteAdmission.Default, _ => { });
        routes.Map(DungeonService.Event, "handler", RouteAdmission.Default, _ => { });
        routes.Map(DungeonService.AwardNotify, "handler", RouteAdmission.Default, _ => { });
        routes.Map(TowerService.Sweep, "handler", RouteAdmission.Role,
            session => session.SendGameAsync(TowerService.Sweep, new ClimTowerSweepRes { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map(TowerService.End, "handler", RouteAdmission.Role, _ => { });
        routes.Map<WildAfkSaveChargeReq>(WildAfkService.SaveCharge, "handler", RouteAdmission.Default, (session, req) =>
        {
            if (session.Activities.WildAfk is { } state) state.PowerSaving = req.IsSaveCharge;
        });
        routes.Map(WildAfkService.OfflineReward, "handler", RouteAdmission.Default,
            session => session.SendGameAsync(WildAfkService.OfflineReward, new ReceiveOfflineRewardRsp { Result = ErrorCodes.AfkNoSummary }));
        routes.Map(WildAfkService.DungeonStart, "handler", RouteAdmission.Default,
            session => session.SendGameAsync(WildAfkService.DungeonStart, new AfkDungeonStartRsp { Result = ErrorCodes.FeatureUnavailable }));
        routes.Map<QueryEquipRankReq>(EquipGetRank, "handler", RouteAdmission.Role, async (session, rank) =>
        {
            await session.SendGameAsync(EquipGetRank, new QueryEquipRankRes
            {
                Rankid = rank.Rankid, Itemuid = rank.Itemuid,
                Rank = await ranks.EquipRankAsync(rank.Rankid, rank.Itemuid)
            });
        });
        routes.Map(AuctionService.Sync, "handler", RouteAdmission.Default, _ => { });
        routes.Map(AuctionService.Changed, "handler", RouteAdmission.Economic, _ => { });
        routes.Map(AuctionService.DungeonDone, "handler", RouteAdmission.Economic, _ => { });
        routes.Map(AuctionService.DungeonChanged, "handler", RouteAdmission.Economic, _ => { });
        routes.Map(AuctionService.HasAuto, "handler", RouteAdmission.Economic, _ => { });
        routes.Map(AuctionService.Reddot, "handler", RouteAdmission.Economic, _ => { });
    }
}
