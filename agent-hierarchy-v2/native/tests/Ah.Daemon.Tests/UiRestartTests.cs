#pragma warning disable xUnit1051 // the rigs own their own deadlines

using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Ui;

namespace Ah.Daemon.Tests;

/// <summary>What a new daemon does with the last holder's record.</summary>
public sealed class UiRestartTests
{
    private const string Started = "2026-01-01T00:00:00.000Z";

    private const int OldHolder = 5150;

    private const int NewHolder = 5151;

    // ---------------------------------------------------------------- the record at daemon start

    private sealed class Machine(string runDir)
    {
        internal List<IReadOnlyList<string>> Spawned { get; } = [];

        /// <summary>What the holder does when spawned; by default it reports a live lease.</summary>
        internal Action Holder { get; set; } = () => File.WriteAllText(
            Path.Combine(runDir, "ui-service.json"),
            JsonText.Stringify(new JsonObject { ["pid"] = NewHolder, ["pid_start"] = Started, ["port"] = 1234, ["started_at"] = Started }, 0) + "\n");

        /// <summary>The pids that are alive, with the start time the record names.</summary>
        internal HashSet<int> Alive { get; } = [NewHolder];
    }

    private sealed class FakeSpawner(Machine machine) : IDetachedSpawner
    {
        public int SpawnDetached(DetachedSpawnRequest request)
        {
            machine.Spawned.Add(request.Argv);
            if (request.Argv.Contains(UiService.Hold, StringComparer.Ordinal))
            {
                machine.Holder();
            }

            return NewHolder;
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        internal required Harness H { get; init; }

        internal required Machine Machine { get; init; }

        internal string RunDir => Path.Combine(H.Box.AhHome, "run");

        internal JsonObject Record => JsonText.Parse(File.ReadAllText(Path.Combine(RunDir, "ui-service.json"))).AsObject();

        /// <summary>The reopen goes as far as the holder: no lease here, and the holder is all that was ever spawned, so no browser.</summary>
        internal void AssertNoLeaseAndNoBrowser()
        {
            Assert.False(H.Server.Ui.Active);
            Assert.Equal(0, H.Server.Ui.Leases);
            Assert.All(Machine.Spawned, argv => Assert.Contains(UiService.Hold, argv));
        }

        public ValueTask DisposeAsync() => H.DisposeAsync();
    }

    private static string StoppedRecord(string reason) =>
        JsonText.Stringify(new JsonObject { ["pid"] = OldHolder, ["pid_start"] = Started, ["port"] = 1234, ["started_at"] = Started, ["stopped_at"] = Started, ["stopped_reason"] = reason }, 0);

    private static string LiveRecord() =>
        JsonText.Stringify(new JsonObject { ["pid"] = OldHolder, ["pid_start"] = Started, ["port"] = 1234, ["started_at"] = Started }, 0);

    private static async Task<Rig> StartAsync(string? record, bool withSupport = true, Action<Machine>? setup = null)
    {
        var box = Sandbox.Create();
        var runDir = Path.Combine(box.AhHome, "run");
        if (record is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(runDir, "ui-service.json"), record + "\n");
        }

        var machine = new Machine(runDir);
        setup?.Invoke(machine);
        var support = new UiStartSupport(
            new FakeSpawner(machine),
            "/usr/bin/ahd",
            pid => machine.Alive.Contains(pid) ? Started : null,
            () => new Dictionary<string, string?>(),
            "fakeopen");
        var h = await Harness.StartAsync(box, o => o.UiStart = withSupport ? support : null);
        return new Rig { H = h, Machine = machine };
    }

    [Fact]
    public async Task NoRecordMeansNothing()
    {
        await using var rig = await StartAsync(null);

        await rig.H.Server.UiRestart;

        Assert.Empty(rig.Machine.Spawned);
        rig.AssertNoLeaseAndNoBrowser();
    }

    [Fact]
    public async Task ARecordThatTheDaemonEndedItReopens()
    {
        await using var rig = await StartAsync(StoppedRecord(UiService.DaemonEnded));

        await rig.H.Server.UiRestart;

        Assert.Single(rig.Machine.Spawned);
        Assert.Equal(["/usr/bin/ahd", "ui", UiService.Hold, rig.H.Pid.ToString(System.Globalization.CultureInfo.InvariantCulture)], rig.Machine.Spawned[0]);
        Assert.Equal(NewHolder, (int)rig.Record["pid"].AsNumber());
        rig.AssertNoLeaseAndNoBrowser();
    }

