using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>Core role routes: bag, stats, skills, combat cast, NPC interaction, emotes, achievements, and guides.</summary>
public sealed class CharacterMessages(
    CharacterService character,
    SkillService skills,
    CombatService combat,
    TaskService tasks,
    GuideService guides,
    EmojiService emoji,
    AchievementService achievements,
    GatheringService gathering,
    TimedBattleService timedBattles,
    NewActivityService activities,
    IEnumerable<INpcInteractionHandler> npcHandlers) : IMessageModule
{
    private readonly INpcInteractionHandler[] _npcHandlers = [.. npcHandlers.OrderBy(h => h.Order)];

    public void Map(MessageRoutes routes)
    {
        MapCharacter(routes);
        MapSkillsAndCombat(routes);
        MapEmojiAndGuides(routes);
    }

    private void MapCharacter(MessageRoutes routes)
    {
        routes.Map<QuickUseSaveReq>(CharacterService.QuickUseSave, "character", RouteAdmission.Role, character.QuickUseSaveAsync);
        routes.Map<MedicinesAutoUseSetReq>(CharacterService.MedicinesAutoUseSet, "character", RouteAdmission.Role, character.MedicinesAutoUseSetAsync);
        routes.Map<ItemArrowSwitchReq>(CharacterService.ArrowSwitch, "character", RouteAdmission.Role, character.ArrowSwitchAsync);
        routes.Map<CareerSpecialTransferItemsReq>(CharacterService.CareerSpecialTransfer, "character", RouteAdmission.Role, character.CareerSpecialTransferAsync);
        routes.Map<ShortcutBarSaveReq>(CharacterService.ShortcutBarSave, "character", RouteAdmission.Role, character.ShortcutBarSaveAsync);
        routes.Map<ShortcutBarUseReq>(CharacterService.ShortcutBarUse, "character", RouteAdmission.Role, character.ShortcutBarUseAsync);
        routes.Map<PlanAttrPointChangeReq>(CharacterService.ChangeStatPlan, "character", RouteAdmission.Role, character.ChangeStatPlanAsync);
        routes.Map<PlanAttrPointNameChangeReq>(CharacterService.RenameStatPlan, "character", RouteAdmission.Role, character.RenameStatPlanAsync);
        routes.Map<EquipSwitchPlanReq>(CharacterService.EquipSwitchPlan, "character", RouteAdmission.Role, character.SwitchEquipmentPlanAsync);
        routes.Map<EquipChangePlanNameReq>(CharacterService.EquipChangePlanName, "character", RouteAdmission.Role, character.RenameEquipmentPlanAsync);
        routes.Map<AllocateAttrPointReq>(CharacterService.AllocateAttrPoints, "character", RouteAdmission.Role, character.AllocateAsync);
        routes.Map<ResetAttrPointReq>(CharacterService.ResetAttrPoints, "character", RouteAdmission.Role, character.ResetStatsAsync);
        routes.Map<EquipItemOprReq>(CharacterService.EquipItemOpr, "character", RouteAdmission.Role, character.EquipAsync);
        // An item whose use action changes a guild or Adventure Group record reserves it in its own item-use scope.
        routes.Map<ItemUseReq>(CharacterService.ItemUse, "character", RouteAdmission.Role, character.UseItemAsync,
            economicWhen: (session, _, body) => character.ItemUseWidens(session, body));
        routes.Map<ArrangeItemReq>(CharacterService.PackItems, "character", RouteAdmission.Role, character.SortAsync);
        routes.Map<PlayerTransformJobReq>(CharacterService.PlayerTransformJob, "character", RouteAdmission.Role, character.TransformJobAsync);
    }

    private void MapSkillsAndCombat(MessageRoutes routes)
    {
        routes.Map<SwitchPlanReq>(SkillService.SwitchPlan, "skills", RouteAdmission.Role, skills.SwitchPlanAsync);
        routes.Map<SkillPlanChangeNameReq>(SkillService.RenamePlan, "skills", RouteAdmission.Role, skills.RenamePlanAsync);
        routes.Map<SkillGrowthSkillPlanUpdateReq>(SkillService.PlanUpdate, "skills", RouteAdmission.Role, skills.PlanUpdateAsync);
        routes.Map<SkillLoadReq>(SkillService.SkillLoad, "skills", RouteAdmission.Role, skills.LoadAsync);
        routes.Map<SkillResetReq>(SkillService.SkillReset, "skills", RouteAdmission.Role, skills.ResetAsync);
        routes.Map<SkillQueueUpdateReq>(SkillService.QueueUpdate, "skills", RouteAdmission.Role, skills.QueueUpdateAsync);
        routes.Map<SkillSwitchGenreReq>(SkillService.SwitchGenre, "skills", RouteAdmission.Role, skills.SwitchGenreAsync);
        // The cast takes its own role admission (SkillCastingService.CastAsync) after landing the running cast's shown hit
        // outside it: a killing hit's reward reserves every contributor's role, which this request's role could not widen.
        routes.Map<CastSkillReq>(CombatService.UseSkill, "gathering", RouteAdmission.Economic, async (session, cast) =>
        {
            bool pickedUp = false;
            await AsRoleAsync(session, "dispatcher/31102", async () =>
            {
                // Starting another action cancels collection even if it never lands a combat hit (inferred).
                await activities.InterruptConchAsync(session);
                await gathering.InterruptAsync(session);
                // Dragon Coffin Island's crystals and soul are taken by their interaction skills (TimedBattleService.DragonIsland).
                pickedUp = await timedBattles.TryDragonPickupAsync(session, cast);
            });
            if (!pickedUp) await combat.CastAsync(session, cast);
        });
        routes.Map<NpcInteractionReq>(SceneService.NpcInteraction, "gathering", RouteAdmission.Role, async (session, npcInteraction) =>
        {
            foreach (var handler in _npcHandlers)
                if (await handler.TryInteractAsync(session, npcInteraction)) return;
        });
    }

    private void MapEmojiAndGuides(MessageRoutes routes)
    {
        // Emotes and sitting (EmojiService).
        routes.Map<EmojiStartPlayReq>(EmojiService.StartPlay, "emoji", RouteAdmission.Default, emoji.StartAsync);
        routes.Map(EmojiService.StopPlay, "emoji", RouteAdmission.Default, emoji.StopAsync);
        routes.Map<SetIdleEmojiReq>(EmojiService.SetIdle, "emoji", RouteAdmission.Role, emoji.SetIdleAsync);
        routes.Map<AddEmojiCollectionReq>(EmojiService.AddCollection, "emoji", RouteAdmission.Role,
            (session, req) => emoji.CollectionAsync(session, req.EmojiId, add: true));
        routes.Map<DelEmojiCollectionReq>(EmojiService.DelCollection, "emoji", RouteAdmission.Role,
            (session, req) => emoji.CollectionAsync(session, req.EmojiId, add: false));
        routes.Map<EmojiInviteReq>(EmojiService.Invite, "emoji", RouteAdmission.Default, emoji.InviteAsync);
        routes.Map<EmojiInviteAcceptReq>(EmojiService.Accept, "emoji", RouteAdmission.Default, emoji.AcceptAsync);
        routes.Map<EmojiInviteRefuseReq>(EmojiService.Refuse, "emoji", RouteAdmission.Default, emoji.RefuseAsync);
        // The Survivor emote wheel (43304 edits it, 83304 plays one of it in the match).
        routes.Map<DungeonEmojiInstallReq>(EmojiService.DungeonEmojiInstall, "emoji", RouteAdmission.Default, emoji.InstallDungeonEmojiAsync);
        routes.Map<DungeonModuleEmojiPlayReq>(TimedBattleService.EmojiPlay, "timedBattles", RouteAdmission.Default,
            (session, req) => timedBattles.PlayEmojiAsync(session, req.EmojiId,
                id => emoji.Wheel(session).Contains(id) && emoji.WheelEmoji(session, id)));
        // Achievements / Trophy (AchievementService). 76004 share has no client sender: unrouted.
        routes.Map<GetAchievementRewardReq>(AchievementService.GetReward, "achievements", RouteAdmission.Role, achievements.ClaimAsync);
        routes.Map<GetAchievementPointRewardReq>(AchievementService.GetPointReward, "achievements", RouteAdmission.Role, achievements.ClaimTierAsync);
        routes.Map(AchievementService.UpdateHintLevel, "achievements", RouteAdmission.Role, achievements.HintSeenAsync);
        routes.Map<GetAchievementAllRewardReq>(AchievementService.GetAllReward, "achievements", RouteAdmission.Role, achievements.ClaimAllAsync);
        routes.Map<UpdateGuideReq>(GuideService.UpdateGuide, "guides", RouteAdmission.Default, guides.UpdateAsync);
        routes.Map<ClientCompleteGuideReq>(GuideService.CompleteGuide, "guides", RouteAdmission.Default, async (session, guideReq) =>
        {
            await guides.CompleteAsync(session, guideReq);
            await tasks.OnGuideCompleteAsync(session, guideReq.GuildId);
        });
    }

    /// <summary>Part of a request that keeps the role admission the route used to hold for all of it.</summary>
    private static Task AsRoleAsync(GameSession session, string origin, Func<Task> work) =>
        session.Owners is { } owners && session.Role is { } role
            ? owners.RunSessionAsync(origin, [Ownership.ReservationKey.Role(role.Id)], session, work)
            : work();
}
