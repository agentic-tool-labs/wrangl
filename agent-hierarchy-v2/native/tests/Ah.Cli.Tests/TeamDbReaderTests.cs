using System.Globalization;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Platform;
using Ah.Roster;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The team seam answers alike from files and from rows, reads the row with its export gone, and tells a store it cannot read from a team that is absent.</summary>
[Collection("RosterServices")]
[UnsupportedOSPlatform("windows")]
public sealed class TeamDbReaderTests : IDisposable
{
    private const string Doc = """{"version":1,"team_id":"t-a","orchestrator":{"session_id":null,"pid":null},"members":[]}""";

    private readonly Sandbox _sandbox = new();

    public TeamDbReaderTests() => _ = Directory.CreateDirectory(Path.Combine(Hier, "teams"));

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string Hier => Path.Combine(Project, ".claude", "ah2");

    private string TeamFile => Path.Combine(Hier, "teams", "a.json");

    private string Db => Path.Combine(_sandbox.AhHome, "state", "ah.db");

    private sealed class Services(ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd) : IRosterCliServices
    {
        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => graph.Launch.Teardown;

        public IRosterLaunch? Launch => graph.Launch;

        public IRosterCallerRegistry? Callers => null;

        public IRosterOwnerProof? OwnerProof => null;
    }

    private FakeEnv Env() => new(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?>
    {
        ["HOME"] = _sandbox.Home,
        ["AH_HOME"] = _sandbox.AhHome,
        ["AH_DAEMON"] = "off",
        ["AH_TEST"] = "1",
        ["WRANGL_HIERARCHY_DIR"] = Hier,
    });

    private ServiceGraph Graph() => ServiceGraph.Build(PlatformModule.CreateIo(null), Env(), new FakeRunner(), new FakeDaemonClient());

    private void CutOver()
    {
        using var db = StateDbModule.Open(_sandbox.AhHome, mayCreate: true);
    }

    /// <summary>A database cut over, with team a in it, whose export is gone: only a read of the row can find it.</summary>
    private ServiceGraph RowOnly()
    {
        CutOver();
        var graph = Graph();
        TestPlant.Team(graph.Roster, graph.Writer, TeamFile, Doc);
        File.Delete(TeamFile);
        return graph;
    }

