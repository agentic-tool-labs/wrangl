using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>
/// The <c>peer-records</c> write ops in a database transaction: the same rows the file ops would write, as rows of <c>peer_records</c>.
/// Only a JSON object is a record, so any other element of a payload is dropped, as every reader of the file drops it.
/// </summary>
internal static class PeerRecordsDb
{
    private const int DeleteChunk = 500;

    internal static OpResult Append(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var rec = args.Member("rec") ?? throw new InvalidOperationException("args.rec is undefined");
        var records = RowsOf(rec, call.OperationId).Where(PeerRows.IsRecord).ToList();
        var inserted = 0L;
        foreach (var row in records)
        {
            var c = PeerRows.ColumnsOf(row);
            tx.Execute(PeerRows.InsertSql, PeerRows.InsertArgs(c, Utf8Text.Encode(JsonText.Stringify(row, 0))));
            inserted += (long)tx.Query("SELECT changes()")[0][0]!;
        }

        var result = new JsonObject { ["ok"] = true };
        if (records.Count > 0 && inserted == 0)
        {
            result["existed"] = true;
        }

        return result;
    });

    internal static OpResult Compact(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var rows = tx.Query("SELECT seq, raw FROM peer_records ORDER BY seq");
        var lines = rows.Select(r => Encoding.UTF8.GetString((byte[])r[1]!)).ToList();
        var drop = PeerLogRetention.DroppedLines(lines, PeerLogRetention.IdSet(args, "closed"), PeerLogRetention.IdSet(args, "open"), JsConvert.ToNumber(args.Member("now_ms")), PeerLogRetention.RetentionOf(args), PeerLogRetention.LiveSessionsOf(args));
        var seqs = drop.Order().Select(i => (long)rows[i][0]!).ToList();
        for (var at = 0; at < seqs.Count; at += DeleteChunk)
        {
            var chunk = seqs.Skip(at).Take(DeleteChunk).Cast<object?>().ToArray();
            tx.Execute($"DELETE FROM peer_records WHERE seq IN ({string.Join(',', chunk.Select((_, i) => "?" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)))})", chunk);
        }

        return new JsonObject { ["dropped"] = drop.Count };
    });

    /// <summary>The rows the files-mode op writes for a payload: an array is one row per element, and with an op id every row carries one.</summary>
    private static List<JsonValue> RowsOf(JsonValue rec, string? operationId)
    {
        var hasId = operationId is { Length: > 0 };
        if (rec.IsArray)
        {
            var elements = rec.AsArray();
            if (elements.Count == 0)
            {
                throw new InvalidOperationException("peer-records.append needs at least one row");
            }

            var rows = new List<JsonValue>();
            for (var i = 0; i < elements.Count; i++)
            {
                rows.Add(hasId ? WithOpId(elements[i], $"{operationId}:{i}") : elements[i]);
            }

            return rows;
        }

        return [hasId ? WithOpId(rec, operationId!) : rec];
    }

    private static JsonValue WithOpId(JsonValue row, string opId)
    {
        var copy = JsOps.Spread(row);
        copy["op_id"] = opId;
        return copy;
    }
}
