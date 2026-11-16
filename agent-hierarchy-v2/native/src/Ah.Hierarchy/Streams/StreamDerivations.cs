using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The stream rules that read nothing: which streams a team has, which members belong to one, each member's live status, the glyph
/// that summarises them, and the stream's row of facts. It takes no file, state or library dependency; what it needs from the
/// outside (does a worktree path exist, an exchange's front matter and age) is passed in.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "The rules are carried as one value (a member of the view's services), so callers use an instance.")]
public sealed record StreamDerivations
{
    private const string Done = "done";

    public IReadOnlyList<KeyValuePair<string, string>> GlyphStates { get; } =
    [
        new("\u2713", "done"),
        new("\u00d7", "blocked"),
        new("\u25d0", "working"),
        new("\u25cb", "idle"),
        new("\u00b7", "none"),
    ];

    public JsonObject StreamsOf(JsonValue? team) =>
        team?.Member("streams") is { IsMap: true } s ? s.AsObject() : new JsonObject();

    public JsonArray StreamMembers(JsonValue? team, string name)
    {
        var result = new JsonArray();
        if (team?.Member("members") is not { IsArray: true } list)
        {
            return result;
        }

        foreach (var m in list.AsArray())
        {
            if (m.IsMap && m.AsObject().Get("stream") is { IsText: true } s && s.AsString() == name)
            {
                result.Add(m);
            }
        }

        return result;
    }

    private static bool Has(JsonArray statuses, string status) =>
        statuses.Any(m => m.Member("status") is { IsText: true } s && s.AsString() == status);

    public string StreamGlyph(string? state, JsonArray statuses, IReadOnlyList<string> owedTo)
    {
        if (state == Done)
        {
            return "✓";
        }

        if (Has(statuses, "blocked"))
        {
            return "×";
        }

        // Work owed by a dead session needs the Orchestrator.
        foreach (var name in owedTo)
        {
            if (statuses.Any(m => m.Member("name") is { IsText: true } n && n.AsString() == name
                && m.Member("status") is { IsText: true } st && st.AsString() == "gone"))
            {
                return "×";
            }
        }

        if (Has(statuses, "working"))
        {
            return "◐";
        }

        return Has(statuses, "idle") || Has(statuses, Done) ? "○" : "·";
    }

    public string StreamGlyphState(string? state, JsonArray statuses, IReadOnlyList<string> owedTo, bool statusKnown = true)
    {
        if (state != Done && !statusKnown)
        {
            return "unknown";
        }

        var glyph = StreamGlyph(state, statuses, owedTo);
        return GlyphStates.First(g => g.Key == glyph).Value;
    }

    public JsonArray StreamMemberStatuses(JsonArray members, JsonArray? agents, JsonValue? self = null)
    {
        var result = new JsonArray();
        foreach (var m in members)
        {
            var row = new JsonObject();
            if (m.Member("name") is { } name)
            {
                row["name"] = name;
            }

            var pane = m.Member("transport_id");
            if (self is { IsTruthy: true } s && pane is { IsTruthy: true } && Same(pane, s.Member("pane")))
            {
                if (s.Member("state") is { } selfState)
                {
                    row["status"] = selfState;
                }
            }
            else if (agents is null)
            {
                row["status"] = "unknown";
            }
            else
            {
                JsonValue? found = null;
                foreach (var x in agents)
                {
                    if (Same(x.Member("pane_id"), pane))
                    {
                        found = x;
                        break;
                    }
                }

                row["status"] = found is null
                    ? "gone"
                    : found.Value.Member("agent_status") is { IsTruthy: true } st ? st : "unknown";
            }

            result.Add(row);
        }

        return result;
    }

    /// <summary><c>a === b</c> for two values read from JSON; two missing values are equal.</summary>
    private static bool Same(JsonValue? a, JsonValue? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        var x = a.Value;
        var y = b.Value;
        if (x.IsNull || y.IsNull)
        {
            return x.IsNull && y.IsNull;
        }

        return x.IsText && y.IsText ? x.AsString() == y.AsString()
            : x.IsNumber && y.IsNumber ? x.AsNumber() == y.AsNumber()
            : x.IsBool && y.IsBool && x.AsBool() == y.AsBool();
    }

