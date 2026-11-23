using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>ah2-143: a spawn completes a team record that names no herdr server, and says where the member landed.</summary>
public sealed partial class RosterTeamVerbTests
{
    private (int Code, string Stdout, string Stderr) CoreSpawnInto(string? teamJson, string role = "implementor")
    {
        _sandbox.Executable("bin/hc");
        _extraEnv["PATH"] = _sandbox.Bin;
        _extraEnv["HC_TERMINAL_ID"] = "t1";

        // no herdr-app: the member runs as a hidden terminal
        _runner.Exit = argv => argv.Contains("herdr-app") ? 1 : 0;
        _runner.Output = argv =>
            argv.Contains("ls") ? "{\"hidden_finished\":0,\"terminals\":[]}"
            : argv.Contains("run") ? "{\"address\":\"t7\",\"state\":\"live\"}"
            : argv.Contains("get") ? """{"error":{"code":"agent_not_found"}}"""
            : "{}";
        if (teamJson is not null)
        {
            WriteTeam("proj", teamJson);
        }

        return Roster("spawn-ad-hoc", role, "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--allow-main", "--split");
    }

    [Fact]
    public void ACoreSpawnIntoARecordWithNoTransportCompletesItAndNamesTheMembersTerminal()
    {
        var (code, stdout, stderr) = CoreSpawnInto(LiveTeamJson("herdr", string.Empty).Replace("\"transport\":\"herdr\"", "\"transport\":null", StringComparison.Ordinal));

        Assert.True(code == 0, stdout + stderr);
        var team = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Equal("herdr", team.Member("transport")?.AsString());
        Assert.Equal("core", team.Member("herdr_flavour")?.AsString());
        var next = JsonText.Parse(stdout).Member("next")!.Value;
        Assert.Equal("t7", next.Member("terminal")?.AsString());
        // a hidden terminal (no herdr-app) has no tab, so the launch returned none
        Assert.True(next.Member("tab") is { IsNull: true }, stdout);
    }

    [Fact]
    public void ASpawnNeverOverwritesAFlavourTheRecordAlreadyNames()
    {
        var (code, stdout, stderr) = CoreSpawnInto(LiveTeamJson("herdr", string.Empty).Replace("\"transport\":\"herdr\"", "\"transport\":\"herdr\",\"herdr_flavour\":\"tui\"", StringComparison.Ordinal));

        Assert.True(code == 0, stdout + stderr);
        var team = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Equal("herdr", team.Member("transport")?.AsString());
        Assert.Equal("tui", team.Member("herdr_flavour")?.AsString());
    }

    [Fact]
    public void ATmuxSpawnIntoARecordWithNoTransportRecordsTmuxAndALaterHerdrSpawnLeavesIt()
    {
        OnTmux();
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty).Replace("\"transport\":\"tmux\"", "\"transport\":null", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--allow-main");

        Assert.True(code == 0, stdout + stderr);
        var team = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Equal("tmux", team.Member("transport")?.AsString());
        var row = team.Member("members")!.Value.AsArray().Single();
        Assert.Equal("%7", row.Member("transport_id")?.AsString());
        Assert.Null(row.Member("herdr_flavour"));

        // the next launch is on herdr: the transport the record now names stays
        _ = _extraEnv.Remove("TMUX");
        _ = _extraEnv.Remove("TMUX_PANE");
        AgeSpawnedRows();
        (code, stdout, stderr) = CoreSpawnInto(null, "reviewer");

        Assert.True(code == 0, stdout + stderr);
        team = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Equal("tmux", team.Member("transport")?.AsString());
        var reviewer = team.Member("members")!.Value.AsArray().Single(m => m.Member("name")?.AsString() == "proj-reviewer");
        Assert.Equal("core", reviewer.Member("herdr_flavour")?.AsString());
    }

