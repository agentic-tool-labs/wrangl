using System.Runtime.InteropServices;
using Ah.Abstractions;

namespace Ah.StateDb;

/// <summary>A SQLite call that returned an error code.</summary>
/// <param name="code">The result code.</param>
/// <param name="message">The library's message.</param>
internal sealed class SqliteException(int code, string message) : Exception(message)
{
    internal int Code { get; } = code;

    /// <summary>Whether the file is not a database or is damaged, so what it held is lost and may be rebuilt.</summary>
    internal bool IsDamage => (Code & 0xFF) is Sqlite.Corrupt or Sqlite.NotADatabase;

    /// <summary>Whether another connection held the lock past the busy timeout.</summary>
    internal bool IsBusy => (Code & 0xFF) is Sqlite.Busy or Sqlite.Locked;
}

/// <summary>One prepared statement; not safe for use from two threads.</summary>
internal sealed class SqliteStatement : ISqliteStatement
{
    private readonly SqliteConnection _owner;
    private readonly string? _cacheKey;
    private nint _handle;

    internal SqliteStatement(SqliteConnection owner, nint handle, string? cacheKey = null)
    {
        _owner = owner;
        _handle = handle;
        _cacheKey = cacheKey;
    }

    public void Bind(int index, long value) => _owner.Check(Sqlite.BindInt64(_handle, index, value));

    public void Bind(int index, string value) => _owner.Check(Sqlite.BindText(_handle, index, value, -1, Sqlite.Transient));

    public void Bind(int index, byte[] value) =>
        _owner.Check(value.Length == 0 ? Sqlite.BindZeroBlob(_handle, index, 0) : Sqlite.BindBlob(_handle, index, value, value.Length, Sqlite.Transient));

    /// <summary>Runs the statement to its next row.</summary>
    /// <returns>True when a row is ready; false when the statement is done.</returns>
    public bool Step()
    {
        var rc = Sqlite.Step(_handle);
        if (rc == Sqlite.Row)
        {
            return true;
        }

        if (rc == Sqlite.Done)
        {
            return false;
        }

        throw _owner.Error(rc);
    }

    public long Int64(int column) => Sqlite.ColumnInt64(_handle, column);

    public string Text(int column) => Marshal.PtrToStringUTF8(Sqlite.ColumnText(_handle, column)) ?? string.Empty;

    /// <summary>How many bytes the column's text or blob holds.</summary>
    internal int ByteLength(int column)
    {
        _ = Sqlite.ColumnText(_handle, column);
        return Sqlite.ColumnBytes(_handle, column);
    }

    /// <summary>Copies the column's UTF-8 text into <paramref name="into"/> (at least <see cref="ByteLength"/> long) and returns its length: no string is made.</summary>
    internal int CopyText(int column, byte[] into)
    {
        var text = Sqlite.ColumnText(_handle, column);
        var length = Sqlite.ColumnBytes(_handle, column);
        if (length > 0)
        {
            Marshal.Copy(text, into, 0, length);
        }

        return length;
    }

    public byte[] Blob(int column)
    {
        var length = Sqlite.ColumnBytes(_handle, column);
        var bytes = new byte[length];
        if (length > 0)
        {
            Marshal.Copy(Sqlite.ColumnBlob(_handle, column), bytes, 0, length);
        }

        return bytes;
    }

    /// <summary>How many columns a row of this statement has.</summary>
    internal int ColumnCount => Sqlite.ColumnCount(_handle);

    internal string ColumnName(int column) => Marshal.PtrToStringUTF8(Sqlite.ColumnName(_handle, column)) ?? string.Empty;

    /// <summary>The storage class of the value in <paramref name="column"/> of the current row: 1 integer, 2 float, 3 text, 4 blob, 5 null.</summary>
    internal int ColumnType(int column) => Sqlite.ColumnType(_handle, column);

    internal double Double(int column) => Sqlite.ColumnDouble(_handle, column);

    /// <summary>Makes the statement ready to run again; its bindings are bound anew by the caller.</summary>
    public void Reset() => _ = Sqlite.Reset(_handle);

    public void BindNull(int index) => _owner.Check(Sqlite.BindNull(_handle, index));

    bool ISqliteStatement.MoveNext() => Step();

    long ISqliteStatement.Number(int column) => Int64(column);

    public void Dispose()
    {
        if (_handle != 0)
        {
            if (_cacheKey is null || !_owner.KeepStatement(_cacheKey, _handle))
            {
                _ = Sqlite.Finalize(_handle);
            }

            _handle = 0;
        }
    }
}

/// <summary>One serialised SQLite connection. Callers hold <see cref="Gate"/> while they use it or any statement prepared on it.</summary>
internal sealed class SqliteConnection : ISqliteConnection
{
    private nint _db;

    private SqliteConnection(nint db) => _db = db;

    public object Gate { get; } = new();

    internal static SqliteConnection Open(string path, bool create, int busyTimeoutMs) =>
        Open(path, create ? SqliteOpenMode.Create : SqliteOpenMode.ReadWrite, busyTimeoutMs);

