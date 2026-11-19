using Ah.Abstractions;
using Ah.Hooks.Gates;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks;

/// <summary>One native hook run: the hook's name, whether its <c>hooks.json</c> row carried <c>--gate</c>, the event bytes and the services.</summary>
public sealed record HookRun(
    string Name,
    bool Gate,
    ReadOnlyMemory<byte> Stdin,
    IHookErrorLog ErrorLog,
    Func<GateServices> Services);

public static class HooksModule
{
    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>The <c>hooks.json</c> PreToolUse scripts: the units that fail closed.</summary>
    public static IReadOnlyList<string> GateNames => HookHost.GateNames;

    /// <summary>Whether a failure of this hook (unusable input, an exception in its body) is a deny: a gate that is not one of the fail-open gates.</summary>
    public static bool FailsClosed(string name, bool gate) => HookHost.FailsClosed(name, gate);

    /// <summary>Whether the hook is a PreToolUse gate at all, fail-open or not.</summary>
    public static bool IsGate(string name, bool gate) => HookHost.IsGate(name, gate);

    /// <summary>The names of every native hook unit this binary has a body for.</summary>
    public static IReadOnlyList<string> UnitNames => GateUnits.All.Select(u => u.Name).ToList();

    /// <summary>Whether this binary has a native body for the hook.</summary>
    /// <summary>
    /// The context lines SessionStart adds for the install stamp (zero or one), starting the opt-in background rebuild through
    /// <paramref name="host"/> when it is due. A stamp that cannot be read or parsed throws.
    /// </summary>
    public static IReadOnlyList<string> InstallStampLines(GateServices services, IInstallStampHost host, string pluginVersion) =>
        InstallStampNotice.Lines(services, host, pluginVersion);

    /// <summary>
    /// The <c>ahd-build</c> row of <c>roster doctor</c> (<c>ok</c> or <c>warn</c> and its text), from the same decision SessionStart
    /// uses. Reads only; a stamp that cannot be read or parsed throws.
    /// </summary>
    public static (string Level, string Detail) AhdBuildRow(IAhEnvironment env, IFileReader files, string pluginVersion, DateTimeOffset now) =>
        InstallStampNotice.DoctorRow(env, files, pluginVersion, now);

    /// <summary>
    /// <c>roster doctor --fix</c> for the <c>ahd-build</c> row: starts the rebuild through <paramref name="host"/> the way SessionStart
    /// does, overriding its automatic holds. Returns the <c>fix</c> entry, or null when current or not installed.
    /// </summary>
    public static string? AhdBuildFix(IInstallStampHost host, IAhEnvironment env, IFileReader files, string pluginVersion, DateTimeOffset now) =>
        InstallStampNotice.Fix(host, env, files, pluginVersion, now);

    /// <summary>The context line SessionStart adds when the herdr desktop mod's marker is older than the plugin (zero or one). Never throws.</summary>
    public static IReadOnlyList<string> HerdrModLines(GateServices services, string pluginVersion) =>
        HerdrModNotice.Lines(services, pluginVersion);

    /// <summary>The herdr desktop home: <c>$HERDR_APP_HOME</c> when set, else <c>~/.config/herdr-app</c>; null when it is set but not absolute.</summary>
    public static string? HerdrAppHome(IAhEnvironment env) => HerdrModNotice.Home(env);

    public static bool Implements(string name) => name == HookHost.ChainName || Units.Any(u => u.Name == name);

    internal static IReadOnlyList<GateUnit> Units { get; } = [.. GateUnits.All, .. AdvisoryUnits.All];

    /// <summary>
    /// Runs the hook. It never returns exit 70 or 2: a gate that cannot decide denies with exit 0, and an advisory hook that
    /// cannot run is silent and leaves one hook-errors row.
    /// </summary>
    public static HookOutcome Run(HookRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        return HookHost.Run(run.Name, run.Gate, run.Stdin, run.ErrorLog, run.Services, Units);
    }

    /// <summary>
    /// The launch-only permissions of a member: one allow list for every role, no deny, ask or sandbox.
    /// </summary>
    public static Ah.Roster.MemberPermissionsResult GenerateMemberPermissions(Ah.Roster.MemberPermissionsRequest request) =>
        Surface.MemberPermissionsGenerator.Generate(request);

    /// <summary>The allow rule that covers <paramref name="command"/> for the member, or why none does.</summary>
    public static Ah.Roster.MemberRuleAnswer QueryMemberRule(Ah.Roster.MemberPermissionsRequest request, string command) =>
        Surface.MemberPermissionsGenerator.RuleFor(request, command);

    /// <summary>
    /// The outcome for a gate whose input could not be read: a deny that names no payload text, with one hook-errors row. An
    /// advisory hook is silent. <paramref name="why"/> is a short fixed word, never input text.
    /// </summary>
    public static HookOutcome GateInputUnusable(string name, string why, IHookErrorLog? errorLog)
    {
        ArgumentNullException.ThrowIfNull(name);
        return HookHost.InputUnusable(name, HookHost.FailsClosed(name, name.StartsWith("pretooluse-", StringComparison.Ordinal)), why, errorLog);
    }

    /// <summary>
    /// The outcome for a hook whose run failed outside its body (composition, platform creation, the host): a gate denies
    /// with exit 0, any other hook is silent, and one hook-errors row is left when a log could be reached.
    /// </summary>
    public static HookOutcome HookFailed(string name, bool gate, Exception error, IHookErrorLog? errorLog)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(error);
        return HookHost.Failed(name, HookHost.FailsClosed(name, gate), error, errorLog);
    }

    /// <summary>
    /// The outcome for a hook the binary refuses to run at all (a state home that is neither the default nor trusted): silent, with
    /// one hook-errors row when a log could be reached. No session has been identified yet, and a deny needs one.
    /// </summary>
    public static HookOutcome Refused(string name, string reason, IHookErrorLog? errorLog)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(reason);
        return HookHost.Refused(name, reason, errorLog);
    }

    /// <summary>The outcome for a hook unit this binary cannot run natively (no binary body, or a contract the caller could not meet).</summary>
    public static HookOutcome Unavailable(string name, string reason, IHookErrorLog errorLog)
    {
        ArgumentNullException.ThrowIfNull(errorLog);
        return HookHost.Unavailable(name, reason, errorLog);
    }
}
