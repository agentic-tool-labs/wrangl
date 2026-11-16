using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Hooks.Plumbing;

/// <summary>
/// One event mail the daemon fired for an orchestrator box: where it lives, which entry, the text to deliver, the event's name
/// (null when the entry has none) and the request it is about (null when it is about none).
/// </summary>
internal sealed record DueEventMail(string Address, string Dir, string Id, string Text, string? Event = null, string? RequestId = null);

/// <summary>
/// Event mail the daemon wrote for a session and nobody acked within the pull timeout: a Stop block is its delivery, and the
/// ack written after the block is what keeps the adapter from delivering it again.
/// </summary>
internal static class EventMail
{
    /// <summary>The unacked event entries from the daemon, addressed to this session's orchestrator boxes, older than the box's pull timeout.</summary>
    public static List<DueEventMail> Due(AdvisoryServices advisory, string sessionId, double nowMs)
    {
        var due = new List<DueEventMail>();
        var registration = advisory.Mailbox.ReadRegistration(sessionId);
        foreach (var box in advisory.Mailbox.RegistrationBoxes(registration is null ? JsonValue.Null : registration))
        {
            if (!box.Address.StartsWith("orchestrator@", StringComparison.Ordinal))
            {
                continue;
            }

            var heartbeat = advisory.Mailbox.ReadHeartbeat(box.Dir, sessionId) ?? advisory.Mailbox.ReadHeartbeat(box.Dir);
            var limit = advisory.MailCore.PullTimeoutMs(heartbeat is null ? JsonValue.Null : heartbeat);
            foreach (var entry in advisory.Mailbox.ReadEntries(box.Dir).Entries)
            {
                if (entry.Member("kind") is not { IsText: true } kind || kind.AsString() != "event"
                    || entry.Member("from") is not { IsText: true } from || from.AsString() != "ahd"
                    || entry.Member("text") is not { IsText: true } text || text.AsString().Length == 0
                    || entry.Member("id") is not { IsText: true } id || advisory.Mailbox.IsAcked(box.Dir, id.AsString()))
                {
                    continue;
                }

                if (entry.Member("to") is not { IsText: true } to || to.AsString() != box.Address
                    || (entry.Member("to_session") is { IsTruthy: true } toSession && !(toSession.IsText && toSession.AsString() == sessionId)))
                {
                    continue;
                }

                if (entry.Member("created") is { IsText: true } created && Timestamps.TryParseMs(created.AsString(), out var createdMs) && nowMs - createdMs > limit)
                {
                    if (!advisory.MailboxHooks.EventApplies(sessionId, entry))
                    {
                        advisory.MailboxHooks.ResolveEvent(box.Dir, id.AsString(), "stop-hook", sessionId);
                        continue;
                    }

                    var name = entry.Member("event") is { IsText: true } ev ? ev.AsString() : null;
                    var request = entry.Member("data")?.Member("request_id") is { IsText: true } rid ? rid.AsString() : null;
                    due.Add(new DueEventMail(box.Address, box.Dir, id.AsString(), text.AsString(), name, request));
                }
            }
        }

        return due;
    }

    /// <summary>
    /// Acks <paramref name="items"/> (from <see cref="Due"/>) by <paramref name="by"/>, one call per box directory, and returns the ids this
    /// call acked first. An id someone else acked in the meantime was delivered by that carrier and is not in the result.
    /// </summary>
    public static HashSet<string> Ack(AdvisoryServices advisory, IReadOnlyList<DueEventMail> items, string by, string sessionId)
    {
        var first = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in items.GroupBy(i => i.Dir))
        {
            first.UnionWith(advisory.Callers.AckRouted(group.Key, [.. group.Select(i => i.Id)], by, sessionId).Fresh);
        }

        return first;
    }
}
