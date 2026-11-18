using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>The queue-stall and member-waiting alarms: what fires, what must not, and once per episode.</summary>
public sealed class QueueEventsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static string Iso(double minutesAfterT0) => Timestamps.ToIsoString(T0.AddMinutes(minutesAfterT0));

    private static double Ms(double minutesAfterT0) => T0.AddMinutes(minutesAfterT0).ToUnixTimeMilliseconds();

    private static ChecklistEntry Item(string id, string state = "next", string role = "implementor", double queuedAt = 0, double? updatedAt = null, string? holder = null, string? todo = null, string[]? refs = null) =>
        new("_run", new PlanItem(id, id, state, Iso(queuedAt), null, null, [], null, null, null, Role: role, Holder: holder, Todo: todo, Refs: refs, UpdatedAt: Iso(updatedAt ?? queuedAt), StateSince: Iso(queuedAt)));

    private static MemberView Member(string name, string role = "implementor", string activity = "idle", double idleAt = 0, bool? live = null, bool dismissed = false) =>
        new(name, new JsonObject { ["role"] = role, ["dismissed"] = dismissed }, live, activity, null, Iso(idleAt), "sess-" + name);

    private static OwnedTeam Team(params MemberView[] members) => new("tm", new JsonObject(), members);

    private static DispatchCandidate Dispatch(string toName, bool open, double createdAt) =>
        new("20261021-120000-aaaa", "implementor", "work", toName + "@tm", toName, "medium", Iso(createdAt), open, string.Empty, false, string.Empty, "/h") { Team = "tm" };

    private static IReadOnlyList<WatchEvent> Run(double nowMinutes, IReadOnlyList<ChecklistEntry> entries, OwnedTeam team, IReadOnlyList<DispatchCandidate>? candidates = null, HashSet<string>? told = null, Func<MemberView, bool>? onJob = null, bool delivered = true)
    {
        var events = QueueEvents.Evaluate("s1", Ms(nowMinutes), [team], candidates ?? [], _ => entries, onJob ?? (_ => false), told ?? [], out var keys);
        if (told is not null && delivered)
        {
            foreach (var key in keys.Values)
            {
                _ = told.Add(key);
            }
        }

        return events;
    }

    [Fact]
    public void ThreeQueuedItemsWithIdleMembersOfTheirRolesRaiseThreeQueueStallEventsNamingEachItemAndMember()
    {
        var entries = new[] { Item("stab-a6"), Item("feat-p1"), Item("ui-p1", role: "ux-engineer") };
        var team = Team(Member("implementor-38"), Member("implementor-39"), Member("ux-engineer-2", "ux-engineer"));

        var events = Run(11, entries, team);

        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("queue-stall", e.Name));
        Assert.Contains(events, e => e.Data.Str("item") == "_run/ui-p1" && e.Data.Str("member") == "ux-engineer-2");
        Assert.Contains(events, e => e.Text.StartsWith("ahd alarm: queue-stall — dispatch _run/stab-a6 to implementor-38 (queued 11m, implementor-38 idle 11m)\n", StringComparison.Ordinal));
        Assert.All(events, e => Assert.True(e.Data.Get("request_id") is null or { IsText: false }));
    }

    [Theory]
    [InlineData("_run", "other", "open", 1)]
    [InlineData("s1", "s1", "open", 1)]
    [InlineData("s1", "other", "open", 0)]
    [InlineData("_run", "other", "done", 0)]
    public void QueueStallProposesOnlyAMemberOfTheItemsStreamAndNeverOneOnAClosedStream(string itemStream, string memberStream, string state, int stalls)
    {
        var member = new MemberView("impl", new JsonObject { ["role"] = "implementor", ["stream"] = memberStream }, null, "idle", null, Iso(0), "sess-impl");
        var doc = new JsonObject { ["streams"] = new JsonObject { [memberStream] = new JsonObject { ["state"] = state } } };

        var events = Run(11, [Item("x") with { Stream = itemStream }], new OwnedTeam("tm", doc, [member]));

        Assert.Equal(stalls, events.Count(static e => e.Name == "queue-stall"));
    }

    public static TheoryData<string> NoStallCases() => new() { "working", "open-dispatch", "blocked-item", "waiting-user-item", "role", "recent-dispatch", "job", "dead", "queued-short", "idle-short", "dismissed" };

    [Theory]
    [MemberData(nameof(NoStallCases))]
    public void NegativeTheNearestInputThatMustNotStallRaisesNothing(string kind)
    {
        var entries = new[] { Item("a", state: kind == "blocked-item" ? "blocked" : kind == "waiting-user-item" ? "waiting_user" : "next", role: kind == "role" ? "reviewer" : "implementor", queuedAt: kind == "queued-short" ? 2 : 0) };
        var member = kind switch
        {
            "working" => Member("impl-1", activity: "working"),
            "dead" => Member("impl-1", live: false),
            "dismissed" => Member("impl-1", dismissed: true),
            "idle-short" => Member("impl-1", idleAt: 4),
            _ => Member("impl-1"),
        };
        var candidates = kind switch
        {
            "open-dispatch" => new[] { Dispatch("impl-1", true, -30) },
            "recent-dispatch" => new[] { Dispatch("other", false, 5) },
            _ => [],
        };
        var team = kind == "recent-dispatch" ? Team(member, Member("other", activity: "working")) : Team(member);

        var events = Run(10.5, entries, team, candidates, onJob: _ => kind == "job");

        Assert.DoesNotContain(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void AQueuedItemWhoseLatestRefIsADispatchStillRunningIsNotStalled()
    {
        var entries = new[] { Item("a", refs: ["20261021-120000-aaaa"]) };

        var events = Run(11, entries, Team(Member("impl-1")), [Dispatch("other", true, -30)]);

        Assert.DoesNotContain(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void TheSharedFilterDropsOnlyQueuedItemsWhoseLatestRefIsRunning()
    {
        var entries = new[]
        {
            Item("running", refs: ["r0", "r1"]),
            Item("old-ref-running", refs: ["r1", "r2"]),
            Item("finished", refs: ["r3"]),
            Item("no-refs"),
            Item("not-queued", state: "in_progress", refs: ["r1"]),
        };

        var kept = HierarchyModule.ChecklistWithoutRunningDispatch(entries, ["r1"]);

        Assert.Equal(["old-ref-running", "finished", "no-refs", "not-queued"], kept.Select(e => e.Item.Item));
    }

    [Fact]
    public void NegativeAQueuedItemWhoseLatestRefIsAFinishedDispatchStillStalls()
    {
        var entries = new[] { Item("a", refs: ["20261021-120000-aaaa"]) };

        var events = Run(11, entries, Team(Member("impl-1")), [Dispatch("other", false, -30)]);

        Assert.Contains(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void NegativeAQueuedItemWhoseLatestRefIsNotADispatchTheTeamKnowsStillStalls()
    {
        var entries = new[] { Item("a", refs: ["20261021-999999-zzzz"]) };

        var events = Run(11, entries, Team(Member("impl-1")), [Dispatch("other", false, -30)]);

        Assert.Contains(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void AnAnsweredItemWithNoRefsDoesNotStall()
    {
        var answered = Item("a", todo: "answer user: a");
        var events = Run(11, [answered with { Item = answered.Item with { Answer = "Don't enroll" } }], Team(Member("impl-1")));

        Assert.DoesNotContain(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void NegativeAnAnsweredItemWithARefStillStalls()
    {
        var answered = Item("a", todo: "answer user: a", refs: ["20261021-110000-bbbb"]);
        var events = Run(11, [answered with { Item = answered.Item with { Answer = "Ship it" } }], Team(Member("impl-1")));

        Assert.Contains(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void NegativeAnAnsweredItemWhoseAnswerTodoWasClearedStillStalls()
    {
        var answered = Item("a");
        var events = Run(11, [answered with { Item = answered.Item with { Answer = "Ship it" } }], Team(Member("impl-1")));

        Assert.Contains(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void NegativeAQueuedItemWithNoRefsStillStallsWhileADispatchExists()
    {
        var events = Run(11, [Item("a")], Team(Member("impl-1")), [Dispatch("other", false, -30)]);

        Assert.Contains(events, e => e.Name == "queue-stall");
    }

    [Fact]
    public void ANoteOnAQueuedItemDoesNotRestartItsQueuedClock()
    {
        var entries = new[] { Item("a", queuedAt: 0, updatedAt: 9) };

        var events = Run(11, entries, Team(Member("impl-1")));

        Assert.Single(events);
    }

    [Fact]
    public void AStallIsToldOncePerEpisodeAndAgainAfterTheItemChanges()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var team = Team(Member("impl-1"));

        var first = Run(11, [Item("a")], team, told: told);
        var again = Run(12, [Item("a")], team, told: told);
        var retitled = Run(12.5, [Item("a", queuedAt: 0, updatedAt: 12)], team, told: told);
        var requeued = Run(13, [Item("a", queuedAt: 2)], Team(Member("impl-1"), Member("impl-2")), told: told);
        var gone = Run(14, [], team, told: told);

        Assert.Single(first);
        Assert.Empty(again);
        Assert.Empty(retitled);
        Assert.Single(requeued);
        Assert.Empty(gone);
        Assert.Empty(told);
    }

    [Fact]
    public void AStallWhoseMailFailedIsToldAgainOnTheNextPass()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var team = Team(Member("impl-1"));

        var failed = Run(11, [Item("a")], team, told: told, delivered: false);
        var retry = Run(12, [Item("a")], team, told: told);
        var after = Run(13, [Item("a")], team, told: told);

        Assert.Single(failed);
        Assert.Single(retry);
        Assert.Empty(after);
    }

    [Fact]
    public void ANoteOnAWaitingItemDoesNotRestartItsWaitedClock()
    {
        var entry = Item("a", state: "waiting_orch", queuedAt: 0, updatedAt: 9, holder: "impl-1", todo: "closed: decide done or re-dispatch");

        var due = Run(10, [entry], Team(Member("impl-1", activity: "working")));

        Assert.Single(due);
    }

    [Fact]
    public void AMemberReportAlreadyMailedRaisesNoMemberWaiting()
    {
        var entry = Item("a", state: "waiting_orch", holder: "impl-1", todo: "answer impl-1: DONE", refs: ["20261021-115500-ffff"]);

        Assert.Empty(Run(30, [entry], Team(Member("impl-1"))));
    }

    [Fact]
    public void AnAnswerTheUserGaveRaisesMemberWaitingAtTheThresholdAndNotBefore()
    {
        var entry = Item("a", state: "waiting_orch", holder: "impl-1", todo: "answer user: a", refs: ["20261021-115500-ffff"]);
        var team = Team(Member("impl-1", activity: "working"));

        var early = Run(9.5, [entry], team);
        var due = Run(10, [entry], team);

        Assert.Empty(early);
        var ev = Assert.Single(due);
        Assert.Equal("member-waiting", ev.Name);
        Assert.Equal("20261021-115500-ffff", ev.Data.Str("request_id"));
        Assert.Equal("_run/a", ev.Data.Str("item"));
        Assert.True(ev.Data.Get("due_slot") is { IsNumber: true });
        Assert.StartsWith("ahd alarm: member-waiting — impl-1 has waited 10m on you: answer user: a (_run/a, 20261021-115500-ffff)", ev.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AHandSetWaitingItemNeedsItsOwnerLiveAndIdleWhileAReportedOneDoesNot()
    {
        var manual = Item("a", state: "waiting_orch", holder: "impl-1", todo: "decide the schema");

        var idle = Run(11, [manual], Team(Member("impl-1")));
        var working = Run(11, [manual], Team(Member("impl-1", activity: "working")));
        var dead = Run(11, [manual], Team(Member("impl-1", live: false)));
        var unknownOwner = Run(11, [manual], Team(Member("impl-2")));

        Assert.Single(idle);
        Assert.Empty(working);
        Assert.Empty(dead);
        Assert.Empty(unknownOwner);
    }

    [Fact]
    public void APausedOwnerRaisesNoMemberWaitingAndAnUnpausedOneDoes()
    {
        var manual = Item("a", state: "waiting_orch", holder: "impl-1", todo: "decide the schema");

        Assert.Empty(Run(11, [manual], Team(Member("impl-1") with { Paused = true })));
        Assert.Single(Run(11, [manual], Team(Member("impl-1") with { Paused = false })));
    }

    [Fact]
    public void APausedMemberIsNotIdleCapacityAndTheUnpausedPairStillIs()
    {
        IReadOnlyList<WatchEvent> Idle(OwnedTeam team) =>
            [.. QueueEvents.Evaluate("s1", Ms(11), [team], [], _ => [], _ => false, new HashSet<string>(), out _,
                readyBacklog: _ => [new ReadyBacklogItem("b260", "Prevent idle members while ready work waits", "now", 1)],
                idleCapacityAt: new Dictionary<string, (double At, string Signature)>(StringComparer.Ordinal))
                .Where(static e => e.Name == "idle-capacity")];

        // ah2-180 A2: the unpaused one alone is idle capacity; with both paused there is none
        Assert.DoesNotContain("reviewer-94", Assert.Single(Idle(Team(Member("implementor-96"), Member("reviewer-94", "reviewer") with { Paused = true }))).Text, StringComparison.Ordinal);
        Assert.Empty(Idle(Team(Member("implementor-96") with { Paused = true }, Member("reviewer-94", "reviewer") with { Paused = true })));
        var told = Assert.Single(Idle(Team(Member("implementor-96"), Member("reviewer-94", "reviewer"), Member("qa-3", "qa") with { Paused = true })));
        Assert.DoesNotContain("qa-3", told.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberWaitingAlarmIsToldOncePerUpdateOfTheItem()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var team = Team(Member("impl-1"));
        var entry = Item("a", state: "waiting_orch", holder: "impl-1", todo: "closed: decide done or re-dispatch");

        Assert.Single(Run(11, [entry], team, told: told));
        Assert.Empty(Run(12, [entry], team, told: told));
        Assert.Single(Run(30, [Item("a", state: "waiting_orch", queuedAt: 15, holder: "impl-1", todo: "closed: decide again")], team, told: told));
    }

    [Fact]
    public void AWaitingItemWhoseHolderGotANewerBriefRaisesNoMemberWaiting()
    {
        var entry = Item("a", state: "waiting_orch", queuedAt: 5, holder: "impl-1", todo: "closed: decide done or re-dispatch");
        var team = Team(Member("impl-1"));

        Assert.Empty(Run(30, [entry], team, [Dispatch("impl-1", open: false, createdAt: 6) with { Item = "a" }]));
        Assert.Single(Run(30, [entry], team, [Dispatch("impl-1", open: false, createdAt: 4) with { Item = "a" }]));
        Assert.Single(Run(30, [entry], team, [Dispatch("impl-1", open: false, createdAt: 6) with { Item = "b" }]));
    }

    [Fact]
    public void IdleCapacityFiresForIdleMembersWithReadyBacklogOnceUntilTheIdleSetChangesAndForOneMemberAlone()
    {
        var stamped = new Dictionary<string, (double At, string Signature)>(StringComparer.Ordinal);
        IReadOnlyList<WatchEvent> Idle(double minutes, OwnedTeam team)
        {
            var events = QueueEvents.Evaluate("s1", Ms(minutes), [team], [], _ => [], _ => false, new HashSet<string>(), out _,
                readyBacklog: _ => [new ReadyBacklogItem("b260", "Prevent idle members while ready work waits", "now", 1)], idleCapacityAt: stamped)
                .Where(static e => e.Name == "idle-capacity").ToList();
            foreach (var ev in events)
            {
                stamped[QueueEvents.UnstaffedTeamKey("s1", ev.Team)] = (Ms(minutes), QueueEvents.IdleCapacitySignature(ev));
            }

            return events;
        }

        var pair = Team(Member("implementor-96"), Member("reviewer-94", "reviewer"));

        var first = Assert.Single(Idle(11, pair));
        Assert.Contains("implementor-96, reviewer-94", first.Text, StringComparison.Ordinal);
        Assert.Contains("b260 Prevent idle members while ready work waits", first.Text, StringComparison.Ordinal);
        Assert.Empty(Idle(16, pair));
        Assert.Empty(Idle(42, pair));
        Assert.Single(Idle(42, Team(Member("implementor-96"), Member("reviewer-94", "reviewer"), Member("qa-3", "qa"))));

        // ah2-180 A2: one idle member is enough
        stamped.Clear();
        Assert.Contains("1 member idle 10+ min with no open request: implementor-96.", Assert.Single(Idle(11, Team(Member("implementor-96")))).Text, StringComparison.Ordinal);
        stamped.Clear();
        Assert.Single(Idle(11, Team(Member("implementor-96"), Member("reviewer-94", "reviewer", dismissed: true))));
    }

    [Fact]
    public void NegativeNoIdleMemberRaisesNoIdleCapacity()
    {
        var events = QueueEvents.Evaluate("s1", Ms(11), [Team(Member("implementor-96", activity: "working"), Member("reviewer-94", "reviewer", idleAt: 2))], [], _ => [], _ => false, new HashSet<string>(), out _,
            readyBacklog: _ => [new ReadyBacklogItem("b260", "Ready work", "now", 1)], idleCapacityAt: new Dictionary<string, (double At, string Signature)>(StringComparer.Ordinal));

        Assert.DoesNotContain(events, static e => e.Name == "idle-capacity");
    }

    [Fact]
    public void AnIdleCapacityDroppedAtDeliveryFiresAgainWhenTheSameMembersAreIdleAgain()
    {
        var stamped = new Dictionary<string, (double At, string Signature)>(StringComparer.Ordinal);
        IReadOnlyList<WatchEvent> Idle(double minutes, OwnedTeam team)
        {
            var events = QueueEvents.Evaluate("s1", Ms(minutes), [team], [], _ => [], _ => false, new HashSet<string>(), out _,
                readyBacklog: _ => [new ReadyBacklogItem("b260", "Ready work", "now", 1)], idleCapacityAt: stamped)
                .Where(static e => e.Name == "idle-capacity").ToList();
            foreach (var ev in events)
            {
                stamped[QueueEvents.UnstaffedTeamKey("s1", ev.Team)] = (Ms(minutes), QueueEvents.IdleCapacitySignature(ev));
            }

            return events;
        }

        // mailed at 11, then dropped at delivery because implementor-96 was sent work
        Assert.Single(Idle(11, Team(Member("implementor-96"), Member("reviewer-94", "reviewer"))));
        // negative: with no break in their idleness the same pair is not told again
        Assert.Empty(Idle(15, Team(Member("implementor-96"), Member("reviewer-94", "reviewer"))));
        // ah2-180 A2: the episode ended and the one member still idle is enough on its own
        Assert.Contains("reviewer-94", Assert.Single(Idle(16, Team(Member("implementor-96", activity: "working"), Member("reviewer-94", "reviewer")))).Text, StringComparison.Ordinal);

        Assert.Single(Idle(47, Team(Member("implementor-96", idleAt: 16), Member("reviewer-94", "reviewer"))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DeputyOnDutyAnItemStillWaitingASecondWindowIsCopiedToTheChiefOnce(bool deputyLive)
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var team = new OwnedTeam("tm", new JsonObject { ["duty"] = new JsonObject { ["deputy"] = "tm-dep" } }, [Member("tm-dep", "deputy-chief", live: deputyLive)]);
        var entry = Item("a", state: "waiting_orch", holder: "impl-1", todo: "answer user: a");

        var first = Run(10, [entry], team, told: told);
        var second = Run(20, [entry], team, told: told);
        var later = Run(30, [entry], team, told: told);

        Assert.Null(Assert.Single(first).To);
        Assert.Empty(later);
        if (deputyLive)
        {
            Assert.Equal("chief@tm", Assert.Single(second).To);
        }
        else
        {
            Assert.Empty(second);
        }
    }

    [Fact]
    public void AReportedRowTenMinutesOldIsToldOnceAsReportWaitingWhateverItsOwnerIsDoing()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var entry = Item("a", state: "reported", holder: "impl-1", refs: ["20261021-120000-aaaa"]);
        var team = Team(Member("impl-1", activity: "working"));

        var first = Run(10, [entry], team, told: told);
        var again = Run(11, [entry], team, told: told);

        var ev = Assert.Single(first);
        Assert.Equal("report-waiting", ev.Name);
        Assert.Contains("impl-1 reported _run/a 10m ago", ev.Text, StringComparison.Ordinal);
        Assert.Contains("20261021-120000-aaaa", ev.Text, StringComparison.Ordinal);
        Assert.Empty(again);
    }

    [Theory]
    [InlineData("reported", 9)]
    [InlineData("in_review", 30)]
    public void NegativeAReportedRowUnderTenMinutesOrARowInReviewRaisesNoReportWaiting(string state, double now)
    {
        var events = Run(now, [Item("a", state: state, holder: "impl-1")], Team(Member("impl-1")));

        Assert.DoesNotContain(events, static e => e.Name == "report-waiting");
    }

    private static IReadOnlyList<WatchEvent> Remind(double minutes, IReadOnlyList<ChecklistEntry> entries, HashSet<string> told, Dictionary<string, double> remindAt)
    {
        var events = QueueEvents.Evaluate("s1", Ms(minutes), [Team(Member("impl-1"))], [], _ => entries, _ => false, told, out var keys, remindAt: remindAt);
        foreach (var key in keys.Values)
        {
            _ = told.Add(key);
        }

        foreach (var ev in events.Where(static e => e.Name == "work-still-waiting"))
        {
            remindAt[QueueEvents.UnstaffedTeamKey("s1", ev.Team)] = Ms(minutes);
        }

        return events;
    }

    [Fact]
    public void AToldReportWaitingStillTrueTwentyMinutesOnIsListedOnceInWorkStillWaitingAndNotAfterTheRowIsDone()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var remindAt = new Dictionary<string, double>(StringComparer.Ordinal);
        var reported = Item("a", state: "reported", holder: "impl-1");

        Assert.Equal("report-waiting", Assert.Single(Remind(10, [reported], told, remindAt)).Name);
        Assert.Empty(Remind(11, [reported], told, remindAt));
        Assert.Empty(Remind(30, [reported], told, remindAt));
        var still = Assert.Single(Remind(31, [reported], told, remindAt));
        Assert.Equal("work-still-waiting", still.Name);
        Assert.Contains("report-waiting _run/a", still.Text, StringComparison.Ordinal);
        Assert.Empty(Remind(40, [reported], told, remindAt));
        Assert.Equal("work-still-waiting", Assert.Single(Remind(51, [reported], told, remindAt)).Name);

        var done = Item("a", state: "done", holder: "impl-1", queuedAt: 52);
        Assert.Empty(Remind(52, [done], told, remindAt));
        Assert.Empty(Remind(80, [done], told, remindAt));
    }

    [Fact]
    public void WorkStillWaitingNamesARowWhoseIdHoldsABarWhole()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var remindAt = new Dictionary<string, double>(StringComparer.Ordinal);
        var reported = Item("a|b", state: "reported", holder: "impl-1");

        _ = Remind(10, [reported], told, remindAt);
        _ = Remind(11, [reported], told, remindAt);
        var still = Assert.Single(Remind(31, [reported], told, remindAt));

        Assert.Equal("ahd alarm: work-still-waiting — still waiting on you: report-waiting _run/a|b", still.Text);
    }

    [Fact]
    public void NegativeWorkStillWaitingNeverListsAConditionNotYetTold()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var remindAt = new Dictionary<string, double>(StringComparer.Ordinal);
        var reported = Item("a", state: "reported", holder: "impl-1");

        // the first tell's mail failed, so the condition was never told: nothing is reminded however long it holds
        _ = QueueEvents.Evaluate("s1", Ms(10), [Team(Member("impl-1"))], [], _ => [reported], _ => false, told, out _, remindAt: remindAt);
        var later = QueueEvents.Evaluate("s1", Ms(40), [Team(Member("impl-1"))], [], _ => [reported], _ => false, told, out _, remindAt: remindAt);

        Assert.DoesNotContain(later, static e => e.Name == "work-still-waiting");
        Assert.Empty(remindAt);
    }
}
