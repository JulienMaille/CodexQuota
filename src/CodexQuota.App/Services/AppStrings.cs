using System;
using System.Collections.Generic;
using System.Globalization;
using CodexQuota.Diagnostics;
using Microsoft.Windows.ApplicationModel.Resources;

namespace CodexQuota;

/// <summary>
/// The app's small, code-facing localization layer. WinUI's resource pipeline chooses the best
/// language for the current Windows user; the dictionary keeps tests and unusual unpackaged startup
/// states usable when the PRI is not available yet.
/// </summary>
internal static partial class AppStrings
{
    private sealed class AppResourceAccess
    {
        private readonly ResourceMap _map;
        private readonly ResourceContext _context;

        private AppResourceAccess(ResourceMap map, ResourceContext context)
        {
            _map = map;
            _context = context;
        }

        public static AppResourceAccess? Create()
        {
            try
            {
                var manager = new ResourceManager();
                var context = manager.CreateResourceContext();
                // The WinAppSDK default context follows the Windows language list, which can still
                // prefer en-US in an unpackaged process even when .NET's UI culture is fr-FR. Pin the
                // resource lookup to the nearest locale we ship so regional variants such as es-MX and
                // pt-PT use their language pack instead of falling back to English.
                context.QualifierValues["Language"] = ResourceLanguageFor(CultureInfo.CurrentUICulture);
                return new AppResourceAccess(manager.MainResourceMap.GetSubtree("Resources"), context);
            }
            catch (Exception ex)
            {
                Log.Warning($"AppStrings PRI unavailable (culture={CultureInfo.CurrentUICulture.Name}): {ex.Message}");
                return null;
            }
        }

        public string? GetString(string key)
            => _map.GetValue(key.Replace('.', '/'), _context)?.ValueAsString;
    }

