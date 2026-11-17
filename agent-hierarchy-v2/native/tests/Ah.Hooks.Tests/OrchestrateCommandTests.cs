using System.Reflection;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;
using Ah.Mesh;

namespace Ah.Hooks.Tests;

/// <summary>
/// <c>/wrangl:orchestrate</c>: the typed line sets or clears the session's Orchestrator flag, the identity text follows the flag across
/// compact, clear and resume, a subagent or a member never has it, and advisor is denied to a flagged session. The store is the real
/// session-roles domain over the sandbox home.
/// </summary>
public sealed class OrchestrateCommandTests
{
    private const string Body = "You are the Chief Orchestrator in an agent hierarchy";

    private const string Other = "Sun Oct 18 09:00:00 2026";

    private const string LeadLine = "If this is cut to a preview, Read the saved file it names, in full";

    private sealed class Callers : IMailCallers
    {
        public bool OrchestratorProcessIsGone(double pid, string? recordedStart) => false;

        public RequestContext RequestContext(string cwd) => new(cwd, cwd, cwd, cwd);

        public IReadOnlyList<CallerBox> ResolveCaller(CallerRequest request) => BindingAttestationTests.Orchestrator;

        public IReadOnlyList<CallerBox> RegisterCaller(CallerRequest request) => BindingAttestationTests.Orchestrator;

        public IReadOnlyList<CallerBox> EnsureRegistered(CallerRequest request) => RegisterCaller(request);

        public IReadOnlyList<CallerBox> CallerBoxes(CallerRequest request) => BindingAttestationTests.Orchestrator;

        public IReadOnlyList<DeciderBox> JoinDeciderBoxes(string cwd, string? sessionId = null) => [];

        public AckIdsResult AckRouted(string dir, IReadOnlyList<string> ids, string by, string? sessionId = null) => throw new NotSupportedException();

        public string? ProcessCommand(int pid) => null;

        public RebindResult RebindOwnedTeams(RebindRequest request) => new([], []);
    }

    internal sealed class Fixture
    {
        private readonly HooksHostTests.Rig _rig;
        private readonly HarnessSessions _sessions = new();

