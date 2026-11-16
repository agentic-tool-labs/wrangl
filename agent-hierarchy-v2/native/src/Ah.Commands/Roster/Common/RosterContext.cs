using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Launch;

namespace Ah.Commands;

/// <summary>
/// One roster invocation: the parsed flags, the working directory, the team scope the shared pre-dispatch resolved, and what a
/// verb prints with. A verb handler reads from it and returns its exit code; it never reaches for a global.
/// </summary>
internal sealed partial class RosterContext
{
    private string _cwd;
    private string? _dir;
    private JsonObject? _registry;

    internal RosterContext(
        IRosterCliServices services, RosterArgs opts, string command, string cwd, TextWriter stdout, TextWriter stderr,
        IReadOnlyList<string>? argv = null)
    {
        Argv = argv ?? [];
        Services = services;
        Opts = opts;
        Command = command;
        _cwd = cwd;
        Stdout = stdout;
        Stderr = stderr;
    }

    internal IRosterCliServices Services { get; }

    internal RosterArgs Opts { get; }

    /// <summary>Set by the owner check when the invoker owns the parent of the lane child the command names and runs one of the parent-owned verbs there.</summary>
    internal Ah.Roster.LaneLeader? ParentOwnerLane { get; set; }

    /// <summary>Whether the lane rules bind this caller: a proven sub-orchestrator, or the owner of a lane child's parent acting on that child.</summary>
    internal bool LaneActor => ParentOwnerLane is not null || Proved().Carve is not null;

    /// <summary>The verb word, the first positional of the invocation.</summary>
    internal string Command { get; }

    /// <summary>The arguments after <c>roster</c> as given, the verb word first.</summary>
    internal IReadOnlyList<string> Argv { get; }

    /// <summary>The <c>--cwd</c> given, else the process's working directory.</summary>
    internal string Cwd
    {
        get => _cwd;
        set
        {
            _cwd = value;
            _dir = null;
            _registry = null;
        }
    }

    internal TextWriter Stdout { get; }

    internal TextWriter Stderr { get; }

    /// <summary>The validated <c>--team</c>; null when absent or when it named the default team (<c>@default</c>).</summary>
    internal string? TeamArg { get; set; }

    /// <summary><c>--team @default</c> was given: the default team, named as explicitly as any other.</summary>
    internal bool TeamDefaultExplicit { get; set; }

    /// <summary>Which team file this invocation reads and writes; null is the default team's.</summary>
    internal string? TeamFile { get; set; }

    /// <summary>No <c>--team</c> was given, so <see cref="TeamFile"/> was derived.</summary>
    internal bool TeamFileDefaulted { get; set; }

    /// <summary>Why the derived prefix cannot name a team file, or null.</summary>
    internal JsonValue TeamFileUnnamable { get; set; }

    /// <summary>The legacy team file that holds no record, or null.</summary>
    internal JsonValue TeamFileUnreadable { get; set; }

    internal string RepoBasename { get; set; } = string.Empty;

    /// <summary>The roster selection of a template verb or <c>create</c>; null for every other verb.</summary>
    internal JsonObject? Selection { get; set; }

    /// <summary>The <c>rosters.&lt;r&gt;</c> key the verb uses; null is the default block.</summary>
    internal string? RosterArg { get; set; }

    /// <summary>Stale config keys this invocation's writes migrated; the verb's output carries them.</summary>
    internal List<JsonValue> MigratedKeys { get; } = [];

    /// <summary>
    /// While true a refusal is only thrown, not printed: a command that runs another verb's work per item (a resume) reports each
    /// refusal in that item's row.
    /// </summary>
    internal bool TrapRefusals { get; set; }

    /// <summary>Whether this command has already waited for restored members to register; it waits once, however many teams it covers.</summary>
    internal bool Settled { get; set; }

    /// <summary>Whether task-gopher is installed, once asked (it is not asked for twice in one invocation).</summary>
    internal bool? TaskGopherInstalled { get; set; }

    /// <summary>The member an ad hoc spawn is about to add, so the team-name check sees every name it would give.</summary>
    internal JsonObject? AdHocForNameCheck { get; set; }

