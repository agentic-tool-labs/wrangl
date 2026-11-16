using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// An AskUserQuestion whose <c>metadata.source</c> names an item (<c>item=&lt;stream/id&gt;</c>): asked (PreToolUse), the item waits on the user, with the question as its
/// note; answered (PostToolUse), an item waiting on the user goes back. A question with no such tag moves nothing. Never fails the hook.
/// </summary>
internal static class AskItemMove
{
    /// <summary>The question was answered: an item waiting on the user goes back.</summary>
    internal static void Run(GateContext c) => Move(c, HierarchyModule.ChecklistEventAnswered);

    /// <summary>The question is being put: the item waits on the user.</summary>
    internal static void Asked(GateContext c) => Move(c, HierarchyModule.ChecklistEventAsked);

    private static void Move(GateContext c, string kind)
    {
        try
        {
            if (c.ToolName() != "AskUserQuestion" || c.Services.Advisory is not { } advisory
                || c.ToolInput().Get("metadata")?.Member("source") is not { IsText: true } source
                || HierarchyModule.ChecklistAskItem(source.AsString()) is not { } item)
            {
                return;
            }

            var question = c.ToolInput().Get("questions") is { IsArray: true } qs && qs.AsArray().Count > 0 && qs.AsArray()[0].Member("question") is { IsText: true } q ? q.AsString() : null;

            var cwd = c.Cwd();
            var session = c.Input.NonEmpty("session_id");
            var s = c.Services;
            var pool = ProgressPool.Of(s, cwd);
            foreach (var team in advisory.Callers.SessionDeciderBoxes(cwd, session).Select(static b => b.Team).Distinct(StringComparer.Ordinal))
            {
                var ev = new ChecklistEvent(kind, HierarchyModule.ChecklistEventTime(s.Clock.UtcNow), c.Input.NonEmpty("tool_use_id") ?? string.Empty, HierarchyModule.OrchestratorAddress(team))
                {
                    ItemId = item,
                    Text = question,
                };
                _ = HierarchyModule.ChecklistOnEvent(s.Hierarchy.Progress, pool, team, ev, ev.Actor, session);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.LogError(e);
        }
    }
}
