using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The busy share is worked out when served: it decays with no new transition, and divides by the time actually seen.</summary>
public sealed class WorkingShareTests
{
    private const long Minute = 60_000;
    private const long T0 = 100 * WorkingShare.WindowMs;

    private static JsonObject Served(long seenFrom, params (long From, long? To)[] spans)
    {
        var served = new JsonArray();
        foreach (var (from, to) in spans)
        {
            served.Add(JsonValue.FromArray(new JsonArray { JsonValue.FromNumber(from), to is { } end ? JsonValue.FromNumber(end) : JsonValue.Null }));
        }

        var body = new JsonObject();
        body["working_1h"] = 1;
        body["working_window_ms"] = Minute;
        body["working_spans"] = JsonValue.FromArray(served);
        body["working_seen_from"] = seenFrom;
        return body;
    }

    [Fact]
    public void AShareDecaysWhenServedLaterWithNoNewTransition()
    {
        var body = Served(T0 - (60 * Minute), (T0 - (60 * Minute), T0));

        WorkingShare.Project(body, T0 + (30 * Minute));
        Assert.Equal(0.5, body.Get("working_1h")!.Value.AsNumber());

        WorkingShare.Project(body, T0 + (90 * Minute));
        Assert.Equal(0, body.Get("working_1h")!.Value.AsNumber());
    }

    [Fact]
    public void AShareDividesByTheTimeSeenAndSaysHowLongThatWas()
    {
        var body = Served(T0, (T0, T0 + (15 * Minute)));

        WorkingShare.Project(body, T0 + (30 * Minute));

        Assert.Equal(0.5, body.Get("working_1h")!.Value.AsNumber());
        Assert.Equal(30 * Minute, body.Get("working_window_ms")!.Value.AsNumber());
    }

    [Fact]
    public void AnOpenSpanRunsToTheServeTime()
    {
        var body = Served(T0, (T0 + (15 * Minute), null));

        WorkingShare.Project(body, T0 + (30 * Minute));

        Assert.Equal(0.5, body.Get("working_1h")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeASpanEndingExactlyAtTheWindowStartCountsNothing()
    {
        var body = Served(T0 - (60 * Minute), (T0 - (60 * Minute), T0));

        WorkingShare.Project(body, T0 + (60 * Minute));

        Assert.Equal(0, body.Get("working_1h")!.Value.AsNumber());
        Assert.Equal(60 * Minute, body.Get("working_window_ms")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeABodyWithNoSpansKeepsWhatItHas()
    {
        var body = new JsonObject();
        body["working_1h"] = 0.75;

        WorkingShare.Project(body, T0);

        Assert.Equal(0.75, body.Get("working_1h")!.Value.AsNumber());
        Assert.Null(body.Get("working_window_ms"));
    }

    [Fact]
    public void NoTimeSeenReadsZeroNotADivisionByZero()
    {
        var body = Served(T0);

        WorkingShare.Project(body, T0);

        Assert.Equal(0, body.Get("working_1h")!.Value.AsNumber());
        Assert.Equal(0, body.Get("working_window_ms")!.Value.AsNumber());
    }

    [Fact]
    public void TheRingsReadingMatchesWhatServingItsSpansGives()
    {
        var share = new WorkingShare();
        share.Observe("d", "s", "working", T0);
        share.Observe("d", "s", "idle", T0 + (15 * Minute));

        var reading = share.Of("d", "s", T0 + (30 * Minute))!.Value;

        Assert.Equal(0.5, reading.Share);
        Assert.Equal(30 * Minute, reading.WindowMs);
        Assert.Equal(T0, reading.SeenFromMs);
    }
}
