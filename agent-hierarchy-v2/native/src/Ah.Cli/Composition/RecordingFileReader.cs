using Ah.Abstractions;

namespace Ah.Cli.Composition;

/// <summary>The paths one memo build consulted, each with the stat taken just before the first access to it.</summary>
internal sealed class FileAccessLog
{
    private readonly Dictionary<(string Path, bool Follow), FileStat?> _seen = new();
    private readonly object _gate = new();

    /// <summary>Whether a stat failed while recording, so the build cannot be validated later.</summary>
    internal bool Unverifiable { get; private set; }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _seen.Count;
            }
        }
    }

    internal (string Path, bool Follow, FileStat? Stat)[] Entries()
    {
        lock (_gate)
        {
            return [.. _seen.Select(static kv => (kv.Key.Path, kv.Key.Follow, kv.Value))];
        }
    }

    internal void Note(IFileReader files, string path, bool follow)
    {
        lock (_gate)
        {
            if (_seen.ContainsKey((path, follow)))
            {
                return;
            }
        }

        FileStat? stat = null;
        var failed = false;
        try
        {
            stat = files.Stat(path, follow);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            failed = true;
        }

        lock (_gate)
        {
            _ = _seen.TryAdd((path, follow), stat);
            Unverifiable |= failed;
        }
    }

    internal void Merge(FileAccessLog inner)
    {
        foreach (var (path, follow, stat) in inner.Entries())
        {
            lock (_gate)
            {
                _ = _seen.TryAdd((path, follow), stat);
            }
        }

        lock (_gate)
        {
            Unverifiable |= inner.Unverifiable;
        }
    }
}

/// <summary>
/// An <see cref="IFileReader"/> that, while a <see cref="FileAccessLog"/> is open in the calling context, notes the stat of every path
/// it is asked about just before it answers. Outside a log every call is the inner reader's, untouched.
/// </summary>
/// <param name="inner">The reader every call is forwarded to.</param>
internal sealed class RecordingFileReader(IFileReader inner) : IFileReader
{
    private static readonly AsyncLocal<FileAccessLog?> Active = new();

    /// <summary>Opens <paramref name="log"/> for the calling context; disposing it restores the log that was open and folds what it saw into that one.</summary>
    internal static IDisposable Begin(FileAccessLog log)
    {
        var outer = Active.Value;
        Active.Value = log;
        return new Scope(outer, log);
    }

    public string ReadText(string path)
    {
        Note(path, true);
        return inner.ReadText(path);
    }

    public byte[] ReadBytes(string path)
    {
        Note(path, true);
        return inner.ReadBytes(path);
    }

    public FileTail ReadTail(string path, int maxBytes)
    {
        Note(path, true);
        return inner.ReadTail(path, maxBytes);
    }

    public FileRange ReadRange(string path, long offset, int maxBytes)
    {
        Note(path, true);
        return inner.ReadRange(path, offset, maxBytes);
    }

    public NoFollowRead ReadNoFollow(string path, int maxBytes)
    {
        Note(path, false);
        return inner.ReadNoFollow(path, maxBytes);
    }

    public FileStat? Stat(string path, bool followSymlinks = true)
    {
        Note(path, followSymlinks);
        return inner.Stat(path, followSymlinks);
    }

    public bool Exists(string path)
    {
        Note(path, true);
        return inner.Exists(path);
    }

    public string RealPath(string path)
    {
        Note(path, true);
        return inner.RealPath(path);
    }

    public string? ReadLink(string path)
    {
        Note(path, false);
        return inner.ReadLink(path);
    }

    public AskLog ReadAskLog(string path)
    {
        Note(path, false);
        return inner.ReadAskLog(path);
    }

    public GrantsLog ReadGrants(string hierarchyDir, string auditPath)
    {
        Note(hierarchyDir.TrimEnd('/') + "/grants", true);
        Note(auditPath, false);
        return inner.ReadGrants(hierarchyDir, auditPath);
    }

    public IReadOnlyList<string> List(string directory)
    {
        Note(directory, true);
        return inner.List(directory);
    }

    public bool IsExecutable(string path) => inner.IsExecutable(path);

    private void Note(string path, bool follow) => Active.Value?.Note(inner, path, follow);

    private sealed class Scope(FileAccessLog? outer, FileAccessLog log) : IDisposable
    {
        public void Dispose()
        {
            Active.Value = outer;
            outer?.Merge(log);
        }
    }
}
