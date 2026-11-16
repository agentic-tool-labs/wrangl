using Ah.Abstractions;
using Ah.Core.Files;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.State;

/// <summary>One row of a domain's executed-op log: its parsed value, and what a repeat of the op id must answer.</summary>
internal sealed record ExecutedRow(string OpId, JsonValue Source, bool IsError, JsonValue Result, string? ErrorCode, string ErrorMessage);

/// <summary>
/// <c>.executed-ops.jsonl</c> of one state directory: the rows that make a write idempotent per <c>op_id</c>. The row
/// shapes, the 256-byte message cut and the rotation are the Node writer's.
/// </summary>
internal sealed class ExecutedOps(IFileReader files, IRawFileWriter writer, IRecordAppender appender, int processId)
{
    internal const string FileName = ".executed-ops.jsonl";
    internal const int Keep = 256;
    internal const int RotateRows = 512;
    internal const int MessageMaxBytes = 256;

    /// <summary>The log's rows, oldest first. A torn or non-object line is skipped.</summary>
    internal IReadOnlyList<ExecutedRow> Read(string directory)
    {
        var path = NodePath.Join(directory, FileName);
        var rows = new List<ExecutedRow>();
        byte[] bytes;
        try
        {
            if (!files.Exists(path))
            {
                return rows;
            }

            bytes = files.ReadBytes(path);
        }
        catch (IOException)
        {
            return rows;
        }

        var start = 0;
        for (var i = 0; i <= bytes.Length; i++)
        {
            if (i < bytes.Length && bytes[i] != (byte)'\n')
            {
                continue;
            }

            if (i > start && TryParse(bytes.AsSpan(start, i - start), out var row))
            {
                rows.Add(row);
            }

            start = i + 1;
        }

        return rows;
    }

    /// <summary>
    /// Appends <paramref name="row"/> to the log whose current rows are <paramref name="rows"/>. Past <see cref="RotateRows"/>
    /// the log is rewritten (temporary file and rename) to its newest <see cref="Keep"/> rows including this one.
    /// </summary>
    internal void Record(string directory, IReadOnlyList<ExecutedRow> rows, JsonObject row)
    {
        var path = NodePath.Join(directory, FileName);
        if (rows.Count + 1 <= RotateRows)
        {
            appender.Append(path, Line(JsonValue.FromObject(row)));
            return;
        }

        var kept = new List<byte>();
        foreach (var old in rows.Skip(rows.Count - (Keep - 1)))
        {
            kept.AddRange(Line(old.Source));
        }

        kept.AddRange(Line(JsonValue.FromObject(row)));
        writer.WriteAtomic(path, TmpNames.For(TmpNameStyle.PidTmp, path, processId), kept.ToArray());
    }

    internal static JsonObject SuccessRow(string opId, string at, JsonValue result)
    {
        var row = new JsonObject();
        row["op_id"] = opId;
        row["at"] = at;
        row["result"] = result;
        return row;
    }

    internal static JsonObject ErrorRow(string opId, string at, string? code, string message)
    {
        var error = new JsonObject();
        error["code"] = code is null ? JsonValue.Null : JsonValue.FromString(code);
        error["message"] = CutBytes(message, MessageMaxBytes);
        var row = new JsonObject();
        row["op_id"] = opId;
        row["at"] = at;
        row["error"] = error;
        return row;
    }

    /// <summary><paramref name="text"/> cut to at most <paramref name="max"/> bytes of UTF-8; a character the cut would split is dropped.</summary>
    internal static string CutBytes(string text, int max)
    {
        var bytes = Utf8Text.Encode(text);
        if (bytes.Length <= max)
        {
            return text;
        }

        var end = max;
        while (end > 0 && (bytes[end] & 0xC0) == 0x80)
        {
            end--;
        }

        return Utf8Text.Decode(bytes.AsSpan(0, end));
    }

    private static byte[] Line(JsonValue value)
    {
        var text = JsonText.ToUtf8(value, 0);
        var line = new byte[text.Length + 1];
        text.CopyTo(line, 0);
        line[^1] = (byte)'\n';
        return line;
    }

    private static bool TryParse(ReadOnlySpan<byte> line, out ExecutedRow row)
    {
        row = null!;
        JsonValue value;
        try
        {
            value = JsonText.Parse(line);
        }
        catch (JsonParseException)
        {
            return false;
        }

        if (!value.IsMap || value.Member("op_id") is not { IsText: true } id)
        {
            return false;
        }

        if (value.Member("error") is { IsMap: true } error)
        {
            var code = error.Member("code") is { IsText: true } c ? c.AsString() : null;
            var message = error.Member("message") is { IsText: true } m ? m.AsString() : string.Empty;
            row = new ExecutedRow(id.AsString(), value, true, JsonValue.Null, code, message);
            return true;
        }

        row = new ExecutedRow(id.AsString(), value, false, value.Member("result") ?? JsonValue.Null, null, string.Empty);
        return true;
    }
}
