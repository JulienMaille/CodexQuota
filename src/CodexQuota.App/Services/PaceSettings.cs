using System;

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
        => RegistrySettings.ReadInt(KeyPath, name, defaultValue);

    internal static int CoerceInt(object? raw, int defaultValue)
        => RegistrySettings.CoerceInt(raw, defaultValue);

    private static bool WriteInt(string name, int value)
    {
        // Settings are best-effort; the default remains effective when the registry is unavailable.
        return RegistrySettings.WriteInt(KeyPath, name, value);
    }
}
