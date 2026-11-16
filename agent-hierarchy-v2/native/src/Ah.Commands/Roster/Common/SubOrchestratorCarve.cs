using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The sub-orchestrator a marked shell runs under: its team and member from the registered session, and a note when the member
/// marker in the environment does not agree with that registration.
/// </summary>
internal sealed record SoCarve(string Team, string Member, string? Disagreement, string? Child = null);

/// <summary>
/// The owner-verb carve-out for a sub-orchestrator. A marked shell is a sub-orchestrator only when the process ancestry proves it
/// runs under a registered member whose row has the orchestrate class; the environment values are a hint that must agree with that
/// registration and never decide anything. The carved verbs are limited to the sub-orchestrator's own child team.
/// </summary>
internal static class SubOrchestratorCarve
{
    /// <summary>The caller of a marked shell: a carved sub-orchestrator, an unproven claim to be one, or an ordinary member shell.</summary>
    internal static ProvedCaller ProveMarked(RosterContext c)
    {
        var member = new ProvedCaller(true, new NearestSession(NearestKind.None), null, null);
        var hinted = HintNamesOrchestrateMember(c);
        if (c.Services.OwnerProof is not { } proof)
        {
            return hinted ? member with { Nearest = new NearestSession(NearestKind.Unavailable), Unproven = true } : member;
        }

        var rowDirs = new[] { c.Dir, c.MainDir }.Where(d => !string.IsNullOrEmpty(d)).Select(d => d!).ToList();
        var nearest = proof.NearestAncestor(c.TeamHome, rowDirs);
        if (nearest.Kind == NearestKind.Member && !nearest.LegacyStart && nearest.Team is { } team && nearest.Member is { } name
            && RowIsOrchestrate(c, ReadTeam(c, team), name))
        {
            return new ProvedCaller(true, nearest, OrchestratorSession.StartOf(c, nearest.Pid), OrchestratorSession.ProcessTied(c, nearest.Pid))
            {
                Carve = new SoCarve(team, name, Agrees(c, team, name) ? null : "disagrees"),
            };
        }

        if ((RegisteredSo(c, proof, nearest, c.TeamFile) ?? RegisteredSo(c, proof, nearest)) is { } owned)
        {
            var carve = owned with { Disagreement = Agrees(c, owned.Team, owned.Member) ? null : "disagrees" };
            return new ProvedCaller(true, nearest, OrchestratorSession.StartOf(c, nearest.Pid), OrchestratorSession.ProcessTied(c, nearest.Pid)) { Carve = carve };
        }

        return hinted && nearest.Kind is NearestKind.None or NearestKind.Unavailable
            ? member with { Nearest = nearest, Unproven = true }
            : member;
    }

    /// <summary>
    /// The carve for a shell that carries no member marker: its process ancestry proves (pid and recorded start) that it runs under a
    /// registered member whose row has the orchestrate class. Only the carved verbs take it; the scope check then limits them to the
    /// streams that member is the sub-orchestrator of. Null for anything else.
    /// </summary>
    internal static ProvedCaller? FromAncestry(RosterContext c, NearestSession nearest) =>
        nearest.Kind == NearestKind.Member && !nearest.LegacyStart
        && nearest.Team is { } team && nearest.Member is { } name
        && (RosterModule.IsSubOrchestratorVerb(c.Command) || (OwnerGuard.IsGuarded(c.Command) && DutyDeputyOf(ReadTeam(c, team)) == name))
        && RowIsOrchestrate(c, ReadTeam(c, team), name)
            ? new ProvedCaller(true, nearest, OrchestratorSession.StartOf(c, nearest.Pid), OrchestratorSession.ProcessTied(c, nearest.Pid)) { Carve = new SoCarve(team, name, null) }
            : null;

    /// <summary>
    /// The walk resolves a sub-orchestrator that owns a child team as the Orchestrator of the first team it owns, so the carve accepts that answer
    /// only when registered state says the process is the sub-orchestrator of the managing team, whatever the environment says: it owns the child, the child hangs
    /// from a stream of the manager, and the manager's row for that stream is an orchestrate row registered to this very process.
    /// The fact is judged for <paramref name="target"/> (the team the command acts on; the walk's team when null), because a process
    /// that owns several teams is the walk's answer for only one of them. The marker is not read. Null when any of that fails.
    /// </summary>
    internal static SoCarve? RegisteredSo(RosterContext c, IRosterOwnerProof proof, NearestSession nearest, string? target = null)
    {
        if (nearest.Kind != NearestKind.Orchestrator || nearest.LegacyStart || (target ?? nearest.Team) is not { } childName)
        {
            return null;
        }

        var child = ReadTeam(c, childName);
        var owner = child.Member("orchestrator");
        var recordedStart = MemberPlanning.Text(owner?.Member("pid_start"));
        if (child.IsNull || owner?.Member("pid") is not { IsNumber: true } pid || pid.AsNumber() != nearest.Pid || string.IsNullOrEmpty(recordedStart)
            || c.Services.Callers is not { } callers || callers.OwnerProcessIsGone(pid.AsNumber(), recordedStart))
        {
            return null;
        }

        if (RosterModule.LaneLeaderOf(c.Services.Roster, c.Dir, childName, name => ReadTeam(c, name), role => c.Services.Config.RoleClass(role, c.Registry) == "orchestrate") is not { } lane)
        {
            return null;
        }

        var managerName = lane.Manager;
        var member = lane.Member;

        var tied = OrchestratorSession.ProcessTied(c, nearest.Pid);
        var registered = proof.RegistrationsOf(nearest.Pid)
            .Any(r => r.Address == $"{member}@{managerName}" && r.PidStart == recordedStart && !string.IsNullOrEmpty(r.SessionId) && (tied is null || tied == r.SessionId));
        return registered ? new SoCarve(managerName, member, null, childName) : null;
    }

