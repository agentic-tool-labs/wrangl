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
/// <param name="Reviewed">Whether a review exchange exists for (request id, member address, the report's scope).</param>
internal sealed record DoneScanReads(
    Func<string, bool> ImplementRole,
    Func<string, double?> TimeOf,
    Func<JsonObject, JsonObject?> RequestFm,
    Func<string, string?> TextOf,
    Func<string, string, ReviewScope, bool> Reviewed);

/// <summary>Where a DONE report sits, for ah2-222's item rule.</summary>
/// <param name="Team">The request's team, or null.</param>
/// <param name="Pool">The pool the report was filed in.</param>
/// <param name="Item">The request's checklist item (<c>stream/id</c>), or null for an unlinked dispatch.</param>
/// <param name="ReportedMs">When the report was filed, in epoch milliseconds.</param>
internal sealed record ReviewScope(string? Team, string Pool, string? Item, double ReportedMs);

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
                if (reads.Reviewed(id, member, new ReviewScope(team, pool, Str(fm, "item"), reportedMs)))
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
    /// Whether request <paramref name="id"/> has a review exchange: a request to a reviewer-class role whose parent chain reaches it, or
    /// reaches the root of its chain and was created after it (ah2-432), or a
    /// link with <paramref name="member"/> and a reviewer-class member whose task is it or one of its children. With a
    /// <paramref name="scope"/> whose item is set (ah2-222): the item is done or dropped, or a reviewer-class request on that item, in the
    /// same pool and team, was created at or after the report.
    /// </summary>
    internal static bool HasReview(
        string id, string member, IReadOnlyList<JsonObject> exchanges, Func<JsonObject, JsonObject?> requestFm, Func<string, string?> classOf,
        IReadOnlyList<JsonObject> links, Func<string, string?> memberClass, ReviewScope? scope = null, Func<string, bool>? itemClosed = null)
    {
        ArgumentNullException.ThrowIfNull(exchanges);
        ArgumentNullException.ThrowIfNull(requestFm);
        ArgumentNullException.ThrowIfNull(classOf);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(memberClass);
        if (scope?.Item is { } closedItem && itemClosed is not null && itemClosed(closedItem))
        {
            return true;
        }

        // created has whole seconds: a review sent in the report's second counts
        var reportedSec = scope is null ? 0 : Math.Floor(scope.ReportedMs / 1000) * 1000;
        var family = new HashSet<string>(StringComparer.Ordinal) { id };
        var parents = new Dictionary<string, string>(StringComparer.Ordinal);
        var reviews = new List<string>();
        var itemOf = new Dictionary<string, string>(StringComparer.Ordinal);
        var createdOf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var e in exchanges)
        {
            if (Str(e, "id") is not { } eid || requestFm(e) is not { } fm)
            {
                continue;
            }

            if (Ah.Core.Time.Timestamps.TryParseMs(Str(fm, "created"), out var made))
            {
                createdOf[eid] = made;
            }

            // ah2-432: the request's own parent, for the walk to its chain's root; "null" is no parent
            if (eid == id)
            {
                if (Str(fm, "parent") is { Length: > 0 } own && own != "null")
                {
                    parents[id] = own;
                }

                continue;
            }

            var review = Str(e, "to") is { } role && classOf(role) == "review";
            if (Str(fm, "item") is { } onItem)
            {
                itemOf[eid] = onItem;
            }

            if (Str(fm, "parent") is { Length: > 0 } parent && parent != "null")
            {
                parents[eid] = parent;
                if (review)
                {
                    reviews.Add(eid);
                }
                else if (parent == id)
                {
                    _ = family.Add(eid);
                }
            }

            if (review && scope is { Item: { } item } && Str(fm, "item") == item && Str(fm, "team") == scope.Team && PoolOf(e) == scope.Pool
                && Ah.Core.Time.Timestamps.TryParseMs(Str(fm, "created"), out var createdMs) && createdMs >= reportedSec)
            {
                return true;
            }
        }

        // ah2-432: the root of the request's chain; a parent cycle or a missing parent ends the walk
        var root = id;
        for (var walked = new HashSet<string>(StringComparer.Ordinal); parents.TryGetValue(root, out var up) && walked.Add(root);)
        {
            root = up;
        }

        var requestCreated = createdOf.TryGetValue(id, out var ownCreated) ? ownCreated : (double?)null;

        // a reviewer-class request whose parent chain reaches the request, or (ah2-432) the request's root when the review was created after it
        foreach (var review in reviews)
        {
            var later = requestCreated is { } since && createdOf.TryGetValue(review, out var reviewCreated) && reviewCreated > since;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var at = review; parents.TryGetValue(at, out var up) && seen.Add(at); at = up)
            {
                if (up == id || (later && up == root))
                {
                    return true;
                }
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

            if (Str(link, "task") is not { } task || !on.Contains(member) || !on.Any(a => a != member && memberClass(a) == "review"))
            {
                continue;
            }

            if (family.Contains(task))
            {
                return true;
            }

            // ah2-312 M2': a later round of a link on the same item, ended at or after the report; a row without the field never counts here
            if (scope is { Item: { } linkItem } && itemOf.TryGetValue(task, out var taskItem) && taskItem == linkItem
                && Ah.Core.Time.Timestamps.TryParseMs(Str(link, "round_ended_at"), out var endedMs) && endedMs >= scope.ReportedMs)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The pool an exchange's request was filed in: <c>&lt;pool&gt;/msgs/&lt;file&gt;</c>.</summary>
    private static string? PoolOf(JsonObject e) =>
        e.TryGetValue("request", out var r) && r.Kind == JsonKind.Map && Str(r.AsObject(), "path") is { } path ? NodePath.Dirname(NodePath.Dirname(path)) : null;

    private static string? Str(JsonObject o, string key) => o.TryGetValue(key, out var v) && v.Kind == JsonKind.Text ? v.AsString() : null;
}
