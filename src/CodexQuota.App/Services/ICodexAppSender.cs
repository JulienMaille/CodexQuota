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

/// <summary>
/// Presses the send button of the Codex desktop app so whatever prompt the user already typed is
/// submitted. Abstracted so <see cref="AutoSendService"/> can be unit-tested without a real window.
/// </summary>
public interface ICodexAppSender
{
    SendPromptResult TrySend(out string detail);
}
