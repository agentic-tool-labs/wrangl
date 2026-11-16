using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Activity;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Gates;

/// <summary>
/// A bare SendMessage between the team's Orchestrator and a member becomes a real message-file exchange, filed from the sender's own
/// session: a task-sized Orchestrator message to a live member is a request, and a member's reply to an open request of the Orchestrator
/// is its response. Identity (sender, role, team) comes from the hook, never from the text. Nothing here ever blocks a send: a
/// failure leaves the message as it was and logs one line.
/// </summary>
internal static partial class AutoTrack
{
    /// <summary>
    /// Whether the PreToolUse hook rewrites the message to carry the file pointer and the entry marker: the last <c>auto_track.rewrite</c>
    /// boolean among the config layers, true when none sets one. With it off the send goes out as typed and is tracked without a rewrite.
    /// </summary>
    internal static bool RewriteOn(JsonObject resolved)
    {
        var on = true;
        if (resolved.Get("layers") is { IsArray: true } layers)
        {
            foreach (var layer in layers.AsArray())
            {
                if (layer.Member("data")?.Member("auto_track")?.Member("rewrite") is { IsBool: true } flag)
                {
                    on = flag.AsBool();
                }
            }
        }

        return on;
    }

    internal static readonly TimeSpan DedupWindow = TimeSpan.FromMinutes(10);

    /// <summary>How long the hook waits for a mailbox's append lock when it files an entry, so a held lock cannot stall the call.</summary>
    internal static readonly TimeSpan SendLockWait = TimeSpan.FromMilliseconds(500);

    private const int SlugMax = 32;
    private const string ReservedSlug = "pipeline-run-anchor";
    private const string RowType = "auto-track";

    private static readonly HashSet<string> AckWords = new(StringComparer.OrdinalIgnoreCase) { "ok", "ack", "thanks", "thx", "ty", "got it", "yes", "no", "sure", "noted", "will do", "👍", "ping", "pong", "hello", "hi", "hey" };

    /// <summary>What a classified send is filed as. A <see cref="RequestPath"/> makes it a response to that request.</summary>
    internal sealed record Plan(string Role, string ToName, string? Team, string? RequestPath, string? RequestId, JsonObject Resolved)
    {
        /// <summary>For an Orchestrator send that names an open request file already written for this member: that file, reused instead of filing a new request.</summary>
        public string? ExistingRequest { get; init; }

        /// <summary>Whether the member is live now: it decides only whether <c>notify_when_idle</c> is added.</summary>
        public bool Live { get; init; }
    }

    /// <summary>The filing a SendMessage calls for, or null when it stays bare. <paramref name="context"/> is advice for the sender when a reply could not be paired.</summary>
    internal static Plan? Classify(GateContext c, out string? context)
    {
        try
        {
            return ClassifyCore(c, out context);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            c.LogError(e);
            context = null;
            return null;
        }
    }

    private static Plan? ClassifyCore(GateContext c, out string? context)
    {
        context = null;
        var s = c.Services;
        var config = s.Config;
        if (c.ToolName() != "SendMessage" || config.IsSubagent(c.Input))
        {
            return null;
        }

        var input = c.ToolInput();
        var text = input.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
        var to = input.Get("to") is { IsText: true } t ? s.Hierarchy.Peers.StripRef(JsText.Trim(t.AsString())) : string.Empty;
        if (to.Length == 0 || to == "main" || text.Contains("[hierarchy-msg ", StringComparison.Ordinal) || text.Contains("[ah-entry ", StringComparison.Ordinal))
        {
            return null;
        }

        var cwd = c.Cwd();
        var sessionKey = c.SessionKey();
        var resolved = config.ResolveConfig(cwd, new ResolveOptions(SessionId: sessionKey != "__nosession__" ? sessionKey : null));
        if (resolved.Get("enabled") is not { IsTruthy: true } || (resolved.Get("msgs") is { IsText: true } off && off.AsString() == "off"))
        {
            return null;
        }

        var team = resolved.Get("team") is { IsText: true } teamName ? teamName.AsString() : null;
        var caller = config.ResolveHierarchyRole(c.Input);
        if (caller.Direct && caller.Role is { } mine)
        {
            return ClassifyReply(c, to, text, mine, team, resolved, out context);
        }

        // Only the session that owns a team (the config's own process proof) files as its Orchestrator; liveness only decides notify_when_idle.
        var owns = resolved.Get("ownedTeams") is { IsArray: true } owned && owned.AsArray().Count > 0;
        if (!owns || IsAck(text) || MemberNamed(c, to) is not { } member
            || TeamMemberRole(c, new JsonObject { ["to"] = member }, resolved) is not { } role)
        {
            return null;
        }

        var live = s.Hierarchy.Core.Liveness?.ForMember(member, team ?? string.Empty) is { Live: true };
        return new Plan(role, member, team, null, null, resolved) { Live = live, ExistingRequest = ExistingBrief(c, text, role, member, team) };
    }

    private const string DefaultSocketDirectory = "/tmp/cc-socks";

