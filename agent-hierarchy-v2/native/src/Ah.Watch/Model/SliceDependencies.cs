namespace Ah.Watch.Model;

/// <summary>One edge of the slice dependency table: a slice type is fed by a key pattern of a lane.</summary>
internal sealed record SliceDependency(string Slice, string Lane, string Keys);

/// <summary>
/// The slice dependency table: which lane keys feed which slice types. A key change must republish every slice an edge here names,
/// so a missing edge is a staleness bug; the cross-lane fixtures hold one change per slice type against it.
/// </summary>
internal static class SliceDependencies
{
    public static IReadOnlyList<SliceDependency> Rows { get; } =
    [
        new("activity:<dirId>/<s>", "activity", "(dir, s)"),
        new("member:<m>", "activity", "references activity:<dirId>/<subject(m)>"),
        new("member:<m>", "registrations", "session(m)"),
        new("member:<m>", "processes", "pid(m)"),
        new("member:<m>", "tokens", "m"),
        new("member:<m>", "messages", "exchanges involving m"),
        new("member:<m>", "teams", "team(m)"),
        new("member:<m>", "sessions", "session(m)"),
        new("team:<t>", "teams", "t"),
        new("team:<t>", "decisions", "t"),
        new("team:<t>", "worktrees", "worktrees of t"),
        new("team:<t>", "registrations", "orchestrator session of t"),
        new("team:<t>", "status", "status.json of t's dir"),
        new("team:<t>", "progress", "a plan with a waiting item and no team record"),
        new("team-summary:<t>", "teams", "t, t's lane parent and t's lane children"),
        new("team-summary:<t>", "teams", "the goal of t and of t's lane parent, read into each team record; a goal write re-reads that team's key (ah2-53)"),
        new("team-summary:<t>", "decisions", "t"),
        new("team-summary:<t>", "parked", "the parked questions of t's dir"),
        new("team-summary:<t>", "worktrees", "worktrees of t"),
        new("team-summary:<t>", "registrations", "sessions of t's members"),
        new("team-summary:<t>", "status", "status.json of t's dir"),
        new("team-summary:<t>", "activity", "members of t"),
        new("team-summary:<t>", "processes", "pids of t's dir"),
        new("team-summary:<t>", "peers", "the roster rows of t's dir"),
        new("team-summary:<t>", "coverage", "every team: the session ids the watcher's blocked pass reads (ah2-102)"),
        new("team-summary:<t>", "messages", "exchanges of t"),
        new("team-summary:<t>", "progress", "plan of t"),
        new("topology:<t>", "teams", "t"),
        new("topology:<t>", "worktrees", "worktrees of t"),
        new("topology:<t>", "registrations", "sessions of t's members"),
        new("topology:<t>", "messages", "exchanges of t"),
        new("worktree:<w>", "worktrees", "w"),
        new("worktree:<w>", "teams", "team(w)"),
        new("exchange:<x>", "messages", "x"),
        new("exchange:<x>", "messages", "gate log of x's dir (open requests)"),
        new("list:exchanges:dir:<d>", "messages", "exchanges of d"),
        new("list:feed:dir:<d>", "messages", "exchanges of d"),
        new("list:exchanges:team:<t>", "messages", "exchanges of t"),
        new("list:exchanges:team:<t>", "teams", "t"),
        new("list:feed:team:<t>", "messages", "exchanges of t"),
        new("list:feed:team:<t>", "teams", "t"),
        new("list:open:team:<t>", "messages", "open exchanges of t"),
        new("list:open:team:<t>", "teams", "t"),
        new("status:<t>", "status", "status.json of t's dir"),
        new("telemetry:<t>", "activity", "members of t"),
        new("telemetry:<t>", "tokens", "members of t"),
        new("timeline:<t>", "activity", "members of t"),
        new("transcript:<dirId>/<subject>", "transcript", "m"),
        new("list:transcript:member:<m>", "transcript", "m"),
        new("files:<dirId>/<team>/<member>", "git", "the member's work root"),
        new("tests:<sha>", "qa", "sha"),
        new("tests:<sha>", "worktrees", "HEAD(w)"),
        new("list:teams", "known-dirs", "*"),
        new("list:teams", "progress", "a plan with a waiting item and no team record"),
        new("plan:<dirId>/<t>", "progress", "(dir, t)"),
        new("plan:<dirId>/<t>", "decisions", "runs of dir"),
        new("plan:<dirId>/<t>", "teams", "t"),
        new("plan:<dirId>/<t>", "messages", "exchanges of t"),
        new("plan:<dirId>/<t>", "worktrees", "worktrees of t"),
        new("queue:<dirId>/<t>", "progress", "(dir, t)"),
        new("queue:<dirId>/<t>", "messages", "exchanges of t"),
        new("sitrep:<dirId>/<t>", "progress", "(dir, t) and (dir, each confirmed lane team of t), ledger.jsonl appends included"),
        new("sitrep:<dirId>/<t>", "messages", "exchanges of t's dir"),
        new("sitrep:<dirId>/<t>", "teams", "team records of t's dir (a lane confirmed or ended)"),
        new("sitrep:<dirId>/<t>", "backlog", "the backlog of t's dir, on the backlog's refresh"),
        new("sitrep:<dirId>/<t>", "activity", "who is working in t or a lane team, as the team summary sees a change"),
        new("sitrep:<dirId>/<t>", "processes", "who is running in t or a lane team, as the team summary sees a change"),
        new("jobs:<dirId>/<t>", "reconcile", "each tick while held"),
        new("list:plan:<dirId>/<t>", "progress", "(dir, t)"),
        new("list:plan-changes:<dirId>/<t>", "progress", "(dir, t)"),
        new("list:teams", "teams", "*"),
        new("flow:<dirId>/<team>", "messages", "exchanges of team"),
        new("flow:<dirId>/<team>", "decisions", "runs of dir"),
        new("flow:<dirId>/<team>", "teams", "team"),
        new("flow-page:<dirId>/<team>/<run>/<k>", "messages", "exchanges of team"),
        new("flow-page:<dirId>/<team>/<run>/<k>", "decisions", "runs of dir"),
        new("flow-page:<dirId>/<team>/<run>/<k>", "teams", "team"),
        new("item-msgs:<dirId>/<team>", "messages", "exchanges of team"),
        new("item-msgs:<dirId>/<team>", "progress", "(dir, team)"),
        new("item-msgs:<dirId>/<team>/<item>", "messages", "exchanges of team naming item"),
        new("item-msgs:<dirId>/<team>/<item>", "progress", "(dir, team)"),
        new("item-log:<dirId>/<n>", "progress", "any team of dir, as the lane re-reads"),
        new("item-log:<dirId>/<n>", "messages", "exchanges of dir"),
        new("item-log:<dirId>/<n>", "item-log", "dir, on a work or backlog write"),
        new("item-desc:<dirId>/<n>", "item-desc", "dir, on a work write"),
    ];

