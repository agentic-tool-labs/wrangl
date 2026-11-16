using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;

namespace Ah.Mesh;

/// <summary>The sentence that tells a session to relay a mail entry; the daemon's prompt and the Stop hook print this one text.</summary>
internal static class RelayInstruction
{
    /// <summary>
    /// The instruction to send <paramref name="line"/> to <paramref name="to"/> so entry <paramref name="entry"/> of <paramref name="address"/> is picked up.
    /// With <paramref name="facts"/> that name something, a fact line and the way to decline come before the instruction.
    /// </summary>
    public static string Text(string entry, string address, string to, string line, JsonObject? facts = null)
    {
        var fact = FactLine(facts, address);
        var decline = fact.Length == 0 ? string.Empty : $"{fact} Relay it, or decline: ah msg decline {entry} --reason <already-reported|superseded|withdrawn>. ";
        return $"ah: mail entry {entry} for {address} was not picked up. {decline}You owe one SendMessage to {to}, with notify_when_idle: true. Send it exactly as printed: {line}";
    }

    /// <summary>The one line shown instead of the instruction when entry <paramref name="entry"/> needs no relay: what ended the need, never something to send.</summary>
    public static string VoidLine(string entry, string address, JsonObject marker) => RelayVoid.Kind(marker) switch
    {
        "acked" => $"ah: relay of {entry} no longer needed: {address} picked it up at {RelayVoid.Field(marker, "at")} ({RelayVoid.Field(marker, "by")})",
        "superseded" => $"ah: relay of {entry} no longer needed: superseded by {RelayVoid.Field(marker, "by")}",
        "withdrawn" => $"ah: relay of {entry} no longer needed: withdrawn by {RelayVoid.Field(marker, "by")} ({RelayVoid.Field(marker, "reason")})",
        "declined" => $"ah: relay of {entry} no longer needed: declined by {RelayVoid.Field(marker, "by")} ({RelayVoid.Field(marker, "reason")})",
        "claimed" => $"ah: relay of {entry} no longer needed: its relay already holds it (claim {RelayVoid.Field(marker, "state")})",
        "released" => $"ah: relay of {entry} no longer needed: released to the delivery ladder (no poller): it pastes it or escalates",
        _ => $"ah: relay of {entry} no longer needed: the delivery ladder recorded it delivered",
    };

    /// <summary><paramref name="line"/> as the carrier of a relay request sends it: led by what tells the receiver to ignore a repeat of an entry it already has.</summary>
    public static string Relayed(string entry, string created, string line) =>
        $"{RelayedHead}{entry}{RelayedSent}{created}{RelayedTail}{line}";

    private const string RelayedHead = "(relayed: entry ";
    private const string RelayedSent = ", sent ";
    private const string RelayedTail = "; if you have already done this, reply \"already done\" and do not repeat it) ";

    /// <summary>
    /// <paramref name="line"/> without the <see cref="Relayed"/> prefix when it starts with one whose entry id is the id of the line's own trailing
    /// entry marker; any other line comes back unchanged.
    /// </summary>
    public static string WithoutRelayedPrefix(string line)
    {
        if (!line.StartsWith(RelayedHead, StringComparison.Ordinal))
        {
            return line;
        }

        var sent = line.IndexOf(RelayedSent, RelayedHead.Length, StringComparison.Ordinal);
        var tail = sent < 0 ? -1 : line.IndexOf(RelayedTail, sent + RelayedSent.Length, StringComparison.Ordinal);
        if (tail < 0)
        {
            return line;
        }

        var rest = line[(tail + RelayedTail.Length)..];
        var id = line[RelayedHead.Length..sent];
        return EntryMarkerText.ParseTrailing(rest) is { } mark && mark.Ids.Count > 0 && mark.Ids[0] == id ? rest : line;
    }

