using Ah.Core.Json;

namespace Ah.Mesh;

/// <summary>
/// Where one mail entry stands, read from the ladder rows and the ack, supersede and decline records through the readers
/// <see cref="RelayVoid"/> uses. Nothing is written. The send gate and <c>ah msg status</c> both take their answer from here.
/// </summary>
internal static class MsgStatus
{
    /// <summary>An entry and the box that holds it.</summary>
    internal sealed record Located(string Dir, string Address, JsonObject Entry);

    /// <summary>The box that holds entry <paramref name="id"/>, or null when no box does.</summary>
    internal static Located? Locate(IMailbox mailbox, string id)
    {
        foreach (var box in mailbox.ListBoxes())
        {
            foreach (var e in mailbox.ReadEntries(box.Dir).Entries)
            {
                if (e.IsMap && Text(e.AsObject(), "id") == id)
                {
                    return new Located(box.Dir, box.Address, e.AsObject());
                }
            }
        }

        return null;
    }

    /// <summary>Whether the caller's box addresses include the entry's recipient box or its sender, or the entry belongs to a team a caller orchestrates.</summary>
    internal static bool InScope(IMailbox mailbox, Located at, IReadOnlyCollection<string> addresses)
    {
        if (addresses.Contains(at.Address) || addresses.Contains(Text(at.Entry, "from")))
        {
            return true;
        }

        var team = mailbox.SplitAddress(at.Address)?.Team;
        return team is not null
            && addresses.Any(a => a.StartsWith("orchestrator@", StringComparison.Ordinal) && mailbox.SplitAddress(a)?.Team == team);
    }

    /// <summary>
    /// The status of the located entry: <c>state</c> is the first that holds of withdrawn, declined, superseded, acked, delivered, pending
    /// (the ladder has started) and routed (it has not); <c>relay_needed</c> is true while a relay was asked for and
    /// <see cref="RelayVoid.Check"/> finds nothing that voids it.
    /// </summary>
    internal static JsonObject Of(IMailbox mailbox, Located at)
    {
        var id = Text(at.Entry, "id");
        if (!mailbox.RecordsReadable(at.Dir, id))
        {
            throw new IOException($"the records of entry {id} cannot be read");
        }

        var rows = mailbox.LadderRowsOf(at.Dir, id);
        var marker = RelayVoid.Check(mailbox, at.Dir, id);
        var kind = marker is null ? null : RelayVoid.Kind(marker);
        var ack = mailbox.ReadAck(at.Dir, id) is { IsMap: true } a ? a.AsObject() : null;
        var claim = mailbox.ReadClaim(at.Dir, id);
        var claimState = claim is null ? string.Empty : Text(claim, "state");
        var state = DrainedState.Is(rows) ? DrainedState.State : kind switch
        {
            "withdrawn" => "withdrawn",
            "declined" => "declined",
            "superseded" => "superseded",
            "acked" => "acked",
            "delivered" => "delivered",
            "claimed" when claimState is ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed or ClaimStates.FallbackClaimed or ClaimStates.Delivered => "delivered",
            _ => rows.Count == 0 && claimState is "" or ClaimStates.Pending ? "routed" : "pending",
        };
        var asked = rows.Any(static r => r.Get("rung") is { IsNumber: true } n && n.AsNumber() == 3 && r.Get("state") is { IsText: true } s && s.AsString() == "pending")
            || (claim is not null && Text(claim, "carrier") == "relay");
        var needed = marker is null && asked;
        var to = Text(at.Entry, "to");
        var deliveredRow = rows.LastOrDefault(static r => r.Get("state") is { IsText: true } s && s.AsString() == "delivered");
        var deliveredAt = new[]
        {
            deliveredRow is null ? string.Empty : Text(deliveredRow, "at"),
            ack is null ? string.Empty : Text(ack, "at"),
            claim is null ? string.Empty : Text(claim, "at"),
        }.FirstOrDefault(static t => t.Length > 0) ?? string.Empty;
        var delivered = state is "acked" or "delivered"
            ? new JsonObject { ["by"] = to.Length > 0 ? to : at.Address, ["at"] = deliveredAt, ["via"] = ViaOf(at.Entry, claim, ack) }
            : null;
        var ended = rows.Count > 0 ? rows[^1] : null;
        return new JsonObject
        {
            ["entry"] = id,
            ["kind"] = Text(at.Entry, "kind"),
            ["from"] = Text(at.Entry, "from"),
            ["to"] = to.Length > 0 ? to : at.Address,
            ["state"] = state,
            ["delivered"] = delivered is null ? JsonValue.Null : delivered,
            ["acked"] = ack is null ? JsonValue.Null : new JsonObject { ["by"] = Text(ack, "by"), ["at"] = Text(ack, "at") },
            ["superseded_by"] = state == "superseded" && ended is not null ? Text(ended, "by") : JsonValue.Null,
            ["declined"] = state == "declined" && ended is not null
                ? new JsonObject { ["by"] = Text(ended, "by"), ["at"] = Text(ended, "at"), ["reason"] = Text(ended, "reason") }
                : JsonValue.Null,
            ["withdrawn"] = state == "withdrawn" && ended is not null
                ? new JsonObject { ["by"] = Text(ended, "by"), ["at"] = Text(ended, "at"), ["reason"] = Text(ended, "reason"), ["readdressed_to"] = ended.Get("readdressed_to") is { IsText: true } to2 ? to2 : JsonValue.Null, ["readdress_delivered"] = ended.Get("readdressed_to") is { IsText: true } to3 ? Locate(mailbox, to3.AsString()) is not null : JsonValue.Null }
                : JsonValue.Null,
            ["claim"] = claimState.Length > 0 ? claimState : JsonValue.Null,
            ["relay_needed"] = needed,
            ["relay_reason"] = !needed && marker is not null ? Reason(marker) : JsonValue.Null,
        };
    }

