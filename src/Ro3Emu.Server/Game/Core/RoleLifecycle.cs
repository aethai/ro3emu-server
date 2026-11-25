using Microsoft.Extensions.Logging;
using Romsg;
using Ro3Emu.Server.Game.Ownership;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Implemented by game services that participate in a role's load, login, movement, or leave lifecycle.
/// Each service registers its ordered steps via <see cref="RegisterLifecycle"/> using constants from
/// <see cref="RoleLifecycleOrder"/>.
/// </summary>
public interface IRoleLifecycle
{
    void RegisterLifecycle(RoleLifecycleBuilder builder);
}

/// <summary>
/// Explicit ordering of every role lifecycle step across the emulator. Comments preserve the client protocol
/// dependencies that dictate the order (moved from RoleSystems).
/// </summary>
public static class RoleLifecycleOrder
{
    // ---- Load (before AccountLoginSelectRoleResp) ----
    public const int LoadPower = 10; // before the first plan dataset: 5007/5025/5026/5067 carry plan power
    public const int LoadCharacter = 20;
    public const int LoadCareerSpecial = 30; // before mounts: a leased mount entitles the battle slot
    public const int LoadValhalla = 40;
    public const int LoadMonsterHunt = 50;
    public const int LoadGrowthClass = 60;
    public const int LoadParagon = 70;
    public const int LoadTitles = 80;
    public const int LoadFirstHeart = 90;
    public const int LoadMounts = 100;
    public const int LoadTotems = 110;
    public const int LoadLifeJob = 120;
    public const int LoadSeasons = 130;
    public const int LoadActivitiesReset = 140;
    public const int LoadDailyReset = 150;
    public const int LoadWildAfk = 160;
    public const int LoadDaily = 170;
    public const int LoadEmblems = 180;
    public const int LoadSeasonTalents = 190;
    public const int LoadSeasonRefine = 200;
    public const int LoadTavern = 210; // before the login's quest settlement: today's Tavern stay (106010)
    public const int LoadPowerPrime = 220; // the login 5066 carries the computed power (pets join at the login publish)
    public const int LoadCharacterRefresh = 230; // first entity/stats use this role's saved active talent plan

    // ---- Login (client sync sequence, each step with optional selection checkpoint) ----
    public const int LoginChat = 10;
    public const int LoginSeasons = 20; // 5021 early: multiverse lookups read its playBookId
    public const int LoginCharacterSync = 30;
    public const int LoginSkillsLoad = 40; // after character/cart sync, before login rewards can call skill sync under StateLock
    public const int LoginLifeJob = 50; // 5018 vigor then 5010 before To-Do/tasks/functions consume life state
    public const int LoginSeasonPage = 60; // 5022 before the Season/honor panels can open
    public const int LoginSeasonRefine = 70; // 5023 after 5007, before function unlocks
    public const int LoginWardrobe = 80;
    public const int LoginMounts = 90;
    public const int LoginCareerSpecial = 100; // 5002: the equipment window reads SpecialPropData unguarded
    public const int LoginTotems = 110;
    public const int LoginWildAfk = 120; // 5035 must precede To-Do/scene loading: LC_OnHookHelper calls Mathf.Max on its remaining seconds
    public const int LoginCharacterJobExp = 130; // before the quests read the role's job level
    public const int LoginTasks = 140; // 5027 before 5056: quest-gated functions (condition 4004)
    public const int LoginDaily = 150; // 5004 and 5059 before 5056 opens To-Do / Dodo
    public const int LoginWildAfkReconcile = 160; // finish any held old-day award before exposing today's allowance
    public const int LoginSeasonTalents = 170; // TaskService may return without quest config; 5050 still has safe nonempty maps
    public const int LoginGuides = 180;
    public const int LoginEmoji = 190; // 5006: the emote panel enables only unlocked emotes
    public const int LoginPersonal = 200; // 5016 before 5056 opens Profile (function 63)
    public const int LoginSocialOutfit = 210; // 5044: the own profile card reads the worn title and honor
    public const int LoginCards = 220; // after 5009: inserted cards are its storagePack
    public const int LoginTower = 230; // 5065 before tower-dependent functions and shop conditions
    public const int LoginDungeons = 240; // 5062 before opening the Dungeon menu
    public const int LoginValhalla = 250; // 5046 after 5062: cleared layers are dungeon records
    public const int LoginFlyingDragon = 260; // 82974 while a Starshatter ritual runs: the top button shows it
    public const int LoginMonsterHunt = 270; // 5053
    public const int LoginEmblems = 280; // after 5009: plan GUIDs refer to item instances
    public const int LoginShops = 290; // after 5066: purchase replies read PlayerBasicInfo.id
    public const int LoginTrade = 300; // 5033 before 5056 makes the trade UI reachable
    public const int LoginStalls = 310; // 5058 is metadata; stock remains in 33408/33411
    public const int LoginGuilds = 320; // 5047 and, for members only, 32213 before 5056 opens guild UI
    public const int LoginAdventureGroups = 330; // 5068 and, for members, 34011 before 5056 opens the Adv. Guild
    public const int LoginGuildChallenge = 340;
    public const int LoginGuildCaravan = 350; // role caravan counters in 5047 before the activity card opens
    public const int LoginTerritoryWars = 360;
    public const int LoginTerritoryEscort = 370;
    public const int LoginPronteraDefense = 380;
    public const int LoginNewActivities = 390; // 5013/5042/5054 before gated activity panels
    public const int LoginDungeonJourneyPending = 400;
    public const int LoginTimedEvents = 410;
    public const int LoginTimedBattles = 420;
    public const int LoginCasualBattles = 430; // Mock Battle payouts left pending, on the timed-battle state just loaded
    public const int LoginFestival = 440;
    public const int LoginTraining = 450;
    public const int LoginAuctions = 460; // 5063 personal state; 82000/82005 supply actual lists
    public const int LoginPets = 470; // 5017 before 5056: opening a function must not expose an uninitialized pet DAL
    public const int LoginGenre = 480;
    public const int LoginPower = 490; // 31502/31503/5066 once every power owner (pets, Build) is loaded
    public const int LoginTasksRefresh = 500; // all owner snapshots are now available for passive objectives; settles with one full 5027
    public const int LoginParagon = 510; // 5015 once the Astral Level is open (base level 99)
    public const int LoginTitles = 520; // 5031 in full once; a rank claimed later travels as an increment
    public const int LoginGrowthClass = 530; // 5045 after the login's quest settlement: it reads the delivered growth quests
    public const int LoginAchievements = 540; // one full 5019 after every owner snapshot its conditions read; function 21 opens at level 1
    public const int LoginFirstHeart = 550; // 5049 after 5019: its login-day tasks read the achievement count
    public const int LoginFunctionsSync = 560;
    public const int LoginLottery = 570;
    public const int LoginMail = 580; // offline deliveries invalidate the mail categories after role selection
    public const int LoginMvpRecover = 590; // frozen MVP outcomes survive commit-before-delivery and restart
    public const int LoginSkillsSync = 600; // after 5066: skill points need PlayerBasicInfo.career
    public const int LoginBuffEnergy = 610;
    public const int LoginWorldCore = 620;
    public const int LoginTavern = 630; // 5043: a role still in a room (a relog) rejoins its chat and voice
    public const int LoginMountVouchers = 640; // posted: runs once the login has finished

