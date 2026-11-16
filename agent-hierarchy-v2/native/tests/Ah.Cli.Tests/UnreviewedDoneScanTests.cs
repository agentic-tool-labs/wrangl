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
}
