namespace Ah.Core.Json;

/// <summary>
/// The crash window of a database write (ah-msgs-fast-2 §3.8 H1, ruling B): its body marks status dirty in the write's own
/// transaction, the post-commit delta clears the mark, and a mark left by a crash makes the next start or status-dir refresh rebuild.
/// </summary>
public static class StatusDirty
{
    /// <summary>The <c>meta</c> key prefix of a mark.</summary>
    public const string Prefix = "status.dirty:";

    /// <summary>The database clock in ms, as a SQL expression: a mark's time and its age are both read from it.</summary>
    public const string NowMsSql = "CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)";

    /// <summary>Upserts one mark: ?1 key, ?2 pool dir (empty when the write names none); the value is the mark's ms, a tab, the pool dir.</summary>
    public const string MarkSql = "INSERT OR REPLACE INTO meta(key, value) VALUES (?1, CAST(" + NowMsSql + " AS TEXT) || char(9) || ?2)";

    /// <summary>The mark of one write: the same for its body and its AfterWrite, which see the same domain, op and arguments.</summary>
    public static string KeyOf(string domain, string op, ReadOnlyMemory<byte> argsJson)
    {
        var head = System.Text.Encoding.UTF8.GetBytes(domain + "\n" + op + "\n");
        var all = new byte[head.Length + argsJson.Length];
        head.CopyTo(all, 0);
        argsJson.Span.CopyTo(all.AsSpan(head.Length));
        return Prefix + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(all), 0, 12);
    }
}
