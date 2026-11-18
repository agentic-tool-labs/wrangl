using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Config;

internal sealed partial class ConfigCore
{
    private const string AlwaysOrchestratorKey = "alwaysOrchestrator";

    private static readonly string[] PreferenceOnlyKeys = ["version", "teamLayout", "modelTiers", "activeRoster", ITestingOrders.Key, AlwaysOrchestratorKey];

    /// <summary>The top-level <c>teamLayout</c> of the global config file when it is a layout; warnings for an invalid one.</summary>
    internal (JsonValue? Layout, List<string> Warnings) TeamLayoutPreferenceTuple()
    {
        var warnings = new List<string>();
        var globalPath = UserConfigPath();
        JsonObject? data = null;
        if (TryReadJson(globalPath) is { IsMap: true } parsed)
        {
            data = parsed.AsObject();
        }

        if (data is null || data.Get("teamLayout") is not { } layout)
        {
            return (null, warnings);
        }

        if (!(layout.IsText && ConfigRegistry.RosterLayoutValues.Contains(layout.AsString())))
        {
            warnings.Add($"ah: teamLayout {JsonText.Stringify(layout, 0)} in {globalPath} is not a layout (allowed: {string.Join(", ", ConfigRegistry.RosterLayoutValues)}) — ignoring it.");
            return (null, warnings);
        }

        return (layout, warnings);
    }

    private JsonObject? LoadScope(string? path, string scope, JsonArray warnings)
    {
        if (string.IsNullOrEmpty(path) || !files.Exists(path))
        {
            return null;
        }

        JsonValue data;
        try
        {
            data = JsonText.Parse(files.ReadText(path));
        }
        catch (Exception)
        {
            warnings.Add($"ah: {scope}-scope config at {path} is not valid JSON — ignoring it.");
            return null;
        }

        if (!data.IsMap)
        {
            warnings.Add($"ah: {scope}-scope config at {path} is not a JSON object — ignoring it.");
            return null;
        }

        var doc = data.AsObject();
        var versionValue = doc.Get("version");
        var version = versionValue ?? ConfigRegistry.ConfigVersion;
        var asInteger = JsConvert.AsInteger(version);
        if (asInteger is null || asInteger > ConfigRegistry.ConfigVersion)
        {
            warnings.Add($"ah: {scope}-scope config at {path} declares version {ConfigRegistry.JsStringify(versionValue)}, which this plugin (v{ConfigRegistry.ConfigVersion}) does not understand — ignoring it.");
            return null;
        }

        return new JsonObject { ["scope"] = scope, ["path"] = path, ["data"] = doc };
    }

    /// <summary>Defaults and validates a chain role's <c>dispatch</c> and <c>peer</c> in place, warning on a bad value.</summary>
    private static void NormalizeDispatch(string role, JsonObject roles, JsonArray warnings)
    {
        var entry = roles[role].AsObject();
        var rawDispatch = entry.Get("dispatch");
        JsonValue dispatch = rawDispatch is null || (rawDispatch.Value.IsText && rawDispatch.Value.AsString() == "model") ? "peer" : rawDispatch.Value;
        if (!(dispatch.IsText && ConfigRegistry.DispatchModes.Contains(dispatch.AsString())))
        {
            warnings.Add($"ah: dispatch {ConfigRegistry.JsStringify(rawDispatch)} is not valid for role \"{role}\" (allowed: {string.Join(", ", ConfigRegistry.DispatchModes)}) — using \"peer\".");
            dispatch = "peer";
        }

        var withDispatch = entry.Clone();
        withDispatch["dispatch"] = dispatch;
        roles[role] = withDispatch;
        if (dispatch.AsString() == "peer")
        {
            var rawPeer = withDispatch.Get("peer");
            var peer = rawPeer ?? "auto";
            var validArray = peer.IsArray && peer.AsArray().Count > 0
                && peer.AsArray().All(p => p.IsText && JsText.Trim(p.AsString()).Length > 0);
            if (!validArray && (!peer.IsText || JsText.Trim(peer.AsString()).Length == 0))
            {
                warnings.Add($"ah: peer value for role \"{role}\" must be a non-empty string or array of names — using \"auto\".");
                peer = "auto";
            }

            var withPeer = withDispatch.Clone();
            withPeer["peer"] = peer;
            roles[role] = withPeer;
        }
    }

