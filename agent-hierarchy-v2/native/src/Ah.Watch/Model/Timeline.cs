using Ah.Core.Json;

namespace Ah.Watch.Model;

/// <summary>One state transition of a subject: <c>From</c> is null for the first state seen; <c>AtMs</c> is epoch milliseconds.</summary>
internal readonly record struct TimelineRow(long Seq, string DirId, string Subject, string? From, string To, long AtMs);

/// <summary>What a directory's timeline holds in a window: the row count, the sequence numbers of its newest (head) and oldest (tail) rows, and the times of its oldest and newest rows; zeros when it is empty.</summary>
internal readonly record struct TimelineSummary(long Total, long HeadSeq, long TailSeq, long OldestAtMs, long NewestAtMs);

/// <summary>The read side of the timeline.</summary>
internal interface ITimelineReader : IDisposable
{
    /// <summary>Rows of <paramref name="dirId"/> with <c>fromMs &lt;= at &lt;= toMs</c>, ordered by seq (arrival order), after the row <paramref name="afterSeq"/> and before <paramref name="beforeSeq"/> (0 = unbounded), at most <paramref name="limit"/> (200 at most).</summary>
    IReadOnlyList<TimelineRow> Range(string dirId, long fromMs, long toMs, long afterSeq, long beforeSeq, int limit);

    long Count(string dirId, long fromMs, long toMs);

    TimelineSummary Summarize(string dirId, long fromMs, long toMs);
}

/// <summary>One subject's state just before a range: the <c>to</c> state of its newest row older than the range, and when that row was written.</summary>
internal readonly record struct TimelineSeed(string Subject, string State, long AtMs);

/// <summary>
/// Paged reads of a timeline for the <c>/list</c> route, on request threads. They run on read-only connections (a small pool, never the
/// writer's), over a window that is half-open: <c>fromMs &lt;= at &lt; toMs</c>.
/// </summary>
internal interface ITimelinePages
{
    /// <summary>
    /// Up to <paramref name="limit"/> rows of <paramref name="dirId"/> in the window, newest first. With <paramref name="cursorSeq"/> 0 the newest;
    /// otherwise the rows strictly older (<paramref name="older"/>) or strictly newer than that seq, the ones nearest it.
    /// </summary>
    IReadOnlyList<TimelineRow> Page(string dirId, long fromMs, long toMs, long cursorSeq, bool older, int limit);

    /// <summary>The number of rows of <paramref name="dirId"/> in the window.</summary>
    long CountIn(string dirId, long fromMs, long toMs);

    /// <summary>For each subject of <paramref name="dirId"/> that has a row older than <paramref name="beforeMs"/>, its newest such row, by one indexed query per subject.</summary>
    IReadOnlyList<TimelineSeed> Seeds(string dirId, long beforeMs);
}

/// <summary>The derived timeline store. The projector is its only writer; it is never the source of truth.</summary>
internal interface ITimelineStore : ITimelineReader
{
    /// <summary>The <c>to</c> state of the newest live row of (<paramref name="dirId"/>, <paramref name="subject"/>), or null when it has none.</summary>
    string? LastState(string dirId, string subject);

    /// <summary>
    /// The state of (<paramref name="dirId"/>, <paramref name="subject"/>) as of <paramref name="sinceMs"/> followed by every transition at or after it,
    /// oldest first: the newest row older than <paramref name="sinceMs"/> when there is one, then the rows with <c>at &gt;= sinceMs</c>. Two seeks on
    /// the (dir, subject, at) index; nothing is scanned.
    /// </summary>
    IReadOnlyList<TimelineSeed> Recent(string dirId, string subject, long sinceMs);

    /// <summary>Adds rows; a row equal to a stored one in (dir, subject, to, at) is ignored, so a replayed transition adds nothing.</summary>
    void Append(IReadOnlyList<TimelineRow> rows);

    /// <summary>Deletes at most <paramref name="batch"/> rows older than <paramref name="beforeMs"/>, oldest first; returns how many it deleted.</summary>
    int Prune(long beforeMs, int batch);
}

/// <summary>
/// The activity lane with the timeline written alongside: each re-read that changes a subject's state appends one transition row and
/// queues the directory's timeline key. It appends whether or not a client is connected. A store failure is counted and never
/// reaches the projector.
/// </summary>
internal sealed class TimelineRecorder(IModelLane inner, ITimelineStore? store, Action<string> appended, Action<string, string>? transitioned = null, WorkingShare? share = null) : IModelLane
{
    private readonly Dictionary<(string Dir, string Subject), string> _state = [];
    private readonly Dictionary<(string Dir, string Subject), long> _stateAt = [];

