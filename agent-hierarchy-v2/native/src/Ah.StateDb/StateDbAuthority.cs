using System.Runtime.InteropServices;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>The identity of the database, kept beside it so a missing or replaced file cannot pass for an empty store.</summary>
internal sealed record AuthorityMarker(string DbId);

internal enum StoreProblem
{
    None,
    Missing,
    Swapped,

    /// <summary>The identity could not be read (the database was busy or unreadable), so nothing is known and writers are refused.</summary>
    Unverified,
}

/// <summary>Whether the store may be opened, and when not, a message that starts with its code (<c>store-missing</c> or <c>store-swapped</c>).</summary>
internal sealed record StoreVerdict(StoreProblem Problem, string Detail)
{
    internal static StoreVerdict Ok { get; } = new(StoreProblem.None, string.Empty);
}

/// <summary>
/// The authority marker <c>state/ah.db.authority.json</c> and the checks built on it. It holds only the database's identity; a marker from an
/// earlier build may also hold <c>domains</c>, which is ignored.
/// </summary>
internal static partial class StateDbAuthority
{
    private const string DbIdKey = "db_id";
    private const string AuthorityPrefix = "authority.";

    internal static string MarkerPath(string ahHome) => Path.Join(ahHome, "state", "ah.db.authority.json");

    /// <summary>The marker, or null when there is none; <c>Unreadable</c> is true when a file exists that cannot be read as a marker.</summary>
    internal static (AuthorityMarker? Marker, bool Unreadable) Read(string ahHome) => File.Exists(MarkerPath(ahHome)) ? ReadFile(MarkerPath(ahHome)) : (null, false);

