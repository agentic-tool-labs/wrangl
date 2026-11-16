using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Verbs.Msg;

internal sealed partial class MsgCommand
{
    /// <summary>The longest reason the note to the addressee carries whole; the ladder row always keeps it verbatim.</summary>
    private const int WithdrawNoteReasonChars = 700;

    /// <summary>
    /// <c>ah msg withdraw &lt;entry-id&gt; --reason &lt;text&gt; [--to &lt;name@team|name&gt;] [--from &lt;address&gt;]</c>: the sender ends a mailed
    /// brief. The addressee gets a note and no longer owes it; with <c>--to</c> the same brief is mailed to another member of the same role.
    /// Everything that can refuse is checked before the terminal row is written.
    /// </summary>
    private int MailWithdraw()
    {
        if (_opts.Positional.Count != 1)
        {
            throw new MailFailure("usage", "withdraw needs exactly one entry id");
        }

        var id = _opts.Positional[0];
        var reason = Str("reason");
        if (reason is null || reason.Trim().Length == 0)
        {
            throw new MailFailure("usage", "withdraw needs --reason <text>");
        }

        var mailbox = _graph.Mailbox;
        var held = HolderOf(id);
        if (held is null)
        {
            return ExchangeWithdraw(id, reason);
        }

        var explicitFrom = Str("from");
        var from = Unwrap(mailbox.ChooseFrom(WriteAddresses(), mailbox.SplitAddress(held)?.Team, explicitFrom));
        var plan = Unwrap(mailbox.WithdrawPlan(id, from));
        var ctx = _graph.Callers.RequestContext(Where);
        ResolvedAddress? target = null;
        if (Str("to") is { Length: > 0 } to)
        {
            target = Unwrap(mailbox.ResolveAddress(to, ctx));
            var role = target.Member?.Member("role") is { IsText: true } r ? r.AsString() : string.Empty;
            var wanted = plan.Get("role") is { IsText: true } w ? w.AsString() : string.Empty;
            if (role != wanted)
            {
                throw new MailFailure("role_mismatch", $"{target.Address} holds the role {(role.Length > 0 ? role : "(none)")}; the request of {id} is for {(wanted.Length > 0 ? wanted : "(unknown)")}. A different role needs a new brief");
            }
        }

        var newId = target is null ? null : mailbox.NewId();
        var result = Unwrap(mailbox.Withdraw(plan.Get("dir")!.Value.AsString(), id, from, reason, newId));
        result["progress"] = plan.Get("progress") ?? JsonValue.Null;
        result["acked_by"] = plan.Get("acked_by") ?? JsonValue.Null;
        result["acked_at"] = plan.Get("acked_at") ?? JsonValue.Null;
        result["response"] = plan.Get("response") ?? JsonValue.Null;

        var addressee = plan.Get("to")!.Value.AsString();
        var lines = new List<string> { $"withdrew {id} from {addressee}: {reason}" };
        var progress = plan.Get("progress")!.Value.AsString();
        lines.Add($"progress: {progress}");
        if (progress == "response_drafted")
        {
            lines.Add($"{addressee} has already drafted a response ({plan.Get("response")!.Value.AsString()}); ask them before discarding that work");
        }

        var failed = 0;
        try
        {
            var shown = reason.Length > WithdrawNoteReasonChars ? reason[..WithdrawNoteReasonChars] + $"… (the full reason: ah msg status {id})" : reason;
            var noteText = $"entry {id} was withdrawn by {from}: {shown}" + (target is null ? string.Empty : $". It is re-addressed to {target.Address}.");
            var noted = SendEntry(WithdrawRequest(from, ctx, addressee, mailbox.NewId(), new JsonObject { ["text"] = noteText, ["reply_to"] = id }));
            RecordRelayObligation(noted, plan.Get("dir")!.Value.AsString());
            result["note"] = noted.Get("entry")?.Member("id") ?? JsonValue.Null;
            result["note_send"] = noted;
            lines.Add($"noted {addressee} ({JsConvertText(noted.Get("entry")?.Member("id"))})");
            lines.Add(mailbox.RenderSend(noted));
        }
        catch (MailFailure e)
        {
            failed = 1;
            result["note_error"] = $"{e.Code}: {e.Message}";
            lines.Add($"error: the note to {addressee} was not sent ({e.Code}: {e.Message}); the withdrawal stands");
        }

        // a brief withdrawn and not re-addressed: the transition table drops the item linked to its request
        if (target is null && plan.Get("ref") is { IsText: true } briefRef)
        {
            ChecklistWithdrawnRef(briefRef.AsString());
        }

        if (target is not null)
        {
            try
            {
                var fields = new JsonObject { ["ref"] = plan.Get("ref")!.Value.AsString() };
                if (plan.Get("eta") is { IsText: true } eta)
                {
                    fields["eta"] = eta;
                }

                var sent = SendEntry(WithdrawRequest(from, ctx, target.Address, newId!, fields));
                RecordRelayObligation(sent, target.Dir);
                var timed = RecordDispatch(plan.Get("ref")!.Value.AsString(), target.Address);
                result["dispatch_recorded"] = timed;
                if (!timed)
                {
                    lines.Add($"warning: the re-addressed brief is not being timed: no dispatch was recorded for {target.Address} (this session is not a known one), so the watch will not raise check-ins or silence for it");
                }

                result["sent"] = sent;
                lines.Add($"re-addressed {id} to {target.Address} as {newId}");
                lines.Add(mailbox.RenderSend(sent));
            }
            catch (MailFailure e)
            {
                failed = 1;
                result["readdress_error"] = $"readdress_failed: {e.Message}";
                lines.Add($"error: readdress_failed: {id} is withdrawn but the brief did not reach {target.Address} ({e.Code}: {e.Message}); send it yourself with ah msg send");
            }
        }

        return Print(result, string.Join("\n", lines), failed);
    }

