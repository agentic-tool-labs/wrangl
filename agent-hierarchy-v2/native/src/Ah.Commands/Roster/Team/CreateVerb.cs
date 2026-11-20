using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// <c>create</c>: plan a team (<c>--plan</c>, the default), record the members a launch verified (<c>--commit</c>), or launch them
/// in one invocation (<c>--spawn</c>).
/// </summary>
internal static class CreateVerb
{
    internal static int Run(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster create is not implemented natively: the launch layer is not composed");
            return 70;
        }

        var modes = new[] { c.Opts.IsTrue("plan"), c.Opts.IsTrue("commit"), c.Opts.IsTrue("spawn"), c.Opts.IsTrue("yes") };
        if (modes.Count(m => m) > 1)
        {
            throw new RosterFailure("create: --plan, --commit, --spawn, and --yes are mutually exclusive");
        }

        var dir = c.Dir;
        var config = c.Services.Config;
        var child = ChildTeam.Parse(c, dir);
        var from = c.Opts.Text("from");
        if (from is not null && c.TeamArg is null && !c.TeamDefaultExplicit)
        {
            var entry = CreatePlanning.ResolveHistoryEntry(c, dir);
            c.TeamArg = entry.Get("alias") is { IsTruthy: true, IsText: true } alias ? alias.AsString() : null;
            c.RepoBasename = config.TeamPrefix(c.Cwd, c.TeamArg);
            RosterCli.ResolveTeamFileScope(c);
        }

        if (child is not null)
        {
            ChildTeam.NameTheTeam(c, child);
        }

        if (c.RosterArg is not null && from is not null)
        {
            throw new RosterFailure("create --from takes no --roster — a history entry carries its own members");
        }

        if (c.RosterArg is { } rosterArg)
        {
            var named = config.ResolveRoster(c.Cwd, rosterArg, c.RepoBasename, c.Registry);
            if (!named.IsMap || MemberPlanning.Text(named.Member("teamKey")) != rosterArg)
            {
                var source = MemberPlanning.Text(c.Selection ?? new JsonObject(), "source");
                var selected = source == "flag"
                    ? $"--roster {rosterArg}"
                    : $"(roster {rosterArg}, selected by {(source == "env" ? "WRANGL_ROSTER" : $"activeRoster at {MemberPlanning.JsConvertText(c.Selection?.Get("level"))} in {MemberPlanning.JsConvertText(c.Selection?.Get("path"))}")})";
                throw new RosterFailure(
                    $"create {selected}: no rosters.{rosterArg} block with members at any level — nothing was launched or written. " +
                    $"Define it with `ah roster init --roster {rosterArg}` and `ah roster add --roster {rosterArg} --role <R>`");
            }
        }

        var warnings = new List<string>();
        if (c.TeamArg is { } teamArg && c.RosterArg is null && from is null)
        {
            var named = config.ResolveRoster(c.Cwd, teamArg, c.RepoBasename, c.Registry);
            if (named.IsMap && MemberPlanning.Text(named.Member("teamKey")) == teamArg)
            {
                warnings.Add(
                    $"a rosters.{teamArg} block exists, but --team names only the team: this team is built from the default roster. To build it from rosters.{teamArg}, pass --roster {teamArg}.");
            }
        }

        var stale = config.StaleTeamKeys(c.Cwd, c.Registry);
        if (c.TeamArg is null)
        {
            var teamName = c.TeamFile ?? config.TeamPrefix(c.Cwd, null);
            if (stale.Get("aliases") is { IsArray: true } aliases)
            {
                foreach (var a in aliases.AsArray())
                {
                    var alias = MemberPlanning.JsConvertText(a);
                    if (alias == teamName)
                    {
                        continue;
                    }

                    warnings.Add(
                        $"this repo's config still sets a team alias, \"{alias}\", which no longer names teams: this team is named \"{teamName}\". To keep the old member names ({alias}-<role>), pass --team {alias}.");
                }
            }
        }

        if (stale.Get("warnings") is { IsArray: true } staleWarnings)
        {
            warnings.AddRange(staleWarnings.AsArray().Select(w => MemberPlanning.JsConvertText(w)));
        }

