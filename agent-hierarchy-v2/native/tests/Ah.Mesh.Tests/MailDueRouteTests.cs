using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary><c>GET /v1/due</c>: answered at once when something is due, otherwise held until mail lands or the wait ends.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailDueRouteTests
{
    private const double Now = 1_790_000_000_000;

    private static readonly DateTimeOffset Clock = DateTimeOffset.FromUnixTimeMilliseconds((long)Now);

    /// <summary>
    /// The upper bound on an answer that must not have waited out the wait limit. The limit asked for is 25 s, so a request held to
    /// its limit overshoots this by far; anything shorter is a latency claim a loaded machine cannot keep.
    /// </summary>
    private static readonly TimeSpan HeldTooLong = TimeSpan.FromSeconds(10);

    private sealed class Registry : IRouteRegistry
    {
        internal Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> Routes { get; } = new(StringComparer.Ordinal);

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) => Routes.Add(pattern, handler);
    }

    private sealed class Rig
    {
        internal Rig(bool registered = true)
        {
            var sandbox = UnitSandbox.Create();
            Home = sandbox.Root + "/ah";
            Directory.CreateDirectory(Home + "/registrations");
            Dir = Home + "/mailbox/t~h/imp";
            Directory.CreateDirectory(Dir);
            var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = Home };
            Fx = MeshFixture.Create(vars, Clock, sandbox.Root);
            var due = MeshModule.CreateMailDue(Fx.Mailbox, Fx.Core, Fx.Files, Fx.Environment);
            MeshModule.RegisterDueRoute(Registry, due, Fx.Mailbox, Fx.Files, log: Logged.Add);
            if (registered)
            {
                Register();
            }
        }

        internal MeshFixture Fx { get; }

        internal List<string> Logged { get; } = [];

        internal Registry Registry { get; } = new();

        internal string Home { get; }

        internal string Dir { get; }

        internal void Register() =>
            File.WriteAllText(
                Home + "/registrations/s1.json",
                "{\"session_id\":\"s1\",\"address\":\"imp@tm\",\"addresses\":[\"imp@tm\"],\"boxes\":{\"imp@tm\":\"" + Dir + "\"},\"pid\":"
                + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ",\"pid_start\":\"" + Fx.OwnStart + "\",\"bound\":true}");

        internal void Append(string id, string route = "pull", string created = "2026-10-12T00:00:00.000Z") =>
            File.AppendAllText(
                Dir + "/entries.jsonl",
                "{\"id\":\"" + id + "\",\"kind\":\"note\",\"from\":\"user\",\"created\":\"" + created + "\",\"text\":\"hi\",\"route\":\"" + route + "\"}\n");

        internal void AppendFromPeer(string id, string kind, string text) =>
            File.AppendAllText(
                Dir + "/entries.jsonl",
                "{\"id\":\"" + id + "\",\"kind\":\"" + kind + "\",\"from\":\"orchestrator@tm\",\"created\":\"2026-10-12T00:00:00.000Z\",\"text\":\"" + text + "\",\"route\":\"pull\"}\n");

        internal static string Input(string session = "s1", double now = Now) =>
            "{\"session\":\"" + session + "\",\"now\":" + now.ToString("R", CultureInfo.InvariantCulture)
            + ",\"busy_at\":null,\"poll_ms\":5000,\"handed\":{},\"state\":null,\"mod_build\":" + DueRequest.CurrentModBuild.ToString(CultureInfo.InvariantCulture) + "}";

        internal async Task<(int Status, JsonValue Body, TimeSpan Took)> Get(string query)
        {
            var clock = Stopwatch.StartNew();
            var response = await Registry.Routes["GET /v1/due"](
                new RouteRequest("GET", "/v1/due", query, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), Environment.ProcessId),
                CancellationToken.None);
            return (response.Status, JsonText.Parse(response.Body.ToArray()), clock.Elapsed);
        }

        internal static string Query(string input, int? wait = null) =>
            "in=" + Uri.EscapeDataString(input) + (wait is { } w ? "&wait=" + w.ToString(CultureInfo.InvariantCulture) : string.Empty);
    }

    private static string[] Ids(JsonValue body) => [.. body.AsObject()["ids"].AsArray().Select(i => i.AsString())];

    [Fact]
    public async Task AnOldModIsLoggedOncePerSession()
    {
        var rig = new Rig();
        rig.Append("e1");
        var old = Rig.Input().Replace(",\"mod_build\":" + DueRequest.CurrentModBuild.ToString(CultureInfo.InvariantCulture), string.Empty, StringComparison.Ordinal);

        _ = await rig.Get(Rig.Query(old, wait: 1));
        _ = await rig.Get(Rig.Query(old, wait: 1));

        Assert.Contains("session s1 runs an old mod", Assert.Single(rig.Logged), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeACurrentModIsNotLogged()
    {
        var rig = new Rig();
        rig.Append("e1");

        _ = await rig.Get(Rig.Query(Rig.Input(), wait: 1));

        Assert.Empty(rig.Logged);
    }

    [Fact]
    public void TheModSendsTheBuildThisDaemonExpects()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(dir!.FullName, "mod", "register.tsx")))
        {
            dir = dir.Parent;
        }

        Assert.Contains($"const MOD_BUILD = {DueRequest.CurrentModBuild}\n", File.ReadAllText(Path.Combine(dir.FullName, "mod", "register.tsx")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APeersPlainNoteWakesNoIdleMemberAndWaitsInItsDigest()
    {
        var rig = new Rig();
        rig.AppendFromPeer("n1", "note", "news");

        var r = await rig.Get(Rig.Query(Rig.Input()));
        var bell = MeshModule.CreateMailDoorbell(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment, null);

        Assert.Empty(Ids(r.Body));
        Assert.Equal("news", Assert.Single(bell.DigestEntries("s1", Now)).Body);
    }

    [Theory]
    [InlineData("brief", "do this")]
    [InlineData("report", "done")]
    [InlineData("note", "QUESTION: which one?")]
    [InlineData("note", "ah: your link request was denied")]
    public async Task NegativeABriefAReportAQuestionOrAhsOwnNoteStillWakesAnIdleMemberAndIsNotInTheDigest(string kind, string text)
    {
        var rig = new Rig();
        rig.AppendFromPeer("n1", kind, text);

        var r = await rig.Get(Rig.Query(Rig.Input()));
        var bell = MeshModule.CreateMailDoorbell(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment, null);

        Assert.Equal(["n1"], Ids(r.Body));
        Assert.Empty(bell.DigestEntries("s1", Now));
    }

    [Fact]
    public void TheDueRouteIsRegisteredOnGet()
    {
        Assert.Equal(["GET /v1/due"], new Rig().Registry.Routes.Keys.ToArray());
    }

    [Fact]
    public async Task TheProbeTheCliSendsToFindTheRouteIsAnsweredWith400ByADaemonThatHasIt()
    {
        var rig = new Rig();
        var query = MeshModule.DueProbePath[(MeshModule.DueProbePath.IndexOf('?', StringComparison.Ordinal) + 1)..];

        var r = await rig.Get(query);

        Assert.StartsWith("/v1/due?", MeshModule.DueProbePath, StringComparison.Ordinal);
        Assert.Equal(400, r.Status);
        Assert.Equal("bad_request", r.Body.AsObject()["error"].AsObject()["code"].AsString());
        Assert.True(r.Took < HeldTooLong, r.Took.ToString());
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task AStoppedSessionIsHandedNoPromptToResubmit(bool held, int handed)
    {
        var rig = new Rig();
        rig.Append("e1");
        var input = Rig.Input().Replace("\"state\":null", "\"held\":" + (held ? "true" : "false") + ",\"state\":null", StringComparison.Ordinal);

        var r = await rig.Get(Rig.Query(input));

        Assert.Equal(200, r.Status);
        Assert.Equal(handed, Ids(r.Body).Length);
    }

    [Fact]
    public async Task MailThatIsDueIsAnsweredAtOnceWithTheSameAnswerTheCliGives()
    {
        var rig = new Rig();
        rig.Append("e1");

        var r = await rig.Get(Rig.Query(Rig.Input(), wait: 25));

        Assert.Equal(200, r.Status);
        Assert.Equal(["e1"], Ids(r.Body));
        Assert.True(r.Took < HeldTooLong, r.Took.ToString());
        Assert.Equal("ah mail for imp@tm (1):\n- note from user: hi\n[ah-entry e1]", r.Body.AsObject()["prompt"].AsString());
        Assert.True(r.Body.AsObject()["daemon"].IsNull);
        Assert.Equal(1, r.Body.AsObject()["v"].AsNumber());
    }

    [Fact]
    public async Task TheAnswerCarriesTheCompactFormWhenSomethingWasShortenedAndNullWhenNothingWas()
    {
        var note = new Rig();
        note.Append("e1");
        var shortened = (await note.Get(Rig.Query(Rig.Input()))).Body.AsObject();
        Assert.Equal("ah mail for imp@tm (1):\n- note from user\n[ah-entry e1]", shortened["compact"].AsString());
        Assert.NotEqual(shortened["prompt"].AsString(), shortened["compact"].AsString());

        var relay = new Rig();
        File.AppendAllText(
            relay.Dir + "/entries.jsonl",
            "{\"id\":\"x1\",\"kind\":\"event\",\"from\":\"ahd\",\"event\":\"relay-needed\",\"route\":\"pull\",\"created\":\"2026-10-12T00:00:00.000Z\","
            + "\"data\":{\"entry\":\"m9\",\"relay\":{\"entry\":\"m9\",\"address\":\"imp@tm\",\"to\":\"peer\",\"line\":\"do it\"}}}\n");
        var same = (await relay.Get(Rig.Query(Rig.Input()))).Body.AsObject();
        Assert.Equal(["x1"], same["ids"].AsArray().Select(i => i.AsString()));
        Assert.True(same["compact"].IsNull);
        Assert.Contains("relay-needed", same["prompt"].AsString(), StringComparison.Ordinal);

        var empty = (await new Rig().Get(Rig.Query(Rig.Input(), wait: 0))).Body.AsObject();
        Assert.True(empty["prompt"].IsNull);
        Assert.True(empty["compact"].IsNull);
    }

    [Fact]
    public async Task AHeldRequestIsAnsweredByAnAppendNotByTheWaitLimit()
    {
        var rig = new Rig();
        var held = rig.Get(Rig.Query(Rig.Input(), wait: 25));
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var appended = Stopwatch.StartNew();
        rig.Append("late");

        var r = await held;

        Assert.Equal(["late"], Ids(r.Body));
        Assert.Contains("\"state\":\"due-recompute\"", File.ReadAllText(Mailbox.TraceFile(rig.Home, "s1")), StringComparison.Ordinal);
        Assert.True(appended.Elapsed < HeldTooLong, appended.Elapsed.ToString());
        Assert.True(r.Took > TimeSpan.FromMilliseconds(400), r.Took.ToString());
    }

    [Fact]
    public async Task WithNothingToHandOverTheRequestAnswersEmptyAtTheWaitLimit()
    {
        var rig = new Rig();

        var r = await rig.Get(Rig.Query(Rig.Input(), wait: 1));

        Assert.Equal(200, r.Status);
        Assert.Empty(Ids(r.Body));
        Assert.True(r.Body.AsObject()["prompt"].IsNull);
        Assert.InRange(r.Took.TotalSeconds, 0.9, 10);
    }

    [Fact]
    public async Task AWaitBelowOneIsRaisedToOneAndOneAboveTwentyFiveIsLowered()
    {
        var rig = new Rig();

        var r = await rig.Get(Rig.Query(Rig.Input(), wait: 0));

        Assert.InRange(r.Took.TotalSeconds, 0.9, 10);
    }

    [Fact]
    public async Task AnEntryThatOutlivesItsGraceIsAnsweredAtThatMomentWithNoAppend()
    {
        var rig = new Rig();
        var due = Now + 800;
        var created = DateTimeOffset.FromUnixTimeMilliseconds((long)(due - 3 * 5000 - 30_000 - 120_000))
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        rig.Append("g1", route: "fallback", created: created);

        var r = await rig.Get(Rig.Query(Rig.Input(), wait: 10));

        Assert.Equal(["g1"], Ids(r.Body));
        Assert.InRange(r.Took.TotalSeconds, 0.6, 8);
    }

    [Fact]
    public async Task ASessionWithNoRegistrationOfThisProcessIsRefusedAtOnce()
    {
        var rig = new Rig(registered: false);
        rig.Append("e1");

        var r = await rig.Get(Rig.Query(Rig.Input(), wait: 25));

        // no registration names this process, so it is no session (anonymous here, with no terminal) and may not ask for s1
        Assert.Equal(403, r.Status);
        Assert.True(r.Took < HeldTooLong, r.Took.ToString());
    }

    [Fact]
    public async Task ASessionRegisteredToAnotherProcessIsRefused()
    {
        var rig = new Rig();
        File.WriteAllText(
            rig.Home + "/registrations/s2.json",
            "{\"session_id\":\"s2\",\"address\":\"imp@tm\",\"addresses\":[\"imp@tm\"],\"boxes\":{\"imp@tm\":\"" + rig.Dir + "\"},\"pid\":1,\"bound\":true}");

        var r = await rig.Get(Rig.Query(Rig.Input("s2"), wait: 1));

        Assert.Equal(403, r.Status);
        Assert.False(File.Exists(rig.Dir + "/adapters/s2.json"));
    }

    [Fact]
    public async Task TheHeldRequestKeepsTheClaudeModHeartbeatFresh()
    {
        var rig = new Rig();
        _ = await rig.Get(Rig.Query(Rig.Input(), wait: 1));

        var hb = JsonText.Parse(File.ReadAllText(rig.Dir + "/adapters/s1.json")).AsObject();

        Assert.Equal("claude-mod", hb["adapter"].AsString());
        Assert.Equal(5000, hb["poll_ms"].AsNumber());
    }

    [Theory]
    [InlineData("", "in is required")]
    [InlineData("in=%7Bnot", "in must be JSON")]
    [InlineData("in=%5B%5D", "in must be a JSON object")]
    [InlineData("in=%7B%22session%22%3A%22s1%22%2C%22now%22%3A1%7D&wait=x", "wait must be a whole number of seconds")]
    [InlineData("in=%7B%22session%22%3A%22s1%22%2C%22now%22%3A1%7D&wait=1.5", "wait must be a whole number of seconds")]
    public async Task ABadRequestIsA400NamingTheProblem(string query, string message)
    {
        var rig = new Rig();

        var r = await rig.Get(query);

        Assert.Equal(400, r.Status);
        Assert.Equal("bad_request", r.Body.AsObject()["error"].AsObject()["code"].AsString());
        Assert.Equal(message, r.Body.AsObject()["error"].AsObject()["message"].AsString());
    }

    /// <summary>A timer that never sleeps: each delay moves the daemon's fake clock and the counter by the same amount.</summary>
    private sealed class VirtualTimer(FakeClock clock) : IDueTimer
    {
        private long _ms;

        internal List<long> Ticks { get; } = [];

        internal Action<long>? OnTick { get; set; }

        public long NowMs => _ms;

        public async Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            // Virtual time follows real time at one tick per few milliseconds, so the request that was woken gets to run before the next
            // tick. Under load the woken request can still fall behind, so the tests bound the virtual time of an answer from below
            // exactly and from above generously: what they prove is "answered by the append, not by the wait limit".
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
            _ms += milliseconds;
            clock.UtcNow = clock.UtcNow.AddMilliseconds(milliseconds);
            Ticks.Add(_ms);
            OnTick?.Invoke(_ms);
        }
    }

    private static (Rig Rig, VirtualTimer Timer, Registry Registry) Virtual()
    {
        var rig = new Rig();
        var timer = new VirtualTimer(rig.Fx.Clock);
        var registry = new Registry();
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);
        MeshModule.RegisterDueRoute(registry, due, rig.Fx.Mailbox, rig.Fx.Files, timer);
        return (rig, timer, registry);
    }

    /// <summary>The mailbox with a sessions counter the test drives: <see cref="Busy"/> makes a read of it fail as a busy store does.</summary>
    public class TokenMailbox : DispatchProxy
    {
        internal IMailbox Inner { get; set; } = null!;

        internal bool Busy { get; set; }

        internal long Token { get; set; }

        /// <summary>The newest entry the boxes report as stored; null leaves them on files.</summary>
        internal long? Newest { get; set; }

        /// <summary>The mail counter, which a heartbeat moves.</summary>
        internal long MailCounter { get; set; }

        /// <summary>How many times a held request came back to read its acks: once when first held, then once per recompute.</summary>
        internal int AckReads { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod!.Name)
            {
                case nameof(IMailbox.SessionsChangeToken):
                    return Busy ? throw new StoreUnavailableException("the state database could not be read: busy") : Token;
                case nameof(IMailbox.BoxesAreStored) when Newest is not null:
                case "get_" + nameof(IMailbox.BoxesAreStored) when Newest is not null:
                    return true;
                case nameof(IMailbox.StoredNewestEntry) when Newest is not null:
                    return Newest;
                case nameof(IMailbox.StoredBoxStamp) when Newest is not null:
                    return MailCounter.ToString(CultureInfo.InvariantCulture);
                case nameof(IMailbox.AckedThrough):
                    AckReads++;
                    break;
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            {
                throw e.InnerException;
            }
        }
    }

    private static (Rig Rig, VirtualTimer Timer, Registry Registry, TokenMailbox Control, List<string> Log) WithToken()
    {
        var rig = new Rig();
        var proxy = DispatchProxy.Create<IMailbox, TokenMailbox>();
        var control = (TokenMailbox)(object)proxy;
        control.Inner = rig.Fx.Mailbox;
        var timer = new VirtualTimer(rig.Fx.Clock);
        var registry = new Registry();
        var log = new List<string>();
        MeshModule.RegisterDueRoute(registry, MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment), proxy, rig.Fx.Files, timer, log.Add);
        return (rig, timer, registry, control, log);
    }

    [Fact]
    public async Task ABusyStoreInTheTickSkipsItAndTheHeldRequestStillWakes()
    {
        var (rig, timer, registry, control, log) = WithToken();
        var appended = false;
        timer.OnTick = ms =>
        {
            if (ms == 400)
            {
                control.Busy = true;
            }
            else if (ms == 1600)
            {
                control.Busy = false;
            }
            else if (ms == 2000 && !appended)
            {
                appended = true;
                rig.Append("e1");
            }
        };

        var answer = await Ask(registry, Rig.Query(Rig.Input(), wait: 25)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(["e1"], Ids(answer));
        Assert.True(timer.Ticks.Max() < 25000, timer.Ticks.Max().ToString(CultureInfo.InvariantCulture));
        Assert.Contains("busy", Assert.Single(log), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeAHeartbeatElsewhereDoesNotMakeAHeldRequestReadAgain()
    {
        var (_, timer, registry, control, _) = WithToken();
        control.Newest = 7;
        timer.OnTick = ms => control.MailCounter = ms;

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 2)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(1, control.AckReads);
    }

    [Fact]
    public async Task NewMailInItsBoxMakesAHeldRequestReadAgain()
    {
        var (_, timer, registry, control, _) = WithToken();
        control.Newest = 7;
        timer.OnTick = ms => control.Newest = ms >= 400 ? 8 : 7;

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 2)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(control.AckReads > 1, control.AckReads.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task NegativeASessionsChangeWithNothingDueStaysHeldToTheEndOfTheWait()
    {
        var (_, timer, registry, control, log) = WithToken();
        timer.OnTick = ms => control.Token = ms / 400;

        var answer = await Ask(registry, Rig.Query(Rig.Input(), wait: 2)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Empty(Ids(answer));
        Assert.True(timer.Ticks.Max() >= 2000, timer.Ticks.Max().ToString(CultureInfo.InvariantCulture));
        Assert.Empty(log);
    }

    private static async Task<JsonValue> Ask(Registry registry, string query)
    {
        var response = await registry.Routes["GET /v1/due"](
            new RouteRequest("GET", "/v1/due", query, new Dictionary<string, string>(), ReadOnlyMemory<byte>.Empty, new Dictionary<string, string>(), Environment.ProcessId),
            CancellationToken.None);
        Assert.Equal(200, response.Status);
        return JsonText.Parse(response.Body.ToArray());
    }

    [Fact]
    public async Task AWaitOfSixtyIsAnsweredAtTwentyFiveSecondsAndAWaitOfZeroAtOne()
    {
        var (_, timer, registry) = Virtual();

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 60));
        var longMs = timer.Ticks[^1];
        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 0));
        var shortMs = timer.Ticks[^1] - longMs;

        // Never before the clamped wait, and long before the 60 s that was asked for.
        Assert.InRange(longMs, 25_000, 40_000);
        Assert.InRange(shortMs, 1_000, 10_000);
    }

    [Fact]
    public async Task TheDoorbellBetweenToolCallsIsAnsweredAtOnceWithNothingDue()
    {
        var (_, timer, registry) = Virtual();
        var input = Rig.Input();
        var doorbell = input[..input.LastIndexOf('}')] + ",\"carrier\":\"tool\"}";

        _ = await Ask(registry, "in=" + Uri.EscapeDataString(doorbell));

        // ah2-70: it sends no wait, and it is a question, never a hold: a member's every tool call rings it
        Assert.True(timer.Ticks.Count == 0 || timer.Ticks[^1] < 1_000, $"held for {(timer.Ticks.Count == 0 ? 0 : timer.Ticks[^1])} ms");
    }

    [Fact]
    public async Task ARequestWithNoWaitIsHeldForTheFullTwentyFiveSeconds()
    {
        var (_, timer, registry) = Virtual();

        _ = await Ask(registry, "in=" + Uri.EscapeDataString(Rig.Input()));

        Assert.InRange(timer.Ticks[^1], 25_000, 40_000);
    }

    [Fact]
    public async Task TheHeartbeatIsNeverStaleToAReaderDuringAHoldAndIsWrittenFewTimes()
    {
        var (rig, timer, registry) = Virtual();
        var dir = rig.Dir;
        var stale = new List<long>();
        var beats = new List<string>();
        timer.OnTick = ms =>
        {
            var hb = rig.Fx.Mailbox.ReadHeartbeat(dir, "s1", rig.Fx.Clock.UtcNow.ToUnixTimeMilliseconds());
            if (hb is null || !rig.Fx.Mailbox.HeartbeatFresh(hb, rig.Fx.Clock.UtcNow.ToUnixTimeMilliseconds()))
            {
                stale.Add(ms);
            }

            if (hb is { } h && h.Get("at") is { IsText: true } at && (beats.Count == 0 || beats[^1] != at.AsString()))
            {
                beats.Add(at.AsString());
            }
        };

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 25));

        Assert.Empty(stale);
        Assert.InRange(beats.Count, 3, 5);
    }

    [Fact]
    public void AHeartbeatWriteLeavesTheMailCounterAndNegativeEveryOtherMailWriteMovesIt()
    {
        var ops = new Rig().Fx.Mailbox.StateDomain().Ops.Where(static o => o.Kind == OpKind.Write).ToList();

        Assert.True(ops.Single(static o => o.Name == "heartbeat").LeavesSeq);
        Assert.All(ops.Where(static o => o.Name != "heartbeat"), static o => Assert.False(o.LeavesSeq, o.Name));
    }

    [Fact]
    public async Task AYoungHeartbeatIsStillRewrittenWhenAnAckLandedSinceItWasWritten()
    {
        var (rig, _, registry) = Virtual();
        double? Through() => rig.Fx.Mailbox.ReadHeartbeat(rig.Dir, "s1", rig.Fx.Clock.UtcNow.ToUnixTimeMilliseconds())?.Get("acked_through")?.AsNumber();
        rig.Append("e1");
        Directory.CreateDirectory(rig.Dir + "/acks");

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 1));
        Assert.Equal(0, Through());
        File.WriteAllText(rig.Dir + "/acks/e1", "{}");

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 1));
        Assert.True(Through() > 0);
    }

    [Fact]
    public async Task ARequestRightAfterAnotherDoesNotRewriteAYoungHeartbeatAndNegativeAnotherPollMsDoes()
    {
        var (rig, _, registry) = Virtual();
        string? At() => rig.Fx.Mailbox.ReadHeartbeat(rig.Dir, "s1", rig.Fx.Clock.UtcNow.ToUnixTimeMilliseconds())?.Get("at")?.AsString();

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 1));
        var first = At();
        Assert.NotNull(first);

        _ = await Ask(registry, Rig.Query(Rig.Input(), wait: 1));
        Assert.Equal(first, At());

        _ = await Ask(registry, Rig.Query(Rig.Input().Replace("\"poll_ms\":5000", "\"poll_ms\":6000", StringComparison.Ordinal), wait: 1));
        Assert.NotEqual(first, At());
    }

    [Fact]
    public async Task AnAckAloneDoesNotWakeAHeldRequestAndTheHeartbeatCatchesUpAtItsNextRewrite()
    {
        var (rig, timer, registry) = Virtual();
        rig.Append("e1");
        Directory.CreateDirectory(rig.Dir + "/acks");
        var atOneSecond = -1d;
        timer.OnTick = ms =>
        {
            if (ms == 400)
            {
                File.WriteAllText(rig.Dir + "/acks/e1", "{}");
            }

            if (ms == 1_000)
            {
                atOneSecond = JsonText.Parse(File.ReadAllText(rig.Dir + "/adapters/s1.json")).AsObject()["acked_through"].AsNumber();
            }
        };
        // b466: pending, since a resolved record is itself the ack.
        var held = Rig.Query(Rig.Input().Replace("\"handed\":{}", "\"handed\":{\"e1\":[\"pending\"," + Now.ToString("R", CultureInfo.InvariantCulture) + "]}", StringComparison.Ordinal), wait: 10);

        var answer = await Ask(registry, held);

        var last = JsonText.Parse(File.ReadAllText(rig.Dir + "/adapters/s1.json")).AsObject();
        Assert.Equal(0, atOneSecond);
        Assert.True(last["acked_through"].AsNumber() > 0);
        Assert.Empty(answer.AsObject()["ids"].AsArray());
        Assert.True(timer.Ticks[^1] >= 10_000);
    }

    [Fact]
    public async Task AnEntryThatLandsAfterAnAckInTheSameHoldIsDeliveredAndTheAckedIdIsReleasedInTheSameAnswer()
    {
        var (rig, timer, registry) = Virtual();
        rig.Append("e1");
        Directory.CreateDirectory(rig.Dir + "/acks");
        timer.OnTick = ms =>
        {
            if (ms == 400)
            {
                File.WriteAllText(rig.Dir + "/acks/e1", "{}");
            }

            if (ms == 800)
            {
                rig.Append("f1");
            }
        };
        var held = Rig.Query(Rig.Input().Replace("\"handed\":{}", "\"handed\":{\"e1\":[\"resolved\"," + Now.ToString("R", CultureInfo.InvariantCulture) + "]}", StringComparison.Ordinal), wait: 10);

        var answer = await Ask(registry, held);

        Assert.Equal(["f1"], answer.AsObject()["ids"].AsArray().Select(i => i.AsString()).ToArray());
        Assert.Equal(["e1"], answer.AsObject()["acked"].AsArray().Select(i => i.AsString()).ToArray());
        // Woken by the append at 800 ms, not by the 10 s wait limit.
        Assert.InRange(timer.Ticks[^1], 800, 8_000);
    }
}
