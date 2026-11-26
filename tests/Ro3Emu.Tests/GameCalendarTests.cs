using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class GameCalendarTests
{
    private static readonly TimeZoneInfo Warsaw = TimeZoneInfo.FindSystemTimeZoneById("Europe/Warsaw");
    private static readonly TimeZoneInfo UtcPlus8 = TimeZoneInfo.CreateCustomTimeZone("UTC+08", TimeSpan.FromHours(8), "UTC+08", "UTC+08");

    [Fact]
    public void DayTurnsAtFiveLocalInUtcPlus8()
    {
        // 2026-06-10 04:59:59 +08:00 belongs to game day 2026-06-09 (started 2026-06-09 05:00 +08:00 = 2026-06-08 21:00 UTC).
        var before = new FixedZoneClock(new DateTimeOffset(2026, 6, 10, 4, 59, 59, TimeSpan.FromHours(8)), UtcPlus8);
        var calBefore = new GameCalendar(before);
        Assert.Equal(new DateOnly(2026, 6, 9), calBefore.Today);
        Assert.Equal(new DateTimeOffset(2026, 6, 9, 5, 0, 0, TimeSpan.FromHours(8)), calBefore.TodayStart);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 5, 0, 0, TimeSpan.FromHours(8)), calBefore.TomorrowStart);

        // One second later: 2026-06-10 05:00:00 +08:00 starts game day 2026-06-10.
        var atReset = new FixedZoneClock(new DateTimeOffset(2026, 6, 10, 5, 0, 0, TimeSpan.FromHours(8)), UtcPlus8);
        var calAt = new GameCalendar(atReset);
        Assert.Equal(new DateOnly(2026, 6, 10), calAt.Today);
        Assert.Equal(new DateTimeOffset(2026, 6, 10, 5, 0, 0, TimeSpan.FromHours(8)), calAt.TodayStart);
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 5, 0, 0, TimeSpan.FromHours(8)), calAt.ThisWeekStart);
        Assert.Equal(new DateTimeOffset(2026, 6, 1, 5, 0, 0, TimeSpan.FromHours(8)), calAt.LastWeekStart);
        Assert.Equal(10, calAt.ServerDaySince(new DateTime(2026, 6, 1)));
    }

    [Fact]
    public void WarsawSpringForwardDstUsesTargetWallTimeOffset()
    {
        // Poland springs forward on Sunday 2026-03-29 at 02:00 CET (+01) -> 03:00 CEST (+02).
        // At 04:59 CEST, game day is still Saturday 2026-03-28 whose 05:00 start was in CET (+01), 23 hours earlier.
        var beforeReset = new FixedZoneClock(new DateTimeOffset(2026, 3, 29, 4, 59, 0, TimeSpan.FromHours(2)), Warsaw);
        Assert.Equal(new DateOnly(2026, 3, 28), GameCalendar.CurrentDay(beforeReset));
        Assert.Equal(new DateTimeOffset(2026, 3, 28, 5, 0, 0, TimeSpan.FromHours(1)), GameCalendar.DayStart(beforeReset));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 5, 0, 0, TimeSpan.FromHours(2)), GameCalendar.NextDayStart(beforeReset));
        Assert.Equal(TimeSpan.FromHours(23), GameCalendar.NextDayStart(beforeReset) - GameCalendar.DayStart(beforeReset));

        // At 06:00 CEST on Sunday, WeekStart must still be Monday 2026-03-23 05:00 CET (+01), not +02.
        var sundayMorning = new FixedZoneClock(new DateTimeOffset(2026, 3, 29, 6, 0, 0, TimeSpan.FromHours(2)), Warsaw);
        Assert.Equal(new DateOnly(2026, 3, 29), GameCalendar.CurrentDay(sundayMorning));
        Assert.Equal(new DateTimeOffset(2026, 3, 29, 5, 0, 0, TimeSpan.FromHours(2)), GameCalendar.DayStart(sundayMorning));
        Assert.Equal(new DateTimeOffset(2026, 3, 23, 5, 0, 0, TimeSpan.FromHours(1)), GameCalendar.WeekStart(sundayMorning));

        // On Monday 2026-03-30 06:00 CEST, PreviousWeekStart is 167 hours before WeekStart (spring-forward week).
        var nextMonday = new FixedZoneClock(new DateTimeOffset(2026, 3, 30, 6, 0, 0, TimeSpan.FromHours(2)), Warsaw);
        var thisWeek = GameCalendar.WeekStart(nextMonday);
        var prevWeek = GameCalendar.PreviousWeekStart(nextMonday);
        Assert.Equal(new DateTimeOffset(2026, 3, 30, 5, 0, 0, TimeSpan.FromHours(2)), thisWeek);
        Assert.Equal(new DateTimeOffset(2026, 3, 23, 5, 0, 0, TimeSpan.FromHours(1)), prevWeek);
        Assert.Equal(TimeSpan.FromHours(167), thisWeek - prevWeek);
    }

    [Fact]
    public void WarsawFallBackDstUsesTargetWallTimeOffset()
    {
        // Poland falls back on Sunday 2026-10-25 at 03:00 CEST (+02) -> 02:00 CET (+01).
        // At 04:59 CET, game day is still Saturday 2026-10-24 whose 05:00 start was in CEST (+02), 25 hours earlier.
        var beforeReset = new FixedZoneClock(new DateTimeOffset(2026, 10, 25, 4, 59, 0, TimeSpan.FromHours(1)), Warsaw);
        Assert.Equal(new DateOnly(2026, 10, 24), GameCalendar.CurrentDay(beforeReset));
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 5, 0, 0, TimeSpan.FromHours(2)), GameCalendar.DayStart(beforeReset));
        Assert.Equal(new DateTimeOffset(2026, 10, 25, 5, 0, 0, TimeSpan.FromHours(1)), GameCalendar.NextDayStart(beforeReset));
        Assert.Equal(TimeSpan.FromHours(25), GameCalendar.NextDayStart(beforeReset) - GameCalendar.DayStart(beforeReset));

        // At 06:00 CET on Sunday, WeekStart must still be Monday 2026-10-19 05:00 CEST (+02), not +01.
        var sundayMorning = new FixedZoneClock(new DateTimeOffset(2026, 10, 25, 6, 0, 0, TimeSpan.FromHours(1)), Warsaw);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.FromHours(2)), GameCalendar.WeekStart(sundayMorning));

        // On Monday 2026-10-26 06:00 CET, PreviousWeekStart is 169 hours before WeekStart (fall-back week).
        var nextMonday = new FixedZoneClock(new DateTimeOffset(2026, 10, 26, 6, 0, 0, TimeSpan.FromHours(1)), Warsaw);
        var thisWeek = GameCalendar.WeekStart(nextMonday);
        var prevWeek = GameCalendar.PreviousWeekStart(nextMonday);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 5, 0, 0, TimeSpan.FromHours(1)), thisWeek);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.FromHours(2)), prevWeek);
        Assert.Equal(TimeSpan.FromHours(169), thisWeek - prevWeek);
    }

    [Fact]
    public void MondayBeforeFiveBelongsToPreviousWeek()
    {
        var mondayPreReset = new FixedZoneClock(new DateTimeOffset(2026, 6, 15, 4, 59, 59, TimeSpan.FromHours(8)), UtcPlus8);
        var (day, week) = GameCalendar.CurrentPeriod(mondayPreReset);
        Assert.Equal(new DateOnly(2026, 6, 14).DayNumber, day);
        Assert.Equal(new DateOnly(2026, 6, 8).DayNumber, week);
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 5, 0, 0, TimeSpan.FromHours(8)), GameCalendar.WeekStart(mondayPreReset));

        var mondayAtReset = new FixedZoneClock(new DateTimeOffset(2026, 6, 15, 5, 0, 0, TimeSpan.FromHours(8)), UtcPlus8);
        var (day2, week2) = GameCalendar.CurrentPeriod(mondayAtReset);
        Assert.Equal(new DateOnly(2026, 6, 15).DayNumber, day2);
        Assert.Equal(new DateOnly(2026, 6, 15).DayNumber, week2);
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 5, 0, 0, TimeSpan.FromHours(8)), GameCalendar.WeekStart(mondayAtReset));
    }

    private sealed class FixedZoneClock(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow.ToUniversalTime();
        public override TimeZoneInfo LocalTimeZone => zone;
    }
}
