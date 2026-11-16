using Ah.Abstractions;

namespace Ah.Watch.Model;

/// <summary>
/// The timeline as the <c>timeline_events</c> table of the state database (<c>ah.db</c>, schema 44), which the state database's
/// migrations create. Rows older than the retention window are pruned. The store never creates, resets or moves the file: it opens
/// the file on first use, and while it cannot, each call drops its appends or reads empty and the next call tries again; a call that finds it damaged
/// yields the default, so the daemon never waits on it.
/// </summary>
internal sealed class SqliteTimelineStore : ITimelineStore, ITimelinePages
{
    /// <summary>The most read-only connections open at once; a page request beyond that waits for one.</summary>
    internal const int MaxReaders = 2;

    private const string Columns = "seq, dir_id, subject, COALESCE(from_state, ''), from_state IS NULL, to_state, at";

    private readonly ISqliteOpener _opener;
    private readonly string _path;
    private readonly int _busyTimeoutMs;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _readers = new(MaxReaders);
    private readonly System.Collections.Concurrent.ConcurrentBag<ISqliteConnection> _idleReaders = [];
    private ISqliteConnection? _connection;
    private bool _disposed;

    /// <param name="opener">The opener of the SQLite binding.</param>
    /// <param name="path">The path of the state database, <c>&lt;AH_HOME&gt;/state/ah.db</c>.</param>
    /// <param name="busyTimeoutMs">The busy timeout of every connection, the state database's own (<c>StateDbModule.BusyTimeoutMs</c>).</param>
    public SqliteTimelineStore(ISqliteOpener opener, string path, int busyTimeoutMs)
    {
        _opener = opener;
        _path = path;
        _busyTimeoutMs = busyTimeoutMs;
    }

    /// <summary>Whether the database is open, trying to open it now if it is not; false when the library or the file could not be opened.</summary>
    public bool Available
    {
        get
        {
            lock (_gate)
            {
                return Writer() is not null;
            }
        }
    }

    /// <summary>The writer's connection, opened on first use and again on each call after an open that failed; null while it cannot be opened or once disposed. Called under <see cref="_gate"/>.</summary>
    private ISqliteConnection? Writer()
    {
        if (_connection is not null || _disposed)
        {
            return _connection;
        }

        ISqliteConnection? connection = null;
        try
        {
            connection = _opener.Open(_path, SqliteOpenMode.ReadWrite, _busyTimeoutMs);
            _ = connection.ScalarText("PRAGMA journal_mode=WAL");
            _connection = connection;
        }
        catch (Exception)
        {
            connection?.Dispose();
        }

        return _connection;
    }

    /// <summary>The pauses before each retry of an append that found the database busy; with the busy timeout of each try, about 250 ms in all.</summary>
    internal static readonly int[] BusyRetryDelaysMs = [25, 75];

