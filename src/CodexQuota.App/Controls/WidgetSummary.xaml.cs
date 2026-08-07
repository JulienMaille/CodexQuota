using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using CodexQuota.Helpers;
using CodexQuota.Usage;

namespace CodexQuota.Controls
{
    public sealed partial class WidgetSummary : UserControl, IDisposable
    {
        private const int MaxRowsPerGroup = 2;
        private const int MinLabelColumnWidth = 0;
        private const int MinResetColumnWidth = 0;
        private const int ValueColumnWidth = 34;
        private const int WidgetFontSize = 11;
        private const int BarHeight = 6;
        private const int SingleRowBarHeight = 8;
        private const int BarWidthBarsOnly = 54;
        private const int BarWidthBarsAndPercentages = 46;
        private const int BarColumnWidthBarsOnly = 54;
        private const int BarColumnWidthBarsAndPercentages = 46;
        private const int IconHostSizeBars = 30;
        private const int IconHostSizePercentagesOnly = 26;
        private const double RowLabelGlyphSize = 12;
        private const double RowLabelGlyphReserve = 18;
        private const double GlyphViewportSize = 100;

        /// <summary>Width of the badge column; collapses to 0 when the icon is hidden.</summary>
        private static double IconColumnLogicalWidth
            => WidgetAppearanceSettings.ShowIcon ? IconHostSizeBars : 0;

        /// <summary>Width of the bar column; collapses to 0 when progress bars are hidden
        /// (percentages stay).</summary>
        private static double BarColumnLogicalWidth
            => WidgetAppearanceSettings.ShowProgressBar ? BarColumnWidthBarsAndPercentages : 0;
        private const double NormalizedGlyphExtent = 88;
        private const double StaleOpacity = 0.55;
        private const int PanelColumnSpacing = 5;
        private const int SlideMilliseconds = 300;
        // Slack the width math reserves on top of the measured columns, so a rounding difference between
        // the analytic total and what the Grid actually arranges can never clip the last column.
        private const int WidthSlack = 2;

        /// <summary>Placeholder shown before any usage has been applied. Shared and never mutated, so a
        /// tile that renders on every usage publish does not allocate a list per pass.</summary>
        private static readonly List<WidgetUsageRow> PlaceholderRows = [new("Usage", 0, "--")];

        public event Action? Clicked;
        public event Action<int>? DesiredHostWidthChanged;

        public bool SuppressNextClick { get; set; }

        /// <summary>
        /// Skips the cross-fade on the next render. Set when a tile is taking over another provider as part
        /// of a re-order: the movement is being conveyed by <see cref="AnimateSlide"/>, and fading the
        /// content at the same time turns a clean shift into a flicker.
        /// </summary>
        public bool SuppressNextTransition { get; set; }

        /// <summary>
        /// The width (logical px) this tile last asked its host for. The taskbar host sums this across the
        /// tiles it shows to size the widget and to decide how many tiles actually fit.
        /// </summary>
        public int DesiredLogicalWidth { get; private set; }

        private readonly List<RenderedRow> _renderedRows = new();
        // Threshold-line brush for the bars, re-derived on every taskbar theme change. The line runs
        // counter to the text colors: the track inverts with the taskbar theme (dark on a light
        // taskbar, light on a dark one), so a light line on light and dark on dark keeps it visible.
        private SolidColorBrush _markerBrush = new SolidColorBrush(Colors.White);
        private List<WidgetUsageRow> _rows = new();
        private UsageResult? _lastResult;
        private ProviderId? _lastAppliedProvider;
        private string? _lastRenderSignature;
        private bool _hasRevealed;
        private bool _isActiveToolVisible = true;
        // Storyboards and their animations are allocated on first use and re-aimed afterwards; all three
        // run on ordinary usage publishes, so rebuilding them per pass was continuous garbage.
        private Storyboard? _visibilityStoryboard;
        private DoubleAnimation? _visibilityOpacity;
        private DoubleAnimation? _visibilityOffset;
        private Storyboard? _softRefreshStoryboard;
        private DoubleAnimation? _softRefreshAnimation;
        private Storyboard? _slideStoryboard;
        private DoubleAnimation? _slideAnimation;

        /// <summary>
        /// Returns the display name for the constrained taskbar widget.
        /// Providers with long brand names expose a short DisplayName so the tray widget stays compact.
        /// </summary>
        private static string WidgetDisplayName(string fullName)
            => string.IsNullOrEmpty(fullName) ? fullName
            : fullName switch
            {
                "GitHub Copilot" => "Copilot",
                _ => fullName,
            };

        /// <summary>Rows this tile shows — the full Codex row set; there are no per-row visibility toggles.</summary>
        public int RowCount => Math.Max(1, _rows.Count);

        /// <summary>
        /// The width this tile WOULD take, without rendering it.
        ///
        /// The host sums this across its tiles to size the widget host window. Rendering to read the width
        /// made the tile visibly flash — every re-render restarts the refresh animation, and the host
        /// re-runs this on every usage publish — so the column widths, which are a pure function of the
        /// rows, are computed directly instead.
        /// </summary>
        public int MeasureDesiredWidth()
            => CalculateDesiredWidth(CurrentRows());

        public HorizontalAlignment ElementsAlignment
        {
            get => Panel.HorizontalAlignment;
            set => Panel.HorizontalAlignment = value;
        }

        public WidgetSummary()
        {
            InitializeComponent();
            ApplyTaskbarForeground();
            RenderRows();
            WidgetAppearanceSettings.Changed += OnAppearanceChanged;
            SystemThemeWatcher.Start();
            SystemThemeWatcher.Changed += OnSystemThemeChanged;
            // The tile click is a Tapped gesture. It must stay a gesture event: the widget host
            // captures the pointer during presses (drag/reposition machinery), and raw
            // PointerReleased routes to the capture owner, not to this tile.
            Tapped += (_, _) =>
            {
                if (SuppressNextClick)
                {
                    SuppressNextClick = false;
                    return;
                }
                Clicked?.Invoke();
            };
        }

