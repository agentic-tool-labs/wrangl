using System.Security.Cryptography;
using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>
/// The daemon's board audit mail (b284): the rows <c>ah roster checklist audit</c> finds disagreeing, as one advisory event to the team's
/// Orchestrator. Nothing is changed; an empty audit sends nothing, and a set the box already holds is not sent again.
/// </summary>
internal static class BoardAudit
{
    /// <summary>The event name of the mail.</summary>
    public const string EventName = "board-audit";

    /// <summary>How often an audit runs with no ship in between.</summary>
    public static readonly TimeSpan Daily = TimeSpan.FromDays(1);

    /// <summary>
    /// Mails <paramref name="findings"/> to <paramref name="team"/>'s Orchestrator through <paramref name="send"/> unless they are empty, the team has no
    /// Orchestrator, or <paramref name="held"/> (the box's entries) already holds an audit mail of the same content. Returns whether it sent.
    /// </summary>
    public static bool Mail(StallTeam team, IReadOnlyList<ChecklistAuditFinding> findings, IEnumerable<JsonValue> held, Action<WatchMail> send)
    {
        ArgumentNullException.ThrowIfNull(team);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(send);
        if (findings.Count == 0 || team.OrchestratorAddress is not { } to)
        {
            return false;
        }

        var lines = findings.Select(static f => $"{f.Stream}/{f.Id}  stored {f.Stored}  expected {f.Expected}  why {f.Why}{(f.PreCause ? "  (pre-cause history: unverifiable)" : string.Empty)}").ToList();
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines.Order(StringComparer.Ordinal)))));
        if (held.Any(e => e.Member("event") is { IsText: true } ev && ev.AsString() == EventName && e.Member("data")?.Member("digest") is { IsText: true } d && d.AsString() == digest))
        {
            return false;
        }

        var rows = new JsonArray();
        foreach (var f in findings)
        {
            rows.Add(new JsonObject { ["id"] = f.Id, ["stream"] = f.Stream, ["stored"] = f.Stored, ["expected"] = f.Expected, ["why"] = f.Why, ["pre_cause"] = f.PreCause });
        }

        var text = $"board audit: {team.Name}: {findings.Count} checklist row(s) disagree with their facts (advisory; nothing was changed):\n" + string.Join('\n', lines);
        send(new WatchMail(to, team.HierarchyDir, team.Cwd, EventName, text, new JsonObject { ["team"] = team.Name, ["digest"] = digest, ["rows"] = rows }, team.OrchestratorSession));
        return true;
    }
}
