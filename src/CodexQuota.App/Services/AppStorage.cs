using System;
using System.IO;
using System.Linq;

namespace CodexQuota;

/// <summary>Paths under %LOCALAPPDATA% and one-time migration from the WinCheck folder name.</summary>
public static class AppStorage
{
    public const string AppFolderName = "CodexQuota";
    private const string LegacyAppFolderName = "WinCheck";

    public static string AppDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    /// <summary>Copies files from %LOCALAPPDATA%\WinCheck when the new folder is empty.</summary>
    public static void MigrateLegacyDataIfNeeded()
    {
        var targetDir = AppDataDirectory;
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyAppFolderName);

        if (!Directory.Exists(legacyDir))
            return;

        Directory.CreateDirectory(targetDir);

        // Only migrate "when empty": a non-empty target already has authoritative data.
        if (Directory.EnumerateFileSystemEntries(targetDir).Any())
            return;

        int failures = 0;
        foreach (var file in Directory.EnumerateFiles(legacyDir))
        {
            var name = Path.GetFileName(file);
            var dest = Path.Combine(targetDir, name);
            if (File.Exists(dest))
                continue;

            try
            {
                File.Copy(file, dest);
            }
            catch (Exception ex)
            {
                // Best-effort migration; surface partial failure instead of swallowing it.
                failures++;
                Diagnostics.Log.Debug($"legacy data migration: skipping '{name}': {ex.Message}");
            }
        }

        if (failures > 0)
            Diagnostics.Log.Warning($"Legacy data migration completed with {failures} skipped file(s); user can copy credentials manually.");
    }
}
