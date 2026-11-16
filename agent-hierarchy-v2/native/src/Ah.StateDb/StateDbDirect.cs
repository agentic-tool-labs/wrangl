using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>How long a direct write waits: each attempt lets the busy handler wait <paramref name="BusyMs"/>, and there are <paramref name="Attempts"/> of them.</summary>
internal sealed record DirectWriteOptions(int BusyMs, int Attempts)
{
    /// <summary>How long each attempt of a DB-backed message write waits for the write lock: 50 ms failed under 44 concurrent writers, 200 ms did not.</summary>
    internal const int MessageBusyMs = 200;

    internal static DirectWriteOptions Default { get; } = new(2000, 3);

    /// <summary>A DB-backed message write: the message busy wait, tried three times like the other direct writers.</summary>
    internal static DirectWriteOptions Message { get; } = new(MessageBusyMs, 3);

    /// <summary>One short attempt, so a write from a hook gives up inside the hook's own budget.</summary>
    internal static DirectWriteOptions Hook { get; } = new(300, 1);
}

internal sealed class StateDbDirect(string ahHome, DirectWriteOptions options) : IStateDbDirect
{
    private StoreVerdictCache? _cache;

    public DirectWriteResult<T> Write<T>(Func<IStateDbTransaction, T> work)
    {
        var path = StateDbBackups.DatabasePath(ahHome);
        if (!File.Exists(path))
        {
            return Fail<T>(StateDbAuthority.Check(ahHome) is { Problem: StoreProblem.Missing } missing ? missing.Detail : "the state database does not exist; start ahd");
        }

        var verdict = (_cache ??= new StoreVerdictCache(ahHome)).Get();
        if (verdict.Problem != StoreProblem.None)
        {
            return Fail<T>(verdict.Detail);
        }

        SqliteConnection connection;
        try
        {
            connection = SqliteConnection.Open(path, SqliteOpenMode.ReadWrite, options.BusyMs);
        }
        catch (SqliteException e)
        {
            return Fail<T>($"the state database could not be opened: {e.Message}");
        }

        using (connection)
        {
            try
            {
                connection.Exec("PRAGMA synchronous=FULL");
                connection.Exec("PRAGMA wal_autocheckpoint=0");
                connection.Exec("PRAGMA foreign_keys=ON");
                if (SchemaProblem(StateDbFile.ReadVersion(connection)) is { } problem)
                {
                    return Fail<T>(problem);
                }

                return Run(connection, work);
            }
            catch (SqliteException e)
            {
                return Fail<T>($"the state database could not be written: {e.Message}");
            }
        }
    }

    public OnceOutcome WriteOnce(string writeId, Func<IStateDbTransaction, string> work)
    {
        OnceOutcome? outcome = null;
        var written = Write(tx =>
        {
            outcome = StateDbOnce.Apply(tx, writeId, "direct", null, static () => DateTimeOffset.UtcNow, work);
            return 0;
        });
        return written.Ok ? outcome! : new OnceOutcome(OnceStatus.Refused, null, null, written.Message, written.DaemonPid);
    }

    private static string? SchemaProblem(int? version) => version switch
    {
        null => "the state database has no schema yet; start ahd",
        { } v when v > StateDbModule.SchemaVersion => $"the state database is schema {v}, newer than this binary's {StateDbModule.SchemaVersion}; it is read only here",
        { } v when v < StateDbModule.SchemaVersion => $"the state database is schema {v}, older than this binary's {StateDbModule.SchemaVersion}; run `ahd restart`, which backs it up and migrates it",
        _ => null,
    };

