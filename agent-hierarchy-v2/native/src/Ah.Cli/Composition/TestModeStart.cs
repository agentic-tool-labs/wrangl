using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Mesh;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>
/// Whether a test-mode daemon may start: not in the real home, and not with a registration, a database row or a known dir naming a
/// path outside its probe root. The scan reads only, and is asked by the daemon before any job and by the client before a spawn.
/// Paths added after start are not seen here. Outside test mode it answers null at once. A daemon that passes then moves its multiplexers
/// inside the root.
/// </summary>
internal static class TestModeStart
{
    private const int Named = 3;

    private static readonly string[] DirQueries =
    [
        "SELECT DISTINCT hier_dir FROM plan_events",
        "SELECT DISTINCT pool_dir FROM status_entries WHERE pool_dir <> ''",
        "SELECT DISTINCT pool_dir FROM msg_messages",
        $"SELECT substr(key, {AhPaths.KnownDirKeyPrefix.Length + 1}) FROM meta WHERE substr(key, 1, {AhPaths.KnownDirKeyPrefix.Length}) = '{AhPaths.KnownDirKeyPrefix}'",
    ];

    /// <summary>The one-line refusal, or null when the daemon may start.</summary>
    internal static string? Refusal(IAhEnvironment environment, IFileReader files)
    {
        if (!environment.TestMode)
        {
            return null;
        }

        if (environment.TestModeInRealHome)
        {
            return $"ahd: test mode with HOME={environment.HomeDirectory}, the real home; refusing to start. Set HOME to a temp dir, and copy the real home into it for real data.";
        }

        var (databaseDirs, unusable) = Database(environment.AhHome, files);
        if (unusable is not null)
        {
            return $"ahd: test mode with an unusable state database: {unusable}; refusing to start. Copy state/ again, or leave it out of the copy.";
        }

        var root = TestModeDirGate.RootOf(environment, files);
        var outside = Registrations(environment.AhHome, files).Concat(databaseDirs).Where(p => !TestModeDirGate.IsInside(files, root, p)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (outside.Count == 0)
        {
            return null;
        }

        return $"ahd: test mode with {outside.Count} path(s) outside the probe root {root}: {string.Join(", ", outside.Take(Named))}; refusing to start. Rewrite or strip them in the copy.";
    }

    /// <summary>
    /// Points this test-mode daemon, and everything it runs, at tmux and herdr sockets inside its probe root, so no call of it can reach a
    /// real pane. Answers a refusal when that cannot be done; outside test mode it changes nothing. tmux prefers an inherited <c>TMUX</c> to
    /// <c>TMUX_TMPDIR</c> and goes back to <c>/tmp</c> when that directory is missing, and <c>herdr session list</c> reads
    /// <c>XDG_CONFIG_HOME</c>, so each of those is settled here too.
    /// </summary>
    internal static string? PrivateMultiplexers(IAhEnvironment environment, IFileReader files, IReadOnlyDictionary<string, string?> current, Action<string, string?> set)
    {
        if (!environment.TestMode)
        {
            return null;
        }

        if (TestModeDirGate.RootOf(environment, files) is not { } root)
        {
            return $"ahd: test mode with no probe root above {environment.AhHome}; refusing to start. Multiplexer sockets could not be kept inside it.";
        }

        var config = root + "/config";
        var wanted = new Dictionary<string, string?>(Ah.Core.Mux.HerdrEnvironment.ForInstance(current, config + "/herdr/herdr.sock"), StringComparer.Ordinal)
        {
            ["TMUX_TMPDIR"] = root + "/tmux",
            ["XDG_CONFIG_HOME"] = config,
        };
        _ = wanted.Remove("TMUX");
        _ = wanted.Remove("TMUX_PANE");
        try
        {
            _ = Directory.CreateDirectory(root + "/tmux");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"ahd: test mode cannot make {root}/tmux for a private tmux server: {e.Message}; refusing to start.";
        }

        foreach (var name in current.Keys.Where(n => !wanted.ContainsKey(n)))
        {
            set(name, null);
        }

        foreach (var (name, value) in wanted.Where(kv => current.GetValueOrDefault(kv.Key) != kv.Value))
        {
            set(name, value);
        }

        return null;
    }

    private static IEnumerable<string> Registrations(string ahHome, IFileReader files)
    {
        var directory = NodePath.Join(ahHome, "registrations");
        IEnumerable<string> names;
        try
        {
            names = [.. files.List(directory)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var name in names.Where(static n => n.EndsWith(".json", StringComparison.Ordinal)))
        {
            if (ParsedMap(files, NodePath.Join(directory, name)) is { } registration)
            {
                foreach (var path in RegistrationPaths(registration))
                {
                    yield return path;
                }
            }
        }
    }

    private static IEnumerable<string> RegistrationPaths(JsonObject registration)
    {
        foreach (var file in MeshModule.RegistrationTeamFiles(registration))
        {
            yield return file;
        }

        if (registration.Get("team_file") is { IsText: true } single && single.AsString().Length > 0)
        {
            yield return single.AsString();
        }

        foreach (var box in MeshModule.RegistrationBoxes(registration))
        {
            yield return box.Dir;
        }
    }

    /// <summary>The registration documents the store holds since b142, placed or not.</summary>
    private const string StoredRegistrations = "SELECT doc FROM sessions WHERE doc IS NOT NULL UNION ALL SELECT doc FROM sessions_unplaced WHERE doc IS NOT NULL";

    private static IEnumerable<string> StoredRegistrationPaths(IStateDbReader reader)
    {
        foreach (var row in Rows(reader, StoredRegistrations))
        {
            var text = row[0] switch
            {
                string s => s,
                byte[] b => System.Text.Encoding.UTF8.GetString(b),
                _ => null,
            };
            JsonValue? doc = null;
            try
            {
                doc = text is null ? null : (JsonValue?)JsonText.Parse(text);
            }
            catch (JsonParseException)
            {
            }

            if (doc is { IsMap: true } map)
            {
                foreach (var path in RegistrationPaths(map.AsObject()))
                {
                    yield return path;
                }
            }
        }
    }

    private static JsonObject? ParsedMap(IFileReader files, string path)
    {
        try
        {
            return JsonText.Parse(files.ReadText(path)) is { IsMap: true } doc ? doc.AsObject() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return null;
        }
    }

    /// <summary>
    /// The dirs the database names, or why it cannot be judged. Starting the daemon can restore a backup holding rows this never saw,
    /// so a database that is present and unreadable, absent beside backups, or not the one the marker names beside backups, is a refusal
    /// and not an empty answer.
    /// </summary>
    private static (List<string> Dirs, string? Unusable) Database(string ahHome, IFileReader files)
    {
        var dirs = new List<string>();
        var state = NodePath.Join(ahHome, "state");
        if (StateDbModule.StoreNeedsRecovery(ahHome) && HasBackups(NodePath.Join(state, "backups"), files))
        {
            return (dirs, "the authority marker says state/ah.db is gone or replaced, and state/backups could restore it");
        }

        if (!files.Exists(NodePath.Join(state, "ah.db")))
        {
            return (dirs, HasBackups(NodePath.Join(state, "backups"), files) ? "state/ah.db is missing and state/backups could restore it" : null);
        }

        var opened = StateDbModule.OpenReader(ahHome);
        if (opened.Reader is not { } reader)
        {
            return (dirs, opened.Message);
        }

        using (reader)
        {
            try
            {
                foreach (var sql in DirQueries)
                {
                    dirs.AddRange(Rows(reader, sql).Select(static row => row[0]).OfType<string>().Where(static d => d.Length > 0));
                }

                dirs.AddRange(StoredRegistrationPaths(reader));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return (dirs, e.Message);
            }
        }

        return (dirs, null);
    }

    private static bool HasBackups(string directory, IFileReader files)
    {
        try
        {
            return files.List(directory).Any(static n => n.EndsWith(".db", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // A copy from before a table or column existed has no rows to name in it; any other query failure is the caller's to refuse.
    private static IReadOnlyList<object?[]> Rows(IStateDbReader reader, string sql)
    {
        try
        {
            return reader.Query(sql);
        }
        catch (Exception e) when (e.Message.Contains("no such table", StringComparison.Ordinal) || e.Message.Contains("no such column", StringComparison.Ordinal))
        {
            return [];
        }
    }
}
