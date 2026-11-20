using Ah.Cli.Composition;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>b487 review findings 1-3: the host's DONE-report scan settles paths per pool and never reads a settled one again.</summary>
public sealed class UnreviewedDoneScanTests
{
    private const string Pool = "/h/.claude/ah2";
    private const double Now = 1_000_000_000_000;
    private const double Minute = 60_000;

    private sealed class Store
    {
        internal Dictionary<string, (double At, string Text, string To)> Responses { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> Reviewed { get; } = new(StringComparer.Ordinal);

        internal int TimeReads { get; private set; }

        internal int TextReads { get; private set; }

        internal int ReviewReads { get; private set; }

        internal static string PathOf(string pool, string id) => $"{pool}/msgs/{id}--orchestrator--s--response.md";

        internal void Add(string id, double at, string text, string to = "implementor", string pool = Pool) => Responses[PathOf(pool, id)] = (at, text, to);

        internal IReadOnlyList<JsonObject> Exchanges() =>
        [
            .. Responses.Select(static kv => new JsonObject
            {
                ["id"] = kv.Key.Split('/')[^1].Split("--")[0],
                ["to"] = kv.Value.To,
                ["response"] = new JsonObject { ["path"] = kv.Key },
            }),
        ];

        internal DoneScanReads Reads() => new(
            static role => role == "implementor",
            path => { TimeReads++; return Responses.TryGetValue(path, out var r) ? r.At : null; },
            static e => new JsonObject { ["from"] = "orchestrator", ["team"] = "t", ["to_name"] = "impl-1", ["title"] = "Build it" },
            path => { TextReads++; return Responses.TryGetValue(path, out var r) ? r.Text : null; },
            (id, _, _) => { ReviewReads++; return Reviewed.Contains(id); });

        internal ReviewScope? LastScope { get; private set; }

        internal DoneScanReads ScopeReads() => Reads() with { Reviewed = (_, _, scope) => { LastScope = scope; return false; } };
    }

    private const string Done = "## [0] tldr\n- [1] status: DONE abc1234\n";
    private const string Blocked = "## [0] tldr\n- [1] status: BLOCKED x\n";

    private static IReadOnlyList<Ah.Watch.DoneReport> Scan(DoneReportScan scan, Store store, IReadOnlyList<string>? pools = null, IReadOnlySet<string>? alarmed = null) =>
        scan.Scan(pools ?? [Pool], store.Exchanges(), new HashSet<string>(StringComparer.Ordinal) { "t" }, alarmed ?? new HashSet<string>(StringComparer.Ordinal), Now, store.Reads());

    /// <summary>The daemon started three minutes before <see cref="Now"/>.</summary>
    private static DoneReportScan NewScan() => new(Now - (3 * Minute));

    [Fact]
    public void SettledPathsAreNeverReadAgain()
    {
        var scan = NewScan();
        var store = new Store();
        store.Add("20261113-100000-old1", Now - (5 * Minute), Done);
        Assert.Empty(Scan(scan, store));
        Assert.Equal((1, 0), (store.TimeReads, store.TextReads)); // filed before the daemon started: settled on its time alone

        store.Add("20261113-110000-blk1", Now - (5 * Minute), Blocked);
        store.Add("20261113-110001-rev1", Now - (5 * Minute), Done);
        store.Add("20261113-110002-alm1", Now - (20 * Minute), Done);
        store.Add("20261113-110003-old2", Now - (61 * Minute), Done);
        store.Reviewed.Add("20261113-110001-rev1");
        var alarmed = new HashSet<string>(StringComparer.Ordinal) { "20261113-110002-alm1" };
        Assert.Empty(Scan(scan, store, alarmed: alarmed));
        var (time, text, review) = (store.TimeReads, store.TextReads, store.ReviewReads);
        Assert.Equal(4, time); // the alarmed one is settled before any read; old1 is not read again
        Assert.Equal(2, text);
        Assert.Equal(1, review);

        // the BLOCKED one is not settled, so its time is read again; nothing else is read
        Assert.Empty(Scan(scan, store, alarmed: alarmed));
        Assert.Equal((time + 1, text, review), (store.TimeReads, store.TextReads, store.ReviewReads));
    }

