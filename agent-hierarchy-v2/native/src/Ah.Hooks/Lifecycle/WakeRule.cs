using System.Text;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Hooks;

/// <summary>
/// The Stop rule that keeps an Orchestrator from going idle with work in flight and nothing to wake it: the overdue timer is always
/// required, and the ahd waiter whenever the daemon is running. It reads files and the process table only.
/// </summary>
internal static class WakeRule
{
    /// <summary>
    /// The block text naming the missing commands, or null when nothing is in flight or both wake sources run. A session the doorbell
    /// serves (<paramref name="doorbellServes"/>: Serves and a fresh pull) and a held or quit team need neither (fewer-turns S4.1).
    /// </summary>
    internal static string? Notice(GateServices s, string dir, string team, string sessionId, string cwd, bool doorbellServes = false)
    {
        try
        {
            if (sessionId.Length == 0 || doorbellServes || s.Roster.HoldOf(dir, team) is not null
                || Ah.Roster.RosterModule.ChainDownActive(s.Hierarchy.Core.PeerRecordsShared(), sessionId)
                || WorkInFlight(s, dir, team) is not { Count: > 0 } jobs)
            {
                return null;
            }

            var cli = s.Config.CliCommand(CliTool.Roster);
            var missing = new List<string>();
            var notRunning = new List<string>();
            if (!Live(s, $"{sessionId}.overdue.json"))
            {
                missing.Add($"{cli} overdue --cwd {cwd}");
                notRunning.Add("the overdue timer");
            }

            if (DaemonUp(s) && !Live(s, $"{sessionId}.json"))
            {
                missing.Add($"{cli} wake --cwd {cwd}");
                notRunning.Add("the ahd waiter");
            }

            // A job set blocks a Stop once. It is asked about again when the set changes, or when both wake sources were seen
            // running and one has stopped since.
            var live = 0;
            var asked = new HashSet<string>(StringComparer.Ordinal);
            _ = s.Hierarchy.Roster.HasGate(dir, g =>
            {
                if (g.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId && g.Member("type") is { IsText: true } t)
                {
                    if (t.AsString() == "wake-live")
                    {
                        live++;
                    }
                    else if (t.AsString() == "wake-nudge" && g.Member("key") is { IsText: true } k)
                    {
                        asked.Add(k.AsString());
                    }
                }

                return false;
            });
            var key = string.Join(',', jobs.Order(StringComparer.Ordinal)) + "|" + string.Join(',', notRunning) + "|" + live.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (missing.Count == 0)
            {
                if (asked.Any(a => a.EndsWith("|" + live.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)))
                {
                    s.Hierarchy.StateCalls.AppendGate(dir, new JsonObject { ["type"] = "wake-live", ["session_id"] = sessionId });
                }

                return null;
            }

            if (asked.Contains(key))
            {
                return null;
            }

            s.Hierarchy.StateCalls.AppendGate(dir, new JsonObject { ["type"] = "wake-nudge", ["session_id"] = sessionId, ["key"] = key });
            return (s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed")).Directive.StopWake(missing, notRunning);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>The ids of the work in flight (the open run anchor and the Orchestrator's open requests); empty when there is none.</summary>
    private static List<string> WorkInFlight(GateServices s, string dir, string team)
    {
        var jobs = new List<string>();
        if (s.Hierarchy.Decisions.OpenRunAnchor(dir, team).Get("id") is { IsText: true } anchor)
        {
            jobs.Add("anchor:" + anchor.AsString());
        }

        foreach (var exchange in s.Hierarchy.Messages.OpenExchanges(dir, team))
        {
            if (exchange.Member("request")?.Member("path") is { IsText: true } path
                && s.Hierarchy.Core.ReadMsgFile(path.AsString()).Member("fm") is { IsMap: true } fm
                && fm.AsObject().Get("from") is { IsText: true } from && from.AsString() is "orchestrator" or "sub-orchestrator"
                && fm.AsObject().Get("type") is { IsText: true } type && type.AsString() == "request")
            {
                var id = fm.AsObject().Get("id") is { IsText: true } idText ? idText.AsString() : null;

                // board-derive § C3 r3: an unarmed plain send is not work in flight
                if (fm.AsObject().Get("reason") is { IsText: true } reason && reason.AsString() == HierarchyModule.PlainSendReason
                    && (id is null || s.Hierarchy.Core.PeerRecordsWhere(new PeerQuery("request_id", id, new HashSet<string>(StringComparer.Ordinal) { HierarchyModule.PlainSendArmedRow })).Count == 0))
                {
                    continue;
                }

                jobs.Add(id ?? path.AsString());
            }
        }

        return jobs;
    }

    /// <summary>A wake file counts when the pid it names is running and is not a reused one.</summary>
    internal static bool Live(GateServices s, string name)
    {
        var path = Path.Combine(s.Environment.AhHome, "wake", name);
        if (s.Files.Stat(path) is null || JsonText.Parse(Encoding.UTF8.GetBytes(s.Files.ReadText(path))) is not { IsMap: true } doc)
        {
            return false;
        }

        var file = doc.AsObject();
        return file.Get("pid") is { IsNumber: true } pid && s.Hierarchy.Core.PidAlive(pid)
            && !(s.Hierarchy.Core.Liveness?.PidReused((int)pid.AsNumber(), file.Get("pid_start") is { IsText: true } start ? start.AsString() : null) ?? false);
    }

    internal static bool DaemonUp(GateServices s)
    {
        var path = Path.Combine(s.Environment.AhHome, "run", "ahd.json");
        return s.Files.Stat(path) is not null
            && JsonText.Parse(Encoding.UTF8.GetBytes(s.Files.ReadText(path))) is { IsMap: true } info
            && info.AsObject().Get("pid") is { IsNumber: true } pid && s.Hierarchy.Core.PidAlive(pid);
    }
}
