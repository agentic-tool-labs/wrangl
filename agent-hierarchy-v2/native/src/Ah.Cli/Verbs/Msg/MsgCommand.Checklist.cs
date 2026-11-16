using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    private string ChecklistPool => HierarchyModule.ProgressPoolOf(Config.MainHierarchyDir(_cwd), HierarchyDir);

    private (string Pool, string Team) ChecklistScope(JsonValue? fm) => (ChecklistPool, HierarchyModule.ProgressTeamOf(FrontText(fm?.Member("team"))));

    /// <summary>
    /// Resolves <c>--item</c> before anything is written: only a request from the Orchestrator may carry one, and it must name one open item of this
    /// team's checklist. Returns the canonical <c>stream/id</c>, or null when no item was given.
    /// </summary>
    private string? ChecklistItemForRequest(string type)
    {
        if (_opts.Text("item") is not { } item)
        {
            return null;
        }

        if (type != "request")
        {
            throw _out.Fail("--item applies only to --type request");
        }

        if (_opts.Text("from") is not ("orchestrator" or "sub-orchestrator" or "deputy-chief"))
        {
            throw _out.Fail("--item is for a request from the Orchestrator or a lane lead; nothing written");
        }

        // a backlog id (b<N>) names a backlog item, not a checklist item: the request gets linked to it as a ref
        if (BacklogVerb.IsBacklogId(item))
        {
            if (!BacklogVerb.Has(_graph, _env.AhHome, _cwd, item))
            {
                _stderr.Write($"ah msg: no backlog item {item}\n");
                throw new MsgExit(1);
            }

            return item;
        }

        var team = HierarchyModule.ProgressTeamOf(_teamArg);
        var problem = HierarchyModule.ChecklistDispatchTarget(Hier.Progress, ChecklistPool, team, item, out var entry);
        if (problem == $"no item {item}" && WorkVerb.Resolve(_graph, _env.AhHome, ChecklistPool, item, team).Row is { } work)
        {
            if (work.IsBacklog)
            {
                return work.Item;
            }

            if (work.Team != team)
            {
                throw _out.Fail($"--item {item}: that item belongs to team {work.Team}; nothing written");
            }

            problem = HierarchyModule.ChecklistDispatchTarget(Hier.Progress, ChecklistPool, team, work.Stream + "/" + work.Item, out entry);
        }

        return problem is null && entry is not null
            ? HierarchyModule.ChecklistKey(entry)
            : throw _out.Fail($"--item: {problem ?? "no item " + item}; nothing written");
    }

    /// <summary>The note for a checklist update that failed: it names the item when the request is linked to one, and says only that the checklist was not updated when it is not.</summary>
    internal static string ChecklistNoteText(string requestId, string? item, string problem) =>
        string.IsNullOrEmpty(item)
            ? $"ah msg: note — request {requestId}: the checklist was not updated: {problem}"
            : $"ah msg: note — request {requestId}: checklist item {item} was not updated: {problem}";

    private void ChecklistNote(string requestId, string? item, string problem) => _out.Note(ChecklistNoteText(requestId, item, problem));

    /// <summary>Where the dispatched work's branch stands: the tip of <c>ah/&lt;slug&gt;</c> when it exists, else HEAD of the repository at the cwd; null outside a repository.</summary>
    private string? DispatchBase(string? slug)
    {
        string? ReadOrNull(string path)
        {
            try
            {
                return _graph.Io.Files.Stat(path) is { Kind: Ah.Abstractions.FileKind.File } ? _graph.Io.Files.ReadText(path) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (HierarchyModule.CheckoutOfCwd(_cwd, _graph.Io.Files.Exists) is { } root && HierarchyModule.DispatchBaseOfWorkTree(root, slug, ReadOrNull) is { } read)
        {
            return read;
        }

        var git = ConfigModule.CreateConventions(_graph.Runner, _graph.Io.Files, () => _env.HomeDirectory);
        var scope = new GitScope(_cwd);
        string? Sha(string reference) => git.TryGit(scope, ["rev-parse", "--verify", "-q", reference + "^{commit}"])?.Trim() is { Length: > 0 } sha ? sha : null;
        return (slug is { Length: > 0 } ? Sha("refs/heads/ah/" + slug) : null) ?? Sha("HEAD");
    }

    /// <summary>A request sent with <c>--item</c>: the item is owned by the addressee, dispatched, and linked to the request. False when the checklist was not updated.</summary>
    private bool ChecklistDispatched(string requestId, string item)
    {
        if (BacklogVerb.IsBacklogId(item))
        {
            if (BacklogVerb.Link(_graph, _env.AhHome, _cwd, item, requestId) is { } failed)
            {
                _out.Note($"ah msg: note — request {requestId}: backlog item {item} was not linked: {failed}");
                return false;
            }

            return true;
        }

        try
        {
            var (_, _, fm) = FindExchange(requestId, string.Empty);
            var (pool, team) = ChecklistScope(fm);
            var owner = FrontText(fm?.Member("to_name")) ?? FrontText(fm?.Member("to")) ?? "unknown";
            var slug = FrontText(fm?.Member("slug"));
            if (HierarchyModule.ChecklistOnDispatch(
                Hier.Progress, pool, team, item, owner, requestId, _opts.Text("from") + "@" + team, SessionId, DispatchBase(slug), FrontText(fm?.Member("to")), slug, FrontText(fm?.Member("sha")), ProgressNow()) is { } problem)
            {
                ChecklistNote(requestId, item, problem);
                return false;
            }

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(requestId, item, e.Message);
            return false;
        }
    }

    /// <summary>
    /// A request from the Orchestrator or a sub-orchestrator sent without <c>--item</c> (and not the run's anchor): a <c>_run</c> item titled from its
    /// <c>--title</c> is added to the sender's team checklist and the request is dispatched against it, the same as <c>--item</c>. Returns the one
    /// stderr line, printed after the normal output: it names the item and how to link it next time, or, when the item could not be added, says the
    /// request is not on the checklist.
    /// </summary>
    private string ChecklistAutoLinked(string requestId)
    {
        const string howTo = "`ah roster checklist add \"<what>\" --ref <request id>` (new item) or `ah roster checklist set <item> --ref <request id>` (existing item), or `ah msg new --item <id>` at dispatch";
        string? key = null;
        string? problem;
        try
        {
            var (_, _, fm) = FindExchange(requestId, string.Empty);
            var (pool, team) = ChecklistScope(fm);
            var title = _opts.Text("title") ?? requestId;
            const string stream = "_run"; // the team's loose-end stream, as ChecklistVerb names it
            var item = HierarchyModule.ChecklistSlug(HierarchyModule.ChecklistAutoPrefix + title,HierarchyModule.ChecklistEntries(Hier.Progress, pool, team).Select(static e => e.Item.Item).ToHashSet(StringComparer.Ordinal));
            var auto = new PlanCause(HierarchyModule.ChecklistCauseVerb, "auto-add", ProgressNow());
            var added = Hier.Progress.RecordChecklist(pool, team, stream, HierarchyModule.OrchestratorAddress(team), [.. new[] { new PlanInput(item, "add", title), HierarchyModule.ChecklistAutoMark(item), new PlanInput(item, "next") }.Select(e => e with { Cause = e.Cause ?? auto })], SessionId);
            problem = added.Off ? $"progress is off for {team}" : added.Refused;
            if (problem is null)
            {
                key = stream + "/" + item;
                problem = ChecklistDispatched(requestId, key) ? null : $"item {key} was added but not linked";
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            problem = e.Message;
        }

        return problem is null
            ? $"note: {requestId} was not on the checklist; added item {key} and linked it. Next time link it yourself: {howTo}.\n"
            : $"note: {requestId} is not on the checklist ({problem}). Link it: {howTo}.\n";
    }

    /// <summary>A brief from the Orchestrator to a member answers what that member is owed: its items waiting on the Orchestrator go back to dispatched.</summary>
    private void ChecklistBriefed(string requestId)
    {
        try
        {
            var (_, _, fm) = FindExchange(requestId, string.Empty);
            var (pool, team) = ChecklistScope(fm);
            if ((FrontText(fm?.Member("to_name")) ?? FrontText(fm?.Member("to"))) is { Length: > 0 } member
                && HierarchyModule.ChecklistOnBrief(Hier.Progress, pool, team, member, HierarchyModule.OrchestratorAddress(team), SessionId, ProgressNow()) is { } problem)
            {
                ChecklistNote(requestId, null, problem);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(requestId, null, e.Message);
        }
    }

    /// <summary>A response landed or was filled: the item linked to the request waits on the Orchestrator, with the kind the response's first line carries.</summary>
    private void ChecklistReplied(string requestId, string? body)
    {
        JsonValue? fm;
        try
        {
            (_, _, fm) = FindExchange(requestId, string.Empty);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }

        try
        {
            var (pool, team) = ChecklistScope(fm);
            var member = FrontText(fm?.Member("to_name")) ?? FrontText(fm?.Member("to")) ?? "member";
            var item = FrontText(fm?.Member("item"));
            var role = FrontText(fm?.Member("to"));
            var reported = HierarchyModule.ChecklistReplyIsDone(body) && HierarchyModule.ChecklistIsLinked(Hier.Progress, pool, team, requestId, item) ? ReportedWork(requestId) : null;
            if (HierarchyModule.ChecklistOnMemberReply(Hier.Progress, pool, team, requestId, item, member, role, body, _graph.Io.Clock.UtcNow, SessionId, RequestSlug, FrontText(fm?.Member("sha")), reported, RequestParent, RequestWorker) is { } problem)
            {
                ChecklistNote(requestId, item, problem);
            }

            // an architect's reply names the specs it wrote: they attach with nothing to remember, and this never fails the reply
            if (role == "architect"
                && (item ?? (HierarchyModule.ChecklistItemOfRequest(HierarchyModule.ChecklistEntries(Hier.Progress, pool, team), requestId) is { } linked ? HierarchyModule.ChecklistKey(linked) : null)) is { } about)
            {
                WorkVerb.AutoAttach(_graph, _env.HomeDirectory, _env.AhHome, _cwd, pool, team, about, body);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(requestId, null, e.Message);
        }
    }

    /// <summary>Where a DONE report's work stands: its <c>--sha</c> when that names a commit, else HEAD of the report's cwd when that tree is clean. Null for a dirty tree, with one note; a failed status counts as dirty.</summary>
    private string? ReportedWork(string requestId)
    {
        var git = ConfigModule.CreateConventions(_graph.Runner, _graph.Io.Files, () => _env.HomeDirectory);
        var scope = new GitScope(_cwd);
        if (_opts.Text("sha") is { Length: > 0 } sha && git.TryGit(scope, ["rev-parse", "--verify", "-q", sha + "^{commit}"])?.Trim() is { Length: > 0 } given)
        {
            return given;
        }

        if (HierarchyModule.ChecklistCleanHead(args => git.TryGit(scope, args)) is { } head)
        {
            return head;
        }

        // outside a repository there is nothing to name
        if (git.TryGit(scope, ["rev-parse", "--verify", "-q", "HEAD^{commit}"]) is null)
        {
            return null;
        }

        _out.Note($"ah msg: note — request {requestId}: no sha recorded, this tree has uncommitted changes; pass --sha <commit> to name the work");
        return null;
    }

    /// <summary>
    /// The lead acked the report on <paramref name="requestId"/>: its acceptance moves the linked item. <c>--hold</c> acknowledges with no move. A report
    /// from a request the lead did not send, or one not filed yet, moves nothing; a report with no item says so in one line and is not refused.
    /// </summary>
    private void ChecklistAcked(string requestId)
    {
        if (_opts.Has("hold"))
        {
            return;
        }

        try
        {
            var (exchange, _, fm) = FindExchange(requestId, string.Empty);
            if (FrontText(fm?.Member("from")) is not ("orchestrator" or "sub-orchestrator" or "deputy-chief")
                || exchange.Member("response")?.Member("path") is not { IsText: true } path
                || !Hier.Messages.ResponseFiled(Hier.Messages.PoolDirs(HierarchyDir, ExchangeTeams()), requestId))
            {
                return;
            }

            var (pool, team) = ChecklistScope(fm);
            var item = FrontText(fm?.Member("item")) is { } named && !BacklogVerb.IsBacklogId(named) ? named : null;
            var read = Hier.Core.ReadMsgFile(path.AsString());
            var text = read.Member("text") is { IsText: true } stored ? stored.AsString() : string.Empty;
            var body = text.StartsWith("---", StringComparison.Ordinal) && text.IndexOf("\n---", 3, StringComparison.Ordinal) is var end and > 0 ? text[(end + 4)..] : text;
            var at = FrontText(read.Member("fm")?.Member("created")) is { } created && DateTimeOffset.TryParse(created, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var filed)
                ? filed
                : _graph.Io.Clock.UtcNow;
            if (HierarchyModule.ChecklistOnAck(Hier.Progress, pool, team, requestId, item, body, at, HierarchyModule.OrchestratorAddress(team), SessionId, RequestSlug, RequestParent, RequestWorker, out var noItem) is { } problem)
            {
                ChecklistNote(requestId, item, problem);
            }
            else if (noItem)
            {
                _out.Note($"ah msg: note — ack {requestId}: moved no checklist item.");
            }

            ChecklistVerdictActed(requestId, fm);
        }
        catch (MailFailure e) when (e.Code == "unknown")
        {
            // an id that names no exchange of this repository has no item to move
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(requestId, null, e.Message);
        }
    }

    /// <summary>
    /// The lead acted on a reviewer's verdict: it acked <paramref name="requestId"/>, or sent a request with <c>--parent</c> naming it. The auto item the
    /// review request made stops waiting on the lead (b333). A request the lead did not send to a reviewer moves nothing; this never fails the verb.
    /// </summary>
    private void ChecklistVerdictActed(string requestId, JsonValue? fm = null)
    {
        try
        {
            fm ??= FindExchange(requestId, string.Empty).Fm;
            if (FrontText(fm?.Member("from")) is not ("orchestrator" or "sub-orchestrator" or "deputy-chief") || FrontText(fm?.Member("to")) != "reviewer")
            {
                return;
            }

            var (pool, team) = ChecklistScope(fm);
            if (HierarchyModule.ChecklistOnVerdictActed(Hier.Progress, pool, team, requestId, HierarchyModule.OrchestratorAddress(team), SessionId, ProgressNow()) is { } problem)
            {
                ChecklistNote(requestId, null, problem);
            }
        }
        catch (MailFailure e) when (e.Code == "unknown")
        {
            // a parent that names no exchange of this repository has no item to close
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(requestId, null, e.Message);
        }
    }

    /// <summary>The time a checklist event happened: now, in the plan log's UTC form.</summary>
    private string ProgressNow() => HierarchyModule.ChecklistEventTime(_graph.Io.Clock.UtcNow);

    /// <summary>The slug of request <paramref name="requestId"/>, or null when it cannot be found.</summary>
    private string? RequestSlug(string requestId) => RequestField(requestId, "slug");

    /// <summary>The parent of request <paramref name="requestId"/>, or null when it has none or cannot be found.</summary>
    private string? RequestParent(string requestId) => RequestField(requestId, "parent");

    private string? RequestWorker(string requestId) => HierarchyModule.ChecklistWorkerOf(RequestField(requestId, "to"), RequestField(requestId, "to_name"));

    /// <summary>A reviewer linked one member on its own request: the rework moves that item's holder (spec §6.2). A spec defect goes to the Architect and moves nothing. Never fails the link.</summary>
    private void ChecklistLinked()
    {
        if (_cmd != "link-request" || _opts.Text("task") is not { } task || _opts.Text("to") is not { } to || to.Contains(',', StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            var (_, _, fm) = FindExchange(task, string.Empty);
            if (FrontText(fm?.Member("to")) != "reviewer" || FrontText(fm?.Member("to_name")) is not { } reviewer)
            {
                return;
            }

            var (pool, team) = ChecklistScope(fm);
            var worker = to.Split('@')[0];
            var isArchitect = _graph.Roster.ReadTeamRecord(pool, team).Member("members") is { IsArray: true } members
                && members.AsArray().Any(m => FrontText(m.Member("name")) == worker && FrontText(m.Member("role")) == "architect");
            if (!isArchitect && HierarchyModule.ChecklistOnRework(Hier.Progress, pool, team, task, FrontText(fm?.Member("item")), reviewer, worker, reviewer + "@" + team, SessionId, ProgressNow()) is { } problem)
            {
                ChecklistNote(task, null, problem);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(task, null, e.Message);
        }
    }

    /// <summary>Whether request <paramref name="requestId"/> is an open exchange whose request file was not withdrawn; false when it cannot be found.</summary>
    private bool RequestOpen(string requestId)
    {
        try
        {
            var (exchange, path, _) = FindExchange(requestId, string.Empty);
            return exchange.Member("open")?.IsTruthy == true && !_graph.Mailbox.WithdrawnRequestFiles().Contains(Path.GetFileName(path.AsString()));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    private string? RequestField(string requestId, string key)
    {
        try
        {
            var (_, _, fm) = FindExchange(requestId, string.Empty);
            return FrontText(fm?.Member(key));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>A mailed brief whose ref is <paramref name="reference"/> (a request id or its file path) was withdrawn: the item linked to the request is dropped.</summary>
    private void ChecklistWithdrawnRef(string reference)
    {
        var name = Path.GetFileName(reference);
        var cut = name.IndexOf("--", StringComparison.Ordinal);
        var requestId = cut > 0 ? name[..cut] : Path.GetFileNameWithoutExtension(name);
        JsonValue? fm;
        try
        {
            (_, _, fm) = FindExchange(requestId, string.Empty);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return;
        }

        ChecklistWithdrawn(requestId, fm);
    }

    /// <summary>A parked decision that names an item puts that item on the user's list, with the question as its note; its answer already takes it off. The line is the record, so a failed row only notes.</summary>
    private void ParkedItemWaitsOnUser(JsonValue input, JsonObject res)
    {
        if (input.Member("kind") is not { IsText: true } kind || kind.AsString() != "parked" || FrontText(input.Member("item")) is not { } item)
        {
            return;
        }

        var id = JsConvert.ToJsString(res.Get("id") ?? JsonValue.Null);
        try
        {
            var team = HierarchyModule.ProgressTeamOf(_teamArg);
            var by = HierarchyModule.OrchestratorAddress(team);
            var ev = new ChecklistEvent(HierarchyModule.ChecklistEventAsked, ProgressNow(), id, by) { ItemId = item, Text = FrontText(input.Member("question")) };
            if (HierarchyModule.ChecklistOnEvent(Hier.Progress, ChecklistPool, team, ev, by, SessionId) is { } problem)
            {
                ChecklistNote(id, item, problem);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(id, item, e.Message);
        }
    }

    /// <summary>A brief was withdrawn: the transition table drops the item linked to it.</summary>
    private void ChecklistWithdrawn(string requestId, JsonValue? fm)
    {
        try
        {
            var (pool, team) = ChecklistScope(fm);
            var item = FrontText(fm?.Member("item"));
            // a withdrawn brief no longer holds its backlog row, so the row's goal rule leaves the member (rules-of-engagement §9)
            if (item is not null && BacklogVerb.IsBacklogId(item))
            {
                if (BacklogVerb.Link(_graph, _env.AhHome, _cwd, item, requestId, remove: true) is { } failed)
                {
                    ChecklistNote(requestId, item, $"backlog item {item} was not unlinked: {failed}");
                }
            }

            var by = HierarchyModule.OrchestratorAddress(team);
            if (HierarchyModule.ChecklistOnEvent(Hier.Progress, pool, team, new ChecklistEvent(HierarchyModule.ChecklistEventWithdraw, ProgressNow(), requestId, by) { ItemId = item, RequestOpen = RequestOpen }, by, SessionId) is { } problem)
            {
                ChecklistNote(requestId, item, problem);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(requestId, null, e.Message);
        }
    }

    /// <summary>The Orchestrator closed a request: a <c>done</c> close marks the linked item done with <paramref name="note"/>; superseded or abandoned leaves it waiting on the Orchestrator, with the reason in its note.</summary>
    private void ChecklistClosed(string requestId, JsonValue? fm, string reason, string? note)
    {
        try
        {
            var (pool, team) = ChecklistScope(fm);
            var by = HierarchyModule.OrchestratorAddress(team);
            var item = FrontText(fm?.Member("item"));
            var problem = reason == "done"
                ? HierarchyModule.ChecklistOnDone(Hier.Progress, pool, team, requestId, item, note ?? string.Empty, by, SessionId, ProgressNow())
                : HierarchyModule.ChecklistOnClose(Hier.Progress, pool, team, requestId, item, by, SessionId, reason, ProgressNow());
            if (problem is not null)
            {
                ChecklistNote(requestId, FrontText(fm?.Member("item")), problem);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            ChecklistNote(requestId, null, e.Message);
        }
    }
}
