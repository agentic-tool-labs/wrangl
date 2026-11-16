namespace Ah.Hierarchy.Tests;

/// <summary>ah-msgs-fast-2 §3.8: status assembled from persisted entries is the status the files give.</summary>
public sealed class StatusEntriesTests
{
    /// <summary>
    /// S8a: the status of every fixture status dir at every fixture time equals the golden file, captured from the pre-split
    /// <c>ComputeStatus</c> at the S8a base (set <c>AH_CAPTURE_GOLDEN=1</c> to rewrite it there, never after the split).
    /// </summary>
    [Fact]
    public void TheStatusOfEveryFixtureDirAtEveryTimeIsTheGoldenPreSplitStatus()
    {
        var fixture = StatusEntriesFixture.Create();
        var snapshot = fixture.Snapshot(fixture.Services());
        var golden = StatusEntriesFixture.GoldenPath();
        if (Environment.GetEnvironmentVariable("AH_CAPTURE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllText(golden, snapshot);
        }

        Assert.Equal(File.ReadAllText(golden), snapshot);
    }

    /// <summary>S8a: <c>assemble(rebuild(fixture), now)</c> is the golden pre-split status, byte for byte, for every status dir at every fixture time.</summary>
    [Fact]
    public void TheStatusAssembledFromRebuiltEntriesIsTheGoldenStatus()
    {
        var fixture = StatusEntriesFixture.Create();
        var lib = (HierLib)fixture.Services().Status;
        Assert.Equal(File.ReadAllText(StatusEntriesFixture.GoldenPath()), Assembled(fixture, lib, lib.RebuildStatusEntries));
    }

    /// <summary>
    /// S8a persist: after a rebuild of every status dir into the state database (R and W share W's pool, so the second rebuild replaces the
    /// first one's pool rows), the status assembled from the stored rows is the golden status.
    /// </summary>
    [Fact]
    public void TheStatusAssembledFromStoredEntriesIsTheGoldenStatus()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var lib = (HierLib)fixture.Services(entries: Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home)).Status;
        foreach (var dir in fixture.StatusDirs)
        {
            Assert.Equal((true, string.Empty), (lib.RebuildStatusEntriesOf(dir).Ok, string.Empty));
        }

