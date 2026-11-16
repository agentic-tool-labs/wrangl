using System.Text;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>A lane leader's session start, on the real hook: its member row is stamped with its process, and the lane's child team is taken over when its owner is gone.</summary>
public sealed class LaneLeaderStartTests
{
    private const string So = "mo-so-ws1";

    private const string OldProcessStart = "Fri Oct 16 08:00:00 2026";

    private sealed class Scene
    {
        public required HooksHostTests.Rig Rig { get; init; }

        public required HarnessSessions Sessions { get; init; }

        public required string TeamsDir { get; init; }

        public string ManagerFile => TeamsDir + "/mo.json";

        public string ChildFile => TeamsDir + "/mo-ws1.json";
    }

    private sealed class DirectiveStub : IDirectiveText
    {
        public string? DesignRoutingBlock(JsonObject resolved) => null;

        public string? ContractBlock(string role, JsonObject? resolved, bool peerRoute = false) => null;

        public string BuildDirective(JsonObject fullResolved, string? sessionId, string? hierarchyDirectory = null, string? model = null, bool subOrchestrator = false) => "directive";

        public string BuildDirective(JsonObject fullResolved, string? sessionId, string? hierarchyDirectory, string? model, bool subOrchestrator, bool deputy) => deputy ? "deputy-directive" : "directive";

        public string BuildNudge(JsonObject resolved) => string.Empty;

        public string BuildRoleSessionNotice(string role, string? agentType, JsonObject? resolved = null) => string.Empty;

        public string StatusReport(string? cwd) => string.Empty;

        public string WindDown(string link, string why, string orchestratorAddress, string form) => StopDirective.Instance.WindDown(link, why, orchestratorAddress, form);
    }

    /// <summary>
    /// A sandbox as a launched lane leader sees it: the member name and team file exported (the marker), a manager team that names
    /// the leader as the sub_orchestrator of ws1, and optionally the lane's child team.
    /// </summary>
    private static Scene Build(string? child = null, bool processTable = true, string role = "sub-orchestrator", string soStreamOwner = So, string? spawnedAtOverride = null, string memberPid = "", Dictionary<string, string>? extraEnv = null, string extraStreams = "", string extraMembers = "")
    {
        var home = UnitSandbox.Create();
        var teams = home.Home + "/.claude/ah2/hierarchy/proj/teams";
        var sessions = new HarnessSessions();
        var env = new Dictionary<string, string>
        {
            ["HOME"] = home.Home,
            ["AH_HOME"] = home.Root + "/ah",
            ["AH_MEMBER_NAME"] = So,
            ["WRANGL_TEAM_FILE"] = teams + "/mo.json",
        };
        foreach (var pair in extraEnv ?? [])
        {
            env[pair.Key] = pair.Value;
        }

        var rig = new HooksHostTests.Rig(env, memberUp: (_, _) => true, processTable: processTable ? sessions.Table : null);
        _ = Directory.CreateDirectory(teams);
        var spawnedAt = spawnedAtOverride ?? Timestamps.ToIsoString(rig.Services.Clock.UtcNow.AddSeconds(-10));
        File.WriteAllText(
            teams + "/mo.json",
            "{\"team_id\":\"t-mo\",\"orchestrator\":{\"pid\":1,\"session_id\":\"o\"},\"transport\":\"terminal\",\"members\":["
            + $"{{\"role\":\"{role}\",\"name\":\"{So}\",\"kind\":\"claude\",\"model\":\"opus\",\"spawned_at\":\"{spawnedAt}\",\"stream\":\"ws1\"{memberPid}}}"
            + extraMembers
            + "],\"streams\":{\"ws1\":{\"state\":\"open\",\"branch\":\"lane/ws1\",\"base\":\"origin/train\",\"sub_orchestrator\":\"" + soStreamOwner + "\"}" + extraStreams + "}}");
        if (child is not null)
        {
            File.WriteAllText(teams + "/mo-ws1.json", child);
        }

        var dir = rig.Sandbox.Root + "/proj/.claude/ah2";
        _ = Directory.CreateDirectory(dir);
        File.WriteAllText(dir + "/agent-hierarchy.json", """{"version":1}""");
        return new Scene { Rig = rig, Sessions = sessions, TeamsDir = teams };
    }

