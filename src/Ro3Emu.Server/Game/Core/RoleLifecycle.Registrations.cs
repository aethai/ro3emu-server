namespace Ro3Emu.Server.Game;

// IRoleLifecycle registrations for all Ro3Emu.Server.Game services.
// Keeping these partial declarations together beside RoleLifecycleOrder makes the entire lifecycle sequence
// auditable in one place while each service owns its own lifecycle participation via DI.

public sealed partial class ChatService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginChat, "ChatService.OnRoleEntered", OnRoleEnteredAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveChat, "chat", "ChatService.OnRoleLeft", OnRoleLeft);
    }
}

public sealed partial class CharacterService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadCharacter, "CharacterService.Load", async s => { Load(s); await FinishLoadAsync(s); });
        builder.OnLoad(RoleLifecycleOrder.LoadCharacterRefresh, "CharacterService.Refresh", Refresh);
        builder.OnLogin(RoleLifecycleOrder.LoginCharacterSync, "CharacterService.SendLoginSync", SendLoginSyncAsync);
        builder.OnLogin(RoleLifecycleOrder.LoginCharacterJobExp, "CharacterService.CatchUpJobExp", CatchUpJobExpAsync);
    }
}

public sealed partial class SkillService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginSkillsLoad, "SkillService.Load", LoadAsync);
        builder.OnLogin(RoleLifecycleOrder.LoginSkillsSync, "SkillService.Sync", SyncAsync);
    }
}

public sealed partial class FunctionService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginFunctionsSync, "FunctionService.Sync", s =>
        {
            s.Profile.DeferFunctionSync = false;
            return SyncAsync(s);
        });
    }
}

public sealed partial class CombatService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnMove(RoleLifecycleOrder.MoveCombatMoved, "CombatService.PlayerMoved", PlayerMoved);
        builder.OnMove(RoleLifecycleOrder.MoveCombatWalked, "CombatService.PlayerWalked", PlayerWalked);
        builder.OnLeave(RoleLifecycleOrder.LeaveCombat, "combat", "CombatService.ClearSkills", ClearSkillsAsync);
    }
}

public sealed partial class BuffService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginBuffEnergy, "BuffService.InitializeEnergy", InitializeEnergyAsync);
    }
}

public sealed partial class SceneService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLeave(RoleLifecycleOrder.LeaveScene, "scene", "SceneService.DisconnectLine", DisconnectLine);
    }
}

public sealed partial class TaskService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTasks, "TaskService.Login", LoginAsync);
        builder.OnLogin(RoleLifecycleOrder.LoginTasksRefresh, "TaskService.Refresh", s =>
        {
            s.Profile.DeferTaskSync = false;
            return RefreshAsync(s);
        }, checkpointAfter: false);
    }
}

public sealed partial class DailyActivityService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadDailyReset, "DailyActivityService.ResetReady", s => s.Activities.DailyReady = false);
        builder.OnLoad(RoleLifecycleOrder.LoadDaily, "DailyActivityService.Load", s => Load(s));
        builder.OnLogin(RoleLifecycleOrder.LoginDaily, "DailyActivityService.Login", s => LoginAsync(s));
        builder.OnLeave(RoleLifecycleOrder.LeaveDailyBoundary, "daily boundary", "DailyActivityService.Reconcile",
            s => ReconcileAsync(s), condition: ctx => !ctx.DeferDailyBoundary);
    }
}

public sealed partial class GuideService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginGuides, "GuideService.Login", LoginAsync);
    }
}

public sealed partial class EmojiService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginEmoji, "EmojiService.Login", LoginAsync);
        builder.OnMove(RoleLifecycleOrder.MoveEmoji, "EmojiService.PlayerMoved", PlayerMovedAsync);
    }
}

public sealed partial class PersonalService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginPersonal, "PersonalService.Login", LoginAsync, checkpointAfter: false);
    }
}

public sealed partial class SocialOutfitService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginSocialOutfit, "SocialOutfitService.Login", LoginAsync);
    }
}