    private DirectWriteResult<T> Run<T>(SqliteConnection connection, Func<IStateDbTransaction, T> work)
    {
        var clock = Stopwatch.StartNew();
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                connection.Exec("BEGIN IMMEDIATE");
                break;
            }
            catch (SqliteException e) when (e.IsBusy)
            {
                if (attempt >= options.Attempts)
                {
                    return Busy<T>(clock.Elapsed);
                }
            }
        }

        try
        {
            if (SchemaProblem(StateDbFile.ReadVersion(connection)) is { } changed)
            {
                connection.Exec("ROLLBACK");
                return Fail<T>(changed);
            }

            var value = work(new StateDbTx(connection));
            connection.Exec("COMMIT");
            return new DirectWriteResult<T>(true, value, string.Empty, null);
        }
        catch
        {
            try
            {
                connection.Exec("ROLLBACK");
            }
            catch (SqliteException)
            {
                // the transaction is already gone, which is what the rollback wanted
            }

            throw;
        }
    }

    private DirectWriteResult<T> Busy<T>(TimeSpan waited)
    {
        var seconds = (int)Math.Round(waited.TotalSeconds, MidpointRounding.AwayFromZero);
        var pid = LiveDaemonPid(ahHome);
        var text = pid is { } p
            ? $"database busy for {seconds.ToString(CultureInfo.InvariantCulture)} s; ahd pid {p.ToString(CultureInfo.InvariantCulture)} may be hung"
                + (StateDbModule.DirectWriteHung(p) is { Length: > 0 } note ? "; " + note : string.Empty)
            : $"database busy for {seconds.ToString(CultureInfo.InvariantCulture)} s; another process holds the write lock and no ahd is running";
        return new DirectWriteResult<T>(false, default, text, pid);
    }

    private static DirectWriteResult<T> Fail<T>(string message) => new(false, default, message, null);

    /// <summary>The process id <c>run/ahd.json</c> names when that process is running.</summary>
    internal static int? LiveDaemonPid(string ahHome)
    {
        try
        {
            var record = JsonText.Parse(File.ReadAllBytes(Path.Join(ahHome, "run", "ahd.json")));
            if (record.Member("pid") is not { IsNumber: true } number)
            {
                return null;
            }

            var pid = (int)number.AsNumber();
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : pid;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>One read connection. With a pool, a clean first <see cref="Dispose"/> hands it back for the next reader of the file born at <c>born</c>.</summary>
internal sealed class StateDbReader(SqliteConnection connection, StateDbReadPool? pool = null, DateTime born = default) : IStateDbReader
{
    private int _disposed;
    private volatile bool _failed;

    internal static StateDbReaderOpen Open(string ahHome, StateDbReadPool? pool = null)
    {
        var path = StateDbBackups.DatabasePath(ahHome);
        if (!File.Exists(path))
        {
            return new StateDbReaderOpen(null, StateDbAuthority.Check(ahHome) is { Problem: StoreProblem.Missing } missing ? missing.Detail : "the state database does not exist yet");
        }

        try
        {
            if (pool is null)
            {
                return new StateDbReaderOpen(new StateDbReader(SqliteConnection.Open(path, SqliteOpenMode.ReadOnly, 250)), string.Empty);
            }

            var born = File.GetCreationTimeUtc(path);
            var connection = pool.Rent(born) ?? SqliteConnection.Open(path, SqliteOpenMode.ReadOnly, 250);
            return new StateDbReaderOpen(new StateDbReader(connection, pool, born), string.Empty);
        }
        catch (SqliteException e)
        {
            return new StateDbReaderOpen(null, $"the state database could not be opened for reading: {e.Message}");
        }
    }

    public IReadOnlyList<object?[]> Query(string sql, params object?[] args)
    {
        lock (connection.Gate)
        {
            try
            {
                return StateDbRows.Run(connection, sql, args);
            }
            catch
            {
                _failed = true;
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (!(pool is not null && !_failed && pool.Keep(born, connection)))
        {
            connection.Dispose();
        }
    }
}

internal static class StateDbRows
{
    internal static IReadOnlyList<object?[]> Run(SqliteConnection connection, string sql, object?[] args)
    {
        using var statement = connection.Prepare(sql);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case null:
                    statement.BindNull(i + 1);
                    break;
                case long l:
                    statement.Bind(i + 1, l);
                    break;
                case int n:
                    statement.Bind(i + 1, (long)n);
                    break;
                case bool b:
                    statement.Bind(i + 1, b ? 1L : 0L);
                    break;
                case string s:
                    statement.Bind(i + 1, s);
                    break;
                case byte[] bytes:
                    statement.Bind(i + 1, bytes);
                    break;
                default:
                    throw new ArgumentException($"argument {i + 1} is a {args[i]!.GetType().Name}, which a statement cannot bind", nameof(args));
            }
        }

        List<object?[]> rows = [];
        while (statement.Step())
        {
            var row = new object?[statement.ColumnCount];
            for (var column = 0; column < row.Length; column++)
            {
                row[column] = statement.ColumnType(column) switch
                {
                    1 => statement.Int64(column),
                    2 => statement.Double(column),
                    3 => statement.Text(column),
                    4 => statement.Blob(column),
                    _ => null,
                };
            }

            rows.Add(row);
        }

        return rows;
    }
}
