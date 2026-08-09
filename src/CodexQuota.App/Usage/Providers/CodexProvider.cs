using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using CodexQuota;
using CodexQuota.Helpers;

namespace CodexQuota.Usage.Providers
{
    /// <summary>
    /// Codex (ChatGPT) usage via the OAuth token stored by the Codex CLI in ~/.codex/auth.json.
    /// Ported from Win-CodexBar rust/src/providers/codex/api.rs.
    /// </summary>
    public sealed class CodexProvider : IUsageProvider
    {
        private const string DefaultBaseUrl = "https://chatgpt.com/backend-api";
        private const string UsagePath = "/wham/usage";
        private const string ResetCreditsPath = "/wham/rate-limit-reset-credits";
        private const string ProfilePath = "/wham/profiles/me";
        private static readonly TimeSpan ResetCreditsTimeout = TimeSpan.FromSeconds(3);
        // The profile endpoint is server-side aggregation (~0.7s measured): a short dedicated cap
        // keeps a slow profile GET from stretching the poll cadence beyond policy + 10s. The shared
        // HttpClient.Timeout (30s) is not enough because the poll re-arms only after the profile
        // await completes (see UsageCoordinator.FetchAndPublishAsync).
        private static readonly TimeSpan ProfileTimeout = TimeSpan.FromSeconds(10);
        private static readonly Regex CodexModelPrefix = new(@"^GPT-[\d.]+-Codex-", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly HttpClient Http = new(new HttpClientHandler())
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public async Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            var creds = LoadCredentials();

            using var request = CreateRequest(creds, UsagePath);
            using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Codex token expired. Run `codex login`.");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new ProviderException(ProviderErrorKind.RateLimited, "Codex API rate limit reached.");

            if (!response.IsSuccessStatusCode)
                throw new ProviderException(ProviderErrorKind.Other, $"Codex API returned {(int)response.StatusCode}");

            double? headerPrimary = TryHeaderF64(response, "x-codex-primary-used-percent");
            double? headerSecondary = TryHeaderF64(response, "x-codex-secondary-used-percent");
            double? headerCredits = TryHeaderF64(response, "x-codex-credits-balance");

            using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            using var resetCreditsDoc = await FetchResetCreditsAsync(creds, ct).ConfigureAwait(false);

            return BuildResult(doc.RootElement, headerPrimary, headerSecondary, headerCredits, resetCreditsDoc?.RootElement);
        }

        internal static ProviderFetchResult BuildResult(
            JsonElement json,
            double? headerPrimary = null,
            double? headerSecondary = null,
            double? headerCredits = null,
            JsonElement? resetCreditsJson = null)
        {
            var (primaryOpt, secondary, codeReview) = ExtractRateLimits(json);
            // Codex org/Business plans expose no rate windows — only credits. Mirror CodexBar's
            // credits-only snapshot (primary == nil) rather than fabricating a bogus "Session 0%".
            // A live header percent still counts as a real session signal. See github issue #12.
            bool hasPrimary = primaryOpt != null || headerPrimary != null;
            var primary = primaryOpt ?? new RateWindow(0);
            if (headerPrimary is double hp) primary = WithUsedPercent(primary, hp);
            if (headerSecondary is double hs)
            {
                secondary = secondary is null
                    ? new RateWindow(hs, 10080)
                    : WithUsedPercent(secondary, hs);
            }

            // OpenAI temporarily disabled the 5h session limit (issue #18), so the API now returns a single
            // weekly window that would otherwise render under the "Session" label. When the only window we
            // have is the weekly one, relabel it "Weekly" — the value and reset time are already correct.
            if (secondary == null && hasPrimary && IsWeeklyWindow(primary))
                primary = WithLabel(primary, "Weekly");

            var usage = new UsageSnapshot(primary) { HasPrimaryWindow = hasPrimary };
            if (secondary != null) usage.Secondary = secondary;
            if (codeReview != null) usage.ModelSpecific = codeReview;

            string? planType = json.TryGetProperty("plan_type", out var planEl) && planEl.ValueKind == JsonValueKind.String
                ? planEl.GetString()
                : null;
            AddAdditionalRateLimits(json, usage, planType);

            if (!string.IsNullOrWhiteSpace(planType))
                usage.LoginMethod = PlanDisplay(planType!);

            if (json.TryGetProperty("email", out var emailEl) && emailEl.ValueKind == JsonValueKind.String)
                usage.Email = emailEl.GetString();

            usage.Cost = ExtractCredits(json, headerCredits);
            usage.ResetCredits = ExtractResetCredits(resetCreditsJson);

            return new ProviderFetchResult(usage, "oauth");
        }

