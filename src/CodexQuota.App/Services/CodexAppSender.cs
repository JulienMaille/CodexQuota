using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CodexQuota.Diagnostics;
using CodexQuota.Interop;
using global::Interop.UIAutomationClient;

namespace CodexQuota.Services;

/// <summary>
/// Presses the send button of the running Codex desktop app via UI Automation, so a prompt the user
/// already typed gets submitted without stealing focus (the button is invoked, not clicked).
///
/// The host is an Electron/Chromium window whose accessibility tree is lazy: when the window sits in
/// the background the tree can collapse to just the top-level window + caption buttons (the Send
/// button only materializes once the window is foregrounded/realized, as seen with Inspect.exe).
/// There may also be several ChatGPT/codex processes with titled windows, so every candidate window
/// is enumerated, the exact "ChatGPT"/"Codex"-titled main window is tried first, and each window is
/// tried in turn. Only as a last resort — when no window yields Send and the tree looks collapsed
/// (a handful of caption-only buttons) — the best candidate is briefly restored/foregrounded once
/// (~800ms) to realize the tree, then the previous foreground window is restored. The fast path
/// never touches focus.
///
/// The exact control locators are app-version-dependent; set the environment variable
/// <c>CODEXQUOTA_DUMP_CODEX_UI=1</c> before launch to dump the Codex window's UI tree to the log so
/// the locator candidates below can be tightened.
/// </summary>
public sealed class CodexAppSender : ICodexAppSender
{
    // Process names that may host the Codex conversation (the desktop ChatGPT app 'ChatGPT', and the
    // 'codex' CLI, whose console window is filtered out by the visible-window + title requirement).
    private static readonly string[] ProcessNameCandidates = { "ChatGPT", "codex" };

    // Send-button locators, ranked: exact name match first, then automation-id/name fragment.
    // P1: the id fragment must not be the bare "send" substring (matches "sender", "sendgrid",
    // "transaction-send", hidden/disabled buttons). Fragments are matched on token boundaries.
    private static readonly string[] SendButtonNameCandidates = { "Send", "Send message", "Submit" };
    private static readonly string[] SendButtonIdFragments = { "send-button", "sendbutton", "send_message", "sendmessage", "composer-send", "prompt-send" };

    private const int ButtonNameCap = 20;
    private const int ForegroundRealizeDelayMs = 800;
    // P2: cap per-control text reads — the content-vs-empty decision only needs a prefix, and a
    // full GetText(-1) over a history Document can pull megabytes on every poll.
    private const int MaxInputTextChars = 4096;

    private readonly object _automationGate = new();
    private IUIAutomation? _automation;

    /// <summary>P2: the cached COM object is touched from the service thread and a
    /// <c>Task.Run</c> sender thread — serialize lazy-create/reset (cheap, small window).</summary>
    private IUIAutomation GetAutomation()
    {
        lock (_automationGate)
        {
            return _automation ??= new CUIAutomation();
        }
    }

    private void ResetAutomation()
    {
        lock (_automationGate)
        {
            _automation = null;
        }
    }

