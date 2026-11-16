using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Commands;

/// <summary>
/// What <c>whoami</c>, <c>teams</c> and <c>doctor</c> say about the worktree gate: read from the team's grants document, the
/// gate's audit log and the hook-error log. A team with no document has nothing to report: every reader here returns null.
/// </summary>
internal static class GateReport
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    internal static string Key(string? team) => team ?? "team";

    /// <summary>The team's grants document as <c>status</c> reports it (with every member's record), or null when there is none.</summary>
    internal static JsonObject? Read(RosterContext c, string dir, string? team)
    {
        try
        {
            var home = c.Services.Roster.TeamHome(dir);
            var status = c.Services.Hierarchy.StateCalls.Grants("status", new JsonObject { ["dir"] = home, ["team"] = Key(team) });
            return status.IsMap && status.Member("present") is { Kind: JsonKind.Bool, IsTruthy: true } ? status.AsObject() : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    internal static JsonObject? MemberOf(JsonObject status, string member) =>
        status.Get("members") is { IsMap: true } members && members.AsObject().Get(member) is { IsMap: true } m ? m.AsObject() : null;

    /// <summary>Whether <paramref name="root"/> is no longer a work tree: gone, not a directory, or without its own <c>.git</c> entry.</summary>
    internal static bool MovedOrMissing(RosterContext c, string root)
    {
        var files = c.Services.Files;
        return files.Stat(root) is not { Kind: FileKind.Directory } || files.Stat(NodePath.Join(root, ".git"), followSymlinks: false) is null;
    }

    /// <summary>The alias of a recorded tmp: whether it exists, and whether it still resolves to the recorded directory.</summary>
    internal static (bool Exists, bool Ok) AliasState(RosterContext c, string alias, string root)
    {
        if (alias.Length == 0 || c.Services.Files.Stat(alias, followSymlinks: false) is not { Kind: FileKind.Symlink })
        {
            return (false, false);
        }

        return (true, c.Services.Files.ReadLink(alias) == root);
    }

    /// <summary>
    /// The stream a member is attached to now: the team file's, which <c>move</c> keeps current, else <paramref name="recorded"/>, the
    /// stream the grants document stored when the member was assigned and which is display-only.
    /// </summary>
    internal static JsonValue CurrentStream(RosterContext c, string dir, string? team, string member, JsonValue? recorded)
    {
        if (c.Services.Roster.ReadTeamRecord(dir, team).Member("members") is { IsArray: true } rows)
        {
            foreach (var row in rows.AsArray())
            {
                if (row.Member("name") is { IsText: true } name && name.AsString() == member && row.Member("stream") is { IsText: true } stream)
                {
                    return stream;
                }
            }
        }

        return recorded ?? JsonValue.Null;
    }

    /// <summary>The <c>work_tree</c> object of <c>whoami</c> for <paramref name="member"/>; null when the team has no document.</summary>
    internal static JsonValue WorkTree(RosterContext c, string home, string? team, string member)
    {
        if (Read(c, home, team) is not { } status)
        {
            return JsonValue.Null;
        }

        var record = MemberOf(status, member);
        if (record is null || record.Get("work_root") is not { IsText: true } root)
        {
            return JsonValue.Null;
        }

        var extras = new JsonArray();
        if (record.Get("extras") is { IsArray: true } list)
        {
            foreach (var x in list.AsArray())
            {
                extras.Add(new JsonObject
                {
                    ["path"] = x.Member("path") ?? JsonValue.Null,
                    ["note"] = x.Member("note") ?? JsonValue.Null,
                    ["for"] = x.Member("for") ?? JsonValue.Null,
                    ["outside"] = x.Member("outside") ?? JsonValue.Null,
                });
            }
        }

        JsonValue tmpOut = JsonValue.Null;
        if (record.Get("tmp") is { IsMap: true } tmp && tmp.Member("root") is { IsText: true } tmpRoot)
        {
            var alias = tmp.Member("alias") is { IsText: true } a ? a.AsString() : string.Empty;
            tmpOut = new JsonObject
            {
                ["root"] = tmpRoot,
                ["alias"] = alias.Length == 0 ? JsonValue.Null : JsonValue.FromString(alias),
                ["alias_ok"] = AliasState(c, alias, tmpRoot.AsString()).Ok,
            };
        }

        return new JsonObject
        {
            ["root"] = root,
            ["stream"] = CurrentStream(c, home, team, member, record.Get("stream")),
            ["mode"] = status.Get("mode") ?? JsonValue.Null,
            ["moved_or_missing"] = MovedOrMissing(c, root.AsString()),
            ["extras"] = extras,
            ["tmp"] = tmpOut,
        };
    }

    /// <summary>The <c>gate</c> object of <c>teams</c> for <paramref name="team"/>; null when the team has no document.</summary>
    internal static JsonValue Gate(RosterContext c, string dir, string? team)
    {
        if (Read(c, dir, team) is not { } status)
        {
            return JsonValue.Null;
        }

        var members = new JsonArray();
        if (status.Get("members") is { IsMap: true } map)
        {
            foreach (var name in map.AsObject().Keys.Order(StringComparer.Ordinal))
            {
                var m = map.AsObject()[name];
                members.Add(new JsonObject
                {
                    ["name"] = name,
                    ["work_root"] = m.Member("work_root") ?? JsonValue.Null,
                    ["stream"] = CurrentStream(c, dir, team, name, m.Member("stream")),
                    ["extras"] = m.Member("extras") is { IsArray: true } e ? e.AsArray().Count : 0,
                    ["tmp_root"] = m.Member("tmp") is { IsMap: true } t ? t.Member("root") ?? JsonValue.Null : JsonValue.Null,
                });
            }
        }

        var wouldRefuse = new JsonObject();
        var deny = 0;
        foreach (var row in AuditRows(c).Where(r => TextOf(r, "team") == Key(team) && TextOf(r, "action") == "write"))
        {
            switch (TextOf(row, "result"))
            {
                case "would_refuse":
                    var reason = TextOf(row, "reason") ?? "unknown";
                    wouldRefuse[reason] = JsonValue.FromNumber((wouldRefuse.Get(reason) is { IsNumber: true } n ? n.AsNumber() : 0) + 1);
                    break;
                case "deny":
                    deny++;
                    break;
            }
        }

        return new JsonObject
        {
            ["mode"] = status.Get("mode") ?? JsonValue.Null,
            ["members"] = members,
            ["would_refuse_24h"] = wouldRefuse,
            ["deny_24h"] = deny,
        };
    }

    /// <summary>The gate's audit rows of the last 24 hours, oldest first; none when the log is absent or unreadable.</summary>
    /// The rows come from the grants tables, then the old audit file (per-dir-files-to-db S11e), when the services carry the grants source.
    internal static List<JsonObject> AuditRows(RosterContext c) =>
        c.Services.Grants is { } grants
            ? RowsIn(c, grants.AuditRows(), "at")
            : RowsOf(c, AhPaths.UserEntry(c.Services.Environment.HomeDirectory, AhPaths.WorktreeAuditFile), "at");

    private static List<JsonObject> RowsOf(RosterContext c, string path, string timeKey)
    {
        try
        {
            return c.Services.Files.Exists(path) ? RowsIn(c, c.Services.Files.ReadText(path).Split('\n'), timeKey) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static List<JsonObject> RowsIn(RosterContext c, IEnumerable<string> lines, string timeKey)
    {
        var rows = new List<JsonObject>();
        var now = c.Services.Clock.UtcNow.ToUnixTimeMilliseconds();
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                if (JsonText.Parse(line) is { IsMap: true } row
                    && TextOf(row.AsObject(), timeKey) is { } at
                    && Timestamps.TryParseMs(at, out var ms)
                    && now - ms <= Window.TotalMilliseconds)
                {
                    rows.Add(row.AsObject());
                }
            }
            catch (JsonParseException)
            {
                // a torn or foreign line carries no row
            }
        }

        return rows;
    }

    private static string? TextOf(JsonObject row, string key) => row.Get(key) is { IsText: true } t ? t.AsString() : null;
}
