using System.Text;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Guild Area Statues of the home: GuildSettingConfig PresentNpcPosition, LeaderNpcPosition and BabyNpcPosition hold
/// [NPCConfig id, x, z, face] (NPC 25000001 "Guild Area Statue", _iNPCSubType 5 Effigy). The client spawns nothing from
/// them itself; their NPC's _iClientAct 4 is Lua_UnitActConfigExt's Display_GuildStatue, whose
/// LC_Display_Agent_GuildStatue._createModel loads a character model (LC_ModelLoader.LoadRole) from the entity's
/// effigyData.carrer, gender and curUseAppearances (LC_UnitBase.InitAppearance). So the server sends each statue as an
/// NPC entity carrying an officer's look: the Guild Master, the Leader and the Charisma Baby (GuildPositionConfig 1, 2
/// and 10, one seat each), in the order of the three settings (inferred from their names). The entity's equipInfo gives
/// the model the officer's worn weapons (LC_UnitBase.GetWeaponsID), read from the stored items for offline officers too.
/// </summary>
public sealed partial class GuildHomeService
{
    private const int MasterOffice = 1, LeaderOffice = 2, CharismaBabyOffice = 10;
    /// <summary>The statues' home actor points (GuildCampfireService.HomeActorUid), clear of the bonfire's: each redraw
    /// takes the next three, because the client keeps a unit it was told to leave for a while and would update that one
    /// (LC_UnitManager.CacheWaitRemoveUnit) instead of building a new statue.</summary>
    private const int StatuePoint = 3900, StatueGenerations = 64;
    /// <summary>A vacant seat shows the statue's own name on a Novice (inferred: nothing says what an empty seat shows).</summary>
    private const int NoviceCareer = 1000;
    /// <summary>The scene the three settings were placed in: MapResource 40002 sc_pm_unioncity_002, where all three stand
    /// on walkable ground, while the home GuildMap 4000101 is sc_pm_unioncity_001, where they fall off the map. No map or
    /// dungeon row uses _002 in this client (inferred from the nav grids).</summary>
    private const long StatueLayoutResource = 40002;
    /// <summary>How often a room's statues are rebuilt to catch an officer's new look, class or name (inferred).</summary>
    private const long StatueRecheckSeconds = 30;

    /// <summary>A live home room's statues: who sits, the view last sent, its generation of actor uids.</summary>
    private sealed class StatueRoom
    {
        internal long[] Holders = [];
        internal string Signature = "";
        internal AoIViewChangeData? View;
        internal int Generation;
        internal bool Building;
        internal long NextCheck;
    }

    private readonly Dictionary<long, StatueRoom> _statueRooms = [];
    private MapPoint? _statueLayoutBorn;

    private (double[]? Setting, int Office)[] StatueSeats()
    {
        var settings = GuildSettings.Of(_data);
        return [(settings.PresentNpcPosition, MasterOffice), (settings.LeaderNpcPosition, LeaderOffice),
            (settings.BabyNpcPosition, CharismaBabyOffice)];
    }

    /// <summary>The role in each statue's office, 0 for a vacant one; the earliest to join wins a tie (inferred).</summary>
    private long[] StatueHolders(GuildStore.State g) => [.. StatueSeats().Select(seat => g.Members.Values
        .Where(m => m.Official == seat.Office).OrderBy(m => m.EnterTime).ThenBy(m => m.RoleId).Select(m => m.RoleId)
        .FirstOrDefault())];

    /// <summary>A member loading into the home sees the room's statues: the view last built, or the first one.</summary>
    private Task StatuesLoadedAsync(GameSession s)
    {
        if (!InHome(s, out long guild) || RoomOf(guild) is not { } room) return Task.CompletedTask;
        if (_statueRooms.TryGetValue(room.Id, out var state) && state.View is { } view)
            return s.SendGameAsync(GameAoiSink.SyncAoiView, view);
        CheckStatues(guild, room); // the first member: the build sends to whoever is inside when it ends
        return Task.CompletedTask;
    }

    /// <summary>From the home's Tick: an office that changed hands, or the periodic recheck, rebuilds the room's statues.</summary>
    private void CheckStatues(long guild, InstanceService.Room room)
    {
        if (_guilds.Get(guild) is not { } g) return;
        if (!_statueRooms.TryGetValue(room.Id, out var state)) _statueRooms[room.Id] = state = new();
        var holders = StatueHolders(g);
        long now = _time.GetUtcNow().ToUnixTimeSeconds();
        if (state.Building || state.View != null && state.Holders.SequenceEqual(holders) && now < state.NextCheck) return;
        state.Building = true;
        state.NextCheck = now + StatueRecheckSeconds;
        int generation = (state.Generation + 1) % StatueGenerations;
        _loop.TrackRuntimeDetached("guild home statues", async () =>
        {
            try
            {
                var (view, signature) = await StatuesAsync(room, holders, generation);
                if (room.Closed || !ReferenceEquals(_statueRooms.GetValueOrDefault(room.Id), state)) return;
                if (state.View != null && signature == state.Signature) { state.Holders = holders; return; }
                var previous = state.View;
                (state.Holders, state.Signature, state.View, state.Generation) = (holders, signature, view, generation);
                await PublishStatuesAsync(guild, room, previous, view);
            }
            finally { state.Building = false; }
        });
    }

