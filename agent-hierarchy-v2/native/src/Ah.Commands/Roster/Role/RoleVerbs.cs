using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>
/// <c>role list|set|remove</c>: the custom roles of a level file, and the built-ins' agent overrides. A role pack's roles come from a
/// roster's packs (adr-46); <c>role trust</c> and <c>role set --from</c> only point there.
/// </summary>
internal static class RoleVerbs
{
    private static readonly string[] Flags =
    [
        "class", "agent", "label", "description", "routes", "model", "dispatch", "shell", "params", "writes", "level", "scaffold",
        "dry-run", "json", "roster", "cwd",
    ];

    private const string Usage =
        "usage: ah roster role list [--json] [--roster <r>] | role set <name> [--class C] [--agent A] [--label L] [--description D] [--routes R] [--model M] [--dispatch peer|model] [--shell self-care] [--params a,b] [--writes glob,…] [--level L] [--scaffold repo|user] [--dry-run] | role remove <name> [--level L]  (all with --cwd <abs cwd>)";

    private const string TrustRemoved =
        "role trust was removed: an installed role pack is trusted, and its updates are reported in role list (adr-46)";

    private const string FromRemoved =
        "pack roles come from a roster's packs: ah roster roster pack <roster> --add <plugin>@<marketplace> (or init --pack)";

