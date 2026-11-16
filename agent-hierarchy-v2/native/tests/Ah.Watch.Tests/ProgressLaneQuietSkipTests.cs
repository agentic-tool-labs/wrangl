using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.TestKit;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>
/// ah2-52 1a: a progress re-read that finds nothing the record shows has moved keeps the held record, and what it serves is byte for byte
/// what a lane that never skips serves. Both lanes read one real store through the real progress and team ops.
/// </summary>
public sealed class ProgressLaneQuietSkipTests
{
    private const string Team = "t";

    private static RealStateRig Make() => RealStateRig.Make();

    private static string Doc(RealStateRig rig, string name) => Path.Combine(rig.Pool, "progress", Team, name);

    private static void Write(RealStateRig rig, string domain, string op, JsonObject args) => rig.Write(domain, op, args);

    private static void TeamRecord(RealStateRig rig, string owner) =>
        Write(rig, "team", "write", new JsonObject
        {
            ["dir"] = rig.Pool,
            ["path"] = rig.Roster.TeamPath(rig.Pool, Team),
            ["team"] = JsonText.Parse("{\"team_id\":\"t-t\",\"members\":[{\"role\":\"implementor\",\"name\":\"" + owner + "\",\"stream\":\"main\"}],\"streams\":{\"main\":{\"branch\":\"ah/main\",\"state\":\"open\"}}}"),
        });

    private static void Ledger(RealStateRig rig, string text) =>
        Assert.True(rig.Services.Progress.AppendLedger(rig.Pool, Team, new LedgerEvent("ship", Stream: "main", Text: text)).Appended);

    /// <summary>Both lanes over one store, each counting its callbacks; <c>A</c> skips, <c>B</c> never does.</summary>
    private sealed class Pair
    {
        public Pair(RealStateRig rig)
        {
            var reader = rig.Reader();
            A = new ProgressLane(reader, TestFolds.New, () => [rig.Pool], reread: _ => ACalls++);
            B = new ProgressLane(reader, TestFolds.New, () => [rig.Pool], reread: _ => BCalls++, skipQuiet: false);
            Key = ProgressLane.KeyOf(rig.Pool, Team);
            Suffix = ProgressLane.SuffixOf(rig.Pool, Team);
        }

        public ProgressLane A { get; }

        public ProgressLane B { get; }

        public string Key { get; }

        public string Suffix { get; }

        public int ACalls { get; private set; }

        public int BCalls { get; private set; }

        public LaneRecord? Last { get; private set; }

        /// <summary>Re-reads both lanes and asserts they serve the same bytes; returns whether A kept its last record.</summary>
        public bool Step(string what)
        {
            var skipsBefore = A.QuietSkips;
            var a = A.Reread(Key, 1_000).Record!;
            var b = B.Reread(Key, 1_000).Record!;
            Assert.True(a.IsAbsent == b.IsAbsent, what + ": absent");
            if (!a.IsAbsent)
            {
                Assert.True(JsonText.ToUtf8(a.Value, 0).AsSpan().SequenceEqual(JsonText.ToUtf8(b.Value, 0)), what + ": record\nA " + JsonText.Stringify(a.Value, 0) + "\nB " + JsonText.Stringify(b.Value, 0));
            }

            Assert.True(a.AsOfMs == b.AsOfMs, what + ": as_of");
            Assert.True(Text(A.PlanChangesOf(Suffix)) == Text(B.PlanChangesOf(Suffix)), what + ": changes");
            foreach (var stream in (string[])["main", "notes"])
            {
                Assert.True(Text(A.ItemsBodyOf(Suffix, stream)) == Text(B.ItemsBodyOf(Suffix, stream)), what + ": items of " + stream);
            }

            Assert.True(ACalls == BCalls, what + ": callbacks");
            var skipped = A.QuietSkips == skipsBefore + 1;
            Assert.True(skipped == ReferenceEquals(a, Last), what + ": a skip is the same instance and only a skip is");
            Last = a;
            return skipped;
        }

        private static string? Text(JsonArray? value) => value is null ? null : JsonText.Stringify(JsonValue.FromArray(value), 0);

        private static string? Text(JsonValue? value) => value is null ? null : JsonText.Stringify(value.Value, 0);
    }

    private static RealStateRig Seeded()
    {
        var rig = Make();
        _ = rig.Services.Progress.EnsureTeam(rig.Pool, Team, new ProgressStreamInfo("worker", "ah/main", "wt", "the goal"));
        var recorded = rig.Services.Progress.RecordPlan(rig.Pool, Team, "main", "me",
        [
            new PlanInput("one", "add", "First"), new PlanInput("two", "add", "Second"), new PlanInput("three", "add", "Third"),
            new PlanInput("one", "start"), new PlanInput("one", "done", Commits: ["abc1234"]), new PlanInput("two", "wait-user", Note: "Which?") { Options = ["a", "b"] },
        ]);
        Assert.Null(recorded.Refused);
        Write(rig, "progress", "create-if-absent", new JsonObject
        {
            ["dir"] = rig.Pool, ["team"] = Team, ["file"] = "notes.md",
            ["content"] = "<!-- ah-progress v1.0 kind=stream -->\n# notes — side notes\n\nOwner: worker\nState: open\nUpdated: 2026-06-01T12:00:00Z\n\n## Checklist\n\n- [ ] n1 — Note one\n- [x] n2 — Note two\n",
        });
        TeamRecord(rig, "alice");
        return rig;
    }

