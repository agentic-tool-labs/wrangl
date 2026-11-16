using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Watch;

/// <summary>One thing that happened in the hierarchy: a member changed state, mail was sent or delivered, a stream opened or ended, a join.</summary>
/// <param name="Kind"><c>state</c>, <c>mail_sent</c>, <c>mail_delivered</c>, <c>stream_open</c>, <c>stream_done</c> or <c>join</c>.</param>
/// <param name="Subject">The view path of the node it is about.</param>
/// <param name="Text">A short sentence for a ticker.</param>
/// <param name="TimeMs">When it happened, in UTC epoch milliseconds.</param>
public sealed record ViewEvent(string Kind, string Subject, string Text, double TimeMs);

/// <summary>What changed between two snapshots: the newest events, oldest first, and how many older ones did not fit.</summary>
/// <param name="Events">At most 200 events, oldest first.</param>
/// <param name="Dropped">How many older events were left out; a client that has not seen them must read the root view again.</param>
public sealed record ViewEventBatch(IReadOnlyList<ViewEvent> Events, int Dropped);

/// <summary>Where the events stand: the last snapshot, the stamp it was read at and when.</summary>
/// <param name="Snapshot">The last snapshot read.</param>
/// <param name="Stamp">What the status revisions, team files and boxes looked like just before it was read.</param>
/// <param name="AtMs">When it was read, in UTC epoch milliseconds.</param>
public sealed record EventCursor(ViewSnapshot Snapshot, string Stamp, double AtMs);

/// <summary>One step of finding events: where they stand now and what was found.</summary>
/// <param name="Cursor">The cursor to pass to the next step.</param>
/// <param name="Batch">The events found; none when nothing was read.</param>
public sealed record EventStep(EventCursor Cursor, ViewEventBatch Batch);

/// <summary>The facts two builds are compared on to find events: members' states, streams, pending joins and box entries.</summary>
public sealed record ViewSnapshot
{
    internal sealed record StreamFact(string Label, bool Done);

    internal sealed record MemberFact(string Label, bool Live, string Activity);

    internal sealed record MailFact(string Box, string Id, string From, string To, string Kind, double CreatedMs, bool Acked);

    internal Dictionary<string, MemberFact> Members { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, StreamFact> Streams { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> Joins { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> Boxes { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> BoxDirs { get; } = new(StringComparer.Ordinal);

    internal HashSet<string> TeamFiles { get; } = new(StringComparer.Ordinal);

    internal Dictionary<string, MailFact> Mail { get; } = new(StringComparer.Ordinal);
}

internal static class ViewEvents
{
    /// <summary>The most events one comparison reports; a larger change reports how many it dropped, which the daemon turns into a gap.</summary>
    internal const int MaxPerDiff = 200;

    private static string Describe(ViewSnapshot.MemberFact f) => f.Live ? f.Activity : "not live";

    /// <summary>
    /// What changed from <paramref name="previous"/> to <paramref name="current"/>. With no previous snapshot nothing is reported:
    /// the first build is the baseline. Mail is reported only for a box the previous snapshot already had, so a member who just
    /// joined does not report its whole inbox as new.
    /// </summary>
    internal static ViewEventBatch Diff(ViewSnapshot? previous, ViewSnapshot current, double nowMs)
    {
        if (previous is null)
        {
            return new ViewEventBatch([], 0);
        }

        var events = new List<ViewEvent>();
        foreach (var (path, now) in current.Members)
        {
            if (!previous.Members.TryGetValue(path, out var before))
            {
                events.Add(new ViewEvent("join", path, $"{now.Label} joined", nowMs));
            }
            else if (before.Live != now.Live || (now.Live && before.Activity != now.Activity))
            {
                events.Add(new ViewEvent("state", path, $"{now.Label}: {Describe(before)} → {Describe(now)}", nowMs));
            }
        }

        foreach (var (path, now) in current.Streams)
        {
            var wasOpen = previous.Streams.TryGetValue(path, out var before) ? !before.Done : (bool?)null;
            if (!now.Done && wasOpen != true)
            {
                events.Add(new ViewEvent("stream_open", path, $"stream {now.Label} opened", nowMs));
            }
            else if (now.Done && wasOpen == true)
            {
                events.Add(new ViewEvent("stream_done", path, $"stream {now.Label} ended", nowMs));
            }
        }

        foreach (var (path, before) in previous.Streams)
        {
            if (!before.Done && !current.Streams.ContainsKey(path))
            {
                events.Add(new ViewEvent("stream_done", path, $"stream {before.Label} ended", nowMs));
            }
        }

        foreach (var id in current.Joins)
        {
            if (!previous.Joins.Contains(id))
            {
                events.Add(new ViewEvent("join", id, $"join {id} is waiting for a decision", nowMs));
            }
        }

        foreach (var (key, now) in current.Mail)
        {
            if (!previous.Boxes.Contains(now.Box))
            {
                continue;
            }

            if (!previous.Mail.TryGetValue(key, out var before))
            {
                var at = double.IsNaN(now.CreatedMs) ? nowMs : now.CreatedMs;
                events.Add(new ViewEvent("mail_sent", now.Box, $"{now.From} → {now.To}: {now.Kind}", at));
            }
            else if (!before.Acked && now.Acked)
            {
                events.Add(new ViewEvent("mail_delivered", now.Box, $"{now.From} → {now.To}: {now.Kind} delivered", nowMs));
            }
        }

        var ordered = events
            .OrderBy(static e => e.TimeMs)
            .ThenBy(static e => e.Kind, StringComparer.Ordinal)
            .ThenBy(static e => e.Subject, StringComparer.Ordinal)
            .ThenBy(static e => e.Text, StringComparer.Ordinal)
            .ToList();
        var dropped = Math.Max(0, ordered.Count - MaxPerDiff);
        return new ViewEventBatch([.. ordered.Skip(dropped)], dropped);
    }

    /// <summary>The stamp of everything a snapshot reads that is cheap to check: status revisions, team files and the previous snapshot's boxes.</summary>
    internal static string Stamp(ViewSnapshot? previous, IViewSource source, IStatusRevs revs, IReadOnlyList<string> statusDirs)
    {
        var parts = new List<string>();
        foreach (var dir in statusDirs.Order(StringComparer.Ordinal))
        {
            parts.Add("s:" + dir + "=" + (revs.Read(dir)?.Rev.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-"));
        }

        if (previous is not null)
        {
            parts.AddRange(previous.TeamFiles.Order(StringComparer.Ordinal).Select(f => "t:" + f + "=" + source.FileStamp(f)));
            parts.AddRange(previous.BoxDirs.Order(StringComparer.Ordinal).Select(b => "b:" + b + "=" + source.BoxStamp(b)));
        }

        return string.Join("\n", parts);
    }

    internal static JsonObject ToJson(ViewEvent e, string id) => new()
    {
        ["id"] = id,
        ["time"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)e.TimeMs)),
        ["kind"] = e.Kind,
        ["subject"] = e.Subject,
        ["text"] = e.Text,
    };
}
