using System.Diagnostics;
using System.Text;
using Ah.Abstractions;

namespace Ah.Watch;

/// <summary>A file reader that tells the current <see cref="ViewTimingScope"/> what it was asked for, and answers as the reader it wraps does.</summary>
/// <param name="inner">The reader every call is forwarded to.</param>
internal sealed class TimedFileReader(IFileReader inner) : IFileReader
{
    /// <inheritdoc />
    public string ReadText(string path)
    {
        ViewTiming.Current?.AddCall("ReadText", path, false);
        var text = inner.ReadText(path);
        ViewTiming.Current?.AddRead(path, Encoding.UTF8.GetByteCount(text));
        return text;
    }

    /// <inheritdoc />
    public byte[] ReadBytes(string path)
    {
        ViewTiming.Current?.AddCall("ReadBytes", path, false);
        var bytes = inner.ReadBytes(path);
        ViewTiming.Current?.AddRead(path, bytes.Length);
        return bytes;
    }

    /// <inheritdoc />
    public FileTail ReadTail(string path, int maxBytes)
    {
        ViewTiming.Current?.AddCall("ReadTail", path, false);
        var tail = inner.ReadTail(path, maxBytes);
        ViewTiming.Current?.AddRead(path, tail.Bytes.Length);
        return tail;
    }

    /// <inheritdoc />
    public FileRange ReadRange(string path, long offset, int maxBytes)
    {
        ViewTiming.Current?.AddCall("ReadRange", path, false);
        var range = inner.ReadRange(path, offset, maxBytes);
        ViewTiming.Current?.AddRead(path, range.Bytes.Length);
        return range;
    }

    /// <inheritdoc />
    public NoFollowRead ReadNoFollow(string path, int maxBytes)
    {
        ViewTiming.Current?.AddCall("ReadNoFollow", path, false);
        var read = inner.ReadNoFollow(path, maxBytes);
        ViewTiming.Current?.AddRead(path, read.Bytes.Length);
        return read;
    }

    /// <inheritdoc />
    public FileStat? Stat(string path, bool followSymlinks = true)
    {
        ViewTiming.Current?.AddCall("Stat", path, false);
        ViewTiming.Current?.AddStat(path);
        return inner.Stat(path, followSymlinks);
    }

    /// <inheritdoc />
    public bool Exists(string path)
    {
        ViewTiming.Current?.AddCall("Exists", path, false);
        ViewTiming.Current?.AddStat(path);
        return inner.Exists(path);
    }

    /// <inheritdoc />
    public string RealPath(string path)
    {
        ViewTiming.Current?.AddCall("RealPath", path, false);
        return inner.RealPath(path);
    }

    /// <inheritdoc />
    public string? ReadLink(string path)
    {
        ViewTiming.Current?.AddCall("ReadLink", path, false);
        return inner.ReadLink(path);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> List(string directory)
    {
        ViewTiming.Current?.AddCall("List", directory, true);
        ViewTiming.Current?.AddList(directory);
        return inner.List(directory);
    }

    /// <inheritdoc />
    public bool IsExecutable(string path)
    {
        ViewTiming.Current?.AddCall("IsExecutable", path, false);
        return inner.IsExecutable(path);
    }
}

/// <summary>A process runner that tells the current <see cref="ViewTimingScope"/> how long each run took, by the executable it ran.</summary>
/// <param name="inner">The runner every call is forwarded to.</param>
internal sealed class TimedProcessRunner(IProcessRunner inner) : IProcessRunner
{
    /// <inheritdoc />
    public ProcessResult Run(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var scope = ViewTiming.Current;
        if (scope is null)
        {
            return inner.Run(request);
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            return inner.Run(request);
        }
        finally
        {
            var executable = request.Argv.Count > 0 ? Path.GetFileName(request.Argv[0]) : string.Empty;
            scope.AddProcess(executable, Stopwatch.GetTimestamp() - start);
        }
    }
}
