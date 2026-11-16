namespace Ah.Cli.Tests;

/// <summary>Spawning a sub-orchestrator: the name must read one way, and it runs on claude only.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Fact]
    public void ASubOrchestratorNameThatReadsAsAnotherTeamsSubOrchestratorIsRefused()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("x-so-y")));
        WriteTeam("proj-so-x", SoTeam(OpenStreamJson("y")));

        var (code, refused) = SpawnSo("--stream", "x-so-y");

        Assert.Equal(2, code);
        Assert.Equal("sub-orchestrator-name", refused["refused"].AsString());
        Assert.Contains("ambiguous split", refused["message"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorNameThatReadsOneWayIsNotRefusedAsAmbiguous()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("x-so-y")));
        WriteTeam("proj-so-x", SoTeam(OpenStreamJson("z")));

        var (code, _) = SpawnSo("--stream", "x-so-y");

        Assert.Equal(0, code);
    }

    [Fact]
    public void ASubOrchestratorOnAnotherKindIsRefusedAndOnClaudeIsNot()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, refused) = SpawnSo("--stream", "s1", "--route", "pane", "--kind", "codex");
        var (claudeCode, _) = SpawnSo("--stream", "s1", "--kind", "claude");

        Assert.Equal(2, code);
        Assert.Equal("sub-orchestrator-kind", refused["refused"].AsString());
        Assert.Contains("runs on claude only", refused["message"].AsString(), StringComparison.Ordinal);
        Assert.Equal(0, claudeCode);
    }
}
