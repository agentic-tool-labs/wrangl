using System.Text;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>The timeline pieces over an in-memory store: the recorder's transitions, the count slice, and the archive mover.</summary>
public sealed class TimelineTests
{
    private const string Dir = "/h/.claude/ah2";

    /// <summary>A store with the same contract as the file-backed one: ordered by (at, seq), idempotent on (dir, subject, to, at), month archives.</summary>
    internal sealed class MemoryStore : ITimelineStore
    {
        private readonly object _gate = new();
        private readonly List<TimelineRow> _live = [];
        private long _seq;

        public bool FailAppend { get; set; }

        public int Counts { get; private set; }

        public void ResetCounts() => Counts = 0;

        public IReadOnlyList<TimelineRow> Live
        {
            get
            {
                lock (_gate)
                {
                    return [.. _live];
                }
            }
        }

        public string? LastState(string dirId, string subject)
        {
            lock (_gate)
            {
                return _live.Where(r => r.DirId == dirId && r.Subject == subject).OrderByDescending(static r => r.Seq).Select(static r => r.To).FirstOrDefault();
            }
        }

        public int Recents { get; private set; }

        public IReadOnlyList<TimelineSeed> Recent(string dirId, string subject, long sinceMs)
        {
            lock (_gate)
            {
                Recents++;
                var own = _live.Where(r => r.DirId == dirId && r.Subject == subject).OrderBy(static r => r.AtMs).ThenBy(static r => r.Seq).ToList();
                var before = own.LastOrDefault(r => r.AtMs < sinceMs);
                return [.. (before.Subject is null ? [] : new[] { before }).Concat(own.Where(r => r.AtMs >= sinceMs)).Select(static r => new TimelineSeed(r.Subject, r.To, r.AtMs))];
            }
        }

        public void Append(IReadOnlyList<TimelineRow> rows)
        {
            lock (_gate)
            {
                if (FailAppend)
                {
                    throw new IOException("disk");
                }

                foreach (var row in rows)
                {
                    if (!_live.Any(r => r.DirId == row.DirId && r.Subject == row.Subject && r.To == row.To && r.AtMs == row.AtMs))
                    {
                        _live.Add(row with { Seq = ++_seq });
                    }
                }
            }
        }

        public IReadOnlyList<TimelineRow> Range(string dirId, long fromMs, long toMs, long afterSeq, long beforeSeq, int limit) =>
            Query(_live, dirId, fromMs, toMs, afterSeq, beforeSeq, limit);

        public long Count(string dirId, long fromMs, long toMs)
        {
            lock (_gate)
            {
                Counts++;
                return _live.Count(r => r.DirId == dirId && r.AtMs >= fromMs && r.AtMs <= toMs);
            }
        }

        public TimelineSummary Summarize(string dirId, long fromMs, long toMs)
        {
            lock (_gate)
            {
                Counts++;
                return SummaryOf(_live, dirId, fromMs, toMs);
            }
        }

        internal static TimelineSummary SummaryOf(List<TimelineRow> rows, string dirId, long fromMs, long toMs)
        {
            var hit = rows.Where(r => r.DirId == dirId && r.AtMs >= fromMs && r.AtMs <= toMs).ToList();
            return hit.Count == 0 ? default : new TimelineSummary(hit.Count, hit.Max(static r => r.Seq), hit.Min(static r => r.Seq), hit.Min(static r => r.AtMs), hit.Max(static r => r.AtMs));
        }

        public int Prune(long beforeMs, int batch)
        {
            lock (_gate)
            {
                var old = _live.Where(r => r.AtMs < beforeMs).OrderBy(static r => r.AtMs).ThenBy(static r => r.Seq).Take(batch).ToList();
                foreach (var row in old)
                {
                    _ = _live.Remove(row);
                }

                return old.Count;
            }
        }

        public void Dispose()
        {
        }

