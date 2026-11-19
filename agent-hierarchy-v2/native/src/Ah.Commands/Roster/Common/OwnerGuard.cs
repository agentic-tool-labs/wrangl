using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>A refusal of an owner verb: what was refused, the proof code, and the two lines printed. The verb has changed nothing.</summary>
internal sealed class OwnerRefusal(string refused, string proof, string first, string why, string? proofFailed = null) : Exception(first)
{
    /// <summary>Which step of the parent-owner proof failed, when the refusal is about a lane child; else null.</summary>
    internal string? ProofFailed { get; } = proofFailed;

    internal string Refused { get; } = refused;

    internal string Proof { get; } = proof;

    internal string First { get; } = first;

    internal string Why { get; } = why;
}

/// <summary>
/// The one check every owner verb routes through before it does anything. The member marker is read first; then the process
/// ancestry must prove an Orchestrator or a Claude session above the command; then <c>--orchestrator-pid</c> and
/// <c>CLAUDE_PID</c>, if given, must name that process; then, for a verb that acts on a team that already exists, the proved caller
/// must own it. <c>disband</c> of an orphaned team needs no proof (there is no live owner to protect). <c>adopt</c> takes a team its
/// holder no longer runs and refuses a live one itself; <c>resume</c> makes the same checks itself.
/// </summary>
internal static class OwnerGuard
{
    private static readonly HashSet<string> Guarded = new(StringComparer.Ordinal)
    {
        "create", "spawn-one", "spawn-ad-hoc", "dismiss", "disband", "untrack", "reap", "adopt", "move", "transfer", "stream-open", "stream-done", "progress", "close-approve", "checkpoint", "lane-caps", "quit", "pause", "stop", "duty", "pause-member", "resume-member", "rename",
    };

    private static readonly HashSet<string> ActOnExistingTeam = new(StringComparer.Ordinal)
    {
        "spawn-one", "spawn-ad-hoc", "dismiss", "disband", "untrack", "move", "transfer", "stream-open", "stream-done", "progress", "close-approve", "checkpoint", "lane-caps", "quit", "pause", "stop", "duty", "pause-member", "resume-member", "rename",
    };

    private static readonly HashSet<string> CreatesTeam = new(StringComparer.Ordinal)
    {
        "spawn-one", "spawn-ad-hoc", "stream-open",
    };

    /// <summary>Whether <paramref name="verb"/> is an owner verb this guard checks.</summary>
    internal static bool IsGuarded(string verb) => Guarded.Contains(verb);

