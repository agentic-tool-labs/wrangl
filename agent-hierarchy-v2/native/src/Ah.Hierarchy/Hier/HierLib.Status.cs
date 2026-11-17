using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

internal sealed partial class HierLib : IHierarchyStatus
{
    private const double ReportedWindowMs = 600 * 1000;
    private const double ExpiresAfterMs = 24 * 3600 * 1000;
    private const int DispatchCap = 50;
    private const int ItemCap = 20;
    private const int NameCap = 64;
    private const int MaxHeadBytes = 4096;
    private const int SlugCap = 32;
    private const int NoteCap = 80;
    private const int ActivityCap = 4096;

    /// <summary>How many one-minute cells of busy history an activity record keeps.</summary>
    private const int MinutesKept = 60;
    private const int OwnerSessionsCap = 32;

    private static readonly string[] Activities = [.. HierarchyModule.SelfState.Select(p => p.Value), "failed", "unknown"];

    [GeneratedRegex(@"\u001b\[[0-?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(?:\u0007|\u001b\\)?|\u001b[PX^_][^\u001b]*(?:\u001b\\)?|\u001b[@-Z\\-_]", RegexOptions.CultureInvariant)]
    private static partial Regex EscSeqRegex();

    [GeneratedRegex(@"[\u0000-\u001f\u007f-\u009f]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlRegex();

    [GeneratedRegex(@"[‪-‮⁦-⁩]", RegexOptions.CultureInvariant)]
    private static partial Regex BidiRegex();

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(?::[0-9]{2}(?:\.[0-9]+)?)?(?:Z|[+-][0-9]{2}:?[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstantRegex();

    [GeneratedRegex(@"^(?<head>[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2})(?<sec>:[0-9]{2}(?:\.[0-9]+)?)?(?<zone>Z|[+-][0-9]{2}:?[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex InstantPartsRegex();

    public int StatusSchema => StatusSchemaVersion;

    internal const int StatusSchemaVersion = 1;

    public int StatusSizeCap => 262144;

    public int RoundCap => RoundCapValue;

    internal const int RoundCapValue = 3;

    /// <summary>A string read from a file or a pane, safe to display; null when it is not a string.</summary>
    public string? Clean(JsonValue? value, JsonValue? cap = null) => CleanValue(value, cap);

    /// <summary>The one sanitizer: <see cref="Clean"/> and the view's pure rules both run it.</summary>
    internal static string? CleanValue(JsonValue? value, JsonValue? cap)
    {
        if (value is not { IsText: true } v)
        {
            return null;
        }

        var text = EscSeqRegex().Replace(v.AsString(), string.Empty);
        text = ControlRegex().Replace(text, string.Empty);
        text = BidiRegex().Replace(text, string.Empty);
        text = WellFormed(text);
        if (cap is { IsTruthy: true } c)
        {
            var points = new List<string>();
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    points.Add(text.Substring(i, 2));
                    i++;
                }
                else
                {
                    points.Add(text[i].ToString());
                }
            }

            var n = JsConvert.ToNumber(c);
            var end = double.IsNaN(n) ? 0 : Math.Truncate(n);
            if (end < 0)
            {
                end = Math.Max(points.Count + end, 0);
            }

            return string.Concat(points.Take((int)Math.Min(end, points.Count)));
        }

        return text;
    }

    private static string? CleanTo(JsonValue? value, int cap) => value is null ? null : value.Value.IsText ? CleanText(value.Value.AsString(), cap) : null;

    private static string CleanText(string text, int cap)
    {
        var s = EscSeqRegex().Replace(text, string.Empty);
        s = ControlRegex().Replace(s, string.Empty);
        s = BidiRegex().Replace(s, string.Empty);
        s = WellFormed(s);
        var points = new List<string>();
        for (var i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                points.Add(s.Substring(i, 2));
                i++;
            }
            else
            {
                points.Add(s[i].ToString());
            }
        }

        return string.Concat(points.Take(cap));
    }

    /// <summary><c>String.prototype.toWellFormed</c>: lone surrogates become U+FFFD.</summary>
    private static string WellFormed(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append(c).Append(s[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c))
            {
                sb.Append('�');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    private static JsonValue StrOrNull(string? s) => s is null ? JsonValue.Null : s;

    private static JsonValue CleanOrNull(JsonValue? value, int cap) => StrOrNull(CleanTo(value, cap) is { Length: > 0 } t ? t : null);

    /// <summary><c>Date.parse</c> of an instant that <see cref="InstantRegex"/> accepts: a missing seconds field and a colon-less offset are filled in.</summary>
    private static bool ParseInstant(string text, out double ms)
    {
        ms = double.NaN;
        var m = InstantPartsRegex().Match(text);
        if (!m.Success)
        {
            return false;
        }

        var zone = m.Groups["zone"].Value;
        if (zone.Length == 5)
        {
            zone = zone[..3] + ":" + zone[3..];
        }

        var sec = m.Groups["sec"].Success ? m.Groups["sec"].Value : ":00";
        return Timestamps.TryParseMs(m.Groups["head"].Value + sec + zone, out ms);
    }

    private FileStat? Lstat(string path)
    {
        try
        {
            return _files.Stat(path, followSymlinks: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ActivityFile(JsonValue? subject) =>
        subject is { IsText: true } s && s.AsString().Length > 0 && s.AsString() is not ("." or "..") && !s.AsString().Contains('/') && !s.AsString().Contains('\\')
            ? s.AsString() + ".json"
            : null;

    /// <summary>The subject an activity record name <c>&lt;subject&gt;.json</c> stands for (the key of its <c>activity</c> row), or null.</summary>
    internal static string? ActivitySubjectOf(string file) =>
        file.EndsWith(".json", StringComparison.Ordinal) && ActivityFile(JsonValue.FromString(file[..^5])) is not null ? file[..^5] : null;

    /// <summary>A stored activity body as a record: null when it is empty, over the size cap, not JSON, or not an object.</summary>
    private static JsonValue ParseActivity(string? body)
    {
        if (string.IsNullOrEmpty(body) || System.Text.Encoding.UTF8.GetByteCount(body) > ActivityCap)
        {
            return JsonValue.Null;
        }

        try
        {
            var rec = JsonText.Parse(body);
            return rec.IsMap ? rec : JsonValue.Null;
        }
        catch (JsonParseException)
        {
            return JsonValue.Null;
        }
    }

    /// <summary>The activity row of <paramref name="file"/> (<c>&lt;subject&gt;.json</c>) under <paramref name="dir"/>, as a record.</summary>
    public JsonValue ReadActivity(string dir, string file) =>
        ActivitySubjectOf(file) is { } subject && _activity?.Row(dir, subject) is { } row ? ParseActivity(row.Body) : JsonValue.Null;

    public JsonValue ReadActivityRecord(string dir, JsonValue? subject) =>
        ActivityFile(subject) is { } file ? ReadActivity(dir, file) : JsonValue.Null;

    /// <summary>Whether the record's <c>compacted_at</c> is later than its <c>state_since</c>; false when either is missing.</summary>
    private static bool CompactedAfterStateSince(JsonValue record) =>
        record.Member("compacted_at") is { IsText: true } compacted && ParseInstantLoose(compacted.AsString(), out var compactedMs)
        && record.Member("state_since") is { IsText: true } since && ParseInstantLoose(since.AsString(), out var sinceMs) && compactedMs > sinceMs;

    /// <summary>Whether a record has a compaction that no working write has followed: <c>compacted_at</c> is later than <c>working_at</c>, or there is no <c>working_at</c>.</summary>
    private static bool CompactedAfterWorking(JsonValue record) =>
        record.Member("compacted_at") is { IsText: true } compacted && ParseInstantLoose(compacted.AsString(), out var compactedMs)
        && !(record.Member("working_at") is { IsText: true } worked && ParseInstantLoose(worked.AsString(), out var workedMs) && workedMs >= compactedMs);

    /// <summary>
    /// The member's verdict, the first row that holds of: gone, failed, blocked, stalled-after-compact, working, waiting, idle,
    /// unknown. Pure: it reads nothing.
    /// </summary>
    internal static string MemberVerdictOf(MemberEvidence e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var idle = e.Activity == "idle";
        if (e.Live == false)
        {
            return "gone";
        }

        if (e.Activity == "failed")
        {
            return "failed";
        }

        if (e.Activity == "blocked" || e.Herdr == "blocked" || e.PromptRecognised)
        {
            return "blocked";
        }

        if (!e.Codex && e.DispatchOpen && e.CompactedAtMs is { } compactedMs && e.WorkingAtMs is { } workingMs && compactedMs > workingMs
            && (idle || e.Herdr is "idle" or "done")
            && e.NowMs - compactedMs >= e.DebounceMs
            // A member with background work open is waiting on it, not stalled.
            && e.BgOpen == 0 && e.Tag != "shell"
            // A compaction after the member had already gone idle interrupted nothing: state_since is held across it.
            && !(idle && e.StateSinceMs is { } idleSinceMs && idleSinceMs < compactedMs))
        {
            return "stalled-after-compact";
        }

        if (e.Activity == "working" || e.Tag == "busy" || e.Herdr == "working")
        {
            return "working";
        }

        if (idle)
        {
            return e.BgOpen > 0 || e.Tag == "shell" ? "waiting" : "idle";
        }

        return "unknown";
    }

    /// <summary>
    /// The verdict of the member an activity record belongs to, for a reader that has the record and the ListAgents tag but no pane
    /// reading. A missing record gives <c>unknown</c>.
    /// </summary>
    internal static string RecordVerdictOf(JsonValue record, double nowMs, string? tag, bool dispatchOpen, bool codex = false, bool? live = true, double? debounceMs = null)
    {
        if (!record.IsMap)
        {
            return MemberVerdictOf(new MemberEvidence { Live = live, Tag = tag, DispatchOpen = dispatchOpen, Codex = codex, NowMs = nowMs });
        }

        double? Instant(string field) => record.Member(field) is { IsText: true } text && ParseInstantLoose(text.AsString(), out var ms) ? ms : null;
        var recorded = record.Member("activity") is { IsText: true } activity ? activity.AsString() : null;
        // Only a session not known to be live: a live one may still be parked on a question the user has not answered.
        if (recorded == "blocked" && live != true && Instant("at") is { } blockedAt && nowMs - blockedAt > HierarchyModule.BlockedStaleMs)
        {
            recorded = "idle";
        }

        return MemberVerdictOf(new MemberEvidence
        {
            Live = live,
            Activity = recorded,
            AtMs = Instant("at"),
            StateSinceMs = Instant("state_since"),
            WorkingAtMs = Instant("working_at"),
            WorkingSinceMs = Instant("working_since"),
            BgOpen = HierarchyModule.IdleOnBackgroundWork(record, nowMs) ? (int)(JsConvert.AsInteger(record.Member("bg_open")) ?? 0) : 0,
            CompactedAtMs = Instant("compacted_at"),
            Tag = tag,
            DispatchOpen = dispatchOpen,
            Codex = codex,
            NowMs = nowMs,
            DebounceMs = debounceMs ?? new MemberEvidence().DebounceMs,
        });
    }

    /// <summary>
    /// A context reading as stored, reduced to <c>{used, window_source: "unknown", at}</c> and the <c>model</c> when it is a non-empty
    /// text, or null when it is not a reading. A record written with a window and percentage loses both: no denominator is trusted.
    /// </summary>
    private static JsonValue CleanContext(JsonValue? c)
    {
        if (c is not { } v || !(v.IsMap || v.IsArray))
        {
            return JsonValue.Null;
        }

        if (JsConvert.AsInteger(v.Member("used")) is not >= 0)
        {
            return JsonValue.Null;
        }

        if (!(v.Member("at") is { IsText: true } at && ParseInstantLoose(at.AsString(), out _)))
        {
            return JsonValue.Null;
        }

        var clean = new JsonObject { ["used"] = v.Member("used")!.Value, ["window_source"] = "unknown", ["at"] = at.AsString() };
        if (v.Member("model") is { IsText: true } model && model.AsString().Length > 0)
        {
            clean["model"] = model;
        }

        return clean;
    }

    private static bool ParseInstantLoose(string text, out double ms) =>
        Timestamps.TryParseMs(text, out ms) || (InstantRegex().IsMatch(text) && ParseInstant(text, out ms));

    /// <summary>
    /// The record a write of <paramref name="fields"/> over <paramref name="current"/> (null: none) leaves, as JSON text, or null when the
    /// write changes nothing that is kept.
    /// </summary>
    private string? NextActivityText(JsonValue current, JsonValue fields)
    {
        var activity = fields.Member("activity");
        var blockedBy = fields.Member("blocked_by") ?? JsonValue.Null;
        var note = fields.Member("note") ?? JsonValue.Null;
        var name = CleanTo(fields.Member("tool"), NameCap) is { Length: > 0 } nm ? nm : null;
        var nowMs = NowMs;
        var hasCurrent = !current.IsNull;
        var toolStale = !hasCurrent || !Member(current, "tool_at").IsTruthy
            || !(current.Member("tool_at") is { } ta && ParseInstantLoose(JsConvert.ToJsString(ta), out var toolMs) && nowMs - toolMs < HierarchyModule.ToolWriteIntervalSeconds * 1000);
        var fill = CleanContext(fields.Member("context"));
        var fillChanged = !fill.IsNull
            && (!hasCurrent || !Member(current, "context").IsTruthy || !StrictEqualsU(Member(current, "context").Member("pct"), fill.Member("pct")));
        var resetStreak = fields.Member("resetStreak") is { IsTruthy: true };
        var keep = new JsonObject();
        if (hasCurrent && !resetStreak && JsConvert.AsInteger(current.Member("streak")) is > 0 && current.Member("streak") is { } streak)
        {
            keep["streak"] = streak;
        }

        if (hasCurrent && current.Member("transcript_path") is { IsText: true } tp)
        {
            keep["transcript_path"] = tp;
        }

        foreach (var carried in new[] { "compacted_at", "bg_open" })
        {
            if (hasCurrent && current.Member(carried) is { IsNull: false } held)
            {
                keep[carried] = held;
            }
        }

        if (fields.Member("extra") is { } extra && !extra.IsNull)
        {
            foreach (var key in JsOps.Spread(extra).Keys)
            {
                var v = JsOps.Spread(extra)[key];
                if (!v.IsNull)
                {
                    keep[key] = v;
                }
            }
        }

        var sameFields = hasCurrent
            && keep.Keys.All(k => current.Member(k) is { } cv && keep.Get(k) is { } kv && StrictScalar(cv, kv))
            && (!resetStreak || !(current.Member("streak") is { } cs && JsConvert.ToNumber(cs) > 0));
        var thisMinute = (long)Math.Floor(nowMs / 60000);
        var minutes = KeptMinutes(hasCurrent ? current.Member("minutes") : null, thisMinute);
        var minuteAdded = activity is { IsText: true } working && working.AsString() == "working" && (minutes.Count == 0 || minutes[^1] != thisMinute);
        if (minuteAdded)
        {
            minutes.Add(thisMinute);
        }

        var sameState = hasCurrent
            && StrictEqualsU(current.Member("activity"), activity)
            && StrictEqualsU(Or(current.Member("blocked_by"), JsonValue.Null).IsNull ? JsonValue.Null : current.Member("blocked_by"), blockedBy)
            && StrictEqualsU(Or(current.Member("note"), JsonValue.Null).IsNull ? JsonValue.Null : current.Member("note"), note);
        var compacting = fields.Member("extra") is { IsMap: true } compactExtra && compactExtra.Member("compacted_at") is { IsNull: false };
        var isWorking = activity is { IsText: true } activityText && activityText.AsString() == "working";
        var compactedSinceWorking = isWorking && hasCurrent && CompactedAfterWorking(current);
        // A Stop after a compaction no working write followed ended a turn the compaction landed in: idle starts now, so the stall reads.
        // An idle write that is the compaction itself is not a Stop, and a compaction with no later Stop keeps the earlier state_since.
        var stoppedAfterCompaction = !compacting && hasCurrent && activity is { IsText: true } written && written.AsString() == "idle"
            && StrictEqualsU(current.Member("activity"), activity) && CompactedAfterWorking(current) && CompactedAfterStateSince(current);
        if (sameState && sameFields && !(name is not null && toolStale) && !fillChanged && !compactedSinceWorking && !stoppedAfterCompaction)
        {
            return null;
        }

        JsonObject? kept = null;
        if (name is null && hasCurrent)
        {
            kept = new JsonObject
            {
                ["tool"] = current.Member("tool") is { IsNull: false } t ? t : JsonValue.Null,
                ["tool_at"] = current.Member("tool_at") is { IsNull: false } tat ? tat : JsonValue.Null,
            };
        }

        {
            var unchanged = sameState && sameFields;
            var at = unchanged && current.Member("at") is { IsText: true } cat ? cat : (JsonValue)IsoNowOf(nowMs);
            var toolAt = IsoNowOf(nowMs);
            var heldFill = !fill.IsNull ? fill : (hasCurrent ? CleanContext(current.Member("context")) : JsonValue.Null);
            var doc = new JsonObject();
            Put(doc, "activity", activity);
            doc["at"] = at;
            doc["blocked_by"] = blockedBy;
            doc["note"] = note;
            if (name is not null)
            {
                doc["tool"] = name;
                doc["tool_at"] = toolAt;
            }
            else if (kept is not null)
            {
                doc["tool"] = kept["tool"];
                doc["tool_at"] = kept["tool_at"];
            }
            else
            {
                doc["tool"] = JsonValue.Null;
                doc["tool_at"] = JsonValue.Null;
            }

            foreach (var key in keep.Keys)
            {
                doc[key] = keep[key];
            }

            if (isWorking && !compacting)
            {
                doc["working_at"] = IsoNowOf(nowMs);
            }
            else if (hasCurrent && current.Member("working_at") is { IsNull: false } workingAt)
            {
                doc["working_at"] = workingAt;
            }

            var continuesWorking = isWorking && hasCurrent && StrictEqualsU(current.Member("activity"), activity);
            if (isWorking && !compacting && !continuesWorking)
            {
                doc["working_since"] = IsoNowOf(nowMs);
            }
            // Kept past the stint too: with the idle transition's state_since it proves a stint ahd did not see (board-derive § C3).
            // Presence never means "working now"; the activity does.
            else if (hasCurrent && current.Member("working_since") is { IsText: true } workingSince)
            {
                doc["working_since"] = workingSince;
            }

            if (!heldFill.IsNull)
            {
                doc["context"] = heldFill;
            }

            var sameActivity = hasCurrent && StrictEqualsU(current.Member("activity"), activity);
            if (!sameActivity || stoppedAfterCompaction)
            {
                doc["state_since"] = IsoNowOf(nowMs);
            }
            else if (current.Member("state_since") is { IsText: true } held)
            {
                doc["state_since"] = held;
            }

            if (minutes.Count > 0)
            {
                var list = new JsonArray();
                foreach (var m in minutes)
                {
                    list.Add(new JsonObject { ["m"] = (double)m, ["busy"] = true });
                }

                doc["minutes"] = list;
            }

            return JsonText.Stringify(doc, 0) + "\n";
        }
    }

    /// <summary>
    /// The hierarchy dir <paramref name="target"/> (<c>mirror_dir</c>) a member in a linked work tree also writes to: the main checkout's,
    /// where the daemon's readers look. Null when it is absent, empty, missing on disk, or the own dir.
    /// </summary>
    private string? ActivityMirror(JsonValue? target, string dir) =>
        target is { IsText: true } named && named.AsString().Length > 0 && MsgRows.PoolKey(named.AsString()) != MsgRows.PoolKey(dir) && Exists(named.AsString()) ? named.AsString() : null;

    private static string? ActivityBody(IDbOpTransaction tx, string dir, string subject) =>
        tx.Query("SELECT body FROM activity WHERE hier_dir = ?1 AND session_id = ?2", MsgRows.PoolKey(dir), subject) is [var row] ? row[0] as string : null;

    private static long Changes(IDbOpTransaction tx) => (long)tx.Query("SELECT changes()")[0][0]!;

    /// <summary>
    /// <c>activity.record</c> in the database: the record of <c>subject</c> under <c>dir</c> rewritten from <c>args</c> (the fields), and the
    /// same text under <c>mirror_dir</c>; <c>{written}</c> says whether anything changed.
    /// </summary>
    internal OpResult ActivityRecordDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = TextOf(args.Member("dir"));
        if (ActivityFile(args.Member("subject")) is null || string.IsNullOrEmpty(dir) || !Exists(dir))
        {
            return new JsonObject { ["written"] = false };
        }

        var subject = args.Member("subject")!.Value.AsString();
        if (NextActivityText(ParseActivity(ActivityBody(tx, dir, subject)), args) is not { } text)
        {
            return new JsonObject { ["written"] = false };
        }

        var now = (long)NowMs;
        foreach (var target in new[] { dir, ActivityMirror(args.Member("mirror_dir"), dir) }.OfType<string>())
        {
            tx.Execute("INSERT OR REPLACE INTO activity(hier_dir, session_id, body, updated_at) VALUES (?1, ?2, ?3, ?4)", MsgRows.PoolKey(target), subject, text, now);
        }

        return new JsonObject { ["written"] = true };
    });

    /// <summary>
    /// <c>activity.mark</c> in the database: sets <c>screen_clear_at</c> to <c>at</c> in the record of <c>subject</c> under <c>dir</c> and
    /// <c>mirror_dir</c>, where that row exists and holds no mark yet, and changes nothing else in it; <c>{written}</c> says whether any row
    /// changed. The watcher's mark that it read the member's screen and found no prompt on it (ah2-74); a later hook write drops it.
    /// </summary>
    internal OpResult ActivityMarkDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
        args.Member("at") is { IsText: true } at
            ? ScreenClearWrite(tx, args, "json_set(body, '$.screen_clear_at', ?3)", "IS NULL", at.AsString())
            : new JsonObject { ["written"] = false });

    /// <summary><c>activity.unmark</c> in the database: removes <c>screen_clear_at</c> from those rows where it is set, as <c>activity.mark</c> sets it.</summary>
    internal OpResult ActivityUnmarkDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
        ScreenClearWrite(tx, args, "json_remove(body, '$.screen_clear_at')", "IS NOT NULL", null));

    private JsonObject ScreenClearWrite(IDbOpTransaction tx, JsonValue args, string body, string when, string? at)
    {
        var dir = TextOf(args.Member("dir"));
        if (ActivityFile(args.Member("subject")) is null || string.IsNullOrEmpty(dir))
        {
            return new JsonObject { ["written"] = false };
        }

        var subject = args.Member("subject")!.Value.AsString();
        var changed = 0L;
        foreach (var target in new[] { dir, ActivityMirror(args.Member("mirror_dir"), dir) }.OfType<string>())
        {
            var sql = $"UPDATE activity SET body = {body} WHERE hier_dir = ?1 AND session_id = ?2 AND json_valid(body) AND json_extract(body, '$.screen_clear_at') {when}";
            if (at is null)
            {
                tx.Execute(sql, MsgRows.PoolKey(target), subject);
            }
            else
            {
                tx.Execute(sql, MsgRows.PoolKey(target), subject, at);
            }

            changed += Changes(tx);
        }

        return new JsonObject { ["written"] = changed > 0 };
    }

    /// <summary>
    /// <c>activity.put</c> (in-process only): stores <c>body</c> (JSON text) as the record of <c>subject</c> under <c>dir</c>, written at
    /// <c>updated_at_ms</c> (now when absent), as given; <c>{written: true}</c>.
    /// </summary>
    internal OpResult ActivityPutDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = TextOf(args.Member("dir")) ?? throw new InvalidOperationException("The \"dir\" argument must be of type string");
        if (ActivityFile(args.Member("subject")) is null || args.Member("body") is not { IsText: true } body)
        {
            throw new InvalidOperationException("activity.put needs a subject and a text body");
        }

        var at = JsConvert.AsInteger(args.Member("updated_at_ms")) ?? (long)NowMs;
        tx.Execute("INSERT OR REPLACE INTO activity(hier_dir, session_id, body, updated_at) VALUES (?1, ?2, ?3, ?4)", MsgRows.PoolKey(dir), args.Member("subject")!.Value.AsString(), body.AsString(), at);
        return new JsonObject { ["written"] = true };
    });

    /// <summary><c>activity.clear</c> in the database: deletes the record of <c>subject</c> under <c>dir</c> and <c>mirror_dir</c>; <c>{cleared}</c> is the own row's.</summary>
    internal OpResult ActivityClearDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = TextOf(args.Member("dir"));
        if (ActivityFile(args.Member("subject")) is null || string.IsNullOrEmpty(dir))
        {
            return new JsonObject { ["cleared"] = false };
        }

        var subject = args.Member("subject")!.Value.AsString();
        tx.Execute("DELETE FROM activity WHERE hier_dir = ?1 AND session_id = ?2", MsgRows.PoolKey(dir), subject);
        var cleared = Changes(tx) > 0;
        if (ActivityMirror(args.Member("mirror_dir"), dir) is { } mirror)
        {
            tx.Execute("DELETE FROM activity WHERE hier_dir = ?1 AND session_id = ?2", MsgRows.PoolKey(mirror), subject);
        }

        return new JsonObject { ["cleared"] = cleared };
    });

