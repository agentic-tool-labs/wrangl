using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>The presentation-neutral documents beside the node views: <c>detail</c>, <c>graph</c>, <c>actions</c> and the three read documents.</summary>
internal static partial class ViewBuilder
{
    /// <summary>The document kinds that take the path of a node after them.</summary>
    private static readonly string[] ModelKinds = ["detail", "graph", "actions", "status", "stream-status", "inbox", "plan"];

    /// <summary>What a model document is built from: the build's discovery, its facts and its path helpers.</summary>
    private sealed class ModelCtx
    {
        internal required IViewSource Src { get; init; }

        internal required double Now { get; init; }

        internal required List<Orch> Orchs { get; init; }

        internal required Func<Orch, TeamEntry, Facts> FactsOf { get; init; }

        internal required Func<string, JsonObject> StatusOf { get; init; }

        internal required Lazy<Func<string, (long? Pid, string? CheckoutRoot, string? Cwd)>> Registrations { get; init; }

        internal required Func<Orch, string> OPath { get; init; }

        internal required Func<Orch, TeamEntry, string> TPath { get; init; }

        internal required Func<Orch, TeamEntry, string, string> MPath { get; init; }

        internal required Func<Orch, TeamEntry, string, string> SPath { get; init; }

        internal required Func<Orch, string> OrchLabel { get; init; }

        internal required Func<Orch, string> OrchState { get; init; }

        internal required Func<TeamEntry, string> TeamLabel { get; init; }

        internal required Func<string, BuiltView> NotFound { get; init; }

        /// <summary>The subagent runs, read at most once for a build.</summary>
        internal required Lazy<IReadOnlyList<SubagentRun>> Runs { get; init; }

        /// <summary>The session ids the latest open outage holds, read at most once for a build.</summary>
        internal required Lazy<HashSet<string>> Held { get; init; }

        /// <summary>The text after a <c>?</c> in the requested path (<c>all=1</c> asks the plan document for every done item).</summary>
        internal string Options { get; init; } = string.Empty;
    }

    /// <summary>A node of the hierarchy a model document is about, found by the path of its view.</summary>
    private sealed class Node
    {
        internal required string Kind { get; init; }

        internal required string Path { get; init; }

        internal Orch? Orch { get; init; }

        internal TeamEntry? Team { get; init; }

        internal Facts? Facts { get; init; }

        internal JsonObject? Member { get; init; }

        internal StreamRow? Stream { get; init; }

        /// <summary>For a mail record: the path of the node whose box holds it.</summary>
        internal string? Holder { get; init; }

        /// <summary>For a mail record: the box owner, a member name or <c>orchestrator</c>.</summary>
        internal string? BoxOwner { get; init; }

        /// <summary>For a mail record: the entry, without its request file read.</summary>
        internal JsonObject? Entry { get; init; }

        /// <summary>For a subagent: its run.</summary>
        internal SubagentRun? Run { get; init; }
    }

