using System;
using Microsoft.Win32;

namespace CodexQuota;

/// <summary>
/// Persistent taskbar-tile appearance toggles, stored under HKCU\Software\CodexQuota. Raised by
/// <see cref="Changed"/> so the tile (and anything else that renders appearance) can re-render.
/// </summary>
public static class WidgetAppearanceSettings
{
    private const string KeyPath = @"Software\CodexQuota";
    private const string ShowIconValueName = "ShowIcon";
    private const string ShowProgressBarValueName = "ShowProgressBar";
    private const string ColorCodeTextValueName = "ColorCodeText";
    private const string WarningUpperValueName = "WarningUpperPercent";
    private const string WarningLowerValueName = "WarningLowerPercent";

    /// <summary>Raised when any appearance setting changes.</summary>
    public static event Action? Changed;

    // One-time migration from the pre-rename "HideIcon"/"HideProgressBar" keys (inverted semantics),
    // left behind when the toggles flipped to Show*. Runs before any property is read so legacy users
    // keep their chosen look; a value already set under the new name wins over the legacy one.
    static WidgetAppearanceSettings()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key is null)
                return;

            MigrateInverted(key, "HideIcon", ShowIconValueName);
            MigrateInverted(key, "HideProgressBar", ShowProgressBarValueName);
        }
        catch
        {
            // Registry unavailable: defaults apply and the legacy keys stay for a later launch.
        }
    }

    /// <summary>
    /// Deletes a legacy Hide* value, mapping it (inverted) onto the Show* value — unless the new
    /// value already exists, in which case it is authoritative.
    /// </summary>
    internal static void MigrateInverted(RegistryKey key, string legacyName, string newName)
    {
        if (key.GetValue(legacyName) is not int legacy)
            return;

        key.DeleteValue(legacyName, throwOnMissingValue: false);
        if (key.GetValue(newName) is int)
            return;

        key.SetValue(newName, legacy == 0 ? 1 : 0, RegistryValueKind.DWord);
    }

    /// <summary>Shows the Codex badge glyph in the taskbar tile (cleared shows the name letter instead).</summary>
    public static bool ShowIcon
    {
        get => ReadBool(ShowIconValueName, defaultValue: true);
        set => WriteBool(ShowIconValueName, value);
    }

    /// <summary>Shows the progress bars in the taskbar tile; percentages always remain.</summary>
    public static bool ShowProgressBar
    {
        get => ReadBool(ShowProgressBarValueName, defaultValue: true);
        set => WriteBool(ShowProgressBarValueName, value);
    }

    /// <summary>
    /// Colors the remaining-percent text by urgency: default white above the upper boundary (default
    /// 50%), caution amber at or below it, critical red at or below the lower boundary (default 20%).
    /// </summary>
    public static bool ColorCodeText
    {
        get => ReadBool(ColorCodeTextValueName, defaultValue: false);
        set => WriteBool(ColorCodeTextValueName, value);
    }

    /// <summary>Remaining-percent boundary for the caution (amber) color and the upper bar marker.</summary>
    public static int WarningUpperPercent
    {
        get => Math.Clamp(ReadInt(WarningUpperValueName, 50), 1, 99);
        set => WriteInt(WarningUpperValueName, Math.Clamp(value, 1, 99));
    }

    /// <summary>Remaining-percent boundary for the critical (red) color and the lower bar marker.</summary>
    public static int WarningLowerPercent
    {
        get => Math.Clamp(ReadInt(WarningLowerValueName, 20), 0, WarningUpperPercent - 1);
        set => WriteInt(WarningLowerValueName, Math.Clamp(value, 0, 99));
    }

    private static bool ReadBool(string name, bool defaultValue)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: false);
            return key?.GetValue(name) is int value ? value != 0 : defaultValue;
        }
        catch
        {
            return defaultValue;
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

    private static void WriteBool(string name, bool value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
            key?.SetValue(name, value ? 1 : 0, RegistryValueKind.DWord);
        }
        catch
        {
            // Appearance is best-effort; the tile just keeps its default look.
        }

        Changed?.Invoke();
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
            // Appearance is best-effort; the tile just keeps its default look.
        }

        Changed?.Invoke();
    }
}
