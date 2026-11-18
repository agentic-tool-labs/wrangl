using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Gates;

/// <summary>
/// ah2-216: the Orchestrator is pointed at the team commands. Its first chain-role Agent call (dispatch peers, prefer-peers or unset),
/// roster verb that starts, reshapes or closes a member, or raw multiplexer start of a session in a span gets one note, unless it ran
/// <c>ah rules show peer-roster</c> in that span; a prompt asking to start a member gets the spawn command. Every call goes through.
/// </summary>
internal static class TeamCommands
{
    internal const string RuleCommand = "ah rules show peer-roster";

    /// <summary>Amendment 3: the note is written before the call runs, so it says only that it does not hold it.</summary>
    internal const string NoHold = "This note does not hold the call.";

    private static readonly string[] MemberVerbs = ["spawn-one", "spawn-ad-hoc", "create", "dismiss", "disband", "move", "adopt"];

    /// <summary>
    /// D5: a top-level session launched as the Orchestrator agent, one that owns a team, or (Amendment 3) one flagged as the Orchestrator
    /// that owns no team yet and is no member.
    /// </summary>
    private static bool IsOrchestrator(GateContext c, JsonObject resolved) =>
        !c.Services.Config.IsSubagent(c.Input)
        && (OrchestratorIdentity.LaunchedAsAgent(c) || resolved.Get("ownedTeams") is { IsArray: true } owned && owned.AsArray().Count > 0
            || (OrchestratorIdentity.Flagged(c) && !IsMember(c)));

    private static bool IsMember(GateContext c) =>
        c.Services.Config.IsMemberSession
        || c.Services.Environment.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is { Length: > 0 }
        || c.Services.Config.ResolveHierarchyRole(c.Input).Role is { } role && role != "orchestrator";

    private static JsonObject? Resolved(GateContext c)
    {
        var sessionKey = c.SessionKey();
        var resolved = c.Services.Config.ResolveConfig(c.Cwd(), new ResolveOptions(SessionId: sessionKey != "__nosession__" ? sessionKey : null));
        return resolved.Get("enabled") is { IsTruthy: true } && IsOrchestrator(c, resolved) ? resolved : null;
    }

    /// <summary>The spawn command with this session's team (when it owns exactly one) and cwd.</summary>
    internal static string SpawnCommand(GateContext c, JsonObject resolved, string role)
    {
        var team = resolved.Get("ownedTeams") is { IsArray: true } owned && owned.AsArray().Count == 1 && owned.AsArray()[0] is { IsText: true } t
            ? $" --team {t.AsString()}"
            : string.Empty;
        return $"{c.Services.Config.CliCommand(CliTool.Roster)} spawn-ad-hoc {role}{team} --cwd {c.Cwd()}";
    }

    /// <summary>The soft-rules row (last, so it never displaces a rule that holds the call): the D2 note, or null.</summary>
    internal static HookOutcome? FirstUse(GateContext c)
    {
        var tool = c.ToolName();
        var commands = tool == "Bash" && c.Command() is { } command ? ShellScan.Commands(command) : null;
        var agentType = tool is "Agent" or "Task" ? c.ToolInput().Get("subagent_type")?.NonEmptyText : null;
        if (agentType is null && commands is null)
        {
            return null;
        }

        var roster = commands is not null && commands.Any(cmd => IsAh(cmd.Argv, "roster") && MemberVerbs.Contains(cmd.Argv[2]));
        var mux = commands is not null && commands.Any(cmd => StartsSession(cmd, c.Command()!));
        if (agentType is null && !roster && !mux)
        {
            return null;
        }

        // A call that shows the rule itself needs no note: its own mark is written only once it runs.
        if ((commands is not null && commands.Any(cmd => IsAh(cmd.Argv, "rules") && cmd.Argv.Count > 3 && cmd.Argv[2] == "show" && cmd.Argv[3] == "peer-roster"))
            || Resolved(c) is not { } resolved)
        {
            return null;
        }

        string? text = null;
        if (agentType is not null)
        {
            var mode = ConfigModule.DispatchMode(resolved);
            if (mode is "peers" or "prefer-peers" && ChainRole(c, resolved, agentType) is { } role)
            {
                text = mode == "peers"
                    ? $"ah: {role} runs as a peer session here (dispatch: peers), not as a subagent. Start it with `{SpawnCommand(c, resolved, role)}`, then brief it with `ah msg new` and `ah msg send`. {NoHold} Team commands: `{RuleCommand}`."
                    : $"ah: peer sessions are the default here (dispatch: prefer-peers); a subagent for {role} is your call: state it in one line. The peer: `{SpawnCommand(c, resolved, role)}`. {NoHold} Team commands: `{RuleCommand}`.";
            }
        }
        else if (mux)
        {
            text = $"ah: members are started with `{SpawnCommand(c, resolved, "<role>")}`, not with herdr or tmux commands. {NoHold} Team commands: `{RuleCommand}`.";
        }
        else
        {
            text = $"ah: before starting, reshaping or closing members, read the team commands: `{RuleCommand}`. {NoHold}";
        }

        return text is null ? null : Once(c, text);
    }

