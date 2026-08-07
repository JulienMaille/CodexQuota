using CodexQuota.Usage;

namespace CodexQuota.Tests;

public class PlanDisplayNamesTests
{
    [Theory]
    [InlineData("ChatGPT Plus", "Plus")]
    [InlineData("Codex Pro", "Pro")]
    [InlineData("OpenAI Pro-5x", "Pro-5x")]
    [InlineData("(ChatGPT Pro)", "ChatGPT Pro")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Shorten_StripsProviderPrefix(string input, string expected)
        => Assert.Equal(expected, PlanDisplayNames.Shorten(ProviderId.Codex, input));

    [Fact]
    public void ForTitle_HidesPlanWhenAlreadyInDisplayName()
        => Assert.Equal(string.Empty, PlanDisplayNames.ForTitle(ProviderId.Codex, "Codex Plus", "Plus"));

    [Fact]
    public void ForTitle_ReturnsPlanWhenNotInDisplayName()
        => Assert.Equal("Plus", PlanDisplayNames.ForTitle(ProviderId.Codex, "Codex", "Plus"));

    [Fact]
    public void IsRedundantWithDisplayName_TrueWhenDisplayEndsWithPlan()
        => Assert.True(PlanDisplayNames.IsRedundantWithDisplayName("Codex Plus", "Plus"));

    [Fact]
    public void IsRedundantWithDisplayName_FalseForDistinctPlan()
        => Assert.False(PlanDisplayNames.IsRedundantWithDisplayName("Codex", "Plus"));
}