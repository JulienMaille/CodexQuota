using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexQuota;

/// <summary>
/// Shared display helpers for quota meters. Codex surfaces quota as the amount *remaining* (its CLI
/// warns "you have X% of your weekly limit remaining"), so every percentage and bar in the app
/// renders remaining, not consumed.
/// </summary>
public static class QuotaDisplay
{
    /// <summary>Remaining quota percent for a given consumed percent, clamped to 0..100.</summary>
    public static double RemainingPercent(double usedPercent)
        => Math.Clamp(100 - usedPercent, 0, 100);

    /// <summary>
    /// Active warning thresholds as remaining percents, ascending (e.g. [20, 50] for the 50/20
    /// defaults) and clamped to 1..99. Drives both the percent coloring and the bar markers.
    /// </summary>
    public static IReadOnlyList<int> WarningThresholds()
    {
        int upper = WidgetAppearanceSettings.WarningUpperPercent;
        int lower = WidgetAppearanceSettings.WarningLowerPercent;
        if (lower > 0)
            return new[] { lower, upper };
        return new[] { upper };
    }

    /// <summary>
    /// Brush key for a remaining percent: critical red at ≤ the lower threshold, caution amber at ≤
    /// the upper threshold, default text otherwise. Defaults (50/20) match CodexBar's urgency scheme.
    /// </summary>
    public static string BrushKeyForRemaining(double remainingPercent)
    {
        remainingPercent = Math.Clamp(remainingPercent, 0, 100);
        if (remainingPercent <= WidgetAppearanceSettings.WarningLowerPercent)
            return "SystemFillColorCriticalBrush";
        if (remainingPercent <= WidgetAppearanceSettings.WarningUpperPercent)
            return "SystemFillColorCautionBrush";
        return "TextFillColorPrimaryBrush";
    }
}

/// <summary>
/// Decides whether a reset countdown is close enough that the tile should show the concrete date
/// instead of "23h" — a reset "about to expire" is within 24 hours.
/// </summary>
public static class ResetDateDisplay
{
    /// <summary>Resets closer than this render as the absolute local date.</summary>
    public static readonly TimeSpan ImminentWindow = TimeSpan.FromHours(24);

    /// <summary>True when <paramref name="resetAt"/> is in the future and within the imminent window.</summary>
    public static bool IsImminent(DateTimeOffset? resetAt, DateTimeOffset now)
        => resetAt is { } when && when > now && when - now <= ImminentWindow;

    /// <summary>Short local date-time in the user's UI culture, e.g. "13 août 21:28".</summary>
    public static string FormatLocalDate(DateTimeOffset when)
        => when.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentUICulture);
}
