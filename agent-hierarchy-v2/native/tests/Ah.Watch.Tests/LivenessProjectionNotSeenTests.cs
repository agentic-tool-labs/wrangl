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

        Assert.Equal("idle", Entry(body, "member:d/t/a").Str("state"));
        Assert.Equal(1, body.Get("working")!.Value.AsNumber());
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
