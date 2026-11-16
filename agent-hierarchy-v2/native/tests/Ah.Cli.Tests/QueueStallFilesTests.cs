using Ah.Abstractions;
using Ah.Cli.Verbs.Msg;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>The failsafe reads the queue from files with the same facts the daemon's alarm uses.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class QueueStallFilesTests : IDisposable
{
    private const string Team = "tm";
    private readonly Sandbox _sandbox = Sandbox.PlantedFiles();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly ServiceGraph _graph;
    private readonly FakeEnv _env;

    public QueueStallFilesTests()
    {
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
            ["AH_SLOTS_DIR"] = Path.Combine(_sandbox.Root, "slots"),
        };
        _env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), _env, FakeRunner.KnowingSelf(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var registered = Path.Combine(Hierarchy, "teams", Team + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(registered)!);
        File.WriteAllText(registered, "{\"team_id\":\"t-tm\",\"orchestrator\":{\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + FakeRunner.SelfStart + "\"},\"members\":[{\"name\":\"impl-1\",\"role\":\"implementor\",\"route\":\"pane\",\"kind\":\"codex\"}]}\n");
        FakeRunner.RegisterSelf(_graph.Mailbox, "m1", registered, "impl-1@" + Team, "impl-1@" + Team);
        FakeRunner.RegisterSelf(_graph.Mailbox, "o", registered, "orchestrator@" + Team);
    }

    public void Dispose() => _sandbox.Dispose();

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    private static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private void IdleSince(DateTimeOffset at, string activity = "idle")
    {
        _sandbox.PlantActivity(Hierarchy, "pane-impl-1", "{\"activity\":\"" + activity + "\",\"at\":\"" + Iso(at) + "\"}\n");
    }

    private PlanItem Queue(string item = "stab")
    {
        var result = _graph.Hierarchy.Progress.RecordChecklist(Hierarchy, Team, "_run", "orchestrator@" + Team, [new PlanInput(item, "add", "Do " + item), new PlanInput(item, "next"), new PlanInput(item, "role", Role: "implementor")]);
        Assert.Null(result.Refused);
        return _graph.Hierarchy.Progress.ReadPlan(Hierarchy, Team, "_run")!.Items.Single(i => i.Item == item);
    }

    private IReadOnlyList<OverdueQueueItem> Stalls(double minutesLater) =>
        _sandbox.Mirrored(() => new WakeComposition(_graph, _env).QueueStalls(Hierarchy, Hierarchy, Team, _now.AddMinutes(minutesLater).ToUnixTimeMilliseconds()));

    private string Send(string toName, double createdMinutesLater, bool close = false)
    {
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ahd", _sandbox.Executable("install/ahd"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] send = ["new", "--type", "request", "--to", "implementor", "--to-name", toName, "--from", "orchestrator", "--slug", "work", "--title", "Do the work", "--id", "20261016-140001-abcd", "--session", "o", "--plain", "--cwd", _sandbox.Root];
        Assert.True(_sandbox.Mirrored(() => MsgVerb.Run(context, send, stdout, stderr)) == 0, stderr.ToString());
        var path = _sandbox.MsgFile(stdout.ToString());
        var text = File.ReadAllText(path);
        var created = text.Split('\n').First(static l => l.StartsWith("created: ", StringComparison.Ordinal));
        File.WriteAllText(path, text.Replace(created, "created: " + Iso(_now.AddMinutes(createdMinutesLater)), StringComparison.Ordinal));
        if (close)
        {
            Assert.True(_sandbox.Mirrored(() => MsgVerb.Run(context, ["close", "20261016-140001-abcd", "--reason", "abandoned", "--session", "o", "--plain", "--cwd", _sandbox.Root], stdout, stderr)) == 0, stderr.ToString());
        }

        return path;
    }

    private IReadOnlyList<Ah.Watch.QueueRow> Rows(double minutesLater) =>
        _sandbox.Mirrored(() => QueueRows.Of(_graph, Hierarchy, Team, _now.AddMinutes(minutesLater).ToUnixTimeMilliseconds()));

    private JsonValue QueueRead() =>
        new StateServiceReader(_graph.InternalStateRead).Read("queue", "read", Hierarchy, new JsonObject { ["dir"] = Hierarchy, ["team"] = Team });

    [Fact]
    public void TheQueueReadOpReturnsTheLaneSerialisationOfTheQueuedRowsAndNoRowsForAnEmptyQueue()
    {
        IdleSince(_now.AddMinutes(-30));
        Assert.Equal("[]", JsonText.Stringify(QueueRead().Member("rows")!.Value, 0));

        _ = Queue("stab");
        _ = Queue("polish");

        var rows = QueueRead().Member("rows")!.Value.AsArray();
        var expected = QueueRows.Of(_graph, Hierarchy, Team, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Select(Ah.Watch.WatchModule.QueueRowToJson).ToList();

        Assert.Equal(2, rows.Count);
        Assert.Equal(
            expected.Select(static r => JsonText.Stringify(r, 0)).ToList(),
            rows.Select(static r => JsonText.Stringify(r, 0)).ToList());
    }

    [Fact]
    public void TheQueueSliceListsAQueuedItemBeforeItStallsWithItsIdleMemberItsStallTimeAndTheLastDispatchOfItsRole()
    {
        IdleSince(_now.AddMinutes(-30));
        var item = Queue();

        var fresh = Assert.Single(Rows(1));
        Assert.Equal("_run/stab", fresh.Item);
        Assert.Equal(["impl-1"], fresh.Eligible);
        Assert.Null(fresh.LastDispatchMs);
        Assert.True(Ah.Core.Time.Timestamps.TryParseMs(item.StateSince!, out var queued));
        Assert.Equal(queued + HierarchyModule.QueueStallMs, fresh.StallAtMs);
        Assert.Empty(Stalls(1));

        Send("impl-1", 2, close: true);
        var sent = Assert.Single(Rows(20));
        Assert.Equal(_now.AddMinutes(2).ToUnixTimeMilliseconds(), sent.LastDispatchMs);
    }

    [Fact]
    public void AQueueRowCarriesTheItemTitleAfterItsUnchangedFieldsAndNotTheItemKey()
    {
        IdleSince(_now.AddMinutes(-30));
        _ = Queue();

        var row = Assert.Single(Rows(1));
        Assert.Equal("Do stab", row.Title);
        Assert.NotEqual(row.Item, row.Title);
        var json = Ah.Watch.WatchModule.QueueRowToJson(row).AsObject();
        Assert.Equal(["item", "role", "queued_since", "stall_at", "eligible_members", "last_dispatch_at", "title", "idle_eligible"], json.Keys.ToArray());
        Assert.Equal("Do stab", json.Get("title")!.Value.AsString());
    }

    [Fact]
    public void AQueueRowIsIdleEligibleWhileAnIdleMemberOfItsRoleIsFree()
    {
        IdleSince(_now.AddMinutes(-30));
        _ = Queue();

        Assert.True(Ah.Watch.WatchModule.QueueRowToJson(Assert.Single(Rows(1))).Member("idle_eligible")!.Value.AsBool());
    }

    [Fact]
    public void AChecklistItemSetThroughTheOpRunnerMovesThePlanCounterAndShowsInTheNextHeldPoll()
    {
        IdleSince(_now.AddMinutes(-30));
        var held = new List<int>();
        var memo = new ChecklistMemo(() => Ah.StateDb.StateDbModule.DomainSeq(_sandbox.AhHome, "backlog"), (dir, team) => HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, dir, team));
        IReadOnlyList<ChecklistEntry> Watched(string dir, string team)
        {
            var entries = memo.Of(dir, team);
            held.Add(entries.Count);
            return entries;
        }

        // the team's queue rows as MemberFactsOf reads them each poll (this sandbox's roster lists no team, so the team is named here)
        string Poll(Func<string, string, IReadOnlyList<ChecklistEntry>>? entriesOf) =>
            string.Join(',', _sandbox.Mirrored(() => QueueRows.Of(_graph, Hierarchy, Team, _now.AddMinutes(1).ToUnixTimeMilliseconds(), entriesOf)).Select(static r => r.Item));
        var before = Ah.StateDb.StateDbModule.DomainSeq(_sandbox.AhHome, "backlog");

        // the negative: a read with nothing written leaves the counter where it was and holds no item
        Assert.Empty(Watched(Hierarchy, HierarchyModule.ProgressTeamOf(Team)));
        Assert.Equal(before, Ah.StateDb.StateDbModule.DomainSeq(_sandbox.AhHome, "backlog"));

        _ = Queue();
        Assert.True(Ah.StateDb.StateDbModule.DomainSeq(_sandbox.AhHome, "backlog") > (before ?? 0));

        // the next held poll folds the item in (a frozen counter would keep the empty fold), and reads what a poll with no memo reads
        Assert.Equal("_run/stab", Poll(Watched));
        Assert.Equal(Poll(null), Poll(Watched));
        Assert.Equal(1, held[^1]);
    }

    [Fact]
    public void TheGraphsOneChecklistHoldShowsAnItemSetThroughTheCliOnTheNextRead()
    {
        // the watch tick's work labels (WatchHostAdapter.Checklist) and backlog health (SitrepRows.DirItems) both read this hold
        var team = HierarchyModule.ProgressTeamOf(Team);
        Assert.Empty(_graph.Checklists.Of(Hierarchy, team));

        _ = Queue();

        Assert.Equal("stab", Assert.Single(_graph.Checklists.Of(Hierarchy, team)).Item.Item);
        Assert.Equal(HierarchyModule.ChecklistEntries(_graph.Hierarchy.Progress, Hierarchy, team), _graph.Checklists.Of(Hierarchy, team));
    }

    [Fact]
    public void HeldGateRowsTakeANewRowAndDropADeletedOneOnTheNextRead()
    {
        void Sql(string statement) => Assert.True(Ah.StateDb.StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute(statement);
            return 0;
        }).Ok);
        var dir = MsgRows.PoolKey(Hierarchy);
        string Ops() => string.Join(',', _graph.Hierarchy.Roster.ReadGates(Hierarchy).Select(static r => r.Member("op_id")!.Value.AsString()));
        // an imported row sits at seq <= 0, before the appended ones
        Sql($"INSERT INTO gates(hier_dir, seq, body, at) VALUES ('{dir}', -1, '{{\"op_id\":\"i\"}}', ''), ('{dir}', 1, '{{\"op_id\":\"a\"}}', ''), ('{dir}', 2, '{{\"op_id\":\"b\"}}', '')");
        Assert.Equal("i,a,b", Ops());

        Sql($"INSERT INTO gates(hier_dir, seq, body, at) VALUES ('{dir}', 3, '{{\"op_id\":\"c\"}}', '')");
        Assert.Equal("i,a,b,c", Ops());

        // the negative: a deleted row is not kept by the hold (the count guard reads the dir again)
        Sql($"DELETE FROM gates WHERE hier_dir = '{dir}' AND seq = 2");
        Assert.Equal("i,a,c", Ops());
    }

    [Fact]
    public void AHeldTeamDocumentIsSharedUntilTheFileChangesAndAClonedEditDoesNotReachTheNextReader()
    {
        var path = Path.Combine(Hierarchy, "teams", Team + ".json");
        JsonValue Doc() => _graph.Roster.ReadTeamWithDigest(path)["doc"];
        var first = Doc();
        Assert.Same(first.AsObject(), Doc().AsObject());

        var mine = first.AsObject().Clone();
        mine["team_id"] = "edited";
        Assert.Equal("t-tm", Doc().Member("team_id")!.Value.AsString());

        // the negative: rewritten bytes are not served from the hold (this sandbox's teams live in the database)
        Assert.True(Ah.StateDb.StateDbModule.OpenDirect(_sandbox.AhHome).Write(tx =>
        {
            tx.Execute("UPDATE teams SET doc = replace(doc, 't-tm', 't-new') WHERE team = 'tm'");
            return 0;
        }).Ok);
        Assert.Equal("t-new", Doc().Member("team_id")!.Value.AsString());
    }

    [Fact]
    public void NegativeAQueueRowWhoseOnlyMemberIsWorkingIsNotIdleEligible()
    {
        IdleSince(_now.AddMinutes(-30), "working");
        _ = Queue();

        Assert.False(Ah.Watch.WatchModule.QueueRowToJson(Assert.Single(Rows(20))).Member("idle_eligible")!.Value.AsBool());
    }

    [Theory]
    [InlineData("working")]
    [InlineData("done")]
    public void NegativeTheQueueSliceDropsAnItemThatIsNotQueuedAndNamesNoMemberWhoIsWorking(string kind)
    {
        IdleSince(_now.AddMinutes(-30), kind == "working" ? "working" : "idle");
        Queue();
        if (kind == "done")
        {
            Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(Hierarchy, Team, "_run", "orchestrator@" + Team, [new PlanInput("stab", "done")]).Refused);
        }

        var rows = Rows(20);

        if (kind == "done")
        {
            Assert.Empty(rows);
        }
        else
        {
            Assert.Empty(Assert.Single(rows).Eligible);
        }
    }

    [Fact]
    public void AnIdleMemberAndAnOldQueuedItemGiveOneStallDueAtTheLaterOfQueuedAndIdlePlusTheThreshold()
    {
        IdleSince(_now.AddMinutes(-30));
        var item = Queue();

        var stalls = Stalls(20);

        var one = Assert.Single(stalls);
        Assert.Equal("_run/stab", one.Item);
        Assert.Equal("impl-1", one.Member);
        Assert.True(Ah.Core.Time.Timestamps.TryParseMs(item.StateSince!, out var queued));
        Assert.Equal(queued + HierarchyModule.QueueStallMs, one.AtMs);
    }

    [Theory]
    [InlineData("working")]
    [InlineData("queued-short")]
    public void NegativeTheNearestInputThatMustNotStallGivesNone(string kind)
    {
        IdleSince(_now.AddMinutes(-30), kind == "working" ? "working" : "idle");
        Queue();
        var stalls = kind switch
        {
            "queued-short" => Stalls(5),
            _ => Stalls(20),
        };

        Assert.Empty(stalls);
    }

    [Fact]
    public void AMemberSessionHoldingASlotIsOnAJobAndAnotherSessionIsNot()
    {
        var table = PlatformModule.CreateProcessTable(_graph.Runner);
        var slot = Path.Combine(_sandbox.Root, "slots", "1");
        Directory.CreateDirectory(slot);
        File.WriteAllText(
            Path.Combine(slot, "holder"),
            "{\"pid\":" + Environment.ProcessId + ",\"pid_start\":\"" + table.StartTime(Environment.ProcessId) + "\",\"since\":" + _now.ToUnixTimeSeconds() + "}\n");
        var slots = new SlotFiles(_graph, null);

        Assert.NotNull(slots.StateOf("m1"));
        Assert.Null(slots.StateOf("m2"));
    }

    [Fact]
    public void ADismissedMemberIsNotEligible()
    {
        IdleSince(_now.AddMinutes(-30));
        Queue();
        var team = Path.Combine(Hierarchy, "teams", Team + ".json");
        File.WriteAllText(team, File.ReadAllText(team).Replace("\"kind\":\"codex\"", "\"kind\":\"codex\",\"dismissed\":true", StringComparison.Ordinal));

        Assert.Empty(Stalls(20));
    }

    [Fact]
    public void AnOpenDispatchToTheMemberGivesNone()
    {
        IdleSince(_now.AddMinutes(-30));
        Queue();
        _ = Send("impl-1", 5);

        Assert.Empty(Stalls(20));
    }

    [Fact]
    public void ADispatchAnsweredInsideTheQuietWindowHoldsTheStallBackAndOneOutsideDoesNot()
    {
        IdleSince(_now.AddMinutes(-30));
        Queue();
        _ = Send("impl-1", 5, close: true);

        Assert.Empty(Stalls(12));
        Assert.Single(Stalls(20));
    }
}
