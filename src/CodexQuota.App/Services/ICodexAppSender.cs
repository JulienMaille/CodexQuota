namespace CodexQuota.Services;

/// <summary>Outcome of a single attempt to press the Codex desktop app's send button.</summary>
public enum SendPromptResult
{
    /// <summary>The send button was invoked.</summary>
    Sent,
    /// <summary>No Codex desktop window could be found.</summary>
    NoWindow,
    /// <summary>The chat input was empty; nothing was sent (never fire an empty prompt).</summary>
    EmptyPrompt,
    /// <summary>The window was found but no send button was located in its UI tree.</summary>
    ButtonNotFound,
    /// <summary>The send button was found but invoking it failed.</summary>
    Failed,
}

/// <summary>Outcome of a dry-run probe for the Codex desktop app's send button. Never sends.</summary>
public enum SendButtonProbeResult
{
    /// <summary>The send button was found; nothing was invoked.</summary>
    Found,
    /// <summary>No Codex desktop window could be found.</summary>
    NoWindow,
    /// <summary>Every probed window with readable inputs was empty (a real send would be skipped).</summary>
    EmptyPrompt,
    /// <summary>The window was found but no send button was located in its UI tree.</summary>
    ButtonNotFound,
    /// <summary>The probe itself failed.</summary>
    Failed,
}

/// <summary>
/// Presses the send button of the Codex desktop app so whatever prompt the user already typed is
/// submitted. Abstracted so <see cref="AutoSendService"/> can be unit-tested without a real window.
/// </summary>
public interface ICodexAppSender
{
    SendPromptResult TrySend(out string detail);

    /// <summary>
    /// Dry-run probe: same window/input/button diagnostics as <see cref="TrySend"/> but never
    /// invokes the button and never touches the foreground. Default implementation keeps existing
    /// test fakes compiling; <see cref="CodexAppSender"/> overrides it.
    /// </summary>
    SendButtonProbeResult TryDetect(out string detail, out string foundTitle)
    {
        detail = string.Empty;
        foundTitle = string.Empty;
        return SendButtonProbeResult.Failed;
    }
}