public sealed partial class AchievementService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginAchievements, "AchievementService.Login", LoginAsync);
    }
}

public sealed partial class MvpService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginMvpRecover, "MvpService.Recover", RecoverAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysMvp, "MvpService.LoginKeys", LoginKeysAsync);
    }
}

public sealed partial class CardService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginCards, "CardService.Login", LoginAsync);
    }
}

public sealed partial class ShopService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginShops, "ShopService.Login", LoginAsync);
    }
}

public sealed partial class EmblemService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadEmblems, "EmblemService.Load", LoadAsync);
        builder.OnLogin(RoleLifecycleOrder.LoginEmblems, "EmblemService.Login", LoginAsync);
    }
}

public sealed partial class SeasonService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadSeasons, "SeasonService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginSeasons, "SeasonService.Login", LoginAsync);
    }
}

public sealed partial class SeasonPageService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginSeasonPage, "SeasonPageService.Login", LoginAsync);
    }
}

public sealed partial class SeasonRefineService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadSeasonRefine, "SeasonRefineService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginSeasonRefine, "SeasonRefineService.Login", LoginAsync);
    }
}

public sealed partial class SeasonTalentService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadSeasonTalents, "SeasonTalentService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginSeasonTalents, "SeasonTalentService.Sync", SyncAsync);
    }
}

public sealed partial class PetService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginPets, "PetService.Login", LoginAsync);
    }
}

public sealed partial class TowerService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTower, "TowerService.Login", LoginAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveTower, "tower", "TowerService.Abandon", AbandonAsync);
    }
}

public sealed partial class ValhallaService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadValhalla, "ValhallaService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginValhalla, "ValhallaService.Login", LoginAsync, checkpointAfter: false);
    }
}

public sealed partial class FlyingDragonService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginFlyingDragon, "FlyingDragonService.Login", LoginAsync, checkpointAfter: false);
    }
}

public sealed partial class MonsterHuntService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadMonsterHunt, "MonsterHuntService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginMonsterHunt, "MonsterHuntService.Login", LoginAsync);
    }
}

public sealed partial class GrowthClassService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadGrowthClass, "GrowthClassService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginGrowthClass, "GrowthClassService.Login", LoginAsync);
    }
}

public sealed partial class ParagonService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadParagon, "ParagonService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginParagon, "ParagonService.Login", LoginAsync, checkpointAfter: false);
    }
}

public sealed partial class TitleService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadTitles, "TitleService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginTitles, "TitleService.Login", LoginAsync, checkpointAfter: false);
    }
}

public sealed partial class FirstHeartService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadFirstHeart, "FirstHeartService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginFirstHeart, "FirstHeartService.Login", LoginAsync);
    }
}

public sealed partial class MailService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginMail, "MailService.Login", LoginAsync, checkpointAfter: false);
    }
}

public sealed partial class AuctionService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginAuctions, "AuctionService.Login", LoginAsync);
    }
}

public sealed partial class GuildService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginGuilds, "GuildService.Login", LoginAsync);
        builder.OnLeaveKeys(RoleLifecycleOrder.LeaveKeysGuild, "GuildService.OwnGuildKeys", OwnGuildKeys);
        builder.OnLeave(RoleLifecycleOrder.LeaveGuild, "guild", "GuildService.OfflineNotice",
            s => OnlineChangedAsync(s, false), condition: ctx => ctx.Session.GuildOnlinePublished);
    }
}

public sealed partial class AdventureGroupService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginAdventureGroups, "AdventureGroupService.Login", LoginAsync);
    }
}

public sealed partial class GuildCaravanService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginGuildCaravan, "GuildCaravanService.Login", LoginAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysCaravan, "GuildCaravanService.LoginKeys", LoginKeys);
    }
}

public sealed partial class GuildChallengeService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginGuildChallenge, "GuildChallengeService.Login", LoginAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysGuildChallenge, "GuildChallengeService.LoginKeys", LoginKeys);
    }
}

