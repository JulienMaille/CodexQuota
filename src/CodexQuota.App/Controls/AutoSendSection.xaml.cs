using System;
using System.Globalization;
using System.Threading.Tasks;
using CodexQuota.Diagnostics;
using CodexQuota.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexQuota.Controls;

/// <summary>
/// Self-contained auto-send settings row: arm/disarm toggle, weekly-veto option and a status line.
/// Talks to <see cref="AutoSendService.Instance"/> directly so the flyout needs no wiring of its
/// own. One-shot semantics: after any outcome the section shows the result and disarms.
/// </summary>
public sealed partial class AutoSendSection : UserControl
{
    // Checked/Unchecked handlers fire during seeding; the guard keeps that from arming/disarming.
    private bool _initializing;

    // Dry-run probe state: only drives the status line, never the arm/disarm machine.
    private bool _testing;

    public AutoSendSection()
    {
        InitializeComponent();
        ApplyLocalizedStrings();

        AutoSendService.Instance.StatusChanged += OnStatusChanged;
        Unloaded += (_, _) => AutoSendService.Instance.StatusChanged -= OnStatusChanged;

        ApplyStatus(AutoSendService.Instance.Status);
    }

    private void ApplyLocalizedStrings()
    {
        ArmCheck.Content = AppStrings.Get("AutoSend.Arm");
        ToolTipService.SetToolTip(ArmCheck, AppStrings.Get("AutoSend.ArmTooltip"));
        SkipWeeklyCheck.Content = AppStrings.Get("AutoSend.SkipWeekly");
        ToolTipService.SetToolTip(SkipWeeklyCheck, AppStrings.Get("AutoSend.SkipWeeklyTooltip"));
        TestButton.Content = AppStrings.Get("AutoSend.Test");
    }

    private void OnStatusChanged(AutoSendStatus status)
    {
        // Status publishes arrive off the UI thread. If the queue is gone (shutdown),
        // drop instead of touching UI inline (RPC_E_WRONG_THREAD).
        if (!DispatcherQueue.TryEnqueue(() => ApplyStatus(status)))
            Log.Warning("AutoSendSection status update dropped: DispatcherQueue unavailable.");
    }

    private void ApplyStatus(AutoSendStatus status)
    {
        _initializing = true;
        try
        {
            ArmCheck.IsChecked = status.State != AutoSendState.Idle;
            SkipWeeklyCheck.IsChecked = status.Mode == AutoSendMode.SkipIfWeeklyReset;
            SkipWeeklyCheck.IsEnabled = status.State != AutoSendState.Idle;

            StatusText.Text = status.State switch
            {
                AutoSendState.Armed => AppStrings.Format("AutoSend.StatusArmed", FormatInstant(status.TargetResetAt)),
                AutoSendState.Confirming => AppStrings.Format("AutoSend.StatusConfirming", FormatInstant(status.TargetResetAt)),
                _ => FormatOutcome(status),
            };

            // The probe temporarily recolors the status line; the state machine always owns the
            // secondary style so a stale probe color never leaks into armed/sent outcomes.
            StatusText.Foreground = LookupBrush("TextFillColorSecondaryBrush") ?? StatusText.Foreground;
            ToolTipService.SetToolTip(StatusText, null);
        }
        finally
        {
            _initializing = false;
        }
    }

    private static string FormatOutcome(AutoSendStatus status)
    {
        string when = status.OutcomeAt is { } at
            ? at.ToLocalTime().ToString("t", CultureInfo.CurrentUICulture)
            : string.Empty;

        return status.Outcome switch
        {
            AutoSendOutcome.Sent => AppStrings.Format("AutoSend.StatusSent", when),
            AutoSendOutcome.SkippedWeekly => AppStrings.Format("AutoSend.StatusSkippedWeekly", when),
            AutoSendOutcome.EmptyPrompt => AppStrings.Format("AutoSend.StatusEmptyPrompt", when),
            AutoSendOutcome.NoWindow => AppStrings.Format("AutoSend.StatusNoWindow", when),
            AutoSendOutcome.ButtonNotFound => AppStrings.Format("AutoSend.StatusNoButton", when),
            AutoSendOutcome.SendFailed => AppStrings.Format("AutoSend.StatusFailed", when),
            AutoSendOutcome.ResetNotObserved => AppStrings.Format("AutoSend.StatusNoReset", when),
            AutoSendOutcome.ArmRejected => AppStrings.Get("AutoSend.StatusArmRejected"),
            _ => AppStrings.Get("AutoSend.StatusIdle"),
        };
    }

