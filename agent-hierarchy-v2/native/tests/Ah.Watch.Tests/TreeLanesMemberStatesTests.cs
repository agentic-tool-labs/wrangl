using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>team-summary serves each member's state from the same readings its counts come from.</summary>
public sealed partial class TreeLanesTests
{
    private static string MemberId(ReadModel model, string member) => TreeIds.Member(TreeData.Teams(model.Records).Single().Dir, "default", member);

    private static TreeReader FiveStates()
    {
        var reader = Truth();
        reader.Teams[TeamPath] = Doc("so", "dev", "qa", "blk", "gn");
        reader.Registrations["s-blk"] = (Reg("s-blk", "blk", 43, "2026-06-01T10:06:00Z"), [TeamPath]);
        reader.Registrations["s-gn"] = (Reg("s-gn", "gn", 44, "2026-06-01T10:07:00Z"), [TeamPath]);
        reader.LivePids.AddRange([1, 42, 43]);
        reader.Activities["s-dev"] = Act("working");
        reader.Activities["so"] = Act("idle");
        reader.Activities["s-blk"] = Act("blocked");
        reader.Activities["s-gn"] = Act("working");
        return reader;
    }

    [Fact]
    public void TheSummaryServesEveryMembersStateAndItsCountsAgreeWithThem()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        var body = Summary(model);
        var states = body.Member("member_states")!.Value;
        Assert.Equal("working", Text(states.Member(MemberId(model, "dev"))!.Value, "state"));
        Assert.Equal("idle", Text(states.Member(MemberId(model, TreeIds.Orchestrator))!.Value, "state"));
        Assert.Equal("blocked", Text(states.Member(MemberId(model, "blk"))!.Value, "state"));
        Assert.Equal("gone", Text(states.Member(MemberId(model, "gn"))!.Value, "state"));
        Assert.Equal("unknown", Text(states.Member(MemberId(model, "qa"))!.Value, "state"));
        var served = states.AsObject().Keys.Select(k => Text(states.Member(k)!.Value, "state")).ToList();
        Assert.Equal(5, served.Count);
        Assert.Equal(Num(body, "working"), served.Count(static s => s == "working"));
        Assert.Equal(Num(body, "blocked"), served.Count(static s => s == "blocked"));
        Assert.Equal(Num(body, "members"), served.Count(static s => s != "gone"));
    }

    // ah2-62: an open turn is served for a live Orchestrator only, beside a state that stays as the activity says.
    [Fact]
    public void TheSummaryServesTheOrchestratorsOpenTurnWhileItIsLive()
    {
        var reader = FiveStates();
        reader.Activities["so"] = Act("idle").Replace("{\"activity\"", "{\"turn_open\":true,\"activity\"", StringComparison.Ordinal);
        reader.Activities["s-dev"] = Act("idle").Replace("{\"activity\"", "{\"turn_open\":true,\"activity\"", StringComparison.Ordinal);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var body = Summary(model);
        var states = body.Member("member_states")!.Value;
        var orchestrator = states.Member(MemberId(model, TreeIds.Orchestrator))!.Value;
        Assert.True(orchestrator.Member("turn_open")!.Value.AsBool());
        Assert.Equal("idle", Text(orchestrator, "state"));
        Assert.Null(states.Member(MemberId(model, "dev"))!.Value.Member("turn_open"));
        Assert.Equal(0, Num(body, "working"));
    }

    [Fact]
    public void NegativeADeadOrchestratorsOpenTurnIsNotServed()
    {
        var reader = FiveStates();
        reader.Activities["so"] = Act("idle").Replace("{\"activity\"", "{\"turn_open\":true,\"activity\"", StringComparison.Ordinal);
        _ = reader.LivePids.Remove(1);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var orchestrator = Summary(model).Member("member_states")!.Value.Member(MemberId(model, TreeIds.Orchestrator))!.Value;
        Assert.Equal("gone", Text(orchestrator, "state"));
        Assert.Null(orchestrator.Member("turn_open"));
    }

    /// <summary>How long before the composers' clock the processes lane stamped its table reading.</summary>
    private static long LaneBehindByMs;

    /// <summary>How long after the composers' clock a response is served.</summary>
    private static long ServeLaterMs;

    /// <summary>The processes lane of the model built last, so a test can poll it.</summary>
    private static ProcessesLane? LastProcesses;

    [Fact]
    public void TheSummaryServesEachMembersLivenessFromTheSharedVerdict()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        var states = Summary(model).Member("member_states")!.Value;
        Assert.Equal("live", Text(states.Member(MemberId(model, "dev"))!.Value, "liveness"));
        Assert.Equal("live", Text(states.Member(MemberId(model, "blk"))!.Value, "liveness"));
        Assert.Equal("proven_dead", Text(states.Member(MemberId(model, "gn"))!.Value, "liveness"));
        Assert.Equal("unknown", Text(states.Member(MemberId(model, "qa"))!.Value, "liveness"));
        Assert.Equal("gone", Text(states.Member(MemberId(model, "gn"))!.Value, "state"));
    }

    [Fact]
    public void NegativeAPidMissingFromTheTableIsNotGoneWhenTheObserverSeesNoOtherProcess()
    {
        var reader = FiveStates();
        reader.LivePids.Clear();
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        var states = Summary(model).Member("member_states")!.Value;
        Assert.Equal("unknown", Text(states.Member(MemberId(model, "gn"))!.Value, "liveness"));
        Assert.NotEqual("gone", Text(states.Member(MemberId(model, "gn"))!.Value, "state"));
        Assert.Equal(5, (int)Num(Summary(model), "members"));
    }

    [Fact]
    public void NegativeAnAbsenceReadMoreThanTheObservationBoundAgoIsNotGone()
    {
        LaneBehindByMs = (Ah.Core.Time.LivenessRules.ObservationAgeSeconds + 1) * 1000L;
        try
        {
            var (model, _) = SeededWithActivity(FiveStates());
            using var owner = model;

            var states = Summary(model).Member("member_states")!.Value;
            Assert.Equal("unknown", Text(states.Member(MemberId(model, "gn"))!.Value, "liveness"));
            Assert.NotEqual("gone", Text(states.Member(MemberId(model, "gn"))!.Value, "state"));
        }
        finally
        {
            LaneBehindByMs = 0;
        }
    }

    [Fact]
    public void AnAbsenceReadJustInsideTheObservationBoundIsStillProvenDead()
    {
        LaneBehindByMs = Ah.Core.Time.LivenessRules.ObservationAgeSeconds * 1000L;
        try
        {
            var (model, _) = SeededWithActivity(FiveStates());
            using var owner = model;

            Assert.Equal("proven_dead", Text(Summary(model).Member("member_states")!.Value.Member(MemberId(model, "gn"))!.Value, "liveness"));
        }
        finally
        {
            LaneBehindByMs = 0;
        }
    }

    [Theory]
    [InlineData("gone", "proven_dead", "gone")]
    [InlineData("gone", "unknown", "unknown")]
    [InlineData("gone", "live", "unknown")]
    [InlineData("working", "unknown", "working")]
    public void TheAssigneeStateReadsGoneOnlyForAProvenDeath(string state, string liveness, string expected) =>
        Assert.Equal(expected, StuckProjection.ServedState(Ah.Core.Json.JsonText.Parse("{\"state\":\"" + state + "\",\"liveness\":\"" + liveness + "\"}")));

    [Fact]
    public void NegativeAnEntryWithNoLivenessNeverReadsGone() =>
        Assert.Equal("unknown", StuckProjection.ServedState(Ah.Core.Json.JsonText.Parse("{\"state\":\"gone\"}")));

    private static string TeamStateWith(Action<TreeReader> arrange)
    {
        var reader = Truth();
        arrange(reader);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;
        return Text(Summary(model), "state");
    }

    [Fact]
    public void TheSummaryServesTheTeamStateWordFromItsOwnCounts()
    {
        Assert.Equal("needs_you", TeamStateWith(r => { r.Teams[TeamPath] = Doc("so", "dev", "blk"); r.Registrations["s-blk"] = (Reg("s-blk", "blk", 43, "2026-06-01T10:06:00Z"), [TeamPath]); r.LivePids.AddRange([1, 42, 43]); r.Activities["s-dev"] = Act("working"); r.Activities["s-blk"] = Act("blocked"); }));
        Assert.Equal("working", TeamStateWith(r => { r.LivePids.AddRange([1, 42]); r.Activities["s-dev"] = Act("working"); }));
        Assert.Equal("idle", TeamStateWith(r => { r.LivePids.AddRange([1, 42]); r.Activities["s-dev"] = Act("idle"); }));
        Assert.Equal("ended", TeamStateWith(r => { r.Teams[TeamPath] = Doc("so", "dev"); r.LivePids.Add(77); r.Registrations["s-ref"] = (Reg("s-ref", "stranger", 77, "2026-06-01T10:08:00Z"), []); r.Activities["s-dev"] = Act("working"); }));
    }

    [Theory]
    [InlineData(true, false, 1, 0, "needs_you")]
    [InlineData(true, false, 0, 0, "ended")]
    [InlineData(false, false, 0, 0, "idle")]
    [InlineData(true, true, 1, 3, "needs_you")]
    [InlineData(true, true, 0, 1, "working")]
    [InlineData(true, true, 0, 0, "idle")]
    public void TheTeamStateIsTheFirstMatchOfNeedsYouEndedWorkingIdle(bool hasMembers, bool running, long needsYou, long working, string expected) =>
        Assert.Equal(expected, SummaryData.TeamState(hasMembers, running, needsYou, working));

    [Fact]
    public void AMemberCountsAsRunningUnlessItsVerdictIsProvenDead()
    {
        Assert.False(SummaryData.IsRunning(new MemberReading("m", "gone", "working", 0, Ah.Core.Time.LivenessVerdict.ProvenDead)));
        Assert.True(SummaryData.IsRunning(new MemberReading("m", "unknown", string.Empty, 0, Ah.Core.Time.LivenessVerdict.Unknown)));
        Assert.True(SummaryData.IsRunning(new MemberReading("m", "idle", "idle", 0, Ah.Core.Time.LivenessVerdict.Live)));
    }

    [Fact]
    public void AMembersStateAtIsTheTransitionTimeAndIsAbsentWhenNoActivityRecordExists()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        var states = Summary(model).Member("member_states")!.Value;
        var read = SummaryData.Members(model.Records, TreeData.Teams(model.Records).Single(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Single(static m => m.Member == "dev");
        Assert.True(read.StateAtMs > 0);
        Assert.Equal(Iso(read.StateAtMs), Text(states.Member(MemberId(model, "dev"))!.Value, "state_at"));
        Assert.Null(states.Member(MemberId(model, "qa"))!.Value.Member("state_at"));
    }
}
