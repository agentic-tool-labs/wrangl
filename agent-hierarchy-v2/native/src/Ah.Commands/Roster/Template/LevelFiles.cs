using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// The roster level files (<c>global</c>, <c>repo</c>, <c>repo-user</c>) as the template and config verbs read and rewrite
/// them: what a level argument means, which level a write lands in, how a file is loaded without being trusted, and the
/// one writer that also brings stale keys up to date.
/// </summary>
internal sealed class LevelFiles(RosterContext c)
{
    private readonly Dictionary<string, JsonArray> _droppedAtRead = new(StringComparer.Ordinal);
    /// <summary>The role registry for this invocation's cwd and team, read once.</summary>
    internal JsonObject Registry() => c.Registry;

    /// <summary>The explicit level: <c>--level</c>, else a bare first word that names one.</summary>
    internal string? LevelArg()
    {
        if (c.Opts.Text("level") is { } level)
        {
            return level;
        }

        var bare = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        return bare is not null && c.Services.Config.RosterLevels.Contains(bare) ? bare : null;
    }

    internal string? LevelError(string level) => c.Services.Config.RosterLevels.Contains(level)
        ? null
        : $"--level must be one of {string.Join(", ", c.Services.Config.RosterLevels)}, got {Quote(level)}";

    internal string RequireLevel(string explicitLevel) =>
        LevelError(explicitLevel) is { } error ? throw new RosterFailure(error) : explicitLevel;

    internal string PathOf(string level) => c.Services.Config.RosterLevelPaths(c.Cwd).Get(level) is { IsText: true } p
        ? p.AsString()
        : throw new RosterFailure($"no roster level path for {level}");

    /// <summary>The level a write targets: explicit, else whichever level currently resolves. <c>Error</c> is set when none can.</summary>
    internal TargetLevel ResolveTarget(bool allowMissing = false, string? key = null, bool useKey = false)
    {
        var teamKey = useKey ? key : c.RosterArg;
        var explicitLevel = LevelArg();
        if (explicitLevel is not null)
        {
            return LevelError(explicitLevel) is { } error ? new TargetLevel(null, false, teamKey, error) : new TargetLevel(explicitLevel, false, teamKey, null);
        }

        var resolved = c.Services.Config.ResolveRoster(c.Cwd, teamKey, c.RepoBasename, Registry());
        if (resolved is { IsMap: true } r && r.Member("level") is { IsText: true } level)
        {
            return new TargetLevel(level.AsString(), true, teamKey, null);
        }

        if (allowMissing && teamKey is null)
        {
            if (c.Services.Config.FindGitRoot(c.Cwd) is not null)
            {
                return new TargetLevel("repo", true, teamKey, null);
            }

            return new TargetLevel(
                null,
                false,
                teamKey,
                $"no roster resolves at any level and {c.Cwd} is not inside a git repo — re-run with --level global to create the user-wide roster (~/.claude/ah2/agent-hierarchy.json), or cd into a repo");
        }

        return new TargetLevel(null, false, teamKey, "no roster resolves at any level — run `ah roster init` first");
    }

    internal TargetLevel RequireTarget(bool allowMissing = false)
    {
        var target = ResolveTarget(allowMissing);
        return target.Error is { } error ? throw new RosterFailure(error) : target;
    }

    /// <summary>A level file's data and, when it exists but cannot be used, why; the data is then the missing-file default.</summary>
    internal (JsonObject Data, string? Problem) Load(string path)
    {
        JsonObject Missing() => new() { ["version"] = c.Services.Config.ConfigVersion };
        if (!File.Exists(path))
        {
            return (Missing(), null);
        }

        JsonValue parsed;
        try
        {
            parsed = JsonText.Parse(File.ReadAllBytes(path));
        }
        catch (JsonParseException e)
        {
            return (Missing(), $"is not valid JSON ({e.Message})");
        }

        if (!parsed.IsMap)
        {
            return (Missing(), "is not a JSON object");
        }

        var data = parsed.AsObject();
        var dropped = c.Services.Config.DropNonObjectMembers(data);
        if (dropped.Count > 0)
        {
            _droppedAtRead[path] = dropped;
        }

        return (data, null);
    }

    /// <summary>A level file's data for a verb that will write it back; a file that cannot be used is refused, never overwritten.</summary>
    internal JsonObject Read(string path)
    {
        var (data, problem) = Load(path);
        return problem is null ? data : throw new RosterFailure($"{path} {problem} — fix or delete it, then re-run; nothing was written");
    }

