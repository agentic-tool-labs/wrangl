using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Hierarchy;

/// <summary>
/// The queue's last-dispatch entries (ah-msgs-fast-2 §3.8 item 7, rulings Q2-Q4): per pool, team and member name, the newest
/// <c>created</c> of the orchestrator's requests to that member, parsed as the queue parses it (<see cref="Timestamps.TryParseMs"/>; a
/// header it cannot parse gives no row). The name is <see cref="HierarchyModule.DispatchName"/> of the request's <c>to_name</c> and its
/// dispatch row's <c>to_addr</c>; the role is rolled up at read time from the roster, as the queue does today.
/// </summary>
internal sealed partial class HierLib
{
    private static string LastDispatchKey(string team, string name) => team + "\n" + name;

    private static string? FmText(JsonValue? fm, string key) => fm?.Member(key) is { IsText: true } t ? t.AsString() : null;

    /// <summary>The (key, team, name, ms) one request's front matter contributes, or null when the queue would not count it.</summary>
    private static (string Key, string Team, string Name, double Ms)? DispatchFactOf(JsonValue? fm, string? toAddr)
    {
        if (FmText(fm, "from") != HierarchyModule.OrchestratorRole || FmText(fm, "type") != "request" || FmText(fm, "team") is not { } team
            || !Timestamps.TryParseMs(FmText(fm, "created"), out var ms))
        {
            return null;
        }

        // F3: the queue keeps only names its roster holds, so "(unnamed)" (no to_name, no peer record yet) is never stored: a request's
        // first dispatch record then only raises its member's row, from that one request, and never makes a stored key stale
        var name = HierarchyModule.DispatchName(FmText(fm, "to_name"), toAddr);
        return name == "(unnamed)" ? null : (LastDispatchKey(team, name), team, name, ms);
    }

    private static StatusEntry LastDispatchEntry(string pool, (string Key, string Team, string Name, double Ms) fact) =>
        new(pool, StatusKind.LastDispatch, fact.Key, new JsonObject { ["team"] = fact.Team, ["name"] = fact.Name, ["ms"] = fact.Ms });

    /// <summary>The <c>to_addr</c> of each request's first peer record, as the rebuild pairs them; one query for every id.</summary>
    private Dictionary<string, List<JsonObject>> PeerRecordsOf(List<string> ids)
    {
        var by = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        if (ids.Count == 0)
        {
            return by;
        }

        foreach (var r in _core.PeerRecordsWhere(new PeerQuery("request_id", null, null, null, ids.ToHashSet(StringComparer.Ordinal))).Where(static r => r.IsMap).Select(static r => r.AsObject()))
        {
            if (FmText(r, "request_id") is { } id)
            {
                (by.TryGetValue(id, out var list) ? list : by[id] = []).Add(r);
            }
        }

        return by;
    }

    /// <summary>One request's dispatch inputs before and after a write: its front matter (null when absent) and its first peer record's <c>to_addr</c>.</summary>
    private readonly record struct DispatchChange(string Id, JsonValue? BeforeFm, string? BeforeAddr, JsonValue? AfterFm, string? AfterAddr);

    /// <summary>
    /// The last-dispatch rows (and deletes) of <paramref name="pool"/> that <paramref name="changes"/> move (§3.8 item 7, F3/F4): a
    /// request's fact after the write raises its key; a key whose held newest was the request's fact before the write is recomputed from
    /// the pool's stored exchange rows. No message file is read.
    /// </summary>
    // ponytail: a key whose newest request leaves (archive, prune, a rename by its first dispatch row) is recomputed from every stored exchange row of the pool, a database read; index the rows by member if it shows in the perf table
    private (List<StatusEntry> Rows, List<(string, string, string)> Deletes) LastDispatchMoved(string pool, IReadOnlyList<DispatchChange> changes)
    {
        var held = _statusEntries!.ReadKind(pool, StatusKind.LastDispatch)
            .ToDictionary(static r => r.Key, static r => JsonText.Parse(r.Json).Member("ms") is { IsNumber: true } ms ? ms.AsNumber() : (double?)null, StringComparer.Ordinal);
        var raised = new Dictionary<string, (string Key, string Team, string Name, double Ms)>(StringComparer.Ordinal);
        var stale = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in changes)
        {
            var before = DispatchFactOf(c.BeforeFm, c.BeforeAddr);
            var after = DispatchFactOf(c.AfterFm, c.AfterAddr);
            if (before == after)
            {
                continue;
            }

            if (before is { } b && held.GetValueOrDefault(b.Key) is { } h && h <= b.Ms)
            {
                _ = stale.Add(b.Key);
            }

            if (after is { } a && !(held.GetValueOrDefault(a.Key) is { } had && had >= a.Ms) && !(raised.TryGetValue(a.Key, out var r) && r.Ms >= a.Ms))
            {
                raised[a.Key] = a;
            }
        }

