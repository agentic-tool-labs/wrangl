using Ah.Core.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Launch;
using Ah.Mesh;

namespace Ah.Commands;

/// <summary>
/// <c>deliver</c>: the one way a brief reaches a non-claude member, since SendMessage cannot reach it. Nothing is typed into a
/// member that is working, stopped at a prompt, or not ready, and the report is only ever the response file.
/// </summary>
internal static class DeliverVerb
{
    private static readonly string[] Flags = ["req", "ping", "wait-only", "timeout", "team", "cwd"];

    /// <summary>The longest one-line brief an explicit deliver will paste; a longer one is refused, never cut.</summary>
    private const int MaxChars = 1000;

    [Verb("roster:deliver")]
    internal static int Deliver(string[] rest, TextWriter stdout, TextWriter stderr) =>
        RosterCli.Execute(rest, stdout, stderr, c => new Run(c).Execute());

    private sealed class Run(RosterContext c)
    {
        private readonly IRosterLaunch _launch = PaneMembers.Launch(c, "deliver");
        private readonly IRosterPanes _panes = PaneMembers.Panes(c, "deliver");
        private readonly JsonObject _base = new();
        private PaneMembers.ActivityObserver _seen = null!;
        private JsonObject _member = null!;
        private string _dir = string.Empty;
        private string _req = string.Empty;
        private string _response = string.Empty;
        private JsonObject _plan = null!;
        private JsonObject? _attemptRow;
        private bool _attemptLogged;
        private bool _waitOnly;
        private int? _ping;
        private bool _tmux;
        private string _pane = string.Empty;
        private DateTimeOffset _deadline;
        private double _pollMs;
        private string _self = string.Empty;

        private string MemberName => MemberPlanning.JsConvertText(_member.Get("name"));

        internal int Execute()
        {
            foreach (var key in c.Opts.Keys)
            {
                if (!Flags.Contains(key))
                {
                    throw new RosterFailure($"deliver: unrecognized flag --{key} (use --req, --ping, --wait-only, --timeout, --team, or --cwd)");
                }
            }

            _dir = c.Dir;
            _member = PaneMembers.PaneMemberOrFail(c, _dir, c.Opts.Positionals.Count > 0 ? c.Opts.Positionals[0] : null, "deliver");
            _waitOnly = c.Opts.IsTrue("wait-only");
            if (!_waitOnly)
            {
                MeshGate();
            }

            _seen = new PaneMembers.ActivityObserver(c, _dir, _member);
            if (c.Opts.Text("req") is not { } req)
            {
                throw new RosterFailure("deliver: --req <abs request path> is required");
            }

            _req = req;
            var messages = c.Services.Hierarchy.Messages;
            var plan = messages.ResponsePlan(req);
            if (!plan.IsMap || !c.Services.Hierarchy.Core.MsgExists(req))
            {
                throw new RosterFailure($"deliver: --req must be the absolute path of an existing request file, got {PaneMembers.Js(JsonValue.FromString(req))}");
            }

            _plan = plan.AsObject();
            if (!_waitOnly)
            {
                var missing = messages.ReviewIntentGap(req, c.Registry);
                if (missing.IsTruthy)
                {
                    throw new RosterFailure(messages.ReviewIntentReason(req, missing.IsArray ? missing.AsArray() : new JsonArray(), "re-run deliver"));
                }
            }

            ReadPingAndTimeout(out var timeoutSec);

            _response = MemberPlanning.JsConvertText(_plan.Get("path"));
            _base["name"] = _member.Get("name") ?? JsonValue.Null;
            _base["request"] = req;
            _base["response"] = _response;
            var team = c.Services.Roster.ReadTeamRecord(_dir, c.TeamFile);
            var teamTransport = team.IsMap && team.Member("transport") is { IsTruthy: true } t && t.IsText ? t.AsString() : _launch.Transports.Detect();
            var place = _panes.Paste.PlacementOf(_member, teamTransport);
            var placeTransport = place.Get("transport") is { IsText: true } pt ? pt.AsString() : null;
            _tmux = placeTransport == "tmux";
            _pane = place.Get("pane") is { IsText: true } pn ? pn.AsString() : string.Empty;
            if (!_waitOnly)
            {
                _attemptRow = new JsonObject
                {
                    ["source"] = "deliver",
                    ["team"] = team.IsMap && team.Member("team_id") is { IsTruthy: true } id ? id : JsonValue.Null,
                    ["member"] = _member.Get("name") ?? JsonValue.Null,
                    ["transport"] = place.Get("transport") ?? JsonValue.Null,
                    ["pane"] = place.Get("pane") ?? JsonValue.Null,
                };
            }

            var slug = _plan.Get("fields") is { IsMap: true } fields ? MemberPlanning.JsConvertText(fields.Member("slug")) : "undefined";
            var text = _ping is { } n
                ? $"Ping {n}/3: you owe a report on task {slug} — write it to {_response}, then end your turn."
                : string.Join(
                    " · ",
                    $"[hierarchy-msg {req}]",
                    $"Report to: {_response}",
                    $"Standing instructions: {_launch.Shapes.InstructionsPath(MemberName, c.LaunchContext())} — read it first if you have not read it this session.");
            if (place.Get("pasteable") is not { IsTruthy: true })
            {
                return Report(Result("not-sent", false, JsonValue.Null, ("message", $"no addressable pane (transport {(string.IsNullOrEmpty(placeTransport) ? "none" : placeTransport)})")));
            }

            if (!_waitOnly && _member.Get("paste") is { IsBool: true } paste && !paste.AsBool())
            {
                return Report(Result("not-sent", false, JsonValue.Null, ("message", "paste disabled for this member")));
            }

            if (!_waitOnly && text.Length > MaxChars)
            {
                return Report(Result("not-sent", false, JsonValue.Null, ("message", $"brief too long to paste ({text.Length} chars)")));
            }

            _self = c.SelfCommand;
            _deadline = c.Services.Clock.UtcNow.AddSeconds(timeoutSec);
            var poll = c.EnvText("AH_DELIVER_POLL_MS");
            _pollMs = poll is null ? 2000 : JsConvert.ToNumber(JsonValue.FromString(poll));
            return Deliver(text, place, placeTransport);
        }

