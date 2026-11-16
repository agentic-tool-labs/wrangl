using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ah proc list [--team T]</c> and <c>ah proc stop &lt;pid&gt;... [--grace S] [--dry-run]</c>: find and stop a runaway process
/// that the caller owns by ancestry (its own session's, or a live member's of a team it owns), never one named by pattern.
/// <c>ah proc wait &lt;pid&gt;... [--timeout S]</c> waits for processes to end, in place of a <c>while kill -0</c> loop.
/// Ownership reads only the team files the caller's own live registration records; <c>--cwd</c> is accepted and ignored.
/// </summary>
internal static class ProcVerb
{
    private const string Usage = "usage: ah proc list [--team T] [--match TEXT] | ah proc stop <pid>... [--grace S] [--dry-run] | ah proc wait <pid>... [--timeout S]";

    private static readonly TimeSpan WaitPoll = TimeSpan.FromMilliseconds(250);

    /// <summary>The audit file under the hierarchy directory: the CLI has no daemon route for this verb.</summary>
    internal const string AuditFile = "proc-audit.jsonl";

    internal static int Run(CliContext context, string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0 || args[0] is not ("list" or "stop" or "wait"))
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        string? team = null;
        string? match = null;
        var grace = TimeSpan.FromSeconds(3);
        var timeout = TimeSpan.FromSeconds(60);
        var dryRun = false;
        var pids = new List<int>();
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--cwd" when i + 1 < args.Length:
                    i++;
                    break;
                case "--team" when args[0] == "list" && i + 1 < args.Length:
                    team = args[++i];
                    break;
                case "--match" when args[0] == "list" && i + 1 < args.Length:
                    match = args[++i];
                    break;
                case "--grace" when args[0] == "stop" && i + 1 < args.Length
                    && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0 && s <= 600:
                    grace = TimeSpan.FromSeconds(s);
                    i++;
                    break;
                case "--timeout" when args[0] == "wait" && i + 1 < args.Length
                    && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t >= 0 && t <= 600:
                    timeout = TimeSpan.FromSeconds(t);
                    i++;
                    break;
                case "--dry-run" when args[0] == "stop":
                    dryRun = true;
                    break;
                default:
                    if (args[0] is "stop" or "wait" && int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
                        && AcceptsPid(args[0], pid))
                    {
                        pids.Add(pid);
                        break;
                    }

