using System.Text;
using Ah.Abstractions;
using Ah.Platform;
using Ah.State;
using Ah.TestKit;

namespace Ah.Daemon.Tests;

/// <summary>The <c>/v1/state/{domain}/{op}</c> route over a real socket, against the native engine and a fake domain.</summary>
public sealed class StateRouteTests
{
    private sealed class Domain(string name, string directory, params StateOp[] ops) : IStateDomain
    {
        public string Name { get; } = name;

        public IReadOnlyList<string> StoreNames => [];

        public IReadOnlyList<StateOp> Ops { get; } = ops;

        public List<string> AfterWrites { get; } = [];

        public string StateDirectory(RequestContext context) => context.HierarchyDirectory + "/" + directory;

        public void AfterWrite(OpCall operation, OpResult result)
        {
            lock (AfterWrites)
            {
                AfterWrites.Add(operation.Op);
            }
        }
    }

    private static StateOp WriteOp(string name, Action? during = null) =>
        new(name, OpKind.Write, OpStyle.Overwrite, false, null, true, (call, ctx) =>
        {
            during?.Invoke();
            ctx!.Write(call.Context.HierarchyDirectory + "/" + name + ".json", call.Context.HierarchyDirectory + "/" + name + ".tmp", "{}"u8);
            return new OpResult(true, "{\"written\":true}"u8.ToArray());
        });

    private static StateOp ReadOp(string name) =>
        new(name, OpKind.Read, null, false, null, true, (call, _) => new OpResult(true, Encoding.UTF8.GetBytes("{\"args\":" + Encoding.UTF8.GetString(call.ArgsJson.Span) + "}")));