    public JsonObject StreamFacts(JsonValue record, JsonArray members, JsonArray statuses, JsonArray exchanges, JsonArray? agents, Func<string, bool> pathExists)
    {
        var memberNames = members.Select(m => m.Member("name")).Where(n => n is { IsText: true }).Select(n => n!.Value.AsString()).ToHashSet(StringComparer.Ordinal);
        bool Names(JsonValue e, string field) => e.Member(field) is { IsText: true } t && memberNames.Contains(t.AsString());
        var mine = exchanges.Where(e => Names(e, "to_name") || Names(e, "from_name")).ToList();

        var facts = new JsonObject
        {
            ["branch"] = NullOr(record.Member("branch")),
            ["base"] = NullOr(record.Member("base")),
        };

        var worktree = record.Member("worktree");
        facts["worktree"] = worktree is { IsTruthy: true } w
            ? new JsonObject { ["path"] = w, ["exists"] = w.IsText && pathExists(w.AsString()) }
            : JsonValue.Null;
        if (record.Member("opened") is { } opened)
        {
            facts["opened"] = opened;
        }

        if (record.Member("closed") is { IsTruthy: true } closed)
        {
            facts["closed"] = closed;
        }

        var recordTab = record.Member("tab_id");
        var peers = new JsonArray();
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var pane = m.Member("transport_id");
            JsonValue? agent = null;
            if (agents is not null)
            {
                foreach (var x in agents)
                {
                    if (Same(x.Member("pane_id"), pane))
                    {
                        agent = x;
                        break;
                    }
                }
            }

            JsonValue tabId;
            if (agent is { } a)
            {
                tabId = a.Member("tab_id") ?? JsonValue.Null;
            }
            else
            {
                tabId = m.Member("tab_id") is { IsTruthy: true } own ? own : JsonValue.Null;
            }

            var peer = new JsonObject();
            if (m.Member("name") is { } n)
            {
                peer["name"] = n;
            }

            if (m.Member("role") is { } role)
            {
                peer["role"] = role;
            }

            if (statuses[i].Member("status") is { } status)
            {
                peer["status"] = status;
            }

            peer["pane_id"] = pane is { IsTruthy: true } p ? p : JsonValue.Null;
            peer["tab_id"] = tabId;
            peer["in_stream_tab"] = recordTab is { IsTruthy: true } && Same(tabId, recordTab);
            peers.Add(peer);
        }

        facts["peers"] = peers;
        var rows = new JsonArray();
        var waiting = new JsonArray();
        foreach (var e in mine)
        {
            var row = new JsonObject();
            foreach (var key in new[] { "id", "slug", "to_name", "from_name", "age_sec" })
            {
                if (e.Member(key) is { } v)
                {
                    row[key] = v;
                }
            }

            rows.Add(row);
            var wait = new JsonObject();
            if (e.Member("to_name") is { } on)
            {
                wait["on"] = on;
            }

            if (e.Member("id") is { } id)
            {
                wait["request"] = id;
            }

            if (e.Member("age_sec") is { } age)
            {
                wait["age_sec"] = age;
            }

            waiting.Add(wait);
        }

        facts["exchanges"] = rows;
        facts["waiting_on"] = waiting;
        return facts;
    }

    private static JsonValue NullOr(JsonValue? v) => v is null || v.Value.IsNull ? JsonValue.Null : v.Value;

    /// <summary><paramref name="text"/> made safe to display (escape sequences, control and bidi characters removed), cut to <paramref name="cap"/> code points.</summary>
    public string Clean(string text, int cap) => HierLib.CleanValue(JsonValue.FromString(text), JsonValue.FromNumber(cap)) ?? string.Empty;

    /// <summary>The row of one open exchange: its id, slug, who it is to and from, and its age in seconds (and, when asked for, when it was created).</summary>
    public JsonObject OpenExchangeRow(OpenExchange exchange, JsonObject frontMatter, double ageSeconds, bool created)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentNullException.ThrowIfNull(frontMatter);
        var row = new JsonObject
        {
            ["id"] = exchange.Id,
            ["slug"] = exchange.Slug is { Length: > 0 } slug ? slug
                : frontMatter.Get("slug") is { IsTruthy: true } fs ? fs : JsonValue.Null,
            ["to_name"] = frontMatter.Get("to_name") is { IsTruthy: true } to ? to : JsonValue.Null,
            ["from_name"] = frontMatter.Get("from_name") is { IsTruthy: true } from ? from : JsonValue.Null,
            ["age_sec"] = Math.Round(ageSeconds, MidpointRounding.AwayFromZero),
        };
        if (created)
        {
            row["created"] = frontMatter.Get("created") is { IsTruthy: true } c ? c : JsonValue.Null;
        }

        return row;
    }
}
