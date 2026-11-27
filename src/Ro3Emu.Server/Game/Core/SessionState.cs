namespace Ro3Emu.Server.Game;

/// <summary>Combat, buff, energy, and cooldown state for one selected session.</summary>
public sealed class SessionCombatState
{
    /// <summary>The 31029 lock target.</summary>
    public long LockTarget { get; set; }
    public List<SkillObject> SkillObjects { get; } = [];
    public Dictionary<(int Group, int Mode), long> SkillModeCds { get; } = [];
    public CombatService.ProtocolState SkillProtocol { get; set; } = new();
    public long LastCombatMs { get; set; }
    /// <summary>Buff-owned portions of 2701/2702/2711; 2701 also contains CombatService's fighting bit.</summary>
    public ulong BuffStatusBits { get; set; }
    public ulong ForbiddenBits { get; set; }
    public ulong StaggerBits { get; set; }
    public HashSet<int> ForbiddenOrdinals { get; } = [];
    /// <summary>Transient configured energy meters; BuffService sends each change as 31334.</summary>
    public Dictionary<int, PlayerEnergyState> Energy { get; } = [];
    public int EnergyCareer { get; set; }
    /// <summary>EnergyConfig._iBelongMount mounts owned when the meters were last reconciled (BuffService.Energy).</summary>
    public int EnergyMounts { get; set; }
    /// <summary>31134/31135 lethal-hit choice; only an eligible active Osiris buff may create it.</summary>
    public ImmuneDeathOffer? ImmuneDeathOffer { get; set; }
    public Dictionary<long, long> BuffEffectCds { get; } = [];
    public long ImmuneRecoveryUntilMs { get; set; }
    public int ImmuneRecoveryPercent { get; set; }
    public Dictionary<int, long> SkillCds { get; } = [];
    public Dictionary<int, long> ItemCds { get; } = [];
    public Dictionary<int, long> ItemGroupCds { get; } = [];
    /// <summary>GM override added to the speed attribute (113), not saved (!speed).</summary>
    public long SpeedBonus { get; set; }
    /// <summary>31054 SKILL_SET_AUTO_BATTLE: the client's auto-battle switch.</summary>
    public bool AutoBattle { get; set; }
    /// <summary>Last successful skill-plan switch; the plans themselves use SkillPlans below.</summary>
    public DateTime SkillPlanSwitchUtc { get; set; }
    public Romsg.DBSkillData? SkillPlans { get; set; }
}