    /// <summary>The busy minutes a record holds that are within the last <see cref="MinutesKept"/>, oldest first, each once.</summary>
    private static List<long> KeptMinutes(JsonValue? held, long thisMinute)
    {
        var kept = new List<long>();
        if (held is { IsArray: true } array)
        {
            foreach (var entry in array.AsArray())
            {
                if (entry.IsMap && entry.Member("busy") is { IsBool: true } busy && busy.AsBool() && entry.Member("m") is { } raw)
                {
                    var number = JsConvert.ToNumber(raw);
                    if (!double.IsNaN(number) && number == Math.Floor(number) && number > thisMinute - MinutesKept && number <= thisMinute && !kept.Contains((long)number))
                    {
                        kept.Add((long)number);
                    }
                }
            }
        }

        kept.Sort();
        return kept;
    }

    private static bool StrictScalar(JsonValue a, JsonValue b) => !(a.IsMap || a.IsArray || b.IsMap || b.IsArray) && StrictEquals(a, b);

    private static string IsoNowOf(double ms) => JsOps.IsoString(ms);

    /// <summary>What a session with no registration file reads as: false means its records are prunable. Every session that writes activity registers at its start, except ones whose registration failed; such a record is idle for the whole sweep age and is rewritten by that session's next hook.</summary>
    private const bool MissingRegistrationIsLive = false;

