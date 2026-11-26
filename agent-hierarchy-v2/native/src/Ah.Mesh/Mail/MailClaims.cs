using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>The states of an entry's claim record. <c>delivered</c> is terminal; only a withdrawn claim (a gate denied the send) returns to <c>pending</c>.</summary>
public sealed record ClaimStates
{
    public const string Pending = "pending";
    public const string RelayClaimed = "relay-claimed";
    public const string RelaySent = "relay-sent";
    public const string RelayUnconfirmed = "relay-unconfirmed";
    public const string RelayFailed = "relay-failed";
    public const string Released = "released";
    public const string FallbackClaimed = "fallback-claimed";
    public const string Delivered = "delivered";
}

/// <summary>The claim operations a caller names.</summary>
public sealed record ClaimActions
{
    /// <summary>The sender's SendMessage PreToolUse: <c>pending → relay-claimed</c>.</summary>
    public const string Take = "take";

    /// <summary>The sender's PostToolUse on success: <c>relay-claimed → relay-sent</c>.</summary>
    public const string Confirm = "confirm";

    /// <summary>The sender's PostToolUse on an error: <c>relay-claimed → relay-failed</c>.</summary>
    public const string Fail = "fail";

    /// <summary>
    /// A gate denied the SendMessage that took the claim, so nothing was sent: <c>relay-claimed → pending</c>, for the holder's own
    /// call only. The retry then takes the claim again.
    /// </summary>
    public const string Withdraw = "withdraw";

    /// <summary>The daemon, the receiver's hooks or a person: <c>relay-claimed | relay-sent → relay-failed</c>.</summary>
    public const string Release = "release";

    /// <summary>
    /// The daemon: applies every transition the leases and a failed relay make due. With <c>idle</c> in its arguments (the due
    /// computation of a receiver), a <c>pending</c> relay whose lease has run out is claimed only when the receiver is idle and a
    /// full due cycle after the one that first found it so.
    /// </summary>
    public const string Advance = "advance";

    /// <summary>The receiver's hooks: <c>relay-sent | fallback-claimed → delivered</c>.</summary>
    public const string Deliver = "deliver";
}

/// <summary>
/// What a claim operation did: <paramref name="Won"/> is true when the caller holds the claim it asked for (a repeat by the same
/// holder wins again), <paramref name="State"/> is the state the record is in now (<c>none</c> when there is no record), and
/// <paramref name="Reason"/> says why a call that did not win lost.
/// </summary>
public sealed record ClaimResult(bool Won, string State, string? Reason, JsonObject? Record);

/// <summary>The settings a new entry's claim record copies: whether single-carrier delivery is on, and the two leases.</summary>
public sealed record CarrierSettings(bool SingleCarrier, long RelayLeaseMs, long ClaimLeaseMs)
{
    /// <summary>How long after the claim lease an unconfirmed claim waits for the sender's Stop hook before it is called unconfirmed.</summary>
    public const long UnconfirmedGraceMs = 60_000;

    public const long DefaultRelayLeaseMs = 180_000;
    public const long DefaultClaimLeaseMs = 60_000;

    public static CarrierSettings Default { get; } = new(true, DefaultRelayLeaseMs, DefaultClaimLeaseMs);

