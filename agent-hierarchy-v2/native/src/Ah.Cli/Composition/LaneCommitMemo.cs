using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Cli.Composition;

/// <summary>
/// The latest commit time of each lane work tree (b459 step 19, Amendment 1): held while a stat (mtime and size) of the tree's
/// <c>&lt;gitdir&gt;/HEAD</c> and <c>&lt;gitdir&gt;/logs/HEAD</c> is unchanged, so an unchanged tree runs no git. A commit, checkout, reset or rebase
/// moves the reflog and the next read runs git again. A tree with no reflog file is never held. <see cref="Forget"/> is the on-demand rebuild.
/// </summary>
internal sealed class LaneCommitMemo(IFileReader files)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Stamp, double? Ms)> _held = new(StringComparer.Ordinal);

    internal double? Of(string tree, Func<double?> read)
    {
        // the stamp is taken before the read: a commit between the two leaves the older stamp held, so the next read runs git again
        if (StampOf(tree) is not { } stamp)
        {
            return read();
        }

        lock (_gate)
        {
            if (_held.TryGetValue(tree, out var held) && held.Stamp == stamp)
            {
                return held.Ms;
            }
        }

        var ms = read();
        lock (_gate)
        {
            _held[tree] = (stamp, ms);
        }

        return ms;
    }

    internal void Forget()
    {
        lock (_gate)
        {
            _held.Clear();
        }
    }

    /// <summary>The stat of the tree's HEAD and reflog, or null when either is missing: <c>.git</c> is the git dir, or a file whose <c>gitdir:</c> line names it.</summary>
    private string? StampOf(string tree)
    {
        try
        {
            var git = NodePath.Join(tree, ".git");
            if (files.Stat(git) is not { } gitStat)
            {
                return null;
            }

            var gitDir = git;
            if (gitStat.Kind == FileKind.File)
            {
                var line = files.ReadText(git).Split('\n')[0].Trim();
                if (!line.StartsWith("gitdir:", StringComparison.Ordinal))
                {
                    return null;
                }

                var target = line["gitdir:".Length..].Trim();
                gitDir = NodePath.IsAbsolute(target) ? target : NodePath.Join(tree, target);
            }

            return files.Stat(NodePath.Join(gitDir, "HEAD")) is { } head && files.Stat(NodePath.Join(gitDir, "logs", "HEAD")) is { } log
                ? $"{head.MtimeMs}:{head.MtimeNs}:{head.Size}:{log.MtimeMs}:{log.MtimeNs}:{log.Size}"
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
