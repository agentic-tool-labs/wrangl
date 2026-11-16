using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Hooks.Activity;

/// <summary>Reads what a session's own transcript says happened to one of its SendMessage calls.</summary>
internal static class RelayTranscript
{
    private const int ReasonChars = 200;

    private const int FirstWindowBytes = 256 * 1024;

    private const int WindowGrowth = 8;

    /// <summary>
    /// The result of the tool call <paramref name="toolUseId"/>: <see cref="RelayResult.Failed"/> for an error result (a call a later
    /// gate or the person denied is one), <see cref="RelayResult.Sent"/> when the result says the message reached the inbox, and
    /// <see cref="RelayResult.Unknown"/> when the transcript has no result for it or it cannot be read. The result row is among the
    /// newest rows, so the tail is read in growing windows and the whole file only when the row is not there at all.
    /// </summary>
    public static RelayResult ResultOf(IFileReader files, string path, string toolUseId, out string? reason)
    {
        reason = null;
        if (path.Length == 0 || toolUseId.Length == 0 || !files.Exists(path))
        {
            return RelayResult.Unknown;
        }

        var result = RelayResult.Unknown;
        string? why = null;
        _ = Windowed(files, path, tail =>
        {
            result = ResultIn(tail, toolUseId, out why);
            return result != RelayResult.Unknown;
        });
        reason = why;
        return result;
    }

    /// <summary>
    /// Whether the transcript shows a SendMessage to <paramref name="to"/> whose message carries the tag of mail entry
    /// <paramref name="entry"/> and whose result says it reached the inbox. The tag is <c>[ah-entry &lt;id&gt;</c> followed by a space
    /// or a closing bracket, so an entry whose id merely starts with this one's does not count. A send stamped before
    /// <paramref name="notBeforeMs"/> (Unix milliseconds) cannot be the relay of an obligation created after it and is not counted, and
    /// the window stops widening once it holds rows older than that.
    /// </summary>
    public static bool SentTo(IFileReader files, string path, string to, string entry, double? notBeforeMs = null)
    {
        if (path.Length == 0 || to.Length == 0 || entry.Length == 0 || !files.Exists(path))
        {
            return false;
        }

        return Windowed(
            files,
            path,
            tail => SentIn(tail, to, entry, notBeforeMs),
            tail => notBeforeMs is { } bound && OldestStampMs(tail) is { } oldest && oldest < bound);
    }

    private const int StampProbeLines = 20;

    /// <summary>The stamp of the first row of the window that has one, or null when none of its first rows does.</summary>
    private static double? OldestStampMs(FileTail tail)
    {
        var lines = System.Text.Encoding.UTF8.GetString(tail.Bytes).Split('\n');
        for (var i = tail.StartsMidFile ? 1 : 0; i < lines.Length && i < StampProbeLines; i++)
        {
            if (TryParse(lines[i], out var row) && StampMs(row) is { } ms)
            {
                return ms;
            }
        }

        return null;
    }

    private static double? StampMs(JsonValue row) =>
        row.IsMap && row.Member("timestamp") is { IsText: true } stamp && Ah.Core.Time.Timestamps.TryParseMs(stamp.AsString(), out var ms) ? ms : null;

