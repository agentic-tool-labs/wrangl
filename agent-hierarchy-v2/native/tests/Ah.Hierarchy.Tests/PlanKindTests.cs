using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>The <c>kind</c> and <c>parent</c> plan events: how they fold, how a row carrying them is checked, and how the backlog doc keeps them.</summary>
public sealed class PlanKindTests
{
    private static string Row(string ev, string? extra = null) =>
        $"{{\"v\":1,\"ts\":\"2026-10-31T10:00:00Z\",\"team\":\"\",\"stream\":\"s\",\"item\":\"b1\",\"event\":\"{ev}\"{extra},\"by\":\"o\"}}";

    private static PlanItem Fold(params string[] rows) => ProgressFormat.FoldPlan(string.Join('\n', [Row("add", ",\"title\":\"T\""), .. rows])).Items.Single();

    [Fact]
    public void KindAndParentFoldAndAnEmptyParentClears()
    {
        var set = Fold(Row("kind", ",\"kind\":\"deliverable\""), Row("parent", ",\"parent\":\"b7\""));
        Assert.Equal("deliverable", set.Kind);
        Assert.Equal("b7", set.Parent);

        Assert.Null(Fold(Row("parent", ",\"parent\":\"b7\""), Row("parent", ",\"parent\":\"\"")).Parent);
    }

    [Fact]
    public void TaskAndAnUnknownKindFoldToNull()
    {
        Assert.Null(Fold(Row("kind", ",\"kind\":\"epic\""), Row("kind", ",\"kind\":\"task\"")).Kind);
        Assert.Null(Fold(Row("kind", ",\"kind\":\"saga\"")).Kind);
        Assert.Null(Fold(Row("kind", ",\"kind\":\"Epic\"")).Kind);
    }

    [Fact]
    public void AnItemWithNoKindOrParentEventFoldsToNullAndItsDocIsUnchanged()
    {
        var plain = Fold(Row("area", ",\"area\":\"db\""));
        Assert.Null(plain.Kind);
        Assert.Null(plain.Parent);
        var doc = Backlog.ToDoc(plain);
        Assert.DoesNotContain("kind", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("parent", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDocRoundTripsKindAndParent()
    {
        var item = Fold(Row("kind", ",\"kind\":\"deliverable\""), Row("parent", ",\"parent\":\"b3\""));
        var back = Backlog.FromDoc(Backlog.ToDoc(item))!;
        Assert.Equal("deliverable", back.Kind);
        Assert.Equal("b3", back.Parent);
    }

    [Theory]
    [InlineData("kind", ",\"kind\":\"saga\"")]
    [InlineData("kind", ",\"kind\":\"Epic\"")]
    [InlineData("kind", "")]
    [InlineData("parent", ",\"parent\":\"12\"")]
    [InlineData("parent", ",\"parent\":\"b\"")]
    [InlineData("parent", ",\"parent\":\"b1x\"")]
    [InlineData("parent", "")]
    public void ARowWithABadKindOrParentIsRefused(string ev, string extra)
    {
        var state = ProgressFormat.FoldPlan(Row("add", ",\"title\":\"T\""));
        Assert.NotNull(ProgressFormat.RefusePlanRow(JsonText.Parse(Row(ev, extra)), state, import: false));
    }

    [Theory]
    [InlineData("kind", ",\"kind\":\"task\"")]
    [InlineData("kind", ",\"kind\":\"epic\"")]
    [InlineData("parent", ",\"parent\":\"b12\"")]
    [InlineData("parent", ",\"parent\":\"\"")]
    public void ARowWithAGoodKindOrParentIsTaken(string ev, string extra)
    {
        var state = ProgressFormat.FoldPlan(Row("add", ",\"title\":\"T\""));
        Assert.Null(ProgressFormat.RefusePlanRow(JsonText.Parse(Row(ev, extra)), state, import: false));
    }

    [Theory]
    [InlineData("b1", true)]
    [InlineData("b1234", true)]
    [InlineData("b", false)]
    [InlineData("B1", false)]
    [InlineData("b1a", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    public void IsBacklogIdTakesOnlyBAndDigits(string text, bool expected) => Assert.Equal(expected, HierarchyModule.IsBacklogId(text));
}
