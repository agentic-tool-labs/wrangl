using System.Text;
using Ah.Abstractions;
using Ah.Core.Text;
using Ah.Platform.Interop;

namespace Ah.Platform;

internal sealed class FileReader : IFileReader
{
    private const uint TypeMask = 0xF000;
    private const uint TypeFile = 0x8000;
    private const uint TypeDirectory = 0x4000;
    private const uint TypeSymlink = 0xA000;

    private readonly bool _statSupported;

    /// <summary>Whether this process can read inode and uid: macOS on arm64 only, the one layout checked on a real host.</summary>
    internal static bool StatSupportedHere => OperatingSystem.IsMacOS() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

    internal FileReader()
        : this(StatSupportedHere)
    {
    }

    internal FileReader(bool statSupported) => _statSupported = statSupported;

    public string ReadText(string path) => Utf8Text.Decode(File.ReadAllBytes(path));

    public byte[] ReadBytes(string path) => File.ReadAllBytes(path);

    public NoFollowRead ReadNoFollow(string path, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        if (!_statSupported)
        {
            throw new PlatformNotSupportedException(
                "IFileReader.ReadNoFollow reads the opened file's type and size, which are implemented for macOS arm64 only; this host has not been verified.");
        }

        var fd = Libc.Open(path, Libc.ONoFollow | Libc.ONonBlock | Libc.OCloexec);
        if (fd < 0)
        {
            var errno = Libc.Errno;
            if (errno == Libc.Eloop)
            {
                return new NoFollowRead(FileKind.Symlink, 0, []);
            }

            throw errno is Libc.Enoent or Libc.Enotdir
                ? new FileNotFoundException($"no such file or directory: '{path}'", path)
                : new IOException($"open failed for '{path}': errno {errno}");
        }

        using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(fd, ownsHandle: true);
        if (Libc.FStat(fd, out var st) != 0)
        {
            throw new IOException($"fstat failed for '{path}': errno {Libc.Errno}");
        }

        var kind = (st.Mode & TypeMask) switch
        {
            TypeFile => FileKind.File,
            TypeDirectory => FileKind.Directory,
            TypeSymlink => FileKind.Symlink,
            _ => FileKind.Other,
        };
        if (kind != FileKind.File)
        {
            return new NoFollowRead(kind, st.Size, []);
        }

        var buffer = new byte[(int)Math.Min(st.Size, maxBytes)];
        var got = 0;
        while (got < buffer.Length)
        {
            var n = RandomAccess.Read(handle, buffer.AsSpan(got), got);
            if (n <= 0)
            {
                break;
            }

            got += n;
        }

        if (got < buffer.Length)
        {
            Array.Resize(ref buffer, got);
        }

        return new NoFollowRead(FileKind.File, st.Size, buffer);
    }

    public FileRange ReadRange(string path, long offset, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        if (offset >= length)
        {
            return new FileRange([], length);
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(maxBytes, length - offset)];
        var got = 0;
        while (got < buffer.Length)
        {
            var n = stream.Read(buffer, got, buffer.Length - got);
            if (n <= 0)
            {
                break;
            }

            got += n;
        }

        if (got < buffer.Length)
        {
            Array.Resize(ref buffer, got);
        }

        return new FileRange(buffer, length);
    }

    public FileTail ReadTail(string path, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = stream.Length;
        var start = Math.Max(0, length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[length - start];
        var got = 0;
        while (got < buffer.Length)
        {
            var n = stream.Read(buffer, got, buffer.Length - got);
            if (n <= 0)
            {
                break;
            }

            got += n;
        }

        if (got < buffer.Length)
        {
            Array.Resize(ref buffer, got);
        }

        return new FileTail(buffer, length, start > 0);
    }

    /// <summary><c>existsSync</c>: symbolic links are followed, so a dangling one does not exist.</summary>
    public bool Exists(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists)
        {
            return false;
        }

        if (info.LinkTarget is null)
        {
            return true;
        }

        try
        {
            return info.ResolveLinkTarget(returnFinalTarget: true) is { Exists: true };
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary><c>realpathSync</c>: every symbolic link along the path is resolved; a missing component is an error.</summary>
    public string RealPath(string path)
    {
        // Not Path.GetFullPath: it folds ".." lexically, but a ".." after a symbolic link names the parent of the link's target.
        var absolute = path.StartsWith('/') ? path : Directory.GetCurrentDirectory() + "/" + path;
        var pending = new Stack<string>(absolute.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        var links = 0;
        while (pending.Count > 0)
        {
            var name = pending.Pop();

            // A component after a file (including "." and "..") is not a path at all: the system says ENOTDIR.
            if (current != "/" && !Directory.Exists(current))
            {
                throw new FileNotFoundException("not a directory", current);
            }

            if (name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                current = Path.GetDirectoryName(current) ?? "/";
                continue;
            }

            var next = current == "/" ? "/" + name : current + "/" + name;
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            var target = info.LinkTarget;
            if (target is null)
            {
                if (!info.Exists)
                {
                    throw new FileNotFoundException("no such file or directory", next);
                }

                current = next;
                continue;
            }

            if (++links > 40)
            {
                throw new IOException("too many levels of symbolic links: " + path);
            }

            if (target.StartsWith('/'))
            {
                current = "/";
            }

            foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
            {
                pending.Push(part);
            }
        }

        return current;
    }

    /// <summary><c>readlinkSync</c>: the target as written; null for a path that is not a link or is missing.</summary>
    public string? ReadLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Names sorted by their UTF-8 bytes, which is the order <c>readdirSync</c> gives.</summary>
    public IReadOnlyList<string> List(string directory)
    {
        var names = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            names.Add(Path.GetFileName(entry));
        }

        names.Sort(static (a, b) => Utf8Compare(a, b));
        return names;
    }

    /// <summary><c>accessSync(path, X_OK)</c>: false for a missing path and for one this process may not execute.</summary>
    public bool IsExecutable(string path) => Libc.Access(path, 1) == 0;

    public FileStat? Stat(string path, bool followSymlinks = true)
    {
        if (!_statSupported)
        {
            throw new PlatformNotSupportedException(
                "IFileReader.Stat reads inode and uid, which are implemented for macOS arm64 only; this host has not been verified.");
        }

        var rc = followSymlinks ? Libc.Stat(path, out var st) : Libc.LStat(path, out st);
        if (rc != 0)
        {
            var errno = Libc.Errno;
            if (errno is Libc.Enoent or Libc.Enotdir)
            {
                return null;
            }

            throw new IOException($"stat failed for '{path}': errno {errno}");
        }

        var type = st.Mode & TypeMask;
        var kind = type switch
        {
            TypeFile => FileKind.File,
            TypeDirectory => FileKind.Directory,
            TypeSymlink => FileKind.Symlink,
            _ => FileKind.Other,
        };
        return new FileStat(kind, st.Size, (st.MtimeSec * 1000.0) + (st.MtimeNsec / 1e6), st.Ino, st.Mode, st.Uid, (ulong)(uint)st.Device, (st.MtimeSec * 1_000_000_000L) + st.MtimeNsec, (st.CtimeSec * 1_000_000_000L) + st.CtimeNsec);
    }

    private static int Utf8Compare(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));
}
