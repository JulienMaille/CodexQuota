using System;
using CodexQuota.Usage;

namespace CodexQuota;

/// <summary>
/// Decides how long to wait before the next automatic usage refresh. Pure by construction: every
/// signal arrives as a parameter, so the same input always yields the same delay.
///
/// The cadence adapts to how recently the user interacted with the flyout and whether the Codex CLI
/// is actively running — the faster intervals only apply while the data is plausibly being watched or
/// consumed (idea stolen from CodexBar's AdaptiveRefreshPolicyCore, adapted for Windows: no thermal
/// signal, coding activity = a live codex process).
/// </summary>
public static class AdaptiveRefreshPolicy
{
    /// <summary>Flyout used within this window counts as "recent interaction".</summary>
    public static readonly TimeSpan RecentInteractionWindow = TimeSpan.FromMinutes(5);

    /// <summary>Recently opened / flyout open / Codex running: keep it fresh.</summary>
    public static readonly TimeSpan ActiveDelay = TimeSpan.FromSeconds(60);

    /// <summary>Warm: recent interaction aged out but within an hour.</summary>
    public static readonly TimeSpan WarmDelay = TimeSpan.FromMinutes(5);

    /// <summary>Idle: no interaction for up to 4 hours (5-minute cadence).</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(5);

    /// <summary>Long idle (or never opened): slowest sustainable cadence (10 minutes).</summary>
    public static readonly TimeSpan LongIdleDelay = TimeSpan.FromMinutes(10);

    /// <summary>Minimum supported interval, used to clamp clock-skewed inputs.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(30);

    /// <summary>Grace added on top of a quota reset before refreshing, so the server has flipped to
    /// post-reset values by the time we poll (a ~30s settle; AutoSendService uses 10s).</summary>
    public static readonly TimeSpan ResetGrace = TimeSpan.FromSeconds(30);

    /// <summary>Short re-arm when a reset was crossed but the latest fetch still shows pre-reset
    /// values (the server hasn't flipped yet) — retry soon instead of waiting out the policy.</summary>
    public static readonly TimeSpan ResetCatchUpDelay = TimeSpan.FromSeconds(15);

    /// <summary>Lower bound for the reset-shortened path. Deliberately below
    /// <see cref="MinimumDelay"/> (5s vs 30s): an imminent reset should still refresh promptly just
    /// after the grace instead of landing ~30s late, while 5s keeps us clear of a tight loop.</summary>
    public static readonly TimeSpan ResetMinimumDelay = TimeSpan.FromSeconds(5);

    public static TimeSpan NextDelay(
        bool flyoutOpen,
        DateTimeOffset? lastFlyoutOpenAtUtc,
        DateTimeOffset now,
        bool codexRunning)
    {
        TimeSpan delay;
        if (flyoutOpen || codexRunning)
        {
            delay = ActiveDelay;
        }
        else if (lastFlyoutOpenAtUtc is { } last)
        {
            TimeSpan age = now - last;
            if (age <= RecentInteractionWindow)
                delay = ActiveDelay;
            else if (age <= TimeSpan.FromHours(1))
                delay = WarmDelay;
            else if (age <= TimeSpan.FromHours(4))
                delay = IdleDelay;
            else
                delay = LongIdleDelay;
        }
        else
        {
            delay = LongIdleDelay;
        }

        return delay >= MinimumDelay ? delay : MinimumDelay;
    }

    /// <summary>Earliest quota reset in the snapshot that still lies in the future. Considers every
    /// reset-bearing surface: Primary, Secondary, ModelSpecific, Monthly, ExtraRateWindows, Cost and
    /// ResetCredits. Pure: same snapshot and clock always yield the same result.</summary>
    public static DateTimeOffset? EarliestFutureReset(UsageSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is null)
            return null;

        DateTimeOffset? earliest = null;
        TrackFutureReset(ref earliest, snapshot.Primary?.ResetAt, now);
        TrackFutureReset(ref earliest, snapshot.Secondary?.ResetAt, now);
        TrackFutureReset(ref earliest, snapshot.ModelSpecific?.ResetAt, now);
        TrackFutureReset(ref earliest, snapshot.Monthly?.ResetAt, now);
        if (snapshot.ExtraRateWindows is { } extras)
            foreach (var extra in extras)
                TrackFutureReset(ref earliest, extra?.Window?.ResetAt, now);
        TrackFutureReset(ref earliest, snapshot.Cost?.ResetsAt, now);
        TrackFutureReset(ref earliest, snapshot.ResetCredits?.EarliestExpiresAt, now);
        return earliest;
    }

    private static void TrackFutureReset(ref DateTimeOffset? earliest, DateTimeOffset? candidate, DateTimeOffset now)
    {
        if (candidate is { } reset && reset > now && (earliest is null || reset < earliest))
            earliest = reset;
    }

    /// <summary>True when any reset in the snapshot fired after <paramref name="fetchedAt"/> and no
    /// later than <paramref name="now"/> — i.e. the fetch predates a reset that has since passed, so
    /// its values are pre-reset and stale. Pure.</summary>
    public static bool HasCrossedReset(UsageSnapshot? snapshot, DateTimeOffset fetchedAt, DateTimeOffset now)
    {
        if (snapshot is null)
            return false;

        return Crossed(snapshot.Primary?.ResetAt, fetchedAt, now)
            || Crossed(snapshot.Secondary?.ResetAt, fetchedAt, now)
            || Crossed(snapshot.ModelSpecific?.ResetAt, fetchedAt, now)
            || Crossed(snapshot.Monthly?.ResetAt, fetchedAt, now)
            || ExtraCrossed(snapshot, fetchedAt, now)
            || Crossed(snapshot.Cost?.ResetsAt, fetchedAt, now)
            || Crossed(snapshot.ResetCredits?.EarliestExpiresAt, fetchedAt, now);
    }

    private static bool ExtraCrossed(UsageSnapshot snapshot, DateTimeOffset fetchedAt, DateTimeOffset now)
    {
        if (snapshot.ExtraRateWindows is not { } extras)
            return false;
        foreach (var extra in extras)
            if (Crossed(extra?.Window?.ResetAt, fetchedAt, now))
                return true;
        return false;
    }

    private static bool Crossed(DateTimeOffset? candidate, DateTimeOffset fetchedAt, DateTimeOffset now)
        => candidate is { } reset && reset > fetchedAt && reset <= now;

    /// <summary>Shortens <paramref name="policyDelay"/> so the next poll lands just after the next
    /// quota reset (<c>timeUntilReset + <see cref="ResetGrace"/></c>) when that is sooner. Clamping:
    /// the reset path clamps to [<see cref="ResetMinimumDelay"/>, <paramref name="policyDelay"/>] —
    /// no <see cref="MinimumDelay"/> floor, so an imminent reset still refreshes promptly — while the
    /// no-reset path returns <paramref name="policyDelay"/> untouched. Pure.</summary>
    public static TimeSpan NextDelayWithReset(TimeSpan policyDelay, UsageSnapshot? snapshot, DateTimeOffset now)
    {
        DateTimeOffset? earliest = EarliestFutureReset(snapshot, now);
        if (earliest is null)
            return policyDelay;

        TimeSpan resetDelay = (earliest.Value - now) + ResetGrace;
        if (resetDelay >= policyDelay)
            return policyDelay;
        return resetDelay < ResetMinimumDelay ? ResetMinimumDelay : resetDelay;
    }
}