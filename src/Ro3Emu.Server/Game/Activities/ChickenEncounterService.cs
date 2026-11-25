using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;
using Match = Ro3Emu.Server.Game.TimedBattleService.Match;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Survivor Battle shops (Kafra Shop / Equipment Merchant, 82334 / 82332) and Magic Die roguelike encounters
/// (5039 / 83421 / 83422). Extracted from <see cref="TimedBattleService"/>.
/// </summary>
public sealed partial class ChickenEncounterService(TimedBattleService timed, ChickenBattleService chicken)
{
    private readonly TimedBattleService _timed = timed;
    private readonly ChickenBattleService _chicken = chicken;

    private GameData _data => _timed.GameData;
    private InstanceService _instances => _timed.Instances;
    private BuffService _buffs => _timed.Buffs;
    private Dictionary<long, Match> _matches => _timed.MatchesByRoom;

    private Task RunMatchTransitionAsync(Match match, string origin, Func<Task> work) =>
        _timed.RunMatchTransitionAsync(match, origin, work);
    private (Match Match, ChickenRun Run, ChickenPlayer Player)? Chicken(GameSession session) => _chicken.Chicken(session);
    private long NowMs() => _chicken.NowMs();
    private long NextChestUid() => _chicken.NextChestUid();
    private IEnumerable<GameSession> InRoom(Match match) => _chicken.InRoom(match);
    private JsonElement? ItemRow(int entry) => _chicken.ItemRow(entry);
    private int ItemType(int entry) => _chicken.ItemType(entry);
    private const int EquipmentType = ChickenBattleService.EquipmentType;
    private const long ChickenScene = ChickenBattleService.ChickenScene;
    private static (float X, float Z) RandomPoint(ChickenRun run, float cx, float cz, float radius) =>
        ChickenBattleService.RandomPoint(run, cx, cz, radius);
    private MapPoint? WalkablePoint(float x, float z) => _chicken.WalkablePoint(x, z);
    private string NpcName(JsonElement row) => _chicken.NpcName(row);
    private static int CanAct(ChickenRun run, ChickenPlayer player, GameSession session) =>
        ChickenBattleService.CanAct(run, player, session);
    private int Fits(ChickenPlayer player, int entry, int wanted) => _chicken.Fits(player, entry, wanted);
    private List<ChickenItem> AddToPack(ChickenRun run, ChickenPlayer player, int entry, int amount) =>
        _chicken.AddToPack(run, player, entry, amount);
    private bool RemoveFromPack(ChickenPlayer player, long guid, int count) => _chicken.RemoveFromPack(player, guid, count);
    private Task SyncRoundAsync(Match match, ChickenRun run, GameSession session) => _chicken.SyncRoundAsync(match, run, session);
}
