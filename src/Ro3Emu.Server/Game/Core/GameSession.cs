using System.Buffers;
using Google.Protobuf;
using MessagePack;
using Phonest.Proto;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server.Game;

/// <summary>
/// One client on the game gate. Server -> client traffic is Phonest EntityMessage on gate index 2:
/// named RPCs (methodName + msgpack values concatenated without an array header) or raw game messages
/// (no methodName, parameters = uint32 LE msgId ‖ romsg protobuf).
/// Feature-specific state lives in the domain state containers (<see cref="Combat"/>, <see cref="Profile"/>,
/// <see cref="Season"/>, <see cref="Activities"/>, <see cref="Instances"/>, <see cref="Social"/>,
/// <see cref="WorldState"/>) or in service-owned <see cref="SessionSlot{T}"/> slots.
/// </summary>
public sealed class GameSession(PhonestChannel channel, TrafficLog traffic, TimeProvider? clock = null)
{
    private object?[] _slots = [];

    internal TimeProvider Clock { get; } = clock ?? TimeProvider.System;
    internal long NowMs => Clock.GetUtcNow().ToUnixTimeMilliseconds();
    internal long NowSec => Clock.GetUtcNow().ToUnixTimeSeconds();
    internal DateTime UtcNow => Clock.GetUtcNow().UtcDateTime;

    /// <summary>Gets the service-owned state attached to this session for <paramref name="slot"/>, or null.</summary>
    public T? GetSlot<T>(SessionSlot<T> slot) where T : class =>
        (uint)slot.Index < (uint)_slots.Length ? (T?)_slots[slot.Index] : null;

    /// <summary>Gets or creates the service-owned state attached to this session for <paramref name="slot"/>.</summary>
    public T GetOrCreateSlot<T>(SessionSlot<T> slot) where T : class, new()
    {
        if ((uint)slot.Index < (uint)_slots.Length && _slots[slot.Index] is T existing) return existing;
        var created = new T();
        SetSlot(slot, created);
        return created;
    }

    /// <summary>Attaches or clears service-owned state on this session for <paramref name="slot"/>.</summary>
    public void SetSlot<T>(SessionSlot<T> slot, T? value) where T : class
    {
        if (slot.Index >= _slots.Length)
        {
            if (value == null) return;
            Array.Resize(ref _slots, Math.Max(slot.Index + 1, _slots.Length == 0 ? 8 : _slots.Length * 2));
        }
        _slots[slot.Index] = value;
    }

    /// <summary>The current game transport. A silent reconnect (ReconnectService) swaps it on the loop; frames and
    /// closes of a swapped-out channel are ignored by the gate.</summary>
    public PhonestChannel Channel { get; internal set; } = channel;

    /// <summary>The socket dropped and the role waits in its reconnect grace.</summary>
    internal bool TransportLost { get; set; }

    /// <summary>The server closed this connection to force a full relogin (recovery): no reconnect grace.</summary>
    internal bool ReconnectBlocked { get; set; }

    /// <summary>A reconnect found a 70010 handoff pending: the client's 30005 publication sends a 31037 even on a map
    /// without NPCs, since the client's socket resync waits for one (spec §1.6.5).</summary>
    internal bool ReconnectAoiPending { get; set; }

    /// <summary>While the web panel's GM console runs a command for this session (loop-owned), the GM replies sent to it.</summary>
    internal List<string>? GmReplies { get; set; }

    /// <summary>Closes the connection for a recovery relogin, after the replies already queued; unlike a network drop it
    /// never waits for a reconnect.</summary>
    internal void AbortForRecovery()
    {
        ReconnectBlocked = true;
        Channel.AbortAfterFlush();
    }

    public long HandshakeToken { get; set; }

    /// <summary>The signed-in account; set only by <see cref="SessionRegistry.SignInAsync"/> (tests may set it).</summary>
    public string? Account { get; internal set; }
    /// <summary>Account as the client spelled it at SDK login (AccountStore.ClientAccount), for what the client compares with it.</summary>
    public string? ClientAccount { get; set; }

