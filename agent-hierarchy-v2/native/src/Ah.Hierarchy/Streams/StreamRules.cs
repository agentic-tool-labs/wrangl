using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>One open request exchange of a team: its id, slug, and the request message file it is read from.</summary>
public sealed record OpenExchange(string Id, string? Slug, string RequestPath);

/// <summary>Where the open exchanges of a team come from (the hierarchy library's <c>openExchanges</c>).</summary>
public interface IOpenExchangeSource
{
    /// <summary>The open exchanges of <paramref name="team"/> (null: the default team), or of every team when <paramref name="allTeams"/>.</summary>
    IReadOnlyList<OpenExchange> OpenExchanges(string dir, string? team, bool allTeams);

    /// <summary>The front matter of a message file, or null when it cannot be read.</summary>
    JsonObject? FrontMatter(string path);

    /// <summary>The age in seconds of an ISO timestamp, never negative; infinite when it cannot be parsed.</summary>
    double AgeSeconds(JsonValue? timestamp);
}

/// <summary>
/// The pure parts of a stream's status: which streams a team has, which members belong to one, each member's live status, the glyph
/// that summarises them, and the stream's row of facts. Nothing here talks to herdr or writes a file.
/// </summary>
public interface IStreamRules
{
    /// <summary>The words a view carries in place of the glyph characters, in table order.</summary>
    IReadOnlyList<KeyValuePair<string, string>> GlyphStates { get; }

    /// <summary>The streams object of a team document, or an empty object.</summary>
    JsonObject StreamsOf(JsonValue? team);

    /// <summary>The team's members tagged into stream <paramref name="name"/>, in the order they joined.</summary>
    JsonArray StreamMembers(JsonValue? team, string name);

    /// <summary>The status glyph of a stream, first matching rule wins.</summary>
    string StreamGlyph(string? state, JsonArray statuses, IReadOnlyList<string> owedTo);

    /// <summary>The glyph's state word; <c>unknown</c> for a stream not done whose members' status could not be read.</summary>
    string StreamGlyphState(string? state, JsonArray statuses, IReadOnlyList<string> owedTo, bool statusKnown = true);

    /// <summary>Each member's live status, matched to the herdr agent list by pane id; <c>gone</c> for no match, <c>unknown</c> for no list.</summary>
    JsonArray StreamMemberStatuses(JsonArray members, JsonArray? agents, JsonValue? self = null);

    /// <summary>The facts of one stream beyond its name, state and tab.</summary>
    JsonObject StreamFacts(JsonValue record, JsonArray members, JsonArray statuses, JsonArray exchanges, JsonArray? agents);

    /// <summary>The team's open exchanges with the names they carry.</summary>
    JsonArray OpenTeamExchanges(string dir, string? team, bool allTeams, bool created = false);
}

/// <summary>The stream rules, reading worktree existence through <paramref name="Files"/> and exchanges through <paramref name="Exchanges"/>.</summary>
public sealed record StreamRules(IFileReader Files, IOpenExchangeSource Exchanges) : IStreamRules
{
    private static readonly StreamDerivations Derived = new();

    public IReadOnlyList<KeyValuePair<string, string>> GlyphStates => Derived.GlyphStates;

    public JsonObject StreamsOf(JsonValue? team) => Derived.StreamsOf(team);

    public JsonArray StreamMembers(JsonValue? team, string name) => Derived.StreamMembers(team, name);

    public string StreamGlyph(string? state, JsonArray statuses, IReadOnlyList<string> owedTo) => Derived.StreamGlyph(state, statuses, owedTo);

    public string StreamGlyphState(string? state, JsonArray statuses, IReadOnlyList<string> owedTo, bool statusKnown = true) =>
        Derived.StreamGlyphState(state, statuses, owedTo, statusKnown);

    public JsonArray StreamMemberStatuses(JsonArray members, JsonArray? agents, JsonValue? self = null) => Derived.StreamMemberStatuses(members, agents, self);

    public JsonObject StreamFacts(JsonValue record, JsonArray members, JsonArray statuses, JsonArray exchanges, JsonArray? agents) =>
        Derived.StreamFacts(record, members, statuses, exchanges, agents, Files.Exists);

    public JsonArray OpenTeamExchanges(string dir, string? team, bool allTeams, bool created = false)
    {
        var result = new JsonArray();
        foreach (var e in Exchanges.OpenExchanges(dir, team, allTeams))
        {
            var fm = Exchanges.FrontMatter(e.RequestPath) ?? new JsonObject();
            result.Add(Derived.OpenExchangeRow(e, fm, Exchanges.AgeSeconds(fm.Get("created")), created));
        }

        return result;
    }
}
