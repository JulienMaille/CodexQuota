using System;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using CodexQuota.Diagnostics;
using CodexQuota.Taskbar;

namespace CodexQuota
{
    public partial class App : Application
    {
        public static DispatcherQueue? Dispatcher { get; private set; }
        public static event Action? Quitting;
        public static bool IsQuitting { get; private set; }
        internal const int TaskbarInitializationMaxAttempts = 20;
        private const int TaskbarInitializationInitialDelayMilliseconds = 1500;
        private const int TaskbarInitializationRetryDelayMilliseconds = 2500;

        private Timer? _taskbarInitializationTimer;
        private int _taskbarInitializationAttempts;
        private int _taskbarInitializationQueued;
        private int _launched;
        private DateTime _taskbarInitializationDeadline = DateTime.MaxValue;

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) =>
            {
                Log.Error(e.Exception, "Unhandled exception");
                // Let fatal, non-recoverable errors crash so they surface in crash
                // reporting instead of limping on in a corrupted state.
                e.Handled = !IsFatalException(e.Exception);
            };
        }

        private static bool IsFatalException(Exception? ex)
            => ex is OutOfMemoryException or StackOverflowException or AccessViolationException;

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            // Single-instance redirect can deliver OnLaunched more than once; keep it idempotent
            // so timers/coordinators are not started twice.
            if (Interlocked.Exchange(ref _launched, 1) != 0)
                return;
            Dispatcher = DispatcherQueue.GetForCurrentThread();

            Log.Information("CodexQuota launching");

            // One-time migrations from the pre-rename WinCheck identity. Must run before the normal
            // startup/autostart paths so legacy data and the legacy Run entry don't linger or collide.
            // Each step is isolated: a failing migration must not prevent the taskbar init below,
            // which would otherwise leave a zombie process with no widget.
            try { AppStorage.MigrateLegacyDataIfNeeded(); }
            catch (Exception ex) { Log.Warning(ex, "Legacy data migration failed"); }
            try { StartupSettingsService.MigrateLegacyStartupEntryIfNeeded(); }
            catch (Exception ex) { Log.Warning(ex, "Legacy startup migration failed"); }

            // Always-on autostart: (re)register the Run entry so the widget stays at logon.
            try { StartupSettingsService.Apply(true); }
            catch (Exception ex) { Log.Warning(ex, "Startup registration failed"); }

            try { UsageCoordinator.Instance.Start(); }
            catch (Exception ex) { Log.Warning(ex, "Usage coordinator start failed"); }
            try { Services.AutoSendService.Instance.Start(); }
            catch (Exception ex) { Log.Warning(ex, "Auto-send start failed"); }
            try { Services.CodexPresence.Instance.Start(); }
            catch (Exception ex) { Log.Warning(ex, "Codex presence start failed"); }
            try { ScheduleTaskbarInitialization(); }
            catch (Exception ex) { Log.Warning(ex, "Taskbar initialization scheduling failed"); }
        }

        /// <summary>Handles an activation that a second process redirected to this instance.
        /// The app never opens a window, so there is nothing to surface — the key instance keeps
        /// running its widget exactly as before.</summary>
        internal static void HandleRedirectedActivation(string? activationArguments)
        {
        }

        private void ScheduleTaskbarInitialization()
        {
            var old = Interlocked.Exchange(ref _taskbarInitializationTimer, null);
            try { old?.Dispose(); } catch { }
            _taskbarInitializationAttempts = 0;
            _taskbarInitializationQueued = 0;
            _taskbarInitializationDeadline = DateTime.UtcNow.AddMinutes(2);
            _taskbarInitializationTimer = new Timer(
                _ =>
                {
                    var dispatcher = Dispatcher;
                    if (dispatcher is not null)
                    {
                        if (Interlocked.Exchange(ref _taskbarInitializationQueued, 1) != 0)
                            return;

                        if (dispatcher.TryEnqueue(InitializeTaskbarManager))
                            return;

                        Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
                    }

                    var completedAttempts = Interlocked.Increment(ref _taskbarInitializationAttempts);
                    Log.Warning($"Could not enqueue taskbar manager initialization (attempt {completedAttempts}/{TaskbarInitializationMaxAttempts})");
                    if (!ShouldRetryTaskbarInitialization(completedAttempts, _taskbarInitializationDeadline))
                    {
                        Log.Warning($"Taskbar manager initialization giving up after {completedAttempts} attempt(s)");
                        StopTaskbarInitializationTimer();
                    }
                },
                null,
                TimeSpan.FromMilliseconds(TaskbarInitializationInitialDelayMilliseconds),
                TimeSpan.FromMilliseconds(TaskbarInitializationRetryDelayMilliseconds));
        }

        private void InitializeTaskbarManager()
        {
            var completedAttempts = Interlocked.Increment(ref _taskbarInitializationAttempts);

            try
            {
                var dispatcher = Dispatcher;
                if (dispatcher is null)
                {
                    Log.Warning("Taskbar manager initialization skipped because the dispatcher is unavailable");
                    if (!ShouldRetryTaskbarInitialization(completedAttempts, _taskbarInitializationDeadline))
                        StopTaskbarInitializationTimer();
                    return;
                }

                TaskBarManager.Initialize(dispatcher);
                StopTaskbarInitializationTimer();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Taskbar manager initialization failed");
                if (!ShouldRetryTaskbarInitialization(completedAttempts, _taskbarInitializationDeadline))
                    StopTaskbarInitializationTimer();
            }
            finally
            {
                Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
            }
        }

        public static void Quit()
        {
            IsQuitting = true;
            try
            {
                if (Current is App app)
                    app.StopTaskbarInitializationTimer();
            }
            catch { }
            var handlers = Quitting?.GetInvocationList();
            if (handlers is not null)
            {
                foreach (var handler in handlers)
                {
                    try { ((Action)handler)(); }
                    catch (Exception ex) { Log.Warning(ex, "Quitting handler failed"); }
                }
            }
            Current.Exit();
        }

        private void StopTaskbarInitializationTimer()
        {
            var timer = Interlocked.Exchange(ref _taskbarInitializationTimer, null);
            try { timer?.Dispose(); } catch { }
            Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
        }

        internal static bool ShouldRetryTaskbarInitialization(int completedAttempts)
            => ShouldRetryTaskbarInitialization(completedAttempts, DateTime.MaxValue);

        internal static bool ShouldRetryTaskbarInitialization(int completedAttempts, DateTime deadlineUtc)
            => completedAttempts < TaskbarInitializationMaxAttempts && DateTime.UtcNow < deadlineUtc;
    }
}
