using System.Globalization;
using System.Text.Json;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs.Msg;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// <c>ah msg withdraw</c> on an exchange no mailbox holds: the request's sender writes a response beside the request that is marked as a
/// withdrawal, so the exchange reads as closed; every refusal exits non-zero with nothing written.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class MsgWithdrawExchangeTests : IDisposable
{
    private const string Id = "20261016-120000-ab12";

    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly string _proj;
    private readonly string _main;
    private readonly string _teamFile;
    private readonly CliContext _context;
    private readonly ServiceGraph _graph;

    public MsgWithdrawExchangeTests()
    {
        _proj = Path.Combine(_sandbox.Root, "proj");
        _main = Path.Combine(_proj, ".claude", "ah2");
        _teamFile = Path.Combine(_main, "teams", "tm.json");
        Directory.CreateDirectory(Path.Combine(_proj, ".git"));
        Directory.CreateDirectory(Path.Combine(_main, "msgs"));
        WriteTeam(string.Empty);
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        _graph = ServiceGraph.Build(io, env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", _teamFile, "orchestrator@tm");
        _context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
    }

    public void Dispose() => _sandbox.Dispose();

    private void WriteTeam(string implementorRoot)
    {
        _sandbox.PlantTeam(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":["
            + "{\"role\":\"reviewer\",\"name\":\"tm-reviewer\",\"kind\":\"claude\",\"transport_id\":\"p1\"},"
            + "{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p2\""
            + (implementorRoot.Length == 0 ? string.Empty : ",\"expected_root\":\"" + implementorRoot + "\"") + "}]}\n");
    }

    private (int Exit, string Out, string Err) Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = _sandbox.Mirrored(() => MsgVerb.Run(_context, [.. args, "--orchestrator-pid", System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture), "--team", "tm", "--cwd", _proj], stdout, stderr));
        return (exit, stdout.ToString(), stderr.ToString());
    }

    private (int Exit, string Out, string Err) Withdraw(string id, params string[] extra) => Run(["withdraw", id, "--json", .. extra]);

    /// <summary>A request in <paramref name="pool"/>, from <paramref name="from"/> (a role) and <paramref name="fromName"/>.</summary>
    private static string Request(string pool, string id = Id, string from = "orchestrator", string fromName = "null", string team = "tm", string to = "implementor", string slug = "t1")
    {
        Directory.CreateDirectory(Path.Combine(pool, "msgs"));
        var path = Path.Combine(pool, "msgs", $"{id}--{to}--{slug}--request.md");
        File.WriteAllText(
            path,
            $"---\nid: {id}\ntype: request\nto: {to}\nfrom: {from}\nslug: {slug}\nparent: null\nreason: null\neta: small\nto_name: tm-implementor\nfrom_name: {fromName}\nteam: {team}\ncreated: 2026-10-16T12:00:00+00:00\n---\n\n## [0] tldr\n- x\n");
        return path;
    }

    private static string[] ResponsesIn(string pool, string id = Id) =>
        Directory.Exists(Path.Combine(pool, "msgs")) ? [.. Directory.GetFiles(Path.Combine(pool, "msgs"), $"{id}--*--response.md")] : [];

    /// <summary>ah-msgs-fast S8: --limit keeps the newest N rows and says how many more; 0 lists all; a non-number is refused.</summary>
    [Fact]
    public void ListAllKeepsTheNewestLimitRowsAndCountsTheRest()
    {
        _ = Request(_main, "20261016-120000-aa01");
        _ = Request(_main, "20261016-120000-aa02");
        _ = Request(_main, "20261016-120000-aa03");

        var (exit, stdout, _) = Run("list", "--all", "--limit", "2", "--plain");
        var all = Run("list", "--all", "--limit", "0", "--plain").Out;

        Assert.Equal(0, exit);
        var lines = stdout.Trim().Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("20261016-120000-aa03", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("20261016-120000-aa02", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("1 more", lines[2], StringComparison.Ordinal);
        Assert.Equal(3, all.Trim().Split('\n').Length);
        Assert.DoesNotContain("more", all, StringComparison.Ordinal);
        Assert.NotEqual(0, Run("list", "--all", "--limit", "x", "--plain").Exit);
    }

    /// <summary>delta-status R1: --slug --count counts every exchange of the slug, open and closed, and only that slug; --slug alone lists them.</summary>
    [Fact]
    public void ListSlugCountCountsOpenAndClosedExchangesOfThatSlugOnly()
    {
        _ = ClosedSkeleton();
        _ = Request(_main, "20261016-120000-aa01");
        _ = Request(_main, "20261016-120000-aa02", slug: "t2");
        _ = Request(_main, "20261016-120000-aa03", slug: "dec-t1");

        var (exit, stdout, _) = Run("list", "--slug", "t1", "--count");
        var listed = Run("list", "--slug", "t1", "--all", "--limit", "0", "--plain").Out;

        Assert.Equal(0, exit);
        Assert.Equal("{\"slug\":\"t1\",\"count\":2}", stdout.Trim());
        Assert.Equal(2, listed.Trim().Split('\n').Length);
        Assert.DoesNotContain("aa02", listed, StringComparison.Ordinal);
        Assert.Equal("{\"slug\":\"t2\",\"count\":1}", Run("list", "--slug", "t2", "--count").Out.Trim());
    }

    /// <summary>delta-status R1 negatives: a slug with no exchange counts 0; a dec- slug counts no rounds even when it has exchanges.</summary>
    [Fact]
    public void NegativeListSlugCountOfAnUnknownSlugOrADecSlugIsZero()
    {
        _ = Request(_main, "20261016-120000-aa01");
        _ = Request(_main, "20261016-120000-aa03", slug: "dec-t1");

        Assert.Equal("{\"slug\":\"nope\",\"count\":0}", Run("list", "--slug", "nope", "--count").Out.Trim());
        Assert.Equal("{\"slug\":\"dec-t1\",\"count\":0}", Run("list", "--slug", "dec-t1", "--count").Out.Trim());
    }

    [Fact]
    public void TheSenderWithdrawsAnExchangeAndItReadsClosedWithAMarkedResponseBesideTheRequest()
    {
        var request = Request(_main);
        var before = File.ReadAllText(request);

        var (exit, stdout, stderr) = Withdraw(Id, "--reason", "superseded by r2");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        var result = JsonText.Parse(stdout).AsObject();
        Assert.True(result["withdrawn"].AsBool());
        var response = Assert.Single(ResponsesIn(_main));
        Assert.Equal(MsgRows.PathKey(result["response"].AsString()), MsgRows.PathKey(response));
        var text = File.ReadAllText(response);
        Assert.Contains("\nfrom: orchestrator\n", text, StringComparison.Ordinal);
        Assert.Contains("\nreason: superseded by r2\n", text, StringComparison.Ordinal);
        Assert.Contains("\nwithdrawn: true\n", text, StringComparison.Ordinal);
        Assert.Contains("\nWITHDRAWN by orchestrator: superseded by r2\n", text, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(request));

        var open = JsonText.Parse(Run("list", "--json").Out).AsArray();
        Assert.DoesNotContain(open, row => row.Member("id")!.Value.AsString() == Id);
        var closed = Assert.Single(JsonText.Parse(Run("list", "--closed", "--json").Out).AsArray(), row => row.Member("id")!.Value.AsString() == Id);
        Assert.Equal("closed", closed.Member("state")!.Value.AsString());
    }

    [Fact]
    public void AWithdrawalIsNoReportLandedButAnOrdinaryReplyIs()
    {
        _ = Request(_main);
        Assert.Equal(0, Withdraw(Id, "--reason", "r").Exit);
        var withdrawal = Assert.Single(ResponsesIn(_main));
        var later = (double)DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
        Assert.False(_sandbox.Mirrored(() => _graph.Hierarchy.Messages.ResponseLanded(withdrawal, later)), "the watch must not report a withdrawal as the addressee's landed report");

        var other = "20261016-120000-cd34";
        _ = Request(_main, other);
        var reply = Path.Combine(_main, "msgs", $"{other}--orchestrator--t1--response.md");
        File.WriteAllText(reply, $"---\nid: {other}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: tm-implementor\nteam: tm\ncreated: 2026-10-16T13:00:00+00:00\n---\n\nbuilt it\n");
        Assert.True(_sandbox.Mirrored(() => _graph.Hierarchy.Messages.ResponseLanded(reply, later)));
    }

    [Fact]
    public void TheWithdrawalIsFiledUnderTheNameAReplyWouldHaveSoEveryReaderOfTheResponseFindsIt()
    {
        _ = Request(_main);

        Assert.Equal(0, Withdraw(Id, "--reason", "r").Exit);

        Assert.Equal($"{Id}--orchestrator--t1--response.md", Path.GetFileName(Assert.Single(ResponsesIn(_main))));
    }

    [Fact]
    public void AReplyAfterAWithdrawalIsRefusedAndTheWithdrawalIsKept()
    {
        var request = Request(_main, to: "orchestrator");
        Assert.Equal(0, Withdraw(Id, "--reason", "r").Exit);
        var withdrawal = Assert.Single(ResponsesIn(_main));
        var written = File.ReadAllText(withdrawal);

        var reply = Path.Combine(_sandbox.Root, "reply.md");
        File.WriteAllText(reply, "built it\n");

        var (exit, _, stderr) = Run("new", "--type", "response", "--id", Id, "--req", request, "--from", "orchestrator", "--body-file", reply);

        Assert.NotEqual(0, exit);
        Assert.Contains("request-withdrawn", stderr, StringComparison.Ordinal);
        Assert.Equal(withdrawal, Assert.Single(ResponsesIn(_main)));
        Assert.Equal(written, File.ReadAllText(withdrawal));
    }

    [Fact]
    public void ARoleOnlySenderIsTheCallersOnlyInTheRequestsOwnTeam()
    {
        _ = Request(_main, team: "other");

        var (exit, _, stderr) = Withdraw(Id, "--reason", "r");

        Assert.Equal(2, exit);
        Assert.Contains("not_your_session", stderr, StringComparison.Ordinal);
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void ARoleOnlyRequestWithNoTeamIsWithdrawnByTheOrchestratorOfTheTeamWhosePoolHoldsIt()
    {
        _ = Request(_main, team: "null");

        var (exit, stdout, stderr) = Withdraw(Id, "--reason", "stranded");

        Assert.True(exit == 0, stderr);
        Assert.Equal("orchestrator@tm", JsonText.Parse(stdout).AsObject()["from"].AsString());
        Assert.Contains("\nWITHDRAWN by orchestrator@tm: stranded\n", File.ReadAllText(Assert.Single(ResponsesIn(_main))), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestFromANamedSenderThatIsGoneIsWithdrawnByTheOrchestratorOfTheTeamWhosePoolHoldsIt()
    {
        // the sender's lane team has no team record and no live session holds its name
        _ = Request(_main, from: "sub-orchestrator", fromName: "gone-lane-so", team: "gone-lane");

        var (exit, stdout, stderr) = Withdraw(Id, "--reason", "lane gone");

        Assert.True(exit == 0, stderr);
        Assert.Equal("orchestrator@tm", JsonText.Parse(stdout).AsObject()["from"].AsString());
    }

    [Fact]
    public void AnExchangeNoOneWithdrewStaysOpen()
    {
        _ = Request(_main);

        var open = JsonText.Parse(Run("list", "--json").Out).AsArray();

        Assert.Contains(open, row => row.Member("id")!.Value.AsString() == Id);
    }

    [Fact]
    public void ANonSenderIsRefusedWithNotYourSessionAndNothingIsWritten()
    {
        _ = Request(_main, from: "implementor", fromName: "tm-implementor");

        var (exit, _, stderr) = Withdraw(Id, "--reason", "r");

        Assert.Equal(2, exit);
        Assert.Contains("not_your_session", stderr, StringComparison.Ordinal);
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void ARequestThatNamesNoSenderIsRefused()
    {
        _ = Request(_main, from: "null");

        var (exit, _, stderr) = Withdraw(Id, "--reason", "r");

        Assert.Equal(2, exit);
        Assert.Contains("names no sender", stderr, StringComparison.Ordinal);
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void WithdrawingTwiceIsRefusedAndKeepsTheFirstResponse()
    {
        _ = Request(_main);
        Assert.Equal(0, Withdraw(Id, "--reason", "first").Exit);
        var first = Assert.Single(ResponsesIn(_main));
        var written = File.ReadAllText(first);

        var (exit, _, stderr) = Withdraw(Id, "--reason", "second");

        Assert.Equal(1, exit);
        Assert.Contains("already has a response", stderr, StringComparison.Ordinal);
        Assert.Equal(first, Assert.Single(ResponsesIn(_main)));
        Assert.Equal(written, File.ReadAllText(first));
    }

    [Fact]
    public void AnExchangeTheAddresseeAlreadyAnsweredIsRefused()
    {
        _ = Request(_main);
        var reply = Path.Combine(_main, "msgs", $"{Id}--orchestrator--t1--response.md");
        File.WriteAllText(reply, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: tm-implementor\nteam: tm\ncreated: 2026-10-16T13:00:00+00:00\n---\n\nbuilt it\n");

        var (exit, _, stderr) = Withdraw(Id, "--reason", "r");

        Assert.Equal(1, exit);
        Assert.Contains("already has a response", stderr, StringComparison.Ordinal);
        Assert.Equal(reply, Assert.Single(ResponsesIn(_main)));
        Assert.DoesNotContain("withdrawn", File.ReadAllText(reply), StringComparison.Ordinal);
    }

    [Fact]
    public void ToOnAnExchangeIsRefusedBecauseOnlyAMailedBriefCanBeReAddressed()
    {
        _ = Request(_main);

        var (exit, _, stderr) = Withdraw(Id, "--reason", "r", "--to", "tm-reviewer");

        Assert.Equal(2, exit);
        Assert.Contains("re-addresses a mailed brief", stderr, StringComparison.Ordinal);
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void AnIdNeitherAMailboxNorAnyPoolHoldsIsRefusedNamingBothPlaces()
    {
        var (exit, _, stderr) = Withdraw("20261016-120000-ffff", "--reason", "r");

        Assert.Equal(1, exit);
        Assert.Contains("no entry 20261016-120000-ffff in any mailbox", stderr, StringComparison.Ordinal);
        Assert.Contains("no exchange 20261016-120000-ffff in the", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestInAMembersWorkTreePoolIsFoundWhereListFindsIt()
    {
        var tree = Path.Combine(_sandbox.Root, "wt1");
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        var pool = Path.Combine(tree, ".claude", "ah2");
        _ = Request(pool);
        WriteTeam(tree);
        Assert.Contains(JsonText.Parse(Run("list", "--json").Out).AsArray(), row => row.Member("id")!.Value.AsString() == Id);

        var (exit, _, stderr) = Withdraw(Id, "--reason", "r");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        _ = Assert.Single(ResponsesIn(pool));
        Assert.Empty(ResponsesIn(_main));
    }

    private (int Exit, string Out, string Err) Close(string id, params string[] extra) => Run(["close", id, "--json", .. extra]);

    [Fact]
    public void TheOrchestratorClosesARequestNoMailboxHoldsAndItLeavesTheOpenList()
    {
        var request = Request(_main, from: "implementor", fromName: "tm-implementor", to: "reviewer");
        var before = File.ReadAllText(request);
        Assert.Contains(JsonText.Parse(Run("list", "--json").Out).AsArray(), row => row.Member("id")!.Value.AsString() == Id);

        var (exit, stdout, stderr) = Close(Id, "--reason", "abandoned", "--note", "the reviewer was dismissed");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        var result = JsonText.Parse(stdout).AsObject();
        Assert.True(result["closed"].AsBool());
        var response = Assert.Single(ResponsesIn(_main));
        Assert.Equal($"{Id}--implementor--t1--response.md", Path.GetFileName(response));
        var text = File.ReadAllText(response);
        Assert.Contains("\nclosed: true\n", text, StringComparison.Ordinal);
        Assert.Contains("\nreason: abandoned\n", text, StringComparison.Ordinal);
        Assert.Contains("\nclosed_by: orchestrator@tm", text, StringComparison.Ordinal);
        Assert.Contains("\nclosed_reason: abandoned\n", text, StringComparison.Ordinal);
        Assert.Contains("CLOSED by orchestrator@tm", text, StringComparison.Ordinal);
        Assert.Contains("(abandoned): the reviewer was dismissed", text, StringComparison.Ordinal);
        Assert.DoesNotContain("withdrawn: true", text, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(request));
        Assert.DoesNotContain(JsonText.Parse(Run("list", "--json").Out).AsArray(), row => row.Member("id")!.Value.AsString() == Id);
    }

    private void WriteLaneChild(string parent, string root = "")
    {
        _sandbox.PlantTeam(
            Path.Combine(_main, "teams", "tm-lane.json"),
            "{\"team_id\":\"t-lane\",\"parent\":{\"team\":\"" + parent + "\"},\"orchestrator\":{\"pid\":1,\"session_id\":\"lane\"},\"members\":["
            + "{\"role\":\"implementor\",\"name\":\"tm-lane-implementor\",\"kind\":\"claude\",\"transport_id\":\"p3\""
            + (root.Length == 0 ? string.Empty : ",\"expected_root\":\"" + root + "\"") + "}]}\n");
    }

    [Fact]
    public void TheTopOrchestratorClosesAnExchangeOfItsLaneChildTeamFiledInTheMainPool()
    {
        _ = Request(_main, team: "tm-lane", from: "sub-orchestrator", fromName: "tm-lane-so");
        WriteLaneChild("tm");

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("closed_by: orchestrator@tm", File.ReadAllText(Assert.Single(ResponsesIn(_main))), StringComparison.Ordinal);
    }

    [Fact]
    public void ATeamThatIsNotTheCallersLaneChildStaysRefused()
    {
        _ = Request(_main, team: "tm-lane", from: "sub-orchestrator", fromName: "tm-lane-so");
        WriteLaneChild("other");

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(2, exit);
        Assert.Contains("not_your_session", stderr, StringComparison.Ordinal);
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void ACloseFindsARequestFiledInALaneChildsWorkTreePool()
    {
        var tree = Path.Combine(_sandbox.Root, "wt-lane");
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        var pool = Path.Combine(tree, ".claude", "ah2");
        _ = Request(pool, team: "tm-lane", from: "sub-orchestrator", fromName: "tm-lane-so");
        WriteLaneChild("tm", tree);

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        _ = Assert.Single(ResponsesIn(pool));
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void ACloseDoesNotLookInTheWorkTreePoolOfATeamThatIsNotALaneChild()
    {
        var tree = Path.Combine(_sandbox.Root, "wt-lane");
        Directory.CreateDirectory(Path.Combine(tree, ".git"));
        var pool = Path.Combine(tree, ".claude", "ah2");
        _ = Request(pool, team: "tm-lane", from: "sub-orchestrator", fromName: "tm-lane-so");
        WriteLaneChild("other", tree);

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.NotEqual(0, exit);
        Assert.Contains("no exchange", stderr, StringComparison.Ordinal);
        Assert.Empty(ResponsesIn(pool));
    }

    [Fact]
    public void AWithdrawalCarriesNoClosedByOrClosedReason()
    {
        _ = Request(_main);
        Assert.Equal(0, Withdraw(Id, "--reason", "r").Exit);

        var text = File.ReadAllText(Assert.Single(ResponsesIn(_main)));

        Assert.DoesNotContain("closed_by:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("closed_reason:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("closed: true", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ACloseNeedsOneOfTheTwoReasons()
    {
        _ = Request(_main);

        Assert.Equal(2, Close(Id).Exit);
        Assert.Equal(2, Close(Id, "--reason", "because").Exit);
        Assert.Equal(2, Close(Id, "--reason", "withdrawn").Exit);
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void ACallerWhoIsNotTheOrchestratorOfTheRequestsTeamIsRefused()
    {
        _ = Request(_main, team: "other");
        WriteOtherTeam(System.Environment.ProcessId, string.Empty);

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(2, exit);
        Assert.Contains("not_your_session", stderr, StringComparison.Ordinal);
        Assert.Empty(ResponsesIn(_main));
    }

    /// <summary>Team <c>other</c>'s file, its Orchestrator at <paramref name="pid"/> (session <c>x</c>), with one implementor.</summary>
    private void WriteOtherTeam(int pid, string pidStart) =>
        _sandbox.PlantTeam(
            Path.Combine(_main, "teams", "other.json"),
            "{\"team_id\":\"t-other\",\"orchestrator\":{\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + ",\"pid_start\":\"" + pidStart
            + "\",\"session_id\":\"x\"},\"members\":[{\"role\":\"implementor\",\"name\":\"other-implementor\",\"kind\":\"claude\",\"transport_id\":\"p9\"}]}\n");

    [Fact]
    public void AnyLiveOrchestratorClosesARequestOfATeamWhoseFileIsGone()
    {
        _ = Request(_main, team: "other");

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("closed_by: orchestrator@tm", File.ReadAllText(Assert.Single(ResponsesIn(_main))), StringComparison.Ordinal);
    }

    [Fact]
    public void AnyLiveOrchestratorClosesARequestOfATeamWhoseOrchestratorIsDead()
    {
        _ = Request(_main, team: "other");
        WriteOtherTeam(int.MaxValue - 7, "Thu Jan  1 00:00:00 2026");

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("closed_by: orchestrator@tm", File.ReadAllText(Assert.Single(ResponsesIn(_main))), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestThatNamesNoTeamIsClosedAsTheCallersOwnTeamWhenTheCallerIsItsOrchestrator()
    {
        _ = Request(_main, team: "null");

        var (exit, _, _) = Close(Id, "--reason", "superseded");

        Assert.Equal(0, exit);
        Assert.Contains("closed_by: orchestrator@tm", File.ReadAllText(Assert.Single(ResponsesIn(_main))), StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestThatNamesNoTeamIsClosedUnderTheTeamTheOrchestratorNames()
    {
        _ = Request(_main, team: "null");

        var (exit, _, stderr) = Close(Id, "--reason", "superseded", "--team", "tm");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("closed_by: orchestrator@tm", File.ReadAllText(Assert.Single(ResponsesIn(_main))), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTeamFallbackNamesTheOwnerProofButTheRequestsOwnTeamStillWins()
    {
        _ = Request(_main, team: "other");
        WriteOtherTeam(System.Environment.ProcessId, string.Empty);

        var (exit, _, _) = Close(Id, "--reason", "superseded", "--team", "tm");

        Assert.NotEqual(0, exit);
        Assert.Empty(ResponsesIn(_main));
    }

    [Fact]
    public void ARequestThatAlreadyHasAReportIsRefusedAndItsReportIsKept()
    {
        _ = Request(_main);
        var reply = Path.Combine(_main, "msgs", $"{Id}--orchestrator--t1--response.md");
        File.WriteAllText(reply, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: tm-implementor\nteam: tm\ncreated: 2026-10-16T13:00:00+00:00\n---\n\nbuilt it\n");
        var written = File.ReadAllText(reply);

        var (exit, _, stderr) = Close(Id, "--reason", "superseded");

        Assert.Equal(1, exit);
        Assert.Contains("already_reported", stderr, StringComparison.Ordinal);
        Assert.Equal(reply, Assert.Single(ResponsesIn(_main)));
        Assert.Equal(written, File.ReadAllText(reply));
    }

    /// <summary>A running lane whose sub-orchestrator is <c>tm-implementor</c>, so the request to it is the lane's charter; <paramref name="stream"/> overrides the stream record.</summary>
    private string ChartersReport(string stream = "\"state\":\"open\"")
    {
        _sandbox.PlantTeam(
            _teamFile,
            "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + System.Environment.ProcessId.ToString(CultureInfo.InvariantCulture)
            + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\",\"session_id\":\"o\"},\"members\":["
            + "{\"role\":\"implementor\",\"name\":\"tm-implementor\",\"kind\":\"claude\",\"transport_id\":\"p2\"}],"
            + "\"streams\":{\"lane\":{" + stream + ",\"sub_orchestrator\":\"tm-implementor\",\"opened\":\"2026-10-01T00:00:00+00:00\"}}}\n");
        _ = Request(_main);
        var reply = Path.Combine(_main, "msgs", $"{Id}--orchestrator--t1--response.md");
        File.WriteAllText(reply, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: tm-implementor\nteam: tm\ncreated: 2026-10-16T12:30:00+00:00\n---\n\n## [0] tldr\n- Release: ready\n");
        File.SetLastWriteTimeUtc(reply, new DateTime(2026, 10, 16, 12, 30, 0, DateTimeKind.Utc));
        return reply;
    }

    [Fact]
    public void ARunningLanesCharterWhoseReportLandedCanBeClosedAndTheReportIsMarkedClosed()
    {
        var reply = ChartersReport();

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, stderr);
        Assert.Equal(reply, Assert.Single(ResponsesIn(_main)));
        var text = File.ReadAllText(reply);
        Assert.Contains("closed: true", text, StringComparison.Ordinal);
        Assert.Contains("closed_by: orchestrator@tm", text, StringComparison.Ordinal);
        Assert.Contains("closed_reason: abandoned", text, StringComparison.Ordinal);
        Assert.Contains("- Release: ready", text, StringComparison.Ordinal);
        Assert.True(JsonText.Parse(Close(Id, "--reason", "abandoned").Out).AsObject()["already"].AsBool());
    }

    [Fact]
    public void ARequestWhoseOnlyResponseIsAnEmptySkeletonIsRetiredByClose()
    {
        _ = Request(_main);
        var reply = Path.Combine(_main, "msgs", $"{Id}--orchestrator--t1--response.md");
        File.WriteAllText(reply, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: tm-implementor\nteam: tm\ncreated: 2026-10-16T13:00:00+00:00\n---\n\n");

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(0, exit);
        Assert.DoesNotContain("already_reported", stderr, StringComparison.Ordinal);
        var text = File.ReadAllText(reply);
        Assert.Contains("closed: true", text, StringComparison.Ordinal);
        Assert.Contains("closed_reason: abandoned", text, StringComparison.Ordinal);
    }

    private (string Request, string Reply) ClosedSkeleton(string to = "implementor")
    {
        var request = Request(_main, to: to);
        var reply = Path.Combine(_main, "msgs", $"{Id}--orchestrator--t1--response.md");
        File.WriteAllText(reply, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: tm-implementor\nteam: tm\ncreated: 2026-10-16T13:00:00+00:00\n---\n\n");
        Assert.Equal(0, Close(Id, "--reason", "abandoned").Exit);
        return (request, reply);
    }

    [Fact]
    public void NegativeAFillIntoAResponseTheCloseRetiredIsRefusedAsRequestClosedAndLeavesTheFileAlone()
    {
        var (request, reply) = ClosedSkeleton();
        var before = File.ReadAllText(reply);

        var (exit, _, stderr) = Run("fill", "--id", Id, "--from", "orchestrator", "--req", request, "--body", "\"built it\"");

        Assert.NotEqual(0, exit);
        Assert.Contains("request-closed", stderr, StringComparison.Ordinal);
        Assert.Contains("your result was not recorded", stderr, StringComparison.Ordinal);
        Assert.Contains("orchestrator@tm", stderr, StringComparison.Ordinal);
        Assert.Contains("abandoned", stderr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(reply));
    }

    [Fact]
    public void NegativeANewResponseForAClosedRequestIsRefusedAsRequestClosedAndLeavesTheFileAlone()
    {
        var (request, reply) = ClosedSkeleton("orchestrator");
        var before = File.ReadAllText(reply);

        var (exit, _, stderr) = Run("new", "--type", "response", "--id", Id, "--from", "orchestrator", "--req", request);

        Assert.NotEqual(0, exit);
        Assert.Contains("request-closed", stderr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(reply));
    }

    private (string Request, string Reply) WithdrawnSkeleton(string to)
    {
        var request = Request(_main, to: to);
        var (exit, _, stderr) = Withdraw(Id, "--reason", "superseded");
        Assert.True(exit == 0, stderr);
        var reply = Assert.Single(ResponsesIn(_main));
        Assert.Contains("withdrawn: true", File.ReadAllText(reply), StringComparison.Ordinal);
        return (request, reply);
    }

    [Fact]
    public void NegativeAFillIntoAResponseOfAWithdrawnRequestIsRefusedAsRequestWithdrawnAndLeavesTheFileAlone()
    {
        var (request, reply) = WithdrawnSkeleton("implementor");
        var before = File.ReadAllText(reply);

        var (exit, _, stderr) = Run("fill", "--id", Id, "--from", "orchestrator", "--req", request, "--body", "\"built it\"");

        Assert.NotEqual(0, exit);
        Assert.Contains("request-withdrawn", stderr, StringComparison.Ordinal);
        Assert.Contains("was withdrawn by", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("request-closed", stderr, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(reply));
    }

    [Fact]
    public void AFillIntoTheSkeletonOfARequestThatIsStillOpenFillsAsToday()
    {
        var request = Request(_main);
        var reply = Path.Combine(_main, "msgs", $"{Id}--orchestrator--t1--response.md");
        File.WriteAllText(reply, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: tm-implementor\nteam: tm\ncreated: 2026-10-16T13:00:00+00:00\n---\n\n");

        var (exit, _, stderr) = Run("fill", "--id", Id, "--from", "orchestrator", "--req", request, "--body", "\"built it\"");

        Assert.True(exit == 0, stderr);
        Assert.Contains("built it", File.ReadAllText(reply), StringComparison.Ordinal);
    }

    [Fact]
    public void AReportedRequestOfAFinishedLaneIsStillRefusedAsAlreadyReported()
    {
        var reply = ChartersReport("\"state\":\"done\"");
        var written = File.ReadAllText(reply);

        var (exit, _, stderr) = Close(Id, "--reason", "abandoned");

        Assert.Equal(1, exit);
        Assert.Contains("already_reported", stderr, StringComparison.Ordinal);
        Assert.Equal(written, File.ReadAllText(reply));
    }

    [Fact]
    public void ClosingTwiceIsANoOpThatKeepsTheFirstClose()
    {
        _ = Request(_main);
        Assert.Equal(0, Close(Id, "--reason", "abandoned").Exit);
        var first = Assert.Single(ResponsesIn(_main));
        var written = File.ReadAllText(first);

        var (exit, stdout, _) = Close(Id, "--reason", "superseded", "--note", "second");

        Assert.Equal(0, exit);
        Assert.True(JsonText.Parse(stdout).AsObject()["already"].AsBool());
        Assert.Equal(first, Assert.Single(ResponsesIn(_main)));
        Assert.Equal(written, File.ReadAllText(first));
    }

    [Fact]
    public void ACloseAfterAWithdrawalIsANoOp()
    {
        _ = Request(_main);
        Assert.Equal(0, Withdraw(Id, "--reason", "r").Exit);
        var withdrawal = Assert.Single(ResponsesIn(_main));
        var written = File.ReadAllText(withdrawal);

        var (exit, stdout, _) = Close(Id, "--reason", "abandoned");

        Assert.Equal(0, exit);
        Assert.True(JsonText.Parse(stdout).AsObject()["already"].AsBool());
        Assert.Equal(written, File.ReadAllText(withdrawal));
    }

    [Fact]
    public void AClosedRequestIsNotALandedReportEither()
    {
        _ = Request(_main);
        Assert.Equal(0, Close(Id, "--reason", "abandoned").Exit);
        var closed = Assert.Single(ResponsesIn(_main));

        Assert.False(_graph.Hierarchy.Messages.ResponseLanded(closed, DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void AMailboxEntryIdStillTakesTheMailboxPathAndWritesNoResponse()
    {
        var made = Run("new", "--to", "reviewer", "--from", "orchestrator", "--slug", "rev", "--title", "Fix test titles", "--to-name", "tm-reviewer", "--json");
        Assert.Equal(0, made.Exit);
        using var doc = JsonDocument.Parse(made.Out);
        var request = doc.RootElement.GetProperty("id").GetString()!;
        Assert.Equal(0, Run("send", "tm-reviewer@tm", "--ref", request, "--id", "b1", "--json").Exit);

        var (exit, stdout, _) = Withdraw("b1", "--reason", "moved");

        Assert.Equal(0, exit);
        var result = JsonText.Parse(stdout).AsObject();
        Assert.Equal("b1", result["withdrawn"].AsString());
        Assert.Equal("not_delivered", result["progress"].AsString());
        Assert.Empty(ResponsesIn(_main, doc.RootElement.GetProperty("id").GetString()!));
    }
}
