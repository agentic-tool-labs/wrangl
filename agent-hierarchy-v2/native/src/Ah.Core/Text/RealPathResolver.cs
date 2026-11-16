using Ah.Abstractions;

namespace Ah.Core.Text;

/// <summary>Real-path resolution for a path that may not exist yet.</summary>
public static class RealPathResolver
{
    /// <summary>
    /// Whether <paramref name="realPath"/> (already resolved) is a test sandbox: it, or something under it, is a directory
    /// named <c>ahprobe.&lt;alphanumerics&gt;</c> directly inside <c>/tmp</c> or <c>/private/tmp</c>. A path that could not be
    /// resolved (null) is not.
    /// </summary>
    public static bool IsUnderProbeSandbox(string? realPath) => ProbeRootOf(realPath) is not null;

    /// <summary>The <c>ahprobe.&lt;alphanumerics&gt;</c> directory <paramref name="realPath"/> (already resolved) is or lies under; null when there is none.</summary>
    public static string? ProbeRootOf(string? realPath)
    {
        if (string.IsNullOrEmpty(realPath))
        {
            return null;
        }

        foreach (var root in new[] { "/tmp/ahprobe.", "/private/tmp/ahprobe." })
        {
            if (!realPath.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = realPath[root.Length..];
            var end = rest.IndexOf('/', StringComparison.Ordinal);
            var name = end < 0 ? rest : rest[..end];
            if (name.Length > 0 && name.All(char.IsAsciiLetterOrDigit))
            {
                return root + name;
            }
        }

        return null;
    }

    /// <summary>
    /// The real path of the longest existing prefix of <paramref name="path"/> with the remaining components taken literally; null
    /// when a remaining component is empty or <c>..</c>, or when a prefix exists but cannot be resolved (a dangling link or a loop).
    /// </summary>
    public static string? ThroughLongestExistingPrefix(IFileReader files, string path)
    {
        var parts = path.Split('/').Skip(1).ToList();
        for (var k = parts.Count; k >= 0; k--)
        {
            var prefix = "/" + string.Join('/', parts.Take(k));
            if (!Exists(files, prefix))
            {
                continue;
            }

            string real;
            try
            {
                real = files.RealPath(prefix);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return null;
            }

            var rest = parts.Skip(k).ToList();
            if (rest.Any(p => p.Length == 0 || p == ".."))
            {
                return null;
            }

            return rest.Count == 0 ? real : (real == "/" ? string.Empty : real) + "/" + string.Join('/', rest);
        }

        return null;
    }

    /// <summary>Whether <paramref name="path"/> exists as the operating system sees it, a link counted even when it dangles; an unreadable component counts as existing.</summary>
    private static bool Exists(IFileReader files, string path)
    {
        // a trailing separator names the same entry as the path without it (the empty component is the caller's to refuse)
        var probe = path.Length > 1 ? path.TrimEnd('/') : path;
        try
        {
            return files.Stat(probe.Length == 0 ? "/" : probe, followSymlinks: false) is not null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return true;
        }
    }
}
