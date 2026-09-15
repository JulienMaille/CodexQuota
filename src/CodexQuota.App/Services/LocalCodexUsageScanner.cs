using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CodexQuota;

/// <summary>
/// Reads Codex token usage from the local session journal. The profile endpoint is retained as the
/// source of truth for historical buckets; journals fill days the server has not reported: today
/// (server aggregation lags), and any gap days older than the server's window so the heatmap can show
/// more than the ~8 weeks the endpoint returns.
/// </summary>
internal static class LocalCodexUsageScanner
{
    /// <summary>How long a range scan result is reused before the journals are re-read.</summary>
    public static readonly TimeSpan CacheAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Width, in weeks, of the canonical journal window scanned and cached on any miss. Must be at least
    /// the flyout heatmap window (<c>ProfileHeatmapLayout.MaxWeeks</c> = 22, plus room to anchor to the
    /// containing week) so a single scan answers every sub-query — today's tokens, the prior-day
    /// spillover, and the full heatmap range — regardless of the order they arrive in.
    /// </summary>
    public const int ScanWindowWeeks = 24;

    private static readonly object CacheLock = new();
    private static string? _cachedHome;
    private static DateOnly _cachedScanStart;
    private static DateOnly _cachedScanEnd;
    private static DateTimeOffset _cachedAtUtc;
    private static IReadOnlyDictionary<DateOnly, long> _cachedRange = new Dictionary<DateOnly, long>();

    /// <summary>
    /// Days before the requested start that must still be read: a session started the previous day can
    /// continue into the first requested day, so the scan window always begins one day early.
    /// </summary>
    private const int ScanSpilloverDays = 1;

    public static long ReadTodayTokens(DateOnly today, string? codexHome = null)
        => ReadRangeTokensCached(today, today, codexHome).GetValueOrDefault(today);

    /// <summary>Tokens observed on a single day, including events of a session started the previous day.</summary>
    public static long ReadDayTokens(DateOnly day, string? codexHome = null)
    {
        string home = ResolveCodexHome(codexHome);
        string sessionsRoot = Path.Combine(home, "sessions");
        long total = 0;

        // A session can start before midnight and continue into the day, so inspect both date folders.
        foreach (var folderDay in new[] { day.AddDays(-1), day })
        {
            string directory = Path.Combine(
                sessionsRoot,
                folderDay.ToString("yyyy", CultureInfo.InvariantCulture),
                folderDay.ToString("MM", CultureInfo.InvariantCulture),
                folderDay.ToString("dd", CultureInfo.InvariantCulture));

            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string path in paths)
                total = SaturatingAdd(total, ReadFileTokens(path, day));
        }