    /// <summary>Retires the previous statues (their uids) and shows the new ones to every member in the room.</summary>
    private async Task PublishStatuesAsync(long guild, InstanceService.Room room, AoIViewChangeData? previous, AoIViewChangeData view)
    {
        var leave = new AoIViewChangeData();
        if (previous != null) leave.LeaveAoi.AddRange(previous.Bases.Select(actor => actor.Uuid));
        foreach (var member in room.ActiveMembers.ToArray())
        {
            var peer = member.Session;
            if (peer.SceneLoading || peer.InstanceId != room.Id || GuildId(peer) != guild) continue;
            try
            {
                if (leave.LeaveAoi.Count > 0) await peer.SendGameAsync(GameAoiSink.SyncAoiView, leave);
                await peer.SendGameAsync(GameAoiSink.SyncAoiView, view);
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
        }
    }

    /// <summary>The statues of one generation and what they show (each seat's name, class, sex and look), to tell a
    /// rebuild that changed something from one that did not.</summary>
    private async Task<(AoIViewChangeData View, string Signature)> StatuesAsync(InstanceService.Room room, long[] holders, int generation)
    {
        var view = new AoIViewChangeData();
        var signature = new StringBuilder();
        var seats = StatueSeats();
        for (int i = 0; i < seats.Length; i++)
        {
            if (seats[i].Setting is not [var npcId, var x, var z, var face] || NPCConfig.Get(_data, (long)npcId) is not { } npc) continue;
            var role = holders[i] == 0 ? null : await _roles.GetAsync(holders[i]);
            var looks = role == null ? null : RoleProto.Appearances(role, await _roles.LoadWardrobeAsync(role.Id), _time);
            long[] gear = role == null ? [] : EquipInfo(await _roles.LoadWornGearAsync(role.Id));
            var (px, pz) = InHomeLayout((float)x, (float)z);
            long uid = GuildCampfireService.HomeActorUid(room, StatuePoint + generation * seats.Length + i);
            var actor = SceneService.NpcEntity(new WorldNpc(uid, Scene, WorldNpcKind.Npc, (int)npcId,
                role?.Name ?? npc.DisplayName(_data), (int)npc.Camp, px, Ground(px, pz), pz, (float)face, default),
                _time.GetUtcNow().ToUnixTimeMilliseconds());
            actor.Gender = role?.Sex ?? 1;
            actor.EffigyData = new EffigyData { Carrer = role?.Career ?? NoviceCareer };
            if (looks != null) actor.CurUseAppearances.Add(looks);
            actor.EquipInfo.AddRange(gear);
            view.Bases.Add(actor);
            signature.Append(i).Append(':').Append(actor.Name).Append('/').Append(actor.EffigyData.Carrer).Append('/')
                .Append(actor.Gender).Append('/').AppendJoin(',', (looks ?? []).OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))
                .Append('/').AppendJoin(',', gear)
                .Append(';');
        }
        return (view, signature.ToString());
    }

    /// <summary>EntityBase.equipInfo: ItemConfig ids of grids 1..7, 0 for empty (CharacterStatsService.EquipInfo).</summary>
    private static long[] EquipInfo(List<(int Grid, long Entry)> worn)
    {
        var slots = new long[CharacterService.EquipInfoSlots];
        foreach (var (grid, entry) in worn)
            if (grid is >= 1 and <= CharacterService.EquipInfoSlots) slots[grid - 1] = entry;
        return slots;
    }

    /// <summary>A settings point moved from the _002 layout into the home map by the offset between the two scenes' default
    /// born points (both face 180): the trio lands on the home plaza, the Guild Master's on its round plinth in front of
    /// the hall (inferred; checked in the client).</summary>
    private (float X, float Z) InHomeLayout(float x, float z)
    {
        if (DungeonConfig.Get(_data, Scene)?.MapResourceId == StatueLayoutResource
            || (_statueLayoutBorn ??= _maps.ResourceBorn(StatueLayoutResource)) is not { } layout || _maps.Map(Scene)?.Born is not { } home) return (x, z);
        return (x - layout.X + home.X, z - layout.Z + home.Z);
    }

    /// <summary>The statue's ground: the top of its cell's layers, a plinth players cannot walk on included (inferred).</summary>
    private float Ground(float x, float z)
    {
        if (_maps.Map(Scene)?.Nav is not { } nav) return 0;
        float? top = null;
        foreach (var (_, height) in nav.Layers(x, z)) top = top is { } t ? MathF.Max(t, height) : height;
        return top ?? 0;
    }
}
