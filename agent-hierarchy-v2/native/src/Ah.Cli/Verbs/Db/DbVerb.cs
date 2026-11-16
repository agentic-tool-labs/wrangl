using System.Diagnostics;
using System.Globalization;
using Ah.StateDb;

namespace Ah.Cli.Verbs.Db;

/// <summary><c>ah db backup|list|restore|check</c>: backups of the state database and the way back to one of them.</summary>
internal static class DbVerb
{
    private static readonly IStateDbBackups Backups = StateDbModule.CreateBackups();

    private const string Usage = "usage: ah db <backup [--label L] | list | restore [<name>] [--apply] | check | prune --unplaced [--yes] | init --accept-empty | import [--dir <H>]>";

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr) =>
        args is ["test-seed", ..] && !MsgsVerb.Handles(args) ? TestSeedVerb.Run(context, args[1..], stdout, stderr)
        : MsgsVerb.Handles(args) ? MsgsVerb.Run(context.Env.AhHome, context.Services?.Value, DateTimeOffset.UtcNow, args, stdout, stderr)
        : args is ["import"] or ["import", "--dir", _] ? Import(context.Env.AhHome, args.Length == 3 ? Path.GetFullPath(args[2]) : null, stdout)
        : args is ["status-rebuild"] or ["status-rebuild", "--pool", _] ? StatusRebuild(context.Services?.Value.Hierarchy.Status, args.Length == 3 ? Path.GetFullPath(args[2]) : null, stdout, stderr)
        : Run(context.Env.AhHome, DateTimeOffset.UtcNow, DaemonPid, args, stdout, stderr);

    /// <summary>
    /// <c>ah db import [--dir &lt;H&gt;]</c> (files-off-disk §3.1): the files import pass over the dir list, or over one dir whether or not any
    /// registry knows it; one JSON line per dir run, its record with the dir. A dir already complete runs only with <c>--dir</c>. Exit 0 always.
    /// </summary>
    internal static int Import(string ahHome, string? dir, TextWriter stdout)
    {
        void Print(string d, string record)
        {
            var line = Ah.Core.Json.JsonText.Parse(record).AsObject();
            line["dir"] = d;
            stdout.WriteLine(Ah.Core.Json.JsonText.Stringify(line, 0));
        }

        if (dir is null)
        {
            Ah.Cli.Composition.FilesImport.Run(ahHome, Ah.Cli.Composition.FilesImport.KnownDirs(ahHome), force: false, Print);
        }
        else if (StateDbModule.ImportHierDirFiles(ahHome, dir, force: true) is { } record)
        {
            Print(dir, record);
        }

        return 0;
    }

    /// <summary>
    /// <c>ah db status-rebuild [--pool &lt;dir&gt;]</c> (ah-msgs-fast-2 §3.8 item 6): recovery, rebuilds the persisted status entries of status dir
    /// <c>dir</c>, or of every status dir the table holds; one line per dir, exit 1 when any rebuild was refused.
    /// </summary>
    internal static int StatusRebuild(Ah.Hierarchy.IHierarchyStatus? status, string? dir, TextWriter stdout, TextWriter stderr)
    {
        if (status is null)
        {
            stderr.WriteLine("ah db status-rebuild: no hierarchy services");
            return 1;
        }

        var failed = false;
        foreach (var d in dir is null ? status.StatusEntryDirs() : [dir])
        {
            var (ok, message) = status.RebuildStatusEntriesOf(d);
            failed |= !ok;
            (ok ? stdout : stderr).WriteLine(ok ? $"rebuilt {d}" : $"ah db status-rebuild: {d}: {message}");
        }

        return failed ? 1 : 0;
    }

    /// <summary>Runs a verb against <paramref name="ahHome"/>; <paramref name="daemonPid"/> names the live daemon's process id, or null when none runs.</summary>
    internal static int Run(string ahHome, DateTimeOffset now, Func<string, int?> daemonPid, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        return args[0] switch
        {
            "backup" => Backup(ahHome, now, args[1..], stdout, stderr),
            "list" when args.Length == 1 => List(ahHome, now, stdout),
            "restore" => Restore(ahHome, now, daemonPid, args[1..], stdout, stderr),
            "check" when args.Length == 1 => Check(ahHome, stdout),
            "prune" => Prune(ahHome, now, args[1..], stdout, stderr),
            "init" when args is ["init", "--accept-empty"] => Init(stderr),
            _ => Usage2(stderr),
        };
    }

    private static int Usage2(TextWriter stderr)
    {
        stderr.WriteLine(Usage);
        return 2;
    }

    private static int Backup(string ahHome, DateTimeOffset now, string[] args, TextWriter stdout, TextWriter stderr)
    {
        string? label = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--label" && i + 1 < args.Length && label is null)
            {
                label = args[++i];
            }
            else
            {
                return Usage2(stderr);
            }
        }

        if (label is not null && (label.Length == 0 || !label.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-')))
        {
            stderr.WriteLine("ah db backup: a label is letters, digits, '.', '_' and '-'");
            return 2;
        }

        if (!File.Exists(StateDbPath(ahHome)))
        {
            stderr.WriteLine("ah db backup: there is no state database yet");
            return 1;
        }

        try
        {
            var backup = Backups.Take(ahHome, now, label);
            _ = Backups.Prune(ahHome, BackupSettings.Default, [backup.Name]);
            stdout.WriteLine($"backed up {backup.Name} ({backup.Bytes / 1024} KiB, schema {backup.Schema}, export {(backup.HasExport ? "written" : "not written")})");
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            stderr.WriteLine("ah db backup: " + e.Message);
            return 1;
        }
    }

    private static int List(string ahHome, DateTimeOffset now, TextWriter stdout)
    {
        var all = Backups.List(ahHome);
        if (all.Count == 0)
        {
            stdout.WriteLine("no backups");
            return 0;
        }

        foreach (var backup in all)
        {
            var age = now - backup.TakenAt;
            stdout.WriteLine(string.Join(
                '\t',
                backup.Name,
                age.TotalHours < 48 ? $"{(int)age.TotalHours}h{age.Minutes:00}m" : $"{(int)age.TotalDays}d",
                (backup.Bytes / 1024).ToString(CultureInfo.InvariantCulture) + " KiB",
                "schema " + backup.Schema,
                Backups.IntegrityStatus(ahHome, backup) + (Backups.PredatesStore(backup) ? " (predates the database store; cannot be restored)" : string.Empty)));
        }

        return 0;
    }

    private static int Restore(string ahHome, DateTimeOffset now, Func<string, int?> daemonPid, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var apply = false;
        string? name = null;
        foreach (var arg in args)
        {
            if (arg == "--apply" && !apply)
            {
                apply = true;
            }
            else if (!arg.StartsWith('-') && name is null)
            {
                name = arg;
            }
            else
            {
                return Usage2(stderr);
            }
        }

        if (apply && daemonPid(ahHome) is { } pid)
        {
            stderr.WriteLine($"ah db restore: ahd is running (pid {pid}); stop ahd, then retry (a restore replaces the file it has open)");
            return 1;
        }

        var result = Backups.Restore(ahHome, name, apply, now, BackupSettings.Default);
        (result.Ok ? stdout : stderr).WriteLine(result.Ok ? result.Message : "ah db restore: " + result.Message);
        foreach (var step in result.Steps)
        {
            stdout.WriteLine("  - " + step);
        }

        return result.Ok ? 0 : 1;
    }

    /// <summary>
    /// <c>ah db prune --unplaced</c>: removes the rows the migration and the session import set aside because they could not be placed
    /// in a hierarchy directory. A session row with a document stays, live or ended: the document is the only copy.
    /// It reports what it would remove and removes it only with <c>--yes</c>. The database file is readable and
    /// writable by its owner alone, so only the owner can run it. Before a delete it takes the backup <c>ah db backup --label pre-prune</c>
    /// takes, and a failed backup stops the delete.
    /// </summary>
    private static int Prune(string ahHome, DateTimeOffset now, string[] args, TextWriter stdout, TextWriter stderr)
    {
        var unplaced = false;
        var yes = false;
        foreach (var arg in args)
        {
            if (arg == "--unplaced" && !unplaced)
            {
                unplaced = true;
            }
            else if (arg == "--yes" && !yes)
            {
                yes = true;
            }
            else
            {
                return Usage2(stderr);
            }
        }

        if (!unplaced)
        {
            return Usage2(stderr);
        }

        if (!File.Exists(StateDbPath(ahHome)))
        {
            stderr.WriteLine("ah db prune: there is no state database yet");
            return 1;
        }

        // the rows are the only copy of those sessions, so a backup precedes the delete and a failed backup stops it
        if (yes)
        {
            try
            {
                var backup = Backups.Take(ahHome, now, "pre-prune");
                _ = Backups.Prune(ahHome, BackupSettings.Default, [backup.Name]);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                stderr.WriteLine("ah db prune: the backup before the delete failed, so nothing was removed: " + e.Message);
                return 1;
            }
        }

        var direct = StateDbModule.OpenDirect(ahHome);
        var result = direct.Write(tx =>
        {
            var sessions = (long)tx.Query("SELECT count(*) FROM sessions_unplaced WHERE " + StateDbModule.UnplacedSessionIsDead)[0][0]!;
            var documented = (long)tx.Query("SELECT count(*) FROM sessions_unplaced")[0][0]! - sessions;
            var teams = (long)tx.Query("SELECT count(*) FROM teams_unplaced")[0][0]!;
            var members = (long)tx.Query("SELECT count(*) FROM members_unplaced")[0][0]!;
            if (yes)
            {
                tx.Execute("DELETE FROM sessions_unplaced WHERE " + StateDbModule.UnplacedSessionIsDead);
                tx.Execute("DELETE FROM teams_unplaced");
                tx.Execute("DELETE FROM members_unplaced");
            }

            return (sessions, teams, members, documented);
        });
        if (!result.Ok)
        {
            stderr.WriteLine("ah db prune: " + result.Message);
            return 1;
        }

        var (s, t, m, l) = result.Value;
        var counts = string.Create(CultureInfo.InvariantCulture, $"{s} unplaced session(s), {t} team(s), {m} member(s)");
        var kept = l > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {l} session(s) with a document stay") : string.Empty;
        stdout.WriteLine((yes ? "removed " + counts : "would remove " + counts + "; run again with --yes to remove them") + kept);
        return 0;
    }

    /// <summary>
    /// <c>ah db init --accept-empty</c> would accept an empty store where a flipped one is gone, which is data loss that only the user may
    /// approve. Nothing on this call proves the user approved it, so it refuses and creates nothing.
    /// </summary>
    private static int Init(TextWriter stderr)
    {
        stderr.WriteLine("ah db init --accept-empty: refused. Accepting an empty store is data loss and only the user can approve it; nothing here proves the user did, so nothing was created.");
        return 1;
    }

    private static int Check(string ahHome, TextWriter stdout)
    {
        var live = StateDbPath(ahHome);
        var failed = false;
        if (File.Exists(live))
        {
            var (ok, detail) = Backups.Integrity(live);
            stdout.WriteLine(ok ? "state database: integrity ok" : "state database: DAMAGED (" + detail + ")");
            failed |= !ok;
        }
        else
        {
            stdout.WriteLine("state database: none yet");
        }

        var newest = Backups.List(ahHome) is { Count: > 0 } all ? all[0] : null;
        stdout.WriteLine(newest is null ? "backups: none" : $"newest backup {newest.Name}: {Backups.IntegrityStatus(ahHome, newest)}");
        stdout.WriteLine("exports: no exported files are kept in step with the database yet, so there is nothing to compare");
        return failed ? 1 : 0;
    }

    private static string StateDbPath(string ahHome) => Path.Join(ahHome, "state", "ah.db");

    /// <summary>The process id <c>run/ahd.json</c> names when that process is alive.</summary>
    private static int? DaemonPid(string ahHome)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Join(ahHome, "run", "ahd.json")));
            if (!document.RootElement.TryGetProperty("pid", out var pidElement) || !pidElement.TryGetInt32(out var pid))
            {
                return null;
            }

            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : pid;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
