using System.Text.RegularExpressions;

namespace Ah.Watch;

/// <summary>A release tag and its date.</summary>
internal sealed record ReleaseTag(string Tag, string Date);

/// <summary>Where one plan item's commits stand: its release (or why there is none), the local branches, and whether they are pushed.</summary>
/// <param name="ReleaseState">One of released, unreleased, no commits, git unavailable, reading git…</param>
/// <param name="Release">The oldest matching tag that contains every commit; null unless released.</param>
/// <param name="Branches">The local branches that contain the item's last commit, at most <see cref="PlanLinkage.MaxBranches"/>.</param>
/// <param name="Pushed">True when every commit is in a remote-tracking branch; null when the repo has no remote or a read is pending.</param>
internal sealed record ItemLink(string ReleaseState, ReleaseTag? Release, IReadOnlyList<string> Branches, bool? Pushed);

/// <summary>
/// Release linkage for plan items, computed from tags in the main checkout through the view's one <see cref="GitRunner"/>. It adds
/// no cache of its own: every read is the runner's, so it stays lazy, read only and bounded.
/// </summary>
internal sealed partial class PlanLinkage(GitRunner git, string repo, bool hasRemote)
{
    internal const int MaxLookups = 32;
    internal const int MaxBranches = 5;
    internal const string Released = "released";
    internal const string Unreleased = "unreleased";
    internal const string NoCommits = "no commits";

    private sealed record Reads(GitReading<TagsContaining> Tags, GitReading<IReadOnlyList<string>> Branches, GitReading<IReadOnlyList<string>> Remotes);

    /// <summary>The matching tags, newest first, or the reason there are none to show.</summary>
    internal (IReadOnlyList<ReleaseTag> Releases, string? Reason) Releases()
    {
        var tags = git.Tags(repo);
        return tags.Value is null
            ? ([], tags.Reason)
            : ([.. tags.Value.Select(static t => new ReleaseTag(t.Name, t.Date)).Reverse()], null);
    }

    /// <summary>
    /// One link per item, in order. At most <see cref="MaxLookups"/> commits that have no answer yet are looked up in one call; the rest
    /// read "reading git…" until a later call finds room.
    /// </summary>
    internal IReadOnlyList<ItemLink> Resolve(IReadOnlyList<IReadOnlyList<string>> items)
    {
        var tags = git.Tags(repo);
        var signature = tags.Value is null ? string.Empty : string.Join('\n', tags.Value.Select(static t => t.Name + "\t" + t.Date));
        var reads = new Dictionary<string, Reads>(StringComparer.Ordinal);
        var fresh = 0;
        foreach (var sha in items.SelectMany(static i => i).Distinct(StringComparer.Ordinal))
        {
            var known = tags.Value is not null && git.TagsIn(repo, sha, signature, start: false) is { } held && (held.Value is not null || held.Reason != GitRunner.ReadingReason);
            var start = tags.Value is not null && GitRunner.IsSha(sha) && (known || fresh < MaxLookups);
            if (start && !known)
            {
                fresh++;
            }

            reads[sha] = new Reads(
                git.TagsIn(repo, sha, signature, start),
                git.BranchesIn(repo, sha, start),
                git.RemoteBranchesIn(repo, sha, start));
        }

        return [.. items.Select(commits => Link(commits, tags, reads))];
    }

    private ItemLink Link(IReadOnlyList<string> commits, GitReading<IReadOnlyList<GitTag>> tags, Dictionary<string, Reads> reads)
    {
        if (commits.Count == 0)
        {
            return new ItemLink(NoCommits, null, [], null);
        }

        var all = commits.Select(c => reads[c]).ToList();
        var unavailable = tags.Reason == GitRunner.UnavailableReason || all.Any(static r => r.Tags.Reason is GitRunner.UnavailableReason or GitRunner.InvalidShaReason);
        var pending = tags.Value is null || all.Any(static r => r.Tags.Value is null);
        var branches = all[^1].Branches.Value?.Take(MaxBranches).ToList() ?? [];
        var pushed = Pushed(all);
        if (unavailable)
        {
            return new ItemLink(GitRunner.UnavailableReason, null, branches, pushed);
        }

        if (pending)
        {
            return new ItemLink(GitRunner.ReadingReason, null, branches, pushed);
        }

        var tag = tags.Value!.FirstOrDefault(t => all.All(r => r.Tags.Value!.Tags.Contains(t.Name, StringComparer.Ordinal)));
        return tag is null
            ? new ItemLink(Unreleased, null, branches, pushed)
            : new ItemLink(Released, new ReleaseTag(tag.Name, tag.Date), branches, pushed);
    }

    private bool? Pushed(List<Reads> all)
    {
        if (!hasRemote || all.Any(static r => r.Remotes.Value is null))
        {
            return null;
        }

        return all.All(static r => r.Remotes.Value!.Count > 0);
    }

    // ---------------------------------------------------------------- issue links, offline

    [GeneratedRegex(@"^\[remote ""origin""\]", RegexOptions.IgnoreCase)]
    private static partial Regex OriginSection();

    [GeneratedRegex(@"^\[remote """)]
    private static partial Regex AnyRemoteSection();

    [GeneratedRegex(@"^(?:https://(?:[^/@]+@)?github\.com/|ssh://git@github\.com/|git@github\.com:)(?<owner>[A-Za-z0-9_.-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?\z")]
    private static partial Regex GitHubUrl();

    /// <summary>Whether a git config text names any remote.</summary>
    internal static bool HasRemote(string gitConfig) => gitConfig.Split('\n').Any(static l => AnyRemoteSection().IsMatch(l.Trim()));

    /// <summary>The <c>url</c> of the <c>origin</c> remote in a git config text; null when there is none.</summary>
    internal static string? OriginUrl(string gitConfig)
    {
        var inOrigin = false;
        foreach (var raw in gitConfig.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inOrigin = OriginSection().IsMatch(line);
            }
            else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal) && line.Split('=', 2) is [var key, var value] && key.Trim() == "url")
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>The page of issue <paramref name="number"/> for a GitHub https or ssh origin URL; null for any other remote. Nothing is fetched.</summary>
    internal static string? IssueUrl(string? originUrl, int number) =>
        OwnerRepo(originUrl) is { } repo ? $"https://github.com/{repo}/issues/{number}" : null;

    /// <summary><c>owner/repo</c> of a GitHub https or ssh origin URL; null for any other remote.</summary>
    internal static string? OwnerRepo(string? originUrl) =>
        originUrl is not null && GitHubUrl().Match(originUrl.Trim()) is { Success: true } m ? $"{m.Groups["owner"].Value}/{m.Groups["repo"].Value}" : null;
}
