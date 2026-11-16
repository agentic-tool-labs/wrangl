using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;

namespace Ah.Watch.Tests;

/// <summary>b296: a member's open request is on its herdr-core terminal's labels, written through <c>hc label</c>, and cleared when none is open.</summary>
public sealed class WorkLabelsTests
{
    private sealed class FakeHc : IMuxRunner
    {
        public List<string> Calls { get; } = [];

        public MuxResult Run(string binary, IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
        {
            Calls.Add(string.Join(' ', [binary, .. args]));
            return new MuxResult(true, string.Empty, string.Empty, 0, false, null);
        }
    }

    [Fact]
    public void ADeliveredBriefSetsTheLabelsAndAReportWithNothingOpenClearsThem()
    {
        var hc = new FakeHc();
        var core = new HerdrCore(hc, new MapEnv());
        var labels = new WorkLabels((address, pairs) => core.Label(address, pairs, 1000).Ok, null);
        var team = new OwnedTeam("t", new JsonObject(), [new MemberView("impl-1", new JsonObject { ["transport_id"] = "t5" }, true, null, null, null, "s1"), new MemberView("gone", new JsonObject { ["transport_id"] = "t6" }, false, null, null, null, "s2")]);
        var item = new ChecklistEntry("main", new PlanItem("atp-944", "Show work", "in_progress", "2026-11-07", null, null, [], null, null, null) { Parent = "b296" });
        var brief = new DispatchCandidate("r1", "implementor", "slug", "impl-1@t", "impl-1", "medium", "2026-11-07T12:00:00Z", true, "/x", false, string.Empty, "/h") { Item = "atp-944", Title = "Show work" };

        labels.Reconcile([team], [brief], _ => [item], null, 0);
        labels.Reconcile([team], [brief], _ => [item], null, 0);
        labels.Reconcile([team], [brief with { Open = false }], _ => [item], null, 0);

        Assert.Equal(
            ["hc label t5 ah.item=b296 ah.title=Show work ah.req=r1", "hc label t5 ah.item= ah.title= ah.req="],
            hc.Calls);
    }

    [Fact]
    public void TheOrchestratorSummaryLeavesOutAQuietTeamAndIsRemovedWhenEveryTeamIsQuiet()
    {
        var hc = new FakeHc();
        var core = new HerdrCore(hc, new MapEnv());
        var labels = new WorkLabels((address, pairs) => core.Label(address, pairs, 1000).Ok, null);
        var busy = new OwnedTeam("rc4", new JsonObject(), [new MemberView("impl-1", new JsonObject(), true, null, null, null, "s1")]);
        var held = new OwnedTeam("rc3", new JsonObject(), [new MemberView("impl-2", new JsonObject(), true, null, null, null, "s2")]);
        var brief = new DispatchCandidate("r1", "implementor", "slug", "impl-1@rc4", "impl-1", "medium", "2026-11-07T12:00:00Z", true, "/x", false, string.Empty, "/h");

        labels.Reconcile([busy, held], [brief], _ => [], "t9", 0);
        labels.Reconcile([busy, held], [brief with { Open = false }], _ => [], "t9", 0);

        Assert.Equal(["hc label t9 ah.title=rc4 · 1 working · 0 waiting on you", "hc label t9 ah.title="], hc.Calls);
    }
}
