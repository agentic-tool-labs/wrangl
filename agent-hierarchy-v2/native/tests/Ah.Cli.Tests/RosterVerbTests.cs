using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The roster identity and read verbs over the real libraries and a sandbox hierarchy dir: output and exit codes of the six
/// verbs, each refusal with its nearest accepted input, and what is written (and not written) on every path.
/// </summary>
[Collection("RosterServices")]
public sealed class RosterVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly List<Process> _children = [];

    public RosterVerbTests()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Hierarchy);
    }

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        foreach (var child in _children)
        {
            try
            {
                child.Kill();
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            child.Dispose();
        }

        _sandbox.Dispose();
    }

    // ---------------------------------------------------------------- the rig

    private sealed class Services(
        ServiceGraph graph, IFileReader files, IClock clock, IAhEnvironment environment, string cwd, IRosterCallerRegistry? callers,
        ITeardown? teardown, IRosterOwnerProof? proof = null, Func<string, JsonObject?>? goals = null) : IRosterCliServices
    {
        public JsonObject? TeamGoal(string cwd, string team) => goals?.Invoke(team);

        public IConfigCore Config => graph.Config;

        public IRosterCore Roster => graph.Roster;

        public HierarchyServices Hierarchy => graph.Hierarchy;

        public IAhEnvironment Environment => environment;

        public IClock Clock => clock;

        public IFileReader Files => files;

        public string CurrentDirectory => cwd;

        public int ParentPid => 4242;

        public ITeardown? Teardown => teardown;

        public IRosterLaunch? Launch => null;

        public IRosterOwnerProof? OwnerProof => proof ?? new SessionAncestorProof(System.Environment.ProcessId);

        public IRosterCallerRegistry? Callers => callers;
    }

    private sealed class RecordingCallers : IRosterCallerRegistry
    {
        public string? ProcessStartText(int pid) => "Sat Oct 17 13:44:38 2026";

        internal List<(string Cwd, string? Session, int Pid, string? Pane, string? Team, bool Default)> Calls { get; } = [];

        public IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit)
        {
            Calls.Add((cwd, sessionId, pid, paneId, teamArg, teamDefaultExplicit));
            return [teamArg is null ? "orchestrator@t" : $"member-{paneId}@{teamArg}"];
        }

        internal List<string> Owned { get; } = [];

        public IReadOnlyList<string> OwnedTeams(string cwd, string? sessionId, int pid) => Owned;
    }

    /// <summary>No daemon is up, so a status refresh reaches the in-process engine; every attempt to use a daemon is counted by its discover call.</summary>
    private readonly FakeDaemonClient _daemon = new();

    private bool _knowSelf;

    /// <summary>The scripted table shows the running CLI a parent that is neither it nor pid 1, so a pid it does not list reads as gone.</summary>
    private bool _seesAParent;

    private IRosterOwnerProof? _proof;

    /// <summary>The team goal the rig's services answer by team name (ah2-53); null answers none.</summary>
    private Func<string, JsonObject?>? _goals;

    private void ComposeServices(Dictionary<string, string?>? env, IRosterCallerRegistry? callers, ITeardown? teardown)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
        };
        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                variables[key] = value;
            }
        }

        var fake = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        var io = PlatformModule.CreateIo(null);
        var runner = _knowSelf ? FakeRunner.KnowingSelf() : new FakeRunner();
        if (_seesAParent)
        {
            var before = runner.Ps;
            runner.Ps = argv => before?.Invoke(argv)
                ?? (argv.Contains("ppid=") ? (argv.Contains("9999") ? "1\n" : "9999\n")
                    : argv.Contains("lstart=") && argv.Contains("9999") ? FakeRunner.SelfStart + "\n" : null);
        }

        var graph = ServiceGraph.Build(io, fake, runner, _daemon);
        var services = new Services(graph, io.Files, io.Clock, fake, Project, callers, teardown, _proof, _goals);
        CommandsModule.ComposeRoster(() => services);
    }

    private (int Code, string Stdout, string Stderr) Roster(
        string[] args, Dictionary<string, string?>? env = null, IRosterCallerRegistry? callers = null, ITeardown? teardown = null)
    {
        ComposeServices(env, callers, teardown);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var code = verb.Run(args, stdout, stderr);
        return (code, stdout.ToString().Replace("\r\n", "\n", StringComparison.Ordinal), stderr.ToString().Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string Self => Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    private int LivePidOfAnotherProcess()
    {
        var start = new ProcessStartInfo("sleep", "120") { UseShellExecute = false, RedirectStandardOutput = true };
        var child = Process.Start(start) ?? throw new InvalidOperationException("sleep did not start");
        _children.Add(child);
        return child.Id;
    }

    private void WriteTeam(string name, string json)
    {
        _sandbox.PlantTeam(Path.Combine(Hierarchy, "teams", name + ".json"), json);
    }

    /// <summary>The team record <paramref name="name"/> as the store holds it, compact; <c>null</c> when it holds none.</summary>
    private string ReadTeam(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var graph = ServiceGraph.Build(
            PlatformModule.CreateIo(null), new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["HOME"] = _sandbox.Home, ["AH_HOME"] = _sandbox.AhHome }), new FakeRunner(), _daemon);
        return JsonText.Stringify(graph.Roster.ReadTeamFileAt(Path.Combine(Hierarchy, "teams", name + ".json")), 0);
    }

    private void WritePeer(string json) => _sandbox.PlantPeers(Hierarchy, json);

    private string[] Tree() => Directory.EnumerateFiles(_sandbox.Root, "*", SearchOption.AllDirectories)
        .Select(p => Path.GetRelativePath(_sandbox.Root, p))
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static JsonValue Parse(string text) => JsonText.Parse(text);

    // ---------------------------------------------------------------- whoami

    [Fact]
    public void WhoamiOutsideAMembersPaneSaysSo()
    {
        var (code, stdout, stderr) = Roster(["whoami", "--team", "tm"]);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(
            "{\n  \"member\": null,\n  \"team\": null,\n  \"team_file\": null,\n  \"orchestrator\": null,\n  \"registered_pid\": null,\n" +
            "  \"last_observed_brief\": null,\n  \"reason\": \"no-pane-id\",\n  \"answered_by\": null\n}\n",
            stdout);
    }

    [Fact]
    public void WhoamiFindsTheMemberHoldingItsPane()
    {
        WriteTeam(
            "tm",
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + ",\"session_id\":\"orch\"},\"members\":[" +
            "{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%7\"}]}");

        var (code, stdout, _) = Roster(["whoami", "--team", "tm"], new() { ["HERDR_PANE_ID"] = "%7" });

        Assert.Equal(0, code);
        var o = Parse(stdout).AsObject();
        Assert.Equal("pane", o["answered_by"].AsString());
        Assert.Equal("tm", o["team"].AsString());
        Assert.Equal("tm-dev", o["member"].AsObject()["name"].AsString());
        Assert.True(o["orchestrator"].AsObject()["live"].AsBool());
        Assert.True(o["reason"].IsNull);
    }

    /// <summary>ah2-97 D2: the Orchestrator of a team its last dismiss left empty still gets that team from whoami.</summary>
    [Fact]
    public void WhoamiNamesTheTeamOfAnOrchestratorWhoseTeamHasNoMembersLeft()
    {
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + ",\"session_id\":\"orch\"},\"members\":[]}");

        var (code, stdout, stderr) = Roster(["whoami"], new() { ["WRANGL_TEAM_FILE"] = Path.Combine(Hierarchy, "teams", "tm.json") });

        Assert.True(code == 0, stderr);
        var o = Parse(stdout).AsObject();
        Assert.Equal("tm", o["team"].AsString());
        Assert.True(o["member"].IsNull);
    }

    [Fact]
    public void WhoamiRefusesAFlagItDoesNotTakeAndAcceptsTheOnesItDoes()
    {
        var (badCode, _, badErr) = Roster(["whoami", "--level", "repo"]);
        var (okCode, _, _) = Roster(["whoami", "--cwd", Project]);

        Assert.Equal(2, badCode);
        Assert.Equal("ah roster: whoami: unrecognized flag --level (use --team or --cwd)\n", badErr);
        Assert.Equal(0, okCode);
    }

    private void WriteAnchor(string id, string authorityLine)
    {
        var msgs = Path.Combine(Hierarchy, "msgs");
        Directory.CreateDirectory(msgs);
        _ = Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(msgs, $"{id}--orchestrator--pipeline-run-anchor--request.md"),
            $"---\nid: {id}\ntype: request\nto: orchestrator\nfrom: orchestrator\nteam: tm\nslug: pipeline-run-anchor\ncreated: 2026-10-11T12:00:00.000Z\n---\n\n## constraints\n   branch: ah/x\n{authorityLine}");
    }

    private JsonObject WhoamiJson(string pidStart = "")
    {
        WriteTeam(
            "tm",
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + ",\"session_id\":\"orch\"" + (pidStart.Length > 0 ? ",\"pid_start\":\"" + pidStart + "\"" : string.Empty) +
            "},\"members\":[{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%7\"}]}");
        var (code, stdout, _) = Roster(["whoami", "--json", "--team", "tm"], new() { ["HERDR_PANE_ID"] = "%7" });
        Assert.Equal(0, code);
        return Parse(stdout).AsObject();
    }

    [Theory]
    [InlineData("   authority: surrogate\n", "surrogate")]
    [InlineData("   authority: user\n", "user")]
    [InlineData("", "none")]
    [InlineData("   authority: everyone\n", "none")]
    public void WhoamiJsonReportsTheAnchorsGrantAndNoneForNoLineOrAnUnknownOne(string line, string mode)
    {
        WriteAnchor("20261011-100000-aaaa", line);
        RecordAnswer("orch", "Orchestrator acts for me (this run)");

        var authority = WhoamiJson()["authority"].AsObject();

        Assert.Equal(mode, authority["mode"].AsString());
        Assert.Equal("20261011-100000-aaaa", authority["run"].AsString());
    }

    private void RecordAnswer(string session, string answer, string at = "2026-10-11T11:00:00.000Z") =>
        File.AppendAllText(
            Path.Combine(Hierarchy, "asks.jsonl"),
            "{\"at\":\"" + at + "\",\"session_id\":\"" + session + "\",\"questions\":[{\"header\":\"Authority\",\"question\":\"Who?\"}],\"response\":{\"answers\":{\"Who?\":\"" + answer + "\"}}}\n");

    [Theory]
    [InlineData("")]
    [InlineData("other")]
    public void WhoamiJsonDowngradesASurrogateAnchorTheOrchestratorSessionNeverRecorded(string recordedBy)
    {
        WriteAnchor("20261011-100000-aaaa", "   authority: surrogate\n");
        if (recordedBy.Length > 0)
        {
            RecordAnswer(recordedBy, "Orchestrator acts for me (this run)");
        }

        Assert.Equal("user", WhoamiJson()["authority"].AsObject()["mode"].AsString());
    }

    private void CloseAnchor(string id, string closedAt, string team = "tm")
    {
        var msgs = Path.Combine(Hierarchy, "msgs");
        Directory.CreateDirectory(msgs);
        _ = Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(msgs, $"{id}--orchestrator--pipeline-run-anchor--request.md"),
            $"---\nid: {id}\ntype: request\nto: orchestrator\nfrom: orchestrator\nteam: {team}\nslug: pipeline-run-anchor\ncreated: 2026-10-11T09:00:00.000Z\n---\n\n## constraints\n   authority: surrogate\n");
        _ = Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(msgs, $"{id}--orchestrator--pipeline-run-anchor--response.md"),
            $"---\nid: {id}\ntype: response\nto: orchestrator\nfrom: orchestrator\nteam: {team}\nslug: pipeline-run-anchor\ncreated: {closedAt}\n---\n\n## [1] status\n- done\n");
    }

    [Theory]
    [InlineData("2026-10-11T10:00:00.000Z", "user")]
    [InlineData("2026-10-11T11:45:00.000Z", "surrogate")]
    public void WhoamiJsonCountsOnlyAnAnswerGivenAfterTheEarlierRunClosed(string answeredAt, string mode)
    {
        CloseAnchor("20261011-090000-c10d", "2026-10-11T10:30:00.000Z");
        WriteAnchor("20261011-100000-aaaa", "   authority: surrogate\n");
        RecordAnswer("orch", "Orchestrator acts for me (this run)", answeredAt);

        Assert.Equal(mode, WhoamiJson()["authority"].AsObject()["mode"].AsString());
    }

    [Fact]
    public void AnotherTeamsRunClosingAfterTheAnswerLeavesThisGrantStanding()
    {
        CloseAnchor("20261011-090000-0b0b", "2026-10-11T11:30:00.000Z", "other");
        WriteAnchor("20261011-100000-aaaa", "   authority: surrogate\n");
        RecordAnswer("orch", "Orchestrator acts for me (this run)", "2026-10-11T11:00:00.000Z");

        Assert.Equal("surrogate", WhoamiJson()["authority"].AsObject()["mode"].AsString());
    }

    [Fact]
    public void ARunThatClosedAfterThisAnchorWasMadeDoesNotBoundIt()
    {
        CloseAnchor("20261011-090000-c10d", "2026-10-11T12:30:00.000Z");
        WriteAnchor("20261011-100000-aaaa", "   authority: surrogate\n");
        RecordAnswer("orch", "Orchestrator acts for me (this run)", "2026-10-11T11:00:00.000Z");

        Assert.Equal("surrogate", WhoamiJson()["authority"].AsObject()["mode"].AsString());
    }

    [Fact]
    public void WhoamiJsonDowngradesASurrogateAnchorWhoseRecordedAnswerWasADifferentOne()
    {
        WriteAnchor("20261011-100000-aaaa", "   authority: surrogate\n");
        RecordAnswer("orch", "Members ask me through the orchestrator");

        Assert.Equal("user", WhoamiJson()["authority"].AsObject()["mode"].AsString());
    }

    [Fact]
    public void WhoamiJsonReportsNoneWithNoOpenAnchor()
    {
        var authority = WhoamiJson()["authority"].AsObject();

        Assert.Equal("none", authority["mode"].AsString());
        Assert.True(authority["run"].IsNull);
    }

    [Fact]
    public void WhoamiJsonReportsNoneWhenTwoAnchorsAreOpen()
    {
        WriteAnchor("20261011-100000-aaaa", "   authority: surrogate\n");
        WriteAnchor("20261011-110000-bbbb", "   authority: surrogate\n");
        RecordAnswer("orch", "Orchestrator acts for me (this run)");

        Assert.Equal("none", WhoamiJson()["authority"].AsObject()["mode"].AsString());
    }

    [Fact]
    public void WhoamiWithoutJsonCarriesNoAuthorityBlock()
    {
        WriteAnchor("20261011-100000-aaaa", "   authority: surrogate\n");
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%7\"}]}");

        var (_, stdout, _) = Roster(["whoami", "--team", "tm"], new() { ["HERDR_PANE_ID"] = "%7" });

        Assert.False(Parse(stdout).AsObject().ContainsKey("authority"));
    }

    [Fact]
    public void AnOrchestratorPidWhoseStartTimeDiffersIsNotLiveAndHasNoSendTo()
    {
        _knowSelf = true;

        var reused = WhoamiJson("1990-01-01T00:00:00.000Z")["orchestrator"].AsObject();

        Assert.False(reused["live"].AsBool());
        Assert.True(reused["send_to"].IsNull);
    }

    [Fact]
    public void AnOrchestratorPidWhoseStartTimeMatchesIsStillLive()
    {
        _knowSelf = true;

        var same = WhoamiJson(FakeRunner.SelfStart)["orchestrator"].AsObject();

        Assert.True(same["live"].AsBool());
    }

    [Fact]
    public void WhoamiNeverWrites()
    {
        var before = Tree();

        Roster(["whoami"]);

        Assert.Equal(before, Tree());
    }

    // ---------------------------------------------------------------- history

    [Fact]
    public void HistoryOfNothingIsAnEmptyList()
    {
        var (code, stdout, _) = Roster(["history"]);

        Assert.Equal(0, code);
        Assert.Equal("{\n  \"teams\": []\n}\n", stdout);
    }

    [Fact]
    public void HistoryListsAnEntryWithItsDistinctRolesAndMemberCount()
    {
        File.WriteAllText(
            Path.Combine(Hierarchy, "team-history.json"),
            "{\"version\":1,\"teams\":[{\"id\":\"h1\",\"label\":\"first\",\"alias\":\"tm\",\"last_used\":\"2026-01-01T00:00:00.000Z\"," +
            "\"created_at\":\"2026-01-01T00:00:00.000Z\",\"roster_level\":\"repo\",\"transport\":\"herdr\"," +
            "\"members\":[{\"role\":\"dev\"},{\"role\":\"dev\"},{\"role\":\"architect\"}]}]}");

        var (code, stdout, _) = Roster(["history"]);

        Assert.Equal(0, code);
        var entry = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject();
        Assert.Equal("h1", entry["id"].AsString());
        Assert.Equal(3, (int)entry["member_count"].AsNumber());
        Assert.Equal(["dev", "architect"], entry["roles"].AsArray().Select(r => r.AsString()).ToArray());
        Assert.False(entry["active"].AsBool());
    }

    // ---------------------------------------------------------------- status

    [Fact]
    public void StatusPrintsTheDocumentAndWritesNoStatusJson()
    {
        var (code, stdout, _) = Roster(["status"]);

        Assert.Equal(0, code);
        var doc = Parse(stdout).AsObject();
        Assert.Equal(1, (int)doc["schema"].AsNumber());
        Assert.False(File.Exists(Path.Combine(Hierarchy, "status.json")));
        Assert.False(Directory.Exists(Path.Combine(Hierarchy, ".state-status")));
    }

    [Fact]
    public void StatusPlainPrintsOneLine()
    {
        var (code, stdout, _) = Roster(["status", "--plain"]);

        Assert.Equal(0, code);
        Assert.EndsWith("\n", stdout, StringComparison.Ordinal);
        Assert.False(stdout.StartsWith('{'));
    }

    [Fact]
    public void StatusRefusesANowThatIsNotATimeAndAcceptsAnIsoOne()
    {
        var (badCode, _, badErr) = Roster(["status", "--now", "yesterday-ish"]);
        var (flagCode, _, flagErr) = Roster(["status", "--now"]);
        var (okCode, _, _) = Roster(["status", "--now", "2026-03-01T12:00:00.000Z"]);

        Assert.Equal(2, badCode);
        Assert.Equal("ah roster: status: --now takes an ISO-8601 time, got \"yesterday-ish\"\n", badErr);
        Assert.Equal(2, flagCode);
        Assert.Equal("ah roster: status: --now takes an ISO-8601 time, got true\n", flagErr);
        Assert.Equal(0, okCode);
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A status instant ten minutes ahead of the clock the writer stamps with, so a stored document is never from the future of it.</summary>
    private static string StatusNow() => Iso(DateTime.UtcNow.AddMinutes(10));

    [Fact]
    public void StatusMarksBlockedOnJobOnThePrintedDocument()
    {
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[{\"name\":\"tm-dev\",\"role\":\"dev\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"%9\"}]}");
        _sandbox.PlantActivity(Hierarchy, "pane-tm-dev", "{\"activity\":\"working\",\"at\":\"" + Iso(DateTime.UtcNow.AddMinutes(-1)) + "\"}");
        Directory.CreateDirectory(Path.Combine(Hierarchy, "jobs", "tm"));
        File.WriteAllText(
            Path.Combine(Hierarchy, "jobs", "tm", "j1.json"),
            "{\"id\":\"j1\",\"team\":\"tm\",\"owner\":\"tm-dev\",\"session_id\":\"s\",\"kind\":\"bg\",\"label\":\"dotnet test\",\"eta\":\"small\",\"sent_at\":\"" + Iso(DateTime.UtcNow.AddHours(-2)) + "\",\"background\":true}");
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(_sandbox.AhHome, Hierarchy, force: true);
        var now = StatusNow();

        var (_, stdout, _) = Roster(["status", "--now", now]);

        var member = Parse(stdout).AsObject()["teams"].AsArray().Single().AsObject()["members"].AsArray().Single().AsObject();
        Assert.Equal("blocked-on-job", member["state"].AsString());
        Assert.Equal("j1", member["blocked_job"].AsObject()["id"].AsString());
    }

    [Fact]
    public void StatusCarriesTheGoalOfATeamWithOneAndPlainPrintsItCutToOneLine()
    {
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[]}");
        WriteTeam("tb", "{\"team_id\":\"t-tb\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[]}");
        var text = "ship\nthe row " + new string('g', 100);
        _goals = team => team == "tm" ? new JsonObject { ["text"] = text, ["done_when"] = "all green", ["team"] = "tm" } : null;
        var now = StatusNow();

        var (code, stdout, stderr) = Roster(["status", "--now", now]);
        Assert.True(code == 0, stderr);
        var teams = Parse(stdout).AsObject()["teams"].AsArray().Select(static t => t.AsObject()).ToDictionary(static t => t["team"].AsString(), StringComparer.Ordinal);
        Assert.Equal(text, teams["tm"]["goal"].AsObject()["text"].AsString());
        Assert.Equal("all green", teams["tm"]["goal"].AsObject()["done_when"].AsString());
        // negative: a team with no goal gets no member
        Assert.False(teams["tb"].TryGetValue("goal", out _));

        var (_, plain, _) = Roster(["status", "--plain", "--now", now]);
        var goalLines = plain.Split('\n').Where(static l => l.StartsWith("goal (", StringComparison.Ordinal)).ToList();
        // one line, the newline a space, cut to 100 code points with the ellipsis, no done-when; nothing for tb
        Assert.Equal(["goal (tm): " + ("ship the row " + new string('g', 100))[..99] + "…"], goalLines);
    }

    [Fact]
    public void StatusWithNoGoalPrintsNoGoalLineAndNoGoalMember()
    {
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[]}");
        var now = StatusNow();

        Assert.DoesNotContain("\"goal\"", Roster(["status", "--now", now]).Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("goal (", Roster(["status", "--plain", "--now", now]).Stdout, StringComparison.Ordinal);
    }

    private void WriteStatusRig(string activity, string jobOwner)
    {
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[{\"name\":\"tm-dev\",\"role\":\"dev\",\"kind\":\"codex\",\"route\":\"pane\",\"transport_id\":\"%9\"}]}");
        _sandbox.PlantActivity(Hierarchy, "pane-tm-dev", "{\"activity\":\"" + activity + "\",\"at\":\"2026-03-01T11:59:00.000Z\"}");
        var jobs = Path.Combine(Hierarchy, "jobs", "tm");
        Directory.CreateDirectory(jobs);
        File.WriteAllText(
            Path.Combine(jobs, "j1.json"),
            "{\"id\":\"j1\",\"team\":\"tm\",\"owner\":\"" + jobOwner + "\",\"session_id\":\"s\",\"kind\":\"bg\",\"label\":\"dotnet test\",\"eta\":\"small\",\"sent_at\":\"2026-03-01T12:00:00.000Z\",\"background\":true}");
        _ = Ah.StateDb.StateDbModule.ImportHierDirFiles(_sandbox.AhHome, Hierarchy, force: true);
    }

    private JsonObject StatusMember(string now)
    {
        var (code, stdout, stderr) = Roster(["status", "--now", now]);

        Assert.True(code == 0, stderr);
        return Parse(stdout).AsObject()["teams"].AsArray().Single().AsObject()["members"].AsArray().Single().AsObject();
    }

    [Fact]
    public void StatusShowsALiveMemberThatOwnsAnOverdueJobAsBlockedOnJobWithTheJob()
    {
        WriteStatusRig("working", "tm-dev");

        var member = StatusMember("2026-03-01T13:00:00.000Z");

        Assert.Equal("blocked-on-job", member["state"].AsString());
        var job = member["blocked_job"].AsObject();
        Assert.Equal("j1", job["id"].AsString());
        Assert.Equal("dotnet test", job["label"].AsString());
        Assert.Equal("small", job["eta"].AsString());
    }

    [Theory]
    [InlineData("working", "tm-dev", "2026-03-01T12:00:10.000Z")]
    [InlineData("working", "tm-other", "2026-03-01T13:00:00.000Z")]
    [InlineData("unknown", "tm-dev", "2026-03-01T13:00:00.000Z")]
    public void StatusDoesNotShowBlockedOnJobForAJobNotYetDueAnotherMembersJobOrAMemberThatIsNotLive(string activity, string jobOwner, string now)
    {
        WriteStatusRig(activity, jobOwner);

        var member = StatusMember(now);

        Assert.False(member.TryGetValue("state", out _));
        Assert.False(member.TryGetValue("blocked_job", out _));
    }

    // ---------------------------------------------------------------- adopt

    private const string DeadPidTeam =
        "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":999999,\"pid_start\":\"Mon Jan  1 00:00:00 2001\",\"session_id\":\"orch-tm\"},\"members\":[{\"name\":\"tm-dev\",\"role\":\"dev\"}]}";

    private sealed class RegisteredProof(params OwnerRegistration[] registrations) : IRosterOwnerProof
    {
        private readonly SessionAncestorProof _inner = new(System.Environment.ProcessId);

        public int NearestPid { get; init; } = System.Environment.ProcessId;

        public Ah.Mesh.OwnerProofResult Check(string dir, string? team) => _inner.Check(dir, team);

        public OwnerActor Actor() => _inner.Actor();

        public Ah.Mesh.NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs) =>
            new(Ah.Mesh.NearestKind.Orchestrator, null, null, NearestPid);

        public IReadOnlyList<OwnerRegistration> RegistrationsOf(int pid) => registrations;
    }

    private void WriteLane()
    {
        WriteTeam(
            "mo",
            "{\"team_id\":\"t-mo\",\"orchestrator\":{\"pid\":1,\"session_id\":\"o\"},\"members\":[{\"name\":\"mo-so-ws1\",\"role\":\"sub-orchestrator\"}],"
            + "\"streams\":{\"ws1\":{\"state\":\"open\",\"branch\":\"lane/ws1\",\"sub_orchestrator\":\"mo-so-ws1\"}}}");
        WriteTeam(
            "mo-ws1",
            "{\"team_id\":\"t-child\",\"orchestrator\":{\"pid\":999999,\"pid_start\":\"Mon Jan  1 00:00:00 2001\",\"session_id\":\"old-sess\"},\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},"
            + "\"members\":[{\"name\":\"mo-ws1-impl\",\"role\":\"implementor\"}],\"streams\":{\"ws1\":{\"state\":\"open\"}}}");
    }

    [Fact]
    public void AdoptOfALaneChildByItsOwnSubOrchestratorTakesItOverUnderTheLaneRule()
    {
        _seesAParent = true;
        WriteLane();
        _proof = new RegisteredProof(new OwnerRegistration("mo-so-ws1@mo", "Sat Oct 17 13:44:38 2026", "sess-so"));

        var (code, stdout, stderr) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "mo-ws1"]);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal("old-sess", Parse(stdout).AsObject()["adopted_from"].AsString());
        var written = Parse(ReadTeam("mo-ws1")).AsObject();
        Assert.Equal(Environment.ProcessId, (int)written["orchestrator"].AsObject()["pid"].AsNumber());
        Assert.Equal("old-sess", written["orchestrator"].AsObject()["adopted_from"].AsObject()["session_id"].AsString());
        Assert.Single(written["members"].AsArray());
    }

    [Theory]
    [InlineData("orchestrator@mo")]
    [InlineData("mo-other@mo")]
    [InlineData("mo-so-ws1@elsewhere")]
    public void NegativeAdoptOfALaneChildByAnyoneButItsSubOrchestratorIsRefusedAndWritesNothing(string address)
    {
        WriteLane();
        var before = ReadTeam("mo-ws1");
        _proof = new RegisteredProof(new OwnerRegistration(address, "Sat Oct 17 13:44:38 2026", "sess-x"));

        var (code, _, stderr) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "mo-ws1"]);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-child-adopt-by-so", stderr, StringComparison.Ordinal);
        Assert.Equal(before, ReadTeam("mo-ws1"));
    }

    [Fact]
    public void NegativeAdoptOfALaneChildWhoseStreamIsDoneIsRefusedAndWritesNothing()
    {
        WriteLane();
        WriteTeam("mo", ReadTeam("mo").Replace("\"state\":\"open\"", "\"state\":\"done\"", StringComparison.Ordinal));
        var before = ReadTeam("mo-ws1");
        _proof = new RegisteredProof(new OwnerRegistration("mo-so-ws1@mo", "Sat Oct 17 13:44:38 2026", "sess-so"));

        var (code, _, _) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "mo-ws1"]);

        Assert.NotEqual(0, code);
        Assert.Equal(before, ReadTeam("mo-ws1"));
    }

    [Fact]
    public void NegativeAdoptOfALaneChildWithAPidThatIsNotTheCallersOwnSessionIsRefusedAndWritesNothing()
    {
        WriteLane();
        var before = ReadTeam("mo-ws1");
        _proof = new RegisteredProof(new OwnerRegistration("mo-so-ws1@mo", "Sat Oct 17 13:44:38 2026", "sess-so")) { NearestPid = Environment.ProcessId + 1 };

        var (code, _, stderr) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "mo-ws1"]);

        Assert.NotEqual(0, code);
        Assert.Contains("names pid", stderr, StringComparison.Ordinal);
        Assert.Equal(before, ReadTeam("mo-ws1"));
    }

    [Fact]
    public void AdoptRestampsTheOwnerPidOfADeadOwnersTeamAndNothingElse()
    {
        _seesAParent = true;
        WriteTeam("tm", DeadPidTeam);

        var (code, stdout, stderr) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "tm"]);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr);
        var o = Parse(stdout).AsObject();
        Assert.True(o["adopted"].AsBool());
        Assert.Equal("t-tm", o["team_id"].AsString());
        Assert.Equal(Environment.ProcessId, (int)o["orchestrator"].AsObject()["pid"].AsNumber());
        var written = Parse(ReadTeam("tm")).AsObject();
        Assert.True(written["orchestrator"].AsObject()["session_id"].IsNull);
        Assert.Single(written["members"].AsArray());
    }

    [Fact]
    public void AdoptNeedsALivePidFromTheFlagNotTheEnvironment()
    {
        WriteTeam("tm", DeadPidTeam);

        var (missing, _, missingErr) = Roster(["adopt", "--team", "tm"], new() { ["CLAUDE_PID"] = Self });
        var (dead, _, deadErr) = Roster(["adopt", "--orchestrator-pid", "999999", "--team", "tm"]);

        Assert.Equal(2, missing);
        Assert.Equal("ah roster: adopt needs --orchestrator-pid <pid>: the pid of the live Claude session taking the team over, which must be this command's own session\n", missingErr);
        Assert.Equal(2, dead);
        Assert.StartsWith($"ah roster adopt: --orchestrator-pid names pid 999999, but this command runs under pid {Self}; nothing was changed.\n", deadErr, StringComparison.Ordinal);
        Assert.Contains("999999", ReadTeam("tm"), StringComparison.Ordinal);
    }

    [Fact]
    public void AdoptRefusesToTakeATeamWhoseOwnerCannotBeProvenGoneUnlessForced()
    {
        var other = LivePidOfAnotherProcess();
        WriteTeam("tm", DeadPidTeam.Replace("999999,\"pid_start\":\"Mon Jan  1 00:00:00 2001\"", other.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
        var before = ReadTeam("tm");

        var (code, _, stderr) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "tm"]);
        var unchanged = ReadTeam("tm");
        var (forcedCode, _, forcedErr) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "tm", "--force"]);

        Assert.Equal(2, code);
        Assert.Contains("cannot be proven gone", stderr, StringComparison.Ordinal);
        Assert.Contains("adopt --force", stderr, StringComparison.Ordinal);
        Assert.Equal(before, unchanged);
        Assert.True(forcedCode == 0, forcedErr);
        Assert.Equal(Environment.ProcessId, (int)Parse(ReadTeam("tm")).AsObject()["orchestrator"].AsObject()["pid"].AsNumber());
    }

    [Fact]
    public void AdoptRefusesAFlagItDoesNotTakeAndAMissingTeam()
    {
        var (flagCode, _, flagErr) = Roster(["adopt", "--orchestrator-pid", Self, "--plan"]);
        var (noneCode, _, noneErr) = Roster(["adopt", "--orchestrator-pid", Self, "--team", "nosuch"]);

        Assert.Equal(2, flagCode);
        Assert.Equal("ah roster: adopt: unrecognized flag --plan (use --orchestrator-pid, --team, --cwd, or --force)\n", flagErr);
        Assert.Equal(2, noneCode);
        Assert.Equal("ah roster: adopt: no team file at this scope to adopt\n", noneErr);
    }

    // ---------------------------------------------------------------- checkin

    private void SessionRow(string extra = "") => WritePeer(
        "{\"ts\":\"" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) +
        "\",\"type\":\"peer\",\"status\":\"up\",\"role\":\"dev\",\"session_id\":\"sess-1\",\"pid\":" + Self + ",\"cwd\":\"" + Project + "\"" + extra + "}");

    [Fact]
    public void CheckinNeedsAPidAndAnExistingRecord()
    {
        var (noPid, _, noPidErr) = Roster(["checkin"]);
        var (noRow, _, noRowErr) = Roster(["checkin", "--orchestrator-pid", Self]);

        Assert.Equal(2, noPid);
        Assert.Equal(
            "ah roster: checkin: cannot resolve this session's pid — CLAUDE_PID is unset; pass --orchestrator-pid <pid>\n", noPidErr);
        Assert.Equal(2, noRow);
        Assert.Equal(
            $"ah roster: checkin: no existing roster record for pid {Self} — SessionStart must run before checkin; " +
            $"tried pid in {Hierarchy}, session unset, pane unset\n", noRowErr);
    }

    [Fact]
    public void CheckinCreatesTheRecordForAnOrchestratorThatOwnsTheTeam()
    {
        var callers = new RecordingCallers();
        callers.Owned.Add("tm");

        var (code, stdout, stderr) = Roster(
            ["checkin", "--orchestrator-pid", Self], new Dictionary<string, string?> { ["CLAUDE_CODE_SESSION_ID"] = "sess-o" }, callers);

        Assert.True(code == 0, stderr);
        Assert.True(Parse(stdout).AsObject()["checked_in"].AsBool());
        Assert.Equal("owner", Parse(stdout).AsObject()["adopted_by"].AsString());
        var row = Parse(_sandbox.PeerLines(Hierarchy)[^1]).AsObject();
        Assert.Equal("orchestrator", row["role"].AsString());
        Assert.Equal("up", row["status"].AsString());
        Assert.Equal("sess-o", row["session_id"].AsString());
        var call = Assert.Single(callers.Calls);
        Assert.Equal("sess-o", call.Session);
        Assert.Equal(Environment.ProcessId, call.Pid);
    }

    [Fact]
    public void CheckinOfANonOwnerWithNoRecordStillFailsWhenCallersKnowNoTeam()
    {
        var callers = new RecordingCallers();

        var (code, _, stderr) = Roster(["checkin", "--orchestrator-pid", Self], callers: callers);

        Assert.Equal(2, code);
        Assert.Equal(
            $"ah roster: checkin: no existing roster record for pid {Self} — SessionStart must run before checkin; " +
            $"tried pid in {Hierarchy}, session unset, pane unset\n", stderr);
        Assert.Empty(callers.Calls);
    }

    [Fact]
    public void CheckinRegistersASessionWithNoRecordAsTheOrchestratorOfItsImplicitTeam()
    {
        var callers = new RecordingCallers();

        var (code, stdout, stderr) = Roster(
            ["checkin", "--orchestrator-pid", Self], new Dictionary<string, string?> { ["CLAUDE_CODE_SESSION_ID"] = "sess-late" }, callers);

        Assert.True(code == 0, stderr);
        Assert.Equal("late", Parse(stdout).AsObject()["adopted_by"].AsString());
        Assert.Equal("orchestrator", Parse(_sandbox.PeerLines(Hierarchy)[^1]).AsObject()["role"].AsString());
        var team = Parse(ReadTeam("proj")).AsObject();
        Assert.True(team["implicit"].AsBool());
        Assert.Equal("sess-late", team["orchestrator"].AsObject()["session_id"].AsString());
        Assert.Equal("sess-late", Assert.Single(callers.Calls).Session);
    }

    [Fact]
    public void CheckinForAnOwnerOfAnotherTeamThanTheNamedOneFails()
    {
        var callers = new RecordingCallers();
        callers.Owned.Add("tm");

        var (code, _, stderr) = Roster(["checkin", "--orchestrator-pid", Self, "--team", "other"], callers: callers);

        Assert.Equal(2, code);
        Assert.Equal(
            $"ah roster: checkin: no existing roster record for pid {Self} — SessionStart must run before checkin; " +
            $"tried pid in {Hierarchy}, session unset, pane unset\n", stderr);
        Assert.Empty(callers.Calls);
    }

    [Fact]
    public void CheckinRefusesAFlagItDoesNotTakeAndATeamThatDoesNotExist()
    {
        SessionRow();

        var (flagCode, _, flagErr) = Roster(["checkin", "--orchestrator-pid", Self, "--plan"]);
        var (teamCode, _, teamErr) = Roster(["checkin", "--orchestrator-pid", Self, "--team", "nosuch"]);

        Assert.Equal(2, flagCode);
        Assert.Equal("ah roster: checkin: unrecognized flag --plan (use --team, --cwd, or --orchestrator-pid)\n", flagErr);
        Assert.Equal(2, teamCode);
        Assert.Equal("ah roster: checkin: no such team \"nosuch\"\n", teamErr);
    }

    [Fact]
    public void CheckinAppendsARowAndRegistersTheCaller()
    {
        SessionRow();
        var callers = new RecordingCallers();
        var peersBefore = _sandbox.PeerLines(Hierarchy).Length;

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self], callers: callers);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, stderr);
        var o = Parse(stdout).AsObject();
        Assert.True(o["checked_in"].AsBool());
        Assert.False(o["misplaced"].AsBool());
        Assert.Equal("pid", o["adopted_by"].AsString());
        Assert.True(o["expected_root"].IsNull);
        Assert.False(o.ContainsKey("team"));
        var lines = _sandbox.PeerLines(Hierarchy);
        Assert.Equal(peersBefore + 1, lines.Length);
        var row = Parse(lines[^1]).AsObject();
        Assert.Equal("up", row["status"].AsString());
        Assert.Equal("dev", row["role"].AsString());
        Assert.Equal(4242, (int)row["ppid"].AsNumber());
        Assert.True(row["pid_start"].IsText);
        var call = Assert.Single(callers.Calls);
        Assert.Equal("sess-1", call.Session);
        Assert.Equal(Environment.ProcessId, call.Pid);
    }

    private static string PeerJson(int pid, string session, string role = "dev") =>
        "{\"ts\":\"" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) +
        "\",\"type\":\"peer\",\"status\":\"up\",\"role\":\"" + role + "\",\"session_id\":\"" + session + "\",\"pid\":" +
        pid.ToString(CultureInfo.InvariantCulture) + "}";

    private static int DeadPid()
    {
        var child = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })
            ?? throw new InvalidOperationException("sleep did not start");
        var pid = child.Id;
        child.Kill();
        child.WaitForExit();
        child.Dispose();
        return pid;
    }

    private (string Tree, string TreeDir, string MainDir) LinkedWorktree()
    {
        var main = Path.Combine(_sandbox.Root, "main");
        var tree = Path.Combine(_sandbox.Root, "wt");
        var gitdir = Path.Combine(main, ".git", "worktrees", "wt");
        Directory.CreateDirectory(gitdir);
        Directory.CreateDirectory(tree);
        File.WriteAllText(Path.Combine(gitdir, "gitdir"), Path.Combine(tree, ".git") + "\n");
        File.WriteAllText(Path.Combine(tree, ".git"), "gitdir: " + gitdir + "\n");
        var mainDir = Path.Combine(main, ".claude", "ah2");
        var treeDir = Path.Combine(tree, ".claude", "ah2");
        Directory.CreateDirectory(mainDir);
        Directory.CreateDirectory(treeDir);
        return (tree, treeDir, mainDir);
    }

    private static readonly Dictionary<string, string?> NoOverride = new() { ["WRANGL_HIERARCHY_DIR"] = null };

    private static Dictionary<string, string?> With(params (string Key, string? Value)[] vars)
    {
        var env = new Dictionary<string, string?>(NoOverride);
        foreach (var (key, value) in vars)
        {
            env[key] = value;
        }

        return env;
    }

    private const string PaneTeam =
        "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":1},\"members\":[" +
        "{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%9\"}]}";

    [Fact]
    public void CheckinAdoptsTheMainCheckoutRowOfASessionMovedIntoAWorkTree()
    {
        var (tree, treeDir, mainDir) = LinkedWorktree();
        _sandbox.PlantPeers(mainDir,PeerJson(Environment.ProcessId, "sess-m"));

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self, "--cwd", tree], With(), new RecordingCallers());

        Assert.True(code == 0, stderr);
        Assert.Equal("pid-main", Parse(stdout).AsObject()["adopted_by"].AsString());
        var row = Parse(_sandbox.PeerLines(treeDir)[^1]).AsObject();
        Assert.Equal(Environment.ProcessId, (int)row["pid"].AsNumber());
        Assert.Equal("sess-m", row["session_id"].AsString());
        Assert.Single(_sandbox.PeerLines(mainDir));
    }

    [Fact]
    public void CheckinDoesNotAdoptAMainCheckoutRowOfAnotherPid()
    {
        var (tree, treeDir, mainDir) = LinkedWorktree();
        _sandbox.PlantPeers(mainDir,PeerJson(LivePidOfAnotherProcess(), "sess-m"));

        var (code, _, stderr) = Roster(["checkin", "--orchestrator-pid", Self, "--cwd", tree], With(), new RecordingCallers());

        Assert.Equal(2, code);
        Assert.Contains($"tried pid in {treeDir}, {mainDir}, session unset, pane unset", stderr, StringComparison.Ordinal);
        Assert.Empty(_sandbox.PeerLines(treeDir));
    }

    [Fact]
    public void CheckinAdoptsTheRowOfASameSessionWhoseProcessIsGone()
    {
        WritePeer(PeerJson(DeadPid(), "sess-r"));
        var callers = new RecordingCallers();

        var (code, stdout, stderr) = Roster(
            ["checkin", "--orchestrator-pid", Self], new() { ["CLAUDE_CODE_SESSION_ID"] = "sess-r" }, callers);

        Assert.True(code == 0, stderr);
        Assert.Equal("session", Parse(stdout).AsObject()["adopted_by"].AsString());
        var row = Parse(_sandbox.PeerLines(Hierarchy)[^1]).AsObject();
        Assert.Equal(Environment.ProcessId, (int)row["pid"].AsNumber());
        Assert.Equal("sess-r", row["session_id"].AsString());
        Assert.Equal("sess-r", Assert.Single(callers.Calls).Session);
    }

    [Fact]
    public void CheckinDoesNotAdoptTheRowOfALiveForkOfTheSameSession()
    {
        WritePeer(PeerJson(LivePidOfAnotherProcess(), "sess-r"));

        var (code, stdout, stderr) = Roster(
            ["checkin", "--orchestrator-pid", Self], new() { ["CLAUDE_CODE_SESSION_ID"] = "sess-r" }, new RecordingCallers());

        // ah2-32: not refused; the fork registers as its own late Orchestrator and the live row stays the other process's.
        Assert.True(code == 0, stderr);
        Assert.Equal("late", Parse(stdout).AsObject()["adopted_by"].AsString());
    }

    [Fact]
    public void CheckinDoesNotAdoptADeadRowOfAnotherSession()
    {
        WritePeer(PeerJson(DeadPid(), "sess-other"));

        var (code, stdout, stderr) = Roster(
            ["checkin", "--orchestrator-pid", Self], new() { ["CLAUDE_CODE_SESSION_ID"] = "sess-r" }, new RecordingCallers());

        Assert.True(code == 0, stderr);
        Assert.Equal("late", Parse(stdout).AsObject()["adopted_by"].AsString());
    }

    [Fact]
    public void CheckinAdoptsTheMemberHoldingTheHerdrPane()
    {
        WriteTeam("tm", PaneTeam);
        var callers = new RecordingCallers();

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9" }, callers);

        Assert.True(code == 0, stderr);
        var o = Parse(stdout).AsObject();
        Assert.Equal("pane", o["adopted_by"].AsString());
        Assert.Equal("tm", o["team"].AsString());
        var row = Parse(_sandbox.PeerLines(Hierarchy)[^1]).AsObject();
        Assert.Equal("dev", row["role"].AsString());
        Assert.Equal(Environment.ProcessId, (int)row["pid"].AsNumber());
        Assert.Equal("%9", row["pane_id"].AsString());
        Assert.Single(callers.Calls);
    }

    [Fact]
    public void CheckinOfASessionItsRowDoesNotNameSaysSoAndNamesRecover()
    {
        WriteTeam("tm", PaneTeam.Replace("\"transport_id\":\"%9\"", "\"transport_id\":\"%9\",\"pid\":99999999", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9" }, new RecordingCallers());

        Assert.Equal(1, code);
        var o = Parse(stdout).AsObject();
        Assert.True(o["row_held_elsewhere"].AsBool());
        Assert.Contains("ah roster recover --team tm", o["next"].AsString(), StringComparison.Ordinal);
        Assert.Contains("ah roster recover --team tm", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeCheckinOfTheSessionItsRowNamesIsPlainSuccess()
    {
        WriteTeam("tm", PaneTeam.Replace("\"transport_id\":\"%9\"", "\"transport_id\":\"%9\",\"pid\":" + Self, StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9" }, new RecordingCallers());

        Assert.True(code == 0, stderr);
        Assert.False(Parse(stdout).AsObject().ContainsKey("row_held_elsewhere"));
    }

    [Fact]
    public void CheckinOfThisPidWithAnotherStartIsHeldElsewhereAndGivesMailsRemedy()
    {
        WriteTeam("tm", PaneTeam.Replace("\"transport_id\":\"%9\"", "\"transport_id\":\"%9\",\"pid\":" + Self + ",\"pid_start\":\"Fri Oct 16 01:00:00 2026\"", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9" }, new RecordingCallers());

        Assert.Equal(1, code);
        var o = Parse(stdout).AsObject();
        Assert.True(o["row_held_elsewhere"].AsBool());
        Assert.Equal(Ah.Mesh.MeshModule.HeldRowRemedy(new JsonObject { ["pid"] = double.Parse(Self, CultureInfo.InvariantCulture) }, running: false, "tm", "tm-dev"), o["next"].AsString());
        Assert.Contains(o["next"].AsString(), stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeCheckinOfThisPidWithTheSameStartIsPlainSuccess()
    {
        WriteTeam("tm", PaneTeam.Replace("\"transport_id\":\"%9\"", "\"transport_id\":\"%9\",\"pid\":" + Self + ",\"pid_start\":\"Sat Oct 17 13:44:38 2026\"", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9" }, new RecordingCallers());

        Assert.True(code == 0, stderr);
        Assert.False(Parse(stdout).AsObject().ContainsKey("row_held_elsewhere"));
    }

    [Fact]
    public void CheckinOfARowHeldByAProcessStillRunningSaysCloseItOrRecover()
    {
        // Pid 1 is always running, is not this session, and no start is recorded to contradict it.
        WriteTeam("tm", PaneTeam.Replace("\"transport_id\":\"%9\"", "\"transport_id\":\"%9\",\"pid\":1", StringComparison.Ordinal));

        var (code, stdout, _) = Roster(["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9" }, new RecordingCallers());

        Assert.Equal(1, code);
        Assert.Contains("is alive: close it", Parse(stdout).AsObject()["next"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void CheckinByPaneAcceptsAMemberNameThatAgreesAndRefusesOneThatDoesNot()
    {
        WriteTeam("tm", PaneTeam);

        var (okCode, okOut, _) = Roster(
            ["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9", ["AH_MEMBER_NAME"] = "tm-dev" },
            new RecordingCallers());
        var (badCode, _, badErr) = Roster(
            ["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%9", ["AH_MEMBER_NAME"] = "tm-other" },
            new RecordingCallers());

        Assert.Equal(0, okCode);
        Assert.Equal("pane", Parse(okOut).AsObject()["adopted_by"].AsString());
        Assert.Equal(2, badCode);
        Assert.Contains("pane %9", badErr, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckinByPaneNeedsAPaneThatHoldsAMember()
    {
        WriteTeam("tm", PaneTeam);

        var (unsetCode, _, unsetErr) = Roster(["checkin", "--orchestrator-pid", Self], callers: new RecordingCallers());
        var (otherCode, _, _) = Roster(
            ["checkin", "--orchestrator-pid", Self], new() { ["HERDR_PANE_ID"] = "%1" }, new RecordingCallers());

        Assert.Equal(2, unsetCode);
        Assert.EndsWith("session unset, pane unset\n", unsetErr, StringComparison.Ordinal);
        Assert.Equal(2, otherCode);
    }

    [Fact]
    public void CheckinTruncatesTheSessionIdInTheTriedList()
    {
        var (code, _, stderr) = Roster(
            ["checkin", "--orchestrator-pid", Self], new() { ["CLAUDE_CODE_SESSION_ID"] = "0123456789abcdef", ["AH_MEMBER_NAME"] = "tm-dev" }, new RecordingCallers());

        Assert.Equal(2, code);
        Assert.EndsWith("session 01234567, pane unset\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckinFromASubdirectoryOfItsExpectedRootIsPlaced()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        var sub = Path.Combine(Project, "agent-hierarchy-v2");
        Directory.CreateDirectory(sub);
        WriteTeam(
            "tm",
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[" +
            "{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%9\",\"expected_root\":\"" + Project + "\"}]}");
        SessionRow();

        var (code, stdout, _) = Roster(
            ["checkin", "--orchestrator-pid", Self, "--team", "tm", "--cwd", sub], new() { ["HERDR_PANE_ID"] = "%9" }, new RecordingCallers());

        Assert.Equal(0, code);
        Assert.False(Parse(stdout).AsObject()["misplaced"].AsBool());
    }

    [Fact]
    public void CheckinOutsideItsExpectedRootIsMisplacedAndExitsOne()
    {
        WriteTeam(
            "tm",
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + "},\"members\":[" +
            "{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%9\",\"expected_root\":\"/somewhere/else\"}]}");
        SessionRow();

        var (code, stdout, _) = Roster(
            ["checkin", "--orchestrator-pid", Self, "--team", "tm"], new() { ["HERDR_PANE_ID"] = "%9" }, new RecordingCallers());

        Assert.Equal(1, code);
        var o = Parse(stdout).AsObject();
        Assert.True(o["misplaced"].AsBool());
        Assert.Equal("/somewhere/else", o["expected_root"].AsString());
        Assert.Equal("tm", o["team"].AsString());
    }

    [Fact]
    public void CheckinWithoutTheMailboxRegistrationWritesNothingAndSaysWhatIsMissing()
    {
        SessionRow();
        var before = string.Join("\n", _sandbox.PeerLines(Hierarchy));

        var (code, stdout, stderr) = Roster(["checkin", "--orchestrator-pid", Self]);

        Assert.Equal(70, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("RegisterCaller", stderr, StringComparison.Ordinal);
        Assert.Equal(before, string.Join("\n", _sandbox.PeerLines(Hierarchy)));
    }

    // ---------------------------------------------------------------- teams

    private sealed class FakeTeardown : ITeardown
    {
        internal List<(string? Team, bool NoTeam)> Scopes { get; } = [];

        public HerdrArm HerdrArmMatches(string dir, TeamScope scope, LaunchContext context) => throw new NotSupportedException();

        public JsonArray LiveRegistrySlots(string dir, TeamScope scope, LaunchContext context, IReadOnlyList<JsonValue>? known = null) =>
            throw new NotSupportedException();

        public JsonArray UntrackedLive(string dir, TeamScope scope, IReadOnlySet<string> tracked, LaunchContext context)
        {
            Scopes.Add((scope.Team, scope.NoTeam));
            return [];
        }

        public JsonObject SourcesField(string dir, TeamScope scope, JsonValue team, LaunchContext context) =>
            new() { ["team"] = new JsonObject(), ["peers"] = new JsonObject(), ["herdr"] = new JsonObject { ["ok"] = false } };

        public JsonArray PeerFallbackMembers(string dir, TeamScope scope, LaunchContext context, IReadOnlyList<JsonValue>? known = null) =>
            throw new NotSupportedException();

        public JsonArray PeerExtras(string dir, IReadOnlyList<JsonValue> members, TeamScope scope, LaunchContext context) =>
            throw new NotSupportedException();

        public PeerTarget ResolvePeerTarget(IReadOnlyList<JsonValue> fallback, string name, string? teamTransport, LaunchContext context) =>
            throw new NotSupportedException();

        public CloseVerification CloseAndVerify(IReadOnlyList<JsonObject> closable, string teamTransport, List<string> warnings, LaunchContext context, JsonValue? teamRecord = null, string? dir = null) =>
            throw new NotSupportedException();

        public ReconcileOutcome ReconcileAfterClose(string dir, JsonValue snapshot, IReadOnlyList<JsonObject> results, string path, LaunchContext context) =>
            throw new NotSupportedException();

        public string CloseToken(JsonValue teamId, IReadOnlyList<JsonValue> closable) => throw new NotSupportedException();

        public IReadOnlyList<JsonValue> ClosableMembers(IReadOnlyList<JsonValue> members) => throw new NotSupportedException();

        internal Func<string, (bool Live, bool Indeterminate)> Liveness { get; set; } = static _ => (false, false);

        public JsonObject MemberLiveness(string dir, JsonValue member, string? team = null)
        {
            var (live, indeterminate) = Liveness(member.Member("name")!.Value.AsString());
            return new() { ["live"] = live, ["indeterminate"] = indeterminate, ["liveness"] = live || indeterminate ? "live" : "proven_dead" };
        }
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheScratchSweepTakesAnIdleTmpUnderTheAhHomeOnlyWhenADeadMembersRecordNamesIt()
    {
        string Idle(string key)
        {
            var dir = Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "t", key)).FullName;
            File.WriteAllText(Path.Combine(dir, "f"), "x");
            var old = DateTime.UtcNow.AddDays(-10);
            File.SetLastWriteTimeUtc(Path.Combine(dir, "f"), old);
            Directory.SetLastWriteTimeUtc(dir, old);
            return dir;
        }

        var dead = Idle("aaaa0001");
        var live = Idle("aaaa0002");
        var unnamed = Idle("aaaa0003");
        WriteTeam("tm", $$"""{"team_id":"t-tm","members":[{"name":"tm-dead","role":"dev","tmp":"{{dead}}"},{"name":"tm-live","role":"dev","tmp":"{{live}}"}]}""");
        var io = PlatformModule.CreateIo(null);
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home });
        var graph = ServiceGraph.Build(io, env, new FakeRunner(), _daemon);
        var teardown = new FakeTeardown { Liveness = name => (name == "tm-live", false) };

        var report = LaunchModule.SweepScratch(
            PlatformModule.CreateScratch(io.Files, _sandbox.AhHome), teardown, graph.Roster, io.Files, (_, _) => JsonValue.Null, Hierarchy, Hierarchy, DateTimeOffset.UtcNow, commit: true);

        Assert.Equal([dead], report.Entries.Select(e => e.Path));
        Assert.False(Directory.Exists(dead));
        Assert.True(Directory.Exists(live));
        Assert.True(Directory.Exists(unnamed));
    }

    [Fact]
    public void TeamsListsEveryTeamFileWithItsLiveness()
    {
        _seesAParent = true;
        WriteTeam("tm", DeadPidTeam);
        var teardown = new FakeTeardown();

        var (code, stdout, _) = Roster(["teams"], teardown: teardown);

        Assert.Equal(0, code);
        var o = Parse(stdout).AsObject();
        var row = o["teams"].AsArray()[0].AsObject();
        Assert.Equal("tm", row["name"].AsString());
        Assert.Equal("t-tm", row["team_id"].AsString());
        Assert.Equal(0, (int)row["members"].AsNumber());
        Assert.Equal(1, (int)row["member_rows"].AsNumber());
        Assert.False(row["pid_alive"].AsBool());
        Assert.True(row["orphaned"].AsBool());
        Assert.Equal("orphaned", row["life"].AsString());
        Assert.Empty(row["misplaced_members"].AsArray());
        Assert.Equal(0, row["misplaced_unattributed"].AsNumber());
        Assert.Empty(o["untracked_live"].AsArray());
        Assert.Contains(("tm", false), teardown.Scopes);
        Assert.Contains((null, true), teardown.Scopes);
    }

    private const string FiveMemberTeam =
        "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":999999,\"pid_start\":\"Mon Jan  1 00:00:00 2001\",\"session_id\":\"orch-tm\"},\"members\":["
        + "{\"name\":\"a\",\"role\":\"dev\"},{\"name\":\"b\",\"role\":\"dev\"},{\"name\":\"c\",\"role\":\"dev\"},"
        + "{\"name\":\"d\",\"role\":\"dev\"},{\"name\":\"e\",\"role\":\"dev\"}]}";

    [Fact]
    public void TeamsCountsOnlyTheLiveMembersAsAgentsAndKeepsTheRowCount()
    {
        WriteTeam("tm", FiveMemberTeam);
        var teardown = new FakeTeardown { Liveness = name => (name is "a" or "b" or "c", false) };

        var (code, stdout, _) = Roster(["teams"], teardown: teardown);

        Assert.Equal(0, code);
        var row = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject();
        Assert.Equal(3, (int)row["members"].AsNumber());
        Assert.Equal(5, (int)row["member_rows"].AsNumber());
    }

    [Fact]
    public void TeamsCountsAMemberItCannotRuleOutButNotOneThatIsDead()
    {
        WriteTeam("tm", FiveMemberTeam);
        var teardown = new FakeTeardown { Liveness = name => (name == "a", name == "b") };

        var (_, stdout, _) = Roster(["teams"], teardown: teardown);

        var row = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject();
        Assert.Equal(2, (int)row["members"].AsNumber());
    }

    private int LivePidRunningAs(string name)
    {
        var start = new ProcessStartInfo("bash", ["-c", $"exec -a {name} sleep 120"]) { UseShellExecute = false, RedirectStandardOutput = true };
        var child = Process.Start(start) ?? throw new InvalidOperationException("bash did not start");
        _children.Add(child);
        Thread.Sleep(300);
        return child.Id;
    }

    private int MisplacedCountForUpRowOf(int pid, string? pidStart = null)
    {
        WriteTeam("tm", DeadPidTeam);
        var start = pidStart is null ? string.Empty : $"\"pid_start\":\"{pidStart}\",";
        WritePeer($"{{\"type\":\"peer\",\"status\":\"up\",\"misplaced\":true,\"team\":\"tm\",\"role\":\"implementor\",\"session_id\":\"s-x\",\"pid\":{pid},{start}\"ts\":\"2026-10-17T10:00:00.000Z\"}}");

        var (code, stdout, _) = Roster(["teams"], teardown: new FakeTeardown());

        Assert.Equal(0, code);
        var row = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject();
        return row["misplaced_members"].AsArray().Count + (int)row["misplaced_unattributed"].AsNumber();
    }

    [Fact]
    public void TeamsListsEveryMemberRowsWorktreeEvenWithNoProcess()
    {
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":999999,\"session_id\":\"orch-tm\"},\"members\":[{\"name\":\"tm-a\",\"worktree\":\"/w/a\"},{\"name\":\"tm-b\"},{\"name\":\"tm-c\",\"worktree\":\"\"}]}");

        var (code, stdout, _) = Roster(["teams"], teardown: new FakeTeardown());

        Assert.Equal(0, code);
        var trees = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject()["member_worktrees"].AsArray();
        Assert.Equal(["/w/a"], trees.Select(t => t.AsString()));
    }

    [Fact]
    public void TeamsWorktreesListsNameLifeAndTreesWithoutTheLaunchLayer()
    {
        WriteTeam("tm", "{\"team_id\":\"t-tm\",\"members\":[{\"name\":\"tm-a\",\"worktree\":\"/w/a\"}]}");
        WriteTeam("done", "{\"team_id\":\"t-d\",\"closed\":{\"at\":\"2026-11-09T00:00:00Z\",\"by\":\"u\"},\"members\":[]}");

        var (code, stdout, stderr) = Roster(["teams", "--worktrees"]);

        Assert.True(code == 0, stderr);
        var rows = Parse(stdout).AsObject()["teams"].AsArray().Select(r => r.AsObject()).ToDictionary(r => r["name"].AsString());
        Assert.Equal("open", rows["tm"]["life"].AsString());
        Assert.Equal(["/w/a"], rows["tm"]["member_worktrees"].AsArray().Select(t => t.AsString()));
        Assert.Equal("closed", rows["done"]["life"].AsString());
    }

    [Fact]
    public void TeamsWorktreesInALinkedTreeListsItsOwnRecordsEvenWhenTheMainCheckoutHasATeamOfTheSameName()
    {
        var (tree, treeDir, mainDir) = LinkedWorktree();
        _sandbox.PlantTeam(Path.Combine(mainDir, "teams", "tm.json"), "{\"team_id\":\"t-main\",\"members\":[{\"name\":\"tm-a\",\"worktree\":\"/w/main\"}]}");
        _sandbox.PlantTeam(Path.Combine(mainDir, "teams", "only.json"), "{\"team_id\":\"t-only\",\"members\":[]}");
        _sandbox.PlantTeam(Path.Combine(treeDir, "teams", "tm.json"), "{\"team_id\":\"t-lane\",\"members\":[{\"name\":\"tm-a\",\"worktree\":\"/w/lane\"}]}");

        var (code, stdout, stderr) = Roster(["teams", "--worktrees", "--cwd", tree], With());

        Assert.True(code == 0, stderr);
        var row = Assert.Single(Parse(stdout).AsObject()["teams"].AsArray()).AsObject();
        Assert.Equal("tm", row["name"].AsString());
        Assert.Equal(["/w/lane"], row["member_worktrees"].AsArray().Select(t => t.AsString()));
    }

    [Fact]
    public void TeamsWorktreesInALinkedTreeReportsAHomeStoredTeamQuitFromThereByItsCheckpoint()
    {
        var (tree, treeDir, mainDir) = LinkedWorktree();
        _sandbox.PlantTeam(Path.Combine(mainDir, "teams", "tm.json"), "{\"team_id\":\"t-main\",\"members\":[]}");
        _sandbox.PlantTeam(Path.Combine(mainDir, "teams", "other.json"), "{\"team_id\":\"t-o\",\"members\":[]}");
        Directory.CreateDirectory(Path.Combine(treeDir, "checkpoints", "tm"));
        File.WriteAllText(
            Path.Combine(treeDir, "checkpoints", "tm", "20261109T000000000Z.json"),
            "{\"team\":\"tm\",\"orchestrator\":{\"worktree\":\"/w/orch\"},\"members\":[]}");
        Ah.TestKit.TestImport.Planted(_sandbox.AhHome, [Path.Combine(treeDir, "checkpoints", "tm", "20261109T000000000Z.json")]);

        var (code, stdout, stderr) = Roster(["teams", "--worktrees", "--cwd", tree], With());

        Assert.True(code == 0, stderr);
        var row = Assert.Single(Parse(stdout).AsObject()["teams"].AsArray()).AsObject();
        Assert.Equal("tm", row["name"].AsString());
        Assert.Equal("open", row["life"].AsString());
        Assert.Equal(["/w/orch"], row["member_worktrees"].AsArray().Select(t => t.AsString()));
    }

    [Fact]
    public void TeamsListsTheLatestCheckpointsOrchestratorAndMemberTreesForAHeldTeam()
    {
        WriteTeam("tm", DeadPidTeam);
        Directory.CreateDirectory(Path.Combine(Hierarchy, "checkpoints", "tm"));
        File.WriteAllText(
            Path.Combine(Hierarchy, "checkpoints", "tm", "20261109T000000000Z.json"),
            "{\"team\":\"tm\",\"orchestrator\":{\"worktree\":\"/w/orch\"},\"members\":[{\"name\":\"tm-dev\",\"worktree\":\"/w/dev\"}]}");
        Ah.TestKit.TestImport.Planted(_sandbox.AhHome, [Path.Combine(Hierarchy, "checkpoints", "tm", "20261109T000000000Z.json")]);

        var (code, stdout, _) = Roster(["teams"], teardown: new FakeTeardown());

        Assert.Equal(0, code);
        var trees = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject()["member_worktrees"].AsArray();
        Assert.Equal(["/w/orch", "/w/dev"], trees.Select(t => t.AsString()));
    }

    [Fact]
    public void AMisplacedUpRowWhosePidIsALiveProcessThatIsNotAHarnessIsStillCountedBecauseNothingProvesItGone()
    {
        Assert.Equal(1, MisplacedCountForUpRowOf(LivePidRunningAs("ScreenSharingSubscriber")));
    }

    [Fact]
    public void AMisplacedUpRowWhosePidIsAnotherUsersProcessIsCountedWhileTheObserverSeesNoOtherProcessAndNotOnceItDoes()
    {
        Assert.False(OperatingSystem.IsWindows());
        const string recorded = "Mon Jan  1 00:00:00 2001";
        Assert.Equal(1, MisplacedCountForUpRowOf(1, recorded));

        _seesAParent = true;
        Assert.Equal(0, MisplacedCountForUpRowOf(1, recorded));
    }

    [Fact]
    public void NegativeAMisplacedUpRowWithNoRecordedStartIsStillCountedWhileTheObserverSeesAParentBecauseItsPidProvesNothing()
    {
        Assert.False(OperatingSystem.IsWindows());
        _seesAParent = true;
        Assert.Equal(1, MisplacedCountForUpRowOf(1));
    }

    [Fact]
    public void AMisplacedUpRowWhosePidIsARunningHarnessIsCounted()
    {
        Assert.Equal(1, MisplacedCountForUpRowOf(LivePidRunningAs("claude")));
    }

    private void WriteActivity(string session, string json)
    {
        _sandbox.PlantActivity(Hierarchy, session, json);
    }

    private const string IdleSinceTeam =
        "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":999999,\"pid_start\":\"Mon Jan  1 00:00:00 2001\",\"session_id\":\"orch-tm\"},\"members\":["
        + "{\"name\":\"idle-one\",\"role\":\"dev\",\"session_id\":\"s-idle\"},"
        + "{\"name\":\"busy-one\",\"role\":\"dev\",\"session_id\":\"s-busy\"},"
        + "{\"name\":\"no-record\",\"role\":\"dev\",\"session_id\":\"s-none\"},"
        + "{\"name\":\"no-session\",\"role\":\"dev\"},"
        + "{\"name\":\"old-record\",\"role\":\"dev\",\"session_id\":\"s-old\"}]}";

    [Fact]
    public void TeamsGivesEachMemberItsIdleSinceOnlyWhileIdle()
    {
        WriteTeam("tm", IdleSinceTeam);
        WriteActivity("s-idle", "{\"activity\":\"idle\",\"at\":\"2026-01-02T05:00:00.000Z\",\"state_since\":\"2026-01-02T03:04:05.000Z\"}");
        WriteActivity("s-busy", "{\"activity\":\"working\",\"at\":\"2026-01-02T05:00:00.000Z\",\"state_since\":\"2026-01-02T01:00:00.000Z\"}");
        WriteActivity("s-old", "{\"activity\":\"idle\",\"at\":\"2026-01-02T05:00:00.000Z\"}");

        var (code, stdout, _) = Roster(["teams"], teardown: new FakeTeardown());

        Assert.Equal(0, code);
        var since = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject()["idle_since"].AsObject();
        Assert.Equal("2026-01-02T03:04:05.000Z", since["idle-one"].AsString());
        Assert.True(since["busy-one"].IsNull);
        Assert.True(since["no-record"].IsNull);
        Assert.True(since["no-session"].IsNull);
        Assert.True(since["old-record"].IsNull);
        Assert.Equal(5, since.Keys.Count);
    }

    [Fact]
    public void TeamsGivesNoIdleSinceToAMemberOnOpenBackgroundWorkUntilThatWorkIsTooOld()
    {
        WriteTeam("tm", IdleSinceTeam);
        var recent = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        WriteActivity("s-idle", $"{{\"activity\":\"idle\",\"state_since\":\"{recent}\",\"bg_open\":1}}");
        WriteActivity("s-old", "{\"activity\":\"idle\",\"state_since\":\"2020-01-02T03:04:05.000Z\",\"bg_open\":1}");

        var (_, stdout, _) = Roster(["teams"], teardown: new FakeTeardown());

        var since = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject()["idle_since"].AsObject();
        Assert.True(since["idle-one"].IsNull);
        Assert.Equal("2020-01-02T03:04:05.000Z", since["old-record"].AsString());
    }

    [Fact]
    public void TeamsReadsIdleSinceOfARowWithNoSessionIdFromItsAttributedSessionOnly()
    {
        WriteTeam("tm", IdleSinceTeam);
        WritePeer("{\"type\":\"peer\",\"status\":\"up\",\"name\":\"no-session\",\"role\":\"dev\",\"session_id\":\"s-attr\",\"pid\":1,\"ts\":\"2026-01-02T05:00:00.000Z\"}");
        WriteActivity("s-attr", "{\"activity\":\"idle\",\"at\":\"2026-01-02T05:00:00.000Z\",\"state_since\":\"2026-01-02T03:04:05.000Z\"}");

        var (code, stdout, _) = Roster(["teams"], teardown: new FakeTeardown());

        Assert.Equal(0, code);
        var since = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject()["idle_since"].AsObject();
        Assert.Equal("2026-01-02T03:04:05.000Z", since["no-session"].AsString());
        Assert.True(since["no-record"].IsNull);
    }

    [Fact]
    public void TeamsAddsOnlyIdleSinceAndParentToItsRows()
    {
        WriteTeam("tm", DeadPidTeam);

        var (_, stdout, _) = Roster(["teams"], teardown: new FakeTeardown());

        var row = Parse(stdout).AsObject()["teams"].AsArray()[0].AsObject();
        var keys = row.Keys.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["gate", "idle_since", "life", "member_rows", "member_worktrees", "members", "misplaced_members", "misplaced_unattributed", "name", "orchestrator_pid", "orphaned", "own", "parent", "pid_alive", "stale_stream_worktrees", "team_id", "untracked_live"],
            keys);
        Assert.True(row["parent"].IsNull);
        Assert.Equal(["tm-dev"], row["idle_since"].AsObject().Keys.ToArray());
        Assert.True(row["idle_since"].AsObject()["tm-dev"].IsNull);
    }

    [Fact]
    public void TeamsWithoutTheLaunchLayerComposedSaysSoAndExitsSeventy()
    {
        WriteTeam("tm", DeadPidTeam);

        var (code, stdout, stderr) = Roster(["teams"]);

        Assert.Equal(70, code);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("Ah.Launch", stderr, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- the shared layer

    [Fact]
    public void AWordThatIsNoRosterVerbGetsNodesUsageError()
    {
        var (code, stdout, stderr) = Roster(["bogus"]);

        Assert.Equal(2, code);
        Assert.Equal(string.Empty, stdout);
        Assert.StartsWith("ah roster: usage: ah roster show|init|add|edit|remove|roster|role|pack|create|", stderr, StringComparison.Ordinal);
        Assert.EndsWith("(unknown command \"bogus\")\n", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpAndABareRosterPrintTheUsageAndAVerbsOwnBlockIsJustThatBlock()
    {
        var (bare, bareOut, _) = Roster([]);
        var (help, helpOut, _) = Roster(["--help"]);
        var (verb, verbOut, _) = Roster(["whoami", "--help"]);

        Assert.Equal(0, bare);
        Assert.Equal(0, help);
        Assert.Equal(bareOut, helpOut);
        Assert.Contains("ah roster history", bareOut, StringComparison.Ordinal);
        Assert.Equal(0, verb);
        Assert.Contains("ah roster whoami", verbOut, StringComparison.Ordinal);
        Assert.DoesNotContain("ah roster history", verbOut, StringComparison.Ordinal);
    }

    [Fact]
    public void LedgerHelpIsItsOwnBlockAndTheBlockAboveItDoesNotRunIntoIt()
    {
        var (ledger, ledgerOut, _) = Roster(["ledger", "--help"]);
        var (timing, timingOut, _) = Roster(["timing", "--help"]);

        Assert.Equal(0, ledger);
        Assert.StartsWith("  ah ledger ship <sha>", ledgerOut, StringComparison.Ordinal);
        Assert.Contains("It marks no item done.", ledgerOut, StringComparison.Ordinal);
        Assert.DoesNotContain("ah roster", ledgerOut, StringComparison.Ordinal);
        Assert.Equal(0, timing);
        Assert.StartsWith("  ah roster timing", timingOut, StringComparison.Ordinal);
        Assert.DoesNotContain("ah ledger", timingOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamFlagIsValidatedAndTheDefaultTeamHasItsOwnWord()
    {
        var (bad, _, badErr) = Roster(["whoami", "--team", "Bad Name"]);
        var (explicitDefault, _, _) = Roster(["whoami", "--team", "@default"]);

        Assert.Equal(2, bad);
        Assert.StartsWith("ah roster: --team: ", badErr, StringComparison.Ordinal);
        Assert.Equal(0, explicitDefault);
    }

    [Fact]
    public void ATemplateVerbRefusesATeamFlagBeforeItDoesAnything()
    {
        var (code, _, stderr) = Roster(["show", "--team", "tm"]);
        var (negative, _, _) = Roster(["whoami", "--team", "tm"]);

        Assert.Equal(2, code);
        Assert.Equal("ah roster: show: `--team` names a live team; to edit a named roster use `--roster <r>`\n", stderr);
        Assert.Equal(0, negative);
    }

    [Fact]
    public void ARosterFlagWithNoValueIsRefused()
    {
        var (code, _, stderr) = Roster(["whoami", "--roster"]);

        Assert.Equal(2, code);
        Assert.Equal("ah roster: --roster needs a value: --roster <name>\n", stderr);
    }

    [Fact]
    public void WithNoServicesComposedEveryRosterVerbIsNotImplementedNatively()
    {
        CommandsModule.ComposeRoster(() => null!);
        var verb = CommandsModule.Registry.Verbs.Single(v => v.Name == "roster:whoami");
        var stdout = new StringWriter(CultureInfo.InvariantCulture);
        var stderr = new StringWriter(CultureInfo.InvariantCulture);

        var code = verb.Run(["whoami"], stdout, stderr);

        Assert.Equal(70, code);
        Assert.Equal(string.Empty, stdout.ToString());
        Assert.Equal("ah: roster is not implemented natively", stderr.ToString().Trim());
    }

    [Fact]
    public void ARosterVerbRunsThroughTheAhNamespaceOfTheExecutable()
    {
        using var harness = new Harness("ah");
        ComposeServices(null, null, null);

        var (code, stdout, _) = harness.Run([CommandsModule.Registry], "roster", "history");

        Assert.Equal(0, code);
        Assert.Equal("{\n  \"teams\": []\n}\n", stdout.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- whoami: work_tree

    private string WhoamiMemberWithDocument(string members)
    {
        WriteTeam(
            "tm",
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + ",\"session_id\":\"orch\"},\"members\":[" +
            "{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%7\"}]}");
        TestGrants.Plant(_sandbox.AhHome, _sandbox.Home, Hierarchy, "tm", "{\"version\":1,\"team\":\"tm\",\"mode\":\"enforce\",\"audit_hash\":null,\"members\":" + members + "}");
        return Path.Combine(Hierarchy, "grants", "tm.json");
    }

    private JsonObject WhoamiAsDev() =>
        Parse(Roster(["whoami", "--team", "tm"], new() { ["HERDR_PANE_ID"] = "%7" }).Stdout).AsObject();

    [Fact]
    public void WhoamiReportsTheMembersWorkTreeExtrasAndTmpFromTheGrantsDocument()
    {
        var tree = Path.Combine(_sandbox.Root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        var tmp = Path.Combine(_sandbox.Root, "tmp-dir");
        Directory.CreateDirectory(tmp);
        var alias = Path.Combine(_sandbox.Root, "alias");
        File.CreateSymbolicLink(alias, tmp);
        WhoamiMemberWithDocument(
            $$$$"""{"tm-dev":{"work_root":"{{{{tree}}}}","stream":"s1","role":"dev","extra":[{"path":"/x/y","note":"why","for":"req-1","outside":true,"at":"2026-10-15T00:00:00Z"}],"tmp":{"root":"{{{{tmp}}}}","alias":"{{{{alias}}}}","kind":"claude"}}}""");

        var work = WhoamiAsDev()["work_tree"].AsObject();

        Assert.Equal(tree, work["root"].AsString());
        Assert.Equal("s1", work["stream"].AsString());
        Assert.Equal("enforce", work["mode"].AsString());
        Assert.False(work["moved_or_missing"].AsBool());
        var extra = Assert.Single(work["extras"].AsArray()).AsObject();
        Assert.Equal(("/x/y", "why", "req-1", true), (extra["path"].AsString(), extra["note"].AsString(), extra["for"].AsString(), extra["outside"].AsBool()));
        Assert.Equal(tmp, work["tmp"].AsObject()["root"].AsString());
        Assert.Equal(alias, work["tmp"].AsObject()["alias"].AsString());
        Assert.True(work["tmp"].AsObject()["alias_ok"].AsBool());
    }

    [Fact]
    public void WhoamiSaysAliasNotOkWhenItWasRepointedAndMovedWhenTheRootLostItsGitEntry()
    {
        var tree = Path.Combine(_sandbox.Root, "bare-dir");
        Directory.CreateDirectory(tree);
        var tmp = Path.Combine(_sandbox.Root, "tmp-dir");
        var elsewhere = Path.Combine(_sandbox.Root, "elsewhere");
        Directory.CreateDirectory(tmp);
        Directory.CreateDirectory(elsewhere);
        var alias = Path.Combine(_sandbox.Root, "alias");
        File.CreateSymbolicLink(alias, elsewhere);
        WhoamiMemberWithDocument(
            $$$$"""{"tm-dev":{"work_root":"{{{{tree}}}}","stream":null,"role":"dev","extra":[],"tmp":{"root":"{{{{tmp}}}}","alias":"{{{{alias}}}}","kind":"claude"}}}""");

        var work = WhoamiAsDev()["work_tree"].AsObject();

        Assert.True(work["moved_or_missing"].AsBool());
        Assert.False(work["tmp"].AsObject()["alias_ok"].AsBool());
        Assert.True(work["stream"].IsNull);
    }

    [Theory]
    [InlineData("\"stream\":\"s2\",", "s2")]
    [InlineData("", "s1")]
    public void WhoamiShowsTheStreamTheTeamFileHoldsAfterAMoveElseTheOneRecordedAtAssignment(string teamRowStream, string expected)
    {
        var tree = Path.Combine(_sandbox.Root, "tree");
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        WhoamiMemberWithDocument($$$$"""{"tm-dev":{"work_root":"{{{{tree}}}}","stream":"s1","role":"dev","extra":[],"tmp":null}}""");
        WriteTeam(
            "tm",
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + ",\"session_id\":\"orch\"},\"members\":[" +
            "{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%7\"," + teamRowStream + "\"x\":0}]}");

        Assert.Equal(expected, WhoamiAsDev()["work_tree"].AsObject()["stream"].AsString());
    }

    [Fact]
    public void WhoamiHasNoWorkTreeForATeamWithoutADocumentOrAMemberWithoutARecord()
    {
        WriteTeam(
            "tm",
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + ",\"session_id\":\"orch\"},\"members\":[" +
            "{\"name\":\"tm-dev\",\"role\":\"dev\",\"route\":\"pane\",\"transport_id\":\"%7\"}]}");

        Assert.True(WhoamiAsDev()["work_tree"].IsNull);

        WhoamiMemberWithDocument("{}");

        Assert.True(WhoamiAsDev()["work_tree"].IsNull);
    }
}
