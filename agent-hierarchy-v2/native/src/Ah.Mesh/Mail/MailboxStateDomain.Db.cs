using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>The bodies of the mail ops for a home whose mail is in the database: one transaction each, its checks inside it.</summary>
internal sealed partial class MailboxStateDomain
{
    private string BoxKey(JsonValue a) => _mailbox.BoxKey(BoxDir(a));

    private static string? Column(JsonValue? value) => value is { IsText: true } text ? text.AsString() : null;

    private static byte[] Raw(JsonValue record) => Utf8Text.Encode(JsonText.Stringify(record, 0));

    private long NowMs => _clock.UtcNow.ToUnixTimeMilliseconds();

    private JsonValue DbAppend(JsonValue a, IDbOpTransaction tx)
    {
        var box = BoxKey(a);
        var entry = a.Member("entry") ?? throw new ArgumentException("entry is required");
        _ = EntryLine(entry);
        if (entry.Member("id") is not { IsText: true } idValue)
        {
            // the files store keeps such a line and no reader ever returns it
            return Flag("existed", false);
        }

        var id = idValue.AsString();
        if (tx.Query("SELECT 1 FROM mail_entries WHERE box = ?1 AND id = ?2", box, id).Count > 0)
        {
            return Flag("existed", true);
        }

        var kind = Column(entry.Member("kind"));
        var from = Column(entry.Member("from"));
        var reference = Column(entry.Member("ref"));
        var digest = Column(entry.Member("ref_sha256"));
        if (kind == "report" && digest is not null
            && tx.Query("SELECT id FROM mail_entries WHERE box = ?1 AND kind = 'report' AND ref_sha256 = ?2 AND \"from\" IS ?3 AND ref IS ?4", box, digest, from, reference) is { Count: > 0 } same)
        {
            return new JsonObject { ["existed"] = true, ["same"] = (string)same[0][0]! };
        }

        long? created = entry.Member("created") is { IsText: true } stamp && Timestamps.TryParseMs(stamp.AsString(), out var ms) ? (long)ms : null;
        tx.Execute(
            "INSERT INTO mail_entries(box, id, kind, \"from\", \"to\", to_session, ref, ref_sha256, created_ms, raw) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10)",
            box, id, kind, from, Column(entry.Member("to")), Column(entry.Member("to_session")), reference, digest, created, Raw(entry));
        if (a.Member("attempt") is { IsMap: true } attempt)
        {
            InsertAttempt(tx, box, attempt, null);
        }

        if (a.Member("claim") is { IsMap: true } claim && Mailbox.IsName(id) && ClaimOf(tx, box, id) is null)
        {
            PutClaim(tx, box, id, claim.AsObject());
        }

        return Flag("existed", false);
    }

    private void InsertAttempt(IDbOpTransaction tx, string box, JsonValue row, double? seq)
    {
        var record = AttemptRecord(row);
        var ladder = IsTrue(record.Get("ladder"));
        tx.Execute(
            "INSERT INTO mail_attempts(box, id, rung, state, ladder, ladder_seq, at_ms, raw) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8)",
            box, Column(record.Get("id")) ?? string.Empty, Column(record.Get("rung")), Column(record.Get("state")), ladder ? 1L : 0L, ladder && seq is { } n ? (long)n : null, NowMs, Raw(record));
    }

    private JsonValue DbAttempt(JsonValue a, IDbOpTransaction tx)
    {
        var box = BoxKey(a);
        var row = a.Member("row") ?? JsonValue.Null;
        var id = Column(row.Member("id")) ?? string.Empty;
        double? seq = a.Member("seq") is { IsNumber: true } number ? number.AsNumber() : null;
        if (seq is { } wanted && (long)tx.Query("SELECT COUNT(*) FROM mail_attempts WHERE box = ?1 AND id = ?2 AND ladder <> 0", box, id)[0][0]! + 1 != wanted)
        {
            return Flag("written", false);
        }

        if (a.Member("once") is { IsTruthy: true } && tx.Query("SELECT 1 FROM mail_attempts WHERE box = ?1 AND id = ?2 LIMIT 1", box, id).Count > 0)
        {
            return Flag("written", false);
        }

        InsertAttempt(tx, box, row, seq);
        return Flag("written", true);
    }

