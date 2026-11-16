using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch.Model;

namespace Ah.Watch.Tests;

/// <summary>A poll slower than its period must not hold one more thread every period.</summary>
public sealed class PollPileUpTests
{
    [Fact]
    public async Task APollStillRunningAnswersForTheNextAndNegativeOneThatFinishedDoesNot()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var runs = 0;
        using var poller = new ClientGatedPoller(() => true, TimeSpan.FromHours(1), () =>
        {
            _ = Interlocked.Increment(ref runs);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        });

        var first = Task.Run(poller.PollNow, TestContext.Current.CancellationToken);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        poller.PollNow();
        Assert.Equal(1, Volatile.Read(ref runs));

        release.Set();
        await first;
        poller.PollNow();
        Assert.Equal(2, Volatile.Read(ref runs));
    }

    [Fact]
    public void NegativeAPollThatThrewDoesNotStopTheNext()
    {
        var runs = 0;
        using var poller = new ClientGatedPoller(() => true, TimeSpan.FromHours(1), () =>
        {
            runs++;
            throw new InvalidOperationException("busy");
        });

        poller.PollNow();
        poller.PollNow();

        Assert.Equal(2, runs);
    }

    private sealed class NoReader : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) => JsonValue.Null;
    }

    private sealed class NoProcesses : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => null;
    }

    private sealed class PidsReader : IViewStateReader
    {
        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args) =>
            domain == "session-pids" ? JsonText.Parse("""{"sessions":[{"pid":7},{"pid":8}]}""") : JsonValue.Null;
    }

    private sealed class ScopeRecordingTable : IProcessTable
    {
        internal List<(int Pid, bool? Shared)> Asked { get; } = [];

        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => pid == 7 ? "start" : null;

        public string? Command(int pid)
        {
            Asked.Add((pid, Ah.Core.Time.ProcessSnapshotScope.Active?.Shared));
            return "claude";
        }
    }

    [Fact]
    public void ACommandLineIsAskedInsideASharedReadingAndNegativeADeadPidIsNotAsked()
    {
        var table = new ScopeRecordingTable();
        var lane = new ProcessesLane(new PidsReader(), static () => ["/d"], table);

        _ = lane.Reread("/d", 0);

        Assert.Equal([(7, true)], table.Asked);
    }

    private sealed class CountingPidsReader : IViewStateReader
    {
        internal int Reads { get; private set; }

        internal Func<long>? Cost { get; init; }

        public JsonValue Read(string domain, string op, string hierarchyDir, JsonObject args)
        {
            if (domain == "session-pids")
            {
                Reads++;
                _ = Cost?.Invoke();
            }

            return domain == "session-pids" ? JsonText.Parse("""{"sessions":[{"pid":7}]}""") : JsonValue.Null;
        }
    }

    [Fact]
    public void AReReadAfterAPollUsesThePollsPidsAndNegativeTheNextReReadReadsAgain()
    {
        var reader = new CountingPidsReader();
        var lane = new ProcessesLane(reader, static () => ["/d"], new NoProcesses());

        lane.Poll(static (_, _) => { });
        Assert.Equal(1, reader.Reads);

        _ = lane.Reread("/d", 0);
        Assert.Equal(1, reader.Reads);

        _ = lane.Reread("/d", 0);
        Assert.Equal(2, reader.Reads);
    }

    private sealed class DyingTable : IProcessTable
    {
        internal bool Alive { get; set; } = true;

        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => Alive ? "start" : null;
    }

    /// <summary>b459 step 28: a dir's pids are held on its stamp; negative: a moved stamp reads that dir alone, and a dead pid shows with no stamp move.</summary>
    [Fact]
    public void PidsAreHeldOnTheirStampAndNegativeAMovedStampReadsThatDirAloneAndADeadPidShowsAtOnce()
    {
        var reader = new CountingPidsReader();
        var stamps = new Dictionary<string, string> { ["/d"] = "1", ["/e"] = "1" };
        var table = new DyingTable();
        var lane = new ProcessesLane(reader, static () => ["/d", "/e"], table, pidsStamp: d => stamps[d]);
        bool Started(string dir) => !JsonText.Parse(lane.Reread(dir, 0).Changes[0].Body!).Member("pids")!.Value.AsArray()[0].Member("start")!.Value.IsNull;

        _ = lane.Reread("/d", 0);
        _ = lane.Reread("/e", 0);
        Assert.Equal(2, lane.PidReads);
        _ = lane.Reread("/d", 0);
        Assert.Equal(2, lane.PidReads);

        stamps["/d"] = "2";
        _ = lane.Reread("/d", 0);
        _ = lane.Reread("/e", 0);
        Assert.Equal(3, lane.PidReads);

        Assert.True(Started("/d"));
        table.Alive = false;
        Assert.False(Started("/d"));
        Assert.Equal(3, lane.PidReads);
    }

    [Fact]
    public void PidsWaitTenTimesWhatTheLastReadingCostAndNegativeANewDirIsReadAtOnce()
    {
        var clock = 1_000L;
        var dirs = new List<string> { "/d" };
        var reader = new CountingPidsReader { Cost = () => clock += 100 };
        var lane = new ProcessesLane(reader, () => dirs, new NoProcesses(), () => clock);

        lane.Poll(static (_, _) => { });
        Assert.Equal(1, reader.Reads);

        clock += 999;
        lane.Poll(static (_, _) => { });
        Assert.Equal(1, reader.Reads);

        dirs.Add("/e");
        lane.Poll(static (_, _) => { });
        Assert.Equal(2, reader.Reads);

        clock += 1;
        lane.Poll(static (_, _) => { });
        Assert.Equal(4, reader.Reads);
    }

    [Fact]
    public void MemberFactsWaitTenTimesWhatTheLastReadingCostAndNegativeACheapReadingWaitsForNothing()
    {
        var clock = 1_000L;
        var cost = 0L;
        var readings = 0;
        var lane = new ProcessesLane(new NoReader(), static () => ["/d"], new NoProcesses(), () => clock, _ =>
        {
            readings++;
            clock += cost;
            return null!;
        });

        lane.Poll(static (_, _) => { });
        lane.Poll(static (_, _) => { });
        Assert.Equal(2, readings);

        cost = 100;
        lane.Poll(static (_, _) => { });
        Assert.Equal(3, readings);

        clock += 999;
        lane.Poll(static (_, _) => { });
        Assert.Equal(3, readings);

        clock += 1;
        lane.Poll(static (_, _) => { });
        Assert.Equal(4, readings);
    }
}