    /// <summary>
    /// The settings in <c>agent-hierarchy.json</c> files, later files overriding earlier ones: <c>single_carrier</c> (bool),
    /// <c>relay_lease_ms</c> and <c>claim_lease_ms</c> (non-negative whole numbers). A value of another type is ignored.
    /// </summary>
    internal static CarrierSettings Read(IFileReader files, IEnumerable<string> files_)
    {
        var single = Default.SingleCarrier;
        var relay = Default.RelayLeaseMs;
        var claim = Default.ClaimLeaseMs;
        foreach (var path in files_)
        {
            JsonObject? data;
            try
            {
                data = JsonText.Parse(files.ReadText(path)) is { IsMap: true } v ? v.AsObject() : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
            {
                continue;
            }

            if (data is null)
            {
                continue;
            }

            if (data.Get("single_carrier") is { IsBool: true } flag)
            {
                single = flag.AsBool();
            }

            relay = Lease(data.Get("relay_lease_ms"), relay);
            claim = Lease(data.Get("claim_lease_ms"), claim);
        }

        return new CarrierSettings(single, relay, claim);
    }

    private static long Lease(JsonValue? value, long fallback) =>
        value is { IsNumber: true } n && double.IsInteger(n.AsNumber()) && n.AsNumber() >= 0 && n.AsNumber() <= int.MaxValue ? (long)n.AsNumber() : fallback;
}

/// <summary>
/// The claim state machine of one entry: the only legal transitions are the arrows of the design, and a call that asks for another
/// loses and is told the state it found. Pure: the caller reads the record, applies one action, and writes what changed.
/// </summary>
internal static class ClaimMachine
{
    /// <summary>The reason that lets a release take a <c>fallback-claimed</c> claim; no other release does.</summary>
    internal const string UnreadAtIdleReason = "unread-at-idle";

    /// <summary>The release reason for a plugin-prompt claim that no session took within the pull timeout.</summary>
    internal const string NoPollerReason = "no-poller";

    internal sealed record Step(ClaimResult Result, JsonObject? Record, IReadOnlyList<JsonObject> Rows);

    /// <summary>A new claim record in <c>pending</c>.</summary>
    internal static JsonObject Create(
        string id,
        string carrier,
        string from,
        string to,
        string? senderSession,
        string? receiverSession,
        CarrierSettings settings,
        string createdIso,
        long? senderPid = null,
        string? senderPidStart = null)
    {
        var record = new JsonObject
        {
            ["v"] = 1,
            ["id"] = id,
            ["state"] = ClaimStates.Pending,
            ["carrier"] = carrier,
            ["from"] = from,
            ["to"] = to,
            ["sender_session"] = senderSession is null ? JsonValue.Null : senderSession,
            ["receiver_session"] = receiverSession is null ? JsonValue.Null : receiverSession,
            ["sender_pid"] = senderPid is null ? JsonValue.Null : (double)senderPid.Value,
            ["sender_pid_start"] = senderPidStart is null ? JsonValue.Null : senderPidStart,
            ["l0_ms"] = (double)settings.RelayLeaseMs,
            ["l1_ms"] = (double)settings.ClaimLeaseMs,
            ["created"] = createdIso,
            ["at"] = createdIso,
        };
        return record;
    }

    /// <summary>The claim a delivery decision looks at: a released claim is no claim, so the entry is delivered as an unclaimed one.</summary>
    internal static JsonObject? Live(JsonObject? record) => record is not null && StateOf(record) == ClaimStates.Released ? null : record;

    internal static string StateOf(JsonObject? record) =>
        record?.Get("state") is { IsText: true } s ? s.AsString() : "none";