    /// <summary>How the entry reached its recipient: the way the ack came when it was acked, else its claim's carrier or its route.</summary>
    private static string ViaOf(JsonObject entry, JsonObject? claim, JsonObject? ack)
    {
        if (ack is not null)
        {
            return Text(ack, "by") switch
            {
                "sendmessage" => "relay",
                "cli" => "ack",
                var other => other,
            };
        }

        return claim is not null && Text(claim, "carrier") == "relay" ? "relay" : Text(entry, "route") == "pull" || (claim is not null && Text(claim, "carrier") == "prompt") ? "pull" : "fallback";
    }

    /// <summary>Why no relay is needed, as a short phrase.</summary>
    internal static string Reason(JsonObject marker) => RelayVoid.Kind(marker) switch
    {
        "acked" => $"acked by {RelayVoid.Field(marker, "by")} at {RelayVoid.Field(marker, "at")}",
        "superseded" => $"superseded by {RelayVoid.Field(marker, "by")}",
        "withdrawn" => $"withdrawn by {RelayVoid.Field(marker, "by")} ({RelayVoid.Field(marker, "reason")})",
        "declined" => $"declined by {RelayVoid.Field(marker, "by")} ({RelayVoid.Field(marker, "reason")})",
        "claimed" => $"claimed {RelayVoid.Field(marker, "state")}",
        "released" => "released to the delivery ladder",
        _ => "delivered",
    };

    /// <summary>
    /// The reason a SendMessage that carries entry <paramref name="status"/>'s marker is refused, or null when a relay of it is owed
    /// and the send may go on to the relay claim.
    /// </summary>
    internal static string? Denial(JsonObject status)
    {
        if (status.Get("relay_needed") is { IsBool: true } needed && needed.AsBool())
        {
            return null;
        }

        if (Text(status, "claim") is ClaimStates.RelayClaimed or ClaimStates.RelayFailed)
        {
            return null;
        }

        var id = Text(status, "entry");
        var delivered = status.Get("delivered") is { IsMap: true } d ? d.AsObject() : null;
        var acked = status.Get("acked") is { IsMap: true } k ? k.AsObject() : null;
        var check = $"Check with ah msg status {id}.";
        return Text(status, "state") switch
        {
            "acked" => $"ah: entry {id} was already read (acked) by {Text(acked!, "by")}{At(acked!)}; not resent. {check}",
            "delivered" => $"ah: entry {id} was already delivered to {Text(delivered!, "by")}{At(delivered!)} via {Text(delivered!, "via")}; not resent. {check}",
            "superseded" => $"ah: entry {id} was superseded by {Text(status, "superseded_by")}; not resent. {check}",
            "withdrawn" => $"ah: entry {id} was withdrawn by {Text(status.Get("withdrawn")!.Value.AsObject(), "by")}; not resent. {check}",
            "declined" => $"ah: entry {id} was declined ({Text(status.Get("declined")!.Value.AsObject(), "reason")}); not resent. {check}",
            DrainedState.State => $"ah: entry {id} was drained (its team is held); not resent. {check}",
            _ when Text(status, "claim") == ClaimStates.Released => MailboxHooks.ReleasedText(id),
            _ => $"ah: entry {id} is queued for pull delivery; the plugin prompt carries it. Do not SendMessage it.",
        };
    }

    /// <summary>The refusal for an entry the sender has no part in: it names no recipient, time or route.</summary>
    internal static string Generic(string id) => $"ah: entry {id} not resent; check ah msg status {id}.";

    /// <summary>" at &lt;time&gt;" when the record has a time, else nothing.</summary>
    internal static string At(JsonObject o) => Text(o, "at") is { Length: > 0 } t ? $" at {t}" : string.Empty;

    private static string Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : string.Empty;
}