        public Fixture(Dictionary<string, string>? environment = null, bool configured = true, string config = """{"version":1}""")
        {
            var pluginRoot = Path.GetFullPath(Path.Combine(typeof(HooksHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!, ".."));
            _rig = new HooksHostTests.Rig(environment, pluginRoot, processTable: _sessions.Table, bare: true);
            _rig.Writer.Executing = ConfigModule.CreateStateDomains(_rig.Services.Files, _rig.Services.Environment, _rig.Services.Clock);
            var project = _rig.Sandbox.Root + "/proj/.claude/ah2";
            _ = Directory.CreateDirectory(project);
            if (configured)
            {
                File.WriteAllText(project + "/agent-hierarchy.json", config);
            }
            _sessions.Starts[4343] = Other;
            _sessions.Commands[4343] = "claude";
        }

        public HarnessSessions Sessions => _sessions;

        public HooksHostTests.Rig Rig => _rig;

        public GateServices Services(int pid = HarnessSessions.Pid) => _rig.Services with
        {
            Advisory = new AdvisoryServices(BindingAttestationTests.MailboxFor(_rig), null!, new Callers(), null!, null!, new SessionStartGuardTests.NoText(), null!, null!, new BindingAttestationTests.Daemon(), null!, null!),
            Sessions = _sessions,
            Process = _rig.Services.Process with { ParentPid = pid },
            RulesBlock = RulesBlock,
        };

        public Func<string, RulesSubject, string?>? RulesBlock { get; set; }

        private JsonObject Event(string name, string session, Action<JsonObject>? tweak)
        {
            var e = new JsonObject { ["hook_event_name"] = name, ["session_id"] = session, ["cwd"] = _rig.Sandbox.Root + "/proj" };
            tweak?.Invoke(e);
            return e;
        }

        /// <summary>The outcome of the command handler for a typed line, or null when the line is not the command.</summary>
        public HookOutcome? Typed(string prompt, string session = "s1", int pid = HarnessSessions.Pid, Action<JsonObject>? tweak = null) =>
            OrchestrateCommand.Run(new GateContext(Services(pid), Event("UserPromptSubmit", session, e => { e["prompt"] = prompt; tweak?.Invoke(e); }), "userpromptsubmit-peer-tracking"));

        public string PluginRoot => _rig.Services.Environment.Plugin!.Root;

        /// <summary>The prompt hook's text for a prompt.</summary>
        public string Prompt(string prompt, string session = "s1", Action<JsonObject>? tweak = null)
        {
            var services = Services();
            var json = JsonText.Stringify(Event("UserPromptSubmit", session, e => { e["prompt"] = prompt; tweak?.Invoke(e); }), 0);
            return Encoding.UTF8.GetString(_rig.Run("userpromptsubmit-peer-tracking", json, services: () => services).Stdout.Span);
        }

        /// <summary>The implicit opt-in unit's text after a tool call.</summary>
        public string After(string tool, JsonObject input, string session = "s1", string hookEvent = "PostToolUse", Action<JsonObject>? tweak = null)
        {
            var services = Services();
            var json = JsonText.Stringify(Event(hookEvent, session, e => { e["tool_name"] = tool; e["tool_input"] = input; tweak?.Invoke(e); }), 0);
            return Encoding.UTF8.GetString(_rig.Run("posttooluse-implicit-opt-in", json, services: () => services).Stdout.Span);
        }

        /// <summary>Whether a gate that always denies reaches this session through the chain, which is what being in pins.</summary>
        public bool GateDeniesOnMerits(string session, int pid = HarnessSessions.Pid, Action<JsonObject>? tweak = null)
        {
            var services = Services(pid);
            var gate = new Ah.Hooks.Plumbing.GateUnit("pretooluse-sendmessage-response", static _ => Ah.Hooks.Plumbing.HookEmit.Deny("on its merits", "on its merits"), Ah.Hooks.Plumbing.GateClass.Advisory, Matcher: "*");
            var json = JsonText.Stringify(Event("PreToolUse", session, e => { e["tool_name"] = "Bash"; tweak?.Invoke(e); }), 0);
            var outcome = Ah.Hooks.Plumbing.HookHost.Run(Ah.Hooks.Plumbing.HookHost.ChainName, true, Encoding.UTF8.GetBytes(json), _rig.Errors, () => services, [gate]);
            return Encoding.UTF8.GetString(outcome.Stdout.Span).Contains("on its merits", StringComparison.Ordinal);
        }

        public bool In(string session, int pid = HarnessSessions.Pid, Action<JsonObject>? tweak = null) =>
            OrchestratorIdentity.In(new GateContext(Services(pid), Event("UserPromptSubmit", session, tweak), "test"));

        public string Start(string source, string session, int pid = HarnessSessions.Pid, Action<JsonObject>? tweak = null)
        {
            var services = Services(pid);
            var json = JsonText.Stringify(Event("SessionStart", session, e => { e["source"] = source; tweak?.Invoke(e); }), 0);
            return Encoding.UTF8.GetString(_rig.Run("sessionstart", json, services: () => services).Stdout.Span);
        }

        /// <summary>What a SessionStart injects through all four of its units (sessionstart, session-directive, session-index, session-rules), in that order.</summary>
        public string StartAll(string source, string session, int pid = HarnessSessions.Pid, Action<JsonObject>? tweak = null)
        {
            var services = Services(pid);
            var json = JsonText.Stringify(Event("SessionStart", session, e => { e["source"] = source; tweak?.Invoke(e); }), 0);
            return string.Concat(StartUnits.Select(unit => Encoding.UTF8.GetString(_rig.Run(unit, json, services: () => services).Stdout.Span)));
        }

        private static readonly string[] StartUnits = ["sessionstart", "session-directive", "session-index", "session-rules"];

        public HookOutcome Tool(string tool, string session, Action<JsonObject>? tweak = null)
        {
            var json = JsonText.Stringify(Event("PreToolUse", session, e => { e["tool_name"] = tool; tweak?.Invoke(e); }), 0);
            return _rig.Run("pretooluse-orchestrator-advisor-gate", json, gate: true);
        }

        public bool Flagged(string session) => _rig.Services.Config.ReadOrchestratorFlag(session) is { IsMap: true };

        public JsonValue? Entry(string session) => _rig.Services.Config.ReadOrchestratorFlag(session) is { IsMap: true } entry ? (JsonValue?)entry : null;
    }

    private static string Text(HookOutcome? outcome) => Encoding.UTF8.GetString(outcome!.Stdout.Span);

    private static bool Blocked(HookOutcome? outcome) => Text(outcome).Contains("\"decision\":\"block\"", StringComparison.Ordinal);

    // ---------------------------------------------------------------- the predicate, one row per line

    private static void Tree(Fixture f, string text) => File.WriteAllText(f.Rig.Sandbox.Root + "/proj/.claude/ah2/agent-hierarchy.json", text);

