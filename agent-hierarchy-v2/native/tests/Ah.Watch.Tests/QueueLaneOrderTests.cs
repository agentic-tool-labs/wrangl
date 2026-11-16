using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The queue slice comes in display order, so no front end sorts it.</summary>
public sealed class QueueLaneOrderTests
{
    private const string Dir = "/repo/.claude/ah2";

    private static QueueRow Row(string item, double stallAt) => new(item, "implementor", 1, stallAt, [], null);

    private static List<string> Served(params QueueRow[] rows)
    {
        var lane = new QueueLane(() => [Dir], static _ => true, (_, _, _) => rows);
        var change = Assert.Single(lane.Reread(ProgressLane.KeyOf(Dir, "t"), 0).Changes);
        return [.. JsonText.Parse(change.Body!).AsArray().Select(static r => r.Member("item")!.Value.AsString())];
    }

    [Fact]
    public void TheSoonestStallComesFirstSoWhatHasStalledLeadsLongestStalledOnTop() =>
        Assert.Equal(["a/2", "a/3", "a/4", "a/1"], Served(Row("a/1", 400), Row("a/2", 100), Row("a/3", 200), Row("a/4", 300)));

    [Fact]
    public void NegativeRowsThatStallTogetherKeepTheProbesOrder() =>
        Assert.Equal(["b", "a", "c"], Served(Row("b", 100), Row("a", 100), Row("c", 100)));
}
