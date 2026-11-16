namespace Ah.StateDb;

internal sealed class StateDbTx(SqliteConnection connection) : IStateDbTransaction
{
    internal SqliteConnection Connection => connection;

    public void Execute(string sql, params object?[] args) => _ = StateDbRows.Run(connection, sql, args);

    public IReadOnlyList<object?[]> Query(string sql, params object?[] args) => StateDbRows.Run(connection, sql, args);
}

/// <summary>The one place a write id is checked, so the daemon and the direct writer cannot disagree about what "already applied" means.</summary>
internal static class StateDbOnce
{
    /// <summary>
    /// Runs inside an open <c>BEGIN IMMEDIATE</c> transaction. The id is inserted first; a conflict means an earlier transaction applied
    /// it, and then nothing else is written. The deadline is read after the lock is held, which is the start of the transaction.
    /// </summary>
    internal static OnceOutcome Apply(IStateDbTransaction tx, string writeId, string path, DateTimeOffset? deadlineAt, Func<DateTimeOffset> clock, Func<IStateDbTransaction, string> work)
    {
        var now = clock();
        if (deadlineAt is { } deadline && now > deadline)
        {
            return new OnceOutcome(OnceStatus.Expired, null, null, "the request was received after its deadline", null);
        }

        tx.Execute("INSERT OR IGNORE INTO applied_writes(write_id, applied_at, path, result) VALUES (?1, ?2, ?3, NULL)", writeId, now.ToUnixTimeMilliseconds(), path);
        if ((long)tx.Query("SELECT changes()")[0][0]! == 0)
        {
            var stored = tx.Query("SELECT path, result FROM applied_writes WHERE write_id = ?1", writeId)[0];
            return new OnceOutcome(OnceStatus.Already, stored[1] as string, stored[0] as string, string.Empty, null);
        }

        var result = work(tx);
        tx.Execute("UPDATE applied_writes SET result = ?1 WHERE write_id = ?2", result, writeId);
        return new OnceOutcome(OnceStatus.Applied, result, path, string.Empty, null);
    }
}
