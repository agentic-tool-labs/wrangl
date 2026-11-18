using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>ah2-143: a spawn completes a team record that names no herdr server, and says where the member landed.</summary>
public sealed partial class RosterTeamVerbTests
{
    private (int Code, string Stdout, string Stderr) CoreSpawnInto(string teamJson)
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
        WriteTeam("proj", teamJson);
        return Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--allow-main", "--split");
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
}
