using Ah.Core.Time;

namespace Ah.Core.Tests;

/// <summary>The <c>ps</c> start time form: what parses, and what is refused.</summary>
public sealed class ProcessStartParseTests
{
    [Theory]
    [InlineData("Wed Oct 14 19:00:00 2026")]
    [InlineData("Thu Oct  1 07:05:09 2026")]
    [InlineData("  Thu Oct 1 07:05:09 2026  ")]
    public void APsStartTimeParsesToItsLocalInstant(string text)
    {
        Assert.True(Timestamps.TryParseProcessStart(text, out var started));
        Assert.Equal(2026, started.Year);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-10-14T19:00:00Z")]
    [InlineData("Wed Oct 14 19:00 2026")]
    [InlineData("Tue Oct 14 19:00:00 2026")]
    [InlineData("not a time")]
    public void AnythingElseIsRefused(string? text)
    {
        Assert.False(Timestamps.TryParseProcessStart(text, out _));
    }
}
