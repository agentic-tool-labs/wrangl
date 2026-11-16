namespace Ah.Abstractions;

/// <summary>How a SQLite file is opened. <see cref="ReadWrite"/> is the default; <see cref="ReadOnly"/> refuses every write; <see cref="Uri"/> reads the path as a URI so an <c>?mode=ro</c> query applies.</summary>
[Flags]
public enum SqliteOpenMode
{
    ReadWrite = 0,
    ReadOnly = 1,
    Uri = 2,

    /// <summary>Creates the file when it is missing; ignored with <see cref="ReadOnly"/>.</summary>
    Create = 4,
}

/// <summary>One prepared statement; not safe for use from two threads.</summary>
public interface ISqliteStatement : IDisposable
{
    void Bind(int index, long value);

    void Bind(int index, string value);

    void Bind(int index, byte[] value);

    void BindNull(int index);

    /// <summary>Runs the statement to its next row: true when a row is ready, false when it is done.</summary>
    bool MoveNext();

    long Number(int column);

    string Text(int column);

    byte[] Blob(int column);

    /// <summary>Makes the statement ready to run again; its bindings are bound anew by the caller.</summary>
    void Reset();
}

/// <summary>One serialised SQLite connection. Callers hold <see cref="Gate"/> while they use it or any statement prepared on it.</summary>
public interface ISqliteConnection : IDisposable
{
    object Gate { get; }

    ISqliteStatement Prepare(string sql);

    /// <summary>Runs <paramref name="sql"/> to its end, discarding any rows.</summary>
    void Exec(string sql);

    /// <summary>The first column of the first row, or null when there is none.</summary>
    string? ScalarText(string sql);
}

/// <summary>Opens SQLite files. Owned by the one project that binds the system library.</summary>
public interface ISqliteOpener
{
    /// <summary>Opens the file at <paramref name="path"/>; throws when the library is missing or the file cannot be opened.</summary>
    ISqliteConnection Open(string path, SqliteOpenMode flags, int busyTimeoutMs);

    /// <summary>Whether <paramref name="failure"/> says the file is not a database or is damaged, so what it held is lost and may be rebuilt.</summary>
    bool IsDamage(Exception failure);

    /// <summary>Whether <paramref name="failure"/> says another connection holds the lock (busy or locked), so a later try may succeed.</summary>
    bool IsBusy(Exception failure);
}
