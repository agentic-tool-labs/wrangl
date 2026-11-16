namespace Ah.Core.Text;

/// <summary>
/// Real paths and containment, decided in one place. A path is resolved the way the file system resolves it: each symbolic link is followed,
/// and <c>..</c> is taken against the parent that is already resolved, never against the text.
/// </summary>
public static class PathGuard
{
    private const int MaxLinkDepth = 40;

    /// <summary>How long a part's on-disk spelling is kept: finding it lists the parent, and every team-file and pool read resolves its whole path. Links are not kept, so a retargeted one is followed at once; a case-only rename shows within this.</summary>
    private const long CaseKeepMs = 5000;

    private static readonly AsyncLocal<CaseMemo?> Cases = new();

    private static Func<string, string?>? s_nameOnDisk;

    /// <summary>
    /// Answers a part's spelling by <paramref name="nameOnDisk"/> instead of listing its parent, which reads every entry beside it: the name a path's
    /// last part has on disk, a link's own for a link, or null when it cannot be read. The platform layer sets it; until then a part lists its parent.
    /// </summary>
    public static void UseNameOnDisk(Func<string, string?> nameOnDisk) => Volatile.Write(ref s_nameOnDisk, nameOnDisk);

    /// <summary>
    /// Opens a scope whose path reads, on this flow and on any thread or task it starts, keep spellings for <see cref="CaseKeepMs"/>. Each program opens
    /// one around its whole run; outside any scope every read lists the directory. Disposing restores the scope before it.
    /// </summary>
    public static IDisposable KeepCases() => ShareCases()();

    /// <summary>
    /// A new set of kept spellings, and how to enter it: each call of the result opens a <see cref="KeepCases"/> scope over that one set. For a server, whose
    /// requests do not run on the flow that started it, to give all of them one set.
    /// </summary>
    public static Func<IDisposable> ShareCases()
    {
        var memo = new CaseMemo();
        return () =>
        {
            var before = Cases.Value;
            Cases.Value = memo;
            return new Restore(before);
        };
    }

    /// <summary>
    /// The path with every symbolic link followed, <c>.</c> and <c>..</c> resolved against the already resolved parent, and each part in the
    /// case it has on disk where the volume ignores case, so every spelling of one directory gives one result. A part that does not exist
    /// is kept as written. A relative path is taken from the current directory.
    /// </summary>
    /// <param name="path">The path to resolve.</param>
    /// <param name="strict">
    /// Off: links that nest deeper than a fixed depth or lead back to themselves stop there, and the link being followed when they stop is kept as written. On: null comes back
    /// for those, and for a link that cannot be read, because then nothing says where the path leads.
    /// </param>
    public static string? RealPath(string path, bool strict) =>
        Resolve(Path.IsPathRooted(path) ? path : Path.Join(Directory.GetCurrentDirectory(), path), 0, strict);

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="ahHome"/> or lies under it once both are resolved strictly. A path that cannot be
    /// resolved is not within; this never throws. Neither needs to exist.
    /// </summary>
    public static bool Within(string ahHome, string path)
    {
        if (RealPath(ahHome, strict: true) is not { } home || RealPath(path, strict: true) is not { } real)
        {
            return false;
        }

        home = home.TrimEnd(Path.DirectorySeparatorChar);
        return real.Equals(home, StringComparison.Ordinal) || real.StartsWith(home + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? Resolve(string path, int depth, bool strict)
    {
        if (depth > MaxLinkDepth)
        {
            return strict ? null : path;
        }

        var current = Path.GetPathRoot(path) ?? string.Empty;
        foreach (var part in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                current = Path.GetDirectoryName(current) ?? current;
                continue;
            }

            current = Path.Join(current, OnDiskCase(current, part));
            try
            {
                if (new DirectoryInfo(current).LinkTarget is { } target)
                {
                    // one hop, as written: the target can pass through links and ".." of its own, so it is resolved again part by part
                    var next = Resolve(Path.IsPathRooted(target) ? target : Path.Join(Path.GetDirectoryName(current), target), depth + 1, strict);
                    if (next is null)
                    {
                        return null;
                    }

                    current = next;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (strict)
                {
                    return null;
                }
            }
        }

        return current.Length > 1 ? current.TrimEnd(Path.DirectorySeparatorChar) : current;
    }

    /// <summary>The name an entry of <paramref name="parent"/> has on disk when it differs from <paramref name="part"/> only in case, on a file system that ignores case; otherwise <paramref name="part"/>.</summary>
    private static string OnDiskCase(string parent, string part)
    {
        if (!(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows()) || parent.Length == 0)
        {
            return part;
        }

        var memo = Cases.Value;
        if (memo?.Seen(parent, part) is { } seen)
        {
            return seen;
        }

        if (Volatile.Read(ref s_nameOnDisk) is { } nameOnDisk)
        {
            var named = nameOnDisk(Path.Join(parent, part));
            var kept = named is not null && string.Equals(named, part, StringComparison.OrdinalIgnoreCase) ? named : part;
            if (named is not null)
            {
                memo?.Keep(parent, part, kept);
            }

            return kept;
        }

        try
        {
            // A volume that ignores case finds a part in any case, so one a stat cannot find has no spelling to list for. Paths named by old rows mostly no longer exist.
            if (!Path.Exists(Path.Join(parent, part)))
            {
                return part;
            }

            List<string> spellings = [];
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent, part))
            {
                var found = Path.GetFileName(entry);
                if (string.Equals(found, part, StringComparison.OrdinalIgnoreCase))
                {
                    spellings.Add(found);
                }
            }

            // two spellings side by side mean the volume tells them apart, and the one asked for is the one meant
            var spelling = spellings.Count == 1 && !spellings.Contains(part, StringComparer.Ordinal) ? spellings[0] : part;

            // A part not on disk is not kept: it may appear in another case.
            if (spellings.Count > 0)
            {
                memo?.Keep(parent, part, spelling);
            }

            return spelling;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // keep the part as written
        }

        return part;
    }

    /// <summary>The spellings one <see cref="KeepCases"/> scope has found, all dropped together once they are <see cref="CaseKeepMs"/> old.</summary>
    private sealed class CaseMemo
    {
        private readonly object _gate = new();
        private readonly Dictionary<(string Parent, string Part), string> _seen = [];
        private long _seenAt;

        internal string? Seen(string parent, string part)
        {
            lock (_gate)
            {
                var now = Environment.TickCount64;
                if (now - _seenAt > CaseKeepMs)
                {
                    _seen.Clear();
                    _seenAt = now;
                }

                return _seen.GetValueOrDefault((parent, part));
            }
        }

        internal void Keep(string parent, string part, string spelling)
        {
            lock (_gate)
            {
                _seen[(parent, part)] = spelling;
            }
        }
    }

    private sealed class Restore(CaseMemo? before) : IDisposable
    {
        public void Dispose() => Cases.Value = before;
    }
}