    [Fact]
    public async Task AStartThatTimesOutBeforeTheNextDaemonListensKeepsTheReopen()
    {
        // `ahd ui start` while the next daemon is still coming up: its holder never reports, so the start times out
        var box = Sandbox.Create();
        var runDir = Path.Combine(box.AhHome, "run");
        await File.WriteAllTextAsync(Path.Combine(runDir, "ui-service.json"), StoppedRecord(UiService.DaemonEnded) + "\n");
        var svc = new UiServiceSeams(runDir, 1, () => 1, static _ => 0, static _ => null, static (_, _) => false, "/usr/bin/ahd", TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1));
        Assert.Equal(UiServiceOutcome.TimedOut, (await UiService.EnsureAsync(svc, TestContext.Current.CancellationToken)).Outcome);
        UiService.Finish(svc, "refused", "the daemon is not reachable");

        var machine = new Machine(runDir);
        var support = new UiStartSupport(new FakeSpawner(machine), "/usr/bin/ahd", pid => machine.Alive.Contains(pid) ? Started : null, () => new Dictionary<string, string?>(), "fakeopen");
        await using var h = await Harness.StartAsync(box, o => o.UiStart = support);
        await h.Server.UiRestart;

        Assert.Single(machine.Spawned);
    }

    [Fact]
    public async Task NegativeAUserStopAfterAStartThatDidNotTakeWins()
    {
        var record = JsonText.Parse(StoppedRecord("stop")).AsObject();
        record[UiService.EndedBefore] = true;
        await using var rig = await StartAsync(JsonText.Stringify(record, 0));

        await rig.H.Server.UiRestart;

        Assert.Empty(rig.Machine.Spawned);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("refused")]
    public async Task ARecordOfAStopOrARefusalDoesNothing(string reason)
    {
        await using var rig = await StartAsync(StoppedRecord(reason));

        await rig.H.Server.UiRestart;

        Assert.Empty(rig.Machine.Spawned);
        Assert.Equal(reason, rig.Record.Str("stopped_reason"));
    }

    [Fact]
    public async Task ARecordWithNoReasonWhoseHolderIsDeadDoesNothing()
    {
        await using var rig = await StartAsync(LiveRecord());

        await rig.H.Server.UiRestart;

        Assert.Empty(rig.Machine.Spawned);
    }

    [Theory]
    [InlineData(UiService.DaemonEnded, 1)]
    [InlineData("stop", 0)]
    public async Task AHolderThatLooksLiveIsWaitedForAndTheTableIsAppliedToWhatItLeaves(string reason, int spawns)
    {
        await using var rig = await StartAsync(LiveRecord(), setup: m => m.Alive.Add(OldHolder));
        await Task.Delay(200);
        Assert.False(rig.H.Server.UiRestart.IsCompleted);
        Assert.Empty(rig.Machine.Spawned);

        // the daemon is polling the record: replace it whole, as a holder does (UiService.Write), so no poll reads it half-written
        var temp = Path.Combine(rig.RunDir, "ui-service.json.tmp");
        await File.WriteAllTextAsync(temp, StoppedRecord(reason) + "\n");
        File.Move(temp, Path.Combine(rig.RunDir, "ui-service.json"), overwrite: true);
        await rig.H.Server.UiRestart;

        Assert.Equal(spawns, rig.Machine.Spawned.Count);
    }

    [Fact]
    public async Task AHolderStillLiveAfterTheWaitIsLeftAloneAndLogged()
    {
        await using var rig = await StartAsync(LiveRecord(), setup: m => m.Alive.Add(OldHolder));

        await rig.H.Server.UiRestart;

        Assert.Empty(rig.Machine.Spawned);
        Assert.Equal(OldHolder, (int)rig.Record["pid"].AsNumber());
        Assert.Contains("a holder on this daemon owns it", rig.H.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartHoldingTheLockIsLeftToIt()
    {
        var box = Sandbox.Create();
        var runDir = Path.Combine(box.AhHome, "run");
        await File.WriteAllTextAsync(Path.Combine(runDir, "ui-service.json"), StoppedRecord(UiService.DaemonEnded) + "\n");
        var machine = new Machine(runDir);
        using var held = UiService.TryLock(new UiServiceSeams(runDir, 1, () => 1, _ => 0, _ => null, (_, _) => false, string.Empty, TimeSpan.Zero, TimeSpan.Zero));
        Assert.NotNull(held);

        await using var h = await Harness.StartAsync(box, o => o.UiStart = new UiStartSupport(new FakeSpawner(machine), "/usr/bin/ahd", _ => null, () => new Dictionary<string, string?>(), "fakeopen"));
        await h.Server.UiRestart;

        Assert.Empty(machine.Spawned);
        Assert.Equal(UiService.DaemonEnded, JsonText.Parse(await File.ReadAllTextAsync(Path.Combine(runDir, "ui-service.json"))).AsObject().Str("stopped_reason"));
        Assert.Contains("another start or stop is running", h.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHolderThatRecordsARefusalEndsTheOneAttemptAndTheFileWaitsForTheNextStart()
    {
        var box = Sandbox.Create();
        var runDir = Path.Combine(box.AhHome, "run");
        await File.WriteAllTextAsync(Path.Combine(runDir, "ui-service.json"), StoppedRecord(UiService.DaemonEnded) + "\n");
        var machine = new Machine(runDir)
        {
            Holder = () => File.WriteAllText(Path.Combine(runDir, "ui-service.json"), StoppedRecord("refused") + "\n"),
        };
        var support = new UiStartSupport(new FakeSpawner(machine), "/usr/bin/ahd", _ => null, () => new Dictionary<string, string?>(), "fakeopen");

        await using (var first = await Harness.StartAsync(box, o => o.UiStart = support))
        {
            await first.Server.UiRestart;
            Assert.Single(machine.Spawned);
            Assert.Contains("bridge reopen: Stopped", first.Log.ToString(), StringComparison.Ordinal);
        }

        await using var second = await Harness.StartAsync(box, o => o.UiStart = support);
        await second.Server.UiRestart;

        Assert.Single(machine.Spawned);
    }

    [Fact]
    public async Task ASpawnThatFailsIsRecordedAsRefusedAndLogged()
    {
        await using var rig = await StartAsync(StoppedRecord(UiService.DaemonEnded), setup: m => m.Holder = static () => throw new IOException("no spawn"));

        await rig.H.Server.UiRestart;

        Assert.Single(rig.Machine.Spawned);
        Assert.Equal("refused", rig.Record.Str("stopped_reason"));
        Assert.Contains("bridge reopen failed: IOException", rig.H.Log.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADrainDuringTheWaitAbandonsItWithNoSpawn()
    {
        await using var rig = await StartAsync(LiveRecord(), setup: m => m.Alive.Add(OldHolder));

        rig.H.Server.RequestShutdown(0);
        await rig.H.Server.UiRestart;

        Assert.Empty(rig.Machine.Spawned);
        Assert.Contains("bridge reopen abandoned", rig.H.Log.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UiService.DaemonEnded)]
    [InlineData("stop")]
    public async Task WithNoStartSupportNothingReopens(string reason)
    {
        await using var rig = await StartAsync(StoppedRecord(reason), withSupport: false);

        await rig.H.Server.UiRestart;

        Assert.Empty(rig.Machine.Spawned);
    }

    /// <summary>A stop or a start holds the lock as the daemon starts, and what it leaves there when it lets go decides.</summary>
    [Theory]
    [InlineData("stop", 0, false)]
    [InlineData(UiService.DaemonEnded, 1, false)]
    [InlineData(null, 0, true)]
    public async Task TheRecordIsReadAgainUnderTheLockThatWasWaitedFor(string? left, int spawns, bool holderLive)
    {
        await using var rig = UiRig.Create();
        var path = Path.Combine(rig.RunDir, "ui-service.json");
        await File.WriteAllTextAsync(path, StoppedRecord(UiService.DaemonEnded) + "\n");
        var machine = new Machine(rig.RunDir);
        if (holderLive)
        {
            _ = machine.Alive.Add(OldHolder);
        }

        var starter = new UiStarter(rig.Bridge, new UiStartSupport(new FakeSpawner(machine), "/usr/bin/ahd", pid => machine.Alive.Contains(pid) ? Started : null, () => new Dictionary<string, string?>(), "fakeopen"), rig.RunDir, 4242);
        var held = UiService.TryLock(new UiServiceSeams(rig.RunDir, 1, () => 1, _ => 0, _ => null, (_, _) => false, string.Empty, TimeSpan.Zero, TimeSpan.Zero));
        Assert.NotNull(held);
        var log = new List<string>();
        try
        {
            var restart = UiStarter.ApplyRestartAsync(starter, rig.RunDir, 4242, log.Add, default);
            await Task.Delay(300);
            Assert.False(restart.IsCompleted, "the restart did not wait for the lock");
            Assert.Empty(machine.Spawned);

            await File.WriteAllTextAsync(path, (left is null ? LiveRecord() : StoppedRecord(left)) + "\n");
            held.Dispose();
            await restart;
        }
        finally
        {
            held.Dispose();
        }

        Assert.Equal(spawns, machine.Spawned.Count);
        if (holderLive)
        {
            Assert.Equal(OldHolder, (int)JsonText.Parse(await File.ReadAllTextAsync(path)).AsObject()["pid"].AsNumber());
            Assert.Null(JsonText.Parse(await File.ReadAllTextAsync(path)).AsObject().Get("stopped_reason"));
        }
    }

    [Fact]
    public async Task ARecordOfNoHolderWaitsForALockAStartHolds()
    {
        await using var rig = UiRig.Create();
        var held = UiService.TryLock(new UiServiceSeams(rig.RunDir, 1, () => 1, _ => 0, _ => null, (_, _) => false, string.Empty, TimeSpan.Zero, TimeSpan.Zero));
        Assert.NotNull(held);
        try
        {
            var restart = UiStarter.ApplyRestartAsync(null, rig.RunDir, 4242, static _ => { }, default);
            await Task.Delay(300);

            Assert.False(restart.IsCompleted, "the restart did not wait for the lock");

            held.Dispose();
            await restart;
        }
        finally
        {
            held.Dispose();
        }
    }

    [Fact]
    public async Task AStartFromTheBandWaitsForALockAReopenHolds()
    {
        await using var rig = UiRig.Create();
        var machine = new Machine(rig.RunDir);
        var seams = new UiServiceSeams(rig.RunDir, 1, () => 1, _ => 0, _ => null, (_, _) => false, string.Empty, TimeSpan.Zero, TimeSpan.Zero);
        var starter = new UiStarter(rig.Bridge, new UiStartSupport(new FakeSpawner(machine), "/usr/bin/ahd", pid => machine.Alive.Contains(pid) ? Started : null, () => new Dictionary<string, string?>(), "fakeopen"), rig.RunDir, 4242);
        var held = UiService.TryLock(seams);
        Assert.NotNull(held);
        try
        {
            var start = starter.RunAsync(default);
            await Task.Delay(300);
            Assert.False(start.IsCompleted, "the start did not wait for the lock");
            Assert.Empty(machine.Spawned);

            held.Dispose();

            Assert.Equal(UiStartKind.Started, (await start).Kind);
        }
        finally
        {
            held.Dispose();
        }
    }

    // b92: a daemon stop that cancels a reopen after its spawn ends that holder, which would otherwise find no daemon and start one.
    // Negatives: a holder pid that no longer has the start time read at the spawn is someone else's now; a holder that went live
    // as the stop came holds the lease and records the daemon's end itself. Neither is signalled.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task ADaemonStopDuringTheReopenWaitEndsTheHolderItSpawned(bool pidReused, bool wentLive)
    {
        await using var rig = UiRig.Create();
        await File.WriteAllTextAsync(Path.Combine(rig.RunDir, "ui-service.json"), StoppedRecord(UiService.DaemonEnded) + "\n");
        var machine = new Machine(rig.RunDir);
        var goLive = machine.Holder;
        machine.Holder = static () => { };
        var signals = new List<(int Pid, int Signal)>();
        using var stop = new CancellationTokenSource();

        // the live record lands only once the stop is asked for, so the wait never sees it and the cancel does
        var support = new UiStartSupport(new FakeSpawner(machine), "/usr/bin/ahd", pid =>
        {
            if (wentLive && stop.IsCancellationRequested)
            {
                goLive();
            }

            return machine.Alive.Contains(pid) ? Started : null;
        }, () => new Dictionary<string, string?>(), "fakeopen", (pid, signal) =>
        {
            signals.Add((pid, signal));
            return true;
        });
        var log = new List<string>();

        var reopen = UiStarter.ApplyRestartAsync(new UiStarter(rig.Bridge, support, rig.RunDir, 4242), rig.RunDir, 4242, line => log.Add(line), stop.Token);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (machine.Spawned.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Single(machine.Spawned);
        if (pidReused)
        {
            _ = machine.Alive.Remove(NewHolder);
        }

        await stop.CancelAsync();
        await reopen;

        Assert.Equal(pidReused || wentLive ? [] : [(NewHolder, 15)], signals);
        Assert.Contains(log, line => line.StartsWith("bridge reopen abandoned", StringComparison.Ordinal));
    }
}
