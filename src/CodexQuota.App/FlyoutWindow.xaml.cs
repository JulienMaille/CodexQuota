using System;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.Graphics;
using CodexQuota.Controls;
using CodexQuota.Interop;
using CodexQuota.Usage;

namespace CodexQuota
{
    /// <summary>
    /// A borderless, always-on-top acrylic flyout shown just above the taskbar widget — a compact
    /// Codex usage detail panel. Hides itself when it loses focus.
    /// </summary>
    public sealed partial class FlyoutWindow : Window
    {
        private IntPtr _widgetHandle;
        private bool _shown;
        private bool _prewarmed;
        private bool _sizeHooksRegistered;
        private bool _applyingBounds;
        private DispatcherQueueTimer? _boundsUpdateTimer;
        private RectInt32? _lastAppliedBounds;
        private double _lastObservedScale = -1;
        private static readonly TimeSpan BoundsCoalesceDelay = TimeSpan.FromMilliseconds(80);
        private static readonly TimeSpan FlyoutOpenCommitDelay = TimeSpan.FromMilliseconds(300);
        private DateTime _shownAtUtc;

        // Appearance-section reveal animation.
        private Storyboard? _appearanceStoryboard;

        // Flyout entrance: the window rises into place from just above the taskbar edge, moved by a
        // short eased sequence. WinUI 3 windows are DirectComposition-composited, so the OS's native
        // AnimateWindow slide cannot move or fade them.
        private DispatcherQueueTimer? _entranceTimer;
        private RectInt32 _entranceTarget;
        private double _entranceStartY;
        private double _entranceDeltaY;
        private int _entranceStep;
        private const int EntranceSteps = 10;
        private static readonly TimeSpan EntranceTick = TimeSpan.FromMilliseconds(12);
        private const double EntranceRiseLogicalPx = 20;

        public bool IsShown => _shown;

        public FlyoutWindow()
        {
            InitializeComponent();
            SystemBackdrop = new DesktopAcrylicBackdrop();
            ThemeService.Register(Root);
            Root.Loaded += (_, _) => RegisterWindowSizeHooks();
            _boundsUpdateTimer = DispatcherQueue.CreateTimer();
            _boundsUpdateTimer.Interval = BoundsCoalesceDelay;
            _boundsUpdateTimer.Tick += (_, _) =>
            {
                _boundsUpdateTimer.Stop();
                ApplyFlyoutBounds();
            };

            _entranceTimer = DispatcherQueue.CreateTimer();
            _entranceTimer.Interval = EntranceTick;
            _entranceTimer.Tick += (_, _) => StepFlyoutEntrance();

            var presenter = OverlappedPresenter.CreateForContextMenu();
            presenter.IsAlwaysOnTop = true;
            var appWindow = GetAppWindow();
            // The flyout is transient taskbar UI, not an application window. Keep it out of the
            // taskbar/Alt+Tab representation.
            appWindow.IsShownInSwitchers = false;
            appWindow.SetPresenter(presenter);

            Activated += OnActivated;
            Closed += OnClosed;

            // Seed the toggles from persisted settings without firing the change handlers (which write
            // the same value back and re-render the tile needlessly during construction).
            _initializingAppearance = true;
            ShowIconCheck.IsChecked = WidgetAppearanceSettings.ShowIcon;
            ShowProgressBarCheck.IsChecked = WidgetAppearanceSettings.ShowProgressBar;
            ColorCodeTextCheck.IsChecked = WidgetAppearanceSettings.ColorCodeText;
            _initializingAppearance = false;

            // Threshold boxes: setting Value fires ValueChanged, but the handler's equality check
            // treats an unchanged value as a no-op, so seeding writes nothing back.
            WarnUpperBox.Value = WidgetAppearanceSettings.WarningUpperPercent;
            WarnLowerBox.Value = WidgetAppearanceSettings.WarningLowerPercent;

            // Keep the panel in sync with every coordinator publish while open.
            UsageCoordinator.Instance.StateChanged += OnStateChanged;
            UsageCoordinator.Instance.ProfileChanged += OnProfileChanged;

            // The panel's header hosts the refresh/close buttons now; route them to the flyout's
            // actions (fetch + hide).
            UsagePanel.RefreshRequested += () => _ = UsageCoordinator.Instance.FetchAndPublishAsync(force: true);
            UsagePanel.SettingsRequested += ToggleAppearanceSection;
            UsagePanel.CloseRequested += Hide;

            // The window hugs the panel's content height: grow/shrink when content changes (profile
            // section arrives or disappears, meter rows swap).
            UsagePanel.SizeChanged += (_, _) => ScheduleFlyoutBoundsUpdate();
        }