    /// <summary>
    /// The fixed head of a slice template, up to its first variable part: <c>team:&lt;t&gt;</c> is <c>team</c>, <c>list:teams</c> is
    /// <c>list:teams</c> and <c>list:exchanges:team:&lt;t&gt;</c> is <c>list:exchanges:team</c>.
    /// </summary>
    public static string PrefixOf(string template)
    {
        var at = template.IndexOf(":<", StringComparison.Ordinal);
        return at < 0 ? template : template[..at];
    }

    /// <summary>Whether a composer claiming <paramref name="prefix"/> may own slices of <paramref name="template"/>: the template's head is the prefix or extends it past a <c>:</c>.</summary>
    public static bool Claims(string prefix, string template)
    {
        var head = PrefixOf(template);
        return head == prefix || head.StartsWith(prefix + ":", StringComparison.Ordinal);
    }

    /// <summary>The slice types a change to <paramref name="lane"/> can touch.</summary>
    public static IReadOnlyList<string> Feeds(string lane) =>
        [.. Rows.Where(r => r.Lane == lane).Select(static r => r.Slice).Distinct(StringComparer.Ordinal)];

    /// <summary>The lanes that feed <paramref name="slice"/>.</summary>
    public static IReadOnlyList<string> FedBy(string slice) =>
        [.. Rows.Where(r => r.Slice == slice).Select(static r => r.Lane).Distinct(StringComparer.Ordinal)];
}
