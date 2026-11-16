using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>A snapshot is read only when the status revision or a box stamp moved since the last one.</summary>
public sealed class EventStampTests
{
    private const double Now = ModelSource.Now;

    private sealed class Revs : IStatusRevs
    {
        internal long Rev { get; set; } = 1;

        public StatusReading? Read(string dir) => new(Rev, JsonValue.Null);

        public void Forget(string dir)
        {
        }
    }

    private static ModelSource Source()
    {
        var src = new ModelSource { TeamName = "alpha" }.Member("impl");
        src.Boxes["impl"] = [];
        return src;
    }

    private static JsonObject Entry(string id) => new()
    {
        ["id"] = id,
        ["kind"] = "request",
        ["from"] = "orchestrator@alpha",
        ["to"] = "impl@alpha",
        ["created"] = "2027-01-15T07:58:00.000Z",
        ["acked"] = false,
    };

    private static EventStep Step(EventCursor? previous, ModelSource src, Revs revs, double at = Now) =>
        WatchModule.StepEvents(previous, src, revs, [ModelSource.Dir], at);

    // The first step cannot stamp boxes it has not seen; the second reads once more and from then on the stamp is stable.
    private static EventStep Warm(ModelSource src, Revs revs) => Step(Step(null, src, revs).Cursor, src, revs);

    [Fact]
    public void TheFirstStepReadsASnapshotAndReportsNothing()
    {
        var src = Source();

        var step = Step(null, src, new Revs());

        Assert.Empty(step.Batch.Events);
        Assert.True(src.IndexBoxReads > 0);
    }

    [Fact]
    public void NegativeAnUnchangedStatusRevAndBoxStampReadNoBoxAndKeepTheCursor()
    {
        var src = Source();
        var revs = new Revs();
        var first = Warm(src, revs);
        var reads = src.IndexBoxReads;

        var again = Step(first.Cursor, src, revs);

        Assert.Equal(reads, src.IndexBoxReads);
        Assert.Same(first.Cursor, again.Cursor);
        Assert.Empty(again.Batch.Events);
    }

    [Fact]
    public void AMovedStatusRevReadsAgainAndFindsTheStateChange()
    {
        var src = Source();
        var revs = new Revs();
        var first = Warm(src, revs);
        src.Status["impl"]["activity"] = "idle";
        revs.Rev = 2;

        var again = Step(first.Cursor, src, revs);

        Assert.Equal("impl: working → idle", Assert.Single(again.Batch.Events).Text);
        Assert.NotSame(first.Cursor, again.Cursor);
    }

    [Fact]
    public void AMovedBoxStampReadsAgainAndFindsTheNewMail()
    {
        var src = Source();
        var revs = new Revs();
        var first = Warm(src, revs);
        src.Boxes["impl"].Add(Entry("m1"));
        src.BoxStamps["impl"] = "1:1";

        var again = Step(first.Cursor, src, revs);

        Assert.Equal("mail_sent", Assert.Single(again.Batch.Events).Kind);
    }

    [Fact]
    public void ARewrittenTeamFileReadsAgainAndFindsTheStreamThatOpened()
    {
        var src = Source();
        var revs = new Revs();
        var first = Warm(src, revs);
        src.StreamPeers = ["impl"];
        src.FileStampText = "2:2";

        var again = Step(first.Cursor, src, revs);

        Assert.Equal("stream_open", Assert.Single(again.Batch.Events).Kind);
    }

    [Fact]
    public void NegativeNoTimeOnlyReadsAgainWhileNoStampMoved()
    {
        var src = Source();
        var revs = new Revs();
        var first = Warm(src, revs);
        src.JoinRows.Add(new JsonObject { ["id"] = "j1" });

        var later = Step(first.Cursor, src, revs, Now + 3_600_000);

        Assert.Empty(later.Batch.Events);
        Assert.Same(first.Cursor, later.Cursor);
    }

    [Fact]
    public void NegativeAChangeNoStampSawIsNotRead()
    {
        var src = Source();
        var revs = new Revs();
        var first = Warm(src, revs);
        src.Boxes["impl"].Add(Entry("m1"));

        var again = Step(first.Cursor, src, revs);

        Assert.Empty(again.Batch.Events);
    }
}