        private bool _initializingAppearance;

        private bool _appearanceSectionVisible;

private void ToggleAppearanceSection()
        {
            _appearanceSectionVisible = !_appearanceSectionVisible;

            _appearanceStoryboard?.Stop();
            var storyboard = new Storyboard();

            if (_appearanceSectionVisible)
            {
                // Reveal: the section fades up over the content at the bottom of the window (overlay —
                // it never contributes to the flyout height).
                AppearanceSection.Visibility = Visibility.Visible;
                AppearanceSection.Opacity = 0;
                AppearanceSectionTransform.Y = 12;

                var fade = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(fade, AppearanceSection);
                Storyboard.SetTargetProperty(fade, "Opacity");

                var slide = new DoubleAnimation
                {
                    From = 12,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(180)),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut },
                };
                Storyboard.SetTarget(slide, AppearanceSectionTransform);
                Storyboard.SetTargetProperty(slide, "Y");

                storyboard.Children.Add(fade);
                storyboard.Children.Add(slide);
                storyboard.Completed += (_, _) =>
                {
                    AppearanceSection.Opacity = 1;
                    AppearanceSectionTransform.Y = 0;
                };
            }
            else
            {
                // Collapse: fade the section out, then remove it from layout.
                var fade = new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(140)),
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseIn },
                };
                Storyboard.SetTarget(fade, AppearanceSection);
                Storyboard.SetTargetProperty(fade, "Opacity");

                storyboard.Children.Add(fade);
                storyboard.Completed += (_, _) =>
                {
                    AppearanceSection.Visibility = Visibility.Collapsed;
                    AppearanceSection.Opacity = 1;
                    AppearanceSectionTransform.Y = 0;
                };
            }

            _appearanceStoryboard = storyboard;
            storyboard.Begin();
        }

        private void AppearanceCheck_Checked(object sender, RoutedEventArgs e)
        {
            if (_initializingAppearance)
                return;

            if (ReferenceEquals(sender, ShowIconCheck))
                WidgetAppearanceSettings.ShowIcon = true;
            else if (ReferenceEquals(sender, ShowProgressBarCheck))
                WidgetAppearanceSettings.ShowProgressBar = true;
            else if (ReferenceEquals(sender, ColorCodeTextCheck))
                WidgetAppearanceSettings.ColorCodeText = true;

            RefreshPanelForAppearance();
        }

        private void AppearanceCheck_Unchecked(object sender, RoutedEventArgs e)
        {
            if (_initializingAppearance)
                return;

            if (ReferenceEquals(sender, ShowIconCheck))
                WidgetAppearanceSettings.ShowIcon = false;
            else if (ReferenceEquals(sender, ShowProgressBarCheck))
                WidgetAppearanceSettings.ShowProgressBar = false;
            else if (ReferenceEquals(sender, ColorCodeTextCheck))
                WidgetAppearanceSettings.ColorCodeText = false;

            RefreshPanelForAppearance();
        }

        private void WarnThresholdBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            if (double.IsNaN(args.NewValue))
                return;

            int value = (int)args.NewValue;
            if (ReferenceEquals(sender, WarnUpperBox))
            {
                if (value == WidgetAppearanceSettings.WarningUpperPercent)
                    return;
                WidgetAppearanceSettings.WarningUpperPercent = value;
            }
            else
            {
                if (value == WidgetAppearanceSettings.WarningLowerPercent)
                    return;
                WidgetAppearanceSettings.WarningLowerPercent = value;
            }

            // The setters clamp independently (upper 1..99, lower 0..upper-1), so the typed value
            // can differ from what is applied — e.g. typing 80 into the lower box stores 80 but
            // reads back as upper-1, and raising the upper box later shifts the effective lower.
            // Re-seed both boxes from the getters so the display always shows the value in effect;
            // the equality check above makes the resulting no-op/value-write-back converge.
            WarnUpperBox.Value = WidgetAppearanceSettings.WarningUpperPercent;
            WarnLowerBox.Value = WidgetAppearanceSettings.WarningLowerPercent;

            RefreshPanelForAppearance();
        }

        // The panel renders the same remaining percent with the same urgency colors, so a toggle while
        // the flyout is open should re-render it in place rather than wait for the next publish.
        private void RefreshPanelForAppearance()
        {
            if (!_shown || UsageCoordinator.Instance.LastState is not { } state)
                return;

            DispatcherQueue.TryEnqueue(() => UsagePanel.SetResult(state));
        }

        private void OnClosed(object sender, WindowEventArgs args)
        {
            _shown = false;
            UsageCoordinator.Instance.NotifyFlyoutClosed();
            Closed -= OnClosed;
            Activated -= OnActivated;
            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
            UsageCoordinator.Instance.ProfileChanged -= OnProfileChanged;
            _appearanceStoryboard?.Stop();
            _boundsUpdateTimer?.Stop();
            _entranceTimer?.Stop();
        }

        private void OnStateChanged(UsageResult result)
        {
            if (!_shown)
                return;

            DispatcherQueue.TryEnqueue(() => UsagePanel.SetResult(result));
        }

        private void OnProfileChanged(CodexProfileSnapshot profile)
        {
            if (!_shown)
                return;

            DispatcherQueue.TryEnqueue(() => UsagePanel.SetProfile(profile));
        }

        private void OnActivated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated
                && User32.GetForegroundWindow() != _widgetHandle
                && !IsPointerOverWidget())
            {
                Hide();
            }
        }

        public void ToggleAbove(IntPtr widgetHandle)
        {
            if (_shown)
            {
                // A click inside the opening window is usually the tail end of a fast double-click:
                // toggling shut then would blink the flyout out of existence ("fast clicks not
                // registered"). Only toggle closed once the open has committed.
                if (DateTime.UtcNow - _shownAtUtc < FlyoutOpenCommitDelay)
                    return;
                Hide();
                return;
            }

            ShowAbove(widgetHandle);
        }

        /// <summary>
        /// Compose the first XAML frame and spin up the acrylic backdrop off-screen once, so the first
        /// real open doesn't flash a black slab while WinUI warms up composition.
        /// </summary>
        public void Prewarm()
        {
            if (_prewarmed)
                return;
            _prewarmed = true;

            var appWindow = GetAppWindow();
            appWindow.Move(new PointInt32(-32000, -32000));
            appWindow.Show(false);
            // Paint the content now (off-screen), so the first on-screen composite shows real data
            // instead of a blank first frame while layout + textures upload.
            UsagePanel.SetResult(UsageCoordinator.Instance.LastState);
            UsagePanel.SetProfile(UsageCoordinator.Instance.LastProfile);
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => { if (!_shown) appWindow.Hide(); });
        }

        public void ShowAbove(IntPtr widgetHandle)
        {
            _widgetHandle = widgetHandle;
            _shown = true;
            _shownAtUtc = DateTime.UtcNow;
            UsageCoordinator.Instance.NotifyFlyoutOpen();
            var target = ApplyFlyoutBounds();
            if (target is { } start)
            {
                // Start slightly lower than the resting position so the show reads as a slide up. The
                // window is fully opaque from the first frame, so there is no perceived opening delay.
                int rise = WindowDpi.ToPhysical(EntranceRiseLogicalPx, Root.XamlRoot?.RasterizationScale ?? GetWindowScale());
                GetAppWindow().MoveAndResize(new RectInt32(start.X, start.Y + rise, start.Width, start.Height));
            }
            GetAppWindow().Show();
            ActivateFlyout();
            if (target is { } animate)
                StartFlyoutEntrance(animate);
            ScheduleFlyoutBoundsUpdate();
            // Re-color the logo now that the window is composed for real: the off-screen prewarm can leave
            // ActualTheme stale (Light on a dark system), so Loaded's paint is not trustworthy.
            UsagePanel.ApplyLogoBrush();

            // Seed the panel from whatever the coordinator last published, then let the fetch
            // round-trip refresh it.
            UsagePanel.SetResult(UsageCoordinator.Instance.LastState);
            UsagePanel.SetProfile(UsageCoordinator.Instance.LastProfile);

            // If the last published state is pending or stale (first open before the widget's periodic
            // fetch completed, or a session-restored snapshot no live fetch confirmed), kick one now so
            // the panel fills in fast instead of sitting on the "No usage data yet." placeholder for the
            // next poll interval.
            if (UsageCoordinator.Instance.LastState is not { } last || last.IsPending || last.IsStale)
            {
                _ = UsageCoordinator.Instance.FetchAndPublishAsync(force: true);
            }

            // Same story for the profile: seed from the last fetch, and re-fetch so the heatmap is not
            // stale on first open (the profile only updates on the poll tick).
            if (UsageCoordinator.Instance.LastProfile is null)
            {
                _ = UsageCoordinator.Instance.FetchProfileAsync();
            }
        }

        // Flyout entrance: the window rises from just above the taskbar edge into its resting spot,
        // ~120 ms cubic ease-out. The window is fully opaque from the first frame, so there is no
        // perceived opening delay.
        private void StartFlyoutEntrance(RectInt32 target)
        {
            _entranceTimer?.Stop();
            _entranceTarget = target;
            _entranceStartY = target.Y + WindowDpi.ToPhysical(EntranceRiseLogicalPx, Root.XamlRoot?.RasterizationScale ?? GetWindowScale());
            _entranceDeltaY = target.Y - _entranceStartY;
            _entranceStep = 0;
            _entranceTimer?.Start();
        }

        private void StepFlyoutEntrance()
        {
            if (_entranceTimer is null)
                return;

            _entranceStep++;
            double t = Math.Min(1.0, _entranceStep / (double)EntranceSteps);
            double eased = 1 - Math.Pow(1 - t, 3);
            int y = (int)Math.Round(_entranceStartY + _entranceDeltaY * eased);
            var appWindow = GetAppWindow();
            appWindow.MoveAndResize(new RectInt32(_entranceTarget.X, y, _entranceTarget.Width, _entranceTarget.Height));

            if (_entranceStep >= EntranceSteps)
            {
                _entranceTimer.Stop();
                appWindow.MoveAndResize(_entranceTarget);
                // Content may have grown while the entrance ran (profile section arriving is the usual
                // case on first open): settle to the measured height now that the animation is done.
                ScheduleFlyoutBoundsUpdate();
            }
        }

        private void ActivateFlyout()
        {
            Activate();

            // WinUI's Activate() is not always enough to transfer foreground ownership from Explorer,
            // leaving DesktopAcrylic in its inactive/transparent state until the user clicks the flyout
            // again. Make the transfer explicit.
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero)
                return;

            var foreground = User32.GetForegroundWindow();
            var foregroundThread = foreground == IntPtr.Zero
                ? 0u
                : User32.GetWindowThreadProcessId(foreground, out _);
            var currentThread = User32.GetCurrentThreadId();
            bool attached = foregroundThread != 0
                && foregroundThread != currentThread
                && User32.AttachThreadInput(foregroundThread, currentThread, true);
            try
            {
                if (User32.GetForegroundWindow() != hwnd)
                    User32.SetForegroundWindow(hwnd);
                User32.SetActiveWindow(hwnd);
                User32.SetFocus(hwnd);
            }
            finally
            {
                if (attached)
                    User32.AttachThreadInput(foregroundThread, currentThread, false);
            }
        }

        private void RegisterWindowSizeHooks()
        {
            if (_sizeHooksRegistered)
                return;

            _sizeHooksRegistered = true;
            if (Root.XamlRoot is { } xamlRoot)
            {
                _lastObservedScale = xamlRoot.RasterizationScale;
                xamlRoot.Changed += (_, _) =>
                {
                    double scale = xamlRoot.RasterizationScale;
                    if (Math.Abs(scale - _lastObservedScale) <= 0.001)
                        return;

                    _lastObservedScale = scale;
                    ScheduleFlyoutBoundsUpdate();
                };
            }
        }

        private void ScheduleFlyoutBoundsUpdate()
        {
            if (!_shown)
                return;

            if (_boundsUpdateTimer is null)
            {
                DispatcherQueue.TryEnqueue(() => ApplyFlyoutBounds());
                return;
            }

            _boundsUpdateTimer.Interval = BoundsCoalesceDelay;
            _boundsUpdateTimer.Stop();
            _boundsUpdateTimer.Start();
        }

        private RectInt32? ApplyFlyoutBounds()
        {
            if (!_shown || _widgetHandle == IntPtr.Zero || _applyingBounds)
                return null;

            // The entrance animates position+size per tick from a fixed target; resizing mid-animation
            // would fight it. Drop the update now; the animation's final step re-schedules it.
            if (_entranceTimer is { IsRunning: true })
                return null;

            _applyingBounds = true;
            try
            {
                var scale = Root.XamlRoot?.RasterizationScale ?? GetWindowScale();
                int w = WindowDpi.ToPhysical(FlyoutLayout.BaseLogicalWidth, scale);
                // Size the window to the panel's measured content height (the ScrollViewer measures
                // the panel unconstrained, so ActualHeight is the full content: usage rows + profile
                // heatmap + detail line) instead of a fixed skeleton. ComputeLogicalHeight clamps to
                // [MinLogicalContentHeight, MaxLogicalContentHeight].
                double contentLogicalHeight = UsagePanel.ActualHeight > 0
                    ? UsagePanel.ActualHeight
                    : FlyoutLayout.MinLogicalContentHeight;
                int h = WindowDpi.ToPhysical(FlyoutLayout.ComputeLogicalHeight(contentLogicalHeight), scale);

                if (!User32.GetWindowRect(_widgetHandle, out RECT wr))
                    return null;

                int gap = WindowDpi.ToPhysical(8, scale);
                int maxHeight = Math.Max(WindowDpi.ToPhysical(320, scale), wr.top - gap);
                h = Math.Min(h, maxHeight);

                // Right-align the flyout to the widget, floating just above the taskbar.
                int x = wr.right - w;
                int y = wr.top - h - gap;

                // Confine the flyout to the monitor that hosts the widget so it never straddles a
                // monitor boundary on multi-display setups (issue #10).
                if (TryGetWorkArea(_widgetHandle, out RECT work))
                {
                    w = Math.Min(w, work.right - work.left);
                    h = Math.Min(h, work.bottom - work.top);
                    x = Math.Clamp(wr.right - w, work.left, work.right - w);
                    y = Math.Clamp(y, work.top, work.bottom - h);
                }
                else
                {
                    if (y < 0) y = 0;
                    if (x < 0) x = 0;
                }

                var bounds = new RectInt32(x, y, w, h);
                if (_lastAppliedBounds is { } last
                    && last.X == bounds.X
                    && last.Y == bounds.Y
                    && last.Width == bounds.Width
                    && last.Height == bounds.Height)
                    return null;

                _lastAppliedBounds = bounds;
                GetAppWindow().MoveAndResize(bounds);
                return bounds;
            }
            finally
            {
                _applyingBounds = false;
            }
        }

        private double GetWindowScale()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var dpi = User32.GetDpiForWindow(hwnd);
            return dpi > 0 ? dpi / 96d : 1d;
        }

        // Work area (taskbar-excluded) of the monitor hosting the given window, in physical pixels.
        private static bool TryGetWorkArea(IntPtr hwnd, out RECT work)
        {
            work = default;
            if (hwnd == IntPtr.Zero)
                return false;

            var monitor = User32.MonitorFromWindow(hwnd, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;

            var info = MONITORINFO.Create();
            if (!User32.GetMonitorInfo(monitor, ref info))
                return false;

            work = info.rcWork;
            return work.right > work.left && work.bottom > work.top;
        }

        public void Hide()
        {
            if (!_shown) return;
            _shown = false;
            _lastAppliedBounds = null;
            _entranceTimer?.Stop();
            UsageCoordinator.Instance.NotifyFlyoutClosed();
            GetAppWindow().Hide();
        }

        private AppWindow GetAppWindow()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        }

        private bool IsPointerOverWidget()
        {
            if (_widgetHandle == IntPtr.Zero)
                return false;
            if (!User32.GetCursorPos(out var point))
                return false;
            if (!User32.GetWindowRect(_widgetHandle, out var rect))
                return false;

            return point.x >= rect.left
                && point.x <= rect.right
                && point.y >= rect.top
                && point.y <= rect.bottom;
        }
    }
}