using System;

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

    /// <summary>Idle: no interaction for up to 4 hours.</summary>
    public static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);

    /// <summary>Long idle (or never opened): slowest sustainable cadence.</summary>
    public static readonly TimeSpan LongIdleDelay = TimeSpan.FromMinutes(30);

    /// <summary>Minimum supported interval, used to clamp clock-skewed inputs.</summary>
    public static readonly TimeSpan MinimumDelay = TimeSpan.FromSeconds(30);

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
}