    [Fact]
    public void ATmuxSpawnIntoARecordThatNamesATransportLeavesIt()
    {
        OnTmux();
        WriteTeam("proj", LiveTeamJson("terminal", string.Empty));

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--allow-main");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("terminal", JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("transport")?.AsString());
    }

    private const string StampedPeer =
        "{\"role\":\"reviewer\",\"name\":\"proj-reviewer\",\"route\":\"peer\",\"transport_id\":\"%2\",\"model\":\"sonnet\",\"herdr_flavour\":\"tui\"}";

    [Fact]
    public void ResyncTreatsARecordWithNoTransportAndAHerdrStampedMemberAsHerdr()
    {
        WriteTeam("proj", LiveTeamJson("herdr", StampedPeer).Replace("\"transport\":\"herdr\"", "\"transport\":null", StringComparison.Ordinal));
        HerdrKnowsPaneOnlyThroughPaneGet(paneExists: true);

        var (code, outText, err) = Roster("resync", "--bind", "{\"proj-reviewer\":\"%9\"}", "--dry-run", "--team", "proj");

        Assert.True(code == 0, err);
        Assert.Null(JsonText.Parse(outText).Member("reason"));
        var row = JsonText.Parse(outText).Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "proj-reviewer");
        Assert.Equal("updated", row.Member("status")!.Value.AsString());
    }

    [Fact]
    public void ResyncOfARecordWithNoTransportAndNoStampedMemberSaysTheRecordNamesNone()
    {
        WriteTeam("proj", LiveTeamJson("herdr", TwoPeers).Replace("\"transport\":\"herdr\"", "\"transport\":null", StringComparison.Ordinal));

        var (code, outText, err) = Roster("resync", "--team", "proj");

        Assert.True(code == 0, err);
        var result = JsonText.Parse(outText);
        Assert.False(result.Member("resynced")!.Value.AsBool());
        Assert.Equal("the team record names no transport", result.Member("reason")!.Value.AsString());
    }

    private bool AgentListAsked() => _runner.Calls.Any(c => c.Contains("agent") && c.Contains("list"));

    [Fact]
    public void StreamStatusOnASessionOnNoHerdrWithAnUntaggedTeamAndNoStampAsksNoAgentList()
    {
        ArrangeMove(null, Project);

        var (code, stdout, stderr) = Roster("stream-status", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.False(AgentListAsked());
        Assert.Null(JsonText.Parse(stdout).Member("warnings"));
    }

    [Fact]
    public void StreamStatusOnATuiSessionWithAnUntaggedTeamStillAsksTheAgentList()
    {
        _extraEnv["HERDR_ENV"] = "1";
        ArrangeMove(null, Project);

        var (code, stdout, stderr) = Roster("stream-status", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.True(AgentListAsked());
    }

    private string? DismissPlanHerdrReason(string member)
    {
        _extraEnv["HC_TERMINAL_ID"] = "t1";
        WriteTeam("proj", LiveTeamJson("herdr", member).Replace("\"transport\":\"herdr\"", "\"transport\":null", StringComparison.Ordinal));

        var (code, stdout, stderr) = Roster("dismiss", "proj-reviewer", "--plan", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        return JsonText.Parse(stdout).Member("sources")?.Member("herdr")?.Member("reason") is { IsText: true } r ? r.AsString() : null;
    }

    [Fact]
    public void DismissOfACoreStampedMemberOnARecordWithNoTransportGivesNoNotHerdrReason()
    {
        var reason = DismissPlanHerdrReason(StampedPeer.Replace("\"tui\"", "\"core\"", StringComparison.Ordinal));

        Assert.NotEqual("team transport is not herdr", reason);
        Assert.NotEqual("team record names no transport", reason);
    }

    [Fact]
    public void DismissOfAnUnstampedMemberOnARecordWithNoTransportSaysTheRecordNamesNone() =>
        Assert.Equal("team record names no transport", DismissPlanHerdrReason(StampedPeer.Replace(",\"herdr_flavour\":\"tui\"", string.Empty, StringComparison.Ordinal)));
}