    /// <summary>
    /// Runs <paramref name="found"/> over the newest bytes of the file in growing windows until it holds, the whole file has been read,
    /// or <paramref name="covered"/> says the window already reaches as far back as the question needs.
    /// </summary>
    private static bool Windowed(IFileReader files, string path, Func<FileTail, bool> found, Func<FileTail, bool>? covered = null)
    {
        var window = FirstWindowBytes;
        while (true)
        {
            FileTail tail;
            try
            {
                tail = files.ReadTail(path, window);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            if (found(tail))
            {
                return true;
            }

            if (!tail.StartsMidFile || covered?.Invoke(tail) == true)
            {
                return false;
            }

            window = window > int.MaxValue / WindowGrowth ? int.MaxValue : window * WindowGrowth;
        }
    }

    private static bool SentIn(FileTail tail, string to, string entry, double? notBeforeMs)
    {
        var lines = System.Text.Encoding.UTF8.GetString(tail.Bytes).Split('\n');
        var tag = "[ah-entry " + entry;
        var calls = new HashSet<string>(StringComparer.Ordinal);
        for (var i = tail.StartsMidFile ? 1 : 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains(tag, StringComparison.Ordinal) || !TryParse(lines[i], out var row))
            {
                continue;
            }

            if (notBeforeMs is { } bound && StampMs(row) is { } stamped && stamped < bound)
            {
                continue;
            }

            foreach (var block in BlocksOf(row))
            {
                if (block.Member("type") is { IsText: true } type && type.AsString() == "tool_use"
                    && block.Member("name") is { IsText: true } name && name.AsString() == "SendMessage"
                    && block.Member("id") is { IsText: true } id
                    && block.Member("input") is { IsMap: true } input
                    && input.Member("to") is { IsText: true } target && target.AsString() == to
                    && input.Member("message") is { IsText: true } message && CarriesTag(message.AsString(), tag))
                {
                    _ = calls.Add(id.AsString());
                }
            }
        }

        if (calls.Count == 0)
        {
            return false;
        }

        for (var i = tail.StartsMidFile ? 1 : 0; i < lines.Length; i++)
        {
            if (!calls.Any(id => lines[i].Contains(id, StringComparison.Ordinal)) || !TryParse(lines[i], out var row))
            {
                continue;
            }

            foreach (var block in BlocksOf(row))
            {
                if (block.Member("type") is { IsText: true } type && type.AsString() == "tool_result"
                    && block.Member("tool_use_id") is { IsText: true } id && calls.Contains(id.AsString()))
                {
                    var result = RelayResults.Of(block, out _);
                    if (result == RelayResult.Unknown && row.Member("toolUseResult") is { } structured)
                    {
                        result = RelayResults.Of(structured, out _);
                    }

                    if (result == RelayResult.Sent)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool CarriesTag(string message, string tag)
    {
        for (var at = message.IndexOf(tag, StringComparison.Ordinal); at >= 0; at = message.IndexOf(tag, at + 1, StringComparison.Ordinal))
        {
            var end = at + tag.Length;
            if (end < message.Length && message[end] is ' ' or ']')
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParse(string line, out JsonValue row)
    {
        try
        {
            row = JsonText.Parse(line);
            return true;
        }
        catch (JsonParseException)
        {
            row = default;
            return false;
        }
    }

    private static IEnumerable<JsonValue> BlocksOf(JsonValue row) =>
        row.Member("message")?.Member("content") is { IsArray: true } blocks ? blocks.AsArray().Where(static b => b.IsMap) : [];

    private static RelayResult ResultIn(FileTail tail, string toolUseId, out string? reason)
    {
        reason = null;
        var lines = System.Text.Encoding.UTF8.GetString(tail.Bytes).Split('\n');
        for (var i = tail.StartsMidFile ? 1 : 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains(toolUseId, StringComparison.Ordinal))
            {
                continue;
            }

            JsonValue row;
            try
            {
                row = JsonText.Parse(line);
            }
            catch (JsonParseException)
            {
                continue;
            }

            if (row.Member("message")?.Member("content") is not { IsArray: true } blocks)
            {
                continue;
            }

            foreach (var block in blocks.AsArray())
            {
                if (!block.IsMap || block.Member("type") is not { IsText: true } type || type.AsString() != "tool_result"
                    || block.Member("tool_use_id") is not { IsText: true } id || id.AsString() != toolUseId)
                {
                    continue;
                }

                var result = RelayResults.Of(block, out reason);
                if (result == RelayResult.Unknown && row.Member("toolUseResult") is { } structured)
                {
                    result = RelayResults.Of(structured, out reason);
                }

                if (reason is { Length: > ReasonChars })
                {
                    reason = reason[..ReasonChars];
                }

                return result;
            }
        }

        return RelayResult.Unknown;
    }
}