        Assert.Equal([.. fixture.StatusDirs.Order(StringComparer.Ordinal)], lib.StatusEntryDirs());
        Assert.Equal(File.ReadAllText(StatusEntriesFixture.GoldenPath()), Assembled(fixture, lib, d => lib.StoredStatusEntries(d)!));
    }

    /// <summary>b459: the store's aggregate revision is the max write time and count of every row the dir reads, as reading them all gave.</summary>
    [Fact]
    public void TheAggregateRevisionIsTheOneTheRowsGive()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var store = Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home);
        var lib = (HierLib)fixture.Services(entries: store).Status;
        string FromRows(string dir) => store.ReadNewerThan(dir, long.MaxValue) is { Count: > 0 } rows ? $"{rows.Max(r => r.ChangedMs)}:{rows.Count}" : string.Empty;

        Assert.Equal(string.Empty, store.Revision(fixture.StatusDirs[0]));
        foreach (var dir in fixture.StatusDirs)
        {
            Assert.True(lib.RebuildStatusEntriesOf(dir).Ok);
        }

        var row = store.Read(fixture.StatusDirs[0]).First(r => r.Kind != "meta");
        Assert.True(store.Replace([], [(row.PoolDir, row.Kind, row.Key)], [], 0).Ok);
        foreach (var dir in fixture.StatusDirs)
        {
            Assert.StartsWith(FromRows(dir) + ":", store.Revision(dir));
        }
    }

    /// <summary>Negative: a second write in the same ms that keeps the count and the max (a key replaced) still moves the revision.</summary>
    [Fact]
    public void NegativeASecondWriteInTheSameMsMovesTheRevision()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var store = Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home);
        var dir = fixture.StatusDirs[0];
        Assert.True(store.Replace([], [], [new Ah.Abstractions.StatusEntryRow(dir, "gate", "k", "{\"count\":1}")], 5).Ok);
        var first = store.Revision(dir);

        Assert.True(store.Replace([], [], [new Ah.Abstractions.StatusEntryRow(dir, "gate", "k", "{\"count\":2}")], 5).Ok);
        Assert.NotEqual(first, store.Revision(dir));
    }

    /// <summary>files-off-disk F3a: <c>status.stamp</c> is the DB revision of the dir's rows; it holds with no write (the negative) and a delete moves it.</summary>
    [Fact]
    public void TheStatusStampIsTheRowsRevisionAndADeleteMovesIt()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var store = Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home);
        var lib = (HierLib)fixture.Services(entries: store).Status;
        var dir = fixture.StatusDirs[0];
        string Stamp() => lib.StatusStampOp(new Ah.Core.Json.JsonObject { ["dir"] = dir }).Member("stamp")!.Value.AsString();

        Assert.Equal(string.Empty, Stamp());
        Assert.True(lib.EnsureStatusEntries(dir));
        var built = Stamp();
        Assert.NotEqual(string.Empty, built);
        Assert.Equal(built, Stamp());

        var row = store.Read(dir).First(r => r.PoolDir == dir && r.Kind != "meta");
        Assert.True(store.Replace([], [(row.PoolDir, row.Kind, row.Key)], [], 0).Ok);
        Assert.NotEqual(built, Stamp());
    }

    /// <summary>S8a recovery: a status dir with no <c>meta</c> or a stale version is rebuilt once; a current one is not (the negative).</summary>
    [Fact]
    public void AMissingOrStaleMetaVersionRebuildsTheStatusDirOnceAndACurrentOneDoesNot()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var store = Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home);
        var lib = (HierLib)fixture.Services(entries: store).Status;
        var dir = fixture.StatusDirs[0];

        Assert.Null(lib.StoredStatusEntries(dir));
        Assert.True(lib.EnsureStatusEntries(dir));
        Assert.NotNull(lib.StoredStatusEntries(dir));
        Assert.False(lib.EnsureStatusEntries(dir));

        var stale = new Ah.Abstractions.StatusEntryRow(dir, "meta", "version", $"{{\"version\":{HierLib.StatusEntriesVersion - 1}}}");
        Assert.True(store.Replace([], [], [stale], 0).Ok);
        Assert.Null(lib.StoredStatusEntries(dir));
        Assert.True(lib.EnsureStatusEntries(dir));
        Assert.False(lib.EnsureStatusEntries(dir));
    }

    /// <summary>
    /// S8d: the first publish of a status dir with a store but no entries yet is computed and does not wait on the rebuild it schedules (one,
    /// however many publishes come before it runs); once it has run, a publish assembles the same document. With no store nothing is kept.
    /// </summary>
    [Fact]
    public void AFirstPublishDoesNotBlockOnTheOneRebuildItSchedulesAndLaterOnesAssemble()
    {
        var fixture = StatusEntriesFixture.Create();
        using var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var lib = (HierLib)fixture.Services(entries: Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home)).Status;
        var scheduled = new List<Action>();
        lib.RunInBackground = scheduled.Add;
        var dir = fixture.StatusDirs[0];
        var now = StatusEntriesFixture.Nows[0];
        static string Doc(Ah.Core.Json.JsonObject d)
        {
            d.Remove("written_at");
            d.Remove("expires_at");
            return Ah.Core.Json.JsonText.Stringify(d, 0);
        }

        var computed = Doc(lib.ComputeStatus(dir, now, dir, true));
        Assert.Equal(computed, Doc(lib.StatusDocument(dir, now, true)));
        Assert.Equal(computed, Doc(lib.StatusDocument(dir, now, true)));
        Assert.Null(lib.StoredStatusEntries(dir));
        var rebuild = Assert.Single(scheduled);

        rebuild();
        Assert.NotNull(lib.StoredStatusEntries(dir));
        Assert.Equal(computed, Doc(lib.StatusDocument(dir, now, true)));
        Assert.Single(scheduled);

        // the negative: outside the daemon (no background runner) a publish computes and builds nothing
        var cli = StatusEntriesFixture.Create();
        using var cliDb = Ah.StateDb.StateDbModule.Open(cli.Home, mayCreate: true);
        var cliLib = (HierLib)cli.Services(entries: Ah.StateDb.StateDbModule.CreateStatusEntries(cli.Home)).Status;
        var cliDir = cli.StatusDirs[0];
        Assert.Equal(Doc(cliLib.ComputeStatus(cliDir, now, cliDir, true)), Doc(cliLib.StatusDocument(cliDir, now, true)));
        Assert.Null(cliLib.StoredStatusEntries(cliDir));

        // F2: the watch's first sight of a status dir schedules the same background rebuild, never one inline; a built dir schedules none
        var seen = StatusEntriesFixture.Create();
        using var seenDb = Ah.StateDb.StateDbModule.Open(seen.Home, mayCreate: true);
        var seenLib = (HierLib)seen.Services(entries: Ah.StateDb.StateDbModule.CreateStatusEntries(seen.Home)).Status;
        var queued = new List<Action>();
        seenLib.RunInBackground = queued.Add;
        Assert.True(seenLib.ScheduleStatusEntries(seen.StatusDirs[0]));
        Assert.Null(seenLib.StoredStatusEntries(seen.StatusDirs[0]));
        Assert.Single(queued)();
        Assert.False(seenLib.ScheduleStatusEntries(seen.StatusDirs[0]));
        Assert.Single(queued);

        var bare = (HierLib)StatusEntriesFixture.Create().Services().Status;
        var bareDir = fixture.StatusDirs[0];
        Assert.Null(bare.StoredStatusEntries(bareDir));
        _ = bare.StatusDocument(bareDir, now, true);
        Assert.Empty(bare.StatusEntryDirs());
    }

    /// <summary>Every status dir at every fixture time, assembled from the entries <paramref name="entriesOf"/> gives, in the golden's form.</summary>
    private static string Assembled(StatusEntriesFixture fixture, HierLib lib, Func<string, List<StatusEntry>> entriesOf)
    {
        var lines = new List<string>();
        foreach (var dir in fixture.StatusDirs)
        {
            var entries = entriesOf(dir);
            foreach (var now in StatusEntriesFixture.Nows)
            {
                var status = lib.AssembleStatus(dir, entries, now, true);
                status.Remove("written_at");
                status.Remove("expires_at");
                lines.Add($"{Path.GetRelativePath(fixture.Root, dir)} @{now - StatusEntriesFixture.T0}: {Ah.Core.Json.JsonText.Stringify(status, 0).Replace(fixture.Root, "$ROOT", StringComparison.Ordinal)}");
            }
        }

        return string.Join("\n", lines) + "\n";
    }

    /// <summary>§3.8 item 1 assumption: the pool list itself is not printed in a status, so <c>view</c> rows may hold candidate trees with no msgs.</summary>
    [Fact]
    public void AStatusPrintsNoPoolList()
    {
        var fixture = StatusEntriesFixture.Create();
        var services = fixture.Services();
        foreach (var dir in fixture.StatusDirs)
        {
            var status = Ah.Core.Json.JsonText.Parse(StatusEntriesFixture.StatusText(services, dir, StatusEntriesFixture.Nows[0])).AsObject();
            Assert.Equal(["schema", "enabled", "member_sessions", "teams", "timeline", "owners"], status.Keys.Where(static k => k is not ("written_at" or "expires_at")));
            Assert.DoesNotContain("\"pools\"", StatusEntriesFixture.StatusText(services, dir, StatusEntriesFixture.Nows[0]), StringComparison.Ordinal);
        }
    }

    /// <summary>The fixture reaches what S8a must keep: a shared work-tree pool, a team home, charters, a pane view, owners, a run.</summary>
    [Fact]
    public void TheFixtureReachesEveryInputS8aSplits()
    {
        var fixture = StatusEntriesFixture.Create();
        var services = fixture.Services();
        var r = StatusEntriesFixture.Pool(fixture.R);
        var w = StatusEntriesFixture.Pool(fixture.W);
        var q = StatusEntriesFixture.Pool(fixture.Q);
        var status = (Func<string, long, string>)((d, n) => StatusEntriesFixture.StatusText(services, d, n));

        foreach (var dir in new[] { r, w, q })
        {
            Assert.Contains(w, services.Messages.PoolDirs(dir, fixture.Roster.ListTeamNames(dir)));
        }

        Assert.Equal(fixture.Roster.ListTeamNames(r), fixture.Roster.ListTeamNames(w));
        var atStart = status(r, StatusEntriesFixture.Nows[0]);
        Assert.Contains("20260528-000010-wimp", atStart, StringComparison.Ordinal);
        Assert.Contains("\"pipeline\":{\"anchor_id\":\"20260528-000001-anch\"", atStart, StringComparison.Ordinal);
        Assert.Contains("\"checkins\":2", atStart, StringComparison.Ordinal);
        Assert.Contains("\"activity\":\"blocked\"", atStart, StringComparison.Ordinal);
        Assert.Contains("\"reported_at\":", atStart, StringComparison.Ordinal);
        Assert.Contains("\"report_line\":", atStart, StringComparison.Ordinal);
        Assert.Contains("\"title\":\"Build the thing\"", atStart, StringComparison.Ordinal);
        Assert.Contains("20260528-000011-wq01", status(q, StatusEntriesFixture.Nows[0]), StringComparison.Ordinal);
        Assert.Contains("\"sessions\":[\"o-old\",\"o-t\"", atStart, StringComparison.Ordinal);
    }
}
