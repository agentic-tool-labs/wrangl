using System.Globalization;
using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The message flow of a team's current run, composed from the messages lane's records and the decisions lane's run. Each held exchange
/// of the team that is not withdrawn gives a brief at <c>created</c> (from → to) and a report at <c>response_created</c> (to → from).
/// The run is the directory's run with the latest decision (<see cref="TreeData.Pipeline"/>); it starts at its anchor exchange's
/// <c>created</c>, or at the anchor id's <c>YYYYMMDD-HHMMSS</c> prefix in local time when the anchor is not held. With no run the window
/// is the hour up to the newest event. It reads no file and no clock.
/// </summary>
internal sealed class RunFlow
{
    public const int PageSize = 200;
    public const int MaxNames = 64;
    public const int MaxTotals = 200;
    public const int SliceBudget = 10240;
    public const long NoRunWindowMs = 60 * 60 * 1000;
    public const string Other = "other";

    /// <summary>One event: its epoch ms, whole seconds since the start, the name indexes of its two ends and its kind (0 brief, 1 report).</summary>
    internal readonly record struct Event(long Ms, long T, string Id, int A, int B, int K);

    public required string? Run { get; init; }

    public required long StartMs { get; init; }

    public required bool Evicted { get; init; }

    public required List<string> Names { get; init; }

    public required List<Event> Events { get; init; }

    /// <summary>The number of full pages; pages exist only for a run.</summary>
    public int FullPages => Run is null ? 0 : Events.Count / PageSize;

    public static RunFlow? Of(IRecordView records, string dirId, string team) =>
        records is IPassMemo memo ? memo.GetOrAdd("run-flow:" + dirId + "/" + team, () => Box(Build(records, dirId, team))).Value : Build(records, dirId, team);

    private sealed record Boxed(RunFlow? Value);

    private static Boxed Box(RunFlow? flow) => new(flow);

    private static RunFlow? Build(IRecordView records, string dirId, string teamName)
    {
        if (TreeData.FindTeam(records, dirId, teamName) is not { } team)
        {
            return null;
        }

        var run = TreeData.Pipeline(records, dirId) is { } pipeline ? LaneParts.Text(pipeline.Value, "run") : null;
        var raw = new List<(long Ms, string Id, int K, string From, string To)>();
        foreach (var (id, record) in ExchangeFiling.OfTeam(records, dirId, teamName))
        {
            if (record.Value.Member("withdrawn") is { IsBool: true } w && w.AsBool())
            {
                continue;
            }

            var (to, from) = ExchangeFiling.Resolve(record, team);
            var toName = to is null ? Other : TreeIds.Member(team.Dir, team.Name, to);
            var fromName = from is null ? Other : TreeIds.Member(team.Dir, team.Name, from);
            if (EpochMs(record.Value, "created") is { } created)
            {
                raw.Add((created, id, 0, fromName, toName));
            }

            if (EpochMs(record.Value, "response_created") is { } responded)
            {
                raw.Add((responded, id, 1, toName, fromName));
            }
        }

        var (heldCount, oldestHeldMs, anchorMs) = Held(records, dirId, run);
        long start;
        if (run is not null)
        {
            start = anchorMs ?? AnchorStamp(run) ?? (raw.Count == 0 ? 0 : raw.Min(static r => r.Ms));
            raw.RemoveAll(r => r.Ms < start);
        }
        else
        {
            start = raw.Count == 0 ? 0 : raw.Max(static r => r.Ms) - NoRunWindowMs;
            raw.RemoveAll(r => r.Ms <= start);
        }

        var sorted = raw.Select(r => (r.Ms, T: (r.Ms - start) / 1000, r.Id, r.K, r.From, r.To))
            .OrderBy(static r => r.Ms).ThenBy(static r => r.Id, StringComparer.Ordinal).ThenBy(static r => r.K).ToList();

        // Names are numbered by first appearance, so an appended event never renumbers an earlier one. A withdrawal or a late older
        // record does shift events and names inside full pages; pages_rev changes when that happens.
        // ponytail: member ids have no length cap, so about 64 names over 150 B each can push a head past the gate; cap the member id
        // length if that ever happens.
        var names = new List<string>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        int IndexOf(string name)
        {
            if (index.TryGetValue(name, out var at))
            {
                return at;
            }

            var members = name == Other ? 0 : 1;
            if (members == 1 && index.Count - (index.ContainsKey(Other) ? 1 : 0) >= MaxNames)
            {
                return IndexOf(Other);
            }

            index[name] = names.Count;
            names.Add(name);
            return names.Count - 1;
        }

        var events = sorted.Select(r => new Event(r.Ms, r.T, r.Id, IndexOf(r.From), IndexOf(r.To), r.K)).ToList();

        // A held anchor proves the run's exchanges are all held: the lane keeps the newest ids and a run's ids follow its anchor's.
        return new RunFlow
        {
            Run = run,
            StartMs = start,
            Evicted = (run is null || anchorMs is null) && heldCount >= MessagesLane.MaxExchangesPerDir && oldestHeldMs > start,
            Names = names,
            Events = events,
        };
    }

