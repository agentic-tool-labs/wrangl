using Ah.Core.Json;

namespace Ah.Config;

internal sealed partial class ConfigCore
{
    private static bool IsObject(JsonValue? v) => v is { IsMap: true };

    private static JsonArray Strings(IEnumerable<string?> values)
    {
        var array = new JsonArray();
        foreach (var s in values)
        {
            array.Add(s is null ? JsonValue.Null : s);
        }

        return array;
    }

    // ---------------------------------------------------------------- roster blocks

    private static JsonValue? PickTeamRoster(JsonObject data, string key)
    {
        var map = data.Get("rosters");
        if (!IsObject(map))
        {
            return null;
        }

        return map!.Value.AsObject().Get(key) ?? JsonValue.Null;
    }

    /// <summary>Every roster block of a level file as <c>(label, key, block)</c>: the default <c>roster</c> (key null), then each <c>rosters.&lt;key&gt;</c>.</summary>
    private static List<(string Label, string? Key, JsonValue? Block)> RosterBlockTuples(JsonObject data)
    {
        var result = new List<(string, string?, JsonValue?)> { ("roster", null, data.Get("roster")) };
        if (IsObject(data.Get("rosters")))
        {
            var map = data.Get("rosters")!.Value.AsObject();
            foreach (var key in map.Keys)
            {
                result.Add(($"rosters.{key}", key, map[key]));
            }
        }

        return result;
    }

    /// <summary>
    /// Drops, in place, every roster <c>members</c> element that is not a plain object, and returns what it dropped as
    /// <c>{label, index, type, value}</c>.
    /// </summary>
    internal static JsonArray DropNonObjectMembersCore(JsonObject data)
    {
        var dropped = new JsonArray();
        foreach (var (label, _, block) in RosterBlockTuples(data))
        {
            if (!IsObject(block) || block!.Value.AsObject().Get("members") is not { IsArray: true } membersValue)
            {
                continue;
            }

            var members = membersValue.AsArray();
            var kept = new JsonArray();
            for (var index = 0; index < members.Count; index++)
            {
                var m = members[index];
                if (m.IsMap)
                {
                    kept.Add(m);
                }
                else
                {
                    var type = m.IsNull ? "null" : m.IsArray ? "array" : m.IsText ? "string" : m.IsNumber ? "number" : "boolean";
                    dropped.Add(new JsonObject { ["label"] = label, ["index"] = index, ["type"] = type, ["value"] = m });
                }
            }

            if (kept.Count != members.Count)
            {
                block.Value.AsObject()["members"] = kept;
            }
        }

        return dropped;
    }

    /// <summary>
    /// A roster block as it reads while the subagent opt-ins written before are ignored: only legwork runs as a
    /// subagent, and <c>onMissing</c> is always auto.
    /// </summary>
    internal static JsonObject NormalizeRosterBlockCore(JsonObject block, JsonObject? registry)
    {
        var staleBlock = block.Get("route") is { IsText: true } r && r.AsString() == "subagent";
        var members = new JsonArray();
        if (block.Get("members") is { IsArray: true } list)
        {
            foreach (var m in list.AsArray())
            {
                var cls = m.IsMap && m.Member("role") is { IsText: true } role ? ConfigRegistry.RoleClass(role.AsString(), registry) : null;
                if (cls is null)
                {
                    members.Add(m);
                    continue;
                }

                var copy = m.AsObject().Clone();
                if (copy.Get("onMissing") is { IsText: true } onMissing && onMissing.AsString() is "never" or "prompt")
                {
                    copy.Remove("onMissing");
                }

                if (cls == "legwork")
                {
                    if (staleBlock && copy.Get("route") is null)
                    {
                        copy["route"] = "subagent";
                    }
                }
                else if (copy.Get("route") is { IsText: true } route && route.AsString() == "subagent")
                {
                    copy.Remove("route");
                }

                members.Add(copy);
            }
        }

        var result = block.Clone();
        if (staleBlock)
        {
            result["route"] = "peer";
        }

        result["members"] = members;
        return result;
    }

