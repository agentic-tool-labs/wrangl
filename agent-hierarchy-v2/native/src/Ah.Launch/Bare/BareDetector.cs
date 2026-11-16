using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Launch;

/// <summary>One process in a pane's foreground group, as <c>herdr pane process-info</c> lists it.</summary>
public sealed record PaneProcess(int Pid, IReadOnlyList<string> Argv, string? Cwd);

/// <summary>What the detector asks of the world; each answer is read fresh.</summary>
public interface IBareFacts
{
    /// <summary>The foreground processes of <paramref name="paneId"/>; null when the multiplexer could not say.</summary>
    IReadOnlyList<PaneProcess>? PaneProcesses(string paneId);

    /// <summary>The newest registered session id of <paramref name="name"/> in <paramref name="team"/>; null when none.</summary>
    string? LastSessionId(string name, string team);

    /// <summary>
    /// Whether another member claims the process: a live registration of <paramref name="sessionId"/> (when it has one) naming an address
    /// other than <paramref name="member"/> of <paramref name="team"/>, or any other member row holding <paramref name="pid"/> with start
    /// time <paramref name="pidStart"/>.
    /// </summary>
    bool ClaimedElsewhere(string? sessionId, int pid, string? pidStart, string team, string member);

    /// <summary>The start time of <paramref name="pid"/> as the process table gives it; null when the process is gone.</summary>
    string? StartOf(int pid);

    /// <summary>The real path of <paramref name="path"/>.</summary>
    string RealPath(string path);

    /// <summary>The text of the launch-only settings file at <paramref name="path"/>; null when it cannot be read.</summary>
    string? ReadLaunchFile(string path) => null;
}

