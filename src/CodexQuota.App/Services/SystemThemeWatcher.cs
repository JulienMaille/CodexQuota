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

    private static DispatcherQueue? _queue;
    private static RegistryKey? _key;
    private static AutoResetEvent? _changeEvent;
    private static RegisteredWaitHandle? _wait;
    private static bool? _last;

    /// <summary>Raised on the UI thread when the taskbar theme changes.</summary>
    public static event Action? Changed;

    /// <summary>Arms the OS notification. A no-op once running; must be called from the UI thread.</summary>
    public static void Start()
    {
        if (_queue is not null)
            return;

        _queue = DispatcherQueue.GetForCurrentThread();
        if (_queue is null)
            return;

        _last = Interop.SystemInfos.IsSystemLightThemeUsed();
        _key = Registry.CurrentUser.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", writable: false);
        if (_key is null)
            return;

        Arm();
    }

    /// <summary>(Re)arms the kernel notification, then double-checks the value on the UI thread — a flip
    /// that slipped between a signal and this re-arm still gets picked up immediately.</summary>
    private static void Arm()
    {
        try
        {
            _changeEvent ??= new AutoResetEvent(false);
            _wait?.Unregister(null);

            int error = RegNotifyChangeKeyValue(_key!.Handle, 0, RegNotifyChangeLastSet,
                _changeEvent.SafeWaitHandle.DangerousGetHandle(), asynchronous: true);
            if (error != 0)
                return;

            _wait = ThreadPool.RegisterWaitForSingleObject(
                _changeEvent, OnRegistryChanged, null, Timeout.Infinite, executeOnlyOnce: true);
        }
        catch (Exception)
        {
            // Best-effort: a failed arm leaves the tile with its last-correct theme; the render
            // signature also includes the theme, so the next usage poll self-heals.
            return;
        }

        // Re-check right away, covering a flip that happened between the previous signal and re-arm.
        bool? current = Interop.SystemInfos.IsSystemLightThemeUsed();
        if (current is { } c && c != _last)
            _queue?.TryEnqueue(HandleChange);
    }

    private static void OnRegistryChanged(object? state, bool timedOut)
    {
        // Re-arm first so a change during dispatch is not missed.
        Arm();
        _queue?.TryEnqueue(HandleChange);
    }

    private static void HandleChange()
    {
        bool? current = Interop.SystemInfos.IsSystemLightThemeUsed();
        if (current is null || current == _last)
            return;

        _last = current;
        Changed?.Invoke();
    }
}