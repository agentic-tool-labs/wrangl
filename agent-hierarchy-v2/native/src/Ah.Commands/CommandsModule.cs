using Ah.Abstractions;

namespace Ah.Commands;

public static class CommandsModule
{
    private static Func<Stream>? s_standardInput;
    private static Func<IRosterCliServices>? s_rosterServices;
    private static Func<IHomeTrustServices>? s_homeTrustServices;

    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>Where verbs that read standard input read it from; the process's own standard input when never composed.</summary>
    public static void Compose(Func<Stream> standardInput)
    {
        ArgumentNullException.ThrowIfNull(standardInput);
        Volatile.Write(ref s_standardInput, standardInput);
    }

    /// <summary>
    /// Where the roster verbs get the services they run on. The factory is asked on every roster invocation and is expected to
    /// build once, so a process that never runs a roster verb builds nothing.
    /// </summary>
    public static void ComposeRoster(Func<IRosterCliServices> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Volatile.Write(ref s_rosterServices, services);
    }

    /// <summary>The daemon's relaunch of one member from its team's checkpoint over <paramref name="services"/>; see <see cref="IMemberRelaunch"/>.</summary>
    public static IMemberRelaunch CreateMemberRelaunch(IRosterCliServices services) => new DaemonMemberRelaunch(services);

    /// <summary>The daemon's removal of one member proven dead over <paramref name="services"/>; see <see cref="IMemberRemoval"/>.</summary>
    public static IMemberRemoval CreateMemberRemoval(IRosterCliServices services) => new DaemonMemberRemove(services);

    /// <summary>The members of <paramref name="team"/>, of <paramref name="names"/>, that <c>dismiss</c>'s release check would close now (live, idle, clean); the dismiss nudge's one check.</summary>
    public static IReadOnlyList<string> ReadyToDismiss(IRosterCliServices services, string hierarchyDir, string projectRoot, string team, IReadOnlyList<string> names) =>
        ReleaseGate.ReadyOf(services, hierarchyDir, projectRoot, team, names);

    /// <summary>The close core both daemon routes run: <c>{dir, team, by, reopen?, preview?, confirm_live?}</c>, answering the preview, the result or <c>{refused}</c>.</summary>
    public static Ah.Core.Json.JsonObject TeamClose(IRosterCliServices services, Ah.Core.Json.JsonObject input) => TeamCloseVerb.Apply(services, input);

    /// <summary>The rename core <c>ah roster rename</c> and <c>POST /ui/v1/team-rename</c> run: <c>{dir, team, display_name, by}</c>, answering <c>{ok, team, display_name, previous}</c> or <c>{refused}</c>.</summary>
    public static Ah.Core.Json.JsonObject TeamRename(IRosterCliServices services, Ah.Core.Json.JsonObject input) => TeamRenameVerb.Apply(services, input);

    /// <summary>The daemon's retire, through the same marker write: why it could not write, or null.</summary>
    public static string? TeamRetire(IRosterCliServices services, string hierarchyDir, string team, long lastWorkMs) => TeamCloseVerb.Retire(services, hierarchyDir, team, lastWorkMs);

    /// <summary>
    /// The daemon's <c>checklist audit</c> of <paramref name="team"/>: the newest ship its board records, and the audit's findings when asked. Read only.
    /// <paramref name="entriesOf"/> gives a board's checklist entries by (pool, team), the daemon's held fold (b459 step 17); <paramref name="lastShipOf"/>
    /// gives a stream's newest ship by (pool, team, stream), reading it with the given function only when the daemon holds none (b459 step 18).
    /// </summary>
    public static (string? LastShip, Func<IReadOnlyList<Ah.Hierarchy.ChecklistAuditFinding>> Findings) ChecklistAudit(
        IRosterCliServices services, string hierarchyDir, string projectRoot, string team, Func<string, string, IReadOnlyList<Ah.Hierarchy.ChecklistEntry>> entriesOf,
        Func<string, string, string, Func<string?>, string?> lastShipOf) =>
        ChecklistVerb.DaemonAudit(services, hierarchyDir, projectRoot, team, entriesOf, lastShipOf);

    /// <summary>Where <c>ahd home trust|untrust</c> get the services they run on; asked on every invocation, built once by the factory.</summary>
    public static void ComposeHomeTrust(Func<IHomeTrustServices> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        Volatile.Write(ref s_homeTrustServices, services);
    }

    /// <summary>The composed trust-verb services, or null when the composition root has not provided them.</summary>
    internal static IHomeTrustServices? HomeTrustServices() => Volatile.Read(ref s_homeTrustServices)?.Invoke();

    /// <summary>The composed roster services, or null when the composition root has not provided them.</summary>
    internal static IRosterCliServices? RosterServices() => Volatile.Read(ref s_rosterServices)?.Invoke();

    /// <summary><c>ah usage-report [session|day|week|month|all | --team &lt;name&gt; | --fleet] [--json]</c>: prints the usage report to <paramref name="stdout"/>; exit 0, or non-zero with the reason on <paramref name="stderr"/> for a bad <c>--team</c>.</summary>
    public static int RunUsageReport(UsageReportServices services, IReadOnlyList<string> args, TextWriter stdout, TextWriter? stderr = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        return UsageReport.Run(services, args, stdout, stderr);
    }

    /// <summary>
    /// <c>ah issue-intake --cwd &lt;abs&gt; --out-dir &lt;abs&gt; (--issues &lt;ref,...&gt; | --labelled)</c>: prints one JSON line to
    /// <paramref name="stdout"/> and returns 0, or <c>{"error": ...}</c> and 1.
    /// </summary>
    public static int RunIssueIntake(IssueIntakeServices services, IReadOnlyList<string> args, TextWriter stdout)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        return IssueIntake.Run(services, args, stdout);
    }

    internal static Stream StandardInput() => (Volatile.Read(ref s_standardInput) ?? Console.OpenStandardInput)();
}
