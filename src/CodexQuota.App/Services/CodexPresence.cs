using System;
using System.Threading;

namespace CodexQuota.Services;

/// <summary>
/// Lightweight liveness probe for the local Codex tooling: any running <c>codex</c> process (CLI) or
/// a visible Codex/ChatGPT desktop window counts as "running". Polled on a slow timer so the widget
/// and flyout can surface "Codex isn't running" without paying a process/desktop probe on every usage
/// publish. The event has no payload when only the value changed.
/// </summary>
public sealed class CodexPresence
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private Timer? _timer;
    private bool _isRunning = true;

    public static CodexPresence Instance { get; } = new();

    private CodexPresence()
    {
    }

    /// <summary>True until the first probe has run — the app assumes Codex is present at boot so a
    /// startup race never flashes a false "down" state.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _isRunning;
            }
        }
    }

    /// <summary>Raised (on the poller thread) only when the running state actually changes.</summary>
    public event Action<bool>? Changed;

    public void Start()
    {
        if (_timer != null)
            return;

        _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    private void Poll()
    {
        bool running;
        try
        {
            running = UsageCoordinator.IsCodexProcessRunning()
                || CodexAppSender.FindCodexWindow() != IntPtr.Zero;
        }
        catch
        {
            // A failed probe must not report the app as up; the next tick retries.
            running = false;
        }

        lock (_gate)
        {
            if (_isRunning == running)
                return;
            _isRunning = running;
        }

        try
        {
            Changed?.Invoke(running);
        }
        catch
        {
            // One dead subscriber must not kill the poller.
        }
    }
}