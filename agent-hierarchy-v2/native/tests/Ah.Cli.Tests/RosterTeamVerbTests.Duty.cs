using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>Dismissing the deputy on duty clears <c>duty</c> before its row goes.</summary>
public sealed partial class RosterTeamVerbTests
{
    [Fact]
    public void DismissingTheDeputyOnDutyClearsTheDuty()
    {
        const string Deputy = "{\"role\":\"deputy-chief\",\"name\":\"proj-deputy-chief\",\"route\":\"peer\",\"transport_id\":\"%3\",\"model\":\"sonnet\"}";
        WriteTeam("proj", LiveTeamJson("tmux", TwoPeers + "," + Deputy).TrimEnd('}') + ",\"duty\":{\"deputy\":\"proj-deputy-chief\",\"since\":\"2026-11-08T00:00:00.000Z\",\"cause\":\"duty on\"}}");
        var token = TokenOf(Roster("dismiss", "proj-deputy-chief", "--plan", "--team", "proj").Stdout);

        var (code, _, err) = Roster("dismiss", "proj-deputy-chief", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, err);
        Assert.Null(JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("duty"));
    }
}
