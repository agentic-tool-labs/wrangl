using System.Text;
using Ah.Abstractions;
using Ah.Core.Digests;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.State.Tests;

/// <summary>The native state client against a fake daemon and a fake engine: which path a write takes, and what each failure maps to.</summary>
public sealed class StateClientTests
{
    private static readonly RequestContext Context = new("/work/repo", "/work/repo/.claude/ah2", "/work/main/.claude/ah2", "/run/sock");

    private static readonly byte[] Args = "{\"path\":\"/work/x\",\"expect\":\"absent\",\"text\":\"hi\"}"u8.ToArray();

    private const string Healthy = "{\"impl\":\"native\",\"protocols\":[1],\"draining\":false}";

    private sealed class Rig
    {
        public FakeDaemonClient Daemon { get; } = new() { Info = FakeDaemonClient.Up() };

        public RecordingHookErrorLog Errors { get; } = new();

        public FakeStateDomain Domain { get; }

        public IStateWriter Client { get; }

        public List<(string Domain, string Op, string Args, string OpId, TimeSpan Wait)> EngineCalls { get; } = [];

        public Func<StateResult> EngineResult { get; set; } = () => new StateResult(true, "{\"engine\":true}"u8.ToArray());

        public ManualTime Time { get; } = new();

        public Rig(StateOp[]? ops = null)
        {
            Domain = new FakeStateDomain(
                "store",
                ops is { Length: > 0 } ? ops : [FakeStateDomain.Write("put"), FakeStateDomain.Write("local", daemon: false), FakeStateDomain.Read("get")]);
            Daemon.Reply = Route;
            Client = StateModule.CreateClient(
                [Domain],
                Daemon,
                Errors,
                (domain, op, args, _, opId, wait) =>
                {
                    EngineCalls.Add((domain, op, Encoding.UTF8.GetString(args.Span), opId, wait));
                    return EngineResult();
                },
                Time);
        }

        public Func<DaemonRequest, DaemonReply> Post { get; set; } = _ => Json(200, "{\"result\":{\"ok\":true}}");

        private DaemonReply Route(DaemonRequest request) =>
            request.PathAndQuery == "/v1/health" ? Json(200, Healthy) : Post(request);
    }

    /// <summary>A time source that moves only when the test says so, in whole milliseconds.</summary>
    private sealed class ManualTime : TimeProvider
    {
        private long _ms;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => _ms;

        public void Advance(TimeSpan by) => _ms += (long)by.TotalMilliseconds;
    }

    private static DaemonReply Json(int status, string body) => new(status, Encoding.UTF8.GetBytes(body));

    private static string Text(StateResult r) => Encoding.UTF8.GetString(r.ResultJson.Span);

    private static JsonObject Body(DaemonRequest r) => JsonText.Parse(r.Body.Span).AsObject();

    // ---------------------------------------------------------------- the daemon path

    [Fact]
    public void DaemonUpAndNativePostsOnceWithAnOperationIdAndNeverRunsTheEngine()
    {
        var rig = new Rig();
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.True(result.Ok);
        Assert.Equal("{\"ok\":true}", Text(result));
        var post = Assert.Single(rig.Daemon.Requests, r => r.Method == "POST");
        Assert.Equal("/v1/state/store/put", post.PathAndQuery);
        Assert.False(post.StartIfDown);
        var body = Body(post);
        Assert.True(Guid.TryParse(body["op_id"].AsString(), out _));
        Assert.Equal("hi", body["args"].AsObject()["text"].AsString());
        Assert.Equal("/work/repo/.claude/ah2", body["context"].AsObject()["hierarchy_dir"].AsString());
        Assert.Equal("/work/main/.claude/ah2", body["context"].AsObject()["main_hierarchy_dir"].AsString());
        Assert.Equal("/run/sock", body["context"].AsObject()["messaging_socket_dir"].AsString());
        Assert.Empty(rig.EngineCalls);
    }