    /// <summary>
    /// Whether a stored entry for a built-in name is a custom row from before the role shipped. Only a role that became built in
    /// after users could define roles has such rows, and they carry <c>class</c>, which <c>role set</c> refuses on a built-in. A
    /// <c>from</c> row is a built-in's pack override and stays one; a stray <c>class</c> on a role built in from the start is
    /// warned about per key like any other fixed key.
    /// </summary>
    private static bool IsFormerCustomRow(string role, JsonObject row) => ConfigRegistry.BuiltinSince(role) is not null && row.Get("class") is not null;

    /// <summary>The built-in's table row with only the keys a built-in entry may carry kept from the former custom row.</summary>
    private static JsonObject SupersededRow(string role, JsonObject row)
    {
        var kept = ConfigRegistry.RoleDefaults(role);
        foreach (var key in new[] { "model", "dispatch", "peer" })
        {
            if (row.Get(key) is { } value)
            {
                kept[key] = value;
            }
        }

        return kept;
    }

    /// <summary>The built-in's entry with the keys the table owns set from the table, or removed where the table has no value.</summary>
    private static JsonObject TableFixedKeys(string role, JsonObject entry)
    {
        var fixedEntry = entry.Clone();
        if (ConfigRegistry.BuiltinRoutes(role) is { } routes)
        {
            fixedEntry["routes"] = routes;
        }
        else
        {
            fixedEntry.Remove("routes");
        }

        fixedEntry.Remove("description");
        if (fixedEntry.Get("class") is not null)
        {
            fixedEntry["class"] = ConfigRegistry.BuiltinClass(role)!;
        }

        if (fixedEntry.Get("label") is not null)
        {
            fixedEntry["label"] = ConfigRegistry.BuiltinLabel(role);
        }

        return fixedEntry;
    }

    /// <summary>The one notice that replaces the per-key warnings for a former custom row; nothing is written or removed.</summary>
    private string SupersededNotice(string role, JsonObject row, string scope, string path, string cwd)
    {
        var storedClass = row.Get("class") is { IsText: true } c ? c.AsString() : null;
        var builtinClass = ConfigRegistry.BuiltinClass(role);
        var lead = $"{role} now ships with {PluginName}, and the built-in replaces ";

        string whose;
        string reinstate;
        if (storedClass == builtinClass)
        {
            whose = $"your custom {role}";
            reinstate = $"To keep using your own, define it under another name with /wrangl:agent-role (for example my-{role}, agent {role}).";
        }
        else
        {
            whose = $"your custom {storedClass} role {role}";
            reinstate = $"To keep using your own, define it under another name with /wrangl:agent-role (for example my-{role}, --class {storedClass}, agent {role}).";
        }

        var agentRef = row.Get("agent") is { IsText: true } a ? a.AsString() : role;
        var located = LocateAgentFile(agentRef, cwd);
        var fileText = located.Get("path") is { IsText: true } found && found.AsString().Length > 0
            ? $"your agent file {found.AsString()} is no longer used"
            : "your agent file is no longer used (agent file not found)";
        return $"ah: {lead}{whose} at {scope} ({path}); {fileText}. Nothing was deleted. {reinstate} To clear this notice: ah roster role remove {role} --level {LevelOfScope(scope)}.";
    }

    /// <summary>The <c>--level</c> name of a config layer's scope, as the role verbs take it.</summary>
    private static string LevelOfScope(string scope) => scope switch { "user" => "global", "project" => "repo", _ => scope };

