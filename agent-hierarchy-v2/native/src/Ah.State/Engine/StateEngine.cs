using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Digests;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.State;

/// <summary>
/// The native single writer: one write op, run under its domain's lock, once per <c>op_id</c>. The steps are the Node
/// writer's, in order: preflight, the state directory, the lock, the dedup lookup, the digest precondition, the op, the
/// result bound, the log row. Reads never come here.
/// <para>
/// With a database runner and a mode reader, an op that has a database body runs in one database transaction while its domain is
/// in <c>db</c> mode, with no file lock and no executed-op row; the files path confirms the mode under its lock and hands the op to
/// the database when the domain has been flipped since the caller chose files. Without them the engine is the files engine alone.
/// </para>
/// </summary>
internal sealed class StateEngine(
    IReadOnlyList<IStateDomain> domains,
    IFileReader files,
    IRawFileWriter writer,
    IRecordAppender appender,
    ILockProvider locks,
    IClock clock,
    int processId,
    IDbOpRunner? dbRunner = null,
    IMessageModeReader? modes = null,
    string dbPath = "direct")
{
    internal const string DbMode = "db";
    internal const string FilesMode = "files";

    internal const string StoreNotImportedCode = "store_not_imported";
    internal const string ImportingMode = "importing";
    internal const int ResultMaxBytes = 256;
    internal const string DefaultLockName = ".domain.lock";
    internal const string InternalCode = "internal";

    private readonly ExecutedOps _log = new(files, writer, appender, processId);

    /// <summary>Every registered <c>domain.op</c>, the keys the op ledger counts.</summary>
    private readonly HashSet<string> _registered = new(domains.SelectMany(static d => d.Ops.Select(o => d.Name + "." + o.Name)), StringComparer.Ordinal);
    private readonly StoreWriteContext _context = new(writer, appender);

    /// <summary>The key a state domain has in the mode rows, the applied-write ids and the change counters: the mailbox domain is <c>mail</c>.</summary>
    internal static string ModeKey(string domain) => domain == "mailbox" ? "mail" : domain;

    private static string ModeKey(string domain, StateOp spec) => spec.ModeKey ?? ModeKey(domain);

    /// <summary>The delegate <c>StateModule.CreateClient</c> takes as its in-process engine.</summary>
    internal StateResult Run(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, string operationId, TimeSpan lockWait)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return RunCore(domain, op, argsJson, context, operationId, lockWait);
        }
        finally
        {
            var key = domain + "." + op;
            if (_registered.Contains(key))
            {
                StateOpLedger.Add(key, System.Diagnostics.Stopwatch.GetTimestamp() - started);
            }
        }
    }

    private StateResult RunCore(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, string operationId, TimeSpan lockWait)
    {
        var owner = domains.FirstOrDefault(d => d.Name == domain);
        var spec = owner?.Ops.FirstOrDefault(o => o.Name == op);
        if (owner is null || spec is null || spec.Kind != OpKind.Write)
        {
            return Fail("bad_request", $"unknown state operation {domain}.{op}");
        }

        JsonValue args;
        try
        {
            args = argsJson.IsEmpty ? new JsonObject() : JsonText.Parse(argsJson.Span);
        }
        catch (JsonParseException)
        {
            return Fail("bad_request", $"{domain}.{op}: args are not JSON");
        }

        if (StateRules.Preflight(domain, op, spec, args) is { } refused)
        {
            return refused;
        }

        if (spec.LockName is { } lockName && (lockName.Length == 0 || lockName == ".." || lockName == "." || lockName.Contains('/', StringComparison.Ordinal) || lockName.Contains('\\', StringComparison.Ordinal)))
        {
            return Fail(InternalCode, $"{domain}.{op} names a lock that is not a file name in the state directory");
        }

        if (owner.DbOnly)
        {
            return dbRunner is null || (spec.DbBody is null && spec.DbBodyWithFiles is null)
                ? Fail("op_failed", $"{domain}.{op} is db-only and this process has no database for it")
                : RunInDatabase(domain, spec, argsJson, context, operationId, dbOnly: true) ?? Fail("op_failed", $"{domain}.{op} is db-only and was redirected to files");
        }

        if (dbRunner is not null && (spec.DbBody is not null || spec.DbBodyWithFiles is not null) && MayBeInDbMode(ModeKey(domain, spec)) && RunInDatabase(domain, spec, argsJson, context, operationId) is { } viaDatabase)
        {
            return viaDatabase;
        }

        var directory = owner.StateDirectory(context, argsJson);
        if (!Path.IsPathRooted(directory))
        {
            // a relative state directory would be created, locked and logged in the process's working directory
            return Fail("op_failed", $"{domain}.{op}: the state directory is not an absolute path");
        }

        // with a database the lock lives under the home and replay in applied_writes; the state directory is only the lock's identity (§13)
        var lockFile = spec.LockName ?? DefaultLockName;
        var homeLock = HomeLock(domain, directory, lockFile);
        writer.Mkdir(homeLock is null ? directory : NodePath.Dirname(homeLock));
        var lockPath = homeLock ?? NodePath.Join(directory, lockFile);
        var replay = new Replay(directory, homeLock is null || string.IsNullOrEmpty(operationId) ? null : ModeKey(domain, spec));
        var call = new OpCall(op, argsJson, context, string.IsNullOrEmpty(operationId) ? null : operationId);
        var held = locks.Acquire(lockPath, lockWait);
        if (held is null)
        {
            return LockTimedOut(domain, spec, call);
        }

        try
        {
            if (modes is not null)
            {
                string mode;
                try
                {
                    mode = modes.Read(ModeKey(domain, spec));
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    return Fail("op_failed", $"{domain}.{op} is refused: {error.Message}");
                }

                if (mode == DbMode)
                {
                    // The caller chose files before the domain was flipped; a file is never written after a flip.
                    held.Dispose();
                    if (dbRunner is null || (spec.DbBody is null && spec.DbBodyWithFiles is null))
                    {
                        return Fail("op_failed", $"{domain}.{op} has no database implementation and {domain} is in db mode");
                    }

                    return RunInDatabase(domain, spec, argsJson, context, operationId)
                        ?? Fail("op_failed", $"{domain} changed mode during {domain}.{op}; retry");
                }

                // files stopped being written (db-only-stores S6): only a home that has not imported holds such a row, and the file bodies are for rigs with no database
                if (mode == FilesMode && dbRunner is not null && ModeKey(domain, spec) is "mail" or "peer-records")
                {
                    return Fail(StoreNotImportedCode, $"{domain}.{op} is refused: this home's {ModeKey(domain, spec)} is still on files, and this build no longer writes them; restore a backup from `ah db list` taken on the database store");
                }

                // during a daemon start's import of message files the op's own body says why it is refused
                if (mode != FilesMode && mode != ImportingMode)
                {
                    return Fail("op_failed", $"{domain}.{op} is refused: {domain} has the unknown mode '{mode}'");
                }
            }

            if (spec.BeforeEveryRun is { } before)
            {
                try
                {
                    before(call, _context);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    return Fail("op_failed", $"{domain}.{op} failed: {error.Message}");
                }
            }

            if (replay.ModeKey is { } modeKey)
            {
                if (StoredResult(modeKey, directory, operationId) is { } stored)
                {
                    return stored.Length > ResultMaxBytes ? Fail("result_too_large", TooLarge(domain, op, stored.Length)) : new StateResult(true, stored);
                }
            }
            else if (!string.IsNullOrEmpty(operationId))
            {
                replay = replay with { Rows = _log.Read(directory) };
                if (replay.Rows.FirstOrDefault(r => r.OpId == operationId) is { } done)
                {
                    return done.IsError
                        ? Fail(done.ErrorCode ?? "op_failed", done.ErrorMessage)
                        : new StateResult(true, JsonText.ToUtf8(done.Result, 0));
                }
            }

            if (spec.DigestPrecondition
                && (spec.FileDigestApplies?.Invoke() ?? true)
                && args.Member("expect") is { IsText: true } expect
                && args.Member("path") is { IsText: true } target
                && ContentDigest.OfFile(files, target.AsString()) != expect.AsString())
            {
                var conflict = new JsonObject();
                conflict["conflict"] = JsonValue.True;
                var conflictValue = JsonValue.FromObject(conflict);
                var conflictBytes = JsonText.ToUtf8(conflictValue, 0);
                Record(replay, operationId, conflictValue, conflictBytes);
                return new StateResult(true, conflictBytes);
            }

            return Execute(replay, operationId, domain, spec, call);
        }
        finally
        {
            held.Dispose();
        }
    }

    /// <summary>
    /// False when the domain is known to be in files mode, so the write goes to the files path without opening a database write
    /// transaction (a home with no database has none to open). A mode that cannot be read is also sent to the files path, which reads
    /// it again under its lock and refuses the write there.
    /// </summary>
    private bool MayBeInDbMode(string modeKey)
    {
        if (modes is null)
        {
            return true;
        }

        try
        {
            return modes.Read(modeKey) == DbMode;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Runs the op's database body in one transaction; null when the domain is in files mode, so the files path runs.</summary>
    /// <summary>The state directory a database write's id is scoped to; null (the old unscoped id) when the domain cannot name an absolute one.</summary>
    private string? ReplayDirectory(string domain, ReadOnlyMemory<byte> argsJson, RequestContext context)
    {
        try
        {
            return domains.FirstOrDefault(d => d.Name == domain)?.StateDirectory(context, argsJson) is { } directory && Path.IsPathRooted(directory) ? directory : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    private StateResult? RunInDatabase(string domain, StateOp spec, ReadOnlyMemory<byte> argsJson, RequestContext context, string operationId, bool dbOnly = false)
    {
        var call = new OpCall(spec.Name, argsJson, context, string.IsNullOrEmpty(operationId) ? null : operationId);
        DbOpOutcome outcome;
        try
        {
            outcome = dbRunner!.Run(new DbOpRequest(ModeKey(domain, spec), call.OperationId, dbPath, tx => spec.DbBodyWithFiles is { } withFiles ? withFiles(call, tx, _context) : spec.DbBody!(call, tx), spec.LeavesSeq, dbOnly, ReplayDirectory(domain, argsJson, context)));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // the transaction was rolled back; an exception is the engine's, as on the files path
            return Fail(InternalCode, error.Message);
        }

        if (outcome.Status == DbOpStatus.Done && spec.DbAfterCommit is { } afterCommit)
        {
            try
            {
                afterCommit(call, _context);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                // the rows are committed; a file left unwritten is written later from them
            }
        }

        return outcome.Status switch
        {
            DbOpStatus.Done => new StateResult(true, outcome.ResultJson.ToArray()),
            DbOpStatus.Redirect => null,
            DbOpStatus.Failed => Fail(outcome.ErrorCode ?? "op_failed", outcome.Error ?? $"{domain}.{spec.Name} failed"),
            _ => Fail("op_failed", outcome.Error ?? $"{domain}.{spec.Name} was refused"),
        };
    }

    /// <summary>Where a files-path op's replay lives: <see cref="ModeKey"/> set means <c>applied_writes</c> (keyed by mode key, directory and op id), else the state directory's executed-op log.</summary>
    private sealed record Replay(string Directory, string? ModeKey)
    {
        internal IReadOnlyList<ExecutedRow> Rows { get; init; } = [];
    }

    /// <summary>The home lock of a files-path op, or null when the engine has no database to use (the state directory's lock, as before).</summary>
    private string? HomeLock(string domain, string directory, string lockName)
    {
        try
        {
            return dbRunner?.FilesLockPath(domain, directory, lockName);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The stored result of an op id in a directory; a read that fails runs the op, as a retry after a lost replay row would.</summary>
    private byte[]? StoredResult(string modeKey, string directory, string operationId)
    {
        try
        {
            return dbRunner!.AppliedResult(modeKey, directory, operationId);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }

    private StateResult Execute(Replay replay, string operationId, string domain, StateOp spec, OpCall call)
    {
        OpResult outcome;
        try
        {
            outcome = spec.Body(call, _context);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // an exception is the engine's, not the op's: the op reports its own failure through OpResult.ErrorCode
            return Failed(replay, operationId, InternalCode, error.Message);
        }

        if (!outcome.Ok)
        {
            return Failed(replay, operationId, outcome.ErrorCode, outcome.Error ?? $"{domain}.{spec.Name} failed");
        }

        JsonValue result;
        try
        {
            result = outcome.ResultJson.IsEmpty ? JsonValue.Null : JsonText.Parse(outcome.ResultJson.Span);
        }
        catch (JsonParseException)
        {
            return Failed(replay, operationId, null, $"{domain}.{spec.Name} returned a result that is not JSON");
        }

        var canonical = JsonText.ToUtf8(result, 0);
        if (canonical.Length > ResultMaxBytes)
        {
            // The write has been done and stands; the call fails, and a retry of the id is answered from its row, never rerun:
            // the executed-op log keeps the error row, applied_writes the oversized result, which a replay answers as this error.
            if (replay.ModeKey is not null)
            {
                Record(replay, operationId, result, canonical);
            }

            return Failed(replay, operationId, "result_too_large", TooLarge(domain, spec.Name, canonical.Length));
        }

        Record(replay, operationId, result, canonical);
        return new StateResult(true, canonical);
    }

    /// <summary>A failure: the executed-op log keeps its error row; <c>applied_writes</c> records nothing, so a retry runs again, as on the database path.</summary>
    private StateResult Failed(Replay replay, string operationId, string? code, string message)
    {
        if (replay.ModeKey is null && !string.IsNullOrEmpty(operationId))
        {
            try
            {
                _log.Record(replay.Directory, replay.Rows, ExecutedOps.ErrorRow(operationId, Now(), code, message));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // the operation's own error is the one to raise
            }
        }

        return Fail(code ?? "op_failed", message);
    }

    private void Record(Replay replay, string operationId, JsonValue result, byte[] canonical)
    {
        if (replay.ModeKey is { } modeKey)
        {
            try
            {
                // a row that cannot be written (busy) costs only the replay; the op's result still stands
                _ = dbRunner!.RecordApplied(modeKey, replay.Directory, operationId, dbPath, canonical);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
            }
        }
        else if (!string.IsNullOrEmpty(operationId))
        {
            _log.Record(replay.Directory, replay.Rows, ExecutedOps.SuccessRow(operationId, Now(), result));
        }
    }

    /// <summary>A lock that never cleared: a <c>lockOptional</c> op checks the stamp and runs unlocked, with no log row; any other op times out.</summary>
    private StateResult LockTimedOut(string domain, StateOp spec, OpCall call)
    {
        if (!spec.LockOptional)
        {
            return Fail("lock_timeout", $"the {domain} state lock did not clear in time");
        }

        try
        {
            var outcome = spec.Body(call with { Locked = false }, _context);
            return outcome.Ok
                ? new StateResult(true, outcome.ResultJson.IsEmpty ? "null"u8.ToArray() : outcome.ResultJson)
                : Fail(outcome.ErrorCode ?? "op_failed", outcome.Error ?? $"{domain}.{spec.Name} failed");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return Fail(InternalCode, error.Message);
        }
    }

    private static string TooLarge(string domain, string op, int length) =>
        $"{domain}.{op} returned {length.ToString(CultureInfo.InvariantCulture)} bytes; a write operation's result is at most {ResultMaxBytes.ToString(CultureInfo.InvariantCulture)}";

    private string Now() => Timestamps.ToIsoString(clock.UtcNow);

    private static StateResult Fail(string code, string message) => StateRules.Fail(code, message);
}
