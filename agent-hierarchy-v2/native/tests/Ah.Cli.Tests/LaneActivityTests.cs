using Ah.Cli.Composition;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>b376: a request in a lane's child team counts for the lead when the child's orchestrating role or the lead sent it.</summary>
public sealed class LaneActivityTests
{
    private static JsonObject Fm(string from, string? fromName) =>
        new() { ["from"] = from, ["from_name"] = fromName is null ? JsonValue.Null : JsonValue.FromString(fromName) };

    [Fact]
    public void B376ARequestFromTheChildsOrchestratorWithNoSenderNameIsTheLeads() =>
        Assert.True(LaneActivity.FromLead(Fm("orchestrator", null), "lead"));

    [Fact]
    public void B376NegativeARequestAMemberOfTheChildSentIsNotTheLeads() =>
        Assert.False(LaneActivity.FromLead(Fm("implementor", "impl-1"), "lead"));
}
