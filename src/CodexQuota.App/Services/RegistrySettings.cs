using System;
using Microsoft.Win32;

namespace CodexQuota;

/// <summary>
/// Shared HKCU registry helpers for settings classes. Single coercion point with
/// DWORD/QWORD/REG_SZ tolerance; all I/O is best-effort (swallows, optional hook).
/// </summary>
internal static class RegistrySettings
{
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

    internal static long CoerceLong(object? raw, long defaultValue)
    {
        try
        {
            switch (raw)
            {
                case null:
                    return defaultValue;
                case long l:
                    return l;
                case int i:
                    return i;
                case string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsed):
                    return parsed;
                case IConvertible convertible:
                    return Convert.ToInt64(convertible, System.Globalization.CultureInfo.InvariantCulture);
                default:
                    return defaultValue;
            }
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static bool CoerceBool(object? raw, bool defaultValue)
    {
        try
        {
            switch (raw)
            {
                case null:
                    return defaultValue;
                case bool b:
                    return b;
                case int i:
                    return i != 0;
                case long l:
                    return l != 0;
                case string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed):
                    return parsed != 0;
                case string s when long.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out long parsedLong):
                    return parsedLong != 0;
                case IConvertible convertible:
                    return Convert.ToInt32(convertible, System.Globalization.CultureInfo.InvariantCulture) != 0;
                default:
                    return defaultValue;
            }
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static int ReadInt(string keyPath, string name, int defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            // Accept DWORD/QWORD/REG_SZ instead of silently defaulting on a type mismatch
            // (e.g. a QWORD written by another version).
            return CoerceInt(key?.GetValue(name), defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static long ReadLong(string keyPath, string name, long defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return CoerceLong(key?.GetValue(name), defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static bool ReadBool(string keyPath, string name, bool defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return CoerceBool(key?.GetValue(name), defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }

    internal static bool WriteInt(string keyPath, string name, int value)
        => WriteInt(keyPath, name, value, onError: null);

    internal static bool WriteInt(string keyPath, string name, int value, Action<Exception>? onError)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key?.SetValue(name, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return false;
        }
    }

    internal static bool WriteLong(string keyPath, string name, long value)
        => WriteLong(keyPath, name, value, onError: null);

    internal static bool WriteLong(string keyPath, string name, long value, Action<Exception>? onError)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key?.SetValue(name, value, RegistryValueKind.QWord);
            return true;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return false;
        }
    }

    internal static bool WriteBool(string keyPath, string name, bool value)
        => WriteBool(keyPath, name, value, onError: null);

    internal static bool WriteBool(string keyPath, string name, bool value, Action<Exception>? onError)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return false;
        }
    }

    /// <summary>
    /// Check-write-delete legacy migration: maps a legacy int value onto the new value (unless the
    /// new value already exists, which wins), then deletes the legacy value. Writes the new value
    /// first so a crash cannot lose both.
    /// </summary>
    internal static void MigrateValue(RegistryKey key, string legacyName, string newName, Func<int, int> map)
    {
        if (key.GetValue(legacyName) is not int legacy)
            return;

        // No Delete-before-Set: write the new value first so a crash cannot lose both.
        if (key.GetValue(newName) is not int)
            key.SetValue(newName, map(legacy), RegistryValueKind.DWord);
        key.DeleteValue(legacyName, throwOnMissingValue: false);
    }
}
