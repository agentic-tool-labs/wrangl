using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Mesh;
using Ah.Roster;
using Ah.Watch;

namespace Ah.Cli.Verbs.AhdCtl;

/// <summary>
/// <c>ahd contract</c> and <c>ahd inspect &lt;boxes|entries|registrations|timers|events|status&gt;</c>: the read-only
/// views of the daemon's mailbox and the participation contract. Each asks the daemon when one is up and answers from the same files,
/// through the same functions, when it is not; none ever starts a daemon. Output is one JSON object carrying <c>ok</c> and
/// <c>source</c>; <c>--plain</c> renders it as text. Exit codes: 0 success, 1 the operation failed, 2 usage.
/// </summary>
internal static class AhdCtlVerb
{
    internal const int NotComposed = 70;

    private const int MaxEntries = 200;

    private static readonly HashSet<string> BoolFlags = new(StringComparer.Ordinal)
    {
        "plain", "pending", "acked", "all", "require", "once", "hook", "follow",
    };

    private static readonly HashSet<string> NeedsValue = new(StringComparer.Ordinal)
    {
        "cwd", "n", "address", "session", "dir", "harness", "path", "budget", "set",
    };

    private static readonly string[] EntryModes = ["pending", "acked", "all"];

    private static readonly string[] InspectSubs = ["boxes", "entries", "registrations", "timers", "events", "status"];

    private static readonly Dictionary<string, string[]> InspectFlags = new(StringComparer.Ordinal)
    {
        ["boxes"] = ["address"],
        ["entries"] = ["address", "pending", "acked", "all", "n"],
        ["registrations"] = [],
        ["timers"] = ["session"],
        ["events"] = ["address"],
        ["status"] = ["dir"],
    };

    private sealed record Result(int Code, JsonObject Body);

    private sealed class Usage(string message) : Exception(message);

    private static Result Ok(JsonObject? rest = null, string? source = null)
    {
        var body = new JsonObject { ["ok"] = true };
        if (source is not null)
        {
            body["source"] = source;
        }

        if (rest is not null)
        {
            foreach (var key in rest.Keys)
            {
                body[key] = rest[key];
            }
        }

        return new Result(0, body);
    }