    /// <summary>The node at <paramref name="segs"/> (a view path split into segments), or null; <c>nearest</c> is the deepest valid ancestor's path.</summary>
    private static Node? ResolveNode(ModelCtx c, IReadOnlyList<string> segs, out string nearest)
    {
        nearest = "/";
        if (segs.Count == 0)
        {
            return new Node { Kind = "root", Path = "/" };
        }

        if (segs[0] != "o" || segs.Count < 2 || c.Orchs.FirstOrDefault(o => o.Oid == segs[1]) is not { } orch)
        {
            return null;
        }

        nearest = c.OPath(orch);
        if (segs.Count == 2)
        {
            return new Node { Kind = "orchestrator", Path = c.OPath(orch), Orch = orch };
        }

        if (segs[2] != "t" || segs.Count < 4 || orch.Teams.FirstOrDefault(t => t.Tkey == segs[3]) is not { } team)
        {
            return null;
        }

        nearest = c.TPath(orch, team);
        var facts = c.FactsOf(orch, team);
        if (segs.Count == 4)
        {
            return new Node { Kind = "team", Path = nearest, Orch = orch, Team = team, Facts = facts };
        }

        if (segs.Count == 8 && segs[4] == "m" && segs[6] == "sub" && facts.Members.FirstOrDefault(m => m.Str("name") == segs[5]) is { } parent)
        {
            var holder = c.MPath(orch, team, segs[5]);
            nearest = holder;
            var session = parent.Str("session_id");
            return c.Runs.Value.FirstOrDefault(r => session is not null && r.SessionId == session && r.AgentId == segs[7]) is { } run
                ? new Node { Kind = "subagent", Path = holder + "/sub/" + Seg(segs[7]), Orch = orch, Team = team, Facts = facts, Member = parent, Holder = holder, Run = run }
                : null;
        }

        if (segs.Count == 6 && segs[4] == "mail")
        {
            return c.Src.BoxEntry(c.Src.BoxDirFor(team.File, "orchestrator"), segs[5], index: true) is { } held
                ? new Node { Kind = "mail", Path = nearest + "/mail/" + Seg(segs[5]), Orch = orch, Team = team, Facts = facts, Holder = nearest, BoxOwner = "orchestrator", Entry = held }
                : null;
        }

        if (segs.Count == 8 && segs[4] == "m" && segs[6] == "mail" && facts.Members.FirstOrDefault(m => m.Str("name") == segs[5]) is { } owner)
        {
            var holder = c.MPath(orch, team, segs[5]);
            nearest = holder;
            return c.Src.BoxEntry(c.Src.BoxDirFor(team.File, segs[5]), segs[7], index: true) is { } entry
                ? new Node { Kind = "mail", Path = holder + "/mail/" + Seg(segs[7]), Orch = orch, Team = team, Facts = facts, Member = owner, Holder = holder, BoxOwner = segs[5], Entry = entry }
                : null;
        }

        if (segs.Count != 6)
        {
            return null;
        }

        if (segs[4] == "m" && facts.Members.FirstOrDefault(m => m.Str("name") == segs[5]) is { } member)
        {
            return new Node { Kind = "member", Path = c.MPath(orch, team, segs[5]), Orch = orch, Team = team, Facts = facts, Member = member };
        }

        if (segs[4] == "s" && facts.Work.Streams.FirstOrDefault(r => r.Name == segs[5]) is { } stream)
        {
            return new Node { Kind = "stream", Path = c.SPath(orch, team, stream.Name), Orch = orch, Team = team, Facts = facts, Stream = stream };
        }

        return null;
    }

    /// <summary>The first path segment after <c>actions</c> that names a node by its member slice id parts (<c>/actions/member/&lt;dirId&gt;/&lt;team&gt;/&lt;member&gt;</c>) instead of by its view path.</summary>
    internal const string MemberIdSegment = "member";

    /// <summary>
    /// The node the member slice id <c>member:&lt;dirId&gt;/&lt;team&gt;/&lt;member&gt;</c> stands for: the member's row of that team, or the team's
    /// orchestrator for the reserved member. The team named <c>default</c> is the team file without an alias. Null when no team or member matches.
    /// </summary>
    private static Node? ResolveMemberId(ModelCtx c, string dirId, string team, string member, out string nearest)
    {
        nearest = "/";
        foreach (var orch in c.Orchs)
        {
            foreach (var entry in orch.Teams.Where(t => Model.ActivityLane.DirId(t.Dir) == dirId && Model.TeamLane.IdName(t.Name) == team))
            {
                nearest = c.TPath(orch, entry);
                if (member == Model.TreeIds.Orchestrator)
                {
                    return new Node { Kind = "orchestrator", Path = c.OPath(orch), Orch = orch };
                }

                var facts = c.FactsOf(orch, entry);
                return facts.Members.FirstOrDefault(m => m.Str("name") == member) is { } row
                    ? new Node { Kind = "member", Path = c.MPath(orch, entry, member), Orch = orch, Team = entry, Facts = facts, Member = row }
                    : null;
            }
        }

        return null;
    }

