using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CodexQuota.Usage;
using CodexQuota.Usage.Providers;

namespace CodexQuota
{
    /// <summary>
    /// Drives the app: on a timer it fetches Codex usage and raises <see cref="StateChanged"/> so the
    /// widget and flyout can update. Single shared instance for the process.
    /// </summary>
    public sealed class UsageCoordinator
    {
        public static UsageCoordinator Instance { get; } = new();

        private readonly UsageService _service = new(UsageSnapshotStore.DefaultDirectory);
        private Timer? _timer;
        private UsageResult? _lastState;
        private CodexProfileSnapshot? _lastProfile;
        private int _pollInFlight;
        private volatile bool _flyoutOpen;
        private DateTimeOffset? _lastFlyoutOpenAtUtc;

        public UsageService Service => _service;

        /// <summary>The single provider this build supports.</summary>
        public ProviderId? ActiveProvider => ProviderId.Codex;

        public ProviderId? WidgetDisplayProvider => ProviderId.Codex;

        /// <summary>Maximum number of quota tile slots allocated by the widget.</summary>
        public const int MaxWidgetTiles = 3;

        /// <summary>Effective quota-tile cap (no activity discount — Codex-only).</summary>
        public static int MaxDisplayedWidgetTiles => MaxWidgetTiles;

        /// <summary>Every provider the taskbar widget renders as its own tile.</summary>
        public IReadOnlyList<ProviderId> WidgetDisplayProviders => new[] { ProviderId.Codex };

        /// <summary>Most recently active providers (Codex is the only one).</summary>
        public IReadOnlyList<ProviderId> RecentProviders => new[] { ProviderId.Codex };

        /// <summary>Last usage snapshot pushed to listeners; used to hydrate the widget if created late.</summary>
        public UsageResult? LastState => _lastState;

        /// <summary>Last Codex profile snapshot pushed to listeners; used to hydrate the flyout if created late.</summary>
        public CodexProfileSnapshot? LastProfile => _lastProfile;

        /// <summary>Codex is always present when this app runs.</summary>
        public bool IsActiveToolPresent => true;

        /// <summary>Focus never hides the tile — Codex-only build.</summary>
        public bool IsActiveTileAllowedByFocus => true;

        /// <summary>Raised whenever a fetch completes (success or degraded failure), with the new state.</summary>
        public event Action<UsageResult>? StateChanged;

        /// <summary>Raised whenever the Codex profile fetch completes with a parsed snapshot.</summary>
        public event Action<CodexProfileSnapshot>? ProfileChanged;

        /// <summary>Starts the poll loop. Idempotent; fires the first fetch immediately.</summary>
        public void Start()
        {
            if (_timer != null)
                return;

            _timer = new Timer(
                static _ => _ = Instance.RunPollTickAsync(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            ScheduleNextPoll(TimeSpan.Zero);
        }

        /// <summary>Re-arms the one-shot poll timer with the policy-chosen delay.</summary>
        private void ScheduleNextPoll(TimeSpan delay)
        {
            _timer?.Change(delay, Timeout.InfiniteTimeSpan);
        }

        private async Task RunPollTickAsync()
        {
            try
            {
                // A tick that outlives its delay must not overlap the next: the usage + profile
                // fetches would double-pump the API. The user-driven refresh path (force: true) and
                // the taskbar-sync alias stay unguarded so they always run.
                if (Interlocked.Exchange(ref _pollInFlight, 1) != 0)
                    return;
                try
                {
                    await FetchAndPublishAsync(force: false).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref _pollInFlight, 0);
                }
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Poll tick failed");
            }
        }

        /// <summary>Cheap probe for an actively running Codex CLI; refreshes speed up while it runs.</summary>
        private static bool IsCodexProcessRunning()
        {
            try
            {
                var processes = System.Diagnostics.Process.GetProcessesByName("codex");
                try
                {
                    return processes.Length > 0;
                }
                finally
                {
                    foreach (var process in processes)
                        process.Dispose();
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The flyout became visible; the poll stays on the fast cadence while it is open.</summary>
        public void NotifyFlyoutOpen()
        {
            _flyoutOpen = true;
            _lastFlyoutOpenAtUtc = DateTimeOffset.UtcNow;
        }

        /// <summary>The flyout hid; the poll may back off on the next re-arm.</summary>
        public void NotifyFlyoutClosed() => _flyoutOpen = false;

/// <summary>Fetches Codex usage and publishes the result. Failures degrade to a cached or failure
        /// <see cref="UsageResult"/> and are logged, never thrown.</summary>
        public async Task FetchAndPublishAsync(bool force)
        {
            try
            {
                var result = await _service.FetchAsync(ProviderId.Codex, force).ConfigureAwait(false);
                _lastState = result;
                StateChanged?.Invoke(result);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Codex usage fetch failed");
            }

            // The profile feed updates the heatmap; keep it fresh on the same cadence. Independent of
            // the usage fetch so one failing never blocks the other.
            await FetchProfileAsync().ConfigureAwait(false);

            // Every completed fetch re-arms the next poll at the policy-chosen delay, so a manual
            // refresh or a fetch-on-open also re-bases the cadence (CodexBar-style adaptive refresh).
            ScheduleNextPoll(AdaptiveRefreshPolicy.NextDelay(
                _flyoutOpen,
                _lastFlyoutOpenAtUtc,
                DateTimeOffset.UtcNow,
                IsCodexProcessRunning()));
        }

        /// <summary>Fetches the Codex profile (lifetime tokens, daily activity buckets) and raises
        /// <see cref="ProfileChanged"/> on success. Errors are logged, never thrown.</summary>
        public async Task FetchProfileAsync()
        {
            try
            {
                if (_service.Get(ProviderId.Codex) is not CodexProvider provider)
                    return;

                var profile = await provider.FetchProfileAsync().ConfigureAwait(false);
                _lastProfile = profile;
                ProfileChanged?.Invoke(profile);
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Codex profile fetch failed");
            }
        }

        /// <summary>Thin alias for <see cref="FetchAndPublishAsync"/>, kept for the taskbar sync path.</summary>
        public Task TickAsync(bool force = false) => FetchAndPublishAsync(force);
    }
}
