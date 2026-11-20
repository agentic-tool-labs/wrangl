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

    private static (HooksHostTests.Rig Rig, string Dir) Arrange(string role = "implementor", string status = "DONE", string requestExtra = "", string? qa = null)
    {
        var rig = new HooksHostTests.Rig();
        if (qa is not null)
        {
            Directory.CreateDirectory(rig.Sandbox.Root + "/proj/.claude/ah2");
            File.WriteAllText(rig.Sandbox.Root + "/proj/.claude/ah2/agent-hierarchy.json", $"{{\"version\":1,\"qa\":{{\"parallel\":3,\"cadence\":\"{qa}\"}}}}");
        }

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

    private static void FileQa(string dir, string slug, string created, string extra = "") =>
        File.WriteAllText($"{dir}/20261011-120000-bb02--qa--{slug}--request.md", Msg("20261011-120000-bb02", "request", "qa", "orchestrator", slug, created, extra, "## [0] tldr\n- set full"));

    [Fact]
    public void AQaRequestOnTheSameItemUnderAnotherSlugSilencesTheStop()
    {
        var (rig, dir) = Arrange(requestExtra: "item: s/lock\n");
        FileQa(dir, "qa-lock", "2026-10-11T12:00:00+00:00", "item: s/lock\n");

        Assert.Equal(string.Empty, Stop(rig));
    }

    /// <summary>Plants the item's plan log: added, started, and one DONE <c>report</c> row for <see cref="Id"/> per flag, oldest first, each with or without <c>at_base</c>.</summary>
    private static void PlantReport(HooksHostTests.Rig rig, params bool[] atBase)
    {
        var progress = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj") + "/progress/tm";
        Directory.CreateDirectory(progress);
        string Row(string ev, string extra) => $"{{\"v\":1,\"ts\":\"2026-10-11T11:30:00Z\",\"team\":\"tm\",\"stream\":\"_run\",\"item\":\"lock\",\"event\":\"{ev}\"{extra},\"by\":\"o\",\"session\":null}}";
        var cause = $",\"cause\":{{\"event\":\"reply\",\"ref\":\"{Id}\",\"at\":\"2026-10-11T11:30:00Z\"}}";
        File.WriteAllText(
            progress + "/_run.plan.jsonl",
            string.Join('\n', [Row("add", ",\"title\":\"Lock\""), Row("start", string.Empty), .. atBase.Select(b => Row("report", cause + (b ? ",\"at_base\":true" : string.Empty)))]) + "\n");
        Ah.TestKit.TestImport.Planted(rig.Services.Environment.AhHome, [progress + "/_run.plan.jsonl"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("item")] // Amendment 3: the one exception to "item behaves as today"
    public void ADoneWhoseReportRowIsAtTheBaseOwesNoQa(string? qa)
    {
        var (rig, _) = Arrange(requestExtra: "item: _run/lock\n", qa: qa);
        PlantReport(rig, true);

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void NegativeAReReportAfterCommitsOnTheSameRequestOwesQaAgain()
    {
        var (rig, _) = Arrange(requestExtra: "item: _run/lock\n");
        PlantReport(rig, true, false);

        Assert.Contains("reported DONE on lock", Stop(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAReportRowWithoutTheFlagStillOwesQa()
    {
        var (rig, _) = Arrange(requestExtra: "item: _run/lock\n");
        PlantReport(rig, false);

        Assert.Contains("reported DONE on lock", Stop(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAQaRequestOnAnotherItemUnderAnotherSlugDoesNotSatisfyIt()
    {
        var (rig, dir) = Arrange(requestExtra: "item: s/lock\n");
        FileQa(dir, "qa-lock", "2026-10-11T12:00:00+00:00", "item: s/other\n");

        Assert.Contains("reported DONE on lock", Stop(rig), StringComparison.Ordinal);
    }

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

    private static void Spend(HooksHostTests.Rig rig) =>
        Seed(rig, new JsonObject { ["type"] = "qa-nudge", ["session_id"] = Session, ["task"] = "qa:" + Id, ["ts"] = "2026-10-24T00:00:00.000Z" });

    [Fact]
    public void ThePlainThirdStopIsSilentAndLeavesOneJournalRow()
    {
        var (rig, _) = Arrange(qa: "item");

        Spend(rig);
        Assert.NotEqual(string.Empty, Stop(rig));
        Spend(rig);

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Contains(rig.Writer.Arguments, a => a.Contains("qa-waived", StringComparison.Ordinal));
    }

    [Fact]
    public void BatchRaisesNoPerItemQaLine()
    {
        var (rig, _) = Arrange(qa: "batch");

        Assert.Equal(string.Empty, Stop(rig));
    }

    [Fact]
    public void NegativeBatchLeavesAnotherPendingObligationsBlockStanding()
    {
        var (rig, _) = Arrange(qa: "batch");
        OwePending(rig);

        var output = Stop(rig);

        Assert.Contains("your report to", output, StringComparison.Ordinal);
        Assert.DoesNotContain("--to qa", output, StringComparison.Ordinal);
    }

    [Fact]
    public void ItemRaisesTheLineWithNoChooseLine()
    {
        var (rig, _) = Arrange(qa: "item");

        var output = Stop(rig);

        Assert.Contains("reported DONE on lock", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/wrangl:hierarchy qa", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("nightly")] // an unknown value is not configured
    public void UnconfiguredRaisesTheLineOnceEndingWithHowToChoose(string? qa)
    {
        var (rig, _) = Arrange(qa: qa);

        var reason = JsonText.Parse(Stop(rig)).AsObject().Get("reason")!.Value.AsString();
        Assert.Contains("reported DONE on lock", reason, StringComparison.Ordinal);
        Assert.Contains("/wrangl:hierarchy qa item", reason, StringComparison.Ordinal);
        Assert.Contains("/wrangl:hierarchy qa batch", reason, StringComparison.Ordinal);
        Spend(rig);

        Assert.Equal(string.Empty, Stop(rig));
    }

    private const string Id2 = "20261011-113000-aa02";

    /// <summary>A second implementor DONE in the same session, on slug <c>gate</c>.</summary>
    private static void SecondDone(string dir)
    {
        File.WriteAllText($"{dir}/{Id2}--implementor--gate--request.md", Msg(Id2, "request", "implementor", "orchestrator", "gate", "2026-10-11T11:30:00+00:00", string.Empty, "## [0] tldr\n- x"));
        File.WriteAllText($"{dir}/{Id2}--orchestrator--gate--response.md", Msg(Id2, "response", "orchestrator", "implementor", "gate", "2026-10-11T11:45:00+00:00", string.Empty, "## [0] tldr\n- [1] status: DONE, commit def\n"));
    }

    [Fact]
    public void UnconfiguredAsksTheCadenceOnlyOnTheSessionsFirstDone()
    {
        var (rig, dir) = Arrange();
        Assert.Contains("/wrangl:hierarchy qa item", Stop(rig), StringComparison.Ordinal);
        Assert.Contains(rig.Writer.Arguments, static a => a.Contains("qa-cadence", StringComparison.Ordinal));
        Spend(rig); // the rig's writer does not land rows in the journal: seed what the first stop wrote
        Seed(rig, new JsonObject { ["type"] = "qa-nudge", ["session_id"] = Session, ["task"] = "qa-cadence", ["ts"] = "2026-10-24T00:00:00.000Z" });
        SecondDone(dir);

        var output = Stop(rig);

        Assert.Contains("reported DONE on gate", output, StringComparison.Ordinal);
        Assert.DoesNotContain("/wrangl:hierarchy qa", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeACadenceAskedInAnotherSessionIsAskedAgainInThisOne()
    {
        var (rig, _) = Arrange();
        Seed(rig, new JsonObject { ["type"] = "qa-nudge", ["session_id"] = "sess-other", ["task"] = "qa-cadence", ["ts"] = "2026-10-24T00:00:00.000Z" });

        Assert.Contains("/wrangl:hierarchy qa item", Stop(rig), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeItemIsStillRaisedAfterOneBlock()
    {
        var (rig, _) = Arrange(qa: "item");
        Spend(rig);

        Assert.Contains("reported DONE on lock", Stop(rig), StringComparison.Ordinal);
    }
}