        private IReadOnlyList<TimelineRow> Query(List<TimelineRow> rows, string dirId, long fromMs, long toMs, long afterSeq, long beforeSeq, int limit)
        {
            lock (_gate)
            {
                return [.. rows
                    .Where(r => r.DirId == dirId && r.AtMs >= fromMs && r.AtMs <= toMs && (afterSeq == 0 || r.Seq > afterSeq) && (beforeSeq == 0 || r.Seq < beforeSeq))
                    .OrderBy(static r => r.Seq).Take(Math.Min(limit, 200))];
            }
        }
    }

    /// <summary>An activity lane stand-in: each key's body and as_of are set by the test.</summary>
    internal sealed class FakeActivity : IModelLane
    {
        public Dictionary<string, (string? State, long AsOf)> Records { get; } = [];

        public string Name => "activity";

        public IReadOnlyList<string> SliceIds(string key) => [key];

        public IReadOnlyList<string> Keys() => [.. Records.Keys];

        public LaneResult Reread(string key, long nowMs)
        {
            if (!Records.TryGetValue(key, out var record))
            {
                return new LaneResult([new SliceChange(key, null)]);
            }

            var body = record.State is null ? "{}" : "{\"activity\":\"" + record.State + "\"}";
            return new LaneResult([new SliceChange(key, Encoding.UTF8.GetBytes(body), record.AsOf)]);
        }
    }

    internal static readonly string Key = ActivityLane.KeyOf(Dir, "s1");
    private static readonly string DirId = ActivityLane.DirId(Dir);

    [Fact]
    public void AScriptedSequenceOfStatesAppendsExactlyItsTransitionsAndAReplayAddsNothing()
    {
        var store = new MemoryStore();
        var inner = new FakeActivity();
        var recorder = new TimelineRecorder(inner, store, _ => { });
        foreach (var (state, at) in new[] { ("busy", 100L), ("busy", 100L), ("idle", 200L), ("idle", 300L), ("busy", 400L) })
        {
            inner.Records[Key] = (state, at);
            _ = recorder.Reread(Key, 999);
        }

        Assert.Equal(
            [(null, "busy", 100L), ("busy", "idle", 200L), ("idle", "busy", 400L)],
            store.Live.Select(static r => (r.From, r.To, r.AtMs)));
        Assert.All(store.Live, r => Assert.Equal((DirId, "s1"), (r.DirId, r.Subject)));
    }

    [Fact]
    public void ARestartedRecorderSeesTheStoredStateAndWritesNoRowForAnUnchangedOneWhateverTheTimestamp()
    {
        var store = new MemoryStore();
        var inner = new FakeActivity();
        inner.Records[Key] = ("busy", 100);
        _ = new TimelineRecorder(inner, store, _ => { }).Reread(Key, 999);

        inner.Records[Key] = ("busy", 0);
        var restarted = new TimelineRecorder(inner, store, _ => { });
        _ = restarted.Reread(Key, 777);

        _ = Assert.Single(store.Live);
        Assert.Equal(0, store.Live.Count(static r => r.AtMs == 777));

        inner.Records[Key] = ("idle", 500);
        _ = restarted.Reread(Key, 999);

        Assert.Equal(("busy", "idle"), (store.Live[^1].From, store.Live[^1].To));
    }

    [Fact]
    public void ARestartedRecorderReplayingTheSameStateAndInstantAddsNoRow()
    {
        var store = new MemoryStore();
        var inner = new FakeActivity();
        inner.Records[Key] = ("busy", 100);
        _ = new TimelineRecorder(inner, store, _ => { }).Reread(Key, 999);

        _ = new TimelineRecorder(inner, store, _ => { }).Reread(Key, 999);

        _ = Assert.Single(store.Live);
    }