    internal static void OwnedTeam(Fixture f, string session, int pid, string start)
    {
        var path = f.Rig.Services.Roster.TeamPath(f.Rig.Services.Config.HierarchyDir(f.Rig.Sandbox.Root + "/proj"), "tm");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, $$"""{"version":1,"team_id":"t1","orchestrator":{"session_id":"{{session}}","pid":{{pid}},"pid_start":"{{start}}"},"members":[]}""");
    }

    [Theory]
    [InlineData("wrangl:orchestrator", true)]
    [InlineData("wrangl:architect", false)]
    [InlineData("wrangl:sub-orchestrator", false)]
    [InlineData("foo", false)]
    public void Row1ATopLevelAgentIsInOnlyAsTheOrchestrator(string agent, bool expected)
    {
        var f = new Fixture();

        Assert.Equal(expected, f.In("s1", tweak: e => e["agent_type"] = agent));
    }

    [Fact]
    public void ASubagentOfALaunchedOrchestratorThatOwnsNoTeamIsIn()
    {
        var f = new Fixture();

        f.Start("startup", "s1", tweak: e => e["agent_type"] = "wrangl:orchestrator");

        Assert.True(f.In("s1", tweak: e => { e["agent_id"] = "a1"; e["agent_type"] = "general-purpose"; }));
    }

    /// <summary>Negative: without that start the same subagent payload is bare, and a start as another role records nothing for it.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("wrangl:architect")]
    public void ASubagentOfAnythingElseIsNotIn(string? startedAs)
    {
        var f = new Fixture();
        if (startedAs is not null)
        {
            f.Start("startup", "s1", tweak: e => e["agent_type"] = startedAs);
        }

        Assert.False(f.In("s1", tweak: e => { e["agent_id"] = "a1"; e["agent_type"] = "general-purpose"; }));
    }

    // ---------------------------------------------------------------- every identified arm stays gated: a deny on its merits reaches it

    [Theory]
    [InlineData("AH_MEMBER_NAME")]
    [InlineData("WRANGL_TEAM_FILE")]
    public void EitherMemberVariableAloneStaysGated(string variable)
    {
        Assert.True(new Fixture(new Dictionary<string, string> { [variable] = "x" }).GateDeniesOnMerits("s1"));
    }

    [Fact]
    public void ASubagentOfALaunchedOrchestratorStaysGated()
    {
        var f = new Fixture();
        f.Start("startup", "s1", tweak: e => e["agent_type"] = "wrangl:orchestrator");

        Assert.True(f.GateDeniesOnMerits("s1", tweak: e => { e["agent_id"] = "a1"; e["agent_type"] = "general-purpose"; }));
    }

    [Theory]
    [InlineData("orchestrator", true)]
    [InlineData("implementor", false)]
    public void OnlyAStoredOrchestratorRoleIdentifiesASessionWithNoFlag(string role, bool gated)
    {
        var f = new Fixture();
        Ah.TestKit.TestHomeDocs.Put(f.Rig.Sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles, "{\"version\":1,\"sessions\":{\"s1\":{\"role\":\"" + role + "\",\"at\":\"2026-01-01T00:00:00.000Z\"}}}");

        Assert.Equal(gated, f.GateDeniesOnMerits("s1"));
    }

    [Fact]
    public void AnOwnerWithNoFlagStaysGatedByProcess()
    {
        var f = new Fixture();
        OwnedTeam(f, "old", HarnessSessions.Pid, HarnessSessions.Start);

        Assert.True(f.GateDeniesOnMerits("s-after-clear"));
        Assert.False(f.Flagged("s-after-clear"));
    }

    [Fact]
    public void AnOwnerWithNoFlagStaysGatedByRecord()
    {
        var f = new Fixture();
        OwnedTeam(f, "s1", 999, "gone");

        Assert.True(f.GateDeniesOnMerits("s1"));
    }

    /// <summary>The decline rules out the registration arm, so what is left is the box.</summary>
    [Fact]
    public void AnOwnerWhoDeclinedStaysGatedByItsOrchestratorBox()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrator off");
        BindingAttestationTests.Register(BindingAttestationTests.MailboxFor(f.Rig), "s1", HarnessSessions.Pid, HarnessSessions.Start);

