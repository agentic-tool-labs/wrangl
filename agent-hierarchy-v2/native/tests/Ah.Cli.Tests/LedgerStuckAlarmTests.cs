using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>A ledger the memory adapter keeps refusing is mailed to the team's live Orchestrator as one <c>memory-forward-stuck</c> event.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class LedgerStuckAlarmTests : IDisposable
{
    private const string Orchestrator = "sess-orch";

    private readonly Sandbox _sandbox = new();
    private ServiceGraph? _graph;

    public void Dispose() => _sandbox.Dispose();

    private StallHost Arrange(bool orchestratorLive)
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        _graph = graph;
        var orchestrator = orchestratorLive ? $$"""{"session_id":"{{Orchestrator}}","pid":{{Environment.ProcessId}}}""" : "null";
        _sandbox.PlantTeam(
            _sandbox.Home + "/hier/teams/beta.json",
            $$"""{"orchestrator":{{orchestrator}},"members":[{"name":"impl","role":"implementor"}]}""");
        var box = _sandbox.Home + "/orchestrator-box";
        _ = Directory.CreateDirectory(box);
        var registered = graph.Writer.Invoke(
            "mailbox",
            "register",
            JsonText.ToUtf8(
                new JsonObject
                {
                    ["session_id"] = Orchestrator,
                    ["boxes"] = new JsonArray { new JsonObject { ["address"] = "orchestrator@beta", ["dir"] = box } },
                    ["cwd"] = _sandbox.Home,
                    ["pid"] = Environment.ProcessId,
                },
                0),
            new RequestContext(_sandbox.Home, _sandbox.Home, string.Empty, string.Empty));
        Assert.True(registered.Ok, registered.Error);
        return new StallHost(graph, _sandbox.AhHome, () => true, () => [_sandbox.Home + "/hier"]);
    }

    /// <summary>The <c>memory-forward-stuck</c> events in any box, read from the store.</summary>
    private int Stored() =>
        _graph!.Mailbox.ListBoxes()
            .SelectMany(b => _graph.Mailbox.ReadEntries(b.Dir).Entries)
            .Count(e => JsonText.Stringify(e, 0).Contains("\"event\":\"memory-forward-stuck\"", StringComparison.Ordinal));

    [Fact]
    public void ARefusedLedgerIsMailedToTheTeamsLiveOrchestrator()
    {
        var host = Arrange(orchestratorLive: true);

        DaemonServices.AlarmLedgerStuck(host, _sandbox.Home + "/hier", "beta", "the memory adapter has refused the ledger batch at byte 0");

        Assert.Equal(1, Stored());
    }

    [Fact]
    public void ALedgerOfATeamWithNoLiveOrchestratorMailsNothing()
    {
        var host = Arrange(orchestratorLive: false);

        DaemonServices.AlarmLedgerStuck(host, _sandbox.Home + "/hier", "beta", "refused");

        Assert.Equal(0, Stored());
    }

    [Fact]
    public void NegativeAQueuedOnlyTeamReachesOnlyTheAlarmListNeverTheListTheBareWatchMarks()
    {
        var host = Arrange(orchestratorLive: false);
        var msgs = _sandbox.Home + "/hier/msgs";
        _ = Directory.CreateDirectory(msgs);
        var path = Path.Combine(msgs, "20261108-040000-abcd--implementor--queued--request.md");
        File.WriteAllText(path, $"---\nid: 20261108-040000-abcd\ntype: request\nto: implementor\nfrom: orchestrator\nslug: queued\nteam: beta\nteam_file: {_sandbox.Home}/hier/teams/beta.json\n---\n\nrequest\n");
        TestMsgStore.Store(path);

        Assert.True(Assert.Single(host.AlarmTeams(), t => t.Name == "beta").QueuedOnly);
        Assert.DoesNotContain(host.Teams(), t => t.Name == "beta");
    }

    /// <summary>b459 step 14: the sweep reads the graph's checklist hold; an item set after a sweep is seen by the next one.</summary>
    [Fact]
    public void NegativeTheStallSweepSeesAChecklistItemSetAfterItsLastRun()
    {
        var host = Arrange(orchestratorLive: false);
        var hier = _sandbox.Home + "/hier";
        Assert.DoesNotContain(host.AlarmTeams(), t => t.Name == "beta");

        var result = _graph!.Hierarchy.Progress.RecordChecklist(hier, "beta", "_run", "orchestrator@beta", [new Ah.Hierarchy.PlanInput("stab", "add", "Do stab"), new Ah.Hierarchy.PlanInput("stab", "next"), new Ah.Hierarchy.PlanInput("stab", "role", Role: "implementor")]);
        Assert.Null(result.Refused);

        Assert.True(Assert.Single(host.AlarmTeams(), t => t.Name == "beta").QueuedOnly);
    }

    /// <summary>b459 step 15: the run anchors are held per dir by the msgs, teams and grants counters, and the held trees are re-statted on each use.</summary>
    [Fact]
    public void AHeldAnchorListTakesANewAnchorAndNegativeARemovedWorktreeDropsItsPoolWithNoSeqMove()
    {
        _ = Arrange(orchestratorLive: false);
        var hier = _sandbox.Home + "/hier";
        var tree = Path.Combine(_sandbox.Home, "wt");
        _ = Directory.CreateDirectory(tree);
        _sandbox.PlantTeam(hier + "/teams/beta.json", $$"""{"orchestrator":null,"members":[{"name":"impl","role":"implementor","expected_root":"{{tree}}"}]}""");
        string? Anchor() => _graph!.Hierarchy.Decisions.OpenRunAnchor(hier, JsonValue.FromString("beta")).Get("id") is { IsText: true } id ? id.AsString() : null;
        void Plant(string pool, string id)
        {
            _ = Directory.CreateDirectory(Path.Combine(pool, "msgs"));
            var path = Path.Combine(pool, "msgs", $"{id}--orchestrator--pipeline-run-anchor--request.md");
            File.WriteAllText(path, $"---\nid: {id}\ntype: request\nto: orchestrator\nfrom: orchestrator\nslug: pipeline-run-anchor\nteam: beta\nteam_file: {hier}/teams/beta.json\n---\n\nanchor\n");
            TestMsgStore.Store(path);
        }

        Assert.Null(Anchor());

        // a new anchor in the worktree's pool moves the msgs counter and shows on the next use
        Plant(_graph!.Config.HierarchyDir(tree)!, "20261108-050000-abcd");
        Assert.Equal("20261108-050000-abcd", Anchor());

        // the worktree goes away with no store write: the held tree is re-statted and its pool dropped
        var seq = Ah.StateDb.StateDbModule.DomainSeq(_sandbox.AhHome, "msgs");
        Directory.Delete(tree, recursive: true);
        Assert.Null(Anchor());
        Assert.Equal(seq, Ah.StateDb.StateDbModule.DomainSeq(_sandbox.AhHome, "msgs"));
    }

    [Fact]
    public void ALedgerOfAnotherTeamMailsNothing()
    {
        var host = Arrange(orchestratorLive: true);

        DaemonServices.AlarmLedgerStuck(host, _sandbox.Home + "/hier", "gamma", "refused");

        Assert.Equal(0, Stored());
    }
}
