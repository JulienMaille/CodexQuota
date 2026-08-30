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
        get => ReadInt(ArmedValueName, 0) != 0;
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
        set => WriteLong(TargetResetAtValueName, value);
    }

    public long BaselineWeeklyResetAtUtcTicks
    {
        get => ReadLong(BaselineWeeklyValueName, 0);
        set => WriteLong(BaselineWeeklyValueName, value);
    }

    public long LastFiredResetAtUtcTicks
    {
        get => ReadLong(LastFiredValueName, 0);
        set => WriteLong(LastFiredValueName, value);
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
            // Settings are best-effort; the service keeps running with in-memory state.
        }
    }

    private static long ReadLong(string name, long defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(name) is long value ? value : defaultValue;
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
        catch
        {
            // Settings are best-effort; the service keeps running with in-memory state.
        }
    }
}