    internal static void Check(RosterContext c)
    {
        var verb = c.Command;
        if (!Guarded.Contains(verb) || (verb is "quit" or "pause" && c.Opts.IsTrue("status")) || ProgressDocsVerb.IsRead(c))
        {
            return;
        }

        if (c.UserActs)
        {
            return; // the user's own quit or stop: never refused
        }

        var proved = c.Proved();
        if (proved.Marked)
        {
            if (proved.Carve is not null)
            {
                SubOrchestratorCarve.CheckScope(c, proved);
                if (c.StatedPidMismatch() is { } stated)
                {
                    throw new OwnerRefusal(
                        "orchestrator-pid-mismatch",
                        "session",
                        $"ah roster {verb}: {stated.Name} names pid {stated.Stated}, but this command runs under pid {proved.Pid}; nothing was changed.",
                        $"owner proof: {stated.Name} is {stated.Stated}; the session above this command is pid {proved.Pid}.");
                }

                return;
            }

            if (proved.Unproven)
            {
                const string NotProven = "could not prove which Claude session runs this command; nothing was changed. Run it from the Orchestrator's Claude session.";
                throw proved.Nearest.Kind == NearestKind.Unavailable
                    ? new OwnerRefusal("owner-unproven", "unproven", $"ah roster {verb}: {NotProven}", "owner proof: the process table could not be read.")
                    : new OwnerRefusal("owner-unproven", "no-orchestrator", $"ah roster {verb}: {NotProven}", "owner proof: no registered session is an ancestor of this command.");
            }

            throw SubOrchestratorCarve.MarkedRefusal(verb);
        }

        SubOrchestratorClose.RefuseForcing(c, verb);
        var team = ExistingTeam(c);
        var laneTeam = c.TeamFile ?? proved.Nearest.Team;
        if (verb == "disband" && team is { } t && c.Services.Roster.TeamIsOrphaned(t)
            && !(laneTeam is not null && ReadTeamNamed(c, laneTeam) is { } laneRecord && ValidLaneChild(c, laneTeam, laneRecord) is not null))
        {
            return;
        }

        // ah2-204: a Claude session above the command with no session-pids row gets the row SessionStart would write, then is proved again
        bool? harnessAbove = null;
        if (proved.Nearest.Kind == NearestKind.None && !c.DaemonPrincipal && c.Services.OwnerProof is { } ownerProof)
        {
            // A3: a CLAUDE_PID that parses is used only on this command's ancestor line; one off the line refuses on its own
            var stated = StatedHarnessPid(c);
            if (stated is { } statedPid && ownerProof.IsAncestor(statedPid) == false)
            {
                throw new OwnerRefusal(
                    "owner-unproven",
                    "no-orchestrator",
                    $"ah roster {verb}: CLAUDE_PID names pid {statedPid}, which is not a process above this command; nothing was changed. Unset CLAUDE_PID, or run the verb inside that Claude session.",
                    $"owner proof: CLAUDE_PID {statedPid} is not an ancestor of this command.");
            }

            var harness = ownerProof.HarnessAncestor(stated);
            harnessAbove = harness is not null;
            if (harness is { } harnessPid && RegisterLate(c, harnessPid))
            {
                proved = c.Proved();
                laneTeam = c.TeamFile ?? proved.Nearest.Team;
            }
        }

        const string Unprovable = "could not prove which Claude session runs this command; nothing was changed. Run it from the Orchestrator's Claude session.";
        const string NoProof = "owner proof: no registered session is an ancestor of this command.";
        switch (proved.Nearest.Kind)
        {
            case NearestKind.Unavailable:
                throw new OwnerRefusal("owner-unproven", "unproven", $"ah roster {verb}: {Unprovable}", "owner proof: the process table could not be read.");
            case NearestKind.None:
                throw new OwnerRefusal("owner-unproven", "no-orchestrator", harnessAbove switch
                {
                    true => $"ah roster {verb}: could not prove which Claude session runs this command; nothing was changed. Run `ah roster checkin` in the Orchestrator's Claude session and retry; if that is refused too, start a new Claude session.",
                    false => $"ah roster {verb}: No Claude session is above this command. Run it inside the Orchestrator's Claude session.",
                    null => $"ah roster {verb}: {Unprovable}",
                }, NoProof);
            case NearestKind.Member:
                throw new OwnerRefusal(
                    $"member-cannot-{verb}",
                    "member-ancestor",
                    $"ah roster {verb}: this command runs under member {proved.Nearest.Member}, and a member session cannot run owner verbs; nothing was changed.",
                    $"owner proof: this command runs under member {proved.Nearest.Member}. {Seen(proved.Nearest)}");
        }

        if (c.StatedPidMismatch() is { } mismatch)
        {
            throw new OwnerRefusal(
                "orchestrator-pid-mismatch",
                proved.Nearest.Kind == NearestKind.Session ? "session" : "orchestrator",
                $"ah roster {verb}: {mismatch.Name} names pid {mismatch.Stated}, but this command runs under pid {proved.Pid}; nothing was changed.",
                $"owner proof: {mismatch.Name} is {mismatch.Stated}; the session above this command is pid {proved.Pid}.");
        }

        // A creating verb with no --team writes the file the settle step names, which may not be the legacy team.json read above.
        // When the scope moves, the settle runs once, here: it fixes c.TeamFile and the verb's own settle then finds the scope
        // already decided, so the file judged is the file written. When the legacy file is usable and not orphaned the scope does
        // not move, c.TeamFile stays null, and the verb's settle reads that file again; that is safe because the check below admits
        // only that team's owner.
        if (CreatesTeam.Contains(verb) && c.TeamFile is null)
        {
            TeamScopes.SettleWritableTeamScope(c, c.Dir);
            team = ExistingTeam(c);
        }

        var invoker = new TeamInvoker(proved.Pid, null, proved.PidStart);
        if (verb == "create" && c.Opts.Text("parent") is { } parentName
            && c.Services.Roster.ReadTeamRecord(c.Dir, parentName) is { IsNull: false } parentTeam && !c.Services.Roster.IsTeamOwnedBy(parentTeam, invoker))
        {
            throw new OwnerRefusal(
                "not-team-owner",
                "other-team",
                $"ah roster create: only the owner of {parentName} may create its lane teams; nothing was changed.",
                $"owner proof: the proved session does not own {parentName}. {Seen(proved.Nearest)}");
        }

        if (ActsOnLaneChild(verb, c) && ReadTeamNamed(c, laneTeam) is { } candidate && candidate.Member("parent") is { IsMap: true }
            && LaneChildCheck(c, proved, verb, invoker, candidate))
        {
            return;
        }

        // pid and start only: a session id that differs at the same process is a /clear the verb's own rebind repairs
        if (team is not { } existing || !ActOnExistingTeam.Contains(verb) || ActsOnAllOwned(c)
            || c.Services.Roster.IsTeamOwnedBy(existing, invoker))
        {
            return;
        }

        // a resumed session (same id, new pid) whose SessionStart rebind did not run re-owns its teams here, proved by its session id
        OrchestratorSession.Rebind(c, proved.Pid);
        if (ExistingTeam(c) is { } rebound && c.Services.Roster.IsTeamOwnedBy(rebound, invoker))
        {
            return;
        }

        var label = c.TeamFile is null ? "the default team" : $"team \"{c.TeamFile}\"";
        var held = RosterJson.Member(existing.Member("orchestrator"), "pid") is { } pid ? JsConvert.ToJsString(pid) : "(none)";
        var brokenLink = laneTeam is not null && ReadTeamNamed(c, laneTeam) is { } named && named.Member("parent") is { IsMap: true } && ValidLaneChild(c, laneTeam, named) is null
            ? BrokenLink(c, laneTeam, named)
            : null;
        throw new OwnerRefusal(
            "not-team-owner",
            "other-team",
            $"ah roster {verb}: this session does not own {label}; nothing was changed. Its Orchestrator record names pid {held}. Pass --team <name> for a team this session owns, or run the verb from that Orchestrator's session."
            + (brokenLink is null ? string.Empty : $" {laneTeam} is a lane child whose link to its manager is broken ({brokenLink}); from the manager's Orchestrator session run `ah roster adopt --team {laneTeam}`."),
            $"owner proof: the proved session is pid {proved.Pid}, not the Orchestrator recorded for {label}. {Seen(proved.Nearest)}",
            brokenLink is null ? "owns-neither" : "child-not-valid-lane");
    }

