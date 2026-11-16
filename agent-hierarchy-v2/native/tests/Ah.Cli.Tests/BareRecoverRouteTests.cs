using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Launch;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;
using Ah.Watch;

namespace Ah.Cli.Tests;

/// <summary>The daemon's <c>recover</c> route: who it serves, where it keeps the state, and what a bare member's relaunch must prove.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class BareRecoverRouteTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly StallTeam _one;
    private readonly StallTeam _two;
    private readonly BareHost _host;
    private CallerPrincipal _caller = CallerPrincipal.Anonymous;

    public BareRecoverRouteTests()
    {
        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.0.0", "1"));
        _graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        _one = MakeTeam("p1");
        _two = MakeTeam("p2");
        _host = new BareHost(_graph, new StallHost(_graph, _sandbox.AhHome, () => true, () => null), () => [_one, _two], pid => pid == 777 ? Command : null);
    }

    public void Dispose() => _sandbox.Dispose();

    private StallTeam MakeTeam(string project)
    {
        var dir = Path.Combine(_sandbox.Home, project, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(dir, "teams"));
        File.WriteAllText(Path.Combine(dir, "teams", "alpha.json"), "{\"members\":[{\"name\":\"a\",\"pid\":777,\"pid_start\":\"S1\"}]}");
        return new StallTeam("alpha", dir, Path.Combine(_sandbox.Home, project), "orchestrator@alpha", "orch-" + project);
    }

    private static CallerPrincipal Session(string address, string sessionId = "orch-p1") => new(PrincipalKind.Session, [new SessionRegistration(sessionId, 100, "start", [address])]);

    private (int Status, JsonObject Body) Ask(StallTeam team, string teamName, string member, string op, string? dir = null)
    {
        var body = new JsonObject { ["hierarchy_dir"] = dir ?? team.HierarchyDir, ["team"] = teamName, ["member"] = member, ["op"] = op };
        var request = new RouteRequest("POST", "/v1/bare/recover", string.Empty, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(JsonText.Stringify(body, 0)), new Dictionary<string, string>(), 4242);
        var response = new BareRecoverRoute(_graph, () => _host, _ => _caller).HandleAsync(request, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return (response.Status, JsonText.Parse(response.Body.ToArray()).AsObject());
    }

    private bool HasControl(StallTeam team) => _host.ReadControl(team, "a") is not null;

    private static bool Holds(JsonObject body) => body["control"].Member("hold")!.Value.IsTruthy;

    [Fact]
    public void TheOwnerHoldsOneProjectsMemberAndAnotherProjectsMemberOfTheSameTeamAndNameIsUntouched()
    {
        _caller = CallerPrincipal.User;

        var held = Ask(_one, "alpha", "a", "hold");
        var other = Ask(_two, "alpha", "a", "show");

        Assert.Equal(200, held.Status);
        Assert.True(Holds(held.Body));
        Assert.Equal(200, other.Status);
        Assert.False(Holds(other.Body));
        Assert.True(HasControl(_one));
        Assert.False(HasControl(_two));
    }

    [Fact]
    public void ProjectAsOrchestratorMayNotReachProjectBsTeamOfTheSameName()
    {
        _caller = Session("orchestrator@alpha", "orch-p1");

        Assert.Equal(200, Ask(_one, "alpha", "a", "hold").Status);
        var (status, body) = Ask(_two, "alpha", "a", "hold");

        Assert.Equal(403, status);
        Assert.Contains("orchestrating session", body["error"].Member("message")!.Value.AsString(), StringComparison.Ordinal);
        Assert.False(HasControl(_two));
        Assert.Equal(403, Ask(_two, "alpha", "a", "show").Status);
    }

    [Fact]
    public void ARecordOneProjectWroteIsNotShownForTheOtherProject()
    {
        _caller = CallerPrincipal.User;
        _host.WriteRecord(_one, "a", new JsonObject { ["state"] = "stopping" });

        Assert.Equal("stopping", Ask(_one, "alpha", "a", "show").Body["record"].Member("state")!.Value.AsString());
        Assert.True(Ask(_two, "alpha", "a", "show").Body["record"].IsNull);
        Assert.True(_host.ReadRecord(_two, "a") is null);
    }

    [Theory]
    [InlineData("hold")]
    [InlineData("release")]
    [InlineData("now")]
    [InlineData("show")]
    [InlineData("enable")]
    [InlineData("disable")]
    public void ASessionThatIsNotTheTeamsOrchestratorIsRefusedAndNothingIsWritten(string op)
    {
        foreach (var caller in new[] { CallerPrincipal.Anonymous, Session("a@alpha"), Session("orchestrator@other") })
        {
            _caller = caller;

            var (status, body) = Ask(_one, "alpha", "a", op);

            Assert.Equal(403, status);
            Assert.True(body.ContainsKey("error"));
            Assert.False(HasControl(_one));
            Assert.Null(SwitchRow);
        }
    }

    [Fact]
    public void RecoveryIsOnUntilTheOwnerDisablesItAndNowIsRefusedWhileItIsOff()
    {
        _caller = CallerPrincipal.User;

        Assert.True(Ask(_one, "alpha", "*", "status").Body["recovery_enabled"].IsTruthy);
        Assert.Equal(200, Ask(_one, "alpha", "a", "now").Status);

        Assert.False(Ask(_one, "alpha", "*", "disable").Body["recovery_enabled"].IsTruthy);
        Assert.False(_host.RecoveryEnabled);
        var before = JsonText.Stringify(_host.ReadControl(_one, "a")!.Value, 0);
        var refused = Ask(_one, "alpha", "a", "now");
        Assert.Equal(404, refused.Status);
        Assert.Equal(before, JsonText.Stringify(_host.ReadControl(_one, "a")!.Value, 0));

        Assert.True(Ask(_one, "alpha", "*", "enable").Body["recovery_enabled"].IsTruthy);
        Assert.True(_host.RecoveryEnabled);
    }

    [Fact]
    public void TheTeamsOrchestratorMayDisableRecoveryAndTheSwitchNamesItAndItsSession()
    {
        _caller = Session("orchestrator@alpha", "orch-p1");

        var (status, body) = Ask(_one, "alpha", "*", "disable");

        Assert.Equal(200, status);
        Assert.False(body["recovery_enabled"].IsTruthy);
        Assert.Equal("orchestrator", body["by"].AsString());
        var doc = JsonText.Parse(SwitchRow!);
        Assert.False(doc.Member("on")!.Value.IsTruthy);
        Assert.Equal("orchestrator", doc.Member("by")!.Value.AsString());
        Assert.Equal("alpha", doc.Member("team")!.Value.AsString());
        Assert.Equal("orch-p1", doc.Member("session_id")!.Value.AsString());
        Assert.False(_host.RecoveryEnabled);
    }

    [Fact]
    public void TheTeamsOrchestratorCanNeverEnableRecoveryAndTheSwitchFileIsLeftAsItWas()
    {
        PutSwitch("{\"on\":false,\"by\":\"daemon\"}");
        var before = SwitchRow;
        _caller = Session("orchestrator@alpha", "orch-p1");

        var (status, body) = Ask(_one, "alpha", "*", "enable");

        Assert.Equal(403, status);
        Assert.Equal("recover enable is for the user only", body["error"].Member("message")!.Value.AsString());
        Assert.Equal(before, SwitchRow);
        Assert.False(_host.RecoveryEnabled);
    }

    [Fact]
    public void AnotherDirsOrchestratorOfASameNamedTeamCannotDisableAndNothingIsWritten()
    {
        _caller = Session("orchestrator@alpha", "orch-p1");

        var (status, _) = Ask(_two, "alpha", "*", "disable");

        Assert.Equal(403, status);
        Assert.Null(SwitchRow);
    }

    [Theory]
    [InlineData("enable")]
    [InlineData("disable")]
    public void AMemberPrincipalCannotFlipTheSwitch(string op)
    {
        _caller = Session("a@alpha");

        Assert.Equal(403, Ask(_one, "alpha", "*", op).Status);
        Assert.Null(SwitchRow);
    }

    [Fact]
    public void AMemberAndAnUnknownPrincipalMayReadStatusAndNothingIsWritten()
    {
        PutSwitch("{\"on\":false,\"by\":\"daemon\",\"team\":\"alpha\",\"member\":\"a\",\"reason\":\"launch refused\"}");
        var before = SwitchRow;
        foreach (var caller in new[] { Session("a@alpha"), CallerPrincipal.Anonymous })
        {
            _caller = caller;

            var (status, body) = Ask(_one, "alpha", "*", "status");

            Assert.Equal(200, status);
            Assert.False(body["recovery_enabled"].IsTruthy);
            Assert.Equal("daemon", body["by"].AsString());
            Assert.Equal("launch refused", body["reason"].AsString());
            Assert.False(body.ContainsKey("session_id"));
            Assert.Equal(before, SwitchRow);
            Assert.False(HasControl(_one));
        }
    }

    [Fact]
    public void StatusShowsTheOrchestratorsSessionOnlyToTheUserAndTheTeamsOrchestrator()
    {
        PutSwitch("{\"on\":false,\"by\":\"orchestrator\",\"team\":\"alpha\",\"session_id\":\"orch-p1\"}");
        foreach (var caller in new[] { CallerPrincipal.User, Session("orchestrator@alpha", "orch-p1") })
        {
            _caller = caller;

            Assert.Equal("orch-p1", Ask(_one, "alpha", "*", "status").Body["session_id"].AsString());
        }

        _caller = Session("orchestrator@alpha", "orch-p1");
        Assert.False(Ask(_two, "alpha", "*", "status").Body.ContainsKey("session_id"));
    }

    [Fact]
    public void StatusStillNeedsAWatchedTeam()
    {
        _caller = CallerPrincipal.Anonymous;

        Assert.Equal(404, Ask(_one, "beta", "*", "status").Status);
    }

    [Fact]
    public void TheDaemonsOwnBrakeWritesTheSwitchOffNamingTheMemberAndTheReason()
    {
        _host.DisableRecovery(_one, "a", "launch refused: name in use");

        Assert.False(_host.RecoveryEnabled);
        var doc = JsonText.Parse(SwitchRow!);
        Assert.Equal("daemon", doc.Member("by")!.Value.AsString());
        Assert.Equal("alpha", doc.Member("team")!.Value.AsString());
        Assert.Equal("a", doc.Member("member")!.Value.AsString());
        Assert.Equal("launch refused: name in use", doc.Member("reason")!.Value.AsString());
        Assert.Null(doc.Member("session_id"));
    }

    [Theory]
    [InlineData("{\"on\":false,\"by\":\"orchestrator\",\"team\":\"alpha\",\"session_id\":\"s\",\"at\":\"x\",\"req_id\":\"r\"}")]
    [InlineData("{\"on\":false,\"by\":\"daemon\",\"team\":\"alpha\",\"member\":\"a\",\"reason\":\"why\",\"at\":\"x\",\"req_id\":\"r\"}")]
    public void ASwitchOffWrittenByTheOrchestratorOrTheDaemonReadsAsOffWithNoProblem(string text)
    {
        PutSwitch(text);

        Assert.False(_host.RecoveryEnabled);
        Assert.Null(_host.RecoveryProblem);
    }

    /// <summary>The switch's meta row, or null when there is none.</summary>
    private string? SwitchRow => TestHomeDocs.Text(_sandbox.AhHome, WatchModule.RecoverySwitchKey);

    private void PutSwitch(string text) => TestHomeDocs.Put(_sandbox.AhHome, WatchModule.RecoverySwitchKey, text);

    [Theory]
    [InlineData("")]
    [InlineData("on\n")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"on\":false}")]
    [InlineData("{\"on\":false,\"by\":\"user\",\"at\":\"2026-10-21T20:00:00Z\",\"req_id\":\"r\"}")]
    [InlineData("{\"on\":\"true\",\"by\":\"user\"}")]
    public void AMalformedSwitchOrOneSayingOffReadsAsOff(string text)
    {
        PutSwitch(text);

        Assert.False(_host.RecoveryEnabled);
    }

    [Theory]
    [InlineData("{\"on\":true}")]
    [InlineData("{\"on\":true,\"by\":\"orchestrator\",\"at\":\"2026-10-21T20:00:00Z\",\"req_id\":\"r\"}")]
    public void ASwitchSayingOnReadsAsOnWhoeverWroteIt(string text)
    {
        PutSwitch(text);

        Assert.True(_host.RecoveryEnabled);
    }

    [Fact]
    public void NoSwitchRowReadsAsOn()
    {
        Assert.Null(SwitchRow);
        Assert.True(_host.RecoveryEnabled);
    }

    [Fact]
    public void AnOldSwitchFileSayingOffIsIgnored()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "recover"));
        File.WriteAllText(Path.Combine(_sandbox.AhHome, "recover", "enabled"), "{\"on\":false}");

        Assert.True(_host.RecoveryEnabled);
        Assert.Null(_host.RecoveryProblem);
    }

    [Fact]
    public void ASwitchTheDaemonWroteForTheUserReadsAsOnAndOffAgainAfterDisable()
    {
        _caller = CallerPrincipal.User;

        Ask(_one, "alpha", "*", "enable");

        Assert.True(_host.RecoveryEnabled);
        var doc = JsonText.Parse(SwitchRow!);
        Assert.True(doc.Member("on")!.Value.IsTruthy);
        Assert.Equal("user", doc.Member("by")!.Value.AsString());
        Assert.False(string.IsNullOrEmpty(doc.Member("req_id")!.Value.AsString()));
        Assert.False(string.IsNullOrEmpty(doc.Member("at")!.Value.AsString()));

        Ask(_one, "alpha", "*", "disable");
        Assert.False(_host.RecoveryEnabled);
        Assert.False(Directory.Exists(Path.Combine(_sandbox.AhHome, "recover")));
    }

    [Fact]
    public void HoldReleaseAndNowChangeOnlyWhatTheyNameAndNowStampsTheTime()
    {
        _caller = CallerPrincipal.User;
        Ask(_one, "alpha", "*", "enable");

        Assert.True(Holds(Ask(_one, "alpha", "a", "hold").Body));
        var now = Ask(_one, "alpha", "a", "now").Body;
        Assert.True(Holds(now));
        Assert.False(now["control"].Member("now_at")!.Value.IsNull);
        var released = Ask(_one, "alpha", "a", "release").Body;
        Assert.False(Holds(released));
        Assert.False(released["control"].Member("now_at")!.Value.IsNull);
    }

    [Fact]
    public void TheUnnamedDefaultTeamAnUnknownMemberAnUnwatchedDirectoryAndAnUnknownOpAreRefusedAndNothingIsWritten()
    {
        _caller = CallerPrincipal.User;

        Assert.Equal(400, Ask(_one, string.Empty, "a", "hold").Status);
        Assert.Equal(404, Ask(_one, "alpha", "ghost", "hold").Status);
        Assert.Equal(404, Ask(_one, "alpha", "a", "hold", dir: Path.Combine(_sandbox.Home, "elsewhere", ".claude", "ah2")).Status);
        Assert.Equal(404, Ask(_one, "beta", "a", "hold").Status);
        Assert.Equal(400, Ask(_one, "alpha", "a", "kill").Status);
        Assert.False(HasControl(_one));
    }

    [Fact]
    public void AnOldRecoverFileIsIgnoredAndARowIsReadUnderEverySpellingOfTheDirectory()
    {
        var raw = _one.HierarchyDir + "/";
        var team = new StallTeam("alpha", raw, _one.Cwd, _one.OrchestratorAddress, _one.OrchestratorSession);
        var oldDir = Path.Combine(_sandbox.AhHome, "recover", WatchModule.RecoverDirKey(raw), "alpha");
        Directory.CreateDirectory(oldDir);
        File.WriteAllText(Path.Combine(oldDir, "a.ctl.json"), "{\"hold\":true,\"now_at\":null}");
        File.WriteAllText(Path.Combine(oldDir, "a.json"), "{\"state\":\"waiting-idle\"}");

        Assert.Null(_host.ReadControl(team, "a"));
        Assert.Null(_host.ReadRecord(team, "a"));

        _host.WriteRecord(team, "a", new JsonObject { ["state"] = "done" });

        Assert.Equal("done", _host.ReadRecord(_one, "a")!.Value.Member("state")!.Value.AsString());
        Assert.Null(_host.ReadRecord(_two, "a"));
    }

    private const string TeamFile = "/t/teams/alpha.json";

    private static readonly string Settings = "{\"env\":{\"WRANGL_TEAM_FILE\":\"" + TeamFile + "\",\"AH_MEMBER_NAME\":\"a\"}}";

    private static readonly string Command = "claude --resume sess-1 --agent wrangl:implementor --name a --settings " + Settings;

    private static BareRow RowOf() =>
        new("a", "implementor", new BareMember("alpha", "a", "w1:p1", null, new AhLaunchExpectation("wrangl:implementor", "a", TeamFile, null, null)), null);

    [Fact]
    public void ARelaunchIsVerifiedOnlyByANewProcessThatRunsTheSessionWithAhsLaunch()
    {
        var row = RowOf();

        Assert.True(_host.Verified(_one, row, 100, "sess-1"));
        Assert.False(_host.Verified(_one, row, 777, "sess-1"));
        Assert.False(_host.Verified(_one, row, 100, "sess-2"));
        Assert.False(_host.Verified(_one, row, 100, string.Empty));
    }

    [Fact]
    public void ANewProcessWithoutAhsLaunchOrWithoutTheSessionIsNotAVerifiedRelaunch()
    {
        var row = RowOf();
        var bare = new BareHost(_graph, new StallHost(_graph, _sandbox.AhHome, () => true, () => null), () => [_one], _ => "claude --resume sess-1");
        var other = new BareHost(_graph, new StallHost(_graph, _sandbox.AhHome, () => true, () => null), () => [_one], _ => "claude --agent wrangl:implementor --name a --settings " + Settings);
        var gone = new BareHost(_graph, new StallHost(_graph, _sandbox.AhHome, () => true, () => null), () => [_one], _ => null);

        Assert.False(bare.Verified(_one, row, 100, "sess-1"));
        Assert.False(other.Verified(_one, row, 100, "sess-1"));
        Assert.False(gone.Verified(_one, row, 100, "sess-1"));
    }
}
