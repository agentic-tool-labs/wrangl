using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// Closing a stream's tab. <c>herdr tab close</c> kills every pane in the tab, so a tab is closed only when every pane in it is one ah
/// made and none runs a harness. One implementation, called by <c>dismiss --close</c>, <c>disband --close</c>, <c>stream-done</c> and
/// <c>stream-status</c>.
/// </summary>
internal static class StreamTabs
{
    /// <summary>What happened to one stream's recorded tab: closed, or kept with the reason and, for a pane-specific reason, the pane.</summary>
    internal sealed record Outcome(string Stream, string Tab, bool Closed, string? Kept, string? Pane = null);

    /// <summary>How many panes of members of <paramref name="team"/> are in <paramref name="tab"/>; null when the pane listing cannot be had.</summary>
    internal static int? MemberPanes(RosterContext c, JsonValue team, string tab)
    {
        var ids = MemberPaneIds(team);
        return c.Services.Launch?.PaneState?.TabPanes("herdr", tab)?.Count(p => ids.Contains(p.PaneId));
    }

    /// <summary>
    /// Why <paramref name="tab"/> must stay open, or null when it may be closed. Every pane must be one ah made (a current member's, or the
    /// stream's recorded root pane) and none may run a harness; an unreadable pane list or process table counts as unsafe.
    /// </summary>
    internal static (string Reason, string? Pane)? KeepReason(RosterContext c, JsonValue team, JsonValue record, string tab)
    {
        var state = c.Services.Launch?.PaneState;
        if (state?.TabPanes("herdr", tab) is not { } panes)
        {
            return ("panes unknown", null);
        }

        var made = MemberPaneIds(team);
        if (MemberPlanning.Text(record.Member("root_pane_id")) is { Length: > 0 } root)
        {
            made.Add(root);
        }

        if (panes.FirstOrDefault(p => !made.Contains(p.PaneId)) is { } foreign)
        {
            return ("foreign pane", foreign.PaneId);
        }

        var running = panes.FirstOrDefault(p => p.RootPid is not { } pid || state.HarnessRunning(pid) != false);
        return running is null ? null : ("harness running", running.PaneId);
    }

    /// <summary>Records <paramref name="tab"/>'s outcome on <paramref name="target"/> as <c>tab_closed</c> or <c>tab_kept</c>, plus <c>tab_kept_pane</c> when a pane is named.</summary>
    internal static void Report(JsonObject target, Outcome tab)
    {
        target[tab.Closed ? "tab_closed" : "tab_kept"] = tab.Closed ? tab.Tab : tab.Kept!;
        if (tab.Pane is not null)
        {
            target["tab_kept_pane"] = tab.Pane;
        }
    }

    /// <summary>Records every outcome on <paramref name="target"/>: <c>tabs_closed</c> (tab ids) and <c>tabs_kept</c> (stream, tab, reason, pane), each only when not empty.</summary>
    internal static void ReportAll(JsonObject target, IEnumerable<Outcome> outcomes)
    {
        var closed = new JsonArray();
        var kept = new JsonArray();
        foreach (var tab in outcomes)
        {
            if (tab.Closed)
            {
                closed.Add(tab.Tab);
                continue;
            }

            kept.Add(new JsonObject
            {
                ["stream"] = tab.Stream,
                ["tab"] = tab.Tab,
                ["reason"] = tab.Kept!,
                ["pane"] = tab.Pane is null ? JsonValue.Null : tab.Pane,
            });
        }

        if (closed.Count > 0)
        {
            target["tabs_closed"] = closed;
        }

        if (kept.Count > 0)
        {
            target["tabs_kept"] = kept;
        }
    }

    private static HashSet<string> MemberPaneIds(JsonValue team) =>
        team.Member("members") is { IsArray: true } members
            ? members.AsArray().Select(m => MemberPlanning.Text(m.Member("transport_id")) ?? string.Empty).Where(id => id.Length > 0).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// Closes the recorded tab of each of <paramref name="streams"/> that <see cref="KeepReason"/> lets go. A stream must be done, unless
    /// <paramref name="evenIfOpen"/> says the caller ends it (a disband, or stream-done itself). A failed query or close is a warning. The
    /// team is the record from before the caller's closes; panes the closes removed are no longer listed.
    /// </summary>
    internal static List<Outcome> CloseEmpty(RosterContext c, JsonValue team, IEnumerable<string> streams, bool evenIfOpen, List<string> warnings)
    {
        var outcomes = new List<Outcome>();
        if (MemberPlanning.Text(team.Member("transport")) != "herdr")
        {
            return outcomes;
        }

        var launch = c.Services.Launch!;
        var records = launch.Streams.StreamsOf(team);
        var candidates = streams
            .Distinct(StringComparer.Ordinal)
            .Select(s => (Stream: s, Record: records.Get(s), Tab: MemberPlanning.Text(records.Get(s)?.Member("tab_id"))))
            .Where(s => s.Record is { IsMap: true } && !string.IsNullOrEmpty(s.Tab)
                && (evenIfOpen || MemberPlanning.Text(s.Record!.Value.Member("state")) == "done"))
            .ToList();
        if (candidates.Count == 0)
        {
            return outcomes;
        }

        // every server a member lives on is listed, and a tab is closed only on the one server that listed it
        var liveTabs = new Dictionary<string, Ah.Launch.IHerdr?>(StringComparer.Ordinal);
        try
        {
            foreach (var herdr in IRosterLaunch.TeamHerdrs(launch, team))
            {
                foreach (var t in herdr.Invoke(["tab", "list"]).Member("result")?.Member("tabs") is { IsArray: true } tabs
                    ? tabs.AsArray()
                    : throw new InvalidOperationException("herdr tab list produced unexpected shape (missing .result.tabs)"))
                {
                    IRosterLaunch.NoteTab(liveTabs, MemberPlanning.Text(t.Member("tab_id")) ?? string.Empty, herdr);
                }
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            warnings.Add($"stream tabs were not checked: {error.Message}");
            return outcomes;
        }

        launch.PaneState?.Refresh();
        foreach (var (stream, record, tab) in candidates)
        {
            if (!liveTabs.TryGetValue(tab!, out var listedBy))
            {
                continue;
            }

            if (listedBy is not { } server)
            {
                warnings.Add($"stream {stream}: tab {tab} is listed by more than one herdr server, so it was left open");
                continue;
            }

            if (KeepReason(c, team, record!.Value, tab!) is { } keep)
            {
                outcomes.Add(new Outcome(stream, tab!, false, keep.Reason, keep.Pane));
                continue;
            }

            try
            {
                server.Invoke(["tab", "close", tab!]);
                outcomes.Add(new Outcome(stream, tab!, true, null));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                warnings.Add($"stream {stream}: closing its tab {tab} failed: {error.Message}");
            }
        }

        return outcomes;
    }
}
