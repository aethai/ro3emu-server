using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's PublicParamConfig (data/config/PublicParamConfig.json): a name and its value.</summary>
[ConfigConstants("PublicParamConfig")]
public readonly partial record struct PublicParams(IReadOnlyDictionary<string, JsonElement> Values)
{
    [ConfigConstant("AdventureGroupAnnounceLimit")] public partial long[]? AdventureGroupAnnounceLimit { get; }
    [ConfigConstant("AdventureGroupApplyHelperLimit")] public partial long? AdventureGroupApplyHelperLimit { get; }
    [ConfigConstant("AdventureGroupApplyNormalLimit")] public partial long? AdventureGroupApplyNormalLimit { get; }
    [ConfigConstant("AdventureGroupChangeOwnerCD")] public partial long? AdventureGroupChangeOwnerCD { get; }
    [ConfigConstant("AdventureGroupConveneCD")] public partial long? AdventureGroupConveneCD { get; }
    [ConfigConstant("AdventureGroupConveneTime")] public partial long? AdventureGroupConveneTime { get; }
    [ConfigConstant("AdventureGroupCreateSnaplingSceneId")]
    public partial long? AdventureGroupCreateSnaplingSceneId { get; }
    [ConfigConstant("AdventureGroupDeclareContentLength")]
    public partial long[]? AdventureGroupDeclareContentLength { get; }
    [ConfigConstant("AdventureGroupDungeonLiveTime")] public partial long? AdventureGroupDungeonLiveTime { get; }
    [ConfigConstant("AdventureGroupDungeonSceneId")] public partial long? AdventureGroupDungeonSceneId { get; }
    [ConfigConstant("AdventureGroupHelperCreateSuccessLimit")]
    public partial long? AdventureGroupHelperCreateSuccessLimit { get; }
    [ConfigConstant("AdventureGroupHelperCreateTimeLimit")]
    public partial long? AdventureGroupHelperCreateTimeLimit { get; }
    [ConfigConstant("AdventureGroupLabelLimit")] public partial long[]? AdventureGroupLabelLimit { get; }
    [ConfigConstant("AdventureGroupLeavePunish")] public partial long? AdventureGroupLeavePunish { get; }
    [ConfigConstant("AdventureGroupMaxCreateLimit")] public partial long? AdventureGroupMaxCreateLimit { get; }
    [ConfigConstant("AdventureGroupMaxEventLimit")] public partial long? AdventureGroupMaxEventLimit { get; }
    [ConfigConstant("AdventureGroupMemberLimit")] public partial long? AdventureGroupMemberLimit { get; }
    [ConfigConstant("AdventureGroupMoneyWeeklyLimit")] public partial long? AdventureGroupMoneyWeeklyLimit { get; }
    [ConfigConstant("AdventureGroupNameLimit")] public partial long[]? AdventureGroupNameLimit { get; }
    [ConfigConstant("AdventureGroupNickNameLength")] public partial long[]? AdventureGroupNickNameLength { get; }
    [ConfigConstant("AdventureGroupNormalApplyLimit")] public partial long? AdventureGroupNormalApplyLimit { get; }
    [ConfigConstant("AdventureGroupSpeechLength")] public partial long[]? AdventureGroupSpeechLength { get; }
    [ConfigConstant("AdventureGroupSwornTask")] public partial long[]? AdventureGroupSwornTask { get; }
    [ConfigConstant("AdventureGroupWaterSaplingLimit")] public partial long? AdventureGroupWaterSaplingLimit { get; }
    [ConfigConstant("AUCTION_BATCH_NUM")] public partial long? AUCTIONBATCHNUM { get; }
    [ConfigConstant("AUCTION_CURRENCY")] public partial long? AUCTIONCURRENCY { get; }
    [ConfigConstant("AuctionCountdown")] public partial long? AuctionCountdown { get; }
    [ConfigConstant("AuctionExtendTime")] public partial long? AuctionExtendTime { get; }
    [ConfigConstant("AuctionRecordLimit")] public partial long? AuctionRecordLimit { get; }
    [ConfigConstant("BlackMarketDelayRangeSec")] public partial long[]? BlackMarketDelayRangeSec { get; }
    [ConfigConstant("BlackMarketFeeRate")] public partial long? BlackMarketFeeRate { get; }
    [ConfigConstant("BlackMarketItemValidSec")] public partial long? BlackMarketItemValidSec { get; }
    [ConfigConstant("BlackMarketListCooldownSec")] public partial long? BlackMarketListCooldownSec { get; }
    [ConfigConstant("BlackMarketPriceAdjust")] public partial long[]? BlackMarketPriceAdjust { get; }
    [ConfigConstant("BlackMarketRecentAvgSample")] public partial long? BlackMarketRecentAvgSample { get; }
    [ConfigConstant("BlackMarketSellStep")] public partial long? BlackMarketSellStep { get; }
    [ConfigConstant("BlackMarketStallCount")] public partial long? BlackMarketStallCount { get; }
    [ConfigConstant("BoothRange")] public partial long? BoothRange { get; }
    [ConfigConstant("CampfireMaxSeedNum")] public partial long? CampfireMaxSeedNum { get; }
    [ConfigConstant("CampfireMonsterLimit")] public partial long? CampfireMonsterLimit { get; }
    [ConfigConstant("ChickenBagLimit")] public partial long? ChickenBagLimit { get; }
    [ConfigConstant("CreditBooth")] public partial long? CreditBooth { get; }
    [ConfigConstant("DiscountMerchantSkill")] public partial long? DiscountMerchantSkill { get; }
    [ConfigConstant("DraculaNpcConfigId")] public partial long? DraculaNpcConfigId { get; }
    [ConfigConstant("DraculaWeddingDynamicStoryIdArr")] public partial long[]? DraculaWeddingDynamicStoryIdArr { get; }
    [ConfigConstant("DragonIslandBossPoints")] public partial long? DragonIslandBossPoints { get; }
    [ConfigConstant("DragonIslandCurrencyBaseLose")] public partial long? DragonIslandCurrencyBaseLose { get; }
    [ConfigConstant("DragonIslandCurrencyBaseWin")] public partial long? DragonIslandCurrencyBaseWin { get; }
    [ConfigConstant("DragonIslandCurrencyLimit")] public partial long? DragonIslandCurrencyLimit { get; }
    [ConfigConstant("EatChickenPoisonBuffId")] public partial long? EatChickenPoisonBuffId { get; }
    [ConfigConstant("EnchantStarsLevel")] public partial long[][]? EnchantStarsLevel { get; }
    [ConfigConstant("EtchantPlanMax")] public partial long? EtchantPlanMax { get; }
    [ConfigConstant("EtchantRefreshItemCount")] public partial long? EtchantRefreshItemCount { get; }
    [ConfigConstant("EtchantRefreshItemId")] public partial long? EtchantRefreshItemId { get; }
    [ConfigConstant("FinalAtkSpdThdAndCastSpdThd")] public partial long[]? FinalAtkSpdThdAndCastSpdThd { get; }
    [ConfigConstant("FiveGamePlay_RandomDuration")] public partial long? FiveGamePlayRandomDuration { get; }
    [ConfigConstant("GamePlayAssembleMaxTime")] public partial long? GamePlayAssembleMaxTime { get; }
    [ConfigConstant("GamePlayAuctionAwaitTime")] public partial long? GamePlayAuctionAwaitTime { get; }
    [ConfigConstant("GamePlayAuctionDividend")] public partial long? GamePlayAuctionDividend { get; }
    [ConfigConstant("GetInPositionMaxTime")] public partial long? GetInPositionMaxTime { get; }
    [ConfigConstant("GuildAuctionExhibit")] public partial long? GuildAuctionExhibit { get; }
    [ConfigConstant("GuildAuctionTime")] public partial long? GuildAuctionTime { get; }
    [ConfigConstant("GuildChallengeSet")] public partial long[]? GuildChallengeSet { get; }
    [ConfigConstant("GuildLeagueMaxPlayerNumber")] public partial long[]? GuildLeagueMaxPlayerNumber { get; }
    [ConfigConstant("HeroSpiritItemExp")] public partial long? HeroSpiritItemExp { get; }
    [ConfigConstant("HeroSpiritItemId")] public partial long? HeroSpiritItemId { get; }
    [ConfigConstant("ImmuneDeadConfirmTimeMs")] public partial long? ImmuneDeadConfirmTimeMs { get; }
    [ConfigConstant("InviteWaitingTime")] public partial long? InviteWaitingTime { get; }
    [ConfigConstant("JourneyAttrInitial")] public partial long[][]? JourneyAttrInitial { get; }
    [ConfigConstant("LeaderBoardRefreshIntervalTs")] public partial long? LeaderBoardRefreshIntervalTs { get; }
    [ConfigConstant("LibraryCardUnlockReward")] public partial JsonElement? LibraryCardUnlockReward { get; }
    [ConfigConstant("LibraryMonsterUnlockReward")] public partial long? LibraryMonsterUnlockReward { get; }
    [ConfigConstant("LibraryOverviewPointID")] public partial long? LibraryOverviewPointID { get; }
    [ConfigConstant("LifeJobGameCountdown")] public partial long? LifeJobGameCountdown { get; }
    [ConfigConstant("LuckyAwardPreview")] public partial long? LuckyAwardPreview { get; }
    [ConfigConstant("MountFlyMaxSpeed")] public partial long? MountFlyMaxSpeed { get; }
    [ConfigConstant("NoviceDungeonReverseDungeonID")] public partial long? NoviceDungeonReverseDungeonID { get; }
    [ConfigConstant("OnePieceMapBoxId")] public partial long? OnePieceMapBoxId { get; }
    [ConfigConstant("OnePieceMapBoxReId")] public partial long? OnePieceMapBoxReId { get; }
    [ConfigConstant("OnePieceMapMembersNum")] public partial long[]? OnePieceMapMembersNum { get; }
    [ConfigConstant("OnePieceMapRefresh")] public partial long[]? OnePieceMapRefresh { get; }
    [ConfigConstant("OnePieceRewardPreview1")] public partial long[]? OnePieceRewardPreview1 { get; }
    [ConfigConstant("OnePieceRewardPreview2")] public partial long[]? OnePieceRewardPreview2 { get; }
    [ConfigConstant("OnHookAwardSnapshotCD")] public partial long? OnHookAwardSnapshotCD { get; }
    [ConfigConstant("OnHookConditionMonito")] public partial long? OnHookConditionMonito { get; }
    [ConfigConstant("OnHookDailyFightTime")] public partial long? OnHookDailyFightTime { get; }
    [ConfigConstant("OnHookDailyRefreshTime")] public partial long? OnHookDailyRefreshTime { get; }
    [ConfigConstant("OnHookFightTimeLimit")] public partial long? OnHookFightTimeLimit { get; }
    [ConfigConstant("OnHookMonsterNum")] public partial long? OnHookMonsterNum { get; }
    [ConfigConstant("OnHookPauseButtonCD")] public partial long? OnHookPauseButtonCD { get; }
    [ConfigConstant("OnHookRecordLimit")] public partial long? OnHookRecordLimit { get; }
    [ConfigConstant("PerformanceDistance")] public partial long? PerformanceDistance { get; }
    [ConfigConstant("PersonalDefaultBackground")] public partial long? PersonalDefaultBackground { get; }
    [ConfigConstant("PersonalDefaultTheme")] public partial long? PersonalDefaultTheme { get; }
    [ConfigConstant("PersonalEmoEmpty")] public partial long? PersonalEmoEmpty { get; }
    [ConfigConstant("PersonalLikeCD")] public partial long? PersonalLikeCD { get; }
    [ConfigConstant("PersonalMaxCabinet")] public partial long? PersonalMaxCabinet { get; }
    [ConfigConstant("PersonalMaxCharacter")] public partial long? PersonalMaxCharacter { get; }
    [ConfigConstant("PersonalMaxSticker")] public partial long? PersonalMaxSticker { get; }
    [ConfigConstant("PersonalShowcaseDefault")] public partial long[]? PersonalShowcaseDefault { get; }
    [ConfigConstant("PersonalSignatureLimit")] public partial long? PersonalSignatureLimit { get; }
    [ConfigConstant("PersonalTemplateLimit")] public partial long? PersonalTemplateLimit { get; }
    [ConfigConstant("PetIncubationAccelerate")] public partial long? PetIncubationAccelerate { get; }
    [ConfigConstant("PetIncubationAccelerateItemId")] public partial long? PetIncubationAccelerateItemId { get; }
    [ConfigConstant("PetNotImproveBaseAttr")] public partial long[]? PetNotImproveBaseAttr { get; }
    [ConfigConstant("QueueNum")] public partial long? QueueNum { get; }
    [ConfigConstant("RelaxGVGLoseMMR")] public partial long? RelaxGVGLoseMMR { get; }
    [ConfigConstant("RelaxGVGStartMMR")] public partial long? RelaxGVGStartMMR { get; }
    [ConfigConstant("RelaxGVGWinMMR")] public partial long? RelaxGVGWinMMR { get; }
    [ConfigConstant("RewardDecayRatio")] public partial long[]? RewardDecayRatio { get; }
    [ConfigConstant("RunRingTaskTurntableItem")] public partial long? RunRingTaskTurntableItem { get; }
    [ConfigConstant("SchemeCD")] public partial long? SchemeCD { get; }
    [ConfigConstant("SchemeNameNumMax")] public partial long? SchemeNameNumMax { get; }
    [ConfigConstant("SchemeNum")] public partial long? SchemeNum { get; }
    [ConfigConstant("ShelvesTaxation_Miao")] public partial long? ShelvesTaxationMiao { get; }
    [ConfigConstant("ShelvesTaxation_Zeny")] public partial long? ShelvesTaxationZeny { get; }
    [ConfigConstant("ShelvesTaxationInterval_Miao")] public partial long[]? ShelvesTaxationIntervalMiao { get; }
    [ConfigConstant("ShelvesTaxationInterval_Zeny")] public partial long[]? ShelvesTaxationIntervalZeny { get; }
    [ConfigConstant("SiegeLoseMailId")] public partial long? SiegeLoseMailId { get; }
    [ConfigConstant("SiegeWinMailId")] public partial long? SiegeWinMailId { get; }
    [ConfigConstant("SOSCD")] public partial long? SOSCD { get; }
    [ConfigConstant("StallsBuyItemMailId")] public partial long? StallsBuyItemMailId { get; }
    [ConfigConstant("StallsCloseMailId")] public partial long? StallsCloseMailId { get; }
    [ConfigConstant("StallsCloseNullMailId")] public partial long? StallsCloseNullMailId { get; }
    [ConfigConstant("StallsCloseTimeMailId")] public partial long? StallsCloseTimeMailId { get; }
    [ConfigConstant("StallsDefaultPlaidNum")] public partial long? StallsDefaultPlaidNum { get; }
    [ConfigConstant("StallsExpenses")] public partial long[]? StallsExpenses { get; }
    [ConfigConstant("StallsIntervalRadius")] public partial long? StallsIntervalRadius { get; }
    [ConfigConstant("StallsLogsNumMax")] public partial long? StallsLogsNumMax { get; }
    [ConfigConstant("StallsMapID")] public partial long[]? StallsMapID { get; }
    [ConfigConstant("StallsModelId")] public partial long? StallsModelId { get; }
    [ConfigConstant("StallsPlaidMerchantSkill")] public partial long? StallsPlaidMerchantSkill { get; }
    [ConfigConstant("StallsSellItemMailId")] public partial long? StallsSellItemMailId { get; }
    [ConfigConstant("StallsShareCDTime")] public partial long? StallsShareCDTime { get; }
    [ConfigConstant("StallsShareWordCount")] public partial long? StallsShareWordCount { get; }
    [ConfigConstant("StallsSystemDefaultName")] public partial long? StallsSystemDefaultName { get; }
    [ConfigConstant("StallsSystemDefaultRoleName")] public partial long? StallsSystemDefaultRoleName { get; }
    [ConfigConstant("StallsSystemRestockCD")] public partial long? StallsSystemRestockCD { get; }
    [ConfigConstant("StallsSystemTime")] public partial long? StallsSystemTime { get; }
    [ConfigConstant("StallsTakenOffMailId")] public partial long? StallsTakenOffMailId { get; }
    [ConfigConstant("StallsTime")] public partial long? StallsTime { get; }
    [ConfigConstant("TerritoryAttackClearCD")] public partial long? TerritoryAttackClearCD { get; }
    [ConfigConstant("TerritoryAuctionAutoBidDelayAggressive")]
    public partial long[]? TerritoryAuctionAutoBidDelayAggressive { get; }
    [ConfigConstant("TerritoryAuctionAutoBidDelaySteady")]
    public partial long[]? TerritoryAuctionAutoBidDelaySteady { get; }
    [ConfigConstant("TerritoryAuctionAutoBidDelayStealth")]
    public partial long[]? TerritoryAuctionAutoBidDelayStealth { get; }
    [ConfigConstant("TerritoryAuctionAutoBidSteps")] public partial long[]? TerritoryAuctionAutoBidSteps { get; }
    [ConfigConstant("TerritoryAuctionBidIncrementRatio")]
    public partial long? TerritoryAuctionBidIncrementRatio { get; }
    [ConfigConstant("TerritoryAuctionFinalTimeRandomRange")]
    public partial long[]? TerritoryAuctionFinalTimeRandomRange { get; }
    [ConfigConstant("TerritoryAuctionMaxBidMinMultiplier")]
    public partial long? TerritoryAuctionMaxBidMinMultiplier { get; }
    [ConfigConstant("TerritoryAuctionMinBidCost")] public partial long? TerritoryAuctionMinBidCost { get; }
    [ConfigConstant("TerritoryAuctionMinGuildMemberCount")]
    public partial long? TerritoryAuctionMinGuildMemberCount { get; }
    [ConfigConstant("TerritoryAuctionPointSeatCount")] public partial long? TerritoryAuctionPointSeatCount { get; }
    [ConfigConstant("TerritoryAuctionSiegeSeatCount")] public partial long? TerritoryAuctionSiegeSeatCount { get; }
    [ConfigConstant("TerritoryAuctionStateSeatCount")] public partial long? TerritoryAuctionStateSeatCount { get; }
    [ConfigConstant("TerritoryChangeCampCD")] public partial long? TerritoryChangeCampCD { get; }
    [ConfigConstant("TerritoryFatigueBuffId")] public partial long? TerritoryFatigueBuffId { get; }
    [ConfigConstant("TerritoryFatigueBuffPileAddFromKill")]
    public partial long[][]? TerritoryFatigueBuffPileAddFromKill { get; }
    [ConfigConstant("TerritoryFatigueBuffPileAddTime")] public partial long? TerritoryFatigueBuffPileAddTime { get; }
    [ConfigConstant("TerritoryFatigueBuffPileDelStacks")]
    public partial long? TerritoryFatigueBuffPileDelStacks { get; }
    [ConfigConstant("TerritoryFatigueBuffPileDelTime")] public partial long? TerritoryFatigueBuffPileDelTime { get; }
    [ConfigConstant("TerritoryFatigueBuffReturnMapId")] public partial long? TerritoryFatigueBuffReturnMapId { get; }
    [ConfigConstant("TerritoryStrongHoldAuctionDefaultPrice")]
    public partial long? TerritoryStrongHoldAuctionDefaultPrice { get; }
    [ConfigConstant("TerritoryStrongHoldBidNum")] public partial long? TerritoryStrongHoldBidNum { get; }
    [ConfigConstant("TerritoryStrongHoldJoinAuctionAward")]
    public partial long? TerritoryStrongHoldJoinAuctionAward { get; }
    [ConfigConstant("TerritoryStrongHoldOccupyAuctionAward")]
    public partial long? TerritoryStrongHoldOccupyAuctionAward { get; }
    [ConfigConstant("TerritoryWorldDeathDropItem")] public partial long? TerritoryWorldDeathDropItem { get; }
    [ConfigConstant("TerritoryWorldDeathDropRate")] public partial double[]? TerritoryWorldDeathDropRate { get; }
    [ConfigConstant("TerritoryWorldDropItemCarryLimit")]
    public partial JsonElement? TerritoryWorldDropItemCarryLimit { get; }
    [ConfigConstant("TerritoryWorldMaterialNPC")] public partial long? TerritoryWorldMaterialNPC { get; }
    [ConfigConstant("TotemAttrLimits")] public partial long[][]? TotemAttrLimits { get; }
    [ConfigConstant("TotemComposeAttrRange")] public partial long[]? TotemComposeAttrRange { get; }
    [ConfigConstant("TotemCurrencyExp")] public partial long[][]? TotemCurrencyExp { get; }
    [ConfigConstant("TotemHoleUnlockLv")] public partial long[]? TotemHoleUnlockLv { get; }
    [ConfigConstant("TotemQualityExp")] public partial long[][]? TotemQualityExp { get; }
    [ConfigConstant("TotemReAttrItem")] public partial long[][]? TotemReAttrItem { get; }
    [ConfigConstant("TotemRefineAttrItem")] public partial long[]? TotemRefineAttrItem { get; }
    [ConfigConstant("TotemRefineNumPerMultiverse")] public partial long? TotemRefineNumPerMultiverse { get; }
    [ConfigConstant("TotemSkillInitMax")] public partial long[][]? TotemSkillInitMax { get; }
    [ConfigConstant("TradeConcernNum")] public partial long? TradeConcernNum { get; }
    [ConfigConstant("TradeCreditDefault")] public partial long? TradeCreditDefault { get; }
    [ConfigConstant("TradeCreditMin")] public partial long? TradeCreditMin { get; }
    [ConfigConstant("TradeLimitPrice")] public partial long? TradeLimitPrice { get; }
    [ConfigConstant("TradeListingsGeneralShowTime")] public partial long? TradeListingsGeneralShowTime { get; }
    [ConfigConstant("TradeListingsTopShowTime")] public partial long? TradeListingsTopShowTime { get; }
    [ConfigConstant("TradePreMaxNum")] public partial long? TradePreMaxNum { get; }
    [ConfigConstant("TradeRefreshCd")] public partial long? TradeRefreshCd { get; }
    [ConfigConstant("TrainingChallengeTimeLimit")] public partial long? TrainingChallengeTimeLimit { get; }
    [ConfigConstant("TrainingRootJob2LdId")] public partial JsonElement? TrainingRootJob2LdId { get; }
    [ConfigConstant("TrumpetWordLimit")] public partial long? TrumpetWordLimit { get; }
    [ConfigConstant("UnlockFastIncubationLv")] public partial long? UnlockFastIncubationLv { get; }
    [ConfigConstant("VariableChantPerParamA")] public partial long? VariableChantPerParamA { get; }
    [ConfigConstant("VariableChantPerParamB")] public partial long? VariableChantPerParamB { get; }
    [ConfigConstant("VariableChantPerParamC")] public partial long? VariableChantPerParamC { get; }
    [ConfigConstant("VitalityExtraLimit")] public partial long? VitalityExtraLimit { get; }
    [ConfigConstant("VitalityInitCount")] public partial long? VitalityInitCount { get; }
    [ConfigConstant("WorldAuctionEnd")] public partial long? WorldAuctionEnd { get; }
    [ConfigConstant("WorldAuctionExhibit")] public partial long? WorldAuctionExhibit { get; }
    [ConfigConstant("WorldAuctionNumber")] public partial long? WorldAuctionNumber { get; }
    [ConfigConstant("WorldTreeItemId")] public partial long? WorldTreeItemId { get; }
    [ConfigConstant("WorldTreeSkillId")] public partial long? WorldTreeSkillId { get; }
}
