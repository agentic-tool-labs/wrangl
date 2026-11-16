using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Config;

/// <summary>
/// The path rules the grants share between what <c>allow</c> refuses to name and what the write gate refuses to let a session
/// write: one implementation of the repository family, the temp roots and the protected set.
/// </summary>
internal static class GrantPaths
{
    /// <summary>The repository's main checkout and its linked worktrees, read from the git directory without a git call.</summary>
    internal static List<string> FamilyRoots(IFileReader files, string hierarchyDir, Func<string, string> canonical)
    {
        var roots = new List<string>();
        for (var at = hierarchyDir; at != "/" && at.Length > 0; at = NodePath.Dirname(at))
        {
            if (files.Stat(NodePath.Join(at, ".git"), followSymlinks: false) is not { } git)
            {
                continue;
            }

            roots.Add(canonical(at));
            if (git.Kind == FileKind.Directory)
            {
                foreach (var name in SafeList(files, NodePath.Join(at, ".git", "worktrees")))
                {
                    try
                    {
                        var gitdir = files.ReadText(NodePath.Join(at, ".git", "worktrees", name, "gitdir")).Trim();
                        if (NodePath.IsAbsolute(gitdir))
                        {
                            roots.Add(canonical(NodePath.Dirname(gitdir)));
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // a pruned worktree has no gitdir file
                    }
                }
            }

            break;
        }

        return roots;
    }

    /// <summary>The OS temp roots: <c>/tmp</c>, <c>/private/tmp</c> and the environment's <c>TMPDIR</c> when it is absolute.</summary>
    internal static List<string> TempRoots(IAhEnvironment environment, Func<string, string> canonical)
    {
        var roots = new List<string> { canonical("/tmp"), canonical("/private/tmp") };
        if (environment.GetVariable("TMPDIR") is { Length: > 0 } tmp && NodePath.IsAbsolute(tmp))
        {
            roots.Add(canonical(tmp));
        }

        return roots;
    }

    /// <summary>
    /// Control state: a <c>.git</c> entry anywhere, the user's ah home, plugin cache, settings and <c>.claude.json</c>, and inside every
    /// family root its <c>.claude/ah2</c>, its <c>.claude/settings*.json</c> and its <c>.mcp.json</c>. Compared ignoring case.
    /// </summary>
    internal static bool IsProtected(string path, string home, IReadOnlyList<string> family)
    {
        bool Under(string root, params string[] parts) => CanonicalPath.IsUnder(path, NodePath.Join(root, string.Join('/', parts)));

        bool Settings(string dir) =>
            CanonicalPath.SameName(NodePath.Dirname(path), dir)
            && NodePath.Basename(path) is var name
            && name.Length >= "settings.json".Length
            && CanonicalPath.SameName(name[..8], "settings")
            && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        if (CanonicalPath.HasSegment(path, ".git"))
        {
            return true;
        }

        if (Under(home, ".claude", "ah2") || Under(home, ".claude", "plugins") || Settings(NodePath.Join(home, ".claude"))
            || CanonicalPath.SameName(path, NodePath.Join(home, ".claude.json")))
        {
            return true;
        }

        return family.Any(r =>
            Under(r, ".claude", "ah2") || Settings(NodePath.Join(r, ".claude")) || CanonicalPath.SameName(path, NodePath.Join(r, ".mcp.json")));
    }

    /// <summary>Why no grant may name <paramref name="path"/> (canonical): the root, the home directory or an ancestor, <c>~/.claude</c>, or the protected set; null when it may be named.</summary>
    internal static string? Forbidden(string path, string home, IReadOnlyList<string> family)
    {
        var claude = NodePath.Join(home, ".claude");
        if (path == "/" || CanonicalPath.IsUnder(home, path) || CanonicalPath.IsUnder(claude, path))
        {
            return $"{path} is the root, the home directory or one of its ancestors, or ~/.claude: it cannot be granted";
        }

        return IsProtected(path, home, family)
            ? $"{path} is in the protected set (control files, settings, .git, the ah state): it cannot be granted"
            : null;
    }

    private static IEnumerable<string> SafeList(IFileReader files, string dir)
    {
        try
        {
            return files.List(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
