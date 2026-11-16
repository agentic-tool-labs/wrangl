using Ah.Core.Json;
using Ah.Commands;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>resume --checkpoint</c> of a team behind a wind-down: a record-only checkpoint of the team as it is now replaces the old record.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string AfterAnyRecord = "2999-01-01T00:00:00.000Z";

    private void ChainDownRow(string team, string state, string at)
    {
        ComposeServices().Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(new JsonObject { ["type"] = "chain-down", ["state"] = state, ["session_id"] = ImplSession, ["member"] = Impl, ["team"] = team, ["at"] = at }));
    }

    /// <summary>An old record of a member who had one open brief and answered its checkpoint request.</summary>
    private (string Request, string Response, string OldBrief, DateTimeOffset Ts) AnsweredRecordWithAnOpenBrief()
    {
        ArrangeCheckpoint();
        _panes.Default = PaneKnowledge.Present;
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var oldBrief = AskRelease(OpenB);
        var taken = Take();
        var response = CheckpointAnswer(taken, $"Checkpoint: ready branch=ah/feature sha={Sha} inflight=none");
        _sent.Clear();
        Registered(ImplSession, live: true);
        var ts = DateTimeOffset.Parse(RecordOf(taken).Member("ts")!.Value.AsString(), System.Globalization.CultureInfo.InvariantCulture);
        return (MemberRecord(RecordOf(taken)).Member("request")!.Value.AsString(), response, oldBrief, ts);
    }

    private (int Code, JsonValue Result, string Text) ResumeNewest(params string[] extra)
    {
        _callers ??= new RecordingCallers { StartText = SelfStart };
        var (code, stdout, stderr) = Roster(["resume", "--checkpoint", "--team", "proj", "--cwd", Project, "--orchestrator-pid", Self, .. extra]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout) : new JsonObject(), stdout + stderr);
    }

    /// <summary>A live member with one open brief and no checkpoint record.</summary>
    private string OrphanedMemberWithABrief()
    {
        ArrangeCheckpoint();
        _panes.Default = PaneKnowledge.Present;
        Registered(ImplSession, live: true);
        return AskRelease(OpenA);
    }

    [Fact]
    public void ATeamBehindAWindDownWithNoRecordGetsAFreshRecordAndItsOpenBrief()
    {
        var brief = OrphanedMemberWithABrief();
        ChainDownRow("proj", "active", AfterAnyRecord);

        var (code, result, text) = ResumeNewest("--yes");

        Assert.True(code == 0, text);
        Assert.Equal(MsgRows.PathKey(brief), MsgRows.PathKey(Assert.Single(_sent).Ref));
        var record = JsonText.Parse(TestCheckpoints.Read(_sandbox.AhHome, result.Member("checkpoint")!.Value.AsString()));
        Assert.Equal("taken for a chain-down resume", record.Member("note")!.Value.AsString());
        Assert.Null(record.Member("stop"));
        Assert.True(MemberRecord(record).Member("request")!.Value.IsNull);
        Assert.Equal(OpenA, MemberRecord(record).Member("open_requests")!.Value.AsArray().Single().AsString());
    }

    [Fact]
    public void AnOldRecordBehindAWindDownIsNotReplayedEvenAfterTheRowIsCleared()
    {
        var (request, response, oldBrief, ts) = AnsweredRecordWithAnOpenBrief();
        var newBrief = AskRelease(OpenA);
        ChainDownRow("proj", "active", ts.AddMilliseconds(1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        ChainDownRow("proj", "cleared", AfterAnyRecord);

        var (code, result, text) = ResumeNewest("--yes");

        Assert.True(code == 0, text);
        Assert.Equal(new[] { newBrief, oldBrief }.Select(MsgRows.PathKey), _sent.Select(s => MsgRows.PathKey(s.Ref)).ToArray());
        Assert.DoesNotContain(_sent, s => MsgRows.PathKey(s.Ref) == MsgRows.PathKey(response));
        var record = JsonText.Parse(TestCheckpoints.Read(_sandbox.AhHome, result.Member("checkpoint")!.Value.AsString()));
        Assert.Equal(request, record.Member("supersedes")!.Value.AsArray().Single().AsString());
    }

    [Fact]
    public void NegativeARecordNewerThanTheRowIsUsedAsItIs()
    {
        var (_, response, _, ts) = AnsweredRecordWithAnOpenBrief();
        ChainDownRow("proj", "active", ts.AddMinutes(-1).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        var before = CheckpointFiles();

        var (code, _, text) = ResumeNewest("--yes");

        Assert.True(code == 0, text);
        Assert.Equal(before, CheckpointFiles());
        Assert.Equal(MsgRows.PathKey(response), MsgRows.PathKey(Assert.Single(_sent).Ref));
    }

    [Fact]
    public void NegativeANamedRecordIsAlwaysThatRecord()
    {
        OrphanedMemberWithABrief();
        ChainDownRow("proj", "active", AfterAnyRecord);
        var named = WriteRecord(null, open: []);

        var (code, result, text) = ResumeCheckpointOf(named, "--yes");

        Assert.True(code == 0, text);
        Assert.Equal(named, result.Member("checkpoint")!.Value.AsString());
        Assert.Empty(_sent);
    }

    [Theory]
    [InlineData("other", "active", AfterAnyRecord)]
    [InlineData("proj", "cleared", AfterAnyRecord)]
    [InlineData("proj", "suspect", AfterAnyRecord)]
    [InlineData("proj", "released", AfterAnyRecord)]
    [InlineData("proj", "active", "not a time")]
    public void NegativeARowThatDoesNotCountLeavesTheRefusal(string team, string state, string at)
    {
        OrphanedMemberWithABrief();
        ChainDownRow(team, state, at);

        var (code, _, text) = ResumeNewest("--yes");

        Assert.NotEqual(0, code);
        Assert.Contains("no checkpoint for", text, StringComparison.Ordinal);
        Assert.Empty(_sent);
    }

    [Fact]
    public void APlanWritesNothingAndShowsWhatTheRecordWouldHold()
    {
        OrphanedMemberWithABrief();
        ChainDownRow("proj", "active", AfterAnyRecord);
        var before = CheckpointFiles();

        var (code, result, text) = ResumeNewest();

        Assert.True(code == 0, text);
        Assert.Equal("would take", result.Member("record_only")!.Value.AsString());
        Assert.Contains(OpenA, text, StringComparison.Ordinal);
        Assert.Equal(before, CheckpointFiles());
        Assert.Empty(_sent);
    }

    [Fact]
    public void AllResumesAnOwnedTeamBehindAWindDownWithNoRecord()
    {
        var brief = OrphanedMemberWithABrief();
        ChainDownRow("proj", "active", AfterAnyRecord);
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, stdout, stderr) = Roster(["resume", "--checkpoint", "--all", "--cwd", Project, "--orchestrator-pid", Self, "--yes"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(MsgRows.PathKey(brief), MsgRows.PathKey(Assert.Single(_sent).Ref));
    }

    [Fact]
    public void NegativeAllSkipsAnOwnedTeamWithNoRecordAndNoRow()
    {
        OrphanedMemberWithABrief();
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, stdout, stderr) = Roster(["resume", "--checkpoint", "--all", "--cwd", Project, "--orchestrator-pid", Self, "--yes"]);

        Assert.NotEqual(0, code);
        Assert.Contains("no owned team has a checkpoint", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAHeldTeamBehindAWindDownIsNeverTaken()
    {
        ArrangeTree();
        TreeTeam("one", "one-implementor");
        ChainDownRow("one", "active", AfterAnyRecord);
        _callers ??= new RecordingCallers { StartText = SelfStart };
        var before = CheckpointFiles();

        var (singleCode, _, _) = Roster(["resume", "--checkpoint", "--team", "one", "--cwd", Project, "--orchestrator-pid", Self, "--yes"]);
        var (allCode, stdout, stderr) = Roster(["resume", "--checkpoint", "--all", "--cwd", Project, "--orchestrator-pid", Self, "--yes"]);

        Assert.NotEqual(0, singleCode);
        Assert.NotEqual(0, allCode);
        Assert.Contains("no owned team has a checkpoint", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal(before, CheckpointFiles());
    }
}
