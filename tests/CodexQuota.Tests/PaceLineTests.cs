using System;
using System.Collections.Generic;
using CodexQuota;
using CodexQuota.Usage;

namespace CodexQuota.Tests;

/// <summary>
/// Pins the pace projection math (docs/pace-eta-line.md): the weekly window's used-percent slope
/// extrapolated to the cap, the profile bucket mean as the "tok/day" label, and the hide rules
/// (thin history, no weekly window, nothing used yet, sub-10 tok/day). Also pins the RemainingPercent
/// output that drives the shared 50/20 urgency brush.
/// </summary>
public class PaceLineTests
{
    // Saturday 2026-08-08 12:00 UTC; all times in the same week.
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
    private const int WeekMinutes = 7 * 24 * 60;

    private static ProfileUsageBucket Bucket(int day, long tokens)
        => new($"2026-08-{day:00}", tokens);

    /// <summary>Newest-last bucket list for a steady 4.0k/day burn across the last week.</summary>
    private static IReadOnlyList<ProfileUsageBucket> SteadyWeek(long tokens = 4000)
        => new[]
        {
            Bucket(1, tokens),
            Bucket(2, tokens),
            Bucket(3, tokens),
            Bucket(4, tokens),
            Bucket(5, tokens),
            Bucket(6, tokens),
            Bucket(7, tokens),
        };

    [Fact]
    public void SteadyBurnProjectsCapDayInsideTheWeek()
    {
        // Reset Friday 08-14 00:00Z → window started 08-07; used 75% after 1.5 days.
        var reset = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero);
        var result = PaceLine.Compute(SteadyWeek(), 75, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        // daysToCap = 25 / (75/1.5) = 0.5d; daysToReset = 5.5d → 0.5/5.5 ≈ 9.1%.
        Assert.Equal(9.1, result!.RemainingPercent, precision: 1);
        Assert.Contains("Pace", result.Label);
        Assert.Contains("tok/day", result.Label);
        // Cap lands ~0.5d out, well before Friday's reset → the "cap" projection branch.
        Assert.Contains("cap", result.Label);
    }

    [Fact]
    public void SlowBurnResetsBeforeTheCap()
    {
        // Reset Monday 08-10 00:00Z; used only 20% after 5.5 days → cap far past reset.
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var result = PaceLine.Compute(SteadyWeek(), 20, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Equal(100, result!.RemainingPercent, precision: 1);
        Assert.Contains("resets before cap", result.Label);
    }

    [Fact]
    public void FullyUsedWindowIsBurned()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var result = PaceLine.Compute(SteadyWeek(), 100, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Equal(0, result!.RemainingPercent);
        Assert.Contains("cap reached", result.Label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ThinHistoryHidesTheLine(int bucketCount)
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var buckets = new List<ProfileUsageBucket>();
        for (int i = 0; i < bucketCount; i++)
            buckets.Add(Bucket(7 - i, 4000));

        Assert.Null(PaceLine.Compute(buckets, 50, reset, WeekMinutes, Now));
    }

    [Fact]
    public void NothingUsedYetHidesTheLine()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(PaceLine.Compute(SteadyWeek(), 0, reset, WeekMinutes, Now));
    }

    [Fact]
    public void MissingResetBoundaryHidesTheLine()
    {
        Assert.Null(PaceLine.Compute(SteadyWeek(), 50, null, WeekMinutes, Now));
    }

    [Fact]
    public void PastResetHidesTheLine()
    {
        var reset = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero);
        Assert.Null(PaceLine.Compute(SteadyWeek(), 50, reset, WeekMinutes, Now));
    }

    [Fact]
    public void NearZeroDailyBurnHidesTheLine()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        var buckets = new[] { Bucket(6, 5), Bucket(7, 7) };
        Assert.Null(PaceLine.Compute(buckets, 50, reset, WeekMinutes, Now));
    }

    [Fact]
    public void RateUsesOnlyTheLastSevenBuckets()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        // Nine buckets, newest last: two old at 1000, seven recent at 9000. The rate must use the
        // last seven (all 9000 → "9.0k"); taking all nine would dilute the mean to ~7.2k.
        var buckets = new List<ProfileUsageBucket>
        {
            Bucket(1, 1000),
            Bucket(2, 1000),
            Bucket(3, 9000),
            Bucket(4, 9000),
            Bucket(5, 9000),
            Bucket(6, 9000),
            Bucket(7, 9000),
            Bucket(8, 9000),
            Bucket(9, 9000),
        };
        var result = PaceLine.Compute(buckets, 50, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Contains("9.0k", result!.Label);
    }

    [Fact]
    public void RateFormatsKAndPlainTokens()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);

        Assert.Contains("5.2k", PaceLine.Compute(
            new[] { Bucket(6, 5200), Bucket(7, 5200) }, 50, reset, WeekMinutes, Now)!.Label);
        Assert.Contains("10k", PaceLine.Compute(
            new[] { Bucket(6, 10_400), Bucket(7, 10_400) }, 50, reset, WeekMinutes, Now)!.Label);
        Assert.Contains("950", PaceLine.Compute(
            new[] { Bucket(6, 950), Bucket(7, 950) }, 50, reset, WeekMinutes, Now)!.Label);
    }

    [Fact]
    public void RestDaysLowerTheMean()
    {
        var reset = new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        // 4000, 0, 4000 → mean 2667, not 4000.
        var buckets = new[] { Bucket(5, 4000), Bucket(6, 0), Bucket(7, 4000) };
        var result = PaceLine.Compute(buckets, 50, reset, WeekMinutes, Now);

        Assert.NotNull(result);
        Assert.Contains("2.7k", result!.Label);
    }
}