    /// <summary>Writes a level file, first bringing stale keys up to date and listing what changed under <c>migrated</c>.</summary>
    internal void Write(string path, JsonObject data)
    {
        var changes = Migrate(path, data);
        WriteAtomically(path, JsonText.Stringify(JsonValue.FromObject(data), 2) + "\n");
        if (changes.Count > 0)
        {
            foreach (var change in changes)
            {
                c.MigratedKeys.Add(change);
            }

            var keys = string.Join(", ", changes.Select(ch => JsConvertText(ch.Get("key"))));
            c.Stderr.Write($"ah roster: migrated {changes.Count} stale key(s) in {path}: {keys} (listed under `migrated`)\n");
        }
    }

    /// <summary>
    /// Replaces <paramref name="path"/> with <paramref name="text"/> by writing a temporary file in the same directory, flushing it
    /// to disk and renaming it over the target, so a failure at any step leaves the previous file whole and no partial file in
    /// its place. An existing file's permission bits carry over; a new one is created as an ordinary file is. A file this process
    /// could not open for writing is refused with <see cref="UnauthorizedAccessException"/> before anything is written, because
    /// the rename would otherwise replace a file whose mode forbids it.
    /// </summary>
    internal static void WriteAtomically(string path, string text) => Publish(path, new System.Text.UTF8Encoding(false).GetBytes(text), overwrite: true);

    /// <summary><see cref="WriteAtomically(string, string)"/> for bytes that are written exactly as given.</summary>
    internal static void WriteAtomically(string path, byte[] bytes) => Publish(path, bytes, overwrite: true);

    /// <summary>
    /// Creates <paramref name="path"/> with <paramref name="text"/> through a temporary file as <see cref="WriteAtomically(string, string)"/> does,
    /// and returns false, leaving whatever is there untouched, when something already exists at the path, including a file that
    /// appears between the caller's check and this call.
    /// </summary>
    internal static bool WriteNew(string path, string text) => Publish(path, new System.Text.UTF8Encoding(false).GetBytes(text), overwrite: false);

