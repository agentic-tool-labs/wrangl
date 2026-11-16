using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The connections of finished reads of one home, kept for its next reads. A long-lived process composes one: a new connection parses the
/// whole schema again, which the daemon's many small reads pay every time. A short-lived one has no next read to save anything for.
/// </summary>
internal sealed class StateDbReadPool
{
    private const int MaxIdle = 4;

    // A plain stack under a lock held for a few instructions and never across I/O. A ConcurrentBag here took every thread's lock on each Count and walked every thread's list on each take,
    // so each read cost more as threads were added, and blocked reads made the thread pool add more.
    private readonly Stack<Idle> _idle = new(MaxIdle);

    private sealed record Idle(DateTime Born, SqliteConnection Connection);

    private bool _closed;

    /// <summary>Closes every idle connection; from here on a returned one is closed rather than kept.</summary>
    internal void Close()
    {
        List<Idle> idle;
        lock (_idle)
        {
            _closed = true;
            idle = [.. _idle];
            _idle.Clear();
        }

        foreach (var i in idle)
        {
            i.Connection.Dispose();
        }
    }

    internal int IdleCount
    {
        get
        {
            lock (_idle)
            {
                return _idle.Count;
            }
        }
    }

    /// <summary>An idle connection to this very file, or null. One to a file a restore has since replaced at that path is closed: it would go on reading the old file.</summary>
    internal SqliteConnection? Rent(DateTime born)
    {
        while (Take() is { } idle)
        {
            if (idle.Born == born)
            {
                return idle.Connection;
            }

            idle.Connection.Dispose();
        }

        return null;
    }

    private Idle? Take()
    {
        lock (_idle)
        {
            return _idle.TryPop(out var idle) ? idle : null;
        }
    }

    /// <summary>Keeps <paramref name="connection"/> unless enough are idle; false when the caller still has to close it.</summary>
    internal bool Keep(DateTime born, SqliteConnection connection)
    {
        lock (_idle)
        {
            if (_closed || _idle.Count >= MaxIdle)
            {
                return false;
            }

            _idle.Push(new Idle(born, connection));
            return true;
        }
    }
}

/// <summary>The one way a domain's reader asks the database. A read that cannot answer throws <see cref="StoreUnavailableException"/> and never reads as absent.</summary>
internal static class StateDbStoreReads
{
    private const int ReadBudgetMs = 250;

    /// <summary>
    /// Runs <paramref name="read"/>. A busy database gets one more try, the two together inside a hook's 250 ms read budget. A newer schema is refused rather than misread.
    /// A read naming no pool uses the one a long-lived process shared for this database (b434), else opens its own.
    /// </summary>
    internal static T Reading<T>(string ahHome, Func<SqliteConnection, T> read, StateDbReadPool? pool = null)
    {
        pool ??= StateDbModule.SharedReadPool(StateDbBackups.DatabasePath(ahHome));
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var path = StateDbBackups.DatabasePath(ahHome);
                var born = pool is null ? default : File.GetCreationTimeUtc(path);
                var connection = pool?.Rent(born) ?? SqliteConnection.Open(path, SqliteOpenMode.ReadOnly, ReadBudgetMs / 2);
                if (pool is not null)
                {
                    connection.CacheStatements();
                }

                var succeeded = false;
                try
                {
                    if (StateDbFile.ReadVersion(connection) is { } version && version > StateDbModule.SchemaVersion)
                    {
                        throw new StoreUnavailableException($"the state database is schema {version}, newer than this binary's {StateDbModule.SchemaVersion}; update ah");
                    }

                    var result = read(connection);
                    succeeded = true;
                    return result;
                }
                finally
                {
                    if (!(succeeded && pool is not null && pool.Keep(born, connection)))
                    {
                        connection.Dispose();
                    }
                }
            }
            catch (SqliteException e) when (attempt == 1 && e.IsBusy)
            {
            }
            catch (SqliteException e)
            {
                throw new StoreUnavailableException($"the state database could not be read: {e.Message}", e);
            }
        }
    }

    /// <summary>
    /// <paramref name="read"/> through <see cref="Reading{T}"/>, which tries a busy database once more; none when the database was never
    /// created, or when it still cannot be read (still busy, a missing table, a newer schema), as an unreadable file read as no rows.
    /// </summary>
    internal static List<T> ReadOrEmpty<T>(string ahHome, Func<SqliteConnection, List<T>> read)
    {
        if (StateDbAuthority.NeverCreated(ahHome))
        {
            return [];
        }

        try
        {
            return Reading(ahHome, read);
        }
        catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
        {
            return [];
        }
    }
}
