using System.Text;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Hooks.Tests;

/// <summary>Which session gets which text from the rules of engagement (rules-of-engagement §5): the <c>session-rules</c> unit, the subagent block and the opt-in delivery.</summary>
public sealed class SessionRulesTests
{
    private const string Block = "BLOCK-MARKER";
    private const string AlwaysOn = """{"version":1,"alwaysOrchestrator":true}""";

    private sealed record Asked(string? Team, IReadOnlySet<string> Names, bool Orchestrator, string? Parent = null);

    private readonly List<Asked> _asked = [];

    private OrchestrateCommandTests.Fixture Fixture(string config = AlwaysOn, bool configured = true, string? rules = Block)
    {
        var fixture = new OrchestrateCommandTests.Fixture(configured: configured, config: config);
        fixture.RulesBlock = (_, who) =>
        {
            _asked.Add(new Asked(who.Team, who.Names, who.Orchestrator, who.Parent));
            return rules;
        };
        return fixture;
    }

    /// <summary>A session as a spawn launches it: with the member marker (SpawnShapes), and the team file its spawn names when given.</summary>
    private OrchestrateCommandTests.Fixture Member(string config, string? teamFile = null)
    {
        var environment = new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "m1" };
        if (teamFile is not null)
        {
            environment["WRANGL_TEAM_FILE"] = teamFile;
        }

