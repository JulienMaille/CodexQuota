using System;
using System.Collections.Generic;

namespace CodexQuota.Usage
{
    public enum ProviderId
    {
        Codex,
    }

    /// <summary>A single rate-limit window (for example session or weekly), expressed as percent used.</summary>
    public sealed class RateWindow
    {
        public double UsedPercent { get; init; }
        public int? WindowMinutes { get; init; }
        public DateTimeOffset? ResetAt { get; init; }
        public string? ResetDescription { get; init; }
        /// <summary>Optional bar label override (e.g. "Spend limit" for Claude Enterprise), when the
        /// window isn't the provider's default Session/Weekly meter.</summary>
        public string? Label { get; init; }
        /// <summary>When true this meter's value is a monetary/credit spend (rendered from the snapshot's
        /// <see cref="UsageSnapshot.Cost"/>, e.g. "$9.27/$100.00") rather than a plain used-percent. Kept
        /// separate from <see cref="Label"/> so a mere label override (e.g. Codex "Weekly") never flips a
        /// usage-% bar into a spend value.</summary>
        public bool ShowCostValue { get; init; }

        public RateWindow(double usedPercent, int? windowMinutes = null, DateTimeOffset? resetAt = null, string? resetDescription = null, string? label = null)
        {
            UsedPercent = Math.Clamp(usedPercent, 0, 100);
            WindowMinutes = windowMinutes;
            ResetAt = resetAt;
            ResetDescription = resetDescription;
            Label = label;
        }

        public double RemainingPercent => 100 - UsedPercent;
    }

    public sealed class NamedRateWindow
    {
        public string Id { get; }
        public string Title { get; }
        public RateWindow Window { get; }

        public NamedRateWindow(string id, string title, RateWindow window)
        {
            Id = id;
            Title = title;
            Window = window;
        }
    }

    /// <summary>Monetary balance / spend info for API-billed providers.</summary>
    public sealed class CostSnapshot
    {
        public double Amount { get; }
        public string Currency { get; }
        public string Label { get; }
        public double? Limit { get; set; }
        public DateTimeOffset? ResetsAt { get; set; }

        public CostSnapshot(double amount, string currency, string label)
        {
            Amount = amount;
            Currency = currency;
            Label = label;
        }

        public CostSnapshot WithLimit(double limit) { Limit = limit; return this; }
        public CostSnapshot WithResetsAt(DateTimeOffset at) { ResetsAt = at; return this; }

        private string Money(double v) =>
            string.Equals(Currency, "USD", StringComparison.OrdinalIgnoreCase) ? $"${v:0.00}" : $"{v:0.00} {Currency}";

        public string Display => Limit is double lim ? $"{Money(Amount)} / {Money(lim)}" : Money(Amount);
    }

    /// <summary>
    /// Metered spend beyond included usage. Copilot reports this in USD (overage budget); Grok reports
    /// it in credits (the on-demand / pay-as-you-go cap), so <see cref="IsCredits"/> selects the units.
    /// </summary>
    public sealed class AdditionalUsageSnapshot
    {
        public bool Enabled { get; init; }
        public double SpentUsd { get; init; }
        public double? BudgetUsd { get; init; }
        /// <summary>When true, the spent/budget values are credit counts rather than US dollars.</summary>
        public bool IsCredits { get; init; }

        public string StatusText => Enabled ? "Enabled" : "Not enabled";

        public string SpendText
        {
            get
            {
                string spent = Amount(SpentUsd);
                string suffix = IsCredits ? "credits" : "budget";
                if (!Enabled)
                    return $"{spent} / {(IsCredits ? "0" : "$0")} {suffix}";
                return BudgetUsd is double budget
                    ? $"{spent} / {Amount(budget)} {suffix}"
                    : $"{spent} / · {suffix}";
            }
        }

        private string Amount(double value)
            => IsCredits ? $"{value:0}" : $"${value:0.00}";
    }

    /// <summary>Codex rate-limit reset credits granted by the Codex backend.</summary>
    public sealed class ResetCreditsSnapshot
    {
        public int AvailableCount { get; }
        public IReadOnlyList<ResetCreditGrant> Credits { get; }

