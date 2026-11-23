using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>Reads a team's session budget from the files: the one reader the watch tick and the checklist verb share.</summary>
internal static class SessionBudgets
{
    /// <summary>
    /// The budget of <paramref name="team"/> (null for the default team) of the hierarchy dir <paramref name="home"/>: its own open run anchor's level,
    /// else the nearest ancestor team's, else the default. A null result means no limit. An anchor or team file that cannot be read gives the default.
    /// </summary>
    internal static int? Of(ServiceGraph graph, string home, string? team, Action<string>? log = null)
    {
        try
        {
            return WatchModule.SessionBudgetOf(team, AnchorOf(graph, home), at => ParentOf(graph, home, at));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"session budget of {team ?? "_default"} not read ({e.Message})");
            return WatchModule.DefaultSessionBudget;
        }
    }

    /// <summary>
    /// ah2-514 LB-1/LB-2: the live member rows of the team tree whose budget bounds <paramref name="team"/>: the budget-owning team and every team
    /// below it by <c>parent</c> links. A team in <paramref name="ownedLive"/> (keyed by name, <c>""</c> for the default team) counts what its owner's
    /// session already sees; any other team's rows are counted from the team files by <see cref="ServiceGraph.MemberUp"/>, the path
    /// <c>ah roster teams</c> counts by. Orchestrator records are never counted, so a Sub-Orchestrator counts once, as its row in its parent team.
    /// A team file that exists and cannot be read counts as zero and is named in <c>Unreadable</c>. Null when the tree cannot be walked.
    /// </summary>
    internal static (int Live, IReadOnlyList<string> Unreadable)? TreeLive(
        ServiceGraph graph, string home, string? team, IReadOnlyDictionary<string, int> ownedLive, Action<string>? log = null)
    {
        try
        {
            // LB-2: a team whose parent's file is gone or closed is its own root
            var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
            string? Parent(string? at) =>
                at is null ? null
                : parents.TryGetValue(at, out var known) ? known
                : parents[at] = ParentOf(graph, home, at) is { } p && graph.Roster.ReadTeamRecord(home, p) is { IsMap: true } parentRecord
                    && parentRecord.Member("closed") is not { IsMap: true } ? p : null;
            var (_, owner) = WatchModule.SessionBudgetWithOwner(team, AnchorOf(graph, home), Parent);
            var names = graph.Roster.ListTeamNames(home);

            bool Below(string name)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal) { name };
                for (var at = Parent(name); at is not null && seen.Add(at); at = Parent(at))
                {
                    if (at == owner)
                    {
                        return true;
                    }
                }

                return false;
            }

            List<string?> tree = [owner, .. owner is null ? [] : names.Where(n => n != owner && Below(n))];
            var live = 0;
            var unreadable = new List<string>();
            foreach (var at in tree)
            {
                if (ownedLive.TryGetValue(at ?? string.Empty, out var seen))
                {
                    live += seen;
                    continue;
                }

                try
                {
                    if (graph.Roster.ReadTeamRecord(home, at) is not { IsMap: true } record)
                    {
                        unreadable.Add(at ?? "_default");
                        continue;
                    }

                    live += record.Member("members") is { IsArray: true } rows
                        ? rows.AsArray().Count(m => m.IsMap && m.Member("name") is { IsText: true } && graph.MemberUp(home, m))
                        : 0;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    unreadable.Add(at ?? "_default");
                }
            }

            return (live, unreadable);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"session tree of {team ?? "_default"} not counted ({e.Message})");
            return null;
        }
    }

    private static Func<string?, string?> AnchorOf(ServiceGraph graph, string home) =>
        at =>
        {
            var anchor = graph.Hierarchy.Decisions.OpenRunAnchor(home, at is null ? JsonValue.Null : JsonValue.FromString(at));
            return anchor.Get("path") is { IsText: true } path ? graph.Io.Files.ReadText(path.AsString()) : null;
        };

    private static string? ParentOf(ServiceGraph graph, string home, string? at) =>
        graph.Roster.ReadTeamRecord(home, at) is { IsMap: true } record && record.Member("parent")?.Member("team") is { IsText: true } parent && parent.AsString().Length > 0
            ? parent.AsString()
            : null;
}