public sealed partial class DungeonService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginDungeons, "DungeonService.Login", LoginAsync, checkpointAfter: false);
        builder.OnLogin(RoleLifecycleOrder.LoginDungeonJourneyPending, "DungeonService.DeliverJourneyPending", DeliverJourneyPendingAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysDungeons, "DungeonService.LoginKeys", LoginKeysAsync);
        builder.OnMove(RoleLifecycleOrder.MoveDungeons, "DungeonService.PlayerMoved", PlayerMovedAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveDungeon, "dungeon", "DungeonService.Abandon", AbandonAsync);
    }
}

public sealed partial class DungeonMatchService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLeave(RoleLifecycleOrder.LeaveDungeonMatch, "dungeon match", "DungeonMatchService.LeaveRole", async s =>
        {
            await LeaveRoleAsync(s);
            s.Instances.DungeonSoloMatch = false;
            s.Profile.GmAllFunctionsOpen = false;
        });
    }
}

public sealed partial class InstanceService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLeave(RoleLifecycleOrder.LeaveInstance, "instance", "InstanceService.Abandon", AbandonAsync);
    }
}

public sealed partial class TradeService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTrade, "TradeService.Login", LoginAsync);
    }
}

public sealed partial class StallsService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginStalls, "StallsService.Login", LoginAsync);
    }
}

public sealed partial class LifeJobService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadLifeJob, "LifeJobService.Load", LoadAsync);
        builder.OnLogin(RoleLifecycleOrder.LoginLifeJob, "LifeJobService.Login", LoginAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveLifeJob, "life", "LifeJobService.Cancel", CancelAsync);
    }
}

public sealed partial class GatheringService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnMove(RoleLifecycleOrder.MoveGathering, "GatheringService.PlayerMoved", PlayerMovedAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveGathering, "gathering", "GatheringService.Interrupt", s => InterruptAsync(s, notify: false));
    }
}

public sealed partial class WildAfkService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadWildAfk, "WildAfkService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginWildAfk, "WildAfkService.Login", LoginAsync);
        builder.OnLogin(RoleLifecycleOrder.LoginWildAfkReconcile, "WildAfkService.Reconcile", ReconcileAsync);
        builder.OnLeaveContext(RoleLifecycleOrder.LeaveWildAfk, "afk", "WildAfkService.Leave", ctx =>
        {
            var s = ctx.Session;
            // AFK.Leave detaches its session state. A held old-day award still needs the old
            // daily/task rows on the next login, so the ordinary daily reset must wait for it.
            ctx.DeferDailyBoundary = PendingDailyAward(s.Activities.WildAfk, s.Activities.Daily, s.Tasks, s.Activities.DailyRows);
            return LeaveAsync(s);
        });
    }
}

public sealed partial class WorldCoreService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginWorldCore, "WorldCoreService.Login", LoginAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveWorldCore, "world core", "WorldCoreService.Disconnected", Disconnected);
    }
}

public sealed partial class TavernService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadTavern, "TavernService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginTavern, "TavernService.Login", LoginAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveTavern, "tavern", "TavernService.LeaveRole", LeaveRoleAsync);
    }
}

public sealed partial class WorldInteractionService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLeave(RoleLifecycleOrder.LeaveInteraction, "interaction", "WorldInteractionService.Disconnect", DisconnectAsync);
    }
}

public sealed partial class NewActivityService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadActivitiesReset, "NewActivityService.ResetReady", s =>
        {
            s.Activities.ActivitiesReady = false;
            s.Activities.NewActivities = null;
        });
        builder.OnLogin(RoleLifecycleOrder.LoginNewActivities, "NewActivityService.Login", LoginAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysActivities, "NewActivityService.LoginKeys", LoginKeys);
        builder.OnMove(RoleLifecycleOrder.MoveActivitiesConch, "NewActivityService.ConchMoved", ConchMovedAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveActivitiesReset, "activities-reset", "NewActivityService.ResetReady",
            s => s.Activities.ActivitiesReady = false, kind: LeaveStepKind.StateReset);
    }
}

