using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>
/// <c>pack list</c> and <c>pack show</c>: the role packs installed plugins carry, read-only. Text from a pack is data from another
/// author, so every string shown has its hidden characters escaped.
/// </summary>
internal static class PackVerbs
{
    private const string Usage =
        "usage: ah roster pack list [--json] | pack show <plugin>[@<marketplace>] [--json] | pack show --path <dir> [--json]  (all with --cwd <abs cwd>)";

    [Verb("roster:pack")]
    internal static int Pack(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (key is not ("json" or "path" or "cwd"))
            {
                throw new RosterFailure($"pack: unrecognized flag --{key} (use --json, --path, --cwd)");
            }
        }

        var sub = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        var target = c.Opts.Positionals.Count > 1 ? c.Opts.Positionals[1] : null;
        return sub switch
        {
            "list" => List(c),
            "show" => Show(c, target),
            _ => throw new RosterFailure(Usage),
        };
    }

    private static string Esc(RosterContext c, JsonValue? text) => c.Services.Config.EscapeTerminal(text);

    /// <summary>The version a plugin at <paramref name="dir"/> declares in its plugin.json, else <paramref name="fallback"/>, escaped for printing.</summary>
    internal static JsonValue PluginVersionAt(RosterContext c, string dir, JsonValue? fallback)
    {
        var version = fallback ?? JsonValue.Null;
        try
        {
            var pj = JsonText.Parse(c.Services.Files.ReadText(NodePath.Join(dir, ".claude-plugin", "plugin.json")));
            if (pj.IsMap && pj.Member("version") is { IsText: true } v)
            {
                version = v;
            }
        }
        catch (Exception e) when (e is IOException or JsonParseException or UnauthorizedAccessException)
        {
            // no readable plugin.json: the install record's version stands
        }

        return version.IsText ? Esc(c, version) : version;
    }

    /// <summary>The roster level names of resolveConfig's scopes.</summary>
    internal static JsonValue LevelOfScope(JsonValue? scope)
    {
        if (scope is not { IsText: true } s)
        {
            return JsonValue.Null;
        }

        return s.AsString() switch
        {
            "user" => "global",
            "project" => "repo",
            "repo-user" => "repo-user",
            "default" => "shipped",
            var other => other,
        };
    }

    /// <summary>
    /// Where pack role <paramref name="from"/> is used (ah2-698 D5): each roster block that names its pack, as <c>{roster, level}</c>
    /// across the visible levels, then each legacy config row with that <c>from</c>, as <c>{role, level}</c>.
    /// </summary>
    private static JsonArray AdoptionsOf(RosterContext c, string from)
    {
        var config = c.Services.Config;
        var reg = c.Registry;
        var result = new JsonArray();
        var pack = from[..from.LastIndexOf(':')];
        var keys = new List<string?> { null };
        keys.AddRange(config.NamedRosterKeys(c.Cwd).Select(k => (string?)k.AsString()));
        foreach (var key in keys)
        {
            foreach (var def in NamedRostersVerb.Definitions(c, key))
            {
                if (def.Block.IsMap && def.Block.AsObject().Get("packs") is { IsArray: true } packs && packs.AsArray().Any(p => p.IsText && p.AsString() == pack))
                {
                    result.Add(new JsonObject { ["roster"] = key ?? config.DefaultRoster, ["level"] = def.Level });
                }
            }
        }

        var roles = reg.Get("roles") is { IsMap: true } r ? r.AsObject() : new JsonObject();
        var sources = reg.Get("sources") is { IsMap: true } s ? s.AsObject() : new JsonObject();
        foreach (var role in config.RegistryRoles(reg))
        {
            if (roles.Get(role) is { IsMap: true } entry && entry.Member("from") is { IsText: true } f && f.AsString() == from
                && entry.Member("pack")?.Member("via") is { IsText: true } via && via.AsString().StartsWith("row:", StringComparison.Ordinal))
            {
                result.Add(new JsonObject { ["role"] = role, ["level"] = LevelOfScope(sources.Get(role)) });
            }
        }

        return result;
    }

    private static string UsedBy(JsonValue a) => a.Member("roster") is { IsText: true } roster
        ? $"roster {roster.AsString()} at {MemberPlanning.JsConvertText(a.Member("level"))}"
        : $"row {MemberPlanning.JsConvertText(a.Member("role"))} at {MemberPlanning.JsConvertText(a.Member("level"))}";

    private static JsonObject RoleSummary(RosterContext c, string role, JsonValue m)
    {
        var src = m.Member("row") is { IsTruthy: true } row ? row : m.Member("raw") is { IsMap: true } raw ? raw : new JsonObject();
        var o = new JsonObject
        {
            ["name"] = Esc(c, role),
            ["class"] = src.Member("class") is { IsNull: false } cls ? cls : JsonValue.Null,
            ["agent"] = m.Member("agent") is { IsTruthy: true } agent ? Esc(c, agent) : JsonValue.Null,
            ["routes"] = src.Member("routes") is { IsText: true } routes ? Esc(c, routes) : JsonValue.Null,
        };
        if (m.Member("error") is { IsTruthy: true } error)
        {
            o["error"] = error.Member("code") ?? JsonValue.Null;
        }

        return o;
    }

    // ---------------------------------------------------------------- list

    private static int List(RosterContext c)
    {
        var config = c.Services.Config;
        var packs = new JsonArray();
        foreach (var r in config.PackRecordList())
        {
            var installPath = MemberPlanning.JsConvertText(r.Member("installPath"));
            var plugin = MemberPlanning.JsConvertText(r.Member("plugin"));
            var marketplace = r.Member("marketplace");
            var manifest = config.ReadPackManifest(installPath, plugin);
            var roles = new JsonArray();
            if (manifest.Get("roles") is { IsMap: true } roleMap)
            {
                foreach (var role in roleMap.AsObject().Keys)
                {
                    var summary = RoleSummary(c, role, roleMap.AsObject()[role]);
                    summary["adopted"] = AdoptionsOf(c, $"{plugin}@{(marketplace is { IsNull: false } m ? JsConvert.ToJsString(m) : "null")}:{role}");
                    roles.Add(summary);
                }
            }

            var pack = new JsonObject
            {
                ["plugin"] = Esc(c, r.Member("plugin")),
                ["marketplace"] = marketplace is { IsNull: true } or null ? JsonValue.Null : Esc(c, marketplace),
                ["version"] = PluginVersionAt(c, installPath, r.Member("version")),
                ["path"] = installPath,
            };
            if (manifest.Get("error") is { IsTruthy: true } error)
            {
                pack["manifest_error"] = error;
            }

            pack["roles"] = roles;
            pack["also_in_plugin"] = config.PackExtras(installPath, manifest).Count;
            packs.Add(pack);
        }

        if (c.Opts.IsTrue("json"))
        {
            c.Out(new JsonObject { ["packs"] = packs });
            return 0;
        }

        var lines = new List<string>();
        foreach (var p in packs)
        {
            var also = p.Member("also_in_plugin") is { IsNumber: true } n ? (int)n.AsNumber() : 0;
            var error = p.Member("manifest_error") is { IsTruthy: true } e
                ? $" — {MemberPlanning.JsConvertText(e.Member("code"))}: {Esc(c, e.Member("message"))}"
                : string.Empty;
            var version = p.Member("version") is { IsTruthy: true } v ? MemberPlanning.JsConvertText(v) : "?";
            lines.Add(
                $"{MemberPlanning.JsConvertText(p.Member("plugin"))}@{MemberPlanning.JsConvertText(p.Member("marketplace"))} {version} — {MemberPlanning.JsConvertText(p.Member("path"))}{error}"
                + (also > 0 ? $" — also carries {also} other thing{(also > 1 ? "s" : string.Empty)} (pack show)" : string.Empty));
            foreach (var role in p.Member("roles")!.Value.AsArray())
            {
                var adopted = string.Join("; ", role.Member("adopted")!.Value.AsArray().Select(UsedBy));
                var routes = role.Member("routes") is { IsTruthy: true } rt ? $" · routes \"{MemberPlanning.JsConvertText(rt)}\"" : string.Empty;
                var err = role.Member("error") is { IsTruthy: true } re ? $" · {MemberPlanning.JsConvertText(re)}" : string.Empty;
                lines.Add(
                    $"  {MemberPlanning.JsConvertText(role.Member("name"))} · {(role.Member("class") is { IsNull: false } cl ? MemberPlanning.JsConvertText(cl) : "?")} · {(role.Member("agent") is { IsTruthy: true } ag ? MemberPlanning.JsConvertText(ag) : "?")}{routes}{err}"
                    + (adopted.Length > 0 ? $" — used by {adopted}" : string.Empty));
            }
        }

        c.Stdout.Write(lines.Count > 0 ? string.Join('\n', lines) + "\n" : "no role packs installed\n");
        return 0;
    }

    // ---------------------------------------------------------------- show

    private static int Show(RosterContext c, string? target)
    {
        var config = c.Services.Config;
        string dir;
        string plugin;
        JsonValue? record = null;
        if (c.Opts.Text("path") is { } given)
        {
            if (target is not null)
            {
                throw new RosterFailure("pack show: pass a plugin name or --path <dir>, not both");
            }

            dir = Path.GetFullPath(given, c.Cwd);
            if (!c.Services.Files.Exists(dir))
            {
                throw new RosterFailure($"pack show --path: {dir} does not exist");
            }

            plugin = config.PluginNameAt(dir);
        }
        else
        {
            if (string.IsNullOrEmpty(target))
            {
                throw new RosterFailure("usage: ah roster pack show <plugin>[@<marketplace>] | pack show --path <dir> [--json]");
            }

            var at = target.IndexOf('@', StringComparison.Ordinal);
            var name = at < 0 ? target : target[..at];
            var marketplace = at < 0 ? null : target[(at + 1)..];
            var next = marketplace is not null && marketplace.Contains('@', StringComparison.Ordinal) ? marketplace[..marketplace.IndexOf('@', StringComparison.Ordinal)] : marketplace;
            var matches = config.PackRecordList()
                .Where(r => MemberPlanning.JsConvertText(r.Member("plugin")) == name
                    && (string.IsNullOrEmpty(next) || (r.Member("marketplace") is { IsText: true } m && m.AsString() == next)))
                .ToList();
            if (matches.Count == 0)
            {
                throw new RosterFailure(
                    $"pack show: no installed role pack {target} — pack list shows the installed ones, and pack show --path <dir> reads one that isn't installed");
            }

            if (matches.Count > 1)
            {
                var each = string.Join(", ", matches.Select(r => $"{MemberPlanning.JsConvertText(r.Member("key"))} at {MemberPlanning.JsConvertText(r.Member("installPath"))}"));
                throw new RosterFailure($"pack show: {target} has {matches.Count} install records ({each}) — name the marketplace, or use --path");
            }

            record = matches[0];
            dir = MemberPlanning.JsConvertText(record.Value.Member("installPath"));
            plugin = MemberPlanning.JsConvertText(record.Value.Member("plugin"));
        }

        var manifest = config.ReadPackManifest(dir, plugin);
        var tree = config.PackTreeJson(dir);
        var claims = config.PackNameClaimsJson(dir, manifest);
        var roles = new JsonArray();
        var roleMap = manifest.Get("roles") is { IsMap: true } rm ? rm.AsObject() : new JsonObject();
        foreach (var role in roleMap.Keys)
        {
            roles.Add(ShowRole(c, dir, plugin, record, role, roleMap[role], claims));
        }

        var markets = record is { } rec && rec.Member("marketplace") is { IsNull: false } mk ? Esc(c, mk) : null;
        var manifestError = manifest.Get("error") is { IsTruthy: true } me
            ? (JsonValue)Clone(me, "message", Esc(c, me.Member("message")))
            : JsonValue.Null;
        var warnings = new JsonArray();
        if (manifest.Get("warnings") is { IsArray: true } mw)
        {
            foreach (var w in mw.AsArray())
            {
                warnings.Add(Esc(c, w));
            }
        }

        var extras = new JsonArray();
        foreach (var e in config.PackExtras(dir, manifest))
        {
            extras.Add(Clone(e, "name", Esc(c, e.Member("name"))));
        }

        var result = new JsonObject
        {
            ["plugin"] = Esc(c, plugin),
            ["marketplace"] = markets is null ? JsonValue.Null : markets,
            ["installed"] = record is not null,
            ["version"] = PluginVersionAt(c, dir, record?.Member("version")),
            ["path"] = dir,
            ["manifest"] = new JsonObject { ["error"] = manifestError, ["warnings"] = warnings },
            ["roles"] = roles,
            ["also_in_plugin"] = extras,
            ["digest"] = tree.Get("error") is { IsTruthy: true } || tree.Get("files") is not { IsArray: true } files
                ? JsonValue.Null
                : config.PackDigest(files.AsArray()),
        };
        if (tree.Get("symlink") is { IsTruthy: true } link)
        {
            result["symlink"] = Esc(c, link);
        }

        // ah2-698 D6: the update role list, doctor and member start last recorded for this install.
        result["updated"] = record is { } seenRec && seenRec.Member("marketplace") is { IsText: true } seenMarket
            ? PackUpdates.Updated(PackUpdates.RecordOf(c, $"{plugin}@{seenMarket.AsString()}"))
            : JsonValue.Null;

        if (c.Opts.IsTrue("json"))
        {
            c.Out(result);
            return 0;
        }

        var lines = new List<string>
        {
            $"{MemberPlanning.JsConvertText(result.Get("plugin"))}{(markets is null ? string.Empty : "@" + markets)} {(result.Get("version") is { IsTruthy: true } v ? MemberPlanning.JsConvertText(v) : "?")} — {dir}{(record is null ? " (not installed)" : string.Empty)}",
            $"digest {MemberPlanning.JsConvertText(result.Get("digest"))}",
        };
        if (result.Get("updated") is { IsMap: true } updatedBlock)
        {
            lines.Add(PackUpdates.Line(updatedBlock));
        }
        if (manifestError.IsMap)
        {
            lines.Add($"manifest: {MemberPlanning.JsConvertText(manifestError.Member("code"))}: {MemberPlanning.JsConvertText(manifestError.Member("message"))}");
        }

        foreach (var w in warnings)
        {
            lines.Add($"manifest warning: {MemberPlanning.JsConvertText(w)}");
        }

        foreach (var r in roles)
        {
            lines.Add($"role {MemberPlanning.JsConvertText(r.Member("name"))}: {JsonText.Stringify(r.Member("fields") ?? JsonValue.Null, 0)}");
            if (r.Member("tools") is { IsMap: true } tools)
            {
                var effective = tools.Member("effective") is { IsArray: true } ef ? string.Join(", ", ef.AsArray().Select(x => MemberPlanning.JsConvertText(x))) : string.Empty;
                var flagged = tools.Member("flagged") is { IsArray: true } fl
                    ? fl.AsArray().Select(x => MemberPlanning.JsConvertText(x)).Where(x => !x.StartsWith(config.McpToolPrefix, StringComparison.Ordinal)).ToList()
                    : [];
                lines.AddRange(config.PackToolLines(tools, dir).Select(l => "  " + Esc(c, l)));
                lines.Add($"  tools: {(effective.Length == 0 ? "(none)" : effective)}{(flagged.Count > 0 ? " — flagged: " + string.Join(", ", flagged) : string.Empty)}");
            }

            foreach (var x in r.Member("findings")!.Value.AsArray())
            {
                lines.Add($"  {MemberPlanning.JsConvertText(x.Member("level")).ToUpperInvariant()} {MemberPlanning.JsConvertText(x.Member("code"))}: {MemberPlanning.JsConvertText(x.Member("message"))}");
            }

            foreach (var w in r.Member("warnings")!.Value.AsArray())
            {
                lines.Add($"  warning: {MemberPlanning.JsConvertText(w)}");
            }

            if (r.Member("adopted") is { IsArray: true } adopted && adopted.AsArray().Count > 0)
            {
                lines.Add("  used by " + string.Join("; ", adopted.AsArray().Select(UsedBy)));
            }
        }

        lines.Add(extras.Count > 0 ? "also in this plugin:" : "also in this plugin: nothing");
        foreach (var e in extras)
        {
            var claims2 = e.Member("claims") is { IsArray: true } cl && cl.AsArray().Count > 0
                ? " — claims role " + string.Join(", ", cl.AsArray().Select(x => MemberPlanning.JsConvertText(x)))
                : string.Empty;
            lines.Add($"  {MemberPlanning.JsConvertText(e.Member("kind"))}: {MemberPlanning.JsConvertText(e.Member("name"))}{claims2}");
        }

        if (result.Get("symlink") is { IsTruthy: true } sym)
        {
            lines.Add($"warning: symbolic link: {MemberPlanning.JsConvertText(sym)}");
        }

        c.Stdout.Write(string.Join('\n', lines) + "\n");
        return 0;
    }

    private static JsonObject Clone(JsonValue source, string key, JsonValue value)
    {
        var o = source.IsMap ? source.AsObject().Clone() : new JsonObject();
        o[key] = value;
        return o;
    }

    private static JsonObject AsWarning(JsonObject finding)
    {
        finding["level"] = "warn";
        return finding;
    }

    private static JsonObject Finding(string level, string code, JsonValue path, string message) => new()
    {
        ["level"] = level,
        ["code"] = code,
        ["path"] = path,
        ["field"] = JsonValue.Null,
        ["message"] = message,
        ["fix"] = new JsonArray(),
    };

    private static JsonObject ShowRole(RosterContext c, string dir, string plugin, JsonValue? record, string role, JsonValue m, JsonObject claims)
    {
        var config = c.Services.Config;
        var fields = m.Member("raw") is { IsMap: true } raw
            ? Escaped(c, raw.AsObject())
            : m.Member("raw") ?? JsonValue.Null;
        var agent = m.Member("agent") is { IsTruthy: true } a ? a : (JsonValue?)null;
        var warnings = new JsonArray();
        if (m.Member("warnings") is { IsArray: true } mw)
        {
            foreach (var w in mw.AsArray())
            {
                warnings.Add(Esc(c, w));
            }
        }

        var findings = new JsonArray();
        var shown = new JsonObject
        {
            ["name"] = Esc(c, role),
            ["fields"] = fields,
            ["agent"] = agent is { } ag ? Esc(c, ag) : JsonValue.Null,
            ["warnings"] = warnings,
            ["findings"] = findings,
        };
        if (m.Member("error") is { IsTruthy: true } error)
        {
            findings.Add(Finding("error", MemberPlanning.JsConvertText(error.Member("code")), JsonValue.Null, Esc(c, error.Member("message"))));
        }

        // ah2-698 D4: name claims, a non-UTF-8 agent file and hidden characters warn; the role stays available.
        if (m.Member("row") is { IsTruthy: true } row)
        {
            if (claims.Get("error") is { IsTruthy: true } claimError)
            {
                findings.Add(Finding("warn", "pack-warning", JsonValue.Null, MemberPlanning.JsConvertText(claimError)));
            }

            if (claims.Get("byRole") is { IsMap: true } byRole && byRole.AsObject().Get(role) is { IsArray: true } claimedBy && claimedBy.AsArray().Count > 0)
            {
                var files = claimedBy.AsArray().Select(x => MemberPlanning.JsConvertText(x)).ToList();
                findings.Add(
                    Finding(
                        "warn",
                        "pack-warning",
                        NodePath.Join(dir, files[0]),
                        config.PackClaimMessage(plugin.Length > 0 ? plugin : "?", MemberPlanning.JsConvertText(agent), files)));
            }

            var agentPath = NodePath.Join(dir, "agents", $"{MemberPlanning.JsConvertText(agent)}.md");
            string? text = null;
            try
            {
                text = new System.Text.UTF8Encoding(false, true).GetString(File.ReadAllBytes(agentPath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                findings.Add(
                    File.Exists(agentPath)
                        ? Finding("warn", "pack-warning", agentPath, $"{agentPath} is not valid UTF-8")
                        : Finding("error", "pack-invalid", agentPath, $"no agents/{MemberPlanning.JsConvertText(agent)}.md"));
            }

            if (text is not null)
            {
                if (config.HiddenCharAt(text) is { IsTruthy: true } hidden)
                {
                    findings.Add(Finding("warn", "pack-hidden-chars", agentPath, $"{agentPath} holds a hidden character ({MemberPlanning.JsConvertText(hidden)})"));
                }

                // adr-47: an installed pack's agent file is taken as written, so its parse and contract findings are warnings.
                var parsed = config.PackAgentParse(text, agentPath);
                if (parsed.Get("findings") is { IsArray: true } pf)
                {
                    foreach (var x in pf.AsArray())
                    {
                        findings.Add(AsWarning(Clone(x, "message", Esc(c, x.Member("message")))));
                    }
                }

                var spec = new JsonObject
                {
                    ["role"] = role,
                    ["cls"] = row.Member("class") ?? JsonValue.Null,
                    ["agent"] = row.Member("agent") ?? JsonValue.Null,
                    ["description"] = row.Member("description") is { IsTruthy: true } d ? d : JsonValue.Null,
                    ["cwd"] = c.Cwd,
                    ["inMemory"] = new JsonObject
                    {
                        ["path"] = agentPath,
                        ["text"] = text,
                        ["fm"] = parsed.Get("fm") ?? JsonValue.Null,
                    },
                };
                var v = config.ValidateAgentContract(spec);
                if (v.Get("findings") is { IsArray: true } vf)
                {
                    foreach (var x in vf.AsArray())
                    {
                        findings.Add(AsWarning(Clone(x, "message", Esc(c, x.Member("message")))));
                    }
                }

                shown["tools"] = config.PackToolReport(parsed.Get("fm"), dir);
            }
        }

        if (record is { } rec)
        {
            var market = rec.Member("marketplace") is { IsNull: false } mk ? JsConvert.ToJsString(mk) : "null";
            shown["adopted"] = AdoptionsOf(c, $"{MemberPlanning.JsConvertText(rec.Member("plugin"))}@{market}:{role}");
        }

        return shown;
    }

    private static JsonObject Escaped(RosterContext c, JsonObject raw)
    {
        var o = new JsonObject();
        foreach (var key in raw.Keys)
        {
            o[key] = raw[key] is { IsText: true } t ? Esc(c, t) : raw[key];
        }

        return o;
    }
}
