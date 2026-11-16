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
            return WatchModule.SessionBudgetOf(
                team,
                at =>
                {
                    var anchor = graph.Hierarchy.Decisions.OpenRunAnchor(home, at is null ? JsonValue.Null : JsonValue.FromString(at));
                    return anchor.Get("path") is { IsText: true } path ? graph.Io.Files.ReadText(path.AsString()) : null;
                },
                at => graph.Roster.ReadTeamRecord(home, at) is { IsMap: true } record && record.Member("parent")?.Member("team") is { IsText: true } parent && parent.AsString().Length > 0
                    ? parent.AsString()
                    : null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.Invoke($"session budget of {team ?? "_default"} not read ({e.Message})");
            return WatchModule.DefaultSessionBudget;
        }
    }
}