    /// <summary>A message was refused before its stage (GameMessageHandler); only the first one is logged.</summary>
    internal bool EarlyMessageRefused { get; set; }

    internal long AccountGeneration { get; set; }
    internal Task PredecessorFence { get; set; } = Task.CompletedTask;

    private Role? _role;
    internal SessionRegistry? Registry { get; set; }
    public Role? Role
    {
        get => _role;
        set
        {
            long oldId = _role?.Id ?? 0;
            _role = value;
            long newId = value?.Id ?? 0;
            if (oldId != newId) Registry?.RoleBound(this, oldId, newId);
        }
    }
    public Ownership.OwnerRegistry? Owners { get; internal set; }
    public Ownership.RoleOwner? Owner { get; internal set; }
    public Ownership.AttachmentLease? Attachment { get; internal set; }
    // Cleanup may run after a selection bound a role but before its per-role loaders finished.
    internal bool RolePositionReady { get; set; }
    internal bool GuildOnlinePublished { get; set; }

    internal void ClearRoleLoadState()
    {
        RolePositionReady = false;
        GuildOnlinePublished = false;
        Progress = null;
        Guild = null; Tasks = null;
        CardLibraryReadyAttachment = null;
        VisibleConches.Clear(); PlayingPillar = null; PendingConch = 0; ConchStartPosition = null;
        DungeonAttempt = null;
        Items = []; Pets = null;
        WorldState = new();
        Profile.ClearRoleLoadState();
        Season.ClearRoleLoadState();
        Activities.ClearRoleLoadState();
        Instances.ClearRoleLoadState();
        SessionSlotRegistry.ClearRoleSlots(_slots);
        Power = null; PowerPublishedMs = 0;
    }

    private CancellationTokenSource _pendingCharacterIntents = new();
    internal CancellationToken PendingCharacterIntent => _pendingCharacterIntents.Token;
    // Only speculative, not-yet-locked grants use this pulse. Admitted mutations keep their durability.
    internal void CancelPendingCharacterIntents()
    {
        var previous = _pendingCharacterIntents;
        _pendingCharacterIntents = new();
        previous.Cancel(); previous.Dispose();
    }

    public WorldSessionState WorldState { get; set; } = new();
    public SessionCombatState Combat { get; } = new();
    public SessionProfileState Profile { get; } = new();
    public SessionSeasonState Season { get; } = new();
    public SessionActivityState Activities { get; } = new(clock ?? TimeProvider.System);
    public SessionInstanceState Instances { get; } = new();
    public SessionSocialState Social { get; } = new();

    /// <summary>One map transition per selected session, including asynchronous leave hooks.</summary>
    public SemaphoreSlim SceneTransitionGate { get; } = new(1, 1);

    /// <summary>Scene entry the client was sent to (70010) and is expected to report loaded (30005).</summary>
    public long SceneEntry { get; set; }

    /// <summary>Allocated dungeon instance, or zero for the shared world map. Never a config id.</summary>
    public long InstanceId { get; set; }

    /// <summary>Simulation/AOI key; the wire scene entry and navigation still use SceneEntry.</summary>
    public int LineNumber { get; set; } = 1;
    public Ro3Emu.Server.Maps.RuntimeMapKind RuntimeKind { get; set; }
    public Ro3Emu.Server.Maps.RuntimeMapKey RuntimeMap => InstanceId == 0
        ? Ro3Emu.Server.Maps.RuntimeMapKey.Public(SceneEntry, LineNumber)
        : RuntimeKind == Ro3Emu.Server.Maps.RuntimeMapKind.Event
            ? Ro3Emu.Server.Maps.RuntimeMapKey.Event(SceneEntry, InstanceId)
            : Ro3Emu.Server.Maps.RuntimeMapKey.Private(SceneEntry, InstanceId);
    public long WorldScene => RuntimeMap.WorldScene;

