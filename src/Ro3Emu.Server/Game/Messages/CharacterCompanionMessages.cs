using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>Companions, appearance, and life-job routes: wardrobe, mounts, career special, pets, life jobs, and gathering.</summary>
public sealed class CharacterCompanionMessages(
    PetService pets,
    MountService mounts,
    CareerSpecialService careerSpecial,
    WardrobeService wardrobe,
    LifeJobService life,
    GatheringService gathering) : IMessageModule
{
    public void Map(MessageRoutes routes)
    {
        routes.Intercept("life", id => id is >= LifeJobService.SwitchLife and <= GatheringService.MiniGame, async (session, msgId, _) =>
        {
            // Life job requests before the role's life state is loaded are refused, not routed.
            if (session.Role != null && session.Progress != null && session.LifeJob != null && session.Restrictions != null) return false;
            await life.RejectUnloadedAsync(session, msgId);
            return true;
        });
        MapWardrobeAndMounts(routes);
        MapLifeAndGathering(routes);
        MapPets(routes);
    }

    private void MapWardrobeAndMounts(MessageRoutes routes)
    {
        routes.Map<WardRobeUseAppearanceReq>(WardrobeService.Use, "wardrobe", RouteAdmission.Role, wardrobe.UseAsync);
        routes.Map<WardRobeUseAppearanceAttrReq>(WardrobeService.UseAttr, "wardrobe", RouteAdmission.Role, wardrobe.UseAttrAsync);
        routes.Map<WardRobeSaveAppearancePlanReq>(WardrobeService.SavePlan, "wardrobe", RouteAdmission.Role, wardrobe.SavePlanAsync);
        routes.Map<WardRobeApplyAppearancePlanReq>(WardrobeService.ApplyPlan, "wardrobe", RouteAdmission.Role, wardrobe.ApplyPlanAsync);
        routes.Map<WardRobeSetLikeReq>(WardrobeService.SetLike, "wardrobe", RouteAdmission.Role, wardrobe.SetLikeAsync);
        routes.Map<WardRobeRedeemUnlockAppearanceReq>(WardrobeService.Redeem, "wardrobe", RouteAdmission.Role, wardrobe.RedeemAsync);
        routes.Map<PlayerMountsSetCfgReq>(MountService.SetConfig, "mounts", RouteAdmission.Role, mounts.SetAsync);
        routes.Map<PlayerRiderMountReq>(MountService.Ride, "mounts", RouteAdmission.Role, mounts.RideAsync);
        routes.Map<PlayerGetMountScoreAwardReq>(MountService.ScoreAward, "mounts", RouteAdmission.Role, mounts.ClaimScoreAsync);
        routes.Map<MountEtchantRefreshReq>(MountService.EtchantRefresh, "mounts", RouteAdmission.Role, mounts.RefreshEtchantAsync);
        routes.Map<MountEtchantSaveReq>(MountService.EtchantSave, "mounts", RouteAdmission.Role, mounts.SaveEtchantAsync);
        routes.Map<MountSetEtchantPlanReq>(MountService.EtchantPlan, "mounts", RouteAdmission.Role, mounts.SetEtchantPlanAsync);
        // Special Class Items (CareerSpecialService): lease / cancel from the Profession panel, function 16.
        routes.Map<LeaseCareerSpecialItemReq>(CareerSpecialService.Lease, "mounts", RouteAdmission.Role, careerSpecial.LeaseAsync);
        routes.Map<CancelLeaseCareerSpecialItemReq>(CareerSpecialService.CancelLease, "mounts", RouteAdmission.Role, careerSpecial.CancelAsync);
    }

    private void MapLifeAndGathering(MessageRoutes routes)
    {
        routes.Map<SwitchLifeJobReq>(LifeJobService.SwitchLife, "life", RouteAdmission.Role, life.SwitchAsync);
        routes.Map<ChooseTitlePointReq>(LifeJobService.ChooseTitle, "life", RouteAdmission.Role, (s, req) => life.ChooseTitleAsync(s, req, false));
        routes.Map<ChooseTitlePointReq>(LifeJobService.ChooseTitleEx, "life", RouteAdmission.Role, (s, req) => life.ChooseTitleAsync(s, req, true));
        routes.Map<RefreshTitlePointReq>(LifeJobService.RefreshTitle, "life", RouteAdmission.Role, (s, req) => life.RefreshTitleAsync(s, req.LifejobId, req.Level, false));
        routes.Map<RefreshTitlePointExReq>(LifeJobService.RefreshTitleEx, "life", RouteAdmission.Role, (s, req) => life.RefreshTitleAsync(s, req.LifejobId, req.Level, true));
        routes.Map<StartForgeReq>(LifeJobService.StartForge, "life", RouteAdmission.Role, life.ForgeAsync);
        routes.Map<UpGradeJobTileReq>(LifeJobService.UpgradeTitle, "life", RouteAdmission.Role, life.UpgradeTitleAsync);
        routes.Map<StudyRecipeReq>(LifeJobService.StudyRecipe, "life", RouteAdmission.Role, life.LearnRecipeAsync);
        routes.Map<StudyCollectionReq>(LifeJobService.StudyCollection, "life", RouteAdmission.Role, life.LearnCollectionAsync);
        routes.Map<StudyCollectionDropReq>(LifeJobService.StudyDrop, "life", RouteAdmission.Role, life.LearnDropAsync);
        routes.Map<GetForgeCostTimeReq>(LifeJobService.ForgeTime, "life", RouteAdmission.Role, life.ForgeTimeAsync);
        routes.Map(LifeJobService.HealthAll, "life", RouteAdmission.Role, session => life.HealthAsync(session));
        routes.Map<GetHealthCostReq>(LifeJobService.HealthOne, "life", RouteAdmission.Role, (s, req) => life.HealthAsync(s, req));
        routes.Map<FinishLifeJobGameReq>(GatheringService.MiniGame, "gathering", RouteAdmission.Economic, gathering.FinishMiniGameAsync);
    }

    private void MapPets(MessageRoutes routes)
    {
        routes.Map<PetCombineReq>(PetService.Combine, "pets", RouteAdmission.Role, pets.CombineAsync);
        routes.Map<PetLvUpReq>(PetService.LevelUp, "pets", RouteAdmission.Role, pets.LevelAsync);
        routes.Map<PetProgressUpReq>(PetService.ProgressUp, "pets", RouteAdmission.Role, (s, req) => pets.AdvanceAsync(s, req.PetId, star: false));
        routes.Map<PetStarUpReq>(PetService.StarUp, "pets", RouteAdmission.Role, (s, req) => pets.AdvanceAsync(s, req.PetId, star: true));
        routes.Map<PetStateUpdateReq>(PetService.StateUpdate, "pets", RouteAdmission.Role, pets.LineupAsync);
        routes.Map<PetSwitchPlanReq>(PetService.SwitchPlan, "pets", RouteAdmission.Role, pets.SwitchAsync);
        routes.Map<PetChangePlanNameReq>(PetService.ChangePlanName, "pets", RouteAdmission.Role, pets.RenameAsync);
        routes.Map<PetCollectActiveReq>(PetService.CollectActive, "pets", RouteAdmission.Role, pets.ActivateAsync);
        routes.Map<PetFragmentsBrokenReq>(PetService.FragmentsBroken, "pets", RouteAdmission.Role, pets.DecomposeAsync);
        routes.Map<PetGenEggReq>(PetService.GenerateEgg, "pets", RouteAdmission.Role, pets.GenerateAsync);
        routes.Map<PetEggAccReq>(PetService.AccelerateEgg, "pets", RouteAdmission.Role, pets.AccelerateAsync);
        routes.Map<PetEggRewardReq>(PetService.EggReward, "pets", RouteAdmission.Role, pets.CollectAsync);
        routes.Map<PetSetSkipAnimReq>(PetService.SetSkipAnim, "pets", RouteAdmission.Role, (s, req) => pets.PreferenceAsync(s, req.Status));
        routes.Map(PetService.OpenRoom, "pets", RouteAdmission.Role, s => pets.PreferenceAsync(s, null));
        routes.Map(PetService.RoomLevelUp, "pets", RouteAdmission.Role, pets.RoomLevelAsync);
        routes.Map<FastIncubateReq>(PetService.FastEgg, "pets", RouteAdmission.Role, pets.FastAsync);
        routes.Map(PetService.Break, "pets", RouteAdmission.Role, (s, id, _) => pets.UnsupportedAsync(s, id));
        routes.Map(PetService.Unsupported33305, "pets", RouteAdmission.Role, (s, id, _) => pets.UnsupportedAsync(s, id));
        routes.Map(PetService.Unsupported33307, "pets", RouteAdmission.Role, (s, id, _) => pets.UnsupportedAsync(s, id));
        routes.Map<PetRebirthReq>(PetService.Rebirth, "pets", RouteAdmission.Role, pets.RebirthAsync);
    }
}
