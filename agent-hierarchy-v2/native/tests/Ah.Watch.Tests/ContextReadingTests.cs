using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>How a member's context and process read, as both front ends draw them.</summary>
public sealed class ContextReadingTests
{
    [Theory]
    [InlineData("claude-sonnet-4-5[1m]", 1_000_000L)]
    [InlineData("Claude-Opus-4-1M", 1_000_000L)]
    [InlineData("claude-x-1m-20260101", 1_000_000L)]
    [InlineData("claude-opus-4-1-20250805", 200_000L)]
    [InlineData("opus", 200_000L)]
    [InlineData("sonnet-4-5", 200_000L)]
    [InlineData("fable", 200_000L)]
    public void AModelIdNamesItsWindow(string model, long window) => Assert.Equal(window, ContextReading.WindowOf(model));

    [Theory]
    [InlineData("gpt-5")]
    [InlineData("gemini-1.5-pro")]
    [InlineData("opusx")]
    [InlineData("claude")]
    [InlineData("11m")]
    [InlineData("")]
    [InlineData(null)]
    public void NegativeAnIdThatIsNoKnownModelHasNoWindow(string? model) => Assert.Null(ContextReading.WindowOf(model));

    [Theory]
    [InlineData(0L, "0%", 0L)]
    [InlineData(1L, "<1%", 0L)]
    [InlineData(1999L, "<1%", 0L)]
    [InlineData(2000L, "1%", 1L)]
    [InlineData(199_999L, "99%", 99L)]
    [InlineData(200_000L, "100%", 100L)]
    [InlineData(412_000L, "41%", 41L)]
    public void TheTextIsTheFlooredPercentage(long used, string text, long pct)
    {
        var r = ContextReading.Of("claude-opus-4-5", used);
        Assert.Equal(text, r.Text);
        Assert.Equal(pct, r.Pct);
        Assert.False(r.Over);
    }

    [Fact]
    public void A200kWindowThatIsOutgrownReadsAsInferred()
    {
        Assert.Contains("window inferred", ContextReading.Of("claude-opus-4-5", 412_000).Title, StringComparison.Ordinal);
        Assert.DoesNotContain("inferred", ContextReading.Of("claude-opus-4-5", 200_000).Title, StringComparison.Ordinal);
        Assert.DoesNotContain("inferred", ContextReading.Of("claude-sonnet-4-5[1m]", 412_000).Title, StringComparison.Ordinal);
    }

    [Fact]
    public void PastTheWindowReadsOverAndAnUnknownModelNeverDoes()
    {
        var over = ContextReading.Of("claude-opus-4-5", 1_000_001);
        Assert.Equal("100%+", over.Text);
        Assert.True(over.Over);
        Assert.False(ContextReading.Of("claude-opus-4-5", 1_000_000).Over);
        Assert.False(ContextReading.Of("gpt-5", 5_000_000).Over);
    }

    [Fact]
    public void NoWindowShowsTokensAndNoReadingShowsADash()
    {
        Assert.Equal((null, "55k", "55,000 tokens · window unknown", false), ContextReading.Of(null, 55_000));
        Assert.Equal((null, "—", "context not reported", false), ContextReading.Of("claude-opus-4-5", null));
    }

    [Theory]
    [InlineData("live", "gone", "running")]
    [InlineData("proven_dead", "working", "not running")]
    [InlineData("unknown", "working", "unknown")]
    [InlineData("not_seen", "gone", "not running")]
    [InlineData("not_seen", "idle", "not seen")]
    [InlineData(null, "working", "running")]
    public void TheProcessLineReadsTheLivenessThenTheState(string? liveness, string state, string line) =>
        Assert.Equal(line, SummaryData.ProcessLine(liveness, state));
}
