using System;
using System.Collections.Generic;
using CodexQuota.Helpers;

namespace CodexQuota.Tests;

public class CountdownFormatTests
{
    [Fact]
    public void Format_Null_ReturnsNull()
    {
        Assert.Null(CountdownFormat.Format((DateTimeOffset?)null));
    }

    public static IEnumerable<object[]> Durations()
    {
        yield return new object[] { TimeSpan.Zero, "now" };
        yield return new object[] { TimeSpan.FromSeconds(-5), "now" };
        yield return new object[] { TimeSpan.FromSeconds(45), "0m" };
        yield return new object[] { TimeSpan.FromMinutes(5), "5m" };
        yield return new object[] { TimeSpan.FromMinutes(59), "59m" };
        yield return new object[] { TimeSpan.FromHours(1), "1h" };
        yield return new object[] { TimeSpan.FromMinutes(90), "1h 30m" };
        yield return new object[] { new TimeSpan(0, 23, 59, 0), "23h 59m" };
        yield return new object[] { TimeSpan.FromHours(24), "1d" };
        yield return new object[] { new TimeSpan(0, 24, 59, 0), "1d" };
        yield return new object[] { TimeSpan.FromHours(36), "1d 12h" };
        yield return new object[] { TimeSpan.FromHours(48), "2d" };
        yield return new object[] { new TimeSpan(7, 3, 0, 0), "7d 3h" };
    }

    [Theory]
    [MemberData(nameof(Durations))]
    public void Format_Duration_RendersCompactCountdown(TimeSpan span, string expected)
    {
        Assert.Equal(expected, CountdownFormat.Format(span));
    }

    // Non-boundary offsets: a few stray seconds absorb the time between UtcNow capture and formatting.
    [Theory]
    [InlineData(5, 17, "5m")]
    [InlineData(90, 11, "1h 30m")]
    [InlineData(36 * 60, 23, "1d 12h")]
    [InlineData(168 * 60, 41, "7d")]
    public void Format_DateTimeOffset_FormatsAgainstUtcNow(int minutes, int seconds, string expected)
    {
        var when = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
        Assert.Equal(expected, CountdownFormat.Format(when));
    }
}
