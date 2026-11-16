using Ah.Core.Json;

namespace Ah.Mesh;

internal sealed partial class Mailbox
{
    internal static readonly string[] DeclineReasons = ["already-reported", "superseded", "withdrawn"];

    private const int MaxDeclineNoteChars = 200;

    public MailboxOutcome<JsonObject> Decline(string dir, string id, string reason, string? note, string? sessionId, IReadOnlyList<string> callerAddresses)
    {
        try
        {
            return new MailboxOutcome<JsonObject>(DeclineCore(dir, id, reason, note, sessionId, callerAddresses), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<JsonObject>(null, e.ToFailure());
        }
    }

    /// <summary>
    /// Ends the relay of entry <paramref name="id"/> in the box <paramref name="dir"/> with a terminal <c>declined</c> ladder row. Only the
    /// carrier named on the entry's rung-3 row may: one of <paramref name="callerAddresses"/> is that address and, when the row names a
    /// carrier session, <paramref name="sessionId"/> (the session of the live registration that proved the caller; null when none did) is that
    /// session or the one session registered to the address now. It never acks the entry.
    /// </summary>
    private JsonObject DeclineCore(string dir, string id, string reason, string? note, string? sessionId, IReadOnlyList<string> callerAddresses)
    {
        if (!DeclineReasons.Contains(reason, StringComparer.Ordinal))
        {
            throw new MailboxError("decline_refused", $"--reason must be one of {string.Join(", ", DeclineReasons)}");
        }

        if (note is { Length: > MaxDeclineNoteChars })
        {
            throw new MailboxError("decline_refused", $"--note is {note.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)} characters; the limit is {MaxDeclineNoteChars.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rows = LadderRowsOf(dir, id);
            var request = rows.LastOrDefault(r => r.Get("rung") is { IsNumber: true } n && n.AsNumber() == 3 && r.Get("state") is { IsText: true } s && s.AsString() == "pending");
            if (request is null)
            {
                throw new MailboxError("decline_refused", $"entry {id} has no relay request to decline");
            }

            var last = rows[^1].Get("state") is { IsText: true } st ? st.AsString() : string.Empty;
            if (last is "delivered" or "superseded" or "declined")
            {
                throw new MailboxError("decline_refused", $"entry {id} is already {last}");
            }

            if (IsAcked(dir, id))
            {
                throw new MailboxError("decline_refused", $"entry {id} was already picked up");
            }

            var carrier = request.Get("carrier") is { IsText: true } c ? c.AsString() : string.Empty;
            if (carrier.Length == 0 || !callerAddresses.Contains(carrier, StringComparer.Ordinal))
            {
                throw new MailboxError("decline_refused", $"only the carrier of entry {id}{(carrier.Length > 0 ? $" ({carrier})" : string.Empty)} may decline its relay");
            }

            var carrierSession = request.Get("carrier_session") is { IsText: true } cs ? cs.AsString() : string.Empty;
            if (carrierSession.Length > 0 && (string.IsNullOrEmpty(sessionId) || (sessionId != carrierSession && !SoleRegistrationOf(carrier, sessionId))))
            {
                throw new MailboxError("decline_refused", $"only the session that carries entry {id}, or the one now registered as {carrier}, may decline its relay");
            }

            var row = new JsonObject { ["id"] = id, ["rung"] = 3, ["state"] = "declined", ["by"] = carrier, ["reason"] = reason };
            if (!string.IsNullOrEmpty(note))
            {
                row["note"] = note;
            }

            if (AppendLadderRow(dir, row, rows.Count + 1L))
            {
                return new JsonObject { ["declined"] = id, ["by"] = carrier, ["reason"] = reason };
            }
        }

        throw new MailboxError("busy", $"could not record the decline of {id}; try again");
    }

    /// <summary>Whether <paramref name="sessionId"/> is the only live registration claiming <paramref name="address"/>.</summary>
    private bool SoleRegistrationOf(string address, string sessionId)
    {
        var claiming = AllRegistrations().Where(r =>
            (r.Get("addresses") is { IsArray: true } list && list.AsArray().Any(a => a.IsText && a.AsString() == address))
            || (r.Get("address") is { IsText: true } single && single.AsString() == address)).ToList();
        return claiming.Count == 1 && claiming[0].Get("session_id") is { IsText: true } sid && sid.AsString() == sessionId;
    }
}
