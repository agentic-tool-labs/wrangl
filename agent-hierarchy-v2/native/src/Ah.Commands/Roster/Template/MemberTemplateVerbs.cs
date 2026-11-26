using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>The roster template verbs that read or change a member of a level file: <c>show</c>, <c>add</c> and <c>edit</c>.</summary>
internal static class MemberTemplateVerbs
{
    private const string AutoInitRoute = "peer";

    /// <summary>The Claude CLI settings a non-claude kind strands, when it maps auto-mode itself and when it does not.</summary>
    private static readonly string[] ClaudeFlagsKept = ["model", "effort"];

    private static readonly string[] ClaudeFlagsAll = ["model", "effort", "autoMode"];

    private const string BypassWarning =
        "auto-mode \"bypassPermissions\" can leave a headless peer stuck at a startup confirmation screen; use --auto-mode auto for hands-off runs";

    [Verb("roster:show")]
    internal static int Show(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, ShowRun);

    [Verb("roster:add")]
    internal static int Add(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, AddRun);

    [Verb("roster:edit")]
    internal static int Edit(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, EditRun);

    // ---------------------------------------------------------------- show

    private static int ShowRun(RosterContext c)
    {
        var files = new LevelFiles(c);
        var config = c.Services.Config;
        var selection = config.SelectionView(c.Selection ?? new JsonObject());
        var result = new JsonObject();
        if (files.LevelArg() is { } given)
        {
            var level = files.RequireLevel(given);
            var path = files.PathOf(level);
            var data = files.Load(path).Data;
            var container = LevelFiles.Container(data, c.RosterArg);
            var resolved = config.ResolveRoster(c.Cwd, c.RosterArg, c.RepoBasename, files.Registry());
            JsonObject? shown = null;
            if (container is not null && container.Get("members") is { IsArray: true } members)
            {
                shown = new JsonObject();
                if (container.Get("route") is { } route)
                {
                    shown["route"] = route;
                }

                if (container.Get("packs") is { IsArray: true } packs)
                {
                    shown["packs"] = packs;
                }

                shown["members"] = files.NamedMembers(members.AsArray());
                if (container.Get("orchestrator_model") is { } orchestratorModel)
                {
                    shown["orchestrator_model"] = orchestratorModel;
                }
            }

            result["level"] = level;
            result["path"] = path;
            result["container"] = LevelFiles.ContainerLabel(c.RosterArg);
            result["roster"] = shown is null ? JsonValue.Null : shown;
            result["shadowed"] = resolved is { IsMap: true } r && r.Member("level") is { } l && !(l.IsText && l.AsString() == level)
                ? $"shadowed by {MemberPlanning.JsConvertText(l)}"
                : JsonValue.Null;
            AddNameNote(c, shown, result);
            result["selection"] = selection;
            c.Out(result);
            return 0;
        }

        var found = config.ResolveRoster(c.Cwd, c.RosterArg, c.RepoBasename, files.Registry());
        if (found is { IsMap: true } map)
        {
            foreach (var key in map.AsObject().Keys)
            {
                result[key] = map.AsObject()[key];
            }

            AddNameNote(c, map.AsObject(), result);
            result["selection"] = selection;
        }
        else
        {
            result["roster"] = JsonValue.Null;
            result["hint"] = "no roster configured — spawn ad hoc with: ah roster spawn-ad-hoc <role> [--kind K] [--route pane|peer] --cwd <abs cwd>";
            result["selection"] = selection;
        }

        c.Out(result);
        return 0;
    }

