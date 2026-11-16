using System.Text;
using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>A SendMessage report whose response token names a file that is still the blank template is refused and resolves nothing.</summary>
public sealed class SendMessageTemplateReportTests
{
    private const string Id = "20261011-110000-aa01";

    private const string Header = $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: lock\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:30:00+00:00\n---\n\n";

    private const string Template = Header + "## [0] tldr\n\n## [1] status\n- Tag decisions, deviations, roadblocks, outcomes and details as \"Decision: …\", \"Deviation: …\", \"Roadblock: …\", \"Outcome: …\", \"Detail: …\"; tagged lines are journaled.\n";

    private const string Filled = Header + "## [0] tldr\n- done\n\n## [1] status\n- [1] status: DONE lock\n";

    private static (HooksHostTests.Rig Rig, string Response) Arrange(string responseText)
    {
        var rig = AutoTrackTests.MemberRig("p1");
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/msgs";
        _ = Directory.CreateDirectory(dir);
        var request = $"{dir}/{Id}--implementor--lock--request.md";
        File.WriteAllText(
            request,
            $"---\nid: {Id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: lock\nparent: null\nreason: null\neta: null\nto_name: implementor-1\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:00:00+00:00\n---\n\n## [0] tldr\n- x\n");
        var response = $"{dir}/{Id}--orchestrator--lock--response.md";
        File.WriteAllText(response, responseText);
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        File.AppendAllText(
            pending,
            $"{{\"task\":\"lock\",\"ts\":\"2026-10-11T11:00:00.000Z\",\"session_id\":\"s-a\",\"status\":\"pending\",\"msg\":\"{request}\",\"from\":\"orchestrator\",\"from_name\":\"orchestrator\"}}\n");
        return (rig, response);
    }

    private static string Pre(HooksHostTests.Rig rig, string message)
    {
        var evt = new JsonObject
        {
            ["hook_event_name"] = "PreToolUse",
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["agent_type"] = "wrangl:implementor",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = new JsonObject { ["to"] = "orchestrator", ["message"] = message },
        };
        return Encoding.UTF8.GetString(rig.Run("pretooluse-sendmessage-response", Encoding.UTF8.GetString(JsonText.ToUtf8(evt, 0)), gate: true).Stdout.Span);
    }

    private static bool Resolves(HooksHostTests.Rig rig, string message)
    {
        var evt = new JsonObject
        {
            ["hook_event_name"] = "PostToolUse",
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["agent_type"] = "wrangl:implementor",
            ["tool_name"] = "SendMessage",
            ["tool_input"] = new JsonObject { ["to"] = "orchestrator", ["message"] = message },
            ["tool_response"] = new JsonObject { ["success"] = true, ["msg_id"] = "m1" },
        };
        var services = rig.Services with { Advisory = StopOrchestratorLivenessTests.Advisory(rig, []) };
        _ = rig.Run("posttooluse-peer-resolve", Encoding.UTF8.GetString(JsonText.ToUtf8(evt, 0)), services: () => services);
        return rig.Writer.Arguments.Any(a => a.Contains("\"status\":\"resolved\"", StringComparison.Ordinal));
    }

    [Fact]
    public void ATokenNamingAFilledFileIsAllowedAndResolves()
    {
        var (rig, response) = Arrange(Filled);
        var message = $"[hierarchy-msg {response}]\n- [1] status: DONE";

        Assert.Equal(string.Empty, Pre(rig, message));
        Assert.True(Resolves(rig, message));
    }

    [Fact]
    public void AnEmptyStatusBulletIsStillTheTemplate()
    {
        var (rig, response) = Arrange(Template + "- [1] status:\n");

        Assert.Contains("is still the template", Pre(rig, $"[hierarchy-msg {response}]\n- [1] status: DONE"), StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedReportFileIsNotRefusedAsATemplate()
    {
        var (rig, response) = Arrange(Filled.Replace($"id: {Id}", "id: 20990101-000000-zzzz", StringComparison.Ordinal));

        var denied = Pre(rig, $"[hierarchy-msg {response}]\n- [1] status: DONE");

        Assert.DoesNotContain("is still the template", denied, StringComparison.Ordinal);
        Assert.Contains("does not satisfy an open request", denied, StringComparison.Ordinal);
        Assert.Contains("[ah-nudge] NOT DONE: ", denied, StringComparison.Ordinal);
    }

    [Fact]
    public void ABlankTemplateReportIsDeniedWithoutTheNudgeMarker()
    {
        var (rig, response) = Arrange(Template + "- [1] status:\n");

        var denied = Pre(rig, $"[hierarchy-msg {response}]\n- [1] status: DONE");

        Assert.Contains("is still the template", denied, StringComparison.Ordinal);
        Assert.DoesNotContain("[ah-nudge]", denied, StringComparison.Ordinal);
    }

    [Fact]
    public void ASendWithoutATokenIsNotTouchedByTheTemplateRule()
    {
        var (rig, _) = Arrange(Template);

        Assert.Equal(string.Empty, Pre(rig, "still working, nothing to report yet"));
    }

    [Fact]
    public void AReportPastTheEchoCapBouncesInlineReportOnceThenTheRetryPasses()
    {
        var (rig, response) = Arrange(Filled);
        rig.Writer.Executing = [.. rig.Writer.Executing ?? [], .. rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "gates")];
        var message = $"[hierarchy-msg {response}]\n" + new string('x', 2500);

        var first = Pre(rig, message);

        Assert.Contains("[ah-nudge] NOT DONE: ", first, StringComparison.Ordinal);
        Assert.Contains("\\nFix: ", first, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Pre(rig, message));
        Assert.Equal(string.Empty, Pre(rig, $"[hierarchy-msg {response}]\n- [1] status: DONE"));
    }

    [Fact]
    public void ATemplateTokenDoesNotResolveThePendingRow()
    {
        var (rig, response) = Arrange(Template);

        Assert.False(Resolves(rig, $"[hierarchy-msg {response}]\n- [1] status: DONE"));
    }
}
