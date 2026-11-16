using System.Globalization;
using Ah.Abstractions;

namespace Ah.StateDb;

/// <summary>The mail tables as the mailbox reads them, each answer through one read of the database.</summary>
internal sealed class StateDbMailStore(string ahHome, StateDbReadPool? reads = null) : IMailStore
{
    private const string TokenSql = "SELECT seq FROM domain_seq WHERE domain = 'mail'";

    public bool IsDatabase => !StateDbAuthority.NeverCreated(ahHome);

    private IReadOnlyList<object?[]> Rows(string sql, params object?[] args) =>
        StateDbStoreReads.Reading(ahHome, connection => StateDbRows.Run(connection, sql, args), reads);

    private static ReadOnlyMemory<byte>? First(IReadOnlyList<object?[]> rows) => rows.Count > 0 ? new ReadOnlyMemory<byte>((byte[])rows[0][0]!) : (ReadOnlyMemory<byte>?)null;

    public long ChangeToken() => Rows(TokenSql) is { Count: > 0 } rows ? (long)rows[0][0]! : 0;

    public string BoxStamp(string box)
    {
        var rows = Rows($"SELECT ({TokenSql}), (SELECT MAX(pk) FROM mail_entries WHERE box = ?1)", box);
        return string.Create(CultureInfo.InvariantCulture, $"{rows[0][0] ?? 0L}:{rows[0][1] ?? 0L}");
    }

    public long NewestEntry(string box) => Rows("SELECT MAX(pk) FROM mail_entries WHERE box = ?1", box)[0][0] as long? ?? 0;

    public IReadOnlyList<string> Boxes() =>
        [.. Rows("SELECT box FROM mail_entries UNION SELECT box FROM mail_acks UNION SELECT box FROM mail_attempts UNION SELECT box FROM mail_claims UNION SELECT box FROM mail_adapters")
            .Select(static r => (string)r[0]!)];

    public long HeartbeatBoxes() => Rows("SELECT COUNT(DISTINCT box) FROM mail_adapters")[0][0] as long? ?? 0;

    public IReadOnlyList<KeyValuePair<string, string>> BriefLadderStates() =>
        [.. Rows("SELECT e.ref, COALESCE((SELECT a.state FROM mail_attempts a WHERE a.box = e.box AND a.id = e.id AND a.ladder <> 0 ORDER BY a.pk DESC LIMIT 1), '') "
                + "FROM mail_entries e WHERE e.kind = 'brief' AND e.ref IS NOT NULL AND instr(e.box, '/') > 1")
            .Select(static r => KeyValuePair.Create((string)r[0]!, r[1] as string ?? string.Empty))];

    public IReadOnlyList<KeyValuePair<long, ReadOnlyMemory<byte>>> Entries(string box, long afterPk) =>
        [.. Rows("SELECT pk, raw FROM mail_entries WHERE box = ?1 AND pk > ?2 ORDER BY pk", box, afterPk)
            .Select(static r => KeyValuePair.Create((long)r[0]!, new ReadOnlyMemory<byte>((byte[])r[1]!)))];

    public IReadOnlyList<ReadOnlyMemory<byte>> UnackedEntriesWithIdPrefix(string box, string idPrefix) =>
        [.. Rows(
                "SELECT raw FROM mail_entries e WHERE box = ?1 AND id >= ?2 AND id < ?3 "
                + "AND NOT EXISTS (SELECT 1 FROM mail_acks a WHERE a.box = e.box AND a.id = e.id) ORDER BY pk",
                box,
                idPrefix,
                idPrefix[..^1] + (char)(idPrefix[^1] + 1))
            .Select(static r => new ReadOnlyMemory<byte>((byte[])r[0]!))];

    public IReadOnlyList<ReadOnlyMemory<byte>> Attempts(string box) =>
        [.. Rows("SELECT raw FROM mail_attempts WHERE box = ?1 ORDER BY pk", box).Select(static r => new ReadOnlyMemory<byte>((byte[])r[0]!))];

    public IReadOnlyList<ReadOnlyMemory<byte>> Attempts(string box, string id) =>
        [.. Rows("SELECT raw FROM mail_attempts WHERE box = ?1 AND id = ?2 ORDER BY pk", box, id).Select(static r => new ReadOnlyMemory<byte>((byte[])r[0]!))];

