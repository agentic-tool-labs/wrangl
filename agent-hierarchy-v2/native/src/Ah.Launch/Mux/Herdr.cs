using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Launch;

/// <summary>The outcome of a herdr call that may fail: <paramref name="Ok"/> is a zero exit.</summary>
public sealed record HerdrRaw(bool Ok, string Stdout, string Stderr, int? Status);

/// <summary>
/// Liveness and readiness of one named agent. <paramref name="Indeterminate"/> means herdr could not answer, which is never the same as
/// dead: a caller that reads it as dead starts a second agent under a name herdr requires to be unique.
/// </summary>
public sealed record HerdrAgentState(bool Live, bool Indeterminate, string Why, string? AgentStatus, bool Ready, string? AgentType = null)
{
    /// <summary>
    /// Herdr answered that its server is not running (<c>server_not_running</c>): still <see cref="Indeterminate"/> for the agent, but a definite
    /// fact about the herdr instance, which a caller may hold as such (ah2-52 item 3).
    /// </summary>
    public bool ServerDown { get; init; }
}

/// <summary>
/// The terminal a spawn nonce names: <paramref name="Address"/> when exactly one live terminal carries it; <paramref name="Error"/> when
/// the listing could not be read or several carry it (nothing may be closed then); both null when none does (nothing to close).
/// </summary>
public sealed record SpawnLookup(string? Address, string? Error);

/// <summary>The one herdr call and the one reading of <c>herdr agent list</c>.</summary>
public interface IHerdr
{
    /// <summary>
    /// The herdr flavour this instance talks to: <see cref="Ah.Core.Mux.HerdrFlavour.Tui"/> runs <c>herdr</c>,
    /// <see cref="Ah.Core.Mux.HerdrFlavour.Core"/> translates each call to <c>hc</c> / <c>herdr-app</c> (<see cref="HerdrCore"/>).
    /// </summary>
    string Flavour { get; }

    /// <summary>
    /// The raw call: <c>herdr &lt;args&gt;</c> as <see cref="IMuxRunner.Run"/> runs it, with <paramref name="environment"/> the per-instance
    /// env and <paramref name="outputCapBytes"/> the output cap. Under tui it is exactly that call; under core it goes through the translator.
    /// </summary>
    MuxResult Run(IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, IReadOnlyDictionary<string, string?>? environment = null);

    /// <summary>Under core, whether <c>herdr-app status</c> succeeds (the app is up); always true under tui.</summary>
    bool AppPresent();

    /// <summary>Under core, the terminal carrying spawn nonce <paramref name="nonce"/> (see <see cref="SpawnLookup"/>); none under tui.</summary>
    SpawnLookup SpawnAddress(string nonce);

    /// <summary>
    /// Runs <c>herdr &lt;args&gt;</c> and parses its JSON stdout. <paramref name="timeoutMs"/> bounds one call; null takes
    /// <c>AH_HERDR_TIMEOUT_MS</c>, else 10000. Throws <see cref="InvalidOperationException"/> whose message names the failure.
    /// </summary>
    JsonValue Invoke(IReadOnlyList<string> args, double? timeoutMs = null);

    /// <summary>
    /// <see cref="Invoke"/> that returns a non-zero exit instead of throwing (<c>herdr agent get</c> on a missing name exits 1 and writes
    /// its error as JSON on stdout). A call the timeout ended still throws.
    /// </summary>
    HerdrRaw InvokeAllowFailure(IReadOnlyList<string> args, double? timeoutMs = null);

    /// <summary><c>herdr agent list</c>, read by <see cref="AgentsFromList"/>.</summary>
    JsonArray Agents(double? timeoutMs = null);

    /// <summary>
    /// The agents of a parsed <c>herdr agent list</c> answer: <c>{name, title, session_id, pane_id, tab_id, workspace_id, cwd,
    /// agent_status}</c>. Throws when the answer has no <c>.result.agents</c> array.
    /// </summary>
    JsonArray AgentsFromList(JsonValue result);

    /// <summary>True when an executable named <c>herdr</c> is on PATH. Reads the file system only; nothing is cached.</summary>
    bool OnPath();

    /// <summary>The first executable named <paramref name="binary"/> on PATH, or null. Reads the file system only; nothing is cached.</summary>
    string? FindOnPath(string binary);

    /// <summary>Liveness and readiness of <paramref name="name"/> from <c>herdr agent get</c>; three-valued by design.</summary>
    HerdrAgentState AgentState(string name);
}

