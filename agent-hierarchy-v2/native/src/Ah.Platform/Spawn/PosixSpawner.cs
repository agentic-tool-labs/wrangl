using System.Runtime.InteropServices;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform.Spawn;

/// <summary>
/// Starts a process in a new session (<c>posix_spawn</c> with <c>POSIX_SPAWN_SETSID</c>), with stdin from
/// <c>/dev/null</c>, stdout and stderr appended to a log, and the working directory and environment given. The child
/// survives this process.
/// </summary>
internal sealed class PosixSpawner(bool reap = false) : IDetachedSpawner
{
    private const int ReaperStackBytes = 64 * 1024;

    private const short SetSigDef = 0x0004;
    private const short SetSidMac = 0x0400;
    private const short SetSidLinux = 0x0080;
    private const short CloexecDefaultMac = 0x4000;

    private const int OWronly = 1;
    private const int OCreatMac = 0x0200;
    private const int OAppendMac = 0x0008;
    private const int OCreatLinux = 0x0040;
    private const int OAppendLinux = 0x0400;

    public unsafe int SpawnDetached(DetachedSpawnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Argv.Count == 0)
        {
            throw new ArgumentException("argv is empty", nameof(request));
        }

        var mac = OperatingSystem.IsMacOS();
        var owned = new List<nint>();
        try
        {
            var path = Alloc(request.Argv[0], owned);
            var argv = stackalloc nint[request.Argv.Count + 1];
            for (var i = 0; i < request.Argv.Count; i++)
            {
                argv[i] = Alloc(request.Argv[i], owned);
            }

            argv[request.Argv.Count] = 0;

            var env = request.Environment.Where(static kv => kv.Value is not null).ToList();
            if (!env.Any(static kv => kv.Key == "PATH"))
            {
                env.Add(new KeyValuePair<string, string?>("PATH", System.Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } inherited ? inherited : "/usr/bin:/bin"));
            }

            var envp = stackalloc nint[env.Count + 1];
            for (var i = 0; i < env.Count; i++)
            {
                envp[i] = Alloc($"{env[i].Key}={env[i].Value}", owned);
            }

            envp[env.Count] = 0;

            var attr = stackalloc byte[SpawnLibc.OpaqueBytes];
            var actions = stackalloc byte[SpawnLibc.OpaqueBytes];
            var signals = stackalloc byte[SpawnLibc.OpaqueBytes];
            Check(SpawnLibc.AttrInit(attr), "posix_spawnattr_init");
            Check(SpawnLibc.FileActionsInit(actions), "posix_spawn_file_actions_init");
            try
            {
                // A .NET child saves the SIGUSR1 action it finds as the one to chain its thread-suspension signal to; if exec left
                // SA_SIGINFO on it, its first suspension jumps to a null handler. Naming the signals here has the kernel clear that.
                _ = SpawnLibc.SigEmptySet(signals);
                foreach (var signal in Libc.ChildDefaultSignals)
                {
                    _ = SpawnLibc.SigAddSet(signals, signal);
                }

                Check(SpawnLibc.AttrSetSigDefault(attr, signals), "posix_spawnattr_setsigdefault");
                var flags = (short)(SetSigDef | (mac ? SetSidMac | CloexecDefaultMac : SetSidLinux));
                Check(SpawnLibc.AttrSetFlags(attr, flags), "posix_spawnattr_setflags");
                var devNull = Alloc("/dev/null", owned);
                var log = Alloc(request.LogPath, owned);
                var cwd = Alloc(request.WorkingDirectory, owned);
                var append = OWronly | (mac ? OCreatMac | OAppendMac : OCreatLinux | OAppendLinux);
                Check(SpawnLibc.FileActionsAddOpen(actions, 0, (byte*)devNull, 0, 0), "addopen stdin");
                Check(SpawnLibc.FileActionsAddOpen(actions, 1, (byte*)log, append, 0x1A4), "addopen stdout");
                Check(SpawnLibc.FileActionsAddOpen(actions, 2, (byte*)log, append, 0x1A4), "addopen stderr");
                Check(SpawnLibc.FileActionsAddChdir(actions, (byte*)cwd), "addchdir");

                int pid;
                Check(SpawnLibc.PosixSpawn(&pid, (byte*)path, actions, attr, argv, envp), "posix_spawn");
                if (reap)
                {
                    StartReaper(pid);
                }

                return pid;
            }
            finally
            {
                _ = SpawnLibc.FileActionsDestroy(actions);
                _ = SpawnLibc.AttrDestroy(attr);
            }
        }
        finally
        {
            foreach (var p in owned)
            {
                Marshal.FreeCoTaskMem(p);
            }
        }
    }

    /// <summary>One background thread per child, blocked in <c>waitpid</c> until it ends. Any answer but an interrupted call ends the wait.</summary>
    private static unsafe void StartReaper(int pid) =>
        new Thread(
            () =>
            {
                while (Libc.WaitPid(pid, null, 0) < 0 && Libc.Errno == Libc.Eintr)
                {
                }
            },
            ReaperStackBytes)
        { IsBackground = true, Name = "reap-detached-child" }.Start();

    private static nint Alloc(string value, List<nint> owned)
    {
        var p = Marshal.StringToCoTaskMemUTF8(value);
        owned.Add(p);
        return p;
    }

    private static void Check(int errno, string what)
    {
        if (errno != 0)
        {
            throw new IOException($"{what} failed (errno {errno})");
        }
    }
}
