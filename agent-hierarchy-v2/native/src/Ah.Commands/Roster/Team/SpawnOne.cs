using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy.StateCalls;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>How a new team record and its owner are made: one constructor for every verb that can create a team by joining it.</summary>
internal static class TeamRecords
{
    /// <summary>
    /// The pid that will own a team this verb creates: a resolvable, live one, refused before anything is launched so a half-made team
    /// (panes up, no persisted owner) cannot happen.
    /// </summary>
    internal static double NewTeamOwnerPid(RosterContext c, string callerLabel)
    {
        var pid = c.OwnerPid();
        if (!RosterContext.IsInteger(pid))
        {
            throw new RosterFailure(
                $"{callerLabel}: no orchestrator session is proved above this command — refusing to create a team with an unowned pid");
        }

        if (!c.PidAlive(pid))
        {
            throw new RosterFailure(
                $"{callerLabel}: pid {JsConvert.ToJsString(JsonValue.FromNumber(pid))} is not a live process — refusing to create a team owned by a dead pid");
        }

        return pid;
    }

    private static JsonObject NewOrchestrator(RosterContext c, double pid)
    {
        var record = new JsonObject { ["session_id"] = JsonValue.Null, ["pid"] = pid };
        if ((c.Proved().PidStart ?? OrchestratorSession.StartOf(c, pid)) is { } start)
        {
            record["pid_start"] = start;
        }

        return record;
    }

    /// <summary>A brand-new team record, before any member joins it.</summary>
    internal static JsonObject NewTeamRecord(RosterContext c, JsonValue resolved, string transport, double pid, string mode)
    {
        var messages = c.Services.Hierarchy.Messages;
        var record = new JsonObject
        {
            ["version"] = 1,
            ["team_id"] = c.Services.Hierarchy.Core.NewId(),
            ["created"] = messages.LocalIso(),
            ["roster_level"] = resolved.IsMap && resolved.Member("level") is { } level ? level : JsonValue.Null,
            ["transport"] = transport,
            ["orchestrator"] = NewOrchestrator(c, pid),
            ["members"] = new JsonArray(),
            ["expected_root"] = messages.RealCwd(c.Cwd),
            ["roster"] = resolved.IsMap && resolved.Member("teamKey") is { IsTruthy: true } key ? key : JsonValue.Null,
            ["layout"] = mode,
        };

        // ah2-143: the flavour it launches on, so later team-wide calls need not guess the server
        if (transport == "herdr" && c.Services.Launch?.Herdr.Flavour is { } flavour)
        {
            record[Ah.Core.Mux.HerdrFlavour.Field] = flavour;
        }

        return record;
    }

    /// <summary>
    /// Completes <paramref name="team"/> after a launch on <paramref name="transport"/>; a value already present is kept. ah2-393: a tmux
    /// launch sets an absent or null <c>transport</c> to <c>tmux</c>; its row's tmux pane id is what teardown closes. ah2-143: a herdr launch
    /// (<paramref name="launched"/> carries its flavour) sets <c>transport</c> and <c>herdr_flavour</c> where absent or null.
    /// </summary>
    internal static void CompleteAfterLaunch(JsonObject team, JsonObject launched, string transport)
    {
        if (transport == "tmux" && team.Get("transport") is null or { IsNull: true })
        {
            team["transport"] = "tmux";
            return;
        }

        if (launched.Get(Ah.Core.Mux.HerdrFlavour.Field) is not { IsText: true } flavour)
        {
            return;
        }

        if (team.Get("transport") is null or { IsNull: true })
        {
            team["transport"] = "herdr";
        }

        if (team.Get(Ah.Core.Mux.HerdrFlavour.Field) is null or { IsNull: true })
        {
            team[Ah.Core.Mux.HerdrFlavour.Field] = flavour;
        }
    }
}

/// <summary>
/// <c>spawn-one</c> and <c>spawn-ad-hoc</c>: launch one member into a team, the roster's or an ad hoc one, and record it.
/// </summary>
internal static class SpawnOne
{
    private static readonly string[] SpawnOneFlags =
        ["cwd", "dry-run", "allow-global", "allow-main", "team", "orchestrator-pid", "member", "model", "names-in-use", "stream", "split", "here", "tab", "replace-live", "lane-members", "lane-streams", "headless", "prompt", LoadThrottle.DespiteLoadFlag];

    private static readonly string[] AdHocFlags =
    [
        "role", "model", "effort", "route", "kind", "harness", "command", "args", "auto-mode", "on-missing", "cwd", "dry-run",
        "allow-global", "allow-main", "team", "orchestrator-pid", "names-in-use", "stream", "split", "here", "tab", "replace-live", "lane-members", "lane-streams",
        LoadThrottle.DespiteLoadFlag,
    ];

    /// <summary><paramref name="Indeterminate"/> is already narrowed to the undecided member that still holds its name: one with no recent sign of life holds nothing.</summary>
    private sealed record Live(bool IsLive, bool Indeterminate, string Why, JsonObject Raw);

    private static Live Liveness(RosterContext c, string dir, JsonValue member)
    {
        var r = c.Services.Launch!.Teardown.MemberLiveness(dir, member, c.TeamFile);
        return new Live(
            r.Get("live") is { IsTruthy: true },
            r.Get("indeterminate") is { IsTruthy: true } && r.Get("holds_claim") is { IsTruthy: true },
            MemberPlanning.JsConvertText(r.Get("why")),
            r);
    }

    /// <summary>
    /// The warning a spawn carries when it went ahead over a member whose liveness could not be decided (ah2-44): the spawn is not
    /// refused, and the line says how to clear an older session that may still hold the name.
    /// </summary>
    internal static string UnknownLifeWarning(string name, string why) =>
        $"the liveness of {name} is unknown ({why}); the spawn went ahead. If an older session still holds that name, close it with " +
        $"`ah roster dismiss {name}` or forget its record with `ah roster untrack {name}`";