    /// <summary>How many exchanges the directory holds, the oldest held <c>created</c>, and the anchor's <c>created</c> when it is held.</summary>
    private static (int Count, long OldestMs, long? AnchorMs) Held(IRecordView records, string dirId, string? run)
    {
        var count = 0;
        var oldest = long.MaxValue;
        long? anchor = null;
        string? lastDir = null;
        var lastMatches = false;
        foreach (var key in records.Keys(MessagesLane.LaneName))
        {
            var at = key.IndexOf(MessagesLane.Separator, StringComparison.Ordinal);
            if (lastDir is null || !key.AsSpan(0, at).SequenceEqual(lastDir))
            {
                lastDir = key[..at];
                lastMatches = ActivityLane.DirId(lastDir) == dirId;
            }

            if (!lastMatches || records.Get(MessagesLane.LaneName, key) is not { IsAbsent: false } record)
            {
                continue;
            }

            count++;
            if (EpochMs(record.Value, "created") is { } created)
            {
                oldest = Math.Min(oldest, created);
                if (run is not null && key.AsSpan(at + 1).SequenceEqual(run))
                {
                    anchor = created;
                }
            }
        }

        return (count, oldest, anchor);
    }

    /// <summary>The local time an anchor id's <c>YYYYMMDD-HHMMSS</c> prefix names, in epoch ms; null when it has none.</summary>
    internal static long? AnchorStamp(string run) =>
        run.Length >= 15 && DateTime.TryParseExact(run[..15], "yyyyMMdd-HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var local)
            ? new DateTimeOffset(local).ToUnixTimeMilliseconds()
            : null;

    internal static long? EpochMs(JsonValue record, string name) =>
        LaneParts.Text(record, name) is { } stamp && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when.ToUnixTimeMilliseconds()
            : null;

    internal static JsonValue Encode(Event e)
    {
        var array = new JsonArray { e.T, e.A, e.B, e.K };
        return JsonValue.FromArray(array);
    }

    internal static int Bytes(JsonValue value) => JsonText.ToUtf8(value, 0).Length;

    /// <summary>
    /// The head body. When it is over <see cref="SliceBudget"/> it is trimmed in this order until it fits: the oldest <c>tail</c> events,
    /// the lowest-count <c>totals</c> rows, then the <c>names</c> nothing left references (nulled in place so no index moves). Any trim sets
    /// <c>truncated</c>.
    /// </summary>
    public JsonObject HeadBody()
    {
        var full = FullPages;
        var pages = new List<long>();
        for (var k = 0; k < full; k++)
        {
            pages.Add(Events[k * PageSize].T);
        }

        var tail = Events.Skip(full * PageSize).ToList();
        var totals = Totals(out var overflow);
        var names = Names.Select(static n => (string?)n).ToList();
        var trimmed = false;

        JsonObject Make() => Body(names, totals, overflow, pages, tail, Evicted || trimmed);

        var excess = Bytes(JsonValue.FromObject(Make())) - SliceBudget;
        while (excess > 0 && tail.Count > 0)
        {
            var drop = 0;
            for (var freed = 0; drop < tail.Count && freed < excess; drop++)
            {
                freed += Bytes(Encode(tail[drop])) + 1;
            }

            tail.RemoveRange(0, drop);
            trimmed = true;
            excess = Bytes(JsonValue.FromObject(Make())) - SliceBudget;
        }

        while (excess > 0 && totals.Count > 0)
        {
            var drop = 0;
            for (var freed = 0; drop < totals.Count && freed < excess; drop++)
            {
                freed += Bytes(Row(totals[totals.Count - 1 - drop])) + 1;
            }

            totals.RemoveRange(totals.Count - drop, drop);
            trimmed = true;
            excess = Bytes(JsonValue.FromObject(Make())) - SliceBudget;
        }

        if (excess > 0)
        {
            var used = new HashSet<int>();
            foreach (var e in Events.Take(full * PageSize).Concat(tail))
            {
                _ = used.Add(e.A);
                _ = used.Add(e.B);
            }

            foreach (var (a, b, _, _) in totals)
            {
                _ = used.Add(a);
                _ = used.Add(b);
            }

            for (var i = 0; i < names.Count; i++)
            {
                if (!used.Contains(i) && names[i] is not null)
                {
                    names[i] = null;
                    trimmed = true;
                }
            }

            while (names.Count > 0 && names[^1] is null)
            {
                names.RemoveAt(names.Count - 1);
            }
        }

        return Make();
    }

    private JsonObject Body(List<string?> names, List<(int A, int B, long Briefs, long Reports)> totals, (long Briefs, long Reports)? overflow, List<long> pages, List<Event> tail, bool truncated)
    {
        var nameArray = new JsonArray();
        foreach (var n in names)
        {
            nameArray.Add(n is null ? JsonValue.Null : n);
        }

        var totalArray = new JsonArray();
        foreach (var row in totals)
        {
            totalArray.Add(Row(row));
        }

        if (overflow is { } o)
        {
            totalArray.Add(Row((-1, -1, o.Briefs, o.Reports)));
        }

        var pageArray = new JsonArray();
        foreach (var t in pages)
        {
            pageArray.Add(t);
        }

        var tailArray = new JsonArray();
        foreach (var e in tail)
        {
            tailArray.Add(Encode(e));
        }

        return new JsonObject
        {
            ["run"] = Run is null ? JsonValue.Null : Run,
            ["start_ms"] = StartMs,
            ["oldest_ms"] = Events.Count == 0 ? JsonValue.Null : Events[0].Ms,
            ["truncated"] = truncated,
            ["names"] = nameArray,
            ["totals"] = totalArray,
            ["page_size"] = PageSize,
            ["pages"] = pageArray,
            ["pages_rev"] = PagesRev(),
            ["tail"] = tailArray,
        };
    }

    /// <summary>
    /// A short hash over every event of the full pages (id, kind, ms, both name indexes) and the names those events reference, so it
    /// changes whenever a full page's content or the meaning of an index in it does; a client drops its page cache when it changes.
    /// </summary>
    internal string PagesRev()
    {
        var text = new System.Text.StringBuilder();
        var used = new SortedSet<int>();
        foreach (var e in Events.Take(FullPages * PageSize))
        {
            _ = text.Append(e.Id).Append(':').Append(e.K).Append(':').Append(e.Ms).Append(':').Append(e.A).Append(':').Append(e.B).Append('\n');
            _ = used.Add(e.A);
            _ = used.Add(e.B);
        }

        foreach (var i in used)
        {
            _ = text.Append(i).Append('=').Append(Names[i]).Append('\n');
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString())))[..16].ToLowerInvariant();
    }

    private static JsonValue Row((int A, int B, long Briefs, long Reports) row)
    {
        var array = new JsonArray { row.A, row.B, row.Briefs, row.Reports };
        return JsonValue.FromArray(array);
    }

    /// <summary>Briefs and reports per ordered pair over the whole window, most first; past <see cref="MaxTotals"/> rows the rest is summed into an overflow row.</summary>
    private List<(int A, int B, long Briefs, long Reports)> Totals(out (long Briefs, long Reports)? overflow)
    {
        var rows = Events.GroupBy(static e => (e.A, e.B))
            .Select(static g => (g.Key.A, g.Key.B, Briefs: (long)g.Count(static e => e.K == 0), Reports: (long)g.Count(static e => e.K == 1)))
            .OrderByDescending(static r => r.Briefs + r.Reports).ThenBy(static r => r.A).ThenBy(static r => r.B).ToList();
        overflow = null;
        if (rows.Count > MaxTotals)
        {
            var rest = rows.Skip(MaxTotals - 1).ToList();
            overflow = (rest.Sum(static r => r.Briefs), rest.Sum(static r => r.Reports));
            rows = rows.Take(MaxTotals - 1).ToList();
        }

        return rows;
    }

    /// <summary>The body of full page <paramref name="k"/>: events [k·<see cref="PageSize"/>, (k+1)·<see cref="PageSize"/>).</summary>
    public (JsonObject Body, long AsOfMs) PageBody(int k)
    {
        var events = new JsonArray();
        var slice = Events.Skip(k * PageSize).Take(PageSize).ToList();
        foreach (var e in slice)
        {
            events.Add(Encode(e));
        }

        return (new JsonObject { ["run"] = Run!, ["k"] = k, ["events"] = events }, slice.Max(static e => e.Ms));
    }

    /// <summary>The teams whose flow a change can touch: the exchange's team (before and after), every team of the directory on a decision or an anchor change, and the changed team.</summary>
    internal static IReadOnlyList<(string DirId, string Team)> TeamsAffected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        if (changed.Lane == TeamLane.LaneName)
        {
            return [.. LaneView.TeamsAround(changed, previous, current, records).Select(static t => (t.DirId, t.Name))];
        }

        var (dir, second) = LaneParts.Split(changed.Key);
        var dirId = ActivityLane.DirId(dir);
        var all = TreeData.Teams(records).Where(t => t.DirId == dirId).Select(static t => (t.DirId, t.Name));
        if (changed.Lane == DecisionsLane.LaneName || (TreeData.Pipeline(records, dirId) is { } p && LaneParts.Text(p.Value, "run") == second))
        {
            return [.. all];
        }

        var teams = new[] { previous, current }.Where(static r => r is { IsAbsent: false }).Select(static r => ExchangeFiling.TeamOf(r!)).ToHashSet(StringComparer.Ordinal);
        return [.. all.Where(t => teams.Contains(t.Name))];
    }

    internal static (string DirId, string Tail)? SplitId(string sliceId, string prefix)
    {
        var head = prefix + ":";
        if (!sliceId.StartsWith(head, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = sliceId[head.Length..];
        var at = rest.IndexOf('/', StringComparison.Ordinal);
        return at <= 0 || at == rest.Length - 1 ? null : (rest[..at], rest[(at + 1)..]);
    }
}

/// <summary><c>flow:&lt;dirId&gt;/&lt;team&gt;</c>: the head of a team's run flow (§1.2 of the graph-views spec).</summary>
internal sealed class FlowHeadComposer : ISliceComposer
{
    public const string Prefix = "flow";

    public string SlicePrefix => Prefix;

    public IReadOnlyList<string> Lanes { get; } = [MessagesLane.LaneName, DecisionsLane.LaneName, TeamLane.LaneName];

    public static string IdOf(string dirId, string team) => Prefix + ":" + dirId + "/" + team;

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) =>
        [.. RunFlow.TeamsAffected(changed, previous, current, records).Select(static t => IdOf(t.DirId, t.Team))];

    public IReadOnlyList<string> AllIds(IRecordView records) => [.. TreeData.Teams(records).Select(static t => IdOf(t.DirId, t.Name))];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (RunFlow.SplitId(sliceId, Prefix) is not var (dirId, team) || RunFlow.Of(records, dirId, team) is not { } flow)
        {
            return new SliceChange(sliceId, null);
        }

        var asOf = flow.Events.Count == 0 ? 0 : flow.Events.Max(static e => e.Ms);
        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(flow.HeadBody()), 0), asOf);
    }
}

