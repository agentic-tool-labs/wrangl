using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Mesh;

namespace Ah.Cli.Verbs;

/// <summary>A process whose descendants a caller may stop: its own session, or a live member session of a team it owns.</summary>
internal readonly record struct ProcRoot(int Pid, string Owner);

/// <summary>Who may stop which process, from the caller's nearest session and the team records, with no I/O of its own.</summary>
internal static class ProcOwnership
{
    public const string SessionOwner = "session";

    /// <summary>
    /// The hierarchy directories the caller's own live registration records its team files in: the registrations of the process
    /// <paramref name="nearest"/> names, with the start time they recorded. Never a directory the caller names (<c>--cwd</c>).
    /// </summary>
    public static IReadOnlyList<string> TrustedDirs(
        IProcessTable table, NearestSession nearest, IEnumerable<(int Pid, string? Start, IReadOnlyList<string> TeamFiles)> registrations)
    {
        if (nearest.Kind is NearestKind.None or NearestKind.Unavailable || nearest.Pid <= 1)
        {
            return [];
        }

        return [.. registrations
            .Where(r => r.Pid == nearest.Pid && ProcessIdentity.Same(table, r.Pid, r.Start))
            .SelectMany(r => r.TeamFiles)
            .Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// The teams the caller owns: those read from a directory in <paramref name="trusted"/> for which the owner proof holds (the
    /// team file's Orchestrator, pid and start time, is the caller's nearest session, and the shell carries no member marker).
    /// </summary>
    public static IReadOnlyList<(TeamOrchestrator Orchestrator, JsonValue Record)> OwnedTeams(
        IProcessTable table,
        string? memberMarker,
        int ownPid,
        IReadOnlyList<(string Dir, TeamOrchestrator Orchestrator, JsonValue Record)> teams,
        IReadOnlyList<string> trusted,
        IReadOnlyList<RegisteredSession> registered,
        Func<int, bool> alive)
    {
        var inTrusted = teams.Where(t => trusted.Contains(t.Dir, StringComparer.Ordinal)).ToList();
        var orchestrators = inTrusted.Select(t => t.Orchestrator).ToList();
        return [.. inTrusted
            .Where(t => MeshModule.CheckOwnerProof(new OwnerProofInputs(table, memberMarker, ownPid, t.Orchestrator, orchestrators, registered, alive)).Allowed)
            .Select(t => (t.Orchestrator, t.Record))];
    }

    /// <summary>
    /// The lane child teams the caller leads: those in <paramref name="trusted"/> with a <c>parent</c> link whose Orchestrator, pid and
    /// start time, is the caller's nearest session. A lead's shell carries a member marker, so the owner proof never reaches these.
    /// Each record's members include those its home team lent the lane: their rows stay in the home team, with this team as <c>lead</c>.
    /// </summary>
    public static IReadOnlyList<(TeamOrchestrator Orchestrator, JsonValue Record)> LedLanes(
        IProcessTable table,
        NearestSession nearest,
        IReadOnlyList<(string Dir, TeamOrchestrator Orchestrator, JsonValue Record)> teams,
        IReadOnlyList<string> trusted) =>
        nearest.Kind is NearestKind.None or NearestKind.Unavailable || nearest.Pid <= 1
            ? []
            : [.. teams
                .Where(t => trusted.Contains(t.Dir, StringComparer.Ordinal)
                    && t.Record.Member("parent") is { IsMap: true }
                    && t.Orchestrator.Pid == nearest.Pid
                    && ProcessIdentity.Same(table, nearest.Pid, t.Orchestrator.PidStart))
                .Select(t => (t.Orchestrator, WithLentMembers(t, teams)))];

    private static JsonValue WithLentMembers(
        (string Dir, TeamOrchestrator Orchestrator, JsonValue Record) lane, IReadOnlyList<(string Dir, TeamOrchestrator Orchestrator, JsonValue Record)> teams)
    {
        var home = lane.Record.Member("parent")?.Member("team") is { IsText: true } name ? name.AsString() : null;
        var lent = teams
            .Where(t => t.Dir == lane.Dir && t.Orchestrator.Team == home && t.Record.Member("members") is { IsArray: true })
            .SelectMany(t => t.Record.Member("members")!.Value.AsArray())
            .Where(row => row.IsMap && row.Member("lead")?.Member("team") is { IsText: true } lead && lead.AsString() == lane.Orchestrator.Team)
            .ToList();
        if (lent.Count == 0)
        {
            return lane.Record;
        }

        var members = new JsonArray();
        if (lane.Record.Member("members") is { IsArray: true } own)
        {
            members.AddRange(own.AsArray());
        }

        members.AddRange(lent);
        var merged = lane.Record.AsObject().Clone();
        merged["members"] = members;
        return merged;
    }

    /// <summary>
    /// The lane child teams of a team in <paramref name="owned"/>, of its current incarnation (<c>team_id</c>), whose lead's process is
    /// gone: no one else can reach the processes of their members. A lane with a live lead stays with that lead.
    /// </summary>
    public static IReadOnlyList<(TeamOrchestrator Orchestrator, JsonValue Record)> LeaderlessLanes(
        IProcessTable table,
        IReadOnlyList<(TeamOrchestrator Orchestrator, JsonValue Record)> owned,
        IReadOnlyList<(string Dir, TeamOrchestrator Orchestrator, JsonValue Record)> teams,
        IReadOnlyList<string> trusted) =>
        [.. teams
            .Where(t => trusted.Contains(t.Dir, StringComparer.Ordinal)
                && t.Record.Member("parent") is { IsMap: true } parent
                && parent.Member("team") is { IsText: true } home
                && parent.Member("team_id") is { IsText: true } id
                && owned.Any(o => o.Orchestrator.Team == home.AsString()
                    && o.Record.Member("team_id") is { IsText: true } current && current.AsString() == id.AsString())
                && !ProcessIdentity.Same(table, t.Orchestrator.Pid, t.Orchestrator.PidStart))
            .Select(t => (t.Orchestrator, t.Record))];

    /// <summary>
    /// The roots of the caller: the session process <paramref name="nearest"/> names, then each live member session of every team
    /// in <paramref name="owned"/>. A member counts only while its process has the start time its row records and
    /// <paramref name="isHarnessSession"/> accepts it.
    /// </summary>
    public static IReadOnlyList<ProcRoot> Roots(
        IProcessTable table,
        NearestSession nearest,
        IReadOnlyList<(TeamOrchestrator Orchestrator, JsonValue Record)> owned,
        Func<int, bool> isHarnessSession)
    {
        if (nearest.Kind is NearestKind.None or NearestKind.Unavailable || nearest.Pid <= 1)
        {
            return [];
        }

        var roots = new List<ProcRoot> { new(nearest.Pid, SessionOwner) };
        foreach (var (orchestrator, record) in owned)
        {
            roots.AddRange(LiveMembers(table, orchestrator.Team, record).Where(m => m.Pid != nearest.Pid && isHarnessSession(m.Pid)));
        }

        return roots;
    }

    /// <summary>The live harness-session members of <paramref name="team"/> when it is among <paramref name="owned"/>; null when it is not.</summary>
    public static IReadOnlyList<ProcRoot>? TeamRoots(
        IProcessTable table, IReadOnlyList<(TeamOrchestrator Orchestrator, JsonValue Record)> owned, string team, Func<int, bool> isHarnessSession) =>
        owned.Where(t => t.Orchestrator.Team == team).Select(t => t.Record).FirstOrDefault() is { IsMap: true } record
            ? [.. LiveMembers(table, team, record).Where(m => isHarnessSession(m.Pid))]
            : null;

    /// <summary>Each member row of <paramref name="record"/> whose process is alive with the start time the row records.</summary>
    public static IEnumerable<ProcRoot> LiveMembers(IProcessTable table, string team, JsonValue record)
    {
        if (record.Member("members") is not { IsArray: true } members)
        {
            yield break;
        }

        foreach (var row in members.AsArray())
        {
            if (!row.IsMap || row.Member("name") is not { IsText: true } name)
            {
                continue;
            }

            foreach (var source in new[] { row, row.Member("bare") ?? JsonValue.Null })
            {
                if (source.IsMap
                    && source.Member("pid") is { IsNumber: true } p && double.IsInteger(p.AsNumber()) && p.AsNumber() > 1 && p.AsNumber() <= int.MaxValue
                    && source.Member("pid_start") is { IsText: true } start
                    && ProcessIdentity.Same(table, (int)p.AsNumber(), start.AsString()))
                {
                    yield return new ProcRoot((int)p.AsNumber(), name.AsString() + "@" + team);
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The root that owns <paramref name="pid"/> in <paramref name="snapshot"/>, or why it is refused. Pid 1, the calling process and
    /// its ancestors (the caller's own session among them) and any root itself are refused; only a strict descendant of a root is owned.
    /// </summary>
    public static (ProcRoot? Owner, string? Why) Decide(IReadOnlyList<ProcessNode> snapshot, IReadOnlyList<ProcRoot> roots, int ownPid, int pid)
    {
        if (pid <= 1)
        {
            return (null, "pid 1 and lower are never stopped");
        }

        var byPid = snapshot.GroupBy(n => n.Pid).ToDictionary(g => g.Key, g => g.First());
        if (pid == ownPid || Chain(byPid, ownPid).Contains(pid))
        {
            return (null, "the calling command or one of its ancestors (the caller's own session among them)");
        }

        if (roots.Any(r => r.Pid == pid))
        {
            return (null, "a session process itself, not one of its descendants");
        }

        foreach (var above in Chain(byPid, pid))
        {
            if (roots.FirstOrDefault(r => r.Pid == above) is { Owner: not null } root)
            {
                return (root, null);
            }
        }

        return (null, roots.Count == 0
            ? "no registered session is an ancestor of this command"
            : "not a descendant of the caller's session or of a live member of a team it owns");
    }

    /// <summary>The ancestors of <paramref name="pid"/> in the snapshot, parent first, stopping at a loop or a missing link.</summary>
    internal static List<int> Chain(IReadOnlyDictionary<int, ProcessNode> byPid, int pid)
    {
        var chain = new List<int>();
        var seen = new HashSet<int> { pid };
        while (byPid.TryGetValue(pid, out var node) && node.Parent > 0 && seen.Add(node.Parent) && chain.Count < 256)
        {
            chain.Add(node.Parent);
            pid = node.Parent;
        }

        return chain;
    }

    /// <summary><paramref name="root"/> and every process below it in the snapshot, the root first.</summary>
    public static List<ProcessNode> Tree(IReadOnlyList<ProcessNode> snapshot, ProcessNode root)
    {
        var children = snapshot.ToLookup(n => n.Parent);
        var tree = new List<ProcessNode> { root };
        var seen = new HashSet<int> { root.Pid };
        for (var i = 0; i < tree.Count; i++)
        {
            tree.AddRange(children[tree[i].Pid].Where(c => seen.Add(c.Pid)));
        }

        return tree;
    }
}

/// <summary>SIGTERM to a pinned process tree, a grace period, then SIGKILL to whatever of it is still the same process.</summary>
internal sealed class ProcStopper(Func<IReadOnlyList<ProcessNode>?> snapshot, Func<int, int, bool> signal, Action<TimeSpan> sleep)
{
    public const int SigTerm = 15;

    public const int SigKill = 9;

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Stops <paramref name="pinned"/> (root first, each pinned by the start time it had in the snapshot it came from). A process
    /// whose pid now has another start time is not signalled. Before SIGKILL the descendants of the pinned processes still running
    /// are read again, so a child forked after the snapshot is pinned and killed too. Returns gone (the root was not running as pinned), stopped (it ended
    /// within <paramref name="grace"/> of SIGTERM) or killed (it needed SIGKILL).
    /// </summary>
    public string Stop(IReadOnlyList<ProcessNode> pinned, TimeSpan grace)
    {
        var tree = pinned.ToList();
        var root = tree[0];
        if (snapshot() is not { } first)
        {
            return "unproven";
        }

        Expand(tree, first);
        var alive = Alive(tree, first);
        var rootWasAlive = alive.Contains(root.Pid);
        foreach (var node in tree.Where(n => alive.Contains(n.Pid)))
        {
            signal(node.Pid, SigTerm);
        }

        for (var waited = TimeSpan.Zero; waited < grace && alive.Count > 0; waited += Poll)
        {
            sleep(Poll);
            alive = Alive(tree) ?? alive;
        }

        var rootKilled = false;
        if (alive.Count > 0 && snapshot() is { } now)
        {
            Expand(tree, now);
        }

        if (alive.Count > 0 && Alive(tree) is { Count: > 0 } still)
        {
            foreach (var node in tree.Where(n => still.Contains(n.Pid)))
            {
                signal(node.Pid, SigKill);
                rootKilled |= node.Pid == root.Pid;
            }
        }

        return !rootWasAlive ? "gone" : rootKilled ? "killed" : "stopped";
    }

    /// <summary>
    /// Adds to <paramref name="tree"/> the children forked since it was pinned: the descendants in <paramref name="now"/> of its
    /// processes still running as pinned, each pinned by the start time <paramref name="now"/> gives it.
    /// </summary>
    private static void Expand(List<ProcessNode> tree, IReadOnlyList<ProcessNode> now)
    {
        var live = Alive(tree, now);
        var known = tree.Select(n => n.Pid).ToHashSet();
        foreach (var node in now.Where(n => live.Contains(n.Pid) && tree.Any(t => t.Pid == n.Pid && t.Started == n.Started)).ToList())
        {
            tree.AddRange(ProcOwnership.Tree(now, node).Where(d => d.Started is not null && known.Add(d.Pid)));
        }
    }

    /// <summary>The pids of <paramref name="tree"/> still running with their pinned start time; null when the table cannot say.</summary>
    private HashSet<int>? Alive(IReadOnlyList<ProcessNode> tree) => snapshot() is { } now ? Alive(tree, now) : null;

    private static HashSet<int> Alive(IReadOnlyList<ProcessNode> tree, IReadOnlyList<ProcessNode> now)
    {
        var started = now.GroupBy(n => n.Pid).ToDictionary(g => g.Key, g => g.First().Started);
        return [.. tree.Where(n => n.Started is not null && started.TryGetValue(n.Pid, out var s) && s == n.Started).Select(n => n.Pid)];
    }
}