/// <summary>Character progression, equipment/stat plans, wardrobe, cards, emblems, totems, and sync state.</summary>
public sealed class SessionProfileState
{
    /// <summary>Property ids already sent in 31008; retained across local Refresh until a zero is sent.</summary>
    public HashSet<int> SentPropertyIds { get; } = [];
    /// <summary>Items still marked new in the bag (guidToNew, the red dot).</summary>
    public HashSet<long> NewItems { get; } = [];
    /// <summary>Last AUTO_SYNC version sent per message id.</summary>
    public Dictionary<uint, long> SyncVersions { get; } = [];
    /// <summary>EntityBase.equipInfo: item ids of equipment grids 1..7.</summary>
    public long[] EquipLook { get; set; } = [];
    public EquipLevels EquipLevels { get; set; } = new();
    /// <summary>5007 plan references/names/current index; instances remain exclusively in Items.</summary>
    public Romsg.DBEquipInfo EquipmentPlans { get; set; } = new();
    /// <summary>5009 autoDecomposeSetting, loaded before the first item sync.</summary>
    public Romsg.AutoDecomposeSetting AutoDecompose { get; set; } = new();
    /// <summary>Durable quick-use, medicine, ammunition and shortcut selections (5009).</summary>
    public RoleBagState BagState { get; set; } = new(null);
    /// <summary>Durable attribute allocations and names for every 5067 stat plan.</summary>
    public StatPlanState StatPlans { get; set; } = new(null);
    /// <summary>Login prerequisites (5027/5017) must arrive before quest-gated 5056, including automatic quest rewards.</summary>
    public bool DeferFunctionSync { get; set; }
    /// <summary>Role selection holds 5027 until every owner has run its login changes.</summary>
    public bool DeferTaskSync { get; set; }
    /// <summary>GM panel GmPlayerAsset openallfunc: every FunctionConfig function counts as open (this role session only).</summary>
    public bool GmAllFunctionsOpen { get; set; }
    /// <summary>Card collection (AUTO_SYNC 5057), loaded at login by CardService.</summary>
    public Romsg.DBLibraryInfo? Library { get; set; }
    /// <summary>Persisted card socket selections (AUTO_SYNC 5026).</summary>
    public Romsg.DBStorageHoleInfo? CardPlans { get; set; }
    public MountService.SavedState MountState { get; set; } = new();
    /// <summary>Leased Special Class Items (5002, role blob "career_special"); CareerSpecialService.</summary>
    public Romsg.DBCareerSpecial? CareerSpecial { get; set; }
    /// <summary>The leased items' PendantConfig ids (EntityBase.pendantInfo), derived from CareerSpecial.</summary>
    public int[] CareerPendants { get; set; } = [];
    public int CareerMountPendant { get; set; }
    /// <summary>The title rank (role blob "title", 5031); TitleService.</summary>
    public Romsg.DBTitleInfo? TitleInfo { get; set; }
    /// <summary>Astral Level stars and Star Cores (role blob "paragon", runegroup only); ParagonService.</summary>
    public Romsg.DBParagonLevel? Paragon { get; set; }
    public Romsg.DBWardRobeInfo? Wardrobe { get; set; }
    public Romsg.DBTotemInfo? Totems { get; set; }
    public string TotemRefineEpoch { get; set; } = "";
    public DateTime TotemSwitchUtc { get; set; }
    public Romsg.DBGenre? Genre { get; set; }
    /// <summary>Durable Soul Echo plans (AUTO_SYNC 5005) and attained power.</summary>
    public EmblemService.EmblemState Emblems { get; set; } = new();
    /// <summary>Current active Soul Echo plan's season score (power protocol).</summary>
    public long EmblemPower { get; set; }
    public DateTime EmblemPlanSwitchUtc { get; set; }
    /// <summary>Transient deployed pet actor; visibility is owned by World, never persisted with the roster.</summary>
    public PetFollowerActor? PetActor { get; set; }

    internal void ClearRoleLoadState()
    {
        Library = null;
    }
}

/// <summary>Season talent/page/refine and life-profession state for one selected session.</summary>
public sealed class SessionSeasonState
{
    /// <summary>Durable talent plans and oath (AUTO_SYNC 5050), loaded for the selected role.</summary>
    public Romsg.DBSeasonTalent? SeasonTalent { get; set; }
    /// <summary>Season page claims/honor (5022) and grid refinement (5023), selected-role state.</summary>
    public Romsg.DBSeasonPage? SeasonPage { get; set; }
    public Romsg.DBSeasonRefine? SeasonRefine { get; set; }
    /// <summary>Current playbook and delivered tasks permit this role's purchased talent attributes.</summary>
    public bool TalentEffectsEnabled { get; set; }
    /// <summary>Last accepted 83552 switch in this session (PublicParamConfig.SchemeCD).</summary>
    public DateTime TalentPlanSwitchUtc { get; set; }
    /// <summary>MG_Lifejob: persisted profession state and the restriction-backed vigor balance.</summary>
    public Romsg.DBLifeJobInfo? LifeJob { get; set; }
    public Romsg.DBRestrictInfo? Restrictions { get; set; }
    public LifeJobService.RuntimeState LifeRuntime { get; set; } = new();

    internal void ClearRoleLoadState()
    {
        LifeJob = null;
        Restrictions = null;
    }
}

