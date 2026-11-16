using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;

namespace Ah.Commands;

/// <summary>
/// Where a team-creating verb writes and whether it may: the team file's scope, the name a new team would get, and the guards
/// that keep a live team of another session from being replaced.
/// </summary>
internal static class TeamScopes
{
    /// <summary>Why a name cannot be a team's, and the longest member name that fails under herdr (null when none does).</summary>
    internal sealed record NameProblem(string Why, JsonObject? FailingMember);

    /// <summary>The block a new team would be built from: its route and its members.</summary>
    internal sealed record TeamBlock(string? Route, IReadOnlyList<JsonObject> Members);

    private static string Transport(RosterContext c) => c.Services.Launch!.Transports.Detect();

    /// <summary>A member-name suffix a Herdr agent name can carry: a dash, then one or more of <c>[a-z0-9_-]</c>.</summary>
    internal static bool ValidHerdrSuffix(string suffix)
    {
        if (suffix.Length < 2 || suffix[0] != '-')
        {
            return false;
        }

        foreach (var c in suffix.AsSpan(1))
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------- the block a team would be built from

    /// <summary>
    /// The block a new team would be built from. Under herdr every pane-routed member it derives must carry a herdr-valid name:
    /// the whole block, because every later spawn into the team derives its name from the same frozen team name.
    /// </summary>
    internal static TeamBlock NewTeamBlock(RosterContext c, string dir)
    {
        if (c.Command == "create" && c.Opts.Text("from") is not null)
        {
            var members = CreatePlanning.ValidateHistoryMembers(c, CreatePlanning.ResolveHistoryEntry(c, dir));
            var route = members.Count > 0 && MemberPlanning.Text(members[0], "route") is { Length: > 0 } r ? r : "peer";
            return new TeamBlock(route, members);
        }

        var found = c.Services.Config.ResolveRoster(c.Cwd, c.Command == "create" ? c.RosterArg : null, null, c.Registry);
        var resolved = c.Command is "spawn-ad-hoc" or "stream-open" ? AdHocRoster(found) : found;
        var list = new List<JsonObject>();
        string? blockRoute = null;
        if (resolved.IsMap)
        {
            blockRoute = MemberPlanning.Text(resolved.Member("route"));
            if (resolved.Member("members") is { IsArray: true } ms)
            {
                list.AddRange(ms.AsArray().Select(MemberPlanning.Obj));
            }
        }

        if (c.Command == "spawn-ad-hoc" && c.AdHocForNameCheck is { } adHoc)
        {
            list.Add(adHoc);
        }

        return new TeamBlock(blockRoute, list);
    }

    /// <summary>The roster an ad hoc spawn may read: it takes a roster's route, so a global roster is not read here. It also takes model, effort and autoMode from the first row of its role in the roster as resolved at any level (<c>SpawnOne.FillFromRosterRow</c>), and nothing else from that row; it takes none of the three from a row of another kind.</summary>
    internal static JsonValue AdHocRoster(JsonValue found) =>
        found.IsMap && found.Member("level") is { IsText: true } l && l.AsString() == "global" ? JsonValue.Null : found;

    /// <summary>The final names of every pane-routed member <paramref name="block"/> gives team <paramref name="name"/>.</summary>
    internal static List<string> PaneNames(RosterContext c, TeamBlock block, string name)
    {
        var stripped = new JsonArray();
        foreach (var m in block.Members)
        {
            var copy = m.Clone();
            copy.Remove("name");
            stripped.Add(copy);
        }

        var named = c.Services.Config.RosterMemberNames(stripped, name);
        var renamed = MemberPlanning.RenameInUse(c, named.Select(MemberPlanning.Obj).ToList(), name);
        return renamed
            .Where(m => MemberPlanning.HasPane(c, m.Get("route") is { IsTruthy: true } r ? r : (block.Route is null ? JsonValue.Null : block.Route)))
            .Select(m => MemberPlanning.Text(m, "name") ?? string.Empty)
            .ToList();
    }

    // ---------------------------------------------------------------- names

    /// <summary>
    /// Whether <paramref name="name"/> can be a team's name: a valid team name on every transport, and under herdr also a prefix
    /// every pane-routed member name fits herdr's rule with. Null, or the problem.
    /// </summary>
    internal static NameProblem? TeamNameProblem(RosterContext c, string name, string transport, TeamBlock block)
    {
        var config = c.Services.Config;
        List<string> failing = [];
        if (transport == "herdr")
        {
            failing = PaneNames(c, block, name)
                .Where(n => config.ValidateHerdrName(JsonValue.FromString(n)).Get("ok") is not { IsTruthy: true })
                .OrderByDescending(n => n.Length)
                .ToList();
        }

        JsonObject? failingMember = failing.Count > 0
            ? new JsonObject { ["name"] = failing[0], ["length"] = failing[0].Length }
            : null;
        var v = config.ValidateTeamAlias(JsonValue.FromString(name), c.Registry);
        if (v.Get("ok") is not { IsTruthy: true })
        {
            return new NameProblem(MemberPlanning.JsConvertText(v.Get("why")), failingMember);
        }

        if (failingMember is not null)
        {
            var why = config.ValidateHerdrName(JsonValue.FromString(failing[0]));
            return new NameProblem(MemberPlanning.JsConvertText(why.Get("why")), failingMember);
        }

        return null;
    }

    /// <summary>Where the name a create would use came from: the user's <c>--team</c> or the repo basename.</summary>
    internal static string TeamNameSource(RosterContext c)
    {
        return c.Opts.Text("team") is not null || (c.Command == "create" && c.Opts.Text("from") is not null && c.TeamArg is not null) ? "explicit" : "basename";
    }

    /// <summary>The name check a verb about to create <c>TeamFile</c> runs before anything is launched or written.</summary>
    internal static void CheckNewTeamName(RosterContext c, string dir)
    {
        if (c.TeamFile is null)
        {
            return;
        }

        var transport = Transport(c);
        var block = NewTeamBlock(c, dir);
        if (TeamNameProblem(c, c.TeamFile, transport, block) is { } problem)
        {
            throw RefuseTeamName(c, c.TeamFile, TeamNameSource(c), problem, transport, block);
        }
    }

    /// <summary>
    /// The one refusal for a team name that cannot be used, whatever proposed it. Choosing a team's name is the user's call, so the
    /// sanitizer's suggestion is only offered: JSON on stdout for a caller that branches on <c>refused</c>, the instruction on stderr
    /// for one that reads text, exit 2. Nothing is launched or written.
    /// </summary>
    internal static RosterFailure RefuseTeamName(RosterContext c, string name, string nameSource, NameProblem problem, string transport, TeamBlock block)
    {
        var config = c.Services.Config;
        var suffixes = transport == "herdr"
            ? PaneNames(c, block, name).Select(n => n[name.Length..]).ToList()
            : [];
        JsonValue suggestionValue;
        if (transport == "herdr")
        {
            var context = new JsonObject { ["transport"] = transport };
            var list = new JsonArray();
            list.AddRange(suffixes.Select(s => (JsonValue)s));
            context["suffixes"] = list;
            context["resolved"] = c.Registry;
            suggestionValue = config.SuggestTeamAlias(JsonValue.FromString(name), context);
        }
        else
        {
            suggestionValue = config.SuggestTeamAlias(JsonValue.FromString(name), JsonValue.Null);
        }

        var suggestion = suggestionValue.IsText ? suggestionValue.AsString() : null;
        string? suggestionWhy = null;
        if (suggestion is null)
        {
            var bad = suffixes.FirstOrDefault(s => !ValidHerdrSuffix(s));
            var longest = suffixes.OrderByDescending(s => s.Length).FirstOrDefault() ?? string.Empty;
            suggestionWhy = bad is not null
                ? $"the member name suffix \"{bad}\" is not valid in a Herdr agent name ([a-z0-9_-] only), whatever the team is called — rename that role or its --member name"
                : $"the longest member name suffix, \"{longest}\" ({longest.Length} characters), leaves no room for a team name within Herdr's 32-character limit — shorten that role or its --member name";
        }

        var needsUserChoice = suggestion is not null;
        var rerun = needsUserChoice ? c.RerunCommand((flag, _) => flag == "team", "--team <TEAM>") : null;
        var message = needsUserChoice
            ? $"Team name \"{name}\" ({nameSource}) can't be used: {problem.Why}. Do not pick a name yourself. Ask the user with AskUserQuestion — first option \"Use {suggestion} (Recommended)\", and let them type another name. Then re-run {rerun} with <TEAM> replaced by their answer. This is not a launch failure."
            : $"No team name can make member names valid under {transport}: {suggestionWhy}. Tell the user; the fix is a shorter or renamed role or --member name. Do not retry with a name of your own.";
        var fields = new JsonObject
        {
            ["refused"] = "team-name-unusable",
            ["needs_user_choice"] = needsUserChoice,
            ["verb"] = c.Command,
            ["name"] = name,
            ["name_source"] = nameSource,
            ["transport"] = transport,
            ["why"] = problem.Why,
            ["failing_member"] = problem.FailingMember is { } fm ? fm : JsonValue.Null,
            ["suggestion"] = RosterJson.Text(suggestion),
            ["suggestion_why"] = RosterJson.Text(suggestionWhy),
            ["rerun"] = RosterJson.Text(rerun),
            ["message"] = message,
        };
        return c.Refuse(fields, message);
    }

    // ---------------------------------------------------------------- collisions

    /// <summary>
    /// <c>--team &lt;T&gt;</c> cannot equal the effective unscoped prefix while a default team with a RUNNING ORCHESTRATOR exists: two
    /// teams would derive the same peer names. Owner-alive is the whole test.
    /// </summary>
    internal static void GuardTeamPrefixCollision(RosterContext c, string dir, string? team)
    {
        if (team is null || team.Length == 0)
        {
            return;
        }

        var unscoped = c.Services.Config.TeamPrefix(c.Cwd, null);
        if (team != unscoped)
        {
            return;
        }

        var existing = c.Services.Roster.ReadTeamRecord(dir, null);
        if (!existing.IsNull && c.Services.Hierarchy.Core.PidAlive(RosterJson.Member(existing.Member("orchestrator"), "pid") ?? JsonValue.Null))
        {
            var id = MemberPlanning.JsConvertText(existing.Member("team_id"));
            var holder = $"a live default team \"{id}\" (orchestrator pid {MemberPlanning.JsConvertText(RosterJson.Member(existing.Member("orchestrator"), "pid"))})";
            var ways = $"Re-run with --team {DeriveTeamCandidate(c, dir, unscoped)} to start a separate team, or --team <your-name>; or disband \"{id}\" first.";
            throw new RosterFailure(c.Opts.Text("team") is null
                ? $"{holder} already holds the name \"{unscoped}\" here. {ways}"
                : $"--team \"{unscoped}\" names the default team, and {holder} already holds it. {ways}");
        }
    }

    /// <summary>
    /// The effective prefix, then -2, -3, ... until a name has no <c>teams/&lt;name&gt;.json</c> yet and is itself a valid alias. The
    /// bare prefix is never offered: the caller only reaches this because a live default team already holds it.
    /// </summary>
    internal static string DeriveTeamCandidate(RosterContext c, string dir, string basePrefix)
    {
        var config = c.Services.Config;
        var baseCheck = config.ValidateTeamAlias(JsonValue.FromString(basePrefix));
        if (baseCheck.Get("ok") is not { IsTruthy: true })
        {
            var transport = Transport(c);
            var block = NewTeamBlock(c, dir);
            var problem = TeamNameProblem(c, basePrefix, transport, block)
                ?? new NameProblem(MemberPlanning.JsConvertText(baseCheck.Get("why")), null);
            throw RefuseTeamName(c, basePrefix, TeamNameSource(c), problem, transport, block);
        }

        for (var n = 2; n <= 1000; n++)
        {
            var candidate = $"{basePrefix}-{n}";
            if (config.ValidateTeamAlias(JsonValue.FromString(candidate)).Get("ok") is { IsTruthy: true }
                && c.Services.Roster.ReadTeamRecord(dir, candidate).IsNull)
            {
                return candidate;
            }
        }

        throw new RosterFailure($"could not derive a free --team candidate from \"{basePrefix}\" after 1000 attempts");
    }

    /// <summary>
    /// A bare <c>create</c> colliding with someone else's live default team refuses with a candidate name instead of guessing: a
    /// create cannot read stdin, so the answer is for the user to give.
    /// </summary>
    internal static RosterFailure RefuseLiveDefaultTeam(RosterContext c, string dir, JsonValue existing)
    {
        var basePrefix = c.Services.Config.TeamPrefix(c.Cwd, null);
        var candidate = DeriveTeamCandidate(c, dir, basePrefix);
        var hasMembers = existing.Member("members") is { IsArray: true } list && list.AsArray().Count > 0;
        var pid = MemberPlanning.JsConvertText(RosterJson.Member(existing.Member("orchestrator"), "pid"));
        return new RosterFailure(
            $"a live team \"{MemberPlanning.JsConvertText(existing.Member("team_id"))}\" (orchestrator pid {pid}) already holds the name \"{basePrefix}\" here, " +
            (hasMembers
                ? "and its members are dispatched under that prefix. "
                : "but it has no members: `disband` ends it. ") +
            $"Re-run with --team {candidate} to accept the auto-derived name, or --team <your-name> to choose your own.");
    }

    // ---------------------------------------------------------------- live teams at the scope

    /// <summary>
    /// The team already at the RESOLVED scope is consulted before <c>create</c> writes. Ownership, not liveness, is the line: refusing
    /// every live team would break committing twice, and allowing every re-commit destroys a team another session is running.
    /// </summary>
    internal static void GuardLiveTeamAtScope(RosterContext c, string dir, bool committing) =>
        GuardLiveTeam(c, c.Services.Roster.ReadTeamRecord(dir, c.TeamFile), committing);

    /// <summary>The judgement of <see cref="GuardLiveTeamAtScope"/> on a team document.</summary>
    internal static void GuardLiveTeam(RosterContext c, JsonValue existing, bool committing)
    {
        var roster = c.Services.Roster;
        var invoker = c.Invoker();
        if (existing.IsNull || !roster.TeamIsLive(existing, invoker, c.Services.Clock.UtcNow))
        {
            return;
        }

        if (!committing)
        {
            return;
        }

        var orchestrator = existing.Member("orchestrator");
        var ownerPid = RosterJson.Member(orchestrator, "pid");
        var myPid = c.OwnerPid();
        if (roster.IsTeamOwnedBy(existing, invoker))
        {
            return;
        }

        var teamId = MemberPlanning.JsConvertText(existing.Member("team_id"));
        if (RosterContext.IsInteger(myPid) && ownerPid is { IsNumber: true } op && op.AsNumber() == myPid)
        {
            var recorded = MemberPlanning.JsConvertText(RosterJson.Member(orchestrator, "session_id"));
            var mine = invoker.SessionId is null ? "null" : invoker.SessionId;
            throw new RosterFailure(
                $"create --commit: team {teamId} at this scope records session {recorded}, not this one ({mine}), " +
                $"so its pid {MemberPlanning.JsConvertText(ownerPid)} is not evidence this session owns it — disband it first, or commit under a different --team");
        }

        throw new RosterFailure(
            $"create --commit: team {teamId} at this scope is owned by another live orchestrator (pid {MemberPlanning.JsConvertText(ownerPid)}) — " +
            "disband it first, or have that session commit its own team");
    }

    /// <summary>Shared by the plan and spawn paths: refuse a live team, clear a stale one.</summary>
    internal static void RefuseOrClearExistingTeam(RosterContext c, string dir)
    {
        ResolveWritableTeamScope(c, dir);
        if (c.Services.Roster.ReadTeamRecord(dir, c.TeamFile).IsNull)
        {
            return;
        }

        var invoker = c.Invoker();
        c.Services.Hierarchy.StateCalls.UpdateTeam(
            dir,
            c.TeamFile,
            doc =>
            {
                if (doc.IsNull)
                {
                    return null;
                }

                // An Orchestrator's own implicit team (ah2-34) is no team to disband first: the create writes over it.
                var ownImplicit = doc.Member("implicit") is { IsBool: true } flag && flag.AsBool()
                    && doc.Member("members") is { IsArray: true } none && none.AsArray().Count == 0
                    && c.Services.Roster.IsTeamOwnedBy(doc, invoker);
                if (ownImplicit)
                {
                    return null;
                }

                if (c.Services.Roster.TeamIsLive(doc, invoker, c.Services.Clock.UtcNow))
                {
                    throw RefuseLiveTeam(c, dir, doc);
                }

                return TeamFileOutcome.Clear();
            });
    }

    /// <summary>The refusal of a create-like verb that finds a live team where it would write one.</summary>
    internal static RosterFailure RefuseLiveTeam(RosterContext c, string dir, JsonValue existing)
    {
        if (c.TeamFileDefaulted)
        {
            return RefuseLiveDefaultTeam(c, dir, existing);
        }

        var noMembers = existing.Member("members") is { IsArray: true } list && list.AsArray().Count == 0;
        return new RosterFailure(
            $"a live Team {MemberPlanning.JsConvertText(existing.Member("team_id"))} already exists{(noMembers ? " but has no members" : string.Empty)} — disband it first");
    }

    // ---------------------------------------------------------------- the file a create writes

    /// <summary>Settles which team file a create or spawn verb writes to, then refuses when that file exists but cannot be read.</summary>
    internal static void ResolveWritableTeamScope(RosterContext c, string dir)
    {
        SettleWritableTeamScope(c, dir);
        var target = c.Services.Roster.TeamFileState(dir, c.TeamFile);
        if (target.Get("state") is { IsText: true } state && state.AsString() == "unusable")
        {
            var escape = c.TeamFileDefaulted ? "pass an explicit --team <name>" : "name a different --team";
            var path = MemberPlanning.JsConvertText(target.Get("path"));
            throw new RosterFailure(
                $"the team file {path} exists but cannot be read, so it will not be written over or beside — nothing was written. Repair or remove that file, or {escape}");
        }
    }

    /// <summary>What the invariant demands at the moment a team would be CREATED: a prefix that cannot name a file refuses.</summary>
    internal static void SettleWritableTeamScope(RosterContext c, string dir)
    {
        if (c.TeamFile is not null || !c.TeamFileDefaulted || c.TeamFileUnreadable.IsTruthy)
        {
            return;
        }

        if (c.TeamFileUnnamable.IsText)
        {
            var name = c.TeamFileUnnamable.AsString();
            var transport = Transport(c);
            var block = NewTeamBlock(c, dir);
            var problem = TeamNameProblem(c, name, transport, block)
                ?? new NameProblem(MemberPlanning.JsConvertText(c.Services.Config.ValidateTeamAlias(JsonValue.FromString(name)).Get("why")), null);
            throw RefuseTeamName(c, name, TeamNameSource(c), problem, transport, block);
        }

        c.TeamFile = c.Services.Config.TeamPrefix(c.Cwd, null);
    }
}
