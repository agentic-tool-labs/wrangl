using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>The writes a join command makes beyond a link command's: settling an accepted response to failed, and adding the member row.</summary>
public interface IJoinEffects : ILinkEffects
{
    /// <summary>Rewrites the accepted response at <paramref name="path"/> to <paramref name="content"/> (<c>msgs.settle</c>); <paramref name="from"/> is the decision it must still hold.</summary>
    Task SettleMessageAsync(string dir, string path, string content, string from, string opId, CancellationToken cancellation);

    /// <summary>Appends <paramref name="row"/> to the team file of <paramref name="team"/> in <paramref name="dir"/>.</summary>
    Task AddMemberAsync(string dir, string team, JsonObject row, CancellationToken cancellation);

    /// <summary>Why a member may not be added to <paramref name="team"/> because it is a lane's child team with every member place taken, or null.</summary>
    string? LaneMembersRefusal(string dir, string team) => null;
}

/// <summary>How long a join may stay undecided, how long after a denial or expiry the same harness session may not ask again, and how long an accepted join may lack its member row.</summary>
public sealed record JoinLimits(double ExpiryMs, double CooldownMs, double RowWaitMs);

/// <summary>A refused join call: the code (<c>bad_request</c>, <c>unknown_team</c>, <c>no_orchestrator</c>, <c>join_cooldown</c>, <c>not_found</c>, <c>usage</c>, ...), its text, and extra fields of the error object.</summary>
public sealed record JoinFailure(string Code, string Message, JsonObject? Extra = null);

