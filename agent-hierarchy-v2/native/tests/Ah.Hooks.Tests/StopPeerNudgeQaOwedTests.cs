using System.Text;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>An implement-class DONE owes a QA dispatch: the Orchestrator's stop is blocked until a QA request for the slug is filed, within a bounded budget.</summary>
public sealed class StopPeerNudgeQaOwedTests
{
    private const string Session = "sess-o";
    private const string Id = "20261011-110000-aa01";

    private static string Msg(string id, string type, string to, string from, string slug, string created, string extra, string body) =>
        $"---\nid: {id}\ntype: {type}\nto: {to}\nfrom: {from}\nslug: {slug}\nparent: null\nreason: null\neta: null\nto_name: {(type == "request" ? to + "-1" : "null")}\nfrom_name: null\nteam: tm\ncreated: {created}\n{extra}---\n\n{body}\n";

    private static (HooksHostTests.Rig Rig, string Dir) Arrange(string role = "implementor", string status = "DONE", string requestExtra = "")
    {
        var rig = new HooksHostTests.Rig();
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/msgs";
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(
            $"{dir}/{Id}--{role}--lock--request.md",
            Msg(Id, "request", role, "orchestrator", "lock", "2026-10-11T11:00:00+00:00", requestExtra, "## [0] tldr\n- x"));
        File.WriteAllText(
            $"{dir}/{Id}--orchestrator--lock--response.md",
            Msg(Id, "response", "orchestrator", role, "lock", "2026-10-11T11:30:00+00:00", string.Empty, $"## [0] tldr\n- [1] status: {status}, commit abc\n"));
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        Seed(rig, new JsonObject { ["type"] = "turn", ["session_id"] = Session, ["status"] = "armed", ["ts"] = "2026-10-11T10:00:00+00:00" });
        return (rig, dir);
    }

    private static void Seed(HooksHostTests.Rig rig, JsonObject row) =>
        File.AppendAllText(rig.Services.Hierarchy.Peers.PeerPendingPath(), Encoding.UTF8.GetString(JsonText.ToUtf8(row, 0)) + "\n");

    private static string Stop(HooksHostTests.Rig rig)
    {
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig), new StopPeerNudgeReportTests.FakeHooks(ReportState.NotWritten, false), new BindingAttestationTests.Callers([]), null!, null!, StopDirective.Instance, null!, null!, new BindingAttestationTests.Daemon(), null!, null!);
        var services = rig.Services with { Advisory = advisory };
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj" };
        var outcome = rig.Run("stop-peer-nudge", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    private static void FileQa(string dir, string slug, string created) =>
        File.WriteAllText($"{dir}/20261011-120000-bb02--qa--{slug}--request.md", Msg("20261011-120000-bb02", "request", "qa", "orchestrator", slug, created, string.Empty, "## [0] tldr\n- set full"));

    [Fact]
    public void AnImplementorDoneWithNoQaRequestBlocksTheStopWithTheMergeAndRequestCommands()
    {
        var (rig, _) = Arrange();

        var output = Stop(rig);

        Assert.Contains("reported DONE on lock", output, StringComparison.Ordinal);
        Assert.Contains("git merge --no-ff", output, StringComparison.Ordinal);
        Assert.Contains("ah/ship/lock", output, StringComparison.Ordinal);
        Assert.Contains("new --type request --to qa --slug lock", output, StringComparison.Ordinal);
    }

    [Fact]
    public void TheQaBlockCarriesTheStepFromTheTableTheRulesQueryPrints()
    {
        var (rig, _) = Arrange();

        var parsed = JsonText.Parse(Stop(rig)).AsObject();

        Assert.True(parsed.Get("reason") is { IsText: true });
        var reason = parsed.Get("reason")!.Value.AsString();
        var msg = rig.Services.Config.CliCommand(Ah.Config.CliTool.Msg, alwaysQuote: true);
        var step = Ah.Config.ConfigModule.QaDispatchStep("implementor-1", "lock", msg, rig.Services.Files.RealPath(rig.Sandbox.Root + "/proj"));
        Assert.True(reason.Contains(step, StringComparison.Ordinal), $"{step}\n--- not in ---\n{reason}");
    }

    private static void OwePending(HooksHostTests.Rig rig) =>
        Seed(rig, new JsonObject { ["session_id"] = Session, ["from"] = "orchestrator@tm", ["from_name"] = "orchestrator", ["reply_to"] = JsonValue.Null, ["task"] = "20261011-090000-zz09", ["status"] = "pending", ["nudges"] = 0, ["ts"] = "2026-10-11T10:30:00.000Z" });

    [Fact]
    public void ABlockForAnotherPendingObligationAlsoCarriesTheQaLine()
    {
        var (rig, _) = Arrange();
        OwePending(rig);

        var output = Stop(rig);

        Assert.Contains("these reports", output, StringComparison.Ordinal);
        Assert.Contains("reported DONE on lock", output, StringComparison.Ordinal);
        Assert.Contains("new --type request --to qa --slug lock", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AQaBlockWithNoFileOwedWritesNoEmptyPeerRowsBecauseTheStoreRefusesThem()
    {
        var (rig, _) = Arrange();

        Assert.Contains("new --type request --to qa --slug lock", Stop(rig), StringComparison.Ordinal);
        Assert.DoesNotContain(rig.Writer.Arguments, static a => a.Contains("\"rec\":[]", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAPendingObligationBlockCarriesNoQaLineWhenNoneIsOwed()
    {
        var (rig, _) = Arrange(status: "BLOCKED");
        OwePending(rig);

        var output = Stop(rig);

        Assert.Contains("your report to", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--to qa", output, StringComparison.Ordinal);
    }

    [Fact]
    public void AQaRequestFiledAfterTheDoneSilencesTheStop()
    {
        var (rig, dir) = Arrange();
        FileQa(dir, "lock", "2026-10-11T12:00:00+00:00");

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void NegativeAQaRequestForAnotherSlugOrFiledBeforeTheDoneDoesNotSatisfyIt()
    {
        var (rig, dir) = Arrange();
        FileQa(dir, "other", "2026-10-11T12:00:00+00:00");

        Assert.Contains("reported DONE on lock", Stop(rig), StringComparison.Ordinal);

        File.Delete($"{dir}/20261011-120000-bb02--qa--other--request.md");
        FileQa(dir, "lock", "2026-10-11T11:10:00+00:00");

        Assert.Contains("reported DONE on lock", Stop(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void QaNoneOnTheOriginalRequestExemptsTheItem()
    {
        var (rig, _) = Arrange(requestExtra: "qa: none\n");

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("reviewer")]
    [InlineData("qa")]
    public void NegativeADoneFromANonImplementRoleOrFromQaOwesNothing(string role)
    {
        var (rig, _) = Arrange(role);

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Theory]
    [InlineData("BLOCKED")]
    [InlineData("DONE-ISH")]
    public void NegativeAnythingButDoneOwesNothing(string status)
    {
        var (rig, _) = Arrange(status: status);

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void ThePlainThirdStopIsSilentAndLeavesOneJournalRow()
    {
        var (rig, _) = Arrange();
        void Spend() => Seed(rig, new JsonObject { ["type"] = "qa-nudge", ["session_id"] = Session, ["task"] = "qa:" + Id, ["ts"] = "2026-10-24T00:00:00.000Z" });

        Spend();
        Assert.NotEqual(string.Empty, Stop(rig));
        Spend();

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Contains(rig.Writer.Arguments, a => a.Contains("qa-waived", StringComparison.Ordinal));
    }
}
