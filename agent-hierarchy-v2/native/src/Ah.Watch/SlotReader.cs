using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Watch;

internal static class SlotReader
{
    /// <summary>
    /// The tokens a session holds or waits for, read from the pool directories <c>with-slot.sh</c> keeps: the holder record of each
    /// lease (and of each numbered slot a pre-token script left, which counts as one token) and the waiter record of each queue ticket. A record counts when its pid is alive with the recorded start time and its
    /// nearest registered Claude session ancestor is <paramref name="session"/>. A waiter wins over a holder.
    /// </summary>
    internal static SlotState? StateOf(IReadOnlyList<SlotRecord> records, string session)
    {
        if (session.Length == 0)
        {
            return null;
        }

        SlotState? holding = null;
        foreach (var record in records.Where(r => r.Sessions.Contains(session)))
        {
            if (record.State.Waiting)
            {
                return record.State;
            }

            holding ??= record.State;
        }

        return holding;
    }

    /// <summary>
    /// The default pool as <c>roster doctor</c> reports it, from the same records: capacity, tokens and runs held, waiters, the lease
    /// records whose holder is gone but which no waiter has reaped yet, and whether a waiter has waited longer than <paramref name="waitLimitMs"/>.
    /// </summary>
    internal static BucketStatus Status(IFileReader files, string root, IProcessTable table, double nowMs, double waitLimitMs)
    {
        var live = Read(files, root, table, static _ => [], null).Where(static r => r.State.Pool == "test").ToList();
        var holders = live.Where(static r => !r.State.Waiting).ToList();
        var recorded = 0;
        foreach (var dir in new[] { root, Path.Combine(root, "held") })
        {
            if (files.Exists(dir))
            {
                recorded += files.List(dir).Select(Path.GetFileName).Count(n => n is { Length: > 0 } && n[0] != '.'
                    && (dir != root || n.All(char.IsAsciiDigit)) && files.Exists(Path.Combine(dir, n, "holder")));
            }
        }

        var waiters = live.Where(static r => r.State.Waiting).ToList();
        return new BucketStatus(
            live.Count > 0 ? live[0].State.Capacity : CapacityOf(files, root),
            holders.Sum(static r => r.State.Tokens),
            holders.Count,
            waiters.Count,
            Math.Max(0, recorded - holders.Count),
            waiters.Any(r => nowMs - r.State.SinceMs > waitLimitMs));
    }

    /// <summary>The pool's capacity in tokens as <c>with-slot.sh</c> last recorded it, or 0 when the pool has none.</summary>
    internal static int CapacityOf(IFileReader files, string dir)
    {
        var file = Path.Combine(dir, "capacity");
        return files.Exists(file) && int.TryParse(files.ReadText(file).Trim(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    /// <summary>Every live holder and waiter record under <paramref name="root"/>, with the sessions its process runs under, in pool order.</summary>
    internal static IReadOnlyList<SlotRecord> Read(
        IFileReader files, string root, IProcessTable table, Func<int, IReadOnlyList<string>?> sessionsOf, Action<string>? log)
    {
        var found = new List<SlotRecord>();
        try
        {
            if (!files.Exists(root))
            {
                return found;
            }

            var pools = new List<(string Dir, string Name)> { (root, "test") };
            pools.AddRange(files.List(root).Select(Path.GetFileName).Where(static n => n is not null && n.StartsWith("pool-", StringComparison.Ordinal))
                .Select(n => (Path.Combine(root, n!), n![5..])));
            foreach (var (dir, pool) in pools)
            {
                var records = new List<(string File, bool Waiting, string Ticket)>();
                records.AddRange(files.List(dir).Select(Path.GetFileName).Where(static n => n is { Length: > 0 } && n.All(char.IsAsciiDigit))
                    .Select(n => (Path.Combine(dir, n!, "holder"), false, n!)));
                var leases = Path.Combine(dir, "held");
                if (files.Exists(leases))
                {
                    records.AddRange(files.List(leases).Select(Path.GetFileName).Where(static n => n is { Length: > 0 } && n[0] != '.')
                        .Select(n => (Path.Combine(leases, n!, "holder"), false, n!)));
                }

                var queue = Path.Combine(dir, "q");
                if (files.Exists(queue))
                {
                    records.AddRange(files.List(queue).Select(Path.GetFileName).Where(static n => n is { Length: > 0 } && n[0] != '.')
                        .Select(n => (Path.Combine(queue, n!, "waiter"), true, n!)));
                }

                var parsed = new List<(SlotRecord Record, string Ticket)>();
                foreach (var (file, waiting, ticket) in records)
                {
                    if (!files.Exists(file) || JsonText.Parse(files.ReadText(file)) is not { IsMap: true } record
                        || record.Member("pid") is not { IsNumber: true } pid || record.Member("pid_start") is not { IsText: true } start
                        || record.Member("since") is not { IsNumber: true } since
                        || table.StartTime((long)pid.AsNumber()) != start.AsString()
                        || sessionsOf((int)pid.AsNumber()) is not { } sessions)
                    {
                        continue;
                    }

                    var sinceMs = since.AsNumber() * 1000;
                    var waited = record.Member("waited") is { IsNumber: true } w ? w.AsNumber() * 1000 : 0;
                    var label = record.Member("label") is { IsText: true } l ? l.AsString() : string.Empty;
                    var tokens = record.Member("tokens") is { IsNumber: true } t ? (int)Math.Max(1, t.AsNumber()) : 1;
                    parsed.Add((new SlotRecord(new SlotState(pool, waiting, sinceMs, waited, tokens), sessions, (int)pid.AsNumber(), label, start.AsString()), ticket));
                }

                var capacity = CapacityOf(files, dir);
                var free = capacity > 0 ? Math.Max(0, capacity - parsed.Where(static p => !p.Record.State.Waiting).Sum(static p => p.Record.State.Tokens)) : 0;
                var queued = parsed.Where(static p => p.Record.State.Waiting).Select(static p => p.Ticket).Order(StringComparer.Ordinal).ToList();
                foreach (var (entry, ticket) in parsed)
                {
                    var ahead = entry.State.Waiting ? queued.IndexOf(ticket) : 0;
                    found.Add(entry with { State = entry.State with { Free = free, Capacity = capacity, Ahead = ahead } });
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke($"slot records unreadable: {e.Message}");
        }

        return found;
    }
}

/// <summary>The default test pool at a glance; see <see cref="WatchModule.BucketStatusOf"/>.</summary>
/// <param name="Capacity">Tokens in the bucket; 0 when none is recorded.</param>
/// <param name="HeldTokens">Tokens the live leases hold.</param>
/// <param name="Runs">Live leases.</param>
/// <param name="Waiting">Live queue tickets.</param>
/// <param name="DeadLeases">Lease records whose holder is gone and that are not yet reaped.</param>
/// <param name="OldWaiter">Whether a live waiter has waited past the limit.</param>
public sealed record BucketStatus(int Capacity, int HeldTokens, int Runs, int Waiting, int DeadLeases, bool OldWaiter);

/// <summary>One live slot record: the slot it is, the sessions its process runs under, and the process (pid and the label the script wrote) it belongs to.</summary>
public sealed record SlotRecord(SlotState State, IReadOnlyList<string> Sessions, int Pid = 0, string Label = "", string PidStart = "");
