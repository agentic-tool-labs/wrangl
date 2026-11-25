using System.Globalization;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Mesh;

internal sealed partial class MailboxHooks
{
    private const string DefaultSocketDirectory = "/tmp/cc-socks";

    /// <summary>An entry a SendMessage names that has a claim record: where it is, and the record as read.</summary>
    internal sealed record RelayTarget(string Id, string Dir, string Address, JsonObject Entry, JsonObject Claim);

    /// <summary>The ids that have no claim record: a relay is no delivery proof for an entry that has one.</summary>
    private List<string> Unclaimed(string dir, IReadOnlyList<string> ids) => ids.Where(id => ClaimMachine.Live(mailbox.ReadClaim(dir, id)) is null).ToList();

    private static string Text(JsonObject o, string key) => o.Get(key) is { IsText: true } t ? t.AsString() : string.Empty;

    private static string ClaimState(JsonObject claim) => Text(claim, "state");

    internal static string ReleasedText(string id) => $"ah: entry {id} was released to the delivery ladder (no poller); do not resend it.";

    private static string DeniedNotSession(string id) => $"ah: entry {id} is not this session's to relay; do not send it.";

    private static string DeniedUnrecorded() => "ah could not record this relay; do not send it; ah will deliver the entry itself.";

    /// <summary>
    /// The entries with a claim record that <paramref name="message"/> names by marker, or by the request token of a brief in the
    /// recipient's box, in message order. An entry that has no claim record is not listed: it follows the old rules.
    /// </summary>
    internal List<RelayTarget> RelayTargets(string? recipient, string? message, RequestContext context, out List<(string Dir, string Id)> unknown)
    {
        unknown = [];
        var targets = new List<RelayTarget>();
        var target = TrailingRefRe().Replace(recipient ?? string.Empty, string.Empty);
        var resolved = mailbox.ResolveAddress(target, context).Value;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string dir, string address, JsonObject entry)
        {
            var id = Text(entry, "id");
            if (seen.Add(dir + "\n" + id) && mailbox.ReadClaim(dir, id) is { } claim)
            {
                targets.Add(new RelayTarget(id, dir, address, entry, claim));
            }
        }

        foreach (var mark in core.EntryMarkers(message))
        {
            string? address = null;
            string? dir = null;
            if (string.IsNullOrEmpty(mark.Address))
            {
                address = resolved?.Address;
                dir = resolved?.Dir;
            }
            else if (BoxAddressOf(mark.Address, context) is { } named && mailbox.ResolveAddress(named, context).Value is { } box)
            {
                address = box.Address;
                dir = box.Dir;
            }

            if (address is null || dir is null)
            {
                continue;
            }

            var entries = mailbox.ReadEntries(dir).Entries.Where(e => e.IsMap).Select(e => e.AsObject()).ToList();
            foreach (var id in mark.Ids)
            {
                var entry = entries.FirstOrDefault(e => Text(e, "id") == id);
                if (entry is null)
                {
                    unknown.Add((dir, id));
                    continue;
                }

                Add(dir, address, entry);
            }
        }

        var token = hierarchy.Core.ExtractMsgToken(message);
        if (!string.IsNullOrEmpty(token) && resolved is not null)
        {
            foreach (var e in mailbox.ReadEntries(resolved.Dir).Entries.Where(e => e.IsMap).Select(e => e.AsObject()))
            {
                if (Text(e, "ref") == token)
                {
                    Add(resolved.Dir, resolved.Address, e);
                }
            }
        }

