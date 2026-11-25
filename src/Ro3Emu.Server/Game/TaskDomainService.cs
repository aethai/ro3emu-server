using System.Text.Json;
using Romsg;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Activity, event, faction, dungeon, growth, tavern, chicken, and plot-region quest subsystems extracted from
/// <see cref="TaskService"/>.
/// </summary>
public sealed partial class TaskDomainService(
    TaskService tasks,
    GameData data,
    FunctionService functions,
    GameLoop loop,
    SessionRegistry sessions,
    SceneService scene,
    TimeProvider clock,
    ILogger<TaskDomainService> log)
{
    private readonly GameData _data = data;
    private readonly FunctionService _functions = functions;
    private readonly GameLoop _loop = loop;
    private readonly SessionRegistry _sessions = sessions;
    private readonly SceneService _scene = scene;
    private readonly TimeProvider _clock = clock;
    private readonly ILogger<TaskDomainService> _log = log;
    private const uint TaskRewardNotice = TaskService.TaskRewardNotice;
    private const TaskState Running = TaskState.Running;
    private const int TPlot = TaskService.TPlot;
    private const int TOnePieceCount = TaskService.TOnePieceCount;
    private const int TGuildHunt = TaskService.TGuildHunt;
    private const int TTimedBattle = TaskService.TTimedBattle;
    private const int TCoffinOrSurvivor = TaskService.TCoffinOrSurvivor;
    private const int TDragonCoffin = TaskService.TDragonCoffin;
    private const int TDraculaWedding = TaskService.TDraculaWedding;
    private const int TLogin = TaskService.TLogin;
    private const int TBarroomActTime = TaskService.TBarroomActTime;
    private const int TSaplingChosen = TaskService.TSaplingChosen;
    private const int TDeclaration = TaskService.TDeclaration;
    private const int TWaterSapling = TaskService.TWaterSapling;
    private const int TFactionPoints = TaskService.TFactionPoints;
    private const int TFactionCrystal = TaskService.TFactionCrystal;
    private const int TRivalKill = TaskService.TRivalKill;
    private const int FactionFunction = TaskService.FactionFunction;
    private const long BecomeStrongerFunction = TaskService.BecomeStrongerFunction;
    private const int BarRoomTaskType = TaskService.BarRoomTaskType;
    private const int ChickenTaskType = TaskService.ChickenTaskType;
    private const int FinalStep = TaskService.FinalStep;
    private const int RunOver = TaskService.RunOver;

    private Func<GameSession, int>? FactionCamp => tasks.FactionCamp;
    private Func<GameSession, GameSession, bool>? FactionRivals => tasks.FactionRivals;
    private Action<GameSession, long>? FactionGuildAward => tasks.FactionGuildAward;
    private Func<GameSession, int, long>? DungeonRunValue => tasks.DungeonRunValue;
    private TimeSpan PlotRegionFallbackDelay => tasks.PlotRegionFallbackDelay;
    private Func<GameSession, IReadOnlyDictionary<(int Type, int Son), bool>>? OpenActivityTaskGroups => tasks.OpenActivityTaskGroups;
    private Func<GameSession, long>? TavernSeconds => tasks.TavernSeconds;

    private Dictionary<long, TaskService.TaskDef> Defs() => tasks.Defs();
    private bool Active(GameSession s, long id) => tasks.Active(s, id);
    private static bool IsDelivered(GameSession s, long id) => TaskService.IsDelivered(s, id);
    private bool Ready(GameSession s, TaskService.TaskDef def) => tasks.Ready(s, def);
    private void Accept(GameSession s, long id) => tasks.Accept(s, id);
    private bool Holds(GameSession s, int type) => tasks.Holds(s, type);
    private void QueueRefresh(GameSession s) => tasks.QueueRefresh(s);
    private long Required(GameSession? s, long[] t) => tasks.Required(s, t);
    private static long[] Longs(JsonElement row, string field) => TaskService.Longs(row, field);
    private static Task<IDisposable> LockTasksAsync(GameSession s) => TaskService.LockTasksAsync(s);
    private Task SyncTasksAsync(GameSession s) => tasks.SyncTasksAsync(s);
    private Task FlushAsync(GameSession s, bool settled = false) => tasks.FlushAsync(s, settled);
    private Task SettleAsync(GameSession s) => tasks.SettleAsync(s);
    private Task ProgressAsync(GameSession s, int? type, Func<long[], long> amount, Func<TaskService.TaskDef, bool>? eligible = null) =>
        tasks.ProgressAsync(s, type, amount, eligible);
}