    private static (IStateService Service, string Hierarchy) Compose(Sandbox box, params Domain[] domains)
    {
        var io = PlatformModule.CreateIo(null);
        var env = new FakeEnvironment(new Dictionary<string, string> { ["AH_HOME"] = box.AhHome, ["HOME"] = box.Root });
        var hierarchy = box.Root + "/proj/.claude/ah2";
        Directory.CreateDirectory(hierarchy);
        Directory.CreateDirectory(box.Root + "/proj/.git");
        var engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, io.Clock, domains, 4242);
        return (StateModule.CreateService(
            domains, engine, io.Files, PlatformModule.CurrentUid(), Ah.Config.ConfigModule.CreateHierarchyDirPolicy(io.Files, env, null, () => PlatformModule.CurrentUid()), null), hierarchy);
    }

    private static string Body(string args, string hierarchy, string? opId) =>
        "{\"args\":" + args + ",\"context\":{\"cwd\":\"" + hierarchy + "\",\"hierarchy_dir\":\"" + hierarchy + "\"}" + (opId is null ? string.Empty : ",\"op_id\":\"" + opId + "\"") + "}";

    [Fact]
    public async Task AWriteAnswers200AndRunsTheDomainsHookAndARepeatedOpIdRunsOnce()
    {
        var box = Sandbox.Create();
        var domain = new Domain("store", "s", WriteOp("put"));
        var (service, hierarchy) = Compose(box, domain);
        await using var h = await Harness.StartAsync(box, o => o.State = service);

        var first = await h.PostAsync("/v1/state/store/put", Body("{}", hierarchy, "op-1"));
        Assert.Equal(200, first.Status);
        Assert.Equal("{\"result\":{\"written\":true}}", first.Body);
        Assert.True(File.Exists(hierarchy + "/put.json"));
        var again = await h.PostAsync("/v1/state/store/put", Body("{}", hierarchy, "op-1"));
        Assert.Equal(first.Body, again.Body);
        Assert.Equal(["put", "put"], domain.AfterWrites);

        var log = File.ReadAllLines(hierarchy + "/s/.executed-ops.jsonl");
        Assert.Single(log);
    }

    [Fact]
    public async Task AReadThroughGetDecodesItsQueryAndAnUnknownOpIs404()
    {
        var box = Sandbox.Create();
        var domain = new Domain("store", "s", ReadOp("peek"));
        var (service, hierarchy) = Compose(box, domain);
        await using var h = await Harness.StartAsync(box, o => o.State = service);

        var args = Uri.EscapeDataString("{\"a\":\"b c\"}");
        var read = await h.GetAsync("/v1/state/store/peek?args=" + args + "&cwd=" + Uri.EscapeDataString(hierarchy) + "&hierarchy_dir=" + Uri.EscapeDataString(hierarchy));
        Assert.Equal(200, read.Status);
        Assert.Equal("{\"result\":{\"args\":{\"a\":\"b c\"}}}", read.Body);

        var missing = await h.PostAsync("/v1/state/store/nope", Body("{}", hierarchy, "op-1"));
        Assert.Equal(404, missing.Status);
        Assert.Equal("{\"error\":{\"code\":\"not_found\",\"message\":\"no state operation store.nope\"}}", missing.Body);
    }

    [Fact]
    public async Task TheOpLedgerNamesARegisteredWriteAndNotAnUnknownOp()
    {
        var box = Sandbox.Create();
        var (service, hierarchy) = Compose(box, new Domain("ledgerstore", "s", WriteOp("put")));
        await using var h = await Harness.StartAsync(box, o => o.State = service);
        Assert.Equal(200, (await h.PostAsync("/v1/state/ledgerstore/put", Body("{}", hierarchy, "op-1"))).Status);
        Assert.Equal(404, (await h.PostAsync("/v1/state/ledgerstore/nope", Body("{}", hierarchy, "op-2"))).Status);

        // the totals only grow, so another daemon's minute line in a parallel test cannot take these counts
        Assert.Equal(1, StateModule.OpCount("ledgerstore.put"));
        Assert.Equal(0, StateModule.OpCount("ledgerstore.nope"));
        var seen = new Dictionary<string, (long Runs, long Ticks)>();
        Assert.Contains("ledgerstore.put n=1 ", StateModule.TakeOpLine(seen), StringComparison.Ordinal);
        Assert.DoesNotContain("ledgerstore.put", StateModule.TakeOpLine(seen) ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnsupportedMethodIs405AndTheRouteIsAbsentWithoutAStateService()
    {
        var box = Sandbox.Create();
        var (service, hierarchy) = Compose(box, new Domain("store", "s", WriteOp("put")));
        await using var h = await Harness.StartAsync(box, o => o.State = service);
        var put = await h.SendAsync("PUT", "/v1/state/store/put", Body("{}", hierarchy, "op-1"));
        Assert.Equal(405, put.Status);
        Assert.Equal("GET, POST", put.Headers["Allow"]);

        await using var bare = await Harness.StartAsync(Sandbox.Create());
        var none = await bare.PostAsync("/v1/state/store/put", "{}");
        Assert.Equal(404, none.Status);
    }

    [Fact]
    public async Task AWriteWaitingInOneDomainDoesNotBlockAnotherDomainsWrite()
    {
        var box = Sandbox.Create();
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var slow = new Domain("slow", "slow", WriteOp("put", () =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        }));
        var fast = new Domain("fast", "fast", WriteOp("put"));
        var (service, hierarchy) = Compose(box, slow, fast);
        await using var h = await Harness.StartAsync(box, o => o.State = service);

        var blocked = h.PostAsync("/v1/state/slow/put", Body("{}", hierarchy, "op-slow"), TimeSpan.FromSeconds(30));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var quick = await h.PostAsync("/v1/state/fast/put", Body("{}", hierarchy, "op-fast"), TimeSpan.FromSeconds(5));
        Assert.Equal(200, quick.Status);
        Assert.False(blocked.IsCompleted);
        release.Set();
        Assert.Equal(200, (await blocked.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Status);
        var health = await h.GetAsync("/v1/health");
        Assert.Equal(200, health.Status);
    }

    [Fact]
    public async Task AWriteQueuedBehindAWedgedWorkerAnswersBusyAtOnceAndTheDaemonLogNamesIt()
    {
        var box = Sandbox.Create();
        using var release = new ManualResetEventSlim(false);
        using var started = new ManualResetEventSlim(false);
        var slow = WriteOp("slow", () =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        });
        var bounded = WriteOp("bounded") with { BusyAfter = TimeSpan.FromMilliseconds(200) };
        var domain = new Domain("store", "s", slow, bounded);
        var (service, hierarchy) = Compose(box, domain);
        await using var h = await Harness.StartAsync(box, o => o.State = service);

        var blocker = h.PostAsync("/v1/state/store/slow", Body("{}", hierarchy, "op-slow"), TimeSpan.FromSeconds(30));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var refused = await h.PostAsync("/v1/state/store/bounded", Body("{\"dir\":\"" + hierarchy + "\"}", hierarchy, "op-bounded"), TimeSpan.FromSeconds(5));
        clock.Stop();

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"refusal took {clock.Elapsed}");
        Assert.Equal(422, refused.Status);
        Assert.Contains("\"error_code\":\"busy\"", refused.Body, StringComparison.Ordinal);
        Assert.Contains("refused busy (pending on store worker: 2)", h.Log.ToString(), StringComparison.Ordinal);

        var spoofed = await h.PostAsync("/v1/state/store/bounded", Body("{\"dir\":\"" + hierarchy + "/x\\ny forged line\"}", hierarchy, "op-spoof"), TimeSpan.FromSeconds(5));
        Assert.Equal(422, spoofed.Status);
        var logged = h.Log.ToString().Split('\n');
        Assert.Single(logged, l => l.Contains("refused busy", StringComparison.Ordinal) && l.Contains("forged line", StringComparison.Ordinal));
        Assert.DoesNotContain(logged, l => l.StartsWith("y forged line", StringComparison.Ordinal));

        release.Set();
        Assert.Equal(200, (await blocker.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).Status);
        Assert.False(File.Exists(hierarchy + "/bounded.json"));
        Assert.DoesNotContain("bounded", domain.AfterWrites);
    }
}
