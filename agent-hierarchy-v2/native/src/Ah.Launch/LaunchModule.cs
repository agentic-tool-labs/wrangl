using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Launch;

public static class LaunchModule
{
    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <inheritdoc cref="AhLaunch.Classify"/>
    public static AhLaunchResult ClassifyLaunch(IReadOnlyList<string> argv, AhLaunchExpectation expected) => AhLaunch.Classify(argv, expected);

    /// <summary>The same, for a command line whose <c>--settings</c> may name a launch-only settings file that <paramref name="readFile"/> reads.</summary>
    public static AhLaunchResult ClassifyLaunch(IReadOnlyList<string> argv, AhLaunchExpectation expected, Func<string, string?>? readFile) =>
        AhLaunch.Classify(argv, expected, readFile);

    /// <inheritdoc cref="AhLaunch.WordsOf"/>
    public static IReadOnlyList<string> WordsOf(string commandLine) => AhLaunch.WordsOf(commandLine);

    /// <summary>
    /// Whether the process with <paramref name="commandLine"/> was started by ah as member <paramref name="name"/> of the team file
    /// <paramref name="teamFile"/>: true for an ah launch (a drifted model or effort still is one), false for any other, null when it
    /// cannot be told: no command line, or a launch settings file <paramref name="readFile"/> cannot read. False is proof, and a file gone is none.
    /// </summary>
    public static bool? OwnerHasAhLaunch(string? commandLine, string agent, string name, string teamFile, string? model, string? effort, Func<string, string?> readFile)
    {
        var words = commandLine is null ? [] : AhLaunch.WordsOf(commandLine);
        return words.Count == 0 || AhLaunch.SettingsFileUnread(words, readFile)
            ? null
            : AhLaunch.Classify(words, new AhLaunchExpectation(agent, name, teamFile, model, effort), readFile).Verdict != AhLaunchVerdict.NoAhLaunch;
    }

    /// <summary>
    /// The chain of command of the members of <paramref name="team"/>: <see cref="Ah.Roster.RosterModule.ChainOf"/> with the live
    /// registrations in <paramref name="registrations"/> (session, pid, recorded start; a registration without a start is not looked up),
    /// the launch check of <see cref="OwnerHasAhLaunch"/> on <paramref name="table"/>, and the built-in orchestrate role class.
    /// </summary>
    public static Ah.Roster.ChainState ChainOfTeam(
        Ah.Roster.IRosterCore roster,
        string hierarchyDirectory,
        string team,
        IReadOnlyList<(string Session, long Pid, string? Start)> registrations,
        IProcessTable table,
        DateTimeOffset now,
        int observerPid,
        Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(roster);
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(table);

        (long Pid, string Start)? Registered(string session)
        {
            foreach (var reg in registrations)
            {
                if (reg.Session == session)
                {
                    return reg.Start is { } start ? (reg.Pid, start) : null;
                }
            }

            return null;
        }

        return Ah.Roster.RosterModule.ChainOf(
            roster,
            hierarchyDirectory,
            team,
            Registered,
            table,
            now,
            observerPid,
            (pid, agent, name, teamFile, model, effort) => OwnerHasAhLaunch(table.Command(pid), agent, name, teamFile, model, effort, readFile),
            static role => Ah.Config.ConfigModule.BuiltinRoleClass(role) == "orchestrate");
    }

    /// <summary>
    /// The chain of command of one session, judged the same way wherever it is asked: a lane lead (a session that owns a lane team other
    /// than <paramref name="memberTeam"/>) is judged first at its own lane team, when that team reads Down as restored-bare, and
    /// otherwise like any member by the chain of <paramref name="memberTeam"/>. An empty member team is Unknown.
    /// </summary>
    public static Ah.Roster.ChainState ChainOfSession(
        Ah.Roster.IRosterCore roster,
        string hierarchyDirectory,
        string memberTeam,
        string sessionId,
        IReadOnlyList<(string Session, long Pid, string? Start)> registrations,
        IProcessTable table,
        DateTimeOffset now,
        int observerPid,
        Func<string, string?> readFile)
    {
        ArgumentNullException.ThrowIfNull(roster);
        Ah.Roster.ChainState Of(string team) => ChainOfTeam(roster, hierarchyDirectory, team, registrations, table, now, observerPid, readFile);

        foreach (var owned in roster.ListTeamNames(hierarchyDirectory))
        {
            if (owned != memberTeam
                && roster.ReadTeamRecord(hierarchyDirectory, owned).Member("orchestrator")?.Member("session_id") is { IsText: true } owner && owner.AsString() == sessionId
                && Of(owned) is { Kind: Ah.Roster.ChainKind.Down, Why: "restored-bare" } bare)
            {
                return bare;
            }
        }

        return memberTeam.Length > 0 ? Of(memberTeam) : new Ah.Roster.ChainState(Ah.Roster.ChainKind.Unknown, null, "no member team");
    }

