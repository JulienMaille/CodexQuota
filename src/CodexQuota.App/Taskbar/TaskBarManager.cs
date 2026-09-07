using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using CodexQuota.Diagnostics;
using CodexQuota.Usage;

namespace CodexQuota.Taskbar
{
    /// <summary>Owns the injected taskbar widgets and pushes coordinator state into every widget.</summary>
    internal static class TaskBarManager
    {
        private static readonly Dictionary<IntPtr, TaskBarWidget> Widgets = new();
        // Reused snapshot of Widgets.Values, so iterating it while a callback may mutate the dictionary
        // doesn't allocate. Only valid until the next SnapshotWidgets call, and only used on the UI thread.
        private static readonly List<TaskBarWidget> _widgetBuffer = new();
        private static FlyoutWindow? _flyout;
        private static DispatcherQueue? _dispatcher;
        private static DispatcherTimer? _widgetHealthTimer;
        private static bool _initialized;
        private static bool _isReconcilingWidgets;
        private static ProviderId? _lastLoggedWidgetApplyProvider;
        // Consecutive health ticks where a widget's taskbar handle was absent from the EnumWindows
        // list. Explorer briefly hides its taskbar window from enumeration while busy (redraw,
        // notification, fullscreen focus change); reacting on the first miss destroys and
        // re-injects a healthy widget for a ~5s visible "restart". Only recreate after sustained
        // absence — a genuinely gone taskbar (monitor unplug, Explorer restart with new handle)
        // stays absent, while a transient miss recovers on the next tick.
        private static readonly Dictionary<IntPtr, int> _missingHandleTicks = new();
        private const int MissingHandleRecreateThreshold = 3;

        public static void Initialize(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;

            EnsureWidgets();

            if (!_initialized)
            {
                UsageCoordinator.Instance.StateChanged += OnStateChanged;
                App.Quitting += OnQuitting;
                _initialized = true;
            }

            StartWidgetHealthTimer();
        }

        private static void StartWidgetHealthTimer()
        {
            if (_widgetHealthTimer != null)
                return;

            _widgetHealthTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _widgetHealthTimer.Tick += (_, _) =>
            {
                EnsureWidgets();
                // Re-run the tile-fit math against the gap the last position pass measured, so tiles that
                // were trimmed off a crowded taskbar come back once there is room for them again.
                // Iterated over the reused buffer rather than Widgets.Values.ToArray(), which allocated an
                // array every five seconds for the life of the process.
                SnapshotWidgets();
                foreach (var widget in _widgetBuffer)
                {
                    if (widget.IsAlive)
                        widget.RefreshLayout();
                }
            };
            _widgetHealthTimer.Start();
        }

        private static void EnsureWidgets()
        {
            if (_isReconcilingWidgets)
                return;

            _isReconcilingWidgets = true;
            try
            {
                if (!TaskbarWindowTarget.TryFindAll(out var targets))
                {
                    Log.Warning("Could not enumerate Windows taskbars; keeping existing widgets until the next health check");
                    return;
                }
                var targetsByHandle = targets.ToDictionary(target => target.Handle);

                foreach (var pair in Widgets.ToArray())
                {
                    if (targetsByHandle.TryGetValue(pair.Key, out var target)
                        && pair.Value.IsAlive
                        // A window whose host content was never built renders nothing and cannot recover on
                        // its own — it is dead in every way that matters to the user, so recreate it.
                        && pair.Value.IsHostContentReady
                        && pair.Value.IsDpiCurrent
                        && pair.Value.MatchesTarget(target))
                    {
                        _missingHandleTicks.Remove(pair.Key);
                        continue;
                    }

                    // The taskbar handle alone can vanish for a tick while Explorer is busy, then come
                    // back on the next enumeration. Debounce that case: every other trigger (dead host,
                    // DPI, target mismatch) recreates immediately, but a missing handle must persist
                    // across ticks before the widget is torn down.
                    if (!targetsByHandle.ContainsKey(pair.Key) && pair.Value.IsAlive)
                    {
                        int misses = _missingHandleTicks.TryGetValue(pair.Key, out int n) ? n + 1 : 1;
                        _missingHandleTicks[pair.Key] = misses;
                        if (misses < MissingHandleRecreateThreshold)
                        {
                            Log.Debug($"Taskbar handle absent ({misses}/{MissingHandleRecreateThreshold}); keeping widget taskbar=0x{pair.Key.ToInt64():X}");
                            continue;
                        }
                    }
                    _missingHandleTicks.Remove(pair.Key);

                    // Name the exact trigger: the single "target taskbar, or DPI changed" line made
                    // every 5s health-tick recreate look identical, hiding whether the taskbar handle,
                    // its monitor key, the DPI, or a half-built host caused the visible "restart".
                    string reason = DescribeRecreateReason(pair.Value,
                        targetsByHandle.TryGetValue(pair.Key, out var current) ? current : null);
                    Widgets.Remove(pair.Key);
                    Log.Warning($"Taskbar widget recreating: {reason} taskbar=0x{pair.Key.ToInt64():X}");
                    try { pair.Value.Dispose(); }
                    catch (Exception ex) { Log.Warning(ex, "Failed to dispose missing taskbar widget"); }
                }

                foreach (var target in targets)
                {
                    if (!Widgets.ContainsKey(target.Handle))
                        CreateWidget(target);
                }

                // Drop miss counters for handles that no longer have a widget (disposed via the
                // Destroying path), so a stale count can't fast-track a future recreate.
                foreach (var handle in _missingHandleTicks.Keys.ToArray())
                {
                    if (!Widgets.ContainsKey(handle))
                        _missingHandleTicks.Remove(handle);
                }
            }
            finally
            {
                _isReconcilingWidgets = false;
            }
        }

