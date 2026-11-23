using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary><c>roster roster list|copy|delete|use</c>: the named roster blocks of the level files and which one is selected.</summary>
internal static class NamedRostersVerb
{
    private const string LegacyDefaultNote =
        "a block named `default` can't be selected or copied — `default` means the unnamed `roster` block — but a team whose file records it keeps using it";

    private static readonly Dictionary<string, string[]> SubFlags = new(StringComparer.Ordinal)
    {
        ["list"] = ["json", "cwd"],
        ["copy"] = ["level", "dry-run", "cwd"],
        ["delete"] = ["level", "dry-run", "cwd"],
        ["use"] = ["level", "clear", "cwd"],
    };

    [Verb("roster:roster")]
    internal static int Roster(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    internal sealed record Definition(string Level, string Path, JsonValue Block, JsonArray Members, JsonValue Route);

    private static int Run(RosterContext c)
    {
        var words = c.Opts.Positionals;
        var sub = words.Count > 0 ? words[0] : null;
        if (sub is null || !SubFlags.TryGetValue(sub, out var allowed))
        {
            throw new RosterFailure(
                "usage: ah roster roster list [--json] | roster copy <src> <dst> [--level L] [--dry-run] | roster delete <name> [--level L] [--dry-run] | roster use <name>|default [--level L] | roster use --clear [--level L]  (all with --cwd <abs cwd>)");
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw new RosterFailure($"roster {sub}: unrecognized flag --{key} (use {string.Join(", ", allowed.Select(k => "--" + k))})");
            }
        }

        var files = new LevelFiles(c);
        var first = words.Count > 1 ? words[1] : null;
        var second = words.Count > 2 ? words[2] : null;
        switch (sub)
        {
            case "list":
                return List(c);
            case "copy":
                return Copy(c, files, first, second);
            case "delete":
                return Delete(c, files, first);
            default:
                return Use(c, files, first);
        }
    }

    // ---------------------------------------------------------------- reading

    /// <summary>A level file's parsed object, or null when it is missing or not an object. Reads only.</summary>
    private static JsonObject? LevelFileData(RosterContext c, string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var parsed = JsonText.Parse(File.ReadAllBytes(path));
            if (!parsed.IsMap)
            {
                return null;
            }

            var data = parsed.AsObject();
            _ = c.Services.Config.DropNonObjectMembers(data);
            return data;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    /// <summary>Every visible level file holding roster block <paramref name="key"/> (null: the unnamed block), most specific first.</summary>
    internal static List<Definition> Definitions(RosterContext c, string? key)
    {
        var config = c.Services.Config;
        var candidates = config.RosterLevelCandidates(c.Cwd);
        var defs = new List<Definition>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in config.RosterLevels)
        {
            if (candidates.Get(level) is not { IsArray: true } paths)
            {
                continue;
            }

            foreach (var p in paths.AsArray())
            {
                if (!p.IsText || !seen.Add(p.AsString()))
                {
                    continue;
                }

                var path = p.AsString();
                if (LevelFileData(c, path) is not { } data)
                {
                    continue;
                }

                JsonValue? block;
                if (key is null)
                {
                    block = data.Get("roster");
                }
                else
                {
                    block = data.Get("rosters") is { IsMap: true } map && !map.IsArray ? map.AsObject().Get(key) : null;
                }

                if (block is null || block.Value.IsNull)
                {
                    continue;
                }

                var members = block.Value.IsMap && block.Value.AsObject().Get("members") is { IsArray: true } m ? m.AsArray() : new JsonArray();
                var route = block.Value.IsMap && block.Value.AsObject().Get("route") is { } r ? r : JsonValue.Null;
                defs.Add(new Definition(level, path, block.Value, members, route));
            }
        }

        return defs;
    }

    internal static Definition? Winning(List<Definition> defs) =>
        defs.FirstOrDefault(d => d.Members.Count > 0) ?? defs.FirstOrDefault();

