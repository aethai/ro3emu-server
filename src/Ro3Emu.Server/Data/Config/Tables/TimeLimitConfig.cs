using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's TimeLimitConfig (data/config/TimeLimitConfig.json).</summary>
[ConfigTable("TimeLimitConfig")]
public readonly partial record struct TimeLimitConfig(JsonElement Row)
{
    [ConfigField("_iCycleCount")] public partial long CycleCount { get; }
    [ConfigField("_iDayEnd")] public partial long DayEnd { get; }
    [ConfigField("_iDayStart")] public partial long DayStart { get; }
    [ConfigField("_iHourEnd")] public partial long HourEnd { get; }
    [ConfigField("_iHourStart")] public partial long HourStart { get; }
    [ConfigField("_iMonthEnd")] public partial long MonthEnd { get; }
    [ConfigField("_iMonthStart")] public partial long MonthStart { get; }
    [ConfigField("_iMonthRefreshCount")] public partial long MonthRefreshCount { get; }
    [ConfigField("_iTimeLimitId")] public partial long TimeLimitId { get; }
    [ConfigField("_iWeekRefreshCount")] public partial long WeekRefreshCount { get; }
    [ConfigField("_iWeekEnd")] public partial long WeekEnd { get; }
    [ConfigField("_iWeekStart")] public partial long WeekStart { get; }
    [ConfigField("_strStateName")] public partial string TrStateName { get; }

    /// <summary>The largest unit the row repeats by (Month, Week, Day, Hour: the first with a start or end set), or
    /// null for a fixed window (LC_TimeLimitHelper).</summary>
    public string? RepeatUnit =>
        MonthStart > 0 || MonthEnd > 0 ? "Month" : WeekStart > 0 || WeekEnd > 0 ? "Week"
        : DayStart > 0 || DayEnd > 0 ? "Day" : HourStart > 0 || HourEnd > 0 ? "Hour" : null;
}
