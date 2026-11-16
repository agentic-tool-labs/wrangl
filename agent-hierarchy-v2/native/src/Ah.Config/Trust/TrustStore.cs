using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>
/// Reads <c>trusted-homes</c> and <c>trusted-hierarchy-dirs</c> from the default home. A file is read only when it is a regular
/// file reached without following a link, owned by the invoking user, not group- or other-writable, in a default home that is
/// itself owned by that user and not group- or other-writable, and at most 64 KiB; an entry counts only when its
/// <c>realpath</c> is a directory owned by that user and not group- or other-writable. Any failure is "untrusted".
/// </summary>
internal sealed class TrustStore(IFileReader files, Func<string> defaultHome, Func<uint?> currentUserId) : ITrustStore
{
    private const long MaxBytes = 64 * 1024;

    private const uint GroupOrOtherWritable = 0x12;

    private static string NameOf(TrustFile file) => ConfigModule.TrustFileNames[file == TrustFile.Homes ? 0 : 1];

    public TrustFileReport Read(TrustFile file)
    {
        var home = defaultHome();
        var path = NodePath.Join(home, NameOf(file));
        var uid = currentUserId();
        FileStat? stat;
        try
        {
            stat = files.Stat(path, followSymlinks: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Ignored(file, path, "cannot be examined");
        }

        if (stat is null)
        {
            return new TrustFileReport(file, path, false, null, []);
        }

        if (uid is not { } me)
        {
            return Ignored(file, path, "the invoking user is unknown");
        }

        if (stat.Value.Kind == FileKind.Symlink)
        {
            return Ignored(file, path, "is a symbolic link");
        }

        if (stat.Value.Kind != FileKind.File)
        {
            return Ignored(file, path, "is not a regular file");
        }

        if (stat.Value.Uid != me)
        {
            return Ignored(file, path, "is not owned by the invoking user");
        }

        if ((stat.Value.Mode & GroupOrOtherWritable) != 0)
        {
            return Ignored(file, path, "is group- or other-writable");
        }

        if (!OwnerOnly(home, me, followSymlinks: true))
        {
            return Ignored(file, path, "its directory is not owned by the invoking user or is group- or other-writable");
        }

        if (stat.Value.Size > MaxBytes)
        {
            return Ignored(file, path, "is larger than 64 KiB");
        }

        string text;
        try
        {
            text = Utf8Text.Decode(files.ReadBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Ignored(file, path, "cannot be read");
        }

        var entries = new List<TrustEntry>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim(' ', '\t', '\r');
            if (line.Length == 0)
            {
                continue;
            }

            entries.Add(line.StartsWith('/') ? Judge(line, me) : new TrustEntry(line, null, false, "is not an absolute path"));
        }

        return new TrustFileReport(file, path, true, null, entries);
    }

    public bool IsTrusted(TrustFile file, string path)
    {
        var report = Read(file);
        if (report.IgnoredReason is not null || report.Entries.Count == 0)
        {
            return false;
        }

        string real;
        try
        {
            real = files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return report.Entries.Any(e => e.Trusted && e.RealPath == real);
    }

    private TrustEntry Judge(string line, uint me)
    {
        string real;
        try
        {
            real = files.RealPath(line);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new TrustEntry(line, null, false, "does not exist");
        }

        var stat = files.Stat(real);
        if (stat is null || stat.Value.Kind != FileKind.Directory)
        {
            return new TrustEntry(line, real, false, "is not a directory");
        }

        if (stat.Value.Uid != me)
        {
            return new TrustEntry(line, real, false, "is not owned by the invoking user");
        }

        return (stat.Value.Mode & GroupOrOtherWritable) != 0
            ? new TrustEntry(line, real, false, "is group- or other-writable")
            : new TrustEntry(line, real, true, null);
    }

    private bool OwnerOnly(string directory, uint me, bool followSymlinks)
    {
        var stat = files.Stat(directory, followSymlinks);
        return stat is { Kind: FileKind.Directory } s && s.Uid == me && (s.Mode & GroupOrOtherWritable) == 0;
    }

    private static TrustFileReport Ignored(TrustFile file, string path, string reason) => new(file, path, true, reason, []);
}
