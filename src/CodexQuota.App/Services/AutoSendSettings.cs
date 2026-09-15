using System;
using Microsoft.Win32;

namespace CodexQuota.Services;

/// <summary>Whether a simultaneous weekly reset blocks the send when the session limit resets.</summary>
public enum AutoSendMode
{
    /// <summary>Always send when the session window resets.</summary>
    Always = 0,
    /// <summary>Skip the send when the weekly window reset at the same time.</summary>
    SkipIfWeeklyReset = 1,
}

/// <summary>Persistence for the auto-send arm state, so an armed trigger survives an app restart.</summary>
public interface IAutoSendStore
{
    bool Armed { get; set; }
    AutoSendMode Mode { get; set; }
    /// <summary>UTC ticks of the session <c>ResetAt</c> the service is armed for, or 0.</summary>
    long TargetResetAtUtcTicks { get; set; }
    /// <summary>UTC ticks of the weekly <c>ResetAt</c> baseline captured while armed, or 0.</summary>
    long BaselineWeeklyResetAtUtcTicks { get; set; }
    /// <summary>UTC ticks of the session <c>ResetAt</c> the last fire/skip acted on, or 0.</summary>
    long LastFiredResetAtUtcTicks { get; set; }
}

/// <summary>Registry-backed <see cref="IAutoSendStore"/> under HKCU\Software\CodexQuota.</summary>
public sealed class AutoSendSettings : IAutoSendStore
{
    private const string KeyPath = @"Software\CodexQuota";
    private const string ArmedValueName = "AutoSendArmed";
    private const string ModeValueName = "AutoSendMode";
    private const string TargetResetAtValueName = "AutoSendTargetResetAt";
    private const string BaselineWeeklyValueName = "AutoSendBaselineWeeklyResetAt";
    private const string LastFiredValueName = "AutoSendLastFiredResetAt";

    public bool Armed
    {
        get
        {
            // P1/P5 torn-write guard: a crash between the Target and Armed writes must never
            // resurrect an arm with no target. Armed with Target==0 reads as disarmed.
            try
            {
                if (ReadInt(ArmedValueName, 0) == 0)
                    return false;
                return ReadLong(TargetResetAtValueName, 0) != 0;
            }
            catch (Exception ex)
            {
                Diagnostics.Log.Warning(ex, "Auto-send store read failed (Armed)");
                return false;
            }
        }
        set => WriteInt(ArmedValueName, value ? 1 : 0);
    }

    public AutoSendMode Mode
    {
        get => ReadInt(ModeValueName, (int)AutoSendMode.SkipIfWeeklyReset) == (int)AutoSendMode.Always
            ? AutoSendMode.Always
            : AutoSendMode.SkipIfWeeklyReset;
        set => WriteInt(ModeValueName, (int)value);
    }

    public long TargetResetAtUtcTicks
    {
        get => ReadLong(TargetResetAtValueName, 0);
        set => WriteLong(TargetResetAtValueName, NormalizeTicks(value));
    }

    public long BaselineWeeklyResetAtUtcTicks
    {
        get => ReadLong(BaselineWeeklyValueName, 0);
        set => WriteLong(BaselineWeeklyValueName, NormalizeTicks(value));
    }

    public long LastFiredResetAtUtcTicks
    {
        get => ReadLong(LastFiredValueName, 0);
        set => WriteLong(LastFiredValueName, NormalizeTicks(value));
    }

    /// <summary>P2: corrupt registry values must never crash
    /// <c>new DateTimeOffset(ticks)</c> in <c>Start</c>. Out-of-range ticks coerce to 0 (disarmed).</summary>
    internal static long NormalizeTicks(long ticks)
        => ticks >= DateTimeOffset.MinValue.UtcTicks && ticks <= DateTimeOffset.MaxValue.UtcTicks ? ticks : 0;

    private static int ReadInt(string name, int defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            // P2: accept DWORD/QWORD/REG_SZ instead of silently defaulting on a type mismatch.
            return PaceSettings.CoerceInt(key?.GetValue(name), defaultValue);
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
        catch (Exception ex)
        {
            // Registry writes are best-effort (locked hive, no profile): surface the failure so a
            // crash-restart that loses the arm is diagnosable. The service keeps running with
            // in-memory state only — there is no cross-process in-memory fallback for the store.
            Diagnostics.Log.Warning(ex, $"Auto-send store write failed ({name})");
        }
    }

    private static long ReadLong(string name, long defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return CoerceLong(key?.GetValue(name), defaultValue);
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

    private static void WriteLong(string name, long value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key?.SetValue(name, value, RegistryValueKind.QWord);
        }
        catch (Exception ex)
        {
            // Registry writes are best-effort (locked hive, no profile): surface the failure so a
            // crash-restart that loses the arm is diagnosable. The service keeps running with
            // in-memory state only — there is no cross-process in-memory fallback for the store.
            Diagnostics.Log.Warning(ex, $"Auto-send store write failed ({name})");
        }
    }
}
