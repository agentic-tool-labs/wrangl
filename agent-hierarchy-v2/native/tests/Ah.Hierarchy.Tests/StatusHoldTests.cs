using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>b459 step 14: a dir's status inputs are held while its entries' stamp is unchanged; liveness and the clock stay live.</summary>
public sealed class StatusHoldTests
{
    /// <summary>The real store, counting the entry reads the hold must skip.</summary>
    private sealed class CountingEntries(IStatusEntryStore inner) : IStatusEntryStore
    {
        internal int Reads { get; set; }

        public IReadOnlyList<StatusEntryRow> Read(string statusDir)
        {
            Reads++;
            return inner.Read(statusDir);
        }

        public IReadOnlyList<StatusEntryStamped>? ReadNewerThan(string statusDir, long sinceMs)
        {
            Reads++;
            return inner.ReadNewerThan(statusDir, sinceMs);
        }

        public string? Revision(string statusDir) => inner.Revision(statusDir);

        public IReadOnlyList<string> StatusDirs() => inner.StatusDirs();

        public IReadOnlyList<StatusEntryRow> ReadKind(string poolDir, string kind, string? key = null) => inner.ReadKind(poolDir, kind, key);

        public IReadOnlyList<string> Listing(string poolDir) => inner.Listing(poolDir);

        public IReadOnlyList<string> PoolsWith(string kind, string key) => inner.PoolsWith(kind, key);

        public (bool Ok, string Message) Replace(
            IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs) =>
            inner.Replace(scopes, deletes, rows, changedMs);

        public (bool Ok, string Message) Replace(
            IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs,
            IReadOnlyList<string> touched, IReadOnlyDictionary<string, long>? expected) =>
            inner.Replace(scopes, deletes, rows, changedMs, touched, expected);

        public IReadOnlyDictionary<string, long> Sequences() => inner.Sequences();

        public IReadOnlyList<(string Key, string PoolDir)> DirtyMarks(long olderThanMs = 0) => inner.DirtyMarks(olderThanMs);

        public void ClearDirty(IReadOnlyList<string> keys) => inner.ClearDirty(keys);
    }

    private static string Text(JsonObject status)
    {
        status.Remove("written_at");
        status.Remove("expires_at");
        return JsonText.Stringify(status, 0);
    }

    /// <summary>A fresh compute from the stored entries, through no hold.</summary>
    private static string Fresh(HierLib lib, string dir, long now) => Text(lib.AssembleStatus(dir, lib.StoredStatusEntries(dir)!, now, true));