    private static void RejectUnknownFlags(RosterContext c, string verb, string[] allowed, string use)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!allowed.Contains(key))
            {
                throw new RosterFailure($"{verb}: unrecognized flag --{key} (use {use})");
            }
        }
    }

    // ---------------------------------------------------------------- the verbs

    internal static int RunSpawnOne(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster spawn-one is not implemented natively: the launch layer is not composed");
            return 70;
        }

        RejectUnknownFlags(
            c, "spawn-one", SpawnOneFlags, "--cwd, --dry-run, --allow-global, --team, --orchestrator-pid, --member, --model, --names-in-use, --here, --tab, --split, --headless, or --prompt");
        if (c.Opts.Has("headless") && !c.Opts.IsTrue("headless"))
        {
            throw new RosterFailure("spawn-one: --headless takes no value; the prompt goes in --prompt <text>");
        }

        if (c.Opts.IsTrue("headless") && !c.Opts.IsTrue("dry-run") && string.IsNullOrEmpty(c.Opts.Text("prompt")))
        {
            throw new RosterFailure("spawn-one: --headless needs --prompt <text>, the one turn the member runs");
        }

        if (c.Opts.Has("prompt") && !c.Opts.IsTrue("headless"))
        {
            throw new RosterFailure("spawn-one: --prompt applies only with --headless");
        }

        var role = c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null;
        var subOrchestrator = SubOrchestratorSpawn.Is(c, role);
        if (role is null || !(subOrchestrator || c.Services.Config.IsBuiltinRole(role)) || RosterDefines(c, role, noRosterCounts: !subOrchestrator))
        {
            return Finish(c, Core(c, role, "spawn-one", null));
        }

        if (subOrchestrator && c.Opts.Keys.Contains("member"))
        {
            throw new RosterFailure($"spawn-one: --member does not apply to a {role}; its name is fixed as <team>-so-<stream>");
        }

        if (!subOrchestrator)
        {
            RefuseIfRoleLive(c, role);
        }

        return RunSpawnAdHoc(c);
    }

    /// <summary>Refuses a role the roster does not define when the team already has a live member of it: spawn-one keeps one per team.</summary>
    private static void RefuseIfRoleLive(RosterContext c, string role)
    {
        var team = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.Member("members") is not { IsArray: true } members)
        {
            return;
        }

        foreach (var record in members.AsArray().Where(r => r.Member("role") is { IsText: true } rr && rr.AsString() == role))
        {
            var state = Liveness(c, c.Dir, record);
            var name = MemberPlanning.JsConvertText(record.Member("name") ?? JsonValue.Null);
            if (state.Indeterminate)
            {
                // unknown is not proven live: the spawn goes ahead and says so (ah2-44)
                c.Stderr.WriteLine("ah roster: spawn-one: " + UnknownLifeWarning(name, state.Why));
            }

            if (state.IsLive)
            {
                var stream = record.Member("stream") is { IsText: true } s ? s.AsString() : null;
                var message =
                    $"ah: a {role} is already live: {name} on stream {stream ?? "none"}. spawn-one keeps one {role} per team; for a second, run ah roster spawn-ad-hoc {role} [--stream <ws>]; nothing was launched";
                throw c.Refuse(
                    new JsonObject
                    {
                        ["refused"] = "role-live",
                        ["member"] = name,
                        ["stream"] = stream is null ? JsonValue.Null : stream,
                        ["message"] = message,
                    },
                    message);
            }
        }
    }

    /// <summary>Whether the resolved roster has a member of <paramref name="role"/>; a sub-orchestrator has none unless the user wrote one.</summary>
    private static bool RosterDefines(RosterContext c, string role, bool noRosterCounts = false)
    {
        var services = c.Services;
        OrchestratorSession.Rebind(c);
        TeamScopes.ResolveWritableTeamScope(c, c.Dir);
        var found = services.Config.ResolveRoster(c.Cwd, services.Roster.TeamRosterKey(c.Dir, c.TeamFile), c.RepoBasename, c.Registry);
        if (!found.IsMap)
        {
            return noRosterCounts;
        }

        return found.Member("members") is { IsArray: true } members
            && members.AsArray().Select(MemberPlanning.Obj).Any(m => MemberPlanning.Text(m, "role") == role);
    }

    /// <summary>
    /// Fills the model, effort and autoMode <paramref name="adHoc"/> left unset from the first member of <paramref name="roster"/>
    /// with its role, at whatever level the roster resolved. Nothing else is taken from the row, and nothing at all when the row
    /// is of another kind: a row's model and effort belong to its harness.
    /// </summary>
    private static void FillFromRosterRow(RosterContext c, JsonObject adHoc, JsonValue roster)
    {
        if (!roster.IsMap || roster.Member("members") is not { IsArray: true } members)
        {
            return;
        }

        var role = MemberPlanning.Text(adHoc, "role");
        var row = members.AsArray().Select(MemberPlanning.Obj).FirstOrDefault(m => MemberPlanning.Text(m, "role") == role);
        if (row is null || MemberPlanning.KindOf(c, row) != MemberPlanning.KindOf(c, adHoc))
        {
            return;
        }

        foreach (var key in new[] { "model", "effort", "autoMode" })
        {
            if (adHoc.Get(key) is not { IsTruthy: true } && row.Get(key) is { IsTruthy: true } fill)
            {
                adHoc[key] = fill;
            }
        }
    }

    internal static int RunSpawnAdHoc(RosterContext c)
    {
        if (c.Services.Launch is null)
        {
            c.Stderr.WriteLine("ah: roster spawn-ad-hoc is not implemented natively: the launch layer is not composed");
            return 70;
        }

        RejectUnknownFlags(
            c,
            "spawn-ad-hoc",
            AdHocFlags,
            "--role, --model, --effort, --route, --kind, --harness, --command, --args, --auto-mode, --on-missing, --team, --cwd, --dry-run, --allow-global, --orchestrator-pid, --names-in-use, --here, --tab, --split");
        var config = c.Services.Config;
        var launch = c.Services.Launch;
        var role = c.Opts.Text("role") ?? (c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null);
        var formalPath = $"Ask the user which role they want, or for a whole team use Skill {config.PluginName}:agent-team (Create).";
        var roles = config.ChainRoles(c.Registry);
        if (string.IsNullOrEmpty(role))
        {
            throw new RosterFailure(
                $"spawn-ad-hoc: under-specified — no role named (spawn-ad-hoc <role>, one of {string.Join(", ", roles)}). {formalPath}");
        }

        if (!roles.Contains(role))
        {
            throw new RosterFailure(
                $"spawn-ad-hoc: under-specified — {JsonText.Quote(role)} is not a role spawn-ad-hoc can stand up; it spawns exactly one of {string.Join(", ", roles)}. {formalPath}");
        }

        var adHoc = MemberFlags.FromFlags(c, role, "spawn-ad-hoc");
        var custom = MemberPlanning.Text(adHoc, "kind") == launch.Registry.KindCustom;
        if (custom)
        {
            SubOrchestratorSpawn.RefuseCustomKind(c, "spawn-ad-hoc");
        }

        if (adHoc.Get("route") is not { IsTruthy: true })
        {
            // a non-claude kind is reached only through its pane, so that is its route when none is given
            var otherKind = MemberPlanning.Text(adHoc, "kind") is { Length: > 0 } kind && kind != "claude";
            adHoc["route"] = custom ? launch.Registry.RouteMail : otherKind ? "pane" : "peer";
        }

        if (adHoc.Get("onMissing") is { } && MemberPlanning.Text(adHoc, "route") == "subagent")
        {
            throw new RosterFailure("on-missing applies only to peer-routed members (this member's route is \"subagent\")");
        }

        var errors = custom ? launch.Rules.ValidateTeamMember(adHoc, c.Registry) : launch.Rules.ValidateMember(adHoc, c.Registry);
        if (errors.Count > 0)
        {
            throw new RosterFailure(string.Join("; ", errors));
        }

        if (!custom && !MemberPlanning.HasPane(c, adHoc.Get("route")))
        {
            throw new RosterFailure(
                $"spawn-ad-hoc: route {JsonText.Stringify(adHoc.Get("route")!.Value, 0)} has no session to spawn — a subagent-routed member is dispatched on demand by the Agent tool, so there is nothing to launch");
        }

        var dir = c.Dir;
        c.AdHocForNameCheck = adHoc;
        OrchestratorSession.Rebind(c);
        TeamScopes.ResolveWritableTeamScope(c, dir);
        if (c.Services.Roster.ReadTeamRecord(dir, c.TeamFile).IsNull)
        {
            TeamScopes.CheckNewTeamName(c, dir);
        }

        var existingTeam = c.Services.Roster.ReadTeamRecord(dir, c.TeamFile);
        var existingMembers = existingTeam.Member("members") is { IsArray: true } em ? em.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var sameRole = existingMembers.Where(m => MemberPlanning.Text(m, "role") == role).ToList();
        var forNaming = new JsonArray();
        foreach (var m in sameRole)
        {
            forNaming.Add(new JsonObject { ["role"] = m.Get("role") ?? JsonValue.Null });
        }

        forNaming.Add(adHoc);
        var names = config.RosterMemberNames(forNaming, c.RepoBasename);
        var derivedName = MemberPlanning.Text(MemberPlanning.Obj(names[names.Count - 1]), "name") ?? string.Empty;
        var teamPrefix = c.RepoBasename;
        var highestUsed = UsedSuffix.Highest(c, dir, role, teamPrefix);
        if (highestUsed >= UsedSuffix.Of(derivedName, teamPrefix, role))
        {
            derivedName = MemberNames.Fit(teamPrefix, role, highestUsed + 1);
        }

        var exactName = SubOrchestratorSpawn.Is(c, role) && c.Opts.Text("stream") is { Length: > 0 } soStream
            ? SubOrchestratorSpawn.NameOf(c, dir, soStream)
            : null;
        var held = c.Services.Roster.TeamMemberNameSet(dir, c.TeamFile).Select(n => MemberPlanning.Text(n)).ToHashSet(StringComparer.Ordinal);
        var next = Math.Max(highestUsed, UsedSuffix.Of(derivedName, teamPrefix, role));
        while (held.Contains(derivedName))
        {
            next++;
            derivedName = MemberNames.Fit(teamPrefix, role, next);
        }

        adHoc["name"] = exactName ?? derivedName;

        var renamed = MemberPlanning.RenameInUse(c, [adHoc], c.RepoBasename, existingMembers.Select(m => (JsonValue)m).ToList())[0];
        foreach (var key in renamed.Keys)
        {
            adHoc[key] = renamed[key];
        }

        if (exactName is null && MemberPlanning.Text(adHoc, "name") is { } finalName && finalName.Length > MemberNames.MaxLength)
        {
            var tooLong = $"spawn-ad-hoc: member name {finalName} is longer than {MemberNames.MaxLength} characters even with the team prefix cut — shorten the role name — nothing was written";
            throw c.Refuse(new JsonObject { ["refused"] = "member-name-too-long", ["role"] = role, ["message"] = tooLong }, tooLong);
        }

        if (custom)
        {
            if (LoadThrottle.Refuse(c, MemberPlanning.Text(adHoc, "name"), c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile)) is { } customTempFail)
            {
                return customTempFail;
            }

            c.Out(WithDespiteLoad(c, SpawnCustomMember(c, adHoc)));
            return 0;
        }

        MemberFlags.RequireHerdrName(c, adHoc, "spawn-ad-hoc");
        return Finish(c, Core(c, role, "spawn-ad-hoc", adHoc));
    }

    private static int Finish(RosterContext c, SpawnOutcome outcome)
    {
        if (outcome.Result is { } result)
        {
            c.Out(WithDespiteLoad(c, WithPaneMessage(c, result)));
        }

        return outcome.Exit;
    }

    /// <summary>A spawn the owning Orchestrator's <c>--despite-load</c> let through says so with <c>despite_load: true</c>.</summary>
    private static JsonObject WithDespiteLoad(RosterContext c, JsonObject result)
    {
        if (!c.DespiteLoad)
        {
            return result;
        }

        var copy = result.Clone();
        copy["despite_load"] = true;
        return copy;
    }

    /// <summary>A spawn or already-live result for a pane-driven member says how to brief it; any other result is returned as it is.</summary>
    private static JsonObject WithPaneMessage(RosterContext c, JsonObject result)
    {
        var member = result.Get("member");
        var alreadyLive = result.Get("reason") is { IsText: true } why && why.AsString() == "already live";
        if (member is not { IsMap: true } m || !c.Services.Config.IsPaneMember(m) || (result.Get("message") is not null && !alreadyLive))
        {
            return result;
        }

        var copy = result.Clone();
        var pane = c.Services.Config.PaneSendText(m, JsonValue.Null, JsonValue.FromString(c.Cwd));
        copy["message"] = result.Get("message") is { IsText: true } lead ? $"{lead.AsString()} {pane}" : pane;
        return copy;
    }

    internal sealed record SpawnOutcome(JsonObject? Result, int Exit);

    // ---------------------------------------------------------------- the spawn

    /// <summary>
    /// Puts the launch-settings result on the spawn output as <c>permissions: {file, cause, remedy}</c> (no key when no writer ran). A
    /// member that launched without its rules also gets one warning line on stderr and a <c>member-launched-bare</c> event for whoever
    /// can fix it: the top Orchestrator when only it can ask the user, else the spawner (the lane's lead for a lane, else the Orchestrator).
    /// </summary>
    private static void ReportPermissions(RosterContext c, string dir, string memberName, JsonObject spawn, JsonObject spawnOut)
    {
        if (spawn.Get("permissions") is not { IsMap: true } permissions)
        {
            return;
        }

        var file = permissions.AsObject().Get("file") ?? JsonValue.Null;
        var cause = MemberPlanning.Text(permissions.AsObject(), "cause") ?? "none";
        if (cause == "none")
        {
            spawnOut["permissions"] = new JsonObject { ["file"] = file, ["cause"] = "none", ["remedy"] = JsonValue.Null };
            return;
        }

        var remedy = ConfigModule.LaunchRemedy(cause);
        spawnOut["permissions"] = new JsonObject { ["file"] = JsonValue.Null, ["cause"] = cause, ["remedy"] = remedy };
        var text = $"permissions: none ({cause}) — {remedy}";
        c.Stderr.Write($"ah roster: warning — {memberName}: {text}\n");
        var lane = c.ParentOwnerLane;
        var recipient = (lane is not null, ConfigModule.LaunchCauseNeedsTopOrchestrator(cause)) switch
        {
            (true, true) => $"orchestrator@{lane!.Manager}",
            (true, false) => $"{lane!.Member}@{lane.Manager}",
            _ => $"orchestrator@{c.ProgressTeam}",
        };
        try
        {
            _ = c.Services.EventMail?.SendEvent(
                c.Cwd,
                dir,
                $"member-launched-bare:{c.ProgressTeam}:{memberName}:{cause}",
                "member-launched-bare",
                $"{memberName} launched bare — {text}",
                new JsonObject { ["member"] = memberName, ["team"] = c.ProgressTeam, ["cause"] = cause, ["remedy"] = remedy },
                [recipient]);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.Stderr.Write($"note: {recipient} not told that {memberName} launched bare\n");
        }
    }

    /// <summary>
    /// Whether <paramref name="label"/> returns without waiting for the member to register, leaving the wait to a detached
    /// <c>confirm-registration</c>: spawn-one and spawn-ad-hoc do; a resume keeps its wait.
    /// </summary>
    private static bool ConfirmsLater(string label) => label is "spawn-one" or "spawn-ad-hoc";

    /// <summary>One member's launch for <paramref name="label"/>: a verb of its own or the resume that runs it per member.</summary>
    internal static SpawnOutcome Spawn(RosterContext c, string? role, string label, JsonObject? adHoc) => Core(c, role, label, adHoc);

    private static SpawnOutcome Core(RosterContext c, string? role, string label, JsonObject? adHoc)
    {
        var services = c.Services;
        var config = services.Config;
        var launch = services.Launch!;
        var roster = services.Roster;
        var dir = c.Dir;
        OrchestratorSession.Rebind(c);
        TeamScopes.ResolveWritableTeamScope(c, dir);
        HoldRefusal.RefuseIfHeld(c);
        if (roster.ReadTeamRecord(dir, c.TeamFile).IsNull)
        {
            TeamScopes.CheckNewTeamName(c, dir);
        }

        var chain = config.ChainRoles(c.Registry);
        if (role is null || !chain.Contains(role))
        {
            var shown = role is null ? "undefined" : JsonText.Quote(role);
            throw new RosterFailure($"{label}: role must be one of {string.Join(", ", chain)}, got {shown}");
        }

        var found = config.ResolveRoster(c.Cwd, roster.TeamRosterKey(dir, c.TeamFile), c.RepoBasename, c.Registry);
        var resolved = adHoc is not null ? TeamScopes.AdHocRoster(found) : found;
        if (adHoc is not null)
        {
            FillFromRosterRow(c, adHoc, found);
        }

        if (!resolved.IsMap && adHoc is null)
        {
            throw new RosterFailure(
                $"no roster configured for {c.Cwd}; run `spawn-ad-hoc {role}` instead (it needs no roster), or run the /wrangl:agent-roster skill's Init flow to define one");
        }

        var rosterMembers = resolved.IsMap && resolved.Member("members") is { IsArray: true } rm ? rm.AsArray().Select(MemberPlanning.Obj).ToList() : [];
        var candidates = adHoc is not null ? [adHoc] : rosterMembers.Where(m => MemberPlanning.Text(m, "role") == role).ToList();
        if (candidates.Count == 0)
        {
            var roles = rosterMembers.Select(m => MemberPlanning.JsConvertText(m.Get("role"))).Distinct().ToList();
            throw new RosterFailure($"{label}: no {role} member in the roster — roles it defines: {(roles.Count > 0 ? string.Join(", ", roles) : "(none)")}");
        }

        var team = roster.ReadTeamRecord(dir, c.TeamFile);
        SubOrchestratorSpawn.CheckLaneSpawn(c, dir, label, role!, team);
        var stream = SpawnStream(c, dir, team, label);
        var isSubOrchestrator = SubOrchestratorSpawn.Is(c, role);
        var laneCaps = SubOrchestratorSpawn.LaneCapsOf(c, label, isSubOrchestrator);
        if (isSubOrchestrator)
        {
            SubOrchestratorSpawn.Preflight(c, dir, label, team, stream, role!);
        }
        else
        {
            SubOrchestratorSpawn.CheckLaneMembers(c, label, team);
        }

        var subOrchestratorName = isSubOrchestrator ? SubOrchestratorSpawn.NameOf(c, dir, stream!.Name) : null;
        var streamTree = stream is null ? null : MemberPlanning.Text(stream.Record.Member("worktree")) is { Length: > 0 } recorded ? recorded : null;
        c.LaunchCwd = streamTree is not null ? LaunchableTree(c, stream!, streamTree, label) : null;
        if (c.DaemonPrincipal && c.PinnedRoot is { } pinned
            && services.Hierarchy.Messages.RealCwd(c.LaunchCwd ?? c.Cwd) != services.Hierarchy.Messages.RealCwd(pinned))
        {
            throw new RosterFailure($"{label}: expected-root-differs: the row records {pinned} but the launch would start in {c.LaunchCwd ?? c.Cwd}; nothing was launched");
        }

        var records = team.Member("members") is { IsArray: true } tm ? tm.AsArray().ToList() : [];
        var claimed = adHoc is not null
            ? candidates
            : MemberPlanning.ClaimRecords(candidates, records.Where(r => r.Member("role") is { IsText: true } rr && rr.AsString() == role).ToList());
        var named = adHoc is not null
            ? candidates
            : MemberPlanning.RenameInUse(
                c,
                claimed,
                c.RepoBasename,
                records,
                name =>
                {
                    var record = records.FirstOrDefault(r => r.Member("name") is { IsText: true } n && n.AsString() == name);
                    var st = Liveness(c, dir, record);
                    return st.IsLive || st.Indeterminate;
                });
        if (c.Opts.IsTrue("member"))
        {
            throw new RosterFailure($"{label}: --member requires a value (the derived member name)");
        }

        JsonObject member;
        if (adHoc is not null)
        {
            member = adHoc;
        }
        else if (c.Opts.Text("member") is { } wanted)
        {
            member = named.FirstOrDefault(m => MemberPlanning.Text(m, "name") == wanted)
                ?? throw new RosterFailure(
                    $"{label}: no member named {wanted} for role {role} in the roster — it defines: " +
                    (named.Count > 0 ? string.Join(", ", named.Select(m => MemberPlanning.JsConvertText(m.Get("name")))) : "(none)"));
        }
        else if (named.Count == 1)
        {
            member = named[0];
        }
        else
        {
            member = named.FirstOrDefault(m => !Liveness(c, dir, m).IsLive) ?? named[^1];
        }

        if (subOrchestratorName is not null)
        {
            member = MemberPlanning.With(member, "name", subOrchestratorName);
        }

        var renamedNow = (adHoc is not null ? member.Get("renamed_from") is { IsTruthy: true } : !claimed.Contains(member))
            ? new List<JsonObject> { member }
            : [];
        if (adHoc is null && c.Opts.Has("model"))
        {
            if (c.Opts.Text("model") is not { } model)
            {
                throw new RosterFailure($"{label}: --model requires a value");
            }

            var modelErrors = MemberPlanning.InvocationModelErrors(c, member, model);
            if (modelErrors is null)
            {
                throw new RosterFailure(
                    $"{label}: --model: {MemberPlanning.JsConvertText(member.Get("name"))} is kind {JsonText.Quote(MemberPlanning.KindOf(c, member))}, and a model applies only to kind claude");
            }

            if (modelErrors.Count > 0)
            {
                throw new RosterFailure($"{label}: --model: {string.Join("; ", modelErrors)}");
            }

            member = MemberPlanning.WithModel(member, model);
        }

        if (adHoc is not null && subOrchestratorName is null && label != "resume")
        {
            // an ad hoc spawn takes the next free suffix: a derived name another member or a live session holds is stepped past;
            // a resume keeps the member's own name, whose record is still in the team file
            var suffix = Math.Max(1, UsedSuffix.Of(MemberPlanning.Text(member, "name") ?? string.Empty, c.RepoBasename, role));
            var free = false;
            for (var attempt = 0; attempt < 200 && !free; attempt++)
            {
                var current = MemberPlanning.Text(member, "name");
                if (!records.Any(r => MemberPlanning.Text(r.Member("name")) == current) && !Liveness(c, dir, member).IsLive)
                {
                    free = true;
                    continue;
                }

                suffix++;
                member = MemberPlanning.With(member, "name", MemberNames.Fit(c.RepoBasename, role, suffix));
            }

            if (!free)
            {
                throw new RosterFailure($"{label}: no free {MemberNames.Fit(c.RepoBasename, role)} name found up to suffix {suffix} — nothing was launched or written. Dismiss or untrack members of this team, or spawn under a different --team");
            }
        }

        double? newTeamPid = team.IsNull ? TeamRecords.NewTeamOwnerPid(c, label) : null;
        var byName = adHoc is not null || candidates.Count > 1 || subOrchestratorName is not null;
        var recordName = member.Get("record") is { IsMap: true } mr && mr.Member("name") is { } rn ? rn : member.Get("name") ?? JsonValue.Null;
        bool Matches(JsonValue m) => byName
            ? SameJson(m.Member("name") ?? JsonValue.Null, recordName)
            : m.Member("role") is { IsText: true } r && r.AsString() == role;
        var existing = records.FirstOrDefault(Matches);
        var existingIsRecord = records.Any(Matches);
        c.RequirePreflightDryRun();
        var selfState = c.PreflightStop ? new Live(false, false, "about to be stopped", new JsonObject()) : Liveness(c, dir, member);
        var kind = MemberPlanning.KindOf(c, member);
        var trust = RefuseUntrustedHarnessCwd(c, member, kind, c.LaunchCwd ?? c.Cwd);
        // A member whose liveness cannot be decided is not proven live: the spawn goes ahead with a warning (ah2-44). One that is
        // proven live, or still starting, is answered below as before.
        var unknownLife = new List<string>();
        if (selfState.Indeterminate)
        {
            unknownLife.Add(UnknownLifeWarning(MemberPlanning.JsConvertText(member.Get("name")), selfState.Why));
        }

        Live? existingState = null;
        if (existingIsRecord && !c.PreflightStop)
        {
            existingState = Liveness(c, dir, MemberPlanning.With(member, "name", existing.Member("name") ?? JsonValue.Null));
            if (existingState.Indeterminate)
            {
                if (existingState.Why.StartsWith("starting (", StringComparison.Ordinal))
                {
                    // a launch of this very member is in flight: that is known, not unknown, and a second one would double it
                    throw new RosterFailure(
                        $"{label}: member {MemberPlanning.JsConvertText(existing.Member("name"))} is {existingState.Why}. Refusing to spawn over it; retry once it has registered or the window has passed.");
                }

                var existingName = MemberPlanning.JsConvertText(existing.Member("name"));
                if (!unknownLife.Any(w => w.Contains($" {existingName} ", StringComparison.Ordinal)))
                {
                    unknownLife.Add(UnknownLifeWarning(existingName, existingState.Why));
                }
            }
        }

        var liveRecord = existingState is { IsLive: true } ? existing : (JsonValue?)null;
        var memberName = MemberPlanning.JsConvertText(member.Get("name"));
        if (adHoc is not null && (selfState.IsLive || liveRecord is not null))
        {
            throw new RosterFailure(
                $"{label}: the derived name {memberName} is already held by a live session outside this team's record — nothing was launched or written. " +
                $"Free the name with `dismiss {memberName}` or `untrack {memberName}`, or spawn under a different prefix with --team <other>");
        }

        if (selfState.IsLive || liveRecord is not null)
        {
            var outMember = liveRecord ?? (existingIsRecord ? existing : (JsonValue)new JsonObject
            {
                ["role"] = member.Get("role") ?? JsonValue.Null,
                ["name"] = member.Get("name") ?? JsonValue.Null,
            });
            var state = selfState.IsLive ? selfState : existingState!;
            var liveName = MemberPlanning.JsConvertText(outMember.Member("name") ?? JsonValue.Null);
            var liveStream = outMember.Member("stream") is { IsText: true } liveStreamText ? liveStreamText.AsString() : null;
            if (!byName && stream is { } asked && asked.Name != liveStream && !c.Opts.IsTrue("replace-live"))
            {
                if (c.Services.OwnerProof?.IsUser() == true)
                {
                    // ah2-76: the user, and the session the user made admin, are never refused: a second member of the role
                    // on another stream is what spawn-ad-hoc does, so do that.
                    c.Stderr.WriteLine($"ah: a {role} is already live on stream {liveStream ?? "none"}; spawning a second one on {asked.Name} (as spawn-ad-hoc)");
                    return new SpawnOutcome(null, RunSpawnAdHoc(c));
                }

                var roleLive =
                    $"ah: a {role} is already live: {liveName} on stream {liveStream ?? "none"}. spawn-one keeps one {role} per team; for a second on {asked.Name}, run ah roster spawn-ad-hoc {role} --stream {asked.Name}; nothing was launched";
                throw c.Refuse(
                    new JsonObject
                    {
                        ["refused"] = "role-live",
                        ["member"] = liveName,
                        ["stream"] = liveStream is null ? JsonValue.Null : liveStream,
                        ["message"] = roleLive,
                    },
                    roleLive);
            }

            var noop = $"{liveName} is already live{(liveStream is null ? string.Empty : $" on stream {liveStream}")}; nothing was launched. For a second {role}: ah roster spawn-ad-hoc {role} [--stream <ws>]";
            c.Stderr.WriteLine(noop);
            var result = new JsonObject { ["spawned"] = false, ["reason"] = "already live", ["member"] = outMember, ["message"] = noop };
            RosterJson.Spread(result, trust);
            if (byName && c.Opts.Text("member") is null)
            {
                var live = new JsonArray();
                foreach (var cand in named)
                {
                    var st = Liveness(c, dir, cand);
                    if (st.IsLive || st.Indeterminate)
                    {
                        var n = MemberPlanning.JsConvertText(cand.Get("name"));
                        live.Add(st.Indeterminate ? $"{n} (liveness unknown)" : n);
                    }
                }

                result["role"] = role;
                result["candidates_live"] = live;
            }

            if (state.Raw.Get("agent_status") is { } agentStatus)
            {
                result["agent_status"] = agentStatus;
            }

            if (state.Raw.Get("ready") is { } ready)
            {
                result["ready"] = ready;
            }

            return new SpawnOutcome(result, 0);
        }

        member = MemberPlanning.WithLaunchModel(c, member);
        var needing = MemberPlanning.MembersNeedingModel(c, [member], rosterMembers);
        if (needing.Count > 0)
        {
            throw MemberPlanning.RefuseMemberModels(c, needing, adHoc is not null);
        }

        if (MemberPlanning.AdviseTierBlocked(c, member))
        {
            throw MemberPlanning.RefuseAdviseTier(c, member);
        }

        WorkTreeAssignment.RefuseMainCheckout(
            c, label, memberName, MemberPlanning.Text(member, "role"), services.Hierarchy.Messages.RealCwd(c.LaunchCwd ?? c.Cwd), streamTree is not null);
        if (stream is null)
        {
            WorkTreeAssignment.RefuseLiveOccupant(
                c, label, memberName, MemberPlanning.Text(member, "role"), WorkTreeAssignment.WorkRootOf(c, services.Hierarchy.Messages.RealCwd(c.Cwd)), team);
        }
        if (stream is not null && c.Opts.Has("cwd"))
        {
            // Only the stream records' worktree paths are read, and those are fixed when the stream opens, so this needs no stream lock.
            WorkTreeAssignment.RefuseCrossStreamRoot(
                c, label, memberName, stream.Name, WorkTreeAssignment.WorkRootOf(c, services.Hierarchy.Messages.RealCwd(c.Cwd)), team, other => $"use --stream {other}, or omit the root");
        }

        WarnMixedPrefix(c, dir, member, team);
        var tmpWarnings = new List<string>();
        var isOtherKind = kind != launch.Registry.KindDefault;
        var memberTmp = MemberTmp.Prepare(c, memberName, isOtherKind, create: !c.Opts.IsTrue("dry-run"), tmpWarnings, resume: label == "resume");
        if (memberTmp is { Made: true } && !isOtherKind)
        {
            c.MemberTmp[memberName] = memberTmp.Export;
        }

        var transport = c.Opts.IsTrue("headless") ? "headless" : c.LaunchTransport ?? launch.Transports.Detect();
        var blockRoute = resolved.IsMap ? resolved.Member("route") : null;
        var candidateRoute = CreatePlanning.EffectiveRoute(member, blockRoute);
        JsonValue memberRoute = candidateRoute is { } cr && MemberPlanning.HasPane(c, cr) ? cr : JsonValue.FromString("peer");
        var planEntry = CreatePlanning.PlanEntry(c, CreatePlanning.WithRoute(member, memberRoute), memberRoute, transport);
        var spawn = planEntry.Get("spawn") is { IsMap: true } sp ? sp.AsObject() : new JsonObject();

        // RP-D6: an unset param is reported, a dry run included, and never stops the launch
        var paramWarnings = CreatePlanning.MissingParamWarnings([planEntry]);
        foreach (var w in paramWarnings)
        {
            c.Stderr.Write($"ah roster: warning — {w}\n");
        }

        if (transport == "headless")
        {
            planEntry["headless_prompt"] = c.Opts.Text("prompt") ?? string.Empty;
        }

        JsonObject layout;
        if (!team.IsNull)
        {
            var stored = MemberPlanning.Text(team.Member("layout"));
            layout = new JsonObject { ["mode"] = stored is "auto" or "columns" or "grid" ? stored : "auto" };
        }
        else
        {
            layout = CreatePlanning.CreateLayout(c);
        }

        var mode = MemberPlanning.Text(layout, "mode") ?? "auto";
        if (spawn.Get("validation") is { IsMap: true } v && v.Member("refused") is { IsTruthy: true })
        {
            throw new RosterFailure($"{label}: {MemberPlanning.JsConvertText(spawn.Get("refuse"))}");
        }

        if (spawn.Get("refusal") is { IsMap: true } refusal)
        {
            var fields = refusal.AsObject();
            throw c.Refuse(fields, MemberPlanning.JsConvertText(fields.Get("message")));
        }

        if (label is "spawn-one" or "spawn-ad-hoc" && LoadThrottle.Refuse(c, memberName, team) is { } tempFail)
        {
            return new SpawnOutcome(null, tempFail);
        }

        var validation = new JsonObject();
        if (spawn.Get("validation") is { IsTruthy: true } validationValue)
        {
            validation["validation"] = validationValue;
        }

        if (c.Opts.IsTrue("dry-run"))
        {
            var dry = new JsonObject
            {
                ["dry_run"] = true,
                ["role"] = member.Get("role") ?? JsonValue.Null,
                ["name"] = member.Get("name") ?? JsonValue.Null,
                ["mode"] = mode,
                ["launch"] = spawn.Get("launch") ?? JsonValue.Null,
            };
            RosterJson.Spread(dry, validation);
            RosterJson.Spread(dry, trust);
            MemberPlanning.SpreadRenamed(dry, renamedNow);
            CodexTrustNeeds.Attach(dry, CodexTrustNeeds.For(c, [member], c.LaunchCwd));
            if (planEntry.Get("params") is { } dryParams)
            {
                dry["params"] = dryParams;
            }

            if (paramWarnings.Count > 0)
            {
                var dryWarnings = new JsonArray();
                dryWarnings.AddRange(paramWarnings.Select(w => (JsonValue)w));
                dry["warnings"] = dryWarnings;
            }

            return new SpawnOutcome(dry, 0);
        }

        var seedPanes = records
            .Where(r => MemberPlanning.HasPane(c, r.Member("route")) && r.Member("transport_id") is { IsNull: false }
                && MemberPlanning.Text(r.Member("name")) != memberName)
            .Select(r => MemberPlanning.JsConvertText(r.Member("transport_id")))
            .ToList();
        using var streamLock = stream is not null && transport == "herdr" ? HoldStreamTab(c, dir, stream, ref team) : null;
        if (stream is not null)
        {
            // The stream's tab lock is held, so the member rows read here include every earlier spawn into this stream.
            WorkTreeAssignment.RefuseLiveOccupant(
                c, label, memberName, MemberPlanning.Text(member, "role"), WorkTreeAssignment.WorkRootOf(c, services.Hierarchy.Messages.RealCwd(c.LaunchCwd ?? c.Cwd)), team);
        }


        var placement = c.IntoPane is { Length: > 0 } ? null : PanePlacement.Parse(c, label);
        if (placement is not null && transport != "herdr")
        {
            throw new RosterFailure($"{label}: --here and --tab place a herdr pane; this team's transport is {transport}");
        }

        var anchor = c.IntoPane is { Length: > 0 } intoPane && transport == "herdr"
            ? new Anchor(intoPane, null, records.Where(r => MemberPlanning.Text(r.Member("name")) == memberName).Select(r => MemberPlanning.Text(r.Member("tab_id"))).FirstOrDefault() ?? string.Empty)
            : stream is not null && transport == "herdr" ? StreamAnchor(c, dir, stream, team)
            : placement is not null ? PlacedAnchor(c, placement, label) : null;
        if (anchor is null && stream is null && transport == "herdr" && !c.Opts.IsTrue("split"))
        {
            anchor = RoleAnchor(c, dir, team, MemberPlanning.Text(member, "role") ?? role!, memberName);
        }
        var joinedZoom = stream is null && anchor is { Seed: { Length: > 0 } joinedPane } ? PaneZoom.ZoomedIn(c, joinedPane) : null;

        var launchCwd = spawn.Get("launch_cwd") is { IsTruthy: true } lcwd ? MemberPlanning.JsConvertText(lcwd) : c.Cwd;
        var spawnedAt = Ah.Core.Time.Timestamps.ToIsoString(services.Clock.UtcNow);
        var newRecord = new JsonObject();
        if (transport == "herdr" && launch.Herdr.Flavour == Ah.Core.Mux.HerdrFlavour.Core)
        {
            // written with the row before the launch, so a terminal whose address never reached the record can still be found
            var nonce = Ah.Launch.HerdrCore.MintSpawnNonce();
            newRecord["spawn_nonce"] = nonce;
            planEntry["spawn_nonce"] = nonce;
        }

        Copy(newRecord, "role", member, "role");
        Copy(newRecord, "name", member, "name");
        if (member.Get("renamed_from") is { IsTruthy: true } renamedFrom)
        {
            newRecord["renamed_from"] = renamedFrom;
        }

        newRecord["route"] = memberRoute;
        Copy(newRecord, "model", member, "model");
        Copy(newRecord, "effort", member, "effort");
        Copy(newRecord, "autoMode", member, "autoMode");
        newRecord["expected_root"] = services.Hierarchy.Messages.RealCwd(launchCwd);
        if (kind != launch.Registry.KindDefault)
        {
            newRecord["kind"] = kind;
        }

        if (launch.Rules.MemberArgs(member) is { } memberArgs)
        {
            var args = new JsonArray();
            args.AddRange(memberArgs);
            newRecord["args"] = args;
        }

        if (stream is not null)
        {
            var streamTab = anchor is not null ? anchor.TabId : MemberPlanning.Text(stream.Record.Member("tab_id"));
            if (!string.IsNullOrEmpty(streamTab))
            {
                newRecord["tab_id"] = streamTab;
            }

            newRecord["stream"] = stream.Name;
        }
        else if (anchor is not null && (c.IntoPane is not { Length: > 0 } || anchor.TabId.Length > 0))
        {
            newRecord["tab_id"] = anchor.TabId;
        }

        if (memberTmp is not null)
        {
            newRecord["tmp"] = memberTmp.Recorded;
        }

        newRecord["spawned_at"] = spawnedAt;
        if (ParentAction.ActorOf(c) is { } placedBy)
        {
            newRecord["placed_by"] = placedBy;
        }

        // The row is written before the launch so the new session finds itself at start; a failed launch takes it back out.
        JsonObject? outTeam = null;
        JsonObject? journaled = null;
        JsonValue? priorRow = null;
        string? rivalRefusal = null;
        List<JsonValue> rowsOfTheRoleBeforeLaunch = [];
        var laneStream = subOrchestratorName is not null ? stream!.Name : null;
        JsonValue? priorStreamEntry = null;
        JsonValue? writtenStreamEntry = null;
        var streamsWereAbsent = false;
        var teamWasNew = false;
        services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                var path = roster.TeamPath(dir, c.TeamFile);
                JsonValue freshTeam;
                string state;
                if (!doc.IsNull)
                {
                    state = "usable";
                    freshTeam = doc;
                }
                else
                {
                    var fresh = roster.TeamFileState(dir, c.TeamFile);
                    state = MemberPlanning.Text(fresh, "state") ?? "absent";
                    freshTeam = fresh.Get("team") ?? JsonValue.Null;
                    path = MemberPlanning.Text(fresh, "path") ?? path;
                }

                if (state == "unusable" || (!team.IsNull && state == "absent"))
                {
                    throw new RosterFailure(
                        $"{label}: {(state == "absent" ? $"the team file {path} is gone" : $"the team file {path} is no longer readable")} — it changed while {memberName} was being planned, so nothing was launched or written");
                }

                var freshMembers = freshTeam.Member("members") is { IsArray: true } fm ? fm.AsArray().ToList() : [];
                var rival = freshMembers.FirstOrDefault(Matches);
                var hasRival = freshMembers.Any(Matches);
                if (hasRival && (!existingIsRecord || !SameJson(rival.Member("transport_id") ?? JsonValue.Null, existing.Member("transport_id") ?? JsonValue.Null)))
                {
                    var rivalName = rival.Member("name") is { IsNull: false } rnm ? rnm : rival.Member("role") ?? JsonValue.Null;
                    throw new RosterFailure(
                        $"{label}: another spawn recorded {MemberPlanning.JsConvertText(rivalName)} (pane {MemberPlanning.JsConvertText(rival.Member("transport_id"))}) in {path} while {memberName} was being planned — its row was not overwritten; nothing was launched");
                }

                var next = freshTeam.IsMap
                    ? freshTeam.AsObject().Clone()
                    : TeamRecords.NewTeamRecord(c, resolved, transport, newTeamPid ?? double.NaN, mode);
                teamWasNew = !freshTeam.IsMap;
                streamsWereAbsent = freshTeam.Member("streams") is not { IsMap: true };
                priorStreamEntry = laneStream is not null && freshTeam.Member("streams") is { IsMap: true } priorStreams && priorStreams.Member(laneStream) is { IsMap: true } priorEntry ? priorEntry : (JsonValue?)null;
                priorRow = null;
                rowsOfTheRoleBeforeLaunch = freshMembers.Where(Matches).Skip(1).ToList();
                var list = new JsonArray();
                var replaced = false;
                foreach (var m in freshMembers)
                {
                    if (!replaced && Matches(m))
                    {
                        priorRow = m;
                        CarryFirstSpawn(c, newRecord, m);
                        list.Add(newRecord);
                        replaced = true;
                    }
                    else
                    {
                        list.Add(m);
                    }
                }

                if (!replaced)
                {
                    SubOrchestratorSpawn.GuardAppend(c, label, freshTeam, list.Count, memberName, null);
                    list.Add(newRecord);
                }

                next["members"] = list;
                journaled = ParentAction.Entry(c, label, memberName, stream?.Name);
                if (journaled is not null)
                {
                    next["parent_actions"] = Ah.Launch.LaunchModule.AppendedParentAction(freshTeam, journaled);
                }

                if (subOrchestratorName is not null)
                {
                    SubOrchestratorSpawn.Record(next, stream!.Name, subOrchestratorName, laneCaps!.Value);
                    writtenStreamEntry = next.Get("streams") is { IsMap: true } writtenStreams && writtenStreams.Member(stream.Name) is { IsMap: true } writtenEntry ? writtenEntry : (JsonValue?)null;
                }

                OrchestratorSession.Backfill(c, next);
                outTeam = next;
                return TeamFileOutcome.Write(next);
            });

        JsonValue CurrentTeam(JsonValue doc) => !doc.IsNull ? doc : roster.TeamFileState(dir, c.TeamFile).Get("team") ?? JsonValue.Null;

        bool TeamFileUnusable() => MemberPlanning.Text(roster.TeamFileState(dir, c.TeamFile), "state") == "unusable";

        void TakeRowBack()
        {
            try
            {
                services.Hierarchy.StateCalls.UpdateTeam(
                    dir,
                    c.TeamFile,
                    given =>
                    {
                        var doc = CurrentTeam(given);
                        var rows = doc.Member("members") is { IsArray: true } rs ? rs.AsArray().ToList() : [];
                        var at = rows.FindIndex(m => m.IsMap && MemberPlanning.Text(m.Member("name")) == memberName && MemberPlanning.Text(m.Member("spawned_at")) == spawnedAt);
                        if (!doc.IsMap || at < 0)
                        {
                            return null;
                        }

                        var list = new JsonArray();
                        for (var i = 0; i < rows.Count; i++)
                        {
                            if (i != at)
                            {
                                list.Add(rows[i]);
                            }
                            else if (priorRow is { } before)
                            {
                                list.Add(before);
                            }
                        }

                        if (teamWasNew && list.Count == 0)
                        {
                            return TeamFileOutcome.Clear();
                        }

                        var restored = doc.AsObject().Clone();
                        restored["members"] = list;
                        if (journaled is not null && ParentAction.Entry(c, "spawn-rollback", memberName, stream?.Name) is { } rollback)
                        {
                            restored["parent_actions"] = Ah.Launch.LaunchModule.AppendedParentAction(doc, rollback);
                        }

                        if (laneStream is not null && restored.Get("streams") is { IsMap: true } nowStreams
                            && nowStreams.Member(laneStream) is { IsMap: true } nowEntry && writtenStreamEntry is { } written
                            && JsonText.Stringify(nowEntry, 0) == JsonText.Stringify(written, 0))
                        {
                            var streams = nowStreams.AsObject().Clone();
                            if (priorStreamEntry is { } before)
                            {
                                streams[laneStream] = before;
                            }
                            else
                            {
                                streams.Remove(laneStream);
                            }

                            if (!streams.Keys.Any() && streamsWereAbsent)
                            {
                                restored.Remove("streams");
                            }
                            else
                            {
                                restored["streams"] = streams;
                            }
                        }

                        return TeamFileOutcome.Write(restored);
                    });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                c.Stderr.Write($"ah roster: warning — the row written for {memberName} before its launch could not be taken back: {e.Message}\n");
            }
        }

        LayoutAndLaunchResult batch;
        JsonObject launched;
        try
        {
            if (joinedZoom is not null && PaneZoom.Off(c, joinedZoom) is { } unzoomFailure)
            {
                throw new RosterFailure($"{label}: the role's tab is zoomed and herdr would not unzoom it ({unzoomFailure}) — nothing was launched");
            }

            try
            {
                batch = launch.Launcher.LayoutAndLaunch(
                    dir,
                    [planEntry],
                    transport,
                    mode,
                    c.LaunchCwd ?? c.Cwd,
                    label,
                    c.LaunchContext(),
                    new LayoutOptions(seedPanes, anchor is null ? null : new PlacementAnchor(anchor.Root, anchor.Seed), DeferRegistration: ConfirmsLater(label)));
            }
            finally
            {
                if (joinedZoom is not null)
                {
                    _ = PaneZoom.Back(c, joinedZoom, null);
                }
            }

            if (batch.Fail is not null)
            {
                throw new RosterFailure(batch.Fail);
            }

            if (batch.Partial is not null)
            {
                TakeRowBack();
                c.Out(batch.Partial);
                return new SpawnOutcome(null, 3);
            }

            launched = batch.Members[0];
            if (MemberPlanning.Text(launched, "launch_status") == "failed")
            {
                throw LaunchFailure(c, label, member, launched);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            TakeRowBack();
            throw;
        }

        var launchFields = new JsonObject();
        Copy(launchFields, "transport_id", launched, "transport_id");
        Copy(launchFields, Ah.Core.Mux.HerdrFlavour.Field, launched, Ah.Core.Mux.HerdrFlavour.Field);
        if (transport == "headless")
        {
            Copy(launchFields, "pid", launched, "pid");
            Copy(launchFields, "pid_start", launched, "pid_start");
        }

        if (launched.Get("appserver_pid") is { IsTruthy: true } appserverPid)
        {
            if (member.Get("adapter") is { IsNull: false } adapter)
            {
                launchFields["adapter"] = adapter;
            }

            launchFields["appserver_pid"] = appserverPid;
            if (launched.Get("appserver_thread_id") is { IsTruthy: true, IsText: true } thread)
            {
                launchFields["appserver_thread_id"] = thread;
            }
        }

        var adapterNote = launched.Get("adapter_note") is { IsTruthy: true } note
            ? note
            : launched.Get("spawn") is { IsMap: true } ls && ls.Member("adapter_note") is { IsTruthy: true } sn ? sn : (JsonValue?)null;
        if (adapterNote is { } an)
        {
            launchFields["adapter_note"] = an;
        }

        if (transport == "herdr"
            && launched.Get("launch_result") is { IsMap: true } lr
            && lr.Member("result")?.Member("pane") is { IsMap: true } pane)
        {
            if (pane.Member("tab_id") is { IsNull: false } tab)
            {
                launchFields["tab_id"] = tab;
            }

            if (pane.Member("workspace_id") is { IsNull: false } workspace)
            {
                launchFields["workspace_id"] = workspace;
            }
        }

        var orphaned = $"the session just launched is still running with transport_id {MemberPlanning.JsConvertText(launched.Get("transport_id"))} and is in no team record";
        services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            given =>
            {
                var doc = CurrentTeam(given);
                var rows = doc.Member("members") is { IsArray: true } rs ? rs.AsArray().ToList() : [];
                bool IsOurRow(JsonValue m) => m.IsMap && MemberPlanning.Text(m.Member("name")) == memberName && MemberPlanning.Text(m.Member("spawned_at")) == spawnedAt;
                var at = rows.FindIndex(IsOurRow);
                if (!byName && doc.IsMap
                    && rows.FirstOrDefault(m => m.IsMap && !IsOurRow(m) && Matches(m) && !rowsOfTheRoleBeforeLaunch.Any(b => SameJson(b, m)) && !(priorRow is { } before && SameJson(before, m))) is { IsMap: true } rival)
                {
                    var rivalName = rival.Member("name") is { IsNull: false } rnm ? rnm : rival.Member("role") ?? JsonValue.Null;
                    rivalRefusal = $"{label}: another spawn recorded {MemberPlanning.JsConvertText(rivalName)} (pane {MemberPlanning.JsConvertText(rival.Member("transport_id"))}) in {roster.TeamPath(dir, c.TeamFile)} while {memberName} was launching — its row was not overwritten; {orphaned}";
                    return null;
                }

                if (!doc.IsMap && TeamFileUnusable())
                {
                    throw new RosterFailure($"{label}: the team file {roster.TeamPath(dir, c.TeamFile)} is no longer readable, so the launch fields of {memberName} were not recorded and the file was left alone; {orphaned}");
                }

                if (!doc.IsMap || at < 0)
                {
                    throw new RosterFailure($"spawn-row-replaced: {memberName} — the row written before the launch is gone or was replaced by another spawn, so the launch fields were not recorded; {orphaned}");
                }

                var merged = rows[at].AsObject().Clone();
                foreach (var key in launchFields.Keys)
                {
                    merged[key] = launchFields[key];
                }

                var list = new JsonArray();
                for (var i = 0; i < rows.Count; i++)
                {
                    list.Add(i == at ? merged : rows[i]);
                }

                var next = doc.AsObject().Clone();
                next["members"] = list;
                TeamRecords.CompleteAfterLaunch(next, launched, transport);
                outTeam = next;
                newRecord = merged;
                return TeamFileOutcome.Write(next);
            });

        if (rivalRefusal is not null)
        {
            TakeRowBack();
            throw new RosterFailure(rivalRefusal);
        }

        var outMemberRecord = newRecord;
        if (launched.Get("label") is { IsTruthy: true } labelValue)
        {
            outMemberRecord = newRecord.Clone();
            outMemberRecord["label"] = labelValue;
        }

        RosterLedger.Member(c, label, "spawned", memberName, MemberPlanning.Text(member, "role"));
        ParentAction.Notify(c, dir, journaled, "spawned");
        var spawnOut = new JsonObject { ["spawned"] = true, ["member"] = outMemberRecord };
        RosterJson.Spread(spawnOut, trust);
        TeamRegistration.Report(spawnOut, TeamRegistration.RegisterTeam(c, dir, outTeam!), c);
        OrchestratorSession.Warn(c, spawnOut);
        Copy(spawnOut, "team_id", outTeam!, "team_id");
        Copy(spawnOut, "roster_level", outTeam!, "roster_level");
        spawnOut["launch_cwd"] = spawn.Get("launch_cwd") ?? JsonValue.Null;
        ReportPermissions(c, dir, memberName, spawn, spawnOut);
        if (ConfirmsLater(label) && kind == launch.Registry.KindDefault && transport is "herdr" or "tmux"
            && launched.Get("transport_id") is { IsText: true } launchedPane)
        {
            var spawner = c.ParentOwnerLane is { } lane ? $"{lane.Member}@{lane.Manager}" : $"orchestrator@{c.ProgressTeam}";
            _ = c.Services.StartDetached(
                ConfirmRegistrationVerb.CommandLine(c, memberName, launchedPane.AsString(), transport, spawner),
                c.Cwd,
                "/dev/null");
        }

        RosterJson.Spread(spawnOut, validation);
        MemberPlanning.SpreadRenamed(spawnOut, renamedNow);
        var warnings = CreatePlanning.SpawnPlacementWarnings(c, dir);
        warnings.AddRange(paramWarnings);
        warnings.AddRange(unknownLife);
        var workRoot = streamTree ?? WorkTreeAssignment.WorkRootOf(c, services.Hierarchy.Messages.RealCwd(launchCwd));
        warnings.AddRange(tmpWarnings);
        var assignWarning = WorkTreeAssignment.Assign(c, memberName, MemberPlanning.Text(member, "role"), workRoot, stream?.Name, memberTmp is { Made: true } ? memberTmp : null);
        if (assignWarning is not null)
        {
            warnings.Add(assignWarning);
        }

        if (assignWarning is null && WorkTreeAssignment.SharedRoots(c, WorkTreeAssignment.TeamOf(c), outTeam!, memberName)
                .FirstOrDefault(r => r.Stream is null && r.Members.Contains(memberName)) is { } sharedRoot)
        {
            warnings.Add($"work root {sharedRoot.Root} is shared with live {string.Join(", ", sharedRoot.Members.Where(m => m != memberName))}");
        }

        if (adapterNote is { } noteText)
        {
            warnings.Add(MemberPlanning.JsConvertText(noteText));
        }

        if (warnings.Count > 0)
        {
            var list = new JsonArray();
            list.AddRange(warnings.Select(w => (JsonValue)w));
            spawnOut["warnings"] = list;
        }

        if (MemberPlanning.Text(launched, "launch_status") == "blocked-at-startup")
        {
            spawnOut["launch_status"] = launched.Get("launch_status")!.Value;
            spawnOut["launch_result"] = launched.Get("launch_result") ?? JsonValue.Null;
            spawnOut["ready"] = false;
        }

        CopyUnconfirmed(launched, spawnOut);

        if (launched.Get("blocked") is { IsTruthy: true } blocked)
        {
            spawnOut["blocked"] = blocked;
            if (launched.Get("message") is { } message)
            {
                spawnOut["message"] = message;
            }
        }

        if (stream is not null)
        {
            var current = launch.Streams.StreamsOf(outTeam!).Get(stream.Name) ?? stream.Record;
            var view = new JsonObject
            {
                ["name"] = stream.Name,
                ["worktree"] = RosterJson.OrNull(current.Member("worktree")),
                ["branch"] = RosterJson.OrNull(current.Member("branch")),
            };
            if (transport == "herdr")
            {
                var streamWarnings = new List<string>();
                var rendered = StreamSupport.RenderStream(
                    c, dir, outTeam!, stream.Name, StreamSupport.HerdrSnapshot(c, streamWarnings), streamWarnings);
                view["label"] = RosterJson.Text(rendered.Label);
                if (streamWarnings.Count > 0)
                {
                    var list = new JsonArray();
                    list.AddRange(streamWarnings.Select(w => (JsonValue)w));
                    view["warnings"] = list;
                }
            }

            spawnOut["stream"] = view;
        }

        var briefer = c.ParentOwnerLane is not null ? "sub-orchestrator" : "orchestrator";
        spawnOut["next"] = new JsonObject
        {
            ["name"] = memberName,
            ["terminal"] = newRecord.Get("transport_id") ?? JsonValue.Null,
            ["tab"] = newRecord.Get("tab_id") ?? JsonValue.Null,
            ["brief"] = $"{services.Config.CliCommand(CliTool.Msg)} new --to {MemberPlanning.Text(member, "role")} --from {briefer} --to-name {memberName} --slug <slug> --title \"<title>\" --cwd {c.Cwd}",
        };
        return new SpawnOutcome(spawnOut, 0);
    }

    // ---------------------------------------------------------------- joining a stream

    /// <summary>The stream a spawn joins, and its record as this command has it.</summary>
    internal sealed class StreamRef(string name, JsonValue record)
    {
        internal string Name { get; } = name;

        internal JsonValue Record { get; set; } = record;
    }

    /// <summary>Where a stream spawn's pane goes: an empty pane to use as is (<c>Root</c>) or a pane whose tab the split loop works in.</summary>
    private sealed record Anchor(string? Root, string? Seed, string TabId);

    /// <summary>The stream <c>--stream</c> names, refused before any pane opens when unknown or done; null without the flag.</summary>
    private static StreamRef? SpawnStream(RosterContext c, string dir, JsonValue team, string label)
    {
        string name;
        if (c.StreamOverride is { Length: > 0 } rejoin)
        {
            name = rejoin;
        }
        else if (c.Opts.Has("stream"))
        {
            name = c.Opts.Text("stream") ?? throw new RosterFailure($"{label}: --stream requires a value");
        }
        else
        {
            return null;
        }

        var record = c.Services.Launch!.Streams.StreamsOf(team).Get(name);
        if (record is not { IsTruthy: true } found)
        {
            var message =
                $"{label}: no stream named {JsonText.Quote(name)} in {c.Services.Roster.TeamPath(dir, c.TeamFile)} — open it first with stream-open {name}; nothing was launched";
            throw c.Refuse(new JsonObject { ["refused"] = "unknown-stream", ["stream"] = name, ["message"] = message }, message);
        }

        if (MemberPlanning.Text(found.Member("state")) == "done")
        {
            var reopen = $"ah roster stream-open {name}{StreamSupport.TeamFlagText(c)}";
            var message = $"{label}: stream {name} is done — re-open it with `{reopen}` first; nothing was launched";
            throw c.Refuse(
                new JsonObject
                {
                    ["refused"] = "stream-done",
                    ["stream"] = name,
                    ["next"] = new JsonArray { reopen },
                    ["message"] = message,
                },
                message);
        }

        return new StreamRef(name, found);
    }

    /// <summary>The stream's recorded work tree as the directory its member launches in; refused, before anything launches, when it is gone.</summary>
    private static string LaunchableTree(RosterContext c, StreamRef stream, string tree, string label)
    {
        if (c.Services.Files.Stat(tree) is not { Kind: FileKind.Directory })
        {
            var message = $"{label}: stream {stream.Name}'s work tree {tree} does not exist — nothing was launched";
            throw c.Refuse(
                new JsonObject { ["refused"] = "stream-worktree-missing", ["stream"] = stream.Name, ["work_root"] = tree, ["message"] = message },
                message);
        }

        return tree;
    }

    private static readonly TimeSpan StreamTabLockWait = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Holds the stream's tab lock from choosing the pane until the member is recorded in the team file, so spawns into one stream run
    /// one after another: the second sees the first's tab and member row and splits beside it instead of making a second tab or
    /// launching into the first's pane. Re-reads the team record and the stream's record once the lock is held.
    /// </summary>
    private static IDisposable HoldStreamTab(RosterContext c, string dir, StreamRef stream, ref JsonValue team)
    {
        var services = c.Services;
        var launch = services.Launch!;
        var teamPath = services.Roster.TeamPath(services.Roster.TeamHome(dir), c.TeamFile);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Ah.Core.Text.NodePath.Resolve(c.Cwd, teamPath) + "\n" + stream.Name))).ToLowerInvariant()[..16];
        var lockFile = Ah.Core.Text.NodePath.Join(services.Environment.AhHome, "run", $"stream-{hash}.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
        var held = launch.ResumeLocks.Acquire(lockFile, StreamTabLockWait);
        if (held is null)
        {
            var message = $"stream {stream.Name}: another spawn into this stream holds its tab lock — nothing was launched";
            throw c.Refuse(new JsonObject { ["refused"] = "stream-tab-busy", ["stream"] = stream.Name, ["message"] = message }, message);
        }

        team = services.Roster.ReadTeamRecord(dir, c.TeamFile);
        if (launch.Streams.StreamsOf(team).Get(stream.Name) is { IsMap: true } fresh)
        {
            stream.Record = fresh;
        }

        return held;
    }

    /// <summary>The waits before the second and third try of the herdr tab list (b472).</summary>
    private static readonly TimeSpan[] TabListRetryWaits = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3)];

    /// <summary>
    /// <c>herdr tab list</c>, tried up to three times, waiting 1 s and then 3 s between tries and logging one line per retry; the
    /// third failure is thrown as it came. Each try keeps herdr's own timeout.
    /// </summary>
    private static JsonValue TabList(RosterContext c)
    {
        var herdr = c.Services.Launch!.Herdr;
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return herdr.Invoke(["tab", "list"]);
            }
            catch (Exception error) when (error is not OutOfMemoryException && attempt <= TabListRetryWaits.Length)
            {
                c.Stderr.Write($"ah roster: herdr tab list slow, try {attempt} of {TabListRetryWaits.Length + 1}, {elapsed.ElapsedMilliseconds} ms elapsed\n");
                c.Services.Sleep(TabListRetryWaits[attempt - 1]);
            }
        }
    }

    /// <summary>
    /// Where a stream spawn's pane goes under herdr. A tab holding no stream member yet gets its one pane used as is; otherwise the
    /// split loop seeds from the earliest-joined stream member still in that tab. A stream with no live tab gets one first.
    /// </summary>
    private static Anchor StreamAnchor(RosterContext c, string dir, StreamRef stream, JsonValue team)
    {
        var launch = c.Services.Launch!;
        RosterFailure Unavailable(string detail)
        {
            var message = $"stream {stream.Name}: no tab to place the member in ({detail}) — nothing was launched";
            return c.Refuse(
                new JsonObject
                {
                    ["refused"] = "stream-tab-unavailable",
                    ["stream"] = stream.Name,
                    ["detail"] = detail,
                    ["message"] = message,
                },
                message);
        }

        var recordedTab = MemberPlanning.Text(stream.Record.Member("tab_id"));
        JsonArray? tabs;
        var listFailed = false;
        try
        {
            tabs = TabList(c).Member("result")?.Member("tabs") is { IsArray: true } listed ? listed.AsArray() : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // b472: every try failed, so the stream's recorded tab is used as is; a tab that is gone fails at its pane list below.
            // b536: a stream with no recorded tab gets a new one (EnsureStreamTab takes a null list) instead of a refusal.
            tabs = null;
            listFailed = true;
        }

        if (tabs is null && !listFailed)
        {
            throw Unavailable("herdr tab list produced unexpected shape (missing .result.tabs)");
        }

        JsonValue? tab = listFailed
            ? recordedTab is { Length: > 0 } ? (JsonValue?)new JsonObject { ["tab_id"] = recordedTab, ["workspace_id"] = JsonValue.Null } : null
            : recordedTab is { Length: > 0 }
                ? tabs!.Cast<JsonValue?>().FirstOrDefault(t => MemberPlanning.Text(t!.Value.Member("tab_id")) == recordedTab)
                : null;
        if (tab is null)
        {
            var label = $"{launch.Streams.StreamGlyph(MemberPlanning.Text(stream.Record.Member("state")), new JsonArray(), [])} {stream.Name}";
            var bare = stream.Record.AsObject().Clone();
            bare["tab_id"] = JsonValue.Null;
            var rootCwd = c.LaunchCwd is { Length: > 0 } launchCwd
                ? launchCwd
                : team.Member("expected_root") is { IsTruthy: true, IsText: true } er
                    ? er.AsString()
                    : c.Services.Hierarchy.Messages.RealCwd(c.Cwd);
            var made = StreamSupport.EnsureStreamTab(c, bare, label, rootCwd, tabs!);
            if (made.TabId is null)
            {
                throw Unavailable(made.Error ?? "undefined");
            }

            var updated = stream.Record.AsObject().Clone();
            updated["tab_id"] = made.TabId;
            if (made.RootPane is not null)
            {
                updated["root_pane_id"] = made.RootPane;
            }

            stream.Record = updated;
            StreamSupport.WriteStreamRecord(c, dir, stream.Name, updated);
            if (made.RootPane is { } rootPane)
            {
                return new Anchor(rootPane, null, made.TabId);
            }

            tab = new JsonObject { ["tab_id"] = made.TabId, ["workspace_id"] = JsonValue.Null };
        }

        var tabId = MemberPlanning.JsConvertText(tab.Value.Member("tab_id"));
        var workspace = tab.Value.Member("workspace_id") is { IsTruthy: true } w ? MemberPlanning.JsConvertText(w) : null;
        List<string> panes;
        try
        {
            List<string> listArgs = ["pane", "list"];
            if (workspace is not null)
            {
                listArgs.AddRange(["--workspace", workspace]);
            }

            var listing = launch.Herdr.Invoke(listArgs).Member("result")?.Member("panes");
            panes = listing is { IsArray: true } arr
                ? arr.AsArray().Where(p => MemberPlanning.Text(p.Member("tab_id")) == tabId).Select(p => MemberPlanning.JsConvertText(p.Member("pane_id"))).ToList()
                : throw new InvalidOperationException("Cannot read properties of undefined (reading 'panes')");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Unavailable(error.Message);
        }

        var memberPanes = launch.Streams.StreamMembers(team, stream.Name)
            .Select(m => m.Member("transport_id"))
            .Where(id => id is { IsTruthy: true })
            .Select(id => MemberPlanning.JsConvertText(id))
            .Where(panes.Contains)
            .ToList();
        if (memberPanes.Count > 0)
        {
            return new Anchor(null, memberPanes[0], tabId);
        }

        if (panes.Count == 1)
        {
            // a lane lead sits in its own stream's tab, so the tab's one pane may be the caller's or run a harness: split from it, never launch into it
            return HostsAgent(c, tabId, panes[0]) ? new Anchor(null, panes[0], tabId) : new Anchor(panes[0], null, tabId);
        }

        // a tab with several panes and no stream member in any of them is split from its first listed pane: launching into one
        // could land in a pane something else is running in
        if (panes.Count > 1)
        {
            return new Anchor(null, panes[0], tabId);
        }

        throw Unavailable($"tab {tabId} has no panes");
    }

    /// <summary>True when <paramref name="pane"/> is the caller's own pane or a harness is running below it; a pane whose processes cannot be read is not.</summary>
    private static bool HostsAgent(RosterContext c, string tab, string pane) =>
        pane == Ah.Core.Mux.HerdrFlavour.SessionPaneId(c.EnvText)
        || (c.Services.Launch?.PaneState is { } state
            && state.TabPanes("herdr", tab)?.FirstOrDefault(p => p.PaneId == pane) is { RootPid: { } pid }
            && state.HarnessRunning(pid) == true);

    /// <summary>
    /// Where a herdr spawn outside any stream puts its pane: split in the tab of a live member of the same role, else in a new
    /// tab labelled <c>&lt;team&gt;: &lt;role&gt;</c>. The caller's own tab is never used.
    /// </summary>
    private static Anchor RoleAnchor(RosterContext c, string dir, JsonValue team, string role, string memberName)
    {
        var launch = c.Services.Launch!;
        RosterFailure Unavailable(string detail)
        {
            var message = $"role tab for {role}: no tab to place the member in ({detail}) — nothing was launched; pass --split to split this pane instead";
            return c.Refuse(
                new JsonObject { ["refused"] = "role-tab-unavailable", ["role"] = role, ["detail"] = detail, ["message"] = message },
                message);
        }

        JsonArray tabs;
        JsonArray panes;
        try
        {
            tabs = TabList(c).Member("result")?.Member("tabs") is { IsArray: true } listedTabs
                ? listedTabs.AsArray()
                : throw new InvalidOperationException("herdr tab list produced unexpected shape (missing .result.tabs)");
            panes = launch.Herdr.Invoke(["pane", "list"]).Member("result")?.Member("panes") is { IsArray: true } listedPanes
                ? listedPanes.AsArray()
                : throw new InvalidOperationException("herdr pane list produced unexpected shape (missing .result.panes)");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw Unavailable(error.Message);
        }

        var callerTab = c.EnvText("HERDR_TAB_ID");
        var liveTabs = tabs.Select(t => MemberPlanning.Text(t.Member("tab_id"))).Where(id => !string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
        var livePanes = panes.Select(p => (Pane: MemberPlanning.Text(p.Member("pane_id")), Tab: MemberPlanning.Text(p.Member("tab_id")))).ToList();
        if (team.Member("members") is { IsArray: true } members)
        {
            foreach (var record in members.AsArray())
            {
                var tab = MemberPlanning.Text(record.Member("tab_id"));
                var pane = MemberPlanning.Text(record.Member("transport_id"));
                if (MemberPlanning.Text(record.Member("role")) == role
                    && MemberPlanning.Text(record.Member("name")) != memberName
                    && !string.IsNullOrEmpty(tab) && !string.IsNullOrEmpty(pane)
                    && tab != callerTab
                    && liveTabs.Contains(tab!)
                    && livePanes.Any(p => p.Pane == pane && p.Tab == tab))
                {
                    return new Anchor(null, pane, tab!);
                }
            }
        }

        var teamName = Path.GetFileNameWithoutExtension(c.Services.Roster.TeamPath(dir, c.TeamFile));
        var rootCwd = c.LaunchCwd is { Length: > 0 } launchCwd ? launchCwd : c.Services.Hierarchy.Messages.RealCwd(c.Cwd);
        var made = StreamSupport.EnsureStreamTab(c, new JsonObject { ["tab_id"] = JsonValue.Null }, $"{teamName}: {role}", rootCwd, tabs);
        if (made.TabId is null)
        {
            throw Unavailable(made.Error ?? "undefined");
        }

        return made.RootPane is { } rootPane ? new Anchor(rootPane, null, made.TabId) : throw Unavailable($"tab {made.TabId} was created without a root pane");
    }

    /// <summary>Where <c>--here</c> or <c>--tab</c> puts the pane: a fresh split there, launched into as is.</summary>
    private static Anchor PlacedAnchor(RosterContext c, PanePlacement.Ask ask, string label)
    {
        var (pane, tab) = PanePlacement.Split(c, ask, label, c.LaunchCwd ?? c.Cwd);
        return new Anchor(pane, null, tab);
    }

    /// <summary>
    /// A relaunch of the same member in the same tree keeps when it was first launched there (<c>first_spawned_at</c>), so the
    /// release check still counts as its dirt a file it wrote before the relaunch.
    /// </summary>
    private static void CarryFirstSpawn(RosterContext c, JsonObject next, JsonValue prior)
    {
        string? Real(string? path) => path is { Length: > 0 } p ? c.Services.Hierarchy.Messages.RealCwd(p) : null;
        _ = next.Remove("first_spawned_at");
        if (prior.IsMap
            && MemberPlanning.Text(prior.Member("name")) == MemberPlanning.Text(next, "name")
            && Real(MemberPlanning.Text(prior.Member("expected_root"))) is { } priorRoot
            && priorRoot == Real(MemberPlanning.Text(next, "expected_root"))
            && (MemberPlanning.Text(prior.Member("first_spawned_at")) ?? MemberPlanning.Text(prior.Member("spawned_at"))) is { Length: > 0 } first)
        {
            next["first_spawned_at"] = first;
        }
    }

    private static bool SameJson(JsonValue a, JsonValue b) => JsonText.Stringify(a, 0) == JsonText.Stringify(b, 0);

    private static void Copy(JsonObject target, string key, JsonObject source, string sourceKey)
    {
        if (source.Get(sourceKey) is { } v)
        {
            target[key] = v;
        }
    }

    /// <summary>
    /// Refuses an untrusted harness cwd before any pane opens, including dry runs, unless a Codex pre-approval covers its trust root.
    /// </summary>
    private static JsonObject RefuseUntrustedHarnessCwd(RosterContext c, JsonObject member, string kind, string launchCwd)
    {
        if (kind == "claude" || kind == c.Services.Launch!.Registry.KindCustom)
        {
            return new JsonObject();
        }

        var check = c.Services.Launch.Trust.Check(launchCwd);
        if (check.Get("verdict") is not { IsText: true } verdict || verdict.AsString() != "untrusted")
        {
            return new JsonObject();
        }

        string? problem = null;
        if (kind == "codex")
        {
            var auto = CodexPreapproval.Of(c);
            var scopes = auto.ReadScopes(out problem);
            var root = c.Services.Hierarchy.Messages.RealCwd(c.Services.Launch.Trust.TrustRoot(launchCwd));
            if (problem is null && auto.Covering(scopes, root) is { } scope)
            {
                return new JsonObject
                {
                    ["trust"] = "preapproved",
                    ["scope"] = new JsonObject { ["path"] = scope.Get("path")!.Value, ["kind"] = scope.Get("kind")!.Value },
                };
            }
        }

        var needs = new JsonArray();
        if (MemberPlanning.Text(check, "trustRoot") is { } trustRoot)
        {
            needs.Add(CodexTrustNeeds.Item(c, trustRoot));
        }

        var resolved = Path.GetFullPath(launchCwd);
        var message = $"Codex has not been told to trust `{resolved}`. That is your decision: run `codex` there once and answer its trust question, then re-run.";
        if (problem is not null)
        {
            message += " " + problem;
        }
        throw c.Refuse(
            new JsonObject
            {
                ["refused"] = "harness-cwd-untrusted",
                ["member"] = member.Get("name") ?? JsonValue.Null,
                ["kind"] = kind,
                ["cwd"] = resolved,
                ["needs"] = needs,
                ["message"] = message,
            },
            message);
    }

    /// <summary>
    /// The refusal for a launch that failed. A name another Herdr agent holds, or a working directory a harness does not trust, is a
    /// choice for the user and nothing was written; anything else folds the evidence into one failure line.
    /// </summary>
    private static RosterFailure LaunchFailure(RosterContext c, string label, JsonObject member, JsonObject launched)
    {
        var name = MemberPlanning.JsConvertText(member.Get("name"));
        var lr = launched.Get("launch_result") is { IsMap: true } r ? r.AsObject() : null;
        var paneFields = new JsonObject();
        if (lr?.Get("closed_pane") is { IsTruthy: true } closed)
        {
            paneFields["closed_pane"] = closed;
        }

        if (lr?.Get("orphaned_transport_id") is { IsTruthy: true } orphaned)
        {
            paneFields["orphaned_transport_id"] = orphaned;
        }

        if (lr is not null && MemberPlanning.Text(lr, "refused") == "name-in-use")
        {
            var rerun = c.RerunCommand((_, _) => false, $"--names-in-use {RosterContext.ArgWord(name)}");
            var detail = lr.Get("detail") ?? JsonValue.Null;
            var fields = new JsonObject
            {
                ["refused"] = "name-in-use",
                ["name"] = name,
                ["rerun"] = rerun,
                ["detail"] = detail,
            };
            RosterJson.Spread(fields, paneFields);
            var message =
                $"Herdr refused to start {name}: another Herdr agent already holds that name ({MemberPlanning.JsConvertText(detail)}). This is not a launch failure, and nothing was written. " +
                $"Re-run with --names-in-use {name}, and the member takes the next free name: {rerun}";
            fields["message"] = message;
            return c.Refuse(fields, message);
        }

        if (lr is not null && MemberPlanning.Text(lr, "refused") == "harness-cwd-untrusted")
        {
            var fields = new JsonObject
            {
                ["refused"] = "harness-cwd-untrusted",
                ["member"] = lr.Get("member") ?? JsonValue.Null,
                ["kind"] = lr.Get("kind") ?? JsonValue.Null,
                ["cwd"] = lr.Get("cwd") ?? JsonValue.Null,
            };
            RosterJson.Spread(fields, paneFields);
            fields["message"] = lr.Get("message") ?? JsonValue.Null;
            return c.Refuse(fields, MemberPlanning.JsConvertText(lr.Get("message")));
        }

        var parts = new List<string> { $"{label}: launching {name} failed" };
        if (launched.Get("error") is { IsTruthy: true } error)
        {
            parts.Add(MemberPlanning.JsConvertText(error));
        }

        if (lr is not null)
        {
            if (lr.Get("likely_cause") is { IsTruthy: true } cause)
            {
                parts.Add(MemberPlanning.JsConvertText(cause));
            }

            if (lr.Get("remedy") is { IsTruthy: true } remedy)
            {
                parts.Add(MemberPlanning.JsConvertText(remedy));
            }

            if (lr.Get("closed_pane") is { IsTruthy: true } closedPane)
            {
                parts.Add($"its pane {MemberPlanning.JsConvertText(closedPane)} was closed");
            }

            if (lr.Get("pane_output") is { IsTruthy: true } output)
            {
                parts.Add($"the pane's last output:\n{MemberPlanning.JsConvertText(output)}");
            }
        }

        return new RosterFailure(string.Join(" — ", parts));
    }

    /// <summary>
    /// A spawn after an alias change can add a member under a different prefix than the team's existing members: no rename, no
    /// disband, just a warning.
    /// </summary>
    private static void WarnMixedPrefix(RosterContext c, string dir, JsonObject member, JsonValue team)
    {
        if (team.Member("members") is not { IsArray: true } list || list.AsArray().Count == 0)
        {
            return;
        }

        string Prefix(JsonValue name, JsonValue role) =>
            SubOrchestratorSpawn.Is(c, MemberPlanning.Text(role)) ? SubOrchestratorSpawn.TeamOf(c, dir)
            : name is { IsText: true } cn && role is { IsText: true } cr && MemberNames.Ordinal(cn.AsString(), c.RepoBasename, cr.AsString()) > 0 ? c.RepoBasename
            : c.Services.Roster.MemberNamePrefixOf(name, role) ?? MemberPlanning.JsConvertText(name);

        var memberName = member.Get("name") ?? JsonValue.Null;
        var newPrefix = Prefix(memberName, member.Get("role") ?? JsonValue.Null);
        foreach (var m in list.AsArray())
        {
            if (m.Member("name") is not { IsTruthy: true } n || m.Member("role") is not { IsTruthy: true } r)
            {
                continue;
            }

            var oldPrefix = Prefix(n, r);
            if (oldPrefix != newPrefix)
            {
                c.Stderr.Write(
                    $"ah roster: ah: this member will be named \"{MemberPlanning.JsConvertText(memberName)}\", but team {MemberPlanning.JsConvertText(team.Member("team_id"))}'s existing members are named \"{oldPrefix}-*\". " +
                    "The team will hold both prefixes. Every name still resolves from team.json, so dispatch is unaffected.\n");
                return;
            }
        }
    }

    // ---------------------------------------------------------------- a custom harness

    /// <summary>
    /// Admit a <c>kind: "custom"</c> member: write its team-file row and start its <c>command</c> in a tmux window when it has one.
    /// Without a <c>command</c> nothing is launched; the user starts the harness with the address.
    /// </summary>
    private static JsonObject SpawnCustomMember(RosterContext c, JsonObject member)
    {
        var services = c.Services;
        var launch = services.Launch!;
        var roster = services.Roster;
        var dir = c.Dir;
        var path = roster.TeamPath(dir, c.TeamFile);
        if (NodePathTeamsDir(path) != "teams")
        {
            throw new RosterFailure(
                $"spawn-ad-hoc: a custom harness is addressed as name@team, and {path} is not a named team file — create or name the team first");
        }

        var memberName = MemberPlanning.JsConvertText(member.Get("name"));
        var address = $"{memberName}@{Path.GetFileNameWithoutExtension(path)}";
        var team = roster.ReadTeamRecord(dir, c.TeamFile);
        var transport = launch.Transports.Detect();
        JsonObject? spawn = null;
        if (member.Get("command") is { IsTruthy: true })
        {
            if (!c.Opts.IsTrue("dry-run"))
            {
                try
                {
                    StatuslineTap.EnsureScript(c);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the guard runs the member's own statusline when the script is missing, so the spawn goes on
                }
            }

            var shape = launch.Shapes.SpawnShape(member, transport, c.LaunchContext());
            if (shape.Fail is not null)
            {
                throw new RosterFailure(shape.Fail);
            }

            spawn = shape.Shape;
            if (spawn.Get("refuse") is { IsTruthy: true } refuse)
            {
                throw new RosterFailure($"spawn-ad-hoc: {MemberPlanning.JsConvertText(refuse)}");
            }
        }

        if (c.Opts.IsTrue("dry-run"))
        {
            var launchLine = spawn?.Get("launch") is { } l ? l : new JsonArray();
            return new JsonObject
            {
                ["dry_run"] = true,
                ["role"] = member.Get("role") ?? JsonValue.Null,
                ["name"] = member.Get("name") ?? JsonValue.Null,
                ["address"] = address,
                ["launch"] = launchLine,
            };
        }

        double? newTeamPid = team.IsNull ? TeamRecords.NewTeamOwnerPid(c, "spawn-ad-hoc") : null;
        var found = services.Config.ResolveRoster(c.Cwd, roster.TeamRosterKey(dir, c.TeamFile), c.RepoBasename, c.Registry);
        var resolved = TeamScopes.AdHocRoster(found);
        var stored = MemberPlanning.Text(team.Member("layout"));
        var mode = !team.IsNull && stored is "auto" or "columns" or "grid" ? stored! : MemberPlanning.Text(CreatePlanning.CreateLayout(c), "mode") ?? "auto";
        var row = new JsonObject
        {
            ["role"] = member.Get("role") ?? JsonValue.Null,
            ["name"] = member.Get("name") ?? JsonValue.Null,
            ["route"] = launch.Registry.RouteMail,
            ["kind"] = launch.Registry.KindCustom,
        };
        Copy(row, "harness", member, "harness");
        if (member.Get("command") is { IsTruthy: true } command)
        {
            row["command"] = command;
        }

        row["expected_root"] = services.Hierarchy.Messages.RealCwd(c.Cwd);
        var spawnedAt = Ah.Core.Time.Timestamps.ToIsoString(services.Clock.UtcNow);
        if (spawn is not null)
        {
            row["spawned_at"] = spawnedAt;
        }

        JsonObject? outTeam = null;
        var teamWasNew = false;
        services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                var members = doc.Member("members") is { IsArray: true } dm ? dm.AsArray().ToList() : [];
                if (members.Any(m => m.IsMap && MemberPlanning.Text(m.Member("name")) == memberName))
                {
                    throw new RosterFailure($"spawn-ad-hoc: team {path} already has a member named {memberName} — refusing to overwrite its record");
                }

                JsonObject next;
                var list = new JsonArray();
                if (doc.IsMap)
                {
                    next = doc.AsObject().Clone();
                    list.AddRange(members);
                }
                else
                {
                    next = TeamRecords.NewTeamRecord(c, resolved, transport, newTeamPid ?? double.NaN, mode);
                    teamWasNew = true;
                }

                list.Add(row);
                next["members"] = list;
                OrchestratorSession.Backfill(c, next);
                outTeam = next;
                return TeamFileOutcome.Write(next);
            });

        JsonObject? adHocLaunched = null;
        if (spawn is not null)
        {
            JsonObject launched;
            try
            {
                var withSpawn = row.Clone();
                withSpawn["spawn"] = spawn;
                var batch = launch.Launcher.LayoutAndLaunch(dir, [withSpawn], transport, mode, c.Cwd, "spawn-ad-hoc", c.LaunchContext(), new LayoutOptions(DeferRegistration: true));
                if (batch.Fail is not null)
                {
                    throw new RosterFailure(batch.Fail);
                }

                if (batch.Partial is not null)
                {
                    c.Out(batch.Partial);
                    throw new RosterFailure("spawn-ad-hoc: layout stopped with a partial result");
                }

                launched = batch.Members[0];
                if (MemberPlanning.Text(launched, "launch_status") == "failed")
                {
                    var error = launched.Get("error") is { IsTruthy: true } e ? $" — {MemberPlanning.JsConvertText(e)}" : string.Empty;
                    throw new RosterFailure($"spawn-ad-hoc: launching {memberName} failed{error}");
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                try
                {
                    services.Hierarchy.StateCalls.UpdateTeam(
                        dir,
                        c.TeamFile,
                        doc =>
                        {
                            var rows = doc.Member("members") is { IsArray: true } rs ? rs.AsArray().ToList() : [];
                            var kept = rows.Where(m => !(m.IsMap && MemberPlanning.Text(m.Member("name")) == memberName && MemberPlanning.Text(m.Member("spawned_at")) == spawnedAt)).ToList();
                            if (!doc.IsMap || kept.Count == rows.Count)
                            {
                                return null;
                            }

                            if (teamWasNew && kept.Count == 0)
                            {
                                return TeamFileOutcome.Clear();
                            }

                            var restored = doc.AsObject().Clone();
                            var list = new JsonArray();
                            list.AddRange(kept);
                            restored["members"] = list;
                            return TeamFileOutcome.Write(restored);
                        });
                }
                catch (Exception undo) when (undo is not OutOfMemoryException)
                {
                    c.Stderr.Write($"ah roster: warning — the row written for {memberName} before its launch could not be taken back: {undo.Message}\n");
                }

                throw;
            }

            adHocLaunched = launched;
            var transportId = launched.Get("transport_id") ?? JsonValue.Null;
            services.Hierarchy.StateCalls.UpdateTeam(
                dir,
                c.TeamFile,
                doc =>
                {
                    var rows = doc.Member("members") is { IsArray: true } rs ? rs.AsArray().ToList() : [];
                    var at = rows.FindIndex(m => m.IsMap && MemberPlanning.Text(m.Member("name")) == memberName && MemberPlanning.Text(m.Member("spawned_at")) == spawnedAt);
                    if (!doc.IsMap && MemberPlanning.Text(c.Services.Roster.TeamFileState(dir, c.TeamFile), "state") == "unusable")
                    {
                        throw new RosterFailure($"the team file {c.Services.Roster.TeamPath(dir, c.TeamFile)} is no longer readable, so the pane {MemberPlanning.JsConvertText(transportId)} of {memberName} was not recorded and the file was left alone");
                    }

                    if (!doc.IsMap || at < 0)
                    {
                        throw new RosterFailure($"spawn-row-replaced: {memberName} — the row written before the launch is gone or was replaced, so its pane {MemberPlanning.JsConvertText(transportId)} was not recorded");
                    }

                    var merged = rows[at].AsObject().Clone();
                    merged["transport_id"] = transportId;
                    Copy(merged, Ah.Core.Mux.HerdrFlavour.Field, launched, Ah.Core.Mux.HerdrFlavour.Field);
                    var list = new JsonArray();
                    for (var i = 0; i < rows.Count; i++)
                    {
                        list.Add(i == at ? merged : rows[i]);
                    }

                    var next = doc.AsObject().Clone();
                    next["members"] = list;
                    TeamRecords.CompleteAfterLaunch(next, launched, transport);
                    outTeam = next;
                    row = merged;
                    return TeamFileOutcome.Write(next);
                });
        }

        var result = new JsonObject
        {
            ["spawned"] = spawn is not null,
            ["admitted"] = true,
            ["address"] = address,
            ["member"] = row,
        };
        if (adHocLaunched is not null)
        {
            CopyUnconfirmed(adHocLaunched, result);
        }

        TeamRegistration.Report(result, TeamRegistration.RegisterTeam(c, dir, outTeam!), c);
        OrchestratorSession.Warn(c, result);
        Copy(result, "team_id", outTeam!, "team_id");
        return result;
    }

    /// <summary>A launch the member never registered after is reported as <c>launch_status: unconfirmed</c> with its reason; the outcome is unchanged.</summary>
    private static void CopyUnconfirmed(JsonObject launched, JsonObject output)
    {
        if (MemberPlanning.Text(launched, "launch_status") == "unconfirmed")
        {
            output["launch_status"] = "unconfirmed";
            output["unconfirmed_reason"] = launched.Get("unconfirmed_reason") ?? JsonValue.Null;
        }
    }

    private static string NodePathTeamsDir(string path) => Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
}

