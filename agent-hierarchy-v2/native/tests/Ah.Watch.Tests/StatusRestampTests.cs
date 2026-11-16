using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>
/// b459 C1 (ADR adr-22): when a dir's stamp has not moved and the TTL is over, <see cref="StatusRevs"/> computes the document again only
/// if its witness says something moved; otherwise it serves the held document with its clock stamps rewritten.
/// </summary>
public sealed class StatusRestampTests
{
    private const long W0 = 1_780_000_000_000;

    private static string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A status document as the compute writes it at <paramref name="now"/>: a reported dispatch (stamped now), a working one (stamped when sent) and two timelines.</summary>
    private static JsonValue DocAt(long now) => JsonText.Parse(
        $$"""
        {"schema":1,"written_at":"{{Iso(now)}}","expires_at":"{{Iso(now + 86_400_000)}}","enabled":true,
         "teams":[{"team":"t","dispatches":[
           {"id":"r1","states":[{"at":"{{Iso(now)}}","state":"reported"},{"at":"{{Iso(W0 + 600_000)}}","state":"expired"}]},
           {"id":"r2","states":[{"at":"{{Iso(W0 - 1000)}}","state":"working"},{"at":"{{Iso(W0 + 900_000)}}","state":"overdue"}]}]}],
         "timeline":[{"at":"{{Iso(now)}}","text":"1 live"},{"at":"{{Iso(W0 + 900_000)}}","text":"later"}],
         "owners":[{"sessions":["s"],"timeline":[{"at":"{{Iso(now)}}","text":"1 live"}]}]}
        """);

    /// <summary>A store whose document is computed at <see cref="Wall"/>, the compute's wall clock.</summary>
    private sealed class State : IViewStateReader
    {
        internal string Stamp { get; set; } = "100:2";

        internal long Wall { get; set; } = W0;

        internal int Reads { get; private set; }

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (op == "stamp")
            {
                return new JsonObject { ["stamp"] = Stamp };
            }