    public SendPromptResult TrySend(out string detail)
    {
        detail = string.Empty;
        try
        {
            var candidates = FindCandidateWindows();
            if (candidates.Count == 0)
            {
                detail = "no Codex desktop window found";
                return SendPromptResult.NoWindow;
            }

            _automation = GetAutomation();
            bool dumpTree = Environment.GetEnvironmentVariable("CODEXQUOTA_DUMP_CODEX_UI") == "1";

            bool sawEmptyPrompt = false;
            bool sawNonEmptyWindow = false;
            var perWindowDiagnostics = new List<string>(candidates.Count);
            IntPtr bestHwnd = IntPtr.Zero;
            string bestTitle = string.Empty;
            int bestButtonCount = int.MaxValue;
            List<string>? bestButtonNames = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var (hwnd, title) = candidates[i];
                var probe = ProbeOneWindow(hwnd, title, i, dumpTree: dumpTree && i == 0, includeInputState: false);
                perWindowDiagnostics.Add(probe.Diag);
                if (!probe.HasElement)
                    continue;

                // Never send an empty prompt: if we can see edit/document inputs and all of them are
                // blank, skip invoking from this window (but keep trying other windows that may hold
                // the real conversation with typed content). Total-unknown (null: no readable
                // composer content) blocks like empty — never sendable.
                if (probe.InputsEmpty != false)
                    sawEmptyPrompt = true;
                else
                    sawNonEmptyWindow = true;

                // P2: prefer the exact-title (i==0, post-rank) window for the collapsed-tree retry;
                // the fewest-buttons heuristic only applies to the retry path and must not prefer a
                // small dialog over the main window when counts tie or the tree is collapsed.
                if (i == 0 || probe.TotalButtons < bestButtonCount)
                {
                    bestHwnd = hwnd;
                    bestTitle = title;
                    bestButtonCount = probe.TotalButtons;
                    bestButtonNames = probe.ButtonNames;
                }

                if (probe.InputsEmpty != false)
                    continue; // empty or unreadable input on this window: never invoke here, try the next window.

                if (probe.Button is not null)
                {
                    // P1: the first bad button must not veto the remaining windows — skip with a
                    // diagnostic and keep probing. Only invoke enabled, on-screen buttons.
                    if (!IsActionable(probe.Button, out string skipReason))
                    {
                        perWindowDiagnostics.Add($"win{i} hwnd=0x{hwnd.ToInt64():X} title='{title}' skip: send button '{SafeName(probe.Button)}' not actionable ({skipReason})");
                        continue;
                    }

                    if (probe.Button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
                    {
                        perWindowDiagnostics.Add($"win{i} hwnd=0x{hwnd.ToInt64():X} title='{title}' skip: send button '{SafeName(probe.Button)}' does not support Invoke");
                        continue;
                    }

                    invoke.Invoke();
                    Log.Information("Codex auto-send: send button invoked");
                    return SendPromptResult.Sent;
                }
            }

            // Collapsed-tree fallback (once): the Chromium a11y tree stays collapsed while the window
            // is in the background (only caption buttons visible). Briefly restore/foreground the
            // exact-title main window (i==0 post-rank) once to realize the tree, then restore the
            // previous foreground. Never steals focus on the fast path above — only here. The
            // fewest-buttons tracking above is only a tiebreak signal for this retry path; the
            // exact-title window stays preferred when counts tie or the tree is collapsed.
            string retryNote = string.Empty;
            if (bestHwnd != IntPtr.Zero && TreeLooksCollapsed(bestButtonNames))
            {
                retryNote = RetryWithForegroundRealization(bestHwnd, bestTitle, dumpTree, out var retryResult, out var retryDetail);
                if (retryResult == SendPromptResult.Sent)
                    return SendPromptResult.Sent;
                if (retryResult == SendPromptResult.EmptyPrompt)
                {
                    detail = retryDetail;
                    return SendPromptResult.EmptyPrompt;
                }
                if (retryResult == SendPromptResult.Failed)
                {
                    detail = retryDetail;
                    return SendPromptResult.Failed;
                }
                // ButtonNotFound after retry falls through to the aggregate detail below.
            }

            if (sawEmptyPrompt && !sawNonEmptyWindow)
            {
                // Single-window-compatible semantics: every probed window with readable inputs was
                // empty and no window showed typed content, so report EmptyPrompt, not ButtonNotFound.
                detail = "chat input is empty";
                return SendPromptResult.EmptyPrompt;
            }

            detail = $"send button not found in Codex UI tree (windows={candidates.Count}; {string.Join("; ", perWindowDiagnostics)}{retryNote})";
            return SendPromptResult.ButtonNotFound;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex auto-send failed");
            detail = ex.Message;
            ResetAutomation();
            return SendPromptResult.Failed;
        }
    }

