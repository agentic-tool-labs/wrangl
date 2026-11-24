using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.StateDb;

namespace Ah.Cli.Composition;

/// <summary>A state database that exists and cannot be read for rules. A missing database, or one not yet at the rules schema, simply holds none.</summary>
internal sealed class RulesUnreadableException(string message) : Exception(message);

/// <summary>Who rules are being resolved for: the verb, the session hook and the subagent block all ask through this.</summary>
internal sealed record RulesAsk(string? Team, IReadOnlySet<string> Names, bool Orchestrator, string? TeamDir = null, string? Parent = null);

/// <summary>What the verbs, the hooks and the brief footer read: the rows of a repo, resolved for a subject through the one resolver.</summary>
internal static class RulesReads
{
    /// <summary>
    /// Runs <paramref name="read"/> over a reader. <paramref name="none"/> stands for a database with no rules table to read. <paramref name="open"/> opens the reader, a
    /// fresh one when null; the daemon passes its pooled <see cref="ServiceGraph.OpenReader"/>.
    /// </summary>
    internal static T With<T>(string ahHome, T none, Func<Func<string, object?[], IReadOnlyList<object?[]>>, T> read, Func<string, StateDbReaderOpen>? open = null)
    {
        var opened = (open ?? StateDbModule.OpenReader)(ahHome);
        if (opened.Reader is not { } reader)
        {
            return File.Exists(Path.Join(ahHome, "state", "ah.db")) ? throw new RulesUnreadableException(opened.Message) : none;
        }

        using (reader)
        {
            try
            {
                return read(reader.Query);
            }
            catch (Exception e) when (e.Message.Contains("no such table: rules", StringComparison.Ordinal))
            {
                return none;
            }
            catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException and not RulesUnreadableException)
            {
                throw new RulesUnreadableException(e.Message);
            }
        }
    }

    /// <summary>
    /// The team and its parents, nearest first; <paramref name="parent"/>, when set, is the team's parent whatever its record says. A cycle, a missing
    /// record or a team record that cannot be read ends the chain.
    /// </summary>
    internal static IReadOnlyList<string> TeamChain(ServiceGraph g, string dir, string? team, string? parent = null)
    {
        var chain = new List<string>();
        for (var at = team; at is { Length: > 0 } && !chain.Contains(at, StringComparer.Ordinal); at = chain.Count == 1 && parent is { Length: > 0 } ? parent : ParentOf(g, dir, at))
        {
            chain.Add(at);
        }

        return chain;
    }

    /// <summary>The subject for <paramref name="team"/>'s chain, with its roster, and no goals or projects yet.</summary>
    internal static RuleSubject SubjectOf(ServiceGraph g, string dir, string? team, string? parent = null)
    {
        var chain = TeamChain(g, dir, team, parent);
        return new RuleSubject(chain, new HashSet<long>(), new HashSet<long>(), RosterOf(g, dir, chain));
    }

    /// <summary>
    /// The roster over <paramref name="chain"/>: the first team in it that records one, so a lane child team runs its parent's; <c>default</c> when none does; null with no team.
    /// A team record that cannot be read records none.
    /// </summary>
    internal static string? RosterOf(ServiceGraph g, string dir, IReadOnlyList<string> chain)
    {
        if (chain.Count == 0)
        {
            return null;
        }

        foreach (var team in chain)
        {
            try
            {
                if (g.Roster.TeamRosterKey(dir, team) is { } roster)
                {
                    return roster;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
            {
                // as ParentOf: an unreadable record names nothing
            }
        }

        return RulesOfEngagement.DefaultRoster;
    }

    private static string? ParentOf(ServiceGraph g, string dir, string team)
    {
        try
        {
            return g.Roster.ReadTeamRecord(dir, team).Member("parent") is not { } parent ? null
                : parent.IsText ? parent.AsString()
                : parent.Member("team") is { IsText: true } t ? t.AsString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonParseException)
        {
            return null;
        }
    }

    /// <summary>
    /// What applies to <paramref name="ask"/>: repo, the team chain, and the goals and projects over the open items it holds and the open backlog rows it has an open brief on;
    /// for an Orchestrator, those of anyone in its team.
    /// </summary>
    internal static ResolvedRules ForMember(ServiceGraph g, string ahHome, string dir, RulesAsk ask) =>
        Resolved(g, ahHome, dir, SubjectOf(g, ask.TeamDir ?? dir, ask.Team, ask.Parent), board => HeldItems(g, dir, board, ask).Concat(BriefedRows(g, dir, board, ask)));

    /// <summary>What applies at a work item: repo, its own chain, and its team when it is a team item.</summary>
    internal static ResolvedRules ForItem(ServiceGraph g, string ahHome, string dir, WorkRow row)
    {
        var team = row.IsBacklog ? null : row.Team;
        return Resolved(g, ahHome, dir, SubjectOf(g, dir, team), board => board.ViewOf(row.N) is { } view ? [view] : []);
    }

    /// <summary>What applies at a team, or with no team at the repo.</summary>
    internal static ResolvedRules ForTeam(ServiceGraph g, string ahHome, string dir, string? team) =>
        Resolved(g, ahHome, dir, SubjectOf(g, dir, team), _ => []);

    /// <summary>
    /// What a session gets for <paramref name="ask"/>: the block (nothing when no rule applies, or the one line when the rules cannot be read), then the standing ADR line. The
    /// session always starts.
    /// </summary>
    internal static string Block(ServiceGraph g, string ahHome, string dir, RulesAsk ask)
    {
        string? goal;
        string? block;
        try
        {
            goal = GoalLine(ahHome, dir, TeamChain(g, ask.TeamDir ?? dir, ask.Team, ask.Parent));
            block = RulesOfEngagement.Render(ForMember(g, ahHome, dir, ask).Rules, With<string?>(ahHome, null, query => HierarchyModule.WorkPrefix(query, dir)), RulesOfEngagement.BlockCap);
        }
        catch (Exception e) when (e is RulesUnreadableException or StoreUnavailableException)
        {
            goal = null;
            block = RulesOfEngagement.UnreadableLine;
        }

        // the goal line rides outside the cap, which bounds only the rule lines
        return (goal is null ? string.Empty : goal + "\n") + (block is null ? RulesOfEngagement.AdrLine : block + "\n" + RulesOfEngagement.AdrLine);
    }

    /// <summary>
    /// Whether a <see cref="RulesOfEngagement.LocalOnlyKey"/> rule applies to <paramref name="team"/> as resolved from <paramref name="cwd"/> (ah2-596). False when none does
    /// and when the rules cannot be read, so <c>stream-done</c> then behaves as without it.
    /// </summary>
    internal static bool LocalOnly(ServiceGraph g, string ahHome, string cwd, string? team, Func<string, StateDbReaderOpen>? open = null)
    {
        try
        {
            var dir = BacklogVerb.DirOf(g, cwd);
            var rows = With<IReadOnlyList<RuleRow>>(ahHome, [], query => RulesOfEngagement.Rows(query, dir), open);
            return RulesOfEngagement.Resolve(rows, SubjectOf(g, dir, team)).Rules.Any(r => r.Row.Key == RulesOfEngagement.LocalOnlyKey && !r.Row.IsProposed);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The team goal that applies over <paramref name="chain"/> (ah2-53): the nearest team's own, else a parent's; null with no team or no goal.</summary>
    internal static RuleRow? GoalOf(string ahHome, string dir, IReadOnlyList<string> chain, Func<string, StateDbReaderOpen>? open = null) =>
        chain.Count == 0 ? null : RulesOfEngagement.GoalOf(With<IReadOnlyList<RuleRow>>(ahHome, [], query => RulesOfEngagement.Goals(query, dir), open), chain);

    /// <summary>
    /// The goal a status row or a team row shows (ah2-53): <c>{text, done_when, team}</c>, <c>team</c> naming whose row it is. Null with none, and null when the dir, the
    /// chain or the goal cannot be read: a goal never fails what shows it. <paramref name="open"/> as for <see cref="With"/>.
    /// </summary>
    internal static JsonObject? ShownGoal(string ahHome, Func<string> dirOf, Func<string, IReadOnlyList<string>> chainOf, Func<string, StateDbReaderOpen>? open = null)
    {
        try
        {
            var dir = dirOf();
            return GoalOf(ahHome, dir, chainOf(dir), open) is { } goal
                ? new JsonObject { ["text"] = goal.Text, ["done_when"] = goal.Applies is { } doneWhen ? doneWhen : JsonValue.Null, ["team"] = goal.Scope }
                : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>The goal line a session or a brief over <paramref name="chain"/> starts with, or null.</summary>
    internal static string? GoalLine(string ahHome, string dir, IReadOnlyList<string> chain) =>
        GoalOf(ahHome, dir, chain) is { } goal ? RulesOfEngagement.GoalLine(goal, goal.Scope != chain[0]) : null;

    /// <summary>Reads the items only when a project or goal rule exists: the board reads every team's plan, and most repos will have none.</summary>
    private static ResolvedRules Resolved(ServiceGraph g, string ahHome, string dir, RuleSubject subject, Func<WorkBoard, IEnumerable<WorkItemView>> items)
    {
        var rows = With<IReadOnlyList<RuleRow>>(ahHome, [], query => RulesOfEngagement.Rows(query, dir));
        if (rows.Any(r => r.Level is RulesOfEngagement.Goal or RulesOfEngagement.Project))
        {
            WorkVerb.Reconcile(g, ahHome, dir);
            subject = With(ahHome, subject, query =>
            {
                var board = HierarchyModule.WorkBoardOf(query, g.Hierarchy.Progress, dir);
                return RulesOfEngagement.Over(subject, board, items(board));
            });
        }

        return RulesOfEngagement.Resolve(rows, subject);
    }

    private static IEnumerable<WorkItemView> HeldItems(ServiceGraph g, string dir, WorkBoard board, RulesAsk ask)
    {
        var team = ask.Team is { Length: > 0 } t ? HierarchyModule.ProgressTeamOf(t) : null;
        if (!ask.Orchestrator)
        {
            return board.Items.Where(v => WorkBoard.HeldBy(v, ask.Names, team));
        }

        var names = new HashSet<string>(ask.Names, StringComparer.Ordinal) { "orchestrator" };
        if (ask.Team is { Length: > 0 } && g.Roster.ReadTeamRecord(ask.TeamDir ?? dir, ask.Team).Member("members") is { IsArray: true } members)
        {
            foreach (var name in members.AsArray().Select(m => m.Member("name")).Where(n => n is { IsText: true }))
            {
                _ = names.Add(name!.Value.AsString());
            }
        }

        return board.Items.Where(v => WorkBoard.IsOpen(v) && v.Plan?.Holder is { Length: > 0 } holder && (v.Row.Team == team || (v.Row.IsBacklog && names.Contains(holder))));
    }

    /// <summary>
    /// A dispatch only links a backlog row, so it holds nothing (rules-of-engagement §9): the open rows with a ref naming a request of the caller's team that has no response and is
    /// addressed to the caller, by <c>to_name</c> when set, else <c>to</c>; for an Orchestrator, addressed to anyone.
    /// </summary>
    private static IEnumerable<WorkItemView> BriefedRows(ServiceGraph g, string dir, WorkBoard board, RulesAsk ask)
    {
        var rows = board.Items.Where(static v => v.Row.IsBacklog && WorkBoard.IsOpen(v) && v.Plan?.Refs is { Count: > 0 }).ToList();
        if (rows.Count == 0)
        {
            return [];
        }

        var unanswered = new List<(string Id, string Path)>();
        foreach (var exchange in g.Hierarchy.Core.ListExchangesWithIds(WorkVerb.PoolsOf(g, dir), [.. rows.SelectMany(static v => v.Plan!.Refs!).Distinct(StringComparer.Ordinal)]))
        {
            if (exchange.Member("response") is not { IsMap: true } && exchange.Member("id") is { IsText: true } id && exchange.Member("request")?.Member("path") is { IsText: true } path)
            {
                unanswered.Add((id.AsString(), path.AsString()));
            }
        }

        // one read of the message store for every request, not one per request
        var docs = g.Hierarchy.Core.ReadMsgFiles([.. unanswered.Select(static u => u.Path)]);
        var open = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < unanswered.Count; i++)
        {
            var fm = docs[i].Member("fm");
            string? Front(string key) => fm?.Member(key) is { IsText: true } value && value.AsString() is { Length: > 0 } text && text != "null" ? text : null;

            if ((Front("team") ?? string.Empty) == (ask.Team ?? string.Empty) && (ask.Orchestrator || (Front("to_name") ?? Front("to")) is { } to && ask.Names.Contains(to)))
            {
                _ = open.Add(unanswered[i].Id);
            }
        }

        return rows.Where(v => v.Plan!.Refs!.Any(open.Contains));
    }
}