    /// <summary>One outstanding 70010/30005 handoff. Generation fences continuations; the wire has no nonce.</summary>
    public long SceneGeneration { get; set; }
    public SceneHandoff? Handoff { get; set; }

    /// <summary>Last position the client reported (WALK is client-authoritative), and the facing in degrees.</summary>
    public Romsg.Vec3 Position { get; set; } = new();

    public float Face { get; set; }

    /// <summary>Speed attribute (idx 113) of the role; the client moves at Speed/100 units per second.</summary>
    public long Speed { get; set; } = RoleSessionService.DefaultSpeed;

    /// <summary>Lazy public-position state (PositionPersistence).</summary>
    internal Persistence.LazyRolePart.State? PositionPart { get; set; }
    /// <summary>The role's client settings, merged in memory and written lazily (SettingsPersistence).</summary>
    internal Romsg.DBSettingInfo? Settings { get; set; }
    internal Persistence.LazyRolePart.State? SettingsPart { get; set; }
    /// <summary>The Bonus Time clock's lazy save (WildAfkPersistence).</summary>
    internal Persistence.LazyRolePart.State? WildAfkPart { get; set; }

    /// <summary>Dead (HP 0, 31049 state 1) until revived (31013).</summary>
    public bool Dead { get; set; }
    /// <summary>One process run and dead-to-alive cycle identify retries of the same paid revival.</summary>
    internal string? ReviveDeathSource { get; set; }

    /// <summary>The running auto-attack (CombatService).</summary>
    public AttackLoop? Attack { get; set; }

    /// <summary>Authoritative cast generation; delayed effects cannot outlive an interrupted/replaced cast.</summary>
    public ActiveSkillCast? SkillCast { get; set; }
    /// <summary>The running Skill Bar Lineup (CombatService.StartLineup); pressing the Skill Bar again replaces it.</summary>
    internal object? LineupRun { get; set; }
    public SemaphoreSlim SkillCastGate { get; } = new(1, 1);

    /// <summary>Status bits (property 2701, bit 15 = fighting).</summary>
    public long StatusBits { get; set; }
    /// <summary>No natural HP/SP regeneration before this server ms (CombatService.RegenAsync). Only tests set it.</summary>
    internal long NoRegenUntilMs { get; set; }

    /// <summary>Active buffs (BuffService).</summary>
    public List<ActiveBuff> Buffs { get; } = [];
    /// <summary>The role's kept buffs are on it, so a logout may save over them (BuffService.Offline).</summary>
    internal bool OfflineBuffsLoaded { get; set; }

    /// <summary>Current HP / SP (null = full); maxima come from the stat sheet.</summary>
    public long? CurrentHp { get; set; }
    public long? CurrentSp { get; set; }

    /// <summary>CharacterService.Stats' memo and the loop work item a combat path allowed it in.</summary>
    internal object? StatsMemo { get; set; }
    internal long StatsReuseSerial { get; set; } = -1;

    /// <summary>Stat builds in progress on this session (a totem's HP condition builds once more for MaxHP).</summary>
    internal int StatsBuildDepth { get; set; }

    /// <summary>Set by a build that read <see cref="CurrentHp"/> (TotemEffects op 10): the memo then keys on HP.</summary>
    internal bool StatsReadHp { get; set; }

    /// <summary>Above 0 while another plan's state stands in for the role's own (GenreService): no memo read or write.</summary>
    internal int StatsMemoBypass { get; set; }

    /// <summary>GM panel combat switches (GmNoCD, GmDamageFix, GmUnlimit), not saved; combat reads the field per cast or hit.</summary>
    public GmCombatSwitches GmCombat { get; set; }
    /// <summary>GM panel attribute changes (GmSetEntityAttr / GmChangeEntityAttr) as raw contributions, not saved.</summary>
    internal Dictionary<int, double>? GmAttributes { get; set; }
    /// <summary>The skill a GM panel cast (GmCastSkill / GmTargetCastSkill) may use unlearned, only while it is admitted.</summary>
    internal int GmCastSkill { get; set; }

