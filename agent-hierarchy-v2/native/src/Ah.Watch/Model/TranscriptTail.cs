using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>Token counts of one assistant turn, from <c>message.usage</c>.</summary>
internal readonly record struct TranscriptUsage(long Input, long Output, long CacheCreation, long CacheRead)
{
    public static TranscriptUsage operator +(TranscriptUsage a, TranscriptUsage b) =>
        new(a.Input + b.Input, a.Output + b.Output, a.CacheCreation + b.CacheCreation, a.CacheRead + b.CacheRead);

    public JsonValue ToJson() => JsonValue.FromObject(new JsonObject
    {
        ["input_tokens"] = Input,
        ["output_tokens"] = Output,
        ["cache_creation_input_tokens"] = CacheCreation,
        ["cache_read_input_tokens"] = CacheRead,
    });
}

/// <summary>
/// One content block of a transcript entry, whitelisted: sizes only for tool input, tool results and thinking. A tool result also carries the
/// newlines of its content (<see cref="Lines"/>); an Edit, MultiEdit or Write tool use carries the lines it adds and deletes (<see cref="DiffStat"/>).
/// </summary>
internal sealed record TranscriptBlock(string Kind, string? Id, string? Name, bool? IsError, string? Excerpt, int Bytes)
{
    public int Lines { get; init; }

    public (int Add, int Del)? DiffStat { get; init; }

    /// <summary>Whether the excerpt stops short of the text.</summary>
    public bool Cut { get; init; }

    /// <summary>This block's display row: what kind of line it is and where its text is, never a copy of it. A tool and its result pair on the served id, in whatever pages a client holds.</summary>
    public JsonValue RowJson(int index, bool turnStart)
    {
        var o = new JsonObject { ["index"] = index };
        switch (Kind)
        {
            case "text":
                o["kind"] = !turnStart ? "text" : (Excerpt ?? string.Empty).StartsWith(BriefPrefix, StringComparison.Ordinal) ? "brief" : "turn";
                o["cut"] = Cut;
                break;
            case "tool_use":
                o["kind"] = "tool";
                o["id"] = Id ?? JsonValue.Null;
                break;
            case "tool_result":
                o["kind"] = "result";
                o["tool_use_id"] = Id ?? JsonValue.Null;
                o["error"] = IsError ?? false;
                o["lines"] = Lines;
                break;
            case "thinking":
                o["kind"] = "thinking";
                break;
            default:
                o["kind"] = "other";
                break;
        }

        return JsonValue.FromObject(o);
    }

    private const string BriefPrefix = "[hierarchy-";

    public JsonValue ToJson()
    {
        var o = new JsonObject { ["kind"] = Kind };
        switch (Kind)
        {
            case "tool_use":
                o["id"] = Id ?? JsonValue.Null;
                o["name"] = Name ?? JsonValue.Null;
                if (DiffStat is { } diff)
                {
                    o["diff_stat"] = JsonValue.FromObject(new JsonObject { ["add"] = diff.Add, ["del"] = diff.Del });
                }

                break;
            case "tool_result":
                o["tool_use_id"] = Id ?? JsonValue.Null;
                o["is_error"] = IsError ?? false;
                o["bytes"] = Bytes;
                o["lines"] = Lines;
                break;
            case "text":
                o["excerpt"] = Excerpt ?? string.Empty;
                break;
            default:
                o["bytes"] = Bytes;
                break;
        }

        return JsonValue.FromObject(o);
    }
}

/// <summary>
/// One transcript line, whitelisted. <see cref="TimestampMs"/> is the entry's own timestamp (0 when it has none). <see cref="TurnStart"/> marks
/// the first entry of a turn; <see cref="DurationMs"/> is what a <c>system/turn_duration</c> entry reports.
/// </summary>
internal sealed record TranscriptEntry(long Offset, long Bytes, string Type, string? Subtype, IReadOnlyList<TranscriptBlock> Blocks, TranscriptUsage? Usage, bool Oversize, long TimestampMs)
{
    public bool TurnStart { get; init; }

    /// <summary>Who a user entry's text is for (<see cref="MessageClass"/>); null on every other entry.</summary>
    public string? Class { get; init; }

    public long? DurationMs { get; init; }

    /// <summary>The id of the message whose usage this entry carries. One message can span several lines, each repeating its usage.</summary>
    public string? MessageId { get; init; }

