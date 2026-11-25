using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>Gear and character progression routes: equipment, appraisal, enchants, cards, totems, seasons, paragon, titles, and growth classroom.</summary>
public sealed class CharacterGearMessages(
    EquipmentService equipment,
    AppraisalService appraisal,
    EnchantService enchants,
    CardService cards,
    TotemService totems,
    SeasonPageService seasonPage,
    SeasonRefineService seasonRefine,
    SeasonTalentService talents,
    ParagonService paragon,
    TitleService titles,
    GrowthClassService growthClass) : IMessageModule
{
    public void Map(MessageRoutes routes)
    {
        MapEquipmentAndCards(routes);
        MapTotems(routes);
        MapSeasonAndProgression(routes);
    }

    private void MapEquipmentAndCards(MessageRoutes routes)
    {
        routes.Map<ItemLockDecomposeReq>(EquipmentService.ItemLockDecompose, "equipment", RouteAdmission.Role, equipment.LockDecomposeAsync);
        routes.Map<ItemAutoDecomposeSettingReq>(EquipmentService.ItemAutoDecomposeSetting, "equipment", RouteAdmission.Role, equipment.AutoDecomposeSettingAsync);
        routes.Map<EquipAppraiseReq>(AppraisalService.Appraise, "appraisal", RouteAdmission.Role, appraisal.AppraiseAsync);
        routes.Map<EnchantReq>(EnchantService.Enchant, "enchants", RouteAdmission.Role, enchants.EnchantAsync);
        routes.Map<StrengthenEquipmentReq>(EquipmentService.Strengthen, "equipment", RouteAdmission.Role, equipment.StrengthenAsync);
        routes.Map<RefineEquipmentReq>(EquipmentService.Refine, "equipment", RouteAdmission.Role, equipment.RefineAsync);
        routes.Map<EquipCraftReq>(EquipmentService.Craft, "equipment", RouteAdmission.Role, equipment.CraftAsync);
        routes.Map<ItemCombineReq>(EquipmentService.ItemCombine, "equipment", RouteAdmission.Role, equipment.CombineAsync);
        routes.Map<ItemDecomposeReq>(EquipmentService.ItemDecompose, "equipment", RouteAdmission.Role, equipment.DecomposeAsync);
        routes.Map<EquipmentCardInsertReq>(CardService.SlotInsert, "cards", RouteAdmission.Role, cards.InsertAsync);
        routes.Map<EquipmentCardRemoveReq>(CardService.SlotRemove, "cards", RouteAdmission.Role, cards.RemoveAsync);
        routes.Map<EquipmentCardTransferReq>(CardService.SlotTransfer, "cards", RouteAdmission.Role, cards.TransferAsync);
        routes.Map<CardSwitchPlanReq>(CardService.SwitchPlan, "cards", RouteAdmission.Role, cards.SwitchPlanAsync);
        routes.Map<StorageholeChangeNameReq>(CardService.ChangePlanName, "cards", RouteAdmission.Role, cards.ChangeNameAsync);
        routes.Map<CardLevelUpReq>(CardService.LevelUp, "cards", RouteAdmission.Role, cards.LevelUpAsync);
        routes.Map<CardStarUpReq>(CardService.StarUp, "cards", RouteAdmission.Role, cards.StarUpAsync);
        routes.Map<CardRevertReq>(CardService.Revert, "cards", RouteAdmission.Role, cards.RevertAsync);
        routes.Map<CardTreasuredReq>(CardService.Treasured, "cards", RouteAdmission.Role, cards.TreasuredAsync);
        routes.Map<LibraryCardUnlockReq>(CardService.LibraryCardUnlock, "cards", RouteAdmission.Role, cards.UnlockAsync);
        routes.Map<LibrarySwitchThemeReq>(CardService.LibrarySwitchTheme, "cards", RouteAdmission.Role, cards.SwitchThemeAsync);
        // The Codex monster unlock pays its Collection Points.
        routes.Map<LibraryMonsterUnlockReq>(CardService.LibraryMonsterUnlock, "cards", RouteAdmission.Economic, cards.UnlockMonsterAsync);
    }

    private void MapTotems(MessageRoutes routes)
    {
        routes.Map<SetUpTotemReq>(TotemService.Setup, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.SetupAsync(session, req)));
        routes.Map<RefineTotemAttrReq>(TotemService.Refine, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.RefineAsync(session, req)));
        routes.Map<RefineTotemSkillReq>(TotemService.Learn, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.LearnAsync(session, req)));
        routes.Map<UpgradeTotemSystemReq>(TotemService.Upgrade, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.UpgradeAsync(session, req)));
        routes.Map<CombineTotemReq>(TotemService.Combine, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.CombineAsync(session, req)));
        routes.Map<TotemSwitchPlanReq>(TotemService.SwitchPlan, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.SwitchAsync(session, req)));
        routes.Map<TotemChangeTotemNameReq>(TotemService.Rename, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.RenameAsync(session, req)));
        routes.Map<TotemSwapReq>(TotemService.Swap, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.SwapAsync(session, req)));
        routes.Map<TotemAppraiseReq>(TotemService.Appraise, "totems", RouteAdmission.Role,
            (session, req) => totems.RunOwnedAsync(session, () => totems.AppraiseAsync(session, req)));
    }

    private void MapSeasonAndProgression(MessageRoutes routes)
    {
        routes.Map(SeasonPageService.GetData, "seasonPage", RouteAdmission.Role, seasonPage.GetDataAsync);
        routes.Map<SeasonPageReceiveRankRewardReq>(SeasonPageService.ReceiveRankReward, "seasonPage", RouteAdmission.Role, seasonPage.ClaimAsync);
        routes.Map<SeasonPagePutOnHonorBadgeReq>(SeasonPageService.PutOnHonorBadge, "seasonPage", RouteAdmission.Role, seasonPage.WearAsync);
        routes.Map<SeasonRefineRecastReq>(SeasonRefineService.Recast, "seasonRefine", RouteAdmission.Role, seasonRefine.RecastAsync);
        routes.Map<SeasonRefineSelectAttrReq>(SeasonRefineService.SelectAttr, "seasonRefine", RouteAdmission.Role, seasonRefine.SelectAttrAsync);
        routes.Map<SeasonRefineGiveUpReq>(SeasonRefineService.GiveUp, "seasonRefine", RouteAdmission.Role, seasonRefine.GiveUpAsync);
        routes.Map<SeasonTalentUpgradeReq>(SeasonTalentService.Upgrade, "talents", RouteAdmission.Role, talents.UpgradeAsync);
        routes.Map<SeasonTalentSetCurPlanReq>(SeasonTalentService.SetPlan, "talents", RouteAdmission.Role, talents.SetPlanAsync);
        routes.Map<SeasonTalentSetPlanNameReq>(SeasonTalentService.RenamePlan, "talents", RouteAdmission.Role, talents.RenameAsync);
        routes.Map(TitleService.Level, "titles", RouteAdmission.Role, titles.LevelAsync);
        routes.Map<ParagonLevelAllocatePointReq>(ParagonService.Allocate, "paragon", RouteAdmission.Role, paragon.AllocateAsync);
        routes.Map<ParagonLevelRuneInstallReq>(ParagonService.Install, "paragon", RouteAdmission.Role, paragon.InstallAsync);
        routes.Map<ParagonLevelRuneRemoveReq>(ParagonService.Remove, "paragon", RouteAdmission.Role, paragon.RemoveAsync);
        routes.Map(ParagonService.ResetAll, "paragon", RouteAdmission.Role, paragon.ResetAllAsync);
        routes.Map<ParagonLevelResetSingleReq>(ParagonService.ResetSingle, "paragon", RouteAdmission.Role, paragon.ResetSingleAsync);
        // Growth Classroom rank claims its reward via economic admission.
        routes.Map<GrowthClassGetRankRewardReq>(GrowthClassService.GetRankReward, "growthClass", RouteAdmission.Economic, growthClass.ClaimAsync);
    }
}
