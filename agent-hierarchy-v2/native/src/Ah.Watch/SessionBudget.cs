using System.Text.RegularExpressions;

namespace Ah.Watch;

/// <summary>
/// The most live members a run's team may hold, read from the <c>session-budget:</c> line of the run anchor. The level word decides and the number
/// written beside it is ignored; no line, or a word that is not a level, means the medium budget.
/// </summary>
internal static partial class SessionBudget
{
    /// <summary>The budget a missing or unknown level gets.</summary>
    public const int Default = 12;

    /// <summary>The level words and their budgets; <c>max</c> has none (no growth limit).</summary>
    public static IReadOnlyDictionary<string, int?> Levels { get; } = new Dictionary<string, int?>(StringComparer.Ordinal)
    {
        ["low"] = 8,
        ["med"] = 12,
        ["high"] = 16,
        ["xhigh"] = 20,
        ["max"] = null,
    };

    [GeneratedRegex("^[ \t]*session-budget:[ \t]*([A-Za-z]+)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex Line();

    /// <summary>The level word of the first <c>session-budget:</c> line of <paramref name="anchorBody"/>, lower-cased; null when there is none.</summary>
    public static string? LevelOf(string? anchorBody) =>
        anchorBody is not null && Line().Match(anchorBody) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>The budget of <paramref name="level"/>: a number, or null for <c>max</c>. A null or unknown level gets <see cref="Default"/>.</summary>
    public static int? BudgetOf(string? level) => level is not null && Levels.TryGetValue(level, out var budget) ? budget : Default;

    /// <summary>The longest chain of parent teams followed, so a team file that names itself as an ancestor ends the walk.</summary>
    private const int MaxAncestors = 16;

    /// <summary>
    /// The budget of <paramref name="team"/> (null for the default team): the one its own open run anchor sets, else the nearest ancestor team's.
    /// The first team on the way up that has an open anchor decides, whether or not the anchor carries a level line. With no anchor anywhere
    /// the budget is <see cref="Default"/>. <paramref name="anchorBodyOf"/> answers a team's open anchor text, or null when it has none;
    /// <paramref name="parentOf"/> answers a team's parent team, or null.
    /// </summary>
    public static int? ForTeam(string? team, Func<string?, string?> anchorBodyOf, Func<string?, string?> parentOf) =>
        ForTeamWithOwner(team, anchorBodyOf, parentOf).Budget;

    /// <summary>
    /// <see cref="ForTeam"/> with the team that owns the budget (ah2-514 LB-2): the team whose open anchor decided, else the last team the walk
    /// reached (the root, when the default applies). The budget bounds that team and every team below it.
    /// </summary>
    public static (int? Budget, string? Owner) ForTeamWithOwner(string? team, Func<string?, string?> anchorBodyOf, Func<string?, string?> parentOf)
    {
        ArgumentNullException.ThrowIfNull(anchorBodyOf);
        ArgumentNullException.ThrowIfNull(parentOf);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var at = team;
        for (var depth = 0; depth <= MaxAncestors; depth++)
        {
            if (anchorBodyOf(at) is { } body)
            {
                return (BudgetOf(LevelOf(body)), at);
            }

            if (parentOf(at) is not { } parent || !seen.Add(parent))
            {
                break;
            }

            at = parent;
        }

        return (Default, at);
    }
}
