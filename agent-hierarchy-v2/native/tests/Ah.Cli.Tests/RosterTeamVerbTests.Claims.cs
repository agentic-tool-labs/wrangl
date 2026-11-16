using System.Globalization;
using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>
/// What resume and adopt do with each shape of a team record's <c>orchestrator</c>: no claim at all is taken unchecked, any other
/// claim is judged by the one verdict, and a record that cannot be read is a claim (indeterminate), never "no claim".
/// </summary>
public sealed partial class RosterTeamVerbTests
{
    private const string Absent = "<absent>";

    private const string ClaimedStart = "2026-10-15T21:00:00.000Z";

    private void WriteRawOrchestrator(string home, string orchestratorJson)
    {
        var path = Path.Combine(home, "teams", "proj.json");
        var doc = JsonText.Parse(_sandbox.TeamText(path)).AsObject();
        if (orchestratorJson == Absent)
        {
            _ = doc.Remove("orchestrator");
        }
        else
        {
            doc["orchestrator"] = JsonText.Parse(orchestratorJson);
        }

        _sandbox.PlantTeam(path, JsonText.Stringify(doc, 0));
    }

    private void WriteRawAdoptTeam(string orchestratorJson) =>
        WriteTeam("proj", "{\"version\":1,\"team_id\":\"t-1\",\"transport\":\"terminal\",\"roster_level\":\"repo\"" + (orchestratorJson == Absent ? string.Empty : ",\"orchestrator\":" + orchestratorJson) + ",\"members\":[]}");

    private JsonValue AdoptedOrchestrator() => JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("orchestrator") ?? JsonValue.Null;