    [Fact]
    public void NegativeARecordWithNoStateOrThatIsGoneAppendsNothingAndAGoneRecordThatReturnsUnchangedAddsNoRow()
    {
        var store = new MemoryStore();
        var inner = new FakeActivity();
        var recorder = new TimelineRecorder(inner, store, _ => { });
        inner.Records[Key] = (null, 50);
        _ = recorder.Reread(Key, 999);
        Assert.Empty(store.Live);

        inner.Records[Key] = ("busy", 100);
        _ = recorder.Reread(Key, 999);
        _ = inner.Records.Remove(Key);
        _ = recorder.Reread(Key, 999);
        inner.Records[Key] = ("busy", 200);
        _ = recorder.Reread(Key, 999);

        Assert.Equal([(null, "busy", 100L)], store.Live.Select(static r => (r.From, r.To, r.AtMs)));

        inner.Records[Key] = ("idle", 300);
        _ = recorder.Reread(Key, 999);

        Assert.Equal([(null, "busy", 100L), ("busy", "idle", 300L)], store.Live.Select(static r => (r.From, r.To, r.AtMs)));
    }

    [Fact]
    public void ARecordWithoutATimestampIsStampedWithTheClockOfTheRead()
    {
        var store = new MemoryStore();
        var inner = new FakeActivity();
        inner.Records[Key] = ("busy", 0);

        _ = new TimelineRecorder(inner, store, _ => { }).Reread(Key, 777);

        Assert.Equal(777, Assert.Single(store.Live).AtMs);
    }

    [Fact]
    public void AStoreFailureIsCountedNeverThrownAndTheTransitionIsRetriedOnTheNextRead()
    {
        var store = new MemoryStore { FailAppend = true };
        var inner = new FakeActivity();
        var notified = new List<string>();
        var recorder = new TimelineRecorder(inner, store, notified.Add);
        inner.Records[Key] = ("busy", 100);

        _ = recorder.Reread(Key, 999);
        Assert.Equal(1, recorder.Failures);
        Assert.Empty(notified);

        store.FailAppend = false;
        _ = recorder.Reread(Key, 999);

        _ = Assert.Single(store.Live);
        Assert.Equal([Dir], notified);
    }

    [Fact]
    public void TheRecorderPassesTheInnerResultThroughUnchanged()
    {
        var inner = new FakeActivity();
        inner.Records[Key] = ("busy", 100);
        var recorder = new TimelineRecorder(inner, new MemoryStore(), _ => { });

        var change = Assert.Single(recorder.Reread(Key, 0).Changes);

        Assert.Equal(Key, change.Id);
        Assert.Equal("{\"activity\":\"busy\"}", Encoding.UTF8.GetString(change.Body!));
        Assert.Equal(100, change.AsOfMs);
    }

    [Fact]
    public void TheTimelineSliceIsTheLiveRowCountOfItsDirAndNoOtherDirsAreCounted()
    {
        var store = new MemoryStore();
        store.Append([new TimelineRow(0, DirId, "s1", null, "busy", 1), new TimelineRow(0, DirId, "s1", "busy", "idle", 2), new TimelineRow(0, "other", "s9", null, "busy", 3)]);
        var lane = new TimelineLane(store, () => [Dir], () => 7, () => 1_000);

        var change = Assert.Single(lane.Reread(Dir, 0).Changes);

        Assert.Equal("timeline:" + DirId, change.Id);
        Assert.Equal("{\"retention_days\":7,\"total\":2,\"head_cursor\":2,\"tail_cursor\":1,\"oldest_at\":1,\"newest_at\":2}", Encoding.UTF8.GetString(change.Body!));
        Assert.Equal(0, change.AsOfMs);
        Assert.Null(Assert.Single(lane.Reread("/not/canonical", 0).Changes).Body);
        Assert.Equal([Dir], lane.Keys());
    }