    /// <summary>The note once per session span: none after a <c>nudge</c> or a <c>read</c> row of this session since its latest reset.</summary>
    private static HookOutcome? Once(GateContext c, string text)
    {
        var dir = c.Services.Config.HierarchyDir(c.Cwd());
        var session = c.SessionKey();
        if (SoftGate.SinceReset(c, SoftGate.Rows(c, dir)).Any(r =>
                r.Text("rule") == ConfigModule.TeamCommandsRule && r.Text("session_id") == session && r.Text("outcome") is "nudge" or "read"))
        {
            return null;
        }

        SoftGate.Note(c, dir, new JsonObject { ["rule"] = ConfigModule.TeamCommandsRule, ["outcome"] = "nudge", ["tool"] = c.ToolName() ?? string.Empty });
        return HookEmit.Combined(null, null, text, null);
    }

    /// <summary>The chain role (not legwork, not orchestrate) an Agent <c>subagent_type</c> names, or null.</summary>
    private static string? ChainRole(GateContext c, JsonObject resolved, string agentType)
    {
        var config = c.Services.Config;
        return config.HierarchyRoleOf(JsonValue.FromString(agentType), resolved, c.Cwd()) is { IsText: true } r
            && r.AsString() is { Length: > 0 } role
            && config.ChainRoles(resolved).Contains(role)
            && config.RoleClass(role, resolved) != "orchestrate"
            ? role
            : null;
    }

    private static bool IsAh(IReadOnlyList<string> argv, string group) =>
        argv.Count > 2 && Path.GetFileName(argv[0]) == "ah" && argv[1] == group;

    // Herdr's global options that take a value.
    private static readonly string[] HerdrValueOptions = ["--session", "--remote", "--remote-keybindings"];

    /// <summary>
    /// A2: a raw multiplexer command that starts a session: <c>herdr|hc pane split</c>, <c>agent start</c>, <c>tab new</c>, <c>hc run … claude</c>,
    /// or <c>tmux new-window|split-window</c> whose later arguments start <c>claude</c>. A quoted multi-word argument has no text in
    /// <paramref name="cmd"/>, so for tmux the word <c>claude</c> anywhere in <paramref name="raw"/> (the whole Bash command) counts.
    /// </summary>
    internal static bool StartsSession(ShellCommand cmd, string raw)
    {
        var argv = cmd.Argv;
        var bin = argv.Count > 0 ? Path.GetFileName(argv[0]) : string.Empty;
        if (bin is "herdr" or "hc")
        {
            var words = new List<string>();
            for (var i = 1; i < argv.Count && words.Count < 2; i++)
            {
                if (HerdrValueOptions.Contains(argv[i]))
                {
                    i++;
                }
                else if (!argv[i].StartsWith('-'))
                {
                    words.Add(argv[i]);
                }
            }

            return words switch
            {
                ["pane", "split"] or ["agent", "start"] or ["tab", "new"] => true,
                ["run", ..] => bin == "hc" && RunsClaude(argv.Skip(2)),
                _ => false,
            };
        }

        if (bin != "tmux")
        {
            return false;
        }

        var at = argv.ToList().FindIndex(a => a is "new-window" or "neww" or "split-window" or "splitw");
        return at > 0 && (RunsClaude(argv.Skip(at + 1)) || Words(raw).Contains("claude"));
    }

