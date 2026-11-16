using Ah.Abstractions;

namespace Ah.Cli.Composition;

/// <summary>
/// The graph's file reader with the asks logs (<c>asks.jsonl</c>, <c>asks-gate.jsonl</c>, <c>parked-questions.jsonl</c>) read from the
/// <c>asks</c> table (per-dir-files-to-db S10, §6c G5). <see cref="ReadAskLog"/> is their one read: one store read, unreadable carried
/// in its result. A log the store holds cannot be read as a file here (<see cref="ReadText"/>, <see cref="ReadBytes"/> and
/// <see cref="ReadTail"/> throw), so a missed reader fails loudly instead of reading a stale file. <see cref="Exists"/> and
/// <see cref="Stat"/> are the plain file facts.
/// The grants (<c>&lt;H&gt;/grants</c> and the worktree audit file, S11e) are read the same way through <see cref="ReadGrants"/>: once a
/// database exists (<c>databaseExists</c>), their files cannot be read here, nor <c>&lt;H&gt;/grants</c> listed.
/// </summary>
internal sealed class AskLogFileReader(IFileReader inner, IAskSource asks, IGrantSource? grants = null, Func<bool>? databaseExists = null, string? auditFile = null) : IFileReader
{
    public AskLog ReadAskLog(string path) => asks.Read(path) ?? inner.ReadAskLog(path);

    public GrantsLog ReadGrants(string hierarchyDir, string auditPath) => grants?.Read(hierarchyDir) ?? inner.ReadGrants(hierarchyDir, auditPath);

    /// <summary>
    /// Whether <paramref name="path"/> is the worktree audit file (<c>auditFile</c>, exactly), or a hierarchy dir's <c>grants</c> dir or a
    /// file directly in it. A hierarchy dir is a project dir by its path (a repo's <c>.claude/ah2</c>, <c>~/.claude/ah2/project</c>
    /// or <c>~/.claude/ah2/hierarchy/&lt;name&gt;</c>), never by what it holds, so a <c>grants</c>
    /// folder elsewhere is not shut.
    /// </summary>
    private bool IsGrantsPath(string path)
    {
        var trimmed = path.TrimEnd('/');
        if (trimmed == auditFile)
        {
            return true;
        }

        var grantsDir = Path.GetFileName(trimmed) is "grants" ? trimmed : Path.GetDirectoryName(trimmed);
        if (Path.GetFileName(grantsDir) is not "grants" || Path.GetDirectoryName(grantsDir) is not { } hier)
        {
            return false;
        }

        return Ah.Core.Text.AhPaths.RepoRootOf(hier) is not null || Ah.Core.Text.AhPaths.IsUserProjectDir(hier);
    }

    /// <summary>
    /// Throws when the table has taken <paramref name="path"/>'s asks log, or when <paramref name="path"/> is a grants path and a database
    /// exists: their rows are read through <see cref="ReadAskLog"/> and <see cref="ReadGrants"/> only.
    /// </summary>
    private void ShutDoor(string path)
    {
        if (asks.Holds(path) && asks.Taken(path))
        {
            throw new InvalidOperationException($"{path} is an asks log the state database holds; read it with ReadAskLog");
        }

        if (grants is not null && IsGrantsPath(path) && databaseExists?.Invoke() == true)
        {
            throw new InvalidOperationException($"{path} is grants the state database holds; read them with ReadGrants");
        }
    }

    public string ReadText(string path)
    {
        ShutDoor(path);
        return inner.ReadText(path);
    }

    public byte[] ReadBytes(string path)
    {
        ShutDoor(path);
        return inner.ReadBytes(path);
    }

    public FileTail ReadTail(string path, int maxBytes)
    {
        ShutDoor(path);
        return inner.ReadTail(path, maxBytes);
    }

    public FileRange ReadRange(string path, long offset, int maxBytes) => inner.ReadRange(path, offset, maxBytes);

    public NoFollowRead ReadNoFollow(string path, int maxBytes) => inner.ReadNoFollow(path, maxBytes);

    public FileStat? Stat(string path, bool followSymlinks = true) => inner.Stat(path, followSymlinks);

    public bool Exists(string path) => inner.Exists(path);

    public string RealPath(string path) => inner.RealPath(path);

    public string? ReadLink(string path) => inner.ReadLink(path);

    public IReadOnlyList<string> List(string directory)
    {
        ShutDoor(directory);
        return inner.List(directory);
    }

    public bool IsExecutable(string path) => inner.IsExecutable(path);
}
