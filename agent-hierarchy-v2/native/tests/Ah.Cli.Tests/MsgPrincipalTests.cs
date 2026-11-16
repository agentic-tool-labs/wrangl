using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// <c>ah msg</c> acts as the session its own process tree proves: a claimed session or pid only selects among the proven ones,
/// a write with several and no hint is ambiguous, and <c>inbox --address</c>, <c>new --from</c> and <c>route --session</c> are
/// limited to what that session holds.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgPrincipalTests : IDisposable
{
    private static readonly string Self = System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture);

    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly FakeRunner _runner = FakeRunner.KnowingSelf();
    private readonly string _teamFile;
    private readonly CliContext _context;
    private readonly ServiceGraph _graph;

    public MsgPrincipalTests()
    {
        var hierarchy = Path.Combine(_sandbox.Root, "hier");
        _teamFile = Path.Combine(hierarchy, "teams", "tm.json");
        _sandbox.PlantTeam(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Self + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},"
            + "\"members\":[{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p1\"}]}\n");
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, _runner, new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private void AsMember() => FakeRunner.RegisterSelf(_graph.Mailbox, "s-impl", _teamFile, "tm-implementor@tm");

    private void AsOrchestrator() => FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, "orchestrator@tm");

    /// <summary>ah2-32: this test process is the Claude harness and the user granted it admin rights.</summary>
    private void AsAdmin()
    {
        _runner.Ps = argv => argv.Contains("command=") && argv[^1] == Self ? "claude\n" : FakeRunner.SelfOnly(argv);
        _graph.HomeDocs.Write("home.admin-grant", "{\"pid\":" + Self + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"}");
    }

    [Fact]
    public void TheAdminStaysTheUserAndAlsoActsAsItsOwnSessionForNextAndAck()
    {
        AsAdmin();
        AsOrchestrator();
        _graph.Hierarchy.Peers.AppendDispatchRecord("o", "20260101-000000-t1aa", "implementor");

        var principal = _graph.Mailbox.PrincipalOf(System.Environment.ProcessId);
        var (next, nextErr) = Run("next", "--team", "tm");
        var (ack, ackErr) = Run("ack", "20260101-000000-t1aa");
        var (asUser, asUserErr) = Run("send", "tm-implementor@tm", "--text", "hi", "--from", "user"); // negative: a send is still the user's

        Assert.True(asUser == 0, asUserErr);
        Assert.Equal(PrincipalKind.User, principal.Kind);
        Assert.Equal("o", Assert.Single(principal.Sessions).SessionId);
        Assert.Contains("orchestrator@tm", principal.Sessions[0].Addresses);
        Assert.True(next == 0, nextErr);
        Assert.True(ack == 0, ackErr);
        Assert.True(_graph.Hierarchy.Peers.ReportAcked("o", "20260101-000000-t1aa"));
    }

    [Fact]
    public void TheAdminWithTwoSessionsAtItsProcessIsNeverRefusedForNoHintAndActsAsTheNewest()
    {
        AsAdmin();
        FakeRunner.RegisterSelf(_graph.Mailbox, "a-before-clear", _teamFile, "orchestrator@tm");
        Thread.Sleep(20); // the registrations' `at` must differ
        AsOrchestrator();
        _graph.Hierarchy.Peers.AppendDispatchRecord("o", "20260101-000000-t1aa", "implementor");

        var principal = _graph.Mailbox.PrincipalOf(System.Environment.ProcessId);
        var daemonChoice = MeshModule.ChooseSession(principal, null, write: true); // what every daemon route asks
        var (send, sendErr) = Run("send", "tm-implementor@tm", "--text", "hi");
        var (ack, ackErr) = Run("ack", "20260101-000000-t1aa");
        var (_, releaseErr) = Run("release", "e1");
        var (_, declineErr) = Run("decline", "e1", "--reason", "superseded");

        Assert.Equal(["o", "a-before-clear"], principal.Sessions.Select(s => s.SessionId)); // newest first, not by name
        Assert.Null(daemonChoice.Refusal);
        Assert.Equal("o", daemonChoice.Session?.SessionId);
        Assert.True(send == 0, sendErr);
        Assert.True(ack == 0, ackErr);
        Assert.True(_graph.Hierarchy.Peers.ReportAcked("o", "20260101-000000-t1aa"));
        Assert.False(_graph.Hierarchy.Peers.ReportAcked("a-before-clear", "20260101-000000-t1aa"));
        foreach (var err in new[] { releaseErr, declineErr })
        {
            Assert.DoesNotContain("ambiguous_session", err, StringComparison.Ordinal);
            Assert.DoesNotContain("cannot tell which address", err, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NegativeAGrantWhoseStartIsAnotherProcessesAttachesNothing()
    {
        AsAdmin();
        _graph.HomeDocs.Write("home.admin-grant", "{\"pid\":" + Self + ",\"pid_start\":\"2020-01-01T00:00:00.000Z\",\"session_id\":\"o\"}");
        AsOrchestrator();

        var principal = _graph.Mailbox.PrincipalOf(System.Environment.ProcessId);

        Assert.Equal(PrincipalKind.Session, principal.Kind); // a plain session, not the admin
    }

    [Fact]
    public void NegativeTheAdminCannotTakeAnotherSessionsIdAndWithNoRegistrationHasNoSession()
    {
        AsAdmin();
        var (bare, bareErr) = Run("next");
        var bareSessions = _graph.Mailbox.PrincipalOf(System.Environment.ProcessId).Sessions;
        AsOrchestrator();

        var (other, otherErr) = Run("ack", "e1", "--session", "s-other");

        Assert.Empty(bareSessions);
        Assert.NotEqual(0, bare);
        Assert.Contains("unknown_address", bareErr, StringComparison.Ordinal);
        Assert.Equal(2, other);
        Assert.Contains("not_your_session", otherErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("note", true)]
    [InlineData("report", false)]
    public void ANoteRefusedForHavingNoTextSaysWhereALongerAddendumGoesAndNegativeAReportsRefusalDoesNot(string kind, bool hinted)
    {
        AsOrchestrator();

        var (code, err) = Run("send", "implementor@tm", "--kind", kind, "--text", "", "--session", "o");

        Assert.NotEqual(0, code);
        Assert.True(hinted == err.Contains("longer text goes in a file", StringComparison.Ordinal), err);
    }

    [Theory]
    [InlineData("note", "note needs --text or --ref <file>")]
    [InlineData("brief", "give exactly one of --ref, --text, --event")] // negative: a brief keeps the exactly-one rule
    public void ANoteWithNeitherTextNorRefGetsTheNoteRefusalAndItsHint(string kind, string refusal)
    {
        AsOrchestrator();

        var (code, err) = Run("send", "implementor@tm", "--kind", kind, "--session", "o");

        Assert.NotEqual(0, code);
        Assert.Contains(refusal, err, StringComparison.Ordinal);
        Assert.True(kind == "note" == err.Contains("longer text goes in a file", StringComparison.Ordinal), err);
    }

    private (int Exit, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = _sandbox.Mirrored(() => MsgVerb.Run(_context, [.. args, "--cwd", _sandbox.Root], stdout, stderr));
        _lastOut = stdout.ToString();
        return (exit, stderr.ToString());
    }

    private string _lastOut = string.Empty;

    [Fact]
    public void AMemberNamingTheOrchestratorsSessionIsRefusedAndItsOwnSessionSends()
    {
        AsMember();

        var (refused, why) = Run("send", "orchestrator@tm", "--text", "hi", "--session", "o");
        var (own, ownErr) = Run("send", "orchestrator@tm", "--text", "hi", "--session", "s-impl");

        Assert.Equal(2, refused);
        Assert.Contains("not_your_session", why, StringComparison.Ordinal);
        Assert.Contains("run `ah roster checkin` in this session to re-register", why, StringComparison.Ordinal);
        Assert.True(own == 0, ownErr);
        Assert.DoesNotContain("ah roster checkin", ownErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AClaimedPidThatNoProvenSessionHoldsIsRefusedAndTheProvenOneIsAccepted()
    {
        AsMember();

        var (refused, why) = Run("inbox", "--orchestrator-pid", "1");
        var (own, ownErr) = Run("inbox", "--orchestrator-pid", Self);

        Assert.Equal(2, refused);
        Assert.Contains("not_your_session: pid", why, StringComparison.Ordinal);
        Assert.Contains("run `ah roster checkin` in this session to re-register", why, StringComparison.Ordinal);
        Assert.True(own == 0, ownErr);
    }

    [Fact]
    public void TwoProvenSessionsAndNoHintRefuseAWriteAsAmbiguousButLetAReadActForBoth()
    {
        AsMember();
        AsOrchestrator();

        var (write, why) = Run("ack", "e1");
        var (read, readErr) = Run("inbox");
        var (hinted, hintedErr) = Run("ack", "e1", "--session", "s-impl");

        Assert.Equal(2, write);
        Assert.Contains("ambiguous_session", why, StringComparison.Ordinal);
        Assert.True(read == 0, readErr);
        Assert.DoesNotContain("ambiguous_session", hintedErr, StringComparison.Ordinal);
        Assert.NotEqual(2, hinted);
    }

    [Fact]
    public void AckingARequestIdRecordsTheResponseAsReadAndAnythingElseStaysUnknown()
    {
        AsOrchestrator();
        _graph.Hierarchy.Peers.AppendDispatchRecord("o", "20260101-000000-t1aa", "implementor");

        var (request, requestErr) = Run("ack", "20260101-000000-t1aa", "--session", "o");
        var (other, _) = Run("ack", "e1", "--session", "o");
        var (entryShaped, _) = Run("ack", "20260101-000000-t9zz", "--session", "o");
        var (nearMiss, _) = Run("ack", "20260101-000000-t1a", "--session", "o");

        Assert.True(request == 0, requestErr);
        Assert.True(_graph.Hierarchy.Peers.ReportAcked("o", "20260101-000000-t1aa"));
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("a-resumed-session", ["tm"])("20260101-000000-t1aa"));
        Assert.Equal(1, other);
        Assert.Equal(1, entryShaped);
        Assert.Equal(1, nearMiss);
        Assert.False(_graph.Hierarchy.Peers.ReportAcked("o", "e1"));
        Assert.False(_graph.Hierarchy.Peers.ReportAcked("o", "20260101-000000-t9zz"));
        Assert.False(_graph.Hierarchy.Peers.ReportAcked("o", "20260101-000000-t1a"));
    }

    private const string ExchangeId = "20261022-120000-aaaa";

    private string BodyFile(string text)
    {
        var path = Path.Combine(_sandbox.Root, "body-" + Guid.NewGuid().ToString("N")[..6] + ".md");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void ARequestWithABacklogItemIsLinkedAsARefAndAnUnknownBacklogIdExitsOne()
    {
        using (var open = Ah.StateDb.StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(Ah.StateDb.StateDbAvailability.Ready, open.Status.Availability);
        }

        var addErr = new StringWriter();
        Assert.True(Ah.Cli.Verbs.BacklogVerb.Run(_context, ["add", "Groom loop", "--cwd", _sandbox.Root], new StringWriter(), addErr) == 0, addErr.ToString());
        AsOrchestrator();
        string[] Request(string item, string id) =>
            ["new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Build it", "--id", id, "--session", "o", "--item", item, "--body-file", BodyFile("do it")];

        var (unknown, why) = Run(Request("b9", "20261022-120002-cccc"));
        Assert.Equal(1, unknown);
        Assert.Contains("no backlog item b9", why, StringComparison.Ordinal);

        var (code, err) = Run(Request("b1", ExchangeId));
        Assert.True(code == 0, err);
        var show = new StringWriter();
        Assert.Equal(0, Ah.Cli.Verbs.BacklogVerb.Run(_context, ["show", "b1", "--history", "--json", "--cwd", _sandbox.Root], show, new StringWriter()));
        Assert.Contains(ExchangeId, show.ToString(), StringComparison.Ordinal);
    }

    private void OpenExchange()
    {
        AsOrchestrator();
        var (code, err) = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Fix test titles", "--id", ExchangeId, "--session", "o", "--body-file", BodyFile("do it"));
        Assert.True(code == 0, err);
    }

    private void FileResponse(string text)
    {
        AsMember();
        var (code, err) = Run("new", "--type", "response", "--id", ExchangeId, "--team", "tm", "--from", "implementor", "--session", "s-impl", "--body-file", BodyFile(text));
        Assert.True(code == 0, err);
    }

    [Fact]
    public void ARequestToAReviewerWithNoGoalWarnsOnStderrButIsWrittenAndOneToAnImplementorDoesNot()
    {
        AsOrchestrator();
        var empty = BodyFile("## [0] tldr\n- none\n## [1] goal\n- none\n## [2] context\n- some background\n## [5] acceptance\n- none\n");

        var (review, reviewErr) = Run("new", "--type", "request", "--to", "reviewer", "--from", "orchestrator", "--team", "tm", "--slug", "r", "--title", "Review it", "--id", "20261022-120001-bbbb", "--session", "o", "--body-file", empty);
        var (impl, implErr) = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "i", "--title", "Build it", "--id", "20261022-120002-cccc", "--session", "o", "--body-file", empty);

        Assert.True(review == 0, reviewErr);
        Assert.Contains("Missing: goal, acceptance", reviewErr, StringComparison.Ordinal);
        Assert.Contains("Minimal accepted shape", reviewErr, StringComparison.Ordinal);
        Assert.True(impl == 0, implErr);
        Assert.DoesNotContain("Missing:", implErr, StringComparison.Ordinal);
    }

    [Fact]
    public void AckOfAnExchangeIdFromTheDispatcherWithAFiledResponseRecordsTheAckForTheTeamsOrchestrator()
    {
        OpenExchange();
        FileResponse("DONE s\n- built");

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.True(_graph.Hierarchy.Peers.ReportAckedFor("some-later-session", ["tm"])(ExchangeId));
    }

    private string SendReportEntryNaming(string reference)
    {
        AsMember();
        var (code, err) = Run("send", "orchestrator@tm", "--kind", "report", "--ref", reference, "--session", "s-impl");
        Assert.True(code == 0, err);
        var id = System.Text.RegularExpressions.Regex.Match(_lastOut, "^entry ([A-Za-z0-9-]+) ");
        Assert.True(id.Success, _lastOut);
        return id.Groups[1].Value;
    }

    [Fact]
    public void AckOfAReportEntryThatNamesAnExchangeRecordsTheReportAckedForThatExchange()
    {
        OpenExchange();
        FileResponse("STATUS s\n- built");
        var response = Directory.GetFiles(_sandbox.Root, "*--response.md", SearchOption.AllDirectories).Single();
        var entry = SendReportEntryNaming(response);
        AsOrchestrator();

        var (code, err) = Run("ack", entry, "--session", "o");

        Assert.True(code == 0, err);
        Assert.True(_graph.Hierarchy.Peers.ReportAckedFor("some-later-session", ["tm"])(ExchangeId));
    }

    [Fact]
    public void NegativeAckOfAReportEntryWhoseRefNamesNoExchangeRecordsNothing()
    {
        OpenExchange();
        FileResponse("STATUS s\n- built");
        var response = Directory.GetFiles(_sandbox.Root, "*--response.md", SearchOption.AllDirectories).Single();
        var stray = Path.Combine(Path.GetDirectoryName(response)!, "20261022-999999-zzzz--orchestrator--s--response.md");
        File.WriteAllText(stray, "---\nid: 20261022-999999-zzzz\n---\nx\n");
        var entry = SendReportEntryNaming(stray);
        AsOrchestrator();

        var (code, err) = Run("ack", entry, "--session", "o");

        Assert.True(code == 0, err);
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("some-later-session", ["tm"])("20261022-999999-zzzz"));
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("some-later-session", ["tm"])(ExchangeId));
    }

    private string RollupFile => Path.Combine(_sandbox.Root, "hier", "progress", "tm", "rollup.md");

    // per-dir-files-to-db S11d: the rollup is a progress_docs row
    private string RollupText() => Ah.TestKit.TestProgressDocs.Read(_sandbox.AhHome, RollupFile);

    private const string BlockBegin = "<!-- ah:rollup-block:begin (managed by ah; text between the markers is rewritten) -->";

    private void SeedRollup(params string[] rows)
    {
        Ah.TestKit.TestProgressDocs.WriteFile(_sandbox.AhHome, RollupFile, "# mine\n" + BlockBegin + "\n" + string.Join("\n", rows) + "\n<!-- ah:rollup-block:end -->\n");
    }

    [Fact]
    public void AckOfAnExchangeSetsItsRollupLineToAckedKeepingStreamFromAndPathAndRestampingTheTime()
    {
        OpenExchange();
        FileResponse("DONE s\n- built");
        SeedRollup(
            "main | other | DONE | tm-reviewer | 2026-01-01T00:00:00Z | .claude/ah2/msgs/a--o--other--response.md",
            "stream-a | s | DONE | tm-implementor | 2026-01-01T00:00:00Z | .claude/ah2/msgs/b--o--s--response.md");

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        var rows = RollupText().Split('\n').Where(l => l.Contains(" | ", StringComparison.Ordinal)).ToList();
        Assert.Equal("main | other | DONE | tm-reviewer | 2026-01-01T00:00:00Z | .claude/ah2/msgs/a--o--other--response.md", rows[0]);
        var cells = rows[1].Split(" | ");
        Assert.Equal(["stream-a", "s", "acked", "tm-implementor"], cells[..4]);
        Assert.NotEqual("2026-01-01T00:00:00Z", cells[4]);
        Assert.EndsWith("Z", cells[4], StringComparison.Ordinal);
        Assert.Equal(".claude/ah2/msgs/b--o--s--response.md", cells[5]);
        Assert.StartsWith("# mine\n", RollupText(), StringComparison.Ordinal);
    }

    [Fact]
    public void AckOfAnExchangeWhoseSlugHasNoRollupLineAddsNoLineAndCreatesNoRollup()
    {
        OpenExchange();
        FileResponse("DONE s\n- built");

        var (none, noneErr) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(none == 0, noneErr);
        Assert.False(Ah.TestKit.TestProgressDocs.Exists(_sandbox.AhHome, RollupFile));
    }

    [Fact]
    public void AckOfAnExchangeLeavesAnotherSlugsLineAndABlocklessRollupAlone()
    {
        OpenExchange();
        FileResponse("DONE s\n- built");
        SeedRollup("main | other | DONE | tm-reviewer | 2026-01-01T00:00:00Z | .claude/ah2/msgs/a--o--other--response.md");
        var before = RollupText();

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.Equal(before, RollupText());
    }

    [Fact]
    public void NegativeAMembersAckOfAnExchangeItDidNotDispatchLeavesTheRollupLineAlone()
    {
        OpenExchange();
        FileResponse("DONE s\n- built");
        SeedRollup("main | s | DONE | tm-implementor | 2026-01-01T00:00:00Z | .claude/ah2/msgs/b--o--s--response.md");
        var before = RollupText();

        var (member, _) = Run("ack", ExchangeId, "--session", "s-impl");

        Assert.Equal(1, member);
        Assert.Equal(before, RollupText());
    }

    [Fact]
    public void NegativeAckOfAnExchangeWithNoResponseIsRefusedAndOneTheCallerDidNotDispatchStaysUnknown()
    {
        OpenExchange();

        var (none, _) = Run("ack", ExchangeId, "--session", "o");
        var noneOut = _lastOut;
        FileResponse("DONE s\n- built");
        var (member, _) = Run("ack", ExchangeId, "--session", "s-impl");

        Assert.Equal(1, none);
        Assert.Contains($"has no response yet {ExchangeId}", noneOut, StringComparison.Ordinal);
        Assert.Equal(1, member);
        Assert.Contains($"sent by orchestrator, not this session: {ExchangeId}", _lastOut, StringComparison.Ordinal);
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("s-impl", ["tm"])(ExchangeId));
    }

    [Fact]
    public void NegativeAMembersAckOfItsOwnExchangeNeverCountsForTheOrchestrator()
    {
        const string id = "20261022-120003-dddd";
        AsMember();
        var (opened, openErr) = Run("new", "--type", "request", "--to", "orchestrator", "--from", "implementor", "--team", "tm", "--slug", "q", "--title", "Fix test titles", "--id", id, "--session", "s-impl", "--body-file", BodyFile("what now"));
        Assert.True(opened == 0, openErr);
        AsOrchestrator();
        var (answered, answerErr) = Run("new", "--type", "response", "--id", id, "--team", "tm", "--from", "orchestrator", "--session", "o", "--body-file", BodyFile("DONE q\n- answer"));
        Assert.True(answered == 0, answerErr);

        var (code, err) = Run("ack", id, "--session", "s-impl");

        Assert.True(code == 0, err);
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("o", ["tm"])(id));
        Assert.True(_graph.Hierarchy.Peers.ReportAckedFor("s-impl")(id));
    }

    private string LinkItem(string item, string requestId, string owner)
    {
        var pool = HierarchyModule.ProgressPoolOf(_graph.Config.MainHierarchyDir(_sandbox.Root), Path.Combine(_sandbox.Root, "hier"));
        var progress = _graph.Hierarchy.Progress;
        Assert.Null(progress.RecordChecklist(pool, "tm", "_run", "o@tm", [new PlanInput(item, "add", "Do " + item), new PlanInput(item, "next")]).Refused);
        Assert.Null(HierarchyModule.ChecklistOnDispatch(progress, pool, "tm", item, owner, requestId, "o@tm", null));
        return pool;
    }

    private string StateOf(string pool, string item) => _graph.Hierarchy.Progress.ReadPlan(pool, "tm", "_run")!.Items.Single(i => i.Item == item).State;

    private string ChecklistPool => HierarchyModule.ProgressPoolOf(_graph.Config.MainHierarchyDir(_sandbox.Root), Path.Combine(_sandbox.Root, "hier"));

    private IReadOnlyList<PlanItem> RunItems() => _graph.Hierarchy.Progress.ReadPlan(ChecklistPool, "tm", "_run")?.Items ?? [];

    private (int Exit, string Err) OrchestratorRequest(string id, string slug = "s", params string[] extra) =>
        Run(["new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", slug, "--title", "Make UI slick", "--id", id, "--session", "o", "--body-file", BodyFile("do it"), .. extra]);

    [Fact]
    public void AnOrchestratorRequestWithNoItemAddsARunItemFromItsTitleLinksItAndSaysSoOnStderr()
    {
        AsOrchestrator();

        var (code, err) = OrchestratorRequest(ExchangeId);

        Assert.True(code == 0, err);
        var item = Assert.Single(RunItems());
        Assert.Equal("Make UI slick", item.Title);
        Assert.True(HierarchyModule.ChecklistIsAuto(item), item.Item);
        Assert.Contains(ExchangeId, item.Refs ?? []);
        Assert.Equal("dispatched", HierarchyModule.ChecklistDisplayState(item.State));
        Assert.Contains($"note: {ExchangeId} was not on the checklist; added item _run/{item.Item} and linked it.", err, StringComparison.Ordinal);
        Assert.Contains("--ref", err, StringComparison.Ordinal);
        Assert.Contains("--item", err, StringComparison.Ordinal);
        Assert.Single(err.Split('\n'), static l => l.StartsWith("note: ", StringComparison.Ordinal));
    }

    [Fact]
    public void TheAutoLinkLeavesStdoutAndTheExitCodeAsAnItemLinkedRequestHasThem()
    {
        AsOrchestrator();
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "o@tm", [new PlanInput("given", "add", "Given"), new PlanInput("given", "next")]).Refused);
        const string hinted = "20261022-120010-hhhh";
        const string linked = "20261022-120011-llll";

        var (hintedCode, hintedErr) = OrchestratorRequest(hinted);
        var hintedOut = _lastOut;
        var (linkedCode, linkedErr) = OrchestratorRequest(linked, "s", "--item", "given");

        Assert.Equal(linkedCode, hintedCode);
        Assert.Equal(_lastOut, hintedOut.Replace(hinted, linked, StringComparison.Ordinal));
        Assert.Contains("note: " + hinted, hintedErr, StringComparison.Ordinal);
        Assert.DoesNotContain("not on the checklist", linkedErr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAnOrchestratorRequestWithAnItemAddsNoItemAndPrintsNoHint()
    {
        AsOrchestrator();
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "o@tm", [new PlanInput("given", "add", "Given"), new PlanInput("given", "next")]).Refused);

        var (code, err) = OrchestratorRequest(ExchangeId, "s", "--item", "given");

        Assert.True(code == 0, err);
        Assert.DoesNotContain("not on the checklist", err, StringComparison.Ordinal);
        Assert.Equal("given", Assert.Single(RunItems()).Item);
    }

    [Fact]
    public void NegativeTheRunsPipelineAnchorAddsNoItemAndPrintsNoHint()
    {
        AsOrchestrator();

        var (code, err) = OrchestratorRequest(ExchangeId, HierarchyModule.PipelineAnchorSlug);

        Assert.True(code == 0, err);
        Assert.DoesNotContain("not on the checklist", err, StringComparison.Ordinal);
        Assert.Empty(RunItems());
    }

    [Fact]
    public void NegativeAMembersRequestAndAResponseAddNoItemAndPrintNoHint()
    {
        AsMember();
        var (asked, askedErr) = Run("new", "--type", "request", "--to", "orchestrator", "--from", "implementor", "--team", "tm", "--slug", "q", "--title", "Fix test titles", "--id", ExchangeId, "--session", "s-impl", "--body-file", BodyFile("what now"));
        AsOrchestrator();
        var (answered, answeredErr) = Run("new", "--type", "response", "--id", ExchangeId, "--team", "tm", "--from", "orchestrator", "--session", "o", "--body-file", BodyFile("answer"));

        Assert.True(asked == 0, askedErr);
        Assert.True(answered == 0, answeredErr);
        Assert.DoesNotContain("not on the checklist", askedErr + answeredErr, StringComparison.Ordinal);
        Assert.Empty(RunItems());
    }

    [Fact]
    public void ASubOrchestratorsRequestWithNoItemIsAddedAndLinkedOnItsOwnTeam()
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, "sub-orchestrator@tm");

        var (code, err) = Run("new", "--type", "request", "--to", "implementor", "--from", "sub-orchestrator", "--team", "tm", "--slug", "s", "--title", "Make UI slick", "--id", ExchangeId, "--session", "o", "--body-file", BodyFile("do it"));

        Assert.True(code == 0, err);
        Assert.Contains(ExchangeId, Assert.Single(RunItems()).Refs ?? []);
        Assert.Contains($"note: {ExchangeId} was not on the checklist; added item _run/", err, StringComparison.Ordinal);
        Assert.Contains("ah msg new --item", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorsRequestInItsOwnChildTeamRecordsThatTeamAsOneItLeads()
    {
        // ah2-49: tm records this process as its orchestrator, so the lane lead filing into tm leads tm
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, "sub-orchestrator@tm");

        var (code, err) = Run("new", "--type", "request", "--to", "implementor", "--from", "sub-orchestrator", "--team", "tm", "--slug", "s", "--title", "Make UI slick", "--id", ExchangeId, "--session", "o", "--body-file", BodyFile("do it"));

        Assert.True(code == 0, err);
        var path = Path.Combine(Path.GetDirectoryName(_teamFile)!, "..", "msgs", $"{ExchangeId}--implementor--s--request.md");
        var leads = _graph.Hierarchy.Core.ReadMsgFile(Path.GetFullPath(path)).Member("fm")?.Member("from_leads");
        Assert.Contains("tm", (leads?.AsString() ?? "<none>").Split(','));
    }

    [Theory]
    [InlineData("sub-orchestrator", true)]
    [InlineData("implementor", false)]
    public void ALaneLeadsRequestLinksItsItemAtDispatchAndNegativeAMembersIsRefused(string from, bool linked)
    {
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, from + "@tm");
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "o@tm", [new PlanInput("given", "add", "Given"), new PlanInput("given", "next")]).Refused);

        var (code, err) = Run("new", "--type", "request", "--to", "implementor", "--from", from, "--team", "tm", "--slug", "s", "--title", "Make UI slick", "--item", "given", "--id", ExchangeId, "--session", "o", "--body-file", BodyFile("do it"));

        Assert.Equal(linked, code == 0);
        if (linked)
        {
            Assert.Contains(ExchangeId, Assert.Single(RunItems()).Refs ?? []);
        }
        else
        {
            Assert.Contains("--item is for a request from the Orchestrator or a lane lead", err, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NegativeAnAutoTrackedNoteRequestAddsNoItemAndPrintsNoHint()
    {
        AsOrchestrator();

        var (code, err) = OrchestratorRequest(ExchangeId, "s", "--reason", HierarchyModule.AutoTrackedNoteReason);

        Assert.True(code == 0, err);
        Assert.DoesNotContain("not on the checklist", err, StringComparison.Ordinal);
        Assert.Empty(RunItems());
    }

    [Fact]
    public void AfterItsRefMovesToARealItemTheRequestsDoneReplyMovesTheRealItemNotTheAutoOne()
    {
        OpenExchange();
        var auto = Assert.Single(RunItems()).Item;
        var progress = _graph.Hierarchy.Progress;
        Assert.Null(progress.RecordChecklist(ChecklistPool, "tm", "_run", "o@tm", [new PlanInput(auto, "drop", Note: "superseded by _run/real"), new PlanInput("real", "add", "Real"), new PlanInput("real", "start"), new PlanInput("real", "ref", Ref: ExchangeId)]).Refused);
        FileResponse("DONE s\n- built");
        AsOrchestrator();

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.DoesNotContain("several items", err, StringComparison.Ordinal);
        Assert.Equal("reported", StateOf(ChecklistPool, "real")); // spec board-derive B1: a DONE waits on the Orchestrator; a ship closes it
        Assert.Equal("dropped", StateOf(ChecklistPool, auto));
    }

    [Fact]
    public void NegativeResendingAnExistingRequestAddsNoSecondItem()
    {
        AsOrchestrator();
        var (first, firstErr) = OrchestratorRequest(ExchangeId);

        var (again, againErr) = OrchestratorRequest(ExchangeId);

        Assert.True(first == 0, firstErr);
        Assert.True(again == 0, againErr);
        Assert.DoesNotContain("not on the checklist", againErr, StringComparison.Ordinal);
        Assert.Single(RunItems());
    }

    [Fact]
    public void TheTopOrchestratorsAckOfAPassApprovesTheLinkedItemAndNegativeHoldLeavesIt()
    {
        OpenExchange(); // sent without --item: msg new adds and links a _run item for it
        var linked = Assert.Single(RunItems()).Item;
        FileResponse("VERDICT PASS s@abc1234");
        AsOrchestrator();

        var (held, heldErr) = Run("ack", ExchangeId, "--hold", "--session", "o");
        Assert.True(held == 0, heldErr);
        Assert.NotEqual("approved", StateOf(ChecklistPool, linked));

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.Equal("approved", StateOf(ChecklistPool, linked));
    }

    [Fact]
    public void NegativeTheTopOrchestratorsAckOfADoneResponseLeavesTheLinkedItemReported()
    {
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;
        FileResponse("DONE s\n- built");
        AsOrchestrator();

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.Equal("reported", StateOf(ChecklistPool, linked));
    }

    [Fact]
    public void ADoneIsReportedAReviewerRequestPutsItInReviewKeepingItsHolderAWithdrawnReviewReportsItAgainAndAPassApprovesIt()
    {
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;
        FileResponse("DONE s\n- built");
        Assert.Equal("reported", StateOf(ChecklistPool, linked));
        var holder = Assert.Single(RunItems()).Holder;
        void Review(string id)
        {
            AsOrchestrator();
            var (code, err) = Run("new", "--type", "request", "--to", "reviewer", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Review it", "--id", id, "--item", linked, "--session", "o", "--body-file", BodyFile("## [1] goal\n- review s\n"));
            Assert.True(code == 0, err);
        }

        string? Event(string kind, string id, string? text = null) => HierarchyModule.ChecklistOnEvent(
            _graph.Hierarchy.Progress, ChecklistPool, "tm", new ChecklistEvent(kind, "2030-01-01T00:00:00Z", id, "tm-reviewer") { Text = text, Role = "reviewer" }, "tm-reviewer@tm", null);

        Review("20261022-120001-rrrr");
        Assert.Equal("in_review", StateOf(ChecklistPool, linked));
        Assert.Equal(holder, Assert.Single(RunItems()).Holder);

        Assert.Null(Event(HierarchyModule.ChecklistEventWithdraw, "20261022-120001-rrrr"));
        Assert.Equal("reported", StateOf(ChecklistPool, linked));

        Review("20261022-120002-rrrr");
        Assert.Null(Event(HierarchyModule.ChecklistEventReply, "20261022-120002-rrrr", "VERDICT PASS s@abc1234"));
        Assert.Equal("approved", StateOf(ChecklistPool, linked));

        // a review of work that never reported DONE is in review, and reports nothing
        var progress = _graph.Hierarchy.Progress;
        Assert.Null(progress.RecordChecklist(ChecklistPool, "tm", "_run", "o@tm", [new PlanInput("solo", "add", "Solo")]).Refused);
        Assert.Null(HierarchyModule.ChecklistOnDispatch(progress, ChecklistPool, "tm", "solo", "tm-reviewer", "20261022-120003-rrrr", "orchestrator@tm", null, role: "reviewer"));
        var solo = Assert.Single(RunItems(), static i => i.Item == "solo");
        Assert.Equal("in_review", solo.State);
        Assert.False(solo.ReportedDone);
    }

    [Fact]
    public void AnAckOfADispatchedIdMovesTheItemTooAsAnAckByExchangeDoes()
    {
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;
        _graph.Hierarchy.Peers.AppendDispatchRecord("o", ExchangeId, "implementor");
        FileResponse("CHANGES REQUIRED: two findings");
        AsOrchestrator();

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.Equal("waiting_orch", StateOf(ChecklistPool, linked));
        Assert.Equal("CHANGES REQUIRED: two findings", Assert.Single(RunItems()).Note);
    }

    [Fact]
    public void AnAckOfAReportWithNoItemSaysSoInOneLineAndIsNotRefused()
    {
        AsOrchestrator();
        var (opened, openErr) = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", HierarchyModule.PipelineAnchorSlug, "--title", "Fix test titles", "--id", ExchangeId, "--session", "o", "--body-file", BodyFile("## [1] goal\n- g\n## [5] acceptance\n- a\n"));
        Assert.True(opened == 0, openErr);
        FileResponse("DONE s\n- built");
        AsOrchestrator();

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.Single(err.Split('\n'), l => l.Contains("moved no checklist item", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAnAckOfAStatusSaysNothingAboutAnItem()
    {
        AsOrchestrator();
        var (opened, openErr) = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", HierarchyModule.PipelineAnchorSlug, "--title", "Fix test titles", "--id", ExchangeId, "--session", "o", "--body-file", BodyFile("## [1] goal\n- g\n## [5] acceptance\n- a\n"));
        Assert.True(opened == 0, openErr);
        FileResponse("STATUS half way");
        AsOrchestrator();

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.DoesNotContain("no checklist item", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ALaterDispatchMovesAnItemBackFromAnyStateButDone()
    {
        const string item = "again";
        foreach (var (state, id) in new[] { ("review", "20261022-120101-aaaa"), ("wait-user", "20261022-120102-bbbb"), ("drop", "20261022-120103-cccc") })
        {
            AsOrchestrator();
            var result = _graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput(item + state, "add", "Again"), new PlanInput(item + state, state, Note: "why")], "o");
            Assert.Null(result.Refused);

            var (code, err) = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Fix test titles", "--item", item + state, "--id", id, "--session", "o", "--body-file", BodyFile("## [1] goal\n- g\n## [5] acceptance\n- a\n"));

            Assert.True(code == 0, err);
            Assert.Equal("in_progress", StateOf(ChecklistPool, item + state));
        }

        _ = _graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput("closed", "add", "Closed"), new PlanInput("closed", "done")], "o");
        var (refused, why) = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Fix test titles", "--item", "closed", "--id", "20261022-120104-dddd", "--session", "o", "--body-file", BodyFile("## [1] goal\n- g\n## [5] acceptance\n- a\n"));
        Assert.NotEqual(0, refused);
        Assert.Contains("item closed is done", why, StringComparison.Ordinal);
    }

    private const string StepShip = "0123456789abcdef0123456789abcdef01234567";

    /// <summary>b525: item <c>step</c> dispatched under slug s, then closed by <c>checklist ship</c>.</summary>
    private void ShipClosedStep()
    {
        AsOrchestrator();
        _ = LinkItem("step", "20261113-100000-aaaa", "tm-implementor");
        var (moved, _, refused) = HierarchyModule.ChecklistOnShip(_graph.Hierarchy.Progress, ChecklistPool, "tm", StepShip, false, DateTimeOffset.Parse("2026-11-13T10:30:00Z", CultureInfo.InvariantCulture), "orchestrator@tm", "o", static _ => "s", static (_, _) => true);
        Assert.Null(refused);
        Assert.Equal(["_run/step"], moved);
    }

    /// <summary>b471: a ship closes the backlog row of the last open item it closed, as the release watch does; not while a sibling under the row is open.</summary>
    [Fact]
    public void AShipClosesTheBacklogRowOfItsLastOpenItemAndNegativeNotWhileASiblingIsOpen()
    {
        AsOrchestrator();
        var progress = _graph.Hierarchy.Progress;
        var closed = new List<string>();
        (IReadOnlyList<string> Items, IReadOnlyList<string> Skipped, string? Refused) Ship() => HierarchyModule.ChecklistOnShip(
            progress, ChecklistPool, "tm", StepShip, false, DateTimeOffset.Parse("2026-11-13T10:30:00Z", CultureInfo.InvariantCulture), "orchestrator@tm", "o",
            static _ => "s", static (_, _) => true, (row, sha) => closed.Add(row + " " + sha));

        _ = LinkItem("step", "20261113-100000-aaaa", "tm-implementor");
        Parented("step", "b7");
        Assert.Null(progress.RecordChecklist(ChecklistPool, "tm", "_run", "o@tm", [new PlanInput("sibling", "add", "Sibling"), new PlanInput("sibling", "next")]).Refused);
        Parented("sibling", "b7");
        Assert.Equal(["_run/step"], Ship().Items);
        Assert.Empty(closed);

        _ = LinkItem("alone", "20261113-100001-bbbb", "tm-implementor");
        Parented("alone", "b8");
        Assert.Equal(["_run/alone"], Ship().Items);
        Assert.Equal(["b8 " + StepShip], closed);
    }

    private (int Exit, string Err) NextStep(string item, string id) =>
        Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s", "--title", "Next step", "--item", item, "--id", id, "--session", "o", "--body-file", BodyFile("## [1] goal\n- g\n## [5] acceptance\n- a\n"));

    [Fact]
    public void NegativeAShipClosedItemWithNoNewRequestStaysDone()
    {
        ShipClosedStep();

        Assert.Equal("done", StateOf(ChecklistPool, "step"));
    }

    [Fact]
    public void ANewRequestForAShipClosedItemReopensItDispatchedWithANoteAndWritesTheRequest()
    {
        ShipClosedStep();
        const string id = "20261113-110000-bbbb";

        var (code, err) = NextStep("step", id);

        Assert.True(code == 0, err);
        var step = RunItems().Single(i => i.Item == "step");
        Assert.Equal("in_progress", step.State);
        Assert.Equal($"reopened by request {id} after ship {StepShip[..8]}", step.Note);
        Assert.Contains(id, step.Refs ?? []);
    }

    [Fact]
    public void NegativeANewRequestForAnItemClosedByChecklistDoneIsRefusedAsToday()
    {
        AsOrchestrator();
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput("handdone", "add", "Hand done"), new PlanInput("handdone", "done") { Cause = HierarchyModule.ChecklistManualCause(DateTimeOffset.Parse("2026-11-13T10:30:00Z", CultureInfo.InvariantCulture)) }], "o").Refused);

        var (code, err) = NextStep("handdone", "20261113-110001-cccc");

        Assert.NotEqual(0, code);
        Assert.Contains("item handdone is done", err, StringComparison.Ordinal);
        Assert.Equal("done", StateOf(ChecklistPool, "handdone"));
    }

    private static readonly string[] Identity = ["-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false"];

    private static string InRepo(string repo, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in Identity.Concat(args))
        {
            start.ArgumentList.Add(a);
        }

        using var git = System.Diagnostics.Process.Start(start)!;
        var stderr = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        Assert.True(git.ExitCode == 0, string.Join(' ', args) + ": " + stderr.Result);
        return output;
    }

    private static string? Quiet(string repo, string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repo, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
        {
            start.ArgumentList.Add(a);
        }

        using var git = System.Diagnostics.Process.Start(start)!;
        _ = git.StandardError.ReadToEndAsync();
        var output = git.StandardOutput.ReadToEnd().Trim();
        git.WaitForExit();
        return git.ExitCode == 0 ? output : null;
    }

    /// <summary>A repo with a release ref at a first commit <c>A</c>: one tick of the watch at the returned callback closes items, and rows the backlog closes land in the list.</summary>
    private (string A, string B, Action<string> ShipTo, List<(string Row, string Sha)> Closed) BacklogShip(string name)
    {
        const string release = "refs/remotes/origin/ah/release-x";
        var repo = Path.Combine(_sandbox.Root, name);
        Directory.CreateDirectory(repo);
        _ = InRepo(repo, "init", "-q", "-b", "trunk");
        string Commit(string message)
        {
            _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", message);
            return InRepo(repo, "rev-parse", "HEAD");
        }

        var a = Commit("a");
        var b = Commit("b");
        var closed = new List<(string, string)>();
        void ShipTo(string sha)
        {
            _ = InRepo(repo, "update-ref", release, sha);
            ReleaseWatch.Tick(_graph.Hierarchy.Progress, ChecklistPool, ["tm", "other"], args => Quiet(repo, args), static _ => null, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static (_, problem) => Assert.Null(problem), (row, sha) => closed.Add((row, sha)));
        }

        return (a, b, ShipTo, closed);
    }

    private void Parented(string item, string parent, string team = "tm") =>
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, team, "_run", "orchestrator@" + team, [new PlanInput(item, "parent") { Parent = parent }], "o").Refused);

    [Fact]
    public void AShipThatClosesTheLastOpenItemOfAParentClosesTheBacklogRowWithThatCommit()
    {
        var (a, b, shipTo, closed) = BacklogShip("backlog-last");
        shipTo(a);
        ReportedItem("first", b, a, "ah/first");
        ReportedItem("second", b, a, "ah/second");
        Parented("first", "b96");
        Parented("second", "b96");

        shipTo(b);

        Assert.Equal(("b96", b), Assert.Single(closed));
    }

    /// <summary>A repo with a release ref seen at its first commit, and a commit per message on top of it; the returned callback ships them all.</summary>
    private (List<string> Shas, Action Ship, List<(string Row, string Sha)> Closed) CommitsAfterBaseline(string name, params string[] messages)
    {
        var (a, _, shipTo, closed) = BacklogShip(name);
        shipTo(a);
        var repo = Path.Combine(_sandbox.Root, name);
        var shas = new List<string>();
        foreach (var message in messages)
        {
            _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", message);
            shas.Add(InRepo(repo, "rev-parse", "HEAD"));
        }

        return (shas, () => shipTo(shas[^1]), closed);
    }

    [Fact]
    public void AShipClosesTheBacklogRowsItsCommitsNameWithTheCommitThatNamedThem()
    {
        var (shas, ship, closed) = CommitsAfterBaseline("backlog-named", "fix one (b96)", "plain work", "fix two (b97, #376)");

        ship();

        Assert.Equal([("b97", shas[2]), ("b96", shas[0])], closed);
    }

    [Theory]
    [InlineData("ship batch b6-b13")]
    [InlineData("(b6-b13)")]
    [InlineData("b96 is mentioned here")]
    [InlineData("mentions (b96) in the middle")]
    [InlineData("work (part of b96)")]
    [InlineData("Revert \"fix (b96)\"")]
    [InlineData("build (b96x)")]
    public void NegativeACommitThatDoesNotEndByNamingARowClosesNone(string subject)
    {
        var (_, ship, closed) = CommitsAfterBaseline("backlog-unnamed", subject);

        ship();

        Assert.Empty(closed);
    }

    [Fact]
    public void NegativeAFirstSightClosesNoRowItsHistoryNames()
    {
        var (_, _, shipTo, closed) = BacklogShip("backlog-history");
        var repo = Path.Combine(_sandbox.Root, "backlog-history");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "old work (b96)");

        shipTo(InRepo(repo, "rev-parse", "HEAD"));

        Assert.Empty(closed);
    }

    [Fact]
    public void NegativeACommitNotYetOnTheReleaseRefClosesNoRow()
    {
        var (a, b, shipTo, closed) = BacklogShip("backlog-unshipped");
        shipTo(a);
        var repo = Path.Combine(_sandbox.Root, "backlog-unshipped");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "later work (b96)");

        shipTo(b);

        Assert.Empty(closed);
    }

    [Fact]
    public void AFirstSightThatClosesTheLastItemClosesTheBacklogRowToo()
    {
        var (_, b, shipTo, closed) = BacklogShip("backlog-first");
        ReportedItem("first", b, "0000000", "ah/first");
        Parented("first", "b96");

        shipTo(b);

        Assert.Equal(("b96", b), Assert.Single(closed));
    }

    [Fact]
    public void NegativeAParentWithAnotherOpenItemDoesNotClose()
    {
        var (a, b, shipTo, closed) = BacklogShip("backlog-open");
        shipTo(a);
        ReportedItem("first", b, a, "ah/first");
        ReportedItem("held", a, a, "ah/held");
        ReportedItem("elsewhere", a, a, "ah/elsewhere", team: "other");
        Parented("first", "b96");
        Parented("held", "b96");
        Parented("elsewhere", "b96", team: "other");

        shipTo(b);

        Assert.Equal("done", StateOf(ChecklistPool, "first"));
        Assert.Empty(closed);
    }

    [Fact]
    public void NegativeAnOpenItemOfTheParentInAnotherTeamHoldsItOpen()
    {
        var (a, b, shipTo, closed) = BacklogShip("backlog-team");
        shipTo(a);
        ReportedItem("first", b, a, "ah/first");
        ReportedItem("elsewhere", a, a, "ah/elsewhere", team: "other");
        Parented("first", "b96");
        Parented("elsewhere", "b96", team: "other");

        shipTo(b);

        Assert.Empty(closed);
    }

    [Fact]
    public void NegativeAParentlessItemClosesNoBacklogRow()
    {
        var (a, b, shipTo, closed) = BacklogShip("backlog-none");
        shipTo(a);
        ReportedItem("first", b, a, "ah/first");

        shipTo(b);

        Assert.Equal("done", StateOf(ChecklistPool, "first"));
        Assert.Empty(closed);
    }

    [Fact]
    public void NegativeAParentWhoseItemsADifferentShipClosedDoesNotCloseOnALaterShip()
    {
        var (a, b, shipTo, closed) = BacklogShip("backlog-later");
        shipTo(a);
        ReportedItem("first", a, a, "ah/first");
        Parented("first", "b96");
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput("first", "done")], "o").Refused);

        shipTo(b);

        Assert.Empty(closed);
    }

    [Fact]
    public void NegativeAFirstSightOfAnItemWhoseShaIsNotInTheTipClosesNothing()
    {
        var (a, b, shipTo, closed) = BacklogShip("first-rollback");
        ReportedItem("rolled", b, "0000000", "ah/rolled");
        Parented("rolled", "b96");

        shipTo(a);

        Assert.Equal("in_review", StateOf(ChecklistPool, "rolled"));
        Assert.Empty(closed);
    }

    private void ReportedItem(string item, string reported, string baseSha, string branch, string team = "tm")
    {
        var refused = _graph.Hierarchy.Progress.RecordChecklist(
            ChecklistPool, team, "_run", "orchestrator@" + team,
            [new PlanInput(item, "add", item), new PlanInput(item, "start"), new PlanInput(item, "ref", Ref: "20261022-110000-" + item) { Base = baseSha }, new PlanInput(item, "review", Commits: [reported], Branch: branch)],
            "o").Refused;
        Assert.Null(refused);
    }

    [Fact]
    public void TheDaemonSeesTheReleaseRefMoveAndClosesByAncestryWithNoCommandRun()
    {
        const string release = "refs/remotes/origin/ah/release-x";
        var repo = Path.Combine(_sandbox.Root, "repo");
        Directory.CreateDirectory(repo);
        _ = InRepo(repo, "init", "-q", "-b", "trunk");
        string Commit(string message)
        {
            _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", message);
            return InRepo(repo, "rev-parse", "HEAD");
        }

        var start = Commit("start");
        void Tick() => ReleaseWatch.Tick(_graph.Hierarchy.Progress, ChecklistPool, ["tm"], args => Quiet(repo, args), static _ => null, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static (_, problem) => Assert.Null(problem));

        // a first sighting is a baseline: it closes a reviewed item its tip holds, and nothing else
        _ = InRepo(repo, "switch", "-q", "-c", "ah/old");
        var old = Commit("old");
        _ = InRepo(repo, "update-ref", release, old);
        ReportedItem("nothing", start, start, "ah/empty");
        ReportedItem("cutover", old, start, "ah/old");
        ReportedItem("redispatched", old, start, "ah/old");
        _ = _graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput("redispatched", "start")], "o");
        Tick();
        Assert.Equal("done", StateOf(ChecklistPool, "cutover"));
        Assert.Equal("in_review", StateOf(ChecklistPool, "nothing"));
        Assert.Equal("in_progress", StateOf(ChecklistPool, "redispatched"));
        Assert.Empty(_graph.Hierarchy.Progress.ReadLedger(ChecklistPool, "tm"));
        ReportedItem("already", old, start, "ah/old");

        // the work reaches the release through a ship branch of another name, with fix commits on top
        _ = InRepo(repo, "switch", "-q", "-c", "ah/feat", "trunk");
        var feat = Commit("feat");
        _ = InRepo(repo, "switch", "-q", "-c", "ah/unshipped", "trunk");
        var unshipped = Commit("unshipped");
        _ = InRepo(repo, "switch", "-q", "-c", "ah/ship/some-other-name", old);
        _ = InRepo(repo, "merge", "-q", "--no-ff", "-m", "ship", feat);
        var tip = Commit("fix on top");
        ReportedItem("shipped", feat, start, "ah/feat");
        ReportedItem("open", unshipped, start, "ah/unshipped");
        ReportedItem("rework", feat, start, "ah/feat");
        _ = _graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput("rework", "start")], "o");

        // not a release ref: moving it is no ship
        _ = InRepo(repo, "update-ref", "refs/remotes/origin/ah/feature", tip);
        Tick();
        Assert.Equal("in_review", StateOf(ChecklistPool, "shipped"));

        _ = InRepo(repo, "update-ref", release, tip);
        Tick();
        Tick();

        Assert.Equal("done", StateOf(ChecklistPool, "shipped"));
        Assert.Equal("in_review", StateOf(ChecklistPool, "already"));
        Assert.Equal("in_review", StateOf(ChecklistPool, "open"));
        Assert.Equal("in_review", StateOf(ChecklistPool, "nothing"));
        Assert.Equal("in_progress", StateOf(ChecklistPool, "rework"));
        var ship = Assert.Single(_graph.Hierarchy.Progress.ReadLedger(ChecklistPool, "tm"), r => r.Member("kind")?.AsString() == "ship");
        Assert.Equal(tip, ship.Member("sha")?.AsString());
        Assert.Equal("ah/release-x", ship.Member("ref")?.AsString());
        Assert.Equal(new[] { feat }, RunItems().Single(i => i.Item == "shipped").Commits);

        // a rollback carries nothing, though the new tip contains the reported sha
        ReportedItem("rolled", old, start, "ah/old");
        _ = InRepo(repo, "update-ref", release, old);
        Tick();
        Assert.Equal("in_review", StateOf(ChecklistPool, "rolled"));
    }

    /// <summary>Progress that refuses one method while <see cref="Refusal"/> is set, and otherwise is the real one.</summary>
    public class RefusingProgress : System.Reflection.DispatchProxy
    {
        internal IHierarchyProgress Inner { get; set; } = null!;

        internal string Method { get; set; } = string.Empty;

        internal object? Refusal { get; set; }

        internal Func<object?[], bool> When { get; set; } = static _ => true;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == Method && Refusal is { } refusal && When(args!) ? refusal : targetMethod.Invoke(Inner, args);
    }

    [Theory]
    [InlineData("AppendLedger")]
    [InlineData("RecordChecklist")]
    public void ARefusedWriteLeavesTheRefUnseenWithALogAndTheNextTickClosesTheItem(string method)
    {
        var repo = Path.Combine(_sandbox.Root, "repo");
        Directory.CreateDirectory(repo);
        _ = InRepo(repo, "init", "-q", "-b", "trunk");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "start");
        var start = InRepo(repo, "rev-parse", "HEAD");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "work");
        var work = InRepo(repo, "rev-parse", "HEAD");
        ReportedItem("x", work, start, "ah/x");
        var progress = System.Reflection.DispatchProxy.Create<IHierarchyProgress, RefusingProgress>();
        var proxy = (RefusingProgress)(object)progress;
        proxy.Inner = _graph.Hierarchy.Progress;
        proxy.Method = method;
        proxy.Refusal = method == "AppendLedger" ? new LedgerAppend(false, false, "disk full") : new PlanWriteResult(false, 0, "disk full", null);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal) { ["tm ah/release-x"] = start };
        var tips = new Dictionary<string, string>(StringComparer.Ordinal) { ["ah/release-x"] = work };
        var lines = new List<string>();
        var report = ReleaseWatch.LogChanges(new Dictionary<string, string>(StringComparer.Ordinal), lines.Add);
        bool Tick() => HierarchyModule.ChecklistOnReleaseTips(
            progress, ChecklistPool, ["tm"], tips, seen, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static _ => null, (rev, commit) => Quiet(repo, ["merge-base", "--is-ancestor", rev, commit]) is not null, report);

        Assert.False(Tick());
        Assert.False(Tick());
        Assert.Equal(start, seen["tm ah/release-x"]);
        Assert.Contains("disk full", Assert.Single(lines), StringComparison.Ordinal);
        Assert.Equal("in_review", StateOf(ChecklistPool, "x"));

        proxy.Refusal = null;
        Assert.True(Tick());
        Assert.Equal(work, seen["tm ah/release-x"]);
        Assert.Single(lines);
        Assert.Equal("done", StateOf(ChecklistPool, "x"));
        Assert.Single(_graph.Hierarchy.Progress.ReadLedger(ChecklistPool, "tm"), r => r.Member("kind")?.AsString() == "ship");
    }

    /// <summary>A repo with a start commit and a work commit, and the refusing progress over the real one.</summary>
    private (string Repo, string Start, string Work, IHierarchyProgress Progress, RefusingProgress Proxy) RefusalRig()
    {
        var repo = Path.Combine(_sandbox.Root, "repo");
        Directory.CreateDirectory(repo);
        _ = InRepo(repo, "init", "-q", "-b", "trunk");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "start");
        var start = InRepo(repo, "rev-parse", "HEAD");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "work");
        var progress = System.Reflection.DispatchProxy.Create<IHierarchyProgress, RefusingProgress>();
        var proxy = (RefusingProgress)(object)progress;
        proxy.Inner = _graph.Hierarchy.Progress;
        return (repo, start, InRepo(repo, "rev-parse", "HEAD"), progress, proxy);
    }

    [Fact]
    public void OneTeamsRefusalDoesNotFreezeAnothersPriorTipAndNegativeTheRefusedTeamKeepsItsOwn()
    {
        var (repo, start, work, progress, proxy) = RefusalRig();
        ReportedItem("a", work, start, "ah/a", team: "ta");
        ReportedItem("b", work, start, "ah/b", team: "tb");
        proxy.Method = "AppendLedger";
        proxy.Refusal = new LedgerAppend(false, false, "disk full");
        proxy.When = static args => (string?)args[1] == "ta";
        var seen = new Dictionary<string, string>(StringComparer.Ordinal) { ["ta ah/release-x"] = start, ["tb ah/release-x"] = start };
        var heard = new List<(string Key, string? Problem)>();

        var changed = HierarchyModule.ChecklistOnReleaseTips(
            progress, ChecklistPool, ["ta", "tb"], new Dictionary<string, string>(StringComparer.Ordinal) { ["ah/release-x"] = work }, seen,
            DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static _ => null, (rev, commit) => Quiet(repo, ["merge-base", "--is-ancestor", rev, commit]) is not null, (key, problem) => heard.Add((key, problem)));

        Assert.True(changed);
        Assert.Equal(start, seen["ta ah/release-x"]);
        Assert.Equal(work, seen["tb ah/release-x"]);
        Assert.Equal([("ta ah/release-x", "disk full"), ("tb ah/release-x", null)], heard);
        Assert.Equal("done", _graph.Hierarchy.Progress.ReadPlan(ChecklistPool, "tb", "_run")!.Items.Single(i => i.Item == "b").State);
        Assert.Equal("in_review", _graph.Hierarchy.Progress.ReadPlan(ChecklistPool, "ta", "_run")!.Items.Single(i => i.Item == "a").State);
    }

    [Fact]
    public void ChecklistIsLinkedAnswersForTheItemOrItsRefAndNegativeNotForARequestOnNoItem()
    {
        var (_, start, work, _, _) = RefusalRig();
        ReportedItem("x", work, start, "ah/x");

        Assert.True(HierarchyModule.ChecklistIsLinked(_graph.Hierarchy.Progress, ChecklistPool, "tm", "20261022-110000-x", "x"));
        Assert.True(HierarchyModule.ChecklistIsLinked(_graph.Hierarchy.Progress, ChecklistPool, "tm", "20261022-110000-x", null));
        Assert.False(HierarchyModule.ChecklistIsLinked(_graph.Hierarchy.Progress, ChecklistPool, "tm", "20261022-110000-none", null));
    }

    [Fact]
    public void AnUnreadablePriorTipClosesNothingAndNegativeTheBaselineStillAdvances()
    {
        var (repo, start, work, _, _) = RefusalRig();
        ReportedItem("x", work, start, "ah/x");
        const string gone = "0123456789012345678901234567890123456789";
        var seen = new Dictionary<string, string>(StringComparer.Ordinal) { ["tm ah/release-x"] = gone };

        _ = HierarchyModule.ChecklistOnReleaseTips(
            _graph.Hierarchy.Progress, ChecklistPool, ["tm"], new Dictionary<string, string>(StringComparer.Ordinal) { ["ah/release-x"] = work }, seen,
            DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static _ => null, (rev, commit) => Quiet(repo, ["merge-base", "--is-ancestor", rev, commit]) is not null, static (_, problem) => Assert.Null(problem));

        Assert.Equal("in_review", StateOf(ChecklistPool, "x"));
        Assert.Equal(work, seen["tm ah/release-x"]);
    }

    [Fact]
    public void ARefusedItemDoesNotStopTheOtherItemsOfTheSameMoveAndNegativeItStaysToRetry()
    {
        var (repo, start, work, progress, proxy) = RefusalRig();
        ReportedItem("x", work, start, "ah/x");
        ReportedItem("y", work, start, "ah/y");
        proxy.Method = "RecordChecklist";
        proxy.Refusal = new PlanWriteResult(false, 0, "disk full", null);
        proxy.When = static args => ((IEnumerable<PlanInput>)args[4]!).Any(static input => input.Item == "x");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal) { ["tm ah/release-x"] = start };
        var heard = new List<string?>();

        _ = HierarchyModule.ChecklistOnReleaseTips(
            progress, ChecklistPool, ["tm"], new Dictionary<string, string>(StringComparer.Ordinal) { ["ah/release-x"] = work }, seen,
            DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static _ => null, (rev, commit) => Quiet(repo, ["merge-base", "--is-ancestor", rev, commit]) is not null, (_, problem) => heard.Add(problem));

        Assert.Equal("done", StateOf(ChecklistPool, "y"));
        Assert.Equal("in_review", StateOf(ChecklistPool, "x"));
        Assert.Equal(start, seen["tm ah/release-x"]);
        Assert.Equal(["disk full"], heard);
    }

    [Fact]
    public void ARefusalLogsOncePerChangeOfTextAndNegativeASuccessClearsIt()
    {
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = new List<string>();
        var report = ReleaseWatch.LogChanges(last, lines.Add);

        report("a", "disk full");
        report("a", "disk full");
        report("a", "no space");
        report("a", "disk full");
        report("b", "disk full");
        report("a", null);
        Assert.False(last.ContainsKey("a"));
        report("a", "disk full");

        Assert.Equal(["a: disk full", "a: no space", "a: disk full", "b: disk full", "a: disk full"], lines);
    }

    [Fact]
    public void TheSeenFileKeepsEachTeamsTipAndNegativeAnOlderLineIsABaselineNotAMove()
    {
        const string release = "refs/remotes/origin/ah/release-x";
        var (repo, start, work, _, _) = RefusalRig();
        _ = InRepo(repo, "update-ref", release, work);
        ReportedItem("x", work, start, "ah/x");
        var file = Path.Combine(ChecklistPool, "release-watch.seen");
        File.WriteAllLines(file, ["ah/release-x " + start]);

        ReleaseWatch.Tick(_graph.Hierarchy.Progress, ChecklistPool, ["tm"], args => Quiet(repo, args), static _ => null, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static (_, problem) => Assert.Null(problem));

        // a dropped line is a first sight: no ship row, and a reviewed item the tip holds closes
        Assert.Equal("done", StateOf(ChecklistPool, "x"));
        Assert.DoesNotContain(_graph.Hierarchy.Progress.ReadLedger(ChecklistPool, "tm"), static r => r.Member("kind")?.AsString() == "ship");
        // b435: the tips go to the state database; the old file is read only while the database holds none, and never written
        Assert.Equal("tm ah/release-x " + work, _graph.Hierarchy.Progress.ReleaseSeen(ChecklistPool));
        Assert.Equal(["ah/release-x " + start], File.ReadAllLines(file));
        File.WriteAllLines(file, ["tm ah/release-x " + start]);
        ReleaseWatch.Tick(_graph.Hierarchy.Progress, ChecklistPool, ["tm"], args => Quiet(repo, args), static _ => null, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static (_, problem) => Assert.Null(problem));
        Assert.DoesNotContain(_graph.Hierarchy.Progress.ReadLedger(ChecklistPool, "tm"), static r => r.Member("kind")?.AsString() == "ship");
    }

    [Fact]
    public void AnUnreadableDatabaseSkipsTheTickAndNegativeNeverFallsBackToTheOldSeenFile()
    {
        const string release = "refs/remotes/origin/ah/release-x";
        var (repo, start, work, _, _) = RefusalRig();
        _ = InRepo(repo, "update-ref", release, work);
        ReportedItem("x", work, start, "ah/x");
        void Tick() => ReleaseWatch.Tick(_graph.Hierarchy.Progress, ChecklistPool, ["tm"], args => Quiet(repo, args), static _ => null, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static (_, problem) => Assert.Null(problem));
        Tick();
        var file = Path.Combine(ChecklistPool, "release-watch.seen");
        File.WriteAllLines(file, ["tm ah/release-x " + start]);
        var db = Path.Combine(_sandbox.AhHome, "state", "ah.db");
        var mode = File.GetUnixFileMode(db);
        File.SetUnixFileMode(db, UnixFileMode.None);
        try
        {
            // b439: the stale file would read as a move from start to work; an unreadable database is an error, never "no tips yet"
            Assert.ThrowsAny<Exception>(() => _graph.Hierarchy.Progress.ReleaseSeen(ChecklistPool));
            Assert.ThrowsAny<Exception>(Tick);
        }
        finally
        {
            File.SetUnixFileMode(db, mode);
        }

        Assert.DoesNotContain(_graph.Hierarchy.Progress.ReadLedger(ChecklistPool, "tm"), static r => r.Member("kind")?.AsString() == "ship");
        Assert.Equal("tm ah/release-x " + work, _graph.Hierarchy.Progress.ReleaseSeen(ChecklistPool));
    }

    [Theory]
    [InlineData("STATUS still going", false)]
    [InlineData("DONE s\n- built", false)]
    [InlineData("VERDICT PASS s@abc1234", true)]
    public void NegativeNoMemberReplyMovesAnItemWaitingOnTheUser(string text, bool reviewer)
    {
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput(linked, "wait-user", Note: "Which?")], "o").Refused);

        var problem = HierarchyModule.ChecklistOnMemberReply(_graph.Hierarchy.Progress, ChecklistPool, "tm", ExchangeId, linked, "m", reviewer ? "reviewer" : "implementor", text, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), "s", static _ => "s");

        Assert.Null(problem);
        Assert.Equal("waiting_user", StateOf(ChecklistPool, linked));
    }

    [Fact]
    public void AnItemWithNoReportedShaClosesWhenTheMoveCarriesItsBranchAndNegativeAnEmptyBranchDoesNot()
    {
        const string release = "refs/remotes/origin/ah/release-x";
        var repo = Path.Combine(_sandbox.Root, "repo");
        Directory.CreateDirectory(repo);
        _ = InRepo(repo, "init", "-q", "-b", "trunk");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "start");
        var start = InRepo(repo, "rev-parse", "HEAD");
        _ = InRepo(repo, "update-ref", release, start);
        void Tick() => ReleaseWatch.Tick(_graph.Hierarchy.Progress, ChecklistPool, ["tm"], args => Quiet(repo, args), static id => id[^1..], DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static (_, problem) => Assert.Null(problem));
        void Item(string item, string slug, params PlanInput[] more) =>
            Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput(item, "add", item), new PlanInput(item, "start"), new PlanInput(item, "ref", Ref: "20261022-110000-" + slug), .. more], "o").Refused);
        Tick();

        _ = InRepo(repo, "branch", "ah/e", start);
        Item("empty", "e");
        _ = InRepo(repo, "switch", "-q", "-c", "ah/z");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "z work");
        Item("landed", "z");
        Item("reported", "z", new PlanInput("reported", "review"));
        Item("elsewhere", "w");
        _ = InRepo(repo, "update-ref", release, InRepo(repo, "rev-parse", "HEAD"));
        Tick();

        Assert.Equal("in_progress", StateOf(ChecklistPool, "empty"));
        Assert.Equal("done", StateOf(ChecklistPool, "landed"));
        Assert.Equal("done", StateOf(ChecklistPool, "reported"));
        Assert.Equal("in_progress", StateOf(ChecklistPool, "elsewhere"));
    }

    [Fact]
    public void AShipCarryingTheCommitABranchWasCutFromLeavesItsItemOpenAndNegativeABranchWithItsOwnCommitCloses()
    {
        const string release = "refs/remotes/origin/ah/release-x";
        var repo = Path.Combine(_sandbox.Root, "repo");
        Directory.CreateDirectory(repo);
        _ = InRepo(repo, "init", "-q", "-b", "trunk");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "start");
        var start = InRepo(repo, "rev-parse", "HEAD");
        _ = InRepo(repo, "update-ref", release, start);
        void Tick() => ReleaseWatch.Tick(_graph.Hierarchy.Progress, ChecklistPool, ["tm"], args => Quiet(repo, args), static id => id[^1..], DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), static (_, problem) => Assert.Null(problem));
        void Item(string item, string slug, string baseSha) =>
            Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(ChecklistPool, "tm", "_run", "orchestrator@tm", [new PlanInput(item, "add", item), new PlanInput(item, "start"), new PlanInput(item, "ref", Ref: "20261022-110000-" + slug) { Base = baseSha }], "o").Refused);
        Tick();

        // the ship commit exists before the release ref moves: a branch is cut from it, and its member has not committed yet
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "ship merge");
        var ship = InRepo(repo, "rev-parse", "HEAD");
        _ = InRepo(repo, "branch", "ah/c", ship);
        Item("cut", "c", ship);
        _ = InRepo(repo, "switch", "-q", "-c", "ah/m", start);
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "m work");
        var work = InRepo(repo, "rev-parse", "HEAD");
        Item("moved", "m", start);
        _ = InRepo(repo, "switch", "-q", "trunk");
        _ = InRepo(repo, "merge", "-q", "--no-edit", work);
        _ = InRepo(repo, "update-ref", release, InRepo(repo, "rev-parse", "HEAD"));
        Tick();

        Assert.Equal("in_progress", StateOf(ChecklistPool, "cut"));
        Assert.Equal("done", StateOf(ChecklistPool, "moved"));
    }

    /// <summary>A repository at the sandbox root that ignores the sandbox's own files, so its tree is clean. Returns the first commit.</summary>
    private string CleanRepo()
    {
        ((FakeRunner)_graph.Runner).GitPassthrough = PlatformModule.CreateProcessRunner();
        _ = InRepo(_sandbox.Root, "init", "-q", "-b", "trunk");
        File.WriteAllText(Path.Combine(_sandbox.Root, ".git", "info", "exclude"), "*\n");
        _ = InRepo(_sandbox.Root, "commit", "-q", "--allow-empty", "-m", "start");
        return InRepo(_sandbox.Root, "rev-parse", "HEAD");
    }

    [Fact]
    public void ARealDoneReportFromACleanTreeRecordsHeadAndNotItsBranchAndItsAckLeavesItInReview()
    {
        var start = CleanRepo();
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;
        _ = InRepo(_sandbox.Root, "commit", "-q", "--allow-empty", "-m", "work");

        FileResponse("DONE s\n- built");
        var item = Assert.Single(RunItems());
        Assert.Equal(new[] { InRepo(_sandbox.Root, "rev-parse", "HEAD") }, item.Commits);
        Assert.Null(item.Branch);
        Assert.Equal(start, item.Base);
        AsOrchestrator();

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.Equal("reported", StateOf(ChecklistPool, linked));
    }

    [Fact]
    public void AnImplementorsCleanDoneAtTheBaseRecordsNoShaAndItsAckLeavesItReportedAndNegativeANewDispatchIsNotRefused()
    {
        _ = CleanRepo();
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;

        FileResponse("DONE s\n- nothing committed");
        Assert.Empty(Assert.Single(RunItems()).Commits);
        AsOrchestrator();
        var (code, err) = Run("ack", ExchangeId, "--session", "o");
        Assert.True(code == 0, err);
        Assert.Equal("reported", StateOf(ChecklistPool, linked));

        var (next, nextErr) = Run("new", "--type", "request", "--to", "implementor", "--from", "orchestrator", "--team", "tm", "--slug", "s2", "--title", "Fix test titles", "--id", "20261022-120009-bbbb", "--item", linked, "--session", "o", "--body-file", BodyFile("build it"));
        Assert.True(next == 0, nextErr);
    }

    [Theory]
    [InlineData("untracked")]
    [InlineData("corrupt")]
    public void ADoneFromADirtyTreeOrAFailedStatusRecordsNoShaAndAnAckLeavesItReported(string how)
    {
        _ = CleanRepo();
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;
        _ = InRepo(_sandbox.Root, "commit", "-q", "--allow-empty", "-m", "work");
        if (how == "untracked")
        {
            File.WriteAllText(Path.Combine(_sandbox.Root, ".git", "info", "exclude"), "*\n!stray.txt\n");
            File.WriteAllText(Path.Combine(_sandbox.Root, "stray.txt"), "x");
        }
        else
        {
            File.WriteAllText(Path.Combine(_sandbox.Root, ".git", "index"), "not an index");
        }

        FileResponse("DONE s\n- built");

        Assert.Empty(Assert.Single(RunItems()).Commits);
        Assert.Equal("reported", StateOf(ChecklistPool, linked));
        File.Delete(Path.Combine(_sandbox.Root, ".git", "index"));
        AsOrchestrator();
        var (code, err) = Run("ack", ExchangeId, "--session", "o");
        Assert.True(code == 0, err);
        Assert.Equal("reported", StateOf(ChecklistPool, linked));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("fill")]
    public void AnExplicitShaIsRecordedFromADirtyTree(string verb)
    {
        _ = CleanRepo();
        OpenExchange();
        _ = InRepo(_sandbox.Root, "commit", "-q", "--allow-empty", "-m", "work");
        var work = InRepo(_sandbox.Root, "rev-parse", "HEAD");
        File.WriteAllText(Path.Combine(_sandbox.Root, ".git", "info", "exclude"), "*\n!stray.txt\n");
        File.WriteAllText(Path.Combine(_sandbox.Root, "stray.txt"), "x");
        AsMember();

        var (code, err) = verb == "new"
            ? Run("new", "--type", "response", "--id", ExchangeId, "--team", "tm", "--from", "implementor", "--session", "s-impl", "--sha", work, "--body-file", BodyFile("DONE s\n- built"))
            : Run("fill", "--id", ExchangeId, "--from", "implementor", "--session", "s-impl", "--sha", work, "--body-file", BodyFile("DONE s\n- built"));

        Assert.True(code == 0, err);
        Assert.Equal(new[] { work }, Assert.Single(RunItems()).Commits);
    }

    [Fact]
    public void NegativeAnUnresolvedShaFromADirtyTreeRecordsNone()
    {
        _ = CleanRepo();
        OpenExchange();
        _ = InRepo(_sandbox.Root, "commit", "-q", "--allow-empty", "-m", "work");
        File.WriteAllText(Path.Combine(_sandbox.Root, ".git", "info", "exclude"), "*\n!stray.txt\n");
        File.WriteAllText(Path.Combine(_sandbox.Root, "stray.txt"), "x");
        AsMember();

        var (code, err) = Run("new", "--type", "response", "--id", ExchangeId, "--team", "tm", "--from", "implementor", "--session", "s-impl", "--sha", "0000000", "--body-file", BodyFile("DONE s\n- built"));

        Assert.True(code == 0, err);
        Assert.Empty(Assert.Single(RunItems()).Commits);
    }

    [Theory]
    [InlineData("VERDICT FAIL s@abc1234", "in_progress")]
    [InlineData("VERDICT PASS s@abc1234", "done")]
    public void TheWriterTakesAReviewersVerdictOnAnItemStillInProgress(string text, string state)
    {
        OpenExchange();
        var linked = Assert.Single(RunItems()).Item;

        var problem = HierarchyModule.ChecklistOnMemberReply(_graph.Hierarchy.Progress, ChecklistPool, "tm", ExchangeId, linked, "tm-reviewer", "reviewer", text, DateTimeOffset.Parse("2026-11-02T12:00:00Z", CultureInfo.InvariantCulture), "s", static _ => "s");

        Assert.Null(problem);
        Assert.Equal(state, StateOf(ChecklistPool, linked));
    }

    [Fact]
    public void AConfiguredReleaseRefIsTheOnlyOneWatchedAndNegativeAnUnsetOneWatchesTheConventionalOnes()
    {
        var repo = Path.Combine(_sandbox.Root, "repo2");
        Directory.CreateDirectory(repo);
        _ = InRepo(repo, "init", "-q", "-b", "trunk");
        _ = InRepo(repo, "commit", "-q", "--allow-empty", "-m", "start");
        var sha = InRepo(repo, "rev-parse", "HEAD");
        _ = InRepo(repo, "update-ref", "refs/remotes/origin/ah/release-a", sha);
        _ = InRepo(repo, "update-ref", "refs/remotes/origin/ah/other", sha);
        _ = InRepo(repo, "update-ref", "refs/remotes/origin/main", sha);
        _ = InRepo(repo, "symbolic-ref", "refs/remotes/origin/HEAD", "refs/remotes/origin/main");
        _ = InRepo(repo, "update-ref", "refs/remotes/origin/line", sha);

        Assert.Equal(["ah/release-a", "main"], ReleaseWatch.Tips(args => Quiet(repo, args)).Keys.Order(StringComparer.Ordinal));

        _ = InRepo(repo, "config", "ah.releaseRef", "origin/line");
        Assert.Equal(["line"], ReleaseWatch.Tips(args => Quiet(repo, args)).Keys);
    }

    [Fact]
    public void NegativeAMembersAckOfADoneResponseLeavesTheLinkedItemWhereItWas()
    {
        const string id = "20261022-120004-eeee";
        AsMember();
        var (opened, openErr) = Run("new", "--type", "request", "--to", "orchestrator", "--from", "implementor", "--team", "tm", "--slug", "q", "--title", "Fix test titles", "--id", id, "--session", "s-impl", "--body-file", BodyFile("what now"));
        Assert.True(opened == 0, openErr);
        var pool = LinkItem("q", id, "orchestrator");
        AsOrchestrator();
        var (answered, answerErr) = Run("new", "--type", "response", "--id", id, "--team", "tm", "--from", "orchestrator", "--session", "o", "--body-file", BodyFile("DONE q\n- answer"));
        Assert.True(answered == 0, answerErr);
        var before = StateOf(pool, "q");

        var (code, err) = Run("ack", id, "--session", "s-impl");

        Assert.True(code == 0, err);
        Assert.Equal(before, StateOf(pool, "q"));
        Assert.NotEqual("done", StateOf(pool, "q"));
    }

    [Fact]
    public void AckOfADispatchedIdRecordsTheOrchestratorAckForTheRequestsOwnTeamOnly()
    {
        OpenExchange();
        _graph.Hierarchy.Peers.AppendDispatchRecord("o", ExchangeId, "implementor");

        var (code, err) = Run("ack", ExchangeId, "--session", "o");

        Assert.True(code == 0, err);
        Assert.True(_graph.Hierarchy.Peers.ReportAckedFor("a-resumed-session", ["tm"])(ExchangeId));
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("a-resumed-session", ["other"])(ExchangeId));
    }

    [Fact]
    public void AckOfADispatchedIdWithNoRequestOnFileWritesOnlyTheSessionsOwnRow()
    {
        AsOrchestrator();
        _graph.Hierarchy.Peers.AppendDispatchRecord("o", "20260101-000000-t7ee", "implementor");

        var (code, err) = Run("ack", "20260101-000000-t7ee", "--session", "o");

        Assert.True(code == 0, err);
        Assert.True(_graph.Hierarchy.Peers.ReportAckedFor("o")("20260101-000000-t7ee"));
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("a-resumed-session", ["tm"])("20260101-000000-t7ee"));
    }

    [Fact]
    public void AckOfSeveralIdsGivesOneLineEachAndAFailureOnOneNeverStopsTheOthers()
    {
        OpenExchange();
        FileResponse("DONE s\n- built");
        AsOrchestrator();

        var (code, _) = Run("ack", ExchangeId, "20261022-999999-zzzz", "20261022-999998-yyyy", "--session", "o");

        Assert.Equal(1, code);
        Assert.Contains($"acked {ExchangeId} (exchange)", _lastOut, StringComparison.Ordinal);
        Assert.Contains("unknown 20261022-999999-zzzz", _lastOut, StringComparison.Ordinal);
        Assert.Contains("unknown 20261022-999998-yyyy", _lastOut, StringComparison.Ordinal);
        Assert.True(_graph.Hierarchy.Peers.ReportAckedFor("another-session", ["tm"])(ExchangeId));
    }

    [Fact]
    public void AnOrchestratorAckCountsFromAnySessionOfThatTeamButNotAMembersRowOrAnotherTeamOrAnUnscopedRead()
    {
        _graph.Hierarchy.Peers.AppendOrchestratorAck("s-old", "r1", "tm");
        _graph.Hierarchy.Peers.AppendReportRecord("report-acked", "s-member", "r2");

        var scoped = _graph.Hierarchy.Peers.ReportAckedFor("s-new", ["tm"]);

        Assert.True(scoped("r1"));
        Assert.False(scoped("r2"));
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("s-new", ["other"])("r1"));
        Assert.False(_graph.Hierarchy.Peers.ReportAckedFor("s-new")("r1"));
        Assert.True(_graph.Hierarchy.Peers.ReportAckedFor("s-member")("r2"));
    }

    [Fact]
    public void AMemberMayReadItsOwnBoxByAddressButNotTheOrchestrators()
    {
        AsMember();

        var (refused, why) = Run("inbox", "--address", "orchestrator@tm");
        var (own, ownErr) = Run("inbox", "--address", "tm-implementor@tm");

        Assert.Equal(2, refused);
        Assert.Contains("not_your_session", why, StringComparison.Ordinal);
        Assert.True(own == 0, ownErr);
    }

    [Fact]
    public void TheOrchestratorMayReadAMembersBoxByAddress()
    {
        AsOrchestrator();

        var (exit, err) = Run("inbox", "--address", "tm-implementor@tm");

        Assert.True(exit == 0, err);
    }

    [Fact]
    public void NoProvenSessionMayReadABoxByAddress()
    {
        var (exit, why) = Run("inbox", "--address", "tm-implementor@tm");

        Assert.Equal(2, exit);
        Assert.Contains("not_your_session", why, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberMayWriteAMessageFromItsOwnRoleButNotFromTheOrchestrator()
    {
        AsMember();

        var (refused, why) = Run("new", "--type", "request", "--to", "orchestrator", "--from", "orchestrator", "--slug", "s", "--title", "Fix test titles", "--id", "20261016-130001-abcd");
        var (role, roleErr) = Run("new", "--type", "request", "--to", "orchestrator", "--from", "implementor", "--slug", "s", "--id", "20261016-130002-abcd");

        Assert.Equal(2, refused);
        Assert.Contains("not_your_session: --from", why, StringComparison.Ordinal);
        Assert.True(role == 0, roleErr);
    }

    [Fact]
    public void AMemberMaySetItsOwnRouteButNotTheOrchestratorsWhileTheOrchestratorMaySetItsMembers()
    {
        AsMember();
        var (refused, why) = Run("route", "--session", "o", "peers");
        var (own, ownErr) = Run("route", "--session", "s-impl", "peers");

        Assert.True(_graph.Writer.Invoke(
            "mailbox",
            "unregister",
            JsonText.ToUtf8(new JsonObject { ["session_id"] = "s-impl" }, 0),
            new RequestContext(_sandbox.Root, _sandbox.Root, string.Empty, string.Empty)).Ok);
        Assert.True(_graph.Mailbox.WriteRegistration(new RegistrationWrite(
            "s-impl", [new BoxRef("tm-implementor@tm", _graph.Mailbox.BoxDirFor(_teamFile, "tm-implementor"))], _teamFile, "claude", 999_999)));
        AsOrchestrator();
        var (owner, ownerErr) = Run("route", "--session", "s-impl", "peers");

        Assert.Equal(2, refused);
        Assert.Contains("not_your_session", why, StringComparison.Ordinal);
        Assert.True(own == 0, ownErr);
        Assert.True(owner == 0, ownerErr);
    }
}
