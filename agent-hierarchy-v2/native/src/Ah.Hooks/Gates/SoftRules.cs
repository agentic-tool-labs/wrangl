using Ah.Abstractions;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// The soft-gate rule rows: each a trigger of a few lines and its bounce through <see cref="SoftGate.Bounce"/>. A rule answers null when
/// its trigger did not fire; the first rule that fires decides. Any error is a silent pass.
/// </summary>
[HookIdentity(HookIdentityKind.RestrictOnly)]
internal static partial class SoftRules
{
    // Conduit must stay first: the unit's StaysHard (ConduitFires) assumes a soft-rules deny of a conduit call is conduit's.
    internal static readonly Func<GateContext, HookOutcome?>[] Rows = [Conduit, MsgsFiles, ReviewerWrite, SelfCare, RosterSkill, HerdrName, UltraAdvisor, Worktree];

    public static HookOutcome Run(GateContext c)
    {
        try
        {
            foreach (var rule in Rows)
            {
                if (rule(c) is { } outcome)
                {
                    return outcome;
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.LogError(e);
        }

        return HookEmit.Silent;
    }

    private static string? Role(GateContext c) => c.Services.Config.ResolveHierarchyRole(c.Input).Role;

    private static bool IsMember(GateContext c) => c.Services.Config.IsMemberSession || Role(c) is not null;

    /// <summary>
    /// Whether conduit's trigger fires: one of the four user-facing tools, called by anyone but the lead. Conduit is the first rule, so
    /// a soft-rules deny of such a call is conduit's; the unit's <c>StaysHard</c>, so the chain of command is never advisory.
    /// </summary>
    internal static bool ConduitFires(GateContext c) =>
        c.ToolName() is "AskUserQuestion" or "ExitPlanMode" or "SendUserFile" or "PushNotification"
        && (c.Services.Config.IsSubagent(c.Input) || IsMember(c));

    private static HookOutcome? Conduit(GateContext c)
    {
        if (!ConduitFires(c))
        {
            return null;
        }

        return SoftGate.Bounce(
            c,
            "conduit",
            "You never reach the user.",
            "Send this to your parent: a `note <request id>: …` with your default, or `open_questions` in your report. Take the default and keep working. Do not retry this tool.",
            retryPasses: false,
            noGiveup: true);
    }

    /// <summary>A file tool on a path in a pool's message directory, when messages live in the store: what is there is no message, so the tool is told the command.</summary>
    private static HookOutcome? MsgsFiles(GateContext c)
    {
        if (c.ToolName() is not ("Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit")
            || (c.ToolInput().Get("file_path") ?? c.ToolInput().Get("notebook_path")) is not { IsText: true } path
            || !InPoolMsgs(c, path.AsString()))
        {
            return null;
        }

        var id = c.Services.Hierarchy.Core.ParseMsgFilename(Path.GetFileName(path.AsString()))?.Id ?? "<id>";
        return SoftGate.Bounce(
            c,
            "msgs-files",
            "messages live in ahd, not in files.",
            $"read with `ah msg show {id} [response]`; write with `ah msg fill --id {id} --from <role> --body-file -`.",
            retryPasses: false,
            noGiveup: true);
    }

    /// <summary>Whether <paramref name="path"/> is directly in <c>msgs/</c> or <c>msgs/archive/</c> of a hierarchy directory: a <c>.claude/ah2</c> one, or the session's own.</summary>
    private static bool InPoolMsgs(GateContext c, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path, c.Cwd()));
        if (dir is not null && Path.GetFileName(dir) == "archive")
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null || Path.GetFileName(dir) != "msgs" || Path.GetDirectoryName(dir) is not { } pool)
        {
            return false;
        }

