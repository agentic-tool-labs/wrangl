using Ah.Core.Json;

namespace Ah.Commands.Tests;

/// <summary>An undecided member is never called live: it may be running, and a member not seen for long says for how long.</summary>
public sealed class UntrackUnknownWordingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-23T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private static JsonObject State(string? lastLife) => new() { ["why"] = "no usable observation of its process", ["last_life_at"] = lastLife is null ? JsonValue.Null : lastLife };

    [Fact]
    public void AnUnknownMemberWithFreshEvidenceMayStillBeRunning()
    {
        var text = UntrackVerb.UnknownWording(State("2026-10-23T11:50:00Z"), Now);

        Assert.Equal("may still be running (liveness unknown: no usable observation of its process)", text);
        Assert.DoesNotContain("is still live", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-10-23T11:00:00Z", "1h00m")]
    [InlineData("2026-10-23T11:29:00Z", "31m")]
    [InlineData("2026-10-20T12:00:00Z", "3d")]
    public void NegativeAnUnknownMemberWhoseEvidenceIsOlderThanTheClaimWindowSaysHowLongItHasNotBeenSeen(string lastLife, string age)
    {
        Assert.Equal(
            $"may still be running (not seen for {age}; liveness unknown: no usable observation of its process)",
            UntrackVerb.UnknownWording(State(lastLife), Now));
    }

    [Fact]
    public void AnUnknownMemberWithNoEvidenceAtAllSaysEver()
    {
        Assert.Contains("not seen for ever", UntrackVerb.UnknownWording(State(null), Now), StringComparison.Ordinal);
    }
}