    /// <summary>
    /// A roster has no team, so <c>show</c> names members under the default team name and says so when a create could not use
    /// that name, rather than refusing a read.
    /// </summary>
    private static void AddNameNote(RosterContext c, JsonObject? block, JsonObject result)
    {
        if (block is null)
        {
            return;
        }

        var members = new List<JsonObject>();
        if (block.Get("members") is { IsArray: true } list)
        {
            members.AddRange(list.AsArray().Select(MemberPlanning.Obj));
        }

        var transport = c.Services.Launch!.Transports.Detect();
        var teamBlock = new TeamScopes.TeamBlock(MemberPlanning.Text(block, "route"), members);
        if (TeamScopes.TeamNameProblem(c, c.RepoBasename, transport, teamBlock) is { } problem)
        {
            result["team_name_note"] =
                $"members are shown under the default team name \"{c.RepoBasename}\", which a team cannot use ({problem.Why}) — create will ask for a name";
        }
    }

    // ---------------------------------------------------------------- add

    private static int AddRun(RosterContext c)
    {
        var files = new LevelFiles(c);
        var config = c.Services.Config;
        var launch = c.Services.Launch!;
        var target = files.RequireTarget(allowMissing: true);
        var level = target.Level!;
        var teamKey = target.TeamKey;
        var path = files.PathOf(level);
        var data = files.Read(path);
        var container = LevelFiles.Container(data, teamKey);
        if (container is null && teamKey is not null)
        {
            throw new RosterFailure(
                $"no {LevelFiles.ContainerLabel(teamKey)} at level \"{level}\" ({path}) — run `ah roster init --roster {teamKey}` first");
        }

        var created = container is null;
        if (container is null)
        {
            var route = c.Opts.Text("route") is { } given && given != "subagent" ? given : AutoInitRoute;
            files.Install(data, teamKey, LevelFiles.FreshBlock(route));
            container = LevelFiles.Container(data, teamKey)!;
        }

        if (container.Get("members") is not { IsArray: true })
        {
            container["members"] = new JsonArray();
        }

        var role = c.Opts.Text("role");
        if (role == "orchestrator")
        {
            throw new RosterFailure("role \"orchestrator\" is not a roster member — the Orchestrator is whatever session runs /wrangl:agent-team create");
        }

        var roles = launch.Registry.RegistryRoles(c.Registry);
        if (role is null || !roles.Contains(role))
        {
            var got = c.Opts.Has("role") ? (role is null ? "true" : LevelFiles.Quote(role)) : "undefined";
            throw new RosterFailure($"--role must be one of {string.Join(", ", roles)}, got {got}");
        }

        var member = MemberFlags.FromFlags(c, role, "add");
        var addRoute = CheckedRoute(c, member, container, givenNow: true);
        if (member.Get("onMissing") is not null && IsText(addRoute, "subagent"))
        {
            throw new RosterFailure("on-missing applies only to peer-routed members (this member's route is \"subagent\")");
        }

        var checkedMember = WithRoute(member, addRoute);
        var memberErrors = launch.Rules.ValidateMember(checkedMember, c.Registry);
        if (memberErrors.Count > 0)
        {
            throw new RosterFailure(string.Join("; ", memberErrors));
        }

        foreach (var warning in launch.Rules.KindFieldWarnings(checkedMember, c.Registry))
        {
            c.Warn(warning);
        }

        if (IsText(member.Get("autoMode"), "bypassPermissions") && IsText(addRoute, "peer"))
        {
            c.Warn(BypassWarning);
        }

        string? handoffWarning = null;
        if (member.Get("model") is { IsTruthy: true })
        {
            var withoutModel = member.Clone();
            withoutModel.Remove("model");
            if (MemberPlanning.AutoSkipped(c, withoutModel))
            {
                handoffWarning = $"{role} has a model, so create will launch it as a dedicated peer session instead of handing legwork to task-gopher; leave --model off (or edit --model \"\") to use task-gopher";
            }
        }

        container["members"].AsArray().Add(member);
        var blockErrors = launch.Rules.ValidateRosterBlock(config.NormalizeRosterBlock(container, c.Registry), c.Registry);
        if (blockErrors.Count > 0)
        {
            throw new RosterFailure(string.Join("; ", blockErrors));
        }

        WarnRoleVisibility(c, role, level);
        var addedNamed = Last(files.NamedMembers(container["members"].AsArray()));
        MemberFlags.RequireHerdrName(c, WithRoute(addedNamed, addedNamed.Get("route") is { IsTruthy: true } r ? r : container.Get("route")), "add");
        files.Write(path, data);
        if (handoffWarning is not null)
        {
            c.Warn(handoffWarning);
        }

        if (created)
        {
            c.Stderr.Write($"ah roster: no roster existed — created a minimal one at level \"{level}\": {path}\n");
        }

        if (target.WasDefaulted)
        {
            c.Stderr.Write($"ah roster: no --level given — added at the currently-resolving level \"{level}\" ({path})\n");
        }

        var added = Last(files.NamedMembers(container["members"].AsArray()));
        c.Stderr.Write($"ah roster: added {role} to {path}\n");
        var chain = config.ClassProp(role, c.Registry, "chain") is { IsBool: true } b && b.AsBool();
        var hasPane = addRoute is { } ar && config.RouteHasPane(ar);
        var nextStep = hasPane && chain
            ? $"config only — nothing was launched. To start this member: ah roster spawn-one {role} --member {MemberPlanning.JsConvertText(added.Get("name"))}"
            : $"config only — nothing was launched. {(!hasPane ? $"route {RouteText(addRoute)}" : $"role {role} is never a peer session")}: dispatched on demand.";
        c.Stderr.Write($"ah roster: {nextStep}\n");
        var output = new JsonObject
        {
            ["level"] = level,
            ["path"] = path,
            ["wasDefaulted"] = target.WasDefaulted,
            ["container"] = LevelFiles.ContainerLabel(teamKey),
            ["member"] = added,
            ["spawned"] = false,
            ["next_step"] = nextStep,
        };
        if (handoffWarning is not null)
        {
            output["warnings"] = new JsonArray { handoffWarning };
        }

        CodexTrustNeeds.Attach(output, CodexTrustNeeds.For(c, [added]));
        c.Out(JsonValue.FromObject(output));
        return 0;
    }