    private static (AuthorityMarker? Marker, bool Unreadable) ReadFile(string path)
    {
        try
        {
            var root = JsonText.Parse(File.ReadAllBytes(path));
            if (!root.IsMap || root.Member("db_id") is not { IsText: true } id || id.AsString().Length == 0)
            {
                return (null, true);
            }

            return (new AuthorityMarker(id.AsString()), false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return (null, true);
        }
    }

    /// <summary>Writes the marker through a temp file and a rename, flushing the file and then the directory, so it is whole or absent.</summary>
    internal static void Write(string ahHome, AuthorityMarker marker)
    {
        var path = MarkerPath(ahHome);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var text = JsonText.Stringify(new JsonObject { [DbIdKey] = marker.DbId }, 2) + "\n";
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            var bytes = new System.Text.UTF8Encoding(false).GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
        FlushDirectory(directory);
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenDirectory(string path, int flags);

    [LibraryImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static partial int Fsync(int fd);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int CloseDescriptor(int fd);

    private static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            var fd = OpenDirectory(directory, 0);
            if (fd < 0)
            {
                return;
            }

            _ = Fsync(fd);
            _ = CloseDescriptor(fd);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no libc to ask; the rename itself has happened
        }
    }

    /// <summary>
    /// Whether the database records a flip of <paramref name="domain"/>: its <c>authority.&lt;domain&gt;</c> row. No database has none; one that cannot be read
    /// counts as having it, so the stored row is what a caller keeps.
    /// </summary>
    internal static bool FlipRecorded(string ahHome, string domain)
    {
        try
        {
            var path = StateDbBackups.DatabasePath(ahHome);
            if (!File.Exists(path))
            {
                return false;
            }

            using var connection = SqliteConnection.Open(path, SqliteOpenMode.ReadOnly, 500);
            return MetaValue(connection, AuthorityPrefix + domain) == "db";
        }
        catch (SqliteException)
        {
            return true;
        }
    }

    internal static string? MetaValue(SqliteConnection connection, string key)
    {
        var rows = StateDbRows.Run(connection, "SELECT value FROM meta WHERE key = ?1", [key]);
        return rows.Count > 0 ? rows[0][0] as string : null;
    }

    /// <summary>
    /// Whether the store at <paramref name="ahHome"/> may be opened. A missing file with a marker is <c>store-missing</c>; a file whose
    /// <c>meta.db_id</c> is not the marker's is <c>store-swapped</c>. With no marker, or an unreadable one beside a database that exists,
    /// nothing is refused, because nothing can be compared. Nothing is written.
    /// </summary>
    internal static StoreVerdict Check(string ahHome, int tries = 1)
    {
        var (marker, unreadable) = Read(ahHome);
        var db = StateDbBackups.DatabasePath(ahHome);
        if (!File.Exists(db))
        {
            if (marker is null && !unreadable)
            {
                return StoreVerdict.Ok;
            }

            var newest = StateDbBackups.List(ahHome) is { Count: > 0 } all ? all[0].Name : "none";
            return new StoreVerdict(StoreProblem.Missing, $"store-missing: the state database is gone but its authority marker remains, so it existed and was lost; newest backup: {newest}; restore with: ah db restore (with no usable backup, only the user can accept an empty store: ah db init --accept-empty)");
        }

        if (marker is null)
        {
            return StoreVerdict.Ok;
        }

        for (var attempt = 0; attempt < Math.Max(1, tries); attempt++)
        {
            try
            {
                using var connection = SqliteConnection.Open(db, SqliteOpenMode.ReadOnly, 250);
                var id = MetaValue(connection, DbIdKey);
                return id == marker.DbId
                    ? StoreVerdict.Ok
                    : new StoreVerdict(StoreProblem.Swapped, "store-swapped: the state database is not the one the authority marker names, so writes are refused; restore with: ah db restore");
            }
            catch (SqliteException)
            {
                // busy or unreadable: try again, and when every try fails say so rather than pass
            }
        }

        return new StoreVerdict(StoreProblem.Unverified, "store-unverified: the identity of the state database could not be read (busy?), so writes are refused until it can be checked");
    }

    /// <summary>
    /// Whether no state database was ever created at <paramref name="ahHome"/>: neither the file nor its authority marker exists (ah-msgs-db-only §13.5).
    /// A marker without the file means a database that existed and was lost.
    /// </summary>
    internal static bool NeverCreated(string ahHome) => !File.Exists(StateDbBackups.DatabasePath(ahHome)) && !File.Exists(MarkerPath(ahHome));

    /// <summary>Creates the database for the first write of a home that never had one; one the marker says existed is not recreated. Why not, or null when a write may go on.</summary>
    internal static string? EnsureForWrite(string ahHome) =>
        LostDatabase(ahHome) ?? (NeverCreated(ahHome) ? StateDbModule.EnsureDatabase(ahHome) : null);

    /// <summary>Why a missing state database is an error and not "no messages yet": null when the file exists or was never created.</summary>
    internal static string? LostDatabase(string ahHome)
    {
        return File.Exists(StateDbBackups.DatabasePath(ahHome)) || NeverCreated(ahHome) ? null : Check(ahHome).Detail;
    }

    /// <summary>After a write that may be the first through a direct connection: makes sure the marker names the database.</summary>
    internal static void SettleAfter(string ahHome)
    {
        using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, 5000);
        _ = Settle(connection, ahHome);
    }

    /// <summary>
    /// For a writable open: gives the database its <c>meta.db_id</c> if it has none, then writes the marker if there is none or it cannot be read.
    /// Returns a <c>store-swapped</c> message when the marker names another database, and then changes nothing.
    /// </summary>
    internal static string? Settle(SqliteConnection connection, string ahHome)
    {
        var id = MetaValue(connection, DbIdKey);
        if (id is null)
        {
            _ = StateDbRows.Run(connection, "INSERT OR IGNORE INTO meta(key, value) VALUES (?1, ?2)", [DbIdKey, Guid.NewGuid().ToString("D")]);
            id = MetaValue(connection, DbIdKey)!;
        }

        return Locked(ahHome, () =>
        {
            var (marker, unreadable) = Read(ahHome);
            if (marker is not null && marker.DbId != id)
            {
                return "store-swapped: the state database is not the one the authority marker names, so writes are refused; restore with: ah db restore";
            }

            if (marker is null || unreadable)
            {
                Write(ahHome, new AuthorityMarker(id));
            }

            return (string?)null;
        });
    }