    /// <summary>Every team file of this checkout's hierarchy dir (and the main checkout's), by the roster key it records.</summary>
    private static Dictionary<string, List<(string? Team, string File)>> TeamsByRosterKey(RosterContext c)
    {
        var byKey = new Dictionary<string, List<(string? Team, string File)>>(StringComparer.Ordinal);
        var dirs = new List<string>();
        foreach (var d in new[] { c.Dir, c.MainDir })
        {
            if (!string.IsNullOrEmpty(d) && !dirs.Contains(d))
            {
                dirs.Add(d);
            }
        }

        var roster = c.Services.Roster;
        foreach (var dir in dirs)
        {
            var names = new List<string?> { null };
            names.AddRange(roster.ListTeamNames(dir));
            foreach (var name in names)
            {
                if (roster.ReadTeamRecord(dir, name).IsNull)
                {
                    continue;
                }

                var key = roster.TeamRosterEntry(dir, name)?.Key ?? "\0default";
                if (!byKey.TryGetValue(key, out var list))
                {
                    list = [];
                    byKey[key] = list;
                }

                list.Add((name, roster.TeamPath(dir, name)));
            }
        }

        return byKey;
    }

    private static string? LevelOpt(RosterContext c, LevelFiles files) =>
        c.Opts.Has("level") ? files.RequireLevel(c.Opts.Text("level") ?? "true") : null;

    // ---------------------------------------------------------------- list

    private static int List(RosterContext c)
    {
        var config = c.Services.Config;
        var selection = config.RosterSelection(c.Cwd, null);
        var teams = TeamsByRosterKey(c);
        var selectionKey = selection.Get("key") is { IsText: true } sk ? sk.AsString() : null;
        var defaultName = config.DefaultRoster;

        JsonObject Entry(string? key)
        {
            var defs = Definitions(c, key);
            var win = Winning(defs);
            var shadowed = new JsonArray();
            foreach (var d in defs.Where(d => d != win))
            {
                shadowed.Add(new JsonObject { ["level"] = d.Level, ["path"] = d.Path });
            }

            var entryTeams = new JsonArray();
            if (teams.TryGetValue(key ?? "\0default", out var users))
            {
                foreach (var (team, file) in users)
                {
                    entryTeams.Add(new JsonObject { ["team"] = team is null ? JsonValue.Null : team, ["file"] = file });
                }
            }

            var entry = new JsonObject
            {
                ["roster"] = key ?? defaultName,
                ["block"] = LevelFiles.ContainerLabel(key),
                ["level"] = win is null ? JsonValue.Null : win.Level,
                ["path"] = win is null ? JsonValue.Null : win.Path,
                ["shadowed"] = shadowed,
                ["members"] = win?.Members.Count ?? 0,
                ["route"] = win?.Route ?? JsonValue.Null,
                ["selected"] = key != defaultName && (key is null ? selectionKey is null : selectionKey == key),
                ["teams"] = entryTeams,
            };
            if (key == defaultName)
            {
                entry["note"] = LegacyDefaultNote;
            }

            return entry;
        }

        var rosters = new List<JsonObject> { Entry(null) };
        foreach (var named in config.NamedRosterKeys(c.Cwd))
        {
            rosters.Add(Entry(named.AsString()));
        }

        if (c.Opts.IsTrue("json"))
        {
            var array = new JsonArray();
            foreach (var r in rosters)
            {
                array.Add(r);
            }

            c.Out(JsonValue.FromObject(new JsonObject
            {
                ["selection"] = config.SelectionView(selection),
                ["rosters"] = array,
            }));
            return 0;
        }

        var source = Text(selection, "source");
        var from = source == "activeRoster"
            ? $"activeRoster at {Text(selection, "level")}, {Text(selection, "path")}"
            : source == "env" ? "WRANGL_ROSTER" : "nothing selected";
        var lines = new List<string> { $"selection: {Text(selection, "roster")} ({from})" };
        foreach (var r in rosters)
        {
            var level = r.Get("level") is { IsText: true } l ? l.AsString() : null;
            var where = level is not null ? $"{level} {Text(r, "path")}" : "not defined";
            var count = (int)r["members"].AsNumber();
            var route = r.Get("route") is { IsNull: false } rt ? JsConvert.ToJsString(rt) : "-";
            var parts = new List<string>
            {
                $"{(r["selected"].AsBool() ? "*" : " ")} {Text(r, "roster")} ({Text(r, "block")})",
                where,
                $"{count} member{(count == 1 ? string.Empty : "s")}",
                $"route {route}",
            };
            var shadowed = r["shadowed"].AsArray();
            if (shadowed.Count > 0)
            {
                parts.Add("shadows " + string.Join(", ", shadowed.Select(s => $"{Text(s.AsObject(), "level")} {Text(s.AsObject(), "path")}")));
            }

            var users = r["teams"].AsArray();
            if (users.Count > 0)
            {
                parts.Add("teams: " + string.Join(", ", users.Select(t => t.AsObject().Get("team") is { IsText: true } n ? n.AsString() : "the default team")));
            }

            if (r.Get("note") is { IsText: true } note)
            {
                parts.Add(note.AsString());
            }

            lines.Add(string.Join(" · ", parts));
        }

        c.Stdout.Write(string.Join("\n", lines) + "\n");
        return 0;
    }

