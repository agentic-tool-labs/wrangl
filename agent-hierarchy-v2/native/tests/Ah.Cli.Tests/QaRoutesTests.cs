using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.State;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The <c>/v1/qa/*</c> routes over the real QA queue: who may call what, which repositories may be queued, and that nothing runs at submit.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class QaRoutesTests : IDisposable
{
    private const long StartMs = 1_800_000_000_000;
    private static readonly string Sha = new('a', 40);

    private readonly string _root = Directory.CreateTempSubdirectory("ahqr").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class EngineWriter(Func<Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult>?> engine) : IStateWriter
    {
        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null) =>
            engine()!(domain, op, argsJson, context, Guid.NewGuid().ToString("N"), TimeSpan.FromSeconds(5));

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
            throw new NotSupportedException();
    }

    private sealed class Registry : IRouteRegistry
    {
        public Dictionary<string, Func<RouteRequest, CancellationToken, Task<RouteResponse>>> Handlers { get; } = [];

        public void Add(string pattern, Func<RouteRequest, CancellationToken, Task<RouteResponse>> handler) => Handlers[pattern] = handler;
    }

    private sealed class Rig
    {
        private int _ids;
        private Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult>? _engine;
        private readonly Registry _registry = new();

        public Rig(string root)
        {
            Main = root + "/main";
            Tree = root + "/tree";
            Outside = root + "/outside";
            Results = root + "/results";
            foreach (var dir in new[] { Main + "/.claude/ah2/teams", Main + "/.git/worktrees/tree", Main + "/tests", Tree, Outside, Results, root + "/main-extra", root + "/ah", root + "/home" })
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(Main + "/.git/worktrees/tree/gitdir", Tree + "/.git\n");
            File.WriteAllText(Tree + "/.git", "gitdir: " + Main + "/.git/worktrees/tree\n");
            File.CreateSymbolicLink(Main + "/link", Outside);
            TeamFile = Main + "/.claude/ah2/teams/t.json";
            File.WriteAllText(TeamFile, "{}");
            Marker = root + "/ran";
            File.WriteAllText(Main + "/tests/run-set.sh", "#!/bin/sh\ntouch '" + Marker + "'\n");
            File.SetUnixFileMode(Main + "/tests/run-set.sh", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var vars = new Dictionary<string, string> { ["HOME"] = root + "/home", ["AH_HOME"] = root + "/ah", ["AH_TEST"] = "1", ["TZ"] = "UTC" };
            var env = new FakeEnvironment(vars, new PluginInfo("/ahroot", "wrangl", "0.0.0", "1"));
            var io = PlatformModule.CreateIo(null);
            var facts = new ProcessFacts(root, 4242);
            IRosterCore roster = null!;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster, facts);
            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
            var services = HierarchyModule.Create(
                new HierarchyEnvironment(io.Files, env, Clock, new SequenceRandom(), io.Processes, config, roster, new EngineWriter(() => _engine), facts, HierarchyModule.CreateBootstrap(io.Writer)) { MsgsRoute = TestMsgStore.Route(env.AhHome), QaJobBodies = () => Ah.StateDb.StateDbModule.QaJobBodies(env.AhHome) });
            _engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, io.Clock, services.StateDomains, 4242, Ah.StateDb.StateDbModule.CreateOpRunner(env.AhHome), Ah.StateDb.StateDbModule.CreateModeReader(env.AhHome));
            var qa = HierarchyModule.CreateQa(
                services,
                new QaDependencies(new FakeHostProbe(new HostSnapshot(0.5, 8, 16)), Clock, io.Files, Live.Contains, () => "e" + (++_ids), [], Results, _ => { }, _ => { }));
            new QaRoutes(qa, pid => Principals.GetValueOrDefault(pid ?? 0, CallerPrincipal.Anonymous), session => session == "no-team" ? null : TeamFile, io.Files).Register(_registry);
        }

        public string Main { get; }

        public string Tree { get; }

        public string Outside { get; }

        public string Results { get; }

        public string TeamFile { get; }

        public string Marker { get; }

        public FakeClock Clock { get; } = new(DateTimeOffset.FromUnixTimeMilliseconds(StartMs));

        public HashSet<string> Live { get; } = new(StringComparer.Ordinal) { "qa1@t", "qa2@t" };

        public Dictionary<int, CallerPrincipal> Principals { get; } = new()
        {
            [1] = Session("impl", "impl@t"),
            [2] = Session("rev", "rev@t"),
            [3] = Session("s-qa1", "qa1@t"),
            [4] = Session("s-qa2", "qa2@t"),
            [5] = CallerPrincipal.User,
            [6] = Session("s-orch", "orchestrator@t"),
            [7] = Session("s-orch-u", "orchestrator@u"),
            [8] = Session("no-team", "lone@t"),
        };

        private static CallerPrincipal Session(string id, string address) => new(PrincipalKind.Session, [new SessionRegistration(id, 1000, null, [address])]);

        public (int Status, JsonObject Body) Call(string route, int? pid, object? body = null)
        {
            var text = body switch
            {
                null => string.Empty,
                string s => s,
                JsonObject o => JsonText.Stringify(o, 0),
                _ => throw new ArgumentException("a body is null, text or a JsonObject", nameof(body)),
            };
            var response = _registry.Handlers["POST /v1/qa/" + route](
                new RouteRequest("POST", "/v1/qa/" + route, string.Empty, new Dictionary<string, string>(), System.Text.Encoding.UTF8.GetBytes(text), new Dictionary<string, string>(), pid),
                CancellationToken.None).GetAwaiter().GetResult();
            return (response.Status, JsonText.Parse(response.Body.Span).AsObject());
        }

        public JsonObject Submit(int pid, string? repo = null, string set = "core", bool fresh = false)
        {
            var (status, body) = Call("submit", pid, new JsonObject { ["repo"] = repo ?? Main, ["sha"] = Sha, ["set"] = set, ["fresh"] = fresh });
            Assert.Equal(200, status);
            return body["entry"].AsObject();
        }

        public string Claimed()
        {
            var id = Submit(1)["id"].AsString();
            var (status, body) = Call("next", 3, new JsonObject());
            Assert.Equal(200, status);
            Assert.Equal(id, body["entry"].AsObject()["id"].AsString());
            return id;
        }

        public static string ErrorCode(JsonObject body) => body["error"].AsObject()["code"].AsString();
    }

    private Rig NewRig() => new(_root);

    [Fact]
    public void TwoCallersOfTheSameCommitAndSetShareOneEntryWithTwoRequesters()
    {
        var rig = NewRig();

        var first = rig.Submit(1);
        var second = rig.Submit(2);

        Assert.Equal(first["id"].AsString(), second["id"].AsString());
        Assert.Equal(["impl@t", "rev@t"], second["requesters"].AsArray().Select(r => r.AsString()));
        Assert.Equal("t", second["team"].AsString());
        Assert.Single(rig.Call("list", 5, new JsonObject { ["all"] = true }).Body["entries"].AsArray());
    }

    [Fact]
    public void ARequesterIsTheCallersProvenAddressAndNeverAFieldOfTheBody()
    {
        var rig = NewRig();

        var (status, body) = rig.Call("submit", 1, new JsonObject { ["repo"] = rig.Main, ["sha"] = Sha, ["set"] = "core", ["requester"] = "orchestrator@t", ["requesters"] = new JsonArray { "x@t" } });

        Assert.Equal(200, status);
        Assert.Equal(["impl@t"], body["entry"].AsObject()["requesters"].AsArray().Select(r => r.AsString()));
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("next")]
    [InlineData("done")]
    public void TheUserAndAnUnprovenCallerAreRefusedWhereASessionIsNeeded(string route)
    {
        var rig = NewRig();
        var body = new JsonObject { ["repo"] = rig.Main, ["sha"] = Sha, ["set"] = "core", ["id"] = "e1", ["error"] = "x" };

        foreach (var pid in new int?[] { 5, 99, null })
        {
            var (status, answer) = rig.Call(route, pid, body);
            Assert.Equal(403, status);
            Assert.Equal("qa-caller-unproven", Rig.ErrorCode(answer));
        }

        Assert.Empty(rig.Call("list", 5, new JsonObject { ["all"] = true }).Body["entries"].AsArray());
    }

    [Fact]
    public void ASessionHintNamingAnotherPrincipalsSessionIsRefusedOnEveryRouteThatTakesOne()
    {
        var rig = NewRig();
        var id = rig.Claimed();

        foreach (var (route, body) in new (string, JsonObject)[]
        {
            ("submit", new JsonObject { ["repo"] = rig.Main, ["sha"] = Sha, ["set"] = "core", ["session_id"] = "s-qa1" }),
            ("next", new JsonObject { ["session_id"] = "s-qa1" }),
            ("done", new JsonObject { ["id"] = id, ["error"] = "x", ["session_id"] = "s-qa1" }),
            ("release", new JsonObject { ["id"] = id, ["session_id"] = "s-qa1" }),
        })
        {
            var (status, answer) = rig.Call(route, 1, body);
            Assert.Equal(403, status);
            Assert.Equal("qa-caller-unproven", Rig.ErrorCode(answer));
        }

        Assert.Equal("claimed", rig.Call("status", 5, new JsonObject { ["id"] = id }).Body["entry"].AsObject()["state"].AsString());
    }

    [Fact]
    public void ASessionHintNamingTheCallersOwnSessionIsAccepted()
    {
        var rig = NewRig();

        var (status, body) = rig.Call("submit", 1, new JsonObject { ["repo"] = rig.Main, ["sha"] = Sha, ["set"] = "core", ["session_id"] = "impl" });

        Assert.Equal(200, status);
        Assert.Equal(["impl@t"], body["entry"].AsObject()["requesters"].AsArray().Select(r => r.AsString()));
    }

    [Fact]
    public void AClaimIsMadeForTheCallersAddressAndNeverForAnotherNamedInTheBody()
    {
        var rig = NewRig();
        rig.Submit(1);

        var (_, body) = rig.Call("next", 3, new JsonObject { ["claimer"] = "qa2@t" });

        Assert.Equal("qa1@t", body["entry"].AsObject()["claimed_by"].AsString());
    }

    [Fact]
    public void DoneByAnotherSessionIsRefusedAndTheClaimerEndsTheEntry()
    {
        var rig = NewRig();
        var id = rig.Claimed();

        var (status, refused) = rig.Call("done", 4, new JsonObject { ["id"] = id, ["error"] = "x" });
        Assert.Equal(403, status);
        Assert.Equal("not-claimer", Rig.ErrorCode(refused));

        var (okStatus, ended) = rig.Call("done", 3, new JsonObject { ["id"] = id, ["error"] = "qa-unknown-set: nope" });
        Assert.Equal(200, okStatus);
        Assert.Equal("failed", ended["entry"].AsObject()["state"].AsString());
    }

    [Fact]
    public void AResultOutsideTheStoredResultDirectoryIsRefused()
    {
        var rig = NewRig();
        var id = rig.Claimed();
        var elsewhere = rig.Outside + "/result.json";
        File.WriteAllText(elsewhere, """{"status":"PASS"}""");

        var (status, body) = rig.Call("done", 3, new JsonObject { ["id"] = id, ["result"] = elsewhere });

        Assert.Equal(400, status);
        Assert.Equal("qa-bad-result", Rig.ErrorCode(body));
    }

    [Fact]
    public void AResultInsideTheStoredResultDirectoryEndsTheEntryWithItsStatus()
    {
        var rig = NewRig();
        var id = rig.Claimed();
        Directory.CreateDirectory(rig.Results + "/" + Sha + "/core");
        var path = rig.Results + "/" + Sha + "/core/result.json";
        File.WriteAllText(path, """{"status":"FAIL"}""");

        var (status, body) = rig.Call("done", 3, new JsonObject { ["id"] = id, ["result"] = path });

        Assert.Equal(200, status);
        Assert.Equal("done", body["entry"].AsObject()["state"].AsString());
        Assert.Equal("FAIL", body["entry"].AsObject()["verdict"].AsString());
    }

    [Fact]
    public void TheUserMayReleaseAClaimAndSoMayTheClaimerAndTheOrchestratorOfItsTeam()
    {
        var rig = NewRig();
        foreach (var pid in new[] { 5, 3, 6 })
        {
            var id = rig.Claimed();
            var (status, body) = rig.Call("release", pid, new JsonObject { ["id"] = id });
            Assert.Equal(200, status);
            Assert.Equal("queued", body["entry"].AsObject()["state"].AsString());
            rig.Call("done", 3, new JsonObject { ["id"] = id, ["error"] = "x" });
        }
    }

    [Fact]
    public void AnotherSessionAndAnotherTeamsOrchestratorMayNotReleaseAClaim()
    {
        var rig = NewRig();
        var id = rig.Claimed();

        foreach (var pid in new[] { 4, 1, 7 })
        {
            var (status, body) = rig.Call("release", pid, new JsonObject { ["id"] = id });
            Assert.Equal(403, status);
            Assert.Equal("not-allowed", Rig.ErrorCode(body));
        }

        Assert.Equal("claimed", rig.Call("status", 5, new JsonObject { ["id"] = id }).Body["entry"].AsObject()["state"].AsString());
        var (anonymous, answer) = rig.Call("release", null, new JsonObject { ["id"] = id });
        Assert.Equal(403, anonymous);
        Assert.Equal("qa-caller-unproven", Rig.ErrorCode(answer));
    }

    [Fact]
    public void AQueuedEntryIsWithdrawnAndNeverHandedOutButAClaimedOneStaysClaimed()
    {
        var rig = NewRig();
        var queued = rig.Submit(1, set: "other")["id"].AsString();

        var (status, body) = rig.Call("withdraw", 2, new JsonObject { ["id"] = queued, ["reason"] = "already passed" });
        Assert.Equal(200, status);
        Assert.Equal("withdrawn", body["entry"].AsObject()["state"].AsString());
        Assert.Equal("already passed", rig.Call("status", 5, new JsonObject { ["id"] = queued }).Body["entry"].AsObject()["reason"].AsString());
        Assert.True(rig.Call("next", 4, new JsonObject()).Body["entry"].IsNull);

        var claimed = rig.Claimed();
        var (refusedStatus, refused) = rig.Call("withdraw", 1, new JsonObject { ["id"] = claimed });
        Assert.Equal(409, refusedStatus);
        Assert.Equal("qa-claimed", Rig.ErrorCode(refused));
        Assert.Contains("qa1@t", refused["error"].AsObject()["message"].AsString(), StringComparison.Ordinal);
        Assert.Equal("claimed", rig.Call("status", 5, new JsonObject { ["id"] = claimed }).Body["entry"].AsObject()["state"].AsString());
    }

    [Fact]
    public void StatusAndListAnswerTheUserAndASessionButNotAnUnprovenCaller()
    {
        var rig = NewRig();
        var id = rig.Submit(1)["id"].AsString();

        foreach (var pid in new int?[] { 5, 1 })
        {
            Assert.Equal(id, rig.Call("status", pid, new JsonObject { ["id"] = id }).Body["entry"].AsObject()["id"].AsString());
            Assert.Single(rig.Call("status", pid).Body["entries"].AsArray());
            Assert.Single(rig.Call("list", pid).Body["entries"].AsArray());
        }

        foreach (var route in new[] { "status", "list" })
        {
            Assert.Equal(403, rig.Call(route, 99).Status);
            Assert.Equal(403, rig.Call(route, null).Status);
        }

        Assert.Equal(404, rig.Call("status", 5, new JsonObject { ["id"] = "nope" }).Status);
    }

    [Fact]
    public void AStatusCallReturnsTheClaimOfADeadClaimerToTheQueue()
    {
        var rig = NewRig();
        var id = rig.Claimed();
        rig.Live.Remove("qa1@t");

        var entry = rig.Call("status", 5, new JsonObject { ["id"] = id }).Body["entry"].AsObject();

        Assert.Equal("queued", entry["state"].AsString());
    }

    [Fact]
    public void ARepositoryThatIsTheTeamsRootOrOneOfItsWorktreesIsAccepted()
    {
        var rig = NewRig();

        Assert.Equal("queued", rig.Submit(1, rig.Main)["state"].AsString());
        Assert.Equal("queued", rig.Submit(1, rig.Tree, set: "shell")["state"].AsString());
    }

    [Fact]
    public void ARepositoryOutsideTheTeamsRootAndItsWorktreesIsRefused()
    {
        var rig = NewRig();
        var sibling = Path.GetDirectoryName(rig.Main)! + "/main-extra";
        File.CreateSymbolicLink(rig.Tree + "/escape", rig.Outside);

        foreach (var repo in new[] { rig.Outside, sibling, rig.Main + "/link", rig.Tree + "/escape", rig.Main + "/missing", "relative/path", "/", rig.Main + "/.." })
        {
            var (status, body) = rig.Call("submit", 1, new JsonObject { ["repo"] = repo, ["sha"] = Sha, ["set"] = "core" });
            Assert.Equal(400, status);
            Assert.Equal("qa-bad-request", Rig.ErrorCode(body));
        }

        Assert.Empty(rig.Call("list", 5, new JsonObject { ["all"] = true }).Body["entries"].AsArray());
    }

    [Fact]
    public void ACallerWhoseRegistrationNamesNoTeamFileMayQueueNoRepository()
    {
        var rig = NewRig();

        var (status, body) = rig.Call("submit", 8, new JsonObject { ["repo"] = rig.Main, ["sha"] = Sha, ["set"] = "core" });

        Assert.Equal(400, status);
        Assert.Equal("qa-bad-request", Rig.ErrorCode(body));
    }

    [Theory]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("A")]
    public void ASetNameWithASlashOrDotsIsRefused(string set)
    {
        var rig = NewRig();

        var (status, body) = rig.Call("submit", 1, new JsonObject { ["repo"] = rig.Main, ["sha"] = Sha, ["set"] = set });

        Assert.Equal(400, status);
        Assert.Equal("qa-bad-request", Rig.ErrorCode(body));
    }

    [Fact]
    public void SubmittingStartsNoProcess()
    {
        var rig = NewRig();

        rig.Submit(1);
        rig.Call("submit", 1, new JsonObject { ["repo"] = rig.Main, ["sha"] = Sha, ["set"] = "unknown-set" });

        Assert.False(File.Exists(rig.Marker));
    }

    [Fact]
    public void AMaxSetOfOtherSkipsAFullSetAndAnythingElseIsRefused()
    {
        var rig = NewRig();
        rig.Submit(1, set: "full");

        Assert.Equal(JsonValue.Null, rig.Call("next", 3, new JsonObject { ["max_set"] = "other" }).Body["entry"]);
        Assert.Equal("full", rig.Call("next", 3, new JsonObject { ["max_set"] = "full" }).Body["entry"].AsObject()["set"].AsString());
        Assert.Equal(400, rig.Call("next", 3, new JsonObject { ["max_set"] = "all" }).Status);
    }

    [Fact]
    public void ABodyThatIsNotAJsonObjectIsRefused()
    {
        var rig = NewRig();

        Assert.Equal(400, rig.Call("submit", 1, "[1]").Status);
        Assert.Equal(400, rig.Call("submit", 1, "not json").Status);
        Assert.Equal(400, rig.Call("submit", 1, new JsonObject { ["repo"] = rig.Main }).Status);
    }
}
