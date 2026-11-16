using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The web verdict over the inputs the alarm has: fresh heartbeat, running pane agent and starting window.</summary>
public sealed partial class TreeLanesTests
{
    /// <summary>What the processes lane is told of the directory's members, for the model built next.</summary>
    private static Func<string, MemberFacts>? FactsOfDir;

    private static MemberFacts Given(string[]? heartbeats = null, Dictionary<string, string>? panes = null, string[]? starting = null) =>
        new(heartbeats ?? [], panes ?? [], starting ?? []);

    private static string LivenessOf(ReadModel model, string member) =>
        Text(Summary(model).Member("member_states")!.Value.Member(MemberId(model, member))!.Value, "liveness");

    private static TreeReader PidLess()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "cust");
        reader.Registrations["s-cust"] = ("{\"session_id\":\"s-cust\",\"address\":\"cust\",\"addresses\":[\"cust\"],\"harness\":\"custom\",\"at\":\"2026-06-01T10:06:00Z\"}", [TeamPath]);
        reader.LivePids.AddRange([1, 42]);
        return reader;
    }

    private static TreeReader StartingOverADownRow()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa");
        reader.LivePids.AddRange([1, 42]);
        reader.PeerRows.Add(PeerRow("qa", "down", Iso(FixtureNowMs - 60_000), 78, "ps-78"));
        return reader;
    }

    private static T WithFacts<T>(MemberFacts facts, Func<T> run)
    {
        FactsOfDir = _ => facts;
        try
        {
            return run();
        }
        finally
        {
            FactsOfDir = null;
            LaneBehindByMs = 0;
        }
    }

    private static string Judged(TreeReader reader, string member)
    {
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;
        return LivenessOf(model, member);
    }

    [Fact]
    public void D1APidLessRegistrationWithAFreshHeartbeatIsLive() =>
        Assert.Equal("live", WithFacts(Given(["s-cust"]), () => Judged(PidLess(), "cust")));

    [Fact]
    public void NegativeD1APidLessRegistrationWithNoHeartbeatIsUnknown() =>
        Assert.Equal("unknown", WithFacts(Given(), () => Judged(PidLess(), "cust")));

    [Fact]
    public void NegativeD1AnotherSessionsHeartbeatDoesNotCount() =>
        Assert.Equal("unknown", WithFacts(Given(["s-other"]), () => Judged(PidLess(), "cust")));

    [Fact]
    public void D4ARunningPaneAgentIsLiveOverADeadPid() =>
        Assert.Equal("live", WithFacts(Given(panes: new() { ["gn"] = "pane shows an agent" }), () => Judged(FiveStates(), "gn")));

    [Fact]
    public void NegativeD4AnotherMembersPaneDoesNotRaiseADeadPid() =>
        Assert.Equal("proven_dead", WithFacts(Given(panes: new() { ["dev"] = "pane shows an agent" }), () => Judged(FiveStates(), "gn")));

    private static JsonValue? QueueWaitingOf(Dictionary<string, int> byTeam) =>
        WithFacts(new MemberFacts([], new Dictionary<string, string>(), [], byTeam), () =>
        {
            var (model, _) = SeededWithActivity(Truth());
            using var owner = model;
            return Summary(model).Member("queue_waiting");
        });

    [Fact]
    public void TheSummaryCarriesItsTeamsQueueWaitingCount() =>
        Assert.Equal(2, QueueWaitingOf(new() { ["default"] = 2 })!.Value.AsNumber());

    [Fact]
    public void NegativeAnotherTeamsQueueWaitingCountIsNotThisTeams() =>
        Assert.Null(QueueWaitingOf(new() { ["other"] = 2 }));

    [Fact]
    public void D4AMemberInsideTheStartingWindowIsUnknownOverADownRow() =>
        Assert.Equal("unknown", WithFacts(Given(starting: ["qa"]), () => Judged(StartingOverADownRow(), "qa")));

    [Fact]
    public void NegativeD4OutsideTheStartingWindowTheDownRowDecides() =>
        Assert.Equal("proven_dead", WithFacts(Given(), () => Judged(StartingOverADownRow(), "qa")));

    [Fact]
    public void AHeartbeatIsIgnoredOnceTheObservationIsTooOld() =>
        Assert.Equal("unknown", WithFacts(Given(["s-cust"]), () =>
        {
            LaneBehindByMs = (Ah.Core.Time.LivenessRules.ObservationAgeSeconds + 1) * 1000L;
            return Judged(PidLess(), "cust");
        }));

    [Fact]
    public void APaneIsIgnoredOnceTheObservationIsTooOld() =>
        Assert.Equal("unknown", WithFacts(Given(panes: new() { ["gn"] = "pane shows an agent" }), () =>
        {
            LaneBehindByMs = (Ah.Core.Time.LivenessRules.ObservationAgeSeconds + 1) * 1000L;
            return Judged(FiveStates(), "gn");
        }));

    [Fact]
    public void TheVerdictIsRecomposedWhenAStartingWindowEndsWithNoOtherChange()
    {
        var facts = Given(starting: ["qa"]);
        FactsOfDir = _ => facts;
        try
        {
            var (model, clock) = SeededWithActivity(StartingOverADownRow());
            using var owner = model;
            Assert.Equal("unknown", LivenessOf(model, "qa"));

            facts = Given();
            model.Emit("processes", Canon);
            Drain(model, clock);

            Assert.Equal("proven_dead", LivenessOf(model, "qa"));
        }
        finally
        {
            FactsOfDir = null;
        }
    }

    [Fact]
    public void TheSliceBodyChangesOnlyWhenAFactChanges()
    {
        var facts = Given(["b", "a"], new() { ["y"] = "2", ["x"] = "1" }, ["q", "p"]);
        FactsOfDir = _ => facts;
        try
        {
            var (model, _) = SeededWithActivity(FiveStates());
            using var owner = model;
            var lane = LastProcesses!;
            string Read() => Convert.ToBase64String(lane.Reread(Canon, FixtureNowMs).Changes[0].Body!);

            var first = Read();
            facts = Given(["a", "b"], new() { ["x"] = "1", ["y"] = "2" }, ["p", "q"]);
            Assert.Equal(first, Read());

            facts = Given(["a"], new() { ["x"] = "1", ["y"] = "2" }, ["p", "q"]);
            Assert.NotEqual(first, Read());
        }
        finally
        {
            FactsOfDir = null;
        }
    }

    [Fact]
    public void NoFactsLeaveTheBodyAsItWas()
    {
        FactsOfDir = null;
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;
        var body = JsonText.Parse(LastProcesses!.Reread(Canon, FixtureNowMs).Changes[0].Body!);
        Assert.Null(body.Member("heartbeats"));
        Assert.Null(body.Member("panes"));
        Assert.Null(body.Member("starting"));
    }
}
