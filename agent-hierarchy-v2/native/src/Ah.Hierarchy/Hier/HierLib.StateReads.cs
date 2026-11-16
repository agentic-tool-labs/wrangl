using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

/// <summary>The read operations of the hierarchy state domains that return what a library read of the same store returns.</summary>
internal sealed partial class HierLib
{
    /// <summary><c>session-pids.pids</c>: <c>{dir}</c> is <c>{sessions: [{session_id, pid}]}</c>, the latest row of each session, oldest first.</summary>
    internal JsonValue SessionPidsReadOp(JsonValue args)
    {
        var sessions = new JsonArray();
        foreach (var entry in SessionPidsOf(ArgText(args, "dir")))
        {
            sessions.Add(new JsonObject { ["session_id"] = entry.Key, ["pid"] = entry.Value });
        }

        return new JsonObject { ["sessions"] = sessions };
    }

    /// <summary>
    /// <c>session-pids.of-pid</c>: <c>{dir, pid, not_before}</c> is <c>{rows: [{session_id, at}]}</c>, the sessions that recorded
    /// <c>pid</c> at or after <c>not_before</c> (an ISO-8601 time), newest first, with <c>at</c> in the same form.
    /// </summary>
    internal JsonValue SessionsOfPidOp(JsonValue args)
    {
        if (args.Member("pid") is not { IsNumber: true } pid || pid.AsNumber() != Math.Floor(pid.AsNumber()))
        {
            throw new InvalidOperationException("The \"pid\" argument must be an integer");
        }

        if (!DateTimeOffset.TryParse(ArgText(args, "not_before"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var notBefore))
        {
            throw new InvalidOperationException("The \"not_before\" argument must be a time");
        }

        var rows = new JsonArray();
        foreach (var row in SessionsOfPid(ArgText(args, "dir"), (int)pid.AsNumber(), notBefore))
        {
            rows.Add(new JsonObject
            {
                ["session_id"] = row.SessionId,
                ["at"] = row.At.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        return new JsonObject { ["rows"] = rows };
    }

    /// <summary><c>status.read</c>: <c>{dir}</c> is the status document of that hierarchy directory now, carrying the config's <c>enabled</c> as the old status.json did.</summary>
    /// <remarks><paramref name="witness"/> is the compute's (b459 C1): it travels beside this one document in the op's result, in process only.</remarks>
    internal JsonValue StatusReadOp(JsonValue args, out StatusWitness? witness) => ReadStatusWitnessed(ArgText(args, "dir"), null, out witness);

    /// <summary>
    /// <c>status.stamp</c>: <c>{dir}</c> is <c>{stamp}</c>, the revision of the <c>status_entries</c> rows that directory's document is
    /// assembled from (<c>maxChangedMs:rowCount</c>, so a delete moves it too); empty when there are none or no database.
    /// </summary>
    internal JsonValue StatusStampOp(JsonValue args) => new JsonObject { ["stamp"] = StatusRevision(MsgRows.PoolKey(ArgText(args, "dir"))) };

    // b459: the store's aggregate; reading every row key to take max+count was ~28% of a live ahd's CPU (10k rows per /v1/status)
    private string StatusRevision(string dir) =>
        _statusEntries?.Revision(dir) ?? (_statusEntries?.ReadNewerThan(dir, long.MaxValue) is { Count: > 0 } rows
            ? rows.Max(static r => r.ChangedMs).ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + rows.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty);

    /// <summary><c>activity.read</c>: <c>{dir, subject}</c> is the activity record of that session or pane (<c>null</c> when there is none).</summary>
    internal JsonValue ActivityReadOp(JsonValue args) => ReadActivityRecord(ArgText(args, "dir"), args.Member("subject"));

    /// <summary>
    /// <c>msgs.open-exchanges</c>: <c>{dir, team?, all_teams?}</c> is <c>{exchanges: [{id, slug, request_path, front_matter, age_seconds}]}</c>,
    /// the open exchanges of <c>team</c> (the default team when absent) or of every team when <c>all_teams</c> is true, each with the
    /// front matter of its request file (null when unreadable) and the age of its <c>created</c> time (null when it cannot be parsed).
    /// </summary>
    internal JsonValue MsgsOpenExchangesOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var team = args.Member("team") is { IsText: true } t ? t.AsString() : null;
        var items = args.Member("all_teams") is { IsBool: true } all && all.AsBool()
            ? ListLiveExchangesAllTeams(dir).Where(static e => e.Member("open")?.IsTruthy == true)
            : OpenExchanges(dir, team is null ? JsonValue.Null : team, ListLiveExchangesAcross(dir, team is null ? [] : [team]));
        var rows = new JsonArray();
        foreach (var e in items)
        {
            var id = e.Member("id") is { IsText: true } i ? i.AsString() : null;
            var path = e.Member("request")?.Member("path") is { IsText: true } p ? p.AsString() : null;
            if (id is null || path is null)
            {
                continue;
            }

            var fm = _core.ReadMsgFile(path).Member("fm") is { IsMap: true } map ? map : JsonValue.Null;
            var age = AgeSecOf(fm.IsNull ? null : fm.Member("created"));
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["slug"] = e.Member("slug") is { IsText: true } s ? s : JsonValue.Null,
                ["request_path"] = path,
                ["front_matter"] = fm,
                ["age_seconds"] = double.IsFinite(age) ? age : JsonValue.Null,
            });
        }

        return new JsonObject { ["exchanges"] = rows };
    }

    /// <summary>
    /// <c>activity.keys</c>: <c>{dir}</c> is <c>{subjects: [...]}</c>, the sessions and panes that have an activity record in that
    /// hierarchy directory, from the <c>activity</c> rows.
    /// </summary>
    internal JsonValue ActivityKeysOp(JsonValue args)
    {
        var subjects = new JsonArray();
        foreach (var subject in (_activity?.Rows(ArgText(args, "dir")) ?? []).Select(static r => r.Subject).Order(StringComparer.Ordinal))
        {
            subjects.Add(subject);
        }

        return new JsonObject { ["subjects"] = subjects };
    }

    private const int MsgsWindow = 8192;
    private const int MsgsExcerptCap = 4096;

    /// <summary>
    /// <c>msgs.keys</c>: <c>{dir}</c> is <c>{ids: [...]}</c>, the ids of the exchanges (a request file is present) in that directory's
    /// message pool, newest first, by one directory listing. It reads no file's content and does not list the archive.
    /// </summary>
    internal JsonValue MsgsKeysOp(JsonValue args)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var name in DbMsgNames(ArgText(args, "dir")))
        {
            if (MessageText.ParseFilename(name) is { Type: "request" } parts)
            {
                _ = ids.Add(parts.Id);
            }
        }

