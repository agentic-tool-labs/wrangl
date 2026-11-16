namespace Ah.Watch.Model;

/// <summary>
/// The canonical hierarchy directories the activity lane reads, cached: loaded when the set is refreshed (at start and at each key-set
/// enumeration), never per event, so classifying a hint costs no state read.
/// </summary>
internal sealed class DirSet(Func<IReadOnlyList<string>> load)
{
    private volatile IReadOnlyList<string> _dirs = [];

    public event Action? Changed;

    public IReadOnlyList<string> Current => _dirs;

    /// <summary>The set, loaded again while it is empty: a home's first pool appears with nothing else looking for it.</summary>
    public IReadOnlyList<string> CurrentOrRefresh() => _dirs is { Count: > 0 } known ? known : Refresh();

    /// <summary>Loads the set again; <see cref="Changed"/> fires when it differs from the last one.</summary>
    public IReadOnlyList<string> Refresh()
    {
        var next = load().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var before = _dirs;
        _dirs = next;
        if (!before.SequenceEqual(next, StringComparer.Ordinal))
        {
            Changed?.Invoke();
        }

        return next;
    }
}

/// <summary>
/// The file-change hint source: for each active canonical directory (chosen by <paramref name="activeDirs"/>; every directory when it
/// is null) one watcher on the directory itself and one recursive watcher on each lane directory below it, whose events are kept only for
/// the paths the lanes read (<see cref="HintsFor"/>). Any kept event is a
/// hint for the path it names; the truth is always the re-read. An error (an overflow among them) asks for a reconcile and restarts that
/// watcher. With <paramref name="tree"/> it also watches the stores of the Tree's lanes: each directory's <c>team.json</c>,
/// <c>teams</c>, <c>grants</c> and <c>pipeline</c>, and under <paramref name="home"/> the <c>registrations</c> directory, and
/// <paramref name="knownDirsFile"/> (which lives in the user's directory, not necessarily under <paramref name="home"/>). A change to
/// the registrations or to the known-directories file sets the active directories again.
/// </summary>
internal sealed class HintWatcher(DirSet dirs, Action<string> onPath, Action onOverflow, bool tree = false, string? home = null, string? knownDirsFile = null, Func<IReadOnlyList<string>>? extraRoots = null, Func<IReadOnlyList<string>, IReadOnlyList<string>>? activeDirs = null, Action<string>? log = null, string? peerPendingFile = null, TimeSpan activeCache = default, Func<long>? clock = null) : IDisposable
{
    private static readonly string[] SubDirectories = ["registrations"];

    /// <summary>The directories below a hierarchy directory that <see cref="HintsFor"/> keeps events of; <c>team.json</c> is the one file above them.</summary>
    private static readonly string[] LaneDirs = ["activity", "msgs", "progress"];
    private static readonly string[] TreeLaneDirs = [.. LaneDirs, "teams", "pipeline"];