        var rows = raised.Values.Where(f => !stale.Contains(f.Key)).Select(f => LastDispatchEntry(pool, f)).ToList();
        var deletes = new List<(string, string, string)>();
        if (stale.Count > 0)
        {
            var newest = NewestStored(pool, stale, changes);
            foreach (var key in stale)
            {
                if (newest.TryGetValue(key, out var fact))
                {
                    rows.Add(LastDispatchEntry(pool, fact));
                }
                else
                {
                    deletes.Add((pool, StatusKind.LastDispatch, key));
                }
            }
        }

        return (rows, deletes);
    }

    /// <summary>
    /// The newest fact of each of <paramref name="keys"/> (every key when null) over <paramref name="pool"/>'s stored exchange rows, each
    /// of <paramref name="changes"/> taken as it is after the write; the <c>to_addr</c> of a request with no <c>to_name</c> from one query.
    /// </summary>
    private Dictionary<string, (string Key, string Team, string Name, double Ms)> NewestStored(string pool, HashSet<string>? keys, IReadOnlyList<DispatchChange> changes)
    {
        var after = changes.ToDictionary(static c => c.Id, StringComparer.Ordinal);
        var teams = keys?.Select(static k => k[..k.IndexOf('\n', StringComparison.Ordinal)]).ToHashSet(StringComparer.Ordinal);
        var requests = new List<(string Id, JsonValue Fm, string? Addr, bool NeedsAddr)>();
        foreach (var row in _statusEntries!.ReadKind(pool, StatusKind.Exchange))
        {
            var (fm, addr, known) = after.TryGetValue(row.Key, out var c) ? (c.AfterFm, c.AfterAddr, true) : (JsonText.Parse(row.Json).Member("fm"), null, false);
            if (fm is { IsMap: true } map && (teams is null || (FmText(map, "team") is { } team && teams.Contains(team))))
            {
                requests.Add((row.Key, map, addr, !known && FmText(map, "to_name") is not { Length: > 0 }));
            }
        }

        var peers = PeerRecordsOf([.. requests.Where(static r => r.NeedsAddr).Select(static r => r.Id)]);
        var newest = new Dictionary<string, (string Key, string Team, string Name, double Ms)>(StringComparer.Ordinal);
        foreach (var (id, fm, addr, needsAddr) in requests)
        {
            var toAddr = needsAddr ? (peers.TryGetValue(id, out var recs) ? FmText(recs[0], "to_addr") : null) : addr;
            if (DispatchFactOf(fm, toAddr) is { } fact && (keys is null || keys.Contains(fact.Key)) && (!newest.TryGetValue(fact.Key, out var had) || fact.Ms > had.Ms))
            {
                newest[fact.Key] = fact;
            }
        }

        return newest;
    }

    /// <summary>Every last-dispatch row of <paramref name="pool"/>, from its full listing and each request's front matter: the rebuild.</summary>
    internal List<StatusEntry> LastDispatchEntries(string pool)
    {
        var paths = _core.ListExchanges(pool).Select(e => TextOf(Member(Member(e, "request"), "path"))).OfType<string>().ToList();
        var ids = paths.Select(p => _core.ParseMsgFilename(p)?.Id).ToList();
        var peers = PeerRecordsOf([.. ids.OfType<string>()]);
        var parsed = _core.ReadMsgFiles(paths);
        var newest = new Dictionary<string, (string Key, string Team, string Name, double Ms)>(StringComparer.Ordinal);
        for (var i = 0; i < paths.Count; i++)
        {
            var toAddr = ids[i] is { } id && peers.TryGetValue(id, out var records) ? FmText(records[0], "to_addr") : null;
            if (DispatchFactOf(parsed[i].Member("fm"), toAddr) is { } fact && (!newest.TryGetValue(fact.Key, out var had) || fact.Ms > had.Ms))
            {
                newest[fact.Key] = fact;
            }
        }

        return [.. newest.Values.Select(f => LastDispatchEntry(pool, f))];
    }

    /// <summary>The create of a request: its one fact raises its member's row; no read beyond the text the write carried.</summary>
    private List<StatusEntry> LastDispatchRaised(string pool, JsonValue? fm)
    {
        if (DispatchFactOf(fm, null) is not { } fact)
        {
            return [];
        }

        var held = _statusEntries!.ReadKind(pool, StatusKind.LastDispatch, fact.Key) is [var row] ? JsonText.Parse(row.Json).Member("ms")?.AsNumber() : null;
        return held is { } ms && ms >= fact.Ms ? [] : [LastDispatchEntry(pool, fact)];
    }

    /// <summary>The queue's newest dispatch per member of <paramref name="team"/> in <paramref name="pool"/>; null when no status dir lists the pool, so no row is kept.</summary>
    public IReadOnlyList<(string Name, double Ms)>? LastDispatches(string pool, string team)
    {
        pool = MsgRows.PoolKey(pool);
        if (_statusEntries is not { } store || string.IsNullOrEmpty(pool) || !Listed(pool))
        {
            return null;
        }

        return [.. store.ReadKind(pool, StatusKind.LastDispatch).Select(static r => JsonText.Parse(r.Json))
            .Where(j => FmText(j, "team") == team && j.Member("ms") is { IsNumber: true }).Select(static j => (FmText(j, "name")!, j.Member("ms")!.Value.AsNumber()))];
    }
}
