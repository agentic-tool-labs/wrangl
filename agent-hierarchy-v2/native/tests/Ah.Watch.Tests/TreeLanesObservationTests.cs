using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>
/// A death rests on a reading of the process table, so a team summary is served with that death unproven once the reading is older than the
/// observation bound, whatever was composed when it was fresh; and only a registered process seen as itself makes an absent pid prove one.
/// </summary>
public sealed partial class TreeLanesTests
{
    private const long BoundMs = Ah.Core.Time.LivenessRules.ObservationAgeSeconds * 1000L;

    private static JsonValue Served(ReadModel model) =>
        JsonText.Parse(model.Manifest("summary")!).Member("slices")!.Value.AsArray()
            .Single(static s => s.Member("id")!.Value.AsString() == SummaryId).Member("body")!.Value;

    private static string ServedLiveness(ReadModel model) =>
        Text(Served(model).Member("member_states")!.Value.Member(MemberId(model, "gn"))!.Value, "liveness");

    [Fact]
    public void OB3ASummaryComposedFreshIsServedWithItsDeathUnprovenOnceTheReadingPassesTheBound()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;
        Assert.Equal("proven_dead", ServedLiveness(model));

        ServeLaterMs = BoundMs + 1;
        try
        {
            var served = Served(model);
            var gn = served.Member("member_states")!.Value.Member(MemberId(model, "gn"))!.Value;
            Assert.Equal("not_seen", Text(gn, "liveness"));
            Assert.NotEqual("gone", Text(gn, "state"));
            Assert.Equal(5, (int)Num(served, "members"));
            Assert.Null(served.Member("if_unobserved"));
        }
        finally
        {
            ServeLaterMs = 0;
        }
    }

    [Fact]
    public void NegativeOB3AReadingExactlyAtTheBoundStillServesTheDeath()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        ServeLaterMs = BoundMs;
        try
        {
            Assert.Equal("proven_dead", ServedLiveness(model));
            Assert.Null(Served(model).Member("if_unobserved"));
        }
        finally
        {
            ServeLaterMs = 0;
        }
    }

    [Fact]
    public async Task OB3TheSummaryIsReservedAtTheInstantTheReadingGoesStale()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;
        var first = JsonText.Parse(await model.PollAsync(0, "e1", [SummaryId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var cursor = (long)first.Member("seq")!.Value.AsNumber();

        ServeLaterMs = BoundMs + 1_000;
        try
        {
            var next = JsonText.Parse(await model.PollAsync(cursor, "e1", [SummaryId], TimeSpan.Zero, TestContext.Current.CancellationToken));
            var slice = Assert.Single(next.Member("slices")!.Value.AsArray());
            Assert.Equal("not_seen", Text(slice.Member("body")!.Value.Member("member_states")!.Value.Member(MemberId(model, "gn"))!.Value, "liveness"));
        }
        finally
        {
            ServeLaterMs = 0;
        }
    }

    [Fact]
    public async Task NegativeOB3ASummaryIsNotReservedWhileTheReadingIsFresh()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;
        var first = JsonText.Parse(await model.PollAsync(0, "e1", [SummaryId], TimeSpan.Zero, TestContext.Current.CancellationToken));
        var cursor = (long)first.Member("seq")!.Value.AsNumber();

        ServeLaterMs = BoundMs - 1_000;
        try
        {
            var next = JsonText.Parse(await model.PollAsync(cursor, "e1", [SummaryId], TimeSpan.Zero, TestContext.Current.CancellationToken));
            Assert.Empty(next.Member("slices")!.Value.AsArray());
        }
        finally
        {
            ServeLaterMs = 0;
        }
    }

    [Fact]
    public void OB4TheServedSummaryReadsTheLatestObservationTimeNotTheOneItWasComposedWith()
    {
        var (model, clock) = SeededWithActivity(FiveStates());
        using var owner = model;
        var stored = Summary(model).ToString();

        ServeLaterMs = BoundMs + 1_000;
        LaneBehindByMs = -120_000;
        try
        {
            Assert.Equal("not_seen", ServedLiveness(model));
            LastProcesses!.Poll(model.Emit);
            Drain(model, clock);

            Assert.Equal("proven_dead", ServedLiveness(model));
            Assert.Equal(stored, Summary(model).ToString());
        }
        finally
        {
            ServeLaterMs = 0;
            LaneBehindByMs = 0;
        }
    }

    [Fact]
    public void ARegisteredPidReadAsAnotherProcessIsNoReferenceSoAnAbsentPidStaysUnknown()
    {
        var reader = FiveStates();
        reader.Registrations["s-blk"] = ("{\"session_id\":\"s-blk\",\"address\":\"blk\",\"addresses\":[\"blk\"],\"pid\":43,\"pid_start\":\"ps-before-recycle\",\"at\":\"2026-06-01T10:06:00Z\"}", [TeamPath]);
        reader.LivePids.Clear();
        reader.LivePids.AddRange([1, 43]);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Equal("not_seen", ServedLiveness(model));
    }

    [Fact]
    public void AMemberPidWhoseStartIsSixtySecondsOffIsNoReference()
    {
        var reader = FiveStates();
        reader.Registrations["s-blk"] = ("{\"session_id\":\"s-blk\",\"address\":\"blk\",\"addresses\":[\"blk\"],\"pid\":43,\"pid_start\":\"Mon Jun  1 13:00:00 2026\",\"at\":\"2026-06-01T10:06:00Z\"}", [TeamPath]);
        reader.LivePids.Clear();
        reader.LivePids.AddRange([1, 43]);
        PidStartOverrides[43] = "Mon Jun  1 13:01:00 2026";
        try
        {
            var (model, _) = SeededWithActivity(reader);
            using var owner = model;

            Assert.Equal("not_seen", ServedLiveness(model));
        }
        finally
        {
            PidStartOverrides.Clear();
        }
    }

    [Fact]
    public void NegativeAMemberPidWhoseStartEqualsItsRecordedStartIsTheReference()
    {
        var reader = FiveStates();
        reader.Registrations["s-blk"] = ("{\"session_id\":\"s-blk\",\"address\":\"blk\",\"addresses\":[\"blk\"],\"pid\":43,\"pid_start\":\"Mon Jun  1 13:00:00 2026\",\"at\":\"2026-06-01T10:06:00Z\"}", [TeamPath]);
        reader.LivePids.Clear();
        reader.LivePids.AddRange([1, 43]);
        PidStartOverrides[43] = "Mon Jun  1 13:00:00 2026";
        try
        {
            var (model, _) = SeededWithActivity(reader);
            using var owner = model;

            Assert.Equal("proven_dead", ServedLiveness(model));
        }
        finally
        {
            PidStartOverrides.Clear();
        }
    }

    [Fact]
    public void TD1TheServedLivenessCountsAreTheTallyOfTheServedMemberWords()
    {
        var (model, _) = SeededWithActivity(FiveStates());
        using var owner = model;

        var served = Served(model);
        var words = served.Member("member_states")!.Value.AsObject().Keys.Select(id => Text(served.Member("member_states")!.Value.Member(id)!.Value, "liveness")).ToList();
        var counts = served.Member("liveness_counts")!.Value;

        Assert.Equal(5, words.Count);
        foreach (var word in new[] { "live", "unknown", "not_seen", "proven_dead" })
        {
            Assert.Equal(words.Count(w => w == word), (int)Num(counts, word));
        }

        Assert.True(words.Contains("proven_dead", StringComparer.Ordinal), "the fixture holds a proven death, so the tally is not all one word");
    }

    private static string WithPidProof(string registration, bool proof) =>
        registration[..^1] + ",\"pid_proof\":" + (proof ? "true" : "false") + "}";

    [Fact]
    public void TM3AGonePidOfARegistrationNotShownToBeTheHarnessIsServedUnknownNotProvenDead()
    {
        var reader = FiveStates();
        reader.Registrations["s-gn"] = (WithPidProof(Reg("s-gn", "gn", 44, "2026-06-01T10:07:00Z"), false), [TeamPath]);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.NotEqual("proven_dead", ServedLiveness(model));
    }

    [Fact]
    public void NegativeTM3AGonePidOfARegistrationShownToBeTheHarnessIsServedProvenDead()
    {
        var reader = FiveStates();
        reader.Registrations["s-gn"] = (WithPidProof(Reg("s-gn", "gn", 44, "2026-06-01T10:07:00Z"), true), [TeamPath]);
        var (model, _) = SeededWithActivity(reader);
        using var owner = model;

        Assert.Equal("proven_dead", ServedLiveness(model));
    }

    [Fact]
    public void ARegistrationNewerThanTheReadingIsASignOfLifeThatOutdatesIt()
    {
        var reader = FiveStates();
        _ = reader.Activities.Remove("s-gn");
        reader.Registrations["s-gn"] = (Reg("s-gn", "gn", 44, Iso(FixtureNowMs - 30_000)), [TeamPath]);
        LaneBehindByMs = 60_000;
        try
        {
            var (model, _) = SeededWithActivity(reader);
            using var owner = model;

            Assert.Equal("unknown", ServedLiveness(model));
        }
        finally
        {
            LaneBehindByMs = 0;
        }
    }

    [Fact]
    public void NegativeARegistrationOlderThanTheReadingLeavesTheDeathProven()
    {
        var reader = FiveStates();
        _ = reader.Activities.Remove("s-gn");
        reader.Registrations["s-gn"] = (Reg("s-gn", "gn", 44, Iso(FixtureNowMs - 120_000)), [TeamPath]);
        LaneBehindByMs = 60_000;
        try
        {
            var (model, _) = SeededWithActivity(reader);
            using var owner = model;

            Assert.Equal("proven_dead", ServedLiveness(model));
        }
        finally
        {
            LaneBehindByMs = 0;
        }
    }

    [Fact]
    public void TS7ATeamWhoseOnlyMemberIsUnknownIsNeverEndedHoweverOldItsEvidence()
    {
        Assert.NotEqual("ended", TeamStateWith(r => { r.Teams[TeamPath] = Doc("so", "dev"); r.LivePids.Clear(); r.Registrations.Clear(); r.Activities.Clear(); }));
        Assert.NotEqual("ended", TeamStateWith(r => { r.Teams[TeamPath] = Doc("so", "dev"); r.LivePids.Clear(); r.Activities["s-dev"] = Act("idle"); }));
    }
}
