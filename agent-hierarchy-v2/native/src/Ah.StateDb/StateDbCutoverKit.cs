using System.Globalization;
using Ah.Core.Text;

namespace Ah.StateDb;

/// <summary>What the import of a domain shares with the import of every other: the schema read, the flip and path containment.</summary>
internal static class StateDbCutoverKit
{
    internal static long Schema(IStateDbTransaction tx) =>
        tx.Query("SELECT value FROM meta WHERE key = 'schema_version'") is { Count: > 0 } rows ? long.Parse(Convert.ToString(rows[0][0], CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture) : 0;

    /// <summary>Sets <c>mode.&lt;domain&gt;</c> and <c>authority.&lt;domain&gt;</c> to <c>db</c>, and bumps the domain's change sequence.</summary>
    internal static void Flip(IStateDbTransaction tx, string domain, DateTimeOffset now)
    {
        const string Set = "INSERT INTO meta(key, value) VALUES (?1, 'db') ON CONFLICT(key) DO UPDATE SET value = excluded.value";
        tx.Execute(Set, "mode." + domain);
        tx.Execute(Set, "authority." + domain);
        tx.Execute(
            "INSERT INTO domain_seq(domain, seq, changed_ms) VALUES (?1, 1, ?2) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = ?2",
            domain,
            now.ToUnixTimeMilliseconds());
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="ahHome"/> or lies under it, once every symbolic link on both is resolved. The path need not exist.</summary>
    internal static bool Within(string ahHome, string path) => PathGuard.Within(ahHome, path);
}