    /// <summary>Between sending 70010 and the client's 30005: walks still refer to the old map and are dropped.</summary>
    public bool SceneLoading { get; set; }

    /// <summary>The distance the walk check still lets this client report (SceneService.AcceptWalkAsync).</summary>
    internal WalkAllowance WalkAllowance { get; } = new();
    /// <summary>A swing a move cancelled holds the next normal attack until this time (CombatService.CancelCastAsync).</summary>
    internal long SwingLockUntilMs { get; set; }
    /// <summary>The last move a skill's 31112 carried this player on (RoleSessionService.WalkAsync drops the walks that predate it).</summary>
    internal SkillCarry? Carry { get; private set; }

    /// <summary>A 31112 carries this player from <paramref name="start"/> to <paramref name="end"/> in <paramref name="moveMs"/>: the server is at the end at once.</summary>
    internal void CarryBySkill(Romsg.Vec3 start, Romsg.Vec3 end, long nowMs, int moveMs)
    {
        WorldState.PreviousPosition = start.Clone();
        Position = end.Clone();
        Carry = new SkillCarry(start.Clone(), end.Clone(), nowMs, nowMs + Math.Max(1, moveMs));
    }

    /// <summary>The game message budgets of this connection (GameMessageHandler.WithinRate).</summary>
    internal SessionRates? MessageRates { get; set; }

    /// <summary>The chat line budget of this connection (ChatService).</summary>
    internal RateBucket? ChatLines { get; set; }

    /// <summary>The same for flight moves (WorldCoreService.FlyMoveAsync), at the flight speed.</summary>
    internal WalkAllowance FlightAllowance { get; } = new();

    /// <summary>Level, exp, stats, currencies of the role in play (CharacterService).</summary>
    public RoleProgress? Progress { get; set; }

    /// <summary>Quest state (AUTO_SYNC 5027), loaded at login by TaskService.</summary>
    public Romsg.DBTaskInfo? Tasks { get; set; }

    /// <summary>Serializes quest transitions and source replay independently of the inventory transaction lock.</summary>
    public SemaphoreSlim TaskLock { get; } = new(1, 1);

    /// <summary>Serializes achievement progress and claims (taken before, never inside, the character lock).</summary>
    public SemaphoreSlim AchievementLock { get; } = new(1, 1);

    /// <summary>A reconciliation from the paced tick is queued (DailyActivityService.Tick).</summary>
    internal bool DailyReconcileQueued { get; set; }

    public SemaphoreSlim ActivityGate { get; } = new(1, 1);
    internal HashSet<long> VisibleConches { get; } = [];
    internal AdventureActivityService.PillarAttempt? PlayingPillar { get; set; }
    internal long ConchCycle { get; set; }
    internal long PendingConch { get; set; }
    internal Romsg.Vec3? ConchStartPosition { get; set; }

    /// <summary>Owned pet roster and plans (AUTO_SYNC 5017).</summary>
    public Romsg.DBPetInfo? Pets { get; set; }

    /// <summary>The card source has been loaded for this exact attachment, not a previous role/select.</summary>
    internal Ownership.AttachmentLease? CardLibraryReadyAttachment { get; set; }

    /// <summary>Mount ownership/selection (5012).</summary>
    public Romsg.DBMountsInfo? Mounts { get; set; }

    /// <summary>The normal attack the last skill sync put on the wheel for the ridden battle mount (0 for the career's own).</summary>
    internal int WheelAttack { get; set; }
    /// <summary>The follow-up statuses (SkillService.FollowUps) the last skill sync showed on the wheel.</summary>
    internal string FollowUpsShown { get; set; } = "";

    /// <summary>5047 role view, rebuilt from the exclusive guild membership index at login.</summary>
    public Romsg.DBRoleGuildInfo? Guild { get; set; }

    /// <summary>Current shared dungeon attempt.</summary>
    public DungeonService.Attempt? DungeonAttempt { get; set; }

