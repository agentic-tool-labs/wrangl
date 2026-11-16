namespace Ah.Hierarchy;

public static partial class HierarchyModule
{
    /// <summary>The daemon job that notices a release ref moved.</summary>
    public const string ReleaseWatchJob = "release-watch";

    /// <summary>The <c>source.verb</c> of a ship row the release watch writes.</summary>
    public const string LedgerReleaseVerb = "release watch";

    /// <summary>
    /// The release refs of a repository stand at <paramref name="tips"/>; <paramref name="seen"/> is where they last stood for each team, keyed <c>team ref</c>, and gains them.
    /// A ref a team sees for the first time sets its baseline and closes the reviewed items its tip holds (<see cref="ChecklistOnFirstSight"/>). A ref that moved is a ship: a team
    /// with open work gets one ship row, unless the sha already has one, and the transition table closes what the new tip carried. True when <paramref name="seen"/> changed.
    /// <paramref name="outcome"/> hears each team's key and its refusal, null when none. A refusal leaves that team's <paramref name="seen"/> as it was and no other team's, so the
    /// next tick retries the same move; replay is safe, since a sha's row is written once and a closed item does not move again. When a ship closes the last open item whose parent
    /// is a backlog row, <paramref name="closeBacklog"/>(row, sha) closes that row.
    /// </summary>
    public static bool ChecklistOnReleaseTips(
        IHierarchyProgress progress, string pool, IReadOnlyList<string> teams, IReadOnlyDictionary<string, string> tips, IDictionary<string, string> seen, DateTimeOffset at,
        Func<string, string?> slugOfRequest, Func<string, string, bool> isAncestor, Action<string, string?> outcome, Action<string, string>? closeBacklog = null)
    {
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(teams);
        ArgumentNullException.ThrowIfNull(tips);
        ArgumentNullException.ThrowIfNull(seen);
        ArgumentNullException.ThrowIfNull(outcome);
        var changed = false;
        foreach (var (name, tip) in tips)
        {
            foreach (var team in teams)
            {
                var key = team + " " + name;
                var known = seen.TryGetValue(key, out var prior);
                if (known && prior == tip)
                {
                    continue;
                }

                string? problem = null;
                var open = ChecklistEntries(progress, pool, team).Where(static e => ChecklistRules.IsOpen(e.Item)).ToList();
                if (!known)
                {
                    problem = ChecklistOnFirstSight(progress, pool, team, tip, at, OrchestratorAddress(team), isAncestor);
                }
                else if (open.Count > 0)
                {
                    problem = progress.ReadLedger(pool, team).Any(r => LedgerText(r, "kind") == ChecklistEventShip && LedgerText(r, "sha") == tip)
                        ? null
                        : progress.AppendLedger(pool, team, new LedgerEvent(ChecklistEventShip, Sha: tip, Ref: name, Text: $"pushed {name}", SourceVerb: LedgerReleaseVerb, Confirmed: true)).Error;
                    problem ??= ChecklistOnLedgerRow(progress, pool, team, tip, prior!, at, OrchestratorAddress(team), null, slugOfRequest, isAncestor);
                }

                if (closeBacklog is not null)
                {
                    CloseFinishedParents(progress, pool, team, teams, open, tip, closeBacklog);
                }

                outcome(key, problem);
                if (problem is null)
                {
                    seen[key] = tip;
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>Closes each backlog row whose last open item, among every team of the pool, this ship just closed: <paramref name="open"/> is what <paramref name="team"/> held open before it.</summary>
    private static void CloseFinishedParents(
        IHierarchyProgress progress, string pool, string team, IReadOnlyList<string> teams, IReadOnlyList<ChecklistEntry> open, string sha, Action<string, string> closeBacklog)
    {
        var after = ChecklistEntries(progress, pool, team);
        var shipped = after.Where(e => e.Item.State == PlanStates.Done && open.Any(o => o.Stream == e.Stream && o.Item.Item == e.Item.Item)).Select(static e => e.Item.Parent).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        foreach (var parent in shipped.Where(p => !teams.Any(t => ChecklistEntries(progress, pool, t).Any(e => e.Item.Parent == p && ChecklistRules.IsOpen(e.Item)))))
        {
            closeBacklog(parent, sha);
        }
    }
}
