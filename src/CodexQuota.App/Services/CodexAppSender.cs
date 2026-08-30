using System;
using System.Collections.Generic;
using System.Text;
using CodexQuota.Diagnostics;
using CodexQuota.Interop;
using global::Interop.UIAutomationClient;

namespace CodexQuota.Services;

/// <summary>
/// Presses the send button of the running Codex desktop app via UI Automation, so a prompt the user
/// already typed gets submitted without stealing focus (the button is invoked, not clicked).
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

    // Send-button locators, ranked: exact name match first, then automation-id/name substring.
    private static readonly string[] SendButtonNameCandidates = { "Send", "Send message", "Submit" };
    private static readonly string[] SendButtonIdFragments = { "send" };

    private IUIAutomation? _automation;

    public SendPromptResult TrySend(out string detail)
    {
        detail = string.Empty;
        try
        {
            IntPtr hwnd = FindCodexWindow();
            if (hwnd == IntPtr.Zero)
            {
                detail = "no Codex desktop window found";
                return SendPromptResult.NoWindow;
            }

            _automation ??= new CUIAutomation();
            var root = _automation.ElementFromHandle(hwnd);
            if (root is null)
            {
                detail = "Codex window has no automation element";
                return SendPromptResult.NoWindow;
            }

            if (Environment.GetEnvironmentVariable("CODEXQUOTA_DUMP_CODEX_UI") == "1")
                DumpTree(root);

            // Never send an empty prompt: if we can see edit/document inputs and all of them are
            // blank, bail out instead of invoking the button.
            var inputsEmpty = AreAllInputsEmpty(root);
            if (inputsEmpty == true)
            {
                detail = "chat input is empty";
                return SendPromptResult.EmptyPrompt;
            }

            var button = FindSendButton(root);
            if (button is null)
            {
                detail = "send button not found in Codex UI tree";
                return SendPromptResult.ButtonNotFound;
            }

            if (button.GetCurrentPattern(UIA_PatternIds.UIA_InvokePatternId) is not IUIAutomationInvokePattern invoke)
            {
                detail = $"send button '{SafeName(button)}' does not support Invoke";
                return SendPromptResult.Failed;
            }

            invoke.Invoke();
            Log.Information("Codex auto-send: send button invoked");
            return SendPromptResult.Sent;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Codex auto-send failed");
            detail = ex.Message;
            _automation = null;
            return SendPromptResult.Failed;
        }
    }

    internal static IntPtr FindCodexWindow()
    {
        IntPtr found = IntPtr.Zero;
        User32.EnumWindows((hwnd, _) =>
        {
            if (!User32.IsWindowVisible(hwnd))
                return true;

            User32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (!IsCodexProcess(pid))
                return true;

            var title = new StringBuilder(256);
            User32.GetWindowText(hwnd, title, title.Capacity);
            if (title.Length == 0)
                return true;

            found = hwnd;
            return false; // stop at the first visible, titled Codex window
        }, IntPtr.Zero);
        return found;
    }

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

    private bool? AreAllInputsEmpty(IUIAutomationElement root)
    {
        var condition = _automation!.CreateOrCondition(
            _automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_EditControlTypeId),
            _automation.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_DocumentControlTypeId));
        var inputs = root.FindAll(TreeScope.TreeScope_Descendants, condition);
        if (inputs is null || inputs.Length == 0)
            return null; // unknown: no input controls found, let the send attempt proceed

        bool sawInputWithContent = false;
        bool sawInputWithUnknownContent = false;
        bool sawInputWithEmptyContent = false;

        for (int i = 0; i < inputs.Length; i++)
        {
            var input = inputs.GetElement(i);
            string? text = null;

            if (input.GetCurrentPattern(UIA_PatternIds.UIA_ValuePatternId) is IUIAutomationValuePattern value)
                text = value.CurrentValue;
            else if (input.GetCurrentPattern(UIA_PatternIds.UIA_TextPatternId) is IUIAutomationTextPattern textPattern)
            {
                try { text = textPattern.DocumentRange.GetText(-1); } catch { /* fall through */ }
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

        // Any visible/readable typed content means there is a prompt to send.
        if (sawInputWithContent)
            return false;

        // All readable inputs were empty, or some were empty and none had content.
        if (!sawInputWithUnknownContent)
            return true;

        // At least one input was unreadable but none had readable content — conservative: block.
        return sawInputWithEmptyContent ? true : null;
    }

    private IUIAutomationElement? FindSendButton(IUIAutomationElement root)
    {
        var buttons = root.FindAll(
            TreeScope.TreeScope_Descendants,
            _automation!.CreatePropertyCondition(UIA_PropertyIds.UIA_ControlTypePropertyId, UIA_ControlTypeIds.UIA_ButtonControlTypeId));
        if (buttons is null || buttons.Length == 0)
            return null;

        var candidates = new List<(IUIAutomationElement Element, string Name, string AutomationId)>();
        for (int i = 0; i < buttons.Length; i++)
        {
            var button = buttons.GetElement(i);
            candidates.Add((button, SafeName(button), SafeAutomationId(button)));
        }

        foreach (var exact in SendButtonNameCandidates)
        {
            foreach (var (element, name, _) in candidates)
            {
                if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
                    return element;
            }
        }

        foreach (var fragment in SendButtonIdFragments)
        {
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

    /// <summary>Logs the Codex window's control tree (type, name, automation id) to help pin the
    /// send-button locator. Only runs when <c>CODEXQUOTA_DUMP_CODEX_UI=1</c>.</summary>
    private void DumpTree(IUIAutomationElement root)
    {
        IUIAutomationTreeWalker walker;
        try { walker = _automation!.RawViewWalker; }
        catch { walker = _automation!.ControlViewWalker; }

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
        Log.Information($"Codex UI dump summary: {lines.Count} elements logged");
    }
}
