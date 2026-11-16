using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The AH MEMORY block a session that owns a team gets at start, resume, clear and compaction: the digest of the team's ledger.
/// A member or a subagent owns no team and gets nothing. A lane's sub-orchestrator reads its own child team's ledger without the lane lines.
/// </summary>
internal static class MemoryRecall
{
    public static string? Digest(GateContext c, string? source)
    {
        if (source is not ("startup" or "resume" or "clear" or "compact" or SessionStart.OptInSource) || c.Services.Advisory is not { } advisory)
        {
            return null;
        }

        try
        {
            var s = c.Services;
            var cwd = c.Cwd();
            var boxes = advisory.Callers.SessionDeciderBoxes(cwd, c.Input.NonEmpty("session_id"));
            if (boxes.Count == 0)
            {
                return null;
            }

            var pool = ProgressPool.Of(s, cwd);
            var top = !DeclaredSubOrchestrator.Is(c);
            var blocks = boxes.Select(b => b.Team).Distinct(StringComparer.Ordinal)
                .Select(team => s.Hierarchy.Progress.RecallDigest(pool, team, includeLanes: top))
                .OfType<string>()
                .ToList();
            var text = new List<string>();
            var length = 0;
            foreach (var block in blocks)
            {
                length += block.Length + (text.Count > 0 ? "\n\n".Length : 0);
                if (text.Count > 0 && length > IHierarchyProgress.DigestMaxChars)
                {
                    break;
                }

                text.Add(block);
            }

            return text.Count == 0 ? null : string.Join("\n\n", text);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
            return null;
        }
    }
}
