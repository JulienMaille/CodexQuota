using System;
using System.Threading;
using System.Threading.Tasks;
using CodexQuota.Diagnostics;
using CodexQuota.Usage;

namespace CodexQuota.Services;

public enum AutoSendState
{
    Idle,
    /// <summary>Waiting for the armed session reset time to pass.</summary>
    Armed,
    /// <summary>The reset time passed; confirming the reset with live data before sending.</summary>
    Confirming,
}

public enum AutoSendOutcome
{
    None,
    Sent,
    SkippedWeekly,
    EmptyPrompt,
    NoWindow,
    ButtonNotFound,
    SendFailed,
    /// <summary>The reset time passed but no fresh snapshot ever showed the session reset.</summary>
    ResetNotObserved,
    /// <summary>Arm request rejected (no usage data / no session reset time).</summary>
    ArmRejected,
}

/// <summary>Immutable status snapshot for the UI.</summary>
public sealed class AutoSendStatus
{
    public AutoSendState State { get; init; }
    public AutoSendMode Mode { get; init; }
    public DateTimeOffset? TargetResetAt { get; init; }
    public AutoSendOutcome Outcome { get; init; }
    public DateTimeOffset? OutcomeAt { get; init; }
    public string? OutcomeDetail { get; init; }
}

/// <summary>One-shot timer abstraction so the service is testable without real timers.</summary>
public interface IAutoSendScheduler
{
    void Schedule(TimeSpan delay, Action callback);
    void Cancel();
}

internal sealed class TimerAutoSendScheduler : IAutoSendScheduler
{
    private readonly object _gate = new();
    private Timer? _timer;

    public void Schedule(TimeSpan delay, Action callback)
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = new Timer(_ => callback(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }
}

/// <summary>
/// One-shot auto-send: when armed, waits for the session rate-limit window to reset
/// (<see cref="RateWindow.ResetAt"/>) and then presses the Codex desktop app's send button so the
/// prompt the user already typed is submitted. A simultaneous weekly reset can veto the send
/// (<see cref="AutoSendMode.SkipIfWeeklyReset"/>). Disarms after any single outcome.
/// </summary>
public sealed class AutoSendService
{
    private static readonly TimeSpan TriggerGrace = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ConfirmRetryDelay = TimeSpan.FromSeconds(30);
    private const int MaxConfirmAttempts = 10;

    private readonly object _gate = new();
    private readonly ICodexAppSender _sender;
    private readonly IAutoSendStore _store;
    private readonly Func<UsageSnapshot?> _getSnapshot;
    private readonly Func<Task> _forceRefresh;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly IAutoSendScheduler _scheduler;

    private AutoSendState _state = AutoSendState.Idle;
    private bool _started;
    private bool _fireClaimed;
    private AutoSendMode _mode;
    private DateTimeOffset _target;
    private DateTimeOffset? _baselineWeeklyResetAt;
    private int _confirmAttempts;
    private AutoSendOutcome _outcome;
    private DateTimeOffset? _outcomeAt;
    private string? _outcomeDetail;

    public event Action<AutoSendStatus>? StatusChanged;

    public AutoSendService(
        ICodexAppSender sender,
        IAutoSendStore store,
        Func<UsageSnapshot?> getSnapshot,
        Func<Task> forceRefresh,
        Func<DateTimeOffset> utcNow,
        IAutoSendScheduler scheduler)
    {
        _sender = sender;
        _store = store;
        _getSnapshot = getSnapshot;
        _forceRefresh = forceRefresh;
        _utcNow = utcNow;
        _scheduler = scheduler;
    }

    public static AutoSendService Instance { get; } = CreateDefault();

    private static AutoSendService CreateDefault()
    {
        var coordinator = UsageCoordinator.Instance;
        return new AutoSendService(
            new CodexAppSender(),
            new AutoSendSettings(),
            getSnapshot: () => coordinator.LastState is { Ok: true } result ? result.Fetch!.Usage : null,
            forceRefresh: () => coordinator.FetchAndPublishAsync(force: true),
            utcNow: () => DateTimeOffset.UtcNow,
            scheduler: new TimerAutoSendScheduler());
    }

