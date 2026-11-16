using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>The one <c>write(2)</c> of a record; replaceable so a test can force a short or failed write.</summary>
internal delegate nint WriteFunction(int fd, ReadOnlySpan<byte> data);

internal sealed class RecordAppender(WriteFunction? write = null) : IRecordAppender
{
    private readonly WriteFunction _write = write ?? DefaultWrite;

    /// <summary>
    /// One <c>write(2)</c> of the whole record on an <c>O_APPEND</c> descriptor, the count checked, then <c>close</c>.
    /// Never <c>FileMode.Append</c>, <c>File.AppendAllText</c>, <c>pwrite</c> or <c>RandomAccess</c>: those lose or tear
    /// records when another process appends to the same file.
    /// </summary>
    public void Append(string path, ReadOnlySpan<byte> record)
    {
        var fd = OpenAppend(path);
        try
        {
            var written = _write(fd, record);
            if (written < 0)
            {
                throw new IOException($"append to '{path}' failed: errno {Libc.Errno}");
            }

            if (written != record.Length)
            {
                throw new IOException($"append to '{path}' wrote {written} of {record.Length} bytes");
            }
        }
        finally
        {
            _ = Libc.Close(fd);
        }
    }

    private static nint DefaultWrite(int fd, ReadOnlySpan<byte> data) => Libc.Write(fd, data, (nuint)data.Length);

    private static int OpenAppend(string path)
    {
        var flags = Libc.OWronly | Libc.OAppend | Libc.OCloexec;
        var fd = Libc.Open(path, flags);
        if (fd >= 0)
        {
            return fd;
        }

        var errno = Libc.Errno;
        if (errno != Libc.Enoent)
        {
            throw new IOException($"open '{path}' for append failed: errno {errno}");
        }

        // A missing file is created through .NET (default mode 0666 under the umask, as appendFileSync creates it);
        // a concurrent creator is fine because OpenOrCreate does not fail when the file appeared meanwhile.
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite, 1))
        {
        }

        fd = Libc.Open(path, flags);
        return fd >= 0 ? fd : throw new IOException($"open '{path}' for append failed: errno {Libc.Errno}");
    }
}
