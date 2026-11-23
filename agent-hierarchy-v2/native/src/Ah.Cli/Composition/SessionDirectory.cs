using Ah.Abstractions;
using Ah.Mesh;
using Ah.Hooks;
using Ah.Platform;

namespace Ah.Cli.Composition;

/// <summary>
/// The registered orchestrators and live sessions of a hierarchy directory, and the process table to relate them to a pid:
/// what the owner proof and the write gate both read to say who a process is.
/// </summary>
internal sealed class SessionDirectory(ServiceGraph g) : IGateSessions
{
    public IProcessTable Table { get; } = PlatformModule.CreateProcessTable(g.Runner);

    public IReadOnlyList<TeamOrchestrator> Teams(string hierarchyDir) => Entries(hierarchyDir).Select(e => e.Entry).ToList();

    /// <summary>Each team's orchestrator with the raw team name it was read under (null for the unnamed legacy team).</summary>
    internal List<(string? Raw, TeamOrchestrator Entry)> Entries(string hierarchyDir)
    {
        var teams = new List<(string?, TeamOrchestrator)>();
        foreach (var name in new string?[] { null }.Concat(g.Roster.ListTeamNames(hierarchyDir)))
        {
            var record = g.Roster.ReadTeamRecord(hierarchyDir, name);
            if (record.IsNull || record.Member("orchestrator") is not { IsMap: true } orchestrator
                || orchestrator.Member("pid") is not { IsNumber: true } pid
                || !double.IsInteger(pid.AsNumber()) || pid.AsNumber() <= 1 || pid.AsNumber() > int.MaxValue)
            {
                continue;
            }

            var start = orchestrator.Member("pid_start") is { IsText: true } text && text.AsString().Length > 0 ? text.AsString() : null;
            var deputy = Ah.Roster.RosterModule.DutyOf(record)?.Get("deputy") is { IsText: true } d ? d.AsString() : null;
            teams.Add((name, new TeamOrchestrator(name ?? "team", (int)pid.AsNumber(), start, deputy)));
        }

        return teams;
    }

    /// <summary>
    /// The nearest process above <paramref name="ownPid"/> that is a team Orchestrator, a registered session or a harness session with a
    /// session-pids row in one of <paramref name="rowDirs"/>, given the team Orchestrators in <paramref name="teams"/>. <paramref name="named"/> is the <c>CLAUDE_PID</c> of the asking environment, or null.
    /// </summary>
    internal NearestSession NearestSession(int ownPid, string? named, IReadOnlyList<TeamOrchestrator> teams, IReadOnlyList<string> rowDirs)
    {
        var verdicts = new Dictionary<int, bool>();

        bool IsSession(int pid)
        {
            if (verdicts.TryGetValue(pid, out var known))
            {
                return known;
            }

            var verdict = IsHarnessSession(pid, named, rowDirs);
            verdicts[pid] = verdict;
            return verdict;
        }

        return MeshModule.NearestRegisteredSession(Table, ownPid, teams, Registered(), IsSession);
    }

    /// <summary>Whether <paramref name="pid"/> is a Claude harness session with a session-pids row in one of <paramref name="rowDirs"/>.</summary>
    internal bool IsHarnessSession(int pid, string? named, IReadOnlyList<string> rowDirs)
    {
        try
        {
            return MeshModule.IsHarnessSession(
                pid,
                named,
                p => Ah.Core.Time.ProcessStart.TryParseUtc(Table.StartTime(p), out var started) ? started : null,
                (p, since) => rowDirs.Distinct(StringComparer.Ordinal).Any(d => g.Hierarchy.Status.SessionsOfPid(d, p, since).Count > 0),
                Table.Command,
                p => ExecutableOf(g.Runner, p));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The executable path of <paramref name="pid"/> as <c>ps -o comm=</c> prints it, or null.</summary>
    internal static string? ExecutableOf(IProcessRunner runner, int pid)
    {
        var result = runner.Run(new ProcessRequest(
            ["/bin/ps", "-ww", "-o", "comm=", "-p", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            Environment: new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin", ["LC_ALL"] = "C" },
            Timeout: TimeSpan.FromSeconds(2)));
        return result.ExitCode == 0 && System.Text.Encoding.UTF8.GetString(result.Stdout.Span).Trim() is { Length: > 0 } text ? text : null;
    }

    public IReadOnlyList<SessionRegistration> Sessions() => MeshModule.LiveSessions(g.Mailbox);

    public string? Executable(int pid) => ExecutableOf(g.Runner, pid);

    public IReadOnlyList<RegisteredSession> Registered()
    {
        var sessions = new List<RegisteredSession>();
        foreach (var r in g.Mailbox.LiveRegistrations())
        {
            if (r.Get("pid") is not { IsNumber: true } p || !double.IsInteger(p.AsNumber()) || p.AsNumber() <= 1 || p.AsNumber() > int.MaxValue)
            {
                continue;
            }

            var start = r.Get("pid_start") is { IsText: true } s && s.AsString().Length > 0 ? s.AsString() : null;
            sessions.AddRange(g.Mailbox.RegistrationBoxes(r).Select(b => new RegisteredSession((int)p.AsNumber(), start, b.Address)));
        }

        return sessions;
    }
}
