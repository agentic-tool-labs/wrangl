using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>Where a link command looks for a team's records besides the team's own directory: the caller's hierarchy directory and the main checkout's.</summary>
public sealed record LinkContext(string? HierarchyDir, string? MainHierarchyDir);

/// <summary>One mailbox entry a link command sends: <c>kind</c> is <c>note</c> or <c>event</c> (then <paramref name="Event"/> names it).</summary>
public sealed record LinkNotice(string To, string From, string Kind, string? Event, string Text, string? PostLink = null);

/// <summary>One mirrored note in a task's record. <paramref name="N"/> is 0 for a request or decision note, null for a posted message.</summary>
public sealed record LinkMirror(string Link, string Task, string TaskHome, string FromAddress, string ToAddress, string ToRole, int? N, string EntryId, string Text);

/// <summary>The writes a link command makes, which the daemon (in process) and the CLI (through the state helper) each supply.</summary>
public interface ILinkEffects
{
    /// <summary>A <c>links</c> domain operation (<c>grant</c>, <c>deny</c>, <c>revoke</c>, <c>pend</c>, <c>extend</c>, <c>sweep</c>, <c>read</c>); its result object.</summary>
    Task<JsonObject> LinksAsync(string op, JsonObject args, string? opId, CancellationToken cancellation);

    /// <summary>Creates <paramref name="path"/> exclusively under the op id; false when the file already exists.</summary>
    Task<bool> CreateMessageAsync(string dir, string path, string content, string opId, CancellationToken cancellation, bool ownPoolContext = false);

    /// <summary>Delivers one mailbox entry.</summary>
    Task NotifyAsync(LinkNotice notice, CancellationToken cancellation);
}

/// <summary>Writes the mirror of a linked message or note beside its task; idempotent for a repeated entry id.</summary>
public interface ILinkMirror
{
    Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation);
}

/// <summary>
/// A link command as the CLI parses it: <paramref name="Command"/> is <c>link-request</c> or <c>link</c>, <paramref name="Positional"/>
/// the words after it, <paramref name="Options"/> the string-valued flags, <paramref name="Addresses"/> the caller's session addresses.
/// </summary>
public sealed record LinkCommandRequest(
    string Command,
    IReadOnlyList<string> Positional,
    IReadOnlyDictionary<string, string> Options,
    LinkContext Context,
    Func<string, MeshTeam?> Lookup,
    IReadOnlyList<string> Addresses,
    double? NowMs = null);

/// <summary>A command's structured result and its one-screen text.</summary>
public sealed record LinkCommandResult(JsonValue Result, string Text);

/// <summary>Whether the daemon granted a link request on its own, and why not when it did not.</summary>
public sealed record AutoGrantResult(bool Granted, string? Why = null);

/// <summary>
/// Link requests and their decisions, and the request / decide / respond machinery they share with joins. A request is a msgs request
/// file addressed to the Orchestrator carrying <c>subject: link|extension|join</c>; a decision is the exclusive creation of its
/// response file, so exactly one decision wins; the effect follows the claim, never precedes it.
/// </summary>
public interface ILinkCommands
{
    /// <summary>Runs <c>link-request</c> or <c>link &lt;sub&gt; ...</c>. Failures carry the code of the refusal (<c>orchestrator_only</c>, <c>already_decided</c>, <c>not_extendable</c>, ...).</summary>
    Task<MailboxOutcome<LinkCommandResult>> RunAsync(LinkCommandRequest request, CancellationToken cancellation);

    /// <summary>What the daemon does when a <c>link-request</c> event lands: grant it if it qualifies.</summary>
    Task<AutoGrantResult> AutoGrantAsync(LinkContext context, Func<string, MeshTeam?> lookup, string team, string requestId, CancellationToken cancellation, double? nowMs = null);

    /// <summary>
    /// Revokes every active run-scoped link of <paramref name="team"/> whose run has ended (see <see cref="IRunFacts"/>), and every
    /// assignment link whose members are no longer assigned together; the number revoked.
    /// </summary>
    Task<int> RevokeDeadRunLinksAsync(LinkContext context, Func<string, MeshTeam?> lookup, string team, CancellationToken cancellation, double? nowMs = null);
}

