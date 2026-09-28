using System.IO;
using CodexQuota.Usage;

namespace CodexQuota.Tests;

public class FetchCachePolicyTests
{
    [Fact]
    public void TtlForFailure_RateLimited_UsesFiveMinuteBackoff()
        => Assert.Equal(UsageService.RateLimitedCacheTtl, FetchCachePolicy.TtlForFailure(ProviderErrorKind.RateLimited));

    [Fact]
    public void TtlForFailure_OtherErrors_UsesShortBackoff()
        => Assert.Equal(UsageService.FailureCacheTtl, FetchCachePolicy.TtlForFailure(ProviderErrorKind.Other));

    [Fact]
    public void TtlForSuccess_UsesSixtySeconds()
        => Assert.Equal(UsageService.SuccessCacheTtl, FetchCachePolicy.TtlForSuccess());

    [Fact]
    public async Task FetchAsync_RateLimitedAfterSuccess_ReturnsLastSuccessfulLiveResult()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        provider.NextException = new ProviderException(ProviderErrorKind.RateLimited, "429");

        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Same(first.Fetch, second.Fetch);
        Assert.Equal(42, second.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(73, second.Fetch.Usage.Secondary!.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_TransientFailureAfterSuccess_DoesNotPublishZeroUsage()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        provider.NextException = new ProviderException(ProviderErrorKind.Other, "Codex API returned 500");

        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.True(first.Ok);
        Assert.True(second.Ok);
        Assert.Equal(42, second.Fetch!.Usage.Primary.UsedPercent);
        Assert.Equal(73, second.Fetch.Usage.Secondary!.UsedPercent);
        Assert.NotEqual(0, second.Fetch.Usage.Primary.UsedPercent);
        Assert.NotEqual(0, second.Fetch.Usage.Secondary.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_AuthFailureAfterSuccess_ReturnsFailureInsteadOfStaleUsage()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        provider.NextException = new ProviderException(ProviderErrorKind.AuthRequired, "Codex OAuth token expired.");

        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.True(first.Ok);
        Assert.False(second.Ok);
        Assert.Contains("expired", second.Error);
    }

    [Fact]
    public async Task FetchAsync_SameLiveUsage_ReturnsFreshResultWithCurrentTimestamp()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        // Values are unchanged, but each confirmed check must carry its own timestamp: the widget's
        // "Last updated" line would otherwise go "(stale)" while refreshes keep succeeding.
        Assert.NotSame(first, second);
        Assert.Equal(first.Fetch!.Usage.Primary.UsedPercent, second.Fetch!.Usage.Primary.UsedPercent);
        Assert.True(second.Fetch.FetchedAt >= first.Fetch.FetchedAt);
        Assert.Equal(2, provider.FetchCount);
    }

    [Fact]
    public async Task FetchAsync_ConfirmingDiskRestoredSnapshot_ReturnsFreshNonStaleResult()
    {
        // Boot path: yesterday's snapshot is restored from disk as stale. A live fetch returning the
        // same values must publish fresh (non-stale) data with a current FetchedAt — not the restored
        // baseline whose age reads as "(stale)" in the flyout right after a successful refresh.
        var dir = Path.Combine(Path.GetTempPath(), "cq-cache-" + Path.GetRandomFileName());
        try
        {
            var staleFetchTime = DateTimeOffset.Now.AddHours(-20);
            UsageSnapshotStore.Save(dir, new Dictionary<ProviderId, UsageResult>
            {
                [ProviderId.Codex] = ResultWith(42, 73, staleFetchTime),
            });

            var service = new UsageService(dir);
            service.Register(new FlakyProvider());

            var fresh = await service.FetchAsync(ProviderId.Codex, force: true);

            Assert.True(fresh.Ok);
            Assert.False(fresh.IsStale);
            Assert.True(fresh.Fetch!.FetchedAt > staleFetchTime);
            Assert.Equal(42, fresh.Fetch.Usage.Primary.UsedPercent);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static UsageResult ResultWith(double primary, double secondary, DateTimeOffset fetchedAt)
        => UsageResult.Success(
            ProviderId.Codex,
            new FlakyProvider(),
            new ProviderFetchResult(
                new UsageSnapshot(new RateWindow(primary)) { Secondary = new RateWindow(secondary), LoginMethod = "Max" },
                "live",
                fetchedAt));

    [Fact]
    public async Task FetchAsync_ResetCrossedInsideTtl_BypassesCacheWithLiveFetch()
    {
        // Quota reset 10s ago, cached fetch 30s ago: the success TTL (60s) is still valid, but the
        // cached values are pre-reset and must not be served — the non-forced fetch goes live.
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        DateTimeOffset now = DateTimeOffset.Now;
        provider.NextFetchedAt = now.AddSeconds(-30);
        provider.NextResetAt = now.AddSeconds(-10);
        var first = await service.FetchAsync(ProviderId.Codex);
        Assert.True(first.Ok);

        provider.NextPrimaryPercent = 5;
        var second = await service.FetchAsync(ProviderId.Codex);

        Assert.Equal(2, provider.FetchCount);
        Assert.Equal(5, second.Fetch!.Usage.Primary.UsedPercent);
    }

    [Fact]
    public async Task FetchAsync_FutureResetInsideTtl_ServesCache()
    {
        // No reset crossed since the cached fetch: the TTL still applies and no live fetch runs.
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        DateTimeOffset now = DateTimeOffset.Now;
        provider.NextFetchedAt = now.AddSeconds(-30);
        provider.NextResetAt = now.AddHours(1);
        var first = await service.FetchAsync(ProviderId.Codex);

        provider.NextPrimaryPercent = 5;
        var second = await service.FetchAsync(ProviderId.Codex);

        Assert.Same(first, second);
        Assert.Equal(1, provider.FetchCount);
    }

    [Fact]
    public async Task FetchAsync_WindowVisibilityChange_IsNotCollapsedAsSameUsage()
    {
        var service = new UsageService();
        var provider = new FlakyProvider();
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        provider.NextHasPrimaryWindow = false;

        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.NotSame(first, second);
        Assert.False(second.Fetch!.Usage.HasPrimaryWindow);
    }

    [Fact]
    public async Task FetchAsync_FreshFetchOmitsCost_RetainsPreviousCost()
    {
        // Flaky credits field: first live fetch reports a balance, the next omits it. The
        // published result must keep the previous Cost instead of flickering to null.
        var service = new UsageService();
        var provider = new FlakyProvider { NextCost = new CostSnapshot(12.5, "credits", "Credits") };
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        Assert.NotNull(first.Fetch!.Usage.Cost);

        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.NotNull(second.Fetch!.Usage.Cost);
        Assert.Equal(12.5, second.Fetch.Usage.Cost!.Amount);
    }

    [Fact]
    public async Task FetchAsync_FreshFetchOmitsResetCredits_RetainsPreviousResetCredits()
    {
        // The 3s reset-credits sub-request can time out on a single poll: a fresh null must
        // not flap the reset-credits row away while the previous live value stands.
        var service = new UsageService();
        var provider = new FlakyProvider
        {
            NextResetCredits = new ResetCreditsSnapshot(3, Array.Empty<ResetCreditGrant>()),
        };
        service.Register(provider);

        var first = await service.FetchAsync(ProviderId.Codex, force: true);
        Assert.NotNull(first.Fetch!.Usage.ResetCredits);

        var second = await service.FetchAsync(ProviderId.Codex, force: true);

        Assert.NotNull(second.Fetch!.Usage.ResetCredits);
        Assert.Equal(3, second.Fetch.Usage.ResetCredits!.AvailableCount);
    }

    private sealed class FlakyProvider : IUsageProvider
    {
        public ProviderException? NextException { get; set; }
        public double? NextPrimaryPercent { get; set; }
        public double? NextSecondaryPercent { get; set; }
        public CostSnapshot? NextCost { get; set; }
        public ResetCreditsSnapshot? NextResetCredits { get; set; }
        public DateTimeOffset? NextResetAt { get; set; }
        public DateTimeOffset? NextFetchedAt { get; set; }
        public bool? NextHasPrimaryWindow { get; set; }
        public int FetchCount { get; private set; }

        public ProviderId Id => ProviderId.Codex;
        public string DisplayName => "Codex CLI";
        public string SessionLabel => "Session";
        public string WeeklyLabel => "Weekly";
        public BillingKind Billing => BillingKind.Subscription;

        public Task<ProviderFetchResult> FetchUsageAsync(CancellationToken ct = default)
        {
            FetchCount++;
            if (NextException is { } exception)
            {
                NextException = null;
                throw exception;
            }

            var usage = new UsageSnapshot(new RateWindow(NextPrimaryPercent ?? 42, resetAt: NextResetAt))
            {
                HasPrimaryWindow = NextHasPrimaryWindow ?? true,
                Secondary = new RateWindow(NextSecondaryPercent ?? 73, resetAt: NextResetAt),
                LoginMethod = "Max",
                Cost = NextCost,
                ResetCredits = NextResetCredits,
            };
            NextPrimaryPercent = null;
            NextSecondaryPercent = null;
            NextCost = null;
            NextResetCredits = null;
            NextResetAt = null;
            NextHasPrimaryWindow = null;
            var fetchedAt = NextFetchedAt;
            NextFetchedAt = null;
            return Task.FromResult(new ProviderFetchResult(usage, "live", fetchedAt));
        }
    }
}
