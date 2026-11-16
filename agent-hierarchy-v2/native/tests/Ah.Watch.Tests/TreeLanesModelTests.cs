using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The member slice carries the model the team file records for the member, and no key when it records none.</summary>
public sealed partial class TreeLanesTests
{
    private const string ModelTeamDoc =
        "{\"team_id\":\"t1\",\"created\":\"2026-06-01T10:00:00Z\",\"orchestrator\":{\"session_id\":\"so\",\"pid\":1,\"pid_start\":\"o1\"},\"members\":["
        + "{\"name\":\"dev\",\"role\":\"developer\",\"model\":\"sonnet\",\"kind\":\"claude\",\"transport_id\":\"p-dev\",\"stream\":null},"
        + "{\"name\":\"qa\",\"role\":\"tester\",\"transport_id\":\"p-qa\",\"stream\":null},"
        + "{\"name\":\"cx\",\"role\":\"developer\",\"model\":\"\",\"transport_id\":\"p-cx\",\"stream\":null},"
        + "{\"name\":\"num\",\"role\":\"developer\",\"model\":5,\"transport_id\":\"p-num\",\"stream\":null}]}";

    [Fact]
    public void TheMemberSliceCarriesTheModelTheTeamFileRecordsAndNoKeyWhenItRecordsNone()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = ModelTeamDoc;
        var (model, _) = Seeded(reader);
        using var owner = model;

        Assert.Contains("\"model\":\"sonnet\"", Body(model, MemberId("dev")), StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\"", Body(model, MemberId("qa")), StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\"", Body(model, MemberId("cx")), StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\"", Body(model, MemberId("num")), StringComparison.Ordinal);
        Assert.DoesNotContain("\"model\"", Body(model, MemberId(TreeIds.Orchestrator)), StringComparison.Ordinal);
    }
}
