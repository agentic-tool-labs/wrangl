namespace Ah.Abstractions;

public enum FileKind
{
    File,
    Directory,
    Symlink,
    Other,
}

public readonly record struct FileStat(FileKind Kind, long Size, double MtimeMs, ulong Inode, uint Mode, uint Uid, ulong Device = 0, long MtimeNs = 0, long CtimeNs = 0);

/// <summary>
/// One read of an asks log (§6c G5). Its lines come only through <see cref="TryLines"/>, which is false, with no lines, when the log
/// could not be read: a reader cannot see a partial log without first being told it is one.
/// </summary>
public sealed record AskLog
{
    private readonly IReadOnlyList<string>? _lines;

    private AskLog(IReadOnlyList<string>? lines) => _lines = lines;

    /// <summary>A log that could not be read.</summary>
    public static AskLog Unreadable { get; } = new((IReadOnlyList<string>?)null);

    /// <summary>A log read whole: its non-blank lines in order.</summary>
    public static AskLog Of(IReadOnlyList<string> lines) => new(lines ?? throw new ArgumentNullException(nameof(lines)));

    /// <summary>The log's lines when it was read; false, and no lines, when it could not be.</summary>
    public bool TryLines(out IReadOnlyList<string> lines)
    {
        lines = _lines ?? [];
        return _lines is not null;
    }
}

/// <summary>One team's grants document as stored, its text byte for byte, and the latest audit row that changed it (null when there is none).</summary>
public sealed record GrantDocument(string Team, string Body, string? AuditRow);

/// <summary>
/// One read of a hierarchy dir's grants (§6c G5, S11e): every team's document with its latest document audit row. The documents come
/// only through <see cref="TryDocuments"/>, which is false, with none, when the store could not be read: never a partial view.
/// </summary>
public sealed record GrantsLog
{
    private readonly IReadOnlyList<GrantDocument>? _documents;

    private GrantsLog(IReadOnlyList<GrantDocument>? documents) => _documents = documents;

    /// <summary>Grants that could not be read.</summary>
    public static GrantsLog Unreadable { get; } = new((IReadOnlyList<GrantDocument>?)null);

    /// <summary>Grants read whole: each team's document, by team.</summary>
    public static GrantsLog Of(IReadOnlyList<GrantDocument> documents) => new(documents ?? throw new ArgumentNullException(nameof(documents)));

    /// <summary>The documents when they were read; false, and none, when they could not be.</summary>
    public bool TryDocuments(out IReadOnlyList<GrantDocument> documents)
    {
        documents = _documents ?? [];
        return _documents is not null;
    }
}

/// <summary>
/// The default body of <see cref="IFileReader.ReadGrants"/> (per-dir-files-to-db S11e): <c>&lt;H&gt;/grants/*.json</c> and the audit log,
/// read as files. A missing grants dir is readable and empty; an I/O or access failure on it or a document is unreadable. The audit log is
/// read from its end, the window widening until every team has its row or the start is reached; an audit log that cannot be read gives
/// no rows, so a document that records a hash reads as inconsistent, as before.
/// </summary>
internal static class GrantFiles
{
    private const int AuditTailBytes = 64 * 1024;