/// <summary>
/// The herdr adapter over <paramref name="Mux"/>. <paramref name="FlavourOverride"/> null takes the flavour from the env
/// (<see cref="Ah.Core.Mux.HerdrFlavour.FromEnv"/>, tui when neither is set); ahd passes the flavour a team stores.
/// <paramref name="Translator"/> null builds the core translator over <paramref name="Mux"/>.
/// </summary>
public sealed record Herdr(IMuxRunner Mux, IAhEnvironment Environment, IFileReader Files, string? FlavourOverride = null, HerdrCore? Translator = null) : IHerdr
{
    private static readonly string[] AgentListArgv = ["agent", "list"];
    private static readonly string[] LiveStates = ["idle", "working", "blocked", "done", "unknown"];

    public string Flavour => FlavourOverride
        ?? Ah.Core.Mux.HerdrFlavour.FromEnv(Environment.GetVariable("HERDR_ENV"), Environment.GetVariable("HC_TERMINAL_ID"))
        ?? Ah.Core.Mux.HerdrFlavour.Tui;

    private bool IsCore => Flavour == Ah.Core.Mux.HerdrFlavour.Core;

    private HerdrCore Core => Translator ?? new HerdrCore(Mux, Environment);

    /// <summary>This adapter for <paramref name="flavour"/>: ahd's per-team instance.</summary>
    public Herdr ForFlavour(string flavour) => this with { FlavourOverride = flavour };

    public MuxResult Run(IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, IReadOnlyDictionary<string, string?>? environment = null) =>
        IsCore ? Core.Run(args, timeoutMs, outputCapBytes, environment) : Mux.Run("herdr", args, timeoutMs, outputCapBytes, null, environment);

    public bool AppPresent() => !IsCore || Core.AppPresent(EnvTimeout());

    public SpawnLookup SpawnAddress(string nonce) => IsCore ? Core.SpawnAddress(nonce, EnvTimeout()) : new SpawnLookup(null, null);

    public JsonValue Invoke(IReadOnlyList<string> args, double? timeoutMs = null)
    {
        var raw = Execute(args, timeoutMs, allowFailure: false);
        return Parse(raw.Stdout, args);
    }

    public HerdrRaw InvokeAllowFailure(IReadOnlyList<string> args, double? timeoutMs = null) => Execute(args, timeoutMs, allowFailure: true);

    public JsonArray Agents(double? timeoutMs = null) => AgentsFromList(Invoke(AgentListArgv, timeoutMs));

    public JsonArray AgentsFromList(JsonValue result)
    {
        if (result.Member("result")?.Member("agents") is not { IsArray: true } agents)
        {
            throw new InvalidOperationException("herdr agent list produced unexpected shape (missing .result.agents)");
        }

        var rows = new JsonArray();
        foreach (var a in agents.AsArray())
        {
            if (a.IsNull)
            {
                throw new InvalidOperationException("Cannot read properties of null (reading 'name')");
            }

            var row = new JsonObject
            {
                ["name"] = a.Member("name") is { IsTruthy: true } name ? name : JsonValue.Null,
                ["title"] = TitleOf(a),
                ["session_id"] = SessionIdOf(a),
            };
            AddIfPresent(row, "pane_id", a.Member("pane_id"));
            AddIfPresent(row, "tab_id", a.Member("tab_id"));
            AddIfPresent(row, "workspace_id", a.Member("workspace_id"));
            row["cwd"] = a.Member("cwd") is { IsTruthy: true } cwd ? cwd : a.Member("foreground_cwd") is { IsTruthy: true } fg ? fg : JsonValue.Null;
            row["agent_status"] = a.Member("agent_status") is { IsTruthy: true } status ? status : JsonValue.Null;
            rows.Add(row);
        }

        return rows;
    }

    public bool OnPath() => FindOnPath(IsCore ? "hc" : "herdr") is not null;

    public string? FindOnPath(string binary)
    {
        var path = Environment.GetVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var entry in path.Split(':'))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            try
            {
                var candidate = NodePath.Join(entry, binary);
                if (Files.IsExecutable(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // not here, or not executable: try the next PATH entry
            }
        }

        return null;
    }

    public HerdrAgentState AgentState(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!OnPath())
        {
            return new HerdrAgentState(false, true, "no `herdr` binary on PATH — cannot ask whether this agent is live", null, false);
        }

        HerdrRaw res;
        try
        {
            res = InvokeAllowFailure(["agent", "get", name]);
        }
        catch (InvalidOperationException err)
        {
            return new HerdrAgentState(false, true, $"herdr agent get {name} failed: {err.Message}", null, false);
        }

        JsonValue? parsed = null;
        foreach (var stream in new[] { res.Stdout, res.Stderr })
        {
            if (JsText.Trim(stream).Length == 0)
            {
                continue;
            }

            if (JsonText.TryParse(Utf8Text.Encode(stream), out var value))
            {
                parsed = value;
                break;
            }
        }

