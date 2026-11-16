using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>What one scan of a pool list may read: each read is a seam, so a test can count them.</summary>
/// <param name="ImplementRole">Whether a role is implement-class and not qa.</param>
/// <param name="TimeOf">A response's seal time (its mtime in files mode), in epoch milliseconds, or null.</param>
/// <param name="RequestFm">An exchange's request front matter, or null.</param>
/// <param name="TextOf">A response's text, or null when it cannot be read.</param>
/// <param name="Reviewed">Whether a review exchange exists for (request id, member address, team).</param>
internal sealed record DoneScanReads(
    Func<string, bool> ImplementRole,
    Func<string, double?> TimeOf,
    Func<JsonObject, JsonObject?> RequestFm,
    Func<string, string?> TextOf,
    Func<string, string, string?, bool> Reviewed);

/// <summary>
/// The host half of b487's <c>unreviewed-done</c>: the DONE reports still waiting on a review, over the exchanges of the owned teams' pools.
/// A response path, once settled, is never read again: in a pool seen for the first time, every response filed before
/// <paramref name="startedMs"/>; and any response that is past the hour, not an owned Orchestrator's request, reviewed, or already
/// alarmed. A response that is not DONE is not settled: its text is read again only when its seal time moves (a <c>--part</c> or
/// BLOCKED fill followed by the DONE one).
/// </summary>
/// <param name="startedMs">When this daemon started, in epoch milliseconds.</param>
internal sealed class DoneReportScan(double startedMs)
{
    private readonly Dictionary<string, HashSet<string>> _settledByPool = new(StringComparer.Ordinal);

    /// <summary>For each unsettled response whose text was read: its seal time then, and whether it was DONE.</summary>
    private readonly Dictionary<string, (double At, bool Done)> _readAt = new(StringComparer.Ordinal);

    internal IReadOnlyList<DoneReport> Scan(
        IReadOnlyList<string> pools, IReadOnlyList<JsonObject> exchanges, IReadOnlySet<string> owned, IReadOnlySet<string> alarmed, double nowMs, DoneScanReads reads)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(owned);
        ArgumentNullException.ThrowIfNull(alarmed);
        ArgumentNullException.ThrowIfNull(reads);
        var reports = new List<DoneReport>();
        lock (_settledByPool)
        {
            var firstLook = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pool in pools)
            {
                if (_settledByPool.TryAdd(pool, new HashSet<string>(StringComparer.Ordinal)))
                {
                    _ = firstLook.Add(pool);
                }
            }

            foreach (var e in exchanges)
            {
                if (Str(e, "id") is not { } id || Str(e, "to") is not { } to
                    || (e.TryGetValue("response", out var r) && r.Kind == JsonKind.Map ? Str(r.AsObject(), "path") : null) is not { } path)
                {
                    continue;
                }

                // <pool>/msgs/<file>: a response from a pool outside the list is a pool seen for the first time too
                var pool = NodePath.Dirname(NodePath.Dirname(path));
                if (!_settledByPool.TryGetValue(pool, out var settled))
                {
                    settled = _settledByPool[pool] = new HashSet<string>(StringComparer.Ordinal);
                    _ = firstLook.Add(pool);
                }

                if (settled.Contains(path))
                {
                    continue;
                }

                void Settle()
                {
                    _ = settled.Add(path);
                    _ = _readAt.Remove(path);
                }

                if (!reads.ImplementRole(to) || alarmed.Contains(id))
                {
                    Settle();
                    continue;
                }

                if (reads.TimeOf(path) is not { } reportedMs)
                {
                    continue;
                }

                if ((firstLook.Contains(pool) && reportedMs < startedMs) || nowMs - reportedMs > WatchModule.UnreviewedDoneWindowMs
                    || reads.RequestFm(e) is not { } fm || Str(fm, "from") != "orchestrator" || !owned.Contains(Str(fm, "team") ?? string.Empty))
                {
                    Settle();
                    continue;
                }

                if (!(_readAt.TryGetValue(path, out var read) && read.At == reportedMs))
                {
                    if (reads.TextOf(path) is not { } text)
                    {
                        continue;
                    }

                    read = (reportedMs, HierarchyModule.IsDoneReport(text));
                    _readAt[path] = read;
                }

                if (!read.Done)
                {
                    continue;
                }

                var team = Str(fm, "team");
                var name = Str(fm, "to_name") is { Length: > 0 } n && n != "null" ? n : to;
                var member = team is { Length: > 0 } ? $"{name}@{team}" : name;
                if (reads.Reviewed(id, member, team))
                {
                    Settle();
                    continue;
                }

                reports.Add(new DoneReport(id, Str(fm, "title"), member, team, reportedMs, false));
            }
        }

        return reports;
    }

    /// <summary>
    /// Whether request <paramref name="id"/> has a review exchange: a request to a reviewer-class role whose parent is it, or a link with
    /// <paramref name="member"/> and a reviewer-class member whose task is it or one of its children.
    /// </summary>
    internal static bool HasReview(
        string id, string member, IReadOnlyList<JsonObject> exchanges, Func<JsonObject, JsonObject?> requestFm, Func<string, string?> classOf,
        IReadOnlyList<JsonObject> links, Func<string, string?> memberClass)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(requestFm);
        ArgumentNullException.ThrowIfNull(classOf);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(memberClass);
        var family = new HashSet<string>(StringComparer.Ordinal) { id };
        foreach (var e in exchanges)
        {
            if (Str(e, "id") is { } child && child != id && requestFm(e) is { } fm && Str(fm, "parent") == id)
            {
                if (Str(e, "to") is { } role && classOf(role) == "review")
                {
                    return true;
                }

                _ = family.Add(child);
            }
        }

        foreach (var link in links)
        {
            var on = new List<string>();
            if (Str(link, "requester") is { } requester)
            {
                on.Add(requester);
            }

            if (link.TryGetValue("peers", out var peers) && peers.IsArray)
            {
                on.AddRange(peers.AsArray().Where(static p => p.IsText).Select(static p => p.AsString()));
            }

            if (Str(link, "task") is { } task && family.Contains(task) && on.Contains(member) && on.Any(a => a != member && memberClass(a) == "review"))
            {
                return true;
            }
        }

        return false;
    }

    private static string? Str(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind == JsonKind.Text ? v.AsString() : null;
}
