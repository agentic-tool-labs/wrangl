using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>
/// The rows of the QA job store (b397: the <c>qa_jobs</c> table): one row per job, its latest snapshot, every change replacing it under
/// a revision check. A row that does not parse as an object with an id is skipped on read.
/// </summary>
internal static partial class QaRows
{
    /// <summary>How long a finished job stays in the store, in milliseconds.</summary>
    internal const double RetentionMs = 604_800_000;

    private static readonly string[] Priorities = ["gate", "lane", "adhoc"];

    private static readonly string[] States = ["queued", "claimed", "done", "failed", "withdrawn"];

    [GeneratedRegex("^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha();

    /// <summary>
    /// The id of the queue-epoch row. It is not a job: its revision goes up with every write that changes the queue's shape (a claim, a new
    /// entry), so two such writes made from the same read cannot both succeed.
    /// </summary>
    internal const string EpochId = "_queue";

    /// <summary>The rows of <paramref name="bodies"/> that parse as an object with an id, in order.</summary>
    internal static List<JsonObject> Parse(IEnumerable<string> bodies)
    {
        var rows = new List<JsonObject>();
        foreach (var body in bodies)
        {
            try
            {
                if (JsonText.Parse(body) is { IsMap: true } parsed && parsed.AsObject().Get("id")?.NonEmptyText is not null)
                {
                    rows.Add(parsed.AsObject());
                }
            }
            catch (JsonParseException)
            {
            }
        }

        return rows;
    }

    /// <summary>The rows without the queue-epoch row.</summary>
    internal static List<JsonObject> Jobs(IEnumerable<JsonObject> rows) => [.. rows.Where(r => r.Get("id")?.NonEmptyText != EpochId)];

    /// <summary>The revision of the queue-epoch row, 0 when there is none.</summary>
    internal static long EpochOf(IEnumerable<JsonObject> rows) =>
        rows.FirstOrDefault(r => r.Get("id")?.NonEmptyText == EpochId) is { } row ? RevOf(row) : 0;

    /// <summary>The end time of a done, failed or withdrawn job, null for any other row.</summary>
    internal static long? EndedAt(JsonObject row) =>
        row.Get("state")?.NonEmptyText is "done" or "failed" or "withdrawn" && row.Get("ended_at") is { IsNumber: true } ended && double.IsFinite(ended.AsNumber())
            ? (long)ended.AsNumber()
            : null;

    /// <summary>Whether a done, failed or withdrawn job ended more than <see cref="RetentionMs"/> before <paramref name="nowMs"/>; one with no readable end time stays.</summary>
    internal static bool Expired(JsonObject row, double nowMs) => EndedAt(row) is { } ended && nowMs - ended > RetentionMs;

    /// <summary>Throws <c>qa-bad-row: &lt;id&gt;: &lt;key&gt;</c> for the first key of the row that is missing, mistyped or inconsistent with its state.</summary>
    internal static void Validate(JsonValue value)
    {
        var row = value.IsMap ? value.AsObject() : null;
        var id = row?.Get("id")?.NonEmptyText ?? string.Empty;
        var bad = row is null ? "id" : id == EpochId ? (IsInt(row.Get("rev") ?? JsonValue.Null, 1) ? null : "rev") : BadKey(row);
        if (bad is not null)
        {
            throw new InvalidOperationException($"qa-bad-row: {id}: {bad}");
        }
    }

    private static string? BadKey(JsonObject row)
    {
        bool Nullable(string key, Func<JsonValue, bool> ok) => row.Get(key) is { } v && (v.IsNull || ok(v));
        bool Required(string key, Func<JsonValue, bool> ok) => row.Get(key) is { } v && ok(v);
        var checks = new (string Key, bool Ok)[]
        {
            ("id", Required("id", v => v.NonEmptyText is not null)),
            ("rev", Required("rev", v => IsInt(v, 1))),
            ("sha", Required("sha", v => v.IsText && Sha().IsMatch(v.AsString()))),
            ("base", Nullable("base", v => v.IsText)),
            ("set", Required("set", v => v.NonEmptyText is not null)),
            ("repo", Required("repo", v => v.IsText && Path.IsPathRooted(v.AsString()))),
            ("fresh", Required("fresh", v => v.IsBool)),
            ("priority", Required("priority", v => v.IsText && Priorities.Contains(v.AsString(), StringComparer.Ordinal))),
            ("requesters", Required("requesters", v => v.IsArray && v.AsArray().Count > 0 && v.AsArray().All(r => r.IsText))),
            ("team", Nullable("team", v => v.IsText)),
            ("created", Required("created", v => IsInt(v, double.MinValue))),
            ("state", Required("state", v => v.IsText && States.Contains(v.AsString(), StringComparer.Ordinal))),
            ("claimed_by", Nullable("claimed_by", v => v.IsText)),
            ("claimed_at", Nullable("claimed_at", v => IsInt(v, double.MinValue))),
            ("result_path", Nullable("result_path", v => v.IsText)),
            ("verdict", Nullable("verdict", v => v.IsText)),
            ("ended_at", Nullable("ended_at", v => IsInt(v, double.MinValue))),
            ("lost_claims", Required("lost_claims", v => IsInt(v, 0))),
            ("reason", Nullable("reason", v => v.IsText)),
        };
        foreach (var (key, ok) in checks)
        {
            if (!ok)
            {
                return key;
            }
        }

        var state = row.Get("state")!.Value.AsString();
        if (state == "claimed")
        {
            return row.Get("claimed_by")!.Value.IsNull ? "claimed_by" : row.Get("claimed_at")!.Value.IsNull ? "claimed_at" : null;
        }

        return state is "done" or "failed" or "withdrawn" && row.Get("ended_at")!.Value.IsNull ? "ended_at" : null;
    }

    /// <summary>The largest integer a JSON number holds exactly.</summary>
    private const double MaxExactInt = 9_007_199_254_740_991;

    private static bool IsInt(JsonValue v, double min) =>
        v.IsNumber && double.IsFinite(v.AsNumber()) && v.AsNumber() == Math.Floor(v.AsNumber()) && v.AsNumber() >= min && v.AsNumber() <= MaxExactInt;

    internal static long RevOf(JsonObject row) => row.Get("rev") is { IsNumber: true } r ? (long)r.AsNumber() : 0;
}

/// <summary>The typed view of one job row; keys the store does not know ride along in <see cref="Extra"/> and come back out of <see cref="ToRow"/>.</summary>
internal sealed record QaEntry(
    string Id,
    long Rev,
    string Sha,
    string? Base,
    string Set,
    string Repo,
    bool Fresh,
    string Priority,
    IReadOnlyList<string> Requesters,
    string? Team,
    long Created,
    string State,
    string? ClaimedBy,
    long? ClaimedAt,
    string? ResultPath,
    string? Verdict,
    long? EndedAt,
    int LostClaims,
    string? Reason,
    JsonObject Extra)
{
    private static readonly HashSet<string> Known = new(StringComparer.Ordinal)
    {
        "id", "rev", "sha", "base", "set", "repo", "fresh", "priority", "requesters", "team", "created", "state",
        "claimed_by", "claimed_at", "result_path", "verdict", "ended_at", "lost_claims", "reason",
    };

    /// <summary>The entry for a row the store accepted; throws <c>qa-bad-row</c> for any other.</summary>
    internal static QaEntry FromRow(JsonObject row)
    {
        QaRows.Validate(row);
        string? Text(string key) => row.Get(key) is { IsText: true } v ? v.AsString() : null;
        long? Int(string key) => row.Get(key) is { IsNumber: true } v ? (long)v.AsNumber() : null;
        var extra = new JsonObject();
        foreach (var key in row.Keys.Where(k => !Known.Contains(k)))
        {
            extra[key] = row[key];
        }

        return new QaEntry(
            Text("id")!, Int("rev")!.Value, Text("sha")!, Text("base"), Text("set")!, Text("repo")!, row.Get("fresh")!.Value.AsBool(),
            Text("priority")!, [.. row.Get("requesters")!.Value.AsArray().Select(r => r.AsString())], Text("team"), Int("created")!.Value,
            Text("state")!, Text("claimed_by"), Int("claimed_at"), Text("result_path"), Text("verdict"), Int("ended_at"),
            (int)Int("lost_claims")!.Value, Text("reason"), extra);
    }

    internal JsonObject ToRow()
    {
        static JsonValue Or(string? text) => text is null ? JsonValue.Null : text;
        static JsonValue OrNum(long? number) => number is null ? JsonValue.Null : number.Value;
        var requesters = new JsonArray();
        foreach (var requester in Requesters)
        {
            requesters.Add(requester);
        }

        var row = new JsonObject
        {
            ["id"] = Id,
            ["rev"] = Rev,
            ["sha"] = Sha,
            ["base"] = Or(Base),
            ["set"] = Set,
            ["repo"] = Repo,
            ["fresh"] = Fresh,
            ["priority"] = Priority,
            ["requesters"] = requesters,
            ["team"] = Or(Team),
            ["created"] = Created,
            ["state"] = State,
            ["claimed_by"] = Or(ClaimedBy),
            ["claimed_at"] = OrNum(ClaimedAt),
            ["result_path"] = Or(ResultPath),
            ["verdict"] = Or(Verdict),
            ["ended_at"] = OrNum(EndedAt),
            ["lost_claims"] = LostClaims,
            ["reason"] = Or(Reason),
        };
        foreach (var key in Extra.Keys)
        {
            row[key] = Extra[key];
        }

        return row;
    }
}

internal sealed partial class HierLib
{
    /// <summary>Every row of the <c>qa_jobs</c> table (the queue-epoch row included), in first-written order; none without a state database.</summary>
    private List<JsonObject> QaStoredRows() => QaRows.Parse(_qaJobBodies?.Invoke() ?? []);

    public JsonArray QaReadOp() => [.. QaRows.Jobs(QaStoredRows()).Select(static r => (JsonValue)r.Clone())];

    /// <summary>The latest snapshot of each job and the queue-epoch revision, from one read of the table.</summary>
    internal (List<JsonObject> Jobs, long Epoch) QaSnapshotRows()
    {
        var rows = QaStoredRows();
        return (QaRows.Jobs(rows), QaRows.EpochOf(rows));
    }

    /// <summary>
    /// <c>qa.append</c>: stores each row of <c>rec</c> (one row or an array) as its job's snapshot when its <c>rev</c> is one past the stored
    /// one (1 for a new job): <c>{ok: true}</c>. Any stale revision writes nothing and answers <c>{ok: false, conflict: [ids]}</c>. A
    /// replayed op id answers its first result (applied_writes).
    /// </summary>
    internal static OpResult QaAppendDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var rec = args.Member("rec") ?? throw new InvalidOperationException("args.rec is undefined");
        var rows = rec.IsArray ? rec.AsArray().ToList() : [rec];
        if (rows.Count == 0)
        {
            throw new InvalidOperationException("qa.append needs at least one row");
        }

        foreach (var row in rows)
        {
            QaRows.Validate(row);
        }

        var revs = tx.Query("SELECT id, rev FROM qa_jobs").ToDictionary(r => (string)r[0]!, r => (long)r[1]!, StringComparer.Ordinal);
        var conflict = new JsonArray();
        foreach (var row in rows)
        {
            var id = row.Member("id")!.Value.AsString();
            var rev = QaRows.RevOf(row.AsObject());
            if (rev != (revs.TryGetValue(id, out var current) ? current + 1 : 1))
            {
                if (!conflict.Any(c => c.AsString() == id))
                {
                    conflict.Add(id);
                }

                continue;
            }

            revs[id] = rev;
        }

        if (conflict.Count > 0)
        {
            return new JsonObject { ["ok"] = false, ["conflict"] = conflict };
        }

        foreach (var value in rows)
        {
            var row = value.AsObject().Clone();
            if (row.Get("sha") is { IsText: true } sha)
            {
                row["sha"] = sha.AsString().ToLowerInvariant();
            }

            tx.Execute(
                "INSERT INTO qa_jobs(id, seq, rev, body, ended_at) VALUES (?1, (SELECT COALESCE(MAX(seq), 0) + 1 FROM qa_jobs), ?2, ?3, ?4) "
                + "ON CONFLICT(id) DO UPDATE SET rev = excluded.rev, body = excluded.body, ended_at = excluded.ended_at",
                row.Get("id")!.Value.AsString(), QaRows.RevOf(row), JsonText.Stringify(row, 0), QaRows.EndedAt(row));
        }

        return new JsonObject { ["ok"] = true };
    });

