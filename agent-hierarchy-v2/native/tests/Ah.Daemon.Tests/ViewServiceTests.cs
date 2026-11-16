#pragma warning disable xUnit1051 // every wait here carries its own hard deadline

using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Daemon.Tests;

public sealed class ViewServiceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private sealed class ManualClock : IClock
    {
        private long _ms = DateTimeOffset.Parse("2026-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds();

        public DateTimeOffset UtcNow => DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _ms));

        public void Advance(int ms) => Interlocked.Add(ref _ms, ms);

        public TimeSpan LocalOffset(DateTimeOffset instant) => TimeSpan.Zero;
    }

    private sealed class FakeRevs : IStatusRevs
    {
        internal long Rev = 100;
        internal JsonValue Doc = JsonValue.FromObject(new JsonObject { ["a"] = 1 });
        internal bool Gone;

        public StatusReading? Read(string dir) => Gone ? null : new StatusReading(Interlocked.Read(ref Rev), Doc);

        public void Forget(string dir)
        {
        }
    }

    private sealed class Rig
    {
        internal FakeRevs Revs { get; } = new();

        internal IStatusDirs Dirs { get; } = WatchModule.CreateStatusDirs();

        internal int Builds;
        internal int Gated;
        internal bool FailFirst;
        internal int HerdrCalls;
        internal string ViewRev = "r1";
        internal int BuildMs;
        internal ManualClock Clock { get; } = new();
        internal Func<TimeSpan, CancellationToken, Task<JsonArray>> Herdr { get; set; } = static (_, _) => Task.FromResult(new JsonArray());
        internal bool UseHerdr;
        internal Action? DuringBuild;

        internal ViewService Service()
        {
            var deps = new ViewServiceDependencies(
                null!,
                Revs,
                Dirs,
                Clock,
                (t, c) =>
                {
                    _ = Interlocked.Increment(ref HerdrCalls);
                    return Herdr(t, c);
                });
            return new ViewService(deps, (path, now, dirs, herdr) =>
            {
                var n = Interlocked.Increment(ref Builds);
                DuringBuild?.Invoke();
                if (FailFirst && n == 1)
                {
                    throw new IOException("the first build fails");
                }

                if (BuildMs > 0)
                {
                    Thread.Sleep(BuildMs);
                }

                if (path == "/missing")
                {
                    return new ViewAnswer(404, new JsonObject { ["view_schema"] = 1, ["error"] = "not_found", ["path"] = path });
                }

                var body = new JsonObject { ["view_schema"] = 1, ["path"] = path, ["at"] = "now", ["rev"] = ViewRev };
                if (UseHerdr)
                {
                    var reading = herdr();
                    body["herdr"] = reading.Reason ?? "ok";
                    body["agents"] = reading.Agents is null ? JsonValue.Null : JsonValue.FromNumber(reading.Agents.Count);
                }

                return new ViewAnswer(200, body);
            });
        }
    }

    private static RouteRequest Req(string path, string query) =>
        new("GET", path, query, new Dictionary<string, string>(), default, new Dictionary<string, string>());

    private static JsonObject Body(RouteResponse r) => JsonText.Parse(r.Body.ToArray()).AsObject();

    private static string Code(RouteResponse r) => Body(r)["error"].AsObject()["code"].AsString();

    private static string Message(RouteResponse r) => Body(r)["error"].AsObject()["message"].AsString();

    private static async Task<RouteResponse> View(ViewService s, string query, CancellationToken token = default) =>
        await s.ViewAsync(Req("/v1/view", query), token).WaitAsync(Deadline, CancellationToken.None);

    private static async Task<RouteResponse> Status(ViewService s, string query, CancellationToken token = default) =>
        await s.StatusAsync(Req("/v1/status", query), token).WaitAsync(Deadline, CancellationToken.None);

    // ---------------------------------------------------------------- one process snapshot per build

    private const string PsSnapshot =
        "    1     0 ??       Sat Oct  3 09:00:00 2026 /sbin/launchd\n"
        + "   50     1 ttys001  Sat Oct 17 12:00:00 2026 claude --agent wrangl:implementor\n";

    private static Ah.TestKit.FakeProcessRunner CountingPs()
    {
        var runner = new Ah.TestKit.FakeProcessRunner();
        runner.Result = _ => Ah.TestKit.FakeProcessRunner.Exited(0, PsSnapshot);
        return runner;
    }

    [Fact]
    public async Task AViewBuildAsksPsOnceHoweverManyProcessesItChecks()
    {
        var runner = CountingPs();
        var table = Ah.Platform.PlatformModule.CreateProcessTable(runner);
        var rig = new Rig { DuringBuild = () => Enumerable.Range(10, 50).ToList().ForEach(pid => _ = table.StartTime(pid)) };

        var r = await View(rig.Service(), "path=/x");

        Assert.Equal(200, r.Status);
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task EachViewBuildTakesItsOwnSnapshotAndNothingIsSharedAcrossBuilds()
    {
        var runner = CountingPs();
        var table = Ah.Platform.PlatformModule.CreateProcessTable(runner);
        var rig = new Rig { DuringBuild = () => _ = table.StartTime(50) };
        var s = rig.Service();

        _ = await View(s, "path=/x");
        _ = await View(s, "path=/y");

        Assert.Equal(2, rig.Builds);
        Assert.Equal(2, runner.Requests.Count);
    }

    // ---------------------------------------------------------------- /v1/status

    [Fact]
    public async Task AStatusRequestNeedsAnAbsoluteDir()
    {
        var s = new Rig().Service();

        var none = await Status(s, string.Empty);
        var relative = await Status(s, "dir=rel/dir");

        Assert.Equal(400, none.Status);
        Assert.Equal("dir must be an absolute hierarchy dir", Message(none));
        Assert.Equal(400, relative.Status);
    }

    [Theory]
    [InlineData("26")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    public async Task AWaitOutsideZeroToTwentyFiveWholeSecondsIsRefused(string wait)
    {
        var rig = new Rig();
        rig.Dirs.Replace(["/h"]);

        var r = await Status(rig.Service(), $"dir=/h&since=1&wait={wait}");

        Assert.Equal(400, r.Status);
        Assert.Equal("wait must be a whole number of seconds from 0 to 25", Message(r));
    }

    [Fact]
    public async Task AWaitOfTwentyFiveIsAcceptedAndWaitWithoutSinceIsRefused()
    {
        var rig = new Rig();
        rig.Dirs.Replace(["/h"]);
        var s = rig.Service();
        using var cts = new CancellationTokenSource();
        var ok = Status(s, $"dir=/h&since={rig.Revs.Rev}&wait=25", cts.Token);
        await Task.Delay(100);
        cts.Cancel();

        var refused = await Status(s, "dir=/h&wait=3");

        Assert.Equal(200, (await ok).Status);
        Assert.Equal(400, refused.Status);
        Assert.Contains("wait needs since", Message(refused), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASinceThatIsNotANumberIsRefused()
    {
        var rig = new Rig();
        rig.Dirs.Replace(["/h"]);

        var r = await Status(rig.Service(), "dir=/h&since=abc");

        Assert.Equal(400, r.Status);
        Assert.Equal("since must be a rev", Message(r));
    }

    [Fact]
    public async Task AnUnwatchedOrUnreadableDirIsNotFoundAndIsForgotten()
    {
        var rig = new Rig();
        var s = rig.Service();

        var unwatched = await Status(s, "dir=/h");
        rig.Dirs.Replace(["/h"]);
        rig.Revs.Gone = true;
        var gone = await Status(s, "dir=/h");

        Assert.Equal(404, unwatched.Status);
        Assert.Equal(404, gone.Status);
        Assert.Equal("not_found", Code(gone));
        Assert.False(rig.Dirs.Contains("/h"));
    }

    [Fact]
    public async Task TheCurrentDocumentAnswersAtOnceWithOrWithoutSinceWhenNoWaitIsAsked()
    {
        var rig = new Rig();
        rig.Dirs.Replace(["/h"]);
        var s = rig.Service();

        var plain = await Status(s, "dir=/h");
        var withSince = await Status(s, $"dir=/h&since={rig.Revs.Rev}");

        Assert.Equal(100, Body(plain)["rev"].AsNumber());
        Assert.Equal(1, Body(plain)["doc"].AsObject()["a"].AsNumber());
        Assert.Equal(100, Body(withSince)["rev"].AsNumber());
    }

    [Fact]
    public async Task ARewriteDuringTheWaitAnswersOnTheNextPollAndNeverBeforeTheChange()
    {
        var rig = new Rig();
        rig.Dirs.Replace(["/h"]);
        var s = rig.Service();
        var clock = Stopwatch.StartNew();
        var waiting = Status(s, $"dir=/h&since={rig.Revs.Rev}&wait=10");

        await Task.Delay(500);
        Assert.False(waiting.IsCompleted);
        var changedAt = clock.ElapsedMilliseconds;
        Interlocked.Exchange(ref rig.Revs.Rev, 101);
        var r = await waiting;
        var answeredAt = clock.ElapsedMilliseconds;

        Assert.Equal(101, Body(r)["rev"].AsNumber());
        Assert.True(answeredAt >= changedAt);
        Assert.Equal(200, ViewService.PollMs);
    }

    [Fact]
    public async Task AWaitWithNoChangeEndsAtItsDeadlineWithTheCurrentDocument()
    {
        var rig = new Rig();
        rig.Dirs.Replace(["/h"]);
        var waiting = Status(rig.Service(), $"dir=/h&since={rig.Revs.Rev}&wait=1");

        rig.Clock.Advance(999);
        await Task.Delay(3 * ViewService.PollMs);
        Assert.False(waiting.IsCompleted);
        rig.Clock.Advance(1);
        var r = await waiting.WaitAsync(Deadline);

        Assert.Equal(200, r.Status);
        Assert.Equal(100, Body(r)["rev"].AsNumber());
    }

    [Fact]
    public async Task ADrainEndsALongPollAtOnce()
    {
        var rig = new Rig();
        rig.Dirs.Replace(["/h"]);
        using var cts = new CancellationTokenSource();
        var waiting = Status(rig.Service(), $"dir=/h&since={rig.Revs.Rev}&wait=20", cts.Token);

        await Task.Delay(150);
        Assert.False(waiting.IsCompleted);
        cts.Cancel();
        var r = await waiting;

        Assert.Equal(200, r.Status);
    }

    // ---------------------------------------------------------------- /v1/view

    [Fact]
    public async Task APathMustStartWithASlashAndBeNoLongerThanAThousandAndTwentyFour()
    {
        var rig = new Rig();
        var s = rig.Service();

        var noSlash = await View(s, "path=o/x");
        var tooLong = await View(s, "path=/" + new string('a', 1024));
        var longest = await View(s, "path=/" + new string('a', 1023));
        var defaulted = await View(s, string.Empty);

        Assert.Equal(400, noSlash.Status);
        Assert.Equal("path must be a view path starting with /", Message(noSlash));
        Assert.Equal(400, tooLong.Status);
        Assert.Equal(200, longest.Status);
        Assert.Equal("/", Body(defaulted)["path"].AsString());
    }

    [Fact]
    public async Task AViewWaitNeedsACursor()
    {
        var r = await View(new Rig().Service(), "path=/&wait=3");

        Assert.Equal(400, r.Status);
        Assert.Contains("wait needs since", Message(r), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANotFoundViewIsA404WithTheBuildersBody()
    {
        var r = await View(new Rig().Service(), "path=/missing");

        Assert.Equal(404, r.Status);
        Assert.Equal("not_found", Body(r)["error"].AsString());
        Assert.Equal("/missing", Body(r)["path"].AsString());
    }

    [Fact]
    public async Task ASinceThatMatchesTheRevIsUnchangedAndOneThatDiffersIsTheView()
    {
        var s = new Rig().Service();

        var same = await View(s, "path=/&since=r1");
        var other = await View(s, "path=/&since=r0");

        Assert.Equal(200, same.Status);
        Assert.True(Body(same)["unchanged"].AsBool());
        Assert.Equal("r1", Body(same)["rev"].AsString());
        Assert.Equal(1, Body(same)["view_schema"].AsNumber());
        Assert.False(Body(other).ContainsKey("unchanged"));
        Assert.Equal("/", Body(other)["path"].AsString());
    }

    [Fact]
    public async Task AWaitingRequestAndAPlainRequestOfOnePathShareOneBuildWithinAnEpoch()
    {
        var rig = new Rig();
        var s = rig.Service();

        _ = await View(s, "path=/a&since=r9&wait=1");
        _ = await View(s, "path=/a&since=r9&wait=1");
        _ = await View(s, "path=/a");
        _ = await View(s, "path=/a");

        Assert.Equal(1, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task ABuiltPageIsRedoneOnceItsEpochIsThreeSecondsOld()
    {
        var rig = new Rig();
        var s = rig.Service();
        _ = await View(s, "path=/a");

        rig.Clock.Advance(ViewService.EpochMs - 1);
        _ = await View(s, "path=/a");
        var withinTheEpoch = Volatile.Read(ref rig.Builds);
        rig.Clock.Advance(1);
        _ = await View(s, "path=/a");

        Assert.Equal(3000, ViewService.EpochMs);
        Assert.Equal(1, withinTheEpoch);
        Assert.Equal(2, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task ASlowBuildStretchesTheEpochSoItsCostFitsThePollRate()
    {
        var rig = new Rig();
        rig.DuringBuild = () => rig.Clock.Advance(2000);
        var s = rig.Service();
        _ = await View(s, "path=/a");

        // 2 s of build: the epoch is 10 s, so 5 s in the page is still served.
        rig.DuringBuild = null;
        rig.Clock.Advance(3000);
        _ = await View(s, "path=/a");
        Assert.Equal(1, Volatile.Read(ref rig.Builds));

        rig.Clock.Advance(6000);
        _ = await View(s, "path=/a");
        Assert.Equal(2, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task AWriteEndsAStretchedEpochOnceThePlainEpochHasPassed()
    {
        var rig = new Rig();
        rig.DuringBuild = () => rig.Clock.Advance(2000);
        var s = rig.Service();
        _ = await View(s, "path=/a");
        rig.DuringBuild = null;

        s.WriteHappened();
        rig.Clock.Advance(1000);
        _ = await View(s, "path=/a");
        Assert.Equal(2, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task NegativeAWriteDoesNotEndAnEpochYoungerThanThePlainOne()
    {
        var rig = new Rig();
        var s = rig.Service();
        _ = await View(s, "path=/a");

        s.WriteHappened();
        rig.Clock.Advance(2999);
        _ = await View(s, "path=/a");

        Assert.Equal(1, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task NegativeAWriteBeforeTheEpochBeganDoesNotEndIt()
    {
        var rig = new Rig();
        var s = rig.Service();
        rig.DuringBuild = () => rig.Clock.Advance(2000);
        s.WriteHappened();
        rig.Clock.Advance(1);
        _ = await View(s, "path=/a");
        rig.DuringBuild = null;

        rig.Clock.Advance(3000);
        _ = await View(s, "path=/a");

        Assert.Equal(1, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task AFastPageOfTheSameEpochDoesNotShortenTheStretch()
    {
        var rig = new Rig();
        rig.DuringBuild = () => rig.Clock.Advance(2000);
        var s = rig.Service();
        _ = await View(s, "path=/a");
        rig.DuringBuild = null;
        _ = await View(s, "path=/b");

        rig.Clock.Advance(3000);
        _ = await View(s, "path=/a");

        Assert.Equal(2, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task NegativeAFastBuildKeepsTheThreeSecondEpoch()
    {
        var rig = new Rig();
        var s = rig.Service();
        _ = await View(s, "path=/a");

        rig.Clock.Advance(ViewService.EpochMs);
        _ = await View(s, "path=/a");

        Assert.Equal(2, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task AnEpochAfterOneThatAskedForHerdrBuildsItsModelOnce()
    {
        var rig = new Rig { UseHerdr = true };
        var s = rig.Service();
        _ = await View(s, "path=/a");
        Assert.Equal(2, Volatile.Read(ref rig.Builds));

        rig.Clock.Advance(ViewService.MaxEpochMs * 10);
        _ = await View(s, "path=/a");

        Assert.Equal(3, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task NegativeAHierarchyThatNeverAskedForHerdrDoesNotReadIt()
    {
        var rig = new Rig();
        var s = rig.Service();
        _ = await View(s, "path=/a");

        rig.Clock.Advance(ViewService.MaxEpochMs * 10);
        _ = await View(s, "path=/a");

        Assert.Equal(0, Volatile.Read(ref rig.HerdrCalls));
    }

    [Fact]
    public async Task ConcurrentRequestsOfOnePathShareTheBuildInFlight()
    {
        var rig = new Rig { BuildMs = 400 };
        var s = rig.Service();

        var all = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => View(s, i % 2 == 0 ? "path=/a&since=r9&wait=1" : "path=/a")));

        Assert.All(all, r => Assert.Equal(200, r.Status));
        Assert.Equal(1, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task ALongPollIsAnsweredWithTheNewViewOnceTheEpochAllowsARebuild()
    {
        var rig = new Rig();
        var s = rig.Service();
        var waiting = View(s, "path=/&since=r1&wait=10");

        await Task.Delay(300);
        rig.ViewRev = "r2";
        await Task.Delay(3 * ViewService.PollMs);
        Assert.False(waiting.IsCompleted);
        rig.Clock.Advance(ViewService.EpochMs);
        var r = await waiting;

        Assert.Equal("r2", Body(r)["rev"].AsString());
    }

    // ---------------------------------------------------------------- one model per epoch

    [Fact]
    public async Task ConcurrentRequestsForOnePathBuildOnceWhateverTheyWait()
    {
        var rig = new Rig { BuildMs = 300 };
        var s = rig.Service();

        var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => View(s, "path=/")));

        Assert.All(all, r => Assert.Equal("r1", Body(r)["rev"].AsString()));
        Assert.Equal(1, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task ARepeatWithNothingChangedIsAnsweredInUnderAHundredMilliseconds()
    {
        var rig = new Rig { BuildMs = 300 };
        var s = rig.Service();
        _ = await View(s, "path=/");

        var clock = Stopwatch.StartNew();
        var again = await View(s, "path=/");

        Assert.True(clock.ElapsedMilliseconds < 100, $"repeat took {clock.ElapsedMilliseconds} ms");
        Assert.Equal("r1", Body(again)["rev"].AsString());
        Assert.Equal(1, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task ANewMessageIsSeenOnTheFirstRequestOfTheNextEpochAndNotBefore()
    {
        var rig = new Rig();
        var s = rig.Service();
        _ = await View(s, "path=/");
        rig.ViewRev = "r2";

        var within = await View(s, "path=/");
        rig.Clock.Advance(ViewService.EpochMs);
        var next = await View(s, "path=/");

        Assert.Equal("r1", Body(within)["rev"].AsString());
        Assert.Equal("r2", Body(next)["rev"].AsString());
    }

    [Fact]
    public async Task EveryPathOfAnEpochIsDerivedFromOneModel()
    {
        var made = 0;
        var derived = new List<string>();
        var rig = new Rig();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) =>
            {
                _ = Interlocked.Increment(ref made);
                return new CountingModel(derived);
            });

        var all = await Task.WhenAll(FivePages.Select(p => View(s, "path=" + p)));

        Assert.All(all, r => Assert.Equal(200, r.Status));
        Assert.Equal(1, made);
        Assert.Equal(5, derived.Count);
    }

    [Fact]
    public async Task NegativeADifferentEpochGetsAModelOfItsOwn()
    {
        var made = 0;
        var rig = new Rig();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) =>
            {
                _ = Interlocked.Increment(ref made);
                return new CountingModel([]);
            });
        _ = await View(s, "path=/");

        rig.Clock.Advance(ViewService.EpochMs);
        _ = await View(s, "path=/o/x");

        Assert.Equal(2, made);
    }

    [Fact]
    public async Task AFailedBuildIsNotKeptAndTheNextRequestBuildsAgain()
    {
        var rig = new Rig { FailFirst = true };
        var s = rig.Service();

        await Assert.ThrowsAsync<IOException>(() => View(s, "path=/"));
        var again = await View(s, "path=/");

        Assert.Equal(200, again.Status);
        Assert.Equal(2, Volatile.Read(ref rig.Builds));
    }

    [Fact]
    public async Task APageStillBeingBuiltWhenItsEpochEndsIsNotBuiltAgainByTheNextEpoch()
    {
        var rig = new Rig();
        var slow = new TaskCompletionSource();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => new GatedModel(rig, slow.Task));
        var detail = View(s, "path=/detail/o/x");
        _ = await View(s, "path=/");
        rig.Clock.Advance(ViewService.EpochMs);
        _ = await View(s, "path=/o/x");

        var second = View(s, "path=/detail/o/x");
        slow.SetResult();
        _ = await Task.WhenAll(detail, second);

        Assert.Equal(1, rig.Gated);
    }

    [Fact]
    public async Task APageThatLandsAfterItsEpochEndedIsNotServedToTheEpochThatFollows()
    {
        var made = 0;
        var gate = new TaskCompletionSource();
        var rig = new Rig();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => new StampedModel(Interlocked.Increment(ref made), rig, gate.Task, 0));
        var detail = View(s, "path=/detail/o/x");
        rig.Clock.Advance(ViewService.EpochMs);
        var other = await View(s, "path=/");

        gate.SetResult();
        var landed = await detail;
        var again = await View(s, "path=/detail/o/x");

        Assert.Equal("m2", Body(other)["rev"].AsString());
        Assert.Equal("m1", Body(landed)["rev"].AsString());
        Assert.Equal("m2", Body(again)["rev"].AsString());
        Assert.Equal(2, made);
    }

    [Fact]
    public async Task ARequestForAPageInFlightAcrossTheEpochEndWaitsForItInsteadOfBuildingItAgain()
    {
        var rig = new Rig();
        var slow = new TaskCompletionSource();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => new GatedModel(rig, slow.Task));
        var first = View(s, "path=/detail/o/x");
        rig.Clock.Advance(ViewService.EpochMs);
        var second = View(s, "path=/detail/o/x");

        slow.SetResult();
        _ = await Task.WhenAll(first, second);

        Assert.Equal(1, rig.Gated);
    }

    [Fact]
    public async Task AnEpochIsThreeSecondsOldFromWhenItStartedReadingEvenIfItsFirstPageLandedLater()
    {
        var made = 0;
        var rig = new Rig();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => new StampedModel(Interlocked.Increment(ref made), rig, Task.CompletedTask, 500));
        _ = await View(s, "path=/");

        rig.Clock.Advance(ViewService.EpochMs - 500);
        var next = await View(s, "path=/");

        Assert.Equal("m2", Body(next)["rev"].AsString());
        Assert.Equal(2, made);
    }

    [Fact]
    public async Task NegativeAPageIsStillServedOneMillisecondBeforeTheEpochIsThreeSecondsOld()
    {
        var made = 0;
        var rig = new Rig();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => new StampedModel(Interlocked.Increment(ref made), rig, Task.CompletedTask, 2000));
        _ = await View(s, "path=/");

        rig.Clock.Advance(ViewService.EpochMs - 2000 - 1);
        var same = await View(s, "path=/");

        Assert.Equal("m1", Body(same)["rev"].AsString());
        Assert.Equal(1, made);
    }

    /// <summary>A model numbered by when it was made: its pages say which model they came from, a detail page waits for a gate, and a build takes <paramref name="buildMs"/> of the manual clock.</summary>
    private sealed class StampedModel(int id, Rig rig, Task gate, int buildMs) : IViewModel
    {
        public bool UsedHerdr => false;

        public ViewAnswer Build(string path)
        {
            if (path.StartsWith("/detail", StringComparison.Ordinal))
            {
                gate.Wait(Deadline);
            }

            rig.Clock.Advance(buildMs);
            return new ViewAnswer(200, new JsonObject { ["view_schema"] = 1, ["path"] = path, ["rev"] = "m" + id });
        }
    }

    private static readonly string[] FivePages = ["/", "/o/x", "/detail/o/x", "/graph/o/x", "/plan/o/x"];

    private sealed class CountingModel(List<string> derived) : IViewModel
    {
        public bool UsedHerdr => false;

        public ViewAnswer Build(string path)
        {
            lock (derived)
            {
                derived.Add(path);
            }

            return new ViewAnswer(200, new JsonObject { ["view_schema"] = 1, ["path"] = path, ["rev"] = "r1" });
        }
    }

    private sealed class GatedModel(Rig rig, Task gate) : IViewModel
    {
        public bool UsedHerdr => false;

        public ViewAnswer Build(string path)
        {
            if (path.StartsWith("/detail", StringComparison.Ordinal))
            {
                _ = Interlocked.Increment(ref rig.Gated);
                gate.Wait(Deadline);
            }

            return new ViewAnswer(200, new JsonObject { ["view_schema"] = 1, ["path"] = path, ["rev"] = "r1" });
        }
    }

    private sealed class ProbeDirs : IStatusDirs
    {
        internal List<(bool Listing, bool Processes)> Seen { get; } = [];

        public bool Contains(string dir) => false;

        public IReadOnlyList<string> Snapshot()
        {
            lock (Seen)
            {
                Seen.Add((Ah.Core.Files.ListingScope.IsOpen, Ah.Core.Time.ProcessSnapshotScope.Active is not null));
            }

            return [];
        }

        public void Remove(string dir)
        {
        }

        public void Replace(IEnumerable<string> dirs)
        {
        }
    }

    [Fact]
    public async Task TheEventsRefreshReadsInsideAListingScopeAndAProcessSnapshotThatItsCallerNeverSees()
    {
        var rig = new Rig();
        var probe = new ProbeDirs();
        var s = new ViewService(new ViewServiceDependencies(null!, rig.Revs, probe, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())));

        _ = await View(s, "path=/events&wait=0");

        Assert.NotEmpty(probe.Seen);
        Assert.All(probe.Seen, static seen => Assert.Equal((true, true), seen));
        Assert.False(Ah.Core.Files.ListingScope.IsOpen);
        Assert.Null(Ah.Core.Time.ProcessSnapshotScope.Active);
    }

    /// <summary>A model that reports, from inside its build, whether a listing scope is open, which scope it is, and what a task it starts there sees later.</summary>
    private sealed class ScopeProbeModel(TaskCompletionSource<bool> inside, Task gate, List<object>? identities = null, TaskCompletionSource? childGate = null, List<Task<bool>>? children = null) : IViewModel
    {
        public bool UsedHerdr => false;

        public ViewAnswer Build(string path)
        {
            _ = inside.TrySetResult(Ah.Core.Files.ListingScope.IsOpen);
            if (identities is not null)
            {
                lock (identities)
                {
                    identities.Add(Ah.Core.Files.ListingScope.Once("probe", static () => new object()));
                }
            }

            if (childGate is not null && children is not null)
            {
                lock (children)
                {
                    children.Add(Task.Run(async () =>
                    {
                        await childGate.Task;
                        return Ah.Core.Files.ListingScope.IsOpen;
                    }));
                }
            }

            gate.Wait(Deadline);
            return new ViewAnswer(200, new JsonObject { ["view_schema"] = 1, ["path"] = path, ["rev"] = "r1" });
        }
    }

    [Fact]
    public async Task ABuildRunsInsideAListingScopeAndNoOtherCallInTheProcessSeesIt()
    {
        var rig = new Rig();
        var inside = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => new ScopeProbeModel(inside, release.Task));

        var page = View(s, "path=/");
        var openInBuild = await inside.Task.WaitAsync(Deadline, CancellationToken.None);
        var openInAGateCall = await Task.Run(static () => Ah.Core.Files.ListingScope.IsOpen);
        var openInTheCaller = Ah.Core.Files.ListingScope.IsOpen;
        release.SetResult();
        _ = await page;

        Assert.True(openInBuild);
        Assert.False(openInAGateCall);
        Assert.False(openInTheCaller);
        Assert.False(Ah.Core.Files.ListingScope.IsOpen);
    }

    [Fact]
    public async Task AScopeIsItsBuildsOwnTwoEpochsMakeTwoScopesAndATaskTheBuildStartedSeesNoneAfterItEnds()
    {
        var rig = new Rig();
        var identities = new List<object>();
        var childGate = new TaskCompletionSource();
        var children = new List<Task<bool>>();
        var s = new ViewService(
            new ViewServiceDependencies(null!, rig.Revs, rig.Dirs, rig.Clock, static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => new ScopeProbeModel(new TaskCompletionSource<bool>(), Task.CompletedTask, identities, childGate, children));
        _ = await View(s, "path=/");
        rig.Clock.Advance(ViewService.EpochMs);
        _ = await View(s, "path=/");

        childGate.SetResult();
        var seen = await Task.WhenAll(children).WaitAsync(Deadline, CancellationToken.None);

        Assert.Equal(2, identities.Count);
        Assert.NotSame(identities[0], identities[1]);
        Assert.Equal(2, children.Count);
        Assert.All(seen, static open => Assert.False(open));
    }

    [Fact]
    public async Task TheSeventeenthWaiterReleasesTheOldestAfterAPauseAndTheSixteenStayWaiting()
    {
        var rig = new Rig();
        var s = rig.Service();
        using var cts = new CancellationTokenSource();
        var waiters = new List<Task<RouteResponse>>();
        for (var i = 0; i < 16; i++)
        {
            waiters.Add(View(s, "path=/&since=r1&wait=25", cts.Token));
            await Task.Delay(20);
        }

        await Task.Delay(1500);
        var stillWaiting = waiters.Count(static w => !w.IsCompleted);
        var clock = Stopwatch.StartNew();
        waiters.Add(View(s, "path=/&since=r1&wait=25", cts.Token));
        var first = await waiters[0];
        var releasedAfter = clock.ElapsedMilliseconds;

        Assert.Equal(16, stillWaiting);
        Assert.True(Body(first)["unchanged"].AsBool());
        Assert.True(releasedAfter >= 900, $"released after {releasedAfter} ms");
        Assert.Equal(16, waiters.Skip(1).Count(static w => !w.IsCompleted));
        Assert.Equal(16, s.Waiting);

        cts.Cancel();
        _ = await Task.WhenAll(waiters).WaitAsync(Deadline, CancellationToken.None);
    }

    // ---------------------------------------------------------------- herdr

    [Fact]
    public async Task HerdrIsReadOnceForConcurrentBuildsAndNotAgainWhileTheReadingIsFresh()
    {
        var rig = new Rig { UseHerdr = true, BuildMs = 50 };
        rig.Herdr = async (_, token) =>
        {
            await Task.Delay(300, token);
            return new JsonArray();
        };
        var s = rig.Service();

        var both = await Task.WhenAll(View(s, "path=/"), View(s, "path=/"));
        var third = await View(s, "path=/");

        Assert.All(both, r => Assert.Equal("ok", Body(r)["herdr"].AsString()));
        Assert.Equal("ok", Body(third)["herdr"].AsString());
        Assert.Equal(1, Volatile.Read(ref rig.HerdrCalls));
    }

    [Fact]
    public async Task AHerdrThatHangsMakesTheViewSayUnknown()
    {
        var rig = new Rig { UseHerdr = true };
        rig.Herdr = async (_, token) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(5), token).ConfigureAwait(false);
            return new JsonArray();
        };

        var r = await View(rig.Service(), "path=/");

        Assert.Equal(WatchModule.HerdrUnreachable, Body(r)["herdr"].AsString());
    }

    [Fact]
    public async Task AFailedHerdrReadingIsKeptForTheTtlAndThenTriedAgain()
    {
        var rig = new Rig { UseHerdr = true };
        rig.Herdr = static (_, _) => throw new IOException("herdr gone");
        var s = rig.Service();

        _ = await View(s, "path=/");
        _ = await View(s, "path=/");
        rig.Clock.Advance(ViewService.EpochMs - 1);
        _ = await View(s, "path=/");
        var within = Volatile.Read(ref rig.HerdrCalls);
        rig.Clock.Advance(1);
        _ = await View(s, "path=/");

        Assert.True(HerdrCache.TtlMs < ViewService.EpochMs);
        Assert.Equal(1, within);
        Assert.Equal(2, Volatile.Read(ref rig.HerdrCalls));
    }

    [Fact]
    public void QueryStringsDecodeLikeUrlSearchParamsAndTheLastValueWins()
    {
        var q = QueryString.Parse("path=%2Fo%2Fx+y&since=a&since=b&flag&empty=");

        Assert.Equal("/o/x y", q["path"]);
        Assert.Equal("b", q["since"]);
        Assert.Equal(string.Empty, q["flag"]);
        Assert.Equal(string.Empty, q["empty"]);
        Assert.Empty(QueryString.Parse(string.Empty));
    }
}
