using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// Marks a session alive. It waits at most the busy budget for the write lock; when the database cannot take the mark in time it
/// appends one line to <c>state/fallback.jsonl</c> and returns, so a hook is never held by a writer.
/// </summary>
internal sealed class StateDbLiveness(string ahHome, int busyMs) : ILivenessWriter
{
    internal const int HookBudgetMs = 100;

    public LivenessOutcome Touch(string sessionId, DateTimeOffset at)
    {
        var millis = at.ToUnixTimeMilliseconds();
        if (TryWrite(sessionId, millis))
        {
            return LivenessOutcome.Written;
        }

        return StateDbFallback.Append(ahHome, JsonText.ToUtf8(new JsonObject { ["kind"] = "liveness", ["session"] = sessionId, ["at"] = millis }, 0))
            ? LivenessOutcome.Deferred
            : LivenessOutcome.Lost;
    }

    private StoreVerdictCache? _cache;

    private bool TryWrite(string sessionId, long millis)
    {
        var path = StateDbBackups.DatabasePath(ahHome);
        if (!File.Exists(path))
        {
            return false;
        }

        if ((_cache ??= new StoreVerdictCache(ahHome)).Get().Problem != StoreProblem.None)
        {
            return false;
        }

        try
        {
            using var connection = SqliteConnection.Open(path, SqliteOpenMode.ReadWrite, busyMs);
            connection.Exec("PRAGMA synchronous=NORMAL");
            connection.Exec("PRAGMA wal_autocheckpoint=0");
            connection.RestrictToLiveness();
            using var statement = connection.Prepare("UPDATE sessions SET last_seen_at = ?1 WHERE session_id = ?2");
            statement.Bind(1, millis);
            statement.Bind(2, sessionId);
            _ = statement.Step();
            return connection.Changes > 0;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}

/// <summary>
/// The file the writers of last resort append to when the database cannot take a write in time. Each line is one JSON object; the
/// daemon applies them when it opens the database and moves the file to <c>state/replaced</c>.
/// </summary>
internal static class StateDbFallback
{
    internal static string FilePath(string ahHome) => Path.Join(ahHome, "state", "fallback.jsonl");

    /// <summary>Appends one line in a single write to a file opened for append, so concurrent writers do not interleave.</summary>
    internal static bool Append(string ahHome, byte[] json)
    {
        try
        {
            var directory = Path.Join(ahHome, "state");
            if (!Directory.Exists(directory))
            {
                return false;
            }

            using var stream = new FileStream(FilePath(ahHome), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            stream.Write([.. json, (byte)'\n']);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly TimeSpan KeepUnknownFor = TimeSpan.FromHours(24);

    private static readonly TimeSpan KeepArchiveFor = TimeSpan.FromDays(7);

    private const int MaxRounds = 4;

    internal static string HoldPath(string ahHome) => Path.Join(ahHome, "state", "fallback.hold");

    /// <summary>
    /// Applies the liveness lines to <c>sessions.last_seen_at</c> and moves each file it has read to <c>state/replaced</c>. Returns how many
    /// rows it updated. A leftover <c>.importing</c> file from an earlier run is applied first. When the database is busy the file stays
    /// where it is and the next call tries again. A line for a session the database does not know yet goes to <c>fallback.hold</c>, one
    /// line per session, and is tried again on every call until it applies or is a day old; nothing is archived for it. A line for a
    /// known session that is not newer than its row is dropped. <paramref name="afterApply"/> runs after each batch commits, which is
    /// where a writer still holding the old file adds its tail.
    /// </summary>
    internal static int Import(SqliteConnection connection, string ahHome, DateTimeOffset now, Action? afterApply = null)
    {
        var file = FilePath(ahHome);
        var taking = file + ".importing";
        var applied = 0;
        var stopped = false;
        try
        {
            if (File.Exists(taking))
            {
                var done = ImportTaken(connection, ahHome, taking, now, afterApply);
                stopped = done is null;
                applied += done ?? 0;
            }

            if (!stopped && File.Exists(file) && !File.Exists(taking))
            {
                File.Move(file, taking, overwrite: false);
                var done = ImportTaken(connection, ahHome, taking, now, afterApply);
                stopped = done is null;
                applied += done ?? 0;
            }
        }
        catch (IOException)
        {
            stopped = true;
        }

        if (!stopped)
        {
            applied += RetryHold(connection, ahHome, now);
        }

        PruneArchive(ahHome, now);
        return applied;
    }

    /// <summary>The rows updated from the taken file, or null when the database was busy (or the hold could not be written) and the file was left for the next try.</summary>
    private static int? ImportTaken(SqliteConnection connection, string ahHome, string taking, DateTimeOffset now, Action? afterApply)
    {
        var applied = 0;
        List<string> kept = [];
        var seen = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(taking);
            }
            catch (IOException)
            {
                return null;
            }

            if (lines.Length <= seen)
            {
                break;
            }

            var batch = ApplyBatch(connection, lines[seen..], now);
            if (batch is null)
            {
                return null;
            }

            applied += batch.Applied;
            kept.AddRange(batch.Kept);
            seen = lines.Length;
            afterApply?.Invoke();
        }

        try
        {
            if (kept.Count > 0)
            {
                WriteHold(ahHome, Compact([.. ReadHold(ahHome), .. kept], now));
            }

            var replaced = StateDbBackups.ReplacedDirectory(ahHome);
            Directory.CreateDirectory(replaced);
            var stem = Path.Join(replaced, "fallback-" + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
            var target = stem + ".jsonl";
            for (var n = 1; File.Exists(target); n++)
            {
                target = $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.jsonl";
            }

            File.Move(taking, target, overwrite: false);
        }
        catch (IOException)
        {
            return null;
        }

        return applied;
    }

    private static string[] ReadHold(string ahHome)
    {
        try
        {
            return File.Exists(HoldPath(ahHome)) ? File.ReadAllLines(HoldPath(ahHome)) : [];
        }
        catch (IOException)
        {
            return [];
        }
    }

    /// <summary>The newest mark per session, without marks older than the hold lasts.</summary>
    private static List<string> Compact(IEnumerable<string> lines, DateTimeOffset now)
    {
        var oldest = now.ToUnixTimeMilliseconds() - (long)KeepUnknownFor.TotalMilliseconds;
        Dictionary<string, (long At, string Line)> newest = new(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (Parse(line) is { } mark && mark.At >= oldest && (!newest.TryGetValue(mark.Session, out var held) || held.At < mark.At))
            {
                newest[mark.Session] = (mark.At, line);
            }
        }

        return [.. newest.Values.OrderBy(v => v.At).Select(v => v.Line)];
    }

    private static void WriteHold(string ahHome, List<string> lines)
    {
        var path = HoldPath(ahHome);
        if (lines.Count == 0)
        {
            File.Delete(path);
            return;
        }

        var temporary = path + ".tmp";
        File.WriteAllText(temporary, string.Concat(lines.Select(l => l + "\n")));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Tries the held lines again: the ones whose session is known now apply, the rest stay; returns the rows updated.</summary>
    private static int RetryHold(SqliteConnection connection, string ahHome, DateTimeOffset now)
    {
        var held = ReadHold(ahHome);
        if (held.Length == 0)
        {
            return 0;
        }

        var batch = ApplyBatch(connection, held, now);
        if (batch is null)
        {
            return 0;
        }

        try
        {
            var remaining = Compact(batch.Kept, now);
            if (remaining.Count != held.Length)
            {
                WriteHold(ahHome, remaining);
            }
        }
        catch (IOException)
        {
            // the old hold stays; applying a line twice changes nothing
        }

        return batch.Applied;
    }

    /// <summary>Removes archived fallback files that are a week old; nothing else in <c>state/replaced</c> is touched.</summary>
    private static void PruneArchive(string ahHome, DateTimeOffset now)
    {
        var replaced = StateDbBackups.ReplacedDirectory(ahHome);
        try
        {
            if (!Directory.Exists(replaced))
            {
                return;
            }

            foreach (var old in Directory.GetFiles(replaced, "fallback-*.jsonl").Where(f => File.GetLastWriteTimeUtc(f) < now.UtcDateTime - KeepArchiveFor))
            {
                File.Delete(old);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the archive is kept a little longer
        }
    }

    private sealed record Batch(int Applied, List<string> Kept);

    private static Batch? ApplyBatch(SqliteConnection connection, string[] lines, DateTimeOffset now)
    {
        var applied = 0;
        List<string> kept = [];
        var oldest = now.ToUnixTimeMilliseconds() - (long)KeepUnknownFor.TotalMilliseconds;
        try
        {
            connection.Exec("BEGIN IMMEDIATE");
            foreach (var line in lines)
            {
                if (Parse(line) is not { } mark)
                {
                    continue;
                }

                using var update = connection.Prepare("UPDATE sessions SET last_seen_at = ?1 WHERE session_id = ?2 AND (last_seen_at IS NULL OR last_seen_at < ?1)");
                update.Bind(1, mark.At);
                update.Bind(2, mark.Session);
                _ = update.Step();
                if (connection.Changes > 0)
                {
                    applied++;
                    continue;
                }

                using var known = connection.Prepare("SELECT 1 FROM sessions WHERE session_id = ?1");
                known.Bind(1, mark.Session);
                if (!known.Step() && mark.At >= oldest)
                {
                    kept.Add(line);
                }
            }

            connection.Exec("COMMIT");
        }
        catch (SqliteException)
        {
            try
            {
                connection.Exec("ROLLBACK");
            }
            catch (SqliteException)
            {
                // the transaction is already gone
            }

            return null;
        }

        return new Batch(applied, kept);
    }

    private static (string Session, long At)? Parse(string line)
    {
        try
        {
            var record = JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(line));
            return record.Member("kind") is { IsText: true } kind && kind.AsString() == "liveness"
                && record.Member("session") is { IsText: true } session && record.Member("at") is { IsNumber: true } at
                ? (session.AsString(), (long)at.AsNumber())
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonParseException)
        {
            return null;
        }
    }
}
