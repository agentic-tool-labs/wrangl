using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.StateDb;

/// <summary>
/// The one import pass of a hierarchy dir's leftover files (files-off-disk §3, adr-19): it calls each group's existing take, records per group
/// what it took, and stores the record under <c>files.import:&lt;realpath&gt;</c>. It never refuses, deletes or renames anything.
/// </summary>
internal static class StateDbFilesImport
{
    /// <summary>The pass version: a slice that adds a group bumps it, so every dir runs once more.</summary>
    internal const int PassVersion = 2;

    internal static string MetaKey(string hierDir) => "files.import:" + StateDbTeamKey.RealPath(hierDir);

    /// <summary>
    /// The dirs the pass runs over without <c>--dir</c>: <paramref name="listed"/> (registrations and known dirs), the session team files'
    /// dirs and <c>sessions.hier_dir</c>, deduped by real path, the first spelling kept; a dir that no longer exists is left out.
    /// </summary>
    internal static IReadOnlyList<string> Dirs(string ahHome, IEnumerable<string> listed)
    {
        IEnumerable<string> sessions = [];
        if (!StateDbAuthority.NeverCreated(ahHome))
        {
            try
            {
                sessions = StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT DISTINCT hier_dir FROM sessions WHERE hier_dir IS NOT NULL AND hier_dir <> ''", [])
                    .Select(static r => (string)r[0]!).ToList());
            }
            catch (Exception e) when (e is StoreUnavailableException or InvalidOperationException)
            {
                // an unreadable database lists no session dirs; the others still run
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return [.. listed.Concat(StateDbModule.RegisteredHierarchyDirs(ahHome)).Concat(sessions)
            .Where(static d => d.Length > 0 && Directory.Exists(d))
            .Where(d => seen.Add(MsgRows.PoolKey(d)))];
    }

    /// <summary>
    /// Runs the pass over <paramref name="hierDir"/> and returns the record it stored; null when the dir is gone, the database was never
    /// created, or (without <paramref name="force"/>) the dir's record is already complete.
    /// </summary>
    internal static string? Import(string ahHome, string hierDir, bool force, IEnumerable<(string Name, Func<JsonObject> Run)>? moreGroups = null)
    {
        if (!Directory.Exists(hierDir) || StateDbAuthority.NeverCreated(ahHome) || (!force && Complete(ahHome, hierDir)))
        {
            return null;
        }

        var groups = new JsonObject();
        foreach (var (name, run) in Groups(ahHome, hierDir).Concat(moreGroups ?? []))
        {
            try
            {
                groups[name] = run();
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
            {
                // any take's failure is this group's alone: recorded, and the pass goes on (§3.2 rules)
                groups[name] = new JsonObject { ["failed"] = e.Message };
            }
        }

        var record = JsonText.Stringify(new JsonObject { ["v"] = PassVersion, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["groups"] = groups }, 0);
        var stored = new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
        {
            tx.Execute("INSERT INTO meta(key, value) VALUES (?1, ?2) ON CONFLICT(key) DO UPDATE SET value = excluded.value", MetaKey(hierDir), record);
            return 0;
        });

        // an unstored record leaves the dir incomplete, so the next start or admit runs it again
        return stored.Ok ? record : null;
    }

    /// <summary>Whether the dir's record is at this pass version or later and names no failed group.</summary>
    internal static bool Complete(string ahHome, string hierDir)
    {
        try
        {
            var found = StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT value FROM meta WHERE key = ?1", [MetaKey(hierDir)]));
            return found is [[string text]] && JsonText.Parse(text) is { IsMap: true } doc
                && doc.Member("v") is { IsNumber: true } v && v.AsNumber() >= PassVersion
                && doc.Member("groups") is { IsMap: true } groups && groups.AsObject() is var g && g.Keys.All(k => g[k].Member("failed") is null);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return false;
        }
    }

    private static IEnumerable<(string Name, Func<JsonObject> Run)> Groups(string ahHome, string h)
    {
        IEnumerable<string> Files(string dir, string pattern) => Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern) : [];
        IEnumerable<string> Teams() => Directory.Exists(Path.Join(h, "progress")) ? Directory.EnumerateDirectories(Path.Join(h, "progress")).Select(Path.GetFileName).OfType<string>() : [];
        IEnumerable<string> IfDir(string dir, string key) => Directory.Exists(dir) ? [key] : [];
        IEnumerable<string> IfFile(string file, string key) => File.Exists(file) ? [key] : [];

        var asks = new StateDbAsks(ahHome);
        var pipeline = Path.Join(h, "pipeline");
        yield return ("decisions", () => FilesMode(ahHome, "decisions") ?? ByKeys(ahHome,
            [.. (Directory.Exists(pipeline) ? Directory.EnumerateDirectories(pipeline) : []).Select(r => Path.Join(r, "decisions.jsonl")).Where(File.Exists).Select(StateDbDecisions.TakenKey)],
            () => new StateDbDecisions(ahHome).TakeRuns(h)));
        yield return ("asks", () => FilesMode(ahHome, "asks") ?? ByKeys(ahHome,
            [.. StateDbAsks.Logs.Select(l => Path.Join(h, l)).Where(File.Exists).Select(StateDbAsks.TakenKey)],
            () =>
            {
                foreach (var log in StateDbAsks.Logs)
                {
                    _ = asks.TakeFile(Path.Join(h, log));
                }
            }));
        yield return ("grants", () => ByKeys(ahHome, [StateDbGrants.TakenKey(h)], () => new StateDbGrants(ahHome, string.Empty).Take(h)));
        yield return ("gates", () => ByKeys(ahHome, [.. IfFile(Path.Join(h, "gates.jsonl"), StateDbGates.TakenKey(Path.Join(h, "gates.jsonl")))], () => new StateDbGates(ahHome).Take(h)));
        yield return ("jobs", () => ByKeys(ahHome, [.. IfDir(Path.Join(h, "jobs"), StateDbJobs.TakenKey(Path.Join(h, "jobs")))], () => new StateDbJobs(ahHome).Take(h)));
        yield return ("peers", () => ByKeys(ahHome, [.. IfFile(Path.Join(h, "peers.jsonl"), StateDbRosterRecords.TakenKey(Path.Join(h, "peers.jsonl")))], () => new StateDbRosterRecords(ahHome).Take(h)));
        yield return ("qa-log", () => ByKeys(ahHome, [.. IfFile(Path.Join(h, "qa-log.jsonl"), StateDbQaLog.TakenKey(Path.Join(h, "qa-log.jsonl")))], () => new StateDbQaLog(ahHome).Take(h)));
        yield return ("checkpoints", () => ByKeys(ahHome, [.. IfDir(Path.Join(h, "checkpoints"), StateDbCheckpoints.TakenKey(h))], () => new StateDbCheckpoints(ahHome).Take(h)));

        // ponytail: holds are taken with the dir as its own team home; a team home that is in no dir list is not reached until it is listed or run with --dir
        yield return ("holds", () => ByKeys(ahHome, [.. IfDir(Path.Join(h, "holds"), StateDbHolds.TakenKey(h))], () => new StateDbHolds(ahHome).Take(h)));
        yield return ("progress-logs", () => ByKeys(ahHome,
            [.. Teams().SelectMany(t => Files(Path.Join(h, "progress", t), "*").Where(f => StateDbProgressLogs.IsLog(Path.GetFileName(f))).Select(StateDbProgressLogs.TakenKey))],
            () =>
            {
                var logs = new StateDbProgressLogs(ahHome);
                foreach (var team in Teams())
                {
                    logs.Take(h, team);
                }
            }));
        yield return ("progress-docs", () => ByKeys(ahHome, [.. Teams().Select(t => StateDbProgressDocs.TakenKey(h, t))], () =>
        {
            var docs = new StateDbProgressDocs(ahHome);
            foreach (var team in Teams())
            {
                docs.Take(h, team);
            }
        }));
        yield return ("links", () => ByKeys(ahHome, [.. Files(Path.Join(h, "links"), "*.json").Select(StateDbLinks.TakenKey)], () =>
        {
            var links = new StateDbLinks(ahHome);
            foreach (var file in Files(Path.Join(h, "links"), "*.json"))
            {
                _ = links.Take(h, Path.GetFileNameWithoutExtension(file));
            }
        }));
        yield return ("teams", () => TeamFiles(ahHome, [.. Files(Path.Join(h, "teams"), "*.json"), .. File.Exists(Path.Join(h, "team.json")) ? [Path.Join(h, "team.json")] : Array.Empty<string>()]));

        // files-off-disk §12.5, per team home as holds are (the dir itself): verbatim text, a row already there is kept, no pruning
        var historyFile = Path.Join(h, "team-history.json");
        yield return ("team-history", () => TakeFiles(ahHome, "team-history", File.Exists(historyFile) ? [(historyFile, (string?)null, (string?)null)] : [], h));
        yield return ("team-snapshots", () => TakeFiles(
            ahHome,
            "team-snapshots",
            [.. Files(Path.Join(h, "snapshots"), "*.json").Select(f => (File: f, Stamp: SnapshotNameOf(Path.GetFileName(f)))).Where(static s => s.Stamp is not null).Select(static s => (s.File, (string?)s.Stamp!.Value.Team, (string?)s.Stamp!.Value.Ts))],
            h));
    }

