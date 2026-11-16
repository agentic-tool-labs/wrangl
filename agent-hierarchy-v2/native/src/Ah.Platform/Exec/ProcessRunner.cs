using System.Diagnostics;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>
/// Runs a command to completion with its output captured: <c>posix_spawn</c> of a fixed argv (never a shell), in its own session so
/// the whole process group can be killed, with stdin from the request (or <c>/dev/null</c>), both output pipes pumped at once and
/// capped, and the group killed on timeout or once the cap is hit. The child's environment is exactly the request's map when one is
/// given, else this process's own.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    private const short SetSigDef = 0x0004;
    private const short SetSidMac = 0x0400;
    private const short SetSidLinux = 0x0080;
    private const short CloexecDefaultMac = 0x4000;
    private const int SigKill = 9;
    private const int WaitNoHang = 1;
    private const int ORdonly = 0;
    private const int OCloexecLinux = 0x80000;

    /// <summary>How long the readers get to finish once the child has exited, before a lingering descendant's pipe is abandoned.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(1);

    public ProcessResult Run(ProcessRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var start = Ah.Core.Time.PhaseTrace.Begin();
        var result = RunCore(request);
        if (Ah.Core.Time.PhaseTrace.On)
        {
            Ah.Core.Time.PhaseTrace.End("spawn " + string.Join(' ', request.Argv.Take(3)), start);
        }

        return result;
    }

    private unsafe ProcessResult RunCore(ProcessRequest request)
    {
        if (request.Argv.Count == 0)
        {
            throw new ArgumentException("argv is empty", nameof(request));
        }

        var environment = BuildEnvironment(request.Environment);
        var executable = Resolve(request.Argv[0], environment);
        if (executable is null)
        {
            return new ProcessResult(127, null, false, false, default, default);
        }

        var mac = OperatingSystem.IsMacOS();
        var owned = new List<nint>();
        var outPipe = stackalloc int[2];
        var errPipe = stackalloc int[2];
        var inPipe = stackalloc int[2];
        var hasStdin = !request.Stdin.IsEmpty;
        var opened = new List<int>();
        try
        {
            MakePipe(outPipe, opened);
            MakePipe(errPipe, opened);
            if (hasStdin)
            {
                MakePipe(inPipe, opened);
            }

            var argv = stackalloc nint[request.Argv.Count + 1];
            for (var i = 0; i < request.Argv.Count; i++)
            {
                argv[i] = Alloc(i == 0 ? executable : request.Argv[i], owned);
            }

            argv[request.Argv.Count] = 0;
            var envp = stackalloc nint[environment.Count + 1];
            for (var i = 0; i < environment.Count; i++)
            {
                envp[i] = Alloc(environment[i], owned);
            }

            envp[environment.Count] = 0;

            var attr = stackalloc byte[SpawnLibc.OpaqueBytes];
            var actions = stackalloc byte[SpawnLibc.OpaqueBytes];
            var signals = stackalloc byte[SpawnLibc.OpaqueBytes];
            Check(SpawnLibc.AttrInit(attr), "posix_spawnattr_init");
            Check(SpawnLibc.FileActionsInit(actions), "posix_spawn_file_actions_init");
            int pid;
            try
            {
                _ = SpawnLibc.SigEmptySet(signals);
                foreach (var signal in Libc.ChildDefaultSignals)
                {
                    _ = SpawnLibc.SigAddSet(signals, signal);
                }

                Check(SpawnLibc.AttrSetSigDefault(attr, signals), "posix_spawnattr_setsigdefault");
                var flags = (short)(SetSigDef | (mac ? SetSidMac | CloexecDefaultMac : SetSidLinux));
                Check(SpawnLibc.AttrSetFlags(attr, flags), "posix_spawnattr_setflags");
                if (hasStdin)
                {
                    Check(SpawnLibc.FileActionsAddDup2(actions, inPipe[0], 0), "adddup2 stdin");
                }
                else
                {
                    var devNull = Alloc("/dev/null", owned);
                    Check(SpawnLibc.FileActionsAddOpen(actions, 0, (byte*)devNull, ORdonly, 0), "addopen stdin");
                }

                Check(SpawnLibc.FileActionsAddDup2(actions, outPipe[1], 1), "adddup2 stdout");
                Check(SpawnLibc.FileActionsAddDup2(actions, errPipe[1], 2), "adddup2 stderr");
                if (request.WorkingDirectory is not null)
                {
                    Check(SpawnLibc.FileActionsAddChdir(actions, (byte*)Alloc(request.WorkingDirectory, owned)), "addchdir");
                }

                var path = Alloc(executable, owned);
                var rc = SpawnLibc.PosixSpawn(&pid, (byte*)path, actions, attr, argv, envp);
                if (rc != 0)
                {
                    CloseAll(opened);
                    return new ProcessResult(127, null, false, false, default, default);
                }
            }
            finally
            {
                _ = SpawnLibc.FileActionsDestroy(actions);
                _ = SpawnLibc.AttrDestroy(attr);
            }

            // The child's ends belong to the child now.
            _ = Libc.Close(outPipe[1]);
            _ = Libc.Close(errPipe[1]);
            if (hasStdin)
            {
                _ = Libc.Close(inPipe[0]);
            }

            return Collect(pid, outPipe[0], errPipe[0], hasStdin ? inPipe[1] : -1, request);
        }
        finally
        {
            foreach (var p in owned)
            {
                System.Runtime.InteropServices.Marshal.FreeCoTaskMem(p);
            }
        }
    }

    private static ProcessResult Collect(int pid, int outFd, int errFd, int inFd, ProcessRequest request)
    {
        var cap = request.OutputCapBytes;
        var killedForCap = 0;
        var outBuffer = new Capture(cap);
        var errBuffer = new Capture(cap);

        void KillGroup()
        {
            _ = Libc.Kill(-pid, SigKill);
            _ = Libc.Kill(pid, SigKill);
        }

        void OnOverflow()
        {
            if (Interlocked.Exchange(ref killedForCap, 1) == 0)
            {
                KillGroup();
            }
        }

        var outReader = new Thread(() => Drain(outFd, outBuffer, OnOverflow)) { IsBackground = true };
        var errReader = new Thread(() => Drain(errFd, errBuffer, OnOverflow)) { IsBackground = true };
        outReader.Start();
        errReader.Start();
        Thread? writer = null;
        if (inFd >= 0)
        {
            var bytes = request.Stdin.ToArray();
            writer = new Thread(() => Feed(inFd, bytes)) { IsBackground = true };
            writer.Start();
        }

        var timeout = request.Timeout;
        var clock = Stopwatch.StartNew();
        var timedOut = false;
        var status = 0;
        var sleep = 1;
        while (true)
        {
            if (WaitNoHangOnce(pid, out status))
            {
                break;
            }

            if (timeout is { } limit && clock.Elapsed >= limit)
            {
                timedOut = true;
                KillGroup();
                _ = WaitBlocking(pid, out status);
                break;
            }

            // a child that exits closes its stdout, so that wakes the wait at once; one that closed it and runs on is polled
            if (!outReader.IsAlive)
            {
                Thread.Sleep(sleep);
            }
            else if (outReader.Join(sleep))
            {
                sleep = 1;
                continue;
            }

            sleep = Math.Min(sleep * 2, 10);
        }

        // The child is gone. Its readers finish when every holder of the pipe has let go; a descendant that outlives it keeps the pipe
        // open, so the wait is bounded and what was read is returned.
        var deadline = clock.Elapsed + DrainGrace;
        foreach (var t in new[] { outReader, errReader })
        {
            var left = deadline - clock.Elapsed;
            _ = t.Join(left > TimeSpan.Zero ? left : TimeSpan.Zero);
        }

        if (timedOut || killedForCap == 1)
        {
            // Nothing the group started outlives a timeout or an overrun.
            KillGroup();
            foreach (var t in new[] { outReader, errReader })
            {
                _ = t.Join(TimeSpan.FromSeconds(2));
            }
        }

        var truncated = outBuffer.Overflowed || errBuffer.Overflowed;
        int? exitCode = null;
        int? signal = null;
        if (!timedOut && !truncated)
        {
            if ((status & 0x7f) == 0)
            {
                exitCode = (status >> 8) & 0xff;
            }
            else
            {
                signal = status & 0x7f;
            }
        }

        return new ProcessResult(exitCode, signal, timedOut, truncated, outBuffer.Snapshot(), errBuffer.Snapshot());
    }

    private static unsafe void Drain(int fd, Capture capture, Action overflow)
    {
        // on the reader's own stack (ah2-52): each reader is a new thread, and an allocation trace under bench/team-load.sh showed the
        // rent here allocating (new arrays and pool set-up) under those threads. Capture.Add copies what was read, so the buffer never escapes
        Span<byte> buffer = stackalloc byte[8192];
        try
        {
            while (true)
            {
                nint n;
                fixed (byte* p = buffer)
                {
                    n = Libc.ReadFd(fd, p, (nuint)buffer.Length);
                }

                if (n < 0)
                {
                    if (Libc.Errno == Libc.Eintr)
                    {
                        continue;
                    }

                    break;
                }

                if (n == 0)
                {
                    break;
                }

                if (!capture.Add(buffer[..(int)n]))
                {
                    overflow();
                }
            }
        }
        finally
        {
            _ = Libc.Close(fd);
        }
    }

    private static void Feed(int fd, byte[] bytes)
    {
        try
        {
            var offset = 0;
            while (offset < bytes.Length)
            {
                var n = Libc.Write(fd, bytes.AsSpan(offset), (nuint)(bytes.Length - offset));
                if (n < 0)
                {
                    if (Libc.Errno == Libc.Eintr)
                    {
                        continue;
                    }

                    break;
                }

                offset += (int)n;
            }
        }
        finally
        {
            _ = Libc.Close(fd);
        }
    }

    private static unsafe bool WaitNoHangOnce(int pid, out int status)
    {
        int s = 0;
        while (true)
        {
            var r = Libc.WaitPid(pid, &s, WaitNoHang);
            if (r == pid)
            {
                status = s;
                return true;
            }

            if (r == 0)
            {
                status = 0;
                return false;
            }

            if (Libc.Errno == Libc.Eintr)
            {
                continue;
            }

            // No such child (already reaped elsewhere): treat as exited with no status.
            status = 0;
            return true;
        }
    }

    private static unsafe bool WaitBlocking(int pid, out int status)
    {
        int s = 0;
        while (true)
        {
            var r = Libc.WaitPid(pid, &s, 0);
            if (r == pid)
            {
                status = s;
                return true;
            }

            if (r < 0 && Libc.Errno == Libc.Eintr)
            {
                continue;
            }

            status = 0;
            return false;
        }
    }

    private static unsafe void MakePipe(int* descriptors, List<int> opened)
    {
        var rc = OperatingSystem.IsMacOS() ? Libc.Pipe(descriptors) : Libc.Pipe2(descriptors, OCloexecLinux);
        if (rc != 0)
        {
            throw new IOException($"pipe failed (errno {Libc.Errno})");
        }

        opened.Add(descriptors[0]);
        opened.Add(descriptors[1]);
    }

    private static void CloseAll(List<int> descriptors)
    {
        foreach (var fd in descriptors)
        {
            _ = Libc.Close(fd);
        }
    }

    private static List<string> BuildEnvironment(IReadOnlyDictionary<string, string?>? map)
    {
        var result = new List<string>();
        if (map is null)
        {
            foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
            {
                result.Add($"{entry.Key}={entry.Value}");
            }

            return result;
        }

        foreach (var (key, value) in map)
        {
            if (value is not null)
            {
                result.Add($"{key}={value}");
            }
        }

        // A map that names no PATH would leave the child unable to find any program it runs by name.
        if (!result.Any(static e => e.StartsWith("PATH=", StringComparison.Ordinal)))
        {
            result.Add("PATH=" + (System.Environment.GetEnvironmentVariable("PATH") is { Length: > 0 } inherited ? inherited : "/usr/bin:/bin"));
        }

        return result;
    }

    /// <summary>The file a bare name stands for on the PATH the child will have, as <c>execvp</c> would find it; null when none.</summary>
    private static string? Resolve(string name, List<string> environment)
    {
        if (name.Contains('/', StringComparison.Ordinal))
        {
            return name;
        }

        string? path = null;
        foreach (var entry in environment)
        {
            if (entry.StartsWith("PATH=", StringComparison.Ordinal))
            {
                path = entry["PATH=".Length..];
                break;
            }
        }

        foreach (var dir in (path ?? string.Empty).Split(':'))
        {
            if (dir.Length == 0)
            {
                continue;
            }

            var candidate = dir.EndsWith('/') ? dir + name : dir + "/" + name;
            if (File.Exists(candidate) && Libc.Access(candidate, 1) == 0)
            {
                return candidate;
            }
        }

        return null;
    }

    private static nint Alloc(string value, List<nint> owned)
    {
        var p = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(value);
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

    private sealed class Capture(int cap)
    {
        private readonly object _gate = new();
        private readonly List<byte> _kept = [];

        internal bool Overflowed { get; private set; }

        /// <summary>Keeps what fits under the cap; false once the cap was exceeded.</summary>
        internal bool Add(ReadOnlySpan<byte> read)
        {
            lock (_gate)
            {
                var room = cap - _kept.Count;
                if (read.Length <= room)
                {
                    _kept.AddRange(read);
                    return true;
                }

                if (room > 0)
                {
                    _kept.AddRange(read[..room]);
                }

                var first = !Overflowed;
                Overflowed = true;
                return !first;
            }
        }

        internal byte[] Snapshot()
        {
            lock (_gate)
            {
                return _kept.ToArray();
            }
        }
    }
}
