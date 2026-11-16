using Ah.Abstractions;

namespace Ah.Hierarchy.Tests;

/// <summary>An <see cref="IStoreWriteContext"/> over the real file system, the way a Node op body writes in the unit sandbox.</summary>
internal sealed class FsStoreWriteContext : IStoreWriteContext
{
    public void Write(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        File.WriteAllBytes(tmpPath, bytes.ToArray());
        File.Move(tmpPath, path, true);
    }

    public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes);
            return true;
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }
    }

    public void Append(string path, ReadOnlySpan<byte> record)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        stream.Write(record);
    }

    public void Rename(string fromPath, string toPath) => File.Move(fromPath, toPath, true);

    public bool Remove(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public void Mkdir(string path, UnixFileMode? mode = null) => Directory.CreateDirectory(path);
}
