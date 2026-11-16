using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>
/// The one question "is this team the child of that manager's lane?", asked by routing and by the team view alike. A child team's file
/// names its parent, and a child's members can write that file, so a claim is a lane only when the manager's own file records the
/// stream too and the child carries the name the manager would have given it.
/// </summary>
internal static class LaneLink
{
    /// <summary>The name of the child team of lane <paramref name="stream"/> of <paramref name="manager"/>.</summary>
    public static string ChildName(string manager, string stream) => LaneChildTeam.NameOf(manager, stream);

    /// <summary>Whether <paramref name="childRecord"/> is a team file that names the lane <paramref name="stream"/> of <paramref name="manager"/> as its parent. The child's side only; a caller that must trust the answer uses <see cref="ConfirmedStream"/>.</summary>
    public static bool IsLaneOf(JsonValue childRecord, string manager, string stream) =>
        !childRecord.IsNull && childRecord.Member("parent") is { IsMap: true } parent
        && Text(parent.Member("team")) == manager && Text(parent.Member("stream")) == stream;

    /// <summary>
    /// Whether the session <paramref name="sessionId"/> is the orchestrating session of a confirmed lane of <paramref name="managerName"/> whose
    /// sub-orchestrator is <paramref name="member"/>: the manager's file records a stream with that sub-orchestrator, the child team named for it
    /// confirms the stream (<see cref="ConfirmedStream"/>), and the child's file names the session as its Orchestrator.
    /// </summary>
    public static bool IsLaneOrchestrator(string managerName, JsonValue managerRecord, string member, string sessionId, Func<string, JsonValue> team)
    {
        if (managerRecord.IsNull || managerRecord.Member("streams") is not { IsMap: true } streams)
        {
            return false;
        }

        foreach (var stream in streams.AsObject().Keys)
        {
            if (streams.AsObject().Get(stream) is not { IsMap: true } record || Text(record.Member("sub_orchestrator")) != member)
            {
                continue;
            }

            var child = ChildName(managerName, stream);
            var childRecord = team(child);
            if (ConfirmedStream(managerName, managerRecord, child, childRecord) == stream
                && Text(childRecord.Member("orchestrator")?.Member("session_id")) == sessionId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The manager team a child team's record names as its parent, or null when it names none.</summary>
    public static string? ManagerOf(JsonValue childRecord) =>
        childRecord.Member("parent") is { IsMap: true } parent ? Text(parent.Member("team")) : null;

    /// <summary>
    /// The stream of <paramref name="managerName"/> that <paramref name="childName"/> is the child team of, or null when it is none: the child's
    /// file names the manager and a stream, the manager's file records that stream with a sub-orchestrator (the lane exists), and the child is named for it.
    /// </summary>
    public static string? ConfirmedStream(string managerName, JsonValue managerRecord, string childName, JsonValue childRecord)
    {
        if (childRecord.IsNull || managerRecord.IsNull || childRecord.Member("parent") is not { IsMap: true } parent
            || Text(parent.Member("team")) != managerName || Text(parent.Member("stream")) is not { } stream)
        {
            return null;
        }

        return managerRecord.Member("streams") is { IsMap: true } streams && streams.AsObject().Get(stream) is { IsMap: true } record
            && Text(record.Member("sub_orchestrator")) is not null
            && childName == ChildName(managerName, stream)
            ? stream
            : null;
    }

    /// <summary>
    /// Where the messages of the member of <paramref name="memberRow"/> in team <paramref name="team"/> go, and why: <c>dissolved-lane</c> when its team is a
    /// lane that is over (its manager's Orchestrator hears it), <c>lead</c> when its named lead is valid and live, <c>none</c> when it names a lead that is
    /// not usable (its home Orchestrator hears it). Null for a member that names no lead in a lane that is not over. It asks the resolver the routing asks.
    /// </summary>
    public static (string Team, string Name, string Via)? RouteOf(string team, JsonValue memberRow, Func<string, JsonValue> record, Func<JsonValue, JsonValue?, bool> identityLive)
    {
        if (LeadResolver.DissolvedParent(record, record(team), team) is { } manager)
        {
            return (manager, LeadResolver.OrchestratorName, "dissolved-lane");
        }

        if (LeadResolver.LeadOf(memberRow) is null)
        {
            return null;
        }

        var to = LeadResolver.Effective(team, memberRow, record, identityLive);
        return (to.Team, to.Name, LeadResolver.ValidLead(team, memberRow, record, identityLive) is null ? "none" : "lead");
    }

    private static string? Text(JsonValue? v) => v is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
}
