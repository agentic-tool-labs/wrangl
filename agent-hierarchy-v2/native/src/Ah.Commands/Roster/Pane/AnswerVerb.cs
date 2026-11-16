using Ah.Launch;
using Ah.Abstractions;
using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Commands;

/// <summary>
/// <c>answer</c>: relays the user's choice to a prompt a non-claude member stopped at. It takes no keys and no text: it sends one
/// table row's keys, and only when the screen still shows the prompt, and that row, exactly as the user was shown them.
/// </summary>
internal static partial class AnswerVerb
{
    private static readonly string[] Flags = ["prompt", "choice", "cancel", "screen-hash", "team", "cwd", "request", "answer", "text", "user-choice"];

    private const string RelayMessage =
        "This prompt is the user's to answer. Tell the user, relay it with AskUserQuestion, then pass their pick to `answer`. A granting answer (Yes, Allow) goes only through that relay, on the user's explicit pick; never answer Yes or Allow for a member on your own. The only exceptions, each backed by a recorded user decision and enforced in code: the Codex trust auto-answer under a pre-approval the user recorded with `codex-trust --preapprove`, and a one-time Codex approval inside the member's work tree under codex_orchestrator_approvals. For the trust screen, pass --user-choice only when the user picked that option in this AskUserQuestion.";

    [GeneratedRegex("^[0-9a-f]{64}\\z")]
    private static partial Regex Sha256Hex();

    [Verb("roster:answer")]
    internal static int Answer(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, Run);

    private static int Run(RosterContext c)
    {
        foreach (var key in c.Opts.Keys)
        {
            if (!Flags.Contains(key))
            {
                throw new RosterFailure($"answer: unrecognized flag --{key} (use --prompt, --choice, --cancel, --screen-hash, --request, --answer, --text, --user-choice, --team, or --cwd)");
            }
        }

        if (c.Opts.Has("request"))
        {
            return RelayRequest(c);
        }

        return c.Opts.Has("cancel") ? Cancel(c) : Relay(c);
    }

    // ---------------------------------------------------------------- --request

    /// <summary>
    /// <c>--request</c> answers a server request an app-server Codex member is holding, through the daemon that holds it: the daemon
    /// proves who is calling and decides whether that caller may make this choice, so this sends nothing the daemon has not allowed.
    /// </summary>
    private static int RelayRequest(RosterContext c)
    {
        var opts = c.Opts;
        foreach (var other in (string[])["cancel", "prompt", "screen-hash", "user-choice"])
        {
            if (opts.Has(other))
            {
                throw new RosterFailure($"answer: --request cannot be combined with --{other}");
            }
        }

        var name = opts.Positionals.Count > 0 ? opts.Positionals[0] : null;
        if (string.IsNullOrEmpty(name))
        {
            throw new RosterFailure("answer: name the member — answer <member name> --request <id> (--choice <id> | --answer <qid>=<text>)");
        }

        var requestId = opts.Text("request");
        if (string.IsNullOrEmpty(requestId))
        {
            throw new RosterFailure("answer: --request requires the request id the member-request mail named");
        }

        var answers = new List<KeyValuePair<string, string>>();
        foreach (var raw in c.RepeatedFlag("answer"))
        {
            var cut = raw.IndexOf('=', StringComparison.Ordinal);
            if (cut <= 0)
            {
                throw new RosterFailure($"answer: --answer takes <qid>=<text>, not {PaneMembers.Js(JsonValue.FromString(raw))}");
            }

            answers.Add(new(raw[..cut], raw[(cut + 1)..]));
        }

        var choice = opts.Text("choice");
        if (choice is null && answers.Count == 0)
        {
            throw new RosterFailure("answer: give --choice <id> for an approval, or --answer <qid>=<text> for a question");
        }

        var service = c.Services.CodexAnswers ?? throw new RosterFailure("answer: the daemon route for Codex requests is not composed");
        var teamPath = c.Services.Roster.TeamPath(c.Dir, c.TeamFile);
        var team = Path.GetFileNameWithoutExtension(teamPath);
        var reply = service.Answer(team, name, requestId, choice, answers, opts.Text("text"));
        if (reply.Body is not { } body)
        {
            throw new RosterFailure($"answer: {reply.Failure}");
        }

        var info = new JsonObject { ["name"] = name, ["kind"] = "codex", ["request"] = requestId };
        var result = new JsonObject { ["status"] = body.Get("status") ?? JsonValue.Null };
        RosterJson.Spread(result, info);
        foreach (var key in body.Keys.Where(k => k != "status"))
        {
            result[key] = body.Get(key)!.Value;
        }

        if (body.Get("status") is { IsText: true } status && status.AsString() == "needs-user")
        {
            result["message"] = RelayMessage;
        }

        c.Out(result);
        return 0;
    }

