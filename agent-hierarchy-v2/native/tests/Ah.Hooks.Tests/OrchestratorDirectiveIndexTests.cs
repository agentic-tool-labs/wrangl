using System.Text;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>
/// The top-level Orchestrator's directive as a resident core plus an index (ah2-68): nothing lost between the two, three SessionStart outputs
/// under the cap, and every other session's text as it was.
/// </summary>
public sealed class OrchestratorDirectiveIndexTests
{
    private const string LeadLine = "If this is cut to a preview, Read the saved file it names, in full";

    private const string IndexHeader = "On demand — at each trigger, run its command first and follow what it prints:";

    private static IDirectiveText Directive(OrchestrateCommandTests.Fixture f)
    {
        var s = f.Rig.Services;
        return ConfigModule.CreateDirectiveText(s.Config, ConfigModule.CreateRoleContracts(s.Config, s.Files, s.Environment), () => s.Roster);
    }

    private static GateServices Services(OrchestrateCommandTests.Fixture f, int pid = HarnessSessions.Pid)
    {
        var services = f.Services(pid);
        return services with { Advisory = services.Advisory! with { Directive = Directive(f) } };
    }

    private static string Cwd(OrchestrateCommandTests.Fixture f) => f.Rig.Sandbox.Root + "/proj";

    private static JsonObject Resolved(OrchestrateCommandTests.Fixture f) => f.Rig.Services.Config.ResolveConfig(Cwd(f), new ResolveOptions(SessionId: "s1"));

    private static string Dir(OrchestrateCommandTests.Fixture f) => f.Rig.Services.Config.HierarchyDir(Cwd(f));

