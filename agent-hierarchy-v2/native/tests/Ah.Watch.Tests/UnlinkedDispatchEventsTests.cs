using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>The unlinked-dispatch alarm: an Orchestrator request no checklist item tracks is told once, in one mail per cooldown.</summary>
public sealed class UnlinkedDispatchEventsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    private static string Iso(double minutesAfterT0) => Timestamps.ToIsoString(T0.AddMinutes(minutesAfterT0));

    private static double Ms(double minutesAfterT0) => T0.AddMinutes(minutesAfterT0).ToUnixTimeMilliseconds();

    private static ChecklistEntry Item(string id, string state = "dispatched", params string[] refs) =>
        new("_run", new PlanItem(id, id, state, Iso(0), null, null, [], null, null, null, Refs: refs, UpdatedAt: Iso(0), StateSince: Iso(0)));

    private static OwnedTeam Team(string name = "tm") => new(name, new JsonObject(), []);

    private static DispatchCandidate Request(string id, double createdAt, string? item = null, string slug = "work", string team = "tm", bool note = false, bool open = true) =>
        new(id, "implementor", slug, "implementor-1@" + team, "implementor-1", "medium", Iso(createdAt), open, string.Empty, false, string.Empty, "/h") { Team = team, Item = item, Note = note };

    /// <summary>An item no request names: the checklist is not empty, so progress is on.</summary>
    private static readonly ChecklistEntry Unrelated = Item("unrelated", "next");

    /// <summary>One evaluation; a delivered mail commits its keys and its (session, team) mail time, the way the tick does.</summary>
    private sealed class Harness
    {
        public HashSet<string> Told { get; } = [];

        public Dictionary<string, double> MailedAt { get; } = [];

        public IReadOnlyList<WatchEvent> Run(double nowMinutes, IReadOnlyList<DispatchCandidate> candidates, IReadOnlyList<ChecklistEntry>? entries = null, string session = "s1", OwnedTeam[]? owned = null, bool delivered = true)
        {
            var events = UnlinkedDispatchEvents.Evaluate(session, Ms(nowMinutes), owned ?? [Team()], candidates, _ => entries ?? [Unrelated], Told, MailedAt, out var keys);
            if (delivered)
            {
                foreach (var ev in events)
                {
                    Told.UnionWith(keys[ev]);
                    MailedAt[UnlinkedDispatchEvents.CooldownKey(session, ev.Team)] = Ms(nowMinutes);
                }
            }

            return events;
        }
    }

    [Fact]
    public void AnUnlinkedOrchestratorRequestPastGraceIsToldOnceNamingItsIdSlugAndTheFix()
    {
        var h = new Harness();
        var r = Request("20261031-120000-aaaa", 0, slug: "make-ui");

        var events = h.Run(3, [r]);

        var ev = Assert.Single(events);
        Assert.Equal("unlinked-dispatch", ev.Name);
        Assert.Equal("tm", ev.Team);
        Assert.Contains("1 request(s) you dispatched are not on the checklist: 20261031-120000-aaaa (make-ui).", ev.Text, StringComparison.Ordinal);
        Assert.Contains("ah roster checklist set <item> --ref <id>", ev.Text, StringComparison.Ordinal);
        Assert.Contains("--item", ev.Text, StringComparison.Ordinal);
        Assert.Equal(1, ev.Data.Get("count")!.Value.AsNumber());
        Assert.Empty(h.Run(30, [r]));
    }

    [Fact]
    public void NegativeARequestWithAnItemRaisesNothing() =>
        Assert.Empty(new Harness().Run(3, [Request("r1", 0, item: "_run/x")]));

    [Fact]
    public void NegativeARequestLinkedByRefWithinGraceRaisesNothing() =>
        Assert.Empty(new Harness().Run(3, [Request("r1", 0)], [Item("x", "dispatched", "r1")]));

    [Fact]
    public void NegativeARequestWhoseItemIsDoneRaisesNothing() =>
        Assert.Empty(new Harness().Run(3, [Request("r1", 0)], [Item("x", "done", "r1")]));

    [Fact]
    public void NegativeARefInAnotherStreamStillCounts() =>
        Assert.Empty(new Harness().Run(3, [Request("r1", 0)], [new ChecklistEntry("feat", Item("x", "next", "r1").Item)]));

    [Fact]
    public void ARequestLinkedAfterItWasToldIsNotToldAgain()
    {
        var h = new Harness();
        Assert.Single(h.Run(3, [Request("r1", 0)]));

        Assert.Empty(h.Run(30, [Request("r1", 0)], [Item("x", "dispatched", "r1")]));
        Assert.Empty(h.Run(60, [Request("r1", 0)]));
    }

    [Fact]
    public void NegativeTheRunsPipelineAnchorRaisesNothing() =>
        Assert.Empty(new Harness().Run(3, [Request("r1", 0, slug: HierarchyModule.PipelineAnchorSlug)]));

    [Fact]
    public void NegativeAnAutoTrackedNoteRaisesNothing() =>
        Assert.Empty(new Harness().Run(3, [Request("r1", 0, note: true)]));

    [Fact]
    public void NegativeARequestOlderThanTheWindowRaisesNothing() =>
        Assert.Empty(new Harness().Run(24 * 60 + 1, [Request("r1", 0)]));

    [Fact]
    public void ARequestAtTheWindowEdgeIsStillTold() =>
        Assert.Single(new Harness().Run(24 * 60, [Request("r1", 0)]));

    [Fact]
    public void NegativeARequestInsideGraceIsNotYetToldThenIsAfterIt()
    {
        var h = new Harness();
        Assert.Empty(h.Run(1.9, [Request("r1", 0)]));
        Assert.Single(h.Run(2, [Request("r1", 0)]));
    }

    [Fact]
    public void AClosedRequestIsEligibleToo() =>
        Assert.Single(new Harness().Run(3, [Request("r1", 0, open: false)]));

    [Fact]
    public void ThirtyUnlinkedRequestsInAnHourAreAtMostFourMailsEachIdToldOnce()
    {
        var h = new Harness();
        var all = new List<DispatchCandidate>();
        var mailed = new List<string>();
        var mails = 0;
        for (var minute = 0; minute < 60; minute++)
        {
            if (minute % 2 == 0)
            {
                all.Add(Request($"r{minute / 2:00}", minute));
            }

            foreach (var ev in h.Run(minute, all))
            {
                mails++;
                mailed.AddRange(ev.Data.Get("request_ids")!.Value.AsArray().Select(static v => v.AsString()));
                Assert.True(ev.Text.Split(" (work)").Length - 1 <= HierarchyModule.UnlinkedDispatchListCap);
            }
        }

        Assert.InRange(mails, 1, 4);
        Assert.Equal(mailed.Count, mailed.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void OneMailListsAtMostFiveIdsThenAndNMore()
    {
        var requests = Enumerable.Range(0, 8).Select(i => Request($"r{i}", i / 10.0)).ToList();

        var ev = Assert.Single(new Harness().Run(3, requests));

        Assert.Contains("8 request(s)", ev.Text, StringComparison.Ordinal);
        Assert.Contains("r0 (work), r1 (work), r2 (work), r3 (work), r4 (work) and 3 more.", ev.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("r5 (work)", ev.Text, StringComparison.Ordinal);
        Assert.Equal(8, ev.Data.Get("request_ids")!.Value.AsArray().Count);
    }

    [Fact]
    public void ARequestEligibleDuringTheCooldownIsCarriedIntoTheNextMail()
    {
        var h = new Harness();
        Assert.Single(h.Run(3, [Request("r1", 0)]));
        var both = new[] { Request("r1", 0), Request("r2", 5) };

        Assert.Empty(h.Run(10, both));
        Assert.Empty(h.Run(17.9, both));
        var next = Assert.Single(h.Run(18, both));

        Assert.Contains("1 request(s)", next.Text, StringComparison.Ordinal);
        Assert.Contains("r2 (work)", next.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("r1 (work)", next.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMailThatFailedIsNotToldAndStartsNoCooldown()
    {
        var h = new Harness();
        Assert.Single(h.Run(3, [Request("r1", 0)], delivered: false));

        Assert.Single(h.Run(4, [Request("r1", 0)]));
    }

    [Fact]
    public void ATellIsPrunedWhenItsRequestLeavesTheCandidates()
    {
        var h = new Harness();
        Assert.Single(h.Run(3, [Request("r1", 0)]));
        Assert.Contains("s1|tm|unlinked-dispatch|r1", h.Told);

        Assert.Empty(h.Run(4, []));

        Assert.DoesNotContain("s1|tm|unlinked-dispatch|r1", h.Told);
    }

    [Fact]
    public void NegativeTwoSessionsOnDifferentTeamsEachSeeOnlyTheirOwnTeam()
    {
        var h = new Harness();
        var candidates = new[] { Request("ra", 0, team: "alpha"), Request("rb", 0, team: "beta") };

        var a = Assert.Single(h.Run(3, candidates, session: "sa", owned: [Team("alpha")]));
        var b = Assert.Single(h.Run(3, candidates, session: "sb", owned: [Team("beta")]));

        Assert.Contains("ra (work)", a.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("rb (work)", a.Text, StringComparison.Ordinal);
        Assert.Contains("rb (work)", b.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("ra (work)", b.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeATeamWhoseChecklistReadsEmptyRaisesNothing() =>
        Assert.Empty(new Harness().Run(3, [Request("r1", 0)], []));

    [Fact]
    public void TheCooldownIsPerTeamSoOneTeamsMailDoesNotHoldBackAnothers()
    {
        var h = new Harness();
        OwnedTeam[] both = [Team("alpha"), Team("beta")];
        Assert.Single(h.Run(3, [Request("ra", 0, team: "alpha")], owned: both));

        var beta = Assert.Single(h.Run(5, [Request("ra", 0, team: "alpha"), Request("rb", 1, team: "beta")], owned: both));
        Assert.Equal("beta", beta.Team);

        Assert.Empty(h.Run(10, [Request("ra", 0, team: "alpha"), Request("ra2", 6, team: "alpha"), Request("rb", 1, team: "beta")], owned: both));
        var alpha = Assert.Single(h.Run(18, [Request("ra", 0, team: "alpha"), Request("ra2", 6, team: "alpha"), Request("rb", 1, team: "beta")], owned: both));
        Assert.Equal("alpha", alpha.Team);
    }

    [Fact]
    public void ARestartTellsAStillEligibleRequestAtMostOnceMore()
    {
        var before = new Harness();
        Assert.Single(before.Run(3, [Request("r1", 0)]));

        var after = new Harness();
        Assert.Single(after.Run(30, [Request("r1", 0)]));
        Assert.Empty(after.Run(60, [Request("r1", 0)]));
    }
}