/// <summary>
/// <see cref="IBareFacts"/> over herdr and the caller's own reads of registrations and the process table. A herdr call that fails
/// leaves the pane unreadable, which concludes nothing.
/// </summary>
/// <param name="Herdr">The herdr client.</param>
/// <param name="LastSession">The newest session id of a member: team, then name.</param>
/// <param name="Claimed">Whether another live member claims a session or pid: session id, pid, team, member.</param>
/// <param name="Start">The start time of a pid.</param>
/// <param name="Real">The real path of a path.</param>
public sealed record BareFacts(
    IHerdr Herdr,
    Func<string, string, string?> LastSession,
    Func<string?, int, string?, string, string, bool> Claimed,
    Func<int, string?> Start,
    Func<string, string> Real) : IBareFacts
{
    /// <summary>Reads the text of a launch-only settings file; null when it cannot be read.</summary>
    public Func<string, string?>? ReadFile { get; init; }

    public string? ReadLaunchFile(string path) => ReadFile?.Invoke(path);

    public IReadOnlyList<PaneProcess>? PaneProcesses(string paneId)
    {
        try
        {
            return BareDetector.ProcessesOf(Herdr.Invoke(["pane", "process-info", "--pane", paneId]));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public string? LastSessionId(string name, string team) => LastSession(team, name);

    public bool ClaimedElsewhere(string? sessionId, int pid, string? pidStart, string team, string member) => Claimed(sessionId, pid, pidStart, team, member);

    public string? StartOf(int pid) => Start(pid);

    public string RealPath(string path) => Real(path);
}

/// <summary>The member whose pane is examined.</summary>
/// <param name="Team">Team name.</param>
/// <param name="Name">Member name.</param>
/// <param name="PaneId">The pane the member's row names.</param>
/// <param name="ExpectedRoot">The row's <c>expected_root</c>.</param>
/// <param name="Expected">What the builder's command line for this member carries.</param>
public sealed record BareMember(string Team, string Name, string PaneId, string? ExpectedRoot, AhLaunchExpectation Expected);

/// <summary>A process seen on an earlier tick, for the two-reads-agree check.</summary>
public sealed record BareSighting(int Pid, string? PidStart);

/// <summary>What the pane holds.</summary>
public enum BareKind
{
    /// <summary>The multiplexer could not say; nothing is concluded.</summary>
    Unreadable,

    /// <summary>No Claude process runs in the pane.</summary>
    NoClaude,

    /// <summary>A Claude process whose launch-fixed settings name this member and whose agent, model and effort match the row.</summary>
    Launched,

    /// <summary>A bare Claude that matches the member on M1-M5 but has been seen only once.</summary>
    Candidate,

    /// <summary>A bare Claude that matches the member on every check.</summary>
    Bare,

    /// <summary>A bare Claude in the pane that fails at least one of M1, M3-M6.</summary>
    Unmatched,

    /// <summary>A Claude process launched by ah for this member whose agent, model or effort differ from the row: reported, never acted on.</summary>
    Drift,
}

/// <summary>The detector's answer for one member.</summary>
/// <param name="Kind">What the pane holds.</param>
/// <param name="Pid">The Claude process, when one was singled out.</param>
/// <param name="PidStart">Its start time.</param>
/// <param name="SessionId">The session id on its <c>--resume</c>, when it has one.</param>
/// <param name="Failed">The checks that failed, <c>M1</c> to <c>M6</c>.</param>
/// <param name="Why">One line for a human.</param>
public sealed record BareVerdict(BareKind Kind, int? Pid, string? PidStart, string? SessionId, IReadOnlyList<string> Failed, string Why)
{
    internal static BareVerdict Of(BareKind kind, string why) => new(kind, null, null, null, [], why);
}

/// <summary>Finds a member's Claude that was started without the builder's flags, by the six checks M1-M6.</summary>
internal static class BareDetector
{
    /// <summary>The foreground processes in a <c>herdr pane process-info</c> answer; null when the answer has no list.</summary>
    public static IReadOnlyList<PaneProcess>? ProcessesOf(JsonValue answer)
    {
        if (answer.Member("result")?.Member("process_info")?.Member("foreground_processes") is not { IsArray: true } list)
        {
            return null;
        }

        var found = new List<PaneProcess>();
        foreach (var p in list.AsArray())
        {
            if (p.Member("pid") is not { } pid || !int.TryParse(JsConvert.ToJsString(pid), out var id) || p.Member("argv") is not { IsArray: true } argv)
            {
                continue;
            }

            found.Add(new PaneProcess(
                id,
                argv.AsArray().Where(a => a.IsText).Select(a => a.AsString()).ToList(),
                p.Member("cwd") is { IsText: true } cwd ? cwd.AsString() : null));
        }

        return found;
    }

    /// <summary>
    /// Examines the pane of <paramref name="member"/>. <paramref name="earlier"/> is what the previous tick saw there; null on the first
    /// sighting, which can only be a <see cref="BareKind.Candidate"/>.
    /// </summary>
    public static BareVerdict Check(IBareFacts facts, BareMember member, BareSighting? earlier)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(member);
        if (facts.PaneProcesses(member.PaneId) is not { } processes)
        {
            return BareVerdict.Of(BareKind.Unreadable, "the pane's processes could not be read");
        }

        var claudes = processes.Where(p => MeshModule.IsHarnessCommand(string.Join(' ', p.Argv))).ToList();
        if (claudes.Count == 0)
        {
            return BareVerdict.Of(BareKind.NoClaude, "no Claude runs in the pane");
        }

        var classes = claudes.Select(p => AhLaunch.Classify(p.Argv, member.Expected, facts.ReadLaunchFile)).ToList();
        if (classes.Any(r => r.Verdict == AhLaunchVerdict.AhLaunch))
        {
            return BareVerdict.Of(BareKind.Launched, "launched by ah");
        }

        if (classes.FirstOrDefault(r => r.Verdict == AhLaunchVerdict.Drift) is { } drift)
        {
            return BareVerdict.Of(BareKind.Drift, drift.Drift!);
        }

        var failed = new List<string>();
        if (claudes.Count != 1)
        {
            failed.Add("M1");
            return new BareVerdict(BareKind.Unmatched, null, null, null, failed, $"{claudes.Count} Claude processes run in the pane");
        }

        var claude = claudes[0];
        var session = ResumedSession(claude.Argv);
        var start = facts.StartOf(claude.Pid);
        if (session is null || session != facts.LastSessionId(member.Name, member.Team))
        {
            failed.Add("M3");
        }

        if (claude.Cwd is null || member.ExpectedRoot is null || !SameProject(facts.RealPath(claude.Cwd), facts.RealPath(member.ExpectedRoot)))
        {
            failed.Add("M4");
        }

        if (facts.ClaimedElsewhere(session, claude.Pid, start, member.Team, member.Name))
        {
            failed.Add("M5");
        }

        var seenTwice = earlier is not null && start is not null && earlier.Pid == claude.Pid && earlier.PidStart == start;
        if (start is null)
        {
            failed.Add("M6");
        }

        if (failed.Count > 0)
        {
            return new BareVerdict(BareKind.Unmatched, claude.Pid, start, session, failed, $"does not match {member.Name}: {string.Join(", ", failed)}");
        }

        return new BareVerdict(seenTwice ? BareKind.Bare : BareKind.Candidate, claude.Pid, start, session, [], seenTwice ? "launched without ah's flags" : "seen once");
    }

    private const string WorktreeSegment = "/.claude/worktrees/";

    /// <summary>
    /// Whether the process's cwd is in the project of the member's <c>expected_root</c>: the root itself or anything under the
    /// project root, which for a stream worktree (<c>&lt;project&gt;/.claude/worktrees/&lt;stream&gt;</c>) is the main checkout.
    /// A member is started where its spawn ran (often the main checkout or another worktree) and works in its stream worktree after,
    /// so the process's own cwd is not its stream root.
    /// </summary>
    internal static bool SameProject(string cwd, string expectedRoot)
    {
        var at = expectedRoot.IndexOf(WorktreeSegment, StringComparison.Ordinal);
        var project = (at >= 0 ? expectedRoot[..at] : expectedRoot).TrimEnd('/');
        return project.Length > 0 && (cwd == project || cwd.StartsWith(project + "/", StringComparison.Ordinal));
    }

    private static string? ResumedSession(IReadOnlyList<string> argv)
    {
        for (var i = 0; i + 1 < argv.Count; i++)
        {
            if (argv[i] == "--resume" || argv[i] == "-r")
            {
                return argv[i + 1];
            }
        }

        return null;
    }
}
