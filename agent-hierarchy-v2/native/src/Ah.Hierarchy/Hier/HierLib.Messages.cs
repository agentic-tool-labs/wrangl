using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Hierarchy;

internal sealed partial class HierLib : IHierarchyMessages
{
    internal static readonly string[] MsgTypes = ["request", "response"];

    /// <summary>The reason of a message a hook filed for a bare SendMessage; accepted when given, but not offered as a choice to the CLI.</summary>
    internal const string AutoTrackedReason = "auto-tracked";

    internal static readonly string[] Reasons = ["context", "second-opinion", "parallel"];

    internal static readonly string[] Etas = ["small", "medium", "large"];

    internal static readonly string[] RequestKeys = ["tldr", "goal", "context", "constraints", "files", "acceptance", "want_back"];

    internal static readonly string[] ResponseKeys = ["tldr", "status", "changes", "evidence", "gaps", "open_questions"];

    private static readonly string[] FrontmatterOrder =
        ["id", "type", "to", "from", "slug", "parent", "reason", "eta", "to_name", "from_name", "team", "team_file", "team_guide", "created"];

    private static readonly string[] EmptyIntent = ["", "none", "n/a", "na", "-", "tbd"];

    // fewer-turns S3.2: an unset eta counts as medium; an unknown one stays small.
    public string EtaOf(JsonValue? eta) => EtaName(eta);

    // static: the status compute calls these, and it may not reach an instance (b459 C1, guard G6)
    internal static string EtaName(JsonValue? eta) =>
        eta is { IsText: true } t && Array.IndexOf(Etas, t.AsString()) >= 0 ? t.AsString()
        : eta is null || eta.Value.IsNull || eta is { IsText: true } e && e.AsString().Length == 0 ? "medium" : "small";

    public double ThresholdFor(JsonValue? eta) => EtaThreshold(eta);

    internal static double EtaThreshold(JsonValue? eta)
    {
        var name = EtaName(eta);
        foreach (var (eta2, seconds) in HierarchyModule.EtaThresholdSeconds)
        {
            if (eta2 == name)
            {
                return seconds;
            }
        }

        throw new InvalidOperationException("unreachable: EtaOf names a known eta");
    }

    public string MsgFilename(JsonValue? parts)
    {
        if (parts is null || parts.Value.IsNull)
        {
            throw new InvalidOperationException("Cannot destructure a null or undefined value");
        }

        return $"{JsConvert.ToJsString(parts.Value.Member("id"))}--{JsConvert.ToJsString(parts.Value.Member("to"))}--{JsConvert.ToJsString(parts.Value.Member("slug"))}--{JsConvert.ToJsString(parts.Value.Member("type"))}.md";
    }

    public string MsgsDir(string dir) => NodePath.Join(dir, "msgs");

    public string ArchiveDir(string dir) => NodePath.Join(dir, "msgs", "archive");

    public string SpecsDir(string dir) => NodePath.Join(dir, "specs");

    public string PeersPath(string dir) => NodePath.Join(dir, "peers.jsonl");

    public string GatesPath(string dir) => NodePath.Join(dir, "gates.jsonl");

    public JsonArray ReadJsonl(string path)
    {
        var rows = new JsonArray();
        if (!Exists(path) || TryRead(path) is not { } raw)
        {
            return rows;
        }

        foreach (var line in raw.Split('\n'))
        {
            if (JsText.Trim(line).Length == 0)
            {
                continue;
            }

            try
            {
                var rec = JsonText.Parse(line);
                if (rec.IsMap || rec.IsArray)
                {
                    rows.Add(rec);
                }
            }
            catch (JsonParseException)
            {
                // corrupt line: skip
            }
        }

        return rows;
    }

    public string LocalIso(DateTimeOffset? now = null) => Timestamps.LocalIso(_clock, now ?? _clock.UtcNow);

    public string FmtAge(JsonValue? seconds) => seconds is { IsNumber: true } n ? Age.Format(n.AsNumber()) : "?";

    public double AgeSecOf(JsonValue? timestamp, double? now = null)
    {
        var text = timestamp is null ? null : JsConvert.ToJsString(timestamp);
        return Timestamps.TryParseMs(text, out var t) ? Math.Max(0, ((now ?? NowMs) - t) / 1000) : double.PositiveInfinity;
    }

    public JsonValue ParseFrontmatter(JsonValue? text)
    {
        if (text is not { IsText: true } t || MessageText.ParseFrontmatter(t.AsString()) is not { } fm)
        {
            return JsonValue.Null;
        }

        return new JsonObject { ["fields"] = fm.Fields, ["end"] = fm.End };
    }

    public JsonArray IndexAnchors(string text) => MessageText.IndexAnchors(text);

    public string? MessageHome(string messagePath, JsonValue? frontmatter)
    {
        var teamFile = TextOf(Member(frontmatter, "team_file"));
        if (string.IsNullOrEmpty(teamFile) || !NodePath.IsAbsolute(teamFile) || !_roster.TeamExistsAt(teamFile))
        {
            return null;
        }

        var home = Ah.Roster.RosterModule.TeamFileHome(teamFile);
        return !string.IsNullOrEmpty(home) && IsUnder(messagePath, MsgsDir(home)) ? Resolve(home) : null;
    }

    private List<(string Path, MsgFilenameParts Meta)> RequestFiles(string dir)
    {
        using var db = _core.DbMsgs();
        return [.. db.Messages(dir).Select(static m => (m.Path, new MsgFilenameParts(m.Id, m.To, m.Slug, m.Type)))];
    }

    private static JsonObject MetaObject(MsgFilenameParts m) =>
        new() { ["id"] = m.Id, ["to"] = m.To, ["slug"] = m.Slug, ["type"] = m.Type };

    public JsonValue FindByIdAndType(string dir, string id, string type)
    {
        using var db = _core.DbMsgs();
        return db.Messages(dir, id, type) is [var first, ..]
            ? new JsonObject { ["path"] = first.Path, ["meta"] = MetaObject(new MsgFilenameParts(first.Id, first.To, first.Slug, first.Type)) }
            : JsonValue.Null;
    }

    public JsonArray ListMessages(string pool, bool includeArchive, IReadOnlyList<string>? kinds)
    {
        using var db = _core.DbMsgs();
        var rows = includeArchive ? db.Messages(pool).Concat(db.ArchivedMessages(pool)) : db.Messages(pool);
        var found = rows.Select(static m => (m.Archived, m.Path, Meta: new MsgFilenameParts(m.Id, m.To, m.Slug, m.Type)));
        var list = new JsonArray();
        foreach (var (archived, path, meta) in found
            .Where(f => kinds is null || kinds.Contains(f.Meta.Type, StringComparer.Ordinal))
            .OrderBy(f => f.Archived)
            .ThenBy(f => f.Meta.Id, StringComparer.Ordinal).ThenBy(f => f.Meta.Type, StringComparer.Ordinal)
            .ThenBy(f => f.Meta.To, StringComparer.Ordinal).ThenBy(f => f.Meta.Slug, StringComparer.Ordinal))
        {
            list.Add(new JsonObject { ["path"] = path, ["meta"] = MetaObject(meta), ["archived"] = archived });
        }

        return list;
    }

