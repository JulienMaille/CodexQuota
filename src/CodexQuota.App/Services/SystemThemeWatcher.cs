using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace CodexQuota;

/// <summary>
/// Fires <see cref="Changed"/> when the taskbar theme — HKCU …\Themes\Personalize\SystemUsesLightTheme —
/// flips. This is event-driven: <c>RegNotifyChangeKeyValue</c> arms the OS-level change notification on
/// that key the moment the value is written; the signal is awaited on a thread-pool wait handle, not a
/// timer. (WinUI's ActualThemeChanged would track the *apps* theme, AppsUseLightTheme, which Windows
/// lets diverge from the taskbar theme, so the tile watches the key it actually reads.)
/// </summary>
public static class SystemThemeWatcher
{
    private const uint RegNotifyChangeLastSet = 0x4; // REG_NOTIFY_CHANGE_LAST_SET: value written

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle hKey, int watchSubtree, uint notifyFilter, IntPtr hEvent, bool asynchronous);

    private static readonly object _sync = new();
    private static DispatcherQueue? _queue;
    private static RegistryKey? _key;
    private static AutoResetEvent? _changeEvent;
    private static RegisteredWaitHandle? _wait;
    private static bool? _last;
    private static bool _stopped;
    private static int _armFailures;

    /// <summary>Raised on the UI thread when the taskbar theme changes.</summary>
    public static event Action? Changed;

    /// <summary>Arms the OS notification. A no-op once running; must be called from the UI thread.</summary>
    public static void Start()
    {
        lock (_sync)
        {
            if (_queue is not null || _stopped)
                return;
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
            return;

        lock (_sync)
        {
            if (_queue is not null || _stopped)
                return;
            _queue = queue;
            _last = Interop.SystemInfos.IsSystemLightThemeUsed();
            _key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
            if (_key is null)
                return;
        }

        Arm();
    }

    public static void Stop()
    {
        RegisteredWaitHandle? wait;
        RegistryKey? key;
        AutoResetEvent? changeEvent;
        lock (_sync)
        {
            _stopped = true;
            wait = _wait;
            _wait = null;
            key = _key;
            _key = null;
            changeEvent = _changeEvent;
            _changeEvent = null;
            _queue = null;
        }

        // Never Unregister(null) on the callback thread: it blocks until the callback
        // returns, which deadlocks when called from inside OnRegistryChanged.
        // Defer it to the pool so the callback thread never waits on itself.
        DeferUnregister(wait);
        try { key?.Dispose(); } catch { }
        try { changeEvent?.Dispose(); } catch { }
    }

    /// <summary>Unregisters a wait off the calling thread. The blocking Unregister overload
    /// waits for outstanding callbacks, so doing it inline on the callback thread self-deadlocks.</summary>
    private static void DeferUnregister(RegisteredWaitHandle? wait)
    {
        if (wait is null)
            return;
        ThreadPool.QueueUserWorkItem(static state =>
        {
            try { ((RegisteredWaitHandle)state!).Unregister(null); } catch { }
        }, wait);
    }

    /// <summary>(Re)arms the kernel notification, then double-checks the value on the UI thread — a flip
    /// that slipped between a signal and this re-arm still gets picked up immediately.</summary>
    private static void Arm()
    {
        DispatcherQueue? queue;
        bool? last;
        try
        {
            RegistryKey? key;
            AutoResetEvent? changeEvent;
            RegisteredWaitHandle? oldWait;
            lock (_sync)
            {
                if (_stopped || _key is null || _queue is null)
                    return;
                key = _key;
                queue = _queue;
                last = _last;
                _changeEvent ??= new AutoResetEvent(false);
                changeEvent = _changeEvent;
                oldWait = _wait;
                _wait = null;
            }

            // Never Unregister(null) on the callback thread: it waits for the in-flight
            // callback to return, which is this thread — a self-deadlock. Defer it to the
            // pool so the callback thread never waits on itself.
            DeferUnregister(oldWait);

            int error = RegNotifyChangeKeyValue(key.Handle, 0, RegNotifyChangeLastSet,
                changeEvent.SafeWaitHandle.DangerousGetHandle(), asynchronous: true);
            if (error != 0)
                throw new System.ComponentModel.Win32Exception(error);

            var wait = ThreadPool.RegisterWaitForSingleObject(
                changeEvent, OnRegistryChanged, null, Timeout.Infinite, executeOnlyOnce: true);
            lock (_sync)
            {
                if (_stopped)
                {
                    DeferUnregister(wait);
                    return;
                }
                // A concurrent Stop() wins: drop the just-registered wait rather than leak it.
                if (!ReferenceEquals(_key, key))
                {
                    DeferUnregister(wait);
                    return;
                }
                _wait = wait;
                _armFailures = 0;
            }
        }
        catch (Exception ex)
        {
            // Best-effort: a failed arm leaves the tile with its last-correct theme; the render
            // signature also includes the theme, so the next usage poll self-heals.
            // Log and retry with backoff instead of silently disarming forever.
            Diagnostics.Log.Warning(ex, "System theme watcher re-arm failed; retrying");
            int failures;
            lock (_sync) { failures = ++_armFailures; }
            int delayMs = Math.Min(30_000, 1000 * (1 << Math.Min(failures, 5)));
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(delayMs);
                Arm();
            });
            return;
        }

        // Re-check right away, covering a flip that happened between the previous signal and re-arm.
        bool? current = null;
        try { current = Interop.SystemInfos.IsSystemLightThemeUsed(); } catch { }
        if (current is { } c && c != last)
            queue?.TryEnqueue(HandleChange);
    }

    private static void OnRegistryChanged(object? state, bool timedOut)
    {
        // Re-arm first so a change during dispatch is not missed.
        Arm();
        DispatcherQueue? queue;
        lock (_sync) { queue = _queue; }
        queue?.TryEnqueue(HandleChange);
    }

    private static void HandleChange()
    {
        bool? current = null;
        try { current = Interop.SystemInfos.IsSystemLightThemeUsed(); } catch { }
        Action? changed;
        lock (_sync)
        {
            if (current is null || current == _last)
                return;

            _last = current;
            changed = Changed;
        }

        changed?.Invoke();
    }
}