    /// <summary>The hierarchy directory of <see cref="Cwd"/>, or the one this context was pinned to.</summary>
    internal string Dir => _dir ??= Services.Config.HierarchyDir(Cwd);

    /// <summary>Makes <paramref name="dir"/> this invocation's hierarchy directory whatever <see cref="Cwd"/> resolves to.</summary>
    internal void PinDir(string dir) => _dir = dir;

    /// <summary>
    /// True only for the daemon's own in-process call of a single-member relaunch. No flag, environment variable or argument of a command
    /// line sets it, so a CLI caller cannot claim it.
    /// </summary>
    internal bool DaemonPrincipal { get; init; }

    /// <summary>
    /// How long one <c>quit</c> call may wait for members' answers, when the daemon runs it for the UI: herdr's fetch gives a request
    /// 30 s, so the UI's call must answer well inside that and be sent again. Null is the verb's own budget. No command line sets it.
    /// </summary>
    internal int? QuitCallBudgetSeconds { get; init; }

    /// <summary>
    /// True only for the daemon's check of a relaunch before it stops the member: the launch path runs as a dry-run for a member that is
    /// still live, so liveness does not end it before its refusals. Like <see cref="DaemonPrincipal"/> no command line sets it.
    /// </summary>
    internal bool PreflightStop { get; init; }

    /// <summary>Throws when <see cref="PreflightStop"/> is set on a context that is not a dry-run, so the mode can never reach a real launch.</summary>
    internal void RequirePreflightDryRun()
    {
        if (PreflightStop && !Opts.IsTrue("dry-run"))
        {
            throw new InvalidOperationException("internal error: the preflight mode needs dry-run");
        }
    }

    /// <summary>Under the daemon principal, the <c>expected_root</c> the member's row records: a launch for it must start there.</summary>
    internal string? PinnedRoot { get; set; }

    /// <summary>The directory that holds the team file and every grants record: the main checkout's hierarchy directory, never a work tree's own pool.</summary>
    internal string TeamHome => Services.Roster.TeamHome(Dir);

    /// <summary>The asks log a member's launch reads for consent: consent belongs to the project, so a linked worktree reads the main checkout's.</summary>
    internal string ConsentAsksPath => NodePath.Join(TeamHome, "asks.jsonl");

    /// <summary>The project config the member_allow switch is read from: the team's home's when <c>--team</c> pinned one in another repo, else <see cref="Cwd"/>'s.</summary>
    internal string? MemberAllowProjectConfig =>
        TeamHomeElsewhere is not null ? NodePath.Join(TeamHome, Services.Config.ConfigBasename) : Services.Config.ProjectConfigPath(Cwd);

    /// <summary>Set when a verb found the team's Orchestrator pid to be this caller's but could prove no session for it.</summary>
    internal bool OrchestratorSessionUnknown { get; set; }

    internal string? MainDir => Services.Config.MainHierarchyDir(Cwd);

    /// <summary>The team's folder name under <c>progress/</c>: its team file name, or <c>_default</c> for the default scope (no team name can start with <c>_</c>).</summary>
    internal string ProgressTeam => Ah.Hierarchy.HierarchyModule.ProgressTeamOf(TeamFile);

    /// <summary>Where the team's progress records live: the main checkout's hierarchy directory when this is a linked work tree, else <see cref="Dir"/>.</summary>
    internal string ProgressPool => Ah.Hierarchy.HierarchyModule.ProgressPoolOf(MainDir, Dir);

    /// <summary>The directories a team file may be found in: this one, then the main checkout's.</summary>
    internal IReadOnlyList<string> Homes => MainDir is { Length: > 0 } main ? [Dir, main] : [Dir];

    /// <summary>The environment variable, or null when unset.</summary>
    internal string? Env(string name) => Services.Environment.GetVariable(name);

    /// <summary>A non-empty environment variable (JavaScript <c>process.env.X || …</c>), or null.</summary>
    internal string? EnvText(string name) => Env(name) is { Length: > 0 } v ? v : null;