        var fixture = new OrchestrateCommandTests.Fixture(environment, config: config);
        fixture.RulesBlock = (_, who) =>
        {
            _asked.Add(new Asked(who.Team, who.Names, who.Orchestrator, who.Parent));
            return Block;
        };
        return fixture;
    }

    /// <summary>b191: of the teams it owns, only one not held and with a live member counts; with two such and nothing naming one, the session gets no team's rules.</summary>
    [Theory]
    [InlineData("held", "t1")]
    [InlineData("stale", "t1")]
    [InlineData("t2", null)]
    public void AnOrchestratorGetsTheOneLiveTeamItOwnsAndNoTeamWhenItOwnsSeveral(string second, string? team)
    {
        var f = Fixture();
        var s = f.Rig.Services;
        s.Hierarchy.Core.Liveness = new AutoTrackTests.Live(true);
        var dir = s.Config.HierarchyDir(f.Rig.Sandbox.Root + "/proj");
        foreach (var (name, members) in new[] { ("t1", """[{"name":"m1"}]"""), (second, second == "stale" ? "[]" : """[{"name":"m1"}]""") })
        {
            var path = s.Roster.TeamPath(dir, name);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $$"""{"version":1,"orchestrator":{"session_id":"s1"},"members":{{members}}}""");
        }

        var hold = s.Roster.HoldPath(dir, "held");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(hold)!);
        File.WriteAllText(hold, """{"kind":"quit","phase":"held"}""");

        _ = Start(f);

        Assert.Equal(team, Assert.Single(_asked).Team);
    }

    /// <summary>b234: a lane session's team is its lane child with the manager next in its chain, before SessionStart has written the child.</summary>
    [Theory]
    [InlineData("my-sub", true)]
    [InlineData("wrangl:reviewer", false)]
    public void ALaneSessionGetsItsChildTeamAndTheManagerOnItsFirstStart(string agent, bool lane)
    {
        var child = HierarchyModule.LaneChildName("mo", "ws1");
        var root = Directory.CreateTempSubdirectory("session-rules-");
        using var removed = new RemovedAfter(root);
        var file = Path.Combine(root.FullName, ".claude", "ah2", "teams", "mo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"version":1,"members":[{"name":"m1","role":"reviewer"}],"streams":{"ws1":{"state":"open","sub_orchestrator":"m1"}}}""");
        var f = Member(LaneConfig, file);

        _ = Start(f, e => e["agent_type"] = agent);

        var asked = Assert.Single(_asked);
        Assert.Equal(lane, asked.Team == child);
        Assert.Equal(lane ? "mo" : null, asked.Parent);
    }

    private static string Run(OrchestrateCommandTests.Fixture f, string unit, string hookEvent, string session = "s1", Action<JsonObject>? tweak = null)
    {
        var e = new JsonObject { ["hook_event_name"] = hookEvent, ["session_id"] = session, ["cwd"] = f.Rig.Sandbox.Root + "/proj", ["source"] = "startup" };
        tweak?.Invoke(e);
        var services = f.Services();
        return Encoding.UTF8.GetString(f.Rig.Run(unit, JsonText.Stringify(e, 0), services: () => services).Stdout.Span);
    }

    private static string Start(OrchestrateCommandTests.Fixture f, Action<JsonObject>? tweak = null) => Run(f, "session-rules", "SessionStart", tweak: tweak);

    private static int Count(string text, string part) => text.Split(part).Length - 1;

    [Theory]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("clear")]
    [InlineData("compact")]
    [InlineData("fork")]
    public void AnOrchestratorGetsTheBlockAndItsTextForEverySource(string source)
    {
        var text = Start(Fixture(), e => e["source"] = source);

        Assert.Contains(Block, text, StringComparison.Ordinal);
        Assert.Contains("You also see that members follow these rules", text, StringComparison.Ordinal);
        Assert.Contains("--said", text, StringComparison.Ordinal);
        Assert.True(Assert.Single(_asked).Orchestrator);
    }

    [Fact]
    public void AnOrchestratorGetsItsTextEvenWithNoRules()
    {
        var text = Start(Fixture(rules: null));

        Assert.DoesNotContain(Block, text, StringComparison.Ordinal);
        Assert.Contains("You also see that members follow these rules", text, StringComparison.Ordinal);
    }

    /// <summary>ah2-54 D11: the capture sentence reaches the Orchestrator only.</summary>
    [Fact]
    public void AnOrchestratorIsAskedToProposeLessonsAndASubOrchestratorIsNot()
    {
        const string capture = "After rework or a CHANGES REQUIRED review, propose a lesson if it would have saved the round (`ah lessons propose`).";

        Assert.Contains(capture, Start(Fixture()), StringComparison.Ordinal);
        Assert.DoesNotContain("ah lessons", Start(Fixture(), e => e["agent_type"] = "wrangl:sub-orchestrator"), StringComparison.Ordinal);
        Assert.DoesNotContain("ah lessons", RulesOfEngagement.SubOrchestratorText, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorPassesRequestsUpInstead()
    {
        var text = Start(Fixture(), e => e["agent_type"] = "wrangl:sub-orchestrator");

        Assert.Contains(Block, text, StringComparison.Ordinal);
        Assert.Contains("to your parent", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ah rules add", text, StringComparison.Ordinal);
    }

    [Fact]
    public void APeerGetsOnlyTheBlockAsItsRole()
    {
        var text = Start(Fixture(), e => e["agent_type"] = "wrangl:implementor");

        Assert.Contains(Block, text, StringComparison.Ordinal);
        Assert.DoesNotContain("You also see", text, StringComparison.Ordinal);
        var asked = Assert.Single(_asked);
        Assert.False(asked.Orchestrator);
        Assert.Contains("implementor", asked.Names);
    }

    [Fact]
    public void APeerWithNoRulesGetsNothing() => Assert.Equal(string.Empty, Start(Fixture(rules: null), e => e["agent_type"] = "wrangl:implementor"));

    [Fact]
    public void ASubagentsSessionStartIsSilent()
    {
        var text = Start(Fixture(), e => e["agent_id"] = "a1");

        Assert.Equal(string.Empty, text);
        Assert.Empty(_asked);
    }

    [Fact]
    public void ADisabledHierarchyIsSilentForAPeerAndAnOrchestrator()
    {
        Assert.Equal(string.Empty, Start(Fixture("""{"version":1,"enabled":false,"alwaysOrchestrator":true}""")));
        Assert.Equal(string.Empty, Start(Fixture("""{"version":1,"enabled":false}"""), e => e["agent_type"] = "wrangl:implementor"));
        Assert.Empty(_asked);
    }

    [Fact]
    public void ABareSessionStaysBare()
    {
        Assert.Equal(string.Empty, Start(Fixture("""{"version":1}""")));
        Assert.Equal(string.Empty, Start(Fixture(configured: false)));
        Assert.Empty(_asked);
    }

    private static string Subagent(OrchestrateCommandTests.Fixture f, string agentType, string session = "s1") =>
        Run(f, "subagentstart-cli-root", "SubagentStart", session, e =>
        {
            e["agent_id"] = "a1";
            e["agent_type"] = agentType;
        });

    [Fact]
    public void ASubagentOfAnOrchestratorGetsTheBlockAsItsParentButNotTheOrchestratorsText()
    {
        var text = Subagent(Fixture(), "general-purpose");

        Assert.Contains(Block, text, StringComparison.Ordinal);
        Assert.DoesNotContain("You also see", text, StringComparison.Ordinal);
        Assert.True(Assert.Single(_asked).Orchestrator);
    }

    [Fact]
    public void WithNoRulesASubagentsTextIsWhatItWas()
    {
        var with = Fixture(rules: null);
        var without = Fixture(rules: null);
        without.RulesBlock = null;

        Assert.Equal(Subagent(without, "wrangl:reviewer"), Subagent(with, "wrangl:reviewer"));
    }

    [Fact]
    public void ANonPluginSubagentOfABareSessionGetsNothing()
    {
        Assert.Equal(string.Empty, Subagent(Fixture("""{"version":1}"""), "general-purpose"));
        Assert.Empty(_asked);
    }

    [Fact]
    public void ADisabledHierarchyGivesAPluginSubagentNoRules()
    {
        Assert.DoesNotContain(Block, Subagent(Fixture("""{"version":1,"enabled":false}"""), "wrangl:reviewer"), StringComparison.Ordinal);
        Assert.Empty(_asked);
    }

    [Fact]
    public void AnOptInDeliversTheRulesOnceAfterTheDirectiveAndAStartDeliveryHasNone()
    {
        var f = Fixture("""{"version":1}""");

        var opted = Encoding.UTF8.GetString(f.Typed("/wrangl:orchestrate")!.Stdout.Span);

        Assert.Equal(1, Count(opted, Block));
        Assert.Equal(1, Count(opted, "You also see that members follow these rules"));
        Assert.True(opted.IndexOf(Block, StringComparison.Ordinal) > opted.IndexOf("If this is cut to a preview", StringComparison.Ordinal));

        var startup = f.Start("startup", "s1");
        Assert.DoesNotContain(Block, startup, StringComparison.Ordinal);
        Assert.DoesNotContain("You also see", startup, StringComparison.Ordinal);
    }

    /// <summary>Rules-of-engagement §5: over the cap the model gets a 2,000 character preview, and the lead line, which says to read the saved file, has to be in it.</summary>
    [Theory]
    [InlineData("typed")]
    [InlineData("implicit")]
    public void AnOptInWithAFullRulesBlockKeepsTheLeadLineInsideThePreviewOnBothPaths(string path)
    {
        const string lead = "Wrangl Orchestrator directive. If this is cut to a preview, Read the saved file it names, in full, before anything else.";
        var block = new string('r', 4000);
        var f = Fixture("""{"version":1}""", rules: block);

        var stdout = path == "typed"
            ? Encoding.UTF8.GetString(f.Typed("/wrangl:orchestrate")!.Stdout.Span)
            : f.After("Skill", new JsonObject { ["skill"] = "wrangl:agent-team" });
        var emitted = JsonText.Parse(Encoding.UTF8.GetBytes(stdout)).Member("hookSpecificOutput")!.Value.Member("additionalContext")!.Value.AsString();

        var at = emitted.IndexOf(lead, StringComparison.Ordinal);
        Assert.InRange(at, 0, 2000 - lead.Length);
        Assert.True(emitted.Length > 2000 + block.Length);
        Assert.True(emitted.IndexOf(block, StringComparison.Ordinal) > at + lead.Length);
    }

    private sealed class RemovedAfter(DirectoryInfo directory) : IDisposable
    {
        public void Dispose() => directory.Delete(recursive: true);
    }

    /// <summary>Review N1: a row SessionStart has already stamped is not claimable by the marker; only SessionStart's own look reports that.</summary>
    [Fact]
    public void OnlySessionStartLogsAMemberRowItCannotClaim()
    {
        var root = Directory.CreateTempSubdirectory("session-rules-");
        using var removed = new RemovedAfter(root);
        var dir = Path.Combine(root.FullName, ".claude", "ah2");
        var teamFile = Path.Combine(dir, "teams", "tm.json");
        Directory.CreateDirectory(Path.GetDirectoryName(teamFile)!);
        File.WriteAllText(teamFile, """{"members":[{"name":"m1","role":"reviewer","transport_id":"%9"}]}""");
        var f = new OrchestrateCommandTests.Fixture(new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "m1", ["WRANGL_TEAM_FILE"] = teamFile });
        var services = f.Services();
        var cwd = f.Rig.Sandbox.Root + "/proj";

        var quiet = Ah.Hooks.Lifecycle.SessionStart.ResolveMemberRow(services, dir, cwd, "reviewer", "%1", HarnessSessions.Pid, "s1", logUnclaimable: false);
        Assert.True(quiet.Team.Member("members") is { IsArray: true }, quiet.Attributed.ToString());
        Assert.DoesNotContain(f.Rig.Errors.Records, r => r.Message.Contains("not claimable", StringComparison.Ordinal));

        _ = Ah.Hooks.Lifecycle.SessionStart.ResolveMemberRow(services, dir, cwd, "reviewer", "%1", HarnessSessions.Pid, "s1");
        Assert.Contains(f.Rig.Errors.Records, r => r.Message.Contains("not claimable", StringComparison.Ordinal));
    }

    private const string LaneConfig ="""{"version":1,"roles":{"lane-x":{"class":"orchestrate","agent":"my-lane"},"sub-orchestrator":{"agent":"my-sub"}}}""";

    private static void RecordRole(OrchestrateCommandTests.Fixture f, string role)
    {
        Ah.TestKit.TestHomeDocs.Put(f.Rig.Sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles, $"{{\"version\":1,\"sessions\":{{\"s1\":{{\"role\":\"{role}\"}}}}}}");
    }

    /// <summary>Review F1: SessionStart records the session role in parallel, and the answer must not depend on whether it has landed.</summary>
    [Theory]
    [InlineData("my-lane", "lane-x")]
    [InlineData("my-sub", "sub-orchestrator")]
    public void ALaneSessionGetsOneAnswerBeforeAndAfterItsRoleIsRecorded(string agent, string role)
    {
        var before = Member(LaneConfig);
        var early = Start(before, e => e["agent_type"] = agent);

        var after = Member(LaneConfig);
        RecordRole(after, role);
        var late = Start(after, e => e["agent_type"] = agent);

        Assert.Equal(early, late);
        Assert.Contains(RulesOfEngagement.SubOrchestratorText, Decoded(early), StringComparison.Ordinal);
        Assert.All(_asked, a => Assert.True(a.Orchestrator));
    }

    [Fact]
    public void AnAgentThatIsNoLaneRoleIsNotTakenForOne()
    {
        Assert.Equal(string.Empty, Start(Fixture(LaneConfig), e => e["agent_type"] = "not-my-lane"));
        Assert.Empty(_asked);
    }

    [Fact]
    public void APeerOfABuiltInRoleStaysAPeerWithALaneInTheRegistry()
    {
        var text = Decoded(Start(Member(LaneConfig), e => e["agent_type"] = "wrangl:reviewer"));

        Assert.DoesNotContain(RulesOfEngagement.SubOrchestratorText, text, StringComparison.Ordinal);
        Assert.False(Assert.Single(_asked).Orchestrator);
    }

    /// <summary>A teamless <c>--agent</c> session of any role is out (ah-silent-rulings Q1): a spawned one carries the member marker.</summary>
    [Theory]
    [InlineData("my-lane")]
    [InlineData("wrangl:reviewer")]
    public void NegativeATeamlessRoleSessionGetsNoRules(string agent)
    {
        Assert.Equal(string.Empty, Start(Fixture(LaneConfig), e => e["agent_type"] = agent));
        Assert.Empty(_asked);
    }

    [Fact]
    public void NegativeAClearOfASessionNeverFlaggedGetsNoRules()
    {
        Assert.Equal(string.Empty, Cleared(Fixture("""{"version":1}"""), "clear"));
        Assert.Empty(_asked);
    }

    private static string Decoded(string stdout) => JsonText.Parse(Encoding.UTF8.GetBytes(stdout)).Member("hookSpecificOutput")!.Value.Member("additionalContext")!.Value.AsString();

    private static string Cleared(OrchestrateCommandTests.Fixture f, string source) =>
        Run(f, "session-rules", "SessionStart", session: "s2", tweak: e => e["source"] = source);

    /// <summary>Review F2: on /clear this hook may run before SessionStart has moved the flag to the new id.</summary>
    [Fact]
    public void AFlaggedSessionKeepsItsRulesOnClearBeforeSessionStartMovesTheFlag()
    {
        var f = Fixture("""{"version":1}""");
        _ = f.Typed("/wrangl:orchestrate");

        Assert.Contains("You also see", Cleared(f, "clear"), StringComparison.Ordinal);
        Assert.True(f.In("s2"));
        Assert.False(f.In("s1"));
        // the second hook's move finds nothing left to do
        Assert.Contains("You also see", Cleared(f, "clear"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("compact")]
    public void OnlyClearMovesTheFlag(string source)
    {
        var f = Fixture("""{"version":1}""");
        _ = f.Typed("/wrangl:orchestrate");

        Assert.Equal(string.Empty, Cleared(f, source));
        Assert.True(f.In("s1"));
    }

    [Fact]
    public void ClearDoesNotTakeTheFlagOfAnotherProcess()
    {
        var f = Fixture("""{"version":1}""");
        _ = f.Typed("/wrangl:orchestrate", pid: 4343);

        Assert.Equal(string.Empty, Cleared(f, "clear"));
        Assert.False(f.In("s2"));
    }

    [Fact]
    public void AnOffOptInDeliversNoRules()
    {
        var f = Fixture("""{"version":1}""");
        _ = f.Typed("/wrangl:orchestrate");

        var off = Encoding.UTF8.GetString(f.Typed("/wrangl:orchestrate off")!.Stdout.Span);

        Assert.DoesNotContain(Block, off, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOrchestratorTextsAreDifferent() => Assert.NotEqual(RulesOfEngagement.OrchestratorText, RulesOfEngagement.SubOrchestratorText);
}