        // B1: the appearance Changed event is static, so an unsubscribed summary is rooted forever and
        // leaks on every widget recreation (DPI/monitor/taskbar change, Explorer restart). The widget
        // calls Dispose for each tile in its own Dispose path.
        public void Dispose()
        {
            WidgetAppearanceSettings.Changed -= OnAppearanceChanged;
            SystemThemeWatcher.Changed -= OnSystemThemeChanged;
        }

        /// <summary>Re-renders the tile when the taskbar theme flips (dark/light mode switch).</summary>
        private void OnSystemThemeChanged()
        {
            // The signature now includes the theme, so a plain Apply re-renders with the new colors.
            if (_lastResult is { } result)
                Apply(result);
            else
                ApplyTaskbarForeground();
        }

        /// <summary>
        /// Re-renders when the tile's appearance toggles change (hide icon / hide progress bar). The
        /// re-render re-measures and raises <see cref="DesiredHostWidthChanged"/>, so the host re-lays
        /// itself out to the new width automatically.
        /// </summary>
        private void OnAppearanceChanged()
        {
            if (_lastResult is { } result)
                Apply(result, force: true);
            else
                RenderRows();
        }

        private void ApplyTaskbarForeground()
        {
            bool light = Interop.SystemInfos.IsSystemLightThemeUsed() == true;
            Foreground = new SolidColorBrush(light ? Color.FromArgb(255, 28, 28, 28) : Colors.White);
            var track = new SolidColorBrush(light ? Color.FromArgb(90, 28, 28, 28) : Color.FromArgb(110, 255, 255, 255));
            _markerBrush = new SolidColorBrush(light ? Colors.White : Colors.Black);

            foreach (var row in _renderedRows)
            {
                row.Track.Background = track;
                row.Value.Foreground = Foreground;
                foreach (var marker in row.Markers)
                    marker.Background = _markerBrush;
            }
            ProviderGlyphRenderer.TryApply(BadgeGlyph, ProviderId.Codex, Foreground);
        }

        public void Apply(UsageResult result, bool force = false)
        {
            var signature = BuildRenderSignature(result);
            if (!force && _lastRenderSignature == signature)
                return;

            var isFirstReveal = !_hasRevealed;
            var providerChanged = _lastAppliedProvider != result.Id;
            _lastAppliedProvider = result.Id;
            _lastRenderSignature = signature;
            _lastResult = result;
            ApplyTaskbarForeground();
            // Values restored from the previous session render dimmed until a live fetch confirms them,
            // so a boot-time snapshot never reads as current data (issue #21).
            Panel.Opacity = RestingPanelOpacity;

            var widgetName = WidgetDisplayName(result.DisplayName);
            BadgeText.Text = Abbrev(widgetName);

            if (!WidgetAppearanceSettings.ShowIcon)
            {
                BadgeGlyphBox.Visibility = Visibility.Collapsed;
                BadgeText.Visibility = Visibility.Collapsed;
            }
            else if (ProviderGlyphRenderer.TryApply(BadgeGlyph, ProviderId.Codex, Foreground))
            {
                BadgeGlyphBox.Visibility = Visibility.Visible;
                BadgeText.Visibility = Visibility.Collapsed;
            }
            else
            {
                BadgeGlyphBox.Visibility = Visibility.Collapsed;
                BadgeText.Visibility = Visibility.Visible;
            }

            if (result.IsPending && result.Fetch is null)
            {
                // No fetch has completed yet (first paint after boot). Show a neutral placeholder rather
                // than the failure rendering — a full red bar with "!" reads as invalid data (issue #21).
                _rows = new()
                {
                    new WidgetUsageRow(CompactLabel(result.Provider?.SessionLabel ?? "Session"), 0, "--", HasBar: false),
                    new WidgetUsageRow(CompactLabel(result.Provider?.WeeklyLabel ?? "Weekly"), 0, "--", HasBar: false),
                };
                RenderRows();
                AnimateRender(isFirstReveal, providerSwitch: providerChanged);
                ToolTipService.SetToolTip(this, $"{widgetName}: {result.Error ?? "Loading..."}");
                return;
            }

            if (!result.Ok || result.Fetch is null)
            {
                _rows = new()
                {
                    new WidgetUsageRow(CompactLabel(result.Provider?.SessionLabel ?? "Session"), 0, "--"),
                    new WidgetUsageRow(CompactLabel(result.Provider?.WeeklyLabel ?? "Weekly"), 100, "!"),
                };
                RenderRows();
                AnimateRender(isFirstReveal, providerSwitch: providerChanged);
                ToolTipService.SetToolTip(this, $"{widgetName}: {result.Error ?? "Unavailable"}");
                return;
            }

            var usage = result.Fetch.Usage;
            _rows = BuildRows(result, usage);
            if (_rows.Count == 0)
            {
                SetActiveToolVisible(false);
                return;
            }
            RenderRows();
            SetBars();
            AnimateRender(isFirstReveal, providerSwitch: providerChanged);

            var tooltipLines = _rows.Select(FormatTooltipLine);
            var plan = FormatPlanLabel(result.Id, widgetName, usage.LoginMethod);
            var resetCreditsTooltip = WidgetResetCreditsTooltipLine(usage.ResetCredits);
            var staleTooltip = StaleTooltipLine(result);
            ToolTipService.SetToolTip(this,
                string.IsNullOrEmpty(plan)
                    ? $"{WidgetTooltipTitle(widgetName)}\n{string.Join("\n", tooltipLines)}{resetCreditsTooltip}{staleTooltip}"
                    : $"{WidgetTooltipTitle(widgetName)} · {plan}\n{string.Join("\n", tooltipLines)}{resetCreditsTooltip}{staleTooltip}");
        }

        public void SetActiveToolVisible(bool isVisible)
        {
            if (_isActiveToolVisible == isVisible)
                return;

            _isActiveToolVisible = isVisible;
            IsHitTestVisible = isVisible;
            if (isVisible)
            {
                Visibility = Visibility.Visible;
                if (!_hasRevealed)
                {
                    if (_lastResult is { } pending)
                        Apply(pending, force: true);
                    return;
                }

                AnimateVisibility(toOpacity: 1, toOffset: 0, milliseconds: 300);
                return;
            }

            AnimateVisibility(toOpacity: 0, toOffset: 6, milliseconds: 460);
        }