    /// <summary>Last published role power (PowerService, 31502/31503/5066); null until this role's login publish.</summary>
    public PowerService.Snapshot? Power { get; set; }
    internal long PowerPublishedMs { get; set; }

    /// <summary>Fills the stored plan power of 5007/5025/5026/5067 before the dataset is diffed (PowerService).</summary>
    internal Action<uint, IMessage>? PlanPowers { get; set; }

    /// <summary>Season/playbook captured at role load, then advanced with its 5021/82708 clock notifications.</summary>
    public Romsg.SeasonState? SeasonState { get; set; }

    /// <summary>The active talent plan's skill parameter modifiers (SeasonTalentService).</summary>
    internal IReadOnlyList<(long Weapon, long Buff, SkillParameterRules.Modifier Modifier)> TalentSkillModifiers { get; set; } = [];
    /// <summary>The skill tuples of the talents' armed next-cast charges (TalentProcs.Charges).</summary>
    internal IReadOnlyList<SkillParameterRules.Modifier> TalentChargeModifiers { get; set; } = [];
    /// <summary>The learned passives' skill tuples with the weapons each needs (CombatService.PassiveModifiers).</summary>
    internal IReadOnlyList<(long[] Weapons, SkillParameterRules.Modifier Modifier)> PassiveSkillModifiers { get; set; } = [];
    /// <summary>The held Soul Echo resonance rows (SkillConfig ids, sorted) and their skill tuples (EmblemService.ResonanceEffects).</summary>
    internal int[] ResonanceSkills { get; set; } = [];
    internal IReadOnlyList<SkillParameterRules.Modifier> ResonanceSkillModifiers { get; set; } = [];
    /// <summary>The active talent plan's Buff(2) rows with their effect's _kDescData (TalentProcs).</summary>
    internal IReadOnlyList<(long Buff, string[] Description)> TalentProcBuffs { get; set; } = [];
    /// <summary>The active talent plan's changes to other buffs' values (BuffConfig._kBuffAttributeList, op 12).</summary>
    internal IReadOnlyList<SeasonTalentService.BuffAttribute> TalentBuffAttributes { get; set; } = [];

    /// <summary>Property list (AoIProperty idx, data, baseData) for ENTITYSELF / AOI bases, kept current by CharacterService.Refresh.</summary>
    public (long Id, long Value, long Base)[] EntityProperties { get; set; } = [];

    /// <summary>Items of the role in play, bag and equipped (CharacterService).</summary>
    public List<InvItem> Items { get; set; } = [];

    /// <summary>The last version of each AUTO_SYNC dataset the client got (msg id -> DB object), for AutoSync increments.</summary>
    public Dictionary<uint, Google.Protobuf.IMessage> Synced { get; } = [];
    /// <summary>The serialized last-sent copy of the large datasets CharacterService keeps packed.</summary>
    internal Dictionary<uint, byte[]> SyncedPacked { get; } = [];

    /// <summary>Serializes changes to the role state (game messages vs. GM commands from the HTTP endpoint).</summary>
    public SemaphoreSlim StateLock { get; } = new(1, 1);

    /// <summary>Distinguishes successive ClientAccount entities of one connection (role switching).</summary>
    public int AccountEntitySerial { get; set; }

    /// <summary>Other players this client currently shows (guarded by World's lock).</summary>
    public HashSet<GameSession> Visible { get; } = [];

    /// <summary>Players whose clients show this one: the reverse of their <see cref="Visible"/> sets.</summary>
    public HashSet<GameSession> Observers { get; } = [];

    /// <summary>Most other players this client shows: its 30005 / 30007 viewMaxCount, clamped to 1..149.</summary>
    public int ViewLimit { get; set; } = Maps.World.MaxViewLimit;

    /// <summary>Server units (monsters...) this client currently shows.</summary>
    public HashSet<Maps.WorldUnit> VisibleUnits { get; } = [];