    /// <summary><paramref name="entries"/> with each message's usage counted once: only its first line is kept, and a line with no id always is.</summary>
    public static IEnumerable<TranscriptEntry> OncePerMessage(IEnumerable<TranscriptEntry> entries)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return entries.Where(e => e.MessageId is null || seen.Add(e.MessageId));
    }

    /// <summary>The placeholder row of a line longer than the line cap, which a walk steps over without reading it: <c>{offset, type:"oversize", bytes}</c>.</summary>
    public static TranscriptEntry Stub(long offset, long bytes) => new(offset, bytes, "oversize", null, [], null, false, 0);

    /// <summary>The entry as a row; <paramref name="turnMs"/> and <paramref name="turnTokens"/> are the length and tokens of the turn this entry starts, when the window reaches its end.</summary>
    public JsonValue ToJson(long? turnMs = null, long? turnTokens = null)
    {
        var o = new JsonObject { ["offset"] = Offset, ["bytes"] = Bytes, ["type"] = Type };
        if (Type == "oversize" && Blocks.Count == 0)
        {
            o["rows"] = JsonValue.FromArray(new JsonArray { JsonValue.FromObject(new JsonObject { ["kind"] = "large" }) });
            return JsonValue.FromObject(o);
        }

        if (Subtype is not null)
        {
            o["subtype"] = Subtype;
        }

        var blocks = new JsonArray();
        foreach (var b in Blocks)
        {
            blocks.Add(b.ToJson());
        }

        o["blocks"] = JsonValue.FromArray(blocks);
        var rows = new JsonArray();
        for (var i = 0; i < Blocks.Count; i++)
        {
            rows.Add(Blocks[i].RowJson(i, TurnStart));
        }

        o["rows"] = JsonValue.FromArray(rows);
        if (Usage is { } u)
        {
            o["usage"] = u.ToJson();
            if (MessageId is not null)
            {
                o["message_id"] = MessageId;
            }
        }

        if (Oversize)
        {
            o["oversize"] = true;
        }

        if (Class is not null)
        {
            o["class"] = Class;
        }

        if (TurnStart)
        {
            o["turn_start"] = true;
            o["turn_ms"] = turnMs is { } ms ? ms : JsonValue.Null;
            o["turn_tokens"] = turnTokens is { } tokens ? tokens : JsonValue.Null;
        }

        return JsonValue.FromObject(o);
    }
}

/// <summary>
/// The tail of one transcript file. It reads only byte windows (<see cref="Window"/>), never the whole file: a tail window when it starts,
/// then ranges from <see cref="NewestOffset"/> as the file grows. It keeps the newest <see cref="RingSize"/> entries, drops a partial first line,
/// reads a line longer than a window in further windows up to <see cref="LineCap"/> and turns a longer one into an <c>oversize</c> entry, and starts
/// over when the file shrinks or is replaced.
/// </summary>
internal sealed class TranscriptTail(IFileReader files, IHierarchyUsage usage)
{
    public const int Window = 256 * 1024;

    public const int LineCap = 4 * 1024 * 1024;

    public const int RingSize = 2000;

    private readonly TranscriptParser _parser = new(usage);
    private readonly List<TranscriptEntry> _ring = [];
    private IReadOnlyList<TranscriptEntry> _snapshot = [];
    private (ulong Device, ulong Inode)? _identity;
    private bool _started;
    private bool _skipToNewline;

    /// <summary>Byte offset just past the last complete line read.</summary>
    public long NewestOffset { get; private set; }

    public long Length { get; private set; }

    public int Resets { get; private set; }

    /// <summary>The ranged and tail reads made so far (for the count gates).</summary>
    public int Reads { get; private set; }

    public IReadOnlyList<TranscriptEntry> Entries => Volatile.Read(ref _snapshot);

    /// <summary>Why the last reset happened: <c>shrink</c> or <c>identity</c>; null when none yet.</summary>
    public string? LastResetReason { get; private set; }

    public long OldestHeldOffset => _ring.Count == 0 ? -1 : _ring[0].Offset;

    /// <summary>The newest held entry's own timestamp, else 0.</summary>
    public long AsOfMs => _ring.Count == 0 ? 0 : _ring[^1].TimestampMs;

    /// <summary>Brings the held tail up to date with <paramref name="path"/>. Returns false when the file is gone.</summary>
    public bool Advance(string path)
    {
        try
        {
            return AdvanceCore(path);
        }
        finally
        {
            Volatile.Write(ref _snapshot, [.. _ring]);
        }
    }

    private bool AdvanceCore(string path)
    {
        var stat = files.Stat(path);
        if (stat is not { } st)
        {
            return false;
        }

        var identity = (st.Device, st.Inode);
        if (!_started)
        {
            StartFresh(path, identity);
        }
        else if (st.Size < NewestOffset)
        {
            Reset(path, identity, "shrink");
        }
        else if (_identity is { } known && known != identity)
        {
            Reset(path, identity, "identity");
        }
        else
        {
            Follow(path);
        }

        return true;
    }

