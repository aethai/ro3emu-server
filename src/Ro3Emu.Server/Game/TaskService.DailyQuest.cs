using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The daily side quests (ServerOptions.DailyQuests, inferred: the client has no board for them): the first
/// <see cref="DailyQuestOptions.PerDay"/> quests a role turns in each game day pay extra base EXP on top of the quest's
/// own reward — <see cref="DailyQuestOptions.ExpPercent"/> of the EXP the role's base level needs — so a new character
/// can level by questing. The count rides in <see cref="TaskRuntimeState"/>, so it commits with the delivery that paid
/// it, and belongs to DailyActivityService's game day (05:00): a delivery on a later day starts the count over.
/// </summary>
public sealed partial class TaskService
{
    /// <summary>The rules; the host sets the operator's (Ro3Emu:DailyQuests). Default: off.</summary>
    public DailyQuestOptions DailyQuests { get; set; } = new();

    /// <summary>
    /// The extra base EXP a delivery of <paramref name="taskId"/> pays now, 0 once the day's allowance is spent; the
    /// count and day it belongs to are written to <paramref name="runtime"/>, which the settlement saves with the
    /// award. Called inside the quest lock, before the award commits.
    /// </summary>
    internal long TakeDailyQuestExp(GameSession session, TaskRuntimeState runtime)
    {
        var rules = DailyQuests;
        if (rules.PerDay <= 0 || rules.ExpPercent <= 0 || session.Progress is not { } progress) return 0;
        long today = session.Daily?.DailyEpoch ?? 0;
        if (today <= 0) return 0; // the daily state has not loaded the role yet
        if (runtime.DailyQuestDay != today)
        {
            runtime.DailyQuestDay = today;
            runtime.DailyQuestCount = 0;
        }
        if (runtime.DailyQuestCount >= rules.PerDay) return 0;
        runtime.DailyQuestCount++;
        return DailyQuestExp(_data, progress.Level, rules.ExpPercent);
    }

    /// <summary>The bonus EXP of one daily quest at <paramref name="level"/>: <paramref name="percent"/> of the EXP
    /// that level needs, at least 1. A level past the table's last takes the last row that needs EXP (the cap level).</summary>
    internal static long DailyQuestExp(GameData data, int level, int percent)
    {
        long need = 0;
        for (int row = level; row >= 1 && need <= 0; row--)
            need = data.Row("BaseLvConfig", row) is { } value ? GameData.Int(value, "_iBaseExp") : 0;
        return Math.Max(1, need * percent / 100);
    }
}