/// <summary>Quest runtime, daily activities, seasonal events, achievements, and monster hunt state.</summary>
public sealed class SessionActivityState(TimeProvider clock)
{
    public TaskRuntimeState TaskRuntime { get; set; } = new();
    public bool InCutScene
    {
        get => TaskRuntime.InCutScene;
        set
        {
            if (value && !TaskRuntime.InCutScene) CutSceneSinceMs = clock.GetUtcNow().ToUnixTimeMilliseconds();
            TaskRuntime.InCutScene = value;
        }
    }
    /// <summary>When this login's cutscene began; 0 when one was only restored from the saved state.</summary>
    public long CutSceneSinceMs { get; private set; }
    /// <summary>Achievements / Trophy (AUTO_SYNC 5019); AchievementService.</summary>
    public Romsg.DBRoleAchievementInfo? Achievements { get; set; }
    public AchievementService.RuntimeState? AchievementRuntime { get; set; }
    /// <summary>The role's own Adventure Group values (role blob "adventure_group", AUTO_SYNC 5068).</summary>
    public Romsg.DBAdventureGroupRelationData? AdventureGroupRole { get; set; }
    /// <summary>To-Do rows and shared stage claims (5004/5059), loaded before quest login.</summary>
    public DailyActivityService.DailyState? Daily { get; set; }
    public Romsg.DBDailyActivityInfo? DailyRows { get; set; }
    public bool DailyReady { get; set; }
    /// <summary>Derived 5004 schedule refresh, independent of persisted progress/reset epochs.</summary>
    public long DailyWindowRefreshAt { get; set; }
    public long DailyWindowSampledAt { get; set; }
    /// <summary>Per-role seasonal activities (5013).</summary>
    public Romsg.DBNewActivityInfo? NewActivities { get; set; }
    public ActivityRuntimeState ActivityRuntime { get; set; } = new();
    public bool ActivitiesReady { get; set; }
    /// <summary>The client has the pass challenge rows (Ro3Emu.ClientHooks.PassChallenges sent 77014).</summary>
    public bool PassChallengeRows { get; set; }
    public long? ActivityServerDay { get; set; }
    public Romsg.DBTrainingGroundInfo? Training { get; set; }
    public Romsg.DBLotteryData? Lottery { get; set; }
    public long LotteryDay { get; set; }
    /// <summary>5035 field Bonus Time, daily consumption and drop history; transient activity never survives logout.</summary>
    public WildAfkService.State? WildAfk { get; set; }
    /// <summary>Finished tutorial guides (AUTO_SYNC 5055), loaded at login by GuideService.</summary>
    public Romsg.DBGuideInfo? Guides { get; set; }
    /// <summary>Growth Classroom claimed rank rewards (role blob "growth_class"); GrowthClassService.</summary>
    public Romsg.DBGrowthClassInfo? GrowthClass { get; set; }
    /// <summary>Seven-Day Goal tasks, score and claims (5049, role blob "first_heart"); FirstHeartService.</summary>
    public Romsg.DBFirstHeartTaskActivityData? FirstHeart { get; set; }
    /// <summary>Monster Hunt counters and claims (5053, role blob "area_concur"); MonsterHuntService.</summary>
    public Romsg.DBAreaConcurInfo? AreaConcur { get; set; }
    /// <summary>The bytes of AreaConcur as stored: a kill's staged write checks them in its transaction.</summary>
    public byte[] AreaConcurPersisted { get; set; } = [];
    /// <summary>A committed kill changed the counters; the 5053 sync follows the kill.</summary>
    public bool AreaConcurChanged { get; set; }

    internal void ClearRoleLoadState()
    {
        Daily = null;
        DailyRows = null;
        DailyReady = false;
        WildAfk = null;
        Achievements = null;
        AchievementRuntime = null;
        AdventureGroupRole = null;
        GrowthClass = null;
        NewActivities = null;
        ActivitiesReady = false;
        Training = null;
    }
}