    /// <summary>Appends <paramref name="rows"/> in one transaction, retried after a pause while the database is busy; the last busy failure propagates.</summary>
    public void Append(IReadOnlyList<TimelineRow> rows)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                AppendOnce(rows);
                return;
            }
            catch (Exception failure) when (attempt < BusyRetryDelaysMs.Length && _opener.IsBusy(failure))
            {
                Thread.Sleep(BusyRetryDelaysMs[attempt]);
            }
        }
    }

    private void AppendOnce(IReadOnlyList<TimelineRow> rows)
    {
        _ = Run(connection =>
        {
            connection.Exec("BEGIN IMMEDIATE");
            try
            {
                using var insert = connection.Prepare("INSERT OR IGNORE INTO timeline_events(dir_id, subject, from_state, to_state, at) VALUES(?1, ?2, ?3, ?4, ?5)");
                foreach (var row in rows)
                {
                    insert.Reset();
                    insert.Bind(1, row.DirId);
                    insert.Bind(2, row.Subject);
                    if (row.From is null)
                    {
                        insert.BindNull(3);
                    }
                    else
                    {
                        insert.Bind(3, row.From);
                    }

                    insert.Bind(4, row.To);
                    insert.Bind(5, row.AtMs);
                    _ = insert.MoveNext();
                }

                connection.Exec("COMMIT");
            }
            catch
            {
                Rollback(connection);
                throw;
            }

            return 0;
        });
    }

    public string? LastState(string dirId, string subject) =>
        Run(connection =>
        {
            using var last = connection.Prepare("SELECT to_state FROM timeline_events WHERE dir_id = ?1 AND subject = ?2 ORDER BY seq DESC LIMIT 1");
            last.Bind(1, dirId);
            last.Bind(2, subject);
            return last.MoveNext() ? last.Text(0) : null;
        });

    public IReadOnlyList<TimelineSeed> Recent(string dirId, string subject, long sinceMs) =>
        Run(connection =>
        {
            var found = new List<TimelineSeed>();
            using (var before = connection.Prepare("SELECT to_state, at FROM timeline_events WHERE dir_id = ?1 AND subject = ?2 AND at < ?3 ORDER BY at DESC LIMIT 1"))
            {
                before.Bind(1, dirId);
                before.Bind(2, subject);
                before.Bind(3, sinceMs);
                if (before.MoveNext())
                {
                    found.Add(new TimelineSeed(subject, before.Text(0), before.Number(1)));
                }
            }

            using var within = connection.Prepare("SELECT to_state, at FROM timeline_events WHERE dir_id = ?1 AND subject = ?2 AND at >= ?3 ORDER BY at");
            within.Bind(1, dirId);
            within.Bind(2, subject);
            within.Bind(3, sinceMs);
            while (within.MoveNext())
            {
                found.Add(new TimelineSeed(subject, within.Text(0), within.Number(1)));
            }

            return (IReadOnlyList<TimelineSeed>)found;
        }) ?? [];

    public IReadOnlyList<TimelineRow> Range(string dirId, long fromMs, long toMs, long afterSeq, long beforeSeq, int limit) =>
        Run(connection => Query(connection, dirId, fromMs, toMs, afterSeq, beforeSeq, limit)) ?? [];

    public long Count(string dirId, long fromMs, long toMs) => Run(connection => CountOn(connection, dirId, fromMs, toMs));

    public TimelineSummary Summarize(string dirId, long fromMs, long toMs) => Run(connection => SummarizeOn(connection, dirId, fromMs, toMs));

    public int Prune(long beforeMs, int batch) =>
        Run(connection =>
        {
            using var delete = connection.Prepare("DELETE FROM timeline_events WHERE seq IN (SELECT seq FROM timeline_events WHERE at < ?1 ORDER BY at, seq LIMIT ?2)");
            delete.Bind(1, beforeMs);
            delete.Bind(2, batch);
            _ = delete.MoveNext();
            return (int)(connection.ScalarText("SELECT changes()") is { } changed ? long.Parse(changed, System.Globalization.CultureInfo.InvariantCulture) : 0);
        });

    public IReadOnlyList<TimelineRow> Page(string dirId, long fromMs, long toMs, long cursorSeq, bool older, int limit) =>
        Read(connection =>
        {
            var rows = new List<TimelineRow>();
            var direction = cursorSeq == 0 || older ? "DESC" : "ASC";
            var bound = cursorSeq == 0 ? "?4 = 0" : older ? "seq < ?4" : "seq > ?4";
            using var select = connection.Prepare(
                "SELECT " + Columns + " FROM timeline_events WHERE dir_id = ?1 AND at >= ?2 AND at < ?3 AND (" + bound + ") ORDER BY seq " + direction + " LIMIT ?5");
            select.Bind(1, dirId);
            select.Bind(2, fromMs);
            select.Bind(3, toMs);
            select.Bind(4, cursorSeq);
            select.Bind(5, Math.Clamp(limit, 0, 200));
            while (select.MoveNext())
            {
                rows.Add(ReadRow(select));
            }

            if (direction == "ASC")
            {
                rows.Reverse();
            }

            return rows;
        }) ?? [];

    public long CountIn(string dirId, long fromMs, long toMs) =>
        Read(connection =>
        {
            using var count = connection.Prepare("SELECT COUNT(*) FROM timeline_events WHERE dir_id = ?1 AND at >= ?2 AND at < ?3");
            count.Bind(1, dirId);
            count.Bind(2, fromMs);
            count.Bind(3, toMs);
            return count.MoveNext() ? count.Number(0) : 0;
        });

    public IReadOnlyList<TimelineSeed> Seeds(string dirId, long beforeMs) =>
        Read(connection =>
        {
            var subjects = new List<string>();
            using (var distinct = connection.Prepare("SELECT DISTINCT subject FROM timeline_events WHERE dir_id = ?1 AND at < ?2 ORDER BY subject"))
            {
                distinct.Bind(1, dirId);
                distinct.Bind(2, beforeMs);
                while (distinct.MoveNext())
                {
                    subjects.Add(distinct.Text(0));
                }
            }

            var seeds = new List<TimelineSeed>(subjects.Count);
            using var newest = connection.Prepare("SELECT to_state, at FROM timeline_events WHERE dir_id = ?1 AND subject = ?2 AND at < ?3 ORDER BY seq DESC LIMIT 1");
            foreach (var subject in subjects)
            {
                newest.Reset();
                newest.Bind(1, dirId);
                newest.Bind(2, subject);
                newest.Bind(3, beforeMs);
                if (newest.MoveNext())
                {
                    seeds.Add(new TimelineSeed(subject, newest.Text(0), newest.Number(1)));
                }
            }

            return seeds;
        }) ?? [];

    /// <summary>
    /// Runs <paramref name="work"/> on a read-only connection of the pool (at most <see cref="MaxReaders"/>, opened on first use). The writer's
    /// connection is never used. An unavailable store, or a database found damaged, yields the default; any other failure propagates.
    /// </summary>
    private T? Read<T>(Func<ISqliteConnection, T> work)
    {
        lock (_gate)
        {
            if (Writer() is null)
            {
                return default;
            }
        }

        _readers.Wait();
        ISqliteConnection? reader = null;
        try
        {
            reader = _idleReaders.TryTake(out var idle) ? idle : _opener.Open(_path, SqliteOpenMode.ReadOnly, _busyTimeoutMs);
            T result;
            lock (reader.Gate)
            {
                result = work(reader);
            }

            lock (_gate)
            {
                if (_connection is not null)
                {
                    _idleReaders.Add(reader);
                    reader = null;
                }
            }

            reader?.Dispose();
            return result;
        }
        catch (Exception failure) when (_opener.IsDamage(failure))
        {
            reader?.Dispose();
            return default;
        }
        catch
        {
            reader?.Dispose();
            throw;
        }
        finally
        {
            _ = _readers.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _connection?.Dispose();
            _connection = null;
        }

        while (_idleReaders.TryTake(out var idle))
        {
            idle.Dispose();
        }
    }

    private static TimelineRow ReadRow(ISqliteStatement s) =>
        new(s.Number(0), s.Text(1), s.Text(2), s.Number(4) == 1 ? null : s.Text(3), s.Text(5), s.Number(6));

    private static List<TimelineRow> Query(ISqliteConnection connection, string dirId, long fromMs, long toMs, long afterSeq, long beforeSeq, int limit)
    {
        var rows = new List<TimelineRow>();
        using var select = connection.Prepare(
            "SELECT " + Columns + " FROM timeline_events WHERE dir_id = ?1 AND at >= ?2 AND at <= ?3 AND (?4 = 0 OR seq > ?4) AND (?5 = 0 OR seq < ?5) ORDER BY seq LIMIT ?6");
        select.Bind(1, dirId);
        select.Bind(2, fromMs);
        select.Bind(3, toMs);
        select.Bind(4, afterSeq);
        select.Bind(5, beforeSeq);
        select.Bind(6, Math.Clamp(limit, 0, 200));
        while (select.MoveNext())
        {
            rows.Add(ReadRow(select));
        }

        return rows;
    }

    /// <summary>Ends a failed transaction without hiding the failure: SQLite may already have rolled it back, and then ROLLBACK itself throws.</summary>
    private static void Rollback(ISqliteConnection connection)
    {
        try
        {
            connection.Exec("ROLLBACK");
        }
        catch (Exception)
        {
            // the original failure is what the caller rethrows
        }
    }

    private static TimelineSummary SummarizeOn(ISqliteConnection connection, string dirId, long fromMs, long toMs)
    {
        using var summary = connection.Prepare(
            "SELECT COUNT(*), COALESCE(MAX(seq), 0), COALESCE(MIN(seq), 0), COALESCE(MIN(at), 0), COALESCE(MAX(at), 0) FROM timeline_events WHERE dir_id = ?1 AND at >= ?2 AND at <= ?3");
        summary.Bind(1, dirId);
        summary.Bind(2, fromMs);
        summary.Bind(3, toMs);
        return summary.MoveNext() ? new TimelineSummary(summary.Number(0), summary.Number(1), summary.Number(2), summary.Number(3), summary.Number(4)) : default;
    }

    private static long CountOn(ISqliteConnection connection, string dirId, long fromMs, long toMs)
    {
        using var count = connection.Prepare("SELECT COUNT(*) FROM timeline_events WHERE dir_id = ?1 AND at >= ?2 AND at <= ?3");
        count.Bind(1, dirId);
        count.Bind(2, fromMs);
        count.Bind(3, toMs);
        return count.MoveNext() ? count.Number(0) : 0;
    }

    /// <summary>Runs <paramref name="work"/> on the writer's connection; a damaged database or an unavailable store yields the default, any other failure propagates.</summary>
    private T? Run<T>(Func<ISqliteConnection, T> work)
    {
        lock (_gate)
        {
            var connection = Writer();
            if (connection is null)
            {
                return default;
            }

            try
            {
                lock (connection.Gate)
                {
                    return work(connection);
                }
            }
            catch (Exception failure) when (_opener.IsDamage(failure))
            {
                return default;
            }
        }
    }
}
