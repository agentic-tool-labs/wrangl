using System.Globalization;
using System.Text.RegularExpressions;
using Ah.Core.Json;
using Ah.Core.Text;

namespace Ah.Mesh;

internal sealed partial class MailboxHooks
{
    private const string WrapperClose = "</cross-session-message>";

    private const string WrapperOpen = "<cross-session-message";

    [GeneratedRegex("\\G<cross-session-message(?![A-Za-z0-9_-])(?<attrs>[^>]*)>", RegexOptions.CultureInvariant)]
    private static partial Regex WrapperTagRe();

    [GeneratedRegex("(?<![A-Za-z0-9_-])from=\"(?<v>[^\"]*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex FromAttributeRe();

    [GeneratedRegex("\\Auds:.*/(?<pid>[0-9]+)\\.sock\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SocketPidRe();

    /// <summary>
    /// One cross-session wrapper the host rendered: the <c>from</c> attribute of its opener, the process id its socket names, and
    /// the body up to the wrapper's own closing tag. <c>from-name</c> (a session label) is never read.
    /// </summary>
    private sealed record WrapperBlock(string From, long? Pid, string Body, string? MsgId = null)
    {
        /// <summary>
        /// The text a marker in this wrapper is judged by: the body before any second opener, since the host only writes one opener
        /// per message and anything after another is the sender's own text pretending to be a wrapper.
        /// </summary>
        internal string Head
        {
            get
            {
                var nested = Body.IndexOf(WrapperOpen, StringComparison.Ordinal);
                return nested < 0 ? Body : Body[..nested];
            }
        }
    }

    /// <summary>
    /// The wrappers a prompt consists of: one that opens it, then any that follow a closing tag with only white space between. A
    /// wrapper quoted further into a prompt, or after other text, is not one.
    /// </summary>
    private static List<WrapperBlock> WrapperBlocks(string? prompt)
    {
        var text = prompt ?? string.Empty;
        var blocks = new List<WrapperBlock>();
        var at = 0;
        while (true)
        {
            while (at < text.Length && char.IsWhiteSpace(text[at]))
            {
                at++;
            }

            var tag = WrapperTagRe().Match(text, at);
            if (!tag.Success || FromAttributeRe().Match(tag.Groups["attrs"].Value) is not { Success: true } from)
            {
                break;
            }

            var bodyStart = tag.Index + tag.Length;
            var close = text.IndexOf(WrapperClose, bodyStart, StringComparison.Ordinal);
            var value = from.Groups["v"].Value;
            var socket = SocketPidRe().Match(value);
            long? pid = socket.Success && long.TryParse(socket.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            blocks.Add(new WrapperBlock(value, pid, close < 0 ? text[bodyStart..] : text[bodyStart..close]));
            if (close < 0)
            {
                break;
            }

            at = close + WrapperClose.Length;
        }

        return blocks;
    }

    /// <summary>
    /// The ids of <paramref name="ids"/> this prompt may ack in <paramref name="box"/>. An entry with no claim record follows the
    /// old rules. For one with a claim record, each marker is judged against the wrapper that carries it: a relay is acked only by
    /// a wrapper naming the process the claim records, and an id no wrapper names is acked only when the daemon holds the
    /// entry's fallback claim (the plugin prompt).
    /// </summary>
    private List<string> Receivable(BoxRef box, IReadOnlyList<string> ids, IReadOnlyList<WrapperBlock> blocks)
    {
        var kept = new List<string>(ids.Count);
        foreach (var id in ids)
        {
            if (ClaimMachine.Live(mailbox.ReadClaim(box.Dir, id)) is not { } claim)
            {
                kept.Add(id);
                continue;
            }

            var carriers = blocks.Where(b => NamesEntry(b, box, id)).ToList();
            if (carriers.Count == 0 ? AckAllowed(box, id, claim, null) : carriers.Any(b => AckAllowed(box, id, claim, b)))
            {
                kept.Add(id);
            }
        }

        return kept;
    }

    private bool NamesEntry(WrapperBlock block, BoxRef box, string id) =>
        core.EntryMarkers(block.Head).Any(m => m.Ids.Contains(id) && (string.IsNullOrEmpty(m.Address) || m.Address == box.Address));

    private bool AckAllowed(BoxRef box, string id, JsonObject claim, WrapperBlock? opener)
    {
        var state = ClaimState(claim);
        if (opener is null)
        {
            // The prompt hook is the prompt carrier, so its delivery proves a pending prompt-carrier entry; with no daemon nothing else advances it.
            return state == ClaimStates.FallbackClaimed
                || (state == ClaimStates.Pending && Text(claim, "carrier") == "prompt")
                || (state == ClaimStates.Delivered && Text(claim, "carrier") != "relay");
        }

        if (Text(claim, "carrier") != "relay")
        {
            return false;
        }

        if (Text(claim, "msg_id") is { Length: > 0 } sent && opener.MsgId is { Length: > 0 } given)
        {
            if (given != sent)
            {
                return false;
            }

            if (state is ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed or ClaimStates.Delivered)
            {
                return true;
            }
        }

        if (opener.Pid is not { } named)
        {
            return false;
        }

        if (state == ClaimStates.Pending)
        {
            return IsRecordedProcess(named, claim.Get("sender_pid"), claim.Get("sender_pid_start"));
        }

        if (state is not (ClaimStates.RelayClaimed or ClaimStates.RelaySent or ClaimStates.RelayUnconfirmed or ClaimStates.Delivered)
            || claim.Get("holder") is not { IsMap: true } held)
        {
            return false;
        }

        return IsRecordedProcess(named, held.AsObject().Get("pid"), held.AsObject().Get("pid_start"));
    }

    /// <summary>
    /// Whether <paramref name="named"/> is the process whose pid and start time were recorded while it was alive. A pid that has
    /// since exited can only have queued the message before it went, so it matches; a live process must still have the recorded
    /// start time, which a process that reused the pid does not.
    /// </summary>
    private bool IsRecordedProcess(long named, JsonValue? recordedPid, JsonValue? recordedStart)
    {
        if (recordedPid is not { IsNumber: true } pid || (long)pid.AsNumber() != named)
        {
            return false;
        }

        if (!mailbox.PidAlive(pid))
        {
            return true;
        }

        return recordedStart is { IsText: true } start
            && Ah.Core.Time.ProcessStart.IsSame(mailbox.ProcessStartMatches(named, start.AsString()));
    }

    [GeneratedRegex("\\A[" + JsText.SpaceClass + "]*Another Claude session sent a message[^<]*", RegexOptions.CultureInvariant)]
    private static partial Regex HostPrefixRe();

    private const string ScanNoteKind = "transcript-scan";

    public IReadOnlyList<string> AckFromTranscript(string? sessionId, string? transcriptPath)
    {
        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(transcriptPath) || !Mailbox.IsName(sessionId))
        {
            return [];
        }

        var boxes = mailbox.RegistrationBoxes(RegistrationValue(sessionId));
        if (boxes.Count == 0)
        {
            return [];
        }

        var recorded = mailbox.ReadNote(ScanNoteKind, sessionId);
        var offset = recorded is not null && Text(recorded, "path") == transcriptPath && recorded.Get("offset") is { IsNumber: true } o ? (long)o.AsNumber() : 0;
        if (mailbox.ReadLinesFrom(transcriptPath, offset) is not { } read)
        {
            return [];
        }

        var acked = new List<string>();
        foreach (var line in read.Text.Split('\n'))
        {
            var blocks = RelayBlocksOf(line);
            if (blocks.Count == 0)
            {
                continue;
            }

            foreach (var box in boxes)
            {
                var ids = new List<string>();
                foreach (var block in blocks)
                {
                    foreach (var mark in core.EntryMarkers(block.Head))
                    {
                        if (!string.IsNullOrEmpty(mark.Address) && mark.Address != box.Address)
                        {
                            continue;
                        }

                        ids.AddRange(mark.Ids.Where(id => ClaimMachine.Live(mailbox.ReadClaim(box.Dir, id)) is not null && !mailbox.IsAcked(box.Dir, id)
                            && Receivable(box, [id], [block]).Count == 1));
                    }
                }

                if (ids.Count == 0)
                {
                    continue;
                }

                var r = Ack(box.Dir, ids.Distinct(StringComparer.Ordinal).ToList(), "stop-hook", sessionId);
                acked.AddRange(r.Acked);
                Delivered(box.Dir, r.Acked);
            }

            foreach (var (tokenBox, tokenIds) in TokenClaims(boxes, blocks))
            {
                var r = Ack(tokenBox.Dir, tokenIds, "stop-hook", sessionId);
                acked.AddRange(r.Acked);
                Delivered(tokenBox.Dir, r.Acked);
            }
        }

        if (read.End != offset || recorded is null)
        {
            try
            {
                mailbox.WriteNote(ScanNoteKind, sessionId, new JsonObject { ["path"] = transcriptPath, ["offset"] = (double)read.End });
            }
            catch (Exception e) when (e is MailboxError or IOException or UnauthorizedAccessException)
            {
                // the acks stand; the next scan reads these rows again and acks nothing new
            }
        }

        return acked;
    }

    /// <summary>
    /// The wrappers of one transcript row, in either shape the host writes a delivered cross-session message in: a user row marked as
    /// meta whose text opens with the host's one-line prefix and a wrapper (a message that arrived while the session was idle), or an
    /// attachment row of type <c>queued_command</c> whose <c>prompt</c> is the wrapper itself (a message absorbed while a turn was
    /// running). For the second the sender is the process the host verified on the socket, not the one the wrapper text names; a
    /// row whose origin is not a peer's is no relay.
    /// </summary>
    private static List<WrapperBlock> RelayBlocksOf(string line)
    {
        if (JsText.Trim(line).Length == 0 || !line.Contains(WrapperOpen, StringComparison.Ordinal))
        {
            return [];
        }

        JsonValue row;
        try
        {
            row = JsonText.Parse(line);
        }
        catch (JsonParseException)
        {
            return [];
        }

        if (row.Member("type") is not { IsText: true } type)
        {
            return [];
        }

        if (type.AsString() == "attachment")
        {
            return QueuedCommandBlocks(row.Member("attachment"));
        }

        if (type.AsString() != "user"
            || row.Member("isMeta") is not { IsBool: true } meta || !meta.AsBool()
            || row.Member("message")?.Member("content") is not { IsText: true } content)
        {
            return [];
        }

        var text = content.AsString();
        var prefix = HostPrefixRe().Match(text);
        return prefix.Success ? WrapperBlocks(text[prefix.Length..]) : [];
    }

    private static List<WrapperBlock> QueuedCommandBlocks(JsonValue? attachment)
    {
        if (attachment is not { IsMap: true } a || a.Member("type") is not { IsText: true } kind || kind.AsString() != "queued_command"
            || a.Member("prompt") is not { IsText: true } prompt)
        {
            return [];
        }

        var origin = a.Member("origin");
        if (origin is { IsMap: true } o && o.Member("kind") is { IsText: true } originKind && originKind.AsString() != "peer")
        {
            return [];
        }

        var blocks = WrapperBlocks(prompt.AsString());
        if (origin is not { IsMap: true } peer)
        {
            return blocks;
        }

        var msgId = peer.Member("msg_id") is { IsText: true } m && m.AsString().Length > 0 ? m.AsString() : null;
        long? verifiedPid = peer.Member("verifiedPeerPid") is { IsNumber: true } verified ? (long)verified.AsNumber() : null;
        return [.. blocks.Select(b => b with { Pid = verifiedPid ?? b.Pid, MsgId = msgId })];
    }

    /// <summary>The unacked entries of the receiver's boxes that a verified wrapper's request token names and that have a claim record.</summary>
    private IEnumerable<(BoxRef Box, List<string> Ids)> TokenClaims(IReadOnlyList<BoxRef> boxes, IReadOnlyList<WrapperBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (hierarchy.Core.ExtractMsgToken(block.Head) is not { Length: > 0 } token)
            {
                continue;
            }

            foreach (var box in boxes)
            {
                var ids = new List<string>();
                foreach (var e in mailbox.ReadEntries(box.Dir).Entries.Where(e => e.IsMap).Select(e => e.AsObject()))
                {
                    var id = Text(e, "id");
                    if (Text(e, "ref") == token && !mailbox.IsAcked(box.Dir, id) && mailbox.ReadClaim(box.Dir, id) is { } claim && AckAllowed(box, id, claim, block))
                    {
                        ids.Add(id);
                    }
                }

                if (ids.Count > 0)
                {
                    yield return (box, ids);
                }
            }
        }
    }

    /// <summary>Moves the claim of each acked entry to <c>delivered</c> when its state allows; an entry with no claim is not touched.</summary>
    private void Delivered(string dir, IReadOnlyList<string> ids)
    {
        foreach (var id in ids)
        {
            if (ClaimMachine.Live(mailbox.ReadClaim(dir, id)) is null)
            {
                continue;
            }

            try
            {
                _ = mailbox.Claim(dir, id, ClaimActions.Deliver, new JsonObject { ["actor"] = "receiver" });
            }
            catch (Exception e) when (e is MailboxError or IOException)
            {
                // the ack stands; the claim only records it
            }
        }
    }
}
