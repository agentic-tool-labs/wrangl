using Ah.Core.Json;

namespace Ah.Watch.Tests;

/// <summary>files-off-disk F3a: the <c>/v1/status</c> revision is the DB stamp's newest write, re-read only when the stamp moves, and only rises.</summary>
public sealed class StatusRevsTests
{
    private sealed class State : IViewStateReader
    {
        internal string Stamp { get; set; } = "100:2";

        internal int Reads { get; private set; }

        internal bool Fail { get; set; }

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (Fail)
            {
                throw new InvalidOperationException("db down");
            }

            if (op == "stamp")
            {
                return new JsonObject { ["stamp"] = Stamp };
            }

            Reads++;
            return Live is null
                ? new JsonObject { ["schema"] = 1, ["n"] = Reads }
                : new JsonObject { ["schema"] = 1, ["written_at"] = $"t{Reads}", ["live"] = Live };
        }

        /// <summary>Set: the document is <c>{written_at: t&lt;reads&gt;, live}</c>, so a re-read moves only its clock stamp unless this changes.</summary>
        internal string? Live { get; set; }
    }

    /// <summary>A status document as computed at <paramref name="now"/>, with one dispatch whose first state is <paramref name="state"/> (DispatchStates' shape).</summary>
    private static JsonValue Doc(string now, string state, string stateAt) => JsonText.Parse(
        $$"""
        {"schema":1,"written_at":"{{now}}","expires_at":"{{now}}+1d","enabled":true,
         "teams":[{"team":"t","dispatches":[{"id":"r1","written_at":"keep","states":[{"at":"{{stateAt}}","state":"{{state}}"},{"at":"2026-03-02T00:00:00.000Z","state":"expired"}]}]}],
         "timeline":[{"at":"{{now}}","text":"0 live"},{"at":"2026-03-02T00:00:00.000Z","text":"later"}],
         "owners":[{"sessions":["s"],"timeline":[{"at":"{{now}}","text":"0 live"}]}]}
        """);

    private static string Norm(JsonValue doc) => JsonText.Stringify(WatchModule.StatusWithoutClockStamps(doc), 0);

    [Fact]
    public void TwoDocsComputedAtDifferentInstantsWithAReportedDispatchNormalizeEqual()
    {
        Assert.Equal(Norm(Doc("2026-03-01T12:00:00.000Z", "reported", "2026-03-01T12:00:00.000Z")), Norm(Doc("2026-03-01T12:00:05.000Z", "reported", "2026-03-01T12:00:05.000Z")));

        // the negatives: a working dispatch's at is its sent time, not the clock; a later timeline entry and a nested written_at are data
        Assert.NotEqual(Norm(Doc("x", "working", "2026-03-01T11:00:00.000Z")), Norm(Doc("x", "working", "2026-03-01T11:00:01.000Z")));
        var later = Doc("x", "reported", "x");
        later.Member("timeline")!.Value.AsArray()[1].AsObject()["at"] = "2026-03-03T00:00:00.000Z";
        Assert.NotEqual(Norm(Doc("x", "reported", "x")), Norm(later));
        Assert.Contains("\"written_at\":\"keep\"", Norm(later), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnmovedStampIsReReadAfterTheTtlAndTheRevMovesOnlyWhenMoreThanTheClockStampChanged()
    {
        var state = new State { Live = "1" };
        var now = 0L;
        var revs = WatchModule.CreateStatusRevs(state, () => now);
        Assert.Equal(100_000, revs.Read("/h")!.Rev);

        // the negative: inside the TTL nothing is read again
        now = 4999;
        _ = revs.Read("/h");
        Assert.Equal(1, state.Reads);

        // past it the document is computed again; only written_at moved, so the rev holds and the fresh document is served
        now = 5000;
        var same = revs.Read("/h")!;
        Assert.Equal(2, state.Reads);
        Assert.Equal(100_000, same.Rev);
        Assert.Equal("t2", same.Doc.Member("written_at")!.Value.AsString());

        // a liveness change the stamp does not see moves the rev on the next TTL re-read
        state.Live = "0";
        now = 10_000;
        Assert.Equal(100_001, revs.Read("/h")!.Rev);
    }

    [Fact]
    public void AStampMoveThatLeavesTheDocEqualKeepsTheRevAndNegativeAContentChangeMovesIt()
    {
        var state = new State { Live = "1" };
        var revs = WatchModule.CreateStatusRevs(state, () => 0);
        Assert.Equal(100_000, revs.Read("/h")!.Rev);

        // b459 step 23: a write that rewrote nothing the document shows (a seq-only move): read again, only written_at differs, rev kept
        state.Stamp = "100:2:x=2";
        var kept = revs.Read("/h")!;
        Assert.Equal(2, state.Reads);
        Assert.Equal(100_000, kept.Rev);

        state.Stamp = "200:2:x=3";
        state.Live = "0";
        Assert.Equal(200_000, revs.Read("/h")!.Rev);
    }

    [Fact]
    public void TheRevIsTheStampsNewestWriteReReadOnlyWhenItMovesAndNeverFalls()
    {
        var state = new State();
        var revs = WatchModule.CreateStatusRevs(state);

        Assert.Equal(100_000, revs.Read("/h")!.Rev);

        // the negative: an unmoved stamp is the same rev and no second document read
        Assert.Equal(100_000, revs.Read("/h")!.Rev);
        Assert.Equal(1, state.Reads);

        state.Stamp = "100:1";
        var deleted = revs.Read("/h")!;
        Assert.Equal(100_001, deleted.Rev);
        Assert.Equal(2, (int)deleted.Doc.Member("n")!.Value.AsNumber());

        state.Fail = true;
        Assert.Null(revs.Read("/h"));
    }
}
