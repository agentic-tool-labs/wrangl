using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The body of the state-prune job: one backup before the first deletion, none when nothing goes, nothing deleted when the backup fails.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class StatePruneRunTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static string Source([CallerFilePath] string here = "") => here;

    private (ServiceGraph Graph, string Dir) Build()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Source())!, "..", "..", ".."));
        var env = new FakeEnv(
            _sandbox.Home,
            _sandbox.AhHome,
            new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home },
            new PluginInfo(root, "wrangl", "0.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var dir = Path.Combine(_sandbox.Home, "proj");
        Directory.CreateDirectory(dir);
        return (graph, dir);
    }

    private static double NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // peer rows live in the store's peer_records: planted through the real append, counted through the hierarchy's own read
    private void WritePeers(ServiceGraph graph, params string[] rows) => Ah.TestKit.TestPlant.PeerRows(graph.Writer, _sandbox.Home, rows);

    private static int PeerCount(ServiceGraph graph) => graph.Hierarchy.Core.ReadPeerRecordsKnown()!.Count;

    private static string OldRow(double ageDays) =>
        "{\"type\":\"watch-event\",\"session_id\":\"s\",\"ts\":\"" + (DateTime.UtcNow - TimeSpan.FromDays(ageDays)).ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\"}";

    private sealed class Backup(bool fail = false)
    {
        internal int Calls { get; private set; }

        internal string Take()
        {
            Calls++;
            return fail ? throw new InvalidOperationException("boom") : "pre-prune-1";
        }
    }

    [Fact]
    public void AMessageStoreThatCannotBeReadSkipsThePruneBeforeAnyBackupInsteadOfCallingEveryRequestClosed()
    {
        var dir = Path.Combine(_sandbox.Home, "proj");
        Directory.CreateDirectory(dir);
        var env = new FakeEnvironment(new Dictionary<string, string> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = _sandbox.AhHome, ["TZ"] = "UTC" });
        var io = PlatformModule.CreateIo(null);
        Ah.Roster.IRosterCore roster = null!;
        var facts = new Ah.Config.ProcessFacts(_sandbox.Home, 4242);
        var config = Ah.Config.ConfigModule.CreateCore(io.Files, env, () => roster, facts);
        roster = Ah.Roster.RosterModule.CreateCore(io.Files, env, config, io.Processes);
        var hierarchy = Ah.Hierarchy.HierarchyModule.Create(new Ah.Hierarchy.HierarchyEnvironment(
            io.Files, env, new FakeClock(DateTimeOffset.UtcNow), new SequenceRandom(), io.Processes, config, roster, null, facts, null, null, null, null,
            new Ah.Hierarchy.MsgsRoute(new UnreadableMsgs(), new DbMsgsMode())));
        var backup = new Backup();
        var log = new List<string>();

        var result = StatePrune.Run(hierarchy, [dir], NowMs, 14, backup.Take, log.Add, () => []);

        Assert.Contains("msgs_read=failed", result, StringComparison.Ordinal);
        Assert.Equal(0, backup.Calls);
    }

    private sealed class DbMsgsMode : IMessageModeReader
    {
        public string Read(string domain) => "db";
    }

    private sealed class UnreadableMsgs : Ah.Core.Json.IMsgScope
    {
        public Ah.Core.Json.IMsgScope Scope() => this;

        public void Dispose()
        {
        }

        public IReadOnlyList<Ah.Core.Json.MsgExchangePair> Exchanges(IReadOnlyList<string> poolDirs) => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<Ah.Core.Json.MsgFileRow> Messages(string poolDir, string? id = null, string? type = null) => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<Ah.Core.Json.MsgFileRow> ArchivedMessages(string poolDir) => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<Ah.Core.Json.MsgFileRow> MessagesById(string id, string type) => throw new InvalidOperationException("the state database cannot be read");

        public Ah.Core.Json.MsgFileRow? ByPath(string path) => throw new InvalidOperationException("the state database cannot be read");

        public long Version() => throw new InvalidOperationException("the state database cannot be read");

        public IReadOnlyList<string> ChangedIds(string poolDir, long sinceVersion) => throw new InvalidOperationException("the state database cannot be read");
    }

    [Fact]
    public void WhenNothingIsToGoThereIsNoBackupNoLogLineAndNoResult()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(0));
        var backup = new Backup();
        var log = new List<string>();

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, backup.Take, log.Add);

        Assert.Null(result);
        Assert.Equal(0, backup.Calls);
        Assert.Empty(log);
    }

    [Fact]
    public void AnOldPeerRowGoesAfterExactlyOneBackupAndTheCountsAreLogged()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(30), OldRow(0));
        var backup = new Backup();
        var log = new List<string>();

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, backup.Take, log.Add);

        Assert.Equal(1, backup.Calls);
        Assert.Equal(1, PeerCount(graph));
        Assert.NotNull(result);
        Assert.StartsWith("mode=on ", result, StringComparison.Ordinal);
        Assert.Contains("peer_dropped=1", result, StringComparison.Ordinal);
        Assert.Contains("backup=pre-prune-1", result, StringComparison.Ordinal);
        Assert.Equal("state prune: " + result, Assert.Single(log));
    }

    [Fact]
    public void NegativeWhenTheBackupFailsNothingIsDeletedAndTheSkipIsLogged()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(30));
        var backup = new Backup(fail: true);
        var log = new List<string>();

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, backup.Take, log.Add);

        Assert.Null(result);
        Assert.Equal(1, backup.Calls);
        Assert.Equal(1, PeerCount(graph));
        Assert.Equal("state prune skipped: backup failed: boom", Assert.Single(log));
    }

    [Fact]
    public void CountModeReportsWhatWouldGoWithNoBackupNoDeletionAndModeCountOnTheLine()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(30), OldRow(0));
        var backup = new Backup();
        var log = new List<string>();

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, backup.Take, log.Add, countOnly: true);

        Assert.NotNull(result);
        Assert.StartsWith("mode=count ", result, StringComparison.Ordinal);
        Assert.Contains("peer_would_drop=1", result, StringComparison.Ordinal);
        Assert.Equal(0, backup.Calls);
        Assert.Equal(2, PeerCount(graph));
        Assert.Equal("state prune: " + result, Assert.Single(log));
    }

    [Fact]
    public void WithTheDefaultSettingsARowFifteenDaysOldIsDeletedAndOneThirteenDaysOldIsKept()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(15), OldRow(13), OldRow(0)); // the fresh row keeps the session out of the idle-session rule

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, new Backup().Take, _ => { }, countOnly: StatePrune.CountOnly(graph.Config.StateSettings()));

        Assert.StartsWith("mode=on ", result, StringComparison.Ordinal);
        Assert.Contains("peer_dropped=1", result, StringComparison.Ordinal);
        Assert.Equal(2, PeerCount(graph));
    }

    [Theory]
    [InlineData("count")]
    [InlineData("off")] // not a recognised value: a set but unknown value fails safe to count
    public void NegativeWithPruneSetToCountOrAnUnknownValueTheSameRowsAreCountedAndNoneIsDeleted(string mode)
    {
        var (graph, dir) = Build();
        var config = graph.Config.UserConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "{\"state\":{\"prune\":\"" + mode + "\"}}\n");
        WritePeers(graph, OldRow(15), OldRow(13), OldRow(0)); // the fresh row keeps the session out of the idle-session rule

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, new Backup().Take, _ => { }, countOnly: StatePrune.CountOnly(graph.Config.StateSettings()));

        Assert.StartsWith("mode=count ", result, StringComparison.Ordinal);
        Assert.Contains("peer_would_drop=1", result, StringComparison.Ordinal);
        Assert.Equal(3, PeerCount(graph));
    }

    [Fact]
    public void CountModeWithNothingToDropStillReturnsItsLineButWritesNoLogLine()
    {
        var (graph, dir) = Build();
        var log = new List<string>();

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, new Backup().Take, log.Add, countOnly: true);

        Assert.Contains("peer_would_drop=0 ", result, StringComparison.Ordinal);
        Assert.Empty(log);
    }

    [Fact]
    public void AnUnreadablePeerLogDropsNothingAndSaysSoInTheCountsLine()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(30));
        var backup = new Backup();
        var log = new List<string>();

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, backup.Take, log.Add, () => null);

        Assert.Contains("peer_read=failed", result, StringComparison.Ordinal);
        Assert.Equal(0, backup.Calls);
        Assert.Equal(1, PeerCount(graph));
        Assert.Contains("peer_read=failed", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public void ABackupOfAHomeWithNoDatabaseIsNoneAndPruningGoesAheadWhileABrokenDatabaseStopsIt()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(30));
        var backups = Ah.StateDb.StateDbModule.CreateBackups();

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, () => StatePrune.Backup(backups, Path.Combine(_sandbox.Root, "nohome"), DateTimeOffset.UtcNow), _ => { }); // a home with no database

        Assert.Contains("backup=none", result, StringComparison.Ordinal);
        Assert.Contains("peer_dropped=1", result, StringComparison.Ordinal);

        WritePeers(graph, OldRow(30));
        var log = new List<string>();

        var stopped = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, () => throw new InvalidOperationException("no room"), log.Add);

        Assert.Null(stopped);
        Assert.StartsWith("state prune skipped: backup failed", Assert.Single(log), StringComparison.Ordinal);

        // a database that cannot be read stops the prune at the listing, before any backup, and the backup itself refuses it too
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "state"));
        File.WriteAllText(Path.Combine(_sandbox.AhHome, "state", "ah.db"), "not a database");

        var unread = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, () => StatePrune.Backup(backups, _sandbox.AhHome, DateTimeOffset.UtcNow), log.Add);

        Assert.Contains("msgs_read=failed", unread, StringComparison.Ordinal);
        _ = Assert.Throws<InvalidOperationException>(() => StatePrune.Backup(backups, _sandbox.AhHome, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ARunTouchesOnlyTheConfiguredHomeAndLeavesAPeerFileInAnotherHomeAlone()
    {
        var (graph, dir) = Build();
        WritePeers(graph, OldRow(30));
        var decoyHome = Path.Combine(_sandbox.Root, "decoy-home");
        var decoy = Ah.Core.Text.AhPaths.UserEntry(decoyHome, Ah.Core.Text.AhPaths.PeerPendingFile);
        Directory.CreateDirectory(Path.GetDirectoryName(decoy)!);
        var decoyText = OldRow(30) + "\n";
        File.WriteAllText(decoy, decoyText);

        var result = StatePrune.Run(graph.Hierarchy, [dir], NowMs, 14, new Backup().Take, _ => { });

        Assert.NotNull(result);
        Assert.Equal(decoyText, File.ReadAllText(decoy));
        Assert.StartsWith(_sandbox.Home, graph.Hierarchy.Peers.PeerPendingPath(), StringComparison.Ordinal);
    }
}