        private static string DescribeRecreateReason(TaskBarWidget widget, TaskbarWindowTarget? target)
        {
            if (!widget.IsAlive)
                return "host window dead;";
            if (!widget.IsHostContentReady)
                return "host content never built;";
            if (target is null)
                return "taskbar handle gone;";
            if (!widget.IsDpiCurrent)
                return $"DPI changed (widget={widget.TaskbarDpi} current={TaskBarWidget.CurrentDpiFor(widget.TaskbarHandle)});";
            return $"target mismatch (primary {widget.IsPrimaryTaskbar}->{target.Value.IsPrimary} display '{widget.DisplayKey}'->'{target.Value.DisplayKey}');";
        }

        private static void CreateWidget(TaskbarWindowTarget target)
        {
            TaskBarWidget? widget = null;
            try
            {
                widget = new TaskBarWidget(target);
                widget.Initialize();
                widget.Destroying += (sender, _) =>
                {
                    if (sender is TaskBarWidget destroyedWidget)
                        _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => OnWidgetDestroying(destroyedWidget));
                };
                widget.HydrateProvider = provider => HydrateResult(UsageCoordinator.Instance, provider);
                widget.Clicked += () => _dispatcher?.TryEnqueue(() => ToggleFlyout(widget));
                Widgets[target.Handle] = widget;
                SyncWidgetState(widget);
                PrewarmFlyout();
                Log.Information($"Taskbar widget created: taskbar=0x{target.Handle.ToInt64():X}, primary={target.IsPrimary}");
            }
            catch (Exception ex)
            {
                try { widget?.Dispose(); } catch { }
                Log.Error(ex, $"Failed to create taskbar widget for taskbar=0x{target.Handle.ToInt64():X}");
            }
        }

        private static void OnWidgetDestroying(TaskBarWidget widget)
        {
            if (Widgets.TryGetValue(widget.TaskbarHandle, out var current) && ReferenceEquals(current, widget))
                Widgets.Remove(widget.TaskbarHandle);

            try { widget.Dispose(); }
            catch (Exception ex) { Log.Warning(ex, "Failed to dispose destroyed taskbar widget"); }
        }

        private static void SyncWidgetState()
        {
            foreach (var widget in Widgets.Values.ToArray())
                SyncWidgetState(widget);
        }

        private static void SyncWidgetState(TaskBarWidget widget)
        {
            if (!widget.IsAlive)
                return;

            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;

            widget.SetDisplayProviders(providers, coordinator.ActiveProvider);
            widget.SetVisible(true);

            bool needsFetch = false;
            foreach (var provider in providers)
            {
                var toApply = HydrateResult(coordinator, provider);
                if (toApply is { } result)
                {
                    widget.ApplyResult(result, force: true);
                    LogWidgetApply(result.Id, "sync");
                }

                // Hydrating from a placeholder/failed snapshot leaves the tile showing a non-value while
                // the flyout fetches its own data. Kick a fetch so the widget resolves on its own (#21).
                if (toApply is null or { Ok: false })
                    needsFetch = true;
            }

            if (needsFetch)
                _ = coordinator.TickAsync(force: true);
        }