    /// <summary>The model document <paramref name="kind"/> of the node at <paramref name="rest"/>.</summary>
    private static BuiltView ModelDocument(ModelCtx c, string kind, string fullPath, IReadOnlyList<string> rest)
    {
        var nearest = "/";
        var node = kind == "actions" && rest is [MemberIdSegment, var dirId, var team, var member]
            ? ResolveMemberId(c, dirId, team, member, out nearest)
            : ResolveNode(c, rest, out nearest);
        if (node is null)
        {
            return c.NotFound(nearest);
        }

        return kind switch
        {
            "detail" => DetailDocument(c, node, fullPath),
            "graph" => GraphDocument(c, node, fullPath),
            "actions" => ActionsDocument(c, node, fullPath),
            "plan" => PlanDocument(c, node, fullPath),
            "status" or "stream-status" or "inbox" => ReadDocument(c, kind, node, fullPath),
            _ => c.NotFound("/"),
        };
    }

    /// <summary>The path of the node's parent: the deepest valid ancestor a document the node has none of reports as <c>nearest</c>.</summary>
    private static string ParentPath(ModelCtx c, Node node) => node.Kind switch
    {
        "team" => c.OPath(node.Orch!),
        "member" or "stream" => c.TPath(node.Orch!, node.Team!),
        "mail" or "subagent" => node.Holder!,
        _ => "/",
    };

    private static JsonObject ModelEnvelope(ModelCtx c, string kind, Node node, string fullPath) =>
        Envelope(kind, node.Path, fullPath, Arr(Crumb("root", string.Empty, "ah", "/"), Crumb(node.Kind, node.Path, NodeLabel(c, node), node.Path)), c.Now);

    private static string NodeLabel(ModelCtx c, Node node) => node.Kind switch
    {
        "member" => node.Member!.Str("name")!,
        "orchestrator" => c.OrchLabel(node.Orch!),
        "team" => c.TeamLabel(node.Team!),
        "stream" => node.Stream!.Name,
        "mail" => node.Entry!.Str("id") ?? "mail",
        "subagent" => node.Run!.AgentId,
        _ => "ah",
    };

    // ------------------------------------------------------------------ detail

    private sealed record DetailField(string Key, string Label, JsonValue Value, string Source, bool Verified, string? Reason);

    private static DetailField Present(string key, string label, JsonValue value, string source, bool verified = true) => new(key, label, value, source, verified, null);

    private static DetailField Absent(string key, string label, string source, string reason) => new(key, label, JsonValue.Null, source, false, reason);

    private static JsonObject FieldJson(DetailField f) => new()
    {
        ["key"] = f.Key,
        ["label"] = f.Label,
        ["present"] = f.Reason is null,
        ["value"] = f.Value,
        ["source"] = f.Source,
        ["verified"] = f.Verified,
        ["reason"] = f.Reason is null ? JsonValue.Null : f.Reason,
    };

    private static BuiltView DetailDocument(ModelCtx c, Node node, string fullPath)
    {
        List<DetailField> fields;
        switch (node.Kind)
        {
            case "member":
                fields = MemberFields(c, node);
                break;
            case "orchestrator":
                fields = OrchestratorFields(c, node);
                break;
            case "mail":
                fields = MailFields(c, node);
                break;
            case "subagent":
                fields = SubagentFields(node);
                break;
            default:
                return c.NotFound(ParentPath(c, node));
        }

        var view = ModelEnvelope(c, "detail", node, fullPath);
        var list = new JsonArray();
        foreach (var f in fields)
        {
            list.Add(FieldJson(f));
        }

        view["summary"] = new JsonObject();
        view["children"] = new JsonArray();
        view["body"] = new JsonObject { ["node"] = new JsonObject { ["kind"] = node.Kind, ["path"] = node.Path }, ["fields"] = list };
        return Finish(view);
    }

    private const int SubagentRows = 20;
    private const int MailRows = 10;
    private const int MailRowsPerDirection = 20;

