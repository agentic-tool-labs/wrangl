using System.Text;
using Ah.Core.Json;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

/// <summary>The Orchestrator's task-sized SendMessage to a member of its team with no brief pointer: one context line, once a turn, never a denial.</summary>
public sealed class BareSendNudgeTests
{
    private static readonly string Task = "Please look at the reviewer's second finding on the stream branch and rework the lock so that two parallel spawns cannot both create the tab, then run the Roster tests and report back. " + new string('x', 20);

    private static string Send(HooksHostTests.Rig rig, string to, string message, string? agentType = null)
    {
        var input = new JsonObject { ["to"] = to, ["message"] = message, ["notify_when_idle"] = true };
        var evt = new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = input,
        };
        if (agentType is not null)
        {
            evt["agent_type"] = agentType;
        }

        var ctx = new GateContext(rig.Services, JsonText.Parse(JsonText.ToUtf8(evt, 0)), "x");
        var resolved = rig.Services.Config.ResolveConfig(rig.Sandbox.Root + "/proj", new Ah.Config.ResolveOptions(SessionId: "s-a"));
        return AutoTrack.BareSendNudge(ctx, input, message, resolved) is { } outcome ? Encoding.UTF8.GetString(outcome.Stdout.Span) : string.Empty;
    }

    private static void NoNudge(string output) => Assert.True(output.Length == 0, output);

    [Fact]
    public void ABareTaskSizedSendToATeamMemberGetsTheNudgeAndIsNotDenied()
    {
        var output = Send(new HooksHostTests.Rig(), "proj-implementor", Task);

        Assert.Contains("auto-filing failed", output, StringComparison.Ordinal);
        Assert.Contains("\"additionalContext\"", output, StringComparison.Ordinal);
        Assert.DoesNotContain("deny", output, StringComparison.Ordinal);
    }

    [Fact]
    public void APointerAShortAckANonMemberRecipientAndASubagentSenderGetNoNudge()
    {
        var rig = new HooksHostTests.Rig();

        NoNudge(Send(rig, "proj-implementor", "[hierarchy-msg /h/msgs/20260101-000000-aa11--implementor--work--request.md]\n" + Task));
        Assert.Equal(string.Empty, Send(rig, "proj-implementor", "merged; stand by"));
        NoNudge(Send(rig, "someone-else", Task));
        NoNudge(Send(rig, "proj-implementor", new string('y', 199)));
    }

    private static void Seed(HooksHostTests.Rig rig, params string[] rows)
    {
        var file = Ah.Core.Text.AhPaths.UserEntry(rig.Sandbox.Home, Ah.Core.Text.AhPaths.PeerPendingFile);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, string.Join("\n", rows) + "\n");
    }

    private const string Nudged = "{\"type\":\"bare-send-nudge\",\"session_id\":\"s-a\",\"ts\":\"2026-01-01T10:00:00.000Z\"}";

    [Fact]
    public void ASessionAlreadyNudgedThisTurnIsNotNudgedAgain()
    {
        var rig = new HooksHostTests.Rig();
        Seed(rig, "{\"type\":\"turn\",\"session_id\":\"s-a\",\"status\":\"start\",\"ts\":\"2026-01-01T09:00:00.000Z\"}", Nudged);

        NoNudge(Send(rig, "proj-implementor", Task));
    }

    [Fact]
    public void ANudgeFromAnEarlierTurnOrAnotherSessionDoesNotSuppressTheNextOne()
    {
        var earlier = new HooksHostTests.Rig();
        Seed(earlier, Nudged, "{\"type\":\"turn\",\"session_id\":\"s-a\",\"status\":\"start\",\"ts\":\"2026-01-01T11:00:00.000Z\"}");
        var other = new HooksHostTests.Rig();
        Seed(other, "{\"type\":\"bare-send-nudge\",\"session_id\":\"s-other\",\"ts\":\"2026-01-01T10:00:00.000Z\"}");

        Assert.Contains("auto-filing failed", Send(earlier, "proj-implementor", Task), StringComparison.Ordinal);
        Assert.Contains("auto-filing failed", Send(other, "proj-implementor", Task), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnyTurnMarkerAnEarlierNudgeDoesNotSilenceTheSession()
    {
        var rig = new HooksHostTests.Rig();
        Seed(rig, Nudged);

        Assert.Contains("auto-filing failed", Send(rig, "proj-implementor", Task), StringComparison.Ordinal);
    }
}
