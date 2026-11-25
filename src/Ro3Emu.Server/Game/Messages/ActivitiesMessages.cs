using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>Dungeons, the tower, daily and timed activities, festivals, training, lottery, AFK and build plans.</summary>
public sealed class ActivitiesMessages(
    PraiseService praise,
    CasualBattleService casual,
    DailyActivityService daily,
    TowerService tower,
    ValhallaService valhalla,
    FlyingDragonService dragon,
    MonsterHuntService monsterHunt,
    FirstHeartService firstHeart,
    DungeonService dungeons,
    DungeonMatchService dungeonMatches,
    WildAfkService afk,
    NewActivityService activities,
    TimedEventService timedEvents,
    TimedBattleService timedBattles,
    FestivalService festival,
    TrainingService training,
    LotteryService lottery,
    GenreService genre,
    GuildHuntingService hunting) : IMessageModule
{
    private const uint SetAutoBattle = 31054;

    public void Map(MessageRoutes routes)
    {
        routes.AdmitEconomic((_, id, body) => activities.IsOfflineOwnerRequest(id, body) || activities.IsEventOwnerRequest(id, body));
        routes.AdmitEconomic((_, id, body) => festival.IsOwnerRequest(id, body));
        routes.AdmitEconomic(timedEvents.IsOwnerRequest);
        routes.AdmitEconomic(IsTimedBattleOwnerRequest);
        routes.AdmitEconomic(training.IsOwnerRequest);

        MapGenre(routes);
        MapActivities(routes);
        MapMatchesAndTimedBattles(routes);
        MapDailyAndDungeons(routes);
        MapTowerAndAfk(routes);
    }

    private void MapGenre(MessageRoutes routes)
    {
        void MapOwn<TReq>(uint id, Func<GameSession, TReq, Task> fn, Func<TReq, long>? peer = null)
            where TReq : Google.Protobuf.IMessage<TReq>, new() =>
            routes.Map<TReq>(id, "genre", RouteAdmission.Economic,
                (s, r) => genre.RunOwnedAsync(s, () => fn(s, r), peer?.Invoke(r) ?? 0, codes: false));

        routes.Map(GenreService.Seek, "genre", RouteAdmission.Economic, s => genre.RunOwnedAsync(s, () => genre.SeekAsync(s), 0, codes: true));
        MapOwn<GenreResponserReq>(GenreService.Respond, genre.RespondAsync, r => r.SeekUserId);
        MapOwn<GenreResponserSendPlanReq>(GenreService.SendPlan, genre.SendRecommendationAsync, r => r.SeekUserId);
        MapOwn<GenreResponserSaveReq>(GenreService.SaveRec, genre.SaveRecommendationAsync, r => r.SeekUserId);
        MapOwn<GenreResponserDeleteReq>(GenreService.DeleteRec, genre.DeleteRecommendationAsync, r => r.SeekerUserId);
        MapOwn<GenreSeekerDeleteReq>(GenreService.DeleteSeeker, genre.DeleteSeekerAsync, r => r.ResponseUserId);
        MapOwn<SwitchGenrePlanReq>(GenreService.SwitchPlan, genre.SwitchAsync);
        routes.Map(GenreService.Code, "genre", RouteAdmission.Economic, s => genre.RunOwnedAsync(s, () => genre.CodeAsync(s), 0, codes: true));
        routes.Map(GenreService.MyRecs, "genre", RouteAdmission.Economic, s => genre.RunOwnedAsync(s, () => genre.MyRecommendationsAsync(s), 0, codes: false));
        MapOwn<GenreGetAllOtherRecommendReq>(GenreService.Others, genre.OthersAsync, r => r.ResponseUserID);
        MapOwn<GenreGetMyRecConcretePlanReq>(GenreService.Concrete, genre.ConcreteAsync, r => r.SeekerUserId);
        MapOwn<GenreSeekerApplyPlanReq>(GenreService.ApplyRec, genre.ApplyRecommendationAsync, r => r.ResponseUserId);
        MapOwn<GenreChangePlanNameReq>(GenreService.RenamePlan, genre.RenameAsync);
        MapOwn<GenreGetPlanPropertyReq>(GenreService.Property, genre.PropertyAsync);
        MapOwn<GenreResponserNameSaveReq>(GenreService.RenameRec, genre.RenameRecommendationAsync, r => r.SeekUserId);
        MapOwn<GenrePlanPowerReq>(GenreService.PlanPower, genre.PowerAsync);
        routes.Map(GenreService.Season, "genre", RouteAdmission.Economic, s => genre.RunOwnedAsync(s, () => genre.SeasonAsync(s), 0, codes: false));
        MapOwn<GenreAttrSettingSchoolReq>(GenreService.School, genre.SchoolAsync);
        routes.Map(GenreService.QueryRecs, "genre", RouteAdmission.Economic, s => genre.RunOwnedAsync(s, () => genre.QueryRecommendationsAsync(s), 0, codes: false));
        routes.Map<GenreResponseCodeReq>(GenreService.RespondCode, "genre", RouteAdmission.Economic, genre.RespondCodeRequestAsync);
    }

    private void MapActivities(MessageRoutes routes)
    {
        routes.Map(NewActivityService.PassPanelMsg, "pass", RouteAdmission.Role, activities.PassPanelAsync);
        routes.Map(NewActivityService.GetList, "activities", RouteAdmission.Default, activities.ListAsync);
        routes.Map<NewActivityGetRewardCommonReq>(NewActivityService.GetReward, "activities", RouteAdmission.Default, activities.ClaimAsync);
        routes.Map<CommonActivityReq>(NewActivityService.Common, "activities", RouteAdmission.Default, activities.CommonAsync);
        routes.Map<CreateOrderReq>(NewActivityService.CreateOrder, "activities", RouteAdmission.Default, activities.CreateOrderAsync);
        routes.Map<PayResultReq>(NewActivityService.PayResult, "activities", RouteAdmission.Default, activities.PayResultAsync);
        routes.Map(NewActivityService.FirstRechargeAward, "activities", RouteAdmission.Default, activities.FirstRechargeAwardsAsync);
        routes.Map(NewActivityService.FirstRechargeUpdate, "activities", RouteAdmission.Default, _ => { });
        routes.Map<GetThreeDayLevelAwardReq>(NewActivityService.LevelAward, "activities", RouteAdmission.Default, activities.LevelAwardAsync);
        routes.Map<GetThreeDayFunctionPreviewAwardReq>(NewActivityService.FunctionPreviewAward, "activities", RouteAdmission.Default, activities.FunctionPreviewAsync);
        routes.Map<NewActivityPreSubmitItemReq>(NewActivityService.PreSubmit, "activities", RouteAdmission.Default, activities.PreSubmitAsync);
        routes.Map<NewActivityPreGetDataReq>(NewActivityService.PreGetData, "activities", RouteAdmission.Default, activities.PreGetDataAsync);
        routes.Map<NewActivityPreReceiveRewardReq>(NewActivityService.PreReceive, "activities", RouteAdmission.Default, activities.PreReceiveAsync);
        routes.Map<NewActivityFragmentGatherAskReq>(NewActivityService.FragmentAsk, "activities", RouteAdmission.Default, activities.FragmentAskAsync);
        routes.Map<NewActivityFragmentGatherSendReq>(NewActivityService.FragmentSend, "activities", RouteAdmission.Default, activities.FragmentSendAsync);
        routes.Map<NewActivityFragmentGatherCraftReq>(NewActivityService.FragmentCraft, "activities", RouteAdmission.Default, activities.FragmentCraftAsync);
        routes.Map<NewActivityFragmentGatherProgressReq>(NewActivityService.FragmentProgress, "activities", RouteAdmission.Default, activities.FragmentProgressAsync);
        routes.Map<NewActivityFragmentGatherAskFinishedReq>(NewActivityService.FragmentAskFinished, "activities", RouteAdmission.Default, activities.FragmentAskFinishedAsync);
        routes.Map<WorldExplorationFillPhaseNodeReq>(NewActivityService.WorldFill, "activities", RouteAdmission.Default, activities.WorldFillAsync);
        routes.Map<LotteryReq>(LotteryService.DrawRequest, "lottery", RouteAdmission.Economic, lottery.DrawAsync);
        routes.Map(TrainingService.ChallengeMode, "training", RouteAdmission.Economic, s => training.ModeAsync(s, true));
        routes.Map<TrainingAimAwardReq>(TrainingService.AimAward, "training", RouteAdmission.Economic, training.ClaimAsync);
        routes.Map(TrainingService.ExitChallengeMode, "training", RouteAdmission.Economic, s => training.ModeAsync(s, false));
        routes.Map<TrainingUpdateWoodNpcAttrReq>(TrainingService.UpdateWoodNpc, "training", RouteAdmission.Economic, training.UpdateAsync);
        routes.Map(TrainingService.Reconnect, "training", RouteAdmission.Economic, s => training.ReconnectAsync(s, false));
        routes.Map(TrainingService.ChallengeReconnect, "training", RouteAdmission.Economic, s => training.ReconnectAsync(s, true));
        routes.Map(TrainingService.ChallengeReset, "training", RouteAdmission.Economic, training.ResetAsync);
        routes.Map<ActivityReq>(TimedEventService.Common, "timedEvents", RouteAdmission.Default, timedEvents.CommonAsync);
        routes.Map<ActivityDataUploadReq>(TimedEventService.Upload, "timedEvents", RouteAdmission.Default, timedEvents.UploadAsync);
    }

    private void MapMatchesAndTimedBattles(MessageRoutes routes)
    {
        routes.Map<StartMatchReq>(DungeonMatchService.StartMatch, "dungeonMatches", RouteAdmission.Default, async (session, startMatch) =>
        {
            if (dungeonMatches.WantsEntry(startMatch.MatchEntryId) || startMatch.MatchData?.MultiDungeon != null)
                await dungeonMatches.StartMatchAsync(session, startMatch);
            else await timedBattles.StartMatchAsync(session, startMatch);
        });
        routes.Map<CancelMatchReq>(DungeonMatchService.CancelMatch, "dungeonMatches", RouteAdmission.Default, async (session, cancelMatch) =>
        {
            if (dungeonMatches.Owns(session) || dungeonMatches.WantsEntry(cancelMatch.MatchEntryId) || cancelMatch.MatchData?.MultiDungeon != null)
                await dungeonMatches.CancelMatchAsync(session, cancelMatch);
            else if (await timedBattles.CancelMatchAsync(session, cancelMatch)
                && (dungeonMatches.WantsEntry(cancelMatch.NextMatchEntry) || cancelMatch.NextMatchData?.MultiDungeon != null))
                await dungeonMatches.StartMatchAsync(session, new StartMatchReq
                { MatchEntryId = cancelMatch.NextMatchEntry, MatchData = cancelMatch.NextMatchData?.Clone() });
        });
        routes.Map<MatchReadyReq>(DungeonMatchService.Ready, "dungeonMatches", RouteAdmission.Default, async (session, matchReady) =>
        {
            if (dungeonMatches.Owns(session) || dungeonMatches.WantsEntry(matchReady.MatchEntryId))
                await dungeonMatches.ReadyAsync(session, matchReady);
            else await timedBattles.ReadyAsync(session, matchReady);
        });
        routes.Map<MatchReadyReminderReq>(DungeonMatchService.Reminder, "dungeonMatches", RouteAdmission.Default, dungeonMatches.RemindAsync);
        routes.Map<DragonIslandClaimRankRewardReq>(TimedBattleService.DragonClaim, "timedBattles", RouteAdmission.Default, timedBattles.ClaimDragonAsync);
        routes.Map<SelectRoleCareerReq>(TimedBattleService.SelectCareer, "timedBattles", RouteAdmission.Default, timedBattles.SelectCareerAsync);
        routes.Map<SelectRolePositionReq>(TimedBattleService.SelectPosition, "timedBattles", RouteAdmission.Default, timedBattles.SelectPositionAsync);
        routes.Map<InsertChickenItemReq>(TimedBattleService.InsertItem, "timedBattles", RouteAdmission.Default, timedBattles.InsertAsync);
        routes.Map<UseChickenItemReq>(TimedBattleService.UseItem, "timedBattles", RouteAdmission.Default, timedBattles.UseAsync);
        routes.Map<DiscardChickenItemReq>(TimedBattleService.DiscardItem, "timedBattles", RouteAdmission.Default, timedBattles.DiscardAsync);
        routes.Map<PickUpChickenItemReq>(TimedBattleService.PickupItem, "timedBattles", RouteAdmission.Default, timedBattles.PickupAsync);
        routes.Map<PickUpAllChickenItemReq>(TimedBattleService.PickupAll, "timedBattles", RouteAdmission.Default, timedBattles.PickupAllAsync);
        routes.Map(TimedBattleService.ForceDead, "timedBattles", RouteAdmission.Default, timedBattles.ForceDeadAsync);
        routes.Map(TimedBattleService.StartRevive, "timedBattles", RouteAdmission.Default, timedBattles.StartReviveAsync);
        routes.Map<ChickenRelifeReq>(TimedBattleService.Relife, "timedBattles", RouteAdmission.Default, timedBattles.RelifeAsync);
        routes.Map<ChickenRelifeOtherReq>(TimedBattleService.RelifeOther, "timedBattles", RouteAdmission.Default, timedBattles.RelifeOtherAsync);
        routes.Map<ChickenItemUpgradeReq>(TimedBattleService.ItemUpgrade, "timedBattles", RouteAdmission.Default, timedBattles.UpgradeAsync);
        routes.Map(TimedBattleService.ItemUpgradeAll, "timedBattles", RouteAdmission.Default, timedBattles.UpgradeAllAsync);
        routes.Map<AdventureEventSelectReq>(TimedBattleService.AdventureSelect, "timedBattles", RouteAdmission.Default, timedBattles.SelectAdventureAsync);
        routes.Map<AdventureRefreshReq>(TimedBattleService.AdventureRefresh, "timedBattles", RouteAdmission.Default, timedBattles.RefreshAdventureAsync);
        routes.Map<SelectFollowReq>(TimedBattleService.SelectFollow, "timedBattles", RouteAdmission.Default, timedBattles.SelectFollowAsync);
        routes.Map<ChickenChangeWatchPlayerReq>(TimedBattleService.ChangeWatch, "timedBattles", RouteAdmission.Default, timedBattles.ChangeWatchAsync);
        routes.Map<ChickenOpenPanelReq>(TimedBattleService.OpenPanel, "timedBattles", RouteAdmission.Default, timedBattles.OpenPanelAsync);
        routes.Map<HiddenTreasureRoundInfoReq>(TimedBattleService.OnePieceInfo, "timedBattles", RouteAdmission.Default, timedBattles.OnePieceInfoAsync);
    }

    private void MapDailyAndDungeons(MessageRoutes routes)
    {
        routes.Map<TeamConfirmRemindersReq>(DungeonService.ConfirmReminder, "dungeons", RouteAdmission.Default, async (session, remind) =>
        {
            if (!await timedBattles.TryRemindRiftAsync(session, remind)) await dungeons.RemindAsync(session, remind);
        });
        routes.Map<CommonDailyActivityReq>(DailyActivityService.RingAccept, "festival", RouteAdmission.Default, async (session, dailyRequest) =>
        {
            if (dailyRequest.RainbowKoiBet != null) await festival.BetAsync(session, dailyRequest);
            else if (dailyRequest.GveHuntingQueryResult != null) await hunting.QueryResultAsync(session, dailyRequest);
            else if (dailyRequest.CasualBattleSignUp != null) await casual.SignUpAsync(session, dailyRequest);
            else await daily.AcceptRingAsync(session, dailyRequest);
        });
        routes.Map<StageRewardGainReq>(DailyActivityService.StageGain, "daily", RouteAdmission.Default, daily.ClaimAsync);
        routes.Map<StageRewardBatchGainReq>(DailyActivityService.StageBatchGain, "daily", RouteAdmission.Default, daily.BatchClaimAsync);
        routes.Map<FirstHeartGetTaskRewardReq>(FirstHeartService.GetTaskReward, "firstHeart", RouteAdmission.Economic, firstHeart.ClaimTaskAsync);
        routes.Map<FirstHeartGetScoreRewardReq>(FirstHeartService.GetScoreReward, "firstHeart", RouteAdmission.Economic, firstHeart.ClaimScoreAsync);
        routes.Map<AreaConcurGainMonsterAwardReq>(MonsterHuntService.GainMonsterAward, "monsterHunt", RouteAdmission.Economic, monsterHunt.ClaimMonsterAsync);
        routes.Map<AreaConcurGainAreaAwardReq>(MonsterHuntService.GainAreaAward, "monsterHunt", RouteAdmission.Economic, monsterHunt.ClaimAreaAsync);
        routes.Map<FlyingDragonStartProduceReq>(FlyingDragonService.Start, "flyingDragon", RouteAdmission.Default, dragon.StartAsync);
        routes.Map(FlyingDragonService.Assist, "flyingDragon", RouteAdmission.Default, dragon.AssistAsync);
        routes.Map(FlyingDragonService.Query, "flyingDragon", RouteAdmission.Default, dragon.QueryAsync);
        routes.Map(FlyingDragonService.Accelerate, "flyingDragon", RouteAdmission.Default, dragon.AccelerateAsync);
        routes.Map(FlyingDragonService.Complete, "flyingDragon", RouteAdmission.Default, dragon.CompleteAsync);
        routes.Map(FlyingDragonService.ThumbsUp, "flyingDragon", RouteAdmission.Default, dragon.ThumbsUpAsync);
        routes.Map<HeroicSpiritChallengeReq>(ValhallaService.Challenge, "valhalla", RouteAdmission.Role, valhalla.ChallengeAsync);
        routes.Map(ValhallaService.Pray, "valhalla", RouteAdmission.Economic, valhalla.PrayAsync);
        routes.Map<ConfirmTeamDungeonReq>(DungeonService.Confirm, "dungeons", RouteAdmission.Default, async (session, confirm) =>
        {
            if (!await timedBattles.TryConfirmRiftAsync(session, confirm)) await dungeons.ConfirmAsync(session, confirm);
        });
        routes.Map<ClientDungeonEvent>(DungeonService.ClientEvent, "training", RouteAdmission.Default, async (session, dungeonEvent) =>
        {
            if (!await training.TryEventAsync(session, dungeonEvent)) await dungeons.ClientEventAsync(session, dungeonEvent);
        });
        routes.Map(DungeonService.Statistics, "dungeons", RouteAdmission.Role, dungeons.StatisticsAsync);
        routes.Map<DungeonStatisticsInfoWithDungeonReq>(DungeonService.StatisticsHistory, "dungeons", RouteAdmission.Role, dungeons.StatisticsHistoryAsync);
        routes.Map(DungeonService.StatisticsCurrent, "dungeons", RouteAdmission.Role, dungeons.StatisticsCurrentAsync);
        routes.Map(DungeonService.StatisticsModule, "dungeons", RouteAdmission.Role, dungeons.StatisticsModuleAsync);
        routes.Map<QueryDungeonResultReq>(DungeonService.QueryResult, "dungeons", RouteAdmission.Role, dungeons.QueryResultAsync);
        routes.Map<MultiDungeonSetJobPositionReq>(DungeonService.SetJobPosition, "dungeons", RouteAdmission.Role, dungeons.SetPositionAsync);
        routes.Map<MultiDungeonSetJobPositionGuideFlagReq>(DungeonService.SetGuide, "dungeons", RouteAdmission.Role, dungeons.SetGuideAsync);
        routes.Map<ClaimFirstPassAwardReq>(DungeonService.ClaimFirstPass, "dungeons", RouteAdmission.Economic, dungeons.ClaimFirstPassAsync);
        routes.Map<QueryHallOfFameInfoReq>(DungeonService.HallOfFame, "dungeons", RouteAdmission.Role, dungeons.HallOfFameAsync);
        routes.Map<MultiDungeonMarkSacredBlessingsReq>(DungeonService.MarkBlessings, "dungeons", RouteAdmission.Role, dungeons.MarkBlessingsAsync);
        routes.Map<MultiDungeonClaimHonorAwardReq>(DungeonService.ClaimHonor, "dungeons", RouteAdmission.Economic, dungeons.ClaimHonorAsync);
        routes.Map<DungeonTeamTransferReq>(CasualBattleService.Transfer, "dungeons", RouteAdmission.Default, casual.TransferAsync);
        routes.Map<DungeonTeamFollowReq>(CasualBattleService.Follow, "dungeons", RouteAdmission.Default, casual.FollowAsync);
        routes.Map<DungeonTeamSwapPositionReq>(CasualBattleService.SwapPosition, "dungeons", RouteAdmission.Default, casual.SwapPositionAsync);
        routes.Map<DungeonTeamSetFlagReq>(CasualBattleService.SetFlag, "dungeons", RouteAdmission.Default, casual.SetFlagAsync);
        routes.Map<DungeonTeamRallyReq>(CasualBattleService.Rally, "dungeons", RouteAdmission.Default, casual.RallyAsync);
        routes.Map<DungeonTeamRallyReplyReq>(CasualBattleService.RallyReply, "dungeons", RouteAdmission.Default, casual.RallyReplyAsync);
        routes.Map<MultiDungeonPraiseReq>(PraiseService.Praise, "praise", RouteAdmission.Role, praise.PraiseAsync);
        routes.Map<DungeonModuleButtonReq>(DungeonService.ModuleButton, "dungeons", RouteAdmission.Default, dungeons.ModuleButtonAsync);
    }

    private void MapTowerAndAfk(MessageRoutes routes)
    {
        routes.Map<ClimTowerStartReq>(TowerService.Start, "tower", RouteAdmission.Role, tower.StartAsync);
        routes.Map(TowerService.Info, "tower", RouteAdmission.Role, tower.InfoAsync);
        routes.Map<SetClimTowerPetsReq>(TowerService.SetPets, "tower", RouteAdmission.Role, tower.SetPetsAsync);
        routes.Map<QueryClimbTowerFirstPassRangeReq>(TowerService.FirstPassRange, "tower", RouteAdmission.Role, tower.FirstPassRangeAsync);
        routes.Map(TowerService.ClaimFirstPass, "tower", RouteAdmission.Role, tower.ClaimFirstPassAsync);
        routes.Map<SetAutoBattleReq>(SetAutoBattle, "afk", RouteAdmission.Role, (s, req) => afk.SetAutoBattleAsync(s, req.IsAutoBattle));
        routes.Map(WildAfkService.Info, "afk", RouteAdmission.Default, afk.InfoAsync);
        routes.Map<SetMonsterCollectionReq>(WildAfkService.Collection, "afk", RouteAdmission.Default, afk.CollectAsync);
        routes.Map(WildAfkService.Start, "afk", RouteAdmission.Default, s => afk.ToggleAsync(s, true));
        routes.Map(WildAfkService.Pause, "afk", RouteAdmission.Default, s => afk.ToggleAsync(s, false));
        routes.Map<SetHighMagnificationReq>(WildAfkService.Magnification, "afk", RouteAdmission.Default, (s, req) => afk.ToggleAsync(s, req.Status, legacy: true));
        routes.Map(WildAfkService.Snapshot, "afk", RouteAdmission.Default, afk.SnapshotAsync);
    }

    // Shared match packets retain legacy admission if either end belongs to an unconverted dungeon family.
    private bool IsTimedBattleOwnerRequest(GameSession session, uint message, ReadOnlyMemory<byte> body)
    {
        if (!timedBattles.IsOwnerRequest(message, body)) return false;
        if (message == TimedBattleService.StartMatch)
            return !dungeonMatches.WantsEntry(StartMatchReq.Parser.ParseFrom(body.Span).MatchEntryId);
        if (message == TimedBattleService.CancelMatch)
        {
            var request = CancelMatchReq.Parser.ParseFrom(body.Span);
            return !dungeonMatches.Owns(session) && !dungeonMatches.WantsEntry(request.MatchEntryId)
                && !dungeonMatches.WantsEntry(request.NextMatchEntry) && request.NextMatchData?.MultiDungeon == null;
        }
        if (message == TimedBattleService.Ready)
            return !dungeonMatches.Owns(session) && !dungeonMatches.WantsEntry(MatchReadyReq.Parser.ParseFrom(body.Span).MatchEntryId);
        return true;
    }
}
