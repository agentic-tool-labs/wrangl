using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;
using Ah.Hierarchy;
using Ah.Hooks.Gates;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Activity;

/// <summary>
/// UserPromptSubmit tracker for the peer report-back contract. A prompt carrying a well-formed brief sentinel inside its
/// cross-session wrapper records a pending obligation; any wrapped delivery arms the turn marker and a typed prompt disarms
/// it while an obligation is pending. It also acks mail the prompt carries, notes which dispatched peers were heard from,
/// annotates peer notes and reports, answers idle notices, hands over daemon findings, nudges toward the team skill on team
/// intent and states the CLI root when it changed. It never blocks the prompt except for a duplicate mail prompt.
/// </summary>
[HookIdentity(HookIdentityKind.Grants)]
internal static partial class UserPromptSubmitPeerTracking
{
    /// <summary>The block reason for a prompt that only repeats mail already delivered: a status line, not an error.</summary>
    internal static string DuplicateMailNote(IEnumerable<string> ids, string at) =>
        $"ah: this prompt repeats entry {string.Join(",", ids)}, already delivered at {at}. Nothing to do: do not act on it again; end the turn unless something else needs you.";

    /// <summary>When the first of <paramref name="ids"/> was acked, from the ack records; "an earlier turn" when none can be read.</summary>
    private static string AckedAt(Ah.Mesh.IMailbox mailbox, IEnumerable<string> ids)
    {
        try
        {
            foreach (var id in ids)
            {
                foreach (var box in mailbox.ListBoxes())
                {
                    if (mailbox.ReadAck(box.Dir, id) is { IsMap: true } ack && ack.Member("at") is { IsText: true } at)
                    {
                        return ack.Member("by") is { IsText: true } by ? $"{at.AsString()} (by {by.AsString()})" : at.AsString();
                    }
                }
            }
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return "an earlier turn";
        }

        return "an earlier turn";
    }

    private const string Ws = JsText.SpaceClass;
    private static readonly TimeSpan DaemonBudget = TimeSpan.FromMilliseconds(1500);