        foreach (var w in warnings)
        {
            c.Stderr.Write($"ah roster: warning — {w}\n");
        }

        var yes = c.Opts.IsTrue("yes");
        var spawning = c.Opts.IsTrue("spawn") || yes;
        var committing = c.Opts.IsTrue("commit") || yes;
        if (spawning || committing || c.Opts.Has("commit"))
        {
            warnings.AddRange(CreatePlanning.SpawnPlacementWarnings(c, dir));
        }

        JsonObject WithWarnings(JsonObject obj)
        {
            if (warnings.Count == 0)
            {
                return obj;
            }

            var copy = obj.Clone();
            var list = new JsonArray();
            list.AddRange(warnings.Select(w => (JsonValue)w));
            copy["warnings"] = list;
            return copy;
        }

        TeamScopes.ResolveWritableTeamScope(c, dir);
        TeamScopes.CheckNewTeamName(c, dir);
        TeamScopes.GuardTeamPrefixCollision(c, dir, c.TeamFile);
        TeamScopes.GuardLiveTeamAtScope(c, dir, committing);
        if (yes)
        {
            return SpawnAndCommit(c, dir, WithWarnings, child);
        }

        if (spawning)
        {
            return Spawn(c, dir, WithWarnings);
        }

        if (committing)
        {
            return Commit(c, dir, WithWarnings, child);
        }

        var plan = WithWarnings(CreatePlanning.GetMembersPlan(c, dir));
        if (child is not null)
        {
            plan = plan.Clone();
            plan["members"] = new JsonArray();
            plan["parent"] = new JsonObject { ["team"] = child.Parent, ["stream"] = child.Stream };
        }

        var needing = MemberPlanning.PlanNeedingModel(c, plan.Get("members") is { IsArray: true } m ? m.AsArray() : new JsonArray());
        if (needing.Count > 0)
        {
            plan = plan.Clone();
            plan["members_needing_model"] = needing;
        }

        var trustNeeds = CodexTrustNeeds.For(c, plan.Get("members") is { IsArray: true } pm ? pm.AsArray() : new JsonArray());
        if (trustNeeds.Count > 0)
        {
            plan = plan.Clone();
            CodexTrustNeeds.Attach(plan, trustNeeds);
        }

