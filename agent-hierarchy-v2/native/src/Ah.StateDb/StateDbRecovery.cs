using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

public enum RecoveryOutcome
{
    /// <summary>The store is present and is the one the marker names; nothing was done.</summary>
    NotNeeded,

    /// <summary>A backup was restored.</summary>
    Restored,

    /// <summary>The store is gone or swapped and no backup passes <c>integrity_check</c> with the marker's <c>db_id</c>; the store stays unavailable.</summary>
    NoEligibleBackup,

    /// <summary>A backup was chosen and restoring it failed.</summary>
    Failed,
}

/// <summary>What a start-up recovery did.</summary>
public sealed record RecoveryResult(RecoveryOutcome Outcome, string Detail, string? Backup, TimeSpan? Age);

/// <summary>Restores ah's own store when the marker says it is gone or has been replaced; an operational repair, not a question for the user.</summary>
internal static class StateDbRecovery
{
    internal static string RecordPath(string ahHome) => Path.Join(ahHome, "state", "store-recovered.json");

    /// <summary>Whether <paramref name="verdict"/> sends a start-up looking for a backup to restore.</summary>
    internal static bool Needed(StoreVerdict verdict) => verdict.Problem is not (StoreProblem.None or StoreProblem.Unverified);

    internal static RecoveryResult Recover(string ahHome, DateTimeOffset now, Action<string>? adopt = null)
    {
        var verdict = StateDbAuthority.Check(ahHome, 4);
        if (!Needed(verdict))
        {
            return new RecoveryResult(RecoveryOutcome.NotNeeded, verdict.Detail, null, null);
        }

        var (marker, _) = StateDbAuthority.Read(ahHome);
        if (marker is null)
        {
            return new RecoveryResult(RecoveryOutcome.NoEligibleBackup, "the authority marker cannot be read, so no backup can be matched to it", null, null);
        }

        var backups = StateDbBackups.List(ahHome);
        var chosen = backups.FirstOrDefault(b => StateDbBackups.Integrity(b.Path).Ok && IdOf(b.Path) == marker.DbId && StateDbBackups.SchemaOf(b.Path) <= StateDbModule.SchemaVersion);
        if (chosen is null)
        {
            return new RecoveryResult(RecoveryOutcome.NoEligibleBackup, $"{verdict.Detail}; of {backups.Count} backup(s) none passes integrity_check with db_id {marker.DbId}", null, null);
        }

        var restored = StateDbBackups.Restore(ahHome, chosen.Name, apply: true, now, BackupSettings.Default, adopt);
        if (!restored.Ok)
        {
            return new RecoveryResult(RecoveryOutcome.Failed, restored.Message, chosen.Name, now - chosen.TakenAt);
        }

        var age = now - chosen.TakenAt;
        var warningAt = restored.Message.IndexOf(StateDbBackups.WarningMark, StringComparison.Ordinal);
        var warning = warningAt < 0 ? string.Empty : restored.Message[(warningAt + StateDbBackups.WarningMark.Length)..];
        WriteRecord(ahHome, now, chosen.Name, age, warning, verdict.Problem);
        return new RecoveryResult(RecoveryOutcome.Restored, $"restored {chosen.Name}" + (warning.Length == 0 ? string.Empty : "; warning: " + warning), chosen.Name, age);
    }

    private static string? IdOf(string databaseFile)
    {
        try
        {
            using var connection = SqliteConnection.Open(databaseFile, SqliteOpenMode.ReadOnly, 250);
            return StateDbAuthority.MetaValue(connection, "db_id");
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static void WriteRecord(string ahHome, DateTimeOffset now, string backup, TimeSpan age, string warning, StoreProblem was)
    {
        var record = new JsonObject
        {
            ["at"] = now.ToString("o", CultureInfo.InvariantCulture),
            ["backup"] = backup,
            ["age_seconds"] = (long)age.TotalSeconds,
            ["was"] = was == StoreProblem.Missing ? "store-missing" : "store-swapped",
            ["warning"] = warning,
        };
        var path = RecordPath(ahHome);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonText.Stringify(record, 2) + "\n");
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>A line for the doctor row when a recovery happened within the last 24 hours; null otherwise.</summary>
    internal static string? RecentNote(string ahHome, DateTimeOffset now)
    {
        try
        {
            var path = RecordPath(ahHome);
            if (!File.Exists(path))
            {
                return null;
            }

            var record = JsonText.Parse(File.ReadAllBytes(path));
            if (record.Member("at") is not { IsText: true } at || !DateTimeOffset.TryParse(at.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when) || now - when > TimeSpan.FromHours(24))
            {
                return null;
            }

            var backup = record.Member("backup") is { IsText: true } b ? b.AsString() : "?";
            var was = record.Member("was") is { IsText: true } w ? w.AsString() : "?";
            var warning = record.Member("warning") is { IsText: true } wn ? wn.AsString() : string.Empty;
            return $"store-recovered {(int)(now - when).TotalMinutes}m ago ({was}): restored {backup}" + (warning.Length > 0 ? "; warning: " + warning : string.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }
}