    private string? BuiltinAgentError(string role, JsonValue agent)
    {
        var why = ConfigRegistry.AgentRefError(agent);
        if (why is not null)
        {
            return why;
        }

        if (agent.AsString().StartsWith(PluginName + ":", StringComparison.Ordinal) && agent.AsString() != Qualified(role))
        {
            return $"{PluginName}:* agents belong to the built-ins";
        }

        return null;
    }

    /// <summary>The active team scope: <c>(name, home, via, owned)</c>.</summary>
    private (string? Name, string? Home, string? Via, List<string?> Owned) ResolveTeamScope(string resolvedCwd, ResolveOptions options)
    {
        if (!string.IsNullOrEmpty(options.Team))
        {
            return (options.Team, options.TeamHome, "team", []);
        }

        if (options.DefaultTeam)
        {
            return (null, options.TeamHome, "team", []);
        }

        var pid = options.Pid ?? process.ParentPid;
        try
        {
            var dir = HierarchyDir(resolvedCwd);
            var sessionId = string.IsNullOrEmpty(options.SessionId) ? null : options.SessionId;
            var pidValue = JsConvert.AsInteger(pid);
            var owned = Teams.OwnedTeams(dir, new TeamInvoker(pidValue is > 0 and <= int.MaxValue ? (int)pidValue : null, sessionId)).ToList();
            if (owned.Count > 0)
            {
                var team = Teams.ReadTeam(dir, owned[0]);
                string? orchSession = null;
                if (team is not null && JsonText.Parse(team.Value.Span) is { } record && record.Member("orchestrator")?.Member("session_id") is { IsText: true } sid)
                {
                    orchSession = sid.AsString();
                }

                return (owned[0], dir, sessionId is not null && orchSession == sessionId ? "session" : "pid", owned);
            }

            var homes = new List<string> { dir };
            if (MainHierarchyDir(resolvedCwd) is { } main)
            {
                homes.Add(main);
            }

            var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(environment.GetVariable);
            pane = string.IsNullOrEmpty(pane) ? environment.GetVariable("TMUX_PANE") : pane;
            var member = Teams.MemberTeam(dir, homes, string.IsNullOrEmpty(pane) ? null : pane);
            if (member is not null)
            {
                return (member.TeamName, member.Home, member.Via, []);
            }
        }
        catch (Exception)
        {
            // fail open to the default team
        }

        return (null, null, null, []);
    }

    private JsonObject ScopedRosterSelection(string resolvedCwd, ResolveOptions options, string? team, string? teamHome)
    {
        JsonValue? flag = options.Roster is { IsNull: true } ? ConfigRegistry.DefaultRoster : options.Roster;
        return RosterSelectionFor(resolvedCwd, flag, (teamHome ?? HierarchyDir(resolvedCwd), team));
    }

    public bool DirEnabled(string dir)
    {
        if (Ah.Core.Text.AhPaths.RepoRootOf(dir) is not { } root)
        {
            return false;
        }

        var resolved = ResolveConfig(root);
        return resolved.Get("configured") is { IsTruthy: true } && resolved.Get("enabled") is { IsTruthy: true };
    }

    // the status compute asks this on every compute (ah2-52): the layers alone, not the roles, rosters and team scope of a whole resolve
    public bool ConfigEnabled(string? cwd) => EnabledOf(ConfigScopes(Resolve(CwdOr(cwd)), new JsonArray()).Layers);

    /// <summary>The <c>enabled</c> the config layers give: the last layer with a bool decides, and with no layer it is on.</summary>
    private static bool EnabledOf(List<JsonObject> layers)
    {
        var enabled = true;
        foreach (var layer in layers)
        {
            if (layer["data"].AsObject().Get("enabled") is { IsBool: true } e)
            {
                enabled = e.AsBool();
            }
        }

        return enabled;
    }