        return targets;
    }

    /// <summary>Whether <paramref name="t"/> reads <c>delivery-failed</c> and no session holds its relay claim.</summary>
    private bool DeliveryFailed(RelayTarget t) =>
        ClaimState(t.Claim) != ClaimStates.RelayClaimed
        && MsgStatus.Of(mailbox, new MsgStatus.Located(t.Dir, t.Address, t.Entry)).Get("state") is { IsText: true } state && state.AsString() == MsgStatus.DeliveryFailed;

    /// <summary>The model-facing text for why <paramref name="t"/> cannot be relayed by <paramref name="session"/> now, or null when it can be claimed.</summary>
    private string? WhyNotClaimable(RelayTarget t, string session, string? toolUseId, string? recipient, ResolvedAddress? resolved, RequestContext context)
    {
        var registration = mailbox.ReadRegistration(session);
        var mine = registration?.Get("addresses") is { IsArray: true } addresses
            ? addresses.AsArray().Where(a => a.IsText).Select(a => a.AsString()).ToHashSet(StringComparer.Ordinal)
            : [];
        var claim = t.Claim;
        var state = ClaimState(claim);
        var to = Text(t.Entry, "to");
        if (to.Length == 0)
        {
            to = t.Address;
        }

        if (state is ClaimStates.FallbackClaimed or ClaimStates.Delivered)
        {
            return $"ah: entry {t.Id} was already delivered to {to}; do not resend it.";
        }

        if (state == ClaimStates.RelayFailed)
        {
            return $"ah: the relay of entry {t.Id} failed; ah is delivering it another way; do not resend it.";
        }

        if (state == ClaimStates.Released)
        {
            return ReleasedText(t.Id);
        }

        if (Text(claim, "carrier") != "relay")
        {
            return $"ah: entry {t.Id} is delivered by the plugin prompt; do not send it by SendMessage.";
        }

        if (state is ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed)
        {
            return $"ah: entry {t.Id} was already delivered to {to}; do not resend it.";
        }

        var held = claim.Get("holder") is { IsMap: true } h ? h.AsObject() : null;
        var holder = held is null ? string.Empty : Text(held, "session_id");
        if (state == ClaimStates.RelayClaimed && holder != session)
        {
            return $"ah: entry {t.Id} is already being delivered by {holder}; do not send it.";
        }

        if (state == ClaimStates.RelayClaimed && (toolUseId is null || held is null || Text(held, "tool_use_id") != toolUseId))
        {
            return $"ah: entry {t.Id} is already being sent by this session; do not send it again.";
        }

        if (Text(claim, "sender_session") != session || !mine.Contains(Text(t.Entry, "from")))
        {
            return DeniedNotSession(t.Id);
        }

        return RecipientIs(t, recipient, resolved, context) ? null : $"ah: entry {t.Id} is addressed to {t.Address}, not {TrailingRefRe().Replace(recipient ?? string.Empty, string.Empty)}; do not relay it there.";
    }

    /// <summary>Whether the SendMessage's recipient is the entry's: its box address, or the receiver session's own socket.</summary>
    private bool RecipientIs(RelayTarget t, string? recipient, ResolvedAddress? resolved, RequestContext context)
    {
        if (resolved is not null)
        {
            return resolved.Address == t.Address;
        }

        var target = TrailingRefRe().Replace(recipient ?? string.Empty, string.Empty);
        if (!target.StartsWith("uds:", StringComparison.Ordinal))
        {
            return false;
        }

        var receiver = Text(t.Claim, "receiver_session");
        return receiver.Length > 0
            && mailbox.ReadRegistration(receiver)?.Get("pid") is { IsNumber: true } pid
            && target == SocketOf((long)pid.AsNumber(), context);
    }

    private static string SocketOf(long pid, RequestContext context)
    {
        var dir = string.IsNullOrEmpty(context.SocketDirectory) ? DefaultSocketDirectory : context.SocketDirectory;
        return $"uds:{dir.TrimEnd('/')}/{pid.ToString(CultureInfo.InvariantCulture)}.sock";
    }

    public RelayClaimDecision ClaimRelay(string? sessionId, string? recipient, string? message, RequestContext context, string? toolUseId = null)
    {
        if (!mailbox.CarrierSettingsFor(context).SingleCarrier)
        {
            return new RelayClaimDecision(false, null, []);
        }

        var session = sessionId ?? string.Empty;
        // an entry the ladder gave up on is let through untouched: no claim is checked or taken for it (ah2-565 C5)
        var targets = RelayTargets(recipient, message, context, out var unknown).Where(t => !DeliveryFailed(t)).ToList();
        foreach (var (dir, id) in unknown)
        {
            try
            {
                mailbox.AppendAttempt(dir, new JsonObject { ["id"] = id, ["rung"] = 0, ["state"] = "relay_unknown_entry", ["session_id"] = session });
            }
            catch (Exception e) when (e is MailboxError or IOException)
            {
                // evidence only
            }
        }

        if (targets.Count == 0)
        {
            return new RelayClaimDecision(false, null, []);
        }

        var resolved = mailbox.ResolveAddress(TrailingRefRe().Replace(recipient ?? string.Empty, string.Empty), context).Value;
        foreach (var t in targets)
        {
            var why = session.Length == 0 ? DeniedNotSession(t.Id) : WhyNotClaimable(t, session, string.IsNullOrEmpty(toolUseId) ? null : toolUseId, recipient, resolved, context);
            if (why is not null)
            {
                try
                {
                    mailbox.AppendAttempt(t.Dir, new JsonObject { ["id"] = t.Id, ["rung"] = 0, ["state"] = "relay_denied", ["session_id"] = session, ["reason"] = why });
                }
                catch (Exception e) when (e is MailboxError or IOException)
                {
                    // evidence only
                }

                return new RelayClaimDecision(true, why, []);
            }
        }

        return TakeClaims(session, string.IsNullOrEmpty(toolUseId) ? null : toolUseId, targets, context);
    }

    public string? PointerDuplicate(string? message, RequestContext context)
    {
        if (string.IsNullOrEmpty(message) || !mailbox.CarrierSettingsFor(context).SingleCarrier)
        {
            return null;
        }

        var tokens = hierarchy.Core.ExtractMsgTokens(message).Select(t => (Token: t, Name: NodePath.Basename(t), Canonical: new Lazy<string>(() => mailbox.CanonicalPath(t)))).ToList();
        if (tokens.Count == 0)
        {
            return null;
        }

        var hits = new List<(string BoxDir, string BoxAddress, JsonObject Entry, string Id, string Token)>();
        foreach (var box in mailbox.ListBoxes())
        {
            foreach (var entry in mailbox.ReadEntries(box.Dir).Entries.Where(e => e.IsMap).Select(e => e.AsObject()))
            {
                var reference = Text(entry, "ref");
                if (reference.Length == 0)
                {
                    continue;
                }

                var name = NodePath.Basename(reference);
                var hit = tokens.FirstOrDefault(t => t.Name == name && (t.Token == reference || t.Canonical.Value == mailbox.CanonicalPath(reference)));
                if (hit.Token is not null)
                {
                    hits.Add((box.Dir, box.Address, entry, Text(entry, "id"), hit.Token));
                }
            }
        }

        // A token is the claim's to judge when the message carries a well-formed marker for some entry whose ref is that token, however
        // many other entries (an earlier send of an amended report, a copy for another recipient) share the ref. A malformed marker,
        // or one for an entry with another ref, exempts nothing.
        var relayed = core.EntryMarkers(message).SelectMany(m => m.Ids).ToHashSet(StringComparer.Ordinal);
        var exempt = hits.Where(h => relayed.Contains(h.Id)).Select(h => h.Token).ToHashSet(StringComparer.Ordinal);
        foreach (var h in hits.Where(h => !exempt.Contains(h.Token)))
        {
            var (by, mode) = DeliveryOf(h.BoxDir, h.Entry, h.Id);
            return $"ah: entry {h.Id} carries {MsgPointer.Token(h.Token)} to {h.BoxAddress}; it is delivered by {by} (delivery: {mode}). "
                + "Do not SendMessage it. If ah asks for a relay, it prints the exact message to send.";
        }

        return null;
    }

    public string? SendDenial(string? message, string? sessionId, RequestContext context, out string? warning)
    {
        warning = null;
        if (string.IsNullOrEmpty(message) || !mailbox.CarrierSettingsFor(context).SingleCarrier)
        {
            return null;
        }

        var ids = core.EntryMarkers(message).SelectMany(m => m.Ids).Distinct(StringComparer.Ordinal).ToList();
        try
        {
            var addresses = (string.IsNullOrEmpty(sessionId) ? null : mailbox.ReadRegistration(sessionId))?.Get("addresses") is { IsArray: true } list
                ? list.AsArray().Where(a => a.IsText).Select(a => a.AsString()).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var id in ids)
            {
                if (MsgStatus.Locate(mailbox, id) is { } at && MsgStatus.Denial(MsgStatus.Of(mailbox, at)) is { } denial)
                {
                    return MsgStatus.InScope(mailbox, at, addresses) ? denial : MsgStatus.Generic(id);
                }
            }
        }
        catch (Exception e) when (e is MailboxError or IOException or UnauthorizedAccessException)
        {
            warning = $"ah: could not check entry {string.Join(",", ids)}; sending";
        }

        return null;
    }

    /// <summary>Who delivers an entry and in which mode: its claim's carrier when it has a claim, else the route it was recorded with.</summary>
    private (string By, string Mode) DeliveryOf(string dir, JsonObject entry, string id)
    {
        var carrier = mailbox.ReadClaim(dir, id) is { } claim ? Text(claim, "carrier") : string.Empty;
        if (carrier == "relay")
        {
            return ("a relay", "relay");
        }

        if (carrier == "prompt")
        {
            return ("the plugin prompt", "pull");
        }

        return Text(entry, "route") == "pull"
            ? (Mailbox.PullCarrierName(mailbox.AttemptFor(dir, id) is { } attempt && attempt.Get("adapter") is { IsText: true } adapter ? adapter.AsString() : null), "pull")
            : ("ah", "fallback");
    }

    /// <summary>
    /// Takes the relay claim of every target for <paramref name="session"/>, recording the call that holds them. A call that loses
    /// any of them is denied, and the claims it had already taken in this call are failed (reason <c>partial</c>) at once: nothing
    /// was sent, so they fall back instead of waiting out a lease.
    /// </summary>
    internal RelayClaimDecision TakeClaims(string session, string? toolUseId, IReadOnlyList<RelayTarget> targets, RequestContext context)
    {
        var registration = mailbox.ReadRegistration(session);
        var holder = new JsonObject { ["session_id"] = session };
        if (toolUseId is not null)
        {
            holder["tool_use_id"] = toolUseId;
        }

        if (registration?.Get("pid") is { IsNumber: true } pid)
        {
            holder["pid"] = pid;
            holder["socket"] = SocketOf((long)pid.AsNumber(), context);
        }

        if (registration?.Get("pid_start") is { IsText: true } start)
        {
            holder["pid_start"] = holder.Get("pid") is { IsNumber: true } held
                ? mailbox.StartToRecord((long)held.AsNumber(), start.AsString()) ?? start.AsString()
                : start.AsString();
        }

        var claimed = new List<string>();
        var taken = new List<RelayTarget>();
        try
        {
            foreach (var t in targets)
            {
                var take = new JsonObject { ["session_id"] = session, ["holder"] = holder };
                if (toolUseId is not null)
                {
                    take["tool_use_id"] = toolUseId;
                }

                var result = mailbox.Claim(t.Dir, t.Id, ClaimActions.Take, take);
                if (!result.Won && result.Reason == "denied-call")
                {
                    // A gate has denied this very call: it never runs, so it holds no claim and nothing is shown for it.
                    foreach (var done in taken)
                    {
                        _ = mailbox.Claim(done.Dir, done.Id, ClaimActions.Withdraw, new JsonObject { ["session_id"] = session, ["tool_use_id"] = toolUseId ?? string.Empty, ["reason"] = "denied" });
                    }

                    return new RelayClaimDecision(false, null, []);
                }

                if (!result.Won)
                {
                    FailPartial(taken, session);
                    return new RelayClaimDecision(true, ReasonOf(t, result), claimed);
                }

                claimed.Add(t.Id);
                if (ClaimState(t.Claim) == ClaimStates.Pending)
                {
                    taken.Add(t);
                }
            }
        }
        catch (Exception e) when (e is MailboxError or IOException or UnauthorizedAccessException)
        {
            FailPartial(taken, session);
            return new RelayClaimDecision(true, DeniedUnrecorded(), claimed);
        }

        return new RelayClaimDecision(false, null, claimed);
    }

    private void FailPartial(IReadOnlyList<RelayTarget> taken, string session)
    {
        foreach (var t in taken)
        {
            try
            {
                _ = mailbox.Claim(t.Dir, t.Id, ClaimActions.Fail, new JsonObject { ["session_id"] = session, ["reason"] = "partial" });
            }
            catch (Exception e) when (e is MailboxError or IOException or UnauthorizedAccessException)
            {
                // the claim lease and the sender's Stop hook settle it
            }
        }
    }

    /// <summary>The text for a take that lost the race after the pre-check: what the record is now.</summary>
    private static string ReasonOf(RelayTarget t, ClaimResult result) => result.State switch
    {
        ClaimStates.FallbackClaimed or ClaimStates.Delivered or ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed =>
            $"ah: entry {t.Id} was already delivered to {t.Address}; do not resend it.",
        ClaimStates.RelayFailed => $"ah: the relay of entry {t.Id} failed; ah is delivering it another way; do not resend it.",
        ClaimStates.Released => ReleasedText(t.Id),
        ClaimStates.RelayClaimed when result.Reason == "held-by-self" => $"ah: entry {t.Id} is already being sent by this session; do not send it again.",
        ClaimStates.RelayClaimed => $"ah: entry {t.Id} is already being delivered by another session; do not send it.",
        _ => DeniedNotSession(t.Id),
    };

    public IReadOnlyList<string> ConfirmRelay(string? sessionId, string? recipient, string? message, RelayResult result, string? reason, RequestContext context, string? msgId = null)
    {
        if (string.IsNullOrEmpty(sessionId) || !mailbox.CarrierSettingsFor(context).SingleCarrier)
        {
            return [];
        }

        var moved = new List<string>();
        foreach (var t in RelayTargets(recipient, message, context, out _))
        {
            var failed = result == RelayResult.Failed;
            var args = new JsonObject { ["session_id"] = sessionId };
            if (failed && reason is not null)
            {
                args["reason"] = reason;
            }
            else if (result == RelayResult.Unknown)
            {
                args["reason"] = "assumed";
            }

            if (!failed && !string.IsNullOrEmpty(msgId))
            {
                args["msg_id"] = msgId;
            }

            var step = ClaimWithRetry(t.Dir, t.Id, failed ? ClaimActions.Fail : ClaimActions.Confirm, args);
            if (step is null)
            {
                KeepConfirmNote(t, sessionId, failed, reason, msgId);
            }
            else if (step.Won && ClaimState(t.Claim) == ClaimStates.RelayClaimed)
            {
                moved.Add(t.Id);
            }
        }

        return moved;
    }

    public IReadOnlyList<string> WithdrawRelay(string? sessionId, string? recipient, string? message, string? toolUseId, RequestContext context)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(toolUseId) || !mailbox.CarrierSettingsFor(context).SingleCarrier)
        {
            return [];
        }

        var moved = new List<string>();
        foreach (var t in RelayTargets(recipient, message, context, out _))
        {
            var args = new JsonObject { ["session_id"] = sessionId, ["tool_use_id"] = toolUseId, ["reason"] = "denied" };
            if (ClaimWithRetry(t.Dir, t.Id, ClaimActions.Withdraw, args) is { Won: true })
            {
                moved.Add(t.Id);
            }
        }

        return moved;
    }

    private const int ConfirmTries = 3;

    /// <summary>The claim action, tried up to <see cref="ConfirmTries"/> times; null when every try failed to reach the box.</summary>
    private ClaimResult? ClaimWithRetry(string dir, string id, string action, JsonObject args)
    {
        for (var attempt = 1; attempt <= ConfirmTries; attempt++)
        {
            try
            {
                return mailbox.Claim(dir, id, action, args);
            }
            catch (Exception e) when (e is MailboxError or IOException or UnauthorizedAccessException)
            {
                if (attempt < ConfirmTries)
                {
                    Thread.Sleep(25 * attempt);
                }
            }
        }

        return null;
    }

    /// <summary>The confirm that could not be written into the box, kept outside it for the daemon to apply.</summary>
    private void KeepConfirmNote(RelayTarget t, string sessionId, bool failed, string? reason, string? msgId)
    {
        var note = new JsonObject { ["dir"] = t.Dir, ["id"] = t.Id, ["session_id"] = sessionId, ["result"] = failed ? "failed" : "sent" };
        if (reason is not null)
        {
            note["reason"] = reason;
        }

        if (!failed && !string.IsNullOrEmpty(msgId))
        {
            note["msg_id"] = msgId;
        }

        try
        {
            mailbox.WriteNote(Mailbox.ConfirmNoteKind, t.Id, note);
        }
        catch (Exception e) when (e is MailboxError or IOException or UnauthorizedAccessException)
        {
            // nothing is left to record it with; the entry stays claimed until the sender's Stop hook reads its transcript
        }
    }
}