    private (int Code, string Stdout, string Stderr) Roster(params string[] args)
    {
        var env = Env();
        var io = PlatformModule.CreateIo(null);
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), new FakeDaemonClient());
        CommandsModule.ComposeRoster(() => new Services(graph, io.Files, io.Clock, env, Project));
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster").Run(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
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
        TestPlant.Team(graph.Roster, graph.Writer, TeamFile, Doc);

        Assert.Equal("t-a", graph.Roster.ReadTeamFileAt(TeamFile).Member("team_id")!.Value.AsString());
        Assert.True(graph.Roster.TeamExistsAt(TeamFile));
        Assert.Equal(["a"], graph.Roster.ListTeamNames(Hier));
        Assert.Equal(graph.Roster.TeamPath(Hier, "a"), TeamFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NegativeAnUnknownTeamIsAbsentInBothModes(bool database)
    {
        if (database)
        {
            CutOver();
        }

        var graph = Graph();
        TestPlant.Team(graph.Roster, graph.Writer, TeamFile, Doc);
        var other = Path.Combine(Hier, "teams", "nobody.json");

        Assert.True(graph.Roster.ReadTeamFileAt(other).IsNull);
        Assert.False(graph.Roster.TeamExistsAt(other));
        Assert.Equal(["a"], graph.Roster.ListTeamNames(Hier));
    }

    [Fact]
    public void WithTheExportDeletedEveryReadFindsTheRow()
    {
        var graph = RowOnly();

        Assert.False(File.Exists(TeamFile));
        Assert.Equal("t-a", graph.Roster.ReadTeamFileAt(TeamFile).Member("team_id")!.Value.AsString());
        Assert.True(graph.Roster.TeamExistsAt(graph.Roster.TeamPath(Hier, "a")));
        Assert.Equal(["a"], graph.Roster.ListTeamNames(Hier));
        Assert.Equal(["a"], graph.Roster.TeamNamesIn(Hier));
        Assert.Equal("usable", graph.Roster.TeamFileStateAt(TeamFile)["state"].AsString());
        Assert.Equal("absent", graph.Roster.TeamFileStateAt(Path.Combine(Hier, "teams", "nobody.json"))["state"].AsString());
    }

    [Fact]
    public void NegativeAHomeWithNoDatabaseHasNoTeamAndNoNamesFromOne()
    {
        Directory.Delete(Path.Combine(_sandbox.AhHome, "state"), recursive: true);
        var graph = Graph();

        Assert.False(graph.Roster.TeamExistsAt(TeamFile));
        Assert.Null(graph.Roster.TeamNamesIn(Hier));
        Assert.Null(graph.Roster.TeamsChangeToken());
    }

    [Fact]
    public void AnUnreadableStoreThrowsTheOneTypeAndNeverReadsAsAbsent()
    {
        var graph = RowOnly();
        var other = Path.Combine(Hier, "teams", "nobody.json");
        File.WriteAllText(Db, new string('x', 8192));

        _ = Assert.Throws<StoreUnavailableException>(() => graph.Roster.ReadTeamFileAt(TeamFile));
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Roster.ReadTeamFileAt(other));
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Roster.TeamExistsAt(TeamFile));
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Roster.ListTeamNames(Hier));
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Roster.TeamFileStateAt(TeamFile));
        _ = Assert.Throws<StoreUnavailableException>(() => graph.Roster.TeamsChangeToken());
    }

    [Fact]
    public void ATeamChangedByAnotherProcessMovesTheToken()
    {
        CutOver();
        var first = Graph();
        var before = first.Roster.TeamsChangeToken();
        var stampBefore = first.Roster.TeamStamp(TeamFile);

        var second = Graph();
        TestPlant.Team(second.Roster, second.Writer, TeamFile, Doc);

        Assert.NotEqual(before, first.Roster.TeamsChangeToken());
        Assert.NotEqual(stampBefore, first.Roster.TeamStamp(TeamFile));
    }

    [Fact]
    public void NegativeAReadLeavesTheTokenAlone()
    {
        var graph = RowOnly();
        var before = graph.Roster.TeamsChangeToken();

        _ = graph.Roster.ReadTeamFileAt(TeamFile);
        _ = graph.Roster.ListTeamNames(Hier);

        Assert.Equal(before, graph.Roster.TeamsChangeToken());
    }

    [Fact]
    public void TeamShowPrintsTheRowWithItsExportGone()
    {
        _ = RowOnly();

        var pretty = Roster("team", "show", "--team", "a", "--cwd", Project);
        var line = Roster("team", "show", "--team", "a", "--json", "--cwd", Project);

        Assert.True(pretty.Code == 0, pretty.Stderr);
        Assert.Equal("t-a", JsonText.Parse(pretty.Stdout).Member("team_id")!.Value.AsString());
        Assert.Contains('\n', pretty.Stdout.Trim());
        Assert.True(line.Code == 0, line.Stderr);
        Assert.DoesNotContain('\n', line.Stdout.Trim());
    }

    [Fact]
    public void NegativeTeamShowRefusesAnUnknownTeamFlagAndSubcommand()
    {
        _ = RowOnly();

        var noTeam = Roster("team", "show", "--team", "nobody", "--cwd", Project);
        var flag = Roster("team", "show", "--bogus", "--cwd", Project);
        var sub = Roster("team", "list", "--cwd", Project);
        var bare = Roster("team", "--cwd", Project);

        Assert.NotEqual(0, noTeam.Code);
        Assert.Contains("team show: no team nobody", noTeam.Stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, flag.Code);
        Assert.Contains("unrecognized flag --bogus", flag.Stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, sub.Code);
        Assert.NotEqual(0, bare.Code);
    }

    [Fact]
    public void WithTheExportDeletedTheHierarchyAndCutoverSitesFindTheRow()
    {
        var graph = RowOnly();
        var request = Path.Combine(Hier, "msgs", "20261104-000000-aaaa--a--b--request.md");

        Assert.Equal(Hier, graph.Hierarchy.Messages.MessageHome(request, JsonValue.FromObject(new JsonObject { ["team_file"] = TeamFile })));
        Assert.True(graph.Roster.TeamExistsAt(graph.Roster.TeamPath(Hier, "a")));
    }

    [Fact]
    public void NegativeTheSitesSayNoTeamForATeamNoRowHolds()
    {
        var graph = RowOnly();
        var other = Path.Combine(Hier, "teams", "nobody.json");
        var request = Path.Combine(Hier, "msgs", "20261104-000000-aaaa--a--b--request.md");

        Assert.Null(graph.Hierarchy.Messages.MessageHome(request, JsonValue.FromObject(new JsonObject { ["team_file"] = other })));
        Assert.False(graph.Roster.TeamExistsAt(graph.Roster.TeamPath(Hier, "nobody")));
    }

    [Fact]
    public void TeamShowOnAnUnreadableStoreIsBusyNotAbsent()
    {
        _ = RowOnly();
        File.WriteAllText(Db, new string('x', 8192));

        var (code, stdout, stderr) = Roster("team", "show", "--team", "a", "--cwd", Project);

        Assert.True(code == 2, $"exit {code}: {stdout}{stderr}");
        Assert.Equal("store-unreadable", JsonText.Parse(stdout).Member("refused")?.AsString());
    }
}
