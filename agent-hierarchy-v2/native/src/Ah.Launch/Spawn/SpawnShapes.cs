using Ah.Abstractions;
using Ah.Codex;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Launch;

/// <summary>
/// What a launch is about: the working directory, the team file's team name (null for the default team), the repo's base name for
/// peer names, the resolved role registry, the verb running (<c>spawn-one</c>, <c>spawn-ad-hoc</c>, <c>create</c>, …) and the <c>--team</c> flag as given.
/// <paramref name="MemberTmp"/> maps a member name to the <c>TMPDIR</c> its launch exports.
/// </summary>
public sealed record LaunchContext(
    string Cwd,
    string? TeamFile,
    string RepoBasename,
    JsonObject Registry,
    string? Command = null,
    string? TeamArg = null,
    string? LaunchCwd = null,
    IReadOnlyDictionary<string, string>? MemberTmp = null)
{
    /// <summary>
    /// An unteamed orchestrator launch (<c>ahd new</c>): no team-prefix name check, no member env (<c>WRANGL_TEAM_FILE</c>,
    /// <c>WRANGL_EXPECTED_ROOT</c>, the member marker) and no <c>--settings</c> when that leaves the env empty. The transport
    /// <c>foreground</c> then shapes a plain <c>argv</c> to run in the caller's terminal.
    /// </summary>
    public bool Orchestrator { get; init; }

    /// <summary>The hierarchy directory of the team when it is not <see cref="Cwd"/>'s: a <c>--team</c> this session owns that is homed in another repo.</summary>
    public string? HierarchyDir { get; init; }
}

/// <summary>
/// A launch shape and the one failure that ends the command: <paramref name="Fail"/> is the message of a refusal that is the
/// whole command's (exit 2); null when <paramref name="Shape"/> stands. <paramref name="Notices"/> are the lines for stderr.
/// </summary>
public sealed record ShapeOutcome(JsonObject Shape, string? Fail, IReadOnlyList<string> Notices);

/// <summary>Where the models a harness declares at a tier come from: the user's declared tiers.</summary>
public interface IModelTiers
{
    /// <summary>The models of <paramref name="kind"/> declared at one of <paramref name="tiers"/>, in declaration order.</summary>
    IReadOnlyList<string> DeclaredModelsAt(string kind, IReadOnlyList<string> tiers);
}

/// <summary>
/// The single launch-shape seam: <c>spawn-one</c>, <c>create --spawn</c> and history replay all build a member's command here. A
/// Claude member's string is the same whatever follows; every other kind gets herdr's own <c>--kind</c> with its native arguments after
/// <c>--</c>.
/// </summary>
public interface ISpawnShapes
{
    /// <summary>Single-quotes one string for <c>/bin/sh -c</c>, so an element holding <c>;</c>, a backtick or <c>$(…)</c> arrives as one argument.</summary>
    string ShQuote(string text);

    /// <summary>
    /// The shape of one member's launch on <paramref name="transport"/>: <c>{transport, kind, args, layout, launch, launch_cwd,
    /// target_placeholder, target_from, target_source}</c>, plus <c>refuse</c> when the member cannot be launched here.
    /// <paramref name="agent"/> overrides the agent reference (a validated custom or fallback agent).
    /// </summary>
    ShapeOutcome SpawnShape(JsonValue member, string transport, LaunchContext context, string? agent = null);

    /// <summary>The agent reference <see cref="ShapeFor"/> passes to <c>--agent</c> for <paramref name="member"/>: the validated custom or fallback agent, else its role's own.</summary>
    string AgentRefOf(JsonValue member, LaunchContext context);

    /// <summary><see cref="SpawnShape"/> behind the spawn-seam revalidation; findings and a fallback notice ride on <c>validation</c>.</summary>
    ShapeOutcome ShapeFor(JsonValue member, string transport, LaunchContext context);

    /// <summary>A non-Claude member's standing instructions: <c>{text}</c>, or <c>{error, refusal}</c> when its contract cannot be found.</summary>
    JsonObject StandingInstructions(JsonValue member, LaunchContext context);

    /// <summary>Where a non-Claude member's standing instructions live, rewritten at each of its spawns.</summary>
    string InstructionsPath(string name, LaunchContext context);
}