        /// <summary>
        /// Best snapshot available to seed a tile: the last active publish, then either cache tier, then a
        /// Pending placeholder. Null only when the provider is unknown to the usage service.
        /// </summary>
        private static UsageResult? HydrateResult(UsageCoordinator coordinator, ProviderId provider)
        {
            if (coordinator.Service.TryGetCached(provider, out var cached))
                return cached;
            // A failed refresh is cached deliberately. Prefer that current failure over LastState,
            // which may still contain the previous successful snapshot and would resurrect stale quota
            // values when the widget is recreated (especially after cookie/auth failures).
            if (coordinator.LastState is { } last && last.Id == provider)
                return last;
            if (coordinator.Service.TryGetLastSuccessfulLiveResult(provider, out var lastSuccess))
                return lastSuccess;
            if (coordinator.Service.Get(provider) is { } usageProvider)
                return UsageResult.Pending(provider, usageProvider, "Loading...");
            return null;
        }

        private static void ToggleFlyout(TaskBarWidget widget)
        {
            if (!widget.IsAlive) return;
            FlyoutWindow? flyout = null;
            try
            {
                flyout = _flyout ?? new FlyoutWindow();
                _flyout = flyout;
                flyout.ToggleAbove(widget.Handle);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to toggle flyout");
                try { flyout?.Close(); } catch { }
                _flyout = null;
            }
        }

        private static void PrewarmFlyout()
        {
            _dispatcher?.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                FlyoutWindow? flyout = null;
                try
                {
                    flyout = _flyout ?? new FlyoutWindow();
                    _flyout = flyout;
                    flyout.Prewarm();
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to prewarm flyout");
                    try { flyout?.Close(); } catch { }
                    _flyout = null;
                }
            });
        }

        private static void OnStateChanged(UsageResult result)
            => _dispatcher?.TryEnqueue(DispatcherQueuePriority.High, () => ApplyStateChanged(result));

        private static void ApplyStateChanged(UsageResult result)
        {
            var coordinator = UsageCoordinator.Instance;
            var providers = coordinator.WidgetDisplayProviders;
            bool isDisplayed = providers.Contains(result.Id);

            // Reused buffer: this runs on every usage publish, so a fresh array per publish was pure waste.
            SnapshotWidgets();
            foreach (var widget in _widgetBuffer)
            {
                if (!widget.IsAlive)
                    continue;

                widget.SetDisplayProviders(providers, coordinator.ActiveProvider);
                widget.SetVisible(true);
                if (!isDisplayed)
                    continue;

                widget.ApplyResult(result);
                LogWidgetApply(result.Id, "state");
            }
        }

        /// <summary>
        /// Refills <see cref="_widgetBuffer"/> from the live dictionary. Callers iterate the buffer rather
        /// than the dictionary because a widget callback can remove an entry mid-loop; the buffer replaces
        /// the defensive copy that the hot paths used to allocate per pass. UI thread only, and the buffer
        /// stays valid only until the next call.
        /// </summary>
        private static void SnapshotWidgets()
        {
            _widgetBuffer.Clear();
            foreach (var widget in Widgets.Values)
                _widgetBuffer.Add(widget);
        }

        private static void LogWidgetApply(ProviderId provider, string source)
        {
            if (_lastLoggedWidgetApplyProvider == provider)
                return;

            _lastLoggedWidgetApplyProvider = provider;
            Log.Debug($"widget {source} applied provider={provider}");
        }

        private static void OnQuitting()
        {
            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
            _initialized = false;
            _widgetHealthTimer?.Stop();
            _widgetHealthTimer = null;
            try { _flyout?.Close(); } catch { }
            _flyout = null;
            _missingHandleTicks.Clear();
            foreach (var widget in Widgets.Values.ToArray())
            {
                try { widget.Dispose(); } catch { }
            }
            Widgets.Clear();
        }
    }
}
