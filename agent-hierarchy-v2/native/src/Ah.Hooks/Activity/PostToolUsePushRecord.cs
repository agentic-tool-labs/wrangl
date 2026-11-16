using Ah.Abstractions;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// PostToolUse (PushNotification): once the Orchestrator has pushed, every question of its session that is parked, open and not yet pushed
/// gets a <c>pushed</c> line in the parked-question store. Runs for the session a team file records as its live Orchestrator, for its own
/// calls only; every other session and every subagent gets nothing. Never fails the tool.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static class PostToolUsePushRecord
{
    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (c.ToolName() != "PushNotification" || s.Advisory is not { } advisory || s.Config.IsSubagent(c.Input) || c.Input.NonEmpty("session_id") is not { } sessionId)
        {
            return HookEmit.Silent;
        }

        try
        {
            var at = Timestamps.ToIsoString(s.Clock.UtcNow);
            var toolUseId = c.Input.NonEmpty("tool_use_id");
            foreach (var dir in advisory.Callers.SessionDeciderBoxes(c.Cwd(), sessionId).Select(b => b.Dir).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                foreach (var q in ParkedQuestions.Fold(AskStore.ReadStore(s, dir)).Where(q => q.Open && !q.Pushed && q.SessionId == sessionId))
                {
                    s.Hierarchy.StateCalls.AppendParkedQuestion(dir, ParkedQuestions.PushedLine(q.Id, at), new StateCallOptions(OperationId: $"pushed:{q.Id}:{toolUseId}", Hook: true));
                }
            }
        }
        catch (Exception e)
        {
            c.LogError(e);
        }

        return HookEmit.Silent;
    }
}
