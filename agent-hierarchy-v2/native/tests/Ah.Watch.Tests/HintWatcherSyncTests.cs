using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>How the watchers are brought in line with the directories: the first sync's re-read, the coalesced queue, and a failed queued sync (b233).</summary>
public sealed class HintWatcherSyncTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ah-sync-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Dir(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static void WaitUntil(Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(30);
        while (!done() && DateTime.UtcNow < end)
        {
            Thread.Sleep(10);
        }
    }

    [Fact]
    public void TheFirstSyncThatStartsADirectoryWatcherAsksForARereadAndNegativeASyncThatStartsNoneDoesNot()
    {
        var dir = Dir("h");
        var dirs = new DirSet(() => [dir]);
        _ = dirs.Refresh();
        var overflows = 0;
        using var watcher = new HintWatcher(dirs, _ => { }, () => Interlocked.Increment(ref overflows));

        watcher.Sync();
        Assert.Equal(1, overflows);

        watcher.Sync();
        Assert.Equal(1, overflows);
    }

    [Fact]
    public void ALaneDirectoryGetsItsOwnWatcherAndNegativeTmpGetsNone()
    {
        var dir = Dir("h");
        _ = Directory.CreateDirectory(Path.Combine(dir, "msgs"));
        _ = Directory.CreateDirectory(Path.Combine(dir, "tmp", "team", "member"));
        var dirs = new DirSet(() => [dir]);
        _ = dirs.Refresh();
        var overflows = 0;
        using var watcher = new HintWatcher(dirs, _ => { }, () => Interlocked.Increment(ref overflows));

        watcher.Sync();
        Assert.Equal(2, watcher.Running); // the directory itself and msgs, not tmp (b459)

        _ = Directory.CreateDirectory(Path.Combine(dir, "activity"));
        watcher.Sync();
        Assert.Equal(3, watcher.Running);
        Assert.Equal(2, overflows); // a lane directory watched later asks for a re-read of what was written before its watcher
    }

    [Fact]
    public void AsksSpacedOutDuringAStalledSyncRunOneMoreSyncOnTheSameSingleThread()
    {
        var dir = Dir("h");
        var dirs = new DirSet(() => [dir]);
        _ = dirs.Refresh();
        var chosen = 0;
        var threads = new System.Collections.Concurrent.ConcurrentDictionary<int, byte>();
        using var release = new ManualResetEventSlim();
        using var watcher = new HintWatcher(dirs, _ => { }, () => { }, activeDirs: d =>
        {
            _ = threads.TryAdd(Environment.CurrentManagedThreadId, 0);
            if (Interlocked.Increment(ref chosen) == 1)
            {
                release.Wait(TimeSpan.FromSeconds(30));
            }

            return d;
        });

        watcher.SyncSoon();
        WaitUntil(() => Volatile.Read(ref chosen) == 1);
        for (var i = 0; i < 15; i++)
        {
            watcher.SyncSoon();
            Thread.Sleep(20);
        }

        release.Set();
        WaitUntil(() => Volatile.Read(ref chosen) >= 2);
        Thread.Sleep(200);
        Assert.Equal(2, Volatile.Read(ref chosen));
        Assert.Single(threads);
    }

    [Fact]
    public void NegativeAnAskAfterTheLoopEndedStartsItAgain()
    {
        var dir = Dir("h");
        var dirs = new DirSet(() => [dir]);
        _ = dirs.Refresh();
        var chosen = 0;
        using var watcher = new HintWatcher(dirs, _ => { }, () => { }, activeDirs: d =>
        {
            _ = Interlocked.Increment(ref chosen);
            return d;
        });

        watcher.SyncSoon();
        WaitUntil(() => Volatile.Read(ref chosen) == 1);
        Thread.Sleep(100);
        watcher.SyncSoon();
        WaitUntil(() => Volatile.Read(ref chosen) == 2);

        Assert.Equal(2, Volatile.Read(ref chosen));
    }

    [Fact]
    public void AQueuedSyncThatThrowsIsLoggedAndTheNextAskStillRuns()
    {
        var dirs = new DirSet(() => [Dir("h")]);
        _ = dirs.Refresh();
        var logged = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var calls = 0;
        using var watcher = new HintWatcher(dirs, _ => { }, () => { }, extraRoots: () =>
        {
            _ = Interlocked.Increment(ref calls);
            throw new IOException("roots unreadable");
        }, log: logged.Enqueue);

        watcher.SyncSoon();
        WaitUntil(() => !logged.IsEmpty);
        watcher.SyncSoon();
        WaitUntil(() => Volatile.Read(ref calls) >= 2);

        Assert.Contains(logged, l => l.Contains("roots unreadable", StringComparison.Ordinal));
        Assert.Equal(2, Volatile.Read(ref calls));
    }
}