    // ---------------------------------------------------------------- edit

    private static int EditRun(RosterContext c)
    {
        var files = new LevelFiles(c);
        var launch = c.Services.Launch!;
        var opts = c.Opts;
        if (opts.Has("orchestrator-model"))
        {
            return EditOrchestratorModel(c, files);
        }

        var memberName = opts.Text("member") ?? throw new RosterFailure("edit needs --member <derived-name>");
        var target = files.RequireTarget();
        var level = target.Level!;
        var path = files.PathOf(level);
        var data = files.Read(path);
        var container = LevelFiles.Container(data, target.TeamKey);
        if (container is null || container.Get("members") is not { IsArray: true } membersValue)
        {
            throw new RosterFailure(
                $"no {LevelFiles.ContainerLabel(target.TeamKey)} at level \"{level}\" ({path}) — run `ah roster init` first");
        }

        var members = membersValue.AsArray();
        var idx = files.FindMemberIndex(members, memberName);
        if (idx == -1)
        {
            throw new RosterFailure($"no member named {LevelFiles.Quote(memberName)} at level \"{level}\"");
        }

        var updated = MemberPlanning.Obj(members[idx]).Clone();
        if (opts.Text("role") is { } role)
        {
            updated["role"] = role;
        }

        SetOrClear(updated, "model", opts.Text("model"));
        SetOrClear(updated, "effort", opts.Text("effort"));
        if (opts.Text("route") is { } routeFlag)
        {
            updated["route"] = routeFlag;
        }

        if (opts.Text("auto-mode") is { } autoModeFlag)
        {
            updated["autoMode"] = autoModeFlag;
        }

        if (opts.IsTrue("adapter"))
        {
            throw new RosterFailure("edit: --adapter requires a value (appserver, off, or \"\" to remove it)");
        }

        SetOrClear(updated, "adapter", opts.Text("adapter"));
        if (opts.IsTrue("kind"))
        {
            throw new RosterFailure("edit: --kind requires a value (e.g. claude, codex, pi)");
        }

        if (opts.Text("kind") is { } kind)
        {
            ApplyKind(c, opts, updated, kind);
        }

        if (opts.Has("args"))
        {
            if (MemberFlags.ParseArgsFlag(c, "edit") is { } parsedArgs)
            {
                updated["args"] = parsedArgs;
            }
            else
            {
                updated.Remove("args");
            }
        }

        if (opts.IsTrue("on-missing"))
        {
            throw new RosterFailure("edit: --on-missing requires a value (auto)");
        }

        var onMissingSupplied = opts.Text("on-missing") is not null;
        if (opts.Text("on-missing") is { } onMissingFlag)
        {
            updated["onMissing"] = onMissingFlag;
        }

        var routeOrRoleGiven = opts.Text("route") is not null || opts.Text("role") is not null;
        var editRoute = CheckedRoute(c, updated, container, routeOrRoleGiven);
        if (onMissingSupplied && IsText(editRoute, "subagent"))
        {
            throw new RosterFailure("on-missing applies only to peer-routed members (this member's route is \"subagent\")");
        }

        if (!onMissingSupplied && IsText(updated.Get("route"), "subagent") && updated.Get("onMissing") is { } dropped)
        {
            updated.Remove("onMissing");
            c.Stderr.Write(
                $"ah roster: ah: dropped on-missing \"{MemberPlanning.JsConvertText(dropped)}\" — it applies only to peer-routed members, and this member is now route \"subagent\"\n");
        }

        if (IsText(updated.Get("role"), "orchestrator"))
        {
            throw new RosterFailure("role \"orchestrator\" is not a roster member");
        }

        var checkedMember = WithRoute(updated, editRoute);
        if (!onMissingSupplied && checkedMember.Get("onMissing") is { IsText: true } stale && stale.AsString() is "never" or "prompt")
        {
            checkedMember.Remove("onMissing");
        }

        var errors = launch.Rules.ValidateMember(checkedMember, c.Registry);
        if (errors.Count > 0)
        {
            throw new RosterFailure(string.Join("; ", errors));
        }

        foreach (var warning in launch.Rules.KindFieldWarnings(checkedMember, c.Registry))
        {
            c.Warn(warning);
        }

        WarnRoleVisibility(c, MemberPlanning.Text(updated, "role"), level);
        if (IsText(updated.Get("autoMode"), "bypassPermissions") && IsText(editRoute, "peer"))
        {
            c.Warn(BypassWarning);
        }

        members[idx] = updated;
        var editedNamed = MemberPlanning.Obj(files.NamedMembers(members)[idx]);
        MemberFlags.RequireHerdrName(c, WithRoute(editedNamed, editedNamed.Get("route") is { IsTruthy: true } r ? r : container.Get("route")), "edit");
        files.Write(path, data);
        if (target.WasDefaulted)
        {
            c.Stderr.Write($"ah roster: no --level given — edited at the currently-resolving level \"{level}\" ({path})\n");
        }

        var edited = new JsonObject
        {
            ["level"] = level,
            ["path"] = path,
            ["wasDefaulted"] = target.WasDefaulted,
            ["container"] = LevelFiles.ContainerLabel(target.TeamKey),
            ["member"] = files.NamedMembers(members)[idx],
        };
        CodexTrustNeeds.Attach(edited, CodexTrustNeeds.For(c, [files.NamedMembers(members)[idx]]));
        c.Out(JsonValue.FromObject(edited));
        return 0;
    }