        // ---------------------------------------------------------------- argument checks

        /// <summary>The mesh's send rule: a deliver is a message to the member, so a policy that holds it holds the deliver.</summary>
        private void MeshGate()
        {
            var sessionId = c.EnvText("CLAUDE_CODE_SESSION_ID");
            if (sessionId is null)
            {
                throw new RosterFailure(
                    "deliver: this session's id is unknown (CLAUDE_CODE_SESSION_ID is unset), so the mesh send rule cannot be applied and nothing was sent");
            }

            var socket = c.Env("CLAUDE_CODE_MESSAGING_SOCKET");
            var context = new RequestContext(
                c.Cwd,
                _dir,
                c.Services.Config.MainHierarchyDir(c.Cwd) ?? string.Empty,
                string.IsNullOrEmpty(socket) ? string.Empty : Path.GetDirectoryName(socket) ?? string.Empty);
            var outcome = _panes.Mesh.Check(new MeshGateRequest(context, sessionId, MemberName, string.Empty, "deliver", CallerPid: Environment.ProcessId));
            if (!outcome.Ok)
            {
                throw new RosterFailure(outcome.Failure!.Message);
            }

            if (outcome.Value is not { } gate)
            {
                return;
            }

            if (!gate.Verdict.Allow)
            {
                throw new RosterFailure(gate.Verdict.Message ?? string.Empty);
            }

            try
            {
                gate.After?.Invoke();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new RosterFailure($"ah: {e.Message}");
            }
        }

        private void ReadPingAndTimeout(out double timeoutSec)
        {
            var opts = c.Opts;
            if (opts.Has("ping"))
            {
                // a bare --ping reads as the number true is: 1
                var number = opts.IsTrue("ping") ? 1 : JsConvert.ToNumber(JsonValue.FromString(opts.Text("ping")!));
                if (!RosterContext.IsInteger(number) || number < 1 || number > 3)
                {
                    var shown = opts.IsTrue("ping") ? "true" : PaneMembers.Js(JsonValue.FromString(opts.Text("ping")!));
                    throw new RosterFailure($"deliver: --ping takes 1, 2 or 3, got {shown}");
                }

                if (_waitOnly)
                {
                    throw new RosterFailure("deliver: --ping and --wait-only cannot be combined — --wait-only sends nothing");
                }

                _ping = (int)number;
            }

            timeoutSec = 1800;
            if (opts.Has("timeout"))
            {
                timeoutSec = opts.IsTrue("timeout") ? 1 : JsConvert.ToNumber(JsonValue.FromString(opts.Text("timeout")!));
                if (!RosterContext.IsInteger(timeoutSec) || timeoutSec < 1)
                {
                    var shown = opts.IsTrue("timeout") ? "true" : PaneMembers.Js(JsonValue.FromString(opts.Text("timeout")!));
                    throw new RosterFailure($"deliver: --timeout takes a whole number of seconds, got {shown}");
                }
            }
        }

