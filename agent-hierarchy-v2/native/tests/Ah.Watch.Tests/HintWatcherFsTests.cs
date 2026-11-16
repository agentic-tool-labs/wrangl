using System.Runtime.CompilerServices;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The hint path end to end on a real directory: a record written to disk reaches a polling client with no emit and no timer.</summary>
public sealed class HintWatcherFsTests
{
    private sealed class DiskReader : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            var activity = Path.Combine(hierarchyDir, "activity");
            if (domain + "." + op == "activity.keys")
            {
                var subjects = new JsonArray();
                if (Directory.Exists(activity))
                {
                    foreach (var f in Directory.EnumerateFiles(activity, "*.json").Order(StringComparer.Ordinal))
                    {
                        subjects.Add(Path.GetFileNameWithoutExtension(f));
                    }
                }

                return JsonValue.FromObject(new JsonObject { ["subjects"] = JsonValue.FromArray(subjects) });
            }

            var file = Path.Combine(activity, args.Get("subject")!.Value.AsString() + ".json");
            return File.Exists(file) ? JsonText.Parse(File.ReadAllText(file)) : JsonValue.Null;
        }
    }

    /// <summary>How long the real-event test writes again: under the one-minute reconcile floor, so that pass cannot publish what it says a file event published.</summary>
    private const double WriteAgainSeconds = 45;

    /// <summary>
    /// What the watcher does with the event of a write, through its own filter. Measured at load 30 on macOS, a bare FileSystemWatcher with no read
    /// model in it delivered events 2 to 14 seconds late and never delivered some at all, so these tests do not wait on one;
    /// <see cref="ARealFileEventReachesAWaitingClient"/> holds the watcher itself.
    /// </summary>
    private static void Hint(ReadModel model, string dir, string path)
    {
        if (HintWatcher.HintsFor(Path.GetRelativePath(dir, path), tree: true))
        {
            model.Hint(path);
        }
    }

    private static string RealPath(string path) =>
        OperatingSystem.IsMacOS() && path.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + path : path;

    [Fact]
    public async Task DisposingTheModelEndsTheProjectorThreadAndStopsTheWatchers()
    {
        var root = RealPath(Directory.CreateTempSubdirectory("ah-stop-").FullName);
        var dir = Path.Combine(root, "h");
        _ = Directory.CreateDirectory(Path.Combine(dir, "activity"));
        var model = LazyAwareModel.Unwrap(WatchModule.CreateReadModel(new DiskReader(), () => [dir]))!;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(dir, "activity", "seed.json"), """{"activity":"idle","at":"2026-06-01T11:00:00Z"}""", TestContext.Current.CancellationToken);
            WatchModule.StartReadModel(model);
            var watcher = (HintWatcher)model.Watcher!;
            _ = await model.PollAsync(0, null, ["activity:" + ActivityLane.DirId(dir) + "/seed"], TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
            Assert.True(model.ProjectorAlive);
            // the watchers start on the thread pool, off the projector
            var started = DateTime.UtcNow.AddSeconds(30);
            while (watcher.Running == 0 && DateTime.UtcNow < started)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.True(watcher.Running > 0);

            model.Dispose();

            Assert.False(model.ProjectorAlive);
            Assert.Equal(0, watcher.Running);
        }
        finally
        {
            model.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class KnownDirsReader(List<string> noted) : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (domain + "." + op != "known-dirs.read")
            {
                return JsonValue.Null;
            }

            var dirs = new JsonArray();
            lock (noted)
            {
                foreach (var d in noted)
                {
                    dirs.Add(d);
                }
            }

            return JsonValue.FromObject(new JsonObject { ["dirs"] = JsonValue.FromArray(dirs) });
        }
    }

    /// <summary>b465: the known-dirs index is rows, so no file event reports a note; the note's emit alone starts the new dir's watcher.</summary>
    [Fact]
    public async Task ANewlyNotedDirGetsAWatcherFromTheNotesEmitAlone()
    {
        var root = RealPath(Directory.CreateTempSubdirectory("ah-noted-").FullName);
        var dir = Path.Combine(root, "h");
        _ = Directory.CreateDirectory(dir);
        var noted = new List<string>();
        List<string> Snapshot()
        {
            lock (noted)
            {
                return [.. noted];
            }
        }

        var model = LazyAwareModel.Unwrap(WatchModule.CreateReadModel(new KnownDirsReader(noted), Snapshot, new ReadModelOptions { Home = Path.Combine(root, "home") }))!;
        try
        {
            WatchModule.StartReadModel(model);
            var watcher = (HintWatcher)model.Watcher!;
            watcher.Sync();
            var before = watcher.Running;
            lock (noted)
            {
                noted.Add(dir);
            }

            // negative: with no emit, a Sync keeps the set it already had
            watcher.Sync();
            Assert.Equal(before, watcher.Running);

            model.Emit(KnownDirsLane.LaneName, WatchModule.KnownDirsKey);
            var until = DateTime.UtcNow.AddSeconds(30);
            while (watcher.Running == before && DateTime.UtcNow < until)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken);
            }

            Assert.Equal(before + 1, watcher.Running);
        }
        finally
        {
            model.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>atp-1673: the known-dirs lane's one-part key re-reads to a record, so the composed model publishes <c>list:teams</c>.</summary>
    [Fact]
    public async Task TheKnownDirsReReadKeepsItsRecordSoTheTeamListIsPublished()
    {
        var root = RealPath(Directory.CreateTempSubdirectory("ah-teamlist-").FullName);
        var dir = Path.Combine(root, "h");
        _ = Directory.CreateDirectory(dir);
        List<string> noted = [dir];
        using var model = WatchModule.CreateReadModel(new KnownDirsReader(noted), () => [dir], new ReadModelOptions { Home = Path.Combine(root, "home") });
        try
        {
            WatchModule.StartReadModel(model);
            model.Emit(KnownDirsLane.LaneName, WatchModule.KnownDirsKey);
            var served = string.Empty;
            var until = DateTime.UtcNow.AddSeconds(30);
            while (!served.Contains("\"list:teams\"", StringComparison.Ordinal) && DateTime.UtcNow < until)
            {
                served = System.Text.Encoding.UTF8.GetString(await model.PollAsync(0, null, [TreeIds.TeamList], TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken, [TreeIds.TeamList]));
            }

            Assert.Contains("\"list:teams\"", served, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TheStatsNameTheCountersTheHealthBodyCarriesAndAreNullForAForeignModel()
    {
        using var model = WatchModule.CreateReadModel(new DiskReader(), () => []);

        var stats = WatchModule.ReadModelStats(model)!;

        Assert.Equal(["unclassified_hint", "noncanonical_hint", "drift", "reread_retry", "coalesce_ms", "projector_faults", "transcript_resets", "full_passes", "last_pass_ms", "listings", "parked", "decision_orphans"], stats.Keys.ToArray());
        Assert.Equal(100, (int)stats["coalesce_ms"].AsNumber());
        Assert.Null(WatchModule.ReadModelStats(null));
    }

    [Fact]
    public void ClosingTheModelNamesHowLongEachPartTook()
    {
        var db = TimelineDb.NewPath();
        var model = WatchModule.CreateReadModel(new DiskReader(), () => [], new ReadModelOptions { Timeline = new TimelineSettings(TimelineDb.Opener, db, Ah.StateDb.StateDbModule.BusyTimeoutMs, () => 1) });
        WatchModule.StartReadModel(model);

        var parts = WatchModule.CloseReadModel(model);

        Assert.Matches(@"^watcher \d+ ms, projector join \d+ ms, archiver join \d+ ms, timeline store close \d+ ms$", parts);
    }

    private sealed class ProgressDiskReader : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            var progress = Path.Combine(hierarchyDir, "progress");
            var team = args.Get("team") is { IsText: true } t ? t.AsString() : string.Empty;
            switch (domain + "." + op)
            {
                case "progress.keys":
                    var teams = new JsonArray();
                    if (Directory.Exists(progress))
                    {
                        foreach (var d in Directory.EnumerateDirectories(progress).Select(Path.GetFileName).Order(StringComparer.Ordinal))
                        {
                            teams.Add(d!);
                        }
                    }

                    return JsonValue.FromObject(new JsonObject { ["teams"] = JsonValue.FromArray(teams) });
                case "progress.streams":
                    return JsonValue.FromObject(new JsonObject { ["streams"] = JsonValue.FromArray(new JsonArray()) });
                default:
                    var streams = new JsonArray();
                    var folder = Path.Combine(progress, team);
                    if (Directory.Exists(folder))
                    {
                        foreach (var file in Directory.EnumerateFiles(folder, "*.plan.jsonl").Order(StringComparer.Ordinal))
                        {
                            var lines = File.ReadAllLines(file);
                            var rows = new JsonArray();
                            foreach (var line in lines.Skip(args.Get("after_offsets")?.Member(Path.GetFileName(file)[..^".plan.jsonl".Length]) is { IsNumber: true } n ? (int)n.AsNumber() : 0))
                            {
                                rows.Add(JsonText.Parse(line));
                            }

                            streams.Add(new JsonObject { ["stream"] = Path.GetFileName(file)[..^".plan.jsonl".Length], ["rows"] = rows, ["offset"] = lines.Length, ["length"] = lines.Length, ["truncated"] = false, ["reset"] = false });
                        }
                    }

                    return JsonValue.FromObject(new JsonObject { ["streams"] = JsonValue.FromArray(streams) });
            }
        }
    }

    [Fact]
    public void NegativeAnActivityFileNamesNoLaneSinceActivityRecordsAreRows()
    {
        var table = new PathLaneTable(() => ["/h"]);

        Assert.Equal(PathClass.Unclassified, table.Classify("/h/activity/s1.json", out _, out _));
    }

    /// <summary>
    /// The watcher itself: a write reaches a waiting client with nothing but the operating system's event. A bare watcher on the same directory
    /// is the control: on a machine too loaded to deliver to it either, the run proves nothing and is skipped; it fails only when the bare
    /// one saw several writes and the model's saw none.
    /// </summary>
    [Fact]
    public async Task ARealFileEventReachesAWaitingClient()
    {
        var root = RealPath(Directory.CreateTempSubdirectory("ah-hint-").FullName);
        var dir = Path.Combine(root, "h");
        // a lane the watcher still reads from files: the team records (progress files are rows since files-off-disk F2b/F4, activity since per-dir-files-to-db S2)
        var teams = Path.Combine(dir, "teams");
        _ = Directory.CreateDirectory(teams);
        _ = Directory.CreateDirectory(Path.Combine(dir, "progress", "t"));
        var reads = new StrongBox<int>(0);
        using var model = SitrepModel(root, dir, reads);
        try
        {
            WatchModule.StartReadModel(model);
            var id = "sitrep:" + ActivityLane.DirId(dir) + "/t";
            var since = await PollUntil(model, id, 0, reads, 1);

            var seen = 0;
            using var bare = new FileSystemWatcher(teams);
            bare.Changed += (_, _) => Volatile.Write(ref seen, 1);
            bare.Created += (_, _) => Volatile.Write(ref seen, 1);
            bare.EnableRaisingEvents = true;

            var bareRounds = 0;
            var deadline = DateTime.UtcNow.AddSeconds(WriteAgainSeconds);
            var slices = new JsonArray();
            for (var n = 1; slices.Count == 0 && DateTime.UtcNow < deadline; n++)
            {
                Volatile.Write(ref seen, 0);
                var poll = model.PollAsync(since, null, [id], TimeSpan.FromSeconds(5), CancellationToken.None);
                await File.WriteAllTextAsync(Path.Combine(teams, $"t-s{n}.json"), "{\"team_id\":\"tc" + n + "\",\"members\":[],\"parent\":{\"team\":\"t\",\"stream\":\"s" + n + "\"}}", TestContext.Current.CancellationToken);

                var reply = JsonText.Parse(await poll.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
                slices = reply.Member("slices")!.Value.AsArray();
                since = (long)reply.Member("seq")!.Value.AsNumber();
                bareRounds += Volatile.Read(ref seen);
            }

            if (slices.Count == 0)
            {
                Assert.True(bareRounds < 3, $"a bare watcher saw {bareRounds} writes and the model's watcher published none");
                Assert.Skip($"the machine delivered {bareRounds} file events to a bare watcher, too few to judge the model's");
            }

            Assert.Equal(id, slices[0].Member("id")!.Value.AsString());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>The progress reader, plus team records read from disk for the teams lane; every other read answers nothing.</summary>
    private sealed class SitrepDiskReader : IViewStateReader
    {
        private readonly ProgressDiskReader _progress = new();

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => (domain, op) switch
        {
            ("progress", _) => _progress.Read(domain, op, hierarchyDir, args),
            ("team", "read") when args.Get("path") is { IsText: true } p && File.Exists(Path.Combine(hierarchyDir, p.AsString())) =>
                JsonText.Parse(File.ReadAllText(Path.Combine(hierarchyDir, p.AsString()))),
            ("team", "keys") => JsonValue.FromObject(new JsonObject { ["teams"] = JsonValue.FromArray(new JsonArray()) }),
            _ => JsonValue.Null,
        };
    }

    /// <summary>A model over <paramref name="dir"/> whose sitrep probe counts its reads into <paramref name="reads"/> and serves the count as the body.</summary>
    private static ReadModel SitrepModel(string root, string dir, StrongBox<int> reads)
    {
        var home = Path.Combine(root, "home");
        _ = Directory.CreateDirectory(home);
        var options = TestFolds.Options with
        {
            Home = home,
            SitrepProbe = (_, team, _, _) => (JsonText.Parse("{\"n\":" + Interlocked.Increment(ref reads.Value) + "}"), [team]),
        };
        return LazyAwareModel.Unwrap(WatchModule.CreateReadModel(new SitrepDiskReader(), () => [dir], options))!;
    }

    /// <summary>
    /// Waits for the team list, so the model has loaded its directories (a lease taken before that names no key), then polls the slice until
    /// the probe has read at least <paramref name="target"/> times; the last seq.
    /// </summary>
    private static async Task<long> PollUntil(ReadModel model, string id, long since, StrongBox<int> reads, int target)
    {
        if (since == 0)
        {
            var listed = JsonText.Parse(await model.PollAsync(0, null, ["list:teams"], TimeSpan.FromSeconds(10), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None));
            since = (long)listed.Member("seq")!.Value.AsNumber();
        }

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Volatile.Read(ref reads.Value) < target && DateTime.UtcNow < deadline)
        {
            var reply = JsonText.Parse(await model.PollAsync(since, null, [id], TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None));
            since = (long)reply.Member("seq")!.Value.AsNumber();
        }

        Assert.True(Volatile.Read(ref reads.Value) >= target, $"the sitrep probe read {reads.Value} times, wanted {target}");
        return since;
    }

    [Fact]
    public async Task ATeamRecordWriteInTheDirectoryReReadsAHeldSitrep()
    {
        var root = RealPath(Directory.CreateTempSubdirectory("ah-sitrep-").FullName);
        var dir = Path.Combine(root, "h");
        _ = Directory.CreateDirectory(Path.Combine(dir, "teams"));
        _ = Directory.CreateDirectory(Path.Combine(dir, "progress", "t"));
        var reads = new StrongBox<int>(0);
        using var model = SitrepModel(root, dir, reads);
        try
        {
            WatchModule.StartReadModel(model);
            var id = "sitrep:" + ActivityLane.DirId(dir) + "/t";
            var since = await PollUntil(model, id, 0, reads, 1);
            var before = reads.Value;

            await File.WriteAllTextAsync(Path.Combine(dir, "teams", "t-s1.json"), """{"team_id":"tc","members":[],"parent":{"team":"t","stream":"s1"}}""", TestContext.Current.CancellationToken);
            Hint(model, dir, Path.Combine(dir, "teams", "t-s1.json"));

            _ = await PollUntil(model, id, since, reads, before + 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NegativeAWriteOutsideTheWatchedFilesDoesNotReReadAHeldSitrep()
    {
        var root = RealPath(Directory.CreateTempSubdirectory("ah-sitrep-").FullName);
        var dir = Path.Combine(root, "h");
        _ = Directory.CreateDirectory(Path.Combine(dir, "teams"));
        _ = Directory.CreateDirectory(Path.Combine(dir, "progress", "t"));
        var reads = new StrongBox<int>(0);
        using var model = SitrepModel(root, dir, reads);
        try
        {
            WatchModule.StartReadModel(model);
            var id = "sitrep:" + ActivityLane.DirId(dir) + "/t";
            var listed = JsonText.Parse(await model.PollAsync(0, null, ["list:teams"], TimeSpan.FromSeconds(10), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20), CancellationToken.None));
            // A directory's first watcher re-reads every held Sitrep once it exists (b290): under load that lands after the lease. Sync here,
            // with the directories loaded and no Sitrep held, so that catch-up is over before the read this test counts from.
            ((HintWatcher)model.Watcher!).Sync();
            var since = await PollUntil(model, id, (long)listed.Member("seq")!.Value.AsNumber(), reads, 1);
            var before = reads.Value;

            await File.WriteAllTextAsync(Path.Combine(dir, "elsewhere.txt"), "x", TestContext.Current.CancellationToken);
            Hint(model, dir, Path.Combine(dir, "elsewhere.txt"));

            _ = await model.PollAsync(since, null, [id], TimeSpan.FromSeconds(3), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
            Assert.Equal(before, Volatile.Read(ref reads.Value));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
