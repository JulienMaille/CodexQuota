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
}