    /// <summary>Each member's dispatch name: the first of a role gets <c>peerName(repoBasename, role)</c>, later ones <c>-2</c>, <c>-3</c>, and a resolved <c>kind</c>.</summary>
    internal static JsonArray RosterMemberNamesCore(JsonArray members, string repoBasename)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new JsonArray();
        foreach (var m in members)
        {
            var role = m.Member("role");
            var roleText = JsConvert.ToJsString(role);
            seen[roleText] = seen.GetValueOrDefault(roleText) + 1;
            var ordinal = seen[roleText];
            var copy = m.AsObject().Clone();
            var kind = m.Member("kind");
            copy["kind"] = kind is null || kind.Value.IsNull ? ConfigRegistry.KindDefault : kind.Value;
            copy["name"] = Ah.Core.Text.MemberNames.Fit(repoBasename, roleText, ordinal);
            result.Add(copy);
        }

        return result;
    }

    /// <summary>
    /// <c>resolveRoster</c>: repo-user, repo, global; the first level whose block is present with at least one member
    /// wins in its entirety. Returns <c>{level, route, members, path, teamKey}</c> or null.
    /// </summary>
    internal JsonObject? ResolveRosterObject(string? cwd, string? rosterKey, string? namingPrefix, JsonObject? registry)
    {
        var candidates = RosterLevelCandidateLists(cwd);
        var prefix = !string.IsNullOrEmpty(namingPrefix) ? namingPrefix : TeamPrefix(cwd, null);
        var passes = new List<string?>();
        if (!string.IsNullOrEmpty(rosterKey))
        {
            passes.Add(rosterKey);
        }

        passes.Add(null);
        foreach (var teamKey in passes)
        {
            foreach (var level in ConfigRegistry.RosterLevels)
            {
                foreach (var path in candidates[level])
                {
                    if (!files.Exists(path) || TryReadJson(path) is not { IsMap: true } doc)
                    {
                        continue;
                    }

                    var data = doc.AsObject();
                    DropNonObjectMembersCore(data);
                    var r = teamKey is not null ? PickTeamRoster(data, teamKey) : data.Get("roster");
                    if (r is not { IsMap: true } rv || rv.AsObject().Get("members") is not { IsArray: true } rm || rm.AsArray().Count == 0)
                    {
                        continue;
                    }

                    var block = NormalizeRosterBlockCore(rv.AsObject(), registry);
                    var result = new JsonObject { ["level"] = level };
                    if (block.Get("route") is { } route)
                    {
                        result["route"] = route;
                    }

                    if (rv.AsObject().Get("packs") is { IsArray: true } packs)
                    {
                        result["packs"] = packs;
                    }

                    result["members"] = RosterMemberNamesCore(block["members"].AsArray(), prefix);
                    result["path"] = path;
                    result["teamKey"] = teamKey is null ? JsonValue.Null : teamKey;
                    if (rv.AsObject().Get("max_lanes") is { IsNull: false } maxLanes)
                    {
                        result["max_lanes"] = maxLanes;
                    }

                    if (rv.AsObject().Get("orchestrator_model") is { IsNull: false } orchestratorModel
                        && !(orchestratorModel.IsText && orchestratorModel.AsString().Length == 0))
                    {
                        result["orchestrator_model"] = orchestratorModel;
                    }

                    return result;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The <c>packs</c> of roster block <paramref name="rosterKey"/> (null: the default block) and the level file it is read from: the
    /// most specific level whose block has members, else the most specific that defines the block (ah2-698 D1). Null when no level defines it.
    /// </summary>
    internal (JsonArray? Packs, string Path)? RosterPacksTuple(string? cwd, string? rosterKey)
    {
        (JsonArray? Packs, string Path)? first = null;
        foreach (var (_, path, data) in ExistingLevelFiles(cwd))
        {
            DropNonObjectMembersCore(data);
            var block = rosterKey is not null ? PickTeamRoster(data, rosterKey) : data.Get("roster");
            if (block is not { IsMap: true } bv)
            {
                continue;
            }

            var packs = bv.AsObject().Get("packs") is { IsArray: true } p ? p.AsArray() : null;
            if (bv.AsObject().Get("members") is { IsArray: true } m && m.AsArray().Count > 0)
            {
                return (packs, path);
            }

            first ??= (packs, path);
        }

        return first;
    }

    private IEnumerable<(string Level, string Path, JsonObject Data)> ExistingLevelFiles(string? cwd)
    {
        var candidates = RosterLevelCandidateLists(cwd);
        foreach (var level in ConfigRegistry.RosterLevels)
        {
            foreach (var path in candidates[level])
            {
                if (!files.Exists(path) || TryReadJson(path) is not { IsMap: true } doc)
                {
                    continue;
                }

                yield return (level, path, doc.AsObject());
            }
        }
    }

    /// <summary>The <c>rosters.*</c> keys a create can be pointed at, sorted, de-duplicated and valid as team names.</summary>
    internal List<string> NamedRosterKeyList(string? cwd)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, _, data) in ExistingLevelFiles(cwd))
        {
            foreach (var (_, key, _) in RosterBlockTuples(data))
            {
                if (key is not null && ConfigRegistry.IsValidTeamAlias(key))
                {
                    keys.Add(key);
                }
            }
        }

        var sorted = keys.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    internal List<string> SelectableRosterList(string? cwd) => NamedRosterKeyList(cwd).Where(k => k != ConfigRegistry.DefaultRoster).ToList();

    /// <summary>The first <c>activeRoster</c> set, most specific level first, as <c>(value, level, path)</c>.</summary>
    internal (JsonValue Value, string Level, string Path)? ActiveRosterSettingTuple(string? cwd)
    {
        foreach (var (level, path, data) in ExistingLevelFiles(cwd))
        {
            if (data.Get("activeRoster") is { IsNull: false } value)
            {
                return (value, level, path);
            }
        }

        return null;
    }

    /// <summary>
    /// Which roster block a command uses and why. The first match wins: the flag; the key the team's file records;
    /// a non-empty <c>WRANGL_ROSTER</c>; <c>activeRoster</c>; the default block.
    /// </summary>
    internal JsonObject RosterSelectionFor(string? cwd, JsonValue? flag, (string Dir, string? Name)? team)
    {
        JsonObject Chosen(JsonValue value, string source, string? level = null, string? path = null)
        {
            JsonValue key = value.IsText && value.AsString() == ConfigRegistry.DefaultRoster ? JsonValue.Null : value;
            return new JsonObject
            {
                ["roster"] = key.IsNull ? ConfigRegistry.DefaultRoster : key,
                ["key"] = key,
                ["source"] = source,
                ["level"] = level is null ? JsonValue.Null : level,
                ["path"] = path is null ? JsonValue.Null : path,
            };
        }

        if (flag is { IsText: true } f && f.AsString().Length > 0)
        {
            return Chosen(f, "flag");
        }

        if (team is not null)
        {
            var recorded = Teams.TeamRosterEntry(team.Value.Dir, team.Value.Name);
            if (recorded is not null)
            {
                JsonValue key = recorded.Key is null ? JsonValue.Null : recorded.Key;
                return new JsonObject
                {
                    ["roster"] = recorded.Key is null ? ConfigRegistry.DefaultRoster : recorded.Key,
                    ["key"] = key,
                    ["source"] = "team",
                    ["level"] = JsonValue.Null,
                    ["path"] = JsonValue.Null,
                };
            }
        }

        var env = environment.GetVariable("WRANGL_ROSTER");
        if (!string.IsNullOrEmpty(env))
        {
            return Chosen(env, "env");
        }

        if (ActiveRosterSettingTuple(cwd) is { } active)
        {
            return Chosen(active.Value, "activeRoster", active.Level, active.Path);
        }

        return Chosen(ConfigRegistry.DefaultRoster, "default");
    }

    internal string? RosterSelectionProblemText(string? cwd, JsonObject selection)
    {
        var source = selection["source"].AsString();
        var key = selection["key"];
        if (source is not ("flag" or "env" or "activeRoster") || key.IsNull)
        {
            return null;
        }

        var level = JsConvert.ToJsString(selection["level"]);
        var path = JsConvert.ToJsString(selection["path"]);
        if (!key.IsText || key.AsString().Length == 0)
        {
            return $"activeRoster at {level} in {path} must be a roster name, got {JsonText.Stringify(key, 0)}. "
                + $"Fix with `ah roster roster use <name> --level {level}` or `ah roster roster use --clear --level {level}`.";
        }

        var defined = SelectableRosterList(cwd);
        var name = key.AsString();
        if (defined.Contains(name))
        {
            return null;
        }

        var from = source == "flag" ? "--roster" : source == "env" ? "WRANGL_ROSTER" : $"activeRoster at {level} in {path}";
        var list = defined.Count == 0 ? "none" : string.Join(", ", defined);
        return $"{from} selects roster \"{name}\", but there is no rosters.{name} block at any level (defined: {list}). "
            + $"Fix with `ah roster roster use default`, `ah roster roster use <other>`, or `ah roster init --roster {name}`.";
    }
}
