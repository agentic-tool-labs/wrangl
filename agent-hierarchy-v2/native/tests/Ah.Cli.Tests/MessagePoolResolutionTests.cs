using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
[Collection("RosterServices")]
public sealed partial class MessagePoolResolutionTests : IDisposable
{
    private const string Id = "20261018-120000-abcd";
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();

    public MessagePoolResolutionTests()
    {
        Directory.CreateDirectory(Main);
        Git(Main, "init", "-q");
        Git(Main, "-c", "user.name=Pool Test", "-c", "user.email=pool@example.invalid", "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-qm", "fixture");
        Git(Main, "worktree", "add", "-q", "-b", "member", Worktree);
        Directory.CreateDirectory(Other);
        Git(Other, "init", "-q");
        Directory.CreateDirectory(Path.Combine(Legacy, "msgs"));
        WriteTeam(TeamFile);
        _sandbox.Executable("bin/herdr");
    }

    private string Main => Path.Combine(_sandbox.Root, "main");

    private string Worktree => Path.Combine(Main, ".claude", "worktrees", "w");

    private string Home => Path.Combine(Main, ".claude", "ah2");

    private string Legacy => Path.Combine(Worktree, ".claude", "ah2");

    private string Other => Path.Combine(_sandbox.Root, "other");

    private string OtherPool => Path.Combine(Other, ".claude", "ah2");

    private string TeamFile => Path.Combine(Home, "teams", "t.json");

    public void Dispose()
    {
        Ah.Commands.CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    internal static void Git(string root, params string[] args)
    {
        var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .Where(entry => !((string)entry.Key).StartsWith("GIT_", StringComparison.Ordinal))
            .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal);
        environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        environment["GIT_CONFIG_SYSTEM"] = "/dev/null";
        var result = PlatformModule.CreateProcessRunner().Run(new ProcessRequest(
            ["git", "-C", root, "-c", "core.hooksPath=/dev/null", .. args],
            Environment: environment, Timeout: TimeSpan.FromSeconds(10)));
        Assert.True(result.ExitCode == 0, $"git exited {result.ExitCode}: {System.Text.Encoding.UTF8.GetString(result.Stderr.Span)}");
    }

