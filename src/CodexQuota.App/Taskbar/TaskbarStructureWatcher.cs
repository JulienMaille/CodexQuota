using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using global::Interop.UIAutomationClient;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using CodexQuota.Interop;

namespace CodexQuota.Taskbar
{

    internal sealed class TaskbarStructureWatcher : IDisposable
    {
        private const string WidgetsButtonAutomationId = "WidgetsButton";

        private readonly IntPtr hwndTaskbar;
        private Timer? _timer;
        private IUIAutomation? _automation;
        private readonly object _sync = new();
        private bool _disposed;
        private int _pollActive;
        private RECT? _lastWidgetsButtonRect;
        private DateTime _lastWidgetsButtonRectAt = DateTime.MinValue;
        private static readonly TimeSpan WidgetsButtonRectMaxAge = TimeSpan.FromSeconds(30);
        private List<RECT>? _lastTaskButtonRects;
        private DateTime _lastTaskButtonRectsAt = DateTime.MinValue;
        private static readonly TimeSpan TaskButtonRectsMaxAge = TimeSpan.FromSeconds(30);
        private RECT _lastTaskbarRect;
        private bool _hasLastTaskbarRect;

        private bool widgetsButtonEnabled;
        private bool taskbarCentered;
        private bool taskbarHidden;

        public event EventHandler<TaskbarChangedEventArgs>? TaskbarChangedNotificationCompleted;