    /// <summary>Wires the coordinator feed and re-arms a persisted trigger after an app restart.
    /// Idempotent: repeat calls neither double-subscribe nor disturb in-memory state.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_started)
                return;
            _started = true;
        }

        UsageCoordinator.Instance.StateChanged += OnStateChanged;

        // One-off diagnostic: with CODEXQUOTA_DUMP_CODEX_UI=1, dump the Codex/ChatGPT window's UI
        // tree to the log right away so the send-button locator can be pinned/adjusted.
        if (Environment.GetEnvironmentVariable("CODEXQUOTA_DUMP_CODEX_UI") == "1")
            Task.Run(() => CodexAppSender.LogUiTreeOnce(out _));

        lock (_gate)
        {
            // P1/P5 torn-write guard: a crash between Target and Armed must never resurrect an
            // arm with no target. Ignore Armed when Target==0.
            if (!_store.Armed || _store.TargetResetAtUtcTicks == 0)
            {
                if (_store is { Armed: true })
                    _store.Armed = false;
                return;
            }

            var target = new DateTimeOffset(_store.TargetResetAtUtcTicks, TimeSpan.Zero);
            if (target == DateTimeOffset.MinValue)
            {
                _store.Armed = false;
                return;
            }

            // Never re-fire a reset we already acted on (the app restarted after firing).
            if (_store.LastFiredResetAtUtcTicks == _store.TargetResetAtUtcTicks)
            {
                _store.Armed = false;
                return;
            }

            _mode = _store.Mode;
            _target = target;
            _baselineWeeklyResetAt = _store.BaselineWeeklyResetAtUtcTicks > 0
                ? new DateTimeOffset(_store.BaselineWeeklyResetAtUtcTicks, TimeSpan.Zero)
                : null;
            _state = AutoSendState.Armed;
            _fireClaimed = false;
        }