    /// <summary>The user, project and repo-user scopes loaded for <paramref name="resolvedCwd"/>, and those of them that are config layers (not preference-only).</summary>
    private (JsonObject? User, JsonObject? Project, JsonObject? RepoUser, List<JsonObject> Layers) ConfigScopes(string resolvedCwd, JsonArray warnings)
    {
        var userPath = UserConfigPath();
        var candidates = RosterLevelCandidateLists(resolvedCwd);
        string? FirstExisting(IEnumerable<string> paths) => paths.FirstOrDefault(files.Exists);
        var projectPath = FirstExisting(candidates["repo"]);
        var repoUserPath = FirstExisting(candidates["repo-user"]);
        var user = LoadScope(userPath, "user", warnings);
        var project = projectPath is null || projectPath == userPath ? null : LoadScope(projectPath, "project", warnings);
        var repoUser = repoUserPath is null || repoUserPath == userPath || repoUserPath == projectPath ? null : LoadScope(repoUserPath, "repo-user", warnings);

        static bool PreferenceOnly(JsonObject layer)
        {
            var keys = layer["data"].AsObject().Keys;
            if (layer["scope"].AsString() == "user")
            {
                return keys.All(k => PreferenceOnlyKeys.Contains(k));
            }

            return keys.Count > 0 && keys.All(k => k is "activeRoster" or ITestingOrders.Key);
        }

        var layers = new List<JsonObject>();
        foreach (var layer in new[] { user, project, repoUser })
        {
            if (layer is not null && !PreferenceOnly(layer))
            {
                layers.Add(layer);
            }
        }

        return (user, project, repoUser, layers);
    }

