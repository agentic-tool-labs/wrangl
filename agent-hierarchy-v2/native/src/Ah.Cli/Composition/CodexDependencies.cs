using Ah.Abstractions;
using Ah.Codex;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Roster;

namespace Ah.Cli.Composition;

/// <summary>
/// What the Codex app-server adapter takes from the daemon, over the mailbox: the live registrations' team files, the
/// registration, heartbeat, pending and ack calls, the activity records of the member's row, and the refusal note.
/// </summary>
internal sealed class CodexDependencies(ServiceGraph graph, Action<string> log, Func<IReadOnlyList<string>, IReadOnlyList<string>>? admitDirs = null) : ICodexDependencies
{
    private const string AdapterName = "adapter";

    private IMailboxHooks? _hooks;

    public DateTimeOffset UtcNow => graph.Io.Clock.UtcNow;

    public string AhHome => graph.Environment.AhHome;

    public IReadOnlyList<CodexTeam> Teams()
    {
        var seen = new Dictionary<string, CodexTeam>(StringComparer.Ordinal);
        foreach (var registration in graph.Mailbox.LiveRegistrations())
        {
            foreach (var path in MeshModule.RegistrationTeamFiles(registration))
            {
                if (seen.ContainsKey(path)
                    || (admitDirs is { } admit && admit([Ah.Roster.RosterModule.TeamFileHome(path) ?? Path.GetDirectoryName(path) ?? path]).Count == 0))
                {
                    continue;
                }

                var data = graph.Roster.ReadTeamFileAt(path);
                if (data.IsNull || !data.IsTruthy)
                {
                    continue;
                }

                var name = Path.GetFileName(path);
                seen[path] = new CodexTeam(path, name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5] : name, data);
            }
        }

