using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A member nothing has seen for a while says so, does not count as working, and does not hold its team off quiet.</summary>
public sealed class LivenessProjectionNotSeenTests
{
    private const double Now = 1_800_000_000_000;
    private const string Old = "2020-01-01T00:00:00.000Z";

    private static JsonValue Member(string liveness, string state, string? lastLife) =>
        JsonValue.FromObject(new JsonObject
        {
            ["liveness"] = liveness,
            ["state"] = state,
            ["process"] = "unknown",
            ["last_life_at"] = lastLife is null ? JsonValue.Null : JsonValue.FromString(lastLife),
        });

    private static JsonObject Team(int working, params (string Id, JsonValue Entry)[] members)
    {
        var states = new JsonObject();
        foreach (var (id, entry) in members)
        {
            states[id] = entry;
        }

        return new JsonObject
        {
            ["state"] = "working", ["running"] = true, ["members"] = members.Length, ["streams_open"] = 0, ["working"] = working,
            ["updated_at"] = "2026-11-01T00:00:00.000Z", ["stale_at"] = "2026-11-02T00:00:00.000Z",
            ["member_states"] = JsonValue.FromObject(states),
        };
    }

    private static JsonObject Entry(JsonObject body, string id) => body.Get("member_states")!.Value.AsObject().Get(id)!.Value.AsObject();

    [Fact]
    public void AWorkingMemberNotSeenSaysSoAndNoLongerCountsAsWorking()
    {
        var body = Team(1, ("member:d/t/a", Member("unknown", "working", Old)));

        LivenessProjection.Project(body, Now);

        Assert.Equal("not seen", Entry(body, "member:d/t/a").Str("process"));
        Assert.Equal("unknown", Entry(body, "member:d/t/a").Str("state"));
        Assert.Equal(0, body.Get("working")!.Value.AsNumber());
    }

    [Fact]
    public void AWorkingMemberNotSeenDoesNotBlockQuiet()
    {
        var body = Team(1, ("member:d/t/a", Member("unknown", "working", Old)));

        LivenessProjection.Project(body, Now);
        SummaryData.ApplyStatus(body);

        Assert.Equal(body.Get("stale_at")!.Value, body.Get("quiet_at")!.Value);
    }

