using System;
using CodexQuota;

namespace CodexQuota.Tests;

/// <summary>
/// Codex surfaces quota as *remaining* (its CLI warns "you have X% of your weekly limit remaining"),
/// so the widget renders 100 − used everywhere. These tests pin the remaining math, the urgency brush
/// thresholds (defaults white >50%, amber ≤50%, red ≤20% — the CodexBar scheme), and the
/// imminent-reset date switch.
/// </summary>
public class QuotaDisplayTests
{
    /// <summary>
    /// Captures the registry-backed threshold pair and restores it on dispose. The tests pin 50/20
    /// (or 0) for deterministic brush math, and must not leave the developer's real CodexQuota
    /// settings overwritten (WriteInt persists to HKCU\Software\CodexQuota).
    /// </summary>
    private sealed class ThresholdSnapshot : IDisposable
    {
        private readonly int _upper;
        private readonly int _lower;

        public ThresholdSnapshot()
        {
            _upper = WidgetAppearanceSettings.WarningUpperPercent;
            _lower = WidgetAppearanceSettings.WarningLowerPercent;
        }

        public void Dispose()
        {
            WidgetAppearanceSettings.WarningUpperPercent = _upper;
            WidgetAppearanceSettings.WarningLowerPercent = _lower;
        }
    }

    [Theory]
    [InlineData(18, 82)]
    [InlineData(0, 100)]
    [InlineData(100, 0)]
    [InlineData(142, 0)]
    [InlineData(-7, 100)]
    public void RemainingPercentFlipsAndClamps(double used, double expected)
        => Assert.Equal(expected, QuotaDisplay.RemainingPercent(used));

    [Theory]
    [InlineData(100, "TextFillColorPrimaryBrush")]
    [InlineData(51, "TextFillColorPrimaryBrush")]
    [InlineData(50, "SystemFillColorCautionBrush")]
    [InlineData(21, "SystemFillColorCautionBrush")]
    [InlineData(20, "SystemFillColorCriticalBrush")]
    [InlineData(0, "SystemFillColorCriticalBrush")]
    public void BrushKeyUsesConfiguredThresholds(double remaining, string expectedKey)
    {
        // Pin the 50/20 defaults (CodexBar scheme) explicitly so the assertion is independent of any
        // registry values on the machine running the tests.
        using var restore = new ThresholdSnapshot();
        WidgetAppearanceSettings.WarningUpperPercent = 50;
        WidgetAppearanceSettings.WarningLowerPercent = 20;
        Assert.Equal(expectedKey, QuotaDisplay.BrushKeyForRemaining(remaining));
    }

    [Fact]
    public void WarningThresholdsAscendAndExcludeZero()
    {
        using var restore = new ThresholdSnapshot();
        WidgetAppearanceSettings.WarningUpperPercent = 50;
        WidgetAppearanceSettings.WarningLowerPercent = 20;
        Assert.Equal(new[] { 20, 50 }, QuotaDisplay.WarningThresholds());
    }

    [Fact]
    public void ZeroLowerThresholdYieldsSingleMarker()
    {
        using var restore = new ThresholdSnapshot();
        WidgetAppearanceSettings.WarningUpperPercent = 50;
        WidgetAppearanceSettings.WarningLowerPercent = 0;
        Assert.Equal(new[] { 50 }, QuotaDisplay.WarningThresholds());
    }

    [Fact]
    public void ThresholdBoxWritesClampToTheValuesTheReSeedDisplays()
    {
        // Pins the FlyoutWindow read side: after any write the threshold boxes are re-seeded from
        // these getters, so what the user sees is what is applied. Typing 80 into the lower box
        // stores 80 but every read (and the re-seeded box) shows upper-1; raising the upper box
        // later moves the effective lower again — the getters are the single source of truth.
        using var restore = new ThresholdSnapshot();
        WidgetAppearanceSettings.WarningUpperPercent = 50;
        WidgetAppearanceSettings.WarningLowerPercent = 20;

        WidgetAppearanceSettings.WarningLowerPercent = 80;
        Assert.Equal(49, WidgetAppearanceSettings.WarningLowerPercent);
        Assert.Equal(50, WidgetAppearanceSettings.WarningUpperPercent);

        WidgetAppearanceSettings.WarningUpperPercent = 60;
        Assert.Equal(59, WidgetAppearanceSettings.WarningLowerPercent);

        WidgetAppearanceSettings.WarningUpperPercent = 0;
        Assert.Equal(1, WidgetAppearanceSettings.WarningUpperPercent);
        Assert.Equal(0, WidgetAppearanceSettings.WarningLowerPercent);
    }

    [Fact]
    public void ResetBeyondTheWindowShowsCountdown()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.False(ResetDateDisplay.IsImminent(now.AddHours(24).AddMinutes(1), now));
    }

    [Fact]
    public void ResetInsideTheImminentWindowShowsTheDate()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.True(ResetDateDisplay.IsImminent(now.AddHours(23), now));
    }

    [Fact]
    public void PastResetIsNotImminent()
    {
        var now = new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
        Assert.False(ResetDateDisplay.IsImminent(now.AddHours(-1), now));
    }

    [Fact]
    public void NullResetAtIsNeverImminent()
        => Assert.False(ResetDateDisplay.IsImminent(null, DateTimeOffset.UtcNow));

    [Fact]
    public void LocalDateFormatIncludesDay()
    {
        var when = new DateTimeOffset(2026, 8, 6, 21, 28, 0, TimeSpan.Zero);
        // Month/day names and 12/24h style follow the OS locale; the day is stable.
        Assert.Contains("6", ResetDateDisplay.FormatLocalDate(when));
    }
}