    private static Result Fail(int code, string error, string message) =>
        new(code, new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = error, ["message"] = message } });

    /// <summary>Runs <paramref name="verb"/> (<c>contract</c> or <c>inspect</c>) with the arguments after its name.</summary>
    internal static int Run(CliContext context, string verb, IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, IDaemonClient? client = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var plain = args.Contains("--plain", StringComparer.Ordinal);
        if (context.Services is not { } services)
        {
            stderr.WriteLine($"ahd {verb}: the services have not been composed");
            return NotComposed;
        }

        if (verb == "ui")
        {
            return UiVerb.Run(context, client ?? services.Value.Daemon, args, stdout, stderr);
        }

        try
        {
            return Execute(context, services, client, verb, args, plain, stdout, stderr);
        }
        catch (Usage u)
        {
            if (plain)
            {
                stderr.Write($"error: usage: {u.Message}\n");
            }
            else
            {
                stdout.Write(JsonText.Stringify(new JsonObject { ["ok"] = false, ["error"] = new JsonObject { ["code"] = "usage", ["message"] = u.Message } }, 0) + "\n");
            }

            return 2;
        }
    }

    // ---------------------------------------------------------------- the command line

    private sealed class Parsed
    {
        internal List<string> Positionals { get; } = [];

        internal List<string> Keys { get; } = [];

        internal Dictionary<string, string?> Values { get; } = new(StringComparer.Ordinal);

        internal List<string>? Launch { get; set; }

        internal bool Has(string key) => Values.TryGetValue(key, out var v) && v is not null;

        internal string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;
    }

    private static Parsed Parse(IReadOnlyList<string> args, string verb)
    {
        var parsed = new Parsed();
        parsed.Positionals.Add(verb);
        var argv = args.ToList();
        var cut = argv.IndexOf("--");
        if (cut >= 0)
        {
            parsed.Launch = argv.Skip(cut + 1).ToList();
            argv = argv.Take(cut).ToList();
        }

        void Set(string key, string? value)
        {
            if (!parsed.Values.ContainsKey(key))
            {
                parsed.Keys.Add(key);
            }

            parsed.Values[key] = value;
        }

        for (var i = 0; i < argv.Count; i++)
        {
            var a = argv[i];
            var key = a.StartsWith("--", StringComparison.Ordinal) ? a[2..] : a == "-n" ? "n" : null;
            if (key is null)
            {
                if (a.StartsWith('-') && a.Length > 1)
                {
                    throw new Usage($"unknown flag {a}");
                }

                parsed.Positionals.Add(a);
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

                    throw new Usage(NeedsValue.Contains(key) ? $"--{key} needs a value" : $"unknown flag {a}");
                }

                Set(key, next);
                i++;
            }
        }

        return parsed;
    }

    private static int Execute(CliContext context, Lazy<ServiceGraph> services, IDaemonClient? client, string verb, IReadOnlyList<string> args, bool plain, TextWriter stdout, TextWriter stderr)
    {
        var p = Parse(args, verb);
        if (p.Values.ContainsKey("help") || args.Contains("--help", StringComparer.Ordinal))
        {
            // The usage block is the lifecycle verbs' one; asking one of them for --help prints it.
            return Ah.DaemonClient.DaemonClientModule.Lifecycle is { } lifecycle
                ? lifecycle.RunAsync("status", ["--help"], stdout, stderr).GetAwaiter().GetResult()
                : NotComposed;
        }

        var sub = verb == "inspect" ? p.Positionals.ElementAtOrDefault(1) : null;
        string[]? allowed;
        if (verb == "inspect")
        {
            allowed = sub is not null && InspectFlags.TryGetValue(sub, out var flags) ? flags : null;
            if (allowed is null)
            {
                throw new Usage($"inspect needs one of {string.Join('|', InspectSubs)}");
            }
        }
        else
        {
            allowed = [];
        }

        var maxArgs = verb == "inspect" ? 2 : 1;
        if (p.Positionals.Count > maxArgs)
        {
            throw new Usage($"unexpected argument {JsonText.Stringify(p.Positionals[maxArgs], 0)}");
        }

        foreach (var k in p.Keys)
        {
            if (k is not ("cwd" or "plain" or "help") && !allowed.Contains(k, StringComparer.Ordinal))
            {
                throw new Usage($"{verb}{(sub is not null ? " " + sub : string.Empty)} does not take --{k}");
            }
        }

        if (p.Launch is not null)
        {
            throw new Usage($"{verb} takes no -- command");
        }

        var cwd = Path.GetFullPath(p.Get("cwd") ?? Directory.GetCurrentDirectory());
        var count = Count(p, MaxEntries);
        var modes = EntryModes.Where(m => p.Get(m) == "true").ToList();
        if (modes.Count > 1)
        {
            throw new Usage("give at most one of --pending, --acked, --all");
        }

        Result result;
        ServiceGraph g;
        switch (verb)
        {
            case "contract":
                g = services.Value;
                result = ContractAsync(g, client ?? g.Daemon).GetAwaiter().GetResult();
                break;
            default:
                if (sub == "entries" && p.Get("address") is null)
                {
                    throw new Usage("inspect entries needs --address <name@team>");
                }

                if (sub == "status" && (p.Get("dir") is not { } dir || !dir.StartsWith('/')))
                {
                    throw new Usage("inspect status needs --dir <absolute hierarchy dir>");
                }

                g = services.Value;
                result = InspectAsync(g, client ?? g.Daemon, sub!, p, cwd, count, modes.FirstOrDefault() ?? "pending").GetAwaiter().GetResult();
                break;
        }

        Emit(result, plain, verb, g, stdout, stderr);
        return result.Code;
    }

    private static int? Count(Parsed p, int max)
    {
        if (p.Get("n") is not { } text)
        {
            return null;
        }

        if (text.Length == 0 || !text.All(static c => c is >= '0' and <= '9')
            || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1 || n > max)
        {
            throw new Usage($"-n must be a whole number from 1 to {max.ToString(CultureInfo.InvariantCulture)}");
        }

        return (int)n;
    }

    // ---------------------------------------------------------------- asking the daemon

    private sealed record Answer(JsonObject? Body, JsonObject? Error);

    /// <summary>
    /// One GET to a live daemon. A body for a 200; an error for an answer that is the final word (a 4xx naming a code, except a
    /// context field complaint and a path the daemon does not serve); null to use the files. Never starts a daemon.
    /// </summary>
    private static async Task<Answer?> AskAsync(IDaemonClient? client, string path, IReadOnlyDictionary<string, string?>? query)
    {
        if (client is null)
        {
            return null;
        }

        var target = path;
        if (query is not null)
        {
            var pairs = query.Where(q => q.Value is not null).Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value!)}").ToList();
            if (pairs.Count > 0)
            {
                target += "?" + string.Join('&', pairs);
            }
        }

        var reply = await client.RequestAsync(new DaemonRequest("GET", target, Timeout: TimeSpan.FromSeconds(10), StartIfDown: false, LaunchIfDown: false)).ConfigureAwait(false);
        if (reply.Status == 0)
        {
            return null;
        }

        JsonObject? body = null;
        try
        {
            body = JsonText.Parse(reply.Body.ToArray()) is { IsMap: true } v ? v.AsObject() : null;
        }
        catch (JsonParseException)
        {
            // not a JSON answer: the files answer instead
        }

        if (reply.Status == 200 && body is not null)
        {
            return new Answer(body, null);
        }

        var error = body?.Get("error") is { IsMap: true } e ? e.AsObject() : null;
        if (reply.Status is >= 400 and < 500 && error is not null && error.Get("code") is { IsText: true } code
            && error.Get("field") is null && code.AsString() != "not_found")
        {
            return new Answer(null, error);
        }

        return null;
    }

    private static async Task<Result> ViaGatherAsync(IDaemonClient? client, string path, IReadOnlyDictionary<string, string?>? query, Func<JsonObject> files)
    {
        var answer = await AskAsync(client, path, query).ConfigureAwait(false);
        if (answer?.Error is { } error)
        {
            return Fail(1, JsConvert.ToJsString(error.Get("code")), JsConvert.ToJsString(error.Get("message")));
        }

        return answer?.Body is { } body ? Ok(body, "daemon") : Ok(files(), "files");
    }

    // ---------------------------------------------------------------- the verbs

    private static async Task<Result> ContractAsync(ServiceGraph g, IDaemonClient? client)
    {
        var answer = await AskAsync(client, "/v1/contract", null).ConfigureAwait(false);
        if (answer?.Body is { } body)
        {
            return Ok(body, "daemon");
        }

        return Ok(MeshModule.CreateContract(g.Io.Files, g.Environment).Document(), "files");
    }

    private static async Task<Result> InspectAsync(ServiceGraph g, IDaemonClient? client, string sub, Parsed p, string cwd, int? n, string filter)
    {
        var inspect = MeshModule.CreateMailboxInspect(g.Mailbox);
        switch (sub)
        {
            case "boxes":
            {
                var address = p.Get("address");
                return await ViaGatherAsync(client, "/v1/inspect/boxes", new Dictionary<string, string?> { ["address"] = address }, () => InspectFiles(g, "/v1/inspect/boxes", address, null)!).ConfigureAwait(false);
            }

            case "registrations":
                return await ViaGatherAsync(client, "/v1/inspect/registrations", null, () => InspectFiles(g, "/v1/inspect/registrations", null, null)!).ConfigureAwait(false);
            case "events":
            {
                var address = p.Get("address");
                return await ViaGatherAsync(client, "/v1/inspect/events", new Dictionary<string, string?> { ["address"] = address }, () => InspectFiles(g, "/v1/inspect/events", address, null)!).ConfigureAwait(false);
            }

            case "timers":
            {
                var session = p.Get("session");
                return await ViaGatherAsync(
                    client,
                    "/v1/inspect/timers",
                    new Dictionary<string, string?> { ["session"] = session },
                    () => InspectFiles(g, "/v1/inspect/timers", null, session)!).ConfigureAwait(false);
            }

            case "entries":
                return await EntriesAsync(g, client, inspect, p, cwd, n ?? MaxEntries, filter).ConfigureAwait(false);
            default:
                return await StatusDocAsync(g, client, p, cwd).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The files answer of an inspect path: every box, registration, event or timer of the state home, narrowed by
    /// <paramref name="address"/> (boxes, events) or <paramref name="session"/> (timers); null for a path that is not an inspect list.
    /// </summary>
    internal static JsonObject? InspectFiles(ServiceGraph g, string path, string? address, string? session)
    {
        var inspect = MeshModule.CreateMailboxInspect(g.Mailbox);
        var now = (double)g.Io.Clock.UtcNow.ToUnixTimeMilliseconds();
        return path switch
        {
            "/v1/inspect/boxes" => inspect.Boxes(address, now),
            "/v1/inspect/registrations" => inspect.Registrations(now),
            "/v1/inspect/events" => inspect.Events(address),
            "/v1/inspect/timers" => Host(g).InspectTimers(session, now, WatchModule.PollMs(g.Environment)),
            _ => null,
        };
    }

    private static WatchHostAdapter Host(ServiceGraph g) =>
        new(g, RosterModule.CreateScreenRules(), g.Io.Clock, WatchHostSeams.Unavailable, static _ => { });

    private static async Task<Result> EntriesAsync(ServiceGraph g, IDaemonClient? client, IMailboxInspect inspect, Parsed p, string cwd, int n, string filter)
    {
        var address = p.Get("address") ?? throw new Usage("inspect entries needs --address <name@team>");
        var hierarchy = g.Config.HierarchyDir(cwd);
        var main = g.Config.MainHierarchyDir(cwd);
        var socket = g.Environment.GetVariable("CLAUDE_CODE_MESSAGING_SOCKET");
        var context = new RequestContext(cwd, hierarchy, main ?? string.Empty, string.IsNullOrEmpty(socket) ? string.Empty : Path.GetDirectoryName(socket) ?? string.Empty);
        var query = new Dictionary<string, string?>
        {
            ["address"] = address,
            ["cwd"] = cwd,
            ["hierarchy_dir"] = hierarchy,
            ["main_hierarchy_dir"] = main,
            ["messaging_socket_dir"] = context.SocketDirectory.Length > 0 ? context.SocketDirectory : null,
            ["filter"] = filter,
            ["n"] = n.ToString(CultureInfo.InvariantCulture),
        };
        var answer = await AskAsync(client, "/v1/inspect/entries", query).ConfigureAwait(false);
        if (answer?.Error is { } error)
        {
            return Fail(1, JsConvert.ToJsString(error.Get("code")), JsConvert.ToJsString(error.Get("message")));
        }

        if (answer?.Body is { } body)
        {
            return Ok(body, "daemon");
        }

        var resolved = g.Mailbox.ResolveAddress(address, context);
        return resolved.Failure is { } failure
            ? Fail(1, failure.Code, failure.Message)
            : Ok(inspect.Entries(resolved.Value!.Dir, address, filter, n), "files");
    }

    /// <summary>
    /// The live status document of hierarchy dir <c>--dir</c>: the daemon's, with its <c>rev</c>, else the in-process
    /// <c>status.read</c> document with <c>rev: null</c>. The local answer only for the hierarchy dir (or the main one) that <c>--cwd</c> resolves to, as the
    /// daemon serves only dirs it knows, so a daemon that is up and one that is down give the same answer for any other dir.
    /// </summary>
    private static async Task<Result> StatusDocAsync(ServiceGraph g, IDaemonClient? client, Parsed p, string cwd)
    {
        var dir = p.Get("dir");
        if (dir is null || !dir.StartsWith('/'))
        {
            throw new Usage("inspect status needs --dir <absolute hierarchy dir>");
        }

        var answer = await AskAsync(client, "/v1/status", new Dictionary<string, string?> { ["dir"] = dir }).ConfigureAwait(false);
        if (answer?.Error is { } error)
        {
            return Fail(1, JsConvert.ToJsString(error.Get("code")), JsConvert.ToJsString(error.Get("message")));
        }

        if (answer?.Body is { } body)
        {
            return Ok(
                new JsonObject
                {
                    ["dir"] = dir,
                    ["rev"] = body.Get("rev") is { } rev ? rev : JsonValue.Null,
                    ["status"] = body.Get("doc") is { } doc ? doc : JsonValue.Null,
                },
                "daemon");
        }

        var known = new[] { g.Config.HierarchyDir(cwd), g.Config.MainHierarchyDir(cwd) }
            .Where(static d => !string.IsNullOrEmpty(d))
            .Select(static d => Path.GetFullPath(d!));
        if (!known.Contains(Path.GetFullPath(dir), StringComparer.Ordinal))
        {
            return Fail(1, "not_found", $"no status document is served for {dir}");
        }

        // files-off-disk F3a: no daemon, so the same status.read document it serves, computed in process
        try
        {
            var doc = new StateServiceReader(g.InternalStateRead).Read("status", "read", dir, new JsonObject { ["dir"] = dir });
            return Ok(new JsonObject { ["dir"] = dir, ["rev"] = JsonValue.Null, ["status"] = doc }, "files");
        }
        catch (InvalidOperationException e)
        {
            return Fail(1, "not_found", $"no status document for {dir}: {e.Message}");
        }
    }

    // ---------------------------------------------------------------- output

    private static void Emit(Result result, bool plain, string verb, ServiceGraph g, TextWriter stdout, TextWriter stderr)
    {
        var body = result.Body;
        if (plain && body.Get("ok") is { Kind: JsonKind.Bool } ok && !ok.AsBool() && body.Get("results") is null)
        {
            var error = body["error"].AsObject();
            stderr.Write($"error: {S(error.Get("code"))}: {S(error.Get("message"))}\n");
            return;
        }

        stdout.Write((plain ? Render(body, verb, g) : JsonText.Stringify(body, 0)) + "\n");
    }

    /// <summary>
    /// A field's text as the plain views print it: as it is, unless it carries a control, escape or bidirectional character, then
    /// quoted and bounded by <see cref="SafeText"/>, so mailbox text a peer wrote cannot drive the terminal or forge a line.
    /// </summary>
    private static string S(JsonValue? v)
    {
        var text = JsConvert.ToJsString(v);
        return text.Any(static c => char.IsControl(c) || c is (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) ? SafeText.Render(text) : text;
    }

    private static string OrDash(JsonValue? v, string fallback) => v is { IsNull: false } x ? S(x) : fallback;

    private static bool Truthy(JsonValue? v) => v is { IsTruthy: true };

    /// <summary>The age of an ISO timestamp as the plain views print it, or <c>?</c> when it does not parse.</summary>
    private static string Ago(JsonValue? iso, ServiceGraph g)
    {
        if (iso is not { IsText: true } t || !Timestamps.TryParseMs(t.AsString(), out var at))
        {
            return "?";
        }

        var s = Math.Max(0, Math.Floor(((g.Io.Clock.UtcNow.ToUnixTimeMilliseconds() - at) / 1000) + 0.5));
        string R(double v) => JsConvert.ToJsString(Math.Floor(v + 0.5));
        return s < 90 ? $"{JsConvert.ToJsString(s)}s" : s < 5400 ? $"{R(s / 60)}m" : s < 129600 ? $"{R(s / 3600)}h" : $"{R(s / 86400)}d";
    }

    private static string Render(JsonObject b, string verb, ServiceGraph g)
    {
        var o = new List<string>();
        if (b.Get("source") is { IsText: true } source && source.AsString() == "files" && verb == "inspect")
        {
            o.Add("daemon down, showing files");
        }

        if (b.Get("boxes") is { IsArray: true } boxes)
        {
            foreach (var item in boxes.AsArray())
            {
                var x = item.AsObject();
                var hb = Truthy(x.Get("heartbeat_at")) ? $"  heartbeat {Ago(x.Get("heartbeat_at"), g)} ago" : string.Empty;
                o.Add($"{S(x.Get("address"))}  entries {S(x.Get("entries"))}  unacked {S(x.Get("unacked"))}  overdue {S(x.Get("overdue"))}  adapter {OrDash(x.Get("adapter"), "none")} ({S(x.Get("adapter_state"))}){hb}  {S(x.Get("dir"))}");
            }
        }
        else if (b.Get("entries") is { IsArray: true } entries)
        {
            foreach (var item in entries.AsArray())
            {
                var e = item.AsObject();
                var attempt = e.Get("attempt") is { IsMap: true } a ? S(a.AsObject().Get("state")) : "-";
                var tail = Truthy(e.Get("ref")) ? S(e.Get("ref")) : Truthy(e.Get("event")) ? S(e.Get("event")) : string.Empty;
                var line = $"{S(e.Get("id"))}  {S(e.Get("kind"))}  from {S(e.Get("from"))}  {S(e.Get("created"))}  route {OrDash(e.Get("route"), "-")}  attempt {attempt}  {(Truthy(e.Get("acked")) ? "acked" : "pending")}  {tail}";
                o.Add(line.TrimEnd());
            }

            if (b.Get("total") is { IsNumber: true } total && total.AsNumber() > entries.AsArray().Count)
            {
                o.Add($"(showing the last {entries.AsArray().Count.ToString(CultureInfo.InvariantCulture)} of {S(total)})");
            }
        }
        else if (b.Get("registrations") is { IsArray: true } regs)
        {
            foreach (var item in regs.AsArray())
            {
                var r = item.AsObject();
                o.Add($"{S(r.Get("session_id"))}  {S(r.Get("address"))}  {(Truthy(r.Get("harness")) ? S(r.Get("harness")) : "?")}  pid {OrDash(r.Get("pid"), "-")}  {(Truthy(r.Get("live")) ? "live" : "stale")}  adapter {S(r.Get("adapter"))}");
            }
        }
        else if (b.Get("timers") is { IsArray: true } timers)
        {
            foreach (var item in timers.AsArray())
            {
                var t = item.AsObject();
                var addresses = t.Get("addresses") is { IsArray: true } al ? string.Join(',', al.AsArray().Select(static x => S(x))) : string.Empty;
                var due = t.Get("due") is { IsArray: true } dl ? dl.AsArray() : new JsonArray();
                o.Add($"{S(t.Get("session"))}  {addresses}  {(Truthy(t.Get("owned")) ? "owned" : "not owned")}  due {due.Count.ToString(CultureInfo.InvariantCulture)}");
                if (t.Get("dispatches") is { IsArray: true } dispatches)
                {
                    foreach (var dx in dispatches.AsArray())
                    {
                        var d = dx.AsObject();
                        var checkIn = Truthy(d.Get("check_in_at")) ? $"  check-in {S(d.Get("check_in_at"))}" : string.Empty;
                        var silent = Truthy(d.Get("silent_at")) ? $"  silent {S(d.Get("silent_at"))}" : string.Empty;
                        var member = Truthy(d.Get("member")) ? S(d.Get("member")) : string.Empty;
                        o.Add($"  {S(d.Get("request_id"))}  {S(d.Get("to"))} {member}  eta {S(d.Get("eta"))}  {OrDash(d.Get("state"), "-")}{checkIn}{silent}".TrimEnd());
                    }
                }

                foreach (var dx in due)
                {
                    var d = dx.AsObject();
                    var member = Truthy(d.Get("member")) ? $" ({S(d.Get("member"))})" : string.Empty;
                    o.Add($"  due: {S(d.Get("event"))} for {S(d.Get("request_id"))}{member}");
                }
            }
        }
        else if (b.Get("events") is { IsArray: true } events)
        {
            foreach (var item in events.AsArray())
            {
                var e = item.AsObject();
                var request = Truthy(e.Get("request_id")) ? S(e.Get("request_id")) : string.Empty;
                o.Add($"{S(e.Get("id"))}  {S(e.Get("address"))}  {S(e.Get("event"))}  {Ago(e.Get("created"), g)} old  {request}".TrimEnd());
            }
        }
        else if (b.ContainsKey("rev"))
        {
            o.Add($"rev {S(b.Get("rev"))}");
            o.Add(JsonText.Stringify(b.Get("status") ?? JsonValue.Null, 0));
        }
        else if (b.ContainsKey("contract_version"))
        {
            o.Add(MeshModule.CreateContract(g.Io.Files, g.Environment).RenderGuide());
        }
        else if (b.Get("results") is { IsArray: true } results)
        {
            foreach (var item in results.AsArray())
            {
                var r = item.AsObject();
                var detail = Truthy(r.Get("detail")) ? "  " + S(r.Get("detail")) : string.Empty;
                o.Add($"{S(r.Get("status")).ToUpperInvariant().PadRight(4)}  {S(r.Get("id"))}{detail}");
            }

            var failedIds = b.Get("failed") is { IsArray: true } failed ? string.Join(", ", failed.AsArray().Select(x => S(x))) : string.Empty;
            o.Add(Truthy(b.Get("ok"))
                ? $"ok: {S(b.Get("harness"))} conforms"
                : $"not conforming: {(failedIds.Length > 0 ? failedIds : "a must did not pass")}");
        }

        return string.Join('\n', o);
    }
}
