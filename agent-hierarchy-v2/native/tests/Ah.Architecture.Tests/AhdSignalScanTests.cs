namespace Ah.Architecture.Tests;

/// <summary>
/// ahd heal (ah2-22, crash-resume §14.11): no client, hook or daemon job kills a daemon it merely finds slow. Only the heal, after
/// it has proven a hang, and the user's own <c>ahd stop</c> and <c>restart</c> signal the ahd process; and the heal is started only
/// from the places named here. A new signal line or a new place that starts the heal fails this scan until it is named.
/// </summary>
public sealed partial class AhdSignalScanTests
{
    private static readonly string Src = Path.Combine(Workspace.NativeRoot, "src");

    private static IEnumerable<(string File, string Line)> CodeLines(string under) =>
        Directory.EnumerateFiles(Path.Combine(Src, under), "*.cs", SearchOption.AllDirectories)
            .Where(static p => !p.Contains("/obj/", StringComparison.Ordinal) && !p.Contains("/bin/", StringComparison.Ordinal))
            .SelectMany(static p => File.ReadLines(p)
                .Where(static l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal))
                .Select(l => (Path.GetRelativePath(Src, p).Replace('\\', '/'), l.Trim())));

    /// <summary>
    /// A line that can send a signal: a spawned kill (bare or by path, as a request or a Process.Start), the platform call (called or
    /// handed on as a method group), a raw libc kill or killpg, a Process.Kill, or the heal's signal dependency. A text scan: a
    /// delegate invoked under another name is caught where the platform call is handed to it, not where it runs.
    /// </summary>
    internal static bool SendsKill(string line) => SignalForm().IsMatch(line);

    [System.Text.RegularExpressions.GeneratedRegex(
        """\[\s*"kill"\s*,|"[^"\s]*/kill"|"pkill"|"killall"|SignalProcess|Libc\.Kill\s*\(|\.Kill\(|\.Signal\?\.Invoke\(|Process\.Start\(\s*"kill"|ProcessStartInfo\(\s*"kill"|killpg""",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex SignalForm();

    /// <summary>
    /// Every source file with a line that can send a signal: how many such lines it has, and why it may. The count is pinned, so a new
    /// signal line in a file already named here fails the scan too. None but the two marked AHD can reach the ahd pid.
    /// </summary>
    private static readonly Dictionary<string, (int Lines, string Why)> SignalSites = new(StringComparer.Ordinal)
    {
        ["Ah.DaemonClient/AhdClient.cs"] = (1, "AHD: StopHung, the user's ahd stop of a daemon that did not answer its drain"),
        ["Ah.DaemonClient/AhdHealEnvironment.cs"] = (1, "AHD: the heal's TERM and KILL, after the verdict"),
        ["Ah.Cli/Composition/DaemonComposition.cs"] = (3, "hands the platform call to the heal and to the daemon's job stopper; HardExit ends this very process (the daemon exiting itself, no pid named)"),
        ["Ah.Cli/Composition/LaunchComposition.cs"] = (2, "TERM to a member's pane process and to a Codex app-server, each checked as that process first"),
        ["Ah.Platform/Exec/PlatformModule.Exec.cs"] = (1, "the platform call itself"),
        ["Ah.Platform/Exec/ProcessRunner.cs"] = (2, "kills the child process this runner started when it overruns its timeout"),
        ["Ah.Platform/SystemServices.cs"] = (2, "kill(pid, 0): asks whether a pid is alive or owned, sends nothing"),
        ["Ah.Cli/Composition/BareHost.cs"] = (1, "TERM to a member's own session process"),
        ["Ah.Cli/Composition/RosterComposition.cs"] = (1, "the roster verbs' signal, limited to signalable member pids"),
        ["Ah.Commands/Roster/Common/IRosterCliServices.cs"] = (1, "the interface member behind the roster verbs' signal, a no-op by default"),
        ["Ah.Commands/Roster/Team/StopVerb.cs"] = (2, "roster stop: TERM then KILL to member sessions"),
        ["Ah.Commands/Roster/Team/DisbandVerb.cs"] = (1, "a flag named kill, no signal"),
        ["Ah.Cli/Verbs/CheckVerb.cs"] = (1, "ah check: kills the test processes it started"),
        ["Ah.Cli/Verbs/ProcVerb.cs"] = (1, "ah proc stop: the user's or the lead's stop of a runaway process, by pid"),
        ["Ah.Cli/Verbs/AhdCtl/UiServiceVerb.cs"] = (1, "hands the platform call to the web page starter"),
        ["Ah.Daemon/Ui/UiStarter.cs"] = (1, "TERM to the web page process it spawned itself and that did not come up"),
        ["Ah.Launch/Mux/HerdrCore.cs"] = (2, "herdr's own `kill <pane>` command, not a signal"),
        ["Ah.Hooks/Surface/MemberPermissionsGenerator.cs"] = (1, "command names in a permission list, no signal"),
    };

    [Fact]
    public void OnlyTheNamedSourceFilesCanSendASignalAndOnlyTheUsersStopAndTheHealReachAhd()
    {
        var sites = CodeLines(string.Empty).Where(static x => SendsKill(x.Line)).GroupBy(static x => x.File, StringComparer.Ordinal)
            .Select(static g => g.Key + " " + g.Count().ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(SignalSites.Select(static x => x.Key + " " + x.Value.Lines.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal), sites);
        Assert.Equal(
            ["Ah.DaemonClient/AhdClient.cs", "Ah.DaemonClient/AhdHealEnvironment.cs"],
            SignalSites.Where(static x => x.Value.Why.StartsWith("AHD:", StringComparison.Ordinal)).Select(static x => x.Key).Order(StringComparer.Ordinal));
        // the daemon client is the only project that knows the ahd pid as a thing to signal, and it signals from those two files alone
        Assert.Equal(
            ["Ah.DaemonClient/AhdClient.cs", "Ah.DaemonClient/AhdHealEnvironment.cs"],
            CodeLines("Ah.DaemonClient").Where(static x => SendsKill(x.Line)).Select(static x => x.File).Distinct().Order(StringComparer.Ordinal));

        // the stop reaches a signal only through StopDaemonAsync, which only the stop and restart verbs call
        var stops = CodeLines(string.Empty).Where(static x => x.Line.Contains("StopDaemonAsync(", StringComparison.Ordinal) && !x.Line.Contains("internal async", StringComparison.Ordinal))
            .Select(static x => x.File).Distinct().ToList();
        Assert.Equal(["Ah.DaemonClient/AhdLifecycle.cs"], stops);
    }

    [Fact]
    public void TheHealIsStartedOnlyFromTheCommandLineTriggersNeverFromAHookTheDaemonOrTheEnsurePath()
    {
        var launchers = CodeLines(string.Empty).Where(static x => x.Line.Contains("LaunchHeal(", StringComparison.Ordinal)).Select(static x => x.File).Distinct().Order(StringComparer.Ordinal).ToList();

        Assert.Equal(
            [
                "Ah.Abstractions/Transport.cs",                  // the interface member, a no-op by default
                "Ah.Cli/Composition/DaemonComposition.cs",       // trigger 1: a command's direct write gave up on a busy database
                "Ah.Cli/Composition/WakeComposition.cs",         // trigger 2's bridge to the daemon client
                "Ah.Commands/Roster/Common/IRosterWake.cs",      // the bridge's interface member
                "Ah.Commands/Roster/Read/OverdueVerb.cs",        // trigger 2: the overdue failsafe
                "Ah.DaemonClient/AhdClient.cs",                  // the launch itself
            ],
            launchers);

        // the launch is defined once in the client and never called from it: discovery and the ensure path start no heal
        _ = Assert.Single(CodeLines("Ah.DaemonClient"), static x => x.Line.Contains("LaunchHeal(", StringComparison.Ordinal));
        foreach (var project in new[] { "Ah.Hooks", "Ah.Daemon" })
        {
            Assert.DoesNotContain(CodeLines(project), static x => x.Line.Contains("Heal(", StringComparison.Ordinal) || x.Line.Contains("AhdHeal", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("var kill = deps.Processes.Run(new ProcessRequest([\"kill\", \"-TERM\", pid]));", true)]
    [InlineData("new ProcessRequest([\"pkill\", \"ahd\"])", true)]
    [InlineData("_ = PlatformModule.SignalProcess(ahdPid, 9);", true)]
    [InlineData("var stopper = new Stopper(PlatformModule.SignalProcess);", true)]
    [InlineData("_ = Libc.Kill(pid, 15);", true)]
    [InlineData("Process.GetProcessById(pid).Kill();", true)]
    [InlineData("_ = deps.Signal?.Invoke(pid, 9);", true)]
    [InlineData("new ProcessRequest([\"/bin/kill\", \"-TERM\", pid])", true)]
    [InlineData("Run([ \"kill\", \"-9\", pid ])", true)]
    [InlineData("Process.Start(\"kill\", pid);", true)]
    [InlineData("new ProcessStartInfo(\"kill\")", true)]
    [InlineData("_ = Libc.Kill (pid, 15);", true)]
    [InlineData("_ = killpg(group, 15);", true)]
    [InlineData("private static readonly string[] Flags = [\"plan\", \"kill\", \"close\"];", false)]
    [InlineData("var skill = registry.Skill(name);", false)]
    public void TheScanTellsASignalLineFromAFlagName(string line, bool sends)
    {
        Assert.Equal(sends, SendsKill(line));
    }
}
