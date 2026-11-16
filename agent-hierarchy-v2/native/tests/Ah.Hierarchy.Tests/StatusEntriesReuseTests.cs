using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>A status read parses only the rows written since the dir's last read; the rest are the rows that read parsed.</summary>
public sealed class StatusEntriesReuseTests
{
    private const long T0 = StatusEntriesFixture.T0;

    private sealed class StampedEntries : IStatusEntryStore
    {
        internal List<StatusEntryStamped> Rows { get; } = [];

        /// <summary>How many rows came back with their text.</summary>
        internal int TextRows { get; set; }

        public IReadOnlyList<StatusEntryStamped>? ReadNewerThan(string statusDir, long sinceMs)
        {
            var rows = Rows.Select(r => r with { Json = r.ChangedMs > sinceMs ? r.Json : null }).ToList();
            TextRows += rows.Count(static r => r.Json is not null);
            return rows;
        }

        public StatusEntriesDelta? ReadChangedSince(string statusDir, long sinceMs)
        {
            var rows = Rows.Where(r => r.ChangedMs > sinceMs).ToList();
            TextRows += rows.Count;
            return new StatusEntriesDelta(Rows.Count, Rows.Sum(static r => r.ChangedMs), rows);
        }

        public IReadOnlyList<StatusEntryRow> Read(string statusDir)
        {
            TextRows += Rows.Count;
            return [.. Rows.Select(static r => new StatusEntryRow(r.PoolDir, r.Kind, r.Key, r.Json!))];
        }

        public IReadOnlyList<string> StatusDirs() => [];

        public IReadOnlyList<StatusEntryRow> ReadKind(string poolDir, string kind, string? key = null) => [];

        public IReadOnlyList<string> Listing(string poolDir) => [];

        public IReadOnlyList<string> PoolsWith(string kind, string key) => [];

        public (bool Ok, string Message) Replace(
            IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs) =>
            (true, string.Empty);

        internal void Write(string key, string json, long changedMs)
        {
            _ = Rows.RemoveAll(r => r.Key == key);
            Rows.Add(new StatusEntryStamped("/d", "session", key, changedMs, json));
        }
    }

    private static string Value(List<StatusEntry> entries, string key) => JsonText.Stringify(entries.Single(e => e.Key == key).Json, 0);

    [Fact]
    public void ASecondReadParsesNoUnchangedRowAndNegativeANewerOrDifferentlyStampedRowIsRead()
    {
        var store = new StampedEntries();
        store.Rows.Add(new StatusEntryStamped("/d", "meta", "version", T0 - 60_000, "{\"version\":" + HierLib.StatusEntriesVersion + "}"));
        store.Write("s1", "{\"n\":1}", T0 - 60_000);
        store.Write("s2", "{\"n\":2}", T0 - 60_000);
        var lib = (HierLib)StatusEntriesFixture.Create().Services(nowMs: T0, entries: store).Status;

        Assert.Equal(3, lib.StoredStatusEntries("/d")!.Count);
        Assert.Equal(3, store.TextRows);

        store.TextRows = 0;
        var again = lib.StoredStatusEntries("/d")!;
        Assert.Equal(0, store.TextRows);
        Assert.Equal("{\"n\":1}", Value(again, "s1"));

        // negative: a row written since the last read comes back with its text
        store.Write("s1", "{\"n\":10}", T0 - 1_000);
        var newer = lib.StoredStatusEntries("/d")!;
        Assert.Equal(1, store.TextRows);
        Assert.Equal("{\"n\":10}", Value(newer, "s1"));

        // negative: a row stamped before the last read that the last read did not see (a late commit) makes the whole read fetch every row
        store.TextRows = 0;
        store.Write("s2", "{\"n\":20}", T0 - 50_000);
        var late = lib.StoredStatusEntries("/d")!;
        Assert.Equal("{\"n\":20}", Value(late, "s2"));
        Assert.True(store.TextRows >= 3, "every row was read again");
    }