    public JsonObject ResolveConfig(string? cwd, ResolveOptions? options = null)
    {
        options ??= new ResolveOptions();
        var warnings = new JsonArray();
        var resolvedCwd = Resolve(CwdOr(cwd));
        var (team, teamHome, teamVia, ownedTeamNames) = ResolveTeamScope(resolvedCwd, options);
        var (user, project, repoUser, layers) = ConfigScopes(resolvedCwd, warnings);

        // The testing orders are read from every loaded scope, preference-only ones included, so setting one never makes a config layer.
        var testing = new JsonObject();
        foreach (var scope in new[] { user, project, repoUser })
        {
            if (scope?["data"].AsObject().Get(ITestingOrders.Key) is { IsMap: true } block)
            {
                foreach (var key in block.AsObject().Keys)
                {
                    testing[key] = block.AsObject().Get(key)!.Value;
                }
            }
        }

        // Read from every loaded scope too, since a file holding only this key is dropped from the layers. The last scope with a bool wins.
        var alwaysOrchestrator = false;
        foreach (var scope in new[] { user, project, repoUser })
        {
            if (scope?["data"].AsObject().Get(AlwaysOrchestratorKey) is { IsBool: true } always)
            {
                alwaysOrchestrator = always.AsBool();
            }
        }

        foreach (var w in TeamLayoutPreferenceTuple().Warnings)
        {
            warnings.Add(w);
        }

        var roles = new JsonObject();
        var sources = new JsonObject();
        foreach (var role in ConfigRegistry.Roles)
        {
            roles[role] = TableFixedKeys(role, ConfigRegistry.RoleDefaults(role));
            sources[role] = "default";
        }

        // qa.cadence (ah2-154): "item" or "batch", resolved as qaCadence; absent from the result when no layer sets a known value, which callers treat as not configured.
        JsonValue qa = JsonValue.Null;
        JsonValue qaSource = JsonValue.Null;

        JsonValue Layers()
        {
            var array = new JsonArray();
            foreach (var l in layers)
            {
                array.Add(l);
            }

            return array;
        }

        JsonObject Result(bool configured, bool enabled, string handoffs, JsonValue handoffsSource, string msgs, JsonValue route, JsonValue routeSource,
            JsonArray shadowed, JsonArray excluded, JsonValue roster, JsonValue rosterLevel)
        {
            var result = Fields(configured, enabled, handoffs, handoffsSource, msgs, route, routeSource, shadowed, excluded, roster, rosterLevel);
            if (testing.Count > 0)
            {
                result[ITestingOrders.Key] = testing;
            }

            if (alwaysOrchestrator)
            {
                result[AlwaysOrchestratorKey] = true;
            }

            // Only when set to a known value, so a config without the key resolves exactly as before.
            if (!qa.IsNull)
            {
                result["qaCadence"] = qa;
                result["qaCadenceSource"] = qaSource;
            }

            return result;
        }

        JsonObject Fields(bool configured, bool enabled, string handoffs, JsonValue handoffsSource, string msgs, JsonValue route, JsonValue routeSource,
            JsonArray shadowed, JsonArray excluded, JsonValue roster, JsonValue rosterLevel) => new()
        {
            ["configured"] = configured,
            ["enabled"] = enabled,
            ["handoffs"] = handoffs,
            ["handoffsSource"] = handoffsSource,
            ["msgs"] = msgs,
            ["route"] = route,
            ["routeSource"] = routeSource,
            ["roles"] = roles,
            ["sources"] = sources,
            ["shadowed"] = shadowed,
            ["excludedRoles"] = excluded,
            ["layers"] = Layers(),
            ["warnings"] = warnings,
            ["cwd"] = resolvedCwd,
            ["roster"] = roster,
            ["rosterLevel"] = rosterLevel,
            ["team"] = team is null ? JsonValue.Null : team,
            ["teamVia"] = teamVia is null ? JsonValue.Null : teamVia,
            ["ownedTeams"] = Strings(ownedTeamNames),
        };

        if (layers.Count == 0)
        {
            return Result(false, true, "auto", "default", "required", JsonValue.Null, JsonValue.Null, new JsonArray(), new JsonArray(), JsonValue.Null, JsonValue.Null);
        }

        var enabled = EnabledOf(layers);

        var handoffs = "auto";
        JsonValue handoffsSource = "default";
        foreach (var layer in layers)
        {
            if (layer["data"].AsObject().Get("handoffs") is { IsText: true } h)
            {
                handoffs = h.AsString();
                handoffsSource = layer["scope"];
            }
        }

        if (!ConfigRegistry.HandoffModes.Contains(handoffs))
        {
            warnings.Add($"ah: handoffs {JsonText.Stringify(handoffs, 0)} is not a mode (allowed: {string.Join(", ", ConfigRegistry.HandoffModes)}) — using \"auto\".");
            handoffs = "auto";
            handoffsSource = "default";
        }

        // qa.cadence lives inside the qa map the QA scheduler also reads (parallel, admit_*); only cadence is read here.
        string? qaText = null;
        foreach (var layer in layers)
        {
            var layerQa = layer["data"].AsObject().Get("qa");
            if (layerQa is { IsMap: true } map && map.AsObject().Get("cadence") is { IsText: true } q)
            {
                qaText = q.AsString();
                qaSource = layer["scope"];
            }
            else if (layerQa is { IsText: true } flat)
            {
                warnings.Add($"ah: qa {JsonText.Stringify(flat.AsString(), 0)} at {layer["scope"].AsString()} scope is a string; when QA runs is qa.cadence inside the qa map — ignoring it.");
            }
        }

        if (qaText is not null && ConfigRegistry.QaModes.Contains(qaText))
        {
            qa = qaText;
        }
        else
        {
            if (qaText is not null)
            {
                warnings.Add($"ah: qa.cadence {JsonText.Stringify(qaText, 0)} is not a mode (allowed: {string.Join(", ", ConfigRegistry.QaModes)}) — treating QA cadence as not configured.");
            }

            qaSource = JsonValue.Null;
        }

        var msgs = "required";
        foreach (var layer in layers)
        {
            if (layer["data"].AsObject().Get("msgs") is { IsText: true } m)
            {
                msgs = m.AsString();
            }
        }

        if (!ConfigRegistry.MsgsModes.Contains(msgs))
        {
            warnings.Add($"ah: msgs {JsonText.Stringify(msgs, 0)} is not a mode (allowed: {string.Join(", ", ConfigRegistry.MsgsModes)}) — using \"required\".");
            msgs = "required";
        }

        string? route = null;
        JsonValue routeSource = JsonValue.Null;
        foreach (var layer in layers)
        {
            if (layer["data"].AsObject().Get("route") is { IsText: true } r)
            {
                route = r.AsString();
                routeSource = layer["scope"];
            }
        }

        if (route is not null && !ConfigRegistry.RouteValues.Contains(route))
        {
            if (!ConfigRegistry.StaleRouteValues.Contains(route))
            {
                warnings.Add($"ah: route {JsonText.Stringify(route, 0)} is not a mode (allowed: {string.Join(", ", ConfigRegistry.RouteValues)}) — ignoring it.");
            }

            route = null;
            routeSource = JsonValue.Null;
        }

        var definedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var customRaw = new Dictionary<string, (JsonValue Raw, string Scope, string Path)>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            var scope = layer["scope"].AsString();
            if (layer["data"].AsObject().Get("roles") is not { IsMap: true } layerRolesValue)
            {
                continue;
            }

            var layerRoles = layerRolesValue.AsObject();
            foreach (var role in layerRoles.Keys)
            {
                var entry = layerRoles[role];
                if (!ConfigRegistry.IsBuiltinRole(role))
                {
                    customRaw[role] = (entry, scope, layer["path"].AsString());
                    (definedBy.TryGetValue(role, out var l1) ? l1 : definedBy[role] = []).Add(scope);
                    continue;
                }

                if (!entry.IsMap)
                {
                    continue;
                }

                var row = entry.AsObject();
                if (IsFormerCustomRow(role, row))
                {
                    warnings.Add(SupersededNotice(role, row, scope, layer["path"].AsString(), resolvedCwd));
                    roles[role] = SupersededRow(role, row);
                }
                else
                {
                    roles[role] = row.Get("from") is { IsText: true } ? BuiltinFromRow(role, row) : row.Clone();
                }

                sources[role] = scope;
                (definedBy.TryGetValue(role, out var l2) ? l2 : definedBy[role] = []).Add(scope);
            }
        }