    /// <summary>The slug of a request file named <c>&lt;id&gt;--&lt;to&gt;--&lt;slug&gt;--&lt;type&gt;.md</c>; null for any other name.</summary>
    private static JsonValue SlugOf(string? file)
    {
        if (string.IsNullOrEmpty(file))
        {
            return JsonValue.Null;
        }

        var parts = Path.GetFileName(file).Split("--");
        return parts.Length >= 4 && parts[2].Length > 0 ? parts[2] : JsonValue.Null;
    }

    private const string StatusSource = "status document (activity record)";
    private const string TeamFileSource = "team file";

    private static List<DetailField> OrchestratorFields(ModelCtx c, Node node)
    {
        var orch = node.Orch!;
        string? session = null;
        foreach (var t in orch.Teams)
        {
            if (t.Doc.Obj("orchestrator")?.Str("session_id") is { Length: > 0 } s)
            {
                session = s;
                break;
            }
        }

        List<DetailField> fields =
        [
            Present("name", "name", c.OrchLabel(orch), "view"),
            Present("state", "state", c.OrchState(orch), "pid liveness"),
            Present("repo", "repo", TextOrNull(c.Src, RepoName(orch.Dir), NameCap), "hierarchy directory"),
            orch.Pid is { } pid ? Present("pid", "pid", pid, TeamFileSource) : Absent("pid", "pid", TeamFileSource, "no orchestrator recorded"),
            session is null ? Absent("session", "session", TeamFileSource, "no session recorded") : Present("session", "session", TextOrNull(c.Src, session, NameCap), TeamFileSource),
            Present("teams", "teams", orch.Teams.Count, "view"),
            StartOf(c, orch),
        ];
        fields.AddRange(OrchestratorGroups(c, node, session));
        return fields;
    }

    private const string EntrySource = "mailbox entry";

    private static List<DetailField> SubagentFields(Node node)
    {
        var run = node.Run!;
        const string source = "inflight records";
        return
        [
            new DetailField("id", "id", run.AgentId, source, true, null),
            new DetailField("status", "status", run.Status, source, true, null),
            new DetailField("started_at", "started", run.StartedAt, source, true, null),
            run.EndedAt is null ? Absent("ended_at", "ended", source, run.Status == "started" ? "still running" : "no stop was recorded") : Present("ended_at", "ended", run.EndedAt, source),
            Present("parent", "parent", node.Holder!, "view"),
        ];
    }

    /// <summary>Whether the segments are exactly <c>o/OID/t/TEAM/mail/ID/text</c> or <c>o/OID/t/TEAM/m/MEMBER/mail/ID/text</c>; any other path ending in <c>mail/…/text</c> may still be a node of its own.</summary>
    private static bool IsMailTextPath(List<string> segs) => segs.Count switch
    {
        7 => segs[0] == "o" && segs[2] == "t" && segs[4] == "mail" && segs[6] == "text",
        9 => segs[0] == "o" && segs[2] == "t" && segs[4] == "m" && segs[6] == "mail" && segs[8] == "text",
        _ => false,
    };

    /// <summary>The most bytes of a request file the text document serves.</summary>
    private const int MailTextBytes = 64 * 1024;

    /// <summary>The text of the file a mail entry names, read only and capped; control characters other than line breaks and tabs are dropped so no layer can be driven by the file's escape sequences.</summary>
    private static BuiltView MailTextDocument(ModelCtx c, string fullPath, IReadOnlyList<string> mailSegs)
    {
        var node = ResolveNode(c, mailSegs, out var nearest);
        if (node is not { Kind: "mail" })
        {
            return c.NotFound(nearest);
        }

        var file = node.Entry!.Str("ref");
        var outside = !string.IsNullOrEmpty(file) && !c.Src.IsMessageFile(file);
        FileText? read = string.IsNullOrEmpty(file) || outside ? null : c.Src.RequestText(file, MailTextBytes);
        var view = ModelEnvelope(c, "text", node, fullPath);
        view["summary"] = new JsonObject();
        view["children"] = new JsonArray();
        view["body"] = new JsonObject
        {
            ["ref"] = string.IsNullOrEmpty(file) ? JsonValue.Null : TextOrNull(c.Src, file, 400),
            ["text"] = read is null ? JsonValue.Null : PlainText(read.Text),
            ["truncated"] = read?.Truncated ?? false,
            ["reason"] = read is not null ? JsonValue.Null : string.IsNullOrEmpty(file) ? "the entry names no file" : "the message is not in the store",
        };
        return Finish(view);
    }

