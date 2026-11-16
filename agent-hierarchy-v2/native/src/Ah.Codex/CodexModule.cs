using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Codex;

/// <summary>The Codex app-server adapter's public face: the launch-side rules the spawn uses, the version gate, and the job registry.</summary>
public static partial class CodexModule
{
    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary>The harness name the adapter registers its members under.</summary>
    public const string Harness = "codex-appserver";

    /// <summary>The sandbox mode of a launch that sets none.</summary>
    public const string DefaultSandbox = CodexLaunch.DefaultSandbox;

    /// <summary>The approval policy of a launch that sets none.</summary>
    public const string DefaultApproval = CodexLaunch.DefaultApproval;

    /// <summary>The values a checked launch runs under: what it set, else the defaults.</summary>
    public static CodexModes LaunchModes(LaunchCheck? launch) => CodexLaunch.LaunchModes(launch);

    /// <summary>Checks a member's launch arguments against the allow-list; reads no environment.</summary>
    public static LaunchCheck CheckLaunch(IReadOnlyList<string> args, string ahHome, IFileReader files) =>
        CodexLaunch.CheckLaunch(args, ahHome, files);

    /// <summary>The violations of <see cref="CheckLaunch"/>: empty means the arguments are admitted.</summary>
    public static IReadOnlyList<string> LaunchContractViolations(IReadOnlyList<string> args, string ahHome, IFileReader files) =>
        CodexLaunch.LaunchContractViolations(args, ahHome, files);

    /// <summary>The arguments that start a member's own app-server, with every policy pinned on its argv.</summary>
    public static IReadOnlyList<string> ServerArgs(string sock, LaunchCheck? launch, string tmp, string? memberName = null) =>
        CodexLaunch.ServerArgs(sock, launch, tmp, memberName);

    /// <summary>The <c>codex</c> arguments that attach a terminal UI to a member's app-server socket.</summary>
    public static IReadOnlyList<string> RemoteArgs(string sock) => CodexLaunch.RemoteArgs(sock);

    /// <summary>The terminal UI's whole argument list: resume the spawn's thread against the member's server.</summary>
    public static IReadOnlyList<string> TuiArgs(string threadId, string sock) => CodexLaunch.TuiArgs(threadId, sock);

    /// <summary>The socket a member's own app-server listens on: <c>&lt;ahHome&gt;/run/codex/&lt;team&gt;-&lt;name&gt;.sock</c>.</summary>
    public static string AppserverSocket(string ahHome, string team, string name) => CodexLaunch.AppserverSocket(ahHome, team, name);

    /// <summary>A member's temp directory: <c>&lt;ahHome&gt;/run/codex/tmp/&lt;team&gt;-&lt;name&gt;</c>.</summary>
    public static string MemberTmpDir(string ahHome, string team, string name) => CodexLaunch.MemberTmpDir(ahHome, team, name);

    /// <summary>Creates a member's temp directory (0700) and returns it, or null when it cannot be made or is not safe.</summary>
    public static string? EnsureMemberTmp(string dir, string ahHome, uint uid, IFileReader files, IRawFileWriter writer) =>
        CodexLaunch.EnsureMemberTmp(dir, ahHome, uid, files, writer);

    /// <summary>The ranges in force: the tested ones, or a test sandbox's override.</summary>
    public static IReadOnlyList<CodexRange> TestedRanges(IAhEnvironment environment) => CodexVersions.TestedRanges(environment);

    /// <summary><c>X.Y.Z[-pre]</c> out of <c>codex --version</c> text, or null.</summary>
    public static CodexVersion? ParseCodexVersion(string? text) => CodexVersions.ParseCodexVersion(text);

    /// <summary>Whether <c>codex --version</c> output is inside <paramref name="ranges"/>, with the note a refusal carries.</summary>
    public static AdapterGateResult AdapterGate(string? versionText, IReadOnlyList<CodexRange> ranges) =>
        CodexVersions.AdapterGate(versionText, ranges);

    /// <summary>Whether a codex member on the herdr transport is to get the adapter, from its roster <c>adapter</c> field.</summary>
    public static bool AdapterWanted(string? adapter, IReadOnlyList<CodexRange> ranges) => CodexVersions.AdapterWanted(adapter, ranges);

    /// <summary>Whether a team row is a codex member that runs on its own app-server.</summary>
    public static bool RowAdapted(JsonValue row) => CodexVersions.RowAdapted(row);

    /// <summary>The handle the answer route holds on the adapter job; pass the same instance to <see cref="Register"/>.</summary>
    public static ICodexRelay CreateRelay() => new CodexRelay();

    /// <summary>How often the adapter job runs.</summary>
    public static TimeSpan AdapterPeriod => TimeSpan.FromMilliseconds(CodexAdapter.PollMs);