    /// <summary>Prints one JSON document, two-space indented, with a trailing newline.</summary>
    internal void Out(JsonValue value)
    {
        var payload = value;
        if (MigratedKeys.Count > 0 && value.IsMap)
        {
            var copy = value.AsObject().Clone();
            var migrated = new JsonArray();
            migrated.AddRange(MigratedKeys);
            copy["migrated"] = migrated;
            payload = copy;
        }

        var renderStart = Ah.Core.Time.PhaseTrace.Begin();
        Stdout.Write(JsonText.Stringify(payload, 2));
        Stdout.Write('\n');
        Ah.Core.Time.PhaseTrace.End("render", renderStart);
    }

    /// <summary>A usage error or refusal: throw the result.</summary>
    internal static RosterFailure Fail(string message) => new(message);

    /// <summary>
    /// The pid this session claims as its own: <c>--orchestrator-pid</c> wins, else <c>CLAUDE_PID</c>. NaN when neither is a
    /// number.
    /// </summary>
    internal double OrchestratorPid()
    {
        var raw = Opts.Text("orchestrator-pid") ?? Env("CLAUDE_PID");
        return raw is null ? double.NaN : JsConvert.ToNumber(JsonValue.FromString(raw));
    }

    internal static bool IsInteger(double value) => double.IsFinite(value) && Math.Floor(value) == value;

    internal bool PidAlive(double pid) => IsInteger(pid) && Services.Hierarchy.Core.PidAlive(JsonValue.FromNumber(pid));

    private bool? _userActs;

    /// <summary>
    /// The user at a plain terminal runs this <c>quit</c>, <c>pause</c> or <c>stop</c>: no Claude harness above the command and no
    /// member marker. The user is never refused, so the owner check is skipped and the folder's teams stand in for "the teams this
    /// session owns". A session, a member and the daemon's own call are judged as before.
    /// </summary>
    internal bool UserActs => _userActs ??= Command is "quit" or "pause" or "stop" && !DaemonPrincipal
        && EnvText(Ah.Mesh.MeshModule.MemberMarkerEnv) is null && Services.OwnerProof?.IsUser() == true;

    /// <summary>
    /// Who runs this command, for deciding which team it owns: the Orchestrator or Claude session the process ancestry proves, with
    /// its start time and session. No one when nothing is proved, the shell carries a member marker, or a stated pid disagrees.
    /// </summary>
    internal TeamInvoker Invoker()
    {
        var proved = Proved();
        return proved.Acts && StatedPidMismatch() is null
            ? new TeamInvoker(proved.Pid, proved.SessionId, proved.PidStart)
            : new TeamInvoker(null, null);
    }

    /// <summary>The pane this session sits in: the transport's own variable, then what SessionStart recorded, then tmux's.</summary>
    internal string? SessionPaneId(JsonValue? record)
    {
        if (Ah.Core.Mux.HerdrFlavour.SessionPaneId(EnvText) is { } herdr)
        {
            return herdr;
        }

        if (record is { IsMap: true } r && r.Member("pane_id") is { IsTruthy: true } pane && pane.IsText)
        {
            return pane.AsString();
        }

        return EnvText("TMUX_PANE");
    }

    /// <summary>
    /// The role registry for this invocation's cwd and team, read once; an unreadable configuration reads as an empty registry
    /// (no roles, no sources, no warnings), as the Node CLI does.
    /// </summary>
    internal JsonObject Registry => _registry ??= LoadRegistry();

    private JsonObject LoadRegistry()
    {
        try
        {
            return Services.Config.ResolveConfig(
                Cwd,
                TeamDefaultExplicit ? new ResolveOptions(DefaultTeam: true) : new ResolveOptions(Team: TeamArg));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new JsonObject
            {
                ["roles"] = new JsonObject(),
                ["sources"] = new JsonObject(),
                ["excludedRoles"] = new JsonArray(),
                ["warnings"] = new JsonArray(),
                ["cwd"] = Cwd,
            };
        }
    }

    /// <summary>The directory a launch runs in when it is not <see cref="Cwd"/> (a member of a stream launches in the stream's work tree); null otherwise.</summary>
    internal string? LaunchCwd { get; set; }

    /// <summary>What a launch is about: the working directory, the team file, the repo base name, the registry and where the member starts.</summary>
    internal LaunchContext LaunchContext() => new(Cwd, TeamFile, RepoBasename, Registry, Command, TeamArg, LaunchCwd, MemberTmp) { HierarchyDir = TeamHomeElsewhere };