        // ---------------------------------------------------------------- results

        /// <summary><c>{status, sent, ...base, agent_status, ...extra}</c>.</summary>
        private JsonObject Result(string status, bool sent, JsonValue agentStatus, params (string Key, JsonValue Value)[] extra)
        {
            var o = new JsonObject { ["status"] = status, ["sent"] = sent };
            RosterJson.Spread(o, _base);
            o["agent_status"] = agentStatus;
            foreach (var (key, value) in extra)
            {
                o[key] = value;
            }

            return o;
        }

        private static JsonValue Status(string? s) => s is null ? JsonValue.Null : s;

        /// <summary>Records the attempt a run that ended before anything was typed leaves, then prints the result.</summary>
        private int Report(JsonObject res)
        {
            if (_attemptRow is not null && !_attemptLogged)
            {
                _attemptLogged = true;
                var row = _attemptRow.Clone();
                var status = res.Get("status") ?? JsonValue.Null;
                row["outcome"] = status is { IsText: true } st && st.AsString() == "mailed" ? "mailed" : "refused";
                row["reason"] = status;
                row["readiness"] = res.Get("blocked_by") is { IsTruthy: true } b ? b : status;
                _panes.RecordDeliverAttempt(row);
            }

            _seen.Final();
            var sentBrief = !_waitOnly && _ping is null && res.Get("sent") is { IsBool: true } s && s.AsBool() && res.Get("message") is null;
            if (sentBrief)
            {
                res = res.Clone();
                res["message"] = $"This member's report is its response file only; nothing printed in its pane counts as the report. Statuses and the relay: {PaneMembersDoc()}.";
            }

            c.Out(res);
            return 0;
        }

        private string PaneMembersDoc()
        {
            var root = c.PluginRoot;
            return Path.Combine(root, "skills", "agent-team", "reference", "pane-members.md");
        }

        private JsonObject NotLive(bool sent)
        {
            _seen.See("agent_not_found");
            var o = Result("not-live", sent, JsonValue.Null);
            RosterJson.Spread(o, PaneMembers.RespawnCommand(c, _dir, _member));
            return o;
        }

        private JsonObject Blocked(string? agentStatus, JsonObject p, bool sent)
        {
            _seen.See("blocked", p);
            var b = PaneMembers.BlockedFields(p);
            var next = sent
                ? $"run {_self} deliver {MemberName} --req {RosterContext.ArgWord(_req)} --wait-only --cwd {RosterContext.ArgWord(c.Cwd)} in the background"
                : $"re-run this same command in the background: {c.RerunCommand((_, _) => false, string.Empty).Trim()}";
            var o = Result("blocked", sent, Status(agentStatus));
            RosterJson.Spread(o, b);
            o["message"] = _launch.Launcher.RelayMessage(_member, b, next, c.LaunchContext());
            return o;
        }

        private JsonObject Evaluate(string? agentStatus, bool sent)
        {
            _seen.See(agentStatus);
            var id = _plan.Get("fields") is { IsMap: true } f ? MemberPlanning.JsConvertText(f.Member("id")) : "undefined";
            var status = c.Services.Hierarchy.Messages.ReportStatus(_response, id);
            if (status == "no-report")
            {
                return Result(status, sent, Status(agentStatus), ("pane_tail", _tmux ? TmuxTail() : PaneMembers.PaneTail(c, _member)));
            }

            return Result(status, sent, Status(agentStatus));
        }

        private JsonValue TmuxTail()
        {
            var capture = PaneMembers.TmuxCapture(c, _pane);
            return capture is null ? JsonValue.Null : PaneMembers.TailLines(capture);
        }

        // ---------------------------------------------------------------- polling

        private JsonObject? PollUntil(Func<JsonObject?> check)
        {
            for (;;)
            {
                var result = check();
                if (result is not null)
                {
                    return result;
                }

                var left = (_deadline - c.Services.Clock.UtcNow).TotalMilliseconds;
                if (left <= 0)
                {
                    return null;
                }

                _panes.Sleeper.Sleep(TimeSpan.FromMilliseconds(Math.Min(_pollMs, left)));
            }
        }

