using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>roster param set|unset|list</c> (role-params RP-D5): the values roles' <c>params</c> name, kept in the level files' top-level <c>params</c>. Anyone may run it, as
/// <c>roster use</c>. A value is stored and shown as written; nothing opens or checks it. Exit codes: 0 ok, 1 not found, 2 bad input.
/// </summary>
internal static class ParamVerb
{
    private const string Usage = "usage: ah roster param set <name> <value> [--level repo-user|repo|global] | param unset <name> [--level L] | param list [--json]  (all with --cwd <abs cwd>)";

    private static readonly Dictionary<string, string[]> SubFlags = new(StringComparer.Ordinal)
    {
        ["set"] = ["level", "cwd"],
        ["unset"] = ["level", "cwd"],
        ["list"] = ["json", "cwd"],
    };

    [Verb("roster:param")]
    internal static int Param(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        var words = c.Opts.Positionals;
        var sub = words.Count > 0 ? words[0] : null;
        if (sub is null || !SubFlags.TryGetValue(sub, out var allowed))
        {
            throw new RosterFailure(Usage);
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw new RosterFailure($"param {sub}: unrecognized flag --{key} (use {string.Join(", ", allowed.Select(k => "--" + k))})");
            }
        }

        var files = new LevelFiles(c);
        return sub switch
        {
            "set" => Set(c, files, words.Count == 3 ? words[1] : null, words.Count == 3 ? words[2] : null),
            "unset" => Unset(c, files, words.Count == 2 ? words[1] : null),
            _ => List(c),
        };
    }

    /// <summary>The level a write goes to: <c>--level</c>, else repo-user inside a git repo, else global (as <c>roster use</c>).</summary>
    private static string LevelOf(RosterContext c, LevelFiles files) =>
        c.Opts.Text("level") is { } given ? files.RequireLevel(given) : c.Services.Config.FindGitRoot(c.Cwd) is not null ? "repo-user" : "global";

    private static int Set(RosterContext c, LevelFiles files, string? name, string? value)
    {
        if (name is null || value is null)
        {
            throw new RosterFailure(Usage);
        }

        c.Out(Write(c, files, LevelOf(c, files), name, value));
        return 0;
    }

    /// <summary>
    /// Writes <c>params.&lt;name&gt;</c> at <paramref name="level"/>: the one writer, for <c>param set</c> and <c>create --param</c> (RP-D11). A bad name or value is
    /// refused before anything is written. The name need not be declared by any role.
    /// </summary>
    internal static JsonObject Write(RosterContext c, LevelFiles files, string level, string name, string value)
    {
        var config = c.Services.Config;
        if ((config.ParamNameError(name) ?? config.ParamValueError(value)) is { } why)
        {
            throw new RosterFailure($"param set {name}: {why}; nothing was written");
        }

        var path = files.PathOf(level);
        var data = File.Exists(path) ? files.Read(path) : new JsonObject { ["version"] = config.ConfigVersion };
        var values = data.Get("params") is { IsMap: true } existing ? existing.AsObject().Clone() : new JsonObject();
        values[name] = value;
        data["params"] = values;
        files.Write(path, data);
        return new JsonObject { ["name"] = name, ["value"] = value, ["level"] = level, ["path"] = path };
    }

    private static int Unset(RosterContext c, LevelFiles files, string? name)
    {
        if (name is null)
        {
            throw new RosterFailure(Usage);
        }

        var config = c.Services.Config;
        var level = c.Opts.Text("level") is { } given ? files.RequireLevel(given) : config.ParamValues(c.Cwd).FirstOrDefault(p => p.Name == name)?.Level;
        var path = level is null ? null : files.PathOf(level);
        if (path is null || !File.Exists(path) || files.Read(path) is not { } data || data.Get("params") is not { IsMap: true } values || !values.AsObject().ContainsKey(name))
        {
            c.Stderr.Write($"ah roster: param unset {name}: {(level is null ? "no level sets it" : $"level {level} does not set it")}\n");
            return 1;
        }

        var rest = values.AsObject().Clone();
        rest.Remove(name);
        if (rest.Count == 0)
        {
            data.Remove("params");
        }
        else
        {
            data["params"] = rest;
        }

        files.Write(path, data);
        c.Out(new JsonObject { ["name"] = name, ["level"] = level!, ["path"] = path, ["removed"] = true });
        return 0;
    }

    /// <summary>Every name a role of the resolved roster declares, and every name set at any level: its value and level, or <c>missing</c>, and the roles that declare it.</summary>
    private static int List(RosterContext c)
    {
        var config = c.Services.Config;
        var rosterKey = config.RosterSelection(c.Cwd, null).Get("key") is { IsText: true } key ? key.AsString() : null;
        var roles = config.ResolveRoster(c.Cwd, rosterKey, c.RepoBasename, c.Registry) is { IsMap: true } roster && roster.Member("members") is { IsArray: true } members
            ? members.AsArray().Select(m => m.Member("role")).Where(r => r is { IsText: true }).Select(r => r!.Value.AsString()).Distinct(StringComparer.Ordinal).ToList()
            : [];
        var declaredBy = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            foreach (var p in config.RoleParams(c.Cwd, role, c.Registry))
            {
                (declaredBy.TryGetValue(p.Name, out var list) ? list : declaredBy[p.Name] = []).Add(role);
            }
        }

        var values = config.ParamValues(c.Cwd).ToDictionary(p => p.Name, StringComparer.Ordinal);
        var rows = new JsonArray();
        var lines = new List<string>();
        foreach (var name in declaredBy.Keys.Union(values.Keys).Order(StringComparer.Ordinal))
        {
            var by = declaredBy.TryGetValue(name, out var d) ? d : [];
            var set = values.GetValueOrDefault(name);
            var byArray = new JsonArray();
            byArray.AddRange(by.Select(r => (JsonValue)r));
            rows.Add(new JsonObject
            {
                ["name"] = name,
                ["value"] = set?.Value is { } v ? v : JsonValue.Null,
                ["level"] = set?.Level is { } l ? l : JsonValue.Null,
                ["roles"] = byArray,
            });
            lines.Add($"{name}: {(set is null ? "missing" : $"{config.EscapeTerminal(set.Value!)} ({set.Level})")}{(by.Count > 0 ? $" — declared by {string.Join(", ", by)}" : string.Empty)}");
        }

        if (c.Opts.IsTrue("json"))
        {
            c.Out(new JsonObject { ["params"] = rows });
            return 0;
        }

        c.Stdout.Write(lines.Count == 0 ? "no role parameters declared or set\n" : string.Join('\n', lines) + "\n");
        return 0;
    }
}
