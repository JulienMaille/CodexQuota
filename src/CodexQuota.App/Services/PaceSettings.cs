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
            // P2: only announce a change that actually persisted; otherwise the UI would show an
            // unpersisted value (the getter re-reads the registry, so it keeps showing the old one).
            if (WriteInt(WorkdayHoursValueName, Math.Clamp(value, 1, 24)))
                Changed?.Invoke();
        }
    }

    private static int ReadInt(string name, int defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            // P2: accept DWORD/QWORD/REG_SZ instead of silently defaulting on a type mismatch
            // (e.g. a QWORD written by another version).
            return CoerceInt(key?.GetValue(name), defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static int CoerceInt(object? raw, int defaultValue)
    {
        try
        {
            return raw switch
            {
                null => defaultValue,
                int i => i,
                long l => l >= int.MinValue && l <= int.MaxValue ? (int)l : defaultValue,
                string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) => parsed,
                string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsedLong)
                    && parsedLong >= int.MinValue && parsedLong <= int.MaxValue => (int)parsedLong,
                IConvertible convertible => Convert.ToInt32(convertible, System.Globalization.CultureInfo.InvariantCulture),
                _ => defaultValue,
            };
        }
        catch
        {
            return defaultValue;
        }
    }

    private static bool WriteInt(string name, int value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key?.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch
        {
            // Settings are best-effort; the default remains effective when the registry is unavailable.
            return false;
        }
    }
}
