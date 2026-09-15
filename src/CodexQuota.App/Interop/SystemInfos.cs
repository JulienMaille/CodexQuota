using Microsoft.Win32;
using System;
using System.Runtime.InteropServices;

namespace CodexQuota.Interop
{
    public static class SystemInfos
    {
        private const int Windows11_Min_BuildNumber = 22000;
        private static readonly Lazy<int> osBuildNumber = new(GetOSBuildNumber);

        public static bool IsWindows11_OrLater => osBuildNumber.Value >= Windows11_Min_BuildNumber;

        public static RECT GetTaskBarBounds()
        {
            APPBARDATA data = new APPBARDATA();
            data.cbSize = Marshal.SizeOf(data);
            if (Shell32.SHAppBarMessage(AppBarMessage.ABM_GETTASKBARPOS, ref data) == UIntPtr.Zero)
                return default;
            return data.rc;
        }

        public static bool IsTaskBarCentered()
        {
            if (IsWindows11_OrLater)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key != null)
                {
                    return TryGetRegistryInt(key, "TaskbarAl", 1) == 1;
                }
                return true;
            }
            return false;
        }

        public static bool IsTaskBarWidgetsEnabled()
        {
            if (IsWindows11_OrLater)
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
                if (key != null)
                {
                    return TryGetRegistryInt(key, "TaskbarDa", 1) == 1;
                }
                return true;
            }
            return false;
        }

        /// <summary>SystemUsesLightTheme governs the taskbar/Start theme (vs. AppsUseLightTheme for app surfaces).</summary>
        public static bool? IsSystemLightThemeUsed()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                return TryGetRegistryInt(key, "SystemUsesLightTheme", 0) != 0;
            }
            return null;
        }

        public static bool? IsAppsLightThemeUsed()
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                return TryGetRegistryInt(key, "AppsUseLightTheme", 0) != 0;
            }
            return null;
        }

        private static int TryGetRegistryInt(RegistryKey key, string name, int defaultValue)
        {
            try
            {
                object? value = key.GetValue(name, defaultValue);
                return value switch
                {
                    int i => i,
                    long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
                    string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) => parsed,
                    byte[] => defaultValue,
                    _ => defaultValue,
                };
            }
            catch
            {
                return defaultValue;
            }
        }

        private static int GetOSBuildNumber()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                object? osBuildNumberValue = key?.GetValue("CurrentBuildNumber");
                return osBuildNumberValue switch
                {
                    int i => i,
                    long l when l >= int.MinValue && l <= int.MaxValue => (int)l,
                    string s when int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int parsed) => parsed,
                    _ => 0,
                };
            }
            catch
            {
                return 0;
            }
        }
    }
}