    /// <summary>
    /// A withdrawal of an exchange that no mailbox holds: the request's own sender writes a response beside the request, marked
    /// <c>withdrawn: true</c> with the reason, so the exchange reads as closed without anyone pretending to be the addressee.
    /// The request is found where <c>ah msg list</c> finds it, in any pool of the teams this caller owns or is in.
    /// </summary>
    private int ExchangeWithdraw(string id, string reason)
    {
        if (Str("to") is not null)
        {
            throw new MailFailure("usage", "--to re-addresses a mailed brief; an exchange can only be withdrawn, not re-addressed");
        }

        if (Str("from") is not null)
        {
            throw new MailFailure("usage", "--from is a mailbox address; the sender of an exchange is the caller, so withdraw takes no --from for it");
        }

        var dir = HierarchyDir;
        var (exchange, requestPath, fm) = FindExchange(id, "no entry {0} in any mailbox, and ");
        var senderName = FrontText(fm?.Member("from_name"));
        var senderRole = FrontText(fm?.Member("from"));
        var sender = senderName ?? senderRole ?? throw new MailFailure("not_your_session", $"the request of {id} names no sender, so no caller is its sender");
        // A role names the same seat in every team, so a request that names only its sender's role is the caller's only if the caller holds that seat in the request's own team.
        var requestTeam = FrontText(fm?.Member("team"));
        if (senderName is null && requestTeam is null)
        {
            // no caller can prove it is such a sender, so the Orchestrator of a team whose pool holds the request may withdraw it in the sender's place
            sender = StrandedRequestOwner(id) ?? throw new MailFailure(
                "not_your_session", $"the request of {id} names its sender only by role ({senderRole}) and no team, so no caller can prove it is the sender; the Orchestrator of the team that holds it can withdraw it");
        }
        else if (!IsOwnName(sender, senderName is null ? requestTeam : null))
        {
            // a named sender that is gone (no live session holds its name and its team has no record) can never withdraw it either
            sender = (senderName is not null && SenderGone(senderName, requestTeam) ? StrandedRequestOwner(id) : null)
                ?? throw new MailFailure("not_your_session", $"only the sender of {id} ({sender}) can withdraw it; this caller is not {sender}");
        }

        if (exchange.Member("response") is { IsMap: true } existing)
        {
            throw new MailFailure("withdraw_refused", $"{id} already has a response ({existing.Member("path")?.AsString()}); nothing to withdraw");
        }

        var oneLine = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var options = new JsonObject
        {
            ["reqPath"] = requestPath,
            ["cwd"] = _cwd,
            ["type"] = "response",
            ["id"] = id,
            ["withdrawn"] = true,
            ["reason"] = oneLine,
            ["body"] = $"WITHDRAWN by {sender}: {oneLine}",
        };
        JsonValue created;
        try
        {
            created = Hier.Messages.CreateMessage(_graph.Bootstrap.EnsureHierarchyDir(dir), options);
        }
        catch (StoreUnavailableException e)
        {
            // defensive: under an outage from the start FindExchange already answers "unknown"; this is reached only if the store fails mid-command
            throw new MailFailure("store_unavailable", $"the message store cannot be read ({e.Message}); {id} was not withdrawn");
        }

        var path = JsConvertText(created.Member("path"));
        ChecklistWithdrawn(id, fm);
        var result = new JsonObject { ["id"] = id, ["withdrawn"] = true, ["from"] = sender, ["reason"] = oneLine, ["response"] = path };
        return Print(result, $"withdrew {id} as {sender}: {oneLine}\nresponse: {path}", 0);
    }