        var shadowedNames = new List<string>(ConfigRegistry.Roles);
        var customNames = customRaw.Keys.ToList();
        customNames.Sort(StringComparer.Ordinal);
        shadowedNames.AddRange(customNames);
        var shadowed = Strings(shadowedNames.Where(role => definedBy.TryGetValue(role, out var d) && d.Count > 1));

        foreach (var role in ConfigRegistry.Roles)
        {
            var model = roles[role].AsObject().Get("model");
            var valid = ConfigRegistry.ValidModels(role);
            if (!(model is { IsText: true } mt && valid.Contains(mt.AsString())))
            {
                var fallback = ConfigRegistry.RoleDefaults(role)["model"].AsString();
                warnings.Add($"ah: model {ConfigRegistry.JsStringify(model)} is not allowed for role \"{role}\" (allowed: {string.Join(", ", valid)}) — using the default \"{fallback}\".");
                var withModel = roles[role].AsObject().Clone();
                withModel["model"] = fallback;
                roles[role] = withModel;
            }

            if (!ConfigRegistry.PeerEligibleRoles.Contains(role))
            {
                continue;
            }

            NormalizeDispatch(role, roles, warnings);
        }

        foreach (var role in ConfigRegistry.Roles)
        {
            var entry = roles[role].AsObject();
            foreach (var key in ConfigRegistry.BuiltinFixedKeys)
            {
                if (entry.Get(key) is { } stored && !(key == "routes" && stored.IsText && stored.AsString() == ConfigRegistry.BuiltinRoutes(role)))
                {
                    warnings.Add($"ah: roles.{role}.{key} is ignored — a built-in role's {key} cannot be changed (only agent, model, dispatch and peer).");
                }
            }

            roles[role] = TableFixedKeys(role, entry);
            if (entry.Get("agent") is not { } agent)
            {
                continue;
            }

            var why = BuiltinAgentError(role, agent);
            if (why is not null)
            {
                warnings.Add($"ah: roles.{role}.agent {JsonText.Stringify(agent, 0)} is invalid ({why}) — using {Qualified(role)}.");
                var rest = roles[role].AsObject().Clone();
                rest.Remove("agent");
                roles[role] = rest;
            }
        }