    /// <summary>Refuses a carved caller's verb unless it is one of the carved verbs on the sub-orchestrator's own child team.</summary>
    internal static void CheckScope(RosterContext c, ProvedCaller proved)
    {
        var verb = c.Command;
        var carve = proved.Carve!;
        if (carve.Disagreement is not null)
        {
            throw new OwnerRefusal(
                "member-marker-disagrees",
                "member-marker",
                $"ah roster {verb}: the member marker disagrees with the registered session; nothing was changed.",
                "owner proof: AH_MEMBER_NAME or WRANGL_TEAM_FILE names a different member or team than the registered session.");
        }

        if (OnDutyDeputy(c, carve))
        {
            if (DeputyBarred.Contains(verb))
            {
                throw new OwnerRefusal(
                    $"deputy-cannot-{verb}",
                    "member-marker",
                    $"ah roster {verb}: the deputy on duty may not run {verb}; ask the chief at chief@{carve.Team}; nothing was changed.",
                    $"owner proof: {verb} stays the chief's while a deputy is on duty.");
            }

            return;
        }

        var parent = c.Opts.Text("parent");
        if (!RosterModule.IsSubOrchestratorVerb(verb) || (verb == "create" && parent is null))
        {
            throw proved.Marked ? MarkedRefusal(verb) : LaneLeadScope(c, verb);
        }

        SubOrchestratorClose.RefuseForcing(c, verb, force: true);
        if (verb == "move" && c.Opts.Has("replace-live"))
        {
            throw new OwnerRefusal(
                "lane-replace-live",
                ProofField(c),
                $"ah roster {verb}: a sub-orchestrator may not use --replace-live, because it displaces a live member's tree; nothing was changed.",
                "owner proof: --replace-live is refused for a sub-orchestrator.");
        }

        if (verb == "move" && PaneDestinationFlags.FirstOrDefault(f => c.Opts.Has(f) || c.Opts.IsTrue(f)) is { } flag)
        {
            throw new OwnerRefusal(
                "lane-pane-destination",
                ProofField(c),
                $"ah roster {verb}: a sub-orchestrator may not pass --{flag}; its members' panes stay within the child team's own tabs; nothing was changed.",
                $"owner proof: --{flag} is refused for a sub-orchestrator's move.");
        }

        var manager = ReadTeam(c, carve.Team);
        var streams = StreamsOf(manager, carve.Member);
        if (verb == "create")
        {
            var stream = c.Opts.Text("stream");
            if (parent != carve.Team || stream is null || !streams.Contains(stream) || (c.TeamArg is { } named && named != $"{carve.Team}-{stream}")
                || MemberPlanning.Text(manager.Member("streams")?.Member(stream)?.Member("state")) == "done")
            {
                throw ScopeMiss(c, verb, streams);
            }

            return;
        }

        if (c.TeamArg is null)
        {
            throw new OwnerRefusal(
                "child-team-unnamed",
                ProofField(c),
                $"ah roster {verb}: name the child team with --team; nothing was changed.",
                "owner proof: a sub-orchestrator's owner verb must name its child team.");
        }

        var child = c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile);
        if (child.IsNull || child.Member("parent") is not { IsMap: true } link
            || MemberPlanning.Text(link.Member("team")) != carve.Team
            || MemberPlanning.Text(link.Member("stream")) is not { } ws || !streams.Contains(ws))
        {
            throw ScopeMiss(c, verb, streams);
        }

