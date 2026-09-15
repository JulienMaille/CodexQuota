using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using CodexQuota.Diagnostics;

namespace CodexQuota
{
    /// <summary>Applies an app theme override to the shell's root element.</summary>
    public static class ThemeService
    {
        private static FrameworkElement? _root;

        public static ElementTheme Current { get; private set; } = ElementTheme.Default;

        public static void Register(FrameworkElement root)
        {
            _root = root;
            _root.RequestedTheme = Current;
        }

        public static void Apply(ElementTheme theme)
        {
            Current = theme;
            var root = _root;
            if (root is null)
                return;

            var dispatcher = App.Dispatcher;
            if (dispatcher is null)
            {
                Log.Debug("ThemeService.Apply dropped: dispatcher unavailable");
                return;
            }

            if (!dispatcher.TryEnqueue(() =>
            {
                try { root.RequestedTheme = theme; }
                catch (Exception ex) { Log.Debug($"ThemeService.Apply failed: {ex.Message}"); }
            }))
            {
                Log.Debug("ThemeService.Apply dropped: TryEnqueue failed");
            }
        }
    }
}
