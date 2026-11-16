using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    private static string ConsentEventIn(Rig rig, string answer)
    {
        var question = ConfigModule.MemberLaunchConsentQuestion;
        return JsonText.Stringify(new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["session_id"] = "sess-1",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["tool_use_id"] = "toolu_1",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = new JsonObject
            {
                ["metadata"] = new JsonObject { ["source"] = "ah:R7;consent=member-launch-rules;cv=2" },
                ["questions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["question"] = question,
                        ["header"] = "Member rules",
                        ["multiSelect"] = false,
                        ["options"] = new JsonArray
                        {
                            new JsonObject { ["label"] = "Allow", ["description"] = "d" },
                            new JsonObject { ["label"] = "Not now", ["description"] = "d" },
                        },
                    },
                },
            },
            ["tool_response"] = new JsonObject { ["answers"] = new JsonObject { [question] = answer } },
        }, 0);
    }

    /// <summary>
    /// The Orchestrator's cwd (/proj) is in one repository and its team in another. The team's orchestrator record has no start
    /// time, the Orchestrator's registration is bound with no start time and holds the box directory the team file maps to, and a
    /// member registered from the team's repository leads to it.
    /// </summary>
    private static string SeedForeignTeam(GateServices s, IMailbox mailbox, string teamRepo, string team, string? boxDir = null, string teamSession = "sess-1")
    {
        Directory.CreateDirectory(Path.Combine(teamRepo, ".git"));
        var dir = s.Config.HierarchyDir(teamRepo);
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        var teamPath = s.Roster.TeamPath(dir, team);
        File.WriteAllText(teamPath, JsonText.Stringify(new JsonObject
        {
            ["members"] = new JsonArray(),
            ["orchestrator"] = new JsonObject { ["pid"] = AskProcessTable.Owner, ["session_id"] = teamSession },
        }, 0));
        var orchestrator = mailbox.RegistrationFile("orch-" + team);
        Directory.CreateDirectory(Path.GetDirectoryName(orchestrator)!);
        File.WriteAllText(orchestrator, JsonText.Stringify(new JsonObject
        {
            ["address"] = "orchestrator@" + team,
            ["boxes"] = new JsonObject { ["orchestrator@" + team] = boxDir ?? mailbox.BoxDirFor(teamPath, "orchestrator") },
            ["pid"] = AskProcessTable.Owner,
            ["pid_start"] = JsonValue.Null,
            ["bound"] = true,
            ["session_id"] = "sess-1",
            ["cwd"] = Path.GetDirectoryName(teamRepo)! + "/proj",
        }, 0));
        var member = mailbox.RegistrationFile("member-" + team);
        File.WriteAllText(member, JsonText.Stringify(new JsonObject
        {
            ["address"] = "m@" + team,
            ["boxes"] = new JsonObject(),
            ["pid"] = 4300,
            ["pid_start"] = "Sat Oct 10 09:00:00 2026",
            ["bound"] = true,
            ["session_id"] = "member-session",
            ["cwd"] = teamRepo,
        }, 0));
        return dir;
    }

    private static string ConsentSaid(HookOutcome outcome) => Encoding.UTF8.GetString(outcome.Stdout.Span);

    [Fact]
    public void ConsentGivenByAnOrchestratorWhoseTeamsLiveInAnotherRepoIsRecordedInTheTeamHomeAndTheGateSeesIt()
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var dir = SeedForeignTeam(services, mailbox, rig.Sandbox.Root + "/repo-y", "y");

        var outcome = rig.Run("posttooluse-ask-record", ConsentEventIn(rig, "Allow"), services: () => services);

        Assert.True(ConfigModule.EvaluateMemberLaunchConsent(services.Files, Path.Combine(dir, "asks.jsonl"), enabled: true).Effective);
        Assert.False(File.Exists(Path.Combine(services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "asks.jsonl")));
        Assert.Contains("ah: consent recorded in " + dir, ConsentSaid(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain("NOT recorded", ConsentSaid(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void AHeldBoxThatIsNotTheTeamFilesBoxDoesNotMakeTheSessionItsOrchestratorAndTheHookSaysNoTeamWasFound()
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var dir = SeedForeignTeam(services, mailbox, rig.Sandbox.Root + "/repo-y", "y", boxDir: rig.Sandbox.Root + "/elsewhere/orchestrator");

        var outcome = rig.Run("posttooluse-ask-record", ConsentEventIn(rig, "Allow"), services: () => services);

        Assert.False(File.Exists(Path.Combine(dir, "asks.jsonl")));
        Assert.Contains("ah: consent NOT recorded: no team found", ConsentSaid(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void ARegistrationWithNoStartTimeDoesNotProveASessionWhoseIdIsNotTheTeamFilesOrchestrators()
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var dir = SeedForeignTeam(services, mailbox, rig.Sandbox.Root + "/repo-y", "y", teamSession: "someone-else");

        var outcome = rig.Run("posttooluse-ask-record", ConsentEventIn(rig, "Allow"), services: () => services);

        Assert.False(File.Exists(Path.Combine(dir, "asks.jsonl")));
        Assert.Contains("ah: consent NOT recorded: no team found", ConsentSaid(outcome), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Not now")]
    [InlineData("Allowance")]
    public void ADeclinedConsentAnswerIsRecordedAsDeclinedAndTheHookSaysItWasNotRecordedAsConsent(string answer)
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var dir = SeedForeignTeam(services, mailbox, rig.Sandbox.Root + "/repo-y", "y");

        var outcome = rig.Run("posttooluse-ask-record", ConsentEventIn(rig, answer), services: () => services);

        Assert.False(ConfigModule.EvaluateMemberLaunchConsent(services.Files, Path.Combine(dir, "asks.jsonl"), enabled: true).Effective);
        Assert.Contains("ah: consent NOT recorded: the answer is not Allow", ConsentSaid(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void ATypedAllowAnswerIsRecordedAsConsent()
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        var dir = SeedForeignTeam(services, mailbox, rig.Sandbox.Root + "/repo-y", "y");

        var outcome = rig.Run("posttooluse-ask-record", ConsentEventIn(rig, "Allow, again...sigh"), services: () => services);

        Assert.True(ConfigModule.EvaluateMemberLaunchConsent(services.Files, Path.Combine(dir, "asks.jsonl"), enabled: true).Effective);
        Assert.Contains("ah: consent recorded in", ConsentSaid(outcome), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAskWithoutTheConsentTagStaysSilent()
    {
        var (rig, services, mailbox) = CrossRepoAskRig();
        _ = SeedAskTeam(services, mailbox, rig.Sandbox.Root + "/repo-b", "b");

        var outcome = rig.Run("posttooluse-ask-record", AskEventIn(rig), services: () => services);

        Assert.True(outcome.Stdout.IsEmpty);
    }
}
