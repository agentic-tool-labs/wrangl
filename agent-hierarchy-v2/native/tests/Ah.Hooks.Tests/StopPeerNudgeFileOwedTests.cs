using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>A member's response written this turn and never sent blocks its stop, found from the message files whatever carried the brief.</summary>
public sealed class StopPeerNudgeFileOwedTests
{
    private const string Session = "sess-m";
    private const string Id = "20261011-110000-aa01";

    private static (HooksHostTests.Rig Rig, string Request, string Response) Arrange(DateTime? responseWritten = null)
    {
        var rig = AutoTrackTests.MemberRig("p1");
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/msgs";
        _ = Directory.CreateDirectory(dir);
        var request = $"{dir}/{Id}--implementor--lock--request.md";
        File.WriteAllText(
            request,
            $"---\nid: {Id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: lock\nparent: null\nreason: null\neta: null\nto_name: implementor-1\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:00:00+00:00\n---\n\n## [0] tldr\n- x\n");
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        File.AppendAllText(
            pending,
            Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject { ["type"] = "turn", ["session_id"] = Session, ["status"] = "armed", ["ts"] = Ah.Core.Time.Timestamps.ToIsoString(DateTimeOffset.UtcNow.AddMinutes(-1)) },
                0)) + "\n");
        var response = $"{dir}/{Id}--orchestrator--lock--response.md";
        File.WriteAllText(response, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: lock\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:30:00+00:00\n---\n\n## [0] tldr\n- done\n");
        if (responseWritten is { } at)
        {
            File.SetLastWriteTimeUtc(response, at);
        }

        return (rig, request, response);
    }

    private static string Stop(HooksHostTests.Rig rig, ReportState state = ReportState.WrittenNotSent)
    {
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig), new StopPeerNudgeReportTests.FakeHooks(state, false), new BindingAttestationTests.Callers([]), null!, null!, StopDirective.Instance, null!, null!, new BindingAttestationTests.Daemon(), null!, null!);
        var services = rig.Services with { Advisory = advisory };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj", ["agent_type"] = "wrangl:implementor" };
        var outcome = rig.Run("stop-peer-nudge", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    private static void Seed(HooksHostTests.Rig rig, JsonObject row) =>
        File.AppendAllText(rig.Services.Hierarchy.Peers.PeerPendingPath(), Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");

    [Fact]
    public void AResponseWrittenThisTurnAndNeverSentBlocksTheStopWithTheSendCommand()
    {
        var (rig, _, response) = Arrange();

        var output = Stop(rig);

        var id = Ah.Core.Json.MsgPointer.Id(response);
        Assert.Contains("you filed response " + id + " but did not send it", output, StringComparison.Ordinal);
        Assert.Contains("send orchestrator@tm --kind report --ref " + id + " ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnfilledPlaceholderResponseIsNotOwed()
    {
        var (rig, _, response) = Arrange();
        File.WriteAllText(
            response,
            $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: lock\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:30:00+00:00\n---\n\n## [0] tldr\n\n## [1] status\n- Tag decisions, deviations, roadblocks, outcomes and details as \"Decision: …\", \"Deviation: …\", \"Roadblock: …\", \"Outcome: …\", \"Detail: …\"; tagged lines are journaled.\n");

        Assert.Equal(string.Empty, Stop(rig));
    }

    private static void SeedOwedRow(HooksHostTests.Rig rig, string request, int nudges = 0) =>
        Seed(rig, new JsonObject { ["session_id"] = Session, ["from"] = "orchestrator@tm", ["from_name"] = "orchestrator", ["reply_to"] = JsonValue.Null, ["task"] = Id, ["status"] = "pending", ["nudges"] = nudges, ["msg"] = request, ["ts"] = "2026-10-11T11:00:00.000Z" });

    [Fact]
    public void AnOwedBriefWhoseStoredResponseIsStillTheTemplateSaysSoWithNoFile()
    {
        var (rig, request, _) = Arrange();
        var response = Directory.EnumerateFiles(Path.GetDirectoryName(request)!, "*--response.md").SingleOrDefault() ?? Path.ChangeExtension(request, null).Replace("--request", "--response", StringComparison.Ordinal) + ".md";
        File.WriteAllText(response, $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: lock\n---\n");
        Ah.TestKit.TestMsgStore.DropFiles(rig.Sandbox.Root + "/ah", rig.Sandbox.Root);
        SeedOwedRow(rig, request);

        Assert.Contains("is still the template", Stop(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOwedBriefWhoseResponseFileIsStillTheTemplateSaysSoAndSpendsTheBudget()
    {
        var (rig, request, response) = Arrange();
        File.WriteAllText(
            response,
            $"---\nid: {Id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: lock\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: tm\ncreated: 2026-10-11T11:30:00+00:00\n---\n\n## [0] tldr\n\n## [1] status\n- Tag decisions, deviations, roadblocks, outcomes and details as \"Decision: …\", \"Deviation: …\", \"Roadblock: …\", \"Outcome: …\", \"Detail: …\"; tagged lines are journaled.\n");
        SeedOwedRow(rig, request);

        var output = Stop(rig);

        Assert.Contains("is still the template", output, StringComparison.Ordinal);
        Assert.Contains("response " + Ah.Core.Json.MsgPointer.Id(response) + " is still", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOwedBriefWithNoResponseFileKeepsTheWriteThenSendLine()
    {
        var (rig, request, response) = Arrange();
        File.Delete(response);
        SeedOwedRow(rig, request);

        var output = Stop(rig);

        Assert.Contains("then SEND it", output, StringComparison.Ordinal);
        Assert.DoesNotContain("blank template", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilledResponseOnAnOwedBriefDoesNotSayTemplate()
    {
        var (rig, request, _) = Arrange();
        SeedOwedRow(rig, request);

        Assert.DoesNotContain("blank template", Stop(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void AResponseWhoseReportIsInTheOrchestratorsBoxIsSilent()
    {
        var (rig, _, _) = Arrange();

        Assert.Equal(string.Empty, Stop(rig, ReportState.Sent));
    }

    [Fact]
    public void AResponseWrittenBeforeThisTurnIsSilent()
    {
        var (rig, _, _) = Arrange(responseWritten: DateTime.UtcNow.AddHours(-2));

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void AResponseSentByATokenedSendMessageIsSilent()
    {
        var (rig, _, response) = Arrange();
        Seed(rig, new JsonObject { ["type"] = "sent", ["session_id"] = Session, ["msg_id"] = "m1", ["to"] = "orchestrator", ["response"] = response });

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void ARequestAddressedToAnotherMemberIsNotOwed()
    {
        var (rig, request, _) = Arrange();
        File.WriteAllText(request, File.ReadAllText(request).Replace("to_name: implementor-1", "to_name: implementor-2", StringComparison.Ordinal));

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void TheNudgeSpendsItsBudgetThenWaives()
    {
        var (rig, _, _) = Arrange();
        void Spend() => Seed(rig, new JsonObject { ["type"] = "file-nudge", ["session_id"] = Session, ["task"] = "report:" + Id, ["ts"] = "2026-10-24T00:00:00.000Z" });

        for (var i = 0; i < HierarchyModule.MaxNudges - 1; i++)
        {
            Spend();
        }

        Assert.NotEqual(string.Empty, Stop(rig));
        Spend();
        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void AnObligationAlreadyOwedByARowIsListedOnceAndNotAsAFileOwed()
    {
        var (rig, request, _) = Arrange();
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        File.AppendAllText(
            pending,
            Encoding.UTF8.GetString(JsonText.ToUtf8(
                new JsonObject { ["session_id"] = Session, ["from"] = "orchestrator@tm", ["from_name"] = "orchestrator", ["reply_to"] = JsonValue.Null, ["task"] = Id, ["status"] = "pending", ["nudges"] = 0, ["msg"] = request, ["ts"] = "2026-10-11T11:00:00.000Z" },
                0)) + "\n");

        var output = Stop(rig);

        Assert.NotEqual(string.Empty, output);
        Assert.DoesNotContain("you wrote", output, StringComparison.Ordinal);
    }
}
