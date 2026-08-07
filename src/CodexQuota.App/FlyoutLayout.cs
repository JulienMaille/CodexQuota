using System;

namespace CodexQuota
{
    /// <summary>
    /// Flyout dimensions. The flyout hosts a single Codex usage panel, so its width is fixed and its
    /// height adapts within a floor/ceiling band.
    /// </summary>
    internal static class FlyoutLayout
    {
        /// <summary>
        /// Fixed flyout width. Wide enough that the Codex usage panel (plan header, meter rows with
        /// bars and percents, credits and reset-credit lines) never wraps.
        /// </summary>
        public const int BaseLogicalWidth = 360;

        /// <summary>Absolute floor; kept for callers that reference a minimum.</summary>
        public const int MinLogicalWidth = BaseLogicalWidth;

        /// <summary>Smallest content height before chrome is added. The panel is a compact single-provider
        /// detail (header with inline actions + a few meter rows + credits), sized to hug its content.</summary>
        public const int MinLogicalContentHeight = 150;

        /// <summary>Largest content height before scrolling takes over.</summary>
        public const int MaxLogicalContentHeight = 760;

        /// <summary>Frame padding + scroll padding below the content. Kept lean so the footer caption
        /// sits close to the flyout's bottom edge; the appearance section floats over the content as an
        /// overlay, so it never contributes to the window height (the flyout does not grow when the
        /// settings reveal).</summary>
        public const int ChromeLogicalHeight = 18;

        public const int HeightMeasureBuffer = 8;
        public const string ForceMinWidthEnvironmentVariable = "CODEXQUOTA_FORCE_MIN_FLYOUT_WIDTH";

        public static int LogicalHeight =>
            ComputeLogicalHeight(MinLogicalContentHeight);

        public static int ComputeLogicalHeight(double detailContentHeight)
        {
            int contentHeight = (int)Math.Ceiling(detailContentHeight);
            contentHeight = Math.Clamp(contentHeight, MinLogicalContentHeight, MaxLogicalContentHeight);
            return contentHeight + ChromeLogicalHeight + HeightMeasureBuffer;
        }

        /// <summary>Flyout width is fixed: the panel is the only content.</summary>
        public static int ComputeLogicalWidth(int stripIconCount, double detailContentWidth)
        {
            if (IsForceMinWidthEnabled())
                return BaseLogicalWidth;

            int contentWidth = (int)Math.Ceiling(detailContentWidth);
            return Math.Max(contentWidth, BaseLogicalWidth);
        }

        private static bool IsForceMinWidthEnabled()
        {
            var value = Environment.GetEnvironmentVariable(ForceMinWidthEnvironmentVariable);
            return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
