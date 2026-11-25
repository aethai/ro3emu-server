using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's GuildSettingConfig (data/config/GuildSettingConfig.json): a setting name and its row, whose
/// _kValue holds the value.</summary>
[ConfigConstants("GuildSettingConfig", ValueField = "_kValue")]
public readonly partial record struct GuildSettings(IReadOnlyDictionary<string, JsonElement> Values)
{
    [ConfigConstant("ActiveImpeachCD")] public partial long? ActiveImpeachCD { get; }
    [ConfigConstant("ActiveImpeachChairmanOffineTimeLimit")]
    public partial long? ActiveImpeachChairmanOffineTimeLimit { get; }
    [ConfigConstant("ActiveImpeachJoinDay")] public partial long? ActiveImpeachJoinDay { get; }
    [ConfigConstant("ActiveImpeachMoney")] public partial long[]? ActiveImpeachMoney { get; }
    [ConfigConstant("ActiveImpeachOnlineDayNum")] public partial long? ActiveImpeachOnlineDayNum { get; }
    [ConfigConstant("ActiveImpeachOnlineScale")] public partial long? ActiveImpeachOnlineScale { get; }
    [ConfigConstant("ActiveImpeachTime")] public partial long? ActiveImpeachTime { get; }
    [ConfigConstant("ApplicationListMaxNum")] public partial long? ApplicationListMaxNum { get; }
    [ConfigConstant("ApplicationListPersonMaxNum")] public partial long? ApplicationListPersonMaxNum { get; }
    [ConfigConstant("GuildApplyTimeout")] public partial long? GuildApplyTimeout { get; }
    [ConfigConstant("GuildChangeNameCost")] public partial long[]? GuildChangeNameCost { get; }
    [ConfigConstant("GuildChangeNameTimeLimit")] public partial long? GuildChangeNameTimeLimit { get; }
    [ConfigConstant("GuildCreateCost")] public partial long[]? GuildCreateCost { get; }
    [ConfigConstant("GuildCreateCostShow")] public partial long[]? GuildCreateCostShow { get; }
    [ConfigConstant("GuildEmailContentNumNew")] public partial long[]? GuildEmailContentNumNew { get; }
    [ConfigConstant("GuildEventDateMaxNum")] public partial long? GuildEventDateMaxNum { get; }
    [ConfigConstant("GuildGameplayMember01")] public partial long[]? GuildGameplayMember01 { get; }
    [ConfigConstant("GuildGatherCD")] public partial long? GuildGatherCD { get; }
    [ConfigConstant("GuildGatherCost")] public partial long[]? GuildGatherCost { get; }
    [ConfigConstant("GuildGatherInviteTime")] public partial long? GuildGatherInviteTime { get; }
    [ConfigConstant("GuildInvitationTime")] public partial long? GuildInvitationTime { get; }
    [ConfigConstant("GuildLeagueActive")] public partial long? GuildLeagueActive { get; }
    [ConfigConstant("GuildLeagueActivityID0")] public partial long? GuildLeagueActivityID0 { get; }
    [ConfigConstant("GuildLeagueActivityID1")] public partial long? GuildLeagueActivityID1 { get; }
    [ConfigConstant("GuildLeagueActivityID2")] public partial long? GuildLeagueActivityID2 { get; }
    [ConfigConstant("GuildLeagueActivityID3")] public partial long? GuildLeagueActivityID3 { get; }
    [ConfigConstant("GuildLeagueAuctionAwardNeedScore")] public partial long? GuildLeagueAuctionAwardNeedScore { get; }
    [ConfigConstant("GuildLeagueAuctionNeedNum")] public partial long? GuildLeagueAuctionNeedNum { get; }
    [ConfigConstant("GuildLeagueAuctionNeedScore")] public partial long? GuildLeagueAuctionNeedScore { get; }
    [ConfigConstant("GuildLeagueEliteNum")] public partial long? GuildLeagueEliteNum { get; }
    [ConfigConstant("GuildLeagueEliteSettingMailID")] public partial long? GuildLeagueEliteSettingMailID { get; }
    [ConfigConstant("GuildLeagueEliteSettingMailTime")] public partial long? GuildLeagueEliteSettingMailTime { get; }
    [ConfigConstant("GuildLeagueGuildIconNoticeMailID")] public partial long? GuildLeagueGuildIconNoticeMailID { get; }
    [ConfigConstant("GuildLeagueHistoryReportNum")] public partial long? GuildLeagueHistoryReportNum { get; }
    [ConfigConstant("GuildLeagueJoinAwardFailedMailID")] public partial long? GuildLeagueJoinAwardFailedMailID { get; }
    [ConfigConstant("GuildLeagueJoinAwardMailID")] public partial long? GuildLeagueJoinAwardMailID { get; }
    [ConfigConstant("GuildLeagueLevelNoticeMailID")] public partial long? GuildLeagueLevelNoticeMailID { get; }
    [ConfigConstant("GuildLeagueLevelWarnMailID")] public partial long? GuildLeagueLevelWarnMailID { get; }
    [ConfigConstant("GuildLeagueMainSceneNeedPosition")]
    public partial long[]? GuildLeagueMainSceneNeedPosition { get; }
    [ConfigConstant("GuildLeagueOccupyNoticeType")] public partial long[][]? GuildLeagueOccupyNoticeType { get; }
    [ConfigConstant("GuildLeaguePeopleNum")] public partial long? GuildLeaguePeopleNum { get; }
    [ConfigConstant("GuildLeaguePersonAwardNeedScore")] public partial long? GuildLeaguePersonAwardNeedScore { get; }
    [ConfigConstant("GuildLeagueSceneID")] public partial long[]? GuildLeagueSceneID { get; }
    [ConfigConstant("GuildLeagueTriggerStayAreaTime")] public partial long? GuildLeagueTriggerStayAreaTime { get; }
    [ConfigConstant("GuildMail17")] public partial long? GuildMail17 { get; }
    [ConfigConstant("GuildMail18")] public partial long? GuildMail18 { get; }
    [ConfigConstant("GuildMail21")] public partial long? GuildMail21 { get; }
    [ConfigConstant("GuildMap")] public partial long? GuildMap { get; }
    [ConfigConstant("GuildMassEmailCD")] public partial long? GuildMassEmailCD { get; }
    [ConfigConstant("GuildMassEmailNum")] public partial long? GuildMassEmailNum { get; }
    [ConfigConstant("GuildMaterialsMax")] public partial long? GuildMaterialsMax { get; }
    [ConfigConstant("GuildMergeLapseTimeLimit")] public partial long? GuildMergeLapseTimeLimit { get; }
    [ConfigConstant("GuildMergeListNumLimit")] public partial long? GuildMergeListNumLimit { get; }
    [ConfigConstant("GuildMergeNoticeLimitNew")] public partial long[]? GuildMergeNoticeLimitNew { get; }
    [ConfigConstant("GuildMergeTimeCD")] public partial long? GuildMergeTimeCD { get; }
    [ConfigConstant("GuildNameNumNew")] public partial long[]? GuildNameNumNew { get; }
    [ConfigConstant("GuildNoticeContentNumNew")] public partial long[]? GuildNoticeContentNumNew { get; }
    [ConfigConstant("GuildPersonResponseMaxNum")] public partial long? GuildPersonResponseMaxNum { get; }
    [ConfigConstant("GuildRecruitButtonCD")] public partial long? GuildRecruitButtonCD { get; }
    [ConfigConstant("GuildRecruitInviteListNum")] public partial long? GuildRecruitInviteListNum { get; }
    [ConfigConstant("GuildRecruitNoticeMaxNum")] public partial long? GuildRecruitNoticeMaxNum { get; }
    [ConfigConstant("GuildResponseInviteTime")] public partial long? GuildResponseInviteTime { get; }
    [ConfigConstant("GuildResponseMaxNum")] public partial long? GuildResponseMaxNum { get; }
    [ConfigConstant("GuildResponsePeopleLimit")] public partial long? GuildResponsePeopleLimit { get; }
    [ConfigConstant("GuildResponseReleaseRecruitment")] public partial long? GuildResponseReleaseRecruitment { get; }
    [ConfigConstant("GuildResponseTimeLimit")] public partial long? GuildResponseTimeLimit { get; }
    [ConfigConstant("GuildSendEmailPermission")] public partial long? GuildSendEmailPermission { get; }
    [ConfigConstant("GuildSpecialWeekActiveLevel")] public partial long? GuildSpecialWeekActiveLevel { get; }
    [ConfigConstant("GuildSpecialWeekActiveTime")] public partial long? GuildSpecialWeekActiveTime { get; }
    [ConfigConstant("GuildTemporaryRegularTime")] public partial long? GuildTemporaryRegularTime { get; }
    [ConfigConstant("GuildTransportHelpDayNum")] public partial long? GuildTransportHelpDayNum { get; }
    [ConfigConstant("GuildTransportRefreshNum")] public partial long? GuildTransportRefreshNum { get; }
    [ConfigConstant("GuildTransportRequestCD")] public partial long? GuildTransportRequestCD { get; }
    [ConfigConstant("GuildTransportRequestDayNum")] public partial long? GuildTransportRequestDayNum { get; }
    [ConfigConstant("GuildTransportRequestItemTypeNum")] public partial long? GuildTransportRequestItemTypeNum { get; }
    [ConfigConstant("GuildTransportRoundNum")] public partial long? GuildTransportRoundNum { get; }
    [ConfigConstant("GuildTransportRoundSAB")] public partial long[][]? GuildTransportRoundSAB { get; }
    [ConfigConstant("GuildVoiceChannelBlacklistDuration")]
    public partial long? GuildVoiceChannelBlacklistDuration { get; }
    [ConfigConstant("GuildVoiceChannelCount")] public partial long? GuildVoiceChannelCount { get; }
    [ConfigConstant("GuildVoiceChannelInviteCooldown")]
    public partial long? GuildVoiceChannelInviteCooldown { get; }
    [ConfigConstant("GuildVoiceChannelInviteRejectDuration")]
    public partial long? GuildVoiceChannelInviteRejectDuration { get; }
}
