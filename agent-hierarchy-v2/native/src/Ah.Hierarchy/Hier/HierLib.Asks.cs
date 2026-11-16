using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

internal sealed partial class HierLib
{
    // RFC 3339 with the offset spelled out: a bare date or time names no instant, and local time is never assumed.
    [GeneratedRegex("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\\.[0-9]+)?(?:Z|[+-][0-9]{2}:[0-9]{2})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetInstantRegex();

    /// <summary>
    /// <c>decisions.ask</c>: appends <c>row</c> (what the user was asked and what came back) to <c>&lt;hier&gt;/asks.jsonl</c>. The file holds the
    /// user's answers unredacted, so it is created readable by its owner only; an existing file keeps its mode.
    /// </summary>
    public JsonObject AsksAppendOp(IStoreWriteContext writer, JsonValue args, string? operationId)
    {
        var path = AsksLogPathOf(args);
        if (args.Member("row") is not { IsMap: true } row)
        {
            throw new InvalidOperationException("decisions.ask takes a row object");
        }

        // ponytail: one file, no rotation; asks are a few per run so it grows by KB. Rotate by size if that stops holding.
        AppendLine(writer, path, row.AsObject(), operationId, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return new JsonObject { ["appended"] = true };
    }

    /// <summary><c>decisions.qa-log</c> (internal read): <c>{rows}</c>, the parsed rows of <c>dir</c>'s question log in append order; none when the table holds none.</summary>
    internal JsonValue QaLogReadOp(JsonValue args)
    {
        var rows = new JsonArray();
        rows.AddRange(QaLogRowsOf(ArgText(args, "dir")));
        return new JsonObject { ["rows"] = rows };
    }

    /// <summary><c>decisions.qa-thread</c> (internal read): <c>{prompt, replies, ask}</c>, the whole thread whose first row's id is <c>id</c>, or null.</summary>
    internal JsonValue QaThreadReadOp(JsonValue args)
    {
        var id = ArgText(args, "id");
        return QaLog.Threads(QaLogRowsOf(ArgText(args, "dir"))).FirstOrDefault(t => (t.Prompt ?? t.Ask ?? t.Replies[0]).Member("id") is { IsText: true } h && h.AsString() == id) is { } thread
            ? QaLog.ThreadJson(thread)
            : JsonValue.Null;
    }

    /// <summary>The object rows of <paramref name="dir"/>'s question log (the <c>qa_log</c> table, per-dir-files-to-db S6) in append order; a body that is not an object is skipped.</summary>
    // ponytail: every read parses the dir's whole 30-day log; add a per-dir cache keyed by MAX(seq) if the questions lane shows up in a profile.
    public IReadOnlyList<JsonValue> QaLogRows(string dir) => QaLogRowsOf(dir);

    // a row past the window is not shown even before the next append drops it: rows taken from an old file can be older
    private List<JsonValue> QaLogRowsOf(string dir)
    {
        var cutoff = NowMs - QaLog.RetentionMs;
        return [.. (_qaLogRows?.Bodies(dir) ?? []).Select(static b => JsonText.TryParse(Utf8Text.Encode(b), out var r) && r.IsMap ? r : JsonValue.Null)
            .Where(r => r.IsMap && !(r.Member("at") is { IsNumber: true } at && at.AsNumber() < cutoff))];
    }

    /// <summary>
    /// <c>qa-log.append</c> in the database: drops <c>dir</c>'s rows past the retention window, then stores <c>row</c> (with the write's
    /// <c>op_id</c>) as the next row; a row whose <c>id</c> is held already is ignored, or with <c>replace: true</c> takes the new body and
    /// <c>at</c> and keeps its <c>seq</c>.
    /// </summary>
    internal OpResult QaLogAppendDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = MsgRows.PoolKey(ArgText(args, "dir"));
        if (args.Member("row") is not { IsMap: true } given)
        {
            throw new InvalidOperationException("qa-log.append takes a row object");
        }

        var row = JsOps.Spread(given);
        if (call.OperationId is { Length: > 0 } operationId)
        {
            row["op_id"] = operationId;
        }

        tx.Execute("DELETE FROM qa_log WHERE hier_dir = ?1 AND at < ?2", dir, (long)(NowMs - QaLog.RetentionMs));
        var replace = args.Member("replace") is { IsBool: true } r && r.AsBool();
        tx.Execute(
            replace
                ? "INSERT INTO qa_log(hier_dir, seq, id, at, body) VALUES (?1, (SELECT COALESCE(MAX(seq), 0) + 1 FROM qa_log WHERE hier_dir = ?1), ?2, ?3, ?4) ON CONFLICT(hier_dir, id) DO UPDATE SET at = excluded.at, body = excluded.body"
                : "INSERT OR IGNORE INTO qa_log(hier_dir, seq, id, at, body) VALUES (?1, (SELECT COALESCE(MAX(seq), 0) + 1 FROM qa_log WHERE hier_dir = ?1), ?2, ?3, ?4)",
            dir,
            given.Member("id") is { IsText: true } id ? id.AsString() : null,
            given.Member("at") is { IsNumber: true } at ? (long)at.AsNumber() : null,
            JsonText.Stringify(row, 0));
        return new JsonObject { ["appended"] = true };
    });

    internal const string AsksGateFile = "asks-gate.jsonl";

    /// <summary>The asks log, or the gate audit or parked questions beside it: the same owner-only append and lock, never mixed into the user's answers.</summary>
    internal static bool IsAsksLog(string? path) => path is not null && NodePath.Basename(path) is "asks.jsonl" or AsksGateFile or ParkedQuestions.FileName;

    private static string AsksLogPathOf(JsonValue args) =>
        args.Member("path") is { IsText: true } text && IsAsksLog(text.AsString())
            ? text.AsString()
            : throw new InvalidOperationException($"{JsConvert.ToJsString(args.Member("path"))} is not the asks log");

    public string AsksPath(string dir) => NodePath.Join(dir, "asks.jsonl");

    public bool TryParseInstant(string text, out DateTimeOffset instant)
    {
        instant = default;
        if (!OffsetInstantRegex().IsMatch(text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return false;
        }

        instant = parsed.ToUniversalTime();
        return true;
    }

    /// <summary>The instant a record's field names, or null when it is not an RFC 3339 string.</summary>
    private bool TryInstantOf(JsonValue record, string field, out DateTimeOffset instant)
    {
        instant = default;
        return Member(record, field) is { IsText: true } t && TryParseInstant(t.AsString(), out instant);
    }

    public JsonObject DecisionRunSpan(JsonArray records)
    {
        DateTimeOffset? first = null;
        DateTimeOffset? last = null;
        foreach (var r in records)
        {
            if (!r.IsMap || !TryInstantOf(r, "time", out var t))
            {
                continue;
            }

            first = first is null || t < first ? t : first;
            last = last is null || t > last ? t : last;
        }

        if (first is not { } from || last is not { } to)
        {
            return new JsonObject();
        }

        // a question still parked has no closing record: its answer, when it comes, is still inside the run
        var answers = records.Where(d => d.IsMap && IsKind(d, "answered")).Select(d => TextOf(Member(d, "answers"))).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var open = records.Any(d => d.IsMap && IsKind(d, "parked") && !(TextOf(Member(d, "id")) is { } id && answers.Contains(id)));
        var until = open ? _clock.UtcNow : to.AddMilliseconds(1);
        return new JsonObject { ["since"] = Timestamps.ToIsoString(from), ["until"] = Timestamps.ToIsoString(until) };
    }

    public JsonObject AsksAnswered(string dir, JsonArray records, DateTimeOffset since, DateTimeOffset until)
    {
        var path = AsksPath(dir);
        double unreadable = 0;
        var covered = new HashSet<string>(StringComparer.Ordinal);
        var keyed = records.Where(d => d.IsMap && (IsKind(d, "parked") || IsKind(d, "answered") || IsKind(d, "decided"))).ToList();
        foreach (var d in keyed)
        {
            // a record from before the window is not the answer to an ask inside it
            if (TryInstantOf(d, "time", out var recordedAt) && (recordedAt < since || recordedAt >= until))
            {
                continue;
            }

            if (TextOf(Member(d, "qkey")) is { } qkey)
            {
                covered.Add(qkey);
            }
        }

        // the item is not part of what the user was asked, so a question is matched under each item the scope's records name
        var items = keyed.Select(d => Member(d, "item")).DistinctBy(i => JsConvert.ToJsString(i)).ToList();
        double answered = 0;
        var seenOps = new HashSet<string>(StringComparer.Ordinal);
        // per-dir-files-to-db §6c G5: one read; a log that cannot be read answers nothing (0)
        if (_files.ReadAskLog(path).TryLines(out var askLines))
        {
            foreach (var line in askLines)
            {
                if (JsText.Trim(line).Length == 0)
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
                    unreadable++;
                    continue;
                }

                if (!row.IsMap || !TryInstantOf(row, "at", out var at))
                {
                    unreadable++;
                    continue;
                }

                // a hook that fired twice for one tool call wrote the call twice; the first row stands. A row with no op_id has nothing to
                // match on, so it is counted, and every row inside the window is already reported as unreadable below.
                if (Member(row, "op_id") is { IsText: true } opId && opId.AsString().Length > 0 && !seenOps.Add(opId.AsString()))
                {
                    continue;
                }

                if (at < since || at >= until)
                {
                    continue;
                }

                // no response shape is pinned, so every row is counted from its questions and flagged unreadable
                unreadable++;
                if (Member(row, "questions") is not { IsArray: true } questionList)
                {
                    continue;
                }

                foreach (var q in questionList.AsArray())
                {
                    if (Member(q, "question") is not { IsText: true } text)
                    {
                        continue;
                    }

                    if (items.Any(item => covered.Contains(QuestionKey(item, text))))
                    {
                        continue;
                    }

                    answered++;
                }
            }
        }

        return new JsonObject { ["answered"] = answered, ["unreadable"] = unreadable };
    }

    public JsonObject DecisionLine(string dir, string? runId, DateTimeOffset? since, DateTimeOffset? until)
    {
        double decided = 0;
        double answered = 0;
        double byRule = 0;
        double waiting = 0;
        double unreadable = 0;
        var scope = new JsonArray();
        DateTimeOffset from;
        DateTimeOffset to;
        if (runId is not null)
        {
            var read = ReadDecisions(DecisionLogPath(dir, runId));
            unreadable += read["skipped"].AsNumber();
            scope.AddRange(read["decisions"].AsArray());
            Tally(read["decisions"].AsArray());
            var span = DecisionRunSpan(scope);
            if (!TryInstantOf(span, "since", out from) || !TryInstantOf(span, "until", out to))
            {
                from = to = default;
            }
        }
        else
        {
            from = since ?? throw new InvalidOperationException("a window needs since");
            to = until ?? _clock.UtcNow;
            foreach (var run in RunIds(dir))
            {
                var read = ReadDecisions(DecisionLogPath(dir, run));
                unreadable += read["skipped"].AsNumber();

                // An answer belongs to the window it was given in, whenever its question was parked: parked lines count up to the end
                // of the window, decided and answered lines inside it.
                var upto = new JsonArray();
                upto.AddRange(read["decisions"].AsArray().Where(d => d.IsMap && TryInstantOf(d, "time", out var t) && t < to));
                var windowed = new JsonArray();
                windowed.AddRange(upto.Where(d => IsKind(d, "parked") || (TryInstantOf(d, "time", out var t) && t >= from)));
                scope.AddRange(windowed);
                var closedByNow = new JsonArray();
                closedByNow.AddRange(upto.Where(d => IsKind(d, "parked") || IsKind(d, "answered")));
                Tally(windowed, closedByNow);
            }
        }

        waiting += OpenParkedQuestionCount(dir);
        var asks = AsksAnswered(dir, scope, from, to);
        unreadable += asks["unreadable"].AsNumber();
        var byYou = answered + asks["answered"].AsNumber();
        var line = $"decisions: {JsConvert.NumberToString(byYou)} answered by you, "
            + (byRule > 0 ? $"{JsConvert.NumberToString(byRule)} answered by your rules, " : string.Empty)
            + $"{JsConvert.NumberToString(decided)} decided by us, {JsConvert.NumberToString(waiting)} waiting"
            + (unreadable > 0 ? $" ({JsConvert.NumberToString(unreadable)} unreadable)" : string.Empty);
        return new JsonObject { ["line"] = line };

        void Tally(JsonArray records, JsonArray? closing = null)
        {
            var summary = DecisionSummary(records, null);
            decided += summary["decided"].AsNumber();
            byRule += summary.Get("by_rule") is { IsNumber: true } r ? r.AsNumber() : 0;
            var a = AnsweredOf(summary);
            answered += a;
            waiting += summary["parked"].AsNumber() - (closing is null ? a : AnsweredOf(DecisionSummary(closing, null)));
        }

        static double AnsweredOf(JsonObject summary) => summary.Get("answered") is { IsNumber: true } n ? n.AsNumber() : 0;
    }

    /// <summary>How many questions parked for the user in <paramref name="dir"/> are still open; an unreadable store counts none.</summary>
    private double OpenParkedQuestionCount(string dir) =>
        _files.ReadAskLog(ParkedQuestions.PathIn(dir)).TryLines(out var lines) ? ParkedQuestions.Fold(string.Join('\n', lines)).Count(q => q.Open) : 0;

    private List<string> RunIds(string dir)
    {
        if (StoredDecisionRuns(dir) is { } stored)
        {
            return [.. stored];
        }

        try
        {
            return _files.List(NodePath.Join(dir, "pipeline")).Order(StringComparer.Ordinal).Where(run => _files.Exists(DecisionLogPath(dir, run))).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
