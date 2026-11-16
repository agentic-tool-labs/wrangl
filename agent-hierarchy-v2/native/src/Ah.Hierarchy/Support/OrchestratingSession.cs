using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The session that orchestrates a team: where its mail goes and what its messages are filed as.</summary>
/// <param name="SessionId">The session's id.</param>
/// <param name="Address">The mailbox address of the session.</param>
/// <param name="Role">The role the session's dispatches are filed under.</param>
/// <param name="Pid">The recorded process id, or null.</param>
/// <param name="PidStart">The recorded process start, or null.</param>
public sealed record OrchestratingSession(string SessionId, string Address, string Role, int? Pid, string? PidStart);

/// <summary>The one place that answers "who orchestrates team T"; the stall tooling asks here and nowhere else.</summary>
internal static class OrchestratingSessions
{
    /// <summary>The role the top-level Orchestrator files its dispatches under.</summary>
    internal const string TopRole = "orchestrator";

    /// <summary>The orchestrating session of <paramref name="team"/> as its team record names it, or null when it names none.</summary>
    internal static OrchestratingSession? Of(JsonValue? teamRecord, string team)
    {
        if (teamRecord is not { IsMap: true } record || record.AsObject().Get("orchestrator") is not { IsMap: true } o
            || o.AsObject().Get("session_id") is not { IsText: true } session || session.AsString().Length == 0)
        {
            return null;
        }

        var fields = o.AsObject();
        return new OrchestratingSession(
            session.AsString(),
            HierarchyModule.OrchestratorAddress(team),
            TopRole,
            fields.Get("pid") is { IsNumber: true } pid ? (int)pid.AsNumber() : null,
            fields.Get("pid_start") is { IsText: true } start ? start.AsString() : null);
    }
}
