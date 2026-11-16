using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>
/// The share of the last hour each activity subject spent working or blocked, kept in memory from the state transitions the timeline recorder
/// sees. A subject is seeded once from the timeline store; after that the ring only grows by observed transitions, so reading it never touches
/// the store. A transition missed between two re-reads is missed here too: the share is an approximation.
/// </summary>
internal sealed class WorkingShare
{
    /// <summary>The length of the window the share covers.</summary>
    public const long WindowMs = 3_600_000;

    private const int MaxTransitions = 512;

    private const long MinuteMs = 60_000;

    private readonly Dictionary<(string Dir, string Subject), List<(long At, string State)>> _rings = [];
    private readonly HashSet<(string Dir, string Subject)> _seeded = [];

    /// <summary>The share of the window spent working or blocked (0 to 1, two decimals), and the span of the window the transitions cover (whole minutes).</summary>
    public readonly record struct Reading(double Share, long WindowMs, IReadOnlyList<(long From, long? To)> Spans, long SeenFromMs);

    /// <summary>Whether <see cref="Seed"/> has run for the subject.</summary>
    public bool IsSeeded(string dir, string subject) => _seeded.Contains((dir, subject));

    /// <summary>Loads the subject's transitions from the store, oldest first. A transition the ring already holds is not added twice.</summary>
    public void Seed(string dir, string subject, IReadOnlyList<TimelineSeed> transitions)
    {
        _ = _seeded.Add((dir, subject));
        foreach (var transition in transitions)
        {
            Observe(dir, subject, transition.State, transition.AtMs);
        }
    }

    /// <summary>Notes that the subject entered <paramref name="state"/> at <paramref name="atMs"/>; the same state as the latest one adds nothing.</summary>
    public void Observe(string dir, string subject, string state, long atMs)
    {
        if (!_rings.TryGetValue((dir, subject), out var ring))
        {
            ring = [];
            _rings[(dir, subject)] = ring;
        }

        if (ring.Count > 0 && ring[^1].State == state)
        {
            return;
        }

        var at = ring.FindLastIndex(t => t.At <= atMs) + 1;
        ring.Insert(at, (atMs, state));
        if (ring.Count > MaxTransitions)
        {
            ring.RemoveRange(0, ring.Count - MaxTransitions);
        }
    }

    /// <summary>When the subject last entered its current state, as far as the ring knows; null when it holds none.</summary>
    public long? LastTransitionAt(string dir, string subject) =>
        _rings.TryGetValue((dir, subject), out var ring) && ring.Count > 0 ? ring[^1].At : null;

    /// <summary>Drops what is held for the subject, so a later seed starts again.</summary>
    public void Forget(string dir, string subject)
    {
        _ = _rings.Remove((dir, subject));
        _ = _seeded.Remove((dir, subject));
    }

    /// <summary>The share for the window ending at <paramref name="nowMs"/>; null when no transition of the subject is known.</summary>
    public Reading? Of(string dir, string subject, long nowMs)
    {
        if (!_rings.TryGetValue((dir, subject), out var ring) || ring.Count == 0)
        {
            return null;
        }

        var start = nowMs - WindowMs;
        var latestBefore = ring.FindLastIndex(t => t.At <= start);
        if (latestBefore > 0)
        {
            ring.RemoveRange(0, latestBefore);
        }

        var spans = new List<(long From, long? To)>();
        for (var i = 0; i < ring.Count; i++)
        {
            long? to = i + 1 < ring.Count ? ring[i + 1].At : null;
            if (IsWorking(ring[i].State) && (to is null || to > start))
            {
                spans.Add((Math.Max(ring[i].At, start), to));
            }
        }

        return Compute(spans, ring[0].At, nowMs);
    }

    /// <summary>The reading at <paramref name="nowMs"/> of the spans a slice carries: the share of the time seen in the window, not of a full hour.</summary>
    private static Reading Compute(IReadOnlyList<(long From, long? To)> spans, long seenFromMs, long nowMs)
    {
        var start = nowMs - WindowMs;
        var coveredFrom = Math.Min(Math.Max(seenFromMs, start), nowMs);
        var covered = nowMs - coveredFrom;
        long working = 0;
        foreach (var (spanFrom, spanTo) in spans)
        {
            var from = Math.Max(spanFrom, start);
            var to = Math.Min(spanTo ?? nowMs, nowMs);
            if (to > from)
            {
                working += to - from;
            }
        }

        var share = covered > 0 ? Math.Round((double)working / covered, 2, MidpointRounding.AwayFromZero) : 0;
        return new Reading(share, covered / MinuteMs * MinuteMs, spans, seenFromMs);
    }

    /// <summary>The body's share as of <paramref name="nowMs"/>, so it decays with no new transition; a body with no spans keeps what it has.</summary>
    public static void Project(JsonObject body, long nowMs)
    {
        if (body.Get("working_spans") is not { IsArray: true } served || body.Get("working_seen_from") is not { IsNumber: true } seenFrom)
        {
            return;
        }

        var spans = new List<(long From, long? To)>();
        foreach (var span in served.AsArray())
        {
            var pair = span.AsArray();
            spans.Add(((long)pair[0].AsNumber(), pair[1].IsNull ? null : (long)pair[1].AsNumber()));
        }

        var reading = Compute(spans, (long)seenFrom.AsNumber(), nowMs);
        body["working_1h"] = reading.Share;
        body["working_window_ms"] = reading.WindowMs;
    }

    private static bool IsWorking(string state) => state is "working" or "blocked";
}
