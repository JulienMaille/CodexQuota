using System;
using System.Threading.Tasks;
using CodexQuota.Services;
using CodexQuota.Usage;
using Xunit;

namespace CodexQuota.Tests;

/// <summary>
/// Pins the one-shot auto-send state machine: arm, confirm the reset with fresh data, apply the
/// weekly veto, dedupe across a simulated restart, and never send an empty prompt.
/// </summary>
public class AutoSendServiceTests
{
    private static readonly DateTimeOffset ResetAt = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeSender : ICodexAppSender
    {
        public SendPromptResult Result = SendPromptResult.Sent;
        public string Detail = string.Empty;
        public int InvokeCount;

        public SendPromptResult TrySend(out string detail)
        {
            InvokeCount++;
            detail = Detail;
            return Result;
        }
    }

    private sealed class FakeStore : IAutoSendStore
    {
        public bool Armed { get; set; }
        public AutoSendMode Mode { get; set; }
        public long TargetResetAtUtcTicks { get; set; }
        public long BaselineWeeklyResetAtUtcTicks { get; set; }
        public long LastFiredResetAtUtcTicks { get; set; }
    }

    private sealed class ManualScheduler : IAutoSendScheduler
    {
        public TimeSpan? LastDelay;
        private Action? _pending;

        public void Schedule(TimeSpan delay, Action callback)
        {
            LastDelay = delay;
            _pending = callback;
        }

        public void Cancel() => _pending = null;

        /// <summary>Runs the pending trigger if any (and only once per firing), like a real timer.</summary>
        public void Fire()
        {
            var callback = _pending;
            _pending = null;
            callback?.Invoke();
        }
    }

    private sealed class Harness
    {
        public FakeSender Sender = new();
        public FakeStore Store = new();
        public ManualScheduler Scheduler = new();
        public UsageSnapshot? Snapshot;
        public int ForceFetchCount;
        public DateTimeOffset Now;

        public AutoSendService Create(DateTimeOffset now)
        {
            Now = now;
            return new AutoSendService(
                Sender,
                Store,
                getSnapshot: () => Snapshot,
                forceRefresh: () =>
                {
                    ForceFetchCount++;
                    return Task.CompletedTask;
                },
                utcNow: () => Now,
                scheduler: Scheduler);
        }
    }

    private static UsageSnapshot SessionSnapshot(
        DateTimeOffset sessionReset,
        DateTimeOffset? weeklyReset = null,
        double usedPercent = 0)
    {
        var snap = new UsageSnapshot(new RateWindow(usedPercent, 300, sessionReset, "reset")) { HasPrimaryWindow = true };
        if (weeklyReset is { } weekly)
            snap.WithSecondary(new RateWindow(usedPercent, 10080, weekly, "resets weekly"));

        return snap;
    }

