using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Ah.State;

/// <summary>
/// Every write op this process's engine ran, by <c>domain.op</c>: count and total wall time since the process started. The daemon logs
/// what changed since its own last line once a minute, beside its route line, so a write made in process (no route) is named too. The
/// totals only grow, so several readers (a test's daemons) never take counts from each other. Only registered pairs are keys.
/// </summary>
internal static class StateOpLedger
{
    private static readonly ConcurrentDictionary<string, long[]> Stats = new(StringComparer.Ordinal);

    internal static void Add(string key, long ticks)
    {
        var stat = Stats.GetOrAdd(key, static _ => new long[2]);
        Interlocked.Increment(ref stat[0]);
        Interlocked.Add(ref stat[1], ticks);
    }

    /// <summary>How many times <paramref name="key"/> (<c>domain.op</c>) has run in this process.</summary>
    public static long Count(string key) => Stats.TryGetValue(key, out var stat) ? Interlocked.Read(ref stat[0]) : 0;

    /// <summary>The ops run since <paramref name="seen"/> was last updated, as one line, or null when none ran; <paramref name="seen"/> then holds the current totals.</summary>
    public static string? Take(Dictionary<string, (long Runs, long Ticks)> seen)
    {
        ArgumentNullException.ThrowIfNull(seen);
        var parts = new List<string>();
        foreach (var (key, stat) in Stats.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            var runs = Interlocked.Read(ref stat[0]);
            var ticks = Interlocked.Read(ref stat[1]);
            var (seenRuns, seenTicks) = seen.TryGetValue(key, out var was) ? was : (0, 0);
            seen[key] = (runs, ticks);
            if (runs > seenRuns)
            {
                parts.Add(string.Create(CultureInfo.InvariantCulture, $"{key} n={runs - seenRuns} total={(ticks - seenTicks) * 1000.0 / Stopwatch.Frequency:F0}ms"));
            }
        }

        return parts.Count == 0 ? null : "state ops last 60s: " + string.Join("; ", parts);
    }
}