    /// <summary>
    /// The member's field changes a <c>--kind</c> brings: the default kind is stored as no key, the app-server adapter belongs to
    /// codex alone, and the Claude CLI settings a non-claude kind cannot take are cleared with a notice (or refused when this
    /// same invocation supplies them).
    /// </summary>
    private static void ApplyKind(RosterContext c, RosterArgs opts, JsonObject updated, string kind)
    {
        var launch = c.Services.Launch!;
        var kindDefault = launch.Registry.KindDefault;
        if (kind == kindDefault)
        {
            updated.Remove("kind");
        }
        else
        {
            updated["kind"] = kind;
        }

        if (kind != "codex")
        {
            if (opts.Text("adapter") is { Length: > 0 })
            {
                throw new RosterFailure($"edit: --adapter cannot be combined with --kind {kind} — the app-server adapter applies only to kind codex.");
            }

            if (updated.Get("adapter") is not null)
            {
                updated.Remove("adapter");
                c.Stderr.Write(
                    $"ah roster: ah: cleared adapter — the app-server adapter applies only to kind codex and this member is now kind \"{kind}\".\n");
            }
        }

        if (kind == kindDefault)
        {
            return;
        }

        var probe = new JsonObject { ["kind"] = kind, ["autoMode"] = "auto" };
        var mapsAutoMode = launch.Rules.KindAutoModeArgs(probe) is not null;
        var mapsModel = launch.Harnesses.Has(kind);
        var modelGivenNow = mapsModel && opts.Text("model") is { Length: > 0 };
        var strandable = (mapsAutoMode ? ClaudeFlagsKept : ClaudeFlagsAll)
            .Where(k => !(k == "model" && modelGivenNow))
            .ToList();
        var stranded = strandable.Where(k => updated.Get(k) is { } v && !v.IsNull).ToList();
        var suppliedNow = new List<string>();
        if (opts.Text("model") is not null && !mapsModel)
        {
            suppliedNow.Add("--model");
        }

        if (opts.Text("effort") is not null)
        {
            suppliedNow.Add("--effort");
        }

        if (opts.Text("auto-mode") is not null && !mapsAutoMode)
        {
            suppliedNow.Add("--auto-mode");
        }

        if (suppliedNow.Count > 0)
        {
            throw new RosterFailure(
                $"edit: {string.Join(", ", suppliedNow)} cannot be combined with --kind {kind} — model and effort are Claude CLI flags and apply only to kind claude. Pass native arguments with --args instead.");
        }

        foreach (var key in stranded)
        {
            updated.Remove(key);
        }

        if (stranded.Count > 0)
        {
            c.Stderr.Write(
                $"ah roster: ah: cleared {string.Join(", ", stranded)} — {(stranded.Count > 1 ? "they are" : "it is")} a Claude CLI setting and this member is now kind \"{kind}\". Pass native arguments with --args.\n");
        }
    }