    /// <summary>
    /// The exchange <paramref name="id"/> as <c>ah msg list</c> finds it, in any pool of the teams this caller owns or is in: the exchange, its
    /// request's path and the request's front matter. <paramref name="missingPrefix"/> (with the id as <c>{0}</c>) leads the message when it is not there.
    /// </summary>
    private (JsonValue Exchange, JsonValue RequestPath, JsonValue? Fm) FindExchange(string id, string missingPrefix, List<string>? searchTeams = null)
    {
        var teams = searchTeams ?? ExchangeTeams();
        var pools = Hier.Messages.PoolDirs(HierarchyDir, teams);
        var exchange = Hier.Core.ListExchangesWithIds(pools, [id]).FirstOrDefault(e => e.Member("id") is { IsText: true } t && t.AsString() == id);
        if (exchange is not { IsMap: true } || exchange.Member("request")?.Member("path") is not { IsText: true } requestPath)
        {
            throw new MailFailure("unknown", string.Format(System.Globalization.CultureInfo.InvariantCulture, missingPrefix, id) + $"no exchange {id} in the {pools.Count} message pool(s) of team(s) {string.Join(", ", teams)}");
        }

        return (exchange, requestPath, Hier.Core.ReadMsgFile(requestPath.AsString()).Member("fm"));
    }

    /// <summary>
    /// For a request whose sender is named only by role, with no team: the Orchestrator address of the first team this caller owns whose pool holds
    /// <paramref name="id"/>, or null when the caller owns none that does.
    /// </summary>
    private string? StrandedRequestOwner(string id)
    {
        foreach (var team in RosterCore.OwnedTeams(HierarchyDir, new TeamInvoker(SessionPid, SessionId)))
        {
            var name = team ?? "team";
            var pools = Hier.Messages.PoolDirs(HierarchyDir, [name]);
            if (Hier.Core.ListExchangesWithIds(pools, [id]).Any(e => e.Member("id") is { IsText: true } t && t.AsString() == id))
            {
                return Ah.Hierarchy.HierarchyModule.OrchestratorAddress(name);
            }
        }

        return null;
    }

    /// <summary>Whether no live session registers an address named <paramref name="name"/> and the request's team <paramref name="team"/> has no team record.</summary>
    private bool SenderGone(string name, string? team)
    {
        var live = _graph.Mailbox.LiveRegistrations().Any(r => r.Get("addresses") is { IsArray: true } a
            && a.AsArray().Any(x => x.IsText && _graph.Mailbox.SplitAddress(x.AsString()) is { } parts && parts.Name == name));
        return !live && (team is null || RosterCore.ReadTeamRecord(HierarchyDir, team) is not { IsMap: true } record || record.Member("members") is not { IsArray: true });
    }

    /// <summary>The teams whose pools an exchange withdrawal looks in: <c>--team</c>, else the caller's own, else the default team.</summary>
    private List<string> ExchangeTeams()
    {
        if (_teamArg is { } named)
        {
            return [named];
        }

        var owned = RosterCore.OwnedTeams(HierarchyDir, new TeamInvoker(SessionPid, SessionId));
        return owned.Count > 0 ? [.. owned.Select(static t => t ?? "team")] : ["team"];
    }

    /// <summary>A front-matter text, null when it is absent or the written <c>null</c>.</summary>
    private static string? FrontText(JsonValue? v) => v is { IsText: true } t && t.AsString() is { Length: > 0 } s && s != "null" ? s : null;

    /// <summary>Records this session's dispatch of the request to <paramref name="address"/>, as a send of it does, so the watch clock times the new addressee.</summary>
    private bool RecordDispatch(string request, string address)
    {
        if (SessionId is not { Length: > 0 } session || Hier.Core.ParseMsgFilename(request) is not { Type: "request" } meta)
        {
            return false;
        }

        try
        {
            Hier.Peers.AppendDispatchRecord(session, meta.Id, meta.To, request, Hier.Peers.StripRef(address));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the re-address itself succeeded; a failed clock row must not fail it
            return false;
        }
    }

    /// <summary>The address of the box that holds entry <paramref name="id"/>, or null.</summary>
    private string? HolderOf(string id)
    {
        foreach (var box in _graph.Mailbox.ListBoxes())
        {
            if (_graph.Mailbox.ReadEntries(box.Dir).Entries.Any(e => e.IsMap && e.AsObject().Get("id") is { IsText: true } t && t.AsString() == id))
            {
                return box.Address;
            }
        }

        return null;
    }

    private JsonObject WithdrawRequest(string from, RequestContext ctx, string to, string id, JsonObject fields)
    {
        var request = new JsonObject { ["to"] = to, ["from"] = from == "user" ? JsonValue.Null : from, ["session_id"] = SendingSession is { } sid ? sid : JsonValue.Null, ["id"] = id };
        AddContext(request, ctx);
        foreach (var key in fields.Keys)
        {
            request[key] = fields[key];
        }

        return request;
    }

    private static string JsConvertText(JsonValue? v) => v is { IsText: true } t ? t.AsString() : string.Empty;
}
