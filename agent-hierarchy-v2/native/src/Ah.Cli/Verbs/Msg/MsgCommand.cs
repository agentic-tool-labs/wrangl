using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Roster;

namespace Ah.Cli.Verbs.Msg;

/// <summary>One run of <c>msg</c>: the resolved team, the working directory, and the verb table.</summary>
internal sealed partial class MsgCommand
{
    private static readonly HashSet<string> NewFlags = new(StringComparer.Ordinal)
    {
        "type", "to", "from", "slug", "id", "to-name", "from-name", "parent", "reason", "eta", "after", "team", "req", "note", "item", "serves",
        "cwd", "plain", "json", "session", "orchestrator-pid", "help", "body-file", "title", "sha",
    };

    private readonly ServiceGraph _graph;
    private readonly IAhEnvironment _env;
    private readonly MsgOptions _opts;
    private readonly string _cmd;
    private readonly MsgOutput _out;
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly string _cwd;
    private readonly bool _plain;
    private readonly Func<Stream> _stdin;
    private readonly Func<DecisionCaller?>? _decisionCaller;
    private readonly Func<bool>? _uiAnswerCaller;

    private string? _teamHome;
    private bool _defaultTeamExplicit;
    private IReadOnlyList<string?>? _everyTeam;
    private string? _teamArg;
    private bool _teamIsMembers;

    /// <summary>Whether the team this caller owns homes its message: a member's implicit team, and a message sent as a member role, stay in the caller's own pool.</summary>
    private bool OwnedTeamRehomes => !_teamIsMembers && _opts.Text("from") is "orchestrator" or "sub-orchestrator" or "deputy-chief";

    internal MsgCommand(
        CliContext context,
        ServiceGraph graph,
        MsgOptions opts,
        string cmd,
        MsgOutput output,
        TextWriter stdout,
        TextWriter stderr,
        Func<Stream>? stdin = null)
    {
        _env = context.Env;
        _decisionCaller = context.DecisionCaller;
        _uiAnswerCaller = context.UiAnswerCaller;
        _graph = graph;
        _opts = opts;
        _cmd = cmd;
        _out = output;
        _stdout = stdout;
        _stderr = stderr;
        _stdin = stdin ?? (context.ReadStdin is { } read ? () => new MemoryStream(read().ToArray()) : Console.OpenStandardInput);
        _cwd = opts.Text("cwd") ?? Directory.GetCurrentDirectory();
        _plain = opts.Has("plain");
    }

    private IConfigCore Config => _graph.Config;

    private IRosterCore RosterCore => _graph.Roster;

    private HierarchyServices Hier => _graph.Hierarchy;

    private string HierarchyDir => Config.HierarchyDir(_cwd);

    internal int Run()
    {
        try
        {
            _teamArg = ResolveTeamArg();
            return Dispatch();
        }
        catch (MsgExit)
        {
            throw;
        }
        catch (MailFailure failure)
        {
            throw _out.Fail($"{failure.Code}: {failure.Message}");
        }
        catch (Exception e) when (e is not Ah.Core.Json.StoreUnavailableException)
        {
            throw _out.Fail(e.Message);
        }
    }

    private int Dispatch()
    {
        switch (_cmd)
        {
            case "new":
                return New();
            case "fill":
                return Fill();
            case "list":
                return List();
            case "downstream":
                return Downstream();
            case "index":
                return Index();
            case "show":
                return Show();
            case "sweep":
                return Sweep();
            case "roster":
                return Roster();
            case "route":
                return Route();
            case "decision":
                return Decision();
            case "send":
            case "inbox":
            case "next":
            case "ack":
            case "release":
            case "status":
            case "decline":
            case "withdraw":
            case "forward":
            case "close":
            case "link-request":
            case "join":
            case "link":
                return MailVerb();
            case "daemon":
                return Daemon();
            case "due":
                return Due();
            case "history":
                return History();
            default:
                throw _out.Fail(
                    "usage: ah msg new|fill|show|list|downstream|index|sweep|roster|route|decision|send|inbox|ack|status|decline|withdraw|forward|close|next|release|due|history|link-request|link|join|daemon [--cwd <path>] [--plain]"
                    + (_cmd.Length > 0 ? $" (unknown command {Quote(_cmd)})" : string.Empty));
        }
    }

    // ---------------------------------------------------------------- team resolution

    private string? ResolveTeamArg()
    {
        if (_opts.Text("team") == RosterModule.DefaultTeamArg)
        {
            _defaultTeamExplicit = true;
            return null;
        }

        if (_opts.Text("team") is { } team)
        {
            var v = Config.ValidateTeamAlias(team);
            if (!(v.Get("ok") is { } ok && ok.IsTruthy))
            {
                var why = v.Get("why") is { } w ? JsConvert.ToJsString(w) : "invalid";
                throw _out.Fail($"--team: {why}");
            }

            return team;
        }

        var identity = new TeamInvoker(SessionPid, SessionId);
        IReadOnlyList<string?> owned = [];
        try
        {
            owned = RosterCore.OwnedTeams(HierarchyDir, identity);
        }
        catch (Exception e) when (e is not Ah.Core.Json.StoreUnavailableException)
        {
            // a team file that cannot be read leaves the default team, as before
        }

        if (owned.Count == 1)
        {
            return owned[0];
        }

        if (owned.Count > 1)
        {
            if (_cmd is "list" or "roster")
            {
                _everyTeam = owned;
                return null;
            }

            if (_cmd == "new")
            {
                var toName = _opts.Text("to-name");
                var holders = RosterCore.TeamsWithMember(HierarchyDir, owned, toName);
                if (holders.Count == 1)
                {
                    return holders[0].IsText ? holders[0].AsString() : null;
                }

                var why = toName is not null ? $"\"{toName}\" is a member of {(holders.Count > 0 ? "more than one" : "none")} of them; " : string.Empty;
                throw _out.Fail(
                    $"new: this session owns {owned.Count} live teams ({RosterModule.TeamListText(owned)}) — {why}pass --team <name> to say which");
            }
        }

        try
        {
            var dir = HierarchyDir;
            var main = Config.MainHierarchyDir(_cwd);
            var homes = main is null ? new[] { dir, string.Empty } : [dir, main];
            var pane = Ah.Core.Mux.HerdrFlavour.SessionPaneId(_env.GetVariable) is { Length: > 0 } herdr ? herdr : _env.GetVariable("TMUX_PANE");
            if (RosterCore.MemberTeam(dir, homes.Where(h => h.Length > 0).ToList(), string.IsNullOrEmpty(pane) ? null : pane) is { } member)
            {
                _teamHome = member.Home;
                _teamIsMembers = true;
                return member.TeamName;
            }
        }
        catch (Exception e) when (e is not Ah.Core.Json.StoreUnavailableException)
        {
            // fail-open to the default team, as above
        }

        var own = OwnMemberTeam();
        _teamIsMembers = own is not null;
        return own;
    }