    private void WriteTeam(string path)
    {
        _sandbox.PlantTeam(path, JsonText.Stringify(new JsonObject
        {
            ["team_id"] = "t",
            ["transport"] = "herdr",
            ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = FakeRunner.SelfStart, ["session_id"] = "o" },
            ["members"] = new JsonArray { new JsonObject
            {
                ["name"] = "m", ["role"] = "implementor", ["kind"] = "pi", ["route"] = "pane", ["transport_id"] = "w1:p1", ["expected_root"] = Worktree,
            } },
        }, 0));
    }

    private IHierarchyMessages Messages(string? hierarchyOverride = null, bool routed = false)
        => Graph(hierarchyOverride, routed).Hierarchy.Messages;

    private ServiceGraph Graph(string? hierarchyOverride = null, bool routed = false, FakeRunner? runner = null)
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home,
            ["PATH"] = _sandbox.Bin, ["AH_DELIVER_POLL_MS"] = "5", ["AH_COMPOSER_REREAD_MS"] = "0", ["CLAUDE_CODE_SESSION_ID"] = "o",
        };
        if (hierarchyOverride is not null)
        {
            variables["WRANGL_HIERARCHY_DIR"] = hierarchyOverride;
        }
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var daemon = new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") };
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, runner ?? FakeRunner.KnowingSelf(), daemon);
        if (routed)
        {
            daemon.Info = FakeDaemonClient.Up();
            daemon.Reply = request =>
            {
                if (request.PathAndQuery == "/v1/health")
                {
                    return new DaemonReply(200, "{\"impl\":\"native\",\"protocols\":[1]}"u8.ToArray());
                }

                if (!request.PathAndQuery.StartsWith("/v1/state/", StringComparison.Ordinal))
                {
                    return new DaemonReply(0, default, "down");
                }

                var parts = request.PathAndQuery.Split('/');
                var reply = graph.State.HandleAsync(request.Method, parts[3], parts[4], new Dictionary<string, string>(), request.Body,
                    static (_, work) => Task.FromResult(work())).GetAwaiter().GetResult();
                return new DaemonReply(reply.Status, reply.Body);
            };
        }

        return graph;
    }

    private static string WriteMessage(string pool, string? teamFile, string type = "request", string id = Id, bool nullTeam = false, string extra = "", bool store = true)
    {
        Directory.CreateDirectory(Path.Combine(pool, "msgs"));
        var path = Path.Combine(pool, "msgs", $"{id}--implementor--pools--{type}.md");
        var team = teamFile is not null ? "team_file: " + teamFile + "\n" : nullTeam ? "team_file: null\n" : "";
        File.WriteAllText(path, $"---\nid: {id}\ntype: {type}\nto: implementor\nfrom: orchestrator\nslug: pools\n{team}{extra}---\n\nrequest\n");
        if (store)
        {
            Ah.TestKit.TestMsgStore.Store(path);
        }

        return path;
    }

    [Fact]
    public void LocateFindsHomeAndRecordedLegacyRequestsButNotAnUnrecordedPool()
    {
        var messages = Messages();
        var home = WriteMessage(Home, TeamFile);
        var legacy = WriteMessage(Legacy, TeamFile, id: "20261018-120001-abcd");
        WriteMessage(OtherPool, TeamFile, id: "20261018-120002-abcd");

        Assert.Equal(MsgRows.PathKey(home), MsgRows.PathKey(messages.Locate(Id, "request", [TeamFile], []).Member("path")!.Value.AsString()));
        Assert.Equal(MsgRows.PathKey(legacy), MsgRows.PathKey(messages.Locate("20261018-120001-abcd", "request", [TeamFile], []).Member("path")!.Value.AsString()));
        Assert.True(messages.Locate("20261018-120002-abcd", "request", [TeamFile], []).IsNull);
        Assert.True(messages.Locate(Id, "response", [TeamFile], []).IsNull);
    }

    [Fact]
    public void HomeWinsOverTheSameIdInALegacyPool()
    {
        WriteMessage(Home, TeamFile);
        WriteMessage(Legacy, TeamFile);

        var found = Messages().Locate(Id, "request", [TeamFile], [Legacy]);

        Assert.Equal(Home, found.Member("pool")!.Value.AsString());
        Assert.Equal(TeamFile, found.Member("team_file")!.Value.AsString());
    }

    [Fact]
    public void TeamOrderPrecedesFallbacksAndAFallbackHitHasNoTeamFile()
    {
        var otherTeam = Path.Combine(OtherPool, "teams", "u.json");
        WriteTeam(otherTeam);
        WriteMessage(Home, TeamFile);
        var other = WriteMessage(OtherPool, otherTeam);
        var messages = Messages();

        Assert.Equal(Home, messages.Locate(Id, "request", [TeamFile, otherTeam], [OtherPool]).Member("pool")!.Value.AsString());
        Assert.Equal(OtherPool, messages.Locate(Id, "request", [otherTeam, TeamFile], [Home]).Member("pool")!.Value.AsString());
        var fallback = messages.Locate(Id, "request", [], [OtherPool]);
        Assert.Equal(MsgRows.PathKey(other), MsgRows.PathKey(fallback.Member("path")!.Value.AsString()));
        Assert.True(fallback.Member("team_file")!.Value.IsNull);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResponsePoolAllowsHomeAndTheRecordedLegacyPool(bool legacy)
    {
        var pool = legacy ? Legacy : Home;
        var path = WriteMessage(pool, TeamFile);

        var result = Messages().ResponsePool(path);

        Assert.Equal("allowed", result.Get("status")!.Value.AsString());
        Assert.Equal(pool, result.Get("pool")!.Value.AsString());
        Assert.Equal(TeamFile, result.Get("team_file")!.Value.AsString());
    }

    [Fact]
    public void ResponsePoolRefusesUnrecordedPools()
    {
        var path = WriteMessage(OtherPool, TeamFile);

        var result = Messages().ResponsePool(path);

        Assert.Equal("refused", result.Get("status")!.Value.AsString());
        Assert.Contains("is outside the request's hierarchy directories", result.Get("reason")!.Value.AsString(), StringComparison.Ordinal);
        Assert.False(File.Exists(path.Replace("--request.md", "--response.md", StringComparison.Ordinal)));
    }

    [Fact]
    public void ARequestWithoutATeamFileReturnsNoTeam()
    {
        var path = WriteMessage(Home, null);

        Assert.Equal("no-team", Messages().ResponsePool(path).Get("status")!.Value.AsString());
    }

    /// <summary>The store holds no team for either path, so a file that is not a team reads as no team (db-only), as a deleted one does.</summary>
    [Fact]
    public void ARequestWhoseTeamFileWasDeletedOrIsNotATeamReturnsNoTeam()
    {
        var deleted = WriteMessage(Home, Path.Combine(Home, "teams", "gone.json"));
        var notTeam = WriteMessage(Legacy, _sandbox.Write("not-a-team-2.json", "{}"));
        var messages = Messages();

        Assert.Equal("no-team", messages.ResponsePool(deleted).Get("status")!.Value.AsString());
        Assert.Equal("no-team", messages.ResponsePool(notTeam).Get("status")!.Value.AsString());
    }

    [Theory]
    [InlineData("null")]
    [InlineData("empty")]
    public void ARequestWhoseTeamFileIsNullOrEmptyReturnsNoTeam(string scenario)
    {
        var path = WriteMessage(Home, scenario == "empty" ? string.Empty : null, nullTeam: scenario == "null");

        var result = Messages().ResponsePool(path);

        Assert.Equal("no-team", result.Get("status")!.Value.AsString());
        Assert.Null(result.Get("reason"));
    }

    [Fact]
    public void ARequestFiledUnderMsgsArchiveIsJudgedByItsOwnPool()
    {
        var archived = ArchiveOf(WriteMessage(Home, TeamFile));
        var planted = ArchiveOf(WriteMessage(OtherPool, TeamFile));
        var messages = Messages();

        var allowed = _sandbox.Mirrored(() => messages.ResponsePool(archived));
        Assert.Equal("allowed", allowed.Get("status")!.Value.AsString());
        Assert.Equal(Home, allowed.Get("pool")!.Value.AsString());
        Assert.Equal("refused", _sandbox.Mirrored(() => messages.ResponsePool(planted)).Get("status")!.Value.AsString());
    }

    private string ArchiveOf(string live)
    {
        var moved = Path.Combine(Path.GetDirectoryName(live)!, "archive", Path.GetFileName(live));
        var text = File.ReadAllText(live);
        Ah.TestKit.TestMsgStore.Delete(_sandbox.AhHome, live);
        return Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, moved, text);
    }

    [Fact]
    public void FamilyRejectsMissingMalformedAndNonTeamFilesAndDeduplicatesRealPaths()
    {
        var alias = Path.Combine(Main, "alias");
        Directory.CreateSymbolicLink(alias, Worktree);
        var record = Graph().Roster.ReadTeamWithDigest(TeamFile)["doc"].AsObject();
        record["streams"] = new JsonObject { ["alias"] = new JsonObject { ["worktree"] = alias } };
        _sandbox.PlantTeam(TeamFile, JsonText.Stringify(record, 0));
        var messages = Messages();

        Assert.Equal([Home, Legacy], messages.Family(TeamFile));
        Assert.Empty(messages.Family(Path.Combine(Home, "teams", "missing.json")));
        Assert.Empty(messages.Family(_sandbox.Write("not-team.json", "{}")));
        Assert.Empty(messages.Family(_sandbox.Write("hier/teams/bad.json", "not json")));
        Assert.Empty(messages.Family(_sandbox.Write("hier/teams/wrong.txt", "{\"members\":[]}")));
    }

    [Fact]
    public void ADefaultTeamFileKeepsItsFamilyAndARealpathAliasOfARecordedPoolIsAllowed()
    {
        var team = Path.Combine(Home, "team.json");
        WriteTeam(team);
        var request = WriteMessage(Legacy, team);
        var alias = Path.Combine(_sandbox.Root, "pool-alias");
        Directory.CreateSymbolicLink(alias, Legacy);
        var messages = Messages();

        Assert.Equal([Home, Legacy], messages.Family(team));
        var result = messages.ResponsePool(Path.Combine(alias, "msgs", Path.GetFileName(request)));
        Assert.Equal("allowed", result.Get("status")!.Value.AsString());
        Assert.Equal(alias, result.Get("pool")!.Value.AsString());
    }

    [Fact]
    public void AResponseFromMainIsWrittenBesideItsRecordedLegacyRequest()
    {
        var request = WriteMessage(Legacy, TeamFile);
        var made = _sandbox.Mirrored(() => Messages(routed: true).CreateMessage(Home, ResponseOptions(request, Main)));

        var path = made.Member("path")!.Value.AsString();
        Assert.Equal(Path.Combine(Legacy, "msgs"), Path.GetDirectoryName(path));
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
    }

    [Fact]
    public void AResponseFromTheWorktreeStillLandsBesideItsRequest()
    {
        var request = WriteMessage(Legacy, TeamFile);
        var made = Messages(routed: true).CreateMessage(Legacy, ResponseOptions(request, Worktree));

        Assert.Equal(Path.Combine(Legacy, "msgs"), Path.GetDirectoryName(made.Member("path")!.Value.AsString()));
    }

    [Fact]
    public void ARequestPlantedOutsideItsTeamsFamilyCannotBeAnswered()
    {
        var request = WriteMessage(OtherPool, TeamFile);

        var error = Assert.Throws<InvalidOperationException>(() => Messages(routed: true).CreateMessage(Home, ResponseOptions(request, Main)));

        Assert.Contains("outside the request's hierarchy directories", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(OtherPool, "*--response.md", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Home, "*--response.md", SearchOption.AllDirectories));
    }

    [Fact]
    public void ANoTeamRequestDoesNotGainAccessToAnOutsidePool()
    {
        var request = WriteMessage(OtherPool, null);

        var error = Assert.Throws<InvalidOperationException>(() => Messages(routed: true).CreateMessage(Home, ResponseOptions(request, Main)));

        Assert.Contains("outside the request's hierarchy directories", error.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(OtherPool, "*--response.md", SearchOption.AllDirectories));
    }

    [Fact]
    public void ARequestWithNoResolvedTeamHasANullTeamFileAndCanBeAnswered()
    {
        var messages = Messages(routed: true);
        var request = _sandbox.Mirrored(() => messages.CreateMessage(Home, new JsonObject
        {
            ["type"] = "request", ["id"] = Id, ["to"] = "implementor", ["from"] = "orchestrator", ["slug"] = "no-team",
        })).Member("path")!.Value.AsString();
        Assert.Contains("team_file: null\n", File.ReadAllText(request), StringComparison.Ordinal);
        var response = _sandbox.Mirrored(() => messages.CreateMessage(Home, ResponseOptions(request, Main)));

        Assert.True(response.Member("fields")!.Value.Member("team_file")!.Value.IsNull);
        Assert.Equal("no-team", messages.ResponsePool(request).Get("status")!.Value.AsString());
        Assert.True(File.Exists(response.Member("path")!.Value.AsString()));
    }

    private static JsonObject ResponseOptions(string request, string cwd) => new()
    {
        ["type"] = "response", ["id"] = Id, ["reqPath"] = request, ["from"] = "implementor", ["to"] = "orchestrator", ["cwd"] = cwd,
    };

    [Fact]
    public void ACustomHierarchyOverrideLimitsFamilyAndLookupButDoesNotAdmitAnUnrelatedRealPool()
    {
        var custom = Path.Combine(_sandbox.Root, "custom-pool");
        var team = Path.Combine(custom, "teams", "t.json");
        WriteTeam(team);
        var own = WriteMessage(custom, team);
        WriteMessage(Legacy, team, id: "20261018-120001-abcd");
        var planted = WriteMessage(OtherPool, team, id: "20261018-120002-abcd");
        var messages = Messages(custom);

        Assert.Equal([custom], messages.Family(team));
        Assert.Equal(custom, messages.Locate(Id, "request", [team], [custom]).Member("pool")!.Value.AsString());
        Assert.True(messages.Locate("20261018-120001-abcd", "request", [team], [custom]).IsNull);
        Assert.Equal("allowed", messages.ResponsePool(own).Get("status")!.Value.AsString());
        Assert.Equal("refused", messages.ResponsePool(planted).Get("status")!.Value.AsString());
    }
}