    [Fact]
    public void ASkipServesWhatAFullReReadServesAfterEveryWrite()
    {
        var rig = Seeded();
        var pair = new Pair(rig);

        Assert.False(pair.Step("1 seeded"));
        Assert.Contains("waiting_user", JsonText.Stringify(pair.Last!.Value, 0), StringComparison.Ordinal);
        Assert.Contains("\"notes\"", JsonText.Stringify(pair.Last!.Value, 0), StringComparison.Ordinal);

        Ledger(rig, "shipped one");
        Assert.True(pair.Step("2 ledger"));

        Assert.Null(rig.Services.Progress.RecordPlan(rig.Pool, Team, "main", "me", [new PlanInput("three", "done", Commits: ["def5678"])]).Refused);
        Assert.False(pair.Step("3 plan done"));

        Thread.Sleep(5);
        Write(rig, "progress", "checklist-rewrite", new JsonObject { ["dir"] = rig.Pool, ["team"] = Team, ["stream"] = "notes", ["optin"] = true, ["updated"] = "2026-06-02T09:00:00Z" });
        Assert.False(pair.Step("4 checklist rewrite"));

        TeamRecord(rig, "bob");
        Assert.False(pair.Step("5 owner"));

        Ledger(rig, "shipped two");
        Assert.True(pair.Step("6 ledger"));

        _ = rig.Services.Progress.SetOff(rig.Pool, Team, true);
        Assert.False(pair.Step("7 off"));
    }

    [Fact]
    public void NegativeAChecklistDocGoneOrTouchedIsAFullReRead()
    {
        var rig = Seeded();
        var pair = new Pair(rig);
        Assert.False(pair.Step("seeded"));
        Assert.True(pair.Step("quiet"));

        // the sig moves and the text does not: full, with an equal record
        TestProgressDocs.Touch(rig.AhHome, Doc(rig, "notes.md"), DateTimeOffset.Parse("2026-06-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        Assert.False(pair.Step("notes.md touched"));
        Assert.True(pair.Step("quiet after touch"));

        TestProgressDocs.Delete(rig.AhHome, Doc(rig, "notes.md"));
        Assert.False(pair.Step("notes.md gone"));
        Assert.DoesNotContain("\"notes\"", JsonText.Stringify(pair.Last!.Value, 0), StringComparison.Ordinal);
        Assert.True(pair.Step("quiet again"));
    }

    [Fact]
    public void NegativeATeamGoneWhileAnItemWaitsOnTheUserIsAFullReReadThoughTheStreamsReplyIsTheSame()
    {
        var rig = Seeded();
        // a team record that names no stream, so the streams reply does not change when it goes
        var path = rig.Roster.TeamPath(rig.Pool, Team);
        Write(rig, "team", "write", new JsonObject { ["dir"] = rig.Pool, ["path"] = path, ["team"] = JsonText.Parse("{\"team_id\":\"t-t\",\"members\":[{\"role\":\"implementor\",\"name\":\"x\"}]}") });
        var pair = new Pair(rig);
        Assert.False(pair.Step("seeded"));
        Assert.Contains("\"team_exists\":true", JsonText.Stringify(pair.Last!.Value, 0), StringComparison.Ordinal);
        Assert.True(pair.Step("quiet"));

        Write(rig, "team", "clear", new JsonObject { ["dir"] = rig.Pool, ["path"] = path });

        Assert.False(pair.Step("team cleared"));
        Assert.DoesNotContain("team_exists", JsonText.Stringify(pair.Last!.Value, 0), StringComparison.Ordinal);
        Assert.True(pair.Step("quiet after"));
    }

    [Fact]
    public void TheProjectorCallsTheSameRecordTheSameWithoutStringifyingIt()
    {
        var big = new JsonArray();
        for (var i = 0; i < 2000; i++)
        {
            big.Add(new JsonObject { ["item"] = "item-" + i, ["title"] = new string('x', 40) });
        }

        var record = new LaneRecord(JsonValue.FromObject(new JsonObject { ["items"] = big }), 7);
        var copy = new LaneRecord(JsonText.Parse(JsonText.Stringify(record.Value, 0)), 7);
        _ = Projector.SameRecord(record, copy);

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(Projector.SameRecord(record, record));
        var same = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(Projector.SameRecord(record, copy));
        var equal = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(same < 1024, "the same instance allocated " + same);
        Assert.True(equal > 100_000, "an equal copy allocated only " + equal);
        Assert.False(Projector.SameRecord(null, record));
    }
}
