using System.Buffers;
using System.Globalization;
using Ah.Core.Json;
using System.Text.RegularExpressions;
using Ah.Abstractions;

namespace Ah.StateDb;

/// <summary>One backup of the state database on disk.</summary>
/// <param name="Name">The file name of the database copy, for example <c>ah-20261021T093355Z-v1-pre-migrate-v2.db</c>.</param>
/// <param name="Path">The full path of the database copy.</param>
/// <param name="TakenAt">When it was taken, to the second, in UTC.</param>
/// <param name="Schema">The schema version the database had.</param>
/// <param name="Label">The reason it was taken (<c>pre-migrate-v2</c>, <c>pre-restore</c>), or null for a scheduled one.</param>
/// <param name="Bytes">The size of the database copy.</param>
/// <param name="HasExport">Whether the logical <c>.jsonl</c> export sits beside it.</param>
public sealed record BackupInfo(string Name, string Path, DateTimeOffset TakenAt, int Schema, string? Label, long Bytes, bool HasExport);

/// <summary>When backups are due and how many are kept.</summary>
/// <param name="Interval">The shortest gap between two scheduled backups.</param>
/// <param name="KeepHourly">How many hourly generations are kept.</param>
/// <param name="KeepDaily">How many daily generations are kept.</param>
/// <param name="KeepWeekly">How many weekly generations are kept.</param>
/// <param name="MaxSnapshots">The most backups that stay on disk, whatever the generations would keep; pinned ones are kept over it.</param>
public sealed record BackupSettings(TimeSpan Interval, int KeepHourly, int KeepDaily, int KeepWeekly, int MaxSnapshots)
{
    /// <summary>An hour between backups, 24 hourly, 7 daily and 4 weekly generations, at most 10 on disk.</summary>
    public static BackupSettings Default { get; } = new(TimeSpan.FromMinutes(60), 24, 7, 4, 10);
}

/// <summary>What <see cref="StateDbBackups.Restore"/> did or would do.</summary>
/// <param name="Ok">False when the restore was refused; <paramref name="Message"/> says why.</param>
/// <param name="Applied">True when the files were replaced; false for a dry run or a refusal.</param>
/// <param name="Message">One sentence for the user.</param>
/// <param name="Source">The backup restored from, when one was chosen.</param>
/// <param name="Steps">What is done, in order; for a dry run, what would be done.</param>
public sealed record RestoreResult(bool Ok, bool Applied, string Message, BackupInfo? Source, IReadOnlyList<string> Steps);

/// <summary>The state of the database and its backups as the doctor row reports it.</summary>
/// <param name="Level"><c>ok</c>, <c>warn</c> or <c>error</c>.</param>
/// <param name="Detail">The row's text.</param>
public sealed record StateDbHealth(string Level, string Detail);

/// <summary>
/// Backups of the state database: <c>VACUUM INTO</c> copies under <c>&lt;ah home&gt;/state/backups</c>, each with a logical
/// <c>.jsonl</c> export beside it, generational retention under a hard cap, and a restore that is a dry run unless applied.
/// A file copy would miss what the write-ahead log still holds, so no backup is ever made by copying <c>ah.db</c>.
/// </summary>
internal static partial class StateDbBackups
{
    /// <summary>The label of the backup taken before a restore replaces the database.</summary>
    internal const string PreRestoreLabel = "pre-restore";

    /// <summary>The label of the backup taken before <c>ah resume</c> acts.</summary>
    internal const string PreResumeLabel = "pre-resume";

    /// <summary>The label prefix of the backup taken before a migration (<c>pre-migrate-v&lt;target&gt;</c>).</summary>
    internal const string PreMigratePrefix = "pre-migrate-";

    private const string TimeFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const string PartialSuffix = ".partial";
    private static readonly string[] JournalSuffixes = ["-wal", "-shm"];

    /// <summary>The directory of the backups of <paramref name="ahHome"/>.</summary>
    internal static string BackupDirectory(string ahHome) => Path.Join(ahHome, "state", "backups");

    /// <summary>The directory that keeps damaged databases, which are never deleted.</summary>
    internal static string DamagedDirectory(string ahHome) => Path.Join(ahHome, "state", "damaged");

    internal static string ReplacedDirectory(string ahHome) => Path.Join(ahHome, "state", "replaced");

    internal static string DatabasePath(string ahHome) => Path.Join(ahHome, "state", "ah.db");

