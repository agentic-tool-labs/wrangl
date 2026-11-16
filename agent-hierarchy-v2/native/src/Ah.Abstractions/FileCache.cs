namespace Ah.Abstractions;

/// <summary>
/// A cache of file contents across processes and builds, keyed on a file's path and its stat tuple (size, modify time, change time, inode).
/// It holds derived data only: a lookup that finds nothing, or a cache that is unavailable, is never an error, and the caller reads the file.
/// </summary>
public interface IFileCache
{
    /// <summary>The content kept for <paramref name="path"/> when it was stored under exactly <paramref name="stat"/>.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="stat">The file's stat as just read; any difference from the stored tuple is a miss.</param>
    /// <returns>The content, or null when none is kept for this tuple or the cache is unavailable.</returns>
    byte[]? Lookup(string path, FileStat stat);

    /// <summary>Keeps <paramref name="content"/> for <paramref name="path"/> under <paramref name="stat"/>, replacing what was kept; does nothing when the cache is unavailable or read-only.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="stat">The file's stat as read before its content was.</param>
    /// <param name="content">The file's bytes.</param>
    void Store(string path, FileStat stat, byte[] content);
}
