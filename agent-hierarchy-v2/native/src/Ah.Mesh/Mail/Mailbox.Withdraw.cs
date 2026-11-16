using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Mesh;

internal sealed partial class Mailbox
{
    private static readonly string[] ClosedStates = ["withdrawn", "superseded", "declined"];

    public MailboxOutcome<JsonObject> WithdrawPlan(string id, string from)
    {
        try
        {
            return new MailboxOutcome<JsonObject>(WithdrawPlanCore(id, from), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<JsonObject>(null, e.ToFailure());
        }
    }

    public MailboxOutcome<JsonObject> Withdraw(string dir, string id, string by, string reason, string? readdressedTo)
    {
        try
        {
            return new MailboxOutcome<JsonObject>(WithdrawCore(dir, id, by, reason, readdressedTo), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<JsonObject>(null, e.ToFailure());
        }
    }

    /// <summary>
    /// What a withdrawal of entry <paramref name="id"/> by <paramref name="from"/> would act on, read before anything is written:
    /// the entry's box, its request file and that file's role, and how far the addressee got (<c>response_drafted</c>, <c>in_turn</c>,
    /// <c>delivered</c> or <c>not_delivered</c>). Refused when no box holds the entry (<c>unknown</c>), <paramref name="from"/> is not
    /// its sender (<c>not_sender</c>), it is not a brief (<c>bad_request</c>), its ladder already ended (<c>already_closed</c>) or its
    /// report was already sent (<c>already_reported</c>).
    /// </summary>
    private JsonObject WithdrawPlanCore(string id, string from)
    {
        var at = MsgStatus.Locate(this, id) ?? throw new MailboxError("unknown", $"no entry {id} in any mailbox");
        var entry = at.Entry;
        var sender = entry.Get("from") is { IsText: true } f && f.AsString().Length > 0 ? f.AsString() : "user";
        if (sender != from)
        {
            throw new MailboxError("not_sender", $"only the sender of entry {id} ({sender}) can withdraw it");
        }

        if (!(entry.Get("kind") is { IsText: true } kind && kind.AsString() == "brief") || entry.Get("ref") is not { IsText: true } reference)
        {
            throw new MailboxError("bad_request", $"entry {id} is not a brief; only a brief can be withdrawn");
        }

        var rows = LadderRowsOf(at.Dir, id);
        if (rows.Count > 0 && rows[^1].Get("state") is { IsText: true } last && ClosedStates.Contains(last.AsString(), StringComparer.Ordinal))
        {
            throw new MailboxError("already_closed", $"entry {id} is already {last.AsString()}");
        }

        var to = entry.Get("to") is { IsText: true } t ? t.AsString() : at.Address;
        var meta = hierarchy.Core.ParseMsgFilename(reference.AsString());
        var responsePath = hierarchy.Messages.ResponsePlan(reference) is { IsMap: true } plan && plan.Member("path") is { IsText: true } path ? path.AsString() : null;
        var drafted = false;
        if (responsePath is not null && hierarchy.Core.MsgExists(responsePath))
        {
            drafted = meta is null || hierarchy.Messages.ReportStatus(responsePath, meta.Id) != "no-report";
            if (drafted && ListBoxes().Where(b => b.Address == sender).Any(b => FindReport(b.Dir, responsePath, null, address => address == to, null) is not null))
            {
                throw new MailboxError("already_reported", $"entry {id} was already reported by {to}; there is nothing left to withdraw");
            }
        }

        var ack = ReadAck(at.Dir, id) is { IsMap: true } a ? a.AsObject() : null;
        var ackedBy = ack is not null && ack.Get("by") is { IsText: true } by ? by.AsString() : null;
        var state = MsgStatus.Of(this, at).Get("state") is { IsText: true } s ? s.AsString() : string.Empty;
        var progress = drafted ? "response_drafted"
            : ackedBy is "prompt-hook" or "stop-hook" ? "in_turn"
            : ack is not null || state is "acked" or "delivered" ? "delivered"
            : "not_delivered";
        return new JsonObject
        {
            ["entry"] = id,
            ["dir"] = at.Dir,
            ["address"] = at.Address,
            ["from"] = sender,
            ["to"] = to,
            ["ref"] = reference,
            ["eta"] = entry.Get("eta") is { IsText: true } eta ? eta : JsonValue.Null,
            ["role"] = meta is null ? JsonValue.Null : meta.To,
            ["progress"] = progress,
            ["acked_by"] = ackedBy is null ? JsonValue.Null : ackedBy,
            ["acked_at"] = ack is not null && ack.Get("at") is { IsText: true } ackAt ? ackAt : JsonValue.Null,
            ["response"] = drafted && responsePath is not null ? responsePath : JsonValue.Null,
        };
    }

    public int WithdrawRewakes(string dir, string? session, string? keepId, double? createdBeforeMs, string reason)
    {
        const string prefix = Ah.Core.Digests.RewakePaths.IdPrefix;

        // b414: with mail in the database, one indexed query reads only the unacked rewake rows.
        IEnumerable<JsonValue> entries;
        Func<string, bool> acked;
        if (Stored is { } store)
        {
            entries = store.UnackedEntriesWithIdPrefix(BoxKey(dir), prefix).Select(static raw => ParseOrNull(Utf8Text.Decode(raw.Span))).OfType<JsonValue>();
            acked = static _ => false;
        }
        else
        {
            entries = ReadEntries(dir).Entries;
            acked = AckedIn(dir);
        }

        var withdrawn = 0;
        foreach (var e in entries.Where(static e => e.IsMap).Select(static e => e.AsObject()))
        {
            var id = Js(e.Get("id"));
            if (!id.StartsWith(prefix, StringComparison.Ordinal) || id == keepId || acked(id)
                || (session is not null && Js(e.Get("to_session")) != session)
                || (createdBeforeMs is { } cutoff && !(Timestamps.TryParseMs(Js(e.Get("created")), out var created) && created < cutoff)))
            {
                continue;
            }

            if (Withdraw(dir, id, "ahd", reason, null).Value is not null)
            {
                withdrawn++;
            }
        }

        return withdrawn;
    }

    /// <summary>
    /// Appends the terminal <c>withdrawn</c> ladder row of entry <paramref name="id"/> in <paramref name="dir"/>: it ranks with
    /// <c>superseded</c> and <c>declined</c>, above an ack, and the entry line itself is not touched. Allowed once the entry was acked or
    /// delivered; refused when its ladder already ended.
    /// </summary>
    private JsonObject WithdrawCore(string dir, string id, string by, string reason, string? readdressedTo)
    {
        if (reason.Trim().Length == 0)
        {
            throw new MailboxError("bad_request", "--reason is required");
        }

        var old = ReadEntries(dir).Entries.Where(e => e.IsMap).Select(e => e.AsObject())
            .FirstOrDefault(e => e.Get("id") is { IsText: true } eid && eid.AsString() == id)
            ?? throw new MailboxError("unknown", $"no entry {id} in {dir}");
        var at = Timestamps.ToIsoString(clock.UtcNow);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var rows = LadderRowsOf(dir, id);
            if (rows.Count > 0 && rows[^1].Get("state") is { IsText: true } last && ClosedStates.Contains(last.AsString(), StringComparer.Ordinal))
            {
                throw new MailboxError("already_closed", $"entry {id} is already {last.AsString()}");
            }

            var rung = rows.Count > 0 && rows[^1].Get("rung") is { IsNumber: true } r ? r : JsonValue.FromNumber(Js(old.Get("route")) == "pull" ? 2 : 3);
            var row = new JsonObject { ["id"] = id, ["rung"] = rung, ["state"] = "withdrawn", ["by"] = by, ["at"] = at, ["reason"] = reason };
            if (readdressedTo is not null)
            {
                row["readdressed_to"] = readdressedTo;
            }

            if (AppendLadderRow(dir, row, rows.Count + 1L))
            {
                return new JsonObject { ["withdrawn"] = id, ["by"] = by, ["at"] = at, ["reason"] = reason, ["readdressed_to"] = readdressedTo is null ? JsonValue.Null : readdressedTo };
            }
        }

        throw new MailboxError("busy", $"could not record the withdrawal of {id}; try again");
    }

    /// <summary>
    /// The file names of the request files with at least one withdrawn brief and no live one. A brief is live while its last ladder state
    /// is not withdrawn, superseded or declined, so a request re-addressed or sent to a second addressee stays out of the set.
    /// Every box counts, as a dispatch row's address is free-form and cannot say which boxes hold the briefs; with mail in the database, one
    /// query answers, kept until a mail write moves the token.
    /// </summary>
    public IReadOnlySet<string> WithdrawnRequestFiles()
    {
        if (Stored is { } store)
        {
            return CachedByToken<IReadOnlySet<string>>(_storedWithdrawn, string.Empty, store, () => Withdrawn(store.BriefLadderStates()));
        }

        var briefs = new List<KeyValuePair<string, string>>();
        foreach (var box in ListBoxes())
        {
            var lastStates = LadderRowsByEntry(box.Dir).ToDictionary(
                e => e.Id,
                e => e.Rows.Count > 0 && e.Rows[^1].Get("state") is { IsText: true } s ? s.AsString() : string.Empty,
                StringComparer.Ordinal);
            foreach (var v in ReadEntries(box.Dir).Entries)
            {
                if (v.IsMap && v.AsObject() is var e && e.Get("kind") is { IsText: true } kind && kind.AsString() == "brief"
                    && e.Get("ref") is { IsText: true } reference && e.Get("id") is { IsText: true } eid)
                {
                    briefs.Add(KeyValuePair.Create(reference.AsString(), lastStates.GetValueOrDefault(eid.AsString(), string.Empty)));
                }
            }
        }

        return Withdrawn(briefs);
    }

    private readonly Dictionary<string, (long Token, IReadOnlySet<string> Value)> _storedWithdrawn = new(StringComparer.Ordinal);

    /// <summary>The request file names of <paramref name="briefs"/> (ref, last ladder state) with a withdrawn brief and no live one.</summary>
    private static HashSet<string> Withdrawn(IEnumerable<KeyValuePair<string, string>> briefs)
    {
        var gone = new HashSet<string>(StringComparer.Ordinal);
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (reference, last) in briefs)
        {
            if (last == "withdrawn")
            {
                gone.Add(NodePath.Basename(reference));
            }
            else if (!ClosedStates.Contains(last, StringComparer.Ordinal))
            {
                live.Add(NodePath.Basename(reference));
            }
        }

        gone.ExceptWith(live);
        return gone;
    }
}