    private static string Text(JsonObject o, string key) => o.Get(key) is { } v ? JsConvert.ToJsString(v) : "undefined";

    // ---------------------------------------------------------------- copy

    private static int Copy(RosterContext c, LevelFiles files, string? src, string? dst)
    {
        var config = c.Services.Config;
        var defaultName = config.DefaultRoster;
        if (src is null || dst is null)
        {
            throw new RosterFailure("usage: ah roster roster copy <src> <dst> [--level L] [--dry-run]");
        }

        var srcKey = src == defaultName ? null : src;
        if (srcKey is not null)
        {
            var v = config.ValidateTeamAlias(JsonValue.FromString(srcKey));
            if (!(v.Get("ok") is { IsTruthy: true }))
            {
                throw new RosterFailure($"roster copy: <src> {LevelFiles.Quote(src)}: {Text(v, "why")}");
            }
        }

        var win = Winning(Definitions(c, srcKey));
        if (win is null)
        {
            var defined = string.Join(", ", config.SelectableRosters(c.Cwd).Select(r => JsConvert.ToJsString(r)));
            var what = srcKey is null ? "the default `roster` block" : $"roster \"{src}\"";
            throw new RosterFailure($"roster copy: {what} is not defined at any level (defined: {(defined.Length == 0 ? "none" : defined)})");
        }

        if (dst == defaultName)
        {
            throw new RosterFailure("roster copy: <dst> can't be `default` — it means the unnamed `roster` block");
        }

        var check = config.ValidateTeamAlias(JsonValue.FromString(dst));
        if (!(check.Get("ok") is { IsTruthy: true }))
        {
            throw new RosterFailure($"roster copy: <dst> {LevelFiles.Quote(dst)}: {Text(check, "why")}");
        }

        var level = LevelOpt(c, files) ?? win.Level;
        var path = files.PathOf(level);
        var data = files.Read(path);
        var taken = Definitions(c, dst).FirstOrDefault(d => d.Level == level);
        if (taken is not null)
        {
            var from = taken.Path == path ? string.Empty : $" --cwd {config.CheckoutRoot(c.Cwd)}";
            throw new RosterFailure(
                $"roster copy: rosters.{dst} is already defined at level \"{level}\" ({taken.Path}) — pick another name, or delete it first with `ah roster roster delete {dst} --level {level}{from}`");
        }

        var block = new JsonObject();
        if (win.Block.IsMap && win.Block.AsObject().Get("route") is { } route)
        {
            block["route"] = route;
        }

        var copied = new JsonArray();
        foreach (var member in win.Members)
        {
            copied.Add(Clone(member));
        }

        block["members"] = copied;
        var dryRun = c.Opts.IsTrue("dry-run");
        if (!dryRun)
        {
            files.Install(data, dst, block);
            files.Write(path, data);
        }

        c.Out(JsonValue.FromObject(new JsonObject
        {
            ["copied"] = !dryRun,
            ["dry_run"] = dryRun,
            ["from"] = new JsonObject
            {
                ["roster"] = srcKey is null ? defaultName : src,
                ["block"] = LevelFiles.ContainerLabel(srcKey),
                ["level"] = win.Level,
                ["path"] = win.Path,
            },
            ["to"] = new JsonObject { ["roster"] = dst, ["block"] = LevelFiles.ContainerLabel(dst), ["level"] = level, ["path"] = path },
            ["route"] = block.Get("route") ?? JsonValue.Null,
            ["members"] = copied.Count,
        }));
        return 0;
    }