    private static string ResourceLanguageFor(CultureInfo culture)
        => culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "de" => "de-DE",
            "en" => "en-US",
            "es" => "es-ES",
            "fr" => "fr-FR",
            "it" => "it-IT",
            "ja" => "ja-JP",
            "ko" => "ko-KR",
            "pt" => "pt-BR",
            "ru" => "ru-RU",
            "zh" => "zh-CN",
            _ => culture.Name,
        };

    private static readonly Lazy<AppResourceAccess?> Resources = new(() =>
    {
        return AppResourceAccess.Create();
    });

    private static bool _mrtBroken;

    private static readonly IReadOnlyDictionary<string, string> Fallback =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Ui.Settings"] = "Settings",
            ["Ui.ShowHideAppearanceOptions"] = "Show or hide appearance options",
            ["Ui.RefreshUsage"] = "Refresh usage",
            ["Ui.Close"] = "Close",
            ["Ui.TokenActivity"] = "Token activity",
            ["Ui.Refreshing"] = "Refreshing…",
            ["Ui.NoUsageDataYet"] = "No usage data yet.",
            ["Ui.LastUpdatedDash"] = "Last updated ·",
            ["Ui.UsageUnavailable"] = "Usage unavailable",
            ["Ui.ProfileAsOf"] = "as of {0}",
            ["Ui.ProfileAsOfLive"] = "as of {0} · local sessions",
            ["Ui.LiveLocalSessions"] = "local sessions",
            ["Ui.TokenDetail"] = "{0} · {1} tokens",
            ["Ui.ResetsOn"] = "resets {0}",
            ["Ui.ResetsIn"] = "resets in {0}",
            ["Ui.ResetCreditsCount"] = "{0} reset credits",
            ["Ui.OldestExpiresOn"] = "oldest expires {0}",
            ["Ui.OldestExpiresIn"] = "oldest expires in {0}",
            ["Ui.LastUpdatedAt"] = "Last updated {0}",
            ["Ui.LastUpdatedStale"] = "Last updated {0} (stale)",
            ["Ui.RefreshingSuffix"] = " · refreshing…",
            ["Ui.AppearanceIcon"] = "Icon",
            ["Ui.AppearanceBars"] = "Bars",
            ["Ui.AppearanceColorPercent"] = "Color %",
            ["Ui.WarnBelowPercent"] = "Warn below %",
            ["Ui.WorkdayHours"] = "Workday hours",
            ["Ui.ShowBadgeTooltip"] = "Show the Codex badge in the taskbar tile",
            ["Ui.ShowBarsTooltip"] = "Show progress bars in the taskbar tile; percentages always stay",
            ["Ui.ShowColorTooltip"] = "Color the remaining percent: orange at 50% or less, red at 20% or less",
            ["Ui.CautionTooltip"] = "Caution (orange) below this remaining percent",
            ["Ui.CriticalTooltip"] = "Critical (red) below this remaining percent",
            ["Ui.WorkdayHoursTooltip"] = "Assumed coding hours in each 24-hour quota day",
            ["Labels.Session"] = "Session",
            ["Labels.Weekly"] = "Weekly",
            ["Labels.Monthly"] = "Monthly",
            ["Labels.CodeReview"] = "Code review",
            ["Labels.Credits"] = "Credits",
            ["Labels.Resets"] = "Resets",
            ["Labels.Usage"] = "Usage",
            ["Labels.Model"] = "Model",
            ["Labels.SparkSession"] = "Spark Session",
            ["Labels.SparkWeekly"] = "Spark Weekly",
            ["Labels.Total"] = "Total",
            ["Labels.AutoComposer"] = "Auto+Composer",
            ["Labels.Api"] = "API",
            ["Plan.Guest"] = "Guest",
            ["Plan.Free"] = "Free",
            ["Plan.Business"] = "Business",
            ["Plan.Enterprise"] = "Enterprise",
            ["Plan.Education"] = "Education",
            ["Time.Now"] = "now",
            ["Time.Day"] = "d",
            ["Time.Hour"] = "h",
            ["Time.Minute"] = "m",
            ["Time.UnitSeparator"] = string.Empty,
            ["Widget.Loading"] = "Loading…",
            ["Widget.LoadingActiveProvider"] = "Loading active provider…",
            ["Widget.Unavailable"] = "Unavailable",
            ["Widget.Unknown"] = "unknown",
            ["Widget.ResetCreditsAvailable"] = "Reset credits: {0} available",
            ["Widget.ResetGrantedExpires"] = "Reset {0}: granted {1}, expires {2}",
            ["Widget.MoreResetCredits"] = "+{0} more reset credits",
            ["Widget.LastUpdatedRefreshing"] = "Last updated {0} · refreshing…",
            ["Widget.OldestExpiresOn"] = "oldest expires {0}",
            ["Widget.OldestExpiresNow"] = "oldest expires now",
            ["Widget.OldestExpiresIn"] = "oldest expires in {0}",
            ["Widget.ResetsOn"] = "resets {0}",
            ["Widget.ResetsNow"] = "resets now",
            ["Widget.ResetsIn"] = "resets in {0}",
            ["Pace.CapReached"] = "Pace · cap reached · resets {0}",
            ["Pace.CapBeforeReset"] = "Pace ~{0}% quota/day → cap {1} (~{2:0.#}d)",
            ["Pace.ResetsBeforeCap"] = "Pace ~{0}% quota/day · resets before cap",
        };

    public static string Get(string key)
    {
        // MRT Core is unreliable in this unpackaged build: when its first lookup throws, every
        // subsequent GetValue throws too. Switch the whole process to the embedded dictionaries so
        // the per-key exceptions don't recur. Localized (generated from the resw files) keeps the
        // exact shipping translations; Fallback is the last-resort English floor.
        if (!_mrtBroken)
        {
            try
            {
                if (Resources.Value is { } resources)
                {
                    string? value = resources.GetString(key);
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
            catch (Exception ex)
            {
                _mrtBroken = true;
                Log.Warning($"AppStrings: MRT Core unavailable (culture={CultureInfo.CurrentUICulture.Name}); using embedded localization dictionaries. {ex.Message}");
            }
        }

        if (Localized.TryGetValue(ResourceLanguageFor(CultureInfo.CurrentUICulture), out var localized)
            && localized.TryGetValue(key, out var localizedValue))
        {
            return localizedValue;
        }

        return Fallback.TryGetValue(key, out var fallback) ? fallback : key;
    }

    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentUICulture, Get(key), args);

    public static string FormatPace(PaceLineResult pace)
    {
        string rate = PaceLine.FormatQuotaRate(pace.RatePercentPerDay);
        if (pace.CapReached)
            return Format("Pace.CapReached", DayName(pace.ResetAt));

        if (pace.WillExhaustBeforeReset && pace.CapAt is { } capAt)
            return Format("Pace.CapBeforeReset", rate, DayName(capAt), pace.DaysToCap);

        return Format("Pace.ResetsBeforeCap", rate);
    }

    public static string LocalizeLabel(string label)
    {
        label = label.Trim();
        return label switch
        {
            "Total" or "Total usage" => Get("Labels.Total"),
            "Auto+Composer" or "Auto + Composer Usage" => Get("Labels.AutoComposer"),
            "API" or "API Usage" => Get("Labels.Api"),
            "Session" => Get("Labels.Session"),
            "Weekly" => Get("Labels.Weekly"),
            "Monthly" => Get("Labels.Monthly"),
            "Code review" => Get("Labels.CodeReview"),
            "Credits" => Get("Labels.Credits"),
            "Resets" => Get("Labels.Resets"),
            "Usage" => Get("Labels.Usage"),
            "Model" => Get("Labels.Model"),
            "Spark Session" => Get("Labels.SparkSession"),
            "Spark Weekly" => Get("Labels.SparkWeekly"),
            _ => label,
        };
    }

    public static string LocalizePlan(string? plan)
    {
        if (string.IsNullOrWhiteSpace(plan))
            return string.Empty;

        return plan.Trim() switch
        {
            "Guest" => Get("Plan.Guest"),
            "Free" => Get("Plan.Free"),
            "Team" or "Teams" => plan.Trim(),
            "Business" => Get("Plan.Business"),
            "Enterprise" => Get("Plan.Enterprise"),
            "Education" => Get("Plan.Education"),
            _ => plan.Trim(),
        };
    }

    public static string LocalizeCountdown(string? countdown)
    {
        if (string.IsNullOrWhiteSpace(countdown))
            return string.Empty;
        if (string.Equals(countdown, "now", StringComparison.OrdinalIgnoreCase))
            return Get("Time.Now");

        var parts = new List<string>();
        foreach (var part in countdown.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length < 2 || !int.TryParse(part[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
                return countdown;

            string unit = part[^1] switch
            {
                'd' => Get("Time.Day"),
                'h' => Get("Time.Hour"),
                'm' => Get("Time.Minute"),
                _ => string.Empty,
            };
            if (unit.Length == 0)
                return countdown;

            parts.Add($"{value.ToString(CultureInfo.CurrentUICulture)}{Get("Time.UnitSeparator")}{unit}");
        }

        return string.Join(" ", parts);
    }

    public static string LocalizeStatus(string? status, string fallbackKey)
    {
        if (string.IsNullOrWhiteSpace(status))
            return Get(fallbackKey);

        return status.Trim() switch
        {
            "Loading..." or "Loading…" => Get("Widget.Loading"),
            "Loading active provider..." or "Loading active provider…" => Get("Widget.LoadingActiveProvider"),
            "Unavailable" => Get("Widget.Unavailable"),
            "Usage unavailable" => Get("Ui.UsageUnavailable"),
            _ => status,
        };
    }

    private static string DayName(DateTimeOffset when)
        => when.ToLocalTime().ToString("dddd", CultureInfo.CurrentUICulture);
}