    private static bool RunsClaude(IEnumerable<string> args) =>
        args.Any(a => !a.StartsWith('-') && a.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] && Path.GetFileName(first) == "claude");

    // ---------------------------------------------------------------- A1: a prompt that asks to start a member

    private static readonly string[] StartVerbs = ["spawn", "start", "launch", "add", "create", "open"];

    private static readonly string[] UpVerbs = ["spin", "bring"];

    private static readonly string[] MemberNouns = ["peer", "peers", "agent", "agents", "member", "members", "teammate", "teammates"];

    /// <summary>How many words after the verb the noun may come: "spawn two peer reviewers", "spin up three more implementors".</summary>
    internal const int Reach = 4;

    /// <summary>
    /// A1: the role a prompt asks to start (<c>&lt;role&gt;</c> when it names only a peer, agent, member or teammate), or null: a start verb
    /// (spawn, start, launch, spin up, bring up, add, create, open) followed within <see cref="Reach"/> words by one of those nouns or a
    /// role of <paramref name="roles"/>, singular or plural. A named role wins over a bare noun.
    /// </summary>
    internal static string? AskedRole(string prompt, IReadOnlyCollection<string> roles)
    {
        var words = Words(prompt);
        string? found = null;
        for (var i = 0; i < words.Count; i++)
        {
            var end = StartVerbs.Contains(words[i]) ? i : UpVerbs.Contains(words[i]) && i + 1 < words.Count && words[i + 1] == "up" ? i + 1 : -1;
            if (end < 0)
            {
                continue;
            }

            for (var j = end + 1; j <= end + Reach && j < words.Count; j++)
            {
                if (roles.FirstOrDefault(r => words[j] == r || words[j] == r + "s") is { } role)
                {
                    return role;
                }

                if (MemberNouns.Contains(words[j]))
                {
                    found ??= "<role>";
                }
            }
        }

        return found;
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        foreach (var ch in text.ToLowerInvariant())
        {
            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            {
                current.Append(ch);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    /// <summary>A1: the note for a typed prompt of the Orchestrator's own session that asks to start a member, or null. Every such prompt gets it.</summary>
    internal static string? PromptNote(GateContext c, string prompt)
    {
        // Amendment 3: the word match first, so a prompt with no start verb never resolves the config.
        if (!HasStartVerb(Words(prompt)) || Resolved(c) is not { } resolved)
        {
            return null;
        }

        var config = c.Services.Config;
        var roles = config.ChainRoles(resolved).Where(r => config.RoleClass(r, resolved) != "orchestrate").ToList();
        if (AskedRole(prompt, roles) is not { } role)
        {
            return null;
        }

        var agent = $"`Agent(subagent_type:\"{config.PluginName}:{role}\")`, with the brief in the Agent prompt";
        return ConfigModule.DispatchMode(resolved) switch
        {
            "subagents" => $"To start a subagent for {role} (dispatch: subagents): {agent}.",
            "prefer-subagents" => $"To start a subagent for {role} (dispatch: prefer-subagents, the default): {agent}; a peer for it instead: `{SpawnCommand(c, resolved, role)}`.",
            _ => $"To start a peer for {role}: `{SpawnCommand(c, resolved, role)}`, once per member; brief each with `ah msg new` then `ah msg send`. Not the Agent tool and not herdr commands.",
        };
    }

    private static bool HasStartVerb(List<string> words) =>
        words.Where((w, i) => StartVerbs.Contains(w) || (UpVerbs.Contains(w) && i + 1 < words.Count && words[i + 1] == "up")).Any();
}