    // ---------------------------------------------------------------- shared

    /// <summary>A roster block's <c>orchestrator_model</c>: one of the sub-orchestrator class's models, else the reason. Shared with <c>roster kit import</c>.</summary>
    internal static string? OrchestratorModelError(RosterContext c, string model)
    {
        var cls = c.Services.Config.RoleClass("sub-orchestrator", c.Registry);
        IReadOnlyList<string> allowed = cls is null ? [] : c.Services.Launch!.Registry.ClassModels(cls) ?? [];
        return allowed.Contains(model, StringComparer.Ordinal)
            ? null
            : $"orchestrator model \"{model}\" is not valid (allowed: {string.Join(", ", allowed)})";
    }

    /// <summary>
    /// The block-level <c>edit --orchestrator-model M</c>: sets the roster block's <c>orchestrator_model</c>, or removes it for <c>""</c>.
    /// The model must be one the orchestrate class (the sub-orchestrator row's class) allows.
    /// </summary>
    private static int EditOrchestratorModel(RosterContext c, LevelFiles files)
    {
        var opts = c.Opts;
        if (opts.Has("member"))
        {
            throw new RosterFailure("edit: --orchestrator-model is a roster-block edit; drop --member and set member fields in a separate edit");
        }

        if (opts.IsTrue("orchestrator-model"))
        {
            throw new RosterFailure("edit: --orchestrator-model requires a value (a model, or \"\" to remove it)");
        }

        var model = opts.Text("orchestrator-model") ?? "";
        if (model.Length > 0 && OrchestratorModelError(c, model) is { } why)
        {
            throw new RosterFailure($"{why}; nothing was written");
        }

        var target = files.RequireTarget();
        var level = target.Level!;
        var path = files.PathOf(level);
        var data = files.Read(path);
        var container = LevelFiles.Container(data, target.TeamKey)
            ?? throw new RosterFailure($"no {LevelFiles.ContainerLabel(target.TeamKey)} at level \"{level}\" ({path}) — run `ah roster init` first");
        SetOrClear(container, "orchestrator_model", model);
        files.Write(path, data);
        if (target.WasDefaulted)
        {
            c.Stderr.Write($"ah roster: no --level given — edited at the currently-resolving level \"{level}\" ({path})\n");
        }

        var output = new JsonObject
        {
            ["level"] = level,
            ["path"] = path,
            ["wasDefaulted"] = target.WasDefaulted,
            ["container"] = LevelFiles.ContainerLabel(target.TeamKey),
            ["orchestrator_model"] = model.Length > 0 ? model : JsonValue.Null,
        };
        c.Out(JsonValue.FromObject(output));
        return 0;
    }