    internal static GrantsLog Read(IFileReader files, string hierarchyDir, string auditPath)
    {
        var dir = hierarchyDir.TrimEnd('/') + "/grants";
        var texts = new List<(string Team, string Text)>();
        try
        {
            if (!files.Exists(dir))
            {
                return GrantsLog.Of([]);
            }

            foreach (var name in files.List(dir).Where(static n => n.EndsWith(".json", StringComparison.Ordinal)))
            {
                try
                {
                    texts.Add((name[..^".json".Length], files.ReadText(dir + "/" + name)));
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    // removed since the listing: no document
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return GrantsLog.Unreadable;
        }

        var rows = texts.Count == 0 ? [] : LastDocumentRows(files, auditPath, [.. texts.Select(static t => t.Team)]);
        return GrantsLog.Of([.. texts.Select(t => new GrantDocument(t.Team, t.Text, rows.GetValueOrDefault(t.Team)))]);
    }

    private static Dictionary<string, string> LastDocumentRows(IFileReader files, string path, HashSet<string> teams)
    {
        for (var window = AuditTailBytes; ; window = checked(window * 4))
        {
            FileTail tail;
            try
            {
                tail = files.ReadTail(path, window);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return [];
            }

            var text = System.Text.Encoding.UTF8.GetString(tail.Bytes);
            if (tail.StartsMidFile)
            {
                var cut = text.IndexOf('\n', StringComparison.Ordinal);
                text = cut < 0 ? string.Empty : text[(cut + 1)..];
            }

            var rows = LastDocumentRowsOf(text, teams);
            if (rows.Count == teams.Count || !tail.StartsMidFile)
            {
                return rows;
            }
        }
    }

    /// <summary>Per team, the last line that is a record with <c>"doc": true</c> and that team's <c>team</c>.</summary>
    private static Dictionary<string, string> LastDocumentRowsOf(string text, HashSet<string> teams)
    {
        var last = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                using var row = System.Text.Json.JsonDocument.Parse(line);
                if (row.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && row.RootElement.TryGetProperty("doc", out var doc) && doc.ValueKind == System.Text.Json.JsonValueKind.True
                    && row.RootElement.TryGetProperty("team", out var team) && team.ValueKind == System.Text.Json.JsonValueKind.String
                    && team.GetString() is { } name && teams.Contains(name))
                {
                    last[name] = line;
                }
            }
            catch (System.Text.Json.JsonException)
            {
                // a torn or foreign line carries no hash
            }
        }

        return last;
    }
}

/// <summary>The end of a file: its last bytes (undecoded), its whole length, and whether they start after the file's beginning.</summary>
public readonly record struct FileTail(byte[] Bytes, long Length, bool StartsMidFile);

/// <summary>What <see cref="IFileReader.ReadRange"/> read: the bytes at the requested offset (fewer than asked at the end of the file) and the file's length at that moment.</summary>
public readonly record struct FileRange(byte[] Bytes, long Length);

/// <summary>What <see cref="IFileReader.ReadNoFollow"/> found: the kind of the file it opened, that file's size, and its leading bytes when it is a regular file.</summary>
public readonly record struct NoFollowRead(FileKind Kind, long Size, byte[] Bytes);

public interface IFileReader
{
    string ReadText(string path);

    byte[] ReadBytes(string path);

    /// <summary>
    /// The last <c>min(length, maxBytes)</c> bytes of the file by a positioned read, never decoded: the caller drops a partial first
    /// line. A missing file fails as <see cref="ReadBytes"/> does; <paramref name="maxBytes"/> must be positive.
    /// </summary>
    FileTail ReadTail(string path, int maxBytes);

    /// <summary>
    /// Up to <paramref name="maxBytes"/> bytes from <paramref name="offset"/> by a positioned read, never decoded, opened as
    /// <see cref="ReadTail"/> is. An offset at or past the end gives no bytes. A missing file fails as <see cref="ReadBytes"/> does;
    /// <paramref name="maxBytes"/> must be positive and <paramref name="offset"/> not negative. The file's identity (device and inode) is <see cref="Stat"/>'s.
    /// </summary>
    FileRange ReadRange(string path, long offset, int maxBytes);

    /// <summary>
    /// Opens <paramref name="path"/> without following a symbolic link in its last component, examines the file it opened, and
    /// when that is a regular file reads at most <paramref name="maxBytes"/> of its bytes. A link comes back as
    /// <see cref="FileKind.Symlink"/> and any other file that is not regular as its kind, each with no bytes. The kind, the size
    /// and the bytes all come from the one open file, so the path cannot be swapped between the check and the read. A missing
    /// file fails as <see cref="ReadBytes"/> does; <paramref name="maxBytes"/> must be positive.
    /// </summary>
    NoFollowRead ReadNoFollow(string path, int maxBytes);

    /// <summary>Returns null when the path does not exist.</summary>
    FileStat? Stat(string path, bool followSymlinks = true);

    bool Exists(string path);

    /// <summary>The canonical path with every symbolic link resolved, as <c>realpathSync</c> gives it. Throws when the path does not exist.</summary>
    string RealPath(string path);

    /// <summary>
    /// The target a symbolic link at <paramref name="path"/> holds, exactly as written (relative to the link's directory when
    /// relative); null when <paramref name="path"/> is not a link or does not exist. A reader that cannot see links answers null.
    /// </summary>
    string? ReadLink(string path) => null;