        var excludedRoles = new JsonArray();

        void Exclude(string name, (JsonValue Raw, string Scope, string Path) info, string reason)
        {
            excludedRoles.Add(new JsonObject { ["name"] = name, ["scope"] = info.Scope, ["path"] = info.Path, ["reason"] = reason });
            warnings.Add($"ah: custom role \"{name}\" at {info.Scope}-scope config ({info.Path}) is invalid ({reason}) — ignoring it.");
        }

        var customRows = new List<(string Name, JsonObject Row)>();
        foreach (var name in customNames)
        {
            var info = customRaw[name];
            JsonObject? expanded = null;
            if (info.Raw.IsMap && info.Raw.AsObject().Get("from") is { IsText: true })
            {
                expanded = ExpandFromRow(info.Raw.AsObject());
                if (expanded.Get("error") is { } fromError)
                {
                    Exclude(name, info, fromError.AsString());
                    continue;
                }
            }

            var checkedRow = CheckCustomRowCore(name, expanded is not null ? expanded["raw"] : info.Raw);
            if (checkedRow.Get("error") is { } checkError)
            {
                Exclude(name, info, checkError.AsString());
                continue;
            }

            foreach (var w in checkedRow["warnings"].AsArray())
            {
                warnings.Add($"ah: custom role \"{name}\": {w.AsString()}");
            }

            var row = checkedRow["row"].AsObject();
            if (expanded is not null)
            {
                var withPack = row.Clone();
                withPack["from"] = info.Raw.AsObject().Get("from") ?? JsonValue.Null;
                if (info.Raw.AsObject().Get("pin") is { } pin)
                {
                    withPack["pin"] = pin;
                }

                withPack["pack"] = expanded["pack"];
                row = withPack;
            }

            customRows.Add((name, row));
        }

        var builtinAgents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var role in ConfigRegistry.Roles)
        {
            builtinAgents.TryAdd(ConfigRegistry.RoleAgent(role, roles[role], PluginName), role);
        }

        var agentUsers = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (name, row) in customRows)
        {
            var agent = row["agent"].AsString();
            (agentUsers.TryGetValue(agent, out var users) ? users : agentUsers[agent] = []).Add(name);
        }

        foreach (var (name, row) in customRows)
        {
            var agent = row["agent"].AsString();
            if (builtinAgents.TryGetValue(agent, out var owner))
            {
                Exclude(name, customRaw[name], $"agent {JsonText.Stringify(agent, 0)} is already the {owner} role's agent");
                continue;
            }

            if (agentUsers[agent].Count > 1)
            {
                Exclude(name, customRaw[name], $"agent {JsonText.Stringify(agent, 0)} is also used by {string.Join(", ", agentUsers[agent].Where(n => n != name))}");
                continue;
            }

            roles[name] = row;
            sources[name] = customRaw[name].Scope;
            if (ConfigRegistry.Class(row["class"].AsString())!.Chain)
            {
                NormalizeDispatch(name, roles, warnings);
            }
        }

        var selection = ScopedRosterSelection(resolvedCwd, options, team, teamHome);
        var selectionProblem = RosterSelectionProblemText(resolvedCwd, selection);
        if (selectionProblem is not null)
        {
            warnings.Add($"ah: {selectionProblem} Using the default roster block until then.");
        }

        var rosterKey = selectionProblem is not null ? null : selection["key"].IsText ? selection["key"].AsString() : null;
        var registry = new JsonObject { ["roles"] = roles };
        var rosterResult = ResolveRosterObject(cwd, rosterKey, TeamPrefix(resolvedCwd, team), registry);
        return Result(
            true,
            enabled,
            handoffs,
            handoffsSource,
            msgs,
            route is null ? JsonValue.Null : route,
            routeSource,
            shadowed,
            excludedRoles,
            rosterResult is null ? JsonValue.Null : rosterResult,
            rosterResult is null ? JsonValue.Null : rosterResult["level"]);
    }
}
