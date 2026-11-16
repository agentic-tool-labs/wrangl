using Ah.Commands;
using Ah.Mesh;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>roster checkpoint</c>: who is asked, what is recorded, and when <c>--status</c> says a restart is safe.</summary>
public sealed partial class RosterTeamVerbTests
{
    private readonly List<(string Cwd, string Member, string Team, string Ref)> _sent = [];

    private string? _deliveryLine;

    private bool _briefFails;

    private BriefSent? SendBriefFake(string cwd, string member, string team, string refPath)
    {
        _sent.Add((cwd, member, team, refPath));
        return _briefFails ? throw new InvalidOperationException("ah msg send refused") : new BriefSent(_noEntryId ? null : "entry-" + member, _deliveryLine);
    }

    private bool _noEntryId;

    private readonly Dictionary<string, string> _branches = [];

    private readonly Dictionary<string, string> _heads = [];

    private readonly Dictionary<string, string> _dirt = [];

    private readonly Dictionary<string, string> _ahead = [];

    /// <summary>Git answers per work tree: the top level, the branch, the head and the porcelain status of the tree the call names with -C.</summary>
    private void ScriptCheckpointTrees()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        _runner.Output = argv =>
        {
            var at = argv.SkipWhile(a => a != "-C").Skip(1).FirstOrDefault() ?? Project;
            return argv.Contains("rev-list") ? (_ahead.TryGetValue(at, out var ahead) ? ahead : "0") + "\n"
                : argv.Contains("--show-toplevel") ? at + "\n"
                : argv.Contains("--abbrev-ref") ? (_branches.TryGetValue(at, out var branch) ? branch : "ah/feature") + "\n"
                : argv.Contains("rev-parse") && argv.Contains("HEAD") ? (_heads.TryGetValue(at, out var head) ? head : Sha) + "\n"
                : argv.Contains("status") ? (_dirt.TryGetValue(at, out var dirt) ? dirt : string.Empty)
                : string.Empty;
        };
    }

    private void ArrangeCheckpoint(bool live = true)
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        if (live)
        {
            PeerUp(Impl, "%1");
        }

        ScriptCheckpointTrees();
    }

    private JsonValue Take(params string[] extra)
    {
        var (code, stdout, stderr) = Roster(["checkpoint", .. extra, "--team", "proj"]);
        Assert.True(code == 0, stdout + stderr);
        return JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single();
    }

    private JsonValue RecordOf(JsonValue taken) => JsonText.Parse(TestCheckpoints.Read(_sandbox.AhHome, taken.Member("record")!.Value.AsString()));

    private static JsonValue MemberRecord(JsonValue record) => record.Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == Impl);

    private (int Code, JsonValue Result) StatusOf(params string[] extra)
    {
        var (code, stdout, stderr) = Roster(["checkpoint", "--status", .. extra, "--team", "proj"]);
        Assert.True(code is 0 or 3, stdout + stderr);
        return (code, JsonText.Parse(stdout));
    }

    private static JsonValue StatusRow(JsonValue result, string name) =>
        result.Member("teams")!.Value.AsArray().Single().Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == name);

    private string CheckpointAnswer(JsonValue taken, string line)
    {
        var id = taken.Member("members")!.Value.AsArray().Single().Member("request")!.Value.AsString();
        return Answer(id, StatusSection(line), "checkpoint");
    }

    // ---------------------------------------------------------------- who is asked

    [Fact]
    public void AnIdleCleanMemberIsAskedNothingAndNoRequestIsFiled()
    {
        ArrangeCheckpoint();

        var taken = Take();

        var row = taken.Member("members")!.Value.AsArray().Single();
        Assert.Equal("idle-clean", row.Member("classification")!.Value.AsString());
        Assert.Null(row.Member("request"));
        Assert.Empty(_sent);
        Assert.DoesNotContain(Tree(), p => p.Contains("--checkpoint--", StringComparison.Ordinal));
    }

    private JsonValue TakeAsWorkingMember(string role, bool withTree = true)
    {
        var row = withTree
            ? Member(role, Impl, "%1")
            : $"{{\"role\":\"{role}\",\"name\":\"{Impl}\",\"route\":\"peer\",\"transport_id\":\"%1\",\"model\":\"sonnet\"}}";
        WriteTeam("proj", LiveTeamJson("tmux", row));
        PeerUp(Impl, "%1", withCwd: withTree);
        ScriptCheckpointTrees();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow);
        return taken;
    }

    private static string RequestBodyOf(JsonValue taken) =>
        File.ReadAllText(taken.Member("members")!.Value.AsArray().Single().Member("request_path")!.Value.AsString());

    [Theory]
    [InlineData("reviewer")]
    [InlineData("architect")]
    public void AMemberThatCannotCommitIsAskedForNothingToCommitAndAReadOnlyAnswer(string role)
    {
        var body = RequestBodyOf(TakeAsWorkingMember(role));

        Assert.DoesNotContain("wip(resume)", body, StringComparison.Ordinal);
        Assert.Contains("nothing to commit", body, StringComparison.Ordinal);
        Assert.Contains("ready read-only", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("implementor")]
    [InlineData("qa")]
    public void AMemberThatCanCommitIsAskedTheUsualAsk(string role)
    {
        var body = RequestBodyOf(TakeAsWorkingMember(role));

        Assert.Contains("wip(resume)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("read-only", body, StringComparison.Ordinal);
        Assert.DoesNotContain("no-tree", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("implementor")]
    [InlineData("reviewer")]
    public void AMemberWithNoWorkTreeIsAskedTheNoTreeAskWhateverItsRole(string role)
    {
        var body = RequestBodyOf(TakeAsWorkingMember(role, withTree: false));

        Assert.DoesNotContain("wip(resume)", body, StringComparison.Ordinal);
        Assert.Contains("Checkpoint: ready no-tree inflight=none", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("reviewer")]
    [InlineData("architect")]
    public void AReadOnlyAnswerFromAMemberThatCannotCommitIsReadyOnACleanTreeAndStaleOnADirtyOne(string role)
    {
        var taken = TakeAsWorkingMember(role);
        _ = CheckpointAnswer(taken, "Checkpoint: ready read-only inflight=none");

        var (code, result) = StatusOf();

        Assert.Equal(0, code);
        var row = StatusRow(result, Impl);
        Assert.Equal("ready", row.Member("status")!.Value.AsString());
        Assert.Equal("read-only ah/feature " + Sha[..7], row.Member("detail")!.Value.AsString());

        _dirt[Project] = "?? note.md\n";
        var (dirtyCode, dirty) = StatusOf();

        Assert.Equal(3, dirtyCode);
        Assert.Equal("stale", StatusRow(dirty, Impl).Member("status")!.Value.AsString());
    }

    [Fact]
    public void AReadOnlyCheckpointAnswerFromAnImplementorIsHeldWithYourRoleCanCommit()
    {
        var taken = TakeAsWorkingMember("implementor");
        _ = CheckpointAnswer(taken, "Checkpoint: ready read-only inflight=none");

        var (code, result) = StatusOf();

        Assert.Equal(3, code);
        var row = StatusRow(result, Impl);
        Assert.Equal("unanswered", row.Member("status")!.Value.AsString());
        Assert.Contains("your role can commit; answer with branch and sha", row.Member("detail")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ANoTreeCheckpointAnswerFromAMemberWithNoTreeIsReady()
    {
        var taken = TakeAsWorkingMember("implementor", withTree: false);
        _ = CheckpointAnswer(taken, "Checkpoint: ready no-tree inflight=none");

        var (code, result) = StatusOf();

        Assert.Equal(0, code);
        Assert.Equal("ready", StatusRow(result, Impl).Member("status")!.Value.AsString());
    }

    [Fact]
    public void ANoTreeMemberThatAnsweredReadyAndThenLeftIsSafeAndOneThatLeftWithoutAnAnswerIsNot()
    {
        var taken = TakeAsWorkingMember("implementor", withTree: false);
        _sandbox.ClearPeers(Hierarchy);
        AppendGoneRow(Impl, "proj");

        var (_, unanswered) = StatusOf();
        _ = CheckpointAnswer(taken, "Checkpoint: ready no-tree inflight=none");
        var (code, answered) = StatusOf();

        Assert.Equal("gone", StatusRow(unanswered, Impl).Member("status")!.Value.AsString());
        Assert.False(StatusRow(unanswered, Impl).Member("safe")!.Value.AsBool());
        Assert.Equal(0, code);
        Assert.True(StatusRow(answered, Impl).Member("safe")!.Value.AsBool());
    }

    private (string Status, string Detail) ReleaseStatusOfImpl()
    {
        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--plan", "--team", "proj"]);
        Assert.True(code is 0 or 3, stdout + stderr);
        var row = JsonText.Parse(stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray().Single(s => s.Member("member")!.Value.AsString() == Impl);
        return (row.Member("status")!.Value.AsString(), row.Member("detail")!.Value.AsString());
    }

    [Fact]
    public void AnIdleCleanMemberIsReadyWhetherOrNotItWasCheckpointed()
    {
        ArrangeCheckpoint();
        Assert.Equal(("ready", "idle-clean"), ReleaseStatusOfImpl());

        _ = Take();

        Assert.Equal(("ready", "idle-clean"), ReleaseStatusOfImpl());
    }

    [Fact]
    public void AnIdleCleanMemberWithACommitAheadOfTheRunsBranchWarnsAsAhead()
    {
        ArrangeCheckpoint();
        _ = Take();
        _ahead[Project] = "1";

        var (status, detail) = ReleaseStatusOfImpl();

        Assert.Equal("ahead", status);
        Assert.StartsWith($"1 commit(s) in {Project} not on the run's branch or the release: ", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableRunHeadWarnsThatUnshippedWorkCannotBeToldNotThatCommitsAreAhead()
    {
        ArrangeCheckpoint();
        Assert.Equal(("ready", "idle-clean"), ReleaseStatusOfImpl());
        _heads[Project] = string.Empty;

        var (status, detail) = ReleaseStatusOfImpl();

        Assert.Equal("stale", status);
        Assert.StartsWith($"cannot tell whether {Project} holds unshipped commits", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoreReleaseWithANameStillParsesAndTheNextCommandKeepsItWhileTheStatusIsUnchanged()
    {
        ArrangeCheckpoint();
        _dirt[Project] = " M src/a.cs\n";

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--plan", "--ignore-release", Impl, "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout);
        var row = plan.Member("release")!.Value.Member("statuses")!.Value.AsArray().Single();
        Assert.Equal("dirty", row.Member("status")!.Value.AsString());
        Assert.True(row.Member("closable")!.Value.AsBool());
        Assert.Contains($"--ignore-release {Impl}", plan.Member("next")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABusyMemberIsAskedByARequestFiledAndSentAsMailAndTheRecordNamesIt()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var taken = Take();

        var row = taken.Member("members")!.Value.AsArray().Single();
        Assert.Equal("asked", row.Member("classification")!.Value.AsString());
        var sent = Assert.Single(_sent);
        Assert.Equal(Impl, sent.Member);
        Assert.Equal(row.Member("request_path")!.Value.AsString(), sent.Ref);
        Assert.Contains("--checkpoint--request.md", sent.Ref, StringComparison.Ordinal);
        Assert.Contains("Checkpoint: ready branch=", File.ReadAllText(sent.Ref), StringComparison.Ordinal);
        Assert.Equal(row.Member("request")!.Value.AsString(), MemberRecord(RecordOf(taken)).Member("request")!.Value.AsString());
    }

    [Fact]
    public void ADirtyTreeMakesAnIdleMemberAskedAndACleanOneIsNot()
    {
        ArrangeCheckpoint();
        _dirt[Project] = " M file.txt\n";

        var dirty = Take();

        Assert.Equal("asked", dirty.Member("members")!.Value.AsArray().Single().Member("classification")!.Value.AsString());
        Assert.Single(_sent);

        _dirt.Clear();
        _sent.Clear();
        Assert.Equal("idle-clean", Take().Member("members")!.Value.AsArray().Single().Member("classification")!.Value.AsString());
        Assert.Empty(_sent);
    }

    [Fact]
    public void AnOpenRequestToTheMemberMakesItAskedEvenWhenIdleAndClean()
    {
        ArrangeCheckpoint();
        _ = AskRelease("20261016-010000-aaaa", slug: "other-work");

        var taken = Take();

        var row = taken.Member("members")!.Value.AsArray().Single();
        Assert.Equal("asked", row.Member("classification")!.Value.AsString());
        Assert.Equal(["20261016-010000-aaaa"], MemberRecord(RecordOf(taken)).Member("open_requests")!.Value.AsArray().Select(v => v.AsString()));
    }

    [Fact]
    public void ARequestThatWasAnsweredIsNotOpenAndLeavesTheMemberIdleClean()
    {
        ArrangeCheckpoint();
        AskRelease("20261016-010000-aaaa", slug: "other-work");
        Answer("20261016-010000-aaaa", StatusSection("- done"), "other-work");

        var taken = Take();

        Assert.Equal("idle-clean", taken.Member("members")!.Value.AsArray().Single().Member("classification")!.Value.AsString());
    }

    [Fact]
    public void ADetachedHeadMakesAnIdleCleanMemberAskedAndANamedBranchDoesNot()
    {
        ArrangeCheckpoint();
        _branches[Project] = "HEAD";

        Assert.Equal("asked", Take().Member("members")!.Value.AsArray().Single().Member("classification")!.Value.AsString());

        _branches[Project] = "ah/feature";
        Assert.Equal("idle-clean", Take().Member("members")!.Value.AsArray().Single().Member("classification")!.Value.AsString());
    }

    [Fact]
    public void AMemberWhoseSessionIsNotRunningIsNotAskedAndIsRecordedAsNotRunning()
    {
        ArrangeCheckpoint(live: false);

        var taken = Take();

        Assert.Equal("not-running", taken.Member("members")!.Value.AsArray().Single().Member("classification")!.Value.AsString());
        Assert.Empty(_sent);
    }

    // ---------------------------------------------------------------- the record and the output

    [Fact]
    public void TheRecordCarriesTheNoteTheSnapshotTheMemberAndTheOrchestratorsTree()
    {
        ArrangeCheckpoint();

        var taken = Take("--note", "before the reboot");

        var record = RecordOf(taken);
        Assert.Equal("before the reboot", record.Member("note")!.Value.AsString());
        Assert.Equal("proj", record.Member("team")!.Value.AsString());
        Assert.Contains("\"members\"", SnapshotText(record.Member("snapshot")!.Value.AsString()), StringComparison.Ordinal);
        var member = MemberRecord(record);
        Assert.Equal("ah/feature", member.Member("branch")!.Value.AsString());
        Assert.Equal(Project, member.Member("worktree")!.Value.AsString());
        Assert.Equal(Sha, member.Member("head")!.Value.AsString());
        Assert.Equal(Project, record.Member("orchestrator")!.Value.Member("worktree")!.Value.AsString());
        Assert.Equal(Sha, record.Member("orchestrator")!.Value.Member("head")!.Value.AsString());
        Assert.False(taken.Member("safe_to_restart")!.Value.AsBool());
    }

    [Fact]
    public void ASecondCheckpointSupersedesTheRequestsOfTheFirstAndLeavesItsFileAlone()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var first = Take();
        var firstPath = first.Member("record")!.Value.AsString();
        var firstText = TestCheckpoints.Read(_sandbox.AhHome, firstPath);
        var firstRequest = first.Member("members")!.Value.AsArray().Single().Member("request")!.Value.AsString();

        var second = Take();

        Assert.NotEqual(firstPath, second.Member("record")!.Value.AsString());
        Assert.Equal([firstRequest], RecordOf(second).Member("supersedes")!.Value.AsArray().Select(v => v.AsString()));
        Assert.Equal(firstText, TestCheckpoints.Read(_sandbox.AhHome, firstPath));
        Assert.Empty(RecordOf(first).Member("supersedes")!.Value.AsArray());
    }

    [Fact]
    public void ARelayDeliveryLineIsPrintedAndOtherDeliveryLinesAreNot()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        _deliveryLine = "delivery: relay — make this one SendMessage now: SendMessage \"hi\" to proj-implementor.";

        var relayed = Take();

        Assert.Equal([_deliveryLine], relayed.Member("relay")!.Value.AsArray().Select(v => v.AsString()));

        _deliveryLine = "delivery: plugin prompt — do not send a SendMessage for this entry";
        Assert.Empty(Take().Member("relay")!.Value.AsArray());
    }

    [Fact]
    public void ARefusedMailSendIsReportedWithExitOneAndTheRecordIsStillWritten()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        _briefFails = true;

        var (code, stdout, _) = Roster("checkpoint", "--team", "proj");

        Assert.Equal(1, code);
        var team = JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single();
        Assert.Equal("ah msg send refused", team.Member("members")!.Value.AsArray().Single().Member("mail_error")!.Value.AsString());
        Assert.True(TestCheckpoints.Exists(_sandbox.AhHome, team.Member("record")!.Value.AsString()));
    }

    [Fact]
    public void ASubmittedBriefWhoseEntryIdWasNotReadIsNotReportedAsAMailFailure()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        _noEntryId = true;

        var (code, stdout, _) = Roster("checkpoint", "--team", "proj");

        Assert.Equal(0, code);
        var member = JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single().Member("members")!.Value.AsArray().Single();
        Assert.True(member.Member("entry")!.Value.IsNull);
        Assert.Null(member.Member("mail_error"));
        Assert.Equal("asked", member.Member("classification")!.Value.AsString());
    }

    [Fact]
    public void NoRollupIsCreatedWhenTheTeamHasNone()
    {
        ArrangeCheckpoint();
        var withoutRollup = Take();
        Assert.False(Directory.Exists(Path.Combine(Hierarchy, "progress")));
        Assert.True(withoutRollup.Member("rollup")!.Value.IsNull);
    }

    [Fact]
    public void TheRollupGainsOneResumePointLineWhenItExists()
    {
        ArrangeCheckpoint();
        var rollup = Path.Combine(Hierarchy, "progress", "proj", "rollup.md");
        Directory.CreateDirectory(Path.GetDirectoryName(rollup)!);
        File.WriteAllText(rollup, "# rollup\n");

        var taken = Take();

        // per-dir-files-to-db S11d: the rollup is a row; the record names it by its key and the file stays as it was
        Assert.Equal(rollup, taken.Member("rollup")!.Value.AsString());
        Assert.Equal("# rollup\nResume point: " + taken.Member("record")!.Value.AsString() + "\n", Roster("progress", "show", "--key", rollup).Item2);
        Assert.Equal("# rollup\n", File.ReadAllText(rollup));
    }

    [Fact]
    public void CheckpointRefusesBadFlagsAndNeedsAnOwner()
    {
        ArrangeCheckpoint();

        Assert.NotEqual(0, Roster("checkpoint", "--all", "--team", "proj").Code);
        Assert.NotEqual(0, Roster("checkpoint", "--bogus", "--team", "proj").Code);
        Assert.NotEqual(0, Roster("checkpoint", "--status", "--note", "x", "--team", "proj").Code);
        Assert.NotEqual(0, Roster("checkpoint", "somewhere", "--team", "proj").Code);

        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);
        var before = Tree();
        var (code, _, stderr) = Roster("checkpoint", "--team", "proj");

        Assert.NotEqual(0, code);
        Assert.Contains("member", stderr, StringComparison.Ordinal);
        Assert.Equal(before, Tree());
        Assert.Empty(_sent);
    }

    [Fact]
    public void AllTakesEveryTeamTheCallerOwnsAndNotOneItDoesNot()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        WriteTeam("other", LiveTeamJson("tmux", Member("implementor", "other-implementor", "%2"), pid: "99999999", pidStart: GoneStart));
        PeerUp(Impl, "%1");
        ScriptCheckpointTrees();

        var (code, stdout, stderr) = Roster("checkpoint", "--all");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(["proj"], JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Select(t => t.Member("team")!.Value.AsString()));
    }

    [Fact]
    public void AllTakesBothTeamsWhenTheCallerOwnsMoreThanOneAndAPlainCheckpointStillAsksWhichTeam()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        WriteTeam("two", LiveTeamJson("tmux", Member("implementor", "two-implementor", "%2")));
        PeerUp(Impl, "%1");
        PeerUp("two-implementor", "%2");
        ScriptCheckpointTrees();

        var (plainCode, _, plainErr) = Roster("checkpoint");
        Assert.NotEqual(0, plainCode);
        Assert.Contains("pass --team <name> to say which", plainErr, StringComparison.Ordinal);

        var (code, stdout, stderr) = Roster("checkpoint", "--all");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(["proj", "two"], JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Select(t => t.Member("team")!.Value.AsString()).Order().ToArray());
    }

    // ---------------------------------------------------------------- the status

    [Fact]
    public void AnIdleCleanTeamIsSafeToRestartWithExitZero()
    {
        ArrangeCheckpoint();
        Take();

        var (code, result) = StatusOf();

        Assert.Equal(0, code);
        Assert.True(result.Member("safe_to_restart")!.Value.AsBool());
        Assert.Equal("idle-clean", StatusRow(result, Impl).Member("status")!.Value.AsString());
    }

    [Fact]
    public void AnIdleCleanMemberThatDirtiedItsTreeSinceIsStaleAndBlocks()
    {
        ArrangeCheckpoint();
        Take();
        _dirt[Project] = " M file.txt\n";

        var (code, result) = StatusOf();

        Assert.Equal(3, code);
        var row = StatusRow(result, Impl);
        Assert.Equal("stale", row.Member("status")!.Value.AsString());
        Assert.Equal("dirty", row.Member("detail")!.Value.AsString());
    }

    [Fact]
    public void AnAskedMemberThatIsStillBusyIsAskedAndAnIdleOneIsUnanswered()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        Take();

        var (busyCode, busy) = StatusOf();

        Assert.Equal(3, busyCode);
        Assert.Equal("asked", StatusRow(busy, Impl).Member("status")!.Value.AsString());

        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow);
        var (idleCode, idle) = StatusOf();

        Assert.Equal(3, idleCode);
        Assert.Equal("unanswered", StatusRow(idle, Impl).Member("status")!.Value.AsString());
    }

    [Fact]
    public void AReadyAnswerWithTheSameShaAndACleanTreeIsReadyAndSafe()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow);
        CheckpointAnswer(taken, "Checkpoint: ready branch=ah/feature sha=" + Sha[..7] + " inflight=none");

        var (code, result) = StatusOf();

        Assert.Equal(0, code);
        var row = StatusRow(result, Impl);
        Assert.Equal("ready", row.Member("status")!.Value.AsString());
        Assert.Equal(Sha[..7], row.Member("detail")!.Value.AsString());
    }

    [Fact]
    public void AReadyAnswerIsStaleWhenTheTreeIsDirtyOrTheHeadMovedAndBusyAgain()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow);
        CheckpointAnswer(taken, "Checkpoint: ready branch=ah/feature sha=" + Sha[..7] + " inflight=none");

        _dirt[Project] = " M file.txt\n";
        var (dirtyCode, dirty) = StatusOf();
        Assert.Equal(3, dirtyCode);
        Assert.Equal("dirty", StatusRow(dirty, Impl).Member("detail")!.Value.AsString());

        _dirt.Clear();
        _heads[Project] = "fedcba9876543210fedcba9876543210fedcba98";
        var (movedCode, moved) = StatusOf();
        Assert.Equal(3, movedCode);
        Assert.Equal("sha", StatusRow(moved, Impl).Member("detail")!.Value.AsString());

        _heads.Clear();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var (busyCode, busy) = StatusOf();
        Assert.Equal(3, busyCode);
        Assert.Equal("busy", StatusRow(busy, Impl).Member("detail")!.Value.AsString());
    }

    [Fact]
    public void ANotReadyAnswerBlocksWithItsReasonAndAReleaseLineAnswersACheckpointAsk()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var taken = Take();
        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow);
        var response = CheckpointAnswer(taken, "Checkpoint: not-ready reason=merge in progress");

        var (code, result) = StatusOf();

        Assert.Equal(3, code);
        var row = StatusRow(result, Impl);
        Assert.Equal("not-ready", row.Member("status")!.Value.AsString());
        Assert.Equal("merge in progress", row.Member("detail")!.Value.AsString());

        File.WriteAllText(response, File.ReadAllText(response).Replace("Checkpoint: not-ready reason=merge in progress", "Release: ready branch=ah/feature sha=" + Sha[..7] + " inflight=none", StringComparison.Ordinal));
        var (otherCode, other) = StatusOf();
        Assert.Equal(0, otherCode);
        Assert.Equal("ready", StatusRow(other, Impl).Member("status")!.Value.AsString());
    }

    [Fact]
    public void AMemberThatLeftIsSafeOnlyIfItWasIdleCleanOrReadyAndItsTreeIsClean()
    {
        ArrangeCheckpoint();
        Take();
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));

        var (goneCode, gone) = StatusOf();

        Assert.Equal(0, goneCode);
        Assert.Equal("gone", StatusRow(gone, Impl).Member("status")!.Value.AsString());

        _dirt[Project] = " M file.txt\n";
        Assert.Equal(3, StatusOf().Code);
    }

    [Fact]
    public void AMemberThatLeftWithoutAnsweringBlocks()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        Take();
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));

        var (code, result) = StatusOf();

        Assert.Equal(3, code);
        Assert.Equal("gone", StatusRow(result, Impl).Member("status")!.Value.AsString());
        Assert.False(StatusRow(result, Impl).Member("safe")!.Value.AsBool());
    }

    [Fact]
    public void ADirtyOrchestratorTreeBlocksAnOtherwiseSafeTeam()
    {
        var other = Path.Combine(_sandbox.Root, "memberroot");
        Directory.CreateDirectory(Path.Combine(other, ".git"));
        WriteTeam("proj", LiveTeamJson("tmux", Member("implementor", Impl, "%1").Replace(Project, other, StringComparison.Ordinal)));
        PeerUp(Impl, "%1", cwdPath: other);
        ScriptCheckpointTrees();
        Take();

        Assert.Equal(0, StatusOf().Code);

        _dirt[Project] = " M mine.txt\n";
        var (code, result) = StatusOf();

        Assert.Equal(3, code);
        Assert.Equal("dirty", result.Member("teams")!.Value.AsArray().Single().Member("orchestrator")!.Value.Member("status")!.Value.AsString());
        Assert.Equal("idle-clean", StatusRow(result, Impl).Member("status")!.Value.AsString());
    }

    [Fact]
    public void AStatusWithoutAnyRecordIsNotSafe()
    {
        ArrangeCheckpoint();

        var (code, result) = StatusOf();

        Assert.Equal(3, code);
        Assert.False(result.Member("safe_to_restart")!.Value.AsBool());
    }

    [Fact]
    public void AStatusNamingAnEarlierRecordJudgesThatRecord()
    {
        ArrangeCheckpoint();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var first = Take();
        var firstPath = first.Member("record")!.Value.AsString();
        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow);
        Take();

        var (code, result) = StatusOf(firstPath);

        Assert.Equal(3, code);
        Assert.Equal(firstPath, result.Member("teams")!.Value.AsArray().Single().Member("record")!.Value.AsString());
        Assert.NotEqual(0, Roster("checkpoint", "--status", "relative/path.json", "--team", "proj").Code);
    }
}