    [Fact]
    public void PartOrBlockedThenDoneOnTheSamePathStillAlarms()
    {
        var scan = NewScan();
        var store = new Store();
        Assert.Empty(Scan(scan, store));
        const string id = "20261113-160000-part";
        store.Add(id, Now - (10 * Minute), Blocked);
        Assert.Empty(Scan(scan, store));
        Assert.Empty(Scan(scan, store));
        Assert.Equal(1, store.TextReads); // unchanged: not read again
        store.Add(id, Now - (2 * Minute), Done);
        Assert.Equal(id, Assert.Single(Scan(scan, store)).RequestId);
        Assert.Equal(2, store.TextReads);
    }

    [Fact]
    public void WaitingDoneReportIsReportedWithoutReadingItsTextAgain()
    {
        var scan = NewScan();
        var store = new Store();
        Assert.Empty(Scan(scan, store));
        store.Add("20261113-120000-wait", Now - (5 * Minute), Done);
        var first = Assert.Single(Scan(scan, store));
        Assert.Equal("impl-1@t", first.Member);
        Assert.Single(Scan(scan, store));
        Assert.Equal(1, store.TextReads);
        Assert.Equal(2, store.ReviewReads); // a review can still appear while it waits
    }

    [Fact]
    public void NewPoolSettlesOnlyReportsFiledBeforeTheDaemonStarted()
    {
        var scan = NewScan();
        var store = new Store();
        Assert.Empty(Scan(scan, store));
        const string other = "/other/.claude/ah2";
        store.Add("20261113-130000-know", Now - Minute, Done);
        store.Add("20261113-130001-oldp", Now - (5 * Minute), Done, pool: other);
        store.Add("20261113-130002-newp", Now - Minute, Done, pool: other);
        var ids = Scan(scan, store, pools: [Pool, other]).Select(static r => r.RequestId).Order(StringComparer.Ordinal);
        Assert.Equal(["20261113-130000-know", "20261113-130002-newp"], ids);
    }

    [Fact]
    public void SameSecondChildWithALowerSuffixCountsAsTheReview()
    {
        JsonObject Ex(string id, string to) => new() { ["id"] = id, ["to"] = to };
        var parent = "20261113-140000-zzzz";
        var child = "20261113-140000-aaaa";
        IReadOnlyList<JsonObject> exchanges = [Ex(parent, "implementor"), Ex(child, "reviewer")];
        JsonObject? Fm(JsonObject e) => e["id"].AsString() == child ? new JsonObject { ["parent"] = parent } : new JsonObject();
        Assert.True(DoneReportScan.HasReview(parent, "impl-1@t", exchanges, Fm, static r => r == "reviewer" ? "review" : "implement", [], static _ => null));
        Assert.False(DoneReportScan.HasReview(parent, "impl-1@t", [Ex(parent, "implementor"), Ex(child, "qa")], Fm, static r => r == "reviewer" ? "review" : "implement", [], static _ => null));
    }

    [Fact]
    public void LinkWithAReviewerOnAChildTaskCountsAndOneWithoutTheMemberDoesNot()
    {
        var parent = "20261113-150000-p000";
        var child = "20261113-150100-c000";
        IReadOnlyList<JsonObject> exchanges = [new() { ["id"] = child, ["to"] = "implementor" }];
        JsonObject? Fm(JsonObject e) => new() { ["parent"] = parent };
        string? Member(string a) => a.StartsWith("rev", StringComparison.Ordinal) ? "review" : "implement";
        JsonObject Link(string requester) => new() { ["task"] = child, ["requester"] = requester, ["peers"] = new JsonArray { JsonValue.FromString("rev-1@t") } };
        Assert.True(DoneReportScan.HasReview(parent, "impl-1@t", exchanges, Fm, static _ => "implement", [Link("impl-1@t")], Member));
        Assert.False(DoneReportScan.HasReview(parent, "impl-1@t", exchanges, Fm, static _ => "implement", [Link("impl-2@t")], Member));
    }

