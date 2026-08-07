using System;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using CodexQuota.Usage;

namespace CodexQuota.Controls
{
    /// <summary>Renders provider SVG paths into a normalized <see cref="Path"/> (same approach as the taskbar widget).</summary>
    internal static class ProviderGlyphRenderer
    {
        private const double ViewportSize = 100;
        private const double NormalizedExtent = 88;

        public static bool TryApply(Path path, ProviderId providerId, Brush foreground)
        {
            if (!ProviderGlyphs.Data.TryGetValue(providerId, out var pathData)
                || ParseGeometry(pathData) is not { } glyph)
            {
                return false;
            }

            path.Data = glyph;
            path.Fill = foreground;
            ApplyTransform(path);
            return true;
        }

        public static void ApplyTransform(Path path)
        {
            var bounds = path.Data?.Bounds ?? Rect.Empty;
            if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0)
            {
                path.RenderTransform = null;
                return;
            }

            double scale = NormalizedExtent / Math.Max(bounds.Width, bounds.Height);
            path.RenderTransform = new CompositeTransform
            {
                ScaleX = scale,
                ScaleY = scale,
                TranslateX = (ViewportSize / 2) - ((bounds.X + bounds.Width / 2) * scale),
                TranslateY = (ViewportSize / 2) - ((bounds.Y + bounds.Height / 2) * scale),
            };
        }

        /// <summary>Parse provider SVG path markup into a fresh <see cref="Geometry"/>. A Geometry can have
        /// only one parent, so callers must build a new instance per element — never share. Returns null
        /// when the markup cannot be parsed.</summary>
        public static Geometry? ParseGeometry(string pathData)
        {
            if (string.IsNullOrWhiteSpace(pathData))
                return null;

            try
            {
                return (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper
                    .ConvertValue(typeof(Geometry), pathData);
            }
            catch
            {
                return null;
            }
        }

        public static double Viewport => ViewportSize;
    }
}