                    stderr.WriteLine($"ah proc: unexpected argument '{args[i]}'");
                    stderr.WriteLine(Usage);
                    return 2;
            }
        }

        if (args[0] is "stop" or "wait" && pids.Count == 0)
        {
            stderr.WriteLine(Usage);
            return 2;
        }

        if (context.Services is not { } services)
        {
            stderr.WriteLine("ah proc: the services have not been composed");
            return 70;
        }

        var g = services.Value;
        if (args[0] == "wait")
        {
            return Wait(pids, g.Io.Processes.PidAlive, timeout, Thread.Sleep, stdout);
        }

        var sessions = new SessionDirectory(g);
        var table = sessions.Table;
        if (table.All() is not { } snapshot)
        {
            stderr.WriteLine("ah proc: the process table could not be read");
            return 1;
        }

        // every directory a live registration records a team file in: the mailbox's, not the caller's, choice
        var registrations = g.Mailbox.LiveRegistrations()
            .Select(r => (
                Pid: r.Get("pid") is { IsNumber: true } p && double.IsInteger(p.AsNumber()) && p.AsNumber() > 1 && p.AsNumber() <= int.MaxValue ? (int)p.AsNumber() : 0,
                Start: r.Get("pid_start") is { IsText: true } st ? st.AsString() : null,
                TeamFiles: g.Mailbox.RegistrationTeamFiles(JsonValue.FromObject(r))))
            .ToList();
        var liveDirs = registrations.SelectMany(r => r.TeamFiles).Select(f => Path.GetDirectoryName(Path.GetDirectoryName(f)))
            .OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var teams = liveDirs.SelectMany(d => sessions.Entries(d).Select(e => (Dir: d, e.Entry, Record: g.Roster.ReadTeamRecord(d, e.Raw)))).ToList();
        var nearest = sessions.NearestSession(Environment.ProcessId, null, [.. teams.Select(t => t.Entry)], liveDirs);
        var trusted = ProcOwnership.TrustedDirs(table, nearest, registrations);
        var owned = ProcOwnership.OwnedTeams(
            table,
            context.Env.GetVariable(MeshModule.MemberMarkerEnv) is { Length: > 0 } marker ? marker : null,
            Environment.ProcessId,
            teams,
            trusted,
            sessions.Registered(),
            pid => g.Io.Processes.PidAlive(pid));
        owned = [.. owned.Concat(ProcOwnership.LedLanes(table, nearest, teams, trusted))
            .Concat(ProcOwnership.LeaderlessLanes(table, owned, teams, trusted))
            .DistinctBy(t => t.Orchestrator.Team)];
        bool IsHarness(int pid) => sessions.IsHarnessSession(pid, null, trusted);
        var roots = ProcOwnership.Roots(table, nearest, owned, IsHarness);
        var rss = Rss(g.Runner);
        if (args[0] == "list")
        {
            if (team is not null)
            {
                if (ProcOwnership.TeamRoots(table, owned, team, IsHarness) is not { } teamRoots)
                {
                    stderr.WriteLine($"ah proc: '{team}' is not a team you own");
                    return 2;
                }

                roots = teamRoots;
            }

            stdout.Write(JsonText.Stringify(List(snapshot, roots, Environment.ProcessId, rss, g.Io.Clock.UtcNow, match), 0) + "\n");
            return 0;
        }

        var audit = Path.Join(context.Env.AhHome, "state", AuditFile);
        return Stop(g, table, snapshot, roots, pids, grace, dryRun, rss, audit, stdout);
    }

    /// <summary>
    /// The processes below <paramref name="roots"/>, largest resident set first; never the calling command or its ancestors. With
    /// <paramref name="match"/>, only those whose command line contains it (case-sensitive), in place of <c>pgrep -f</c>.
    /// </summary>
    internal static JsonArray List(
        IReadOnlyList<ProcessNode> snapshot, IReadOnlyList<ProcRoot> roots, int ownPid, IReadOnlyDictionary<int, long> rss, DateTimeOffset now,
        string? match = null)
    {
        var byPid = snapshot.GroupBy(n => n.Pid).ToDictionary(x => x.Key, x => x.First());
        var mine = ProcOwnership.Chain(byPid, ownPid).Append(ownPid).ToHashSet();
        var rows = new List<(long Rss, JsonObject Row)>();
        foreach (var node in snapshot)
        {
            if (mine.Contains(node.Pid) || roots.Any(r => r.Pid == node.Pid)
                || (match is not null && node.Command?.Contains(match, StringComparison.Ordinal) != true)
                || ProcOwnership.Chain(byPid, node.Pid).Select(p => roots.FirstOrDefault(r => r.Pid == p)).FirstOrDefault(r => r.Owner is not null) is not { Owner: { } owner })
            {
                continue;
            }

            var kb = rss.GetValueOrDefault(node.Pid);
            rows.Add((kb, new JsonObject
            {
                ["pid"] = JsonValue.FromNumber(node.Pid),
                ["owner"] = owner,
                ["rss_mb"] = JsonValue.FromNumber(Mb(kb)),
                ["elapsed_s"] = node.Started is { } started ? JsonValue.FromNumber(Math.Max(0, Math.Round((now - started).TotalSeconds))) : JsonValue.Null,
                ["command"] = node.Command is { } c ? JsonValue.FromString(c) : JsonValue.Null,
            }));
        }

        var list = new JsonArray();
        foreach (var (_, row) in rows.OrderByDescending(r => r.Rss))
        {
            list.Add(row);
        }

        return list;
    }

    private static int Stop(
        ServiceGraph g, IProcessTable table, IReadOnlyList<ProcessNode> snapshot, IReadOnlyList<ProcRoot> roots,
        List<int> pids, TimeSpan grace, bool dryRun, IReadOnlyDictionary<int, long> rss, string audit, TextWriter stdout)
    {
        var stopper = new ProcStopper(table.All, PlatformModule.SignalProcess, Thread.Sleep);
        var results = new JsonArray();
        var refused = false;
        foreach (var pid in pids.Distinct())
        {
            var row = StopOne(stopper, snapshot, roots, Environment.ProcessId, pid, grace, dryRun, rss);
            refused |= row.Get("outcome") is { IsText: true } o && o.AsString() == "refused";
            results.Add(row);
            if (!dryRun)
            {
                var line = new JsonObject
                {
                    ["ts"] = g.Io.Clock.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    ["verb"] = "proc stop",
                    ["caller_pid"] = JsonValue.FromNumber(Environment.ProcessId),
                };
                foreach (var key in new[] { "pid", "start", "command", "rss_mb", "owner", "outcome", "why", "tree" })
                {
                    if (row.Get(key) is { } v)
                    {
                        line[key] = v;
                    }
                }

                AuditLog.Append(audit, line);
            }
        }

        stdout.Write(JsonText.Stringify(new JsonObject { ["dry_run"] = dryRun, ["audit"] = audit, ["pids"] = results }, 0) + "\n");
        return refused ? 2 : 0;
    }

    /// <summary>Whether <paramref name="verb"/> takes <paramref name="pid"/>: <c>wait</c> needs a pid above 1 (0 and 1 never end); <c>stop</c> decides its own.</summary>
    internal static bool AcceptsPid(string verb, int pid) => verb != "wait" || pid > 1;

    /// <summary>
    /// Waits, polling every 250 ms, until no pid in <paramref name="pids"/> is alive or <paramref name="timeout"/> has passed. Prints
    /// <c>{gone, alive}</c>; exit 0 when every pid is gone, 1 when some are still alive. Signals nothing and needs no ownership.
    /// </summary>
    internal static int Wait(IReadOnlyList<int> pids, Func<int, bool> alive, TimeSpan timeout, Action<TimeSpan> sleep, TextWriter stdout)
    {
        var waited = TimeSpan.Zero;
        var left = pids.Distinct().Where(alive).ToList();
        while (left.Count > 0 && waited < timeout)
        {
            sleep(WaitPoll);
            waited += WaitPoll;
            left = left.Where(alive).ToList();
        }

        var gone = new JsonArray();
        var still = new JsonArray();
        foreach (var pid in pids.Distinct())
        {
            (left.Contains(pid) ? still : gone).Add(JsonValue.FromNumber(pid));
        }

        stdout.Write(JsonText.Stringify(new JsonObject { ["gone"] = gone, ["alive"] = still }, 0) + "\n");
        return left.Count == 0 ? 0 : 1;
    }

    /// <summary>One pid's decision and, unless refused or a dry run, its stop: the row the JSON output and the audit line carry.</summary>
    internal static JsonObject StopOne(
        ProcStopper stopper, IReadOnlyList<ProcessNode> snapshot, IReadOnlyList<ProcRoot> roots, int ownPid, int pid, TimeSpan grace, bool dryRun,
        IReadOnlyDictionary<int, long> rss)
    {
        var node = snapshot.FirstOrDefault(n => n.Pid == pid);
        var row = new JsonObject
        {
            ["pid"] = JsonValue.FromNumber(pid),
            ["command"] = node?.Command is { } c ? JsonValue.FromString(c) : JsonValue.Null,
            ["rss_mb"] = JsonValue.FromNumber(Mb(rss.GetValueOrDefault(pid))),
            ["start"] = node?.Started is { } st ? JsonValue.FromString(st.ToString("O", CultureInfo.InvariantCulture)) : JsonValue.Null,
        };
        if (node is null && pid > 1)
        {
            row["owner"] = JsonValue.Null;
            row["outcome"] = "gone";
            return row;
        }

        var (owner, why) = ProcOwnership.Decide(snapshot, roots, ownPid, pid);
        if (owner is not { } root)
        {
            row["owner"] = JsonValue.Null;
            row["outcome"] = "refused";
            row["why"] = why!;
            return row;
        }

        row["owner"] = root.Owner;
        var tree = ProcOwnership.Tree(snapshot, node!);
        row["tree"] = JsonValue.FromNumber(tree.Count);
        row["outcome"] = dryRun ? "dry-run" : stopper.Stop(tree, grace);
        return row;
    }

    /// <summary>The resident set size of every process, in KiB, from one <c>ps</c> read; empty when it cannot be read.</summary>
    private static Dictionary<int, long> Rss(IProcessRunner runner)
    {
        var map = new Dictionary<int, long>();
        var result = runner.Run(new ProcessRequest(
            ["/bin/ps", "-axo", "pid=,rss="],
            Environment: new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin", ["LC_ALL"] = "C" },
            Timeout: TimeSpan.FromSeconds(5),
            OutputCapBytes: 8 << 20));
        if (result.ExitCode != 0)
        {
            return map;
        }

        foreach (var line in System.Text.Encoding.UTF8.GetString(result.Stdout.Span).Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && int.TryParse(parts[0], CultureInfo.InvariantCulture, out var pid) && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var kb))
            {
                map[pid] = kb;
            }
        }

        return map;
    }

    private static double Mb(long kb) => Math.Round(kb / 1024.0, 1);
}
