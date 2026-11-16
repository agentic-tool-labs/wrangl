using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>Where the transcript pager gets a key's held tail and its file path.</summary>
internal interface ITranscriptSource
{
    /// <summary>The newest entries the lane holds for (directory id, subject), oldest first, and the file they came from; empty and null when the key is not leased.</summary>
    (IReadOnlyList<TranscriptEntry> Ring, string? Path) Held(string dirId, string subject);

    /// <summary>The validated transcript path of an unleased key, from its activity record; null when there is no record or no valid path.</summary>
    string? ColdPath(string dirId, string subject);
}

/// <summary>
/// The pager of <c>list:transcript:&lt;dirId&gt;/&lt;subject&gt;</c>. Rows are the entries as lane T's ring keeps them, newest first, with the
/// derived fields; the cursor is the byte offset of an entry. The newest pages come from the ring, older pages from <c>ReadRange</c> read
/// backwards, and a key that is not leased is served from the file alone, with no lease, no ring and nothing published. <c>expand=&lt;offset&gt;</c>
/// returns one entry's full block content from one ranged read of at most <see cref="ExpandCap"/> bytes.
/// </summary>
/// <param name="source">The lane's held tails and paths.</param>
/// <param name="files">The file reader the lane reads transcripts with.</param>
/// <param name="usage">The usage reader the shared parser takes.</param>
/// <param name="version">The publish counter of the published state.</param>
internal sealed class TranscriptPager(ITranscriptSource source, IFileReader files, IHierarchyUsage usage, Func<long> version) : IListPager
{
    public const string ListPrefix = "list:transcript:";

    public const int ExpandCap = 256 * 1024;

    private readonly TranscriptParser _parser = new(usage);

    public string Prefix => ListPrefix;

    public IReadOnlyCollection<string> Filters { get; } = ["expand"];

    public ValueTask<ListResult> PageAsync(ListRequest request, CancellationToken cancellation)
    {
        var key = request.SliceId[Prefix.Length..];
        var slash = key.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == key.Length - 1)
        {
            return ValueTask.FromResult<ListResult>(new ListResult.NotFound());
        }

        var (ring, heldPath) = source.Held(key[..slash], key[(slash + 1)..]);
        var path = heldPath ?? source.ColdPath(key[..slash], key[(slash + 1)..]);
        if (path is null)
        {
            return ValueTask.FromResult<ListResult>(new ListResult.NotFound());
        }

