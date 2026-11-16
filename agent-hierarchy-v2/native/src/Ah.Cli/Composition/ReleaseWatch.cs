using Ah.Hierarchy;

namespace Ah.Cli.Composition;

/// <summary>
/// One tick of the <c>release-watch</c> job for one repository: reads where its release refs stand (offline, from the remote-tracking refs a push updates) and hands
/// them to the transition table. Nobody runs a command for a ship to be seen. What a tick last saw lives in <c>release-watch.seen</c> in the pool, so a ship made while the daemon was down is seen at start.
/// </summary>
internal static class ReleaseWatch
{
    private const string Tracking = "refs/remotes/origin/";

    /// <summary>The release refs and their commits: <c>ah.releaseRef</c> alone when it is set, else the remote's default branch and its <c>ah/release-*</c> branches.</summary>
    internal static Dictionary<string, string> Tips(Func<string[], string?> git)
    {
        var tips = new Dictionary<string, string>(StringComparer.Ordinal);
        if (git(["config", "--get", "ah.releaseRef"])?.Trim() is { Length: > 0 } configured)
        {
            if (git(["rev-parse", "--verify", "-q", configured + "^{commit}"])?.Trim() is { Length: > 0 } sha)
            {
                tips[Name(configured)] = sha;
            }

            return tips;
        }

        string[] patterns = git(["symbolic-ref", "-q", Tracking + "HEAD"])?.Trim() is { Length: > 0 } head ? [Tracking + "ah/release-*", head] : [Tracking + "ah/release-*"];
        foreach (var line in (git(["for-each-ref", "--format=%(refname) %(objectname)", .. patterns]) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Split(' ') is [var name, var sha])
            {
                tips[Name(name)] = sha;
            }
        }

        return tips;
    }

    /// <summary>The branch name a ship row names: the remote-side one, as the push hook writes it.</summary>
    private static string Name(string reference)
    {
        var name = reference.StartsWith(Tracking, StringComparison.Ordinal) ? reference[Tracking.Length..] : reference;
        return name.StartsWith("origin/", StringComparison.Ordinal) ? name["origin/".Length..] : name;
    }

    /// <summary>A reporter that logs one line on each change of a key's refusal text: a refusal retries every tick, and its log line should not. A success clears the key.</summary>
    internal static Action<string, string?> LogChanges(IDictionary<string, string> last, Action<string> log) => (key, problem) =>
    {
        if (problem is null)
        {
            _ = last.Remove(key);
        }
        else if (!last.TryGetValue(key, out var was) || was != problem)
        {
            last[key] = problem;
            log($"{key}: {problem}");
        }
    };

    internal static void Tick(IHierarchyProgress progress, string pool, IReadOnlyList<string> teams, Func<string[], string?> git, Func<string, string?> slugOfRequest, DateTimeOffset at, Action<string, string?> outcome, Action<string, string>? closeBacklog = null)
    {
        var tips = Tips(git);
        if (tips.Count == 0)
        {
            return;
        }

        // b435: the prior tips live in the state database; the pool's old release-watch.seen is read only while the database holds none; a database that cannot be read throws, so the tick is skipped (b439)
        var file = Path.Combine(pool, "release-watch.seen");
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        if ((progress.ReleaseSeen(pool) ?? (File.Exists(file) ? File.ReadAllText(file) : null)) is { } stored)
        {
            foreach (var line in stored.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                // "<team> <ref> <sha>": a team has its own prior tip, so one team's refusal does not hold another's back. An older "<ref> <sha>" line is dropped, and the ref takes a new baseline.
                if (line.Split(' ') is [var team, var name, var sha])
                {
                    seen[team + " " + name] = sha;
                }
            }
        }

        if (closeBacklog is not null)
        {
            CloseNamedRows(teams, tips, seen, git, closeBacklog);
        }

        if (HierarchyModule.ChecklistOnReleaseTips(progress, pool, teams, tips, seen, at, slugOfRequest, (rev, commit) => git(["merge-base", "--is-ancestor", rev, commit]) is not null, outcome, closeBacklog))
        {
            progress.SaveReleaseSeen(pool, string.Join('\n', seen.Select(static kv => kv.Key + " " + kv.Value)));
        }
    }

    /// <summary>Closes the backlog rows named by the commits a release ref moved over. A ref seen for the first time closes none: its history is not a ship.</summary>
    private static void CloseNamedRows(IReadOnlyList<string> teams, Dictionary<string, string> tips, Dictionary<string, string> seen, Func<string[], string?> git, Action<string, string> closeBacklog)
    {
        foreach (var (name, tip) in tips)
        {
            foreach (var prior in teams.Select(t => seen.GetValueOrDefault(t + " " + name)).OfType<string>().Where(p => p != tip).Distinct())
            {
                foreach (var line in (git(["log", "--no-merges", "--format=%H %s", prior + ".." + tip]) ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    var space = line.IndexOf(' ', StringComparison.Ordinal);
                    foreach (var row in RowsNamedBy(line[(space + 1)..]))
                    {
                        closeBacklog(row, line[..space]);
                    }
                }
            }
        }
    }

    /// <summary>The backlog ids in the parentheses that end a commit subject, as in <c>fix the thing (b96)</c> or <c>(b96, #376)</c>. An id anywhere else, or a range such as <c>b6-b13</c>, names nothing.</summary>
    internal static IEnumerable<string> RowsNamedBy(string subject)
    {
        var text = subject.TrimEnd();
        var open = text.LastIndexOf('(');
        if (open < 0 || !text.EndsWith(')'))
        {
            return [];
        }

        return text[(open + 1)..^1]
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(static t => t.Length > 1 && t[0] == 'b' && t.Skip(1).All(char.IsAsciiDigit));
    }
}