public sealed partial class TimedEventService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTimedEvents, "TimedEventService.Login", LoginAsync);
    }
}

public sealed partial class PronteraDefenseService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginPronteraDefense, "PronteraDefenseService.Login", LoginAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysDefense, "PronteraDefenseService.LoginKeys", LoginKeys);
    }
}

public sealed partial class TimedBattleService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTimedBattles, "TimedBattleService.Login", LoginAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysTimedBattles, "TimedBattleService.LoginKeys", LoginKeysAsync);
        builder.OnLeaveKeys(RoleLifecycleOrder.LeaveKeysTimedBattles, "TimedBattleService.CleanupKeys", CleanupKeys);
        builder.OnLeave(RoleLifecycleOrder.LeaveTimedBattle, "timed battle", "TimedBattleService.LeaveRole", async s =>
        {
            await LeaveRoleAsync(s);
            s.Instances.EventFaction = 0;
        });
    }
}

public sealed partial class CasualBattleService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginCasualBattles, "CasualBattleService.Login", LoginAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysCasual, "CasualBattleService.LoginKeys", LoginKeysAsync);
        builder.OnMove(RoleLifecycleOrder.MoveCasualMadogear, "CasualBattleService.MadogearMoved", MadogearMovedAsync);
    }
}

public sealed partial class FestivalService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginFestival, "FestivalService.Login", LoginAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveFestival, "festival", "FestivalService.LeaveRole", LeaveRoleAsync);
    }
}

public sealed partial class TrainingService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTraining, "TrainingService.Login", LoginAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveTraining, "training", "TrainingService.LeaveRole", LeaveRoleAsync);
    }
}

public sealed partial class LotteryService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginLottery, "LotteryService.Login", LoginAsync);
    }
}

public sealed partial class MountService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadMounts, "MountService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginMounts, "MountService.Login", LoginAsync);
        builder.OnLogin(RoleLifecycleOrder.LoginMountVouchers, "MountService.UseHeldVouchers", UseHeldVouchers, checkpointAfter: false);
    }
}

public sealed partial class CareerSpecialService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadCareerSpecial, "CareerSpecialService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginCareerSpecial, "CareerSpecialService.Login", LoginAsync);
    }
}

public sealed partial class WardrobeService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginWardrobe, "WardrobeService.Login", LoginAsync);
    }
}

public sealed partial class TotemService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadTotems, "TotemService.Load", Load);
        builder.OnLogin(RoleLifecycleOrder.LoginTotems, "TotemService.Sync", SyncAsync);
    }
}

public sealed partial class GenreService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginGenre, "GenreService.Login", LoginAsync, checkpointAfter: false);
    }
}

public sealed partial class PowerService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLoad(RoleLifecycleOrder.LoadPower, "PowerService.Load", Load);
        builder.OnLoad(RoleLifecycleOrder.LoadPowerPrime, "PowerService.Prime", Prime);
        builder.OnLogin(RoleLifecycleOrder.LoginPower, "PowerService.Login", LoginAsync);
    }
}

public sealed partial class TerritoryWarsService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTerritoryWars, "TerritoryWarsService.Login", LoginAsync);
        builder.OnLoginKeys(RoleLifecycleOrder.LoginKeysTerritory, "TerritoryWarsService.LoginKeys", _ => LoginKeys());
        builder.OnMove(RoleLifecycleOrder.MoveTerritoryVehicle, "TerritoryWarsService.VehicleMoved", VehicleMovedAsync);
        builder.OnMove(RoleLifecycleOrder.MoveTerritoryPlatform, "TerritoryWarsService.PlatformMoved", PlatformMovedAsync);
        builder.OnLeave(RoleLifecycleOrder.LeaveTerritory, "territory", "TerritoryWarsService.Disconnect", DisconnectAsync);
    }
}

public sealed partial class TerritoryEscortService : IRoleLifecycle
{
    void IRoleLifecycle.RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLogin(RoleLifecycleOrder.LoginTerritoryEscort, "TerritoryEscortService.Login", LoginAsync);
    }
}
