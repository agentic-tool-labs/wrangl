using System.Globalization;
using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch.Model;

/// <summary>
/// Turns one transcript line into a whitelisted <see cref="TranscriptEntry"/>. The tail that keeps the ring and the pager that reads older
/// pages both parse through this one class. Besides the whitelist it derives what the line already holds: <c>turn_start</c> (a user entry that
/// is not only tool results), the newline count of a tool result, the added and deleted lines of an Edit, MultiEdit or Write, and the
/// <c>durationMs</c> of a <c>system/turn_duration</c> entry. It parses no exit codes.
/// </summary>
internal sealed class TranscriptParser(IHierarchyUsage usage)
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.Ordinal) { "user", "assistant", "system", "summary" };

    private static readonly HashSet<string> DiffTools = new(StringComparer.Ordinal) { "Edit", "MultiEdit", "Write" };

    public TranscriptEntry Parse(ReadOnlySpan<byte> line, long offset, long bytes)
    {
        JsonValue parsed;
        try
        {
            parsed = JsonText.Parse(line);
        }
        catch (JsonParseException)
        {
            return new TranscriptEntry(offset, bytes, "other", null, [], null, false, 0);
        }

        if (!parsed.IsMap)
        {
            return new TranscriptEntry(offset, bytes, "other", null, [], null, false, 0);
        }

        var type = parsed.Member("type") is { IsText: true } t && KnownTypes.Contains(t.AsString()) ? t.AsString() : "other";
        var subtype = type == "system" && parsed.Member("subtype") is { IsText: true } s ? s.AsString() : null;
        var stamp = parsed.Member("timestamp") is { IsText: true } ts
            && DateTimeOffset.TryParse(ts.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            ? when.ToUnixTimeMilliseconds() : 0;
        TranscriptUsage? counted = null;
        if (usage.AssistantUsageOf(parsed) is { } turn)
        {
            counted = new TranscriptUsage(
                Count(turn.Usage, "input_tokens"),
                Count(turn.Usage, "output_tokens"),
                Count(turn.Usage, "cache_creation_input_tokens"),
                Count(turn.Usage, "cache_read_input_tokens"));
        }

        var blocks = BlocksOf(parsed);
        return new TranscriptEntry(offset, bytes, type, subtype, blocks, counted, false, stamp)
        {
            MessageId = counted is not null && parsed.Member("message")?.Member("id") is { IsText: true } messageId ? messageId.AsString() : null,
            TurnStart = type == "user" && blocks.Any(static b => b.Kind != "tool_result"),
            Class = type == "user" && blocks.FirstOrDefault(static b => b.Kind == "text") is { } first ? MessageClass.OfUserText(first.Excerpt ?? string.Empty) : null,
            DurationMs = type == "system" && subtype == "turn_duration" && parsed.Member("durationMs") is { IsNumber: true } d && double.IsFinite(d.AsNumber()) && d.AsNumber() >= 0
                ? (long)d.AsNumber() : null,
        };
    }

    private static long Count(JsonValue usage, string name) =>
        usage.Member(name) is { IsNumber: true } n && double.IsFinite(n.AsNumber()) && n.AsNumber() >= 0 ? (long)n.AsNumber() : 0;

    private static List<TranscriptBlock> BlocksOf(JsonValue entry)
    {
        var blocks = new List<TranscriptBlock>();
        var content = entry.Member("message")?.Member("content");
        if (content is { IsText: true } text)
        {
            blocks.Add(TextBlock(text.AsString()));
            return blocks;
        }

        if (content is not { IsArray: true } array)
        {
            return blocks;
        }

        foreach (var block in array.AsArray())
        {
            if (!block.IsMap || block.Member("type") is not { IsText: true } kind)
            {
                continue;
            }

            switch (kind.AsString())
            {
                case "tool_use":
                    var name = TextOf(block, "name");
                    blocks.Add(new TranscriptBlock("tool_use", TextOf(block, "id"), name, null, null, 0)
                    {
                        DiffStat = name is not null && DiffTools.Contains(name) ? DiffStatOf(name, block.Member("input")) : null,
                    });
                    break;
                case "tool_result":
                    blocks.Add(new TranscriptBlock("tool_result", TextOf(block, "tool_use_id"), null, block.Member("is_error") is { IsBool: true } e && e.AsBool(), null, SizeOf(block.Member("content")))
                    {
                        Lines = NewlinesIn(block.Member("content")),
                    });
                    break;
                case "text":
                    blocks.Add(TextBlock(TextOf(block, "text") ?? string.Empty));
                    break;
                case "thinking":
                    blocks.Add(new TranscriptBlock("thinking", null, null, null, null, Encoding.UTF8.GetByteCount(TextOf(block, "thinking") ?? string.Empty)));
                    break;
            }
        }

        return blocks;
    }

    private static string? TextOf(JsonValue o, string name) => o.Member(name) is { IsText: true } v ? v.AsString() : null;

    private static int SizeOf(JsonValue? content) => content is null ? 0 : Encoding.UTF8.GetByteCount(JsonText.Stringify(content.Value, 0));

    /// <summary>The newlines in a tool result's content: its text, or the text of each text item of an array.</summary>
    private static int NewlinesIn(JsonValue? content)
    {
        if (content is { IsText: true } text)
        {
            return text.AsString().Count(static c => c == '\n');
        }

        return content is { IsArray: true } array
            ? array.AsArray().Sum(static item => item.IsMap && TextOf(item, "text") is { } t ? t.Count(static c => c == '\n') : 0)
            : 0;
    }

    /// <summary>The lines added and deleted by an edit tool's input: new against old text for Edit and MultiEdit, the written content for Write (nothing deleted).</summary>
    private static (int Add, int Del)? DiffStatOf(string tool, JsonValue? input)
    {
        if (input is not { IsMap: true } map)
        {
            return null;
        }

        switch (tool)
        {
            case "Write":
                return (LinesOf(TextOf(map, "content")), 0);
            case "Edit":
                return (LinesOf(TextOf(map, "new_string")), LinesOf(TextOf(map, "old_string")));
            default:
                var add = 0;
                var del = 0;
                if (map.Member("edits") is { IsArray: true } edits)
                {
                    foreach (var edit in edits.AsArray().Where(static e => e.IsMap))
                    {
                        add += LinesOf(TextOf(edit, "new_string"));
                        del += LinesOf(TextOf(edit, "old_string"));
                    }
                }

                return (add, del);
        }
    }

    /// <summary>The lines of a text: 0 for none, else the newlines plus one for a last line with no newline after it.</summary>
    private static int LinesOf(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : text.Count(static c => c == '\n') + (text[^1] == '\n' ? 0 : 1);

    /// <summary>The removed and added lines of an edit tool's input, removed before added, per edit; null for a tool that edits nothing.</summary>
    internal static JsonArray? DiffLinesOf(string tool, JsonValue? input)
    {
        if (!DiffTools.Contains(tool))
        {
            return null;
        }

        var lines = new JsonArray();
        void Add(string kind, string? text)
        {
            foreach (var line in string.IsNullOrEmpty(text) ? [] : text.Split('\n'))
            {
                lines.Add(JsonValue.FromObject(new JsonObject { ["kind"] = kind, ["text"] = line }));
            }
        }

        void Edit(JsonValue edit)
        {
            Add("del", TextOf(edit, "old_string"));
            Add("add", TextOf(edit, "new_string"));
        }

        if (input is not { IsMap: true } map)
        {
            return lines;
        }

        if (tool == "Write")
        {
            Add("add", TextOf(map, "content"));
        }
        else if (tool == "Edit")
        {
            Edit(map);
        }
        else if (map.Member("edits") is { IsArray: true } edits)
        {
            foreach (var edit in edits.AsArray().Where(static e => e.IsMap))
            {
                Edit(edit);
            }
        }

        return lines;
    }

    private const int ExcerptCap = 2048;

    private static TranscriptBlock TextBlock(string text) =>
        new("text", null, null, null, Excerpt(text), 0) { Cut = Encoding.UTF8.GetByteCount(text) > ExcerptCap };

    private static string Excerpt(string text)
    {
        const int Cap = ExcerptCap;
        if (Encoding.UTF8.GetByteCount(text) <= Cap)
        {
            return text;
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var cut = Cap;
        while (cut > 0 && (bytes[cut] & 0xC0) == 0x80)
        {
            cut--;
        }

        return Encoding.UTF8.GetString(bytes, 0, cut);
    }
}