    [Theory]
    [InlineData(7, 7)]
    [InlineData(30, 30)]
    [InlineData(0, 1)]
    public void TheTimelineSliceCarriesTheRetentionDaysOfTheConfigAtEachCompose(int configured, int expected)
    {
        var days = 7;
        var lane = new TimelineLane(new MemoryStore(), () => [Dir], () => days, () => 1_000);
        string Retention() => Ah.Core.Json.JsonText.Parse(Encoding.UTF8.GetString(Assert.Single(lane.Reread(Dir, 0).Changes).Body!)).Member("retention_days")!.Value.AsNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("7", Retention());

        days = configured;

        Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), Retention());
    }

    [Fact]
    public void AnIdleLazyTimelineLaneCountsNothingAndTheFirstRequestCountsOnce()
    {
        var store = new MemoryStore();
        var connected = false;
        var lazy = new LazyLane(new TimelineLane(store, () => [Dir], () => 7, () => 1_000), () => connected);

        for (var i = 0; i < 20; i++)
        {
            _ = lazy.Reread(Dir, 0);
        }

        Assert.Equal(0, store.Counts);

        connected = true;
        _ = lazy.Reread(Dir, 0);

        Assert.Equal(1, store.Counts);
    }

    [Fact]
    public void ADisposeThatOutwaitsItsJoinLeavesTheRunningArchiverNothingDisposedToTouch()
    {
        using var inRun = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var background = new BackgroundArchiver(
            new TimelineArchiver(new MemoryStore(), () => 7, () => 0),
            _ => { inRun.Set(); release.Wait(); },
            TimeSpan.FromHours(1),
            TimeSpan.Zero) { JoinTimeout = TimeSpan.FromMilliseconds(20) };
        Assert.True(inRun.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        background.Dispose();
        background.Signal();
        release.Set();

        // the thread finishes its run and stops on the cancel; a throw there would crash the test host
        Assert.True(SpinWait.SpinUntil(() => background.Runs == 1, TimeSpan.FromSeconds(10)));
        background.Signal();
    }

    [Fact]
    public void TheArchiverPrunesOnlyRowsOlderThanTheRetentionWindowInBatchesOfAThousand()
    {
        var store = new MemoryStore();
        const long Day = 86_400_000L;
        var now = 100 * Day;
        var old = Enumerable.Range(1, 2500).Select(i => new TimelineRow(0, DirId, "s" + i, null, "busy", (50 * Day) + i)).ToList();
        store.Append(old);
        store.Append([new TimelineRow(0, DirId, "fresh", null, "busy", now - Day), new TimelineRow(0, DirId, "edge", null, "busy", now - (7 * Day))]);
        var archiver = new TimelineArchiver(store, () => 7, () => now);

        var moved = archiver.Run();

        Assert.Equal(2500, moved);
        Assert.Equal(["fresh", "edge"], store.Live.Select(static r => r.Subject).OrderByDescending(static s => s));
        Assert.Equal(2, store.Count(DirId, 0, long.MaxValue));
    }

    [Fact]
    public void NegativeAZeroRetentionAnEmptyStoreAndAFailingStoreMoveNothingAndNeverThrow()
    {
        var store = new MemoryStore();
        store.Append([new TimelineRow(0, DirId, "s1", null, "busy", 1)]);

        Assert.Equal(0, new TimelineArchiver(store, () => 0, () => 10_000_000_000).Run());
        Assert.Single(store.Live);
        Assert.Equal(0, new TimelineArchiver(new MemoryStore(), () => 7, () => 10_000_000_000).Run());

        var failing = new TimelineArchiver(new ThrowingStore(), () => 7, () => 10_000_000_000);
        Assert.Equal(0, failing.Run());
        Assert.Equal(1, failing.Failures);
    }

    private sealed class ThrowingStore : ITimelineStore
    {
        public void Append(IReadOnlyList<TimelineRow> rows) => throw new IOException();

        public string? LastState(string dirId, string subject) => null;

        public IReadOnlyList<TimelineSeed> Recent(string dirId, string subject, long sinceMs) => [];

        public int Prune(long beforeMs, int batch) => throw new IOException();

        public IReadOnlyList<TimelineRow> Range(string dirId, long fromMs, long toMs, long afterSeq, long beforeSeq, int limit) => [];

        public long Count(string dirId, long fromMs, long toMs) => 0;

        public TimelineSummary Summarize(string dirId, long fromMs, long toMs) => default;

        public void Dispose()
        {
        }
    }
}