    private static bool Publish(string path, byte[] bytes, bool overwrite)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        if (overwrite && File.Exists(path))
        {
            new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite).Dispose();
        }

        var temp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (overwrite && !OperatingSystem.IsWindows() && File.Exists(path))
            {
                File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
            }

            if (overwrite)
            {
                File.Move(temp, path, overwrite: true);
                return true;
            }

            try
            {
                File.Move(temp, path, overwrite: false);
                return true;
            }
            catch (IOException) when (Exists(path))
            {
                File.Delete(temp);
                return false;
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // the temporary file is left for the next run to find; the target was not touched
            }

            throw;
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>The roster container at a level for a team scope; never creates one.</summary>
    internal static JsonObject? Container(JsonObject data, string? teamKey)
    {
        if (teamKey is null)
        {
            return data.Get("roster") is { IsTruthy: true, IsMap: true } r ? r.AsObject() : null;
        }

        return data.Get("rosters") is { IsMap: true } all && all.AsObject().Get(teamKey) is { IsTruthy: true, IsMap: true } block
            ? block.AsObject()
            : null;
    }

    internal static string ContainerLabel(string? teamKey) => teamKey is null ? "roster" : $"rosters.{teamKey}";

    /// <summary>Installs a fresh block as the container for a team scope, replacing it wholesale.</summary>
    internal void Install(JsonObject data, string? teamKey, JsonObject fresh)
    {
        if (!(data.Get("version") is { IsTruthy: true }))
        {
            data["version"] = c.Services.Config.ConfigVersion;
        }

        if (teamKey is null)
        {
            data["roster"] = fresh;
            return;
        }

        if (!(data.Get("rosters") is { IsMap: true } rosters))
        {
            rosters = JsonValue.FromObject(new JsonObject());
            data["rosters"] = rosters;
        }

        rosters.AsObject()[teamKey] = fresh;
    }

    internal static JsonObject FreshBlock(string? route)
    {
        if (route is null || !Ah.Roster.RosterModule.RouteValues.Contains(route))
        {
            throw new RosterFailure($"--route must be \"peer\" or \"subagent\", got {(route is null ? "undefined" : Quote(route))}");
        }

        return new JsonObject { ["route"] = route, ["members"] = new JsonArray() };
    }

    /// <summary>The members of a container with the names they derive for this repository.</summary>
    internal JsonArray NamedMembers(JsonArray members) => c.Services.Config.RosterMemberNames(members, c.RepoBasename);

    internal int FindMemberIndex(JsonArray members, string name)
    {
        var named = NamedMembers(members);
        for (var i = 0; i < named.Count; i++)
        {
            if (named[i].Member("name") is { IsText: true } n && n.AsString() == name)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Removes one member by derived name from a roster level; writes nothing when it is not found.</summary>
    internal RemoveResult RemoveConfigMember(string name, string? key, bool sideEffect = false)
    {
        RemoveResult NotRemoved(string? level, string? path, bool defaulted, string reason)
        {
            c.Stderr.Write($"ah roster: warning — {name} was not removed from the config: {reason}\n");
            return new RemoveResult(level, path, defaulted, false, reason, true, null);
        }

        var target = sideEffect ? ResolveTarget(false, key, useKey: true) : ResolveTarget(false, key, useKey: true);
        if (target.Error is { } error)
        {
            if (!sideEffect)
            {
                throw new RosterFailure(error);
            }

            var unusable = new List<string>();
            if (LevelArg() is null)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var candidates = c.Services.Config.RosterLevelCandidates(c.Cwd);
                foreach (var level in candidates.Keys)
                {
                    if (candidates[level] is { IsArray: true } list)
                    {
                        foreach (var p in list.AsArray())
                        {
                            if (p.IsText && seen.Add(p.AsString()) && Load(p.AsString()) is (_, { } problem))
                            {
                                unusable.Add($"{p.AsString()} {problem}");
                            }
                        }
                    }
                }
            }

            return NotRemoved(null, null, false, unusable.Count > 0 ? $"no roster resolves at any level ({string.Join("; ", unusable)})" : error);
        }

        var levelName = target.Level!;
        var path = PathOf(levelName);
        JsonObject data;
        if (sideEffect)
        {
            var (loaded, problem) = Load(path);
            if (problem is not null)
            {
                return NotRemoved(levelName, path, target.WasDefaulted, $"{path} {problem}");
            }

            data = loaded;
        }
        else
        {
            data = Read(path);
        }

        var container = Container(data, target.TeamKey);
        if (container is null || !(container.Get("members") is { IsArray: true } members))
        {
            return new RemoveResult(
                levelName,
                path,
                target.WasDefaulted,
                false,
                $"no {ContainerLabel(target.TeamKey)} at level \"{levelName}\" ({path}) — run `ah roster init` first",
                false,
                null);
        }

        var idx = FindMemberIndex(members.AsArray(), name);
        if (idx == -1)
        {
            return new RemoveResult(levelName, path, target.WasDefaulted, false, "no such member", false, null);
        }

        var before = NamedMembers(members.AsArray());
        var kept = new JsonArray();
        var all = members.AsArray();
        for (var i = 0; i < all.Count; i++)
        {
            if (i != idx)
            {
                kept.Add(all[i]);
            }
        }

        container["members"] = kept;
        var after = NamedMembers(kept);
        try
        {
            Write(path, data);
        }
        catch (Exception e) when (sideEffect && e is IOException or UnauthorizedAccessException)
        {
            return NotRemoved(levelName, path, target.WasDefaulted, $"{path} could not be written ({e.Message})");
        }

        return new RemoveResult(levelName, path, target.WasDefaulted, true, null, false, ContainerLabel(target.TeamKey)) { Index = idx, Before = before, After = after };
    }

    private List<JsonObject> Migrate(string path, JsonObject data)
    {
        var config = c.Services.Config;
        var changes = new List<JsonObject>();

        void Note(string key, JsonValue from, JsonValue to) =>
            changes.Add(new JsonObject { ["path"] = path, ["key"] = key, ["from"] = from, ["to"] = to });

        var registry = Registry();
        if (_droppedAtRead.Remove(path, out var atRead))
        {
            foreach (var d in atRead)
            {
                Note($"{Str(d.Member("label"))}.members[{Str(d.Member("index"))}]", d.Member("value") ?? JsonValue.Null, JsonValue.Null);
            }
        }

        foreach (var d in config.DropNonObjectMembers(data))
        {
            Note($"{Str(d.Member("label"))}.members[{Str(d.Member("index"))}]", d.Member("value") ?? JsonValue.Null, JsonValue.Null);
        }

        if (data.Get("route") is { IsText: true } route && config.StaleRouteValues.Contains(route.AsString()))
        {
            Note("route", route, JsonValue.Null);
            data.Remove("route");
        }

        if (data.Get("teamAlias") is { } alias)
        {
            Note("teamAlias", alias, JsonValue.Null);
            data.Remove("teamAlias");
        }

        if (data.Get("teamLayout") is { } layout
            && !(config.RosterLevelPaths(c.Cwd).Get("global") is { IsText: true } global && global.AsString() == path))
        {
            Note("teamLayout", layout, JsonValue.Null);
            data.Remove("teamLayout");
        }

        var roles = data.Get("roles") is { IsMap: true } r ? r.AsObject() : new JsonObject();
        string? ClassOf(string role)
        {
            if (roles.Get(role) is { IsMap: true } row && !config.IsBuiltinRole(role)
                && row.AsObject().Get("class") is { IsText: true } cls && config.Classes.Get(cls.AsString()) is { IsTruthy: true })
            {
                return cls.AsString();
            }

            return config.RoleClass(role, registry);
        }

        foreach (var role in roles.Keys.ToList())
        {
            if (!(roles[role] is { IsMap: true } row) || !(row.AsObject().Get("dispatch") is { IsText: true } d && d.AsString() == "model"))
            {
                continue;
            }

            var cls = ClassOf(role);
            if (cls is not null && cls != "legwork")
            {
                Note($"roles.{role}.dispatch", "model", JsonValue.Null);
                row.AsObject().Remove("dispatch");
            }
        }

        foreach (var entry in config.RosterBlocksOf(data))
        {
            var parts = entry.AsArray();
            var label = Str(parts[0]);
            if (!(parts[2] is { IsMap: true } blockValue))
            {
                continue;
            }

            var block = blockValue.AsObject();
            if (block.Get("layout") is { } blockLayout)
            {
                Note($"{label}.layout", blockLayout, JsonValue.Null);
                block.Remove("layout");
            }

            var staleBlock = block.Get("route") is { IsText: true } br && br.AsString() == "subagent";
            if (staleBlock)
            {
                Note($"{label}.route", "subagent", "peer");
                block["route"] = "peer";
            }

            if (block.Get("members") is not { IsArray: true } membersValue)
            {
                continue;
            }

            var members = membersValue.AsArray();
            for (var i = 0; i < members.Count; i++)
            {
                if (!(members[i] is { IsMap: true } mv))
                {
                    continue;
                }

                var m = mv.AsObject();
                var cls = m.Get("role") is { IsText: true } roleText ? ClassOf(roleText.AsString()) : null;
                if (cls is null)
                {
                    continue;
                }

                var at = $"{label}.members[{i}]";
                if (m.Get("onMissing") is { IsText: true } onMissing && onMissing.AsString() is "never" or "prompt")
                {
                    Note($"{at}.onMissing", onMissing, JsonValue.Null);
                    m.Remove("onMissing");
                }

                if (cls == "legwork")
                {
                    if (staleBlock && m.Get("route") is null)
                    {
                        Note($"{at}.route", JsonValue.Null, "subagent");
                        m["route"] = "subagent";
                    }
                }
                else if (m.Get("route") is { IsText: true } mr && mr.AsString() == "subagent")
                {
                    Note($"{at}.route", "subagent", JsonValue.Null);
                    m.Remove("route");
                }
            }
        }

        return changes;
    }

    private static string Str(JsonValue? v) => v is { } x ? JsConvertText(x) : "undefined";

    private static string JsConvertText(JsonValue? v) => v is { } x ? Ah.Core.Json.JsConvert.ToJsString(x) : "undefined";

    internal static string Quote(string text) => JsonText.Quote(text);
}

/// <summary>A resolved write target: the level, whether it was the default, the roster key, or why none could be found.</summary>
internal sealed record TargetLevel(string? Level, bool WasDefaulted, string? TeamKey, string? Error);

/// <summary>What <c>removeConfigMember</c> did: where, whether a row went, and the names before and after when one did.</summary>
internal sealed record RemoveResult(string? Level, string? Path, bool WasDefaulted, bool Removed, string? Reason, bool Unusable, string? Container)
{
    internal int Index { get; init; } = -1;

    internal JsonArray? Before { get; init; }

    internal JsonArray? After { get; init; }
}