    /// <summary>One unit's additionalContext for a SessionStart, or the empty string when it is silent.</summary>
    private static string Output(OrchestrateCommandTests.Fixture f, string unit, string source = "startup", Action<JsonObject>? tweak = null, int pid = HarnessSessions.Pid)
    {
        var e = new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = "s1", ["cwd"] = Cwd(f), ["source"] = source };
        tweak?.Invoke(e);
        var services = Services(f, pid);
        var stdout = Encoding.UTF8.GetString(f.Rig.Run(unit, JsonText.Stringify(e, 0), services: () => services).Stdout.Span);
        return stdout.Length == 0 ? string.Empty : JsonText.Parse(stdout).Member("hookSpecificOutput")!.Value.Member("additionalContext")!.Value.AsString();
    }

    private static OrchestrateCommandTests.Fixture Flagged(string config = """{"version":1}""")
    {
        var f = new OrchestrateCommandTests.Fixture(config: config);
        _ = f.Typed("/wrangl:orchestrator");
        Assert.True(f.Flagged("s1"));
        return f;
    }

    private static string[] Lines(string text) => text.Split('\n');

    // ---------------------------------------------------------------- no rule is lost

    [Theory]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":1,"handoffs":"confirm"}""")]
    public void EveryLineOfTheFullDirectiveIsResidentOrInExactlyOneSection(string config)
    {
        var f = new OrchestrateCommandTests.Fixture(config: config);
        var directive = Directive(f);
        var resolved = Resolved(f);
        var full = Lines(directive.BuildDirective(resolved, "s1", Dir(f)));
        var resident = Lines(directive.BuildOrchestratorDirective(resolved, Dir(f)) + "\n" + directive.BuildOrchestratorIndex(resolved)).ToHashSet(StringComparer.Ordinal);
        var sections = ConfigModule.DirectiveIndex().Where(e => e.Key != "identity")
            .ToDictionary(e => e.Key, e => Lines(directive.DirectiveSection(e.Key, resolved, Dir(f)) ?? throw new InvalidOperationException($"no section {e.Key}")), StringComparer.Ordinal);

        foreach (var line in full.Where(l => l.Length > 0))
        {
            var homes = sections.Where(s => s.Value.Contains(line, StringComparer.Ordinal)).Select(s => s.Key).ToList();
            Assert.True(resident.Contains(line) ^ homes.Count == 1, $"resident={resident.Contains(line)} sections=[{string.Join(", ", homes)}]: {line}");
        }

        // and the sections print only lines of the full directive: rebuilt, the two are the full text
        var rebuilt = full.Where(l => resident.Contains(l)).Concat(sections.Values.SelectMany(v => v)).Where(l => l.Length > 0).Order(StringComparer.Ordinal);
        Assert.Equal(full.Where(l => l.Length > 0).Order(StringComparer.Ordinal), rebuilt);
    }

    [Theory]
    [InlineData("""{"version":1}""", "auto")]
    [InlineData("""{"version":1,"handoffs":"confirm"}""", "confirm")]
    public void FlowAndRoutingLeaveOneResidentLineEachThatNamesTheirKey(string config, string mode)
    {
        var f = new OrchestrateCommandTests.Fixture(config: config);
        var directive = Directive(f);

        var resident = directive.BuildOrchestratorDirective(Resolved(f), Dir(f));

        Assert.Contains($"\nHandoffs are currently {mode}; the user may flip it at any time: `ah rules show flow`.\n", resident, StringComparison.Ordinal);
        Assert.Contains("\nCustom implement-class routes exist: `ah rules show routing` before choosing who builds.\n", resident, StringComparison.Ordinal);
        Assert.DoesNotContain("10. Flow control", resident, StringComparison.Ordinal);
        Assert.DoesNotContain("ROUTING — custom alternatives", resident, StringComparison.Ordinal);
        Assert.Contains("14. TIER RULE", resident, StringComparison.Ordinal);
        Assert.StartsWith($"10. Flow control — handoffs are currently \"{mode}\"", directive.DirectiveSection("flow", Resolved(f), Dir(f)), StringComparison.Ordinal);
        Assert.StartsWith("15. ROUTING — custom alternatives", directive.DirectiveSection("routing", Resolved(f), Dir(f)), StringComparison.Ordinal);
    }

    [Fact]
    public void WithConfirmHandoffsItemZeroIsResidentAndWithAutoThereIsNone()
    {
        var confirm = new OrchestrateCommandTests.Fixture(config: """{"version":1,"handoffs":"confirm"}""");
        var auto = new OrchestrateCommandTests.Fixture();

        var withGate = Directive(confirm).BuildOrchestratorDirective(Resolved(confirm), Dir(confirm));
        Assert.Contains("\n0. Handoff gate", withGate, StringComparison.Ordinal);
        Assert.DoesNotContain("0. Handoff gate", Directive(auto).BuildOrchestratorDirective(Resolved(auto), Dir(auto)), StringComparison.Ordinal);

        // Amendment 3 F1: item 0's "PEER BRIEF CONTRACT above" still has a resident header to point at, ahead of the item
        Assert.Contains("follow the PEER BRIEF CONTRACT above", withGate, StringComparison.Ordinal);
        var header = withGate.IndexOf("\nPEER BRIEF CONTRACT (core; the rest: `ah rules show message-files`):\n- Open with the sentinel line", StringComparison.Ordinal);
        Assert.True(header > 0 && header < withGate.IndexOf("\n0. Handoff gate", StringComparison.Ordinal));
    }

    /// <summary>
    /// The shipped registry fixes routes on the built-in Docs-Writer and UX-Engineer, so the default configuration has routes and the index lists
    /// the entry; the no-routes rendering is pinned in OrchestratorSlimLinesTests.
    /// </summary>
    [Fact]
    public void WithCustomRoutesTheIndexListsRoutingAndTheResidentLineNamesIt()
    {
        var f = new OrchestrateCommandTests.Fixture();
        var directive = Directive(f);

        Assert.Contains("- before choosing who builds an implement-class item (item 15): ah rules show routing", directive.BuildOrchestratorIndex(Resolved(f)), StringComparison.Ordinal);
        Assert.Contains("Custom implement-class routes exist", directive.BuildOrchestratorDirective(Resolved(f), Dir(f)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndexCarriesTheTeamHoldStopAndSkillsNevers()
    {
        var f = new OrchestrateCommandTests.Fixture();

        var index = Directive(f).BuildOrchestratorIndex(Resolved(f));

        Assert.Contains("- A team hold (`ah roster quit`, `ah roster resume --hold`) is the user's order only (identity: team hold).", index, StringComparison.Ordinal);
        Assert.Contains("- `ah roster stop --now --confirm` unasked only when a member's work is itself doing harm (identity: team hold).", index, StringComparison.Ordinal);
        Assert.Contains("never a raw roster MCP call (item 13)", index, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdentityIsResidentDeferredOrARepeatOfAResidentDirectiveLineAndNeverTwo()
    {
        var f = new OrchestrateCommandTests.Fixture();
        var body = ConfigModule.AgentBody(File.ReadAllText(Path.Combine(f.PluginRoot, "agents", "orchestrator.md")))!;
        var (resident, deferred) = ConfigModule.SplitOrchestratorIdentity(body);
        var directive = Directive(f).BuildOrchestratorDirective(Resolved(f), Dir(f));

        foreach (var line in Lines(body).Where(l => l.Trim().Length > 0))
        {
            var inResident = Lines(resident).Contains(line, StringComparer.Ordinal);
            var inDeferred = Lines(deferred).Contains(line, StringComparer.Ordinal);
            var repeat = line.StartsWith("- Run the friction loop", StringComparison.Ordinal) || line.StartsWith("A runaway process", StringComparison.Ordinal);
            Assert.True((inResident ? 1 : 0) + (inDeferred ? 1 : 0) + (repeat ? 1 : 0) == 1, line);
        }

        Assert.Contains("Friction loop", directive, StringComparison.Ordinal);
        Assert.Contains("**Never call the generic `advisor` tool.**", resident, StringComparison.Ordinal);
        Assert.Contains("**ADRs.**", deferred, StringComparison.Ordinal);
        Assert.Contains("**A brief travels by mailbox.**", deferred, StringComparison.Ordinal);
        Assert.DoesNotContain("A brief travels by mailbox", resident, StringComparison.Ordinal);
        Assert.Contains("A brief travels by mailbox: `ah msg new`", directive, StringComparison.Ordinal);
        Assert.Contains("**Team hold:**", deferred, StringComparison.Ordinal);
        Assert.Equal(deferred, Directive(f).DirectiveSection("identity", Resolved(f), Dir(f), body));
    }

    // ---------------------------------------------------------------- the sections read as they did inline

    [Fact]
    public void ASectionReflectsTheCallersResolvedConfig()
    {
        var f = new OrchestrateCommandTests.Fixture();
        var directive = Directive(f);

        var section = directive.DirectiveSection("message-files", Resolved(f), Dir(f))!;
        var elsewhere = directive.DirectiveSection("message-files", Resolved(f), "/elsewhere/.claude/ah2")!;

        Assert.Contains($"Files under {Dir(f)}/msgs/", section, StringComparison.Ordinal);
        Assert.Contains("Files under /elsewhere/.claude/ah2/msgs/", elsewhere, StringComparison.Ordinal);
        Assert.Contains($"Files under {Dir(f)}/msgs/", directive.BuildDirective(Resolved(f), "s1", Dir(f)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("Identity")]
    [InlineData("")]
    public void NegativeAnUnknownKeyHasNoSection(string key)
    {
        var f = new OrchestrateCommandTests.Fixture();

        Assert.Null(Directive(f).DirectiveSection(key, Resolved(f), Dir(f)));
    }

    [Fact]
    public void NegativeTheIdentitySectionWithoutABodyIsNoneRatherThanEmpty()
    {
        var f = new OrchestrateCommandTests.Fixture();

        Assert.Null(Directive(f).DirectiveSection("identity", Resolved(f), Dir(f)));
    }

    // ---------------------------------------------------------------- the three outputs

    [Theory]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("clear")]
    [InlineData("compact")]
    public void DefaultConfigurationEachOfTheFourOutputsIsUnderTheCapWithNoLeadLine(string source)
    {
        var f = Flagged();

        var start = Output(f, "sessionstart", source);
        var directive = Output(f, "session-directive", source);
        var index = Output(f, "session-index", source);
        var rules = Output(f, "session-rules", source);

        foreach (var (name, text) in new[] { ("sessionstart", start), ("session-directive", directive), ("session-index", index), ("session-rules", rules) })
        {
            Assert.True(text.Length is > 0 and <= SessionStart.OutputCap, $"{name}: {text.Length}");
            Assert.DoesNotContain(LeadLine, text, StringComparison.Ordinal);
        }

        Assert.True(start.Length + directive.Length + index.Length + rules.Length <= 28_799 * 60 / 100, $"total {start.Length + directive.Length + index.Length + rules.Length}");
        Assert.Contains("HIERARCHY STATE", start, StringComparison.Ordinal);
        Assert.Contains("ah CLI root", start, StringComparison.Ordinal);
        Assert.DoesNotContain("Agent hierarchy ACTIVE", start, StringComparison.Ordinal);
        Assert.StartsWith("Agent hierarchy ACTIVE. You are the Orchestrator", directive, StringComparison.Ordinal);
        Assert.DoesNotContain(IndexHeader, directive, StringComparison.Ordinal);
        Assert.Contains(IndexHeader, index, StringComparison.Ordinal);
        Assert.StartsWith("`<ah>` = the `ah` command", rules, StringComparison.Ordinal);
        Assert.DoesNotContain("**ADRs.**", rules, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIndexOutputIsTheLookUpRuleThenTheIndexThenTheNevers()
    {
        var f = Flagged();

        var lines = Lines(Output(f, "session-index"));

        Assert.StartsWith("Before you decide or take an action that an index entry below relates to, run its `ah rules show <key>` first", lines[0], StringComparison.Ordinal);
        Assert.Equal(IndexHeader, lines[1]);
        Assert.Equal(ConfigModule.DirectiveIndexLines(), lines[2..(2 + ConfigModule.DirectiveIndex().Count)]);
        Assert.Equal("Never, from the on-demand sections (no gate enforces these):", lines[3 + ConfigModule.DirectiveIndex().Count]);
        Assert.Contains("A release waits for full QA on its final commit.", Lines(Output(f, "session-directive")));
    }

    [Fact]
    public void TheOptInPayloadIsTheFourOutputsConcatenated()
    {
        var f = Flagged();
        var services = Services(f);
        var e = new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = "s1", ["cwd"] = Cwd(f) };

        var payload = SessionStart.OptInPayload(new GateContext(services, e, "userpromptsubmit-peer-tracking"), Cwd(f))!;

        Assert.Contains("HIERARCHY STATE", payload, StringComparison.Ordinal);
        Assert.Contains(IndexHeader, payload, StringComparison.Ordinal);
        Assert.Contains("You are the Chief Orchestrator", payload, StringComparison.Ordinal);
        Assert.True(payload.IndexOf("HIERARCHY STATE", StringComparison.Ordinal) < payload.IndexOf("Agent hierarchy ACTIVE", StringComparison.Ordinal));
        Assert.True(payload.IndexOf("Agent hierarchy ACTIVE", StringComparison.Ordinal) < payload.IndexOf(IndexHeader, StringComparison.Ordinal));
        Assert.True(payload.IndexOf(IndexHeader, StringComparison.Ordinal) < payload.IndexOf("You are the Chief Orchestrator", StringComparison.Ordinal));
        Assert.Equal(payload.Length > SessionStart.OutputCap, payload.Contains(LeadLine, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(SessionStart.OutputCap, false)]
    [InlineData(SessionStart.OutputCap + 1, true)]
    public void TheLeadLineGoesOnlyOnAnOutputOverTheCap(int length, bool lead) =>
        Assert.Equal(lead, SessionStart.WithLeadLine(new string('x', length)).Contains(LeadLine, StringComparison.Ordinal));

    // ---------------------------------------------------------------- every other session as it was

    [Theory]
    [InlineData("wrangl:sub-orchestrator")]
    [InlineData("wrangl:architect")]
    public void NegativeASessionThatIsNotTheTopOrchestratorGetsNoIndex(string agent)
    {
        var f = new OrchestrateCommandTests.Fixture(new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "m1" });

        Assert.Equal(string.Empty, Output(f, "session-directive", tweak: e => e["agent_type"] = agent));
        Assert.Equal(string.Empty, Output(f, "session-index", tweak: e => e["agent_type"] = agent));
        Assert.DoesNotContain(IndexHeader, Output(f, "sessionstart", tweak: e => e["agent_type"] = agent), StringComparison.Ordinal);
    }

    /// <summary>
    /// A lane session's role is recorded by sessionstart in the same event; the directive unit picks as sessionstart does without that record:
    /// a lane of the built-in sub-orchestrator keeps its directive in sessionstart, a custom lane role (not orchestrate without the registry) gets the index.
    /// </summary>
    [Theory]
    [InlineData("my-sub", false)]
    [InlineData("my-lane", true)]
    public void ALaneSessionOnItsFirstStartGetsTheDirectiveFromExactlyOneUnit(string agent, bool index)
    {
        var f = new OrchestrateCommandTests.Fixture(
            new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "m1" },
            config: """{"version":1,"roles":{"lane-x":{"class":"orchestrate","agent":"my-lane"},"sub-orchestrator":{"agent":"my-sub"}}}""");

        var directive = Output(f, "session-directive", tweak: e => e["agent_type"] = agent);
        var indexText = Output(f, "session-index", tweak: e => e["agent_type"] = agent);
        var start = Output(f, "sessionstart", tweak: e => e["agent_type"] = agent);

        Assert.Equal(index, directive.Contains("Agent hierarchy ACTIVE", StringComparison.Ordinal));
        Assert.Equal(index, indexText.Contains(IndexHeader, StringComparison.Ordinal));
        Assert.Equal(!index, start.Contains("Agent hierarchy ACTIVE", StringComparison.Ordinal));
    }

    private static readonly string[] OwnedTeams = ["ta", "tb"];

    /// <summary>
    /// b100: the directive unit rebinds a --agent Orchestrator's teams before it resolves, so with two owned teams whose records name this
    /// session and a dead pid its role lines list both. A plain top-level session, or a start refused (pid 5151 is not a Claude harness),
    /// triggers no rebind and its records stay as they were.
    /// </summary>
    [Theory]
    [InlineData("wrangl:orchestrator", HarnessSessions.Pid, true)]
    [InlineData(null, HarnessSessions.Pid, false)]
    [InlineData("wrangl:orchestrator", 5151, false)]
    public void TheDirectiveUnitRebindsAnAgentOrchestratorsTeamsBeforeItsRoleLines(string? agent, int pid, bool rebinds)
    {
        var f = new OrchestrateCommandTests.Fixture();
        var dir = Dir(f);
        var stale = new JsonObject { ["pid"] = 99, ["pid_start"] = "Fri Oct 16 08:00:00 2026", ["session_id"] = "s1" };
        foreach (var team in OwnedTeams)
        {
            var path = f.Rig.Services.Roster.TeamPath(dir, team);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, JsonText.ToUtf8(new JsonObject { ["version"] = 1, ["team_id"] = team, ["orchestrator"] = stale.Clone(), ["members"] = new JsonArray() }, 0));
        }

        var before = OwnedTeams.Select(t => File.ReadAllText(f.Rig.Services.Roster.TeamPath(dir, t))).ToList();
        var rebound = new List<RebindRequest>();
        var callers = new BindingAttestationTests.Callers(onRebind: r =>
        {
            // stands in for MailCallers.RebindOwnedTeams in Pid mode: the session already matches, the dead pid becomes this process
            rebound.Add(r);
            foreach (var t in OwnedTeams)
            {
                var path = f.Rig.Services.Roster.TeamPath(dir, t);
                var doc = JsonText.Parse(File.ReadAllBytes(path)).AsObject().Clone();
                var orchestrator = doc.Get("orchestrator")!.Value.AsObject().Clone();
                orchestrator["pid"] = (double)r.Pid;
                orchestrator["pid_start"] = HarnessSessions.Start;
                doc["orchestrator"] = orchestrator;
                File.WriteAllBytes(path, JsonText.ToUtf8(doc, 0));
            }
        });
        var services = Services(f, pid);
        services = services with { Advisory = services.Advisory! with { Callers = callers } };
        var e = new JsonObject { ["hook_event_name"] = "SessionStart", ["session_id"] = "s1", ["cwd"] = Cwd(f), ["source"] = "resume" };
        if (agent is not null)
        {
            e["agent_type"] = agent;
        }

        var stdout = Encoding.UTF8.GetString(f.Rig.Run("session-directive", JsonText.Stringify(e, 0), services: () => services).Stdout.Span);
        var text = stdout.Length == 0 ? string.Empty : JsonText.Parse(stdout).Member("hookSpecificOutput")!.Value.Member("additionalContext")!.Value.AsString();

        if (rebinds)
        {
            Assert.Equal("s1", Assert.Single(rebound).SessionId);
            Assert.Contains(Lines(text), l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("--team ta ", StringComparison.Ordinal) && l.Contains("--team tb ", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(rebound);
            Assert.Equal(before, OwnedTeams.Select(t => File.ReadAllText(f.Rig.Services.Roster.TeamPath(dir, t))));
        }
    }

    [Fact]
    public void NegativeTheDeputyGetsNoIndexAndKeepsItsDirectiveInSessionStart()
    {
        var f = new OrchestrateCommandTests.Fixture(new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "m1" });

        Assert.Equal(string.Empty, Output(f, "session-directive", tweak: e => e["agent_type"] = "wrangl:deputy-chief"));
        Assert.Equal(string.Empty, Output(f, "session-index", tweak: e => e["agent_type"] = "wrangl:deputy-chief"));
    }

    [Fact]
    public void NegativeABareSessionGetsNoDirective()
    {
        var f = new OrchestrateCommandTests.Fixture();

        Assert.Equal(string.Empty, Output(f, "session-directive"));
        Assert.Equal(string.Empty, Output(f, "session-index"));
    }

    [Fact]
    public void NegativeADisabledRepoGetsNoDirectiveEvenForAFlaggedSession()
    {
        var f = Flagged();
        File.WriteAllText(Cwd(f) + "/.claude/ah2/agent-hierarchy.json", """{"version":1,"enabled":false}""");

        Assert.Equal(string.Empty, Output(f, "session-directive"));
        Assert.Equal(string.Empty, Output(f, "session-index"));
    }

    /// <summary>Amendment 3 F6: a start that sessionstart refuses (the parent is no Claude harness) gets no identity, directive or index; the same session from its harness does.</summary>
    [Theory]
    [InlineData(HarnessSessions.Pid, true)]
    [InlineData(5555, false)]
    public void ARefusedStartGetsNoIdentityDirectiveOrIndex(int pid, bool delivered)
    {
        var f = Flagged();

        Assert.Equal(delivered, Output(f, "session-rules", pid: pid).Contains("You are the Chief Orchestrator", StringComparison.Ordinal));
        Assert.Equal(delivered, Output(f, "session-directive", pid: pid).Length > 0);
        Assert.Equal(delivered, Output(f, "session-index", pid: pid).Length > 0);
    }

    [Fact]
    public void ASubOrchestratorKeepsItsFullDirectiveAndLeadLineInSessionStart()
    {
        var f = new OrchestrateCommandTests.Fixture(new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "m1" });

        var start = Output(f, "sessionstart", tweak: e => e["agent_type"] = "wrangl:sub-orchestrator");

        Assert.StartsWith("Wrangl Orchestrator directive. " + LeadLine, start, StringComparison.Ordinal);
    }

    /// <summary>Evidence: with AH_PROBE_OUT set, the characters of each output for an opted-in Orchestrator, default configuration.</summary>
    [Fact]
    public void Evidence()
    {
        if (Environment.GetEnvironmentVariable("AH_PROBE_OUT") is not { Length: > 0 } path)
        {
            return;
        }

        var f = Flagged();
        var lines = new List<string>();
        foreach (var source in new[] { "startup", "compact" })
        {
            foreach (var unit in new[] { "sessionstart", "session-directive", "session-index", "session-rules" })
            {
                var text = Output(f, unit, source);
                lines.Add($"after\t{source}\t{unit}\t{text.Length}");
                File.WriteAllText($"{path}.after.{source}.{unit}.txt", text);
            }
        }

        var services = Services(f);
        var payload = SessionStart.OptInPayload(new GateContext(services, new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = "s1", ["cwd"] = Cwd(f) }, "x"), Cwd(f)) ?? string.Empty;
        lines.Add($"after\topt-in\tpayload\t{payload.Length}");
        File.WriteAllLines(path, lines);
    }
}