    /// <summary>The team member name a SendMessage recipient denotes: a bare name, <c>name@team</c>, or the socket of a registered member's session. Null when none.</summary>
    internal static string? MemberNamed(GateContext c, string to)
    {
        if (!to.StartsWith("uds:", StringComparison.Ordinal))
        {
            return to.IndexOf('@', StringComparison.Ordinal) is var at and > 0 ? to[..at] : to;
        }

        if (c.Services.Advisory is not { } advisory)
        {
            return null;
        }

        var dir = advisory.Callers.RequestContext(c.Cwd()).SocketDirectory;
        dir = string.IsNullOrEmpty(dir) ? DefaultSocketDirectory : dir;
        foreach (var registration in advisory.Mailbox.LiveRegistrations())
        {
            if (registration.Get("pid") is { IsNumber: true } pid && $"uds:{dir.TrimEnd('/')}/{(long)pid.AsNumber()}.sock" == to
                && registration.Get("addresses") is { IsArray: true } addresses
                && addresses.AsArray().FirstOrDefault(a => a.IsText && a.AsString().Contains('@', StringComparison.Ordinal)) is { } address)
            {
                return address.AsString()[..address.AsString().IndexOf('@', StringComparison.Ordinal)];
            }
        }

        return null;
    }

    /// <summary>The open request file this Orchestrator text names, when it is a request of this team to this member; else null.</summary>
    private static string? ExistingBrief(GateContext c, string text, string role, string member, string? team)
    {
        var s = c.Services;
        foreach (var path in NamedPaths(c, RequestPathRe().Matches(text)))
        {
            if (!s.Hierarchy.Core.MsgExists(path) || s.Hierarchy.Core.ParseMsgFilename(path) is not { Type: "request" } meta
                || s.Hierarchy.Core.ReadMsgFile(path).Member("fm") is not { IsMap: true } fm
                || fm.Text("from") != "orchestrator" || fm.Text("to") != role || (fm.Text("team") ?? string.Empty) != (team ?? string.Empty)
                || (fm.Text("to_name") is { Length: > 0 } toName && toName != "null" && toName != member))
            {
                continue;
            }

            if (s.Hierarchy.Messages.OpenExchanges(s.Config.HierarchyDir(c.Cwd()), null).Any(e => e.Member("id") is { IsText: true } id && id.AsString() == meta.Id))
            {
                return path;
            }
        }

        return null;
    }

    [GeneratedRegex("[^" + JsText.SpaceClass + "\"'<>\\[\\]()]*--request\\.md", RegexOptions.CultureInvariant)]
    private static partial Regex RequestPathRe();

    [GeneratedRegex("[^" + JsText.SpaceClass + "\"'<>\\[\\]()]*--response\\.md", RegexOptions.CultureInvariant)]
    private static partial Regex ResponsePathRe();

    /// <summary>The message-file paths <paramref name="matches"/> name, a relative one resolved against the call's working directory.</summary>
    private static IEnumerable<string> NamedPaths(GateContext c, MatchCollection matches) =>
        matches.Select(m => m.Value).Select(v => v.StartsWith('/') ? v : Path.GetFullPath(v, c.Cwd()));

    /// <summary>A send that names an exchange file with no pointer or entry marker: <c>bare-brief</c> or <c>bare-report</c>, the file named and the address its entry would go to.</summary>
    internal readonly record struct BareNamed(string Reason, string Path, string Recipient);