    private static JsonValue Clone(JsonValue value) => value.Kind switch
    {
        JsonKind.Map => JsonValue.FromObject(CloneObject(value.AsObject())),
        JsonKind.Array => JsonValue.FromArray(CloneArray(value.AsArray())),
        _ => value,
    };

    private static JsonObject CloneObject(JsonObject source)
    {
        var copy = new JsonObject();
        foreach (var key in source.Keys)
        {
            copy[key] = Clone(source[key]);
        }

        return copy;
    }

    private static JsonArray CloneArray(JsonArray source)
    {
        var copy = new JsonArray();
        foreach (var item in source)
        {
            copy.Add(Clone(item));
        }

        return copy;
    }

    // ---------------------------------------------------------------- delete

    private static int Delete(RosterContext c, LevelFiles files, string? name)
    {
        var config = c.Services.Config;
        if (string.IsNullOrEmpty(name))
        {
            throw new RosterFailure("usage: ah roster roster delete <name> [--level L] [--dry-run]");
        }

        if (name == config.DefaultRoster)
        {
            throw new RosterFailure("roster delete: `default` is the unnamed `roster` block, which delete never removes");
        }

        var explicitLevel = LevelOpt(c, files);
        var win = explicitLevel is null ? Winning(Definitions(c, name)) : null;
        if (explicitLevel is null && win is null)
        {
            var defined = string.Join(", ", config.SelectableRosters(c.Cwd).Select(r => JsConvert.ToJsString(r)));
            throw new RosterFailure($"roster delete {name}: no level defines it (defined: {(defined.Length == 0 ? "none" : defined)})");
        }

        var level = explicitLevel ?? win!.Level;
        var path = files.PathOf(level);
        var data = files.Read(path);
        var rosters = data.Get("rosters") is { IsMap: true } r ? r.AsObject() : null;
        if (!File.Exists(path) || rosters is null || !rosters.ContainsKey(name))
        {
            var elsewhere = Definitions(c, name).FirstOrDefault(d => d.Level == level && d.Path != path);
            if (elsewhere is not null)
            {
                throw new RosterFailure(
                    $"roster delete {name}: rosters.{name} at level \"{level}\" is in the main checkout's file ({elsewhere.Path}), not this worktree's ({path}) — nothing was deleted. "
                    + $"Delete it from the main checkout: `ah roster roster delete {name} --level {level} --cwd {config.CheckoutRoot(c.Cwd)}`");
            }

            throw new RosterFailure($"roster delete {name}: no rosters.{name} at level \"{level}\" ({path})");
        }

        var users = TeamsByRosterKey(c).GetValueOrDefault(name) ?? [];
        if (users.Count > 0)
        {
            var who = string.Join(", ", users.Select(t => $"{(t.Team is null ? "the default team" : $"team \"{t.Team}\"")} ({t.File})"));
            throw new RosterFailure(
                $"roster delete {name}: {who} {(users.Count == 1 ? "uses" : "use")} it — nothing was deleted. Disband the team first (`ah roster disband`), or remove an orphaned record with `ah roster reap --commit`.");
        }

        if (data.Get("activeRoster") is { IsText: true } active && active.AsString() == name)
        {
            throw new RosterFailure(
                $"roster delete {name}: activeRoster in {path} selects it — nothing was deleted. Select another first: `ah roster roster use <other> --level {level}`, or `ah roster roster use --clear --level {level}`.");
        }

        var warnings = new List<string>();
        var candidates = config.RosterLevelCandidates(c.Cwd);
        foreach (var other in config.RosterLevels)
        {
            if (candidates.Get(other) is not { IsArray: true } paths)
            {
                continue;
            }

            foreach (var p in paths.AsArray())
            {
                if (!p.IsText || p.AsString() == path)
                {
                    continue;
                }

                if (LevelFileData(c, p.AsString()) is { } d && d.Get("activeRoster") is { IsText: true } sel && sel.AsString() == name)
                {
                    warnings.Add(
                        $"activeRoster at {other} in {p.AsString()} still selects \"{name}\"; once it is deleted, commands there will refuse until that selection changes");
                }
            }
        }

        foreach (var w in warnings)
        {
            c.Stderr.Write($"ah roster: warning — {w}\n");
        }

        var dryRun = c.Opts.IsTrue("dry-run");
        if (!dryRun)
        {
            rosters.Remove(name);
            files.Write(path, data);
        }

        var result = new JsonObject
        {
            ["deleted"] = !dryRun,
            ["dry_run"] = dryRun,
            ["roster"] = name,
            ["block"] = LevelFiles.ContainerLabel(name),
            ["level"] = level,
            ["path"] = path,
        };
        if (warnings.Count > 0)
        {
            var array = new JsonArray();
            foreach (var w in warnings)
            {
                array.Add(w);
            }

            result["warnings"] = array;
        }

        c.Out(JsonValue.FromObject(result));
        return 0;
    }

