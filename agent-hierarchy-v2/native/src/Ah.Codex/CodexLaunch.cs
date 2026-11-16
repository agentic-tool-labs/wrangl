using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Codex;

/// <summary>The sandbox and approval values a member's server runs under.</summary>
public sealed record CodexModes(string Sandbox, string Approval);

/// <summary>One model or <c>-c</c> setting a checked launch passes on to the server's argv.</summary>
public sealed record LaunchSetting(string Key, string Value);

/// <summary>
/// What <c>CheckLaunch</c> found: the violations (labels that never carry a user-written value), the sandbox and approval
/// values the arguments set (null when none), the <c>--add-dir</c> roots and the settings, in order.
/// </summary>
public sealed record LaunchCheck(
    IReadOnlyList<string> Violations,
    string? Sandbox,
    string? Approval,
    IReadOnlyList<string> Roots,
    IReadOnlyList<LaunchSetting> Settings);

/// <summary>The launch-side rules of the Codex adapter: the arguments of a member's own app-server and the allow-list its launch arguments are checked against.</summary>
internal static class CodexLaunch
{
    private const string MemberMarker = Ah.Mesh.MeshModule.MemberMarkerEnv;

    public const string DefaultSandbox = "workspace-write";
    public const string DefaultApproval = "on-request";

    private static readonly string[] SandboxModes = ["workspace-write", "read-only"];
    private static readonly string[] ApprovalModes = ["on-request", "never"];
    private static readonly string[] ConfigKeys = ["model_instructions_file", "approvals_reviewer", "model_reasoning_effort"];

    /// <summary>The values a checked launch runs under: what it set, else the defaults.</summary>
    public static CodexModes LaunchModes(LaunchCheck? launch) => new(
        string.IsNullOrEmpty(launch?.Sandbox) ? DefaultSandbox : launch.Sandbox,
        string.IsNullOrEmpty(launch?.Approval) ? DefaultApproval : launch.Approval);

    /// <summary>The <c>codex</c> arguments that attach a terminal UI to a member's app-server socket.</summary>
    public static IReadOnlyList<string> RemoteArgs(string sock) => ["--remote", $"unix://{sock}"];

    /// <summary>The terminal UI's whole argument list: it resumes the thread the spawn created and attaches to the member's server.</summary>
    public static IReadOnlyList<string> TuiArgs(string threadId, string sock) => ["resume", threadId, .. RemoteArgs(sock)];

    private static string MemberKey(string team, string name) => $"{team}-{name}";

    /// <summary>The socket a member's own app-server listens on: <c>&lt;ahHome&gt;/run/codex/&lt;team&gt;-&lt;name&gt;.sock</c>.</summary>
    public static string AppserverSocket(string ahHome, string team, string name) =>
        NodePath.Join(ahHome, "run", "codex", $"{MemberKey(team, name)}.sock");

    /// <summary>The directory a member's commands use for temporary files: <c>&lt;ahHome&gt;/run/codex/tmp/&lt;team&gt;-&lt;name&gt;</c>.</summary>
    public static string MemberTmpDir(string ahHome, string team, string name) =>
        NodePath.Join(ahHome, "run", "codex", "tmp", MemberKey(team, name));

