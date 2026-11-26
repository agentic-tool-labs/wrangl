using System.Globalization;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.DaemonClient;

/// <summary>A verb's answer: the exit code and a JSON object that always carries <c>ok</c>.</summary>
internal readonly record struct VerbResult(int Code, JsonObject Body);

/// <summary>
/// The lifecycle verbs of <c>ahd</c>: <c>start</c>, <c>stop</c>, <c>restart</c>, <c>drain</c>, <c>status</c>,
/// <c>health</c>, <c>version</c> and <c>logs</c>. Output and exit codes are the recorded contract's.
/// </summary>
internal sealed class AhdLifecycle(AhdClient client, Func<string, CancellationToken, Task<JsonObject?>>? inspectFiles = null) : IDaemonLifecycle
{
    /// <summary>The exit code of a verb that needs the daemon when it is down or draining.</summary>
    internal const int DaemonDownExit = 3;

    private const int MaxLogLines = 1000;
    private const int DefaultLogLines = 50;

    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal)
    {
        "plain", "pending", "acked", "all", "require", "once", "hook", "follow", "force",
    };

    private static readonly HashSet<string> ValuedNeedingValue = new(StringComparer.Ordinal)
    {
        "cwd", "n", "address", "session", "dir", "harness", "path", "budget", "set", "idle-exit-ms", "trigger",
    };

    private static readonly string[] AllFlagsOf = [];

    private static VerbResult Ok(params (string Key, JsonValue Value)[] members)
    {
        var body = new JsonObject { ["ok"] = true };
        foreach (var (key, value) in members)
        {
            body[key] = value;
        }

        return new VerbResult(0, body);
    }


    private static VerbResult Fail(int code, string error, string message, params (string Key, JsonValue Value)[] extra)
    {
        var body = new JsonObject { ["ok"] = false };
        foreach (var (key, value) in extra)
        {
            body[key] = value;
        }

        body["error"] = Jx.Obj(("code", error), ("message", message));
        return new VerbResult(code, body);
    }

    private async Task<Discovered?> LiveAsync(CancellationToken ct)
    {
        var info = await client.DiscoverCoreAsync(AhdClient.DiscoverTimeoutMs, ct).ConfigureAwait(false);
        return info is { Draining: false } ? info : null;
    }

    // ---------------------------------------------------------------- lifecycle

    internal async Task<VerbResult> StartAsync(bool upgrade, CancellationToken ct)
    {
        if (client.DaemonOff)
        {
            return Fail(1, "daemon_unavailable", "AH_DAEMON=off", ("source", "files"));
        }

        var running = upgrade ? null : await LiveAsync(ct).ConfigureAwait(false);

        // ah2-566 K4: an answer is "running" only while its pid is a live ahd for this home; one gone since it answered is started over
        if (running is not null && client.OwnershipOf(running.Pid) is not false)
        {
            return Ok(("source", "daemon"), ("status", "running"), ("started", false), ("pid", running.Pid), ("version", running.Health!.TryGetValue("version", out var rv) ? rv : JsonValue.Null));
        }

        var why = string.Empty;
        var info = await client
            .EnsureAsync(new EnsureOptions(AhdClient.StartWaitMs, Warn: (w, _) => why = w, Explicit: true), ct)
            .ConfigureAwait(false);
        if (info is null)
        {
            return Fail(1, "daemon_unavailable", why.Length > 0 ? why : "could not start", ("source", "files"));
        }

        return Ok(("source", "daemon"), ("status", "started"), ("started", true), ("pid", info.Pid), ("version", info.Health!.TryGetValue("version", out var v) ? v : JsonValue.Null));
    }

    internal async Task<VerbResult> StopAsync(CancellationToken ct, string reason = "stop")
    {
        var beforePid = client.DaemonPidOnFile();
        var (stopped, refusal) = await client.StopDaemonAsync(reason, ct).ConfigureAwait(false);
        if (stopped)
        {
            return Ok(("source", "daemon"), ("status", "stopped"), ("pid", beforePid is { } p ? p : JsonValue.Null));
        }

        if (refusal is not null)
        {
            return Fail(1, "drain_refused", $"the daemon refused to stop: {refusal}", ("source", "daemon"));
        }

        if (beforePid is { } alive && client.Dependencies.Inspector.PidAlive((int)alive) && client.IsOurDaemon((int)alive))
        {
            return Fail(1, "stop_failed", $"the daemon (pid {alive.ToString(CultureInfo.InvariantCulture)}) did not exit", ("source", "daemon"));
        }

        return Ok(("source", "files"), ("status", "not-running"));
    }

    internal async Task<VerbResult> RestartAsync(CancellationToken ct)
    {
        var beforePid = client.DaemonPidOnFile();
        var stopped = await StopAsync(ct, "restart").ConfigureAwait(false);
        if (stopped.Body["ok"].AsBool() == false)
        {
            return stopped;
        }

        var started = await StartAsync(false, ct).ConfigureAwait(false);
        if (started.Body["ok"].AsBool() == false)
        {
            return started;
        }

        var wasStopped = stopped.Body.Str("status") == "stopped";
        var body = Jx.Copy(started.Body);
        body["status"] = wasStopped ? "restarted" : "started";
        body["old_pid"] = wasStopped && beforePid is { } oldPid ? oldPid : JsonValue.Null;
        body["new_pid"] = started.Body.TryGetValue("pid", out var np) ? np : JsonValue.Null;
        return new VerbResult(0, body);
    }

    internal async Task<VerbResult> DrainNowAsync(bool require, CancellationToken ct)
    {
        var info = await client.DiscoverCoreAsync(AhdClient.DiscoverTimeoutMs, ct).ConfigureAwait(false);
        if (info is null)
        {
            return require
                ? Fail(DaemonDownExit, "daemon_down", "the daemon is not running", ("source", "files"), ("status", "not-running"))
                : Ok(("source", "files"), ("status", "not-running"));
        }

        if (info.Draining)
        {
            return Ok(("source", "daemon"), ("status", "draining"), ("pid", info.Pid));
        }

        var reply = await client.PostDrainAsync(info.Socket, "daemon drain", 2000, ct: ct).ConfigureAwait(false);
        if (AhdClient.DrainRefusal(reply) is { } refusal)
        {
            return Fail(1, "drain_refused", $"the daemon refused to drain: {refusal}", ("source", "daemon"), ("pid", info.Pid));
        }

        if (reply is null)
        {
            return client.Dependencies.Inspector.PidAlive(info.Pid)
                ? Fail(1, "drain_no_answer", $"the daemon (pid {info.Pid.ToString(CultureInfo.InvariantCulture)}) did not answer the drain request and is still running", ("source", "daemon"), ("pid", info.Pid))
                : Ok(("source", "files"), ("status", "not-running"));
        }

        return Ok(("source", "daemon"), ("status", "draining"), ("pid", info.Pid));
    }

    // ---------------------------------------------------------------- health, version, status, logs

    internal async Task<VerbResult> HealthAsync(CancellationToken ct)
    {
        var info = await client.DiscoverCoreAsync(AhdClient.DiscoverTimeoutMs, ct).ConfigureAwait(false);
        if (info is null)
        {
            return Fail(DaemonDownExit, "daemon_down", "the daemon is not running", ("source", "files"), ("status", "down"));
        }

        if (info.Draining)
        {
            return Fail(DaemonDownExit, "daemon_draining", "the daemon is shutting down", ("source", "daemon"), ("status", "draining"), ("pid", info.Pid));
        }

        var body = new JsonObject { ["ok"] = true, ["source"] = "daemon", ["status"] = "up" };
        foreach (var key in info.Health!.Keys)
        {
            body[key] = info.Health[key];
        }

        return new VerbResult(0, body);
    }

    internal async Task<VerbResult> VersionAsync(CancellationToken ct)
    {
        var clientBlock = Jx.Obj(("version", client.EffectiveVersion));
        var info = await LiveAsync(ct).ConfigureAwait(false);
        if (info is null)
        {
            return Ok(("source", "files"), ("client", clientBlock), ("daemon", JsonValue.Null), ("verdict", JsonValue.Null));
        }

        var h = info.Health!;
        var version = h.Str("version") ?? "?";
        JsonValue protocols = h.TryGetValue("protocols", out var ps) && ps.Kind == JsonKind.Array
            ? ps
            : Jx.Arr(h.TryGetValue("protocol", out var single) ? [single] : []);
        var supports = protocols.AsArray().Any(static x => x.Kind == JsonKind.Number && x.AsNumber() == 1);
        var cmp = Versions.Compare(client.EffectiveVersion, version);
        var verdict = !supports ? "unsupported" : cmp == 0 ? "equal" : cmp > 0 ? "client-newer" : "client-older";
        return Ok(
            ("source", "daemon"),
            ("client", clientBlock),
            ("daemon", Jx.Obj(("version", version), ("protocols", protocols), ("pid", info.Pid))),
            ("verdict", verdict));
    }

    private sealed record Gathered(string Source, JsonObject? Data);

    private async Task<Gathered> GatherAsync(Discovered? info, string path, CancellationToken ct)
    {
        if (info is not null)
        {
            try
            {
                var res = await client.HttpJsonAsync(info.Socket, "GET", path, null, 10_000, ct).ConfigureAwait(false);
                if (res.Status == 200 && res.Body.AsObject() is { } body)
                {
                    return new Gathered("daemon", body);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // the files answer instead
            }
        }

        var files = inspectFiles is null ? null : await inspectFiles(path, ct).ConfigureAwait(false);
        return new Gathered("files", files);
    }

    internal async Task<VerbResult> StatusAsync(CancellationToken ct)
    {
        var info = await LiveAsync(ct).ConfigureAwait(false);
        var regs = await GatherAsync(info, "/v1/inspect/registrations", ct).ConfigureAwait(false);
        var boxes = await GatherAsync(info, "/v1/inspect/boxes", ct).ConfigureAwait(false);
        var timers = await GatherAsync(info, "/v1/inspect/timers", ct).ConfigureAwait(false);
        var h = info?.Health;
        var clock = h?.Map("timer_state");
        var source = info is not null && new[] { regs, boxes, timers }.All(static g => g.Source == "daemon") ? "daemon" : "files";

        var body = new JsonObject { ["ok"] = true, ["source"] = source, ["up"] = info is not null };
        if (h is not null)
        {
            var started = h.Str("started_at");
            var uptime = started is not null && Timestamps.TryParseMs(started, out var startedMs)
                ? Math.Floor(((client.Dependencies.Clock.UtcNow.ToUnixTimeMilliseconds() - startedMs) / 1000) + 0.5)
                : double.NaN;
            body["version"] = h.TryGetValue("version", out var ver) ? ver : JsonValue.Null;
            body["pid"] = info!.Pid;
            body["uptime_s"] = double.IsNaN(uptime) ? JsonValue.Null : uptime;
            body["started_at"] = h.TryGetValue("started_at", out var sa) ? sa : JsonValue.Null;
            body["root"] = h.TryGetValue("root", out var root) ? root : JsonValue.Null;
            body["requests"] = h.TryGetValue("requests", out var req) && req.Kind != JsonKind.Null ? req : JsonValue.Null;
            body["errors"] = h.TryGetValue("errors", out var err) && err.Kind != JsonKind.Null ? err : JsonValue.Null;
            body["errors_by_code"] = h.Map("errors_by_code") is { } ebc ? ebc : new JsonObject();
            body["owns"] = h.Map("owns") is { } owns ? owns : JsonValue.Null;
            body["ladder"] = h.Map("ladder") is { } ladder ? ladder : JsonValue.Null;
        }

        var registrations = new JsonArray();
        if (regs.Data is not null && regs.Data.TryGetValue("registrations", out var rl) && rl.Kind == JsonKind.Array)
        {
            foreach (var r in rl.AsArray().Select(static x => x.AsObject()).Where(static x => x is not null))
            {
                if (!(r!.TryGetValue("live", out var live) && Truthy(live)))
                {
                    continue;
                }

                var item = new JsonObject();
                foreach (var key in new[] { "session_id", "address", "harness", "adapter" })
                {
                    if (r.TryGetValue(key, out var value))
                    {
                        item[key] = value;
                    }
                }

                registrations.Add(item);
            }
        }

        body["registrations"] = registrations;
        double overdue = 0;
        if (boxes.Data is not null && boxes.Data.TryGetValue("boxes", out var bl) && bl.Kind == JsonKind.Array)
        {
            foreach (var b in bl.AsArray().Select(static x => x.AsObject()).Where(static x => x is not null))
            {
                if (b!.TryGetValue("overdue", out var o) && o.Kind == JsonKind.Number)
                {
                    overdue += o.AsNumber();
                }
            }
        }

        body["overdue"] = overdue;
        var timerRows = new JsonArray();
        if (timers.Data is not null && timers.Data.TryGetValue("timers", out var tl) && tl.Kind == JsonKind.Array)
        {
            foreach (var t in tl.AsArray().Select(static x => x.AsObject()).Where(static x => x is not null))
            {
                var session = t!.Str("session");
                var state = session is not null ? clock?.Map(session) : null;
                var row = new JsonObject();
                if (t.TryGetValue("session", out var sv))
                {
                    row["session"] = sv;
                }

                if (t.TryGetValue("owned", out var ow))
                {
                    row["owned"] = ow;
                }

                if (t.TryGetValue("due", out var due) && due.Kind == JsonKind.Array)
                {
                    row["due"] = due.AsArray().Count;
                }

                row["last_tick"] = state is not null && state.TryGetValue("last_tick", out var lt) && lt.Kind != JsonKind.Null ? lt : JsonValue.Null;
                row["last_event"] = state is not null && state.TryGetValue("last_event", out var le) && le.Kind != JsonKind.Null ? le : JsonValue.Null;
                timerRows.Add(row);
            }
        }

        body["timers"] = timerRows;
        body["fallbacks"] = client.ReadFallbacks(h?.Str("started_at"));
        return new VerbResult(0, body);
    }

    /// <summary>JavaScript truthiness of a parsed JSON value.</summary>
    private static bool Truthy(JsonValue v) => v.Kind switch
    {
        JsonKind.Null => false,
        JsonKind.Bool => v.AsBool(),
        JsonKind.Number => v.AsNumber() != 0 && !double.IsNaN(v.AsNumber()),
        JsonKind.Text => v.AsString().Length > 0,
        _ => true,
    };

    internal VerbResult Logs(int? n)
    {
        var path = client.Paths.Log;
        var lines = new JsonArray();
        try
        {
            var all = client.Dependencies.Files.ReadText(path).Split('\n').Where(static l => l.Length > 0).ToArray();
            var take = Math.Min(n ?? DefaultLogLines, MaxLogLines);
            foreach (var line in all.Skip(Math.Max(0, all.Length - take)))
            {
                lines.Add(line);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // no log yet
        }

        return Ok(("source", "files"), ("path", path), ("lines", lines));
    }

    // ---------------------------------------------------------------- the command line

    private static readonly Dictionary<string, string[]> Verbs = new(StringComparer.Ordinal)
    {
        ["start"] = ["idle-exit-ms"],
        ["stop"] = AllFlagsOf,
        ["restart"] = AllFlagsOf,
        ["drain"] = ["require"],
        ["status"] = AllFlagsOf,
        ["health"] = AllFlagsOf,
        ["version"] = AllFlagsOf,
        ["logs"] = ["n"],
        ["heal"] = ["force", "trigger"],
    };

    public async Task<int> RunMsgAliasAsync(string verb, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (verb == "start")
        {
            var started = await StartAsync(true, cancellationToken).ConfigureAwait(false);
            if (started.Body["ok"].AsBool() == false)
            {
                var error = started.Body.Map("error")!;
                stderr.Write($"error: {error.Str("code")}: {error.Str("message")}\n");
                return 1;
            }

            var version = started.Body.TryGetValue("version", out var v) ? JsConvert.ToJsString(v) : "undefined";
            var pid = started.Body.TryGetValue("pid", out var p) ? JsConvert.ToJsString(p) : "undefined";
            stdout.Write($"ahd {version} pid {pid}\n");
            return 0;
        }

        if (verb != "stop")
        {
            throw new ArgumentException("the msg alias verbs are start and stop", nameof(verb));
        }

        var stopped = await StopAsync(cancellationToken).ConfigureAwait(false);
        if (stopped.Body["ok"].AsBool() == false)
        {
            var failure = stopped.Body.Map("error")!;
            stderr.Write($"error: {failure.Str("code")}: {failure.Str("message")}\n");
            return 1;
        }

        stdout.Write(stopped.Body.Str("status") == "stopped" ? "stopped\n" : "not running\n");
        return 0;
    }

    public async Task<int> RunAsync(string verb, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken = default)
    {
        var plain = args.Contains("--plain", StringComparer.Ordinal);

        int Usage(string message)
        {
            if (plain)
            {
                stderr.Write($"error: usage: {message}\n");
            }
            else
            {
                stdout.Write(JsonText.Stringify(Jx.Obj(("ok", false), ("error", Jx.Obj(("code", "usage"), ("message", message)))), 0) + "\n");
            }

            return 2;
        }

        if (!Verbs.TryGetValue(verb, out var allowed))
        {
            return Usage($"unknown verb {JsonText.Stringify(verb, 0)}; verbs: {string.Join('|', Verbs.Keys)}|inspect");
        }

        // The verb itself is the first positional, as in `ahd <verb> [flags]`.
        var argv = args.ToList();
        var cut = argv.IndexOf("--");
        var launch = cut < 0 ? null : argv.Skip(cut + 1).ToList();
        if (cut >= 0)
        {
            argv = argv.Take(cut).ToList();
        }

        var keys = new List<string>();
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var positionals = new List<string> { verb };
        var helpGiven = false;
        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            var key = a.StartsWith("--", StringComparison.Ordinal) ? a[2..] : a == "-n" ? "n" : null;
            if (key is null)
            {
                if (a.StartsWith('-') && a.Length > 1)
                {
                    return Usage($"unknown flag {a}");
                }

                positionals.Add(a);
            }
            else if (BoolFlags.Contains(key))
            {
                Set(key, "true");
            }
            else
            {
                var next = i + 1 < argv.Count ? argv[i + 1] : null;
                if (next is null || next.StartsWith("--", StringComparison.Ordinal) || (next.StartsWith('-') && key != "n"))
                {
                    Set(key, null);
                    if (key == "help")
                    {
                        continue;
                    }

                    return ValuedNeedingValue.Contains(key) ? Usage($"--{key} needs a value") : Usage($"unknown flag {a}");
                }

                Set(key, next);
                i++;
            }
        }

        void Set(string key, string? value)
        {
            if (!values.ContainsKey(key))
            {
                keys.Add(key);
            }

            values[key] = value;
            if (key == "help" && value is not null)
            {
                helpGiven = true;
            }
        }

        if (helpGiven || argv.Contains("--help", StringComparer.Ordinal))
        {
            stdout.Write(UsageText.Block + "\n");
            return 0;
        }

        if (positionals.Count > 1)
        {
            return Usage($"unexpected argument {JsonText.Stringify(positionals[1], 0)}");
        }

        foreach (var k in keys)
        {
            if (k != "cwd" && k != "plain" && k != "help" && !allowed.Contains(k))
            {
                return Usage($"{verb} does not take --{k}");
            }
        }

        if (launch is not null)
        {
            return Usage($"{verb} takes no -- command");
        }

        int? count = null;
        if (values.TryGetValue("n", out var nText) && nText is not null)
        {
            if (!nText.All(static c => c is >= '0' and <= '9') || nText.Length == 0
                || !long.TryParse(nText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1 || parsed > MaxLogLines)
            {
                return Usage($"-n must be a whole number from 1 to {MaxLogLines}");
            }

            count = (int)parsed;
        }

        if (values.TryGetValue("idle-exit-ms", out var idleText) && idleText is not null)
        {
            if (idleText.Length == 0 || idleText.Length > 9 || !idleText.All(static c => c is >= '0' and <= '9')
                || long.Parse(idleText, CultureInfo.InvariantCulture) <= 0)
            {
                return Usage("--idle-exit-ms must be a whole number of milliseconds, 1 to 999999999");
            }

            client.SpawnIdleExitMs = long.Parse(idleText, CultureInfo.InvariantCulture);
        }

        if (verb == "heal")
        {
            return await HealAsync(values.TryGetValue("force", out var fc) && fc == "true", values.TryGetValue("trigger", out var tg) ? tg ?? "manual" : "manual", stdout, stderr).ConfigureAwait(false);
        }

        var require = values.TryGetValue("require", out var rq) && rq == "true";
        var result = verb switch
        {
            "start" => await StartAsync(false, cancellationToken).ConfigureAwait(false),
            "stop" => await StopAsync(cancellationToken).ConfigureAwait(false),
            "restart" => await RestartAsync(cancellationToken).ConfigureAwait(false),
            "drain" => await DrainNowAsync(require, cancellationToken).ConfigureAwait(false),
            "status" => await StatusAsync(cancellationToken).ConfigureAwait(false),
            "health" => await HealthAsync(cancellationToken).ConfigureAwait(false),
            "version" => await VersionAsync(cancellationToken).ConfigureAwait(false),
            _ => Logs(count),
        };
        Emit(result, plain, verb, stdout, stderr);
        return result.Code;
    }

    /// <summary>
    /// <c>ahd heal [--force]</c>: judges the daemon of this state home and replaces it only when it is proven hung. Single-flight: a
    /// second heal while one runs exits 0 and does nothing. Progress goes to stderr, one line per probe. <c>--force</c> skips the
    /// backoff and the cap, never the verdict, and is the user's alone: any other caller is refused before anything runs.
    /// Exit 0 for restarted, not hung and heal in progress; 1 for capped, failed or refused.
    /// </summary>
    internal Task<int> HealAsync(bool force, string triggerName, TextWriter stdout, TextWriter stderr) =>
        HealAsync(force, triggerName, stdout, stderr, HealTiming.Production);

    internal async Task<int> HealAsync(bool force, string triggerName, TextWriter stdout, TextWriter stderr, HealTiming timing)
    {
        var deps = client.Dependencies;
        if (force)
        {
            var user = deps.CallerIsUser?.Invoke();
            if (user != true)
            {
                stderr.Write(deps.CallerIsUser is not null && user is null
                    ? "ahd heal --force: refused; could not tell who is asking. Run it again.\n"
                    : "ahd heal --force: refused; only the user may restart ahd past the backoff and the restart cap. Run it yourself in a plain terminal.\n");
                return 1;
            }
        }

        var trigger = triggerName switch
        {
            "direct-write" => HealTrigger.DirectWrite,
            "failsafe" => HealTrigger.Failsafe,
            _ => HealTrigger.Manual,
        };
        var pidFile = Path.Combine(client.Paths.Run, "heal.pid");
        using var held = deps.Locks.TryHold(Path.Combine(client.Paths.Run, "heal.lock"));
        if (held is null)
        {
            stdout.Write("heal in progress" + (DaemonClientModule.HealPid(deps.Files, client.Paths.Run) is { } other ? $" (pid {other})" : string.Empty) + "\n");
            return 0;
        }

        try
        {
            deps.Writer.WriteAtomic(pidFile, pidFile + ".tmp", Utf8Text.Encode(Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "\n"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the pid only makes "heal in progress" name its holder
        }

        var environment = new AhdHealEnvironment(client, deps, client.Paths, line => stderr.Write(line + "\n"));
        HealOutcome outcome;
        try
        {
            outcome = await new AhdHeal(environment, timing).RunAsync(trigger, force).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                _ = deps.Writer.Unlink(pidFile);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // a pid file left behind names a dead pid, which nobody takes for a running heal
            }
        }

        switch (outcome.Kind)
        {
            case "restarted":
                stdout.Write("restarted by heal: " + outcome.Detail + "\n");
                return 0;
            case "not-hung":
                stdout.Write("ahd is not hung (" + outcome.Detail + "); nothing was changed\n");
                return 0;
            case "capped":
                stderr.Write("heal-capped: " + outcome.Detail + "; only the user may pass --force\n");
                return 1;
            default:
                stderr.Write("heal-" + outcome.Kind + ": " + outcome.Detail + "\n");
                return 1;
        }
    }

    private static void Emit(VerbResult result, bool plain, string verb, TextWriter stdout, TextWriter stderr)
    {
        var body = result.Body;
        if (plain && body.TryGetValue("ok", out var ok) && ok.Kind == JsonKind.Bool && !ok.AsBool() && !body.ContainsKey("results"))
        {
            var error = body.Map("error")!;
            stderr.Write($"error: {error.Str("code")}: {error.Str("message")}\n");
        }
        else
        {
            stdout.Write((plain ? Render(body, verb, DateTimeOffset.UtcNow) : JsonText.Stringify(body, 0)) + "\n");
        }
    }

    // ---------------------------------------------------------------- plain rendering

    private static string Str(JsonValue v) => v.Kind switch
    {
        JsonKind.Null => "null",
        JsonKind.Bool => v.AsBool() ? "true" : "false",
        JsonKind.Number => JsonText.Stringify(v, 0),
        JsonKind.Text => v.AsString(),
        _ => JsonText.Stringify(v, 0),
    };

    /// <summary>A member's JavaScript string form in a template literal (<c>undefined</c> when absent).</summary>
    private static string Field(JsonObject o, string key) => o.TryGetValue(key, out var v) ? Str(v) : "undefined";

    private static string Or(JsonObject o, string key, string fallback) =>
        o.TryGetValue(key, out var v) && Truthy(v) ? Str(v) : fallback;

    internal static string LadderLine(JsonObject? l)
    {
        double N(string k) => l is not null && l.TryGetValue(k, out var v) && v.Kind == JsonKind.Number ? v.AsNumber() : 0;
        if (l is not null && (N("pending") != 0 || N("sending") != 0 || N("paused") != 0 || N("escalated") != 0))
        {
            return $"ladder: pending {Field(l, "pending")}, sending {Field(l, "sending")}, paused {Field(l, "paused")}, escalated {Field(l, "escalated")}";
        }

        return "ladder: none";
    }

    internal static string Render(JsonObject b, string verb, DateTimeOffset now)
    {
        var o = new List<string>();
        if (b.Str("source") == "files" && verb is "status" or "version")
        {
            o.Add("daemon down, showing files");
        }

        if (b.ContainsKey("up"))
        {
            var up = b.TryGetValue("up", out var upv) && Truthy(upv);
            o.Add(up
                ? $"up  version {Field(b, "version")}  pid {Field(b, "pid")}  uptime {Field(b, "uptime_s")}s  requests {Field(b, "requests")}"
                : "down");
            if (up)
            {
                o.Add($"root {Field(b, "root")}");
                var owns = b.Map("owns");
                var timersOwned = owns is not null && owns.TryGetValue("timers", out var tv) && tv.Kind == JsonKind.Array
                    ? string.Join(',', tv.AsArray().Select(Str))
                    : string.Empty;
                o.Add($"owns mailbox={(owns is null ? "undefined" : Field(owns, "mailbox"))} status={(owns is null ? "undefined" : Field(owns, "status"))} timers={timersOwned}");
                var byCode = b.Map("errors_by_code");
                var codes = byCode is not null && byCode.Count > 0
                    ? "  by code: " + string.Join(' ', byCode.Keys.Select(k => $"{k}={Str(byCode[k])}"))
                    : string.Empty;
                o.Add($"errors {(b.TryGetValue("errors", out var ev) && ev.Kind != JsonKind.Null ? Str(ev) : "?")} (5xx){codes}");
            }

            var regs = b.TryGetValue("registrations", out var rv) && rv.Kind == JsonKind.Array ? rv.AsArray() : new JsonArray();
            o.Add("registrations: " + (regs.Count > 0
                ? string.Join("; ", regs.Select(static x =>
                {
                    var r = x.AsObject()!;
                    return $"{Field(r, "address")} ({Or(r, "harness", "?")}, adapter {Field(r, "adapter")})";
                }))
                : "none"));
            o.Add($"overdue: {Field(b, "overdue")}");
            if (up)
            {
                o.Add(LadderLine(b.Map("ladder")));
            }

            if (b.TryGetValue("timers", out var tr) && tr.Kind == JsonKind.Array)
            {
                foreach (var tx in tr.AsArray())
                {
                    var t = tx.AsObject()!;
                    var owned = t.TryGetValue("owned", out var ov) && Truthy(ov) ? "owned" : "not owned";
                    var lastTick = Or(t, "last_tick", "none yet");
                    var le = t.Map("last_event");
                    var lastEvent = le is not null ? $"{Field(le, "kind")} at {Field(le, "at")}" : "none";
                    o.Add($"timer {Field(t, "session")}: {owned}; due {Field(t, "due")}; last tick {lastTick}; last event {lastEvent}");
                }
            }

            var fb = b.Map("fallbacks");
            o.Add($"fallbacks to the files: {(fb is null ? "undefined" : Field(fb, "count"))}");
            if (fb is not null && fb.TryGetValue("last", out var last) && last.Kind == JsonKind.Array)
            {
                foreach (var fx in last.AsArray())
                {
                    var f = fx.AsObject()!;
                    o.Add($"  {Field(f, "at")}  {Field(f, "verb")}  {Field(f, "reason")}");
                }

                if (up && last.AsArray().Any(static fx => fx.AsObject()!.TryGetValue("reason", out var r) && !(r.Kind == JsonKind.Text && r.AsString() == "down")))
                {
                    o.Add("warning: a verb fell back to the files while the daemon was up; a daemon defect may be masked");
                }
            }
        }
        else if (b.Map("client") is { } c)
        {
            var daemon = b.Map("daemon");
            o.Add($"client {Field(c, "version")}");
            if (daemon is not null)
            {
                var protocols = daemon.TryGetValue("protocols", out var pv) && pv.Kind == JsonKind.Array ? string.Join(',', pv.AsArray().Select(Str)) : string.Empty;
                o.Add($"daemon {Field(daemon, "version")}  protocols {protocols}  pid {Field(daemon, "pid")}");
            }
            else
            {
                o.Add("daemon none");
            }

            o.Add($"verdict: {Field(b, "verdict")}");
        }
        else if (b.TryGetValue("lines", out var lines) && lines.Kind == JsonKind.Array)
        {
            o.AddRange(lines.AsArray().Select(Str));
        }
        else if (b.Str("status") == "up")
        {
            o.Add($"up  version {Field(b, "version")}  pid {Field(b, "pid")}  requests {Field(b, "requests")}");
        }
        else if (b.TryGetValue("status", out var st) && Truthy(st))
        {
            var pid = Truthy2(b, "pid") ? $"  pid {Field(b, "pid")}" : string.Empty;
            var version = Truthy2(b, "version") ? $"  version {Field(b, "version")}" : string.Empty;
            var old = Truthy2(b, "old_pid") ? $"  (old pid {Field(b, "old_pid")})" : string.Empty;
            o.Add($"{Str(st)}{pid}{version}{old}");
        }

        _ = now;
        return string.Join('\n', o);
    }

    private static bool Truthy2(JsonObject o, string key) => o.TryGetValue(key, out var v) && Truthy(v);

    // The lifecycle's byte-level text helpers share Ah.Core's UTF-8 handling.
    internal static string Decode(ReadOnlySpan<byte> bytes) => Utf8Text.Decode(bytes);
}
