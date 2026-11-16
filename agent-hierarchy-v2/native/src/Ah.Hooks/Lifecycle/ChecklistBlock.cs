using System.Text;
using Ah.Config;
using Ah.Hierarchy;

namespace Ah.Hooks.Lifecycle;

/// <summary>
/// The Orchestrator's checklist as additional context at the start of a session, so a compaction or a resume does not lose what is
/// outstanding. It reads the plan files only, so it works with the daemon down.
/// </summary>
internal static class ChecklistBlock
{
    internal const int MaxBytes = 1500;

    /// <summary>The block for the session, or null when it owns no team or its checklist is empty.</summary>
    internal static string? Of(GateServices s, AdvisoryServices advisory, string sessionId, string cwd)
    {
        if (sessionId.Length == 0 || OwnTeam(advisory, sessionId) is not { } team)
        {
            return null;
        }

        var entries = HierarchyModule.ChecklistEntries(s.Hierarchy.Progress, ProgressPool.Of(s, cwd), HierarchyModule.ProgressTeamOf(team));
        if (entries.Count == 0)
        {
            return null;
        }

        var lines = HierarchyModule.ChecklistShow(entries, [], s.Clock.UtcNow.ToUnixTimeMilliseconds(), false);
        if (lines.Count == 0)
        {
            return null;
        }


        var cli = s.Config.CliCommand(CliTool.Roster);
        var block = new StringBuilder($"ah: your checklist, as `{cli} checklist show` prints it:");
        var shown = 0;
        foreach (var line in lines)
        {
            var footer = $"\n… {lines.Count - shown - 1} more: run `{cli} checklist show`";
            var isLast = shown == lines.Count - 1;
            if (Encoding.UTF8.GetByteCount(block.ToString()) + Encoding.UTF8.GetByteCount("\n" + line) + (isLast ? 0 : Encoding.UTF8.GetByteCount(footer)) > MaxBytes)
            {
                break;
            }

            _ = block.Append('\n').Append(line);
            shown++;
        }

        if (shown < lines.Count)
        {
            _ = block.Append($"\n… {lines.Count - shown} more: run `{cli} checklist show`");
        }

        return block.ToString();
    }

    private static string? OwnTeam(AdvisoryServices advisory, string sessionId)
    {
        var registration = advisory.Mailbox.ReadRegistration(sessionId);
        if (registration is null)
        {
            return null;
        }

        return advisory.Mailbox.RegistrationBoxes(Ah.Core.Json.JsonValue.FromObject(registration))
            .Select(static b => b.Address)
            .FirstOrDefault(static a => a.StartsWith("orchestrator@", StringComparison.Ordinal))?["orchestrator@".Length..];
    }
}