/// <summary>
/// <c>flow-page:&lt;dirId&gt;/&lt;team&gt;/&lt;run&gt;/&lt;k&gt;</c>: one full page of a run's events. A page never changes once full, unless the
/// lane's cap evicted run events, an exchange in it was withdrawn or an older record arrived late; the head's <c>pages_rev</c> changes then.
/// </summary>
internal sealed class FlowPageComposer : ISliceComposer
{
    public const string Prefix = "flow-page";

    public string SlicePrefix => Prefix;

    public IReadOnlyList<string> Lanes { get; } = [MessagesLane.LaneName, DecisionsLane.LaneName, TeamLane.LaneName];

    public static string IdOf(string dirId, string team, string run, int k) =>
        Prefix + ":" + dirId + "/" + team + "/" + run + "/" + k.ToString(CultureInfo.InvariantCulture);

    // ponytail: one page past the current count is re-composed so a page lost to eviction is removed; a previous run's pages are left
    // until the next full rebuild. Track published page ids per team if that leak matters.
    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) =>
        [.. RunFlow.TeamsAffected(changed, previous, current, records).SelectMany(t => Pages(records, t.DirId, t.Team, 1))];

    public IReadOnlyList<string> AllIds(IRecordView records) =>
        [.. TreeData.Teams(records).SelectMany(t => Pages(records, t.DirId, t.Name, 0))];

    private static IEnumerable<string> Pages(IRecordView records, string dirId, string team, int extra) =>
        RunFlow.Of(records, dirId, team) is { Run: { } run } flow
            ? Enumerable.Range(0, flow.FullPages + extra).Select(k => IdOf(dirId, team, run, k))
            : [];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (RunFlow.SplitId(sliceId, Prefix) is not var (dirId, rest))
        {
            return new SliceChange(sliceId, null);
        }

        var parts = rest.Split('/');
        if (parts.Length < 3 || !int.TryParse(parts[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var k))
        {
            return new SliceChange(sliceId, null);
        }

        var team = string.Join('/', parts[..^2]);
        if (RunFlow.Of(records, dirId, team) is not { } flow || flow.Run != parts[^2] || k >= flow.FullPages)
        {
            return new SliceChange(sliceId, null);
        }

        var (body, asOf) = flow.PageBody(k);
        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), asOf);
    }
}

