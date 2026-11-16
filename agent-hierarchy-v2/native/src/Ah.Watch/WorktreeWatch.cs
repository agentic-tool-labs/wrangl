using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>
/// The <c>worktrees-piling-up</c> nudge (spec worktree-cleanup §5): counts each repo's linked work trees at most once per
/// <see cref="Every"/>, raises once per episode at <see cref="RaiseAt"/> and closes the episode quietly below <see cref="ClearBelow"/>.
/// The episode is kept in <see cref="IAlarmEpisodes"/>, so a daemon restart does not tell it again. It never removes or blocks anything.
/// </summary>
internal sealed class WorktreeWatch(IAlarmEpisodes episodes, Func<DateTimeOffset> now)
{
    public const string AlarmKind = "worktrees-piling-up";

    public const int RaiseAt = 30;

    public const int ClearBelow = 20;

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    private DateTimeOffset? _last;

    /// <summary>Whether a sample is due now; a due answer starts the next interval.</summary>
    public bool Due()
    {
        var at = now();
        if (_last is { } last && at - last < Every)
        {
            return false;
        }

        _last = at;
        return true;
    }

    /// <summary>The main work tree's path and the linked-entry count of <c>git worktree list --porcelain</c>; null when it lists nothing.</summary>
    public static (string Repo, int Linked)? Parse(string porcelain)
    {
        var trees = porcelain.Split('\n').Where(static l => l.StartsWith("worktree ", StringComparison.Ordinal)).ToList();
        return trees.Count == 0 ? null : (trees[0]["worktree ".Length..].TrimEnd('\r'), trees.Count - 1);
    }

    /// <summary>Records one count for <paramref name="repo"/>; returns the mail text when this count starts an episode, else null.</summary>
    public string? Sample(string repo, int linked)
    {
        var key = AlarmKind + "|" + repo;
        if (linked < ClearBelow)
        {
            episodes.Clear(key);
            return null;
        }

        if (episodes.Told(key) is { } told)
        {
            episodes.Tell(key, told);
            return null;
        }

        if (linked < RaiseAt)
        {
            return null;
        }

        episodes.Tell(key, Timestamps.ToIsoString(now()));
        return $"{repo}: {linked} work trees. Run Tidy C: branch-sweep.sh -C {repo} --target <integration branch>.";
    }
}