    /// <summary>
    /// Writes the session-pids row SessionStart writes for <paramref name="harnessPid"/>, the harness above an unmarked command that no walk
    /// could prove, under <c>CLAUDE_CODE_SESSION_ID</c> in the dirs the proof reads, and walks again. False, writing nothing, when the session
    /// id is unset, the shell carries a team file marker (A1), or the session id's newest row names another live process (A2); and when
    /// the walk still proves no Orchestrator or session.
    /// </summary>
    private static bool RegisterLate(RosterContext c, int harnessPid)
    {
        if (c.EnvText("CLAUDE_CODE_SESSION_ID") is not { } session || c.EnvText("WRANGL_TEAM_FILE") is not null)
        {
            return false;
        }

        var dirs = new[] { c.Dir, c.MainDir }.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.Ordinal).ToList();
        var status = c.Services.Hierarchy.Status;
        if (dirs.Any(d => status.SessionPidsOf(d!).Any(r => r.Key == session && r.Value != harnessPid && c.PidAlive(r.Value))))
        {
            return false;
        }

        foreach (var dir in dirs)
        {
            c.Services.Hierarchy.Status.AppendSessionPid(dir!, JsonValue.FromString(session), JsonValue.FromNumber(harnessPid));
        }

        c.ForgetProof();
        return c.Proved().Nearest.Kind is NearestKind.Orchestrator or NearestKind.Session;
    }

    /// <summary>The pid <c>CLAUDE_PID</c> names when it parses as an integer above 1; else null.</summary>
    private static int? StatedHarnessPid(RosterContext c) =>
        c.EnvText("CLAUDE_PID") is { } raw && int.TryParse(raw, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) && pid > 1 ? pid : null;

    /// <summary>Which link of a lane child's record fails the lane test: the lane leader, or the manager's incarnation.</summary>
    private static string BrokenLink(RosterContext c, string childName, JsonValue record)
    {
        var lane = RosterModule.LaneLeaderOf(c.Services.Roster, c.Dir, childName, name => ReadTeamNamed(c, name) ?? JsonValue.Null, role => c.Services.Config.RoleClass(role, c.Registry) == "orchestrate");
        return lane is null ? "no lane leader is recorded for it" : "it belongs to an earlier incarnation of its manager team";
    }

    private static bool ActsOnLaneChild(string verb, RosterContext c) =>
        verb == "adopt" || (ActOnExistingTeam.Contains(verb) && !ActsOnAllOwned(c));

    /// <summary>The verb's <c>--all</c> picks its teams from what the caller owns, so no one team is judged here.</summary>
    internal static bool ActsOnAllOwned(RosterContext c) => c.Command is "checkpoint" or "quit" or "pause" && c.Opts.IsTrue("all");

    /// <summary>
    /// The record of the team the lane test is about, before any verb redirects the ownership check to another team: read from the
    /// directory, else from the team home. The same name feeds this read and the lane test; null name reads the default team.
    /// </summary>
    internal static JsonValue? ReadTeamNamed(RosterContext c, string? name)
    {
        var roster = c.Services.Roster;
        var record = roster.ReadTeamRecord(c.Dir, name);
        if (record.IsNull && c.TeamHome != c.Dir)
        {
            record = roster.ReadTeamRecord(c.TeamHome, name);
        }

        return record.IsNull ? null : (JsonValue?)record;
    }

    /// <summary>
    /// The one lane determination: the lane a team record is the child team of, or null. A record is a valid lane child when its
    /// <c>parent</c> is a map, a lane leader is recorded for it, and it belongs to the current incarnation of its manager.
    /// </summary>
    internal static LaneLeader? ValidLaneChild(RosterContext c, string childName, JsonValue record)
    {
        if (record.Member("parent") is not { IsMap: true })
        {
            return null;
        }

        var roster = c.Services.Roster;
        var lane = RosterModule.LaneLeaderOf(roster, c.Dir, childName, name => ReadTeamNamed(c, name) ?? JsonValue.Null, role => c.Services.Config.RoleClass(role, c.Registry) == "orchestrate");
        return lane is not null && RosterModule.SameIncarnation(record, ReadTeamNamed(c, lane.Manager) ?? JsonValue.Null) ? lane : null;
    }

    /// <summary>
    /// A caller that owns a lane's child team, or the manager above it, is judged by the lane's rules whichever way it was proved.
    /// True when the check is settled and the verb may go on (the manager's owner for a staffing verb, or the registered lane lead);
    /// false when the caller owns neither team and today's ownership refusal applies.
    /// </summary>
    private static bool LaneChildCheck(RosterContext c, ProvedCaller proved, string verb, TeamInvoker invoker, JsonValue record)
    {
        var childName = c.TeamFile ?? proved.Nearest.Team;
        if (childName is null)
        {
            throw LaneOwnerUnproven(c, verb, record, null);
        }

        var roster = c.Services.Roster;
        var lane = ValidLaneChild(c, childName, record);
        if (lane is null)
        {
            // The link points at a lane that is gone: a leftover owner of the child (not the owner of the manager it names) may only tear it down.
            var named = MemberPlanning.Text(record.Member("parent")?.Member("team"));
            var namedManager = named is null ? JsonValue.Null : roster.ReadTeamRecord(c.Dir, named);
            if (roster.IsTeamOwnedBy(record, invoker) && !StaleLinkTeardown.Contains(verb)
                && (namedManager.IsNull || !roster.IsTeamOwnedBy(namedManager, invoker)))
            {
                throw StaleLink(verb, childName, record);
            }

            return false;
        }

        var manager = roster.ReadTeamRecord(c.Dir, lane.Manager);
        var orphanDisband = verb == "disband" && roster.TeamIsOrphaned(record);
        if (!manager.IsNull && roster.IsTeamOwnedBy(manager, invoker))
        {
            if (verb == "close-approve")
            {
                return true;
            }

            if (RosterModule.ParentOwnedVerbs.Contains(verb) && proved.Nearest is { Kind: NearestKind.Orchestrator, LegacyStart: false })
            {
                c.ParentOwnerLane = lane;
                return true;
            }

            if (orphanDisband)
            {
                return true;
            }

            throw ParentOwnerLaneText(verb, childName, lane, proved);
        }

        if (verb == "adopt" || orphanDisband)
        {
            throw ParentOwnerLaneText(verb, childName, lane, proved);
        }

        if (!roster.IsTeamOwnedBy(record, invoker))
        {
            return false;
        }

        if (c.Services.OwnerProof is not { } proof || SubOrchestratorCarve.RegisteredSo(c, proof, proved.Nearest, childName) is not { } registered)
        {
            throw LaneOwnerUnproven(c, verb, record, lane);
        }

        var carved = proved with { Carve = registered };
        c.AdoptProved(carved);
        SubOrchestratorCarve.CheckScope(c, carved);
        if (c.StatedPidMismatch() is { } stated)
        {
            throw new OwnerRefusal(
                "orchestrator-pid-mismatch",
                "session",
                $"ah roster {verb}: {stated.Name} names pid {stated.Stated}, but this command runs under pid {proved.Pid}; nothing was changed.",
                $"owner proof: {stated.Name} is {stated.Stated}; the session above this command is pid {proved.Pid}.");
        }

        return true;
    }

    private static readonly HashSet<string> StaleLinkTeardown = new(StringComparer.Ordinal) { "dismiss", "untrack", "disband" };

    private static OwnerRefusal StaleLink(string verb, string childName, JsonValue record)
    {
        var manager = MemberPlanning.Text(record.Member("parent")?.Member("team")) ?? "?";
        var stream = MemberPlanning.Text(record.Member("parent")?.Member("stream")) ?? "?";
        return new OwnerRefusal(
            "lane-owner-unproven",
            "lane-child-owner",
            $"ah roster {verb}: {childName} links to a lane ({manager}/{stream}) that no longer exists; only teardown (dismiss, untrack, disband) is allowed on it; nothing was changed.",
            "owner proof: the link names a lane whose manager or lane lead is not the current one.");
    }

    private static OwnerRefusal ParentOwnerLaneText(string verb, string childName, LaneLeader lane, ProvedCaller proved) =>
        new(
            "not-team-owner",
            "other-team",
            $"ah roster {verb}: {childName} is lane {lane.Stream}'s team, led by {lane.Member}; its parent's owner may run only {string.Join(", ", RosterModule.ParentOwnedVerbs.Take(RosterModule.ParentOwnedVerbs.Count - 1))} and {RosterModule.ParentOwnedVerbs[^1]} there; ask {lane.Member}@{lane.Manager} for {verb}, or, if {lane.Member} is gone, respawn the lane lead (it adopts the team). Nothing was changed.",
            $"owner proof: the proved session owns {lane.Manager}, the parent of {childName}. {Seen(proved.Nearest)}"
            + (RosterModule.ParentOwnedVerbs.Contains(verb) ? " This session is not registered as an Orchestrator: run `ah roster checkin` from it, then retry." : string.Empty),
            RosterModule.ParentOwnedVerbs.Contains(verb) ? "caller-not-orchestrator-registration" : "verb-not-parent-owned");

    private static OwnerRefusal LaneOwnerUnproven(RosterContext c, string verb, JsonValue record, LaneLeader? lane)
    {
        var child = c.TeamFile ?? "this team";
        var stream = lane?.Stream ?? MemberPlanning.Text(record.Member("parent")?.Member("stream")) ?? "?";
        var manager = lane?.Manager ?? MemberPlanning.Text(record.Member("parent")?.Member("team")) ?? "?";
        var member = lane?.Member ?? "the lane lead";
        return new OwnerRefusal(
            "lane-owner-unproven",
            "lane-child-owner",
            $"ah roster {verb}: {child} is lane {stream}'s team; only its registered lane lead may run owner verbs on it, and this session could not be proved to be that lead; nothing was changed. Ask the Orchestrator to dismiss and respawn {member}@{manager}.",
            "owner proof: the session that owns this lane child has no registration as its lane lead.");
    }

    /// <summary>
    /// Refuses a role-writing verb to an agent session: one that carries the member marker, or whose nearest registered ancestor is a
    /// member. It is not an owner verb, so a plain session outside any team still runs it.
    /// </summary>
    internal static void RefuseAgentRoleWrite(RosterContext c, string sub)
    {
        if (c.Services.OwnerProof is null)
        {
            return;
        }

        var proved = c.Proved();
        if (!proved.Marked && proved.Nearest.Kind != NearestKind.Member)
        {
            return;
        }

        var member = proved.Nearest.Member ?? "an agent";
        throw new OwnerRefusal(
            $"member-cannot-role-{sub}",
            "member-ancestor",
            $"ah roster role {sub}: this command runs under member {member}, and a member session cannot run owner verbs; nothing was changed.",
            $"owner proof: this command runs under member {member}. {Seen(proved.Nearest)}");
    }

    /// <summary>What the process walk found, for a refusal to print: the kind, whether the start matched only the legacy form, and the team and member. No pids or session ids.</summary>
    internal static string Seen(NearestSession nearest) =>
        $"walk: nearest={nearest.Kind} legacy-start={(nearest.LegacyStart ? "yes" : "no")} team={nearest.Team ?? "-"} member={nearest.Member ?? "-"}.";

    private static JsonValue? ExistingTeam(RosterContext c)
    {
        var roster = c.Services.Roster;
        var team = roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (team.IsNull && c.TeamHome != c.Dir)
        {
            team = roster.ReadTeamRecord(c.TeamHome, c.TeamFile);
        }

        if (c.Command == "close-approve" && !team.IsNull)
        {
            // the grant is the managing team's to give, so ownership is checked against the team the child hangs from
            var manager = team.Member("parent")?.Member("team") is { IsText: true } named ? named.AsString() : null;
            team = manager is null ? JsonValue.Null : roster.ReadTeamRecord(c.Dir, manager);
        }

        return team.IsNull ? null : (JsonValue?)team;
    }
}
