using System.Globalization;
using System.Runtime.InteropServices;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Verbs;

/// <summary>
/// <c>ahd check</c>, the conformance self-check: seeds a fixture in a fresh <c>/tmp/ahprobe.*</c> sandbox, runs every read-only
/// <c>status</c> and <c>inspect</c> verb once with the daemon off and once against a daemon of its own, and compares the
/// answers minus the fields only a live daemon has. The caller's AH_HOME and daemon are never touched: the sandbox daemon is
/// stopped and the sandbox removed before it returns.
/// </summary>
internal static class CheckVerb
{
    private const string SandboxPrefix = "/tmp/ahprobe.";
    private const int SandboxSuffix = 6;
    private const int CaseTimeoutSeconds = 30;
    private const int ExitWaitSeconds = 5;
    private const string DefaultIdleExitMs = "300000";

    /// <summary>Top-level keys only a live daemon has (or that move between two calls).</summary>
    private static readonly HashSet<string> Volatile = new(StringComparer.Ordinal)
    {
        "source", "rev", "pid", "uptime", "uptime_s", "owns", "requests", "up", "version", "root", "started_at",
        "errors", "errors_by_code", "ladder",
    };

    private static readonly string[] TimerVolatile = ["last_tick", "last_event"];

    internal static int Run(CliContext context, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var root = MakeSandbox();
        if (root is null)
        {
            return Emit(stdout, 1, Fail("sandbox", "the sandbox directory could not be created"));
        }

        // An interrupt or a termination must not leave the sandbox daemon or the sandbox behind: the run is cleaned up once and the
        // process exits with 128 plus the signal's number.
        var cleaned = 0;
        var daemonPid = 0;

        // Cancelled by the signal handler before it removes the sandbox: the run's own thread stops writing into it.
        using var interrupted = new CancellationTokenSource();

        // Set when the one cleanup has finished, so a caller that lost the race to run it still waits for its result.
        using var cleanedUp = new ManualResetEventSlim();

        void Once()
        {
            if (Interlocked.Exchange(ref cleaned, 1) == 0)
            {
                try
                {
                    daemonPid = ReadDaemonPid(root);
                    Cleanup(context, root);
                }
                finally
                {
                    cleanedUp.Set();
                }
            }
            else
            {
                _ = cleanedUp.Wait(TimeSpan.FromSeconds(ExitWaitSeconds));
            }
        }

        void OnSignal(PosixSignalContext signal, int number)
        {
            signal.Cancel = true;
            interrupted.Cancel();
            Once();
            var gone = WaitUntilGone(root, daemonPid, TimeSpan.FromSeconds(ExitWaitSeconds));
            stderr.Write($"check: interrupted by signal {number.ToString(CultureInfo.InvariantCulture)}; the sandbox daemon and the sandbox {(gone ? "were removed" : "are still exiting")}\n");
            System.Environment.Exit(128 + number);
        }

        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => OnSignal(c, 2));
        using var hangup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, c => OnSignal(c, 1));
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => OnSignal(c, 15));
        try
        {
            var code = Check(context, root, interrupted.Token, out var body);
            if (interrupted.IsCancellationRequested)
            {
                // The handler is removing the sandbox, and the run it interrupted fails on its own (the daemon it was starting is
                // gone); returning now would exit with that failure's status instead of the signal's.
                Thread.Sleep(Timeout.Infinite);
            }

            return Emit(stdout, code, body);
        }
        catch (Exception) when (interrupted.IsCancellationRequested)
        {
            // the handler is removing the sandbox and ends the process with the signal's status
            Thread.Sleep(Timeout.Infinite);
            throw;
        }
        finally
        {
            Once();
        }
    }

    private static int Check(CliContext context, string root, CancellationToken stop, out JsonObject body)
    {
        var ah = root + "/ah";
        var proj = root + "/proj";
        var home = root + "/home";
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(proj);
        var baseEnv = Environment(context, home, ah);
        _ = context.Runner.Run(new ProcessRequest(["git", "-C", proj, "init", "-q"], Environment: baseEnv));

        string hier;
        try
        {
            hier = Seed(context, root, proj, ah, home, stop);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            body = Fail("fixture", e.Message);
            return 1;
        }

        string[][] cases =
        [
            ["status"],
            ["inspect", "boxes"],
            ["inspect", "entries", "--address", "tm-implementor@tm", "--all"],
            ["inspect", "registrations"],
            ["inspect", "timers"],
            ["inspect", "events"],
            ["inspect", "status", "--dir", hier],
        ];
        var names = cases.Select(static c => string.Join(' ', c.Take(c[0] == "inspect" ? 2 : 1))).ToList();

        stop.ThrowIfCancellationRequested();
        var off = cases.Select(c => RunCase(context, c, proj, baseEnv, daemonOff: true, stop)).ToList();
        // The daemon leaves by itself when idle (the --idle-exit-ms option of serve), so one orphaned by a kill that cannot be handled
        // does not outlive the run for long.
        var idleExitMs = context.Env.GetTestOnly("AHD_IDLE_EXIT_MS") is { Length: > 0 } idle ? idle : DefaultIdleExitMs;
        var started = RunCase(context, ["start", "--idle-exit-ms", idleExitMs], proj, baseEnv, daemonOff: false, stop).Answer;
        if (started is not { } s || !(s.Get("ok") is { IsTruthy: true }))
        {
            var message = started?.Get("error")?.Member("message") is { IsText: true } m ? m.AsString() : "no answer";
            body = Fail("daemon_unavailable", $"the sandbox daemon did not start: {message}");
            return 1;
        }

        stop.ThrowIfCancellationRequested();
        var on = cases.Select(c => RunCase(context, c, proj, baseEnv, daemonOff: false, stop)).ToList();
        var mismatches = new JsonArray();
        var mismatched = new List<string>();
        for (var i = 0; i < cases.Length; i++)
        {
            var a = off[i].Answer;
            var b = on[i].Answer;
            string? detail = null;
            if (a is null || !(a.Get("ok") is { IsTruthy: true }) || b is null || !(b.Get("ok") is { IsTruthy: true }))
            {
                detail = $"a run failed (files exit {off[i].Code}, daemon exit {on[i].Code})";
            }
            else if (!(b.Get("source") is { IsText: true } src && src.AsString() == "daemon"))
            {
                detail = "the sandbox daemon did not serve it";
            }
            else if (JsonText.Stringify(Normalize(a), 0) != JsonText.Stringify(Normalize(b), 0))
            {
                detail = "the daemon's answer differs from the files'";
            }

            if (detail is not null)
            {
                mismatches.Add(new JsonObject { ["case"] = names[i], ["detail"] = detail });
                mismatched.Add(names[i]);
            }
        }

        if (mismatched.Count > 0)
        {
            body = Fail("mismatch", string.Join(", ", mismatched), new JsonObject { ["mismatches"] = mismatches, ["sandbox"] = root });
            return 4;
        }

        var listed = new JsonArray();
        foreach (var n in names)
        {
            listed.Add(n);
        }

        body = new JsonObject
        {
            ["ok"] = true,
            ["source"] = "files",
            ["sandbox"] = root,
            ["checked"] = cases.Length,
            ["cases"] = listed,
        };
        return 0;
    }

    private static JsonObject Normalize(JsonValue answer)
    {
        var source = answer.AsObject();
        var kept = new JsonObject();
        foreach (var key in source.Keys)
        {
            if (!Volatile.Contains(key))
            {
                kept[key] = source[key];
            }
        }

        if (kept.Get("timers") is { IsArray: true } timers)
        {
            var rows = new JsonArray();
            foreach (var member in timers.AsArray())
            {
                if (!member.IsMap)
                {
                    rows.Add(member);
                    continue;
                }

                var row = new JsonObject();
                foreach (var key in member.AsObject().Keys)
                {
                    if (!TimerVolatile.Contains(key, StringComparer.Ordinal))
                    {
                        row[key] = member.AsObject()[key];
                    }
                }

                rows.Add(row);
            }

            kept["timers"] = rows;
        }

        // files-off-disk F3a: each side computes the status document at its own instant
        if (kept.Get("status") is { IsMap: true } status)
        {
            kept["status"] = Ah.Watch.WatchModule.StatusWithoutClockStamps(status);
        }

        return kept;
    }

    private sealed record CaseRun(JsonObject? Answer, int? Code);

    private static CaseRun RunCase(CliContext context, string[] args, string proj, IReadOnlyDictionary<string, string?> env, bool daemonOff, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var merged = new Dictionary<string, string?>(env, StringComparer.Ordinal);
        if (daemonOff)
        {
            merged["AH_DAEMON"] = "off";
        }

        var argv = new List<string> { context.ProcessPath };
        argv.AddRange(args);
        argv.Add("--cwd");
        argv.Add(proj);
        var result = context.Runner.Run(new ProcessRequest(argv, proj, merged, default, TimeSpan.FromSeconds(CaseTimeoutSeconds)));
        try
        {
            var parsed = JsonText.Parse(result.Stdout.Span);
            return new CaseRun(parsed.IsMap ? parsed.AsObject() : null, result.ExitCode);
        }
        catch (JsonParseException)
        {
            return new CaseRun(null, result.ExitCode);
        }
    }

    internal static Dictionary<string, string?> Environment(CliContext context, string home, string ah)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = context.Env.GetVariable("PATH"),
            ["HOME"] = home,
            ["AH_HOME"] = ah,
        };
        if (context.Env.TestMode)
        {
            env["AH_TEST"] = "1";
        }

        return env;
    }

    /// <summary>Writes the fixture the nine verbs read: a team, two boxes, a heartbeat each, two registrations and a status document.</summary>
    private static string Seed(CliContext context, string root, string proj, string ah, string home, CancellationToken stop)
    {
        var env = new SandboxEnvironment(context.Env, home, ah);
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, context.Runner, new DaemonComposition.InProcessDaemonClient());
        var hier = Ah.Core.Text.AhPaths.ProjectDir(proj);
        var teamFile = hier + "/teams/tm.json";
        Directory.CreateDirectory(hier + "/teams");
        var pid = System.Environment.ProcessId;
        var team = new JsonObject
        {
            ["team_id"] = "t-tm",
            ["orchestrator"] = new JsonObject { ["pid"] = pid, ["session_id"] = "orch-tm" },
            ["members"] = new JsonArray
            {
                new JsonObject { ["role"] = "implementor", ["name"] = "tm-implementor", ["transport_id"] = "p-tm-implementor" },
            },
        };
        File.WriteAllText(teamFile, JsonText.Stringify(team, 0) + "\n");

        var mail = graph.Mailbox;
        var impl = mail.BoxDirFor(teamFile, "tm-implementor");
        var orch = mail.BoxDirFor(teamFile, "orchestrator");
        var now = graph.Io.Clock.UtcNow;
        var longAgo = Iso(now.AddHours(-3));

        JsonObject Note(string id, string created) => new()
        {
            ["v"] = 1,
            ["id"] = id,
            ["from"] = "orchestrator@tm",
            ["to"] = "tm-implementor@tm",
            ["kind"] = "note",
            ["route"] = "pull",
            ["text"] = "probe",
            ["created"] = created,
        };

        Append(mail, impl, Note("p1", longAgo), stop);
        Append(mail, impl, Note("p2", longAgo), stop);
        _ = mail.AckIds(impl, ["p2"], "cli");
        Append(
            mail,
            orch,
            new JsonObject
            {
                ["v"] = 1,
                ["id"] = "ev1",
                ["from"] = "ahd",
                ["to"] = "orchestrator@tm",
                ["kind"] = "event",
                ["event"] = "check-in-due",
                ["route"] = "pull",
                ["text"] = "probe event",
                ["data"] = new JsonObject { ["request_id"] = "r-probe" },
                ["created"] = Iso(now),
            },
            stop);
        stop.ThrowIfCancellationRequested();
        mail.WriteHeartbeat(impl, "probe", "probe-impl", 3_600_000);
        mail.WriteHeartbeat(orch, "probe", "orch-tm", 3_600_000);
        stop.ThrowIfCancellationRequested();
        _ = mail.WriteRegistration(new RegistrationWrite("orch-tm", [new BoxRef("orchestrator@tm", orch)], teamFile, "probe", pid, Cwd: proj));
        _ = mail.WriteRegistration(new RegistrationWrite("gone", [new BoxRef("tm-implementor@tm", impl)], teamFile, "probe", 2147483646, Cwd: proj));
        stop.ThrowIfCancellationRequested();
        File.WriteAllText(
            hier + "/status.json",
            JsonText.Stringify(new JsonObject { ["v"] = 1, ["probe"] = true, ["version"] = 1, ["root"] = "fixture" }, 0) + "\n");
        _ = root;
        return hier;
    }

    private static void Append(IMailbox mail, string dir, JsonObject entry, CancellationToken stop)
    {
        stop.ThrowIfCancellationRequested();
        var result = mail.AppendEntry(dir, entry);
        if (result.Failure is { } failure)
        {
            throw new InvalidOperationException($"{failure.Code}: {failure.Message}");
        }
    }

    private static string Iso(DateTimeOffset at) =>
        at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    internal static string? MakeSandbox()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var suffix = string.Create(SandboxSuffix, 0, static (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = alphabet[System.Security.Cryptography.RandomNumberGenerator.GetInt32(alphabet.Length)];
                }
            });
            var path = SandboxPrefix + suffix;
            if (Directory.Exists(path) || File.Exists(path))
            {
                continue;
            }

            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }

        return null;
    }

    internal static bool IsSandbox(string path) =>
        path.StartsWith(SandboxPrefix, StringComparison.Ordinal)
        && path.Length == SandboxPrefix.Length + SandboxSuffix
        && path[SandboxPrefix.Length..].All(char.IsAsciiLetterOrDigit);

    private static void Cleanup(CliContext context, string root)
    {
        if (!IsSandbox(root))
        {
            return;
        }

        var env = Environment(context, root + "/home", root + "/ah");
        _ = context.Runner.Run(new ProcessRequest([context.ProcessPath, "stop", "--cwd", root + "/proj"], root + "/proj", env, default, TimeSpan.FromSeconds(CaseTimeoutSeconds)));
        KillDaemonLeft(context, root);
        // A write that was in flight when a signal arrived can land after a removal; it is swept until the tree stays gone.
        for (var attempt = 0; attempt < 20 && Directory.Exists(root); attempt++)
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // left for the next pass, and finally for the next reaper
            }

            if (Directory.Exists(root))
            {
                Thread.Sleep(25);
            }
        }
    }

    /// <summary>The pid in the sandbox's <c>ahd.json</c>, or 0 when there is none.</summary>
    private static int ReadDaemonPid(string root)
    {
        try
        {
            var parsed = JsonText.Parse(File.ReadAllBytes(root + "/ah/run/ahd.json"));
            return parsed.Member("pid") is { IsNumber: true } n && n.AsNumber() > 1 ? (int)n.AsNumber() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonParseException)
        {
            return 0;
        }
    }

    /// <summary>Waits, up to <paramref name="limit"/>, for the sandbox directory to be gone and the daemon with that pid to have exited.</summary>
    private static bool WaitUntilGone(string root, int daemonPid, TimeSpan limit)
    {
        var until = DateTime.UtcNow + limit;
        while (true)
        {
            if (!Directory.Exists(root) && !ProcessAlive(daemonPid))
            {
                return true;
            }

            if (DateTime.UtcNow >= until)
            {
                return false;
            }

            Thread.Sleep(25);
        }
    }

    private static bool ProcessAlive(int pid)
    {
        if (pid <= 1)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// A daemon that did not go with <c>stop</c> is killed, but only when its pid is the one in the sandbox's <c>ahd.json</c> and its
    /// command line is a <c>serve</c> that ends with the sandbox's state home.
    /// </summary>
    private static void KillDaemonLeft(CliContext context, string root)
    {
        var pid = ReadDaemonPid(root);
        if (pid == 0)
        {
            return;
        }

        var text = pid.ToString(CultureInfo.InvariantCulture);
        var ps = context.Runner.Run(new ProcessRequest(
            ["ps", "-ww", "-o", "command=", "-p", text],
            Environment: new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin", ["LC_ALL"] = "C" },
            Timeout: TimeSpan.FromSeconds(5)));
        var command = ps.ExitCode == 0 ? System.Text.Encoding.UTF8.GetString(ps.Stdout.Span).Trim() : string.Empty;
        if (command.Contains(" serve", StringComparison.Ordinal) && command.EndsWith($" --ah-home {root}/ah", StringComparison.Ordinal))
        {
            _ = context.Runner.Run(new ProcessRequest(
                ["kill", "-KILL", text],
                Environment: new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" },
                Timeout: TimeSpan.FromSeconds(5)));
        }
    }

    private static JsonObject Fail(string code, string message, JsonObject? extra = null)
    {
        var body = new JsonObject { ["ok"] = false };
        if (extra is not null)
        {
            foreach (var key in extra.Keys)
            {
                body[key] = extra[key];
            }
        }

        body["error"] = new JsonObject { ["code"] = code, ["message"] = message };
        return body;
    }

    private static int Emit(TextWriter stdout, int code, JsonObject body)
    {
        stdout.Write(JsonText.Stringify(body, 0) + "\n");
        return code;
    }

    /// <summary>The caller's environment with the home and the state home replaced by the sandbox's.</summary>
    internal sealed class SandboxEnvironment(IAhEnvironment inner, string homeDirectory, string stateHome) : IAhEnvironment
    {
        public string? GetVariable(string name) => name switch
        {
            "HOME" => HomeDirectory,
            "AH_HOME" => AhHome,
            _ => inner.GetVariable(name),
        };

        public bool TestMode => inner.TestMode;

        public string? GetTestOnly(string name) => inner.GetTestOnly(name);

        public string HomeDirectory => homeDirectory;

        public string AhHome => stateHome;

        public PluginInfo? Plugin => inner.Plugin;
    }
}
