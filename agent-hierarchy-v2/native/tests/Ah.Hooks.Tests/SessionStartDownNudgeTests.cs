using System.Text;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>b238 S2.b: an Orchestrator resumed after a restart is told, in one advisory line, how many members of each team it owns are down.</summary>
public sealed class SessionStartDownNudgeTests
{
    private const string Session = "s1";
    private const double GonePid = 9999;

    private static string Start(string source, bool held)
    {
        var f = new OrchestrateCommandTests.Fixture();
        HoldFixture.Orchestrates(f.Rig, Session, "tm");
        var path = f.Rig.Services.Roster.TeamPath(f.Rig.Services.Config.HierarchyDir(f.Rig.Sandbox.Root + "/proj"), "tm");
        var team = JsonText.Parse(File.ReadAllText(path)).AsObject();
        team["members"] = new JsonArray
        {
            new JsonObject { ["name"] = "tm-a", ["role"] = "implementor", ["pid"] = GonePid, ["pid_start"] = "Mon" },
            new JsonObject { ["name"] = "tm-b", ["role"] = "reviewer", ["pid"] = GonePid, ["pid_start"] = "Mon" },
            new JsonObject { ["name"] = "tm-c", ["role"] = "architect", ["pid"] = 8888.0, ["pid_start"] = "Mon" },
        };
        File.WriteAllBytes(path, JsonText.ToUtf8(team, 0));
        if (held)
        {
            HoldFixture.Hold(f.Rig, "tm");
        }

        var plain = f.Services();
        var services = plain with
        {
            Advisory = plain.Advisory! with { Callers = new BindingAttestationTests.Callers(BindingAttestationTests.Orchestrator, processGone: (pid, _) => pid == GonePid) },
        };
        var e = new JsonObject
        {
            ["hook_event_name"] = "SessionStart", ["session_id"] = Session, ["cwd"] = f.Rig.Sandbox.Root + "/proj", ["source"] = source, ["agent_type"] = "wrangl:orchestrator",
        };
        return Encoding.UTF8.GetString(f.Rig.Run("sessionstart", JsonText.Stringify(e, 0), services: () => services).Stdout.Span);
    }

    [Fact]
    public void AResumedOrchestratorIsToldHowManyMembersOfItsTeamAreDown()
    {
        var text = Start("resume", held: false);
        Assert.True(text.Contains("2 members of tm are down after a restart; run ah roster resume --all", StringComparison.Ordinal), text);
    }

    [Theory]
    [InlineData("startup", false)] // not a resume
    [InlineData("resume", true)] // a held team is the user's
    public void NegativeNoDownLineOutsideAResumeOrForAHeldTeam(string source, bool held) =>
        Assert.DoesNotContain("are down after a restart", Start(source, held), StringComparison.Ordinal);
}