    [Verb("roster:role")]
    internal static int Role(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        var sub = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        var name = c.Opts.Positionals.Count > 1 ? c.Opts.Positionals[1] : null;

        // ah2-698 D5: the removed trust verbs answer with the command that replaces them, whatever else was passed.
        if (sub == "trust")
        {
            throw new RosterFailure(TrustRemoved);
        }

        if (sub == "set" && c.Opts.Has("from"))
        {
            throw new RosterFailure(FromRemoved);
        }

        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure(
                    $"role: unrecognized flag --{key} (use --class, --agent, --label, --description, --routes, --model, --dispatch, --shell, --params, --writes, --level, --scaffold, --dry-run, --json, --roster, --team, --cwd)");
            }
        }

        if (sub is "set" or "remove")
        {
            OwnerGuard.RefuseAgentRoleWrite(c, sub);
        }

        var ctx = new RoleRun(c);
        switch (sub)
        {
            case "list":
                return ctx.List();
            case "set":
                c.Out(ctx.Set(name));
                return 0;
            case "remove":
                c.Out(ctx.Remove(name));
                return 0;
            default:
                throw new RosterFailure(Usage);
        }
    }

    private sealed class RoleRun(RosterContext c)
    {
        private readonly LevelFiles _files = new(c);
        private readonly Ah.Config.IConfigCore _config = c.Services.Config;

        private JsonObject Reg => c.Registry;

        private JsonObject Roles => Reg.Get("roles") is { IsMap: true } r ? r.AsObject() : new JsonObject();

        private JsonObject Sources => Reg.Get("sources") is { IsMap: true } r ? r.AsObject() : new JsonObject();

        private static string T(JsonValue? v) => MemberPlanning.JsConvertText(v);

        private static bool IsText(JsonValue? v, string text) => v is { IsText: true } x && x.AsString() == text;

        private string Esc(JsonValue? v) => _config.EscapeTerminal(v);

        private string? OptText(string key) => c.Opts.Text(key);

        /// <summary><c>--key</c> was given without a value.</summary>
        private bool Valueless(string key) => c.Opts.IsTrue(key);

        // ---------------------------------------------------------------- list

        private JsonArray Rows()
        {
            var rows = new JsonArray();
            var classes = _config.Classes;
            var updates = PackUpdates.Check(c, Reg);
            foreach (var role in _config.RegistryRoles(Reg))
            {
                var entry = Roles.Get(role) ?? new JsonObject();
                var builtin = _config.IsBuiltinRole(role);
                var cls = _config.RoleClass(role, Reg) ?? string.Empty;
                JsonValue v;
                try
                {
                    v = _config.ValidateRole(role, Reg);
                }
                catch (InvalidOperationException e)
                {
                    v = new JsonObject
                    {
                        ["findings"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["level"] = "error",
                                ["code"] = "agent-not-found",
                                ["path"] = JsonValue.Null,
                                ["field"] = JsonValue.Null,
                                ["message"] = e.Message,
                                ["fix"] = new JsonArray { new JsonObject { ["kind"] = "create-file", ["detail"] = "check the agent file" } },
                            },
                        },
                        ["description"] = new JsonObject { ["text"] = JsonValue.Null, ["source"] = JsonValue.Null },
                        ["file"] = new JsonObject { ["found"] = JsonValue.Null },
                    };
                }

                var validated = v.IsMap;
                var findings = validated && v.Member("findings") is { IsArray: true } f ? f.AsArray() : new JsonArray();
                var errors = findings.Count(x => IsText(x.Member("level"), "error"));
                var warns = findings.Count - errors;
                string status;
                if (!validated)
                {
                    status = "shipped";
                }
                else if (errors > 0)
                {
                    status = builtin ? $"UNAVAILABLE → reverted to {_config.Qualified(role)}" : $"UNAVAILABLE: {errors} error{(errors > 1 ? "s" : string.Empty)}";
                }
                else
                {
                    status = warns > 0 ? $"{warns} warning{(warns > 1 ? "s" : string.Empty)}" : "ok";
                }

                var clsRow = classes.Get(cls) is { IsMap: true } cr ? cr : (JsonValue?)null;
                var chain = _config.ClassProp(role, Reg, "chain") is { IsBool: true } cb && cb.AsBool();
                string placement;
                if (!chain)
                {
                    placement = "legwork";
                }
                else if (_config.IsAlternative(role, entry) is { IsTruthy: true })
                {
                    var built = _config.ClassBuiltin(cls);
                    var label = built is not null && _config.RoleLabels.Get(built) is { } l ? T(l) : "undefined";
                    placement = $"alt. to {label}: {T(entry.Member("routes"))}";
                }
                else
                {
                    placement = builtin ? $"built-in · {T(clsRow?.Member("step"))}" : "side";
                }

                var row = new JsonObject
                {
                    ["name"] = role,
                    ["builtin"] = builtin,
                    ["class"] = cls,
                    ["label"] = _config.RoleLabel(role, Reg),
                    ["agent"] = _config.RoleAgent(role, entry),
                    ["model"] = entry.Member("model") ?? JsonValue.Null,
                    ["level"] = PackVerbs.LevelOfScope(Sources.Get(role)),
                    ["placement"] = placement,
                    ["description"] = validated && v.Member("description")?.Member("text") is { IsTruthy: true } dt ? dt : JsonValue.Null,
                    ["description_source"] = validated && v.Member("description")?.Member("source") is { IsTruthy: true } ds ? ds : JsonValue.Null,
                    ["status"] = status,
                    ["path"] = validated && v.Member("file")?.Member("found") is { IsTruthy: true } fp ? fp : JsonValue.Null,
                    ["findings"] = findings,
                };
                if (entry.Member("params") is { IsArray: true } declared)
                {
                    row["params"] = declared;
                }

                if (entry.Member("from") is { IsTruthy: true } from)
                {
                    row["from"] = from;
                    if (entry.Member("pack")?.Member("via") is { IsText: true } rowVia && rowVia.AsString().StartsWith("row:", StringComparison.Ordinal))
                    {
                        row["legacy"] = true;
                    }

                    if (builtin)
                    {
                        row["pack_override"] = true;
                    }
                }

                // ah2-698 D3: roster:<name> for a role a roster's packs supply, row:<level> for a config row with `from`.
                if (entry.Member("pack")?.Member("via") is { IsText: true } via)
                {
                    // ah2-698 D6: the pack's last reported update, or null.
                    var updated = PackUpdates.PackKeyOf(entry) is { } key && updates.TryGetValue(key, out var u) ? u : JsonValue.Null;
                    row["pack"] = new JsonObject { ["via"] = via, ["updated"] = updated };
                }

                rows.Add(row);
            }

            return rows;
        }

        internal int List()
        {
            var roles = Rows();
            var excluded = new JsonArray();
            if (Reg.Get("excludedRoles") is { IsArray: true } ex)
            {
                foreach (var e in ex.AsArray())
                {
                    excluded.Add(new JsonObject
                    {
                        ["name"] = e.Member("name") ?? JsonValue.Null,
                        ["level"] = PackVerbs.LevelOfScope(e.Member("scope")),
                        ["path"] = e.Member("path") ?? JsonValue.Null,
                        ["reason"] = e.Member("reason") ?? JsonValue.Null,
                    });
                }
            }

            if (c.Opts.IsTrue("json"))
            {
                c.Out(new JsonObject { ["roles"] = roles, ["excluded"] = excluded });
                return 0;
            }

            var lines = new List<string>();
            foreach (var r in roles)
            {
                var from = r.Member("from") is { IsTruthy: true };
                var description = r.Member("description") is { IsTruthy: true } d
                    ? $" — \"{(from ? Esc(d) : T(d))}\" ({T(r.Member("description_source"))})"
                    : string.Empty;
                var adopted = from ? $" — from {T(r.Member("from"))}" : string.Empty;
                var declared = r.Member("params") is { IsArray: true } p ? $" — params: {string.Join(", ", p.AsArray().Select(x => T(x)))}" : string.Empty;
                declared += r.Member("pack")?.Member("updated") is { IsMap: true } up ? $" — {PackUpdates.Line(up)}" : string.Empty;
                lines.Add(
                    $"{T(r.Member("name")),-20} {(r.Member("builtin") is { IsBool: true } b && b.AsBool() ? "built-in" : "custom  ")} {T(r.Member("class")),-9} {T(r.Member("agent")),-26} {T(r.Member("model")),-8} {T(r.Member("level")),-9} {T(r.Member("placement"))} — {T(r.Member("status"))}{description}{adopted}{declared}");
            }

            foreach (var e in excluded)
            {
                lines.Add($"EXCLUDED {T(e.Member("name"))} ({T(e.Member("level"))}, {T(e.Member("path"))}): {T(e.Member("reason"))}");
            }

            c.Stdout.Write(string.Join('\n', lines) + "\n");
            return 0;
        }

        // ---------------------------------------------------------------- shared

        private string RequireLevel(string level) => _files.RequireLevel(level);

        private static JsonObject? Layer(JsonObject data) =>
            data.Get("roles") is { IsMap: true } r ? r.AsObject() : null;

        private static JsonObject? RowAt(JsonObject? layer, string name) =>
            layer?.Get(name) is { IsMap: true } row ? row.AsObject() : null;

        private JsonValue ConfigVersion => _config.ConfigVersion;

        private void WriteRoles(string path, JsonObject data, JsonObject roles)
        {
            var next = data.Clone();
            if (!(next.Get("version") is { IsTruthy: true }))
            {
                next["version"] = ConfigVersion;
            }

            next["roles"] = roles;
            _files.Write(path, next);
        }

        private void CheckDispatch(string name, JsonObject row)
        {
            if (RoleRowChecks.DispatchError(_config, row) is { } why)
            {
                throw new RosterFailure($"role set {name}: {why}");
            }
        }

        private void CheckModelAllowed(string name, string cls, JsonValue model)
        {
            if (RoleRowChecks.ModelError(_config, name, cls, model) is { } why)
            {
                throw new RosterFailure($"role set {name}: {why}");
            }
        }

        private void CheckOwner(string name, string agent)
        {
            if (RoleRowChecks.OwnerError(_config, Reg, name, agent) is { } why)
            {
                throw new RosterFailure($"role set {name}: {why}");
            }
        }

        // ---------------------------------------------------------------- set

        internal JsonValue Set(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new RosterFailure("role set needs a role name: role set <name> [flags]");
            }

            var opts = c.Opts;
            var builtin = _config.IsBuiltinRole(name);
            var dry = opts.IsTrue("dry-run");
            foreach (var k in new[] { "class", "agent", "label", "description", "routes", "model", "dispatch", "shell", "params", "writes", "scaffold", "level" })
            {
                if (Valueless(k))
                {
                    throw new RosterFailure($"role set: --{k} needs a value");
                }
            }

            // ah2-562: a review-class role's write folders; "" deletes the key. The class is checked once the row is known.
            JsonArray? writes = null;
            if (OptText("writes") is { Length: > 0 } writesText)
            {
                writes = [.. writesText.Split(',', StringSplitOptions.TrimEntries).Select(JsonValue.FromString)];
                if (_config.WritesError(writes) is { } writesWhy)
                {
                    throw new RosterFailure($"role set {name}: --writes: {writesWhy}");
                }
            }

            // a built-in may declare params too (role-params Amendment 1, Q2); "" deletes the key
            JsonArray? declared = null;
            if (OptText("params") is { Length: > 0 } paramsText)
            {
                declared = [.. paramsText.Split(',', StringSplitOptions.TrimEntries).Select(JsonValue.FromString)];
                if (_config.ParamsError(declared) is { } paramsWhy)
                {
                    throw new RosterFailure($"role set {name}: --params: {paramsWhy}");
                }
            }

            if (builtin)
            {
                foreach (var k in RoleRowChecks.NotOnBuiltin)
                {
                    if (opts.Has(k))
                    {
                        throw new RosterFailure($"role set {name}: {RoleRowChecks.NotOnBuiltinError(k)}");
                    }
                }
            }

            var shell = OptText("shell");
            if (shell is not null && shell != _config.ShellSelfCare)
            {
                throw new RosterFailure($"role set {name}: --shell must be {_config.ShellSelfCare}");
            }

            var defined = PackVerbs.LevelOfScope(Sources.Get(name));
            var packSupplied = RosterOf(name) is not null;
            var scaffoldFlag = OptText("scaffold");
            string level;
            if (OptText("level") is { } given)
            {
                level = RequireLevel(given);
            }
            else if (!packSupplied && defined is { IsText: true } dl && dl.AsString() != "shipped")
            {
                level = dl.AsString();
            }
            else if (packSupplied && LevelWithRow(name) is { } held)
            {
                // ah2-698 Y11: the level that already holds the user's row, as role remove picks it.
                level = held;
            }
            else
            {
                level = scaffoldFlag == "repo" ? "repo" : "global";
            }

            var path = _files.PathOf(level);
            var data = _files.Read(path);
            var layer = Layer(data);
            var atLevel = RowAt(layer, name);
            if (atLevel is not null && _config.IsFromRow(atLevel))
            {
                return SetAdoptedFields(name, level, path, data, layer, atLevel, T(atLevel.Get("from")), dry, builtin);
            }

            // ah2-698 Y1: a role a roster's pack supplies gets an ordinary row of the user's own fields; the pack still supplies agent and class.
            if (packSupplied)
            {
                return SetAdoptedFields(name, level, path, data, layer, atLevel, T(Roles.Get(name)?.Member("from")), dry, builtin);
            }

            if (atLevel is null && Roles.Get(name) is { IsMap: true } existing && existing.Member("from") is { IsTruthy: true } adopted)
            {
                throw new RosterFailure(
                    $"role set {name}: it is adopted from a pack ({T(adopted)}) at level \"{T(defined)}\" — edit it there with --level {T(defined)}");
            }

            var row = atLevel is not null
                ? atLevel.Clone()
                : Roles.Get(name) is { IsMap: true } seeded ? SeedRow(name, seeded.AsObject()) : new JsonObject();
            foreach (var k in new[] { "class", "agent", "label", "model", "dispatch" })
            {
                if (OptText(k) is { } v)
                {
                    row[k] = v;
                }
            }

            if (shell is not null)
            {
                row["shell"] = shell;
            }

            if (declared is not null)
            {
                row["params"] = declared;
            }
            else if (OptText("params") is not null)
            {
                row.Remove("params");
            }

            if (writes is not null)
            {
                row["writes"] = writes;
            }
            else if (OptText("writes") is not null)
            {
                row.Remove("writes");
            }

            foreach (var k in new[] { "description", "routes" })
            {
                if (OptText(k) is not { } v)
                {
                    continue;
                }

                if (v.Length == 0)
                {
                    row.Remove(k);
                }
                else
                {
                    row[k] = v;
                }
            }

            CheckDispatch(name, row);

            var warnings = new List<string>();
            string cls;
            JsonObject checkedRow;
            if (builtin)
            {
                cls = _config.RoleClass(name, null) ?? string.Empty;
                if (row.Get("agent") is { } agentValue && RoleRowChecks.BuiltinAgentError(_config, name, agentValue) is { } why)
                {
                    throw new RosterFailure($"role set {name}: {why}");
                }

                if (row.Get("model") is { } model)
                {
                    CheckModelAllowed(name, cls, model);
                }

                checkedRow = row;
            }
            else
            {
                if (!(row.Get("class") is { IsTruthy: true }))
                {
                    throw new RosterFailure($"role set {name}: --class is required for a new role (one of {string.Join(", ", _config.Classes.Keys)})");
                }

                if (OptText("writes") is not null && T(row.Get("class")) != "review")
                {
                    throw new RosterFailure($"role set {name}: --writes applies only to a review-class role");
                }

                if (writes?.Select(g => g.AsString()).FirstOrDefault(_config.WriteGlobMatchesAll) is { } all)
                {
                    warnings.Add($"--writes \"{all}\" matches every path: {name} can then write anywhere in the checkout");
                }

                var check = _config.CheckCustomRow(name, row);
                if (check.Get("error") is { IsTruthy: true } error)
                {
                    throw new RosterFailure($"role set {name}: {T(error)}");
                }

                if (check.Get("warnings") is { IsArray: true } cw)
                {
                    warnings.AddRange(cw.AsArray().Select(x => T(x)));
                }

                var chainClass = _config.Classes.Get(T(row.Get("class"))) is { IsMap: true } cc && cc.Member("chain") is { IsBool: true } cb && cb.AsBool();
                if (row.Get("routes") is not null && !chainClass)
                {
                    row.Remove("routes");
                }

                cls = T(row.Get("class"));
                checkedRow = check.Get("row") is { IsMap: true } rr ? rr.AsObject() : row;
            }

            if (RoleRowChecks.DispatchModelError(OptText("dispatch"), cls) is { } dispatchWhy)
            {
                throw new RosterFailure($"role set {name}: {dispatchWhy}");
            }

            var agent = builtin ? _config.RoleAgent(name, row) : T(checkedRow.Get("agent"));
            CheckOwner(name, agent);

            if (!builtin)
            {
                var overlay = Roles.Clone();
                overlay[name] = checkedRow;
                foreach (var (team, prefix) in Prefixes())
                {
                    var v = _config.ValidateTeamAlias(JsonValue.FromString(prefix), new JsonObject { ["roles"] = overlay });
                    if (!(v.Get("ok") is { IsTruthy: true }))
                    {
                        throw new RosterFailure(
                            $"role set {name}: {(team is null ? "the team prefix" : $"team {team}'s prefix")} \"{prefix}\" collides with this role name ({T(v.Get("why"))}) — rename the role, or create the team with a different `--team <name>`");
                    }
                }

                if (checkedRow.Get("routes") is { IsTruthy: true })
                {
                    var rival = _config.CustomRoleNames(Reg).FirstOrDefault(r =>
                        r != name && Roles.Get(r) is { IsMap: true } re && IsText(re.Member("class"), cls) && re.Member("routes") is { IsTruthy: true });
                    if (rival is not null)
                    {
                        warnings.Add($"class {cls} already has an alternative ({rival}); candidates are tried in name order and the first fit wins");
                    }
                }
            }

            var located = agent.Contains(':', StringComparison.Ordinal) ? null : _config.LocateAgentFile(JsonValue.FromString(agent), c.Cwd);
            if (!builtin)
            {
                foreach (var (team, prefix) in Prefixes())
                {
                    var peerName = $"{prefix}-{name}";
                    if (!(_config.ValidateHerdrName(JsonValue.FromString(peerName)).Get("ok") is { IsTruthy: true }))
                    {
                        warnings.Add(
                            $"under {(team is null ? "the team prefix" : $"team {team}'s prefix")} \"{prefix}\" its peer name, {peerName}, is {peerName.Length} characters — Herdr allows at most 32 ([a-z][a-z0-9_-]), so spawning it under Herdr is refused; use a shorter role name, or create the team with a shorter `--team <name>`");
                    }
                }
            }

            if (level == "repo" && located is not null && IsText(located.Get("level"), "user"))
            {
                warnings.Add($"this repo-level row points at {T(located.Get("path"))}, which exists only in your user agents dir — other users of this repo will not have it");
            }

            (string Path, string Text)? scaffold = null;
            if (scaffoldFlag is not null)
            {
                if (scaffoldFlag is not ("repo" or "user"))
                {
                    throw new RosterFailure("role set: --scaffold must be repo or user");
                }

                if (agent.Contains(':', StringComparison.Ordinal))
                {
                    throw new RosterFailure($"role set {name}: --scaffold cannot write a plugin agent's file ({agent})");
                }

                var root = _config.FindGitRoot(c.Cwd);
                if (scaffoldFlag == "repo" && root is null)
                {
                    throw new RosterFailure("role set: --scaffold repo needs a git repo");
                }

                var file = scaffoldFlag == "repo"
                    ? NodePath.Join(root!, ".claude", "agents", $"{agent}.md")
                    : NodePath.Join(LifecyclePermissions.ClaudeDir(c.Services.Environment), "agents", $"{agent}.md");
                if (c.Services.Files.Exists(file))
                {
                    throw new RosterFailure($"role set {name}: --scaffold refuses to overwrite {file}");
                }

                var label = T(checkedRow.Get("label"));
                var routesText = checkedRow.Get("routes") is { IsTruthy: true } rt ? T(rt) : null;
                var description = checkedRow.Get("description") is { IsTruthy: true } dd
                    ? T(dd)
                    : routesText is not null ? $"{label} — {routesText}" : null;
                if (description is null)
                {
                    throw new RosterFailure($"role set {name}: --scaffold needs a description — give --description or --routes");
                }

                scaffold = (file, ScaffoldText(agent, cls, label, description, routesText));
            }

            var needsContract = !builtin || _config.IsOverride(name, row);
            JsonValue? Validate(bool inMemory)
            {
                if (!needsContract)
                {
                    return null;
                }

                var spec = new JsonObject
                {
                    ["role"] = name,
                    ["cls"] = cls,
                    ["agent"] = agent,
                    ["description"] = builtin ? JsonValue.Null : checkedRow.Get("description") is { IsTruthy: true } d ? d : JsonValue.Null,
                    ["builtin"] = builtin,
                    ["cwd"] = c.Cwd,
                };
                if (inMemory && scaffold is { } sc)
                {
                    spec["inMemory"] = new JsonObject
                    {
                        ["path"] = sc.Path,
                        ["text"] = sc.Text,
                        ["fm"] = _config.ParseAgentFrontmatter(sc.Text),
                    };
                }

                return _config.ValidateAgentContract(spec);
            }

            JsonObject Report(JsonValue? v)
            {
                var findings = v is { IsMap: true } vm && vm.Member("findings") is { IsArray: true } fa ? fa.AsArray() : new JsonArray();
                if (findings.Count > 0)
                {
                    c.Stderr.Write($"{_config.FormatFindings(findings)}\n");
                }

                foreach (var w in warnings)
                {
                    c.Warn(w);
                }

                var warningArray = new JsonArray();
                foreach (var w in warnings)
                {
                    warningArray.Add(w);
                }

                var found = v is { IsMap: true } vv && vv.Member("file")?.Member("found") is { IsTruthy: true } ff ? ff : (JsonValue?)null;
                JsonValue agentFile = JsonValue.Null;
                if (v is { IsMap: true })
                {
                    agentFile = new JsonObject
                    {
                        ["path"] = found ?? JsonValue.Null,
                        ["status"] = scaffold is not null && dry ? "scaffold (not written)" : found is not null ? "found" : "not found",
                    };
                }

                return new JsonObject
                {
                    ["level"] = level,
                    ["path"] = path,
                    ["role"] = name,
                    ["row"] = row,
                    ["agent_file"] = agentFile,
                    ["description"] = v is { IsMap: true } dv && dv.Member("description") is { } desc ? desc : JsonValue.Null,
                    ["findings"] = findings,
                    ["warnings"] = warningArray,
                    ["scaffold"] = scaffold is { } s2 ? s2.Path : JsonValue.Null,
                };
            }

            if (dry)
            {
                var dryResult = new JsonObject { ["dry_run"] = true };
                RosterJson.Spread(dryResult, Report(Validate(true)));
                return dryResult;
            }

            if (scaffold is { } written)
            {
                LevelFiles.WriteAtomically(written.Path, written.Text);
            }

            void DropScaffold()
            {
                if (scaffold is not { } s)
                {
                    return;
                }

                try
                {
                    File.Delete(s.Path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the file was not there to take back
                }
            }

            JsonValue? validated;
            try
            {
                validated = Validate(false);
            }
            catch
            {
                DropScaffold();
                throw;
            }

            var result = Report(validated);
            if (validated is { IsMap: true } vd && _config.HasContractErrors(vd.Member("findings")))
            {
                DropScaffold();
                throw new RosterFailure($"role set {name}: the agent file fails the {cls} contract — nothing was written (findings above)");
            }

            try
            {
                WriteRoles(path, data, With(layer, name, row));
            }
            catch
            {
                DropScaffold();
                throw;
            }

            var done = new JsonObject { ["written"] = true };
            RosterJson.Spread(done, result);
            return done;
        }

        private static JsonObject With(JsonObject? layer, string name, JsonObject row)
        {
            var next = layer is null ? new JsonObject() : layer.Clone();
            next[name] = row;
            return next;
        }

        private List<(string? Team, string Prefix)> Prefixes()
        {
            var dir = c.Dir;
            var owned = c.Services.Roster.OwnedTeams(dir, c.Invoker());
            if (owned.Count > 1)
            {
                return owned.Select(t => ((string?)Ah.Roster.RosterModule.TeamArgName(t), _config.TeamPrefix(c.Cwd, t))).ToList();
            }

            return [(null, owned.Count > 0 ? _config.TeamPrefix(c.Cwd, owned[0]) : _config.TeamPrefix(c.Cwd, null))];
        }

        /// <summary>The row to seed <c>role set</c> from when its level has none: the effective row, minus defaults.</summary>
        private JsonObject SeedRow(string name, JsonObject entry)
        {
            var seed = new JsonObject();
            var builtin = _config.IsBuiltinRole(name);
            foreach (var key in entry.Keys)
            {
                var v = entry[key];
                if (key is "effectiveDescription" or "pack")
                {
                    continue;
                }

                if (key == "dispatch" && IsText(v, "peer"))
                {
                    continue;
                }

                if (key == "peer" && IsText(v, "auto"))
                {
                    continue;
                }

                if (!builtin && key == "agent" && IsText(v, name))
                {
                    continue;
                }

                if (!builtin && key == "label" && IsText(v, _config.DefaultLabel(name)))
                {
                    continue;
                }

                seed[key] = v.IsArray ? CopyArray(v.AsArray()) : v;
            }

            return seed;
        }

        /// <summary>A minimal agent file that passes its class contract: no tools allowlist, and the class's forbidden plus discouraged tools disallowed.</summary>
        private string ScaffoldText(string agent, string cls, string label, string description, string? routes)
        {
            var cr = _config.Classes.Get(cls);
            var disallowed = new List<string>();
            if (cr is { IsMap: true } row && row.Member("contract") is { IsMap: true } contract)
            {
                foreach (var key in new[] { "forbidden", "discouraged" })
                {
                    if (contract.Member(key) is { IsArray: true } list)
                    {
                        disallowed.AddRange(list.AsArray().Select(x => T(x)));
                    }
                }
            }

            var builtin = _config.ClassBuiltin(cls);
            var standIn = routes is not null
                ? $", standing in for the {(builtin is not null && _config.RoleLabels.Get(builtin) is { } l ? T(l) : "undefined")} for: {routes}"
                : string.Empty;
            var lines = new List<string>
            {
                "---",
                $"name: {agent}",
                $"description: {JsonText.Quote(description)}",
            };
            if (disallowed.Count > 0)
            {
                lines.Add($"disallowedTools: {string.Join(", ", disallowed)}");
            }

            lines.AddRange(
            [
                "---",
                string.Empty,
                $"You are the {label}, an agent-hierarchy role of class {cls}{standIn}. The hierarchy contract you receive at session or subagent start governs your protocol; this file adds your specialisation.",
                string.Empty,
                "## Specialisation",
                string.Empty,
                "<!-- Replace this line with what this role specialises in. -->",
                string.Empty,
            ]);
            return string.Join('\n', lines);
        }

        // ---------------------------------------------------------------- remove

        internal JsonValue Remove(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new RosterFailure("role remove needs a role name");
            }

            var builtin = _config.IsBuiltinRole(name);
            var excluded = Reg.Get("excludedRoles") is { IsArray: true } ex
                ? ex.AsArray().FirstOrDefault(e => IsText(e.Member("name"), name))
                : default;
            var scope = Sources.Get(name) is { IsTruthy: true } s ? s : excluded.IsMap ? excluded.Member("scope") : null;
            var defined = PackVerbs.LevelOfScope(scope);
            if (RosterOf(name) is { } roster)
            {
                return RemovePackSupplied(name, roster);
            }

            string level;
            if (OptText("level") is { } given)
            {
                level = RequireLevel(given);
            }
            else if (defined is { IsText: true } dl && dl.AsString() != "shipped")
            {
                level = dl.AsString();
            }
            else
            {
                throw new RosterFailure($"role remove {name}: it is not defined at any level");
            }

            if (!builtin)
            {
                var users = new List<string>();
                var paths = _config.RosterLevelPaths(c.Cwd);
                foreach (var lvl in paths.Keys)
                {
                    var p = T(paths[lvl]);
                    var d = _files.Read(p);
                    foreach (var triple in _config.RosterBlocksOf(d))
                    {
                        var label = T(triple.AsArray()[0]);
                        var block = triple.AsArray()[2];
                        if (!block.IsMap || block.Member("members") is not { IsArray: true } members)
                        {
                            continue;
                        }

                        var i = 0;
                        foreach (var m in members.AsArray())
                        {
                            i++;
                            if (m.IsMap && IsText(m.Member("role"), name))
                            {
                                users.Add($"{lvl} {label} member {i} ({name})");
                            }
                        }
                    }
                }

                if (users.Count > 0)
                {
                    throw new RosterFailure($"role remove {name}: roster members still use it — {string.Join("; ", users)}. Remove them first (/{_config.PluginName}:agent-roster).");
                }
            }

            var path = _files.PathOf(level);
            var data = _files.Read(path);
            var layer = Layer(data);
            if (layer is null || !(layer.Get(name) is { IsTruthy: true }))
            {
                throw new RosterFailure($"role remove {name}: no row for it at level \"{level}\" ({path})");
            }

            var layerNext = layer.Clone();
            var formerCustom = builtin && Ah.Config.ConfigModule.BuiltinSince(name) is not null && RowAt(layerNext, name)!.Get("class") is not null;
            if (formerCustom)
            {
                // A model, dispatch or peer stored beside the old custom keys is a legitimate built-in override and stays.
                var kept = RowAt(layerNext, name)!.Clone();
                foreach (var key in new[] { "class", "label", "description", "routes", "agent", "from", "pin", "slots" })
                {
                    kept.Remove(key);
                }

                // An empty row would still shadow a wider level's row for this role under whole-row precedence.
                if (kept.Count == 0)
                {
                    layerNext.Remove(name);
                }
                else
                {
                    layerNext[name] = kept;
                }
            }
            else if (builtin)
            {
                var row = RowAt(layerNext, name)!.Clone();
                var adopted = _config.IsFromRow(row);
                if (row.Get("agent") is null && !adopted)
                {
                    throw new RosterFailure(
                        $"role remove {name}: a built-in role cannot be removed, and it has no agent override at level \"{level}\"");
                }

                row.Remove("agent");
                if (adopted)
                {
                    row.Remove("from");
                    row.Remove("pin");
                    row.Remove("slots");
                }

                // An empty row would still shadow a wider level's row for this role under whole-row precedence.
                if (row.Count == 0)
                {
                    layerNext.Remove(name);
                }
                else
                {
                    layerNext[name] = row;
                }
            }
            else
            {
                layerNext.Remove(name);
            }

            var write = data.Clone();
            write["roles"] = layerNext;
            _files.Write(path, write);
            return new JsonObject { ["removed"] = name, ["level"] = level, ["path"] = path, ["override_only"] = builtin && !formerCustom };
        }

        /// <summary>The most specific level whose file holds a row for <paramref name="name"/> (the one in effect), or null.</summary>
        private string? LevelWithRow(string name)
        {
            // ponytail: reads every level file; a narrower row elsewhere needs --level.
            var paths = _config.RosterLevelPaths(c.Cwd);
            return paths.Keys.LastOrDefault(l => RowAt(Layer(_files.Read(T(paths[l]))), name) is not null);
        }

        /// <summary>
        /// <c>role remove</c> on a role roster <paramref name="roster"/>'s pack supplies (ah2-698 Y2, Y3): the user's row goes, at
        /// <c>--level</c> or else at the most specific level that has one; members keep the role, since the pack still supplies it.
        /// </summary>
        private JsonObject RemovePackSupplied(string name, string roster)
        {
            var level = OptText("level") is { } given ? RequireLevel(given) : LevelWithRow(name);
            var path = level is null ? null : _files.PathOf(level);
            var data = path is null ? null : _files.Read(path);
            if (data is null || Layer(data) is not { } layer || RowAt(layer, name) is null)
            {
                var pack = PackUpdates.PackKeyOf(Roles.Get(name)) ?? "<plugin>@<marketplace>";
                throw new RosterFailure(
                    $"role remove {name}: no config row for it{(level is null ? string.Empty : $" at level \"{level}\" ({path})")} to remove — roster {roster}'s pack supplies it; to drop the pack: ah roster roster pack {roster} --remove {pack}");
            }

            var layerNext = layer.Clone();
            var kept = RowAt(layerNext, name)!.Clone();
            var legacyBuiltin = _config.IsBuiltinRole(name) && _config.IsFromRow(kept);
            if (legacyBuiltin)
            {
                // A legacy built-in row's model, dispatch and peer still apply under the pack (OverDefaults), so only its pack keys go.
                foreach (var key in new[] { "agent", "from", "pin", "slots" })
                {
                    kept.Remove(key);
                }
            }

            // An empty row would still shadow a wider level's row for this role under whole-row precedence.
            if (!legacyBuiltin || kept.Count == 0)
            {
                layerNext.Remove(name);
            }
            else
            {
                layerNext[name] = kept;
            }

            var write = data.Clone();
            write["roles"] = layerNext;
            _files.Write(path!, write);
            return new JsonObject { ["removed"] = name, ["level"] = level!, ["path"] = path!, ["override_only"] = false };
        }

        /// <summary>The roster whose packs supply <paramref name="name"/> (ah2-698 D3: its source is <c>roster:&lt;name&gt;</c>), or null.</summary>
        private string? RosterOf(string name) =>
            Sources.Get(name) is { IsText: true } s && s.AsString().StartsWith("roster:", StringComparison.Ordinal) ? s.AsString()["roster:".Length..] : null;

        /// <summary>
        /// <c>role set</c> on a row already adopted from a pack, or on a role a roster's pack supplies (<paramref name="atLevel"/> then
        /// the user's plain row or null): only the user's own fields change; a pin stays, and none is added.
        /// </summary>
        private JsonValue SetAdoptedFields(string name, string level, string path, JsonObject data, JsonObject? layer, JsonObject? atLevel, string from, bool dry, bool builtin)
        {
            var opts = c.Opts;
            foreach (var k in new[] { "class", "agent", "scaffold", "shell" })
            {
                if (opts.Has(k))
                {
                    throw new RosterFailure(RosterOf(name) is { } roster
                        ? $"role set {name}: --{k} can't change a role supplied by a pack ({from}) — to drop the pack: ah roster roster pack {roster} --remove {(from.LastIndexOf(':') is > 0 and var cut ? from[..cut] : "<plugin>@<marketplace>")}"
                        : $"role set {name}: --{k} can't change a role adopted from a pack ({from}) — remove it and define your own role to change that");
                }
            }

            var row = atLevel?.Clone() ?? new JsonObject();
            foreach (var k in new[] { "label", "description", "routes", "model", "dispatch" })
            {
                if (OptText(k) is { } v)
                {
                    row[k] = v;
                }
            }

            CheckDispatch(name, row);
            var fromRow = _config.IsFromRow(row);
            var expanded = fromRow ? _config.ExpandFromRow(row) : new JsonObject();
            if (expanded.Get("error") is { IsTruthy: true } error)
            {
                throw new RosterFailure($"role set {name}: {T(error)}");
            }

            if (builtin)
            {
                if (row.Get("model") is { } model)
                {
                    CheckModelAllowed(name, _config.RoleClass(name, null) ?? string.Empty, model);
                }
            }
            else if (fromRow)
            {
                var check = _config.CheckCustomRow(name, expanded.Get("raw") ?? new JsonObject());
                if (check.Get("error") is { IsTruthy: true } ce)
                {
                    throw new RosterFailure($"role set {name}: {T(ce)}");
                }
            }

            var result = new JsonObject { ["level"] = level, ["path"] = path, ["role"] = name, ["row"] = row };
            if (dry)
            {
                var dryResult = new JsonObject { ["dry_run"] = true };
                RosterJson.Spread(dryResult, result);
                return dryResult;
            }

            WriteRoles(path, data, With(layer, name, row));
            var written = new JsonObject { ["written"] = true };
            RosterJson.Spread(written, result);
            return written;
        }

        private static JsonArray CopyArray(JsonArray source)
        {
            var copy = new JsonArray();
            copy.AddRange(source);
            return copy;
        }
    }
}
