using Ah.Core.Json;

namespace Ah.Commands;

internal static class HistoryVerb
{
    /// <summary>Read-only inventory of the stored roster configs, for <c>create --from</c>.</summary>
    internal static int Run(RosterContext c)
    {
        var dir = c.Dir;
        var history = c.Services.Roster.ReadHistory(dir);
        var now = c.Services.Clock.UtcNow;
        var teams = new JsonArray();
        if (history.Get("teams") is { IsArray: true } list)
        {
            foreach (var entry in list.AsArray())
            {
                teams.Add(Describe(c, dir, entry, now));
            }
        }

        var result = new JsonObject { ["teams"] = teams };
        c.Out(result);
        return 0;
    }

    private static JsonValue Describe(RosterContext c, string dir, JsonValue entry, DateTimeOffset now)
    {
        var members = RosterJson.Member(entry, "members");
        var roles = new JsonArray();
        var seen = new List<JsonValue>();
        if (members is { IsTruthy: true } m)
        {
            if (!m.IsArray)
            {
                throw new RosterFailure("(e.members || []).map is not a function");
            }

            foreach (var member in m.AsArray())
            {
                if (member.IsNull || !member.IsMap && !member.IsText && !member.IsNumber && !member.IsBool && !member.IsArray)
                {
                    throw new RosterFailure("Cannot read properties of null (reading 'role')");
                }

                var role = member.Member("role") ?? JsonValue.Null;
                if (!seen.Exists(s => SameValue(s, role)))
                {
                    seen.Add(role);
                    roles.Add(role);
                }
            }
        }

        var o = new JsonObject();
        Put(o, "id", RosterJson.Member(entry, "id"));
        Put(o, "label", RosterJson.Member(entry, "label"));
        Put(o, "alias", RosterJson.Member(entry, "alias"));
        o["active"] = c.Services.Roster.HistoryEntryIsActive(dir, entry, now);
        Put(o, "last_used", RosterJson.Member(entry, "last_used"));
        Put(o, "created_at", RosterJson.Member(entry, "created_at"));
        o["roles"] = roles;
        o["member_count"] = members is { IsArray: true } list ? list.AsArray().Count : 0;
        Put(o, "roster_level", RosterJson.Member(entry, "roster_level"));
        Put(o, "transport", RosterJson.Member(entry, "transport"));
        return o;
    }

    /// <summary>A property whose value is <c>undefined</c> is left out, as <c>JSON.stringify</c> leaves it out.</summary>
    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }

    /// <summary>The <c>Set</c> notion of equality: same kind and same content, a missing role (undefined) alike.</summary>
    private static bool SameValue(JsonValue a, JsonValue b) =>
        a.Kind == b.Kind && a.Kind switch
        {
            JsonKind.Null => true,
            JsonKind.Bool => a.AsBool() == b.AsBool(),
            JsonKind.Number => a.AsNumber().Equals(b.AsNumber()),
            JsonKind.Text => a.AsString() == b.AsString(),
            _ => false,
        };
}