/// <summary>
/// <c>item-msgs:&lt;dirId&gt;/&lt;team&gt;</c> (message counts per plan item and per stream) and <c>item-msgs:&lt;dirId&gt;/&lt;team&gt;/&lt;item&gt;</c>
/// (one item's count and newest exchange ids). <c>&lt;team&gt;</c> is the progress folder, as in <c>plan-items:</c>. All held exchanges of
/// the team count, not only the run's; withdrawn ones do not, nor any whose slug is not one of the team's plan streams.
/// </summary>
internal sealed class ItemMsgsComposer : ISliceComposer
{
    public const string Prefix = "item-msgs";
    public const int MaxItems = 400;
    public const int MaxStreams = 64;
    public const int MaxIds = 14;

    public string SlicePrefix => Prefix;

    public IReadOnlyList<string> Lanes { get; } = [MessagesLane.LaneName, ProgressLane.LaneName];

    public static string IdOf(string dirId, string folder) => Prefix + ":" + dirId + "/" + folder;

    public static string IdOf(string dirId, string folder, string item) => IdOf(dirId, folder) + "/" + item;

    /// <summary>The counted exchanges of a team: per item and per stream, each list newest first; null when the team has no plan.</summary>
    private sealed record Counted(List<(string Item, List<(string Id, long AsOf)> Ids)> Items, List<(string Stream, int N)> Streams);

