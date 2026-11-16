namespace Ah.StateDb;

/// <summary>One open write transaction. A domain write runs inside <see cref="IStateDbDirect.Write{T}"/> and must not wait on anything else.</summary>
public interface IStateDbTransaction
{
    /// <summary>Runs one statement; <paramref name="args"/> bind to ?1, ?2, … and may be null, integers, bool, text or bytes.</summary>
    void Execute(string sql, params object?[] args);

    /// <summary>Runs one query; each row holds null, a long, a double, a string or bytes.</summary>
    IReadOnlyList<object?[]> Query(string sql, params object?[] args);
}

/// <summary>What a direct write did. <paramref name="DaemonPid"/> is the daemon recorded as running when the write gave up on a busy database.</summary>
public sealed record DirectWriteResult<T>(bool Ok, T? Value, string Message, int? DaemonPid);

/// <summary>
/// Writes the state database from the calling process when the daemon cannot take the write. It never migrates and never takes a
/// backup, and a write it cannot make fails with the reason instead of being queued anywhere.
/// </summary>
public interface IStateDbDirect
{
    /// <summary>Runs <paramref name="work"/> in one <c>BEGIN IMMEDIATE</c> transaction and commits it; an exception from it rolls back and propagates.</summary>
    DirectWriteResult<T> Write<T>(Func<IStateDbTransaction, T> work);

    /// <summary>
    /// Like <see cref="Write{T}"/>, but the transaction first records <paramref name="writeId"/>. An id that is already recorded
    /// means the write was applied before: nothing changes and the stored result comes back, so the verb prints the same whichever
    /// path applied it. <paramref name="work"/> returns the result text to store.
    /// </summary>
    OnceOutcome WriteOnce(string writeId, Func<IStateDbTransaction, string> work);
}

/// <summary>What a write that carries an id did.</summary>
public enum OnceStatus
{
    /// <summary>This call applied the write and stored its result.</summary>
    Applied,

    /// <summary>The id was already applied, by <see cref="OnceOutcome.By"/>; nothing changed and <see cref="OnceOutcome.Result"/> is what that application returned.</summary>
    Already,

    /// <summary>The daemon started the transaction after the request's deadline and applied nothing.</summary>
    Expired,

    /// <summary>The write could not be made (busy, schema skew, no database); nothing changed and <see cref="OnceOutcome.Message"/> says why.</summary>
    Refused,
}

/// <summary>The outcome of <c>WriteOnce</c>. <paramref name="DaemonPid"/> is the daemon recorded as running when a busy database made the write give up.</summary>
public sealed record OnceOutcome(OnceStatus Status, string? Result, string? By, string Message, int? DaemonPid);

/// <summary>A read-only view of the database; it opens beside any writer and never waits for one.</summary>
public interface IStateDbReader : IDisposable
{
    IReadOnlyList<object?[]> Query(string sql, params object?[] args);
}

/// <summary>The result of opening a reader: <paramref name="Reader"/> is null, with the reason in <paramref name="Message"/>, when none could be opened.</summary>
public sealed record StateDbReaderOpen(IStateDbReader? Reader, string Message);

/// <summary>Where a liveness mark went.</summary>
public enum LivenessOutcome
{
    /// <summary>The session row was updated.</summary>
    Written,

    /// <summary>The database was busy, absent or had no row for the session; a line waits in the fallback file for the daemon to import.</summary>
    Deferred,

    /// <summary>Neither the database nor the fallback file could take it.</summary>
    Lost,
}

/// <summary>The liveness hooks' writer: bounded to about 100 ms, never blocking a hook, never writing a table of record.</summary>
public interface ILivenessWriter
{
    LivenessOutcome Touch(string sessionId, DateTimeOffset at);
}