        return pool.EndsWith("/.claude/ah2", StringComparison.Ordinal) || pool == c.Services.Config.HierarchyDir(c.Cwd());
    }

    private static HookOutcome? ReviewerWrite(GateContext c)
    {
        if (c.ToolName() is not ("Write" or "Edit" or "MultiEdit" or "NotebookEdit")
            || (c.ToolInput().Get("file_path") ?? c.ToolInput().Get("notebook_path")) is not { IsText: true } path
            || Role(c) != "reviewer")
        {
            return null;
        }

        return SoftGate.Bounce(
            c,
            "reviewer-write",
            "the Reviewer writes only its own response.",
            "write your findings with `ah msg fill --id <id> --from reviewer --body-file -`.",
            retryPasses: false);
    }

    /// <summary>The simple commands of the Bash call; null for another tool or a line the lexer cannot classify.</summary>
    private static IReadOnlyList<ShellCommand>? Shell(GateContext c) =>
        c.ToolName() == "Bash" && c.Command() is { } command ? ShellScan.Commands(command) : null;

    private static bool IsAh(IReadOnlyList<string> argv, string group) =>
        argv.Count > 2 && Path.GetFileName(argv[0]) == "ah" && argv[1] == group;

    private static readonly string[] SelfCareRoles = ["architect", "reviewer", "ultra-advisor"];

    // The legwork runners a self-care role delegates to: their subagent calls fall back to the session's role in Role(c); exempt them here only.
    private static readonly string[] LegworkRunners = ["task-gopher:task-gopher", "task-gopher:smart-gopher", "wrangl:task-runner"];

    private static bool IsLegworkRunner(GateContext c) =>
        c.Services.Config.IsSubagent(c.Input) && c.Input.Member("agent_type")?.NonEmptyText is { } agentType && LegworkRunners.Contains(agentType);

    internal const string ArchitectNudge =
        "ah: fetching and research go to a gopher (task-gopher:task-gopher, or task-gopher:smart-gopher when it needs judgment); read directly only when you need the context yourself.";

    private static HookOutcome? SelfCare(GateContext c)
    {
        if (Shell(c) is not { } commands || Role(c) is not { } role || !SelfCareRoles.Contains(role)
            || IsLegworkRunner(c)
            || (commands is [{ Argv: var argv }] && (SelfCareCall(argv) || (role == "architect" && SpecCommit(argv)))))
        {
            return null;
        }

        // Amendment 1: the architect is nudged once per session, never denied.
        if (role == "architect")
        {
            return SoftGate.Once(c, "self-care", ArchitectNudge);
        }

        return SoftGate.Bounce(
            c,
            "self-care",
            $"the {role} runs only ah self-care in the shell (`ah roster checkin|whoami|status`, `ah msg new|list|index|show|send|fill|status|link-request`, `ah work list|show|log|mine|record|attach|add`, `ah adr list|show`, read-only `git diff|status|show|log|grep`), one call at a time.",
            commands.Any(static command => BriefRead(command.Argv))
                ? "run `ah msg index` or `ah msg show` as a call of its own, with nothing chained to it."
                : "delegate this to `task-gopher:task-gopher` (or `wrangl:task-runner`) with WHERE / HOW / BACK.",
            retryPasses: false);
    }

    private static bool BriefRead(IReadOnlyList<string> argv) => IsAh(argv, "msg") && argv[2] is "index" or "show" && !ShowWrites(argv);

    // `ah msg show --out <path>` writes a file; a role that only runs self-care does not get to write one.
    private static bool ShowWrites(IReadOnlyList<string> argv) =>
        argv[2] == "show" && argv.Any(static a => a == "--out" || a.StartsWith("--out=", StringComparison.Ordinal));

    private static bool SelfCareCall(IReadOnlyList<string> argv) =>
        (IsAh(argv, "roster") && argv[2] is "checkin" or "whoami" or "status")
        || (IsAh(argv, "work") && argv[2] is "list" or "show" or "log" or "mine" or "record" or "attach" or "add")
        || (IsAh(argv, "msg") && argv[2] is "new" or "list" or "index" or "show" or "send" or "fill" or "status" or "link-request" && !ShowWrites(argv))
        || (IsAh(argv, "adr") && argv[2] is "list" or "show")
        || (argv is ["git", ..] && (argv.Count > 3 && argv[1] == "-C" ? argv[3] : argv.Count > 1 ? argv[1] : null) is "diff" or "status" or "show" or "log" or "grep");

    // Q5: the Architect may stage and commit its own spec files, `git [-C <dir>] add|commit … docs/specs/*.md`.
    private static bool SpecCommit(IReadOnlyList<string> argv)
    {
        var i = argv.Count > 3 && argv[0] == "git" && argv[1] == "-C" ? 3 : 1;
        if (argv.Count <= i || argv[0] != "git" || argv[i] is not ("add" or "commit"))
        {
            return false;
        }

        var paths = new List<string>();
        for (i++; i < argv.Count; i++)
        {
            if (argv[i] is "-m" or "-F" or "--message" or "--file" || (argv[i].Length > 2 && argv[i][0] == '-' && argv[i][1] != '-' && argv[i][^1] is 'm' or 'F'))
            {
                i++;
            }
            else if (!argv[i].StartsWith('-'))
            {
                paths.Add(argv[i]);
            }
        }

        return (paths.Count > 0 || argv.Contains("commit")) && paths.All(p => p.Contains("docs/specs/", StringComparison.Ordinal) && p.EndsWith(".md", StringComparison.Ordinal));
    }

    private static readonly string[] TeamVerbs = ["create", "adopt", "move", "dismiss", "disband", "untrack"];

    private static HookOutcome? RosterSkill(GateContext c)
    {
        if (c.ToolName() == "Skill")
        {
            if (c.ToolInput().Get("skill") is { IsText: true } skill && skill.AsString().EndsWith("agent-team", StringComparison.Ordinal))
            {
                SoftGate.Note(c, c.Services.Config.HierarchyDir(c.Cwd()), new Core.Json.JsonObject { ["rule"] = "roster-skill", ["outcome"] = "skill-loaded" });
            }

            return null;
        }

        if (Shell(c) is not { } commands
            || !commands.Any(cmd => IsAh(cmd.Argv, "roster") && TeamVerbs.Contains(cmd.Argv[2]) && !cmd.Argv.Any(a => a is "--plan" or "--dry-run"))
            || c.Services.Config.IsSubagent(c.Input) || IsMember(c))
        {
            return null;
        }

        var session = c.SessionKey();
        if (SoftGate.SinceReset(c, SoftGate.Rows(c, c.Services.Config.HierarchyDir(c.Cwd())))
            .Any(r => r.Text("outcome") == "skill-loaded" && r.Text("session_id") == session))
        {
            return null;
        }

        return SoftGate.Bounce(c, "roster-skill", "team lifecycle runs through the agent-team skill, which is not loaded.", "load it (Skill `wrangl:agent-team`), then re-run this command.", retryPasses: true, SoftScope.Session);
    }

    private static HookOutcome? HerdrName(GateContext c)
    {
        if (c.ToolName() != "Bash" || c.Command() is not { } command || !command.Contains("herdr", StringComparison.Ordinal)
            || !Ah.Hooks.Lifecycle.OrchestratorIdentity.In(c))
        {
            return null;
        }

        var config = c.Services.Config;
        if (HerdrCalls.Find(command, "agent", ["start"]).FirstOrDefault(call => config.ValidateHerdrName(Core.Json.JsonValue.FromString(call.Name)).Get("ok") is not { IsTruthy: true }) is { } bad)
        {
            return SoftGate.Bounce(
                c,
                "herdr-name",
                $"`{bad.Name}` is not a herdr name: lowercase letter first, then `[a-z0-9_-]`, 1-32 chars.",
                $"re-run `herdr agent start <valid name>{(bad.Pane is null ? string.Empty : " --pane " + bad.Pane)} …` in the same pane.",
                retryPasses: true,
                SoftScope.Session);
        }

        var dir = config.HierarchyDir(c.Cwd());
        foreach (var call in HerdrCalls.InputCalls(command))
        {
            var team = c.Services.Roster.ResolveMemberTeam(dir, call.Name);
            var member = team.Found ? c.Services.Roster.TeamMemberByName(dir, call.Name, team.Team) : Core.Json.JsonValue.Null;
            if (config.IsPaneMember(member))
            {
                return SoftGate.Bounce(
                    c,
                    "herdr-name",
                    $"`{call.Name}` is a {member.Text("kind") ?? "pane"} member, and raw herdr input bypasses its mailbox.",
                    $"brief it with `ah roster deliver {call.Name} --req <path> --cwd {c.Cwd()}`.",
                    retryPasses: true,
                    SoftScope.Session);
            }
        }

        return null;
    }

    private static HookOutcome? Worktree(GateContext c)
    {
        List<ShellCommand> switches = Shell(c) is { } commands
            ? [.. commands.Where(cmd => !cmd.Argv.Contains("--") && GitSubcommand(cmd.Argv) is "switch" or "checkout")]
            : [];
        var asked = c.ToolName() == "EnterWorktree" || switches.Count > 0;
        if (!asked || !IsMember(c) || (switches.Count > 0 && IsLegworkRunner(c) && OutsideWorkTree(c, switches)))
        {
            return null;
        }

        // The member is the roster's `up` record of this session; without one no approval matches and the loop breaker still applies.
        var member = c.Services.Hierarchy.Roster.UpRecordFor(c.Services.Config.HierarchyDir(c.Cwd()), c.SessionKey()).Member("name") is { IsText: true } name ? name.AsString() : null;
        return SoftGate.Bounce(
            c,
            "worktree",
            "switching this member's tree or branch is the Orchestrator's call.",
            $"report BLOCKED with the reason (or put it in `open_questions`); once your Orchestrator runs `ah roster approve {member ?? "<your member name>"} worktree --cwd <cwd>`, re-run this call.",
            retryPasses: false,
            SoftScope.Input,
            member);
    }

    /// <summary>
    /// The git subcommand <paramref name="argv"/> runs, or null when it is not git: through <c>env [options] [NAME=value]…</c>, git named
    /// by a path, and git's own options before the subcommand (<c>-C dir</c>, <c>-c k=v</c>, <c>--git-dir</c>, <c>--work-tree</c> …).
    /// A <c>sh -c</c> string needs nothing here: <see cref="ShellScan.Commands"/> already lists the commands inside it.
    /// </summary>
    internal static string? GitSubcommand(IReadOnlyList<string> argv)
    {
        var i = 0;
        if (argv.Count > 0 && Path.GetFileName(argv[0]) == "env")
        {
            for (i = 1; i < argv.Count && (argv[i].StartsWith('-') || argv[i].Contains('=')); i++)
            {
                if (argv[i] is "-u" or "--unset" or "-C" or "--chdir" or "-S" or "--split-string")
                {
                    i++;
                }
            }
        }

        if (i >= argv.Count || Path.GetFileName(argv[i]) != "git")
        {
            return null;
        }

        for (i++; i < argv.Count && argv[i].StartsWith('-'); i++)
        {
            if (argv[i] is "-C" or "-c" or "--git-dir" or "--work-tree" or "--namespace" or "--config-env" or "--super-prefix" or "--exec-path")
            {
                i++;
            }
        }

        return i < argv.Count ? argv[i] : null;
    }

    private static readonly System.Buffers.SearchValues<char> ExpandingChars = System.Buffers.SearchValues.Create("$`*?[{~");

    /// <summary>
    /// b474: whether every switch carries <c>-C &lt;absolute dir&gt;</c> whose real path lies outside the member's work tree (the checkout at or
    /// above the cwd, else <c>WRANGL_EXPECTED_ROOT</c>). False, so the switch bounces, when the root or a dir cannot be resolved, or a dir is
    /// relative or holds text the shell expands.
    /// </summary>
    private static bool OutsideWorkTree(GateContext c, List<ShellCommand> switches)
    {
        var root = Ah.Hierarchy.HierarchyModule.CheckoutOfCwd(c.Cwd(), p => c.Services.Files.Stat(p) is not null)
            ?? c.Services.Environment.GetVariable("WRANGL_EXPECTED_ROOT");
        // A GIT_* variable anywhere in the line (a prefix, export or env) can point git's work tree back into the member's tree.
        if (string.IsNullOrEmpty(root) || !Path.IsPathRooted(root) || Ah.Core.Text.PathGuard.RealPath(root, strict: true) is null
            || (c.Command() ?? string.Empty).Contains("GIT_", StringComparison.Ordinal))
        {
            return false;
        }

        // Only the plain form `git -C <dir> switch|checkout …` can be judged: any other option before the subcommand may move the tree.
        return switches.All(cmd => cmd.Assignments.Count == 0 && cmd.Argv.Count > 3 && cmd.Argv[1] == "-C" && cmd.Argv[3] is "switch" or "checkout"
            && cmd.Argv[2] is { Length: > 0 } dir
            && Path.IsPathRooted(dir) && !dir.AsSpan().ContainsAny(ExpandingChars)
            && Ah.Core.Text.PathGuard.RealPath(dir, strict: true) is { } real
            && !Ah.Core.Text.PathGuard.Within(root, real)
            && PlainClone(c, real));
    }

    /// <summary>
    /// Whether <paramref name="dir"/>/.git is a real directory (no gitfile, no link) whose config names no <c>worktree</c> key and no
    /// <c>include</c>, read as a file with no git process: either could send the checkout's writes into another tree. An unreadable config is false.
    /// </summary>
    private static bool PlainClone(GateContext c, string dir)
    {
        var git = Path.Join(dir, ".git");
        try
        {
            // commondir and config.worktree hand git another config, which could carry core.worktree.
            if (c.Services.Files.Stat(git, followSymlinks: false) is not { Kind: FileKind.Directory }
                || c.Services.Files.Stat(Path.Join(git, "commondir"), followSymlinks: false) is not null
                || c.Services.Files.Stat(Path.Join(git, "config.worktree"), followSymlinks: false) is not null)
            {
                return false;
            }

            var config = c.Services.Files.ReadText(Path.Join(git, "config"));
            return !config.Contains("worktree", StringComparison.OrdinalIgnoreCase) && !config.Contains("include", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Run turns an escaped error into a silent pass, so a failed read must bounce here.
            return false;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^[ \t]*(?:[-*][ \t]+)?ultra-advisor:[ \t]*no\b", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex UltraNo();

    // Q3: no Ultra-Advisor spawn or dispatch while the open run anchor says `ultra-advisor: no`; no anchor, or `yes`, passes.
    private static HookOutcome? UltraAdvisor(GateContext c)
    {
        var asked = c.ToolName() is "Agent" or "Task"
            ? c.ToolInput().Get("subagent_type") is { IsText: true } t && (t.AsString() == "ultra-advisor" || t.AsString().EndsWith(":ultra-advisor", StringComparison.Ordinal))
            : Shell(c) is { } commands && commands.Any(cmd => IsAh(cmd.Argv, "roster") && cmd.Argv.Contains("ultra-advisor") && cmd.Argv.Any(a => a is "spawn-one" or "spawn-ad-hoc"));
        if (!asked
            || c.Services.Hierarchy.Decisions.OpenRunAnchor(c.Services.Config.HierarchyDir(c.Cwd()), null).Get("path") is not { IsText: true } anchor
            || c.Services.Hierarchy.Core.ReadMsgFile(anchor.AsString()).Member("text") is not { IsText: true } anchorText
            || !UltraNo().IsMatch(anchorText.AsString()))
        {
            return null;
        }

        return SoftGate.Bounce(c, "ultra-advisor", "this run's anchor says `ultra-advisor: no`.", "decide without the Ultra-Advisor, or ask the user to change the anchor.", retryPasses: true);
    }
}
