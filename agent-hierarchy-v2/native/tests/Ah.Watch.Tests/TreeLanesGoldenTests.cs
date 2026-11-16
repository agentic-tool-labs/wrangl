using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>
/// The tree and exchange slices as the real lanes and composers publish them, committed as <c>Goldens/tree-exchange-slices.json</c>.
/// The web client's shape-contract test (<c>tests/web/contract.test.mjs</c>) loads this file, so a change to what the server publishes
/// fails there until the client follows it.
/// </summary>
public sealed partial class TreeLanesTests
{
    private static readonly string[] GoldenPrefixes = ["team:", "member:", "topology:", "worktree:", "list:teams", "exchange:", "list:exchanges:", "list:feed:", "team-summary:"];

    private static string Answered(string to, string from, string? toName, string? fromName, string? team) =>
        "{\"slug\":\"done\",\"to\":\"" + to + "\",\"from\":\"" + from + "\",\"to_name\":" + Quoted(toName) + ",\"from_name\":" + Quoted(fromName)
        + ",\"team\":" + Quoted(team) + ",\"created\":\"2026-06-01T10:30:00Z\",\"has_request\":true,\"has_response\":true,\"request_excerpt\":\"do it\\nnow\",\"response_excerpt\":\"ok\",\"request_bytes\":120,\"response_bytes\":40}";

    [Fact]
    public void TheTreeAndExchangeSlicesAreTheCommittedGoldenTheWebClientReads()
    {
        var reader = Truth();
        reader.Exchanges["x1"] = Msg("developer", "orchestrator", "dev", null, "default");
        reader.Exchanges["x2"] = Msg("tester", "developer", "qa", "dev", "default");
        reader.Exchanges["x3"] = Answered("tester", "orchestrator", "qa", null, "default");
        reader.Exchanges["x4"] = Msg("developer", "orchestrator", "dev", null, "other");
        reader.Exchanges["x5"] = Msg("developer", "orchestrator", "dev", null, null);
        var (model, _) = Seeded(reader);
        using var owner = model;

        var slices = new JsonArray();
        foreach (var slice in model.Store.Current.Slices.Values.OrderBy(static s => s.Id, StringComparer.Ordinal))
        {
            if (GoldenPrefixes.Any(p => slice.Id.StartsWith(p, StringComparison.Ordinal)))
            {
                slices.Add(JsonValue.FromObject(SliceStore.ToJson(slice)));
            }
        }

        ModelGolden.Check(new JsonObject { ["slices"] = JsonValue.FromArray(slices) }, "tree-exchange-slices");
    }
}
