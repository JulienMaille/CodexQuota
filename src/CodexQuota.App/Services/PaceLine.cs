using System;
using System.Collections.Generic;
using System.Globalization;
using CodexQuota.Usage;

namespace CodexQuota;

/// <summary>Result of a pace projection: the one-line label plus the fraction of the remaining
/// week the current burn rate would exhaust, for the shared urgency-brush mapping.</summary>
public sealed record PaceLineResult(string Label, double RemainingPercent);

/// <summary>
/// Projects the weekly quota's exhaustion date from the rate window's used-percent trajectory and
/// the profile's daily token buckets, and renders the one-glance "where are we going" line for the
/// flyout (design: docs/pace-eta-line.md).
///
/// Burning faster than the week's allowance makes "resets in Nd" misleading — at current pace the
/// cap is hit in 2 days, not 7. The line states exactly that: "Pace ~5.2k tok/day → cap Wed".
/// No notifications, no settings, no charts; hidden whenever any input is missing.
///
/// Math (all from data this app already fetches):
///   - used-percent slope: the weekly window reports {@link RateWindow.UsedPercent} used after
///     (now - windowStart) elapsed in a window of {@link RateWindow.WindowMinutes} minutes, so
///     the pace is a straight line to 100%: daysToCap = (100-used)*elapsed/used. The projection
///     only becomes a warning when actual usage is more than two percentage points ahead of the
///     ideal elapsed-time usage; this keeps a small first sample from dominating the estimate.
///   - the "tok/day" number comes from the profile's daily buckets (mean of the last ≤ 7).
///   - only a materially-ahead pace that exhausts before reset folds into the configured 50/20
///     thresholds via {@link QuotaDisplay.BrushKeyForRemaining}; all other live pace is neutral.
///
/// Hide (null) when: fewer than 2 closed profile days, a ~0 token/day mean, no weekly window /
/// reset boundary, a window that has not started, or nothing used yet.
/// </summary>
public static class PaceLine
{
    /// <summary>How many closed profile days (newest last) feed the daily rate.</summary>
    public const int RateWindowDays = 7;

    /// <summary>Ignore small positive deviations from the ideal time-based pace.</summary>
    public const double OnTrackDeltaPercent = 2;

    /// <summary>Below this mean daily burn the line reads "nothing to say" and hides.</summary>
    public const double MinimumDailyTokens = 10;

    public static PaceLineResult? Compute(
        IReadOnlyList<ProfileUsageBucket> dailyBuckets,  // newest last
        double weeklyUsedPercent,                        // 0..100, from the weekly RateWindow
        DateTimeOffset? weeklyResetAt,                   // weekly RateWindow.ResetAt
        int weeklyWindowMinutes,                         // weekly RateWindow.WindowMinutes
        DateTimeOffset now)
    {
        if (weeklyResetAt is not { } resetAt || resetAt <= now)
            return null;
        if (weeklyUsedPercent <= 0)
            return null;

        double spanDays = weeklyWindowMinutes > 0 ? weeklyWindowMinutes / 1440.0 : 7.0 * 24 * 60 / 1440.0;
        var windowStart = resetAt.AddDays(-spanDays);
        double elapsedDays = (now - windowStart).TotalDays;
        if (elapsedDays <= 1e-9)
            return null;

        double used = Math.Clamp(weeklyUsedPercent, 0, 100);
        double expectedUsed = Math.Clamp(elapsedDays / spanDays * 100, 0, 100);
        bool materiallyAhead = used - expectedUsed > OnTrackDeltaPercent;
        double burnPerDay = used / elapsedDays;
        double daysToCap = (100 - used) / burnPerDay;

        bool burned = used >= 100 || daysToCap <= 0;
        double daysToReset = (resetAt - now).TotalDays;
        double remaining = burned
            ? 0
            : materiallyAhead && daysToCap < daysToReset
                ? Math.Min(100, daysToCap / Math.Max(1, daysToReset) * 100)
                : 100;

        // The daily rate comes from the profile buckets; the label is as-if meaningful only when
        // there is a real burn behind it.
        long? rate = DailyRate(dailyBuckets);
        if (rate is not { } rateTokens || rateTokens < MinimumDailyTokens)
            return null;

        string rateLabel = FormatTokens(rateTokens);
        string label;
        if (burned)
            label = $"Pace — cap reached · resets {DayName(resetAt)}";
        else if (materiallyAhead && daysToCap < daysToReset)
            label = $"Pace ~{rateLabel} tok/day → cap {DayName(now.AddDays(daysToCap))} (~{daysToCap:0.#}d)";
        else
            label = $"Pace ~{rateLabel} tok/day — resets before cap";

        return new PaceLineResult(label, remaining);
    }

    /// <summary>Mean tokens/day over the last ≤7 closed buckets, or null when fewer than 2.</summary>
    private static long? DailyRate(IReadOnlyList<ProfileUsageBucket> buckets)
    {
        if (buckets.Count < 2)
            return null;

        int take = Math.Min(RateWindowDays, buckets.Count);
        long sum = 0;
        for (int i = buckets.Count - take; i < buckets.Count; i++)
            sum += Math.Max(0, buckets[i].Tokens);
        return (long)Math.Round(sum / (double)take);
    }

    private static string DayName(DateTimeOffset when)
        => when.ToLocalTime().ToString("ddd", CultureInfo.InvariantCulture);

    private static string FormatTokens(long tokens)
    {
        if (tokens >= 10_000)
            return (tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture) + "k";
        if (tokens >= 1_000)
            return (tokens / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }
}