    public string Name => inner.Name;

    /// <summary>Store calls that failed.</summary>
    public long Failures { get; private set; }

    public IReadOnlyList<string> SliceIds(string key) => inner.SliceIds(key);

    public IReadOnlyList<string> Keys() => inner.Keys();

    public LaneResult Reread(string key, long nowMs)
    {
        var result = inner.Reread(key, nowMs);
        var (dir, subject) = ActivityLane.Split(key);
        var changes = new List<SliceChange>(result.Changes.Count);
        foreach (var change in result.Changes)
        {
            if (change.Body is null)
            {
                _ = _state.Remove((dir, subject));
                _ = _stateAt.Remove((dir, subject));
                share?.Forget(dir, subject);
                changes.Add(change);
                continue;
            }

            if (JsonText.TryParse(change.Body, out var body) && body.Member("activity") is { IsText: true } state)
            {
                Record(dir, subject, state.AsString(), change.AsOfMs > 0 ? change.AsOfMs : nowMs, nowMs);
                changes.Add(share?.Of(dir, subject, nowMs) is { } working ? change with { Body = WithShare(body, working) } : change);
                continue;
            }

            changes.Add(change);
        }

        return result with { Changes = changes, Record = WithStateAt(result.Record, dir, subject) };
    }

    /// <summary>
    /// The stored activity record with <c>state_at</c>, the time the subject entered its current state, which is the time a summary may show: unlike the
    /// record's own timestamps it moves only when the state does. Unknown (no ring, no transition seen) leaves the record as it was.
    /// </summary>
    private LaneRecord? WithStateAt(LaneRecord? record, string dir, string subject)
    {
        if (record is null || record.IsAbsent || !record.Value.IsMap)
        {
            return record;
        }

        long? at = _stateAt.TryGetValue((dir, subject), out var seen) ? seen : share?.LastTransitionAt(dir, subject);
        if (at is not { } known || known <= 0)
        {
            return record;
        }

        var patched = new JsonObject();
        var map = record.Value.AsObject();
        foreach (var name in map.Keys)
        {
            patched[name] = map[name];
        }

        patched["state_at"] = known;
        return new LaneRecord(JsonValue.FromObject(patched), record.AsOfMs);
    }

    private static byte[] WithShare(JsonValue body, WorkingShare.Reading working)
    {
        var patched = new JsonObject();
        var map = body.AsObject();
        foreach (var name in map.Keys)
        {
            patched[name] = map[name];
        }

        patched["working_1h"] = working.Share;
        patched["working_window_ms"] = working.WindowMs;
        var spans = new JsonArray();
        foreach (var (from, to) in working.Spans)
        {
            var pair = new JsonArray { JsonValue.FromNumber(from), to is { } end ? JsonValue.FromNumber(end) : JsonValue.Null };
            spans.Add(JsonValue.FromArray(pair));
        }

        patched["working_spans"] = JsonValue.FromArray(spans);
        patched["working_seen_from"] = working.SeenFromMs;
        return JsonText.ToUtf8(JsonValue.FromObject(patched), 0);
    }

    private void SeedShare(string dir, string subject, long nowMs)
    {
        if (share is null || store is null || share.IsSeeded(dir, subject))
        {
            return;
        }

        try
        {
            share.Seed(dir, subject, store.Recent(ActivityLane.DirId(dir), subject, nowMs - WorkingShare.WindowMs));
        }
        catch (Exception)
        {
            Failures++;
        }
    }

    private void Record(string dir, string subject, string to, long atMs, long nowMs)
    {
        SeedShare(dir, subject, nowMs);
        var changed = false;
        try
        {
            if (!_state.TryGetValue((dir, subject), out var from))
            {
                from = store?.LastState(ActivityLane.DirId(dir), subject);
            }

            if (from == to)
            {
                _state[(dir, subject)] = to;
                return;
            }

            store?.Append([new TimelineRow(0, ActivityLane.DirId(dir), subject, from, to, atMs)]);
            _state[(dir, subject)] = to;
            _stateAt[(dir, subject)] = atMs;
            share?.Observe(dir, subject, to, atMs);
            if (store is not null)
            {
                appended(dir);
            }

            changed = from is not null;
        }
        catch (Exception)
        {
            Failures++;
        }

        if (changed)
        {
            transitioned?.Invoke(dir, subject);
        }
    }
}