/// <summary>The launch shapes.</summary>
public sealed record SpawnShapes(
    IAhEnvironment Environment,
    IFileReader Files,
    IProcessRunner Processes,
    IConfigCore Config,
    IRosterCore Roster,
    IMemberRules Rules,
    IMemberRegistry Registry,
    IKindHarnesses Harnesses,
    IHierarchyMessages Messages,
    IModelTiers Tiers,
    IHierarchyDirPolicy? HierarchyPolicy = null) : ISpawnShapes
{
    /// <summary>Writes a consented member's launch-only settings file; without it every launch carries the inline settings.</summary>
    /// <summary>
    /// Decides what a Claude member's <c>--settings</c> names. Returns the absolute path of a launch-only settings file it has just
    /// written, or null when the member launches with the inline settings (and then removes a file left by an earlier launch). It may
    /// throw; the caller then launches inline.
    /// </summary>
    public Func<MemberLaunchSettingsRequest, MemberLaunchOutcome?>? LaunchSettings { get; init; }

    /// <summary>Stands for the thread id in the planned terminal command: the spawn creates the thread, and only then is the id known.</summary>
    public const string ThreadPlaceholder = "<thread-id>";

    /// <summary>The statusline tap's guard is recognised by this path inside a statusline command.</summary>
    public const string TapMarker = "$HOME/.claude/ah2/bin/statusline-tap.sh";

    private const string PluginRootPlaceholder = "${CLAUDE_PLUGIN_ROOT}";
    private static readonly string[] TierNames = ["haiku", "sonnet", "opus", "fable"];

    /// <summary>
    /// The statusline tap's wrapper command: the tap script when it is readable, else <paramref name="original"/>, which is passed as <c>$0</c>
    /// (so removing the script can never break the statusline). The script records the statusline input and runs the original on it.
    /// </summary>
    public static string TapGuard(string original)
    {
        ArgumentNullException.ThrowIfNull(original);
        return "sh -c 'f=\"" + TapMarker + "\"; if [ -r \"$f\" ]; then exec sh \"$f\" \"$0\"; else exec sh -c \"$0\"; fi' "
            + "'" + original.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    public string ShQuote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>
    /// The member's statusline: the one it would inherit (the project's local settings, the project's, then the user's) behind the tap's guard, so that
    /// the pane shows the same line and the tap records the rate limits; a command that already carries the guard is used as it is, and with
    /// no statusline the guard wraps an empty command. Settings files are only read.
    /// </summary>
    internal JsonObject StatusLineFor(string cwd, string claudeDir)
    {
        var inherited = new[]
        {
            NodePath.Join(cwd, ".claude", "settings.local.json"),
            NodePath.Join(cwd, ".claude", "settings.json"),
            NodePath.Join(claudeDir, "settings.json"),
        }.Select(StatusLineIn).FirstOrDefault(static s => s is not null);

        var line = new JsonObject();
        if (inherited is not null)
        {
            foreach (var key in inherited.Keys)
            {
                line[key] = inherited[key];
            }
        }

        line["type"] = "command";
        var original = line.Get("command") is { IsText: true } command ? command.AsString() : string.Empty;
        line["command"] = original.Contains(TapMarker, StringComparison.Ordinal) ? original : TapGuard(original);
        return line;
    }

    private JsonObject? StatusLineIn(string path)
    {
        try
        {
            return Files.Exists(path) && JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(Files.ReadText(path)), out var doc) && doc.IsMap
                && doc.AsObject().Get("statusLine") is { IsMap: true } line && line.AsObject().Get("type") is { IsText: true } type && type.AsString() == "command"
                ? line.AsObject()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The value after <c>--settings</c>: the path of a launch-only file when a writer is composed and the member is consented to, else
    /// the inline JSON. A writer that throws leaves the inline JSON, so a launch never depends on it.
    /// </summary>
    private (string Value, JsonObject? Permissions) SettingsValue(JsonValue member, JsonObject settings, LaunchContext context, string launchCwd, bool isClaude)
    {
        if (!isClaude || LaunchSettings is not { } prepare)
        {
            return (JsonText.Stringify(settings, 0), null);
        }

        MemberLaunchOutcome outcome;
        try
        {
            outcome = prepare(new MemberLaunchSettingsRequest(member, settings, context, launchCwd)) ?? new MemberLaunchOutcome(null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the launch does not depend on the launch file: it goes inline, and the result says why
            outcome = new MemberLaunchOutcome(null, MemberLaunchOutcome.WriteFailed);
        }

        var permissions = new JsonObject
        {
            ["file"] = outcome.Path is { } file ? file : JsonValue.Null,
            ["cause"] = outcome.Cause ?? "none",
        };
        return (outcome.Path ?? JsonText.Stringify(settings, 0), permissions);
    }

    /// <summary>A Claude member's recorded <see cref="LaunchModule.ClaudeConfigField"/>: null when absent, empty for unset, else the path.</summary>
    internal static string? ClaudeConfigOf(JsonValue member) =>
        member.Member(LaunchModule.ClaudeConfigField) is { IsText: true } value ? value.AsString() : null;

    /// <summary>The Claude configuration directory a member launches in: its recorded value, else <c>~/.claude</c> as before.</summary>
    private string ClaudeDirFor(string? recorded) => Ah.Core.CoreModule.ClaudeConfigDir(recorded, Environment.HomeDirectory);

    /// <summary>A Claude member's recorded Claude environment: one (variable, value) per <see cref="LaunchModule.ClaudeEnvironment"/> row it records, in list order.</summary>
    internal static List<(string Variable, string Value)> ClaudeEnvOf(JsonValue member) =>
        [.. LaunchModule.ClaudeEnvironment.Where(v => member.Member(v.Field) is { IsText: true }).Select(v => (v.Variable, member.Member(v.Field)!.Value.AsString()))];

    /// <summary>
    /// The words before <c>claude</c> that put <paramref name="recorded"/> on the launch: none when empty, else one <c>env</c> with <c>-u NAME</c> or
    /// <c>NAME=value</c> per variable, every <c>-u</c> first: env reads options only before its first assignment.
    /// </summary>
    private string ClaudeEnvPrefix(List<(string Variable, string Value)> recorded) =>
        recorded.Count == 0 ? string.Empty : "env " + string.Concat(recorded.OrderBy(r => r.Value.Length != 0).Select(r => r.Value.Length == 0 ? $"-u {r.Variable} " : $"{r.Variable}={ShQuote(r.Value)} "));

    /// <summary>
    /// The commands before <c>claude</c> on a line typed into a pane's shell (tmux): an export or unset per variable, so the shell still resolves
    /// <c>claude</c> itself, an alias or function included; none when nothing is recorded.
    /// </summary>
    private string ClaudeShellPrefix(List<(string Variable, string Value)> recorded) =>
        string.Concat(recorded.Select(r => r.Value.Length == 0 ? $"unset {r.Variable}; " : $"export {r.Variable}={ShQuote(r.Value)}; "));

    public string InstructionsPath(string name, LaunchContext context) =>
        NodePath.Join(context.HierarchyDir ?? Config.HierarchyDir(context.Cwd), "instructions", name + ".md");

    public ShapeOutcome SpawnShape(JsonValue member, string transport, LaunchContext context, string? agent = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(context);
        var cwd = context.Cwd;
        var launchCwd = context.LaunchCwd ?? cwd;
        var kind = KindOf(member);
        var isClaude = kind == Registry.KindDefault;
        var name = Field(member, "name");
        var role = Field(member, "role");

        // A trusted hierarchy directory grants a launch command; one the environment named and the user did not trust launches nothing.
        if (HierarchyPolicy?.Override() is { Kind: HierarchyOverrideKind.Untrusted } untrusted)
        {
            return Refused(
                transport,
                kind,
                cwd,
                $"member {name} was not launched: WRANGL_HIERARCHY_DIR names {JsonText.Stringify(untrusted.Path ?? string.Empty, 0)}, which is not a trusted hierarchy directory, and a hierarchy directory supplies launch commands. Ask the user to trust it from a terminal (the trust-hierarchy verb), or unset WRANGL_HIERARCHY_DIR");
        }

        var configErrors = Rules.KindFieldErrors(member, context.Registry);
        if (configErrors.Count == 0 && kind == Registry.KindCustom)
        {
            return new ShapeOutcome(CustomShape(member, transport, context), null, []);
        }

        if (configErrors.Count > 0)
        {
            return Refused(transport, kind, cwd, $"member {name} (kind {kind}) has an invalid configuration: {string.Join("; ", configErrors)}");
        }

        if (transport == "herdr")
        {
            var herdrName = Config.ValidateHerdrName(member.Member("name"));
            if (herdrName.Get("ok") is not { IsTruthy: true })
            {
                return Refused(transport, kind, cwd, $"member {name}: {JsConvert.ToJsString(herdrName.Get("why"))}");
            }
        }

        if (!isClaude && transport != "herdr")
        {
            return Refused(transport, kind, cwd, $"member {name} has kind {JsonText.Stringify(kind, 0)}, which requires the herdr transport, but the detected transport is {JsonText.Stringify(transport, 0)} — start a Herdr session (HERDR_ENV=1) to spawn non-claude kinds");
        }

        if (!isClaude)
        {
            var instructions = StandingInstructions(member, context);
            if (instructions.Get("error") is { IsTruthy: true } error)
            {
                var refused = RefusedShape(transport, kind, cwd, JsConvert.ToJsString(error));
                if (instructions.Get("refusal") is { } refusal)
                {
                    refused["refusal"] = refusal;
                }

                return new ShapeOutcome(refused, null, []);
            }
        }

        var agentRef = !string.IsNullOrEmpty(agent) ? agent : Config.RoleAgent(role, null);
        if (isClaude && !Config.IsAgentRef(agentRef))
        {
            return Refused(transport, kind, cwd, $"member {name} has an invalid agent reference {JsonText.Stringify(agentRef, 0)}");
        }

        var hierarchyDir = context.HierarchyDir ?? Config.HierarchyDir(cwd);
        var teamFilePath = Roster.TeamPath(hierarchyDir, context.TeamFile);
        var orchestrator = context.Orchestrator;
        if (isClaude && context.TeamFile is not null && !orchestrator)
        {
            var expected = BaseNameWithout(teamFilePath, ".json");
            var soNamed = member.Member("role") is { IsText: true } soRole && soRole.AsString() == "sub-orchestrator"
                && member.Member("name") is { IsText: true } soName && soName.AsString().StartsWith(expected + "-so-", StringComparison.Ordinal);
            var cappedName = member.Member("name") is { IsText: true } cn && member.Member("role") is { IsText: true } cr
                && Ah.Core.Text.MemberNames.Ordinal(cn.AsString(), expected, cr.AsString()) > 0;
            if (!soNamed && !cappedName && Roster.MemberNamePrefixOf(member.Member("name"), member.Member("role")) != expected)
            {
                return new ShapeOutcome(
                    new JsonObject(),
                    $"member {name} would be launched with WRANGL_TEAM_FILE={teamFilePath}, but a member of that team is named {expected}-{role} or {expected}-{role}-<n> (expected prefix \"{expected}-\") — nothing was launched",
                    []);
            }
        }

        var memberEnv = orchestrator
            ? new JsonObject()
            : new JsonObject
            {
                ["WRANGL_TEAM_FILE"] = teamFilePath,
                ["WRANGL_EXPECTED_ROOT"] = Messages.RealCwd(launchCwd),
                [MeshModule.MemberMarkerEnv] = name,
            };
        if (isClaude && context.MemberTmp is { } tmpDirs && tmpDirs.TryGetValue(name, out var tmpDir) && tmpDir.Length > 0)
        {
            memberEnv["TMPDIR"] = tmpDir;
        }

        var claudeConfig = isClaude ? ClaudeConfigOf(member) : null;
        List<(string Variable, string Value)> claudeEnv = isClaude ? ClaudeEnvOf(member) : [];
        string? settingsValue = null;
        JsonObject? permissions = null;
        if (!orchestrator || memberEnv.Count > 0)
        {
            var settings = new JsonObject { ["env"] = memberEnv };
            if (isClaude)
            {
                settings["statusLine"] = StatusLineFor(launchCwd, ClaudeDirFor(claudeConfig));
            }

            (settingsValue, permissions) = SettingsValue(member, settings, context, launchCwd, isClaude);
        }

        // The shell line and the headless argv are two renderings of one flag list.
        var agentFlags = new List<string>();
        var agentArgv = new List<string>();
        void Flag(string flag, string value, bool quote = false)
        {
            agentFlags.Add($"{flag} {(quote ? ShQuote(value) : value)}");
            agentArgv.Add(flag);
            agentArgv.Add(value);
        }

        if (isClaude)
        {
            if (member.Member("resumeSession") is { IsTruthy: true } resume)
            {
                Flag("--resume", JsConvert.ToJsString(resume));
            }

            Flag("--agent", agentRef);
            Flag("--name", name);
            if (member.Member("model") is { IsTruthy: true } model && !(model.IsText && model.AsString() == "inherit"))
            {
                Flag("--model", JsConvert.ToJsString(model));
            }

            if (member.Member("effort") is { IsTruthy: true } effort)
            {
                Flag("--effort", JsConvert.ToJsString(effort));
            }

            if (member.Member("autoMode") is { IsTruthy: true } autoMode)
            {
                Flag("--permission-mode", JsConvert.ToJsString(autoMode));
            }

            if (settingsValue is not null)
            {
                Flag("--settings", settingsValue, quote: true);
            }
        }

        List<JsonValue>? nativeArgs = null;
        if (!isClaude)
        {
            var all = new List<JsonValue>();
            foreach (var a in Rules.KindAutoModeArgs(member) ?? [])
            {
                all.Add(a);
            }

            if (Rules.MemberArgs(member) is { } own)
            {
                all.AddRange(own);
            }

            nativeArgs = all.Count > 0 ? all : null;
        }

        var pool = NodePath.Join(hierarchyDir, "msgs");
        var shapeNotices = new List<string>();
        var mapped = new List<string>();
        if (!isClaude && Harnesses.Has(kind))
        {
            if (member.Member("model") is { IsTruthy: true } model)
            {
                mapped.AddRange(Harnesses.ModelArgs(kind, JsConvert.ToJsString(model)));
            }

            mapped.AddRange(Harnesses.InstructionsArgs(kind, InstructionsPath(JsConvert.ToJsString(member.Member("name")), context)));
            if (!IsUnderDir(pool, launchCwd))
            {
                mapped.AddRange(Harnesses.WritableDirArgs(kind, pool));
            }

            foreach (var gitDir in LinkedWorktreeGitDirs(cwd, launchCwd, shapeNotices, name))
            {
                mapped.AddRange(Harnesses.WritableDirArgs(kind, gitDir));
            }

            mapped.AddRange(Harnesses.ApprovalsArgs(kind));
        }

        var plainArgs = new List<string>(mapped);
        foreach (var a in nativeArgs ?? [])
        {
            plainArgs.Add(JsConvert.ToJsString(a));
        }

        AppserverPlan? plan = null;
        if (kind == "codex" && transport == "herdr" && CodexModule.AdapterWanted(AdapterOf(member), CodexModule.TestedRanges(Environment)))
        {
            plan = PlanAppserver(name, teamFilePath, plainArgs);
        }

        var plainWithMarker = new List<string>(plainArgs);
        if (!isClaude)
        {
            plainWithMarker.AddRange(Harnesses.EnvArgs(kind, MeshModule.MemberMarkerEnv, name));
        }

        var launchArgs = plan?.Appserver is not null ? plan.LaunchArgs! : plainWithMarker;
        string StartLine(IReadOnlyList<string> args) =>
            $"herdr agent start {name} --kind {kind} --pane <TARGET>" + (args.Count > 0 ? " -- " + string.Join(' ', args.Select(ShQuote)) : string.Empty);
        var claudeCmd = ClaudeEnvPrefix(claudeEnv) + "claude " + string.Join(' ', agentFlags);

        if (orchestrator && transport == "foreground")
        {
            return new ShapeOutcome(
                new JsonObject
                {
                    ["transport"] = transport,
                    ["kind"] = kind,
                    ["argv"] = ToArray(agentArgv.Prepend("claude").Select(JsonValue.FromString)),
                    ["launch_cwd"] = launchCwd,
                },
                null,
                shapeNotices);
        }

        if (transport == "headless")
        {
            // The process is started directly with this argv, never through a shell, so the pid recorded is the member's own.
            return new ShapeOutcome(
                WithPermissions(
                    WithClaudeConfig(new JsonObject
                    {
                        ["transport"] = transport,
                        ["kind"] = kind,
                        ["args"] = JsonValue.Null,
                        ["layout"] = new JsonArray(),
                        ["launch"] = Array($"{claudeCmd} -p <PROMPT>"),
                        ["argv"] = ToArray(agentArgv.Prepend("claude").Append("-p").Select(JsonValue.FromString)),
                        ["launch_cwd"] = launchCwd,
                        ["target_placeholder"] = JsonValue.Null,
                        ["target_from"] = JsonValue.Null,
                        ["target_source"] = JsonValue.Null,
                    }, claudeEnv),
                    permissions),
                null,
                []);
        }

        if (transport == "herdr")
        {
            var shape = new JsonObject
            {
                ["transport"] = transport,
                ["kind"] = kind,
                ["args"] = nativeArgs is not null ? ToArray(nativeArgs) : JsonValue.Null,
                ["layout"] = new JsonArray(),
                ["launch"] = Array(isClaude ? $"herdr agent start {name} --kind claude --pane <TARGET> -- {string.Join(' ', agentFlags)}" : StartLine(launchArgs)),
            };
            if (plan?.Appserver is { } appserver)
            {
                var withPlain = appserver.Clone();
                withPlain["plain"] = StartLine(plainWithMarker);
                shape["appserver"] = withPlain;
            }

            if (plan?.Note is { } note)
            {
                shape["adapter_note"] = note;
            }

            shape["launch_cwd"] = launchCwd;
            shape["target_placeholder"] = "<TARGET>";
            shape["target_from"] = JsonValue.Null;
            shape["target_source"] = new JsonObject { ["kind"] = "json", ["path"] = ".result.pane.pane_id" };
            WithClaudeConfig(shape, claudeEnv);
            WithPermissions(shape, permissions);
            return new ShapeOutcome(shape, null, shapeNotices);
        }

        if (transport == "tmux")
        {
            return new ShapeOutcome(
                WithPermissions(
                    new JsonObject
                    {
                        ["transport"] = transport,
                        ["kind"] = kind,
                        ["args"] = JsonValue.Null,
                        ["layout"] = Array($"tmux new-window -P -F '#{{pane_id}}' -c \"{launchCwd}\""),
                        // single quotes for the shell that runs this line: nothing in the typed command is expanded before tmux types it
                        ["launch"] = Array($"tmux send-keys -t <TARGET> {ShQuote(ClaudeShellPrefix(claudeEnv) + "claude " + string.Join(' ', agentFlags))} Enter"),
                        ["launch_cwd"] = launchCwd,
                        ["target_placeholder"] = "<TARGET>",
                        ["target_from"] = 0,
                        ["target_source"] = new JsonObject { ["kind"] = "stdout", ["trim"] = true },
                    },
                    permissions),
                null,
                []);
        }

        return new ShapeOutcome(
            WithPermissions(
                new JsonObject
                {
                    ["transport"] = transport,
                    ["kind"] = kind,
                    ["args"] = JsonValue.Null,
                    ["layout"] = new JsonArray(),
                    ["launch"] = Array($"{claudeCmd} --bg"),
                    ["launch_cwd"] = launchCwd,
                    ["target_placeholder"] = JsonValue.Null,
                    ["target_from"] = JsonValue.Null,
                    ["target_source"] = JsonValue.Null,
                },
                permissions),
            null,
            []);
    }

    /// <summary>Carries a recorded Claude environment on the shape, for the launcher to put on the child's environment or pane; absent stays absent.</summary>
    private static JsonObject WithClaudeConfig(JsonObject shape, List<(string Variable, string Value)> recorded)
    {
        foreach (var (variable, value) in recorded)
        {
            shape[LaunchModule.ClaudeEnvironment.First(v => v.Variable == variable).Field] = value;
        }

        return shape;
    }

    /// <summary>Records the launch-settings result on a Claude member's shape; a member with no writer composed has no key.</summary>
    private static JsonObject WithPermissions(JsonObject shape, JsonObject? permissions)
    {
        if (permissions is not null)
        {
            shape["permissions"] = permissions;
        }

        return shape;
    }

    public string AgentRefOf(JsonValue member, LaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var validated = SpawnValidation(member, context)?.Get("agent") is { IsText: true } a ? a.AsString() : null;
        return !string.IsNullOrEmpty(validated) ? validated : Config.RoleAgent(Field(member, "role"), null);
    }

    public ShapeOutcome ShapeFor(JsonValue member, string transport, LaunchContext context)
    {
        var notices = new List<string>();
        var validation = SpawnValidation(member, context);
        JsonObject shape;
        string? fail = null;
        if (validation?.Get("refuse") is { IsTruthy: true } refuse)
        {
            shape = RefusedShape(transport, KindOf(member), context.Cwd, JsConvert.ToJsString(refuse));
        }
        else
        {
            var agent = validation?.Get("agent") is { IsText: true } a ? a.AsString() : null;
            var built = SpawnShape(member, transport, context, agent);
            shape = built.Shape;
            fail = built.Fail;
            if (fail is not null)
            {
                return new ShapeOutcome(shape, fail, notices);
            }
        }

        var findings = validation?.Get("findings") is { IsArray: true } f ? f.AsArray() : new JsonArray();
        var noticeText = validation?.Get("notice") is { IsText: true } n ? n.AsString() : null;
        var refused = validation?.Get("refuse") is { IsTruthy: true };
        if (validation is not null && (refused || noticeText is not null || findings.Count > 0))
        {
            shape["validation"] = new JsonObject
            {
                ["refused"] = refused,
                ["notice"] = noticeText is not null ? noticeText : JsonValue.Null,
                ["findings"] = findings,
            };
            if (!refused)
            {
                var name = Field(member, "name");
                foreach (var finding in findings)
                {
                    var one = new JsonArray();
                    one.Add(finding);
                    notices.Add($"ah roster: {name}: {Config.FormatFindings(one)}");
                }

                if (noticeText is not null)
                {
                    notices.Add($"ah roster: {name}: {noticeText}");
                }
            }
        }

        if (shape.Get("refuse") is not { IsTruthy: true })
        {
            var name = Field(member, "name");
            foreach (var w in Rules.KindFieldWarnings(member, context.Registry, tier: false))
            {
                notices.Add($"ah roster: warning — {name}: {w}");
            }

            var cls = Config.RoleClass(Field(member, "role"), context.Registry);
            var child = LegworkChild(member, cls, context);
            if (child is not null && child.Get("error") is null && child.Get("model") is not { IsTruthy: true })
            {
                notices.Add($"ah roster: warning — {name}'s native legwork children will run on its own model: no {JsConvert.ToJsString(child.Get("kind"))} model is declared at tier {JsConvert.ToJsString(child.Get("tier"))}. Declare one with `ah roster tier set {JsConvert.ToJsString(child.Get("kind"))} <model> {JsConvert.ToJsString(child.Get("tier"))}`.");
            }
        }

        return new ShapeOutcome(shape, null, notices);
    }

    public JsonObject StandingInstructions(JsonValue member, LaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var role = Field(member, "role");
        var registry = context.Registry;
        var entry = registry.Get("roles") is { IsMap: true } roles ? roles.Member(role) : null;
        var builtin = Config.IsBuiltinRole(role);
        string? reference = builtin ? Config.Qualified(role) : entry?.Member("agent") is { IsTruthy: true } agentRef ? JsConvert.ToJsString(agentRef) : null;
        var ownRoot = OwnRoot();
        var ownFile = NodePath.Join(ownRoot, "agents", role + ".md");

        string? path = null;
        string? why = "no agent configured";
        if (builtin)
        {
            if (Files.Exists(ownFile))
            {
                path = ownFile;
            }
            else
            {
                why = $"{ownFile} does not exist";
            }
        }
        else if (reference is not null)
        {
            var found = Config.LocateAgentFile(reference, context.Cwd);
            if (found.Get("path") is { IsText: true, IsTruthy: true } located)
            {
                path = located.AsString();
            }
            else
            {
                why = found.Get("error") is { IsNull: false } err ? JsConvert.ToJsString(err) : "undefined";
            }
        }

        if (path is null)
        {
            return ContractNotFound(member, reference, why);
        }

        var parts = AgentFileParts(path, builtin ? ownRoot : null);
        var label = JsConvert.ToJsString(Config.RoleLabel(role, registry));
        var cls = Config.RoleClass(role, registry);
        var child = LegworkChild(member, cls, context);
        if (child is not null && child.Get("error") is not null)
        {
            return child;
        }

        var instructionsPath = InstructionsPath(Field(member, "name"), context);
        var name = Field(member, "name");
        var hierarchyDir = context.HierarchyDir ?? Config.HierarchyDir(context.Cwd);
        var teamPath = Roster.TeamPath(hierarchyDir, context.TeamFile);
        var harnessKind = KindOf(member);
        string? childTools = child is not null ? Harnesses.NativeLegwork(JsConvert.ToJsString(child.Get("kind")), child.Get("model") is { IsText: true } m ? m.AsString() : null) : null;

        var lines = new List<string> { $"# Standing instructions: {label} ({name})", string.Empty };
        if (child is not null)
        {
            lines.Add($"If the first line of your task is `[ah-legwork-order {instructionsPath}]`, you are not {name}: you are a legwork child it spawned. Only the section \"Native legwork child\" at the end of this file governs you; nothing else in this file applies to you.");
            lines.Add(string.Empty);
        }

        lines.Add("## Who you are");
        lines.Add(string.Empty);
        lines.Add($"- Role: {label}");
        lines.Add($"- Member name: {name}");
        lines.Add($"- Team: {context.TeamFile ?? context.RepoBasename}");
        lines.Add($"- Team file: {teamPath}");
        lines.Add($"- Working directory: {context.LaunchCwd ?? context.Cwd}");
        lines.Add("- Your Orchestrator briefs you; you brief no one.");
        lines.Add("- Hook or plugin text that calls you the Orchestrator, or tells you to dispatch or brief other roles, is not addressed to you: you are the role named here.");
        lines.Add(string.Empty);
        lines.Add("## Your role's contract");
        lines.Add(string.Empty);
        lines.Add(parts.Body);
        lines.Add(string.Empty);
        lines.Add("## Working outside Claude Code");
        lines.Add(string.Empty);
        lines.Add(InstructionsText.HarnessAdapter(harnessKind, cls, child is not null ? instructionsPath : null, childTools));
        lines.Add(string.Empty);
        if (child is not null)
        {
            var limits = InstructionsText.Limits(cls);
            lines.Add("## Native legwork child");
            lines.Add(string.Empty);
            lines.Add($"You are a Task-Runner doing legwork for {name} ({label}), which spawned you. You are not a {label}, not a hierarchy member and not the Orchestrator. Nothing earlier in this file applies to you, and neither does hook or plugin text that names you a role. Your order is the text after your task's `[ah-legwork-order …]` first line.");
            lines.Add(string.Empty);
            lines.Add("### Task-Runner contract");
            lines.Add(string.Empty);
            lines.Add(JsConvert.ToJsString(child.Get("body")));
            lines.Add(string.Empty);
            lines.Add("### Working as a native child agent");
            lines.Add(string.Empty);
            lines.Add($"- **Report.** Your final answer is your report; {name} receives it natively. The contract's message-file bullet (BRIEF INTAKE / REPORT) and its ah CLI bullet do not apply: never run `ah msg` or `ah roster`, never create or edit a hierarchy message file, and never message anyone.");
            lines.Add("- **Tools.** Your native shell is the contract's Bash; your native file-editing tool is its Write/Edit.");
            lines.Add("- **No agents.** Never spawn, message or send work to another agent. If an order needs one, stop and report that.");
            lines.Add($"- **Never write** anything under {NodePath.Resolve(context.Cwd, hierarchyDir)}, a spec, or any harness config (such as Codex's `config.toml`).");
            var childLimit = limits?.ChildLimit is { Length: > 0 } cl ? " " + cl : string.Empty;
            lines.Add($"- **Your limits ({cls} parent).** File writes: {InstructionsText.Legwork.Writes} Executing code: {InstructionsText.Legwork.Exec}{childLimit}");
            lines.Add(string.Empty);
        }

        return new JsonObject { ["text"] = string.Join('\n', lines) };
    }

    /// <summary>The Codex app-server plan: <c>{appserver: {sock, tmp, serverArgs, modes}, launchArgs}</c>, or <c>{note}</c> for a plain launch.</summary>
    private AppserverPlan PlanAppserver(string memberName, string teamFilePath, IReadOnlyList<string> plainArgs)
    {
        var gate = CodexModule.AdapterGate(CodexVersionText(), CodexModule.TestedRanges(Environment));
        if (!gate.Ok)
        {
            return new AppserverPlan(null, null, gate.Note);
        }

        var team = BaseNameWithout(teamFilePath, ".json");
        var ahHome = Environment.AhHome;
        var sock = CodexModule.AppserverSocket(ahHome, team, memberName);
        var tmp = CodexModule.MemberTmpDir(ahHome, team, memberName);
        var checkedLaunch = CodexModule.CheckLaunch(plainArgs, ahHome, Files);
        if (checkedLaunch.Violations.Count > 0)
        {
            return new AppserverPlan(null, null, $"the app-server adapter was not used: launch arguments outside the adapter allow-list: {string.Join(", ", checkedLaunch.Violations)}; launched without it");
        }

        var modes = CodexModule.LaunchModes(checkedLaunch);
        var server = new JsonArray();
        foreach (var a in CodexModule.ServerArgs(sock, checkedLaunch, tmp, memberName))
        {
            server.Add(a);
        }

        var appserver = new JsonObject
        {
            ["sock"] = sock,
            ["tmp"] = tmp,
            ["serverArgs"] = server,
            ["modes"] = new JsonObject { ["sandbox"] = modes.Sandbox, ["approval"] = modes.Approval },
        };
        return new AppserverPlan(appserver, CodexModule.TuiArgs(ThreadPlaceholder, sock), null);
    }

    /// <summary><c>codex --version</c> output, or empty when codex cannot be run.</summary>
    private string CodexVersionText()
    {
        var result = Processes.Run(new ProcessRequest(["codex", "--version"], null, null, default, TimeSpan.FromSeconds(10)));
        return result is { ExitCode: 0, TimedOut: false, Truncated: false } ? Utf8Text.Decode(result.Stdout.Span) : string.Empty;
    }

    private JsonObject CustomShape(JsonValue member, string transport, LaunchContext context)
    {
        var cwd = context.LaunchCwd ?? context.Cwd;
        var name = Field(member, "name");
        JsonObject Base() => new()
        {
            ["transport"] = transport,
            ["kind"] = Registry.KindCustom,
            ["args"] = JsonValue.Null,
            ["layout"] = new JsonArray(),
            ["launch"] = new JsonArray(),
            ["launch_cwd"] = cwd,
            ["target_placeholder"] = JsonValue.Null,
            ["target_from"] = JsonValue.Null,
            ["target_source"] = JsonValue.Null,
        };

        var shape = Base();
        if (member.Member("command") is not { IsTruthy: true } command)
        {
            shape["refuse"] = $"member {name} has no command to launch";
            return shape;
        }

        if (transport != "tmux")
        {
            shape["refuse"] = $"member {name} has a command, and ah launches a custom harness's command only in a tmux window (the detected transport is {JsonText.Stringify(transport, 0)}) — omit --command and start the harness yourself with AH_HOME and AH_ADDRESS set";
            return shape;
        }

        var hierarchyDir = context.HierarchyDir ?? Config.HierarchyDir(context.Cwd);
        var team = BaseNameWithout(Roster.TeamPath(hierarchyDir, context.TeamFile), ".json");
        var address = ShQuote($"{name}@{team}");
        var home = Environment.AhHome;
        var defaultHome = AhPaths.HomeUnder(Environment.HomeDirectory);
        var prefix = home == defaultHome ? string.Empty : $"AH_HOME={ShQuote(home)} ";
        var line = $"{prefix}{MeshModule.MemberMarkerEnv}={ShQuote(name)} AH_ADDRESS={address} {JsConvert.ToJsString(command)}";
        shape["layout"] = Array($"tmux new-window -P -F '#{{pane_id}}' -c \"{cwd}\"");
        shape["launch"] = Array($"tmux send-keys -t <TARGET> {ShQuote(line)} Enter");
        shape["target_placeholder"] = "<TARGET>";
        shape["target_from"] = 0;
        shape["target_source"] = new JsonObject { ["kind"] = "stdout", ["trim"] = true };
        return shape;
    }

    /// <summary>
    /// Re-runs the class-contract validator at the spawn seam for a claude-kind member whose agent is custom or overridden. Null for a
    /// shipped built-in; otherwise <c>{agent, findings, notice}</c> or <c>{refuse, findings}</c>.
    /// </summary>
    private JsonObject? SpawnValidation(JsonValue member, LaunchContext context)
    {
        var refusal = PackSpawnRefusal(member, context);
        if (refusal is not null)
        {
            return new JsonObject { ["refuse"] = refusal, ["findings"] = new JsonArray() };
        }

        if (KindOf(member) != Registry.KindDefault)
        {
            return null;
        }

        var role = Field(member, "role");
        var entry = context.Registry.Get("roles") is { IsMap: true } roles ? roles.Member(role) : null;
        if (entry is not { IsTruthy: true } e)
        {
            return new JsonObject { ["refuse"] = $"role \"{role}\" is not defined here — ah roster role list", ["findings"] = new JsonArray() };
        }

        if (Config.IsBuiltinRole(role) && !Config.IsOverride(role, e))
        {
            return null;
        }

        var registry = context.Registry;
        if (ClaudeConfigOf(member) is { } recorded)
        {
            registry = registry.Clone();
            registry[ConfigModule.ClaudeDirKey] = ClaudeDirFor(recorded);
        }

        var result = Config.ValidateRole(role, registry);
        var findings = result.Member("findings") ?? new JsonArray();
        var failed = Config.HasContractErrors(findings);
        var name = Field(member, "name");
        if (Config.IsBuiltinRole(role))
        {
            if (!failed)
            {
                return new JsonObject { ["agent"] = Config.RoleAgent(role, e), ["findings"] = findings, ["notice"] = JsonValue.Null };
            }

            return new JsonObject
            {
                ["agent"] = Config.Qualified(role),
                ["findings"] = findings,
                ["notice"] = $"spawned {Config.Qualified(role)} in place of {Config.RoleAgent(role, e)} — that agent fails the {Config.RoleClass(role, context.Registry)} contract",
            };
        }

        if (failed)
        {
            var entryAgent = JsConvert.ToJsString(e.Member("agent"));
            var entryClass = JsConvert.ToJsString(e.Member("class"));
            return new JsonObject
            {
                ["refuse"] = $"role {role}: agent {entryAgent} fails the {entryClass} contract, so {name} was not launched:\n{Config.FormatFindings(findings.AsArray())}",
                ["findings"] = findings,
            };
        }

        return new JsonObject { ["agent"] = e.Member("agent") ?? JsonValue.Null, ["findings"] = findings, ["notice"] = JsonValue.Null };
    }

    private string? PackSpawnRefusal(JsonValue member, LaunchContext context)
    {
        var role = Field(member, "role");
        var entry = context.Registry.Get("roles") is { IsMap: true } roles ? roles.Member(role) : null;
        if (entry is not { IsTruthy: true } e || e.Member("from") is not { IsTruthy: true } from)
        {
            return null;
        }

        var mode = member.Member("autoMode");
        var unset = mode is null || mode.Value.IsNull || (mode.Value.IsText && mode.Value.AsString().Length == 0);
        var name = Field(member, "name");
        if ((mode is { IsText: true } m && m.AsString() == "bypassPermissions") || unset)
        {
            var whyText = unset
                ? "has no auto-mode, so it would run in the default mode of the user's settings, which can be bypassPermissions"
                : "has auto-mode bypassPermissions";
            var tail = context.Command == "spawn-ad-hoc" ? ", or pass --auto-mode auto" : string.Empty;
            return $"role {role} is adopted from a pack ({JsConvert.ToJsString(from)}) and a pack role never runs with permission checks off, but {name} {whyText} — it was not launched. Give it a mode with `ah roster edit --member {name} --auto-mode <mode>` (auto is the hands-off one){tail}.";
        }

        var slot = Config.RoleClass(role, context.Registry);
        var optedIn = slot == "design" && Config.DesignOptInHolds(role, e);
        if ((context.Command is "spawn-one" or "spawn-ad-hoc") && slot is "review" or "design" && !optedIn && Messages.PipelineRunLive(context.Cwd))
        {
            var builtin = Config.ClassBuiltin(slot);
            var label = builtin is not null ? JsConvert.ToJsString(Config.RoleLabel(builtin, context.Registry)) : "undefined";
            return $"role {role} is adopted from a pack ({JsConvert.ToJsString(from)}) and is {slot}-class, and a pipeline run is live in this checkout — its reviewer and designer are always first-party, so {name} was not launched. Use the built-in {label} for the run.";
        }

        return null;
    }

    /// <summary>
    /// The native legwork child a non-Claude member delegates to, or null when it gets none: <c>{kind, body, tier, model}</c>, or
    /// <c>{error, refusal}</c> when the task-runner contract is missing.
    /// </summary>
    private JsonObject? LegworkChild(JsonValue member, string? cls, LaunchContext context)
    {
        var kind = KindOf(member);
        if (!Harnesses.HasNativeLegwork(kind) || cls is null || !Registry.ClassIsChain(cls))
        {
            return null;
        }

        var ownRoot = OwnRoot();
        var path = NodePath.Join(ownRoot, "agents", "task-runner.md");
        if (!Files.Exists(path))
        {
            return ContractNotFound(member, Config.Qualified("task-runner"), $"{path} does not exist");
        }

        var parts = AgentFileParts(path, ownRoot);
        var tier = parts.Fields.Get("model");
        string? model = null;
        if (tier is { IsText: true } t && TierNames.Contains(t.AsString()))
        {
            model = Tiers.DeclaredModelsAt(kind, [t.AsString()]).FirstOrDefault(m => m.Length > 0);
        }

        return new JsonObject
        {
            ["kind"] = kind,
            ["body"] = parts.Body,
            ["tier"] = tier ?? JsonValue.Null,
            ["model"] = model is not null ? model : JsonValue.Null,
        };
    }

    private sealed record AgentParts(string Body, JsonObject Fields);

    private AgentParts AgentFileParts(string path, string? pluginRoot)
    {
        var source = Files.ReadText(path);
        var fm = Messages.ParseFrontmatter(source);
        string body;
        JsonObject fields;
        if (fm is { IsMap: true } doc)
        {
            var end = (int)JsConvert.ToNumber(doc.Member("end"));
            var lines = source.Split('\n');
            body = JsText.Trim(string.Join('\n', lines.Skip(end)));
            fields = doc.Member("fields") is { IsMap: true } f ? f.AsObject() : new JsonObject();
        }
        else
        {
            body = JsText.Trim(source);
            fields = new JsonObject();
        }

        if (pluginRoot is not null)
        {
            body = body.Replace(PluginRootPlaceholder, pluginRoot, StringComparison.Ordinal);
        }

        return new AgentParts(body, fields);
    }

    private static JsonObject ContractNotFound(JsonValue member, string? reference, string? why)
    {
        var name = Field(member, "name");
        var role = Field(member, "role");
        var message = $"cannot find the agent file for {(string.IsNullOrEmpty(reference) ? role : reference)} ({why}), so {name}'s standing instructions cannot be written — nothing was launched for it";
        return new JsonObject
        {
            ["error"] = message,
            ["refusal"] = new JsonObject
            {
                ["refused"] = "agent-file-not-found",
                ["member"] = member.Member("name") ?? JsonValue.Null,
                ["role"] = member.Member("role") ?? JsonValue.Null,
                ["ref"] = string.IsNullOrEmpty(reference) ? JsonValue.Null : reference,
                ["message"] = message,
            },
        };
    }

    private string OwnRoot() =>
        Environment.Plugin?.Root ?? throw new InvalidOperationException("the plugin root is not known to this process");

    private bool IsUnderDir(string path, string dir)
    {
        string Real(string p)
        {
            try
            {
                return Files.RealPath(p);
            }
            catch (Exception)
            {
                return NodePath.Resolve(dir, p);
            }
        }

        static bool Under(string p, string d) => p == d || p.StartsWith(d.EndsWith('/') ? d : d + "/", StringComparison.Ordinal);
        var resolvedPath = NodePath.Resolve(dir, path);
        var resolvedDir = NodePath.Resolve(dir, dir);
        return Under(resolvedPath, resolvedDir) || Under(Real(path), Real(dir));
    }

    /// <summary>
    /// The directories a commit from the linked work tree <paramref name="tree"/> writes outside it: the work tree's own git dir and
    /// the shared objects, refs and logs. The shared config and hooks are left out. The shared git dir comes from the launcher's own
    /// checkout (<paramref name="home"/>), never from files in the work tree, which its member can write: the work tree's
    /// <c>.git</c> file must name a directory directly under <c>&lt;common&gt;/worktrees</c> whose <c>commondir</c> names that same
    /// common dir. Anything else grants nothing.
    /// </summary>
    private List<string> LinkedWorktreeGitDirs(string home, string tree, List<string> notices, string member)
    {
        var dirs = new List<string>();
        if (IsUnderDir(home, tree))
        {
            // git run in the member's own tree would answer from files its member can write
            return dirs;
        }

        try
        {
            var dotGit = NodePath.Join(tree, ".git");
            if (!Files.Exists(dotGit) || Files.ReadText(dotGit).Split('\n')[0] is not { } first || !first.StartsWith("gitdir:", StringComparison.Ordinal))
            {
                return dirs;
            }

            var asked = Processes.Run(new ProcessRequest(["git", "rev-parse", "--path-format=absolute", "--git-common-dir"], home, null, default, TimeSpan.FromSeconds(10)));
            if (asked is not { ExitCode: 0, TimedOut: false, Truncated: false })
            {
                return dirs;
            }

            var common = Files.RealPath(Utf8Text.Decode(asked.Stdout.Span).Trim());
            var gitDir = Files.RealPath(NodePath.Resolve(tree, first["gitdir:".Length..].Trim()));
            var shared = NodePath.Join(gitDir, "commondir");
            if (NodePath.Dirname(gitDir) != NodePath.Join(common, "worktrees")
                || !Files.Exists(shared)
                || Files.RealPath(NodePath.Resolve(gitDir, Files.ReadText(shared).Trim())) != common)
            {
                return dirs;
            }

            // a per-worktree config would be read by the Orchestrator's own git runs in that tree, and its git dir is writable here
            var worktreeConfig = Processes.Run(new ProcessRequest(["git", "config", "--get", "extensions.worktreeConfig"], home, null, default, TimeSpan.FromSeconds(10)));
            if (worktreeConfig is { ExitCode: 0, TimedOut: false } && Utf8Text.Decode(worktreeConfig.Stdout.Span).Trim() is "true" or "1" or "yes" or "on")
            {
                notices.Add($"ah roster: warning — {member}: extensions.worktreeConfig is on in {common}, so its work tree's git dir was not made writable and the member cannot commit there");
                return dirs;
            }

            dirs.Add(gitDir);
            foreach (var part in new[] { "objects", "refs", "logs" })
            {
                var path = NodePath.Join(common, part);
                if (Files.Exists(path))
                {
                    dirs.Add(path);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // a work tree whose git dir cannot be read launches without the extra directories
        }

        return dirs;
    }

    private string KindOf(JsonValue member) => JsConvert.ToJsString(Config.ResolveKind(member));

    private static string Field(JsonValue member, string name) =>
        member.Member(name) is { } v ? JsConvert.ToJsString(v) : "undefined";

    private static string? AdapterOf(JsonValue member) =>
        member.Member("adapter") is { IsText: true } a ? a.AsString() : null;

    private static string BaseNameWithout(string path, string extension)
    {
        var b = NodePath.Basename(path);
        return b.EndsWith(extension, StringComparison.Ordinal) && b.Length > extension.Length ? b[..^extension.Length] : b;
    }

    private static JsonArray Array(string one)
    {
        var a = new JsonArray();
        a.Add(one);
        return a;
    }

    private static JsonArray ToArray(IEnumerable<JsonValue> values)
    {
        var a = new JsonArray();
        a.AddRange(values);
        return a;
    }

    private static JsonObject RefusedShape(string transport, string kind, string cwd, string message) => new()
    {
        ["transport"] = transport,
        ["kind"] = kind,
        ["layout"] = new JsonArray(),
        ["launch"] = new JsonArray(),
        ["launch_cwd"] = cwd,
        ["target_placeholder"] = JsonValue.Null,
        ["target_from"] = JsonValue.Null,
        ["target_source"] = JsonValue.Null,
        ["refuse"] = message,
    };

    private static ShapeOutcome Refused(string transport, string kind, string cwd, string message) =>
        new(RefusedShape(transport, kind, cwd, message), null, []);

    private sealed record AppserverPlan(JsonObject? Appserver, IReadOnlyList<string>? LaunchArgs, string? Note);
}
