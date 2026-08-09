using System;
using CodexQuota;

namespace CodexQuota.Tests;

/// <summary>
/// Pins the pace projection math: the weekly window's used-percent slope extrapolated to the cap,
/// the percentage-points-per-day label, and the hide rules (no weekly window, nothing used yet).
/// Also pins the RemainingPercent output that drives the shared 50/20 urgency brush.
/// </summary>
public class PaceLineTests
{
    // Saturday 2026-08-08 12:00 UTC; all times in the same week.
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private const int WeekMinutes = 7 * 24 * 60;

    [Fact]
    public void SteadyBurnProjectsCapDayInsideTheWeek()
    {
        // Reset Friday 08-14 00:00Z → window started 08-07; used 75% after 1.5 days.
        var reset = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        var result = PaceLine.Compute(75, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        // Workday model (8h/day): 1.5 elapsed days = 2 workdays → pace = 75/2 = 37.5%/day;
        // daysToCap = 25/37.5 ≈ 0.67d; daysToReset = 5.5d → 0.67/5.5 ≈ 12.1%.
        Assert.Equal(12.1, result!.RemainingPercent, precision: 1);
        Assert.Contains("Pace ~38% quota/day", result.Label);
        Assert.Contains("cap", result.Label);
    }

    [Fact]
    public void SlowBurnResetsBeforeTheCap()
    {
        // Reset Monday 08-10 00:00Z; used only 20% after 5.5 days → cap far past reset.
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var result = PaceLine.Compute(20, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Equal(100, result!.RemainingPercent, precision: 1);
        Assert.Contains("3.3% quota/day", result.Label);
        Assert.Contains("resets before cap", result.Label);
    }

    [Fact]
    public void FullyUsedWindowIsBurned()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var result = PaceLine.Compute(100, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Equal(0, result!.RemainingPercent);
        Assert.Contains("cap reached", result.Label);
    }

    [Fact]
    public void PaceDoesNotNeedProfileTokenHistory()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var result = PaceLine.Compute(50, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Contains("% quota/day", result!.Label);
        Assert.DoesNotContain("tok/day", result.Label);
    }

    [Fact]
    public void NothingUsedYetHidesTheLine()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(PaceLine.Compute(0, reset, WeekMinutes, Now));
    }

    [Fact]
    public void SmallUsageImmediatelyAfterResetDoesNotProjectAFalseRunout()
    {
        // One percent during the first 25 minutes is within the two-point on-track band. A
        // projection from that tiny sample would claim the quota is exhausted in ~5.2 days
        // (workday model: 25 min = 0.052 workdays → 99 × 0.052 ≈ 5.2d), still before the reset.
        var reset = Now.AddDays(6).AddHours(23).AddMinutes(35);
        var result = PaceLine.Compute(1, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Equal(100, result!.RemainingPercent);
        Assert.Contains("resets before cap", result.Label);
        Assert.DoesNotContain("→ cap", result.Label);
    }

    [Fact]
    public void MissingResetBoundaryHidesTheLine()
    {
        Assert.Null(PaceLine.Compute(50, null, WeekMinutes, Now));
    }

    [Fact]
    public void PastResetHidesTheLine()
    {
        var reset = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(PaceLine.Compute(50, reset, WeekMinutes, Now));
    }

    [Fact]
    public void RateUsesPercentagePointsPerDay()
    {
        // Reset five days from now means the seven-day window started two days ago. At 10.4% used,
        // the current quota slope is 5.2 percentage points per day.
        var reset = Now.AddDays(5);
        var result = PaceLine.Compute(10.4, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Contains("5.2% quota/day", result!.Label);
    }

    [Fact]
    public void RateFormatsSmallPercentagePoints()
    {
        var reset = Now.AddDays(5);
        var result = PaceLine.Compute(0.28, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Contains("0.14% quota/day", result!.Label);
    }

    [Fact]
    public void WorkdayHoursScalePartialDayInsteadOfTreatingItAsTwentyFourHourPace()
    {
        // The window started four hours ago. Seven percent in half an assumed workday is 14%/day,
        // not 42%/day when the same sample is annualized over a 24-hour day.
        var reset = Now.AddDays(6).AddHours(20);
        var result = PaceLine.Compute(7, reset, WeekMinutes, Now, workdayHours: 8);

        Assert.NotNull(result);
        Assert.Contains("14% quota/day", result!.Label);
        Assert.Equal(100, result.RemainingPercent);
    }
}
