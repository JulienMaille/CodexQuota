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

        public App()
        {
            InitializeComponent();
            UnhandledException += (_, e) =>
            {
                Log.Error(e.Exception, "Unhandled exception");
                e.Handled = true;
            };
        }

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Dispatcher = DispatcherQueue.GetForCurrentThread();

            Log.Information("CodexQuota launching");

            // One-time migrations from the pre-rename WinCheck identity. Must run before the normal
            // startup/autostart paths so legacy data and the legacy Run entry don't linger or collide.
            AppStorage.MigrateLegacyDataIfNeeded();
            StartupSettingsService.MigrateLegacyStartupEntryIfNeeded();

            // Always-on autostart: (re)register the Run entry so the widget stays at logon.
            StartupSettingsService.Apply(true);

            UsageCoordinator.Instance.Start();
            ScheduleTaskbarInitialization();
        }

        /// <summary>Handles an activation that a second process redirected to this instance.
        /// The app never opens a window, so there is nothing to surface — the key instance keeps
        /// running its widget exactly as before.</summary>
        internal static void HandleRedirectedActivation(string? activationArguments)
        {
        }

        private void ScheduleTaskbarInitialization()
        {
            _taskbarInitializationTimer?.Dispose();
            _taskbarInitializationAttempts = 0;
            _taskbarInitializationQueued = 0;
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
                    Log.Warning("Could not enqueue taskbar manager initialization");
                    if (!ShouldRetryTaskbarInitialization(completedAttempts))
                        StopTaskbarInitializationTimer();
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
                    if (!ShouldRetryTaskbarInitialization(completedAttempts))
                        StopTaskbarInitializationTimer();
                    return;
                }

                TaskBarManager.Initialize(dispatcher);
                StopTaskbarInitializationTimer();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Taskbar manager initialization failed");
                if (!ShouldRetryTaskbarInitialization(completedAttempts))
                    StopTaskbarInitializationTimer();
            }
            finally
            {
                Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
            }
        }

        private void StopTaskbarInitializationTimer()
        {
            _taskbarInitializationTimer?.Dispose();
            _taskbarInitializationTimer = null;
            Interlocked.Exchange(ref _taskbarInitializationQueued, 0);
        }

        public static void Quit()
        {
            IsQuitting = true;
            Quitting?.Invoke();
            Current.Exit();
        }

        internal static bool ShouldRetryTaskbarInitialization(int completedAttempts)
            => completedAttempts < TaskbarInitializationMaxAttempts;
    }
}