    /// <inheritdoc cref="BareDetector.Check"/>
    public static BareVerdict CheckBare(IBareFacts facts, BareMember member, BareSighting? earlier) => BareDetector.Check(facts, member, earlier);

    /// <inheritdoc cref="BareDetector.ProcessesOf"/>
    public static IReadOnlyList<PaneProcess>? PaneProcessesOf(JsonValue answer) => BareDetector.ProcessesOf(answer);

    /// <inheritdoc cref="PaneTie.IsMembers"/>
    public static bool PaneIsMembers(IHerdr herdr, string pane, string? spawnNonce, string? name) => PaneTie.IsMembers(herdr, pane, spawnNonce, name);

    /// <inheritdoc cref="PaneTie.NameHolder"/>
    public static string? PaneNameHolder(IHerdr herdr, string name) => PaneTie.NameHolder(herdr, name);

    /// <inheritdoc cref="TeamJournal.Appended"/>
    public static JsonArray AppendedParentAction(JsonValue team, JsonObject entry) => TeamJournal.Appended(team, entry);

    /// <summary>How old scratch must be before a sweep takes it.</summary>
    public static TimeSpan ScratchRetention => ScratchSweep.Retention;

    /// <inheritdoc cref="ScratchSweep.Run"/>
    public static ScratchSweepReport SweepScratch(IScratchDirectories scratch, ITeardown teardown, Ah.Roster.IRosterCore roster, IFileReader files, Func<string, JsonObject, JsonValue> grants, string dir, string teamHome, DateTimeOffset now, bool commit) =>
        ScratchSweep.Run(scratch, teardown, roster, files, grants, dir, teamHome, now, commit);

    /// <summary>The largest process id a signal may be sent to: Linux's <c>pid_max</c> ceiling (2^22).</summary>
    public const int PidCeiling = 4194304;

    /// <summary>Whether <paramref name="pid"/> may be signalled at all: a single process, never 0 (a group), 1 (init) or a negative (every process).</summary>
    public static bool IsSignalablePid(int pid) => pid > 1 && pid <= PidCeiling;

    /// <summary>
    /// Whether <paramref name="commandLine"/> (arguments joined by single spaces, as <c>ps -o command=</c> prints them) is the Codex
    /// app-server listening on <paramref name="socket"/>: the launch arguments end with <c>app-server --listen unix://&lt;socket&gt;</c>.
    /// A line that is missing, or that merely mentions the socket, is not.
    /// </summary>
    public static bool IsAppserverOf(string? commandLine, string socket)
    {
        ArgumentNullException.ThrowIfNull(socket);
        return commandLine is not null && commandLine.EndsWith($" app-server --listen unix://{socket}", StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="id"/> has the shape of a herdr pane id, <c>workspace:pN</c>: an alphanumeric workspace, a colon, and a
    /// <c>p</c> followed by alphanumerics. A tab id (<c>workspace:tN</c>), a leading dash or any other character is not a pane id, so it
    /// cannot be read as an option or a target of another kind. A local herdr core terminal address (<c>t7</c>) is accepted too.
    /// </summary>
    public static bool IsHerdrPaneId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        var colon = id.IndexOf(':', StringComparison.Ordinal);
        return (colon > 0 && id.Length - colon > 2 && id[colon + 1] == 'p'
            && id.Take(colon).All(char.IsAsciiLetterOrDigit)
            && id.Skip(colon + 2).All(char.IsAsciiLetterOrDigit))
            || Ah.Core.Mux.MuxIds.IsHerdrCoreAddress(id);
    }

    /// <summary>Whether <paramref name="id"/> is a tmux pane id, <c>%</c> and digits, as <c>#{pane_id}</c> prints it.</summary>
    public static bool IsTmuxPaneId(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length > 1 && id[0] == '%' && id.Skip(1).All(char.IsAsciiDigit);
    }

    /// <summary>
    /// Whether <paramref name="value"/> can be one part of a paste-lock file name: letters, digits, <c>_</c>, <c>.</c> and <c>-</c>, no
    /// <c>..</c>, so the name cannot reach outside the lock directory.
    /// </summary>
    public static bool IsLockNamePart(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && !value.Contains("..", StringComparison.Ordinal)
            && value.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '.' or '-');
    }
}