/// <summary>
/// The timeline lane: one key per canonical directory, one slice <c>timeline:&lt;dirId&gt;</c> of <c>{total, head_cursor, tail_cursor,
/// oldest_at, newest_at}</c> over the retention window (head is the newest row's sequence number, tail the oldest's; times are epoch ms).
/// Rows are paged through the store, not carried.
/// </summary>
internal sealed class TimelineLane(ITimelineStore store, Func<IReadOnlyList<string>> canonicalDirs, Func<int> retentionDays, Func<long> clock) : IModelLane
{
    public const string LaneName = "timeline";

    public string Name => LaneName;

    public static string SliceIdOf(string dir) => "timeline:" + ActivityLane.DirId(dir);

    /// <summary>The retention window in whole days, at least 1: the one rule the summary slice and the paged window share.</summary>
    public static int ClampDays(int configured) => Math.Max(configured, 1);

    public IReadOnlyList<string> SliceIds(string key) => [SliceIdOf(key)];

    public IReadOnlyList<string> Keys() => [.. canonicalDirs().Distinct(StringComparer.Ordinal)];

    public LaneResult Reread(string key, long nowMs)
    {
        var id = SliceIdOf(key);
        if (!canonicalDirs().Contains(key, StringComparer.Ordinal))
        {
            return new LaneResult([new SliceChange(id, null)]);
        }

        var now = clock();
        var days = ClampDays(retentionDays());
        var summary = store.Summarize(ActivityLane.DirId(key), now - (days * 86_400_000L), now);
        var body = new JsonObject
        {
            ["retention_days"] = days,
            ["total"] = summary.Total,
            ["head_cursor"] = summary.HeadSeq,
            ["tail_cursor"] = summary.TailSeq,
            ["oldest_at"] = summary.OldestAtMs,
            ["newest_at"] = summary.NewestAtMs,
        };
        return new LaneResult([new SliceChange(id, JsonText.ToUtf8(body, 0), 0)]);
    }
}

/// <summary>Prunes rows older than the retention window, a bounded batch at a time.</summary>
internal sealed class TimelineArchiver(ITimelineStore store, Func<int> retentionDays, Func<long> nowMs)
{
    public const int Batch = 1000;

    private const int MaxBatchesPerRun = 100;

    /// <summary>Archive runs that failed.</summary>
    public long Failures { get; private set; }

    /// <summary>One run: archives batches until none is left or the per-run bound is reached; returns the rows moved.</summary>
    public int Run()
    {
        var moved = 0;
        try
        {
            var days = retentionDays();
            if (days <= 0)
            {
                return 0;
            }

            var cutoff = nowMs() - (days * 86_400_000L);
            for (var batches = 0; batches < MaxBatchesPerRun; batches++)
            {
                var n = store.Prune(cutoff, Batch);
                moved += n;
                if (n < Batch)
                {
                    break;
                }
            }
        }
        catch (Exception)
        {
            Failures++;
        }

        return moved;
    }
}

/// <summary>
/// Runs the archive mover on its own background thread at below-normal priority: once a day, a short while after start, and whenever
/// <see cref="Signal"/> is called (a client arrived). A signal only wakes the thread; it never runs the mover on the caller.
/// </summary>
internal sealed class BackgroundArchiver : IDisposable
{
    private readonly TimelineArchiver _archiver;
    private readonly Action<int> _moved;
    private readonly TimeSpan _period;
    private readonly TimeSpan _firstDelay;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;
    private long _runs;

    public BackgroundArchiver(TimelineArchiver archiver, Action<int> moved, TimeSpan period, TimeSpan firstDelay)
    {
        _archiver = archiver;
        _moved = moved;
        _period = period;
        _firstDelay = firstDelay;
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "timeline-archiver" };
        _thread.Start();
    }

    /// <summary>How many runs finished.</summary>
    public long Runs => Interlocked.Read(ref _runs);

    /// <summary>Asks for a run soon; returns at once.</summary>
    public void Signal()
    {
        try
        {
            if (_wake.CurrentCount == 0)
            {
                _ = _wake.Release();
            }
        }
        catch (ObjectDisposedException)
        {
            // stopped: nothing to wake
        }
    }

    /// <summary>How long <see cref="Dispose"/> waits for the thread; one still running then keeps its semaphore and token, which it may still touch.</summary>
    internal TimeSpan JoinTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public void Dispose()
    {
        _stop.Cancel();
        if (_thread.Join(JoinTimeout))
        {
            _wake.Dispose();
            _stop.Dispose();
        }
    }

    private void Loop()
    {
        var wait = _firstDelay;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                _ = _wake.Wait(wait, _stop.Token);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }

            wait = _period;
            try
            {
                _moved(_archiver.Run());
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                // a failed notification must not end the thread; the archiver counts its own failures
            }
            finally
            {
                _ = Interlocked.Increment(ref _runs);
            }
        }
    }
}
