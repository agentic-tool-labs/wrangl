namespace Ah.StateDb;

/// <summary>Backups of the state database: <c>VACUUM INTO</c> copies with a logical export beside each, generational retention under a cap, and a restore that is a dry run unless applied.</summary>
public interface IStateDbBackups
{
    /// <summary>Backs up the database of <paramref name="ahHome"/>.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="now">The time the backup is named for.</param>
    /// <param name="label">Why it is taken; null for a scheduled backup.</param>
    /// <returns>The backup.</returns>
    BackupInfo Take(string ahHome, DateTimeOffset now, string? label = null);

    /// <summary>Backs up the database and then removes the older backups carrying the same <paramref name="label"/>, so the label keeps one copy.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="now">The time the backup is named for.</param>
    /// <param name="label">The label whose older copies go.</param>
    /// <returns>The backup.</returns>
    BackupInfo TakeReplacing(string ahHome, DateTimeOffset now, string label);

    /// <summary>The backups on disk, newest first.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <returns>The backups.</returns>
    IReadOnlyList<BackupInfo> List(string ahHome);

    /// <summary>Removes the backups retention drops; the newest <c>pre-migrate</c> and <c>pre-resume</c> backups and <paramref name="pinned"/> are kept over the cap.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="settings">The limits.</param>
    /// <param name="pinned">Backup names to keep, such as one just taken for an operation.</param>
    /// <returns>The names removed.</returns>
    IReadOnlyList<string> Prune(string ahHome, BackupSettings settings, IReadOnlyCollection<string>? pinned = null);

    /// <summary><c>ok</c>, or <c>FAILED:</c> with what the check reported, kept beside the backups so a listing does not check every copy again.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="backup">The backup.</param>
    /// <returns>The status text.</returns>
    string IntegrityStatus(string ahHome, BackupInfo backup);

    /// <summary>Runs <c>integrity_check</c> on a database file through a read-only connection.</summary>
    /// <param name="databaseFile">The file.</param>
    /// <returns>True with an empty detail when it reports ok; otherwise false with what it reported.</returns>
    (bool Ok, string Detail) Integrity(string databaseFile);

    /// <summary>Whether a backup predates the database store (a mode row says files), so <see cref="Restore"/> refuses it.</summary>
    /// <param name="backup">The backup.</param>
    /// <returns>True for such a backup.</returns>
    bool PredatesStore(BackupInfo backup);

    /// <summary>Restores the database from a backup; a dry run unless <paramref name="apply"/>. The caller has made sure the daemon is not using the database.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="name">The backup to restore, or null for the newest that passes the integrity check.</param>
    /// <param name="apply">Whether to replace the files.</param>
    /// <param name="now">The time the replaced file and the <c>pre-restore</c> backup are named for.</param>
    /// <param name="settings">The retention applied after the restore.</param>
    /// <returns>What was done or why not.</returns>
    RestoreResult Restore(string ahHome, string? name, bool apply, DateTimeOffset now, BackupSettings settings);

    /// <summary>The doctor row for the state database: schema, integrity, and how fresh the backups are.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="now">The current time.</param>
    /// <param name="settings">The interval the freshness is judged against.</param>
    /// <returns>The row.</returns>
    StateDbHealth Health(string ahHome, DateTimeOffset now, BackupSettings settings);

    /// <summary>Takes the scheduled backup when it is due and prunes; a failure is returned as text so the daemon's job can log it and try again.</summary>
    /// <param name="ahHome">The ah home directory.</param>
    /// <param name="now">The current time.</param>
    /// <param name="settings">The interval and the limits.</param>
    /// <param name="failure">Why the backup failed, when it did.</param>
    /// <returns>The backup taken, or null when none was due or it failed.</returns>
    BackupInfo? TakeIfDue(string ahHome, DateTimeOffset now, BackupSettings settings, out string? failure);
}

internal sealed class StateDbBackupService : IStateDbBackups
{
    public BackupInfo Take(string ahHome, DateTimeOffset now, string? label = null) => StateDbBackups.Take(ahHome, now, label);

    public BackupInfo TakeReplacing(string ahHome, DateTimeOffset now, string label) => StateDbBackups.TakeReplacing(ahHome, now, label);

    public IReadOnlyList<BackupInfo> List(string ahHome) => StateDbBackups.List(ahHome);

    public IReadOnlyList<string> Prune(string ahHome, BackupSettings settings, IReadOnlyCollection<string>? pinned = null) => StateDbBackups.Prune(ahHome, settings, pinned);

    public string IntegrityStatus(string ahHome, BackupInfo backup) => StateDbBackups.IntegrityStatus(ahHome, backup);

    public (bool Ok, string Detail) Integrity(string databaseFile) => StateDbBackups.Integrity(databaseFile);

    public bool PredatesStore(BackupInfo backup) => StateDbBackups.PredatesStore(backup.Path);

    public RestoreResult Restore(string ahHome, string? name, bool apply, DateTimeOffset now, BackupSettings settings) =>
        StateDbBackups.Restore(ahHome, name, apply, now, settings);

    public StateDbHealth Health(string ahHome, DateTimeOffset now, BackupSettings settings) => StateDbBackups.Health(ahHome, now, settings);

    public BackupInfo? TakeIfDue(string ahHome, DateTimeOffset now, BackupSettings settings, out string? failure) =>
        StateDbBackups.TakeIfDue(ahHome, now, settings, out failure);
}
