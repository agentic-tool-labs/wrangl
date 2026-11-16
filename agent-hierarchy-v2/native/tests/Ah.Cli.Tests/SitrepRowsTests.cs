using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>ah-ui-ia U0 (c): the Sitrep body (§3.3) and its ship and merge records (§3.3a).</summary>
public sealed class SitrepRowsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static string Iso(int minutes) => T0.AddMinutes(minutes).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static double Ms(int minutes) => T0.AddMinutes(minutes).ToUnixTimeMilliseconds();

    [Fact]
    public void PlannedRowsAreThoseAnItemNamesAndOutranksAndNegativeAnEqualRankOrARowNotInTheBacklogIsNot()
    {
        var backlog = new[] { Plan("epic-1", "not_started") with { Kind = "epic" }, Plan("epic-2", "not_started") with { Kind = "epic" }, Plan("task-1", "not_started") };
        var checklist = new[]
        {
            Plan("c", "in_progress") with { Parent = "epic-1" },
            Plan("closed", "done") with { Parent = "epic-2" },
            Plan("d", "next") with { Parent = "task-1" },
            Plan("e", "next") with { Parent = "elsewhere" },
            Plan("f", "next"),
        };

        Assert.Equal(["epic-1", "epic-2"], HierarchyModule.PlannedBacklogKeys(backlog, checklist).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ASheetsDashHolderIsServedAsNobodyAndNegativeANamedHolderIsKept()
    {
        var body = Compose(
        [
            new("mgr", null, "s", Plan("d", "in_progress") with { UpdatedAt = Iso(1), Holder = "—" }),
            new("mgr", null, "s", Plan("n", "in_progress") with { UpdatedAt = Iso(2), Holder = "impl" }),
        ], live: AllWorking);

        Assert.True(body.Member("not_moving")!.Value.AsArray().Single().Member("holder")!.Value.IsNull);
        Assert.Equal("impl", body.Member("in_progress")!.Value.AsArray().Single().Member("holder")!.Value.AsString());
    }

    private static PlanItem Plan(string item, string state, string? doneAt = null, string? since = null, string? wave = null, int? rank = null) =>
        new(item, "Title " + item, state, Iso(0), null, doneAt, [], null, null, null, Rank: rank, Holder: "impl-1", Role: "implementor", StateSince: since, Wave: wave);

    private static SitrepRows.Item Done(string item, int minutes, string team = "mgr", string? lane = null) =>
        new(team, lane, "s", Plan(item, "done", Iso(minutes)));

    private static SitrepRows.Ledger Record(string kind, string sha, int minutes, string team = "mgr", string? lane = null, string? at = null) =>
        new(team, lane, JsonText.Parse("{\"kind\":\"" + kind + "\",\"sha\":\"" + sha + "\",\"ref\":\"main\",\"into\":\"main\",\"tag\":null,\"confirmed\":" + (sha.EndsWith('u') ? "false" : "true") + ",\"at\":\"" + (at ?? Iso(minutes)) + "\"}"));

    private static SitrepRows.Queued Queued(string item, int minutes, bool idle = false) =>
        new("mgr", null, new QueueRow("s/" + item, "implementor", Ms(minutes), Ms(minutes + 10), idle ? ["impl-1"] : [], null) { Title = "Title " + item });

    private static JsonValue Compose(
        IReadOnlyList<SitrepRows.Item>? items = null,
        IReadOnlyList<SitrepRows.Queued>? queued = null,
        IReadOnlyList<PlanItem>? backlog = null,
        IReadOnlyList<SitrepRows.Ledger>? ledger = null,
        IReadOnlyList<PlanItem>? dirItems = null,
        double? nowMs = null,
        ChecklistLiveness? live = null,
        IReadOnlyList<(string Team, JsonValue Row)>? decisions = null,
        IReadOnlyList<PlanItem>? testing = null) =>
        SitrepRows.Compose(items ?? [], queued ?? [], () => backlog ?? [], () => dirItems ?? [.. (items ?? []).Select(static i => i.Plan)], ledger ?? [], nowMs, live: _ => live, decisions: decisions, testing: () => testing ?? []);

    [Fact]
    public void AWorkingMemberIsInProgressWithWhatItIsOnAndNegativeAnIdleMemberOrOneAlreadyHoldingAMovingItemIsNot()
    {
        var live = new ChecklistLiveness(new HashSet<string> { "impl-1", "arch" }, new HashSet<string> { "x1" })
        {
            OnExchange = new Dictionary<string, string> { ["rev"] = "review the diff" },
        };
        var items = new SitrepRows.Item[] { new("mgr", null, "s", Plan("held", "in_progress")) };
        var body = SitrepRows.Compose(items, [], static () => [], static () => [], [], working: [("mgr", null, live)], live: _ => live);
        var rows = body.Member("in_progress")!.Value.AsArray();

        // impl-1 holds the moving item: its row is the item's, not a second one; idle members (not in either set) get none
        Assert.Equal(["s:held", "member:mgr/arch", "member:mgr/rev"], rows.Select(static r => r.Member("key")!.Value.AsString()));
        Assert.Equal("review the diff", rows[2].Member("on")!.Value.AsString());
        Assert.True(rows[1].Member("on")!.Value.IsNull);
        Assert.Equal(3, body.Member("in_progress_total")!.Value.AsNumber());
    }

    [Fact]
    public void ARunningJobIsInProgressWithItsSlotAndNegativeAnEndedJobIsNot()
    {
        static JobFact Job(string id, bool ended, JobSlot? slot = null) =>
            new(id, "mgr", "qa", "sess", "slot", "ship " + id, 42, "large", Ms(1), null, null, ended, true, [], Slot: slot);
        var body = SitrepRows.Compose([], [], static () => [], static () => [], [],
            jobs: [("mgr", null, Job("j1", false, new JobSlot("running", Ms(2), 0, "dotnet", "ship j1 pid 42"))), ("mgr", null, Job("j2", true))]);
        var row = body.Member("in_progress")!.Value.AsArray().Single();

        Assert.Equal("job:j1", row.Member("key")!.Value.AsString());
        Assert.Equal("running", row.Member("state")!.Value.AsString());
        Assert.Equal("ship j1 pid 42", row.Member("slot_holder")!.Value.AsString());
        Assert.Equal("qa", row.Member("holder")!.Value.AsString());
    }

    [Fact]
    public void AnItemReportedOrWaitingOnTheOrchestratorOverAnHourIsStaleAndNegativeOneUnderAnHourOrInAnotherStateIsNot()
    {
        var items = new SitrepRows.Item[]
        {
            new("mgr", null, "s", Plan("old-report", "reported", since: Iso(0))),
            new("mgr", null, "s", Plan("old-wait", "waiting_orch", since: Iso(0))),
            new("mgr", null, "s", Plan("new-report", "reported", since: Iso(2))),
            new("mgr", null, "s", Plan("old-blocked", "blocked", since: Iso(0))),
        };
        var body = Compose(items, nowMs: Ms(61));
        var stale = body.Member("not_moving")!.Value.AsArray().Concat(body.Member("blocked")!.Value.AsArray())
            .ToDictionary(static r => r.Member("key")!.Value.AsString(), static r => r.Member("stale")!.Value.AsBool());

        Assert.True(stale["s:old-report"]);
        Assert.True(stale["s:old-wait"]);
        Assert.False(stale["s:new-report"]);
        Assert.False(stale["s:old-blocked"]);
    }

    [Fact]
    public void ARowMarkedTestingIsInProgress()
    {
        var body = Compose(testing: [Plan("b7", "testing")]);

        Assert.Equal(["b7"], body.Member("in_progress")!.Value.AsArray().Select(static r => r.Member("key")!.Value.AsString()));
        Assert.Equal("testing", body.Member("in_progress")!.Value.AsArray()[0].Member("state")!.Value.AsString());
        Assert.Equal(1, body.Member("in_progress_total")!.Value.AsNumber());
    }

    [Fact]
    public void ARowWithAnItemUnderWayLeavesUpNextAndNegativeOneOnlyQueuedStays()
    {
        var body = Compose(
            backlog: [Plan("b1", "not_started", wave: "now"), Plan("b2", "not_started", wave: "now")],
            dirItems: [Plan("a", "in_review") with { Parent = "b1" }, Plan("c", "next") with { Parent = "b2" }]);

        Assert.Equal(["b2"], body.Member("up_next")!.Value.AsArray().Select(static r => r.Member("key")!.Value.AsString()));
    }

    private static readonly ChecklistLiveness AllWorking = new(new HashSet<string> { "impl" }, new HashSet<string>());

    private static List<string> Keys(JsonValue body, string column) => [.. body.Member(column)!.Value.AsArray().Select(static r => r.Member("key")!.Value.AsString())];

    [Fact]
    public void TheBodyServesEveryRegionInStackOrderAndEachNamesItsRowsAndCount()
    {
        var body = Compose([Done("a", 1)]);
        var regions = body.Member("regions")!.Value.AsArray();

        Assert.Equal(["completed", "in_progress", "up_next", "waiting_on_you", "blocked", "not_moving", "stale", "up_next_old"], regions.Select(static r => r.Member("list")!.Value.AsString()));
        Assert.Equal(["not_moving", "stale", "up_next_old"], regions.Where(static r => r.Member("folded")!.Value.AsBool()).Select(static r => r.Member("list")!.Value.AsString()));
        Assert.Equal(["completed", "in_progress", "up_next"], regions.Where(static r => !r.Member("optional")!.Value.AsBool()).Select(static r => r.Member("list")!.Value.AsString()));
        foreach (var r in regions)
        {
            Assert.True(body.Member(r.Member("list")!.Value.AsString()) is { IsArray: true });
            if (r.Member("total")!.Value.IsNull)
            {
                continue;
            }

            Assert.True(body.Member(r.Member("total")!.Value.AsString()) is { IsNumber: true });
        }
    }

    [Fact]
    public void NegativeUpNextNamesNoCountBecauseItsListIsNotCapped()
    {
        var upNext = Compose([Done("a", 1)]).Member("regions")!.Value.AsArray().Single(static r => r.Member("list")!.Value.AsString() == "up_next");

        Assert.True(upNext.Member("total")!.Value.IsNull);
    }

    [Fact]
    public void CompletedIsTheTenNewestDoneItemsByDoneAtAndTheTotalCountsThemAll()
    {
        var body = Compose([.. Enumerable.Range(1, 12).Select(i => Done("i" + i, i))]);

        Assert.Equal([.. Enumerable.Range(3, 10).Reverse().Select(i => "s:i" + i)], Keys(body, "completed"));
        Assert.Equal(12, body.Member("completed_total")!.Value.AsNumber());
        Assert.Equal("item", body.Member("completed")!.Value.AsArray()[0].Member("kind")!.Value.AsString());
        Assert.Equal(Ms(12), body.Member("as_of")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeADroppedOrReopenedItemIsNotCompleted()
    {
        var body = Compose([Done("a", 1), new("mgr", null, "s", Plan("b", "dropped", Iso(2))), new("mgr", null, "s", Plan("c", "in_progress", since: Iso(3)))]);

        Assert.Equal(["s:a"], Keys(body, "completed"));
        Assert.Equal(1, body.Member("completed_total")!.Value.AsNumber());
    }

    [Fact]
    public void ADoneItemWithNoDoneEventSortsByItsOwnStateSinceThenItsUpdatedAt()
    {
        var body = Compose(
        [
            Done("a", 5),
            new("mgr", null, "s", Plan("legacy", "done", since: Iso(9))),
            new("mgr", null, "s", Plan("older", "done") with { UpdatedAt = Iso(3) }),
        ]);

        Assert.Equal(["s:legacy", "s:a", "s:older"], Keys(body, "completed"));
        Assert.Equal(Ms(9), body.Member("completed")!.Value.AsArray()[0].Member("done_at")!.Value.AsNumber());
        Assert.Equal(Ms(3), body.Member("completed")!.Value.AsArray()[2].Member("done_at")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeAnUndatedDoneItemSortsLastAndSetsNoFloor()
    {
        var body = Compose([Done("a", 5), Done("b", 2), new("mgr", null, "s", Plan("undated", "done"))], ledger: [Record("ship", "x", 4), Record("ship", "y", 1)]);

        Assert.Equal(["s:a", "ship:x:main", "s:b", "s:undated"], Keys(body, "completed"));
        Assert.True(body.Member("completed")!.Value.AsArray()[3].Member("done_at")!.Value.IsNull);
    }

    [Fact]
    public void QueueRowsCarryTheirStallAtAndBacklogRowsANullOne()
    {
        var body = Compose(queued: [Queued("q", 3)], backlog: [Plan("b", "not_started", wave: "now", rank: 1)]);
        var rows = body.Member("up_next")!.Value.AsArray();

        Assert.Equal(Ms(13), rows[0].Member("stall_at")!.Value.AsNumber());
        Assert.True(rows[1].Member("stall_at")!.Value.IsNull);
    }

    [Fact]
    public void NegativeTheBacklogIsNotReadWhenTheQueueFillsTheCap()
    {
        var asked = 0;
        SitrepRows.Queued[] queued = [.. Enumerable.Range(1, 10).Select(i => Queued("q" + i, i))];

        _ = SitrepRows.Compose([], queued, () => { asked++; return []; }, () => { asked++; return []; }, []);
        Assert.Equal(0, asked);

        _ = SitrepRows.Compose([], queued[..9], () => { asked++; return []; }, () => { asked++; return []; }, []);
        Assert.Equal(1, asked);
    }

    [Fact]
    public void ABacklogRowPlannedByAnotherTeamsItemInTheDirectoryIsNotUpNext()
    {
        var epic = Plan("epic-1", "not_started", wave: "now", rank: 1) with { Kind = "epic" };
        var elsewhere = Plan("c", "in_progress", since: Iso(1)) with { Parent = "epic-1" };

        Assert.Empty(Keys(Compose(backlog: [epic], dirItems: [elsewhere]), "up_next"));
        Assert.Equal(["epic-1"], Keys(Compose(backlog: [epic], dirItems: []), "up_next"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void NegativeAShipOrMergeRowWithNoShaIsSkipped(string? sha)
    {
        var row = JsonText.Parse("{\"kind\":\"ship\"," + (sha is null ? "" : "\"sha\":\"" + sha + "\",") + "\"ref\":\"main\",\"at\":\"" + Iso(2) + "\"}");

        Assert.Equal(["ship:ok:main"], Keys(Compose(ledger: [new("mgr", null, row), Record("ship", "ok", 1)]), "completed"));
    }

    [Fact]
    public void LaneItemsAreIncludedWithTheirLaneAndTeam()
    {
        var body = Compose([Done("a", 1), Done("b", 2, team: "mgr-s1", lane: "s1")]);
        var top = body.Member("completed")!.Value.AsArray()[0];

        Assert.Equal("mgr-s1", top.Member("team")!.Value.AsString());
        Assert.Equal("s1", top.Member("lane")!.Value.AsString());
        Assert.True(body.Member("completed")!.Value.AsArray()[1].Member("lane")!.Value.IsNull);
    }

    [Fact]
    public void InProgressLeadsWithOldReportsSoTheCapKeepsThem()
    {
        var items = Enumerable.Range(1, 12).Select(i => new SitrepRows.Item("mgr", null, "s", Plan("p" + i, "in_progress", since: Iso(20 - i)) with { Holder = "impl", UpdatedAt = Iso(20 - i) })).ToList();
        items.Add(new("mgr", null, "s", Plan("old", "in_review", since: Iso(1)) with { UpdatedAt = Iso(1) }));
        items.Add(new("mgr", null, "s", Plan("older", "approved", since: Iso(0)) with { UpdatedAt = Iso(0) }));

        var keys = Keys(Compose(items, live: AllWorking), "in_progress");

        Assert.Equal(["s:old", "s:older", "s:p1"], keys.Take(3));
        Assert.Equal(10, keys.Count);
    }

    [Fact]
    public void InProgressIsWorkingItemsNewestActivityFirstCappedAtTenWithATotal()
    {
        var items = Enumerable.Range(1, 12).Select(i => new SitrepRows.Item("mgr", null, "s", Plan("p" + i, "in_progress", since: Iso(20 - i)) with { Holder = "impl", UpdatedAt = Iso(20 - i) })).ToList();
        items.Add(new("mgr", null, "s", Plan("q", "next", since: Iso(0))));
        items.Add(new("mgr", null, "s", Plan("n", "not_started", since: Iso(0))));

        var body = Compose(items, live: AllWorking);

        Assert.Equal([.. Enumerable.Range(1, 10).Select(i => "s:p" + i)], Keys(body, "in_progress"));
        Assert.Equal(12, body.Member("in_progress_total")!.Value.AsNumber());
        Assert.Equal(Ms(19), body.Member("in_progress")!.Value.AsArray()[0].Member("last_activity")!.Value.AsNumber());
    }

    [Fact]
    public void EachOpenItemIsInTheOneSectionItsWorkNames()
    {
        var body = Compose(
        [
            new("mgr", null, "s", Plan("b", "blocked") with { UpdatedAt = Iso(2) }),
            new("mgr", null, "s", Plan("w", "waiting_user") with { UpdatedAt = Iso(3), Note = "which one?", Holder = "impl-14", AskedBy = "orchestrator@mgr" }),
            new("mgr", null, "s", Plan("p", "in_progress") with { UpdatedAt = Iso(1), Holder = "impl" }),
            new("mgr", null, "s", Plan("idle", "in_progress") with { UpdatedAt = Iso(4), Holder = "other" }),
            new("mgr", null, "s", Plan("r", "in_review") with { UpdatedAt = Iso(5), Holder = "impl" }),
        ], live: AllWorking);

        Assert.Equal(["s:b"], Keys(body, "blocked"));
        Assert.Equal(1, body.Member("blocked_total")!.Value.AsNumber());
        Assert.Equal(["s:w"], Keys(body, "waiting_on_you"));
        Assert.Equal(1, body.Member("waiting_on_you_total")!.Value.AsNumber());
        Assert.Equal("which one?", body.Member("waiting_on_you")!.Value.AsArray()[0].Member("note")!.Value.AsString());

        // a waiting row names its asker, not its stale holder; negative: a working row keeps its holder and names no asker
        Assert.Equal("orchestrator@mgr", body.Member("waiting_on_you")!.Value.AsArray()[0].Member("asker")!.Value.AsString());
        Assert.True(body.Member("waiting_on_you")!.Value.AsArray()[0].Member("holder")!.Value.IsNull);
        var working = body.Member("in_progress")!.Value.AsArray().Single(static r => r.Member("key")!.Value.AsString() == "s:p");
        Assert.Equal("impl", working.Member("holder")!.Value.AsString());
        Assert.True(working.Member("asker")!.Value.IsNull);
        Assert.Equal(["s:r", "s:p"], Keys(body, "in_progress"));
        Assert.Equal(2, body.Member("in_progress_total")!.Value.AsNumber());
        Assert.Equal(["s:idle"], Keys(body, "not_moving"));
        Assert.Equal(["no_one"], body.Member("not_moving")!.Value.AsArray().Select(static r => r.Member("work")!.Value.AsString()));
    }

    [Fact]
    public void ADecisionNamingAWaitingItemCountsOnceAndNegativeOneNamingNoItemIsItsOwnRow()
    {
        var named = JsonText.Parse("{\"id\":\"d1\",\"question\":\"Named?\",\"item\":\"w\"}");
        var free = JsonText.Parse("{\"id\":\"d2\",\"question\":\"Free?\",\"item\":null}");
        var body = Compose([new("mgr", null, "s", Plan("w", "waiting_user"))], decisions: [("mgr", named), ("mgr", free)]);

        Assert.Equal(["d1", "d2"], Keys(body, "waiting_on_you"));
        Assert.Equal(2, body.Member("waiting_on_you_total")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeADecisionOfOneTeamDoesNotHideAnItemOfTheSameNameInAnother()
    {
        var named = JsonText.Parse("{\"id\":\"d1\",\"question\":\"Named?\",\"item\":\"w\"}");
        var body = Compose([new("mgr", null, "s", Plan("w", "waiting_user")), new("lane", "lane", "s", Plan("w", "waiting_user"))], decisions: [("mgr", named)]);

        Assert.Equal(["d1", "s:w"], Keys(body, "waiting_on_you"));
        Assert.Equal(2, body.Member("waiting_on_you_total")!.Value.AsNumber());
    }

    [Fact]
    public void AnAnsweredItemShowsItsAnswerUnderNotMovingAndNegativeOthersCarryNone()
    {
        var body = Compose([new("mgr", null, "s", Plan("w", "waiting_orch") with { Answer = "Ship it" }), new("mgr", null, "s", Plan("x", "waiting_orch"))]);

        var rows = body.Member("not_moving")!.Value.AsArray();
        Assert.Equal("Ship it", rows.Single(static r => r.Member("key")!.Value.AsString() == "s:w").Member("answer")!.Value.AsString());
        Assert.Null(rows.Single(static r => r.Member("key")!.Value.AsString() == "s:x").Member("answer"));
    }

    [Fact]
    public void NegativeAWaitingItemThreeDaysOldIsNotStale()
    {
        var body = Compose([new("mgr", null, "s", Plan("w", "waiting_user") with { UpdatedAt = Iso(0) })], nowMs: Ms(3 * 24 * 60));

        Assert.Equal(["s:w"], Keys(body, "waiting_on_you"));
        Assert.Equal(0, body.Member("stale_total")!.Value.AsNumber());
    }

    [Fact]
    public void AnItemIdleForMoreThanADayIsStaleAndNotCountedInProgress()
    {
        var now = Ms(25 * 60);
        var body = Compose(
        [
            new("mgr", null, "s", Plan("old", "in_progress") with { UpdatedAt = Iso(0) }),
            new("mgr", null, "s", Plan("held", "blocked") with { UpdatedAt = Iso(0) }),
            new("mgr", null, "s", Plan("fresh", "in_progress") with { UpdatedAt = Iso(2 * 60), Holder = "impl" }),
        ], nowMs: now, live: AllWorking);

        Assert.Equal(["s:fresh"], Keys(body, "in_progress"));
        Assert.Equal(1, body.Member("in_progress_total")!.Value.AsNumber());
        Assert.Equal(1, body.Member("stale_total")!.Value.AsNumber());
        Assert.Equal(["s:old"], Keys(body, "stale"));
        Assert.Equal(["s:held"], Keys(body, "blocked")); // blocked stays on show at any age
    }

    [Fact]
    public void NegativeAnItemIdleJustUnderADayOrAQueuedItemIdleTwoDaysIsNotStale()
    {
        var now = Ms(48 * 60);
        var body = Compose(
        [
            new("mgr", null, "s", Plan("recent", "in_progress") with { UpdatedAt = Iso((24 * 60) + 1) }),
            new("mgr", null, "s", Plan("q", "next") with { UpdatedAt = Iso(0) }),
            new("mgr", null, "s", Plan("n", "not_started") with { UpdatedAt = Iso(0) }),
        ], nowMs: now);

        Assert.Equal(["s:recent"], Keys(body, "not_moving"));
        Assert.Equal(0, body.Member("stale_total")!.Value.AsNumber());
    }

    [Fact]
    public void ALedgerRowNamingAnItemsRefCountsAsItsActivity()
    {
        var item = Plan("p", "in_progress") with { UpdatedAt = Iso(0), Refs = ["20261031-120000-aaaa"] };
        var row = JsonText.Parse("{\"kind\":\"verdict\",\"ref\":\"20261031-120000-aaaa\",\"at\":\"" + Iso(24 * 60) + "\"}");

        var body = Compose([new("mgr", null, "s", item)], ledger: [new("mgr", null, row)], nowMs: Ms(25 * 60));

        Assert.Equal(["s:p"], Keys(body, "not_moving"));
        Assert.Equal(Ms(24 * 60), body.Member("not_moving")!.Value.AsArray()[0].Member("last_activity")!.Value.AsNumber());
    }

    [Fact]
    public void AsOfIsTheNewestPlanOrLedgerRowEvenWhenThatRowIsNotShown()
    {
        var items = Enumerable.Range(1, 12).Select(i => new SitrepRows.Item("mgr", null, "s", Plan("p" + i, "in_progress") with { UpdatedAt = Iso(i) })).ToList();
        items.Add(new("mgr", null, "s", Plan("hidden", "not_started") with { UpdatedAt = Iso(100) }));
        var verdict = JsonText.Parse("{\"kind\":\"verdict\",\"ref\":\"x\",\"at\":\"" + Iso(90) + "\"}");

        Assert.Equal(Ms(100), Compose(items, ledger: [new("mgr", null, verdict)]).Member("as_of")!.Value.AsNumber());
        Assert.Equal(Ms(120), Compose(items, ledger: [new("mgr", null, JsonText.Parse("{\"kind\":\"verdict\",\"ref\":\"x\",\"at\":\"" + Iso(120) + "\"}"))]).Member("as_of")!.Value.AsNumber());
    }

    [Fact]
    public void UpNextIsTheQueueInOrderThenUnplannedNowBacklogRowsByRankUnderTheCap()
    {
        var queued = Enumerable.Range(1, 8).Select(i => Queued("q" + i, i, idle: i == 1)).ToList();
        PlanItem[] backlog =
        [
            Plan("b-late", "not_started", wave: "now", rank: 3),
            Plan("b-first", "in_progress", wave: "now", rank: 1),
            Plan("b-second", "not_started", wave: "now", rank: 2),
            Plan("b-next-wave", "not_started", wave: "next", rank: 0),
            Plan("b-done", "done", wave: "now", rank: 0),
            Plan("b-dropped", "dropped", wave: "now", rank: 0),
        ];

        var body = Compose(queued: queued, backlog: backlog);
        var rows = body.Member("up_next")!.Value.AsArray();

        Assert.Equal([.. Enumerable.Range(1, 8).Select(i => "s:q" + i), "b-first", "b-second"], Keys(body, "up_next"));
        Assert.Equal("queue", rows[0].Member("source")!.Value.AsString());
        Assert.True(rows[0].Member("idle_eligible")!.Value.AsBool());
        Assert.Equal(["impl-1"], rows[0].Member("could_take")!.Value.AsArray().Select(static n => n.AsString()));
        Assert.False(rows[1].Member("idle_eligible")!.Value.AsBool());
        Assert.Equal("Title q1", rows[0].Member("title")!.Value.AsString());
        Assert.Equal("backlog", rows[8].Member("source")!.Value.AsString());
    }

    [Fact]
    public void NegativeAFullQueueLeavesNoRoomForTheBacklog()
    {
        var body = Compose(queued: [.. Enumerable.Range(1, 11).Select(i => Queued("q" + i, i))], backlog: [Plan("b", "not_started", wave: "now", rank: 1)]);

        Assert.Equal([.. Enumerable.Range(1, 10).Select(i => "s:q" + i)], Keys(body, "up_next"));
    }

    [Fact]
    public void AnUpNextRowOverADayOldWithNoIdleMemberIsOldAndCounted()
    {
        var body = Compose(queued: [Queued("old", 0), Queued("about-to-run", 0, idle: true), Queued("recent", 3 * 24 * 60 - 5), Queued("at-the-line", 2 * 24 * 60)], backlog: [Plan("b", "not_started", wave: "now", rank: 1)], nowMs: Ms(3 * 24 * 60));

        Assert.Equal(["s:old"], Keys(body, "up_next_old"));
        Assert.Equal(1, body.Member("up_next_old_total")!.Value.AsNumber());
        Assert.Equal(["s:about-to-run", "s:recent", "s:at-the-line", "b"], Keys(body, "up_next"));
    }

    [Fact]
    public void NegativeWithNoClockNothingInUpNextIsOld()
    {
        var body = Compose(queued: [Queued("old", 0)]);

        Assert.Empty(Keys(body, "up_next_old"));
        Assert.Equal(0, body.Member("up_next_old_total")!.Value.AsNumber());
        Assert.Equal(["s:old"], Keys(body, "up_next"));
    }

    [Fact]
    public void NegativeABacklogRowIsNeverOld()
    {
        var body = Compose(backlog: [Plan("b", "not_started", since: Iso(0), wave: "now", rank: 1)], nowMs: Ms(30 * 24 * 60));

        Assert.Empty(Keys(body, "up_next_old"));
        Assert.Equal(["b"], Keys(body, "up_next"));
    }

    [Fact]
    public void NegativeABacklogRowAnItemInScopeValidlyNamesAsParentIsPlannedButAnInvalidLinkIsNot()
    {
        var epic = Plan("epic-1", "not_started", wave: "now", rank: 1) with { Kind = "epic" };
        var task = Plan("task-1", "not_started", wave: "now", rank: 2);
        var child = new SitrepRows.Item("mgr", null, "s", Plan("c", "in_progress", since: Iso(1)) with { Parent = "epic-1" });
        var sameRank = new SitrepRows.Item("mgr", null, "s", Plan("d", "next", since: Iso(1)) with { Parent = "task-1" });

        var body = Compose([child, sameRank], backlog: [epic, task]);

        Assert.Equal(["task-1"], Keys(body, "up_next"));
    }

    [Fact]
    public void ATeamWithNoItemsHasANullAsOfAndEmptyColumns()
    {
        var body = Compose();

        Assert.True(body.Member("as_of")!.Value.IsNull);
        Assert.Empty(body.Member("completed")!.Value.AsArray());
        Assert.Empty(body.Member("up_next")!.Value.AsArray());
    }

    [Fact]
    public void TheSameInputsComposeTheSameBytesSoAsOfDoesNotMoveWithoutAChange()
    {
        SitrepRows.Item[] items = [Done("a", 1), new("mgr", null, "s", Plan("b", "in_progress", since: Iso(2)))];
        SitrepRows.Queued[] queued = [Queued("q", 3)];

        Assert.Equal(JsonText.Stringify(Compose(items, queued), 0), JsonText.Stringify(Compose(items, queued), 0));
        Assert.Equal(Ms(3), Compose(items, queued).Member("as_of")!.Value.AsNumber());
    }

    [Fact]
    public void RecordsInterleaveWithItemsByTimeAndItemsComeFirstOnATie()
    {
        var body = Compose([Done("a", 1), Done("b", 5)], ledger: [Record("ship", "s3", 3), Record("merge", "m5", 5), Record("ship", "s7u", 7)]);
        var rows = body.Member("completed")!.Value.AsArray();

        Assert.Equal(["ship:s7u:main", "s:b", "merge:m5:main", "ship:s3:main", "s:a"], Keys(body, "completed"));
        Assert.False(rows[0].Member("confirmed")!.Value.AsBool());
        Assert.Equal("main", rows[2].Member("into")!.Value.AsString());
        Assert.Null(rows[2].Member("tag"));
        Assert.Equal(Ms(7), rows[0].Member("at")!.Value.AsNumber());
        Assert.Equal(2, body.Member("completed_total")!.Value.AsNumber());
        Assert.Equal(Ms(7), body.Member("as_of")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeARecordOlderThanTheOldestOfTheTenItemsIsLeftOut()
    {
        var body = Compose([.. Enumerable.Range(10, 10).Select(i => Done("i" + i, i))], ledger: [Record("ship", "old", 9), Record("ship", "edge", 10)]);

        Assert.Contains("ship:edge:main", Keys(body, "completed"));
        Assert.DoesNotContain("ship:old:main", Keys(body, "completed"));
    }

    [Fact]
    public void WithNoDoneItemsTheTenNewestRecordsAppear()
    {
        var body = Compose(ledger: [.. Enumerable.Range(1, 12).Select(i => Record("ship", "r" + i, i))]);

        Assert.Equal([.. Enumerable.Range(3, 10).Reverse().Select(i => "ship:r" + i + ":main")], Keys(body, "completed"));
        Assert.Equal(0, body.Member("completed_total")!.Value.AsNumber());
    }

    [Fact]
    public void RecordsAreCappedAtTenBesideTheTenItems()
    {
        var body = Compose([.. Enumerable.Range(1, 10).Select(i => Done("i" + i, i))], ledger: [.. Enumerable.Range(1, 15).Select(i => Record("merge", "r" + i, i))]);

        Assert.Equal(20, Keys(body, "completed").Count);
        Assert.Equal(10, Keys(body, "completed").Count(static k => k.StartsWith("merge:", StringComparison.Ordinal)));
    }

    [Fact]
    public void OneRecordInTheParentAndALaneLedgerIsOneRowWithItsLaneAndTheEarliestTime()
    {
        var body = Compose(ledger: [Record("ship", "x", 4), Record("ship", "x", 2, team: "mgr-s1", lane: "s1"), Record("merge", "x", 3)]);
        var rows = body.Member("completed")!.Value.AsArray();

        Assert.Equal(["ship:x:main", "merge:x:main"], Keys(body, "completed").Order(StringComparer.Ordinal).Reverse());
        var ship = rows.Single(static r => r.Member("kind")!.Value.AsString() == "ship");
        Assert.Equal("s1", ship.Member("lane")!.Value.AsString());
        Assert.Equal(Ms(2), ship.Member("at")!.Value.AsNumber());
    }

    [Theory]
    [InlineData("verdict")]
    [InlineData("decision")]
    [InlineData("train")]
    [InlineData("shipped")]
    public void NegativeOtherLedgerKindsAreIgnored(string kind)
    {
        Assert.Empty(Keys(Compose(ledger: [Record(kind, "x", 1)]), "completed"));
    }

    [Theory]
    [InlineData("yesterday")]
    [InlineData("")]
    public void NegativeARecordWhoseAtDoesNotParseIsSkipped(string at)
    {
        Assert.Equal(["ship:ok:main"], Keys(Compose(ledger: [Record("ship", "bad", 1, at: at), Record("ship", "ok", 2)]), "completed"));
    }
}