        public ResetCreditsSnapshot(int availableCount, IReadOnlyList<ResetCreditGrant> credits)
        {
            AvailableCount = Math.Max(0, availableCount);
            Credits = credits;
        }

        public DateTimeOffset? EarliestExpiresAt
        {
            get
            {
                DateTimeOffset? earliest = null;
                foreach (var credit in Credits)
                {
                    if (credit.ExpiresAt is not { } expiresAt)
                        continue;

                    if (earliest is null || expiresAt < earliest)
                        earliest = expiresAt;
                }

                return earliest;
            }
        }
    }

    public sealed class ResetCreditGrant
    {
        public string Status { get; }
        public DateTimeOffset? GrantedAt { get; }
        public DateTimeOffset? ExpiresAt { get; }

        public ResetCreditGrant(string status, DateTimeOffset? grantedAt, DateTimeOffset? expiresAt)
        {
            Status = status;
            GrantedAt = grantedAt;
            ExpiresAt = expiresAt;
        }
    }

    /// <summary>Normalized usage data for a provider (session / weekly / model-specific windows).</summary>
    public sealed class UsageSnapshot
    {
        public RateWindow Primary { get; }            // session
        /// <summary>False when the provider reported no session window at all (e.g. Codex org/Business
        /// plans that only expose credits): the card skips the primary bar instead of showing "0%".</summary>
        public bool HasPrimaryWindow { get; set; } = true;
        public RateWindow? Secondary { get; set; }    // weekly
        public RateWindow? ModelSpecific { get; set; }// e.g. Opus / code review
        public RateWindow? Monthly { get; set; }      // monthly window when available
        public List<NamedRateWindow> ExtraRateWindows { get; } = new();
        public string? LoginMethod { get; set; }
        public string? Email { get; set; }
        public CostSnapshot? Cost { get; set; }
        public AdditionalUsageSnapshot? AdditionalUsage { get; set; }
        public ResetCreditsSnapshot? ResetCredits { get; set; }

        public UsageSnapshot(RateWindow primary) => Primary = primary;

        public UsageSnapshot WithSecondary(RateWindow w) { Secondary = w; return this; }
        public UsageSnapshot WithModelSpecific(RateWindow w) { ModelSpecific = w; return this; }
        public UsageSnapshot WithLoginMethod(string m) { LoginMethod = m; return this; }
        public UsageSnapshot WithEmail(string e) { Email = e; return this; }
        public UsageSnapshot WithCost(CostSnapshot c) { Cost = c; return this; }
    }

    public sealed class ProviderFetchResult
    {
        public UsageSnapshot Usage { get; }
        public string SourceLabel { get; }
        public DateTimeOffset FetchedAt { get; }

        /// <param name="fetchedAt">Original fetch time; only passed when restoring a persisted snapshot.</param>
        public ProviderFetchResult(UsageSnapshot usage, string sourceLabel, DateTimeOffset? fetchedAt = null)
        {
            Usage = usage;
            SourceLabel = sourceLabel;
            FetchedAt = fetchedAt ?? DateTimeOffset.Now;
        }
    }

    /// <summary>One day (or week) of token usage reported by the Profile endpoint's buckets.</summary>
    public sealed class ProfileUsageBucket
    {
        public string StartDate { get; }
        public long Tokens { get; }

        public ProfileUsageBucket(string startDate, long tokens)
        {
            StartDate = startDate;
            Tokens = tokens;
        }
    }

    /// <summary>A single entry in the Profile endpoint's "most used" plugins/skills list.</summary>
    public sealed class ProfileTopInvocation
    {
        public string? Type { get; }
        public string? Name { get; }
        public long UsageCount { get; }

        public ProfileTopInvocation(string? type, string? name, long usageCount)
        {
            Type = type;
            Name = name;
            UsageCount = usageCount;
        }
    }