    /// <summary>
    /// The team of the one member box this proven caller holds, so a member's message carries its own team and lists with it. The
    /// legacy default team stays null, and so does a caller whose boxes span several teams or none.
    /// </summary>
    private string? OwnMemberTeam()
    {
        try
        {
            var teams = Me()
                .Select(b => _graph.Mailbox.SplitAddress(b.Address))
                .Where(a => a is not null && a.Name != "orchestrator" && a.Team != "default")
                .Select(a => a!.Team)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return teams.Count == 1 ? teams[0] : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TeamTagOf(JsonValue parsed)
    {
        var tag = parsed.Member("fm")?.Member("team");
        return tag is { IsTruthy: true } t && t.IsText ? t.AsString() : null;
    }

    // ---------------------------------------------------------------- new

    /// <summary>
    /// A run anchor is made here, so this is where the grant is checked at its source: a body that says <c>authority: surrogate</c>
    /// needs this session's recorded Authority answer, whoever runs the command.
    /// </summary>
    private void RefuseUnrecordedAuthority(string dir, string? bodyText)
    {
        if (bodyText is null || _opts.Text("slug") != HierarchyModule.PipelineAnchorSlug)
        {
            return;
        }

        var now = _graph.Io.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var asks = Hier.Decisions.AsksPath(dir);
        var parkedStore = ParkedQuestions.PathIn(dir);
        // per-dir-files-to-db §6c G5: each log read once; an asks store that cannot be read holds no recorded answer for this command
        var logs = new[] { asks, parkedStore }.Select(_graph.Io.Files.ReadAskLog).ToList();
        var unreadable = logs.Any(static l => !l.TryLines(out _));
        var asksText = unreadable ? string.Empty : string.Join('\n', logs.Select(static l => l.TryLines(out var lines) ? string.Join('\n', lines) : string.Empty).Where(static t => t.Length > 0));
        var because = unreadable ? " (the asks store could not be read, so no recorded answer was found)" : string.Empty;
        var sessionId = _env.GetVariable("CLAUDE_CODE_SESSION_ID");
        var afterIso = Hier.Decisions.LastAnchorCloseTime(dir, _teamArg, now);
        var recorded = HierarchyModule.RecordedAuthority(asksText, sessionId, afterIso, now);
        if (HierarchyModule.AnchorAuthorityRefusal(bodyText, null, recorded) is { } why)
        {
            throw _out.Fail("new: " + why + because);
        }
    }

    /// <summary>The refusal the dispatch gate would give a Reviewer brief at <paramref name="path"/> that lacks its goal or acceptance, or null; the file is written either way.</summary>
    private string? ReviewIntentWarning(string path)
    {
        var resolved = Config.ResolveConfig(_cwd, new ResolveOptions(Team: _teamArg));
        return Hier.Messages.ReviewIntentGap(path, resolved) is { IsArray: true } missing && missing.AsArray().Count > 0
            ? Hier.Messages.ReviewIntentReason(path, missing.AsArray(), "send it")
            : null;
    }

    /// <summary>The most <c>--serves</c> keeps, in code points.</summary>
    private const int ServesMax = 160;

    /// <summary>
    /// <c>--serves</c> as stored (ah2-53): never refused. Control characters go, then each <c> #</c> loses its <c>#</c> (the front-matter reader takes it for a
    /// comment, and the line is <c>serves: </c> then the value), then the value is trimmed and cut to <see cref="ServesMax"/> with an ellipsis. Empty, the word null,
    /// or on anything but a request, it is not written.
    /// </summary>
    private string? ServesOf(string type)
    {
        if (_opts.Text("serves") is not { } given)
        {
            return null;
        }

        if (type != "request")
        {
            _out.Note("note: --serves applies only to a request; not written");
            return null;
        }

        var value = " " + string.Concat(given.Where(c => !char.IsControl(c)));
        while (value.Contains(" #", StringComparison.Ordinal))
        {
            value = value.Replace(" #", " ", StringComparison.Ordinal);
        }

        value = value.Trim();
        var runes = value.EnumerateRunes().ToList();
        if (runes.Count > ServesMax)
        {
            value = string.Concat(runes.Take(ServesMax - 1).Select(r => r.ToString())) + "…";
            _out.Note($"note: --serves was cut to {ServesMax} characters");
        }

        return value.Length == 0 || value == "null" ? null : value;
    }

    /// <summary>The one reaction to a lead's request with no <c>--serves</c> while its team chain has a goal: a note, never a refusal. A goal that cannot be read means none.</summary>
    private string? ServesNudge(string dir)
    {
        try
        {
            return _teamArg is { Length: > 0 } team && RulesReads.GoalOf(_env.AhHome, dir, RulesReads.TeamChain(_graph, dir, team)) is not null
                ? $"note: team {team} has a goal; say how this request serves it: --serves \"<how>\" or --serves \"off-goal: <why>\". The request was written."
                : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>The most a body given with <c>--body-file</c> may be, in bytes.</summary>
    private const int BodyFileMax = 64 * 1024;

    /// <summary>The text of <c>--body-file</c>: the named file, or standard input for <c>-</c>; refused when unreadable, over the size limit or not UTF-8.</summary>
    private string ReadBodyFile(int max = BodyFileMax)
    {
        if (_opts.Text("body-file") is not { Length: > 0 } source)
        {
            throw _out.Fail("--body-file needs a file path, or - for standard input");
        }

        byte[] bytes;
        try
        {
            using var input = source == "-" ? _stdin() : File.OpenRead(source);
            using var held = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                held.Write(buffer, 0, read);
                if (held.Length > max)
                {
                    throw _out.Fail($"--body-file is over {max} bytes");
                }
            }

            bytes = held.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw _out.Fail($"--body-file: cannot read {source}: {e.Message}");
        }

        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException)
        {
            throw _out.Fail("--body-file is not valid UTF-8");
        }
    }

    private int New()
    {
        foreach (var key in _opts.Keys)
        {
            if (!NewFlags.Contains(key))
            {
                throw _out.Fail($"unrecognized flag --{key}");
            }
        }

        if (_opts.Positional.Count > 0)
        {
            throw _out.Fail($"unexpected argument {_opts.Positional[0]}");
        }

        RequireOwnFrom();
        var type = _opts.Text("type") ?? "request";
        if (type == "request" && !_opts.Has("body-file") && _env.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is { Length: > 0 })
        {
            throw _out.Fail("members cannot edit msgs files; write the body to a file and pass --body-file <path>");
        }

        // Checked before the body is read, and again where the request is stored, so a bad title is refused before a piped body is consumed.
        if (type == "request" && _opts.Has("title") && Hier.Messages.TitleRefusal(_opts.Text("title") ?? string.Empty) is { } titleRefusal)
        {
            throw _out.Fail(titleRefusal);
        }

        var linkedItem = ChecklistItemForRequest(type);
        var bodyText = _opts.Has("body-file") ? ReadBodyFile() : null;
        var dir = _graph.Bootstrap.EnsureHierarchyDir(HierarchyDir);
        RefuseUnrecordedAuthority(dir, bodyText);
        var toName = _opts.Text("to-name");
        (string Local, string Target)? rehomed = null;
        if (type == "request" && (toName is not null || _teamArg is not null))
        {
            var main = Config.MainHierarchyDir(_cwd);
            var target = Hier.Messages.TeamHome(dir, toName, _opts.Text("team") ?? (OwnedTeamRehomes ? _teamArg : null));
            if (target is null && toName is not null && main is { Length: > 0 } && RosterCore.ResolveMemberTeam(main, toName).Found)
            {
                target = main;
            }

            if (!string.IsNullOrEmpty(target) && !SamePath(target, dir))
            {
                rehomed = (dir, target);
                dir = target;
            }
        }

        if (_opts.IsBare("req"))
        {
            throw _out.Fail("--req needs the request file's absolute path");
        }

        var reqPath = _opts.Text("req");
        if (reqPath is not null && type != "response")
        {
            throw _out.Fail("--req applies only to --type response");
        }

        if (_opts.IsBare("note"))
        {
            throw _out.Fail("--note needs the note entry's id");
        }

        var noteId = _opts.Text("note");
        if (noteId is not null && (type != "response" || reqPath is not null || _opts.Text("id") is not null))
        {
            throw _out.Fail("--note applies only to --type response, and replaces --req and --id");
        }

        if (_opts.Has("title") && type != "request")
        {
            throw _out.Fail("--title applies only to --type request");
        }

        if (type == "request" && !_opts.Has("title") && _opts.Text("from") is "orchestrator" or "sub-orchestrator" or "deputy-chief")
        {
            throw _out.Fail($"--title is required for a request from {_opts.Text("from")}; give the dispatch a few plain words, verb first, e.g. --title \"Make UI slick\"");
        }

        var options = new JsonObject();
        options["reqPath"] = reqPath is null ? JsonValue.Null : reqPath;
        options["cwd"] = _cwd;
        options["type"] = type;
        SetOptional(options, "to", "to");
        SetOptional(options, "from", "from");
        SetOptional(options, "slug", "slug");
        SetOptional(options, "title", "title");
        if (_opts.Text("id") is { } id)
        {
            options["id"] = id;
        }

        options["parent"] = TextOrNull("parent");
        options["reason"] = TextOrNull("reason");
        options["eta"] = TextOrNull("eta");
        options["after"] = TextOrNull("after");
        options["item"] = linkedItem is null ? JsonValue.Null : linkedItem;
        var serves = ServesOf(type);
        options["serves"] = serves is null ? JsonValue.Null : serves;
        options["toName"] = TextOrNull("to-name");
        options["fromName"] = TextOrNull("from-name");
        options["markerName"] = _env.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is { Length: > 0 } marker ? marker : JsonValue.Null;
        options["team"] = _teamArg is null ? JsonValue.Null : _teamArg;
        if (bodyText is not null)
        {
            options["body"] = bodyText;
        }

        if (options["fromName"].IsNull && options["markerName"].IsNull)
        {
            options["paneName"] = PaneMemberName();
        }

        if (type == "request" && _opts.Text("from") is { } fromText)
        {
            FromOwnName(options, fromText);
        }

        // ah2-49: a lead-class request records the teams its filer leads, so an assignment counts it only in one of them
        if (type == "request" && options["from"] is { IsText: true } leadFrom && leadFrom.AsString() is "orchestrator" or "sub-orchestrator" or "deputy-chief")
        {
            // ResolveCaller, not the stored boxes: it proves ownership afresh, so a registration written before a child team named
            // this process (or after a deputy's duty ended) cannot add or drop a lead
            var sessions = SessionId is { } one ? [one] : HeldSessions().DefaultIfEmpty(null).ToList();
            var leads = sessions.SelectMany(s => _graph.Callers.ResolveCaller(CallerFor(s))).Select(b => b.Address.Split('@')).Where(p => p is ["orchestrator" or "chief", { Length: > 0 }]).Select(p => p[1]).Distinct().ToList();
            options["fromLeads"] = leads.Count == 0 ? "-" : string.Join(',', leads); // "-" (leads none) is no team name: those start with a letter
        }

        if (noteId is not null)
        {
            var sender = NoteSender(noteId);
            options["note"] = noteId;
            options["to"] = sender.Role;
            options["toName"] = sender.Name is null ? JsonValue.Null : sender.Name;
        }

        RefuseUnclaimedPool(dir);

        JsonValue res;
        try
        {
            res = Hier.Messages.CreateMessage(dir, options);
        }
        catch (InvalidOperationException e) when (e.Data["error_code"] is "state_timeout")
        {
            if (_opts.IsBare("json"))
            {
                var maybe = e.Data["id"] is string && e.Data["path"] is string;
                _out.Json(new JsonObject
                {
                    ["error"] = "state_timeout",
                    ["id"] = e.Data["id"] is string maybeId ? maybeId : JsonValue.Null,
                    ["path"] = e.Data["path"] is string maybePath ? maybePath : JsonValue.Null,
                    ["maybe_written"] = maybe,
                });
            }

            throw _out.Fail(e.Message);
        }

        var resId = JsConvert.ToJsString(res.Member("id") ?? JsonValue.Null);
        var resPath = JsConvert.ToJsString(res.Member("path") ?? JsonValue.Null);
        var existing = res.Member("existing") is { IsTruthy: true };
        if (type == "request" && bodyText is not null && !existing && ReviewIntentWarning(resPath) is { } warning)
        {
            _out.Note(warning);
        }

        if (type == "request" && serves is null && _opts.Text("from") is "orchestrator" or "sub-orchestrator" or "deputy-chief" && ServesNudge(dir) is { } nudge)
        {
            _out.Note(nudge);
        }

        string? checklistHint = null;
        if (linkedItem is not null && !existing)
        {
            _ = ChecklistDispatched(resId, linkedItem);
        }
        else if (type == "response")
        {
            ChecklistReplied(resId, bodyText);
        }
        else if (type == "request" && !existing && _opts.Text("from") is "orchestrator" or "sub-orchestrator" or "deputy-chief")
        {
            ChecklistBriefed(resId);
            if (_opts.Text("slug") != HierarchyModule.PipelineAnchorSlug && _opts.Text("reason") != HierarchyModule.AutoTrackedNoteReason)
            {
                checklistHint = ChecklistAutoLinked(resId);
            }
        }

        if (type == "request" && !existing && _opts.Text("from") is "orchestrator" or "sub-orchestrator" or "deputy-chief" && _opts.Text("parent") is { Length: > 0 } parent)
        {
            ChecklistVerdictActed(parent);
        }

        if (_plain)
        {
            _out.Text($"{resPath}  {resId}" + (existing ? "  existing" : string.Empty)); // the plain form from before 7902a3827 (keep every API)
        }
        else
        {
            var written = new JsonObject();
            written["id"] = resId;
            written["type"] = type;
            if (!string.IsNullOrEmpty(resPath) && resPath != "null")
            {
                written["path"] = resPath; // kept for callers that read the file (review-d2: keep every API)
            }

            written["existing"] = existing;
            if (existing)
            {
                if (res.Member("duplicates") is { IsArray: true } duplicates)
                {
                    written["duplicates"] = duplicates;
                }
            }

            _out.Json(written);
        }

        if (rehomed is { } moved && toName is not null && RosterCore.ResolveMemberTeam(moved.Target, toName).Found)
        {
            _out.Note($"ah msg: note — \"{toName}\" is a member of a Team under {moved.Target}, not {moved.Local}; the request was written to that Team's pool");
        }

        if (res.Member("divergent") is { IsMap: true } divergent)
        {
            var local = JsConvert.ToJsString(divergent.Member("local") ?? JsonValue.Null);
            var target = JsConvert.ToJsString(divergent.Member("target") ?? JsonValue.Null);
            _out.Note($"ah msg: note — this session's own pool is {local}; the response was written beside its request under {target}, since a response always goes in its request's pool");
        }

        if (checklistHint is not null)
        {
            _stderr.Write(checklistHint);
        }

        return 0;
    }

    /// <summary>The note entry <paramref name="noteId"/> in one of the caller's own boxes, or null. Never another box and never another tree.</summary>
    private JsonObject? CallerNote(string noteId)
    {
        foreach (var box in Me())
        {
            foreach (var entry in _graph.Mailbox.ReadEntries(box.Dir).Entries)
            {
                if (entry.IsMap && entry.Member("kind") is { IsText: true } kind && kind.AsString() == "note"
                    && entry.Member("id") is { IsText: true } id && id.AsString() == noteId)
                {
                    return entry.AsObject();
                }
            }
        }

        return null;
    }

    /// <summary>The role and member name a response to note <paramref name="noteId"/> is addressed to, from the entry's <c>from</c> address.</summary>
    private (string Role, string? Name) NoteSender(string noteId)
    {
        var note = CallerNote(noteId) ?? throw _out.Fail($"no note {noteId} in your inbox");
        var address = note.Get("from") is { IsText: true } from ? from.AsString() : string.Empty;
        var parts = address.Split('@');
        var name = parts[0];
        if (parts.Length == 2)
        {
            var member = RosterCore.TeamMemberByName(HierarchyDir, name, parts[1]);
            if (member.Member("role") is { IsText: true } role && role.AsString().Length > 0)
            {
                return (role.AsString(), name);
            }
        }

        if (name == "orchestrator")
        {
            return (name, null);
        }

        throw _out.Fail($"--note: the sender of note {noteId} ({(address.Length == 0 ? "none" : address)}) is not a member of a team here, so there is no role to address the response to");
    }

    private void SetOptional(JsonObject options, string key, string flag)
    {
        if (_opts.Text(flag) is { } text)
        {
            options[key] = text;
        }
        else if (_opts.Has(flag))
        {
            options[key] = JsonValue.True;
        }
    }

    private JsonValue TextOrNull(string flag) => _opts.Text(flag) is { } text ? text : JsonValue.Null;

    /// <summary>The name of the one team member holding this herdr pane; null when there is no pane, no match, or more than one.</summary>
    private JsonValue PaneMemberName()
    {
        if (Ah.Core.Mux.HerdrFlavour.SessionPaneId(_env.GetVariable) is not { Length: > 0 } pane)
        {
            return JsonValue.Null;
        }

        var homes = Config.MainHierarchyDir(_cwd) is { Length: > 0 } main ? new[] { HierarchyDir, main } : [HierarchyDir];
        var located = RosterCore.LocateSession(HierarchyDir, homes, _teamArg, _defaultTeamExplicit, pane);
        return located.Get("answeredBy") is { IsText: true } && located.Get("member") is { IsMap: true } member
            && member.Member("name") is { IsText: true } name
            ? name
            : JsonValue.Null;
    }

    private bool SamePath(string a, string b)
    {
        try
        {
            return _graph.Io.Files.RealPath(a) == _graph.Io.Files.RealPath(b);
        }
        catch (Exception)
        {
            return a == b;
        }
    }

    // ---------------------------------------------------------------- fill

    private int Fill()
    {
        RequireOwnFrom();
        if (_opts.Has("body-file") && _opts.Has("body"))
        {
            throw _out.Fail("fill: pass the report with --body or --body-file, not both");
        }

        if (_opts.Text("id") is not { Length: > 0 } id)
        {
            throw _out.Fail("fill: --id is required");
        }

        if (_opts.IsBare("req"))
        {
            throw _out.Fail("--req needs the request's absolute path; or leave it out, --id names the request");
        }

        var amend = _opts.Has("request");
        var part = !amend && _opts.Has("part");
        // a --req that is relative or names no message is left to FillPlan's refusals; only a real request may get a response created
        var req = _opts.Text("req") ?? ResolveHandle(id, "request");
        var reqKnown = Path.IsPathRooted(req) && Hier.Core.MsgExists(req);
        if (reqKnown && KeyPart(req, 0) is { } reqId && reqId != id)
        {
            throw _out.Fail($"fill: --req: request id {Quote(reqId)} does not match --id {Quote(id)}");
        }

        // The file holds the report as plain text; the plan wants it as one JSON string, as --body gives it.
        var body = _opts.Has("body-file") ? System.Text.Encoding.UTF8.GetString(JsonText.ToUtf8(JsonValue.FromString(ReadBodyFile(HierarchyModule.FillBodyMax)), 0)) : _opts.Text("body");
        var args = new JsonObject();
        args["id"] = id;
        SetIfText(args, "from", "from");
        args["reqPath"] = req;
        args["amend"] = amend;
        args["part"] = part;
        if (reqKnown && !amend && Hier.Messages.ResponsePlan(req) is { IsMap: true } plan && plan.Member("path") is { IsText: true } responsePath
            && !Hier.Core.MsgExists(responsePath.AsString()))
        {
            // ponytail: a fill refused after this create leaves the skeleton behind, as msg new then a refused fill did
            CreateResponseFor(id, req);
        }

        var stacked = false;
        if (_opts.Texts("section") is { Count: > 0 } sections && body is not null)
        {
            if (sections.Count > 1)
            {
                throw _out.Fail("fill: --section takes one section");
            }

            var target = amend ? req : Hier.Messages.ResponsePlan(req).Member("path")!.Value.AsString();
            var sectionText = JsonText.Parse(body) is { IsText: true } plain ? plain.AsString() : throw _out.Fail("fill: --body must be a JSON string");
            body = System.Text.Encoding.UTF8.GetString(JsonText.ToUtf8(JsonValue.FromString(SplicedBody(MessageTextAt(target, id), sections[0], sectionText)), 0));
        }
        else if (!amend && !part && body is not null && reqKnown && Hier.Messages.ResponsePlan(req).Member("path") is { IsText: true } stored
            && Hier.Core.MsgExists(stored.AsString()) && EarlierReportKept(MessageTextAt(stored.AsString(), id), body) is { } kept)
        {
            body = kept;
            stacked = true;
        }

        if (body is not null)
        {
            args["body"] = body;
        }

        JsonObject res;
        try
        {
            _ = Hier.Messages.FillPlan(args);
            res = Hier.StateCalls.FillResponse(id, _opts.Text("from")!, req, body!, amend: amend, part: part);
        }
        catch (InvalidOperationException e) when (stacked && (e.Message.Contains("--body is over ", StringComparison.Ordinal) || e.Message.Contains("a sealed message holds at most", StringComparison.Ordinal)))
        {
            // b485: the cap counts the earlier reports a repeat fill keeps, so say that nothing was lost and how to go on
            var refusal = new InvalidOperationException($"{e.Message}; earlier reports are kept; file this one as a new request with `ah msg new --parent {id}`", e);
            foreach (System.Collections.DictionaryEntry d in e.Data)
            {
                refusal.Data[d.Key] = d.Value;
            }

            throw refusal;
        }

        if (!amend && !part)
        {
            ChecklistReplied(id, FilledText(body!));
        }

        if (_plain)
        {
            _out.Text($"{JsConvert.ToJsString(res.Get("path") ?? JsonValue.Null)}  {id}"); // the plain form from before 7902a3827 (keep every API)
        }
        else
        {
            _out.Json(new JsonObject
            {
                ["id"] = id,
                ["type"] = amend ? "request" : "response",
                ["path"] = res.Get("path") ?? JsonValue.Null, // kept from before 7902a3827 (keep every API)
                ["rev"] = res.Get("rev") ?? JsonValue.Null,
                ["bytes"] = res.Get("bytes") ?? JsonValue.Null,
            });
        }

        return 0;
    }

    /// <summary>Files the response skeleton to request <paramref name="req"/>, as <c>msg new --type response --req</c> does, so one fill files a report.</summary>
    private void CreateResponseFor(string id, string req)
    {
        var options = new JsonObject
        {
            ["reqPath"] = req,
            ["cwd"] = _cwd,
            ["type"] = "response",
            ["id"] = id,
            ["markerName"] = _env.GetVariable(Ah.Mesh.MeshModule.MemberMarkerEnv) is { Length: > 0 } marker ? marker : JsonValue.Null,
            ["team"] = _teamArg is null ? JsonValue.Null : _teamArg,
        };
        SetOptional(options, "from", "from");
        if (options["markerName"].IsNull)
        {
            options["paneName"] = PaneMemberName();
        }

        _ = Hier.Messages.CreateMessage(_graph.Bootstrap.EnsureHierarchyDir(HierarchyDir), options);
    }

    /// <summary>
    /// b485: a plain fill over a response that already holds a report puts the new body first, then <c>## earlier report</c>, then the
    /// earlier body byte for byte, so first-match readers of status and tldr read the newest report. Null leaves the body as given:
    /// over the skeleton (the first report replaces it), over parts (FillPlan appends those itself), or for a body FillPlan refuses.
    /// </summary>
    private static string? EarlierReportKept(string stored, string body)
    {
        var lines = stored.Split('\n');
        var start = BodyStart(lines);
        if (!MessageFront.HasAuthoredContent(stored)
            || lines.Take(start).Any(static l => l.StartsWith("parts:", StringComparison.Ordinal) || l.StartsWith("partial:", StringComparison.Ordinal)))
        {
            return null;
        }

        JsonValue parsed;
        try
        {
            parsed = JsonText.Parse(body);
        }
        catch (JsonParseException)
        {
            return null;
        }

        if (!parsed.IsText || JsText.Trim(parsed.AsString()).Length == 0)
        {
            return null;
        }

        var text = parsed.AsString();
        var composed = (text.EndsWith('\n') ? text : text + "\n") + "## earlier report\n" + string.Join('\n', lines.Skip(start));
        return System.Text.Encoding.UTF8.GetString(JsonText.ToUtf8(JsonValue.FromString(composed), 0));
    }

    private static string FilledText(string body)
    {
        try
        {
            return JsonText.Parse(System.Text.Encoding.UTF8.GetBytes(body)) is { IsText: true } text ? text.AsString() : body;
        }
        catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return body;
        }
    }

    private int ListLimit(string which)
    {
        if (_opts.Text("limit") is not { } text)
        {
            return which == "open" ? 0 : 200;
        }

        return int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n
            : throw _out.Fail($"ah msg list: --limit takes a whole number (0 = all), got '{text}'");
    }

    private void SetIfText(JsonObject target, string key, string flag)
    {
        if (_opts.Text(flag) is { } text)
        {
            target[key] = text;
        }
    }

    // ---------------------------------------------------------------- list, downstream, index, sweep

    private int List()
    {
        var dir = _teamArg is not null && OwnedTeamRehomes && Hier.Messages.TeamHome(HierarchyDir, null, _teamArg) is { Length: > 0 } teamHome ? teamHome : HierarchyDir;
        var which = _opts.Has("all") ? "all" : _opts.Has("closed") ? "closed" : "open";
        var span = Ah.Core.Time.PhaseTrace.Begin();
        var pools = Hier.Messages.PoolDirs(dir, _everyTeam is { } every0 ? [.. every0.Select(static t => t ?? "team")] : [_teamArg ?? "team"]);
        Ah.Core.Time.PhaseTrace.End("list.pool-dirs", span);
        var slug = _opts.Text("slug");
        if (slug is not null && _opts.Has("count"))
        {
            // Orchestrator ruling R1 (delta-status): every exchange of the slug, open and closed; a dec- slug counts no rounds
            _out.Json(new JsonObject { ["slug"] = slug, ["count"] = slug.StartsWith("dec-", StringComparison.Ordinal) ? 0 : Hier.Core.CountSlugExchanges(pools, slug) });
            return 0;
        }

        span = Ah.Core.Time.PhaseTrace.Begin();

        // --closed/--all with a limit page the newest ids instead of listing history: the live scope holds every open exchange
        var limit = ListLimit(which);
        IReadOnlyList<string> teams = _everyTeam is { } everyTeam ? [.. everyTeam.Select(static t => t ?? string.Empty)] : [_teamArg ?? string.Empty];
        var firstPage = slug is null && which != "open" && limit > 0 ? Hier.Core.ExchangeRowsBefore(pools, null, limit + 1, teams, _opts.Text("to")) : null;
        // a paged --all needs no listing: only --closed leaves out the open exchanges, which the live listing holds
        var items = slug is not null
            ? Hier.Core.ListSlugExchanges(pools, slug).Where(e => e.Member("slug") is { IsText: true } s && s.AsString() == slug).ToList()
            : firstPage is not null && which == "all" ? [] : Hier.Core.ListExchanges(pools, live: which == "open" || firstPage is not null).ToList();
        Ah.Core.Time.PhaseTrace.End("list.list-exchanges", span);
        span = Ah.Core.Time.PhaseTrace.Begin();
        var withdrawn = _graph.Mailbox.WithdrawnRequestFiles();
        Ah.Core.Time.PhaseTrace.End("list.withdrawn-files", span);
        span = Ah.Core.Time.PhaseTrace.Begin();
        bool IsWithdrawn(JsonValue e) => e.Member("open")?.IsTruthy == true && e.Member("request")?.Member("path") is { IsText: true } p && withdrawn.Contains(Path.GetFileName(p.AsString()));
        List<(JsonValue E, string? Tag)> tagged;
        // the tags of the rows the team filter dropped; null in the bounded --closed/--all paging path, which does not count them
        List<string?>? hidden = null;
        int more;
        bool moreBounded;
        if (firstPage is not null)
        {
            (tagged, more, moreBounded) = BoundedList(pools, which, limit, _opts.Text("to"), teams, firstPage, items, IsWithdrawn);
        }
        else
        {
        if (which == "open")
        {
            items = items.Where(e => e.Member("open")?.IsTruthy == true && !IsWithdrawn(e)).ToList();
        }

        if (which == "closed")
        {
            items = items.Where(e => e.Member("open")?.IsTruthy != true || IsWithdrawn(e)).ToList();
        }

        if (_opts.Text("to") is { } to)
        {
            items = items.Where(e => e.Member("to") is { IsText: true } t && t.AsString() == to).ToList();
        }

        // --closed/--all show the newest 200 unless --limit says otherwise (0 = all); request files are read a chunk at a time,
        // so a bounded list never reads the rest of history
        tagged = [];
        hidden = [];
        var scanned = 0;
        while (scanned < items.Count && (limit == 0 || tagged.Count < limit))
        {
            var chunk = items.Skip(scanned).Take(limit == 0 ? items.Count : Math.Max(limit - tagged.Count, 64)).ToList();
            var paths = chunk.Select(static e => e.Member("request")?.Member("path") is { IsText: true } p ? p.AsString() : string.Empty).ToList();

            // the stored team header answers; a request with no row reads as absent
            var stored = Hier.Core.MsgTeams(paths);
            var unread = paths.Where(p => !stored.ContainsKey(p)).ToList();
            var requestFiles = unread.Count > 0 ? Hier.Core.ReadMsgFiles(unread) : [];
            var next = 0;
            string? Tag(string path) => stored.TryGetValue(path, out var team)
                ? (string.IsNullOrEmpty(team) ? null : team)
                : TeamTagOf(requestFiles[next++]);
            foreach (var t in paths.Select((p, i) => (E: chunk[i], Tag: Tag(p))).ToList())
            {
                if (_everyTeam is { } every ? every.Contains(t.Tag) : t.Tag == _teamArg)
                {
                    tagged.Add(t);
                }
                else
                {
                    hidden.Add(t.Tag);
                }
            }

            scanned += chunk.Count;
        }

        // rows past the limit: the overshoot of the last chunk is exact; the unscanned rest is not team-filtered, so it is an upper bound
        more = limit > 0 && tagged.Count > limit ? tagged.Count - limit : 0;
        moreBounded = scanned < items.Count;
        more += items.Count - scanned;
        if (limit > 0 && tagged.Count > limit)
        {
            tagged.RemoveRange(limit, tagged.Count - limit);
        }
        }

        Ah.Core.Time.PhaseTrace.End("list.filter", span);

        span = Ah.Core.Time.PhaseTrace.Begin();
        var rows = new JsonArray();
        var texts = new List<string>();
        foreach (var (e, itemTag) in tagged)
        {
            var open = e.Member("open")?.IsTruthy == true;
            var age = Hier.Messages.FmtAge(Hier.Messages.ExchangeAgeSec(e));
            var row = new JsonObject();
            row["id"] = e.Member("id") ?? JsonValue.Null;
            row["to"] = e.Member("to") ?? JsonValue.Null;
            row["slug"] = e.Member("slug") ?? JsonValue.Null;
            row["age"] = age;
            var (title, titleSource) = Hier.Messages.ExchangeTitle(e);
            row["title"] = title is null ? JsonValue.Null : title;
            row["title_source"] = titleSource is null ? JsonValue.Null : titleSource;
            var state = IsWithdrawn(e) ? "withdrawn" : open ? "open" : "closed";
            row["state"] = state;
            // request/response paths kept from before 7902a3827 (keep every API)
            row["request"] = e.Member("request")?.Member("path") ?? JsonValue.Null;
            row["response"] = e.Member("response") is { IsMap: true } response ? response.Member("path") ?? JsonValue.Null : JsonValue.Null;
            row["has_response"] = e.Member("response") is { IsMap: true };
            // b375: an open request whose response holds partial reports shows how many
            var parts = open && row["response"] is { IsText: true } rp && Hier.Core.MsgBytes(rp.AsString()) is { } rb
                && System.Text.Encoding.UTF8.GetString(rb) is var rt && MsgRows.IsPartial(rt)
                && MessageFront.ParseFrontmatter(rt)?.Fields.Get("parts") is { IsText: true } pc ? pc.AsString() : null;
            if (parts is not null)
            {
                row["parts"] = parts;
                state += $" ({parts} parts)";
            }

            string? tag = null;
            if (_everyTeam is not null)
            {
                tag = itemTag;
                row["team"] = tag is null ? JsonValue.Null : tag;
            }

            var pool = pools.Count > 1 ? PoolLabel(dir, e) : null;
            if (pool is not null)
            {
                row["pool"] = pool;
            }

            rows.Add(row);
            texts.Add(
                $"{JsConvert.ToJsString(row["id"])}  {JsConvert.ToJsString(row["to"])}  {JsConvert.ToJsString(row["slug"])}  {age}  {state}"
                + (_everyTeam is not null ? $"  team={RosterModule.TeamArgName(tag)}" : string.Empty)
                + (pool is not null ? $"  pool={pool}" : string.Empty)
                + (title is not null ? $"  {title}" : string.Empty));
        }

        Ah.Core.Time.PhaseTrace.End("list.rows", span);
        if (rows.Count == 0 && hidden is { Count: > 0 })
        {
            // ah2-344 D3 / Amendment 3: an empty listing whose team filter hid exchanges says on stderr which filter, which pools and what it hid
            var filter = _everyTeam is { } every1 ? string.Join(",", every1.Select(RosterModule.TeamArgName)) : RosterModule.TeamArgName(_teamArg);
            var byTag = hidden.GroupBy(RosterModule.TeamArgName).Select(static g => $"{g.Key}: {g.Count()}").ToList();
            var hint = hidden.Select(RosterModule.TeamArgName).Distinct().ToList() is [var only] ? only : "<name>";
            _out.Note($"ah msg list: no {which} exchanges for team={filter} in {pools.Count} pool{(pools.Count == 1 ? string.Empty : "s")} ({string.Join(", ", pools)}); "
                + $"{hidden.Count} hidden by the team filter ({string.Join(", ", byTag)}) — pass --team {hint}");
        }

        span = Ah.Core.Time.PhaseTrace.Begin();
        // Orchestrator ruling on ah-msgs-fast-2 §3.4: the live scope by default, the newest --limit requests with --all/--closed
        var downstream = limit == 0 && which != "open" ? Hier.Messages.ListDownstreamDispatches(dir) : Hier.Messages.ListDownstreamDispatches(dir, which == "open" ? 0 : limit);
        Ah.Core.Time.PhaseTrace.End("list.downstream", span);
        if (_plain)
        {
            if (more > 0)
            {
                texts.Add($"{(moreBounded ? "up to " : string.Empty)}{more} more (--limit N, 0 = all)");
            }

            var rowsText = string.Join("\n", texts);
            var lines = downstream.Select(DownstreamLine);
            var text = downstream.Count > 0
                ? (rowsText.Length > 0 ? $"{rowsText}\n\ndownstream:\n" : "downstream:\n") + string.Join("\n", lines)
                : rowsText;
            _out.Text(text);
        }
        else if (downstream.Count > 0)
        {
            var wrapped = new JsonObject();
            wrapped["exchanges"] = rows;
            wrapped["downstream"] = downstream;
            _out.Json(wrapped);
        }
        else
        {
            _out.Json(rows);
        }

        return 0;
    }

    /// <summary>
    /// <c>--all</c>/<c>--closed</c> with a limit in db mode: the rows, the count past the limit and whether it is an upper bound. Ids come
    /// newest first a page at a time, the team and recipient filtered by the query (review F1: never O(history)) and tested again on the row
    /// the full listing pairs (the first pool's); <c>--closed</c> leaves out the open ones (from <paramref name="live"/>, which holds every
    /// one); only the rows kept are paired. Past the limit the count is the query's, less the ids already turned down: an upper bound.
    /// </summary>
    private (List<(JsonValue E, string? Tag)> Tagged, int More, bool MoreBounded) BoundedList(
        IReadOnlyList<string> pools, string which, int limit, string? to, IReadOnlyList<string> teams,
        IReadOnlyList<(string Id, string To, string? Team, string Pool)> firstPage, List<JsonValue> live, Func<JsonValue, bool> isWithdrawn)
    {
        var hidden = which == "closed"
            ? live.Where(e => e.Member("open")?.IsTruthy == true && !isWithdrawn(e) && (to is null || (e.Member("to") is { IsText: true } t && t.AsString() == to)))
                .Select(static e => e.Member("id")!.Value.AsString()).ToHashSet(StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var keys = pools.Select(MsgRows.PoolKey).ToList();
        int Rank(string pool) => keys.IndexOf(pool) is >= 0 and var i ? i : int.MaxValue;
        var picked = new List<(string Id, string? Tag)>();
        var turnedDown = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rows = firstPage;
        while (true)
        {
            var ids = rows.Select(static r => r.Id).Distinct(StringComparer.Ordinal).Count();
            foreach (var r in rows.GroupBy(static r => r.Id, StringComparer.Ordinal).Select(g => g.OrderBy(x => Rank(x.Pool)).First()))
            {
                _ = seen.Add(r.Id);
                var tag = string.IsNullOrEmpty(r.Team) ? null : r.Team;
                if ((to is null || r.To == to) && !hidden.Contains(r.Id) && (_everyTeam is { } every ? every.Contains(tag) : tag == _teamArg))
                {
                    picked.Add((r.Id, tag));
                }
                else
                {
                    turnedDown++;
                }
            }

            if (picked.Count > limit || ids < limit + 1)
            {
                break;
            }

            rows = Hier.Core.ExchangeRowsBefore(pools, rows[^1].Id, limit + 1, teams, to);
        }

        var more = 0;
        var moreBounded = false;
        if (picked.Count > limit)
        {
            // the open ids --closed leaves out that the query counts and the pages never reached are not more either; every id the
            // query counts was seen: the count is exact
            var unseen = live.Where(e => e.Member("id") is { IsText: true } id && hidden.Contains(id.AsString()) && !seen.Contains(id.AsString()))
                .Select(static e => e.Member("request")?.Member("path") is { IsText: true } p ? p.AsString() : string.Empty).ToList();
            var stored = unseen.Count > 0 ? Hier.Core.MsgTeams(unseen) : null;
            var counted = Hier.Core.ExchangeCount(pools, teams, to)
                - unseen.Count(p => stored is not null && stored.TryGetValue(p, out var team) && teams.Contains(team ?? string.Empty));
            more = (int)Math.Max(picked.Count - limit, counted - limit - turnedDown);
            moreBounded = counted > picked.Count + turnedDown;
            picked.RemoveRange(limit, picked.Count - limit);
        }

        var byId = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var e in Hier.Core.ListExchangesWithIds(pools, [.. picked.Select(static p => p.Id)]))
        {
            byId[e.Member("id")!.Value.AsString()] = e;
        }

        return ([.. picked.Where(p => byId.ContainsKey(p.Id)).Select(p => (byId[p.Id], p.Tag))], more, moreBounded);
    }

    /// <summary><c>main</c> for an exchange whose request is in the main pool, else the name of the work tree whose pool holds it.</summary>
    private static string PoolLabel(string mainDir, JsonValue exchange)
    {
        var path = exchange.Member("request")?.Member("path") is { IsText: true } p ? p.AsString() : string.Empty;
        var pool = NodePath.Dirname(NodePath.Dirname(path));
        return pool == mainDir ? "main" : NodePath.Basename(NodePath.Dirname(NodePath.Dirname(pool)));
    }

    private static string DownstreamLine(JsonValue d)
    {
        string S(string key) => JsConvert.ToJsString(d.Member(key) ?? JsonValue.Null);
        var tag = d.Member("identity") is { IsText: true } id && id.AsString() == "role-only" ? ", role-only" : string.Empty;
        return $"{S("root_from_name")} → {S("from_name")} → {S("to")}: {S("slug")}  (id {S("id")}, parent {S("parent")}{tag})";
    }

    private int Downstream()
    {
        var rows = Hier.Messages.ListDownstreamDispatches(HierarchyDir);
        IEnumerable<JsonValue> shown = rows;
        if (_opts.Text("root-name") is { } root)
        {
            shown = rows.Where(r => r.Member("root_from_name") is { IsText: true } n && n.AsString() == root);
        }

        var list = shown.ToList();
        if (_plain)
        {
            _out.Text(string.Join("\n", list.Select(DownstreamLine)));
        }
        else
        {
            var array = new JsonArray();
            array.AddRange(list);
            _out.Json(array);
        }

        return 0;
    }

    private int Index()
    {
        var (path, id) = HandleFromPositional("index");
        var text = MessageTextAt(path, id);
        var anchors = Hier.Messages.IndexAnchors(text);
        if (_plain)
        {
            _out.Text(string.Join("\n", anchors.Select(a => $"{JsConvert.ToJsString(a.Member("line") ?? JsonValue.Null)}:{JsConvert.ToJsString(a.Member("text") ?? JsonValue.Null)}")));
        }
        else
        {
            var array = new JsonArray();
            foreach (var a in anchors)
            {
                var row = new JsonObject();
                row["line"] = a.Member("line") ?? JsonValue.Null;
                row["n"] = a.Member("n") ?? JsonValue.Null;
                row["key"] = a.Member("key") ?? JsonValue.Null;
                array.Add(row);
            }

            _out.Json(array);
        }

        return 0;
    }

    private int Sweep()
    {
        double? days = null;
        if (_opts.Has("days"))
        {
            var number = _opts.IsBare("days") ? 1 : MsgOptions.ToNumber(_opts.Text("days"));
            if (double.IsNaN(number) || double.IsInfinity(number) || number < 0)
            {
                var shown = _opts.Text("days") is { } raw ? Quote(raw) : "true";
                throw _out.Fail($"--days must be a non-negative number, got {shown}");
            }

            days = number;
        }

        var moved = Hier.Messages.Sweep(HierarchyDir, days);
        if (_plain)
        {
            _out.Text(moved.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            var result = new JsonObject();
            result["archived"] = moved;
            _out.Json(result);
        }

        return 0;
    }

    // ---------------------------------------------------------------- roster

    private List<string> TableLines(JsonObject resolved, JsonObject ros)
    {
        var lines = new List<string>();
        foreach (var role in Config.ChainRoles(resolved))
        {
            var list = ros.Get(role) is { IsArray: true } a ? a.AsArray() : new JsonArray();
            if (list.Count == 0)
            {
                lines.Add($"{role}: none");
            }

            foreach (var i in list)
            {
                string S(string key) => JsConvert.ToJsString(i.Member(key) ?? JsonValue.Null);
                var live = i.Member("live")?.IsTruthy == true ? "live" : "stale";
                var busy = i.Member("busy")?.IsTruthy == true ? "  busy" : string.Empty;
                var task = i.Member("task") is { IsTruthy: true } tk ? $"  task={JsConvert.ToJsString(tk)}" : string.Empty;
                var unassigned = i.Member("unassigned") is { IsTruthy: true } un ? $" ({JsConvert.ToJsString(un)} to role, no instance yet)" : string.Empty;
                var ago = Hier.Messages.FmtAge(i.Member("ageSec") ?? JsonValue.Null);
                lines.Add($"{role}: {S("name")}  {live}  {S("how")}  {ago} ago{busy}{task}  open={S("openBriefs")}{unassigned}");
            }
        }

        return lines;
    }

    /// <summary>
    /// Refuses, before any daemon call, a pool the daemon would refuse: the directories of the write context must exist and be directories, and
    /// the hierarchy directories must be derived from the working directory or trusted. The daemon's own checks stay; this only answers sooner.
    /// </summary>
    private void RefuseUnclaimedPool(string pool)
    {
        var context = Hier.Messages.PoolContext(pool);
        foreach (var (field, path) in new[] { ("cwd", context.Cwd), ("hierarchy_dir", context.HierarchyDirectory), ("main_hierarchy_dir", context.MainHierarchyDirectory) })
        {
            // A hierarchy directory that is missing is created by the write, so only the working directory has to exist.
            if (path.Length > 0 && path.StartsWith('/') && !Directory.Exists(path) && (field == "cwd" || File.Exists(path)))
            {
                throw _out.Fail(File.Exists(path) ? $"{field} {path} is not a directory" : $"{field} {path} does not exist");
            }
        }

        if (_graph.HierarchyPolicy.CheckClaim(context) is { } problem)
        {
            throw _out.Fail(problem.Message);
        }
    }

    private bool IsOwnQualifiedName(string from) =>
        from.Split('@') is [{ Length: > 0 } name, { Length: > 0 } team] && name != "orchestrator" && IsOwnName(name, team, nameOnly: true);

    /// <summary><c>--from</c>, when given, must be the name or the role of one of the caller's own boxes.</summary>
    private void RequireOwnFrom()
    {
        if (_opts.Text("from") is not { } from || IsOwnName(from) || IsOwnQualifiedName(from))
        {
            return;
        }

        var own = Me();
        var (roles, names) = ValidFromValues();
        var addresses = own.Count == 0 ? string.Empty : $" (this caller is {string.Join(", ", own.Select(b => b.Address))})";
        throw _out.Fail($"not_your_session: --from must be one of {string.Join(", ", roles)} or your member name {string.Join(", ", names)}; got {Quote(from)}{addresses}");
    }

    /// <summary>The roles and member names this caller may pass as <c>--from</c>: one role and one name per box it owns.</summary>
    private (List<string> Roles, List<string> Names) ValidFromValues()
    {
        var roles = new List<string>();
        var names = new List<string>();
        foreach (var box in Me())
        {
            var role = BoxRole(box);
            if (role is not null && !roles.Contains(role))
            {
                roles.Add(role);
            }

            if (_graph.Mailbox.SplitAddress(box.Address)?.Name is { Length: > 0 } name && name != "orchestrator" && !names.Contains(name))
            {
                names.Add(name);
            }
        }

        return (roles, names);
    }

    private string? BoxRole(Ah.Mesh.CallerBox box)
    {
        if (_graph.Mailbox.SplitAddress(box.Address) is not { } parts)
        {
            return null;
        }

        if (parts.Name == "orchestrator")
        {
            return "orchestrator";
        }

        return RosterCore.TeamMemberByName(HierarchyDir, parts.Name, parts.Team).Member("role") is { IsText: true } role && role.AsString().Length > 0
            ? role.AsString()
            : null;
    }

    /// <summary>
    /// A <c>--from</c> that names the caller's own member (<c>name</c> or <c>name@team</c>) is sent as that member's role with the name as
    /// <c>from_name</c>. A value that is a role is left as it is, and so is a value that is not the caller's own name.
    /// </summary>
    private void FromOwnName(JsonObject options, string from)
    {
        var parts = from.Split('@');
        var name = parts[0];
        var team = parts.Length == 2 ? parts[1] : null;
        if (name == "orchestrator" || name.Length == 0 || parts.Length > 2)
        {
            return;
        }

        foreach (var box in Me())
        {
            if (_graph.Mailbox.SplitAddress(box.Address) is { } address && address.Name == name && (team is null || address.Team == team) && BoxRole(box) is { } role)
            {
                options["from"] = role;
                if (options["fromName"].IsNull)
                {
                    options["fromName"] = name;
                }

                return;
            }
        }
    }

    /// <summary>Whether <paramref name="from"/> is the name, or the role, of one of the caller's own boxes; with <paramref name="inTeam"/>, only boxes of that team count.</summary>
    private bool IsOwnName(string from, string? inTeam = null, bool nameOnly = false)
    {
        foreach (var box in Me())
        {
            var parts = _graph.Mailbox.SplitAddress(box.Address);
            if (parts is null || (inTeam is not null && parts.Team != inTeam))
            {
                continue;
            }

            if (parts.Name == from)
            {
                return true;
            }

            if (!nameOnly && RosterCore.ReadTeamRecord(HierarchyDir, parts.Team).Member("members") is { IsArray: true } members
                && members.AsArray().Any(m => m.Member("name") is { IsText: true } n && n.AsString() == parts.Name
                    && m.Member("role") is { IsText: true } r && r.AsString() == from))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The session <paramref name="sessionId"/> is the caller's own, or one of its addresses is in a team the caller provably orchestrates.</summary>
    private void RequireSelfOrOwner(string sessionId)
    {
        if (Principal.Kind == PrincipalKind.Session && Principal.Sessions.Any(s => s.SessionId == sessionId))
        {
            return;
        }

        if (Principal.Kind == PrincipalKind.Session && _graph.Mailbox.ReadRegistration(sessionId) is { } reg
            && _graph.Mailbox.RegistrationBoxes(reg).Any(b => Ah.Mesh.MeshModule.MayActFor(Principal, Choice(write: false), b.Address, _graph.Mailbox)))
        {
            return;
        }

        throw _out.Fail($"not_your_session: session {Quote(sessionId)} is neither this caller's nor in a team it orchestrates");
    }

    private JsonValue PidOption()
    {
        return SessionPid is { } pid ? JsonValue.FromNumber(pid) : JsonValue.Null;
    }

    private int Roster()
    {
        var dir = HierarchyDir;
        if (_everyTeam is { } every)
        {
            var parts = new List<(string? Team, JsonValue Roster, JsonObject Resolved, JsonObject Ros)>();
            foreach (var team in every)
            {
                var resolved = Config.ResolveConfig(_cwd, new ResolveOptions(Team: team, TeamHome: dir, DefaultTeam: team is null));
                var ros = Hier.Roster.Roster(dir, resolved, Config.TeamPrefix(resolved.Get("cwd") is { IsText: true } c ? c.AsString() : _cwd, team));
                var record = RosterCore.ReadTeamRecord(dir, team);
                var rosterName = record.IsMap && record.Member("roster") is { } r && !r.IsNull ? r : JsonValue.Null;
                parts.Add((team, rosterName, resolved, ros));
            }

            if (_plain)
            {
                var lines = new List<string>();
                foreach (var p in parts)
                {
                    var rosterText = p.Roster.IsNull ? "default" : JsConvert.ToJsString(p.Roster);
                    lines.Add($"Team {RosterModule.TeamArgName(p.Team)} (roster {rosterText}):");
                    lines.AddRange(TableLines(p.Resolved, p.Ros));
                }

                _out.Text(string.Join("\n", lines));
            }
            else
            {
                var teams = new JsonArray();
                foreach (var p in parts)
                {
                    var row = new JsonObject();
                    row["team"] = p.Team is null ? JsonValue.Null : p.Team;
                    row["roster"] = p.Roster;
                    row["summary"] = Hier.Roster.RosterLine(p.Ros);
                    row["roles"] = p.Ros;
                    teams.Add(row);
                }

                var body = new JsonObject();
                body["dir"] = dir;
                body["teams"] = teams;
                _out.Json(body);
            }

            return 0;
        }

        var options = _defaultTeamExplicit
            ? new ResolveOptions(TeamHome: _teamHome, DefaultTeam: true, Pid: PidOption())
            : new ResolveOptions(Team: _teamArg, TeamHome: _teamHome, Pid: PidOption());
        var single = Config.ResolveConfig(_cwd, options);
        var repo = Config.TeamPrefix(
            single.Get("cwd") is { IsText: true } sc ? sc.AsString() : _cwd,
            single.Get("team") is { IsText: true } st ? st.AsString() : null);
        var table = Hier.Roster.Roster(dir, single, repo);
        if (_plain)
        {
            _out.Text(string.Join("\n", TableLines(single, table)));
        }
        else
        {
            var body = new JsonObject();
            body["dir"] = dir;
            body["summary"] = Hier.Roster.RosterLine(table);
            body["roles"] = table;
            _out.Json(body);
        }

        return 0;
    }

    // ---------------------------------------------------------------- route

    private int Route()
    {
        var dir = HierarchyDir;
        var resolved = Config.ResolveConfig(_cwd, new ResolveOptions(Pid: PidOption()));
        var sessionId = _opts.Text("session");
        if (sessionId is null)
        {
            throw _out.Fail("route needs --session <id>");
        }

        var value = _opts.Positional.Count > 0 ? _opts.Positional[0] : null;
        if (value is null)
        {
            var eff = Hier.Roster.EffectiveRoute(dir, resolved, sessionId);
            if (_plain)
            {
                _out.Text($"{JsConvert.ToJsString(eff["value"])} ({JsConvert.ToJsString(eff["source"])})");
            }
            else
            {
                _out.Json(eff);
            }

            return 0;
        }

        if (!Config.RouteValues.Contains(value))
        {
            throw _out.Fail($"route must be one of {string.Join("|", Config.RouteValues)}, got {Quote(value)} — only legwork roles run as subagents");
        }

        RequireSelfOrOwner(sessionId);
        Hier.Roster.RecordRoute(dir, sessionId, value);
        if (_plain)
        {
            _out.Text(value);
        }
        else
        {
            var recorded = new JsonObject();
            recorded["recorded"] = value;
            recorded["session_id"] = sessionId;
            _out.Json(recorded);
        }

        return 0;
    }

    // ---------------------------------------------------------------- decision

    private int Refuse(string reason, string detail, string verb = "add")
    {
        var refused = new JsonObject();
        refused["refused"] = true;
        refused["reason"] = reason;
        refused["detail"] = detail;
        _out.Json(refused);
        _out.Note($"ah msg: decision {verb} refused ({reason}): {detail}");
        return 2;
    }

    /// <summary>
    /// <c>decision answer &lt;id&gt; --choice &lt;a|b|c|d|other:text&gt;</c>: logs the user's answer to the parked decision <c>id</c> as an
    /// <c>answered</c> line, naming the caller as the one who relayed it. The hook layer keeps members and subagents from running it;
    /// nothing here proves who the caller is.
    /// </summary>
    private int DecisionAnswer(string dir)
    {
        var id = _opts.Positional.Count > 1 ? _opts.Positional[1] : null;
        var choices = _opts.Texts("choice");
        if (id is null || choices.Count == 0)
        {
            throw _out.Fail("usage: ah msg decision answer <id> --choice <a|b|c|d|other:<text>> [--choice …] [--team <name>] [--run <id>]");
        }

        var proofExit = 0;
        JsonObject? Prove(string runId, string logPath)
        {
            if (ProveDecisionCaller(DecisionAction.Answer, dir, runId, id, string.Join(",", choices), logPath, out proofExit) is not { } stamp)
            {
                return null;
            }

            var proven = new JsonObject { ["answered_by"] = stamp.AnsweredBy, ["recorded_by"] = stamp.RecordedBy, ["caller"] = stamp.Caller };
            if (stamp.SurrogateClass is { } surrogateClass)
            {
                proven["surrogate_class"] = surrogateClass;
            }

            return proven;
        }

        string? Notify(string to, string eventName, string text)
        {
            var sent = _graph.Mailbox.Send(Ah.Mesh.MeshModule.EventRequest(to, "user", _cwd, dir, eventName, text), _graph.Io.Clock.UtcNow);
            return sent.Failure is { } failure ? $"{failure.Code}: {failure.Message}" : null;
        }

        var res = Hier.Decisions.AnswerParked(
            dir,
            _teamArg is null ? JsonValue.Null : _teamArg,
            _opts.Text("run"),
            id,
            [.. choices],
            choices.Count > 1,
            Prove,
            (logPath, runId, input) => Hier.StateCalls.AppendDecision(logPath, runId, input),
            Notify,
            Hier.Progress,
            out var eventError);
        if (res is null)
        {
            return proofExit;
        }

        if (eventError is not null)
        {
            _out.Note($"ah msg: decision answer recorded; the question-answered event was not sent: {eventError}");
        }

        if (res.Get("refused") is { IsMap: true } refusal)
        {
            return Refuse(
                JsConvert.ToJsString(refusal.Member("reason") ?? JsonValue.Null),
                JsConvert.ToJsString(refusal.Member("detail") ?? JsonValue.Null),
                "answer");
        }

        _out.Json(res);
        return 0;
    }

    private int Decision()
    {
        var sub = _opts.Positional.Count > 0 ? _opts.Positional[0] : null;
        var dir = HierarchyDir;
        if (sub == "add")
        {
            var anchor = Hier.Decisions.OpenRunAnchor(dir, _teamArg is null ? JsonValue.Null : _teamArg);
            JsonValue input = JsonValue.Null;
            string runId;
            JsonValue lineRun;
            if (anchor.Get("error") is { IsTruthy: true } anchorError)
            {
                // outside a run only an operational line is logged, against the team
                runId = TeamDecisionRun();
                if (ReadDecisionAddInput(dir, runId, out input) is { } noRunInputExit)
                {
                    return noRunInputExit;
                }

                // a line decided by the user's own standing rule logs to the team log too (spec-b19 slice 3)
                if (!IsOperationalInput(input) && !_opts.Has("rule"))
                {
                    return Refuse("no-run", JsConvert.ToJsString(anchorError));
                }

                lineRun = JsonValue.Null;
            }
            else
            {
                runId = JsConvert.ToJsString(anchor["id"]);
                lineRun = runId;
            }

            var logPath = Hier.Decisions.DecisionLogPath(dir, runId);
            if (ProveDecisionCaller(DecisionAction.Add, dir, runId, null, null, logPath, out var proofExit) is not { } stamp)
            {
                return proofExit;
            }

            if (!lineRun.IsNull && ReadDecisionAddInput(dir, runId, out input) is { } inputExit)
            {
                return inputExit;
            }

            if ((_opts.Has("rule") || DeciderNamesRule(input)) && ApplyRule(input) is { } ruleExit)
            {
                return ruleExit;
            }

            if (!(input.IsMap && (IsOperationalInput(input) || (input.Member("kind") is { IsText: true } kind && kind.AsString() is "parked" or "decided" or "merge"))))
            {
                return Refuse("invalid", "add takes one object whose kind is parked or decided, or an operational line (class: operational, no kind); kind answered is written only by ah msg decision answer");
            }

            if (input.IsMap)
            {
                input.AsObject()["caller"] = stamp.Caller;
            }

            var res = Hier.StateCalls.AppendDecision(logPath, lineRun, input);
            if (res.Get("refused") is { IsMap: true } refusal)
            {
                return Refuse(
                    JsConvert.ToJsString(refusal.Member("reason") ?? JsonValue.Null),
                    JsConvert.ToJsString(refusal.Member("detail") ?? JsonValue.Null));
            }

            ParkedItemWaitsOnUser(input, res);
            _out.Json(res);
            return 0;
        }

        if (sub == "park")
        {
            return QuestionPark(dir);
        }

        if (sub == "ui-answer")
        {
            return UiAnswerVerb();
        }

        if (sub == "answer")
        {
            var target = _opts.Positional.Count > 1 ? _opts.Positional[1] : null;
            return IsQuestionId(target) ? QuestionAnswer(dir) : IsItemKey(target) ? ItemAnswer(dir) : DecisionAnswer(dir);
        }

        if (sub == "list")
        {
            if (_opts.Has("waiting"))
            {
                return DecisionWaiting(dir);
            }

            if (_opts.Has("line"))
            {
                return DecisionLine(dir);
            }

            if (_opts.Has("since") || _opts.Has("until"))
            {
                return Refuse("bad_request", "--since and --until go with --line", "list");
            }

            string run;
            if (_opts.IsBare("run"))
            {
                throw _out.Fail("decision list: --run needs an anchor id");
            }

            if (_opts.Text("run") is { } named)
            {
                if (!Hier.Decisions.KnownRun(dir, named))
                {
                    throw _out.Fail($"decision list: no run {Quote(named)} in {dir}");
                }

                run = named;
            }
            else
            {
                var anchor = Hier.Decisions.OpenRunAnchor(dir, _teamArg is null ? JsonValue.Null : _teamArg);
                if (anchor.Get("error") is { IsTruthy: true } anchorError)
                {
                    if (_opts.Has("summary"))
                    {
                        // the team's operational lines, logged outside a run, still show
                        var teamLog = Hier.Decisions.ReadDecisions(Hier.Decisions.DecisionLogPath(dir, TeamDecisionRun()));
                        var noRun = teamLog["decisions"].AsArray().Count > 0
                            ? Hier.Decisions.DecisionSummary(teamLog["decisions"].AsArray(), teamLog["skipped"], null)
                            : new JsonObject();
                        noRun["questions_waiting"] = QuestionsWaiting(dir);
                        _out.Json(noRun);
                        return 0;
                    }

                    throw _out.Fail($"decision list: {JsConvert.ToJsString(anchorError)} — pass --run <anchor id> for a finished run");
                }

                run = JsConvert.ToJsString(anchor["id"]);
            }

            var path = Hier.Decisions.DecisionLogPath(dir, run);
            var read = Hier.Decisions.ReadDecisions(path);
            var decisions = read["decisions"].AsArray();
            var skipped = read["skipped"];
            var item = _opts.Text("item");
            if (item is not null)
            {
                var filtered = new JsonArray();
                filtered.AddRange(decisions.Where(d => d.Member("item") is { IsText: true } i && i.AsString() == item));
                decisions = filtered;
            }

            if (_opts.Has("summary"))
            {
                // asks rows name no item, so a count narrowed to one item has nothing to say about them
                JsonValue? asks = null;
                if (item is null)
                {
                    var span = Hier.Decisions.DecisionRunSpan(decisions);
                    asks = Hier.Decisions.TryParseInstant(JsConvert.ToJsString(span.Get("since")), out var from)
                        && Hier.Decisions.TryParseInstant(JsConvert.ToJsString(span.Get("until")), out var to)
                        ? Hier.Decisions.AsksAnswered(dir, decisions, from, to)["answered"]
                        : JsonValue.FromNumber(0);
                }

                var summary = Hier.Decisions.DecisionSummary(decisions, skipped, asks);
                summary["questions_waiting"] = QuestionsWaiting(dir);
                _out.Json(summary);
            }
            else
            {
                var body = new JsonObject();
                body["run"] = run;
                body["path"] = path;
                body["decisions"] = decisions;
                body["skipped"] = skipped;
                _out.Json(body);
            }

            return 0;
        }

        throw _out.Fail(
            "usage: ah msg decision add [--team <name>] [--json '<object>' | --input <path>] (one JSON object from the argument or the file, else stdin) | decision answer <id> --choice <a|b|c|d|other:<text>> [--team <name>] | decision list [--team <name>] [--run <anchor id>] [--item <slug>] [--summary] | decision list --line (--run <anchor id> | --since <instant> [--until <instant>])");
    }

    /// <summary>The pseudo-run whose log holds a team's operational lines written outside a pipeline run.</summary>
    private string TeamDecisionRun() => $"team-{_teamArg ?? "default"}";

    private static bool IsOperationalInput(JsonValue input) => input.IsMap && input.AsObject().ContainsKey("class");

    /// <summary>Reads the <c>decision add</c> object from --json, --input or stdin; null on success, else the refusal's exit code.</summary>
    private int? ReadDecisionAddInput(string dir, string runId, out JsonValue input)
    {
        input = JsonValue.Null;
        if (_opts.IsBare("input"))
        {
            return Refuse("invalid", "--input needs the path of the decision input file");
        }

        if (_opts.IsBare("json"))
        {
            return Refuse("invalid", "--json needs the decision object as its value");
        }

        if (_opts.Has("json") && _opts.Has("input"))
        {
            return Refuse("invalid", "give the decision object once: --json or --input, not both");
        }

        if (_opts.Text("json") is { } inline)
        {
            if (_opts.Positional.Count > 1)
            {
                return Refuse("invalid", "decision add takes the object once, as the --json value: nothing may follow it");
            }

            try
            {
                input = JsonText.Parse(inline);
            }
            catch (JsonParseException e)
            {
                return Refuse("invalid", $"--json must hold one JSON object ({e.Message})");
            }
        }
        else if (_opts.Text("input") is { } inputPath)
        {
            var read = Hier.Decisions.ReadDecisionInput(dir, runId, inputPath);
            if (read.Get("error") is { IsTruthy: true } readError)
            {
                return Refuse("invalid", JsConvert.ToJsString(readError));
            }

            input = read["input"];
        }
        else
        {
            try
            {
                using var stream = _stdin();
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                input = JsonText.Parse(buffer.ToArray());
            }
            catch (JsonParseException e)
            {
                return Refuse("invalid", $"stdin must hold one JSON object ({e.Message})");
            }
        }

        return null;
    }

    /// <summary><c>decision list --line</c>: the one-line count for a run (<c>--run</c>) or for a window (<c>--since</c> up to <c>--until</c>).</summary>
    private int DecisionLine(string dir)
    {
        if (_opts.IsBare("run") || _opts.IsBare("since") || _opts.IsBare("until"))
        {
            return Refuse("bad_request", "--run, --since and --until each need a value", "list");
        }

        var run = _opts.Text("run");
        var sinceText = _opts.Text("since");
        var untilText = _opts.Text("until");
        if (run is not null && sinceText is not null)
        {
            return Refuse("bad_request", "give --run or --since, not both", "list");
        }

        if (run is null && sinceText is null)
        {
            return Refuse("bad_request", "give --run or --since", "list");
        }

        if (run is not null && untilText is not null)
        {
            return Refuse("bad_request", "--until goes with --since", "list");
        }

        DateTimeOffset since = default;
        DateTimeOffset? until = null;
        if (sinceText is not null && !Hier.Decisions.TryParseInstant(sinceText, out since))
        {
            return Refuse("bad_request", $"--since must be an RFC 3339 instant with Z or an offset, not {Quote(sinceText)}", "list");
        }

        if (untilText is not null)
        {
            if (!Hier.Decisions.TryParseInstant(untilText, out var parsed))
            {
                return Refuse("bad_request", $"--until must be an RFC 3339 instant with Z or an offset, not {Quote(untilText)}", "list");
            }

            until = parsed;
        }

        if (run is not null && !Hier.Decisions.KnownRun(dir, run))
        {
            throw _out.Fail($"decision list: no run {Quote(run)} in {dir}");
        }

        _out.Text(JsConvert.ToJsString(Hier.Decisions.DecisionLine(dir, run, run is null ? since : null, until)["line"]));
        return 0;
    }

    private static string Quote(string text) => JsonText.Quote(text);
}
