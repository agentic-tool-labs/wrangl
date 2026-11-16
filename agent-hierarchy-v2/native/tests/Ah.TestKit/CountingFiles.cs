using Ah.Abstractions;

namespace Ah.TestKit;

/// <summary>A file reader that passes every call to <paramref name="inner"/> and counts the bytes its reads returned.</summary>
public sealed class CountingFiles(IFileReader inner) : IFileReader
{
    /// <summary>The bytes the content reads returned so far (text counts as its UTF-8 length).</summary>
    public long BytesRead { get; private set; }

    /// <summary>How many times each directory was listed.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> Lists { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times each path was stat'ed.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> Stats { get; } = new(StringComparer.Ordinal);

    /// <summary>How many times each path's text was read.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, int> TextReads { get; } = new(StringComparer.Ordinal);

    /// <summary>Runs before each text read, with its path: a test interleaves a write with a reader mid-read.</summary>
    public Action<string>? BeforeReadText { get; set; }

    public string ReadText(string path)
    {
        BeforeReadText?.Invoke(path);
        _ = TextReads.AddOrUpdate(path, 1, static (_, n) => n + 1);
        var text = inner.ReadText(path);
        BytesRead += System.Text.Encoding.UTF8.GetByteCount(text);
        return text;
    }

    public byte[] ReadBytes(string path)
    {
        var bytes = inner.ReadBytes(path);
        BytesRead += bytes.Length;
        return bytes;
    }

    public FileTail ReadTail(string path, int maxBytes)
    {
        var tail = inner.ReadTail(path, maxBytes);
        BytesRead += tail.Bytes.Length;
        return tail;
    }

    public FileRange ReadRange(string path, long offset, int maxBytes)
    {
        var range = inner.ReadRange(path, offset, maxBytes);
        BytesRead += range.Bytes.Length;
        return range;
    }

    public NoFollowRead ReadNoFollow(string path, int maxBytes)
    {
        var read = inner.ReadNoFollow(path, maxBytes);
        BytesRead += read.Bytes.Length;
        return read;
    }

    public FileStat? Stat(string path, bool followSymlinks = true)
    {
        _ = Stats.AddOrUpdate(path, 1, static (_, n) => n + 1);
        return inner.Stat(path, followSymlinks);
    }

    public bool Exists(string path) => inner.Exists(path);

    public string RealPath(string path) => inner.RealPath(path);

    public string? ReadLink(string path) => inner.ReadLink(path);

    public IReadOnlyList<string> List(string directory)
    {
        _ = Lists.AddOrUpdate(directory, 1, static (_, n) => n + 1);
        return inner.List(directory);
    }

    public bool IsExecutable(string path) => inner.IsExecutable(path);
}
