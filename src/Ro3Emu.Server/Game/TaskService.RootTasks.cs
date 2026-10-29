namespace Ro3Emu.Server.Game;

/// <summary>
/// Main, Side, Guide and Connect quests that follow no other quest (TaskConfig rows with an empty _kPreTask and
/// _kAcceptTask [1] Auto; enum_TaskType Main 1, Side 2, Guide 3, Connect 19: a Connect quest only once every one of its
/// targets has a reader, so the two with an unread target stay unstarted; inferred, as for the other types).
/// Their only gate is _iCondition: 19991050 Third Class Advancement
/// [88000107: base level 50], the Side quests such as 10200010 and the Guide rows 306070/306080. The client never asks for
/// an Auto quest; LC_DAL_Task only shows the 5027 task list it is sent, so the server starts each one once its condition
/// holds (inferred: the only other root, FirstTask, is given to a new role). Without this a role that finished the
/// main story (10108262 "Time to Explore Freely", target base level 999, the end of the current main quests) never
/// got the Third Class Advancement chain.
/// </summary>
public sealed partial class TaskService
{
    private const int MainTaskType = 1, SideTaskType = 2, GuideTaskType = 3, ConnectTaskType = 19;

    private TaskDef[]? _rootTasks;

    private TaskDef[] RootTasks() => _rootTasks ??= [.. Defs().Values
        .Where(d => d.Pre.Length == 0 && d.Accept == ReceiveAuto && d.Id != FirstTask
            && (d.Type is MainTaskType or SideTaskType or GuideTaskType
                || d.Type == ConnectTaskType && TargetsSupported(d.Id)))
        .OrderBy(d => d.Id)];

    private int AcceptRootTasks(GameSession session)
    {
        int accepted = 0, connect = 0;
        foreach (var def in RootTasks())
        {
            if (Active(session, def.Id) || IsDelivered(session, def.Id) || !Ready(session, def)) continue;
            bool batch = def.Type == ConnectTaskType;
            Accept(session, def.Id, quiet: batch);
            accepted++;
            if (batch) connect++;
        }
        // One line, not one per quest: the feedback report keeps only the last log lines of a player.
        if (connect > 0) LogConnectStarted(session.Role?.Name, connect);
        return accepted;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Name} starts {Count} Connect quests")]
    private partial void LogConnectStarted(string? name, int count);
}
