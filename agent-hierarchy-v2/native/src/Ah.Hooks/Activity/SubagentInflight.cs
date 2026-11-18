using Ah.Abstractions;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// Recorder of this session's in-flight background work (subagents, background Bash tasks and Monitors), which the peer nudge reads
/// to tell a waiting peer from a finished one. Silent, never blocks. Not gated on the hierarchy being enabled.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class SubagentInflight
{
    [GeneratedRegex("<task-notification>((?s:.*?))</task-notification>", RegexOptions.CultureInvariant)]
    private static partial Regex NotificationRegex();

    [GeneratedRegex("<task-id>((?s:.*?))</task-id>", RegexOptions.CultureInvariant)]
    private static partial Regex TaskIdRegex();

    [GeneratedRegex("<status>((?s:.*?))</status>", RegexOptions.CultureInvariant)]
    private static partial Regex StatusRegex();

    [GeneratedRegex("<event>((?s:.*?))</event>", RegexOptions.CultureInvariant)]
    private static partial Regex EventRegex();

    public static HookOutcome Run(GateContext c)
    {
        var sessionId = c.Input.NonEmpty("session_id");
        if (sessionId is null)
        {
            return HookEmit.Silent;
        }

        var peers = c.Services.Hierarchy.Peers;
        var hookEvent = c.Input.Text("hook_event_name");
        var agentId = c.Input.NonEmpty("agent_id");
        var session = JsonValue.FromString(sessionId);

        if (hookEvent == "PostToolUse")
        {
            if (c.ToolName() == "TaskStop")
            {
                OnTaskStop(c, session);
            }
            else
            {
                var taskId = BackgroundTaskId(c);
                if (taskId is not null && !c.Services.Config.IsSubagent(c.Input) && (PeerLatestFor.Of(c).HasPending(session) || IsMemberSession(c, session)))
                {
                    peers.AppendSubagentRecord(session, taskId, "started", JsonValue.FromString("background"));
                }
            }
        }
        else if (hookEvent == "UserPromptSubmit")
        {
            if (c.Input.Text("prompt") is { } prompt && prompt.Contains("<task-notification>", StringComparison.Ordinal))
            {
                var ended = EndedTaskIds(prompt);
                if (ended.Count > 0)
                {
                    CloseStarted(c, session, ended.Select(JsonValue.FromString).ToList());
                }
            }
        }
        else if (agentId is not null)
        {
            if (hookEvent == "SubagentStart")
            {
                // An owning Orchestrator's subagent is recorded too, for the Graph to draw (ah2-201).
                if (PeerLatestFor.Of(c).HasPending(session) || IsMemberSession(c, session) || Lifecycle.OrchestratorIdentity.OwnsTeam(c, c.Cwd()))
                {
                    var agentType = c.Input.NonEmpty("agent_type");
                    peers.AppendSubagentRecord(session, agentId, "started", JsonValue.FromString("subagent"), agentType is null ? (JsonValue?)null : JsonValue.FromString(agentType));
                }
            }
            else if (hookEvent == "SubagentStop")
            {
                CloseStarted(c, session, [agentId]);
            }
        }

        return HookEmit.Silent;
    }

    /// <summary>A session the activity recorder keeps its own activity for, other than the Orchestrator's.</summary>
    private static bool IsMemberSession(GateContext c, JsonValue session) =>
        ActivityRecorder.InMemberBranch(c.Services, c.Services.Advisory?.Context, c.Services.Config.HierarchyDir(c.Cwd()), session, c.Services.Process.ParentPid, out _)
        && !(c.Services.Config.ReadSessionRole(session) is { IsText: true } role && role.AsString() == "orchestrator");

    /// <summary>Appends <c>stopped</c> for each id whose latest record of this session is <c>started</c>.</summary>
    private static void CloseStarted(GateContext c, JsonValue session, IReadOnlyList<JsonValue> ids)
    {
        var peers = c.Services.Hierarchy.Peers;
        var latest = peers.LatestSubagentRecords(session);
        foreach (var id in ids)
        {
            if (LatestOf(latest, id) is { } row && row.Member("status") is { IsText: true } status && status.AsString() == "started")
            {
                peers.AppendSubagentRecord(session, id, "stopped", row.Member("kind"));
            }
        }
    }

    private static JsonValue? LatestOf(JsonArray latest, JsonValue id)
    {
        foreach (var row in latest)
        {
            if (row.Member("agent_id") is { } rowId && rowId.IsText && id.IsText && rowId.AsString() == id.AsString())
            {
                return row;
            }
        }

        return null;
    }

    private static void OnTaskStop(GateContext c, JsonValue session)
    {
        var request = c.ToolInput();
        var response = c.Input.ObjectOrEmpty("tool_response");
        var candidates = new List<string>();
        foreach (var id in new[] { response.Get("task_id"), request.Get("task_id") })
        {
            if (id is { IsText: true } t && t.AsString().Length > 0 && !candidates.Contains(t.AsString()))
            {
                candidates.Add(t.AsString());
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        var peers = c.Services.Hierarchy.Peers;
        var latest = peers.LatestSubagentRecords(session);
        var known = candidates.Where(id => LatestOf(latest, JsonValue.FromString(id)) is not null).ToList();
        if (known.Count > 0)
        {
            CloseStarted(c, session, known.Select(JsonValue.FromString).ToList());
            return;
        }

        var resolved = response.Get("task_id") is { IsText: true } rt && rt.AsString().Length > 0;
        var maybeAgent = response.Get("task_type") is not { IsText: true } taskType || taskType.AsString() == "local_agent";
        if (!resolved && maybeAgent)
        {
            var every = new List<JsonValue>();
            foreach (var row in latest)
            {
                if (row.Member("agent_id") is { } rowId)
                {
                    every.Add(rowId);
                }
            }

            CloseStarted(c, session, every);
        }
    }

    private static string? BackgroundTaskId(GateContext c)
    {
        var request = c.ToolInput();
        var response = c.Input.ObjectOrEmpty("tool_response");
        JsonValue? id = null;
        if (c.ToolName() == "Bash")
        {
            id = request.Get("run_in_background") is { IsBool: true } flag && flag.AsBool() ? response.Get("backgroundTaskId") : null;
        }
        else if (c.ToolName() == "Monitor")
        {
            id = response.Get("taskId");
        }

        return id is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
    }

    private static List<string> EndedTaskIds(string prompt)
    {
        var ended = new List<string>();
        foreach (Match m in NotificationRegex().Matches(prompt))
        {
            var block = m.Groups[1].Value;
            var idMatch = TaskIdRegex().Match(block);
            var id = idMatch.Success ? JsText.Trim(idMatch.Groups[1].Value) : null;
            if (string.IsNullOrEmpty(id))
            {
                continue;
            }

            var statusMatch = StatusRegex().Match(block);
            var status = statusMatch.Success ? JsText.Trim(statusMatch.Groups[1].Value) : null;
            var eventMatch = EventRegex().Match(block);
            var eventText = eventMatch.Success ? eventMatch.Groups[1].Value : null;
            var monitorEnded = eventText is not null
                && (eventText.StartsWith("[Monitor timed out", StringComparison.Ordinal) || eventText.StartsWith("[Monitor expired", StringComparison.Ordinal));
            if (status is "completed" or "failed" or "killed" || monitorEnded)
            {
                if (!ended.Contains(id))
                {
                    ended.Add(id);
                }
            }
        }

        return ended;
    }
}