        try
        {
            return ValueTask.FromResult(request.Param("expand") is { } expand ? Expand(path, expand) : Serve(request, path, ring, cancellation));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ValueTask.FromResult<ListResult>(new ListResult.NotFound());
        }
    }

    // ---------------------------------------------------------------- pages

    private ListResult Serve(ListRequest request, string path, IReadOnlyList<TranscriptEntry> ring, CancellationToken cancellation)
    {
        if (!TryOffset(request.After, out var after) || !TryOffset(request.Before, out var before))
        {
            return new ListResult.Invalid("bad_cursor");
        }

        var reads = new Reads();
        List<TranscriptEntry>? ascending;
        if (after is { } older)
        {
            ascending = Older(path, ring, older, request.Limit, reads, cancellation);
        }
        else if (before is { } newer)
        {
            ascending = Newer(path, ring, newer, request.Limit, reads, cancellation);
        }
        else
        {
            ascending = Newest(path, ring, request.Limit, reads, cancellation);
        }

        if (ascending is null)
        {
            return new ListResult.Invalid("bad_cursor");
        }

        var rows = ascending.AsEnumerable().Reverse().ToList();
        var context = reads.Seen.Count == 0 ? ring : [.. ring.Concat(reads.Seen).DistinctBy(static e => e.Offset).OrderBy(static e => e.Offset)];
        var head = rows.Count > 0 ? rows[0] : null;
        var tail = rows.Count > 0 ? rows[^1] : null;
        var next = tail is not null ? (tail.Offset > 0 ? Cursor(tail.Offset) : null) : (after is { } a ? Cursor(a) : null);
        var prev = head is not null ? Cursor(head.Offset) : (before is { } b ? Cursor(b) : null);
        return new ListResult.Found(new ListPage(
            version(),
            null,
            head is null ? null : Cursor(head.Offset),
            tail is null ? null : Cursor(tail.Offset),
            [.. rows.Select(e => e.ToJson(TurnMsOf(e, context), TurnTokensOf(e, context)))],
            next,
            prev,
            reads.Count));
    }

    private static string Cursor(long offset) => ListCursor.Encode(offset.ToString(CultureInfo.InvariantCulture));

    private static bool TryOffset(string? cursor, out long? offset)
    {
        offset = null;
        if (cursor is null)
        {
            return true;
        }

        if (ListCursor.Decode(cursor, 1) is { } parts && long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            offset = value;
            return true;
        }

        return false;
    }

    /// <summary>The length of the turn <paramref name="entry"/> starts: the <c>durationMs</c> of the next <c>turn_duration</c> entry before the next turn, when the window holds one.</summary>
    private static long? TurnMsOf(TranscriptEntry entry, IReadOnlyList<TranscriptEntry> window)
    {
        if (!entry.TurnStart)
        {
            return null;
        }

        foreach (var e in window.Where(e => e.Offset > entry.Offset))
        {
            if (e.TurnStart)
            {
                return null;
            }

            if (e.DurationMs is { } ms)
            {
                return ms;
            }
        }

        return null;
    }

    /// <summary>The input and output tokens of the turn <paramref name="entry"/> starts, or null when the window stops before that turn ends.</summary>
    private static long? TurnTokensOf(TranscriptEntry entry, IReadOnlyList<TranscriptEntry> window)
    {
        if (!entry.TurnStart)
        {
            return null;
        }

        var tokens = 0L;
        foreach (var e in TranscriptEntry.OncePerMessage(window.Where(e => e.Offset > entry.Offset)))
        {
            if (e.TurnStart)
            {
                return tokens;
            }

            tokens += e.Usage is { } u ? u.Input + u.Output : 0;
            if (e.DurationMs is not null)
            {
                return tokens;
            }
        }

        return null;
    }

    private sealed class Reads
    {
        public int Count { get; private set; }

        public List<TranscriptEntry> Seen { get; } = [];

        public void Read() => Count++;

        public List<TranscriptEntry> Take(List<TranscriptEntry> entries)
        {
            Seen.AddRange(entries);
            return entries;
        }
    }

    /// <summary>The newest <paramref name="n"/> entries, oldest first.</summary>
    private List<TranscriptEntry> Newest(string path, IReadOnlyList<TranscriptEntry> ring, int n, Reads reads, CancellationToken cancellation)
    {
        if (ring.Count > 0)
        {
            var held = ring.TakeLast(n).ToList();
            return held.Count >= n || held[0].Offset == 0 ? held : [.. reads.Take(ReadOlder(path, held[0].Offset, n - held.Count, reads, cancellation) ?? []), .. held];
        }

        reads.Read();
        var tail = files.ReadTail(path, TranscriptTail.Window);
        var data = tail.Bytes;
        var baseOffset = tail.Length - data.Length;
        var pos = 0;
        if (tail.StartsMidFile)
        {
            var first = Array.IndexOf(data, (byte)'\n');
            if (first < 0)
            {
                return [];
            }

            pos = first + 1;
        }

        var entries = ParseLines(data, pos, baseOffset);
        var newest = reads.Take(entries).TakeLast(n).ToList();
        return newest.Count >= n || newest.Count == 0 || newest[0].Offset == 0 ? newest : [.. reads.Take(ReadOlder(path, newest[0].Offset, n - newest.Count, reads, cancellation) ?? []), .. newest];
    }

    /// <summary>The <paramref name="n"/> entries strictly older than <paramref name="cursor"/>, nearest first in the list and oldest first here; null when the cursor is not an entry start.</summary>
    private List<TranscriptEntry>? Older(string path, IReadOnlyList<TranscriptEntry> ring, long cursor, int n, Reads reads, CancellationToken cancellation)
    {
        var fromRing = ring.Where(e => e.Offset < cursor).ToList();
        if (fromRing.Count >= n)
        {
            return fromRing.TakeLast(n).ToList();
        }

        var boundary = fromRing.Count > 0 ? fromRing[0].Offset : cursor;
        var fromFile = ReadOlder(path, boundary, n - fromRing.Count, reads, cancellation);
        return fromFile is null ? null : [.. reads.Take(fromFile), .. fromRing];
    }

    /// <summary>The <paramref name="n"/> entries strictly newer than <paramref name="cursor"/> nearest to it, oldest first.</summary>
    private List<TranscriptEntry> Newer(string path, IReadOnlyList<TranscriptEntry> ring, long cursor, int n, Reads reads, CancellationToken cancellation)
    {
        if (ring.Count > 0 && cursor >= ring[0].Offset)
        {
            return ring.Where(e => e.Offset > cursor).Take(n).ToList();
        }

        var stopAt = ring.Count > 0 ? ring[0].Offset : long.MaxValue;
        var forward = reads.Take(ReadForward(path, cursor, n, stopAt, reads, cancellation));
        return forward.Count >= n ? forward : [.. forward, .. ring.Take(n - forward.Count)];
    }

    // ---------------------------------------------------------------- ranged reads

    private List<TranscriptEntry> ParseLines(byte[] data, int from, long baseOffset)
    {
        var entries = new List<TranscriptEntry>();
        var pos = from;
        while (Array.IndexOf(data, (byte)'\n', pos) is var nl and >= 0)
        {
            entries.Add(_parser.Parse(data.AsSpan(pos, nl - pos), baseOffset + pos, nl + 1 - pos));
            pos = nl + 1;
        }

        return entries;
    }

    /// <summary>
    /// Up to <paramref name="n"/> entries that end at <paramref name="end"/> (a line start), oldest first, read backwards in windows. Null when
    /// <paramref name="end"/> is not a line start. A line longer than <see cref="TranscriptTail.LineCap"/> is never buffered: its start is found by
    /// scanning back a window at a time, and it is returned as an <see cref="TranscriptEntry.Stub"/> that takes its place in the walk.
    /// </summary>
    private List<TranscriptEntry>? ReadOlder(string path, long end, int n, Reads reads, CancellationToken cancellation)
    {
        var collected = new List<TranscriptEntry>();
        var cursorEnd = end;
        var first = true;
        while (collected.Count < n && cursorEnd > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var start = Math.Max(0, cursorEnd - TranscriptTail.Window);
            reads.Read();
            var data = files.ReadRange(path, start, (int)(cursorEnd - start)).Bytes;
            if (data.Length != cursorEnd - start)
            {
                break;
            }

            if (first && data[^1] != (byte)'\n')
            {
                return null;
            }

            first = false;
            var from = 0;
            if (start > 0)
            {
                var nl = Array.IndexOf(data, (byte)'\n', 0, data.Length - 1);
                if (nl < 0)
                {
                    var lineStart = LineStartBefore(path, start, reads, cancellation);
                    var length = cursorEnd - lineStart;
                    var line = length > TranscriptTail.LineCap ? TranscriptEntry.Stub(lineStart, length) : ReadLine(path, lineStart, length, reads);
                    if (line is null)
                    {
                        break;
                    }

                    collected.Insert(0, line);
                    cursorEnd = lineStart;
                    continue;
                }

                from = nl + 1;
            }

            collected.InsertRange(0, ParseLines(data, from, start));
            cursorEnd = start + from;
        }

        return collected.Count <= n ? collected : collected.GetRange(collected.Count - n, n);
    }

    /// <summary>The offset just past the last newline before <paramref name="position"/> (0 when there is none), found by reading back a window at a time.</summary>
    private long LineStartBefore(string path, long position, Reads reads, CancellationToken cancellation)
    {
        var end = position;
        while (end > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var begin = Math.Max(0, end - TranscriptTail.Window);
            reads.Read();
            var chunk = files.ReadRange(path, begin, (int)(end - begin)).Bytes;
            if (chunk.Length != end - begin)
            {
                return 0;
            }

            var nl = chunk.AsSpan().LastIndexOf((byte)'\n');
            if (nl >= 0)
            {
                return begin + nl + 1;
            }

            end = begin;
        }

        return 0;
    }

    /// <summary>The one entry of the line of <paramref name="length"/> bytes at <paramref name="offset"/>, or null when the file no longer holds it.</summary>
    private TranscriptEntry? ReadLine(string path, long offset, long length, Reads reads)
    {
        reads.Read();
        var data = files.ReadRange(path, offset, (int)length).Bytes;
        return data.Length == length && ParseLines(data, 0, offset) is [var entry] ? entry : null;
    }

    /// <summary>Up to <paramref name="n"/> entries after the entry at <paramref name="cursor"/>, oldest first, stopping before <paramref name="stopAt"/>.</summary>
    private List<TranscriptEntry> ReadForward(string path, long cursor, int n, long stopAt, Reads reads, CancellationToken cancellation)
    {
        var entries = new List<TranscriptEntry>();
        var pos = LineEndAt(path, cursor, reads, cancellation);
        while (pos is { } at && entries.Count < n && at < stopAt)
        {
            cancellation.ThrowIfCancellationRequested();
            reads.Read();
            var data = files.ReadRange(path, at, TranscriptTail.Window).Bytes;
            var parsed = ParseLines(data, 0, at).TakeWhile(e => e.Offset < stopAt).Take(n - entries.Count).ToList();
            if (parsed.Count > 0)
            {
                entries.AddRange(parsed);
                pos = at + parsed.Sum(static e => e.Bytes);
                continue;
            }

            if (data.Length < TranscriptTail.Window || LineEndAt(path, at, reads, cancellation) is not { } end)
            {
                break;
            }

            var length = end - at;
            var line = length > TranscriptTail.LineCap ? TranscriptEntry.Stub(at, length) : ReadLine(path, at, length, reads);
            if (line is null)
            {
                break;
            }

            entries.Add(line);
            pos = end;
        }

        return entries;
    }

    /// <summary>The offset just past the line that starts at <paramref name="start"/>, found a window at a time without keeping the line; null when the line is not finished.</summary>
    private long? LineEndAt(string path, long start, Reads reads, CancellationToken cancellation)
    {
        var pos = start;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            reads.Read();
            var data = files.ReadRange(path, pos, TranscriptTail.Window).Bytes;
            var nl = Array.IndexOf(data, (byte)'\n');
            if (nl >= 0)
            {
                return pos + nl + 1;
            }

            if (data.Length < TranscriptTail.Window)
            {
                return null;
            }

            pos += data.Length;
        }
    }

    // ---------------------------------------------------------------- expand

    private ListResult Expand(string path, string text)
    {
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return new ListResult.Invalid("bad_offset");
        }

        var back = offset > 0 ? 1 : 0;
        var data = files.ReadRange(path, offset - back, ExpandCap + 1 + back).Bytes;
        if (data.Length <= back || (back == 1 && data[0] != (byte)'\n'))
        {
            return new ListResult.Invalid("bad_offset");
        }

        data = data[back..];
        var nl = Array.IndexOf(data, (byte)'\n');
        if (nl < 0 && data.Length <= ExpandCap)
        {
            return new ListResult.Invalid("bad_offset");
        }

        var truncated = nl < 0;
        var body = new JsonObject { ["offset"] = offset };
        JsonValue? line = null;
        if (!truncated)
        {
            try
            {
                line = JsonText.Parse(data.AsSpan(0, nl));
            }
            catch (JsonParseException)
            {
                line = null;
            }
        }

        var entry = line is { IsMap: true } map ? map : (JsonValue?)null;
        var type = entry is { } e && e.Member("type") is { IsText: true } t && KnownTypes.Contains(t.AsString()) ? t.AsString() : "other";
        body["type"] = type;
        if (entry is { } s && type == "system" && s.Member("subtype") is { IsText: true } sub)
        {
            body["subtype"] = sub.AsString();
        }

        body["blocks"] = JsonValue.FromArray(entry is { } full ? FullBlocks(full) : new JsonArray());
        body["truncated"] = truncated;
        return new ListResult.Document(JsonValue.FromObject(body));
    }

    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal) { "user", "assistant", "system", "summary" };

    private static JsonArray FullBlocks(JsonValue entry)
    {
        var blocks = new JsonArray();
        var content = entry.Member("message")?.Member("content");
        if (content is { IsText: true } text)
        {
            blocks.Add(JsonValue.FromObject(new JsonObject { ["kind"] = "text", ["text"] = text.AsString() }));
            return blocks;
        }

        if (content is not { IsArray: true } array)
        {
            return blocks;
        }

        foreach (var block in array.AsArray().Where(static b => b.IsMap && b.Member("type") is { IsText: true }))
        {
            var kind = block.Member("type")!.Value.AsString();
            var o = new JsonObject { ["kind"] = kind };
            switch (kind)
            {
                case "tool_use":
                    o["id"] = Pass(block, "id");
                    o["name"] = Pass(block, "name");
                    o["input"] = Pass(block, "input");
                    if (block.Member("name") is { IsText: true } tool && TranscriptParser.DiffLinesOf(tool.AsString(), block.Member("input")) is { } diff)
                    {
                        o["diff"] = JsonValue.FromArray(diff);
                    }

                    break;
                case "tool_result":
                    o["tool_use_id"] = Pass(block, "tool_use_id");
                    o["is_error"] = block.Member("is_error") is { IsBool: true } err && err.AsBool();
                    o["content"] = Pass(block, "content");
                    break;
                case "text":
                    o["text"] = Pass(block, "text");
                    break;
                case "thinking":
                    o["text"] = Pass(block, "thinking");
                    break;
                default:
                    continue;
            }

            blocks.Add(JsonValue.FromObject(o));
        }

        return blocks;
    }

    private static JsonValue Pass(JsonValue block, string name) => block.Member(name) ?? JsonValue.Null;
}
