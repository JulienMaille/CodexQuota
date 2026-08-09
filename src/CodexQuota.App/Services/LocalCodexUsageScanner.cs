using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace CodexQuota;

/// <summary>
/// Reads today's observed Codex token usage from the local session journal. The profile endpoint is
/// intentionally retained as the source of truth for historical buckets; this scanner only fills the
/// current UTC day when the server-side aggregation has not caught up yet.
/// </summary>
internal static class LocalCodexUsageScanner
{
    public static long ReadTodayTokens(DateOnly today, string? codexHome = null)
    {
        string home = ResolveCodexHome(codexHome);
        string sessionsRoot = Path.Combine(home, "sessions");
        long total = 0;

        // A session can start before midnight and continue into today, so inspect both date folders.
        foreach (var day in new[] { today.AddDays(-1), today })
        {
            string directory = Path.Combine(
                sessionsRoot,
                day.ToString("yyyy", CultureInfo.InvariantCulture),
                day.ToString("MM", CultureInfo.InvariantCulture),
                day.ToString("dd", CultureInfo.InvariantCulture));

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
                total = SaturatingAdd(total, ReadFileTokens(path, today));
        }

        return total;
    }

    private static long ReadFileTokens(string path, DateOnly today)
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
                    if (!TryString(root, "type", out string? eventType)
                        || !string.Equals(eventType, "event_msg", StringComparison.Ordinal)
                        || !root.TryGetProperty("payload", out var payload)
                        || !TryString(payload, "type", out string? payloadType)
                        || !string.Equals(payloadType, "token_count", StringComparison.Ordinal)
                        || !TryString(root, "timestamp", out string? timestampText)
                        || !DateTimeOffset.TryParse(
                            timestampText,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                            out var timestamp)
                        || DateOnly.FromDateTime(timestamp.UtcDateTime) != today
                        || !payload.TryGetProperty("info", out var info))
                    {
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
                catch (JsonException)
                {
                    // The final line may be in-flight while Codex is appending to the journal.
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