    [Fact]
    public void NegativeAWorkingMemberSeenWithinTheFreshWindowStillCounts()
    {
        var justSeen = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)Now - 1000));
        var body = Team(1, ("member:d/t/a", Member("unknown", "working", justSeen)));

        LivenessProjection.Project(body, Now);
        SummaryData.ApplyStatus(body);

        Assert.Equal("working", Entry(body, "member:d/t/a").Str("state"));
        Assert.Equal(1, body.Get("working")!.Value.AsNumber());
        Assert.True(body.Get("quiet_at")!.Value.IsNull);
    }

    [Fact]
    public void NegativeAnIdleMemberNotSeenTakesNothingOffTheWorkingCount()
    {
        var body = Team(1, ("member:d/t/a", Member("unknown", "idle", Old)), ("member:d/t/b", Member("live", "working", Old)));

        LivenessProjection.Project(body, Now);

        Assert.Equal("unknown", Entry(body, "member:d/t/a").Str("state"));
        Assert.Equal(1, body.Get("working")!.Value.AsNumber());
    }

    // ah2-242 C2: an idle member nothing locates reads No signal; a located one (fresh life, live, or marked by the composer) keeps Idle;
    // working is demoted as before whatever the mark; blocked is never rewritten. The mark is never served.
    [Theory]
    [InlineData("unknown", "idle", true, false, "unknown", "not_seen", 1)] // not located: was Idle
    [InlineData("unknown", "idle", false, false, "idle", "unknown", 1)] // negative: last life fresh (L2)
    [InlineData("live", "idle", true, false, "idle", "live", 1)] // negative: live verdict (L1)
    [InlineData("unknown", "idle", true, true, "idle", "not_seen", 1)] // negative: Orchestrator / blind / starting mark (L3-L5)
    [InlineData("unknown", "working", true, false, "unknown", "not_seen", 0)]
    [InlineData("unknown", "working", true, true, "unknown", "not_seen", 0)] // the mark does not stop the working demotion
    [InlineData("unknown", "blocked", true, false, "blocked", "not_seen", 1)] // negative: blocked keeps its alarm
    public void AnIdleMemberNothingLocatesReadsNoSignal(string liveness, string state, bool old, bool located, string servedState, string servedLiveness, double working)
    {
        var lastLife = old ? Old : Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)Now - 1000));
        var member = Member(liveness, state, lastLife);
        if (located)
        {
            member.AsObject()[TeamSummaryComposer.LocatedKey] = true;
        }

        var body = Team(1, ("member:d/t/a", member), ("member:d/t/b", Member("live", "working", Old)));
        // The stuck rule reads the composed entry (ReadModel.AssigneeState does not project), so a demotion here does not move it.
        Assert.Equal(state, StuckProjection.ServedState(member));

        LivenessProjection.Project(body, Now);

        Assert.Equal(servedState, Entry(body, "member:d/t/a").Str("state"));
        Assert.Equal(servedLiveness, Entry(body, "member:d/t/a").Str("liveness"));
        Assert.Equal(working, body.Get("working")!.Value.AsNumber());
        Assert.False(Entry(body, "member:d/t/a").ContainsKey(TeamSummaryComposer.LocatedKey));
    }

    // ah2-31: an item only a member that is no longer seen was on stops counting, so its team goes quiet like any stale team.
    private static JsonObject TeamWithItem(JsonValue member)
    {
        var body = Team(1, ("member:d/t/a", member));
        body["in_progress_now"] = 1;
        var on = new JsonArray();
        on.Add("member:d/t/a");
        var items = new JsonArray();
        items.Add(on);
        body[TeamSummaryComposer.UnseenWorkKey] = items;
        return body;
    }

    [Fact]
    public void AnItemOnlyANotSeenMemberWasOnStopsCountingAndTheTeamGoesQuietAtStale()
    {
        var body = TeamWithItem(Member("unknown", "working", Old));

        LivenessProjection.Project(body, Now);
        SummaryData.ApplyStatus(body);

        Assert.Equal(0, body.Get("in_progress_now")!.Value.AsNumber());
        Assert.Equal(body.Get("stale_at")!.Value, body.Get("quiet_at")!.Value);
        Assert.False(body.ContainsKey(TeamSummaryComposer.UnseenWorkKey));
    }

    [Fact]
    public void NegativeAnItemAMemberSeenJustNowIsOnStillCountsAndHoldsOffQuiet()
    {
        var justSeen = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)Now - 1000));
        var body = TeamWithItem(Member("unknown", "working", justSeen));

        LivenessProjection.Project(body, Now);
        SummaryData.ApplyStatus(body);

        Assert.Equal(1, body.Get("in_progress_now")!.Value.AsNumber());
        Assert.True(body.Get("quiet_at")!.Value.IsNull);
        Assert.False(body.ContainsKey(TeamSummaryComposer.UnseenWorkKey));
    }

    [Fact]
    public void TheComposerListsOnlyItemsNoLiveMemberIsOn()
    {
        var team = new TeamEntry("/d", "/d/teams/t.json", "t", JsonText.Parse("{}"), 0);
        var items = JsonText.Parse("""
            [{"state":"in_progress","owner":"a","refs":[]},
             {"state":"in_progress","owner":"b","refs":[]},
             {"state":"in_progress","owner":null,"refs":["x1"]},
             {"state":"reported","owner":"a","refs":[]}]
            """).AsArray();
        MemberReading[] members =
        [
            new("a", "working", "working", 0),
            new("b", "working", "working", 0, LivenessVerdict.Live),
            new("c", "idle", "idle", 0),
        ];

        var unseen = TeamSummaryComposer.UnseenWork([.. items], team, members, [("x1", "c")]);

        // a's item (undecided holder) and the one whose open request goes to c (undecided); not b's (live), not the reported one
        Assert.Equal(
            """[["member:%/t/a"],["member:%/t/c"]]""".Replace("%", team.DirId, StringComparison.Ordinal),
            JsonText.Stringify(unseen, 0));
    }
}