        var result = new JsonArray();
        foreach (var id in ids.Reverse())
        {
            result.Add(id);
        }

        return new JsonObject { ["ids"] = result };
    }

    /// <summary>
    /// <c>msgs.read</c>: <c>{dir, id}</c> is the header of the exchange and an excerpt and the byte size of each of its files, or <c>null</c> when the pool
    /// holds no request of that id. It makes at most two ranged reads of <see cref="MsgsWindow"/> bytes from the start of a file; an
    /// excerpt is the part of the body inside that window, at most <see cref="MsgsExcerptCap"/> bytes, cut on a character boundary.
    /// A file whose front matter does not close inside the window fails the read (a write in progress), so the caller keeps its slice.
    /// </summary>
    internal JsonValue MsgsReadOp(JsonValue args)
    {
        var dir = ArgText(args, "dir");
        var msgs = MsgsDir(dir);
        var id = ArgText(args, "id");
        using var db = _core.DbMsgs();
        // ah2-1: the id's messages come with their bodies in one read; a source that cannot gives the listing, and each body is read by path
        var withBodies = db.MessagesWithBodies(dir, id);
        var (requestName, responseName) = MsgsListingIndex.For([.. (withBodies ?? db.Messages(dir, id)).Select(static m => m.Name)]).Of(id);
        if (requestName is null)
        {
            return JsonValue.Null;
        }

        var name0 = MessageText.ParseFilename(requestName)!;
        var request = ReadMsgWindow(msgs, requestName, db, withBodies);
        var response = responseName is null ? (MsgWindow?)null : ReadMsgWindow(msgs, responseName, db, withBodies);
        var (title, titleSource) = MessageText.ResolveTitle(
            FieldOrNull(request.Fields, "title") is { IsText: true } fmTitle ? fmTitle.AsString() : null,
            request.Error is null ? request.Excerpt : null);
        var result = new JsonObject(30)
        {
            ["id"] = id,
            ["slug"] = name0.Slug,
            ["title"] = title is null ? JsonValue.Null : title,
            ["title_source"] = titleSource is null ? JsonValue.Null : titleSource,
            ["to"] = name0.To,
            ["from"] = FieldOrNull(request.Fields, "from"),
            ["to_name"] = FieldOrNull(request.Fields, "to_name"),
            ["from_name"] = FieldOrNull(request.Fields, "from_name"),
            ["team"] = FieldOrNull(request.Fields, "team"),
            ["parent"] = FieldOrNull(request.Fields, "parent"),
            ["created"] = FieldOrNull(request.Fields, "created"),
            ["response_created"] = response is { } r ? FieldOrNull(r.Fields, "created") : JsonValue.Null,
            ["has_request"] = true,
            ["has_response"] = response is not null,
            ["partial"] = response is { } rp && rp.Fields.Get("partial") is { IsText: true } pt && pt.AsString() == "true",
            ["withdrawn"] = response is { } rw && rw.Fields.Get("withdrawn") is { IsText: true } wt && wt.AsString() == "true",
            ["closed"] = response is { } rc && rc.Fields.Get("closed") is { IsText: true } ct && ct.AsString() == "true",
            ["closed_by"] = response is { } rb && rb.Fields.Get("closed_by") is { IsText: true } cbt ? cbt.AsString() : JsonValue.Null,
            ["closed_reason"] = response is { } rr && rr.Fields.Get("closed_reason") is { IsText: true } crt ? crt.AsString() : JsonValue.Null,
            ["request_excerpt"] = request.Error is null ? request.Excerpt : JsonValue.Null,
            ["response_excerpt"] = response is { Error: null } r2 ? r2.Excerpt : JsonValue.Null,
            ["request_bytes"] = request.Error is null ? JsonValue.FromNumber(request.Bytes) : JsonValue.Null,
            ["response_bytes"] = response is { Error: null } r3 ? JsonValue.FromNumber(r3.Bytes) : JsonValue.Null,
            ["excerpt_truncated"] = request.Truncated || (response?.Truncated ?? false),
        };
        if (request.Error is null)
        {
            if (request.Fields.Get("item") is { IsText: true } item)
            {
                result["item"] = item;
            }

            result["eta"] = EtaOf(request.Fields.Get("eta"));
            result["eta_facts"] = response is null ? EtaFactsOf(ArgText(args, "dir"), id, request.Fields) : JsonValue.Null;
        }

        if ((request.Error ?? response?.Error) is { } error)
        {
            result["error"] = error;
        }

        return result;
    }

    /// <summary>
    /// What the files say about when an open request is due, as plain numbers: its send time, the latest word from the addressee, the
    /// moment the addressee began working (when it is working now), and the request's nudge and busy-look times. They come from the gates
    /// file, the peer records, the registrations and the activity records only, never from the clock.
    /// </summary>
    private JsonValue EtaFactsOf(string dir, string id, JsonObject request)
    {
        var eta = EtaOf(request.Get("eta"));
        var start = request.Get("created") is { IsText: true } created && Timestamps.TryParseMs(created.AsString(), out var createdMs) ? createdMs : 0;
        var (session, toAddr) = DispatchTargetOf(id);
        var heard = HeardMsOf(session, toAddr);
        var addressee = toAddr is null ? null : SessionOfAddress(toAddr);
        double busySince = 0;
        double busyAt = 0;
        double lastActivity = 0;
        if (addressee is not null && ReadActivity(dir, addressee + ".json") is { IsMap: true } activity)
        {
            foreach (var key in new[] { "at", "tool_at", "working_at" })
            {
                lastActivity = Math.Max(lastActivity, activity.Member(key) is { IsText: true } stamp && Timestamps.TryParseMs(stamp.AsString(), out var stampMs) ? stampMs : 0);
            }

            if (activity.Member("activity") is { IsText: true } status && status.AsString() == "working")
            {
                busySince = activity.Member("working_since") is { IsText: true } since && Timestamps.TryParseMs(since.AsString(), out var sinceMs) ? sinceMs : 0;
                busyAt = activity.Member("working_at") is { IsText: true } worked && Timestamps.TryParseMs(worked.AsString(), out var workedMs) ? workedMs : 0;
            }
        }

        var nudges = new JsonArray();
        var looks = new JsonArray();
        if (LivenessGateTimes(dir).TryGetValue(id, out var times))
        {
            foreach (var at in times.Nudges)
            {
                nudges.Add(JsonValue.FromNumber(at));
            }

            foreach (var at in times.Looks)
            {
                looks.Add(JsonValue.FromNumber(at));
            }
        }

        return new JsonObject
        {
            ["eta"] = eta,
            ["threshold_s"] = JsonValue.FromNumber(ThresholdFor(request.Get("eta"))),
            ["start_ms"] = JsonValue.FromNumber(start),
            ["heard_ms"] = JsonValue.FromNumber(heard),
            ["addressee_session"] = addressee is null ? JsonValue.Null : JsonValue.FromString(addressee),
            ["busy_since_ms"] = JsonValue.FromNumber(busySince),
            ["busy_at_ms"] = JsonValue.FromNumber(busyAt),
            ["activity_ms"] = JsonValue.FromNumber(lastActivity),
            ["nudge_ms"] = nudges,
            ["checked_ms"] = looks,
        };
    }

    private sealed record GateTimes(List<double> Nudges, List<double> Looks);

    private readonly object _gateTimesGate = new();
    private readonly Dictionary<string, (string Stamp, Dictionary<string, GateTimes> ById)> _gateTimes = new(StringComparer.Ordinal);

    /// <summary>
    /// The liveness-nudge and liveness-checked times of <paramref name="dir"/>'s gate log by request id, in log order. The log is parsed
    /// once per gate-store stamp, not once per open request read (b459: 19 open requests over 8k gate rows made each
    /// messages reread parse ~157k rows). Without a stamp (a store that cannot say) it is parsed for this call only.
    /// </summary>
    private Dictionary<string, GateTimes> LivenessGateTimes(string dir)
    {
        var stamp = _gates?.Stamp(dir);
        lock (_gateTimesGate)
        {
            if (stamp is not null && _gateTimes.TryGetValue(dir, out var held) && held.Stamp == stamp)
            {
                return held.ById;
            }
        }

        var byId = new Dictionary<string, GateTimes>(StringComparer.Ordinal);
        // a failed read is no times for this call and is never cached: its stamp may still be good
        if (ReadGatesOrNull(dir, out _) is not { } gates)
        {
            return byId;
        }

        foreach (var gate in gates)
        {
            if (!gate.IsMap)
            {
                continue;
            }

            var row = gate.AsObject();
            var kind = row.Get("type") is { IsText: true } type ? type.AsString() : string.Empty;
            if (kind is not ("liveness-nudge" or "liveness-checked") || row.Get("request_id") is not { IsText: true } rid
                || !(row.Get("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var at)))
            {
                continue;
            }

            if (!byId.TryGetValue(rid.AsString(), out var times))
            {
                times = new GateTimes([], []);
                byId[rid.AsString()] = times;
            }

            (kind == "liveness-nudge" ? times.Nudges : times.Looks).Add(at);
        }

        if (stamp is not null)
        {
            lock (_gateTimesGate)
            {
                _gateTimes[dir] = (stamp, byId);
            }
        }

        return byId;
    }

    private (string? Session, string? ToAddr) DispatchTargetOf(string id)
    {
        string? session = null;
        string? toAddr = null;
        foreach (var record in _core.PeerRecordsWhere(new PeerQuery("request_id", id, new HashSet<string>(StringComparer.Ordinal) { "dispatch" })))
        {
            if (record.IsMap && record.AsObject().Get("type") is { IsText: true } kind && kind.AsString() == "dispatch"
                && record.AsObject().Get("request_id") is { IsText: true } rid && rid.AsString() == id)
            {
                session = record.AsObject().Get("session_id") is { IsText: true } sid ? sid.AsString() : null;
                toAddr = record.AsObject().Get("to_addr") is { IsText: true } to ? to.AsString() : null;
            }
        }

        return (session, toAddr);
    }

    private double HeardMsOf(string? session, string? toAddr)
    {
        double heard = 0;
        if (session is null || toAddr is null)
        {
            return heard;
        }

        foreach (var record in _core.PeerRecordsWhere(new PeerQuery("session_id", session, new HashSet<string>(StringComparer.Ordinal) { "heard" })))
        {
            if (record.IsMap && record.AsObject().Get("type") is { IsText: true } kind && kind.AsString() == "heard"
                && record.AsObject().Get("session_id") is { IsText: true } sid && sid.AsString() == session
                && record.AsObject().Get("from") is { IsText: true } from && from.AsString() == toAddr
                && record.AsObject().Get("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var ms))
            {
                heard = Math.Max(heard, ms);
            }
        }

        return heard;
    }

    /// <summary>
    /// The session of the newest registration that holds <paramref name="address"/>, or null. A bare member name (no <c>@team</c>, as dispatch rows
    /// write <c>to_addr</c>) matches a registered <c>&lt;name&gt;@&lt;team&gt;</c>.
    /// </summary>
    private string? SessionOfAddress(string address)
    {
        bool Same(string registered) => registered == address || (!address.Contains('@', StringComparison.Ordinal) && registered.StartsWith(address + "@", StringComparison.Ordinal));

        string? best = null;
        var bestAt = string.Empty;
        try
        {
            // the rows come with their documents in one read; the files are read one by one
            var stored = _sessions is { IsDatabase: true } store
                ? store.ListStored().Select(static row => (Session: row.Key, Document: (JsonValue?)JsonText.Parse(row.Value.Span)))
                : (RegisteredSessions() ?? []).Select(session => (Session: session, Document: RegistrationDocument(session)));
            foreach (var (session, document) in stored)
            {
                if (document is not { } registration)
                {
                    continue;
                }

                var holds = registration.Member("address") is { IsText: true } one && Same(one.AsString())
                    || registration.Member("addresses") is { IsArray: true } many && many.AsArray().Any(a => a.IsText && Same(a.AsString()));
                var at = registration.Member("at") is { IsText: true } t ? t.AsString() : string.Empty;
                if (holds && string.CompareOrdinal(at, bestAt) >= 0)
                {
                    best = session;
                    bestAt = at;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return best;
        }

        return best;
    }

    /// <summary>
    /// <c>msgs.heard</c>: <c>{dir, ids}</c> is <c>{heard: {id: ms}}</c>, the latest time each listed request's addressee was heard from for
    /// the dispatching session. The peer records are passed over once for every id together, not once per id, and are not copied. An id
    /// with no dispatch row maps to 0.
    /// </summary>
    internal JsonValue MsgsHeardOp(JsonValue args)
    {
        var heard = new JsonObject();
        if (args.Member("ids") is not { IsArray: true } idArray)
        {
            return new JsonObject { ["heard"] = heard };
        }

        var ids = idArray.AsArray().Where(static i => i.IsText).Select(static i => i.AsString()).ToList();
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        var targets = new Dictionary<string, (string? Session, string? ToAddr)>(StringComparer.Ordinal);
        var dispatchRows = wanted.Count == 0
            ? []
            : _core.PeerRecordsWhere(new PeerQuery("request_id", null, new HashSet<string>(StringComparer.Ordinal) { "dispatch" }, null, wanted));
        foreach (var record in dispatchRows)
        {
            if (record.IsMap && record.AsObject().Get("type") is { IsText: true } kind && kind.AsString() == "dispatch"
                && record.AsObject().Get("request_id") is { IsText: true } rid && wanted.Contains(rid.AsString()))
            {
                targets[rid.AsString()] = (
                    record.AsObject().Get("session_id") is { IsText: true } sid ? sid.AsString() : null,
                    record.AsObject().Get("to_addr") is { IsText: true } to ? to.AsString() : null);
            }
        }

        var latest = new Dictionary<(string Session, string From), double>();
        foreach (var (session, toAddr) in targets.Values)
        {
            if (session is not null && toAddr is not null)
            {
                latest[(session, toAddr)] = 0;
            }
        }

        var heardRows = latest.Keys.Select(static k => k.Session).Distinct(StringComparer.Ordinal)
            .SelectMany(s => _core.PeerRecordsWhere(new PeerQuery("session_id", s, new HashSet<string>(StringComparer.Ordinal) { "heard" })));
        foreach (var record in heardRows)
        {
            if (record.IsMap && record.AsObject().Get("type") is { IsText: true } kind && kind.AsString() == "heard"
                && record.AsObject().Get("session_id") is { IsText: true } sid
                && record.AsObject().Get("from") is { IsText: true } from
                && latest.TryGetValue((sid.AsString(), from.AsString()), out var best)
                && record.AsObject().Get("ts") is { IsText: true } ts && Timestamps.TryParseMs(ts.AsString(), out var ms))
            {
                latest[(sid.AsString(), from.AsString())] = Math.Max(best, ms);
            }
        }

        foreach (var id in ids)
        {
            heard[id] = JsonValue.FromNumber(
                targets.TryGetValue(id, out var target) && target.Session is { } s && target.ToAddr is { } t ? latest[(s, t)] : 0);
        }

        return new JsonObject { ["heard"] = heard };
    }

    /// <summary>One file's first window: its front matter, its excerpt, and <c>Error</c> when the file can never be read as a message (it is not a regular file, or its front matter is missing or does not close inside the window).</summary>
    private readonly record struct MsgWindow(JsonObject Fields, string Excerpt, bool Truncated, string? Error = null, long Bytes = 0);

    private static JsonValue FieldOrNull(JsonObject fields, string key) =>
        fields.Get(key) is { IsText: true } text && text.AsString().Length > 0 && text.AsString() != "null" ? text : JsonValue.Null;

    /// <summary>The names of the pool's messages that are not archived.</summary>
    private IReadOnlyList<string> DbMsgNames(string dir)
    {
        using var db = _core.DbMsgs();
        return db.MessageNames(dir);
    }

    /// <summary>The first <see cref="MsgsWindow"/> bytes of a message's stored body.</summary>
    private static MsgWindow ReadMsgWindow(string msgs, string name, IMsgSource db, IReadOnlyList<MsgFileRow>? read = null)
    {
        var path = NodePath.Join(msgs, name);

        // a row already read stands for the read by path only when that path is its own
        if ((read?.FirstOrDefault(m => m.Name == name && m.Path == path) ?? db.ByPath(path)) is not { Bytes: { } stored })
        {
            return new MsgWindow(new JsonObject(), string.Empty, false, $"{name} has no body");
        }

        // ah2-1: the window is a span over the stored bytes, and the body is cut from the text without splitting it into lines
        ReadOnlySpan<byte> bytes = stored.AsSpan(0, Math.Min(stored.Length, MsgsWindow));
        var size = stored.Length;
        var windowCut = size > bytes.Length;
        if (windowCut)
        {
            bytes = bytes[..WithoutIncompleteTail(bytes)];
        }

        // ah2-1: the window is decoded into a rented buffer, so the only text made is the fields' values and the excerpt
        var rented = System.Buffers.ArrayPool<char>.Shared.Rent(System.Text.Encoding.UTF8.GetMaxCharCount(bytes.Length));
        JsonObject fields;
        string excerpt;
        try
        {
            var decoded = rented.AsSpan(0, System.Text.Encoding.UTF8.GetChars(bytes, rented));
            ReadOnlySpan<char> text = decoded[..Ah.Core.Text.MessageFront.CollapseCrLf(decoded)];
            if (Ah.Core.Text.MessageFront.ParseFrontmatter(text) is not { } frontmatter)
            {
                return new MsgWindow(new JsonObject(), string.Empty, false, $"the front matter of {name} is missing or does not close inside its first {MsgsWindow} bytes");
            }

            fields = frontmatter.Fields;
            excerpt = text[Ah.Core.Text.MessageFront.OffsetAfterLines(text, frontmatter.End)..].Trim('\n').ToString();
        }
        finally
        {
            System.Buffers.ArrayPool<char>.Shared.Return(rented);
        }

        var truncated = windowCut;
        if (System.Text.Encoding.UTF8.GetByteCount(excerpt) > MsgsExcerptCap)
        {
            var body = System.Text.Encoding.UTF8.GetBytes(excerpt);
            excerpt = System.Text.Encoding.UTF8.GetString(body, 0, CutBoundary(body, MsgsExcerptCap));
            truncated = true;
        }

        return new MsgWindow(fields, excerpt, truncated, null, size);
    }

    /// <summary>The largest length at most <paramref name="limit"/> at which <paramref name="bytes"/> can be cut without splitting a UTF-8 character.</summary>
    private static int CutBoundary(byte[] bytes, int limit)
    {
        var n = Math.Min(limit, bytes.Length);
        while (n > 0 && n < bytes.Length && (bytes[n] & 0xC0) == 0x80)
        {
            n--;
        }

        return n;
    }

    /// <summary>The length of <paramref name="bytes"/> without a trailing UTF-8 character that the end of the data cut short.</summary>
    private static int WithoutIncompleteTail(ReadOnlySpan<byte> bytes)
    {
        var i = bytes.Length - 1;
        while (i > 0 && (bytes[i] & 0xC0) == 0x80)
        {
            i--;
        }

        if (i < 0)
        {
            return 0;
        }

        var lead = bytes[i];
        var width = lead >= 0xF0 ? 4 : lead >= 0xE0 ? 3 : lead >= 0xC0 ? 2 : 1;
        return i + width > bytes.Length ? i : bytes.Length;
    }

    /// <summary><c>known-dirs.read</c>: <c>{}</c> is <c>{dirs: [...]}</c>, the hierarchy directories the user's known-dirs file lists.</summary>
    internal JsonValue KnownDirsReadOp()
    {
        var dirs = new JsonArray();
        foreach (var dir in KnownHierarchyDirs())
        {
            dirs.Add(dir);
        }

        return new JsonObject { ["dirs"] = dirs };
    }
}
