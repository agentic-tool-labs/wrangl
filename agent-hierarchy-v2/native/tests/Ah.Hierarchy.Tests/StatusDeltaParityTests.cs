using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// ah-msgs-fast-2 §3.8 S8b merge gate: seeded random writes through the real domain ops (their AfterWrite deltas run), each followed by,
/// for every status dir at every fixture time, the status assembled from the stored (delta-maintained) entries compared byte for byte with
/// the status assembled from a fresh rebuild.
/// </summary>
public sealed class StatusDeltaParityTests
{
    private const int Writes = 300;

    private static readonly string[] Kinds =
        ["request", "response", "fill", "retire", "archive", "settle", "peer", "nudge", "dispatch", "decision", "activity", "session", "team", "backlog", "register", "unregister"];

    [Fact]
    public void EveryDeltaLeavesEveryStatusDirEqualToARebuildAtEveryTime()
    {
        var rig = new Rig();
        var done = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var step = 0; step < Writes; step++)
        {
            var kind = Kinds[rig.Rng.Next(Kinds.Length)];
            if (rig.Write(kind, step))
            {
                done[kind] = done.GetValueOrDefault(kind) + 1;
            }

            rig.AssertParity(step, kind);
        }

        Assert.False(File.Exists(Path.Combine(StatusEntriesFixture.Pool(rig.Fixture.W), "status.json")), "a linked tree's H got a status.json");
        Assert.True(rig.DispatchRows >= Writes, $"{rig.DispatchRows} last-dispatch rows compared");

