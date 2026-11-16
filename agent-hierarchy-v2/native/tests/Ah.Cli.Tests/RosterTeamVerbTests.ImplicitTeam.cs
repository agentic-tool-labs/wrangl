namespace Ah.Cli.Tests;

public sealed partial class RosterTeamVerbTests
{
    /// <summary>ah2-34: the empty team a session start gave the Orchestrator is no team to disband first; any other empty live team still is.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ACreatePlanGoesAheadOverTheCallersOwnEmptyImplicitTeam(bool isImplicit)
    {
        var file = Path.Combine(_sandbox.Home, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"enabled":true,"roster":{"route":"peer","members":[{"role":"implementor","model":"sonnet"}]}}""");
        WriteTeam(
            "proj",
            "{\"version\":1,\"team_id\":\"t-implicit\",\"created\":\"" + DateTimeOffset.UtcNow.ToString("o") + "\","
            + "\"orchestrator\":{\"session_id\":null,\"pid\":" + Self + ",\"pid_start\":\"" + SelfStart + "\"},\"members\":[]"
            + (isImplicit ? ",\"implicit\":true" : string.Empty) + "}");

        var (code, stdout, stderr) = Roster("create", "--plan", "--team", "proj", "--orchestrator-pid", Self);

        if (isImplicit)
        {
            Assert.True(code == 0, stdout + stderr);
        }
        else
        {
            Assert.NotEqual(0, code);
            Assert.Contains("a live Team", stdout + stderr, StringComparison.Ordinal);
        }
    }
}
