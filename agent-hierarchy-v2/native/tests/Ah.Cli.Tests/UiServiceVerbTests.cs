#pragma warning disable xUnit1051 // the fake server owns its own deadlines

using System.Runtime.Versioning;
using Ah.Cli.Verbs.AhdCtl;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ahd ui start|stop|restart|status</c> against a stand-in daemon socket, a fake process table and a state file in a scratch directory.</summary>
[SupportedOSPlatform("macos")]
[SupportedOSPlatform("linux")]
public sealed class UiServiceVerbTests
{
    private const string Grant = "{\"port\":4711}";

    private const int Child = 999;

    private sealed class Rig : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("ahui-").FullName;
        private readonly string _runDir;

        internal Rig() => _runDir = Path.Combine(_root, "run");

        internal FakeLeaseServer Server { get; } = FakeLeaseServer.Start();

        internal StringWriter Out { get; } = new();

        internal StringWriter Err { get; } = new();

        internal List<string> Opened { get; } = [];

        internal List<IReadOnlyList<string>> Spawned { get; } = [];

        internal List<(int Pid, int Signal)> Signals { get; } = [];

        internal Dictionary<int, string> Alive { get; } = [];

        /// <summary>The start time each live fake process prints in the machine's local zone, the form older state files recorded.</summary>
        internal Dictionary<int, string> LocalStart { get; } = [];

        internal int LeaseRequests { get; private set; }

        internal int ParentPid { get; set; } = 100;

        /// <summary>Whether a signal ends the fake process.</summary>
        internal bool SignalsKill { get; set; } = true;

        /// <summary>The fake process ends on the first signal and another process takes its pid.</summary>
        internal bool PidReused { get; set; }

        /// <summary>What the fake process does as the signal reaches it, such as the record a real holder writes on its way out.</summary>
        internal Action? OnSignal { get; set; }

        internal string LockPath => Path.Combine(_runDir, "ui-service.lock");

        /// <summary>What the spawned holder writes in place of the real one: a running service, or the refusal it recorded.</summary>
        internal string? ChildState { get; set; } = "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"started_at\":\"2026-10-20T00:00:00.000Z\"}";

        internal string StatePath => Path.Combine(_runDir, "ui-service.json");

        internal UiVerb.Seams Seams => new(
            _ => Task.FromResult<string?>(Server.SocketPath),
            (socket, ct) =>
            {
                LeaseRequests++;
                return PlatformModule.OpenHeldAsync(socket, "POST", "/v1/ui/lease", "test", TimeSpan.FromSeconds(10), ct);
            },
            url =>
            {
                Opened.Add(url);
                return true;
            },
            (socket, ct) => PlatformModule.OpenHeldAsync(socket, "POST", "/v1/ui/lease?service=1", "test", TimeSpan.FromSeconds(10), ct));

        internal UiServiceVerb.ServiceSeams Service => new(
            _runDir,
            100,
            () => ParentPid,
            argv =>
            {
                Spawned.Add(argv);
                Directory.CreateDirectory(_runDir);
                if (ChildState is not null)
                {
                    File.WriteAllText(StatePath, ChildState);
                }

                Alive[Child] = "T1";
                return Child;
            },
            pid => Alive.GetValueOrDefault(pid),
            (pid, signal) =>
            {
                Signals.Add((pid, signal));
                OnSignal?.Invoke();
                if (PidReused)
                {
                    Alive[pid] = "T2";
                }
                else if (SignalsKill)
                {
                    _ = Alive.Remove(pid);
                }

                return true;
            },
            "/bin/ahd",
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(300),
            pid => LocalStart.GetValueOrDefault(pid));

        internal void Running()
        {
            Directory.CreateDirectory(_runDir);
            File.WriteAllText(StatePath, "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"started_at\":\"2026-10-20T00:00:00.000Z\"}");
            Alive[Child] = "T1";
        }

        /// <summary>Runs a verb; when it takes a short lease, answers it with a held reply.</summary>
        internal async Task<int> RunAsync(bool answersLease, params string[] args)
        {
            var accepted = answersLease ? Server.AcceptAsync() : null;
            var exit = UiServiceVerb.RunAsync(args, Out, Err, Seams, Service, CancellationToken.None);
            if (accepted is not null)
            {
                await accepted.WaitAsync(TimeSpan.FromSeconds(10));
                await Server.HoldAsync(Grant);
            }

            return await exit.WaitAsync(TimeSpan.FromSeconds(10));
        }