        private bool NoStatus(PaneState s) => !_tmux && (s.AgentStatus == "unknown"
            || (s.Indeterminate && s.AgentStatus is null && (s.Why ?? string.Empty).EndsWith(" returned no agent_status", StringComparison.Ordinal)));

        private double EnvMs(string name, double fallback) => c.EnvText(name) is { } v ? JsConvert.ToNumber(JsonValue.FromString(v)) : fallback;

        private PaneState State() => _tmux ? PaneMembers.TmuxAgentState(c, _member, _pane) : PaneMembers.HerdrAgentState(c, MemberName);

        // ---------------------------------------------------------------- the delivery

        private int Deliver(string text, JsonObject place, string? placeTransport)
        {
            PaneState? state = null;
            var pre = PollUntil(() =>
            {
                state = State();

                // A fresh pane is found before herdr gives it an agent_status (b353): re-read it briefly, as the paste does.
                // Still none: nothing is typed.
                var statusBy = c.Services.Clock.UtcNow.AddMilliseconds(EnvMs("AH_PASTE_STATUS_WAIT_MS", 3000));
                while (NoStatus(state) && c.Services.Clock.UtcNow < statusBy)
                {
                    _panes.Sleeper.Sleep(TimeSpan.FromMilliseconds(EnvMs("AH_PASTE_POLL_MS", 200)));
                    state = State();
                }

                if (NoStatus(state))
                {
                    return Result(
                        "mailed",
                        false,
                        JsonValue.Null,
                        ("message", "Herdr gives the member no agent_status yet, so nothing was typed. Send the brief by mail; the member reads its mailbox on its own."));
                }

                if (state.Indeterminate)
                {
                    return Result("indeterminate", false, Status(state.AgentStatus), ("why", state.Why ?? string.Empty));
                }

                if (!state.Live)
                {
                    return NotLive(false);
                }

                // Herdr reports a harness idle and ready while it shows its own startup screens, so what is on screen is
                // checked too. A working member shows no prompt herdr would not call blocked.
                var onScreen = state.AgentStatus == "working"
                    ? null
                    : _tmux ? state.Prompt : PaneMembers.ReadPrompt(c, _member, state.AgentStatus);
                if (onScreen?.Get("blocked_by") is { IsTruthy: true })
                {
                    return Blocked(state.AgentStatus, onScreen, false);
                }

                _seen.See(state.AgentStatus);
                if (_waitOnly)
                {
                    return new JsonObject { ["go"] = true };
                }

                if (state.AgentStatus == "working" || !state.Ready)
                {
                    return null;
                }

                // A brief or ping is typed only into the idle, empty composer.
                var screen = _tmux
                    ? _launch.Launcher.ComposerOrPrompt(
                        _member,
                        state.AgentStatus,
                        c.LaunchContext(),
                        onScreen,
                        () => State().AsHerdr(),
                        _ => State().Prompt ?? new JsonObject())
                    : _launch.Launcher.ComposerOrPrompt(_member, state.AgentStatus, c.LaunchContext(), onScreen);
                if (screen.Get("busy") is { IsTruthy: true })
                {
                    return null;
                }

                if (screen.Get("reason") is { IsText: true } reason && reason.AsString() == "unrecognised-screen")
                {
                    return Result(
                        "indeterminate",
                        false,
                        Status(state.AgentStatus),
                        ("reason", "unrecognised-screen"),
                        ("why", screen.Get("why") ?? JsonValue.Null));
                }

                return screen.Get("go") is { IsTruthy: true } ? screen : Blocked(state.AgentStatus, screen, false);
            });
            if (pre is null)
            {
                return Report(Result(
                    "busy",
                    false,
                    Status(state?.AgentStatus),
                    ("message", "The member stayed working or not ready until --timeout, so nothing was sent. Re-run this same command.")));
            }

            if (pre.Get("go") is not { IsTruthy: true })
            {
                return Report(pre);
            }

            if (_waitOnly)
            {
                return WaitOnly(state!);
            }

            return Send(text, placeTransport);
        }

