using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>
/// The stream rules the view builds with: the pure derivations, the worktree-existence fact from the file seam, and the open
/// exchanges from the state reader's <c>msgs.open-exchanges</c> op.
/// </summary>
internal sealed class ViewStreamRules(StreamDerivations rules, IFileReader files, IViewStateReader? state) : IStreamRules
{
    public IReadOnlyList<KeyValuePair<string, string>> GlyphStates => rules.GlyphStates;

    public JsonObject StreamsOf(JsonValue? team) => rules.StreamsOf(team);

    public JsonArray StreamMembers(JsonValue? team, string name) => rules.StreamMembers(team, name);

    public string StreamGlyph(string? state, JsonArray statuses, IReadOnlyList<string> owedTo) => rules.StreamGlyph(state, statuses, owedTo);

    public string StreamGlyphState(string? state, JsonArray statuses, IReadOnlyList<string> owedTo, bool statusKnown = true) =>
        rules.StreamGlyphState(state, statuses, owedTo, statusKnown);

    public JsonArray StreamMemberStatuses(JsonArray members, JsonArray? agents, JsonValue? self = null) => rules.StreamMemberStatuses(members, agents, self);

    public JsonObject StreamFacts(JsonValue record, JsonArray members, JsonArray statuses, JsonArray exchanges, JsonArray? agents) =>
        rules.StreamFacts(record, members, statuses, exchanges, agents, files.Exists);

    public JsonArray OpenTeamExchanges(string dir, string? team, bool allTeams, bool created = false)
    {
        var args = new JsonObject { ["dir"] = dir, ["all_teams"] = allTeams };
        if (team is not null)
        {
            args["team"] = team;
        }

        var read = (state ?? throw new InvalidOperationException("the view source has no state reader")).Read("msgs", "open-exchanges", dir, args);
        var result = new JsonArray();
        foreach (var row in read.Member("exchanges")!.Value.AsArray())
        {
            var fm = row.Member("front_matter") is { IsMap: true } map ? map.AsObject() : new JsonObject();
            var age = row.Member("age_seconds") is { IsNumber: true } n ? n.AsNumber() : double.PositiveInfinity;
            var slug = row.Member("slug") is { IsText: true } s ? s.AsString() : null;
            result.Add(rules.OpenExchangeRow(new OpenExchange(row.Member("id")!.Value.AsString(), slug, row.Member("request_path")!.Value.AsString()), fm, age, created));
        }

        return result;
    }
}