    /// <summary>Appends the rows through the <c>qa</c> state domain, so the revision check runs inside the write's transaction.</summary>
    internal QaAppendResult QaAppendRouted(IReadOnlyList<QaEntry> rows, long? epoch)
    {
        var writer = _writer ?? throw new InvalidOperationException("no state writer is configured");
        var rec = new JsonArray();
        foreach (var row in rows)
        {
            rec.Add(row.ToRow());
        }

        if (epoch is { } revision)
        {
            rec.Add(new JsonObject { ["id"] = QaRows.EpochId, ["rev"] = revision });
        }

        var result = writer.Invoke("qa", "append", JsonText.ToUtf8(new JsonObject { ["rec"] = rec }, 0), CwdContext(), new StateCallOptions());
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? result.ErrorCode ?? "state call failed");
        }

        var parsed = JsonText.Parse(Utf8Text.Decode(result.ResultJson.Span));
        return new QaAppendResult(
            parsed.Member("ok") is { IsBool: true } ok && ok.AsBool(),
            parsed.Member("conflict") is { IsArray: true } conflict ? [.. conflict.AsArray().Select(c => c.AsString())] : []);
    }

    /// <summary><c>qa.compact</c>: deletes the done, failed or withdrawn jobs that ended more than <see cref="QaRows.RetentionMs"/> before <c>now_ms</c>: <c>{dropped: n}</c>.</summary>
    internal static OpResult QaCompactDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        tx.Execute("DELETE FROM qa_jobs WHERE ended_at IS NOT NULL AND ended_at < ?1", (long)Math.Ceiling(JsConvert.ToNumber(args.Member("now_ms")) - QaRows.RetentionMs));
        return new JsonObject { ["dropped"] = tx.Query("SELECT changes()") is [[long n]] ? n : 0 };
    });

    public int CompactQa(double nowMs)
    {
        var writer = _writer ?? throw new InvalidOperationException("no state writer is configured");
        // Nothing to drop costs no write; the op repeats the check inside its transaction.
        if (!QaStoredRows().Any(r => QaRows.Expired(r, nowMs)))
        {
            return 0;
        }

        var result = writer.Invoke(
            "qa",
            "compact",
            JsonText.ToUtf8(new JsonObject { ["now_ms"] = JsonValue.FromNumber(nowMs) }, 0),
            CwdContext(),
            new StateCallOptions());
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? result.ErrorCode ?? "state call failed");
        }

        return JsonText.TryParse(result.ResultJson.Span, out var parsed) && parsed.Member("dropped") is { IsNumber: true } n ? (int)n.AsNumber() : 0;
    }
}