        /// <summary>The Codex tile's row set, built unconditionally from the usage snapshot.</summary>
        private static List<WidgetUsageRow> BuildRows(UsageResult result, UsageSnapshot usage)
        {
            var rows = new List<WidgetUsageRow>();
            // Session meter. Skipped when the provider reported no session window at all (Codex
            // org/Business plans that only expose credits): otherwise the widget shows a bogus
            // "Session 0%". Honor the window's Label override.
            if (usage.HasPrimaryWindow)
            {
                var primaryLabel = usage.Primary.Label ?? result.Provider?.SessionLabel ?? "Session";
                // Spend-limit meter: show the money value "$9.27/$100.00" instead of a bare percent so it
                // matches Codex's "used/limit credits". The bar tracks remaining %.
                string primaryValue = usage.Primary.ShowCostValue && usage.Cost is { } spend
                    ? FormatSpendValue(spend)
                    : FormatRemainingPercent(usage.Primary.UsedPercent);
                rows.Add(new WidgetUsageRow(
                    CompactLabel(primaryLabel),
                    RemainingPercent(usage.Primary.UsedPercent),
                    primaryValue,
                    usage.Primary.ResetDescription,
                    ResetAt: usage.Primary.ResetAt));
            }
            // Weekly meter.
            if (usage.Secondary != null)
            {
                var secondaryLabel = result.Provider?.WeeklyLabel ?? "Weekly";
                rows.Add(new WidgetUsageRow(
                    CompactLabel(secondaryLabel),
                    RemainingPercent(usage.Secondary.UsedPercent),
                    FormatRemainingPercent(usage.Secondary.UsedPercent),
                    usage.Secondary.ResetDescription,
                    ResetAt: usage.Secondary.ResetAt));
            }
            if (usage.ModelSpecific != null)
            {
                rows.Add(new WidgetUsageRow(
                    CompactLabel(usage.ModelSpecific.Label ?? "Model"),
                    RemainingPercent(usage.ModelSpecific.UsedPercent),
                    FormatRemainingPercent(usage.ModelSpecific.UsedPercent),
                    usage.ModelSpecific.ResetDescription,
                    ResetAt: usage.ModelSpecific.ResetAt));
            }
            if (usage.Monthly != null)
            {
                rows.Add(new WidgetUsageRow(
                    "Monthly",
                    RemainingPercent(usage.Monthly.UsedPercent),
                    FormatRemainingPercent(usage.Monthly.UsedPercent),
                    usage.Monthly.ResetDescription,
                    ResetAt: usage.Monthly.ResetAt));
            }
            // Codex credits (raw balance, or used/limit when the API reports a real cap). Lets org/
            // Business plans surface credits in the widget — the only meter they have.
            if (usage.Cost is { } cost)
            {
                if (cost.Limit is { } creditLimit && creditLimit > 0)
                {
                    double creditUsed = Math.Max(0, creditLimit - cost.Amount);
                    rows.Add(new WidgetUsageRow(
                        "Credits",
                        RemainingPercent(Math.Clamp(creditUsed / creditLimit * 100, 0, 100)),
                        cost.Display));
                }
                else
                {
                    rows.Add(new WidgetUsageRow("Credits", 0, cost.Display, HasBar: false));
                }
            }
            if (usage.ResetCredits is { AvailableCount: > 0 } resetCredits)
            {
                rows.Add(new WidgetUsageRow(
                    "Resets",
                    0,
                    resetCredits.AvailableCount.ToString("N0", CultureInfo.InvariantCulture),
                    CountdownFormat.Format(resetCredits.EarliestExpiresAt),
                    HasBar: false,
                    ResetAt: resetCredits.EarliestExpiresAt));
            }
            rows.AddRange(usage.ExtraRateWindows.Select(w => new WidgetUsageRow(
                CompactLabel(w.Title),
                RemainingPercent(w.Window.UsedPercent),
                FormatRemainingPercent(w.Window.UsedPercent),
                w.Window.ResetDescription,
                ResetAt: w.Window.ResetAt)));
            return rows;
        }

        internal static IReadOnlyList<string> BuildRowLabelsForTesting(UsageResult result, UsageSnapshot usage)
            => BuildRows(result, usage).Select(row => row.Label).ToList();

        /// <summary>Rows assumed for a provider whose usage has not been fetched yet.</summary>
        public const int AssumedRowCount = 2;

        /// <summary>How many rows the Codex tile would render for the given result.</summary>
        public static int CountRenderedRows(UsageResult result)
        {
            if (!result.Ok || result.Fetch is not { } fetch)
                return AssumedRowCount;

            return Math.Max(1, BuildRows(result, fetch.Usage).Count);
        }

        /// <summary>
        /// Width reserve for a "used/limit" credits value, so the column doesn't twitch as the used side
        /// grows. Sized from the tile's OWN limit — the used side can never be wider than the limit — rather
        /// than from a fixed worst case: reserving room for "10,000/10,000" on a plan whose limit is 300
        /// padded the tile by tens of pixels of permanent dead space, which on a crowded taskbar was enough
        /// to cost the provider its tile entirely.
        /// </summary>
        internal static string CreditValueSample(string value)
        {
            int slash = value.IndexOf('/');
            if (slash <= 0 || slash == value.Length - 1)
                return value;

            var limit = value[(slash + 1)..];
            return new string('0', limit.Length) + "/" + limit;
        }

        private static string FormatCreditCount(double value)
            => value.ToString(value % 1 == 0 ? "N0" : "N1", CultureInfo.InvariantCulture);

        /// <summary>Compact "used / limit" money string for a spend-limit meter, e.g. "$9.27/$100".
        /// Space-free to fit the widget's narrow value column.</summary>
        private static string FormatSpendValue(CostSnapshot cost)
        {
            string Money(double v)
            {
                string n = v.ToString(v % 1 == 0 ? "N0" : "N2", CultureInfo.InvariantCulture);
                return string.Equals(cost.Currency, "USD", StringComparison.OrdinalIgnoreCase) ? $"${n}" : $"{n} {cost.Currency}";
            }
            return cost.Limit is { } limit ? $"{Money(cost.Amount)}/{Money(limit)}" : Money(cost.Amount);
        }