        if (parsed is not { IsTruthy: true } doc)
        {
            return new HerdrAgentState(false, true, $"herdr agent get {name} produced unparseable output", null, false);
        }

        var code = doc.Member("error") is { IsTruthy: true } error ? error.Member("code") : doc.Member("error");
        if (code is { IsText: true } text && text.AsString() == "agent_not_found")
        {
            return new HerdrAgentState(false, false, "agent_not_found", null, false);
        }

        if (code is { IsTruthy: true } truthy)
        {
            return new HerdrAgentState(false, true, $"herdr agent get {name} returned error code {JsonText.Stringify(truthy, 0)}", null, false)
            {
                ServerDown = truthy is { IsText: true } named && named.AsString() == "server_not_running",
            };
        }

        var agent = doc.Member("result") is { IsTruthy: true } result ? result.Member("agent") : null;
        if (agent is not { IsTruthy: true } a || !(a.IsMap || a.IsArray))
        {
            return new HerdrAgentState(false, true, $"herdr agent get {name} returned no .result.agent", null, false);
        }

        if (a.Member("agent_status") is not { IsText: true } statusValue)
        {
            return new HerdrAgentState(false, true, $"herdr agent get {name} returned no agent_status", null, false);
        }

        var status = statusValue.AsString();
        if (!LiveStates.Contains(status))
        {
            return new HerdrAgentState(false, true, $"herdr agent get {name} returned unrecognized agent_status {JsonText.Stringify(statusValue, 0)}", status, false);
        }

        var ready = a.Member("interactive_ready") is { IsNull: false } ir
            ? ir.IsBool && ir.AsBool()
            : status is "idle" or "done";
        var type = a.Member("agent") is { IsText: true } agentType && agentType.AsString().Length > 0 ? agentType.AsString() : null;
        return new HerdrAgentState(true, false, $"agent_status {status}", status, ready, type);
    }

    private static void AddIfPresent(JsonObject row, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            row[key] = v;
        }
    }

    private static JsonValue TitleOf(JsonValue a)
    {
        if (a.Member("terminal_title_stripped") is { IsTruthy: true } title)
        {
            var s = JsConvert.ToJsString(title);
            return s.Length > 0 ? s : JsonValue.Null;
        }

        return JsonValue.Null;
    }

    private static JsonValue SessionIdOf(JsonValue a)
    {
        if (a.Member("agent_session") is { IsTruthy: true } session && session.Member("value") is { IsText: true } value && value.AsString().Length > 0)
        {
            return value;
        }

        return JsonValue.Null;
    }

    private static JsonValue Parse(string stdout, IReadOnlyList<string> args)
    {
        if (JsonText.TryParse(Utf8Text.Encode(stdout), out var value))
        {
            return value;
        }

        throw new InvalidOperationException($"herdr {string.Join(' ', args)} produced unparseable output");
    }

    private HerdrRaw Execute(IReadOnlyList<string> args, double? timeoutMs, bool allowFailure)
    {
        var timeout = timeoutMs is > 0 ? timeoutMs.Value : EnvTimeout();
        if (!(double.IsFinite(timeout) && Math.Floor(timeout) == timeout && timeout >= 0))
        {
            if (allowFailure)
            {
                return new HerdrRaw(false, string.Empty, string.Empty, null);
            }

            throw new InvalidOperationException($"herdr {string.Join(' ', args)} failed: The value of \"timeout\" is out of range. It must be an integer >= 0 && <= 9007199254740991. Received {JsConvert.NumberToString(timeout)}");
        }

        var result = Run(args, timeout);
        if (result.Ok)
        {
            return new HerdrRaw(true, result.Stdout, allowFailure ? string.Empty : result.Stderr, 0);
        }

        if (result.Signalled)
        {
            throw new InvalidOperationException($"herdr {string.Join(' ', args)} timed out after {JsConvert.NumberToString(timeout)}ms");
        }

        if (allowFailure)
        {
            return new HerdrRaw(false, result.Stdout, result.Stderr, result.Status);
        }

        var shown = result.Stderr.Length > 0 && JsText.Trim(result.Stderr).Length > 0 ? JsText.Trim(result.Stderr) : result.ErrorMessage;
        throw new InvalidOperationException($"herdr {string.Join(' ', args)} failed: {shown}");
    }

    private double EnvTimeout()
    {
        var text = Environment.GetVariable("AH_HERDR_TIMEOUT_MS");
        return string.IsNullOrEmpty(text) ? 10000 : JsConvert.ToNumber(JsonValue.FromString(text));
    }
}