    /// <summary>
    /// Whether the session that owns an activity record is running: false only when the shared liveness verdict over its registration's pid
    /// and start time is proven dead (or no registration exists, which leaves unclaimed data), true when it is live or undecided, null when
    /// the registration cannot be judged at all (a pane record, which names no session, or a registration that is unreadable or has no pid or start time).
    /// </summary>
    internal bool? SessionLive(string subject)
    {
        if (subject.StartsWith("pane-", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            if (RegistrationDocument(subject) is not { } registration)
            {
                return MissingRegistrationIsLive;
            }

            if (registration.Member("pid") is not { IsNumber: true } pid || registration.Member("pid_start") is not { IsText: true } start || start.AsString().Length == 0)
            {
                return null;
            }

            if (_core.Liveness is not { } liveness)
            {
                return _core.IdentityLive(pid, start) ? true : null;
            }

            return pid.AsNumber() is var number && number > 0 && number <= int.MaxValue && number == Math.Floor(number)
                ? liveness.PidVerdict((int)number, start.AsString()) != Ah.Core.Time.LivenessVerdict.ProvenDead
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    private string? PrunedFailedLine(string body, string subject)
    {
        try
        {
            var record = JsonText.Parse(body);
            if (record.Member("activity") is not { IsText: true } activity || activity.AsString() != "failed")
            {
                return null;
            }

            var when = record.Member("failed_at") is { IsText: true } failedAt ? failedAt.AsString() : record.Member("at") is { IsText: true } at ? at.AsString() : string.Empty;
            var member = subject;
            if (RegistrationDocument(subject)?.Member("address") is { IsText: true } address && address.AsString().Split('@')[0] is { Length: > 0 } name)
            {
                member = name;
            }

            return $"activity-prune: pruned failed record for {member} from {(when.Length >= 10 ? when[..10] : "an unknown date")}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    /// <summary>
    /// <c>activity.sweep</c> in the database: deletes each record under <c>dir</c> written before <c>cutoff_ms</c> whose session is proven
    /// gone; <c>{removed}</c>, with <c>pruned_failed</c> naming each failed record it removed.
    /// </summary>
    internal OpResult ActivitySweepDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = MsgRows.PoolKey(TextOf(args.Member("dir")) ?? throw new InvalidOperationException("The \"path\" argument must be of type string"));
        var cutoffMs = JsConvert.ToNumber(args.Member("cutoff_ms"));
        var removed = 0;
        var prunedFailed = new JsonArray();
        foreach (var row in tx.Query("SELECT session_id, body, updated_at FROM activity WHERE hier_dir = ?1 ORDER BY session_id", dir))
        {
            var subject = (string)row[0]!;
            if ((long)row[2]! < cutoffMs && SessionLive(subject) == false)
            {
                var line = PrunedFailedLine((string)row[1]!, subject);
                tx.Execute("DELETE FROM activity WHERE hier_dir = ?1 AND session_id = ?2", dir, subject);
                removed++;
                if (line is not null)
                {
                    prunedFailed.Add(line);
                }
            }
        }

        var result = new JsonObject { ["removed"] = removed };
        if (prunedFailed.Count > 0)
        {
            result["pruned_failed"] = prunedFailed;
        }

        return result;
    });

    private static string? TeamNameOf(JsonValue team, string? given) =>
        given ?? (Member(team, "team") is { IsText: true } text && text.AsString().Length > 0 ? text.AsString() : null);

    public JsonArray MemberFills(string dir, JsonValue team, string? teamName = null)
    {
        var teamKey = TeamNameOf(team, teamName);
        var roster = AttributedRoster(dir);
        var result = new JsonArray();
        var members = Member(team, "members") is { IsArray: true } ms ? ms.AsArray() : new JsonArray();
        foreach (var m in members)
        {
            if (!m.IsTruthy || !(m.Member("name") is { IsText: true } name) || _config.IsPaneMember(m))
            {
                continue;
            }

            var att = AttributedLiveness(roster, name, teamKey);
            var sessionId = Member(Member(att, "rec"), "session_id") is { IsText: true } sid ? sid.AsString() : null;
            var rec = sessionId is null ? JsonValue.Null : ReadActivity(dir, sessionId + ".json");
            result.Add(new JsonObject
            {
                ["name"] = name,
                ["role"] = m.Member("role") is { IsText: true } role && role.AsString().Length > 0 ? role : JsonValue.Null,
                ["session_id"] = StrOrNull(sessionId),
                ["context"] = rec.IsNull ? JsonValue.Null : CleanContext(rec.Member("context")),
            });
        }

        return result;
    }

    public JsonArray DescribeMembers(string dir, JsonValue team, JsonArray roster, string? teamName = null) =>
        DescribeMembers(new FileStatusSource(this), dir, team, roster, teamName, NowMs, null);

    private double? MtimeMs(string path)
    {
        try
        {
            return _core.MsgTimeMs(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? ReadTextOrNull(string path)
    {
        try
        {
            return _files.Stat(path) is { Kind: FileKind.File, Size: <= MaxHeadBytes } ? _files.ReadText(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private const int TitleCap = 120;
    private const int ReportLineCap = 160;

    /// <summary>One line of model-authored text for a terminal: the first line, without escape or control characters, trimmed, cut to <paramref name="cap"/> characters with an ellipsis.</summary>
    private static string? ProducerText(string? text, int cap)
    {
        if (text is null)
        {
            return null;
        }

        var line = CleanText(text.Split('\n')[0], int.MaxValue).Trim();
        if (line.Length == 0)
        {
            return null;
        }

        var cut = CleanText(line, cap);
        return cut.Length < line.Length ? cut.TrimEnd() + "…" : line;
    }

    /// <summary>The <c>session_pids</c> rows of <paramref name="dir"/>, oldest <c>at</c> first; none without a database.</summary>
    internal IReadOnlyList<SessionPidEntry> SessionPidRows(string? dir) =>
        string.IsNullOrEmpty(dir) || _sessionPids is null ? [] : _sessionPids.Rows(dir);

    /// <summary>Whether the session's pid under <paramref name="dir"/> changed: <paramref name="newestPid"/> is its newest recorded pid, or null.</summary>
    private bool SessionPidDue(string? dir, JsonValue? sessionId, JsonValue? pid, Func<string, string, long?> newestPid) =>
        !string.IsNullOrEmpty(dir) && Exists(dir) && sessionId is { IsText: true } sid && sid.AsString().Length > 0
        && JsConvert.AsInteger(pid) is { } p && newestPid(dir, sid.AsString()) != p;

    private long? NewestPidOf(string dir, string sessionId) => SessionPidRows(dir).LastOrDefault(r => r.SessionId == sessionId)?.Pid;

    public IReadOnlyList<SessionPidRow> SessionsOfPid(string dir, int pid, DateTimeOffset notBefore)
    {
        var found = new List<(DateTimeOffset At, int Order, string Session)>();
        var order = 0;
        foreach (var row in SessionPidRows(dir))
        {
            order++;
            if (row.Pid == pid && row.SessionId.Length > 0
                && DateTimeOffset.TryParse(row.At, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                && at >= notBefore)
            {
                found.Add((at, order, row.SessionId));
            }
        }

        return found.OrderByDescending(f => f.At).ThenByDescending(f => f.Order)
            .DistinctBy(f => f.Session, StringComparer.Ordinal).Select(f => new SessionPidRow(f.Session, f.At)).ToList();
    }

    /// <summary>
    /// The status rows of <paramref name="dir"/>'s session pids: the newest row of each session, oldest first, as <c>{ord, ts, session_id, pid}</c>.
    /// The status entries key them by session id and order them by <c>ord</c>; the file-backed status source reads the same rows.
    /// </summary>
    internal List<JsonObject> SessionStatusRows(string dir)
    {
        var latest = new List<SessionPidEntry>();
        foreach (var row in SessionPidRows(dir))
        {
            latest.RemoveAll(e => e.SessionId == row.SessionId);
            latest.Add(row);
        }

        return [.. latest.Select((r, i) => new JsonObject { ["ord"] = i, ["ts"] = r.At, ["session_id"] = r.SessionId, ["pid"] = r.Pid })];
    }

    /// <summary>
    /// <c>session-pids.append</c> in the database: when the session's newest pid under <c>dir</c> is not <c>pid</c>, upserts the
    /// (dir, session, pid) row with <c>at</c> now; <c>{written}</c> says whether it did.
    /// </summary>
    internal OpResult AppendSessionPidDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = TextOf(args.Member("dir"));
        long? Newest(string d, string s) =>
            tx.Query("SELECT pid FROM session_pids WHERE hier_dir = ?1 AND session_id = ?2 ORDER BY at DESC, rowid DESC LIMIT 1", MsgRows.PoolKey(d), s) is [var row] ? (long)row[0]! : null;
        if (!SessionPidDue(dir, args.Member("session_id"), args.Member("pid"), Newest))
        {
            return new JsonObject { ["written"] = false };
        }

        // REPLACE rather than an update, so a pid that comes back also takes the newest rowid: readers order by (at, rowid)
        tx.Execute(
            "INSERT OR REPLACE INTO session_pids(hier_dir, session_id, pid, at) VALUES (?1, ?2, ?3, ?4)",
            MsgRows.PoolKey(dir!),
            args.Member("session_id")!.Value.AsString(),
            JsConvert.AsInteger(args.Member("pid"))!.Value,
            IsoNowString());
        return new JsonObject { ["written"] = true };
    });

    public JsonObject ComputeStatus(string? cwd, double? nowMs = null, string? dir = null, bool? enabledOverride = null) =>
        ComputeStatusFrom(new FileStatusSource(this), cwd, nowMs ?? NowMs, dir ?? _config.HierarchyDir(cwd), enabledOverride);

    /// <summary>Whether <paramref name="dir"/> is a linked work tree's hierarchy directory: its repository's main checkout has another one.</summary>
    private bool IsLinkedTreeDir(string dir) =>
        AhPaths.RepoRootOf(dir) is { } root && _config.MainHierarchyDir(root) is { Length: > 0 } main && main != dir;

    /// <summary>
    /// <c>status.refresh</c> in the database (files-off-disk F3a): the document is served from <c>status_entries</c>, so nothing is
    /// written. A main-checkout dir whose entries were never built still gets its one background rebuild, as the file publish gave it.
    /// </summary>
    internal OpResult RefreshDb(OpCall call, IDbOpTransaction tx) => OpArgs.Run(call, args =>
    {
        var dir = TextOf(args.Member("dir"));
        if (string.IsNullOrEmpty(dir))
        {
            throw new InvalidOperationException("refresh needs the hierarchy dir");
        }

        if (!IsLinkedTreeDir(dir))
        {
            _ = ScheduleStatusEntries(dir);
        }

        return new JsonObject { ["written"] = false };
    });

    public string PlainStatus(JsonValue document, double nowMs)
    {
        var timeline = document.Member("timeline") is { IsArray: true } t ? t.AsArray() : throw new InvalidOperationException("Cannot read properties of undefined (reading '0')");
        var entry = timeline.Count > 0 ? timeline[0] : (JsonValue?)null;
        foreach (var e in timeline)
        {
            if (Member(e, "at") is { IsText: true } at && ParseInstantLoose(at.AsString(), out var ms) && ms <= nowMs)
            {
                entry = e;
            }
        }

        return entry is { } en && Member(en, "visible").IsTruthy ? $"ah · {JsConvert.ToJsString(en.Member("text"))}" : string.Empty;
    }

    // ---- routed entries: the library's own call sites into the state writer

    private string CwdOfDir(string dir)
    {
        return AhPaths.RepoRootOf(dir) ?? _cwd;
    }

    private JsonObject RefreshArgs(string dir, JsonObject? opts)
    {
        var cwd = TextOf(opts?.Get("cwd")) ?? CwdOfDir(dir);
        var args = new JsonObject
        {
            ["dir"] = dir,
            ["enabled"] = _config.ResolveConfig(cwd).Get("enabled") is { IsTruthy: true },
            ["event"] = opts?.Get("event") is { IsBool: true } ev ? ev.AsBool() : true,
        };
        if (opts?.Get("now") is { } now)
        {
            args["now"] = now;
        }

        return args;
    }

    private StatusRefreshCoalescer? _statusCoalescer;

    /// <summary>
    /// From now on a refresh the library asks for without options (the daemon's own after-write refresh) is run at most once per
    /// <paramref name="gap"/> for each directory; see <see cref="StatusRefreshCoalescer"/>. A refresh with options is always run at once.
    /// </summary>
    internal IDisposable CoalesceStatusRefresh(TimeSpan gap, Func<long>? nowMs = null)
    {
        var coalescer = new StatusRefreshCoalescer(dir => RefreshStatusNow(dir, null), gap, nowMs);
        Interlocked.Exchange(ref _statusCoalescer, coalescer)?.Dispose();

        // the daemon lives long enough for a never-built status dir's one background rebuild (StatusDocument)
        RunInBackground ??= static work => _ = Task.Run(work);
        return coalescer;
    }

    /// <summary>Recomputes and saves the status document of pool <paramref name="dir"/> through the state writer. Never throws.</summary>
    public void RefreshStatus(string dir, JsonObject? options = null)
    {
        if (options is null && !string.IsNullOrEmpty(dir) && _statusCoalescer is { } coalescer)
        {
            coalescer.Request(dir);
            return;
        }

        RefreshStatusNow(dir, options);
    }

    private void RefreshStatusNow(string dir, JsonObject? options)
    {
        try
        {
            if (!string.IsNullOrEmpty(dir) && Exists(dir) && !AhPaths.IsRemovedWorktreeDir(dir, Exists))
            {
                Call(PoolContext(dir), "status", "refresh", RefreshArgs(dir, options), options?.Get("hook") is { IsTruthy: true });
            }
        }
        catch (Exception)
        {
            // A status write never fails the operation that triggered it.
        }
    }

    public bool RecordActivityRouted(string dir, JsonValue subject, JsonObject fields, bool hook = false)
    {
        if (string.IsNullOrEmpty(dir) || !Exists(dir))
        {
            return false;
        }

        try
        {
            var args = new JsonObject { ["dir"] = dir, ["subject"] = subject };
            foreach (var k in fields.Keys)
            {
                args[k] = fields[k];
            }

            var result = Call(PoolContext(dir), "activity", "record", args, hook);
            return JsonText.Parse(result.ResultJson.Span).Member("written") is { IsBool: true } w && w.AsBool();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public bool MarkScreenClearRouted(string dir, JsonValue subject, string? at, string? mirrorDir = null)
    {
        if (string.IsNullOrEmpty(dir) || !Exists(dir))
        {
            return false;
        }

        try
        {
            var args = new JsonObject { ["dir"] = dir, ["subject"] = subject };
            if (at is not null)
            {
                args["at"] = at;
            }

            if (!string.IsNullOrEmpty(mirrorDir))
            {
                args["mirror_dir"] = mirrorDir;
            }

            var result = Call(PoolContext(dir), "activity", at is null ? "unmark" : "mark", args);
            return JsonText.Parse(result.ResultJson.Span).Member("written") is { IsBool: true } w && w.AsBool();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void ClearActivityRouted(string dir, JsonValue subject, bool hook = false, string? mirrorDir = null)
    {
        if (string.IsNullOrEmpty(dir) || !Exists(dir))
        {
            return;
        }

        try
        {
            var args = new JsonObject { ["dir"] = dir, ["subject"] = subject };
            if (!string.IsNullOrEmpty(mirrorDir))
            {
                args["mirror_dir"] = mirrorDir;
            }

            Call(PoolContext(dir), "activity", "clear", args, hook);
        }
        catch (Exception)
        {
            // an activity record is best-effort
        }
    }

    public int SweepActivityRouted(string dir, double cutoffMs, bool hook = false, Action<string>? pruned = null)
    {
        if (string.IsNullOrEmpty(dir) || !Exists(dir))
        {
            return 0;
        }

        try
        {
            var result = Call(PoolContext(dir), "activity", "sweep", new JsonObject { ["dir"] = dir, ["cutoff_ms"] = cutoffMs }, hook);
            var parsed = JsonText.Parse(result.ResultJson.Span);
            if (pruned is not null && parsed.Member("pruned_failed") is { IsArray: true } lines)
            {
                foreach (var line in lines.AsArray().Where(static l => l.IsText))
                {
                    pruned(line.AsString());
                }
            }

            return parsed.Member("removed") is { IsNumber: true } r ? (int)r.AsNumber() : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public void AppendSessionPid(string dir, JsonValue? sessionId, JsonValue? pid)
    {
        try
        {
            if (!SessionPidDue(dir, sessionId, pid, NewestPidOf))
            {
                if (sessionId is { IsText: true } sid && sid.AsString().Length > 0 && pid is { IsNumber: true })
                {
                    NoteKnownDirIfAbsent(dir);
                }

                return;
            }

            var result = Call(
                PoolContext(dir),
                "session-pids",
                "append",
                new JsonObject { ["dir"] = dir, ["session_id"] = sessionId!.Value, ["pid"] = pid!.Value },
                hook: true);
            if (JsonText.Parse(result.ResultJson.Span).Member("written") is { IsBool: true } written && written.AsBool())
            {
                NoteKnownDir(dir!);
            }
            else
            {
                NoteKnownDirIfAbsent(dir);
            }
        }
        catch (Exception)
        {
            // a session record never fails the operation that triggered it
        }
    }

    public void AppendGateRouted(string dir, JsonObject record) =>
        Call(PoolContext(dir), "gates", "append", new JsonObject { ["dir"] = dir, ["rec"] = record }, hook: true);

    private void AppendRosterRecordRouted(string dir, JsonObject record, bool hook = false) =>
        Call(PoolContext(dir), "peers", "append", new JsonObject { ["dir"] = dir, ["rec"] = record }, hook);
}
