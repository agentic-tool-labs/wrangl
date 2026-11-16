using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// Runs one message write in one <c>BEGIN IMMEDIATE</c> transaction: the guard and the domain's mode are read in it, a write id already
/// applied answers from its stored result, then the body runs, the applied-write row is inserted and the domain's change counter is
/// bumped, all before the single COMMIT. A refusal, a failed body or an exception leaves nothing behind.
/// </summary>
internal sealed class StateDbOpRunner(IStateDbDirect direct, string? ahHome = null) : IDbOpRunner
{
    internal const int ResultMaxBytes = 256;
    private const string SelectApplied = "SELECT result FROM applied_writes WHERE write_id = ?1";
    private const string InsertApplied = "INSERT INTO applied_writes(write_id, applied_at, path, result) VALUES (?1, ?2, ?3, ?4)";

    public string? FilesLockPath(string domain, string stateDirectory, string lockName) =>
        ahHome is null || StateDbAuthority.EnsureForWrite(ahHome) is not null ? null : StateLocks.PathFor(ahHome, domain, stateDirectory, lockName);

    public byte[]? AppliedResult(string modeKey, string stateDirectory, string operationId)
    {
        if (ahHome is null)
        {
            return null;
        }

        var opened = StateDbReader.Open(ahHome);
        if (opened.Reader is not { } reader)
        {
            throw new InvalidOperationException($"the state database cannot be read: {opened.Message}");
        }

        using (reader)
        {
            var rows = reader.Query(SelectApplied, StateLocks.WriteId(modeKey, stateDirectory, operationId));
            return rows.Count == 0 ? null : Encoding.UTF8.GetBytes(rows[0][0] as string ?? "null");
        }
    }