    private JsonValue DbHeartbeat(JsonValue a, IDbOpTransaction tx)
    {
        var box = BoxKey(a);

        // ponytail: walks the box's entries inside the write, linear in the box; start from the last acked_through if the hook benchmark at 40,000 asks for it
        var through = (long)tx.Query(
            "SELECT COALESCE(MAX(pk), 0) FROM mail_entries WHERE box = ?1 AND pk < COALESCE((SELECT MIN(e.pk) FROM mail_entries e WHERE e.box = ?1 AND NOT EXISTS (SELECT 1 FROM mail_acks k WHERE k.box = e.box AND k.id = e.id)), 9223372036854775807)",
            box)[0][0]!;
        tx.Execute(
            "INSERT INTO mail_adapters(box, session, at_ms, raw) VALUES (?1, ?2, ?3, ?4) ON CONFLICT(box, session) DO UPDATE SET at_ms = excluded.at_ms, raw = excluded.raw",
            box, HeartbeatName(a), NowMs, Raw(HeartbeatRecord(a, through)));
        return Flag("ok", true);
    }

    private JsonValue DbAck(JsonValue a, IDbOpTransaction tx)
    {
        var box = BoxKey(a);
        var id = TextOf(a, "id");
        if (Acked(tx, box, id))
        {
            return Flag("fresh", false);
        }

        var record = AckRecord(a, id);
        tx.Execute(
            "INSERT INTO mail_acks(box, id, at, by, reason, session_id, raw) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7)",
            box, id, Column(record.Get("at")), Column(record.Get("by")), Column(record.Get("reason")), Column(record.Get("session_id")), Raw(record));
        return Flag("fresh", true);
    }

    private static bool Acked(IDbOpTransaction tx, string box, string id) => tx.Query("SELECT 1 FROM mail_acks WHERE box = ?1 AND id = ?2", box, id).Count > 0;

    private static JsonObject? ClaimOf(IDbOpTransaction tx, string box, string id) =>
        tx.Query("SELECT raw FROM mail_claims WHERE box = ?1 AND id = ?2", box, id) is { Count: > 0 } rows ? Mailbox.ClaimRecordOf((byte[])rows[0][0]!) : null;

    private void PutClaim(IDbOpTransaction tx, string box, string id, JsonObject record) =>
        tx.Execute(
            "INSERT INTO mail_claims(box, id, state, at_ms, raw) VALUES (?1, ?2, ?3, ?4, ?5) ON CONFLICT(box, id) DO UPDATE SET state = excluded.state, at_ms = excluded.at_ms, raw = excluded.raw",
            box, id, Column(record.Get("state")), NowMs, Raw(record));

    private JsonValue DbClaim(JsonValue a, IDbOpTransaction tx)
    {
        var box = BoxKey(a);
        var id = TextOf(a, "id");
        var action = TextOf(a, "action");
        if (!Mailbox.IsName(id))
        {
            throw new ArgumentException("id must match [A-Za-z0-9._-]+");
        }

        var step = ClaimMachine.Apply(ClaimOf(tx, box, id), action, a.AsObject(), _clock.UtcNow, Acked(tx, box, id));
        if (step.Record is { } record)
        {
            PutClaim(tx, box, id, record);
            foreach (var row in step.Rows)
            {
                var full = new JsonObject { ["id"] = id };
                foreach (var key in row.Keys)
                {
                    full[key] = row[key];
                }

                InsertAttempt(tx, box, full, null);
            }
        }

        return ClaimMachine.ToJson(step.Result);
    }

    private static JsonValue DbNote(JsonValue a, IDbOpTransaction tx)
    {
        var kind = TextOf(a, "kind");
        var name = TextOf(a, "name");
        if (!Mailbox.IsName(kind) || !Mailbox.IsName(name))
        {
            throw new ArgumentException("kind and name must match [A-Za-z0-9._-]+");
        }

        var held = tx.Query("SELECT raw FROM mail_notes WHERE kind = ?1 AND name = ?2", kind, name);
        if (IsTrue(a.Member("clear")))
        {
            tx.Execute("DELETE FROM mail_notes WHERE kind = ?1 AND name = ?2", kind, name);
            return Flag("removed", held.Count > 0);
        }

        var data = a.Member("data") is { IsMap: true } map ? map : throw new ArgumentException("data must be an object");
        if (a.Member("expect_seq") is { IsNumber: true } expect)
        {
            var seq = held.Count > 0 && JsonText.Parse((byte[])held[0][0]!).Member("seq") is { IsNumber: true } number ? number.AsNumber() : 0;
            if (seq != expect.AsNumber())
            {
                return Flag("written", false);
            }
        }

        tx.Execute(
            "INSERT INTO mail_notes(kind, name, raw) VALUES (?1, ?2, ?3) ON CONFLICT(kind, name) DO UPDATE SET raw = excluded.raw",
            kind, name, Raw(data));
        return Flag("written", true);
    }
}