    [Fact]
    public void AnAbsentDirectoryIsSentAsNullNotAsAnEmptyString()
    {
        var rig = new Rig();
        rig.Client.Invoke("store", "put", Args, new RequestContext("/work/repo", "/work/repo/.claude/ah2", string.Empty, string.Empty));
        var context = Body(Assert.Single(rig.Daemon.Requests, r => r.Method == "POST"))["context"].AsObject();
        Assert.True(context["main_hierarchy_dir"].IsNull);
        Assert.True(context["messaging_socket_dir"].IsNull);
        Assert.Equal("/work/repo", context["cwd"].AsString());
    }

    [Fact]
    public void EachCallGetsAFreshOperationIdAndAnExplicitOneIsKept()
    {
        var rig = new Rig();
        rig.Client.Invoke("store", "put", Args, Context);
        rig.Client.Invoke("store", "put", Args, Context);
        rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(OperationId: "fixed-id"));

        var ids = rig.Daemon.Requests.Where(r => r.Method == "POST").Select(r => Body(r)["op_id"].AsString()).ToList();
        Assert.Equal(3, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
        Assert.Equal("fixed-id", ids[2]);
    }

    [Fact]
    public void DaemonDownRunsTheEngineInProcessWithTheCallAndTheDefaultLockWait()
    {
        var rig = new Rig();
        rig.Daemon.Info = null;
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.True(result.Ok);
        var call = Assert.Single(rig.EngineCalls);
        Assert.Equal("store", call.Domain);
        Assert.Equal("put", call.Op);
        Assert.True(Guid.TryParse(call.OpId, out _));
        Assert.Equal(ILockProvider.CliWait, call.Wait);
        Assert.Equal("hi", JsonText.Parse(call.Args).AsObject()["text"].AsString());
        Assert.Empty(rig.Daemon.Requests);
    }

    [Theory]
    [InlineData("{\"impl\":\"node\",\"protocols\":[1],\"draining\":false}")]
    [InlineData("{\"protocols\":[1],\"draining\":false}")]
    [InlineData("{\"impl\":\"native\",\"protocols\":[1],\"draining\":true}")]
    [InlineData("{\"impl\":\"native\",\"protocols\":[2],\"draining\":false}")]
    [InlineData("{\"impl\":\"native\",\"protocols\":[],\"draining\":false}")]
    public void ADaemonThatIsNotAUsableNativeDaemonSendsTheWriteToTheEngine(string health)
    {
        var rig = new Rig();
        rig.Daemon.Reply = r => r.PathAndQuery == "/v1/health" ? Json(200, health) : throw new InvalidOperationException("must not POST");
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.True(result.Ok);
        Assert.DoesNotContain(rig.Daemon.Requests, r => r.Method == "POST");
        Assert.Single(rig.EngineCalls);
    }

    [Fact]
    public void ANativeDaemonThatListsProtocolOneAmongOthersIsUsed()
    {
        var rig = new Rig();
        rig.Daemon.Reply = r => r.PathAndQuery == "/v1/health"
            ? Json(200, "{\"impl\":\"native\",\"protocols\":[0,1,2],\"draining\":false}")
            : Json(200, "{\"result\":{\"ok\":true}}");
        Assert.True(rig.Client.Invoke("store", "put", Args, Context).Ok);
        Assert.Empty(rig.EngineCalls);
    }

    [Fact]
    public void AHealthAnswerThatIsNot200SendsTheWriteToTheEngine()
    {
        var rig = new Rig();
        rig.Daemon.Reply = r => r.PathAndQuery == "/v1/health" ? Json(503, Healthy) : throw new InvalidOperationException();
        Assert.True(rig.Client.Invoke("store", "put", Args, Context).Ok);
        Assert.Single(rig.EngineCalls);
    }

