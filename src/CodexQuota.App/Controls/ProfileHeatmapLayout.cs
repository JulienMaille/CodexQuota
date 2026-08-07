using System;
using System.Collections.Generic;
using System.Globalization;
using CodexQuota.Usage;

namespace CodexQuota.Controls
{
    /// <summary>
    /// Lays out the Profile endpoint's daily token buckets as a GitHub-style activity grid:
    /// a fixed window of week columns ending at the current week, seven rows (Sunday on top), each
    /// cell a single day's tokens with zero-filled gaps. Pure data mapping so the flyout renderer
    /// stays a dumb painter.
    /// </summary>
    internal static class ProfileHeatmapLayout
    {
        /// <summary>Number of week columns rendered; buckets older than this window are dropped.</summary>
        public const int MaxWeeks = 12;

        public readonly record struct DayCell(DateTimeOffset Day, long Tokens);

        /// <summary>Builds the grid ending at today (UTC); <paramref name="endDay"/> overrides for tests.</summary>
        public static IReadOnlyList<IReadOnlyList<DayCell>> Build(
            IReadOnlyList<ProfileUsageBucket> buckets,
            DateOnly? endDay = null)
        {
            DateOnly lastDay = endDay ?? DateOnly.FromDateTime(DateTime.UtcNow.Date);

            var byDay = new Dictionary<DateOnly, long>();
            foreach (var bucket in buckets)
            {
                if (DateOnly.TryParse(bucket.StartDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                    byDay[day] = bucket.Tokens;
            }

            var columns = new List<IReadOnlyList<DayCell>>(MaxWeeks);
            DateOnly lastWeekSunday = lastDay.AddDays(-(int)lastDay.DayOfWeek); // Sunday=0
            DateOnly firstWeekSunday = lastWeekSunday.AddDays(-(MaxWeeks - 1) * 7);

            for (int w = 0; w < MaxWeeks; w++)
            {
                var column = new List<DayCell>(7);
                for (int i = 0; i < 7; i++)
                {
                    var day = firstWeekSunday.AddDays(w * 7 + i);
                    if (day > lastDay)
                        break; // trailing partial week
                    column.Add(new DayCell(
                        new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                        byDay.GetValueOrDefault(day)));
                }

                columns.Add(column);
            }

            return columns;
        }
    }
}
