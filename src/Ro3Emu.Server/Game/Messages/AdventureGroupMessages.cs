using Romsg;
using Ro3Emu.Server.Game.Messaging;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Adventure Group 34000-34023 (AdventureGroupService). They keep the dispatcher's default Realm reservation
/// (MessageRoutes.Admit): an operation changes several roles' groups, quests and instance at once.
/// </summary>
public sealed class AdventureGroupMessages(AdventureGroupService adventureGroups) : IMessageModule
{
    public void Map(MessageRoutes routes)
    {
        const string owner = "advguild";
        routes.Map<CreateAdventureGroupReq>(AdventureGroupService.Create, owner, RouteAdmission.Default, adventureGroups.CreateAsync);
        routes.Map(AdventureGroupService.Delete, owner, RouteAdmission.Default, adventureGroups.DeleteAsync);
        routes.Map<PullHelperAdventureGroupListReq>(AdventureGroupService.HelperList, owner, RouteAdmission.Default, adventureGroups.HelperListAsync);
        routes.Map<PullHelperAdventureGroupApplyerReq>(AdventureGroupService.HelperApplicants, owner, RouteAdmission.Default, adventureGroups.HelperApplicantsAsync);
        routes.Map<HelperAdventureGroupApplyerOpeReq>(AdventureGroupService.HelperOpe, owner, RouteAdmission.Default, adventureGroups.HelperOpeAsync);
        routes.Map(AdventureGroupService.StartBrothers, owner, RouteAdmission.Default, adventureGroups.StartBrothersAsync);
        routes.Map<AdventureGroupEditReq>(AdventureGroupService.Edit, owner, RouteAdmission.Default, adventureGroups.EditAsync);
        routes.Map<PullAdventureGroupReq>(AdventureGroupService.NormalList, owner, RouteAdmission.Default, adventureGroups.NormalListAsync);
        routes.Map<NormalAdventureGroupOpeReq>(AdventureGroupService.NormalOpe, owner, RouteAdmission.Default, adventureGroups.NormalOpeAsync);
        routes.Map(AdventureGroupService.MemberData, owner, RouteAdmission.Default, adventureGroups.MemberDataAsync);
        routes.Map(AdventureGroupService.CheckBrothers, owner, RouteAdmission.Default, adventureGroups.CheckBrothersAsync);
        routes.Map(AdventureGroupService.EnterRitual, owner, RouteAdmission.Default, adventureGroups.EnterRitualAsync);
        routes.Map<SelectSaplingReq>(AdventureGroupService.SelectSapling, owner, RouteAdmission.Default, adventureGroups.SelectSaplingAsync);
        routes.Map<AdventureGroupDeclarationReq>(AdventureGroupService.Declare, owner, RouteAdmission.Default, adventureGroups.DeclareAsync);
        routes.Map(AdventureGroupService.Convene, owner, RouteAdmission.Default, adventureGroups.ConveneAsync);
        routes.Map<AdventureGroupConfirmConveneReq>(AdventureGroupService.ConfirmConvene, owner, RouteAdmission.Default, adventureGroups.ConfirmConveneAsync);
        routes.Map(AdventureGroupService.Commemorate, owner, RouteAdmission.Default, adventureGroups.CommemorateAsync);
        routes.Map<WateringSaplingReq>(AdventureGroupService.Water, owner, RouteAdmission.Default, adventureGroups.WaterAsync);
        routes.Map<GetSaplingDataReq>(AdventureGroupService.SaplingData, owner, RouteAdmission.Default, adventureGroups.SaplingDataAsync);
        routes.Map<GetAdventureGroupDetailDataReq>(AdventureGroupService.Detail, owner, RouteAdmission.Default, adventureGroups.DetailAsync);
    }
}