    [Theory]
    [InlineData(413, "{\"error\":{\"code\":\"too_large\",\"message\":\"body too large\"}}", "too_large")]
    [InlineData(400, "{\"error\":{\"code\":\"bad_request\",\"message\":\"nope\"}}", "bad_request")]
    [InlineData(404, "{}", "http_404")]
    [InlineData(500, "{\"error\":{\"code\":\"internal\",\"message\":\"internal error\"}}", "internal")]
    public void ARouteFailureIsFinalAndNeverFallsBackToTheEngine(int status, string body, string code)
    {
        var rig = new Rig { Post = _ => Json(status, body) };
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.False(result.Ok);
        Assert.Equal(code, result.ErrorCode);
        Assert.Empty(rig.EngineCalls);
    }

    [Fact]
    public void ATooLargeReplyNamesTheRouteAndKeepsTheCap()
    {
        var rig = new Rig { Post = _ => Json(413, "{\"error\":{\"code\":\"too_large\",\"message\":\"body is 70000 bytes; the limit is 65536\"}}") };
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.Equal("POST /v1/state/store/put: body is 70000 bytes; the limit is 65536", result.Error);
    }

    [Fact]
    public void OpFailedCarriesTheOpsOwnErrorCode()
    {
        var rig = new Rig { Post = _ => Json(422, "{\"error\":{\"code\":\"op_failed\",\"message\":\"bad edit\",\"error_code\":\"stale_digest\"}}") };
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.Equal("stale_digest", result.ErrorCode);
        Assert.Equal("bad edit", result.Error);
    }

    [Fact]
    public void OnlyA422OpFailedIsRemappedToTheOpsOwnCode()
    {
        var rig = new Rig { Post = _ => Json(500, "{\"error\":{\"code\":\"op_failed\",\"message\":\"x\",\"error_code\":\"stale_digest\"}}") };
        Assert.Equal("op_failed", rig.Client.Invoke("store", "put", Args, Context).ErrorCode);
    }

    [Fact]
    public void OpFailedWithoutAnOpCodeKeepsOpFailed()
    {
        var rig = new Rig { Post = _ => Json(422, "{\"error\":{\"code\":\"op_failed\",\"message\":\"bad edit\"}}") };
        Assert.Equal("op_failed", rig.Client.Invoke("store", "put", Args, Context).ErrorCode);
    }

    [Fact]
    public void AnUnreadableReplyBodyIsAnInternalFailure()
    {
        var rig = new Rig { Post = _ => Json(200, "<html>") };
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.False(result.Ok);
        Assert.Equal("internal", result.ErrorCode);
        Assert.Empty(rig.EngineCalls);
    }