        Assert.True(f.GateDeniesOnMerits("s1"));
    }

    [Fact]
    public void AnOwnerWhoDeclinedStaysGatedByRecord()
    {
        var f = new Fixture();
        OwnedTeam(f, "s1", 999, "gone");
        _ = f.Typed("/wrangl:orchestrator off");

        Assert.True(f.GateDeniesOnMerits("s1"));
    }

    [Fact]
    public void ASessionOnlyRegisteredStaysGated()
    {
        var f = new Fixture();
        BindingAttestationTests.Register(BindingAttestationTests.MailboxFor(f.Rig), "s1", HarnessSessions.Pid, HarnessSessions.Start, "rev@tm");

        Assert.True(f.GateDeniesOnMerits("s1"));
    }

    /// <summary>Negative: nothing identifies the session, or its only arm is a member box it declined, so the same gate never reaches it.</summary>
    [Fact]
    public void TheSameGateDoesNotReachABareOrADeclinedMemberBoxSession()
    {
        var bare = new Fixture();
        var declined = new Fixture();
        _ = declined.Typed("/wrangl:orchestrator off");
        BindingAttestationTests.Register(BindingAttestationTests.MailboxFor(declined.Rig), "s1", HarnessSessions.Pid, HarnessSessions.Start, "rev@tm");

        Assert.False(bare.GateDeniesOnMerits("s1"));
        Assert.False(declined.GateDeniesOnMerits("s1"));
    }

    [Fact]
    public void ARegisteredSessionIsIn()
    {
        var f = new Fixture();
        Assert.False(f.In("s1"));

        BindingAttestationTests.Register(BindingAttestationTests.MailboxFor(f.Rig), "s1", HarnessSessions.Pid, HarnessSessions.Start, "rev@tm");

        Assert.True(f.In("s1"));
    }

    /// <summary>Negative: a registration of another session does not identify this one.</summary>
    [Fact]
    public void AnotherSessionsRegistrationDoesNotCount()
    {
        var f = new Fixture();

        BindingAttestationTests.Register(BindingAttestationTests.MailboxFor(f.Rig), "other", HarnessSessions.Pid, HarnessSessions.Start, "rev@tm");

        Assert.False(f.In("s1"));
    }

    private static void RoleTeam(Fixture f)
    {
        var path = f.Rig.Services.Roster.TeamPath(f.Rig.Services.Config.HierarchyDir(f.Rig.Sandbox.Root + "/proj"), "tm");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"version":1,"team_id":"t1","members":[{"name":"tm-architect","role":"architect","route":"peer"}]}""");
    }

    [Fact]
    public void AHandLaunchedRoleSessionAttributedToATeamGetsItsNotice()
    {
        var f = new Fixture();
        RoleTeam(f);

        Assert.NotEqual(string.Empty, f.Start("startup", "s1", tweak: e => e["agent_type"] = "wrangl:architect"));
    }

    /// <summary>Negative: the same role with no team claiming it is bare, and a team of another role does not claim it.</summary>
    [Theory]
    [InlineData(false, "wrangl:architect")]
    [InlineData(true, "wrangl:reviewer")]
    public void ARoleSessionNoTeamClaimsGetsNothing(bool team, string agent)
    {
        var f = new Fixture();
        if (team)
        {
            RoleTeam(f);
        }

        Assert.Equal(string.Empty, f.Start("startup", "s1", tweak: e => e["agent_type"] = agent));
    }

    [Fact]
    public void Row2ATeamFileMemberIsIn()
    {
        Assert.True(new Fixture(new Dictionary<string, string> { ["WRANGL_TEAM_FILE"] = "/x/t.json" }).In("s1"));
    }

    [Fact]
    public void Row3TheOwnerOfATeamIsInByProcessWithANewSessionId()
    {
        var f = new Fixture();
        OwnedTeam(f, "old", HarnessSessions.Pid, HarnessSessions.Start);

        Assert.True(f.In("s-after-clear"));
    }

    [Fact]
    public void Row3TheOwnerOfATeamIsInByTheRecordedSessionIdInANewProcess()
    {
        var f = new Fixture();
        OwnedTeam(f, "s1", 999, "gone");

        Assert.True(f.In("s1"));
        Assert.False(f.In("s2", pid: 4343));
    }

    [Fact]
    public void Row3OwningATeamBeatsADecline()
    {
        var f = new Fixture();
        OwnedTeam(f, "s1", 999, "gone");
        _ = f.Typed("/wrangl:orchestrate off");

        Assert.True(f.In("s1"));
    }

    [Fact]
    public void Row4ADeclinedSessionIsOutEvenWithTheSettingOn()
    {
        var f = new Fixture(config: """{"version":1,"alwaysOrchestrator":true}""");
        _ = f.Typed("/wrangl:orchestrate off");

        Assert.False(f.In("s1"));
        Assert.True(f.In("s2"));
    }

    [Fact]
    public void Row5AFlaggedSessionIsInAndItsSubagentToo()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        Assert.True(f.In("s1"));
        Assert.True(f.In("s1", tweak: e => e["agent_id"] = "a1"));
    }

    [Fact]
    public void Row6TheSettingPutsASessionIn()
    {
        Assert.True(new Fixture(config: """{"version":1,"alwaysOrchestrator":true}""").In("s1"));
    }

    [Fact]
    public void Row6ASettingOfFalseOrNotABoolLeavesItOut()
    {
        Assert.False(new Fixture(config: """{"version":1,"alwaysOrchestrator":false}""").In("s1"));
        Assert.False(new Fixture(config: """{"version":1,"alwaysOrchestrator":"true"}""").In("s1"));
    }

    [Fact]
    public void Row7AnythingElseIsOutAndSoIsItsSubagent()
    {
        var f = new Fixture();

        Assert.False(f.In("s1"));
        Assert.False(f.In("s1", tweak: e => e["agent_id"] = "a1"));
    }

    [Fact]
    public void AUserFileHoldingOnlyTheSettingDoesNotConfigureARepoAndTheProjectOverridesIt()
    {
        var f = new Fixture(configured: false);
        var path = f.Rig.Services.Config.UserConfigPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """{"version":1,"alwaysOrchestrator":true}""");

        var resolved = f.Rig.Services.Config.ResolveConfig(f.Rig.Sandbox.Root + "/proj");
        Assert.False(resolved.Get("configured") is { IsTruthy: true });
        Assert.True(resolved.Get("alwaysOrchestrator") is { IsTruthy: true });

        Tree(f, """{"version":1,"alwaysOrchestrator":false}""");
        Assert.Null(f.Rig.Services.Config.ResolveConfig(f.Rig.Sandbox.Root + "/proj").Get("alwaysOrchestrator"));
    }

    // ---------------------------------------------------------------- the command

    [Fact]
    public void OnFlagsTheSessionWithItsProcessAndReturnsTheIdentityForThisTurn()
    {
        var f = new Fixture();

        var outcome = f.Typed("/wrangl:orchestrate");

        Assert.False(Blocked(outcome), Text(outcome) + string.Join('\n', f.Rig.Errors.Records.Select(r => r.Message + r.Stack)));
        Assert.Contains("this session is now the Orchestrator", Text(outcome), StringComparison.Ordinal);
        Assert.Contains(Body, Text(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain(LeadLine, Text(outcome), StringComparison.Ordinal);
        Assert.Contains("ah CLI root", Text(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain("disallowedTools", Text(outcome), StringComparison.Ordinal);
        Assert.Equal(HarnessSessions.Pid, f.Entry("s1")!.Value.Member("pid")!.Value.AsNumber());
        Assert.Equal(HarnessSessions.Start, f.Entry("s1")!.Value.Member("pid_start")!.Value.AsString());
    }

    /// <summary>The top Orchestrator's sessionstart output under the cap carries no lead line (ah2-68 D6); a Sub-Orchestrator's directive keeps it.</summary>
    [Theory]
    [InlineData("wrangl:orchestrator", false)]
    [InlineData("wrangl:sub-orchestrator", true)]
    [InlineData("wrangl:architect", false)]
    public void ASubOrchestratorDirectiveAtStartCarriesTheLeadLineAndATopOrchestratorUnderTheCapOrARoleNoticeDoesNot(string agent, bool lead)
    {
        var f = new Fixture(new Dictionary<string, string> { ["AH_MEMBER_NAME"] = "m1" });

        var context = f.Start("startup", "s1", tweak: e => e["agent_type"] = agent);

        Assert.Equal(lead, context.Contains(LeadLine, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/wrangl:orchestrator")]
    [InlineData("/wrangl:orchestrate")]
    public void BothNamesRunTheSameHandler(string prompt)
    {
        var f = new Fixture();

        Assert.Contains("this session is now the Orchestrator", Text(f.Typed(prompt)), StringComparison.Ordinal);
        Assert.True(f.Flagged("s1"));
        Assert.Contains("no longer the Orchestrator", Text(f.Typed(prompt + " off")), StringComparison.Ordinal);
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void ASecondOnDoesNotDeliverAgain()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrator");

        var again = Text(f.Typed("/wrangl:orchestrator"));

        Assert.Contains("already on", again, StringComparison.Ordinal);
        Assert.DoesNotContain(Body, again, StringComparison.Ordinal);
    }

    [Fact]
    public void InADisabledRepoOnSaysSoAndWritesNoFlag()
    {
        var f = new Fixture(config: """{"version":1,"enabled":false}""");

        var outcome = f.Typed("/wrangl:orchestrator");

        Assert.False(Blocked(outcome));
        Assert.Contains("hierarchy is off in this repo; run /wrangl:hierarchy on first", Text(outcome), StringComparison.Ordinal);
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void InAnUnconfiguredRepoOnFlagsAndDeliversTheBodyWithoutTheDirective()
    {
        var f = new Fixture(configured: false);

        var outcome = Text(f.Typed("/wrangl:orchestrator"));

        Assert.True(f.Flagged("s1"));
        Assert.Contains(Body, outcome, StringComparison.Ordinal);
        Assert.DoesNotContain(LeadLine, outcome, StringComparison.Ordinal);
    }

    [Fact]
    public void OffDeclinesEvenWhenNotOnAndChangedStillReportsOnlyTheFlag()
    {
        var f = new Fixture();

        var off = Text(f.Typed("/wrangl:orchestrator off"));

        Assert.Contains("was not on", off, StringComparison.Ordinal);
        Assert.Contains("can't be removed", off, StringComparison.Ordinal);
        Assert.DoesNotContain("stays in", off, StringComparison.Ordinal);
        Assert.False(f.In("s1"));
        Assert.True(f.Rig.Services.Config.ReadOrchestratorDeclined("s1"));
    }

    [Fact]
    public void OffOfATeamOwnerSaysItStaysIn()
    {
        var f = new Fixture();
        OwnedTeam(f, "s1", 999, "gone");

        Assert.Contains("this session owns a team and stays in", Text(f.Typed("/wrangl:orchestrator off")), StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitOnClearsTheDeclineAndDelivers()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrator off");

        var on = Text(f.Typed("/wrangl:orchestrator"));

        Assert.Contains(Body, on, StringComparison.Ordinal);
        Assert.True(f.Flagged("s1"));
        Assert.False(f.Rig.Services.Config.ReadOrchestratorDeclined("s1"));
    }


    [Fact]
    public void OnAgainIsANoOpThatRecordsNothingMore()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");
        var calls = f.Rig.Writer.Calls.Count;

        var outcome = f.Typed("/wrangl:orchestrate");

        Assert.Contains("already on", Text(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain(Body, Text(outcome), StringComparison.Ordinal);
        Assert.Equal(calls, f.Rig.Writer.Calls.Count);
    }

    [Fact]
    public void OffClearsTheFlagAndOffWhenNotOnSaysSo()
    {
        var f = new Fixture();
        Assert.Contains("was not on", Text(f.Typed("/wrangl:orchestrate off")), StringComparison.Ordinal);
        _ = f.Typed("/wrangl:orchestrate");

        var outcome = f.Typed("/wrangl:orchestrate off");

        Assert.Contains("no longer the Orchestrator", Text(outcome), StringComparison.Ordinal);
        Assert.False(f.Flagged("s1"));
    }

    [Theory]
    [InlineData("please run /wrangl:orchestrate now")]
    [InlineData("/wrangl:orchestratefoo")]
    [InlineData("/orchestrate")]
    [InlineData("hello")]
    public void ALineThatIsNotTheCommandIsNotRecognisedAndChangesNothing(string prompt)
    {
        var f = new Fixture();

        Assert.Null(f.Typed(prompt));

        Assert.False(f.Flagged("s1"));
    }

    [Theory]
    [InlineData("/wrangl:orchestrate on")]
    [InlineData("/wrangl:orchestrate off now")]
    [InlineData("/wrangl:orchestrate --force")]
    public void AnArgumentOtherThanOffIsRefusedWithTheUsageAndChangesNothing(string prompt)
    {
        var f = new Fixture();

        var outcome = f.Typed(prompt);

        Assert.True(Blocked(outcome));
        Assert.Contains("usage: /wrangl:orchestrate [off]", Text(outcome), StringComparison.Ordinal);
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void TheUserPromptSubmitUnitRunsTheCommand()
    {
        var f = new Fixture();
        var services = f.Services();
        var json = JsonText.Stringify(new JsonObject { ["hook_event_name"] = "UserPromptSubmit", ["session_id"] = "s1", ["cwd"] = f.Rig.Sandbox.Root + "/proj", ["prompt"] = "/wrangl:orchestrate" }, 0);

        var outcome = f.Rig.Run("userpromptsubmit-peer-tracking", json, services: () => services);

        Assert.Contains("this session is now the Orchestrator", Encoding.UTF8.GetString(outcome.Stdout.Span), StringComparison.Ordinal);
        Assert.True(f.Flagged("s1"));
    }

    [Fact]
    public void AMemberSessionIsRefusedAndTheStoreIsUnchanged()
    {
        var f = new Fixture();

        var byRole = f.Typed("/wrangl:orchestrate", tweak: e => e["agent_type"] = "wrangl:implementor");

        Assert.True(Blocked(byRole));
        Assert.False(f.Flagged("s1"));
        Assert.Empty(f.Rig.Writer.Calls);
    }

    [Fact]
    public void ASessionResolvingToATeamMemberRowIsRefused()
    {
        var f = new Fixture(new Dictionary<string, string> { ["WRANGL_TEAM_FILE"] = "/somewhere/team.json" });

        var outcome = f.Typed("/wrangl:orchestrate");

        Assert.True(Blocked(outcome));
        Assert.False(f.Flagged("s1"));
        Assert.Empty(f.Rig.Writer.Calls);
    }

    [Fact]
    public void AnotherAgentSessionAndASubagentAreRefused()
    {
        var f = new Fixture();

        var other = f.Typed("/wrangl:orchestrate", tweak: e => e["agent_type"] = "Explore");
        var subagent = f.Typed("/wrangl:orchestrate", tweak: e => e["agent_id"] = "a1");

        Assert.True(Blocked(other));
        Assert.True(Blocked(subagent));
        Assert.False(f.Flagged("s1"));
    }

    [Fact]
    public void ARequestWithoutASessionIdIsRefused()
    {
        var f = new Fixture();

        var outcome = f.Typed("/wrangl:orchestrate", session: " ");

        Assert.True(Blocked(outcome));
        Assert.Empty(f.Rig.Writer.Calls);
    }

    [Fact]
    public void AnAgentOrchestratorIsAlreadyTheOrchestratorAndCannotTurnItOff()
    {
        var f = new Fixture();

        var on = f.Typed("/wrangl:orchestrate", tweak: e => e["agent_type"] = "wrangl:orchestrator");
        var off = f.Typed("/wrangl:orchestrate off", tweak: e => e["agent_type"] = "wrangl:orchestrator");

        Assert.False(Blocked(on));
        Assert.Contains("already the Orchestrator", Text(on), StringComparison.Ordinal);
        Assert.DoesNotContain(Body, Text(on), StringComparison.Ordinal);
        Assert.True(Blocked(off));
        Assert.False(f.Flagged("s1"));
        Assert.Empty(f.Rig.Writer.Calls);
    }

    [Fact]
    public void ASessionWhoseProcessCannotBeIdentifiedIsRefusedAndNotFlagged()
    {
        var f = new Fixture();
        f.Sessions.Starts.Remove(HarnessSessions.Pid);

        var outcome = f.Typed("/wrangl:orchestrate");

        Assert.True(Blocked(outcome));
        Assert.False(f.Flagged("s1"));
    }

    // ---------------------------------------------------------------- the identity text

    [Fact]
    public void CompactKeepsTheIdentityAndAnUnflaggedSessionGetsNone()
    {
        var f = new Fixture();
        Assert.DoesNotContain(Body, f.StartAll("compact", "s1"), StringComparison.Ordinal);
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("compact", "s1");

        Assert.Contains(Body, context, StringComparison.Ordinal);
        Assert.DoesNotContain("disallowedTools", context, StringComparison.Ordinal);
    }

    [Fact]
    public void ClearMovesTheFlagToTheNewSessionOfTheSameProcessAndInjectsTheIdentity()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("clear", "s2");

        Assert.Contains(Body, context, StringComparison.Ordinal);
        Assert.True(f.Flagged("s2"));
        Assert.False(f.Flagged("s1"));
        var again = f.StartAll("clear", "s3");
        Assert.Contains(Body, again, StringComparison.Ordinal);
        Assert.True(f.Flagged("s3"));
        Assert.False(f.Flagged("s2"));
    }

    [Theory]
    [InlineData(HarnessSessions.Pid, true)]
    [InlineData(4343, false)]
    public void ClearCarriesADeclineOnlyInTheSameProcess(int pid, bool carried)
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");
        _ = f.Typed("/wrangl:orchestrate off");

        _ = f.StartAll("clear", "s2", pid: pid);

        Assert.Equal(carried, f.Rig.Services.Config.ReadOrchestratorDeclined("s2"));
        Assert.Equal(!carried, f.Rig.Services.Config.ReadOrchestratorDeclined("s1"));
    }

    [Fact]
    public void ClearInAnotherProcessMovesNothingAndGetsNoIdentity()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("clear", "s2", pid: 4343);

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
        Assert.False(f.Flagged("s2"));
        Assert.True(f.Flagged("s1"));
    }

    [Fact]
    public void ClearByAReusedPidWithAnotherStartTimeMovesNothing()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");
        f.Sessions.Starts[HarnessSessions.Pid] = Other;

        var context = f.StartAll("clear", "s2");

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
        Assert.False(f.Flagged("s2"));
        Assert.True(f.Flagged("s1"));
    }

    [Fact]
    public void ClearOfAnUnflaggedSessionMigratesAndInjectsNothing()
    {
        var f = new Fixture();

        var context = f.StartAll("clear", "s2");

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
        Assert.False(f.Flagged("s2"));
    }

    [Fact]
    public void OnlyClearMovesAFlagAStartupInAnotherProcessDoesNot()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("startup", "s2", pid: 4343);

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
        Assert.False(f.Flagged("s2"));
        Assert.True(f.Flagged("s1"));
    }

    [Fact]
    public void ResumeWithTheSameIdCarriesTheFlagAndTheNewProcessOwnsIt()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("resume", "s1", pid: 4343);

        Assert.Contains(Body, context, StringComparison.Ordinal);
        Assert.Equal(4343, f.Entry("s1")!.Value.Member("pid")!.Value.AsNumber());
        Assert.Equal(Other, f.Entry("s1")!.Value.Member("pid_start")!.Value.AsString());
    }

    [Fact]
    public void ResumeWithAnotherIdIsNotFlagged()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("resume", "s9", pid: 4343);

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
        Assert.False(f.Flagged("s9"));
    }

    [Fact]
    public void AForkOfAFlaggedSessionIsNotFlaggedAndGetsNoIdentity()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("fork", "s2");

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
        Assert.False(f.Flagged("s2"));
    }

    [Fact]
    public void ASubagentOfAFlaggedSessionGetsNoIdentity()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll("compact", "s1", tweak: e => e["agent_id"] = "a1");

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("startup")]
    [InlineData("resume")]
    [InlineData("compact")]
    [InlineData("clear")]
    public void AFlaggedSessionInAnUnconfiguredProjectGetsTheIdentityAloneAndAnUnflaggedOneGetsNothing(string source)
    {
        var f = new Fixture(configured: false);
        Assert.DoesNotContain(Body, f.StartAll(source, "s1"), StringComparison.Ordinal);
        _ = f.Typed("/wrangl:orchestrate");

        var context = f.StartAll(source, "s1");

        Assert.Contains(Body, context, StringComparison.Ordinal);
        Assert.DoesNotContain("HIERARCHY STATE", context, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("startup", true)]
    [InlineData("resume", true)]
    [InlineData("compact", true)]
    [InlineData("clear", true)]
    [InlineData("fork", false)]
    public void AFlaggedSessionWhoseStartEmitsNoOtherContextGetsTheIdentityAlone(string source, bool injected)
    {
        var f = new Fixture(config: """{"version":1,"enabled":false}""");
        var silent = f.StartAll(source, "s1");

        // A disabled repo refuses the command, so the flag is one the session took before the repo was turned off.
        Tree(f, """{"version":1}""");
        _ = f.Typed("/wrangl:orchestrate");
        Tree(f, """{"version":1,"enabled":false}""");

        var context = f.StartAll(source, "s1");

        Assert.Equal(string.Empty, silent);
        Assert.Equal(injected, context.Contains(Body, StringComparison.Ordinal));
        if (injected)
        {
            Assert.DoesNotContain("ah CLI root (v", context, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(string.Empty, context);
        }
    }

    [Fact]
    public void OffStopsTheIdentityAtTheNextCompact()
    {
        var f = new Fixture();
        _ = f.Typed("/wrangl:orchestrate");
        _ = f.Typed("/wrangl:orchestrate off");

        Assert.DoesNotContain(Body, f.StartAll("compact", "s1"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentOrchestratorIsNotGivenTheBodyAgain()
    {
        var f = new Fixture();

        var context = f.StartAll("compact", "s1", tweak: e => e["agent_type"] = "wrangl:orchestrator");

        Assert.DoesNotContain(Body, context, StringComparison.Ordinal);
    }
}
