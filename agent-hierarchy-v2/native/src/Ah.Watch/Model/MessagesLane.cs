using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>
/// The messages lane: one key per (canonical hierarchy directory, exchange id), one slice per key, <c>exchange:&lt;dirId&gt;/&lt;id&gt;</c>.
/// An exchange is a request file and the response file that shares its id, in the directory's message pool. A key is re-read with one
/// <c>msgs.read</c> (at most two ranged reads of the files' first bytes); the key set comes from <c>msgs.keys</c>, newest first, so the
/// lane never lists a directory or opens a file itself. The slice carries the header and an excerpt of each file, never a whole body,
/// and an <c>as_of</c> taken from the files' own <c>created</c> stamps, never from the clock. An exchange that has left the pool (archived)
/// removes its slice. The lane also keeps a small record per key, from which the per-directory lists are composed.
/// </summary>
internal sealed partial class MessagesLane(IViewStateReader reader, Func<IReadOnlyList<string>> canonicalDirs, Func<IReadOnlyList<string>>? knownDirs = null, Action<string>? reread = null, Action<string>? rereadSelf = null) : IModelLane, IQuietKeys
{
    public const string LaneName = "messages";

    /// <summary>The newest exchanges of one directory the lane holds; older ones are neither seeded nor kept.</summary>
    public const int MaxExchangesPerDir = 5000;

    internal const char Separator = '\u001f';

    /// <summary>The id of the pseudo key a change to a directory's gates file is routed to: it rereads every open request of that directory, whose eta facts include the gate rows.</summary>
    internal const string GatesId = "*gates";

    /// <summary>The id of the pseudo key a change to the global peer-pending file is routed to: it rereads the open requests whose addressee was heard since they were read.</summary>
    internal const string HeardId = "*heard";

    public string Name => LaneName;

    /// <summary>The key of (<paramref name="dir"/>, <paramref name="id"/>).</summary>
    public static string KeyOf(string dir, string id) => dir + Separator + id;

    public static string SliceIdOf(string dir, string id) => SliceIdOfDirId(ActivityLane.DirId(dir), id);

    /// <summary>The slice id of the exchange <paramref name="id"/> of the directory whose id is <paramref name="dirId"/>.</summary>
    public static string SliceIdOfDirId(string dirId, string id) => "exchange:" + dirId + "/" + id;

    /// <summary>The (directory, exchange id) a key names.</summary>
    internal static (string Dir, string Id) Split(string key)
    {
        var at = key.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0 ? throw new ArgumentException("not a messages key", nameof(key)) : (key[..at], key[(at + 1)..]);
    }

    public IReadOnlyList<string> SliceIds(string key)
    {
        var (dir, id) = Split(key);
        return id is GatesId or HeardId ? [] : [SliceIdOf(dir, id)];
    }

    /// <summary>The slice of a directory's latest soft-gate rows (rule, outcome, reason), newest last.</summary>
    public static string SoftGatesSliceOf(string dir) => "soft-gates:" + ActivityLane.DirId(dir);

    private const int SoftGateRows = 50;

    /// <summary>The soft-gate slice of <paramref name="dir"/>; none when the gates file cannot be read, which leaves the exchanges' reread as it was.</summary>
    private SliceChange[] SoftGates(string dir)
    {
        JsonValue gates;
        try
        {
            gates = reader.Read("gates", "read", dir, new JsonObject { ["dir"] = dir });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [];
        }

        var rows = new JsonArray();
        foreach (var r in (gates.IsArray ? gates.AsArray() : []).Where(static r => Field(r, "type").NonEmptyText == "soft-gate").TakeLast(SoftGateRows))
        {
            rows.Add(JsonValue.FromObject(new JsonObject
            {
                ["rule"] = Field(r, "rule"), ["outcome"] = Field(r, "outcome"), ["reason"] = Field(r, "reason"),
                ["tool"] = Field(r, "tool"), ["role"] = Field(r, "role"), ["session_id"] = Field(r, "session_id"), ["ts"] = Field(r, "ts"),
            }));
        }

        return [new SliceChange(SoftGatesSliceOf(dir), JsonText.ToUtf8(JsonValue.FromObject(new JsonObject { ["rows"] = rows }), 0))];
    }

