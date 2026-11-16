using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;

namespace Ah.Cli.Composition;

/// <summary>Who is calling the cutover route, as the daemon resolved it from the peer process.</summary>
internal enum CutoverCallerKind
{
    User,
    TopOrchestrator,
    SubOrchestrator,
    Member,
    Unknown,

    /// <summary>The live deputy on duty of a top team, holding its <c>orchestrator@</c> box: it decides for the team's work, never authority.</summary>
    DeputyChief,
}

/// <summary>A resolved caller: its kind and the reason that decided it, for the audit row and the refusal text.</summary>
internal sealed record CutoverCaller(CutoverCallerKind Kind, string Detail)
{
    public bool MayChangeAuthority => Kind is CutoverCallerKind.User or CutoverCallerKind.TopOrchestrator;

    public string Name => Kind switch
    {
        CutoverCallerKind.User => "user",
        CutoverCallerKind.TopOrchestrator => "orchestrator",
        CutoverCallerKind.SubOrchestrator => "sub-orchestrator",
        CutoverCallerKind.Member => "member",
        CutoverCallerKind.DeputyChief => "deputy-chief",
        _ => "unknown",
    };
}

/// <summary>The facts the top-level test reads, each from the kernel or from a record only ah's owner verbs and hooks write.</summary>
internal interface ICutoverFacts
{
    /// <summary>The registration's <c>team_files</c> entry for exactly <paramref name="address"/>; null when there is none (no fallback to the single <c>team_file</c>).</summary>
    string? TeamFileEntry(string sessionId, string address);

    /// <summary>The team record at <paramref name="path"/>; a non-map value when it cannot be read.</summary>
    JsonValue ReadTeamFile(string path);

    /// <summary>Whether process <paramref name="pid"/> is alive with the start time <paramref name="pidStart"/>.</summary>
    bool ProcessIs(int pid, string pidStart);

    /// <summary>Whether <paramref name="pid"/> is the pid, or the bare pid, of a member row in any team record of this AH home.</summary>
    bool IsMemberProcess(int pid);

    /// <summary>Whether a manager team in <paramref name="hierDir"/> has a stream entry, open or done, that resolves to <paramref name="team"/> as its lane child.</summary>
    bool ManagerNamesLane(string hierDir, string team);
}

/// <summary>
/// Classifies the principal behind a cutover request. A session is a top-level Orchestrator only when its registration names the
/// team by key, the team's record names the session's own process as its Orchestrator, that process is no member row of any team,
/// no manager's stream entry names the team as a lane, and the team's own record claims no parent. The record's own claim is only
/// ever believed when it refuses. A caller holding several teams is judged on <c>preferTeam</c> first, so one proven for that team
/// is named by it rather than by whichever other team it also holds comes first (b467).
/// </summary>
internal static class CutoverCallers
{
    public static CutoverCaller Classify(CallerPrincipal principal, ICutoverFacts facts, string? preferTeam = null)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(facts);
        if (principal.Kind == PrincipalKind.User)
        {
            return new CutoverCaller(CutoverCallerKind.User, "user");
        }

        if (principal.Kind != PrincipalKind.Session)
        {
            return new CutoverCaller(CutoverCallerKind.Unknown, "no registered session");
        }

        var claimed = principal.Sessions
            .SelectMany(s => s.Addresses.Select(a => (Session: s, Parts: a.Split('@', 2))))
            .Where(x => x.Parts.Length == 2 && x.Parts[0] == "orchestrator" && x.Parts[1].Length > 0)
            .Select(x => (x.Session, Team: x.Parts[1]))
            .OrderBy(x => x.Team == preferTeam ? 0 : 1)
            .ToList();
        if (claimed.Count == 0)
        {
            // ah2-32: a session that holds no address at all and is no team's member process is the user's own top-level session:
            // an Orchestrator before it has a team. It is the user's surrogate, so it is not refused for lacking a team.
            if (principal.Sessions.Count > 0 && principal.Sessions.All(s => s.Addresses.Count == 0 && !facts.IsMemberProcess(s.Pid)))
            {
                return new CutoverCaller(CutoverCallerKind.TopOrchestrator, "top-level session with no team");
            }

            return new CutoverCaller(CutoverCallerKind.Member, string.Join(",", principal.Sessions.SelectMany(s => s.Addresses)));
        }