    /// <summary>
    /// Creates a member's temp directory (mode 0700) and returns its path, or null when it cannot be made or is not safe: the
    /// path must be in its normal form and lie under <paramref name="ahHome"/>, no component below it may be a symlink, and what
    /// stands at the end must be a real directory owned by <paramref name="uid"/>. Never removes anything.
    /// </summary>
    public static string? EnsureMemberTmp(string dir, string ahHome, uint uid, IFileReader files, IRawFileWriter writer)
    {
        try
        {
            var rel = NodePath.Relative(ahHome, dir);
            if (rel.Length == 0 || rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || NodePath.IsAbsolute(rel))
            {
                return null;
            }

            var norm = NodePath.Join(ahHome, rel);
            if (dir != norm)
            {
                return null;
            }

            var current = ahHome;
            var missing = false;
            foreach (var part in rel.Split('/'))
            {
                current = NodePath.Join(current, part);
                var stat = files.Stat(current, followSymlinks: false);
                if (stat is null)
                {
                    missing = true;
                    break;
                }

                if (stat.Value.Kind == FileKind.Symlink)
                {
                    return null;
                }
            }

            if (missing)
            {
                CreateDirectories(norm, ahHome, files, writer);
            }

            var end = files.Stat(norm, followSymlinks: false);
            return end is { Kind: FileKind.Directory } found && found.Uid == uid ? norm : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    private static void CreateDirectories(string dir, string ahHome, IFileReader files, IRawFileWriter writer)
    {
        var rel = NodePath.Relative(ahHome, dir);
        var current = ahHome;
        foreach (var part in rel.Split('/'))
        {
            current = NodePath.Join(current, part);
            if (files.Stat(current, followSymlinks: false) is null)
            {
                writer.Mkdir(current, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
    }

    private static string Quote(string value) => JsonText.Quote(value);

    /// <summary>
    /// The arguments that start a member's own app-server. All policy and every thread setting live here, because the server
    /// creates the thread: the sandbox mode, no network from the sandbox, the writable roots, the approval policy, the exclusion of
    /// the system temp locations with <c>TMPDIR</c> pointed at <paramref name="tmp"/>, then the checked list's model and
    /// <c>-c</c> settings. They go before the subcommand.
    /// </summary>
    public static IReadOnlyList<string> ServerArgs(string sock, LaunchCheck? launch, string tmp, string? memberName = null)
    {
        if (!NodePath.IsAbsolute(tmp))
        {
            throw new ArgumentException("serverArgs needs the member's temp directory as an absolute path", nameof(tmp));
        }

        var modes = LaunchModes(launch);
        var roots = new List<string> { Quote(tmp) };
        roots.AddRange((launch?.Roots ?? []).Select(Quote));
        var args = new List<string>
        {
            "-c", $"sandbox_mode={Quote(modes.Sandbox)}",
            "-c", "sandbox_workspace_write.network_access=false",
            "-c", $"sandbox_workspace_write.writable_roots=[{string.Join(',', roots)}]",
            "-c", $"approval_policy={Quote(modes.Approval)}",
            "-c", "sandbox_workspace_write.exclude_slash_tmp=true",
            "-c", "sandbox_workspace_write.exclude_tmpdir_env_var=true",
            "-c", $"shell_environment_policy.set.TMPDIR={Quote(tmp)}",
        };
        if (memberName is { Length: > 0 })
        {
            args.Add("-c");
            args.Add($"shell_environment_policy.set.{MemberMarker}={Quote(memberName)}");
        }

        foreach (var setting in launch?.Settings ?? [])
        {
            args.Add("-c");
            args.Add(setting.Key == "model" ? $"model={Quote(setting.Value)}" : $"{setting.Key}={setting.Value}");
        }

        args.Add("app-server");
        args.Add("--listen");
        args.Add($"unix://{sock}");
        return args;
    }

    private static bool Related(string p, string q)
    {
        static bool Within(string a, string b)
        {
            var r = NodePath.Relative(a, b);
            return r.Length == 0 || (!r.StartsWith("..", StringComparison.Ordinal) && !NodePath.IsAbsolute(r));
        }

        return Within(p, q) || Within(q, p);
    }

    private static string RealOrSelf(string path, IFileReader files)
    {
        try
        {
            return files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return path;
        }
    }

    /// <summary>Whether <paramref name="dir"/> is an absolute path unrelated to <paramref name="ahHome"/>: not it, not under it, not an ancestor of it, compared lexically and by real path.</summary>
    private static bool AddDirOk(string dir, string ahHome, IFileReader files)
    {
        if (!NodePath.IsAbsolute(dir))
        {
            return false;
        }

        string[] Forms(string p)
        {
            var resolved = NodePath.Resolve(p);
            return [resolved, RealOrSelf(resolved, files)];
        }

        var home = Forms(ahHome);
        return !Forms(dir).Any(p => home.Any(q => Related(p, q)));
    }

    /// <summary>JavaScript's <c>String.prototype.trim</c> whitespace set.</summary>
    private static bool IsJsSpace(char c) => c is '\t' or '\n' or '\v' or '\f' or '\r' or ' '
        || (int)c is 0xA0 or 0x1680 or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF;

    private static string JsTrim
(string value)
    {
        var start = 0;
        var end = value.Length;
        while (start < end && IsJsSpace(value[start]))
        {
            start++;
        }

        while (end > start && IsJsSpace(value[end - 1]))
        {
            end--;
        }

        return value[start..end];
    }

    private static bool IsConfigKey(string key)
    {
        if (key.Length == 0)
        {
            return false;
        }

        foreach (var c in key)
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private static string? KnownKind(string flag) => flag switch
    {
        "--model" or "-m" => "model",
        "--sandbox" or "-s" => "sandbox",
        "--ask-for-approval" or "-a" => "approval",
        "--add-dir" => "dir",
        "--config" or "-c" => "config",
        _ => null,
    };

    /// <summary>
    /// Checks the launch arguments of a member that asked for the app-server adapter against the allow-list: each token must be
    /// one of <c>--model/-m</c>, <c>--sandbox/-s</c> (<c>workspace-write</c> or <c>read-only</c>), <c>--ask-for-approval/-a</c>
    /// (<c>on-request</c> or <c>never</c>), <c>--add-dir</c> (an absolute path unrelated to <paramref name="ahHome"/>) and
    /// <c>-c/--config</c> for an allowed key. Reads no environment.
    /// </summary>
    public static LaunchCheck CheckLaunch(IReadOnlyList<string> args, string ahHome, IFileReader files)
    {
        var violations = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? sandbox = null;
        string? approval = null;
        var roots = new List<string>();
        var settings = new List<LaunchSetting>();
        for (var i = 0; i < args.Count; i++)
        {
            var tok = args[i];
            if (!tok.StartsWith('-') || tok == "-")
            {
                violations.Add($"positional argument {i + 1}");
                continue;
            }

            var isLong = tok.StartsWith("--", StringComparison.Ordinal);
            var eq = tok.IndexOf('=', StringComparison.Ordinal);
            var flag = !isLong && tok.Length > 2 ? $"{tok[..2]} (attached value)" : eq < 0 ? tok : tok[..eq];
            string? value = null;
            var separate = false;
            if (eq >= 0 && isLong)
            {
                value = tok[(eq + 1)..];
            }
            else if (eq < 0)
            {
                var next = i + 1 < args.Count ? args[i + 1] : null;
                value = next is not null && !next.StartsWith('-') ? next : null;
                separate = value is not null;
            }

            var known = KnownKind(flag);
            var knownForm = known is not null && (isLong || eq < 0);
            if (!knownForm)
            {
                violations.Add(flag);
                if (separate)
                {
                    i++;
                }

                continue;
            }

            if (separate)
            {
                i++;
            }

            if (known == "config")
            {
                var at = value?.IndexOf('=', StringComparison.Ordinal) ?? -1;
                var key = at < 0 ? null : JsTrim(value![..at]);
                if (key is null)
                {
                    violations.Add("-c");
                }
                else if (!IsConfigKey(key) || !ConfigKeys.Contains(key))
                {
                    violations.Add($"-c {key}");
                }
                else
                {
                    settings.Add(new LaunchSetting(key, value![(at + 1)..]));
                }

                continue;
            }

            var good = value is { Length: > 0 }
                && !value.StartsWith('-')
                && (known == "model"
                    || (known == "sandbox" && SandboxModes.Contains(value))
                    || (known == "approval" && ApprovalModes.Contains(value))
                    || (known == "dir" && AddDirOk(value, ahHome, files)));
            if (!good)
            {
                violations.Add($"{flag} (bad value)");
            }
            else if (known != "dir" && seen.Contains(known!))
            {
                violations.Add($"{flag} (repeated)");
            }
            else
            {
                if (known != "dir")
                {
                    seen.Add(known!);
                }

                switch (known)
                {
                    case "sandbox":
                        sandbox = value;
                        break;
                    case "approval":
                        approval = value;
                        break;
                    case "dir":
                        roots.Add(value!);
                        break;
                    case "model":
                        settings.Add(new LaunchSetting("model", value!));
                        break;
                }
            }
        }

        return new LaunchCheck(violations, sandbox, approval, roots, settings);
    }

    /// <summary>The violations of <see cref="CheckLaunch"/>: empty means the launch arguments are admitted.</summary>
    public static IReadOnlyList<string> LaunchContractViolations(IReadOnlyList<string> args, string ahHome, IFileReader files) =>
        CheckLaunch(args, ahHome, files).Violations;
}
