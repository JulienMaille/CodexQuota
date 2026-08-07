namespace CodexQuota.Tests;

public class FlyoutLayoutTests
{
    [Fact]
    public void LogicalHeight_IsMinContentPlusChrome()
        => Assert.Equal(176, FlyoutLayout.LogicalHeight);

    [Fact]
    public void ComputeLogicalHeight_GrowsWithDetailContent()
        => Assert.Equal(646, FlyoutLayout.ComputeLogicalHeight(620));

    [Fact]
    public void ComputeLogicalHeight_ClampsTallContent()
        => Assert.Equal(786, FlyoutLayout.ComputeLogicalHeight(1200));

    [Fact]
    public void ComputeLogicalWidth_UsesFixedBaseWidth()
    {
        // The flyout hosts a single Codex panel, so its width is fixed at the base logical width.
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 0, detailContentWidth: 300);
        Assert.Equal(FlyoutLayout.BaseLogicalWidth, width);
    }

    [Fact]
    public void ComputeLogicalWidth_RespectsMinimumWidth()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 10, detailContentWidth: 200);
        Assert.Equal(FlyoutLayout.MinLogicalWidth, width);
    }

    [Fact]
    public void ComputeLogicalWidth_GrowsWithDetailContent()
    {
        int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 12, detailContentWidth: 900);
        Assert.Equal(900, width);
    }

    [Fact]
    public void ComputeLogicalWidth_CanForceMinimumWidthForManualTesting()
    {
        var previous = Environment.GetEnvironmentVariable(FlyoutLayout.ForceMinWidthEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(FlyoutLayout.ForceMinWidthEnvironmentVariable, "1");

            int width = FlyoutLayout.ComputeLogicalWidth(stripIconCount: 12, detailContentWidth: 900);

            Assert.Equal(FlyoutLayout.MinLogicalWidth, width);
        }
        finally
        {
            Environment.SetEnvironmentVariable(FlyoutLayout.ForceMinWidthEnvironmentVariable, previous);
        }
    }
}