    internal static Step Apply(JsonObject? record, string action, JsonObject args, DateTimeOffset now, bool acked)
    {
        if (record is null)
        {
            return Lost("none", "no-claim", null);
        }

        var state = StateOf(record);
        var session = args.Get("session_id") is { IsText: true } sid && sid.AsString().Length > 0 ? sid.AsString() : null;
        var next = record.Clone();
        var rows = new List<JsonObject>();
        string? holderSession = record.Get("holder") is { IsMap: true } h && h.AsObject().Get("session_id") is { IsText: true } hs ? hs.AsString() : null;

        void Move(string to, string actor, string? reason)
        {
            var row = new JsonObject { ["claim"] = true, ["from_state"] = StateOf(next), ["state"] = "claim-" + to, ["actor"] = actor };
            if (reason is not null)
            {
                row["reason"] = reason;
                next["reason"] = reason;
            }

            next["state"] = to;
            next["at"] = Timestamps.ToIsoString(now);
            rows.Add(row);
        }

        var changed = false;

        Step Done(bool won, string? reason = null) =>
            new(new ClaimResult(won, StateOf(next), won ? null : reason, next), rows.Count > 0 || changed ? next : null, rows);

        switch (action)
        {
            case ClaimActions.Take:
            {
                if (state == ClaimStates.Pending)
                {
                    if (Carrier(record) != "relay")
                    {
                        return Done(false, "carrier-prompt");
                    }

                    if (session is null || record.Get("sender_session") is not { IsText: true } owner || owner.AsString() != session)
                    {
                        return Done(false, "foreign");
                    }

                    if (ToolUseOf(args) is { } takingCall && record.Get("denied_call") is { IsText: true } deniedCall && deniedCall.AsString() == takingCall)
                    {
                        return Done(false, "denied-call");
                    }

                    var held = args.Get("holder") is { IsMap: true } given ? given.AsObject().Clone() : new JsonObject();
                    held["session_id"] = session;
                    if (ToolUseOf(args) is { } callId)
                    {
                        held["tool_use_id"] = callId;
                    }

                    next["holder"] = held;

                    Move(ClaimStates.RelayClaimed, session, null);
                    return Done(true);
                }

                if (state == ClaimStates.RelayClaimed && session is not null && holderSession == session)
                {
                    var same = ToolUseOf(args) is { } incoming && record.Get("holder") is { IsMap: true } hold
                        && hold.AsObject().Get("tool_use_id") is { IsText: true } recorded && recorded.AsString() == incoming;
                    return same ? Done(true) : Done(false, "held-by-self");
                }

                if (state == ClaimStates.RelayClaimed)
                {
                    return Done(false, "held-by-other");
                }

                return Done(false, state);
            }

            case ClaimActions.Confirm:
            {
                if (session is null || holderSession != session)
                {
                    return Done(false, "not-holder");
                }

                if (state is ClaimStates.RelayClaimed or ClaimStates.RelayUnconfirmed)
                {
                    if (args.Get("msg_id") is { IsText: true } msgId && msgId.AsString().Length > 0)
                    {
                        next["msg_id"] = msgId.AsString();
                    }

                    Move(ClaimStates.RelaySent, session, Reason(args));
                    if (acked)
                    {
                        Move(ClaimStates.Delivered, "receiver", null);
                    }

                    return Done(true);
                }

                return Done(state is ClaimStates.RelaySent or ClaimStates.Delivered, state);
            }

            case ClaimActions.Fail:
            {
                if (session is null || holderSession != session || state is not (ClaimStates.RelayClaimed or ClaimStates.RelayUnconfirmed))
                {
                    return Done(false, state == ClaimStates.RelayFailed ? null : "not-claimed");
                }

                Move(ClaimStates.RelayFailed, session, Reason(args));
                return Done(true);
            }

            case ClaimActions.Withdraw:
            {
                if (ToolUseOf(args) is not { } call || session is null || record.Get("sender_session") is not { IsText: true } sender || sender.AsString() != session)
                {
                    return Done(false, "not-claimed");
                }

                if (state == ClaimStates.Pending)
                {
                    // The denial came before this call's claim: the claim is never to be taken for that call.
                    next["denied_call"] = call;
                    changed = true;
                    return Done(true);
                }

                var held = record.Get("holder") is { IsMap: true } hold && hold.AsObject().Get("tool_use_id") is { IsText: true } recorded ? recorded.AsString() : null;
                if (state != ClaimStates.RelayClaimed || holderSession != session || call != held)
                {
                    return Done(false, state == ClaimStates.RelayClaimed ? "other-call" : "not-claimed");
                }

                next.Remove("holder");
                next["denied_call"] = call;
                Move(ClaimStates.Pending, session, Reason(args));
                return Done(true);
            }

            case ClaimActions.Release:
            {
                var unread = Reason(args) == UnreadAtIdleReason;
                var noPoller = Reason(args) == NoPollerReason && Carrier(record) != "relay" && state is ClaimStates.Pending or ClaimStates.FallbackClaimed;
                if (state is not (ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed) && !(unread && state == ClaimStates.FallbackClaimed) && !noPoller)
                {
                    return Done(false, state);
                }

                if (acked)
                {
                    return Done(false, "acked");
                }

                Move(noPoller ? ClaimStates.Released : ClaimStates.RelayFailed, args.Get("actor") is { IsText: true } actor ? actor.AsString() : "daemon", Reason(args));
                return Done(true);
            }

            case ClaimActions.Deliver:
            {
                var fromPending = state == ClaimStates.Pending && Carrier(record) == "relay";
                if (fromPending || state is ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed or ClaimStates.FallbackClaimed)
                {
                    Move(ClaimStates.Delivered, args.Get("actor") is { IsText: true } by ? by.AsString() : "receiver", null);
                    return Done(true);
                }

                return Done(state == ClaimStates.Delivered, state);
            }

            case ClaimActions.Advance:
            {
                if (acked)
                {
                    if (state is ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed or ClaimStates.FallbackClaimed)
                    {
                        Move(ClaimStates.Delivered, "receiver", null);
                    }

                    return Done(true);
                }

                for (var i = 0; i < 3; i++)
                {
                    var current = StateOf(next);
                    if (current == ClaimStates.Pending && Carrier(next) != "relay")
                    {
                        Move(ClaimStates.FallbackClaimed, "daemon", "carrier-prompt");
                    }
                    else if (current == ClaimStates.Pending && Elapsed(next, "created", "l0_ms", now))
                    {
                        if (args.Get("idle") is { IsBool: true } idle)
                        {
                            var waited = next.Get("poll_wait") is { IsNumber: true } w && w.AsNumber() >= 1;
                            if (!idle.AsBool())
                            {
                                if (next.Remove("poll_wait"))
                                {
                                    changed = true;
                                }

                                break;
                            }

                            if (!waited)
                            {
                                next["poll_wait"] = 1d;
                                changed = true;
                                break;
                            }
                        }

                        Move(ClaimStates.FallbackClaimed, "daemon", "relay-not-started");
                    }
                    else if (current == ClaimStates.RelayClaimed && Elapsed(next, "at", "l1_ms", now, CarrierSettings.UnconfirmedGraceMs))
                    {
                        Move(ClaimStates.RelayUnconfirmed, "daemon", "no-evidence");
                    }
                    else if (current == ClaimStates.RelayFailed)
                    {
                        Move(ClaimStates.FallbackClaimed, "daemon", "relay-failed");
                    }
                    else
                    {
                        break;
                    }
                }

                return Done(true);
            }

            default:
                return Done(false, "unknown-action");
        }

        static string Carrier(JsonObject o) => o.Get("carrier") is { IsText: true } c ? c.AsString() : "prompt";

        static string? ToolUseOf(JsonObject a) => a.Get("tool_use_id") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;

        static string? Reason(JsonObject a) => a.Get("reason") is { IsText: true } r && r.AsString().Length > 0 ? r.AsString() : null;

        static Step Lost(string state, string reason, JsonObject? record) =>
            new(new ClaimResult(false, state, reason, record), null, []);
    }