        c.Out(plan);
        return 0;
    }

    // ---------------------------------------------------------------- --commit

    private static string JsTypeof(JsonValue v) => v.IsText ? "string" : v.IsNumber ? "number" : v.IsBool ? "boolean" : "object";

    // ---------------------------------------------------------------- --yes

    /// <summary>The members a launch result hands to the commit, as the spawn/check-in cycle would: every member but a failed launch.</summary>
    private static readonly string[] CommitKeys =
        ["role", "name", "kind", "args", "model", "effort", "route", "autoMode", "transport_id", "tab_id", "workspace_id", "launch_cwd", "spawn_nonce", Ah.Core.Mux.HerdrFlavour.Field];

    /// <summary><c>create --yes</c>: plan, launch and record the team in one call; prints <c>{plan, spawn, commit}</c>.</summary>
    private static int SpawnAndCommit(RosterContext c, string dir, Func<JsonObject, JsonObject> withWarnings, ChildTeamSpec? child)
    {
        var all = new JsonObject();
        var exit = Spawn(c, dir, withWarnings, all);
        if (exit != 0 || all.Get("spawn") is not { IsMap: true } spawned)
        {
            c.Out(all);
            return exit;
        }

        var verified = new JsonArray();
        foreach (var m in spawned.Member("members") is { IsArray: true } ms ? ms.AsArray() : new JsonArray())
        {
            if (!m.IsMap || MemberPlanning.Text(m.Member("launch_status")) == "failed")
            {
                continue;
            }

            var entry = new JsonObject();
            foreach (var key in CommitKeys)
            {
                Copy(entry, key, m.AsObject(), key);
            }

            verified.Add(entry);
        }

        var transport = MemberPlanning.Text(spawned.Member("transport")) ?? string.Empty;
        var level = MemberPlanning.Text(spawned.Member("level")) ?? string.Empty;
        exit = Commit(c, dir, withWarnings, child, verified, transport, level, all);
        c.Out(all);
        return exit;
    }

    private static int Commit(
        RosterContext c,
        string dir,
        Func<JsonObject, JsonObject> withWarnings,
        ChildTeamSpec? child,
        JsonArray? verifiedGiven = null,
        string? transportGiven = null,
        string? levelGiven = null,
        JsonObject? into = null)
    {
        var services = c.Services;
        var config = services.Config;
        var from = c.Opts.Text("from");
        if (from is not null)
        {
            CreatePlanning.ValidateHistoryMembers(c, CreatePlanning.ResolveHistoryEntry(c, dir));
        }

        var verified = verifiedGiven is not null
            ? (JsonValue)verifiedGiven
            : JsJson.Parse(c.Opts.Text("verified") ?? (child is not null ? "[]" : throw new RosterFailure("--commit needs --verified <json array>")));
        if (!verified.IsArray)
        {
            throw new RosterFailure($"create --commit: --verified must be a JSON array, got {JsTypeof(verified)}");
        }

        var transport = transportGiven ?? c.Opts.Text("transport") ?? throw new RosterFailure("--commit needs --transport");
        var rosterLevel = levelGiven ?? c.Opts.Text("roster-level") ?? throw new RosterFailure("--commit needs --roster-level");
        var items = verified.AsArray().ToList();
        var allStrings = items.Count > 0 && items.All(i => i.IsText);
        var allObjects = items.All(i => i.IsMap);
        List<JsonObject> members;
        var needsResync = false;
        var sourceMembers = CreatePlanning.CreateSourceMembers(c, dir);
        var models = MemberPlanning.MemberModelOverrides(c, sourceMembers);
        var skips = sourceMembers
            .Select(m => MemberPlanning.WithModel(m, MemberPlanning.Text(m, "name") is { } n && models.TryGetValue(n, out var model) ? model : null))
            .Where(m => MemberPlanning.HasPane(c, m.Get("route")) && MemberPlanning.AutoSkipped(c, m))
            .ToList();
        var skipNames = new HashSet<string>(skips.Select(m => MemberPlanning.Text(m, "name") ?? string.Empty), StringComparer.Ordinal);
        if (allStrings)
        {
            var resolved = config.ResolveRoster(c.Cwd, c.RosterArg, c.RepoBasename, c.Registry);
            var rosterSource = resolved.IsMap && resolved.Member("members") is { IsArray: true } rm ? rm.AsArray().Select(MemberPlanning.Obj).ToList() : [];
            var rosterMembers = resolved.IsMap ? MemberPlanning.RenameInUse(c, rosterSource, c.RepoBasename) : [];
            var rosterRoute = resolved.IsMap ? resolved.Member("route") : null;
            members = [];
            foreach (var name in items.Select(i => i.AsString()).Where(n => !skipNames.Contains(n)))
            {
                var found = rosterMembers.FirstOrDefault(m => MemberPlanning.Text(m, "name") == name)
                    ?? throw new RosterFailure(
                        $"create --commit: --verified names no member {JsonText.Quote(name)} in the {rosterLevel} roster — it defines: " +
                        (rosterMembers.Count > 0 ? string.Join(", ", rosterMembers.Select(m => MemberPlanning.JsConvertText(m.Get("name")))) : "(none)"));
                var member = new JsonObject();
                Copy(member, "role", found, "role");
                Copy(member, "name", found, "name");
                if (models.TryGetValue(name, out var overridden))
                {
                    member["model"] = overridden;
                }
                else
                {
                    Copy(member, "model", found, "model");
                }

                Copy(member, "effort", found, "effort");
                if (CreatePlanning.EffectiveRoute(found, rosterRoute) is { } route)
                {
                    member["route"] = route;
                }

                Copy(member, "autoMode", found, "autoMode");
                member["transport_id"] = JsonValue.Null;
                members.Add(member);
            }

            needsResync = true;
        }
        else if (allObjects)
        {
            var rules = services.Launch!.Rules;
            var offenses = items
                .Select((m, i) => (i, errs: rules.ValidateTeamMember(m, c.Registry)))
                .Where(o => o.errs.Count > 0)
                .ToList();
            if (offenses.Count > 0)
            {
                var detail = string.Join(". ", offenses.Select(o => $"--verified entry {o.i} is not a valid member: {string.Join("; ", o.errs)}"));
                throw new RosterFailure(
                    $"create --commit: {detail}. --verified takes either a JSON array of member objects (as produced by the spawn/check-in cycle) or a JSON array of member-name strings (hydrated from the roster at --roster-level).");
            }

            var foreign = items
                .Where(m => m.Member("team") is { } t && (t.IsNull ? null : MemberPlanning.JsConvertText(t)) != c.TeamFile)
                .ToList();
            if (foreign.Count > 0)
            {
                string Label(JsonValue? t) => t is not { IsNull: false } v ? "the default team (team.json)" : JsonText.Stringify(v, 0);
                throw new RosterFailure(
                    $"create --commit: {string.Join(", ", foreign.Select(m => $"{MemberPlanning.JsConvertText(m.Member("name"))} checked in to team {Label(m.Member("team"))}"))}, " +
                    $"but this commit writes team {Label(c.TeamFile is null ? (JsonValue?)null : JsonValue.FromString(c.TeamFile))} — nothing was written. " +
                    "Pass the same --team to every create phase (--plan, --spawn, --commit).");
            }

            members = [];
            foreach (var m in items)
            {
                var obj = m.AsObject();
                var name = MemberPlanning.Text(obj, "name");
                if (name is not null && skipNames.Contains(name) && obj.Get("model") is not { IsTruthy: true })
                {
                    continue;
                }

                var copy = obj.Clone();
                copy.Remove("team");
                members.Add(copy);
            }
        }
        else
        {
            throw new RosterFailure("create --commit: --verified must be either all member objects or all member-name strings, not a mix");
        }

        if (child is not null)
        {
            members = [];
        }

        members = members.Select(m =>
        {
            var source = sourceMembers.FirstOrDefault(s =>
                s.Get("renamed_from") is { IsTruthy: true }
                && MemberPlanning.Text(s, "name") == MemberPlanning.Text(m, "name")
                && MemberPlanning.Text(s, "role") == MemberPlanning.Text(m, "role"));
            var rest = m.Clone();
            rest.Remove("renamed_from");
            if (source is not null)
            {
                rest["renamed_from"] = source.Get("renamed_from")!.Value;
            }

            return rest;
        }).ToList();
        var messages = services.Hierarchy.Messages;
        members = members.Select(m =>
        {
            if (m.Get("expected_root") is { IsTruthy: true })
            {
                return m;
            }

            var copy = m.Clone();
            var launchCwd = m.Get("launch_cwd") is { IsTruthy: true, IsText: true } lc ? lc.AsString() : c.Cwd;
            copy["expected_root"] = messages.RealCwd(launchCwd);
            return copy;
        }).ToList();

        var pid = c.OwnerPid();
        if (!RosterContext.IsInteger(pid))
        {
            throw new RosterFailure(
                "create --commit: no orchestrator session is proved above this command — refusing to write a team with an unowned pid");
        }

        if (!c.PidAlive(pid))
        {
            throw new RosterFailure(
                $"create --commit: pid {MemberPlanning.JsConvertText(JsonValue.FromNumber(pid))} is not a live process — refusing to write a team owned by a dead pid");
        }

        OrchestratorSession.Rebind(c);
        JsonValue orchestratorSession = RosterJson.Text(c.Opts.Text("session"));
        if (!orchestratorSession.IsText)
        {
            if (OrchestratorSession.ProcessTied(c, pid) is { } tied)
            {
                orchestratorSession = tied;
            }
            else
            {
                c.OrchestratorSessionUnknown = true;
            }
        }

        var templateRoster = from is not null ? JsonValue.Null : config.ResolveRoster(c.Cwd, c.RosterArg, c.RepoBasename, c.Registry);
        var layout = CreatePlanning.CreateLayout(c);
        var memberList = new JsonArray();
        memberList.AddRange(members.Select(m => (JsonValue)m));
        var team = new JsonObject
        {
            ["version"] = 1,
            ["team_id"] = services.Hierarchy.Core.NewId(),
            ["created"] = messages.LocalIso(),
            ["roster_level"] = rosterLevel,
            ["transport"] = transport,
            ["orchestrator"] = RecordedOrchestrator(c, orchestratorSession, pid),
            ["members"] = memberList,
            ["expected_root"] = messages.RealCwd(c.Cwd),
            ["roster"] = templateRoster.IsMap && templateRoster.Member("teamKey") is { IsTruthy: true } key ? key : JsonValue.Null,
            ["layout"] = layout.Get("mode")!.Value,
        };
        if (transport == "herdr" && c.Services.Launch?.Transports.Flavour() == Ah.Core.Mux.HerdrFlavour.Core)
        {
            team[Ah.Core.Mux.HerdrFlavour.Field] = Ah.Core.Mux.HerdrFlavour.Core;
        }

        if (child is not null)
        {
            ChildTeam.AddRecords(c, team, child);
        }

        services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                TeamScopes.GuardLiveTeam(c, doc, committing: true);
                KeepAnExistingSessionUnlessProven(c, doc, team, pid);
                return TeamFileOutcome.Write(team);
            });
        CreatePlanning.StoreTeamLayout(c, layout);
        var assignWarnings = new List<string>();
        var parentMode = child is null ? null : ChildTeam.ParentGateMode(c, child);
        if ((child is null || parentMode is not null) && WorkTreeAssignment.EnsureDocument(c, parentMode) is { } documentWarning)
        {
            assignWarnings.Add(documentWarning);
        }

        var recordedTmp = new Dictionary<string, string>();
        foreach (var m in members)
        {
            if (MemberPlanning.Text(m, "name") is not { Length: > 0 } memberName || MemberPlanning.Text(m, "expected_root") is not { Length: > 0 } root)
            {
                continue;
            }

            var workRoot = WorkTreeAssignment.WorkRootOf(c, root);
            var memberTmp = MemberTmp.Prepare(c, memberName, IsOtherKind(c, m), create: true, assignWarnings, resume: true, onCreate: true);
            if (memberTmp is not null)
            {
                recordedTmp[memberName] = memberTmp.Recorded;
            }

            if (WorkTreeAssignment.Assign(c, memberName, MemberPlanning.Text(m, "role"), workRoot, null, memberTmp is { Made: true } ? memberTmp : null) is { } assignWarning)
            {
                assignWarnings.Add(assignWarning);
            }
        }

        if (recordedTmp.Count > 0)
        {
            c.Services.Hierarchy.StateCalls.UpdateTeam(
                c.Dir,
                c.TeamFile,
                doc =>
                {
                    if (!doc.IsMap || doc.Member("members") is not { IsArray: true } rows || rows.AsArray().Any(r => !r.IsMap))
                    {
                        return null;
                    }

                    var next = doc.AsObject().Clone();
                    var rewritten = new JsonArray();
                    foreach (var row in rows.AsArray())
                    {
                        var copy = row.AsObject().Clone();
                        if (MemberPlanning.Text(copy, "name") is { } rowName && recordedTmp.TryGetValue(rowName, out var path))
                        {
                            copy["tmp"] = path;
                        }

                        rewritten.Add(copy);
                    }

                    next["members"] = rewritten;
                    return TeamFileOutcome.Write(next);
                });
        }

        var skipped = skips
            .Where(m => !members.Any(x => MemberPlanning.Text(x, "name") == MemberPlanning.Text(m, "name")))
            .Select(m => MemberPlanning.SkippedEntry(c, m))
            .ToList();
        var committed = new JsonObject { ["committed"] = true, ["team"] = team };
        MemberPlanning.SpreadRenamed(committed, members);
        var output = MemberPlanning.WithSkipped(c, withWarnings(committed), skipped).Clone();
        if (assignWarnings.Count > 0)
        {
            var shown = output.Get("warnings") is { IsArray: true } prior ? prior.AsArray().ToList() : [];
            shown.AddRange(assignWarnings.Select(w => (JsonValue)w));
            var list = new JsonArray();
            list.AddRange(shown);
            output["warnings"] = list;
        }

        OrchestratorSession.Warn(c, output);
        if (needsResync)
        {
            output["needs_resync"] = true;
        }

        try
        {
            var normalized = services.Launch!.Rules.NormalizeMembers(memberList);
            var entry = new JsonObject
            {
                ["fingerprint"] = services.Roster.Fingerprint(rosterLevel, transport, normalized),
                ["alias"] = RosterJson.Text(c.TeamFile),
                ["roster_level"] = rosterLevel,
                ["transport"] = transport,
                ["members"] = normalized,
                ["team_id"] = team.Get("team_id")!.Value,
            };
            var history = services.Hierarchy.StateCalls.UpsertHistory(dir, entry);
            if (history.Member("capExceeded") is { IsTruthy: true })
            {
                output["history"] = new JsonObject { ["ok"] = true, ["cap_exceeded"] = true };
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            output["history"] = new JsonObject { ["ok"] = false, ["why"] = error.Message };
        }

        try
        {
            var made = services.Hierarchy.Progress.EnsureTeam(c.ProgressPool, c.ProgressTeam, new Ah.Hierarchy.ProgressStreamInfo("orchestrator", "—", "—", null));
            if (made.Count > 0)
            {
                var created = new JsonArray();
                created.AddRange(made.Select(m => (JsonValue)m));
                output["progress"] = new JsonObject { ["ok"] = true, ["created"] = created };
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            output["progress"] = new JsonObject { ["ok"] = false, ["why"] = error.Message };
        }

        if (child is not null && ChildTeam.EnsureChecklist(c, child) is { } checklistWarning)
        {
            var earlier = output.Get("progress") is { IsMap: true } prior && MemberPlanning.Text(prior.Member("why")) is { Length: > 0 } why ? why + "; " : string.Empty;
            output["progress"] = new JsonObject { ["ok"] = false, ["why"] = earlier + checklistWarning };
        }

        TeamRegistration.Report(output, TeamRegistration.RegisterTeam(c, dir, team), c);
        if (into is not null)
        {
            into["commit"] = output;
            return 0;
        }

        c.Out(output);
        return 0;
    }

    /// <summary>The Orchestrator record of a new team: its session and pid, and the pid's start time when it can be read.</summary>
    private static JsonObject RecordedOrchestrator(RosterContext c, JsonValue session, double pid)
    {
        var record = new JsonObject
        {
            ["session_id"] = session,
            ["pid"] = pid,
        };
        if (OrchestratorSession.StartOf(c, pid) is { } started)
        {
            record["pid_start"] = started;
        }

        return record;
    }

    /// <summary>
    /// A <c>--session</c> that differs from the session an existing team file already records replaces it only when it is
    /// process-tied to the Orchestrator pid; otherwise the recorded one stays and the caller is warned.
    /// </summary>
    private static void KeepAnExistingSessionUnlessProven(RosterContext c, JsonValue existing, JsonObject team, double pid)
    {
        if (c.Opts.Text("session") is not { Length: > 0 } stated
            || existing.Member("orchestrator")?.Member("session_id") is not { IsText: true } held
            || held.AsString().Length == 0 || held.AsString() == stated
            || OrchestratorSession.ProcessTied(c, pid) == stated)
        {
            return;
        }

        team.Get("orchestrator")!.Value.AsObject()["session_id"] = held;
        c.OrchestratorSessionUnknown = true;
    }

    /// <summary>Whether the member's kind is one other than the default (Claude) kind.</summary>
    private static bool IsOtherKind(RosterContext c, JsonObject member) =>
        MemberPlanning.Text(member, "kind") is { Length: > 0 } kind && c.Services.Launch is { } launch && kind != launch.Registry.KindDefault;

    private static void Copy(JsonObject target, string key, JsonObject source, string sourceKey)
    {
        if (source.Get(sourceKey) is { } v)
        {
            target[key] = v;
        }
    }

    // ---------------------------------------------------------------- --spawn

    private static int Spawn(RosterContext c, string dir, Func<JsonObject, JsonObject> withWarnings, JsonObject? into = null)
    {
        var launch = c.Services.Launch!;
        if (c.Opts.Text("from") is not null)
        {
            CreatePlanning.ResolveHistoryEntry(c, dir);
        }

        var plan = CreatePlanning.GetMembersPlan(c, dir);
        if (into is not null)
        {
            into["plan"] = plan;
        }

        var transport = MemberPlanning.Text(plan, "transport") ?? string.Empty;
        var layout = plan.Get("layout") is { IsMap: true } l ? l.AsObject() : new JsonObject();
        var members = plan.Get("members") is { IsArray: true } ml ? ml.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var peers = members.Where(m => MemberPlanning.HasPane(c, m.Get("route"))).ToList();
        var needing = MemberPlanning.PlanNeedingModel(c, plan.Get("members") is { IsArray: true } pm ? pm.AsArray() : new JsonArray());
        if (needing.Count > 0)
        {
            throw MemberPlanning.RefuseMemberModels(c, needing);
        }

        var blocked = peers.FirstOrDefault(m => MemberPlanning.AdviseTierBlocked(c, m));
        if (blocked is not null)
        {
            throw MemberPlanning.RefuseAdviseTier(c, blocked);
        }

        foreach (var m in peers)
        {
            WorkTreeAssignment.RefuseMainCheckout(
                c,
                "create --spawn",
                MemberPlanning.Text(m, "name") ?? string.Empty,
                MemberPlanning.Text(m, "role"),
                c.Services.Hierarchy.Messages.RealCwd(c.Cwd),
                inStream: false);
        }

        var existingTeam = c.TeamFile is { } teamName ? c.Services.Roster.ReadTeamRecord(dir, teamName) : JsonValue.Null;
        foreach (var m in peers)
        {
            WorkTreeAssignment.RefuseLiveOccupant(
                c,
                "create --spawn",
                MemberPlanning.Text(m, "name") ?? string.Empty,
                MemberPlanning.Text(m, "role"),
                WorkTreeAssignment.WorkRootOf(c, c.Services.Hierarchy.Messages.RealCwd(c.Cwd)),
                existingTeam);
        }

        var tmpWarnings = new List<string>();
        foreach (var m in peers)
        {
            if (MemberPlanning.Text(m, "name") is not { Length: > 0 } peerName)
            {
                continue;
            }

            var peerTmp = MemberTmp.Prepare(c, peerName, IsOtherKind(c, m), create: true, tmpWarnings, resume: true, onCreate: true);
            if (peerTmp is not { Made: true })
            {
                continue;
            }

            if (!IsOtherKind(c, m))
            {
                c.MemberTmp[peerName] = peerTmp.Export;
            }

            if (WorkTreeAssignment.SetTmp(c, peerName, peerTmp) is { } setWarning)
            {
                tmpWarnings.Add(setWarning);
            }
        }

        if (c.MemberTmp.Count > 0)
        {
            plan = CreatePlanning.GetMembersPlan(c, dir);
            members = plan.Get("members") is { IsArray: true } ml2 ? ml2.AsArray().Select(MemberPlanning.Obj).ToList() : [];
            peers = members.Where(m => MemberPlanning.HasPane(c, m.Get("route"))).ToList();
        }

        HoldRefusal.RefuseIfHeld(c);
        if (transport == "herdr" && launch.Herdr.Flavour == Ah.Core.Mux.HerdrFlavour.Core)
        {
            // minted before the launch, so a terminal whose address never reached the output can still be found by it
            foreach (var m in peers)
            {
                m["spawn_nonce"] = Ah.Launch.HerdrCore.MintSpawnNonce();
            }
        }

        var launched = launch.Launcher.LayoutAndLaunch(
            dir, peers, transport, MemberPlanning.Text(layout, "mode") ?? "auto", c.Cwd, "create --spawn", c.LaunchContext());
        if (launched.Fail is not null)
        {
            throw new RosterFailure(launched.Fail);
        }

        if (launched.Partial is not null)
        {
            if (into is not null)
            {
                into["spawn"] = launched.Partial;
                return 3;
            }

            c.Out(launched.Partial);
            return 3;
        }

        CreatePlanning.StoreTeamLayout(c, layout);
        var defaultKind = launch.Registry.KindDefault;
        var output = new JsonArray();
        foreach (var m in members)
        {
            var entry = new JsonObject();
            Copy(entry, "role", m, "role");
            Copy(entry, "name", m, "name");
            if (MemberPlanning.Text(m, "kind") is { } kind && kind != defaultKind)
            {
                entry["kind"] = kind;
            }

            if (m.Get("args") is { IsArray: true } args && args.AsArray().Count > 0)
            {
                var copy = new JsonArray();
                copy.AddRange(args.AsArray());
                entry["args"] = copy;
            }

            Copy(entry, "model", m, "model");
            Copy(entry, "route", m, "route");
            Copy(entry, "autoMode", m, "autoMode");
            if (!MemberPlanning.HasPane(c, m.Get("route")))
            {
                entry["transport_id"] = JsonValue.Null;
                entry["launch_status"] = JsonValue.Null;
                entry["launch_cwd"] = JsonValue.Null;
                output.Add(entry);
                continue;
            }

            var lm = launched.Members[peers.IndexOf(m)];
            var spawn = m.Get("spawn") is { IsMap: true } s ? s.AsObject() : null;
            Copy(entry, "transport_id", m, "transport_id");
            Copy(entry, "spawn_nonce", m, "spawn_nonce");
            Copy(entry, Ah.Core.Mux.HerdrFlavour.Field, m, Ah.Core.Mux.HerdrFlavour.Field);
            Copy(entry, "launch_status", lm, "launch_status");
            Copy(entry, "launch_result", lm, "launch_result");
            Copy(entry, "retried", lm, "retried");
            entry["launch_cwd"] = spawn?.Get("launch_cwd") is { IsTruthy: true } cwd ? cwd : JsonValue.Null;
            if (lm.Get("error") is { IsTruthy: true } error)
            {
                entry["error"] = error;
            }

            if (lm.Get("unconfirmed_reason") is { IsTruthy: true } unconfirmed)
            {
                entry["unconfirmed_reason"] = unconfirmed;
            }

            if (lm.Get("label") is { IsTruthy: true } label)
            {
                entry["label"] = label;
            }

            if (lm.Get("blocked") is { IsTruthy: true } block)
            {
                entry["blocked"] = block;
                if (lm.Get("message") is { } message)
                {
                    entry["message"] = message;
                }
            }

            if (spawn?.Get("validation") is { IsTruthy: true } validation)
            {
                entry["validation"] = validation;
            }

            if (transport == "herdr"
                && lm.Get("launch_result") is { IsMap: true } result
                && result.Member("result")?.Member("pane") is { IsMap: true } pane)
            {
                if (pane.Member("tab_id") is { IsNull: false } tab)
                {
                    entry["tab_id"] = tab;
                }

                if (pane.Member("workspace_id") is { IsNull: false } workspace)
                {
                    entry["workspace_id"] = workspace;
                }
            }

            output.Add(entry);
        }

        var partial = output.Any(o => MemberPlanning.HasPane(c, o.Member("route")) && MemberPlanning.Text(o.Member("launch_status")) == "failed");
        var result2 = new JsonObject();
        Copy(result2, "level", plan, "level");
        result2["transport"] = transport;
        result2["members"] = output;
        result2["partial"] = partial;
        if (tmpWarnings.Count > 0)
        {
            var warned = new JsonArray();
            warned.AddRange(tmpWarnings.Select(w => (JsonValue)w));
            result2["warnings"] = warned;
        }

        if (plan.Get("renamed_members") is { IsTruthy: true } renamed)
        {
            result2["renamed_members"] = renamed;
        }

        var skipped = plan.Get("skipped_members") is { IsArray: true } sk ? sk.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var spawnOut = withWarnings(MemberPlanning.WithSkipped(c, result2, skipped));
        if (into is not null)
        {
            into["spawn"] = spawnOut;
            return 0;
        }

        c.Out(spawnOut);
        return 0;
    }
}