    public (IReadOnlyList<ReadOnlyMemory<byte>> Open, IReadOnlyList<KeyValuePair<string, string>> Settled) LadderSweep(string box, IReadOnlyList<string> settled)
    {
        ArgumentNullException.ThrowIfNull(settled);
        // ?2.. are the settled states; a null state is never one of them
        var states = string.Join(", ", settled.Select(static (_, i) => "?" + (i + 2).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        object?[] args = [box, .. settled];
        const string NewestState = "COALESCE((SELECT l.state FROM mail_attempts l WHERE l.box = t.box AND l.id = t.id AND l.ladder <> 0 ORDER BY l.pk DESC LIMIT 1), '')";
        var open = Rows($"SELECT raw FROM mail_attempts t WHERE box = ?1 AND ladder <> 0 AND {NewestState} NOT IN ({states}) ORDER BY pk", args)
            .Select(static r => new ReadOnlyMemory<byte>((byte[])r[0]!))
            .ToList();
        var done = Rows(
                $"SELECT id, s FROM (SELECT id, COALESCE(state, '') AS s, ROW_NUMBER() OVER (PARTITION BY id ORDER BY pk DESC) AS n FROM mail_attempts WHERE box = ?1 AND ladder <> 0) WHERE n = 1 AND s IN ({states})",
                args)
            .Select(static r => KeyValuePair.Create((string)r[0]!, (string)r[1]!))
            .ToList();
        return (open, done);
    }

    // b459 step 15: the count row is tagged 0 and sorted first, so it leads whatever the cursors; b500: it also carries the id at the cursor
    public (long Count, string? CursorId, string? LastId, IReadOnlyList<KeyValuePair<long, ReadOnlyMemory<byte>>> Rows) CountedEntries(string box, long afterPk)
    {
        var rows = Rows(
            "SELECT 0, count(*), NULL, (SELECT id FROM mail_entries WHERE box = ?1 AND pk = ?2) FROM mail_entries WHERE box = ?1 UNION ALL SELECT 1, pk, raw, id FROM mail_entries WHERE box = ?1 AND pk > ?2 ORDER BY 1, 2",
            box,
            afterPk);
        var cursorId = (string?)rows[0][3];
        return ((long)rows[0][1]!, cursorId, rows.Count > 1 ? (string?)rows[^1][3] : cursorId, [.. rows.Skip(1).Select(static r => KeyValuePair.Create((long)r[1]!, new ReadOnlyMemory<byte>((byte[])r[2]!)))]);
    }

    public string AckStamp(string box) =>
        Rows("SELECT count(*) FROM mail_acks WHERE box = ?1", box) is [var r] ? $"{r[0]}" : string.Empty;

    public ReadOnlyMemory<byte>? Ack(string box, string id) => First(Rows("SELECT raw FROM mail_acks WHERE box = ?1 AND id = ?2", box, id));

    public IReadOnlyList<string> AckIds(string box) => [.. Rows("SELECT id FROM mail_acks WHERE box = ?1", box).Select(static r => (string)r[0]!)];

    public ReadOnlyMemory<byte>? Claim(string box, string id) => First(Rows("SELECT raw FROM mail_claims WHERE box = ?1 AND id = ?2", box, id));

    public IReadOnlyList<string> ClaimIds(string box) => [.. Rows("SELECT id FROM mail_claims WHERE box = ?1 ORDER BY id", box).Select(static r => (string)r[0]!)];

    public IReadOnlyList<KeyValuePair<string, ReadOnlyMemory<byte>>> Adapters(string box) =>
        [.. Rows("SELECT session, raw FROM mail_adapters WHERE box = ?1 ORDER BY session", box)
            .Select(static r => KeyValuePair.Create((string)r[0]!, new ReadOnlyMemory<byte>((byte[])r[1]!)))];

    public ReadOnlyMemory<byte>? Note(string kind, string name) => First(Rows("SELECT raw FROM mail_notes WHERE kind = ?1 AND name = ?2", kind, name));
}