        return [.. seen.Values];
    }

    public bool PidAlive(int pid) => graph.Hierarchy.Core.PidAlive(pid);

    public bool SocketExists(string path) => graph.Io.Files.Exists(path);

    public string BoxFor(string address, CodexMember member)
    {
        var context = new RequestContext(string.Empty, HierDirOf(member), string.Empty, string.Empty);
        var resolved = graph.Mailbox.ResolveAddress(address, context);
        if (resolved.Failure is { } failure)
        {
            throw new InvalidOperationException(failure.Message);
        }

        return resolved.Value!.Dir;
    }

    public CodexModes ModesFor(CodexMember member)
    {
        var row = member.Row;
        IReadOnlyList<string>? args = null;
        if (row.Member("autoMode") is { IsTruthy: true } mode
            && RosterModule.KindAutoModeArgs.TryGetValue(JsConvert.ToJsString(graph.Config.ResolveKind(row)), out var table))
        {
            var key = JsConvert.ToJsString(mode);
            foreach (var (name, mapped) in table)
            {
                if (name == key)
                {
                    args = [.. mapped];
                    break;
                }
            }
        }

        return CodexModule.ModesOfRow(row, args, AhHome, graph.Io.Files);
    }

    public void Register(CodexRegistration registration)
    {
        var boxes = registration.Boxes.Select(b => new BoxRef(b.Address, b.Dir)).ToList();
        var written = graph.Mailbox.WriteRegistration(
            new RegistrationWrite(registration.SessionId, boxes, registration.TeamFile, registration.Harness, registration.Pid, AhVersion: BuildInfo.Version, HerdrFlavour: registration.HerdrFlavour));
        if (!written)
        {
            throw new InvalidOperationException($"registration of {registration.SessionId} was refused");
        }
    }

    public void Unregister(string sessionId) => graph.Mailbox.RemoveRegistration(sessionId);

    public void Heartbeat(string box, CodexHeartbeat heartbeat) =>
        graph.Mailbox.WriteHeartbeat(box, heartbeat.Adapter, heartbeat.SessionId, heartbeat.PollMs);

    public IReadOnlyList<CodexPendingEntry> Pending(string box)
    {
        var rows = new List<CodexPendingEntry>();
        foreach (var entry in graph.Mailbox.Inbox(box).Entries)
        {
            var id = entry.Member("id") is { IsText: true } t ? t.AsString() : null;
            if (id is not null)
            {
                rows.Add(new CodexPendingEntry(id, entry));
            }
        }

        return rows;
    }

    public string Render(string address, IReadOnlyList<CodexPendingEntry> entries)
    {
        var group = new JsonObject();
        group["address"] = address;
        var list = new JsonArray();
        foreach (var e in entries)
        {
            list.Add(graph.Mailbox.MarkRelayVoid(e.Entry));
        }

        group["entries"] = list;
        var groups = new JsonArray();
        groups.Add(group);
        return Ah.Mesh.MeshModule.CreateMailCore(graph.Io.Clock).BuildPrompt(groups);
    }

    public void Ack(string box, IReadOnlyList<string> ids, string sessionId) =>
        graph.Mailbox.AckIds(box, ids, AdapterName, sessionId);

    public void Activity(CodexMember member, string state, string? blockedBy)
    {
        var fields = new JsonObject();
        fields["activity"] = state;
        fields["blocked_by"] = blockedBy is null ? JsonValue.Null : blockedBy;
        if (state == "failed")
        {
            var extra = new JsonObject();
            extra["error"] = "unknown";
            extra["error_details"] = JsonValue.Null;
            extra["error_code"] = JsonValue.Null;
            extra["source"] = "appserver";
            extra["failed_at"] = graph.Io.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            fields["extra"] = extra;
        }

        graph.Hierarchy.Status.RecordActivityRouted(HierDirOf(member), $"pane-{member.Name}", fields);
    }

    public void Refused(CodexMember member, string note)
    {
        var hierarchyDir = HierDirOf(member);
        var text = $"{note}; launched without the app-server adapter";
        try
        {
            graph.Hierarchy.StateCalls.UpdateTeamFile(
                member.TeamFile,
                hierarchyDir,
                doc =>
                {
                    if (!doc.IsMap || doc.Member("members") is not { IsArray: true } members)
                    {
                        return null;
                    }

                    var next = new JsonArray();
                    var changed = false;
                    foreach (var m in members.AsArray())
                    {
                        if (m.IsMap && m.Member("name") is { IsText: true } n && n.AsString() == member.Name
                            && !(m.Member("adapter_note") is { IsText: true } have && have.AsString() == text))
                        {
                            var row = m.AsObject().Clone();
                            row["adapter_note"] = text;
                            next.Add(row);
                            changed = true;
                        }
                        else
                        {
                            next.Add(m);
                        }
                    }

                    if (!changed)
                    {
                        return null;
                    }

                    var team = doc.AsObject().Clone();
                    team["members"] = next;
                    return Ah.Hierarchy.StateCalls.TeamFileOutcome.Write(team);
                });
        }
        catch (Exception e)
        {
            log($"codex-appserver {member.Key}: row note not written ({e.Message})");
        }

        var data = new JsonObject();
        data["member"] = member.Address;
        MailOrchestrator(member, "event", "adapter-refused", $"{member.Name}: {note}; its mail stays on the paste rung", data);
    }

    public void RequestRaised(CodexMember member, CodexPending pending)
    {
        var data = new JsonObject();
        data["member"] = member.Address;
        data["request_id"] = pending.RequestId;
        data["kind"] = pending.Kind;
        data["blocking"] = pending.Blocking;
        data["secret"] = pending.Secret;
        data["verbatim"] = pending.Verbatim is null ? JsonValue.Null : pending.Verbatim;
        var choices = new JsonArray();
        foreach (var c in pending.Choices)
        {
            choices.Add(c);
        }

        data["choices"] = choices;
        MailOrchestrator(member, "event", "member-request", RequestText(member, pending, WorkTreeOf(member)), data);
    }

    private string WorkTreeOf(CodexMember member) => CodexAnswerRoute.WorkRootOf(graph, member) ?? "its work tree (not recorded: ask the user)";

    internal static string RequestText(CodexMember member, CodexPending pending, string workTree)
    {
        var who = $"{member.Name}@{member.Team}";
        if (pending.Secret)
        {
            return $"{who} asks a secret question (request {pending.RequestId}). The user answers it in the member's pane.\n"
                + "Do not ask for the secret in chat or mail.";
        }

        if (pending.Kind == "question")
        {
            return $"{who} asks (request {pending.RequestId}):\n{QuestionLines(pending.Verbatim)}\n"
                + "Answer it yourself if the decision is yours under your brief; otherwise ask the user with\n"
                + "AskUserQuestion, one question per item above.\n"
                + $"Answer: ah roster answer {member.Name} --request {pending.RequestId} --answer <qid>=<text> [--answer …] --cwd <cwd>";
        }

        return $"{who} is waiting on a Codex {pending.Kind} (request {pending.RequestId}).\n"
            + $"{pending.Verbatim}\n"
            + $"Choices: {string.Join(", ", pending.Choices)}\n"
            + "You may answer this yourself only with decline or cancel, or with accept (one time) when the action is\n"
            + $"inside {workTree} and is not one the user reserves (destructive, irreversible, outward-facing,\n"
            + "credentials, or outside the work tree). Otherwise ask the user with AskUserQuestion, showing the\n"
            + "request above verbatim. Never choose a session-wide or policy-changing option yourself.\n"
            + $"Answer: ah roster answer {member.Name} --request {pending.RequestId} --choice <choice> --cwd <cwd>\n"
            + "If ah replies needs-user, ask the user and answer with their choice.";
    }

    private static string QuestionLines(string? verbatim)
    {
        if (verbatim is null || JsonText.Parse(verbatim).Member("questions") is not { IsArray: true } list)
        {
            return verbatim ?? string.Empty;
        }

        return string.Join(
            "\n",
            list.AsArray().Select(q =>
            {
                var options = q.Member("options") is { IsArray: true } o
                    ? string.Join(", ", o.AsArray().Select(x => $"{Text(x, "label")} ({Text(x, "description")})"))
                    : "none";
                var other = q.Member("isOther") is { IsBool: true } flag && flag.AsBool() ? "yes" : "no";
                return $"[{Text(q, "id")}] {Text(q, "header")}: {Text(q, "question")} — options: {options}; other allowed: {other}";
            }));
    }

    private static string Text(JsonValue v, string key) => v.Member(key) is { IsText: true } t ? t.AsString() : string.Empty;

    public void RequestDropped(CodexMember member, CodexPending pending, string why)
    {
        var data = new JsonObject();
        data["member"] = member.Address;
        data["request_id"] = pending.RequestId;
        data["kind"] = pending.Kind;
        data["why"] = why;
        MailOrchestrator(member, "event", "member-request-dropped", $"{member.Name}'s {pending.Kind} request {pending.RequestId} was dropped unanswered: {why}.", data);
    }

    public void RequestUnsupported(CodexMember member, string method)
    {
        log($"codex-appserver {member.Key}: refused an unsupported server request ({method})");
        var data = new JsonObject();
        data["member"] = member.Address;
        data["method"] = method;
        MailOrchestrator(member, "event", "member-request-unsupported", $"{member.Name}'s app-server sent {method}, which ah does not route; it was refused with method-not-found.", data);
    }

    public void PermissionsDeclined(CodexMember member, string details) =>
        MailOrchestrator(
            member,
            "note",
            "permissions-declined",
            $"{member.Name} requested more permissions; ah declined it automatically (not yet supported). Details: {details}",
            new JsonObject { ["member"] = member.Address });

    public void TurnEnded(CodexMember member, string? lastMessage)
    {
        _hooks ??= MeshModule.CreateMailboxHooks(graph.Mailbox, MeshModule.CreateRelayObligations(graph.Mailbox, graph.Hierarchy), graph.Hierarchy, graph.Io.Clock);
        var owed = _hooks.OwedBriefs(CodexAdapterSession(member));
        if (owed.Count == 0)
        {
            return;
        }

        var text = $"{member.Name} ended a turn without reporting; its last message:\n{lastMessage ?? "(no final message captured)"}\n(owes a report on {owed[0].Ref})";
        MailOrchestrator(member, "note", string.Empty, text, new JsonObject { ["member"] = member.Address, ["ref"] = owed[0].Ref });
    }

    private static string CodexAdapterSession(CodexMember member) => $"codex-appserver.{member.Team}.{member.Name}";

    /// <summary>
    /// Whom a member's notices go to: the Orchestrator of its valid, live lead's team, otherwise its own team's Orchestrator, with
    /// the team file that Orchestrator is registered under.
    /// </summary>
    internal static (string Address, string TeamFile) NoticeTarget(CodexMember member, Func<string, JsonValue> team, Func<string, string> teamFileOf, Func<JsonValue, JsonValue?, bool> identityLive)
    {
        var home = (HierarchyModule.OrchestratorAddress(member.Team), member.TeamFile);
        var address = Ah.Hierarchy.HierarchyModule.EventAddress(member.Team, member.Row, team, identityLive);
        if (address == home.Item1)
        {
            return home;
        }

        var leadTeam = address[(address.IndexOf('@', StringComparison.Ordinal) + 1)..];
        return (address, teamFileOf(leadTeam));
    }

    private void MailOrchestrator(CodexMember member, string kind, string eventName, string text, JsonObject data)
    {
        var hierarchyDir = HierDirOf(member);
        try
        {
            var (orchestrator, orchestratorFile) = NoticeTarget(
                member,
                team => graph.Roster.ReadTeamRecord(hierarchyDir, team),
                team => graph.Roster.TeamFileState(hierarchyDir, team).Get("path") is { IsText: true } path ? path.AsString() : member.TeamFile,
                graph.Hierarchy.Core.IdentityLive);
            string? sessionId = null;
            foreach (var r in graph.Mailbox.LiveRegistrations())
            {
                if (MeshModule.RegistrationTeamFileOf(r, orchestrator) == orchestratorFile
                    && graph.Mailbox.RegistrationBoxes(r).Any(b => b.Address == orchestrator))
                {
                    sessionId = r.Get("session_id") is { IsText: true } sid ? sid.AsString() : null;
                    break;
                }
            }

            var request = new JsonObject();
            request["to"] = orchestrator;
            request["from"] = "ahd";
            request["session_id"] = JsonValue.Null;
            request["cwd"] = Path.GetDirectoryName(Path.GetDirectoryName(hierarchyDir)) ?? hierarchyDir;
            request["hierarchy_dir"] = hierarchyDir;
            request["kind"] = kind;
            if (kind == "event")
            {
                request["event"] = eventName;
            }

            request["text"] = text;
            request["data"] = data;
            if (sessionId is not null)
            {
                request["to_session"] = sessionId;
            }

            _ = graph.Mailbox.Send(request);
        }
        catch (Exception e)
        {
            log($"codex-appserver {member.Key}: {eventName} not mailed ({e.Message})");
        }
    }

    public void Log(string message) => log(message);

    private static string HierDirOf(CodexMember member) =>
        RosterModule.TeamFileHome(member.TeamFile) is { Length: > 0 } home ? home : Path.GetDirectoryName(member.TeamFile) ?? string.Empty;
}