    /// <summary>b459 step 27b: a source with the same exchange entries shares the held listing; negative: a changed exchange row rebuilds it.</summary>
    [Fact]
    public void AnUnchangedSourceSharesTheListingAndNegativeAChangedExchangeRowRebuildsIt()
    {
        var lib = (HierLib)StatusEntriesFixture.Create().Services(nowMs: T0, entries: new StampedEntries()).Status;
        static JsonValue Exchange(string slug) => JsonText.Parse(
            "{\"request\":{\"path\":\"/p/msgs/20260101-000001-x001--w--" + slug + "--request.md\",\"meta\":{\"id\":\"20260101-000001-x001\",\"to\":\"w\",\"slug\":\"" + slug + "\"}},\"response\":null}");
        var view = new StatusEntry("/d", StatusKind.View, "/p", JsonText.Parse("{\"ord\":0}"));
        var first = new StatusEntry("/p", StatusKind.Exchange, "20260101-000001-x001", Exchange("a"));

        var listing = new HierLib.EntryStatusSource(lib, "/d", [view, first]).Exchanges("/d");
        Assert.Same(listing, new HierLib.EntryStatusSource(lib, "/d", [view, first]).Exchanges("/d"));
        Assert.Equal(1, lib.ListingBuilds);

        var changed = new HierLib.EntryStatusSource(lib, "/d", [view, first with { Json = Exchange("b") }]).Exchanges("/d");
        Assert.NotSame(listing, changed);
        Assert.Equal("b", changed.Single().Member("slug")!.Value.AsString());
        Assert.Equal(2, lib.ListingBuilds);
    }

    /// <summary>b459 step 27: an unchanged dir reads no row; negative: a deleted row moves the count and is gone on the next read.</summary>
    [Fact]
    public void AnUnchangedDirReadsNoRowAndNegativeADeletedRowShowsOnTheNextRead()
    {
        var store = new StampedEntries();
        store.Rows.Add(new StatusEntryStamped("/d", "meta", "version", T0 - 60_000, "{\"version\":" + HierLib.StatusEntriesVersion + "}"));
        store.Write("s1", "{\"n\":1}", T0 - 60_000);
        store.Write("s2", "{\"n\":2}", T0 - 60_000);
        var lib = (HierLib)StatusEntriesFixture.Create().Services(nowMs: T0, entries: store).Status;
        _ = lib.StoredStatusEntries("/d");
        var (_, full) = lib.EntryReads;

        store.TextRows = 0;
        Assert.Equal(3, lib.StoredStatusEntries("/d")!.Count);
        Assert.Equal(0, store.TextRows);
        Assert.Equal(full, lib.EntryReads.Full);

        _ = store.Rows.RemoveAll(static r => r.Key == "s2");
        var after = lib.StoredStatusEntries("/d")!;
        Assert.DoesNotContain(after, static e => e.Key == "s2");
        Assert.Equal(full + 1, lib.EntryReads.Full);
    }

    /// <summary>ah2-1: a row two dirs read is parsed once; negative: the row written again, with another text and stamp, is parsed again.</summary>
    [Fact]
    public void ARowTwoDirsReadIsParsedOnceAndNegativeARewrittenRowIsParsedAgain()
    {
        var store = new StampedEntries();
        store.Rows.Add(new StatusEntryStamped("/d", "meta", "version", T0 - 60_000, "{\"version\":" + HierLib.StatusEntriesVersion + "}"));
        store.Write("s1", "{\"n\":1}", T0 - 60_000);
        var lib = (HierLib)StatusEntriesFixture.Create().Services(nowMs: T0, entries: store).Status;

        var first = lib.StoredStatusEntries("/d")!;
        Assert.Equal(2, lib.RowParses);

        // the fake gives every dir the same rows, as two dirs that view one pool get
        _ = lib.StoredStatusEntries("/e");
        Assert.Equal(2, lib.RowParses);

        store.Write("s1", "{\"n\":9}", T0 - 30_000);
        var again = lib.StoredStatusEntries("/d")!;
        Assert.Equal(3, lib.RowParses);
        Assert.Equal("{\"n\":1}", Value(first, "s1"));
        Assert.Equal("{\"n\":9}", Value(again, "s1"));
    }
}