    private static Counted? Tally(IRecordView records, string dirId, string folder)
    {
        var plan = records.Keys(ProgressLane.LaneName)
            .Where(k => ProgressLane.Split(k) is var (dir, f) && f == folder && ActivityLane.DirId(dir) == dirId)
            .Select(k => records.Get(ProgressLane.LaneName, k))
            .FirstOrDefault(static r => r is { IsAbsent: false });
        if (plan is null)
        {
            return null;
        }

        var streams = new HashSet<string>(StringComparer.Ordinal);
        if (plan.Value.Member("streams") is { IsArray: true } listed)
        {
            foreach (var s in listed.AsArray())
            {
                if (LaneParts.Text(s, "stream") is { } name)
                {
                    _ = streams.Add(name);
                }
            }
        }

        var items = new List<(string Item, List<(string Id, long AsOf)> Ids)>();
        var itemAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var byStream = new List<(string Stream, int N)>();
        var streamAt = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (id, record) in ExchangeFiling.OfTeam(records, dirId, ProgressLane.TeamNameOf(folder)))
        {
            if ((record.Value.Member("withdrawn") is { IsBool: true } w && w.AsBool()) || LaneParts.Text(record.Value, "slug") is not { } slug || !streams.Contains(slug))
            {
                continue;
            }

            if (!streamAt.TryGetValue(slug, out var s))
            {
                s = byStream.Count;
                streamAt[slug] = s;
                byStream.Add((slug, 0));
            }

            byStream[s] = (slug, byStream[s].N + 1);
            if (LaneParts.Text(record.Value, "item") is { } item)
            {
                if (!itemAt.TryGetValue(item, out var i))
                {
                    i = items.Count;
                    itemAt[item] = i;
                    items.Add((item, []));
                }

                items[i].Ids.Add((id, record.AsOfMs));
            }
        }

