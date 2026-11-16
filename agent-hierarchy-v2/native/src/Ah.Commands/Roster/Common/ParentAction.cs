using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Commands;

/// <summary>
/// What the owner of a lane child's parent team leaves behind when it acts on the child: the address recorded on the rows it
/// writes, and the journal entry written in the same update as the row change.
/// </summary>
internal static class ParentAction
{
    /// <summary>The address the acting owner is recorded as, or null when the caller is not acting as a lane child's parent owner.</summary>
    internal static string? ActorOf(RosterContext c) => c.ParentOwnerLane is { } lane ? HierarchyModule.OrchestratorAddress(lane.Manager) : null;

    /// <summary>The journal entry for one action, or null when the caller is not acting as a lane child's parent owner.</summary>
    internal static JsonObject? Entry(RosterContext c, string verb, string member, string? stream)
    {
        if (ActorOf(c) is not { } by)
        {
            return null;
        }

        var entry = new JsonObject { ["verb"] = verb, ["member"] = member, ["by"] = by, ["at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") };
        if (stream is not null)
        {
            entry["stream"] = stream;
        }

        return entry;
    }

    /// <summary>
    /// Tells the lane's lead, once and after the team file holds <paramref name="entry"/>, what its parent's owner did. The journal is
    /// the record; this is an alert, so an unreachable lead or a missing mailbox costs one stderr line and never the verb.
    /// </summary>
    internal static void Notify(RosterContext c, string dir, JsonObject? entry, string result)
    {
        if (entry is null || c.ParentOwnerLane is not { } lane)
        {
            return;
        }

        var child = c.TeamArg ?? string.Empty;
        var lead = $"{lane.Member}@{lane.Manager}";
        var data = entry.Clone();
        data["team"] = child;
        IReadOnlyList<string> reached = [];
        try
        {
            reached = c.Services.EventMail?.SendEvent(
                c.Cwd,
                dir,
                $"parent-action:{child}:{entry.Get("at")?.AsString()}:{entry.Get("member")?.AsString()}:{entry.Get("verb")?.AsString()}",
                "parent-action",
                NoteText(entry, result),
                data,
                [lead]) ?? [];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // an alert only: the journal on the child already holds the action
        }

        if (!reached.Contains(lead))
        {
            c.Stderr.Write($"note: lane lead {lead} not notified; the action is journalled on {child}\n");
        }
    }

    /// <summary>The line the lead is told: what was done, to whom, by whom, with what result.</summary>
    internal static string NoteText(JsonObject entry, string result) =>
        $"parent action: {entry.Get("verb")?.AsString()} {entry.Get("member")?.AsString()}"
        + (entry.Get("stream") is { IsText: true } s ? $" stream {s.AsString()}" : string.Empty)
        + $" by {entry.Get("by")?.AsString()}; {result}";
}