    private static string FrontmatterText(JsonObject fields)
    {
        // ah2-49: the one place values become front-matter lines; a control character (a newline above all) would start a line of
        // the caller's choosing, such as a second `from:`, so every text value loses them here, and in place, so the stored row agrees
        foreach (var key in fields.Keys.ToList())
        {
            if (fields.Get(key) is { IsText: true } text && text.AsString().Any(char.IsControl))
            {
                fields[key] = string.Concat(text.AsString().Where(c => !char.IsControl(c)));
            }
        }

        var lines = new List<string> { "---" };
        foreach (var key in FrontmatterOrder)
        {
            var v = fields.Get(key);
            lines.Add($"{key}: {(v is null || v.Value.IsNull ? "null" : JsConvert.ToJsString(v))}");

            if (key == "slug" && fields.Get("title") is { IsText: true } title && title.AsString().Length > 0)
            {
                lines.Add($"title: {title.AsString()}");
            }
        }

        if (fields.Get("after") is { IsText: true } after && after.AsString().Length > 0)
        {
            lines.Add($"after: {after.AsString()}");
        }

        if (fields.Get("item") is { IsText: true } item && item.AsString().Length > 0)
        {
            lines.Add($"item: {item.AsString()}");
        }

        // ah2-53: how a request serves its team's goal; written only when set
        if (fields.Get("serves") is { IsText: true } serves && serves.AsString().Length > 0)
        {
            lines.Add($"serves: {serves.AsString()}");
        }

        if (fields.Get("from_leads") is { IsText: true } leadsOf && leadsOf.AsString().Length > 0)
        {
            lines.Add($"from_leads: {leadsOf.AsString()}");
        }

        if (fields.Get("withdrawn") is { IsTruthy: true })
        {
            lines.Add("withdrawn: true");
        }

        if (fields.Get("closed") is { IsTruthy: true })
        {
            lines.Add("closed: true");
        }

        if (fields.Get("closed_by") is { IsText: true } closedBy)
        {
            lines.Add("closed_by: " + closedBy.AsString());
        }

        if (fields.Get("closed_reason") is { IsText: true } closedReason)
        {
            lines.Add("closed_reason: " + closedReason.AsString());
        }

        lines.Add("---");
        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    /// <summary>The slug of a release check: the Orchestrator asks a member whether it can be closed, and the member answers in the status section of its response.</summary>
    internal const string ReleaseSlug = "release";

    private const string ReleaseAsk =
        "- Release check: reply `Release: ready branch=<branch> sha=<sha> inflight=none` only if nothing is in flight (no running subagent, background shell or monitor) and your work is committed; otherwise reply `Release: not-ready reason=<one line>`. Put the line under `## [1] status` of your response.";

    /// <summary>The body of a release request or its response: the fixed ask in the request's goal, the answer line placed first under the response's status heading.</summary>
    private static string ReleaseSkeleton(string skeleton, bool request) => request
        ? skeleton.Replace("## [1] goal\n- none", "## [1] goal\n" + ReleaseAsk, StringComparison.Ordinal)
        : skeleton.Replace("## [1] status\n", "## [1] status\n" + MessageText.ReleaseAnswerPlaceholder + "\n", StringComparison.Ordinal);

    /// <summary>The slug of a checkpoint request: the Orchestrator asks a member to leave its work in a state a restart cannot lose.</summary>
    internal const string CheckpointSlug = "checkpoint";

    private const string CheckpointAsk =
        "- Checkpoint request: the team is about to be restarted. Commit your work on your own branch as `wip(resume): <your open request's slug, or idle>`, with these lines in the body: `Request: <open request id or none>`, `Where: <one line>`, `Next: <one line>`, `Processes: <stopped: … | none>` and, if useful, `Notes:`. `git add` your own files only, untracked work you need included. Do not push. If a commit is impossible (a merge or rebase is in progress), answer not-ready.\n"
        + "- Stop every background process you started (headless Godot runs, servers, watchers) and name them in `Processes:`.\n"
        + "- Reply `Checkpoint: ready branch=<branch> sha=<sha> inflight=none` only after that, or `Checkpoint: not-ready reason=<one line>`. Put the line first under `## [1] status` of your response.\n"
        + "- Then stop working until you are resumed; a new instruction supersedes this.";

    /// <summary>The <c>checkpointAsk</c> option of a member that cannot commit: its response file is its handoff and the gate reads its work tree.</summary>
    internal const string CheckpointAskReadOnly = "read-only";

    /// <summary>The <c>checkpointAsk</c> option of a member with no work tree.</summary>
    internal const string CheckpointAskNoTree = "no-tree";

    private const string CheckpointAskReadOnlyText =
        "- Checkpoint request: nothing to commit. Your response is your handoff: fill [1] status and [4] gaps with where you are and what is next. Reply \"Checkpoint: ready read-only inflight=none\", or \"Checkpoint: not-ready reason=<one line>\".";

    private const string CheckpointAskNoTreeText =
        "- Checkpoint request: the team is about to be restarted and you have no work tree, so there is nothing to commit.\n"
        + "- Stop every background process you started (headless Godot runs, servers, watchers) and name them in your response.\n"
        + "- Reply `Checkpoint: ready no-tree inflight=none` only after that, or `Checkpoint: not-ready reason=<one line>`. Put the line first under `## [1] status` of your response.\n"
        + "- Then stop working until you are resumed; a new instruction supersedes this.";

    /// <summary>
    /// The <c>checkpointAsk</c> prefix of a quit request: the team is paused at the user's order. The rest names the checkpoint form it
    /// extends (<c>read-only</c>, <c>no-tree</c>, or none).
    /// </summary>
    internal const string CheckpointAskQuit = "quit";

    private const string QuitLead = "- The team is QUIT (paused) at the user's order. Finish no new work.\n";

    private const string QuitTail =
        "\n- Add a `## [2] resume plan` section to your response with exactly these bullets, each present even when empty (write `none`): `Doing:` (request id and what), `Done:`, `Next:` (ordered steps to resume), `Open:` (questions or blockers for the user), `Processes:` (what you stopped), `Lanes:` (sub-orchestrators only: the child plan path).\n"
        + "- Your `ah msg send` will report `delivery: drained`. That is expected; the Orchestrator reads your response.\n"
        + "- Then stop. Do not start, resume or continue anything until a resume brief arrives.";

    private static string CheckpointAskOf(string? variant)
    {
        if (variant is not null && variant.StartsWith(CheckpointAskQuit, StringComparison.Ordinal))
        {
            var inner = variant[CheckpointAskQuit.Length..].TrimStart('-');
            return QuitLead + CheckpointAskOf(inner.Length == 0 ? null : inner) + QuitTail;
        }

        return variant switch
        {
            CheckpointAskReadOnly => CheckpointAskReadOnlyText,
            CheckpointAskNoTree => CheckpointAskNoTreeText,
            _ => CheckpointAsk,
        };
    }

    /// <summary>The body of a checkpoint request or its response: the fixed ask in the request's goal (the <paramref name="ask"/> variant), the answer line placed first under the response's status heading.</summary>
    private static string CheckpointSkeleton(string skeleton, bool request, string? ask) => request
        ? skeleton.Replace("## [1] goal\n- none", "## [1] goal\n" + CheckpointAskOf(ask), StringComparison.Ordinal)
        : skeleton.Replace("## [1] status\n", "## [1] status\n" + MessageText.CheckpointAnswerPlaceholder + "\n", StringComparison.Ordinal);

    private static string SkeletonBody(string[] keys)
    {
        var lines = new List<string> { "## [0] tldr" };
        for (var i = 1; i < keys.Length; i++)
        {
            lines.Add($"- [{i}] {keys[i]}: ");
        }

        for (var i = 1; i < keys.Length; i++)
        {
            lines.Add(string.Empty);
            lines.Add($"## [{i}] {keys[i]}");
            lines.Add(ReferenceEquals(keys, ResponseKeys) && keys[i] == "status" ? MessageText.StatusTagRule : "- none");
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>The path of the team file a message belongs to, or null: the file that names the recipient wins over the sender's team scope.</summary>
    private string? TeamFileFor(string dir, JsonValue? toName, JsonValue? team)
    {
        var holder = _roster.ResolveMemberTeam(dir, TextOf(toName));
        var wanted = holder.Found ? holder.Team : TextOf(team);
        // a team is where the roster keeps it (the store once teams are cut over), not where a file is
        var p = Resolve(_roster.TeamPath(dir, wanted));
        if (_roster.TeamExistsAt(p))
        {
            return p;
        }

        // a linked worktree's team file lives in the main checkout's hierarchy directory
        var main = _config.MainHierarchyDir(AhPaths.RepoRootOf(dir) ?? _cwd);
        if (string.IsNullOrEmpty(wanted) || string.IsNullOrEmpty(main) || main == dir)
        {
            return null;
        }

        var inMain = Resolve(_roster.TeamPath(main, wanted));
        return _roster.TeamExistsAt(inMain) ? inMain : null;
    }

    public string? TeamHome(string dir, string? toName, string? team) =>
        TeamFileFor(dir, toName is null ? JsonValue.Null : toName, team is null ? JsonValue.Null : team) is { } file ? Ah.Roster.RosterModule.TeamFileHome(file) : null;

    /// <summary><c>"orchestrator"</c>, <c>"chief"</c> plus every registry role of a cwd: what <c>--to</c> and <c>--from</c> accept.</summary>
    public JsonArray MsgRoles(string? cwd)
    {
        var roles = new JsonArray();
        try
        {
            var registry = _config.RegistryRoles(_config.ResolveConfig(cwd));
            roles.Add("orchestrator");
            roles.Add("chief");
            foreach (var r in registry)
            {
                roles.Add(r);
            }
        }
        catch (Exception)
        {
            roles = new JsonArray();
            foreach (var r in _config.MsgRoles)
            {
                roles.Add(r);
            }
        }

        return roles;
    }

    private static bool Contains(JsonArray roles, JsonValue? value) =>
        value is { IsText: true } t && roles.Any(r => r.IsText && r.AsString() == t.AsString());

    private static string Show(JsonValue? value) => value is null ? "undefined" : JsonText.Stringify(value.Value, 0);

    // Existing: the call retries a request already on disk, Path is that file and nothing is written. Duplicates: the files a no-id retry matched, newest first.
    private sealed record MessagePlan(JsonObject Fields, string Path, string Body, string TargetMsgs, bool Existing = false, IReadOnlyList<string>? Duplicates = null);

    /// <summary>How long after its creation an unfilled request still answers a retry of the same call that had no <c>--id</c>.</summary>
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(10);

    private MessagePlan PlanMessage(string dir, JsonObject options)
    {
        var type = options.Get("type") is { IsTruthy: true } t ? t : (JsonValue)"request";
        if (!(type.IsText && Array.IndexOf(MsgTypes, type.AsString()) >= 0))
        {
            throw new InvalidOperationException($"type must be request|response, got {Show(type)}");
        }

        var authored = AuthoredBodyOf(options);
        _bootstrap?.EnsureHierarchyDir(_config.HierarchyDir(TextOf(options.Get("cwd"))));
        JsonObject fields;
        if (type.AsString() == "request")
        {
            var to = options.Get("to");
            var from = options.Get("from");
            var slug = options.Get("slug");
            var cwd = TextOf(options.Get("cwd"));
            var valid = _config.MsgRoles.Contains(to is { IsText: true } tt ? tt.AsString() : string.Empty) && _config.MsgRoles.Contains(from is { IsText: true } ff ? ff.AsString() : string.Empty)
                ? ToArray(_config.MsgRoles)
                : MsgRoles(cwd);
            if (!Contains(valid, to))
            {
                var memberHint = to is { IsText: true } named && _roster.ResolveMemberTeam(dir, named.AsString()).Found
                    ? $"; to address a member use --to <its role> --to-name {named.AsString()}"
                    : string.Empty;
                throw new InvalidOperationException($"--to must be one of {Join(valid)}, got {Show(to)}{memberHint}");
            }

            if (!Contains(valid, from))
            {
                throw new InvalidOperationException($"--from must be one of {Join(valid)}, got {Show(from)}");
            }

            if (!(slug is { IsText: true } s && SlugRegex().IsMatch(s.AsString())))
            {
                throw new InvalidOperationException($"--slug must match [a-z0-9-]{{1,32}}, got {Show(slug)}");
            }

            if (options.Get("reason") is { IsNull: false } reason && !(reason.IsText && (Array.IndexOf(Reasons, reason.AsString()) >= 0 || reason.AsString() is AutoTrackedReason or HierarchyModule.AutoTrackedNoteReason or HierarchyModule.PlainSendReason)))
            {
                throw new InvalidOperationException($"--reason must be one of {string.Join('|', Reasons)}, got {Show(reason)}");
            }

            if (options.Get("eta") is { IsNull: false } eta && !(eta.IsText && Array.IndexOf(Etas, eta.AsString()) >= 0))
            {
                throw new InvalidOperationException($"--eta must be one of {string.Join('|', Etas)}, got {Show(eta)}");
            }

            string? title = null;
            if (options.Get("title") is { IsNull: false } titleValue)
            {
                var (checkedTitle, refusal) = MessageText.CheckTitle(titleValue.IsText ? titleValue.AsString() : string.Empty);
                title = checkedTitle ?? throw new InvalidOperationException(refusal);
            }

            var idOpt = options.Get("id");
            var idGiven = idOpt is { IsTruthy: true };
            var id = idOpt is { IsTruthy: true } given ? given : (JsonValue)_core.NewId();
            if (!(id.IsText && IdRegex().IsMatch(id.AsString())))
            {
                throw new InvalidOperationException($"--id must look like YYYYMMDD-HHMMSS-xxxx, got {Show(id)}");
            }

            var sameId = FindByIdAndType(dir, id.AsString(), "request");
            if (!sameId.IsNull)
            {
                if (idGiven && SameRequest(sameId.AsObject()["path"].AsString(), options))
                {
                    return ExistingPlan(sameId.AsObject()["path"].AsString(), null);
                }

                throw new InvalidOperationException($"request {id.AsString()} already exists");
            }

            if (!idGiven && RetryMatches(dir, options) is { Count: > 0 } matches)
            {
                return ExistingPlan(matches[0], matches.Count > 1 ? matches : null);
            }

            var teamFile = TeamFileFor(dir, options.Get("toName"), options.Get("team"));
            fields = new JsonObject
            {
                ["id"] = id,
                ["type"] = type,
                ["to"] = to ?? JsonValue.Null,
                ["from"] = from ?? JsonValue.Null,
                ["slug"] = slug ?? JsonValue.Null,
                ["parent"] = Or(options.Get("parent"), JsonValue.Null),
                ["reason"] = Or(options.Get("reason"), JsonValue.Null),
                ["eta"] = Or(options.Get("eta"), JsonValue.Null),
                ["to_name"] = Or(options.Get("toName"), JsonValue.Null),
                ["from_name"] = Or(options.Get("fromName"), Or(options.Get("markerName"), Or(options.Get("paneName"), JsonValue.Null))),
                ["team"] = Or(options.Get("team"), JsonValue.Null),
                ["team_file"] = teamFile is null ? JsonValue.Null : teamFile,
                ["created"] = LocalIso(),
            };
            if (options.Get("after") is { IsText: true } afterId && afterId.AsString().Length > 0)
            {
                fields["after"] = afterId;
            }

            if (options.Get("item") is { IsText: true } itemId && itemId.AsString().Length > 0)
            {
                fields["item"] = itemId;
            }

            if (options.Get("serves") is { IsText: true } serves && serves.AsString().Length > 0)
            {
                fields["serves"] = serves;
            }

            if (options.Get("fromLeads") is { IsText: true } leads && leads.AsString().Length > 0)
            {
                fields["from_leads"] = leads;
            }

            if (title is not null)
            {
                fields["title"] = title;
            }
        }
        else if (options.Get("note") is { IsTruthy: true } noteId)
        {
            fields = NoteResponseFields(dir, options, noteId, type);
        }
        else
        {
            if (options.Get("id") is not { IsTruthy: true } idv)
            {
                throw new InvalidOperationException("--id is required for a response");
            }

            var idText = JsConvert.ToJsString(idv);
            JsonObject req;
            if (options.Get("reqPath") is { IsTruthy: true } reqPathValue)
            {
                var p = TextOf(reqPathValue) ?? JsConvert.ToJsString(reqPathValue);
                if (!NodePath.IsAbsolute(p))
                {
                    throw new InvalidOperationException($"--req must be an absolute path, got {Show(reqPathValue)} — answer the brief's [hierarchy-msg <id>] with `ah msg fill --id <id> --from <your role> --body-file -` instead");
                }

                if (!_core.MsgExists(p))
                {
                    throw new InvalidOperationException($"--req: no such file {p} — either the path is typo'd, or the request was archived/moved. Nothing written.");
                }

                var meta = MessageText.ParseFilename(p);
                if (meta is null || meta.Type != "request")
                {
                    throw new InvalidOperationException($"--req: {p} is not a request file");
                }

                if (meta.Id != idText)
                {
                    throw new InvalidOperationException($"--req: request id {Show(meta.Id)} does not match --id {Show(idv)}");
                }

                req = new JsonObject { ["path"] = p, ["meta"] = MetaObject(meta) };
            }
            else
            {
                var found = FindByIdAndType(dir, idText, "request");
                if (found.IsNull && TeamFileFor(dir, null, options.Get("team")) is { } teamFile)
                {
                    // a response goes beside its request, whichever pool of the team holds it
                    if (Locate(idText, "request", [teamFile], []).Member("path") is { IsText: true } at)
                    {
                        options["reqPath"] = at.AsString();
                        found = FindByIdAndType(NodePath.Dirname(NodePath.Dirname(at.AsString())), idText, "request");
                    }
                }

                if (found.IsNull)
                {
                    throw new InvalidOperationException(
                        $"no request `{idText}` found in {MsgsDir(dir)} — if you are answering a request delivered as [hierarchy-msg <id>], file it with `ah msg fill --id <id> --from <your role> --body-file -`; if it was delivered as [hierarchy-msg <path>], re-run with --req <that path>. A response created here would land in a pool the requester never reads.");
                }

                if (!FindByIdAndType(dir, idText, "response").IsNull)
                {
                    throw new InvalidOperationException($"response {idText} already exists");
                }

                req = found.AsObject();
            }

            var parsed = _core.ReadMsgFile(req["path"].AsString());
            var rf = parsed.Member("fm") is { IsMap: true } fmv ? fmv : new JsonObject();
            var reqMeta = req["meta"].AsObject();
            if (options.Get("reqPath") is { IsTruthy: true })
            {
                if (!(parsed.Member("fm") is { IsMap: true }))
                {
                    throw new InvalidOperationException($"--req: {req["path"].AsString()} has no parseable frontmatter");
                }

                Mismatch("id mismatch", idv, rf.Member("id"));
                Mismatch("--to must equal the request's from", options.Get("to"), rf.Member("from"));
                Mismatch("--from must equal the request's to", options.Get("from"), rf.Member("to"));
                Mismatch("--to-name must equal the request's from_name", options.Get("toName"), rf.Member("from_name"));
                Mismatch("--from-name must equal the request's to_name", options.Get("fromName"), rf.Member("to_name"));
            }

            // A withdrawal is the request's own sender closing it: it names the sender as `from` and the one who no longer owes the work as `to`,
            // the other way round from a reply, and carries the reason.
            var withdrawing = options.Get("withdrawn") is { IsTruthy: true } || options.Get("closed") is { IsTruthy: true };
            fields = withdrawing
                ? new JsonObject
                {
                    ["id"] = idv,
                    ["type"] = type,
                    ["to"] = Or(rf.Member("to"), reqMeta["to"]),
                    ["from"] = Or(rf.Member("from"), JsonValue.Null),
                    ["slug"] = reqMeta["slug"],
                    ["parent"] = Or(rf.Member("parent"), JsonValue.Null),
                    ["reason"] = Or(options.Get("reason"), JsonValue.Null),
                    ["eta"] = JsonValue.Null,
                    ["to_name"] = Or(rf.Member("to_name"), JsonValue.Null),
                    ["from_name"] = Or(rf.Member("from_name"), JsonValue.Null),
                    ["team"] = Or(rf.Member("team"), JsonValue.Null),
                    ["team_file"] = Or(rf.Member("team_file"), JsonValue.Null),
                    ["created"] = LocalIso(),
                }
                : new JsonObject
                {
                    ["id"] = idv,
                    ["type"] = type,
                    ["to"] = Or(options.Get("to"), Or(rf.Member("from"), reqMeta["to"])),
                    ["from"] = Or(options.Get("from"), Or(rf.Member("to"), reqMeta["to"])),
                    ["slug"] = reqMeta["slug"],
                    ["parent"] = Or(rf.Member("parent"), JsonValue.Null),
                    ["reason"] = JsonValue.Null,
                    ["eta"] = JsonValue.Null,
                    ["to_name"] = Or(options.Get("toName"), Or(rf.Member("from_name"), JsonValue.Null)),
                    ["from_name"] = Or(options.Get("fromName"), Or(options.Get("markerName"), Or(rf.Member("to_name"), Or(options.Get("paneName"), JsonValue.Null)))),
                    ["team"] = Or(options.Get("team"), Or(rf.Member("team"), JsonValue.Null)),
                    ["team_file"] = Or(rf.Member("team_file"), JsonValue.Null),
                    ["created"] = LocalIso(),
                };
        }

        if (options.Get("withdrawn") is { IsTruthy: true })
        {
            fields["withdrawn"] = true;
        }

        if (options.Get("closed") is { IsTruthy: true })
        {
            fields["closed"] = true;
            fields["closed_by"] = options.Get("closedBy") is { IsText: true } cb ? cb.AsString() : JsonValue.Null;
            fields["closed_reason"] = options.Get("closedReason") is { IsText: true } cr ? cr.AsString() : JsonValue.Null;
        }

        fields["team_guide"] = Or(fields["team_file"], JsonValue.Null).IsTruthy ? TeamGuide : JsonValue.Null;
        var targetMsgs = options.Get("reqPath") is { IsTruthy: true } rp ? NodePath.Dirname(JsConvert.ToJsString(rp)) : MsgsDir(dir);
        // A withdrawal sits where a reply to the request would, so everything that looks for the request's response finds it.
        var path = (options.Get("withdrawn") is { IsTruthy: true } || options.Get("closed") is { IsTruthy: true }) && options.Get("reqPath") is { IsTruthy: true } wrp && ResponsePlan(wrp).Member("path") is { IsText: true } planned
            ? planned.AsString()
            : NodePath.Join(targetMsgs, MsgFilename(fields));
        var body = FrontmatterText(fields) + "\n" + BodyAfterFrontmatter(type.AsString() == "request", fields["slug"], authored, TextOf(options.Get("checkpointAsk")));
        return new MessagePlan(fields, path, body, targetMsgs);
    }

    /// <summary>The text a new message carries after its front matter: <paramref name="authored"/> when the caller gave a body, else the skeleton of its kind, with the ask the release and checkpoint slugs add.</summary>
    private static string BodyAfterFrontmatter(bool isRequest, JsonValue slug, string? authored = null, string? checkpointAsk = null)
    {
        if (authored is not null)
        {
            return authored.EndsWith('\n') ? authored : authored + "\n";
        }

        var skeleton = SkeletonBody(isRequest ? RequestKeys : ResponseKeys);
        return slug is { IsText: true } kind
            ? kind.AsString() switch
            {
                ReleaseSlug => ReleaseSkeleton(skeleton, isRequest),
                CheckpointSlug => CheckpointSkeleton(skeleton, isRequest, checkpointAsk),
                _ => skeleton,
            }
            : skeleton;
    }

    /// <summary>
    /// The body the caller wrote (<c>body</c> in <paramref name="options"/>), checked: it must be non-blank, have no line that is exactly
    /// <c>---</c>, and carry text beyond the generated skeleton. Null when the caller gave none.
    /// </summary>
    private static string? AuthoredBodyOf(JsonObject options)
    {
        if (options.Get("body") is not { IsText: true } given)
        {
            return null;
        }

        var text = given.AsString();
        if (JsText.Trim(text).Length == 0)
        {
            throw new InvalidOperationException("--body-file is empty");
        }

        if (text.Split('\n').Contains("---"))
        {
            throw new InvalidOperationException("--body-file has a line that is exactly --- , which would read as a frontmatter fence");
        }

        if (!MessageText.HasAuthoredBody(text))
        {
            throw new InvalidOperationException("--body-file has no text beyond the skeleton: write the body, not the template");
        }

        return text;
    }

    /// <summary>The text after the closing <c>---</c> line of the front matter of <paramref name="text"/>; empty when there is none.</summary>
    private static string BodyOf(string text)
    {
        var close = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return close >= 0 ? text[(close + 5)..] : string.Empty;
    }

    /// <summary>
    /// What makes two requests the same request: type, from, to, to_name, team, slug, parent and the text after the front matter. The
    /// <c>id</c> and <c>created</c> fields are left out. Null when <paramref name="fm"/> is not a request's front matter.
    /// </summary>
    private static string? CreateKey(JsonValue fm, string text)
    {
        if (fm.IsNull || TextOf(fm.Member("type")) != "request")
        {
            return null;
        }

        return string.Join(
            '\u0001',
            CreateKeyFields.Select(k => TextOf(fm.Member(k)) ?? string.Empty).Append(BodyOf(text)));
    }

    private static readonly string[] CreateKeyFields = ["from", "to", "to_name", "team", "slug", "title", "parent"];

    private static readonly string[] PlannedKeyOptions = ["from", "to", "toName", "team", "slug", "title", "parent"];

    /// <summary>The create key of the request <paramref name="options"/> plans, in the form <see cref="CreateKey"/> gives a file; <paramref name="body"/> is the text after its front matter.</summary>
    private static string PlannedKey(JsonObject options, string body) =>
        string.Join(
            '\u0001',
            PlannedKeyOptions.Select(k => (TextOf(options.Get(k)) ?? string.Empty) is var v && k == "title" ? JsText.Trim(v) : v).Append(body));

    /// <summary>
    /// The request already on disk that a create of <paramref name="content"/> at <paramref name="path"/> would duplicate, or null. A file at
    /// <paramref name="path"/> with the same create key is the same request whenever it was made; otherwise a request in the pool for the same
    /// <c>to</c> and slug with the same key counts when it was created no later than the new one and within <see cref="RetryWindow"/> of it;
    /// an exchange that already has a response is closed and never counts; that scan runs only when <paramref name="scan"/> is set, which the client does for an id it generated (an id the caller chose is the caller's own key).
    /// </summary>
    internal (string Id, string Path)? HeldCreate(string dir, string path, string content, bool scan)
    {
        if (MessageText.ParseFrontmatter(content) is not { } parsed || CreateKey(parsed.Fields, content) is not { } key)
        {
            return null;
        }

        (string, string)? Held(string candidate)
        {
            var read = _core.ReadMsgFile(candidate);
            return read.Member("fm") is { IsMap: true } held && CreateKey(held, TextOf(read.Member("text")) ?? string.Empty) == key
                ? (TextOf(held.Member("id")) ?? NodePath.Basename(candidate), candidate)
                : null;
        }

        if (_core.MsgExists(path))
        {
            return Held(path);
        }

        if (!scan)
        {
            return null;
        }

        var to = TextOf(parsed.Fields.Get("to"));
        var slug = TextOf(parsed.Fields.Get("slug"));
        if (!DateTimeOffset.TryParse(TextOf(parsed.Fields.Get("created")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var planned))
        {
            return null;
        }

        foreach (var (candidate, meta) in RetryCandidates(dir, to, slug, planned))
        {
            if (meta.Type != "request" || meta.To != to || meta.Slug != slug)
            {
                continue;
            }

            var read = _core.ReadMsgFile(candidate);
            if (read.Member("fm") is { IsMap: true } held
                && DateTimeOffset.TryParse(TextOf(held.Member("created")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var created)
                && created <= planned && planned - created <= RetryWindow
                && CreateKey(held, TextOf(read.Member("text")) ?? string.Empty) == key
                && FindByIdAndType(dir, meta.Id, "response").IsNull)
            {
                return (TextOf(held.Member("id")) ?? meta.Id, candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// The requests a create's retry check reads: only those to <paramref name="to"/> with <paramref name="slug"/> opened within
    /// <see cref="RetryWindow"/> before <paramref name="planned"/> (an index range, a superset the caller still checks exactly; x53 listed
    /// ~251k rows per create); when the source cannot narrow, every request of the pool.
    /// </summary>
    private List<(string Path, MsgFilenameParts Meta)> RetryCandidates(string dir, string? to, string? slug, DateTimeOffset planned)
    {
        if (to is not null && slug is not null)
        {
            using var db = _core.DbMsgs();
            if (db.RequestsOpenedSince(dir, to, slug, (planned - RetryWindow).ToUnixTimeMilliseconds() - 1) is { } rows)
            {
                return [.. rows.Select(static m => (m.Path, new MsgFilenameParts(m.Id, m.To, m.Slug, m.Type)))];
            }
        }

        return RequestFiles(dir);
    }

    private MessagePlan ExistingPlan(string path, IReadOnlyList<string>? duplicates)
    {
        var fm = _core.ReadMsgFile(path).Member("fm") is { IsMap: true } map ? map.AsObject() : new JsonObject();
        return new MessagePlan(fm, path, string.Empty, NodePath.Dirname(path), Existing: true, Duplicates: duplicates);
    }

    /// <summary>Whether the request file at <paramref name="path"/> carries the same type, from, to, to_name, team, slug and parent as the call's options.</summary>
    private bool SameRequest(string path, JsonObject options)
    {
        if (_core.ReadMsgFile(path).Member("fm") is not { IsMap: true } fm)
        {
            return false;
        }

        var text = TextOf(_core.ReadMsgFile(path).Member("text")) ?? string.Empty;
        var planned = "\n" + BodyAfterFrontmatter(true, options.Get("slug") ?? JsonValue.Null, AuthoredBodyOf(options), TextOf(options.Get("checkpointAsk")));
        return CreateKey(fm, text) == PlannedKey(options, planned);
    }

    /// <summary>
    /// The requests a retry of a call without <c>--id</c> would duplicate, newest first: same type, from, to, to_name, team, slug and parent,
    /// created within <see cref="RetryWindow"/>, still the unfilled template, and with no response yet (an answered exchange is closed).
    /// </summary>
    private List<string> RetryMatches(string dir, JsonObject options)
    {
        var to = TextOf(options.Get("to"));
        var slug = TextOf(options.Get("slug"));
        var from = TextOf(options.Get("from"));
        var toName = TextOf(options.Get("toName"));
        var team = TextOf(options.Get("team"));
        var parent = TextOf(options.Get("parent"));
        var template = "\n" + BodyAfterFrontmatter(true, options.Get("slug") ?? JsonValue.Null, AuthoredBodyOf(options), TextOf(options.Get("checkpointAsk")));
        var now = _clock.UtcNow;
        var found = new List<(DateTimeOffset Created, string Path)>();

        // db mode reads only the requests to this recipient and slug opened within the window (x53: 538 ms of a 677 ms msg new listed the pool)
        foreach (var (path, meta) in RetryCandidates(dir, to, slug, now))
        {
            if (meta.Type != "request" || meta.To != to || meta.Slug != slug)
            {
                continue;
            }

            var read = _core.ReadMsgFile(path);
            if (read.Member("fm") is not { IsMap: true } fm
                || TextOf(fm.Member("from")) != from
                || TextOf(fm.Member("to_name")) != toName
                || TextOf(fm.Member("team")) != team
                || TextOf(fm.Member("parent")) != parent
                || !DateTimeOffset.TryParse(TextOf(fm.Member("created")), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var created)
                || now - created > RetryWindow || created > now)
            {
                continue;
            }

            if (BodyOf(TextOf(read.Member("text")) ?? string.Empty) == template
                && FindByIdAndType(dir, meta.Id, "response").IsNull)
            {
                found.Add((created, path));
            }
        }

        return found.OrderByDescending(f => f.Created).Select(f => f.Path).ToList();
    }

    /// <summary>
    /// The frontmatter of a response to a note entry, which has no request file: the note's entry id is the message id, <c>to</c> and
    /// <c>to_name</c> are the note's sender (resolved by the caller), and the slug is <c>note</c>. A msgs file that already has the id
    /// is not overwritten.
    /// </summary>
    private JsonObject NoteResponseFields(string dir, JsonObject options, JsonValue noteId, JsonValue type)
    {
        var id = JsConvert.ToJsString(noteId);
        if (!IdRegex().IsMatch(id))
        {
            throw new InvalidOperationException($"--note must be a note entry id (YYYYMMDD-HHMMSS-xxxx), got {Show(noteId)}");
        }

        foreach (var kind in new[] { "request", "response" })
        {
            var used = FindByIdAndType(dir, id, kind);
            if (!used.IsNull)
            {
                throw new InvalidOperationException($"id already used by {used.AsObject()["path"].AsString()}");
            }
        }

        if (!(options.Get("to") is { IsText: true } to && to.AsString().Length > 0))
        {
            throw new InvalidOperationException("--note: the note's sender has no role to address the response to");
        }

        if (!(options.Get("from") is { IsText: true } from && from.AsString().Length > 0))
        {
            throw new InvalidOperationException("--note needs --from <your role>");
        }

        var teamFile = TeamFileFor(dir, options.Get("toName"), options.Get("team"));
        return new JsonObject
        {
            ["id"] = id,
            ["type"] = type,
            ["to"] = to,
            ["from"] = from,
            ["slug"] = "note",
            ["parent"] = JsonValue.Null,
            ["reason"] = JsonValue.Null,
            ["eta"] = JsonValue.Null,
            ["to_name"] = Or(options.Get("toName"), JsonValue.Null),
            ["from_name"] = Or(options.Get("fromName"), Or(options.Get("markerName"), Or(options.Get("paneName"), JsonValue.Null))),
            ["team"] = Or(options.Get("team"), JsonValue.Null),
            ["team_file"] = teamFile is null ? JsonValue.Null : teamFile,
            ["created"] = LocalIso(),
        };
    }

    private static void Mismatch(string label, JsonValue? got, JsonValue? want)
    {
        if (got is { IsTruthy: true } g && want is { IsTruthy: true } w && !StrictEquals(g, w))
        {
            throw new InvalidOperationException($"--req: {label} — response has {Show(g)}, request has {Show(w)}");
        }
    }

    private static bool StrictEquals(JsonValue a, JsonValue b) => PeerRows.StrictEquals(a, b);

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values)
        {
            array.Add(v);
        }

        return array;
    }

    private static string Join(JsonArray values) => string.Join('|', values.Select(v => v.AsString()));

    [GeneratedRegex("^[a-z0-9-]{1,32}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SlugRegex();

    [GeneratedRegex("^[0-9]{8}-[0-9]{6}-[0-9a-z]{4}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdRegex();

    public bool IsSlug(string value) => SlugRegex().IsMatch(value);

    public bool IsMessageId(string value) => IdRegex().IsMatch(value);

    public JsonObject CloseExchange(string dir, JsonValue exchange, string requestPath, string reason, string? note, string by, string cwd)
    {
        var id = exchange.Member("id")?.AsString() ?? string.Empty;
        if (exchange.Member("response") is { IsMap: true } held)
        {
            var heldPath = held.Member("path")?.AsString() ?? string.Empty;
            var heldFm = _core.ReadMsgFile(heldPath).Member("fm");
            if (IsRetired(heldFm))
            {
                return new JsonObject { ["id"] = id, ["closed"] = true, ["already"] = true, ["response"] = heldPath };
            }

            // A response with no authored content is an empty skeleton: it is no report, so closing retires it with the request. A running
            // lane's charter stays open after its report lands; closing it marks that report closed, since a second response cannot be filed.
            if (!ResponseAuthored(heldPath) || (ResponseLanded(heldPath) && _core.IsOpenLaneCharter(NodePath.Dirname(NodePath.Dirname(requestPath)), id)))
            {
                var by1 = string.Join(' ', by.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                var retire = new JsonObject
                {
                    ["dir"] = NodePath.Dirname(NodePath.Dirname(heldPath)),
                    ["path"] = heldPath,
                    ["closed_by"] = by1,
                    ["closed_reason"] = reason,
                };
                _ = Call(PoolContext(NodePath.Dirname(NodePath.Dirname(heldPath))), "msgs", "retire", retire);
                var retired = new JsonObject { ["id"] = id, ["closed"] = true, ["closed_by"] = by, ["reason"] = reason, ["response"] = heldPath };
                if (note is { } n && string.Join(' ', n.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } cleanNote)
                {
                    retired["note"] = cleanNote;
                }

                return retired;
            }

            return new JsonObject { ["id"] = id, ["closed"] = false, ["refused"] = "already_reported", ["response"] = heldPath };
        }

        var clean = note is null ? null : string.Join(' ', note.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var options = new JsonObject
        {
            ["reqPath"] = requestPath,
            ["cwd"] = cwd,
            ["type"] = "response",
            ["id"] = id,
            ["closed"] = true,
            ["closedBy"] = string.Join(' ', by.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            ["closedReason"] = reason,
            ["reason"] = reason,
            ["body"] = $"CLOSED by {by} ({reason}){(clean is { Length: > 0 } ? ": " + clean : string.Empty)}",
        };
        var created = CreateMessage(dir, options);
        var result = new JsonObject { ["id"] = id, ["closed"] = true, ["closed_by"] = by, ["reason"] = reason, ["response"] = created.Member("path") ?? JsonValue.Null };
        if (clean is { Length: > 0 })
        {
            result["note"] = clean;
        }

        return result;
    }

    private static bool IsRetired(JsonValue? fm) =>
        fm?.Member("closed") is { IsText: true } c && c.AsString() == "true" || fm?.Member("withdrawn") is { IsText: true } w && w.AsString() == "true";

    public JsonValue CreateMessage(string dir, JsonObject options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // the id, duplicate and charter checks decide what may be written: a store that cannot answer them refuses the write
        using var decisions = _core.DecisionReads();
        var plan = PlanMessage(dir, options);
        if (plan.Existing)
        {
            var again = new JsonObject { ["id"] = plan.Fields["id"], ["path"] = plan.Path, ["fields"] = plan.Fields, ["existing"] = true };
            if (plan.Duplicates is { } others)
            {
                var list = new JsonArray();
                foreach (var other in others)
                {
                    list.Add(other);
                }

                again["duplicates"] = list;
            }

            return again;
        }

        var pool = NodePath.Dirname(plan.TargetMsgs);
        var reqPath = options.Get("reqPath") is { IsTruthy: true } rp ? rp : (JsonValue?)null;
        var args = new JsonObject
        {
            ["dir"] = pool,
            ["path"] = plan.Path,
            ["content"] = plan.Body,
            ["req"] = reqPath ?? JsonValue.Null,
            ["id"] = plan.Fields["id"],
        };
        if (reqPath is null)
        {
            if (options.Get("id") is not { IsTruthy: true })
            {
                args["dedupe"] = true;
            }

            var created = CallCreate(dir, args, plan, "request");
            if (!created.ResultJson.IsEmpty && JsonText.Parse(created.ResultJson.Span) is { IsMap: true } outcome
                && outcome.Member("existing") is { IsTruthy: true } && outcome.Member("path") is { IsText: true } heldPath)
            {
                return new JsonObject
                {
                    ["id"] = outcome.Member("id") ?? plan.Fields["id"],
                    ["path"] = heldPath.AsString(),
                    ["fields"] = ExistingPlan(heldPath.AsString(), null).Fields,
                    ["existing"] = true,
                };
            }

            return new JsonObject { ["id"] = plan.Fields["id"], ["path"] = plan.Path, ["fields"] = plan.Fields };
        }

        if (plan.Fields["type"] is { IsText: true } responseKind && responseKind.AsString() == "response" && options.Get("reqPath") is { IsTruthy: true } charterAt
            && plan.Fields["id"] is { IsText: true } charterId
            && !(options.Get("withdrawn") is { IsTruthy: true }) && !(options.Get("closed") is { IsTruthy: true })
            && _core.IsOpenLaneCharter(NodePath.Dirname(NodePath.Dirname(JsConvert.ToJsString(charterAt))), charterId.AsString()))
        {
            var refusal = new InvalidOperationException(
                $"charter-open: request {charterId.AsString()} is this lane's charter and stays open until the lane is done. Send interim status with `ah msg send --kind note` or `--kind report`, and finish the lane with `ah roster stream-report --state done`; write the charter response only then");
            refusal.Data[ErrorCodeKey] = "charter-open";
            throw refusal;
        }

        if (plan.Fields["type"] is { IsText: true } kind && kind.AsString() == "response" && options.Get("reqPath") is { IsTruthy: true } requestAt
            && FindByIdAndType(NodePath.Dirname(NodePath.Dirname(JsConvert.ToJsString(requestAt))), JsConvert.ToJsString(plan.Fields["id"]), "response").Member("path") is { IsText: true } elsewhere
            && Ah.Core.Json.MsgRows.PathKey(elsewhere.AsString()) != Ah.Core.Json.MsgRows.PathKey(plan.Path)) // the store keys a message by its real path
        {
            throw new InvalidOperationException($"response {plan.Fields["id"].AsString()} already exists at {elsewhere.AsString()}");
        }

        if (_core.MsgExists(plan.Path))
        {
            // A retry of a call whose first attempt already wrote the skeleton gets that file back; a response with text in it is finished work.
            var read = _core.ReadMsgFile(plan.Path);
            var heldText = TextOf(read.Member("text")) ?? string.Empty;
            if (read.Member("fm") is { IsMap: true } closedFm && !(options.Get("withdrawn") is { IsTruthy: true }) && !(options.Get("closed") is { IsTruthy: true }))
            {
                RefuseIfClosed(plan.Path, plan.Fields["id"].AsString(), closedFm.AsObject());
            }

            if (AuthoredBodyOf(options) is not null && read.Member("fm") is { IsMap: true } same && TextOf(same.Member("from")) == TextOf(plan.Fields["from"])
                && BodyOf(heldText) == BodyOf(plan.Body))
            {
                return new JsonObject { ["id"] = plan.Fields["id"], ["path"] = plan.Path, ["fields"] = same, ["existing"] = true };
            }

            var heldClose = heldText.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            if (AuthoredBodyOf(options) is null && read.Member("fm") is { IsMap: true } held && TextOf(held.Member("from")) == TextOf(plan.Fields["from"])
                && heldClose >= 0 && !MessageText.HasAuthoredBody(heldText[(heldClose + 5)..]))
            {
                return new JsonObject { ["id"] = plan.Fields["id"], ["path"] = plan.Path, ["fields"] = held, ["existing"] = true };
            }

            throw new InvalidOperationException($"response {plan.Fields["id"].AsString()} already exists at {plan.Path}{(AuthoredBodyOf(options) is null ? string.Empty : "; its text differs from --body-file: use `ah msg fill` to replace it")}");
        }

        try
        {
            var responsePool = ResponsePool(JsConvert.ToJsString(reqPath));
            var status = responsePool["status"].AsString();
            if (status == "refused")
            {
                throw new InvalidOperationException(responsePool["reason"].AsString());
            }

            CallCreate(status == "allowed" ? responsePool["pool"].AsString() : dir, args, plan, "response");
        }
        catch (InvalidOperationException e) when (e.Data[ErrorCodeKey] is "state_timeout")
        {
            throw;
        }
        catch (Exception e) when (e is not StoreUnavailableException)
        {
            var who = plan.Fields["to_name"].IsTruthy ? plan.Fields["to_name"] : plan.Fields["to"];
            throw new InvalidOperationException(
                $"could not write the response beside the request at {plan.Path}: {e.Message} — deliver the response content via SendMessage to {JsConvert.ToJsString(who)} instead, naming this path. Nothing was written to the local pool.");
        }

        var local = Resolve(dir);
        var target = Resolve(pool);
        var result = new JsonObject { ["id"] = plan.Fields["id"], ["path"] = plan.Path, ["fields"] = plan.Fields };
        result["divergent"] = local != target ? new JsonObject { ["local"] = local, ["target"] = target } : JsonValue.Null;
        return result;
    }

    /// <summary>
    /// The <c>msgs.create</c> call of <paramref name="plan"/>. When the daemon does not answer in time the file may still land, so the failure says
    /// where and under which id, and carries both in <see cref="Exception.Data"/>.
    /// </summary>
    private StateResult CallCreate(string dir, JsonObject args, MessagePlan plan, string kind)
    {
        try
        {
            return Call(PoolContext(dir), "msgs", "create", args);
        }
        catch (InvalidOperationException e) when (e.Data[ErrorCodeKey] is "state_timeout")
        {
            // The daemon may finish the write after the client stopped waiting, so the file can exist.
            var id = plan.Fields["id"].AsString();
            var maybe = new InvalidOperationException(
                $"{e.Message}; the {kind} may still land as {plan.Path} (id {id}). Re-running the same command is safe: it returns that file if it landed.");
            maybe.Data[ErrorCodeKey] = "state_timeout";
            maybe.Data["id"] = id;
            maybe.Data["path"] = plan.Path;
            throw maybe;
        }
    }

    public JsonValue ResponsePlan(JsonValue? requestPath)
    {
        if (requestPath is not { IsText: true } rp || rp.AsString().Length == 0 || !NodePath.IsAbsolute(rp.AsString()))
        {
            return JsonValue.Null;
        }

        var path = rp.AsString();
        var meta = MessageText.ParseFilename(path);
        if (meta is null || meta.Type != "request")
        {
            return JsonValue.Null;
        }

        var parsed = _core.MsgExists(path) ? _core.ReadMsgFile(path) : JsonValue.Null;
        var rf = parsed.Member("fm") is { IsMap: true } fm ? fm : new JsonObject();
        var fields = new JsonObject
        {
            ["id"] = meta.Id,
            ["type"] = "response",
            ["to"] = Or(rf.Member("from"), "orchestrator"),
            ["from"] = Or(rf.Member("to"), meta.To),
            ["slug"] = meta.Slug,
            ["parent"] = Or(rf.Member("parent"), JsonValue.Null),
            ["reason"] = JsonValue.Null,
            ["eta"] = JsonValue.Null,
            ["to_name"] = Or(rf.Member("from_name"), JsonValue.Null),
            ["from_name"] = Or(rf.Member("to_name"), JsonValue.Null),
            ["team"] = Or(rf.Member("team"), JsonValue.Null),
            ["team_file"] = Or(rf.Member("team_file"), JsonValue.Null),
        };
        fields["team_guide"] = Or(rf.Member("team_file"), JsonValue.Null).IsTruthy ? TeamGuide : JsonValue.Null;
        return new JsonObject { ["path"] = NodePath.Join(NodePath.Dirname(path), MsgFilename(fields)), ["fields"] = fields };
    }

    public string ResponseFrontmatter(JsonObject fields, DateTimeOffset? now = null)
    {
        var copy = fields.Clone();
        copy["created"] = LocalIso(now);
        return FrontmatterText(copy);
    }

    public JsonValue FillPlan(JsonObject args)
    {
        var idv = args.Get("id");
        var from = args.Get("from");
        var reqPath = args.Get("reqPath");
        var body = args.Get("body");
        if (!(idv is { IsText: true } id && IdRegex().IsMatch(id.AsString())))
        {
            throw new InvalidOperationException($"fill: --id must be a message id, got {Show(idv)}");
        }

        if (!(from is { IsText: true } f && f.AsString().Length > 0))
        {
            throw new InvalidOperationException("fill: --from is required");
        }

        if (!(reqPath is { IsText: true } r && r.AsString().Length > 0))
        {
            throw new InvalidOperationException("fill: --req needs the request file's absolute path");
        }

        var req = r.AsString();
        if (!NodePath.IsAbsolute(req))
        {
            throw new InvalidOperationException($"fill: --req must be an absolute path, got {Show(reqPath)} — drop it: `ah msg fill --id <id>` finds the request of the brief's [hierarchy-msg <id>]");
        }

        if (!_core.MsgExists(req))
        {
            throw new InvalidOperationException($"fill: --req: no such file {req}");
        }

        var meta = MessageText.ParseFilename(req);
        if (meta is null || meta.Type != "request")
        {
            throw new InvalidOperationException($"fill: --req: {req} is not a request file");
        }

        if (meta.Id != id.AsString())
        {
            throw new InvalidOperationException($"fill: --req: request id {Show(meta.Id)} does not match --id {Show(idv)}");
        }

        // amend: the request's own author rewrites the brief in place of the response
        var amend = args.Get("amend") is { IsTruthy: true };
        var want = amend ? "request" : "response";
        var path = amend ? req : ResponsePlan(req).Prop("path").AsString();
        if (!_core.MsgExists(path))
        {
            throw new InvalidOperationException($"fill: no response {id.AsString()} yet — ah msg fill --id {id.AsString()} creates it");
        }

        string current;
        using (var db = _core.DbMsgs())
        {
            current = HierarchyCore.ReadMsgText(path, db);
        }

        var fm = MessageText.ParseFrontmatter(current);
        if (fm is null)
        {
            throw new InvalidOperationException($"fill: {path} has no frontmatter");
        }

        var fields = fm.Value.Fields;
        if (!(fields.Get("type") is { IsText: true } ft && ft.AsString() == want))
        {
            throw new InvalidOperationException($"fill: {path} is not a {want} (type {Show(fields.Get("type"))})");
        }

        if (!(fields.Get("id") is { IsText: true } fid && fid.AsString() == id.AsString()))
        {
            throw new InvalidOperationException($"fill: {path} has id {Show(fields.Get("id"))}, not {Show(idv)}");
        }

        if (!(fields.Get("from") is { IsText: true } ffrom && ffrom.AsString() == f.AsString()))
        {
            throw new InvalidOperationException(amend
                ? $"fill --request: {want} {id.AsString()} is from {Show(fields.Get("from"))}; only its author may amend it, not {Show(from)}"
                : $"fill: {path} is from {Show(fields.Get("from"))}, not {Show(from)}");
        }

        RefuseIfClosed(path, id.AsString(), fields);

        // the closed and withdrawn marks of an exchange live on its response, so an amend of the brief reads them there
        if (amend && ResponsePlan(req).Member("path") is { IsText: true } responsePath && _core.MsgExists(responsePath.AsString()))
        {
            string response;
            using (var db = _core.DbMsgs())
            {
                response = HierarchyCore.ReadMsgText(responsePath.AsString(), db);
            }

            if (MessageText.ParseFrontmatter(response) is { } responseFm)
            {
                RefuseIfClosed(responsePath.AsString(), id.AsString(), responseFm.Fields);
            }
        }

        if (body is not { IsText: true } b)
        {
            throw new InvalidOperationException("fill: --body is required: the report as one JSON string");
        }

        JsonValue parsedBody;
        try
        {
            parsedBody = JsonText.Parse(b.AsString());
        }
        catch (JsonParseException e)
        {
            throw new InvalidOperationException($"fill: --body must be a JSON string ({e.Message})");
        }

        if (!parsedBody.IsText)
        {
            throw new InvalidOperationException("fill: --body must be a JSON string, not another JSON value");
        }

        var text = parsedBody.AsString();
        if (JsText.Trim(text).Length == 0)
        {
            throw new InvalidOperationException("fill: --body is empty");
        }

        if (Utf8Text.Encode(text).Length > HierarchyModule.FillBodyMax)
        {
            throw new InvalidOperationException($"fill: --body is over {HierarchyModule.FillBodyMax} bytes");
        }

        if (text.Split('\n').Contains("---"))
        {
            throw new InvalidOperationException("fill: --body has a line that is exactly --- , which would read as a frontmatter fence");
        }

        var headLines = current.Split('\n').Take(fm.Value.End).ToList();
        var part = !amend && args.Get("part") is { IsTruthy: true };
        var partial = !amend && fields.Get("partial") is { IsText: true } pv && pv.AsString() == "true";
        // once a response has parts, every later fill appends: a revised final never writes over the parts or an earlier final
        if (part || partial || (!amend && fields.Get("parts") is { IsText: true }))
        {
            // b375: parts accumulate in the response under numbered headings; `partial: true` keeps the exchange open until a plain fill
            // appends the final report. A part after a final report is appended too, never written over it, and leaves it closed.
            var earlierText = string.Join('\n', current.Split('\n').Skip(fm.Value.End));
            var keep = partial || MessageText.HasAuthoredContent(current);
            var count = fields.Get("parts") is { IsText: true } pc && int.TryParse(pc.AsString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
            count += part ? 1 : 0;
            text = (keep ? JsText.Trim(earlierText) + "\n\n" : string.Empty) + $"## {(part ? $"part {count}" : "final")} · {LocalIso()}\n" + text;
            _ = headLines.RemoveAll(l => l.StartsWith("parts:", StringComparison.Ordinal) || l.StartsWith("partial:", StringComparison.Ordinal));
            headLines.Insert(headLines.Count - 1, $"parts: {count}");
            if (part && (partial || !keep))
            {
                headLines.Insert(headLines.Count - 1, "partial: true");
            }
        }

        var head = string.Join('\n', headLines) + "\n";
        var content = head + (text.StartsWith('\n') ? string.Empty : "\n") + (text.EndsWith('\n') ? text : text + "\n");
        return new JsonObject { ["path"] = path, ["content"] = content };
    }

    /// <summary>Refuses a write of a body into a response whose frontmatter says <c>closed: true</c>: the request was closed, so the result would be lost silently.</summary>
    private void RefuseIfClosed(string path, string id, JsonObject fields)
    {
        var at = _core.MsgTimeMs(path) is { } written ? Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)written)) : "an unknown time";
        if (HierarchyModule.WriteRefusal(id, key => fields.Get(key) is { IsText: true } v ? v.AsString() : null, at) is not { } refused)
        {
            return;
        }

        var refusal = new InvalidOperationException(refused.Code + ": " + refused.Message);
        refusal.Data[ErrorCodeKey] = refused.Code;
        throw refusal;
    }
}
