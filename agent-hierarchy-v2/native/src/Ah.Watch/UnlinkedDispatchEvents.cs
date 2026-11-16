using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>
/// The <c>unlinked-dispatch</c> alarm for one Orchestrator session: a request the Orchestrator sent that no checklist item tracks. Each request is told
/// once per (session, team); one mail lists every eligible request of a team, and a (session, team) gets at most one such mail per cooldown.
/// </summary>
internal static class UnlinkedDispatchEvents
{
    internal const string Name = "unlinked-dispatch";

    /// <summary>
    /// The mails due now, at most one per team. <paramref name="keys"/> holds, for each mail, the told keys to add once it is mailed; the caller also
    /// records <paramref name="now"/> in <paramref name="mailedAt"/> under <see cref="CooldownKey"/> of the session and the event's team then. Told keys of requests that left
    /// <paramref name="candidates"/> are pruned.
    /// </summary>
    internal static IReadOnlyList<WatchEvent> Evaluate(
        string session,
        double now,
        IReadOnlyList<OwnedTeam> owned,
        IReadOnlyList<DispatchCandidate> candidates,
        Func<string?, IReadOnlyList<ChecklistEntry>> checklist,
        ISet<string> told,
        IReadOnlyDictionary<string, double> mailedAt,
        out IReadOnlyDictionary<WatchEvent, IReadOnlyList<string>> keys)
    {
        var keyed = new Dictionary<WatchEvent, IReadOnlyList<string>>();
        keys = keyed;
        var events = new List<WatchEvent>();
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var team in owned)
        {
            var progressTeam = HierarchyModule.ProgressTeamOf(team.Name);
            var mine = candidates.Where(c => HierarchyModule.ProgressTeamOf(c.Team) == progressTeam).ToList();
            foreach (var c in mine)
            {
                _ = listed.Add(Key(session, progressTeam, c.RequestId));
            }

            if (mailedAt.TryGetValue(CooldownKey(session, team.Name), out var last) && now - last < HierarchyModule.UnlinkedDispatchCooldownMs)
            {
                continue;
            }

            var due = mine
                .Where(c => string.IsNullOrEmpty(c.Item) && !c.Note &&c.Slug != HierarchyModule.PipelineAnchorSlug && !told.Contains(Key(session, progressTeam, c.RequestId)))
                .Select(c => (Candidate: c, Created: c.Created is not null && Timestamps.TryParseMs(c.Created, out var ms) ? ms : (double?)null))
                .Where(x => x.Created is { } at && now - at >= HierarchyModule.UnlinkedDispatchGraceMs && now - at <= HierarchyModule.UnlinkedDispatchWindowMs)
                .ToList();
            if (due.Count == 0)
            {
                continue;
            }

            // an empty checklist reads the same as progress off (msg new adds an item to every request it can), so it is skipped as QueueEvents does
            var entries = checklist(team.Name);
            if (entries.Count == 0)
            {
                continue;
            }

            var unlinked = due
                .Where(x => !entries.Any(e => (e.Item.Refs ?? []).Contains(x.Candidate.RequestId, StringComparer.Ordinal)))
                .OrderBy(static x => x.Created)
                .ThenBy(static x => x.Candidate.RequestId, StringComparer.Ordinal)
                .Select(static x => x.Candidate)
                .ToList();
            if (unlinked.Count == 0)
            {
                continue;
            }

            var shown = string.Join(", ", unlinked.Take(HierarchyModule.UnlinkedDispatchListCap).Select(static c => $"{c.RequestId} ({c.Slug})"));
            var more = unlinked.Count > HierarchyModule.UnlinkedDispatchListCap ? $" and {unlinked.Count - HierarchyModule.UnlinkedDispatchListCap} more" : string.Empty;
            var ids = new JsonArray();
            var slugs = new JsonArray();
            foreach (var c in unlinked)
            {
                ids.Add(c.RequestId);
                slugs.Add(c.Slug);
            }

            var ev = new WatchEvent(
                null,
                "UNLINKED-DISPATCH",
                $"ahd alarm: checklist drift — {unlinked.Count} request(s) you dispatched are not on the checklist: {shown}{more}.\n"
                + "Link each: `ah roster checklist add \"<what>\" --ref <id>` (new item) or `ah roster checklist set <item> --ref <id>` (existing item), or `--item` at dispatch.\n"
                + "Operational call (O3): link them, or note why they are untracked.",
                new JsonObject { ["request_id"] = JsonValue.Null, ["request_ids"] = ids, ["slugs"] = slugs, ["count"] = unlinked.Count })
            { Name = Name, Team = team.Name };
            events.Add(ev);
            keyed[ev] = [.. unlinked.Select(c => Key(session, progressTeam, c.RequestId))];
        }

        var prefix = session + "|";
        var marker = "|" + Name + "|";
        foreach (var gone in told.Where(k => k.StartsWith(prefix, StringComparison.Ordinal) && k.Contains(marker, StringComparison.Ordinal) && !listed.Contains(k)).ToList())
        {
            _ = told.Remove(gone);
        }

        return events;
    }

    /// <summary>The key under which the time of a team's last <c>unlinked-dispatch</c> mail in a session is kept: the cooldown is per (session, team).</summary>
    internal static string CooldownKey(string session, string? team) => $"{session}|{HierarchyModule.ProgressTeamOf(team)}";

    private static string Key(string session, string team, string requestId) => $"{session}|{team}|{Name}|{requestId}";
}