    /// <summary>The team and stamp of a snapshot file name <c>&lt;team&gt;-&lt;yyyyMMdd&gt;T&lt;9 digits&gt;Z.json</c>, the name RosterCore.SnapshotStampOf accepts; else null.</summary>
    internal static (string Team, string Ts)? SnapshotNameOf(string file)
    {
        if (file.Length < 26 || !file.EndsWith(".json", StringComparison.Ordinal) || file[^25] != '-')
        {
            return null;
        }

        var ts = file[^24..^5];
        var stamped = ts[8] == 'T' && ts[18] == 'Z' && ts.Where((c, i) => i is not 8 and not 18).All(char.IsAsciiDigit);
        return stamped ? (file[..^25], ts) : null;
    }

    /// <summary>
    /// The team-history or team-snapshots take (§12.5): each file with no <c>&lt;group&gt;.taken:</c> key is stored verbatim at its key
    /// unless a row is already there, and its key is set in the same transaction. A file taken before is skipped.
    /// </summary>
    private static JsonObject TakeFiles(string ahHome, string group, IReadOnlyList<(string File, string? Team, string? Ts)> files, string teamHome)
    {
        int rows = 0, skipped = 0;
        var home = StateDbTeamKey.RealPath(teamHome);
        foreach (var (file, team, ts) in files)
        {
            var key = group + ".taken:" + StateDbTeamKey.RealPath(file);
            if (Taken(ahHome, key))
            {
                skipped++;
                continue;
            }

            var text = File.ReadAllText(file);
            var at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var stored = new StateDbDirect(ahHome, DirectWriteOptions.Default).Write(tx =>
            {
                if (team is null)
                {
                    tx.Execute("INSERT OR IGNORE INTO team_history(home, body, updated_ms) VALUES (?1, ?2, ?3)", home, text, at);
                }
                else
                {
                    tx.Execute("INSERT OR IGNORE INTO team_snapshots(home, file, team, ts, body, created_ms) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", home, Path.GetFileName(file), team, ts!, text, at);
                }

                var inserted = ((StateDbTx)tx).Connection.Changes > 0;
                tx.Execute("INSERT OR IGNORE INTO meta(key, value) VALUES (?1, ?2)", key, at.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return inserted;
            });
            if (!stored.Ok)
            {
                throw new InvalidOperationException(stored.Message);
            }

            if (stored.Value)
            {
                rows++;
            }
            else
            {
                skipped++;
            }
        }

        return new JsonObject { ["rows"] = rows, ["skipped"] = skipped };
    }

    /// <summary>
    /// Runs <paramref name="trigger"/> (the group's existing take) when any of <paramref name="keys"/> is untaken: a key taken before is
    /// skipped, one taken now is a row, and one still untaken after the take fails the group so the dir runs again.
    /// </summary>
    private static JsonObject ByKeys(string ahHome, IReadOnlyList<string> keys, Action trigger)
    {
        var before = keys.Where(k => Taken(ahHome, k)).ToHashSet(StringComparer.Ordinal);
        if (before.Count < keys.Count)
        {
            trigger();
        }

        var untaken = keys.Where(k => !before.Contains(k) && !Taken(ahHome, k)).ToList();
        return untaken.Count > 0
            ? new JsonObject { ["failed"] = "not taken: " + string.Join(", ", untaken) }
            : new JsonObject { ["rows"] = keys.Count - before.Count, ["skipped"] = before.Count };
    }

    /// <summary>The team files' take: a file taken now is a row; one already taken, stamped, or whose team already has a row is skipped, as the store's own take does.</summary>
    private static JsonObject TeamFiles(string ahHome, IReadOnlyList<string> files)
    {
        int rows = 0, skipped = 0;
        using var connection = SqliteConnection.Open(StateDbBackups.DatabasePath(ahHome), SqliteOpenMode.ReadWrite, 250);
        foreach (var file in files)
        {
            if (StateDbStoreImport.ImportMissedTeam(connection, file, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) is null)
            {
                skipped++;
            }
            else
            {
                rows++;
            }
        }

        return new JsonObject { ["rows"] = rows, ["skipped"] = skipped };
    }

    /// <summary>
    /// While <c>mode.&lt;group&gt;</c> is not the database's, the group's files are still written, so they are not taken: the group is left
    /// incomplete, and a pass after the mode is <c>db</c> takes them. Null when the group reads the database (no mode row, as the engine reads it).
    /// </summary>
    private static JsonObject? FilesMode(string ahHome, string group) =>
        StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT value FROM meta WHERE key = ?1", ["mode." + group])) is [[string mode]] && mode != StateDbMessageMode.Db
            ? new JsonObject { ["failed"] = $"mode.{group} is {mode}: not taken" }
            : null;

    private static bool Taken(string ahHome, string key) =>
        StateDbStoreReads.Reading(ahHome, c => StateDbRows.Run(c, "SELECT 1 FROM meta WHERE key = ?1", [key]).Count > 0);
}
