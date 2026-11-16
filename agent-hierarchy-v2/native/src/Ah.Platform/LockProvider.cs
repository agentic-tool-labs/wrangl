using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>
/// A lock is a file holding <c>{"pid","at","nonce"}</c>; it is created by writing a temporary file and hard-linking it into
/// place, so the content is there from the instant the lock exists. A lock younger than the stale age is never taken over,
/// whatever its pid. Every change to the lock path (creating it, removing a stale one, releasing) happens under an exclusive
/// <c>flock</c> on the lock's directory, so a taker that judged a lock stale removes it only if it is still that very lock when
/// the section is entered: a lock another taker put in its place is never removed, and no moment exists in which a live lock is
/// out of the way. The kernel drops the flock with its holder, so a crash leaves nothing to expire. Release removes the lock
/// only when its content is still the releaser's own. <c>beforeTakeover</c> runs between judging a lock stale and removing it,
/// the window in which another taker can act; tests use it to stage that race.
/// </summary>
internal sealed class LockProvider(
    IClock clock,
    IRandom random,
    TimeSpan staleAfter,
    Action<string>? beforeTakeover = null,
    IHookErrorLog? errors = null,
    Func<int, int, int>? flock = null) : ILockProvider
{
    private const int MaxRounds = 6;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private const int LockUnlock = 8;
    private static readonly TimeSpan TryAcquireSectionWait = TimeSpan.FromMilliseconds(250);

    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private const long StaleFileMaxAgeMs = 60 * 60 * 1000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(5);

    private int Pid { get; } = Environment.ProcessId;

    public TimeSpan CallerWait => ILockProvider.WaitFor(staleAfter);

    public IDisposable? TryAcquire(string lockPath)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        var deadline = Stopwatch.GetTimestamp() + (long)(TryAcquireSectionWait.TotalSeconds * Stopwatch.Frequency);
        return Take(lockPath, deadline) is { } mine ? new Held(lockPath, mine, this) : null;
    }

    public IDisposable? TryHold(string lockPath)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        // The file is created through .NET, which takes its own advisory lock while it has the file open, so a file that exists
        // (and may be held) is never opened that way; the descriptor that carries the flock is opened without O_CREAT.
        if (!File.Exists(lockPath))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                using (File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite))
                {
                }
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                // another taker created it first
            }
        }

        var fd = Libc.Open(lockPath, Libc.OCloexec);
        if (fd < 0)
        {
            throw new IOException($"open '{lockPath}' failed: errno {Libc.Errno}");
        }

        for (;;)
        {
            var result = Flock(fd, LockExclusive | LockNonBlocking);
            if (result == 0)
            {
                return new Gate(fd, busy: false, serialised: true, this);
            }

            if (result == Libc.Eintr)
            {
                continue;
            }

            _ = Libc.Close(fd);
            if (result == Libc.WouldBlock)
            {
                return null;
            }

            throw new IOException($"flock '{lockPath}' failed: errno {result.ToString(CultureInfo.InvariantCulture)}");
        }
    }

    public IDisposable? Acquire(string lockPath, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(lockPath);
        var deadline = Stopwatch.GetTimestamp() + (long)(wait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (Take(lockPath, deadline) is { } mine)
            {
                return new Held(lockPath, mine, this);
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return null;
            }

            Thread.Sleep(PollInterval);
        }
    }

    private long NowMs() => clock.UtcNow.ToUnixTimeMilliseconds();

    private string RandomHex(int bytes)
    {
        Span<byte> buffer = stackalloc byte[bytes];
        random.Fill(buffer);
        return Convert.ToHexStringLower(buffer);
    }

    private Record? Take(string lockPath, long deadline)
    {
        var now = NowMs();
        var staleMs = staleAfter.TotalMilliseconds;
        for (var round = 0; round < MaxRounds; round++)
        {
            var mine = new Record(Pid, NowMs(), RandomHex(16));
            var tmp = $"{lockPath}.{Pid.ToString(CultureInfo.InvariantCulture)}.{RandomHex(8)}";
            File.WriteAllBytes(tmp, mine.ToBytes());
            var linked = false;
            Content seen = default;
            try
            {
                using var gate = Enter(lockPath, deadline);
                if (gate.Busy)
                {
                    return null;
                }

                if (Libc.Link(tmp, lockPath) == 0)
                {
                    linked = true;
                }
                else if (Libc.Errno != Libc.Eexist)
                {
                    throw new IOException($"link '{tmp}' -> '{lockPath}' failed: errno {Libc.Errno}");
                }
                else
                {
                    seen = Read(lockPath);
                }
            }
            finally
            {
                _ = Libc.Unlink(tmp);
            }

            if (linked)
            {
                SweepStale(lockPath);
                return mine;
            }

            if (seen.IsFalsy)
            {
                continue;
            }

            if (now - seen.At < staleMs)
            {
                return null;
            }

            beforeTakeover?.Invoke(lockPath);
            using var section = Enter(lockPath, deadline);
            if (section.Busy)
            {
                return null;
            }

            if (!section.Serialised)
            {
                // without the section a takeover could remove a lock another taker has just put there: a lock that cannot be
                // judged safely is left to age out of the filesystem's own failure
                return null;
            }

            if (Read(lockPath).SameAs(seen))
            {
                _ = Libc.Unlink(lockPath);
            }
        }

        return null;
    }

    private int Flock(int fd, int operation)
    {
        if (flock is not null)
        {
            return flock(fd, operation);
        }

        return Libc.Flock(fd, operation) == 0 ? 0 : Libc.Errno;
    }

    /// <summary>
    /// Takes the exclusive flock of the lock's directory, polling without blocking until <paramref name="deadline"/> (a stopwatch
    /// timestamp). <c>Busy</c> is a section that stayed held past the deadline; a section the file system cannot give is neither busy nor
    /// serialised: creating and releasing go on without it, a takeover does not, and one hook-errors row names the directory and errno.
    /// </summary>
    private Gate Enter(string lockPath, long deadline)
    {
        var directory = Path.GetDirectoryName(lockPath);
        var target = string.IsNullOrEmpty(directory) ? "." : directory;
        var fd = Libc.Open(target, Libc.OCloexec);
        if (fd < 0)
        {
            Report(target, Libc.Errno);
            return Gate.Unserialised;
        }

        for (;;)
        {
            var result = Flock(fd, LockExclusive | LockNonBlocking);
            if (result == 0)
            {
                return new Gate(fd, busy: false, serialised: true, this);
            }

            if (result == Libc.Eintr)
            {
                continue;
            }

            if (result != Libc.WouldBlock)
            {
                _ = Libc.Close(fd);
                Report(target, result);
                return Gate.Unserialised;
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                _ = Libc.Close(fd);
                return Gate.TimedOut;
            }

            Thread.Sleep(PollInterval);
        }
    }

    private void Report(string directory, int errno)
    {
        if (errors is null || !_reported.TryAdd(directory, 0))
        {
            return;
        }

        errors.Log("ah-lock", $"stale-lock takeover is skipped in {directory}: its directory cannot be flocked (errno {errno.ToString(CultureInfo.InvariantCulture)})");
    }

    private sealed class Gate : IDisposable
    {
        internal static readonly Gate Unserialised = new(-1, busy: false, serialised: false, null);
        internal static readonly Gate TimedOut = new(-1, busy: true, serialised: false, null);

        private readonly int _fd;
        private readonly LockProvider? _owner;

        internal Gate(int fd, bool busy, bool serialised, LockProvider? owner)
        {
            _fd = fd;
            Busy = busy;
            Serialised = serialised;
            _owner = owner;
        }

        internal bool Busy { get; }

        internal bool Serialised { get; }

        public void Dispose()
        {
            if (_fd >= 0 && _owner is not null)
            {
                _ = _owner.Flock(_fd, LockUnlock);
                _ = Libc.Close(_fd);
            }
        }
    }

    private void SweepStale(string lockPath)
    {
        var directory = Path.GetDirectoryName(lockPath);
        var prefix = Path.GetFileName(lockPath) + ".stale.";
        try
        {
            foreach (var file in Directory.EnumerateFileSystemEntries(string.IsNullOrEmpty(directory) ? "." : directory))
            {
                if (!Path.GetFileName(file).StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var mtimeMs = new DateTimeOffset(File.GetLastWriteTimeUtc(file)).ToUnixTimeMilliseconds();
                    if (NowMs() - mtimeMs > StaleFileMaxAgeMs)
                    {
                        _ = Libc.Unlink(file);
                    }
                }
                catch (IOException)
                {
                    // gone meanwhile
                }
            }
        }
        catch (IOException)
        {
            // no directory to sweep
        }
    }

    private static Content Read(string path)
    {
        try
        {
            return JsonText.TryParse(File.ReadAllBytes(path), out var value) ? new Content(value, true) : default;
        }
        catch (IOException)
        {
            return default;
        }
        catch (UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>What a lock file held, as JavaScript would see it after <c>JSON.parse</c>.</summary>
    private readonly record struct Content(JsonValue Value, bool Parsed)
    {
        /// <summary>Unreadable, or JSON that is falsy in JavaScript: nothing to judge.</summary>
        internal bool IsFalsy => !Parsed || Value.Kind switch
        {
            JsonKind.Null => true,
            JsonKind.Bool => !Value.AsBool(),
            JsonKind.Number => Value.AsNumber() == 0 || double.IsNaN(Value.AsNumber()),
            JsonKind.Text => Value.AsString().Length == 0,
            _ => false,
        };

        /// <summary><c>Number.isFinite(seen.at) ? seen.at : 0</c></summary>
        internal double At => Field("at") is { Kind: JsonKind.Number } n && double.IsFinite(n.AsNumber()) ? n.AsNumber() : 0;

        /// <summary><c>!!a &amp;&amp; !!b &amp;&amp; a.pid === b.pid &amp;&amp; a.at === b.at &amp;&amp; a.nonce === b.nonce</c></summary>
        internal bool SameAs(Content other) =>
            !IsFalsy && !other.IsFalsy && StrictEquals(Field("pid"), other.Field("pid"))
            && StrictEquals(Field("at"), other.Field("at")) && StrictEquals(Field("nonce"), other.Field("nonce"));

        internal bool Is(Record mine) =>
            !IsFalsy && Field("pid") is { Kind: JsonKind.Number } p && p.AsNumber() == mine.Pid
            && Field("at") is { Kind: JsonKind.Number } a && a.AsNumber() == mine.At
            && Field("nonce") is { Kind: JsonKind.Text } n && string.Equals(n.AsString(), mine.Nonce, StringComparison.Ordinal);

        private JsonValue? Field(string name)
        {
            if (Parsed && Value.Kind == JsonKind.Map && Value.AsObject().TryGetValue(name, out var v))
            {
                return v;
            }

            return null;
        }

        private static bool StrictEquals(JsonValue? a, JsonValue? b)
        {
            if (a is null || b is null)
            {
                return a is null && b is null;
            }

            if (a.Value.Kind != b.Value.Kind)
            {
                return false;
            }

            return a.Value.Kind switch
            {
                JsonKind.Null => true,
                JsonKind.Bool => a.Value.AsBool() == b.Value.AsBool(),
                JsonKind.Number => a.Value.AsNumber() == b.Value.AsNumber(),
                JsonKind.Text => string.Equals(a.Value.AsString(), b.Value.AsString(), StringComparison.Ordinal),
                _ => false,
            };
        }
    }

    private sealed record Record(int Pid, long At, string Nonce)
    {
        internal byte[] ToBytes()
        {
            var obj = new JsonObject { ["pid"] = Pid, ["at"] = At, ["nonce"] = Nonce };
            return JsonText.ToUtf8(obj, 0);
        }
    }

    private sealed class Held(string lockPath, Record mine, LockProvider owner) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            // a release that cannot take the section within the wait still removes its own lock: leaving it would hold others out
            // until it ages, and the content check below keeps it from removing anyone else's
            using var gate = owner.Enter(lockPath, Stopwatch.GetTimestamp() + (long)(TryAcquireSectionWait.TotalSeconds * Stopwatch.Frequency));
            if (Read(lockPath).Is(mine))
            {
                _ = Libc.Unlink(lockPath);
            }
        }
    }
}
