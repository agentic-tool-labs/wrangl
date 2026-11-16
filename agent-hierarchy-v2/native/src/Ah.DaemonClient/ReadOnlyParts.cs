using Ah.Abstractions;

namespace Ah.DaemonClient;

/// <summary>The writers and spawner of a client that may only look at a daemon: every one of them does nothing, or refuses.</summary>
internal sealed class InertWriter : IRawFileWriter
{
    public void WriteAtomic(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        // a read-only client writes nothing
    }

    public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => false;

    public void Rename(string fromPath, string toPath)
    {
        // a read-only client writes nothing
    }

    public bool Unlink(string path) => false;

    public void Mkdir(string path, UnixFileMode? mode = null)
    {
        // a read-only client writes nothing
    }
}

internal sealed class InertAppender : IRecordAppender
{
    public void Append(string path, ReadOnlySpan<byte> record)
    {
        // a read-only client records no fallback
    }
}

/// <summary>Never grants a lock, so nothing that needs the start lock can proceed.</summary>
internal sealed class InertLocks : ILockProvider
{
    public IDisposable? TryAcquire(string lockPath) => null;

    public IDisposable? Acquire(string lockPath, TimeSpan wait) => null;
}

internal sealed class RefusingSpawner : IDetachedSpawner
{
    public int SpawnDetached(DetachedSpawnRequest request) =>
        throw new InvalidOperationException("a read-only daemon client does not start a daemon");
}
