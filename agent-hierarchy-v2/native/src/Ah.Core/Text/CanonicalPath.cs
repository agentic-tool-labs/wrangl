using System.Text;
using Ah.Abstractions;

namespace Ah.Core.Text;

/// <summary>
/// Canonical real paths for a path that may not exist yet, and the name comparisons a write gate makes on them. The
/// kernel's own resolution is the model: a link is followed wherever it appears, a <c>..</c> after a link names the parent of
/// the link's target, and only what follows the last existing component is taken literally.
/// </summary>
public static class CanonicalPath
{
    /// <summary>The most links one path may pass through; a longer chain, or a loop, is unresolvable.</summary>
    public const int MaxLinks = 40;

    /// <summary>
    /// The real path of <paramref name="path"/> (absolute), or null when it cannot be resolved: a loop or an overlong chain
    /// of links, a link whose target cannot be read, or a <c>..</c> after a component that does not exist. A link that dangles
    /// is followed to where its target would be, so the result names the file a write through it would create.
    /// </summary>
    public static string? Resolve(IFileReader files, string path)
    {
        var pending = new Stack<string>(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse());
        var current = "/";
        var links = 0;
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (name == ".")
            {
                continue;
            }

            if (name == "..")
            {
                current = NodePath.Dirname(current);
                continue;
            }

            var next = current == "/" ? "/" + name : current + "/" + name;
            var stat = StatNoFollow(files, next);
            if (stat is null)
            {
                return Literal(current, name, pending);
            }

            if (stat.Value.Kind == FileKind.Symlink)
            {
                if (++links > MaxLinks || files.ReadLink(next) is not { Length: > 0 } target)
                {
                    return null;
                }

                if (target[0] == '/')
                {
                    current = "/";
                }

                foreach (var part in target.Split('/', StringSplitOptions.RemoveEmptyEntries).Reverse())
                {
                    pending.Push(part);
                }

                continue;
            }

            current = next;
            if (stat.Value.Kind != FileKind.Directory && pending.Count > 0)
            {
                return Literal(current, pending.Pop(), pending);
            }
        }

        return current;
    }

    /// <summary>Whether <paramref name="stored"/>, a path stored as canonical, still names the same real path.</summary>
    public static bool StillCanonical(IFileReader files, string stored)
    {
        try
        {
            return files.RealPath(stored) == stored;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether two names are the same under NFC normalisation, ignoring case (ordinal). <see cref="string.Normalize()"/> changes
    /// nothing in a build with <c>InvariantGlobalization</c> on, which the product build sets, so there a decomposed and a
    /// precomposed spelling of one non-ASCII name compare as different.
    /// </summary>
    public static bool SameName(string a, string b) =>
        string.Equals(a.Normalize(NormalizationForm.FormC), b.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether any segment of <paramref name="path"/> is <paramref name="name"/>, by <see cref="SameName"/>.</summary>
    public static bool HasSegment(string path, string name) =>
        path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(s => SameName(s, name));

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="root"/> or inside it, compared by whole segments with
    /// <see cref="SameName"/>: <c>/a/wt2</c> is not under <c>/a/wt</c>.
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        var p = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var r = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return p.Length >= r.Length && r.Select((segment, i) => SameName(p[i], segment)).All(same => same);
    }

    private static FileStat? StatNoFollow(IFileReader files, string path)
    {
        try
        {
            return files.Stat(path, followSymlinks: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Literal(string existing, string first, Stack<string> rest)
    {
        var parts = new List<string> { first };
        parts.AddRange(rest);
        if (parts.Contains(".."))
        {
            return null;
        }

        var tail = string.Join('/', parts.Where(p => p != "."));
        return tail.Length == 0 ? existing : (existing == "/" ? string.Empty : existing) + "/" + tail;
    }
}
