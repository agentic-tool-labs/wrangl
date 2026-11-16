using Ah.Abstractions;
using Ah.Hooks.Plumbing;

namespace Ah.Hooks.Tests;

/// <summary>
/// The identity matrix: a session nothing identifies gets no gate, no injected text and no record from any hook unit, and a member,
/// a top-level Orchestrator and an opted-in session still reach the units.
/// </summary>
public sealed partial class HooksHostTests
{
    private const string BareSession = "11111111-1111-4111-8111-111111111111";

    private static string BareEvent(Rig rig, string hookEvent, string extra = "") =>
        $$"""{"hook_event_name":"{{hookEvent}}","session_id":"{{BareSession}}","cwd":"{{rig.Sandbox.Root}}/proj"{{extra}}}""";

    private const string HerdrBash = ""","tool_name":"Bash","tool_use_id":"t1","tool_input":{"command":"herdr agent start Bad_Name"}""";

    [Fact]
    public void EveryUnitIsSilentForASessionNothingIdentifies()
    {
        foreach (var unit in HooksModule.Units)
        {
            var rig = new Rig(bare: true);

            var outcome = rig.Run(unit.Name, BareEvent(rig, "PreToolUse", HerdrBash));

            Assert.True(outcome.Stdout.IsEmpty, unit.Name);
            Assert.Equal(0, outcome.ExitCode);
            Assert.Empty(rig.Writer.Mutations);
        }
    }

    [Fact]
    public void TheChainIsSilentForASessionNothingIdentifies()
    {
        var rig = new Rig(bare: true);

        Assert.True(rig.Run(HookHost.ChainName, BareEvent(rig, "PreToolUse", HerdrBash)).Stdout.IsEmpty);
    }

    [Fact]
    public void SessionStartLeavesNoStatusFileForASessionNothingIdentifies()
    {
        var rig = new Rig(bare: true);

        Assert.True(rig.Run("sessionstart", BareEvent(rig, "SessionStart", ""","source":"startup" """)).Stdout.IsEmpty);
        Assert.False(File.Exists(rig.Sandbox.Root + "/proj/.claude/ah2/status.json"));
    }

    /// <summary>Negative: each identity the predicate accepts still reaches the gate that a bare session skips.</summary>
    [Theory]
    [InlineData("AH_MEMBER_NAME", "")]
    [InlineData("WRANGL_TEAM_FILE", "")]
    [InlineData("", ""","agent_type":"wrangl:orchestrator" """)]
    public void AnIdentifiedSessionStillReachesTheGate(string variable, string extra)
    {
        var environment = variable.Length > 0 ? new Dictionary<string, string> { [variable] = "x" } : null;
        var rig = new Rig(extraEnvironment: environment, bare: true);

        var outcome = rig.Run(HookHost.ChainName, BareEvent(rig, "PreToolUse", extra + HerdrBash));

        Assert.False(outcome.Stdout.IsEmpty);
    }

    private static HookOutcome RunChain(Rig rig, string json, IReadOnlyList<GateUnit> units, Func<Ah.Hooks.GateServices>? services = null, TimeSpan? deadline = null) =>
        HookHost.Run(HookHost.ChainName, true, System.Text.Encoding.UTF8.GetBytes(json), rig.Errors, services ?? (() => rig.Services), units, deadline);

    private static GateUnit StubGate(GateBody body) => new("pretooluse-sendmessage-response", body, GateClass.Advisory, Matcher: "*");

    private static readonly Dictionary<string, string> Member = new() { ["AH_MEMBER_NAME"] = "m1" };

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"session_id":"s"}""")]
    public void UnusableInputOnTheChainPassesWithOneRow(string json)
    {
        var rig = new Rig(Member);

        var outcome = RunChain(rig, json, []);

        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Single(rig.Errors.Records);
    }

    [Fact]
    public void AStoreFailureWhileIdentifyingPassesWithOneRow()
    {
        var rig = new Rig(bare: true);

        var outcome = RunChain(rig, BareEvent(rig, "PreToolUse", HerdrBash), [StubGate(_ => HookEmit.Deny("no", "no"))], () => throw new Ah.Core.Json.StoreUnavailableException());

        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Single(rig.Errors.Records);
    }

    [Fact]
    public void IdentifyingThatOutlivesTheBudgetPassesWithOneRow()
    {
        var rig = new Rig(bare: true);

        var outcome = RunChain(
            rig,
            BareEvent(rig, "PreToolUse", HerdrBash),
            [StubGate(_ => HookEmit.Deny("no", "no"))],
            () =>
            {
                Thread.Sleep(2000);
                return rig.Services;
            },
            TimeSpan.FromMilliseconds(100));

        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Single(rig.Errors.Records);
    }

    [Fact]
    public void ARefusedHomePassesWithOneRow()
    {
        var rig = new Rig(Member);

        var outcome = HooksModule.Refused(HookHost.ChainName, "AH_HOME is not trusted", rig.Errors);

        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Equal("AH_HOME is not trusted", Assert.Single(rig.Errors.Records).Message);
    }

    /// <summary>Negative: a member's gate that outlives its budget still denies, since the session was identified first.</summary>
    [Fact]
    public void AnIdentifiedMembersGateTimeoutDenies()
    {
        var rig = new Rig(Member, bare: true);
        GateBody slow = _ =>
        {
            Thread.Sleep(2000);
            return HookEmit.Silent;
        };

        Assert.True(IsDeny(RunChain(rig, BareEvent(rig, "PreToolUse", HerdrBash), [StubGate(slow)], deadline: TimeSpan.FromMilliseconds(500))));
    }

    /// <summary>Negative: a member's gate still denies on its merits, and the same gate never runs for a bare session.</summary>
    [Fact]
    public void AGateDeniesOnItsMeritsOnlyForAnIdentifiedSession()
    {
        var member = new Rig(Member, bare: true);
        var bare = new Rig(bare: true);
        GateBody deny = _ => HookEmit.Deny("no", "no");

        Assert.True(IsDeny(RunChain(member, BareEvent(member, "PreToolUse", HerdrBash), [StubGate(deny)])));
        Assert.True(RunChain(bare, BareEvent(bare, "PreToolUse", HerdrBash), [StubGate(deny)]).Stdout.IsEmpty);
        Assert.Empty(bare.Errors.Records);
    }

    private static bool IsDeny(HookOutcome o) => Json(o).Member("hookSpecificOutput")!.Value.Member("permissionDecision")!.Value.AsString() == "deny";

    /// <summary>Negative: a teamless top-level role other than the Orchestrator is not identified by its agent_type alone.</summary>
    [Fact]
    public void ATeamlessRoleSessionIsBare()
    {
        var rig = new Rig(bare: true);

        Assert.True(rig.Run(HookHost.ChainName, BareEvent(rig, "PreToolUse", ""","agent_type":"wrangl:reviewer" """ + HerdrBash)).Stdout.IsEmpty);
    }

    /// <summary>Negative: a subagent's own ah2 agent_type does not identify a parent nothing else identifies.</summary>
    [Fact]
    public void AnAhSubagentOfABareSessionIsBare()
    {
        var rig = new Rig(bare: true);

        Assert.True(rig.Run(HookHost.ChainName, BareEvent(rig, "PreToolUse", ""","agent_id":"a1","agent_type":"wrangl:task-runner" """ + HerdrBash)).Stdout.IsEmpty);
    }
}
