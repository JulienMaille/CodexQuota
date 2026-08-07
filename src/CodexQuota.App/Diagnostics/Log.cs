using System;
using System.Diagnostics;
using System.IO;

namespace CodexQuota.Diagnostics
{
    /// <summary>Tiny logging shim so ported code can call Log.X without pulling Serilog.</summary>
    public static class Log
    {
        // Cap prevents unbounded growth of the temp log; the pre-rotation tail is kept in .old.
        private const long MaxLogBytes = 4 * 1024 * 1024;
        private static readonly string LogPath =
            Path.Combine(Path.GetTempPath(), "CodexQuota.log");
        private static readonly object FileLock = new();
        public static void Information(string message) => Write("INFO", message);
        public static void Debug(string message) => Write("DEBUG", message);
        public static void Warning(string message) => Write("WARN", message);
        public static void Warning(Exception ex, string message) => Write("WARN", $"{message} :: {ex}");
        public static void Error(Exception ex, string message) => Write("ERROR", $"{message} :: {ex}");
        public static void Error(string message) => Write("ERROR", message);

        private static void Write(string level, string message)
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
            Trace.WriteLine(line);
            try
            {
                lock (FileLock)
                {
                    var info = new FileInfo(LogPath);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        File.Copy(LogPath, LogPath + ".old", overwrite: true);
                        File.Delete(LogPath);
                    }

                    File.AppendAllText(LogPath, line + Environment.NewLine);
                }
            }
            catch
            {
            }
        }
    }
}
