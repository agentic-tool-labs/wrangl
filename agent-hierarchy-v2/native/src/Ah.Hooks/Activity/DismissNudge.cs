using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Activity;

/// <summary>
/// The dismiss nudge: for each member of a team this session orchestrates that is idle and that <c>dismiss</c>'s release check
/// would close now, one line naming the close, once per idle spell (a <c>dismiss-nudge</c> peer row keyed on its <c>state_since</c>).
/// A held member is checked again at most every five minutes in the same spell, so the git calls run only for idle members. Never
/// blocks; fails silent.
/// </summary>
internal static class DismissNudge
{
    internal const string RowType = "dismiss-nudge";

    /// <summary>How long a member the release check held waits before it is checked again in the same idle spell.</summary>
    private const double RecheckMs = 5 * 60 * 1000;

    internal static List<string> Lines(GateContext c, AdvisoryServices advisory, string sessionId)
    {
        var s = c.Services;
        var lines = new List<string>();
        if (s.ReadyToDismiss is not { } ready || sessionId.Length == 0)
        {
            return lines;
        }

        try
        {
            var cwd = c.Cwd();
            var dir = s.Config.HierarchyDir(cwd);
            // per (member, idle spell): fired once → never again; else at most one check per RecheckMs
            var nowMs = s.Clock.UtcNow.ToUnixTimeMilliseconds();
            var skip = s.Hierarchy.Core.SessionRows(sessionId, RowType)
                .Where(r => r.Member("type") is { IsText: true } t && t.AsString() == RowType
                    && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                    && (r.Member("fired") is { IsTruthy: true } || (r.Member("checked_ms") is { IsNumber: true } at && nowMs - at.AsNumber() < RecheckMs)))
                .Select(r => $"{Text(r.Member("name"))}\0{Text(r.Member("key"))}")
                .ToHashSet(StringComparer.Ordinal);
            var teams = advisory.Mailbox.ReadRegistration(sessionId) is { } registration
                ? advisory.Mailbox.RegistrationBoxes(registration).Select(static b => b.Address)
                    .Where(static a => a.StartsWith("orchestrator@", StringComparison.Ordinal)).Select(static a => a["orchestrator@".Length..]).Distinct(StringComparer.Ordinal)
                : [];
            foreach (var team in teams)
            {
                var spells = new Dictionary<string, string>(StringComparer.Ordinal);
                if (s.Roster.ReadTeamRecord(dir, team).Member("members") is { IsArray: true } members)
                {
                    foreach (var m in members.AsArray())
                    {
                        var name = Text(m.Member("name"));
                        var session = Text(m.Member("session_id"));
                        var record = session.Length > 0 ? s.Hierarchy.Status.ReadActivityRecord(dir, session) : JsonValue.Null;
                        if (name.Length > 0 && record.IsMap && Text(record.Member("activity")) == "idle"
                            && Text(record.Member("state_since")) is { Length: > 0 } since && !skip.Contains($"{name}\0{since}"))
                        {
                            spells[name] = since;
                        }
                    }
                }

                if (spells.Count == 0)
                {
                    continue;
                }

                IReadOnlyList<string> closable = [];
                try
                {
                    closable = ready(dir, cwd, team, [.. spells.Keys]);
                }
                finally
                {
                    // a throw counts as a check, so a failing check is not retried and logged on every prompt
                    foreach (var (name, since) in spells)
                    {
                        s.Hierarchy.Peers.RecordPeerRow(new JsonObject { ["type"] = RowType, ["session_id"] = sessionId, ["name"] = name, ["key"] = since, ["checked_ms"] = nowMs, ["fired"] = closable.Contains(name) });
                    }
                }

                lines.AddRange(closable.Where(spells.ContainsKey).Select(name =>
                    $"ah: {name} is idle and clean — dismiss it if its work is signed off: {s.Config.CliCommand(CliTool.Roster)} dismiss {name} --close --cwd {cwd}"));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }

        return lines;
    }

    private static string Text(JsonValue? v) => v is { IsText: true } t ? t.AsString() : string.Empty;
}