    /// <summary>The characters that reorder how the text around them is displayed: embeddings and overrides, isolates, and the directional marks.</summary>
    private static bool IsBidiControl(char ch) => ch is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '\u200E' or '\u200F' or '\u061C';

    private static string PlainText(string text)
    {
        var kept = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            var separator = char.GetUnicodeCategory(ch) is System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;
            if (ch is '\n' or '\t' || !(char.IsControl(ch) || separator || IsBidiControl(ch)))
            {
                _ = kept.Append(ch);
            }
        }

        return kept.ToString();
    }

    private static List<DetailField> MailFields(ModelCtx c, Node node)
    {
        var src = c.Src;
        var entry = node.Entry!;
        var id = entry.Str("id")!;
        var full = src.BoxEntry(src.BoxDirFor(node.Team!.File, node.BoxOwner!), id) ?? entry;
        var file = full.Str("ref");
        var hasFile = !string.IsNullOrEmpty(file);
        var spec = full.Str("spec");
        var acked = full.TryGetValue("acked", out var a) && a.Kind == JsonKind.Bool && a.AsBool();
        var slug = SlugOf(file);
        return
        [
            Present("id", "id", id, EntrySource),
            Present("kind", "kind", Get(full, "kind"), EntrySource),
            Present("from", "from", Get(full, "from"), EntrySource),
            Present("to", "to", Get(full, "to"), EntrySource),
            Present("created", "created", Get(full, "created"), EntrySource),
            Present("state", "state", acked ? "acked" : "waiting", EntrySource),
            hasFile ? Present("ref", "message", TextOrNull(src, MsgPointer.Id(file!), 400), EntrySource) : Absent("ref", "message", EntrySource, "the entry names no file"),
            slug.IsNull ? Absent("slug", "slug", "request file name", hasFile ? "the file name carries no slug" : "the entry names no file") : Present("slug", "slug", slug, "request file name"),
            spec is null ? Absent("spec", "spec", "request text", hasFile ? "the request text names no spec" : "the entry names no file") : Present("spec", "spec", TextOrNull(src, spec, 400), "request text"),
            Present("changed_since_delivery", "changed since delivery", full.TryGetValue("changed", out var ch) && ch.Kind == JsonKind.Bool && ch.AsBool(), "request file hash"),
        ];
    }

    /// <summary>The daemon's local zone: the zone older <c>pid_start</c> records were written in.</summary>
    internal static readonly TimeZoneInfo StartZone = TimeZoneInfo.Local;

    /// <summary>The ISO-8601 UTC instant a recorded <c>pid_start</c> (the <c>ps -o lstart</c> text) names when read in <paramref name="zone"/>, or null when it is not a start time.</summary>
    internal static string? StartedIso(string pidStart, TimeZoneInfo zone) =>
        Timestamps.TryParseProcessStart(pidStart, zone, out var ms) ? IsoTime.Seconds(ms) : null;

    /// <summary>
    /// A recorded <c>pid_start</c> as an ISO-8601 UTC instant and whether the process table vouches for it. Current records are UTC text;
    /// only a <see cref="StartMatch.Legacy"/> match is read in <paramref name="local"/>. A match the table does not confirm
    /// (<see cref="StartMatch.Different"/>, or <see cref="StartMatch.Unknown"/> for a gone or unreadable process) is read as UTC and unverified.
    /// </summary>
    internal static (string? Iso, bool Verified) StartedFrom(string pidStart, StartMatch match, TimeZoneInfo local) =>
        (StartedIso(pidStart, match == StartMatch.Legacy ? local : TimeZoneInfo.Utc), ProcessStart.IsSame(match));

    private static DetailField StartOf(ModelCtx c, Orch orch)
    {
        foreach (var t in orch.Teams)
        {
            if (t.Doc.Obj("orchestrator")?.Str("pid_start") is { Length: > 0 } start)
            {
                var match = orch.Pid is { } pid ? c.Src.ProcessStartMatch(pid, start) : StartMatch.Unknown;
                var (iso, verified) = StartedFrom(start, match, StartZone);
                return iso is not null
                    ? Present("started", "started", iso, "team file (pid_start)", verified)
                    : Absent("started", "started", "team file (pid_start)", "start time unreadable");
            }
        }

        return Absent("started", "started", "team file (pid_start)", "no start time recorded");
    }

    private static List<DetailField> MemberFields(ModelCtx c, Node node)
    {
        var src = c.Src;
        var orch = node.Orch!;
        var team = node.Team!;
        var facts = node.Facts!;
        var member = node.Member!;
        var name = member.Str("name")!;
        var body = MemberView(src, orch, team, facts, member, new JsonArray(), c.Now, c.MPath, c.Registrations).Body.Obj("body")!;
        var fields = new List<DetailField>
        {
            Present("name", "name", name, TeamFileSource),
            Present("role", "role", body["role"], TeamFileSource),
            Present("model", "model", body["model"], TeamFileSource),
            body["effort"].IsNull ? Absent("effort", "effort", TeamFileSource, "not configured") : Present("effort", "effort", body["effort"], TeamFileSource),
            Present("harness", "harness", body["kind"], TeamFileSource),
            Present("team", "team", c.TeamLabel(team), TeamFileSource),
            Present("repo", "repo", TextOrNull(src, RepoName(orch.Dir), NameCap), "hierarchy directory"),
            Present(
                "state",
                "state",
                new JsonObject { ["live"] = body["live"], ["activity"] = body["activity"], ["blocked_by"] = body["blocked_by"] },
                StatusSource),
            Present("badge", "badge", BadgeOf(body["activity"], member.Str("session_id") is { Length: > 0 } heldSession && c.Held.Value.Contains(heldSession)), StatusSource),
        };

        var sessionId = member.Str("session_id");
        var history = sessionId is { Length: > 0 } sid ? src.ActivityOf(team.Dir, sid) : null;
        fields.Add(history?.StateSince is { } since
            ? Present("state_since", "in this state since", since, "activity record")
            : Absent("state_since", "in this state since", "activity record", history is null ? "no activity record" : "the record predates state_since"));
        fields.Add(Present(
            "doing",
            "doing",
            new JsonObject { ["activity"] = body["activity"], ["at"] = body["activity_at"], ["tool"] = body["last_tool"], ["tool_at"] = body["last_tool_at"] },
            StatusSource));

        var brief = facts.Open.Where(d => d.Str("member") == name).OrderBy(static d => d.Str("sent_at") ?? string.Empty, StringComparer.Ordinal).LastOrDefault();
        var boxEntries = src.BoxEntries(src.BoxDirFor(team.File, name), 10);
        if (brief is null)
        {
            fields.Add(Absent("brief", "brief", "open request", "no open request"));
            fields.Add(Absent("spec", "spec", "request text", "no open request"));
        }
        else
        {
            var briefId = brief.Str("id");
            var entry = boxEntries.FirstOrDefault(e => briefId is not null && e.Str("id") == briefId);
            fields.Add(Present("brief", "brief", new JsonObject { ["id"] = Get(brief, "id"), ["slug"] = Get(brief, "slug"), ["sent_at"] = Get(brief, "sent_at"), ["from"] = entry is null ? JsonValue.Null : Get(entry, "from") }, "open request"));
            var spec = entry?.Str("spec");
            fields.Add(spec is null
                ? Absent("spec", "spec", "request text", entry is null ? "request not among the recent entries" : "the request text names no spec")
                : Present("spec", "spec", TextOrNull(src, spec, 400), "request text"));
        }

        var report = facts.Dispatches.Where(d => d.Str("member") == name && d.Str("reported_at") is { Length: > 0 }).OrderBy(static d => d.Str("reported_at"), StringComparer.Ordinal).LastOrDefault();
        fields.Add(report is null
            ? Absent("report", "latest report", "dispatches", "no report yet")
            : Present("report", "latest report", new JsonObject { ["id"] = Get(report, "id"), ["slug"] = Get(report, "slug"), ["reported_at"] = Get(report, "reported_at") }, "dispatches"));
        fields.Add(HeldField(c, team, name));

        var pct = body["context_pct"];
        var used = body["context_used"];
        fields.Add(pct.IsNull && used.IsNull
            ? Absent("context", "context", StatusSource, "no context reading")
            : Present("context", "context", new JsonObject { ["pct"] = pct, ["used"] = used, ["level"] = body["context_level"] }, StatusSource));
        fields.Add(Absent("context_window", "tokens of window", "model", "window size unknown"));
        fields.Add(Absent("compactions", "compactions", "transcript", "no reader"));
        fields.Add(Absent("tokens", "tokens this session", "transcript usage", "pending usage dedupe (journal E1)"));

        fields.Add(body["work_tree"].IsNull ? Absent("work_tree", "work tree", "registration", "no work tree known") : Present("work_tree", "work tree", body["work_tree"], "team file, registration"));
        fields.Add(body["stream"].IsNull ? Absent("stream", "stream", TeamFileSource, "not in a stream") : Present("stream", "stream", body["stream"], TeamFileSource));
        fields.Add(body["pid"].IsNull ? Absent("pid", "pid", "registration", "no registration") : Present("pid", "pid", body["pid"], "registration"));
        fields.Add(body["session_id"].IsNull ? Absent("session", "session", "status document", "no session recorded") : Present("session", "session", body["session_id"], "status document"));
        fields.Add(Absent("started", "started", "registration", "registration carries no start time"));

        fields.Add(history is null || (history.Minutes.Count == 0 && history.StateSince is null)
            ? Absent("activity", "activity, last 60 minutes", "activity record", history is null ? "no activity record" : "the record predates minute history")
            : Present("activity", "activity, last 60 minutes", ActivityStrip(history, body["live"].AsBool(), body["activity"].IsText && body["activity"].AsString() == "working", ParseMs(body["activity_at"].IsText ? body["activity_at"].AsString() : null), c.Now), "activity record"));

        var rows = boxEntries.Select(e => (Entry: e, Direction: "in", Holder: c.MPath(orch, team, name))).ToList();
        var address = AddressOf(team, name);
        foreach (var other in facts.Members.Select(static m => m.Str("name")!).Where(n => n != name).Append("orchestrator"))
        {
            var holder = other == "orchestrator" ? c.TPath(orch, team) : c.MPath(orch, team, other);
            rows.AddRange(src.BoxEntries(src.BoxDirFor(team.File, other), MailRowsPerDirection, index: true)
                .Where(e => e.Str("from") == address)
                .Select(e => (Entry: e, Direction: "out", Holder: holder)));
        }

        var mail = new JsonArray();
        foreach (var (e, direction, holder) in rows
            .OrderBy(static r => r.Entry.Str("created") ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(static r => r.Entry.Str("id") ?? string.Empty, StringComparer.Ordinal)
            .TakeLast(MailRows))
        {
            mail.Add(new JsonObject
            {
                ["id"] = Get(e, "id"),
                ["time"] = Get(e, "created"),
                ["kind"] = Get(e, "kind"),
                ["from"] = Get(e, "from"),
                ["to"] = Get(e, "to"),
                ["slug"] = SlugOf(e.Str("ref")),
                ["direction"] = direction,
                ["path"] = e.Str("id") is { Length: > 0 } entryId ? $"{holder}/mail/{Seg(entryId)}" : JsonValue.Null,
                ["state"] = e.TryGetValue("acked", out var acked) && acked.Kind == JsonKind.Bool && acked.AsBool() ? "acked" : "waiting",
                ["changed_since_delivery"] = direction == "out" ? JsonValue.Null : e.TryGetValue("changed", out var changed) && changed.Kind == JsonKind.Bool && changed.AsBool(),
            });
        }

        fields.Add(Present("mail", "mail", mail, "mailbox entries"));
        var runs = new JsonArray();
        foreach (var run in c.Runs.Value.Where(r => sessionId is not null && r.SessionId == sessionId).TakeLast(SubagentRows))
        {
            runs.Add(new JsonObject
            {
                ["id"] = run.AgentId,
                ["status"] = run.Status,
                ["started_at"] = run.StartedAt,
                ["ended_at"] = run.EndedAt is null ? JsonValue.Null : run.EndedAt,
            });
        }

        fields.Add(Present("subagents", "subagents", runs, "inflight records"));
        return fields;
    }

    private const int HeldRows = 20;

    /// <summary>The plan items of the member's team it holds or has held, newest first; absent when no progress records can be read.</summary>
    private static DetailField HeldField(ModelCtx c, TeamEntry team, string name)
    {
        const string source = "plan items";
        if ((c.Src as IDetailReads)?.Progress is not { } progress || ReadTeamProgress(progress, team) is not { Off: false } read)
        {
            return Absent("held", "items held", source, "no progress records");
        }

        var teamKey = team.Name ?? "_default";
        var held = read.Streams
            .Where(static s => s.Planned)
            .SelectMany(s => (progress.ReadPlan(team.Dir, teamKey, s.Stream)?.Items ?? []).Where(i => i.Holder == name).Select(i => (s.Stream, Item: i)))
            .OrderByDescending(static x => x.Item.UpdatedAt ?? x.Item.StateSince ?? x.Item.DoneAt ?? x.Item.StartedAt ?? x.Item.AddedAt, StringComparer.Ordinal)
            .Take(HeldRows);
        var items = new JsonArray();
        foreach (var (stream, item) in held)
        {
            items.Add(new JsonObject { ["key"] = item.Item, ["title"] = TextOrNull(c.Src, item.Title, 400), ["state"] = item.State, ["stream"] = stream });
        }

        return Present("held", "items held", items, source);
    }

    /// <summary>
    /// The last 60 one-minute cells ending at the minute of <paramref name="nowMs"/>, oldest first: <c>busy</c>, <c>idle</c>, or
    /// null for no data. Before the earliest minute the record knows of there is no data, and a member that is not live has none
    /// after its last recorded activity. While a live member is busy now, every minute since its state began is busy: no change
    /// of state was recorded in between.
    /// </summary>
    internal static JsonObject ActivityStrip(ActivityHistory history, bool live, bool busyNow, double activityAtMs, double nowMs)
    {
        var end = (long)Math.Floor(nowMs / 60000);
        long? first = history.Minutes.Count > 0 ? history.Minutes[0] : null;
        long? busySince = null;
        if (history.StateSince is { } since && Timestamps.TryParseMs(since, out var sinceMs))
        {
            var sinceMinute = (long)Math.Floor(sinceMs / 60000);
            first = first is { } f ? Math.Min(f, sinceMinute) : sinceMinute;
            busySince = live && busyNow ? sinceMinute : null;
        }

        long? last = null;
        if (!live)
        {
            last = history.Minutes.Count > 0 ? history.Minutes[^1] : null;
            if (!double.IsNaN(activityAtMs))
            {
                var at = (long)Math.Floor(activityAtMs / 60000);
                last = last is { } l ? Math.Max(l, at) : at;
            }
        }

        var busy = history.Minutes.ToHashSet();
        var cells = new JsonArray();
        for (var i = 0; i < 60; i++)
        {
            var minute = end - 59 + i;
            if (first is not { } start || minute < start || (!live && (last is not { } stop || minute > stop)))
            {
                cells.Add(JsonValue.Null);
            }
            else
            {
                cells.Add(busy.Contains(minute) || (busySince is { } from && minute >= from) ? "busy" : "idle");
            }
        }

        return new JsonObject { ["end_minute"] = (double)end, ["cells"] = cells };
    }
}