    /// <summary>The lane's child team as the dismissed lead left it: owned by the given process, with a member of its own and the stream copy.</summary>
    private static string Child(string session, int pid, string? pidStart) =>
        "{\"version\":1,\"team_id\":\"t-child\",\"created\":\"2026-10-11T00:00:00.000Z\","
        + $"\"orchestrator\":{{\"session_id\":\"{session}\",\"pid\":{pid}{(pidStart is null ? string.Empty : $",\"pid_start\":\"{pidStart}\"")}}},"
        + "\"members\":[{\"role\":\"implementor\",\"name\":\"mo-ws1-impl\"}],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},"
        + "\"streams\":{\"ws1\":{\"state\":\"open\",\"branch\":\"lane/ws1\",\"base\":\"origin/train\",\"worktree\":null,\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}},"
        + "\"workstream\":{\"state\":\"working\",\"summary\":\"half\",\"updated\":\"2026-10-11T00:00:00.000Z\"}}";

    private static string Start(Scene scene, string agentType = "wrangl:sub-orchestrator", string boxName = So, bool subagent = false, Action<CallerRequest>? onRegister = null, Func<double, string?, bool>? gone = null, IGateSessions? sessions = null)
    {
        var rig = scene.Rig;
        var box = new CallerBox($"{boxName}@mo", "/box", "mo", boxName, scene.ManagerFile);
        var advisory = new AdvisoryServices(
            BindingAttestationTests.MailboxFor(rig),
            new StopPeerNudgeReportTests.FakeHooks(ReportState.Sent, mailed: true),
            new BindingAttestationTests.Callers([box], onRegister, processGone: gone),
            null!,
            null!,
            new DirectiveStub(),
            null!,
            null!,
            new BindingAttestationTests.Daemon(),
            null!,
            null!);
        var services = rig.Services with { Advisory = advisory, Sessions = sessions ?? scene.Sessions };
        var e = new JsonObject
        {
            ["hook_event_name"] = "SessionStart",
            ["session_id"] = "sess-1",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["source"] = "startup",
            ["agent_type"] = agentType,
        };
        if (subagent)
        {
            e["agent_id"] = "a-1";
        }

        var outcome = rig.Run("sessionstart", Encoding.UTF8.GetString(JsonText.ToUtf8(e, 0)), services: () => services);
        return Encoding.UTF8.GetString(outcome.Stdout.Span);
    }

    private static List<JsonObject> Writes(Scene scene) =>
        scene.Rig.Writer.Mutations
            .Select(m => JsonText.Parse(m))
            .Where(m => m.IsMap && m.AsObject().Get("write") is { IsMap: true })
            .Select(m => m.AsObject().Get("write")!.Value.AsObject())
            .ToList();

    private static JsonObject? WrittenLeaderRow(Scene scene) =>
        Writes(scene)
            .Where(w => w.Get("members") is { IsArray: true })
            .SelectMany(w => w.Get("members")!.Value.AsArray())
            .Select(r => r.AsObject())
            .FirstOrDefault(r => r.Get("name") is { IsText: true } n && n.AsString() == So && r.Get("pid") is not null);

    private static JsonObject? WrittenChild(Scene scene) => Writes(scene).FirstOrDefault(w => w.Get("parent") is { IsMap: true });

    // ---------------------------------------------------------------- 1d: the row stamp

    [Theory]
    [InlineData("deputy-chief", "wrangl:deputy-chief", "deputy-directive")]
    [InlineData("sub-orchestrator", "wrangl:sub-orchestrator", "directive")]
    public void ADeputyStartedThroughTheLaneChildPathGetsTheDeputyDirectiveAndALaneLeaderDoesNot(string role, string agentType, string expected)
    {
        var scene = Build(role: role);

        var output = Start(scene, agentType: agentType);

        Assert.Contains(expected, output, StringComparison.Ordinal);
        Assert.Equal(role == "deputy-chief", output.Contains("deputy-directive", StringComparison.Ordinal));
    }

    [Fact]
    public void ALaneLeaderStartRegistersFirstThenStampsItsProcessOnItsRowAndRegistersAgain()
    {
        var scene = Build();
        var stampedWhenRegistered = new List<bool>();

        _ = Start(scene, onRegister: _ => stampedWhenRegistered.Add(WrittenLeaderRow(scene) is not null));

        var row = WrittenLeaderRow(scene);
        Assert.NotNull(row);
        Assert.Equal(4242, row.Get("pid")!.Value.AsNumber());
        Assert.Equal(HarnessSessions.Start, row.Get("pid_start")!.Value.AsString());
        Assert.Equal("sess-1", row.Get("session_id")!.Value.AsString());
        // b340: the first registration precedes everything that could run the hook past its time bound, the stamp included.
        Assert.True(stampedWhenRegistered.Count >= 2);
        Assert.False(stampedWhenRegistered[0]);
        Assert.True(stampedWhenRegistered[^1]);
    }

    [Fact]
    public void ALaneLeaderStartMatchedByThePaneStampsItsProcess()
    {
        var scene = Build(spawnedAtOverride: "2026-10-11T08:00:00.000Z", extraEnv: new() { ["HERDR_PANE_ID"] = "p1", ["AH_MEMBER_NAME"] = string.Empty }, memberPid: ",\"transport_id\":\"p1\"");

        _ = Start(scene);

        var row = WrittenLeaderRow(scene);
        Assert.NotNull(row);
        Assert.Equal(4242, row.Get("pid")!.Value.AsNumber());
    }

    [Fact]
    public void NegativeALaneLeaderStartOnARowAnotherLiveProcessHoldsStampsNothing()
    {
        var scene = Build(memberPid: ",\"pid\":9999,\"pid_start\":\"Other\"");

        _ = Start(scene, gone: (_, _) => false);

        Assert.Null(WrittenLeaderRow(scene)?.Get("pid") is { } pid && pid.AsNumber() == 4242 ? scene : null);
        Assert.DoesNotContain(Writes(scene).Where(w => w.Get("members") is { IsArray: true }).SelectMany(w => w.Get("members")!.Value.AsArray()), r => r.Member("session_id") is { IsText: true } s && s.AsString() == "sess-1");
    }

    [Fact]
    public void NegativeALaneLeaderStartOutsideTheStartingWindowOnARowWithNoProcessStampsNothing()
    {
        var scene = Build(spawnedAtOverride: "2026-10-11T08:00:00.000Z");

        _ = Start(scene);

        Assert.Null(WrittenLeaderRow(scene));
    }

    // ---------------------------------------------------------------- 2e: adopting a stale lane child

    [Fact]
    public void ALaneLeaderStartTakesOverAChildWhoseOwnerProcessIsGoneAndLeavesEverythingElseAlone()
    {
        var scene = Build(Child("old-sess", 9999, OldProcessStart));

        var output = Start(scene);

        var child = WrittenChild(scene);
        Assert.NotNull(child);
        var owner = child.Get("orchestrator")!.Value.AsObject();
        Assert.Equal("sess-1", owner.Get("session_id")!.Value.AsString());
        Assert.Equal(4242, owner.Get("pid")!.Value.AsNumber());
        Assert.Equal(HarnessSessions.Start, owner.Get("pid_start")!.Value.AsString());
        var from = owner.Get("adopted_from")!.Value.AsObject();
        Assert.Equal("old-sess", from.Get("session_id")!.Value.AsString());
        Assert.Equal(9999, from.Get("pid")!.Value.AsNumber());
        var original = JsonText.Parse(Child("old-sess", 9999, OldProcessStart)).AsObject();
        Assert.Equal(JsonText.Stringify(original.Get("members")!.Value, 0), JsonText.Stringify(child.Get("members")!.Value, 0));
        Assert.Equal(JsonText.Stringify(original.Get("streams")!.Value, 0), JsonText.Stringify(child.Get("streams")!.Value, 0));
        Assert.Equal(JsonText.Stringify(original.Get("parent")!.Value, 0), JsonText.Stringify(child.Get("parent")!.Value, 0));
        Assert.Equal(JsonText.Stringify(original.Get("workstream")!.Value, 0), JsonText.Stringify(child.Get("workstream")!.Value, 0));
        Assert.Contains("from session old-sess (its process is gone)", output, StringComparison.Ordinal);
        Assert.Contains("You took over your lane", output, StringComparison.Ordinal);
        Assert.Contains(scene.Rig.Errors.Records, r => r.Message.StartsWith("info: ", StringComparison.Ordinal) && r.Message.Contains("took over lane child team mo-ws1", StringComparison.Ordinal));
    }

    [Fact]
    public void ALaneLeaderStartTakesOverAChildWhoseRecordedPidNowBelongsToAnotherProcess()
    {
        var scene = Build(Child("old-sess", 7777, OldProcessStart));
        scene.Sessions.Starts[7777] = "Sat Oct 17 09:30:00 2026";

        _ = Start(scene);

        Assert.NotNull(WrittenChild(scene)?.Get("orchestrator")!.Value.Member("adopted_from"));
    }

    [Fact]
    public void NegativeALaneLeaderStartDoesNotTakeOverAChildAnotherLiveSessionHolds()
    {
        var scene = Build(Child("live-sess", 8888, "Sat Oct 17 09:00:00 2026"));
        scene.Sessions.Starts[8888] = "Sat Oct 17 09:00:00 2026";

        var output = Start(scene);

        Assert.Null(WrittenChild(scene));
        Assert.Contains("lane-child-owned: mo-ws1 is held by live session live-sess (pid 8888)", output, StringComparison.Ordinal);
        Assert.DoesNotContain("You took over", output, StringComparison.Ordinal);
        Assert.Contains("Do not create it again", output, StringComparison.Ordinal);
        Assert.DoesNotContain("create it with `ah roster create", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"version\":1,\"team_id\":\"t-child\",\"orchestrator\":{\"session_id\":\"old-sess\",\"pid\":9999},\"parent\":{\"team\":\"mo\",\"stream\":\"ws2\"},\"members\":[],\"streams\":{}}")]
    [InlineData("{\"version\":1,\"team_id\":\"t-child\",\"orchestrator\":{\"session_id\":\"old-sess\",\"pid\":9999},\"parent\":{\"team\":\"other\",\"stream\":\"ws1\"},\"members\":[],\"streams\":{}}")]
    [InlineData("{\"version\":1,\"team_id\":\"t-child\",\"orchestrator\":{\"session_id\":\"old-sess\",\"pid\":9999},\"members\":[],\"streams\":{}}")]
    public void NegativeATeamThatIsNotTheChildOfTheStreamIsNotAdoptedEvenWhenItsOwnerIsGone(string child)
    {
        var scene = Build(child);
        var owner = new Ah.Hierarchy.LaneChildOwner("sess-1", 4242, HarnessSessions.Start);

        var result = Ah.Hierarchy.HierarchyModule.AdoptLaneChild(scene.Rig.Services.Hierarchy.StateCalls, scene.Rig.Services.Roster, Path.GetDirectoryName(scene.TeamsDir)!, "mo", "ws1", "mo-ws1", owner);

        Assert.StartsWith("not-child-team", result.Refusal, StringComparison.Ordinal);
        Assert.Null(WrittenChild(scene));
    }

    [Fact]
    public void NegativeAChildNamedForAnotherStreamIsNotAdoptedEvenWhenItsLinkMatches()
    {
        var scene = Build(Child("old-sess", 9999, OldProcessStart));
        File.WriteAllText(scene.TeamsDir + "/mo-ws9.json", File.ReadAllText(scene.ChildFile));
        var owner = new Ah.Hierarchy.LaneChildOwner("sess-1", 4242, HarnessSessions.Start);

        var result = Ah.Hierarchy.HierarchyModule.AdoptLaneChild(scene.Rig.Services.Hierarchy.StateCalls, scene.Rig.Services.Roster, Path.GetDirectoryName(scene.TeamsDir)!, "mo", "ws1", "mo-ws9", owner);

        Assert.StartsWith("not-child-team", result.Refusal, StringComparison.Ordinal);
        Assert.Null(WrittenChild(scene));
    }

    [Fact]
    public void NegativeAStartWhoseOwnerCannotBeCheckedIsRefusedAsUnknownAndRewritesNothing()
    {
        var scene = Build(Child("old-sess", 9999, OldProcessStart), processTable: false);

        var output = Start(scene);

        Assert.Null(WrittenChild(scene));
        Assert.Contains("lane-child-owner-unknown", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAMemberThatDoesNotRunTheStreamTakesNothingOver()
    {
        var scene = Build(Child("old-sess", 9999, OldProcessStart), soStreamOwner: "someone-else");

        _ = Start(scene);

        Assert.Null(WrittenChild(scene));
    }

    [Fact]
    public void NegativeTheLeaderOfAnotherStreamDoesNotTakeOverThisStreamsChild()
    {
        var scene = Build(
            Child("old-sess", 9999, OldProcessStart),
            extraStreams: ",\"ws2\":{\"state\":\"open\",\"branch\":\"lane/ws2\",\"base\":\"origin/train\",\"sub_orchestrator\":\"mo-so-ws2\"}",
            extraMembers: ",{\"role\":\"sub-orchestrator\",\"name\":\"mo-so-ws2\",\"kind\":\"claude\",\"model\":\"opus\"}");

        _ = Start(scene, boxName: "mo-so-ws2");

        Assert.DoesNotContain(Writes(scene), w => w.Get("orchestrator") is { IsMap: true } o && o.Member("adopted_from") is not null);
        Assert.DoesNotContain(Writes(scene), w => w.Get("orchestrator") is { IsMap: true } o && o.Member("session_id") is { IsText: true } s && s.AsString() == "sess-1" && w.Get("parent") is { IsMap: true } p && p.Member("stream") is { IsText: true } ps && ps.AsString() == "ws1");
    }

    [Fact]
    public void NegativeALaneLeadersSubagentTakesNothingOver()
    {
        var scene = Build(Child("old-sess", 9999, OldProcessStart));

        _ = Start(scene, subagent: true);

        Assert.Null(WrittenChild(scene));
    }

    [Fact]
    public void NegativeAnOrchestrateRoleIsNotEnoughWhenTheNamedMemberIsNotOfThatRole()
    {
        var scene = Build(Child("old-sess", 9999, OldProcessStart), role: "implementor");

        _ = Start(scene);

        Assert.Null(WrittenChild(scene));
    }

    [Fact]
    public void NegativeAStartOnAStreamThatIsNoLongerOpenTakesNothingOver()
    {
        var scene = Build(Child("old-sess", 9999, OldProcessStart));
        File.WriteAllText(scene.ManagerFile, File.ReadAllText(scene.ManagerFile).Replace("\"state\":\"open\"", "\"state\":\"done\"", StringComparison.Ordinal));

        _ = Start(scene);

        Assert.Null(WrittenChild(scene));
    }

    [Fact]
    public void AStartByTheSessionThatAlreadyOwnsTheChildWritesNothingAndRecordsNoAdoption()
    {
        var scene = Build(Child("sess-1", 4242, HarnessSessions.Start));

        _ = Start(scene);

        Assert.Null(WrittenChild(scene));
    }

    [Fact]
    public void AClearedSessionInTheOwningProcessTakesTheRecordedSessionOverWithoutAnAdoptionRecord()
    {
        var scene = Build(Child("previous-sess", 4242, HarnessSessions.Start));

        _ = Start(scene);

        var owner = WrittenChild(scene)?.Get("orchestrator")!.Value.AsObject();
        Assert.NotNull(owner);
        Assert.Equal("sess-1", owner.Get("session_id")!.Value.AsString());
        Assert.Null(owner.Get("adopted_from"));
    }

    // ---------------------------------------------------------------- chain-down at session start

    private static List<string> ChainRows(Scene scene) => SessionStartChainDownTests.States(scene.Rig, "sess-1");

    /// <summary>The lane's child team as the chain walk accepts it: its parent link names the manager's incarnation.</summary>
    private static string LinkedChild(string session, int pid, string? pidStart) =>
        Child(session, pid, pidStart).Replace("\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"}", "\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\",\"team_id\":\"t-mo\"}", StringComparison.Ordinal);

    [Fact]
    public void ARestoredLaneLeadWithNoRegistryEntryAndABareCommandLineIsToldToWindDownAndGetsAnActiveRowFromTheRecordAsReadBeforeTheLaneChildIsTakenOver()
    {
        var scene = Build(LinkedChild("sess-1", 9999, OldProcessStart));

        var output = Start(scene);

        Assert.Contains("your chain of command is down (mo-ws1: restored-bare)", output, StringComparison.Ordinal);
        Assert.Contains("ah msg send orchestrator@mo --kind note", output, StringComparison.Ordinal);
        Assert.Equal(["active"], ChainRows(scene));
        Assert.True(output.IndexOf("directive", StringComparison.Ordinal) < output.IndexOf("your chain of command is down", StringComparison.Ordinal), output);
        Assert.NotNull(WrittenChild(scene));
    }

    [Fact]
    public void ALaneLeadWhoseMemberRowCannotBeReadWhenItIsJudgedIsStillRegistered()
    {
        var scene = Build(LinkedChild("sess-1", 9999, OldProcessStart));
        var registered = 0;

        _ = Start(scene, onRegister: _ => registered++, sessions: new ThrowsOnceInMemberRowLookup(scene.Sessions));

        Assert.True(registered > 0);
    }

    /// <summary>Sessions whose process table fails the first time the member-row lookup reads it, as an unreadable team file or process table would.</summary>
    private sealed class ThrowsOnceInMemberRowLookup(HarnessSessions inner) : IGateSessions
    {
        private bool _thrown;

        public Ah.Abstractions.IProcessTable Table
        {
            get
            {
                if (!_thrown && new System.Diagnostics.StackTrace().ToString().Contains("ResolveMemberRow", StringComparison.Ordinal))
                {
                    _thrown = true;
                    throw new IOException("the lookup failed");
                }

                return inner.Table;
            }
        }

        public IReadOnlyList<TeamOrchestrator> Teams(string hierarchyDir) => inner.Teams(hierarchyDir);

        public IReadOnlyList<RegisteredSession> Registered() => inner.Registered();

        IReadOnlyList<SessionRegistration> IGateSessions.Sessions() => ((IGateSessions)inner).Sessions();
    }

    [Fact]
    public void NegativeALaneLeadStillRunningInTheProcessItsLaneRecordNamesIsNotToldToWindDown()
    {
        var scene = Build(LinkedChild("sess-1", 4242, HarnessSessions.Start));

        var output = Start(scene);

        Assert.DoesNotContain("chain of command is down", output, StringComparison.Ordinal);
        Assert.Empty(ChainRows(scene));
    }

    [Fact]
    public void NegativeALaneLeadWhoseCommandLineCannotBeReadIsNotToldToWindDown()
    {
        var scene = Build(LinkedChild("sess-1", 9999, OldProcessStart));
        _ = scene.Sessions.Commands.Remove(HarnessSessions.Pid);

        var output = Start(scene);

        Assert.DoesNotContain("chain of command is down", output, StringComparison.Ordinal);
        Assert.Empty(ChainRows(scene));
    }
}
