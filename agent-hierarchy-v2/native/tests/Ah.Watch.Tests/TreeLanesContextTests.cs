using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A member's context is one reading, on its slice and in its team's summary, with when it was read.</summary>
public sealed partial class TreeLanesTests
{
    private const string ContextReadAt = "2026-06-01T11:58:00Z";

    private static string ActWithContext(string activity, int used) =>
        Act(activity).Replace("\"used\":1,", "\"used\":" + used + ",\"model\":\"claude-opus-4\",", StringComparison.Ordinal).Replace("\"at\":\"2026-06-01T12:00:00Z\"}}", "\"at\":\"" + ContextReadAt + "\"}}", StringComparison.Ordinal);

    private static JsonValue? SummaryContextOf(ReadModel model, string member) =>
        Summary(model).Member("member_states")!.Value.Member(MemberId(member))!.Value.Member("context_pct");

    [Fact]
    public void TheSliceAndTheSummaryGiveOneContextReadingWithItsTime()
    {
        var reader = FiveStates();
        reader.Activities["s-dev"] = ActWithContext("working", 62_000);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var member = JsonText.Parse(Body(model, MemberId("dev")));

        Assert.Equal(31, member.Member("context_pct")!.Value.AsNumber());
        Assert.Equal("31%", member.Member("context_text")!.Value.AsString());
        Assert.Equal(ContextReadAt, member.Member("context_at")!.Value.AsString());
        Assert.Equal(31, SummaryContextOf(model, "dev")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeAGoneMemberServesNoContextInItsTeamsSummary()
    {
        var reader = FiveStates();
        reader.Activities["s-gn"] = ActWithContext("working", 62_000);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Null(SummaryContextOf(model, "gn"));
    }

    [Fact]
    public void NegativeAReadingWithNoWindowServesNoPercent()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        Assert.Null(SummaryContextOf(model, "dev"));
    }
}
