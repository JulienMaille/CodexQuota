using System;
using System.Globalization;

namespace CodexQuota;

/// <summary>Result of a pace projection. The English label is retained for culture-independent
/// tests and diagnostics; the UI uses the semantic fields to render the selected language.</summary>
public sealed record PaceLineResult(
    string Label,
    double RemainingPercent,
    double RatePercentPerDay,
    bool CapReached,
    bool WillExhaustBeforeReset,
    double DaysToCap,
    DateTimeOffset ResetAt,
    DateTimeOffset? CapAt);

/// <summary>
/// Projects the weekly quota's exhaustion date from the rate window's used-percent trajectory and
/// renders the one-glance "where are we going" line for the flyout (design: docs/pace-eta-line.md).
///
/// Burning faster than the week's allowance makes "resets in Nd" misleading - at current pace the
/// cap is hit in 2 days, not 7. The line states exactly that: "Pace ~42% quota/day -> cap Wed".
/// No notifications or charts; hidden whenever any input is missing.
///
/// Math (all from data this app already fetches):
///   - used-percent slope: the weekly window reports {@link RateWindow.UsedPercent} used after
///     (now - windowStart) elapsed in a window of {@link RateWindow.WindowMinutes} minutes. Each
///     24-hour quota day contributes at most the configured workday hours, so idle hours do not
///     inflate the daily rate. The pace is a straight line to 100%: daysToCap =
///     (100-used)*elapsedWorkdays/used. The projection only becomes a warning when actual usage is
///     more than two percentage points ahead of ideal workday usage; this keeps a small first sample
///     from dominating the estimate.
///   - the displayed rate is the same quota slope, expressed as percentage points of quota per
///     assumed workday.
///   - only a materially-ahead pace that exhausts before reset folds into the configured 50/20
///     thresholds via {@link QuotaDisplay.BrushKeyForRemaining}; all other live pace is neutral.
///
/// Hide (null) when: no weekly window / reset boundary, a window that has not started, or nothing
/// used yet.
/// </summary>
public static class PaceLine
{
    /// <summary>Ignore small positive deviations from the ideal time-based pace.</summary>
    public const double OnTrackDeltaPercent = 2;

    public static PaceLineResult? Compute(
        double weeklyUsedPercent,                        // 0..100, from the weekly RateWindow
        DateTimeOffset? weeklyResetAt,                   // weekly RateWindow.ResetAt
        int weeklyWindowMinutes,                         // weekly RateWindow.WindowMinutes
        DateTimeOffset now,
        int workdayHours = PaceSettings.DefaultWorkdayHours)
    {
        if (weeklyResetAt is not { } resetAt || resetAt <= now)
            return null;
        if (weeklyUsedPercent <= 0)
            return null;

        workdayHours = Math.Clamp(workdayHours, 1, 24);
        double spanDays = weeklyWindowMinutes > 0 ? weeklyWindowMinutes / 1440.0 : 7.0;
        var windowStart = resetAt.AddDays(-spanDays);
        double elapsedDays = (now - windowStart).TotalDays;
        if (elapsedDays <= 1e-9)
            return null;

        double elapsedWorkdays = WorkdaysElapsed(elapsedDays, workdayHours);
        if (elapsedWorkdays <= 1e-9)
            return null;

        double used = Math.Clamp(weeklyUsedPercent, 0, 100);
        double expectedUsed = Math.Clamp(elapsedWorkdays / spanDays * 100, 0, 100);
        bool materiallyAhead = used - expectedUsed > OnTrackDeltaPercent;
        double burnPerDay = used / elapsedWorkdays;
        double daysToCap = (100 - used) / burnPerDay;

        bool burned = used >= 100 || daysToCap <= 0;
        double daysToReset = (resetAt - now).TotalDays;
        bool willExhaustBeforeReset = !burned && materiallyAhead && daysToCap < daysToReset;
        DateTimeOffset? capAt = willExhaustBeforeReset ? now.AddDays(daysToCap) : null;
        double remaining = burned
            ? 0
            : willExhaustBeforeReset
                ? Math.Min(100, daysToCap / Math.Max(1, daysToReset) * 100)
                : 100;

        string rateLabel = FormatQuotaRate(burnPerDay);
        string label;
        if (burned)
            label = $"Pace · cap reached · resets {DayName(resetAt)}";
        else if (willExhaustBeforeReset)
            label = $"Pace ~{rateLabel}% quota/day → cap {DayName(capAt!.Value)} (~{daysToCap.ToString("0.#", CultureInfo.InvariantCulture)}d)";
        else
            label = $"Pace ~{rateLabel}% quota/day · resets before cap";

        return new PaceLineResult(
            label,
            remaining,
            burnPerDay,
            burned,
            willExhaustBeforeReset,
            daysToCap,
            resetAt,
            capAt);
    }

    private static string DayName(DateTimeOffset when)
        => when.ToLocalTime().ToString("dddd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Counts one assumed workday per 24-hour quota day. The current partial day contributes its
    /// elapsed hours divided by the configured workday, capped at one workday.
    /// </summary>
    private static double WorkdaysElapsed(double elapsedDays, int workdayHours)
    {
        double fullDays = Math.Floor(elapsedDays);
        double partialDayHours = (elapsedDays - fullDays) * 24;
        return fullDays + Math.Min(1, partialDayHours / workdayHours);
    }

    internal static string FormatQuotaRate(double percentPerDay)
    {
        if (percentPerDay >= 10)
            return percentPerDay.ToString("0", CultureInfo.InvariantCulture);
        if (percentPerDay >= 1)
            return percentPerDay.ToString("0.0", CultureInfo.InvariantCulture);
        return percentPerDay.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