    public bool RecordApplied(string modeKey, string stateDirectory, string operationId, string path, ReadOnlyMemory<byte> result)
    {
        var text = Encoding.UTF8.GetString(result.Span);
        var writeId = StateLocks.WriteId(modeKey, stateDirectory, operationId);
        return direct.Write(tx =>
        {
            tx.Execute(InsertApplied, writeId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), path, text);
            return 0;
        }).Ok;
    }

    /// <summary>Called after the writes and before the COMMIT; tests set it to throw so a crash between the two can be proved to leave nothing.</summary>
    internal Action? BeforeCommit { get; set; }

    private sealed class Abort(DbOpOutcome outcome) : Exception
    {
        internal DbOpOutcome Outcome => outcome;
    }

    private sealed class Adapter(IStateDbTransaction tx) : IDbOpTransaction
    {
        public void Execute(string sql, params object?[] args) => tx.Execute(sql, args);

        public IReadOnlyList<object?[]> Query(string sql, params object?[] args) => tx.Query(sql, args);
    }

    public DbOpOutcome Run(DbOpRequest request)
    {
        if (ahHome is not null && StateDbAuthority.EnsureForWrite(ahHome) is { } why)
        {
            return new DbOpOutcome(DbOpStatus.Refused, default, why);
        }

        DbOpOutcome? outcome = null;
        var armed = false;
        DirectWriteResult<int> written;
        try
        {
            written = direct.Write(tx =>
            {
                outcome = Apply(tx, request, out armed);
                return 0;
            });
        }
        catch (Abort aborted)
        {
            return aborted.Outcome;
        }

        if (written.Ok && armed && ahHome is not null)
        {
            // the marker names the database, so one lost after this write reads as store-missing, never as no messages
            StateDbAuthority.SettleAfter(ahHome);
        }

        return written.Ok ? outcome! : new DbOpOutcome(DbOpStatus.Refused, default, written.Message);
    }

    private DbOpOutcome Apply(IStateDbTransaction tx, DbOpRequest request, out bool armed)
    {
        armed = false;
        if (StateDbMessageMode.WriteRefusal(tx) is { } refusal)
        {
            return new DbOpOutcome(DbOpStatus.Refused, default, refusal);
        }

        if (!request.DbOnly && StateDbMessageMode.Read(tx, request.Domain) != StateDbMessageMode.Db)
        {
            return new DbOpOutcome(DbOpStatus.Redirect, default);
        }

        var writeId = string.IsNullOrEmpty(request.OperationId) ? null : StateLocks.WriteId(request.Domain, request.StateDirectory, request.OperationId);
        if (writeId is not null && tx.Query(SelectApplied, writeId) is { Count: > 0 } stored)
        {
            return new DbOpOutcome(DbOpStatus.Done, Encoding.UTF8.GetBytes(stored[0][0] as string ?? "null"));
        }

        var result = request.Body(new Adapter(tx));
        if (!result.Ok)
        {
            throw new Abort(new DbOpOutcome(DbOpStatus.Failed, default, result.Error ?? $"{request.Domain} write failed", result.ErrorCode));
        }

        byte[] canonical;
        try
        {
            canonical = result.ResultJson.IsEmpty ? "null"u8.ToArray() : JsonText.ToUtf8(JsonText.Parse(result.ResultJson.Span), 0);
        }
        catch (JsonParseException)
        {
            throw new Abort(new DbOpOutcome(DbOpStatus.Failed, default, $"{request.Domain} write returned a result that is not JSON"));
        }

        if (canonical.Length > ResultMaxBytes)
        {
            throw new Abort(new DbOpOutcome(DbOpStatus.Failed, default, $"{request.Domain} write returned {canonical.Length} bytes; a write operation's result is at most {ResultMaxBytes}", "result_too_large"));
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (writeId is not null)
        {
            tx.Execute(InsertApplied, writeId, now, request.Path, Encoding.UTF8.GetString(canonical));
        }

        if (!request.LeavesSeq)
        {
            tx.Execute(
                "INSERT INTO domain_seq(domain, seq, changed_ms) VALUES (?1, 1, ?2) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1, changed_ms = ?2",
                request.Domain,
                now);
        }

        // the first db-default write arms the domain's authority, as a cutover would (review-d2 F1)
        if (request.Domain == "msgs" && tx.Query("SELECT 1 FROM meta WHERE key = ?1", "authority.msgs").Count == 0)
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('authority.msgs', 'db')");
            armed = true;
        }

        BeforeCommit?.Invoke();
        return new DbOpOutcome(DbOpStatus.Done, canonical);
    }
}

/// <summary>
/// Reads a message domain's mode row from a read-only connection, so a files-mode writer holding its lock can confirm it is still files.
/// A domain that is not a message domain means files; a missing database or no mode row means <see cref="StateDbMessageMode.DefaultFor"/>; a database that cannot be opened or read, a newer
/// schema, or a mode row this build does not know throws.
/// </summary>
internal sealed class StateDbModeReader(string ahHome, StateDbReadPool? pool = null) : IMessageModeReader
{
    public string Read(string domain)
    {
        if (!StateDbMessageMode.KnownDomains.Contains(domain, StringComparer.Ordinal))
        {
            return StateDbMessageMode.Files;
        }

        if (!File.Exists(StateDbBackups.DatabasePath(ahHome)))
        {
            return StateDbMessageMode.DefaultFor(domain);
        }

        var opened = StateDbReader.Open(ahHome, pool);
        if (opened.Reader is not { } reader)
        {
            throw new InvalidOperationException($"the state database cannot be read: {opened.Message}");
        }

        using (reader)
        {
            try
            {
                if (StateDbMessageMode.WriteRefusal((sql, args) => reader.Query(sql, args)) is { } refusal)
                {
                    throw new InvalidOperationException(refusal);
                }

                var rows = reader.Query("SELECT value FROM meta WHERE key = ?1", StateDbMessageMode.KeyPrefix + domain);
                return rows.Count == 0 ? StateDbMessageMode.DefaultFor(domain) : (string)rows[0][0]!;
            }
            catch (SqliteException error)
            {
                throw new InvalidOperationException($"the state database cannot be read: {error.Message}", error);
            }
        }
    }
}
