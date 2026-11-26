namespace Ro3Emu.Server.Game;

/// <summary>
/// The realm's 05:00 local daily and Monday-05:00 weekly reset calendar.
/// Every local wall time is resolved against the target wall time's own zone offset rather than the current
/// instant's offset, so day and week boundaries remain at 05:00 local across DST transitions.
/// </summary>
public sealed class GameCalendar(TimeProvider clock)
{
    public const int ResetHour = 5;

    public TimeProvider Clock => clock;
    public TimeZoneInfo Zone => clock.LocalTimeZone;

    /// <summary>Local civil date of the game day containing <paramref name="local"/> (turns at 05:00).</summary>
    public static DateOnly GameDayOf(DateTime local) =>
        DateOnly.FromDateTime(local.TimeOfDay >= TimeSpan.FromHours(ResetHour) ? local.Date : local.Date.AddDays(-1));

    /// <summary>Local civil date of the game day containing <paramref name="utcInstant"/> in <paramref name="zone"/>.</summary>
    public static DateOnly GameDayOf(DateTimeOffset utcInstant, TimeZoneInfo zone) =>
        GameDayOf(TimeZoneInfo.ConvertTime(utcInstant, zone).DateTime);

    /// <summary>Local civil date of the Monday of the game week (Monday 05:00) containing <paramref name="local"/>.</summary>
    public static DateOnly GameWeekOf(DateTime local) => MondayOf(GameDayOf(local));

    /// <summary>The Monday on or before <paramref name="day"/>.</summary>
    public static DateOnly MondayOf(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    /// <summary>The Monday <see cref="DateOnly.DayNumber"/> on or before <paramref name="dayNumber"/>.</summary>
    public static long MondayDayNumber(long dayNumber) =>
        MondayOf(DateOnly.FromDayNumber(checked((int)dayNumber))).DayNumber;

    /// <summary>Converts an unspecified local wall time to a <see cref="DateTimeOffset"/> in <paramref name="zone"/>,
    /// stepping forward if a DST gap skips the wall time.</summary>
    public static DateTimeOffset ToZoneOffset(DateTime local, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(wall))
            wall = wall.AddHours(1);
        return new DateTimeOffset(wall, zone.GetUtcOffset(wall));
    }

    public static long ToUnixSeconds(DateTime local, TimeZoneInfo zone) => ToZoneOffset(local, zone).ToUnixTimeSeconds();
    public static long ToUnixMilliseconds(DateTime local, TimeZoneInfo zone) => ToZoneOffset(local, zone).ToUnixTimeMilliseconds();

    /// <summary>The 05:00 local instant starting <paramref name="day"/> in <paramref name="zone"/>.</summary>
    public static DateTimeOffset DayStartOf(DateOnly day, TimeZoneInfo zone) =>
        ToZoneOffset(day.ToDateTime(new TimeOnly(ResetHour, 0)), zone);

    /// <summary>Current game day in <paramref name="clock"/>'s local zone.</summary>
    public static DateOnly CurrentDay(TimeProvider clock) => GameDayOf(clock.GetLocalNow().DateTime);

    /// <summary>Current game day as a <see cref="DateOnly.DayNumber"/>.</summary>
    public static long CurrentDayNumber(TimeProvider clock) => CurrentDay(clock).DayNumber;

    /// <summary>Current game day and Monday week day number in <paramref name="clock"/>'s local zone.</summary>
    public static (long Day, long Week) CurrentPeriod(TimeProvider clock)
    {
        var day = CurrentDay(clock);
        return (day.DayNumber, MondayOf(day).DayNumber);
    }

    /// <summary>Current 05:00 local game-day boundary in <paramref name="clock"/>'s local zone.</summary>
    public static DateTimeOffset DayStart(TimeProvider clock) => DayStartOf(CurrentDay(clock), clock.LocalTimeZone);

    /// <summary>Next 05:00 local game-day boundary in <paramref name="clock"/>'s local zone.</summary>
    public static DateTimeOffset NextDayStart(TimeProvider clock) => DayStartOf(CurrentDay(clock).AddDays(1), clock.LocalTimeZone);

    /// <summary>Current Monday 05:00 local game-week boundary in <paramref name="clock"/>'s local zone.</summary>
    public static DateTimeOffset WeekStart(TimeProvider clock) => DayStartOf(MondayOf(CurrentDay(clock)), clock.LocalTimeZone);

    /// <summary>Previous Monday 05:00 local game-week boundary in <paramref name="clock"/>'s local zone (DST-safe).</summary>
    public static DateTimeOffset PreviousWeekStart(TimeProvider clock) => DayStartOf(MondayOf(CurrentDay(clock)).AddDays(-7), clock.LocalTimeZone);

    /// <summary>1-based server day since <paramref name="seasonStart"/> (local civil date, clamped to at least 1).</summary>
    public static int ServerDay(TimeProvider clock, DateTime seasonStart) =>
        Math.Max(1, (clock.GetLocalNow().Date - seasonStart.Date).Days + 1);

    public DateOnly Today => CurrentDay(clock);
    public long TodayNumber => CurrentDayNumber(clock);
    public DateTimeOffset TodayStart => DayStart(clock);
    public DateTimeOffset TomorrowStart => NextDayStart(clock);
    public DateTimeOffset ThisWeekStart => WeekStart(clock);
    public DateTimeOffset LastWeekStart => PreviousWeekStart(clock);
    public int ServerDaySince(DateTime seasonStart) => ServerDay(clock, seasonStart);
}
