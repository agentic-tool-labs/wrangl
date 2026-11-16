using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Codex;

/// <summary>One clause of the gate; tests mutate one at a time by passing a list without it.</summary>
internal sealed record GateClause(string Name, Func<JsonValue, CodexModes, bool> Holds);

internal sealed record GateResult(bool Ok, IReadOnlyList<string> Failed);

/// <summary>The outcome of the spawn's steps 3 to 6: a thread, or the step that refused with a note that carries no server text.</summary>
internal sealed record BringUpResult(bool Ok, string? ThreadId, string? Step, string? Note);

/// <summary>The protocol-level rules that sit on the client: the gate on a thread report, error labels, and the warm-up.</summary>
internal static partial class CodexProtocol
{
    /// <summary>How long the spawn waits for the warm-up turn to complete.</summary>
    public const int WarmupTimeoutMs = 90 * 1000;

    /// <summary>The fixed text of the warm-up turn: harmless, and no tool use is expected.</summary>
    public const string WarmupText = "Reply with the single word READY and nothing else. Do not run any tools.";

    /// <summary>How long a hand-over may wait for the server to report its user message before it is given up on.</summary>
    public const int AckWaitMs = 60 * 1000;

    public static readonly IReadOnlyList<string> WarmupClasses = ["not-logged-in", "rate-limit", "network", "timeout", "error"];

    public static readonly IReadOnlyList<GateClause> GateClauses =
    [
        new("sandbox", (report, modes) => SandboxTypeOf(modes.Sandbox) is { } wanted && report.Member("sandbox") is { IsTruthy: true } sb && Text(sb.Member("type")) == wanted),
        new("network", (report, _) => report.Member("sandbox") is { IsTruthy: true } sb && sb.Member("networkAccess") is { IsBool: true } net && !net.AsBool()),
        new("approval", (report, modes) => report.IsTruthy && Text(report.Member("approvalPolicy")) == modes.Approval),
        new("tmpExclusions", (report, modes) =>
            modes.Sandbox != CodexLaunch.DefaultSandbox
            || (report.Member("sandbox") is { IsTruthy: true } sb
                && sb.Member("excludeSlashTmp") is { IsBool: true } slash && slash.AsBool()
                && sb.Member("excludeTmpdirEnvVar") is { IsBool: true } tmpdir && tmpdir.AsBool())),
    ];

    private static string? SandboxTypeOf(string sandbox) => sandbox switch
    {
        "read-only" => "readOnly",
        "workspace-write" => "workspaceWrite",
        _ => null,
    };

    private static string? Text(JsonValue? value) => value is { IsText: true } v ? v.AsString() : null;

    /// <summary>
    /// The gate, used at spawn and on every reconnect: the effective mode a <c>thread/start</c> or <c>thread/resume</c> report
    /// gives must equal the values ah pinned. A report that is missing, partial or of another shape fails every clause it cannot show.
    /// </summary>
    public static GateResult GateReport(JsonValue? report, CodexModes modes, IReadOnlyList<GateClause>? clauses = null)
    {
        var shape = report is { } r && (r.IsMap || r.IsArray) ? r : JsonValue.Null;
        var failed = (clauses ?? GateClauses).Where(c => !c.Holds(shape, modes)).Select(c => c.Name).ToList();
        return new GateResult(failed.Count == 0, failed);
    }

    private static readonly HashSet<double> RpcCodes = [-32700, -32600, -32601, -32602, -32603];

    private static readonly HashSet<string> ErrnoCodes = ["ECONNREFUSED", "ECONNRESET", "ENOENT", "EPIPE", "ETIMEDOUT", "EACCES", "ENOTSOCK"];