        var owned = verb == "adopt"
            ? c.Services.Roster.TeamIsOrphaned(child)
            : c.Services.Roster.IsTeamOwnedBy(child, new TeamInvoker(proved.Pid, null, proved.PidStart));
        if (!owned)
        {
            throw ScopeMiss(c, verb, streams);
        }
    }

    /// <summary>The owner verbs the chief keeps while a deputy is on duty.</summary>
    private static readonly HashSet<string> DeputyBarred = new(StringComparer.Ordinal)
    {
        "quit", "stop", "disband", "untrack", "adopt", "duty", "lane-caps", "resume", "create",
    };

    /// <summary>
    /// The carved member is the deputy on duty for the team the command acts on: its own team, whose <c>duty.deputy</c> names it. The
    /// carve itself was proved from this command's process ancestry, so the deputy's row is held by a running process.
    /// </summary>
    private static bool OnDutyDeputy(RosterContext c, SoCarve carve) =>
        carve.Child is null && c.TeamFile == carve.Team && DutyDeputyOf(ReadTeam(c, carve.Team)) == carve.Member;

    private static string? DutyDeputyOf(JsonValue team) => MemberPlanning.Text(team.Member("duty")?.Member("deputy"));

    /// <summary>The proof field a scope refusal prints: the member marker for a marked shell, the lane child's owner for an unmarked one.</summary>
    private static string ProofField(RosterContext c) => c.Proved().Marked ? "member-marker" : "lane-child-owner";

    private static OwnerRefusal LaneLeadScope(RosterContext c, string verb)
    {
        var child = c.TeamFile ?? "this team";
        var ws = MemberPlanning.Text(c.Services.Roster.ReadTeamRecord(c.Dir, c.TeamFile).Member("parent")?.Member("stream")) ?? "?";
        return new OwnerRefusal(
            "lane-lead-scope",
            "lane-child-owner",
            $"ah roster {verb}: {child} is lane {ws}'s team; its lead may run only {string.Join(", ", RosterModule.CarvedVerbs)} on it; nothing was changed.",
            "owner proof: this verb is not one of the verbs carved to a lane lead.");
    }

    private static readonly string[] PaneDestinationFlags = ["tab", "workspace", "new-workspace", "allow-global"];

    internal static OwnerRefusal MarkedRefusal(string verb) =>
        new(
            $"member-cannot-{verb}",
            "member-marker",
            $"ah roster {verb}: this shell carries a member marker, and a member session cannot run owner verbs; nothing was changed.",
            "owner proof: this shell carries a member marker.");

    /// <summary>The streams of the manager team whose sub-orchestrator is <paramref name="member"/>.</summary>
    private static List<string> StreamsOf(JsonValue manager, string member) =>
        manager.Member("streams") is { IsMap: true } streams
            ? streams.AsObject().Keys.Where(k => MemberPlanning.Text(streams.AsObject()[k].Member("sub_orchestrator")) == member).ToList()
            : [];

    private static OwnerRefusal ScopeMiss(RosterContext c, string verb, List<string> streams)
    {
        var carve = c.Proved().Carve!;
        var expected = streams.Select(ws => $"{carve.Team}-{ws}").FirstOrDefault(n => !c.Services.Roster.ReadTeamRecord(c.Dir, n).IsNull) ?? "none yet";
        return new OwnerRefusal(
            "not-child-team",
            ProofField(c),
            $"ah roster {verb}: this sub-orchestrator may run owner verbs only on its own child team ({expected}); nothing was changed.",
            "owner proof: the target is not the child team of this sub-orchestrator's stream.");
    }

    internal static JsonValue ReadTeam(RosterContext c, string team)
    {
        var record = c.Services.Roster.ReadTeamRecord(c.Dir, team);
        return record.IsNull && c.TeamHome != c.Dir ? c.Services.Roster.ReadTeamRecord(c.TeamHome, team) : record;
    }

    private static bool RowIsOrchestrate(RosterContext c, JsonValue team, string name) =>
        team.Member("members") is { IsArray: true } list
        && list.AsArray().FirstOrDefault(m => MemberPlanning.Text(m.Member("name")) == name) is { IsMap: true } row
        && MemberPlanning.Text(row.Member("role")) is { } role
        && c.Services.Config.RoleClass(role, c.Registry) == "orchestrate";

    /// <summary>The environment's member and team file name the registered member and team.</summary>
    private static bool Agrees(RosterContext c, string team, string member)
    {
        if (c.EnvText(MeshModule.MemberMarkerEnv) != member)
        {
            return false;
        }

        if (c.EnvText("WRANGL_TEAM_FILE") is not { } file)
        {
            return true;
        }

        var full = Path.GetFullPath(file);
        return new[] { c.Dir, c.TeamHome }.Where(d => !string.IsNullOrEmpty(d)).Any(d => Path.GetFullPath(c.Services.Roster.TeamPath(d, team)) == full);
    }

    /// <summary>The environment hint names a member whose row has the orchestrate class.</summary>
    private static bool HintNamesOrchestrateMember(RosterContext c) =>
        c.EnvText(MeshModule.MemberMarkerEnv) is { } name && c.EnvText("WRANGL_TEAM_FILE") is { } file
        && Path.IsPathRooted(file) && RowIsOrchestrate(c, c.Services.Roster.ReadTeamFileAt(file), name);
}