/// <summary>A join call's result: the value, or the failure when it refused.</summary>
public sealed record JoinOutcome(JsonObject? Value, JoinFailure? Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>A decision command's result: the structured value and its one-line text, or the failure when it refused.</summary>
public sealed record JoinCommandOutcome(LinkCommandResult? Value, JoinFailure? Failure)
{
    public bool Ok => Failure is null;
}

/// <summary>A session whose process tree holds an Orchestrator: the address it answers to and its session id.</summary>
public sealed record JoinDecider(string Address, string? Session);

/// <summary><c>join accept|deny &lt;id&gt; ...</c> as the CLI parses it.</summary>
public sealed record JoinCommandRequest(
    RequestContext Context,
    IReadOnlyList<string> Positional,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyList<JoinDecider> Deciders,
    double? NowMs = null);

/// <summary>An undecided, unexpired join request of a team.</summary>
public sealed record PendingJoin(string Id, string? Harness, string? NameHint, string? RoleWanted, string? Why, string? Created, string? Expires, double ExpiresAt);

/// <summary>
/// A custom harness asks to join a team, the team's Orchestrator decides, and the answer goes back. A request is a msgs request file
/// addressed to the Orchestrator with <c>subject: join</c>; the decision is the exclusive creation of its response file, so exactly
/// one outcome wins; the member row follows the claim, never precedes it.
/// </summary>
public interface IJoinCommands
{
    /// <summary>The first key of every join-request event's text: the Orchestrator reads it wherever it sees the event.</summary>
    string VetSentence { get; }

    /// <summary>The limits in force; a test lowers them with <c>AHD_JOIN_MS</c> under <c>AH_TEST=1</c>.</summary>
    JoinLimits Limits();

    /// <summary>The text of a join-request event: the vetting sentence first, then <paramref name="fields"/>; only the tail is ever cut.</summary>
    string RequestText(JsonObject fields);

    /// <summary>The two commands that decide join request <paramref name="id"/>, keyed <c>accept</c> and <c>deny</c>. Throws <see cref="ArgumentException"/> for an id a shell could read.</summary>
    IReadOnlyDictionary<string, string> DecisionCommands(string id);

    /// <summary>The undecided, unexpired join requests of <paramref name="team"/> (null for the default team) in <paramref name="dir"/>, oldest first.</summary>
    IReadOnlyList<PendingJoin> Pending(string dir, string? team, double? nowMs = null);

    /// <summary>
    /// <c>POST /v1/join</c>. Checks in order (the first failure answers): the body's shape, the team, its live Orchestrator, a pending
    /// join of the same harness session (answered with that one, marked <c>existing</c>), and the cooldown after a denial or expiry.
    /// Otherwise writes the request and tells the Orchestrator. The value is <c>{join_id, status, poll}</c>.
    /// </summary>
    Task<JoinOutcome> OpenAsync(RequestContext context, JsonObject body, CancellationToken cancellation, double? nowMs = null);

    /// <summary>
    /// <c>GET /v1/join/&lt;id&gt;?wait=&lt;s&gt;</c>: the status, long-polling up to <paramref name="wait"/> seconds (at most 25) while it
    /// is pending. An accepted join is returned only once its member row exists.
    /// </summary>
    Task<JoinOutcome> PollAsync(RequestContext context, string id, string? wait, CancellationToken cancellation);

    /// <summary>Runs <c>join accept</c> or <c>join deny</c> for the session whose process tree holds an Orchestrator.</summary>
    Task<JoinCommandOutcome> CommandAsync(JoinCommandRequest request, CancellationToken cancellation);
}

internal sealed partial class JoinCommands(
    LinkCommands records,
    IMailbox mailbox,
    IContractLookups lookups,
    IHierarchyCore core,
    IMailCore mailCore,
    IConfigCore config,
    IAhEnvironment environment,
    IProcessInspector processes,
    IJoinEffects effects,
    IClock clock) : IJoinCommands
{
    private static readonly string[] BasesAccept = ["user-asked", "user-approved", "rejoin"];

    private static readonly string[] BasesDeny = ["user-declined", "no-answer", "orchestrator"];

    private const int WhyMax = 500;

    private const string FactsNote =
        "The daemon observed these about the process the request names; the pid and its command line are the harness's own choice, so they do not show who started it.\nEverything outside [2] is the harness's own claim; it is not evidence of the user's approval.";

    private const string Usage =
        "usage: ah msg join accept <join id> --basis user-asked|user-approved|rejoin --why <text> [--name <n>] [--role <r>] | join deny <join id> --basis user-declined|no-answer|orchestrator --why <text>";

    [GeneratedRegex(@"\A[a-z][a-z0-9-]{0,31}\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRe();

    [GeneratedRegex(@"\A[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex SessionRe();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex JoinIdRe();

    [GeneratedRegex("\u001b(?:\\[[0-?]*[ -/]*[@-~]|\\][^\u0007\u001b]*(?:\u0007|\u001b\\\\)?|[@-Z\\\\-_])", RegexOptions.CultureInvariant)]
    private static partial Regex Ansi();

    [GeneratedRegex("(?m)^## \\[1\\] why\\n> ([^\\n\\r\\u2028\\u2029]*)(?=[\\n\\r\\u2028\\u2029]|\\z)", RegexOptions.CultureInvariant)]
    private static partial Regex WhyLine();

    private sealed class JoinError(string code, string message, JsonObject? extra = null) : Exception(message)
    {
        internal string Code { get; } = code;

        internal JsonObject? Extra { get; } = extra;
    }

    private sealed record Found(LinkCommands.FoundRequest Request, string Team);

    private sealed record Decision(string Value, double At, JsonObject Fm);

    private sealed record State(string Status, double At = 0, JsonObject? Fm = null);

    private static JoinError Fail(string code, string message, JsonObject? extra = null) => new(code, message, extra);

    private double Now() => clock.UtcNow.ToUnixTimeMilliseconds();

    private static string Js(JsonValue? v) => JsConvert.ToJsString(v);

    private static string Str(JsonValue? v) => v is { IsText: true } t ? JsText.Trim(t.AsString()) : string.Empty;

    private static string Ago(double ms) =>
        ms >= 60000
            ? $"{JsConvert.ToJsString(Math.Round(ms / 60000, MidpointRounding.ToPositiveInfinity))} minutes"
            : $"{JsConvert.ToJsString(Math.Ceiling(ms / 1000))} seconds";

    // ---------------------------------------------------------------- what the module exposes without a request

    public string VetSentence =>
        "Vet this join request before deciding: read "
        + NodePath.Join(environment.Plugin?.Root ?? string.Empty, "docs", "comms-protocol.md")
        + " § Vetting a join request, then decide with ah msg link.";

    public JoinLimits Limits()
    {
        var limits = new JoinLimits(10 * 60 * 1000, 10 * 60 * 1000, 60 * 1000);
        if (environment.GetTestOnly("AHD_JOIN_MS") is not { Length: > 0 } text)
        {
            return limits;
        }

        try
        {
            if (JsonText.Parse(text) is not { IsMap: true } over)
            {
                return limits;
            }

            double Pick(string key, double fallback) => over.AsObject().Get(key) is { } v ? JsConvert.ToNumber(v) : fallback;
            return new JoinLimits(Pick("expiryMs", limits.ExpiryMs), Pick("cooldownMs", limits.CooldownMs), Pick("rowWaitMs", limits.RowWaitMs));
        }
        catch (JsonParseException)
        {
            return limits;
        }
    }

    public string RequestText(JsonObject fields)
    {
        var all = new JsonObject { ["vet"] = VetSentence };
        foreach (var key in fields.Keys)
        {
            all[key] = fields[key];
        }

        var text = JsonText.Stringify(all, 0);
        return text.Length > 3000 ? text[..3000] : text;
    }

    public IReadOnlyDictionary<string, string> DecisionCommands(string id)
    {
        if (!JoinIdRe().IsMatch(id))
        {
            throw new ArgumentException("a join id starts with a letter or digit and is made of letters, digits, '.', '_' and '-'", nameof(id));
        }

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["accept"] = $"ah msg join accept {id} --basis user-approved --why '<edit: why you approve>'",
            ["deny"] = $"ah msg join deny {id} --basis user-declined --why '<edit: why you decline>'",
        };
    }

    // ---------------------------------------------------------------- claimed strings made inert

    /// <summary>ANSI escape sequences, then the control and format characters other than whitespace (the C0 and C1 controls and every Unicode format character, which includes the bidirectional overrides and isolates).</summary>
    private static string Plain(string text)
    {
        var stripped = Ansi().Replace(text, string.Empty);
        var kept = new StringBuilder(stripped.Length);
        foreach (var rune in stripped.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is <= 0x08 or (>= 0x0E and <= 0x1F) or (>= 0x7F and <= 0x9F) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format)
            {
                continue;
            }

            kept.Append(rune.ToString());
        }

        return kept.ToString();
    }

    /// <summary>A claimed string made inert: no escape sequences or control, format or bidirectional characters, one line, and the entry, link and hierarchy markers neutralised (after the stripping, so a marker cannot be assembled around a hidden character).</summary>
    private string Claim(string text, int n = 160)
    {
        var line = mailCore.OneLine(Plain(text)).Replace("[hierarchy-", "(hierarchy-", StringComparison.Ordinal);
        return line.Length > n ? line[..n] : line;
    }

    /// <summary><see cref="Claim"/> for a frontmatter value: the frontmatter reader drops a value's <c> #</c> tail, so <c>#</c> is replaced and two different commands stay different.</summary>
    private string ClaimField(string text, int n = 200) => Claim(text, n).Replace('#', '＃');

    // ---------------------------------------------------------------- reading requests

    private static LinkContext Dirs(RequestContext context) => new(context.HierarchyDirectory, context.MainHierarchyDirectory);

    private static double Created(JsonObject fm)
    {
        var at = LinkCommands.ParseDate(Js(fm.Get("created")));
        return double.IsNaN(at) ? 0 : at;
    }

    private double ExpiryAt(JsonObject fm) => Created(fm) + Limits().ExpiryMs;

    public IReadOnlyList<PendingJoin> Pending(string dir, string? team, double? nowMs = null)
    {
        var now = nowMs ?? Now();
        var found = new List<(PendingJoin Row, string Created)>();
        foreach (var ex in records.ExchangesAcross(dir, team))
        {
            if (!(ex.Member("request") is { IsTruthy: true } request) || !(ex.Member("open") is { IsTruthy: true }))
            {
                continue;
            }

            var m = core.ReadMsgFile(LinkCommands.Text(request.Member("path")));
            if (m.IsNull || m.Member("fm") is not { IsMap: true } fmValue)
            {
                continue;
            }

            var fm = fmValue.AsObject();
            var fmTeam = fm.Get("team") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
            if (!(fm.Get("subject") is { IsText: true } subject && subject.AsString() == "join") || fmTeam != (string.IsNullOrEmpty(team) ? null : team))
            {
                continue;
            }

            if (fm.Get("id") is not { IsText: true } id || !JoinIdRe().IsMatch(id.AsString()))
            {
                continue;
            }

            var expiresAt = ExpiryAt(fm);
            if (now > expiresAt)
            {
                continue;
            }

            var why = WhyLine().Match(m.Member("text") is { IsText: true } body ? body.AsString() : string.Empty);
            string? Field(string key) => fm.Get(key) is { IsText: true } v && v.AsString().Length > 0 ? v.AsString() : null;
            found.Add((
                new PendingJoin(id.AsString(), Field("harness"), Field("name_hint"), Field("role_wanted"), why.Success ? why.Groups[1].Value : null, Field("created"), Field("expires"), expiresAt),
                Field("created") ?? "null"));
        }

        return found.OrderBy(f => f.Created, StringComparer.Ordinal).Select(f => f.Row).ToList();
    }

    /// <summary>The join requests of a team, oldest first.</summary>
    private List<LinkCommands.FoundRequest> JoinRequests(Func<string, MeshTeam?> lookup, string team)
    {
        if (lookup(team) is not { Dir: { } dir })
        {
            return [];
        }

        var found = new List<LinkCommands.FoundRequest>();
        foreach (var ex in records.ExchangesAcross(dir, team))
        {
            if (ex.Member("request") is not { IsTruthy: true } request)
            {
                continue;
            }

            var path = LinkCommands.Text(request.Member("path"));
            var m = core.ReadMsgFile(path);
            if (!m.IsNull && m.Member("fm") is { IsMap: true } fm && fm.AsObject().Get("subject") is { IsText: true } s && s.AsString() == "join")
            {
                found.Add(new LinkCommands.FoundRequest(HierarchyModule.HomeOf(ex, dir), ex, fm.AsObject(), path));
            }
        }

        return found.OrderBy(r => Js(r.Fm.Get("created")), StringComparer.Ordinal).ToList();
    }

    private Found? FindJoin(RequestContext context, Func<string, MeshTeam?> lookup, string id)
    {
        foreach (var t in mailbox.VisibleTeams(context))
        {
            var r = records.FindRequest(Dirs(context), lookup, t.Team, id);
            if (r is not null && LinkCommands.FmText(r.Fm, "subject") == "join")
            {
                return new Found(r, LinkCommands.FmText(r.Fm, "team"));
            }
        }

        return null;
    }

    private Decision? DecisionOf(JsonValue exchange)
    {
        if (exchange.Member("response") is not { IsTruthy: true } response)
        {
            return null;
        }

        var m = core.ReadMsgFile(LinkCommands.Text(response.Member("path")));
        if (m.IsNull || m.Member("fm") is not { IsMap: true } fm)
        {
            return null;
        }

        var f = fm.AsObject();
        var at = Created(f);
        return new Decision(Js(f.Get("decision")), at, f);
    }

    // ---------------------------------------------------------------- closing out requests

    private async Task ExpireAsync(LinkCommands.FoundRequest req, string team, double now, CancellationToken ct)
    {
        try
        {
            await records.RespondAsync(
                req, "expired", "ahd", "no decision in time",
                [new("basis", "expired"), new("why", "no decision in time")], "expired by ahd (expired): no decision in time", now, ct).ConfigureAwait(false);
        }
        catch (MailboxError e) when (e.Code == "already_decided")
        {
            return;
        }

        try
        {
            await effects.NotifyAsync(
                new LinkNotice(HierarchyModule.OrchestratorAddress(team), "ahd", "note", null, $"ah: join request {LinkCommands.FmText(req.Fm, "id")} from {LinkCommands.FmText(req.Fm, "harness")} expired with no decision"),
                ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Telling the Orchestrator about an expiry is best effort.
        }
    }

    /// <summary>Closes an accepted request whose member row is missing: its response is rewritten to <c>decision: failed</c> with <paramref name="error"/>. True when it was.</summary>
    private async Task<bool> CloseFailedAsync(
        RequestContext context, Func<string, MeshTeam?> lookup, LinkCommands.FoundRequest req, string team, string error, CancellationToken ct)
    {
        var id = LinkCommands.FmText(req.Fm, "id");
        var fresh = records.FindRequest(Dirs(context), lookup, team, id);
        if (fresh is null || fresh.Exchange.Member("response") is not { IsTruthy: true } response)
        {
            return false;
        }

        var path = LinkCommands.Text(response.Member("path"));
        var resp = core.ReadMsgFile(path);
        if (resp.IsNull || resp.Member("fm") is not { IsMap: true } fm || Js(fm.AsObject().Get("decision")) != "accepted")
        {
            return false;
        }

        // The rewritten response keeps every field and position of the decision, with the two that change set in place.
        var rewritten = new List<KeyValuePair<string, JsonValue?>>();
        foreach (var key in fm.AsObject().Keys)
        {
            rewritten.Add(new(key, key == "decision" ? "failed" : fm.AsObject()[key]));
        }

        rewritten.Add(new("error", LinkCommands.OneLine(error, 200)));
        try
        {
            await effects.SettleMessageAsync(
                fresh.Home, path, LinkCommands.NoteFile(rewritten, $"## [0] tldr\n- failed: {LinkCommands.OneLine(error)}"), "accepted", $"fail-{id}", ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }

        return true;
    }

    /// <summary>The status of a request, writing the expiry when its deadline has passed, and closing an accepted request still without its row after the row wait.</summary>
    private async Task<State> StateOfAsync(
        RequestContext context, Func<string, MeshTeam?> lookup, LinkCommands.FoundRequest req, string team, double now, CancellationToken ct)
    {
        var d = DecisionOf(req.Exchange);
        if (d is null && now > ExpiryAt(req.Fm))
        {
            await ExpireAsync(req, team, now, ct).ConfigureAwait(false);
            var fresh = records.FindRequest(Dirs(context), lookup, team, LinkCommands.FmText(req.Fm, "id"));
            d = DecisionOf((fresh ?? req).Exchange) ?? new Decision("expired", now, new JsonObject());
        }

        if (d is not null && d.Value == "accepted" && now - d.At >= Limits().RowWaitMs
            && lookups.Join($"{Js(d.Fm.Get("name"))}@{team}", context) is null
            && await CloseFailedAsync(context, lookup, req, team, "member row not written", ct).ConfigureAwait(false))
        {
            d = DecisionOf(req.Exchange) ?? d;
        }

        if (d is null)
        {
            return new State("pending");
        }

        return new State(d.Value is "accepted" or "denied" or "expired" or "failed" ? d.Value : "pending", d.At, d.Fm);
    }

    // ---------------------------------------------------------------- POST /v1/join

    public async Task<JoinOutcome> OpenAsync(RequestContext context, JsonObject body, CancellationToken cancellation, double? nowMs = null)
    {
        try
        {
            return new JoinOutcome(await OpenCoreAsync(context, body, nowMs ?? Now(), cancellation).ConfigureAwait(false), null);
        }
        catch (JoinError e)
        {
            return new JoinOutcome(null, new JoinFailure(e.Code, e.Message, e.Extra));
        }
        catch (MailboxError e)
        {
            return new JoinOutcome(null, new JoinFailure(e.Code, e.Message));
        }
    }

    private async Task<JsonObject> OpenCoreAsync(RequestContext context, JsonObject b, double now, CancellationToken ct)
    {
        var team = Str(b.Get("team"));
        var harness = Str(b.Get("harness"));
        var sessionId = Str(b.Get("session_id"));
        var why = Str(b.Get("why"));
        var name = Str(b.Get("name_hint"));
        var role = Str(b.Get("role_wanted"));
        if (team.Length == 0 || !config.IsHarnessName(JsonValue.FromString(harness)) || config.BuiltinHarnesses.Contains(harness, StringComparer.Ordinal))
        {
            throw Fail("bad_request", "a join needs team, and harness: a custom harness name (a built-in one needs no join)");
        }

        if (!SessionRe().IsMatch(sessionId))
        {
            throw Fail("bad_request", "a join needs session_id matching [A-Za-z0-9._-]+");
        }

        if (why.Length == 0 || why.Length > 500)
        {
            throw Fail("bad_request", "a join needs why: one to 500 characters");
        }

        if (b.Get("name_hint") is not null && !NameRe().IsMatch(name))
        {
            throw Fail("bad_request", "name_hint must match [a-z][a-z0-9-]{0,31}");
        }

        var roles = config.JoinableRoles(null);
        if (b.Get("role_wanted") is not null && !roles.Contains(role, StringComparer.Ordinal))
        {
            throw Fail("bad_request", $"role_wanted must be one of {string.Join(", ", roles)}");
        }

        long? pid = null;
        if (b.Get("pid") is { } pidValue)
        {
            if (!(pidValue.IsNumber && double.IsInteger(pidValue.AsNumber()) && pidValue.AsNumber() > 0))
            {
                throw Fail("bad_request", "pid must be a positive integer");
            }

            pid = (long)pidValue.AsNumber();
        }

        var lookup = mailbox.TeamLookup(context);
        var live = lookups.Teams(context);
        var t = lookup(team);
        if (t is null)
        {
            var names = new JsonArray();
            foreach (var row in live)
            {
                names.Add(row.AsObject()["team"]);
            }

            throw Fail("unknown_team", $"no team {JsonText.Stringify(team, 0)}", new JsonObject { ["teams"] = names });
        }

        var entry = live.Select(x => x.AsObject()).FirstOrDefault(x => x.Get("team") is { IsText: true } n && n.AsString() == team);
        if (!(entry?.Get("orchestrator_live") is { IsTruthy: true }))
        {
            throw Fail("no_orchestrator", $"the Orchestrator of {team} is not live, so nothing was asked");
        }

        var limits = Limits();
        var home = t.Dir ?? throw Fail("unknown_team", $"no team {JsonText.Stringify(team, 0)}");
        var same = JoinRequests(lookup, team)
            .Where(r => Js(r.Fm.Get("harness")) == harness && Js(r.Fm.Get("join_session")) == sessionId)
            .Reverse();
        foreach (var req in same)
        {
            var s = await StateOfAsync(context, lookup, req, team, now, ct).ConfigureAwait(false);
            if (s.Status == "failed")
            {
                continue;
            }

            var id = LinkCommands.FmText(req.Fm, "id");
            if (s.Status is "pending" or "accepted")
            {
                return new JsonObject { ["join_id"] = id, ["status"] = s.Status, ["poll"] = $"/v1/join/{id}", ["existing"] = true };
            }

            if (now - s.At < limits.CooldownMs)
            {
                throw Fail(
                    "join_cooldown",
                    $"a join from {harness} was {s.Status} less than {Ago(limits.CooldownMs)} ago",
                    new JsonObject { ["retry_after"] = Math.Max(1, Math.Ceiling((limits.CooldownMs - (now - s.At)) / 1000)) });
            }
        }

        // What the daemon saw, apart from what the harness said. The pid is a claim: these are facts about the process it names.
        var expires = records.Iso(now + limits.ExpiryMs);
        bool? alive = pid is { } p ? processes.PidAlive((int)p) : null;
        string? command = null;
        string? digest = null;
        if (alive == true)
        {
            var raw = processes.CommandLine((int)pid!.Value) ?? string.Empty;
            command = ClaimField(raw) is { Length: > 0 } c ? c : "unknown";
            if (raw.Length > 0)
            {
                digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
            }
        }

        var facts = new List<string> { $"expires: {expires}" };
        if (alive is { } a)
        {
            facts.Add($"join_pid_alive: {(a ? "true" : "false")}");
        }

        if (command is not null)
        {
            facts.Add($"join_pid_command: {command}");
        }

        if (digest is not null)
        {
            facts.Add($"join_pid_command_sha256: {digest}");
        }

        var fields = new List<KeyValuePair<string, JsonValue?>> { new("harness", harness), new("join_session", sessionId) };
        if (name.Length > 0)
        {
            fields.Add(new("name_hint", name));
        }

        if (role.Length > 0)
        {
            fields.Add(new("role_wanted", role));
        }

        if (pid is { } pidNumber)
        {
            fields.Add(new("join_pid", (double)pidNumber));
        }

        fields.Add(new("expires", expires));
        if (alive is { } alive2)
        {
            fields.Add(new("join_pid_alive", alive2));
        }

        if (command is not null)
        {
            fields.Add(new("join_pid_command", command));
        }

        if (digest is not null)
        {
            fields.Add(new("join_pid_command_sha256", digest));
        }

        var requestBody = $"## [0] tldr\n- {Claim(why)}\n\n## [1] why\n> {Claim(why, WhyMax)}\n\n## [2] facts\n{string.Join('\n', facts)}\n{FactsNote}";
        var opened = await records.OpenRequestAsync(
            "join", team, home, null, $"join:{harness}", "member", name.Length > 0 ? name : harness, why, fields, now, ct, requestBody).ConfigureAwait(false);
        var text = RequestText(new JsonObject
        {
            ["request"] = opened.Id,
            ["harness"] = harness,
            ["name_hint"] = name.Length > 0 ? name : JsonValue.Null,
            ["role_wanted"] = role.Length > 0 ? role : JsonValue.Null,
            ["pid"] = pid is { } pn ? (double)pn : JsonValue.Null,
            ["why"] = Claim(why, 200),
            ["path"] = opened.Path,
            ["expires"] = expires,
            ["pid_alive"] = alive is { } al ? al : JsonValue.Null,
            ["pid_command"] = command is not null ? command : JsonValue.Null,
        });
        await effects.NotifyAsync(new LinkNotice(HierarchyModule.OrchestratorAddress(team), "ahd", "event", "join-request", text), ct).ConfigureAwait(false);

        // A join takes effect at once (ah-simplify F1); the request and its decision stay on record for status and the UI. A
        // request the daemon cannot accept on its own (no role named, a name taken) stays pending for the Orchestrator.
        try
        {
            _ = await AcceptAsync(
                context, lookup, new LinkCommands.Caller("orchestrator", string.Empty, team), null, team, opened.Id, null, null,
                "immediate", "joined at once: ah records joins, it does not hold them", now, ct).ConfigureAwait(false);
            return new JsonObject { ["join_id"] = opened.Id, ["status"] = "accepted", ["poll"] = $"/v1/join/{opened.Id}" };
        }
        catch (Exception e) when (e is JoinError or MailboxError)
        {
            return new JsonObject { ["join_id"] = opened.Id, ["status"] = "pending", ["poll"] = $"/v1/join/{opened.Id}" };
        }
    }

    // ---------------------------------------------------------------- GET /v1/join/<id>

    public async Task<JoinOutcome> PollAsync(RequestContext context, string id, string? wait, CancellationToken cancellation)
    {
        try
        {
            return new JoinOutcome(await PollCoreAsync(context, id, wait, cancellation).ConfigureAwait(false), null);
        }
        catch (JoinError e)
        {
            return new JoinOutcome(null, new JoinFailure(e.Code, e.Message, e.Extra));
        }
        catch (MailboxError e)
        {
            return new JoinOutcome(null, new JoinFailure(e.Code, e.Message));
        }
    }

    private async Task<JsonObject> PollCoreAsync(RequestContext context, string id, string? wait, CancellationToken ct)
    {
        var lookup = mailbox.TeamLookup(context);
        var seconds = wait is null ? 0 : JsConvert.ToNumber(JsonValue.FromString(wait));
        var span = Math.Min(25, Math.Max(0, double.IsNaN(seconds) ? 0 : seconds));
        var deadline = Environment.TickCount64 + (long)(span * 1000);
        var seen = false;
        while (true)
        {
            var req = FindJoin(context, lookup, id);
            if (req is null && !seen)
            {
                throw Fail("not_found", $"no join request {JsonText.Stringify(id, 0)}");
            }

            if (req is null)
            {
                // The team file is being rewritten (an accept adds the member), so the team is unreadable for an instant: ask again.
                if (Environment.TickCount64 >= deadline)
                {
                    return new JsonObject { ["join_id"] = id, ["status"] = "pending" };
                }

                await Task.Delay(100, ct).ConfigureAwait(false);
                continue;
            }

            seen = true;
            var s = await StateOfAsync(context, lookup, req.Request, req.Team, Now(), ct).ConfigureAwait(false);
            var fm = s.Fm ?? new JsonObject();
            if (s.Status == "denied")
            {
                return new JsonObject { ["join_id"] = id, ["status"] = "denied", ["why"] = fm.Get("why") is { IsTruthy: true } w ? w : string.Empty };
            }

            if (s.Status == "expired")
            {
                return new JsonObject { ["join_id"] = id, ["status"] = "expired" };
            }

            if (s.Status == "failed")
            {
                return new JsonObject { ["join_id"] = id, ["status"] = "failed", ["error"] = fm.Get("error") is { IsTruthy: true } e ? e : string.Empty };
            }

            if (s.Status == "accepted" && lookups.Join($"{Js(fm.Get("name"))}@{req.Team}", context) is { } values)
            {
                var accepted = new JsonObject { ["join_id"] = id, ["status"] = "accepted" };
                foreach (var key in values.Keys)
                {
                    accepted[key] = values[key];
                }

                return accepted;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return new JsonObject { ["join_id"] = id, ["status"] = "pending" };
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------- deciding

    private static string FreeName(string harness, JsonArray members)
    {
        var taken = members.Where(m => m.IsMap).Select(m => Js(m.AsObject().Get("name"))).ToHashSet(StringComparer.Ordinal);
        for (var n = 1; ; n++)
        {
            var candidate = $"{harness}-{n.ToString(CultureInfo.InvariantCulture)}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    private static JsonArray MembersOf(MeshTeam t) =>
        t.Data.IsMap && t.Data.AsObject().Get("members") is { IsArray: true } m ? m.AsArray() : [];

    private static void RequireJoinDecider(LinkCommands.Caller caller, string team)
    {
        if (caller.Kind != "orchestrator" || caller.Team != team)
        {
            throw new MailboxError("orchestrator_only", "join is decided by the team's Orchestrator");
        }
    }

    private LinkCommands.FoundRequest RequestOrFail(RequestContext context, Func<string, MeshTeam?> lookup, string team, string reqId)
    {
        var req = records.FindRequest(Dirs(context), lookup, team, reqId);
        if (req is null || LinkCommands.FmText(req.Fm, "subject") != "join" || LinkCommands.FmText(req.Fm, "team") != team)
        {
            throw Fail("unknown_request", $"no join request {reqId} on team {team}");
        }

        return req;
    }

    /// <summary>
    /// Refuses a <c>--basis rejoin</c> accept unless the code can confirm it: a prior accepted join of this harness under this name on the
    /// team, the team file still holding that member's custom row with no live registration, and the request's process command equal to
    /// the one the prior accept recorded. Returns the existing row.
    /// </summary>
    private JsonObject CheckRejoin(Func<string, MeshTeam?> lookup, string team, LinkCommands.FoundRequest req, string name)
    {
        var t = lookup(team)!;
        var reqId = LinkCommands.FmText(req.Fm, "id");
        var harness = LinkCommands.FmText(req.Fm, "harness");
        var accepted = JoinRequests(lookup, team)
            .Where(r => LinkCommands.FmText(r.Fm, "id") != reqId && LinkCommands.FmText(r.Fm, "team") == team && LinkCommands.FmText(r.Fm, "harness") == harness)
            .Select(r => DecisionOf(r.Exchange))
            .Where(d => d is not null && d.Value == "accepted" && Js(d.Fm.Get("name")) == name)
            .Select(d => d!)
            .ToList();

        // Times have one-second resolution, so records of one second are all "the latest": a rejoin must match every one of them.
        var newest = accepted.Count == 0 ? 0 : Math.Max(0, accepted.Max(d => d.At));
        var prior = accepted.Where(d => d.At == newest).ToList();
        const string VetNew = "vet this request as a new one (--basis user-asked or user-approved)";
        if (prior.Count == 0)
        {
            throw Fail("rejoin_refused", $"no earlier accepted join of {harness} as {name}@{team} is on record; {VetNew}");
        }

        var row = MembersOf(t)
            .Where(m => m.IsMap)
            .Select(m => m.AsObject())
            .FirstOrDefault(m => Js(m.Get("name")) == name && Js(m.Get("kind")) == config.KindCustom && Js(m.Get("harness")) == harness);
        if (row is null)
        {
            throw Fail("rejoin_refused", $"the team file has no custom member {name} of harness {harness}; {VetNew}");
        }

        var address = $"{name}@{team}";
        if (mailbox.LiveRegistrations().Any(r => mailbox.RegistrationBoxes(r).Any(b => b.Address == address)))
        {
            throw Fail("rejoin_refused", $"{address} is live, so this is not a rejoin; {VetNew}");
        }

        if (!(req.Fm.Get("join_pid_command_sha256") is { IsTruthy: true } digest))
        {
            throw Fail("rejoin_refused", $"the request carries no process command to compare with the recorded one; {VetNew}");
        }

        if (prior.Any(d => Js(d.Fm.Get("admitted_command_sha256")) != Js(digest)))
        {
            throw Fail("rejoin_refused", $"the request's process command differs from the one recorded when {address} was admitted; {VetNew}");
        }

        return row;
    }

    private async Task<JsonObject> AcceptAsync(
        RequestContext context, Func<string, MeshTeam?> lookup, LinkCommands.Caller caller, string? session, string team, string reqId,
        string? nameOpt, string? roleOpt, string basis, string why, double now, CancellationToken ct)
    {
        RequireJoinDecider(caller, team);
        var req = RequestOrFail(context, lookup, team, reqId);
        var t = lookup(team) ?? throw Fail("unknown_request", $"no join request {reqId} on team {team}");
        if (DecisionOf(req.Exchange) is not null)
        {
            throw Fail("already_decided", $"join request {reqId} is already decided");
        }

        if (now > ExpiryAt(req.Fm))
        {
            await ExpireAsync(req, team, now, ct).ConfigureAwait(false);
            throw Fail("already_decided", $"join request {reqId} expired before it was decided");
        }

        var rejoin = basis == "rejoin";
        var members = MembersOf(t);
        var harness = LinkCommands.FmText(req.Fm, "harness");
        var chosen = Str(nameOpt is null ? (JsonValue?)null : JsonValue.FromString(nameOpt));
        if (chosen.Length == 0)
        {
            chosen = Str(req.Fm.Get("name_hint"));
        }

        if (chosen.Length == 0 && !rejoin)
        {
            chosen = FreeName(harness, members);
        }

        if (!NameRe().IsMatch(chosen))
        {
            throw Fail(
                rejoin ? "rejoin_refused" : "bad_request",
                rejoin ? "a rejoin needs --name <the member>, or a name_hint in the request" : "the member name must match [a-z][a-z0-9-]{0,31}");
        }

        var existing = rejoin ? CheckRejoin(lookup, team, req, chosen) : null;
        if (!rejoin && members.Any(m => m.IsMap && Js(m.AsObject().Get("name")) == chosen))
        {
            throw Fail("bad_request", $"team {team} already has a member named {chosen}");
        }

        var chosenRole = Str(roleOpt is null ? (JsonValue?)null : JsonValue.FromString(roleOpt));
        if (chosenRole.Length == 0)
        {
            chosenRole = existing is not null && existing.Get("role") is { IsTruthy: true } r ? Js(r) : Str(req.Fm.Get("role_wanted"));
        }

        if (chosenRole.Length == 0)
        {
            throw Fail("bad_request", "accept needs --role <role> (the request named none)");
        }

        var roles = config.JoinableRoles(null);
        if (!roles.Contains(chosenRole, StringComparer.Ordinal))
        {
            throw Fail("bad_request", $"role must be one of {string.Join(", ", roles)}");
        }

        var extra = new List<KeyValuePair<string, JsonValue?>>
        {
            new("decided_session", session is null ? JsonValue.Null : session),
            new("basis", basis),
            new("why", Claim(why, WhyMax)),
            new("name", chosen),
            new("role", chosenRole),
        };
        if (req.Fm.Get("join_pid_command") is { IsTruthy: true } command)
        {
            extra.Add(new("admitted_command", command));
        }

        if (req.Fm.Get("join_pid_command_sha256") is { IsTruthy: true } digest)
        {
            extra.Add(new("admitted_command_sha256", digest));
        }

        if (!rejoin && effects.LaneMembersRefusal(t.Dir ?? string.Empty, team) is { } laneFull)
        {
            throw Fail("lane-members-full", laneFull);
        }

        await records.RespondAsync(
            req, "accepted", caller.Address, why, extra, $"accepted by {caller.Address} ({basis}): {Claim(why, WhyMax)}", now, ct).ConfigureAwait(false);
        var answer = new JsonObject { ["name"] = chosen, ["role"] = chosenRole, ["address"] = $"{chosen}@{team}" };
        if (rejoin)
        {
            return answer;
        }

        var row = new JsonObject
        {
            ["role"] = chosenRole,
            ["name"] = chosen,
            ["route"] = "mail",
            ["kind"] = config.KindCustom,
            ["harness"] = harness,
        };
        try
        {
            await effects.AddMemberAsync(t.Dir ?? string.Empty, team, row, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var detail = LinkCommands.OneLine(e.Message, 120);
            var text = $"accepted, but the member row was not written ({(detail.Length > 0 ? detail : "write failed")}); the request is closed, the harness may ask again";
            await CloseFailedAsync(context, lookup, req, team, text, ct).ConfigureAwait(false);
            throw Fail("member_row_failed", text);
        }

        return answer;
    }

    private async Task<JsonObject> DenyAsync(
        RequestContext context, Func<string, MeshTeam?> lookup, LinkCommands.Caller caller, string? session, string team, string reqId,
        string basis, string why, double now, CancellationToken ct)
    {
        RequireJoinDecider(caller, team);
        var req = RequestOrFail(context, lookup, team, reqId);
        if (DecisionOf(req.Exchange) is null && now > ExpiryAt(req.Fm))
        {
            await ExpireAsync(req, team, now, ct).ConfigureAwait(false);
            throw Fail("already_decided", $"join request {reqId} expired before it was decided");
        }

        await records.RespondAsync(
            req, "denied", caller.Address, why,
            [new("decided_session", session is null ? JsonValue.Null : session), new("basis", basis), new("why", Claim(why, WhyMax))],
            $"denied by {caller.Address} ({basis}): {Claim(why, WhyMax)}", now, ct).ConfigureAwait(false);
        return new JsonObject { ["request"] = reqId };
    }

    public async Task<JoinCommandOutcome> CommandAsync(JoinCommandRequest request, CancellationToken cancellation)
    {
        try
        {
            return new JoinCommandOutcome(await CommandCoreAsync(request, cancellation).ConfigureAwait(false), null);
        }
        catch (JoinError e)
        {
            return new JoinCommandOutcome(null, new JoinFailure(e.Code, e.Message, e.Extra));
        }
        catch (MailboxError e)
        {
            return new JoinCommandOutcome(null, new JoinFailure(e.Code, e.Message));
        }
    }

    private async Task<LinkCommandResult> CommandCoreAsync(JoinCommandRequest request, CancellationToken ct)
    {
        var sub = request.Positional.ElementAtOrDefault(0);
        var reqId = request.Positional.ElementAtOrDefault(1);
        if (sub is not ("accept" or "deny"))
        {
            throw Fail("usage", Usage);
        }

        if (string.IsNullOrEmpty(reqId))
        {
            throw Fail("usage", $"join {sub} needs a join id; {Usage}");
        }

        var bases = sub == "accept" ? BasesAccept : BasesDeny;
        var basis = request.Options.TryGetValue("basis", out var b) ? b : string.Empty;
        if (!bases.Contains(basis, StringComparer.Ordinal))
        {
            throw Fail("usage", $"join {sub} needs --basis {string.Join('|', bases)}; {Usage}");
        }

        var why = request.Options.TryGetValue("why", out var w) ? Claim(w, 1_000_000) : string.Empty;
        if (why.Length == 0 || why.Length > WhyMax)
        {
            throw Fail("usage", $"join {sub} needs --why <text> of 1 to {WhyMax.ToString(CultureInfo.InvariantCulture)} characters, naming the evidence; {Usage}");
        }

        var context = request.Context;
        var lookup = mailbox.TeamLookup(context);
        var caller = records.CallerOf(request.Deciders.Select(d => d.Address).ToList(), lookup);
        var session = request.Deciders.FirstOrDefault(d => d.Address == caller.Address)?.Session;
        var found = FindJoin(context, lookup, reqId);
        var team = request.Options.TryGetValue("team", out var named) ? named : found?.Team;
        if (string.IsNullOrEmpty(team))
        {
            throw Fail("unknown_request", $"no join request {reqId}");
        }

        var now = request.NowMs ?? Now();
        if (sub == "accept")
        {
            var name = request.Options.GetValueOrDefault("name");
            var role = request.Options.GetValueOrDefault("role");
            var r = await AcceptAsync(context, lookup, caller, session, team, reqId, name, role, basis, why, now, ct).ConfigureAwait(false);
            return new LinkCommandResult(r, $"accepted {reqId}: {Js(r["address"])} ({Js(r["role"])})");
        }

        var denied = await DenyAsync(context, lookup, caller, session, team, reqId, basis, why, now, ct).ConfigureAwait(false);
        return new LinkCommandResult(denied, $"denied {reqId}");
    }
}