    /// <summary>
    /// Registers the adapter job with the daemon: the composition root calls this once with the collaborators the adapter takes
    /// from the rest of ahd. The job holds its own adapter instance; nothing is shared between modules.
    /// </summary>
    public static void Register(IJobRegistry jobs, ICodexDependencies dependencies, ICodexRelay? relay = null)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        ArgumentNullException.ThrowIfNull(dependencies);
        // ponytail: a cast at one internal seam; split ICodexRelay into consumer and producer interfaces if a second relay appears.
        if (relay is not null and not CodexRelay)
        {
            throw new ArgumentException("relay must come from CodexModule.CreateRelay", nameof(relay));
        }

        var adapter = new CodexAdapter(dependencies);
        (relay as CodexRelay)?.Attach(adapter);
        jobs.Add("codex-adapter", AdapterPeriod, adapter.RunJobAsync);
    }

    private static readonly string[] ServerEnvironmentDeniedNames =
        ["CLAUDECODE", "CLAUDE_PID", "HERDR_PANE_ID", "HERDR_ENV", "TMUX", "TMUX_PANE"];

    private static readonly string[] ServerEnvironmentDeniedPrefixes = ["CLAUDE_CODE_", "AH_", "AHD_", "WRANGL_"];

    private static readonly string[] ServerEnvironmentKeptNames = ["AH_HOME", "AH_PLUGIN_ROOT"];

    /// <summary>
    /// The environment a member's detached app-server starts with: what the launching process had, without the variables that
    /// describe the launching session. A server is a daemon of its own; it must not carry the caller's Claude session markers, ah's
    /// own identity and test settings (<c>AH_*</c>, <c>WRANGL_*</c> and <c>AHD_*</c>, except <c>AH_HOME</c> and <c>AH_PLUGIN_ROOT</c>, which locate the
    /// state and the plugin the member's own <c>ah</c> uses), or the caller's pane. Everything else, which Codex and the commands it
    /// runs may need (PATH, HOME, locale, credentials, proxy and certificate settings), passes through: this is a deny-list because
    /// that set is open-ended.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> ServerEnvironment(IReadOnlyDictionary<string, string?> inherited)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        var kept = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (name, value) in inherited)
        {
            var denied = ServerEnvironmentDeniedNames.Contains(name, StringComparer.Ordinal)
                || (ServerEnvironmentDeniedPrefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal))
                    && !ServerEnvironmentKeptNames.Contains(name, StringComparer.Ordinal));
            if (!denied)
            {
                kept[name] = value;
            }
        }

        return kept;
    }

    /// <summary>
    /// A spawn's conversation with the member's own server just started on <paramref name="socket"/>: connect, create the thread,
    /// gate it, run the warm-up turn and resume it, then close the connection. The note of a failure is one fixed phrase and
    /// carries nothing the server said; a thread id that is not a plain token is refused, because it goes into a command line.
    /// </summary>
    public static (bool Ok, string? ThreadId, string? Note) BringUp(string socket, CodexModes modes, string cwd)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(modes);
        ArgumentNullException.ThrowIfNull(cwd);
        return Task.Run(() => BringUpAsync(socket, modes, cwd)).GetAwaiter().GetResult();
    }

    private static async Task<(bool Ok, string? ThreadId, string? Note)> BringUpAsync(string socket, CodexModes modes, string cwd)
    {
        const string startFailed = CodexProtocol.BringUpNotes.Start;
        var client = new AppServerClient(socket);
        try
        {
            await client.OpenAsync().ConfigureAwait(false);
            var result = await CodexProtocol.BringUpThreadAsync(client, cwd, modes).ConfigureAwait(false);
            if (result.Ok && (result.ThreadId is null || !PlainToken().IsMatch(result.ThreadId)))
            {
                return (false, null, startFailed);
            }

            return (result.Ok, result.ThreadId, result.Note);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (false, null, startFailed);
        }
        finally
        {
            client.Close();
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainToken();

    /// <summary>
    /// The sandbox and approval values ah pinned for a member row: what its <c>autoMode</c> mapping (<paramref name="kindAutoModeArgs"/>,
    /// from the roster) and its own <c>args</c> set, else the defaults.
    /// </summary>
    public static CodexModes ModesOfRow(JsonValue row, IReadOnlyList<string>? kindAutoModeArgs, string ahHome, IFileReader files)
    {
        var args = new List<string>(kindAutoModeArgs ?? []);
        if (row.Member("args") is { IsArray: true } own)
        {
            args.AddRange(own.AsArray().Where(a => a.IsText).Select(a => a.AsString()));
        }

        return CodexLaunch.LaunchModes(CodexLaunch.CheckLaunch(args, ahHome, files));
    }
}