    /// <summary>The hierarchy directory <c>--team</c> was pinned to because the owned team of that name is homed in another repo than <see cref="Cwd"/>'s; else null.</summary>
    internal string? TeamHomeElsewhere { get; set; }

    /// <summary>The stream a member being resumed rejoins; read where <c>--stream</c> is, so a resume needs no flag.</summary>
    internal string? StreamOverride { get; set; }

    /// <summary>The existing herdr pane a launch runs in instead of placing a new one; set only by the daemon's relaunch of a restored member.</summary>
    internal string? IntoPane { get; set; }

    /// <summary>The transport a launch uses instead of the one this process detects; set only by the daemon's relaunch, from the team's record, because the daemon's own environment describes the daemon and not the team.</summary>
    internal string? LaunchTransport { get; set; }

    /// <summary>The <c>TMPDIR</c> each member about to launch exports, by member name.</summary>
    internal Dictionary<string, string> MemberTmp { get; } = [];

    /// <summary>
    /// A refusal whose choice belongs to the user: the fields as JSON on stdout (<c>ok: false</c> first) for a caller that
    /// branches on <c>refused</c>, then the message as an ordinary failure (stderr, exit 2). Throw the result.
    /// </summary>
    internal RosterFailure Refuse(JsonObject fields, string message)
    {
        if (!TrapRefusals)
        {
            var o = new JsonObject { ["ok"] = false };
            RosterJson.Spread(o, fields);
            Stdout.Write(JsonText.Stringify(o, 2));
            Stdout.Write('\n');
        }

        return new RosterFailure(message, fields.Get("refused") is { IsText: true } code ? code.AsString() : null);
    }

    /// <summary>A warning line on stderr: <c>ah roster: warning — &lt;text&gt;</c>.</summary>
    internal void Warn(string text) => Stderr.Write($"ah roster: warning — {text}\n");

    /// <summary>The registrations the mailbox refused or that failed while a verb wrote its team, for the verb's output.</summary>
    internal List<string> RegistrationWarnings { get; } = [];

    /// <summary>The installed plugin's root, which the commands this CLI prints are rooted at.</summary>
    internal string PluginRoot => Services.Config.PluginRoot;

    /// <summary>The command line that runs this CLI, as the commands it prints name it.</summary>
    internal string SelfCommand => Services.Config.CliCommand(CliTool.Roster);

    /// <summary>One word of a printed command line: as it is when it is safe, else single-quoted for <c>/bin/sh</c>.</summary>
    internal static string ArgWord(string word) => SafeWord().IsMatch(word) ? word : ShQuote(word);

    internal static string ShQuote(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [GeneratedRegex("^[A-Za-z0-9_@%+=:,./-]+\\z")]
    private static partial Regex SafeWord();

    /// <summary>
    /// This invocation as a command line, without each <c>--flag [value]</c> that <paramref name="drop"/> selects, and with
    /// <paramref name="tail"/> appended as written.
    /// </summary>
    internal string RerunCommand(Func<string, string?, bool> drop, string tail)
    {
        var kept = new List<string>();
        for (var i = 0; i < Argv.Count; i++)
        {
            var next = i + 1 < Argv.Count && !Argv[i + 1].StartsWith("--", StringComparison.Ordinal) ? Argv[i + 1] : null;
            if (Argv[i].StartsWith("--", StringComparison.Ordinal) && drop(Argv[i][2..], next))
            {
                if (next is not null)
                {
                    i++;
                }

                continue;
            }

            kept.Add(Argv[i]);
        }

        return $"{SelfCommand} {string.Join(' ', kept.Select(ArgWord))} {tail}";
    }

    /// <summary>Every value of a repeatable <c>--flag value</c>, in order; the flag with no value is refused.</summary>
    internal List<string> RepeatedFlag(string flag)
    {
        var values = new List<string>();
        for (var i = 0; i < Argv.Count; i++)
        {
            if (Argv[i] != "--" + flag)
            {
                continue;
            }

            if (i + 1 >= Argv.Count || Argv[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new RosterFailure($"{Command}: --{flag} requires a value");
            }

            values.Add(Argv[i + 1]);
            i++;
        }

        return values;
    }
}
