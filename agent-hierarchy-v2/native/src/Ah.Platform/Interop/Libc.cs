using System.Runtime.InteropServices;

namespace Ah.Platform.Interop;

/// <summary>
/// The libc entry points of the platform layer, and the only P/Invoke declarations in the solution.
/// <c>open</c> is variadic in C and its <c>mode</c> argument is misread when a fixed-signature P/Invoke passes it on
/// Apple arm64, so files are created through .NET and <see cref="Open"/> is only ever called without <c>O_CREAT</c>.
/// </summary>
internal static partial class Libc
{
    internal const int Eperm = 1;
    internal const int Enoent = 2;
    internal const int Eintr = 4;
    internal const int Eexist = 17;

    internal static int WouldBlock => OperatingSystem.IsMacOS() ? 35 : 11;
    internal const int Enotdir = 20;

    internal static int OWronly => 1;

    internal static int OAppend => OperatingSystem.IsMacOS() ? 0x8 : 0x400;

    internal static int ONoFollow => OperatingSystem.IsMacOS() ? 0x100 : 0x20000;

    internal static int ONonBlock => OperatingSystem.IsMacOS() ? 0x4 : 0x800;

    internal static int Eloop => OperatingSystem.IsMacOS() ? 62 : 40;

    internal static int OCloexec => OperatingSystem.IsMacOS() ? 0x1000000 : 0x80000;

    internal static int Errno => Marshal.GetLastPInvokeError();

    /// <summary>The name <paramref name="path"/>'s last part has on disk, a link's own for a link; null when it cannot be read. macOS only.</summary>
    internal static string? NameOnDisk(string path)
    {
        // uint32 length, then an attrreference_t (int32 offset from itself, uint32 length with its NUL), then the name
        const int reference = sizeof(uint);
        const nuint noFollow = 1;
        Span<byte> reply = stackalloc byte[(3 * sizeof(uint)) + 1024];
        var name = new AttrList { BitmapCount = 5, CommonAttr = 1 };
        if (GetAttrList(path, ref name, reply, (nuint)reply.Length, noFollow) != 0)
        {
            return null;
        }

        var at = reference + BitConverter.ToInt32(reply[reference..]);
        var length = BitConverter.ToInt32(reply[(reference + sizeof(int))..]) - 1;
        return length > 0 && at + length <= reply.Length ? System.Text.Encoding.UTF8.GetString(reply.Slice(at, length)) : null;
    }