        return total;
    }

    /// <summary>Local tokens per day over [start, end] inclusive; days with no journal activity are absent.</summary>
    public static IReadOnlyDictionary<DateOnly, long> ReadRangeTokens(
        DateOnly start,
        DateOnly end,
        string? codexHome = null)
    {
        var result = new Dictionary<DateOnly, long>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            long tokens = ReadDayTokens(day, codexHome);
            if (tokens > 0)
                result[day] = tokens;
        }

        return result;
    }

    /// <summary>
    /// <see cref="ReadRangeTokens"/> memoized for <see cref="CacheAge"/>: the flyout re-renders the
    /// heatmap on every profile poll, and re-parsing the whole journal on each tick would be wasteful.
    /// </summary>
    public static IReadOnlyDictionary<DateOnly, long> ReadRangeTokensCached(
        DateOnly start,
        DateOnly end,
        string? codexHome = null)
    {
        string home = ResolveCodexHome(codexHome);

        // Serve any requested [start, end] from a single cached managed-full-window scan: historical
        // days are immutable once they end and the current day is always the cache end, so a cached
        // scan answers every sub-range — today's tokens (ReadTodayTokens), the prior-day spillover, and
        // the whole heatmap window — no matter the order the callers ask for them.
        if (TryGetCached(home, start, end) is { } hit)
            return hit;

        // Scan the canonical window in one pass so one I/O+parse run fills the whole cache: from
        // (ScanWindowWeeks back) to end. A narrower sub-request widens to this canonical range so later
        // sub-queries hit instead of re-reading; the request's own end is retained as the cache end when
        // it reaches past today (defensive — every caller ends at today).
        var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
        var scanStart = MinDate(start.AddDays(-ScanSpilloverDays), today.AddDays(-ScanWindowWeeks * 7));
        var scanEnd = MaxDate(end, today);
        var fresh = ReadRangeTokens(scanStart, scanEnd, home);
        lock (CacheLock)
        {
            _cachedHome = home;
            _cachedScanStart = scanStart;
            _cachedScanEnd = scanEnd;
            _cachedAtUtc = DateTimeOffset.UtcNow;
            _cachedRange = fresh;
            return fresh;
        }
    }

    private static IReadOnlyDictionary<DateOnly, long>? TryGetCached(string home, DateOnly requestedStart, DateOnly requestedEnd)
    {
        lock (CacheLock)
        {
            if (_cachedHome != home
                || string.IsNullOrEmpty(_cachedHome)
                || requestedStart < _cachedScanStart
                || requestedEnd > _cachedScanEnd
                || DateTimeOffset.UtcNow - _cachedAtUtc > CacheAge)
            {
                return null;
            }

            return _cachedRange;
        }
    }

    private static DateOnly MinDate(DateOnly a, DateOnly b) => a < b ? a : b;

    private static DateOnly MaxDate(DateOnly a, DateOnly b) => a > b ? a : b;

    private static long ReadFileTokens(string path, DateOnly targetDay)
    {
        long total = 0;
        long previousCumulative = 0;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            while (reader.ReadLine() is { } line)
            {
                if (line.IndexOf("\"type\":\"event_msg\"", StringComparison.Ordinal) < 0
                    || line.IndexOf("\"type\":\"token_count\"", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    // Older journals carry JSON-null fields ("payload":null, "info":null);
                    // TryGetProperty on a non-object element throws InvalidOperationException,
                    // so every navigation hop re-checks ValueKind first.
                    if (root.ValueKind != JsonValueKind.Object
                        || !TryString(root, "type", out string? eventType)
                        || !string.Equals(eventType, "event_msg", StringComparison.Ordinal)
                        || !root.TryGetProperty("payload", out var payload)
                        || payload.ValueKind != JsonValueKind.Object
                        || !TryString(payload, "type", out string? payloadType)
                        || !string.Equals(payloadType, "token_count", StringComparison.Ordinal)
                        || !TryString(root, "timestamp", out string? timestampText)
                        || !DateTimeOffset.TryParse(
                            timestampText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var timestamp)
                        || !payload.TryGetProperty("info", out var info)
                        || info.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var eventDay = DateOnly.FromDateTime(timestamp.UtcDateTime);
                    if (eventDay != targetDay)
                    {
                        // Midnight-spanning session: track the cumulative baseline from pre-target-day
                        // events so the first target-day delta excludes the previous day's usage.
                        if (eventDay < targetDay
                            && info.TryGetProperty("total_token_usage", out var baseline)
                            && TryUsageTotal(baseline, out long baselineTokens)
                            && baselineTokens > previousCumulative)
                        {
                            previousCumulative = baselineTokens;
                        }
                        continue;
                    }

                    if (info.TryGetProperty("last_token_usage", out var last)
                        && TryUsageTotal(last, out long lastTokens))
                    {
                        total = SaturatingAdd(total, lastTokens);
                        continue;
                    }

                    // Older journals expose only the cumulative total. Convert that to a delta so
                    // repeated token_count events do not inflate the current-day value.
                    if (info.TryGetProperty("total_token_usage", out var cumulative)
                        && TryUsageTotal(cumulative, out long cumulativeTokens))
                    {
                        if (cumulativeTokens > previousCumulative)
                            total = SaturatingAdd(total, cumulativeTokens - previousCumulative);
                        previousCumulative = cumulativeTokens;
                    }
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException)
                {
                    // The final line may be in-flight while Codex is appending to the journal, and
                    // older schemas hide JSON nulls where objects are expected. Skip the line rather
                    // than let a journal quirk crash the caller (the flyout opens on this path).
                }
            }
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }

        return total;
    }

    private static bool TryUsageTotal(JsonElement usage, out long total)
    {
        if (TryInt64(usage, "total_tokens", out total))
            return true;

        bool hasInput = TryInt64(usage, "input_tokens", out long input);
        bool hasOutput = TryInt64(usage, "output_tokens", out long output);
        if (!hasInput && !hasOutput)
        {
            total = 0;
            return false;
        }

        total = SaturatingAdd(Math.Max(0, input), Math.Max(0, output));
        return true;
    }

    private static bool TryString(JsonElement parent, string name, out string? value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static bool TryInt64(JsonElement parent, string name, out long value)
    {
        if (parent.ValueKind == JsonValueKind.Object
            && parent.TryGetProperty(name, out var element))
        {
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value))
                return true;
            if (element.ValueKind == JsonValueKind.String
                && long.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }

    private static long SaturatingAdd(long left, long right)
    {
        if (right <= 0)
            return left;
        if (left > long.MaxValue - right)
            return long.MaxValue;
        return left + right;
    }

    private static string ResolveCodexHome(string? codexHome)
    {
        string? configured = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }
}
