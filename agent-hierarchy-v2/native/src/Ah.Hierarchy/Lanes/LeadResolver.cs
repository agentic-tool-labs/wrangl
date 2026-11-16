using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>A lead: the member <paramref name="Name"/> of team <paramref name="Team"/> that a member takes its direction from.</summary>
public sealed record LeadRef(string Team, string Name);

/// <summary>
/// Who a member answers to. A member row's <c>lead</c> names another team's member (a lane's sub-orchestrator, or a parent's
/// Orchestrator); a row with no usable <c>lead</c>, or whose lead is not live, answers to the Orchestrator of its home team.
/// </summary>
internal static class LeadResolver
{
    public const string OrchestratorName = "orchestrator";

    public static LeadRef? LeadOf(JsonValue row) =>
        row.Member("lead") is { IsMap: true } lead
        && lead.Member("team") is { IsText: true } team && team.AsString().Length > 0
        && lead.Member("name") is { IsText: true } name && name.AsString().Length > 0
            ? new LeadRef(team.AsString(), name.AsString())
            : null;

    public static LeadRef Effective(string homeTeam, JsonValue row, Func<string, JsonValue> team, Func<JsonValue, JsonValue?, bool> identityLive) =>
        DissolvedParent(team, team(homeTeam), homeTeam) is { } manager
            ? new LeadRef(manager, OrchestratorName)
            : ValidLead(homeTeam, row, team, identityLive) ?? new LeadRef(homeTeam, OrchestratorName);

    /// <summary>
    /// The manager team of <paramref name="teamRecord"/> when it is the child team of a lane that is over: it is named
    /// <see cref="LaneChildTeam.NameOf"/> of its recorded parent team and stream, its workstream is dissolved and the manager's
    /// stream is done. Its members then answer to that manager's Orchestrator. Null otherwise.
    /// </summary>
    public static string? DissolvedParent(Func<string, JsonValue> team, JsonValue teamRecord, string teamName)
    {
        if (teamRecord.Member("workstream") is not { IsMap: true } workstream || Text(workstream.Member("state")) != "dissolved"
            || teamRecord.Member("parent") is not { IsMap: true } parent || Text(parent.Member("team")) is not { } manager || Text(parent.Member("stream")) is not { } stream
            || teamName != LaneChildTeam.NameOf(manager, stream))
        {
            return null;
        }

        return team(manager).Member("streams") is { IsMap: true } streams && streams.AsObject().Get(stream) is { IsMap: true } record && Text(record.Member("state")) == "done" && Text(record.Member("sub_orchestrator")) is not null
            ? manager
            : null;
    }

    public static LeadRef? ValidLead(string homeTeam, JsonValue row, Func<string, JsonValue> team, Func<JsonValue, JsonValue?, bool> identityLive)
    {
        if (LeadOf(row) is not { } lead)
        {
            return null;
        }

        var lane = team(lead.Team);
        if (LaneLink.ManagerOf(lane) is not { } manager)
        {
            return null;
        }

        var managerRecord = team(manager);
        if (LaneLink.ConfirmedStream(manager, managerRecord, lead.Team, lane) is not { } stream)
        {
            return null;
        }

        var record = managerRecord.Member("streams") is { IsMap: true } streams ? streams.AsObject().Get(stream) : null;
        return record is { IsMap: true } r && Text(r.Member("sub_orchestrator")) == lead.Name && Text(r.Member("state")) != "done"
            && HasSubOrchestratorRow(managerRecord, lead.Name, stream) && OrchestratorLive(lane, identityLive)
            ? lead
            : null;
    }

    /// <summary>The manager team that confirms the lane whose child team is <paramref name="lead"/>'s team, or null when that team names no parent.</summary>
    public static string? ManagerOf(LeadRef lead, Func<string, JsonValue> team) => LaneLink.ManagerOf(team(lead.Team));

    private static bool HasSubOrchestratorRow(JsonValue home, string name, string stream) =>
        home.Member("members") is { IsArray: true } rows
        && rows.AsArray().Any(m => m.IsMap && Text(m.Member("name")) == name && IsOrchestrateRow(m) && Text(m.Member("stream")) == stream);

    /// <summary>Whether a member row has a built-in role of the orchestrate class. The one test for "this row runs a lane", shared by the lead rule and the mesh.</summary>
    public static bool IsOrchestrateRow(JsonValue row) =>
        row.IsMap && Text(row.Member("role")) is { } role && Ah.Config.ConfigModule.BuiltinRoleClass(role) == "orchestrate";

    public static bool OrchestratorLive(JsonValue teamRecord, Func<JsonValue, JsonValue?, bool> identityLive) =>
        teamRecord.Member("orchestrator") is { IsMap: true } orchestrator && orchestrator.Member("pid") is { } pid
        && Text(orchestrator.Member("pid_start")) is not null && identityLive(pid, orchestrator.Member("pid_start"));

    private static string? Text(JsonValue? v) => v is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
}
