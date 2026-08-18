using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using CodexQuota.Diagnostics;
using CodexQuota.Helpers;
using CodexQuota.Usage;

namespace CodexQuota.Controls
{
    /// <summary>
    /// Renders a single Codex <see cref="UsageSnapshot"/> as a compact detail panel for the flyout:
    /// the Codex badge + plan/email header, a row per usage meter with a progress bar and percent, a
    /// credits/reset-credits block, and a last-updated line that dims when the snapshot is stale.
    /// The only input is <see cref="SetResult"/>.
    /// </summary>
    public sealed partial class CodexUsagePanel : UserControl
    {
        private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(5);

        /// <summary>Raised when the header refresh button is clicked; the flyout owns the fetch.</summary>
        public event Action? RefreshRequested;

        /// <summary>Raised when the header settings button is clicked; the flyout owns the appearance section.</summary>
        public event Action? SettingsRequested;

        /// <summary>Raised when the header close button is clicked; the flyout owns hiding.</summary>
        public event Action? CloseRequested;

        private static readonly Brush LogoBrushDark = new SolidColorBrush(Colors.White);
        private static readonly Brush LogoBrushLight = new SolidColorBrush(Color.FromArgb(255, 28, 28, 28));

        public CodexUsagePanel()
        {
            InitializeComponent();
            ApplyLocalizedStrings();
            // The logo has no XAML fill: it is painted here from the effective theme. Loaded alone is not
            // enough — the panel can load (off-screen prewarm) before the window's theme is resolved, and
            // ActualTheme then reports Light even on a dark system, leaving a near-black glyph on the dark
            // acrylic. Re-apply when the theme resolves/changes, and again once the flyout is really shown.
            Loaded += (_, _) => ApplyLogoBrush();
            ActualThemeChanged += (_, _) => ApplyLogoBrush();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // The coordinator's fetch is fire-and-forget; show feedback immediately so a refresh
            // that produces identical numbers (the common case — the server aggregates on its own
            // schedule) never looks like the click did nothing.
            UpdatedText.Text = AppStrings.Get("Ui.Refreshing");
            RefreshRequested?.Invoke();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

        /// <summary>Renders the given Codex usage result (or failure placeholder). Replaces all rows.</summary>
        public void SetResult(UsageResult? result)
        {
            try
            {
                // Build the next rows off-tree first, then swap them in as one step: the panel never
                // shows a cleared-but-unpopulated state, even if building or the native renderer fails.
                var nextRows = new List<UIElement>(8);

                // A refresh that has not produced a real snapshot must not blank the panel: once
                // content is rendered it stays until genuinely new data arrives. Only the very first
                // paint (nothing rendered yet) falls through to the placeholder/failure rendering.
                bool hasContent = UsageRows.Children.Count > 0;

                if (result is null)
                {
                    if (!hasContent)
                    {
                        PlanText.Text = string.Empty;
                        EmailText.Text = string.Empty;
                        UpdatedText.Text = AppStrings.Get("Ui.NoUsageDataYet");
                        SwapRows(nextRows);
                    }
                    return;
                }

                // Stamp the check time only for live fetch outcomes: a disk-restored stale snapshot
                // (IsStale, nothing fetched) must not claim a check that never happened. Failures and
                // pending placeholders have no Fetch either — they stay out of the stamp.
                var usage = result.Fetch?.Usage;
                if (!result.Ok || usage is null || result.IsPending)
                {
                    if (hasContent)
                    {
                        // Keep the rendered meters, but surface a failed refresh (expired token,
                        // API down) in the caption: freezing the old "Last updated HH:mm" line would
                        // make a permanent failure look silently fresh.
                        if (!result.IsPending)
                            UpdatedText.Text = result.Error ?? AppStrings.Get("Ui.LastUpdatedDash");
                        return;
                    }

                    PlanText.Text = AppStrings.LocalizePlan(usage?.LoginMethod);
                    EmailText.Text = usage?.Email ?? string.Empty;
                    nextRows.Add(new TextBlock
                    {
                        Text = AppStrings.LocalizeStatus(result.Error, "Ui.UsageUnavailable"),
                        Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    });
                    UpdatedText.Text = FormatUpdatedLine(result);
                    SwapRows(nextRows);
                    return;
                }

                PlanText.Text = AppStrings.LocalizePlan(usage.LoginMethod);
                EmailText.Text = usage.Email ?? string.Empty;
                RenderMeters(usage, nextRows);
                UpdatedText.Text = FormatUpdatedLine(result);
                SwapRows(nextRows);
            }
            catch (Exception ex)
            {
                // A failed render keeps the previous content: the panel is never emptied by a bad
                // snapshot or a transient native failure.
                Diagnostics.Log.Error(ex, "SetResult render failed; keeping previous content");
            }
        }

        private void SwapRows(List<UIElement> nextRows)
        {
            if (UsageRows.Children.Count == nextRows.Count && SeqEqual(UsageRows.Children, nextRows))
                return;

            UsageRows.Children.Clear();
            foreach (var row in nextRows)
                UsageRows.Children.Add(row);
        }

        /// <summary>Renders the Codex profile's daily token activity as a heatmap grid. Hidden when null.</summary>
        public void SetProfile(CodexProfileSnapshot? profile)
        {
            ProfileSection.Visibility = profile is null ? Visibility.Collapsed : Visibility.Visible;
            if (profile is null)
                return;

            ProfileAsOfText.Text = profile.StatsAsOf is { } asOf
                ? AppStrings.Format(profile.TodayUsageIsLocal ? "Ui.ProfileAsOfLive" : "Ui.ProfileAsOf", asOf)
                : profile.TodayUsageIsLocal ? AppStrings.Get("Ui.LiveLocalSessions") : string.Empty;

            var columns = ProfileHeatmapLayout.Build(MergeLocalHistory(profile.DailyUsageBuckets));
            var next = new List<UIElement>(columns.Count);

            long maxTokens = 0;
            foreach (var column in columns)
                foreach (var cell in column)
                    if (cell.Tokens > maxTokens)
                        maxTokens = cell.Tokens;

            var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
            var quiet = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"];

            foreach (var column in columns)
            {
                var week = new StackPanel { Spacing = 3 };
                foreach (var cell in column)
                {
                    var square = new Border
                    {
                        Width = 12,
                        Height = 12,
                        CornerRadius = new CornerRadius(2),
                        Background = quiet,
                    };

                    if (cell.Tokens > 0)
                    {
                        // Intensity scales with the day's share of the visible-window peak; the accent
                        // keeps the hue theme-consistent while opacity carries the magnitude.
                        double intensity = maxTokens > 0 ? (double)cell.Tokens / maxTokens : 0;
                        square.Background = accent;
                        square.Opacity = 0.25 + 0.75 * intensity;
                    }

                    // Hovering a square shows the exact day + tokens in the caption below the grid;
                    // a 12px square cannot carry the text itself. Use the full localized weekday
                    // and month because this is the readable history detail, not the compact grid.
                    square.PointerEntered += (_, _) => ActivityDetailText.Text = DetailLabel(cell);
                    square.PointerExited += (_, _) => ActivityDetailText.Text = DefaultDetailLabel;
                    week.Children.Add(square);
                }

                next.Add(week);
            }

            // The window always ends at today, so the last cell of the last column is today's bucket:
            // show it as the resting caption (also serves as the label when nothing is hovered).
            var lastColumn = columns[^1];
            DefaultDetailLabel = lastColumn.Count > 0 ? DetailLabel(lastColumn[^1]) : string.Empty;
            ActivityDetailText.Text = DefaultDetailLabel;

            SwapColumns(next);

        }

        private string DefaultDetailLabel = string.Empty;

        /// <summary>
        /// Server buckets plus local journal days the server has not reported yet (the endpoint's
        /// window is ~8 weeks; journals reach back further and also cover the current day before the
        /// server aggregation catches up). Server values stay authoritative where both exist.
        /// </summary>
        private static IReadOnlyList<ProfileUsageBucket> MergeLocalHistory(
            IReadOnlyList<ProfileUsageBucket> server)
        {
            var serverByDay = new Dictionary<DateOnly, long>();
            foreach (var bucket in server)
            {
                if (DateOnly.TryParse(
                        bucket.StartDate,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var day))
                {
                    serverByDay[day] = bucket.Tokens;
                }
            }

            // A superset of the grid window (Build anchors to the containing week's Sunday): scanning
            // slightly wider costs nothing and cannot drop the first rendered column.
            var today = DateOnly.FromDateTime(DateTime.UtcNow.Date);
            var windowStart = today.AddDays(-(ProfileHeatmapLayout.MaxWeeks + 1) * 7);

            var merged = new List<ProfileUsageBucket>(server.Count + 16);
            merged.AddRange(server);
            foreach (var (day, tokens) in LocalCodexUsageScanner.ReadRangeTokensCached(windowStart, today))
            {
                if (!serverByDay.ContainsKey(day))
                {
                    merged.Add(new ProfileUsageBucket(
                        day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        tokens));
                }
            }

            return merged;
        }

        private static string DetailLabel(ProfileHeatmapLayout.DayCell cell) =>
            AppStrings.Format(
                "Ui.TokenDetail",
                cell.Day.ToString("dddd d MMMM", CultureInfo.CurrentUICulture),
                FormatTokens(cell.Tokens));

        private void SwapColumns(List<UIElement> next)
        {
            ActivityColumns.Children.Clear();
            foreach (var column in next)
                ActivityColumns.Children.Add(column);
        }

        private static string FormatTokens(long tokens)
        {
            double value = tokens;
            if (value >= 1_000_000_000)
                return $"{(value / 1_000_000_000).ToString("0.##", CultureInfo.CurrentUICulture)}B";
            if (value >= 1_000_000)
                return $"{(value / 1_000_000).ToString("0.#", CultureInfo.CurrentUICulture)}M";
            if (value >= 1_000)
                return $"{(value / 1_000).ToString("0.#", CultureInfo.CurrentUICulture)}K";
            return tokens.ToString("N0", CultureInfo.CurrentUICulture);
        }

        private static bool SeqEqual(UIElementCollection current, List<UIElement> next)
        {
            for (int i = 0; i < current.Count; i++)
                if (!ReferenceEquals(current[i], next[i]))
                    return false;
            return true;
        }

        private void RenderMeters(UsageSnapshot usage, List<UIElement> rows)
        {
            if (usage.HasPrimaryWindow)
            {
                var primaryLabel = usage.Primary.Label ?? "Session";
                AddMeterRow(rows, primaryLabel, usage.Primary.UsedPercent, usage.Primary.ResetAt, usage.Primary.ResetDescription);
            }

            if (usage.Secondary is { } secondary)
                AddMeterRow(rows, secondary.Label ?? "Weekly", secondary.UsedPercent, secondary.ResetAt, secondary.ResetDescription);

            if (usage.Monthly is { } monthly)
                AddMeterRow(rows, monthly.Label ?? "Monthly", monthly.UsedPercent, monthly.ResetAt, monthly.ResetDescription);

            if (usage.ModelSpecific is { } model)
                AddMeterRow(rows, model.Label ?? "Code review", model.UsedPercent, model.ResetAt, model.ResetDescription);

            foreach (var extra in usage.ExtraRateWindows)
                AddMeterRow(rows, MeterLabel(extra.Title), extra.Window.UsedPercent, extra.Window.ResetAt, extra.Window.ResetDescription);

            if (usage.Cost is { } cost)
                AddCreditsRow(rows, AppStrings.Get("Labels.Credits"), cost);

            if (usage.ResetCredits is { AvailableCount: > 0 } resetCredits)
                AddResetCreditsLine(rows, resetCredits);

            AddPaceRow(rows, usage);
        }

        /// <summary>
        /// The one-glance pace line (design: docs/pace-eta-line.md): at the current weekly burn rate,
        /// when does the cap give out? It uses only the weekly rate window; profile token history is
        /// intentionally not part of the projection.
        /// </summary>
        private void AddPaceRow(List<UIElement> rows, UsageSnapshot usage)
        {
            // The weekly meter is the secondary window, or the primary when the provider relabels a
            // lone weekly window as "Weekly" (see CodexProvider.BuildResult).
            RateWindow? weekly = usage.Secondary ?? (usage.Primary is { Label: "Weekly" } p ? p : null);
            if (weekly is null)
                return;

            var pace = PaceLine.Compute(
                weekly.UsedPercent,
                weekly.ResetAt,
                weekly.WindowMinutes ?? 7 * 24 * 60,
                DateTimeOffset.UtcNow,
                PaceSettings.WorkdayHours);
            if (pace is null)
                return;

            rows.Add(new TextBlock
            {
                Text = AppStrings.FormatPace(pace),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources[QuotaDisplay.BrushKeyForRemaining(pace.RemainingPercent)],
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        private void AddMeterRow(List<UIElement> rows, string label, double usedPercent, DateTimeOffset? resetAt, string? resetDescription)
        {
            double remainingPercent = QuotaDisplay.RemainingPercent(usedPercent);

            // Metadata line first — label, the reset countdown beside it, the percent at the right
            // edge — and the bar as a full-width row beneath. The old layout stranded the reset
            // caption below the bar, splitting the block; one line reads as a single unit. Label and
            // reset share one StackPanel (8px gap, as originally) inside a star column, so a long
            // API-supplied label ellipsizes instead of pushing the percent column off-panel.
            var row = new Grid { ColumnSpacing = 10, RowSpacing = 3 };
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });

            var head = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
            };
            head.Children.Add(new TextBlock
            {
                Text = AppStrings.LocalizeLabel(label),
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            if (!string.IsNullOrWhiteSpace(resetDescription) || ResetDateDisplay.IsImminent(resetAt, DateTimeOffset.UtcNow))
            {
                string resetText = ResetDateDisplay.IsImminent(resetAt, DateTimeOffset.UtcNow) && resetAt is { } resetWhen
                    ? AppStrings.Format("Ui.ResetsOn", ResetDateDisplay.FormatLocalDate(resetWhen))
                    : AppStrings.Format("Ui.ResetsIn", AppStrings.LocalizeCountdown(resetDescription));
                head.Children.Add(new TextBlock
                {
                    Text = resetText,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            row.Children.Add(head);

            var valueBox = new TextBlock
            {
                Text = FormatPercent(remainingPercent),
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // Optional urgency coloring of the remaining percent (default white, amber ≤ upper
            // threshold, red ≤ lower threshold).
            if (WidgetAppearanceSettings.ColorCodeText)
                valueBox.Foreground = (Brush)Application.Current.Resources[QuotaDisplay.BrushKeyForRemaining(remainingPercent)];
            Grid.SetColumn(valueBox, 1);
            row.Children.Add(valueBox);

            var barHost = BuildBar(remainingPercent);
            Grid.SetRow(barHost, 1);
            Grid.SetColumnSpan(barHost, 2);
            row.Children.Add(barHost);

            rows.Add(row);
        }

        private void AddCreditsRow(List<UIElement> rows, string label, CostSnapshot cost)
        {
            double? limit = cost.Limit;
            string value = limit is { } lim && lim > 0
                ? $"{FormatCredits(cost.Amount)} / {FormatCredits(lim)}"
                : FormatCredits(cost.Amount);

            var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });

            var labelBox = new TextBlock
            {
                Text = label,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(labelBox, 0);

            Grid spacer = new()
            {
                Height = 5,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (limit is { } l && l > 0)
            {
                // cost.Amount is the remaining balance, so the bar already tracks what is left.
                double remainingPercent = Math.Clamp(cost.Amount / l * 100, 0, 100);
                spacer = BuildBar(remainingPercent);
            }
            Grid.SetColumn(spacer, 1);

            var valueBox = new TextBlock
            {
                Text = value,
                Style = (Style)Application.Current.Resources["BodyTextBlockStyle"],
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(valueBox, 2);

            row.Children.Add(labelBox);
            row.Children.Add(spacer);
            row.Children.Add(valueBox);
            rows.Add(row);
        }

        private void AddResetCreditsLine(List<UIElement> rows, ResetCreditsSnapshot resetCredits)
        {
            var line = new TextBlock
            {
                Text = AppStrings.Format(
                    "Ui.ResetCreditsCount",
                    resetCredits.AvailableCount.ToString("N0", CultureInfo.CurrentUICulture)),
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            if (resetCredits.EarliestExpiresAt is { } expires)
                line.Text += ResetDateDisplay.IsImminent(expires, DateTimeOffset.UtcNow)
                    ? $" · {AppStrings.Format("Ui.OldestExpiresOn", ResetDateDisplay.FormatLocalDate(expires))}"
                    : $" · {AppStrings.Format("Ui.OldestExpiresIn", AppStrings.LocalizeCountdown(CountdownFormat.Format(expires)))}";
            rows.Add(line);
        }

        private static Grid BuildBar(double fillPercent)
        {
            // Star-sized fill column so the bar spans exactly fillPercent/100 of the track at any
            // panel width. A fixed pixel width leaves the track partially empty even at 100%.
            double clamped = Math.Clamp(fillPercent, 0, 100);
            var host = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(clamped, GridUnitType.Star) });
            host.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - clamped, GridUnitType.Star) });

            var track = new Border
            {
                Background = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
                CornerRadius = new CornerRadius(2),
            };
            Grid.SetColumnSpan(track, 2);
            host.Children.Add(track);

            host.Children.Add(new Border
            {
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(2),
            });

            // Threshold tick marks: a 1px line at each warning boundary, positioned by its own
            // star-split overlay so the marker stays exact regardless of the fill width. The line
            // runs counter to the theme like the tile's bars: light app theme gets a light line,
            // dark gets a dark one, so the threshold reads against both the gray track and the
            // accent fill (the old text-gray at 45% sank into whichever surface it crossed).
            var markerBrush = new SolidColorBrush(
                CodexQuota.Interop.SystemInfos.IsAppsLightThemeUsed() == true ? Colors.White : Colors.Black);
            foreach (int threshold in QuotaDisplay.WarningThresholds())
            {
                var overlay = new Grid();
                Grid.SetColumnSpan(overlay, 2);
                overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(threshold, GridUnitType.Star) });
                overlay.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - threshold, GridUnitType.Star) });
                var tick = new Border
                {
                    Width = 1,
                    Height = 5,
                    Background = markerBrush,
                    Opacity = 0.45,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                Grid.SetColumn(tick, 0);
                overlay.Children.Add(tick);
                host.Children.Add(overlay);
            }
            return host;
        }

