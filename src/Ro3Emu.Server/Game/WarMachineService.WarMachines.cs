using System.Collections.Concurrent;
using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

using static Ro3Emu.Server.Game.TerritoryWarsService;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The battle side of the war machines (GuildService.Wartime keeps the warehouse, the reservations and who operates
/// what): where a member may reserve and build them, and the cannons standing in a siege or a type-18 territory battle
/// (TerritoryWarsService.WarRooms).
/// </summary>
public sealed partial class WarMachineService
{
    /// <summary>Cannon unit ids: their own namespace below 2^53 (see DungeonService.SceneActorUidBase).</summary>
    private const long CannonUidBase = 1L << 49;
    private long _cannonSerial;
    private long _cannonBuffGuid = (1L << 56) | (1L << 55);
    private readonly ConcurrentDictionary<long, (CannonUnit Unit, InstanceService.Room Room)> _cannons = new();

    /// <summary>
    /// GuildService.WarSideOf: 1 for the attackers, 2 for the defenders of the running siege the role is loaded into and
    /// fights for, 3 in an active type-18 territory battle (<see cref="WarRoomOf"/>), else 0.
    /// </summary>
    private int WarSide(GameSession s) => WarRoomOf(s)?.Side ?? 0;

    /// <summary>GuildService.WarMachines for the siege and the type-18 territory battles.</summary>
    private sealed class MachineSite(WarMachineService owner) : GuildService.IWarMachineSite
    {
        /// <summary>
        /// The client's checks (LC_GVGBuildCannon): the blueprint stays within _iBuildRadius of where its builder
        /// stood, on walkable ground, and out of every standing cannon's _iExclusiveRadius. (inferred) The server allows
        /// 3 m beyond the build radius for the walk while placing; the invalid areas of dungeon var 501
        /// (CInvalidBuildCannonAreasID) are a server list the client does not hold, so none are checked.
        /// </summary>
        public int CheckPlace(GameSession builder, JsonElement cannon, Vec3 pos)
        {
            if (owner.WarRoomOf(builder) is not { } battle) return ErrorCodes.GuildCannonPlace;
            float reach = new GVGCannonConfig(cannon).BuildRadius / 100f + 3f;
            if (MonsterService.Distance(builder.Position.X, builder.Position.Z, pos.X, pos.Z) > reach)
                return ErrorCodes.GuildCannonPlace;
            if (owner._maps.Map(builder.SceneEntry)?.Nav is { } nav && nav.WalkHeight(pos.X, pos.Z, pos.Y) == null)
                return ErrorCodes.GuildCannonPlace;
            float exclusive = new GVGCannonConfig(cannon).ExclusiveRadius / 100f;
            foreach (var (unit, room) in owner.StandingCannons())
                if (room == battle.Room && MonsterService.Distance(unit.X, unit.Z, pos.X, pos.Z) < exclusive * 2)
                    return ErrorCodes.GuildCannonPlace;
            return 0;
        }

        /// <summary>32702 to the builder and the players who see it (WorldInteractionService.NotifyAsync's shape).</summary>
        public async Task BuildStateAsync(GameSession builder, BuildCannonState? state)
        {
            if (builder.Role == null) return;
            var info = state == null ? new InteractInfo { EType = InteractInfo.Types.EState.Idle }
                : new InteractInfo { EType = InteractInfo.Types.EState.BuildingCannon, StateId = 1, BuildCannonInfo = state };
            var message = new InteractChangeNtf { Uid = builder.Role.Id, DataInfo = info };
            await owner.Send(builder, WorldInteractionService.Change, message);
            foreach (var viewer in owner._world.Viewers(builder)) await owner.Send(viewer, WorldInteractionService.Change, message);
        }

        public async Task<long> RaiseAsync(GameSession builder, JsonElement cannon, Vec3 pos, float face)
        {
            if (owner.WarRoomOf(builder) is not { } battle || builder.Role is not { } role) return 0;
            int npcId = (int)new GVGCannonConfig(cannon).NpcId;
            if (owner._data.Row("NPCConfig", npcId) is not { } npc || owner._monsters.MachineTemplate(npcId) is not { } template) return 0;
            var at = owner._maps.Map(builder.SceneEntry) is { } map ? map.Grounded(new MapPoint(pos.X, pos.Y, pos.Z, face))
                : new MapPoint(pos.X, pos.Y, pos.Z, face);
            var (group, home) = MachineHome(npcId, at);
            long camp = CampOf(builder);
            var unit = new CannonUnit
            {
                Uid = CannonUidBase | Interlocked.Increment(ref owner._cannonSerial), SceneEntry = builder.SceneEntry,
                InstanceId = battle.Room.Id, X = at.X, Y = at.Y, Z = at.Z, Face = face, NpcId = npcId, Builder = role.Id,
                Camp = camp, CampOverride = (int)camp, Guild = battle.Guild, Name = role.Name,
                Template = template, Group = group, Home = home, Hp = template.MaxHp, BornTimeMs = owner._loop.NowMs,
                ConfigId = new GVGCannonConfig(cannon).Id, Bullets = new GVGCannonConfig(cannon).BulletNumLimit,
                // Its HP is only its own: no lock on whoever hits it, no heal to full when the fight moves away; the repair
                // zone mends it (TerritoryWarsService.WarRepair.cs).
                KeepsDamage = true, StaysOutOfBattle = true,
            };
            foreach (long[] entry in new NPCConfig(npc).BornEventBuff)
                if (entry is [var buff, ..] && buff > 0 && owner._data.Row("BuffConfig", buff) != null)
                    unit.Buffs.Add(new BuffInfo
                    {
                        BuffGuid = Interlocked.Increment(ref owner._cannonBuffGuid), ConfigId = (int)buff, PileCount = 1,
                        SourceId = unit.Uid,
                    });
            if (!await owner._instances.AddPropAsync(battle.Room, unit)) return 0;
            owner._cannons[unit.Uid] = (unit, battle.Room);
            return unit.Uid;
        }

        public Task<long> RaiseVehicleAsync(GameSession vehicleOwner, int npcId) => owner.RaiseVehicleAsync(vehicleOwner, npcId);

        public Task<bool> RaiseRepairZoneAsync(GameSession user, int item, long skill) => owner.RaiseRepairZoneAsync(user, item, skill);

        public bool Standing(long machine) => owner._cannons.TryGetValue(machine, out var c) && !c.Unit.Removed && !c.Room.Closed
            || owner.VehicleStanding(machine);

        public async Task RemoveAsync(long machine)
        {
            if (await owner.RemoveVehicleAsync(machine) || !owner._cannons.TryRemove(machine, out var c)) return;
            await owner.EjectCannonOperatorAsync(c.Unit);
            await owner._instances.RemovePropAsync(c.Room, c.Unit);
        }
    }

    /// <summary>The cannons still standing; the ones their siege removed are forgotten.</summary>
    private IEnumerable<(CannonUnit Unit, InstanceService.Room Room)> StandingCannons()
    {
        foreach (var (uid, cannon) in _cannons)
        {
            if (cannon.Unit.Removed || cannon.Room.Closed) _cannons.TryRemove(uid, out _);
            else yield return cannon;
        }
    }
}