    private const string Item = "s/fix-it";
    private const string ReportAt = "2026-11-18T15:00:00Z";

    private static readonly double ReportedMs = Ah.Core.Time.Timestamps.TryParseMs(ReportAt, out var ms) ? ms : double.NaN;

    /// <summary>An exchange whose request front matter rides along under <c>fm</c>, so <see cref="FmOf"/> can read it back.</summary>
    private static JsonObject Req(string id, string to, string created, string item = Item, string team = "t", string pool = Pool, string? parent = null)
    {
        var fm = new JsonObject { ["item"] = item, ["team"] = team, ["created"] = created };
        if (parent is not null)
        {
            fm["parent"] = parent;
        }

        return new JsonObject { ["id"] = id, ["to"] = to, ["request"] = new JsonObject { ["path"] = $"{pool}/msgs/{id}--orchestrator--{to}.md" }, ["fm"] = fm };
    }

    private static JsonObject? FmOf(JsonObject e) => e.TryGetValue("fm", out var fm) ? fm.AsObject() : null;

    private static string? ClassOf(string role) => role == "reviewer" ? "review" : "implement";

    private static bool Reviewed(string id, IReadOnlyList<JsonObject> exchanges, double reportedMs = double.NaN, string? item = Item, Func<string, bool>? closed = null) =>
        DoneReportScan.HasReview(id, "impl-1@t", exchanges, FmOf, ClassOf, [], static _ => null, new ReviewScope("t", Pool, item, double.IsNaN(reportedMs) ? ReportedMs : reportedMs), closed);

    [Fact]
    public void OrchestratorReviewRequestOnTheItemCountsAndANonReviewOneDoesNot()
    {
        const string done = "20261118-145000-impl";
        var impl = Req(done, "implementor", "2026-11-18T14:50:00Z");
        Assert.True(Reviewed(done, [impl, Req("20261118-150500-rev1", "reviewer", "2026-11-18T15:05:00Z")]));
        Assert.True(Reviewed(done, [impl, Req("20261118-150000-same", "reviewer", ReportAt)], ReportedMs + 400)); // created's whole second
        Assert.False(Reviewed(done, [impl])); // no review request: alarm
        Assert.False(Reviewed(done, [impl, Req("20261118-150500-imp2", "implementor", "2026-11-18T15:05:00Z")])); // not a review role
        Assert.False(Reviewed(done, [impl, Req("20261118-150500-oth1", "reviewer", "2026-11-18T15:05:00Z", item: "s/other")])); // another item
        Assert.False(Reviewed(done, [impl, Req("20261118-150500-tm01", "reviewer", "2026-11-18T15:05:00Z", team: "u")])); // another team
        Assert.False(Reviewed(done, [impl, Req("20261118-150500-pl01", "reviewer", "2026-11-18T15:05:00Z", pool: "/other/.claude/ah2")])); // another pool
        Assert.False(Reviewed(done, [impl, Req("20261118-150500-rev1", "reviewer", "2026-11-18T15:05:00Z")], item: null)); // unlinked: today's rule only
    }

    [Fact]
    public void NewerDoneReportOnTheItemNeedsANewerReviewRequest()
    {
        const string first = "20261118-140000-impl";
        const string rework = "20261118-145000-impl";
        List<JsonObject> exchanges =
        [
            Req(first, "implementor", "2026-11-18T14:00:00Z"),
            Req("20261118-143000-rev1", "reviewer", "2026-11-18T14:30:00Z"),
            Req(rework, "implementor", "2026-11-18T14:50:00Z"),
        ];
        Assert.False(Reviewed(rework, exchanges)); // the review request is older than the rework's DONE
        exchanges.Add(Req("20261118-151000-rev2", "reviewer", "2026-11-18T15:10:00Z"));
        Assert.True(Reviewed(rework, exchanges));
    }