        return new Counted(items, byStream);
    }

    private static Counted? CountOf(IRecordView records, string dirId, string folder) =>
        records is IPassMemo memo ? memo.GetOrAdd("item-msgs:" + dirId + "/" + folder, () => new Box(Tally(records, dirId, folder))).Value : Tally(records, dirId, folder);

    private sealed record Box(Counted? Value);

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records)
    {
        if (changed.Lane == ProgressLane.LaneName)
        {
            var (dir, folder) = ProgressLane.Split(changed.Key);
            var dirId = ActivityLane.DirId(dir);
            var all = ExchangeFiling.OfTeam(records, dirId, ProgressLane.TeamNameOf(folder))
                .Select(static x => LaneParts.Text(x.Record.Value, "item")).Where(static i => i is not null).Distinct(StringComparer.Ordinal);
            return [IdOf(dirId, folder), .. all.Select(i => IdOf(dirId, folder, i!))];
        }

        var changedDirId = ActivityLane.DirId(MessagesLane.Split(changed.Key).Dir);
        var ids = new List<string>();
        foreach (var record in new[] { previous, current }.Where(static r => r is { IsAbsent: false }))
        {
            var folderOf = ProgressLane.FolderOf(ExchangeFiling.TeamOf(record!));
            ids.Add(IdOf(changedDirId, folderOf));
            if (LaneParts.Text(record!.Value, "item") is { } item)
            {
                ids.Add(IdOf(changedDirId, folderOf, item));
            }
        }

        return [.. ids.Distinct(StringComparer.Ordinal)];
    }

    public IReadOnlyList<string> AllIds(IRecordView records)
    {
        var ids = new List<string>();
        foreach (var key in records.Keys(ProgressLane.LaneName))
        {
            var (dir, folder) = ProgressLane.Split(key);
            var dirId = ActivityLane.DirId(dir);
            if (CountOf(records, dirId, folder) is not { } counted)
            {
                continue;
            }

            ids.Add(IdOf(dirId, folder));
            ids.AddRange(counted.Items.Take(MaxItems).Select(i => IdOf(dirId, folder, i.Item)));
        }

        return ids;
    }

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        if (RunFlow.SplitId(sliceId, Prefix) is not var (dirId, rest))
        {
            return new SliceChange(sliceId, null);
        }

        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        var folder = slash < 0 ? rest : rest[..slash];
        if (CountOf(records, dirId, folder) is not { } counted)
        {
            return new SliceChange(sliceId, null);
        }

        if (slash >= 0)
        {
            var item = rest[(slash + 1)..];
            if (counted.Items.Take(MaxItems).FirstOrDefault(i => i.Item == item) is not { Ids: { } ids })
            {
                return new SliceChange(sliceId, null);
            }

            var newest = new JsonArray();
            foreach (var (id, _) in ids.Take(MaxIds))
            {
                newest.Add(id);
            }

            var one = new JsonObject { ["item"] = item, ["n"] = ids.Count, ["ids"] = newest };
            return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(one), 0), ids.Max(static i => i.AsOf));
        }

        var items = counted.Items.Take(MaxItems).ToList();
        var streams = counted.Streams.Take(MaxStreams).ToList();
        var truncated = items.Count < counted.Items.Count || streams.Count < counted.Streams.Count;
        var body = CountsBody(items, streams, truncated);

        // Keys are unbounded strings, so the gate is held by dropping the least recently active items, then streams.
        while (RunFlow.Bytes(JsonValue.FromObject(body)) > RunFlow.SliceBudget && (items.Count > 0 || streams.Count > 0))
        {
            if (items.Count > 0)
            {
                items.RemoveAt(items.Count - 1);
            }
            else
            {
                streams.RemoveAt(streams.Count - 1);
            }

            body = CountsBody(items, streams, true);
        }

        var asOf = counted.Items.SelectMany(static i => i.Ids).Select(static i => i.AsOf).DefaultIfEmpty(0).Max();
        return new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(body), 0), asOf);
    }

    private static JsonObject CountsBody(List<(string Item, List<(string Id, long AsOf)> Ids)> items, List<(string Stream, int N)> streams, bool truncated)
    {
        var counts = new JsonObject();
        foreach (var (item, ids) in items.OrderBy(static i => i.Item, StringComparer.Ordinal))
        {
            counts[item] = ids.Count;
        }

        var byStream = new JsonObject();
        foreach (var (stream, n) in streams.OrderBy(static s => s.Stream, StringComparer.Ordinal))
        {
            byStream[stream] = n;
        }

        return new JsonObject { ["counts"] = JsonValue.FromObject(counts), ["by_stream"] = JsonValue.FromObject(byStream), ["truncated"] = truncated };
    }
}
