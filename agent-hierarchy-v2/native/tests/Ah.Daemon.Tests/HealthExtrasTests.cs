using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary>The health fields other modules own are read from the composed source on every health call.</summary>
public sealed class HealthExtrasTests
{
    private sealed class Source(Func<HealthExtras> read) : IHealthExtras
    {
        public HealthExtras Read() => read();
    }

    [Fact]
    public async Task ARouteOnABusyStoreAnswers503WithTheReason()
    {
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Routes.Add(new RouteRegistration("GET /t/busy", (_, _) =>
            throw new StoreUnavailableException("the state database could not be read: busy"))));

        var reply = await h.GetAsync("/t/busy");

        Assert.Equal(503, reply.Status);
        Assert.Contains("busy", reply.Body, StringComparison.Ordinal);
        Assert.Equal("1", reply.Headers["Retry-After"]);
    }

    [Fact]
    public async Task NegativeARouteThatFailsOtherwiseIsNotCalledBusy()
    {
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Routes.Add(new RouteRegistration("GET /t/boom", (_, _) =>
            throw new InvalidOperationException("boom"))));

        var reply = await h.GetAsync("/t/boom");

        Assert.NotEqual(503, reply.Status);
        Assert.DoesNotContain("store-unavailable", reply.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HealthReportsTheComposedExtras()
    {
        var ladder = new JsonObject { ["pending"] = 2, ["sending"] = 1, ["paused"] = 0, ["escalated"] = 3 };
        var timerState = new JsonObject { ["s1"] = new JsonObject { ["last_tick"] = "t" } };
        var source = new Source(() => new HealthExtras(true, ["s1"], ladder, timerState, 4));
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Health = source);

        var json = (await h.GetAsync("/v1/health")).Json;

        Assert.True(json["owns"].AsObject()["status"].AsBool());
        Assert.Equal("s1", json["owns"].AsObject()["timers"].AsArray()[0].AsString());
        Assert.Equal(2, (int)json["ladder"].AsObject()["pending"].AsNumber());
        Assert.Equal(3, (int)json["ladder"].AsObject()["escalated"].AsNumber());
        Assert.True(json["timer_state"].AsObject().ContainsKey("s1"));
        Assert.Equal(4, (int)json["unpulled_acks"].AsNumber());
    }

    [Fact]
    public async Task HealthCarriesTheLoadObjectWhenTheSourceHasOneAndNullWhenItDoesNot()
    {
        var load = new JsonObject { ["state"] = "overloaded", ["harnesses"] = 7 };
        await using var with = await Harness.StartAsync(Sandbox.Create(), o => o.Health = new Source(() => HealthExtras.None with { Load = load }));
        await using var without = await Harness.StartAsync(Sandbox.Create(), o => o.Health = new Source(() => HealthExtras.None));

        var present = (await with.GetAsync("/v1/health")).Json;
        var absent = (await without.GetAsync("/v1/health")).Json;

        Assert.Equal("overloaded", present["load"].AsObject()["state"].AsString());
        Assert.Equal(7, (int)present["load"].AsObject()["harnesses"].AsNumber());
        Assert.True(absent["load"].IsNull);
    }

    [Fact]
    public async Task HealthIsExactlyTheRecordedContractAndCarriesNoReadModelCounters()
    {
        var source = new Source(() => new HealthExtras(true, ["s1"], new JsonObject { ["pending"] = 1 }, new JsonObject(), 0));
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Health = source);

        var json = (await h.GetAsync("/v1/health")).Json;

        Assert.False(json.ContainsKey("read_model"));
        Assert.Equal(
            ["errors", "errors_by_code", "gc", "health_calls", "impl", "ladder", "load", "owns", "pid", "protocol", "protocols", "requests", "root", "started_at", "timer_state", "unpulled_acks", "version"],
            json.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>b459: the census counts each cache that added itself, with its bytes where it gives them; a name never added is not listed.</summary>
    [Fact]
    public async Task HealthCountsEachCacheThatAddedItselfToTheCensus()
    {
        var cache = new List<string> { "a", "bb" };
        Ah.Core.CoreModule.CensusAdd(cache, "test.census_probe", static c => c.Count, static c => c.Sum(static s => (long)s.Length));
        await using var h = await Harness.StartAsync(Sandbox.Create());

        var census = (await h.GetAsync("/v1/health")).Json["gc"].AsObject()["census"].AsObject();

        var row = census["test.census_probe"].AsObject();
        Assert.Equal(2, row["count"].AsNumber());
        Assert.Equal(3, row["bytes"].AsNumber());
        Assert.False(census.ContainsKey("test.census_never_added"));
        GC.KeepAlive(cache);
    }

    [Fact]
    public async Task ACensusCountThatThrowsLeavesHealthAnsweringWithTheOtherEntries()
    {
        var cache = new List<string> { "a" };
        Ah.Core.CoreModule.CensusAdd(cache, "test.census_throws", static _ => throw new NotSupportedException());
        Ah.Core.CoreModule.CensusAdd(cache, "test.census_beside", static c => c.Count);
        await using var h = await Harness.StartAsync(Sandbox.Create());

        var reply = await h.GetAsync("/v1/health");

        Assert.Equal(200, reply.Status);
        var census = reply.Json["gc"].AsObject()["census"].AsObject();
        Assert.Equal(1, census["test.census_beside"].AsObject()["count"].AsNumber());
        Assert.False(census.ContainsKey("test.census_throws"));
        GC.KeepAlive(cache);
    }

    [Fact]
    public async Task AStopHookThatNeverReturnsEndsTheProcessAtTheDeadlineWithItsFilesGone()
    {
        using var release = new ManualResetEventSlim();
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var box = Sandbox.Create();
        await using var h = await Harness.StartAsync(box, o =>
        {
            o.Timings = DaemonTimings.Default with { DrainDeadline = TimeSpan.FromMilliseconds(200) };
            o.OnStop = () => release.Wait();
            o.HardExit = () => exited.TrySetResult();
        });
        var info = Path.Combine(box.AhHome, "run", "ahd.json");
        Assert.True(File.Exists(info));

        _ = await h.PostAsync("/v1/drain", "{}");
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.False(File.Exists(info));
        Assert.False(h.Exit.IsCompleted);
        Assert.Contains("(at: stop hook; open: 0 request(s), busy workers: none)", h.Log.ToString(), StringComparison.Ordinal);
        release.Set();
    }

    [Fact]
    public async Task ADrainWithAHangingStopHookFinishesWithinAFewSecondsWithoutTheDeadline()
    {
        using var release = new ManualResetEventSlim();
        var hardExits = 0;
        await using var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.OnStop = () => release.Wait();
            o.HardExit = () => Interlocked.Increment(ref hardExits);
        });
        var started = System.Diagnostics.Stopwatch.StartNew();

        _ = await h.PostAsync("/v1/drain", "{}");
        await h.Exit.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
        Assert.Equal(0, Volatile.Read(ref hardExits));
        Assert.Contains("stop hook still running after 5 s", h.Log.ToString(), StringComparison.Ordinal);
        release.Set();
    }

    [Fact]
    public async Task ADrainThatFinishesInTimeNeverReachesTheDeadline()
    {
        var hardExits = 0;
        await using var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.Timings = DaemonTimings.Default with { DrainDeadline = TimeSpan.FromSeconds(10) };
            o.HardExit = () => Interlocked.Increment(ref hardExits);
        });

        _ = await h.PostAsync("/v1/drain", "{}");
        await h.Exit.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromSeconds(10.5), TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref hardExits));
    }

    [Fact]
    public async Task ADrainedDaemonAnswers503UntilTheGraceAndThenItsSocketIsGone()
    {
        // ah2-645 G1: the grace is a floor, so a loaded machine only makes the wait longer; a grace well above the 300 ms wait keeps the 503 check off the load.
        var grace = TimeSpan.FromSeconds(6);
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Timings = DaemonTimings.Default with { DrainGrace = grace });
        var started = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(200, (await h.PostAsync("/v1/drain", "{}")).Status);
        await Task.Delay(TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken);
        Assert.Equal(503, (await h.GetAsync("/v1/health")).Status);
        Assert.Equal(503, (await h.PostAsync("/v1/drain", "{}")).Status);
        Assert.True(File.Exists(h.Box.Sock));

        await h.Exit.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.True(started.Elapsed >= grace, $"exited after {started.Elapsed}");
        Assert.False(File.Exists(h.Box.Sock));
    }

    [Fact]
    public async Task AShutdownThatIsNotADrainDoesNotWaitForTheGrace()
    {
        // ah2-645 G2: the idle and signal paths call RequestShutdown directly; a grace far above the wait proves none applies.
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Timings = DaemonTimings.Default with { DrainGrace = TimeSpan.FromSeconds(60) });

        h.Server.RequestShutdown(0);

        Assert.True(await h.ExitsWithinAsync(TimeSpan.FromSeconds(20)), h.Log.ToString());
        Assert.False(File.Exists(h.Box.Sock));
    }

    [Fact]
    public async Task ADrainAskingForNoGraceExitsWithoutWaitingForIt()
    {
        // ah2-645 G4: the stop hook holds the drain so the no-grace drain is seen refusing new work before it exits.
        using var release = new ManualResetEventSlim();
        await using var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.Timings = DaemonTimings.Default with { DrainGrace = TimeSpan.FromSeconds(60) };
            o.OnStop = () => release.Wait();
        });

        Assert.Equal(200, (await h.PostAsync("/v1/drain", "{\"reason\":\"test\",\"no_grace\":true}")).Status);
        Assert.Equal(503, (await h.GetAsync("/v1/health")).Status);
        release.Set();

        Assert.True(await h.ExitsWithinAsync(TimeSpan.FromSeconds(20)), h.Log.ToString());
        Assert.False(File.Exists(h.Box.Sock));
    }

    [Fact]
    public async Task ANoGraceFlagThatIsNotAJsonTrueStillGetsTheFullGrace()
    {
        // ah2-645 G4 negative: only a JSON true opts out; the string "true" is the nearest input that must not.
        var grace = TimeSpan.FromSeconds(3);
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Timings = DaemonTimings.Default with { DrainGrace = grace });
        var started = System.Diagnostics.Stopwatch.StartNew();

        Assert.Equal(200, (await h.PostAsync("/v1/drain", "{\"no_grace\":\"true\"}")).Status);
        await h.Exit.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.True(started.Elapsed >= grace, $"exited after {started.Elapsed}");
    }

    [Fact]
    public async Task TheStopHookRunsOnceDuringShutdownAndAFailingHookDoesNotStopTheExit()
    {
        var stops = 0;
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.OnStop = () =>
        {
            stops++;
            throw new InvalidOperationException("boom");
        });

        _ = await h.PostAsync("/v1/drain", "{}");
        await h.Exit.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.Equal(1, stops);
    }

    [Fact]
    public async Task WithoutAStopHookShutdownStillExits()
    {
        await using var h = await Harness.StartAsync(Sandbox.Create());

        _ = await h.PostAsync("/v1/drain", "{}");
        await h.Exit.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

        Assert.True(h.Exited);
    }

    [Fact]
    public async Task WithoutASourceHealthReportsNothingOwned()
    {
        await using var h = await Harness.StartAsync(Sandbox.Create());

        var json = (await h.GetAsync("/v1/health")).Json;

        Assert.False(json["owns"].AsObject()["status"].AsBool());
        Assert.Empty(json["owns"].AsObject()["timers"].AsArray());
        Assert.Equal(0, (int)json["unpulled_acks"].AsNumber());
    }

    [Fact]
    public async Task EachHealthCallReadsTheSourceAgain()
    {
        var reads = 0;
        var source = new Source(() =>
        {
            reads++;
            return HealthExtras.None with { UnpulledAcks = reads };
        });
        await using var h = await Harness.StartAsync(Sandbox.Create(), o => o.Health = source);

        var first = (int)(await h.GetAsync("/v1/health")).Json["unpulled_acks"].AsNumber();
        var second = (int)(await h.GetAsync("/v1/health")).Json["unpulled_acks"].AsNumber();

        Assert.Equal(first + 1, second);
    }
}