    [GeneratedRegex(@"^ah-(?<t>[0-9]{8}T[0-9]{6}Z)-v(?<v>[0-9]+)(?:-(?<l>[a-z0-9._-]+))?\.db\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    /// <summary>Backs up the database of <paramref name="ahHome"/> through its own read-only connection.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="now">The time the backup is named for.</param>
    /// <param name="label">Why it is taken; null for a scheduled backup.</param>
    /// <returns>The backup.</returns>
    internal static BackupInfo Take(string ahHome, DateTimeOffset now, string? label = null)
    {
        Sqlite.InstallResolver();
        using var connection = SqliteConnection.Open(DatabasePath(ahHome), SqliteOpenMode.ReadOnly, 2000);
        return Take(connection, BackupDirectory(ahHome), now, label);
    }

    /// <summary>
    /// Takes a backup labelled <paramref name="label"/> and then removes the older ones with that label, so the label never holds more
    /// than one copy: a repeated job cannot fill the snapshot cap and push out the backups taken for a migration or a cutover.
    /// </summary>
    internal static BackupInfo TakeReplacing(string ahHome, DateTimeOffset now, string label)
    {
        var older = List(ahHome).Where(b => b.Label == label).ToList();
        var taken = Take(ahHome, now, label);
        foreach (var backup in older.Where(b => b.Name != taken.Name))
        {
            try
            {
                File.Delete(backup.Path);
                File.Delete(Path.ChangeExtension(backup.Path, ".jsonl"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // an older copy that cannot be removed now is removed by the next run, which lists it again
            }
        }

        return taken;
    }

    internal static BackupInfo Take(SqliteConnection connection, string backupDirectory, DateTimeOffset now, string? label)
    {
        Directory.CreateDirectory(backupDirectory);
        var schema = StateDbFile.ReadVersion(connection) ?? 0;
        var taken = Truncate(now);
        var suffix = Suffix(label);
        string name;
        while (File.Exists(Path.Join(backupDirectory, name = $"ah-{taken.ToString(TimeFormat, CultureInfo.InvariantCulture)}-v{schema}{suffix}.db")))
        {
            taken = taken.AddSeconds(1);
        }

        var final = Path.Join(backupDirectory, name);
        var partial = final + PartialSuffix;
        File.Delete(partial);
        try
        {
            connection.Exec("VACUUM INTO '" + partial.Replace("'", "''", StringComparison.Ordinal) + "'");
            File.Move(partial, final);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        var export = Path.ChangeExtension(final, ".jsonl");
        try
        {
            WriteExport(final, export);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            File.Delete(export + PartialSuffix);
        }

        return new BackupInfo(name, final, taken, schema, suffix.Length > 0 ? suffix[1..] : null, new FileInfo(final).Length, File.Exists(export));
    }

    /// <summary>The backups on disk, newest first.</summary>
    internal static IReadOnlyList<BackupInfo> List(string ahHome)
    {
        var directory = BackupDirectory(ahHome);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        List<BackupInfo> found = [];
        foreach (var file in Directory.EnumerateFiles(directory, "ah-*.db"))
        {
            var match = NamePattern().Match(Path.GetFileName(file));
            if (!match.Success
                || !DateTimeOffset.TryParseExact(match.Groups["t"].Value, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            {
                continue;
            }

            found.Add(new BackupInfo(
                Path.GetFileName(file),
                file,
                at,
                int.Parse(match.Groups["v"].Value, CultureInfo.InvariantCulture),
                match.Groups["l"].Success ? match.Groups["l"].Value : null,
                new FileInfo(file).Length,
                File.Exists(Path.ChangeExtension(file, ".jsonl"))));
        }

        return [.. found.OrderByDescending(b => b.TakenAt).ThenByDescending(b => b.Name, StringComparer.Ordinal)];
    }

    /// <summary>The backups that survive retention, and the ones it removes.</summary>
    /// <param name="snapshots">All backups.</param>
    /// <param name="settings">The limits.</param>
    /// <param name="pinned">Names kept even over the cap.</param>
    /// <returns>Kept and removed, each newest first.</returns>
    internal static (IReadOnlyList<BackupInfo> Keep, IReadOnlyList<BackupInfo> Remove) Plan(
        IReadOnlyList<BackupInfo> snapshots,
        BackupSettings settings,
        IReadOnlyCollection<string>? pinned = null)
    {
        var ordered = snapshots.OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Name, StringComparer.Ordinal).ToList();
        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (ordered.Count > 0)
        {
            _ = keep.Add(ordered[0].Name);
        }

        // A labelled backup (pre-migrate, pre-resume, pre-ship, a snapshot, a manual one) records a moment somebody asked for, so
        // thinning by hour, day and week leaves it alone and works only on the scheduled ones; the cap below still counts it.
        foreach (var labelled in ordered.Where(s => s.Label is not null))
        {
            _ = keep.Add(labelled.Name);
        }

        var scheduled = ordered.Where(s => s.Label is null).ToList();
        Fill(scheduled, keep, settings.KeepHourly, s => s.TakenAt.ToString("yyyyMMddHH", CultureInfo.InvariantCulture));
        Fill(scheduled, keep, settings.KeepDaily, s => s.TakenAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        Fill(scheduled, keep, settings.KeepWeekly, s => $"{ISOWeek.GetYear(s.TakenAt.UtcDateTime)}-{ISOWeek.GetWeekOfYear(s.TakenAt.UtcDateTime):00}");

        // The cap wins over the generations, newest first and at least one; a pinned backup survives even when it is not
        // among the newest, and pins that outnumber the cap are all kept, and the newest unpinned backup is kept beside them: the cap limits only the unpinned rest, which it never empties.
        var cap = Math.Max(1, settings.MaxSnapshots);
        var pins = ordered.Where(s => pinned?.Contains(s.Name) == true).ToList();
        var survivors = ordered.Where(s => keep.Contains(s.Name)).ToList();
        if (survivors.Count > cap || pins.Any(s => !keep.Contains(s.Name)))
        {
            var rest = survivors.Where(s => !pins.Contains(s)).Take(Math.Max(1, cap - pins.Count));
            keep = new HashSet<string>(pins.Concat(rest).Select(s => s.Name), StringComparer.Ordinal);
        }

        return (ordered.Where(s => keep.Contains(s.Name)).ToList(), ordered.Where(s => !keep.Contains(s.Name)).ToList());
    }

    /// <summary>The newest <c>pre-migrate-*</c> backup and the newest <c>pre-resume</c> backup, which retention keeps over the cap.</summary>
    internal static IReadOnlyCollection<string> PinnedNames(IReadOnlyList<BackupInfo> snapshots)
    {
        var newestFirst = snapshots.OrderByDescending(s => s.TakenAt).ThenByDescending(s => s.Name, StringComparer.Ordinal).ToList();
        List<string> pins = [];
        if (newestFirst.FirstOrDefault(s => s.Label?.StartsWith(PreMigratePrefix, StringComparison.Ordinal) == true) is { } migrate)
        {
            pins.Add(migrate.Name);
        }

        if (newestFirst.FirstOrDefault(s => s.Label == PreResumeLabel) is { } resume)
        {
            pins.Add(resume.Name);
        }

        return pins;
    }

    /// <summary>Removes the backups retention drops; a failure to delete one is ignored, because the work that asked for the backup has gone on.</summary>
    /// <returns>The names removed.</returns>
    internal static IReadOnlyList<string> Prune(string ahHome, BackupSettings settings, IReadOnlyCollection<string>? pinned = null)
    {
        var all = List(ahHome);
        var (_, remove) = Plan(all, settings, [.. PinnedNames(all), .. pinned ?? []]);
        List<string> removed = [];
        foreach (var backup in remove)
        {
            try
            {
                File.Delete(backup.Path);
                File.Delete(Path.ChangeExtension(backup.Path, ".jsonl"));
                removed.Add(backup.Name);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // left for the next prune
            }
        }

        return removed;
    }

    /// <summary>Takes the scheduled backup when it is due, then prunes; a failure is returned as text so the daemon's job can log it and try again later.</summary>
    /// <returns>The backup taken, or null when none was due or it failed (with <paramref name="failure"/> set then).</returns>
    internal static BackupInfo? TakeIfDue(string ahHome, DateTimeOffset now, BackupSettings settings, out string? failure)
    {
        failure = null;
        try
        {
            if (!File.Exists(DatabasePath(ahHome)))
            {
                return null;
            }

            var newest = List(ahHome) is { Count: > 0 } all ? all[0] : null;
            if (!IsDue(now, newest, settings.Interval, newest is null || ChangedSince(ahHome, newest.TakenAt)))
            {
                return null;
            }

            var backup = Take(ahHome, now);
            _ = Prune(ahHome, settings);
            return backup;
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            failure = "scheduled state database backup failed: " + e.Message;
            return null;
        }
    }

    /// <summary>
    /// <c>ok</c>, or <c>FAILED:</c> with what <c>integrity_check</c> reported, for a backup; the answer is kept beside the backups, keyed by
    /// name, size and write time, so a listing does not check every copy again.
    /// </summary>
    internal static string IntegrityStatus(string ahHome, BackupInfo backup)
    {
        var cache = Path.Join(BackupDirectory(ahHome), ".integrity");
        var stamp = $"{backup.Name}\t{backup.Bytes}\t{File.GetLastWriteTimeUtc(backup.Path).Ticks}\t";
        try
        {
            if (File.Exists(cache) && File.ReadLines(cache).LastOrDefault(l => l.StartsWith(stamp, StringComparison.Ordinal)) is { } hit)
            {
                return hit[stamp.Length..];
            }
        }
        catch (IOException)
        {
            // checked again below
        }

        var (ok, detail) = Integrity(backup.Path);
        var status = ok ? "ok" : "FAILED: " + detail.ReplaceLineEndings(" ");
        try
        {
            File.AppendAllText(cache, stamp + status + "\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the next listing checks again
        }

        return status;
    }

    /// <summary>Whether a scheduled backup is due: none exists, or the interval has passed and the data changed since the newest.</summary>
    internal static bool IsDue(DateTimeOffset now, BackupInfo? newest, TimeSpan interval, bool changed) =>
        newest is null || (changed && now - newest.TakenAt >= interval);

    /// <summary>Whether the database or its write-ahead log was written after <paramref name="since"/> (plus the second the backup name drops).</summary>
    internal static bool ChangedSince(string ahHome, DateTimeOffset since)
    {
        var path = DatabasePath(ahHome);
        var limit = since.UtcDateTime.AddSeconds(1);
        return File.Exists(path + "-wal") && File.GetLastWriteTimeUtc(path + "-wal") > limit
            || File.Exists(path) && File.GetLastWriteTimeUtc(path) > limit;
    }

    /// <summary>Runs <c>integrity_check</c> on a database file through a read-only connection.</summary>
    /// <returns>True with an empty detail when it reports ok; otherwise false with what it reported.</returns>
    internal static (bool Ok, string Detail) Integrity(string databaseFile) => Pragma(databaseFile, "integrity_check");

    internal static (bool Ok, string Detail) Pragma(string databaseFile, string pragma)
    {
        try
        {
            Sqlite.InstallResolver();
            using var connection = SqliteConnection.Open(databaseFile, SqliteOpenMode.ReadOnly, 2000);
            using var statement = connection.Prepare("PRAGMA " + pragma);
            List<string> rows = [];
            while (statement.Step() && rows.Count < 5)
            {
                rows.Add(statement.Text(0));
            }

            return rows is ["ok"] ? (true, string.Empty) : (false, string.Join("; ", rows));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException)
        {
            return (false, e.Message);
        }
    }

    /// <summary>Restores the database from a backup; a dry run unless <paramref name="apply"/>.</summary>
    /// <param name="ahHome">The ah home directory; the caller has made sure the daemon is not using the database.</param>
    /// <param name="name">The backup to restore, or null for the newest that passes <c>integrity_check</c>.</param>
    /// <param name="apply">Whether to replace the files.</param>
    /// <param name="now">The time the replaced file and the <c>pre-restore</c> backup are named for.</param>
    /// <param name="settings">The retention applied after the restore.</param>
    /// <param name="adopt">Makes the marker name the restored database; a test replaces it to fail.</param>
    /// <param name="reinstate">Puts the marker back as it was before the restore; a test replaces it to fail.</param>
    /// <returns>What was done or why not.</returns>
    internal static RestoreResult Restore(string ahHome, string? name, bool apply, DateTimeOffset now, BackupSettings settings, Action<string>? adopt = null, Action<string, byte[]?, byte[]?>? reinstate = null)
    {
        reinstate ??= StateDbAuthority.Reinstate;
        var all = List(ahHome);
        BackupInfo? source = null;
        if (name is not null)
        {
            var wanted = name.EndsWith(".db", StringComparison.Ordinal) ? name : name + ".db";
            source = all.FirstOrDefault(b => b.Name == wanted);
            if (source is null)
            {
                return Refused($"no backup named {name}");
            }
        }
        else
        {
            foreach (var candidate in all)
            {
                if (Integrity(candidate.Path).Ok)
                {
                    source = candidate;
                    break;
                }
            }

            if (source is null)
            {
                return Refused(all.Count == 0 ? "there is no backup to restore from" : "no backup passes integrity_check");
            }
        }

        var check = Integrity(source.Path);
        if (!check.Ok)
        {
            return Refused($"{source.Name} fails integrity_check ({check.Detail}); nothing was changed", source);
        }

        var sourceSchema = SchemaOf(source.Path);
        if (sourceSchema > StateDbModule.SchemaVersion)
        {
            return Refused($"{source.Name} is schema {sourceSchema}, newer than this binary's {StateDbModule.SchemaVersion}; update ah first", source);
        }

        if (PredatesStore(source.Path))
        {
            return Refused($"{source.Name} predates the database store (a domain is still on files) and cannot be restored on this version; nothing was changed", source);
        }

        var live = DatabasePath(ahHome);
        var journals = JournalSuffixes.Where(x => File.Exists(live + x)).ToList();
        var liveExists = File.Exists(live);
        var liveOk = liveExists && Pragma(live, "quick_check").Ok;
        List<string> steps = [];
        if (liveOk)
        {
            steps.Add($"back up the current database as a {PreRestoreLabel} backup");
        }

        if (liveExists || journals.Count > 0)
        {
            var where = liveOk ? ReplacedDirectory(ahHome) : DamagedDirectory(ahHome);
            steps.Add(liveExists
                ? $"move the current database to {where}"
                : $"move the stray {string.Join(" and ", journals.Select(j => "ah.db" + j))} to {where}, so the restored database never sits next to them");
        }

        steps.Add($"copy {source.Name} (schema {sourceSchema}) to {live}");
        if (!apply)
        {
            return new RestoreResult(true, false, $"dry run: restoring {source.Name} would change nothing yet; run again with --apply", source, steps);
        }

        BackupInfo? preRestore = null;
        if (liveOk)
        {
            try
            {
                preRestore = Take(ahHome, now, PreRestoreLabel);
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            {
                return Refused($"the {PreRestoreLabel} backup of the current database failed ({e.Message}); nothing was changed", source);
            }
        }

        byte[]? markerBefore;
        byte[]? markerWritten;
        try
        {
            markerBefore = StateDbAuthority.Snapshot(ahHome);
            StateDbAuthority.PreAdopt(ahHome, source.Path);
            markerWritten = StateDbAuthority.Snapshot(ahHome);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            return Refused($"the authority marker could not be prepared for {source.Name} ({e.Message}); nothing was changed", source);
        }

        string? movedTo = null;
        if (liveExists || journals.Count > 0)
        {
            try
            {
                movedTo = MoveAside(ahHome, liveOk ? ReplacedDirectory(ahHome) : DamagedDirectory(ahHome), now);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                try
                {
                    reinstate(ahHome, markerBefore, markerWritten);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    return Refused($"the current database could not be moved aside ({e.Message}) and the authority marker could not be put back ({back.Message}); the store reads as swapped until `ah db restore` is run", source);
                }

                return Refused($"the current database could not be moved aside ({e.Message}); nothing was changed", source);
            }
        }

        var staging = live + ".restoring";
        try
        {
            // A staging file an interrupted restore left behind goes first, so the copy never meets its bytes or its mode.
            File.Delete(staging);
            File.Copy(source.Path, staging, overwrite: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(staging, live);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (movedTo is not null && PutBack(movedTo, live))
            {
                try
                {
                    reinstate(ahHome, markerBefore, markerWritten);
                    return Refused($"copying {source.Name} into place failed ({e.Message}); the previous database and its authority marker were put back", source);
                }
                catch (Exception back) when (back is IOException or UnauthorizedAccessException)
                {
                    return Refused($"copying {source.Name} into place failed ({e.Message}); the previous database was put back but the authority marker could not be ({back.Message}); the store reads as swapped until `ah db restore` is run", source);
                }
            }

            var kept = movedTo is null ? "there was no database to keep" : $"the previous database is at {movedTo}";
            return new RestoreResult(false, false, $"copying {source.Name} into place failed ({e.Message}); {kept}; no database is in place until a restore succeeds", source, steps);
        }

        string? adoptWarning = null;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                (adopt ?? StateDbAuthority.Adopt)(ahHome);
                break;
            }
            catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
            {
                if (attempt < 3)
                {
                    Thread.Sleep(100);
                    continue;
                }

                adoptWarning = $"the domains the restored database holds could not be written to the authority marker ({e.Message}); the marker already names the restored database, and the next start settles the rest";
                break;
            }
        }

        _ = Prune(ahHome, settings, preRestore is null ? null : [preRestore.Name]);
        return new RestoreResult(
            true,
            true,
            $"restored {source.Name}" + (adoptWarning is null ? string.Empty : WarningMark + adoptWarning),
            source,
            steps);
    }

    /// <summary>Separates a restore's message from a warning it carries.</summary>
    internal const string WarningMark = "; warning: ";

    /// <summary>Moves the files <see cref="MoveAside"/> moved back to <paramref name="live"/>; false when any could not be moved back.</summary>
    private static bool PutBack(string movedTo, string live)
    {
        var ok = true;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            if (!File.Exists(movedTo + suffix))
            {
                continue;
            }

            try
            {
                File.Move(movedTo + suffix, live + suffix);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ok = false;
            }
        }

        return ok;
    }

    /// <summary>
    /// Moves <c>ah.db</c> and its journal files into <paramref name="directory"/> under a name made from <paramref name="now"/>; nothing
    /// is deleted. The name is chosen so that none of the three target files exists, the journal files go first and the database file
    /// last, and a move that fails puts back the ones already moved, so the files are never left split between the two places.
    /// </summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="directory">Where the files go.</param>
    /// <param name="now">The time the name is made from.</param>
    /// <param name="move">Moves one file; a test replaces it to fail part way.</param>
    /// <returns>The path the database file has now (or would have, when only journal files were there), or null when none of the three existed.</returns>
    internal static string? MoveAside(string ahHome, string directory, DateTimeOffset now, Action<string, string>? move = null)
    {
        var live = DatabasePath(ahHome);
        string[] suffixes = ["-wal", "-shm", string.Empty];
        if (!suffixes.Any(x => File.Exists(live + x)))
        {
            return null;
        }

        Directory.CreateDirectory(directory);
        var taken = Truncate(now);
        string target;
        while (true)
        {
            target = Path.Join(directory, $"ah-{taken.ToString(TimeFormat, CultureInfo.InvariantCulture)}.db");
            if (!suffixes.Any(x => File.Exists(target + x) || Directory.Exists(target + x)))
            {
                break;
            }

            taken = taken.AddSeconds(1);
        }

        move ??= static (from, to) => File.Move(from, to);
        List<string> done = [];
        try
        {
            foreach (var suffix in suffixes)
            {
                if (File.Exists(live + suffix))
                {
                    move(live + suffix, target + suffix);
                    done.Add(suffix);
                }
            }
        }
        catch
        {
            foreach (var suffix in Enumerable.Reverse(done))
            {
                try
                {
                    File.Move(target + suffix, live + suffix);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // the file stays where it landed; the caller's message names the directory
                }
            }

            throw;
        }

        return target;
    }

    /// <summary>The doctor row for the state database: schema, integrity, and how fresh the backups are.</summary>
    internal static StateDbHealth Health(string ahHome, DateTimeOffset now, BackupSettings settings)
    {
        var live = DatabasePath(ahHome);
        var backups = List(ahHome);
        var newest = backups.Count > 0 ? backups[0] : null;
        var damaged = Directory.Exists(DamagedDirectory(ahHome)) ? Directory.EnumerateFiles(DamagedDirectory(ahHome), "ah-*.db").Count() : 0;
        var keptNote = damaged > 0 ? $"; {damaged} damaged cop{(damaged == 1 ? "y" : "ies")} kept in {DamagedDirectory(ahHome)}" : string.Empty;
        var sizeNote = backups.Count == 0
            ? "no backups"
            : $"{backups.Count} backup(s), newest {Age(now - newest!.TakenAt)} old, {backups.Sum(b => b.Bytes) / 1024} KiB";
        var verdict = StateDbAuthority.Check(ahHome);
        if (verdict.Problem is StoreProblem.Missing or StoreProblem.Swapped)
        {
            return new StateDbHealth("error", verdict.Detail + "; " + sizeNote + keptNote);
        }

        if (!File.Exists(live))
        {
            return new StateDbHealth(damaged > 0 ? "error" : "ok", (damaged > 0 ? "no state database; restore one with: ah db restore" : "no state database yet") + "; " + sizeNote + keptNote);
        }

        var quick = Pragma(live, "quick_check");
        if (!quick.Ok)
        {
            return new StateDbHealth("error", $"the state database is damaged ({quick.Detail}); restore with: ah db restore; {sizeNote}{keptNote}");
        }

        var schema = SchemaOf(live);
        var recovered = StateDbRecovery.RecentNote(ahHome, now);
        var head = $"schema v{schema}, integrity ok, {sizeNote}{keptNote}{UnplacedNote(ahHome)}{(recovered is null ? string.Empty : "; " + recovered)}";
        if (recovered is not null)
        {
            return new StateDbHealth("warn", head);
        }

        if (backups.Count == 0)
        {
            return new StateDbHealth("warn", head);
        }

        return now - newest!.TakenAt > settings.Interval * 2 && ChangedSince(ahHome, newest.TakenAt)
            ? new StateDbHealth("warn", head + "; the newest backup is more than twice the interval old and the data changed since")
            : new StateDbHealth("ok", head);
    }

    /// <summary>What the migration and the session import set aside because it could not be placed in a hierarchy directory, as an informational tail for the health row; empty when nothing is set aside.</summary>
    internal static string UnplacedNote(string ahHome)
    {
        var opened = StateDbModule.OpenReader(ahHome);
        if (opened.Reader is not { } reader)
        {
            return string.Empty;
        }

        using (reader)
        {
            try
            {
                var sessions = reader.Query("SELECT reason, count(*) FROM sessions_unplaced WHERE " + StateDbModule.UnplacedSessionIsDead + " GROUP BY reason ORDER BY reason");
                var teams = (long)reader.Query("SELECT count(*) FROM teams_unplaced")[0][0]!;
                var members = (long)reader.Query("SELECT count(*) FROM members_unplaced")[0][0]!;
                var total = sessions.Sum(r => (long)r[1]!);
                var placelessIds = StateDbSessions.Read(ahHome).Rows.Where(r => r.HierDir.Length == 0).Select(r => r.SessionId).ToHashSet(StringComparer.Ordinal);
                var placeless = placelessIds.Count;
                var stale = reader.Query("SELECT session_id FROM sessions WHERE ended_at IS NULL").Count(r => placelessIds.Contains((string)r[0]!));
                if (total + teams + members + placeless == 0)
                {
                    return string.Empty;
                }

                var byReason = string.Join(", ", sessions.Select(r => FormattableString.Invariant($"{r[0]}: {(long)r[1]!}")));
                return FormattableString.Invariant($"; unplaced sessions: {total}") + (byReason.Length > 0 ? $" ({byReason}; after an upgrade a session can be in both sessions and sessions_unplaced)" : string.Empty)
                    + (placeless > 0 ? FormattableString.Invariant($"; registrations without a team file: {placeless}") : string.Empty)
                    + (stale > 0 ? FormattableString.Invariant($"; live sessions whose registration lost its team file: {stale}") : string.Empty)
                    + (teams + members > 0 ? FormattableString.Invariant($"; unplaced teams: {teams}, members: {members}") : string.Empty)
                    + (total + teams + members > 0 ? "; remove with: ah db prune --unplaced" : string.Empty);
            }
            catch (SqliteException)
            {
                return string.Empty;
            }
        }
    }

    private static string Age(TimeSpan span) =>
        span.TotalMinutes < 1 ? "under a minute" : span.TotalHours < 1 ? $"{(int)span.TotalMinutes}m" : span.TotalDays < 1 ? $"{(int)span.TotalHours}h" : $"{(int)span.TotalDays}d";

    internal static int SchemaOf(string databaseFile)
    {
        try
        {
            Sqlite.InstallResolver();
            using var connection = SqliteConnection.Open(databaseFile, SqliteOpenMode.ReadOnly, 2000);
            return StateDbFile.ReadVersion(connection) ?? 0;
        }
        catch (Exception e) when (e is SqliteException or IOException)
        {
            return 0;
        }
    }

    /// <summary>Whether a backup's mode rows put any domain on files: it predates the database store, and this build no longer imports files.</summary>
    internal static bool PredatesStore(string databaseFile)
    {
        try
        {
            Sqlite.InstallResolver();
            using var connection = SqliteConnection.Open(databaseFile, SqliteOpenMode.ReadOnly, 2000);
            return StateDbRows.Run(connection, "SELECT 1 FROM meta WHERE key LIKE 'mode.%' AND value = ?1 LIMIT 1", [StateDbMessageMode.Files]).Count > 0;
        }
        catch (Exception e) when (e is SqliteException or IOException)
        {
            return false;
        }
    }

    private static RestoreResult Refused(string message, BackupInfo? source = null) => new(false, false, message, source, []);

    private static DateTimeOffset Truncate(DateTimeOffset at)
    {
        var utc = at.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, utc.Second, TimeSpan.Zero);
    }

    private static string Suffix(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var clean = new string([.. label.ToLowerInvariant().Select(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '-' ? c : '-')]);
        return "-" + (clean.Length > 40 ? clean[..40] : clean);
    }

    private static void Fill(List<BackupInfo> ordered, HashSet<string> keep, int count, Func<BackupInfo, string> bucket)
    {
        HashSet<string> seen = [];
        foreach (var snapshot in ordered)
        {
            if (seen.Count >= count)
            {
                return;
            }

            if (seen.Add(bucket(snapshot)))
            {
                _ = keep.Add(snapshot.Name);
            }
        }
    }

    /// <summary>
    /// Writes one JSON line per row of every table except the disposable file cache, read from the backup copy so it matches the
    /// database beside it: <c>{"table":"x","row":{"col":value}}</c>, a blob as <c>{"base64":"..."}</c>. The bytes are the ones
    /// <c>JsonText.ToUtf8</c> gives for that object, written straight from the row's UTF-8 with no object and no text per row
    /// (ah2-1): a scheduled backup of a 250 MB database allocated about 1.5 GB here, every hour.
    /// </summary>
    private static void WriteExport(string backupFile, string exportFile)
    {
        var partial = exportFile + PartialSuffix;
        using (var connection = SqliteConnection.Open(backupFile, SqliteOpenMode.ReadOnly, 2000))
        using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            List<string> tables = [];
            using (var list = connection.Prepare("SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> 'cache_files' ORDER BY name"))
            {
                while (list.Step())
                {
                    tables.Add(list.Text(0));
                }
            }

            var line = new System.Buffers.ArrayBufferWriter<byte>(1 << 16);
            foreach (var table in tables)
            {
                using var rows = connection.Prepare("SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
                var head = System.Text.Encoding.UTF8.GetBytes("{\"table\":" + JsonText.Quote(table) + ",\"row\":{");
                byte[][]? keys = null;
                while (rows.Step())
                {
                    keys ??= [.. Enumerable.Range(0, rows.ColumnCount).Select(c => System.Text.Encoding.UTF8.GetBytes((c > 0 ? "," : string.Empty) + JsonText.Quote(rows.ColumnName(c)) + ":"))];
                    line.Clear();
                    line.Write(head);
                    for (var column = 0; column < keys.Length; column++)
                    {
                        line.Write(keys[column]);
                        WriteValue(line, rows, column);
                    }

                    line.Write("}}\n"u8);
                    output.Write(line.WrittenSpan);
                }
            }
        }

        File.Move(partial, exportFile, overwrite: true);
    }

    /// <summary>The largest integer a JSON number (a double) holds exactly; within it the number's text is its decimal digits.</summary>
    private const long ExactInteger = 1L << 53;

    /// <summary>The bytes a JSON string must escape: the controls, the quote and the backslash.</summary>
    private static readonly System.Buffers.SearchValues<byte> Escaped =
        System.Buffers.SearchValues.Create([.. Enumerable.Range(0, 0x20).Select(static b => (byte)b), (byte)'"', (byte)'\\']);

    private static void WriteValue(System.Buffers.ArrayBufferWriter<byte> line, SqliteStatement rows, int column)
    {
        switch (rows.ColumnType(column))
        {
            case 1:
                var integer = rows.Int64(column);
                if (integer is > -ExactInteger and < ExactInteger)
                {
                    _ = System.Buffers.Text.Utf8Formatter.TryFormat(integer, line.GetSpan(20), out var digits);
                    line.Advance(digits);
                }
                else
                {
                    // past 2^53 the old export wrote the double's text: kept, so an export reads the same as before
                    WriteAscii(line, JsonText.NumberText(integer));
                }

                break;
            case 2:
                WriteAscii(line, JsonText.NumberText(rows.Double(column)));
                break;
            case 3:
                var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(Math.Max(1, rows.ByteLength(column)));
                try
                {
                    // the text ends at its first NUL, as it did when it was read as a C string
                    var text = rented.AsSpan(0, rows.CopyText(column, rented));
                    var nul = text.IndexOf((byte)0);
                    WriteQuoted(line, nul < 0 ? text : text[..nul]);
                }
                finally
                {
                    System.Buffers.ArrayPool<byte>.Shared.Return(rented);
                }

                break;
            case 4:
                var blob = rows.Blob(column);
                line.Write("{\"base64\":\""u8);
                _ = System.Buffers.Text.Base64.EncodeToUtf8(blob, line.GetSpan(System.Buffers.Text.Base64.GetMaxEncodedToUtf8Length(blob.Length)), out _, out var encoded);
                line.Advance(encoded);
                line.Write("\"}"u8);
                break;
            default:
                line.Write("null"u8);
                break;
        }
    }

    private static void WriteAscii(System.Buffers.ArrayBufferWriter<byte> line, string text) =>
        line.Advance(System.Text.Encoding.ASCII.GetBytes(text, line.GetSpan(text.Length)));

    /// <summary><paramref name="utf8"/> as a JSON string literal, with <c>JSON.stringify</c>'s escapes; text that is not valid UTF-8 goes the old way, through a string.</summary>
    internal static void WriteQuoted(System.Buffers.ArrayBufferWriter<byte> line, ReadOnlySpan<byte> utf8)
    {
        if (!System.Text.Unicode.Utf8.IsValid(utf8))
        {
            var quoted = JsonText.Quote(System.Text.Encoding.UTF8.GetString(utf8));
            line.Advance(System.Text.Encoding.UTF8.GetBytes(quoted, line.GetSpan(System.Text.Encoding.UTF8.GetMaxByteCount(quoted.Length))));
            return;
        }

        line.Write("\""u8);
        while (true)
        {
            var at = utf8.IndexOfAny(Escaped);
            if (at < 0)
            {
                line.Write(utf8);
                break;
            }

            line.Write(utf8[..at]);
            var b = utf8[at];
            switch (b)
            {
                case (byte)'"': line.Write("\\\""u8); break;
                case (byte)'\\': line.Write("\\\\"u8); break;
                case (byte)'\b': line.Write("\\b"u8); break;
                case (byte)'\f': line.Write("\\f"u8); break;
                case (byte)'\n': line.Write("\\n"u8); break;
                case (byte)'\r': line.Write("\\r"u8); break;
                case (byte)'\t': line.Write("\\t"u8); break;
                default:
                    line.Write("\\u00"u8);
                    var hex = line.GetSpan(2);
                    hex[0] = (byte)"0123456789abcdef"[b >> 4];
                    hex[1] = (byte)"0123456789abcdef"[b & 0xF];
                    line.Advance(2);
                    break;
            }

            utf8 = utf8[(at + 1)..];
        }

        line.Write("\""u8);
    }
}
