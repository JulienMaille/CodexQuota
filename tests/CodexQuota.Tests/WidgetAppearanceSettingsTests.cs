using System;
using Microsoft.Win32;

namespace CodexQuota.Tests;

public class WidgetAppearanceSettingsTests
{
    private const string TestRoot = @"Software\CodexQuotaTests";

    private static RegistryKey OpenTestKey() => Registry.CurrentUser.OpenSubKey(TestRoot, writable: true)!;

    private static void Cleanup()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false); }
        catch { }
    }

    // Legacy Hide* key only: the new Show* key is written inverted and the legacy key is deleted.
    [Fact]
    public void MigrateInverted_LegacyHidden_ShowValueClearedAndLegacyDeleted()
    {
        try
        {
            using (var setup = Registry.CurrentUser.CreateSubKey(TestRoot))
                setup!.SetValue("HideIcon", 1, RegistryValueKind.DWord);

            using (var key = OpenTestKey())
                WidgetAppearanceSettings.MigrateInverted(key, "HideIcon", "ShowIcon");

            using (var key = OpenTestKey())
            {
                Assert.Equal(0, key.GetValue("ShowIcon"));   // hidden → not shown
                Assert.Null(key.GetValue("HideIcon"));       // legacy removed
            }
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void MigrateInverted_LegacyVisible_ShowSetToTrue()
    {
        try
        {
            using (var setup = Registry.CurrentUser.CreateSubKey(TestRoot))
                setup!.SetValue("HideProgressBar", 0, RegistryValueKind.DWord);

            using (var key = OpenTestKey())
                WidgetAppearanceSettings.MigrateInverted(key, "HideProgressBar", "ShowProgressBar");

            using (var key = OpenTestKey())
            {
                Assert.Equal(1, key.GetValue("ShowProgressBar"));
                Assert.Null(key.GetValue("HideProgressBar"));
            }
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void MigrateInverted_NewValueAlreadySet_WinsOverLegacy()
    {
        try
        {
            using (var setup = Registry.CurrentUser.CreateSubKey(TestRoot))
            {
                setup!.SetValue("HideIcon", 1, RegistryValueKind.DWord);
                setup.SetValue("ShowIcon", 1, RegistryValueKind.DWord); // user already chose under new name
            }

            using (var key = OpenTestKey())
                WidgetAppearanceSettings.MigrateInverted(key, "HideIcon", "ShowIcon");

            using (var key = OpenTestKey())
            {
                Assert.Equal(1, key.GetValue("ShowIcon"));   // new value untouched
                Assert.Null(key.GetValue("HideIcon"));       // legacy still removed
            }
        }
        finally { Cleanup(); }
    }

    [Fact]
    public void MigrateInverted_NoLegacy_LeavesKeyUntouched()
    {
        try
        {
            using (var setup = Registry.CurrentUser.CreateSubKey(TestRoot))
                setup!.SetValue("ShowIcon", 0, RegistryValueKind.DWord);

            using (var key = OpenTestKey())
                WidgetAppearanceSettings.MigrateInverted(key, "HideIcon", "ShowIcon");

            using (var key = OpenTestKey())
                Assert.Equal(0, key.GetValue("ShowIcon"));
        }
        finally { Cleanup(); }
    }
}