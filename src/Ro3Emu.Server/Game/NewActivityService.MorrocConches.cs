using Romsg;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

public sealed partial class NewActivityService
{
    private const int SouthernMorroc = 10522, MorrocPhase = 102;
    private const int MorrocConch = 8000004, MorrocPillar = 8000312;

    // Server-authored fallback (inferred). ExplorePhaseConfig[102] lists 13 pickups; the current
    // sc_moc_fild12_001_sceneeditor has none. Both templates have chapter 102 show conditions.
    // The cell centres are connected on foot to the map entrance. Keep these IDs stable across edits:
    // they are the per-player collection keys in DBWorldExplorationNpcCollectCount.
    private static readonly (int Id, int Npc, float X, float Z)[] MorrocFallback =
    [
        (900001, MorrocConch, 180.5f, 354.5f),
        (900002, MorrocConch, 60.5f, 63.5f),
        (900003, MorrocConch, 276.5f, 84.5f),
        (900004, MorrocConch, 63.5f, 231.5f),
        (900005, MorrocConch, 270.5f, 231.5f),
        (900006, MorrocConch, 57.5f, 351.5f),
        (900007, MorrocConch, 162.5f, 255.5f),
        (900008, MorrocConch, 183.5f, 57.5f),
        (900009, MorrocConch, 261.5f, 318.5f),
        (900010, MorrocConch, 75.5f, 147.5f),
        (900011, MorrocConch, 246.5f, 156.5f),
        (900012, MorrocConch, 123.5f, 96.5f),
        (900013, MorrocPillar, 165.5f, 153.5f),
    ];

    private long MorrocCollectLimit()
    {
        if (ExplorePhaseConfig.Get(_data, MorrocPhase) is not { Activityid: ExplorationActivity } phase) return 0;
        int index = Array.IndexOf(phase.Map, (long)SouthernMorroc);
        var totals = phase.MapCollect;
        return index >= 0 && index < totals.Length ? totals[index] : 0;
    }

    internal IEnumerable<SpawnPoint> ConchPlacements(MapWorld world)
    {
        var authored = world.SpawnGroups.SelectMany(g => g.Points);
        if (world.SceneEntry != SouthernMorroc || world.Nav == null || MorrocCollectLimit() != MorrocFallback.Length)
            return authored;
        // Prefer a replacement scene's own Genesis placements. Gathering and monster points use other interactions.
        var templates = authored.Select(p => p.ConfigId).Concat(world.Npcs.Select(n => n.NpcId)).Distinct();
        if (templates.Any(id => NPCConfig.Get(_data, id)?.InteractId is
            [ConchInteraction] or [SpecialConchInteraction] or [_, PillarFinishInteraction])) return authored;
        return MorrocGroundedPoints(world.Nav, world.Born?.Y ?? 0);
    }

    private static IEnumerable<SpawnPoint> MorrocGroundedPoints(NavGrid nav, float hint)
    {
        foreach (var (id, npc, x, z) in MorrocFallback)
            if (nav.WalkHeight(x, z, hint) is { } y)
                yield return new SpawnPoint(id, x, y, z, 0, npc);
    }

    private bool MorrocFallbackQuotaReached(GameSession session) =>
        session.SceneEntry == SouthernMorroc &&
        session.NewActivities?.ActivityInfo.GetValueOrDefault(ExplorationActivity)?.ActivityParam?.WorldExploration
            ?.MapCollect.GetValueOrDefault(SouthernMorroc) is { } collected &&
        MorrocQuotaReached(collected, MorrocCollectLimit());

    // When real scene placements supersede the fallback, the editor IDs change. Old pickups still
    // count against the same map total. Both AOI visibility and claim validation share this gate.
    internal static bool MorrocQuotaReached(DBWorldExplorationNpcCollectCount collected, long limit) =>
        limit > 0 && collected.Sum >= limit && MorrocFallback.Any(p => collected.CollectCount.ContainsKey(p.Id));
}
