using System;
using Microsoft.Win32;

namespace CodexQuota;

/// <summary>Persistent assumptions used by the pace projection.</summary>
public static class PaceSettings
{
    private const string KeyPath = @"Software\CodexQuota";
    private const string WorkdayHoursValueName = "PaceWorkdayHours";

    public const int DefaultWorkdayHours = 8;

    /// <summary>Raised when a pace assumption changes while the flyout is open.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Maximum number of working hours counted in each 24-hour quota day. Remaining hours in that
    /// quota day are treated as idle rather than extending the observed workday.
    /// </summary>
    public static int WorkdayHours
    {
        get => Math.Clamp(ReadInt(WorkdayHoursValueName, DefaultWorkdayHours), 1, 24);
        set
        {
            WriteInt(WorkdayHoursValueName, Math.Clamp(value, 1, 24));
            Changed?.Invoke();
        }
    }

    private static int ReadInt(string name, int defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(name) is int value ? value : defaultValue;
        }
        catch
        {
            return defaultValue;
        }
    }

    private static void WriteInt(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key?.SetValue(name, value, RegistryValueKind.DWord);
        }
        catch
        {
            // Settings are best-effort; the default remains effective when the registry is unavailable.
        }
    }
}
