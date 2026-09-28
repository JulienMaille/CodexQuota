using System;
using CodexQuota;
using CodexQuota.Usage;

namespace CodexQuota.Tests;

/// <summary>
/// Pins the adaptive poll cadence: fast while the flyout is open or Codex is running, then back off
/// by how recently the flyout was used, mirroring CodexBar's adaptive-refresh decision table.
/// </summary>
public class AdaptiveRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void NeverOpenedAndIdleUsesLongIdleDelay()
        => Assert.Equal(TimeSpan.FromMinutes(10), AdaptiveRefreshPolicy.NextDelay(false, null, Now, codexRunning: false));

    [Fact]
    public void OpenFlyoutStaysOnFastCadence()
        => Assert.Equal(TimeSpan.FromSeconds(60), AdaptiveRefreshPolicy.NextDelay(true, null, Now, codexRunning: false));

    [Fact]
    public void RunningCodexKeepsItFreshEvenWhenLongIdle()
        => Assert.Equal(TimeSpan.FromSeconds(60), AdaptiveRefreshPolicy.NextDelay(false, Now.AddHours(-8), Now, codexRunning: true));

    [Fact]
    public void RecentInteractionUsesActiveDelay()
        => Assert.Equal(
            TimeSpan.FromSeconds(60),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddMinutes(-4), Now, codexRunning: false));

    [Fact]
    public void WarmWindowUsesFiveMinutes()
        => Assert.Equal(
            TimeSpan.FromMinutes(5),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddMinutes(-30), Now, codexRunning: false));

    [Fact]
    public void IdleWindowUsesFiveMinutes()
        => Assert.Equal(
            TimeSpan.FromMinutes(5),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddHours(-2), Now, codexRunning: false));

    [Fact]
    public void LongIdleUsesTenMinutes()
        => Assert.Equal(
            TimeSpan.FromMinutes(10),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddHours(-6), Now, codexRunning: false));

    [Fact]
    public void ClockSkewReadsAsRecentInteraction()
        // A future timestamp ages negative, which falls inside the 5-minute active window: the
        // cadence stays fresh rather than backing off into a 10-minute hole.
        => Assert.Equal(
            TimeSpan.FromSeconds(60),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddMinutes(2), Now, codexRunning: false));

    [Fact]
    public void ResetSoonerThanPolicyShortensDelayToResetPlusGrace()
    {
        var snapshot = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddMinutes(3)));
        TimeSpan delay = AdaptiveRefreshPolicy.NextDelayWithReset(TimeSpan.FromMinutes(10), snapshot, Now);
        Assert.Equal(TimeSpan.FromMinutes(3) + AdaptiveRefreshPolicy.ResetGrace, delay);
    }

    [Fact]
    public void ResetBeyondPolicyLeavesDelayUntouched()
    {
        var snapshot = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddHours(1)));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            AdaptiveRefreshPolicy.NextDelayWithReset(TimeSpan.FromMinutes(10), snapshot, Now));
    }

    [Fact]
    public void NoSnapshotLeavesDelayUntouched()
        => Assert.Equal(
            TimeSpan.FromMinutes(10),
            AdaptiveRefreshPolicy.NextDelayWithReset(TimeSpan.FromMinutes(10), null, Now));

    [Fact]
    public void PastResetsAreIgnoredWhenFindingTheNextDelay()
    {
        var snapshot = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddMinutes(-1)));
        Assert.Equal(
            TimeSpan.FromMinutes(10),
            AdaptiveRefreshPolicy.NextDelayWithReset(TimeSpan.FromMinutes(10), snapshot, Now));
    }

    [Fact]
    public void ImminentResetSchedulesAtResetPlusGraceWithoutRoundingUp()
    {
        // Reset in 2s lands 32s out: served as-is (reset + grace), neither rounded up nor pushed
        // out — the reset path keeps its own 5s floor instead of the 30s policy MinimumDelay, so a
        // just-missed reset still refreshes promptly.
        var snapshot = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddSeconds(2)));
        TimeSpan delay = AdaptiveRefreshPolicy.NextDelayWithReset(TimeSpan.FromMinutes(10), snapshot, Now);
        Assert.Equal(TimeSpan.FromSeconds(32), delay);
        Assert.True(delay >= AdaptiveRefreshPolicy.ResetMinimumDelay);
    }

    [Fact]
    public void EarliestFutureResetPicksMinimumAcrossAllWindows()
    {
        var snapshot = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddMinutes(5)))
        {
            Secondary = new RateWindow(80, resetAt: Now.AddMinutes(2)),
            ModelSpecific = new RateWindow(70, resetAt: Now.AddMinutes(6)),
            Monthly = new RateWindow(60, resetAt: Now.AddMinutes(8)),
            Cost = new CostSnapshot(1, "USD", "spend").WithResetsAt(Now.AddMinutes(7)),
            ResetCredits = new ResetCreditsSnapshot(1, new[]
            {
                new ResetCreditGrant("active", Now.AddDays(-1), Now.AddMinutes(9)),
            }),
        };
        snapshot.ExtraRateWindows.Add(new NamedRateWindow("extra", "Extra", new RateWindow(10, resetAt: Now.AddMinutes(4))));

        Assert.Equal(Now.AddMinutes(2), AdaptiveRefreshPolicy.EarliestFutureReset(snapshot, Now));
    }

    [Fact]
    public void EarliestFutureResetWithNoResetsReturnsNull()
    {
        var snapshot = new UsageSnapshot(new RateWindow(90));
        Assert.Null(AdaptiveRefreshPolicy.EarliestFutureReset(snapshot, Now));
        Assert.Null(AdaptiveRefreshPolicy.EarliestFutureReset(null, Now));
    }

    [Fact]
    public void HasCrossedResetDetectsResetBetweenFetchAndNow()
    {
        var snapshot = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddMinutes(-3)));
        Assert.True(AdaptiveRefreshPolicy.HasCrossedReset(snapshot, Now.AddMinutes(-6), Now));
    }

    [Fact]
    public void HasCrossedResetIgnoresFutureAndPreFetchResets()
    {
        var future = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddMinutes(3)));
        Assert.False(AdaptiveRefreshPolicy.HasCrossedReset(future, Now.AddMinutes(-6), Now));

        var old = new UsageSnapshot(new RateWindow(90, resetAt: Now.AddMinutes(-9)));
        Assert.False(AdaptiveRefreshPolicy.HasCrossedReset(old, Now.AddMinutes(-6), Now));

        Assert.False(AdaptiveRefreshPolicy.HasCrossedReset(null, Now.AddMinutes(-6), Now));
    }
}