    /// <summary>
    /// Codex account-level profile statistics served by <c>/backend-api/wham/profiles/me</c> — the data
    /// behind the Codex/ChatGPT "Profile" dashboard (lifetime tokens, peak daily tokens, longest running
    /// turn, streaks, token-activity buckets, and most-used plugins). Server-side aggregation: values lag
    /// real usage and may omit the current day (openai/codex#25479, #26192, #31010); the current day can
    /// be supplemented from local Codex session journals.
    /// </summary>
    public sealed class CodexProfileSnapshot
    {
        public string? Username { get; init; }
        public string? DisplayName { get; init; }
        public long LifetimeTokens { get; init; }
        public long PeakDailyTokens { get; init; }
        public long LongestRunningTurnSec { get; init; }
        public int CurrentStreakDays { get; init; }
        public int LongestStreakDays { get; init; }
        public long TotalThreads { get; init; }
        public double? FastModeUsagePercentage { get; init; }
        public string? MostUsedReasoningEffort { get; init; }
        public double? MostUsedReasoningEffortPercentage { get; init; }
        public int UniqueSkillsUsed { get; init; }
        public int TotalSkillsUsed { get; init; }
        public IReadOnlyList<ProfileUsageBucket> DailyUsageBuckets { get; init; } = Array.Empty<ProfileUsageBucket>();
        /// <summary>True when today's bucket was supplemented by local Codex session journals.</summary>
        public bool TodayUsageIsLocal { get; init; }
        public IReadOnlyList<ProfileUsageBucket> WeeklyUsageBuckets { get; init; } = Array.Empty<ProfileUsageBucket>();
        public IReadOnlyList<ProfileTopInvocation> TopInvocations { get; init; } = Array.Empty<ProfileTopInvocation>();
        public DateTimeOffset? GeneratedAt { get; init; }
        public string? StatsAsOf { get; init; }

        /// <summary>
        /// Merges locally observed usage into today's bucket without replacing a larger server value.
        /// The profile endpoint remains authoritative for all historical days.
        /// </summary>
        public CodexProfileSnapshot WithLiveToday(DateOnly today, long liveTokens)
        {
            if (liveTokens <= 0)
                return this;

            string todayKey = today.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
            var merged = new List<ProfileUsageBucket>(DailyUsageBuckets.Count + 1);
            bool foundToday = false;
            bool localWon = false;

            foreach (var bucket in DailyUsageBuckets)
            {
                if (!string.Equals(bucket.StartDate, todayKey, StringComparison.Ordinal))
                {
                    merged.Add(bucket);
                    continue;
                }

                foundToday = true;
                long tokens = Math.Max(bucket.Tokens, liveTokens);
                localWon |= liveTokens > bucket.Tokens;
                merged.Add(new ProfileUsageBucket(todayKey, tokens));
            }

            if (!foundToday)
            {
                merged.Add(new ProfileUsageBucket(todayKey, liveTokens));
                localWon = true;
            }

            if (!localWon)
                return this;

            return new CodexProfileSnapshot
            {
                Username = Username,
                DisplayName = DisplayName,
                LifetimeTokens = LifetimeTokens,
                PeakDailyTokens = PeakDailyTokens,
                LongestRunningTurnSec = LongestRunningTurnSec,
                CurrentStreakDays = CurrentStreakDays,
                LongestStreakDays = LongestStreakDays,
                TotalThreads = TotalThreads,
                FastModeUsagePercentage = FastModeUsagePercentage,
                MostUsedReasoningEffort = MostUsedReasoningEffort,
                MostUsedReasoningEffortPercentage = MostUsedReasoningEffortPercentage,
                UniqueSkillsUsed = UniqueSkillsUsed,
                TotalSkillsUsed = TotalSkillsUsed,
                DailyUsageBuckets = merged,
                TodayUsageIsLocal = true,
                WeeklyUsageBuckets = WeeklyUsageBuckets,
                TopInvocations = TopInvocations,
                GeneratedAt = GeneratedAt,
                StatsAsOf = StatsAsOf,
            };
        }
    }

    public enum ProviderErrorKind
    {
        NotRunning,
        AuthRequired,
        Timeout,
        RateLimited,
        Parse,
        Other,
    }

    public sealed class ProviderException : Exception
    {
        public ProviderErrorKind Kind { get; }
        public ProviderException(ProviderErrorKind kind, string message) : base(message) => Kind = kind;
    }
}
