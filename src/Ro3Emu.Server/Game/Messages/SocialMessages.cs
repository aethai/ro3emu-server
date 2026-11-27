using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>Chat, teams, friends, guilds and their caravan, challenge, campfire and home, emblems.</summary>
public sealed class SocialMessages(
    ChatService chat,
    TeamService teams,
    EmblemService emblems,
    GuildService guilds,
    GuildCaravanService caravan,
    GuildChallengeService guildChallenge,
    FriendExtraService friendExtra,
    GuildCampfireService campfire,
    GuildHomeService guildHome,
    GuildHuntingService hunting,
    PersonalService personal,
    SocialOutfitService socialOutfit) : IMessageModule
{
    public void Map(MessageRoutes routes)
    {
        routes.AdmitEconomic((_, id, body) => ChatService.IsEventOwnerRequest(id, body));
        MapPersonalAndFriends(routes);
        MapTeams(routes);
        MapGuildCore(routes);
        MapGuildOperations(routes);
        MapCaravanEmblemsAndChat(routes);
    }

    private void MapPersonalAndFriends(MessageRoutes routes)
    {
        routes.Map<UpdatePersonalAppearanceReq>(PersonalService.UpdateAppearance, "personal", RouteAdmission.Role, personal.UpdateAppearanceAsync);
        // A like also writes the liked role's received likes: the default Realm reservation covers both roles.
        routes.Map<UpvotePersonalReq>(PersonalService.Upvote, "personal", RouteAdmission.Default, personal.UpvoteAsync);
        routes.Map<UpdateCurPersonalPlanReq>(PersonalService.UpdateCurPlan, "personal", RouteAdmission.Role, personal.UpdateCurPlanAsync);
        routes.Map<UpdatePersonalPlanNameReq>(PersonalService.UpdatePlanName, "personal", RouteAdmission.Role, personal.UpdatePlanNameAsync);
        routes.Map<EquipSocialOutfitReq>(SocialOutfitService.Equip, "socialOutfit", RouteAdmission.Role, socialOutfit.EquipAsync);
        routes.Map(SocialOutfitService.Refresh, "socialOutfit", RouteAdmission.Role, socialOutfit.RefreshAsync);
        routes.Map(GuildHomeService.EnterHome, "guildHome", RouteAdmission.Default, guildHome.EnterAsync);
        routes.Map<UseGveHuntingSkillReq>(GuildHuntingService.UseSkill, "guildHunt", RouteAdmission.Default, hunting.UseSkillAsync);
        routes.Map(GuildHuntingService.History, "guildHunt", RouteAdmission.Role, hunting.HistoryAsync);
        routes.Map(GuildCampfireService.Info, "campfire", RouteAdmission.Economic, campfire.InfoAsync);
        routes.Map<GuildCampFireAddAccelerantReq>(GuildCampfireService.Accelerant, "campfire", RouteAdmission.Economic, campfire.AccelerantAsync);
        routes.Map(GuildCampfireService.PlayerInfo, "campfire", RouteAdmission.Economic, campfire.PlayerInfoAsync);
        routes.Map(GuildChallengeService.Info, "guildChallenge", RouteAdmission.Economic, guildChallenge.InfoAsync);
        routes.Map<GuildDungeonChallengeOpenReq>(GuildChallengeService.Open, "guildChallenge", RouteAdmission.Economic, guildChallenge.OpenAsync);
        routes.Map<DungeonCallMemberReq>(GuildChallengeService.CallMembers, "guildChallenge", RouteAdmission.Economic, guildChallenge.CallMembersAsync);
        routes.Map<CreateFriendChatGroupReq>(FriendExtraService.CreateGroup, "friendExtra", RouteAdmission.Economic, friendExtra.CreateAsync);
        routes.Map<RenameFriendChatGroupReq>(FriendExtraService.RenameGroup, "friendExtra", RouteAdmission.Economic, friendExtra.RenameAsync);
        routes.Map<InviteFriendChatGroupReq>(FriendExtraService.InviteGroup, "friendExtra", RouteAdmission.Economic, friendExtra.InviteAsync);
        routes.Map<KickFriendChatGroupReq>(FriendExtraService.KickGroup, "friendExtra", RouteAdmission.Economic, friendExtra.KickAsync);
        routes.Map<DismissFriendChatGroupReq>(FriendExtraService.DismissGroup, "friendExtra", RouteAdmission.Economic, friendExtra.DismissAsync);
        routes.Map<LeaveFriendChatGroupReq>(FriendExtraService.LeaveGroup, "friendExtra", RouteAdmission.Economic, friendExtra.LeaveAsync);
        routes.Map<SetFriendChatGroupDNDReq>(FriendExtraService.SetGroupDnd, "friendExtra", RouteAdmission.Economic, friendExtra.DndAsync);
        routes.Map(FriendExtraService.FetchMetadata, "friendExtra", RouteAdmission.Economic, friendExtra.FetchAsync);
        routes.Map<SetFriendSystemMetadataReq>(FriendExtraService.SetMetadata, "friendExtra", RouteAdmission.Economic, friendExtra.SetAsync);
        routes.Map<GiveFriendGiftReq>(FriendExtraService.GiveGift, "friendExtra", RouteAdmission.Economic, friendExtra.GiftAsync);
        routes.Map(FriendExtraService.Recommend, "friendExtra", RouteAdmission.Economic, friendExtra.RecommendAsync);
    }

    private void MapTeams(MessageRoutes routes)
    {
        routes.Map<TeamCreateReq>(TeamService.TeamCreate, "teams", RouteAdmission.Economic, teams.CreateAsync);
        routes.Map(TeamService.TeamLeave, "teams", RouteAdmission.Economic, teams.LeaveAsync);
        routes.Map<ChangeTeamTypeReq>(TeamService.TeamChangeType, "teams", RouteAdmission.Economic, teams.ChangeTypeAsync);
        routes.Map<ChangeTeamInfoReq>(TeamService.TeamChangeInfo, "teams", RouteAdmission.Economic, teams.ChangeInfoAsync);
        routes.Map<InviteIntoTeamReq>(TeamService.TeamInvite, "teams", RouteAdmission.Economic, teams.InviteAsync);
        routes.Map<InviteeResp>(TeamService.TeamInviteeResp, "teams", RouteAdmission.Economic, teams.InviteeRespAsync);
        routes.Map<TeamOperateReq>(TeamService.TeamOperate, "teams", RouteAdmission.Economic, teams.OperateAsync);
        routes.Map<RallyTeamReq>(TeamService.TeamRally, "teams", RouteAdmission.Economic, teams.RallyAsync);
        routes.Map<RallyTeamMemberResponse>(TeamService.TeamRallyResp, "teams", RouteAdmission.Economic, teams.RallyResponseAsync);
        routes.Map<EditorTeamReq>(TeamService.TeamEditor, "teams", RouteAdmission.Economic, teams.EditorAsync);
        routes.Map<GetTeamListReq>(TeamService.TeamGetList, "teams", RouteAdmission.Economic, teams.GetListAsync);
        routes.Map<EnterTeamReq>(TeamService.TeamEnter, "teams", RouteAdmission.Economic, teams.EnterAsync);
        routes.Map<TeamSetFlagReq>(TeamService.TeamSetFlag, "teams", RouteAdmission.Economic, teams.SetFlagAsync);
        routes.Map<TeamInviteListReq>(TeamService.TeamInviteList, "teams", RouteAdmission.Economic, teams.InviteListAsync);
        routes.Map<ApplyListOperateReq>(TeamService.TeamApply, "teams", RouteAdmission.Economic, teams.ApplyAsync);
        routes.Map<TeamFollowOperReq>(TeamService.TeamFollow, "teams", RouteAdmission.Economic, teams.FollowAsync);
        routes.Map<TeamRecruitReq>(TeamService.TeamRecruit, "teams", RouteAdmission.Economic, teams.RecruitAsync);
        routes.Map<ChangeTeamMemberStateReq>(TeamService.TeamChangeMemberState, "teams", RouteAdmission.Economic, teams.ChangeMemberStateAsync);
        routes.Map(GuildService.EliteTeam, "teams", RouteAdmission.Economic, guilds.EliteTeamAsync);
    }

    private void MapGuildCore(MessageRoutes routes)
    {
        routes.Map<BuyGuildCommodityReq>(GuildService.BuyCommodity, "guilds", RouteAdmission.Economic, guilds.BuyCommodityAsync);
        routes.Map<ReceiveGuildWeekActiveRewardReq>(GuildService.ReceiveWeekActiveReward, "guilds", RouteAdmission.Economic, guilds.ReceiveWeekRewardAsync);
        routes.Map(GuildService.TakeActivityBonus, "guilds", RouteAdmission.Economic, guilds.TakeActivityBonusAsync);
        routes.Map<PullRecommendPlayersReq>(GuildService.RecommendPlayers, "guilds", RouteAdmission.Economic, guilds.RecommendPlayersAsync);
        routes.Map<GuildEventReq>(GuildService.Events, "guilds", RouteAdmission.Economic, guilds.EventsAsync);
        routes.Map(GuildService.MoneyRank, "guilds", RouteAdmission.Economic, guilds.MoneyRankAsync);
        routes.Map<GuildCreateReq>(GuildService.Create, "guilds", RouteAdmission.Economic, guilds.CreateAsync);
        routes.Map<CheckSensitiveWordReq>(GuildService.CheckSensitive, "guilds", RouteAdmission.Economic, guilds.CheckSensitiveAsync);
        routes.Map<GuildRecommendListReq>(GuildService.Recommend, "guilds", RouteAdmission.Economic, guilds.RecommendAsync);
        routes.Map<GuildCreateHelperListReq>(GuildService.HelperList, "guilds", RouteAdmission.Economic, guilds.HelperListAsync);
        routes.Map(GuildService.CancelCreate, "guilds", RouteAdmission.Economic, guilds.CancelCreateAsync);
        routes.Map<GuildCreateHelperOpeReq>(GuildService.HelperOpe, "guilds", RouteAdmission.Economic, guilds.HelperOpeAsync);
        routes.Map(GuildService.Convene, "guilds", RouteAdmission.Economic, guilds.ConveneAsync);
        routes.Map<ConfirmConveneReq>(GuildService.ConfirmConvene, "guilds", RouteAdmission.Economic, guilds.ConfirmConveneAsync);
        routes.Map(GuildService.PullHelperApply, "guilds", RouteAdmission.Economic, guilds.PullHelperApplyAsync);
        routes.Map<CreateHelperApplyOpeReq>(GuildService.HelperApplyOpe, "guilds", RouteAdmission.Economic, guilds.HelperApplyOpeAsync);
        routes.Map<InviteCreateHelperTokenReq>(GuildService.InviteHelperToken, "guilds", RouteAdmission.Economic, guilds.InviteHelperTokenAsync);
        routes.Map<InviteCreateHelperOpeReq>(GuildService.InviteHelperOpe, "guilds", RouteAdmission.Economic, guilds.InviteHelperOpeAsync);
        routes.Map<GuildCreateHelperLeaveReq>(GuildService.HelperLeave, "guilds", RouteAdmission.Economic, guilds.HelperLeaveAsync);
        routes.Map<GuildRecruitReq>(GuildService.Recruit, "guilds", RouteAdmission.Economic, guilds.RecruitAsync);
        routes.Map<GuildDetailInfoReq>(GuildService.Detail, "guilds", RouteAdmission.Economic, guilds.DetailAsync);
        routes.Map<GuildMemberIDListReq>(GuildService.MemberIds, "guilds", RouteAdmission.Economic, guilds.MemberIdsAsync);
        routes.Map<SyncGuildDataOnceReq>(GuildService.SyncOnce, "guilds", RouteAdmission.Economic, guilds.SyncOnceAsync);
        routes.Map<GuildBuildLevelUpReq>(GuildService.BuildUpgrade, "guilds", RouteAdmission.Economic, guilds.BuildUpgradeAsync);
        routes.Map<GuildEnterApplyReq>(GuildService.Apply, "guilds", RouteAdmission.Economic, guilds.ApplyAsync);
        routes.Map<GuildEnterApplyOpeReq>(GuildService.ApplyOpe, "guilds", RouteAdmission.Economic, guilds.ApplyOpeAsync);
        routes.Map<GuildInviteReq>(GuildService.Invite, "guilds", RouteAdmission.Economic, guilds.InviteAsync);
        routes.Map<GuildInviteAckReq>(GuildService.InviteAck, "guilds", RouteAdmission.Economic, guilds.InviteAckAsync);
        routes.Map<GuildChangeOfficialReq>(GuildService.ChangeOfficial, "guilds", RouteAdmission.Economic, guilds.ChangeOfficialAsync);
        routes.Map<GuildMemberKickReq>(GuildService.Kick, "guilds", RouteAdmission.Economic, guilds.KickAsync);
        routes.Map(GuildService.Leave, "guilds", RouteAdmission.Economic, guilds.LeaveAsync);
        routes.Map<GuildEditReq>(GuildService.Edit, "guilds", RouteAdmission.Economic, guilds.EditAsync);
    }

    private void MapGuildOperations(MessageRoutes routes)
    {
        routes.Map(GuildService.EliteQuery, "guilds", RouteAdmission.Economic, guilds.EliteQueryAsync);
        routes.Map<QuickJoinEliteGroupReq>(GuildService.EliteSave, "guilds", RouteAdmission.Economic, guilds.EliteSaveAsync);
        routes.Map(GuildService.ToolList, "guilds", RouteAdmission.Economic, guilds.ToolListAsync);
        routes.Map<GuildWartimeToolBelongOperReq>(GuildService.ToolAssign, "guilds", RouteAdmission.Economic, guilds.ToolAssignAsync);
        routes.Map<GuildWartimeToolExchangeOperReq>(GuildService.ToolExchange, "guilds", RouteAdmission.Economic, guilds.ToolExchangeAsync);
        routes.Map<GuildWartimeStorePrepayReq>(GuildService.Prepay, "guilds", RouteAdmission.Economic, guilds.PrepayAsync);
        routes.Map<GuildAttackCityBuildReq>(GuildService.BuildCannon, "guilds", RouteAdmission.Economic, guilds.BuildCannonAsync);
        routes.Map(GuildService.CancelPrepay, "guilds", RouteAdmission.Economic, guilds.CancelPrepayAsync);
        routes.Map<GuildVehicleUseReq>(GuildService.VehicleUse, "guilds", RouteAdmission.Economic, guilds.VehicleUseAsync);
        routes.Map(GuildService.MergePull, "guilds", RouteAdmission.Economic, guilds.MergePullAsync);
        routes.Map(GuildService.MergeRecommend, "guilds", RouteAdmission.Economic, guilds.MergeRecommendAsync);
        routes.Map<MergeGuildDetailDataReq>(GuildService.MergeDetail, "guilds", RouteAdmission.Economic, guilds.MergeDetailAsync);
        routes.Map<SaveMergeMemberReq>(GuildService.MergeMembers, "guilds", RouteAdmission.Economic, guilds.MergeMembersAsync);
        routes.Map<MergeGuildOpeReq>(GuildService.MergeOpe, "guilds", RouteAdmission.Economic, guilds.MergeOpeAsync);
        routes.Map<InviteMergeGuildReq>(GuildService.MergeAsk, "guilds", RouteAdmission.Economic, guilds.MergeAskAsync);
        routes.Map<GuildPeachReq>(GuildService.Peach, "guilds", RouteAdmission.Economic, guilds.PeachAsync);
        routes.Map(GuildService.PeachApply, "guilds", RouteAdmission.Economic, guilds.PeachApplyAsync);
        routes.Map<GuildBroadEmailReq>(GuildService.BroadEmail, "guilds", RouteAdmission.Economic, guilds.BroadEmailAsync);
        routes.Map(GuildService.MarketBooth, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildService.MarketItems, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildService.MarketList, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildService.MarketUnlist, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildService.MarketBuy, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildService.MarketSellRecords, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildService.MarketBuyRecords, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildService.MarketUpdate, "guilds", RouteAdmission.Economic, guilds.MarketAsync);
        routes.Map(GuildLeagueService.MgrData, "guilds", RouteAdmission.Economic, s => guilds.LeagueMgrDataAsync(s, GuildLeagueService.MgrData));
        routes.Map(GuildLeagueService.Info, "guilds", RouteAdmission.Economic, s => guilds.LeagueInfoAsync(s, GuildLeagueService.Info));
        routes.Map(GuildLeagueService.GroupInfo, "guilds", RouteAdmission.Economic, s => guilds.LeagueGroupInfoAsync(s, GuildLeagueService.GroupInfo));
    }

    private void MapCaravanEmblemsAndChat(MessageRoutes routes)
    {
        routes.Map(GuildCaravanService.Show, "caravan", RouteAdmission.Economic, caravan.ShowAsync);
        routes.Map<GuildCatShopItemSubmitReq>(GuildCaravanService.Submit, "caravan", RouteAdmission.Economic, caravan.SubmitAsync);
        routes.Map<GuildCatShopItemWantHelpReq>(GuildCaravanService.WantHelp, "caravan", RouteAdmission.Economic, caravan.WantHelpAsync);
        routes.Map<GuildCatShopItemHelpOtherReq>(GuildCaravanService.HelpOther, "caravan", RouteAdmission.Economic, caravan.HelpOtherAsync);
        routes.Map(GuildCaravanService.HelpList, "caravan", RouteAdmission.Economic, caravan.HelpListAsync);
        routes.Map(GuildCaravanService.Depart, "caravan", RouteAdmission.Economic, caravan.DepartAsync);
        routes.Map<GuildCatShopRefreshReq>(GuildCaravanService.Refresh, "caravan", RouteAdmission.Economic, caravan.RefreshAsync);
        routes.Map<SetUpEmblemReq>(EmblemService.SetUp, "emblems", RouteAdmission.Role, emblems.SetUpAsync);
        routes.Map<SetDownEmblemReq>(EmblemService.SetDown, "emblems", RouteAdmission.Role, emblems.SetDownAsync);
        routes.Map<UpgradeEmblemReq>(EmblemService.Upgrade, "emblems", RouteAdmission.Role, emblems.UpgradeAsync);
        routes.Map<EmbleSwitchPlanReq>(EmblemService.SwitchPlan, "emblems", RouteAdmission.Role, emblems.SwitchPlanAsync);
        routes.Map<EmblemPlanChangeNameReq>(EmblemService.RenamePlan, "emblems", RouteAdmission.Role, emblems.RenamePlanAsync);
        routes.Map(ChatService.ImUserSig, "chat", RouteAdmission.Default, chat.UserSigAsync);
        routes.Map<JoinChatChannelReq>(ChatService.JoinChannel, "chat", RouteAdmission.Role, chat.JoinAsync);
        routes.Map(ChatService.TunnelUp, "chat", RouteAdmission.Role, (session, _, body) => chat.TunnelAsync(session, body));
        routes.Map(ChatService.VoiceCapability, "chat", RouteAdmission.Role, chat.VoiceCapabilityAsync);
        routes.Map(ChatService.VoiceUpload, "chat", RouteAdmission.Role, chat.VoiceUploadAsync);
        routes.Map(ChatService.VoiceFetch, "chat", RouteAdmission.Role, chat.VoiceFetchAsync);
    }
}
