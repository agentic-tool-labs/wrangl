using System.Runtime.InteropServices;

namespace Ah.Platform.Interop;

internal static partial class SpawnLibc
{
    /// <summary>Over-allocated opaque storage for <c>posix_spawnattr_t</c> and <c>posix_spawn_file_actions_t</c> on both platforms.</summary>
    internal const int OpaqueBytes = 512;

    [LibraryImport("libc", EntryPoint = "posix_spawn")]
    internal static unsafe partial int PosixSpawn(int* pid, byte* path, void* fileActions, void* attr, nint* argv, nint* envp);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_init")]
    internal static unsafe partial int AttrInit(void* attr);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setflags")]
    internal static unsafe partial int AttrSetFlags(void* attr, short flags);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_destroy")]
    internal static unsafe partial int AttrDestroy(void* attr);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_init")]
    internal static unsafe partial int FileActionsInit(void* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addopen")]
    internal static unsafe partial int FileActionsAddOpen(void* actions, int fd, byte* path, int oflag, uint mode);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    internal static unsafe partial int FileActionsAddChdir(void* actions, byte* path);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_destroy")]
    internal static unsafe partial int FileActionsDestroy(void* actions);

    [LibraryImport("libc", EntryPoint = "posix_spawn_file_actions_adddup2")]
    internal static unsafe partial int FileActionsAddDup2(void* actions, int fd, int newFd);

    [LibraryImport("libc", EntryPoint = "posix_spawnattr_setsigdefault")]
    internal static unsafe partial int AttrSetSigDefault(void* attr, void* signals);

    [LibraryImport("libc", EntryPoint = "sigemptyset")]
    internal static unsafe partial int SigEmptySet(void* signals);

    [LibraryImport("libc", EntryPoint = "sigaddset")]
    internal static unsafe partial int SigAddSet(void* signals, int signal);
}
