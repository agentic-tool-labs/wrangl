using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

internal sealed partial class HierLib : IHierarchyPeers
{
    [GeneratedRegex(
        "\\[hierarchy-peer-brief[" + Ws + "]+reply-to=\"(?<reply>[^\"]+)\"(?:[" + Ws + "]+task=\"(?<task>[^\"]*)\")?[^\\]]*\\]",
        RegexOptions.CultureInvariant)]
    private static partial Regex SentinelRegex();

    [GeneratedRegex("<cross-session-message(?![A-Za-z0-9_])[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex WrapperTagRegex();

    [GeneratedRegex("[" + Ws + "]*\\[[^\\]]*\\][" + Ws + "]*\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingRefRegex();

    [GeneratedRegex("(?<![A-Za-z0-9_])from=\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex FromAttrRegex();

    [GeneratedRegex("(?<![A-Za-z0-9_])from-name=\"(?<v>[^\"]+)\"", RegexOptions.CultureInvariant)]
    private static partial Regex FromNameAttrRegex();

    [GeneratedRegex("^\\[hierarchy-msg[" + Ws + "]+(?<path>[^\\]" + Ws + "]+)(?:[" + Ws + "]+(?:request|response|note))?[" + Ws + "]*\\]", RegexOptions.CultureInvariant)]
    private static partial Regex MsgTokenLineRegex();

    public string PeerPendingPath() => AhPaths.UserEntry(_environment.HomeDirectory, AhPaths.PeerPendingFile);

    public string StripRef(string value) => TrailingRefRegex().Replace(value, string.Empty, 1);

    public JsonValue ParseSentinel(JsonValue? text)
    {
        if (text is not { IsText: true } t || t.AsString().Length == 0)
        {
            return JsonValue.Null;
        }

        var m = SentinelRegex().Match(t.AsString());
        if (!m.Success)
        {
            return JsonValue.Null;
        }

        return new JsonObject
        {
            ["replyTo"] = StripRef(JsText.Trim(m.Groups["reply"].Value)),
            ["task"] = m.Groups["task"].Success ? m.Groups["task"].Value : string.Empty,
        };
    }

    public JsonValue ParseWrapper(JsonValue? text)
    {
        if (text is not { IsText: true } t || t.AsString().Length == 0)
        {
            return JsonValue.Null;
        }

        var source = t.AsString();
        var tagMatch = WrapperTagRegex().Match(source);
        if (!tagMatch.Success)
        {
            return JsonValue.Null;
        }

        var from = FromAttrRegex().Match(tagMatch.Value);
        var fromName = FromNameAttrRegex().Match(tagMatch.Value);
        if (!from.Success)
        {
            return JsonValue.Null;
        }

        return new JsonObject
        {
            ["from"] = from.Groups["v"].Value,
            ["fromName"] = fromName.Success ? fromName.Groups["v"].Value : string.Empty,
            ["body"] = source[(tagMatch.Index + tagMatch.Length)..],
        };
    }

    public JsonValue UnwrapCrossSession(JsonValue? text)
    {
        if (text is not { IsText: true } t || ParseWrapper(t).IsNull)
        {
            return JsonValue.Null;
        }

        var trimmed = JsText.Trim(t.AsString());
        var tag = WrapperTagRegex().Match(trimmed);
        const string close = "</cross-session-message>";
        if (!tag.Success || tag.Index != 0 || !trimmed.EndsWith(close, StringComparison.Ordinal))
        {
            return JsonValue.Null;
        }

        return trimmed.Substring(tag.Length, trimmed.Length - close.Length - tag.Length);
    }

    public JsonValue ExtractPendingRecord(JsonValue? text, JsonValue? role = null)
    {
        if (text is not { IsText: true } t || t.AsString().Length == 0)
        {
            return JsonValue.Null;
        }

        var source = t.AsString();
        var tagMatch = WrapperTagRegex().Match(source);
        if (!tagMatch.Success)
        {
            return JsonValue.Null;
        }

        var afterTag = source[(tagMatch.Index + tagMatch.Length)..];
        var firstLine = afterTag.Split('\n').FirstOrDefault(l => JsText.Trim(l).Length != 0);
        if (firstLine is null)
        {
            return JsonValue.Null;
        }

        var line = JsText.Trim(firstLine);
        var wrapper = ParseWrapper(source);
        if (wrapper.IsNull)
        {
            return JsonValue.Null;
        }

        var from = wrapper.Prop("from");
        var fromName = wrapper.Prop("fromName");
        var sentinel = SentinelRegex().Match(line);
        if (sentinel.Success && sentinel.Index == 0)
        {
            return new JsonObject
            {
                ["from"] = from,
                ["from_name"] = fromName,
                ["reply_to"] = StripRef(JsText.Trim(sentinel.Groups["reply"].Value)),
                ["task"] = sentinel.Groups["task"].Success ? sentinel.Groups["task"].Value : string.Empty,
                ["armed_by"] = "sentinel",
            };
        }

        var msg = MsgTokenLineRegex().Match(line);
        if (msg.Success && msg.Index == 0)
        {
            var path = _core.ExtractMsgToken(msg.Value, CwdContext().HierarchyDirectory) ?? string.Empty;
            if (path.EndsWith("--request.md", StringComparison.Ordinal) && _core.MsgExists(path))
            {
                var meta = MessageText.ParseFilename(path);
                if (meta is not null && meta.Type == "request")
                {
                    return new JsonObject
                    {
                        ["from"] = from,
                        ["from_name"] = fromName,
                        ["reply_to"] = from,
                        ["task"] = meta.Slug,
                        ["armed_by"] = "msg-token",
                    };
                }
            }
        }

        if (role is { IsTruthy: true } r)
        {
            var path = _core.ExtractMsgToken(afterTag, CwdContext().HierarchyDirectory, r.IsText ? r.AsString() : null);
            if (path is not null && path.EndsWith("--request.md", StringComparison.Ordinal) && _core.MsgExists(path))
            {
                var meta = MessageText.ParseFilename(path);
                var parsed = meta is not null && meta.Type == "request" ? _core.ReadMsgFile(path) : JsonValue.Null;
                if (parsed.Member("fm") is { IsMap: true } fm && fm.Member("to") is { } to && StrictEquals(to, r))
                {
                    return new JsonObject
                    {
                        ["from"] = from,
                        ["from_name"] = fromName,
                        ["reply_to"] = from,
                        ["task"] = meta!.Slug,
                        ["armed_by"] = "msg-token",
                    };
                }
            }
        }

        return JsonValue.Null;
    }

    public JsonArray LatestByKey(IReadOnlyList<JsonValue> records) => [.. PeerRows.LatestByKey(records)];

    public void AppendPeerRecord(IStoreWriteContext writer, JsonValue record)
    {
        ArgumentNullException.ThrowIfNull(writer);
        var path = PeerPendingPath();
        writer.Mkdir(NodePath.Dirname(path));
        var rows = record.IsArray ? record.AsArray().ToList() : [record];
        writer.Append(path, Utf8Text.Encode(string.Concat(rows.Select(r => JsonText.Stringify(r, 0) + "\n"))));
    }

    private static string CliRootDigest(JsonValue? line) =>
        Convert.ToHexStringLower(SHA256.HashData(Utf8Text.Encode(JsConvert.ToJsString(line))))[..16];

    /// <summary>
    /// The rows a reader of one session can accept: those of <paramref name="sessionId"/>, narrowed to <paramref name="types"/> when it names
    /// some (none: any type). A session id that is not a non-empty text is no key range, so the whole log is read as before. The rows are the
    /// cache's: a caller that hands a row on takes <see cref="Own"/> of it.
    /// </summary>
    private IReadOnlyList<JsonValue> SessionRows(JsonValue? sessionId, params string[] types) =>
        sessionId is { IsText: true } sid && sid.AsString().Length > 0
            ? _core.PeerRecordsWhere(new PeerQuery("session_id", sid.AsString(), types.Length == 0 ? null : TypeSet(types)))
            : _core.PeerRecordsShared();

    private static HashSet<string> TypeSet(params string[] types) => new HashSet<string>(types, StringComparer.Ordinal);

    /// <summary>A copy of a cached row that its caller may change, as <see cref="IHierarchyCore.ReadPeerRecords"/> hands out.</summary>
    private static JsonValue Own(JsonValue row) => row.IsMap ? row.AsObject().Clone() : row;

    public bool CliRootAlreadyEmitted(JsonValue? sessionId, JsonValue? line)
    {
        if (sessionId is not { IsText: true } sid || sid.AsString().Length == 0)
        {
            return false;
        }

        var rows = SessionRows(sid, "cli-root");
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var r = rows[i];
            if (r.IsTruthy && Member(r, "type") is { IsText: true } t && t.AsString() == "cli-root" && r.Member("session_id") is { IsText: true } rs && rs.AsString() == sid.AsString())
            {
                return r.Member("digest") is { IsText: true } d && d.AsString() == CliRootDigest(line);
            }
        }

        return false;
    }

    /// <summary>The request context of a call made from the process's own working directory.</summary>
    internal RequestContext CwdContext()
    {
        var socket = _environment.GetVariable("CLAUDE_CODE_MESSAGING_SOCKET");
        return new RequestContext(
            _cwd,
            _config.HierarchyDir(_cwd),
            _config.MainHierarchyDir(_cwd) ?? string.Empty,
            string.IsNullOrEmpty(socket) ? string.Empty : NodePath.Dirname(socket));
    }

    private void AppendPeerRecordRouted(JsonValue record, TimeSpan? budget = null)
    {
        var writer = _writer ?? throw new InvalidOperationException("no state writer is configured");
        var result = writer.Invoke(
            "peer-records",
            "append",
            JsonText.ToUtf8(new JsonObject { ["rec"] = record }, 0),
            CwdContext(),
            new StateCallOptions(Hook: true, Budget: budget));
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? result.ErrorCode ?? "state call failed");
        }
    }

    public void RecordCliRootEmitted(JsonValue? sessionId, JsonValue? line)
    {
        if (sessionId is not { IsText: true } sid || sid.AsString().Length == 0)
        {
            return;
        }

        try
        {
            var row = new JsonObject { ["type"] = "cli-root", ["session_id"] = sid, ["digest"] = CliRootDigest(line), ["ts"] = IsoNowString() };
            AppendPeerRecordRouted(row, TimeSpan.FromMilliseconds(150));
        }
        catch (Exception)
        {
            // the record is an optimisation: never let it affect the prompt
        }
    }

    public void RecordPeerRow(JsonValue record) => AppendPeerRecordRouted(record);

    public JsonObject PeerRecordsAppendOp(IStoreWriteContext writer, JsonValue args, string? operationId)
    {
        var rec = args.Member("rec") ?? throw new InvalidOperationException("args.rec is undefined");
        if (rec.IsArray)
        {
            var rows = rec.AsArray();
            if (rows.Count == 0)
            {
                throw new InvalidOperationException("peer-records.append needs at least one row");
            }

            var out2 = new JsonArray();
            for (var i = 0; i < rows.Count; i++)
            {
                if (operationId is { Length: > 0 })
                {
                    var row = JsOps.Spread(rows[i]);
                    row["op_id"] = $"{operationId}:{i}";
                    out2.Add(row);
                }
                else
                {
                    out2.Add(rows[i]);
                }
            }

            AppendPeerRecord(writer, out2);
        }
        else if (operationId is { Length: > 0 })
        {
            var row = JsOps.Spread(rec);
            row["op_id"] = operationId;
            AppendPeerRecord(writer, row);
        }
        else
        {
            AppendPeerRecord(writer, rec);
        }

        return new JsonObject { ["ok"] = true };
    }

    /// <summary>The session ids with a registration, from the rows once the database holds them; null when the registrations directory is missing.</summary>
    private IReadOnlyList<string>? RegisteredSessions()
    {
        if (_sessions is { IsDatabase: true } store)
        {
            return [.. store.ListStored().Select(static row => row.Key)];
        }

        var dir = NodePath.Join(_environment.AhHome, "registrations");
        return Lstat(dir) is null ? null : [.. _files.List(dir).Where(static n => n.EndsWith(".json", StringComparison.Ordinal)).Select(static n => n[..^5])];
    }

    /// <summary>The registration of the session as it is stored, or null when it has none.</summary>
    private JsonValue? RegistrationDocument(string session)
    {
        if (_sessions is { IsDatabase: true } store)
        {
            return store.ReadStored(session) is { } stored ? JsonText.Parse(stored.Span) : (JsonValue?)null;
        }

        var path = NodePath.Join(_environment.AhHome, "registrations", session + ".json");
        return Lstat(path) is null ? (JsonValue?)null : JsonText.Parse(_files.ReadText(path));
    }

    /// <summary>
    /// The sessions that are not gone: every registration whose <see cref="SessionLive"/> is not false. A session with no registration, or
    /// one proven dead, is outside the set. Null when the registrations directory is missing or cannot be listed: with nothing known about
    /// which sessions run, the idle-session rule is off rather than treating every session as gone.
    /// </summary>
    private HashSet<string>? NotGoneSessions()
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (RegisteredSessions() is not { } sessions)
            {
                return null;
            }

            // ponytail: reads every registration on each run, so the cost grows with the registrations that pile up; narrow it to the sessions the log names if that ever shows.
            foreach (var session in sessions)
            {
                if (SessionLive(session) != false)
                {
                    _ = live.Add(session);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return live;
    }

    public int CountPeerRecordsToCompact(IReadOnlyCollection<string> closedRequestIds, IReadOnlyCollection<string> openRequestIds, double nowMs, int retentionDays, bool idleRule = true)
    {
        ArgumentNullException.ThrowIfNull(closedRequestIds);
        ArgumentNullException.ThrowIfNull(openRequestIds);
        IReadOnlyList<string> lines;
        if (_core.PeerRecordsInDb())
        {
            lines = [.. _core.ReadPeerRecords().Select(static r => JsonText.Stringify(r, 0))];
        }
        else
        {
            var pending = PeerPendingPath();
            if (!_files.Exists(pending))
            {
                return 0;
            }

            lines = _files.ReadText(pending).Split('\n');
        }

        return PeerLogRetention.DroppedLines(lines, new HashSet<string>(closedRequestIds, StringComparer.Ordinal), new HashSet<string>(openRequestIds, StringComparer.Ordinal), nowMs, retentionDays, idleRule ? NotGoneSessions() : null).Count;
    }

    public int CompactPeerRecords(IReadOnlyCollection<string> closedRequestIds, IReadOnlyCollection<string> openRequestIds, double nowMs, int retentionDays = PeerLogRetention.DefaultDays)
    {
        ArgumentNullException.ThrowIfNull(closedRequestIds);
        ArgumentNullException.ThrowIfNull(openRequestIds);
        var writer = _writer ?? throw new InvalidOperationException("no state writer is configured");
        var closed = new JsonArray();
        foreach (var id in closedRequestIds)
        {
            closed.Add(id);
        }

        var openIds = new JsonArray();
        foreach (var id in openRequestIds)
        {
            openIds.Add(id);
        }

        var notGone = NotGoneSessions();
        var notGoneIds = new JsonArray();
        foreach (var id in notGone ?? [])
        {
            notGoneIds.Add(id);
        }

        // Nothing to drop leaves no lock file and no journal row behind, so look first without the lock; the op repeats the work under it.
        // In db mode the peer-log file is frozen: neither its absence nor its text says anything about the rows to drop, so the op decides.
        if (!_core.PeerRecordsInDb())
        {
            var pending = PeerPendingPath();
            if (!_files.Exists(pending))
            {
                return 0;
            }

            _ = PeerLogRetention.Compact(_files.ReadText(pending), new HashSet<string>(closedRequestIds, StringComparer.Ordinal), new HashSet<string>(openRequestIds, StringComparer.Ordinal), nowMs, retentionDays, notGone, out var wouldDrop);
            if (wouldDrop == 0)
            {
                return 0;
            }
        }

        var arguments = new JsonObject { ["closed"] = closed, ["open"] = openIds, ["now_ms"] = JsonValue.FromNumber(nowMs), ["retention_days"] = retentionDays };
        if (notGone is not null)
        {
            arguments["live_sessions"] = notGoneIds;
        }

        var result = writer.Invoke(
            "peer-records",
            "compact",
            JsonText.ToUtf8(arguments, 0),
            CwdContext(),
            new StateCallOptions());
        if (!result.Ok)
        {
            throw new InvalidOperationException(result.Error ?? result.ErrorCode ?? "state call failed");
        }

        return JsonText.TryParse(result.ResultJson.Span, out var parsed) && parsed.Member("dropped") is { IsNumber: true } n ? (int)n.AsNumber() : 0;
    }

    public JsonObject PeerRecordsCompactOp(IStoreWriteContext writer, JsonValue args)
    {
        var closed = PeerLogRetention.IdSet(args, "closed");
        var open = PeerLogRetention.IdSet(args, "open");
        var path = PeerPendingPath();
        if (!_files.Exists(path))
        {
            return new JsonObject { ["dropped"] = 0 };
        }

        var readBytes = _files.ReadBytes(path);
        var next = PeerLogRetention.Compact(Utf8Text.Decode(readBytes), closed, open, JsConvert.ToNumber(args.Member("now_ms")), PeerLogRetention.RetentionOf(args), PeerLogRetention.LiveSessionsOf(args), out var dropped);
        if (next is null)
        {
            return new JsonObject { ["dropped"] = dropped };
        }

        // An appender writes without this lock, so the file may have grown since it was read; renaming over it would lose those rows.
        var tmp = path + "." + Environment.ProcessId + ".tmp";
        writer.Write(tmp, tmp + ".w", Utf8Text.Encode(next));
        if (_files.Stat(path)?.Size != readBytes.Length)
        {
            _ = writer.Remove(tmp);
            return new JsonObject { ["dropped"] = 0 };
        }

        writer.Rename(tmp, path);
        return new JsonObject { ["dropped"] = dropped };
    }

    public bool AppendRelayRecord(JsonValue? sessionId, JsonValue relay, DateTimeOffset? now = null, string? voidNote = null)
    {
        var task = $"relay:{JsConvert.ToJsString(relay.Member("entry"))}";
        var to = relay.Member("to");
        if (LatestByKey(_core.PeerRecordsLatestByKeyFor(sessionId)).Any(r =>
                r.Member("session_id") is { } rs && sessionId is { } sid && StrictEquals(rs, sid)
                && r.Member("task") is { IsText: true } rt && rt.AsString() == task
                && r.Member("from") is { } rf && to is { } rto && StrictEquals(rf, rto)))
        {
            return false;
        }

        var row = new JsonObject
        {
            ["session_id"] = sessionId ?? JsonValue.Null,
            ["from"] = to ?? JsonValue.Null,
            ["from_name"] = JsonValue.Null,
            ["reply_to"] = JsonValue.Null,
            ["task"] = task,
            ["status"] = voidNote is null ? "pending" : "resolved",
            ["nudges"] = 0,
            ["msg"] = JsonValue.Null,
            ["ts"] = Timestamps.ToIsoString(now ?? _clock.UtcNow),
            ["relay"] = relay,
        };
        if (voidNote is not null)
        {
            row["void"] = voidNote;
        }

        RecordPeerRow(row);
        return true;
    }

    /// <summary>The rows pending for <paramref name="sessionId"/> over the whole peer log, latest by key. The rows are the ones the peer-log cache holds: the caller must not change them.</summary>
    public JsonArray PendingFor(JsonValue? sessionId) => PendingOf(_core.PeerRecordsLatestByKeyFor(sessionId), sessionId);

    /// <summary>The one implementation of the pending rule over a list of peer rows, whatever the list was read from.</summary>
    public static JsonArray PendingOf(IReadOnlyList<JsonValue> candidateRows, JsonValue? sessionId) => [.. PeerRows.PendingOf(candidateRows, sessionId)];

    public void AppendTurnMarker(JsonValue? sessionId, JsonValue? status) =>
        RecordPeerRow(RowOf(("type", "turn"), ("session_id", sessionId), ("status", status), ("ts", IsoNowString())));

    public void AppendUserPromptRecord(string sessionId, string promptId) =>
        RecordPeerRow(RowOf(("type", "user-prompt"), ("session_id", sessionId), ("prompt_id", promptId), ("ts", IsoNowString())));

    public JsonValue? LatestUserPrompt(string sessionId)
    {
        JsonValue? last = null;
        foreach (var r in SessionRows(JsonValue.FromString(sessionId), "user-prompt"))
        {
            if (r.IsTruthy && Member(r, "type") is { IsText: true } t && t.AsString() == "user-prompt" && r.Member("session_id") is { IsText: true } rs && rs.AsString() == sessionId)
            {
                last = Own(r);
            }
        }

        return last;
    }

    public void AppendTurnEnd(string sessionId) =>
        RecordPeerRow(RowOf(("type", "turn-end"), ("session_id", sessionId), ("ts", IsoNowString())));

    public JsonValue? LatestTurnEnd(string sessionId)
    {
        JsonValue? last = null;
        foreach (var r in SessionRows(JsonValue.FromString(sessionId), "turn-end"))
        {
            if (r.IsTruthy && Member(r, "type") is { IsText: true } t && t.AsString() == "turn-end" && r.Member("session_id") is { IsText: true } rs && rs.AsString() == sessionId)
            {
                last = Own(r);
            }
        }

        return last;
    }

    private static JsonObject RowOf(params (string Key, JsonValue? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (k, v) in fields)
        {
            if (v is { } value)
            {
                o[k] = value;
            }
        }

        return o;
    }

    public JsonValue LatestTurnMarker(JsonValue? sessionId)
    {
        JsonValue last = JsonValue.Null;
        foreach (var r in SessionRows(sessionId, "turn"))
        {
            if (r.IsTruthy && Member(r, "type") is { IsText: true } t && t.AsString() == "turn" && r.Member("session_id") is { } rs && sessionId is { } sid && StrictEquals(rs, sid))
            {
                last = Own(r);
            }
        }

        return last;
    }

    public void AppendDispatchRecord(JsonValue? sessionId, JsonValue? requestId, JsonValue? recipient, JsonValue? path = null, JsonValue? toAddr = null)
    {
        var row = RowOf(("type", "dispatch"), ("session_id", sessionId), ("request_id", requestId), ("to", recipient));
        if (path is { IsTruthy: true } p)
        {
            row["path"] = p;
        }

        if (toAddr is { IsTruthy: true } a)
        {
            row["to_addr"] = a;
        }

        row["created"] = IsoNowString();
        RecordPeerRow(row);
    }

    public void AppendReportRecord(JsonValue? type, JsonValue? sessionId, JsonValue? requestId) =>
        RecordPeerRow(RowOf(("type", type), ("session_id", sessionId), ("request_id", requestId), ("ts", IsoNowString())));

    public bool ReportShown(JsonValue? sessionId, JsonValue? requestId) => ReportShownFor(sessionId)(requestId);

    public bool ReportAcked(JsonValue? sessionId, JsonValue? requestId) => ReportAckedFor(sessionId)(requestId);

    public void AppendOrchestratorAck(JsonValue? sessionId, JsonValue? requestId, string team) =>
        RecordPeerRow(RowOf(("type", "report-acked"), ("session_id", sessionId), ("request_id", requestId), ("role", "orchestrator"), ("team", team), ("ts", IsoNowString())));

    public Func<JsonValue?, bool> ReportAckedFor(JsonValue? sessionId, IReadOnlyCollection<string>? teams = null) =>
        ReportAckedBy(session => sessionId is { IsText: true } sid && sid.AsString() == session, teams);

    public Func<JsonValue?, bool> ReportAckedBy(Func<string, bool> counts, IReadOnlyCollection<string>? teams = null) =>
        PeerReach(_core.PeerRecordsWhere(new PeerQuery(Types: TypeSet("reopened", "report-acked", "sent"))), MemoizedRealPath()).AckedBy(counts, teams);

    public Func<JsonValue?, bool> ReportShownFor(JsonValue? sessionId) => ReportShownFor(sessionId, SessionRows(sessionId, "seen", "surfaced"));

    public Func<JsonValue?, bool> ReportShownFor(JsonValue? sessionId, IReadOnlyList<JsonValue> records)
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (r.IsTruthy && Member(r, "type") is { IsText: true } t && t.AsString() is "seen" or "surfaced"
                && r.Member("session_id") is { } rs && sessionId is { } sid && StrictEquals(rs, sid)
                && r.Member("request_id") is { IsText: true } rr)
            {
                _ = shown.Add(rr.AsString());
            }
        }

        return requestId => requestId is { IsText: true } rid && shown.Contains(rid.AsString());
    }

    public Func<string, bool> ResponseSentFor(Func<string, bool> toIsOrchestrator) =>
        PeerReach(_core.PeerRecordsWhere(new PeerQuery(Types: TypeSet("reopened", "report-acked", "sent"))), MemoizedRealPath()).SentFor(toIsOrchestrator);

    public JsonArray LatestDispatchRows(JsonValue? sessionId) => LatestDispatchRows(sessionId, SessionRows(sessionId, "dispatch"));

    public JsonArray LatestDispatchRows(JsonValue? sessionId, IReadOnlyList<JsonValue> records)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var unique = 0;
        foreach (var r in DispatchRecordsFor(sessionId, records))
        {
            var key = r.Member("request_id") is { } id ? JsOps.SetKey(id) ?? "ref:" + unique++ : "undefined";
            if (!byId.TryGetValue(key, out var earlier))
            {
                order.Add(key);
            }
            else if (CarryAddress(r, earlier) is { } carried)
            {
                byId[key] = carried;
                continue;
            }

            byId[key] = r;
        }

        var result = new JsonArray();
        foreach (var k in order)
        {
            result.Add(byId[k]);
        }

        return result;
    }

    public void AppendSubagentRecord(JsonValue? sessionId, JsonValue? agentId, JsonValue? status, JsonValue? kind = null, JsonValue? agentType = null) =>
        RecordPeerRow(RowOf(("type", "subagent"), ("session_id", sessionId), ("agent_id", agentId), ("status", status), ("ts", IsoNowString()), ("kind", kind), ("agent_type", agentType)));

    public JsonArray LatestSubagentRecords(JsonValue? sessionId)
    {
        var order = new List<string>();
        var byAgent = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var unique = 0;
        foreach (var r in SessionRows(sessionId, "subagent"))
        {
            if (r.IsTruthy && Member(r, "type") is { IsText: true } t && t.AsString() == "subagent"
                && r.Member("session_id") is { } rs && sessionId is { } sid && StrictEquals(rs, sid))
            {
                var key = r.Member("agent_id") is { } id ? JsOps.SetKey(id) ?? "ref:" + unique++ : "undefined";
                if (!byAgent.ContainsKey(key))
                {
                    order.Add(key);
                }

                byAgent[key] = Own(r);
            }
        }

        var result = new JsonArray();
        foreach (var k in order)
        {
            result.Add(byAgent[k]);
        }

        return result;
    }

    public bool HasInflightSubagent(JsonValue? sessionId, double? now = null)
    {
        var at = now ?? NowMs;
        foreach (var r in LatestSubagentRecords(sessionId))
        {
            if (!(Member(r, "status") is { IsText: true } st && st.AsString() == "started"))
            {
                continue;
            }

            if (r.Member("ts") is { } ts && Timestamps.TryParseMs(JsConvert.ToJsString(ts), out var t) && Math.Abs(at - t) <= HierarchyModule.SubagentInflightTtlMs)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The newest dispatch row of a request with the address of the newest earlier row that has one, when the newest has none; null
    /// when the newest row already has an address or no earlier row does.
    /// </summary>
    private static JsonValue? CarryAddress(JsonValue newest, JsonValue earlier)
    {
        if (!newest.IsMap || newest.Member("to_addr") is { IsTruthy: true } || earlier.Member("to_addr") is not { IsTruthy: true } address)
        {
            return null;
        }

        var copy = new JsonObject();
        var source = newest.AsObject();
        foreach (var key in source.Keys)
        {
            copy[key] = source[key];
        }

        copy["to_addr"] = address;
        return copy;
    }

    public JsonArray DispatchRecordsFor(JsonValue? sessionId) => DispatchRecordsFor(sessionId, SessionRows(sessionId, "dispatch"));

    private static JsonArray DispatchRecordsFor(JsonValue? sessionId, IReadOnlyList<JsonValue> records)
    {
        var result = new JsonArray();
        foreach (var r in records)
        {
            if (r.IsTruthy && Member(r, "type") is { IsText: true } t && t.AsString() == "dispatch"
                && r.Member("session_id") is { } rs && sessionId is { } sid && StrictEquals(rs, sid))
            {
                result.Add(r.IsMap ? r.AsObject().Clone() : r);
            }
        }

        return result;
    }

    public Dictionary<string, JsonValue> DispatchOrigin(JsonArray requests) => OriginsOf(requests, DispatchFacts);

    /// <summary>Per request id, how many dispatch records name it and the earliest <c>created</c> among them (null when none parses).</summary>
    internal Dictionary<string, (int Rows, JsonValue Origin)> DispatchFacts(IReadOnlyCollection<string> ids)
    {
        var facts = ids.ToDictionary(static id => id, static _ => (Rows: 0, Origin: JsonValue.Null), StringComparer.Ordinal);
        if (facts.Count == 0)
        {
            return facts;
        }

        var openRequests = new PeerQuery("request_id", Types: new HashSet<string>(StringComparer.Ordinal) { "dispatch" }, Values: new HashSet<string>(facts.Keys, StringComparer.Ordinal));
        foreach (var r in _core.PeerRecordsWhere(openRequests))
        {
            if (!r.IsTruthy || !(Member(r, "type") is { IsText: true } t && t.AsString() == "dispatch"))
            {
                continue;
            }

            var rid = r.Member("request_id") is { } ridv ? JsConvert.ToJsString(ridv) : "undefined";
            if (!facts.TryGetValue(rid, out var s))
            {
                continue;
            }

            var origin = s.Origin;
            if (r.Member("created") is { } created && Timestamps.TryParseMs(JsConvert.ToJsString(created), out var tm)
                && (origin.IsNull || (Timestamps.TryParseMs(JsConvert.ToJsString(origin), out var originMs) && tm < originMs)))
            {
                origin = created;
            }

            facts[rid] = (s.Rows + 1, origin);
        }

        return facts;
    }

    /// <summary>When each request was dispatched: its earliest dispatch record's <c>created</c>, else its own <c>created</c> when a record names it, else null.</summary>
    internal static Dictionary<string, JsonValue> OriginsOf(JsonArray requests, Func<IReadOnlyCollection<string>, Dictionary<string, (int Rows, JsonValue Origin)>> factsOf)
    {
        var created = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var req in requests)
        {
            var id = JsConvert.ToJsString(req.Member("id"));
            if (!created.ContainsKey(id))
            {
                order.Add(id);
            }

            created[id] = req.Member("created") ?? JsonValue.Null;
        }

        var facts = factsOf(order);
        var result = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var id in order)
        {
            var s = facts.TryGetValue(id, out var f) ? f : (Rows: 0, Origin: JsonValue.Null);
            result[id] = !s.Origin.IsNull ? s.Origin : s.Rows > 0 ? created[id] : JsonValue.Null;
        }

        return result;
    }

    public bool TargetSatisfiesRecord(JsonValue? target, JsonValue record)
    {
        if (_config.IsGatedPeerTarget(target, record.Member("from")))
        {
            return true;
        }

        if (_config.IsGatedPeerTarget(target, record.Member("from_name")))
        {
            return true;
        }

        return !(record.Member("reply_to") is { IsText: true } rt && rt.AsString() == "sender") && _config.IsGatedPeerTarget(target, record.Member("reply_to"));
    }

    public JsonArray UnconsumedWatchEvents(JsonValue? sessionId)
    {
        var rows = SessionRows(sessionId, "watch-consumed", "watch-event").Where(r => r.IsTruthy && r.Member("session_id") is { } rs && sessionId is { } sid && StrictEquals(rs, sid)).ToList();
        var consumed = rows
            .Where(r => Member(r, "type") is { IsText: true } t && t.AsString() == "watch-consumed")
            .Select(r => r.Member("ts"))
            .OrderBy(ts => JsConvert.ToJsString(ts), StringComparer.Ordinal)
            .LastOrDefault();
        var consumedText = consumed is { IsTruthy: true } c ? JsConvert.ToJsString(c) : string.Empty;
        var result = new JsonArray();
        foreach (var r in rows)
        {
            if (Member(r, "type") is { IsText: true } t && t.AsString() == "watch-event"
                && string.CompareOrdinal(JsConvert.ToJsString(r.Member("ts")), consumedText) > 0)
            {
                result.Add(Own(r));
            }
        }

        return result;
    }
}