    /// <summary>The work tree the team's grants record for the member, which is where its Codex was launched; null when there is no readable record.</summary>
    private static string? MemberWorkRoot(RosterContext c, string member)
    {
        try
        {
            var grants = Ah.Config.ConfigModule.CreateWorktreeGrants(c.Services.Files, c.Services.Environment);
            var view = grants.Read(c.TeamHome, WorkTreeAssignment.TeamOf(c));
            return view.Members.TryGetValue(member, out var granted) && granted.WorkRoot.Length > 0 ? granted.WorkRoot : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The one place the answer verb presses keys: a table row's keys, or the single Esc of <c>--cancel</c>.</summary>
    internal static void PressKeys(RosterContext c, string name, IEnumerable<string> keys)
    {
        var herdr = PaneMembers.Launch(c, "answer").Herdr;
        try
        {
            foreach (var key in keys)
            {
                herdr.Invoke(["agent", "send-keys", name, key]);
            }
        }
        catch (InvalidOperationException e)
        {
            throw new RosterFailure(e.Message);
        }
    }

    private static JsonObject Info(string name, string kind) => new() { ["name"] = name, ["kind"] = kind };

    private static JsonObject Say(string status, JsonObject info, params (string Key, JsonValue Value)[] extra)
    {
        var o = new JsonObject { ["status"] = status };
        RosterJson.Spread(o, info);
        foreach (var (key, value) in extra)
        {
            o[key] = value;
        }

        return o;
    }

    private static JsonValue Status(string? s) => s is null ? JsonValue.Null : s;

    private static void ClearBlocked(RosterContext c, string dir, string member) =>
        c.Services.Hierarchy.Roster.AppendGateRouted(
            dir,
            new JsonObject
            {
                ["type"] = "blocked-clear",
                ["team"] = c.TeamFile is null ? JsonValue.Null : c.TeamFile,
                ["member"] = member,
                ["by"] = "answer",
            });

    // ---------------------------------------------------------------- --cancel

    /// <summary>
    /// <c>--cancel</c> presses Esc once, on a Claude member that is blocked at a dialog right now and whose screen is still the one
    /// the caller saw. It sends nothing else, ever.
    /// </summary>
    private static int Cancel(RosterContext c)
    {
        var opts = c.Opts;
        if (!opts.IsTrue("cancel"))
        {
            throw new RosterFailure("answer: --cancel takes no value");
        }

        if (opts.Has("choice") || opts.Has("prompt"))
        {
            throw new RosterFailure("answer: --cancel cannot be combined with --choice or --prompt; it only ever sends Esc");
        }

        var name = opts.Positionals.Count > 0 ? opts.Positionals[0] : null;
        var hash = opts.Text("screen-hash");
        if (hash is null || !Sha256Hex().IsMatch(hash))
        {
            throw new RosterFailure("answer --cancel: --screen-hash <hash> is required: the screen_hash the daemon reported");
        }

        if (string.IsNullOrEmpty(name))
        {
            throw new RosterFailure("answer: name the member — answer <member name> --cancel");
        }

        var launch = PaneMembers.Launch(c, "answer");
        var dir = c.Dir;
        var roster = c.Services.Roster;
        var teamPath = roster.TeamPath(dir, c.TeamFile);
        var team = roster.ReadTeamRecord(dir, c.TeamFile);
        if (!team.IsMap)
        {
            throw new RosterFailure($"answer: there is no team at {teamPath} — pass --team <name> for the team {name} is in");
        }

        var memberValue = team.Member("members") is { IsArray: true } list
            ? list.AsArray().FirstOrDefault(m => m.IsMap && m.Member("name") is { IsText: true } n && n.AsString() == name)
            : default;
        if (!memberValue.IsMap)
        {
            throw new RosterFailure($"answer: {teamPath} has no member named {PaneMembers.Js(JsonValue.FromString(name))}");
        }

        var member = memberValue.AsObject();
        var kind = MemberPlanning.JsConvertText(c.Services.Config.ResolveKind(member));
        var info = Info(name, kind);
        if (kind != launch.Registry.KindDefault)
        {
            c.Out(Say(
                "cancel-unsupported",
                info,
                ("why", $"Esc is Claude Code's dismiss key, not {kind}'s: relay this prompt to the user instead"),
                ("message", RelayMessage)));
            return 0;
        }

        var transport = member.Get("transport") is { IsNull: false } mt ? mt : team.Member("transport");
        var paneId = member.Get("transport_id") is { IsText: true } tid ? tid.AsString() : string.Empty;
        if (!(transport is { IsText: true } tt && tt.AsString() == "herdr") || !LaunchModule.IsHerdrPaneId(paneId))
        {
            c.Out(Say(
                "cancel-unsupported",
                info,
                ("why", "answer --cancel sends its key through herdr, and this member has no herdr pane"),
                ("message", RelayMessage)));
            return 0;
        }

        var state = PaneMembers.HerdrAgentState(c, name);
        if (state.Indeterminate)
        {
            c.Out(Say("indeterminate", info, ("agent_status", Status(state.AgentStatus)), ("why", state.Why ?? string.Empty)));
            return 0;
        }

        if (!state.Live)
        {
            c.Out(Say("not-live", info, ("agent_status", JsonValue.Null)));
            return 0;
        }

        var seen = PaneMembers.ReadPrompt(c, member, state.AgentStatus);
        if (!(seen.Get("screen_hash") is { IsText: true } sh && sh.AsString() == hash))
        {
            var changed = Say("screen-changed", info, ("agent_status", Status(state.AgentStatus)));
            RosterJson.Spread(changed, PaneMembers.BlockedFields(seen));
            c.Out(changed);
            return 0;
        }

        // A stray Esc in a Claude session that is working interrupts its turn, so this check carries the weight.
        var hierarchy = c.Services.Hierarchy;
        var described = hierarchy.Status.DescribeMembers(dir, team, hierarchy.Roster.ReadRoster(dir), c.TeamFile);
        var recorded = described.FirstOrDefault(m => m.IsMap && m.Member("name") is { IsText: true } n && n.AsString() == name);
        var recordedBlocked = recorded.IsMap && recorded.Member("activity") is { IsText: true } act && act.AsString() == "blocked";
        if (state.AgentStatus != "blocked" && !recordedBlocked)
        {
            c.Out(Say("not-blocked", info, ("agent_status", Status(state.AgentStatus))));
            return 0;
        }

        PressKeys(c, name, ["esc"]);
        var after = PaneMembers.HerdrAgentState(c, name);
        // A cancel ends the member's blocked episode, so the daemon reports the next block as a new one.
        if (after.AgentStatus != "blocked")
        {
            ClearBlocked(c, dir, name);
        }

        var stillBlocked = after.AgentStatus == "blocked";
        var result = Say(stillBlocked ? "still-blocked" : "cancelled", info, ("agent_status", Status(after.AgentStatus)));
        if (!after.Indeterminate)
        {
            result["live"] = after.Live;
        }

        if (stillBlocked)
        {
            result["message"] = RelayMessage;
        }

        c.Out(result);
        return 0;
    }

    // ---------------------------------------------------------------- a prompt's answer

    private static int Relay(RosterContext c)
    {
        var opts = c.Opts;
        var screens = PaneMembers.Panes(c, "answer").Screens;
        var dir = c.Dir;
        var member = PaneMembers.PaneMemberOrFail(c, dir, opts.Positionals.Count > 0 ? opts.Positionals[0] : null, "answer");
        var kind = MemberPlanning.JsConvertText(c.Services.Config.ResolveKind(member));
        var prompt = opts.Text("prompt");
        var choice = opts.Text("choice");
        var hash = opts.Text("screen-hash");
        var relayed = screens.RelayedPrompts(kind);
        string RelayedList() => relayed.Count == 0 ? "(none)" : string.Join(", ", relayed);
        if (prompt is null)
        {
            throw new RosterFailure($"answer: --prompt <blocked_by> is required — {kind} prompts that can be answered: {RelayedList()}");
        }

        var rows = screens.PromptRows(kind, prompt);
        if (rows is null || rows.Count == 0)
        {
            throw new RosterFailure(
                $"answer: {kind} has no answers to relay for prompt {PaneMembers.Js(JsonValue.FromString(prompt))} — the prompts it relays: {RelayedList()}");
        }

        if (choice is null)
        {
            throw new RosterFailure($"answer: --choice <id> is required — the ids for {prompt}: {string.Join(", ", rows.Select(r => r.Id))}");
        }

        var row = rows.FirstOrDefault(r => r.Id == choice);
        if (row is null)
        {
            throw new RosterFailure(
                $"answer: {PaneMembers.Js(JsonValue.FromString(choice))} is not an answer to {prompt} — the ids are: {string.Join(", ", rows.Select(r => r.Id))}");
        }

        if (hash is null || !Sha256Hex().IsMatch(hash))
        {
            throw new RosterFailure("answer: --screen-hash <hash> is required: the screen_hash that deliver or spawn reported with the prompt");
        }

        var name = MemberPlanning.JsConvertText(member.Get("name"));
        var info = new JsonObject { ["name"] = member.Get("name") ?? JsonValue.Null, ["prompt"] = prompt, ["choice"] = choice };
        var state = PaneMembers.HerdrAgentState(c, name);
        if (state.Indeterminate)
        {
            c.Out(Say("indeterminate", info, ("agent_status", Status(state.AgentStatus)), ("why", state.Why ?? string.Empty)));
            return 0;
        }

        if (!state.Live)
        {
            c.Out(Say("not-live", info, ("agent_status", JsonValue.Null)));
            return 0;
        }

        var now = PaneMembers.ReadPrompt(c, member, state.AgentStatus);
        var recognized = now.Get("recognized") is { IsText: true } r ? r.AsString() : null;
        var nowHash = now.Get("screen_hash") is { IsText: true } h ? h.AsString() : null;
        if (recognized != prompt || nowHash != hash)
        {
            var changed = Say("screen-changed", info, ("agent_status", Status(state.AgentStatus)));
            RosterJson.Spread(changed, PaneMembers.BlockedFields(now));
            c.Out(changed);
            return 0;
        }

        if (!screens.RowOffered(kind, prompt, row.Id, now.Get("block")))
        {
            var offered = now.Get("options") is { IsArray: true } options
                ? string.Join(", ", options.AsArray().Select(o => MemberPlanning.JsConvertText(o.Member("id"))))
                : string.Empty;
            throw new RosterFailure(
                $"answer: {choice} was not offered on this screen, so nothing was sent — the ids offered on it: {(offered.Length == 0 ? "(none)" : offered)}");
        }

        JsonObject? autoAnswered = null;
        if (kind == "codex" && prompt == "trust-folder" && row.Grants && !opts.IsTrue("user-choice"))
        {
            var seenScreen = now.Get("screen") is { IsText: true } st ? st.AsString() : string.Empty;
            var launchCwd = MemberWorkRoot(c, name);
            var auto = CodexPreapproval.Of(c);
            var decision = launchCwd is null
                ? new JsonObject { ["answer"] = false, ["reason"] = "no-work-tree-record" }
                : auto.Decide(seenScreen, launchCwd);
            if (launchCwd is null || decision.Get("answer") is not { IsTruthy: true })
            {
                c.Out(Say(
                    "needs-user",
                    info,
                    ("why", $"trusting a folder is the user's to grant, and no pre-approval covers this screen ({MemberPlanning.JsConvertText(decision.Get("reason"))}); nothing was sent"),
                    ("message", RelayMessage)));
                return 0;
            }

            // A failed send is logged as send-failed first and then surfaced.
            Exception? sendFailure = null;
            autoAnswered = auto.Answer(
                decision,
                name,
                WorkTreeAssignment.TeamOf(c),
                JsonValue.FromString(hash),
                launchCwd,
                () =>
                {
                    try
                    {
                        PressKeys(c, name, row.Keys);
                        return true;
                    }
                    catch (Exception e) when (e is RosterFailure or IOException)
                    {
                        sendFailure = e;
                        return false;
                    }
                },
                () => PaneMembers.Launch(c, "answer").Launcher.ComposerUpAfterAnswer(member, c.LaunchContext()).Get("go") is { IsTruthy: true });
            if (sendFailure is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(sendFailure).Throw();
            }
        }
        else
        {
            PressKeys(c, name, row.Keys);
        }

        // One look afterwards. A prompt still shown is reported, never answered again; an agent that is gone (distrust quits
        // Codex) is reported not live.
        c.Services.Hierarchy.Status.RecordActivityRouted(dir, $"pane-{name}", new JsonObject { ["activity"] = "working" });
        var after = PaneMembers.HerdrAgentState(c, name);
        // An answered prompt ends the member's blocked episode, as a cancel does: its next block is a new one.
        if (after.AgentStatus != "blocked")
        {
            ClearBlocked(c, dir, name);
        }

        var read = PaneMembers.ReadPrompt(c, member, after.AgentStatus);
        var result = Say("answered", info, ("agent_status", Status(after.AgentStatus)));
        if (autoAnswered is not null)
        {
            result["trust_autoanswer"] = autoAnswered;
            if (autoAnswered.Get("result") is { IsText: true } outcome && outcome.AsString() != "answered")
            {
                result["status"] = outcome;
            }
        }

        result["live"] = after.Indeterminate ? JsonValue.Null : after.Live;
        result["prompt_after"] = read.Get("recognized") ?? JsonValue.Null;
        result["screen"] = read.Get("screen") ?? JsonValue.Null;
        c.Out(result);
        return 0;
    }
}