    [Fact]
    public void ALaterRoundOfAnEarlierLinkOnTheItemCountsOnlyWhenItEndedAtOrAfterTheReport()
    {
        const string first = "20261118-140000-impl";
        const string rework = "20261118-145000-impl";
        List<JsonObject> exchanges = [Req(first, "implementor", "2026-11-18T14:00:00Z"), Req(rework, "implementor", "2026-11-18T14:50:00Z")];
        static string? Member(string a) => a.StartsWith("rev", StringComparison.Ordinal) ? "review" : "implement";
        static JsonObject Link(string? endedAt, string task = first)
        {
            var link = new JsonObject { ["task"] = task, ["requester"] = "impl-1@t", ["peers"] = new JsonArray { JsonValue.FromString("rev-1@t") } };
            if (endedAt is not null)
            {
                link["round_ended_at"] = endedAt;
            }

            return link;
        }

        bool Covered(JsonObject link) =>
            DoneReportScan.HasReview(rework, "impl-1@t", exchanges, FmOf, ClassOf, [link], Member, new ReviewScope("t", Pool, Item, ReportedMs));

        Assert.True(Covered(Link("2026-11-18T15:20:00.000Z")));
        Assert.True(Covered(Link(ReportAt))); // at the report counts
        Assert.False(Covered(Link("2026-11-18T14:59:59.000Z"))); // a verdict before the DONE does not cover it
        Assert.False(Covered(Link(null))); // an older row without the field: today's rule only
        exchanges.Add(Req("20261118-130000-othr", "implementor", "2026-11-18T13:00:00Z", item: "s/other"));
        Assert.False(Covered(Link("2026-11-18T15:20:00.000Z", task: "20261118-130000-othr"))); // a link on another item
    }

    [Fact]
    public void ClosedItemNeverAlarmsAndAnOpenOneStillDoes()
    {
        const string done = "20261118-145000-impl";
        IReadOnlyList<JsonObject> exchanges = [Req(done, "implementor", "2026-11-18T14:50:00Z")];
        Assert.True(Reviewed(done, exchanges, closed: static i => i == Item));
        Assert.False(Reviewed(done, exchanges, closed: static _ => false));
        Assert.False(Reviewed(done, exchanges, item: null, closed: static _ => true)); // no item: nothing to look up
    }

    [Fact]
    public void ReviewRequestDownTheParentChainCountsAndOneOffItDoesNot()
    {
        const string done = "20261118-145000-impl";
        var impl = Req(done, "implementor", "2026-11-18T14:50:00Z");
        var mid = Req("20261118-145500-midl", "implementor", "2026-11-18T14:00:00Z", item: "s/other", parent: done);
        var rev = Req("20261118-145600-rev1", "reviewer", "2026-11-18T14:00:00Z", item: "s/other", parent: "20261118-145500-midl");
        Assert.True(Reviewed(done, [impl, mid, rev]));
        var stray = Req("20261118-145600-rev2", "reviewer", "2026-11-18T14:00:00Z", item: "s/other", parent: "20261118-145500-nope");
        Assert.False(Reviewed(done, [impl, mid, stray]));
    }

    [Fact]
    public void ScanHandsTheReviewTheReportsItemPoolAndTime()
    {
        var scan = NewScan();
        var store = new Store();
        Assert.Empty(scan.Scan([Pool], store.Exchanges(), new HashSet<string>(StringComparer.Ordinal) { "t" }, new HashSet<string>(StringComparer.Ordinal), Now, store.ScopeReads()));
        store.Add("20261118-150000-scop", Now - Minute, Done);
        var reads = store.ScopeReads() with { RequestFm = static _ => new JsonObject { ["from"] = "orchestrator", ["team"] = "t", ["item"] = Item } };
        _ = Assert.Single(scan.Scan([Pool], store.Exchanges(), new HashSet<string>(StringComparer.Ordinal) { "t" }, new HashSet<string>(StringComparer.Ordinal), Now, reads));
        Assert.Equal(new ReviewScope("t", Pool, Item, Now - Minute), store.LastScope);
    }
}