        private int WaitOnly(PaneState state)
        {
            // No response file means no brief was ever delivered for this request: that is not a report owed, and never counts
            // toward the pings.
            if (!c.Services.Hierarchy.Core.MsgExists(_response))
            {
                return Report(Result(
                    "not-sent",
                    false,
                    Status(state.AgentStatus),
                    ("message", "Nothing was delivered for this request: send the brief, without --wait-only.")));
            }

            if (state.AgentStatus != "working")
            {
                return Report(Evaluate(state.AgentStatus, false));
            }

            if (_tmux)
            {
                return Report(WaitTmuxTurn(false));
            }

            var call = Call(["agent", "wait", MemberName]);
            if (call.Error is not null)
            {
                return Report(Result(
                    "timeout",
                    false,
                    JsonValue.Null,
                    ("why", call.Error),
                    ("pane_tail", PaneMembers.PaneTail(c, _member))));
            }

            return Report(Finish(call.Raw!, call.Args, false));
        }

        private int Send(string text, string? placeTransport)
        {
            var messages = c.Services.Hierarchy.Messages;
            if (!c.Services.Hierarchy.Core.MsgExists(_response))
            {
                try
                {
                    var fields = _plan.Get("fields") is { IsMap: true } f ? f : (JsonValue)new JsonObject();
                    messages.CreateMessage(
                        _dir,
                        new JsonObject
                        {
                            ["reqPath"] = _req,
                            ["cwd"] = c.Cwd,
                            ["type"] = "response",
                            ["id"] = fields.Member("id") ?? JsonValue.Null,
                            ["from"] = _member.Get("role") ?? JsonValue.Null,
                            ["fromName"] = _member.Get("name") ?? JsonValue.Null,
                        });
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    if (_attemptRow is not null && !_attemptLogged)
                    {
                        _attemptLogged = true;
                        var row = _attemptRow.Clone();
                        row["outcome"] = "refused";
                        row["reason"] = "response-file";
                        row["readiness"] = "response-file";
                        _panes.RecordDeliverAttempt(row);
                    }

                    throw new RosterFailure($"deliver: {e.Message}");
                }
            }

            var attempt = _attemptRow!;
            _attemptLogged = true;
            var teamName = attempt.Get("team") is { IsTruthy: true } t ? MemberPlanning.JsConvertText(t) : "team";
            if (!LaunchModule.IsLockNamePart(teamName) || !LaunchModule.IsLockNamePart(MemberName))
            {
                var bad = attempt.Clone();
                bad["outcome"] = "refused";
                bad["reason"] = "bad-name";
                bad["readiness"] = "bad-name";
                _panes.RecordDeliverAttempt(bad);
                throw new RosterFailure("deliver: the team id or the member name holds characters a lock file name cannot — nothing was sent");
            }

            if (_tmux)
            {
                return SendTmux(text, teamName, placeTransport);
            }

            // The same per-member lock as every other paste, so two typists never overlap.
            var lockHandle = _panes.HoldPasteLock(teamName, MemberName);
            if (lockHandle is null)
            {
                var busy = attempt.Clone();
                busy["outcome"] = "refused";
                busy["reason"] = "lock-busy";
                busy["readiness"] = "lock-busy";
                _panes.RecordDeliverAttempt(busy);
                return Report(Result(
                    "busy",
                    false,
                    "working",
                    ("message", "Another paste to this member is in progress, so nothing was sent. Re-run this same command.")));
            }

            CallResult sentCall;
            try
            {
                sentCall = Call(["agent", "prompt", MemberName, text, "--wait"]);
            }
            finally
            {
                lockHandle.Dispose();
            }

            var logged = attempt.Clone();
            logged["outcome"] = sentCall.Raw is { Ok: true } ? "confirmed" : "unconfirmed";
            logged["readiness"] = "ready";
            _panes.RecordDeliverAttempt(logged);
            if (sentCall.Error is not null)
            {
                return Report(Result("timeout", true, JsonValue.Null, ("why", sentCall.Error), ("pane_tail", PaneMembers.PaneTail(c, _member))));
            }

            return Report(Finish(sentCall.Raw!, sentCall.Args, true));
        }