    [GeneratedRegex("^[" + Ws + "]*\\[Cross-session idle notice\\][" + Ws + "]+\"([^\"]+)\"((?s:.*))\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdleNotice();

    [GeneratedRegex("^[" + Ws + "]*\\[Cross-session delivery notice\\]((?s:.*))\\z", RegexOptions.CultureInvariant)]
    private static partial Regex DeliveryNotice();

    [GeneratedRegex("msg_id:[" + Ws + "]*([0-9A-Za-z-]+)", RegexOptions.CultureInvariant)]
    private static partial Regex NoticeMsgId();

    [GeneratedRegex("finished a turn at ([0-9]{1,2}:[0-9]{2})", RegexOptions.CultureInvariant)]
    private static partial Regex FinishedAt();

    [GeneratedRegex("«([^»]*)»", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex("^[" + Ws + "]*note[" + Ws + "]+[A-Za-z0-9._-]+:", RegexOptions.CultureInvariant)]
    private static partial Regex NoteLine();

    [GeneratedRegex("(?:^|(?<=[\\n\\r\\u2028\\u2029]))- note from [^" + Ws + "]+:", RegexOptions.CultureInvariant)]
    private static partial Regex MailNoteLine();

    [GeneratedRegex("^[" + Ws + "]*note[" + Ws + "]+([^" + Ws + "]+?):", RegexOptions.CultureInvariant)]
    private static partial Regex NoteId();

    /// <summary>
    /// Whether the prompt is text the user typed: not a peer delivery (a wrapped cross-session message), not mail or a compact mail
    /// row, not a harness task notification, and not any prompt <see cref="HierarchyModule.QaIsUserTyped"/> calls injected (a subagent
    /// hand-back, a plugin prompt, a doorbell). One rule for the user-prompt record, the latest-prompt id, the question row and the reply scan.
    /// </summary>
    internal static bool IsTypedPrompt(AdvisoryServices advisory, string prompt, string submitted, JsonValue wrapper) =>
        prompt.Length > 0 && prompt == submitted && !wrapper.IsTruthy && advisory.MailCore.MailMarkers(prompt) is null
        && !prompt.Contains("<cross-session-message", StringComparison.Ordinal) && !prompt.Contains("<task-notification>", StringComparison.Ordinal)
        && !StartsAutomated(prompt) && HierarchyModule.QaIsUserTyped(prompt);

    /// <summary>
    /// How the prompts that something other than the user submits begin: a slash command, ah's own idle and delivery notices, and the
    /// nudges the retry-on-error plugin submits in the user's place. A mod's prompt carries no origin field the hook can read, so
    /// the start of the text is what tells them apart. A prompt from a mod whose start is not listed here counts as typed.
    /// </summary>
    private static readonly string[] AutomatedPromptStarts =
    [
        "/",
        "<command-name>",
        "[Cross-session ",
        "Continue from where you stopped.",
        "The previous turn ended on an API error (",
    ];

    private static bool StartsAutomated(string prompt)
    {
        var text = prompt.TrimStart();
        return AutomatedPromptStarts.Any(start => text.StartsWith(start, StringComparison.Ordinal));
    }

    public static HookOutcome Run(GateContext c)
    {
        var s = c.Services;
        if (Ah.Hooks.Lifecycle.OrchestrateCommand.Run(c) is { } command)
        {
            return command;
        }

        if (s.Config.IsSubagent(c.Input))
        {
            return HookEmit.Silent;
        }

        if (Ah.Hooks.Lifecycle.ImplicitOptIn.FromPrompt(c) is { } optedIn)
        {
            return optedIn;
        }

        if (!Ah.Hooks.Lifecycle.OrchestratorIdentity.In(c))
        {
            return HookEmit.Silent;
        }

        var advisory = s.Advisory ?? throw new InvalidOperationException("the advisory services are not composed");
        var parts = new List<string>();
        var sessionId = c.Input.Text("session_id") ?? string.Empty;
        var prompt = c.Input.Text("prompt") ?? string.Empty;

        // A compact mail prompt carries only its row. The hook reads the full text back from the boxes and every step below sees
        // what it sees for the full prompt; the text itself reaches the model only as the quoted block.
        var compactPrompt = prompt;
        var expanded = ExpandCompactMail(c, advisory, sessionId, ref prompt);
        parts.AddRange(expanded);

        // The daemon keeps this session's clock and carries its mail; a hook is what brings it back mid-session.
        if (sessionId.Length > 0 && advisory.Mailbox.ReadRegistration(sessionId) is not null && HoldGate.Of(s, c.Cwd(), sessionId) is null)
        {
            try
            {
                advisory.Daemon.EnsureDaemonForHookAsync(DaemonBudget).GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }
        }

        if (sessionId.Length > 0 && prompt.Length > 0)
        {
            HookAckResult? mail = null;
            try
            {
                mail = advisory.MailboxHooks.AckFromPrompt(sessionId, prompt);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                c.LogError(e);
            }

            // A mail prompt that acked nothing new and names only ids a prompt hook already acked is a second copy of text
            // that already entered a turn. It passes through with a note: a prompt hook never blocks a prompt.
            if (mail is { Duplicate: true })
            {
                parts.RemoveAll(expanded.Contains);
                parts.Add(DuplicateMailNote(mail.Acked, AckedAt(advisory.Mailbox, mail.Acked)));
            }

            var peers = s.Hierarchy.Peers;
            var role = s.Config.ResolveHierarchyRole(c.Input).Role;
            var rec = peers.ExtractPendingRecord(prompt, role is null ? JsonValue.Null : role);
            if (rec.IsTruthy)
            {
                var row = new JsonObject
                {
                    ["session_id"] = sessionId,
                    ["from"] = rec.Member("from") ?? JsonValue.Null,
                    ["from_name"] = rec.Member("from_name") ?? JsonValue.Null,
                    ["reply_to"] = rec.Member("reply_to") ?? JsonValue.Null,
                    ["task"] = rec.Member("task") ?? JsonValue.Null,
                    ["armed_by"] = rec.Member("armed_by") ?? JsonValue.Null,
                };
                if (s.Hierarchy.Core.ExtractMsgToken(prompt) is { Length: > 0 } msg)
                {
                    row["msg"] = msg;
                }

                row["ts"] = Now(s);
                row["status"] = "pending";
                row["nudges"] = 0;
                peers.RecordPeerRow(row);
            }

            // A wrapped delivery carrying the response to one of this session's dispatches means the report reached it.
            var wrapper = peers.ParseWrapper(prompt);
            if (wrapper.IsTruthy)
            {
                RecordHeard(c, advisory, sessionId, wrapper, parts);
                var token = s.Hierarchy.Core.ExtractMsgToken(prompt);
                var meta = token is not null && token.EndsWith("--response.md", StringComparison.Ordinal) ? s.Hierarchy.Core.ParseMsgFilename(token) : null;
                if (meta is not null && peers.DispatchRecordsFor(sessionId).Any(r => r.Member("request_id") is { IsText: true } id && id.AsString() == meta.Id))
                {
                    peers.AppendReportRecord("seen", sessionId, meta.Id);
                }
            }

            // a prompt the mod bridge submitted itself (a Stop block's reason, hooks/register.ts) is not the user's (Amendment 1 G3)
            if (IsTypedPrompt(advisory, prompt, compactPrompt, wrapper) && c.Input.Member("plugin_submitted") is not { IsTruthy: true } && !HierarchyDisabled(c))
            {
                try
                {
                    var digest = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId + "\0" + Now(s) + "\0" + prompt));
                    var promptId = "p" + Convert.ToHexStringLower(digest)[..12];
                    peers.AppendUserPromptRecord(sessionId, promptId);
                    var at = s.Clock.UtcNow.ToUnixTimeMilliseconds();
                    var typed = prompt;
                    QaLogCapture.Append(c, advisory, sessionId, team => HierarchyModule.QaPromptRow(promptId, at, sessionId, team, typed));
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    c.LogError(e);
                }
            }

            if (DeliveryNoticeLine(c, sessionId, prompt) is { } notDelivered)
            {
                parts.Add(notDelivered);
            }

            List<string> idleNoReport = [];
            if (role is null or "orchestrator" or "deputy-chief")
            {
                if (role is not "deputy-chief" && advisory.Mailbox.DeputyTally(sessionId) is { } tally)
                {
                    parts.Add(tally);
                }

                var annotations = PeerAnnotations(s, advisory, prompt, wrapper);
                if (annotations.Count > 0)
                {
                    parts.Add(string.Join('\n', annotations));
                }

                // ponytail: repeats on each prompt until acked, as it did on each Stop; say it once per response if the repeat grates.
                if (Ah.Hooks.Lifecycle.StopOrchestratorLiveness.UnackedResponsesLine(c) is { } unacked)
                {
                    parts.Add(unacked);
                }

                var (idle, noReport) = IdleNoticeLines(c, sessionId, prompt, OwnsOrchestratorBox(advisory, sessionId));
                if (idle.Count > 0)
                {
                    parts.Add(string.Join('\n', idle));
                }

                idleNoReport = noReport;
                if (DismissNudge.Lines(c, advisory, sessionId) is { Count: > 0 } dismiss)
                {
                    parts.Add(string.Join('\n', dismiss));
                }

                if (s.Hierarchy.Peers.TakeBacklogDueLine(s.Config.HierarchyDir(c.Cwd()), JsonValue.FromString(sessionId)) is { } backlogDue)
                {
                    parts.Add(backlogDue);
                }

                // The daemon's findings come from the store, never from a path named in the prompt.
                var found = peers.UnconsumedWatchEvents(sessionId);
                if (found.Count > 0)
                {
                    parts.Add(string.Join('\n', found.Select(ev => JsConvert.ToJsString(ev.Member("text") ?? JsonValue.Null))));
                    peers.RecordPeerRow(new JsonObject
                    {
                        ["type"] = "watch-consumed",
                        ["session_id"] = sessionId,
                        ["ts"] = found[^1].Member("ts") ?? JsonValue.Null,
                    });
                }
            }

            // The digest carries the notes and advisories queued in this session's orchestrator@ boxes, a member's plain notes, and the
            // idle-with-no-report lines; a session with no orchestrator@ box keeps its idle lines where they always were.
            if (DigestBlock(c, advisory, sessionId, idleNoReport) is { } digestBlock)
            {
                parts.Add(digestBlock);
            }

            // This hook owns both edges of the turn marker: a wrapped delivery arms, a typed prompt disarms.
            if (PeerLatestFor.Of(c).HasPending(sessionId))
            {
                peers.AppendTurnMarker(sessionId, peers.ParseWrapper(prompt).IsTruthy ? "armed" : "disarmed");
            }
        }

        if (prompt.Length > 0 && advisory.TeamIntent.MatchedPhrase(prompt) is not null)
        {
            parts.Add($"ah: standing up, reshaping, or tearing down a live Team goes through the `{s.Config.PluginName}:agent-team` skill.");
        }

        // `/reload-plugins` fires no SessionStart, so a session that updates mid-flight never hears the root line that hook
        // injected. A prompt states it when it differs from the line last put into this session's context.
        if (!HierarchyDisabled(c))
        {
            var rootLine = s.Config.CliRootLine();
            if (!s.Hierarchy.Peers.CliRootAlreadyEmitted(sessionId, rootLine))
            {
                parts.Add(rootLine);
                s.Hierarchy.Peers.RecordCliRootEmitted(sessionId, rootLine);
            }
        }

        return parts.Count > 0 ? HookEmit.AdditionalContext("UserPromptSubmit", string.Join("\n\n", parts)) : HookEmit.Silent;
    }

    /// <summary>Whether the cwd is configured for the hierarchy and the hierarchy is switched off there: nothing is written about it.</summary>
    private static bool HierarchyDisabled(GateContext c)
    {
        var resolved = c.Services.Config.ResolveConfig(c.Cwd());
        return resolved.Get("configured") is { IsTruthy: true } && resolved.Get("enabled") is not { IsTruthy: true };
    }

    /// <summary>
    /// The harness's report that one of this session's messages was dropped at the recipient's inbox (a relay loop was cut). It
    /// arrives as the sender's next input, about a dozen seconds after a send that reported success. The report names the message by
    /// id: the obligations that send resolved are reopened, and the sender is told its response file is filed and to send one short
    /// pointer after its current work. A notice that does not say NOT delivered, or names no id, is left alone.
    /// </summary>
    private static string? DeliveryNoticeLine(GateContext c, string sessionId, string prompt)
    {
        var notice = DeliveryNotice().Match(prompt);
        if (!notice.Success || !notice.Groups[1].Value.Contains("NOT delivered", StringComparison.Ordinal)
            || NoticeMsgId().Match(notice.Groups[1].Value) is not { Success: true } id)
        {
            return null;
        }

        try
        {
            var s = c.Services;
            var sent = s.Hierarchy.Core.SessionRows(sessionId, "sent")
                .LastOrDefault(r => r.Member("type") is { IsText: true } t && t.AsString() == "sent"
                    && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                    && r.Member("msg_id") is { IsText: true } mid && mid.AsString() == id.Groups[1].Value);
            if (!sent.IsMap)
            {
                return $"ah: a message you sent was not delivered (msg_id {id.Groups[1].Value}). If it was a report, its response is filed: send ONE short pointer after your current work, never in a loop.";
            }

            var token = sent.Member("response") is { IsText: true } r ? r.AsString() : string.Empty;
            var requestId = s.Hierarchy.Core.ParseMsgFilename(Path.GetFileName(token))?.Id ?? string.Empty;
            var again = requestId.Length > 0 && s.Hierarchy.Core.SessionRows(sessionId, "reopened").Any(x =>
                x.Member("type") is { IsText: true } xt && xt.AsString() == "reopened"
                && x.Member("session_id") is { IsText: true } xs && xs.AsString() == sessionId
                && x.Member("request_id") is { IsText: true } xr && xr.AsString() == requestId);
            s.Hierarchy.Peers.RecordPeerRow(new JsonObject
            {
                ["type"] = "reopened",
                ["session_id"] = sessionId,
                ["request_id"] = requestId,
                ["msg_id"] = id.Groups[1].Value,
                ["ts"] = Now(s),
            });

            if (again)
            {
                return $"ah: your report was cut again (msg_id {id.Groups[1].Value}); its response is filed, `[hierarchy-msg {Ah.Core.Json.MsgPointer.Handle(token) ?? token}]`. Send ONE short pointer after your current work, never in a loop.";
            }

            foreach (var rec in sent.Member("resolved") is { IsArray: true } list ? list.AsArray() : [])
            {
                var reopened = new JsonObject();
                foreach (var key in rec.AsObject().Keys)
                {
                    reopened[key] = rec.AsObject()[key];
                }

                reopened["ts"] = Now(s);
                reopened["status"] = "pending";
                reopened["nudges"] = 0;
                s.Hierarchy.Peers.RecordPeerRow(reopened);
            }

            var slug = s.Hierarchy.Core.ParseMsgFilename(Path.GetFileName(token))?.Slug ?? "response";
            var to = sent.Member("to") is { IsText: true } rcpt ? rcpt.AsString() : "its recipient";
            return $"ah: your {slug} report to {to} was not delivered (the harness cut a relay loop); your response is filed. Send ONE short pointer after your current work, `[hierarchy-msg {Ah.Core.Json.MsgPointer.Handle(token) ?? token}]`, never in a loop.";
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            c.LogError(e);
            return null;
        }
    }

    /// <summary>Largest quoted block the hook emits; a longer one is reported as not loaded.</summary>
    private const int MaxBlockBytes = 64 * 1024;

    /// <summary>
    /// The context parts for a compact mail prompt, with <paramref name="prompt"/> replaced by the full text of the entries that
    /// loaded; none for any other prompt. The quoted block (for the entries that loaded) comes first, then the not-loaded line for
    /// the rest. A failure that loads nothing, or a block that is too long, is the not-loaded line alone naming every marker id,
    /// with one hook-errors row, and leaves the prompt as it is.
    /// </summary>
    private static List<string> ExpandCompactMail(GateContext c, AdvisoryServices advisory, string sessionId, ref string prompt)
    {
        var parts = new List<string>();
        var core = advisory.MailCore;
        if (prompt.Length == 0 || core.ParseCompactMail(prompt) is not { } compact)
        {
            return parts;
        }

        var allIds = compact.Markers.SelectMany(m => m.Ids).ToList();
        FullMail full;
        try
        {
            full = advisory.MailboxHooks.FullMailText(sessionId, compact);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            full = new FullMail(null, [], compact.Address, "read failed", []);
            c.LogError(e);
        }

        string? failure = null;
        IReadOnlyList<string> failIds = full.MissingIds.Count > 0 ? full.MissingIds : allIds;
        if (full.Text is { } text)
        {
            var block = core.QuotedForHook(full.Ids, text);
            if (Utf8Text.Encode(block).Length > MaxBlockBytes)
            {
                failure = "too long";
                failIds = allIds;
            }
            else
            {
                prompt = text;
                parts.Add(block);
                failure = full.MissingIds.Count > 0 ? full.Reason ?? "not in your mailbox" : null;
            }
        }
        else
        {
            failure = full.Reason ?? "unknown";
        }

        if (failure is null)
        {
            return parts;
        }

        try
        {
            c.Services.ErrorLog.Log(new HookErrorRecord(
                c.Name,
                $"mail body not loaded ({failure}): ids={string.Join(",", failIds)} address={full.Address}",
                c.Input.Text("hook_event_name"),
                sessionId.Length > 0 ? sessionId : null));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the log is the last link in the chain
        }

        parts.Add(core.LoadFailureLine(failIds, failure, full.Address));
        return parts;
    }

    private static string Now(GateServices s) => Timestamps.ToIsoString(s.Clock.UtcNow);

    /// <summary>
    /// A peer that writes to this session restarts the check-in clock for it. The sender counts as a dispatched peer by its
    /// socket address, by its name, or by a progress note naming one of this session's requests. An idle notice is not
    /// wrapped, so it never counts as hearing from the peer.
    /// </summary>
    private static void RecordHeard(GateContext c, AdvisoryServices advisory, string sessionId, JsonValue wrapper, List<string> parts)
    {
        var s = c.Services;
        var peers = s.Hierarchy.Peers;
        var body = wrapper.Member("body") is { IsText: true } b ? b.AsString() : string.Empty;
        var from = wrapper.Member("from") is { IsText: true } f ? f.AsString() : string.Empty;
        var fromName = wrapper.Member("fromName") is { IsText: true } fn ? fn.AsString() : string.Empty;
        var noteMatch = NoteId().Match(body);
        var noteId = noteMatch.Success ? noteMatch.Groups[1].Value : string.Empty;
        var heard = new List<(string To, string? Request)>();
        foreach (var r in peers.LatestDispatchRows(sessionId))
        {
            if (r.Member("to_addr") is not { IsText: true } toValue || toValue.AsString().Length == 0)
            {
                continue;
            }

            var to = toValue.AsString();
            if (heard.Any(h => h.To == to))
            {
                continue;
            }

            var byAddr = peers.StripRef(from) == to || (fromName.Length > 0 && fromName == to);
            var requestId = r.Member("request_id") is { IsText: true } rid ? rid.AsString() : string.Empty;
            var byNote = requestId.Length > 0 && s.Hierarchy.Messages.IsMessageId(requestId) && byAddr && (requestId == noteId || body.Contains(requestId, StringComparison.Ordinal));
            if (byAddr || byNote)
            {
                heard.Add((to, byNote ? requestId : null));
            }
        }

        foreach (var (to, request) in heard)
        {
            peers.RecordPeerRow(new JsonObject
            {
                ["type"] = "heard",
                ["session_id"] = sessionId,
                ["from"] = to,
                ["request"] = request is null ? JsonValue.Null : request,
                ["ts"] = Now(s),
            });
        }

        try
        {
            var line = s.Mesh.LinkEnvelopeFor(advisory.Callers.RequestContext(c.Cwd()), sessionId, from);
            if (!string.IsNullOrEmpty(line))
            {
                parts.Add(line);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            c.LogError(e);
        }
    }

    /// <summary>
    /// What the orchestrator should know about a delivered peer note or report before acting on it. Three prompt shapes can
    /// carry one: a wrapped cross-session delivery and the mod's mail prompt. A typed prompt carries none.
    /// </summary>
    private static List<string> PeerAnnotations(GateServices s, AdvisoryServices advisory, string prompt, JsonValue wrapper)
    {
        var wrapped = wrapper.IsTruthy;
        string? text = wrapped
            ? (wrapper.Member("body") is { IsText: true } b ? b.AsString() : string.Empty)
            : advisory.MailCore.MailMarkers(prompt) is not null ? prompt : null;
        var output = new List<string>();
        if (text is null)
        {
            return output;
        }

        var note = wrapped ? NoteLine().IsMatch(text) : MailNoteLine().IsMatch(text);
        if (note)
        {
            output.Add($"ah: {HierarchyModule.NoteRule}");
        }

        var files = new JsonArray();
        foreach (var key in s.Hierarchy.Core.ExtractMsgTokens(text).Where(static k => k.StartsWith('/') && k.EndsWith("--response.md", StringComparison.Ordinal)))
        {
            files.Add(key);
        }

        var rules = s.Hierarchy.Messages.FollowUpRules(new JsonObject { ["delivered"] = text, ["files"] = files });
        foreach (var rule in rules)
        {
            var line = JsConvert.ToJsString(rule);
            if (!output.Contains(line))
            {
                output.Add(line);
            }
        }

        return output;
    }

    /// <summary>
    /// The digest block (<see cref="IDirectiveText.DigestHeader"/>) of a session: one line per unacked digest entry (kind, sender,
    /// request, the first 200 characters of its text or its <c>--ref</c> path), then the idle-with-no-report lines, at most 20 lines with a
    /// <c>+K more</c> line for the entries cut. The entries shown are acked; the idle lines are not entries and are never acked. Null when the
    /// block would be empty.
    /// </summary>
    private static string? DigestBlock(GateContext c, AdvisoryServices advisory, string sessionId, IReadOnlyList<string> idleLines)
    {
        try
        {
            if (sessionId.Length == 0)
            {
                return null;
            }

            var s = c.Services;
            var bell = MeshModule.CreateMailDoorbell(advisory.Mailbox, advisory.MailCore, s.Files, s.Environment, advisory.MailboxHooks);
            var entries = bell.DigestEntries(sessionId, s.Clock.UtcNow.ToUnixTimeMilliseconds());
            if (MeshModule.DigestText(entries, idleLines, out var shown) is not { } block)
            {
                return null;
            }

            foreach (var byBox in shown.GroupBy(static e => e.Dir))
            {
                _ = advisory.Mailbox.AckIds(byBox.Key, [.. byBox.Select(static e => e.Id)], "digest", sessionId);
            }

            return block;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException and not StoreUnavailableException)
        {
            c.LogError(e);
            return null;
        }
    }

    /// <summary>Whether the session registered an <c>orchestrator@</c> box: only such a session has a digest.</summary>
    internal static bool OwnsOrchestratorBox(AdvisoryServices advisory, string sessionId) =>
        sessionId.Length > 0 && advisory.Mailbox.ReadRegistration(sessionId) is { } registration
        && advisory.Mailbox.RegistrationBoxes(registration).Any(static b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal));

    private const string IdleNoReportShown = "idle-noreport-shown";

    /// <summary>
    /// Whether the idle-with-no-report line for (<paramref name="name"/>, <paramref name="requestId"/>) may be shown now, recording it when so:
    /// at most once before its check-in time and once after it, then never (S7.5, ruling G2). A line with no known check-in time is shown once.
    /// </summary>
    private static bool ShowIdleNoReport(GateServices s, string sessionId, string name, string requestId, double? dueMs)
    {
        var phase = dueMs is { } due && s.Clock.UtcNow.ToUnixTimeMilliseconds() >= due ? "after" : "before";
        var seen = s.Hierarchy.Core.SessionRows(sessionId, IdleNoReportShown).Any(r =>
            r.Member("type") is { IsText: true } t && t.AsString() == IdleNoReportShown
            && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
            && r.Member("name") is { IsText: true } n && n.AsString() == name
            && r.Member("request") is { IsText: true } rq && rq.AsString() == requestId
            && r.Member("phase") is { IsText: true } p && p.AsString() == phase);
        if (seen)
        {
            return false;
        }

        s.Hierarchy.Peers.RecordPeerRow(new JsonObject
        {
            ["type"] = IdleNoReportShown, ["session_id"] = sessionId, ["name"] = name, ["request"] = requestId, ["phase"] = phase, ["ts"] = Now(s),
        });
        return true;
    }

    /// <summary>
    /// For an idle notice naming peer X, the lines to inject: one per request this session dispatched to X and has not yet
    /// been shown the report for. A notice that repeats the last one for X injects nothing, and one whose wording is not
    /// recognised injects a single check-X line. It never asks for a status query or a re-subscribe. The idle-with-no-report
    /// lines are throttled, and with <paramref name="toDigest"/> they come back apart (<c>NoReport</c>) for the digest; without it they
    /// stay in place among the other lines.
    /// </summary>
    private static (List<string> Lines, List<string> NoReport) IdleNoticeLines(GateContext c, string sessionId, string prompt, bool toDigest)
    {
        var s = c.Services;
        var lines = new List<string>();
        var noReport = new List<string>();
        var m = IdleNotice().Match(prompt);
        if (!m.Success)
        {
            return (lines, noReport);
        }

        var peers = s.Hierarchy.Peers;
        var name = m.Groups[1].Value;
        var body = m.Groups[2].Value;
        var recognised = body.Contains("is idle now", StringComparison.Ordinal);
        var finished = FinishedAt().Match(body);
        var quoted = Quoted().Match(body);
        var key = $"{(finished.Success ? finished.Groups[1].Value : string.Empty)}|{(quoted.Success ? quoted.Groups[1].Value : string.Empty)}|{(recognised ? string.Empty : Slice(body, 200))}";
        JsonValue? last = null;
        foreach (var r in s.Hierarchy.Core.SessionRows(sessionId, "idle-seen"))
        {
            if (r.Member("type") is { IsText: true } t && t.AsString() == "idle-seen"
                && r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                && r.Member("name") is { IsText: true } n && n.AsString() == name)
            {
                last = r;
            }
        }

        if (last is { } l && l.Member("key") is { IsText: true } lk && lk.AsString() == key)
        {
            return (lines, noReport);
        }

        peers.RecordPeerRow(new JsonObject { ["type"] = "idle-seen", ["session_id"] = sessionId, ["name"] = name, ["key"] = key, ["ts"] = Now(s) });
        var dispatched = DispatchObligations.Live(s, sessionId);
        if (recognised && finished.Success && StaleSince(c, name, finished.Groups[1].Value, dispatched) is { } since)
        {
            lines.Add($"stale idle notice: {name} has since {since}; {(since.StartsWith("become blocked", StringComparison.Ordinal) || since.StartsWith("become failed", StringComparison.Ordinal) ? "act on that, not this notice" : "ignore it")}.");
            return (lines, noReport);
        }

        if (!recognised)
        {
            var open = dispatched
                .Where(r => r.Member("path") is { IsTruthy: true } && r.Member("to_addr") is { IsText: true } to && to.AsString() == name
                    && !peers.ReportShown(sessionId, r.Member("request_id") ?? JsonValue.Null))
                .Select(r => JsConvert.ToJsString(r.Member("request_id") ?? JsonValue.Null))
                .ToList();
            lines.Add(
                $"ah: idle notice about {name} not recognised: «{Slice(JsText.Trim(body), 200)}». Call ListAgents. If {name} is gone and has open requests ({(open.Count > 0 ? string.Join(", ", open) : "none")}), tell the user. If {name} is alive, there is nothing to do; ah keeps time.");
            return (lines, noReport);
        }

        var nowMs = (double)s.Clock.UtcNow.ToUnixTimeMilliseconds();
        foreach (var row in dispatched)
        {
            if (row.Member("path") is not { IsText: true } pathValue || pathValue.AsString().Length == 0
                || row.Member("to_addr") is not { IsText: true } to || to.AsString() != name
                || peers.ReportShown(sessionId, row.Member("request_id") ?? JsonValue.Null))
            {
                continue;
            }

            var requestId = row.Member("request_id") is { IsText: true } rid ? rid.AsString() : string.Empty;
            var exchange = HierarchyModule.CreateExchangeIndex(s.Hierarchy.Core, s.Hierarchy.Messages.PoolsOfRequest(pathValue.AsString())).Find(requestId);
            if (exchange.IsNull)
            {
                continue;
            }

            var id = requestId;
            if (exchange.Member("open") is not { IsTruthy: true })
            {
                var responsePath = exchange.Member("response")?.Member("path") is { IsText: true } rp ? rp.AsString() : string.Empty;
                if (!s.Hierarchy.Messages.ResponseLanded(responsePath, nowMs) || Ah.Hooks.Lifecycle.StopOrchestratorLiveness.ReachedOf(s, sessionId)(requestId, responsePath))
                {
                    continue;
                }

                var followUp = s.Hierarchy.Messages.ReportFollowUp(responsePath);
                var followText = followUp is { IsText: true } fu && fu.AsString().Length > 0 ? $" {fu.AsString()}" : string.Empty;
                lines.Add($"{name} is idle; its report for {id} landed but was never sent to you: {responsePath}. Read it now.{followText}");
                peers.AppendReportRecord("surfaced", sessionId, id);
            }
            else
            {
                var requestPath = exchange.Member("request")?.Member("path") is { IsText: true } qp ? qp.AsString() : string.Empty;
                var fm = s.Hierarchy.Core.ReadMsgFile(requestPath).Member("fm") ?? JsonValue.Null;
                var due = " checks in";
                double? dueMs = null;
                if (fm.Member("created") is { IsText: true } created && Timestamps.TryParseMs(created.AsString(), out var createdMs))
                {
                    var at = createdMs + s.Hierarchy.Messages.ThresholdFor(fm.Member("eta") ?? JsonValue.Null) * 1000;
                    due = $" checks in at {HourMinute(s, at)}";
                    dueMs = at;
                }

                if (ShowIdleNoReport(s, sessionId, name, id, dueMs))
                {
                    (toDigest ? noReport : lines).Add($"{name} is idle with no report for {id} yet. It may be waiting on its own background work. Nothing to do now; ah{due}.");
                }
            }
        }

        // Said for every recognised idle notice, with or without a dispatch line of its own.
        lines.Add($"Never re-subscribe to an idle peer; {name} is idle now.");
        return (lines, noReport);
    }

    /// <summary>The member part of a dispatch address: <c>name@team</c> is <c>name</c>, a bare name is itself. A <c>uds:</c> address names no member here, so it never matches.</summary>
    private static string MemberOf(string address) => address.Contains('@', StringComparison.Ordinal) ? address[..address.IndexOf('@', StringComparison.Ordinal)] : address;

    /// <summary>
    /// What happened to peer <paramref name="name"/> after the idle notice's <paramref name="finishedClock"/> (a local HH:MM), as a phrase
    /// for the stale line, or null when nothing did and the notice is fresh. A report that landed after it wins; else an activity that is
    /// not idle, or that changed after it. A peer with no activity record, or a notice time that cannot be placed, is fresh.
    /// </summary>
    private static string? StaleSince(GateContext c, string name, string finishedClock, IReadOnlyList<JsonValue> dispatched)
    {
        var s = c.Services;
        var finishedMs = FinishedInstantMs(s, finishedClock);
        foreach (var row in dispatched)
        {
            if (row.Member("path") is not { IsText: true } pathValue || pathValue.AsString().Length == 0
                || row.Member("to_addr") is not { IsText: true } to || MemberOf(to.AsString()) != name
                || row.Member("request_id") is not { IsText: true } rid)
            {
                continue;
            }

            var exchange = HierarchyModule.CreateExchangeIndex(s.Hierarchy.Core, s.Hierarchy.Messages.PoolsOfRequest(pathValue.AsString())).Find(rid.AsString());
            if (exchange.IsNull || exchange.Member("open") is { IsTruthy: true })
            {
                continue;
            }

            if (exchange.Member("response")?.Member("path") is { IsText: true } responsePath
                && s.Hierarchy.Core.MsgTimeMs(responsePath.AsString()) is { } written && written >= finishedMs + MinuteMs)
            {
                return $"reported at {HourMinute(s, written)}";
            }
        }

        var dir = s.Config.HierarchyDir(c.Cwd());
        var attributed = s.Hierarchy.Roster.AttributedLiveness(s.Hierarchy.Roster.AttributedRoster(dir), name).Get("rec");
        if (attributed?.Member("session_id") is not { IsText: true } peerSession
            || s.Hierarchy.Status.ReadActivityRecord(dir, peerSession) is not { IsMap: true } record
            || record.Member("activity") is not { IsText: true } activity)
        {
            return null;
        }

        var become = $"become {activity.AsString()}";
        var sinceMs = record.Member("state_since") is { IsText: true } stamp && Timestamps.TryParseMs(stamp.AsString(), out var parsed) ? parsed : (double?)null;
        var at = sinceMs is { } ms ? $" at {HourMinute(s, ms)}" : string.Empty;
        if (activity.AsString() is "working" or "blocked" or "failed")
        {
            return become + at;
        }

        return activity.AsString() == "idle" && sinceMs >= finishedMs + MinuteMs ? become + at : null;
    }

    // The notice names its time to the minute, so anything inside that minute happened no later than the turn it reports.
    private const double MinuteMs = 60_000;

    /// <summary>The latest instant that is not in the future whose local clock reads <paramref name="clock"/> (H:MM): today's, else yesterday's.</summary>
    private static double FinishedInstantMs(GateServices s, string clock)
    {
        var parts = clock.Split(':');
        var hour = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var minute = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var now = s.Clock.UtcNow;
        var today = now.ToOffset(s.Clock.LocalOffset(now));

        // The offset is taken for the candidate's own date, so a day that crossed a clock change still reads right.
        DateTimeOffset At(DateTime day)
        {
            var wall = new DateTime(day.Year, day.Month, day.Day, hour, minute, 0, DateTimeKind.Unspecified);
            var guess = new DateTimeOffset(wall, today.Offset);
            var offset = s.Clock.LocalOffset(guess);
            return offset == today.Offset ? guess : new DateTimeOffset(wall, offset);
        }

        var candidate = At(today.Date);
        if (candidate > now)
        {
            candidate = At(today.Date.AddDays(-1));
        }

        return candidate.ToUnixTimeMilliseconds();
    }

    private static string Slice(string text, int length) => text.Length <= length ? text : text[..length];

    private static string HourMinute(GateServices s, double epochMs)
    {
        var instant = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(epochMs));
        var local = instant.ToOffset(s.Clock.LocalOffset(instant));
        return string.Create(CultureInfo.InvariantCulture, $"{local.Hour:00}:{local.Minute:00}");
    }
}