    [Fact]
    public void AHeldDirReadsNoEntriesAndEqualsAFreshComputeAsTheClockAdvances()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var store = new CountingEntries(Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home));
        var lib = (HierLib)fixture.Services(entries: store).Status;
        foreach (var dir in fixture.StatusDirs)
        {
            Assert.True(lib.RebuildStatusEntriesOf(dir).Ok);
        }

        foreach (var dir in fixture.StatusDirs)
        {
            _ = lib.ReadStatus(dir, StatusEntriesFixture.Nows[0], dir, true);
            var seen = new List<string>();
            foreach (var now in StatusEntriesFixture.Nows)
            {
                store.Reads = 0;
                var held = Text(lib.ReadStatus(dir, now, dir, true));
                Assert.Equal(0, store.Reads);
                Assert.Equal(Fresh(lib, dir, now), held);
                // a second read at the same clock is the same: nothing a compute changed leaked into the held rows
                Assert.Equal(held, Text(lib.ReadStatus(dir, now, dir, true)));
                seen.Add(held);
            }

            // the clock alone moves what is overdue (the main dir holds the ETA'd dispatch): the held reads are not one frozen answer
            Assert.True(dir != fixture.StatusDirs[0] || seen.Distinct().Count() > 1, dir);
        }
    }

    /// <summary>b459 step 15: the latest roster is held per dir by the peers counter and row count; pane attribution stays live.</summary>
    [Fact]
    public void AHeldRosterTakesANewRowAndNegativeAPaneChangeWithNoRosterWriteIsSeen()
    {
        var fixture = StatusEntriesFixture.Create();
        var lib = (HierLib)fixture.Services().Status;
        var dir = fixture.StatusDirs[0];
        static string? Text(JsonValue? v) => v is { IsText: true } t ? t.AsString() : null;
        string? SessionOf(string name) =>
            lib.LatestRoster(dir).Where(r => Text(r.Member("name")) == name).Select(r => Text(r.Member("session_id"))).FirstOrDefault();
        string? PaneName() =>
            lib.AttributedRoster(dir).Where(r => Text(r.Member("session_id")) == "s-pane").Select(r => Text(r.Member("name"))).FirstOrDefault();
        Assert.Equal("s-impl", SessionOf("t-implementor"));
        Assert.Equal("u-implementor", PaneName());

        fixture.Peers(dir, JsonText.Stringify(new JsonObject
        {
            ["type"] = "peer", ["status"] = "up", ["name"] = "t-implementor", ["role"] = "implementor", ["session_id"] = "s-new", ["team"] = "t", ["ts"] = "2026-05-28T21:00:00.000Z",
        }, 0));
        Assert.Equal("s-new", SessionOf("t-implementor"));

        // the member's pane moves in its team file; no roster row is written
        File.WriteAllText(Path.Combine(dir, "teams", "u.json"), $$"""{"team_id":"t-u","team":"u","orchestrator":{"pid":{{Environment.ProcessId}},"session_id":"o-u"},"members":[{"name":"u-implementor","role":"implementor","transport_id":"%8"}]}""" + "\n");
        Assert.Null(PaneName());
    }

    [Fact]
    public void NegativeANewEntryRowMovesTheStampAndShowsOnTheNextRead()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var store = Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home);
        var lib = (HierLib)fixture.Services(entries: store).Status;
        var dir = fixture.StatusDirs[0];
        Assert.True(lib.RebuildStatusEntriesOf(dir).Ok);
        var now = StatusEntriesFixture.Nows[0];
        var before = Text(lib.ReadStatus(dir, now, dir, true));

        var row = store.Read(dir).First(r => r.Kind != "meta");
        Assert.True(store.Replace([], [(row.PoolDir, row.Kind, row.Key)], [], now).Ok);

        var after = Text(lib.ReadStatus(dir, now, dir, true));
        Assert.Equal(Fresh(lib, dir, now), after);
        Assert.NotEqual(before, after);
    }

    /// <summary>b459 step 16: a new, a changed and a removed entry each patch the held source, and each read equals a fresh build.</summary>
    [Fact]
    public void ANewAChangedAndARemovedEntryEachPatchTheHeldSourceToAFreshBuild()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var store = Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home);
        var lib = (HierLib)fixture.Services(entries: store).Status;
        var dir = fixture.StatusDirs[0];
        Assert.True(lib.RebuildStatusEntriesOf(dir).Ok);
        var now = StatusEntriesFixture.Nows[0];
        var last = Text(lib.ReadStatus(dir, now, dir, true));
        // two exchange rows that show differently: removing one, adding it back, then giving it the other's value each move the status
        var exchanges = store.Read(dir).Where(r => r.Kind == StatusKind.Exchange).ToList();
        var row = exchanges[0];
        var other = exchanges.First(r => r.Json != row.Json);

        (string, string, string)[] none = [];
        var writes = new (string Name, Func<(bool Ok, string Message)> Write)[]
        {
            ("removed", () => store.Replace([], [(row.PoolDir, row.Kind, row.Key)], [], now)),
            ("new", () => store.Replace([], none, [row], now)),
            ("changed", () => store.Replace([], none, [row with { Json = other.Json }], now)),
        };
        foreach (var (name, write) in writes)
        {
            var patches = lib.StatusPatches;
            Assert.True(write().Ok, name);
            var read = Text(lib.ReadStatus(dir, now, dir, true));
            Assert.Equal(patches + 1, lib.StatusPatches);
            Assert.Equal(Fresh(lib, dir, now), read);
            Assert.NotEqual(last, read);
            last = read;
        }

        // the nearest input that must not patch: no write leaves the stamp, so the held source is read as it is
        var held = lib.StatusPatches;
        Assert.Equal(last, Text(lib.ReadStatus(dir, now, dir, true)));
        Assert.Equal(held, lib.StatusPatches);
    }

    /// <summary>b459 step 18: a dir not polled for over ten minutes loses its held source on the next poll of any dir, and builds the same status again.</summary>
    [Fact]
    public void ADirNotPolledForTenMinutesIsEvictedAndItsNextPollEqualsAFreshBuild()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(StatusEntriesFixture.T0));
        var lib = (HierLib)fixture.Services(entries: Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home), clock: clock).Status;
        var (idle, busy) = (fixture.StatusDirs[0], fixture.StatusDirs[1]);
        Assert.True(lib.RebuildStatusEntriesOf(idle).Ok);
        Assert.True(lib.RebuildStatusEntriesOf(busy).Ok);
        var now = StatusEntriesFixture.Nows[0];
        var before = Text(lib.ReadStatus(idle, now, idle, true));
        _ = lib.ReadStatus(busy, now, busy, true);
        Assert.Equal(2, lib.StatusHoldCount);

        // the nearest input that must not evict: exactly ten minutes idle
        clock.UtcNow = clock.UtcNow.AddMilliseconds(HierLib.StatusHoldIdleMs);
        _ = lib.ReadStatus(busy, now, busy, true);
        Assert.Equal(2, lib.StatusHoldCount);

        clock.UtcNow = clock.UtcNow.AddMilliseconds(1);
        _ = lib.ReadStatus(busy, now, busy, true);
        Assert.Equal(1, lib.StatusHoldCount);

        var after = Text(lib.ReadStatus(idle, now, idle, true));
        Assert.Equal(before, after);
        Assert.Equal(Fresh(lib, idle, now), after);
        Assert.Equal(2, lib.StatusHoldCount);
    }

    [Fact]
    public void NegativeAMemberWhoseProcessExitsShowsDeadWithNoStoreWrite()
    {
        var fixture = StatusEntriesFixture.Create();
        using var child = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false })!;
        try
        {
            fixture.Peers(fixture.StatusDirs[0], JsonText.Stringify(new JsonObject
            {
                ["type"] = "peer", ["status"] = "up", ["name"] = "t-implementor", ["role"] = "implementor", ["session_id"] = "s-impl", ["pid"] = child.Id, ["team"] = "t",
                ["ts"] = DateTimeOffset.FromUnixTimeMilliseconds(StatusEntriesFixture.T0 - 10_000).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            }, 0));
            using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
            var store = Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home);
            var lib = (HierLib)fixture.Services(entries: store).Status;
            var dir = fixture.StatusDirs[0];
            Assert.True(lib.RebuildStatusEntriesOf(dir).Ok);
            var now = StatusEntriesFixture.Nows[0];
            var alive = Text(lib.ReadStatus(dir, now, dir, true));
            var stamp = store.Revision(dir);

            child.Kill();
            child.WaitForExit();

            var dead = Text(lib.ReadStatus(dir, now, dir, true));
            Assert.Equal(stamp, store.Revision(dir));
            Assert.Equal(Fresh(lib, dir, now), dead);
            Assert.NotEqual(alive, dead);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }
}
