using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>ah-ui-ia U0 (c): the Sitrep read from the files: the done event order, the confirmed lane team's items and ledger, and the folders it covers.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class SitrepFilesTests : IDisposable
{
    private const string Team = "tm";
    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;

    public SitrepFilesTests()
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
            ["AH_SLOTS_DIR"] = Path.Combine(_sandbox.Root, "slots"),
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
    }

    public void Dispose() => _sandbox.Dispose();

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    private static string Child => HierarchyModule.LaneChildName(Team, "s1");

    private void WriteTeam(string name, string doc)
    {
        _sandbox.PlantTeam(Path.Combine(Hierarchy, "teams", name + ".json"), doc);
    }

    private void Record(string team, params PlanInput[] inputs) =>
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(Hierarchy, team, "_run", "orchestrator@" + team, inputs).Refused);

    /// <summary>Records, then waits past the next whole second, so the next plan row's <c>ts</c> is later (it may carry whole seconds only).</summary>
    private void RecordThenTick(params PlanInput[] inputs)
    {
        Record(Team, inputs);
        Thread.Sleep(1100);
    }

    private void Ledger(string team, string line)
    {
        Ah.TestKit.TestProgressLogs.AppendFile(_sandbox.AhHome, Path.Combine(Hierarchy, "progress", team, "ledger.jsonl"), line + "\n");
    }

    private (JsonValue Body, IReadOnlyList<string> Teams) Read() => SitrepRows.Of(_graph, Hierarchy, Team, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), static () => []);

    private static List<string> Keys(JsonValue body) => [.. body.Member("completed")!.Value.AsArray().Select(static r => r.Member("key")!.Value.AsString())];

    [Fact]
    public void ADoneThenReopenThenDoneItemIsOrderedByItsLastDone()
    {
        WriteTeam(Team, "{\"team_id\":\"t-tm\",\"members\":[]}\n");
        Record(Team, new PlanInput("a", "add", "Do a"), new PlanInput("b", "add", "Do b"));
        RecordThenTick(new PlanInput("a", "done"));
        RecordThenTick(new PlanInput("b", "done"));
        Assert.Equal(["_run:b", "_run:a"], Keys(Read().Body));

        Record(Team, new PlanInput("a", "reopen"));
        Assert.Equal(["_run:b"], Keys(Read().Body));

        Record(Team, new PlanInput("a", "done"));
        Assert.Equal(["_run:a", "_run:b"], Keys(Read().Body));
    }

    [Fact]
    public void AStreamWriteThatChangesNoSitrepRowStillMovesAsOfButNoRow()
    {
        WriteTeam(Team, "{\"team_id\":\"t-tm\",\"members\":[]}\n");
        Record(Team, new PlanInput("a", "add", "Do a", At: "2026-10-31T10:00:00Z"), new PlanInput("a", "done", At: "2026-10-31T11:00:00Z"));
        var before = Read().Body;

        Record(Team, new PlanInput("a", "note", Note: "a later note", At: "2026-10-31T12:00:00Z"), new PlanInput("z", "add", "Not started", At: "2026-10-31T12:00:00Z"));
        var after = Read().Body;

        // spec ah-sitrep-events §4: as_of is the newest plan or ledger row read, shown or not
        Assert.True(after.Member("as_of")!.Value.AsNumber() > before.Member("as_of")!.Value.AsNumber());
        Assert.Equal(JsonText.Stringify(before.Member("completed")!.Value, 0), JsonText.Stringify(after.Member("completed")!.Value, 0));
    }

    [Fact]
    public void AConfirmedLaneTeamsItemsAndLedgerAreInTheParentsSitrepAndItsFolderIsCovered()
    {
        WriteTeam(Team, "{\"team_id\":\"t-tm\",\"members\":[],\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"b-s1\",\"base\":\"main\",\"sub_orchestrator\":\"so-s1\",\"lane\":{\"max_members\":4,\"max_streams\":2}}}}\n");
        WriteTeam(Child, "{\"team_id\":\"t-c\",\"members\":[],\"parent\":{\"team\":\"" + Team + "\",\"stream\":\"s1\"},\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"b\"}}}\n");
        Record(Team, new PlanInput("top", "add", "Top item"));
        Record(Team, new PlanInput("top", "done"));
        Record(Child, new PlanInput("low", "add", "Lane item"));
        Record(Child, new PlanInput("low", "done"));
        var at = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        Ledger(Child, "{\"v\":1,\"at\":\"" + at + "\",\"kind\":\"ship\",\"sha\":\"abc\",\"ref\":\"main\",\"tag\":null,\"confirmed\":true}");
        Ledger(Child, "{\"v\":1,\"at\":\"" + at + "\",\"kind\":\"verdict\",\"sha\":\"abc\",\"ref\":\"main\"}");

        var (body, teams) = Read();
        var rows = body.Member("completed")!.Value.AsArray();

        Assert.Equal([Team, Child], teams);
        Assert.Equal(["ship:abc:main", "_run:low", "_run:top"], Keys(body));
        Assert.Equal("s1", rows[0].Member("lane")!.Value.AsString());
        Assert.Equal(Child, rows[1].Member("team")!.Value.AsString());
        Assert.Equal("s1", rows[1].Member("lane")!.Value.AsString());
        Assert.True(rows[2].Member("lane")!.Value.IsNull);
        Assert.Equal(2, body.Member("completed_total")!.Value.AsNumber());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)] // negative: the same lane, open
    public void AClosedLanesWaitingItemLeavesTheSitrepAndWhatItFinishedStays(bool closed, int waiting)
    {
        var mark = closed ? ",\"closed\":{\"at\":\"2026-11-03T00:00:00.000Z\",\"by\":\"retired\"}" : string.Empty;
        WriteTeam(Team, "{\"team_id\":\"t-tm\",\"members\":[],\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"b-s1\",\"base\":\"main\",\"sub_orchestrator\":\"so-s1\",\"lane\":{\"max_members\":4,\"max_streams\":2}}}}\n");
        WriteTeam(Child, "{\"team_id\":\"t-c\",\"members\":[],\"parent\":{\"team\":\"" + Team + "\",\"stream\":\"s1\"},\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"b\"}}" + mark + "}\n");
        Record(Child, new PlanInput("low", "add", "Lane item"));
        Record(Child, new PlanInput("low", "done"));
        Record(Child, new PlanInput("q", "add", "Lane question"));
        Record(Child, new PlanInput("q", "wait-user", Note: "Which?"));

        var (body, _) = Read();

        Assert.Equal(waiting, body.Member("waiting_on_you_total")!.Value.AsNumber());
        Assert.Equal(["_run:low"], Keys(body));
    }

    [Fact]
    public void NegativeAClaimTheManagerDoesNotConfirmIsNotInScope()
    {
        WriteTeam(Team, "{\"team_id\":\"t-tm\",\"members\":[],\"streams\":{\"s2\":{\"state\":\"open\",\"branch\":\"b-s2\",\"base\":\"main\",\"sub_orchestrator\":\"so-s2\"}}}\n");
        WriteTeam(Child, "{\"team_id\":\"t-c\",\"members\":[],\"parent\":{\"team\":\"" + Team + "\",\"stream\":\"s1\"}}\n");
        Record(Child, new PlanInput("low", "add", "Lane item"));
        Record(Child, new PlanInput("low", "done"));

        var (body, teams) = Read();

        Assert.Equal([Team], teams);
        Assert.Empty(Keys(body));
    }
}
