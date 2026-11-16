using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>A member no team lists is judged by its registration when that proves the session dead; anything less stays unknown.</summary>
public sealed class AttributedNoTeamTests
{
    private sealed class Registered(string address, bool provenDead) : IMemberLiveness
    {
        public MemberRegistrationLiveness? ForMember(string memberName, string team) =>
            $"{memberName}@{team}" == address ? new MemberRegistrationLiveness("sess-1", false, provenDead ? "pid gone" : "unproven", ProvenDead: provenDead) : null;

        public bool PidIdentityLive(int pid, string? recordedStart) => false;

        public IReadOnlyList<JsonObject> Registrations() => [new JsonObject { ["session_id"] = "sess-1", ["address"] = address }];
    }

    [Theory]
    [InlineData("fix-reviewer@fix", true, "proven_dead")]
    [InlineData("fix-reviewer@fix", false, "unknown")]
    [InlineData("fix-reviewer-2@fix", true, "unknown")]
    public void AMemberNoTeamListsIsProvenDeadByItsDeadRegistrationOnly(string address, bool provenDead, string liveness)
    {
        var services = StatusEntriesFixture.Create().Services();
        services.Core.Liveness = new Registered(address, provenDead);

        var answer = services.Roster.AttributedLiveness([], "fix-reviewer", null);

        Assert.Equal(liveness, answer["liveness"].AsString());
    }

    /// <summary>b459 step 16: a registered member's record comes out as the copy with the registration's session id did, whether its record holds that session or another; the roster row is never changed.</summary>
    [Theory]
    [InlineData("sess-1")]
    [InlineData("sess-old")]
    public void ARegisteredMembersRecordIsTodaysCopyWithTheRegistrationSessionAndTheRowIsUnchanged(string held)
    {
        var services = StatusEntriesFixture.Create().Services();
        services.Core.Liveness = new Registered("fix-reviewer@fix", false);
        var row = new JsonObject { ["name"] = "fix-reviewer", ["role"] = "reviewer", ["session_id"] = held, ["team"] = "fix", ["status"] = "up" };
        var before = JsonText.Stringify(row, 0);
        var today = row.Clone();
        today["session_id"] = "sess-1";

        var answer = services.Roster.AttributedLiveness([row], "fix-reviewer", "fix");

        Assert.Equal(JsonText.Stringify(today, 0), JsonText.Stringify(answer["rec"], 0));
        Assert.Equal(before, JsonText.Stringify(row, 0));
    }
}