    private static string FormatInstant(DateTimeOffset? instant)
        => instant is { } value
            ? value.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture)
            : "?";

    private void ArmCheck_Checked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;

        AutoSendService.Instance.Arm(CurrentMode());
        // If arming was rejected the status refresh unchecks the box again.
    }

    private void ArmCheck_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_initializing)
            return;

        AutoSendService.Instance.Disarm();
    }

    private void SkipWeeklyCheck_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing || AutoSendService.Instance.Status.State == AutoSendState.Idle)
            return;

        // Re-arm with the new mode; the existing target is kept (Arm re-reads it from the snapshot).
        AutoSendService.Instance.Arm(CurrentMode());
    }

    private AutoSendMode CurrentMode()
        => SkipWeeklyCheck.IsChecked == true ? AutoSendMode.SkipIfWeeklyReset : AutoSendMode.Always;

    /// <summary>
    /// Dry-run check that Send-button detection works, without sending anything: probes the Codex
    /// UI tree on a background thread (UIA can block), then reports into the status line. Never
    /// arms/disarms auto-send and never touches the status state machine.
    /// </summary>
    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_testing)
            return;

        _testing = true;
        TestButton.IsEnabled = false;
        TestButton.Content = AppStrings.Get("AutoSend.Testing");
        StatusText.Text = AppStrings.Get("AutoSend.Testing");
        try
        {
            var (result, detail, foundTitle) = await Task.Run(() =>
            {
                ICodexAppSender sender = new CodexAppSender();
                var probe = sender.TryDetect(out string probeDetail, out string probeTitle);
                return (probe, probeDetail, probeTitle);
            });

            ShowProbeResult(result, detail, foundTitle);
        }
        catch (Exception ex)
        {
            ShowProbeResult(SendButtonProbeResult.Failed, ex.Message, string.Empty);
        }
        finally
        {
            _testing = false;
            TestButton.Content = AppStrings.Get("AutoSend.Test");
            TestButton.IsEnabled = true;
        }
    }

    private void ShowProbeResult(SendButtonProbeResult result, string detail, string foundTitle)
    {
        if (result == SendButtonProbeResult.Found)
        {
            StatusText.Text = AppStrings.Format("AutoSend.TestFound", string.IsNullOrEmpty(foundTitle) ? "?" : foundTitle);
            StatusText.Foreground = LookupBrush("SystemFillColorSuccessBrush")
                ?? LookupBrush("AccentTextFillColorPrimaryBrush")
                ?? StatusText.Foreground;
        }
        else
        {
            string now = DateTimeOffset.Now.ToLocalTime().ToString("t", CultureInfo.CurrentUICulture);
            StatusText.Text = result switch
            {
                SendButtonProbeResult.NoWindow => AppStrings.Format("AutoSend.StatusNoWindow", now),
                SendButtonProbeResult.EmptyPrompt => AppStrings.Format("AutoSend.StatusEmptyPrompt", now),
                SendButtonProbeResult.Failed => AppStrings.Format("AutoSend.StatusFailed", now),
                _ => AppStrings.Get("AutoSend.TestNotFound"),
            };
            StatusText.Foreground = LookupBrush("TextFillColorSecondaryBrush") ?? StatusText.Foreground;
        }

        ToolTipService.SetToolTip(StatusText, string.IsNullOrEmpty(detail) ? null : detail);
        Log.Information($"Codex send-button probe: {result} ({detail})");
    }

    private static Brush? LookupBrush(string key)
    {
        try
        {
            if (Application.Current?.Resources.TryGetValue(key, out object? value) == true && value is Brush brush)
                return brush;
        }
        catch { /* theme lookup is best effort */ }
        return null;
    }
}
