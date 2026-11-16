using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

internal sealed class RawFileWriter : IRawFileWriter
{
    /// <summary>
    /// <c>writeFileSync(tmp, bytes, {flag: "wx"})</c> then <c>renameSync(tmp, path)</c>; the temporary file is removed
    /// when either step fails. Nothing is synced to disk.
    /// </summary>
    public void WriteAtomic(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        _ = CreateExclusive(tmpPath, bytes, mode, throwIfExists: true);

        try
        {
            RenameChecked(tmpPath, path);
        }
        catch
        {
            _ = Libc.Unlink(tmpPath);
            throw;
        }
    }

    public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) =>
        CreateExclusive(path, bytes, mode, throwIfExists: false);

    public void Rename(string fromPath, string toPath) => RenameChecked(fromPath, toPath);

    public bool Unlink(string path)
    {
        if (Libc.Unlink(path) == 0)
        {
            return true;
        }

        var errno = Libc.Errno;
        return errno == Libc.Enoent ? false : throw new IOException($"unlink failed for '{path}': errno {errno}");
    }

    public void Mkdir(string path, UnixFileMode? mode = null)
    {
        if (mode is { } m)
        {
            Directory.CreateDirectory(path, m);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    private static void RenameChecked(string from, string to)
    {
        if (Libc.Rename(from, to) != 0)
        {
            throw new IOException($"rename '{from}' -> '{to}' failed: errno {Libc.Errno}");
        }
    }

    private static bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode, bool throwIfExists)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.ReadWrite,
            BufferSize = 0,
        };
        if (mode is { } m)
        {
            options.UnixCreateMode = m;
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, options);
        }
        catch (IOException) when (!throwIfExists && (File.Exists(path) || Directory.Exists(path)))
        {
            return false;
        }

        using (stream)
        {
            stream.Write(bytes);
        }

        return true;
    }
}