        private static string FormatPercent(double percent)
        {
            percent = Math.Clamp(percent, 0, 100);
            return percent == (int)percent
                ? $"{percent.ToString("N0", CultureInfo.CurrentUICulture)}%"
                : $"{percent.ToString("0.#", CultureInfo.CurrentUICulture)}%";
        }

        private static string FormatCredits(double value)
            => value.ToString(value % 1 == 0 ? "N0" : "N1", CultureInfo.CurrentUICulture);

        private string FormatUpdatedLine(UsageResult result)
        {
            if (result.Fetch is not { } fetch)
                return AppStrings.Get("Ui.LastUpdatedDash");

            var local = fetch.FetchedAt.ToLocalTime();
            bool stale = DateTimeOffset.UtcNow - fetch.FetchedAt > StaleThreshold;
            string time = local.ToString("HH:mm", CultureInfo.CurrentUICulture);
            string label = stale
                ? AppStrings.Format("Ui.LastUpdatedStale", time)
                : AppStrings.Format("Ui.LastUpdatedAt", time);
            if (result.IsStale)
                label += AppStrings.Get("Ui.RefreshingSuffix");
            return label;
        }

        private void ApplyLocalizedStrings()
        {
            TokenActivityText.Text = AppStrings.Get("Ui.TokenActivity");

            AutomationProperties.SetName(SettingsButton, AppStrings.Get("Ui.Settings"));
            ToolTipService.SetToolTip(SettingsButton, AppStrings.Get("Ui.ShowHideAppearanceOptions"));

            AutomationProperties.SetName(RefreshButton, AppStrings.Get("Ui.RefreshUsage"));
            ToolTipService.SetToolTip(RefreshButton, AppStrings.Get("Ui.RefreshUsage"));

            AutomationProperties.SetName(CloseButton, AppStrings.Get("Ui.Close"));
            ToolTipService.SetToolTip(CloseButton, AppStrings.Get("Ui.Close"));
        }

        private static string MeterLabel(string title) => title.Trim();

        internal void ApplyLogoBrush()
        {
            if (XamlRoot is null)
                return;

            var brush = ActualTheme == ElementTheme.Dark ? LogoBrushDark : LogoBrushLight;
            ProviderGlyphRenderer.TryApply(LogoPath, ProviderId.Codex, brush, normalizeToViewport: false);
        }
    }
}