        public TaskbarStructureWatcher(IntPtr hwndTaskbar)
        {
            this.hwndTaskbar = hwndTaskbar;

            widgetsButtonEnabled = SystemInfos.IsTaskBarWidgetsEnabled();
            taskbarCentered = SystemInfos.IsTaskBarCentered();
            taskbarHidden = IsTaskbarHidden();
            if (User32.GetWindowRect(hwndTaskbar, out var initialRect))
            {
                _lastTaskbarRect = initialRect;
                _hasLastTaskbarRect = true;
            }

            _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        private void Poll()
        {
            if (Interlocked.Exchange(ref _pollActive, 1) != 0)
                return;
            try
            {
                bool isWidgets = SystemInfos.IsTaskBarWidgetsEnabled();
                bool isCentered = SystemInfos.IsTaskBarCentered();
                bool isHidden = IsTaskbarHidden();
                bool taskbarRectChanged = HasTaskbarRectChanged();

                TaskbarChangedEventArgs? args;
                lock (_sync)
                {
                    if (_disposed)
                        return;
                    TaskbarChangeReason? reason = DetectChange(
                        isWidgets, widgetsButtonEnabled,
                        isCentered, taskbarCentered,
                        isHidden, taskbarHidden,
                        taskbarRectChanged);
                    if (reason is null)
                        return;

                    switch (reason.Value)
                    {
                        case TaskbarChangeReason.WidgetsButton:
                            widgetsButtonEnabled = isWidgets;
                            break;
                        case TaskbarChangeReason.Alignment:
                            taskbarCentered = isCentered;
                            break;
                        case TaskbarChangeReason.Visibility:
                            taskbarHidden = isHidden;
                            break;
                    }

                    args = new TaskbarChangedEventArgs
                    {
                        Reason = reason.Value,
                        IsTaskbarHidden = taskbarHidden,
                        IsTaskbarCentered = taskbarCentered,
                        IsTaskbarWidgetsEnabled = widgetsButtonEnabled,
                    };
                }

                TaskbarChangedNotificationCompleted?.Invoke(this, args);
            }
            catch { /* best-effort */ }
            finally
            {
                Interlocked.Exchange(ref _pollActive, 0);
            }
        }

        private static TaskbarChangeReason? DetectChange(
            bool isWidgets, bool widgetsEnabled,
            bool isCentered, bool centered,
            bool isHidden, bool hidden,
            bool taskbarRectChanged)
        {
            if (isWidgets != widgetsEnabled)
                return TaskbarChangeReason.WidgetsButton;
            if (isCentered != centered)
                return TaskbarChangeReason.Alignment;
            if (isHidden != hidden)
                return TaskbarChangeReason.Visibility;
            if (taskbarRectChanged)
                return TaskbarChangeReason.Other;
            return null;
        }

        /// <summary>
        /// Screen bounds (physical px) of the taskbar Widgets button via UI Automation, or null when it
        /// can't be found (disabled, or the tree isn't ready). Runs off the UI thread — the UIA cross-
        /// process walk can block. Lets the default anchor sit clear of the Widgets pill (issue #10).
        /// </summary>
        public Task<RECT?> GetWidgetsButtonRectAsync() => Task.Run(TryGetWidgetsButtonRect);

        private RECT? TryGetWidgetsButtonRect()
        {
            if (hwndTaskbar == IntPtr.Zero || !SystemInfos.IsTaskBarWidgetsEnabled())
                return null;

            lock (_sync)
            {
                IUIAutomationElement? root = null;
                IUIAutomationCondition? condition = null;
                IUIAutomationElement? button = null;
                try
                {
                    if (!TryGetAutomationRootLocked(out root) || root is null || _automation is null)
                        return CachedWidgetsButtonRectLocked();

                    condition = _automation.CreatePropertyCondition(
                        UIA_PropertyIds.UIA_AutomationIdPropertyId, WidgetsButtonAutomationId);
                    button = root.FindFirst(TreeScope.TreeScope_Descendants, condition);
                    if (button is null)
                        return CachedWidgetsButtonRectLocked();

                    var r = button.CurrentBoundingRectangle;
                    if (r.right <= r.left || r.bottom <= r.top)
                        return CachedWidgetsButtonRectLocked();

                    var rect = new RECT { left = r.left, top = r.top, right = r.right, bottom = r.bottom };
                    // The Widgets flyout hosts a "WidgetsButton" element of its own; that one floats above the
                    // bar and its x-span covers the whole left half, which would wipe out every left-hand gap.
                    if (!IsOnTaskbarBand(rect))
                        return CachedWidgetsButtonRectLocked();

                    _lastWidgetsButtonRect = rect;
                    _lastWidgetsButtonRectAt = DateTime.UtcNow;
                    return rect;
                }
                catch (Exception ex)
                {
                    // Cross-process UIA reads fail intermittently (shell busy, tree rebuilding). Returning null
                    // here makes the caller anchor the widget at x=0 — right on top of the weather/Widgets pill.
                    // Fall back to the last known-good rect so a transient failure never causes the overlap (#17).
                    Diagnostics.Log.Debug($"widgets-button UIA lookup failed: {ex.Message}");
                    ReleaseAutomationLocked();
                    return CachedWidgetsButtonRectLocked();
                }
                finally
                {
                    ReleaseAll(button, condition, root);
                }
            }
        }

        private RECT? CachedWidgetsButtonRectLocked()
            => _lastWidgetsButtonRect is { } rect && IsFresh(_lastWidgetsButtonRectAt, WidgetsButtonRectMaxAge)
                ? rect
                : null;

        /// <summary>
        /// Screen bounds (physical px) of every Button in the taskbar UIA tree — the running-app icons plus
        /// system buttons (Start, Search, Widgets, tray). On Win11 the app icons are XAML, not classic
        /// MSTask* child windows, so an HWND scan can't see them; treating each button rect as an obstacle
        /// keeps the widget from ever landing on top of the app cluster (issue #17). Falls back to the last
        /// good set on transient UIA failure.
        /// </summary>
        public Task<List<RECT>?> GetTaskbarButtonRectsAsync() => Task.Run(TryGetTaskbarButtonRects);

        private List<RECT>? TryGetTaskbarButtonRects()
        {
            if (hwndTaskbar == IntPtr.Zero)
            {
                lock (_sync)
                {
                    return CachedTaskButtonRectsLocked();
                }
            }

            lock (_sync)
            {
                IUIAutomationElement? root = null;
                IUIAutomationCondition? condition = null;
                IUIAutomationElementArray? buttons = null;
                try
                {
                    if (!TryGetAutomationRootLocked(out root) || root is null || _automation is null)
                        return CachedTaskButtonRectsLocked();

                    condition = _automation.CreatePropertyCondition(
                        UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_ButtonControlTypeId);
                    buttons = root.FindAll(TreeScope.TreeScope_Descendants, condition);
                    if (buttons is not IUIAutomationElementArray array)
                        return CachedTaskButtonRectsLocked();

                    var rects = new List<RECT>(array.Length);
                    for (int i = 0; i < array.Length; i++)
                    {
                        IUIAutomationElement? element = null;
                        try
                        {
                            element = array.GetElement(i);
                            if (element is not IUIAutomationElement el)
                                continue;
                            var r = el.CurrentBoundingRectangle;
                            if (r.right <= r.left || r.bottom <= r.top)
                                continue;

                            var rect = new RECT { left = r.left, top = r.top, right = r.right, bottom = r.bottom };
                            // The taskbar UIA tree also carries buttons that are not ON the bar: the Widgets/weather
                            // flyout, the hidden-icons overflow popup, jump lists and tooltips all hang off the same
                            // root. Their bounds sit above the taskbar but span wide horizontal ranges, so treating
                            // them as obstacles erases whole zones and the widget can no longer be dragged into them
                            // (issue #21 follow-up: drag moved right but never left while the Widgets flyout tree was
                            // present). Keep only elements that actually live in the taskbar band.
                            if (!IsOnTaskbarBand(rect))
                                continue;

                            rects.Add(rect);
                        }
                        finally
                        {
                            ReleaseComObject(element);
                        }
                    }

                    if (rects.Count > 0)
                    {
                        _lastTaskButtonRects = rects;
                        _lastTaskButtonRectsAt = DateTime.UtcNow;
                        return new List<RECT>(rects);
                    }

                    // Don't cache an empty tree: a transient empty scan (shell rebuilding)
                    // must not poison the cache for 30s.
                    return CachedTaskButtonRectsLocked() ?? new List<RECT>(rects);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Debug($"taskbar-button UIA scan failed: {ex.Message}");
                    ReleaseAutomationLocked();
                    return CachedTaskButtonRectsLocked();
                }
                finally
                {
                    ReleaseAll(buttons, condition, root);
                }
            }
        }

        /// <summary>
        /// True when <paramref name="rect"/> vertically belongs to the taskbar itself rather than to a popup
        /// hosted in the same UIA tree. Requires most of the element's height to fall inside the bar, which
        /// keeps genuine bar items (a pixel or two of rounding outside) and drops flyout content above it.
        /// </summary>
        private bool IsOnTaskbarBand(RECT rect)
        {
            if (!User32.GetWindowRect(hwndTaskbar, out var bar) || bar.bottom <= bar.top)
                return false;

            return TaskBarWidget.IsInVerticalBand(rect, bar.top, bar.bottom);
        }

        private List<RECT>? CachedTaskButtonRectsLocked()
        {
            if (_lastTaskButtonRects is { } rects && IsFresh(_lastTaskButtonRectsAt, TaskButtonRectsMaxAge))
                return new List<RECT>(rects);
            return null;
        }

        private static bool IsFresh(DateTime sampledAt, TimeSpan maxAge)
            => DateTime.UtcNow - sampledAt < maxAge;

        private bool TryGetAutomationRootLocked(out IUIAutomationElement? root)
        {
            root = null;
            if (_disposed)
                return false;

            _automation ??= new CUIAutomation();
            root = _automation.ElementFromHandle(hwndTaskbar);
            return root is not null;
        }

        private static void ReleaseAll(params object?[] comObjects)
        {
            foreach (var comObject in comObjects)
                ReleaseComObject(comObject);
        }

        private bool HasTaskbarRectChanged()
        {
            if (!User32.GetWindowRect(hwndTaskbar, out var current))
                return false;
            if (!_hasLastTaskbarRect)
            {
                _lastTaskbarRect = current;
                _hasLastTaskbarRect = true;
                return true;
            }
            if (current.Equals(_lastTaskbarRect))
                return false;
            _lastTaskbarRect = current;
            return true;
        }

        private bool IsTaskbarHidden()
        {
            IntPtr p = User32.GetProp(hwndTaskbar, "IsAutoHideEnabled");
            if (p != (IntPtr)1) return false;
            if (!User32.GetWindowRect(hwndTaskbar, out var rect))
                return false;
            IntPtr monitor = User32.MonitorFromWindow(hwndTaskbar, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return false;
            var info = MONITORINFO.Create();
            if (!User32.GetMonitorInfo(monitor, ref info))
                return false;
            var m = info.rcMonitor;
            // Hidden autohide bar slides off the monitor edge it is docked to.
            return rect.bottom <= m.top || rect.top >= m.bottom || rect.right <= m.left || rect.left >= m.right;
        }

        public void Dispose()
        {
            Timer? timer;
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                timer = _timer;
                _timer = null;
            }

            try { timer?.Dispose(); } catch { }
            lock (_sync)
            {
                ReleaseAutomationLocked();
            }
        }

        private void ReleaseAutomationLocked()
        {
            if (_automation is not null)
            {
                try { Marshal.ReleaseComObject(_automation); } catch { }
                _automation = null;
            }
        }

        private static void ReleaseComObject(object? comObject)
        {
            if (comObject is null)
                return;
            try
            {
                if (Marshal.IsComObject(comObject))
                    Marshal.ReleaseComObject(comObject);
            }
            catch { }
        }
    }

    public sealed class TaskbarChangedEventArgs : EventArgs
    {
        public TaskbarChangeReason Reason { get; init; }
        public bool IsTaskbarHidden { get; init; }
        public bool IsTaskbarCentered { get; init; }
        public bool IsTaskbarWidgetsEnabled { get; init; }
    }

    public enum TaskbarChangeReason { None, Alignment, Visibility, WidgetsButton, TabletMode, Other }
}