        private static string WidgetResetCreditsTooltipLine(ResetCreditsSnapshot? resetCredits)
        {
            if (resetCredits is null)
                return string.Empty;

            var lines = new List<string>
            {
                $"Reset credits: {resetCredits.AvailableCount.ToString("N0", CultureInfo.InvariantCulture)} available",
            };

            int shown = 0;
            for (int i = 0; i < resetCredits.Credits.Count && shown < 3; i++)
            {
                var credit = resetCredits.Credits[i];
                string granted = FormatLocalDateTime(credit.GrantedAt);
                string expires = FormatLocalDateTime(credit.ExpiresAt);
                lines.Add($"Reset {shown + 1}: granted {granted}, expires {expires}");
                shown++;
            }

            if (resetCredits.Credits.Count > shown)
                lines.Add($"+{resetCredits.Credits.Count - shown} more reset credits");

            return "\n" + string.Join("\n", lines);
        }

        /// <summary>Names the age of a snapshot restored from the previous session; empty when live.</summary>
        private static string StaleTooltipLine(UsageResult result)
            => result.IsStale && result.Fetch is { } fetch
                ? $"\nLast updated {fetch.FetchedAt.ToLocalTime():t} — refreshing…"
                : string.Empty;

        private static string BuildRenderSignature(UsageResult result)
        {
            var parts = new List<string>
            {
                result.Id.ToString(),
                result.DisplayName,
                result.Error ?? string.Empty,
                result.IsPending ? "pending" : "settled",
                result.IsStale ? "stale" : "live",
                // The tile's colors follow the taskbar theme (SystemUsesLightTheme), so a theme flip
                // must invalidate an otherwise-identical render (usage data often stays unchanged
                // between polls; without this the next poll would no-op and the tile would keep its
                // launch-time foreground forever).
                Interop.SystemInfos.IsSystemLightThemeUsed() == true ? "light" : "dark",
            };

            if (result.Fetch is not { } fetch)
                return string.Join("|", parts);

            var usage = fetch.Usage;
            parts.Add(fetch.SourceLabel);
            parts.Add(usage.LoginMethod ?? string.Empty);
            parts.Add(usage.Email ?? string.Empty);
            parts.Add(usage.Cost?.Display ?? string.Empty);
            if (usage.AdditionalUsage is { Enabled: true } additional)
            {
                parts.Add(additional.SpentUsd.ToString(CultureInfo.InvariantCulture));
                parts.Add(additional.BudgetUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty);
                parts.Add(additional.IsCredits ? "credits" : "usd");
            }
            if (usage.ResetCredits is { } resetCredits)
            {
                parts.Add(resetCredits.AvailableCount.ToString(CultureInfo.InvariantCulture));
                foreach (var credit in resetCredits.Credits)
                {
                    parts.Add(credit.Status);
                    parts.Add(credit.GrantedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                    parts.Add(credit.ExpiresAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
                }
            }
            AppendRateWindow(parts, usage.Primary);
            AppendRateWindow(parts, usage.Secondary);
            AppendRateWindow(parts, usage.ModelSpecific);
            AppendRateWindow(parts, usage.Monthly);
            foreach (var extra in usage.ExtraRateWindows)
            {
                parts.Add(extra.Id);
                parts.Add(extra.Title);
                AppendRateWindow(parts, extra.Window);
            }

            return string.Join("|", parts);
        }

        private static void AppendRateWindow(List<string> parts, RateWindow? window)
        {
            if (window is null)
            {
                parts.Add("null");
                return;
            }

            parts.Add(window.UsedPercent.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
            parts.Add(window.ResetDescription ?? string.Empty);
        }

        private static string FormatPlanLabel(ProviderId id, string displayName, string? loginMethod)
            => PlanDisplayNames.ForTitle(id, displayName, loginMethod);

        private static string FormatTooltipLine(WidgetUsageRow row)
        {
            string resetDisplay = row.ResetDescription ?? string.Empty;
            bool imminent = ResetDateDisplay.IsImminent(row.ResetAt, DateTimeOffset.UtcNow);

            if (resetDisplay.Length == 0 && !imminent)
                return $"{row.Label}: {row.Value}";

            string? dateForm = imminent && row.ResetAt is { } resetWhen ? ResetDateDisplay.FormatLocalDate(resetWhen) : null;

            if (row.Label == "Resets")
            {
                string expiry = dateForm is not null
                    ? $"oldest expires {dateForm}"
                    : resetDisplay == "now"
                        ? "oldest expires now"
                        : $"oldest expires in {resetDisplay}";
                return $"{row.Label}: {row.Value} - {expiry}";
            }

            string reset = dateForm is not null
                ? $"resets {dateForm}"
                : resetDisplay == "now"
                    ? "resets now"
                    : $"resets in {resetDisplay}";
            return $"{row.Label}: {row.Value} - {reset}";
        }

        private static string WidgetTooltipTitle(string widgetName) => widgetName;

        private static string BaseLabelText(WidgetUsageRow row) => row.Label;

        private static double MeasureTextWidth(string text, int fontSize = WidgetFontSize)
        {
            var textBlock = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Segoe UI Variable Text"),
                FontSize = fontSize,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            };
            textBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            return Math.Ceiling(textBlock.DesiredSize.Width);
        }

        private static string FormatLocalDateTime(DateTimeOffset? timestamp)
        {
            if (timestamp is not DateTimeOffset value)
                return "unknown";

            var local = value.ToLocalTime();
            return $"{local:MMM d h:mm tt}";
        }

        private static Brush ResetBrush(string resetDescription)
        {
            string key = TryParseResetMinutes(resetDescription) switch
            {
                <= 30 => "AccentFillColorDefaultBrush",
                <= 120 => "AccentFillColorSecondaryBrush",
                _ => "TextFillColorSecondaryBrush",
            };
            return (Brush)Application.Current.Resources[key];
        }

        private static int? TryParseResetMinutes(string resetDescription)
        {
            if (resetDescription == "now")
                return 0;

            int total = 0;
            foreach (var part in resetDescription.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Length < 2 || !int.TryParse(part[..^1], out int value))
                    return null;

                total += part[^1] switch
                {
                    'd' => value * 24 * 60,
                    'h' => value * 60,
                    'm' => value,
                    _ => 0,
                };
            }

            return total;
        }

        private static string CompactLabel(string label)
        {
            label = label.Trim();
            return label switch
            {
                "Total usage" => "Total",
                "Auto + Composer Usage" => "Auto+Composer",
                "API Usage" => "API",
                "Session" => "Session",
                "Spark Session" => "Spark Session",
                _ when label.Contains("claude", StringComparison.OrdinalIgnoreCase) => "Claude",
                _ when label.Contains("gemini", StringComparison.OrdinalIgnoreCase) && label.Contains("flash", StringComparison.OrdinalIgnoreCase) => "Gemini Flash",
                _ when label.Contains("gemini", StringComparison.OrdinalIgnoreCase) && label.Contains("pro", StringComparison.OrdinalIgnoreCase) => "Gemini Pro",
                _ when label.Contains("github copilot", StringComparison.OrdinalIgnoreCase) => "Copilot",
                _ => label.Length > 12 ? label[..12] : label,
            };
        }

        private sealed record WidgetUsageRow(
            string Label,
            double Percent,
            string Value,
            string? ResetDescription = null,
            bool HasBar = true,
            string? GlyphData = null,
            DateTimeOffset? ResetAt = null);

        private sealed record RenderedRow(
            WidgetUsageRow Source,
            Border Track,
            Border Bar,
            double BarWidth,
            FrameworkElement? Label,
            TextBlock Value,
            IReadOnlyList<Border> Markers);

        private sealed record WidgetLayoutMetrics(double LabelWidth, double ResetWidth, double ValueWidth);

        /// <summary>
        /// Slides this tile into its new position from <paramref name="fromOffsetX"/> logical px away.
        ///
        /// The tiles occupy fixed slots and providers are re-assigned between them, so a re-order is really
        /// a content swap. Starting each tile at the offset where its provider used to sit and easing that
        /// back to zero turns the swap into what the eye expects: the existing tiles travel sideways and
        /// the newcomer arrives from the edge.
        /// </summary>
        public void AnimateSlide(double fromOffsetX)
        {
            _slideStoryboard?.Stop();

            // The RESTING value is written before starting, and the animation supplies the offset through
            // its From. Storyboard.Stop reverts a property to its local value, so a slide interrupted by the
            // next layout pass — which happens constantly, the layout is recomputed on every usage publish —
            // lands at zero instead of stranding the tile at the offset it started from.
            RootTranslate.X = 0;

            // Storyboard and animation are built once and re-aimed, not rebuilt. These run on every layout
            // pass across every tile, and a fresh Storyboard + DoubleAnimation + CubicEase per pass was
            // steady garbage for the life of the process.
            if (_slideStoryboard is null)
            {
                _slideAnimation = CreateDoubleAnimation(RootTranslate, "X", fromOffsetX, 0, SlideMilliseconds);
                _slideStoryboard = new Storyboard();
                _slideStoryboard.Children.Add(_slideAnimation);
            }

            _slideAnimation!.From = fromOffsetX;
            _slideStoryboard.Begin();
        }

        /// <summary>
        /// Whether a render that skips the cross-fade still has to put the tile on screen outright.
        ///
        /// True exactly when this is the tile's first render: the root ships at Opacity 0, and the reveal is
        /// the only thing that raises it. Provider seeding happens before the layout pass marks the slot
        /// visible, so consulting the pre-layout visibility flag here can leave the tile permanently
        /// transparent. The synchronous layout pass still collapses any slot that should not be shown.
        /// </summary>
        internal static bool ShouldRevealWithoutTransition(bool isFirstReveal)
            => isFirstReveal;

        private void AnimateRender(bool isFirstReveal, bool providerSwitch = false)
        {
            if (SuppressNextTransition)
            {
                SuppressNextTransition = false;
                Panel.Opacity = RestingPanelOpacity;

                // Suppressing the cross-fade must not swallow the first reveal. Root ships at Opacity 0 and
                // only the reveal raises it, so a tile seeded from the boot snapshot (which suppresses the
                // transition) used to stay fully transparent for the life of the process: measured, laid
                // out, Visible, and painting nothing. Show it outright instead — skipping the fade is the
                // whole point of the suppression, showing it is not.
                if (ShouldRevealWithoutTransition(isFirstReveal))
                {
                    Root.Opacity = 1;
                    RootTranslate.Y = 0;
                }

                _hasRevealed = true;
                return;
            }

            _hasRevealed = true;

            if (isFirstReveal)
                AnimateFirstReveal();
            else if (providerSwitch)
                AnimateProviderSwitch();
            else
                AnimateSoftRefresh();
        }

        // A provider switch rebuilds every row and usually resizes the host, so a hard content swap
        // reads as the whole widget flashing. Cross-fade the new content in (from fully hidden, not the
        // soft-refresh's partial dim) so the switch feels like a transition rather than a redraw.
        private void AnimateProviderSwitch()
        {
            Panel.Opacity = 0;
            AnimatePanelOpacity(from: 0, to: RestingPanelOpacity, milliseconds: 200);
        }

        private void AnimateFirstReveal()
        {
            Root.Opacity = 0;
            RootTranslate.Y = 4;

            AnimateVisibility(toOpacity: _isActiveToolVisible ? 1 : 0, toOffset: _isActiveToolVisible ? 0 : 4, milliseconds: 260);
        }

        private void AnimateSoftRefresh()
        {
            // A refresh replaces the row elements in-place. Dimming the entire panel here made every
            // quota poll flash, especially when providers publish a stale snapshot followed by a live
            // result a moment later. Keep the resting opacity stable; first reveal and provider switches
            // still use their dedicated transitions.
            Panel.Opacity = RestingPanelOpacity;
        }

        /// <summary>
        /// Runs the shared Panel.Opacity storyboard. Both callers fire on ordinary usage publishes, so the
        /// storyboard is built once and re-aimed rather than reallocated per refresh.
        /// </summary>
        private void AnimatePanelOpacity(double from, double to, int milliseconds)
        {
            _softRefreshStoryboard?.Stop();
            if (_softRefreshStoryboard is null)
            {
                _softRefreshAnimation = CreateDoubleAnimation(Panel, "Opacity", from, to, milliseconds);
                _softRefreshStoryboard = new Storyboard();
                _softRefreshStoryboard.Children.Add(_softRefreshAnimation);
            }

            _softRefreshAnimation!.From = from;
            _softRefreshAnimation.To = to;
            _softRefreshAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
            _softRefreshStoryboard.Begin();
        }

        /// <summary>
        /// The opacity the panel must settle at for the result currently shown. A DoubleAnimation's final
        /// To value becomes the property's resting value, so every animation that touches Panel.Opacity has
        /// to end here or it animates away the stale-snapshot dimming applied in Apply (#21).
        /// </summary>
        private double RestingPanelOpacity => _lastResult?.IsStale == true ? StaleOpacity : 1.0;

        private void AnimateVisibility(double toOpacity, double toOffset, int milliseconds)
        {
            double fromOpacity = Root.Opacity;
            double fromOffset = RootTranslate.Y;

            _visibilityStoryboard?.Stop();
            // Same rule as AnimateSlide: park the local values at the destination and let the animation
            // supply the start through From, so an interrupted transition can never leave a tile stuck
            // invisible or offset.
            Root.Opacity = toOpacity;
            RootTranslate.Y = toOffset;

            if (_visibilityStoryboard is null)
            {
                _visibilityOpacity = CreateDoubleAnimation(Root, "Opacity", fromOpacity, toOpacity, milliseconds);
                _visibilityOffset = CreateDoubleAnimation(RootTranslate, "Y", fromOffset, toOffset, milliseconds);
                _visibilityStoryboard = new Storyboard();
                _visibilityStoryboard.Children.Add(_visibilityOpacity);
                _visibilityStoryboard.Children.Add(_visibilityOffset);
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
            _visibilityOpacity!.From = fromOpacity;
            _visibilityOpacity.To = toOpacity;
            _visibilityOpacity.Duration = duration;
            _visibilityOffset!.From = fromOffset;
            _visibilityOffset.To = toOffset;
            _visibilityOffset.Duration = duration;
            _visibilityStoryboard.Begin();
        }

        private static DoubleAnimation CreateDoubleAnimation(
            DependencyObject target,
            string property,
            double from,
            double to,
            int milliseconds)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                EnableDependentAnimation = true,
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            return animation;
        }

        private void RenderRows()
        {
            ClearDynamicContent();
            ConfigureStaticColumns();

            var rows = CurrentRows();

            // Appearance toggles: ShowProgressBar drops the meter bars (percentages remain),
            // ShowIcon collapses the badge column entirely.
            bool showBars = WidgetAppearanceSettings.ShowProgressBar;
            const bool showPercentages = true;
            double barWidth = showBars ? BarWidthBarsAndPercentages : 0;

            for (int i = 0; i < rows.Count; i++)
            {
                int group = i / MaxRowsPerGroup;
                int row = i % MaxRowsPerGroup;
                int groupStart = group * MaxRowsPerGroup;
                int groupCount = Math.Min(MaxRowsPerGroup, rows.Count - groupStart);
                // Only a tile that holds ONE row overall gets the full-height treatment (the Credits
                // meter). A trailing lone row in a multi-group tile stays on the top line, level
                // with the first row of the group beside it — centring it there just reads as misaligned.
                bool isSingleRowGroup = rows.Count == 1 && groupCount == 1;
                var layout = CalculateLayoutMetrics(rows, group);
                int firstColumn = EnsureGroupColumns(group, layout);
                AddRow(rows[i], isSingleRowGroup ? 0 : row, firstColumn, showBars, showPercentages, barWidth, isSingleRowGroup);
            }

            ApplyTaskbarForeground();
            SetBars();
            DesiredLogicalWidth = CalculateDesiredWidth(rows);
            DesiredHostWidthChanged?.Invoke(DesiredLogicalWidth);
        }

        private void ClearDynamicContent()
        {
            _renderedRows.Clear();
            for (int i = Panel.Children.Count - 1; i >= 0; i--)
            {
                if (Panel.Children[i] != BadgeHost)
                    Panel.Children.RemoveAt(i);
            }

            while (Panel.ColumnDefinitions.Count > 1)
                Panel.ColumnDefinitions.RemoveAt(1);
        }

        private void ConfigureStaticColumns()
        {
            Panel.ColumnSpacing = PanelColumnSpacing;
            IconColumn.Width = new GridLength(IconColumnLogicalWidth);
            BadgeHost.Width = IconColumnLogicalWidth;
            BadgeHost.Height = IconColumnLogicalWidth;
            BadgeHost.Visibility = WidgetAppearanceSettings.ShowIcon ? Visibility.Visible : Visibility.Collapsed;
            Grid.SetColumn(BadgeHost, 0);
        }

        private int EnsureGroupColumns(int group, WidgetLayoutMetrics layout)
        {
            const int columnsPerGroup = 4;

            while (Panel.ColumnDefinitions.Count < 1 + ((group + 1) * columnsPerGroup))
            {
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.LabelWidth) });
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ResetWidth) });
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BarColumnLogicalWidth) });
                Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(layout.ValueWidth) });
            }

            return 1 + (group * columnsPerGroup);
        }

        /// <summary>The rows this tile draws — the full Codex row set, or the placeholder until the
        /// first result lands. Never copies: the measure and render paths both run on every usage publish.
        /// </summary>
        private List<WidgetUsageRow> CurrentRows() => _rows.Count > 0 ? _rows : PlaceholderRows;

        /// <summary>
        /// Total width of the tile for a given row set: the icon column, then per two-row group a label,
        /// reset and bar/value column, plus inter-column spacing and the root padding. Mirrors exactly what
        /// <see cref="ConfigureStaticColumns"/> and <see cref="EnsureGroupColumns"/> build, so a measured
        /// candidate and the rendered result can never disagree.
        ///
        /// Root.Padding is read from the live element rather than mirrored as a constant: the analytic
        /// width and the XAML have to agree exactly or a column is clipped, and a constant only agrees
        /// until someone edits the XAML.
        /// </summary>
        private int CalculateDesiredWidth(IReadOnlyList<WidgetUsageRow> rows)
        {
            const int columnsPerGroup = 4;

            double total = IconColumnLogicalWidth;
            int columnCount = 1;
            int groups = (rows.Count + MaxRowsPerGroup - 1) / MaxRowsPerGroup;

            for (int group = 0; group < groups; group++)
            {
                var layout = CalculateLayoutMetrics(rows, group);
                total += layout.LabelWidth + layout.ResetWidth;
                total += BarColumnLogicalWidth + layout.ValueWidth;
                columnCount += columnsPerGroup;
            }

            double padding = Root.Padding.Left + Root.Padding.Right + WidthSlack;
            return (int)Math.Ceiling(total + (Math.Max(0, columnCount - 1) * PanelColumnSpacing) + padding);
        }

        private static WidgetLayoutMetrics CalculateLayoutMetrics(
            IReadOnlyList<WidgetUsageRow> rows,
            int group)
        {
            int start = group * MaxRowsPerGroup;
            int count = Math.Min(MaxRowsPerGroup, rows.Count - start);
            // Single-row groups (e.g. the Credits meter) render one point larger, so
            // measure at that size — otherwise the label ("Credits") is sized too narrow and clips.
            bool isSingleRowGroup = rows.Count == 1 && count == 1;
            int labelFont = isSingleRowGroup ? WidgetFontSize + 1 : WidgetFontSize;
            double widestLabel = 0;
            double widestReset = 0;
            for (int i = 0; i < count; i++)
            {
                var row = rows[start + i];
                double iconWidth = row.GlyphData != null ? RowLabelGlyphReserve : 0;
                widestLabel = Math.Max(widestLabel, MeasureTextWidth(BaseLabelText(row), labelFont) + iconWidth);
                string resetDisplay = ResetDisplayText(row);
                if (resetDisplay.Length > 0)
                    widestReset = Math.Max(widestReset, MeasureTextWidth(resetDisplay, labelFont));
            }

            double widestValue = 0;
            for (int i = 0; i < count; i++)
            {
                var row = rows[start + i];
                widestValue = Math.Max(widestValue, MeasureTextWidth(row.Value, labelFont));
                if (row.Label == "Credits")
                    widestValue = Math.Max(widestValue, MeasureTextWidth(CreditValueSample(row.Value), labelFont));
                // Reserve room for a large dollar balance so amounts like "$1,000.00" aren't clipped.
                if (row.Label == "Balance")
                    widestValue = Math.Max(widestValue, MeasureTextWidth("$1,000.00", labelFont));
            }

            return new WidgetLayoutMetrics(
                Math.Max(MinLabelColumnWidth, widestLabel + 3),
                widestReset == 0 ? MinResetColumnWidth : widestReset + 2,
                Math.Max(ValueColumnWidth, widestValue + 4));
        }

        private void AddRow(
            WidgetUsageRow usageRow,
            int row,
            int firstColumn,
            bool showBars,
            bool showPercentages,
            double barWidth,
            bool isSingleRowGroup)
        {
            int rowSpan = isSingleRowGroup ? MaxRowsPerGroup : 1;
            int textSize = isSingleRowGroup ? WidgetFontSize + 1 : WidgetFontSize;
            // Bar rows put the value in its own column; text-only rows (and every row when bars are
            // hidden) let the value span the bar+value columns. Both must land in the same place or the
            // percents drift out of line with e.g. the Resets count once the bar column collapses.
            bool showBar = usageRow.HasBar && showBars;
            bool compactTextOnlyValue = !showBar;
            var value = CreateText(
                usageRow.Value,
                0.86,
                compactTextOnlyValue ? TextAlignment.Left : TextAlignment.Center,
                textSize);
            // Optional urgency coloring of the remaining percent (default white, amber at or below
            // the configured upper threshold, red at or below the lower threshold).
            if (WidgetAppearanceSettings.ColorCodeText && usageRow.HasBar)
                value.Foreground = (Brush)Application.Current.Resources[QuotaDisplay.BrushKeyForRemaining(usageRow.Percent)];
            var reset = CreateResetText(usageRow, textSize);

            FrameworkElement label;
            if (usageRow.GlyphData != null)
            {
                var icon = CreateNormalizedGlyph(usageRow.GlyphData, RowLabelGlyphSize, Foreground, new Thickness(0, 0, 4, 0));
                var labelText = CreateLabelText(usageRow, textSize);
                var sp = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children = { icon, labelText },
                };
                label = sp;
            }
            else
            {
                label = CreateLabelText(usageRow, textSize);
            }

            var track = new Border { CornerRadius = new CornerRadius(2), Opacity = 0.28 };
            var bar = new Border
            {
                Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                CornerRadius = new CornerRadius(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = 0,
            };
            var barHost = new Grid
            {
                Width = barWidth,
                Height = isSingleRowGroup ? SingleRowBarHeight : BarHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            barHost.Children.Add(track);
            barHost.Children.Add(bar);

            // Threshold tick marks: a 1px line at each warning boundary, positioned by pixel offset
            // since the tile bar has a fixed width. Colored from _markerBrush (theme-counter to the
            // text, see ApplyTaskbarForeground) so a theme flip recolors the existing lines too.
            var markers = new List<Border>(QuotaDisplay.WarningThresholds().Count);
            foreach (int threshold in QuotaDisplay.WarningThresholds())
            {
                var marker = new Border
                {
                    Width = 1,
                    Height = barHost.Height,
                    Background = _markerBrush,
                    Opacity = 0.45,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(threshold / 100d * barWidth - 0.5, 0, 0, 0),
                };
                markers.Add(marker);
                barHost.Children.Add(marker);
            }

            value.Visibility = showPercentages ? Visibility.Visible : Visibility.Collapsed;
            AddToPanel(label, row, firstColumn, rowSpan);
            AddToPanel(reset, row, firstColumn + 1, rowSpan);
            if (showBar)
            {
                barHost.Visibility = Visibility.Visible;
                AddToPanel(barHost, row, firstColumn + 2, rowSpan);
                AddToPanel(value, row, firstColumn + 3, rowSpan);
            }
            else
            {
                // Text-only rows span the bar column too, so the value starts at the same x as the bar
                // column's left edge. When bars are hidden that column is 0-wide, so percents and the
                // Resets count line up; when bars are shown the value sits where the bar would be.
                barHost.Visibility = Visibility.Collapsed;
                if (usageRow.HasBar)
                    AddToPanel(barHost, row, firstColumn + 2, rowSpan);
                Grid.SetColumnSpan(value, 2);
                AddToPanel(value, row, firstColumn + 2, rowSpan);
            }

            _renderedRows.Add(new RenderedRow(usageRow, track, bar, barWidth, label, value, markers));
        }

        private static FrameworkElement CreateNormalizedGlyph(
            string glyphData,
            double size,
            Brush foreground,
            Thickness margin)
        {
            var path = new Path
            {
                Data = ProviderGlyphRenderer.ParseGeometry(glyphData),
                Fill = foreground,
            };
            SetNormalizedGlyphTransform(path);

            var canvas = new Canvas { Width = GlyphViewportSize, Height = GlyphViewportSize };
            canvas.Children.Add(path);

            return new Viewbox
            {
                Width = size,
                Height = size,
                Child = canvas,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = margin,
            };
        }

        private static void SetNormalizedGlyphTransform(Path path)
        {
            var bounds = path.Data?.Bounds ?? Rect.Empty;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.RenderTransform = null;
                return;
            }

            double scale = NormalizedGlyphExtent / Math.Max(bounds.Width, bounds.Height);
            path.RenderTransform = new CompositeTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                TranslateX = (GlyphViewportSize / 2) - ((bounds.X + bounds.Width / 2) * scale),
                TranslateY = (GlyphViewportSize / 2) - ((bounds.Y + bounds.Height / 2) * scale),
            };
        }

        private static TextBlock CreateText(string text, double opacity, TextAlignment alignment, int fontSize = WidgetFontSize) => new()
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI Variable Text"),
            FontSize = fontSize,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            Opacity = opacity,
            TextAlignment = alignment,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = alignment switch
            {
                TextAlignment.Center => HorizontalAlignment.Stretch,
                TextAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
            VerticalAlignment = VerticalAlignment.Center,
        };

        private static FrameworkElement CreateLabelText(WidgetUsageRow row, int fontSize = WidgetFontSize)
        {
            var baseLabel = CreateText(BaseLabelText(row), 0.78, TextAlignment.Left, fontSize);
            baseLabel.TextTrimming = TextTrimming.None;
            return baseLabel;
        }

        private static TextBlock CreateResetText(WidgetUsageRow row, int fontSize = WidgetFontSize)
        {
            string display = ResetDisplayText(row);
            if (display.Length == 0)
                return CreateText("", 0.9, TextAlignment.Left, fontSize);

            var reset = CreateText(display, 0.9, TextAlignment.Left, fontSize);
            // A reset inside the imminent window shows its absolute date and gets the accent brush so
            // it stands out; countdowns keep the urgency-colored treatment.
            reset.Foreground = ResetDateDisplay.IsImminent(row.ResetAt, DateTimeOffset.UtcNow)
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : ResetBrush(row.ResetDescription ?? string.Empty);
            reset.TextTrimming = TextTrimming.None;
            return reset;
        }

        /// <summary>
        /// Reset text for the tile: the absolute local date when the reset is about to expire (within
        /// 24h), otherwise the countdown ("6d 23h"). Shared by rendering and width measurement so the
        /// tile never clips the date form. Parens were dropped: the imminent date is already drawn in
        /// the accent color, and the countdown reads fine bare.
        /// </summary>
        private static string ResetDisplayText(WidgetUsageRow row)
        {
            if (ResetDateDisplay.IsImminent(row.ResetAt, DateTimeOffset.UtcNow) && row.ResetAt is { } resetWhen)
                return ResetDateDisplay.FormatLocalDate(resetWhen);
            if (string.IsNullOrWhiteSpace(row.ResetDescription))
                return string.Empty;
            return row.ResetDescription;
        }

        private void AddToPanel(FrameworkElement element, int row, int column, int rowSpan = 1)
        {
            Grid.SetRow(element, row);
            Grid.SetRowSpan(element, rowSpan);
            Grid.SetColumn(element, column);
            Panel.Children.Add(element);
        }


        private void SetBars()
        {
            foreach (var row in _renderedRows)
                SetBar(row.Bar, row.Source.Percent, row.BarWidth);
        }

        private static void SetBar(FrameworkElement bar, double remainingPercent, double maxWidth)
        {
            bar.Width = Math.Clamp(remainingPercent, 0, 100) * (maxWidth / 100d);
            string key = GetRemainingBrushResourceKey(remainingPercent);
            if (bar is Border border)
            {
                bool emphasized = remainingPercent <= WidgetAppearanceSettings.WarningUpperPercent;
                border.Background = (Brush)Application.Current.Resources[key];
                border.Opacity = emphasized ? 0.95 : 0.78;
            }
        }

        private static string Abbrev(string name)
            => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();

        /// <summary>Remaining quota percent (100 − used), clamped to 0..100. Codex's UI shows remaining.</summary>
        private static double RemainingPercent(double usedPercent) => QuotaDisplay.RemainingPercent(usedPercent);

        private static string FormatRemainingPercent(double usedPercent) => $"{RemainingPercent(usedPercent):0}%";

        /// <summary>Brush key for a remaining percent: critical red at or below the configured lower
        /// threshold, caution amber at or below the upper threshold (defaults 20 and 50).</summary>
        private static string GetRemainingBrushResourceKey(double remainingPercent)
            => QuotaDisplay.BrushKeyForRemaining(remainingPercent);
    }
}
