using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Mesh;

/// <summary>
/// One question of the Claude mod's mail poll: which entries are due for <paramref name="Session"/> now. <paramref name="Handed"/> is
/// the mod's claim map (<c>pending</c> or <c>resolved</c> and when); <paramref name="State"/> is the previous answer's cursor, or
/// null at the first request.
/// </summary>
public sealed record DueRequest(
    string Session,
    double Now,
    double? BusyAt,
    double PollMs,
    IReadOnlyDictionary<string, HandedState> Handed,
    string? State)
{
    /// <summary>The build of the mod this daemon expects; <c>MOD_BUILD</c> in <c>mod/register.tsx</c> says the same.</summary>
    public const int CurrentModBuild = 1;

    /// <summary>The build the asking mod sent; 0 from a mod older than the field.</summary>
    public int ModBuild { get; init; }

    /// <summary>Who is asking: the mail poll by default, or the mod between tool calls of a busy turn (<see cref="DoorbellCarrier.Tool"/>), which only a doorbell ring answers.</summary>
    public DoorbellCarrier Carrier { get; init; } = DoorbellCarrier.Prompt;

    /// <summary>The asking session's hierarchy directory, when it sent one: where an escalation finds the team records of its lead.</summary>
    public string? HierarchyDir { get; init; }

    /// <summary>The engine refused the mod's last ring because the person stopped the session and has sent nothing since.</summary>
    public bool Held { get; init; }
}

/// <summary>
/// The answer: the due entry ids in prompt order and the prompt that hands them over, whether the session counts as idle, the
/// cursor to send back, the handed ids positively seen acked, the earliest time an entry becomes due with no new input
/// (<paramref name="NextDeadlineMs"/>), the box directories the answer was computed from, and the short form of the prompt
/// (<paramref name="Compact"/>, null when there is no prompt or the short form is the prompt itself).
/// </summary>
public sealed record DueAnswer(
    IReadOnlyList<string> Ids,
    string? Prompt,
    bool Idle,
    string State,
    IReadOnlyList<string> Acked,
    double? NextDeadlineMs,
    IReadOnlyList<string> BoxDirs,
    string? Compact = null,
    DoorbellEscalation? Escalation = null)
{
    /// <summary>Whether the prompt is a doorbell ring, which this answer already claimed: asking again before handing it over would find nothing due.</summary>
    public bool Ring { get; init; }

    /// <summary>While the session is held: how many items wait unread, a count only (ah2-180 D2); null otherwise.</summary>
    public int? Unread { get; init; }
}

/// <summary>The unacked entries a box holds from a cursor, and the cursor after them.</summary>
public sealed record DueBoxRead(IReadOnlyList<JsonValue> Entries, long Next);

/// <summary>Where a box's entries come from when they are not read from its files: the daemon's own answer.</summary>
public interface IDueBoxSource
{
    /// <summary>The box's unacked entries from byte <paramref name="since"/>, or null to read the files.</summary>
    DueBoxRead? Read(string address, string dir, long since);
}

/// <summary>The decision the Claude mod's mail poll makes, over the mailbox and the registration of one session.</summary>
public interface IMailDue
{
    /// <summary>
    /// Reads the session's registration and boxes (each through <paramref name="source"/>, else the files with the adapter
    /// heartbeat written) and returns what is due. A box that cannot be read contributes nothing and changes no other box.
    /// </summary>
    DueAnswer Compute(DueRequest request, IDueBoxSource? source = null);

    /// <summary>Where the session's registration file is, whether or not it exists.</summary>
    string RegistrationFile(string session);

    /// <summary>
    /// The request in <paramref name="input"/> (<c>session</c>, <c>now</c>, <c>busy_at</c>, <c>poll_ms</c>, <c>handed</c>, <c>state</c>),
    /// or null with <paramref name="problem"/> saying what is wrong with it.
    /// </summary>
    DueRequest? ParseRequest(JsonValue input, out string? problem);

