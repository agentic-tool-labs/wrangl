using System.Runtime.Versioning;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The registration seam answers alike from files and rows, reads the row with its export gone, and tells a store it cannot read from a session with no registration.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class SessionDbReaderTests : IDisposable
{
    private const string Session = "s-1";

    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string File1 => Path.Combine(_sandbox.AhHome, "registrations", Session + ".json");

    private string Db => Path.Combine(_sandbox.AhHome, "state", "ah.db");

    private ServiceGraph Graph() => ServiceGraph.Build(
        PlatformModule.CreateIo(null),
        new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = _sandbox.AhHome, ["AH_DAEMON"] = "off", ["AH_TEST"] = "1" }),
        new FakeRunner(),
        new FakeDaemonClient());

    private void CutOver()
    {
        _ = Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "registrations"));
        using var db = StateDbModule.Open(_sandbox.AhHome, mayCreate: true);
    }

    private void Plant(ServiceGraph graph)
    {
        var teamFile = Path.Combine(_sandbox.Root, "proj", ".claude", "ah2", "teams", "t.json");
        TestPlant.Registration(graph.Writer, Session, teamFile, _sandbox.Root, ("a@t", graph.Mailbox.BoxDirFor(teamFile, "a")));
    }

    /// <summary>A database cut over, with one registration in it, whose export is gone.</summary>
    private ServiceGraph RowOnly()
    {
        CutOver();
        var graph = Graph();
        Plant(graph);
        File.Delete(File1);
        return graph;
    }

    [Fact]
    public void AllRegistrationsKeepsItsRowsUntilTheTokenMoves()
    {
        var graph = RowOnly();

        var first = graph.Mailbox.AllRegistrations();
        Assert.Same(first, graph.Mailbox.AllRegistrations());

        Assert.True(graph.Mailbox.RemoveRegistration(Session));

        var after = graph.Mailbox.AllRegistrations();
        Assert.NotSame(first, after);
        Assert.Empty(after);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSeamAnswersTheSameFromFilesAndFromRows(bool database)
    {
        if (database)
        {
            CutOver();
        }

        var graph = Graph();
        Plant(graph);

        Assert.Equal("a@t", graph.Mailbox.ReadRegistration(Session)!.Get("address")!.Value.AsString());
        Assert.Equal(Session, Assert.Single(graph.Mailbox.AllRegistrations()).Get("session_id")!.Value.AsString());
        Assert.Null(graph.Mailbox.ReadRegistration("s-nobody"));
        Assert.Equal(Session, graph.Inspect.Registrations()["registrations"].AsArray().Single().Member("session_id")!.Value.AsString());
    }

    [Fact]
    public void WithTheExportDeletedEveryReadFindsTheRow()
    {
        var graph = RowOnly();

        Assert.False(File.Exists(File1));
        Assert.NotNull(graph.Mailbox.ReadRegistration(Session));
        Assert.Single(graph.Mailbox.AllRegistrations());
        Assert.Equal([Session], graph.Mailbox.StoredSessionIds());
        Assert.Equal([Session], graph.Mailbox.StoredSessionStamps()!.Keys);
        Assert.Single(graph.Inspect.Registrations()["registrations"].AsArray());
        Assert.NotNull(graph.Mailbox.RegistrationWrittenMs(Session));
    }

    [Fact]
    public void NegativeAHomeWithNoDatabaseReadsNothingFromOne()
    {
        Directory.Delete(Path.Combine(_sandbox.AhHome, "state"), recursive: true);
        var graph = Graph();

        Assert.Null(graph.Mailbox.StoredSessionIds());
        Assert.Null(graph.Mailbox.StoredSessionStamps());
        Assert.Null(graph.Mailbox.SessionsChangeToken());
        Assert.Null(graph.Mailbox.RegistrationWrittenMs(Session));
    }

    [Fact]
    public void NegativeAnEndedSessionIsAbsent()
    {
        var graph = RowOnly();

        Assert.True(graph.Mailbox.RemoveRegistration(Session));

        Assert.Null(graph.Mailbox.ReadRegistration(Session));
        Assert.Empty(graph.Mailbox.AllRegistrations());
    }

    [Fact]
    public void AnUnreadableStoreThrowsTheOneTypeAndNeverReadsAsAbsent()
    {
        var graph = RowOnly();
        File.WriteAllText(Db, new string('x', 8192));

        _ = Assert.Throws<StoreUnavailableException>(() => graph.Mailbox.ReadRegistration(Session));
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Mailbox.ReadRegistration("s-nobody"));
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Mailbox.AllRegistrations());
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Mailbox.LiveRegistrations());
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Mailbox.SessionsChangeToken());
    }

    [Fact]
    public void ARegistrationByAnotherProcessMovesTheToken()
    {
        CutOver();
        var first = Graph();
        var before = first.Mailbox.SessionsChangeToken();

        Plant(Graph());

        Assert.NotEqual(before, first.Mailbox.SessionsChangeToken());
    }

    [Fact]
    public void NegativeAReadLeavesTheTokenAlone()
    {
        var graph = RowOnly();
        var before = graph.Mailbox.SessionsChangeToken();

        _ = graph.Mailbox.AllRegistrations();
        _ = graph.Mailbox.ReadRegistration(Session);

        Assert.Equal(before, graph.Mailbox.SessionsChangeToken());
    }
}