    /// <summary>
    /// Whether this SendMessage is a brief or a report in all but the mailbox entry: an Orchestrator owning the request's team names an open
    /// request of that team addressed to the recipient (bare-brief), or a member owing a request names the response file answering it
    /// (bare-report). Subagents and sends that carry a pointer or an entry marker are none. Null otherwise.
    /// </summary>
    internal static BareNamed? BareNamedBy(GateContext c)
    {
        try
        {
            var s = c.Services;
            var config = s.Config;
            if (c.ToolName() != "SendMessage" || config.IsSubagent(c.Input))
            {
                return null;
            }

            var input = c.ToolInput();
            var text = input.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
            var to = input.Get("to") is { IsText: true } t ? s.Hierarchy.Peers.StripRef(JsText.Trim(t.AsString())) : string.Empty;
            if (to.Length == 0 || to == "main" || text.Contains("[hierarchy-msg", StringComparison.Ordinal) || text.Contains("[ah-entry", StringComparison.Ordinal))
            {
                return null;
            }

            var sessionKey = c.SessionKey();
            var resolved = config.ResolveConfig(c.Cwd(), new ResolveOptions(SessionId: sessionKey != "__nosession__" ? sessionKey : null));
            var team = resolved.Get("team") is { IsText: true } teamName ? teamName.AsString() : null;
            if (team is null)
            {
                return null;
            }

            var caller = config.ResolveHierarchyRole(c.Input);
            if (caller.Direct && caller.Role is { } mine)
            {
                var owed = OpenRequestsTo(c, mine, to, team).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var path in NamedPaths(c, ResponsePathRe().Matches(text)))
                {
                    if (s.Hierarchy.Core.MsgExists(path) && s.Hierarchy.Core.ParseMsgFilename(path) is { Type: "response" } meta && owed.Contains(meta.Id))
                    {
                        return new BareNamed("bare-report", path, HierarchyModule.OrchestratorAddress(team));
                    }
                }

                return null;
            }

            var owns = resolved.Get("ownedTeams") is { IsArray: true } list && list.AsArray().Count > 0;
            if (!owns || MemberNamed(c, to) is not { } member || TeamMemberRole(c, new JsonObject { ["to"] = member }, resolved) is not { } role
                || ExistingBrief(c, text, role, member, team) is not { } request)
            {
                return null;
            }

            return new BareNamed("bare-brief", request, $"{member}@{team}");
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            c.LogError(e);
            return null;
        }
    }

    /// <summary>Whether <paramref name="text"/> is a pure acknowledgement or greeting (at most 20 characters and exactly one word of the set), which is chat and not a dispatch.</summary>
    internal static bool IsAck(string text)
    {
        var trimmed = JsText.Trim(text).TrimEnd('.', '!', '?', ',', ';', ':');
        return trimmed.Length <= 20 && AckWords.Contains(trimmed);
    }

    private static Plan? ClassifyReply(GateContext c, string to, string text, string mine, string? team, JsonObject resolved, out string? context)
    {
        context = null;
        var trimmed = JsText.Trim(text);
        if (IsAck(trimmed))
        {
            return null;
        }

        var open = OpenRequestsTo(c, mine, to, team);
        if (open.Count == 0)
        {
            return null;
        }

        if (open.Count == 1)
        {
            return new Plan(mine, to, team, open[0].Path, open[0].Id, resolved);
        }

        var firstLine = trimmed.Split('\n')[0];
        var tokens = TokenSplit().Split(firstLine).Where(x => x.Length > 0).ToHashSet(StringComparer.Ordinal);
        var named = open.Where(r => r.Slug.Length > 0 && tokens.Contains(r.Slug)).ToList();
        if (named.Count == 1)
        {
            return new Plan(mine, to, team, named[0].Path, named[0].Id, resolved);
        }

        context = $"ah: this reply could not be paired with one open request, so it went out bare. Open requests to you: {string.Join(", ", open.Select(r => $"{r.Id} ({r.Slug})"))}. Start the reply with `DONE <slug>` (or BLOCKED/VERDICT/NEEDS) naming one slug, or answer with a response file.";
        if (trimmed.StartsWith("DONE", StringComparison.Ordinal))
        {
            context += "\nstart with DONE <slug>, or the Orchestrator closes it with: ah msg close <id> --reason done";
        }

        return null;
    }

    /// <summary>The open requests addressed to role <paramref name="mine"/> whose sender the SendMessage target <paramref name="to"/> is.</summary>
    private static List<(string Path, string Id, string Slug)> OpenRequestsTo(GateContext c, string mine, string to, string? team)
    {
        var s = c.Services;
        var found = new List<(string, string, string)>();
        if (SelfOf(c, mine) is not { } self)
        {
            return found;
        }

        var session = c.Input.Text("session_id") ?? string.Empty;
        var turnMs = s.Hierarchy.Peers.LatestTurnMarker(JsonValue.FromString(session)) is { IsTruthy: true } marker && marker.Text("ts") is { } ts && Timestamps.TryParseMs(ts, out var parsed) ? parsed : (double?)null;
        foreach (var e in s.Hierarchy.Core.ListExchanges(s.Config.HierarchyDir(c.Cwd())))
        {
            if (e.Member("request")?.Member("path") is not { IsText: true } p)
            {
                continue;
            }

            // Open, or answered since this turn began with the report not yet in a box: the reply is that report's send.
            var answered = e.Member("open") is not { IsTruthy: true }
                && e.Member("response")?.Member("path") is { IsText: true } responseFile && turnMs is { } since
                && s.Hierarchy.Core.MsgTimeMs(responseFile.AsString()) is { } written && written >= since
                && s.Advisory?.MailboxHooks.ReportStateOf(session, new OpenBrief(e.Text("id") ?? string.Empty, p.AsString(), s.Hierarchy.Core.ReadMsgFile(p.AsString()).Member("fm")?.Member("from")), out _) != ReportState.Sent;
            if (e.Member("open") is not { IsTruthy: true } && !answered)
            {
                continue;
            }

            var path = p.AsString();
            if (s.Hierarchy.Core.ReadMsgFile(path).Member("fm") is not { IsMap: true } fm || fm.Text("to") != mine || fm.Text("type") != "request" || s.Hierarchy.Core.ParseMsgFilename(path) is not { } meta)
            {
                continue;
            }

            if ((fm.Text("team") ?? string.Empty) != (self.Team ?? string.Empty)
                || (fm.Member("to_name") is { IsText: true } toName && toName.AsString() != self.Name))
            {
                continue;
            }

            var sender = new JsonObject { ["from"] = fm.Member("from") ?? JsonValue.Null, ["from_name"] = fm.Member("from_name") ?? JsonValue.Null };
            if (s.Hierarchy.Peers.TargetSatisfiesRecord(to, sender))
            {
                found.Add((path, meta.Id, fm.Text("slug") ?? string.Empty));
            }
        }

        return found;
    }

    /// <summary>The name and team of the member session making this call, from the roster row of its pane; null when the session cannot be placed in a team.</summary>
    internal static (string Name, string? Team)? SelfOf(GateContext c, string role)
    {
        var s = c.Services;
        var dir = s.Config.HierarchyDir(c.Cwd());
        var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(s.Environment.GetVariable);
        pane = string.IsNullOrEmpty(pane) ? s.Environment.GetVariable("TMUX_PANE") : pane;
        var attributed = s.Roster.AttributeSessionTeam(dir, role, null, string.IsNullOrEmpty(pane) ? null : pane, [dir, s.Config.MainHierarchyDir(c.Cwd()) ?? string.Empty]);
        var member = s.Roster.SessionMemberRow(attributed.Member("team") ?? JsonValue.Null, string.IsNullOrEmpty(pane) ? null : pane);
        return member.IsMap && member.Text("name") is { Length: > 0 } name
            ? (name, attributed.Member("teamName") is { IsText: true } teamName ? teamName.AsString() : null)
            : null;
    }

    /// <summary>
    /// Files the exchange file for a classified send and returns its path plus advice for the sender, or (null, null) when nothing was filed. The same
    /// body within <see cref="DedupWindow"/> reuses the file already made.
    /// </summary>
    internal static (string? Path, string? Advice) File(GateContext c, Plan plan, bool recordRow = true)
    {
        try
        {
            var s = c.Services;
            var input = c.ToolInput();
            var text = input.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
            var sessionId = c.Input.Text("session_id") ?? string.Empty;
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsText.Trim(text)))).ToLowerInvariant();
            var now = s.Clock.UtcNow;
            if (HeldRow(c, plan.ToName, hash) is { } heldRow && heldRow.Text("path") is { Length: > 0 } reused)
            {
                return (reused, null);
            }

            var dir = s.Config.HierarchyDir(c.Cwd());
            var body = "## [0] message\n" + text;
            string path;
            if (plan.ExistingRequest is { } existingRequest)
            {
                path = existingRequest;
            }
            else if (plan.RequestPath is not null && s.Hierarchy.Messages.ResponsePlan(plan.RequestPath) is { IsTruthy: true } responsePlan
                && responsePlan.Member("path") is { IsText: true } existingResponse && s.Hierarchy.Core.MsgExists(existingResponse.AsString()))
            {
                path = existingResponse.AsString();
            }
            else
            {
                var options = plan.RequestPath is null
                    ? RequestOptions(c, plan, input, text, body, dir)
                    : new JsonObject { ["reqPath"] = plan.RequestPath, ["cwd"] = c.Cwd(), ["type"] = "response", ["id"] = plan.RequestId!, ["from"] = plan.Role, ["body"] = body };
                var made2 = s.Hierarchy.Messages.CreateMessage(dir, options);
                if (made2.Member("path") is not { IsText: true } pathValue)
                {
                    return (null, null);
                }

                path = pathValue.AsString();
            }

            if (recordRow)
            {
                s.Hierarchy.Peers.RecordPeerRow(new JsonObject
                {
                    ["type"] = RowType,
                    ["session_id"] = sessionId,
                    ["to"] = plan.ToName,
                    ["hash"] = hash,
                    ["path"] = path,
                    ["ts"] = Timestamps.ToIsoString(now),
                });
            }

            if (plan.RequestPath is not null)
            {
                ChecklistReply(c, plan, text);
            }

            return (path, plan.RequestPath is null ? IntentAdvice(c, plan, path) : null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.LogError(e);
            return (null, null);
        }
    }

    /// <summary>A message paired to a request moves the checklist item that request is linked to (by its <c>item:</c> field, else a ref) to waiting on the Orchestrator.</summary>
    private static void ChecklistReply(GateContext c, Plan plan, string text)
    {
        try
        {
            var s = c.Services;
            if (s.Hierarchy.Core.ReadMsgFile(plan.RequestPath!).Member("fm") is not { IsMap: true } fm)
            {
                return;
            }

            static string? Field(JsonValue fm, string key) => fm.Text(key) is { Length: > 0 } v && v != "null" ? v : null;
            var team = HierarchyModule.ProgressTeamOf(Field(fm, "team"));
            var pool = Ah.Hooks.Lifecycle.ProgressPool.Of(s, c.Cwd());
            var member = Field(fm, "to_name") ?? plan.Role;
            var done = HierarchyModule.ChecklistReplyIsDone(text);
            var scope = new GitScope(c.Cwd());
            var head = done ? HierarchyModule.ChecklistCleanHead(args => s.Conventions.TryGit(scope, args)) : null;
            _ = HierarchyModule.ChecklistOnMemberReply(
                s.Hierarchy.Progress, pool, team, plan.RequestId!, Field(fm, "item"), member, Field(fm, "to") ?? plan.Role, text, s.Clock.UtcNow, c.Input.Text("session_id"),
                id => s.Hierarchy.Messages.FindByIdAndType(pool, id, "request").Member("meta")?.Member("slug") is { IsText: true } slug ? slug.AsString() : null,
                reportedSha: head,
                parentOfRequest: id => s.Hierarchy.Messages.FindByIdAndType(pool, id, "request").Member("meta")?.Member("parent") is { IsText: true } parent ? parent.AsString() : null,
                workerOfRequest: id => s.Hierarchy.Messages.FindByIdAndType(pool, id, "request").Member("meta") is { } meta && Field(meta, "to") != "reviewer" ? Field(meta, "to_name") : null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.LogError(e);
        }
    }

    private static JsonObject RequestOptions(GateContext c, Plan plan, JsonObject input, string text, string body, string dir)
    {
        var s = c.Services;
        var source = input.Get("summary") is { IsText: true } sum && JsText.Trim(sum.AsString()).Length > 0 ? sum.AsString() : text.Split('\n')[0];
        var taken = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in s.Hierarchy.Messages.OpenExchanges(dir, null))
        {
            if (e.Member("request")?.Member("path") is { IsText: true } p
                && s.Hierarchy.Core.ReadMsgFile(p.AsString()).Member("fm") is { IsMap: true } fm && fm.Text("to") == plan.Role)
            {
                taken.Add(fm.Text("slug") ?? string.Empty);
            }
        }

        return new JsonObject
        {
            ["reqPath"] = JsonValue.Null,
            ["cwd"] = c.Cwd(),
            ["type"] = "request",
            ["to"] = plan.Role,
            ["from"] = Ah.Hierarchy.HierarchyModule.OrchestratorRole,
            ["slug"] = SlugOf(source, taken),
            ["title"] = s.Hierarchy.Messages.AutoTitle(source + "\n" + text) is { } autoTitle ? autoTitle : JsonValue.Null,
            ["parent"] = JsonValue.Null,
            ["reason"] = Ah.Hierarchy.HierarchyModule.PlainSendReason,
            ["eta"] = "small",
            ["toName"] = plan.ToName,
            ["fromName"] = JsonValue.Null,
            ["markerName"] = JsonValue.Null,
            ["team"] = plan.Team is { } planTeam ? planTeam : JsonValue.Null,
            ["body"] = body,
        };
    }

    /// <summary>Advice, never a denial, when the auto-filed brief lacks what the role's intent check asks for.</summary>
    internal static string? IntentAdvice(GateContext c, Plan plan, string path)
    {
        if (c.Services.Hierarchy.Messages.ReviewIntentGap(path, plan.Resolved) is not { IsArray: true } missing || missing.AsArray().Count == 0)
        {
            return null;
        }

        var names = string.Join(", ", missing.AsArray().Select(x => x.IsText ? x.AsString() : JsonText.Stringify(x, 0)));
        var id = c.Services.Hierarchy.Core.ParseMsgFilename(NodePath.Basename(path))?.Id ?? "<id>";
        return $"ah: the auto-filed brief {id} has no {names}; add it with `ah msg fill --id {id} --from <your role> --request --section <1 or 5> --body-file -`.";
    }

    /// <summary>The slug of a message: lowercase letters and digits, runs of anything else as one <c>-</c>, at most 32 characters cut at a <c>-</c>, never empty, never the reserved name, never one of <paramref name="taken"/>.</summary>
    internal static string SlugOf(string source, ISet<string> taken)
    {
        var slug = CutSlug(NonSlug().Replace(source.ToLowerInvariant(), "-").Trim('-'), SlugMax);
        if (slug.Length == 0)
        {
            slug = "msg";
        }

        if (slug == ReservedSlug)
        {
            slug += "-msg";
        }

        var candidate = slug;
        for (var n = 2; taken.Contains(candidate); n++)
        {
            var suffix = "-" + n;
            candidate = CutSlug(slug, SlugMax - suffix.Length) + suffix;
        }

        return candidate;
    }

    private static string CutSlug(string slug, int max)
    {
        if (slug.Length <= max)
        {
            return slug;
        }

        var head = slug[..max];
        if (slug[max] != '-' && head.Contains('-', StringComparison.Ordinal))
        {
            head = head[..head.LastIndexOf('-')];
        }

        return head.Trim('-');
    }

    /// <summary>The message the recipient is sent once the exchange file exists: the pointer, then the original text unchanged.</summary>
    internal static string PointerMessage(string path, string text) => $"{Ah.Core.Json.MsgPointer.ShowLine(path)}\n{text}";

    /// <summary>
    /// PreToolUse: when <paramref name="enabled"/>, files the exchange and rewrites the input to carry the pointer. Null when nothing is rewritten.
    /// No permission decision is given, so the call goes through the user's normal permissions.
    /// </summary>
    internal static HookOutcome? Rewrite(GateContext c, bool enabled)
    {
        if (!enabled)
        {
            return null;
        }

        _ = PreSend(c, true, out var outcome);
        return outcome;
    }

    /// <summary>What the PreToolUse tracking of a bare send came to.</summary>
    internal enum Tracking
    {
        /// <summary>The send is none of auto-track's, or it was tracked already within the dedup window.</summary>
        NotApplicable,

        /// <summary>The request or response file, its dispatch row and its mailbox entry exist, and this call holds the entry's claim.</summary>
        Done,

        /// <summary>The send is auto-track's but a step could not be taken.</summary>
        Failed,
    }

    /// <summary>The mailbox entry marker the recipient (or the PostToolUse settling) keys the entry by.</summary>
    internal static string Marker(string entry, string address) => $"[ah-entry {entry} to {address}]";

    /// <summary>The last auto-track row of this session for the same recipient and text, if it is still inside the dedup window.</summary>
    private static JsonValue? HeldRow(GateContext c, string toName, string hash)
    {
        var s = c.Services;
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var now = s.Clock.UtcNow;
        var held = s.Hierarchy.Core.SessionRows(sessionId, RowType).LastOrDefault(r =>
            r.IsMap && r.Text("type") == RowType && r.Text("session_id") == sessionId && r.Text("to") == toName && r.Text("hash") == hash
            && r.Text("ts") is { } at && DateTimeOffset.TryParse(at, out var made) && now - made < DedupWindow);
        return held.IsMap ? held : (JsonValue?)null;
    }

    private static string HashOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsText.Trim(text)))).ToLowerInvariant();

    /// <summary>
    /// PreToolUse for a bare Orchestrator↔member send: files the request or response (an existing one is reused), writes the dispatch row,
    /// files the mailbox entry through the mesh sender and takes its claim for this call. With <paramref name="rewrite"/> the message is
    /// rewritten to carry the pointer and the entry marker; without it the send goes out as typed and PostToolUse settles the claim.
    /// </summary>
    internal static Tracking PreSend(GateContext c, bool rewrite, out HookOutcome? outcome)
    {
        outcome = null;
        try
        {
            if (Classify(c, out _) is not { } plan)
            {
                return Tracking.NotApplicable;
            }

            var input = c.ToolInput();
            var text = input.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
            if (HeldRow(c, plan.ToName, HashOf(text)) is not null)
            {
                return Tracking.NotApplicable;
            }

            // A send that names no request file is mail: it is not filed as a brief and records no dispatch, so no check-in clock runs for it.
            if (plan.RequestPath is null && plan.ExistingRequest is null)
            {
                return Tracking.NotApplicable;
            }

            if (HookHost.PastDeadline)
            {
                return Tracking.NotApplicable;
            }

            var (path, advice) = File(c, plan, recordRow: false);
            if (path is null || c.Services.Advisory is not { Sender: not null } advisory)
            {
                return Tracking.Failed;
            }

            var s = c.Services;
            var session = c.Input.Text("session_id") ?? string.Empty;
            var toolUseId = c.Input.Text("tool_use_id") ?? string.Empty;
            var to = input.Get("to") is { IsText: true } t ? s.Hierarchy.Peers.StripRef(JsText.Trim(t.AsString())) : string.Empty;
            var kind = plan.RequestPath is null ? "brief" : "report";
            if (HookHost.PastDeadline)
            {
                return Tracking.NotApplicable;
            }

            if (plan.RequestPath is null && s.Hierarchy.Core.ParseMsgFilename(path) is { Type: "request" } meta)
            {
                s.Hierarchy.Peers.AppendDispatchRecord(JsonValue.FromString(session), meta.Id, plan.Role, path, to);
            }

            var recipient = plan.RequestPath is null ? $"{plan.ToName}@{plan.Team}" : HierarchyModule.OrchestratorAddress(plan.Team);
            if (HookHost.PastDeadline)
            {
                return Tracking.NotApplicable;
            }

            if (plan.Team is null || FileEntry(c, advisory, session, recipient, kind, path) is not { } filed)
            {
                return Tracking.Failed;
            }

            if (HookHost.PastDeadline)
            {
                return Tracking.NotApplicable;
            }

            var marker = Marker(filed.Entry, filed.Address);
            var message = rewrite ? PointerMessage(path, text) + "\n" + marker : marker;
            var context = advisory.Callers.RequestContext(c.Cwd());
            var decision = advisory.MailboxHooks.ClaimRelay(session, to, message, context, toolUseId.Length == 0 ? null : toolUseId);
            if (decision.Deny)
            {
                outcome = HookEmit.Decision("deny", decision.Reason ?? "ah could not record this relay.", "ah: held a send whose mailbox entry is already delivered or being delivered.");
                return Tracking.Done;
            }

            try
            {
                s.Hierarchy.Peers.RecordPeerRow(new JsonObject
                {
                    ["type"] = RowType,
                    ["session_id"] = session,
                    ["to"] = plan.ToName,
                    ["hash"] = HashOf(text),
                    ["path"] = path,
                    ["ts"] = Timestamps.ToIsoString(s.Clock.UtcNow),
                    ["tool_use_id"] = toolUseId,
                    ["entry"] = filed.Entry,
                    ["address"] = filed.Address,
                    ["mode"] = rewrite ? "rewrite" : "hook",
                });
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            {
                // The claim must not outlive a call that is denied for want of its row.
                _ = advisory.MailboxHooks.WithdrawRelay(session, to, message, toolUseId.Length == 0 ? null : toolUseId, context);
                c.LogError(e);
                return Tracking.Failed;
            }
            if (rewrite)
            {
                var changed = new JsonObject();
                foreach (var key in input.Keys)
                {
                    changed[key] = input[key];
                }

                changed["message"] = message;
                if (plan.Live && plan.RequestPath is null && changed.Get("notify_when_idle") is not { IsBool: true })
                {
                    changed["notify_when_idle"] = true;
                }

                outcome = HookEmit.UpdatedInput(changed, advice);
            }

            return Tracking.Done;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            c.LogError(e);
            return Tracking.Failed;
        }
    }

    /// <summary>Files the brief or report as a mailbox entry through the mesh sender; the sender is chosen from the session's proven addresses.</summary>
    private static (string Entry, string Address)? FileEntry(GateContext c, AdvisoryServices advisory, string session, string recipient, string kind, string path)
    {
        var context = advisory.Callers.RequestContext(c.Cwd());
        var request = new JsonObject
        {
            ["to"] = recipient,
            ["session_id"] = session,
            ["cwd"] = context.Cwd,
            ["hierarchy_dir"] = context.HierarchyDirectory,
            ["main_hierarchy_dir"] = context.MainHierarchyDirectory.Length > 0 ? context.MainHierarchyDirectory : JsonValue.Null,
            ["messaging_socket_dir"] = context.SocketDirectory.Length > 0 ? context.SocketDirectory : JsonValue.Null,
            ["id"] = advisory.Mailbox.NewId(),
            ["kind"] = kind,
            ["ref"] = path,
        };
        var outcome = advisory.Sender!.Send(request, null, SendLockWait);
        if (outcome.Value is not { } result || result.Get("entry")?.Member("id") is not { IsText: true } id)
        {
            return null;
        }

        return (id.AsString(), result.Get("address") is { IsText: true } address ? address.AsString() : recipient);
    }

    /// <summary>
    /// PostToolUse for a send PreSend tracked: confirms the claim this call took with the marker line (a real marker already confirmed
    /// it, a missing one means the rewrite was not honoured or none was made) and acks the entry, since the SendMessage carried no marker
    /// to ack by. Does nothing for a call that PreSend did not track.
    /// </summary>
    private static bool Settle(GateContext c)
    {
        var s = c.Services;
        var session = c.Input.Text("session_id") ?? string.Empty;
        var toolUseId = c.Input.Text("tool_use_id") ?? string.Empty;
        if (toolUseId.Length == 0 || s.Advisory is not { } advisory)
        {
            return false;
        }

        var row = s.Hierarchy.Core.SessionRows(session, RowType).LastOrDefault(r =>
            r.IsMap && r.Text("type") == RowType && r.Text("session_id") == session && r.Text("tool_use_id") == toolUseId && r.Text("entry") is { Length: > 0 });
        if (!row.IsMap)
        {
            return false;
        }

        var entry = row.Text("entry")!;
        var address = row.Text("address") ?? string.Empty;
        var message = c.ToolInput().Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
        var honoured = row.Text("mode") == "rewrite" && message.Contains(Marker(entry, address), StringComparison.Ordinal);
        if (honoured)
        {
            return true;
        }

        if (row.Text("mode") == "rewrite")
        {
            c.LogError(new InvalidOperationException($"auto-track rewrite not honoured ({c.Input.Text("permission_mode") ?? "unknown"})"));
        }

        var context = advisory.Callers.RequestContext(c.Cwd());
        var to = c.ToolInput().Get("to") is { IsText: true } t ? t.AsString() : string.Empty;
        var result = RelayResults.Of(c.Input.Member("tool_response"), out var reason);
        var msgId = c.Input.Member("tool_response")?.Member("msg_id") is { IsText: true } id ? id.AsString() : null;
        _ = advisory.MailboxHooks.ConfirmRelay(session, to, Marker(entry, address), result, reason, context, msgId);
        if (SendSucceeded(c) && advisory.Mailbox.ResolveAddress(address, context).Value is { } resolved)
        {
            _ = advisory.Mailbox.AckIds(resolved.Dir, [entry], "sendmessage", session);
        }

        return true;
    }

    /// <summary>PostToolUse: the filing for a send that has gone out bare. Returns advice for the sender, or null.</summary>
    internal static HookOutcome? AfterSend(GateContext c)
    {
        try
        {
            return AfterSendCore(c) is { } advice && Ah.Hooks.Lifecycle.OrchestratorIdentity.In(c) ? advice : null;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            c.LogError(e);
            return null;
        }
    }

    private static HookOutcome? AfterSendCore(GateContext c)
    {
        if (Settle(c))
        {
            return null;
        }

        var plan = Classify(c, out var context);
        var advice = context;
        if (plan is { RequestPath: null } && SendSucceeded(c))
        {
            // The Orchestrator answering a member by message answers whatever of that member's it was waiting on, once the message has gone out.
            var team = HierarchyModule.ProgressTeamOf(plan.Team);
            _ = HierarchyModule.ChecklistOnOrchestratorMessage(c.Services.Hierarchy.Progress, Ah.Hooks.Lifecycle.ProgressPool.Of(c.Services, c.Cwd()), team, plan.ToName, HierarchyModule.OrchestratorAddress(team),c.Input.Text("session_id"), HierarchyModule.ChecklistEventTime(c.Services.Clock.UtcNow));
        }

        if (plan is not null && (plan.RequestPath is not null || plan.ExistingRequest is not null))
        {
            var (path, filed) = File(c, plan);
            if (path is null)
            {
                var input = c.ToolInput();
                return BareSendNudge(c, input, input.Get("message") is { IsText: true } m ? m.AsString() : string.Empty, plan.Resolved);
            }

            advice = filed;
        }
        else if (plan is { RequestPath: null, ExistingRequest: null, Team: not null } && SendSucceeded(c))
        {
            RecordPlainSend(c, plan);
        }

        return advice is null ? null : HookEmit.AdditionalContext("PostToolUse", advice);
    }

    /// <summary>
    /// board-derive § C: a plain Orchestrator send that went out is recorded here, never in PreSend, and nothing is written once the hook's
    /// deadline has passed. A follow-up to the member's newest open request is filed as an entry of that request, already delivered by this
    /// SendMessage, with a <c>followup</c> peer row that restarts its idle-without-report count; any other send becomes a
    /// <see cref="HierarchyModule.PlainSendReason"/> request, eta small, whose clock arms only once the member works on it.
    /// </summary>
    private static void RecordPlainSend(GateContext c, Plan plan)
    {
        var s = c.Services;
        if (HookHost.PastDeadline || s.Advisory is not { Sender: not null } advisory)
        {
            return;
        }

        var input = c.ToolInput();
        var text = input.Get("message") is { IsText: true } m ? m.AsString() : string.Empty;
        var session = c.Input.Text("session_id") ?? string.Empty;
        var to = input.Get("to") is { IsText: true } t ? s.Hierarchy.Peers.StripRef(JsText.Trim(t.AsString())) : string.Empty;
        var dir = s.Config.HierarchyDir(c.Cwd());
        var open = NewestOpenBrief(c, plan, dir);
        var path = open;
        if (path is null)
        {
            if (HookHost.PastDeadline || s.Hierarchy.Messages.CreateMessage(dir, RequestOptions(c, plan, input, text, "## [0] message\n" + text, dir)).Member("path") is not { IsText: true } made)
            {
                return;
            }

            path = made.AsString();
        }

        // The settle unit (board-derive § C1 r3): the deadline is checked once before it and never inside, so a run leaves no entry or a
        // settled one, never an unread entry the mailbox would deliver again. The SendMessage carried the text, so the entry is claimed,
        // confirmed and acked as delivered by it; a refused claim still acks it.
        if (HookHost.PastDeadline || FileEntry(c, advisory, session, $"{plan.ToName}@{plan.Team}", "brief", path) is not { } filed)
        {
            return;
        }

        var context = advisory.Callers.RequestContext(c.Cwd());
        var marker = Marker(filed.Entry, filed.Address);
        var toolUseId = c.Input.Text("tool_use_id") ?? string.Empty;
        if (!advisory.MailboxHooks.ClaimRelay(session, to, marker, context, toolUseId.Length == 0 ? null : toolUseId).Deny)
        {
            var result = RelayResults.Of(c.Input.Member("tool_response"), out var reason);
            var msgId = c.Input.Member("tool_response")?.Member("msg_id") is { IsText: true } id ? id.AsString() : null;
            _ = advisory.MailboxHooks.ConfirmRelay(session, to, marker, result, reason, context, msgId);
        }

        if (advisory.Mailbox.ResolveAddress(filed.Address, context).Value is { } resolved)
        {
            _ = advisory.Mailbox.AckIds(resolved.Dir, [filed.Entry], "sendmessage", session);
        }

        if (open is null)
        {
            if (!HookHost.PastDeadline && s.Hierarchy.Core.ParseMsgFilename(path) is { Type: "request" } meta)
            {
                s.Hierarchy.Peers.AppendDispatchRecord(JsonValue.FromString(session), meta.Id, plan.Role, path, to);
            }

            return;
        }

        if (HookHost.PastDeadline || s.Hierarchy.Core.ParseMsgFilename(open) is not { Type: "request" } followed)
        {
            return;
        }

        var at = Timestamps.ToIsoString(s.Clock.UtcNow);
        s.Hierarchy.Peers.RecordPeerRow(new JsonObject { ["type"] = "followup", ["session_id"] = session, ["request_id"] = followed.Id, ["at"] = at, ["entry"] = filed.Entry });
        if (HookHost.PastDeadline)
        {
            return;
        }

        // The follow-up re-arms the exchange: a dispatch record starts its check-in clock, as a tokened dispatch would.
        s.Hierarchy.Peers.AppendDispatchRecord(JsonValue.FromString(session), followed.Id, plan.Role, open, to);
        if (HookHost.PastDeadline)
        {
            return;
        }

        // Start and a note, not a ref: a ref sorts after every request id, so the item would read as dispatched since its request and drop the request's DONE.
        var team = HierarchyModule.ProgressTeamOf(plan.Team);
        var pool = Ah.Hooks.Lifecycle.ProgressPool.Of(s, c.Cwd());
        var note = "follow-up: " + (text.Length > 80 ? text[..80] : text);
        foreach (var entry in HierarchyModule.ChecklistEntries(s.Hierarchy.Progress, pool, team).Where(e => (e.Item.Refs ?? []).Contains(followed.Id, StringComparer.Ordinal)))
        {
            var cause = new PlanCause("followup", filed.Entry, at);
            _ = s.Hierarchy.Progress.RecordChecklist(pool, team, entry.Stream, HierarchyModule.OrchestratorAddress(team), [new PlanInput(entry.Item.Item, "start") { Cause = cause }, new PlanInput(entry.Item.Item, "note", Note: note) { Cause = cause }], session);
        }
    }

    /// <summary>The newest open request from the Orchestrator of <paramref name="plan"/>'s team to its member, or null when the member holds none.</summary>
    private static string? NewestOpenBrief(GateContext c, Plan plan, string dir)
    {
        var s = c.Services;
        string? newest = null, newestId = null;
        foreach (var e in s.Hierarchy.Messages.OpenExchanges(dir, null))
        {
            if (e.Member("id") is not { IsText: true } id || e.Member("request")?.Member("path") is not { IsText: true } p
                || s.Hierarchy.Core.ReadMsgFile(p.AsString()).Member("fm") is not { IsMap: true } fm
                || fm.Text("from") != HierarchyModule.OrchestratorRole || fm.Text("to") != plan.Role || (fm.Text("team") ?? string.Empty) != (plan.Team ?? string.Empty)
                || fm.Text("to_name") != plan.ToName)
            {
                continue;
            }

            if (newestId is null || string.CompareOrdinal(id.AsString(), newestId) > 0)
            {
                (newest, newestId) = (p.AsString(), id.AsString());
            }
        }

        return newest;
    }

    /// <summary>
    /// Whether the SendMessage result reports the message as sent: a message id came back, and no failure is marked. A send that returns no
    /// message id (an in-process one) counts as not sent, so the clear fails closed and member-waiting still fires.
    /// </summary>
    private static bool SendSucceeded(GateContext c) =>
        c.Input.Member("tool_response") is { IsMap: true } response
        && response.Member("msg_id") is { IsText: true } id && id.AsString().Length > 0
        && (response.Member("success") is not { } ok || ok.IsTruthy);

    [GeneratedRegex("[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlug();

    [GeneratedRegex("[\\s@:,;()]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenSplit();
}
