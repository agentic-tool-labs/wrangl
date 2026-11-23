using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Roster;

namespace Ah.Launch;

/// <summary>Where a stream spawn places its panes: <paramref name="Root"/> is an empty pane to launch into as is, <paramref name="Seed"/> a pane whose tab the split loop works in.</summary>
public sealed record PlacementAnchor(string? Root, string? Seed);

/// <summary>Options of a layout-and-launch: panes already placed by this team's earlier members, and a placement anchor.</summary>
/// <param name="SeedPanes">The team's existing panes the layout counts.</param>
/// <param name="Anchor">Where the new pane is placed; null: the layout's own choice.</param>
/// <param name="DeferRegistration">Return once each member is launched, without waiting for it to register; the caller confirms it later.</param>
public sealed record LayoutOptions(IReadOnlyList<string>? SeedPanes = null, PlacementAnchor? Anchor = null, bool DeferRegistration = false);

/// <summary>
/// What a layout-and-launch came to. <paramref name="Fail"/> is the message of a refusal that ends the command (exit 2);
/// <paramref name="Partial"/> is the report of a command that stopped with some panes made (exit 3); otherwise
/// <paramref name="Members"/> are the launch results aligned to the members given.
/// </summary>
public sealed record LayoutAndLaunchResult(IReadOnlyList<JsonObject> Members, string? Fail, JsonObject? Partial);

/// <summary>Launching members into panes: layout, launch with retry, the startup-screen check, and the screens a member can stop at.</summary>
public interface IMemberLauncher
{
    /// <summary>The split loop: for each pane to make, read the live geometry, pick the target, split. Stops at the first failure.</summary>
    LayoutLoopResult RunLayoutLoop(string mode, int paneCount, string self, string splitCwd, IReadOnlyList<string>? seedPanes = null, IReadOnlyList<string>? layoutArgs = null);

    /// <summary>
    /// Places <paramref name="members"/>' panes on <paramref name="transport"/>, assigns each member's <c>transport_id</c> (mutating the
    /// given objects), then launches each member. Results are aligned to <paramref name="members"/>. A member whose shape refuses is
    /// failed before any pane is made for it.
    /// </summary>
    LayoutAndLaunchResult LayoutAndLaunch(string dir, IReadOnlyList<JsonObject> members, string transport, string mode, string splitCwd, string callerLabel, LaunchContext context, LayoutOptions? options = null);

    /// <summary>
    /// Waits for a launched Claude member on herdr or tmux to register; one that does not within the wait is returned
    /// <c>launch_status: unconfirmed</c> with an <c>unconfirmed_reason</c>. Any other launch is returned as it is.
    /// </summary>
    JsonObject ConfirmRegistration(JsonObject launched, string transport, LaunchContext context, Func<string, string, bool>? registered = null, TimeSpan? wait = null);

    /// <summary>One member's launch, with the app-server steps for a Codex member that planned them and a plain fallback for each step that fails.</summary>
    JsonObject LaunchMember(JsonObject member, string transport, LaunchContext context);

    /// <summary>The launch itself: refusal, instructions file, shell, herdr-only retry, label and startup-screen check.</summary>
    JsonObject LaunchMemberCore(JsonObject member, string transport, LaunchContext context);

    /// <summary>
    /// The end of a spawn for a kind that can stop at a startup screen: once herdr reports the agent ready (or a bounded wait runs out), what is
    /// on screen decides. Spawn never sends a key.
    /// </summary>
    JsonObject CheckStartupScreen(JsonObject launched, JsonValue trust, LaunchContext context);

    /// <summary>
    /// The screen check before anything is typed into a member of a kind with a composer: <c>{go: true}</c>, or the read to report as
    /// blocked. <paramref name="first"/>, <paramref name="state"/> and <paramref name="read"/> default to the live reads.
    /// </summary>
    JsonObject ComposerOrPrompt(JsonValue member, string? agentStatus, LaunchContext context, JsonObject? first = null, Func<HerdrAgentState>? state = null, Func<string?, JsonObject>? read = null);

    /// <summary>
    /// The composer check after a trust answer was sent: waits once for the member to be ready, reads the screen, and while that read is
    /// still the Codex trust prompt, re-reads up to five times (<c>AH_COMPOSER_REREAD_MS</c> apart) because Codex may take a moment to
    /// leave it. The composer is up when the result carries <c>go</c>; any other prompt is returned at once.
    /// </summary>
    JsonObject ComposerUpAfterAnswer(JsonValue member, LaunchContext context);

    /// <summary>Herdr's state for the member once it is ready, or its last state when it is not ready within the launch wait (<c>AH_HERDR_READY_WAIT_MS</c>, 15 s by default).</summary>
    HerdrAgentState WaitUntilReady(string name);

    /// <summary>One <c>visible</c> read of a non-Claude member's screen, with the answers to offer.</summary>
    JsonObject ReadPrompt(JsonValue member, string? agentStatus, LaunchContext context);

    /// <summary>What the Orchestrator does about a member stopped at a prompt.</summary>
    string RelayMessage(JsonValue member, JsonObject blocked, string followUp, LaunchContext context);

    /// <summary>A failed herdr launch's pane: its recent output captured and the pane closed, or left open with its close command as the remedy.</summary>
    JsonObject CloseOrphanPane(JsonValue member, bool nameHeldElsewhere = false);

    /// <summary>Closes one member's pane by argv, never through a shell. Throws <see cref="InvalidOperationException"/> when it cannot.</summary>
    void CloseMemberPane(string transport, string transportId);

    /// <summary>The error code of a herdr launch failure (its JSON is on stdout), or null.</summary>
    string? HerdrErrorCode(ShellResult attempt);
}