    /// <summary>
    /// The one read of an asks log (<c>asks.jsonl</c>, <c>asks-gate.jsonl</c>, <c>parked-questions.jsonl</c>; per-dir-files-to-db §6c G5):
    /// its non-blank lines, or unreadable. A plain reader reads the file: missing is readable and empty, an I/O or access failure is
    /// unreadable. The state database's reader overrides it with one read of the asks store.
    /// </summary>
    AskLog ReadAskLog(string path)
    {
        try
        {
            return Exists(path) ? AskLog.Of([.. ReadText(path).Split('\n').Where(static l => l.Trim().Length > 0)]) : AskLog.Of([]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AskLog.Unreadable;
        }
    }

    /// <summary>
    /// The one read of a hierarchy dir's grants (<c>&lt;H&gt;/grants/*.json</c> and the worktree audit log at <paramref name="auditPath"/>;
    /// per-dir-files-to-db S11e, §6c G5): each team's document with its latest audit row, or unreadable. A plain reader reads the files.
    /// The state database's reader overrides it with one read of the grants store.
    /// </summary>
    GrantsLog ReadGrants(string hierarchyDir, string auditPath) => GrantFiles.Read(this, hierarchyDir, auditPath);

    IReadOnlyList<string> List(string directory);

    /// <summary><c>access(path, X_OK)</c>: whether this process may execute <paramref name="path"/>. A directory with the search bit counts.</summary>
    bool IsExecutable(string path);
}

/// <summary>Restricted: only the assemblies that own an excluded store, and the state engine, may use it.</summary>
public interface IRawFileWriter
{
    /// <summary>Writes <paramref name="bytes"/> to <paramref name="tmpPath"/> and renames it over <paramref name="path"/>.</summary>
    void WriteAtomic(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null);

    /// <summary>Returns false when <paramref name="path"/> already exists.</summary>
    bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null);

    void Rename(string fromPath, string toPath);

    /// <summary>Returns false when <paramref name="path"/> did not exist.</summary>
    bool Unlink(string path);

    /// <summary>Creates the directory and any missing parents.</summary>
    void Mkdir(string path, UnixFileMode? mode = null);
}

/// <summary>Restricted: only the assemblies that own an excluded store, and the state engine, may use it.</summary>
public interface IRecordAppender
{
    /// <summary>Appends one record with a single <c>write(2)</c> on an <c>O_APPEND</c> descriptor and checks the count written.</summary>
    void Append(string path, ReadOnlySpan<byte> record);
}

public interface ILockProvider
{
    /// <summary>How long a lock may sit before a taker removes it. A lock a dead holder left behind is taken over only once it is that old.</summary>
    static readonly TimeSpan StaleAfter = TimeSpan.FromMilliseconds(10_000);

    /// <summary>How much longer than the stale age a caller that can wait must wait, so a dead holder's lock is taken over inside the wait.</summary>
    static readonly TimeSpan Margin = TimeSpan.FromSeconds(2);

    /// <summary>The wait of a command line caller: the stale age plus the margin.</summary>
    static readonly TimeSpan CliWait = WaitFor(StaleAfter);

    /// <summary>How long a command line caller waits for the daemon's answer to a write: the daemon's own lock wait plus a second for the request.</summary>
    static readonly TimeSpan DaemonWait = CliWait + TimeSpan.FromSeconds(1);

    /// <summary>The wait that outlasts locks which go stale after <paramref name="staleAfter"/>.</summary>
    static TimeSpan WaitFor(TimeSpan staleAfter) => staleAfter + Margin;

    /// <summary>The wait of a caller that can wait on this provider's locks: its own stale age plus the margin, so a dead holder's lock is taken over inside the wait.</summary>
    TimeSpan CallerWait => CliWait;

    /// <summary>
    /// One attempt at a lock the kernel holds on the file for as long as the holder lives: it is released the moment the holder exits,
    /// however it exits, and never goes stale while held. Returns null when another holder has it. A provider with no such lock gives
    /// the age-based <see cref="TryAcquire"/>.
    /// </summary>
    IDisposable? TryHold(string lockPath) => TryAcquire(lockPath);

    /// <summary>One attempt. Disposing the result releases the lock. Returns null when another holder owns a fresh lock.</summary>
    IDisposable? TryAcquire(string lockPath);

    /// <summary>Retries until <paramref name="wait"/> elapses. Returns null on timeout.</summary>
    IDisposable? Acquire(string lockPath, TimeSpan wait);
}
