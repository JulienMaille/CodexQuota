using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CodexQuota.Interop;

namespace CodexQuota.Taskbar
{
    internal readonly record struct TaskbarWindowTarget(IntPtr Handle, bool IsPrimary, string DisplayKey)
    {
        internal const string PrimaryClassName = "Shell_TrayWnd";
        internal const string SecondaryClassName = "Shell_SecondaryTrayWnd";

        public static bool TryFindAll(out IReadOnlyList<TaskbarWindowTarget> result)
        {
            var targets = new List<TaskbarWindowTarget>();
            var gc = GCHandle.Alloc(targets);
            bool success;
            try
            {
                success = User32.EnumWindows(EnumTaskbarWindow, GCHandle.ToIntPtr(gc));
            }
            finally
            {
                gc.Free();
            }

            // Drop windows destroyed between enumeration and sort so dead handles
            // never reach the manager. Filter before sorting so dead entries
            // never participate in ordering.
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                if (!User32.IsWindow(targets[i].Handle))
                    targets.RemoveAt(i);
            }

            // Precompute bounds once per sort: one GetWindowRect per target instead
            // of one per comparison.
            var boundsCache = new Dictionary<IntPtr, RECT>(targets.Count);
            foreach (var t in targets)
                boundsCache[t.Handle] = GetBounds(t.Handle);
            targets.Sort((left, right) => CompareTargetsCached(left, right, boundsCache));
            result = targets;
            return success;
        }

        internal static bool IsTaskbarClassName(string className, out bool isPrimary)
        {
            isPrimary = string.Equals(className, PrimaryClassName, StringComparison.Ordinal);
            return isPrimary || string.Equals(className, SecondaryClassName, StringComparison.Ordinal);
        }

        internal static string BuildDisplayKey(string? displayId, RECT bounds)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(displayId))
            {
                foreach (char c in displayId)
                {
                    if (char.IsLetterOrDigit(c) || c is '-' or '_')
                        builder.Append(c);
                }
            }

            return builder.Length > 0
                ? builder.ToString()
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $"{bounds.left}_{bounds.top}_{bounds.right}_{bounds.bottom}");
        }

        private static bool EnumTaskbarWindow(IntPtr hwnd, IntPtr lParam)
        {
            var builder = new StringBuilder(64);
            if (User32.GetClassName(hwnd, builder, builder.Capacity) <= 0)
                return true;
            if (IsTaskbarClassName(builder.ToString(), out bool isPrimary)
                && User32.IsWindow(hwnd)
                && User32.IsWindowVisible(hwnd)
                && GCHandle.FromIntPtr(lParam).Target is List<TaskbarWindowTarget> targets)
            {
                var bounds = GetBounds(hwnd);
                targets.Add(new TaskbarWindowTarget(
                    hwnd,
                    isPrimary,
                    BuildDisplayKey(TryGetDisplayId(hwnd), bounds)));
            }

            return true;
        }

        private static int CompareTargets(TaskbarWindowTarget left, TaskbarWindowTarget right)
        {
            if (left.IsPrimary != right.IsPrimary)
                return left.IsPrimary ? -1 : 1;

            // Re-validate liveness: EnumWindows and Sort are separated in time; a taskbar
            // window destroyed in between must not win the sort or poison ordering.
            bool leftAlive = User32.IsWindow(left.Handle);
            bool rightAlive = User32.IsWindow(right.Handle);
            if (leftAlive != rightAlive)
                return leftAlive ? -1 : 1;
            if (!leftAlive)
                return 0;

            // Bounds precomputed once per sort (single GetWindowRect per target).
            var leftBounds = GetBounds(left.Handle);
            var rightBounds = GetBounds(right.Handle);
            int byTop = leftBounds.top.CompareTo(rightBounds.top);
            return byTop != 0 ? byTop : leftBounds.left.CompareTo(rightBounds.left);
        }

        private static int CompareTargetsCached(
            TaskbarWindowTarget left,
            TaskbarWindowTarget right,
            Dictionary<IntPtr, RECT> boundsCache)
        {
            if (left.IsPrimary != right.IsPrimary)
                return left.IsPrimary ? -1 : 1;

            bool leftAlive = User32.IsWindow(left.Handle);
            bool rightAlive = User32.IsWindow(right.Handle);
            if (leftAlive != rightAlive)
                return leftAlive ? -1 : 1;
            if (!leftAlive)
                return 0;

            if (!boundsCache.TryGetValue(left.Handle, out var leftBounds))
                leftBounds = GetBounds(left.Handle);
            if (!boundsCache.TryGetValue(right.Handle, out var rightBounds))
                rightBounds = GetBounds(right.Handle);
            int byTop = leftBounds.top.CompareTo(rightBounds.top);
            return byTop != 0 ? byTop : leftBounds.left.CompareTo(rightBounds.left);
        }

        private static string TryGetDisplayId(IntPtr taskbarHandle)
        {
            var monitor = User32.MonitorFromWindow(taskbarHandle, MonitorFromFlags.MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                return string.Empty;

            var info = MONITORINFOEX.Create();
            return User32.GetMonitorInfo(monitor, ref info) ? info.szDevice : string.Empty;
        }

        private static RECT GetBounds(IntPtr hwnd)
            => User32.GetWindowRect(hwnd, out var bounds) ? bounds : default;
    }
}
