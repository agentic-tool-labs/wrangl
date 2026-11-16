using System.Globalization;
using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>The stage fold (spec ah-stage-timing §1, §3): stage per trail row, rework, group union, batches and the critical path.</summary>
public sealed class StageTimingTests
{
    private const string Sha = "374a3da44aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Sha2 = "b0b0b0b0bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static string T(string hhmm) => $"2026-11-01T{hhmm}:00Z";

    private static double Ms(string hhmm) => DateTimeOffset.Parse(T(hhmm), CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

    private static string Row(string hhmm, string item, string ev, string? extra = null) =>
        $"{{\"v\":1,\"ts\":\"{T(hhmm)}\",\"team\":\"tm\",\"stream\":\"s\",\"item\":\"{item}\",\"event\":\"{ev}\"{extra},\"by\":\"orchestrator@tm\"}}";

    private static string Cause(string ev, string at, string reference = "r") => $",\"cause\":{{\"event\":\"{ev}\",\"ref\":\"{reference}\",\"at\":\"{T(at)}\"}}";

    /// <summary>An item added with a role and a ref request, started at <paramref name="start"/>.</summary>
    private static string[] Item(string item, string role, string request, string start) =>
    [
        Row("09:00", item, "add", $",\"title\":\"{item}\""),
        Row("09:00", item, "role", $",\"role\":\"{role}\""),
        Row("09:00", item, "ref", $",\"ref\":\"{request}\""),
        Row(start, item, "start"),
    ];

    private static string Log(params IEnumerable<string>[] parts) => string.Join('\n', parts.SelectMany(static p => p));

    private static JsonValue Ledger(string kind, string at, string? sha = null, string? reference = null, string? phase = null, string? slug = null) =>
        JsonText.Parse(JsonText.Stringify(
            new JsonObject
            {
                ["kind"] = kind, ["at"] = T(at), ["sha"] = sha is null ? JsonValue.Null : sha, ["ref"] = reference is null ? JsonValue.Null : reference,
                ["phase"] = phase is null ? JsonValue.Null : phase, ["slug"] = slug is null ? JsonValue.Null : slug,
            },
            0));

    private static string? Slug(string request) => request.StartsWith("req-", StringComparison.Ordinal) ? request[4..] : null;

    private static StageTimingReport Fold(string log, params JsonValue[] ledger) => HierarchyModule.StageTiming([log], ledger, Slug, Ms("23:00"));

    private static double S(double seconds) => seconds * 1000;

    [Theory]
    [InlineData("in_progress", "architect", null, "spec")]
    [InlineData("in_progress", "implementor", null, "build")]
    [InlineData("in_progress", "ux-engineer", null, "build")]
    [InlineData("in_progress", "docs-writer", null, "build")]
    [InlineData("in_progress", null, null, "build")]
    [InlineData("in_progress", "implementor", "reply", "rework")]
    [InlineData("in_progress", "implementor", "manual", "build")]
    [InlineData("in_review", "implementor", "reply", "review")]
    [InlineData("in_review", null, null, "review")]
    [InlineData("waiting_orch", "implementor", null, "orch")]
    [InlineData("blocked", "implementor", null, "blocked")]
    [InlineData("waiting_user", "architect", null, "blocked")]
    [InlineData("approved", "implementor", null, "ship_wait")]
    [InlineData("in_progress", "reviewer", null, "review")]
    [InlineData("in_progress", "qa", null, "qa")]
    [InlineData("not_started", "implementor", null, "queued")]
    [InlineData("next", "implementor", null, "queued")]
    [InlineData("done", "implementor", "ship", null)]
    [InlineData("dropped", "implementor", null, null)]
    public void EachStateAndRoleMapsToItsStageAndOnlyAReplyCausedStartIsRework(string state, string? role, string? cause, string? stage) =>
        Assert.Equal(stage, HierarchyModule.StageOf(state, role, cause is null ? null : new PlanCause(cause, "r", T("10:00"))));

    [Fact]
    public void AFailVerdictIsOneReworkRoundAndAShipClosesTheItemIntoItsBatchWithTheCriticalPath()
    {
        var log = Log(
            Item("a", "implementor", "req-x", "10:00"),
            [
                Row("11:00", "a", "review", Cause("reply", "11:00")),
                Row("11:30", "a", "start", Cause("reply", "11:30")),
                Row("11:40", "a", "note", ",\"note\":\"re-dispatched\""),
                Row("12:00", "a", "review", Cause("reply", "12:00")),
                Row("12:10", "a", "approve", Cause("reply", "12:10")),
                Row("12:30", "a", "done", Cause("ship", "12:30", Sha)),
            ]);

        var report = Fold(log, Ledger("ship", "12:30", Sha));

        var group = Assert.Single(report.Groups);
        Assert.Equal("x", group.Key);
        Assert.Equal(1, group.ReworkRounds);
        Assert.Equal(S(3600), group.StageMs["build"]);
        Assert.Equal(S(1800), group.StageMs["rework"]);
        Assert.Equal(S(2400), group.StageMs["review"]);
        Assert.Equal(S(1200), group.StageMs["ship_wait"]);
        Assert.Equal(S(9000), group.WallMs);
        Assert.False(group.Open);
        var batch = Assert.Single(report.Batches);
        Assert.Equal(["a"], batch.Items);
        Assert.Equal(S(9000), batch.WallMs);
        Assert.Equal(new StageCritical("x", "review", S(1200)), batch.Critical);
    }

    [Fact]
    public void CauseAtIsPreferredOverTs()
    {
        var log = Log(Item("a", "implementor", "req-x", "10:00"), [Row("11:05", "a", "review", Cause("reply", "11:00"))]);

        Assert.Equal(S(3600), Fold(log).Groups.Single().StageMs["build"]);
    }

    [Fact]
    public void ItemsSharingASlugAreOneGroupAndOverlapIsNotCountedTwice()
    {
        var log = Log(
            Item("a", "implementor", "req-x", "10:00"), [Row("11:00", "a", "done")],
            Item("b", "docs-writer", "req-x", "10:30"), [Row("11:30", "b", "done")],
            Item("c", "implementor", "req-other", "10:00"), [Row("10:10", "c", "done")]);

        var report = Fold(log);

        var x = report.Groups.Single(static g => g.Key == "x");
        Assert.Equal(["a", "b"], x.Items);
        Assert.Equal(S(5400), x.StageMs["build"]);
        Assert.Equal(S(5400), x.WallMs);
        Assert.Equal(S(600), report.Groups.Single(static g => g.Key == "other").StageMs["build"]);
    }

    [Fact]
    public void AnItemWithNoRefIsItsOwnGroupAndAnOpenOneShowsItsCurrentStage()
    {
        var log = Log([Row("09:00", "solo", "add", ",\"title\":\"solo\""), Row("22:00", "solo", "wait-orch", ",\"note\":\"answer\"")]);

        var group = Assert.Single(Fold(log).Groups);

        Assert.Equal("solo", group.Key);
        Assert.True(group.Open);
        Assert.Equal("orch", group.CurrentStage);
        Assert.Equal(S(3600), group.CurrentAgeMs);
    }

    [Fact]
    public void BatchMembershipIsByCauseRefAndTheLastGroupToBecomeReadyGatesTheShip()
    {
        var log = Log(
            Item("a", "implementor", "req-early", "10:00"), [Row("10:30", "a", "review", Cause("reply", "10:30")), Row("10:40", "a", "approve", Cause("reply", "10:40")), Row("12:00", "a", "done", Cause("ship", "12:00", Sha))],
            Item("b", "implementor", "req-late", "10:00"), [Row("11:30", "b", "review", Cause("reply", "11:30")), Row("11:50", "b", "approve", Cause("reply", "11:50")), Row("12:00", "b", "done", Cause("ship", "12:00", Sha))],
            Item("c", "implementor", "req-other", "10:00"), [Row("11:00", "c", "done", Cause("ship", "11:00", Sha2))],
            Item("m", "implementor", "req-manual", "10:00"), [Row("11:00", "m", "done", Cause("manual", "11:00", string.Empty))]);

        var report = Fold(log, Ledger("ship", "11:00", Sha2), Ledger("ship", "12:00", Sha));

        var batch = report.Batches.Single(static b => b.Sha == Sha);
        Assert.Equal(["a", "b"], batch.Items);
        Assert.Equal(["early", "late"], batch.Groups.Order(StringComparer.Ordinal));
        Assert.Equal(new StageCritical("late", "review", S(600)), batch.Critical);
        Assert.Equal(["c"], report.Batches.Single(static b => b.Sha == Sha2).Items);
        Assert.DoesNotContain(report.Batches, static b => b.Items.Contains("m"));
        Assert.Equal(S(3600), report.Groups.Single(static g => g.Key == "manual").StageMs["build"]);
    }

    [Fact]
    public void AShipThatClosedNothingIsABatchWithNoItemsNoWallAndNoCriticalPath()
    {
        var batch = Assert.Single(Fold(Log(Item("a", "implementor", "req-x", "10:00")), Ledger("ship", "12:00", Sha)).Batches);

        Assert.Empty(batch.Items);
        Assert.Equal(0, batch.WallMs);
        Assert.Null(batch.Critical);
    }

    [Fact]
    public void AMergeRowAndAShipRowForTheSameShaAreOneBatchAndALoneMergeIsABatch()
    {
        var report = Fold(Log(Item("a", "implementor", "req-x", "10:00")), Ledger("merge", "11:00", Sha), Ledger("ship", "12:00", Sha), Ledger("merge", "13:00", Sha2));

        Assert.Equal([(Sha, T("12:00")), (Sha2, T("13:00"))], report.Batches.Select(static b => (b.Sha, b.At)));
    }

    [Fact]
    public void ADroppedItemCountsUpToTheDropAndIsInNoBatch()
    {
        var log = Log(Item("a", "implementor", "req-x", "10:00"), [Row("10:20", "a", "drop", ",\"note\":\"n\"" + Cause("withdraw", "10:20"))]);

        var report = Fold(log, Ledger("ship", "12:00", Sha));

        Assert.Equal(S(1200), report.Groups.Single().StageMs["build"]);
        Assert.False(report.Groups.Single().Open);
        Assert.Empty(report.Batches.Single().Items);
    }

    [Fact]
    public void AnOldLogWithNoCauseRowsFoldsWithoutError()
    {
        var log = Log([Row("09:00", "a", "add", ",\"title\":\"a\""), Row("10:00", "a", "start"), Row("11:00", "a", "done", ",\"commits\":[\"abc1234\"]"), "not json"]);

        var report = Fold(log, Ledger("ship", "12:00", Sha));

        Assert.Equal(S(3600), report.Groups.Single().StageMs["build"]);
        Assert.Empty(report.Batches.Single().Items);
    }

    [Fact]
    public void QaAfterEveryGroupIsReadyGatesTheShipAndIsAttributedToTheNextShipOnly()
    {
        var log = Log(Item("a", "implementor", "req-x", "10:00"), [Row("10:30", "a", "review", Cause("reply", "10:30")), Row("10:40", "a", "approve", Cause("reply", "10:40")), Row("12:00", "a", "done", Cause("ship", "12:00", Sha))]);

        var report = Fold(
            log,
            Ledger("ship", "09:30", Sha2),
            Ledger("qa", "11:00", reference: "q1", phase: "start", slug: "x"),
            Ledger("qa", "11:45", reference: "q1", phase: "end", slug: "x"),
            Ledger("ship", "12:00", Sha));

        var batch = report.Batches.Single(static b => b.Sha == Sha);
        Assert.Equal(new StageCritical("x", "qa", S(900)), batch.Critical);
        Assert.Equal(S(2700), batch.StageMs["qa"]);
        Assert.Equal(0, report.Batches.Single(static b => b.Sha == Sha2).StageMs["qa"]);
        Assert.Equal(S(2700), report.Groups.Single(static g => g.Key == "x").StageMs["qa"]);
    }

    [Fact]
    public void NegativeQaThatEndsBeforeTheLastGroupIsReadyDoesNotGate()
    {
        var log = Log(Item("a", "implementor", "req-x", "10:00"), [Row("11:30", "a", "review", Cause("reply", "11:30")), Row("11:50", "a", "approve", Cause("reply", "11:50")), Row("12:00", "a", "done", Cause("ship", "12:00", Sha))]);

        var batch = Fold(log, Ledger("qa", "10:00", reference: "q1", phase: "start"), Ledger("qa", "10:30", reference: "q1", phase: "end"), Ledger("ship", "12:00", Sha)).Batches.Single();

        Assert.Equal(new StageCritical("x", "review", S(600)), batch.Critical);
        Assert.Equal(S(1800), batch.StageMs["qa"]);
    }

    [Fact]
    public void AnUpstreamItemWaitingOnTheOrchestratorStopsCountingWhenTheNextItemOfItsGroupStarts()
    {
        var log = Log(
            Item("spec", "architect", "req-x", "10:00"), [Row("11:00", "spec", "wait-orch", ",\"note\":\"answer\"")],
            Item("impl", "implementor", "req-x2", "11:30"));

        var group = Assert.Single(HierarchyModule.StageTiming([log], [], static r => r.StartsWith("req-x", StringComparison.Ordinal) ? "x" : null, Ms("23:00")).Groups);

        Assert.Equal("build", group.CurrentStage);
        Assert.Equal(S(1800), group.StageMs["orch"]);
        Assert.Equal(S(3600), group.StageMs["spec"]);
        Assert.Equal(Ms("23:00") - Ms("11:30"), group.StageMs["build"]);
    }

    [Fact]
    public void AReviewerItemStartingLaterDoesNotDropTheImplementorsRework()
    {
        var log = Log(
            Item("spec", "architect", "req-x", "10:00"), [Row("11:00", "spec", "wait-orch", ",\"note\":\"answer\"")],
            Item("impl", "implementor", "req-x", "11:30"),
            [
                Row("12:30", "impl", "review", Cause("reply", "12:30")),
                Row("13:00", "impl", "start", Cause("reply", "13:00")),
                Row("14:00", "impl", "review", Cause("reply", "14:00")),
            ],
            Item("rev", "reviewer", "req-x", "12:40"), [Row("12:55", "rev", "done", Cause("reply", "12:55"))]);

        var group = Assert.Single(Fold(log).Groups);

        Assert.Equal(1, group.ReworkRounds);
        Assert.Equal(S(3600), group.StageMs["rework"]);
        Assert.Equal(S(3600), group.StageMs["build"]);
        Assert.Equal(S(1800), group.StageMs["orch"]);
    }

    [Fact]
    public void NegativeAnUpstreamItemWithNoLaterItemKeepsWaitingToNow()
    {
        var log = Log(Item("spec", "architect", "req-x", "10:00"), [Row("11:00", "spec", "wait-orch", ",\"note\":\"answer\"")]);

        var group = Fold(log).Groups.Single();

        Assert.Equal("orch", group.CurrentStage);
        Assert.Equal(Ms("23:00") - Ms("11:00"), group.StageMs["orch"]);
    }

    [Fact]
    public void AnOpenQaRunOrOneEndingAfterTheShipIsNotInTheBatchAndDoesNotGate()
    {
        var log = Log(Item("a", "implementor", "req-x", "10:00"), [Row("10:30", "a", "review", Cause("reply", "10:30")), Row("10:40", "a", "approve", Cause("reply", "10:40")), Row("12:00", "a", "done", Cause("ship", "12:00", Sha))]);

        var report = Fold(
            log,
            Ledger("qa", "11:00", reference: "q1", phase: "start", slug: "x"),
            Ledger("qa", "11:10", reference: "q2", phase: "start", slug: "x"),
            Ledger("ship", "12:00", Sha),
            Ledger("qa", "12:30", reference: "q2", phase: "end", slug: "x"));

        var batch = report.Batches.Single();
        Assert.Equal(new StageCritical("x", "review", S(4800)), batch.Critical);
        Assert.Equal(0, batch.StageMs["qa"]);
        Assert.True(report.Groups.Single().Open);
    }

    [Fact]
    public void AQaStartWithNoEndIsOpenToNowAndInNoBatch()
    {
        var report = Fold(string.Empty, Ledger("qa", "22:00", reference: "q1", phase: "start", slug: "y"), Ledger("ship", "22:30", Sha));

        var group = Assert.Single(report.Groups);
        Assert.Equal("y", group.Key);
        Assert.True(group.Open);
        Assert.Equal("qa", group.CurrentStage);
        Assert.Equal(S(3600), group.StageMs["qa"]);
        Assert.Equal(0, report.Batches.Single().StageMs["qa"]);
    }

    // b29 §2 signals, over hand-built groups and batches: one case per signal, its nearest quiet input, and the S-review boundary

    private static StageGroup G(string key, int rework = 0, double shipWaitS = 0, params StageInterval[] intervals) =>
        new(key, [key], intervals, new Dictionary<string, double> { ["ship_wait"] = S(shipWaitS) }, rework, 0, true, "build", 0);

    private static StageBatch B(string? critical = "a", string criticalStage = "build", string[]? groups = null, params (string Stage, double S)[] stages) =>
        new(Sha, T("12:00"), ["a"], groups ?? ["a"], stages.ToDictionary(static s => s.Stage, static s => S(s.S)), 0, S(1000), critical is null ? null : new StageCritical(critical, criticalStage, 0));

    private static QueueEntry Q(string item, string role, bool idleEligible) =>
        new(new ChecklistEntry("s", new PlanItem(item, item, "next", T("09:00"), null, null, [], null, null, null)), role, Ms("22:50"), idleEligible ? ["impl-2"] : [], null, 1);

    private static List<string> Ids(StageBatch? batch, IReadOnlyList<StageGroup> groups, IReadOnlyList<QueueEntry>? queue = null, params JsonValue[] ledger) =>
        [.. HierarchyModule.StageSignals(new StageTimingReport(groups, batch is null ? [] : [batch]), batch, groups, queue ?? [], ledger, Ms("23:00")).Select(static s => s.Id)];

    private static JsonValue Gate(string at, string sha) => JsonText.Parse($"{{\"kind\":\"gate\",\"at\":\"{T(at)}\",\"sha\":\"{sha}\",\"result\":\"fail\"}}");

    [Fact]
    public void AQuietBatchAndNoShipsGiveNoSignals()
    {
        Assert.Empty(Ids(null, [G("a")]));
        Assert.Empty(Ids(B(stages: ("build", 900)), [G("a")]));
    }

    [Theory]
    [InlineData(600, true)]
    [InlineData(599, false)]
    public void SReviewFiresWhenTwoGroupsAreInReviewTogetherForTenMinutes(int overlapS, bool fires)
    {
        var a = G("a", intervals: new StageInterval("a", "review", Ms("10:00"), Ms("11:00"), false));
        var b = G("b", intervals: new StageInterval("b", "review", Ms("11:00") - S(overlapS), Ms("12:00"), false));

        Assert.Equal(fires, Ids(null, [a, b]).Contains("S-review"));
        Assert.Empty(Ids(null, [a]));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void SReviewCountsAQueuedReviewerItemUnderItsGroup(bool secondGroup, bool fires)
    {
        StageGroup InReview(string key) => G(key, intervals: new StageInterval(key, "review", Ms("22:00"), Ms("23:00"), true)) with { Items = [key, key + "2"] };
        var groups = secondGroup ? new[] { InReview("a"), InReview("b") } : [InReview("a")];

        Assert.Equal(fires, Ids(null, groups, [.. groups.Select(static g => Q(g.Key + "2", "reviewer", false))]).Contains("S-review"));
    }

    [Theory]
    [InlineData("review", 310, "build", "S-review")]
    [InlineData("qa", 310, "build", "S-qa")]
    [InlineData("qa", 0, "qa", "S-qa")]
    [InlineData("orch", 210, "build", "S-orch")]
    [InlineData("orch", 200, "build", null)]
    [InlineData("review", 300, "build", null)]
    public void AStageShareOfTheBatchWallFiresItsSignal(string stage, int seconds, string criticalStage, string? id)
    {
        var ids = Ids(B(criticalStage: criticalStage, stages: (stage, seconds)), [G("a")]);

        Assert.Equal(id is null ? [] : [id], ids);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SBuildFiresWhenTwoItemsWaitAndNoImplementorIsIdle(bool idleEligible, bool fires) =>
        Assert.Equal(fires, Ids(null, [], [Q("x", "implementor", idleEligible), Q("y", "implementor", idleEligible)]).Contains("S-build"));

    [Theory]
    [InlineData(2, true)]
    [InlineData(1, false)]
    public void SReworkFiresAtTheSecondRound(int rounds, bool fires) => Assert.Equal(fires, Ids(null, [G("a", rounds)]).Contains("S-rework"));

    [Theory]
    [InlineData(1800, true)]
    [InlineData(1799, false)]
    public void SShipwaitFiresWhenMostGroupsWaitOnTheOneThatGates(int waitS, bool fires)
    {
        var ids = Ids(B("c", groups: ["a", "b", "c"]), [G("a", shipWaitS: waitS), G("b", shipWaitS: waitS), G("c")]);

        Assert.Equal(fires, ids.Contains("S-shipwait"));
    }

    [Fact]
    public void SGateFiresOnASecondFailureOfOneShaOrTwoShasInARowButNotOnOne()
    {
        Assert.Contains("S-gate", Ids(null, [], null, Gate("10:00", Sha), Gate("10:30", Sha)));
        Assert.Contains("S-gate", Ids(null, [], null, Gate("10:00", Sha), Gate("10:30", Sha2)));
        Assert.Empty(Ids(null, [], null, Gate("10:00", Sha)));
        Assert.Empty(Ids(null, [], null, Gate("10:00", Sha), Ledger("ship", "10:15", Sha2), Gate("10:30", Sha2)));
    }

    [Fact]
    public void RecentListsTheThreeBatchesBeforeTheCurrentNewestFirstWithTheirSignals()
    {
        static StageBatch Batch(string sha, string at, double reviewS) =>
            new(sha, T(at), ["a"], ["a"], new Dictionary<string, double> { ["review"] = S(reviewS) }, 0, S(1000), new StageCritical("a", "build", 0));
        var batches = new[] { Batch("1111111111", "10:00", 310), Batch("2222222222", "11:00", 0), Batch("3333333333", "12:00", 0), Batch("4444444444", "13:00", 0) };
        var report = new StageTimingReport([G("a")], batches);

        var recent = HierarchyModule.RecentBatches(report, batches[^1], [], Ms("23:00"));

        Assert.Equal(["3333333333", "2222222222", "1111111111"], recent.Select(static r => r.Sha));
        Assert.Equal(["", "", "S-review"], recent.Select(static r => string.Join(',', r.Signals)));
        Assert.Equal(0.31, recent[^1].Shares["review"], 3);
        Assert.Empty(HierarchyModule.RecentBatches(report, batches[0], [], Ms("23:00")));
        Assert.Empty(HierarchyModule.RecentBatches(report, null, [], Ms("23:00")));
    }
}
