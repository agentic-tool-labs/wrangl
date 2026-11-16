using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// Usage collector fired on SubagentStop: sums the per-turn <c>message.usage</c> of the subagent's transcript and appends one
/// line to the usage log. A <c>found: false</c> line is deliberate telemetry. Collects whenever the hooks are loaded, whatever
/// the hierarchy's on-off flag says. Never blocks, never writes stdout.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class SubagentStopUsage
{
    public static HookOutcome Run(GateContext c)
    {
        var agentId = c.Input.Text("agent_id") ?? string.Empty;
        if (agentId.Length == 0)
        {
            return HookEmit.Silent;
        }

        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var transcriptPath = c.Input.Text("transcript_path") ?? string.Empty;
        var projectDir = transcriptPath.Length > 0 ? NodePath.Dirname(transcriptPath) : string.Empty;
        var usage = HierarchyModule.CreateUsage(s.Files, s.Environment);

        var rec = new JsonObject
        {
            ["ts"] = Timestamps.ToIsoString(s.Clock.UtcNow),
            ["session_id"] = sessionId,
            ["agent_id"] = agentId,
            ["agent_type"] = c.Input.Text("agent_type") ?? string.Empty,
            ["project_dir"] = projectDir,
            ["found"] = false,
            ["model"] = string.Empty,
        };
        var zero = usage.ZeroUsage();
        foreach (var key in zero.Keys)
        {
            rec[key] = zero[key];
        }

        if (projectDir.Length > 0 && sessionId.Length > 0 && IsSingleComponent(sessionId) && IsSingleComponent(agentId))
        {
            var sums = usage.SumTranscript(NodePath.Join(projectDir, sessionId, "subagents", $"agent-{agentId}.jsonl"));
            if (sums is { IsMap: true } found)
            {
                rec["found"] = true;
                foreach (var key in found.AsObject().Keys)
                {
                    rec[key] = found.AsObject()[key];
                }
            }
        }

        s.Hierarchy.StateCalls.AppendUsage(rec);
        return HookEmit.Silent;
    }

    /// <summary>A payload id that stays one directory level once joined into the transcript path.</summary>
    private static bool IsSingleComponent(string name) => name is not ("." or "..") && name.IndexOfAny(['/', '\0']) < 0;
}