    /// <summary>
    /// The answer as the one JSON object both transports return: <c>v</c>, <c>ids</c>, <c>prompt</c>, <c>compact</c>, <c>idle</c>, <c>state</c>,
    /// <c>acked</c> and <c>daemon</c> (<c>{socket}</c> when <paramref name="daemonSocket"/> is given, else null), plus <c>ring</c> and
    /// <c>escalation</c> (<c>{kind, address, text}</c>) only when the answer has them.
    /// </summary>
    JsonObject AnswerJson(DueAnswer answer, string? daemonSocket);
}

/// <summary>The time the daemon's held <c>/v1/due</c> requests are timed by: a millisecond counter and a delay, so a test can drive them.</summary>
public interface IDueTimer
{
    /// <summary>A monotonic millisecond counter.</summary>
    long NowMs { get; }

    /// <summary>Completes after <paramref name="milliseconds"/>, or throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    Task DelayAsync(int milliseconds, CancellationToken cancellationToken);
}

internal sealed class MailDue(IMailbox mailbox, IMailCore core, IFileReader files, IAhEnvironment environment, IMailboxHooks? hooks = null, IMailDoorbell? doorbell = null) : IMailDue
{
    /// <summary>The entries of <paramref name="box"/> that still apply: an event that no longer does is acked <c>resolved</c> and left out.</summary>
    private IReadOnlyList<JsonValue> StillApplying(BoxRef box, DueRequest request, IReadOnlyList<JsonValue> entries)
    {
        if (hooks is null)
        {
            return entries;
        }

        var kept = new List<JsonValue>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.IsMap && entry.AsObject().Get("event") is { IsText: true } && entry.AsObject().Get("id") is { IsText: true } id && !hooks.EventApplies(request.Session, entry))
            {
                hooks.ResolveEvent(box.Dir, id.AsString(), "adapter", request.Session);
                continue;
            }