    private static string FactLine(JsonObject? facts, string address)
    {
        if (facts is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (facts.Get("reported_since") is { IsMap: true } reported && reported.Member("entry") is { IsText: true } entry
            && reported.Member("created") is { IsText: true } at)
        {
            var request = reported.Member("request") is { IsText: true } r ? $" on request {r.AsString()}" : string.Empty;
            var same = reported.Member("same_request") is { IsBool: true } b && b.AsBool() ? ", the request this entry names" : string.Empty;
            parts.Add($"{address} has reported{request} since (entry {entry.AsString()} at {at.AsString()}){same}.");
        }

        if (facts.Get("newer_from_sender") is { IsArray: true } newer && newer.AsArray().FirstOrDefault(static n => n.IsMap) is { IsMap: true } first
            && first.Member("id") is { IsText: true } id)
        {
            var replaces = first.Member("supersedes") is { IsBool: true } s && s.AsBool();
            parts.Add($"a newer entry {id.AsString()} from the same sender {(replaces ? "supersedes this one" : "is unread")}.");
        }

        return string.Join(' ', parts);
    }
}

/// <summary>
/// The carrier's side of a rung-3 relay request. When a session receives the <c>relay-needed</c> event <c>ahd</c> mailed it (acked by
/// the prompt hook or an adapter, which run in the client), the session owes one SendMessage for the original entry: this writes that
/// obligation as a relay row in the peer records, the same row <c>ah msg send</c> writes for a sender that carries its own relay.
/// </summary>
public interface IRelayObligations
{
    /// <summary>
    /// <paramref name="ids"/> were just acked by <paramref name="by"/> in the boxes <paramref name="boxDirs"/> of session
    /// <paramref name="sessionId"/>: writes the obligation for each that is a well-formed <c>relay-needed</c> request from <c>ahd</c>
    /// addressed to this session. Returns how many rows were written.
    /// </summary>
    int ObligationsFor(string? sessionId, IReadOnlyList<string> boxDirs, IReadOnlyList<string> ids, string by);
}

internal sealed class RelayObligations(Mailbox mailbox, HierarchyServices hierarchy) : IRelayObligations
{
    private static readonly string[] Received = ["prompt-hook", "adapter", "watcher", "stop-hook"];

    private static readonly string[] Fields = ["entry", "address", "dir", "to", "line"];

    private static bool Text(JsonValue? v) => v is { IsText: true } t && t.AsString().Length > 0;

    public int ObligationsFor(string? sessionId, IReadOnlyList<string> boxDirs, IReadOnlyList<string> ids, string by)
    {
        if (string.IsNullOrEmpty(sessionId) || !Received.Contains(by, StringComparer.Ordinal) || ids.Count == 0)
        {
            return 0;
        }

        var written = 0;
        foreach (var dir in boxDirs)
        {
            var entries = mailbox.ReadEntries(dir).Entries.Select(e => e.AsObject()).ToList();
            foreach (var id in ids)
            {
                var e = entries.FirstOrDefault(x => x.Get("id") is { IsText: true } i && i.AsString() == id);
                if (e is null
                    || !Is(e.Get("from"), "ahd")
                    || !Is(e.Get("kind"), "event")
                    || !Is(e.Get("event"), "relay-needed"))
                {
                    continue;
                }

                if (e.Get("data") is not { IsMap: true } data || data.AsObject().Get("relay") is not { IsMap: true } relay)
                {
                    continue;
                }

                var r = relay.AsObject();
                if (!Fields.All(k => Text(r.Get(k))) || !NodePath.IsAbsolute(r.Get("dir")!.Value.AsString())
                    || r.Get("entry")!.Value.AsString() != DataEntry(data.AsObject()))
                {
                    continue;
                }

                if (e.Get("to_session") is { IsTruthy: true } toSession && !(toSession.IsText && toSession.AsString() == sessionId))
                {
                    continue;
                }

                var task = $"relay:{DataEntry(data.AsObject())}";
                using var decisions = hierarchy.Core.DecisionReads();
                if (hierarchy.Core.SessionRows(sessionId).Any(x => x.IsMap
                        && x.AsObject().Get("session_id") is { IsText: true } s && s.AsString() == sessionId
                        && x.AsObject().Get("task") is { IsText: true } t && t.AsString() == task))
                {
                    continue;
                }

                var voided = mailbox.RelayVoidNote(r.Get("dir")!.Value.AsString(), r.Get("entry")!.Value.AsString());
                if (hierarchy.Peers.AppendRelayRecord(sessionId, relay, voidNote: voided))
                {
                    written++;
                }
            }
        }

        return written;
    }

    private static bool Is(JsonValue? value, string text) => value is { IsText: true } t && t.AsString() == text;

    private static string DataEntry(JsonObject data) => data.Get("entry") is { IsText: true } e ? e.AsString() : string.Empty;
}
