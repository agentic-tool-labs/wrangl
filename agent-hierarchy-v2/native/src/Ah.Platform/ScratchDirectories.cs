using System.Globalization;
using Ah.Abstractions;

namespace Ah.Platform;

/// <summary>
/// The member tmp operations over the real file system. It holds no policy beyond containment: a tmp root is
/// <c>&lt;team home&gt;/tmp</c> or one directory name below it, checked by real path, and the alias root is always
/// <c>&lt;ah home&gt;/t</c>, where a member's tmp is a real directory (older versions left links there into the team home's tmp).
/// Deletion never follows a link, and checks each entry with lstat
/// before listing it; a racer who swaps a directory for a link between those two steps, inside <c>_dismissed/</c> and 72
/// hours after the dismissal, is a ceiling this helper accepts rather than a handle-based walk.
/// </summary>
internal sealed class ScratchDirectories(IFileReader files, string ahHome) : IScratchDirectories
{
    internal const string DismissedName = "_dismissed";

    private const string StampFormat = "yyyyMMdd'T'HHmmss'Z'";

    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private string AliasRoot => Path.Combine(ahHome, "t");

    public string MakeMemberDirectory(string aliasRoot, IReadOnlyList<string> keys)
    {
        CheckAliasRoot(aliasRoot);
        Directory.CreateDirectory(aliasRoot, Private);
        foreach (var key in keys)
        {
            var path = Path.Combine(aliasRoot, CheckedName(key));
            switch (files.Stat(path, followSymlinks: false))
            {
                case null:
                    Directory.CreateDirectory(path, Private);
                    return path;
                case { Kind: FileKind.Directory }:
                    return path;
                case { Kind: FileKind.Symlink }:
                    // spec member-tmpdir T5: an old alias in the way loses its link, never its target
                    File.Delete(path);
                    Directory.CreateDirectory(path, Private);
                    return path;
            }
        }

        throw new InvalidOperationException($"every member tmp name under {aliasRoot} is taken by something that is not a directory");
    }

    public bool RemoveMemberDirectory(string aliasRoot, string path)
    {
        CheckAliasRoot(aliasRoot);
        if (!DirectlyUnderAliasRoot(path) || files.Stat(path, followSymlinks: false) is not { Kind: FileKind.Directory })
        {
            return false;
        }

        DeleteTree(path);
        return true;
    }

    public ScratchSweepReport SweepMemberDirectories(IReadOnlyCollection<string> paths, DateTimeOffset now, TimeSpan olderThan, bool commit)
    {
        var found = paths.Distinct(StringComparer.Ordinal)
            .Where(p => DirectlyUnderAliasRoot(p) && files.Stat(p, followSymlinks: false) is { Kind: FileKind.Directory } && now - NewestModification(p) > olderThan)
            .Select(p => new ScratchSweepEntry(p, Size(p)))
            .ToList();
        if (commit)
        {
            foreach (var entry in found)
            {
                DeleteTree(entry.Path);
            }
        }

        return new ScratchSweepReport(found, commit);
    }

    private bool DirectlyUnderAliasRoot(string path) =>
        Path.GetDirectoryName(path.TrimEnd('/')) == AliasRoot.TrimEnd('/') && Path.GetFileName(path.TrimEnd('/')) is { Length: > 0 } name && name is not ("." or "..");

    public string? AliasTarget(string link) =>
        files.Stat(link, followSymlinks: false) is { Kind: FileKind.Symlink } ? files.ReadLink(link) : null;

    public bool RemoveAlias(string aliasRoot, string link)
    {
        CheckAliasRoot(aliasRoot);
        if (Path.GetDirectoryName(link) != aliasRoot.TrimEnd('/') || files.Stat(link, followSymlinks: false) is not { Kind: FileKind.Symlink })
        {
            return false;
        }

        File.Delete(link);
        return true;
    }

    public string? Dismiss(string teamHome, string tmpRoot, string member, DateTimeOffset at)
    {
        var real = TmpRootReal(teamHome, tmpRoot, create: false);
        if (real is null)
        {
            return null;
        }

        var source = Path.Combine(real, CheckedName(member));
        switch (files.Stat(source, followSymlinks: false))
        {
            case null:
                return null;
            case { Kind: FileKind.Directory }:
                break;
            default:
                throw new InvalidOperationException($"{source} is not a directory; it was not dismissed");
        }

        // spec worktree-cleanup §5a: a verified close reaps the folder rather than parking it in _dismissed/ for 72 h
        DeleteTree(source);
        return source;
    }

    public long Size(string path)
    {
        long total = 0;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var at = pending.Pop();
            switch (files.Stat(at, followSymlinks: false))
            {
                case { Kind: FileKind.File } file:
                    total += file.Size;
                    break;
                case { Kind: FileKind.Directory }:
                    foreach (var name in files.List(at))
                    {
                        pending.Push(Path.Combine(at, name));
                    }

                    break;
            }
        }