    // ---- LoginKeys ----
    public const int LoginKeysDefense = 10;
    public const int LoginKeysActivities = 20;
    public const int LoginKeysTerritory = 30;
    public const int LoginKeysCaravan = 40;
    public const int LoginKeysGuildChallenge = 50;
    public const int LoginKeysMvp = 60;
    public const int LoginKeysDungeons = 70;
    public const int LoginKeysCasual = 80;
    public const int LoginKeysTimedBattles = 90;

    // ---- LeaveKeys ----
    public const int LeaveKeysGuild = 10;
    public const int LeaveKeysTimedBattles = 20;

    // ---- Move ----
    public const int MoveWorld = 10;
    public const int MoveTerritoryVehicle = 20; // a driver's walk is its vehicle's
    public const int MoveCasualMadogear = 30; // and a Mock Battle Madogear's
    public const int MoveTerritoryPlatform = 40; // a SummonPlatform activator who walks off
    public const int MoveActivitiesConch = 50;
    public const int MoveGathering = 60;
    public const int MoveCombatMoved = 70;
    public const int MoveCombatWalked = 80; // Swift Warg's dash on the next walk
    public const int MoveEmoji = 90;
    public const int MoveDungeons = 100; // scripted scene regions and points

    // ---- Leave (step names match RoleSessionService.CleanupFailureForTest) ----
    public const int LeaveScene = 10;
    public const int LeaveCombat = 20;
    public const int LeaveInteraction = 30;
    public const int LeaveGathering = 40;
    public const int LeaveLifeJob = 50;
    public const int LeaveFriends = 60;
    public const int LeavePosition = 70;
    public const int LeaveSettings = 80;
    public const int LeaveTerritory = 90;
    public const int LeaveWildAfk = 100;
    public const int LeaveDailyBoundary = 110;
    public const int LeaveDungeon = 120;
    public const int LeaveTower = 130;
    public const int LeaveGuild = 140;
    public const int LeaveActivitiesReset = 150;
    public const int LeaveTraining = 160;
    public const int LeaveFestival = 170;
    public const int LeaveDungeonMatch = 180;
    public const int LeaveTimedBattle = 190;
    public const int LeaveInstance = 200;
    public const int LeaveWorld = 210;
    public const int LeaveTavern = 220;
    public const int LeaveChat = 230;
    public const int LeaveWorldCore = 240;
}

/// <summary>Per-handover transient state shared across leave steps (e.g. AFK deferring the daily boundary).</summary>
public sealed class RoleLeaveContext(GameSession session, Action savePosition)
{
    public GameSession Session { get; } = session;
    public Action SavePosition { get; } = savePosition;
    public bool DeferDailyBoundary { get; set; }
}

public enum LeaveStepKind
{
    Essential,
    Optional,
    StateReset,
}

