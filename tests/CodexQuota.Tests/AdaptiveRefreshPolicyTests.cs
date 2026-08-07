using System;
using CodexQuota;

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
        => Assert.Equal(TimeSpan.FromMinutes(30), AdaptiveRefreshPolicy.NextDelay(false, null, Now, codexRunning: false));

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
    public void IdleWindowUsesFifteenMinutes()
        => Assert.Equal(
            TimeSpan.FromMinutes(15),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddHours(-2), Now, codexRunning: false));

    [Fact]
    public void LongIdleUsesThirtyMinutes()
        => Assert.Equal(
            TimeSpan.FromMinutes(30),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddHours(-6), Now, codexRunning: false));

    [Fact]
    public void ClockSkewReadsAsRecentInteraction()
        // A future timestamp ages negative, which falls inside the 5-minute active window: the
        // cadence stays fresh rather than backing off into a 30-minute hole.
        => Assert.Equal(
            TimeSpan.FromSeconds(60),
            AdaptiveRefreshPolicy.NextDelay(false, Now.AddMinutes(2), Now, codexRunning: false));
}