using Ah.Core.Json;

namespace Ah.Hierarchy.Tests;

/// <summary>The state block's member and workstream words read the member's verdict; an unproven member is never dead.</summary>
public sealed class StateBlockVerdictTests
{
    private const double Now = 1780315200000;

    private static string Block(Func<string, JsonValue, string?>? verdict)
    {
        var fixture = StatusEntriesFixture.Create();
        var dir = StatusEntriesFixture.Pool(fixture.R);
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        File.WriteAllText(Path.Combine(dir, "teams", "tm.json"), """
            {"team_id":"T1","members":[
            {"name":"found","role":"architect","route":"pane","transport_id":"%1"},
            {"name":"up","role":"architect","route":"pane","transport_id":"%2"},
            {"name":"down","role":"architect","route":"pane","transport_id":"%3"},
            {"name":"blind","role":"architect","route":"pane","transport_id":"%4"}],
            "streams":{"w1":{"sub_orchestrator":"up"},"w2":{"sub_orchestrator":"down"},"w3":{"sub_orchestrator":"blind"}}}
            """);
        fixture.Peers(dir, "{\"type\":\"peer\",\"ts\":\"2026-06-01T11:59:50.000Z\",\"name\":\"found\",\"role\":\"architect\",\"status\":\"up\",\"pid\":1}");
        var resolved = JsonText.Parse("""{"enabled":true,"roles":{"architect":{"model":"opus"}},"cwd":"/x","team":"tm","ownedTeams":[]}""").AsObject();
        return fixture.Services(memberVerdict: verdict).Roster.BuildStateBlock(dir, resolved, "repo", JsonValue.FromString("opus"), null, null, Now);
    }

    private static string? ByName(JsonValue member) => member.Member("name")!.Value.AsString() switch
    {
        "up" => "live",
        "down" => "proven_dead",
        "blind" => "unknown",
        _ => null,
    };

    [Fact]
    public void AMemberLineWithNoRowSaysItsVerdict()
    {
        // A found row keeps busy or idle: the contract cases of lib-hier/buildStateBlock pin those two words.
        var block = Block((_, member) => ByName(member));

        Assert.Contains("architect=up live", block, StringComparison.Ordinal);
        Assert.Contains("architect=down gone", block, StringComparison.Ordinal);
        Assert.Contains("architect=blind not-seen", block, StringComparison.Ordinal);
    }

    [Fact]
    public void AWorkstreamLineSaysDeadOnlyForAProvenDeath()
    {
        var block = Block((_, member) => ByName(member));

        Assert.Contains("workstream w1: up live ", block, StringComparison.Ordinal);
        Assert.Contains("workstream w2: down dead ", block, StringComparison.Ordinal);
        Assert.Contains("workstream w3: blind unknown ", block, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeWithNoVerdictAMemberIsNotSeenAndAWorkstreamIsNotDead()
    {
        var block = Block(null);

        Assert.Contains("architect=up not-seen", block, StringComparison.Ordinal);
        Assert.Contains("workstream w2: down unknown ", block, StringComparison.Ordinal);
        Assert.DoesNotContain(" dead ", block, StringComparison.Ordinal);
    }
}