    /// <summary>After a restore through ah: makes the marker name the restored database.</summary>
    internal static void Adopt(string ahHome)
    {
        using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, 5000);
        var id = MetaValue(connection, DbIdKey);
        if (id is null)
        {
            _ = StateDbRows.Run(connection, "INSERT OR IGNORE INTO meta(key, value) VALUES (?1, ?2)", [DbIdKey, Guid.NewGuid().ToString("D")]);
            id = MetaValue(connection, DbIdKey)!;
        }

        _ = Locked(ahHome, () =>
        {
            Write(ahHome, new AuthorityMarker(id));
            return 0;
        });
    }

    /// <summary>
    /// Before a restore moves a backup into place: makes the marker name the backup's database, so a crash between the move and
    /// <see cref="Adopt"/> leaves a marker that matches the file. A backup with no identity is skipped.
    /// </summary>
    internal static void PreAdopt(string ahHome, string sourceDatabase)
    {
        string id;
        using (var source = SqliteConnection.Open(sourceDatabase, SqliteOpenMode.ReadOnly, 250))
        {
            if (MetaValue(source, DbIdKey) is not { } found)
            {
                return;
            }

            id = found;
        }

        _ = Locked(ahHome, () =>
        {
            Write(ahHome, new AuthorityMarker(id));
            return 0;
        });
    }

    /// <summary>The marker's bytes now, or null when it does not exist; <see cref="Reinstate"/> puts them back.</summary>
    internal static byte[]? Snapshot(string ahHome) => File.Exists(MarkerPath(ahHome)) ? File.ReadAllBytes(MarkerPath(ahHome)) : null;

    /// <summary>
    /// Puts the marker back to <paramref name="bytes"/> (null: no marker) after a restore that did not go ahead, unless it is no longer what
    /// the restore wrote (<paramref name="expected"/>): another opener changed it since, and that change stays.
    /// </summary>
    internal static void Reinstate(string ahHome, byte[]? bytes, byte[]? expected) => Locked(ahHome, () =>
    {
        var current = Snapshot(ahHome);
        if (!(current is null && expected is null) && !(current is not null && expected is not null && current.AsSpan().SequenceEqual(expected)))
        {
            return 0;
        }

        if (bytes is null)
        {
            File.Delete(MarkerPath(ahHome));
        }
        else
        {
            var temp = MarkerPath(ahHome) + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, MarkerPath(ahHome), overwrite: true);
        }

        return 0;
    });

    /// <summary>Runs <paramref name="work"/> holding the marker's lock file, which every read-modify-write of the marker takes; waits up to half a second for it.</summary>
    internal static T Locked<T>(string ahHome, Func<T> work)
    {
        var path = MarkerPath(ahHome) + ".lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FileStream? stream = null;
        for (var attempt = 0; stream is null; attempt++)
        {
            try
            {
                stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 25)
            {
                Thread.Sleep(20);
            }
        }

        using (stream)
        {
            return work();
        }
    }
}

/// <summary>A verdict that is looked up again after a short while, so a store swapped or removed under a long-lived writer is noticed; an unverified one is never kept.</summary>
internal sealed class StoreVerdictCache(string ahHome, int tries = 1)
{
    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(2);

    private StoreVerdict? _verdict;
    private long _at;

    internal StoreVerdict Get()
    {
        if (_verdict is not null && System.Diagnostics.Stopwatch.GetElapsedTime(_at) < Fresh)
        {
            return _verdict;
        }

        var fresh = StateDbAuthority.Check(ahHome, tries);
        _verdict = fresh.Problem == StoreProblem.Unverified ? null : fresh;
        _at = System.Diagnostics.Stopwatch.GetTimestamp();
        return fresh;
    }
}