    public LaneResult Reread(string key, long nowMs)
    {
        var (dir, id) = Split(key);
        if (id == GatesId)
        {
            List<string> opens;
            lock (_gate)
            {
                opens = _open.TryGetValue(dir, out var openIds) ? openIds.ToList() : [];
            }

            foreach (var open in opens)
            {
                rereadSelf?.Invoke(KeyOf(dir, open));
            }

            return new LaneResult(SoftGates(dir)) { Record = LaneRecord.Absent };
        }

        if (id == HeardId)
        {
            RereadHeard();
            return new LaneResult([]) { Record = LaneRecord.Absent };
        }

        var sliceId = SliceIdOf(dir, id);
        if (knownDirs is not null && !knownDirs().Contains(dir, StringComparer.Ordinal))
        {
            Forget(dir, id);
            return new LaneResult([new SliceChange(sliceId, null)]) { Record = LaneRecord.Absent };
        }

        var exchange = reader.Read("msgs", "read", dir, new JsonObject { ["dir"] = dir, ["id"] = id });
        if (!exchange.IsMap)
        {
            Forget(dir, id);
            reread?.Invoke(key);
            return new LaneResult([new SliceChange(sliceId, null)]) { Record = LaneRecord.Absent };
        }

        List<string> over;
        lock (_gate)
        {
            if (!_held.TryGetValue(dir, out var held))
            {
                held = new SortedSet<string>(StringComparer.Ordinal);
                if (Interlocked.Exchange(ref _counted, 1) == 0)
                {
                    Ah.Core.CoreModule.CensusAdd(this, "watch.messages.held_ids", static l => { lock (l._gate) { return l._held.Values.Sum(static s => (long)s.Count); } });
                    Ah.Core.CoreModule.CensusAdd(this, "watch.messages.open_ids", static l => { lock (l._gate) { return l._open.Values.Sum(static s => (long)s.Count); } });
                    Ah.Core.CoreModule.CensusAdd(this, "watch.messages.facts", static l => { lock (l._gate) { return l._facts.Count; } });
                }

                _held[dir] = held;
            }

            _ = held.Add(id);
            MarkOpen(dir, id, IsOpen(exchange));
            NoteFacts(dir, id, exchange);
            over = held.Count > MaxExchangesPerDir ? held.Take(held.Count - MaxExchangesPerDir).ToList() : [];
            held.ExceptWith(over);
        }

        var others = over.Where(o => o != id).ToList();
        var dropped = new LaneResult([]) { Record = LaneRecord.Absent, Evict = [.. others.Select(o => SliceIdOf(dir, o))], EvictRecords = [.. others.Select(o => KeyOf(dir, o))] };
        if (over.Contains(id))
        {
            return dropped;
        }

        var asOf = AsOf(exchange);
        var record = new LaneRecord(
            JsonValue.FromObject(new JsonObject(13)
            {
                ["dir_id"] = ActivityLane.DirId(dir),
                ["id"] = id,
                ["to"] = Field(exchange, "to"),
                ["from"] = Field(exchange, "from"),
                ["to_name"] = Field(exchange, "to_name"),
                ["from_name"] = Field(exchange, "from_name"),
                ["team"] = Field(exchange, "team"),
                ["slug"] = Field(exchange, "slug"),
                ["item"] = Field(exchange, "item"),
                ["created"] = Field(exchange, "created"),
                ["response_created"] = HasResponse(exchange) ? Field(exchange, "response_created") : JsonValue.Null,
                ["open"] = IsOpen(exchange),
                ["withdrawn"] = HasResponse(exchange) && exchange.Member("withdrawn") is { IsBool: true } w && w.AsBool(),
            }),
            asOf);
        reread?.Invoke(key);
        return new LaneResult([new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(BodyOf(dir, id, exchange)), 0), asOf)])
        {
            Record = record,
            Evict = dropped.Evict,
            EvictRecords = dropped.EvictRecords,
        };
    }

    private readonly Dictionary<string, SortedSet<string>> _held = new(StringComparer.Ordinal);

    private readonly Dictionary<string, SortedSet<string>> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Dir, string Id), (double Heard, string? Session)> _facts = [];
    private int _counted;

    /// <summary>Guards <c>_held</c>, <c>_open</c> and <c>_facts</c>: a message-file reread and an activity-triggered session reread run on different workers.</summary>
    private readonly object _gate = new();

    private void NoteFacts(string dir, string id, JsonValue exchange)
    {
        if (exchange.Member("eta_facts") is { IsMap: true } facts)
        {
            _facts[(dir, id)] = (
                facts.Member("heard_ms") is { IsNumber: true } heard ? heard.AsNumber() : 0,
                facts.Member("addressee_session") is { IsText: true } session ? session.AsString() : null);
        }
        else
        {
            _ = _facts.Remove((dir, id));
        }
    }

    /// <summary>Rereads the open requests whose addressee runs <paramref name="session"/>, after that session's activity changed.</summary>
    public void RereadSession(string dir, string session)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = [.. _facts.Where(f => f.Key.Dir == dir && f.Value.Session == session).Select(static f => f.Key.Id)];
        }

        foreach (var id in ids)
        {
            rereadSelf?.Invoke(KeyOf(dir, id));
        }
    }

    private void RereadHeard()
    {
        List<(string Dir, List<string> Ids)> snapshot;
        lock (_gate)
        {
            snapshot = [.. _open.Select(o => (o.Key, o.Value.Where(id => _facts.ContainsKey((o.Key, id))).ToList()))];
        }

        foreach (var (dir, ids) in snapshot)
        {
            if (ids.Count == 0)
            {
                continue;
            }

            var list = new JsonArray();
            foreach (var id in ids)
            {
                list.Add(id);
            }

            var reply = reader.Read("msgs", "heard", dir, new JsonObject { ["dir"] = dir, ["ids"] = list });
            if (reply.Member("heard") is not { IsMap: true } heard)
            {
                continue;
            }

            foreach (var id in ids)
            {
                var now = heard.Member(id) is { IsNumber: true } n ? n.AsNumber() : 0;
                double known;
                lock (_gate)
                {
                    known = _facts.TryGetValue((dir, id), out var fact) ? fact.Heard : now;
                }

                if (now != known)
                {
                    rereadSelf?.Invoke(KeyOf(dir, id));
                }
            }
        }
    }

    private void MarkOpen(string dir, string id, bool open)
    {
        if (!_open.TryGetValue(dir, out var ids))
        {
            ids = new SortedSet<string>(StringComparer.Ordinal);
            _open[dir] = ids;
        }

        _ = open ? ids.Add(id) : ids.Remove(id);
    }

    private void Forget(string dir, string id)
    {
        lock (_gate)
        {
            MarkOpen(dir, id, false);
            _ = _facts.Remove((dir, id));
            if (_held.TryGetValue(dir, out var held))
            {
                _ = held.Remove(id);
            }
        }
    }

    /// <summary>The store's write counter at the last reconcile; null until one has read it. Projector thread only.</summary>
    private long? _quietMark;

    /// <summary>
    /// The answered exchanges this lane holds whose rows no write has touched since the last reconcile. An open one stays out, since its eta facts move with
    /// no write to it. A store whose counter cannot be read (a home still on files), or one that went back, names none.
    /// </summary>
    public IReadOnlySet<string> QuietKeys()
    {
        var quiet = new HashSet<string>(StringComparer.Ordinal);
        var since = _quietMark;
        _quietMark = null;
        if (canonicalDirs() is not [var first, ..])
        {
            return quiet;
        }

        try
        {
            if (reader.Read("msgs", "version", first, new JsonObject()).Member("version") is not { IsNumber: true } read)
            {
                return quiet;
            }

            var version = (long)read.AsNumber();
            if (since is { } mark && version >= mark)
            {
                foreach (var (dir, closed) in ClosedHeld())
                {
                    var changed = new HashSet<string>(StringComparer.Ordinal);
                    if (version > mark && reader.Read("msgs", "changed", dir, new JsonObject { ["dir"] = dir, ["since"] = mark }).Member("ids") is { IsArray: true } ids)
                    {
                        changed.UnionWith(ids.AsArray().Where(static i => i.IsText).Select(static i => i.AsString()));
                    }

                    quiet.UnionWith(closed.Where(id => !changed.Contains(id)).Select(id => KeyOf(dir, id)));
                }
            }

            _quietMark = version;
            return quiet;
        }
        catch (InvalidOperationException)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private List<(string Dir, List<string> Closed)> ClosedHeld()
    {
        lock (_gate)
        {
            return [.. _held.Select(h => (h.Key, h.Value.Where(id => !_open.TryGetValue(h.Key, out var open) || !open.Contains(id)).ToList()))];
        }
    }

    /// <summary>Each dir's keys with the message store version they were listed at (b459 step 28): listed again only after the version moves.</summary>
    private readonly Dictionary<string, (long Version, List<string> Keys)> _heldKeys = new(StringComparer.Ordinal);

    private long _keyReads;

    /// <summary>How many times a dir's keys were listed rather than taken from the hold, for the tests of the hold.</summary>
    internal long KeyReads => Interlocked.Read(ref _keyReads);

    /// <summary>Forgets every held key list: the doctor's rebuild.</summary>
    internal void ForgetKeys()
    {
        lock (_heldKeys)
        {
            _heldKeys.Clear();
        }
    }

    public IReadOnlyList<string> Keys()
    {
        var keys = new List<string>();
        var dirs = canonicalDirs().Distinct(StringComparer.Ordinal).ToList();

        // read before the lists: a write between the two leaves them on the older version, so the next call lists them again
        long? version = null;
        if (dirs is [var first, ..])
        {
            try
            {
                version = reader.Read("msgs", "version", first, new JsonObject()).Member("version") is { IsNumber: true } read ? (long)read.AsNumber() : null;
            }
            catch (InvalidOperationException)
            {
            }
        }

        foreach (var dir in dirs)
        {
            if (version is { } v)
            {
                (long Version, List<string> Keys) held;
                bool found;
                lock (_heldKeys)
                {
                    found = _heldKeys.TryGetValue(dir, out held);
                }

                if (found && (held.Version == v || (held.Version < v && NoneChanged(dir, held.Version))))
                {
                    // ah2-52 item 2: the version moved for another pool; no row of this one was stamped after the hold, so its keys stand at v
                    lock (_heldKeys)
                    {
                        _heldKeys[dir] = (v, held.Keys);
                    }

                    keys.AddRange(held.Keys);
                    continue;
                }
            }

            _ = Interlocked.Increment(ref _keyReads);
            var own = new List<string>();
            if (ListKeys(dir, own) && version is { } listed)
            {
                lock (_heldKeys)
                {
                    _heldKeys[dir] = (listed, own);
                }
            }

            keys.AddRange(own);
        }

        return keys;
    }

    /// <summary>
    /// Whether no message of <paramref name="dir"/>'s pool was changed by a write after the store version was <paramref name="since"/>
    /// (<c>msgs.changed</c>: every insert and update stamps <c>changed_seq</c> above the version of the moment). A read that fails or answers
    /// anything but an empty list is a change.
    /// </summary>
    private bool NoneChanged(string dir, long since)
    {
        try
        {
            return reader.Read("msgs", "changed", dir, new JsonObject { ["dir"] = dir, ["since"] = since }).Member("ids") is { IsArray: true } ids && ids.AsArray().Count == 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Lists the keys of <paramref name="dir"/> into <paramref name="keys"/>; false when the read failed, so nothing is held for it.</summary>
    private bool ListKeys(string dir, List<string> keys)
    {
        JsonValue reply;
        try
        {
            reply = reader.Read("msgs", "keys", dir, new JsonObject { ["dir"] = dir });
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (reply.Member("ids") is { IsArray: true } ids)
        {
            keys.AddRange(ids.AsArray().Where(static i => i.IsText).Take(MaxExchangesPerDir).Select(i => KeyOf(dir, i.AsString())));
        }

        return true;
    }

    /// <summary>The longest <c>preview</c> of an exchange: its first characters, so a row can say what the request is about without the excerpt.</summary>
    public const int PreviewChars = 160;

    /// <summary>The slice body: the header fields, the <c>preview</c> and the byte sizes of what <c>msgs.read</c> answered. The excerpts are in the <c>exchange-body</c> slice.</summary>
    internal static JsonObject BodyOf(string dir, string id, JsonValue exchange)
    {
        var hasResponse = HasResponse(exchange);
        var body = new JsonObject(32)
        {
            ["dir_id"] = ActivityLane.DirId(dir),
            ["id"] = id,
            ["slug"] = Field(exchange, "slug"),
            ["class"] = MessageClass.OfExchange(exchange.Member("slug") is { IsText: true } slug ? slug.AsString() : null),
            // A row leads with the title, never a raw slug: an exchange with none reads its slug with spaces, and a one-word slug stays the client's to show
            ["title"] = exchange.Member("title") is { IsText: true } title ? HierarchyModule.ChecklistTitle(title.AsString())
                : exchange.Member("slug") is { IsText: true } named && HierarchyModule.ChecklistTitle(named.AsString()) is var read && read != named.AsString() ? read : JsonValue.Null,
            ["title_source"] = Field(exchange, "title_source"),
            ["to"] = Field(exchange, "to"),
            ["from"] = Field(exchange, "from"),
            ["to_name"] = Field(exchange, "to_name"),
            ["from_name"] = Field(exchange, "from_name"),
            ["team"] = Field(exchange, "team"),
            ["parent"] = Field(exchange, "parent"),
            ["created"] = Field(exchange, "created"),
            ["response_created"] = hasResponse ? Field(exchange, "response_created") : JsonValue.Null,
            ["has_request"] = true,
            ["has_response"] = hasResponse,
            ["open"] = IsOpen(exchange),
            ["withdrawn"] = hasResponse && exchange.Member("withdrawn") is { IsBool: true } withdrawn && withdrawn.AsBool(),
            ["closed"] = hasResponse && exchange.Member("closed") is { IsBool: true } closed && closed.AsBool(),
            ["closed_by"] = hasResponse ? Field(exchange, "closed_by") : JsonValue.Null,
            ["closed_reason"] = hasResponse ? Field(exchange, "closed_reason") : JsonValue.Null,
            ["preview"] = PreviewOf(Field(exchange, "request_excerpt")),
            ["request_bytes"] = Bytes(exchange, "request_bytes"),
            ["response_bytes"] = Bytes(exchange, "response_bytes"),
            ["excerpt_truncated"] = exchange.Member("excerpt_truncated") is { IsBool: true } t && t.AsBool(),
            ["eta"] = Field(exchange, "eta"),
            ["eta_facts"] = !hasResponse && exchange.Member("eta_facts") is { IsMap: true } facts ? facts : JsonValue.Null,
        };
        if (exchange.Member("item") is { IsText: true } item)
        {
            body["item"] = item;
        }

        if (exchange.Member("error") is { IsText: true } error)
        {
            body["error"] = error;
        }

        return body;
    }

    /// <summary>The first <see cref="PreviewChars"/> characters of <paramref name="excerpt"/> with each run of whitespace made one space, cut between characters; null when there is no excerpt.</summary>
    internal static JsonValue PreviewOf(JsonValue excerpt)
    {
        if (!excerpt.IsText)
        {
            return JsonValue.Null;
        }

        // ah2-1: what `Regex.Replace(text, @"[\f\n\r\t\v\u0085\p{Z}]+", " ").Trim()` then the cut gives, made from the head of the text alone: the flattened
        // text is walked one character at a time and nothing past the preview is kept
        Span<char> kept = stackalloc char[PreviewChars];
        var at = 0;      // the place in the flattened, left-trimmed text of the next character
        var solid = 0;   // the length of that text up to its last character that Trim() keeps
        var inRun = false;
        foreach (var raw in excerpt.AsString())
        {
            var run = IsFlattened(raw);
            if (run && inRun)
            {
                continue;
            }

            inRun = run;
            var c = run ? ' ' : raw;
            var white = char.IsWhiteSpace(c);
            if (at == 0 && white)
            {
                continue; // Trim() drops it from the front
            }

            if (at >= PreviewChars)
            {
                if (white)
                {
                    at++;
                    continue;
                }

                // the trimmed text is longer than a preview: cut between characters
                return new string(kept[..(char.IsHighSurrogate(kept[PreviewChars - 1]) ? PreviewChars - 1 : PreviewChars)]);
            }

            kept[at++] = c;
            if (!white)
            {
                solid = at;
            }
        }

        return new string(kept[..solid]);
    }

    /// <summary>Whether <paramref name="c"/> is white space a preview flattens: the class <c>[\f\n\r\t\v\u0085\p{Z}]</c>.</summary>
    private static bool IsFlattened(char c) =>
        c is '\f' or '\n' or '\r' or '\t' or '\v' or '\u0085'
        || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.SpaceSeparator or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

    private static JsonValue Bytes(JsonValue exchange, string name) => exchange.Member(name) is { IsNumber: true } n ? n : JsonValue.Null;

    /// <summary>The latest of the request's and the response's <c>created</c> stamps in epoch ms; 0 when it has none.</summary>
    internal static long AsOf(JsonValue exchange) => Math.Max(EpochMs(Field(exchange, "created")), EpochMs(Field(exchange, "response_created")));

    private static bool HasResponse(JsonValue exchange) => exchange.Member("has_response") is { IsBool: true } r && r.AsBool();

    /// <summary>No response yet, or one holding only partial reports (<c>msg fill --part</c>): the request is still open.</summary>
    private static bool IsOpen(JsonValue exchange) => !HasResponse(exchange) || exchange.Member("partial") is { IsBool: true } p && p.AsBool();

    private static JsonValue Field(JsonValue exchange, string name) =>
        exchange.Member(name) is { IsText: true } value ? value : JsonValue.Null;

    private static long EpochMs(JsonValue stamp) =>
        stamp.IsText && DateTimeOffset.TryParse(stamp.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) ? when.ToUnixTimeMilliseconds() : 0;
}

/// <summary>
/// One per-directory exchange list, composed from the messages lane's records: <c>list:exchanges:dir:&lt;dirId&gt;</c> or
/// <c>list:feed:dir:&lt;dirId&gt;</c>, one composer for each prefix. It holds the ids of a directory's exchanges, newest first, and
/// nothing else (membership and order). A directory with no exchange has no list.
/// </summary>
internal sealed class ExchangeListComposer(string slicePrefix) : ISliceComposer
{
    public const string ExchangesPrefix = "list:exchanges:dir";
    public const string FeedPrefix = "list:feed:dir";

    public string SlicePrefix { get; } = slicePrefix;

    public IReadOnlyList<string> Lanes { get; } = [MessagesLane.LaneName];

    private string IdOf(string dirId) => SlicePrefix + ":" + dirId;

    public IReadOnlyList<string> Affected(LaneKey changed, LaneRecord? previous, LaneRecord? current, IRecordView records) =>
        [IdOf(ActivityLane.DirId(MessagesLane.Split(changed.Key).Dir))];

    public IReadOnlyList<string> AllIds(IRecordView records) =>
        [.. records.Keys(MessagesLane.LaneName).Select(static k => ActivityLane.DirId(MessagesLane.Split(k).Dir)).Distinct(StringComparer.Ordinal).Select(IdOf)];

    public SliceChange Compose(string sliceId, IRecordView records)
    {
        var head = SlicePrefix + ":";
        var dirId = sliceId.StartsWith(head, StringComparison.Ordinal) ? sliceId[head.Length..] : throw new ArgumentException("not an exchange list of " + SlicePrefix, nameof(sliceId));
        var ids = new JsonArray();
        var asOf = 0L;
        string? lastDir = null;
        var lastMatches = false;
        foreach (var key in records.Keys(MessagesLane.LaneName).Reverse())
        {
            // The keys are ordered by directory, so the directory's id is worked out once per run of keys and a record is opened only for a match.
            var at = key.IndexOf(MessagesLane.Separator, StringComparison.Ordinal);
            if (lastDir is null || !key.AsSpan(0, at).SequenceEqual(lastDir))
            {
                lastDir = key[..at];
                lastMatches = ActivityLane.DirId(lastDir) == dirId;
            }

            if (!lastMatches || records.Get(MessagesLane.LaneName, key) is not { } record)
            {
                continue;
            }

            ids.Add(key[(at + 1)..]);
            asOf = Math.Max(asOf, record.AsOfMs);
        }

        return ids.Count == 0
            ? new SliceChange(sliceId, null)
            : new SliceChange(sliceId, JsonText.ToUtf8(JsonValue.FromObject(new JsonObject { ["dir_id"] = dirId, ["ids"] = ids }), 0), asOf);
    }
}
