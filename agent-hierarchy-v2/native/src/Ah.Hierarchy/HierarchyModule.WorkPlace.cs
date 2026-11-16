using Ah.Core.Text;

namespace Ah.Hierarchy;

/// <summary>Where a member works: the one rule every reader of a member's work folder shares.</summary>
public static partial class HierarchyModule
{
    /// <summary>The folder assigned to the member in its team row, else the checkout its session registered from.</summary>
    public static string? SelectWorkRoot(string? assigned, string? checkout) => string.IsNullOrEmpty(assigned) ? checkout : assigned;

    /// <summary>The nearest directory at or above <paramref name="cwd"/> holding a <c>.git</c>; null when there is none.</summary>
    public static string? CheckoutOfCwd(string? cwd, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        for (var d = cwd; !string.IsNullOrEmpty(d);)
        {
            if (exists(NodePath.Join(d, ".git")))
            {
                return d;
            }

            var parent = NodePath.Dirname(d);
            d = parent == d ? null : parent;
        }

        return null;
    }

    /// <summary>
    /// The short branch checked out in <paramref name="root"/>, read from its HEAD file with no process: <c>.git</c> is a directory
    /// holding HEAD, or a file whose <c>gitdir:</c> line names the directory that does. Null when HEAD is detached or unreadable.
    /// </summary>
    public static string? BranchOfWorkTree(string? root, Func<string, string?> readText)
    {
        ArgumentNullException.ThrowIfNull(readText);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        var git = NodePath.Join(root, ".git");
        var gitDir = git;
        if (readText(git) is { } pointer)
        {
            var line = pointer.Split('\n')[0].Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                return null;
            }

            var target = line["gitdir:".Length..].Trim();
            gitDir = NodePath.IsAbsolute(target) ? target : NodePath.Join(root, target);
        }

        const string Prefix = "ref: refs/heads/";
        var head = readText(NodePath.Join(gitDir, "HEAD"))?.Split('\n')[0].Trim();
        return head is not null && head.StartsWith(Prefix, StringComparison.Ordinal) && head.Length > Prefix.Length ? head[Prefix.Length..] : null;
    }

    /// <summary>
    /// The tip of <c>ah/&lt;slug&gt;</c> when that branch exists, else the commit at HEAD, read from the ref files of the checkout at <paramref name="root"/> with no process.
    /// Null when the files do not settle it (no loose or packed ref for HEAD, a symbolic branch, a slug that is not a plain ref name): the caller asks git.
    /// </summary>
    public static string? DispatchBaseOfWorkTree(string root, string? slug, Func<string, string?> readText)
    {
        ArgumentNullException.ThrowIfNull(readText);
        var git = NodePath.Join(root, ".git");
        var gitDir = git;
        if (readText(git) is { } pointer)
        {
            var line = pointer.Split('\n')[0].Trim();
            if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                return null;
            }

            var target = line["gitdir:".Length..].Trim();
            gitDir = NodePath.IsAbsolute(target) ? target : NodePath.Join(root, target);
        }

        var common = gitDir;
        if (readText(NodePath.Join(gitDir, "commondir"))?.Trim() is { Length: > 0 } shared)
        {
            common = NodePath.IsAbsolute(shared) ? shared : NodePath.Join(gitDir, shared);
        }

        if (readText(NodePath.Join(gitDir, "HEAD"))?.Split('\n')[0].Trim() is not { } head)
        {
            return null;
        }

        var packed = readText(NodePath.Join(common, "packed-refs"));
        var headSha = head.StartsWith("ref: ", StringComparison.Ordinal) ? RefCommit(common, head["ref: ".Length..].Trim(), packed, readText, out _) : IsCommitId(head) ? head : null;
        if (headSha is null)
        {
            return null;
        }

        if (string.IsNullOrEmpty(slug))
        {
            return headSha;
        }

        if (slug.Contains("..", StringComparison.Ordinal) || slug.Any(static c => char.IsWhiteSpace(c) || c is '\\' or '~' or '^' or ':' or '?' or '*' or '[' or '@'))
        {
            return null;
        }

        var branch = RefCommit(common, "refs/heads/ah/" + slug, packed, readText, out var unsettled);
        return unsettled ? null : branch ?? headSha;
    }

    private static string? RefCommit(string common, string reference, string? packed, Func<string, string?> readText, out bool unsettled)
    {
        unsettled = false;
        if (readText(NodePath.Join(common, reference))?.Trim() is { } loose)
        {
            unsettled = !IsCommitId(loose);
            return unsettled ? null : loose;
        }

        foreach (var line in (packed ?? string.Empty).Split('\n'))
        {
            var row = line.TrimEnd('\r');
            var space = row.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && row[(space + 1)..] == reference && IsCommitId(row[..space]))
            {
                return row[..space];
            }
        }

        return null;
    }

    private static bool IsCommitId(string text) => text.Length is 40 or 64 && text.All(static c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
