using System;
using System.IO;
using CodexQuota.Usage;

namespace CodexQuota.Tests;

public class LocalCodexUsageScannerTests
{
    [Fact]
    public void ReadTodayTokensSumsOnlyTodaysPerResponseUsage()
    {
        var today = new DateOnly(2026, 8, 9);
        string home = Path.Combine(Path.GetTempPath(), "CodexQuotaTests", Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(home, "sessions", "2026", "08", "08");
        Directory.CreateDirectory(directory);

        try
        {
            File.WriteAllLines(Path.Combine(directory, "session.jsonl"), new[]
            {
                "{\"timestamp\":\"2026-08-08T23:59:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":500}}}}",
                "{\"timestamp\":\"2026-08-09T00:01:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":40}}}}",
                "{\"timestamp\":\"2026-08-09T00:02:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":60}}}}",
            });

            Assert.Equal(100, LocalCodexUsageScanner.ReadTodayTokens(today, home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void WithLiveTodayAddsMissingBucketAndPreservesServerHistory()
    {
        var today = new DateOnly(2026, 8, 9);
        var profile = new CodexProfileSnapshot
        {
            DailyUsageBuckets = new[] { new ProfileUsageBucket("2026-08-08", 25) },
            StatsAsOf = "2026-08-09",
        };

        var merged = profile.WithLiveToday(today, 100);

        Assert.Equal(25, merged.DailyUsageBuckets[0].Tokens);
        Assert.Equal("2026-08-09", merged.DailyUsageBuckets[1].StartDate);
        Assert.Equal(100, merged.DailyUsageBuckets[1].Tokens);
        Assert.True(merged.TodayUsageIsLocal);
        Assert.Equal("2026-08-09", merged.StatsAsOf);
    }

    [Fact]
    public void WithLiveTodayDoesNotReplaceLargerServerBucket()
    {
        var today = new DateOnly(2026, 8, 9);
        var profile = new CodexProfileSnapshot
        {
            DailyUsageBuckets = new[] { new ProfileUsageBucket("2026-08-09", 250) },
        };

        var merged = profile.WithLiveToday(today, 100);

        Assert.Same(profile, merged);
        Assert.Equal(250, merged.DailyUsageBuckets[0].Tokens);
        Assert.False(merged.TodayUsageIsLocal);
    }

    [Fact]
    public void ReadDayTokensCountsEventsFromPreviousDayFolder()
    {
        var day = new DateOnly(2026, 8, 9);
        string home = Path.Combine(Path.GetTempPath(), "CodexQuotaTests", Guid.NewGuid().ToString("N"));
        string previousDirectory = Path.Combine(home, "sessions", "2026", "08", "08");
        Directory.CreateDirectory(previousDirectory);

        try
        {
            // A session that started on the 8th and continued past midnight writes its 9th events
            // into the 8th's folder; they still belong to the 9th.
            File.WriteAllLines(Path.Combine(previousDirectory, "session.jsonl"), new[]
            {
                "{\"timestamp\":\"2026-08-08T23:59:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":700}}}}",
                "{\"timestamp\":\"2026-08-09T01:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":70}}}}",
                "{\"timestamp\":\"2026-08-09T02:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"last_token_usage\":{\"total_tokens\":30}}}}",
            });

            Assert.Equal(100, LocalCodexUsageScanner.ReadDayTokens(day, home));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    [Fact]
    public void ReadRangeTokensSumsPerDayAndOmitsEmptyDays()
    {
        string home = Path.Combine(Path.GetTempPath(), "CodexQuotaTests", Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var (y, m, d) in new[] { ("2026", "08", "06"), ("2026", "08", "08") })
            {
                string directory = Path.Combine(home, "sessions", y, m, d);
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "session.jsonl"),
                    $"{{\"timestamp\":\"{y}-{m}-{d}T12:00:00Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"token_count\",\"info\":{{\"last_token_usage\":{{\"total_tokens\":111}}}}}}}}");
            }

            var result = LocalCodexUsageScanner.ReadRangeTokens(new DateOnly(2026, 8, 6), new DateOnly(2026, 8, 8), home);

            Assert.Equal(111, result[new DateOnly(2026, 8, 6)]);
            Assert.Equal(111, result[new DateOnly(2026, 8, 8)]);
            Assert.False(result.ContainsKey(new DateOnly(2026, 8, 7))); // no journal, no key
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }
}