    /// <summary>
    /// Dry-run probe for the auto-send settings "Test" button: reuses the same candidate-window
    /// enumeration, input-emptiness guard and Send-button locator as <see cref="TrySend"/>, with
    /// the same per-window diagnostics format — but NEVER invokes the button and NEVER touches
    /// the foreground (no SetForegroundWindow/ShowWindow, no collapsed-tree realization retry;
    /// a collapsed tree is only reported in the detail string).
    /// </summary>
    public SendButtonProbeResult TryDetect(out string detail, out string foundTitle)
    {
        detail = string.Empty;
        foundTitle = string.Empty;
        try
        {
            var candidates = FindCandidateWindows();
            if (candidates.Count == 0)
            {
                detail = "no Codex desktop window found";
                return SendButtonProbeResult.NoWindow;
            }

            _automation = GetAutomation();

            bool sawEmptyPrompt = false;
            bool sawNonEmptyWindow = false;
            var perWindowDiagnostics = new List<string>(candidates.Count);
            bool bestProbed = false;
            string bestTitle = string.Empty;
            List<string>? bestButtonNames = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var (hwnd, title) = candidates[i];
                var probe = ProbeOneWindow(hwnd, title, i, dumpTree: false, includeInputState: true);
                perWindowDiagnostics.Add(probe.Diag);
                if (!probe.HasElement)
                    continue;

                // Same empty-prompt guard as a real send: report whether this window holds typed
                // content, an empty input, or inputs we cannot read (unknown blocks like empty).
                if (probe.InputsEmpty != false)
                    sawEmptyPrompt = true;
                else
                    sawNonEmptyWindow = true;

                if (i == 0)
                {
                    bestProbed = true;
                    bestTitle = title;
                    bestButtonNames = probe.ButtonNames;
                }

                if (probe.InputsEmpty != false)
                    continue; // a real send would skip this window (empty or unreadable); keep probing the others.

                if (probe.Button is not null)
                {
                    // Parity check with TrySend (which would report Failed here) — observed only,
                    // never invoked.
                    bool invokeSupported;
                    try { invokeSupported = probe.Button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is IUIAutomationInvokePattern; }
                    catch { invokeSupported = false; }

                    if (!invokeSupported)
                    {
                        detail = $"send button '{SafeName(probe.Button)}' in '{title}' does not support Invoke (windows={candidates.Count}; {string.Join("; ", perWindowDiagnostics)})";
                        return SendButtonProbeResult.Failed;
                    }

                    foundTitle = title;
                    detail = $"send button '{SafeName(probe.Button)}' found (buttons={probe.CountText}; windows={candidates.Count}; input={probe.InputState}; {string.Join("; ", perWindowDiagnostics)})";
                    return SendButtonProbeResult.Found;
                }
            }

            if (sawEmptyPrompt && !sawNonEmptyWindow)
            {
                // Matches TrySend semantics: nothing readable holds typed content, so a real send
                // would be skipped as EmptyPrompt.
                detail = $"chat input is empty (windows={candidates.Count}; {string.Join("; ", perWindowDiagnostics)})";
                return SendButtonProbeResult.EmptyPrompt;
            }

            // No foreground steal attempted here — just note when the tree looks collapsed (a
            // background Chromium window exposing only caption buttons).
            string collapsedNote = bestProbed && TreeLooksCollapsed(bestButtonNames)
                ? $"; note: tree looks collapsed on '{bestTitle}' (only caption buttons while Codex is in the background) — no foreground steal attempted"
                : string.Empty;
            detail = $"send button not found in Codex UI tree (windows={candidates.Count}; {string.Join("; ", perWindowDiagnostics)}{collapsedNote})";
            return SendButtonProbeResult.ButtonNotFound;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex send-button probe failed");
            detail = ex.Message;
            ResetAutomation();
            return SendButtonProbeResult.Failed;
        }
    }

    internal static IntPtr FindCodexWindow()
    {
        var candidates = FindCandidateWindows();
        return candidates.Count == 0 ? IntPtr.Zero : candidates[0].Hwnd;
    }

    /// <summary>Enumerates all visible, titled windows owned by a ChatGPT/codex process, ranked with
    /// the exact "ChatGPT"/"Codex"-titled main window first. Console hosts
    /// (<c>ConsoleWindowClass</c>) are excluded explicitly — a `codex` CLI console shares the
    /// process name but can never host a Send button.</summary>
    internal static List<(IntPtr Hwnd, string Title)> FindCandidateWindows()
    {
        var found = new List<(IntPtr Hwnd, string Title)>();
        User32.EnumWindows((hwnd, _) =>
        {
            if (!User32.IsWindowVisible(hwnd))
                return true;

            // P2: exclude console windows explicitly (codex CLI console shares the process name).
            try
            {
                var className = new StringBuilder(64);
                if (User32.GetClassName(hwnd, className, className.Capacity) > 0
                    && string.Equals(className.ToString(), "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // Best effort: a failed class check must not hide a candidate window.
            }

            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!IsCodexProcess(pid))
                return true;

            var title = new StringBuilder(256);
            User32.GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0)
                return true;

            found.Add((hwnd, title.ToString()));
            return true; // keep enumerating: the first window may be the wrong one
        }, IntPtr.Zero);

        found.Sort((a, b) => RankTitle(a.Title).CompareTo(RankTitle(b.Title)));
        return found;

        static int RankTitle(string title)
        {
            if (string.Equals(title, "ChatGPT", StringComparison.OrdinalIgnoreCase)
                || string.Equals(title, "Codex", StringComparison.OrdinalIgnoreCase))
                return 0;
            return 1;
        }
    }

    /// <summary>Shared per-window scan for <see cref="TrySend"/> and <see cref="TryDetect"/>:
    /// element lookup, input-emptiness guard, Send-button locator and the win{i} diagnostics line.
    /// Never invokes and never touches the foreground.</summary>
    private readonly struct WindowProbe
    {
        public bool HasElement { get; init; }
        public bool? InputsEmpty { get; init; }
        public IUIAutomationElement? Button { get; init; }
        public List<string> ButtonNames { get; init; }
        public int TotalButtons { get; init; }
        public string CountText { get; init; }
        public string InputState { get; init; }
        public string Diag { get; init; }
    }

    private WindowProbe ProbeOneWindow(IntPtr hwnd, string title, int index, bool dumpTree, bool includeInputState)
    {
        IUIAutomationElement? root;
        IUIAutomation automation;
        try
        {
            automation = GetAutomation();
        }
        catch (Exception ex)
        {
            return new WindowProbe
            {
                HasElement = false,
                ButtonNames = new List<string>(),
                TotalButtons = int.MaxValue,
                CountText = "?",
                InputState = string.Empty,
                Diag = $"win{index} hwnd=0x{hwnd.ToInt64():X} title='{title}' error='{ex.Message}'",
            };
        }

        try
        {
            root = automation.ElementFromHandle(hwnd);
        }
        catch (Exception ex)
        {
            return new WindowProbe
            {
                HasElement = false,
                ButtonNames = new List<string>(),
                TotalButtons = int.MaxValue,
                CountText = "?",
                InputState = string.Empty,
                Diag = $"win{index} hwnd=0x{hwnd.ToInt64():X} title='{title}' error='{ex.Message}'",
            };
        }

        if (root is null)
        {
            return new WindowProbe
            {
                HasElement = false,
                ButtonNames = new List<string>(),
                TotalButtons = int.MaxValue,
                CountText = "?",
                InputState = string.Empty,
                Diag = $"win{index} hwnd=0x{hwnd.ToInt64():X} title='{title}' no automation element",
            };
        }

        if (dumpTree)
            DumpTree(root);

        bool? inputsEmpty = null;
        try { inputsEmpty = AreAllInputsEmpty(root); }
        catch { inputsEmpty = null; }

        // Collect button names for diagnostics (cap ~20) and try the Send match.
        List<string> buttonNames;
        int totalButtons;
        IUIAutomationElement? button;
        try
        {
            button = FindSendButton(root, out buttonNames, out totalButtons);
        }
        catch
        {
            buttonNames = new List<string>();
            totalButtons = int.MaxValue;
            button = null;
        }

        string countText = FormatButtonCount(totalButtons);
        string inputState = InputStateText(inputsEmpty);
        return new WindowProbe
        {
            HasElement = true,
            InputsEmpty = inputsEmpty,
            Button = button,
            ButtonNames = buttonNames,
            TotalButtons = totalButtons,
            CountText = countText,
            InputState = inputState,
            Diag = FormatDiag(hwnd, title, index, countText, buttonNames, includeInputState ? inputState : null),
        };
    }

    private static string FormatDiag(IntPtr hwnd, string title, int index, string countText, List<string> buttonNames, string? inputState)
    {
        string names = buttonNames.Count == 0
            ? "none"
            : string.Join(", ", buttonNames);
        string diag = $"win{index} hwnd=0x{hwnd.ToInt64():X} title='{title}' buttons={countText} [{names}]";
        return inputState is null ? diag : $"{diag} input={inputState}";
    }

    private static string FormatButtonCount(int totalButtons)
        => totalButtons >= 0 && totalButtons != int.MaxValue ? totalButtons.ToString() : "?";

    private static string InputStateText(bool? inputsEmpty)
        => inputsEmpty == true ? "empty" : inputsEmpty == false ? "content" : "unknown";

    /// <summary>Diagnostic: logs the Codex/ChatGPT window's UI tree once. Used with the
    /// <c>CODEXQUOTA_DUMP_CODEX_UI=1</c> environment variable to pin the send-button locator.</summary>
    public static void LogUiTreeOnce(out string summary)
    {
        summary = string.Empty;
        try
        {
            IntPtr hwnd = FindCodexWindow();
            if (hwnd == IntPtr.Zero)
            {
                Log.Warning("Codex UI dump: no Codex/ChatGPT window found");
                summary = "no window";
                return;
            }

            var automation = new CUIAutomation();
            var root = automation.ElementFromHandle(hwnd);
            if (root is null)
            {
                Log.Warning("Codex UI dump: window has no automation element");
                summary = "no automation element";
                return;
            }

            new CodexAppSender { _automation = automation }.DumpTree(root);
            summary = "dumped";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex UI dump failed");
            summary = $"failed: {ex.Message}";
        }
    }

    private static bool IsCodexProcess(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            foreach (var candidate in ProcessNameCandidates)
            {
                if (string.Equals(process.ProcessName, candidate, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Process exited between EnumWindows and here.
        }

        return false;
    }

    private static bool TreeLooksCollapsed(List<string>? buttonNames)
    {
        if (buttonNames is null)
            return true;
        if (buttonNames.Count == 0)
            return true;
        if (buttonNames.Count > 5)
            return false;
        // P2: count alone is not enough — a small dialog also has <=5 buttons. Only treat the
        // tree as collapsed when every visible button looks like window chrome (caption-only
        // names), so a small dialog never triggers a focus steal.
        foreach (var name in buttonNames)
        {
            if (!IsCaptionLikeName(name))
                return false;
        }

        return true;
    }

    /// <summary>Caption/chrome button names across locales (Minimize/Maximize/Close et al).
    /// A realized Chromium composer exposes dozens of buttons, so <=5 caption-only buttons means
    /// the accessibility tree never materialized.</summary>
    private static bool IsCaptionLikeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return true; // unnamed buttons in a tiny tree read as chrome, not composer controls.
        if (name.StartsWith("id='", StringComparison.OrdinalIgnoreCase))
            return false; // automation-id-only entries came from non-caption controls.
        string n = name.Trim();
        string[] captionTokens =
        {
            "minimize", "maximize", "restore", "close",
            "minimiser", "minimizar", "reduire", "réduire", "reduzir", "minimieren", "ridurre",
            "agrandir", "maximizar", "maximieren", "ingrandire", "restaur", "fermer", "fechar",
            "schlie", "chiudi", "cerrar",
        };
        foreach (var token in captionTokens)
        {
            if (n.Contains(token, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Single-glyph caption symbols (— ▢ ✕ × ─ ━ ╳) with no other text.
        if (n.Length <= 2)
            return true;

        return false;
    }

    /// <summary>Realizes a collapsed Chromium tree: restore if minimized, foreground once,
    /// re-query the Send button a single time, then restore the previous foreground window.</summary>
    private string RetryWithForegroundRealization(
        IntPtr hwnd, string title, bool dumpTree, out SendPromptResult result, out string detail)
    {
        result = SendPromptResult.ButtonNotFound;
        detail = string.Empty;
        IntPtr previousForeground = IntPtr.Zero;
        try
        {
            previousForeground = User32.GetForegroundWindow();
        }
        catch { /* best effort */ }

        bool wasIconic = false;
        try
        {
            GetAutomation();
            try
            {
                wasIconic = User32.IsIconic(hwnd);
                if (wasIconic && !User32.ShowWindow(hwnd, User32.SW_RESTORE))
                    Log.Debug($"Codex auto-send: ShowWindow(SW_RESTORE) failed for '{title}'");
            }
            catch (Exception ex)
            {
                Log.Debug($"Codex auto-send: restore check failed for '{title}': {ex.Message}");
            }

            BringToFront(hwnd);
            Thread.Sleep(ForegroundRealizeDelayMs);

            IUIAutomationElement? root;
            try
            {
                // GetAutomation() above already ensured the cached instance; re-read under the
                // gate so a concurrent ResetAutomation() cannot null it mid-path.
                root = GetAutomation().ElementFromHandle(hwnd);
            }
            catch (Exception ex)
            {
                return $"; collapsed-tree retry on '{title}': no automation element ({ex.Message})";
            }

            if (root is null)
                return $"; collapsed-tree retry on '{title}': no automation element";

            if (dumpTree)
            {
                try { DumpTree(root); } catch { /* best effort */ }
            }

            bool? inputsEmpty = null;
            try { inputsEmpty = AreAllInputsEmpty(root); }
            catch { inputsEmpty = true; } // P0: unreadable input state blocks, never sendable.

            if (inputsEmpty != false)
            {
                result = SendPromptResult.EmptyPrompt;
                detail = "chat input is empty";
                return $"; collapsed-tree retry on '{title}': input empty";
            }

            var button = FindSendButton(root, out var buttonNames, out int totalButtons);
            string names = buttonNames.Count == 0
                ? "none"
                : string.Join(", ", buttonNames);
            string countText = totalButtons >= 0 && totalButtons != int.MaxValue ? totalButtons.ToString() : "?";
            if (button is null)
                return $"; collapsed-tree retry on '{title}': buttons={countText} [{names}]";

            // P1 mirror of the fast path: skip non-actionable buttons with a diagnostic.
            if (!IsActionable(button, out string retrySkip))
                return $"; collapsed-tree retry on '{title}': send button '{SafeName(button)}' not actionable ({retrySkip}); buttons={countText} [{names}]";

            if (button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
            {
                // P1: an Invoke-less button must not read as a hard failure — keep the
                // ButtonNotFound aggregate semantics so other windows stay eligible.
                return $"; collapsed-tree retry on '{title}': send button '{SafeName(button)}' does not support Invoke; buttons={countText} [{names}]";
            }

            invoke.Invoke();
            Log.Information("Codex auto-send: send button invoked after foreground realization");
            result = SendPromptResult.Sent;
            return $"; collapsed-tree retry on '{title}': sent";
        }
        catch (Exception ex)
        {
            return $"; collapsed-tree retry on '{title}': {ex.Message}";
        }
        finally
        {
            // Restore the previous foreground window; never leave focus stolen.
            if (previousForeground != IntPtr.Zero && previousForeground != hwnd)
            {
                try { BringToFront(previousForeground); }
                catch { /* best effort */ }
            }

            if (wasIconic)
            {
                try { User32.ShowWindowMinimized(hwnd); }
                catch { /* best effort */ }
            }
        }
    }

    private static void BringToFront(IntPtr hwnd)
    {
        try
        {
            if (User32.SetForegroundWindow(hwnd))
                return;
        }
        catch { /* fall through to AttachThreadInput trick */ }

        // Plain SetForegroundWindow fails when called from a background process; attach our input
        // queue to the foreground window's thread and retry.
        try
        {
            IntPtr foreground = User32.GetForegroundWindow();
            uint foregroundThread = foreground != IntPtr.Zero
                ? User32.GetWindowThreadProcessId(foreground, out _)
                : 0;
            uint currentThread = User32.GetCurrentThreadId();
            bool attached = false;
            try
            {
                if (foregroundThread != 0 && foregroundThread != currentThread)
                {
                    attached = User32.AttachThreadInput(currentThread, foregroundThread, true);
                }

                User32.SetForegroundWindow(hwnd);
            }
            finally
            {
                if (attached)
                {
                    try { User32.AttachThreadInput(currentThread, foregroundThread, false); }
                    catch { /* best effort */ }
                }
            }
        }
        catch { /* best effort */ }
    }

    private bool? AreAllInputsEmpty(IUIAutomationElement root)
    {
        // P2: snapshot the cached COM instance so a concurrent ResetAutomation() cannot null it.
        var automation = GetAutomation();
        var condition = automation.CreateOrCondition(
            automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_EditControlTypeId),
            automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_DocumentControlTypeId));
        var inputs = root.FindAll(TreeScope.TreeScope_Descendants, condition);
        if (inputs is null || inputs.Length == 0)
            return true; // P0: total failure (no readable composer input) blocks, never sendable.

        bool sawComposer = false;
        bool sawInputWithContent = false;
        bool sawInputWithUnknownContent = false;
        bool sawInputWithEmptyContent = false;

        for (int i = 0; i < inputs.Length; i++)
        {
            var input = inputs.GetElement(i);

            // P0: only editable, enabled, on-screen composer controls count. Read-only history
            // Documents, disabled inputs and off-screen phantoms are ignored — their text is
            // conversation history, not a typed prompt.
            if (!IsComposerInput(input))
                continue;
            sawComposer = true;

            string? text = null;

            if (input.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern value)
            {
                try
                {
                    if (value.CurrentIsReadOnly != 0)
                        continue; // read-only: history, not the composer.
                    text = CapText(value.CurrentValue);
                }
                catch { sawInputWithUnknownContent = true; continue; }
            }
            else if (input.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern textPattern)
            {
                // P2: cap the read — the content-vs-empty decision only needs a prefix.
                try { text = CapText(textPattern.DocumentRange.GetText(MaxInputTextChars)); } catch { sawInputWithUnknownContent = true; continue; }
            }
            else
            {
                // An input whose content we simply cannot read (rich editors that expose neither
                // Value nor Text) — default to blocking so we never risk an empty send.
                sawInputWithUnknownContent = true;
                continue;
            }

            if (text is null)
            {
                // An input whose content we simply cannot read (rich editors that expose neither
                // Value nor Text) — default to blocking so we never risk an empty send.
                sawInputWithUnknownContent = true;
            }
            else if (string.IsNullOrWhiteSpace(text))
            {
                sawInputWithEmptyContent = true;
            }
            else
            {
                sawInputWithContent = true;
            }
        }

        // No composer control found (only history/disabled/offscreen inputs): total-unknown blocks.
        if (!sawComposer)
            return true;

        // Any typed composer content means there is a prompt to send.
        if (sawInputWithContent)
            return false;

        // P0: total-unknown (no readable composer content) blocks, never sendable.
        if (sawInputWithUnknownContent && !sawInputWithEmptyContent)
            return true;

        // All readable composer inputs were empty (or some empty + some unreadable): block.
        return true;
    }

    /// <summary>P0 composer filter: an input only counts toward the empty-guard when it is enabled
    /// and on-screen. Edit controls are composer candidates outright; Document controls additionally
    /// need a composer hint (name/id mentioning prompt/composer/chatbox/input) so read-only history
    /// Documents never masquerade as typed content. Unreadable state blocks (returns false).</summary>
    private static bool IsComposerInput(IUIAutomationElement input)
    {
        try
        {
            if (input.CurrentIsEnabled == 0)
                return false;
        }
        catch { return false; }

        try
        {
            if (input.CurrentIsOffscreen != 0)
                return false;
        }
        catch { return false; }

        int controlType;
        try { controlType = input.CurrentControlType; }
        catch { return false; }

        if (controlType == UIA_ControlTypeIds.UIA_EditControlTypeId)
            return true;

        if (controlType == UIA_ControlTypeIds.UIA_DocumentControlTypeId)
        {
            string name = SafeName(input);
            string automationId = SafeAutomationId(input);
            if (name.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                || name.Contains("composer", StringComparison.OrdinalIgnoreCase)
                || name.Contains("chatbox", StringComparison.OrdinalIgnoreCase)
                || name.Contains("messagebox", StringComparison.OrdinalIgnoreCase)
                || name.Contains("input", StringComparison.OrdinalIgnoreCase)
                || name.Contains("ask ", StringComparison.OrdinalIgnoreCase)
                || name.Contains("type ", StringComparison.OrdinalIgnoreCase)
                || automationId.Contains("prompt", StringComparison.OrdinalIgnoreCase)
                || automationId.Contains("composer", StringComparison.OrdinalIgnoreCase)
                || automationId.Contains("chatbox", StringComparison.OrdinalIgnoreCase)
                || automationId.Contains("messagebox", StringComparison.OrdinalIgnoreCase)
                || automationId.Contains("input", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        return false;
    }

    private IUIAutomationElement? FindSendButton(IUIAutomationElement root, out List<string> buttonNames, out int totalButtons)
    {
        buttonNames = new List<string>();
        totalButtons = 0;
        var buttons = root.FindAll(
            TreeScope.TreeScope_Descendants,
            GetAutomation().CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_ButtonControlTypeId));
        if (buttons is null || buttons.Length == 0)
            return null;

        totalButtons = buttons.Length;

        var candidates = new List<(IUIAutomationElement Element, string Name, string AutomationId)>();
        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons.GetElement(i);
            string name = SafeName(button);
            string automationId = SafeAutomationId(button);
            candidates.Add((button, name, automationId));
            if (buttonNames.Count < ButtonNameCap)
                buttonNames.Add(string.IsNullOrEmpty(name) ? $"id='{automationId}'" : name);
        }

        foreach (var exact in SendButtonNameCandidates)
        {
            // P1: prefer the composer-subtree hint when several buttons share the Send name.
            foreach (var (element, name, automationId) in candidates)
            {
                if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase)
                    && IsComposerHinted(name, automationId))
                    return element;
            }

            foreach (var (element, name, _) in candidates)
            {
                if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
                    return element;
            }
        }

        foreach (var fragment in SendButtonIdFragments)
        {
            // P1: prefer the composer-subtree hint on the fragment pass too.
            foreach (var (element, name, automationId) in candidates)
            {
                if ((automationId.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                        || name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    && IsComposerHinted(name, automationId))
                    return element;
            }

            foreach (var (element, name, automationId) in candidates)
            {
                if (automationId.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                    || name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return element;
            }
        }

        return null;
    }

    private static string SafeName(IUIAutomationElement element)
    {
        try { return element.CurrentName ?? string.Empty; } catch { return string.Empty; }
    }

    private static string SafeAutomationId(IUIAutomationElement element)
    {
        try { return element.CurrentAutomationId ?? string.Empty; } catch { return string.Empty; }
    }

    /// <summary>P2: truncate a control-text read to the decision prefix. A null/empty read stays
    /// null/empty so unknown-vs-empty semantics are preserved.</summary>
    private static string? CapText(string? text)
        => text is null || text.Length <= MaxInputTextChars ? text : text.Substring(0, MaxInputTextChars);

    /// <summary>P0/P1 guard: only enabled, on-screen buttons with a non-empty rect may be invoked.
    /// Hidden/disabled/offscreen matches (background-tree phantoms, "sender"/"sendgrid" lookalikes)
    /// are skipped with a diagnostic, never invoked and never veto the remaining windows.</summary>
    private static bool IsActionable(IUIAutomationElement element, out string reason)
    {
        reason = string.Empty;
        try
        {
            if (element.CurrentIsEnabled == 0)
            {
                reason = "disabled";
                return false;
            }
        }
        catch { reason = "IsEnabled unreadable"; return false; }

        try
        {
            if (element.CurrentIsOffscreen != 0)
            {
                reason = "offscreen";
                return false;
            }
        }
        catch { reason = "IsOffscreen unreadable"; return false; }

        try
        {
            var rect = element.CurrentBoundingRectangle;
            if (rect.right <= rect.left || rect.bottom <= rect.top)
            {
                reason = "empty rect";
                return false;
            }
        }
        catch { reason = "rect unreadable"; return false; }

        return true;
    }

    /// <summary>Composer hint: a send-button id/name tied to the composer ("composer", "prompt",
    /// "chat") is preferred over chrome lookalikes when several buttons share the Send name.</summary>
    private static bool IsComposerHinted(string name, string automationId)
        => name.Contains("composer", StringComparison.OrdinalIgnoreCase)
        || name.Contains("prompt", StringComparison.OrdinalIgnoreCase)
        || name.Contains("chat", StringComparison.OrdinalIgnoreCase)
        || automationId.Contains("composer", StringComparison.OrdinalIgnoreCase)
        || automationId.Contains("prompt", StringComparison.OrdinalIgnoreCase)
        || automationId.Contains("chat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Logs the Codex window's control tree (type, name, automation id) to help pin the
    /// send-button locator. Only runs when <c>CODEXQUOTA_DUMP_CODEX_UI=1</c>.</summary>
    private void DumpTree(IUIAutomationElement root)
    {
        IUIAutomationTreeWalker walker;
        var automation = GetAutomation();
        try { walker = automation.RawViewWalker; }
        catch { walker = automation.ControlViewWalker; }

        var lines = new List<string>();
        void Walk(IUIAutomationElement element, int depth)
        {
            if (depth > 30 || lines.Count > 2000)
                return;
            string type;
            try { type = element.CurrentLocalizedControlType ?? "?"; } catch { type = "?"; }
            lines.Add($"{new string(' ', depth * 2)}{element.CurrentControlType} name='{SafeName(element)}' id='{SafeAutomationId(element)}' (\"{type}\")");

            try
            {
                var child = walker.GetFirstChildElement(element);
                int siblings = 0;
                while (child is not null && siblings < 200)
                {
                    Walk(child, depth + 1);
                    child = walker.GetNextSiblingElement(child);
                    siblings++;
                }
            }
            catch { /* subtree not navigable -- continue up */ }
        }

        try { Walk(root, 0); } catch { /* best effort */ }
        Log.Information("Codex UI tree dump:\n" + string.Join(Environment.NewLine, lines));

        int buttonCount = -1;
        try
        {
            var buttons = root.FindAll(
                TreeScope.TreeScope_Descendants,
                GetAutomation().CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_ButtonControlTypeId));
            buttonCount = buttons?.Length ?? -1;
        }
        catch { /* best effort */ }

        Log.Information($"Codex UI dump summary: {lines.Count} elements logged"
            + (buttonCount >= 0 ? $", {buttonCount} buttons" : string.Empty));
    }
}
