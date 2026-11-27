using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// ah2-698 D6: a role pack's updates are reported, never blocking. One check, run by <c>role list</c>, doctor and <c>spawn-one</c>
/// (never by resolve), compares each pack the resolved roles use with <c>~/.claude/ah2/packs-seen.json</c>. A changed pack shifts its
/// record and, under a team, sends one <c>pack-updated</c> mail to that team's Orchestrator. First sight writes the record and sends nothing.
/// </summary>
internal static class PackUpdates
{
    /// <summary>Checks the packs <paramref name="resolved"/>'s roles use and returns each one's <see cref="Updated"/> view, keyed <c>plugin@marketplace</c>.</summary>
    internal static Dictionary<string, JsonValue> Check(RosterContext c, JsonObject resolved)
    {
        var views = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var key in PacksOf(c, resolved))
        {
            try
            {
                views[key] = CheckOne(c, key);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // it reports and blocks nothing: an unreadable, malformed or unwritable record leaves the pack unreported
                views[key] = JsonValue.Null;
            }
        }

        return views;
    }

    /// <summary><c>plugin@marketplace</c> of a role's <c>from</c>, or null.</summary>
    internal static string? PackKeyOf(JsonValue? entry) =>
        entry?.Member("pack") is { IsTruthy: true } && entry.Value.Member("from") is { IsText: true } f && f.AsString().LastIndexOf(':') is > 0 and var cut
            ? f.AsString()[..cut]
            : null;

    /// <summary>The record's <c>updated</c> view (<c>{at, from_version, to_version, files, roles}</c> as counts), or null with no <c>previous</c>.</summary>
    internal static JsonValue Updated(JsonValue? record)
    {
        if (record?.Member("previous") is not { IsMap: true } previous)
        {
            return JsonValue.Null;
        }

        var (files, roles) = Diff(previous, record.Value);
        return new JsonObject
        {
            ["at"] = record.Value.Member("seen_at") ?? JsonValue.Null,
            ["from_version"] = previous.Member("version") ?? JsonValue.Null,
            ["to_version"] = record.Value.Member("version") ?? JsonValue.Null,
            ["files"] = Counts(files),
            ["roles"] = Counts(roles),
        };
    }

    /// <summary>The record of <paramref name="key"/> in the packs-seen file, or null.</summary>
    internal static JsonValue? RecordOf(RosterContext c, string key)
    {
        try
        {
            return Read(c).Get(key);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary><c>updated &lt;date&gt;: files +a −r ~c, roles ~z</c> for an <see cref="Updated"/> view.</summary>
    internal static string Line(JsonValue updated)
    {
        int N(string group, string kind) => updated.Member(group)?.Member(kind) is { IsNumber: true } n ? (int)n.AsNumber() : 0;
        var at = MemberPlanning.JsConvertText(updated.Member("at"));
        return $"updated {(at.Length >= 10 ? at[..10] : at)}: files +{N("files", "added")} −{N("files", "removed")} ~{N("files", "changed")}, roles ~{N("roles", "changed")}";
    }

    private static List<string> PacksOf(RosterContext c, JsonObject resolved)
    {
        var config = c.Services.Config;
        var keys = new List<string>();
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        var roles = resolved.Get("roles") is { IsMap: true } r ? r.AsObject() : new JsonObject();
        foreach (var role in config.RegistryRoles(resolved))
        {
            var entry = roles.Get(role);
            if (PackKeyOf(entry) is not { } key)
            {
                continue;
            }

            if (entry!.Value.Member("pack")?.Member("status") is { IsText: true } s && s.AsString() == "pack-ambiguous")
            {
                ambiguous.Add(key);
            }

            if (!keys.Contains(key))
            {
                keys.Add(key);
            }
        }

        return keys.Where(k => !ambiguous.Contains(k)).ToList();
    }

    private static JsonValue CheckOne(RosterContext c, string key)
    {
        var config = c.Services.Config;
        var at = key.IndexOf('@', StringComparison.Ordinal);
        var plugin = key[..at];
        var marketplace = key[(at + 1)..];
        var record = config.PackRecordList().Where(r => MemberPlanning.JsConvertText(r.Member("plugin")) == plugin).Where(r => r.Member("marketplace") is { IsText: true } m && m.AsString() == marketplace).ToList();
        if (record.Count != 1)
        {
            return JsonValue.Null;
        }

        var installPath = MemberPlanning.JsConvertText(record[0].Member("installPath"));
        var tree = config.PackTreeJson(installPath);
        if (tree.Get("error") is { IsTruthy: true } || tree.Get("files") is not { IsArray: true } files)
        {
            return JsonValue.Null;
        }

        var digest = config.PackDigest(files.AsArray());
        var seen = Read(c);
        var old = seen.Get(key);
        if (old is { IsMap: true } o && o.Member("digest") is { IsText: true } d && d.AsString() == digest)
        {
            return Updated(o);
        }

        var manifest = config.ReadPackManifest(installPath, plugin);
        var roles = new JsonObject();
        if (manifest.Get("roles") is { IsMap: true } rm)
        {
            foreach (var role in rm.AsObject().Keys)
            {
                roles[role] = rm.AsObject()[role].Member("raw") ?? JsonValue.Null;
            }
        }

        // the writer sends the mail: re-read, and shift only when the record is still the one this check compared against
        seen = Read(c);
        var current = seen.Get(key);
        if (!JsonEquals(current, old))
        {
            return Updated(current);
        }

        var next = new JsonObject
        {
            ["digest"] = digest,
            ["version"] = PackVerbs.PluginVersionAt(c, installPath, record[0].Member("version")),
            ["files"] = files,
            ["roles"] = roles,
            ["seen_at"] = c.Services.Clock.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture),
            ["previous"] = old is { IsMap: true } prev
                ? new JsonObject
                {
                    ["digest"] = prev.Member("digest") ?? JsonValue.Null,
                    ["version"] = prev.Member("version") ?? JsonValue.Null,
                    ["files"] = prev.Member("files") ?? JsonValue.Null,
                    ["roles"] = prev.Member("roles") ?? JsonValue.Null,
                    ["seen_at"] = prev.Member("seen_at") ?? JsonValue.Null,
                }
                : JsonValue.Null,
        };
        // ponytail: the re-read guards only this key, so two concurrent checks of different packs can lose one shift; the next run
        // re-detects it and the deterministic id dedupes the mail. Merge per key under a lock if a delayed report ever matters.
        seen[key] = next;
        LevelFiles.WriteAtomically(c.Services.Config.PacksSeenPath(), JsonText.Stringify(JsonValue.FromObject(seen), 2) + "\n");
        if (old is { IsMap: true })
        {
            Mail(c, key, next);
        }

        return Updated(next);
    }

    private static void Mail(RosterContext c, string key, JsonObject record)
    {
        if (c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile).IsNull)
        {
            return;
        }

        var previous = record["previous"];
        var (files, roles) = Diff(previous, record);
        var names = roles.Added.Concat(roles.Removed).Concat(roles.Changed).ToList();
        var digest = record["digest"].AsString();
        var hex = digest[(digest.IndexOf(':', StringComparison.Ordinal) + 1)..];
        var text = $"role pack {key} updated {MemberPlanning.JsConvertText(previous.Member("version"))}→{MemberPlanning.JsConvertText(record["version"])}: "
            + $"files +{files.Added.Count} −{files.Removed.Count} ~{files.Changed.Count}; roles +{roles.Added.Count} −{roles.Removed.Count} ~{roles.Changed.Count}"
            + $"{(names.Count > 0 ? $" ({string.Join(", ", names)})" : string.Empty)}. It takes effect at each member's next start; nothing was paused.";
        var data = new JsonObject
        {
            ["pack"] = key,
            ["from_version"] = previous.Member("version") ?? JsonValue.Null,
            ["to_version"] = record["version"],
            ["files"] = Lists(files),
            ["roles"] = Lists(roles),
        };
        var recipient = $"orchestrator@{c.ProgressTeam}";
        try
        {
            _ = c.Services.EventMail?.SendEvent(c.Cwd, c.Dir, $"pack-updated:{key}:{hex[..Math.Min(12, hex.Length)]}", "pack-updated", text, data, [recipient]);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.Stderr.Write($"note: {recipient} not told that role pack {key} was updated\n");
        }
    }

    private sealed record Changes(List<string> Added, List<string> Removed, List<string> Changed);

    private static (Changes Files, Changes Roles) Diff(JsonValue previous, JsonValue current)
    {
        static Dictionary<string, JsonValue> FileMap(JsonValue? v) =>
            v is { IsArray: true } a
                ? a.AsArray().Where(x => x.IsArray && x.AsArray().Count == 2).ToDictionary(x => x.AsArray()[0].AsString(), x => x.AsArray()[1], StringComparer.Ordinal)
                : new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        static Dictionary<string, JsonValue> RoleMap(JsonValue? v) =>
            v is { IsMap: true } m ? m.AsObject().Keys.ToDictionary(k => k, k => m.AsObject()[k], StringComparer.Ordinal) : new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        static Changes Compare(Dictionary<string, JsonValue> before, Dictionary<string, JsonValue> after) => new(
            after.Keys.Where(k => !before.ContainsKey(k)).Order(StringComparer.Ordinal).ToList(),
            before.Keys.Where(k => !after.ContainsKey(k)).Order(StringComparer.Ordinal).ToList(),
            after.Keys.Where(k => before.TryGetValue(k, out var b) && !JsonEquals(b, after[k])).Order(StringComparer.Ordinal).ToList());

        return (Compare(FileMap(previous.Member("files")), FileMap(current.Member("files"))),
            Compare(RoleMap(previous.Member("roles")), RoleMap(current.Member("roles"))));
    }

    private static JsonObject Counts(Changes x) => new() { ["added"] = x.Added.Count, ["removed"] = x.Removed.Count, ["changed"] = x.Changed.Count };

    private static JsonObject Lists(Changes x) => new()
    {
        ["added"] = ConfigStrings(x.Added),
        ["removed"] = ConfigStrings(x.Removed),
        ["changed"] = ConfigStrings(x.Changed),
    };

    private static JsonArray ConfigStrings(IEnumerable<string> items)
    {
        var a = new JsonArray();
        foreach (var i in items)
        {
            a.Add(i);
        }

        return a;
    }

    private static bool JsonEquals(JsonValue? a, JsonValue? b) =>
        (a is null || a.Value.IsNull) == (b is null || b.Value.IsNull) && (a is null || a.Value.IsNull || JsonText.Stringify(a.Value, 0) == JsonText.Stringify(b!.Value, 0));

    private static JsonObject Read(RosterContext c)
    {
        var path = c.Services.Config.PacksSeenPath();
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        var parsed = JsonText.Parse(File.ReadAllText(path));
        return parsed.IsMap ? parsed.AsObject() : new JsonObject();
    }
}