        CutoverCaller? refused = null;
        CutoverCaller? deputy = null;
        foreach (var (session, team) in claimed)
        {
            var verdict = Judge(session, team, facts);
            if (verdict.Kind == CutoverCallerKind.TopOrchestrator)
            {
                return verdict;
            }

            if (verdict.Kind == CutoverCallerKind.DeputyChief)
            {
                deputy ??= verdict;
                continue;
            }

            if (refused is null || Severity(verdict.Kind) > Severity(refused.Kind))
            {
                refused = verdict;
            }
        }

        return deputy ?? refused!;
    }

    private static int Severity(CutoverCallerKind kind) => kind switch
    {
        CutoverCallerKind.Unknown => 3,
        CutoverCallerKind.SubOrchestrator => 2,
        _ => 1,
    };

    private static CutoverCaller Judge(SessionRegistration session, string team, ICutoverFacts facts)
    {
        var address = HierarchyModule.OrchestratorAddress(team);
        if (facts.TeamFileEntry(session.SessionId, address) is not { Length: > 0 } entry)
        {
            return new CutoverCaller(CutoverCallerKind.Unknown, $"the registration has no team_files entry for {address}");
        }

        if (HierDirOf(entry, team) is not { } hierDir)
        {
            return new CutoverCaller(CutoverCallerKind.Unknown, $"the team_files entry of {address} is not <hier_dir>/teams/{team}.json");
        }

        var record = facts.ReadTeamFile(entry);
        if (!record.IsMap)
        {
            return new CutoverCaller(CutoverCallerKind.Unknown, $"the team record of {team} could not be read");
        }

        var recorded = record.AsObject().Get("orchestrator") is { IsMap: true } o ? o.AsObject() : null;
        if (recorded?.Get("pid") is not { IsNumber: true } pid || pid.AsNumber() != session.Pid
            || recorded.Get("pid_start") is not { IsText: true } start || !facts.ProcessIs(session.Pid, start.AsString()))
        {
            return IsLiveDeputy(record.AsObject(), session.Pid, facts)
                ? new CutoverCaller(CutoverCallerKind.DeputyChief, address)
                : new CutoverCaller(CutoverCallerKind.Member, $"the record of {team} does not name this process as its Orchestrator");
        }

        if (facts.IsMemberProcess(session.Pid))
        {
            return new CutoverCaller(CutoverCallerKind.SubOrchestrator, $"{address}: the process is a member row of a team");
        }

        if (facts.ManagerNamesLane(hierDir, team))
        {
            return new CutoverCaller(CutoverCallerKind.SubOrchestrator, $"{address}: a manager's stream entry names {team} as a lane");
        }

        return record.AsObject().Get("parent") is { IsTruthy: true }
            ? new CutoverCaller(CutoverCallerKind.SubOrchestrator, $"{address}: the record of {team} claims a parent")
            : new CutoverCaller(CutoverCallerKind.TopOrchestrator, address);
    }

    /// <summary>
    /// The top team's <c>duty.deputy</c> row is held by <paramref name="pid"/> with the row's recorded start: the deputy on duty, proved
    /// as the chief is (pid and start from the record), never by the registration's own claim.
    /// </summary>
    private static bool IsLiveDeputy(JsonObject record, int pid, ICutoverFacts facts) =>
        record.Get("parent") is not { IsTruthy: true }
        && record.Get("duty")?.Member("deputy") is { IsText: true } deputy && deputy.AsString().Length > 0
        && record.Get("members") is { IsArray: true } members
        && members.AsArray().FirstOrDefault(m => m.Member("name") is { IsText: true } n && n.AsString() == deputy.AsString()) is { IsMap: true } row
        && row.Member("pid") is { IsNumber: true } rowPid && rowPid.AsNumber() == pid
        && row.Member("pid_start") is { IsText: true } rowStart && facts.ProcessIs(pid, rowStart.AsString());

    /// <summary>The hierarchy directory of a team file path of the form <c>&lt;hier_dir&gt;/teams/&lt;team&gt;.json</c>; null for any other shape.</summary>
    internal static string? HierDirOf(string teamFile, string team)
    {
        if (Path.GetFileName(teamFile) != team + ".json" || Path.GetDirectoryName(teamFile) is not { } teams || Path.GetFileName(teams) != "teams")
        {
            return null;
        }

        return Path.GetDirectoryName(teams) is { Length: > 0 } dir ? dir : null;
    }
}