        private int SendTmux(string text, string teamName, string? placeTransport)
        {
            var r = _panes.Paste.Paste(new PasteOptions(_member, teamName, placeTransport, text, Source: "deliver", MaxChars: MaxChars));
            var outcome = r.Get("outcome") is { IsText: true } o ? o.AsString() : null;
            var reason = r.Get("reason") is { IsTruthy: true } rv ? MemberPlanning.JsConvertText(rv) : null;
            if (outcome == "confirmed")
            {
                return Report(WaitTmuxTurn(true));
            }

            if (outcome == "unconfirmed")
            {
                return Report(Result(
                    "indeterminate",
                    r.Get("entered") is { IsTruthy: true },
                    JsonValue.Null,
                    ("why", $"the paste was not confirmed ({reason ?? "undefined"})"),
                    ("pane_tail", TmuxTail())));
            }

            if (reason == "blocked")
            {
                var screen = PaneMembers.ReadPromptTmux(c, _member, _pane, PaneMembers.TmuxCapture(c, _pane));
                return Report(Blocked("blocked", screen, false));
            }

            if (reason is "busy" or "lock-busy")
            {
                return Report(Result(
                    "busy",
                    false,
                    "working",
                    ("message", "The member is not ready for a paste (a draft, a running turn, or another paste in progress), so nothing was sent. Re-run this same command.")));
            }

            if (reason == "indeterminate")
            {
                var why = r.Get("why") is { IsTruthy: true } w ? MemberPlanning.JsConvertText(w) : "the member's screen is not a recognised idle, empty composer";
                return Report(Result("indeterminate", false, JsonValue.Null, ("why", why)));
            }

            var message = reason == "paste-disabled"
                ? "paste disabled for this member"
                : reason == "no-pane"
                    ? $"no addressable pane (transport {(string.IsNullOrEmpty(placeTransport) ? "none" : placeTransport)})"
                    : $"the paste was refused ({reason ?? "undefined"})";
            return Report(Result("not-sent", false, JsonValue.Null, ("message", message)));
        }

        // ---------------------------------------------------------------- herdr calls

        private sealed record CallResult(string[] Args, HerdrRaw? Raw, string? Error);

        private CallResult Call(string[] args)
        {
            var ms = Math.Max(1000, (_deadline - c.Services.Clock.UtcNow).TotalMilliseconds);
            string[] withTimeout = [.. args, "--timeout", JsConvert.NumberToString(Math.Floor(ms))];
            try
            {
                return new CallResult(withTimeout, _launch.Herdr.InvokeAllowFailure(withTimeout, Math.Floor(ms) + 30000), null);
            }
            catch (InvalidOperationException e)
            {
                return new CallResult(withTimeout, null, e.Message);
            }
        }

        /// <summary>What a finished <c>herdr agent wait|prompt</c> says about the member's turn.</summary>
        private JsonObject Finish(HerdrRaw res, string[] args, bool sent)
        {
            var parsed = PaneMembers.HerdrJson(res);
            var code = parsed?.Member("error") is { IsMap: true } error && error.Member("code") is { IsTruthy: true } cv ? MemberPlanning.JsConvertText(cv) : null;
            if (!res.Ok)
            {
                if (code == "timeout")
                {
                    return Result("timeout", sent, JsonValue.Null, ("pane_tail", PaneMembers.PaneTail(c, _member)));
                }

                if (code == "agent_not_found")
                {
                    return NotLive(sent);
                }

                var detail = code ?? (JsText.Trim(res.Stderr).Length > 0 ? JsText.Trim(res.Stderr) : JsText.Trim(res.Stdout).Length > 0 ? JsText.Trim(res.Stdout) : $"exit {res.Status}");
                return Result("indeterminate", sent, JsonValue.Null, ("why", $"herdr agent {args[1]} {MemberName} failed: {detail}"));
            }

            string? agentStatus = parsed?.Member("result")?.Member("agent")?.Member("agent_status") is { IsText: true } st ? st.AsString() : null;
            // A turn that ended at an approval request is not over: the report is judged by the re-run after the user has answered.
            if (agentStatus == "blocked")
            {
                return Blocked(agentStatus, PaneMembers.ReadPrompt(c, _member, agentStatus), sent);
            }

            return Evaluate(agentStatus, sent);
        }

        /// <summary>The tmux twin of <c>herdr agent wait</c>: the same poll loop, until the member is no longer working.</summary>
        private JsonObject WaitTmuxTurn(bool sent)
        {
            PaneState? done = null;
            var result = PollUntil(() =>
            {
                var s = State();
                if (s.Indeterminate || s.AgentStatus != "working")
                {
                    done = s;
                    return new JsonObject();
                }

                return null;
            });
            if (result is null || done is null)
            {
                return Result("timeout", sent, "working", ("pane_tail", TmuxTail()));
            }

            if (done.Indeterminate)
            {
                return Result("indeterminate", sent, JsonValue.Null, ("why", done.Why ?? string.Empty));
            }

            if (done.AgentStatus == "blocked")
            {
                return Blocked("blocked", done.Prompt ?? new JsonObject(), sent);
            }

            return Evaluate(done.AgentStatus, sent);
        }
    }
}