        private static async Task<JsonDocument?> FetchResetCreditsAsync(Credentials creds, CancellationToken ct)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(ResetCreditsTimeout);
                using var request = CreateRequest(creds, ResetCreditsPath);
                request.Headers.TryAddWithoutValidation("OpenAI-Beta", "codex-1");
                request.Headers.TryAddWithoutValidation("originator", "Codex Desktop");

                using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    return null;

                using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Fetches the account's Profile summary (<c>/wham/profiles/me</c>) — the data behind the
        /// Codex/ChatGPT Profile dashboard: lifetime tokens, peak daily tokens, longest running turn,
        /// streaks, token-activity buckets, and most-used plugins/skills. Server-side aggregation, so
        /// values lag real usage and the current day's bucket may be absent; local session journals
        /// supplement that one bucket when available.
        /// </summary>
        public async Task<CodexProfileSnapshot> FetchProfileAsync(CancellationToken ct = default)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(ProfileTimeout);

            var creds = LoadCredentials();

            using var request = CreateRequest(creds, ProfilePath);
            try
            {
                using var response = await Http.SendAsync(request, timeout.Token).ConfigureAwait(false);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new ProviderException(ProviderErrorKind.AuthRequired, "Codex token expired. Run `codex login`.");
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new ProviderException(ProviderErrorKind.RateLimited, "Codex profile API rate limit reached.");

                if (!response.IsSuccessStatusCode)
                    throw new ProviderException(ProviderErrorKind.Other, $"Codex profile API returned {(int)response.StatusCode}");

                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token).ConfigureAwait(false);
                var profile = ParseProfile(doc.RootElement);
                var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
                return profile.WithLiveToday(today, LocalCodexUsageScanner.ReadTodayTokens(today));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderException(ProviderErrorKind.Timeout, "Codex profile API timed out.");
            }
        }

        internal static CodexProfileSnapshot ParseProfile(JsonElement json)
        {
            string? username = null;
            string? displayName = null;
            if (json.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object)
            {
                username = TryString(profile, "username");
                displayName = TryString(profile, "display_name");
            }

            var stats = json.TryGetProperty("stats", out var statsEl) && statsEl.ValueKind == JsonValueKind.Object
                ? statsEl
                : default;

            long lifetime = TryI64(stats, "lifetime_tokens") ?? TryI64(json, "lifetime_tokens") ?? 0;
            long? peak = TryI64(stats, "peak_daily_tokens") ?? TryI64(json, "peak_daily_tokens");
            long? longestTurn = TryI64(stats, "longest_running_turn_sec") ?? TryI64(json, "longest_running_turn_sec");
            int streak = TryInt32(stats, "current_streak_days") ?? TryInt32(json, "current_streak_days") ?? 0;
            int longestStreak = TryInt32(stats, "longest_streak_days") ?? TryInt32(json, "longest_streak_days") ?? 0;
            long threads = TryI64(stats, "total_threads") ?? TryI64(json, "total_threads") ?? 0;
            double? fastMode = TryF64(stats, "fast_mode_usage_percentage");
            string? reasoningEffort = TryString(stats, "most_used_reasoning_effort");
            double? reasoningPercent = TryF64(stats, "most_used_reasoning_effort_percentage");
            int uniqueSkills = TryInt32(stats, "unique_skills_used") ?? 0;
            int totalSkills = TryInt32(stats, "total_skills_used") ?? 0;

            // The May-2026 response variant reported bucket_count + last_buckets at the top level
            // instead of daily_usage_buckets (openai/codex#25479); newer responses nest under stats.
            // Both feeds also appeared at the top level at times, so try every nesting for each name
            // before falling back to the legacy last_buckets shapes (symmetric with the weekly chain).
            var daily = TryBuckets(stats, "daily_usage_buckets")
                ?? TryBuckets(json, "daily_usage_buckets")
                ?? TryBuckets(stats, "last_buckets")
                ?? TryBuckets(json, "last_buckets")
                ?? Array.Empty<ProfileUsageBucket>();
            var weekly = TryBuckets(stats, "weekly_usage_buckets")
                ?? TryBuckets(json, "weekly_usage_buckets")
                ?? Array.Empty<ProfileUsageBucket>();

            var invocations = new List<ProfileTopInvocation>();
            if (stats.ValueKind == JsonValueKind.Object
                && stats.TryGetProperty("top_invocations", out var invocationsEl)
                && invocationsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in invocationsEl.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    string? type = TryString(entry, "type");
                    string? name = TryString(entry, "plugin_name") ?? TryString(entry, "skill_name");
                    long count = TryI64(entry, "usage_count") ?? 0;
                    invocations.Add(new ProfileTopInvocation(type, name, count));
                }
            }

            var metadata = json.TryGetProperty("metadata", out var metadataEl) && metadataEl.ValueKind == JsonValueKind.Object
                ? metadataEl
                : default;

            return new CodexProfileSnapshot
            {
                Username = username,
                DisplayName = displayName,
                LifetimeTokens = lifetime,
                PeakDailyTokens = peak ?? 0,
                LongestRunningTurnSec = longestTurn ?? 0,
                CurrentStreakDays = streak,
                LongestStreakDays = longestStreak,
                TotalThreads = threads,
                FastModeUsagePercentage = fastMode,
                MostUsedReasoningEffort = reasoningEffort,
                MostUsedReasoningEffortPercentage = reasoningPercent,
                UniqueSkillsUsed = uniqueSkills,
                TotalSkillsUsed = totalSkills,
                DailyUsageBuckets = daily,
                WeeklyUsageBuckets = weekly,
                TopInvocations = invocations,
                GeneratedAt = TryDateTimeOffset(metadata, "generated_at") ?? TryDateTimeOffset(json, "generated_at"),
                StatsAsOf = TryString(metadata, "stats_as_of") ?? TryString(json, "stats_as_of"),
            };
        }

        private static IReadOnlyList<ProfileUsageBucket>? TryBuckets(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object
                || !parent.TryGetProperty(name, out var el)
                || el.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var buckets = new List<ProfileUsageBucket>();
            foreach (var entry in el.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                    continue;

                string? start = TryString(entry, "start_date");
                long tokens = TryI64(entry, "tokens") ?? 0;
                if (string.IsNullOrWhiteSpace(start))
                    continue;

                buckets.Add(new ProfileUsageBucket(start!, tokens));
            }

            return buckets;
        }

        private static string? TryString(JsonElement parent, string name)
            => parent.ValueKind == JsonValueKind.Object
                && parent.TryGetProperty(name, out var el)
                && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;

        private static long? TryI64(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var el))
                return null;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long value))
                return value;
            if (el.ValueKind == JsonValueKind.String
                && long.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value))
                return value;
            return null;
        }

        private static HttpRequestMessage CreateRequest(Credentials creds, string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, ResolveBaseUrl() + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
            request.Headers.UserAgent.ParseAdd("CodexQuota");
            request.Headers.Accept.ParseAdd("application/json");
            if (!string.IsNullOrEmpty(creds.AccountId))
                request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", creds.AccountId);
            return request;
        }

        private static (RateWindow? primary, RateWindow? secondary, RateWindow? codeReview) ExtractRateLimits(JsonElement json)
        {
            if (json.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
            {
                RateWindow? p = rl.TryGetProperty("primary_window", out var pw) && pw.ValueKind == JsonValueKind.Object ? ParseWindow(pw) : null;
                RateWindow? s = rl.TryGetProperty("secondary_window", out var sw) && sw.ValueKind == JsonValueKind.Object ? ParseWindow(sw) : null;
                RateWindow? cr = rl.TryGetProperty("code_review_window", out var cw) && cw.ValueKind == JsonValueKind.Object ? ParseWindow(cw) : null;
                if (cr is null &&
                    json.TryGetProperty("code_review_rate_limit", out var codeReview) &&
                    codeReview.ValueKind == JsonValueKind.Object &&
                    codeReview.TryGetProperty("primary_window", out var rw) &&
                    rw.ValueKind == JsonValueKind.Object)
                {
                    cr = ParseWindow(rw);
                }

                // Promote secondary to primary for weekly-only plans
                if (p == null && s != null) { p = s; s = null; }
                return (p, s, cr);
            }

            return (null, null, null);
        }

        private static void AddAdditionalRateLimits(JsonElement json, UsageSnapshot usage, string? planType)
        {
            if (!json.TryGetProperty("additional_rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
                return;

            foreach (var entry in limits.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !entry.TryGetProperty("rate_limit", out var rl) ||
                    rl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string rawName = entry.TryGetProperty("limit_name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                    ? nameEl.GetString() ?? string.Empty
                    : string.Empty;
                string shortName = CodexModelPrefix.Replace(rawName, string.Empty);
                if (string.IsNullOrWhiteSpace(shortName))
                    shortName = string.IsNullOrWhiteSpace(rawName) ? "Model" : rawName;
                if (IsSparkLimit(shortName) && !IsProPlan(planType))
                    continue;

                if (rl.TryGetProperty("primary_window", out var primary) && primary.ValueKind == JsonValueKind.Object)
                    usage.ExtraRateWindows.Add(new NamedRateWindow($"{shortName}-session", $"{shortName} Session", ParseWindow(primary)));
                if (rl.TryGetProperty("secondary_window", out var secondary) && secondary.ValueKind == JsonValueKind.Object)
                    usage.ExtraRateWindows.Add(new NamedRateWindow($"{shortName}-weekly", $"{shortName} Weekly", ParseWindow(secondary)));
            }
        }

        private static bool IsSparkLimit(string shortName)
            => string.Equals(shortName.Trim(), "Spark", StringComparison.OrdinalIgnoreCase);

        private static bool IsProPlan(string? planType)
        {
            var normalized = NormalizePlanType(planType);
            return normalized is "pro" or "prolite" or "pro_lite" or "pro-lite";
        }

        private static RateWindow ParseWindow(JsonElement window)
        {
            double used = TryF64(window, "used_percent") ?? TryF64(window, "usage_percent") ?? 0;
            int? minutes = null;
            if (window.TryGetProperty("limit_window_seconds", out var lw) && lw.TryGetInt64(out var secs))
                minutes = (int)(secs / 60);

            DateTimeOffset? resetAt = null;
            if (window.TryGetProperty("reset_at", out var ra) && ra.TryGetInt64(out var ts))
                resetAt = DateTimeOffset.FromUnixTimeSeconds(ts);

            return new RateWindow(used, minutes, resetAt, CountdownFormat.Format(resetAt));
        }

        private static RateWindow WithUsedPercent(RateWindow window, double usedPercent)
            => new(usedPercent, window.WindowMinutes, window.ResetAt, window.ResetDescription, window.Label);

        private static RateWindow WithLabel(RateWindow window, string label)
            => new(window.UsedPercent, window.WindowMinutes, window.ResetAt, window.ResetDescription, label);

        // A weekly window spans ~7 days. Treat an unknown duration as weekly too: when only one window is
        // present the 5h session has been dropped, so the lone window is the weekly one. A window shorter
        // than a day is still a real session window and keeps its "Session" label.
        private static bool IsWeeklyWindow(RateWindow window)
            => window.WindowMinutes is not int minutes || minutes >= 1440;

        private static CostSnapshot? ExtractCredits(JsonElement json, double? headerCredits)
        {
            // Codex credits mirror CodexBar's model: the /wham/usage `credits` object carries a raw
            // `balance` plus `has_credits` / `unlimited` flags, but NO limit. Business/Enterprise plans
            // report a raw balance with no cap, so we must never fabricate one (the old code slapped
            // `.WithLimit(1000)` on every balance, which rendered a bogus "1,000 / 1,000"). A limit is
            // surfaced only when the API actually reports one; otherwise the balance shows on its own.
            double? balance = headerCredits;
            double? limit = null;

            if (json.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
            {
                // Unlimited orgs have no meaningful credit balance to show.
                if (credits.TryGetProperty("unlimited", out var un) && un.ValueKind == JsonValueKind.True)
                    return null;

                bool hasCredits = credits.TryGetProperty("has_credits", out var hc) && hc.ValueKind == JsonValueKind.True;
                balance ??= TryF64(credits, "balance") ?? (hasCredits ? 0 : null);
                limit = TryF64(credits, "limit") ?? TryF64(credits, "total_granted") ?? TryF64(credits, "granted") ?? TryF64(credits, "monthly_limit");

                if (!hasCredits && balance is null)
                    return null;
            }

            if (balance is null)
                return null;

            var snapshot = new CostSnapshot(balance.Value, "credits", "Credits");
            if (limit is { } lim && lim > 0)
                snapshot.WithLimit(lim);
            return snapshot;
        }

        private static ResetCreditsSnapshot? ExtractResetCredits(JsonElement? maybeJson)
        {
            if (maybeJson is not JsonElement json || json.ValueKind != JsonValueKind.Object)
                return null;

            var credits = new List<ResetCreditGrant>();
            if (json.TryGetProperty("credits", out var creditsEl) && creditsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in creditsEl.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object)
                        continue;

                    string status = entry.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                        ? statusEl.GetString() ?? string.Empty
                        : string.Empty;
                    if (!string.Equals(status, "available", StringComparison.OrdinalIgnoreCase))
                        continue;

                    credits.Add(new ResetCreditGrant(
                        status,
                        TryDateTimeOffset(entry, "granted_at"),
                        TryDateTimeOffset(entry, "expires_at")));
                }
            }

            int? availableCount = TryInt32(json, "available_count");
            if (availableCount is null && credits.Count == 0)
                return null;

            credits.Sort(static (left, right) => Nullable.Compare(left.ExpiresAt, right.ExpiresAt));
            return new ResetCreditsSnapshot(availableCount ?? credits.Count, credits);
        }

        private static double? TryHeaderF64(HttpResponseMessage response, string name)
        {
            if (!response.Headers.TryGetValues(name, out var values)) return null;
            foreach (var value in values)
            {
                if (double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }
            return null;
        }

        private static double? TryF64(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var el)) return null;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.String when double.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var v) => v,
                _ => null,
            };
        }

        private static int? TryInt32(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var el)) return null;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value)) return value;
            if (el.ValueKind == JsonValueKind.String && int.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value)) return value;
            return null;
        }

        private static DateTimeOffset? TryDateTimeOffset(JsonElement parent, string name)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
                return null;

            return DateTimeOffset.TryParse(
                el.GetString(),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed)
                ? parsed
                : null;
        }

        private static string PlanDisplay(string pt) => NormalizePlanType(pt) switch
        {
            "guest" => "Guest",
            "free" => "Free",
            "go" => "Go",
            "plus" => "Plus",
            "pro" => "Pro 20x",
            "pro_lite" or "prolite" or "pro-lite" => "Pro 5x",
            "team" => "Team",
            "business" => "Business",
            "enterprise" => "Enterprise",
            "education" or "edu" => "Education",
            _ => Capitalize(pt),
        };

        private static string NormalizePlanType(string? pt) => (pt ?? string.Empty).Trim().ToLowerInvariant();

        private static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

        private sealed record Credentials(string AccessToken, string? AccountId);

        private static Credentials LoadCredentials()
        {
            var authPath = GetAuthPath();
            if (!File.Exists(authPath))
                throw new ProviderException(ProviderErrorKind.AuthRequired, "Codex auth.json not found. Run `codex login`.");

            using var doc = JsonDocument.Parse(File.ReadAllText(authPath));
            var root = doc.RootElement;

            if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
            {
                string? access = tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                if (!string.IsNullOrEmpty(access))
                {
                    string? acct = tokens.TryGetProperty("account_id", out var ac) ? ac.GetString() : null;
                    return new Credentials(access!, acct);
                }
            }

            if (root.TryGetProperty("OPENAI_API_KEY", out var key) && key.ValueKind == JsonValueKind.String)
            {
                var k = key.GetString()?.Trim();
                if (!string.IsNullOrEmpty(k)) return new Credentials(k!, null);
            }

            throw new ProviderException(ProviderErrorKind.Parse, "Codex auth.json contains no usable token.");
        }

        private static string GetAuthPath()
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
            if (!string.IsNullOrEmpty(codexHome))
                return Path.Combine(codexHome, "auth.json");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");
        }

        private static string ResolveBaseUrl()
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME")?.Trim();
            var configPath = !string.IsNullOrEmpty(codexHome)
                ? Path.Combine(codexHome, "config.toml")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "config.toml");

            if (File.Exists(configPath))
            {
                foreach (var raw in File.ReadAllLines(configPath))
                {
                    var line = raw.Split('#')[0].Trim();
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    if (line[..eq].Trim() != "chatgpt_base_url") continue;
                    var val = line[(eq + 1)..].Trim().Trim('"', '\'');
                    var normalized = NormalizeBaseUrl(val);
                    if (normalized.StartsWith("https://") || normalized.StartsWith("http://127.0.0.1") || normalized.StartsWith("http://localhost"))
                        return normalized;
                }
            }
            return DefaultBaseUrl;
        }

        private static string NormalizeBaseUrl(string url)
        {
            var trimmed = url.Trim().TrimEnd('/');
            if (trimmed.Length == 0) return DefaultBaseUrl;
            if ((trimmed.StartsWith("https://chatgpt.com") || trimmed.StartsWith("https://chat.openai.com")) && !trimmed.Contains("/backend-api"))
                trimmed += "/backend-api";
            return trimmed;
        }
    }
}
