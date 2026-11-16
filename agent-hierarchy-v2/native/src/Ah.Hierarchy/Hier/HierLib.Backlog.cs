using Ah.Core.Json;

namespace Ah.Hierarchy;

/// <summary>The backlog's groom health where the hierarchy reports it: status.json, the state block, and the Orchestrator's due line.</summary>
internal sealed partial class HierLib
{
    /// <summary>The backlog health facts of status dir <paramref name="dir"/> (its <c>backlog</c> entry), or null with none or unreadable (fail open).</summary>
    private JsonValue BacklogFactsOf(string dir)
    {
        try
        {
            return _backlogFacts?.Invoke(dir) is { } facts ? facts : JsonValue.Null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return JsonValue.Null;
        }
    }

    /// <summary>The backlog health a status shows at <paramref name="nowMs"/> from stored facts, or null when there are none or it shows nothing.</summary>
    private static BacklogHealth? BacklogFromFacts(JsonValue facts, string dir, double nowMs, Action<double>? flip = null)
    {
        if (!facts.IsMap)
        {
            return null;
        }

        // a backlog that is not shown puts nothing in the status, so its clock rules move nothing: whether it is shown (it has an
        // item, or was groomed) does not depend on the clock
        var flips = flip is null ? null : new List<double>();
        var health = Backlog.HealthFromFacts(facts, dir, (long)nowMs, null, flips is null ? null : flips.Add);
        if (!HierarchyModule.BacklogShown(health))
        {
            return null;
        }

        flips?.ForEach(flip!);
        return health;
    }

    /// <summary>The backlog health of hierarchy dir <paramref name="dir"/>, or null when there is no backlog or it cannot be read (fail open).</summary>
    private BacklogHealth? BacklogHealthOf(string? dir)
    {
        if (_backlogHealth is null || string.IsNullOrEmpty(dir))
        {
            return null;
        }

        try
        {
            return _backlogHealth(dir);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The backlog of hierarchy dir <paramref name="dir"/> as a plan, or null when there is none or it cannot be read (fail open).</summary>
    private PlanState? BacklogPlanOf(string dir)
    {
        try
        {
            return _backlogPlan?.Invoke(dir);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    private bool IsMemberSession(JsonValue? sessionId) =>
        sessionId is { IsText: true } && _config.ReadSessionRole(sessionId.Value) is { IsText: true } role && role.AsString() != "orchestrator";

    /// <summary>
    /// The due line for an Orchestrator session's prompt, once per due episode: an episode is keyed by its <c>due_since</c>, recorded as a
    /// <c>backlog-due</c> peer row of the session (as the CLI root line is). Null when no groom is due or the session was already told.
    /// </summary>
    public string? TakeBacklogDueLine(string dir, JsonValue? sessionId)
    {
        if (sessionId is not { IsText: true } sid || sid.AsString().Length == 0 || BacklogHealthOf(dir) is not { Due: true, DueSinceMs: { } since } health)
        {
            return null;
        }

        var key = since.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var rows = SessionRows(sid, "backlog-due");
        if (rows.Any(r => r.Member("type") is { IsText: true } t && t.AsString() == "backlog-due" && r.Member("due_since") is { IsText: true } d && d.AsString() == key))
        {
            return null;
        }

        try
        {
            AppendPeerRecordRouted(new JsonObject { ["type"] = "backlog-due", ["session_id"] = sid, ["due_since"] = key, ["ts"] = IsoNowString() }, TimeSpan.FromMilliseconds(150));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the record keeps the line to once per episode; failing to write it only risks saying it again
        }

        return HierarchyModule.BacklogDueLine(health.Reason);
    }
}
