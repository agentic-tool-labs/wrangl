using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A reply carries whether the observation behind its data is too old; an empty long-poll reply says so too, so a reply arriving proves nothing.</summary>
public sealed partial class TreeLanesTests
{
    private static async Task<JsonValue> EmptyPollReply(ReadModel model)
    {
        var reply = JsonText.Parse(await model.PollAsync(0, "e1", [SummaryId], TimeSpan.Zero, TestContext.Current.CancellationToken));

        // Crossing the bound republishes the summary once, so the reply after that one is the empty one.
        for (var i = 0; i < 3 && reply.Member("slices")!.Value.AsArray().Count > 0; i++)
        {
            reply = JsonText.Parse(await model.PollAsync((long)reply.Member("seq")!.Value.AsNumber(), "e1", [SummaryId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        }

        return reply;
    }

    [Fact]
    public async Task AReadingPastTheBoundIsServedAsStaleDataOnManifestAndPollReply()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        ServeLaterMs = BoundMs + 1;
        try
        {
            Assert.True(JsonText.Parse(model.Manifest("summary")!).Member("data_stale")!.Value.AsBool());
            var reply = await EmptyPollReply(model);
            Assert.True(reply.Member("data_stale")!.Value.AsBool());
            Assert.False(reply.Member("observed_at")!.Value.IsNull && LastProcesses!.ObservedAtMs > 0);
        }
        finally
        {
            ServeLaterMs = 0;
        }
    }

    [Fact]
    public void TheBusyShareInAManifestIsWorkedOutWhenServed()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        ServeLaterMs = 2 * WorkingShare.WindowMs;
        try
        {
            var id = ActivityLane.SliceIdOf(Canon, "s-dev");
            var slice = JsonText.Parse(model.Manifest("members")!).Member("slices")!.Value.AsArray().First(s => s.Member("id")!.Value.AsString() == id);
            var body = slice.Member("body")!.Value;
            Assert.Equal(1, body.Member("working_1h")!.Value.AsNumber());
            Assert.Equal(WorkingShare.WindowMs, body.Member("working_window_ms")!.Value.AsNumber());
        }
        finally
        {
            ServeLaterMs = 0;
        }
    }

    [Fact]
    public async Task NegativeAReadingExactlyAtTheBoundIsNotStaleData()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        ServeLaterMs = BoundMs;
        try
        {
            Assert.False(JsonText.Parse(model.Manifest("summary")!).Member("data_stale")!.Value.AsBool());
            var empty = await EmptyPollReply(model);
            Assert.Empty(empty.Member("slices")!.Value.AsArray());
            Assert.False(empty.Member("data_stale")!.Value.AsBool());
        }
        finally
        {
            ServeLaterMs = 0;
        }
    }
}