    [Fact]
    public void AHookWriteWhoseDaemonDoesNotAnswerInTimeIsWrittenInProcess()
    {
        var rig = new Rig();
        rig.Daemon.Hang = true;
        var options = new StateCallOptions(Hook: true, Budget: TimeSpan.FromMilliseconds(1500));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = rig.Client.Invoke("store", "put", Args, Context, options);

        Assert.True(result.Ok);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(3), $"took {timer.Elapsed}");
        var call = Assert.Single(rig.EngineCalls);
        Assert.Equal("put", call.Op);
    }

    [Fact]
    public void AHookWriteThatTheDaemonRejectsWith500IsWrittenInProcessUnderTheSameOpId()
    {
        var rig = new Rig { Post = _ => Json(500, "{\"error\":{\"code\":\"internal\",\"message\":\"boom\"}}") };

        var result = rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true));

        Assert.True(result.Ok);
        var sent = Body(Assert.Single(rig.Daemon.Requests, r => r.Method == "POST"))["op_id"].AsString();
        Assert.Equal(sent, Assert.Single(rig.EngineCalls).OpId);
    }

    [Fact]
    public void AHookWriteThatFailsBothWaysReportsBothOutcomes()
    {
        var rig = new Rig { Post = _ => Json(500, "{\"error\":{\"code\":\"internal\",\"message\":\"daemon boom\"}}") };
        rig.EngineResult = () => StateRules.Fail("lock_timeout", "engine boom");

        var result = rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true));

        Assert.False(result.Ok);
        Assert.Contains("daemon boom", result.Error, StringComparison.Ordinal);
        Assert.Contains("engine boom", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A4xxStillFailsForAHookWrite()
    {
        var rig = new Rig { Post = _ => Json(400, "{\"error\":{\"code\":\"bad_request\",\"message\":\"nope\"}}") };

        var result = rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true));

        Assert.False(result.Ok);
        Assert.Equal("bad_request", result.ErrorCode);
        Assert.Empty(rig.EngineCalls);
    }

    [Fact]
    public void ACliWriteThatTimedOutButLandedIsReportedAsConfirmedAfterTimeout()
    {
        var rig = new Rig { Post = _ => throw new OperationCanceledException() };

        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.True(result.Ok);
        Assert.Equal("confirmed after timeout", result.Error);
        var call = Assert.Single(rig.EngineCalls);
        Assert.Equal(Body(Assert.Single(rig.Daemon.Requests, r => r.Method == "POST"))["op_id"].AsString(), call.OpId);
        Assert.Equal(ILockProvider.CliWait, call.Wait);
    }

    [Fact]
    public void ACliWriteThatTimedOutAndCannotBeConfirmedStillFailsAsATimeout()
    {
        var rig = new Rig
        {
            Post = _ => throw new OperationCanceledException(),
            EngineResult = () => new StateResult(false, default, "busy", "lock_timeout"),
        };

        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.False(result.Ok);
        Assert.Equal("state_timeout", result.ErrorCode);
    }

    [Fact]
    public void ADaemonThatCannotBeReachedFallsBackToTheEngine()
    {
        var rig = new Rig();
        rig.Daemon.ThrowOnDiscover = new IOException("socket gone");
        Assert.True(rig.Client.Invoke("store", "put", Args, Context).Ok);
        Assert.Single(rig.EngineCalls);
    }

    // ---------------------------------------------------------------- budgets

    [Fact]
    public void HookModeCapsTheDaemonWaitAtThreeHundredMillisecondsAndGivesTheEngineTheRemainingBudgetAsItsLockWait()
    {
        var rig = new Rig();
        rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true));
        var post = Assert.Single(rig.Daemon.Requests, r => r.Method == "POST");
        Assert.Equal(TimeSpan.FromMilliseconds(300), post.Timeout);

        var down = new Rig();
        down.Daemon.Info = null;
        down.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true));
        var call = Assert.Single(down.EngineCalls);
        Assert.InRange(call.Wait, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1500));
    }

    [Fact]
    public void ABudgetUnderThreeHundredMillisecondsShortensTheDaemonWaitToTheBudget()
    {
        var rig = new Rig();
        rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true, Budget: TimeSpan.FromMilliseconds(120)));
        Assert.Equal(TimeSpan.FromMilliseconds(120), Assert.Single(rig.Daemon.Requests, r => r.Method == "POST").Timeout);
    }

    [Fact]
    public void TheDaemonWaitOutlastsTheLockWaitTheDaemonItselfMakes()
    {
        Assert.Equal(ILockProvider.CliWait + TimeSpan.FromSeconds(1), ILockProvider.DaemonWait);
        Assert.True(ILockProvider.DaemonWait > ILockProvider.CliWait);
        Assert.True(ILockProvider.DaemonWait > ILockProvider.StaleAfter);
    }

    [Fact]
    public void WithoutHookModeTheDaemonGetsTheLongWaitAndTheEngineTheCliLockWait()
    {
        var rig = new Rig();
        rig.Client.Invoke("store", "put", Args, Context);
        Assert.Equal(ILockProvider.DaemonWait, Assert.Single(rig.Daemon.Requests, r => r.Method == "POST").Timeout);

        var down = new Rig();
        down.Daemon.Info = null;
        down.Client.Invoke("store", "put", Args, Context);
        Assert.Equal(ILockProvider.CliWait, Assert.Single(down.EngineCalls).Wait);
    }

    [Fact]
    public void AnExhaustedHookBudgetStillWritesOnceInProcessAndLeavesNoRow()
    {
        var rig = new Rig();
        rig.Daemon.Info = null;
        var result = rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true, Budget: TimeSpan.Zero));

        Assert.True(result.Ok);
        Assert.Equal(StateClient.HookWriteFloor, Assert.Single(rig.EngineCalls).Wait);
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void NegativeAWriteThatFailsPastItsBudgetIsARowNamingTheOperation()
    {
        var rig = new Rig { EngineResult = static () => new StateResult(false, default, "the lock did not clear", "state_timeout") };
        rig.Daemon.Info = null;
        var result = rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true, Budget: TimeSpan.Zero));

        Assert.Equal("state_timeout", result.ErrorCode);
        var logged = Assert.Single(rig.Errors.Records);
        Assert.Equal("state-client", logged.Hook);
        Assert.Contains("store.put was not written", logged.Message, StringComparison.Ordinal);
        Assert.Contains("the lock did not clear", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAFailedWriteWithBudgetLeftKeepsThatWaitAndLeavesItToTheCaller()
    {
        var rig = new Rig { EngineResult = static () => new StateResult(false, default, "refused", "bad_request") };
        rig.Daemon.Info = null;
        var result = rig.Client.Invoke("store", "put", Args, Context, new StateCallOptions(Hook: true, Budget: TimeSpan.FromMilliseconds(1000)));

        Assert.Equal("bad_request", result.ErrorCode);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), Assert.Single(rig.EngineCalls).Wait);
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void AnOpThatSkipsTheDaemonRunsTheEngineEvenWhenADaemonIsUp()
    {
        var rig = new Rig();
        Assert.True(rig.Client.Invoke("store", "local", Args, Context).Ok);
        Assert.Equal(0, rig.Daemon.DiscoverCalls);
        Assert.Single(rig.EngineCalls);
    }

    [Fact]
    public void AnEngineFailureIsReturnedAsItIsAndRunsNoAfterWriteHook()
    {
        var rig = new Rig();
        rig.Daemon.Info = null;
        rig.EngineResult = () => new StateResult(false, ReadOnlyMemory<byte>.Empty, "the put failed", "stale_target");
        var result = rig.Client.Invoke("store", "put", Args, Context);

        Assert.False(result.Ok);
        Assert.Equal("stale_target", result.ErrorCode);
        Assert.Equal("the put failed", result.Error);
        Assert.Empty(rig.Domain.AfterWrites);
    }

    // ---------------------------------------------------------------- reads, preflight, after-write

    [Fact]
    public void AReadOpRunsItsBodyInProcessWithoutAnyTransport()
    {
        var rig = new Rig();
        var result = rig.Client.Invoke("store", "get", "{}"u8.ToArray(), Context);

        Assert.True(result.Ok);
        Assert.Equal("{\"read\":true}", Text(result));
        Assert.Empty(rig.Daemon.Requests);
        Assert.Empty(rig.EngineCalls);
        Assert.Equal(0, rig.Daemon.DiscoverCalls);
    }

    [Fact]
    public void AFailedReadBodyIsAnOpFailedWithTheOpsCodeWhenItHasOne()
    {
        var plain = new Rig([FakeStateDomain.Read("get", _ => new OpResult(false, default, "boom"))]);
        var result = plain.Client.Invoke("store", "get", "{}"u8.ToArray(), Context);
        Assert.False(result.Ok);
        Assert.Equal("op_failed", result.ErrorCode);
        Assert.Equal("boom", result.Error);

        var coded = new Rig([FakeStateDomain.Read("get", _ => new OpResult(false, default, "boom", "missing_thing"))]);
        Assert.Equal("missing_thing", coded.Client.Invoke("store", "get", "{}"u8.ToArray(), Context).ErrorCode);
    }

    [Fact]
    public void UnknownOpsAndUnparseableArgsAreBadRequestsWithNothingSent()
    {
        var rig = new Rig();
        Assert.Equal("bad_request", rig.Client.Invoke("store", "nope", Args, Context).ErrorCode);
        Assert.Equal("bad_request", rig.Client.Invoke("elsewhere", "put", Args, Context).ErrorCode);
        Assert.Equal("bad_request", rig.Client.Invoke("store", "put", "{nope"u8.ToArray(), Context).ErrorCode);
        Assert.Empty(rig.Daemon.Requests);
        Assert.Empty(rig.EngineCalls);
        Assert.Equal(0, rig.Daemon.DiscoverCalls);
    }

    [Fact]
    public void EmptyArgsAreAnEmptyObject()
    {
        var rig = new Rig();
        Assert.True(rig.Client.Invoke("store", "put", default, Context).Ok);
        var args = Body(Assert.Single(rig.Daemon.Requests, r => r.Method == "POST"))["args"].AsObject();
        Assert.Empty(args.Keys);
    }

    [Theory]
    [InlineData("{\"expect\":\"absent\"}", false)]
    [InlineData("{\"path\":\"\",\"expect\":\"absent\"}", false)]
    [InlineData("{\"path\":5,\"expect\":\"absent\"}", false)]
    [InlineData("{\"path\":\"/f\"}", false)]
    [InlineData("{\"path\":\"/f\",\"expect\":\"ABSENT\"}", false)]
    [InlineData("{\"path\":\"/f\",\"expect\":\"abc\"}", false)]
    [InlineData("{\"path\":\"/f\",\"expect\":7}", false)]
    [InlineData("{\"path\":\"/f\",\"expect\":\"absent\"}", true)]
    public void ADigestPreconditionOpNeedsAPathAndAWellFormedExpect(string args, bool accepted)
    {
        var rig = new Rig([FakeStateDomain.Write("cas", digestPrecondition: true)]);
        var result = rig.Client.Invoke("store", "cas", Encoding.UTF8.GetBytes(args), Context);

        Assert.Equal(accepted, result.Ok);
        if (!accepted)
        {
            Assert.Equal("bad_request", result.ErrorCode);
            Assert.Empty(rig.Daemon.Requests);
        }
    }

    [Fact]
    public void ADigestPreconditionOpAcceptsASixtyFourHexDigestAndRejectsOneCharacterMoreOrUppercase()
    {
        var rig = new Rig([FakeStateDomain.Write("cas", digestPrecondition: true)]);
        var digest = ContentDigest.OfBytes("abc"u8);
        Assert.True(rig.Client.Invoke("store", "cas", Encoding.UTF8.GetBytes($"{{\"path\":\"/f\",\"expect\":\"{digest}\"}}"), Context).Ok);
        Assert.False(rig.Client.Invoke("store", "cas", Encoding.UTF8.GetBytes($"{{\"path\":\"/f\",\"expect\":\"{digest}0\"}}"), Context).Ok);
        Assert.False(rig.Client.Invoke("store", "cas", Encoding.UTF8.GetBytes($"{{\"path\":\"/f\",\"expect\":\"{digest.ToUpperInvariant()}\"}}"), Context).Ok);
    }

    [Fact]
    public void AnOpWithoutADigestPreconditionIsNotCheckedForOne()
    {
        var rig = new Rig();
        Assert.True(rig.Client.Invoke("store", "put", "{\"text\":\"x\"}"u8.ToArray(), Context).Ok);
    }

    [Fact]
    public void AWriteTheDaemonAnsweredRunsNoAfterWriteHereTheDaemonRanIt()
    {
        var rig = new Rig();
        Assert.True(rig.Client.Invoke("store", "put", Args, Context).Ok);
        Assert.Single(rig.Daemon.Requests, static r => r.PathAndQuery != "/v1/health");
        Assert.Empty(rig.EngineCalls);
        Assert.Empty(rig.Domain.AfterWrites);
    }

    [Fact]
    public void AnInProcessWriteRunsAfterWriteOnceOnSuccessWithTheResultAndNotOnFailure()
    {
        var rig = new Rig();
        rig.Daemon.Info = null;
        rig.Client.Invoke("store", "put", Args, Context);
        var (call, result) = Assert.Single(rig.Domain.AfterWrites);
        Assert.Equal("put", call.Op);
        Assert.Equal("{\"engine\":true}", Encoding.UTF8.GetString(result.ResultJson.Span));

        rig.EngineResult = () => StateRules.Fail("state_failed", "x");
        rig.Client.Invoke("store", "put", Args, Context);
        Assert.Single(rig.Domain.AfterWrites);
    }

    [Fact]
    public void AfterWriteDoesNotRunForReads()
    {
        var rig = new Rig();
        rig.Client.Invoke("store", "get", "{}"u8.ToArray(), Context);
        Assert.Empty(rig.Domain.AfterWrites);
    }

    // ---------------------------------------------------------------- Update

    private static StateUpdatePlan Plan(Func<StateDocument>? read = null, Func<StateDocument, ReadOnlyMemory<byte>?>? mutate = null, List<string>? digests = null) =>
        new(
            read ?? (() => new StateDocument(null, "absent")),
            mutate ?? (_ => "\"changed\""u8.ToArray()),
            (outcome, digest) =>
            {
                digests?.Add(digest);
                return new StateCallSpec("put", Encoding.UTF8.GetBytes($"{{\"path\":\"/f\",\"expect\":\"{digest}\",\"text\":{Encoding.UTF8.GetString(outcome.Span)}}}"));
            });

    [Fact]
    public void UpdateWritesTheMutationWithTheDigestItReadAsThePrecondition()
    {
        var rig = new Rig([FakeStateDomain.Write("put", digestPrecondition: true)]);
        var digest = ContentDigest.OfBytes("old"u8);
        var plan = Plan(read: () => new StateDocument("old"u8.ToArray(), digest));
        var outcome = rig.Client.Update("store", plan, Context);

        Assert.True(outcome.Changed);
        Assert.True(outcome.Result.Ok);
        var args = Body(Assert.Single(rig.Daemon.Requests, r => r.Method == "POST"))["args"].AsObject();
        Assert.Equal(digest, args["expect"].AsString());
        Assert.Equal("changed", args["text"].AsString());
    }

    [Fact]
    public void UpdateWithNoMutationWritesNothing()
    {
        var rig = new Rig();
        var outcome = rig.Client.Update("store", Plan(mutate: _ => null), Context);

        Assert.False(outcome.Changed);
        Assert.True(outcome.Result.Ok);
        Assert.Empty(rig.Daemon.Requests);
        Assert.Empty(rig.EngineCalls);
    }

    [Fact]
    public void UpdateRereadsAndRetriesOnConflictAndSucceedsOnTheThirdAttempt()
    {
        var rig = new Rig();
        var attempts = 0;
        rig.Post = _ => ++attempts < 3 ? Json(200, "{\"result\":{\"conflict\":true}}") : Json(200, "{\"result\":{\"ok\":true}}");
        var reads = 0;
        var digests = new List<string>();
        var plan = Plan(read: () => new StateDocument(null, $"{++reads:x64}"), digests: digests);
        var outcome = rig.Client.Update("store", plan, Context);

        Assert.True(outcome.Changed);
        Assert.Equal(3, reads);
        Assert.Equal(3, attempts);
        Assert.Equal(3, digests.Distinct().Count());

        // the daemon answered every attempt, so it ran the AfterWrite
        Assert.Empty(rig.Domain.AfterWrites);
    }

    [Fact]
    public void UpdateGivesUpAfterFiveConflictsWithTheConflictCode()
    {
        var rig = new Rig { Post = _ => Json(200, "{\"result\":{\"conflict\":true}}") };
        var reads = 0;
        var outcome = rig.Client.Update("store", Plan(read: () =>
        {
            reads++;
            return new StateDocument(null, "absent");
        }), Context);

        Assert.False(outcome.Changed);
        Assert.False(outcome.Result.Ok);
        Assert.Equal("conflict", outcome.Result.ErrorCode);
        Assert.Equal(5, reads);
        Assert.Equal(5, rig.Daemon.Requests.Count(r => r.Method == "POST"));
        Assert.Empty(rig.Domain.AfterWrites);
    }

    [Fact]
    public void UpdateDoesNotRetryAFailureThatIsNotAConflict()
    {
        var rig = new Rig { Post = _ => Json(422, "{\"error\":{\"code\":\"op_failed\",\"message\":\"x\",\"error_code\":\"bad_edit\"}}") };
        var reads = 0;
        var outcome = rig.Client.Update("store", Plan(read: () =>
        {
            reads++;
            return new StateDocument(null, "absent");
        }), Context);

        Assert.False(outcome.Changed);
        Assert.Equal("bad_edit", outcome.Result.ErrorCode);
        Assert.Equal(1, reads);
    }

    [Fact]
    public void UpdateGivesEachAttemptAFreshOperationIdEvenWhenTheCallerNamedOne()
    {
        var rig = new Rig();
        var attempts = 0;
        rig.Post = _ => ++attempts < 2 ? Json(200, "{\"result\":{\"conflict\":true}}") : Json(200, "{\"result\":{}}");
        rig.Client.Update("store", Plan(), Context, new StateCallOptions(OperationId: "caller-id"));

        var ids = rig.Daemon.Requests.Where(r => r.Method == "POST").Select(r => Body(r)["op_id"].AsString()).ToList();
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
        Assert.DoesNotContain("caller-id", ids);
    }

    [Fact]
    public void UpdateInHookModeStillTriesWhenTheBudgetIsGone()
    {
        var rig = new Rig { Post = static _ => Json(200, "{\"result\":{}}") };
        var outcome = rig.Client.Update("store", Plan(), Context, new StateCallOptions(Hook: true, Budget: TimeSpan.Zero));

        Assert.True(outcome.Changed);
        Assert.Equal(StateClient.HookWriteFloor, Assert.Single(rig.Daemon.Requests, static r => r.Method == "POST").Timeout);
        Assert.Empty(rig.Errors.Records);
    }

    [Fact]
    public void UpdateInHookModeSpendsTheRemainingBudgetNotTheWholeOneOnEachAttempt()
    {
        var rig = new Rig();
        var attempts = 0;
        rig.Post = _ =>
        {
            if (++attempts == 1)
            {
                rig.Time.Advance(TimeSpan.FromMilliseconds(250));
                return Json(200, "{\"result\":{\"conflict\":true}}");
            }

            return Json(200, "{\"result\":{}}");
        };
        rig.Client.Update("store", Plan(), Context, new StateCallOptions(Hook: true, Budget: TimeSpan.FromMilliseconds(450)));

        var waits = rig.Daemon.Requests.Where(r => r.Method == "POST").Select(r => r.Timeout!.Value).ToList();
        Assert.Equal(2, waits.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(300), waits[0]);
        Assert.Equal(TimeSpan.FromMilliseconds(200), waits[1]);
    }

    [Fact]
    public void NegativeUpdateInHookModeStopsAtItsAttemptCapPastTheBudget()
    {
        var rig = new Rig();
        rig.Post = _ =>
        {
            rig.Time.Advance(TimeSpan.FromMilliseconds(450));
            return Json(200, "{\"result\":{\"conflict\":true}}");
        };
        var outcome = rig.Client.Update("store", Plan(), Context, new StateCallOptions(Hook: true, Budget: TimeSpan.FromMilliseconds(450)));

        Assert.False(outcome.Changed);
        Assert.Equal("conflict", outcome.Result.ErrorCode);
        Assert.Equal(StateClient.UpdateAttempts, rig.Daemon.Requests.Count(static r => r.Method == "POST"));
    }
}
