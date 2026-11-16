using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>An operational line records a call the Orchestrator made itself: uncapped, never parked, and never carrying a reserved class.</summary>
public sealed class DecisionOperationalTests
{
    private const string Run = "20260601-000001-aaaa";

    private sealed class NoStateWriter : IStateWriter
    {
        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null) =>
            throw new NotSupportedException();

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
            throw new NotSupportedException();
    }

    private sealed record Rig(IHierarchyDecisions Decisions, string Log)
    {
        public JsonObject Add(string json) => Decisions.AppendDecision(new FsStoreWriteContext(), Log, Run, JsonText.Parse(json));

        public JsonObject Summary() => Decisions.DecisionSummary(Decisions.ReadDecisions(Log)["decisions"].AsArray(), 0);
    }

    private static Rig Make()
    {
        var sandbox = UnitSandbox.Create();
        var variables = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["TZ"] = "UTC" };
        var cwd = sandbox.Root;
        var env = new FakeEnvironment(variables, new PluginInfo(HierarchyTableFns.AhRoot, "wrangl", "0.0.0", "1"));
        var io = PlatformModule.CreateIo(null);
        IRosterCore roster = null!;
        var config = ConfigModule.CreateCore(io.Files, env, () => roster, new ProcessFacts(cwd, 4242));
        roster = RosterModule.CreateCore(io.Files, env, config, io.Processes);
        var clock = new FakeClock(DateTimeOffset.Parse("2026-06-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        var services = HierarchyModule.Create(new HierarchyEnvironment(io.Files, env, clock, new SequenceRandom(), io.Processes, config, roster, new NoStateWriter(), new ProcessFacts(cwd, 4242), HierarchyModule.CreateBootstrap(io.Writer)) { MsgsRoute = TestMsgStore.Route(env.AhHome) });
        return new Rig(services.Decisions, Path.Combine(cwd, ".claude", "ah2", "pipeline", Run, "decisions.jsonl"));
    }

    private const string Self = "{\"role\":\"orchestrator\",\"self\":true}";

    private static string Op(string o = "O1", string decider = Self, string extra = "") =>
        "{\"class\":\"operational\",\"o\":\"" + o + "\",\"question\":\"kill hung pid 48847?\",\"decision\":\"killed it\",\"rationale\":\"owner dead\",\"revert\":\"none needed\",\"decider\":" + decider + extra + "}";

    private static string Decided(string question, string item = "i1") =>
        "{\"kind\":\"decided\",\"question\":\"" + question + "\",\"choice\":\"a\",\"decider\":{\"role\":\"architect\",\"name\":null,\"model\":null},\"why_user\":null,"
        + "\"item\":\"" + item + "\",\"source\":\"s\",\"options\":[\"x\",\"y\"],\"default\":null,\"rationale\":\"r\",\"revert\":\"\",\"review\":false,\"dangerous\":false,\"exchange\":null}";

    private static string Reason(JsonObject result) => result["refused"].Member("reason")!.Value.AsString();

    /// <summary>The single-choice answered line the writer at 08d010972 (before multi-select) wrote for <see cref="SingleChoiceAnswerLine"/>'s input.</summary>
    private const string PreMultiAnsweredLine =
        """{"id":"d2","time":"2026-06-01T12:00:00.000Z","run":"20260601-000001-aaaa","kind":"answered","answers":"d1","item":"i1","question":"Which?","qkey":"a7532edab557","options":["x","y"],"choice":"b","decision":"y","answered_by":"user","recorded_by":"orchestrator@tm","caller":"top-orchestrator","op_id":"op-answer"}""";

    [Fact]
    public void ASingleChoiceAnsweredLineIsByteIdenticalToThePreMultiWriter()
    {
        var rig = Make();
        var parked = "{\"kind\":\"parked\",\"question\":\"Which?\",\"choice\":null,\"decider\":null,\"why_user\":\"unsure\","
            + "\"item\":\"i1\",\"source\":\"s\",\"options\":[\"x\",\"y\"],\"default\":null,\"rationale\":\"r\",\"revert\":\"\",\"review\":false,\"dangerous\":false,\"exchange\":null}";
        _ = rig.Decisions.AppendDecision(new FsStoreWriteContext(), rig.Log, Run, JsonText.Parse(parked), "op-parked");

        _ = rig.Decisions.AppendDecision(new FsStoreWriteContext(), rig.Log, Run, JsonText.Parse(SingleChoiceAnswerLine), "op-answer");

        Assert.Equal(PreMultiAnsweredLine, File.ReadAllLines(rig.Log)[1]);
    }

    private const string SingleChoiceAnswerLine =
        "{\"kind\":\"answered\",\"answers\":\"d1\",\"choice\":\"b\",\"answered_by\":\"user\",\"recorded_by\":\"orchestrator@tm\",\"caller\":\"top-orchestrator\"}";

    [Fact]
    public void TwentyFiveOperationalLinesInOneRunAreAcceptedAndCountSeparately()
    {
        var rig = Make();

        for (var i = 1; i <= 25; i++)
        {
            var result = rig.Add(Op("O" + ((i % 9) + 1)));
            Assert.Null(result.Get("refused"));
            Assert.Equal("o" + i, result["id"].AsString());
        }

        var summary = rig.Summary();
        Assert.Equal(25, summary["operational"].AsNumber());
        Assert.Equal(0, summary["decided"].AsNumber());
        Assert.EndsWith(", 25 operational", summary["line"].AsString(), StringComparison.Ordinal);
        Assert.Null(rig.Add(Decided("a design question")).Get("refused"));
    }

    [Fact]
    public void AnOperationalLineIsWrittenWithTheOrchestratorsSelfDeciderAndTheKindOperational()
    {
        var rig = Make();

        rig.Add(Op("O7"));

        var line = rig.Decisions.ReadDecisions(rig.Log)["decisions"].AsArray()[0];
        Assert.Equal(("operational", "operational", "O7", "killed it", "owner dead", "none needed"), (
            line.Member("kind")!.Value.AsString(), line.Member("class")!.Value.AsString(), line.Member("o")!.Value.AsString(),
            line.Member("decision")!.Value.AsString(), line.Member("rationale")!.Value.AsString(), line.Member("revert")!.Value.AsString()));
        Assert.True(line.Member("decider")!.Value.Member("self")!.Value.AsBool());
    }

    [Fact]
    public void TheDeputyOnDutyLogsItsOwnOperationalCallButNotAsNotSelf()
    {
        var rig = Make();

        var own = rig.Add(Op(decider: "{\"role\":\"deputy-chief\",\"self\":true}"));
        var notSelf = rig.Add(Op(decider: "{\"role\":\"deputy-chief\",\"self\":false}"));

        Assert.Null(own.Get("refused"));
        Assert.Equal("invalid", Reason(notSelf));
    }

    [Fact]
    public void AStaleDutyIsDroppedOnTheNextWriteAndALiveRowsIsKept()
    {
        JsonValue Team(string deputy) => JsonText.Parse("{\"members\":[{\"name\":\"tm-dep\"}],\"duty\":{\"deputy\":\"" + deputy + "\"}}");
        var stale = Team("tm-gone");
        var kept = Team("tm-dep");

        Ah.Hierarchy.StateCalls.HierarchyStateCalls.DropStaleDuty(stale);
        Ah.Hierarchy.StateCalls.HierarchyStateCalls.DropStaleDuty(kept);

        Assert.Null(stale.AsObject().Get("duty"));
        Assert.NotNull(kept.AsObject().Get("duty"));
    }

    [Fact]
    public void AnOperationalLineThatNamesAReservedClassIsRefused()
    {
        var rig = Make();

        var whyUser = rig.Add(Op(extra: ",\"why_user\":\"unsure\""));
        var dangerous = rig.Add(Op(extra: ",\"dangerous\":true"));
        var reservedO = rig.Add(Op("R2"));
        var deciderD = rig.Add(Op("D4"));

        Assert.Equal("invalid", Reason(whyUser));
        Assert.Equal("invalid", Reason(dangerous));
        Assert.Equal("invalid", Reason(reservedO));
        Assert.Equal("invalid", Reason(deciderD));
        Assert.False(File.Exists(rig.Log));
    }

    [Fact]
    public void AnOperationalLineThatIsNotTheOrchestratorsOwnOrIsIncompleteIsRefused()
    {
        var rig = Make();

        var otherRole = rig.Add(Op(decider: "{\"role\":\"architect\",\"self\":true}"));
        var notSelf = rig.Add(Op(decider: "{\"role\":\"orchestrator\",\"self\":false}"));
        var noDecider = rig.Add(Op().Replace(",\"decider\":" + Self, string.Empty, StringComparison.Ordinal));
        var noRationale = rig.Add(Op().Replace("\"rationale\":\"owner dead\"", "\"rationale\":\" \"", StringComparison.Ordinal));
        var unknownField = rig.Add(Op(extra: ",\"choice\":\"a\""));
        var otherClass = rig.Add(Op().Replace("\"operational\"", "\"strategic\"", StringComparison.Ordinal));
        var noClass = rig.Add(Op().Replace("\"class\":\"operational\",", string.Empty, StringComparison.Ordinal));

        Assert.All(new[] { otherRole, notSelf, noDecider, noRationale, unknownField, otherClass, noClass }, r => Assert.Equal("invalid", Reason(r)));
    }

    private const string Relayed = "{\"role\":\"sub-orchestrator\",\"name\":\"lane-ops\",\"relayed_by\":\"orchestrator\"}";

    [Fact]
    public void ALanesOperationalCallTheOrchestratorRelaysIsLoggedAsGivenAndCountsAsOperational()
    {
        var rig = Make();

        var result = rig.Add(Op("O5", Relayed));

        Assert.Null(result.Get("refused"));
        Assert.Equal("o1", result["id"].AsString());
        var line = rig.Decisions.ReadDecisions(rig.Log)["decisions"].AsArray()[0];
        Assert.Equal("operational", line.Member("kind")!.Value.AsString());
        Assert.Equal("O5", line.Member("o")!.Value.AsString());
        Assert.Equal("sub-orchestrator", line.Member("decider")!.Value.Member("role")!.Value.AsString());
        Assert.Equal("lane-ops", line.Member("decider")!.Value.Member("name")!.Value.AsString());
        Assert.Equal("orchestrator", line.Member("decider")!.Value.Member("relayed_by")!.Value.AsString());
        Assert.Equal(1, rig.Summary()["operational"].AsNumber());
    }

    [Theory]
    [InlineData("{\"role\":\"sub-orchestrator\",\"name\":\"\",\"relayed_by\":\"orchestrator\"}")]
    [InlineData("{\"role\":\"sub-orchestrator\",\"name\":null,\"relayed_by\":\"orchestrator\"}")]
    [InlineData("{\"role\":\"sub-orchestrator\",\"relayed_by\":\"orchestrator\"}")]
    [InlineData("{\"role\":\"sub-orchestrator\",\"name\":\"lane-ops\"}")]
    [InlineData("{\"role\":\"sub-orchestrator\",\"name\":\"lane-ops\",\"relayed_by\":\"architect\"}")]
    [InlineData("{\"role\":\"sub-orchestrator\",\"name\":\"lane-ops\",\"relayed_by\":\"orchestrator\",\"self\":true}")]
    [InlineData("{\"role\":\"sub-orchestrator\",\"name\":\"lane-ops\",\"relayed_by\":\"orchestrator\",\"model\":\"x\"}")]
    [InlineData("{\"role\":\"sub-orchestrator\",\"name\":\"lane-ops\",\"self\":true}")]
    [InlineData("{\"role\":\"architect\",\"name\":\"lane-ops\",\"relayed_by\":\"orchestrator\"}")]
    [InlineData("{\"role\":\"orchestrator\",\"name\":\"lane-ops\",\"relayed_by\":\"orchestrator\"}")]
    public void ARelayedDeciderMissingAPartOrCarryingAnotherIsRefused(string decider)
    {
        var rig = Make();

        var result = rig.Add(Op("O1", decider));

        Assert.Equal("invalid", Reason(result));
        Assert.Empty(rig.Decisions.ReadDecisions(rig.Log)["decisions"].AsArray());
    }

    [Theory]
    [InlineData("O10", null)]
    [InlineData("O11", "invalid")]
    public void O10WorkflowReshapeIsAnOperationalClassAndO11IsNot(string o, string? reason)
    {
        var rig = Make();

        var result = rig.Add(Op(o));

        Assert.Equal(reason, result.Get("refused") is null ? null : Reason(result));
    }

    [Theory]
    [InlineData(",\"why_user\":\"money\"")]
    [InlineData(",\"dangerous\":true")]
    public void ARelayedLineStillNamesNoReservedClass(string extra)
    {
        var rig = Make();

        var result = rig.Add(Op("O1", Relayed, extra));

        Assert.Equal("invalid", Reason(result));
    }

    [Fact]
    public void TwentyFiveRelayedLinesAreAcceptedBesideTheOrchestratorsOwn()
    {
        var rig = Make();

        for (var i = 1; i <= 25; i++)
        {
            Assert.Null(rig.Add(Op("O" + ((i % 9) + 1), i % 2 == 0 ? Relayed : Self)).Get("refused"));
        }

        Assert.Equal(25, rig.Summary()["operational"].AsNumber());
    }

    [Fact]
    public void ADecidedLineIsStillCappedAtTwentyAfterOperationalLinesAreWritten()
    {
        var rig = Make();
        for (var i = 0; i < 5; i++)
        {
            rig.Add(Op());
        }

        for (var i = 0; i < 20; i++)
        {
            Assert.Null(rig.Add(Decided("q" + i, "i" + i)).Get("refused"));
        }

        var over = rig.Add(Decided("q-over", "i-over"));

        Assert.Equal("cap", Reason(over));
    }
}