public sealed record RoleLoadStep(int Order, string Name, Func<GameSession, Task> Work);
public sealed record RoleLoginStep(int Order, string Name, Func<GameSession, Task> Work, bool CheckpointAfter);
public sealed record RoleLoginKeysStep(int Order, string Name, Func<long, Task<ReservationKey[]>> Keys);
public sealed record RoleLeaveKeysStep(int Order, string Name, Func<GameSession, ReservationKey[]> Keys);
public sealed record RoleMoveStep(int Order, string Name, Func<GameSession, WalkReq, Task> Work);
public sealed record RoleLeaveStep(int Order, string StepName, string Label, LeaveStepKind Kind,
    Func<RoleLeaveContext, Task> Work, Func<RoleLeaveContext, bool>? Condition = null);

/// <summary>Collects ordered role lifecycle registrations from every <see cref="IRoleLifecycle"/> service.</summary>
public sealed class RoleLifecycleBuilder
{
    internal List<RoleLoadStep> Load { get; } = [];
    internal List<RoleLoginStep> Login { get; } = [];
    internal List<RoleLoginKeysStep> LoginKeys { get; } = [];
    internal List<RoleLeaveKeysStep> LeaveKeys { get; } = [];
    internal List<RoleMoveStep> Move { get; } = [];
    internal List<RoleLeaveStep> Leave { get; } = [];

    public void OnLoad(int order, string name, Func<GameSession, Task> work) => Load.Add(new(order, name, work));
    public void OnLoad(int order, string name, Action<GameSession> work) =>
        Load.Add(new(order, name, s => { work(s); return Task.CompletedTask; }));

    public void OnLogin(int order, string name, Func<GameSession, Task> work, bool checkpointAfter = true) =>
        Login.Add(new(order, name, work, checkpointAfter));
    public void OnLogin(int order, string name, Action<GameSession> work, bool checkpointAfter = true) =>
        Login.Add(new(order, name, s => { work(s); return Task.CompletedTask; }, checkpointAfter));

    public void OnLoginKeys(int order, string name, Func<long, Task<ReservationKey[]>> keys) =>
        LoginKeys.Add(new(order, name, keys));
    public void OnLoginKeys(int order, string name, Func<long, ReservationKey[]> keys) =>
        LoginKeys.Add(new(order, name, roleId => Task.FromResult(keys(roleId))));

    public void OnLeaveKeys(int order, string name, Func<GameSession, ReservationKey[]> keys) =>
        LeaveKeys.Add(new(order, name, keys));

    public void OnMove(int order, string name, Func<GameSession, WalkReq, Task> work) =>
        Move.Add(new(order, name, work));
    public void OnMove(int order, string name, Func<GameSession, Task> work) =>
        Move.Add(new(order, name, (s, _) => work(s)));
    public void OnMove(int order, string name, Action<GameSession> work) =>
        Move.Add(new(order, name, (s, _) => { work(s); return Task.CompletedTask; }));
    public void OnMove(int order, string name, Action<GameSession, WalkReq> work) =>
        Move.Add(new(order, name, (s, w) => { work(s, w); return Task.CompletedTask; }));

    public void OnLeave(int order, string stepName, string label, Func<GameSession, Task> work,
        Func<RoleLeaveContext, bool>? condition = null, LeaveStepKind kind = LeaveStepKind.Essential) =>
        Leave.Add(new(order, stepName, label, kind, ctx => work(ctx.Session), condition));

    public void OnLeave(int order, string stepName, string label, Action<GameSession> work,
        Func<RoleLeaveContext, bool>? condition = null, LeaveStepKind kind = LeaveStepKind.Essential) =>
        Leave.Add(new(order, stepName, label, kind, ctx => { work(ctx.Session); return Task.CompletedTask; }, condition));

    public void OnLeaveContext(int order, string stepName, string label, Func<RoleLeaveContext, Task> work,
        Func<RoleLeaveContext, bool>? condition = null, LeaveStepKind kind = LeaveStepKind.Essential) =>
        Leave.Add(new(order, stepName, label, kind, work, condition));
}

/// <summary>Adapter keeping Maps.World free of a dependency on Game.</summary>
internal sealed class WorldRoleLifecycle(Maps.World world) : IRoleLifecycle
{
    public void RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnMove(RoleLifecycleOrder.MoveWorld, "World.Move", (s, w) => world.MoveAsync(s, w));
        builder.OnLeave(RoleLifecycleOrder.LeaveWorld, "world", "World.Leave", s => world.LeaveAsync(s));
    }
}

/// <summary>Adapter keeping Micro.FriendService free of a dependency on Game lifecycle orchestration.</summary>
internal sealed class FriendRoleLifecycle(Micro.FriendService friends) : IRoleLifecycle
{
    public void RegisterLifecycle(RoleLifecycleBuilder builder)
    {
        builder.OnLeave(RoleLifecycleOrder.LeaveFriends, "friends", "FriendService.OfflineNotice",
            s => s.Role is { } leaving ? friends.OnlineChangedAsync(leaving.Id, online: false) : Task.CompletedTask,
            condition: ctx => ctx.Session.Role != null,
            kind: LeaveStepKind.Optional);
    }
}