            kept.Add(entry.IsMap && entry.AsObject().Get("event") is { IsText: true } ? hooks.EventShown(request.Session, entry) : entry);
        }

        return kept;
    }

    private const long FileCap = 4L * 1024 * 1024;
    internal const string Adapter = "claude-mod";

    /// <summary>The first line of a tool call's context that carries member digest notes (b478).</summary>
    internal const string MidTurnPrefix = "[ah note, delivered mid-turn]";

    public string RegistrationFile(string session) => mailbox.RegistrationFile(session);

    public DueRequest? ParseRequest(JsonValue input, out string? problem)
    {
        problem = null;
        if (!input.IsMap)
        {
            problem = "in must be a JSON object";
            return null;
        }

        var o = input.AsObject();
        if (o.Get("session") is not { IsText: true } session || session.AsString().Length == 0)
        {
            problem = "session must be a non-empty string";
            return null;
        }

        if (o.Get("now") is not { IsNumber: true } now || !double.IsFinite(now.AsNumber()))
        {
            problem = "now must be a number";
            return null;
        }

        double? busyAt = null;
        if (o.Get("busy_at") is { IsNull: false } busy)
        {
            if (!busy.IsNumber || !double.IsFinite(busy.AsNumber()))
            {
                problem = "busy_at must be a number or null";
                return null;
            }

            busyAt = busy.AsNumber();
        }

        var pollMs = IMailCore.DefaultPollMs;
        if (o.Get("poll_ms") is { IsNull: false } poll)
        {
            if (!poll.IsNumber || !double.IsFinite(poll.AsNumber()) || poll.AsNumber() <= 0)
            {
                problem = "poll_ms must be a positive number";
                return null;
            }

            pollMs = poll.AsNumber();
        }

        var handed = new Dictionary<string, HandedState>(StringComparer.Ordinal);
        if (o.Get("handed") is { IsNull: false } map)
        {
            if (!map.IsMap)
            {
                problem = "handed must be an object";
                return null;
            }

            foreach (var id in map.AsObject().Keys)
            {
                if (map.AsObject()[id] is { IsArray: true } pair && pair.AsArray().Count == 2
                    && pair.AsArray()[0] is { IsText: true } state && state.AsString() is "pending" or "resolved"
                    && pair.AsArray()[1] is { IsNumber: true } at && double.IsFinite(at.AsNumber()))
                {
                    handed[id] = new HandedState(((JsonValue)pair.AsArray()[0]).AsString(), at.AsNumber());
                    continue;
                }

                problem = $"handed[{id}] must be [\"pending\"|\"resolved\", <ms>]";
                return null;
            }
        }

        string? prior = null;
        if (o.Get("state") is { IsNull: false } st)
        {
            if (!st.IsText)
            {
                problem = "state must be a string or null";
                return null;
            }

            prior = st.AsString();
        }

        var carrier = DoorbellCarrier.Prompt;
        if (o.Get("carrier") is { IsNull: false } named)
        {
            if (!named.IsText || named.AsString() != "tool")
            {
                problem = "carrier must be \"tool\" when given";
                return null;
            }

            carrier = DoorbellCarrier.Tool;
        }

        return new DueRequest(session.AsString(), now.AsNumber(), busyAt, pollMs, handed, prior) { ModBuild = o.Get("mod_build") is { IsNumber: true } build ? (int)build.AsNumber() : 0, Carrier = carrier,HierarchyDir = o.Get("hierarchy_dir") is { IsText: true } hier && hier.AsString().Length > 0 ? hier.AsString() : null, Held = o.Get("held") is { IsBool: true } held && held.AsBool() };
    }

    public JsonObject AnswerJson(DueAnswer answer, string? daemonSocket)
    {
        ArgumentNullException.ThrowIfNull(answer);
        var ids = new JsonArray();
        foreach (var id in answer.Ids)
        {
            ids.Add(id);
        }

        var acked = new JsonArray();
        foreach (var id in answer.Acked)
        {
            acked.Add(id);
        }

        var json = new JsonObject
        {
            ["v"] = 1d,
            ["ids"] = ids,
            ["prompt"] = answer.Prompt is null ? JsonValue.Null : answer.Prompt,
            ["compact"] = answer.Compact is null ? JsonValue.Null : answer.Compact,
            ["idle"] = answer.Idle,
            ["state"] = answer.State,
            ["acked"] = acked,
            ["daemon"] = daemonSocket is null ? JsonValue.Null : new JsonObject { ["socket"] = daemonSocket },
        };
        if (answer.Ring)
        {
            json["ring"] = true;
        }

        if (answer.Unread is { } unread)
        {
            json["unread"] = (double)unread;
        }

        if (answer.Escalation is { } escalation)
        {
            json["escalation"] = new JsonObject { ["kind"] = escalation.Kind, ["address"] = escalation.Address, ["text"] = escalation.Text };
        }

        return json;
    }

    public DueAnswer Compute(DueRequest request, IDueBoxSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var idle = request.BusyAt is null || request.Now - request.BusyAt.Value > IMailCore.BusyMaxMs;
        var cursors = ParseState(request.State);
        var handed = new Dictionary<string, HandedState>(request.Handed, StringComparer.Ordinal);
        var hb = new JsonObject { ["poll_ms"] = request.PollMs };
        var groups = new JsonArray();
        var ids = new List<string>();
        var readable = new List<string>();
        var boxDirs = new List<string>();
        var nextState = new JsonObject();
        double? deadline = null;
        var ringsFor = doorbell?.Serves(request.Session) == true;
        var notes = new List<DigestEntry>();
        var pull = new PullOptions(SessionId: request.Session);

        foreach (var box in BoxesOf(request.Session))
        {
            boxDirs.Add(box.Dir);
            var cursor = cursors.GetValueOrDefault(box.Address);
            IReadOnlyList<JsonValue> list;
            try
            {
                (list, cursor) = ReadBox(box, request, cursor, source);
                readable.Add(box.Dir);
                list = AckTaken(box.Dir, list, request);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                list = [];
            }

            nextState[box.Address] = new JsonArray { (double)cursor.Since, (double)cursor.Through };
            if (request.Carrier == DoorbellCarrier.Tool)
            {
                // b478: a member's own digest notes reach a busy turn at its next tool call; every other kind keeps its carrier.
                notes.AddRange(list.Where(e => mailbox.IsMemberDigestNote(e, box.Address) && core.Pullable(e, pull)).Select(e => MailDoorbell.MemberDigestEntry(box.Dir, e)));
            }

            // A held session is answered with nothing: every prompt would be dropped until a prompt enters.
            if (request.Carrier == DoorbellCarrier.Tool || request.Held || (ringsFor && box.Address.StartsWith("orchestrator@", StringComparison.Ordinal)))
            {
                // the doorbell, not a body, tells this session about its orchestrator box; the read above kept the heartbeat fresh.
                // A tool-call poll is answered by the doorbell alone, never by a prompt for the entries of another box.
                continue;
            }

            list = [.. list.Where(e => !mailbox.IsMemberDigestNote(e, box.Address))];
            list = Claimed(box, list, request, idle, ref deadline);
            var due = core.DueEntries(list.Select(e => (JsonValue?)e), new DueOptions(request.Session, request.Now, hb, handed, idle));
            deadline = Earliest(deadline, GraceDeadline(list, due, request));
            if (due.Count == 0)
            {
                continue;
            }

            var entries = new JsonArray();
            foreach (var entry in due)
            {
                entries.Add(mailbox.MarkRelayVoid(entry));
                var id = entry.AsObject().Get("id")!.Value.AsString();
                ids.Add(id);
                handed[id] = new HandedState("pending", request.Now);
            }

            groups.Add(new JsonObject { ["address"] = box.Address, ["entries"] = entries });
        }

        foreach (var record in request.Handed.Values.Where(_ => request.Carrier != DoorbellCarrier.Tool))
        {
            if (record.State != "resolved")
            {
                continue;
            }

            var at = idle
                ? record.At + IMailCore.ResubmitMs
                : request.BusyAt!.Value + IMailCore.BusyMaxMs + 1;
            if (at > request.Now)
            {
                deadline = Earliest(deadline, at);
            }

        }

        var acked = request.Handed.Keys.Where(id => readable.Any(dir => mailbox.IsAcked(dir, id))).ToList();
        var prompt = ids.Count == 0 ? null : core.BuildPrompt(groups);
        var compact = ids.Count == 0 ? null : core.CompactPrompt(groups);
        // The notes shown mid-turn are acked as the digest acks them, so the next prompt's digest skips them: each is shown once.
        if (notes.Count > 0 && MailDoorbell.DigestText(
            [.. notes.OrderBy(static d => d.Created, StringComparer.Ordinal).ThenBy(static d => d.Id, StringComparer.Ordinal)], [], out var shown) is { } block)
        {
            foreach (var byBox in shown.GroupBy(static e => e.Dir))
            {
                _ = mailbox.AckIds(byBox.Key, [.. byBox.Select(static e => e.Id)], "digest", request.Session);
            }

            ids.AddRange(shown.Select(static e => e.Id));
            prompt = MidTurnPrefix + "\n" + block;
        }
        DoorbellEscalation? escalation = null;
        var rang = false;
        int? unread = null;
        if (ringsFor && ids.Count == 0)
        {
            var decision = doorbell!.Decide(new DoorbellRequest(request.Session, request.Now, request.BusyAt, request.Handed, request.Carrier) { HierarchyDir = request.HierarchyDir, Held = request.Held });
            acked.AddRange(decision.Acked.Where(id => !acked.Contains(id)));
            deadline = Earliest(deadline, decision.NextDeadlineMs);
            escalation = decision.Escalation;
            unread = decision.Unread;
            if (decision.Ring is { } ring)
            {
                ids.AddRange(ring.Inline ? ring.EntryIds : [ring.Id]);
                prompt = ring.Text;
                rang = true;
            }
        }

        return new DueAnswer(
            ids,
            prompt,
            idle,
            JsonText.Stringify(nextState, 0),
            acked,
            deadline,
            boxDirs,
            compact == prompt ? null : compact,
            escalation)
        { Ring = rang, Unread = unread };
    }

    /// <summary>
    /// b466: an entry the mod reports <c>resolved</c> was in a prompt the engine took, so it was read: it is acked here, whether or not
    /// a prompt hook saw that prompt, and left out of what is due. Unacked, the mod would hand it over again once the resubmit time passed.
    /// </summary>
    private IReadOnlyList<JsonValue> AckTaken(string dir, IReadOnlyList<JsonValue> list, DueRequest request)
    {
        var taken = list.Select(static e => MailDoorbell.Text(e, "id")).Where(id => request.Handed.TryGetValue(id, out var h) && h.State == "resolved").ToList();
        if (taken.Count == 0)
        {
            return list;
        }

        var acked = mailbox.AckIds(dir, taken, "adapter", request.Session).Acked;
        return [.. list.Where(e => !acked.Contains(MailDoorbell.Text(e, "id")))];
    }

    /// <summary>
    /// Applies the claim step to every entry of the box that has a claim record, and keeps from them only those the daemon holds
    /// the fallback claim of, which no timer may hold back. An entry in any other state is delivered, or being delivered, by the
    /// relay and is never due. Entries without a claim record are returned as they are.
    /// </summary>
    private List<JsonValue> Claimed(BoxRef box, IReadOnlyList<JsonValue> list, DueRequest request, bool idle, ref double? deadline)
    {
        var kept = new List<JsonValue>(list.Count);
        foreach (var e in list)
        {
            if (!e.IsMap || e.AsObject().Get("id") is not { IsText: true } id || ClaimMachine.Live(mailbox.ReadClaim(box.Dir, id.AsString())) is null)
            {
                kept.Add(e);
                continue;
            }

            var step = mailbox.AdvanceClaim(box.Dir, id.AsString(), idle);
            if (step.Record is { } record && ClaimDeadline(record) is { } at && at > request.Now)
            {
                deadline = Earliest(deadline, at);
            }

            if (step.Record is { } waiting && step.State == ClaimStates.Pending && waiting.Get("carrier") is { IsText: true } carrier && carrier.AsString() == "relay"
                && ClaimDeadline(waiting) is { } leaseEnd && leaseEnd <= request.Now)
            {
                // The lease has run out and the claim waits for the receiver to be idle, then for one more cycle.
                deadline = Earliest(deadline, idle ? request.Now + request.PollMs : request.BusyAt!.Value + IMailCore.BusyMaxMs + 1);
            }

            if (step.State == ClaimStates.FallbackClaimed)
            {
                var due = e.AsObject().Clone();
                due["route"] = "pull";
                kept.Add(due);
            }
        }

        return kept;
    }

    /// <summary>The moment the claim's own lease makes the daemon move it: a relay not started, or a relay claimed and never confirmed or found sent.</summary>
    internal static double? ClaimDeadline(JsonObject record)
    {
        var state = record.Get("state") is { IsText: true } s ? s.AsString() : string.Empty;
        var (from, lease) = state switch
        {
            ClaimStates.Pending when record.Get("carrier") is { IsText: true } c && c.AsString() == "relay" => ("created", "l0_ms"),
            ClaimStates.RelayClaimed => ("at", "l1_ms"),
            _ => (string.Empty, string.Empty),
        };
        if (from.Length == 0 || record.Get(from) is not { IsText: true } start || !Ah.Core.Time.Timestamps.TryParseMs(start.AsString(), out var startMs)
            || record.Get(lease) is not { IsNumber: true } ms)
        {
            return null;
        }

        return startMs + ms.AsNumber() + (state == ClaimStates.RelayClaimed ? CarrierSettings.UnconfirmedGraceMs : 0);
    }

    private IReadOnlyList<BoxRef> BoxesOf(string session)
    {
        if (mailbox.ReadRegistration(session) is not { } registration || registration.Get("session_id") is not { IsText: true } id || id.AsString() != session)
        {
            return [];
        }

        return core.RegistrationBoxList(JsonValue.FromString(environment.AhHome), JsonValue.FromObject(registration));
    }

    private (IReadOnlyList<JsonValue> Entries, Cursor Cursor) ReadBox(BoxRef box, DueRequest request, Cursor cursor, IDueBoxSource? source)
    {
        var path = NodePath.Join(box.Dir, "entries.jsonl");
        var stat = files.Stat(path, followSymlinks: false);
        var readable = stat is { Kind: FileKind.File } file && file.Size <= FileCap;
        if (source?.Read(box.Address, box.Dir, cursor.Since) is { } read)
        {
            // The source is asked even for a file this computation will not take, so the adapter heartbeat it writes stays fresh.
            if (stat is not null && !readable)
            {
                return ([], cursor);
            }

            var moves = read.Entries.All(e => e.Member("to_session") is { IsText: true } to && to.AsString() != request.Session);
            return (StillApplying(box, request, read.Entries), cursor with { Since = moves ? read.Next : cursor.Since });
        }

        IReadOnlyList<ScannedEntry> rows = [];
        if (mailbox.StoredRows(box.Dir, cursor.Through) is { } stored)
        {
            rows = stored;
        }
        else if (readable)
        {
            rows = core.ScanEntryText(Utf8Text.Decode(files.ReadBytes(path)), cursor.Through).Rows;
        }

        var isAcked = mailbox.AckedIn(box.Dir);
        var open = new List<JsonValue>();
        foreach (var row in rows)
        {
            if (!isAcked(row.Entry.AsObject().Get("id")!.Value.AsString()))
            {
                open.Add(row.Entry);
            }
        }

        var through = core.AckedThroughRows(rows, cursor.Through, isAcked);
        mailbox.WriteHeartbeat(box.Dir, Adapter, request.Session, request.PollMs);
        return (StillApplying(box, request, open), cursor with { Through = through });
    }

    /// <summary>The moment a fallback-routed entry that is not due yet outlives its grace.</summary>
    private double? GraceDeadline(IReadOnlyList<JsonValue> list, IReadOnlyList<JsonValue> due, DueRequest request)
    {
        double? earliest = null;
        var hb = new JsonObject { ["poll_ms"] = request.PollMs };
        var dueSet = new HashSet<JsonValue>(due, SameEntry.Instance);
        foreach (var entry in list)
        {
            if (dueSet.Contains(entry) || entry.Member("route") is not { IsText: true } route || route.AsString() != "fallback")
            {
                continue;
            }

            if (entry.Member("created") is { IsText: true } created
                && DateTimeOffset.TryParse(created.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            {
                earliest = Earliest(earliest, at.ToUnixTimeMilliseconds() + core.PullTimeoutMs(hb) + IMailCore.GraceMs);
            }
        }

        return earliest;
    }

    /// <summary>
    /// The equality <c>List&lt;JsonValue&gt;.Contains</c> applies, with a hash that agrees with it: a map or array is the same entry
    /// only as the same instance, and any other value is the same when its kind and content are.
    /// </summary>
    private sealed class SameEntry : IEqualityComparer<JsonValue>
    {
        internal static readonly SameEntry Instance = new();

        public bool Equals(JsonValue x, JsonValue y) => x.Kind == y.Kind && x.Kind switch
        {
            JsonKind.Map => ReferenceEquals(x.AsObject(), y.AsObject()),
            JsonKind.Array => ReferenceEquals(x.AsArray(), y.AsArray()),
            _ => x.Equals(y),
        };

        public int GetHashCode(JsonValue value) => value.Kind switch
        {
            JsonKind.Map => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.AsObject()),
            JsonKind.Array => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value.AsArray()),
            _ => (int)value.Kind,
        };
    }

    private static double? Earliest(double? a, double? b) => a is null ? b : b is null ? a : Math.Min(a.Value, b.Value);

    private readonly record struct Cursor(long Since, long Through);

    private static Dictionary<string, Cursor> ParseState(string? text)
    {
        var map = new Dictionary<string, Cursor>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text))
        {
            return map;
        }

        try
        {
            var value = JsonText.Parse(text);
            if (!value.IsMap)
            {
                return map;
            }

            foreach (var key in value.AsObject().Keys)
            {
                if (value.AsObject()[key] is { IsArray: true } pair && pair.AsArray().Count == 2
                    && pair.AsArray()[0] is { IsNumber: true } since && pair.AsArray()[1] is { IsNumber: true } through
                    && since.AsNumber() >= 0 && through.AsNumber() >= 0
                    && double.IsInteger(since.AsNumber()) && double.IsInteger(through.AsNumber()))
                {
                    map[key] = new Cursor((long)since.AsNumber(), (long)through.AsNumber());
                }
            }
        }
        catch (JsonParseException)
        {
            return [];
        }

        return map;
    }
}
