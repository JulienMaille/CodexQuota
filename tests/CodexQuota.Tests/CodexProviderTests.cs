using System.Text.Json;
using CodexQuota.Usage.Providers;
using CodexQuota.Usage;
using CodexQuota.Controls;

namespace CodexQuota.Tests;

public class ProfileHeatmapLayoutTests
{
    private static ProfileUsageBucket Bucket(string date, long tokens) => new(date, tokens);

    [Fact]
    public void Build_NoBuckets_ReturnsFullWindowOfZeros()
    {
        var columns = ProfileHeatmapLayout.Build(Array.Empty<ProfileUsageBucket>(), new DateOnly(2026, 8, 7));

        Assert.Equal(ProfileHeatmapLayout.MaxWeeks, columns.Count);
        Assert.All(columns, c => Assert.All(c, cell => Assert.Equal(0, cell.Tokens)));
    }

    [Fact]
    public void Build_SpanningSundayToSaturday_AlignsColumnsOnSundays()
    {
        // 2026-08-03 is a Monday (synthetic test data); the window ends on Saturday 2026-08-08 so the
        // containing week (starting Sunday 2026-08-02) renders in full.
        var buckets = new[]
        {
            Bucket("2026-08-03", 1000),
        };
        var columns = ProfileHeatmapLayout.Build(buckets, new DateOnly(2026, 8, 8));

        // The window is anchored to the containing week's Sunday.
        var last = columns[^1];
        Assert.Equal("2026-08-02", last[0].Day.ToString("yyyy-MM-dd"));
        Assert.Equal(0, last[0].Tokens);
        Assert.Equal(1000, last[1].Tokens);
        Assert.Equal(7, last.Count);
    }

    [Fact]
    public void Build_TwoAdjacentWeeks_ZeroFillsMissingDays()
    {
        var buckets = new[]
        {
            Bucket("2026-08-03", 1000),  // Monday of week 1
            Bucket("2026-08-10", 500),   // Monday of week 2
        };
        var columns = ProfileHeatmapLayout.Build(buckets, new DateOnly(2026, 8, 15));

        Assert.Equal(ProfileHeatmapLayout.MaxWeeks, columns.Count);
        Assert.Equal(1000, columns[^2][1].Tokens);
        Assert.Equal(500, columns[^1][1].Tokens);
        Assert.Equal(0, columns[^2][2].Tokens);   // Tuesday week 1: missing bucket
        Assert.Equal(0, columns[^1][6].Tokens);   // Saturday week 2 (window end): missing bucket
    }

    [Fact]
    public void Build_BucketOlderThanMaxWeeks_Dropped()
    {
        var latest = Bucket("2026-08-07", 100);
        var ancient = Bucket("2026-01-15", 999); // > 22 weeks before the latest
        var columns = ProfileHeatmapLayout.Build(new[] { ancient, latest }, new DateOnly(2026, 8, 7));

        Assert.Equal(ProfileHeatmapLayout.MaxWeeks, columns.Count);
        Assert.DoesNotContain(
            columns.SelectMany(c => c),
            cell => cell.Tokens == 999);
    }

    [Fact]
    public void Build_LegacyFlatBuckets_ParseStartDateWithoutTime()
    {
        var buckets = new[] { Bucket("2026-08-03", 42) };
        var columns = ProfileHeatmapLayout.Build(buckets, new DateOnly(2026, 8, 3));

        Assert.Equal(ProfileHeatmapLayout.MaxWeeks, columns.Count);
        Assert.Equal(42, columns[^1][1].Tokens);
    }

    [Fact]
    public void Build_PartialEndWeek_StopsAtEndDay()
    {
        // End day is Wednesday; the trailing column must contain only Sunday..Wednesday.
        var columns = ProfileHeatmapLayout.Build(Array.Empty<ProfileUsageBucket>(), new DateOnly(2026, 8, 5));

        Assert.Equal(ProfileHeatmapLayout.MaxWeeks, columns.Count);
        Assert.Equal(4, columns[^1].Count);
    }
}

public class CodexProviderTests
{
    [Theory]
    [InlineData("pro", "Pro 20x")]
    [InlineData("prolite", "Pro 5x")]
    [InlineData("pro_lite", "Pro 5x")]
    public void BuildResult_ProPlans_UseCodexMultiplierLabels(string planType, string expected)
    {
        using var doc = JsonDocument.Parse(CodexUsageJson(planType));

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Equal(expected, result.Usage.LoginMethod);
    }

