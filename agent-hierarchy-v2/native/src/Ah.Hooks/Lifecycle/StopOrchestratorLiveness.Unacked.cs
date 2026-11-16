using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Lifecycle;

internal static partial class StopOrchestratorLiveness
{
    /// <summary>The most unacknowledged responses a listing names; the rest are counted.</summary>
    internal const int MaxUnackedShown = 5;

    /// <summary>The most request ids the older-responses line names.</summary>
    internal const int MaxOlderNamed = 10;

    /// <summary>How far back a response counts when the install stamp records no usable build time.</summary>
    internal const int UnstampedWindowHours = 24;

    /// <summary>
    /// The instant before which a filed response is history: the install stamp's <c>built_at</c>, so a fresh install does not list every
    /// response filed before it. A missing or unreadable stamp, or one with no parsable <c>built_at</c>, falls back to
    /// <see cref="UnstampedWindowHours"/> before <paramref name="nowMs"/>.
    /// </summary>
    internal static double ListingBaselineMs(GateServices s, double nowMs)
    {
        try
        {
            var file = Ah.Core.Text.NodePath.Join(s.Environment.AhHome, "install.json");
            if (s.Files.Exists(file) && JsonText.Parse(s.Files.ReadText(file)).Member("built_at") is { IsText: true } built
                && Timestamps.TryParseMs(built.AsString(), out var builtMs))
            {
                return builtMs;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            // an unreadable stamp is the same as none
        }

        return nowMs - (UnstampedWindowHours * 3600.0 * 1000);
    }

    /// <summary>
    /// A listing of the responses filed to this orchestrator session, for requests it dispatched, that it has not acknowledged with
    /// <c>ah msg ack &lt;request id&gt;</c>; null when there are none or the session is not an enabled orchestrator. A response
    /// the harness dropped on the way is still filed, so this keeps naming it until it is acknowledged.
    /// </summary>
    internal static string? UnackedResponsesLine(GateContext c)
    {
        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        if (sessionId.Length == 0 || s.Config.IsSubagent(c.Input) || s.Config.ResolveHierarchyRole(c.Input).Role is { } role && role != "orchestrator")
        {
            return null;
        }

        var cwd = c.Cwd();
        var resolved = s.Config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionId));
        if (resolved.Get("enabled") is not { IsTruthy: true })
        {
            return null;
        }

        try
        {
            var now = (double)s.Clock.UtcNow.ToUnixTimeMilliseconds();
            var pools = s.Hierarchy.Messages.PoolDirs(s.Config.HierarchyDir(cwd), TeamNames(resolved));
            var exchanges = HierarchyModule.CreateExchangeIndex(s.Hierarchy.Core, pools);
            var items = new List<string>();
            var older = new List<string>();
            var told = s.Hierarchy.Peers.ReportShownFor(sessionId);
            var baseline = ListingBaselineMs(s, now);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var acked = AckedOf(s, sessionId);
            foreach (var row in DispatchObligations.Live(s, sessionId))
            {
                if (row.Member("path") is not { IsText: true } path || path.AsString().Length == 0
                    || row.Member("request_id") is not { IsText: true } rid || !seen.Add(rid.AsString())
                    || acked(rid))
                {
                    continue;
                }

                var exchange = ExchangeAt(exchanges, path.AsString(), rid.AsString());
                if (exchange.IsNull || exchange.Member("open") is { IsTruthy: true })
                {
                    continue;
                }

                var responsePath = StringOf(exchange.Member("response")?.Member("path"));
                if (!s.Hierarchy.Messages.ResponseLanded(responsePath, now) || s.Advisory?.MailboxHooks.WrittenByOrchestrator(responsePath) == true)
                {
                    continue;
                }

                if (s.Hierarchy.Core.MsgTimeMs(responsePath) is { } written && written < baseline)
                {
                    if (!told(rid))
                    {
                        older.Add(rid.AsString());
                    }

                    continue;
                }

                var response = s.Hierarchy.Core.ReadMsgFile(responsePath);
                var from = StringOf(response.Member("fm")?.Member("from"));
                items.Add($"{rid.AsString()} from {(from.Length > 0 ? from : StringOf(exchange.Member("to")))} {StringOf(exchange.Member("slug"))}: {FirstLine(StringOf(response.Member("text")))} ({responsePath})");
            }

            string? olderLine = null;
            if (older.Count > 0)
            {
                var named = string.Join(", ", older.Take(MaxOlderNamed));
                var rest = older.Count > MaxOlderNamed ? $" +{older.Count - MaxOlderNamed} more" : string.Empty;
                olderLine = $"ah: {older.Count} older responses not listed; clear them with `ah msg ack <request id>` ({named}{rest}).";

                // Said once: marking them shown keeps the next prompt from repeating the line.
                foreach (var id in older)
                {
                    s.Hierarchy.Peers.AppendReportRecord("surfaced", sessionId, id);
                }
            }

            if (items.Count == 0)
            {
                return olderLine;
            }

            var more = items.Count > MaxUnackedShown ? $" +{items.Count - MaxUnackedShown} more" : string.Empty;
            var listing = $"ah: responses filed to you, not yet acknowledged ({items.Count}); read each, then `ah msg ack <request id>`:\n- {string.Join("\n- ", items.Take(MaxUnackedShown))}{more}";
            return olderLine is null ? listing : $"{listing}\n{olderLine}";
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            c.LogError(e);
            return null;
        }
    }

    /// <summary>The first line of the body after the frontmatter that is neither blank nor a heading, capped so a listing stays small.</summary>
    private static string FirstLine(string text)
    {
        var dashes = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line == "---")
            {
                dashes++;
                continue;
            }

            if (dashes >= 2 && line.Length > 0 && !line.StartsWith('#'))
            {
                return line.Length > 100 ? line[..100] : line;
            }
        }

        return string.Empty;
    }
}