/// <summary>The member fields <c>--role/--model/--effort/--route/--kind/--args/--auto-mode/--on-missing</c> decode to.</summary>
internal static class MemberFlags
{
    /// <summary>
    /// The member those flags describe. Validation is the caller's, against its own effective route: the roster block's for
    /// <c>add</c>, the member's own for an ad hoc spawn.
    /// </summary>
    internal static JsonObject FromFlags(RosterContext c, string role, string label)
    {
        var launch = c.Services.Launch!;
        var opts = c.Opts;
        if (opts.IsTrue("kind"))
        {
            throw new RosterFailure($"{label}: --kind requires a value (e.g. claude, codex, pi)");
        }

        var kind = opts.Text("kind") ?? launch.Registry.KindDefault;
        var member = new JsonObject { ["role"] = role };
        if (opts.Text("model") is { } model)
        {
            member["model"] = model;
        }

        if (kind != launch.Registry.KindDefault)
        {
            member["kind"] = kind;
        }

        if (opts.Text("effort") is { } effort)
        {
            member["effort"] = effort;
        }

        if (opts.Text("route") is { } route)
        {
            member["route"] = route;
        }

        if (opts.Text("auto-mode") is { } autoMode)
        {
            member["autoMode"] = autoMode;
        }

        if (opts.Has("harness") || opts.Has("command"))
        {
            if (kind != launch.Registry.KindCustom)
            {
                throw new RosterFailure($"{label}: --harness and --command apply only to --kind {launch.Registry.KindCustom}");
            }

            foreach (var flag in new[] { "harness", "command" })
            {
                if (opts.Has(flag) && opts.Text(flag) is null)
                {
                    throw new RosterFailure($"{label}: --{flag} requires a value");
                }
            }

            if (opts.Text("harness") is { } harness)
            {
                member["harness"] = harness;
            }

            if (opts.Text("command") is { } command)
            {
                member["command"] = command;
            }
        }

        if (opts.IsTrue("adapter"))
        {
            throw new RosterFailure($"{label}: --adapter requires a value (appserver or off)");
        }

        if (opts.Text("adapter") is { Length: > 0 } adapter)
        {
            member["adapter"] = adapter;
        }

        if (opts.Has("args") && ParseArgsFlag(c, label) is { } parsed)
        {
            member["args"] = parsed;
        }

        if (opts.IsTrue("on-missing"))
        {
            throw new RosterFailure($"{label}: --on-missing requires a value (auto)");
        }

        if (opts.Text("on-missing") is { } onMissing)
        {
            member["onMissing"] = onMissing;
        }

        return member;
    }

