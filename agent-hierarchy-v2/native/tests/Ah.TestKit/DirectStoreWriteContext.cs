using System.Runtime.Versioning;
using Ah.Abstractions;

namespace Ah.TestKit;

/// <summary>An <see cref="IStoreWriteContext"/> that writes straight to the file system, for running an op body the way the engine would.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class DirectStoreWriteContext : IStoreWriteContext
{
    public void Write(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        using (var stream = new FileStream(tmpPath, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(bytes);
        }

        if (mode is { } m)
        {
            File.SetUnixFileMode(tmpPath, m);
        }

        File.Move(tmpPath, path, overwrite: true);
    }

    public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.Write(bytes);
        }
        catch (IOException) when (File.Exists(path))
        {
            return false;
        }

        if (mode is { } m)
        {
            File.SetUnixFileMode(path, m);
        }

        return true;
    }

    public void Append(string path, ReadOnlySpan<byte> record)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        stream.Write(record);
    }

    public void Rename(string fromPath, string toPath) => File.Move(fromPath, toPath, overwrite: true);

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
