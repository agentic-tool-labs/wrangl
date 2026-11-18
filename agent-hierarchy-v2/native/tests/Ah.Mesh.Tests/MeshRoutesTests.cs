using System.Runtime.Versioning;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>The mesh routes as the router calls them: a request in, a status, headers and a JSON body out.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MeshRoutesTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-03-01T12:00:00.000Z", System.Globalization.CultureInfo.InvariantCulture);

    private sealed class Registry : IRouteRegistry
    {
        internal Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> Routes { get; } = new(StringComparer.Ordinal);

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler)
        {
            if (!Routes.TryAdd(pattern, handler))
            {
                throw new InvalidOperationException($"route {pattern} is already registered");
            }
        }
    }

    private sealed class Checker(Func<RequestContext, RequestContextProblem?> check) : IRequestContextChecker
    {
        public RequestContextProblem? Check(RequestContext context) => check(context);
    }

    private sealed class Effects : IJoinEffects, ILinkMirror
    {
        internal List<bool> TargetContexts { get; } = [];

        public Task<JsonObject> LinksAsync(string op, JsonObject args, string? opId, CancellationToken cancellation) => Task.FromResult(new JsonObject());

        public Task<bool> CreateMessageAsync(string dir, string path, string content, string opId, CancellationToken cancellation, bool ownPoolContext = false)
        {
            TargetContexts.Add(ownPoolContext);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return Task.FromResult(true);
        }

        public Task NotifyAsync(LinkNotice notice, CancellationToken cancellation) => Task.CompletedTask;

        public Task SettleMessageAsync(string dir, string path, string content, string from, string opId, CancellationToken cancellation) => Task.CompletedTask;

        public Task AddMemberAsync(string dir, string team, JsonObject row, CancellationToken cancellation) => Task.CompletedTask;

        public Task MirrorAsync(LinkMirror mirror, CancellationToken cancellation) => Task.CompletedTask;
    }

    private sealed class Inspector : IProcessInspector
    {
        public bool PidAlive(int pid) => false;

        public bool PidOwned(int pid) => false;

        public string? CommandLine(int pid) => null;
    }

    /// <summary>The real send, made slow by <paramref name="delay"/> before it runs.</summary>
    private sealed class SlowSender(IMeshSender inner, TimeSpan delay) : IMeshSender
    {
        public MailboxOutcome<MeshSender> SenderOf(JsonObject request, Func<string, MeshTeam?> lookup, string? toTeam) => inner.SenderOf(request, lookup, toTeam);

        public MailboxOutcome<JsonObject> Send(JsonObject request, DateTimeOffset? now = null, TimeSpan? lockWait = null) => inner.Send(request, now, lockWait);

        public MailboxOutcome<JsonObject> SendMarked(JsonObject request, Action<string> mark)
        {
            Thread.Sleep(delay);
            return inner.SendMarked(request, mark);
        }
    }

    private sealed class Rig
    {
        internal Rig(
            Func<RequestContext, RequestContextProblem?>? check = null,
            Func<string, string, DateTimeOffset?>? removedAt = null,
            Action<string>? log = null,
            long slowSendMs = 2000,
            TimeSpan? sendDelay = null,
            bool admin = false)
        {
            var sandbox = UnitSandbox.Create();
            Root = sandbox.Root;
            Hierarchy = Root + "/proj/.claude/ah2";
            Directory.CreateDirectory(Hierarchy);
            var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = Root + "/ah" };
            // the caller is this test process: the user at a terminal unless a test registers it as a session; with `admin` it is
            // the Claude harness holding the admin grant (ah2-32), so it is the user and also holds the sessions it registers
            var real = Ah.Platform.PlatformModule.CreateProcessTable(Ah.Platform.PlatformModule.CreateProcessRunner());
            var fx = MeshFixture.Create(vars, Now, Root, table: admin ? new ClaudeSelfTable(real) : new UserTerminalTable(real));
            Fx = fx;
            if (admin)
            {
                File.WriteAllText(
                    Ah.Mesh.AdminGrant.LegacyPathIn(Root + "/ah"),
                    JsonText.Stringify(new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = fx.OwnStart ?? string.Empty, ["session_id"] = "s-orch" }, 0));
            }

            var teamFile = fx.Roster.TeamPath(Hierarchy, "tm");
            TeamFile = teamFile;
            Directory.CreateDirectory(Path.GetDirectoryName(teamFile)!);
            var members = new JsonArray
            {
                new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["kind"] = "claude" },
                new JsonObject { ["name"] = "bot", ["role"] = "reviewer", ["kind"] = "custom", ["harness"] = "my-harness" },
            };
            // the admin's team records it with its start, so its orchestrator@tm registration is proven
            var orchestrator = new JsonObject { ["pid"] = Environment.ProcessId };
            if (admin)
            {
                orchestrator["pid_start"] = fx.OwnStart ?? string.Empty;
            }

            File.WriteAllText(teamFile, JsonText.Stringify(new JsonObject { ["orchestrator"] = orchestrator, ["members"] = members }, 0));
            var config = ConfigModule.CreateCore(fx.Files, fx.Environment, () => fx.Roster, new ProcessFacts(Root, 4242));
            var effects = new Effects();
            var lookups = MeshModule.CreateContractLookups(fx.Mailbox, fx.Hierarchy.Core, config);
            var links = MeshModule.CreateLinkCommands(fx.Rule, fx.Links, fx.Hierarchy.Core, fx.Hierarchy.Messages, fx.Files, fx.Clock, effects, effects);
            var joins = MeshModule.CreateJoinCommands(
                fx.Rule, fx.Links, fx.Hierarchy.Core, fx.Hierarchy.Messages, fx.Files, fx.Clock, fx.Mailbox, lookups, config, fx.Environment,
                new Inspector(), effects, effects);
            var roles = MeshModule.CreateContractRoles(config, ConfigModule.CreateRoleContracts(config, fx.Files, fx.Environment));
            var sender = MeshModule.CreateMeshSender(fx.Mailbox, fx.Rule, fx.Links, fx.Hierarchy, fx.Clock, fx.Writer, fx.Files, removedAt);
            Registry = new Registry();
            MeshModule.Register(
                Registry,
                new MeshRouteDependencies(
                    MeshModule.CreateContract(fx.Files, fx.Environment), roles, lookups, sendDelay is { } delay ? new SlowSender(sender, delay) : sender, joins, links, fx.Mailbox,
                    new Checker(check ?? (_ => null)), log, slowSendMs));
        }

        internal string Root { get; }

        internal string Hierarchy { get; }

        internal Registry Registry { get; }

        internal string ContextQuery => $"cwd={Uri.EscapeDataString(Root + "/proj")}&hierarchy_dir={Uri.EscapeDataString(Hierarchy)}";

        internal string ContextBody => $"\"cwd\":\"{Root}/proj\",\"hierarchy_dir\":\"{Hierarchy}\"";

        internal MeshFixture Fx { get; }

        internal string TeamFile { get; }

        internal bool Anonymous { get; set; }

        /// <summary>A record of <paramref name="team"/> kept in a second checkout this rig's request context does not see: its Orchestrator is this process unless <paramref name="orchestratorPid"/> says another, and <paramref name="members"/> are its member names.</summary>
        internal string OtherCheckoutTeam(string team, string[] members, int? orchestratorPid = null, string? raw = null)
        {
            var dir = Root + "/other/.claude/ah2/teams";
            Directory.CreateDirectory(dir);
            var file = dir + "/" + team + ".json";
            var rows = new JsonArray();
            foreach (var m in members)
            {
                rows.Add(new JsonObject { ["name"] = m, ["role"] = "implementor", ["kind"] = "claude" });
            }

            File.WriteAllText(file, raw ?? JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = orchestratorPid ?? Environment.ProcessId, ["pid_start"] = Fx.OwnStart ?? string.Empty }, ["members"] = rows }, 0));
            return file;
        }

        internal Task<(int Status, JsonValue Body)> Send(string to, string kind = "note", string? context = null) =>
            Call("POST /v1/send", body: $"{{\"to\":\"{to}\",\"kind\":\"{kind}\",\"text\":\"closing\",{context ?? ContextBody}}}");

        internal void Register(string session, string recordPath, string address) =>
            Fx.Prove(session, recordPath, new BoxRef(address, Fx.Mailbox.BoxDirFor(recordPath, address.Split('@')[0])));

        internal async Task<(int Status, JsonValue Body)> Call(string pattern, string query = "", string? body = null, params (string Key, string Value)[] path)
        {
            var response = await Registry.Routes[pattern](
                new RouteRequest(
                    pattern.Split(' ')[0], pattern.Split(' ')[1], query, new Dictionary<string, string>(),
                    body is null ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(body),
                    path.ToDictionary(p => p.Key, p => p.Value),
                    Anonymous ? null : Environment.ProcessId),
                CancellationToken.None);
            var parsed = response.Body.IsEmpty ? JsonValue.Null : JsonText.Parse(response.Body.ToArray());
            return (response.Status, parsed);
        }
    }

    private static JsonObject Error(JsonValue body) => body.AsObject()["error"].AsObject();

    [Fact]
    public void EveryRouteOfTheMeshIsRegisteredOnce()
    {
        var rig = new Rig();

        Assert.Equal(
            ["GET /v1/contract", "GET /v1/contract/guide", "GET /v1/contract/join", "GET /v1/contract/role", "GET /v1/contract/teams", "GET /v1/join/{id}", "POST /v1/join", "POST /v1/send"],
            rig.Registry.Routes.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task TeamsAreListedWhenAContextIsGivenAndNotOtherwise()
    {
        var rig = new Rig();

        var none = await rig.Call("GET /v1/contract/teams");
        var some = await rig.Call("GET /v1/contract/teams", rig.ContextQuery);

        Assert.Equal(200, none.Status);
        Assert.Empty(none.Body.AsArray());
        Assert.Equal(200, some.Status);
        Assert.Equal("tm", some.Body.AsArray()[0].AsObject()["team"].AsString());
        Assert.True(some.Body.AsArray()[0].AsObject()["orchestrator_live"].IsTruthy);
    }

    [Fact]
    public async Task AContextTheCheckerRefusesIsBadRequestNamingTheField()
    {
        var rig = new Rig(c => c.Cwd.EndsWith("/proj", StringComparison.Ordinal) ? new RequestContextProblem("cwd must be an absolute path", "cwd") : null);

        var teams = await rig.Call("GET /v1/contract/teams", rig.ContextQuery);
        var send = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"x\",{rig.ContextBody}}}");

        Assert.Equal(400, teams.Status);
        Assert.Equal("bad_request", Error(teams.Body)["code"].AsString());
        Assert.Equal("cwd", Error(teams.Body)["field"].AsString());
        Assert.Equal(400, send.Status);
    }

    [Fact]
    public async Task AnAdmittedCustomMemberIsAnswered()
    {
        var rig = new Rig();

        var ok = await rig.Call("GET /v1/contract/join", $"address=bot%40tm&{rig.ContextQuery}");

        Assert.Equal(200, ok.Status);
        Assert.Equal("bot@tm", ok.Body.AsObject()["address"].AsString());
        Assert.Equal("reviewer", ok.Body.AsObject()["role"].AsString());
    }

    [Theory]
    [InlineData("", 400, "bad_address")]
    [InlineData("address=bot", 400, "bad_address")]
    [InlineData("address=ghost%40tm", 404, "not_admitted")]
    [InlineData("address=imp%40tm", 404, "not_admitted")]
    public async Task AnAddressThatCannotJoinIsRefusedWithItsCode(string query, int status, string code)
    {
        var rig = new Rig();

        var r = await rig.Call("GET /v1/contract/join", query + (query.Length > 0 ? "&" : string.Empty) + rig.ContextQuery);

        Assert.Equal(status, r.Status);
        Assert.Equal(code, Error(r.Body)["code"].AsString());
    }

    [Fact]
    public async Task ASendBodyThatIsNotAnObjectIsBadRequest()
    {
        var rig = new Rig();

        var array = await rig.Call("POST /v1/send", body: "[]");
        var junk = await rig.Call("POST /v1/send", body: "{nope");

        Assert.Equal(400, array.Status);
        Assert.Equal("body must be a JSON object", Error(array.Body)["message"].AsString());
        Assert.Equal(400, junk.Status);
    }

    [Fact]
    public async Task ASendClaimingToBeTheDaemonIsRefused()
    {
        var rig = new Rig();

        var forged = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"kind\":\"event\",\"event\":\"relay-needed\",\"from\":\"ahd\",\"text\":\"x\",{rig.ContextBody}}}");
        var plain = await rig.Call("POST /v1/send", body: $"{{\"to\":\"ghost@tm\",\"text\":\"hello\",\"from\":\"user\",{rig.ContextBody}}}");

        Assert.Equal("from_mismatch", Error(forged.Body)["code"].AsString());
        Assert.NotEqual(200, forged.Status);
        Assert.Equal("unknown_address", Error(plain.Body)["code"].AsString());
    }

    [Fact]
    public async Task ASendFromACallerWhoseProcessIsUnknownIsRefused()
    {
        var rig = new Rig { Anonymous = true };

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",{rig.ContextBody}}}");

        Assert.Equal((403, "anonymous"), (r.Status, Error(r.Body)["code"].AsString()));
    }

    [Fact]
    public async Task TheUserMaySendOnlyAsUser()
    {
        var rig = new Rig();

        var asMember = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"from\":\"orchestrator@tm\",{rig.ContextBody}}}");
        var asSession = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"session_id\":\"s-orch\",{rig.ContextBody}}}");

        Assert.Equal("from_mismatch", Error(asMember.Body)["code"].AsString());
        Assert.Equal((403, "not_your_session"), (asSession.Status, Error(asSession.Body)["code"].AsString()));
    }

    /// <summary>ah2-150 D4: the admin sends as an address its own named session proves, keeping that session; nothing else it may claim.</summary>
    [Fact]
    public async Task TheAdminSendsAsTheOrchestratorItsNamedSessionHolds()
    {
        var rig = new Rig(admin: true);
        rig.Register("s-orch", rig.TeamFile, "orchestrator@tm");
        var principal = rig.Fx.Mailbox.PrincipalOf(Environment.ProcessId);
        Assert.Equal(PrincipalKind.User, principal.Kind);
        Assert.Equal(["orchestrator@tm"], Assert.Single(principal.Sessions).Addresses);

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"from\":\"orchestrator@tm\",\"session_id\":\"s-orch\",{rig.ContextBody}}}");
        var asUser = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hi\",\"from\":\"user\",{rig.ContextBody}}}");

        Assert.True(r.Status == 200, JsonText.Stringify(r.Body, 0));
        Assert.True(asUser.Status == 200, JsonText.Stringify(asUser.Body, 0));
        var froms = rig.Fx.Mailbox.ReadEntries(rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "imp")).Entries.Select(static e => e.Member("from")!.Value.AsString()).ToList();
        Assert.Equal(["orchestrator@tm", "user"], froms);
    }

    [Fact]
    public async Task NegativeTheAdminNamingASessionItDoesNotHoldIsRefused()
    {
        var rig = new Rig(admin: true);
        rig.Register("s-orch", rig.TeamFile, "orchestrator@tm");

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"from\":\"orchestrator@tm\",\"session_id\":\"s-other\",{rig.ContextBody}}}");

        // today's refusal of an unheld session_id comes first (CallerOf) and is kept as it is
        Assert.Equal((403, "not_your_session"), (r.Status, Error(r.Body)["code"].AsString()));
    }

    [Theory]
    [InlineData("imp@tm")]
    [InlineData("orchestrator@zz")]
    public async Task NegativeTheAdminsHeldSessionWithoutTheAddressIsRefused(string held)
    {
        var rig = new Rig(admin: true);
        rig.Register("s-orch", rig.TeamFile, held);

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"from\":\"orchestrator@tm\",\"session_id\":\"s-orch\",{rig.ContextBody}}}");

        Assert.Equal((403, "from_mismatch"), (r.Status, Error(r.Body)["code"].AsString()));
    }

    [Fact]
    public async Task NegativeTheAdminWithNoSessionIdMaySendOnlyAsUser()
    {
        var rig = new Rig(admin: true);
        rig.Register("s-orch", rig.TeamFile, "orchestrator@tm");

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"from\":\"orchestrator@tm\",{rig.ContextBody}}}");

        Assert.Equal((403, "from_mismatch"), (r.Status, Error(r.Body)["code"].AsString()));
    }

    private static string Code(JsonValue body) => body.IsMap && body.AsObject().ContainsKey("error") ? Error(body)["code"].AsString() : string.Empty;

    private static readonly string[] SenderRefusals = ["removed_member", "team_gone", "team_mismatch", "team_unreadable", "team_unresolved", "from_mismatch"];

    [Fact]
    public async Task ARemovedMemberStillAliveSendsANoteToItsOrchestratorWhenTheRemovalTimeIsUnknown()
    {
        var rig = new Rig();
        rig.Register("s-gone", rig.TeamFile, "gone@tm");

        var r = await rig.Send("orchestrator@tm");

        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task ASendSlowerThanTheBoundLogsItsLapsAndAFastOneLogsNothing()
    {
        var lines = new List<string>();
        var slow = new Rig(log: lines.Add, slowSendMs: 20, sendDelay: TimeSpan.FromMilliseconds(80));
        var fast = new Rig(log: lines.Add);

        var r = await slow.Send("orchestrator@tm");
        var line = Assert.Single(lines);
        var quick = await fast.Send("orchestrator@tm");

        Assert.Equal(200, r.Status);
        Assert.Equal(200, quick.Status);
        Assert.Single(lines);
        Assert.StartsWith("send slow: ", line, StringComparison.Ordinal);
        foreach (var lap in new[] { "context=", "caller=", "prepare=", "resolve=", "append=", "describe=", "send=" })
        {
            Assert.Contains(lap, line, StringComparison.Ordinal);
        }

        // the delay sits before the mailbox send's first mark, so it lands in its "resolve" lap
        var resolve = long.Parse(line.Split(' ').First(p => p.StartsWith("resolve=", StringComparison.Ordinal))["resolve=".Length..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(resolve >= 70, line);
    }

    [Theory]
    [InlineData("imp@tm", "note")]
    [InlineData("bot@tm", "report")]
    [InlineData("orchestrator@tm", "brief")]
    [InlineData("orchestrator@tm", "event")]
    public async Task ARemovedMembersOtherSendsAre403RemovedMember(string to, string kind)
    {
        var rig = new Rig();
        rig.Register("s-gone", rig.TeamFile, "gone@tm");

        var r = await rig.Send(to, kind);

        Assert.Equal((403, "removed_member"), (r.Status, Code(r.Body)));
    }

    [Theory]
    [InlineData(29, 200)]
    [InlineData(31, 403)] // negative
    public async Task AnAnsweredBriefStaysFreshForThirtyMinutesFromTheStoredResponseWithNoFile(int minutesAgo, int status)
    {
        var rig = new Rig();
        rig.Register("s-imp", rig.TeamFile, "imp@tm");
        const string Id = "20260101-000000-aa11";
        var msgs = rig.Hierarchy + "/msgs";
        Directory.CreateDirectory(msgs);
        var request = $"{msgs}/{Id}--imp--work--request.md";
        var response = $"{msgs}/{Id}--bot--work--response.md";
        File.WriteAllText(request, $"---\nid: {Id}\ntype: request\nto: imp\nfrom: bot\nslug: work\nteam: tm\ncreated: 2026-01-01T00:00:00+00:00\n---\n\n## [0] tldr\n- x\n");
        File.WriteAllText(response, $"---\nid: {Id}\ntype: response\nto: bot\nfrom: imp\nslug: work\nteam: tm\ncreated: 2026-01-01T00:00:00+00:00\n---\n\n## [0] tldr\n- [1] status: DONE\n");
        File.SetLastWriteTimeUtc(response, Now.AddMinutes(-minutesAgo).UtcDateTime);
        var box = rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "imp");
        Directory.CreateDirectory(box);
        File.WriteAllText(box + "/entries.jsonl", "{\"id\":\"e1\",\"kind\":\"brief\",\"ref\":\"" + request + "\",\"from\":\"bot@tm\",\"to\":\"imp@tm\",\"created\":\"2026-01-01T00:00:00.000Z\"}\n");
        TestMsgStore.DropFiles(rig.Root + "/ah", rig.Root);

        var r = await rig.Send("bot@tm");

        Assert.Equal(status, r.Status);
    }

    [Theory]
    [InlineData(29, 200)]
    [InlineData(31, 403)]
    public async Task ARemovedMembersAllowanceLastsThirtyMinutesFromAKnownRemoval(int minutesAgo, int status)
    {
        var rig = new Rig(removedAt: (_, _) => Now.AddMinutes(-minutesAgo));
        rig.Register("s-gone", rig.TeamFile, "gone@tm");

        var r = await rig.Send("orchestrator@tm");

        Assert.Equal(status, r.Status);
        Assert.Equal(status == 403 ? "removed_member" : string.Empty, Code(r.Body));
    }

    [Fact]
    public async Task ARemovedMemberWhoseOrchestratorIsNotLiveGetsTeamGone()
    {
        var rig = new Rig();
        var archived = rig.OtherCheckoutTeam("ar", ["imp"], orchestratorPid: 2147483646);
        rig.Register("s-gone", archived, "gone@ar");

        var r = await rig.Send("orchestrator@ar");

        Assert.Equal((403, "team_gone"), (r.Status, Code(r.Body)));
    }

    /// <summary>The real processes, with one shown as another user's: alive to kill-0 (EPERM), never owned.</summary>
    private sealed class NotOwned(IProcessInspector real, int foreign) : IProcessInspector
    {
        public bool PidAlive(int pid) => real.PidAlive(pid);

        public bool PidOwned(int pid) => pid != foreign && real.PidOwned(pid);

        public string? CommandLine(int pid) => real.CommandLine(pid);
    }

    // one-liveness.md site 6: an orchestrator record naming another user's process is not running; kill-0 read it running before
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARemovedMemberGetsTeamGoneWhenItsOrchestratorsPidIsAnotherUsers(bool foreign)
    {
        var rig = new Rig();
        var real = Ah.Platform.PlatformModule.CreateIo(null).Processes;
        // the orchestrator is the test host's parent: the sender, this host, stays its own user's
        var parent = rig.Fx.Table.Ancestors(Environment.ProcessId)![0];
        rig.Fx.Hierarchy.Core.Liveness = MeshModule.CreateMemberLiveness(rig.Fx.Mailbox, rig.Fx.Table, foreign ? new NotOwned(real, parent) : real);
        var orchestrator = new JsonObject { ["pid"] = parent, ["pid_start"] = rig.Fx.Table.StartTime(parent)! };
        var archived = rig.OtherCheckoutTeam("ar", [], raw: JsonText.Stringify(new JsonObject { ["orchestrator"] = orchestrator, ["members"] = new JsonArray() }, 0));
        rig.Register("s-gone", archived, "gone@ar");

        var r = await rig.Send("orchestrator@ar");

        Assert.Equal(foreign, (r.Status, Code(r.Body)) == (403, "team_gone"));
    }

    [Fact]
    public async Task ASessionWhoseTeamRecordIsGoneGetsTeamGone()
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("ar", ["imp"]);
        rig.Register("s-gone", file, "imp@ar");
        File.Delete(file);

        var r = await rig.Send("orchestrator@ar");

        Assert.Equal((403, "team_gone"), (r.Status, Code(r.Body)));
    }

    [Fact]
    public async Task ASessionWhoseTeamRecordIsMalformedGetsTeamUnreadableAndIsNeverAllowed()
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("ar", [], raw: "{nope");
        rig.Register("s-bad", file, "imp@ar");

        var r = await rig.Send("orchestrator@ar");

        Assert.Equal((403, "team_unreadable"), (r.Status, Code(r.Body)));
    }

    [Fact]
    public async Task AMemberOfATeamKeptInAnotherCheckoutIsNotRefused()
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("ot", ["imp"]);
        rig.Register("s-other", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.DoesNotContain(Code(r.Body), SenderRefusals);
        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task ARegistrationKeptInAnotherCheckoutUnderTheNameOfATeamTheContextSeesIsTeamMismatch()
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("tm", ["gone"]);
        rig.Register("s-other", file, "gone@tm");

        var r = await rig.Send("orchestrator@tm");

        Assert.Equal((403, "team_mismatch"), (r.Status, Code(r.Body)));
    }

    [Fact]
    public async Task AMemberTheTeamRecordStillListsIsNeverTreatedAsRemoved()
    {
        var rig = new Rig();
        rig.Register("s-imp", rig.TeamFile, "imp@tm");

        var r = await rig.Send("bot@tm");

        Assert.NotEqual("removed_member", Code(r.Body));
    }

    [Fact]
    public async Task ASendFromAWorktreeWhoseCwdIsNotTheTeamsCheckoutIsNotRefusedAsAnOutsideParty()
    {
        var rig = new Rig();
        rig.Register("s-imp", rig.TeamFile, "imp@tm");
        Directory.CreateDirectory(rig.Root + "/wt");

        var r = await rig.Send("orchestrator@tm", context: $"\"cwd\":\"{rig.Root}/wt\",\"hierarchy_dir\":\"{rig.Hierarchy}\"");

        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task TheOrchestratorOfATeamKeptInAnotherCheckoutIsNotRefusedAsAnOutsideParty()
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("ot", ["imp"]);
        rig.Register("s-orch-ot", file, "orchestrator@ot");

        var r = await rig.Send("imp@ot");

        Assert.DoesNotContain(Code(r.Body), SenderRefusals);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2147483646)]
    public async Task AStaleSessionRegisteredAsAnOrchestratorWhoseTeamFileNamesAnotherProcessHasNoOrchestratorRights(int filePid)
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("ot", ["imp"], orchestratorPid: filePid);
        rig.Register("s-stale", file, "orchestrator@ot");

        var r = await rig.Send("imp@ot");

        Assert.Equal((403, "team_mismatch"), (r.Status, Code(r.Body)));
    }

    [Theory]
    [InlineData(2147483646, "is gone", "ah roster recover --team ot")]
    [InlineData(1, "is alive", "ah roster recover --team ot")]
    public async Task AMemberRowHeldByAnotherProcessIsRefusedWithTheHoldersStateAndTheRemedy(int rowPid, string state, string remedy)
    {
        var rig = new Rig();
        var raw = JsonText.Stringify(
            new JsonObject
            {
                ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId },
                ["members"] = new JsonArray { new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["kind"] = "claude", ["pid"] = rowPid, ["transport_id"] = "p_1" } },
            },
            0);
        var file = rig.OtherCheckoutTeam("ot", ["imp"], raw: raw);
        rig.Register("s-imp", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.Equal((403, "team_mismatch"), (r.Status, Code(r.Body)));
        var message = Error(r.Body)["message"].AsString();
        Assert.Contains(state, message);
        Assert.Contains(remedy, message);
    }

    [Theory]
    [InlineData("bare", "recover now --team ot imp", "ah roster recover --team ot`,")]
    [InlineData("bare-unmatched", "ah roster recover --team ot`", "recover now")]
    public async Task ADeadRowMarkedBareIsToldTheRelaunchPathBecauseRecoverLeavesItAlone(string mark, string remedy, string notRemedy)
    {
        var rig = new Rig();
        var raw = JsonText.Stringify(
            new JsonObject
            {
                ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId },
                ["members"] = new JsonArray { new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["kind"] = "claude", ["pid"] = 2147483646, ["transport_id"] = "p_1", ["bare"] = new JsonObject { ["state"] = mark } } },
            },
            0);
        var file = rig.OtherCheckoutTeam("ot", ["imp"], raw: raw);
        rig.Register("s-imp", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.Equal((403, "team_mismatch"), (r.Status, Code(r.Body)));
        var message = Error(r.Body)["message"].AsString();
        Assert.Contains(remedy, message);
        Assert.DoesNotContain(notRemedy, message);
    }

    [Theory]
    [InlineData("bare", "s-imp", 200)]
    [InlineData("bare", "s-other", 403)]
    [InlineData("bare-unmatched", "s-imp", 403)]
    public async Task ARelaunchedMemberOverADeadBareRowWithItsSessionIsAcceptedAndRebound(string mark, string rowSession, int status)
    {
        var rig = new Rig();
        var raw = JsonText.Stringify(
            new JsonObject
            {
                ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId },
                ["members"] = new JsonArray { new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["kind"] = "claude", ["pid"] = 2147483646, ["pid_start"] = "Thu Jan  1 00:00:00 1970", ["session_id"] = rowSession, ["transport_id"] = "p_1", ["bare"] = new JsonObject { ["state"] = mark } } },
            },
            0);
        var file = rig.OtherCheckoutTeam("ot", ["imp"], raw: raw);
        rig.Register("s-imp", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.Equal(status, r.Status);
        var row = JsonText.Parse(File.ReadAllText(file)).Member("members")!.Value.AsArray()[0];
        Assert.Equal(status == 200 ? Environment.ProcessId : 2147483646, (int)row.Member("pid")!.Value.AsNumber());
    }

    [Fact]
    public async Task AMemberRowReStampedWithTheLiveSessionsPidStartAndSessionAcceptsItsSend()
    {
        var rig = new Rig();
        var raw = JsonText.Stringify(
            new JsonObject
            {
                ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId },
                ["members"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "imp", ["role"] = "implementor", ["kind"] = "claude", ["transport_id"] = "p_1",
                        ["pid"] = Environment.ProcessId, ["pid_start"] = rig.Fx.OwnStart ?? string.Empty, ["session_id"] = "s-imp",
                    },
                },
            },
            0);
        var file = rig.OtherCheckoutTeam("ot", ["imp"], raw: raw);
        rig.Register("s-imp", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.DoesNotContain(Code(r.Body), SenderRefusals);
    }

    [Fact]
    public async Task ARegistrationThatIsTheTeamFilesOrchestratorKeepsItsRights()
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("ot", ["imp"]);
        rig.Register("s-orch", file, "orchestrator@ot");

        var r = await rig.Send("imp@ot");

        Assert.DoesNotContain(Code(r.Body), SenderRefusals);
    }

    [Fact]
    public async Task AnOrchestratorWhoseRecordedStartDiffersFromTheRegistrationsIsAReusedPid()
    {
        var rig = new Rig();
        var file = rig.OtherCheckoutTeam("ot", ["imp"], raw: JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = "Thu Jan  1 00:00:00 1970" }, ["members"] = new JsonArray { new JsonObject { ["name"] = "imp" } } }, 0));
        rig.Register("s-orch", file, "orchestrator@ot");

        var r = await rig.Send("imp@ot");

        Assert.Equal((403, "team_mismatch"), (r.Status, Code(r.Body)));
    }

    [Theory]
    [InlineData("s-new", 403)]
    [InlineData("s-imp", 200)]
    public async Task AMemberRowThatRecordsAnotherSessionRefusesTheOldOne(string rowSession, int status)
    {
        var rig = new Rig();
        var row = new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["session_id"] = rowSession };
        var file = rig.OtherCheckoutTeam("ot", [], raw: JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = rig.Fx.OwnStart ?? string.Empty }, ["members"] = new JsonArray { row } }, 0));
        rig.Register("s-imp", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.Equal(status, r.Status);
        Assert.Equal(status == 403 ? "team_mismatch" : string.Empty, Code(r.Body));
    }

    [Theory]
    [InlineData("s-old", true, 200)]
    [InlineData("s-old", false, 403)]
    [InlineData("s-imp", false, 403)]
    public async Task AMemberRowsPidProvesTheSessionWhateverItsSessionIdSaysAndNoSessionIdDoesWhenTheRowRecordsAPid(string rowSession, bool rowPidIsTheRegistrations, int status)
    {
        var rig = new Rig();
        var row = new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["session_id"] = rowSession, ["pid"] = rowPidIsTheRegistrations ? Environment.ProcessId : 1, ["pid_start"] = rowPidIsTheRegistrations ? rig.Fx.OwnStart ?? string.Empty : "Thu Jan  1 00:00:00 1970" };
        var file = rig.OtherCheckoutTeam("ot", [], raw: JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = rig.Fx.OwnStart ?? string.Empty }, ["members"] = new JsonArray { row } }, 0));
        rig.Register("s-imp", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.Equal(status, r.Status);
        Assert.Equal(status == 403 ? "team_mismatch" : string.Empty, Code(r.Body));
        if (status == 403)
        {
            Assert.Contains("row pid 1, registration pid ", Error(r.Body)["message"].AsString(), StringComparison.Ordinal);
            Assert.Contains("row start Thu Jan  1 00:00:00 1970", Error(r.Body)["message"].AsString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ARegistrationThatCopiesTheOrchestratorsSessionIdUnderItsOwnPidIsNotTheOrchestrator()
    {
        var rig = new Rig();
        var held = rig.OtherCheckoutTeam("ot", ["imp"], raw: JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = 1, ["session_id"] = "s-orch" }, ["members"] = new JsonArray { new JsonObject { ["name"] = "imp" } } }, 0));
        rig.Register("s-orch", held, "orchestrator@ot");

        var r = await rig.Send("imp@ot");

        Assert.Equal((403, "team_mismatch"), (r.Status, Code(r.Body)));
    }

    [Fact]
    public async Task ARowThatRecordsAStartRefusesARegistrationWhosePidNumberMatchesButWhoseStartDiffers()
    {
        var rig = new Rig();
        var row = new JsonObject { ["name"] = "imp", ["role"] = "implementor", ["pid"] = Environment.ProcessId, ["pid_start"] = "Thu Jan  1 00:00:00 1970" };
        var file = rig.OtherCheckoutTeam("ot", [], raw: JsonText.Stringify(new JsonObject { ["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = rig.Fx.OwnStart ?? string.Empty }, ["members"] = new JsonArray { row } }, 0));
        rig.Register("s-imp", file, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.Equal((403, "team_mismatch"), (r.Status, Code(r.Body)));
    }

    [Fact]
    public async Task ARegistrationWhoseTeamFileIsNotTheNamedFileOfItsTeamIsUnresolved()
    {
        var rig = new Rig();
        var other = rig.Root + "/other/.claude/ah2/teams/elsewhere.json";
        File.Copy(rig.OtherCheckoutTeam("ot", ["imp"]), other);
        rig.Register("s-imp", other, "imp@ot");

        var r = await rig.Send("orchestrator@ot");

        Assert.Equal((403, "team_unresolved"), (r.Status, Code(r.Body)));
    }

    [Fact]
    public async Task ASubOrchestratorIsAMemberOfItsParentAndTheOrchestratorOfItsChildAndSendsAsEither()
    {
        var rig = new Rig();
        var child = rig.OtherCheckoutTeam("ch", ["w1"]);
        rig.Fx.Prove("s-so", rig.TeamFile, new BoxRef("imp@tm", rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "imp")));
        rig.Fx.Prove("s-so", child, new BoxRef("orchestrator@ch", rig.Fx.Mailbox.BoxDirFor(child, "orchestrator")));

        var asMember = await rig.Send("orchestrator@tm");
        var asChildOrchestrator = await rig.Call("POST /v1/send", body: $"{{\"to\":\"w1@ch\",\"from\":\"orchestrator@ch\",\"kind\":\"note\",\"text\":\"x\",{rig.ContextBody}}}");

        Assert.Equal(200, asMember.Status);
        Assert.DoesNotContain(Code(asChildOrchestrator.Body), SenderRefusals);
    }

    [Fact]
    public async Task ARegisteredSessionWhoseAddressNamesNoTeamMemberDoesNotSendAsAnOutsideParty()
    {
        var rig = new Rig();
        rig.Fx.Prove("s-ghost", rig.TeamFile, new BoxRef("ghost@tm", rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "ghost")));

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",{rig.ContextBody}}}");

        Assert.Equal((403, "removed_member"), (r.Status, Error(r.Body)["code"].AsString()));
    }

    [Fact]
    public async Task ARegisteredMemberWhoseAddressIsInTheTeamFileIsNotRefusedAsAnOutsideParty()
    {
        var rig = new Rig();
        rig.Fx.Prove("s-imp", rig.TeamFile, new BoxRef("imp@tm", rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "imp")));

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"orchestrator@tm\",\"text\":\"hello\",{rig.ContextBody}}}");

        Assert.Equal(200, r.Status);
    }

    [Fact]
    public async Task ASessionSendsOnlyAsItsOwnProvenAddresses()
    {
        var rig = new Rig();
        rig.Fx.Prove("s-imp", rig.TeamFile, new BoxRef("imp@tm", rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "imp")));

        var impersonated = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"from\":\"orchestrator@tm\",{rig.ContextBody}}}");
        var otherSession = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"session_id\":\"s-orch\",{rig.ContextBody}}}");
        var asUser = await rig.Call("POST /v1/send", body: $"{{\"to\":\"imp@tm\",\"text\":\"hello\",\"from\":\"user\",{rig.ContextBody}}}");

        Assert.Equal("from_mismatch", Error(impersonated.Body)["code"].AsString());
        Assert.Equal((403, "not_your_session"), (otherSession.Status, Error(otherSession.Body)["code"].AsString()));
        Assert.Equal("from_mismatch", Error(asUser.Body)["code"].AsString());
    }

    [Fact]
    public async Task ASendToAnUnknownAddressIsAnsweredWithItsRefusal()
    {
        var rig = new Rig();

        var r = await rig.Call("POST /v1/send", body: $"{{\"to\":\"ghost@tm\",\"text\":\"hello\",{rig.ContextBody}}}");

        Assert.Equal(404, r.Status);
        Assert.Equal("unknown_address", Error(r.Body)["code"].AsString());
    }

    [Fact]
    public async Task AJoinIsOpenedThenAnsweredAsExistingAndPolled()
    {
        var rig = new Rig();
        var body = $"{{\"team\":\"tm\",\"harness\":\"my-harness\",\"session_id\":\"s1\",\"why\":\"to review\",{rig.ContextBody}}}";

        var first = await rig.Call("POST /v1/join", body: body);
        var again = await rig.Call("POST /v1/join", body: body);
        var id = first.Body.AsObject()["join_id"].AsString();
        var poll = await rig.Call("GET /v1/join/{id}", rig.ContextQuery, path: ("id", id));

        Assert.Equal(202, first.Status);
        Assert.Equal("pending", first.Body.AsObject()["status"].AsString());
        Assert.Null(first.Body.AsObject().Get("existing"));
        Assert.Equal(200, again.Status);
        Assert.Equal(id, again.Body.AsObject()["join_id"].AsString());
        Assert.Null(again.Body.AsObject().Get("existing"));
        Assert.Equal(200, poll.Status);
        Assert.Equal("pending", poll.Body.AsObject()["status"].AsString());
    }

    [Theory]
    [InlineData("nope", "my-harness", 404, "unknown_team")]
    [InlineData("tm", "claude", 400, "bad_request")]
    public async Task AJoinThatBreaksARuleIsRefusedWithItsStatus(string team, string harness, int status, string code)
    {
        var rig = new Rig();

        var r = await rig.Call(
            "POST /v1/join",
            body: $"{{\"team\":\"{team}\",\"harness\":\"{harness}\",\"session_id\":\"s1\",\"why\":\"w\",{rig.ContextBody}}}");

        Assert.Equal(status, r.Status);
        Assert.Equal(code, Error(r.Body)["code"].AsString());
    }

    [Fact]
    public async Task AJoinBodyThatIsNotAnObjectIsBadRequest()
    {
        var rig = new Rig();

        var r = await rig.Call("POST /v1/join", body: "[]");

        Assert.Equal(400, r.Status);
        Assert.Equal("body must be a JSON object", Error(r.Body)["message"].AsString());
    }

    [Fact]
    public async Task APollOfAnUnknownJoinIsNotFound()
    {
        var rig = new Rig();

        var r = await rig.Call("GET /v1/join/{id}", rig.ContextQuery, path: ("id", "nothing"));

        Assert.Equal(404, r.Status);
        Assert.Equal("not_found", Error(r.Body)["code"].AsString());
    }
}