    [Theory]
    [InlineData(Absent)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"pid\":null}")]
    [InlineData("{\"pid\":0}")]
    [InlineData("{\"session_id\":\"\",\"pid\":null}")]
    public void AR1AResumeTakesATeamWhoseOrchestratorMakesNoClaimWithoutForce(string orchestrator)
    {
        var (home, snapshot) = ResumeReady("ar1");
        WriteRawOrchestrator(home, orchestrator);

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
    }

    [Theory]
    [InlineData(Absent)]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"pid\":null}")]
    [InlineData("{\"pid\":0}")]
    public void AR1AdoptTakesATeamWhoseOrchestratorMakesNoClaimWithoutForce(string orchestrator)
    {
        _callers = new RecordingCallers { Started = Started };
        WriteRawAdoptTeam(orchestrator);

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        Assert.Equal(Environment.ProcessId, (int)AdoptedOrchestrator().Member("pid")!.Value.AsNumber());
    }

    private const string ReleasedDeadOwner = "{\"session_id\":null,\"pid\":99999,\"pid_start\":\"Mon Jan  1 00:00:00 2001\",\"released\":{\"pid\":99999,\"pid_start\":\"Mon Jan  1 00:00:00 2001\",\"at\":\"2026-10-24T12:00:00.000Z\"}}";

    [Fact]
    public void T7AdoptDropsTheReleasedMarkerFromTheOrchestratorItWrites()
    {
        _callers = new RecordingCallers { Started = Started };
        WriteRawAdoptTeam(ReleasedDeadOwner);

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        Assert.Equal(Environment.ProcessId, (int)AdoptedOrchestrator().Member("pid")!.Value.AsNumber());
        Assert.Null(AdoptedOrchestrator().Member("released"));
    }

    [Fact]
    public void T7ResumeDropsTheReleasedMarkerFromTheOrchestratorItWrites()
    {
        var (home, snapshot) = ResumeReady("t7");
        WriteRawOrchestrator(home, ReleasedDeadOwner);

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
        Assert.Null(RecordedOrchestrator(home).Get("released"));
    }

    [Fact]
    public void AR2AResumeRefusesASessionOnlyClaimNothingProvesGoneUnlessForced()
    {
        var (home, snapshot) = ResumeReady("ar2");
        WriteRawOrchestrator(home, "{\"session_id\":\"s-other\"}");

        var (code, stdout, stderr) = ResumeAs(snapshot);
        var kept = RecordedOrchestrator(home)["session_id"].AsString();
        var (forcedCode, forcedOut, forcedErr) = ResumeAs(snapshot, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("cannot be proven gone", stdout + stderr, StringComparison.Ordinal);
        Assert.Contains("resume --force", stdout + stderr, StringComparison.Ordinal);
        Assert.Equal("s-other", kept);
        Assert.True(forcedCode == 0, forcedOut + forcedErr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
    }

    [Fact]
    public void AR2AAdoptRefusesASessionOnlyClaimNothingProvesGoneUnlessForced()
    {
        _callers = new RecordingCallers { Started = Started };
        WriteRawAdoptTeam("{\"session_id\":\"s-other\"}");

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);
        var kept = AdoptedOrchestrator().Member("session_id")!.Value.AsString();
        var (forcedCode, _, forcedErr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("cannot be proven gone", stderr, StringComparison.Ordinal);
        Assert.Contains("adopt --force", stderr, StringComparison.Ordinal);
        Assert.Equal("s-other", kept);
        Assert.True(forcedCode == 0, forcedErr);
        Assert.Equal(Environment.ProcessId, (int)AdoptedOrchestrator().Member("pid")!.Value.AsNumber());
    }

    [Fact]
    public void AR3AResumeRefusesAClaimOfAProvenLiveSessionEvenWhenForced()
    {
        var (home, snapshot) = ResumeReady("ar3");
        var holder = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder) ? ClaimedStart + "\n" : SelfStartOnly(argv);
        WriteRawOrchestrator(home, "{\"session_id\":\"s-other\",\"pid\":" + holder + ",\"pid_start\":\"" + ClaimedStart + "\"}");

        var (code, stdout, stderr) = ResumeAs(snapshot);
        var (forcedCode, forcedOut, forcedErr) = ResumeAs(snapshot, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("held by a live Orchestrator", stdout + stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, forcedCode);
        Assert.Contains("held by a live Orchestrator", forcedOut + forcedErr, StringComparison.Ordinal);
        Assert.Equal("s-other", RecordedOrchestrator(home)["session_id"].AsString());
    }

    [Fact]
    public void AR3AAdoptRefusesAClaimOfAProvenLiveSessionEvenWhenForced()
    {
        _callers = new RecordingCallers { Started = Started };
        var holder = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder) ? ClaimedStart + "\n" : SelfStartOnly(argv);
        WriteRawAdoptTeam("{\"session_id\":\"s-other\",\"pid\":" + holder + ",\"pid_start\":\"" + ClaimedStart + "\"}");

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);
        var (forcedCode, _, forcedErr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("refusing to hijack a live team", stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, forcedCode);
        Assert.Contains("refusing to hijack a live team", forcedErr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"pid\":\"abc\"}")]
    [InlineData("{\"pid\":-1.5}")]
    [InlineData("\"not-an-object\"")]
    public void AR4AResumeTreatsAMalformedClaimAsAClaimNotAsNone(string orchestrator)
    {
        var (home, snapshot) = ResumeReady("ar4");
        WriteRawOrchestrator(home, orchestrator);

        var (code, stdout, stderr) = ResumeAs(snapshot);
        var (forcedCode, forcedOut, forcedErr) = ResumeAs(snapshot, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("cannot be proven gone", stdout + stderr, StringComparison.Ordinal);
        Assert.True(forcedCode == 0, forcedOut + forcedErr);
    }

    [Theory]
    [InlineData("{\"pid\":\"abc\"}")]
    [InlineData("{\"pid\":-1.5}")]
    [InlineData("\"not-an-object\"")]
    public void AR4AAdoptTreatsAMalformedClaimAsAClaimNotAsNone(string orchestrator)
    {
        _callers = new RecordingCallers { Started = Started };
        WriteRawAdoptTeam(orchestrator);

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);
        var (forcedCode, _, forcedErr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self, "--force");

        Assert.NotEqual(0, code);
        Assert.Contains("cannot be proven gone", stderr, StringComparison.Ordinal);
        Assert.True(forcedCode == 0, forcedErr);
    }

    [Fact]
    public void AR5AResumeTakesATeamTheInvokerOwnsByItsPidAndStartEvenThoughTheVerdictSaysLive()
    {
        var (home, snapshot) = ResumeReady("ar5");
        WriteRawOrchestrator(home, "{\"session_id\":\"sess-self\",\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\"}");

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
    }

    [Fact]
    public void NegativeAR5AResumeDoesNotTreatAnotherSessionsPidAsTheInvokersOwn()
    {
        var (home, snapshot) = ResumeReady("ar5n");
        var holder = LivePidOfAnotherProcess().ToString(CultureInfo.InvariantCulture);
        _runner.Ps = argv => argv.Contains("lstart=") && argv.Contains(holder) ? ClaimedStart + "\n" : SelfStartOnly(argv);
        WriteRawOrchestrator(home, "{\"session_id\":\"sess-self\",\"pid\":" + holder + ",\"pid_start\":\"" + ClaimedStart + "\"}");

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.NotEqual(0, code);
        Assert.Contains("held by a live Orchestrator", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AR5AAdoptTakesATeamWhoseClaimIsTheInvokersOwnSessionWithoutAskingTheVerdict()
    {
        _callers = new RecordingCallers { Started = Started };
        _callers.Bound.Add(new BoundSession("sess-self", DateTimeOffset.UtcNow));
        WriteRawAdoptTeam("{\"session_id\":\"sess-self\"}");

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        Assert.Equal(Environment.ProcessId, (int)AdoptedOrchestrator().Member("pid")!.Value.AsNumber());
    }

    [Fact]
    public void AR6AResumeTakesAClaimWhoseProcessIsProvenGone()
    {
        var (home, snapshot) = ResumeReady("ar6");
        ObserverSeesAParent();
        WriteRawOrchestrator(home, "{\"session_id\":null,\"pid\":99999999,\"pid_start\":\"" + ClaimedStart + "\"}");

        var (code, stdout, stderr) = ResumeAs(snapshot);

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(Environment.ProcessId, (int)RecordedOrchestrator(home)["pid"].AsNumber());
    }

    [Fact]
    public void AR6AAdoptTakesAClaimWhoseProcessIsProvenGone()
    {
        _callers = new RecordingCallers { Started = Started };
        ObserverSeesAParent();
        WriteRawAdoptTeam("{\"session_id\":null,\"pid\":99999999,\"pid_start\":\"" + ClaimedStart + "\"}");

        var (code, _, stderr) = Roster("adopt", "--team", "proj", "--orchestrator-pid", Self);

        Assert.True(code == 0, stderr);
        Assert.Equal(Environment.ProcessId, (int)AdoptedOrchestrator().Member("pid")!.Value.AsNumber());
    }
}