    [Fact]
    public async Task ArmThenTriggerSendsOnce()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);
        Assert.Equal(AutoSendState.Armed, service.Status.State);
        Assert.Equal(TimeSpan.FromSeconds(110), h.Scheduler.LastDelay);

        // A mid-wait poll keeps the weekly baseline; the reset lands, then the trigger fires.
        h.Snapshot = SessionSnapshot(ResetAt, weeklyReset: ResetAt.AddHours(-6));
        service.OnStateChanged(OkResult(h.Snapshot));
        h.Snapshot = SessionSnapshot(ResetAt.AddHours(5), ResetAt.AddHours(-6));
        h.Scheduler.Fire();

        await WaitForOutcome(service, AutoSendOutcome.Sent);
        Assert.Equal(1, h.Sender.InvokeCount);
        Assert.Equal(1, h.ForceFetchCount);
        Assert.False(h.Store.Armed);
        Assert.Equal(ResetAt.UtcTicks, h.Store.LastFiredResetAtUtcTicks);
    }

    [Fact]
    public async Task PollObservingResetConfirmsWithoutForcedFetch()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);
        Assert.Equal(0, h.ForceFetchCount);

        // A poll after the reset crossfades in; no forced refresh is needed. The armed reset time
        // must have passed in wall-clock terms for the poll to count as observing the reset.
        h.Now = ResetAt.AddSeconds(30);
        h.Snapshot = SessionSnapshot(ResetAt.AddHours(5));
        service.OnStateChanged(OkResult(h.Snapshot));

        await WaitForOutcome(service, AutoSendOutcome.Sent);
        Assert.Equal(1, h.Sender.InvokeCount);
        Assert.Equal(0, h.ForceFetchCount);
    }

    [Fact]
    public async Task SlidingResetTimeDoesNotFireBeforeTheTarget()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);
        Assert.Equal(TimeSpan.FromSeconds(110), h.Scheduler.LastDelay);

        // The API slides the session reset forward as traffic flows: a mid-wait poll now reports a
        // later reset. That is not the reset having happened — stay armed, chase the new target.
        h.Snapshot = SessionSnapshot(ResetAt.AddMinutes(1));
        service.OnStateChanged(OkResult(h.Snapshot));

        Assert.Equal(AutoSendState.Armed, service.Status.State);
        Assert.Equal(ResetAt.AddMinutes(1), service.Status.TargetResetAt);
        Assert.Equal(0, h.Sender.InvokeCount);
        Assert.Equal(0, h.ForceFetchCount);

        // Once the wall clock passes the (chased) target, a poll can confirm and fire.
        h.Now = ResetAt.AddMinutes(2);
        h.Snapshot = SessionSnapshot(ResetAt.AddMinutes(1).AddHours(5));
        service.OnStateChanged(OkResult(h.Snapshot));

        await WaitForOutcome(service, AutoSendOutcome.Sent);
        Assert.Equal(1, h.Sender.InvokeCount);
        Assert.True(h.Store.Armed == false);
    }

    [Fact]
    public void SkipIfWeeklyAlsoResetAtTrigger()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        DateTimeOffset weeklyBaseline = ResetAt.AddHours(-6);
        h.Snapshot = SessionSnapshot(ResetAt, weeklyBaseline);

        service.Arm(AutoSendMode.SkipIfWeeklyReset);
        Assert.Equal(weeklyBaseline.UtcTicks, h.Store.BaselineWeeklyResetAtUtcTicks);

        // Both windows reset in the same confirmation snapshot.
        h.Snapshot = SessionSnapshot(ResetAt.AddHours(5), weeklyBaseline.AddHours(5));
        h.Scheduler.Fire();

        Assert.Equal(AutoSendOutcome.SkippedWeekly, service.Status.Outcome);
        Assert.Equal(0, h.Sender.InvokeCount);
        Assert.False(h.Store.Armed);
        Assert.Equal(ResetAt.UtcTicks, h.Store.LastFiredResetAtUtcTicks);
    }

    [Fact]
    public async Task DoesNotSkipWhenWeeklyResetHappenedLongBefore()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        // Weekly reset 6h ago and already reflected by the pre-trigger baseline poll.
        h.Snapshot = SessionSnapshot(ResetAt, weeklyReset: ResetAt.AddHours(-6));

        service.Arm(AutoSendMode.SkipIfWeeklyReset);
        h.Snapshot = SessionSnapshot(ResetAt, weeklyReset: ResetAt.AddHours(-6));
        service.OnStateChanged(OkResult(h.Snapshot)); // baseline == snapshot weekly, no new advance
        h.Snapshot = SessionSnapshot(ResetAt.AddHours(5), ResetAt.AddHours(-6));
        h.Scheduler.Fire();

        await WaitForOutcome(service, AutoSendOutcome.Sent);
        Assert.Equal(1, h.Sender.InvokeCount);
    }

    [Fact]
    public void WeeklyBaselineUndergoesAllPreResetPolls()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        DateTimeOffset weekly = ResetAt.AddHours(-6);
        h.Snapshot = SessionSnapshot(ResetAt, weekly);

        service.Arm(AutoSendMode.SkipIfWeeklyReset);

        // Several warm-up polls nudge the weekly reset a bit (server rounding) before the real one.
        weekly = weekly.AddMinutes(2);
        h.Snapshot = SessionSnapshot(ResetAt, weekly);
        service.OnStateChanged(OkResult(h.Snapshot));

        // Weekly resets with the session: baseline moved forward so the skip must trigger.
        h.Snapshot = SessionSnapshot(ResetAt.AddHours(5), weekly.AddHours(5));
        h.Scheduler.Fire();

        Assert.Equal(AutoSendOutcome.SkippedWeekly, service.Status.Outcome);
    }

    [Fact]
    public void RestartDoesNotRefireConsumedReset()
    {
        var h = new Harness();
        h.Store.Armed = true;
        h.Store.Mode = AutoSendMode.Always;
        h.Store.TargetResetAtUtcTicks = ResetAt.UtcTicks;
        h.Store.BaselineWeeklyResetAtUtcTicks = 0;
        h.Store.LastFiredResetAtUtcTicks = ResetAt.UtcTicks; // already acted on

        var service = h.Create(ResetAt.AddHours(5));
        service.Start();

        Assert.Equal(AutoSendState.Idle, service.Status.State);
        Assert.False(h.Store.Armed);
        Assert.Equal(0, h.Sender.InvokeCount);
    }

    [Fact]
    public void RestartReArmsPendingReset()
    {
        var h = new Harness();
        h.Store.Armed = true;
        h.Store.Mode = AutoSendMode.Always;
        h.Store.TargetResetAtUtcTicks = ResetAt.UtcTicks;
        h.Store.BaselineWeeklyResetAtUtcTicks = 0;
        h.Store.LastFiredResetAtUtcTicks = 0;

        var service = h.Create(ResetAt.AddHours(-5)); // now is before the target
        service.Start();

        Assert.Equal(AutoSendState.Armed, service.Status.State);
        Assert.Equal(ResetAt, service.Status.TargetResetAt);
    }

    [Fact]
    public async Task EmptyInputDoesNotTryToSend()
    {
        var h = new Harness();
        h.Sender.Result = SendPromptResult.EmptyPrompt;
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);
        h.Snapshot = SessionSnapshot(ResetAt.AddHours(5));
        h.Scheduler.Fire();

        await WaitForOutcome(service, AutoSendOutcome.EmptyPrompt);
        Assert.Equal(1, h.Sender.InvokeCount);
        Assert.False(h.Store.Armed);
    }

    [Fact]
    public async Task MissingWindowSurfacesFailureAndDisarms()
    {
        var h = new Harness();
        h.Sender.Result = SendPromptResult.NoWindow;
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);
        h.Snapshot = SessionSnapshot(ResetAt.AddHours(5));
        h.Scheduler.Fire();

        await WaitForOutcome(service, AutoSendOutcome.NoWindow);
        Assert.False(h.Store.Armed);
    }

    [Fact]
    public void UnobservedResetRetriesThenDisarms()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);

        // Confirmation fetches keep returning the pre-reset window; stays armed and retries.
        for (int i = 0; i < 5; i++)
        {
            h.Scheduler.Fire();
            Assert.Equal(AutoSendState.Armed, service.Status.State);
            Assert.Equal(TimeSpan.FromSeconds(30), h.Scheduler.LastDelay);
        }

        // The final retry exhausts the budget.
        for (int i = 0; i < 5; i++)
            h.Scheduler.Fire();
        Assert.Equal(AutoSendOutcome.ResetNotObserved, service.Status.Outcome);
        Assert.Equal(0, h.Sender.InvokeCount);
        Assert.False(h.Store.Armed);
    }

    [Fact]
    public void ArmRejectedWithoutSessionResetTime()
    {
        var h = new Harness();
        var service = h.Create(ResetAt);
        h.Snapshot = new UsageSnapshot(new RateWindow(50, 300, null, "reset")) { HasPrimaryWindow = true };

        service.Arm(AutoSendMode.Always);

        Assert.Equal(AutoSendOutcome.ArmRejected, service.Status.Outcome);
        Assert.False(h.Store.Armed);
        Assert.False(service.Status.TargetResetAt is { });
    }

    [Fact]
    public void ArmRejectedForAlreadyPastResetTime()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddHours(5));
        // The snapshot reports a reset that is already in the past (stale/restored data): arming
        // against it would fire immediately, so it must be rejected like a missing reset time.
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);

        Assert.Equal(AutoSendOutcome.ArmRejected, service.Status.Outcome);
        Assert.False(h.Store.Armed);
        Assert.Equal(0, h.Sender.InvokeCount);
    }

    [Fact]
    public void TogglingWeeklyModeReArms()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);
        service.Arm(AutoSendMode.SkipIfWeeklyReset);

        Assert.Equal(AutoSendMode.SkipIfWeeklyReset, service.Status.Mode);
        Assert.Equal(AutoSendState.Armed, service.Status.State);
    }

    [Fact]
    public void DisarmClearsOutcomeAndState()
    {
        var h = new Harness();
        var service = h.Create(ResetAt.AddSeconds(-100));
        h.Snapshot = SessionSnapshot(ResetAt);

        service.Arm(AutoSendMode.Always);
        service.Disarm();

        Assert.Equal(AutoSendState.Idle, service.Status.State);
        Assert.Equal(AutoSendOutcome.None, service.Status.Outcome);
        Assert.False(h.Store.Armed);
    }

    private static UsageResult OkResult(UsageSnapshot snapshot)
        => UsageResult.Success(ProviderId.Codex, null!, new ProviderFetchResult(snapshot, "test"));

    /// <summary>The sender path runs on a thread-pool task; wait until the terminal outcome lands.</summary>
    private static async Task WaitForOutcome(AutoSendService service, AutoSendOutcome expected)
        => await WaitUntil(() => service.Status.Outcome == expected);

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);
        Assert.True(condition(), "condition not reached within timeout");
    }
}