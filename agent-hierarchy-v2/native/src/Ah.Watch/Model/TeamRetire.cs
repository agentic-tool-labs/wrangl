using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>A team a retire pass found: its hierarchy dir, its name, and the instant of its own last work.</summary>
internal sealed record RetireCandidate(string Dir, string Team, long LastWorkMs);

/// <summary>What one retire pass found: the teams to retire, and the team files the read model could not read.</summary>
internal sealed record RetirePass(IReadOnlyList<RetireCandidate> Retire, IReadOnlyList<string> Unreadable)
{
    public static readonly RetirePass Empty = new([], []);
}

/// <summary>
/// Which teams the daemon retires (ah-ui-focus §6): no marker, no live member, nothing asked of a live session, no work of
/// its own for <see cref="SummaryData.StaleAfterMs"/>, and an Orchestrator that is dead or leads another team with a live member. A team reopened since its last work stays open.
/// </summary>
internal static class TeamRetire
{
    /// <summary>One pass. <paramref name="summaryOf"/> gives a team's summary as served, by slice id; <paramref name="recordedDead"/> says whether the process a team's record names for its Orchestrator is gone, for one the readings leave undecided.</summary>
    public static RetirePass Find(IRecordView records, IEnumerable<string> teamKeys, Func<string, JsonObject?> summaryOf, long nowMs, Func<string, string, bool> recordedDead)
    {
        var teams = TreeData.Teams(records);
        var summaries = teams.ToDictionary(static t => t, t => summaryOf("team-summary:" + t.DirId + "/" + t.Name));
        var retire = new List<RetireCandidate>();
        foreach (var team in teams)
        {
            if (team.Value.Member("closed") is { IsMap: true } || summaries[team] is not { } body || LiveMember(body))
            {
                continue;
            }

            var lastWork = LaneParts.Ms(body.Get("updated_at") is { IsText: true } updated ? updated.AsString() : null);
            if (nowMs < lastWork + SummaryData.StaleAfterMs
                || lastWork <= LaneParts.Ms(team.Value.Member("reopened") is { IsMap: true } reopened ? LaneParts.Text(reopened, "at") : null)
                || AskedOfALiveSession(records, team, body)
                || !OrchestratorIsDone(team, body, teams, summaries, recordedDead))
            {
                continue;
            }

            retire.Add(new RetireCandidate(team.Dir, team.Name, lastWork));
        }

        // a team file that does not read as a record is listed by the lane and holds no record, and so is in no list above
        var unreadable = teamKeys
            .Where(k => records.Get(TeamLane.LaneName, k) is not { IsAbsent: false })
            .Select(static k => LaneParts.Split(k).Second)
            .ToList();
        return new RetirePass(retire, unreadable);
    }

    private static string? Liveness(JsonObject body, string key) =>
        body.Get("member_states") is { IsMap: true } states && states.AsObject().Get(key) is { IsMap: true } entry ? LaneParts.Text(entry, "liveness") : null;

    private static IEnumerable<string> MemberKeys(JsonObject body) =>
        body.Get("member_states") is { IsMap: true } states ? states.AsObject().Keys.Where(static k => !IsOrchestrator(k)) : [];

    private static bool IsOrchestrator(string key) => key.EndsWith("/" + TreeIds.Orchestrator, StringComparison.Ordinal);

    private static bool LiveMember(JsonObject body) => MemberKeys(body).Any(k => Liveness(body, k) == "live");

    private static bool AskedOfALiveSession(IRecordView records, TeamEntry team, JsonObject body) =>
        ExchangeFiling.OfTeam(records, team.DirId, team.Name).Any(x =>
            ExchangeFiling.IsOpen(x.Record) && ExchangeFiling.Resolve(x.Record, team).To is { } to && Liveness(body, TreeIds.Member(team.Dir, team.Name, to)) == "live");

    private static bool OrchestratorIsDone(TeamEntry team, JsonObject body, IReadOnlyList<TeamEntry> teams, Dictionary<TeamEntry, JsonObject?> summaries, Func<string, string, bool> recordedDead)
    {
        var word = Liveness(body, TreeIds.Member(team.Dir, team.Name, TreeIds.Orchestrator));
        if (word is null or "proven_dead" || (word != "live" && recordedDead(team.Dir, team.Name)))
        {
            return true;
        }

        return SessionOf(team) is { } session
            && teams.Any(other => !ReferenceEquals(other, team) && SessionOf(other) == session && summaries[other] is { } summary && LiveMember(summary));
    }

    private static string? SessionOf(TeamEntry team) =>
        team.Value.Member("orchestrator") is { IsMap: true } orchestrator ? LaneParts.Text(orchestrator, "session_id") : null;
}