    private readonly object _gate = new();
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.Ordinal);
    private bool _disposed;

    /// <summary>A watched place; for a hierarchy directory's watchers, <paramref name="Base"/> is that directory, which <see cref="HintsFor"/> paths are relative to.</summary>
    private sealed record Spec(string Root, bool Recursive, Func<string, bool>? Accept, bool Resync = false, bool Directory = false, string? Base = null);

    /// <summary>The number of watchers running now (for tests and doctor).</summary>
    public int Running
    {
        get
        {
            lock (_gate)
            {
                return _watchers.Count;
            }
        }
    }

    /// <summary>Brings the running watchers in line with <see cref="DirSet.Current"/>.</summary>
    public void Sync()
    {
        var addedLater = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var wanted = new Dictionary<string, Spec>(StringComparer.Ordinal);
            foreach (var dir in ActiveNow())
            {
                // the directory itself only, and each lane directory below it recursively: its tmp/ holds every member's $TMPDIR, whose build
                // and test churn a recursive watcher on the directory took in event by event (b459)
                wanted["d:" + dir] = new Spec(dir, false, null, Directory: true, Base: dir);
                foreach (var sub in tree ? TreeLaneDirs : LaneDirs)
                {
                    var path = Path.Combine(dir, sub);
                    if (Directory.Exists(path))
                    {
                        wanted["d:" + path] = new Spec(path, true, null, Directory: true, Base: dir);
                    }
                }
            }

            if (tree && home is not null)
            {
                AddTree(wanted, home, static n => n == "registrations", "registrations");
            }

            if (tree && knownDirsFile is not null && Path.GetDirectoryName(knownDirsFile) is { Length: > 0 } knownDir)
            {
                var name = Path.GetFileName(knownDirsFile);
                wanted["k:" + knownDir] = new Spec(knownDir, false, n => n == name, Resync: true);
            }

            if (tree && peerPendingFile is not null && Path.GetDirectoryName(peerPendingFile) is { Length: > 0 } peerDir && Directory.Exists(peerDir))
            {
                var peerName = Path.GetFileName(peerPendingFile);
                wanted["p:" + peerDir] = new Spec(peerDir, false, n => n == peerName);
            }

            foreach (var extra in extraRoots?.Invoke() ?? [])
            {
                if (Directory.Exists(extra))
                {
                    wanted["t:" + extra] = new Spec(extra, false, static n => n.EndsWith(".jsonl", StringComparison.Ordinal));
                }
            }

            foreach (var gone in _watchers.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
            {
                _watchers[gone].Dispose();
                _ = _watchers.Remove(gone);
            }

            foreach (var (id, spec) in wanted.Where(w => !_watchers.ContainsKey(w.Key)))
            {
                var watcher = Create(id, spec);
                if (watcher is not null)
                {
                    _watchers[id] = watcher;
                    addedLater |= spec.Directory && !_restarting.Remove(id);
                }
            }

            _ = _restarting.RemoveWhere(k => !wanted.ContainsKey(k));
        }

        if (addedLater)
        {
            // what was written in a directory before its watcher existed raised no hint, so the lanes read it again
            onOverflow();
        }
    }

    private bool _activeFaultLogged;
    private int _syncDirty;
    private int _syncRunning;

    /// <summary>
    /// Runs <see cref="Sync"/> on the thread pool, not on the caller's thread: starting a watcher waits on the system's file-event
    /// service, which with 40 daemons starting at once held the read model's thread for up to 150 s (b233). One loop runs the Syncs, on
    /// at most one pool thread ever: an ask marks the set dirty and starts the loop only when none is running; the loop runs Sync again
    /// while asks came in during the last one.
    /// </summary>
    public void SyncSoon()
    {
        Volatile.Write(ref _syncDirty, 1);
        if (Interlocked.CompareExchange(ref _syncRunning, 1, 0) == 0)
        {
            _ = ThreadPool.UnsafeQueueUserWorkItem(static w => w.SyncLoop(), this, preferLocal: false);
        }
    }

    private void SyncLoop()
    {
        while (true)
        {
            while (Interlocked.Exchange(ref _syncDirty, 0) == 1)
            {
                try
                {
                    Sync();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    log?.Invoke($"watching the directories failed: {e.Message}");
                }
            }

            Volatile.Write(ref _syncRunning, 0);

            // an ask between the last check and the release found the loop running and started none: take it here
            if (Volatile.Read(ref _syncDirty) == 0 || Interlocked.CompareExchange(ref _syncRunning, 1, 0) != 0)
            {
                return;
            }
        }
    }
    private readonly HashSet<string> _restarting = new(StringComparer.Ordinal);
    private IReadOnlyList<string>? _activeHeld;
    private long _activeAtMs;
    private bool _activeResyncPending;
    private Timer? _activeResync;

    /// <summary>
    /// The directories to watch. <see cref="Sync"/> runs on file-event threads, where a throw ends the process, and the chooser reads
    /// registrations that may be half written; a failure watches every directory and is logged once.
    /// </summary>
    private IReadOnlyList<string> ActiveNow()
    {
        if (activeDirs is null)
        {
            return dirs.Current;
        }

        try
        {
            var now = clock?.Invoke() ?? Environment.TickCount64;
            if (activeCache > TimeSpan.Zero)
            {
                if (_activeHeld is { } held && now - _activeAtMs < activeCache.TotalMilliseconds)
                {
                    ResyncAfterCache(activeCache.TotalMilliseconds - (now - _activeAtMs));
                    return held;
                }
            }

            var chosen = activeDirs(dirs.Current);
            _activeHeld = chosen;
            _activeAtMs = now;
            return chosen;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (!_activeFaultLogged)
            {
                _activeFaultLogged = true;
                log?.Invoke($"choosing the directories to watch failed, watching all of them: {e.Message}");
            }

            return dirs.Current;
        }
    }

    /// <summary>
    /// A set served from the cache may be older than the registrations it was chosen from, so one more <see cref="Sync"/> is run when the
    /// cache runs out; asked for again before then, it is still only the one.
    /// </summary>
    private void ResyncAfterCache(double inMs)
    {
        if (_activeResyncPending)
        {
            return;
        }

        _activeResyncPending = true;
        _activeResync ??= new Timer(static state => ((HintWatcher)state!).ResyncNow(), this, Timeout.Infinite, Timeout.Infinite);
        _ = _activeResync.Change(TimeSpan.FromMilliseconds(Math.Max(1, inMs) + 1), Timeout.InfiniteTimeSpan);
    }

    private void ResyncNow()
    {
        lock (_gate)
        {
            _activeResyncPending = false;
        }

        try
        {
            Sync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"watching the directories again failed: {e.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _activeResync?.Dispose();
            foreach (var w in _watchers.Values)
            {
                w.Dispose();
            }

            _watchers.Clear();
        }
    }

    private static void AddTree(Dictionary<string, Spec> wanted, string baseDir, Func<string, bool> rootAccept, string sub)
    {
        wanted["r:" + baseDir] = new Spec(baseDir, false, rootAccept);
        var path = Path.Combine(baseDir, sub);
        if (Directory.Exists(path))
        {
            wanted["s:" + path] = new Spec(path, false, null, Resync: true);
        }
    }

    /// <summary>Whether a change at <paramref name="relative"/> (the path below a hierarchy directory) is one the lanes read.</summary>
    internal static bool HintsFor(string relative, bool tree)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar);
        return parts[0] switch
        {
            "activity" => parts.Length == 2,
            "msgs" => (parts.Length == 2 && parts[1] != "archive") || (parts.Length == 3 && parts[1] == "archive"),
            "progress" => parts.Length == 3,
            "team.json" => tree && parts.Length == 1,
            "teams" => tree && parts.Length == 2,
            "pipeline" => tree && parts.Length >= 2,
            _ => false,
        };
    }

    /// <summary>
    /// A watcher that raised an error is dropped and made again by <see cref="Sync"/>. The lanes are asked to read everything once
    /// for the error, so the watcher made again does not ask for a second read as a newly watched directory would.
    /// </summary>
    internal void Failed(string id)
    {
        lock (_gate)
        {
            if (_watchers.Remove(id, out var failed))
            {
                failed.Dispose();
                _ = _restarting.Add(id);
            }
        }

        onOverflow();
        SyncSoon();
    }

    private FileSystemWatcher? Create(string id, Spec spec)
    {
        var root = spec.Root;
        var hierDir = spec.Base ?? root;
        FileSystemWatcher watcher;
        try
        {
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = spec.Recursive,
                InternalBufferSize = 65536,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.DirectoryName,
            };
        }
        catch (ArgumentException)
        {
            return null;
        }

        void Hint(object? sender, FileSystemEventArgs e)
        {
            if (spec.Directory)
            {
                if (HintsFor(Path.GetRelativePath(hierDir, e.FullPath), tree))
                {
                    onPath(e.FullPath);
                }
                else if (!spec.Recursive && e.Name is { } name && (tree ? TreeLaneDirs : LaneDirs).Contains(name))
                {
                    // a lane directory made or removed after the last Sync gets or loses its own watcher
                    SyncSoon();
                }

                return;
            }

            if (spec.Accept is { } accept)
            {
                if (e.Name is null || !accept(e.Name))
                {
                    return;
                }

                if (SubDirectories.Contains(e.Name))
                {
                    SyncSoon();
                    return;
                }
            }

            onPath(e.FullPath);
            if (spec.Resync)
            {
                SyncSoon();
            }
        }

        watcher.Created += Hint;
        watcher.Changed += Hint;
        watcher.Deleted += Hint;
        watcher.Renamed += (_, e) =>
        {
            if (!spec.Directory || HintsFor(Path.GetRelativePath(hierDir, e.OldFullPath), tree))
            {
                onPath(e.OldFullPath);
            }

            if (!spec.Directory || HintsFor(Path.GetRelativePath(hierDir, e.FullPath), tree))
            {
                onPath(e.FullPath);
            }
        };
        watcher.Error += (_, _) => Failed(id);
        try
        {
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) when (e is IOException or ArgumentException or PlatformNotSupportedException)
        {
            watcher.Dispose();
            return null;
        }

        return watcher;
    }
}
