using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>Which registration stands for a member: one that lists the team's file and names the member's address or bare name.</summary>
public sealed class TreeDataRegistersTests
{
    private static readonly TeamEntry Team = new("/d", "/d/teams/t.json", "t", JsonText.Parse("{}"), 0);

    [Theory]
    [InlineData("{\"team_files\":[\"/d/teams/t.json\"],\"address\":\"imp@t\"}", "imp", true)]
    [InlineData("{\"team_files\":[5,\"/d/teams/t.json\"],\"addresses\":[null,\"lead@t\",\"imp@t\"]}", "imp", true)]
    [InlineData("{\"team_files\":[\"/d/teams/t.json\"],\"addresses\":[\"imp\"]}", "imp", true)] // a bare name is still its own
    [InlineData("{\"team_files\":[\"/d/teams/t.json\"],\"addresses\":[\"orchestrator@t\"]}", "@orchestrator", true)]
    [InlineData("{\"team_files\":[\"/d/teams/t.json\"],\"addresses\":[\"imp@t2\",\"imp@\",7]}", "imp", false)]
    [InlineData("{\"team_files\":[\"/d/teams/t.json\"],\"addresses\":[\"@orchestrator\"]}", "@orchestrator", false)] // the lead has no bare-name form
    [InlineData("{\"team_files\":[\"/d/teams/t.json\"],\"addresses\":[\"orchestrator@t\"]}", "orchestrator", false)] // the reserved name is no member
    [InlineData("{\"team_files\":[\"/d/teams/t2.json\",\"/d/teams\"],\"address\":\"imp@t\"}", "imp", false)] // not this team's file
    [InlineData("{\"team_files\":[],\"address\":\"imp@t\"}", "imp", false)]
    [InlineData("{\"address\":\"imp@t\"}", "imp", false)]
    public void ARegistrationStandsForAMemberOnlyByTheTeamsFileAndAnExactAddressAndNegativeANearOneDoesNot(string registration, string member, bool expected) =>
        Assert.Equal(expected, TreeData.Registers(JsonText.Parse(registration), Team, member));
}