        Log.Information($"Auto-send re-armed for session reset at {_target:O} (mode {_mode})");
        RescheduleTrigger();
        PublishStatus();
    }

    /// <summary>Detaches the coordinator feed and cancels any pending trigger. Idempotent.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (!_started)
                return;
            _started = false;
        }

        try
        {
            UsageCoordinator.Instance.StateChanged -= OnStateChanged;
        }
        catch
        {
            // Best effort: never throw out of teardown.
        }

        _scheduler.Cancel();
    }

    public AutoSendStatus Status
    {
        get
        {
            lock (_gate)
            {
                return new AutoSendStatus
                {
                    State = _state,
                    Mode = _mode,
                    TargetResetAt = _state == AutoSendState.Idle ? null : _target,
                    Outcome = _outcome,
                    OutcomeAt = _outcomeAt,
                    OutcomeDetail = _outcomeDetail,
                };
            }
        }
    }

    /// <summary>Arms for the next session reset described by the latest snapshot.</summary>
    public void Arm(AutoSendMode mode)
    {
        var snapshot = _getSnapshot();
        // A reset that is already in the past can never be "the next" reset — the snapshot is stale,
        // and arming against it would fire immediately. Reject it like a missing reset time.
        if (snapshot?.HasPrimaryWindow != true || snapshot.Primary.ResetAt is not { } resetAt
            || resetAt <= _utcNow())
        {
            // P1: a failed (re-)arm must not wipe a still-valid arm (target/store/timer stay put).
            lock (_gate)
            {
                if (_state != AutoSendState.Idle)
                {
                    Log.Warning("Auto-send re-arm rejected: keeping the existing arm (no future session reset time in the current snapshot)");
                    return;
                }
            }

            Log.Warning("Auto-send arm rejected: no future session reset time in the current snapshot");
            FinishLocked(outcome: AutoSendOutcome.ArmRejected, detail: "no session reset time available");
            return;
        }

        lock (_gate)
        {
            _mode = mode;
            _target = resetAt;
            _baselineWeeklyResetAt = snapshot.Secondary?.ResetAt;
            _confirmAttempts = 0;
            _fireClaimed = false;
            _outcome = AutoSendOutcome.None;
            _outcomeAt = null;
            _outcomeDetail = null;
            _state = AutoSendState.Armed;

            // P1 torn-write order: Target/Baseline/Mode first, Armed last, so a crash can never
            // leave Armed=true with a zero target (the store also ignores Armed when Target==0).
            _store.TargetResetAtUtcTicks = resetAt.UtcTicks;
            _store.BaselineWeeklyResetAtUtcTicks = _baselineWeeklyResetAt?.UtcTicks ?? 0;
            _store.Mode = mode;
            _store.Armed = true;
        }

        Log.Information($"Auto-send armed for session reset at {resetAt:O} (mode {mode})");
        RescheduleTrigger();
        PublishStatus();
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _state = AutoSendState.Idle;
            _fireClaimed = false; // revoke: a stale in-flight fire must stand down, never send
            _outcome = AutoSendOutcome.None;
            _outcomeAt = null;
            _outcomeDetail = null;
            _store.Armed = false;
        }

        _scheduler.Cancel();
        Log.Information("Auto-send disarmed");
        PublishStatus();
    }

    internal void OnStateChanged(UsageResult result)
    {
        // Stale/pending/error results never advance the baselines and never confirm a reset.
        if (!result.Ok || result.IsStale || result.IsPending)
            return;

        var usage = result.Fetch!.Usage;
        lock (_gate)
        {
            if (_state != AutoSendState.Armed || usage.Primary.ResetAt is not { } primaryResetAt)
                return;

            if (primaryResetAt <= _target)
            {
                // Pre-reset snapshot: the weekly baseline tracks the latest pre-reset observation so
                // a weekly reset that coincides with the trigger is visible at confirm time.
                _baselineWeeklyResetAt = usage.Secondary?.ResetAt ?? _baselineWeeklyResetAt;
                if (_baselineWeeklyResetAt is { } baseline)
                    _store.BaselineWeeklyResetAtUtcTicks = baseline.UtcTicks;

                // The server moved the target (manual reset, plan change); chase it.
                if (primaryResetAt != _target)
                    ChaseTarget(primaryResetAt);

                return;
            }

            // The reported reset moved past the armed target. That alone is NOT the reset having
            // happened: the API slides the session reset forward as traffic flows, so a poll moments
            // after arming can already report a later reset (which previously fired the trigger and
            // disarmed the feature minutes early). Only treat the armed reset as confirmed once the
            // wall clock has actually reached the target; until then chase the sliding target so the
            // eventual fire is still anchored to the real window.
            if (_utcNow() < _target)
            {
                ChaseTarget(primaryResetAt);
                return;
            }

            // P0 double-send guard: CAS Armed->Confirming under the gate so only one poll
            // wins the fire; a concurrent poll observes Confirming and stands down.
            _state = AutoSendState.Confirming;
            _fireClaimed = true;
        }

        PublishStatus();

        // A poll that arrived after the armed reset time observed it: confirm immediately, no extra fetch.
        _ = ConfirmAndFireAsync(usage);
    }

    /// <summary>Re-anchors the armed target (and its persisted copy) to a reset time the API now reports.</summary>
    private void ChaseTarget(DateTimeOffset primaryResetAt)
    {
        _target = primaryResetAt;
        _store.TargetResetAtUtcTicks = primaryResetAt.UtcTicks;
        RescheduleTrigger();
        PublishStatus();
    }

    private void RescheduleTrigger()
    {
        TimeSpan delay;
        lock (_gate)
        {
            if (_state == AutoSendState.Idle)
                return;

            delay = _target + TriggerGrace - _utcNow();
        }

        if (delay < TimeSpan.Zero)
            delay = TimeSpan.Zero;

        _scheduler.Schedule(delay, OnTriggerDue);
    }

    private void OnTriggerDue()
    {
        lock (_gate)
        {
            if (_state != AutoSendState.Armed)
                return;

            // P0 double-send guard (timer path): the Armed->Confirming claim under the gate
            // lets exactly one trigger own the fire; ConfirmAndFireAsync no-ops without it.
            _state = AutoSendState.Confirming;
            _fireClaimed = true;
        }

        PublishStatus();
        _ = ConfirmWithRefreshAsync();
    }

    private async Task ConfirmWithRefreshAsync()
    {
        UsageSnapshot? usage = null;
        try
        {
            await _forceRefresh().ConfigureAwait(false);
            usage = _getSnapshot();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-send confirmation fetch failed");
        }

        if (usage?.Primary.ResetAt is { } primaryResetAt && primaryResetAt > ReadTarget())
        {
            await ConfirmAndFireAsync(usage).ConfigureAwait(false);
            return;
        }

        lock (_gate)
        {
            if (_state != AutoSendState.Confirming)
                return;

            _confirmAttempts++;
            if (_confirmAttempts >= MaxConfirmAttempts)
            {
                FinishLocked(AutoSendOutcome.ResetNotObserved, "session reset never observed after the target time");
                return;
            }

            _state = AutoSendState.Armed; // next retry counts as a fresh trigger
            _fireClaimed = false; // release the claim so the retry trigger can claim the fire
        }

        Log.Information("Auto-send: reset not observed yet, retrying shortly");
        _scheduler.Schedule(ConfirmRetryDelay, OnTriggerDue);
        PublishStatus();
    }

    private async Task ConfirmAndFireAsync(UsageSnapshot usage)
    {
        if (usage.Primary.ResetAt is not { } primaryResetAt)
            return;

        bool skipForWeekly;
        lock (_gate)
        {
            // P0 double-send guard (winner check): only the path that CASed Armed->Confirming
            // may fire. Concurrent polls stand down (state Confirming, claim consumed) and a
            // Disarm/Finish revokes the claim, so a stale in-flight fire is a no-op.
            if (_state == AutoSendState.Idle || !_fireClaimed)
                return;
            _fireClaimed = false;

            // Weekly veto: the weekly window observed now advanced past the armed baseline, meaning
            // it reset at (or during the run-up to) this session reset.
            skipForWeekly = _mode == AutoSendMode.SkipIfWeeklyReset
                && _baselineWeeklyResetAt is { } baseline
                && usage.Secondary?.ResetAt > baseline;
        }

        if (skipForWeekly)
        {
            Log.Information("Auto-send skipped: weekly window also reset");
            FinishLocked(AutoSendOutcome.SkippedWeekly, "weekly window also reset");
            return;
        }

        // UIA against another process can block; keep it off the coordinator's thread.
        var (result, detail) = await Task.Run(() =>
        {
            var sendResult = _sender.TrySend(out string sendDetail);
            return (sendResult, sendDetail);
        }).ConfigureAwait(false);

        AutoSendOutcome outcome = result switch
        {
            SendPromptResult.Sent => AutoSendOutcome.Sent,
            SendPromptResult.EmptyPrompt => AutoSendOutcome.EmptyPrompt,
            SendPromptResult.NoWindow => AutoSendOutcome.NoWindow,
            SendPromptResult.ButtonNotFound => AutoSendOutcome.ButtonNotFound,
            _ => AutoSendOutcome.SendFailed,
        };

        Log.Information($"Auto-send finished: {outcome} ({detail})");
        FinishLocked(outcome, detail);
    }

    private DateTimeOffset ReadTarget()
    {
        lock (_gate)
        {
            return _target;
        }
    }

    /// <summary>Terminal transition for one-shot semantics: records the outcome, disarms, notifies.</summary>
    private void FinishLocked(AutoSendOutcome outcome, string? detail)
    {
        lock (_gate)
        {
            // A finish already recorded (or a clean disarm) blocks a second terminal transition; an
            // arm rejection is allowed even from the initial Idle state.
            if (_state == AutoSendState.Idle && _outcome != AutoSendOutcome.None)
                return;

            _state = AutoSendState.Idle;
            _outcome = outcome;
            _outcomeAt = _utcNow();
            _outcomeDetail = detail;
            _store.Armed = false;
            if (outcome != AutoSendOutcome.ArmRejected)
                _store.LastFiredResetAtUtcTicks = _target.UtcTicks;
        }

        _scheduler.Cancel();
        PublishStatus();
    }

    private void PublishStatus()
    {
        AutoSendStatus status;
        lock (_gate)
        {
            status = new AutoSendStatus
            {
                State = _state,
                Mode = _mode,
                TargetResetAt = _state == AutoSendState.Idle ? null : _target,
                Outcome = _outcome,
                OutcomeAt = _outcomeAt,
                OutcomeDetail = _outcomeDetail,
            };
        }

        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Auto-send status subscriber failed");
        }
    }
}
