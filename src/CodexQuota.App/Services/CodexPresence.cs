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
        // P2: guard with the existing gate — double-Start must not leak a second timer.
        lock (_gate)
        {
            if (_timer != null)
                return;

            _timer = new Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
        }
    }

    public void Stop()
    {
        // P2: Stop races Poll safely — Poll only touches _timer via Start/Stop under the gate;
        // the timer callback never dereferences _timer itself.
        Timer? timer;
        lock (_gate)
        {
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    private void Poll()
    {
        // P2: per-probe try/catch — a failing probe preserves the previous state instead of
        // reporting a false "down".
        bool processRunning;
        try
        {
            processRunning = UsageCoordinator.IsCodexProcessRunning();
        }
        catch
        {
            lock (_gate)
            {
                processRunning = _isRunning;
            }
        }

        bool windowFound;
        try
        {
            windowFound = CodexAppSender.FindCodexWindow() != IntPtr.Zero;
        }
        catch
        {
            lock (_gate)
            {
                windowFound = _isRunning;
            }
        }

        bool running = processRunning || windowFound;

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