    /// <summary>
    /// <c>--args</c>: a JSON array of native CLI arguments. Only the JSON shape is decoded here; whether it is a legal <c>args</c> is
    /// the kind field rule's single answer. An empty array clears the field.
    /// </summary>
    internal static JsonValue? ParseArgsFlag(RosterContext c, string cmd)
    {
        if (c.Opts.Text("args") is not { } value)
        {
            throw new RosterFailure(
                $"{cmd}: --args requires a value — a JSON array of strings, e.g. --args '[\"--profile\",\"fast\"]'");
        }

        var raw = value.Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        if (JsJson.Describe(raw) is not null)
        {
            throw new RosterFailure(
                $"{cmd}: --args must be a JSON array of strings, e.g. --args '[\"--profile\",\"fast\"]' (could not parse {JsonText.Quote(raw)})");
        }

        var parsed = JsonText.Parse(raw);
        return parsed.IsArray && parsed.AsArray().Count == 0 ? null : (JsonValue?)parsed;
    }

    /// <summary>
    /// Any member launched through Herdr must have a name Herdr will accept. A non-claude kind has no other transport, so an
    /// unusable name is refused; a claude member launches fine on tmux or the terminal, so it is warned about.
    /// </summary>
    internal static void RequireHerdrName(RosterContext c, JsonObject member, string cmd)
    {
        if (!MemberPlanning.HasPane(c, member.Get("route")))
        {
            return;
        }

        var check = c.Services.Config.ValidateHerdrName(member.Get("name") ?? JsonValue.Null);
        if (check.Get("ok") is { IsTruthy: true })
        {
            return;
        }

        var why = MemberPlanning.JsConvertText(check.Get("why"));
        if (MemberPlanning.KindOf(c, member) != c.Services.Launch!.Registry.KindDefault)
        {
            throw new RosterFailure($"{cmd}: {why}");
        }

        c.Stderr.Write(
            $"ah roster: warning — {why}. This member spawns fine on the tmux/terminal transport, but `herdr agent start` will reject the name.\n");
    }
}