        return total;
    }

    public IReadOnlyList<DismissedScratch> Dismissed(string teamHome, string tmpRoot)
    {
        var real = TmpRootReal(teamHome, tmpRoot, create: false);
        if (real is null)
        {
            return [];
        }

        var dismissed = Path.Combine(real, DismissedName);
        if (files.Stat(dismissed, followSymlinks: false) is not { Kind: FileKind.Directory })
        {
            return [];
        }

        var found = new List<DismissedScratch>();
        foreach (var name in files.List(dismissed))
        {
            var path = Path.Combine(dismissed, name);
            if (files.Stat(path, followSymlinks: false) is not { } stat)
            {
                continue;
            }

            var dash = name.LastIndexOf('-');
            var member = dash > 0 ? name[..dash] : name;
            var at = dash > 0 && DateTimeOffset.TryParseExact(name[(dash + 1)..], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : DateTimeOffset.FromUnixTimeMilliseconds((long)stat.MtimeMs);
            found.Add(new DismissedScratch(path, member, at, Size(path)));
        }

        return [.. found.OrderBy(d => d.At)];
    }

    public int SweepDismissed(string teamHome, string tmpRoot, DateTimeOffset now, TimeSpan olderThan)
    {
        var removed = 0;
        foreach (var entry in Dismissed(teamHome, tmpRoot).Where(d => now - d.At > olderThan))
        {
            DeleteTree(entry.Path);
            removed++;
        }

        return removed;
    }

    public ScratchSweepReport Sweep(string teamHome, DateTimeOffset now, TimeSpan olderThan, Func<string, bool> isMemberName, Func<string?, string, ScratchOwner> ownerOf, bool commit)
    {
        var top = Path.Combine(teamHome, "tmp");
        var found = new List<ScratchSweepEntry>();
        if (TmpRootReal(teamHome, top, create: false) is null)
        {
            return new ScratchSweepReport(found, false);
        }

        bool Idle(string path) => now - NewestModification(path) > olderThan;

        void TakeMember(string? team, string path, string member)
        {
            if (ownerOf(team, member) != ScratchOwner.Live && Idle(path))
            {
                found.Add(new ScratchSweepEntry(path, Size(path)));
            }
        }

        var roots = new List<(string Root, string? Team)> { (top, null) };
        foreach (var name in files.List(top).Where(n => n != DismissedName))
        {
            var path = Path.Combine(top, name);
            if (files.Stat(path, followSymlinks: false) is not { Kind: FileKind.Directory })
            {
                continue;
            }

            if (isMemberName(name))
            {
                TakeMember(null, path, name);
            }
            else
            {
                roots.Add((path, name));
            }
        }

        foreach (var (root, team) in roots)
        {
            foreach (var entry in Dismissed(teamHome, root).Where(d => now - d.At > olderThan && Idle(d.Path)))
            {
                found.Add(new ScratchSweepEntry(entry.Path, entry.Size));
            }

            if (team is null)
            {
                continue;
            }

            foreach (var name in files.List(root).Where(n => n != DismissedName))
            {
                var path = Path.Combine(root, name);
                if (files.Stat(path, followSymlinks: false) is { Kind: FileKind.Directory })
                {
                    TakeMember(team, path, name);
                }
            }
        }

        if (commit)
        {
            foreach (var entry in found)
            {
                DeleteTree(entry.Path);
            }
        }

        return new ScratchSweepReport(found, commit);
    }

    /// <summary>The newest modification time of <paramref name="path"/> or anything below it; links are not followed.</summary>
    private DateTimeOffset NewestModification(string path)
    {
        var newest = DateTimeOffset.MinValue;
        var pending = new Stack<string>();
        pending.Push(path);
        while (pending.Count > 0)
        {
            var at = pending.Pop();
            if (files.Stat(at, followSymlinks: false) is not { } stat)
            {
                continue;
            }

            var modified = DateTimeOffset.FromUnixTimeMilliseconds((long)stat.MtimeMs);
            newest = modified > newest ? modified : newest;
            if (stat.Kind == FileKind.Directory)
            {
                foreach (var name in files.List(at))
                {
                    pending.Push(Path.Combine(at, name));
                }
            }
        }

        return newest;
    }

    private void DeleteTree(string path)
    {
        switch (files.Stat(path, followSymlinks: false))
        {
            case null:
                return;
            case { Kind: FileKind.Directory }:
                foreach (var name in files.List(path))
                {
                    DeleteTree(Path.Combine(path, name));
                }

                Directory.Delete(path, recursive: false);
                return;
            default:
                File.Delete(path);
                return;
        }
    }

    private string? TmpRootReal(string teamHome, string tmpRoot, bool create)
    {
        var top = Path.Combine(teamHome, "tmp");
        var given = tmpRoot.TrimEnd('/');
        string? child = null;
        if (given != top)
        {
            if (Path.GetDirectoryName(given) != top)
            {
                throw new InvalidOperationException($"{tmpRoot} is not under {top}");
            }

            child = CheckedName(Path.GetFileName(given));
        }

        var topReal = EnsureDirectory(top, create);
        if (topReal is null || child is null)
        {
            return topReal;
        }

        var real = EnsureDirectory(Path.Combine(top, child), create);
        if (real is not null && real != Path.Combine(topReal, child))
        {
            throw new InvalidOperationException($"{tmpRoot} resolves to {real}, outside {topReal}");
        }

        return real;
    }

    private string? EnsureDirectory(string path, bool create)
    {
        switch (files.Stat(path, followSymlinks: false))
        {
            case null when !create:
                return null;
            case null:
                Directory.CreateDirectory(path, Private);
                break;
            case { Kind: FileKind.Directory }:
                break;
            default:
                throw new InvalidOperationException($"{path} is not a directory");
        }

        return files.RealPath(path);
    }

    private void CheckAliasRoot(string aliasRoot)
    {
        if (aliasRoot.TrimEnd('/') != AliasRoot.TrimEnd('/'))
        {
            throw new InvalidOperationException($"{aliasRoot} is not the alias root");
        }
    }

    private static string CheckedName(string name)
    {
        if (name.Length == 0 || name is "." or ".." or DismissedName || name.Contains('/', StringComparison.Ordinal) || name.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"{name} is not a usable directory name");
        }

        return name;
    }
}
