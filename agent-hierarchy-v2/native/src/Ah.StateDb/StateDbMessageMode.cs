using System.Globalization;

namespace Ah.StateDb;

/// <summary>
/// The mode of each message domain, kept as one <c>meta</c> row per domain (<c>mode.&lt;domain&gt;</c>, <c>files</c> or <c>db</c>; no row
/// means <see cref="DefaultFor"/>), and the check a message write makes inside its transaction before it touches a store.
/// </summary>
internal static class StateDbMessageMode
{
    internal const string Files = "files";

    internal const string Db = "db";

    internal const string KeyPrefix = "mode.";

    /// <summary>The domains this build knows how to write. A mode row for any other domain means a newer build has moved data this one cannot place.</summary>
    internal static IReadOnlyList<string> KnownDomains { get; } = ["mail", "msgs", "peer-records", "backlog", "decisions", "asks"];

    /// <summary>The mode of a domain with no mode row: the database's. A files mode is reachable only through an explicit row, which the import replaces.</summary>
    internal static string DefaultFor(string domain) => Db;

    /// <summary>The mode of <paramref name="domain"/> as the connection's current transaction sees it.</summary>
    internal static string Read(SqliteConnection connection, string domain) => Read(new StateDbTx(connection), domain);

    /// <inheritdoc cref="Read(SqliteConnection, string)"/>
    internal static string Read(IStateDbTransaction tx, string domain)
    {
        var rows = tx.Query("SELECT value FROM meta WHERE key = ?1", KeyPrefix + domain);
        return rows.Count == 0 ? DefaultFor(domain) : (string)rows[0][0]!;
    }

    /// <summary>
    /// Why a message write must not go ahead, or null when it may: the schema is newer than this build knows, or a mode row names a
    /// domain this build does not know or holds a mode it does not know.
    /// </summary>
    internal static string? WriteRefusal(SqliteConnection connection) => WriteRefusal(new StateDbTx(connection));

    /// <inheritdoc cref="WriteRefusal(SqliteConnection)"/>
    internal static string? WriteRefusal(IStateDbTransaction tx) => WriteRefusal(tx.Query);

    /// <inheritdoc cref="WriteRefusal(SqliteConnection)"/>
    internal static string? WriteRefusal(Func<string, object?[], IReadOnlyList<object?[]>> query)
    {
        var version = query("SELECT value FROM meta WHERE key = 'schema_version'", []);
        if (version.Count > 0 && int.TryParse((string)version[0][0]!, NumberStyles.Integer, CultureInfo.InvariantCulture, out var found) && found > StateDbModule.SchemaVersion)
        {
            return $"the state database is schema {found}, newer than this binary's {StateDbModule.SchemaVersion}; message writes are refused";
        }

        foreach (var row in query("SELECT key, value FROM meta WHERE key >= 'mode.' AND key < 'mode/'", []))
        {
            var domain = ((string)row[0]!)[KeyPrefix.Length..];
            var mode = (string)row[1]!;
            if (!KnownDomains.Contains(domain, StringComparer.Ordinal) && domain is not ("teams" or "sessions"))
            {
                return $"the state database holds a mode for the unknown message domain '{domain}'; message writes are refused";
            }

            if (mode is not (Files or Db))
            {
                return $"the state database holds the unknown mode '{mode}' for the message domain '{domain}'; message writes are refused";
            }
        }

        return null;
    }
}