internal sealed partial class LinkCommands(
    IMeshRule rule,
    ILinks links,
    IHierarchyCore core,
    IHierarchyMessages messages,
    IFileReader files,
    IClock clock,
    ILinkEffects effects,
    ILinkMirror mirror,
    IRunFacts? runFacts = null) : ILinkCommands
{
    private const int MaxEventTextChars = 3000;

    private static readonly string[] Subjects = ["link", "extension", "join"];

    private static readonly string[] Subs = ["grant", "deny", "revoke", "extend-request", "extend", "list", "show", "post"];

    [GeneratedRegex("[" + JsText.SpaceClass + "]+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex("^---[" + JsText.SpaceClass + "]*(?=[\\n\\r\\u2028\\u2029]|\\z)", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex Fence();

    internal sealed record Caller(string Kind, string Address, string? Team = null, string? Role = null, string? Name = null);

    private sealed record FoundTask(string Dir, JsonValue Exchange);

    internal sealed record FoundRequest(string Home, JsonValue Exchange, JsonObject Fm, string Path);

    private sealed record FoundLink(string Dir, string Team, JsonValue Link);

    internal sealed record Opened(string Id, string Path);

    // ---------------------------------------------------------------- small helpers

    internal static string OneLine(string? s, int n = 160)
    {
        var line = JsText.Trim(Spaces().Replace(s ?? string.Empty, " "));
        return line.Length > n ? line[..n] : line;
    }

    internal static double Num(JsonValue? v, double d)
    {
        if (v is null)
        {
            return d;
        }

        var n = JsConvert.ToNumber(v);
        return double.IsFinite(n) && n > 0 ? Math.Floor(n) : d;
    }

    private static double Num(string? v, double d) => v is null ? d : Num(JsonValue.FromString(v), d);

    internal static string RoleWord(JsonValue? r)
    {
        var s = r is { IsText: true } t ? t.AsString() : string.Empty;
        return s.Length > 0 && s.All(c => c is (>= 'a' and <= 'z') or '-') ? s : "member";
    }

    private static string RoleWord(string? r) => RoleWord(r is null ? (JsonValue?)null : JsonValue.FromString(r));

    internal static string Text(JsonValue? v) => JsConvert.ToJsString(v);

    private static List<string> Split(JsonValue? list) => Split(list is { IsTruthy: true } ? Text(list) : string.Empty);

    private static List<string> Split(string text) =>
        text.Split(',').Select(s => JsText.Trim(s)).Where(s => s.Length > 0).ToList();

    private static JsonArray Strings(IEnumerable<string> items)
    {
        var all = new JsonArray();
        foreach (var item in items)
        {
            all.Add(item);
        }

        return all;
    }

    private double NowOr(double? now) => now ?? clock.UtcNow.ToUnixTimeMilliseconds();

    internal string NewId() => core.NewId();

    internal string Iso(double now) => messages.LocalIso(DateTimeOffset.FromUnixTimeMilliseconds((long)now));

    private double Limit(string name, double fallback = 0) => AutoGrant.Limit(links, name, fallback);

    /// <summary>Whether the requester and its one peer are an implementer and the reviewer: the pair a review loop runs between.</summary>
    private bool IsLoopPair(Func<string, MeshTeam?> lookup, string requester, List<string> peers) =>
        peers.Count == 1 && AutoGrant.IsLoopPair(rule.Classify(requester, lookup).Role, rule.Classify(peers[0], lookup).Role);

    private static MeshTeam TeamOf(Func<string, MeshTeam?> lookup, string team) =>
        lookup(team) ?? throw new MailboxError("unknown_address", $"no team {team}");

    internal static string NoteFile(IEnumerable<KeyValuePair<string, JsonValue?>> front, string text) =>
        MeshSend.NoteFile(front.Select(kv => (kv.Key, kv.Value)), text);

    internal string MsgFilename(string id, string to, string slug, string type) =>
        messages.MsgFilename(new JsonObject { ["id"] = id, ["to"] = to, ["slug"] = slug, ["type"] = type });

    // ---------------------------------------------------------------- who is calling

    internal Caller CallerOf(IReadOnlyList<string> addresses, Func<string, MeshTeam?> lookup, string? team = null)
    {
        var parties = addresses.Select(a => rule.Classify(a, lookup)).Where(c => c.Kind is "orchestrator" or "member").ToList();
        var pick = (team is null ? null : parties.FirstOrDefault(c => c.Kind == "orchestrator" && c.Team == team))
            ?? parties.FirstOrDefault(c => c.Kind == "orchestrator") ?? parties.FirstOrDefault();
        return pick is null
            ? new Caller("user", "user")
            : new Caller(pick.Kind, pick.Address ?? string.Empty, pick.Team, pick.Role, pick.Name);
    }

    internal static void RequireOrchestrator(Caller caller, string? team = null)
    {
        if (caller.Kind == "user")
        {
            return;
        }

        if (caller.Kind != "orchestrator" || (team is not null && caller.Team != team))
        {
            throw new MailboxError("orchestrator_only", "only the Orchestrator of the team may run this");
        }
    }

    // ---------------------------------------------------------------- finding things

    private static List<string> Pools(LinkContext ctx, Func<string, MeshTeam?> lookup, string team) =>
        AutoGrant.Pools(lookup(team)?.Dir, ctx.HierarchyDir, ctx.MainHierarchyDir);

    /// <summary>The exchanges of every pool of <paramref name="team"/> recorded in <paramref name="dir"/> (just that pool for no team).</summary>
    internal JsonArray ExchangesAcross(string dir, string? team) =>
        messages.ListExchangesAcross(dir, string.IsNullOrEmpty(team) ? [] : [team]);

    private JsonValue? ExchangeOf(string dir, string team, string id) =>
        ExchangesAcross(dir, team).Cast<JsonValue>().Cast<JsonValue?>().FirstOrDefault(x => x!.Value.Member("id") is { IsText: true } t && t.AsString() == id);

    private static bool HasRequest(JsonValue exchange) => exchange.Member("request") is { IsTruthy: true };

    private FoundTask? FindTask(LinkContext ctx, Func<string, MeshTeam?> lookup, string team, string taskId) =>
        AutoGrant.FindTask(messages, core, Pools(ctx, lookup, team), team, taskId) is { } t ? new FoundTask(t.Dir, t.Exchange) : null;

    internal FoundRequest? FindRequest(LinkContext ctx, Func<string, MeshTeam?> lookup, string team, string id)
    {
        foreach (var dir in Pools(ctx, lookup, team))
        {
            if (ExchangeOf(dir, team, id) is not { } ex || !HasRequest(ex))
            {
                continue;
            }

            var path = Text(ex.Member("request")!.Value.Member("path"));
            var m = core.ReadMsgFile(path);
            if (!m.IsNull && m.Member("fm") is { IsMap: true } fm
                && fm.AsObject().Get("subject") is { IsText: true } subject && Subjects.Contains(subject.AsString(), StringComparer.Ordinal)
                && OfTeam(fm.AsObject(), team))
            {
                return new FoundRequest(HierarchyModule.HomeOf(ex, dir), ex, fm.AsObject(), path);
            }
        }

        return null;
    }

    /// <summary>Whether a request belongs to <paramref name="team"/>: it names that team, or names none. Every team's search also covers the context's own pool, so the team has to be checked on the request.</summary>
    internal static bool OfTeam(JsonObject fm, string team) =>
        NamesTeam(fm) && fm.Get("team")!.Value.AsString() == team;

    private static bool NamesTeam(JsonObject fm) => fm.Get("team") is { IsText: true } named && named.AsString() is not ("" or "null");

    private bool SameDir(string? a, string? b) =>
        !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && Normalized(a) == Normalized(b);

    private string Normalized(string dir)
    {
        string real;
        try
        {
            real = files.RealPath(dir);
        }
        catch (IOException)
        {
            real = dir;
        }

        return real.Length > 1 ? real.TrimEnd('/') : real;
    }

    /// <summary>
    /// Why request <paramref name="id"/> was not found on <paramref name="team"/>: when it exists but names no team, the link mesh has no
    /// default team to answer it under, so it says to re-send it with a team instead of calling it unknown.
    /// </summary>
    private MailboxError RequestNotFound(LinkContext ctx, Func<string, MeshTeam?> lookup, string team, string id, string what)
    {
        foreach (var dir in Pools(ctx, lookup, team))
        {
            if (ExchangeOf(dir, team, id) is { } ex && HasRequest(ex)
                && core.ReadMsgFile(Text(ex.Member("request")!.Value.Member("path"))) is { IsNull: false } m
                && m.Member("fm") is { IsMap: true } fm && !NamesTeam(fm.AsObject()))
            {
                return new MailboxError("teamless_request", $"this {(what.Length > 0 ? what + " " : string.Empty)}request names no team; re-send it with --team <team>");
            }
        }

        return new MailboxError("unknown_request", $"no {(what.Length > 0 ? what + " " : string.Empty)}request {id} on team {team}");
    }

    private FoundLink? FindLink(Func<string, MeshTeam?> lookup, string team, string id)
    {
        if (lookup(team) is not { Dir: { } dir })
        {
            return null;
        }

        var all = links.Judge(dir, team, clock.UtcNow.ToUnixTimeMilliseconds());
        foreach (var l in all)
        {
            if (l.Member("id") is { IsText: true } t && t.AsString() == id)
            {
                return new FoundLink(dir, team, l);
            }
        }

        return null;
    }

    internal static string FmText(JsonObject fm, string key) => Text(fm.Get(key));

    internal static bool Decided(FoundRequest req) => req.Exchange.Member("response") is { IsTruthy: true };

    // ---------------------------------------------------------------- the shared request machinery

    internal async Task<Opened> OpenRequestAsync(
        string subject, string team, string home, string? task, string requester, string? requesterRole, string? requesterName,
        string? why, IEnumerable<KeyValuePair<string, JsonValue?>> fields, double now, CancellationToken ct, string? body = null)
    {
        var id = NewId();
        var slug = $"{(subject == "extension" ? "lext" : subject == "join" ? "join" : "lreq")}-{Tail(string.IsNullOrEmpty(task) ? id : task, 4)}";
        var path = NodePath.Join(home, "msgs", MsgFilename(id, "orchestrator", slug, "request"));
        var front = new List<KeyValuePair<string, JsonValue?>>
        {
            new("id", id), new("type", "request"), new("to", "orchestrator"), new("from", RoleWord(requesterRole)), new("slug", slug),
            new("parent", string.IsNullOrEmpty(task) ? JsonValue.Null : task), new("reason", JsonValue.Null), new("eta", "small"),
            new("to_name", JsonValue.Null), new("from_name", string.IsNullOrEmpty(requesterName) ? JsonValue.Null : requesterName),
            new("team", team), new("created", Iso(now)), new("subject", subject), new("requester", requester),
        };
        front.AddRange(fields);
        body ??= $"## [0] tldr\n- {OneLine(why)}\n\n## [1] why\n{why ?? string.Empty}";
        if (!await effects.CreateMessageAsync(home, path, NoteFile(front, body), $"req-{id}", ct).ConfigureAwait(false))
        {
            throw new IOException($"EEXIST: file already exists, open '{path}'");
        }

        return new Opened(id, path);
    }

    private static string Tail(string s, int n) => s.Length > n ? s[^n..] : s;

    internal async Task RespondAsync(
        FoundRequest req, string decision, string by, string why, IEnumerable<KeyValuePair<string, JsonValue?>> extra, string? tldr, double now, CancellationToken ct)
    {
        var reqId = FmText(req.Fm, "id");
        if (Decided(req))
        {
            throw new MailboxError("already_decided", $"request {reqId} is already decided");
        }

        var to = RoleWord(req.Fm.Get("from"));
        var slug = Text(req.Exchange.Member("slug"));
        var name = MsgFilename(reqId, to, slug, "response");
        var front = new List<KeyValuePair<string, JsonValue?>>
        {
            new("id", reqId), new("type", "response"), new("to", to), new("from", "orchestrator"), new("slug", slug),
            new("subject", req.Fm.Get("subject")), new("decision", decision), new("decided_by", by), new("created", Iso(now)),
        };
        front.AddRange(extra);
        var body = $"## [0] tldr\n- {tldr ?? $"{decision}{(why.Length > 0 ? $": {OneLine(why)}" : string.Empty)}"}";
        var responsePool = messages.ResponsePool(req.Path);
        var status = responsePool["status"].AsString();
        if (status == "refused")
        {
            throw new InvalidOperationException(responsePool["reason"].AsString());
        }

        var home = status == "allowed" ? responsePool["pool"].AsString() : req.Home;
        bool created;
        try
        {
            created = await effects.CreateMessageAsync(home, NodePath.Join(home, "msgs", name), NoteFile(front, body), $"resp-{reqId}-{decision}", ct, ownPoolContext: status == "allowed").ConfigureAwait(false);
        }
        catch (InvalidOperationException e) when (e.Message.Contains("outside the request's hierarchy directories", StringComparison.Ordinal))
        {
            throw new MailboxError("bad_request", $"request {reqId} lives in {home}, outside this --cwd's hierarchy; re-run with --cwd {WorkTreeOf(home)}");
        }

        if (!created)
        {
            throw new MailboxError("already_decided", $"request {reqId} is already decided");
        }
    }

    private static string WorkTreeOf(string hierarchyDir)
    {
        const string Suffix = "/.claude/ah2";
        return hierarchyDir.EndsWith(Suffix, StringComparison.Ordinal) ? hierarchyDir[..^Suffix.Length] : hierarchyDir;
    }

    private async Task RecordNoteAsync(string link, string task, string home, string fromAddress, string toAddress, string toRole, string text, CancellationToken ct)
    {
        try
        {
            await mirror.MirrorAsync(new LinkMirror(link, task, home, fromAddress, toAddress, toRole, 0, NewId(), text), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // The record note is best effort: a failure to write it never undoes the decision.
        }
    }

    private static string Json(JsonObject o) => JsonText.Stringify(o, 0) is { Length: > MaxEventTextChars } s ? s[..MaxEventTextChars] : JsonText.Stringify(o, 0);

    // ---------------------------------------------------------------- link-request

    private async Task<JsonObject> LinkRequestAsync(
        LinkContext ctx, Func<string, MeshTeam?> lookup, Caller caller, string? to, string? task, string? why, string? direction,
        string? messagesOpt, string? minutesOpt, double now, CancellationToken ct)
    {
        if (caller.Kind != "member")
        {
            throw new MailboxError("bad_request", "link-request is a member's: it asks the Orchestrator of your team for a link");
        }

        var peers = Split(to ?? string.Empty);
        if (peers.Count == 0)
        {
            throw new MailboxError("bad_request", "link-request needs --to <address>[,<address>]");
        }

        if (string.IsNullOrEmpty(task))
        {
            throw new MailboxError("bad_request", "link-request needs --task <request id>");
        }

        if (JsText.Trim(why ?? string.Empty).Length == 0)
        {
            throw new MailboxError("bad_request", "link-request needs --why <reason>");
        }

        foreach (var p in peers)
        {
            var c = rule.Classify(p, lookup);
            if (c.Kind != "member" || c.Team != caller.Team)
            {
                throw new MailboxError("bad_request", $"{p} is not a member of {caller.Team}; a link joins members of one team");
            }

            if (p == caller.Address)
            {
                throw new MailboxError("bad_request", "a link needs a peer other than yourself");
            }
        }

        var team = caller.Team!;
        var found = FindTask(ctx, lookup, team, task);
        var home = found?.Dir ?? TeamOf(lookup, team).Dir ?? throw new MailboxError("unknown_address", $"no team {team}");
        var dir = direction == "out" ? "out" : "both";

        // A repeat of a request whose link is still live answers with that link and opens no second one (b481).
        foreach (var l in lookup(team)?.Dir is { } linksDir ? links.Judge(linksDir, team, now) : new JsonArray())
        {
            // a link opened by assignment is not a repeat: a review loop on the same task still gets its own link, with its rounds
            if (Text(l.Member("state")) == "active" && l.Member("assigned") is not { IsText: true } && Text(l.Member("requester")) == caller.Address && Text(l.Member("task")) == task
                && Text(l.Member("direction")) == dir && peers.ToHashSet(StringComparer.Ordinal).SetEquals(PeersOf(l)))
            {
                var live = Text(l.Member("id"));
                return new JsonObject { ["id"] = live.StartsWith("lk-", StringComparison.Ordinal) ? live[3..] : live, ["link"] = live, ["state"] = "active", ["existing"] = true };
            }
        }

        var askedMessages = Num(messagesOpt, Limit("messages"));
        var askedMinutes = Num(minutesOpt, AutoGrant.Minutes(links.Limits(), IsLoopPair(lookup, caller.Address, peers)));
        var req = await OpenRequestAsync(
            "link", team, home, task, caller.Address, caller.Role, caller.Name, why,
            [new("peers", string.Join(',', peers)), new("direction", dir), new("messages", askedMessages), new("minutes", askedMinutes)], now, ct).ConfigureAwait(false);
        var hint = found is not null && Links.ExchangeClosedOnPurpose(core, found.Exchange) ? "task_closed" : found is null ? "task_unknown" : null;
        var body = new JsonObject
        {
            ["request"] = req.Id,
            ["from"] = caller.Address,
            ["peers"] = Strings(peers),
            ["task"] = task,
            ["direction"] = dir,
            ["messages"] = askedMessages,
            ["minutes"] = askedMinutes,
            ["why"] = OneLine(why, 200),
        };
        if (hint is not null)
        {
            body["hint"] = hint;
        }

        body["path"] = req.Path;
        await effects.NotifyAsync(new LinkNotice(HierarchyModule.OrchestratorAddress(team), caller.Address, "event", "link-request", Json(body)), ct).ConfigureAwait(false);
        var result = new JsonObject { ["id"] = req.Id, ["path"] = req.Path };
        if (hint is not null)
        {
            result["hint"] = hint;
        }

        // A link takes effect at once (ah-simplify F1); the request and its response stay on record for status and the UI.
        var filed = FindRequest(ctx, lookup, team, req.Id);
        var granted = filed is not null && (await GrantNowAsync(ctx, lookup, team, req.Id, filed, now, ct).ConfigureAwait(false)).Granted;
        result["link"] = granted ? IdOfLink(req.Id) : JsonValue.Null;

        return result;
    }

    // ---------------------------------------------------------------- deciding

    private static string IdOfLink(string reqId) => $"lk-{reqId}";

    private async Task<JsonObject> GrantLinkAsync(
        LinkContext ctx, Func<string, MeshTeam?> lookup, Caller caller, string team, string reqId, string? messagesOpt, string? minutesOpt,
        string by, double now, CancellationToken ct, string? anchor = null)
    {
        var req = FindRequest(ctx, lookup, team, reqId);
        if (req is null || FmText(req.Fm, "subject") != "link")
        {
            throw RequestNotFound(ctx, lookup, team, reqId, "link");
        }

        RequireOrchestrator(caller, team);
        if (req.Fm.Get("parent") is { IsText: true } parent
            && FindTask(ctx, lookup, team, parent.AsString()) is { } parentTask
            && Links.ExchangeClosedOnPurpose(core, parentTask.Exchange))
        {
            throw new MailboxError("task_closed", $"task {parent.AsString()} is already closed; a link granted now would close unused");
        }

        var t = TeamOf(lookup, team);
        var peers = Split(req.Fm.Get("peers"));
        var loop = IsLoopPair(lookup, FmText(req.Fm, "requester"), peers);
        var askedMessages = Num(messagesOpt, Num(req.Fm.Get("messages"), Limit("messages")));
        var askedMinutes = Num(minutesOpt, Num(req.Fm.Get("minutes"), AutoGrant.Minutes(links.Limits(), loop)));
        var linkId = IdOfLink(reqId);
        await RespondAsync(
            req, "granted", by, string.Empty,
            [new("link", linkId), new("messages", askedMessages), new("minutes", askedMinutes)], null, now, ct).ConfigureAwait(false);
        var requester = FmText(req.Fm, "requester");
        var task = req.Fm.Get("parent") is { IsText: true } p ? p.AsString() : Text(req.Fm.Get("parent"));
        var link = new JsonObject
        {
            ["id"] = linkId,
            ["task"] = req.Fm.Get("parent") ?? JsonValue.Null,
            ["task_home"] = req.Home,
            ["peers"] = Strings(peers),
            ["direction"] = FmText(req.Fm, "direction") == "out" ? "out" : "both",
            ["requester"] = req.Fm.Get("requester") ?? JsonValue.Null,
            ["granted_by"] = by,
            ["messages"] = askedMessages,
            ["minutes"] = askedMinutes,
        };
        if (loop)
        {
            link["rounds"] = AutoGrant.LoopRounds;
        }

        if (anchor is not null)
        {
            link["anchor"] = anchor;
        }

        var granted = await effects.LinksAsync(
            "grant", new JsonObject { ["dir"] = t.Dir is { } d ? d : JsonValue.Null, ["team"] = team, ["link"] = link, ["now"] = now }, $"grant-{reqId}", ct).ConfigureAwait(false);
        await AnnounceEndedAsync(lookup, team, granted.Get("ended"), ct).ConfigureAwait(false);
        var orch = HierarchyModule.OrchestratorAddress(team);
        var text = $"link {linkId} granted ({by}) for task {task}: {string.Join(" ↔ ", new[] { requester }.Concat(peers))}, {JsConvert.ToJsString(askedMessages)} messages / {JsConvert.ToJsString(askedMinutes)} minutes";
        await RecordNoteAsync(linkId, task, req.Home, orch, requester, RoleWord(req.Fm.Get("from")), text, ct).ConfigureAwait(false);
        await effects.NotifyAsync(
            new LinkNotice(
                requester,
                by == "ahd-auto" ? "ahd" : orch,
                "note",
                null,
                $"ah: link {linkId} granted for task {task} with {string.Join(", ", peers)}: {JsConvert.ToJsString(askedMessages)} messages or {JsConvert.ToJsString(askedMinutes)} minutes. Send to them with ah msg send; the Orchestrator reads the record."),
            ct).ConfigureAwait(false);
        if (by == "ahd-auto")
        {
            await effects.NotifyAsync(new LinkNotice(orch, "ahd", "event", "link-granted", $"{linkId} {requester} -> {string.Join(",", peers)} task {task} (auto)"), ct).ConfigureAwait(false);
        }

        return new JsonObject { ["link"] = link, ["request"] = req.Fm.Get("id") ?? JsonValue.Null };
    }

    private async Task<JsonObject> DenyRequestAsync(
        LinkContext ctx, Func<string, MeshTeam?> lookup, Caller caller, string team, string reqId, string why, double now, CancellationToken ct)
    {
        var req = FindRequest(ctx, lookup, team, reqId) ?? throw RequestNotFound(ctx, lookup, team, reqId, string.Empty);
        RequireOrchestrator(caller, team);
        var t = TeamOf(lookup, team);
        await RespondAsync(req, "denied", "orchestrator", why, [], null, now, ct).ConfigureAwait(false);
        var dir = t.Dir is { } d ? (JsonValue)d : JsonValue.Null;
        await effects.LinksAsync("deny", new JsonObject { ["dir"] = dir, ["team"] = team, ["request"] = reqId, ["why"] = why, ["now"] = now }, $"deny-{reqId}", ct).ConfigureAwait(false);
        var subject = FmText(req.Fm, "subject");
        if (subject == "extension")
        {
            await effects.LinksAsync(
                "pend", new JsonObject { ["dir"] = dir, ["team"] = team, ["id"] = req.Fm.Get("link") ?? JsonValue.Null, ["request"] = string.Empty }, $"unpend-{reqId}", ct).ConfigureAwait(false);
        }

        await effects.NotifyAsync(
            new LinkNotice(
                FmText(req.Fm, "requester"), HierarchyModule.OrchestratorAddress(team), "note", null,
                $"ah: your {subject} request {reqId} was denied{(why.Length > 0 ? $": {OneLine(why)}" : string.Empty)}"),
            ct).ConfigureAwait(false);
        return new JsonObject { ["request"] = reqId };
    }

    private async Task<JsonObject> RevokeLinkAsync(Func<string, MeshTeam?> lookup, Caller caller, string team, string id, double now, CancellationToken ct)
    {
        RequireOrchestrator(caller, team);
        var t = TeamOf(lookup, team);
        var dir = t.Dir is { } d ? (JsonValue)d : JsonValue.Null;
        var r = await effects.LinksAsync("revoke", new JsonObject { ["dir"] = dir, ["team"] = team, ["id"] = id, ["reason"] = "revoked", ["now"] = now }, $"revoke-{id}", ct).ConfigureAwait(false);
        await AnnounceEndedAsync(lookup, team, r.Get("ended"), ct).ConfigureAwait(false);
        if (r.Get("revoked") is { IsTruthy: true })
        {
            var one = new JsonArray { new JsonArray { id, "revoked", JsonValue.Null } };
            await AnnounceEndedAsync(lookup, team, one, ct).ConfigureAwait(false);
        }

        return r;
    }

    // ---------------------------------------------------------------- extensions

    private async Task<JsonObject> ExtensionRequestAsync(
        LinkContext ctx, Func<string, MeshTeam?> lookup, Caller caller, string id, string? messagesOpt, string? minutesOpt, string why, double now, CancellationToken ct)
    {
        if (caller.Kind != "member")
        {
            throw new MailboxError("not_a_peer", "extend-request is a peer of the link's");
        }

        var team = caller.Team!;
        var found = FindLink(lookup, team, id) ?? throw new MailboxError("unknown_link", $"no link {id} on team {team}");
        var link = found.Link;
        var on = new[] { Text(link.Member("requester")) }.Concat(PeersOf(link)).ToList();
        if (!on.Contains(caller.Address, StringComparer.Ordinal))
        {
            throw new MailboxError("not_a_peer", $"{caller.Address} is not on link {id}");
        }

        var endedAt = ParseDate(Text(link.Member("ended_at") is { IsTruthy: true } e ? e : (JsonValue?)""));
        var state = Text(link.Member("state"));
        var reason = Text(link.Member("end_reason"));
        var extendable = state == "active"
            || (state == "expired" && (reason == "message_cap" || reason == "time") && now - endedAt <= Limit("extendGraceMs"));
        if (!extendable)
        {
            var shown = link.Member("end_reason") is { IsTruthy: true } r ? Text(r) : state;
            throw new MailboxError("not_extendable", $"link {id} ended ({shown}); ask the Orchestrator for a new link");
        }

        if (link.Member("pending_extension") is { IsTruthy: true } pendingId)
        {
            var pending = FindRequest(ctx, lookup, team, Text(pendingId));
            if (pending is not null && !Decided(pending))
            {
                return new JsonObject { ["id"] = pendingId, ["existing"] = true };
            }
        }

        var askedMessages = Num(messagesOpt, Limit("messages"));
        var askedMinutes = Num(minutesOpt, Limit("minutes"));
        var home = link.Member("task_home") is { IsTruthy: true } th ? Text(th) : found.Dir;
        var extensions = link.Member("extensions") is { IsTruthy: true } x ? x : (JsonValue)0;
        var task = link.Member("task") is { IsText: true } tk ? tk.AsString() : null;
        var req = await OpenRequestAsync(
            "extension", team, home, task, caller.Address, caller.Role, caller.Name, why,
            [new("link", Text(link.Member("id"))), new("messages", askedMessages), new("minutes", askedMinutes), new("extensions", extensions)], now, ct).ConfigureAwait(false);
        await effects.LinksAsync(
            "pend", new JsonObject { ["dir"] = found.Dir, ["team"] = team, ["id"] = link.Member("id") ?? JsonValue.Null, ["request"] = req.Id }, $"pend-{req.Id}", ct).ConfigureAwait(false);
        var body = new JsonObject
        {
            ["request"] = req.Id,
            ["link"] = link.Member("id") ?? JsonValue.Null,
            ["from"] = caller.Address,
            ["messages"] = askedMessages,
            ["minutes"] = askedMinutes,
            ["extensions_so_far"] = extensions,
            ["why"] = OneLine(why, 200),
            ["path"] = req.Path,
        };
        await effects.NotifyAsync(new LinkNotice(HierarchyModule.OrchestratorAddress(team), caller.Address, "event", "link-extension", Json(body)), ct).ConfigureAwait(false);
        return new JsonObject { ["id"] = req.Id, ["path"] = req.Path };
    }

    internal static double ParseDate(string text) =>
        DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
            ? at.ToUnixTimeMilliseconds()
            : double.NaN;

    private static IEnumerable<string> PeersOf(JsonValue link) =>
        link.Member("peers") is { IsArray: true } p ? p.AsArray().Select(x => Text(x)) : [];

    private async Task<JsonObject> GrantExtensionAsync(
        LinkContext ctx, Func<string, MeshTeam?> lookup, Caller caller, string team, string reqId, string? messagesOpt, string? minutesOpt, double now, CancellationToken ct)
    {
        var req = FindRequest(ctx, lookup, team, reqId);
        if (req is null || FmText(req.Fm, "subject") != "extension")
        {
            throw RequestNotFound(ctx, lookup, team, reqId, "extension");
        }

        RequireOrchestrator(caller, team);
        var t = TeamOf(lookup, team);
        var addMessages = Num(messagesOpt, Num(req.Fm.Get("messages"), Limit("messages")));
        var addMinutes = Num(minutesOpt, Num(req.Fm.Get("minutes"), Limit("minutes")));
        var linkId = req.Fm.Get("link") ?? JsonValue.Null;
        await RespondAsync(
            req, "extended", "orchestrator", string.Empty,
            [new("link", linkId), new("messages", addMessages), new("minutes", addMinutes)], null, now, ct).ConfigureAwait(false);
        var r = await effects.LinksAsync(
            "extend",
            new JsonObject { ["dir"] = t.Dir is { } d ? d : JsonValue.Null, ["team"] = team, ["id"] = linkId, ["messages"] = addMessages, ["minutes"] = addMinutes, ["now"] = now },
            $"extend-{reqId}", ct).ConfigureAwait(false);
        await AnnounceEndedAsync(lookup, team, r.Get("ended"), ct).ConfigureAwait(false);
        if (r.Get("refused") is { IsTruthy: true } refused)
        {
            throw new MailboxError("not_extendable", $"link {Text(linkId)} ended ({Text(refused)}) and cannot be extended");
        }

        await effects.NotifyAsync(
            new LinkNotice(
                FmText(req.Fm, "requester"), HierarchyModule.OrchestratorAddress(team), "note", null,
                $"ah: link {Text(linkId)} extended by {Text(addMessages)} messages and {Text(addMinutes)} minutes"),
            ct).ConfigureAwait(false);
        return new JsonObject { ["link"] = linkId, ["messages"] = addMessages, ["minutes"] = addMinutes };
    }

    // ---------------------------------------------------------------- auto-grant (the daemon, no LLM turn)

    private (bool Ok, string? Why) AutoGrantable(LinkContext ctx, Func<string, MeshTeam?> lookup, string team, FoundRequest req, double now)
    {
        var t = lookup(team);
        if (t is null || FmText(req.Fm, "subject") != "link")
        {
            return (false, "not a link request");
        }

        if (Decided(req))
        {
            return (false, "already decided");
        }

        var requester = Text(req.Fm.Get("requester"));
        var people = new[] { requester }.Concat(Split(req.Fm.Get("peers"))).Select(a => rule.Classify(a, lookup)).ToList();
        var task = req.Fm.Get("parent") is { IsText: true } parent ? FindTask(ctx, lookup, team, parent.AsString()) : null;
        var facts = new AutoGrantFacts(
            people,
            team,
            AutoGrant.OpenTaskFm(core, task is null ? null : (task.Dir, task.Exchange)),
            rule.SettingsOf(t.Data).AutoGrant,
            Num(req.Fm.Get("messages"), 0),
            Num(req.Fm.Get("minutes"), 0),
            t.Dir is { } dir ? links.ActiveCount(dir, team, requester, now) : null);
        return AutoGrant.Judge(facts, links.Limits());
    }

    /// <summary>
    /// The review-loop or ruling link inside a pipeline run: the reviewer or architect asked the implementer for a link on its request, the
    /// Orchestrator holds a surrogate grant, and the pair are the newest addressees of the review (or design) and implement requests of that slug. All hold:
    /// the link is run-scoped (carries the anchor). Any condition missing: no run scope, and the ordinary rules decide as before. A link
    /// between the pair already exists: no second one.
    /// </summary>
    private (string? Anchor, string? Block) RunBranch(Func<string, MeshTeam?> lookup, string team, FoundRequest req, double now)
    {
        var requester = Text(req.Fm.Get("requester"));
        var peers = Split(req.Fm.Get("peers"));
        if (runFacts is null || peers.Count != 1 || lookup(team)?.Dir is not { } dir
            || req.Fm.Get("parent") is not { IsText: true } parent
            || runFacts.Anchor(dir, team) is not { Surrogate: true } anchor
            || runFacts.SlugOf(dir, team, parent.AsString()) is not { } slug)
        {
            return (null, null);
        }

        var from = rule.Classify(requester, lookup).Name;
        var to = rule.Classify(peers[0], lookup).Name;
        if (from is null || to is null || from == to
            || (runFacts.NewestRequestTo(dir, team, slug, "review") != from && runFacts.NewestRequestTo(dir, team, slug, "design") != from)
            || runFacts.NewestRequestTo(dir, team, slug, "implement") != to)
        {
            return (null, null);
        }

        var blocks = links.ActiveCovering(dir, team, JsonValue.FromString(requester), JsonValue.FromString(peers[0]), now)
            // the pair's assignment link is no review loop: it never stands in for the run-scoped one
            .Any(l => l.AsObject().Get("assigned") is not { IsText: true } && (l.AsObject().Get("task") is not { IsText: true } t || t.AsString() == parent.AsString()));
        return blocks
            ? (null, "a link between the pair already exists")
            : (anchor.Id, null);
    }

    public async Task<int> RevokeDeadRunLinksAsync(LinkContext context, Func<string, MeshTeam?> lookup, string team, CancellationToken cancellation, double? nowMs = null)
    {
        var now = NowOr(nowMs);
        if (lookup(team)?.Dir is not { } dir)
        {
            return 0;
        }

        var read = await effects.LinksAsync("read", new JsonObject { ["dir"] = dir, ["team"] = team, ["now"] = now }, null, cancellation).ConfigureAwait(false);
        var revoked = 0;
        foreach (var item in read.Get("links") is { IsArray: true } rows ? rows.AsArray() : new JsonArray())
        {
            if (item.Member("state") is not { IsText: true } state || state.AsString() != "active" || item.Member("id") is not { IsText: true } id)
            {
                continue;
            }

            // a run-scoped link dies with its run; an assignment link when its members are no longer assigned together
            string? reason = null;
            if (item.Member("anchor") is { IsText: true })
            {
                reason = runFacts is null || RunLinks.Live(runFacts, rule, lookup, dir, team, item.AsObject()) ? null : "run_ended";
            }
            else if (item.Member("assigned") is { IsText: true })
            {
                reason = AssignedTogether(lookup, dir, team, item) ? null : "assignment_ended";
            }

            if (reason is null)
            {
                continue;
            }

            var r = await effects.LinksAsync("revoke", new JsonObject { ["dir"] = dir, ["team"] = team, ["id"] = id, ["reason"] = reason, ["now"] = now }, $"revoke-{id.AsString()}", cancellation).ConfigureAwait(false);
            await AnnounceEndedAsync(lookup, team, r.Get("ended"), cancellation).ConfigureAwait(false);
            revoked += r.Get("revoked") is { IsTruthy: true } ? 1 : 0;
        }

        return revoked;
    }

    /// <summary>Whether the two members of an assignment link still share an assignment (<see cref="Assignments.Shared"/>, the send rule's own answer).</summary>
    private bool AssignedTogether(Func<string, MeshTeam?> lookup, string dir, string team, JsonValue link)
    {
        var on = new[] { Text(link.Member("requester")) }.Concat(PeersOf(link)).Select(a => rule.Classify(a, lookup)).ToList();
        return on.Count == 2 && on.All(p => p.Kind == "member" && p.Team == team && p.Role != "ultra-advisor")
            && Assignments.Shared(messages, core, lookup, dir, team, on[0].Name, on[1].Name) is not null;
    }

    public async Task<AutoGrantResult> AutoGrantAsync(
        LinkContext context, Func<string, MeshTeam?> lookup, string team, string requestId, CancellationToken cancellation, double? nowMs = null)
    {
        var now = NowOr(nowMs);
        var req = FindRequest(context, lookup, team, requestId);
        if (req is null)
        {
            return new AutoGrantResult(false, "unknown request");
        }

        var verdict = AutoGrantable(context, lookup, team, req, now);
        if (!verdict.Ok)
        {
            return new AutoGrantResult(false, verdict.Why);
        }

        return await GrantNowAsync(context, lookup, team, requestId, req, now, cancellation).ConfigureAwait(false);
    }

    /// <summary>Grants a found request with no approval conditions (ah-simplify F1); run scope and the denial of a closed task still apply.</summary>
    private async Task<AutoGrantResult> GrantNowAsync(
        LinkContext context, Func<string, MeshTeam?> lookup, string team, string requestId, FoundRequest req, double now, CancellationToken cancellation)
    {
        var run = RunBranch(lookup, team, req, now);
        if (run.Block is not null)
        {
            return new AutoGrantResult(false, run.Block);
        }

        try
        {
            await GrantLinkAsync(context, lookup, new Caller("orchestrator", string.Empty, team), team, requestId, null, null, "ahd-auto", now, cancellation, run.Anchor).ConfigureAwait(false);
            return new AutoGrantResult(true);
        }
        catch (MailboxError e) when (e.Code == "already_decided")
        {
            return new AutoGrantResult(false, "already decided");
        }
        catch (MailboxError e) when (e.Code == "task_closed")
        {
            try
            {
                await DenyRequestAsync(context, lookup, new Caller("orchestrator", string.Empty, team), team, requestId, "task_closed", now, cancellation).ConfigureAwait(false);
            }
            catch (MailboxError)
            {
                // decided meanwhile, or the denial could not be written: the requester is no worse off than before
            }

            return new AutoGrantResult(false, "task closed");
        }
    }

    // ---------------------------------------------------------------- reading

    private async Task<(JsonArray Links, JsonValue Ended)> ListLinksAsync(Func<string, MeshTeam?> lookup, string team, double now, CancellationToken ct)
    {
        var t = lookup(team) ?? throw new MailboxError("unknown_address", $"no team {team}");
        var dir = t.Dir is { } d ? (JsonValue)d : JsonValue.Null;
        var swept = await effects.LinksAsync("sweep", new JsonObject { ["dir"] = dir, ["team"] = team, ["now"] = now }, $"sweep-{NewId()}", ct).ConfigureAwait(false);
        await AnnounceEndedAsync(lookup, team, swept.Get("ended"), ct).ConfigureAwait(false);
        var read = await effects.LinksAsync("read", new JsonObject { ["dir"] = dir, ["team"] = team, ["now"] = now }, null, ct).ConfigureAwait(false);
        return (read.Get("links") is { IsArray: true } l ? l.AsArray() : new JsonArray(), swept.Get("ended") ?? JsonValue.Null);
    }

    /// <summary>One <c>link-ended</c> event per link that just ended; a failed delivery is skipped.</summary>
    private async Task AnnounceEndedAsync(Func<string, MeshTeam?> lookup, string team, JsonValue? ended, CancellationToken ct)
    {
        if (ended is not { IsArray: true } list)
        {
            return;
        }

        var dir = lookup(team)?.Dir;

        foreach (var item in list.AsArray())
        {
            if (!item.IsArray)
            {
                continue;
            }

            var row = item.AsArray();
            var cell = (int i) => i < row.Count ? Text(row[i]) : "undefined";
            try
            {
                await effects.NotifyAsync(new LinkNotice(HierarchyModule.OrchestratorAddress(team), "ahd", "event", "link-ended", $"{cell(0)} {cell(1)} used={cell(2)}"), ct).ConfigureAwait(false);
                if (dir is not null && links.LoopClosedText(dir, team, item) is { } closed)
                {
                    await effects.NotifyAsync(new LinkNotice(HierarchyModule.OrchestratorAddress(team), "ahd", "note", null, closed), ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Announcing an ended link is best effort.
            }
        }
    }

    private sealed record LinkNote(int N, string From, string To, string At, string Text, string File);

    /// <summary>The mirrored messages of a link, oldest first, numbered from 1.</summary>
    private List<LinkNote> LinkMessages(JsonValue link, string dir)
    {
        var home = link.Member("task_home") is { IsTruthy: true } th ? Text(th) : dir;
        IReadOnlyList<string> paths;
        try
        {
            paths = [.. messages.ListMessages(home, includeArchive: false, ["link-note"]).Select(m => Text(m.Member("path")))];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var linkId = Text(link.Member("id"));
        var notes = new List<(string Path, JsonObject Fm, string Body)>();
        foreach (var path in paths)
        {
            var m = core.ReadMsgFile(path);
            if (m.IsNull || m.Member("fm") is not { IsMap: true } fm)
            {
                continue;
            }

            var f = fm.AsObject();
            if (f.Get("link") is { IsText: true } l && l.AsString() == linkId && !(f.Get("n") is { IsText: true } n && n.AsString() == "0"))
            {
                notes.Add((path, f, Text(m.Member("text"))));
            }
        }

        return notes
            .OrderBy(x => Text(x.Fm.Get("created")), StringComparer.Ordinal)
            .ThenBy(x => { var n = JsConvert.ToNumber(x.Fm.Get("n")); return double.IsNaN(n) ? 0 : n; })
            .ThenBy(x => Text(x.Fm.Get("id")), StringComparer.Ordinal)
            .Select((x, i) => new LinkNote(
                i + 1,
                Text(x.Fm.Get("from")),
                Text(x.Fm.Get("to_address")),
                Text(x.Fm.Get("created")),
                JsText.Trim(string.Join("---", Fence().Split(x.Body).Skip(2))),
                x.Path))
            .ToList();
    }

    // ---------------------------------------------------------------- the CLI verbs

    private List<string> TeamsFor(IReadOnlyList<string> addresses, Func<string, MeshTeam?> lookup, IReadOnlyDictionary<string, string> opts)
    {
        if (opts.TryGetValue("team", out var named) && named.Length > 0)
        {
            return [named];
        }

        var own = addresses.Select(a => rule.Classify(a, lookup)).Where(c => c.Kind is "member" or "orchestrator" && c.Team is not null)
            .Select(c => c.Team!).Distinct(StringComparer.Ordinal).ToList();
        return own.Count == 0 ? throw new MailboxError("bad_request", "cannot tell which team this is for; pass --team <name>") : own;
    }

    private static string LinkLine(JsonValue l)
    {
        var limits = l.Member("limits");
        var reason = l.Member("end_reason") is { IsTruthy: true } r ? $"({Text(r)})" : string.Empty;
        var arrow = l.Member("direction") is { IsText: true } d && d.AsString() == "out" ? "->" : "<->";
        var pending = l.Member("pending_extension") is { IsTruthy: true } p ? $"  extension pending {Text(p)}" : string.Empty;
        var assigned = l.Member("assigned") is { IsText: true } key ? $"  opened by assignment ({key.AsString()})" : string.Empty;
        return $"{Text(l.Member("id"))}  {Text(l.Member("state"))}{reason}  task {Text(l.Member("task"))}  {Text(l.Member("requester"))} {arrow} {string.Join(",", PeersOf(l))}  used {Text(l.Member("used"))}/{Text(limits?.Member("messages"))}  {Text(limits?.Member("minutes"))} min{pending}{assigned}";
    }

    private static string? Opt(IReadOnlyDictionary<string, string> opts, string name) => opts.TryGetValue(name, out var v) ? v : null;

    public async Task<MailboxOutcome<LinkCommandResult>> RunAsync(LinkCommandRequest request, CancellationToken cancellation)
    {
        try
        {
            return new MailboxOutcome<LinkCommandResult>(await RunCoreAsync(request, cancellation).ConfigureAwait(false), null);
        }
        catch (MailboxError e)
        {
            return new MailboxOutcome<LinkCommandResult>(null, e.ToFailure());
        }
    }

    private async Task<LinkCommandResult> RunCoreAsync(LinkCommandRequest request, CancellationToken ct)
    {
        var lookup = request.Lookup;
        var ctx = request.Context;
        var opts = request.Options;
        var now = NowOr(request.NowMs);
        var caller = CallerOf(request.Addresses, lookup);
        var sub = request.Command == "link-request" ? "request" : request.Positional.ElementAtOrDefault(0);
        var arg = request.Command == "link-request" ? null : request.Positional.ElementAtOrDefault(1);
        var messagesOpt = Opt(opts, "messages");
        var minutesOpt = Opt(opts, "minutes");
        if (sub == "request")
        {
            var r = await LinkRequestAsync(ctx, lookup, caller, Opt(opts, "to"), Opt(opts, "task"), Opt(opts, "why"), Opt(opts, "direction"), messagesOpt, minutesOpt, now, ct).ConfigureAwait(false);
            return new LinkCommandResult(r, Text(r["id"]));
        }

        if (sub is null || !Subs.Contains(sub, StringComparer.Ordinal))
        {
            throw new MailboxError("bad_request", "usage: ah msg link grant|deny|revoke|extend-request|extend|list|show|post ...");
        }

        var teams = TeamsFor(request.Addresses, lookup, opts);
        if (sub == "list")
        {
            RequireOrchestrator(caller);
            // A command line run reaches only the context's own hierarchy directories, so a team kept in another checkout cannot be read from here.
            bool InContext(string t) => lookup(t)?.Dir is { } dir && (SameDir(dir, ctx.HierarchyDir) || SameDir(dir, ctx.MainHierarchyDir));
            if (!opts.ContainsKey("team"))
            {
                teams = teams.Where(InContext).ToList();
            }
            else if (teams.FirstOrDefault(t => !InContext(t)) is { } outside)
            {
                var kept = lookup(outside)?.Dir;
                throw new MailboxError("bad_request", kept is null
                    ? $"team {outside} was not found in this --cwd's hierarchy"
                    : $"team {outside} lives in {kept}, outside this --cwd's hierarchy; re-run with --cwd {WorkTreeOf(kept)}");
            }

            var lines = new List<string>();
            var all = new JsonArray();
            foreach (var team in teams)
            {
                var (found, _) = await ListLinksAsync(lookup, team, now, ct).ConfigureAwait(false);
                foreach (var l in found)
                {
                    var row = new JsonObject { ["team"] = team };
                    if (l.IsMap)
                    {
                        foreach (var key in l.AsObject().Keys)
                        {
                            row[key] = l.AsObject()[key];
                        }
                    }

                    all.Add(row);
                    lines.Add(LinkLine(l));
                }
            }

            return new LinkCommandResult(new JsonObject { ["links"] = all }, lines.Count == 0 ? "no links" : string.Join('\n', lines));
        }

        if (string.IsNullOrEmpty(arg))
        {
            throw new MailboxError("bad_request", $"link {sub} needs an id");
        }

        if (sub is "grant" or "deny" or "extend")
        {
            var team = teams.FirstOrDefault(t => FindRequest(ctx, lookup, t, arg) is not null) ?? teams[0];
            caller = CallerOf(request.Addresses, lookup, team);
            if (sub == "grant")
            {
                var r = await GrantLinkAsync(ctx, lookup, caller, team, arg, messagesOpt, minutesOpt, caller.Kind == "user" ? "user" : "orchestrator", now, ct).ConfigureAwait(false);
                return new LinkCommandResult(r, $"granted {Text(r["link"].AsObject()["id"])}");
            }

            if (sub == "deny")
            {
                if (string.IsNullOrEmpty(Opt(opts, "why")))
                {
                    throw new MailboxError("bad_request", "link deny needs --why <text>");
                }

                var r = await DenyRequestAsync(ctx, lookup, caller, team, arg, Opt(opts, "why")!, now, ct).ConfigureAwait(false);
                return new LinkCommandResult(r, $"denied {arg}");
            }

            var ext = await GrantExtensionAsync(ctx, lookup, caller, team, arg, messagesOpt, minutesOpt, now, ct).ConfigureAwait(false);
            return new LinkCommandResult(ext, $"extended {Text(ext["link"])} by {Text(ext["messages"])} messages and {Text(ext["minutes"])} minutes");
        }

        var linkTeam = teams.FirstOrDefault(t => FindLink(lookup, t, arg) is not null) ?? teams[0];
        caller = CallerOf(request.Addresses, lookup, linkTeam);
        if (sub == "revoke")
        {
            var r = await RevokeLinkAsync(lookup, caller, linkTeam, arg, now, ct).ConfigureAwait(false);
            return new LinkCommandResult(r, r.Get("revoked") is { IsTruthy: true } ? $"revoked {arg}" : $"{arg} is not active");
        }

        if (sub == "extend-request")
        {
            if (string.IsNullOrEmpty(Opt(opts, "why")))
            {
                throw new MailboxError("bad_request", "link extend-request needs --why <text>");
            }

            var r = await ExtensionRequestAsync(ctx, lookup, caller, arg, messagesOpt, minutesOpt, Opt(opts, "why")!, now, ct).ConfigureAwait(false);
            return new LinkCommandResult(r, r.Get("existing") is { IsTruthy: true } ? $"{Text(r["id"])} (already pending)" : Text(r["id"]));
        }

        var found2 = FindLink(lookup, linkTeam, arg) ?? throw new MailboxError("unknown_link", $"no link {arg} on team {linkTeam}");
        if (sub == "post")
        {
            return await PostAsync(caller, linkTeam, arg, found2, Opt(opts, "text"), ct).ConfigureAwait(false);
        }

        return await ShowAsync(caller, found2, opts, ct).ConfigureAwait(false);
    }

    private async Task<LinkCommandResult> PostAsync(Caller caller, string team, string linkId, FoundLink found, string? text, CancellationToken ct)
    {
        RequireOrchestrator(caller, team);
        if (string.IsNullOrEmpty(text))
        {
            throw new MailboxError("bad_request", "link post needs --text <message>");
        }

        var to = new[] { Text(found.Link.Member("requester")) }.Concat(PeersOf(found.Link)).ToList();
        var from = caller.Address == "user" ? HierarchyModule.OrchestratorAddress(team) : caller.Address;
        var home = found.Link.Member("task_home") is { IsTruthy: true } th ? Text(th) : found.Dir;
        try
        {
            await mirror.MirrorAsync(
                new LinkMirror(Text(found.Link.Member("id")), Text(found.Link.Member("task")), home, from, string.Join(',', to), "member", null, NewId(), text), ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new MailboxError("mirror_failed", $"the post was not delivered: its record could not be written ({e.Message})");
        }

        foreach (var address in to)
        {
            await effects.NotifyAsync(new LinkNotice(address, from, "note", null, text, linkId), ct).ConfigureAwait(false);
        }

        return new LinkCommandResult(
            new JsonObject { ["link"] = linkId, ["delivered"] = Strings(to) },
            $"posted to {string.Join(", ", to)}");
    }

    private async Task<LinkCommandResult> ShowAsync(Caller caller, FoundLink found, IReadOnlyDictionary<string, string> opts, CancellationToken ct)
    {
        var onLink = new[] { Text(found.Link.Member("requester")) }.Concat(PeersOf(found.Link)).ToList();
        if (caller.Kind is not ("user" or "orchestrator") && !onLink.Contains(caller.Address, StringComparer.Ordinal))
        {
            throw new MailboxError("orchestrator_only", "link show is for the Orchestrator and the link's peers");
        }

        var sinceRaw = JsConvert.ToNumber(JsonValue.FromString(Opt(opts, "since") ?? "undefined"));
        var since = double.IsFinite(sinceRaw) ? sinceRaw : 0;
        var waitRaw = JsConvert.ToNumber(JsonValue.FromString(Opt(opts, "wait") ?? "undefined"));
        var wait = Math.Min(25, Math.Max(0, double.IsNaN(waitRaw) || waitRaw == 0 ? 0 : waitRaw));
        var deadline = Environment.TickCount64 + (long)(wait * 1000);
        var msgs = LinkMessages(found.Link, found.Dir);
        while (wait > 0 && !msgs.Any(m => m.N > since) && Environment.TickCount64 < deadline)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            msgs = LinkMessages(found.Link, found.Dir);
        }

        var shown = msgs.Where(m => m.N > since).ToList();
        if (Opt(opts, "tail") is { Length: > 0 } tailText)
        {
            var tail = JsConvert.ToNumber(JsonValue.FromString(tailText));
            if (tail > 0)
            {
                var keep = double.IsInfinity(tail) ? shown.Count : (int)Math.Min(Math.Truncate(tail), shown.Count);
                if (keep > 0)
                {
                    shown = shown.Skip(shown.Count - keep).ToList();
                }
            }
        }

        var rows = new JsonArray();
        foreach (var m in shown)
        {
            rows.Add(new JsonObject { ["n"] = m.N, ["from"] = m.From, ["to"] = m.To, ["at"] = m.At, ["text"] = m.Text, ["file"] = m.File });
        }

        var text = string.Join('\n', new[] { LinkLine(found.Link) }.Concat(shown.Select(m => $"#{m.N}  {m.From} -> {m.To}  {m.At}\n{m.Text}")));
        return new LinkCommandResult(new JsonObject { ["link"] = found.Link, ["messages"] = rows }, text);
    }
}