    internal static SqliteConnection Open(string path, SqliteOpenMode openFlags, int busyTimeoutMs)
    {
        var readOnly = openFlags.HasFlag(SqliteOpenMode.ReadOnly);
        if (readOnly && !openFlags.HasFlag(SqliteOpenMode.Uri) && WalFilesGone(path))
        {
            // Another client's clean last close removes -wal and -shm, and then a read-only connection cannot read the database
            // ("unable to open database file"). A read-write one brings them back; query_only keeps it a reader.
            var reader = Open(path, SqliteOpenMode.ReadWrite, busyTimeoutMs);
            try
            {
                reader.Exec("PRAGMA query_only=ON");
                return reader;
            }
            catch (SqliteException)
            {
                reader.Dispose();
                throw;
            }
        }

        var flags = Sqlite.OpenFullMutex
            | (readOnly ? Sqlite.OpenReadOnly : Sqlite.OpenReadWrite)
            | (!readOnly && openFlags.HasFlag(SqliteOpenMode.Create) ? Sqlite.OpenCreate : 0)
            | (openFlags.HasFlag(SqliteOpenMode.Uri) ? Sqlite.OpenUri : 0);
        var rc = Sqlite.OpenV2(path, out var db, flags, 0);
        var connection = new SqliteConnection(db);
        if (rc != Sqlite.Ok)
        {
            var error = connection.Error(rc);
            connection.Dispose();
            throw error;
        }

        _ = Sqlite.BusyTimeout(db, busyTimeoutMs);
        return connection;
    }

    /// <summary>Whether <paramref name="path"/> is a WAL-mode database (bytes 18 and 19 of its header are 2) with no <c>-wal</c> file beside it. A file that cannot be read answers false, and the open reports it.</summary>
    private static bool WalFilesGone(string path)
    {
        if (File.Exists(path + "-wal"))
        {
            return false;
        }

        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            Span<byte> header = stackalloc byte[20];
            return file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && header[18] == 2 && header[19] == 2;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Rows the last statement on this connection inserted, updated or deleted.</summary>
    internal int Changes => Sqlite.Changes(_db);

    /// <summary>From here on this connection may write only what a liveness hook writes; anything else fails when the statement is prepared.</summary>
    internal void RestrictToLiveness() => Check(Sqlite.SetAuthorizer(_db, LivenessAuthorizer.Callback, 0));

    internal SqliteException Error(int rc)
    {
        var extended = _db == 0 ? rc : Sqlite.ExtendedErrCode(_db);
        var message = _db == 0 ? "no connection" : Marshal.PtrToStringUTF8(Sqlite.ErrMsg(_db)) ?? "unknown";
        return new SqliteException(extended == 0 ? rc : extended, message);
    }

    internal void Check(int rc)
    {
        if (rc != Sqlite.Ok)
        {
            throw Error(rc);
        }
    }

    // b438: idle prepared statements by SQL text, on a pooled read connection only; a statement in use is out of it, so a nested prepare of the same text gets its own
    // ponytail: unbounded, fine while SQL texts are a fixed set in code; cap it if a caller builds SQL per call
    private Dictionary<string, nint>? _statements;

    /// <summary>How many statements this connection has compiled.</summary>
    internal int PrepareCount { get; private set; }

    /// <summary>From here on a disposed statement is reset and kept for the next prepare of its text, and finalized when the connection closes.</summary>
    internal void CacheStatements() => _statements ??= [];

    internal bool KeepStatement(string sql, nint handle)
    {
        if (_statements is null || _db == 0 || !_statements.TryAdd(sql, handle))
        {
            return false;
        }

        _ = Sqlite.Reset(handle);
        _ = Sqlite.ClearBindings(handle);
        return true;
    }

    public SqliteStatement Prepare(string sql)
    {
        if (_statements is not null && _statements.Remove(sql, out var cached))
        {
            return new SqliteStatement(this, cached, sql);
        }

        Check(Sqlite.PrepareV2(_db, sql, -1, out var handle, 0));
        PrepareCount++;
        return new SqliteStatement(this, handle, _statements is null || handle == 0 ? null : sql);
    }

    ISqliteStatement ISqliteConnection.Prepare(string sql) => Prepare(sql);

    /// <summary>Runs <paramref name="sql"/> to its end, discarding any rows.</summary>
    public void Exec(string sql)
    {
        using var statement = Prepare(sql);
        while (statement.Step())
        {
        }
    }

    /// <summary>The first column of the first row, or null when there is none.</summary>
    public string? ScalarText(string sql)
    {
        using var statement = Prepare(sql);
        return statement.Step() ? statement.Text(0) : null;
    }

    public void Dispose()
    {
        if (_db != 0)
        {
            foreach (var handle in _statements?.Values ?? Enumerable.Empty<nint>())
            {
                _ = Sqlite.Finalize(handle);
            }

            _statements?.Clear();
            _ = Sqlite.CloseV2(_db);
            _db = 0;
        }
    }
}