    [Fact]
    public void BuildResult_ProPlan_SurfacesSparkSessionAndWeeklyWindows()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("pro", includeSpark: true));

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Contains(result.Usage.ExtraRateWindows, w => w.Title == "Spark Session" && w.Window.UsedPercent == 25);
        Assert.Contains(result.Usage.ExtraRateWindows, w => w.Title == "Spark Weekly" && w.Window.UsedPercent == 40);
    }

    [Fact]
    public void BuildResult_SessionAndWeekly_LeavesPrimaryUnlabeled()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("plus"));

        var result = CodexProvider.BuildResult(doc.RootElement);

        // Both windows present: primary keeps its default "Session" label (Label override stays null).
        Assert.Null(result.Usage.Primary.Label);
        Assert.NotNull(result.Usage.Secondary);
    }

    [Fact]
    public void BuildResult_WeeklyOnlyWindow_RelabelsPrimaryAsWeekly()
    {
        // OpenAI dropped the 5h session, so the API returns only the weekly window (issue #18).
        var json = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 0,
                  "limit_window_seconds": 604800,
                  "reset_at": 1893542400
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Equal("Weekly", result.Usage.Primary.Label);
        Assert.Equal(0, result.Usage.Primary.UsedPercent);
        Assert.True(result.Usage.HasPrimaryWindow);
        Assert.Null(result.Usage.Secondary);
    }

    [Fact]
    public void BuildResult_SessionOnlyShortWindow_KeepsSessionLabel()
    {
        // A lone sub-day window is still a real 5h session — must not be relabeled Weekly.
        var json = """
            {
              "plan_type": "plus",
              "rate_limit": {
                "primary_window": {
                  "used_percent": 30,
                  "limit_window_seconds": 18000,
                  "reset_at": 1893456000
                }
              }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Null(result.Usage.Primary.Label);
    }

    [Fact]
    public void BuildResult_NonProPlan_HidesSparkWindows()
    {
        using var doc = JsonDocument.Parse(CodexUsageJson("plus", includeSpark: true));

        var result = CodexProvider.BuildResult(doc.RootElement);

        Assert.Equal("Plus", result.Usage.LoginMethod);
        Assert.DoesNotContain(result.Usage.ExtraRateWindows, w => w.Title.Contains("Spark"));
    }

    [Fact]
    public void BuildResult_WithResetCredits_SurfacesAvailableCountAndTimes()
    {
        using var usage = JsonDocument.Parse(CodexUsageJson("pro"));
        using var resetCredits = JsonDocument.Parse("""
            {
              "credits": [
                {
                  "status": "available",
                  "granted_at": "2026-06-12T03:43:26.144717Z",
                  "expires_at": "2026-07-12T03:43:26.144717Z"
                },
                {
                  "status": "redeemed",
                  "granted_at": "2026-06-10T03:43:26.144717Z",
                  "expires_at": "2026-07-10T03:43:26.144717Z"
                },
                {
                  "status": "available",
                  "granted_at": "2026-06-18T00:14:18.923019Z",
                  "expires_at": "2026-07-18T00:14:18.923019Z"
                }
              ],
              "available_count": 2
            }
            """);

        var result = CodexProvider.BuildResult(usage.RootElement, resetCreditsJson: resetCredits.RootElement);

        Assert.NotNull(result.Usage.ResetCredits);
        Assert.Equal(2, result.Usage.ResetCredits!.AvailableCount);
        Assert.Equal(2, result.Usage.ResetCredits.Credits.Count);
        Assert.Equal(DateTimeOffset.Parse("2026-06-12T03:43:26.144717Z"), result.Usage.ResetCredits.Credits[0].GrantedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T00:14:18.923019Z"), result.Usage.ResetCredits.Credits[1].ExpiresAt);
    }

    [Fact]
    public void ParseProfile_FullPayload_ExtractsStatsAndMetadata()
    {
        // Live-shaped payload from /wham/profiles/me (metadata + stats nesting).
        string json = """
            {
              "profile": { "username": "test.user", "display_name": "Test User", "profile_picture_url": "https://example.com/avatar.png" },
              "stats": {
                "lifetime_tokens": 1000000,
                "peak_daily_tokens": 2000000,
                "longest_running_turn_sec": 3600,
                "current_streak_days": 5,
                "longest_streak_days": 20,
                "total_threads": 258,
                "fast_mode_usage_percentage": 2.27,
                "most_used_reasoning_effort": "high",
                "most_used_reasoning_effort_percentage": 39.7,
                "unique_skills_used": 8,
                "total_skills_used": 25,
                "daily_usage_buckets": [
                  { "start_date": "2026-01-06", "tokens": 1000000 },
                  { "start_date": "2026-01-07", "tokens": 2000000 }
                ],
                "weekly_usage_buckets": [
                  { "start_date": "2026-01-03", "tokens": 3000000 }
                ],
                "top_invocations": [
                  { "type": "plugin", "plugin_id": "outlook-email@openai-curated-remote", "plugin_name": "outlook-email", "skill_id": null, "skill_name": null, "usage_count": 14 },
                  { "type": "skill", "plugin_id": null, "plugin_name": null, "skill_id": "abc", "skill_name": "writing-blocks", "usage_count": 4 }
                ],
                "workspace_rank": null,
                "workspace_total_user_count": null
              },
              "metadata": { "stats_as_of": "2026-01-08", "generated_at": "2026-01-08T12:00:00.000000Z", "stats_error": null }
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var profile = CodexProvider.ParseProfile(doc.RootElement);

        Assert.Equal("test.user", profile.Username);
        Assert.Equal("Test User", profile.DisplayName);
        Assert.Equal(1000000, profile.LifetimeTokens);
        Assert.Equal(2000000, profile.PeakDailyTokens);
        Assert.Equal(3600, profile.LongestRunningTurnSec);
        Assert.Equal(5, profile.CurrentStreakDays);
        Assert.Equal(20, profile.LongestStreakDays);
        Assert.Equal(258, profile.TotalThreads);
        Assert.Equal(2.27, profile.FastModeUsagePercentage);
        Assert.Equal("high", profile.MostUsedReasoningEffort);
        Assert.Equal(39.7, profile.MostUsedReasoningEffortPercentage);
        Assert.Equal(8, profile.UniqueSkillsUsed);
        Assert.Equal(25, profile.TotalSkillsUsed);
        Assert.Equal(2, profile.DailyUsageBuckets.Count);
        Assert.Equal("2026-01-07", profile.DailyUsageBuckets[^1].StartDate);
        Assert.Equal(2000000, profile.DailyUsageBuckets[^1].Tokens);
        Assert.Single(profile.WeeklyUsageBuckets);
        Assert.Equal(3000000, profile.WeeklyUsageBuckets[0].Tokens);
        Assert.Equal(2, profile.TopInvocations.Count);
        Assert.Equal("outlook-email", profile.TopInvocations[0].Name);
        Assert.Equal("plugin", profile.TopInvocations[0].Type);
        Assert.Equal(14, profile.TopInvocations[0].UsageCount);
        Assert.Equal("writing-blocks", profile.TopInvocations[1].Name);
        Assert.Equal("2026-01-08", profile.StatsAsOf);
        Assert.Equal(DateTimeOffset.Parse("2026-01-08T12:00:00.000000Z"), profile.GeneratedAt);
    }

    [Fact]
    public void ParseProfile_LegacyVariant_FallsBackToTopLevelLastBuckets()
    {
        // May-2026 shape (openai/codex#25479): bucket_count + last_buckets at top level, no metadata.
        string json = """
            {
              "lifetime_tokens": 1000000,
              "peak_daily_tokens": 2000000,
              "longest_running_turn_sec": 3600,
              "current_streak_days": 6,
              "longest_streak_days": 14,
              "bucket_count": 25,
              "last_buckets": [
                { "start_date": "2025-05-25", "tokens": 100000 },
                { "start_date": "2025-05-26", "tokens": 200000 }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);

        var profile = CodexProvider.ParseProfile(doc.RootElement);

        Assert.Equal(1000000, profile.LifetimeTokens);
        Assert.Equal(2000000, profile.PeakDailyTokens);
        Assert.Equal(3600, profile.LongestRunningTurnSec);
        Assert.Equal(6, profile.CurrentStreakDays);
        Assert.Equal(14, profile.LongestStreakDays);
        Assert.Equal(2, profile.DailyUsageBuckets.Count);
        Assert.Equal("2025-05-26", profile.DailyUsageBuckets[^1].StartDate);
        Assert.Empty(profile.WeeklyUsageBuckets);
        Assert.Null(profile.StatsAsOf);
    }

    [Fact]
    public void ParseProfile_EmptyStats_ReturnsDefaults()
    {
        using var doc = JsonDocument.Parse("""{ "profile": {} }""");

        var profile = CodexProvider.ParseProfile(doc.RootElement);

        Assert.Equal(0, profile.LifetimeTokens);
        Assert.Equal(0, profile.PeakDailyTokens);
        Assert.Null(profile.Username);
        Assert.Empty(profile.DailyUsageBuckets);
        Assert.Empty(profile.TopInvocations);
    }

    private static string CodexUsageJson(string planType, bool includeSpark = false)
    {
        var additional = includeSpark
            ? """
              ,
                "additional_rate_limits": [
                  {
                    "limit_name": "GPT-5.3-Codex-Spark",
                    "rate_limit": {
                      "primary_window": {
                        "used_percent": 25,
                        "limit_window_seconds": 18000,
                        "reset_at": 1893456000
                      },
                      "secondary_window": {
                        "used_percent": 40,
                        "limit_window_seconds": 604800,
                        "reset_at": 1893542400
                      }
                    }
                  }
                ]
              """
            : string.Empty;

        return $$"""
               {
                 "plan_type": "{{planType}}",
                 "rate_limit": {
                   "primary_window": {
                     "used_percent": 10,
                     "limit_window_seconds": 18000,
                     "reset_at": 1893456000
                   },
                   "secondary_window": {
                     "used_percent": 20,
                     "limit_window_seconds": 604800,
                     "reset_at": 1893542400
                   }
                 }
                 {{additional}}
               }
               """;
    }
}