    [LibraryImport("libc", EntryPoint = "getattrlist", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int GetAttrList(string path, ref AttrList list, Span<byte> reply, nuint size, nuint options);

    /// <summary>The <c>attrlist</c> of getattrlist(2).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AttrList
    {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint CommonAttr;
        public uint VolAttr;
        public uint DirAttr;
        public uint FileAttr;
        public uint ForkAttr;
    }

    /// <summary><c>sysctl(name, namelen, oldp, &amp;oldlen, NULL, 0)</c>: with an empty <paramref name="oldValue"/> it only sets the length.</summary>
    [LibraryImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    internal static partial int Sysctl(Span<int> name, uint nameLength, Span<byte> oldValue, ref nuint oldLength, nint newValue, nuint newLength);

    /// <summary><c>sysctlbyname(name, oldp, &amp;oldlen, NULL, 0)</c>.</summary>
    [LibraryImport("libc", EntryPoint = "sysctlbyname", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int SysctlByName(string name, out int oldValue, ref nuint oldLength, nint newValue, nuint newLength);

    /// <summary><c>getloadavg(loadavg, nelem)</c>: the number of samples written, or -1.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    internal static partial int ClockGetTime(int clock, out TimeSpec time);

    [LibraryImport("libc", EntryPoint = "getloadavg", SetLastError = true)]
    internal static partial int GetLoadAvg(Span<double> loadavg, int count);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    internal static partial nint Write(int fd, ReadOnlySpan<byte> buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "close")]
    internal static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "flock", SetLastError = true)]
    internal static partial int Flock(int fd, int operation);

    [LibraryImport("libc", EntryPoint = "link", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Link(string from, string to);

    [LibraryImport("libc", EntryPoint = "rename", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Rename(string from, string to);

    [LibraryImport("libc", EntryPoint = "unlink", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Unlink(string path);

    [LibraryImport("libc", EntryPoint = "kill", SetLastError = true)]
    internal static partial int Kill(int pid, int signal);

    /// <summary><c>getsockopt(fd, level, name, value, &amp;length)</c> for an option whose value is one <c>int</c>.</summary>
    [LibraryImport("libc", EntryPoint = "getsockopt", SetLastError = true)]
    internal static unsafe partial int GetSockOptInt(int fd, int level, int name, int* value, ref uint length);

    [LibraryImport("libc", EntryPoint = "pipe", SetLastError = true)]
    internal static unsafe partial int Pipe(int* descriptors);

    [LibraryImport("libc", EntryPoint = "pipe2", SetLastError = true)]
    internal static unsafe partial int Pipe2(int* descriptors, int flags);

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    internal static unsafe partial nint ReadFd(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    internal static unsafe partial int WaitPid(int pid, int* status, int options);

    [LibraryImport("libc", EntryPoint = "access", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Access(string path, int mode);

    [LibraryImport("libc", EntryPoint = "stat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int Stat(string path, out DarwinStat buffer);

    [LibraryImport("libc", EntryPoint = "fstat", SetLastError = true)]
    internal static partial int FStat(int fd, out DarwinStat buffer);

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    internal static partial int LStat(string path, out DarwinStat buffer);

    /// <summary><c>getloadavg(loadavg, nelem)</c>: the number of samples stored, or -1.</summary>
    [LibraryImport("libc", EntryPoint = "getloadavg")]
    internal static unsafe partial int GetLoadAvg(double* loadavg, int count);

    internal const int HostVmInfo64 = 4;

    /// <summary><c>HOST_VM_INFO64_COUNT</c>: the size of <c>vm_statistics64</c> in 32-bit units.</summary>
    internal const uint HostVmInfo64Count = 38;

    [LibraryImport("libc", EntryPoint = "mach_host_self")]
    internal static partial uint MachHostSelf();

    [LibraryImport("libc", EntryPoint = "host_page_size")]
    internal static partial int HostPageSize(uint host, out nuint pageSize);

    [LibraryImport("libc", EntryPoint = "host_statistics64")]
    internal static partial int HostStatistics64(uint host, int flavor, out VmStatistics64 info, ref uint count);
}

/// <summary>
/// <c>struct vm_statistics64</c> of macOS (<c>mach/vm_statistics.h</c>). Only the page counts the memory probe reads are declared;
/// offsets are checked against <c>vm_stat</c> in the platform tests.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 152)]
internal struct VmStatistics64
{
    [FieldOffset(0)]
    internal uint FreeCount;

    [FieldOffset(8)]
    internal uint InactiveCount;

    [FieldOffset(92)]
    internal uint SpeculativeCount;
}

/// <summary>
/// <c>struct stat</c> of 64-bit-inode macOS (<c>sys/stat.h</c>): the same layout on arm64 and x86-64. Only the fields
/// the product reads are declared; offsets are checked against <c>/usr/bin/stat</c> in the platform tests.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 144)]
internal struct DarwinStat
{
    [FieldOffset(0)]
    internal int Device;

    [FieldOffset(4)]
    internal ushort Mode;

    [FieldOffset(8)]
    internal ulong Ino;

    [FieldOffset(16)]
    internal uint Uid;

    [FieldOffset(48)]
    internal long MtimeSec;

    [FieldOffset(56)]
    internal long MtimeNsec;

    [FieldOffset(64)]
    internal long CtimeSec;

    [FieldOffset(72)]
    internal long CtimeNsec;

    [FieldOffset(96)]
    internal long Size;
}
