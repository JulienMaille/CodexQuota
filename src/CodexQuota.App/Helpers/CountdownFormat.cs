using System;

namespace CodexQuota.Helpers
{
    /// <summary>
    /// Renders a wall-clock timestamp as a compact "now" / "3h 25m" / "6d 17h" countdown.
    /// Shared by the taskbar tile (CodexProvider) and the flyout (CodexUsagePanel) so both
    /// render an identical snapshot; <see cref="DateTimeOffset.UtcNow"/> is captured once.
    /// </summary>
    public static class CountdownFormat
    {
        /// <summary>Returns null when <paramref name="resetAt"/> is null, "now" when it is due.</summary>
        public static string? Format(DateTimeOffset? resetAt)
        {
            if (resetAt is not DateTimeOffset dt)
                return null;
            return Format(dt - DateTimeOffset.UtcNow);
        }

        /// <summary>Formats a remaining duration; "now" at or below zero.</summary>
        public static string Format(TimeSpan diff)
        {
            if (diff <= TimeSpan.Zero)
                return "now";

            int hours = (int)diff.TotalHours;
            int mins = diff.Minutes;
            if (hours >= 24)
            {
                int days = hours / 24;
                int rem = hours % 24;
                return rem == 0 ? $"{days}d" : $"{days}d {rem}h";
            }
            return hours > 0 ? (mins == 0 ? $"{hours}h" : $"{hours}h {mins}m") : $"{mins}m";
        }
    }
}