    [GeneratedRegex("timed out", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TimedOut();

    [GeneratedRegex("closed|not connected", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Closed();

    [GeneratedRegex("upgrade|handshake", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Handshake();

    /// <summary>
    /// A fixed label for an error that may carry the server's own words: a JSON-RPC code or a system error code from a short
    /// fixed list, one of a few fixed words, or the constant "server-error" for any other code. Never the message and never a
    /// code the server chose.
    /// </summary>
    public static string ErrorClass(Exception? error)
    {
        if (error is CodexClientException { Code: { } code } && !code.IsNull)
        {
            if (code.IsNumber && RpcCodes.Contains(code.AsNumber()))
            {
                return $"rpc-error {(long)code.AsNumber()}";
            }

            if (code.IsText && ErrnoCodes.Contains(code.AsString()))
            {
                return code.AsString();
            }

            return "server-error";
        }

        var text = error?.Message ?? "null";
        if (TimedOut().IsMatch(text))
        {
            return "timeout";
        }

        if (Closed().IsMatch(text))
        {
            return "closed";
        }

        return Handshake().IsMatch(text) ? "handshake" : "error";
    }

    [GeneratedRegex("timed out|timeout", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarmTimeout();

    [GeneratedRegex("log.?in|logged|auth|unauthor|credential|sign.?in|401|403", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarmAuth();

    [GeneratedRegex("rate|limit|quota|429|usage", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarmRate();

    [GeneratedRegex("network|econn|enotfound|etimedout|fetch|offline|connect", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WarmNetwork();

    private static string CodeText(Exception error)
    {
        if (error is not CodexClientException { Code: { } code } || code.IsNull)
        {
            return string.Empty;
        }

        return code.IsText ? code.AsString() : code.IsNumber ? JsonText.Stringify(code, 0) : string.Empty;
    }

    private static string WarmupClass(Exception error)
    {
        var text = $"{CodeText(error)} {error.Message}";
        if (WarmTimeout().IsMatch(text))
        {
            return "timeout";
        }

        if (WarmAuth().IsMatch(text))
        {
            return "not-logged-in";
        }

        if (WarmRate().IsMatch(text))
        {
            return "rate-limit";
        }

        return WarmNetwork().IsMatch(text) ? "network" : "error";
    }

    /// <summary>The note a failed bring-up step carries: one fixed phrase per step, no value from the server.</summary>
    public static class BringUpNotes
    {
        public const string Start = "thread/start failed";
        public const string Pins = "the app-server did not apply ah's pins";
        public const string Rollout = "no rollout after warm-up";
    }

    public static string WarmupNote(string cls) => $"warm-up turn failed ({(WarmupClasses.Contains(cls) ? cls : "error")})";

    private static JsonObject InputOf(string text)
    {
        var part = new JsonObject { ["type"] = "text", ["text"] = text };
        var input = new JsonArray();
        input.Add(part);
        return new JsonObject { ["input"] = input };
    }

    /// <summary>The <c>input</c> array of a turn: one text part.</summary>
    public static JsonArray TextInput(string text) => InputOf(text)["input"].AsArray();

    /// <summary>
    /// The spawn's steps 3-6 against an open <paramref name="client"/>: <c>thread/start{cwd}</c> (cwd only), the gate on its
    /// report, one warm-up turn (the fixed text, completion awaited), <c>thread/resume</c> (it must succeed, which shows a rollout
    /// exists), and the gate on that report. Reads only the gate fields and the turn status from the server, and never the reply
    /// of the warm-up turn.
    /// </summary>
    public static async Task<BringUpResult> BringUpThreadAsync(
        IAppServerClient client,
        string cwd,
        CodexModes modes,
        int warmupMs = WarmupTimeoutMs,
        string text = WarmupText)
    {
        static BringUpResult Refuse(string step, string note) => new(false, null, step, note);

        JsonValue started;
        try
        {
            started = await client.RequestAsync("thread/start", new JsonObject { ["cwd"] = cwd }).ConfigureAwait(false);
        }
        catch (CodexClientException)
        {
            return Refuse("start", BringUpNotes.Start);
        }

        var threadId = started.Member("thread") is { IsMap: true } thread && Text(thread.Member("id")) is { } id ? id : null;
        if (threadId is null)
        {
            return Refuse("start", BringUpNotes.Start);
        }

        if (!GateReport(started, modes).Ok)
        {
            return Refuse("gate", BringUpNotes.Pins);
        }

        var done = new TaskCompletionSource<(bool Ok, string Class)>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.OnNotification(n =>
        {
            if (n.Method != "turn/completed")
            {
                return;
            }

            var p = n.Params;
            var turn = p.Member("turn");
            var ev = p.Member("threadId") is { IsTruthy: true } direct ? direct : turn?.Member("threadId");
            if (Text(ev) != threadId)
            {
                return;
            }

            var status = turn?.Member("status") is { IsTruthy: true } s ? StatusText(s) : string.Empty;
            done.TrySetResult(status == "completed" ? (true, string.Empty) : (false, "error"));
        });
        client.OnClose(() => done.TrySetResult((false, "network")));

        using var timeout = new CancellationTokenSource();
        var timer = Task.Delay(warmupMs, timeout.Token).ContinueWith(_ => (Ok: false, Class: "timeout"), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        try
        {
            _ = await client.RequestAsync("turn/start", new JsonObject { ["threadId"] = threadId, ["input"] = TextInput(text) }).ConfigureAwait(false);
        }
        catch (CodexClientException e)
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            return Refuse("warmup", WarmupNote(WarmupClass(e)));
        }

        var winner = await Task.WhenAny(done.Task, timer).ConfigureAwait(false);
        await timeout.CancelAsync().ConfigureAwait(false);
        var outcome = await winner.ConfigureAwait(false);
        if (!outcome.Ok)
        {
            return Refuse("warmup", WarmupNote(outcome.Class));
        }

        JsonValue resumed;
        try
        {
            resumed = await client.RequestAsync("thread/resume", new JsonObject { ["threadId"] = threadId }).ConfigureAwait(false);
        }
        catch (CodexClientException)
        {
            return Refuse("rollout", BringUpNotes.Rollout);
        }

        return GateReport(resumed, modes).Ok ? new BringUpResult(true, threadId, null, null) : Refuse("gate", BringUpNotes.Pins);
    }

    /// <summary><c>String(value)</c> for the status field: text as is, a number or boolean as JavaScript prints it.</summary>
    private static string StatusText(JsonValue value) => value.IsText ? value.AsString() : JsonText.Stringify(value, 0);
}