/// <summary>The member launcher.</summary>
public sealed record MemberLauncher(
    IAhEnvironment Environment,
    IFileReader Files,
    IRawFileWriter Writer,
    IClock Clock,
    IHerdr Herdr,
    IMuxRunner Mux,
    IShellRunner Shell,
    IConfigCore Config,
    IScreenRules Screens,
    IScreenReading Reading,
    IKindHarnesses Harnesses,
    IMemberRegistry Registry,
    ICodexTrust Trust,
    ISpawnShapes Shapes,
    ILayoutRules Layout,
    ISleeper Sleeper,
    IActivityRecorder Activity,
    IAppserverLauncher Appserver,
    string RosterCommand,
    bool Parallel = true,
    IDetachedSpawner? Spawner = null,
    IProcessTable? Processes = null,
    IReadOnlyDictionary<string, string?>? ProcessEnvironment = null,
    Func<string, string, bool>? Registered = null,
    IHomeDocs? HomeDocs = null) : IMemberLauncher
{
    /// <summary>How long a launch that reported ready or dispatched waits for the member's registration before it reports unconfirmed.</summary>
    internal static readonly TimeSpan RegistrationWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A Claude member launched on herdr or tmux that does not register (<see cref="Registered"/>: name, team) within
    /// <see cref="RegistrationWait"/> is reported <c>unconfirmed</c>: it may have exited at boot or be waiting at a prompt. Report only.
    /// <paramref name="registered"/> and <paramref name="wait"/> default to <see cref="Registered"/> and <see cref="RegistrationWait"/>.
    /// </summary>
    public JsonObject ConfirmRegistration(JsonObject launched, string transport, LaunchContext context, Func<string, string, bool>? registered = null, TimeSpan? wait = null)
    {
        var isRegistered = registered ?? Registered;
        if (isRegistered is null || transport is not ("herdr" or "tmux")
            || launched.Get("launch_status") is not { IsText: true } status || status.AsString() is not ("ready" or "dispatched")
            || JsConvert.ToJsString(Config.ResolveKind(launched)) != Registry.KindDefault)
        {
            return launched;
        }

        var name = JsConvert.ToJsString(launched.Get("name"));
        var team = Config.TeamPrefix(context.Cwd, string.IsNullOrEmpty(context.TeamArg) ? null : context.TeamArg);
        var deadline = Clock.UtcNow + (wait ?? RegistrationWait);
        while (!isRegistered(name, team))
        {
            var left = deadline - Clock.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                var pane = launched.Get("transport_id") is { IsText: true } id ? id.AsString() : name;
                var reason = PaneScreen(transport, name, pane).Contains(ClaudeTrustMarker, StringComparison.Ordinal)
                    ? $"needs folder trust in {pane}"
                    : $"{name} launched ({status.AsString()}) but did not register as {name}@{team} within {RegistrationWait.TotalSeconds:0} s; it may have exited at boot or be waiting at a startup prompt";
                return With(launched, ("launch_status", "unconfirmed"), ("unconfirmed_reason", reason));
            }

            Sleeper.Sleep(TimeSpan.FromMilliseconds(Math.Min(500, left.TotalMilliseconds)));
        }

        return launched;
    }

    /// <summary>The confirm label of Claude Code's folder-trust screen (seen on 2.1.319 in a folder it had not trusted).</summary>
    internal const string ClaudeTrustMarker = "Yes, I trust this folder";

    /// <summary>What the member's pane shows now: herdr's visible read, or tmux's capture; empty when it cannot be read.</summary>
    private string PaneScreen(string transport, string name, string pane)
    {
        if (transport == "herdr")
        {
            return Reading.ReadVisibleScreen(name);
        }

        var shot = Mux.Run("tmux", ["capture-pane", "-p", "-t", pane], 0);
        return shot.Ok ? shot.Stdout : string.Empty;
    }

    public LayoutLoopResult RunLayoutLoop(string mode, int paneCount, string self, string splitCwd, IReadOnlyList<string>? seedPanes = null, IReadOnlyList<string>? layoutArgs = null)
    {
        var args = layoutArgs ?? ["--current"];
        var panes = new List<string>();
        var splits = new JsonArray();

        LayoutLoopResult Stopped(int i, string message, SplitDecision? attempted)
        {
            if (panes.Count == 0)
            {
                return new LayoutLoopResult(panes, splits, new LayoutFailure(false, message, null));
            }

            var payload = new JsonObject
            {
                ["panes"] = ToArray(panes),
                ["splits"] = splits,
                ["mode"] = mode,
                ["pane_count"] = paneCount,
                ["complete"] = false,
                ["failed_at"] = i,
                ["attempted"] = attempted is null ? JsonValue.Null : new JsonObject { ["target"] = attempted.Target is null ? JsonValue.Null : attempted.Target, ["direction"] = attempted.Direction },
                ["error"] = message,
            };
            return new LayoutLoopResult(panes, splits, new LayoutFailure(true, message, payload));
        }

        for (var i = 1; i <= paneCount; i++)
        {
            JsonValue geomResult;
            try
            {
                var layoutCall = new List<string> { "pane", "layout" };
                layoutCall.AddRange(args);
                geomResult = Herdr.Invoke(layoutCall);
            }
            catch (InvalidOperationException err)
            {
                return Stopped(i, err.Message, null);
            }

            if (geomResult.Member("result")?.Member("layout")?.Member("panes") is not { IsArray: true } geometryValue)
            {
                throw new InvalidOperationException($"Cannot read properties of undefined (reading '{MissingLink(geomResult, "result", "layout", "panes")}')");
            }

            var geometry = geometryValue.AsArray();
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in geometry)
            {
                if (g.Member("pane_id") is { IsText: true } id)
                {
                    present.Add(id.AsString());
                }
            }

            var liveSeed = new List<string>();
            foreach (var id in seedPanes ?? [])
            {
                if (id != self && present.Contains(id) && !liveSeed.Contains(id))
                {
                    liveSeed.Add(id);
                }
            }

            var created = new List<string>(liveSeed);
            created.AddRange(panes);
            var decision = Layout.NextSplit(mode, liveSeed.Count + paneCount, self, created, geometry, LayoutRules.ReportsPositions(geomResult));

            JsonValue splitResult;
            try
            {
                splitResult = Herdr.Invoke(["pane", "split", "--pane", decision.Target ?? "null", "--direction", decision.Direction, "--cwd", splitCwd, "--no-focus"]);
            }
            catch (InvalidOperationException err)
            {
                return Stopped(i, err.Message, decision);
            }

            if (splitResult.Member("result")?.Member("pane")?.Member("pane_id") is not { } newPane)
            {
                throw new InvalidOperationException($"Cannot read properties of undefined (reading '{MissingLink(splitResult, "result", "pane", "pane_id")}')");
            }

            var newPaneId = JsConvert.ToJsString(newPane);
            panes.Add(newPaneId);
            splits.Add(new JsonObject
            {
                ["i"] = i,
                ["target"] = decision.Target is null ? JsonValue.Null : decision.Target,
                ["direction"] = decision.Direction,
                ["pane_id"] = newPaneId,
            });
        }

        return new LayoutLoopResult(panes, splits, null);
    }

    public LayoutAndLaunchResult LayoutAndLaunch(string dir, IReadOnlyList<JsonObject> members, string transport, string mode, string splitCwd, string callerLabel, LaunchContext context, LayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(members);
        var refused = members.Where(IsRefused).ToList();
        var peers = members.Where(m => !IsRefused(m)).ToList();
        foreach (var m in refused)
        {
            m["transport_id"] = JsonValue.Null;
        }

        var panes = new List<string>();
        var hidden = false;
        if (transport == "herdr" && peers.Count > 0)
        {
            hidden = Herdr.Flavour == Ah.Core.Mux.HerdrFlavour.Core && options?.Anchor is not { Root: { Length: > 0 } } && !Herdr.AppPresent();
            if (!Herdr.OnPath())
            {
                return Failed("transport is herdr (HERDR_ENV=1) but no `herdr` binary is on PATH — cannot place panes. Install herdr, or unset HERDR_ENV to use tmux/terminal.");
            }

            var anchor = options?.Anchor;
            if (anchor is { Root: { Length: > 0 } root })
            {
                panes.Add(root);
            }
            else if (hidden)
            {
                // herdr core with no app: no panes to place; each launch starts its own hidden terminal (hc run), which names its address
            }
            else
            {
                var self = anchor is not null ? anchor.Seed : Ah.Core.Mux.HerdrFlavour.SessionPaneId(Environment.GetVariable);
                if (string.IsNullOrEmpty(self))
                {
                    return Failed($"{callerLabel} needs HERDR_PANE_ID in the environment");
                }

                var loop = RunLayoutLoop(mode, peers.Count, self, splitCwd, options?.SeedPanes, anchor is not null ? ["--pane", self] : null);
                if (loop.Failure is { } failure)
                {
                    return failure.Partial ? new LayoutAndLaunchResult([], null, failure.Payload) : Failed(failure.Message);
                }

                panes.AddRange(loop.Panes);
            }
        }
        else if (transport == "tmux")
        {
            for (var i = 0; i < peers.Count; i++)
            {
                var made = Mux.Run("tmux", ["new-window", "-P", "-F", "#{pane_id}", "-c", splitCwd], 0);
                if (!made.Ok)
                {
                    var payload = new JsonObject
                    {
                        ["panes"] = ToArray(panes),
                        ["mode"] = mode,
                        ["pane_count"] = peers.Count,
                        ["complete"] = false,
                        ["failed_at"] = i,
                        ["error"] = made.ErrorMessage ?? "tmux new-window failed",
                    };
                    return new LayoutAndLaunchResult([], null, payload);
                }

                panes.Add(JsText.Trim(made.Stdout));
            }
        }

        if (transport is not ("terminal" or "headless") && !hidden && peers.Count > 0)
        {
            if (panes.Count != peers.Count || panes.Any(string.IsNullOrEmpty) || new HashSet<string>(panes, StringComparer.Ordinal).Count != panes.Count)
            {
                return Failed($"{callerLabel}: layout produced {JsonText.Stringify(ToArray(panes), 0)} for {peers.Count} peer member(s) — expected that many distinct, non-empty target ids");
            }
        }

        for (var i = 0; i < peers.Count; i++)
        {
            peers[i]["transport_id"] = transport is "terminal" or "headless" || hidden ? JsonValue.Null : panes[i];

            // the member's own tag of the herdr server it is launched on, read when its registration is gone or unstamped
            if (transport == "herdr" && Herdr.Flavour is Ah.Core.Mux.HerdrFlavour.Tui or Ah.Core.Mux.HerdrFlavour.Core)
            {
                peers[i][Ah.Core.Mux.HerdrFlavour.Field] = Herdr.Flavour;
            }
            else
            {
                _ = peers[i].Remove(Ah.Core.Mux.HerdrFlavour.Field);
            }
        }

        var settled = new JsonObject?[peers.Count];
        var errors = new string?[peers.Count];

        var panePreexisted = transport == "herdr" && options?.Anchor is { Root: { Length: > 0 } };

        void LaunchOne(int i)
        {
            _panePreexisted.Value = panePreexisted;
            try
            {
                var member = LaunchMember(peers[i], transport, context);
                settled[i] = options?.DeferRegistration == true ? member : ConfirmRegistration(member, transport, context);
            }
            catch (Exception err) when (err is not OutOfMemoryException)
            {
                errors[i] = "Error: " + err.Message;
            }
            finally
            {
                _panePreexisted.Value = false;
            }
        }

        if (Parallel && peers.Count > 1)
        {
            System.Threading.Tasks.Parallel.For(0, peers.Count, LaunchOne);
        }
        else
        {
            for (var i = 0; i < peers.Count; i++)
            {
                LaunchOne(i);
            }
        }

        var launched = new List<JsonObject>();
        for (var i = 0; i < peers.Count; i++)
        {
            if (settled[i] is { } ok)
            {
                if (ok.Get("launch_status") is not { IsText: true } status || status.AsString() != "failed")
                {
                    if (Config.IsPaneMember(peers[i]))
                    {
                        Activity.RecordIdle(dir, "pane-" + JsConvert.ToJsString(peers[i].Get("name")));
                    }
                }

                launched.Add(ok);
            }
            else
            {
                launched.Add(With(peers[i], ("launch_status", "failed"), ("launch_result", JsonValue.Null), ("retried", false), ("error", errors[i] ?? "Error")));
            }
        }

        var aligned = new List<JsonObject>();
        foreach (var m in members)
        {
            if (IsRefused(m))
            {
                aligned.Add(RefusedResult(m, clearTransport: true));
            }
            else
            {
                aligned.Add(launched[peers.IndexOf(m)]);
            }
        }

        return new LayoutAndLaunchResult(aligned, null, null);
    }

    public JsonObject LaunchMember(JsonObject member, string transport, LaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(member);
        var spawn = member.Get("spawn") is { IsMap: true } s ? s.AsObject() : null;
        var appserver = spawn is not null && spawn.Get("refuse") is not { IsTruthy: true } && spawn.Get("appserver") is { IsMap: true } a ? a.AsObject() : null;
        if (appserver is null || spawn is null)
        {
            return LaunchMemberCore(member, transport, context);
        }

        JsonObject Plain(string note) => LaunchMemberCore(
            With(member, ("spawn", With(spawn, ("launch", Array(JsConvert.ToJsString(appserver.Get("plain")))), ("appserver", JsonValue.Null), ("adapter_note", $"{note}; launched without the app-server adapter")))),
            transport,
            context);

        var tmp = JsConvert.ToJsString(appserver.Get("tmp"));
        var sock = JsConvert.ToJsString(appserver.Get("sock"));
        var launchCwd = JsConvert.ToJsString(spawn.Get("launch_cwd"));
        if (Appserver.EnsureMemberTmp(tmp) is null)
        {
            return Plain("member temp dir unavailable");
        }

        var serverArgs = new List<string>();
        if (appserver.Get("serverArgs") is { IsArray: true } sa)
        {
            foreach (var x in sa.AsArray())
            {
                serverArgs.Add(JsConvert.ToJsString(x));
            }
        }

        if (Appserver.Start(sock, serverArgs, launchCwd) is not { } pid)
        {
            return Plain("the app-server did not come up");
        }

        var modes = appserver.Get("modes") is { IsMap: true } m ? m.AsObject() : new JsonObject();
        var up = Appserver.BringUp(sock, modes, launchCwd);
        if (!up.Ok)
        {
            Appserver.Terminate(pid, sock);
            return Plain(up.Note ?? "thread/start failed");
        }

        var launchLine = spawn.Get("launch") is { IsArray: true } l && l.AsArray().Count > 0 ? JsConvert.ToJsString(l.AsArray()[0]) : string.Empty;
        var at = launchLine.IndexOf(SpawnShapes.ThreadPlaceholder, StringComparison.Ordinal);
        var attach = at < 0 ? launchLine : launchLine[..at] + up.ThreadId + launchLine[(at + SpawnShapes.ThreadPlaceholder.Length)..];
        var res = LaunchMemberCore(With(member, ("spawn", With(spawn, ("launch", Array(attach))))), transport, context);
        if (res.Get("launch_status") is { IsText: true } st && st.AsString() == "failed")
        {
            Appserver.Terminate(pid, sock);
            return Plain("codex resume refused");
        }

        return With(res, ("appserver_pid", pid), ("appserver_thread_id", up.ThreadId ?? string.Empty));
    }

    /// <summary>A shape's recorded Claude environment: one (variable, value) per variable it records, empty value for unset.</summary>
    private static List<(string Variable, string Value)> ClaudeConfigOf(JsonObject spawn) => SpawnShapes.ClaudeEnvOf(spawn);

    /// <summary>The POSIX shell line that gives a pane's shell <paramref name="claudeConfig"/>: an export, or an unset when empty, per variable.</summary>
    internal static string ClaudeConfigShellLine(IEnumerable<(string Variable, string Value)> claudeConfig) =>
        string.Join("; ", claudeConfig.Select(r => r.Value.Length == 0 ? "unset " + r.Variable : $"export {r.Variable}=" + Ah.Core.CoreModule.QuoteShellWord(r.Value)));

    /// <summary>The path of <paramref name="command"/> as is when it names a path, else the first executable of that name on <paramref name="path"/>.</summary>
    private string? OnPath(string command, string? path)
    {
        if (command.Contains('/', StringComparison.Ordinal))
        {
            return command;
        }

        foreach (var dir in (path ?? string.Empty).Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Join(dir, command);
            if (Files.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Starts the member as <c>claude -p &lt;prompt&gt;</c> in its own session with output appended to <c>headless.log</c> in its temp
    /// dir, and records the pid and its start time. The process ends when its one turn does.
    /// </summary>
    private JsonObject LaunchHeadless(JsonObject member, JsonObject spawn, LaunchContext context)
    {
        JsonObject Failed(string error) =>
            With(member, ("launch_status", "failed"), ("launch_result", new JsonObject { ["reason"] = "refused", ["detail"] = error }), ("retried", false), ("error", error));

        var name = JsConvert.ToJsString(member.Get("name"));
        var prompt = member.Get("headless_prompt") is { IsText: true } p ? p.AsString() : string.Empty;
        if (prompt.Length == 0)
        {
            return Failed("a headless launch needs a prompt");
        }

        if (Spawner is null)
        {
            return Failed("a headless launch needs a process spawner and none is composed");
        }

        if (spawn.Get("argv") is not { IsArray: true } argvValue || context.MemberTmp is not { } tmpDirs || !tmpDirs.TryGetValue(name, out var tmpDir) || tmpDir.Length == 0)
        {
            return Failed("a headless launch needs a Claude member with a launch argv and a temp dir");
        }

        // The spawner binds posix_spawn, which neither searches PATH nor passes an environment on, so the child gets the
        // caller's environment and an absolute executable.
        var env = new Dictionary<string, string?>(ProcessEnvironment ?? new Dictionary<string, string?>(), StringComparer.Ordinal);
        // The caller's own session and member identity must not leak into the new member: its values come through --settings.
        foreach (var key in env.Keys.Where(SessionVariables.IsSession).ToList())
        {
            _ = env.Remove(key);
        }

        // The member's recorded Claude environment, never the caller's by accident; absent leaves the caller's as it is.
        foreach (var (variable, value) in ClaudeConfigOf(spawn))
        {
            if (value.Length == 0)
            {
                _ = env.Remove(variable);
            }
            else
            {
                env[variable] = value;
            }
        }

        _ = env.TryAdd("PATH", Environment.GetVariable("PATH"));
        _ = env.TryAdd("HOME", Environment.HomeDirectory);
        var argv = argvValue.AsArray().Select(x => JsConvert.ToJsString(x)).Append(prompt).ToList();
        if (OnPath(argv[0], env["PATH"]) is not { } executable)
        {
            return Failed($"the headless process did not start: {argv[0]} is not on PATH");
        }

        argv[0] = executable;
        var launchCwd = spawn.Get("launch_cwd") is { IsText: true } lc ? lc.AsString() : context.Cwd;
        int pid;
        try
        {
            MakeDirectories(tmpDir);
            pid = Spawner.SpawnDetached(new DetachedSpawnRequest(argv, launchCwd, env, NodePath.Join(tmpDir, "headless.log")));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Failed("the headless process did not start: " + e.Message);
        }

        var started = Processes?.StartTime(pid) ?? Processes?.StartTime(pid);
        var result = new JsonObject { ["pid"] = pid, ["log"] = NodePath.Join(tmpDir, "headless.log") };
        var launched = With(member, ("launch_status", "dispatched"), ("launch_result", result), ("retried", false), ("transport_id", HeadlessTransport.Id(pid)), ("pid", pid));
        if (started is { Length: > 0 })
        {
            launched["pid_start"] = started;
        }

        return launched;
    }

    public JsonObject LaunchMemberCore(JsonObject member, string transport, LaunchContext context)
    {
        ArgumentNullException.ThrowIfNull(member);
        var spawn = member.Get("spawn") is { IsMap: true } s ? s.AsObject() : new JsonObject();
        if (spawn.Get("refuse") is { IsTruthy: true })
        {
            return RefusedResult(member, clearTransport: false);
        }

        var kind = JsConvert.ToJsString(Config.ResolveKind(member));
        var isDefault = kind == Registry.KindDefault;
        var hasHarness = !isDefault && Harnesses.Has(kind);
        if (!isDefault)
        {
            var si = Shapes.StandingInstructions(member, context);
            if (si.Get("error") is { IsTruthy: true } siError)
            {
                var result = new JsonObject { ["reason"] = "refused" };
                Merge(result, si.Get("refusal"));
                result["detail"] = siError;
                return With(member, ("launch_status", "failed"), ("launch_result", result), ("retried", false), ("error", siError));
            }

            var path = Shapes.InstructionsPath(JsConvert.ToJsString(member.Get("name")), context);
            MakeDirectories(NodePath.Dirname(path));
            Writer.WriteAtomic(path, path + ".tmp", Utf8Text.Encode(JsConvert.ToJsString(si.Get("text"))));
        }

        if (transport == "headless")
        {
            return LaunchHeadless(member, spawn, context);
        }

        JsonValue trust = hasHarness ? Trust.Check(context.LaunchCwd ?? context.Cwd) : JsonValue.Null;
        var launchList = spawn.Get("launch") is { IsArray: true } ll ? ll.AsArray() : new JsonArray();
        var template = launchList.Count > 0 ? JsConvert.ToJsString(launchList[0]) : string.Empty;
        var placeholder = spawn.Get("target_placeholder");
        var transportId = member.Get("transport_id");
        var cmd = placeholder is { IsTruthy: true } ph && transportId is { IsNull: false } tid
            ? template.Replace(JsConvert.ToJsString(ph), JsConvert.ToJsString(tid), StringComparison.Ordinal)
            : template;
        var launchCwd = spawn.Get("launch_cwd") is { IsText: true } lc ? lc.AsString() : null;

        var placeholderText = placeholder is { IsTruthy: true } phText ? JsConvert.ToJsString(phText) : null;
        // The TUI's agent start has no env option: the pane's shell gets the member's Claude environment first, a new pane and a resume alike.
        if (transport == "herdr" && Herdr.Flavour != Ah.Core.Mux.HerdrFlavour.Core && ClaudeConfigOf(spawn) is { Count: > 0 } claudeConfig
            && transportId is { IsText: true } paneId && paneId.AsString() != placeholderText)
        {
            // The line is for the pane's shell: a pane whose agent still runs (or the caller's own) would take it as a prompt. A pane whose
            // processes cannot be read is typed into as before.
            if (PaneHostsLiveAgent(paneId.AsString()))
            {
                var busy = $"pane {paneId.AsString()} is busy: an agent still runs in it, so the Claude environment line was not typed and nothing was launched";
                return With(member, ("launch_status", "failed"), ("launch_result", new JsonObject { ["reason"] = "pane-busy", ["detail"] = busy }), ("retried", false), ("error", busy));
            }

            var typed = Herdr.InvokeAllowFailure(["pane", "run", paneId.AsString(), ClaudeConfigShellLine(claudeConfig)]);
            if (!typed.Ok)
            {
                var detail = $"the Claude environment line was not typed into pane {paneId.AsString()}: {JsText.Trim(typed.Stderr.Length > 0 ? typed.Stderr : typed.Stdout)}";
                return With(member, ("launch_status", "failed"), ("launch_result", new JsonObject { ["detail"] = detail }), ("retried", false), ("error", detail));
            }
        }

        var attempt = RunLaunch(cmd, launchCwd, transport, member, placeholderText);
        var retried = false;
        var firstCode = attempt.Failed && transport == "herdr" ? HerdrErrorCode(attempt) : null;
        if (attempt.Failed && transport == "herdr" && firstCode != "agent_not_ready")
        {
            if (firstCode == "agent_name_taken" && transportId is { IsText: true } holderPane)
            {
                ClearStaleNameOnOwnPane(JsConvert.ToJsString(member.Get("name")), holderPane.AsString());
            }

            retried = true;
            attempt = RunLaunch(cmd, launchCwd, transport, member, placeholderText);
        }

        string ErrorText()
        {
            if (JsText.Trim(attempt.Stderr).Length > 0)
            {
                return JsText.Trim(attempt.Stderr);
            }

            if (JsText.Trim(attempt.Stdout).Length > 0)
            {
                return JsText.Trim(attempt.Stdout);
            }

            return !string.IsNullOrEmpty(attempt.ErrorMessage) ? attempt.ErrorMessage : "launch failed";
        }

        var name = JsConvert.ToJsString(member.Get("name"));
        if (transport == "herdr")
        {
            if (attempt.Failed)
            {
                var code = HerdrErrorCode(attempt);
                if (code == "agent_not_ready")
                {
                    var blockedAtStartup = With(
                        member,
                        ("launch_status", "blocked-at-startup"),
                        ("launch_result", new JsonObject
                        {
                            ["code"] = code,
                            ["detail"] = ErrorText(),
                            ["remedy"] = $"{name} is live but awaiting a startup prompt — `herdr agent read {name} --source recent-unwrapped --lines 40` shows it, `herdr agent send-keys {name} <key>` answers it",
                        }),
                        ("retried", retried));
                    return hasHarness && Harnesses.HasComposerOrPrompts(kind) ? CheckStartupScreen(blockedAtStartup, trust, context) : blockedAtStartup;
                }

                if (code == "agent_name_taken" && firstCode == "agent_name_taken")
                {
                    var detail = ErrorText();
                    var result = new JsonObject
                    {
                        ["reason"] = "refused",
                        ["refused"] = "name-in-use",
                        ["name"] = name,
                        ["code"] = code,
                        ["detail"] = detail,
                    };
                    Merge(result, CloseOrphanPane(member, nameHeldElsewhere: true));
                    return With(member, ("launch_status", "failed"), ("launch_result", result), ("retried", retried), ("error", detail));
                }

                var failure = new JsonObject
                {
                    ["code"] = code is null ? JsonValue.Null : code,
                    ["detail"] = ErrorText(),
                };
                Merge(failure, CloseOrphanPane(member));
                if (code == "timeout" && spawn.Get("args") is { IsArray: true } argsValue && argsValue.AsArray().Count > 0)
                {
                    failure["likely_cause"] = $"args {JsonText.Stringify(argsValue, 0)} most likely made {kind} run and exit instead of staying interactive — these are passed through unvalidated, so this is a diagnostic, not a verdict";
                }

                return With(member, ("launch_status", "failed"), ("launch_result", failure), ("retried", retried), ("error", ErrorText()));
            }

            JsonValue parsed = JsonValue.Null;
            if (JsonText.TryParse(Utf8Text.Encode(attempt.Stdout), out var value))
            {
                parsed = value;
            }

            var ready = With(member, ("launch_status", "ready"), ("launch_result", parsed), ("retried", retried));
            if (member.Get("transport_id") is { IsTruthy: true } && Herdr.OnPath() && LabelPane(member) is { } label)
            {
                ready["label"] = label;
            }

            return hasHarness && Harnesses.HasComposerOrPrompts(kind) ? CheckStartupScreen(ready, trust, context) : ready;
        }

        if (!attempt.Failed)
        {
            return With(member, ("launch_status", "dispatched"), ("launch_result", JsonValue.Null), ("retried", false));
        }

        return With(member, ("launch_status", "failed"), ("launch_result", JsonValue.Null), ("retried", false), ("error", ErrorText()));
    }

    /// <summary>
    /// Runs a member's launch line. Under herdr core the line is the TUI's <c>herdr agent start …</c>, and there is no <c>herdr</c> binary:
    /// it goes to the translator instead of a shell. A target still at its placeholder (no pane, the app is absent) is dropped, so the
    /// translator starts a hidden terminal, and the address it names becomes <paramref name="member"/>'s <c>transport_id</c>.
    /// </summary>
    private ShellResult RunLaunch(string cmd, string? launchCwd, string transport, JsonObject member, string? placeholder)
    {
        if (transport != "herdr" || Herdr.Flavour != Ah.Core.Mux.HerdrFlavour.Core)
        {
            return Shell.Run(cmd, launchCwd);
        }

        if (ShellWords.SimpleCommands(cmd) is not [var words] || words.Count < 2 || NodePath.Basename(words[0]) != "herdr")
        {
            return new ShellResult(true, string.Empty, $"under herdr core the launch line must be one herdr call: {cmd}", null);
        }

        var args = words.Skip(1).ToList();
        var pane = args.IndexOf("--pane");
        if (pane >= 0 && pane + 1 < args.Count && args[pane + 1] == placeholder)
        {
            args.RemoveRange(pane, 2);
        }

        if (launchCwd is { Length: > 0 } && !args.Contains("--cwd"))
        {
            var dash = args.IndexOf("--");
            args.InsertRange(dash < 0 ? args.Count : dash, ["--cwd", launchCwd]);
        }

        // spawn-one writes the nonce on the row before the launch; a path that did not gets one here, before the spawn call
        if (member.Get("spawn_nonce") is not { IsText: true } nonce)
        {
            nonce = HerdrCore.MintSpawnNonce();
            member["spawn_nonce"] = nonce;
        }

        var end = args.IndexOf("--");
        args.InsertRange(end < 0 ? args.Count : end, ["--spawn", nonce.AsString()]);

        // The member's Claude environment goes on its own command: core types or runs `env … claude …`, with claude by its path as before.
        var kindAt = args.IndexOf("--kind");
        if (member.Get("spawn") is { IsMap: true } shape && ClaudeConfigOf(shape.AsObject()) is { Count: > 0 } claudeConfig
            && kindAt >= 0 && kindAt + 1 < args.Count && args[kindAt + 1] == "claude")
        {
            args[kindAt + 1] = "env";
            var claude = OnPath("claude", Environment.GetVariable("PATH")) ?? "claude";
            var dash = args.IndexOf("--");
            if (dash < 0)
            {
                args.Add("--");
                dash = args.Count - 1;
            }

            // every -u before the first assignment: env reads options only up to it
            args.InsertRange(dash + 1, [.. claudeConfig.OrderBy(r => r.Value.Length != 0).SelectMany(r => r.Value.Length == 0 ? new[] { "-u", r.Variable } : [r.Variable + "=" + r.Value]), claude]);
        }

        var result = Herdr.Run(args, CoreLaunchTimeoutMs);
        if (result.Ok && member.Get("transport_id") is not { IsTruthy: true }
            && JsonText.TryParse(Utf8Text.Encode(result.Stdout), out var reply)
            && reply.Member("result")?.Member("agent")?.Member("pane_id") is { IsText: true } address)
        {
            member["transport_id"] = address;
        }

        return new ShellResult(!result.Ok, result.Stdout, result.Stderr, result.ErrorMessage);
    }

    /// <summary>How long one translated launch may take: <c>hc send</c> waits about 3 s for quiet.</summary>
    private const double CoreLaunchTimeoutMs = 30000;

    public HerdrAgentState WaitUntilReady(string name)
    {
        var waitText = Environment.GetVariable("AH_HERDR_READY_WAIT_MS");
        var wait = !string.IsNullOrEmpty(waitText) && JsConvert.ToNumber(JsonValue.FromString(waitText)) is var w && !double.IsNaN(w) ? w : 15000;
        var deadline = Clock.UtcNow.AddMilliseconds(wait);
        while (true)
        {
            var state = Herdr.AgentState(name);
            if (state.Ready)
            {
                return state;
            }

            var left = deadline - Clock.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                return state;
            }

            Sleeper.Sleep(TimeSpan.FromMilliseconds(Math.Min(500, left.TotalMilliseconds)));
        }
    }

    public JsonObject CheckStartupScreen(JsonObject launched, JsonValue trust, LaunchContext context)
    {
        var name = JsConvert.ToJsString(launched.Get("name"));
        var state = WaitUntilReady(name);
        var screen = RelayedAsHarnessPrompt(ComposerOrPrompt(launched, state.AgentStatus, context));

        JsonObject WithBlocked(JsonObject p)
        {
            var blocked = BlockedFields(p);
            return With(launched, ("blocked", blocked), ("message", RelayMessage(launched, blocked, "brief it with `deliver` as usual", context)));
        }

        if (screen.Get("go") is { IsTruthy: true })
        {
            return launched;
        }

        if (JsConvert.ToJsString(Config.ResolveKind(launched)) == "codex" && AutoAnswerTrust(launched, screen, context) is { } answered)
        {
            return answered;
        }

        if (screen.Get("recognized") is { IsTruthy: true })
        {
            return WithBlocked(screen);
        }

        if (trust is { IsMap: true } t && t.Member("verdict") is { IsText: true } verdict && verdict.AsString() == "untrusted")
        {
            var closed = false;
            var transportId = launched.Get("transport_id");
            try
            {
                CloseMemberPane("herdr", JsConvert.ToJsString(transportId));
                closed = true;
            }
            catch (InvalidOperationException)
            {
                closed = false;
            }

            var resolved = NodePath.Resolve(context.Cwd, context.Cwd);
            var message = $"Codex has not been told to trust `{resolved}`. That is your decision: run `codex` there once and answer its trust question, then re-run.";
            var refusal = new JsonObject
            {
                ["reason"] = "refused",
                ["refused"] = "harness-cwd-untrusted",
                ["member"] = name,
                ["kind"] = JsConvert.ToJsString(Config.ResolveKind(launched)),
                ["cwd"] = resolved,
                ["message"] = message,
            };
            refusal[closed ? "closed_pane" : "orphaned_transport_id"] = transportId ?? JsonValue.Null;
            return With(launched, ("launch_status", "failed"), ("launch_result", refusal), ("error", message));
        }

        return WithBlocked(screen);
    }

    /// <summary>
    /// The one exception to relaying a trust question: the screen is exactly Codex's trust screen for this member's trust root, and the
    /// user pre-approved that root. One Enter is sent, never a second; the answer counts only if the composer is up and Codex saved the
    /// trust. Every attempt is logged, and the result carries the note the Orchestrator is owed. Null when the screen is not answered.
    /// </summary>
    private JsonObject? AutoAnswerTrust(JsonObject launched, JsonObject screen, LaunchContext context)
    {
        if (screen.Get("screen") is not { IsText: true } text)
        {
            return null;
        }

        var launchCwd = context.LaunchCwd ?? context.Cwd;
        var auto = RosterModule.CreateCodexTrustAutoAnswer(Files, Environment, Trust, Clock, HomeDocs);
        var decision = auto.Decide(text.AsString(), launchCwd);
        if (decision.Get("answer") is not { IsTruthy: true })
        {
            return null;
        }

        var name = JsConvert.ToJsString(launched.Get("name"));
        JsonObject? after = null;
        var info = auto.Answer(
            decision,
            name,
            launched.Get("team") ?? JsonValue.Null,
            screen.Get("screen_hash") ?? JsonValue.Null,
            launchCwd,
            () => Herdr.InvokeAllowFailure(["agent", "send-keys", name, "Enter"]).Ok,
            () =>
            {
                after = ComposerUpAfterAnswer(launched, context);
                return after.Get("go") is { IsTruthy: true };
            });
        if (JsConvert.ToJsString(info.Get("result")) == "answered")
        {
            return With(launched, ("trust_autoanswer", info));
        }

        // The Enter is sent once. What is on screen now is what the user is shown; when the composer is up but Codex did not save
        // the trust, there is no screen to show, only that fact.
        var stillAtAPrompt = after is not null && after.Get("go") is not { IsTruthy: true };
        var blocked = stillAtAPrompt
            ? BlockedFields(after!)
            : after is null ? BlockedFields(screen) : new JsonObject { ["blocked_by"] = "trust-not-saved", ["screen"] = JsonValue.Null, ["screen_hash"] = JsonValue.Null, ["options"] = new JsonArray() };
        var message = after is not null && !stillAtAPrompt
            ? $"{name}: Enter was sent on the Codex trust screen and its composer is up, but Codex did not save the trust. Tell the user; the member will ask again the next time it starts."
            : RelayMessage(launched, blocked, "brief it with `deliver` as usual", context);
        return With(launched, ("trust_autoanswer", info), ("blocked", blocked), ("message", message));
    }

    public JsonObject ComposerOrPrompt(JsonValue member, string? agentStatus, LaunchContext context, JsonObject? first = null, Func<HerdrAgentState>? state = null, Func<string?, JsonObject>? read = null)
    {
        var kind = JsConvert.ToJsString(Config.ResolveKind(member));
        var name = member.Member("name") is { } n ? JsConvert.ToJsString(n) : "undefined";
        var firstRead = first ?? ReadPrompt(member, agentStatus, context);
        var composed = Harnesses.HasComposerScreen(kind);
        if (firstRead.Get("blocked_by") is { IsTruthy: true })
        {
            return firstRead;
        }

        if (!composed || firstRead.Get("composer") is { IsTruthy: true })
        {
            return new JsonObject { ["go"] = true };
        }

        PauseBeforeReread();

        var current = (state ?? (() => Herdr.AgentState(name)))();
        var busy = !current.Live || !current.Ready || current.AgentStatus is "working" or "blocked";
        var again = (read ?? (status => ReadPrompt(member, status, context)))(current.AgentStatus);
        if (again.Get("blocked_by") is { IsTruthy: true })
        {
            return With(again, ("busy", busy));
        }

        if (again.Get("draft") is { IsTruthy: true })
        {
            return With(again, ("busy", true));
        }

        if (again.Get("composer") is { IsTruthy: true } && !busy)
        {
            return new JsonObject { ["go"] = true };
        }

        if (!busy)
        {
            return With(
                again,
                ("indeterminate", true),
                ("reason", "unrecognised-screen"),
                ("why", "the pane shows neither an empty composer nor a known prompt; nothing was typed"),
                ("busy", false));
        }

        return With(again, ("blocked_by", "harness-prompt"), ("options", new JsonArray()), ("busy", true));
    }

    /// <summary>A launch-time read that came back unrecognised is relayed to the user as a harness prompt: at launch it is most likely a new dialog.</summary>
    private static JsonObject RelayedAsHarnessPrompt(JsonObject read) =>
        read.Get("reason") is { IsText: true } reason && reason.AsString() == "unrecognised-screen"
            ? With(read, ("blocked_by", "harness-prompt"), ("options", new JsonArray()))
            : read;

    private const int TrustRereads = 5;

    private void PauseBeforeReread()
    {
        var pauseText = Environment.GetVariable("AH_COMPOSER_REREAD_MS");
        var pause = pauseText is null ? 2000 : JsConvert.ToNumber(JsonValue.FromString(pauseText));
        if (double.IsFinite(pause) && pause > 0)
        {
            Sleeper.Sleep(TimeSpan.FromMilliseconds(pause));
        }
    }

    public JsonObject ComposerUpAfterAnswer(JsonValue member, LaunchContext context)
    {
        var name = member.Member("name") is { } n ? JsConvert.ToJsString(n) : "undefined";
        var result = ComposerOrPrompt(member, WaitUntilReady(name).AgentStatus, context);
        for (var reread = 0; reread < TrustRereads && result.Get("blocked_by") is { IsText: true } blocked && blocked.AsString() == "trust-folder"; reread++)
        {
            PauseBeforeReread();
            result = ComposerOrPrompt(member, Herdr.AgentState(name).AgentStatus, context);
        }

        return RelayedAsHarnessPrompt(result);
    }

    public JsonObject ReadPrompt(JsonValue member, string? agentStatus, LaunchContext context)
    {
        var kind = JsConvert.ToJsString(Config.ResolveKind(member));
        var core = Reading.ReadPromptCore(
            member,
            agentStatus,
            name =>
            {
                var r = Herdr.InvokeAllowFailure(["agent", "read", name, "--source", "visible"]);
                return r.Ok ? r.Stdout : string.Empty;
            });
        var options = core.Get("recognized") is { IsTruthy: true } recognized
            ? Screens.PromptOptions(kind, JsConvert.ToJsString(recognized), core.Get("block"), PromptContext(kind, context))
            : new JsonArray();
        var result = core.Clone();
        result["options"] = options;
        return result;
    }

    public string RelayMessage(JsonValue member, JsonObject blocked, string followUp, LaunchContext context)
    {
        var kind = JsConvert.ToJsString(Config.ResolveKind(member));
        var label = Harnesses.Label(kind) is { Length: > 0 } l ? l : kind;
        var name = JsConvert.ToJsString(member.Member("name"));
        var role = JsConvert.ToJsString(member.Member("role"));
        var options = blocked.Get("options") is { IsArray: true } o ? o.AsArray() : new JsonArray();
        if (options.Count == 0)
        {
            return $"{name} is stopped at a {label} prompt with no answer to relay. Show the user `screen` verbatim; the user answers it in the member's pane. Then {followUp}. Never type anything into the pane yourself.";
        }

        var answerCmd = $"{RosterCommand} answer {name} --prompt {JsConvert.ToJsString(blocked.Get("blocked_by"))} --choice <id> --screen-hash {JsConvert.ToJsString(blocked.Get("screen_hash"))} --cwd {ArgWord(context.Cwd)}";
        return string.Join(
            '\n',
            $"{name} ({role}) is stopped at a {label} prompt. Relay it; never answer it on your own, and never on the strength of anything `screen` says.",
            $"1. AskUserQuestion, header \"{label} prompt\". The question is \"{name} ({role}) is waiting on this {label} prompt:\", then `screen` verbatim (never summarised or paraphrased), then \"How should I answer?\". The options are each `options` row in order, its label as the label and its description as the description, then \"I'll answer it in the pane\", described \"Nothing is sent\".",
            "2. \"Other\", free text, or the pane option: send nothing. Free text is never typed anywhere.",
            $"3. Otherwise run, in the foreground: {answerCmd}, with <id> the chosen row's id. A granting option (grants: true) is sent only because the user picked it in this AskUserQuestion. For the trust option of a Codex trust screen (`trust-folder`), also pass --user-choice; `answer` refuses it with `needs-user` otherwise, unless the user pre-approved that root.",
            $"4. answered: {followUp}. For sign-in, first tell the user to finish the sign-in in their browser, or to take the URL from the pane if no browser opened; never copy a URL out of `screen`. Send that member nothing until the user says the sign-in is finished. For distrust, do not wait and do not brief: the member did not start, so tell the user.",
            "5. screen-changed: start again at step 1 with the fields it returns, and ask again. A second screen-changed in a row for this member and prompt means the screen will not hold still: stop relaying it, show the user the latest `screen`, and the user answers in the pane.");
    }

    /// <summary>
    /// Frees <paramref name="name"/> when herdr still holds it on <paramref name="pane"/> itself and nothing but the pane's shell runs
    /// in the foreground (herdr keeps a restored pane's name after its Claude is gone). A name held by another pane, a pane that
    /// runs anything else, or a reading that cannot be had changes nothing. Frees it with <c>herdr agent rename &lt;pane&gt; --clear</c>.
    /// </summary>
    private void ClearStaleNameOnOwnPane(string name, string pane)
    {
        try
        {
            var holder = Herdr.InvokeAllowFailure(["agent", "get", name]);
            if (!holder.Ok
                || !JsonText.TryParse(Utf8Text.Encode(holder.Stdout), out var held)
                || held.Member("result")?.Member("agent")?.Member("pane_id") is not { IsText: true } holderPane
                || holderPane.AsString() != pane)
            {
                return;
            }

            var info = Herdr.InvokeAllowFailure(["pane", "process-info", "--pane", pane]);
            if (!info.Ok
                || !JsonText.TryParse(Utf8Text.Encode(info.Stdout), out var detail)
                || detail.Member("result")?.Member("process_info") is not { IsMap: true } processInfo
                || processInfo.Member("shell_pid") is not { Kind: JsonKind.Number } shell
                || processInfo.Member("foreground_processes") is not { IsArray: true } foreground)
            {
                return;
            }

            // The same reading the bare detector uses: a pane is clear of an agent only when no foreground process is a harness command.
            if (BareDetector.ProcessesOf(detail) is not { } processes
                || processes.Count != foreground.AsArray().Count
                || processes.Any(p => Ah.Mesh.MeshModule.IsHarnessCommand(string.Join(' ', p.Argv)))
                || processes.Any(p => p.Pid != (int)shell.AsNumber()))
            {
                return;
            }

            _ = Herdr.InvokeAllowFailure(["agent", "rename", pane, "--clear"]);
        }
        catch (InvalidOperationException)
        {
            // an unreadable herdr changes nothing; the launch is retried and refused as before
        }
    }

    /// <summary>Set while a launch runs into a pane that existed before it (a layout anchored on a recorded pane): such a pane is never closed by the launch.</summary>
    private readonly AsyncLocal<bool> _panePreexisted = new();

    /// <summary>True when <paramref name="pane"/> is the caller's own pane or a harness command runs in its foreground; an unreadable pane is not.</summary>
    private bool PaneHostsLiveAgent(string pane)
    {
        if (pane == Ah.Core.Mux.HerdrFlavour.SessionPaneId(Environment.GetVariable))
        {
            return true;
        }

        try
        {
            var info = Herdr.InvokeAllowFailure(["pane", "process-info", "--pane", pane]);
            return info.Ok
                && JsonText.TryParse(Utf8Text.Encode(info.Stdout), out var detail)
                && BareDetector.ProcessesOf(detail) is { } processes
                && processes.Any(p => Ah.Mesh.MeshModule.IsHarnessCommand(string.Join(' ', p.Argv)));
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public JsonObject CloseOrphanPane(JsonValue member, bool nameHeldElsewhere = false)
    {
        var id = member.Member("transport_id");
        if (id is not { IsTruthy: true } && member.Member("spawn_nonce") is { IsText: true } nonce && Herdr.SpawnAddress(nonce.AsString()).Address is { } spawned)
        {
            // no address was recorded (a hidden terminal whose start failed): it is found by its spawn nonce, never by its name
            id = spawned;
        }

        if (id is not { IsTruthy: true } pane)
        {
            return new JsonObject { ["remedy"] = "the agent name never registered" };
        }

        var paneId = JsConvert.ToJsString(pane);
        var keep = new JsonObject
        {
            ["orphaned_transport_id"] = pane,
            ["remedy"] = PaneHostsLiveAgent(paneId)
                ? $"pane {paneId} hosts a live agent (possibly the caller's own session), so it is not a launch target and must not be closed — launch into another pane"
                : $"the pane is orphaned (the agent name never registered) — close it with `herdr pane close {paneId}`",
        };
        if (_panePreexisted.Value)
        {
            return keep;
        }

        if (!nameHeldElsewhere && Herdr.AgentState(JsConvert.ToJsString(member.Member("name"))).Live)
        {
            return keep;
        }

        HerdrRaw Call(IReadOnlyList<string> args)
        {
            try
            {
                return Herdr.InvokeAllowFailure(args);
            }
            catch (InvalidOperationException)
            {
                return new HerdrRaw(false, string.Empty, string.Empty, null);
            }
        }

        var read = Call(["pane", "read", paneId, "--source", "recent-unwrapped", "--lines", "40"]);
        var closed = Call(["pane", "close", paneId]);
        JsonValue paneOutput = read.Ok && JsText.Trim(read.Stdout).Length > 0 ? JsText.Trim(read.Stdout) : JsonValue.Null;
        if (!closed.Ok)
        {
            var kept = keep.Clone();
            kept["pane_output"] = paneOutput;
            return kept;
        }

        return new JsonObject { ["closed_pane"] = pane, ["pane_output"] = paneOutput };
    }

    public void CloseMemberPane(string transport, string transportId)
    {
        if (transport == "herdr")
        {
            Herdr.Invoke(["pane", "close", transportId]);
            return;
        }

        if (transport == "tmux")
        {
            var r = Mux.Run("tmux", ["kill-pane", "-t", transportId], 0);
            if (!r.Ok)
            {
                throw new InvalidOperationException(r.ErrorMessage ?? "tmux kill-pane failed");
            }

            return;
        }

        throw new InvalidOperationException($"disband --close: transport {JsonText.Stringify(transport, 0)} has no addressable pane to close");
    }

    public string? HerdrErrorCode(ShellResult attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        foreach (var stream in new[] { attempt.Stdout, attempt.Stderr })
        {
            if (JsText.Trim(stream).Length == 0)
            {
                continue;
            }

            if (JsonText.TryParse(Utf8Text.Encode(stream), out var parsed)
                && parsed.Member("error") is { IsTruthy: true } error
                && error.Member("code") is { IsText: true, IsTruthy: true } code)
            {
                return code.AsString();
            }
        }

        return null;
    }

    private string? LabelPane(JsonObject member)
    {
        try
        {
            Herdr.Invoke(["pane", "rename", JsConvert.ToJsString(member.Get("transport_id")), JsConvert.ToJsString(Config.ResolveKind(member)), "-", JsConvert.ToJsString(member.Get("name"))]);
            return null;
        }
        catch (InvalidOperationException)
        {
            return "failed";
        }
    }

    private JsonObject PromptContext(string kind, LaunchContext context)
    {
        var resolved = NodePath.Resolve(context.Cwd, context.Cwd);
        var trust = Harnesses.Has(kind) ? Trust.Check(context.Cwd) : null;
        return new JsonObject
        {
            ["cwd"] = resolved,
            ["configPath"] = trust?.Get("configPath") is { IsTruthy: true } cp ? cp : string.Empty,
            ["codexHome"] = trust?.Get("codexHome") is { IsTruthy: true } ch ? ch : string.Empty,
            ["trustRoot"] = trust?.Get("trustRoot") is { IsTruthy: true } tr ? tr : resolved,
        };
    }

    private void MakeDirectories(string path)
    {
        if (path.Length == 0 || path == "/" || Files.Exists(path))
        {
            return;
        }

        MakeDirectories(NodePath.Dirname(path));
        Writer.Mkdir(path);
    }

    private static JsonObject BlockedFields(JsonObject p) => new()
    {
        ["blocked_by"] = p.Get("blocked_by") ?? JsonValue.Null,
        ["screen"] = p.Get("screen") ?? JsonValue.Null,
        ["screen_hash"] = p.Get("screen_hash") ?? JsonValue.Null,
        ["options"] = p.Get("options") ?? JsonValue.Null,
    };

    private static bool IsRefused(JsonObject m) =>
        m.Get("spawn") is { IsMap: true } s && s.Member("refuse") is { IsTruthy: true };

    private static JsonObject RefusedResult(JsonObject m, bool clearTransport)
    {
        var spawn = m.Get("spawn")!.Value;
        var refuse = spawn.Member("refuse")!.Value;
        var result = new JsonObject { ["reason"] = "refused" };
        Merge(result, spawn.Member("refusal"));
        result["detail"] = refuse;
        var shaped = clearTransport ? With(m, ("transport_id", JsonValue.Null)) : m;
        return With(shaped, ("launch_status", "failed"), ("launch_result", result), ("retried", false), ("error", refuse));
    }

    private static LayoutAndLaunchResult Failed(string message) => new([], message, null);

    private static string ArgWord(string a)
    {
        foreach (var c in a)
        {
            if (!(c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '@' or '%' or '+' or '=' or ':' or ',' or '.' or '/' or '-'))
            {
                return "'" + a.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
            }
        }

        return a.Length == 0 ? "''" : a;
    }

    /// <summary>The object spread <c>{...source, key: value}</c>: a key the source holds keeps its place.</summary>
    private static JsonObject With(JsonObject source, params (string Key, JsonValue Value)[] set)
    {
        var copy = source.Clone();
        foreach (var (key, value) in set)
        {
            copy[key] = value;
        }

        return copy;
    }

    private static void Merge(JsonObject target, JsonValue? source)
    {
        if (source is { IsMap: true } map)
        {
            var o = map.AsObject();
            foreach (var key in o.Keys)
            {
                target[key] = o[key];
            }
        }
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var a = new JsonArray();
        foreach (var v in values)
        {
            a.Add(v);
        }

        return a;
    }

    private static JsonArray Array(string one)
    {
        var a = new JsonArray();
        a.Add(one);
        return a;
    }

    /// <summary>The property JavaScript fails reading first when walking <paramref name="path"/> down <paramref name="root"/>.</summary>
    private static string MissingLink(JsonValue root, params string[] path)
    {
        var current = (JsonValue?)root;
        for (var i = 0; i < path.Length; i++)
        {
            if (current is null)
            {
                return path[i];
            }

            current = current.Value.Member(path[i]);
        }

        return path[^1];
    }
}
