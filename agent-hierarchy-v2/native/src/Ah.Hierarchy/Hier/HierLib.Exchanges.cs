using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

internal sealed partial class HierLib
{
    private const string Ws = JsText.SpaceClass;

    [GeneratedRegex("^## \\[[0-9]+\\] ([^" + Ws + "]+)", RegexOptions.CultureInvariant)]
    private static partial Regex AnchorKeyRegex();

    [GeneratedRegex("^- \\[[0-9]+\\] (?<key>goal|acceptance):(?<rest>[^\\n\\r\\u2028\\u2029]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TldrIntentRegex();

    [GeneratedRegex("^[" + Ws + "]*[-*] (?<rest>[^\\n\\r\\u2028\\u2029]*)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex BulletRegex();

    [GeneratedRegex("^[" + Ws + "]*\\[hierarchy-msg [^\\]]*\\][" + Ws + "]*", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPrefixRegex();

    [GeneratedRegex(
        "^[" + Ws + "]*(?:#{1,6}[" + Ws + "]+)?(?:(?:[-*]|[0-9]+\\.)[" + Ws + "]+)?(?:\\[[0-9]+\\][" + Ws + "]+)?(?:[*_]*(?:[Ss][Tt][Aa][Tt][Uu][Ss]|[Vv][Ee][Rr][Dd][Ii][Cc][Tt])[*_]*:[*_]*[" + Ws + "]*)?[*_`]*(NEEDS-DECISION|NEEDS-INTENT|BLOCKED)(?![A-Za-z0-9_-])",
        RegexOptions.CultureInvariant)]
    private static partial Regex OutcomeLineRegex();

    private double CreatedMs(string path)
    {
        var parsed = _core.ReadMsgFile(path);
        var created = parsed.Member("fm")?.Member("created");
        if (created is { IsTruthy: true } c && Timestamps.TryParseMs(JsConvert.ToJsString(c), out var t))
        {
            return t;
        }

        try
        {
            return _core.MsgTimeMs(path) ?? NowMs;
        }
        catch (Exception)
        {
            return NowMs;
        }
    }

    public JsonValue RootOf(IReadOnlyDictionary<string, JsonValue> byId, string startId)
    {
        var seen = new HashSet<string> { startId };
        var id = startId;
        if (!byId.TryGetValue(id, out var fm))
        {
            throw new InvalidOperationException("Cannot read properties of undefined (reading 'parent')");
        }

        for (var hops = 0; ; hops++)
        {
            if (!(fm.Member("parent") is { IsTruthy: true } parent))
            {
                return new JsonObject { ["id"] = id, ["fm"] = fm };
            }

            var parentId = JsConvert.ToJsString(parent);
            if (hops >= 32 || !parent.IsText || !byId.ContainsKey(parentId) || seen.Contains(parentId))
            {
                return JsonValue.Null;
            }

            seen.Add(parentId);
            id = parentId;
            fm = byId[id];
        }
    }

    public bool ChainReaches(string messagePath, string taskId, string dir, string team)
    {
        ArgumentNullException.ThrowIfNull(messagePath);
        ArgumentNullException.ThrowIfNull(taskId);
        if (MessageText.ParseFilename(NodePath.Basename(messagePath)) is not { } start || !_core.MsgExists(messagePath))
        {
            return false;
        }

        var pools = PoolDirs(dir, [team]);
        var home = PoolKey(NodePath.Dirname(NodePath.Dirname(messagePath)));
        if (!pools.Any(p => PoolKey(p) == home))
        {
            return false;
        }

        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var pool in pools)
        {
            foreach (var (path, meta) in RequestFiles(pool))
            {
                if (meta.Type == "request" && !byId.ContainsKey(meta.Id) && _core.ReadMsgFile(path).Member("fm") is { IsMap: true } fm)
                {
                    byId[meta.Id] = fm;
                }
            }
        }

        if (!byId.TryGetValue(taskId, out var task) || !byId.ContainsKey(start.Id))
        {
            return false;
        }

        // The task is made a root of its own, so the walk stops at it instead of running on to whatever dispatched it.
        var asRoot = task.AsObject().Clone();
        asRoot["parent"] = JsonValue.Null;
        byId[taskId] = asRoot;
        return RootOf(byId, start.Id) is { IsNull: false } root && root.Member("id") is { } id && id.AsString() == taskId;
    }

    private static bool BothNamed(JsonValue a, JsonValue b) => Member(a, "from_name").IsTruthy && Member(b, "from_name").IsTruthy;

    private static bool StrictEqualsU(JsonValue? a, JsonValue? b) => a is null || b is null ? a is null && b is null : StrictEquals(a.Value, b.Value);

    private static void Put(JsonObject target, string key, JsonValue? value)
    {
        if (value is { } v)
        {
            target[key] = v;
        }
    }

    private static bool SameSender(JsonValue a, JsonValue b) =>
        BothNamed(a, b) ? StrictEqualsU(a.Member("from_name"), b.Member("from_name")) : StrictEqualsU(a.Member("from"), b.Member("from"));

    public JsonArray ListDownstreamDispatches(string dir)
    {
        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        var order = new List<string>();
        var requests = RequestFiles(dir).Where(static f => f.Meta.Type == "request").ToList();
        var parsedAll = _core.ReadMsgFiles([.. requests.Select(static f => f.Path)]);
        for (var i = 0; i < requests.Count; i++)
        {
            var meta = requests[i].Meta;
            if (parsedAll[i].Member("fm") is { IsMap: true } fm)
            {
                if (!byId.ContainsKey(meta.Id))
                {
                    order.Add(meta.Id);
                }

                byId[meta.Id] = fm;
            }
        }

        return DownstreamOf(byId, order);
    }

    /// <summary>
    /// <see cref="ListDownstreamDispatches(string)"/> over the message rows (Orchestrator ruling on ah-msgs-fast-2 §3.4): with
    /// <paramref name="newest"/> 0 the requests of the live scope and their descendants, else the newest <paramref name="newest"/> requests;
    /// each with its parent chain read by id from the rows, no request file read.
    /// </summary>
    public JsonArray ListDownstreamDispatches(string dir, int newest)
    {
        var rows = _core.DownstreamRequests(dir, newest);
        static JsonValue Text(string? s) => s is null ? JsonValue.Null : JsonValue.FromString(s);
        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var r in rows.Rows.OrderBy(static r => r.Name, StringComparer.Ordinal))
        {
            // the headers every written request carries; one that is empty reads null, as in its front matter
            byId[r.Id] = new JsonObject
            {
                ["from"] = Text(r.From), ["from_name"] = Text(r.FromName), ["to"] = r.To, ["to_name"] = Text(r.ToName),
                ["slug"] = r.Slug, ["created"] = Text(r.Created), ["parent"] = Text(r.Parent),
            };
        }

        return DownstreamOf(byId, [.. rows.Seeds.Where(byId.ContainsKey).Order(StringComparer.Ordinal)]);
    }

    private JsonArray DownstreamOf(Dictionary<string, JsonValue> byId, List<string> order)
    {
        var found = new List<JsonObject>();
        foreach (var id in order)
        {
            var fm = byId[id];
            if (!Member(fm, "parent").IsTruthy)
            {
                continue;
            }

            var root = RootOf(byId, id);
            if (root.IsNull || root.Member("id") is { } rid && rid.AsString() == id)
            {
                continue;
            }

            var rootFm = root.Member("fm")!.Value;
            if (SameSender(rootFm, fm))
            {
                continue;
            }

            var item = new JsonObject { ["id"] = id, ["parent"] = Member(fm, "parent"), ["root_id"] = root.Member("id")!.Value };
            Put(item, "root_from", rootFm.Member("from"));
            Put(item, "root_from_name", rootFm.Member("from_name"));
            Put(item, "from", fm.Member("from"));
            Put(item, "from_name", fm.Member("from_name"));
            Put(item, "to", fm.Member("to"));
            Put(item, "to_name", fm.Member("to_name"));
            Put(item, "slug", fm.Member("slug"));
            Put(item, "created", fm.Member("created"));
            item["identity"] = BothNamed(rootFm, fm) ? "name" : "role-only";
            found.Add(item);
        }

        found.Sort(static (a, b) => string.CompareOrdinal(a["id"].AsString(), b["id"].AsString()) < 0 ? 1 : -1);
        var result = new JsonArray();
        foreach (var item in found)
        {
            result.Add(item);
        }

        return result;
    }

    public JsonArray OpenExchanges(string dir, JsonValue? team, JsonArray? exchanges = null)
    {
        var source = exchanges ?? _core.ListExchanges([dir], live: true);
        var open = new JsonArray();
        foreach (var e in source)
        {
            if (Member(e, "open").IsTruthy)
            {
                open.Add(e);
            }
        }

        if (team is null)
        {
            return open;
        }

        return KeptForTeam(open, team.Value, StoredTeamTags([.. open]));
    }

    /// <summary>The exchanges of <paramref name="open"/> whose team tag (<paramref name="tags"/>, by position) is <paramref name="team"/>.</summary>
    private static JsonArray KeptForTeam(IReadOnlyList<JsonValue> open, JsonValue team, IReadOnlyList<JsonValue> tags)
    {
        var wanted = Or(team, JsonValue.Null);
        var kept = new JsonArray();
        for (var i = 0; i < open.Count; i++)
        {
            if (StrictEquals(tags[i], wanted))
            {
                kept.Add(open[i]);
            }
        }

        return kept;
    }

    /// <summary>Each exchange's team tag: in db mode the stored team header; a request with no row is read from its file.</summary>
    private List<JsonValue> StoredTeamTags(List<JsonValue> exchanges)
    {
        var paths = exchanges.Select(e => TextOf(Member(Member(e, "request"), "path"))).ToList();
        var stored = _core.MsgTeams([.. paths.Where(static p => p is not null).Select(static p => p!)]);
        var unread = paths.Where(p => p is not null && (stored is null || !stored.ContainsKey(p))).Select(static p => p!).ToList();
        var parsedAll = _core.ReadMsgFiles(unread);
        var next = 0;
        var tags = new List<JsonValue>();
        for (var i = 0; i < exchanges.Count; i++)
        {
            if (paths[i] is { } path && stored is not null && stored.TryGetValue(path, out var storedTeam))
            {
                tags.Add(storedTeam is null ? JsonValue.Null : JsonValue.FromString(storedTeam));
            }
            else
            {
                var parsed = paths[i] is null ? JsonValue.Null : parsedAll[next++];
                tags.Add(Or(parsed.Member("fm")?.Member("team"), JsonValue.Null));
            }
        }

        return tags;
    }

    /// <summary><see cref="OpenRunAnchor"/> over <paramref name="exchanges"/> with team tags from <paramref name="source"/>.</summary>
    private static JsonObject OpenRunAnchorOf(IStatusSource source, JsonValue team, JsonArray exchanges)
    {
        var open = exchanges.Where(static e => Member(e, "open").IsTruthy).ToList();
        return RunAnchorOf(KeptForTeam(open, team, source.TeamTags(open)));
    }

    public bool PipelineRunLive(string? cwd)
    {
        var dirs = new List<string>();
        foreach (var d in new[] { _config.HierarchyDir(cwd), _config.MainHierarchyDir(cwd) })
        {
            if (!string.IsNullOrEmpty(d) && !dirs.Contains(d))
            {
                dirs.Add(d);
            }
        }

        return dirs.Any(dir => OpenExchanges(dir, null, ListLiveExchangesAllTeams(dir)).Any(e => Member(e, "slug") is { IsText: true } s && s.AsString() == HierarchyModule.PipelineAnchorSlug));
    }

    public int Sweep(string dir, double? days = null, double? now = null, bool hook = false)
    {
        var cutoff = (now ?? NowMs) - ((days ?? HierarchyModule.SweepDays) * 86400 * 1000);
        var files = new JsonArray();
        var pairs = 0;
        foreach (var e in _core.ListExchanges(dir))
        {
            if (Member(e, "open").IsTruthy)
            {
                continue;
            }

            var responsePath = Member(Member(e, "response"), "path").AsString();
            if (CreatedMs(responsePath) > cutoff)
            {
                continue;
            }

            files.Add(Member(Member(e, "request"), "path"));
            files.Add(responsePath);
            pairs++;
        }

        if (pairs > 0)
        {
            Call(PoolContext(dir), "msgs", "archive", new JsonObject { ["dir"] = dir, ["files"] = files }, hook);
        }

        SweepActivityRouted(dir, cutoff, hook);
        return pairs;
    }

    public string? AutoTitle(string text) => MessageText.AutoTitle(text);

    public string? TitleRefusal(string raw) => MessageText.CheckTitle(raw).Refusal;

    public (string? Title, string? Source) ExchangeTitle(JsonValue exchange)
    {
        if (exchange.Member("request")?.Member("path") is not { IsText: true } path || _core.ReadMsgFile(path.AsString()) is not { IsMap: true } read)
        {
            return (null, null);
        }

        return TitleOfText(read.Member("text") is { IsText: true } t ? t.AsString() : string.Empty);
    }

    /// <summary>The title of a request from its text: the front matter's <c>title</c>, else one resolved from the body's first 4 KiB.</summary>
    internal static (string? Title, string? Source) TitleOfText(string text)
    {
        var fm = MessageText.ParseFrontmatter(text);
        var title = fm is { } f && f.Fields.Get("title") is { IsText: true } ft ? ft.AsString() : null;
        var body = fm is { } g ? string.Join('\n', text.Split('\n').Skip(g.End)) : string.Empty;
        return MessageText.ResolveTitle(title, body.Length > 4096 ? body[..4096] : body);
    }

    public double ExchangeAgeSec(JsonValue exchange, double? now = null) =>
        Math.Max(0, ((now ?? NowMs) - CreatedMs(Member(Member(exchange, "request"), "path").AsString())) / 1000);

    /// <summary>The key of <paramref name="text"/>'s first token resolved in <paramref name="dir"/>'s pool, or why it has none: no token, an unknown id, or an id more than one message has.</summary>
    private (string? Key, string? Why) TokenKey(JsonValue? text, string dir, string? expectedTo)
    {
        var hit = _core.ResolveMsgToken(TextOf(text), dir, expectedTo);
        return hit switch
        {
            null => (null, "missing token"),
            { Key: { } key } => (key, null),
            { Candidates.Count: 0 } => (null, $"no {hit.Type} {hit.Word}"),
            _ => (null, $"{hit.Type} {hit.Word} is in more than one place ({string.Join("; ", hit.Candidates.Select(static c => $"to {c.To} in pool {c.Pool}"))})"),
        };
    }

    public JsonValue ValidateRequestToken(JsonValue? text, string dir, JsonValue? expectedTo)
    {
        JsonValue Fail(string why) => new JsonObject { ["ok"] = false, ["why"] = why };
        var (path, unresolved) = TokenKey(text, dir, TextOf(expectedTo));
        if (path is null)
        {
            return Fail(unresolved!);
        }

        if (!NodePath.IsAbsolute(path) || !_core.MsgExists(path))
        {
            return Fail($"path not found ({path})");
        }

        if (!path.EndsWith("--request.md", StringComparison.Ordinal))
        {
            return Fail("not a request file");
        }

        var parsed = _core.ReadMsgFile(path);
        var fm = parsed.Member("fm");
        if (!IsUnder(path, MsgsDir(dir)) && MessageHome(path, fm) is null)
        {
            return Fail(OutsidePool(dir));
        }

        if (!(fm is { IsMap: true } && Member(fm, "type") is { IsText: true } type && type.AsString() == "request"))
        {
            return Fail("not a request file");
        }

        if (expectedTo is { IsTruthy: true } want && !StrictEquals(Member(fm, "to"), want))
        {
            return Fail($"wrong to: (file says {JsConvert.ToJsString(Member(fm, "to"))}, dispatch is {JsConvert.ToJsString(want)})");
        }

        return new JsonObject { ["ok"] = true, ["path"] = path, ["fm"] = fm!.Value };
    }

    private string OutsidePool(string dir) =>
        $"file is outside this session's message pool ({MsgsDir(dir)}) — the pool follows the session cwd; if cwd moved into a worktree or another repo, the file is fine and the cwd is wrong";

    private static string OutsideRequestDir(string reqDir, string reqPath) =>
        $"response is not in its request's directory ({reqDir}) — file it with `ah msg fill --id {Ah.Core.Json.MsgPointer.Id(reqPath) ?? "<id>"} --from <your role> --body-file -`, which places it beside the request";

    public JsonValue ValidateResponseToken(JsonValue? text, string dir, JsonValue? expectedFrom, JsonValue? expectedId, JsonValue? requestPath = null)
    {
        JsonValue Fail(string why) => new JsonObject { ["ok"] = false, ["why"] = why };
        var (path, unresolved) = TokenKey(text, dir, null);
        if (path is null)
        {
            return Fail(unresolved!);
        }

        if (!NodePath.IsAbsolute(path) || !_core.MsgExists(path))
        {
            return Fail($"path not found ({path})");
        }

        if (!path.EndsWith("--response.md", StringComparison.Ordinal))
        {
            return Fail("not a response file");
        }

        var parsed = _core.ReadMsgFile(path);
        var fm = parsed.Member("fm");
        if (!IsUnder(path, MsgsDir(dir)) && MessageHome(path, fm) is null)
        {
            var reqPath = TextOf(requestPath);
            var anchored = reqPath is not null && NodePath.IsAbsolute(reqPath) && reqPath.EndsWith("--request.md", StringComparison.Ordinal) && _core.MsgExists(reqPath);
            if (!anchored)
            {
                return Fail(OutsidePool(dir));
            }

            // The directories resolve, not the paths: a stored message has no file for a realpath to follow.
            var reqDir = RealCwd(NodePath.Dirname(reqPath!));
            if (RealCwd(NodePath.Dirname(path)) != reqDir)
            {
                return Fail(OutsideRequestDir(reqDir, reqPath!));
            }
        }

        if (!(fm is { IsMap: true } && Member(fm, "type") is { IsText: true } type && type.AsString() == "response"))
        {
            return Fail("not a response file");
        }

        if (expectedFrom is { IsTruthy: true } from && !StrictEquals(Member(fm, "from"), from))
        {
            return Fail($"wrong from: (file says {JsConvert.ToJsString(Member(fm, "from"))}, expected {JsConvert.ToJsString(from)})");
        }

        if (expectedId is { IsTruthy: true } id && !StrictEquals(Member(fm, "id"), id))
        {
            return Fail($"wrong id: (file says {JsConvert.ToJsString(Member(fm, "id"))}, expected {JsConvert.ToJsString(id)})");
        }

        return new JsonObject { ["ok"] = true, ["path"] = path, ["fm"] = fm!.Value };
    }

    /// <summary>A cwd realpath-normalised for comparison; the path itself when it does not resolve.</summary>
    public string RealCwd(string path)
    {
        if (path.Length == 0)
        {
            return path;
        }

        try
        {
            return _files.RealPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static bool BlankIntent(string value) => Array.IndexOf(EmptyIntent, JsLower.Lower(JsText.Trim(value))) >= 0;

    public JsonArray MissingIntent(JsonValue parsed)
    {
        var text = TextOf(parsed.Member("text")) ?? throw new InvalidOperationException("Cannot read properties of undefined (reading 'split')");
        var lines = text.Split('\n');
        bool Filled(string key)
        {
            string? section = null;
            var firstLineSeen = false;
            foreach (var line in lines)
            {
                var a = AnchorKeyRegex().Match(line);
                if (a.Success)
                {
                    section = JsLower.Lower(a.Groups[1].Value);
                    firstLineSeen = false;
                    continue;
                }

                if (section == key && !firstLineSeen && JsText.Trim(line).Length > 0)
                {
                    firstLineSeen = true;
                    var trimmed = JsText.Trim(line);
                    if (trimmed[0] != '#' && !BlankIntent(BulletRegex().Match(line) is { Success: true } lead ? lead.Groups["rest"].Value : trimmed))
                    {
                        return true;
                    }
                }

                if (section == "tldr")
                {
                    var m = TldrIntentRegex().Match(line);
                    if (m.Success && m.Groups["key"].Value == key && !BlankIntent(m.Groups["rest"].Value))
                    {
                        return true;
                    }
                }
                else if (section == key)
                {
                    var b = BulletRegex().Match(line);
                    if (b.Success && !BlankIntent(b.Groups["rest"].Value))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        var missing = new JsonArray();
        foreach (var key in new[] { "goal", "acceptance" })
        {
            if (!Filled(key))
            {
                missing.Add(key);
            }
        }

        return missing;
    }

    /// <summary>
    /// Whether the request is a checkpoint or release ask still exactly as <c>msg new</c> generated it. The slug alone proves nothing,
    /// because any author can choose it, so the body must also equal the generated skeleton.
    /// </summary>
    private static bool IsSystemRequest(JsonValue fm, JsonValue parsed)
    {
        if (Member(fm, "slug") is not { IsText: true } slug || slug.AsString() is not (CheckpointSlug or ReleaseSlug)
            || TextOf(parsed.Member("text")) is not { } text)
        {
            return false;
        }

        var body = BodyAfterFrontmatter(text);
        return new string?[] { null, CheckpointAskReadOnly, CheckpointAskNoTree }.Any(ask => body == JsText.Trim(BodyAfterFrontmatter(true, slug, null, ask)));
    }

    public JsonValue ReviewIntentGap(string path, JsonObject? resolved)
    {
        try
        {
            var parsed = _core.ReadMsgFile(path);
            var fm = parsed.Member("fm");
            if (!(fm is { IsMap: true } && Member(fm, "type") is { IsText: true } type && type.AsString() == "request"
                  && Member(fm, "to") is { IsText: true } to && _config.RoleClass(to.AsString(), resolved) == "review"
                  && !IsSystemRequest(fm.Value, parsed)))
            {
                return JsonValue.Null;
            }

            var missing = MissingIntent(parsed);
            return missing.Count > 0 ? missing : JsonValue.Null;
        }
        catch (Exception)
        {
            return JsonValue.Null;
        }
    }

    // The message is a stored row, not a file a person can edit, so the reason names the verb that amends it.
    public string ReviewIntentReason(JsonValue? path, JsonArray missing, JsonValue? again) =>
        ReviewIntentReasonFor(MessageText.ParseFilename(NodePath.Basename(JsConvert.ToJsString(path)))?.Id ?? "<id>", missing, again);

    private static string ReviewIntentReasonFor(string id, JsonArray missing, JsonValue? again) =>
        $"ah: a Reviewer brief needs its intent. Fill [1] goal (what the change must deliver, and its source: spec path, ticket or PR text) and [5] acceptance of request {id} with `ah msg fill --id {id} --from <your role> --request --section <1 or 5> --body-file -`, then {JsConvert.ToJsString(again)}. Missing: {string.Join(", ", missing.Select(m => JsConvert.ToJsString(m)))}. Minimal accepted shape (either form):\n  ## [1] goal\n  - <what the change must deliver, and its source>\n  ## [5] acceptance\n  - <how the reviewer decides it is met>\nor, in ## [0] tldr:  - [1] goal: <…>   - [5] acceptance: <…>";

    private static string BodyAfterFrontmatter(string text)
    {
        var fm = MessageText.ParseFrontmatter(text);
        return fm is null ? JsText.Trim(text) : JsText.Trim(string.Join('\n', text.Split('\n').Skip(fm.Value.End)));
    }

    public string ReportStatus(string path, string id)
    {
        if (TryReadMsg(path) is not { } text)
        {
            return "no-report";
        }

        var fm = MessageText.ParseFrontmatter(text);
        if (fm is null || !(fm.Value.Fields.Get("id") is { IsText: true } fid && fid.AsString() == id))
        {
            return "malformed-report";
        }

        return MessageText.HasAuthoredContent(text) ? "reported" : "no-report";
    }

    public JsonArray OutcomeTokens(JsonValue? text)
    {
        var tokens = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var source = text is { IsTruthy: true } t ? JsConvert.ToJsString(t) : string.Empty;
        foreach (var raw in source.Split('\n'))
        {
            var m = OutcomeLineRegex().Match(TokenPrefixRegex().Replace(raw, string.Empty, 1));
            if (m.Success && seen.Add(m.Groups[1].Value))
            {
                tokens.Add(m.Groups[1].Value);
            }
        }

        return tokens;
    }

    public string FirstSection(JsonValue? text)
    {
        var lines = (text is { IsTruthy: true } t ? JsConvert.ToJsString(t) : string.Empty).Split('\n');
        var first = Array.FindIndex(lines, l => l.StartsWith("## [", StringComparison.Ordinal));
        if (first < 0)
        {
            return string.Empty;
        }

        var next = -1;
        for (var i = first + 1; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("## [", StringComparison.Ordinal))
            {
                next = i;
                break;
            }
        }

        return string.Join('\n', next < 0 ? lines.Skip(first) : lines.Skip(first).Take(next - first));
    }

    public JsonValue StatusFollowUp(JsonValue? text)
    {
        var tokens = OutcomeTokens(FirstSection(text));
        return tokens.Count > 0 ? tokens[0] : JsonValue.Null;
    }

    public string FollowUpRule(JsonValue? status, JsonValue? id)
    {
        var parent = id is { IsTruthy: true } i ? $"--parent {JsConvert.ToJsString(i)}" : "--parent <id>";
        if (status is { IsText: true } s && s.AsString() == "NEEDS-INTENT")
        {
            return $"This report says NEEDS-INTENT: supply the intent and re-dispatch as a new request ({parent}); it is not a review round-trip.";
        }

        return $"This report says {JsConvert.ToJsString(status)}: answer it with a new request ({parent}), never by re-sending the old one.";
    }

    public JsonArray FollowUpRules(JsonValue? options)
    {
        var tokens = OutcomeTokens(options?.Member("delivered") is { } d && !d.IsNull ? d : (JsonValue)string.Empty);
        JsonValue id = JsonValue.Null;
        var idSet = false;
        var files = options?.Member("files") is { IsArray: true } f ? f.AsArray() : new JsonArray();
        foreach (var file in files)
        {
            var path = file.IsText ? file.AsString() : JsConvert.ToJsString(file);
            if (TryReadMsg(path) is not { } text)
            {
                continue;
            }

            foreach (var t in OutcomeTokens(FirstSection(text)))
            {
                if (!tokens.Any(x => x.AsString() == t.AsString()))
                {
                    tokens.Add(t);
                }
            }

            if (!idSet)
            {
                var meta = MessageText.ParseFilename(path);
                if (meta is not null)
                {
                    id = meta.Id;
                    idSet = true;
                }
            }
        }

        var rules = new JsonArray();
        foreach (var t in tokens)
        {
            rules.Add(FollowUpRule(t, idSet ? id : JsonValue.Null));
        }

        return rules;
    }

    public JsonValue ReportFollowUp(JsonValue? path)
    {
        if (path is not { IsText: true } p || !p.AsString().EndsWith("--response.md", StringComparison.Ordinal))
        {
            return JsonValue.Null;
        }

        var rules = FollowUpRules(new JsonObject { ["files"] = new JsonArray { p } });
        return rules.Count > 0 ? string.Join(' ', rules.Select(r => r.AsString())) : JsonValue.Null;
    }

    public bool HasResponseToken(JsonValue? text, JsonValue? id)
    {
        var path = _core.ExtractMsgToken(TextOf(text));
        if (path is null || !path.EndsWith("--response.md", StringComparison.Ordinal) || !_core.MsgExists(path))
        {
            return false;
        }

        if (id is { IsTruthy: true } want)
        {
            var meta = MessageText.ParseFilename(path);
            if (meta is null || !(want.IsText && meta.Id == want.AsString()))
            {
                return false;
            }
        }

        return TryReadMsg(path) is { } body && HasAuthored(BodyAfterFrontmatter(body));
    }

    private static bool HasAuthored(string body) => MessageText.HasAuthoredBody(body);

    /// <summary>Whether the response file holds authored content and is no withdrawal: the part of <see cref="ResponseLanded"/> that does not wait for the file to be quiet.</summary>
    public bool ResponseAuthored(string path)
    {
        try
        {
            using var db = _core.DbMsgs();
            var text = HierarchyCore.ReadMsgText(path, db);
            return HasAuthored(BodyAfterFrontmatter(text)) && !IsWithdrawal(text);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public bool ResponseLanded(string path, double? now = null) => TryResponseLanded(path, now) ?? false;

    public bool? TryResponseLanded(string path, double? now = null)
    {
        try
        {
            // In the store a fill is atomic, so there is no half-written draft to wait out.
            var text = _core.ReadMsgTextShared(path);
            return HasAuthored(BodyAfterFrontmatter(text)) && !IsWithdrawal(text);
        }
        catch (Exception e) when (e is not StoreUnavailableException)
        {
            return null;
        }
    }

    /// <summary>Whether a response retires its request (<c>withdrawn: true</c> by the sender, <c>closed: true</c> by the Orchestrator), which is no report from the addressee.</summary>
    private static bool IsWithdrawal(string text) =>
        MessageText.ParseFrontmatter(text) is { } fm
        && (fm.Fields.Get("withdrawn") is { IsText: true } w && w.AsString() == "true" || fm.Fields.Get("closed") is { IsText: true } c && c.AsString() == "true");
}