    /// <summary>An empty value clears the field; a value sets it; no value leaves it.</summary>
    private static void SetOrClear(JsonObject member, string key, string? value)
    {
        if (value is null)
        {
            return;
        }

        if (value.Length == 0)
        {
            member.Remove(key);
        }
        else
        {
            member[key] = value;
        }
    }

    /// <summary>
    /// The route <paramref name="member"/> of <paramref name="block"/> is checked against: a route or role given by this
    /// invocation is checked as given; anything left from before reads as the block normalizer reads it. Null is "no route".
    /// </summary>
    private static JsonValue? CheckedRoute(RosterContext c, JsonObject member, JsonObject block, bool givenNow)
    {
        if (givenNow && member.Get("route") is { } given)
        {
            return given;
        }

        var probe = new JsonObject();
        if (block.Get("route") is { } blockRoute)
        {
            probe["route"] = blockRoute;
        }

        probe["members"] = new JsonArray { member.Clone() };
        var view = c.Services.Config.NormalizeRosterBlock(probe, c.Registry);
        if (view.Get("members") is { IsArray: true } list && list.AsArray().Count > 0
            && list.AsArray()[0].Member("route") is { IsTruthy: true } own)
        {
            return own;
        }

        return view.Get("route");
    }

    /// <summary><paramref name="member"/> with its route replaced (removed when there is none): the object spread of the Node verbs.</summary>
    private static JsonObject WithRoute(JsonObject member, JsonValue? route)
    {
        var copy = member.Clone();
        if (route is { } r)
        {
            copy["route"] = r;
        }
        else
        {
            copy.Remove("route");
        }

        return copy;
    }

    private static JsonObject Last(JsonArray named) => MemberPlanning.Obj(named[named.Count - 1]);

    private static bool IsText(JsonValue? value, string text) => value is { IsText: true } v && v.AsString() == text;

    private static string RouteText(JsonValue? route) => route is { } r ? MemberPlanning.JsConvertText(r) : "undefined";

    /// <summary>
    /// Warns when a roster at <paramref name="level"/> names a custom role defined only at a more specific level: another repo
    /// (for a global roster) or another user (for a repo roster) resolving it would not see the role.
    /// </summary>
    private static void WarnRoleVisibility(RosterContext c, string? role, string level)
    {
        if (role is null || c.Services.Config.IsBuiltinRole(role))
        {
            return;
        }

        if (c.Registry.Get("sources") is not { IsMap: true } sources || sources.AsObject().Get(role) is not { IsText: true } sourceValue)
        {
            return;
        }

        var source = sourceValue.AsString();
        int? rank = source switch { "user" => 0, "project" => 1, "repo-user" => 2, _ => null };
        int? levelRank = level switch { "global" => 0, "repo" => 1, "repo-user" => 2, _ => null };
        if (rank is { } s && levelRank is { } l && s > l)
        {
            c.Warn($"role \"{role}\" is defined at {(source == "project" ? "repo" : source)} level, below this {level} roster; where that definition does not resolve, this member's role is unknown.");
        }
    }
}