/// <summary>Dungeon, Endless Tower, Valhalla, Territory Wars, Guild Challenge, and timed-event instance state.</summary>
public sealed class SessionInstanceState
{
    /// <summary>Ordinary dungeon progress/allowances (5062).</summary>
    public DungeonService.State? Dungeons { get; set; }
    public long DungeonSyncMinute { get; set; }
    /// <summary>GM test aid only: this role session may start a multi-dungeon match alone. Never persisted.</summary>
    public bool DungeonSoloMatch { get; set; }
    /// <summary>Spirit Tower progress (5065), including four party slots.</summary>
    public Romsg.DBClimTower? Tower { get; set; }
    public TowerService.Attempt? TowerAttempt { get; set; }
    /// <summary>Valhalla Trials Blessing statue (role blob "heroic_spirit"); ValhallaService.</summary>
    public Romsg.DBOdinStatue? OdinStatue { get; set; }
    /// <summary>The layer of the last admitted Valhalla challenge (5046 latestChallengeId).</summary>
    public long ValhallaLatest { get; set; }
    public PronteraDefenseService.State? Defense { get; set; }
    public TimedBattleService.State? TimedBattle { get; set; }
    public int EventFaction { get; set; }
    /// <summary>Territory war role counters and shared, guild-qualified battle instances.</summary>
    public TerritoryWarsService.RoleState? TerritoryWars { get; set; }
    public TerritoryWarsService.Battle? TerritoryBattle { get; set; }
    public TerritoryWarsService.SiegeRun? SiegeBattle { get; set; }
    public TerritoryEscortService.RoleState? TerritoryEscort { get; set; }
    public bool TerritoryMinimapOpen { get; set; }
    public GuildChallengeService.PersonalState? GuildChallenge { get; set; }

    internal void ClearRoleLoadState()
    {
        Dungeons = null;
        Tower = null;
        TowerAttempt = null;
        EventFaction = 0;
        TerritoryWars = null;
        TerritoryBattle = null;
        SiegeBattle = null;
        TerritoryEscort = null;
        Defense = null;
        TimedBattle = null;
    }
}

/// <summary>Social, emote, tavern, chat-group, shop, auction, trade, and stall state for one selected session.</summary>
public sealed class SessionSocialState
{
    /// <summary>Chat groups this player joined (guarded by ChatService's lock).</summary>
    public HashSet<string> ChatGroups { get; } = [];
    /// <summary>The client hook asked for 77017: it records and plays chat voice messages itself (ChatService.Voice).</summary>
    public bool ChatVoice { get; set; }
    /// <summary>Voice clips and clip fetches this player may still send now (ChatService.Voice).</summary>
    internal RateBucket? VoiceClips { get; set; }
    internal RateBucket? VoiceFetches { get; set; }
    /// <summary>Emote favourites, unlocks and idle emote (5006, role blob "emoji_play"); EmojiService.</summary>
    public Romsg.DBEmojiPlayInfo? EmojiPlay { get; set; }
    /// <summary>Chat emoticons unlocked by items (5041, role blob "emoji_info"); EmojiService.</summary>
    public Romsg.DBEmojiInfo? EmojiInfo { get; set; }
    /// <summary>The emote or sitting in progress (state 17 Prepare or 14 Play); runtime only, gone at logout.</summary>
    public EmojiService.Emote? Emote { get; set; }
    /// <summary>Last accepted emote request (PerformanceConfig._iPerformanceCD).</summary>
    public long LastEmoteRequestMs { get; set; }
    /// <summary>Tavern Star Power, active days and today's time in a room (5043, role blob "barroom"); TavernService.</summary>
    public TavernService.RoleState? Tavern { get; set; }
    /// <summary>Profile card presets and 620 item unlocks (5016, role blob "personal"); PersonalService.</summary>
    public Romsg.DBPersonalInfo? Personal { get; set; }
    /// <summary>Chat cosmetics unlocked by items and the worn choices (role blob "social_outfit"); SocialOutfitService.</summary>
    public Romsg.DBSocialOutfit? SocialOutfit { get; set; }
    public ShopService.ShopState? Shop { get; set; }
    /// <summary>5063 personal pre-buy counters and joined auction history.</summary>
    public Romsg.DBAuction? Auction { get; set; }
    /// <summary>5033 preferences and market counters; offers live in durable escrow, not this dataset.</summary>
    public Romsg.DBUserTradeInfo? Trade { get; set; }
    /// <summary>5058 world-stall metadata. The actual stock travels in 33408/33411.</summary>
    public Romsg.DBStallsInfo? Stalls { get; set; }
    public long ViewedStall { get; set; }
    public long ViewedSystemStall { get; set; }
}