    public Task SendRpcAsync(string method, params object?[] args)
    {
        if (!CanPresent()) return Task.CompletedTask;
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new MessagePackWriter(buffer);
        foreach (var arg in args) MessagePackSerializer.Serialize(arg?.GetType() ?? typeof(object), ref writer, arg);
        writer.Flush();
        traffic?.Rpc(Account, "s2c-rpc", method, () => string.Join(", ", args.Select(a => a?.ToString() ?? "nil")));
        return Channel.SendAsync(GameGateServer.EntityMessageOut, new EntityMessage
        {
            MethodName = ByteString.CopyFromUtf8(method),
            Parameters = ByteString.CopyFrom(buffer.WrittenSpan),
        });
    }

    private bool CanPresent() => Owners?.AllowsPresentation(this) != false
        && (Owners?.Current?.CanPresent(this) ?? true);

    public Task SendGameAsync(uint msgId, IMessage body)
    {
        if (!CanPresent()) return Task.CompletedTask;
        var bytes = body.ToByteArray();
        traffic?.ServerToClient(Account, msgId, body.Descriptor, bytes);
        return SendGameBytesAsync(msgId, bytes);
    }

    /// <summary>A message serialized once for many recipients (AOI broadcasts).</summary>
    public Task SendPreparedAsync(PreparedMessage prepared)
    {
        if (!CanPresent()) return Task.CompletedTask;
        if (Owners?.Current?.Notices is { } notices)
        {
            DeferPrepared(notices, prepared);
            return Task.CompletedTask;
        }
        traffic?.ServerToClient(Account, prepared.MsgId, prepared.Type, prepared.Body);
        Diagnostics.MessageCoverage.Outbound(prepared.MsgId);
        using var measurement = Channel.Metrics?.Outgoing(prepared.MsgId, prepared.Body.Length);
        return Channel.SendFrameAsync(prepared.Frame);
    }

    // The closure lives here so that the common path (no notices) does not allocate it on every send.
    private void DeferPrepared(Ownership.OwnerNotices notices, PreparedMessage prepared) =>
        notices.Add(this, () => SendPreparedAsync(prepared));

    /// <summary>A game message whose body is not a romsg protobuf (the chat tunnel).</summary>
    public Task SendGameRawAsync(uint msgId, byte[] body)
    {
        if (!CanPresent()) return Task.CompletedTask;
        traffic?.ServerToClientRaw(Account, msgId, body.Length);
        return SendGameBytesAsync(msgId, body);
    }

    private Task SendGameBytesAsync(uint msgId, byte[] bytes)
    {
        if (!CanPresent()) return Task.CompletedTask;
        if (Owners?.Current?.Notices is { } notices)
        {
            notices.Add(this, () => SendGameBytesAsync(msgId, bytes));
            return Task.CompletedTask;
        }
        Diagnostics.MessageCoverage.Outbound(msgId);
        using var measurement = Channel.Metrics?.Outgoing(msgId, bytes.Length);
        var parameters = new byte[4 + bytes.Length];
        BitConverter.TryWriteBytes(parameters, msgId);
        bytes.CopyTo(parameters, 4);
        return Channel.SendAsync(GameGateServer.EntityMessageOut, new EntityMessage { Parameters = ByteString.CopyFrom(parameters) });
    }

    /// <summary>Creates the client-side entity ("ClientAccount" before role select, "ClientAvatar" after).</summary>
    public Task CreateChannelEntityAsync(string type, string entityId, long channelId, byte[]? reconnectInfo = null)
    {
        var info = new ChannelEntityInfo
        {
            Type = ByteString.CopyFromUtf8(type),
            EntityId = ByteString.CopyFromUtf8(entityId),
            ChannelId = channelId,
            ChannelMode = 0,
        };
        if (reconnectInfo != null) info.ReconnectInfo = ByteString.CopyFrom(reconnectInfo);
        return Channel.SendAsync(GameGateServer.CreateChannelEntity, info);
    }
}
