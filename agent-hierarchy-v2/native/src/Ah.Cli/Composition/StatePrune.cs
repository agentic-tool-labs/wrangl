using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Composition;

/// <summary>
/// The body of the daemon's state-prune job: the settled peer rows older than the retention go, after one backup, and what went is
/// counted. Stored messages are not pruned. A function of its collaborators so a test drives it with a fixed clock and a backup of its own.
/// </summary>
internal static class StatePrune
{
    /// <summary>The label of the one rolling backup taken before a prune that drops something.</summary>
    internal const string BackupLabel = "pre-prune";

    /// <summary>
    /// The backup a prune takes: one rolling <see cref="BackupLabel"/> copy of the state database, or <c>none</c> when the home has no database,
    /// because the copy protects database rows only. A database that exists and cannot be backed up throws, so nothing is deleted.
    /// </summary>
    internal static string Backup(Ah.StateDb.IStateDbBackups backups, string ahHome, DateTimeOffset now)
    {
        if (!File.Exists(Path.Join(ahHome, "state", "ah.db")))
        {
            return "none";
        }

        try
        {
            return backups.TakeReplacing(ahHome, now, BackupLabel).Name;
        }
        catch (Exception e) when (e is not (IOException or UnauthorizedAccessException or OutOfMemoryException))
        {
            throw new InvalidOperationException(e.Message, e);
        }
    }

    /// <summary>Whether the state settings make the prune count only: every value but <c>on</c>.</summary>
    internal static bool CountOnly(JsonObject settings) => settings["prune"].AsString() != "on";

    /// <summary>How long an ended session row is kept before a prune deletes it.</summary>
    internal const int EndedSessionDays = 7;

    /// <summary>
    /// One run. Builds the open and closed sets once; looks without a lock at what would go and ends there when it is nothing (no backup,
    /// no lock, no log line); otherwise takes <paramref name="backup"/> once, and when that throws deletes nothing; then prunes the peer
    /// rows. <paramref name="readPeers"/> is the peer-log read (null: the hierarchy's own known-good read); a read that reports failure
    /// ends the run with nothing deleted. With <paramref name="countOnly"/> the run stops after the dry pass and returns the would-drop
    /// counts (<c>mode=count</c>): no backup, no lock, no deletion. Returns the counts line the daemon logs and the job shows, or null
    /// when nothing was to go. The idle-session count is the one the dry pass found, because the peer prune reports one number for all its rules.
    /// <paramref name="sessions"/>, when given, also has its session rows ended more than <see cref="EndedSessionDays"/> ago deleted after the same backup.
    /// </summary>
    internal static string? Run(HierarchyServices hierarchy, IReadOnlyList<string> hierarchyDirs, double nowMs, int retentionDays, Func<string> backup, Action<string> log, Func<JsonArray?>? readPeers = null, bool countOnly = false, Ah.Abstractions.ISessionAuthority? sessions = null)
    {
        var closed = new HashSet<string>(StringComparer.Ordinal);
        var open = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            // The open set is what protects a request's peer rows; a message store that cannot be read must not answer "nothing is open".
            using var decisions = hierarchy.Core.DecisionReads();
            var answeredBeforeMs = (long)(nowMs - (retentionDays * 86_400_000d));
            foreach (var dir in hierarchyDirs)
            {
                // ah-msgs-fast-2 §3.8 item 7, ruling Q5: the closed set is a query and the open set comes from the live listing, which holds every open exchange
                closed.UnionWith(hierarchy.Messages.ClosedExchangeIdsAllTeams(dir, answeredBeforeMs));
                open.UnionWith(hierarchy.Messages.ListLiveExchangesAllTeams(dir)
                    .Where(static e => e.Member("open") is { IsTruthy: true }).Select(static e => e.Member("id")?.NonEmptyText).OfType<string>());
            }
        }
        catch (StoreUnavailableException)
        {
            var skipped = $"mode={(countOnly ? "count" : "on")} backend={(hierarchy.Core.PeerRecordsInDb() ? "db" : "files")} msgs_read=failed peer_dropped=0 peer_idle_session_dropped=0 backup=none";
            log("state prune skipped: " + skipped);
            return skipped;
        }

        closed.ExceptWith(open);
        if ((readPeers ?? hierarchy.Core.ReadPeerRecordsKnown)() is null)
        {
            var skipped = $"mode={(countOnly ? "count" : "on")} backend={(hierarchy.Core.PeerRecordsInDb() ? "db" : "files")} peer_read=failed peer_dropped=0 peer_idle_session_dropped=0 backup=none";
            log("state prune skipped: " + skipped);
            return skipped;
        }

        var peerWouldDrop = hierarchy.Peers.CountPeerRecordsToCompact(closed, open, nowMs, retentionDays);
        var idleWouldDrop = peerWouldDrop - hierarchy.Peers.CountPeerRecordsToCompact(closed, open, nowMs, retentionDays, idleRule: false);
        var endedBeforeMs = (long)(nowMs - (EndedSessionDays * 86_400_000d));
        var sessionsWouldDrop = sessions?.PruneEnded(endedBeforeMs, countOnly: true) ?? 0;
        if (countOnly)
        {
            // The counts of the dry pass: no backup, no lock, no deletion. A run that would drop nothing still returns its line so the job shows it.
            var counted = string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"mode=count backend={(hierarchy.Core.PeerRecordsInDb() ? "db" : "files")} peer_would_drop={peerWouldDrop} peer_idle_session_would_drop={idleWouldDrop}")
                + (sessions is null ? string.Empty : $" ended_sessions_would_drop={sessionsWouldDrop}");
            if (peerWouldDrop + sessionsWouldDrop > 0)
            {
                log("state prune: " + counted);
            }

            return counted;
        }

        if (peerWouldDrop == 0 && sessionsWouldDrop == 0)
        {
            return null;
        }

        string backupName;
        try
        {
            backupName = backup();
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            log($"state prune skipped: backup failed: {e.Message}");
            return null;
        }

        var peerDropped = peerWouldDrop == 0 ? 0 : hierarchy.Peers.CompactPeerRecords(closed, open, nowMs, retentionDays);
        var sessionsDropped = sessionsWouldDrop == 0 ? 0 : sessions!.PruneEnded(endedBeforeMs, countOnly: false);
        var line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"mode=on backend={(hierarchy.Core.PeerRecordsInDb() ? "db" : "files")} peer_dropped={peerDropped} peer_idle_session_dropped={idleWouldDrop} backup={backupName}")
            + (sessions is null ? string.Empty : $" ended_sessions_dropped={sessionsDropped}");
        log("state prune: " + line);
        return line;
    }
}