    /// <summary>Whether the relay lease of a claim record has run out at <paramref name="at"/>: past it the daemon delivers a pending relay itself.</summary>
    internal static bool RelayLeaseOver(JsonObject record, DateTimeOffset at) => Elapsed(record, "created", "l0_ms", at);

    private static bool Elapsed(JsonObject o, string from, string lease, DateTimeOffset at, long extraMs = 0)
    {
        if (o.Get(from) is not { IsText: true } start || !Timestamps.TryParseMs(start.AsString(), out var startMs)
            || o.Get(lease) is not { IsNumber: true } ms)
        {
            return false;
        }

        return at.ToUnixTimeMilliseconds() >= startMs + ms.AsNumber() + extraMs;
    }

    /// <summary>The wire form of a result, as the <c>claim</c> op returns it: small, since a write op's result is capped.</summary>
    internal static JsonObject ToJson(ClaimResult result)
    {
        var json = new JsonObject { ["won"] = result.Won, ["state"] = result.State };
        if (result.Reason is not null)
        {
            json["reason"] = result.Reason;
        }

        return json;
    }

    internal static ClaimResult FromJson(JsonValue value) =>
        new(
            value.Member("won") is { IsBool: true } won && won.AsBool(),
            value.Member("state") is { IsText: true } state ? state.AsString() : "none",
            value.Member("reason") is { IsText: true } reason ? reason.AsString() : null,
            value.Member("record") is { IsMap: true } record ? record.AsObject() : null);
}