    /// <summary>The sum of the usage of entries at or after <paramref name="sinceMs"/> (0 when <paramref name="sinceMs"/> is 0, a turn with no known start).</summary>
    public TranscriptUsage UsageSince(long sinceMs)
    {
        var sum = default(TranscriptUsage);
        if (sinceMs <= 0)
        {
            return sum;
        }

        foreach (var e in TranscriptEntry.OncePerMessage(_ring.Where(e => e.Usage is not null && e.TimestampMs >= sinceMs)))
        {
            sum += e.Usage!.Value;
        }

        return sum;
    }

    private void Reset(string path, (ulong Device, ulong Inode) identity, string reason)
    {
        Resets++;
        LastResetReason = reason;
        _ring.Clear();
        StartFresh(path, identity);
    }

    private void StartFresh(string path, (ulong Device, ulong Inode) identity)
    {
        _started = true;
        _identity = identity;
        _skipToNewline = false;
        Reads++;
        var tail = files.ReadTail(path, Window);
        Length = tail.Length;
        var baseOffset = tail.Length - tail.Bytes.Length;
        var data = tail.Bytes;
        var from = 0;
        if (tail.StartsMidFile)
        {
            var nl = Array.IndexOf(data, (byte)'\n');
            if (nl < 0)
            {
                NewestOffset = tail.Length;
                _skipToNewline = true;
                return;
            }

            from = nl + 1;
        }

        NewestOffset = baseOffset + from;
        var pos = from;
        while (pos < data.Length)
        {
            var nl = Array.IndexOf(data, (byte)'\n', pos);
            if (nl < 0)
            {
                break;
            }

            Add(data.AsSpan(pos, nl - pos), baseOffset + pos, nl + 1 - pos, oversize: false);
            pos = nl + 1;
            NewestOffset = baseOffset + pos;
        }
    }

    private void Follow(string path)
    {
        while (true)
        {
            Reads++;
            var range = files.ReadRange(path, NewestOffset, Window);
            Length = range.Length;
            var buffer = range.Bytes;
            if (buffer.Length == 0)
            {
                return;
            }

            if (_skipToNewline)
            {
                var skip = Array.IndexOf(buffer, (byte)'\n');
                NewestOffset += skip < 0 ? buffer.Length : skip + 1;
                _skipToNewline = skip >= 0 ? false : _skipToNewline;
                continue;
            }

            var pos = 0;
            while (Array.IndexOf(buffer, (byte)'\n', pos) is var nl and >= 0)
            {
                Add(buffer.AsSpan(pos, nl - pos), NewestOffset + pos, nl + 1 - pos, oversize: false);
                pos = nl + 1;
            }

            NewestOffset += pos;
            if (pos > 0)
            {
                if (buffer.Length < Window)
                {
                    return;
                }

                continue;
            }

            if (buffer.Length < Window || !ReadLongLine(path, buffer))
            {
                return;
            }
        }
    }

    /// <summary>
    /// A line with no newline in a whole window: read on until its newline or <see cref="LineCap"/>. A line that ends at or past the cap becomes an
    /// oversize entry covering exactly its bytes; one that is still open at the cap is oversize up to there and the rest is skipped. Returns false
    /// when the line is not finished yet.
    /// </summary>
    private bool ReadLongLine(string path, byte[] first)
    {
        var start = NewestOffset;
        var buffer = first;
        while (true)
        {
            var nl = Array.IndexOf(buffer, (byte)'\n');
            if (nl >= 0)
            {
                if (nl < LineCap)
                {
                    Add(buffer.AsSpan(0, nl), start, nl + 1, oversize: false);
                }
                else
                {
                    Add([], start, nl + 1, oversize: true);
                }

                NewestOffset = start + nl + 1;
                return true;
            }

            if (buffer.Length >= LineCap)
            {
                Add([], start, buffer.Length, oversize: true);
                NewestOffset = start + buffer.Length;
                _skipToNewline = true;
                return true;
            }

            Reads++;
            var more = files.ReadRange(path, start + buffer.Length, Window);
            Length = more.Length;
            if (more.Bytes.Length == 0)
            {
                return false;
            }

            buffer = [.. buffer, .. more.Bytes];
        }
    }

    private void Add(ReadOnlySpan<byte> line, long offset, long bytes, bool oversize)
    {
        var entry = oversize ? new TranscriptEntry(offset, bytes, "other", null, [], null, true, 0) : _parser.Parse(line, offset, bytes);
        _ring.Add(entry);
        if (_ring.Count > RingSize)
        {
            _ring.RemoveRange(0, _ring.Count - RingSize);
        }
    }
}