    // ---------------------------------------------------------------- use

    private static int Use(RosterContext c, LevelFiles files, string? name)
    {
        var config = c.Services.Config;
        var clear = c.Opts.IsTrue("clear");
        if (clear && name is not null)
        {
            throw new RosterFailure("roster use: pass a roster name or --clear, not both");
        }

        if (!clear && string.IsNullOrEmpty(name))
        {
            throw new RosterFailure("usage: ah roster roster use <name>|default [--level L] | roster use --clear [--level L]");
        }

        var level = LevelOpt(c, files) ?? (config.FindGitRoot(c.Cwd) is not null ? "repo-user" : "global");
        var path = files.PathOf(level);
        var warnings = new List<string>();
        if (clear)
        {
            var had = LevelFileData(c, path) is { } existing && existing.Get("activeRoster") is not null;
            var fileRemoved = false;
            if (had)
            {
                var data = files.Read(path);
                data.Remove("activeRoster");
                if (data.Count == 0)
                {
                    File.Delete(path);
                    fileRemoved = true;
                }
                else
                {
                    files.Write(path, data);
                }
            }

            var cleared = new JsonObject
            {
                ["level"] = level,
                ["path"] = path,
                ["activeRoster"] = JsonValue.Null,
                ["cleared"] = had,
            };
            if (fileRemoved)
            {
                cleared["file_removed"] = true;
            }

            cleared["selection"] = config.SelectionView(config.RosterSelection(c.Cwd, null));
            c.Out(JsonValue.FromObject(cleared));
            return 0;
        }

        if (name != config.DefaultRoster)
        {
            var defined = config.SelectableRosters(c.Cwd).Select(r => JsConvert.ToJsString(r)).ToList();
            if (!defined.Contains(name!))
            {
                throw new RosterFailure(
                    $"roster use {name}: no level defines roster \"{name}\" (defined: {(defined.Count == 0 ? "none" : string.Join(", ", defined))}) — make it with `ah roster init --roster {name}` or `ah roster roster copy <src> {name}`");
            }

            if (level != "repo-user" && !Definitions(c, name).Any(d => d.Level == level))
            {
                warnings.Add(
                    level == "global"
                        ? $"roster \"{name}\" is not defined in the global file, so other repos will see this selection as missing — select it at repo-user or repo level instead"
                        : $"roster \"{name}\" is defined only in your own files (repo-user or global), so others working in this repo will see this selection as missing — define it at repo level, or select it at repo-user instead");
            }
        }

        var existed = File.Exists(path);
        var current = existed ? files.Read(path) : new JsonObject();
        if (existed && current.Count == 0)
        {
            current["version"] = config.ConfigVersion;
        }

        current["activeRoster"] = name!;
        files.Write(path, current);
        foreach (var w in warnings)
        {
            c.Stderr.Write($"ah roster: warning — {w}\n");
        }

        var result = new JsonObject
        {
            ["level"] = level,
            ["path"] = path,
            ["activeRoster"] = name!,
            ["selection"] = config.SelectionView(config.RosterSelection(c.Cwd, null)),
        };
        if (warnings.Count > 0)
        {
            var array = new JsonArray();
            foreach (var w in warnings)
            {
                array.Add(w);
            }

            result["warnings"] = array;
        }

        c.Out(JsonValue.FromObject(result));
        return 0;
    }
}
