using Ah.Commands;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary>The owner of a manager team staffing a lane's child team with real writes: the row, the journal, the child's file and the note to the lead.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string LaneMember = "{\"role\":\"reviewer\",\"name\":\"proj-s1-reviewer\",\"route\":\"peer\",\"transport_id\":\"%1\",\"model\":\"sonnet\",\"stream\":\"s1\"}";

    private const string LaneMemberWithoutPane = "{\"role\":\"reviewer\",\"name\":\"proj-s1-reviewer\",\"route\":\"peer\",\"stream\":\"s1\"}";

    private sealed class LeadMail(Func<string> childText, Func<IReadOnlyList<string>, IReadOnlyList<string>> reach) : IRosterEventMail
    {
        public List<(string Event, string Text, JsonObject Data, IReadOnlyList<string> Recipients, string FileAtCall)> Sent { get; } = [];

        public List<string> Ids { get; } = [];

        public IReadOnlyList<string> SendEvent(string cwd, string hierarchyDir, string id, string eventName, string text, JsonObject data, IReadOnlyList<string> recipients)
        {
            if (eventName == "member-launched-bare")
            {
                return reach(recipients);
            }

            Ids.Add(id);
            Sent.Add((eventName, text, data, recipients, childText()));
            return reach(recipients);
        }
    }

    private LeadMail LeadMailOf(Func<IReadOnlyList<string>, IReadOnlyList<string>>? reach = null)
    {
        var mail = new LeadMail(() => _sandbox.TeamText(TeamPath("proj-s1")), reach ?? (r => r));
        _eventMail = mail;
        return mail;
    }

    /// <summary>A manager team owned by this process and an open lane s1 whose child proj-s1 is led by another process; the caller is the manager's unmarked Orchestrator.</summary>
    private void ParentLane(string members, string leadPid = "1", string laneRecord = "", string parentTeamId = "t-1", string transport = "tmux")
    {
        var tree1 = MakeWorkTree("s1");
        var tree2 = MakeWorkTree("s2");
        var lane = laneRecord.Length == 0 ? string.Empty : ",\"lane\":" + laneRecord;
        WriteTeam(
            "proj",
            SoTeam(
                OpenStreamJson("s1", "proj-so-s1").TrimEnd('}') + ",\"worktree\":\"" + tree1 + "\"" + lane + "}",
                SoMember("s1", false)));
        WriteTeam(
            "proj-s1",
            "{\"version\":1,\"team_id\":\"t-lane\",\"created\":\"" + DateTimeOffset.Now.ToString("o", System.Globalization.CultureInfo.InvariantCulture) + "\",\"transport\":\"" + transport + "\",\"orchestrator\":{\"session_id\":null,\"pid\":" + leadPid
            + (leadPid == Self ? ",\"pid_start\":\"" + SelfStart + "\"" : string.Empty) + "},\"members\":[" + members + "],\"parent\":{\"team\":\"proj\",\"stream\":\"s1\",\"team_id\":\"" + parentTeamId + "\"},\"workstream\":{\"state\":\"working\"},"
            + "\"streams\":{\"s1\":{\"state\":\"open\",\"worktree\":\"" + tree1 + "\"},\"s2\":{\"state\":\"open\",\"worktree\":\"" + tree2 + "\"}}}");
        _nearest = new NearestSession(NearestKind.Orchestrator, null, null, Environment.ProcessId);
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;
    }

    private JsonObject LaneChild() => JsonText.Parse(_sandbox.TeamText(TeamPath("proj-s1"))).AsObject();

    private (int Code, string Stdout, string Stderr) DismissLaneMember()
    {
        var token = TokenOf(Roster("dismiss", "proj-s1-reviewer", "--plan", "--ignore-release", "--team", "proj-s1").Stdout);
        return Roster("dismiss", "proj-s1-reviewer", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", "proj-s1");
    }

    private static readonly string[] SpawnIntoLane = ["spawn-ad-hoc", "reviewer", "--kind", "claude", "--model", "sonnet", "--team", "proj-s1", "--orchestrator-pid", Self];

    [Fact]
    public void ParentOwnerDismissOfTheOnlyMemberKeepsTheChildFileJournalsItAndTellsTheLead()
    {
        ParentLane(LaneMember);
        PeerUp("proj-s1-reviewer", "%1");
        var mail = LeadMailOf();
        var before = LaneChild();

        var (code, stdout, stderr) = DismissLaneMember();

        Assert.True(code == 0, stdout + stderr);
        var after = LaneChild();
        Assert.Empty(after["members"].AsArray());
        Assert.Equal(JsonText.Stringify(before["orchestrator"], 0), JsonText.Stringify(after["orchestrator"], 0));
        Assert.Equal(JsonText.Stringify(before["parent"], 0), JsonText.Stringify(after["parent"], 0));
        var entry = Assert.Single(after["parent_actions"].AsArray());
        Assert.Equal("dismiss", entry.Member("verb")!.Value.AsString());
        Assert.Equal("proj-s1-reviewer", entry.Member("member")!.Value.AsString());
        Assert.Equal("orchestrator@proj", entry.Member("by")!.Value.AsString());
        var sent = Assert.Single(mail.Sent);
        Assert.Equal("parent-action", sent.Event);
        Assert.Equal(["proj-so-s1@proj"], sent.Recipients);
        Assert.Contains("parent_actions", sent.FileAtCall, StringComparison.Ordinal);
        Assert.Equal(entry.Member("at")!.Value.AsString(), sent.Data.Get("at")!.Value.AsString());
    }

    [Fact]
    public void ParentOwnersBareDismissOfALaneMemberThatIsNotCleanClosesItAndWarnsWithItsReasons()
    {
        ParentLane(LaneMember);
        PeerUp("proj-s1-reviewer", "%1");

        var (code, stdout, stderr) = Roster("dismiss", "proj-s1-reviewer", "--team", "proj-s1");

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.Empty(LaneChild()["members"].AsArray());
        Assert.Contains(
            JsonText.Parse(stdout).Member("warnings")!.Value.AsArray(),
            w => w.AsString().StartsWith("not clean: proj-s1-reviewer: ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(NearestKind.Session, false)]
    [InlineData(NearestKind.Orchestrator, true)]
    public void NegativeTheManagersOwnerIsNotAdmittedToTheChildWhenItsNearestIsASessionOrAnOrchestratorMatchedOnlyByTheLegacyStart(NearestKind kind, bool legacyStart)
    {
        ParentLane(LaneMemberWithoutPane);
        _nearest = new NearestSession(kind, null, null, Environment.ProcessId, legacyStart);
        var before = _sandbox.TeamText(TeamPath("proj-s1"));

        var (code, stdout, stderr) = Roster("dismiss", "proj-s1-reviewer", "--team", "proj-s1");

        Assert.NotEqual(0, code);
        Assert.Contains("not-team-owner", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj-s1")));
    }

    [Fact]
    public void TheNoteEventIdNamesTheVerbSoTwoActionsOnOneMemberInOneSecondDoNotShareAnId()
    {
        ParentLane(LaneMemberWithoutPane);
        var mail = LeadMailOf();

        var move = Roster("move", "proj-s1-reviewer", "--stream", "s2", "--team", "proj-s1");
        var back = Roster("move", "proj-s1-reviewer", "--stream", "s1", "--team", "proj-s1");

        Assert.True(move.Code == 0 && back.Code == 0, move.Stdout + move.Stderr + back.Stdout + back.Stderr);
        Assert.Equal(2, mail.Ids.Count);
        Assert.EndsWith(":proj-s1-reviewer:move", mail.Ids[0], StringComparison.Ordinal);
        Assert.StartsWith("parent-action:proj-s1:", mail.Ids[1], StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheNoteEventIdOfADismissIsNotTheIdOfAMoveOfTheSameMember()
    {
        ParentLane(LaneMember);
        PeerUp("proj-s1-reviewer", "%1");
        var mail = LeadMailOf();

        var (code, stdout, stderr) = DismissLaneMember();

        Assert.True(code == 0, stdout + stderr);
        Assert.EndsWith(":proj-s1-reviewer:dismiss", Assert.Single(mail.Ids), StringComparison.Ordinal);
    }

    [Fact]
    public void ParentOwnerDismissOfAMemberLentToTheLaneIsRefusedAsLentAndAnUnknownNameAsNotOwnAndNothingIsClosed()
    {
        ParentLane(LaneMember);
        WriteTeam(
            "home",
            "{\"version\":1,\"team_id\":\"t-home\",\"transport\":\"tmux\",\"orchestrator\":{\"session_id\":null,\"pid\":1},\"members\":["
            + "{\"role\":\"architect\",\"name\":\"home-arch\",\"route\":\"peer\",\"transport_id\":\"%5\",\"lead\":{\"team\":\"proj-s1\",\"name\":\"proj-so-s1\"}}]}");
        var before = _sandbox.TeamText(TeamPath("proj-s1"));
        var callsBefore = _runner.Calls.Count;

        var lent = Roster("dismiss", "home-arch", "--team", "proj-s1");
        var unknown = Roster("dismiss", "nobody", "--team", "proj-s1");

        Assert.NotEqual(0, lent.Code);
        Assert.Contains("lent-member", lent.Stdout + lent.Stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, unknown.Code);
        Assert.Contains("not-own-member", unknown.Stdout + unknown.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj-s1")));
        Assert.DoesNotContain(_runner.Calls.Skip(callsBefore), argv => argv.Contains("close") || argv.Contains("kill-pane"));
    }

    [Fact]
    public void NegativeADismissOfTheLastMemberOfATeamThatIsNotALaneChildKeepsItsFileEmpty()
    {
        WriteTeam("proj", LiveTeamJson("tmux", "{\"role\":\"reviewer\",\"name\":\"proj-reviewer\",\"route\":\"peer\",\"transport_id\":\"%2\",\"model\":\"sonnet\"}"));
        var plan = Roster("dismiss", "proj-reviewer", "--plan", "--ignore-release", "--team", "proj").Stdout;
        Assert.Null(JsonText.Parse(plan).Member("team_will_be_removed"));
        var token = TokenOf(plan);

        var (code, stdout, stderr) = Roster("dismiss", "proj-reviewer", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray());
    }

    [Fact]
    public void ParentOwnerMoveRecordsTheActorJournalsItAndTellsTheLead()
    {
        ParentLane(LaneMemberWithoutPane);
        var mail = LeadMailOf();

        var (code, stdout, stderr) = Roster("move", "proj-s1-reviewer", "--stream", "s2", "--team", "proj-s1");

        Assert.True(code == 0, stdout + stderr);
        var row = LaneChild()["members"].AsArray().Single();
        Assert.Equal("s2", row.Member("stream")!.Value.AsString());
        Assert.Equal("orchestrator@proj", row.Member("placed_by")!.Value.AsString());
        var entry = Assert.Single(LaneChild()["parent_actions"].AsArray());
        Assert.Equal("move", entry.Member("verb")!.Value.AsString());
        Assert.Equal("s2", entry.Member("stream")!.Value.AsString());
        Assert.Equal("proj-so-s1@proj", Assert.Single(Assert.Single(mail.Sent).Recipients));
    }

    [Fact]
    public void NegativeParentOwnerMoveToAStreamTheChildDoesNotHaveIsRefusedAndWritesNothing()
    {
        ParentLane(LaneMemberWithoutPane);
        var mail = LeadMailOf();

        var (code, _, _) = Roster("move", "proj-s1-reviewer", "--stream", "elsewhere", "--team", "proj-s1");

        Assert.NotEqual(0, code);
        Assert.Empty(mail.Sent);
        Assert.False(LaneChild().ContainsKey("parent_actions"));
    }

    [Fact]
    public void ParentOwnerSpawnRecordsTheActorOnTheRowJournalsItAndTellsTheLeadOnceAfterTheWrite()
    {
        ParentLane(string.Empty);
        var mail = LeadMailOf();
        var before = LaneChild();

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.True(code == 0, stdout + stderr);
        var after = LaneChild();
        Assert.Equal("orchestrator@proj", after["members"].AsArray().Single().Member("placed_by")!.Value.AsString());
        Assert.Equal(JsonText.Stringify(before["orchestrator"], 0), JsonText.Stringify(after["orchestrator"], 0));
        var entry = Assert.Single(after["parent_actions"].AsArray());
        var sent = Assert.Single(mail.Sent);
        Assert.Equal(["proj-so-s1@proj"], sent.Recipients);
        Assert.Contains("parent_actions", sent.FileAtCall, StringComparison.Ordinal);
        Assert.Equal(entry.Member("at")!.Value.AsString(), sent.Data.Get("at")!.Value.AsString());
        Assert.Equal("proj-s1", sent.Data.Get("team")!.Value.AsString());
    }

    [Theory]
    [InlineData("none")]
    [InlineData("empty")]
    [InlineData("throws")]
    public void ALeadThatCannotBeToldDoesNotFailTheVerbAndTheJournalStillHoldsTheAction(string how)
    {
        ParentLane(string.Empty);
        if (how == "none")
        {
            _eventMail = null;
        }
        else
        {
            _ = LeadMailOf(how == "empty" ? _ => [] : _ => throw new InvalidOperationException("mailbox down"));
        }

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("note: lane lead proj-so-s1@proj not notified", stderr, StringComparison.Ordinal);
        Assert.Single(LaneChild()["parent_actions"].AsArray());
    }

    [Fact]
    public void NegativeADryRunSpawnByTheParentOwnerWritesNoJournalAndSendsNothing()
    {
        ParentLane(string.Empty);
        var mail = LeadMailOf();

        var (code, stdout, stderr) = Roster([.. SpawnIntoLane, "--dry-run"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(mail.Sent);
        Assert.False(LaneChild().ContainsKey("parent_actions"));
    }

    [Fact]
    public void NegativeARefusedSpawnByTheParentOwnerSendsNothingAndJournalsNothing()
    {
        ParentLane("{\"role\":\"architect\",\"name\":\"proj-s1-architect\",\"route\":\"peer\",\"stream\":\"s1\"}", laneRecord: "{\"max_members\":1,\"max_streams\":8}");
        var mail = LeadMailOf();

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-members-full", stdout + stderr, StringComparison.Ordinal);
        Assert.Empty(mail.Sent);
        Assert.False(LaneChild().ContainsKey("parent_actions"));
    }

    private void AsTheLaneLead()
    {
        _extraEnv[Ah.Mesh.MeshModule.MemberMarkerEnv] = "proj-so-s1";
        _extraEnv["WRANGL_TEAM_FILE"] = TeamPath("proj");
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-so-s1", Environment.ProcessId);
    }

    [Fact]
    public void NegativeTheLaneLeadsOwnSpawnIntoItsChildSendsNoNoteAndWritesNoParentRecord()
    {
        ParentLane(string.Empty, leadPid: Self);
        AsTheLaneLead();
        var mail = LeadMailOf();

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(mail.Sent);
        var child = LaneChild();
        Assert.False(child.ContainsKey("parent_actions"));
        Assert.Null(child["members"].AsArray().Single().Member("placed_by"));
    }

    private static readonly string[] CustomIntoLane = ["spawn-ad-hoc", "reviewer", "--kind", "custom", "--harness", "mytool", "--team", "proj-s1", "--orchestrator-pid", Self];

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ParentOwnerCustomKindIntoALaneChildIsRefusedAtAndBelowTheCap(int cap)
    {
        ParentLane(
            "{\"role\":\"architect\",\"name\":\"proj-s1-architect\",\"route\":\"peer\",\"stream\":\"s1\"}",
            laneRecord: "{\"max_members\":" + cap + ",\"max_streams\":8}");
        var mail = LeadMailOf();

        var (code, stdout, stderr) = Roster(CustomIntoLane);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-custom-kind", stdout + stderr, StringComparison.Ordinal);
        Assert.Single(LaneChild()["members"].AsArray());
        Assert.False(LaneChild().ContainsKey("parent_actions"));
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public void NegativeTheLaneLeadsCustomKindIntoItsOwnChildIsStillRefused()
    {
        ParentLane(string.Empty, leadPid: Self);
        AsTheLaneLead();

        var (code, stdout, stderr) = Roster(CustomIntoLane);

        Assert.NotEqual(0, code);
        Assert.Contains("lane-custom-kind", stdout + stderr, StringComparison.Ordinal);
        Assert.Empty(LaneChild()["members"].AsArray());
    }

    [Fact]
    public void NegativeTheOwnerOfAManagerTeamMaySpawnTheCustomKindIntoTheManagerTeamItself()
    {
        ParentLane(string.Empty);
        var before = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).AsObject()["members"].AsArray().Count;

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "reviewer", "--kind", "custom", "--harness", "mytool", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(before + 1, JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).AsObject()["members"].AsArray().Count);
    }

    [Fact]
    public void AParentOwnerSpawnWhoseLaunchFailsLeavesTheChildFileJournalsTheRollbackAndSendsNoNote()
    {
        ParentLane(string.Empty);
        _runner.Output = _ => string.Empty;
        var mail = LeadMailOf();
        var before = LaneChild();

        var (code, _, _) = Roster(SpawnIntoLane);

        Assert.NotEqual(0, code);
        var after = LaneChild();
        Assert.Empty(after["members"].AsArray());
        Assert.Equal(JsonText.Stringify(before["orchestrator"], 0), JsonText.Stringify(after["orchestrator"], 0));
        Assert.Equal(JsonText.Stringify(before["parent"], 0), JsonText.Stringify(after["parent"], 0));
        var journal = after["parent_actions"].AsArray();
        Assert.Equal(2, journal.Count);
        Assert.Equal("spawn-ad-hoc", journal[0].Member("verb")!.Value.AsString());
        Assert.Equal("spawn-rollback", journal[1].Member("verb")!.Value.AsString());
        Assert.Equal(journal[0].Member("member")!.Value.AsString(), journal[1].Member("member")!.Value.AsString());
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public void AParentOwnerSpawnThatSucceedsJournalsOnceAndTheNoteSeesTheRowPresent()
    {
        ParentLane(string.Empty);
        var mail = LeadMailOf();

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.True(code == 0, stdout + stderr);
        Assert.Single(LaneChild()["parent_actions"].AsArray());
        var sent = Assert.Single(mail.Sent);
        Assert.Single(JsonText.Parse(sent.FileAtCall).AsObject()["members"].AsArray());
    }

    [Fact]
    public void NegativeTheLaneLeadsOwnSpawnWhoseLaunchFailsWritesNoJournalAndSendsNoNote()
    {
        ParentLane(string.Empty, leadPid: Self);
        AsTheLaneLead();
        _runner.Output = _ => string.Empty;
        var mail = LeadMailOf();

        var (code, _, _) = Roster(SpawnIntoLane);

        Assert.NotEqual(0, code);
        Assert.False(LaneChild().ContainsKey("parent_actions"));
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public void NegativeAMemberProvenByAncestryMovingItselfIsRefusedAndItsRowStaysInItsStream()
    {
        ParentLane(LaneMemberWithoutPane, leadPid: Self);
        _nearest = new NearestSession(NearestKind.Member, "proj-s1", "proj-s1-reviewer", Environment.ProcessId);
        var before = JsonText.Stringify(LaneChild()["members"], 0);

        var (code, stdout, stderr) = Roster("move", "proj-s1-reviewer", "--stream", "s2", "--team", "proj-s1");

        Assert.Equal(2, code);
        Assert.Contains("member-cannot-move", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(before, JsonText.Stringify(LaneChild()["members"], 0));
    }

    [Fact]
    public void ParentOwnerMoveWithDryRunWritesNoRowNoJournalAndSendsNothing()
    {
        ParentLane(LaneMemberWithoutPane);
        var mail = LeadMailOf();
        var before = JsonText.Stringify(LaneChild(), 0);

        var (code, stdout, stderr) = Roster("move", "proj-s1-reviewer", "--stream", "s2", "--team", "proj-s1", "--dry-run");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(mail.Sent);
        Assert.Equal(before, JsonText.Stringify(LaneChild(), 0));
    }

    [Fact]
    public void ParentOwnerMayPassAPaneDestinationToMoveOnTheChildAndADryRunOfItWritesAndSendsNothing()
    {
        ParentLane(LaneMember, transport: "herdr");
        var mail = LeadMailOf();
        var before = JsonText.Stringify(LaneChild(), 0);

        var (code, stdout, stderr) = Roster("move", "proj-s1-reviewer", "--tab", "t1", "--split", "right", "--dry-run", "--team", "proj-s1");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("herdr pane move %1 --tab t1 --split right", JsonText.Parse(stdout).Member("command")!.Value.AsString());
        Assert.Empty(mail.Sent);
        Assert.Equal(before, JsonText.Stringify(LaneChild(), 0));
    }

    [Fact]
    public void ParentOwnerDismissPlanWritesNoRowNoJournalAndSendsNothing()
    {
        ParentLane(LaneMember);
        PeerUp("proj-s1-reviewer", "%1");
        var mail = LeadMailOf();
        var before = JsonText.Stringify(LaneChild(), 0);

        var (code, stdout, stderr) = Roster("dismiss", "proj-s1-reviewer", "--plan", "--ignore-release", "--team", "proj-s1");

        Assert.True(code == 0, stdout + stderr);
        Assert.Empty(mail.Sent);
        Assert.Equal(before, JsonText.Stringify(LaneChild(), 0));
    }

    [Fact]
    public void TheJournalKeepsTheLastTwentyActions()
    {
        ParentLane(string.Empty);
        _ = LeadMailOf();
        var seeded = string.Join(",", Enumerable.Range(0, 20).Select(i => "{\"verb\":\"spawn-one\",\"member\":\"old" + i + "\",\"by\":\"orchestrator@proj\",\"at\":\"2026-01-01T00:00:00Z\"}"));
        var child = LaneChild();
        child["parent_actions"] = JsonText.Parse("[" + seeded + "]");
        _sandbox.PlantTeam(TeamPath("proj-s1"),JsonText.Stringify(child, 0));

        var (code, stdout, stderr) = Roster(SpawnIntoLane);

        Assert.True(code == 0, stdout + stderr);
        var kept = LaneChild()["parent_actions"].AsArray();
        Assert.Equal(20, kept.Count);
        Assert.Equal("old1", kept[0].Member("member")!.Value.AsString());
    }
}