        public void Dispose()
        {
            Server.Dispose();
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task StartSpawnsTheHolderOnceTakesOneShortLeaseAndPrintsTheLink()
    {
        using var rig = new Rig();

        var exit = await rig.RunAsync(true, "start", "--json");

        Assert.Equal(0, exit);
        Assert.Equal([["/bin/ahd", "ui", "--service-hold", "100"]], rig.Spawned);
        Assert.Equal(1, rig.LeaseRequests);
        Assert.Equal(["http://127.0.0.1:4711/ui/"], rig.Opened);
        Assert.Contains("\"already_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Zm9vYmFy", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartOnARunningServiceOnlyTakesAFreshLink()
    {
        using var rig = new Rig();
        rig.Running();

        var exit = await rig.RunAsync(true, "start", "--json");

        Assert.Equal(0, exit);
        Assert.Empty(rig.Spawned);
        Assert.Equal(1, rig.LeaseRequests);
        Assert.Contains("\"already_running\":true", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartNoOpenPrintsTheLinkAndOpensNothing()
    {
        using var rig = new Rig();

        var exit = await rig.RunAsync(true, "start", "--no-open", "--json");

        Assert.Equal(0, exit);
        Assert.Empty(rig.Opened);
        Assert.Contains("\"opened\":false", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:4711/ui/", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartWhereTheHolderWasRefusedExitsThreeWithTheDaemonsMessageAndTakesNoLease()
    {
        using var rig = new Rig();
        rig.ChildState = "{\"pid\":999,\"stopped_reason\":\"refused\",\"message\":\"the UI bridge is opened only by the user, from a terminal\"}";

        var exit = await rig.RunAsync(false, "start");

        Assert.Equal(3, exit);
        Assert.Contains("opened only by the user", rig.Err.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, rig.LeaseRequests);
        Assert.Empty(rig.Opened);
    }

    [Fact]
    public async Task TheHolderStartedByAnotherParentRequestsNoLease()
    {
        using var rig = new Rig { ParentPid = 1 };

        var exit = await rig.RunAsync(false, "--service-hold", "100");

        Assert.Equal(1, exit);
        Assert.Equal(0, rig.LeaseRequests);
        Assert.False(File.Exists(rig.StatePath));
    }

    [Fact]
    public async Task TheHolderWritesItsStateWhileItHoldsTheLeaseAndFinishesItWhenStopped()
    {
        using var rig = new Rig();
        using var stop = new CancellationTokenSource();
        var accepted = rig.Server.AcceptAsync();
        var exit = UiServiceVerb.RunAsync(["--service-hold", "100"], rig.Out, rig.Err, rig.Seams, rig.Service, stop.Token);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Server.HoldAsync(Grant);
        for (var i = 0; i < 200 && !File.Exists(rig.StatePath); i++)
        {
            await Task.Delay(10);
        }

        Assert.Contains("\"port\":4711", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
        Assert.Contains("POST /v1/ui/lease?service=1", rig.Server.RequestHead, StringComparison.Ordinal);
        await stop.CancelAsync();

        Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHolderThatFindsAStopOnRecordAfterItsLeaseDropsItAndNeverReportsLive()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        var recorded = "{\"pid\":100,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"stop\"}";
        File.WriteAllText(rig.StatePath, recorded);
        var accepted = rig.Server.AcceptAsync();
        var exit = UiServiceVerb.RunAsync(["--service-hold", "100"], rig.Out, rig.Err, rig.Seams, rig.Service, CancellationToken.None);
        await accepted.WaitAsync(TimeSpan.FromSeconds(10));
        await rig.Server.HoldAsync(Grant);

        Assert.Equal(0, await exit.WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(recorded, File.ReadAllText(rig.StatePath));
    }

    [Fact]
    public async Task StopWithNoRecordLeavesAStopThatAHolderStillToReportWillFind()
    {
        using var rig = new Rig();

        Assert.Equal(0, await rig.RunAsync(false, "stop", "--json"));

        Assert.Contains("\"was_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopOfARefusedRecordCarryingTheDaemonEndedMarkRecordsTheStopSoTheNextDaemonDoesNotReopen()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        File.WriteAllText(rig.StatePath, "{\"pid\":1,\"stopped_reason\":\"refused\",\"daemon_ended_before\":true}\n");
        Assert.True(Ah.Core.Ui.UiService.DaemonEndedIt(Read()), "before the stop the next daemon would reopen");

        Assert.Equal(0, await rig.RunAsync(false, "stop", "--json"));

        Assert.Equal("stop", Read().Get("stopped_reason")?.AsString());
        Assert.False(Ah.Core.Ui.UiService.DaemonEndedIt(Read()));

        Ah.Core.Json.JsonObject Read() => Ah.Core.Json.JsonText.Parse(File.ReadAllText(rig.StatePath)).AsObject();
    }

    [Fact]
    public async Task StopOfARecordWhoseHolderDiedRecordsTheStop()
    {
        using var rig = new Rig();
        rig.Running();
        rig.Alive.Clear();

        Assert.Equal(0, await rig.RunAsync(false, "stop", "--json"));

        Assert.Contains("\"was_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHomeThatNeverStartedReadsNotRunningBeforeAndAfterAStop()
    {
        using var rig = new Rig();

        Assert.Equal(3, await rig.RunAsync(false, "status"));
        Assert.Equal(0, await rig.RunAsync(false, "stop"));
        Assert.Equal(3, await rig.RunAsync(false, "status"));
        Assert.Equal(3, await rig.RunAsync(false, "status", "--json"));

        var lines = rig.Out.ToString().ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("UI not running", lines[0]);
        Assert.Equal("UI not running", lines[2]);
        Assert.DoesNotContain("stopped_reason", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStatusAfterStoppingAServiceThatRanStillSaysItStopped()
    {
        using var rig = new Rig();
        rig.Running();
        Assert.Equal(0, await rig.RunAsync(false, "stop"));

        Assert.Equal(3, await rig.RunAsync(false, "status"));

        Assert.Contains("UI stopped (stop)", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOrdinaryStartAfterAStopStillStarts()
    {
        using var rig = new Rig();
        Assert.Equal(0, await rig.RunAsync(false, "stop"));

        Assert.Equal(0, await rig.RunAsync(true, "start", "--no-open"));

        Assert.Single(rig.Spawned);
        Assert.Contains("\"port\":4711", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopWithAStaleStateSignalsNothing()
    {
        using var rig = new Rig();
        rig.Running();
        rig.Alive[Child] = "T2";

        var exit = await rig.RunAsync(false, "stop", "--json");

        Assert.Equal(0, exit);
        Assert.Empty(rig.Signals);
        Assert.Contains("\"was_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStateThatRecordedTheLocalStartTimeIsStillTheService()
    {
        using var rig = new Rig();
        rig.Running();
        File.WriteAllText(rig.StatePath, "{\"pid\":999,\"pid_start\":\"L1\",\"port\":4711,\"started_at\":\"2026-10-20T00:00:00.000Z\"}");
        rig.LocalStart[Child] = "L1";

        var exit = await rig.RunAsync(false, "stop", "--json");

        Assert.Equal(0, exit);
        Assert.Equal([(Child, 15)], rig.Signals);
        Assert.Contains("\"was_running\":true", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStateWhoseStartTimeIsNeitherTheUtcNorTheLocalOneIsNotTheService()
    {
        using var rig = new Rig();
        rig.Running();
        File.WriteAllText(rig.StatePath, "{\"pid\":999,\"pid_start\":\"L9\",\"port\":4711,\"started_at\":\"2026-10-20T00:00:00.000Z\"}");
        rig.LocalStart[Child] = "L1";

        var exit = await rig.RunAsync(false, "stop", "--json");

        Assert.Equal(0, exit);
        Assert.Empty(rig.Signals);
        Assert.Contains("\"was_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopOnARunningServiceSendsTermAndFinishesTheState()
    {
        using var rig = new Rig();
        rig.Running();

        var exit = await rig.RunAsync(false, "stop", "--json");

        Assert.Equal(0, exit);
        Assert.Equal([(Child, 15)], rig.Signals);
        Assert.Contains("\"was_running\":true", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusOfARunningServiceHasNoCodeInItsUrl()
    {
        using var rig = new Rig();
        rig.Running();

        var exit = await rig.RunAsync(false, "status", "--json");

        Assert.Equal(0, exit);
        Assert.Contains("\"url\":\"http://127.0.0.1:4711/ui/\"", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("#", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusOfAServiceTheDaemonEndedExitsThreeAndNamesTheCommand()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        File.WriteAllText(rig.StatePath, "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"daemon_ended\"}");
        rig.Alive[Child] = "T1";

        var exit = await rig.RunAsync(false, "status", "--json");

        Assert.Equal(3, exit);
        Assert.Contains("\"stopped_reason\":\"daemon_ended\"", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"sign_in\":\"ahd ui start\"", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopOfAServiceTheDaemonEndedRecordsTheStopSoTheNextDaemonDoesNotReopenIt()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        File.WriteAllText(rig.StatePath, "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"daemon_ended\"}");

        var exit = await rig.RunAsync(false, "stop", "--json");

        Assert.Equal(0, exit);
        Assert.Contains("\"was_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopOfAHolderThatSawTheDaemonEndAsTheSignalArrivedStillRecordsTheStop()
    {
        using var rig = new Rig();
        rig.Running();
        rig.OnSignal = () => File.WriteAllText(rig.StatePath, "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"daemon_ended\"}");

        var exit = await rig.RunAsync(false, "stop", "--json");

        Assert.Equal(0, exit);
        Assert.Contains("\"was_running\":true", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopOfAHolderThatRecordedTheStopItselfLeavesItsRecord()
    {
        using var rig = new Rig();
        rig.Running();
        var recorded = "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"stop\"}";
        rig.OnSignal = () => File.WriteAllText(rig.StatePath, recorded);

        Assert.Equal(0, await rig.RunAsync(false, "stop", "--json"));

        Assert.Equal(recorded, File.ReadAllText(rig.StatePath));
    }

    [Fact]
    public async Task AStopWhileAStartHoldsTheLockWaitsAndThenStopsTheHolderThatStartBrought()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        var other = new FileStream(rig.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var stop = rig.RunAsync(false, "stop", "--json");
            await Task.Delay(300);
            Assert.False(stop.IsCompleted, "the stop did not wait for the start");

            rig.Running();
            other.Dispose();

            Assert.Equal(0, await stop);
        }
        finally
        {
            other.Dispose();
        }

        Assert.Equal([(Child, 15)], rig.Signals);
        Assert.Contains("\"was_running\":true", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStopWhoseWaitForTheLockRunsOutGoesOnWithoutIt()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        File.WriteAllText(rig.StatePath, "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"daemon_ended\"}");
        using var other = new FileStream(rig.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(0, await rig.RunAsync(false, "stop", "--json"));

        Assert.Contains("\"was_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStartWhileTheLockIsHeldWaitsAndThenStarts()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        var other = new FileStream(rig.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var start = rig.RunAsync(true, "start", "--no-open");
            await Task.Delay(300);
            Assert.False(start.IsCompleted, "the start did not wait for the lock");
            Assert.Empty(rig.Spawned);

            other.Dispose();

            Assert.Equal(0, await start);
        }
        finally
        {
            other.Dispose();
        }

        Assert.Single(rig.Spawned);
        Assert.Empty(rig.Err.ToString());
    }

    [Fact]
    public async Task StopOfAServiceThatWasRefusedLeavesTheRefusalOnRecord()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        var refused = "{\"pid\":999,\"pid_start\":\"T1\",\"port\":4711,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"refused\",\"message\":\"no\"}";
        File.WriteAllText(rig.StatePath, refused);

        var exit = await rig.RunAsync(false, "stop", "--json");

        Assert.Equal(0, exit);
        Assert.Contains("\"was_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Equal(refused, File.ReadAllText(rig.StatePath));
    }

    [Fact]
    public async Task RestartStopsTheOldHolderThenStartsANewOne()
    {
        using var rig = new Rig();
        rig.Running();

        var exit = await rig.RunAsync(true, "restart", "--no-open", "--json");

        Assert.Equal(0, exit);
        Assert.Equal([(Child, 15)], rig.Signals);
        Assert.Single(rig.Spawned);
        Assert.Contains("\"already_running\":false", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartPrintsTheLinkAsTextByDefaultAndSaysWhenItOpenedTheBrowser()
    {
        using var rig = new Rig();

        var exit = await rig.RunAsync(true, "start");

        Assert.Equal(0, exit);
        Assert.Equal("UI running · pid 999 · http://127.0.0.1:4711/ui/ · opened in the browser\n", rig.Out.ToString().ReplaceLineEndings("\n"));
        Assert.DoesNotContain("{", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartWithNoOpenPrintsTheLinkAndDoesNotClaimTheBrowserOpened()
    {
        using var rig = new Rig();

        _ = await rig.RunAsync(true, "start", "--no-open");

        Assert.Empty(rig.Opened);
        Assert.DoesNotContain("opened", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.Contains("http://127.0.0.1:4711/ui/", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusOfARunningServiceIsOneTextLine()
    {
        using var rig = new Rig();
        rig.Running();

        var exit = await rig.RunAsync(false, "status");

        Assert.Equal(0, exit);
        Assert.StartsWith("UI running · pid 999 · http://127.0.0.1:4711/ui/ · up ", rig.Out.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusOfARefusedServiceNamesTheReasonTheStoredMessageAndWhen()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        File.WriteAllText(rig.StatePath, "{\"pid\":999,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"refused\",\"message\":\"the UI bridge is opened only by the user, from a terminal\"}");
        var local = DateTimeOffset.Parse("2026-10-20T01:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        var exit = await rig.RunAsync(false, "status");

        Assert.Equal(3, exit);
        Assert.Equal($"UI stopped (refused: the UI bridge is opened only by the user, from a terminal) at {local}\n", rig.Out.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task StatusJsonCarriesTheStoredMessage()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.StatePath)!);
        File.WriteAllText(rig.StatePath, "{\"pid\":999,\"stopped_at\":\"2026-10-20T01:00:00.000Z\",\"stopped_reason\":\"refused\",\"message\":\"no\"}");

        _ = await rig.RunAsync(false, "status", "--json");

        Assert.Contains("\"message\":\"no\"", rig.Out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusWithNoStateAndStopOfAServiceThatIsNotRunningAreText()
    {
        using var rig = new Rig();

        var status = await rig.RunAsync(false, "status");
        var stop = await rig.RunAsync(false, "stop");

        Assert.Equal(3, status);
        Assert.Equal(0, stop);
        Assert.Equal("UI not running\nUI was not running\n", rig.Out.ToString().ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task AnUnknownSubcommandOrAStrayOptionIsAUsageError()
    {
        using var rig = new Rig();

        Assert.Equal(2, await rig.RunAsync(false, "stop", "--no-open"));
        Assert.Equal(2, await rig.RunAsync(false, "status", "extra"));
        Assert.Empty(rig.Spawned);
        Assert.Empty(rig.Signals);
    }

    [Fact]
    public async Task StartWhileAnotherStartHoldsTheLockSpawnsNothing()
    {
        using var rig = new Rig();
        Directory.CreateDirectory(Path.GetDirectoryName(rig.LockPath)!);
        using var other = new FileStream(rig.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var exit = await rig.RunAsync(false, "start");

        Assert.Equal(3, exit);
        Assert.Empty(rig.Spawned);
        Assert.Contains("another `ahd ui start` is running", rig.Err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopOfAHolderThatSurvivesKillReportsItAndRestartStartsNothing()
    {
        using var rig = new Rig { SignalsKill = false };
        rig.Running();

        var stopExit = await rig.RunAsync(false, "stop");
        var restartExit = await rig.RunAsync(false, "restart");

        Assert.Equal(1, stopExit);
        Assert.Equal(1, restartExit);
        Assert.Contains("did not stop", rig.Err.ToString(), StringComparison.Ordinal);
        Assert.Empty(rig.Spawned);
        Assert.Equal(0, rig.LeaseRequests);
    }

    [Fact]
    public async Task StopNeverKillsAPidThatNowBelongsToAnotherProcess()
    {
        using var rig = new Rig { PidReused = true };
        rig.Running();

        var exit = await rig.RunAsync(false, "stop");

        Assert.Equal(0, exit);
        Assert.Equal([(Child, 15)], rig.Signals);
        Assert.Contains("\"stopped_reason\":\"stop\"", File.ReadAllText(rig.StatePath), StringComparison.Ordinal);
    }
}
