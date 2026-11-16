using Ah.Commands;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Tests;

/// <summary><c>transfer</c>: a member's lead moves to a lane's sub-orchestrator and back, and a refused transfer writes nothing.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string Lane = "proj-s1";

    private static string Member(string name) =>
        $"{{\"role\":\"reviewer\",\"name\":\"{name}\",\"route\":\"peer\",\"transport_id\":\"%3\",\"model\":\"sonnet\"}}";

    private static string SoRow(string name, string stream) =>
        $"{{\"role\":\"sub-orchestrator\",\"name\":\"{name}\",\"route\":\"peer\",\"transport_id\":\"%9\",\"model\":\"sonnet\",\"stream\":\"{stream}\"}}";

    private (string TeamPath, string Home) TransferTeam(string subOrchestrator = "so-1", string stream = "open", string childWorkstream = "working", bool withChild = true, string? worktree = null, int? childPid = null)
    {
        var home = SpawnTeamHome();
        var teamPath = Path.Combine(home, "teams", "proj.json");
        var tree = worktree is null ? "null" : $"\"{worktree}\"";

        // The store refuses a nameless row and a second row of one name, so a lane with no sub-orchestrator, or one led by a member, plants no sub-orchestrator row.
        var soRow = subOrchestrator is "" or "mover" or "other" ? string.Empty : "," + SoRow(subOrchestrator, "s1");
        _sandbox.PlantTeam(
            teamPath,
            LiveTeamJson("tmux", Member("mover") + "," + Member("other") + soRow).TrimEnd('}')
            + $",\"streams\":{{\"s1\":{{\"state\":\"{stream}\",\"sub_orchestrator\":\"{subOrchestrator}\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":{tree},\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}}}}}}");
        if (withChild)
        {
            _sandbox.PlantTeam(
                Path.Combine(home, "teams", Lane + ".json"),
                $"{{\"version\":1,\"team_id\":\"t\",\"orchestrator\":{{\"session_id\":\"so\",\"pid\":{(childPid?.ToString() ?? Self.ToString())},\"pid_start\":\"{FakeRunner.SelfStart}\"}},\"members\":[],\"parent\":{{\"team\":\"proj\",\"stream\":\"s1\"}},\"workstream\":{{\"state\":\"{childWorkstream}\"}}}}");
        }

        return (teamPath, home);
    }

    private JsonValue RowOf(string teamPath, string name) =>
        JsonText.Parse(_sandbox.TeamText(teamPath)).Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == name);

    private (int Code, JsonObject Out, string Err) Transfer(params string[] args)
    {
        var (code, stdout, stderr) = Roster(["transfer", .. args, "--team", "proj"]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject(), stdout + stderr);
    }

    [Fact]
    public void TransferringAMemberToALaneSetsItsLeadToTheSubOrchestratorAndHomeClearsIt()
    {
        var (path, _) = TransferTeam();

        var (code, result, err) = Transfer("mover", "--to", Lane);

        Assert.True(code == 0, err);
        Assert.True(result["transferred"].AsBool());
        var lead = RowOf(path, "mover").Member("lead")!.Value;
        Assert.Equal(Lane, lead.Member("team")!.Value.AsString());
        Assert.Equal("so-1", lead.Member("name")!.Value.AsString());
        Assert.Null(RowOf(path, "other").Member("lead"));

        var (backCode, _, backErr) = Transfer("mover", "--to", "proj");

        Assert.True(backCode == 0, backErr);
        Assert.Null(RowOf(path, "mover").Member("lead"));
    }

    [Theory]
    [InlineData("nobody", "proj-s1", "unknown-member")]
    [InlineData("orchestrator", "proj-s1", "member-is-orchestrator")]
    [InlineData("mover", "elsewhere", "target-not-lane")]
    [InlineData("mover", "proj", "already-there")]
    public void ARefusedTransferNamesItsReasonAndWritesNothing(string member, string to, string reason)
    {
        var (path, home) = TransferTeam();
        _sandbox.PlantTeam(Path.Combine(home, "teams", "elsewhere.json"), "{\"version\":1,\"members\":[]}");
        var before = _sandbox.TeamText(path);

        var (code, result, err) = Transfer(member, "--to", to);

        Assert.True(code == 2, err);
        Assert.Equal(reason, result["refused"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Theory]
    [InlineData("done", "working")]
    [InlineData("open", "done")]
    public void ATransferToAClosedLaneIsRefusedAndWritesNothing(string stream, string childWorkstream)
    {
        var (path, _) = TransferTeam(stream: stream, childWorkstream: childWorkstream);
        var before = _sandbox.TeamText(path);

        var (code, result, err) = Transfer("mover", "--to", Lane);

        Assert.True(code == 2, err);
        Assert.Equal("lane-closed", result["refused"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void ATransferToTheSameLaneTwiceIsAlreadyThere()
    {
        _ = TransferTeam();
        Assert.True(Transfer("mover", "--to", Lane).Code == 0);

        var (code, result, _) = Transfer("mover", "--to", Lane);

        Assert.Equal(2, code);
        Assert.Equal("already-there", result["refused"].AsString());
    }

    [Fact]
    public void ADryRunReportsTheTransferAndWritesNothing()
    {
        var (path, _) = TransferTeam();
        var before = _sandbox.TeamText(path);

        var (code, result, err) = Transfer("mover", "--to", Lane, "--dry-run");

        Assert.True(code == 0, err);
        Assert.False(result["transferred"].AsBool());
        Assert.Equal("planned", result["transfer"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void AMemberCannotBeTransferredToItself()
    {
        var (path, _) = TransferTeam(subOrchestrator: "mover");
        var before = _sandbox.TeamText(path);

        var (code, result, _) = Transfer("mover", "--to", Lane);

        Assert.Equal(2, code);
        Assert.Equal("lead-is-member", result["refused"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void ATransferWithAStreamAlsoMovesTheMemberIntoTheStreamTree()
    {
        var tree = RealPathOf(MakeWorkTree("s1"));
        var (path, _) = TransferTeam(worktree: tree);

        var (code, result, err) = Transfer("mover", "--to", Lane, "--stream", "s1");

        Assert.True(code == 0, err);
        Assert.True(result["moved"].AsBool());
        Assert.Equal("done", result["transfer"].AsString());
        var row = RowOf(path, "mover");
        Assert.Equal("s1", row.Member("stream")!.Value.AsString());
        Assert.Equal("so-1", row.Member("lead")!.Value.Member("name")!.Value.AsString());
    }

    [Fact]
    public void ATeamNameWithAPathSeparatorIsNotATeamName()
    {
        var (path, _) = TransferTeam();
        var before = _sandbox.TeamText(path);

        var (code, _, _) = Transfer("mover", "--to", "../proj-s1");

        Assert.NotEqual(0, code);
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("dead")]
    public void ATransferToALaneWithNoLiveSubOrchestratorIsRefused(string kind)
    {
        var (path, _) = kind == "dead" ? TransferTeam(childPid: 2147483646) : TransferTeam(subOrchestrator: string.Empty);
        var before = _sandbox.TeamText(path);

        var (code, result, _) = Transfer("mover", "--to", Lane);

        Assert.Equal(2, code);
        Assert.Equal("lane-no-so", result["refused"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void ATransferToTheHomeTeamNeedsNoSubOrchestrator()
    {
        var (path, _) = TransferTeam(childPid: 2147483646);
        _sandbox.PlantTeam(path, _sandbox.TeamText(path).Replace("\"name\":\"mover\"", "\"name\":\"mover\",\"lead\":{\"team\":\"proj-s1\",\"name\":\"so-1\"}", StringComparison.Ordinal));

        var (code, _, err) = Transfer("mover", "--to", "proj");

        Assert.True(code == 0, err);
        Assert.Null(RowOf(path, "mover").Member("lead"));
    }

    [Fact]
    public void AMemberMovesFromOneLaneToAnotherInOneStepWhenBothAreLanesOfItsHome()
    {
        var (path, home) = TransferTeam();
        _sandbox.PlantTeam(path, _sandbox.TeamText(path).Replace("\"members\":[", "\"members\":[" + SoRow("so-2", "s2") + ",", StringComparison.Ordinal).Replace("\"streams\":{", "\"streams\":{\"s2\":{\"state\":\"open\",\"sub_orchestrator\":\"so-2\",\"branch\":\"feat/s2\",\"base\":\"main\",\"worktree\":null,\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"},", StringComparison.Ordinal));
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj-s2.json"),
            $"{{\"version\":1,\"team_id\":\"t2\",\"orchestrator\":{{\"session_id\":\"so2\",\"pid\":{Self},\"pid_start\":\"{FakeRunner.SelfStart}\"}},\"members\":[],\"parent\":{{\"team\":\"proj\",\"stream\":\"s2\"}},\"workstream\":{{\"state\":\"working\"}}}}");
        Assert.True(Transfer("mover", "--to", Lane).Code == 0);

        var (code, _, err) = Transfer("mover", "--to", "proj-s2");

        Assert.True(code == 0, err);
        var lead = RowOf(path, "mover").Member("lead")!.Value;
        Assert.Equal("proj-s2", lead.Member("team")!.Value.AsString());
        Assert.Equal("so-2", lead.Member("name")!.Value.AsString());
    }

    [Fact]
    public void AMemberWhoseHomeIsALaneCannotBeTransferredToItsParent()
    {
        var (path, home) = TransferTeam();
        var owner = JsonText.Stringify(JsonText.Parse(_sandbox.TeamText(path)).Member("orchestrator")!.Value, 0);
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", Lane + ".json"),
            $"{{\"version\":1,\"team_id\":\"t\",\"orchestrator\":{owner},\"members\":[{Member("kid")}],\"parent\":{{\"team\":\"proj\",\"stream\":\"s1\"}},\"workstream\":{{\"state\":\"working\"}}}}");

        var (code, stdout, _) = Roster(["transfer", "kid", "--to", "proj", "--team", Lane]);

        Assert.Equal(2, code);
        Assert.Equal("target-not-lane", JsonText.Parse(stdout).AsObject()["refused"].AsString());
    }

    [Fact]
    public void AStreamThatIsNotTheLanesOwnIsRefusedAndWritesNothing()
    {
        var (path, _) = TransferTeam();
        var before = _sandbox.TeamText(path);

        var (code, result, _) = Transfer("mover", "--to", Lane, "--stream", "other");

        Assert.Equal(2, code);
        Assert.Equal("stream-not-lane", result["refused"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void ADryRunWithAStreamPlansTheTransferWritesNothingAndMakesNoEvent()
    {
        var tree = RealPathOf(MakeWorkTree("s1"));
        var (path, _) = TransferTeam(worktree: tree);
        var before = _sandbox.TeamText(path);

        var (code, result, err) = Transfer("mover", "--to", Lane, "--stream", "s1", "--dry-run");

        Assert.True(code == 0, err);
        Assert.False(result["moved"].AsBool());
        Assert.Equal("planned", result["transfer"].AsString());
        Assert.False(result["transferred"].AsBool());
        Assert.Null(result.Get("event"));
        Assert.Null(result.Get("note"));
        Assert.Equal(3, result["recipients"].AsArray().Count);
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void ADryRunListsTheRequestsNamedForTheMemberOrForItsRoleWhenTheyNameNobody()
    {
        var (_, home) = TransferTeam();
        var msgs = Path.Combine(home, "msgs");
        Directory.CreateDirectory(msgs);
        void Request(string id, string? toName) => Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(msgs, $"{id}--reviewer--work--request.md"),
            $"---\nid: {id}\ntype: request\nto: reviewer\nfrom: orchestrator\nslug: work\n{(toName is null ? string.Empty : $"to_name: {toName}\n")}---\n\nx");
        Request("20261011-100000-aa01", "mover");
        Request("20261011-100000-aa02", "other");
        Request("20261011-100000-aa03", null);

        var (code, result, err) = Transfer("mover", "--to", Lane, "--dry-run");

        Assert.True(code == 0, err);
        var ids = result["open_requests"].AsArray().Select(r => r.Member("id")!.Value.AsString()).Order().ToArray();
        Assert.Equal(["20261011-100000-aa01", "20261011-100000-aa03"], ids);
        Assert.True(result["checklist_items"].IsNull);
    }

    [Fact]
    public void ARealTransferSendsOneEventUnderOneIdToTheMemberAndBothLeads()
    {
        _ = TransferTeam();
        var mail = new RecordingMail();
        _eventMail = mail;

        var (code, result, err) = Transfer("mover", "--to", Lane);

        Assert.True(code == 0, err);
        Assert.Single(mail.Sent);
        Assert.Equal(result["event"].AsObject()["id"].AsString(), mail.Sent[0].Id);
        Assert.Equal(["mover@proj", "orchestrator@proj", "orchestrator@proj-s1"], mail.Sent[0].Recipients);
        Assert.Equal(3, result["event"].AsObject()["reached"].AsArray().Count);
    }

    [Fact]
    public void ARefusedTransferSendsNoEvent()
    {
        _ = TransferTeam();
        var mail = new RecordingMail();
        _eventMail = mail;

        var (code, _, _) = Transfer("mover", "--to", "proj");

        Assert.Equal(2, code);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public void WithNoMailboxTheTransferStillHappensAndSaysNoEventWasSent()
    {
        var (path, _) = TransferTeam();
        _eventMail = null;

        var (code, result, err) = Transfer("mover", "--to", Lane);

        Assert.True(code == 0, err);
        Assert.NotNull(RowOf(path, "mover").Member("lead"));
        Assert.False(result["event"].AsObject()["sent"].AsBool());
        Assert.False(string.IsNullOrEmpty(result["event"].AsObject()["warning"].AsString()));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    public void ADotTeamNameIsNotATeamName(string to)
    {
        var (path, _) = TransferTeam();
        var before = _sandbox.TeamText(path);

        var (code, _, _) = Transfer("mover", "--to", to);

        Assert.NotEqual(0, code);
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    private (string Path, string Tree) IsolatedMover(string status, string? isolatedIn = null, bool recorded = true, string? expectedRoot = null)
    {
        var tree = RealPathOf(MakeWorkTree("s1"));
        var (path, home) = TransferTeam(worktree: tree);
        var record = recorded ? $",\"isolated_worktree\":\"{isolatedIn ?? "/launch/tree"}\",\"isolated_pane\":\"%3\"" : string.Empty;
        _sandbox.PlantTeam(
            path,
            _sandbox.TeamText(path)
                .Replace("\"name\":\"mover\"", $"\"name\":\"proj-reviewer\",\"expected_root\":\"{expectedRoot ?? Project}\"{record}")
                .Replace("\"name\":\"other\",\"route\":\"peer\",\"transport_id\":\"%3\"", "\"name\":\"other\",\"route\":\"peer\",\"transport_id\":\"%4\""));
        PeerRow(home, "%3", "reviewer", status);
        return (path, tree);
    }

    [Theory]
    [InlineData("transfer")]
    [InlineData("move")]
    public void ARunningMemberIsolatedInAnotherTreeIsNotMovedIntoAStreamAndNothingIsWritten(string verb)
    {
        var (path, _) = IsolatedMover("up");
        var before = _sandbox.TeamText(path);

        var (code, stdout, _) = verb == "transfer" ? Roster(["transfer", "proj-reviewer", "--to", Lane, "--stream", "s1", "--team", "proj"]) : Roster(["move", "proj-reviewer", "--stream", "s1", "--team", "proj"]);

        Assert.True(code == 2, stdout);
        Assert.Equal("relaunch-needed", JsonText.Parse(stdout).AsObject()["refused"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void ARunningMemberWithNoIsolationRecordIsHeldToItsExpectedRoot()
    {
        var (path, _) = IsolatedMover("up", recorded: false);
        var before = _sandbox.TeamText(path);

        var (code, stdout, _) = Roster(["move", "proj-reviewer", "--stream", "s1", "--team", "proj"]);

        Assert.True(code == 2, stdout);
        Assert.Equal("relaunch-needed", JsonText.Parse(stdout).AsObject()["refused"].AsString());
        Assert.Equal(before, _sandbox.TeamText(path));
    }

    [Fact]
    public void ARunningMemberWithNoIsolationRecordWhoseExpectedRootIsTheStreamsTreeIsMoved()
    {
        var tree = RealPathOf(MakeWorkTree("s1"));
        var (path, _) = IsolatedMover("up", recorded: false, expectedRoot: tree);

        var (code, _, err) = Roster(["move", "proj-reviewer", "--stream", "s1", "--team", "proj"]);

        Assert.True(code == 0, err);
        Assert.Equal("s1", RowOf(path, "proj-reviewer").Member("stream")!.Value.AsString());
    }

    [Fact]
    public void AMemberThatIsNotRunningIsMovedEvenWhenItsLastSessionWasIsolatedElsewhere()
    {
        var (path, _) = IsolatedMover("down");

        var (code, _, err) = Roster(["move", "proj-reviewer", "--stream", "s1", "--team", "proj"]);

        Assert.True(code == 0, err);
        Assert.Equal("s1", RowOf(path, "proj-reviewer").Member("stream")!.Value.AsString());
        Assert.Null(RowOf(path, "proj-reviewer").Member("isolated_worktree"));
        Assert.Null(RowOf(path, "proj-reviewer").Member("isolated_pane"));
    }

    [Fact]
    public void ARunningMemberIsolatedInTheStreamsOwnTreeIsMoved()
    {
        var tree = RealPathOf(MakeWorkTree("s1"));
        var (path, _) = IsolatedMover("up", isolatedIn: tree);

        var (code, _, err) = Roster(["move", "proj-reviewer", "--stream", "s1", "--team", "proj"]);

        Assert.True(code == 0, err);
        Assert.Equal("s1", RowOf(path, "proj-reviewer").Member("stream")!.Value.AsString());
        Assert.Equal(tree, RowOf(path, "proj-reviewer").Member("isolated_worktree")!.Value.AsString());
        Assert.NotNull(RowOf(path, "proj-reviewer").Member("isolated_pane"));
    }

    private string WithLeadOn(string teamPath, string member, string leadTeam, string leadName)
    {
        var doc = JsonText.Parse(_sandbox.TeamText(teamPath)).AsObject().Clone();
        var rows = new JsonArray();
        foreach (var row in doc["members"].AsArray())
        {
            var next = row.AsObject().Clone();
            if (row.Member("name")!.Value.AsString() == member)
            {
                next["lead"] = new JsonObject { ["team"] = leadTeam, ["name"] = leadName };
            }

            rows.Add(next);
        }

        doc["members"] = rows;
        _sandbox.PlantTeam(teamPath, JsonText.Stringify(doc, 0));
        return member;
    }

    [Fact]
    public void ClosingALanesStreamReturnsItsMembersToTheManagerAndMarksTheLaneDissolved()
    {
        var (path, home) = TransferTeam();
        var mail = new RecordingMail();
        _eventMail = mail;
        Assert.True(Transfer("mover", "--to", Lane).Code == 0);
        mail.Sent.Clear();
        _ = WithLeadOn(path, "other", "proj-elsewhere", "so-9");

        var (code, stdout, stderr) = Roster("stream-done", "s1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(["mover"], JsonText.Parse(stdout).Member("reverted")!.Value.AsArray().Select(v => v.AsString()).ToArray());
        Assert.Null(RowOf(path, "mover").Member("lead"));
        Assert.Equal("proj-elsewhere", RowOf(path, "other").Member("lead")!.Value.Member("team")!.Value.AsString());
        Assert.Equal("dissolved", JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", Lane + ".json"))).Member("workstream")!.Value.Member("state")!.Value.AsString());
        Assert.Single(mail.Sent);
    }

    private string LaneWithOwnMember(string home, string parentTeam, string parentStream, string? row = null)
    {
        var lanePath = Path.Combine(home, "teams", Lane + ".json");
        _sandbox.PlantTeam(
            lanePath,
            $"{{\"version\":1,\"team_id\":\"t\",\"orchestrator\":{{\"session_id\":\"so\",\"pid\":{Self},\"pid_start\":\"{FakeRunner.SelfStart}\"}},\"members\":[{row ?? Member("kid")}],\"parent\":{{\"team\":\"{parentTeam}\",\"stream\":\"{parentStream}\"}},\"workstream\":{{\"state\":\"working\"}}}}");
        return lanePath;
    }

    [Fact]
    public void ClosingALanesStreamMovesTheMembersOfItsOwnTeamFileIntoTheManagersTeamWithNoLead()
    {
        var (path, home) = TransferTeam();
        var lanePath = LaneWithOwnMember(home, "proj", "s1");
        var mail = new RecordingMail();
        _eventMail = mail;

        var (code, stdout, stderr) = Roster("stream-done", "s1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Null(RowOf(path, "kid").Member("lead"));
        Assert.Empty(JsonText.Parse(_sandbox.TeamText(lanePath)).Member("members")!.Value.AsArray());
        Assert.Equal("dissolved", JsonText.Parse(_sandbox.TeamText(lanePath)).Member("workstream")!.Value.Member("state")!.Value.AsString());
        Assert.Contains("kid", JsonText.Parse(stdout).Member("reverted")!.Value.AsArray().Select(v => v.AsString()));
        Assert.Single(mail.Sent);
        Assert.Equal("orchestrator@proj", HierarchyModule.EventAddress("proj", JsonText.Parse(_sandbox.TeamText(path)).Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "kid"), name => JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", name + ".json"))), (_, _) => true));
    }

    [Fact]
    public void ACollisionOnlyCloseKeepsTheCollidingMemberInTheDissolvedLaneRoutedToTheManagerAndSaysSo()
    {
        var (path, home) = TransferTeam();
        var differing = "{\"role\":\"reviewer\",\"name\":\"other\",\"route\":\"peer\",\"transport_id\":\"%77\",\"model\":\"sonnet\"}";
        var lanePath = LaneWithOwnMember(home, "proj", "s1", differing);
        _eventMail = new RecordingMail();

        var (code, stdout, stderr) = Roster("stream-done", "s1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var laneRecord = JsonText.Parse(_sandbox.TeamText(lanePath));
        Assert.Single(laneRecord.Member("members")!.Value.AsArray());
        Assert.Equal(
            new LeadRef("proj", "orchestrator"),
            HierarchyModule.EffectiveLead(Lane, laneRecord.Member("members")!.Value.AsArray()[0], name => name == Lane ? laneRecord : name == "proj" ? JsonText.Parse(_sandbox.TeamText(path)) : JsonValue.Null, (_, _) => true));
        Assert.Contains("already has a different member", JsonText.Parse(stdout).Member("lane_warnings")!.Value.AsArray().Single().AsString(), StringComparison.Ordinal);
        Assert.Equal("%3", RowOf(path, "other").Member("transport_id")!.Value.AsString());
    }

    [Fact]
    public void AChildRowAndAManagerRowOfTheSameNameThatRecordNoSessionPidOrPaneAreDifferentMembers()
    {
        var (path, home) = TransferTeam();
        _sandbox.PlantTeam(path, _sandbox.TeamText(path).Replace("\"members\":[", "\"members\":[{\"role\":\"reviewer\",\"name\":\"ghost\"},", StringComparison.Ordinal));
        var lanePath = LaneWithOwnMember(home, "proj", "s1", "{\"role\":\"reviewer\",\"name\":\"ghost\"}");
        _eventMail = new RecordingMail();

        var (code, stdout, stderr) = Roster("stream-done", "s1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Single(JsonText.Parse(_sandbox.TeamText(lanePath)).Member("members")!.Value.AsArray());
        Assert.Contains("already has a different member", JsonText.Parse(stdout).Member("lane_warnings")!.Value.AsArray().Single().AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ACloseAfterAnInterruptedOneRemovesTheRowThatIsAlreadyInTheManagersTeamFromTheLane()
    {
        var (path, home) = TransferTeam();
        var lanePath = LaneWithOwnMember(home, "proj", "s1", Member("other"));
        _eventMail = new RecordingMail();

        var (code, stdout, stderr) = Roster("stream-done", "s1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(JsonText.Parse(_sandbox.TeamText(lanePath)).Member("members")!.Value.AsArray());
        Assert.Single(JsonText.Parse(_sandbox.TeamText(path)).Member("members")!.Value.AsArray(), m => m.Member("name")!.Value.AsString() == "other");
        Assert.Contains("earlier close", JsonText.Parse(stdout).Member("lane_warnings")!.Value.AsArray().Single().AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMovedMemberIsNamedInAnAdvisoryBecauseALiveSessionIsRegisteredUnderTheLanesFile()
    {
        var (_, home) = TransferTeam();
        _ = LaneWithOwnMember(home, "proj", "s1");
        _eventMail = new RecordingMail();

        var (_, stdout, _) = Roster("stream-done", "s1", "--team", "proj");

        Assert.Contains("kid: now listed in proj", JsonText.Parse(stdout).Member("lane_warnings")!.Value.AsArray().Select(v => v.AsString()).Single(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("other", "s1")]
    [InlineData("proj-s", "1")]
    [InlineData("proj", "s2")]
    public void ClosingAStreamLeavesATeamOfTheLanesNameThatDoesNotRecordItAsItsParentUntouched(string parentTeam, string parentStream)
    {
        var (path, home) = TransferTeam();
        var lanePath = LaneWithOwnMember(home, parentTeam, parentStream);
        var before = _sandbox.TeamText(lanePath);
        _eventMail = new RecordingMail();

        var (code, stdout, stderr) = Roster("stream-done", "s1", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(before, _sandbox.TeamText(lanePath));
        Assert.DoesNotContain(JsonText.Parse(_sandbox.TeamText(path)).Member("members")!.Value.AsArray(), m => m.Member("name")!.Value.AsString() == "kid");
    }

    [Fact]
    public void DisbandingATeamThatIsNotTheLanesChildTeamNameButClaimsTheLaneAsParentRevertsNobody()
    {
        var (path, home) = TransferTeam();
        Assert.True(Transfer("mover", "--to", Lane).Code == 0);
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "forged.json"),
            $"{{\"version\":1,\"team_id\":\"t3\",\"orchestrator\":{{\"session_id\":\"so3\",\"pid\":{Self},\"pid_start\":\"{FakeRunner.SelfStart}\"}},\"members\":[],\"parent\":{{\"team\":\"proj\",\"stream\":\"s1\"}},\"workstream\":{{\"state\":\"working\"}}}}");
        _eventMail = new RecordingMail();

        var token = JsonText.Parse(Roster("disband", "--team", "forged").Stdout).Member("close_token")!.Value.AsString();
        _ = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", "forged");

        Assert.Equal(Lane, RowOf(path, "mover").Member("lead")!.Value.Member("team")!.Value.AsString());
    }

    [Fact]
    public void DisbandingALanesChildTeamReturnsItsMembersToTheManager()
    {
        var (path, home) = TransferTeam();
        var mail = new RecordingMail();
        _eventMail = mail;
        Assert.True(Transfer("mover", "--to", Lane).Code == 0);
        mail.Sent.Clear();

        var token = JsonText.Parse(Roster("disband", "--team", Lane).Stdout).Member("close_token")!.Value.AsString();
        var (code, stdout, stderr) = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", Lane);

        Assert.True(code == 0, stdout + stderr);
        Assert.Null(RowOf(path, "mover").Member("lead"));
        Assert.Single(mail.Sent);
    }

    [Fact]
    public void ClosingAStreamAgainOrAStreamWithNoLaneMembersReturnsNobody()
    {
        var (_, _) = TransferTeam();
        var mail = new RecordingMail();
        _eventMail = mail;

        var (code, stdout, stderr) = Roster("stream-done", "s1", "--team", "proj");
        var (againCode, again, _) = Roster("stream-done", "s1", "--team", "proj");

        Assert.True(code == 0 && againCode == 0, stdout + stderr);
        Assert.Null(JsonText.Parse(stdout).Member("reverted"));
        Assert.Null(JsonText.Parse(again).Member("reverted"));
        Assert.Empty(mail.Sent);
    }

    private JsonObject? CharterRow(bool answered)
    {
        var (_, home) = TransferTeam();
        var msgs = Path.Combine(home, "msgs");
        Directory.CreateDirectory(msgs);
        const string Id = "20261011-120000-ab01";
        _ = Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(msgs, $"{Id}--sub-orchestrator--work--request.md"),
            $"---\nid: {Id}\ntype: request\nto: sub-orchestrator\nfrom: orchestrator\nslug: work\nteam: proj\ncreated: 2026-10-11T12:00:00Z\nto_name: so-1\n---\n\n## [0] tldr\n- do it\n");
        if (answered)
        {
            _ = Ah.TestKit.TestMsgStore.Plant(
                _sandbox.AhHome,
                Path.Combine(msgs, $"{Id}--orchestrator--work--response.md"),
                $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: so-1\nslug: work\n---\n\n## [0] tldr\n- done early\n");
        }

        var (code, stdout, stderr) = Roster("doctor", "--team", "proj");
        Assert.True(code is 0 or 1, stdout + stderr);
        return JsonText.Parse(stdout).AsObject()["rows"].AsArray()
            .Where(r => r.Member("name")!.Value.AsString() == "charter-response-early").Select(r => r.AsObject()).SingleOrDefault();
    }

    [Fact]
    public void DoctorWarnsOfAResponseWrittenToARunningLanesCharter()
    {
        var row = CharterRow(answered: true)!;

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("20261011-120000-ab01", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorHasNoCharterRowForACharterThatHoldsNoResponse() =>
        Assert.Null(CharterRow(answered: false));

    private sealed class RecordingMail : IRosterEventMail
    {
        public List<(string Id, IReadOnlyList<string> Recipients)> Sent { get; } = [];

        public IReadOnlyList<string> SendEvent(string cwd, string hierarchyDir, string id, string eventName, string text, JsonObject data, IReadOnlyList<string> recipients)
        {
            Sent.Add((id, recipients));
            return recipients;
        }
    }
}
