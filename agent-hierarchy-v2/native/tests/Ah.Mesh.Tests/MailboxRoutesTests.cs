using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>The mailbox routes as the router calls them: a request in, a status and a JSON body out.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailboxRoutesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Registry : IRouteRegistry
    {
        internal Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> Routes { get; } = new(StringComparer.Ordinal);

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) => Routes.Add(pattern, handler);
    }

    private sealed class Processes : IProcessInspector
    {
        internal HashSet<int> Owned { get; } = [];

        /// <summary>Another user's processes: kill-0 answers EPERM, which reads alive.</summary>
        internal HashSet<int> Foreign { get; } = [];

        public bool PidAlive(int pid) => Owned.Contains(pid) || Foreign.Contains(pid);

        public bool PidOwned(int pid) => Owned.Contains(pid);

        public string? CommandLine(int pid) => null;
    }

    private sealed class AcceptAll : IRequestContextChecker
    {
        public RequestContextProblem? Check(RequestContext context) => null;
    }

    /// <summary>The rig's binder, except that a line's harness verdict is <paramref name="noHarness"/>.</summary>
    private sealed class LineVerdict(IRegistrationBinder real, bool? noHarness) : IRegistrationBinder
    {
        public bool Binds(long? claimedPid, string? claimedPidStart, int? registrantPid) => real.Binds(claimedPid, claimedPidStart, registrantPid);

        public string? StartOf(long pid) => real.StartOf(pid);

        public Ah.Core.Time.StartMatch StartMatches(long pid, string? recorded) => real.StartMatches(pid, recorded);

        public string? StartAs(long pid, string? recorded) => real.StartAs(pid, recorded);

        public bool? NoHarnessFrom(long pid) => noHarness;

        public bool IsHarnessProcess(long pid) => real.IsHarnessProcess(pid);
    }

    private sealed class Rig
    {
        internal Rig(Func<IRegistrationBinder, IRegistrationBinder>? binder = null, bool seam = false, DateTimeOffset? boot = null, Func<IMailbox, IMailbox>? wrap = null)
        {
            var sandbox = UnitSandbox.Create();
            Root = sandbox.Root;
            Hierarchy = Root + "/proj/.claude/ah2";
            Directory.CreateDirectory(Hierarchy);
            var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = Root + "/ah" };
            Fx = MeshFixture.Create(vars, Now, Root);
            TeamFile = Fx.Roster.TeamPath(Hierarchy, "tm");
            Directory.CreateDirectory(Path.GetDirectoryName(TeamFile)!);
            var members = new JsonArray
            {
                new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["kind"] = "claude" },
                new JsonObject { ["name"] = "bot", ["role"] = "reviewer", ["kind"] = "custom", ["harness"] = "my-harness" },
            };
            File.WriteAllText(TeamFile, JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = Fx.OwnStart! }, ["members"] = members }, 0));
            var config = ConfigModule.CreateCore(Fx.Files, Fx.Environment, () => Fx.Roster, new ProcessFacts(Root, 4242));
            Tally = MeshModule.CreateAckTally();
            Registry = new Registry();
            if (seam)
            {
                Fx.Hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(Fx.Mailbox, Fx.Table, Pids, () => boot);
            }

            MeshModule.RegisterMailboxRoutes(
                Registry,
                new MailboxRouteDependencies(
                    wrap?.Invoke(Fx.Mailbox) ?? Fx.Mailbox, Fx.Roster, config, new AcceptAll(), Fx.Files, _ => false, Tally, Pids,
                    (binder ?? (b => b))(MeshModule.CreateRegistrationBinder(Pids.PidAlive, new ClaudeSelfTable(Fx.Table))),
                    seam ? (pid, start, policy) => Fx.Hierarchy.Core.PidRunning(pid, start, policy) : null,
                    Heard));
        }

        internal IHeardProcesses Heard { get; } = MeshModule.CreateHeardProcesses();

        private bool _caller;

        internal MeshFixture Fx { get; }

        internal string Root { get; }

        internal string Hierarchy { get; }

        internal string TeamFile { get; }

        internal IAckTally Tally { get; }

        internal Processes Pids { get; } = new();

        internal Registry Registry { get; }

        internal string ContextQuery => $"cwd={Uri.EscapeDataString(Root + "/proj")}&hierarchy_dir={Uri.EscapeDataString(Hierarchy)}";

        internal string ContextBody => $"\"cwd\":\"{Root}/proj\",\"hierarchy_dir\":\"{Hierarchy}\"";

        internal string BoxOf(string name) => Fx.Mailbox.BoxDirFor(TeamFile, name);

        internal void Append(string id, string to = "imp@tm", string kind = "note")
        {
            var outcome = Fx.Mailbox.AppendEntry(
                BoxOf(to.Split('@')[0]),
                new JsonObject { ["v"] = 1, ["id"] = id, ["kind"] = kind, ["from"] = "user", ["to"] = to, ["created"] = "2026-03-01T12:00:00+00:00", ["text"] = "hi", ["route"] = "fallback" });
            Assert.True(outcome.Ok, outcome.Failure?.Message);
        }

        internal async Task<(int Status, JsonValue Body)> Call(string pattern, string query = "", string? body = null, bool anonymous = false)
        {
            // the mail routes act for the caller the peer pid proves: this test process is session s-rig, the member boxes' holder.
            // It registers on the first such call only, so the registration tests see only what they register.
            if (!_caller && pattern is "GET /v1/inbox" or "POST /v1/ack" or "POST /v1/decline")
            {
                _caller = true;
                Fx.Prove("s-rig", TeamFile, new BoxRef("imp@tm", BoxOf("imp")), new BoxRef("bot@tm", BoxOf("bot")));
            }

            var response = await Registry.Routes[pattern](
                new RouteRequest(
                    pattern.Split(' ')[0], pattern.Split(' ')[1], query, new Dictionary<string, string>(),
                    body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body),
                    new Dictionary<string, string>(),
                    anonymous ? null : Environment.ProcessId),
                CancellationToken.None);
            return (response.Status, JsonText.Parse(response.Body.ToArray()));
        }

        internal static string RegisterBody(string session, string address, string dir, string? harness = null, string? teamFile = null, int? pid = null) =>
            "{" + $"\"session_id\":\"{session}\",\"addresses\":[\"{address}\"],\"boxes\":{{\"{address}\":\"{dir}\"}}"
            + (teamFile is null ? string.Empty : $",\"team_file\":\"{teamFile}\"")
            + (harness is null ? string.Empty : $",\"harness\":\"{harness}\"")
            + (pid is null ? string.Empty : $",\"pid\":{pid}") + "}";
    }

    private static JsonObject Error(JsonValue body) => body.AsObject()["error"].AsObject();

    [Fact]
    public void EveryMailboxRouteIsRegistered()
    {
        var rig = new Rig();

        Assert.Equal(
            ["GET /v1/inbox", "GET /v1/inspect/boxes", "GET /v1/inspect/entries", "GET /v1/inspect/registrations", "POST /v1/ack", "POST /v1/decline", "POST /v1/register"],
            rig.Registry.Routes.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    // ---------------------------------------------------------------- decline

    private static Rig RelayedEntry(string carrier, string? carrierSession = null)
    {
        var rig = new Rig();
        rig.Append("e1");
        var row = new JsonObject { ["id"] = "e1", ["rung"] = 3, ["state"] = "pending", ["attempt"] = 1, ["carrier"] = carrier };
        if (carrierSession is not null)
        {
            row["carrier_session"] = carrierSession;
        }

        Assert.True(rig.Fx.Mailbox.AppendLadderRow(rig.BoxOf("imp"), row, 1));
        rig.Fx.Prove("s-orch", rig.TeamFile, new BoxRef("orchestrator@tm", rig.BoxOf("orchestrator")));
        rig.Fx.Prove("s-imp", rig.TeamFile, new BoxRef("imp@tm", rig.BoxOf("imp")));
        return rig;
    }

    private static string DeclineBody(Rig rig, string session, string reason = "already-reported", string address = "imp@tm") =>
        "{" + $"\"address\":\"{address}\",\"id\":\"e1\",\"reason\":\"{reason}\",\"session_id\":\"{session}\",{rig.ContextBody}" + "}";

    private static string[] States(Rig rig) =>
        [.. rig.Fx.Mailbox.LadderRowsOf(rig.BoxOf("imp"), "e1").Select(r => r["state"].AsString())];

    [Fact]
    public async Task TheCarriersSessionDeclinesAThroughTheDaemonRoute()
    {
        var rig = RelayedEntry("orchestrator@tm");

        var (status, body) = await rig.Call("POST /v1/decline", body: DeclineBody(rig, "s-orch"));

        Assert.Equal(200, status);
        Assert.Equal(("e1", "orchestrator@tm", "already-reported"), (body.AsObject()["declined"].AsString(), body.AsObject()["by"].AsString(), body.AsObject()["reason"].AsString()));
        Assert.Equal(["pending", "declined"], States(rig));
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxOf("imp"), "e1"));
    }

    [Fact]
    public async Task ADeclineNamingASessionThisProcessDoesNotHoldIsRefusedAndWritesNothing()
    {
        var rig = RelayedEntry("orchestrator@tm");

        var (status, body) = await rig.Call("POST /v1/decline", body: DeclineBody(rig, "s-nobody"));

        Assert.Equal(403, status);
        Assert.Equal("not_your_session", Error(body)["code"].AsString());
        Assert.Equal(["pending"], States(rig));
    }

    [Theory]
    [InlineData("s-imp", "already-reported")]
    [InlineData("s-orch", "because")]
    public async Task ADeclineFromANonCarrierSessionOrWithAnUnknownReasonIsRefusedAndWritesNothing(string session, string reason)
    {
        var rig = RelayedEntry("orchestrator@tm");

        var (status, body) = await rig.Call("POST /v1/decline", body: DeclineBody(rig, session, reason));

        Assert.Equal(400, status);
        Assert.Equal("decline_refused", Error(body)["code"].AsString());
        Assert.Equal(["pending"], States(rig));
    }

    [Fact]
    public async Task ADeclineFromARegistrationWhosePidIsGoneIsRefusedAndWritesNothing()
    {
        var rig = RelayedEntry("orchestrator@tm");
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s-orch", [new BoxRef("orchestrator@tm", rig.BoxOf("orchestrator"))], rig.TeamFile, "claude", int.MaxValue - 1, Bound: true)));

        var (status, body) = await rig.Call("POST /v1/decline", body: DeclineBody(rig, "s-orch"));

        Assert.Equal(403, status);
        Assert.Equal("not_your_session", Error(body)["code"].AsString());
        Assert.Equal(["pending"], States(rig));
    }

    [Theory]
    [InlineData("s-orch", 200)]
    [InlineData("s-imp", 400)]
    public async Task ARelayThatNamesACarrierSessionIsDeclinedOnlyByThatLiveSession(string session, int status)
    {
        var rig = RelayedEntry("orchestrator@tm", carrierSession: "s-orch");

        var answer = await rig.Call("POST /v1/decline", body: DeclineBody(rig, session));

        Assert.Equal(status, answer.Status);
        Assert.Equal(status == 200 ? ["pending", "declined"] : ["pending"], States(rig));
    }

    [Fact]
    public async Task ADeclineOfARelayThatNamesACarrierSessionWithNoSessionInTheBodyIsRefused()
    {
        var rig = RelayedEntry("orchestrator@tm", carrierSession: "s-orch");
        var body = "{" + $"\"address\":\"imp@tm\",\"id\":\"e1\",\"reason\":\"withdrawn\",{rig.ContextBody}" + "}";

        var (status, answer) = await rig.Call("POST /v1/decline", body: body);

        // this process holds several sessions, so a decline that names none cannot say which one is declining
        Assert.Equal(403, status);
        Assert.Equal("ambiguous_session", Error(answer)["code"].AsString());
        Assert.Equal(["pending"], States(rig));
    }

    [Theory]
    [InlineData("{\"address\":\"imp@tm\",\"id\":\"e1\"}", "bad_request")]
    [InlineData("{\"address\":\"imp\",\"id\":\"e1\",\"reason\":\"withdrawn\"}", "bad_address")]
    public async Task AMalformedDeclineBodyIsA400WithItsCode(string body, string code)
    {
        var rig = RelayedEntry("orchestrator@tm");

        var (status, answer) = await rig.Call("POST /v1/decline", body: body);

        Assert.Equal(400, status);
        Assert.Equal(code, Error(answer)["code"].AsString());
        Assert.Equal(["pending"], States(rig));
    }

    // ---------------------------------------------------------------- inbox

    [Fact]
    public async Task AnInboxHoldsTheUnackedEntriesAndTheCursor()
    {
        var rig = new Rig();
        rig.Append("e1");

        var r = await rig.Call("GET /v1/inbox", $"address=imp%40tm&{rig.ContextQuery}");

        Assert.Equal(200, r.Status);
        Assert.Equal("imp@tm", r.Body.AsObject()["address"].AsString());
        Assert.Equal("e1", r.Body.AsObject()["entries"].AsArray()[0].AsObject()["id"].AsString());
        Assert.True(r.Body.AsObject()["next"].AsNumber() > 0);
    }

    [Theory]
    [InlineData("address=bot", 400, "bad_address")]
    [InlineData("address=ghost%40tm", 404, "unknown_address")]
    [InlineData("address=imp%40tm&wait=5", 400, "bad_request")]
    [InlineData("address=imp%40tm&wait=26&since=0", 400, "bad_request")]
    [InlineData("address=imp%40tm&wait=1.5&since=0", 400, "bad_request")]
    [InlineData("address=imp%40tm&since=-1", 400, "bad_request")]
    [InlineData("address=imp%40tm&since=x", 400, "bad_request")]
    [InlineData("address=imp%40tm&poll_ms=0", 400, "bad_request")]
    public async Task AnInboxRequestThatBreaksARuleIsRefused(string query, int status, string code)
    {
        var rig = new Rig();

        var r = await rig.Call("GET /v1/inbox", query + "&" + rig.ContextQuery);

        Assert.Equal(status, r.Status);
        Assert.Equal(code, Error(r.Body)["code"].AsString());
    }

    [Fact]
    public async Task APullNamingAnAdapterWritesItsHeartbeatAndOneWithoutDoesNot()
    {
        var rig = new Rig();
        var box = rig.BoxOf("imp");

        await rig.Call("GET /v1/inbox", $"address=imp%40tm&{rig.ContextQuery}");
        var before = rig.Fx.Mailbox.ReadHeartbeat(box);
        await rig.Call("GET /v1/inbox", $"address=imp%40tm&adapter=my-harness&session_id=s-rig&poll_ms=1000&{rig.ContextQuery}");
        var after = rig.Fx.Mailbox.ReadHeartbeat(box);

        Assert.Null(before);
        Assert.NotNull(after);
        Assert.Equal("my-harness", after["adapter"].AsString());
    }

    [Fact]
    public async Task ALongPollReturnsAtOnceWhenAnEntryIsThereAndAfterTheWaitWhenNot()
    {
        var rig = new Rig();
        rig.Append("e1");

        var held = await rig.Call("GET /v1/inbox", $"address=imp%40tm&since=0&wait=1&{rig.ContextQuery}");
        var empty = System.Diagnostics.Stopwatch.StartNew();
        var none = await rig.Call("GET /v1/inbox", $"address=bot%40tm&since=0&wait=1&{rig.ContextQuery}");
        empty.Stop();

        Assert.Single(held.Body.AsObject()["entries"].AsArray());
        Assert.Empty(none.Body.AsObject()["entries"].AsArray());
        Assert.True(empty.ElapsedMilliseconds >= 900);
    }

    // ---------------------------------------------------------------- the caller

    [Fact]
    public async Task AnInboxOfABoxTheCallersSessionDoesNotHoldIsRefused()
    {
        var rig = new Rig();

        var r = await rig.Call("GET /v1/inbox", $"address=orchestrator%40tm&{rig.ContextQuery}");

        Assert.Equal(403, r.Status);
        Assert.Equal("not_your_session", Error(r.Body)["code"].AsString());
    }

    [Fact]
    public async Task ACallerWhoseProcessIsUnknownIsRefusedTheInboxAndTheAck()
    {
        var rig = new Rig();
        rig.Append("e1");

        var inbox = await rig.Call("GET /v1/inbox", $"address=imp%40tm&{rig.ContextQuery}", anonymous: true);
        var ack = await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e1\"],\"by\":\"cli\",{rig.ContextBody}}}", anonymous: true);

        Assert.Equal((403, "anonymous"), (inbox.Status, Error(inbox.Body)["code"].AsString()));
        Assert.Equal((403, "anonymous"), (ack.Status, Error(ack.Body)["code"].AsString()));
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxOf("imp"), "e1"));
    }

    [Fact]
    public async Task ARegistrationNamingATeamFileThatIsNotTheNamedFileOfTheAddressTeamIsNotAdmitted()
    {
        var rig = new Rig();
        var wrongName = Path.Combine(Path.GetDirectoryName(rig.TeamFile)!, "other.json");
        File.Copy(rig.TeamFile, wrongName);

        var named = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile));
        var other = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s2", "imp@tm", rig.Fx.Mailbox.BoxDirFor(wrongName, "imp"), teamFile: wrongName));

        Assert.Equal(200, named.Status);
        Assert.Equal((403, "not_admitted"), (other.Status, Error(other.Body)["code"].AsString()));
    }

    [Fact]
    public async Task AMemberRemovedFromTheTeamRecordWhileItsSessionIsAliveStillAcksItsOwnEntry()
    {
        var rig = new Rig();
        rig.Append("e1");
        var record = JsonText.Parse(File.ReadAllText(rig.TeamFile)).AsObject();
        var kept = new JsonArray();
        foreach (var m in record["members"].AsArray().Where(m => m.AsObject()["name"].AsString() != "imp"))
        {
            kept.Add(m);
        }

        record["members"] = kept;
        File.WriteAllText(rig.TeamFile, JsonText.Stringify(record, 0));

        var r = await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e1\"],\"by\":\"cli\",{rig.ContextBody}}}");

        Assert.Equal(200, r.Status);
        Assert.True(rig.Fx.Mailbox.IsAcked(rig.BoxOf("imp"), "e1"));
    }

    [Fact]
    public async Task AnAckNamingAnotherSessionIsRefused()
    {
        var rig = new Rig();
        rig.Append("e1");

        var r = await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e1\"],\"by\":\"cli\",\"session_id\":\"s-other\",{rig.ContextBody}}}");

        Assert.Equal((403, "not_your_session"), (r.Status, Error(r.Body)["code"].AsString()));
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxOf("imp"), "e1"));
    }

    [Fact]
    public async Task AnAckRecordsTheDerivedSession()
    {
        var rig = new Rig();
        rig.Append("e1");

        var r = await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e1\"],\"by\":\"cli\",{rig.ContextBody}}}");

        Assert.Equal(200, r.Status);
        Assert.Contains(
            Directory.EnumerateFiles(rig.BoxOf("imp"), "*", SearchOption.AllDirectories),
            f => File.ReadAllText(f).Contains("\"session_id\":\"s-rig\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheTeamsProvenOrchestratorReadsAMembersBoxButLeavesItsHeartbeat()
    {
        var rig = new Rig();
        rig.Append("e1");
        rig.Fx.Prove("s-orch", rig.TeamFile, new BoxRef("orchestrator@tm", rig.BoxOf("orchestrator")));

        var r = await rig.Call("GET /v1/inbox", $"address=imp%40tm&adapter=my-harness&session_id=s-orch&poll_ms=1000&{rig.ContextQuery}");

        Assert.Equal(200, r.Status);
        Assert.Single(r.Body.AsObject()["entries"].AsArray());
        Assert.Null(rig.Fx.Mailbox.ReadHeartbeat(rig.BoxOf("imp")));
    }

    [Fact]
    public async Task AnOrchestratorAddressTheTeamFileDoesNotProveReadsNothing()
    {
        var rig = new Rig();
        File.WriteAllText(rig.TeamFile, JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = "Thu Jan  1 00:00:00 2026" }, ["members"] = new JsonArray { new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["kind"] = "claude" } } }, 0));
        rig.Fx.Prove("s-orch", rig.TeamFile, new BoxRef("orchestrator@tm", rig.BoxOf("orchestrator")));

        var r = await rig.Call("GET /v1/inbox", $"address=orchestrator%40tm&session_id=s-orch&{rig.ContextQuery}");

        Assert.Equal((403, "not_your_session"), (r.Status, Error(r.Body)["code"].AsString()));
    }

    // ---------------------------------------------------------------- ack

    [Fact]
    public async Task AnAckNamesWhatItAckedWhatItDidNotKnowAndWhatWasFresh()
    {
        var rig = new Rig();
        rig.Append("e1");

        var r = await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e1\",\"e-nope\"],\"by\":\"cli\",{rig.ContextBody}}}");
        var again = await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e1\"],\"by\":\"cli\",{rig.ContextBody}}}");

        Assert.Equal(200, r.Status);
        Assert.Equal(["e1"], r.Body.AsObject()["acked"].AsArray().Select(x => x.AsString()).ToArray());
        Assert.Equal(["e1"], r.Body.AsObject()["fresh"].AsArray().Select(x => x.AsString()).ToArray());
        Assert.Equal(["e-nope"], r.Body.AsObject()["unknown"].AsArray().Select(x => x.AsString()).ToArray());
        Assert.Empty(again.Body.AsObject()["fresh"].AsArray());
        Assert.True(rig.Fx.Mailbox.IsAcked(rig.BoxOf("imp"), "e1"));
    }

    [Theory]
    [InlineData("{\"address\":\"bot\",\"ids\":[],\"by\":\"cli\"", 400, "bad_address")]
    [InlineData("{\"address\":\"imp@tm\",\"ids\":\"e1\",\"by\":\"cli\"", 400, "bad_request")]
    [InlineData("{\"address\":\"imp@tm\",\"ids\":[1],\"by\":\"cli\"", 400, "bad_request")]
    [InlineData("{\"address\":\"imp@tm\",\"ids\":[],\"by\":\"nobody\"", 400, "bad_request")]
    [InlineData("{\"address\":\"ghost@tm\",\"ids\":[],\"by\":\"cli\"", 404, "unknown_address")]
    public async Task AnAckThatBreaksARuleIsRefusedAndAcksNothing(string head, int status, string code)
    {
        var rig = new Rig();
        rig.Append("e1");

        var r = await rig.Call("POST /v1/ack", body: head + "," + rig.ContextBody + "}");

        Assert.Equal(status, r.Status);
        Assert.Equal(code, Error(r.Body)["code"].AsString());
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxOf("imp"), "e1"));
    }

    [Fact]
    public async Task AnAdapterAckOfAnEntryNoPullReturnedIsCountedAndOneOfAPulledEntryIsNot()
    {
        var rig = new Rig();
        rig.Append("e1");
        rig.Append("e2");
        await rig.Call("GET /v1/inbox", $"address=imp%40tm&{rig.ContextQuery}");

        await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e1\"],\"by\":\"adapter\",{rig.ContextBody}}}");
        var afterPulled = rig.Tally.Unpulled;
        await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e-never\"],\"by\":\"adapter\",{rig.ContextBody}}}");
        await rig.Call("POST /v1/ack", body: $"{{\"address\":\"imp@tm\",\"ids\":[\"e-other\"],\"by\":\"cli\",{rig.ContextBody}}}");

        Assert.Equal(0, afterPulled);
        Assert.Equal(1, rig.Tally.Unpulled);
    }

    // ---------------------------------------------------------------- register

    [Fact]
    public async Task ARegistrationRowCarriesItsTeamFiles()
    {
        var rig = new Rig();
        var dir = Path.Join(rig.Root, "ah", "registrations");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "s1.json"), JsonText.Stringify(new JsonObject { ["session_id"] = "s1", ["address"] = "imp@tm", ["team_files"] = new JsonObject { ["imp@tm"] = "/x/teams/tm.json" } }, 0));
        File.WriteAllText(Path.Join(dir, "s2.json"), JsonText.Stringify(new JsonObject { ["session_id"] = "s2", ["address"] = "other@tm" }, 0));

        var rows = (await rig.Call("GET /v1/inspect/registrations")).Body.AsObject()["registrations"].AsArray();

        Assert.Equal("/x/teams/tm.json", rows[0].AsObject()["team_files"].AsObject()["imp@tm"].AsString());
        Assert.True(rows[1].AsObject()["team_files"].IsNull);
    }

    [Fact]
    public async Task AnUnmovedSessionsCounterAnswersUnchangedAndNegativeAMovedOneSendsTheRows()
    {
        MailDueRouteTests.TokenMailbox control = null!;
        var rig = new Rig(wrap: inner =>
        {
            var proxy = System.Reflection.DispatchProxy.Create<IMailbox, MailDueRouteTests.TokenMailbox>();
            control = (MailDueRouteTests.TokenMailbox)(object)proxy;
            control.Inner = inner;
            return proxy;
        });
        control.Token = 7;
        var dir = Path.Join(rig.Root, "ah", "registrations");
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "s1.json"), JsonText.Stringify(new JsonObject { ["session_id"] = "s1", ["address"] = "imp@tm" }, 0));

        var first = (await rig.Call("GET /v1/inspect/registrations", "since=-1")).Body.AsObject();
        Assert.Equal(7, first["token"].AsNumber());
        Assert.Single(first["registrations"].AsArray());

        var same = (await rig.Call("GET /v1/inspect/registrations", "since=7")).Body.AsObject();
        Assert.True(same["unchanged"].AsBool());
        Assert.False(same.ContainsKey("registrations"));

        control.Token = 8;
        var moved = (await rig.Call("GET /v1/inspect/registrations", "since=7")).Body.AsObject();
        Assert.Equal(8, moved["token"].AsNumber());
        Assert.Single(moved["registrations"].AsArray());

        var plain = (await rig.Call("GET /v1/inspect/registrations")).Body.AsObject();
        Assert.False(plain.ContainsKey("token"));
        Assert.Single(plain["registrations"].AsArray());
    }

    [Theory]
    [InlineData(true, "stale")] // the rig's heartbeat is old by the clock the route reads
    [InlineData(false, "none")]
    public async Task OnlyALiveRegistrationReadsItsAdapter(bool alive, string adapter)
    {
        var rig = new Rig();
        var box = rig.BoxOf("imp");
        rig.Fx.Mailbox.WriteHeartbeat(box, "stub", "s1", 5000);
        var dir = Path.Join(rig.Root, "ah", "registrations");
        _ = Directory.CreateDirectory(dir);
        var pid = alive ? Environment.ProcessId : int.MaxValue;
        File.WriteAllText(Path.Join(dir, "s1.json"), JsonText.Stringify(new JsonObject { ["session_id"] = "s1", ["address"] = "imp@tm", ["pid"] = pid, ["boxes"] = new JsonObject { ["imp@tm"] = box } }, 0));

        var row = (await rig.Call("GET /v1/inspect/registrations")).Body.AsObject()["registrations"].AsArray()[0].AsObject();

        Assert.Equal(alive, row["live"].AsBool());
        Assert.Equal(adapter, row["adapter"].AsString());
    }

    [Fact]
    public async Task B267OnlyAProcessThatRegisteredWithThisDaemonIsHeardAndACopiedRowWithItsPathsInTheRootIsNot()
    {
        var rig = new Rig();
        rig.Pids.Owned.Add(Environment.ProcessId);
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("sleep", ["60"]) { UseShellExecute = false })!;
        try
        {
            var otherStart = rig.Fx.Table.StartTime(other.Id);
            Assert.NotNull(otherStart);
            // a copied home's row: a live process, its real start, every path rewritten into the probe root
            Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s-copied", [new BoxRef("bot@tm", rig.BoxOf("bot"))], rig.TeamFile, "claude", other.Id, Cwd: rig.Root + "/proj", PidStart: otherStart, Bound: true)));

            await Registers(rig, Rig.RegisterBody("s-heard", "imp@tm", rig.BoxOf("imp"), "claude", rig.TeamFile, Environment.ProcessId));

            Assert.False(rig.Heard.Contains(other.Id, otherStart));
            Assert.True(rig.Heard.Contains(Environment.ProcessId, rig.Fx.OwnStart));
        }
        finally
        {
            other.Kill();
            other.WaitForExit();
        }
    }

    private static string WithFlavour(string body, string flavourJson) => body[..^1] + ",\"herdr_flavour\":" + flavourJson + "}";

    private static async Task Registers(Rig rig, string body)
    {
        var r = await rig.Call("POST /v1/register", body: body);
        Assert.True(r.Status == 200, $"{r.Status} {Ah.Core.Json.JsonText.Stringify(r.Body, 0)}");
    }

    [Fact]
    public async Task AStatedHerdrFlavourIsStoredAndARegistrationThatNamesNoneKeepsItUnchanged()
    {
        var rig = new Rig();
        rig.Pids.Owned.Add(Self);
        var plain = Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self);

        await Registers(rig, WithFlavour(plain, "\"core\""));
        Assert.Equal("core", rig.Fx.Mailbox.ReadRegistration("s1")!["herdr_flavour"].AsString());
        var file = rig.Fx.Mailbox.RegistrationFile("s1");
        var stored = File.ReadAllText(file);

        await Registers(rig, plain);
        Assert.Equal(stored, File.ReadAllText(file));
    }

    [Theory]
    [InlineData("\"desktop\"")]
    [InlineData("5")]
    [InlineData("\"\"")]
    public async Task AHerdrFlavourOtherThanTuiOrCoreIsIgnoredNotRefused(string flavour)
    {
        var rig = new Rig();
        rig.Pids.Owned.Add(Self);
        var plain = Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self);
        await Registers(rig, WithFlavour(plain, "\"tui\""));

        await Registers(rig, WithFlavour(plain, flavour));
        Assert.Equal("tui", rig.Fx.Mailbox.ReadRegistration("s1")!["herdr_flavour"].AsString());
    }

    [Fact]
    public async Task ARegistrationThatNeverNamedAFlavourStoresNone()
    {
        var rig = new Rig();

        await Registers(rig, WithFlavour(Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile), "\"desktop\""));
        Assert.False(rig.Fx.Mailbox.ReadRegistration("s1")!.ContainsKey("herdr_flavour"));
    }

    [Fact]
    public async Task ABuiltInHarnessRegistersWithoutAdmission()
    {
        var rig = new Rig();

        var r = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile));

        Assert.Equal(200, r.Status);
        Assert.True(r.Body.AsObject()["ok"].IsTruthy);
        Assert.Contains(rig.Fx.Mailbox.AllRegistrations(), x => x["session_id"].AsString() == "s1");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"session_id\":\"s1\",\"addresses\":[],\"boxes\":{}}")]
    [InlineData("{\"session_id\":\"s1\",\"addresses\":[\"x\"],\"boxes\":{\"x\":\"/b\"}}")]
    [InlineData("{\"session_id\":\"s1\",\"addresses\":[\"a@t\"],\"boxes\":{}}")]
    public async Task ARegistrationMissingItsPartsIsBadRequest(string body)
    {
        var rig = new Rig();

        var r = await rig.Call("POST /v1/register", body: body);

        Assert.Equal(400, r.Status);
        Assert.Equal("bad_request", Error(r.Body)["code"].AsString());
        Assert.Empty(rig.Fx.Mailbox.AllRegistrations());
    }

    [Fact]
    public async Task ASettledRegistrationsDirectoryGivesTheSameRowsUntilAWrite()
    {
        var rig = new Rig();
        Assert.Equal(200, (await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile))).Status);
        var directory = rig.Root + "/ah/registrations";
        Directory.SetLastWriteTimeUtc(directory, Now.UtcDateTime.AddHours(-1));

        var first = rig.Fx.Mailbox.AllRegistrations();
        Assert.Same(first, rig.Fx.Mailbox.AllRegistrations());

        // a write moves the directory's stat, so the next read sees the new row
        Assert.Equal(200, (await rig.Call("POST /v1/register", body: Rig.RegisterBody("s2", "rev@tm", rig.BoxOf("rev"), teamFile: rig.TeamFile))).Status);
        var after = rig.Fx.Mailbox.AllRegistrations();
        Assert.NotSame(first, after);
        Assert.Contains(after, x => x["session_id"].AsString() == "s2");
    }

    [Fact]
    public async Task ARegistrationsDirectoryChangedAtTheReadIsReadAgain()
    {
        var rig = new Rig();
        Assert.Equal(200, (await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile))).Status);
        Directory.SetLastWriteTimeUtc(rig.Root + "/ah/registrations", Now.UtcDateTime);

        // within a second of the read, a later write could leave the same stat, so the rows are not reused
        Assert.NotSame(rig.Fx.Mailbox.AllRegistrations(), rig.Fx.Mailbox.AllRegistrations());
    }

    [Fact]
    public async Task ASessionIdThatIsNotANameIsBadRequest()
    {
        var rig = new Rig();

        var r = await rig.Call("POST /v1/register", body: Rig.RegisterBody("bad id", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile));

        Assert.Equal(400, r.Status);
        Assert.Equal("session_id must match [A-Za-z0-9._-]+", Error(r.Body)["message"].AsString());
    }

    [Fact]
    public async Task ACustomHarnessIsAdmittedOnlyUnderItsRowAndBox()
    {
        var rig = new Rig();
        var ok = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "bot@tm", rig.BoxOf("bot"), "my-harness", rig.TeamFile));
        var otherHarness = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s2", "bot@tm", rig.BoxOf("bot"), "other-harness", rig.TeamFile));
        var notCustom = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s3", "imp@tm", rig.BoxOf("imp"), "my-harness", rig.TeamFile));
        var wrongBox = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s4", "bot@tm", rig.Root + "/elsewhere", "my-harness", rig.TeamFile));
        var noTeamFile = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s5", "bot@tm", rig.BoxOf("bot"), "my-harness"));
        var relative = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s6", "bot@tm", rig.BoxOf("bot"), "my-harness", "teams/tm.json"));

        Assert.Equal(200, ok.Status);
        foreach (var refused in new[] { otherHarness, notCustom, wrongBox, noTeamFile, relative })
        {
            Assert.Equal(403, refused.Status);
            Assert.Equal("not_admitted", Error(refused.Body)["code"].AsString());
        }

        Assert.Equal(["s1"], rig.Fx.Mailbox.AllRegistrations().Select(x => x["session_id"].AsString()).ToArray());
    }

    // ---------------------------------------------------------------- register: every harness, the pid

    [Fact]
    public async Task ABuiltInHarnessIsHeldToTheBoxItsTeamFileGives()
    {
        var rig = new Rig();
        var outside = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.Root + "/elsewhere", teamFile: rig.TeamFile));
        var sibling = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s2", "imp@tm", rig.BoxOf("bot"), teamFile: rig.TeamFile));
        var noTeamFile = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s3", "imp@tm", rig.BoxOf("imp")));
        var relative = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s4", "imp@tm", rig.BoxOf("imp"), teamFile: "teams/tm.json"));
        var ok = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s5", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile));

        foreach (var refused in new[] { outside, sibling, noTeamFile, relative })
        {
            Assert.Equal(403, refused.Status);
            Assert.Equal("not_admitted", Error(refused.Body)["code"].AsString());
        }

        Assert.Equal(200, ok.Status);
        Assert.Equal(["s5"], rig.Fx.Mailbox.AllRegistrations().Select(x => x["session_id"].AsString()).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(-4)]
    public async Task APidAtOrBelowOneIsBadRequestAndRecordsNothing(int pid)
    {
        var rig = new Rig();

        var r = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile, pid: pid));

        Assert.Equal(400, r.Status);
        Assert.Equal("bad_request", Error(r.Body)["code"].AsString());
        Assert.Empty(rig.Fx.Mailbox.AllRegistrations());
    }

    // the registered pid is this test process, the caller, which the rig's table reads as a Claude harness
    private static readonly int Self = Environment.ProcessId;

    [Theory]
    [InlineData(null, 503, "busy")] // a line that could not be read may read next time
    [InlineData(false, 403, "not_admitted")] // negative: a line proven to run under a Claude session stays refused
    public async Task ACustomHarnessWhoseLineCannotBeReadIsToldToRetryAndOneUnderClaudeIsRefused(bool? noHarness, int status, string code)
    {
        var rig = new Rig(b => new LineVerdict(b, noHarness));
        rig.Pids.Owned.Add(Self);

        var r = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "bot@tm", rig.BoxOf("bot"), "my-harness", rig.TeamFile, pid: Self));

        Assert.Equal((status, code), (r.Status, Error(r.Body)["code"].AsString()));
        Assert.Empty(rig.Fx.Mailbox.AllRegistrations());
    }

    [Fact]
    public async Task APidOfAnotherUsersProcessIsRefusedAndOneOfTheUsersIsRecorded()
    {
        var rig = new Rig();
        rig.Pids.Owned.Add(Self);

        var foreign = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: 4243));
        var own = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s2", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self));

        Assert.Equal(403, foreign.Status);
        Assert.Equal("not_admitted", Error(foreign.Body)["code"].AsString());
        Assert.Equal(200, own.Status);
        var recorded = Assert.Single(rig.Fx.Mailbox.AllRegistrations());
        Assert.Equal(Self, recorded["pid"].AsNumber());
    }

    // re-recorded for C4/C12: another pid is outside the caller's line, a pidless claim may only create, and a second session on the
    // same pid is pid_occupied; a pidless new session is still taken, unbound
    [Fact]
    public async Task ARegistrationHeldByALiveProcessIsNotDisplacedByAClaimOfAnotherPidOrNone()
    {
        var rig = new Rig();
        rig.Pids.Owned.Add(Self);
        rig.Pids.Owned.Add(4243);
        var held = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self));

        var other = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: 4243));
        var pidless = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile));
        var sameProcess = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self));
        var occupied = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s2", "bot@tm", rig.BoxOf("bot"), harness: "claude", teamFile: rig.TeamFile, pid: Self));
        var otherSession = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s3", "bot@tm", rig.BoxOf("bot"), harness: "claude", teamFile: rig.TeamFile));

        Assert.Equal(200, held.Status);
        foreach (var refused in new[] { other, pidless })
        {
            Assert.Equal(403, refused.Status);
            Assert.Equal("not_admitted", Error(refused.Body)["code"].AsString());
        }

        Assert.Equal(200, sameProcess.Status);
        Assert.Equal((403, "pid_occupied"), (occupied.Status, Error(occupied.Body)["code"].AsString()));
        Assert.Equal(200, otherSession.Status);
        Assert.Equal(Self, rig.Fx.Mailbox.ReadRegistration("s1")!["pid"].AsNumber());
    }

    // one-liveness.md site 4: only a record whose start proves it is the running process occupies the pid; a reused, startless or
    // pre-boot one does not (the pre-boot row occupied before)
    [Theory]
    [InlineData("own", false, true)]
    [InlineData("own", true, false)]
    [InlineData("Thu Jan  1 00:00:00 2015", false, false)]
    [InlineData(null, false, false)]
    public async Task OnlyARegistrationWhoseStartMatchesOccupiesItsPid(string? start, bool bootAfterIt, bool occupied)
    {
        var rig = new Rig(seam: true, boot: bootAfterIt ? DateTimeOffset.UtcNow : null);
        rig.Pids.Owned.Add(Self);
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s2", [new BoxRef("bot@tm", rig.BoxOf("bot"))], rig.TeamFile, "claude", Self, PidStart: start == "own" ? rig.Fx.OwnStart : start, Bound: true)));

        var r = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s3", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self));

        Assert.Equal(occupied ? (403, "pid_occupied") : (200, (string?)null), (r.Status, occupied ? Error(r.Body)["code"].AsString() : null));
    }

    // one-liveness.md site 5: a held record moves only when its process is proven gone; a start that can't be compared still refuses
    // (Q2). Another user's held pid still refuses: the guarded write's own kill-0 check (HeldProcess) refuses it after HeldIsRunning.
    [Theory]
    [InlineData("own", false, true)]
    [InlineData("own", true, true)]
    [InlineData("Thu Jan  1 00:00:00 2015", false, false)]
    [InlineData("not a start", false, true)]
    public async Task ARegistrationHeldByARunningOrUnprovenProcessIsNotMovedButAReusedOneIs(string start, bool foreign, bool refused)
    {
        var rig = new Rig(seam: true);
        var parent = rig.Fx.Table.Ancestors(Self)![0];
        rig.Pids.Owned.Add(Self);
        (foreign ? rig.Pids.Foreign : rig.Pids.Owned).Add(parent);
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s1", [new BoxRef("imp@tm", rig.BoxOf("imp"))], rig.TeamFile, "claude", parent, PidStart: start == "own" ? rig.Fx.Table.StartTime(parent) : start, Bound: true)));

        var r = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self));

        Assert.Equal(refused ? (403, "not_admitted") : (200, (string?)null), (r.Status, refused ? Error(r.Body)["code"].AsString() : null));
    }

    // re-recorded for C4: a session's record moves to another pid only through its own SessionStart, never through the route
    [Fact]
    public async Task ADeadRegistrationIsNotMovedThroughTheRoute()
    {
        var rig = new Rig();
        rig.Pids.Owned.Add(Self);
        await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: Self));
        rig.Pids.Owned.Remove(Self);
        rig.Pids.Owned.Add(4243);

        var r = await rig.Call("POST /v1/register", body: Rig.RegisterBody("s1", "imp@tm", rig.BoxOf("imp"), harness: "claude", teamFile: rig.TeamFile, pid: 4243));

        Assert.Equal(403, r.Status);
        Assert.Equal(Self, rig.Fx.Mailbox.ReadRegistration("s1")!["pid"].AsNumber());
    }

    [Theory]
    [InlineData("1e30")]
    [InlineData("9223372036854775808")]
    [InlineData("9223372036854775807")]
    public async Task ACursorBeyondTheLongRangeIsBadRequestNotAServerError(string since)
    {
        var rig = new Rig();

        var r = await rig.Call("GET /v1/inbox", $"address=imp%40tm&since={since}&{rig.ContextQuery}");

        Assert.Equal(400, r.Status);
        Assert.Equal("since must be a byte offset", Error(r.Body)["message"].AsString());
    }

    [Fact]
    public async Task AnOrdinaryCursorStillAnswers()
    {
        var rig = new Rig();
        rig.Append("e1");

        var r = await rig.Call("GET /v1/inbox", $"address=imp%40tm&since=4611686018427387904&{rig.ContextQuery}");

        Assert.Equal(200, r.Status);
    }

    // ---------------------------------------------------------------- inspect

    [Fact]
    public async Task TheBoxViewCountsEntriesUnackedAndOverdue()
    {
        var rig = new Rig();
        rig.Append("e1");
        rig.Append("e2");
        rig.Fx.Mailbox.AckIds(rig.BoxOf("imp"), ["e1"], "cli");

        var r = await rig.Call("GET /v1/inspect/boxes", "address=imp%40tm");

        var boxes = r.Body.AsObject()["boxes"].AsArray();
        Assert.Single(boxes);
        Assert.Equal(2, boxes[0].AsObject()["entries"].AsNumber());
        Assert.Equal(1, boxes[0].AsObject()["unacked"].AsNumber());
        Assert.Equal("none", boxes[0].AsObject()["adapter_state"].AsString());
        Assert.Null(boxes[0].AsObject()["heartbeat_at"].IsNull ? null : "set");
    }

    [Fact]
    public async Task TheBoxViewIsNarrowedByAddress()
    {
        var rig = new Rig();
        rig.Append("e1");
        rig.Append("e2", "bot@tm");

        var all = await rig.Call("GET /v1/inspect/boxes");
        var one = await rig.Call("GET /v1/inspect/boxes", "address=bot%40tm");

        Assert.Equal(2, all.Body.AsObject()["boxes"].AsArray().Count);
        Assert.Equal("bot@tm", one.Body.AsObject()["boxes"].AsArray()[0].AsObject()["address"].AsString());
    }

    [Fact]
    public async Task TheEntryViewFiltersByAckAndKeepsTheNewest()
    {
        var rig = new Rig();
        rig.Append("e1");
        rig.Append("e2");
        rig.Append("e3");
        rig.Fx.Mailbox.AckIds(rig.BoxOf("imp"), ["e1"], "cli");

        var pending = await rig.Call("GET /v1/inspect/entries", $"address=imp%40tm&{rig.ContextQuery}");
        var acked = await rig.Call("GET /v1/inspect/entries", $"address=imp%40tm&filter=acked&{rig.ContextQuery}");
        var newest = await rig.Call("GET /v1/inspect/entries", $"address=imp%40tm&filter=all&n=1&{rig.ContextQuery}");

        Assert.Equal(2, pending.Body.AsObject()["total"].AsNumber());
        Assert.Equal(["e2", "e3"], pending.Body.AsObject()["entries"].AsArray().Select(e => e.AsObject()["id"].AsString()).ToArray());
        Assert.Equal(["e1"], acked.Body.AsObject()["entries"].AsArray().Select(e => e.AsObject()["id"].AsString()).ToArray());
        Assert.True(acked.Body.AsObject()["entries"].AsArray()[0].AsObject()["acked"].IsTruthy);
        Assert.Equal(["e3"], newest.Body.AsObject()["entries"].AsArray().Select(e => e.AsObject()["id"].AsString()).ToArray());
        Assert.Equal(3, newest.Body.AsObject()["total"].AsNumber());
    }

    [Theory]
    [InlineData("address=bot", 400, "bad_address")]
    [InlineData("address=imp%40tm&filter=weird", 400, "bad_request")]
    [InlineData("address=imp%40tm&n=0", 400, "bad_request")]
    [InlineData("address=imp%40tm&n=201", 400, "bad_request")]
    [InlineData("address=imp%40tm&n=1.5", 400, "bad_request")]
    [InlineData("address=ghost%40tm", 404, "unknown_address")]
    public async Task AnEntryViewThatBreaksARuleIsRefused(string query, int status, string code)
    {
        var rig = new Rig();

        var r = await rig.Call("GET /v1/inspect/entries", query + "&" + rig.ContextQuery);

        Assert.Equal(status, r.Status);
        Assert.Equal(code, Error(r.Body)["code"].AsString());
    }

    [Fact]
    public async Task TheRegistrationViewListsEveryRegistrationInSessionOrder()
    {
        var rig = new Rig();
        await rig.Call("POST /v1/register", body: Rig.RegisterBody("s-b", "imp@tm", rig.BoxOf("imp"), teamFile: rig.TeamFile));
        await rig.Call("POST /v1/register", body: Rig.RegisterBody("s-a", "bot@tm", rig.BoxOf("bot"), teamFile: rig.TeamFile));

        var r = await rig.Call("GET /v1/inspect/registrations");

        var rows = r.Body.AsObject()["registrations"].AsArray();
        Assert.Equal(["s-a", "s-b"], rows.Select(x => x.AsObject()["session_id"].AsString()).ToArray());
        Assert.Equal("none", rows[0].AsObject()["adapter"].AsString());
        Assert.False(rows[0].AsObject()["live"].IsTruthy);
        Assert.Equal(["bot@tm"], rows[0].AsObject()["addresses"].AsArray().Select(x => x.AsString()).ToArray());
    }
}