            Reads++;
            return DocAt(Wall);
        }
    }

    private sealed class Rig
    {
        internal State State { get; } = new();

        internal long Tick { get; set; }

        internal double Horizon { get; set; } = double.PositiveInfinity;

        /// <summary>What a replay answers: null when nothing moved.</summary>
        internal string? Moved { get; set; }

        internal bool ReplayThrows { get; set; }

        internal bool Witnessed { get; set; } = true;

        internal int Replays { get; private set; }

        /// <summary>Run inside each replay, before it answers.</summary>
        internal Action? DuringReplay { get; set; }

        internal StatusRevs Revs { get; }

        internal Rig() => Revs = new StatusRevs(State, () => Tick, _ => Witnessed
            ? new StatusWitness(State.Wall, Horizon, () => State.Wall, () =>
            {
                Replays++;
                DuringReplay?.Invoke();
                return ReplayThrows ? throw new InvalidOperationException("ps failed") : Moved;
            })
            : null);

        /// <summary>The next read after the TTL, with the wall clock moved by <paramref name="wallMs"/>.</summary>
        internal StatusReading After(long wallMs)
        {
            Tick += StatusRevs.TtlMs;
            State.Wall += wallMs;
            return Revs.Read("/d")!;
        }
    }

    [Fact]
    public void AnExpiryWithNothingMovedServesTheHeldDocumentRestampedAndNeverChangesIt()
    {
        var rig = new Rig();
        var first = rig.Revs.Read("/d")!;
        var held = JsonText.Stringify(first.Doc, 0);

        var second = rig.After(5000);

        Assert.Equal(1, rig.State.Reads);
        Assert.Equal(1, rig.Replays);
        Assert.Equal(first.Rev, second.Rev);
        // the document a compute at this instant writes, every field
        Assert.Equal(JsonText.Stringify(DocAt(W0 + 5000), 0), JsonText.Stringify(second.Doc, 0));
        // a new object; the held one keeps every byte
        Assert.NotSame(first.Doc.AsObject(), second.Doc.AsObject());
        Assert.Equal(held, JsonText.Stringify(first.Doc, 0));
        Assert.Equal(1, rig.Revs.Counters("/d")["restamp"]);

        // inside the TTL the held document is served with no replay
        rig.Tick += StatusRevs.TtlMs - 1;
        Assert.Same(second.Doc.AsObject(), rig.Revs.Read("/d")!.Doc.AsObject());
        Assert.Equal(1, rig.Replays);
    }

    [Theory]
    [InlineData("no-witness")]
    [InlineData("entries-not-current")]
    [InlineData("replay-differed")]
    [InlineData("replay-threw")]
    [InlineData("past-horizon")]
    [InlineData("past-cap")]
    [InlineData("clock-stepped-back")]
    public void NegativeAnExpiryIsComputedAgainWhenTheWitnessCannotVouchForIt(string reason)
    {
        var rig = new Rig();
        long wall = 5000;
        switch (reason)
        {
            case "no-witness": rig.Witnessed = false; break;
            case "replay-threw": rig.ReplayThrows = true; break;
            case "past-horizon": rig.Horizon = W0 + 5000; break;
            case "past-cap": wall = StatusRevs.RestampCapMs; break;
            case "clock-stepped-back": wall = -1; break;
            default: rig.Moved = reason; break;
        }

        _ = rig.Revs.Read("/d");
        var second = rig.After(wall);

        Assert.Equal(2, rig.State.Reads);
        Assert.Equal(JsonText.Stringify(DocAt(W0 + wall), 0), JsonText.Stringify(second.Doc, 0));
        var counters = rig.Revs.Counters("/d");
        Assert.Equal(1, counters["refused:" + reason]);
        Assert.Equal(1, counters["recompute:ttl"]);
        Assert.False(counters.ContainsKey("restamp"));
    }

    [Fact]
    public void ARestampNeverOutlivesTheCapAndNegativeAMovedStampIsComputedWhateverTheWitnessSays()
    {
        var rig = new Rig();
        _ = rig.Revs.Read("/d");
        // eleven expiries inside the cap are restamps of the one compute; the twelfth is 60 s after it
        for (var i = 1; i <= 11; i++)
        {
            _ = rig.After(5000);
            Assert.Equal(1, rig.State.Reads);
        }

        _ = rig.After(5000);
        Assert.Equal(2, rig.State.Reads);
        Assert.Equal(11, rig.Revs.Counters("/d")["restamp"]);
        Assert.Equal(1, rig.Revs.Counters("/d")["refused:past-cap"]);

        // the cap counts from the new compute
        _ = rig.After(5000);
        Assert.Equal(2, rig.State.Reads);

        rig.State.Stamp = "200:3";
        var replays = rig.Replays;
        _ = rig.Revs.Read("/d");
        Assert.Equal(3, rig.State.Reads);
        Assert.Equal(replays, rig.Replays);
        Assert.Equal(1, rig.Revs.Counters("/d")["recompute:stamp"]);
    }

    [Theory]
    [InlineData(55_000, true)]
    [InlineData(55_001, false)]
    public void ARestampIsServedForATtlSoItsWholeWindowEndsInsideTheCap(long wallAge, bool restamped)
    {
        var rig = new Rig();
        _ = rig.Revs.Read("/d");

        // a restamp at this age is served unchecked until age + TTL: at most the cap, never a millisecond past it
        _ = rig.After(wallAge);

        Assert.True(wallAge + StatusRevs.TtlMs <= StatusRevs.RestampCapMs == restamped);
        Assert.Equal(restamped ? 1 : 2, rig.State.Reads);
        Assert.Equal(restamped, rig.Revs.Counters("/d").ContainsKey("restamp"));
        Assert.Equal(!restamped, rig.Revs.Counters("/d").ContainsKey("refused:past-cap"));
    }

    [Fact]
    public void NegativeAWallClockSteppedBackByLessThanTheDocumentsAgeDoesNotStretchTheCap()
    {
        var rig = new Rig();
        _ = rig.Revs.Read("/d");
        // 50 s of real time pass, restamped on the way
        for (var i = 1; i <= 10; i++)
        {
            _ = rig.After(5000);
        }

        Assert.Equal(1, rig.State.Reads);
        // the wall clock steps back 40 s: it now reads 15 s after the compute, though 55 s have gone by
        _ = rig.After(-35_000);
        Assert.Equal(1, rig.State.Reads);
        Assert.Equal(11, rig.Revs.Counters("/d")["restamp"]);

        // 60 s of real time after the compute the wall clock says 20 s: computed again all the same
        _ = rig.After(5000);
        Assert.Equal(2, rig.State.Reads);
        Assert.Equal(1, rig.Revs.Counters("/d")["refused:past-cap"]);
    }

    [Fact]
    public void ARestampNeverBringsBackAForgottenEntryAndForgetDropsTheDirsCounters()
    {
        var rig = new Rig();
        _ = rig.Revs.Read("/d");
        // the dir is forgotten while its replay runs
        rig.DuringReplay = () => rig.Revs.Forget("/d");
        _ = rig.After(5000);
        rig.DuringReplay = null;
        Assert.Equal(1, rig.State.Reads);

        // nothing is held: the next read, well inside the TTL, computes
        _ = rig.Revs.Read("/d");
        Assert.Equal(2, rig.State.Reads);

        rig.Revs.Forget("/d");
        Assert.Empty(rig.Revs.Counters("/d"));
    }
}
