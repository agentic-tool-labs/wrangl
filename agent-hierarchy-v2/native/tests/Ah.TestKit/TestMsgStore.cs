using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.TestKit;

/// <summary>
/// The message store a test rig reads, as the CLI wires it: a real state database under the rig's AH_HOME, created here as the first
/// message verb creates it (ah-msgs-db-only §13). One helper, so no rig builds its own copy of the route.
/// </summary>
public static partial class TestMsgStore
{
    // the file-name rule of MessageText.ParseFilename, so the store holds exactly the files a files-mode listing would read
    [System.Text.RegularExpressions.GeneratedRegex("/msgs/(archive/)?[0-9]{8}-[0-9]{6}-[0-9a-z]{4}--(" + Ah.Config.ConfigModule.RoleNamePattern + ")--[a-z0-9-]{1,32}--(request|response|note|link-note)\\.md\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex MessagePathRegex();

    /// <summary>Whether <paramref name="path"/> names a message file in a pool's <c>msgs/</c> or <c>msgs/archive/</c>.</summary>
    public static bool IsMessagePath(string path) => MessagePathRegex().IsMatch(path);

    /// <summary>Whether the store of <paramref name="ahHome"/> has a row for the message <paramref name="path"/>.</summary>
    public static bool Holds(string ahHome, string path) => HasRow(ahHome, Row(PoolOf(path), path, archived: IsArchived(path)));

    /// <summary>The msgs route over the state database of <paramref name="ahHome"/>, creating the database when it does not exist yet.</summary>
    public static MsgsRoute Route(string ahHome)
    {
        if (StateDbModule.EnsureDatabase(ahHome) is { } why)
        {
            throw new InvalidOperationException(why);
        }

        return new MsgsRoute(StateDbModule.CreateMsgSource(ahHome), StateDbModule.CreateModeReader(ahHome), () => StateDbModule.DomainFlipped(ahHome, "msgs"));
    }

    /// <summary>
    /// <see cref="Route"/> for a rig whose tests plant, edit and delete message files under <paramref name="root"/> by hand: each time a core asks
    /// the mode, the store is first brought up to the files (<see cref="Sync"/>), as <c>msg new</c>/<c>fill</c> would have stored them.
    /// </summary>
    public static MsgsRoute SyncedRoute(string ahHome, string root)
    {
        var route = Route(ahHome);
        return route with { Modes = new SyncingModes(route.Modes, () => Sync(ahHome, root)) };
    }

    private static readonly AsyncLocal<bool> SyncHeld = new();

    /// <summary>
    /// Brings the store up to the files now, and until disposed this flow's mode reads do not again: for a read-only stretch that plants no file and
    /// asks the mode thousands of times. Each ask lists the whole tree.
    /// </summary>
    public static IDisposable HoldSync(string ahHome, string root)
    {
        Sync(ahHome, root);
        SyncHeld.Value = true;
        return new SyncRelease();
    }

    private sealed class SyncRelease : IDisposable
    {
        public void Dispose() => SyncHeld.Value = false;
    }

    private sealed class SyncingModes(Ah.Abstractions.IMessageModeReader inner, Action sync) : Ah.Abstractions.IMessageModeReader
    {
        public string Read(string domain)
        {
            if (!SyncHeld.Value)
            {
                sync();
            }

            return inner.Read(domain);
        }
    }

    // the planted files Sync stored, by home and path, with the stamp they had then; a row Sync did not write (a real msg new) is never touched.
    // By home, so a sync looks at its own home's files and not at every file every test in the process has planted.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, string>> SyncedHomes = new(StringComparer.Ordinal);

    private static System.Collections.Concurrent.ConcurrentDictionary<string, string> SyncedIn(string ahHome) =>
        SyncedHomes.GetOrAdd(ahHome, static _ => new(StringComparer.Ordinal));

    private static string Stamp(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : string.Empty;
    }

    /// <summary>
    /// Stores every message file under <paramref name="root"/> that has no row, and takes back the row of one it stored before
    /// whose file is gone or changed since (an in-place edit, a delete, a move to the archive), deriving the exchange of each such id again.
    /// </summary>
    public static void Sync(string ahHome, string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        var files = Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Where(IsMessagePath).ToList();
        var synced = SyncedIn(ahHome);
        var stale = synced.Where(e => e.Key.StartsWith(root, StringComparison.Ordinal) && Stamp(e.Key) != e.Value)
            .Select(e => e.Key).ToList();
        var fresh = files.Where(f => !synced.ContainsKey(f) && !Foreign.ContainsKey((ahHome, f))).ToList();
        foreach (var path in stale.Concat(fresh))
        {
            var row = Row(PoolOf(path), path, archived: IsArchived(path));
            if (!stale.Contains(path) && HasRow(ahHome, row))
            {
                Foreign[(ahHome, path)] = 0; // stored by the code under test or by Plant: its owner keeps it
                continue;
            }

            _ = synced.TryRemove(path, out _);
            ForgetRow(ahHome, row);
            Store(ahHome, path); // derives the id's exchange again from the files left, a gone file's included
            if (File.Exists(path))
            {
                synced[path] = Stamp(path);
            }
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string AhHome, string Path), byte> Foreign = new();

    /// <summary>Counts a write in the message store's version, as the op runner does for every <c>msgs</c> op: readers key held listings on it.</summary>
    private const string BumpMsgsSql = "INSERT INTO domain_seq(domain, seq, changed_ms) VALUES ('msgs', 1, 0) ON CONFLICT(domain) DO UPDATE SET seq = seq + 1";

    private static void ForgetRow(string ahHome, MsgFileRow row)
    {
        var written = StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            tx.Execute(BumpMsgsSql);
            tx.Execute("DELETE FROM msg_exchanges WHERE pool_dir = ?1 AND id = ?2", row.PoolDir, row.Id);
            tx.Execute("DELETE FROM msg_bodies WHERE msg_pk IN (SELECT pk FROM msg_messages WHERE pool_dir = ?1 AND id = ?2 AND type = ?3 AND \"to\" = ?4 AND slug = ?5)", MsgRows.KeyArgs(row));
            tx.Execute("DELETE FROM msg_messages WHERE pool_dir = ?1 AND id = ?2 AND type = ?3 AND \"to\" = ?4 AND slug = ?5", MsgRows.KeyArgs(row));
            return 0;
        });
        if (!written.Ok)
        {
            throw new InvalidOperationException(written.Message);
        }
    }

    private static bool IsArchived(string path) => Path.GetFileName(Path.GetDirectoryName(path)) == "archive";

    private static string PoolOf(string path)
    {
        var msgs = Path.GetDirectoryName(path)!;
        return Path.GetDirectoryName(IsArchived(path) ? Path.GetDirectoryName(msgs)! : msgs)!;
    }

    private static bool HasRow(string ahHome, MsgFileRow row)
    {
        var found = false;
        var read = StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            found = tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(row)).Count > 0;
            return 0;
        });
        return read.Ok ? found : throw new InvalidOperationException(read.Message);
    }

    /// <summary>
    /// Writes the message file <paramref name="path"/> (under a pool's <c>msgs/</c> or <c>msgs/archive/</c>) and stores it as <see cref="Store(string, string)"/> does.
    /// For a test that plants a message as a file: the store sees no message it was not told about.
    /// </summary>
    public static string Plant(string ahHome, string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        Store(ahHome, path);
        return path;
    }

    /// <summary><see cref="Plant(string, string, string)"/> into the database of the sandbox that holds <paramref name="path"/> (<see cref="HomeOf"/>).</summary>
    public static string Plant(string path, string text) => Plant(HomeOf(path), path, text);

    /// <summary><see cref="Store(string, string)"/> into the database of the sandbox that holds <paramref name="path"/> (<see cref="HomeOf"/>).</summary>
    public static void Store(string path) => Store(HomeOf(path), path);

    /// <summary>
    /// The AH_HOME whose state database a rig built over the sandbox holding <paramref name="path"/>: the nearest directory above it with
    /// <c>home/.claude/ah2</c>, <c>ah</c> or <c>.claude/ah2</c> holding a database that <see cref="Route"/> created.
    /// </summary>
    // ponytail: found by the sandbox layouts the rigs use (UnitSandbox, Cli Sandbox, HooksHost); a rig with another AH_HOME passes it to Plant/Store
    public static string HomeOf(string path)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(path)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            foreach (var home in new[] { Path.Combine(dir, "home", ".claude", "ah2"), Path.Combine(dir, "ah"), Path.Combine(dir, ".claude", "ah2") })
            {
                if (File.Exists(Path.Combine(home, "state", "ah.db")))
                {
                    return home;
                }
            }
        }

        throw new InvalidOperationException($"no rig state database above {path}; build the rig (TestMsgStore.Route) before planting");
    }

    /// <summary>
    /// Stores the message files of <paramref name="path"/>'s id in its pool, already written, as <c>msg new</c> stores a message not sent yet:
    /// a draft row holding the file's bytes as revision 1, and the exchange of the id derived again from them.
    /// A message already stored keeps its row; a body stored here follows the file on the next call.
    /// </summary>
    public static void Store(string ahHome, string path)
    {
        var msgs = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(msgs) == "archive")
        {
            msgs = Path.GetDirectoryName(msgs)!;
        }

        var pool = Path.GetDirectoryName(msgs)!;
        var id = Path.GetFileName(path).Split("--")[0];
        var archive = Path.Combine(msgs, "archive");
        var files = Directory.EnumerateFiles(msgs, id + "--*.md").Where(IsMessagePath).Select(f => (Row: Row(pool, f, archived: false), File: f))
            .Concat(Directory.Exists(archive) ? Directory.EnumerateFiles(archive, id + "--*.md").Where(IsMessagePath).Select(f => (Row: Row(pool, f, archived: true), File: f)) : [])
            .ToList();
        var rows = files.ConvertAll(f => f.Row);
        var bodies = files.ConvertAll(f => (f.Row, Bytes: File.ReadAllBytes(f.File), MtimeMs: new DateTimeOffset(File.GetLastWriteTimeUtc(f.File)).ToUnixTimeMilliseconds()))
            .FindAll(b => b.Bytes.Length <= MsgRows.BodyCap);
        if (StateDbModule.EnsureDatabase(ahHome) is { } why)
        {
            throw new InvalidOperationException(why);
        }

        var written = StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            tx.Execute(BumpMsgsSql);
            foreach (var row in rows)
            {
                tx.Execute(MsgRows.InsertMessageSql, MsgRows.MessageArgs(row));
            }

            long Pk(MsgFileRow r) => (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(r))[0][0]!;
            foreach (var (row, bytes, mtimeMs) in bodies)
            {
                // a body the code under test wrote stays; one planted here follows the file
                tx.Execute(
                    "INSERT INTO msg_bodies(msg_pk, rev, bytes, sha256, sealed_at_ms, sealed_by) VALUES (?1, 1, ?2, ?3, ?4, 'plant') ON CONFLICT(msg_pk, rev) DO UPDATE SET bytes = excluded.bytes, sha256 = excluded.sha256 WHERE sealed_by = 'plant'",
                    Pk(row),
                    bytes,
                    MsgRows.Sha256Hex(bytes),
                    mtimeMs);
                tx.Execute("UPDATE msg_messages SET body_rev = 1 WHERE pk = ?1 AND body_rev IS NULL", Pk(row));
            }

            tx.Execute("DELETE FROM msg_exchanges WHERE pool_dir = ?1 AND id = ?2", MsgRows.PoolKey(pool), id);
            foreach (var pair in MsgRows.Pair(rows))
            {
                tx.Execute(MsgRows.InsertExchangeSql, MsgRows.ExchangeArgs(pair, Pk(pair.Request), pair.Response is null ? null : Pk(pair.Response), null));
            }

            return 0;
        });
        if (!written.Ok)
        {
            throw new InvalidOperationException(written.Message);
        }
    }

    /// <summary>
    /// Stores every message file under <paramref name="root"/> and deletes the files, leaving only the rows: what a store holds in db mode.
    /// A read that still goes to a file finds nothing.
    /// </summary>
    public static void DropFiles(string ahHome, string root)
    {
        Sync(ahHome, root);
        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories).Where(IsMessagePath).ToList())
        {
            File.Delete(file);
            _ = SyncedIn(ahHome).TryRemove(file, out _);
            Foreign[(ahHome, file)] = 0;
        }
    }

    // what MirrorOut last wrote, by path, with its stamp: a later change to one is the test's own edit
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string AhHome, string Path), string> Mirrored = new();

    /// <summary>
    /// Before a product run, for a test that plants, edits and reads messages as files: what the test wrote since <see cref="MirrorOut"/> goes
    /// into the store, then every message file is deleted, so the product can only read the store (ah-msgs-db-only §13).
    /// </summary>
    public static void MirrorIn(string ahHome, string root)
    {
        if (BrokenOnPurpose(ahHome))
        {
            return;
        }

        var mirrored = Mirrored.Where(e => e.Key.AhHome == ahHome).ToList();
        foreach (var ((_, path), _) in mirrored.Where(e => !File.Exists(e.Key.Path)))
        {
            Delete(ahHome, path);
        }

        foreach (var ((_, path), stamp) in mirrored.Where(e => File.Exists(e.Key.Path) && Stamp(e.Key.Path) != e.Value))
        {
            ReplaceBody(ahHome, path);
        }

        foreach (var ((_, path), _) in mirrored)
        {
            File.Delete(path);
            _ = Mirrored.TryRemove((ahHome, path), out _);
        }

        DropFiles(ahHome, root);
    }

    /// <summary>After a product run: every stored message's newest revision is written out at its key's path, for the test to read. Nothing in the product reads these files.</summary>
    public static void MirrorOut(string ahHome)
    {
        if (BrokenOnPurpose(ahHome))
        {
            return;
        }

        using var reader = StateDbModule.OpenReader(ahHome).Reader!;
        foreach (var row in reader.Query("SELECT m.pool_dir, m.id, m.\"to\", m.slug, m.type, m.state, b.bytes FROM msg_messages m JOIN msg_bodies b ON b.msg_pk = m.pk AND b.rev = m.body_rev"))
        {
            var msgs = Path.Combine((string)row[0]!, "msgs");
            if ((string)row[5]! == "archived")
            {
                msgs = Path.Combine(msgs, "archive");
            }

            Directory.CreateDirectory(msgs);
            var path = Path.Combine(msgs, $"{row[1]}--{row[2]}--{row[3]}--{row[4]}.md");
            File.WriteAllBytes(path, (byte[])row[6]!);
            Mirrored[(ahHome, path)] = Stamp(path);
        }
    }

    // a test that writes garbage over ah.db is testing the refusal, and there is nothing to mirror
    private static bool BrokenOnPurpose(string ahHome)
    {
        var db = Path.Join(ahHome, "state", "ah.db");
        return File.Exists(db) && !File.ReadAllBytes(db).AsSpan().StartsWith("SQLite format 3"u8);
    }

    private static void ReplaceBody(string ahHome, string path)
    {
        var bytes = File.ReadAllBytes(path);
        var row = Row(PoolOf(path), path, IsArchived(path));
        var written = StateDbModule.OpenDirect(ahHome).Write(tx =>
        {
            var pk = (long)tx.Query(MsgRows.SelectPkSql, MsgRows.KeyArgs(row))[0][0]!;
            tx.Execute(BumpMsgsSql);
            tx.Execute("UPDATE msg_bodies SET bytes = ?1, sha256 = ?2 WHERE msg_pk = ?3 AND rev = (SELECT body_rev FROM msg_messages WHERE pk = ?3)", bytes, MsgRows.Sha256Hex(bytes), pk);
            return 0;
        });
        if (!written.Ok)
        {
            throw new InvalidOperationException(written.Message);
        }
    }

    /// <summary>Deletes the message file <paramref name="path"/> and its row, and derives its id's exchange again from what is left.</summary>
    public static void Delete(string ahHome, string path)
    {
        var row = Row(PoolOf(path), path, IsArchived(path));
        File.Delete(path);
        ForgetRow(ahHome, row);
        Store(ahHome, path);
    }

    private static MsgFileRow Row(string pool, string file, bool archived)
    {
        var name = Path.GetFileName(file);
        var parts = name[..^3].Split("--");
        var fm = File.Exists(file) ? MessageFront.ParseFrontmatter(File.ReadAllText(file))?.Fields : null; // a gone file still names its key
        return MsgFileRows.FromFrontMatter(
            pool, file, name, parts[0], parts[^1], parts[1], string.Join("--", parts[2..^1]), fm is null ? default(JsonValue?) : JsonValue.FromObject(fm), archived, MsgRows.Draft, null);
    }

    /// <summary>
    /// <see cref="Route"/> with an explicit <c>mode.msgs = files</c> row, the only way files mode is reached until D4 deletes it: for a test
    /// whose subject is the files side (a files-versus-database parity check).
    /// </summary>
    public static MsgsRoute FilesRoute(string ahHome)
    {
        var route = Route(ahHome);
        var written = StateDbModule.OpenDirect(ahHome).Write(static tx =>
        {
            tx.Execute("INSERT OR REPLACE INTO meta(key, value) VALUES ('mode.msgs', 'files')");
            return 0;
        });
        return written.Ok ? route : throw new InvalidOperationException(written.Message);
    }
}
