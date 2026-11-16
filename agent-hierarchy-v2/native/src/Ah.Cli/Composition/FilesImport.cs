using System.Collections.Concurrent;

namespace Ah.Cli.Composition;

/// <summary>The callers' side of the files import pass (files-off-disk §3.1): the dir list, the run over it, and the daemon's background worker.</summary>
internal static class FilesImport
{
    /// <summary>The dirs of the user's known-dirs index (b397: rows of the state database); none when it cannot be read.</summary>
    internal static IReadOnlyList<string> KnownDirs(string ahHome) => [.. Ah.StateDb.StateDbModule.KnownDirs(ahHome).Select(static e => e.Key)];

    /// <summary>The pass over the whole dir list (§3.3); <paramref name="record"/> gets each dir with the record it stored.</summary>
    internal static void Run(string ahHome, IEnumerable<string> listed, bool force, Action<string, string> record)
    {
        foreach (var dir in Ah.StateDb.StateDbModule.FilesImportDirs(ahHome, listed))
        {
            if (Ah.StateDb.StateDbModule.ImportHierDirFiles(ahHome, dir, force) is { } stored)
            {
                record(dir, stored);
            }
        }
    }

    /// <summary>
    /// The daemon's one background worker: imports one dir at a time, each dir once per process, never on a caller's thread. <see cref="Start"/>
    /// queues the start list (through <paramref name="admit"/>, as the dir gate filters it) once the database is open; <see cref="OnAdmit"/> queues
    /// each dir an admission lets through. Nothing it does can fail a caller or the daemon: every failure is logged and the next dir runs.
    /// </summary>
    internal sealed class Worker(string ahHome, Func<IReadOnlyList<string>, IReadOnlyList<string>> admit, Action<string> log, Action openDb)
    {
        private readonly ConcurrentDictionary<string, byte> _seen = new(StringComparer.Ordinal);
        private readonly BlockingCollection<string> _queue = [];

        /// <summary>Starts the worker; its first job is the start pass over the dir list.</summary>
        internal void Start() => new Thread(Loop) { IsBackground = true, Name = "ahd files import" }.Start();

        /// <summary>Admits <paramref name="dirs"/> and queues each newly admitted dir; returns what was admitted.</summary>
        internal IReadOnlyList<string> OnAdmit(IReadOnlyList<string> dirs)
        {
            var admitted = admit(dirs);
            Queue(admitted);
            return admitted;
        }

        private void Queue(IEnumerable<string> dirs)
        {
            foreach (var dir in dirs.Where(d => _seen.TryAdd(d, 0)))
            {
                _queue.Add(dir);
            }
        }

        private void Loop()
        {
            Guard("start list", () =>
            {
                openDb();
                Queue(admit(Ah.StateDb.StateDbModule.FilesImportDirs(ahHome, KnownDirs(ahHome))));
            });
            foreach (var dir in _queue.GetConsumingEnumerable())
            {
                Guard(dir, () =>
                {
                    if (Ah.StateDb.StateDbModule.ImportHierDirFiles(ahHome, dir) is { } stored)
                    {
                        log($"files import {dir}: {stored}");
                    }
                });
            }
        }

        private void Guard(string what, Action run)
        {
            try
            {
                run();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a dir left incomplete runs again at the next start
                log($"files import {what}: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
