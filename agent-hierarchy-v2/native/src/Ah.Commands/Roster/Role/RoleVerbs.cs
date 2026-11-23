using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Commands;

/// <summary>
/// <c>role list|set|remove|trust</c>: the custom roles of a level file, and the built-ins' agent overrides, with a role pack's
/// roles adopted only on the user's own pin.
/// </summary>
internal static class RoleVerbs
{
    private static readonly string[] Flags =
    [
        "class", "agent", "label", "description", "routes", "model", "dispatch", "shell", "params", "level", "scaffold", "from", "pin",
        "dry-run", "json", "cwd",
    ];

    private const string Usage =
        "usage: ah roster role list [--json] | role set <name> [--class C] [--agent A] [--label L] [--description D] [--routes R] [--model M] [--dispatch peer|model] [--shell self-care] [--params a,b] [--level L] [--scaffold repo|user] [--dry-run] | role set <name> --from <plugin>@<marketplace>:<role> [--label L] [--description D] [--routes R] [--model M] [--dispatch …] [--level L] (--dry-run | --pin sha256:…) | role trust <name> [--level L] (--dry-run | --pin sha256:…) | role remove <name> [--level L]  (all with --cwd <abs cwd>)";

    [Verb("roster:role")]
    internal static int Role(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    /// <summary>
    /// A trust commit (<c>role set --from</c>, <c>role trust</c>, <c>kit import --pin</c>; never a dry run) is refused here, before any
    /// write, from a team member or while a pipeline run is live at <c>--cwd</c> or at this process's own cwd. An error while checking refuses.
    /// </summary>
    internal static void RefuseTrustCommitHere(RosterContext c, string label)
    {
        string? why = null;
        try
        {
            if (c.EnvText("WRANGL_TEAM_FILE") is not null)
            {
                why = "this session is a team member (WRANGL_TEAM_FILE is set)";
            }
            else
            {
                var own = c.Services.CurrentDirectory;
                var dirs = new[] { c.Cwd, own }.Distinct(StringComparer.Ordinal);
                if (dirs.Any(d => c.Services.Hierarchy.Messages.PipelineRunLive(d)))
                {
                    why = "a pipeline run is live in this checkout";
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            why = $"the check couldn't run ({e.Message})";
        }

        if (why is not null)
        {
            throw new RosterFailure(
                $"{label}: refused — {why}. Trust is committed only from the user's own top-level session, outside a pipeline run; nothing was written.");
        }
    }

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure(
                    $"role: unrecognized flag --{key} (use --class, --agent, --label, --description, --routes, --model, --dispatch, --shell, --params, --level, --scaffold, --from, --pin, --dry-run, --json, --team, --cwd)");
            }
        }

        var sub = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        var name = c.Opts.Positionals.Count > 1 ? c.Opts.Positionals[1] : null;

        if (sub is "set" or "remove" or "trust")
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
            case "trust":
                c.Out(ctx.Trust(name));
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
                    row["pin_state"] = PackVerbs.PinState(entry.Member("pack"));
                    if (builtin)
                    {
                        row["pack_override"] = true;
                    }
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
                var adopted = from ? $" — from {T(r.Member("from"))} ({T(r.Member("pin_state"))})" : string.Empty;
                var declared = r.Member("params") is { IsArray: true } p ? $" — params: {string.Join(", ", p.AsArray().Select(x => T(x)))}" : string.Empty;
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

        private static string FailPackState(string who, JsonObject state)
        {
            var subcode = state.Get("subcode") is { IsTruthy: true } sc && !IsText(sc, T(state.Get("status")))
                ? $" [{T(sc)}]"
                : string.Empty;
            return $"{who}: {T(state.Get("status"))}: {T(state.Get("message"))}{subcode}";
        }

        // ---------------------------------------------------------------- set

        internal JsonValue Set(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new RosterFailure("role set needs a role name: role set <name> [flags]");
            }

            var opts = c.Opts;
            if (opts.Has("from"))
            {
                return SetFrom(name);
            }

            if (opts.Has("pin"))
            {
                throw new RosterFailure($"role set {name}: --pin goes with --from; to re-trust an adopted role, use `role trust {name}`");
            }

            var builtin = _config.IsBuiltinRole(name);
            var dry = opts.IsTrue("dry-run");
            foreach (var k in new[] { "class", "agent", "label", "description", "routes", "model", "dispatch", "shell", "params", "scaffold", "level" })
            {
                if (Valueless(k))
                {
                    throw new RosterFailure($"role set: --{k} needs a value");
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
            var scaffoldFlag = OptText("scaffold");
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
                level = scaffoldFlag == "repo" ? "repo" : "global";
            }

            var path = _files.PathOf(level);
            var data = _files.Read(path);
            var layer = Layer(data);
            var atLevel = RowAt(layer, name);
            if (atLevel is not null && _config.IsFromRow(atLevel))
            {
                return SetAdoptedFields(name, level, path, data, layer, atLevel, dry, builtin);
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
                    : NodePath.Join(c.Services.Environment.HomeDirectory, ".claude", "agents", $"{agent}.md");
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
                foreach (var key in new[] { "class", "label", "description", "routes", "agent", "from", "pin" })
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

        // ---------------------------------------------------------------- adopting a pack role

        /// <summary>The row an adopted role resolves to: the pack's fields with the user's <paramref name="overlay"/> laid over them.</summary>
        private static JsonObject AdoptedRaw(JsonObject state, JsonObject overlay)
        {
            var manifestRole = state.Get("manifestRole") is { IsMap: true } mr ? mr : (JsonValue?)null;
            var raw = new JsonObject();
            var source = manifestRole?.Member("raw") is { IsMap: true } r ? r.AsObject() : new JsonObject();
            foreach (var k in new[] { "class", "label", "description", "routes", "model", "dispatch" })
            {
                if (source.Get(k) is { } v)
                {
                    raw[k] = v;
                }
            }

            var from = state.Get("from") is { IsMap: true } f ? f : (JsonValue?)null;
            raw["agent"] = $"{T(from?.Member("plugin"))}:{T(manifestRole?.Member("agent"))}";
            foreach (var k in new[] { "label", "description", "routes", "model", "dispatch" })
            {
                if (overlay.Get(k) is not { } v)
                {
                    continue;
                }

                if (IsText(v, string.Empty) && k is "routes" or "description")
                {
                    raw.Remove(k);
                }
                else
                {
                    raw[k] = v;
                }
            }

            return raw;
        }

        private static JsonObject PackStateFinding(JsonObject state)
        {
            var subcode = state.Get("subcode") is { IsTruthy: true } sc && !IsText(sc, T(state.Get("status"))) ? $" [{T(sc)}]" : string.Empty;
            return new JsonObject
            {
                ["level"] = "error",
                ["code"] = state.Get("status") ?? JsonValue.Null,
                ["path"] = state.Get("agentPath") is { IsTruthy: true } ap ? ap : JsonValue.Null,
                ["field"] = JsonValue.Null,
                ["message"] = $"{T(state.Get("message"))}{subcode}",
                ["fix"] = new JsonArray(),
            };
        }

        private JsonObject Shown(JsonValue? raw)
        {
            var o = new JsonObject();
            if (raw is { IsMap: true } m)
            {
                foreach (var k in m.AsObject().Keys)
                {
                    var v = m.AsObject()[k];
                    o[k] = v.IsText ? Esc(v) : v;
                }
            }

            return o;
        }

        private sealed record Review(JsonObject Raw, string Cls, JsonArray Findings, List<string> Warnings, JsonObject Details);

        /// <summary>
        /// Everything a person reviews before trusting pack role <paramref name="state"/> as <paramref name="name"/>: the effective
        /// row, the contract and pack-agent findings, the tools, what else the plugin carries, and whether it takes unattended
        /// pipeline work.
        /// </summary>
        private Review PackReview(string name, JsonObject state, JsonObject overlay)
        {
            var builtin = _config.IsBuiltinRole(name);
            var findings = new JsonArray();
            var warnings = new List<string>();
            if (!IsText(state.Get("status"), "ok"))
            {
                findings.Add(PackStateFinding(state));
            }

            var raw = AdoptedRaw(state, overlay);
            var cls = T(raw.Get("class"));
            if (builtin)
            {
                cls = _config.RoleClass(name, null) ?? string.Empty;
                if (!IsText(raw.Get("class"), cls))
                {
                    throw new RosterFailure(
                        $"role set {name}: the pack role's class is {T(raw.Get("class"))}, but {name} is a {cls} role — a pack role can override a built-in only of its own class");
                }
            }
            else
            {
                var check = _config.CheckCustomRow(name, raw);
                if (check.Get("error") is { IsTruthy: true } error)
                {
                    throw new RosterFailure($"role set {name}: {T(error)}");
                }

                if (check.Get("warnings") is { IsArray: true } cw)
                {
                    warnings.AddRange(cw.AsArray().Select(x => T(x)));
                }
            }

            var agentText = state.Get("agentText") is { IsText: true } at ? at.AsString() : string.Empty;
            var agentPath = state.Get("agentPath") is { IsTruthy: true } ap ? ap : JsonValue.Null;
            var parsed = _config.PackAgentParse(agentText, agentPath.IsText ? agentPath.AsString() : null);
            var spec = new JsonObject
            {
                ["role"] = name,
                ["cls"] = cls,
                ["agent"] = raw.Get("agent") ?? JsonValue.Null,
                ["description"] = builtin ? JsonValue.Null : raw.Get("description") is { IsTruthy: true } d ? d : JsonValue.Null,
                ["builtin"] = builtin,
                ["cwd"] = c.Cwd,
                ["inMemory"] = new JsonObject { ["path"] = agentPath, ["text"] = agentText, ["fm"] = parsed.Get("fm") ?? JsonValue.Null },
            };
            var v = _config.ValidateAgentContract(spec);
            if (parsed.Get("findings") is { IsArray: true } pf)
            {
                findings.AddRange(pf.AsArray());
            }

            if (v.Get("findings") is { IsArray: true } vf)
            {
                findings.AddRange(vf.AsArray());
            }

            var record = state.Get("record") is { IsMap: true } rec ? rec : (JsonValue?)null;
            var installPath = T(record?.Member("installPath"));
            var manifest = _config.ReadPackManifest(installPath, T((state.Get("from") is { IsMap: true } ff ? ff : (JsonValue?)null)?.Member("plugin")));
            var from = state.Get("from") is { IsMap: true } fr ? fr : (JsonValue?)null;
            var extras = new JsonArray();
            foreach (var e in _config.PackExtras(installPath, manifest))
            {
                var copy = e.IsMap ? e.AsObject().Clone() : new JsonObject();
                copy["name"] = Esc(e.Member("name"));
                extras.Add(copy);
            }

            var details = new JsonObject
            {
                ["from"] = $"{T(from?.Member("plugin"))}@{T(from?.Member("marketplace"))}:{T(from?.Member("role"))}",
                ["install_path"] = installPath,
                ["fields"] = Shown(state.Get("manifestRole")?.Member("raw")),
                ["agent_file"] = agentPath,
                ["tools"] = _config.PackToolReport(parsed.Get("fm")),
                ["also_in_plugin"] = extras,
            };
            if (cls == "implement" && !builtin && raw.Get("routes") is { IsTruthy: true })
            {
                details["unattended"] = _config.UnattendedLine;
            }

            return new Review(raw, cls, findings, warnings, details);
        }

        private void RefuseTrustCommitHere(string label) => RoleVerbs.RefuseTrustCommitHere(c, label);

        private void WriteStoredCopy(string pin, JsonObject row, string agentText, JsonValue files)
        {
            var hex = pin["sha256:".Length..];
            var path = AhPaths.TrustedPackFile(c.Services.Environment.HomeDirectory, hex);
            var keys = row.Keys.ToList();
            keys.Sort(StringComparer.Ordinal);
            var sorted = new JsonObject();
            foreach (var k in keys)
            {
                sorted[k] = row[k];
            }

            var doc = new JsonObject { ["row"] = sorted, ["agentText"] = agentText, ["files"] = files };
            LevelFiles.WriteAtomically(path, JsonText.Stringify(doc, 0) + "\n");
        }

        private JsonObject ReadyState(string who, JsonValue from)
        {
            var state = _config.PackRoleStateJson(from, JsonValue.Null);
            if (!(state.Get("manifestRole") is { IsMap: true } mr) || mr.Member("error") is { IsTruthy: true } || !(state.Get("agentText") is { IsText: true }))
            {
                throw new RosterFailure(FailPackState(who, state));
            }

            return state;
        }

        private JsonValue SetFrom(string name)
        {
            var opts = c.Opts;
            var dry = opts.IsTrue("dry-run");
            foreach (var k in new[] { "class", "agent", "scaffold", "shell" })
            {
                if (opts.Has(k))
                {
                    throw new RosterFailure(
                        $"role set {name}: --{k} can't be used with --from — a pack role's {(k == "scaffold" ? "agent file" : k)} comes from the pack. To change it, remove the role and define your own");
                }
            }

            foreach (var k in new[] { "from", "pin", "label", "description", "routes", "model", "dispatch", "level" })
            {
                if (Valueless(k))
                {
                    throw new RosterFailure($"role set: --{k} needs a value");
                }
            }

            if (!dry)
            {
                RefuseTrustCommitHere($"role set {name} --from");
            }

            if (!dry && OptText("pin") is null)
            {
                throw new RosterFailure($"role set {name} --from: committing needs --pin <the pin the dry run printed> — run it with --dry-run first");
            }

            var builtin = _config.IsBuiltinRole(name);
            if (builtin)
            {
                foreach (var k in new[] { "label", "description", "routes" })
                {
                    if (opts.Has(k))
                    {
                        throw new RosterFailure($"role set {name}: --{k} does not apply to a built-in role — only --model and --dispatch");
                    }
                }
            }
            else if (_config.RoleNameError(JsonValue.FromString(name)) is { IsTruthy: true } nameError)
            {
                throw new RosterFailure($"role set {name}: {T(nameError)}");
            }

            var fromFlag = OptText("from");
            var parsed = _config.ParseFrom(fromFlag is null ? JsonValue.Null : JsonValue.FromString(fromFlag));
            if (!parsed.IsMap)
            {
                var shown = fromFlag is null ? "undefined" : JsonText.Quote(fromFlag);
                throw new RosterFailure($"role set {name}: --from must be <plugin>@<marketplace>:<role>, got {shown}");
            }

            var plugin = T(parsed.Member("plugin"));
            var role = T(parsed.Member("role"));
            string marketplace;
            if (parsed.Member("marketplace") is { IsTruthy: true } given)
            {
                marketplace = T(given);
            }
            else
            {
                var offering = _config.InstallRecordList()
                    .Where(r => IsText(r.Member("plugin"), plugin))
                    .Select(r => T(r.Member("marketplace")))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (offering.Count == 0)
                {
                    throw new RosterFailure($"role set {name}: plugin {plugin} is not installed");
                }

                if (offering.Count > 1)
                {
                    throw new RosterFailure(
                        $"role set {name}: plugin {plugin} is offered by {offering.Count} marketplaces ({string.Join(", ", offering)}) — name one: --from {plugin}@<marketplace>:{role}");
                }

                marketplace = offering[0];
            }

            var from = $"{plugin}@{marketplace}:{role}";
            var state = ReadyState($"role set {name} --from {from}", from);
            var defined = PackVerbs.LevelOfScope(Sources.Get(name));
            string level;
            if (OptText("level") is { } lg)
            {
                level = RequireLevel(lg);
            }
            else
            {
                level = defined is { IsText: true } dl && dl.AsString() != "shipped" ? dl.AsString() : "global";
            }

            var path = _files.PathOf(level);
            var data = _files.Read(path);
            var layer = Layer(data);
            var atLevel = RowAt(layer, name);
            if (atLevel is not null && !(_config.IsFromRow(atLevel) && IsText(atLevel.Get("from"), from)))
            {
                var by = _config.IsFromRow(atLevel) ? $"from {T(atLevel.Get("from"))}" : "by a row of its own";
                throw new RosterFailure($"role set {name}: {name} is already defined at level \"{level}\" ({path}) {by} — pick another name or remove it first");
            }

            var row = new JsonObject { ["from"] = from };
            if (atLevel is not null)
            {
                foreach (var k in atLevel.Keys)
                {
                    if (k is not ("from" or "pin"))
                    {
                        row[k] = atLevel[k];
                    }
                }
            }

            foreach (var k in new[] { "label", "description", "routes", "model", "dispatch" })
            {
                if (OptText(k) is { } v)
                {
                    row[k] = v;
                }
            }

            CheckDispatch(name, row);
            if (builtin && row.Get("model") is { } bm)
            {
                CheckModelAllowed(name, _config.RoleClass(name, null) ?? string.Empty, bm);
            }

            var review = PackReview(name, state, row);
            var agent = T(review.Raw.Get("agent"));
            CheckOwner(name, agent);

            if (review.Findings.Count > 0)
            {
                c.Stderr.Write($"{Esc(_config.FormatFindings(review.Findings))}\n");
            }

            if (review.Details.Get("unattended") is { IsTruthy: true } unattended)
            {
                c.Stderr.Write($"ah roster: {name} {T(unattended)}\n");
            }

            foreach (var w in review.Warnings)
            {
                c.Warn(w);
            }

            var pin = T(state.Get("pin"));
            var shownRow = row.Clone();
            shownRow["pin"] = pin;
            var warningArray = new JsonArray();
            foreach (var w in review.Warnings)
            {
                warningArray.Add(w);
            }

            var result = new JsonObject
            {
                ["level"] = level,
                ["path"] = path,
                ["role"] = name,
                ["from"] = from,
                ["row"] = shownRow,
                ["pack"] = review.Details,
                ["findings"] = review.Findings,
                ["warnings"] = warningArray,
                ["pin"] = pin,
            };
            if (dry)
            {
                var dryResult = new JsonObject { ["dry_run"] = true };
                RosterJson.Spread(dryResult, result);
                return dryResult;
            }

            if (OptText("pin") != pin)
            {
                throw new RosterFailure(
                    $"role set {name} --from: --pin {OptText("pin")} doesn't match what is installed now ({pin}) — nothing was written. Run --dry-run again and review what changed");
            }

            if (_config.HasContractErrors(review.Findings))
            {
                throw new RosterFailure($"role set {name} --from: errors stand (above) — nothing was written");
            }

            var manifestRaw = state.Get("manifestRole")!.Value.Member("raw") is { IsMap: true } mraw ? mraw.AsObject() : new JsonObject();
            WriteStoredCopy(pin, manifestRaw, T(state.Get("agentText")), state.Get("files") ?? new JsonArray());
            var stored = row.Clone();
            stored["pin"] = pin;
            WriteRoles(path, data, With(layer, name, stored));
            var written = new JsonObject { ["written"] = true };
            RosterJson.Spread(written, result);
            return written;
        }

        /// <summary><c>role set</c> on a row already adopted from a pack: only the user's own fields change; the pin stays.</summary>
        private JsonValue SetAdoptedFields(string name, string level, string path, JsonObject data, JsonObject? layer, JsonObject atLevel, bool dry, bool builtin)
        {
            var opts = c.Opts;
            foreach (var k in new[] { "class", "agent", "scaffold", "shell" })
            {
                if (opts.Has(k))
                {
                    throw new RosterFailure(
                        $"role set {name}: --{k} can't change a role adopted from a pack ({T(atLevel.Get("from"))}) — remove it and define your own role to change that");
                }
            }

            var row = atLevel.Clone();
            foreach (var k in new[] { "label", "description", "routes", "model", "dispatch" })
            {
                if (OptText(k) is { } v)
                {
                    row[k] = v;
                }
            }

            CheckDispatch(name, row);
            var expanded = _config.ExpandFromRow(row);
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
            else
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

        /// <summary>
        /// A line diff of two texts: the system <c>diff -u</c> of two files named <c>trusted</c> and <c>installed</c>, else both
        /// texts in full when there is no process runner, no <c>diff</c>, or it fails.
        /// </summary>
        private string TextDiff(string before, string after)
        {
            if (before == after)
            {
                return string.Empty;
            }

            var full = $"--- trusted\n{before}\n+++ installed\n{after}";
            if (c.Services.Launch?.Processes is not { } runner)
            {
                return full;
            }

            DirectoryInfo? dir = null;
            try
            {
                dir = Directory.CreateTempSubdirectory("ah-trust-");
                var trusted = Path.Combine(dir.FullName, "trusted");
                var installed = Path.Combine(dir.FullName, "installed");
                var utf8 = new System.Text.UTF8Encoding(false);
                File.WriteAllText(trusted, before, utf8);
                File.WriteAllText(installed, after, utf8);
                var result = runner.Run(new ProcessRequest(["diff", "-u", trusted, installed]));
                return result.ExitCode switch
                {
                    0 => string.Empty,
                    1 => utf8.GetString(result.Stdout.Span),
                    _ => full,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return full;
            }
            finally
            {
                try
                {
                    dir?.Delete(recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the directory is under the temp dir and holds only the two copies
                }
            }
        }

        internal JsonValue Trust(string? name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new RosterFailure("role trust needs a role name: role trust <name> (--dry-run | --pin sha256:…)");
            }

            var opts = c.Opts;
            var dry = opts.IsTrue("dry-run");
            if (Valueless("pin"))
            {
                throw new RosterFailure("role trust: --pin needs a value");
            }

            if (!dry)
            {
                RefuseTrustCommitHere($"role trust {name}");
            }

            if (!dry && OptText("pin") is null)
            {
                throw new RosterFailure($"role trust {name}: committing needs --pin <the pin the dry run printed> — run it with --dry-run first");
            }

            var excluded = Reg.Get("excludedRoles") is { IsArray: true } ex
                ? ex.AsArray().FirstOrDefault(e => IsText(e.Member("name"), name))
                : default;
            var scope = Sources.Get(name) is { IsTruthy: true } s ? s : excluded.IsMap ? excluded.Member("scope") : null;
            var defined = PackVerbs.LevelOfScope(scope);
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
                throw new RosterFailure($"role trust {name}: it is not defined at any level");
            }

            var path = _files.PathOf(level);
            var data = _files.Read(path);
            var layer = Layer(data);
            var row = RowAt(layer, name);
            if (row is null || !_config.IsFromRow(row))
            {
                throw new RosterFailure($"role trust {name}: it isn't adopted from a pack at level \"{level}\" ({path}) — role trust works only on a row set with --from");
            }

            var state = ReadyState($"role trust {name}", row.Get("from") ?? JsonValue.Null);
            var review = PackReview(name, state, row);
            var stored = _config.StoredCopy(row.Get("pin"));
            JsonObject changes;
            var now = state.Get("manifestRole")!.Value.Member("raw") is { IsMap: true } nr ? nr.AsObject() : new JsonObject();
            var nowFiles = state.Get("files") is { IsArray: true } nf ? nf.AsArray() : new JsonArray();
            if (stored.IsMap)
            {
                var storedRow = stored.Member("row") is { IsMap: true } sr ? sr.AsObject() : new JsonObject();
                var keys = storedRow.Keys.Concat(now.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal).ToList();
                var fields = new JsonArray();
                foreach (var k in keys)
                {
                    var a = storedRow.Get(k);
                    var b = now.Get(k);
                    var sa = a is { } av ? JsonText.Stringify(av, 0) : "undefined";
                    var sb = b is { } bv ? JsonText.Stringify(bv, 0) : "undefined";
                    if (sa == sb)
                    {
                        continue;
                    }

                    JsonValue Show(JsonValue? v) => v is { IsText: true } t ? Esc(t) : v ?? JsonValue.Null;
                    fields.Add(new JsonObject { ["field"] = k, ["trusted"] = Show(a), ["installed"] = Show(b) });
                }

                var before = Pairs(stored.Member("files"));
                var after = Pairs(nowFiles);
                var added = after.Keys.Where(p => !before.ContainsKey(p)).Select(p => (JsonValue)Esc(p)).ToList();
                var removed = before.Keys.Where(p => !after.ContainsKey(p)).Select(p => (JsonValue)Esc(p)).ToList();
                var changed = after.Keys.Where(p => before.TryGetValue(p, out var h) && h != after[p]).Select(p => (JsonValue)Esc(p)).ToList();
                changes = new JsonObject
                {
                    ["stored_copy"] = true,
                    ["fields_changed"] = fields,
                    ["agent_diff"] = Esc(TextDiff(T(stored.Member("agentText")), T(state.Get("agentText")))),
                    ["files"] = new JsonObject { ["added"] = ToArray(added), ["removed"] = ToArray(removed), ["changed"] = ToArray(changed) },
                };
            }
            else
            {
                var all = new JsonArray();
                foreach (var f in nowFiles)
                {
                    all.Add(Esc(f.IsArray && f.AsArray().Count > 0 ? f.AsArray()[0] : f));
                }

                changes = new JsonObject
                {
                    ["stored_copy"] = false,
                    ["note"] = "no stored copy of what was trusted is on this machine, so the whole agent file and every file are shown",
                    ["fields"] = Shown(state.Get("manifestRole")!.Value.Member("raw")),
                    ["agent_text"] = Esc(state.Get("agentText")),
                    ["files"] = new JsonObject { ["all"] = all },
                };
            }

            if (review.Findings.Count > 0)
            {
                c.Stderr.Write($"{Esc(_config.FormatFindings(review.Findings))}\n");
            }

            var pin = T(state.Get("pin"));
            var result = new JsonObject
            {
                ["level"] = level,
                ["path"] = path,
                ["role"] = name,
                ["from"] = row.Get("from") ?? JsonValue.Null,
                ["pinned"] = row.Get("pin") is { IsTruthy: true } rp ? rp : JsonValue.Null,
            };
            RosterJson.Spread(result, changes);
            result["pack"] = review.Details;
            result["findings"] = review.Findings;
            result["pin"] = pin;
            if (dry)
            {
                var dryResult = new JsonObject { ["dry_run"] = true };
                RosterJson.Spread(dryResult, result);
                return dryResult;
            }

            if (OptText("pin") != pin)
            {
                throw new RosterFailure(
                    $"role trust {name}: --pin {OptText("pin")} doesn't match what is installed now ({pin}) — nothing was written. Run --dry-run again and review what changed");
            }

            if (_config.HasContractErrors(review.Findings))
            {
                throw new RosterFailure($"role trust {name}: errors stand (above) — nothing was written");
            }

            WriteStoredCopy(pin, now, T(state.Get("agentText")), state.Get("files") ?? new JsonArray());
            var pinned = row.Clone();
            pinned["pin"] = pin;
            WriteRoles(path, data, With(layer, name, pinned));
            var written = new JsonObject { ["written"] = true };
            RosterJson.Spread(written, result);
            return written;
        }

        private static Dictionary<string, string> Pairs(JsonValue? files)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (files is { IsArray: true } list)
            {
                foreach (var pair in list.AsArray())
                {
                    if (pair.IsArray && pair.AsArray().Count >= 2)
                    {
                        map[T(pair.AsArray()[0])] = T(pair.AsArray()[1]);
                    }
                }
            }

            return map;
        }

        private static JsonArray CopyArray(JsonArray source)
        {
            var copy = new JsonArray();
            copy.AddRange(source);
            return copy;
        }

        private static JsonArray ToArray(IEnumerable<JsonValue> values)
        {
            var array = new JsonArray();
            array.AddRange(values);
            return array;
        }
    }
}