        // every kind of write landed, so the gate is not passing on refused ops
        Assert.All(Kinds, k => Assert.True(done.GetValueOrDefault(k) >= 3, $"{k}: {done.GetValueOrDefault(k)} writes landed; refused: {string.Join(" | ", rig.Refused.Select(static r => r.Key + ": " + r.Value))}"));
    }

    /// <summary>
    /// A message create, a fill and a dispatch row each run their status delta on the store. This no longer bounds how many messages the status side reads.
    /// </summary>
    [Fact]
    public void AMessageCreateAndFillAndADispatchRowEachRunTheirDeltaOnTheStore()
    {
        var rig = new Rig();
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        var id = "20260601-000001-path";
        var request = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(id, "implementor", "feat"));
        var text = StatusEntriesFixture.RequestText(id, "implementor", "feat", "t", StatusEntriesFixture.T0 - 1_000, "t-implementor");
        File.WriteAllText(request, text);

        rig.Lib.AfterStatusWrite("msgs", "create", Args(new() { ["dir"] = dir, ["path"] = request, ["content"] = text }), Args(new() { ["ok"] = true }), dir);

        Assert.Contains(rig.Lib.StoredStatusEntries(dir)!, e => e.Kind == StatusKind.Exchange && e.Key == id && e.Json.Member("fm")?.Member("team") is { IsText: true });

        var response = Path.Combine(dir, "msgs", StatusEntriesFixture.ResponseName(id, "feat"));
        File.WriteAllText(response, StatusEntriesFixture.ResponseText(id, "implementor", "feat", "t", StatusEntriesFixture.T0, "- DONE it\n"));

        rig.Lib.AfterStatusWrite("msgs", "fill", Args(new() { ["dir"] = dir, ["path"] = response, ["req"] = request }), Args(new() { ["ok"] = true }), dir);

        // F3: the dispatch row of a request with no to_name moves its member from that one request's records: no message is read
        var unnamed = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName("20260601-000002-path", "implementor", "feat"));
        var unnamedText = StatusEntriesFixture.RequestText("20260601-000002-path", "implementor", "feat", "t", StatusEntriesFixture.T0 - 500, null);
        File.WriteAllText(unnamed, unnamedText);
        rig.Lib.AfterStatusWrite("msgs", "create", Args(new() { ["dir"] = dir, ["path"] = unnamed, ["content"] = unnamedText }), Args(new() { ["ok"] = true }), dir);

        var rec = new JsonObject { ["type"] = "dispatch", ["session_id"] = "o-t", ["request_id"] = "20260601-000002-path", ["to"] = "implementor", ["to_addr"] = "t-implementor@t" };
        rig.Lib.AfterStatusWrite("peer-records", "append", Args(new() { ["rec"] = rec }), Args(new() { ["ok"] = true }), null);

        Assert.Contains(rig.Lib.StoredStatusEntries(dir)!, e => e.Kind == StatusKind.Exchange && e.Key == "20260601-000002-path");
    }

    /// <summary>b442 Amendment 1: a status dir read through a symlink gets the gate delta a write through the link's target makes; another checkout's does not.</summary>
    [Fact]
    public void AStatusDirReadThroughASymlinkGetsTheDeltaOfAWriteThroughItsTarget()
    {
        var rig = new Rig();
        var link = Path.Combine(rig.Fixture.Root, "r-link");
        _ = Directory.CreateSymbolicLink(link, rig.Fixture.R);
        var viaLink = StatusEntriesFixture.Pool(link);
        Assert.True(rig.Lib.RebuildStatusEntriesOf(viaLink).Ok);

        var target = StatusEntriesFixture.Pool(rig.Fixture.R);
        rig.Fixture.Gates(target, "{\"type\":\"liveness-nudge\",\"request_id\":\"20260601-000009-link\"}");
        rig.Lib.AfterStatusWrite("gates", "put", Args(new() { ["dir"] = target }), Args(new() { ["appended"] = true }), target);

        Assert.Contains(rig.Lib.StoredStatusEntries(viaLink)!, e => e.Kind == StatusKind.Gate && e.Key == "20260601-000009-link");
        // the negative: an unrelated checkout's status shows no such nudge
        Assert.DoesNotContain(rig.Lib.StoredStatusEntries(StatusEntriesFixture.Pool(rig.Fixture.Q))!, e => e.Kind == StatusKind.Gate && e.Key == "20260601-000009-link");
    }

    /// <summary>
    /// H1 ruling B: a db-mode create commits, its AfterWrite never runs (a crash), and the next status-dir refresh's heal rebuilds so
    /// assemble == rebuild again. Negative: a create whose AfterWrite runs leaves no mark for the heal.
    /// </summary>
    [Fact]
    public void ADatabaseWriteThatCrashesBeforeItsDeltaIsHealedByTheNextRefreshAndOneThatDoesNotLeavesNoMark()
    {
        var rig = new Rig();
        var home = rig.Fixture.Home;
        Assert.True(StateDbModule.OpenDirect(home).Write(tx =>
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.msgs', 'db')");
            return true;
        }).Ok);
        var store = StateDbModule.CreateStatusEntries(home);
        var create = rig.Domains.First(static d => d.Name == "msgs").Ops.First(static o => o.Name == "create").DbBody!;
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);

        OpCall Create(string id, out ReadOnlyMemory<byte> args)
        {
            var path = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(id, "implementor", "feat"));
            args = Args(new() { ["dir"] = dir, ["path"] = path, ["content"] = StatusEntriesFixture.RequestText(id, "implementor", "feat", "t", StatusEntriesFixture.T0, "t-implementor") });
            var call = new OpCall("create", args, new RequestContext(dir, dir, dir, string.Empty), id);
            Assert.Equal(DbOpStatus.Done, StateDbModule.CreateOpRunner(home).Run(new DbOpRequest("msgs", id, "direct", tx => create(call, tx))).Status);
            return call;
        }

        bool Holds(string id) => rig.Lib.StoredStatusEntries(dir)!.Any(e => e.Kind == StatusKind.Exchange && e.Key == id);

        // negative: the write's AfterWrite ran, so it cleared its own mark
        _ = Create("20260601-000001-safe", out var safeArgs);
        rig.Lib.AfterStatusWrite("msgs", "create", safeArgs, Args(new() { ["ok"] = true }), dir);
        Assert.Empty(store.DirtyMarks());
        Assert.True(Holds("20260601-000001-safe"));

        // the crash: committed, no AfterWrite, so the stored entries miss it until the heal
        _ = Create("20260601-000002-lost", out _);
        Assert.Single(store.DirtyMarks());
        Assert.False(Holds("20260601-000002-lost"));

        // F2 negative: a mark younger than the grace (a write still in its POST) is not healed, and the heal never rebuilds inline
        var scheduled = new List<Action>();
        rig.Lib.RunInBackground = scheduled.Add;
        rig.Lib.HealDirtyStatus();
        Assert.Empty(scheduled);
        rig.Lib.DirtyGraceMs = 0;
        rig.Lib.HealDirtyStatus();
        Assert.NotEmpty(scheduled);
        Assert.False(Holds("20260601-000002-lost"));

        // F5 negative: a rebuild that does not land leaves the mark
        Exec(home, "ALTER TABLE status_entries RENAME TO status_entries_away");
        RunAll(scheduled);
        Exec(home, "ALTER TABLE status_entries_away RENAME TO status_entries");
        Assert.Single(store.DirtyMarks());

        rig.Lib.HealDirtyStatus();
        RunAll(scheduled);

        Assert.Empty(store.DirtyMarks());
        Assert.True(Holds("20260601-000002-lost"));
        rig.AssertParity(0, "heal");
    }

    private static void RunAll(List<Action> scheduled)
    {
        var now = scheduled.ToList();
        scheduled.Clear();
        now.ForEach(static a => a());
    }

    private static void Exec(string home, string sql) =>
        Assert.True(StateDbModule.OpenDirect(home).Write(tx =>
        {
            tx.Execute(sql);
            return true;
        }).Ok);

    /// <summary>
    /// F3: the first dispatch record of a request with no to_name reads O(1) stored exchange rows (its own), however many the pool holds.
    /// </summary>
    [Fact]
    public void AFirstDispatchOfAnUnnamedRequestReadsOnlyItsOwnStoredRow()
    {
        CountingEntries? counting = null;
        var rig = new Rig(entries: s => counting = new CountingEntries(s));
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        const string Id = "20260601-000004-once";
        var path = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(Id, "implementor", "feat"));
        var text = StatusEntriesFixture.RequestText(Id, "implementor", "feat", "t", StatusEntriesFixture.T0 + 1_000, null);
        Assert.True(rig.CreateRequest(dir, path, text));
        var pool = rig.Lib.StoredStatusEntries(dir)!.Count(e => e.Pool == dir && e.Kind == StatusKind.Exchange);
        Assert.True(pool > 3, $"{pool} stored exchange rows: the bound is not vacuous");
        counting!.ExchangeRows = 0;

        var rec = new JsonObject
        {
            ["type"] = "dispatch", ["session_id"] = "o-t", ["request_id"] = Id, ["to"] = "implementor", ["to_addr"] = "t-implementor@t",
            ["created"] = StatusEntriesFixture.Iso(StatusEntriesFixture.T0 + 1_000),
        };
        Assert.True(rig.AppendRecord(dir, rec));

        Assert.InRange(counting.ExchangeRows, 0, 1);

        // and the member moved: its row is the request's created
        Assert.Contains(rig.Lib.StoredStatusEntries(dir)!, e => e.Kind == StatusKind.LastDispatch && e.Json.Member("name") is { IsText: true } n && n.AsString() == "t-implementor"
            && e.Json.Member("ms") is { IsNumber: true } ms && ms.AsNumber() == StatusEntriesFixture.T0 + 1_000);
    }

    /// <summary>
    /// F1 (mutant G): a write to a never-built status dir's pool while its first rebuild is reading writes no entry (no status dir lists the
    /// pool yet) but still bumps its write sequence, so that rebuild does not land; the next one holds the write.
    /// </summary>
    [Fact]
    public void AWriteToANeverBuiltDirDuringItsFirstRebuildKeepsThatRebuildFromLanding()
    {
        var files = new CountingFiles(Ah.Platform.PlatformModule.CreateIo(null).Files);
        var rig = new Rig(files);
        var dir = StatusEntriesFixture.Pool(rig.Fixture.Q);
        Exec(rig.Fixture.Home, $"DELETE FROM status_entries WHERE pool_dir = '{dir}' OR (kind = 'view' AND key = '{dir}')");
        Assert.Null(rig.Lib.StoredStatusEntries(dir));
        const string Id = "20260601-000005-fres";
        var request = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(Id, "implementor", "feat"));
        var text = StatusEntriesFixture.RequestText(Id, "implementor", "feat", "t", StatusEntriesFixture.T0 - 1_000, "t-implementor");
        var raced = false;
        rig.BeforeStoreRead = () =>
        {
            if (!raced)
            {
                raced = true;
                Assert.True(rig.CreateRequest(dir, request, text));
            }
        };

        Assert.False(rig.Lib.RebuildStatusEntriesOf(dir).Ok);
        rig.BeforeStoreRead = null;
        Assert.True(raced);

        Assert.True(rig.Lib.RebuildStatusEntriesOf(dir).Ok);
        Assert.Contains(rig.Lib.StoredStatusEntries(dir)!, e => e.Kind == StatusKind.Exchange && e.Key == Id);
    }

    /// <summary>
    /// R2-1 ruling P: in the daemon, writes keep landing while a rebuild reads (a busy team), and the rebuild still lands: the deltas it saw
    /// are re-run on its rows, so every write is held, the stored entries equal a rebuild, and its crash mark is gone.
    /// </summary>
    [Fact]
    public void ABusyTeamsRebuildStillLandsAndReplaysTheWritesThatLandedDuringIt()
    {
        var files = new CountingFiles(Ah.Platform.PlatformModule.CreateIo(null).Files);
        var rig = new Rig(files);
        var scheduled = new List<Action>();
        rig.Lib.RunInBackground = scheduled.Add;
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        var ids = new List<string>();
        rig.BeforeStoreRead = () =>
        {
            if (ids.Count < 4)
            {
                var id = $"20260601-00001{ids.Count}-busy";
                ids.Add(id);
                var request = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(id, "implementor", "feat"));
                Assert.True(rig.CreateRequest(dir, request, StatusEntriesFixture.RequestText(id, "implementor", "feat", "t", StatusEntriesFixture.T0 + ids.Count, "t-implementor")));
            }
        };

        var landed = rig.Lib.RebuildStatusEntriesOf(dir);
        rig.BeforeStoreRead = null;

        Assert.True(landed.Ok, landed.Message);
        Assert.Equal(4, ids.Count);
        var stored = rig.Lib.StoredStatusEntries(dir)!;
        Assert.All(ids, id => Assert.Contains(stored, e => e.Kind == StatusKind.Exchange && e.Key == id));
        Assert.DoesNotContain(StateDbModule.CreateStatusEntries(rig.Fixture.Home).DirtyMarks(), m => m.Key.Contains("rebuild:", StringComparison.Ordinal));
        rig.Lib.RunInBackground = null;
        rig.AssertParity(0, "busy");
    }

    /// <summary>
    /// R3-4 (mutant K): a daemon rebuild that commits and then dies before its replay leaves its crash mark, so the write that landed
    /// during it is missing from the stored entries only until the graced heal rebuilds the dir. Negative: inside the grace it is left alone.
    /// </summary>
    [Fact]
    public void ARebuildThatDiesBeforeItsReplayIsHealedFromItsMark()
    {
        CountingEntries? store = null;
        var files = new CountingFiles(Ah.Platform.PlatformModule.CreateIo(null).Files);
        var rig = new Rig(files, entries: s => store = new CountingEntries(s));
        var scheduled = new List<Action>();
        rig.Lib.RunInBackground = scheduled.Add;
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        const string Id = "20260601-000020-dies";
        var request = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(Id, "implementor", "feat"));
        // the write lands at the rebuild's last message read, after every listing, so the rebuild's own rows miss it and only the replay holds it
        var reads = 0;
        rig.BeforeMsgRead = () => reads++;
        Assert.True(rig.Lib.RebuildStatusEntriesOf(dir).Ok);
        var last = reads;
        reads = 0;
        var raced = false;
        rig.BeforeMsgRead = () =>
        {
            if (++reads == last)
            {
                raced = true;
                Assert.True(rig.CreateRequest(dir, request, StatusEntriesFixture.RequestText(Id, "implementor", "feat", "t", StatusEntriesFixture.T0 + 1, "t-implementor")));
            }
        };

        store!.CrashAfterRebuildCommit = true;
        _ = Assert.Throws<InvalidOperationException>(() => rig.Lib.RebuildStatusEntriesOf(dir));
        rig.BeforeMsgRead = null;
        Assert.True(raced);
        bool Holds() => rig.Lib.StoredStatusEntries(dir)!.Any(e => e.Kind == StatusKind.Exchange && e.Key == Id);
        Assert.False(Holds());
        var marks = StateDbModule.CreateStatusEntries(rig.Fixture.Home);
        Assert.Contains(marks.DirtyMarks(), m => m.Key.EndsWith("rebuild:" + dir, StringComparison.Ordinal));

        rig.Lib.HealDirtyStatus();
        Assert.Empty(scheduled);
        rig.Lib.DirtyGraceMs = 0;
        rig.Lib.HealDirtyStatus();
        RunAll(scheduled);

        Assert.True(Holds());
        Assert.Empty(marks.DirtyMarks());
        rig.Lib.RunInBackground = null;
        rig.AssertParity(0, "died");
    }

    /// <summary>
    /// R5: a write that commits after a daemon rebuild sets its crash mark, before its Replace, is counted in the rebuild's expected
    /// sequences, so the rebuild still lands, and the write is held through the replay. Mutant: the sequences fixed before the mark.
    /// </summary>
    [Fact]
    public void AWriteBetweenARebuildsMarkAndItsReplaceDoesNotKeepItFromLanding()
    {
        CountingEntries? store = null;
        var rig = new Rig(entries: s => store = new CountingEntries(s));
        rig.Lib.RunInBackground = static _ => { };
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        const string Id = "20260601-000050-mark";
        var request = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(Id, "implementor", "feat"));
        var wrote = false;
        store!.AfterMarkDirty = () => wrote = rig.CreateRequest(dir, request, StatusEntriesFixture.RequestText(Id, "implementor", "feat", "t", StatusEntriesFixture.T0 + 1, "t-implementor"));

        var landed = rig.Lib.RebuildStatusEntriesOf(dir);

        Assert.True(wrote);
        Assert.True(landed.Ok, landed.Message);
        Assert.Contains(rig.Lib.StoredStatusEntries(dir)!, e => e.Kind == StatusKind.Exchange && e.Key == Id);
        rig.Lib.RunInBackground = null;
        rig.AssertParity(0, "mark");
    }

    /// <summary>
    /// R4-1: a write that lands while a rebuild replays is recorded too and re-run after the older replayed ones. S registers and
    /// unregisters while the rebuild reads, then registers again between their replays: the replayed unregister (which deletes the row
    /// without a re-read) is followed by the re-run register, so S stays listed and the entries equal a rebuild. Mutant: the log closed at
    /// the commit.
    /// </summary>
    [Fact]
    public void AWriteDuringARebuildsReplayIsReRunAfterTheOlderOnes()
    {
        CountingEntries? store = null;
        var files = new CountingFiles(Ah.Platform.PlatformModule.CreateIo(null).Files);
        var rig = new Rig(files, entries: s => store = new CountingEntries(s));
        rig.Lib.RunInBackground = static _ => { };
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        var registered = false;
        rig.BeforeStoreRead = () =>
        {
            if (!registered)
            {
                registered = rig.RegisterSession("s-impl", 40) && rig.Unregister("s-impl");
            }
        };
        // S registers again between the replayed register and the replayed unregister (inside the first replayed delta's write)
        var again = false;
        store!.OnReplayWrite = () => again = rig.RegisterSession("s-impl", 41);

        store.RebuildCommitted = false;
        var landed = rig.Lib.RebuildStatusEntriesOf(dir);
        rig.BeforeStoreRead = null;

        Assert.True(landed.Ok, landed.Message);
        Assert.True(registered && again);
        Assert.Contains(StateDbModule.CreateStatusEntries(rig.Fixture.Home).ReadKind(string.Empty, StatusKind.Registration), static r => r.Key == "s-impl");
        rig.Lib.RunInBackground = null;
        rig.AssertParity(0, "order");
    }

    /// <summary>
    /// R3-2: a live write whose delta starts while a rebuild replays waits for the replayed delta in hand (one at a time), never runs
    /// alongside it; both writes are held and the entries equal a rebuild.
    /// </summary>
    [Fact]
    public void ALiveDeltaDuringTheReplayWaitsForTheReplayedOne()
    {
        CountingEntries? store = null;
        var files = new CountingFiles(Ah.Platform.PlatformModule.CreateIo(null).Files);
        var rig = new Rig(files, entries: s => store = new CountingEntries(s));
        rig.Lib.RunInBackground = static _ => { };
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        string Request(string id) => Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(id, "implementor", "feat"));
        string Text(string id, long t) => StatusEntriesFixture.RequestText(id, "implementor", "feat", "t", StatusEntriesFixture.T0 + t, "t-implementor");
        const string During = "20260601-000030-durg";
        const string Live = "20260601-000031-live";
        var raced = false;
        Thread? live = null;
        bool? liveEndedDuringReplay = null;
        rig.BeforeStoreRead = () =>
        {
            if (!raced)
            {
                raced = true;
                Assert.True(rig.CreateRequest(dir, Request(During), Text(During, 1)));
            }
        };
        store!.OnReplayWrite = () =>
        {
            live = new Thread(() => rig.CreateRequest(dir, Request(Live), Text(Live, 2)));
            live.Start();
            liveEndedDuringReplay = live.Join(2_000);
        };

        store.RebuildCommitted = false;
        var landed = rig.Lib.RebuildStatusEntriesOf(dir);
        live?.Join();
        rig.BeforeStoreRead = null;

        Assert.True(landed.Ok, landed.Message);
        Assert.False(liveEndedDuringReplay ?? true, "the replay read no message, or the live delta ran alongside it");
        var stored = rig.Lib.StoredStatusEntries(dir)!;
        Assert.Contains(stored, e => e.Kind == StatusKind.Exchange && e.Key == During);
        Assert.Contains(stored, e => e.Kind == StatusKind.Exchange && e.Key == Live);
        rig.Lib.RunInBackground = null;
        rig.AssertParity(0, "live");
    }

    /// <summary>
    /// R3-1: rebuilds of several dirs scheduled at once (a global mark's heal reaches every status dir) run one at a time in one queue,
    /// never as one background task each, and every dir lands.
    /// </summary>
    [Fact]
    public void RebuildsOfSeveralDirsRunOneAtATimeAndAllLand()
    {
        var rig = new Rig();
        var scheduled = new List<Action>();
        rig.Lib.RunInBackground = scheduled.Add;
        var marks = StateDbModule.CreateStatusEntries(rig.Fixture.Home);
        var dirs = marks.StatusDirs();
        Assert.True(dirs.Count > 1, $"{dirs.Count} status dirs: the queue is not vacuous");
        marks.MarkDirty(StatusDirty.Prefix + "test:global", string.Empty);
        rig.Lib.DirtyGraceMs = 0;

        rig.Lib.HealDirtyStatus();

        _ = Assert.Single(scheduled);
        RunAll(scheduled);
        Assert.Empty(scheduled);
        Assert.Empty(marks.DirtyMarks());
        Assert.All(dirs, d => Assert.NotNull(rig.Lib.StoredStatusEntries(d)));
        rig.Lib.RunInBackground = null;
        rig.AssertParity(0, "queue");
    }

    /// <summary>Several marks on one pool share one pass and all clear. Negative: a pass that does not land clears none of them, and the next pass clears them all.</summary>
    [Fact]
    public void MarksOnOnePoolShareOneRebuildPassAndNegativeAPassThatFailsClearsNone()
    {
        var rig = new Rig();
        var scheduled = new List<Action>();
        rig.Lib.RunInBackground = scheduled.Add;
        var marks = StateDbModule.CreateStatusEntries(rig.Fixture.Home);
        var pool = StatusEntriesFixture.Pool(rig.Fixture.R);
        Assert.True(marks.Listing(pool).Count > 0, "the pool has status dirs: the pass is not vacuous");
        foreach (var name in new[] { "a", "b", "c" })
        {
            marks.MarkDirty(StatusDirty.Prefix + "test:" + name, pool);
        }

        rig.Lib.DirtyGraceMs = 0;
        rig.Lib.HealDirtyStatus();
        _ = Assert.Single(scheduled);

        Exec(rig.Fixture.Home, "ALTER TABLE status_entries RENAME TO status_entries_away");
        RunAll(scheduled);
        Exec(rig.Fixture.Home, "ALTER TABLE status_entries_away RENAME TO status_entries");
        Assert.Equal(3, marks.DirtyMarks().Count(static m => m.Key.Contains("test:", StringComparison.Ordinal)));

        rig.Lib.HealDirtyStatus();
        _ = Assert.Single(scheduled);
        RunAll(scheduled);

        Assert.Empty(marks.DirtyMarks());
        rig.Lib.RunInBackground = null;
        rig.AssertParity(0, "shared pass");
    }

    /// <summary>A shared read keeps a message's text while the store's msgs counter is unchanged. Negative: once the counter moves, the stored text is read again.</summary>
    [Fact]
    public void ASharedTextReadIsKeptUntilTheMsgsCounterMovesAndNegativeAMovedCounterReadsAgain()
    {
        var rig = new Rig();
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        const string Id = "20260601-000001-memo";
        Assert.True(rig.CreateRequest(dir, Id, "q-memo", "2026-06-01T10:00:00Z"));
        var path = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(Id, "implementor", "feat"));
        string Shared()
        {
            using (rig.Lib.Core.ShareMsgReads())
            {
                return rig.Lib.Core.ReadMsgTextShared(path);
            }
        }

        var first = Shared();
        Assert.Contains("q-memo", first, StringComparison.Ordinal);
        Exec(rig.Fixture.Home, $"UPDATE msg_bodies SET bytes = CAST('changed behind the counter' AS BLOB) WHERE msg_pk IN (SELECT pk FROM msg_messages WHERE id = '{Id}')");
        Assert.Equal(first, Shared());

        Exec(rig.Fixture.Home, "UPDATE domain_seq SET seq = seq + 1 WHERE domain = 'msgs'");
        Assert.Equal("changed behind the counter", Shared());
    }

    /// <summary>The open run's read gives the anchor request's <c>started_at</c>, and other exchanges do not change it. Negative: a team with no open run reads null.</summary>
    [Fact]
    public void APipelineRunReadsItsAnchorsStartAndNegativeATeamWithNoRunReadsNull()
    {
        var rig = new Rig();
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        JsonValue Run(string team) => rig.Lib.DecisionsPipelineRunOp(new JsonObject { ["dir"] = dir, ["team"] = team });

        var before = Run("t");
        Assert.True(before.Member("started_at") is { IsText: true }, JsonText.Stringify(before, 0));
        for (var i = 1; i < 11; i++)
        {
            Assert.True(rig.CreateRequest(dir, $"20260601-0000{i:00}-more", "q-more", "2026-06-01T10:00:00Z"));
        }

        Assert.Equal(JsonText.Stringify(before, 0), JsonText.Stringify(Run("t"), 0));
        Assert.True(Run("no-such-team").IsNull);
    }

    /// <summary>The rebuild's last-dispatch read opens the message store once, however many requests the pool holds. Negative: the rows stay equal to the writes' own.</summary>
    [Fact]
    public void ALastDispatchRebuildOpensTheStoreOnceHoweverManyRequests()
    {
        var rig = new Rig();
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        var opens = 0;
        int Opens()
        {
            opens = 0;
            rig.BeforeStoreRead = () => opens++;
            _ = rig.Lib.LastDispatchEntries(dir);
            rig.BeforeStoreRead = null;
            return opens;
        }

        Assert.True(rig.CreateRequest(dir, "20260601-000001-once", "q-few", "2026-06-01T10:00:00Z"));
        var few = Opens();
        for (var i = 2; i < 12; i++)
        {
            Assert.True(rig.CreateRequest(dir, $"20260601-0000{i:00}-many", "q-many", "2026-06-01T10:00:00Z"));
        }

        Assert.Equal(few, Opens());
        rig.AssertParity(0, "last dispatch");
    }

    /// <summary>A store that counts the exchange rows read through it.</summary>
    private sealed class CountingEntries(IStatusEntryStore inner) : IStatusEntryStore
    {
        internal int ExchangeRows { get; set; }

        public IReadOnlyList<StatusEntryRow> Read(string statusDir) => inner.Read(statusDir);

        public IReadOnlyList<string> StatusDirs() => inner.StatusDirs();

        public IReadOnlyList<StatusEntryRow> ReadKind(string poolDir, string kind, string? key = null)
        {
            var rows = inner.ReadKind(poolDir, kind, key);
            ExchangeRows += kind == StatusKind.Exchange ? rows.Count : 0;
            return rows;
        }

        public IReadOnlyList<string> Listing(string poolDir) => inner.Listing(poolDir);

        public IReadOnlyList<string> PoolsWith(string kind, string key) => inner.PoolsWith(kind, key);

        public (bool Ok, string Message) Replace(
            IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs) =>
            inner.Replace(scopes, deletes, rows, changedMs);

        public (bool Ok, string Message) Replace(
            IReadOnlyList<(string PoolDir, string Kind)> scopes, IReadOnlyList<(string PoolDir, string Kind, string Key)> deletes, IReadOnlyList<StatusEntryRow> rows, long changedMs,
            IReadOnlyList<string> touched, IReadOnlyDictionary<string, long>? expected)
        {
            if (expected is null && RebuildCommitted && Interlocked.Exchange(ref _onReplayWrite, null) is { } hook)
            {
                hook();
            }

            var landed = inner.Replace(scopes, deletes, rows, changedMs, touched, expected);
            RebuildCommitted |= expected is not null && landed.Ok;
            if (expected is not null && landed.Ok && Interlocked.Exchange(ref _afterRebuildCommit, null) is { } after)
            {
                after();
            }
            if (expected is not null && CrashAfterRebuildCommit)
            {
                CrashAfterRebuildCommit = false;
                throw new InvalidOperationException("crash between the rebuild's commit and its replay");
            }

            return landed;
        }

        /// <summary>The next guarded (rebuild) Replace commits, then throws: the process dies before the replay and the mark's clear.</summary>
        internal bool CrashAfterRebuildCommit { get; set; }

        /// <summary>Whether a guarded (rebuild) Replace has committed: what runs after it is the replay. Reset it before the rebuild under test (the rig's own first rebuild sets it).</summary>
        internal volatile bool RebuildCommitted;

        private Action? _onReplayWrite;

        /// <summary>Runs once, before the first delta write after a rebuild commit: inside the replay.</summary>
        internal Action? OnReplayWrite { set => _onReplayWrite = value; }

        private Action? _afterRebuildCommit;

        /// <summary>Runs once, right after a rebuild's commit, before its replay.</summary>
        internal Action? AfterRebuildCommit { set => _afterRebuildCommit = value; }

        public IReadOnlyDictionary<string, long> Sequences() => inner.Sequences();

        public IReadOnlyList<(string Key, string PoolDir)> DirtyMarks(long olderThanMs = 0) => inner.DirtyMarks(olderThanMs);

        public void ClearDirty(IReadOnlyList<string> keys) => inner.ClearDirty(keys);

        public void MarkDirty(string key, string poolDir)
        {
            inner.MarkDirty(key, poolDir);
            Interlocked.Exchange(ref _afterMarkDirty, null)?.Invoke();
        }

        private Action? _afterMarkDirty;

        /// <summary>Runs once, right after a rebuild sets its crash mark: between the mark and the rebuild's Replace.</summary>
        internal Action? AfterMarkDirty { set => _afterMarkDirty = value; }
    }

    /// <summary>
    /// F1: a delta that lands while a rebuild is reading is never overwritten: the rebuild, whose snapshot began before it, does not commit,
    /// and the stored entries still hold the delta's row and equal a rebuild. (The negative, a rebuild nothing overtook, commits: every
    /// <see cref="Rig"/> starts with one.)
    /// </summary>
    [Fact]
    public void ADeltaThatLandsDuringARebuildIsNotOverwrittenByIt()
    {
        var files = new CountingFiles(Ah.Platform.PlatformModule.CreateIo(null).Files);
        var rig = new Rig(files);
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        const string Id = "20260601-000003-race";
        var request = Path.Combine(dir, "msgs", StatusEntriesFixture.RequestName(Id, "implementor", "feat"));
        var text = StatusEntriesFixture.RequestText(Id, "implementor", "feat", "t", StatusEntriesFixture.T0 - 1_000, "t-implementor");
        var raced = false;
        rig.BeforeStoreRead = () =>
        {
            if (!raced)
            {
                raced = true;
                File.WriteAllText(request, text);
                rig.Lib.AfterStatusWrite("msgs", "create", Args(new() { ["dir"] = dir, ["path"] = request, ["content"] = text }), Args(new() { ["ok"] = true }), dir);
            }
        };

        Assert.False(rig.Lib.RebuildStatusEntriesOf(dir).Ok);
        rig.BeforeStoreRead = null;

        Assert.True(raced);
        Assert.Contains(rig.Lib.StoredStatusEntries(dir)!, e => e.Kind == StatusKind.Exchange && e.Key == Id);
        rig.AssertParity(0, "race");
    }

    /// <summary>S8c readers: <c>ReadStatus</c> assembles from current stored entries; negative: with no current <c>meta</c> it computes from the stores.</summary>
    [Fact]
    public void AReaderAssemblesFromCurrentStoredEntriesAndComputesWhenTheyAreNotCurrent()
    {
        var rig = new Rig();
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);
        var store = StateDbModule.CreateStatusEntries(rig.Fixture.Home);
        var computed = JsonText.Stringify(rig.Lib.ComputeStatus(dir, StatusEntriesFixture.T0, dir, true), 0);

        // the stored gate rows go, behind the files' back: only a reader of the entries sees it
        Assert.True(store.Replace([(dir, StatusKind.Gate)], [], [], 0).Ok);
        var read = JsonText.Stringify(rig.Lib.ReadStatus(dir, StatusEntriesFixture.T0, dir, true), 0);
        Assert.Equal(JsonText.Stringify(rig.Lib.AssembleStatus(dir, rig.Lib.StoredStatusEntries(dir)!, StatusEntriesFixture.T0, true), 0), read);
        Assert.NotEqual(computed, read);

        Assert.True(store.Replace([(dir, StatusKind.Meta)], [], [], 0).Ok);
        Assert.Equal(computed, JsonText.Stringify(rig.Lib.ReadStatus(dir, StatusEntriesFixture.T0, dir, true), 0));
    }

    /// <summary>
    /// The queue's last dispatch (rulings Q2, Q3): a header the queue's parse rejects (no offset, date only) gives no row, as the queue
    /// skips it today; a parseable one does. Rows key by member name, so a team write that changes the member's role leaves them as they
    /// are (the role rolls up at read time); both stay equal to a rebuild.
    /// </summary>
    [Fact]
    public void ALastDispatchSkipsHeadersTheQueueCannotParseAndKeysByMemberNameAcrossARoleChange()
    {
        var rig = new Rig();
        var dir = StatusEntriesFixture.Pool(rig.Fixture.R);

        Assert.True(rig.CreateRequest(dir, "20260601-000001-noof", "q-pin", "2026-06-01T10:00:00"));
        Assert.True(rig.CreateRequest(dir, "20260601-000002-date", "q-pin", "2026-06-01"));
        Assert.DoesNotContain(rig.Lib.LastDispatches(dir, "t")!, static d => d.Name == "q-pin");
        rig.AssertParity(0, "unparseable");

        Assert.True(rig.CreateRequest(dir, "20260601-000003-good", "q-pin", "2026-06-01T10:00:00Z"));
        Assert.Contains(rig.Lib.LastDispatches(dir, "t")!, static d => d.Name == "q-pin" && d.Ms == 1_780_308_000_000);

        var before = rig.Lib.LastDispatches(dir, "t")!.Order().ToList();
        Assert.True(rig.Write("team", 1));
        Assert.Equal(before, rig.Lib.LastDispatches(dir, "t")!.Order().ToList());
        rig.AssertParity(1, "role change");
    }

    private static ReadOnlyMemory<byte> Args(JsonObject o) => Utf8Text.Encode(JsonText.Stringify(o, 0));

    /// <summary>A member liveness over registration records: the newest record naming the address decides, its <c>live</c> field standing in for the pid probe.</summary>
    private sealed class RecordLiveness : IMemberLiveness
    {
        internal Dictionary<string, JsonObject> Records { get; } = new(StringComparer.Ordinal);

        public MemberRegistrationLiveness? ForMember(string memberName, string team) => ForMember(memberName, team, [.. Records.Values]);

        public MemberRegistrationLiveness? ForMember(string memberName, string team, IReadOnlyList<JsonObject> registrations)
        {
            var address = $"{memberName}@{team}";
            var newest = registrations.Where(r => r.Get("address") is { IsText: true } a && a.AsString() == address)
                .OrderBy(static r => r.Get("at") is { IsText: true } at ? at.AsString() : string.Empty, StringComparer.Ordinal).LastOrDefault();
            return newest is null ? null : new MemberRegistrationLiveness(
                newest.Get("session_id") is { IsText: true } s ? s.AsString() : string.Empty, newest.Get("live") is { IsTruthy: true }, "record", null,
                newest.Get("cwd") is { IsText: true } c ? c.AsString() : null);
        }

        public bool PidIdentityLive(int pid, string? recordedStart) => pid == Environment.ProcessId;

        public IReadOnlyList<JsonObject> Registrations() => [.. Records.Values];

        public JsonObject? RegistrationOf(string sessionId) => Records.GetValueOrDefault(sessionId);
    }

    /// <summary>A message source that runs a hook before each query, on it and on every scope it opens.</summary>
    public class HookedSource : System.Reflection.DispatchProxy
    {
        internal object Inner { get; set; } = null!;

        internal Action Hook { get; set; } = null!;

        internal static IMsgScope Over(object inner, Action hook)
        {
            var proxy = Create<IMsgScope, HookedSource>();
            ((HookedSource)(object)proxy).Inner = inner;
            ((HookedSource)(object)proxy).Hook = hook;
            return proxy;
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IMsgSource.Scope))
            {
                return Over(((IMsgSource)Inner).Scope(), Hook);
            }

            if (targetMethod.Name == nameof(IDisposable.Dispose))
            {
                (Inner as IDisposable)?.Dispose();
                return null;
            }

            Hook();
            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException error)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException!).Throw();
                throw;
            }
        }
    }

    private sealed class Rig
    {
        private readonly StatusEntriesFixture _fixture = StatusEntriesFixture.Create();
        private readonly InProcessStateWriter _writer = new();
        private readonly HierLib _lib;
        private readonly List<(string Pool, string Id, string To, string Slug, string Team)> _requests = [];
        private readonly IStateDb _db;
        private readonly RecordLiveness _live = new();

        /// <summary>Runs before each read of the message store, outside its own reads: these tests time writes against the store reads of a rebuild.</summary>
        internal Action? BeforeStoreRead { get; set; }

        private bool _inHook;

        private void Hook()
        {
            if (_inHook || BeforeStoreRead is not { } hook)
            {
                return;
            }

            _inHook = true;
            try
            {
                hook();
            }
            finally
            {
                _inHook = false;
            }
        }

        /// <summary>Runs before each query on the message store, outside its own reads; inside a read share, <see cref="BeforeStoreRead"/> runs only at the share's one open.</summary>
        internal Action? BeforeMsgRead { get; set; }

        private void MsgHook()
        {
            if (_inHook || BeforeMsgRead is not { } hook)
            {
                return;
            }

            _inHook = true;
            try
            {
                hook();
            }
            finally
            {
                _inHook = false;
            }
        }

        private sealed class HookedModes(IMessageModeReader inner, Action hook) : IMessageModeReader
        {
            public string Read(string domain)
            {
                hook();
                return inner.Read(domain);
            }
        }

        internal Rig(IFileReader? files = null, Func<IStatusEntryStore, IStatusEntryStore>? entries = null)
        {
            _db = StateDbModule.Open(_fixture.Home, mayCreate: true);
            var statusStore = StateDbModule.CreateStatusEntries(_fixture.Home);
            var synced = TestMsgStore.SyncedRoute(_fixture.Home, _fixture.Root);
            var services = _fixture.Services(route: synced with { Modes = new HookedModes(synced.Modes, Hook), Source = HookedSource.Over(synced.Source, MsgHook) }, entries: entries is null ? statusStore : entries(statusStore), writer: _writer, backlogFacts: BacklogFacts, files: files);
            _lib = (HierLib)services.Status;
            services.Core.Liveness = _live;
            _writer.AhHome = _fixture.Home;
            _writer.Domains = [.. services.StateDomains, RosterModule.CreateStateDomain(_fixture.Roster!, new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(StatusEntriesFixture.T0)), new SequenceRandom(), services.StatusRefresher)];
            foreach (var dir in _fixture.StatusDirs)
            {
                Assert.True(_lib.RebuildStatusEntriesOf(dir).Ok);
            }
        }

        internal Random Rng { get; } = new(20261030);

        internal HierLib Lib => _lib;

        internal IReadOnlyList<IStateDomain> Domains => _writer.Domains;

        internal StatusEntriesFixture Fixture => _fixture;

        private int _dispatchRows;

        /// <summary>How many last-dispatch rows the run compared.</summary>
        internal int DispatchRows => _dispatchRows;

        private string[] Pools => [.. _fixture.StatusDirs];

        private string Pick(params string[] options) => options[Rng.Next(options.Length)];

        private long When() => StatusEntriesFixture.T0 + Rng.Next(-3 * 3_600_000, 2 * 3_600_000);

        private JsonValue? BacklogFacts(string dir)
        {
            var opened = StateDbModule.OpenReader(_fixture.Home);
            if (opened.Reader is not { } reader)
            {
                return null;
            }

            using (reader)
            {
                return HierarchyModule.BacklogHealthFacts(reader.Query, dir);
            }
        }

        internal Dictionary<string, string> Refused { get; } = new(StringComparer.Ordinal);

        /// <summary>A request create through the write path (its op and its AfterWrite), as a client makes it.</summary>
        internal bool CreateRequest(string pool, string path, string text) =>
            Call("msgs", "create", new JsonObject { ["dir"] = pool, ["path"] = path, ["content"] = text }, pool);

        /// <summary>A request retire (the exchange closes) through the write path.</summary>
        internal bool RetireRequest(string pool, string path) =>
            Call("msgs", "retire", new JsonObject { ["dir"] = pool, ["path"] = path, ["closed_by"] = "orchestrator", ["closed_reason"] = "done" }, pool);

        /// <summary>A peer-record append through the write path.</summary>
        internal bool AppendRecord(string pool, JsonObject rec) => Call("peer-records", "append", new JsonObject { ["rec"] = rec }, pool);

        private bool Call(string domain, string op, JsonObject args, string dir)
        {
            var result = _writer.Invoke(domain, op, Utf8Text.Encode(JsonText.Stringify(args, 0)), new RequestContext(dir, dir, dir, string.Empty));
            if (!result.Ok)
            {
                Refused.TryAdd($"{domain}/{op}", result.Error ?? "?");
            }

            return result.Ok;
        }

        internal bool Write(string kind, int step)
        {
            var pool = Pick(Pools);
            try
            {
                return kind switch
                {
                    "request" => NewRequest(pool, step),
                    "response" => _requests.Count > 0 && Respond(Pick([.. Pools]), _requests[Rng.Next(_requests.Count)]),
                    "fill" => _requests.Where(r => TestMsgStore.Holds(_fixture.Home, Path.Combine(r.Pool, "msgs", StatusEntriesFixture.ResponseName(r.Id, r.Slug)))).ToList() is { Count: > 0 } answered && Fill(answered[Rng.Next(answered.Count)]),
                    "retire" => _requests.Count > 0 && Retire(_requests[Rng.Next(_requests.Count)]),
                    "archive" => _requests.Count > 0 && Archive(_requests[Rng.Next(_requests.Count)]),
                    "settle" => _requests.Where(static r => r.Slug.StartsWith("dec-", StringComparison.Ordinal)).ToList() is { Count: > 0 } held && Settle(held[Rng.Next(held.Count)]),
                    "peer" => Call("peers", "append", new JsonObject
                    {
                        ["dir"] = pool,
                        ["rec"] = new JsonObject
                        {
                            ["type"] = "peer", ["status"] = Pick("up", "down"), ["name"] = Pick("t-implementor", "t-reviewer", "u-implementor", "q-implementor"),
                            ["role"] = Pick("implementor", "reviewer"), ["session_id"] = Pick("s-impl", "s-rev", "s-new"), ["pid"] = Environment.ProcessId,
                            ["team"] = Pick("t", "u", "q"), ["ts"] = StatusEntriesFixture.Iso(When()),
                        },
                    }, pool),
                    "nudge" => Call("gates", "append", new JsonObject { ["dir"] = pool, ["rec"] = new JsonObject { ["type"] = "liveness-nudge", ["request_id"] = AnyId() } }, pool),
                    "dispatch" => Call("peer-records", "append", new JsonObject
                    {
                        ["rec"] = new JsonObject
                        {
                            ["type"] = "dispatch", ["session_id"] = "o-t", ["request_id"] = AnyId(), ["to"] = "x", ["to_addr"] = Pick("t-implementor@t", "u-m0@u", "u-m1@u"),
                            ["created"] = StatusEntriesFixture.Iso(When()),
                        },
                    }, pool),
                    "decision" => Call("decisions", "append", new JsonObject
                    {
                        ["path"] = Path.Combine(StatusEntriesFixture.Pool(_fixture.R), "pipeline", "20260528-000001-anch", "decisions.jsonl"),
                        ["merge"] = new JsonObject { ["id"] = $"m{step}", ["time"] = StatusEntriesFixture.Iso(When()), ["run"] = "20260528-000001-anch", ["slug"] = "feat", ["sha"] = "abc1234" },
                    }, pool),
                    "activity" => Call("activity", "record", new JsonObject
                    {
                        ["dir"] = pool, ["subject"] = Pick("s-impl", "s-pane", "s-rev"), ["activity"] = Pick("working", "blocked", "idle"), ["blocked_by"] = Pick("permission", "question"), ["tool"] = Pick("Bash", "Edit"),
                    }, pool),
                    "session" => Call("session-pids", "append", new JsonObject { ["dir"] = pool, ["session_id"] = $"s-{step}", ["pid"] = Environment.ProcessId }, pool),
                    "team" => TeamWrite(step),
                    "backlog" => BacklogWrite(step),
                    "register" => Register(step),
                    "unregister" => _live.Records.Count > 0 && Unregister(_live.Records.Keys.ElementAt(Rng.Next(_live.Records.Count))),
                    _ => false,
                };
            }
            catch (InvalidOperationException e)
            {
                Refused.TryAdd(kind, e.Message);
                return false;
            }
        }

        private string AnyId() => _requests.Count > 0 && Rng.Next(3) > 0 ? _requests[Rng.Next(_requests.Count)].Id : "20260528-000002-impl";

        internal bool CreateRequest(string pool, string id, string toName, string created)
        {
            var path = Path.Combine(pool, "msgs", StatusEntriesFixture.RequestName(id, "implementor", "feat"));
            var text = StatusEntriesFixture.RequestText(id, "implementor", "feat", "t", StatusEntriesFixture.T0, toName, created: created);
            return Call("msgs", "create", new JsonObject { ["dir"] = pool, ["path"] = path, ["content"] = text }, pool);
        }

        private bool NewRequest(string pool, int step)
        {
            var id = $"20260601-{step:D6}-{(char)('a' + Rng.Next(26))}{(char)('a' + Rng.Next(26))}rq";
            var to = Pick("implementor", "reviewer", "sub-orchestrator", "orchestrator");
            var slug = Pick("feat", "lane-s1", "w-feat", "dec-x", HierarchyModule.PipelineAnchorSlug);
            var team = Pick("t", "u", "q");
            var path = Path.Combine(pool, "msgs", StatusEntriesFixture.RequestName(id, to, slug));
            var text = StatusEntriesFixture.RequestText(id, to, slug, team, When(), Pick("t-implementor", "t-sub", "u-implementor"), Pick("small", "medium", "large"), Rng.Next(2) == 0 ? "A title" : null, decision: Decision(slug), created: Created(step));
            if (!Call("msgs", "create", new JsonObject { ["dir"] = pool, ["path"] = path, ["content"] = text }, pool))
            {
                return false;
            }

            _requests.Add((pool, id, to, slug, team));
            return true;
        }

        private bool Respond(string pool, (string Pool, string Id, string To, string Slug, string Team) r)
        {
            var path = Path.Combine(pool, "msgs", StatusEntriesFixture.ResponseName(r.Id, r.Slug));
            var text = StatusEntriesFixture.ResponseText(r.Id, r.To, r.Slug, r.Team, When(), Pick("- DONE it\n", "- no-report\n", "- VERDICT PASS x@abc1234\n", "- BLOCKED y\n"));
            return Call("msgs", "create", new JsonObject { ["dir"] = pool, ["path"] = path, ["content"] = text, ["req"] = Path.Combine(r.Pool, "msgs", StatusEntriesFixture.RequestName(r.Id, r.To, r.Slug)) }, pool);
        }

        // Q3: a header with no offset and a date-only one, which the queue's parse skips (no last-dispatch row); most headers parse
        private static string? Created(int step) => (step % 7) switch
        {
            3 => $"2026-06-01T10:{step % 60:D2}:00",
            5 => "2026-06-01",
            _ => null,
        };

        // a decision request holds the decision its settle names
        private static string? Decision(string slug) => slug.StartsWith("dec-", StringComparison.Ordinal) ? "orchestrator" : null;

        private bool Settle((string Pool, string Id, string To, string Slug, string Team) r)
        {
            var path = Path.Combine(r.Pool, "msgs", StatusEntriesFixture.RequestName(r.Id, r.To, r.Slug));
            return TestMsgStore.Holds(_fixture.Home, path) && Call("msgs", "settle", new JsonObject
            {
                ["dir"] = r.Pool, ["path"] = path, ["from"] = Decision(r.Slug)!,
                ["content"] = StatusEntriesFixture.RequestText(r.Id, r.To, r.Slug, Pick("t", "u"), When(), "t-implementor", "small", Pick("Settled title", "Other"), decision: Decision(r.Slug)),
            }, r.Pool);
        }

        private bool Fill((string Pool, string Id, string To, string Slug, string Team) r) =>
            Call("msgs", "fill", new JsonObject
            {
                ["dir"] = r.Pool, ["path"] = Path.Combine(r.Pool, "msgs", StatusEntriesFixture.ResponseName(r.Id, r.Slug)),
                ["req"] = Path.Combine(r.Pool, "msgs", StatusEntriesFixture.RequestName(r.Id, r.To, r.Slug)), ["id"] = r.Id, ["from"] = r.To,
                ["body"] = JsonText.Stringify(JsonValue.FromString($"## [0] tldr\n- [1] status: {Pick("DONE filled", "no-report", "BLOCKED on z")}\n"), 0),
            }, r.Pool);

        private bool Retire((string Pool, string Id, string To, string Slug, string Team) r) =>
            Call("msgs", "retire", new JsonObject
            {
                ["dir"] = r.Pool, ["path"] = Path.Combine(r.Pool, "msgs", StatusEntriesFixture.RequestName(r.Id, r.To, r.Slug)), ["closed_by"] = "orchestrator", ["closed_reason"] = "done",
            }, r.Pool);

        private bool Archive((string Pool, string Id, string To, string Slug, string Team) r)
        {
            var files = new JsonArray();
            foreach (var name in new[] { StatusEntriesFixture.RequestName(r.Id, r.To, r.Slug), StatusEntriesFixture.ResponseName(r.Id, r.Slug) })
            {
                if (TestMsgStore.Holds(_fixture.Home, Path.Combine(r.Pool, "msgs", name)))
                {
                    files.Add(Path.Combine(r.Pool, "msgs", name));
                }
            }

            if (files.Count == 0 || !Call("msgs", "archive", new JsonObject { ["dir"] = r.Pool, ["files"] = files }, r.Pool))
            {
                return false;
            }

            _ = _requests.Remove(r);
            return true;
        }

        private bool TeamWrite(int step)
        {
            var home = StatusEntriesFixture.Pool(_fixture.R);
            var members = new JsonArray();
            for (var i = 0; i < 1 + Rng.Next(3); i++)
            {
                members.Add(new JsonObject { ["name"] = $"u-m{Rng.Next(4)}", ["role"] = Pick("implementor", "reviewer"), ["transport_id"] = Pick("%7", "%8") });
            }

            var team = new JsonObject
            {
                ["team_id"] = "t-u", ["team"] = "u", ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["session_id"] = "o-u" }, ["members"] = members,
                ["streams"] = new JsonObject
                {
                    [$"s{step % 3}"] = new JsonObject { ["sub_orchestrator"] = "u-m0", ["state"] = Pick("open", "done"), ["opened"] = StatusEntriesFixture.Iso(When()), ["worktree"] = Pick(_fixture.W, _fixture.Q) },
                },
            };
            return Call("team", "write", new JsonObject { ["team"] = team, ["path"] = Path.Combine(home, "teams", "u.json"), ["dir"] = home }, home);
        }

        private bool BacklogWrite(int step)
        {
            var key = Ah.Core.Json.MsgRows.PoolKey(Pick(Pools));
            var state = Pick("not_started", "in_progress", "done");
            var wave = Pick("untriaged", "now", "next", "later");
            var updated = When();
            var written = StateDbModule.OpenDirect(_fixture.Home).Write(tx =>
            {
                tx.Execute(
                    "INSERT OR REPLACE INTO plan_items(hier_dir, stream, item, state, wave, rank, holder, ext, updated_ms, doc) VALUES (?1, 'backlog', ?2, ?3, ?4, NULL, NULL, NULL, ?5, ?6)",
                    key, $"b{step % 5}", state, wave, updated, $$"""{"id":"b{{step % 5}}","added_at":"{{StatusEntriesFixture.Iso(updated - 1000)}}"}""");
                if (step % 4 == 0)
                {
                    tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES (?1, ?2)", "backlog.groom:" + key, $$"""{"ms":{{When()}},"summary":"groomed"}""");
                }

                return true;
            });
            _lib.StatusDelta("progress", Backlog.WriteOp, new JsonObject(), JsonValue.Null);
            return written.Ok;
        }

        // the mailbox domain lives in Ah.Mesh: its register/unregister land here as the AfterWrite it publishes ("mailbox" delta)
        private bool Register(int step) => RegisterSession(Pick("s-impl", "s-rev", "s-u0", "s-u1"), step);

        /// <summary>Registers <paramref name="session"/> as live and runs its registration delta.</summary>
        internal bool RegisterSession(string session, int step)
        {
            _live.Records[session] = new JsonObject
            {
                ["session_id"] = session, ["address"] = Pick("t-implementor@t", "t-reviewer@t", "u-m0@u", "u-m1@u"), ["live"] = Rng.Next(2) == 0,
                ["cwd"] = Pick(_fixture.W, _fixture.Q), ["at"] = StatusEntriesFixture.Iso(StatusEntriesFixture.T0 + step),
            };
            _lib.StatusDelta("mailbox", "register", new JsonObject { ["session_id"] = session }, new JsonObject { ["registered"] = true });
            return true;
        }

        internal bool Unregister(string session)
        {
            _ = _live.Records.Remove(session);
            _lib.StatusDelta("mailbox", "unregister", new JsonObject { ["session_id"] = session }, new JsonObject { ["removed"] = true });
            return true;
        }

        internal void AssertParity(int step, string kind)
        {
            // the comparison plants no message file, and asks the mode on every read
            using var held = TestMsgStore.HoldSync(_fixture.Home, _fixture.Root);
            foreach (var dir in _fixture.StatusDirs)
            {
                var stored = _lib.StoredStatusEntries(dir);
                Assert.True(stored is not null, $"step {step} ({kind}): {dir} lost its meta");
                // files-off-disk F3a: the document is served from these entries; no write publishes a status.json
                Assert.False(File.Exists(Path.Combine(dir, "status.json")), $"step {step} ({kind}) {Path.GetRelativePath(_fixture.Root, dir)}: a status.json was written");

                var rebuilt = _lib.RebuildStatusEntries(dir);
                rebuilt.Add(new(dir, StatusKind.Backlog, string.Empty, BacklogFacts(dir) ?? JsonValue.Null));

                // the queue's last-dispatch rows are not in the status document: compared row for row
                static string Dispatches(IEnumerable<StatusEntry> e) => string.Join("\n", e.Where(static x => x.Kind == StatusKind.LastDispatch)
                    .Select(static x => x.Pool + "|" + x.Key + "|" + JsonText.Stringify(x.Json, 0)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
                Assert.True(Dispatches(stored!) == Dispatches(rebuilt), $"step {step} ({kind}) {Path.GetRelativePath(_fixture.Root, dir)}: last-dispatch delta\n{Dispatches(stored!)}\nrebuild\n{Dispatches(rebuilt)}");
                _dispatchRows += rebuilt.Count(static x => x.Kind == StatusKind.LastDispatch);
                foreach (var now in StatusEntriesFixture.Nows)
                {
                    var delta = Text(_lib.AssembleStatus(dir, stored!, now, true));
                    var full = Text(_lib.AssembleStatus(dir, rebuilt, now, true));
                    if (delta != full)
                    {
                        var at = Enumerable.Range(0, Math.Min(delta.Length, full.Length)).FirstOrDefault(i => delta[i] != full[i]);
                        Assert.Fail($"step {step} ({kind}) {Path.GetRelativePath(_fixture.Root, dir)} @{now - StatusEntriesFixture.T0}: delta …{Slice(delta, at)}… rebuild …{Slice(full, at)}…");
                    }
                }
            }
        }

        private static string Slice(string s, int at) => s[Math.Max(0, at - 80)..Math.Min(s.Length, at + 120)];

        private static string Text(JsonObject status)
        {
            status.Remove("written_at");
            status.Remove("expires_at");
            return JsonText.Stringify(status, 0);
        }
    }
}
