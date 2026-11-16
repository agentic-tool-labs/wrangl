using System.Reflection;
using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>
/// What every native hook unit must do whatever its body does: fail closed with exit 0 for a gate, stay silent and leave one
/// hook-errors row for an advisory hook, and never exit 70 or 2.
/// </summary>
public sealed partial class HooksHostTests
{
    private sealed class NoMesh : IMeshGate
    {
        public MailboxOutcome<MeshGateVerdict> Check(MeshGateRequest request) => new(null, null);

        public string? LinkEnvelopeFor(RequestContext context, string? sessionId, string? from, double? nowMs = null) => null;

        public void RevokeLinksFor(string teamPath, IReadOnlyList<string>? names)
        {
        }
    }

    internal sealed class RecordingWriter : IStateWriter
    {
        public List<string> Calls { get; } = [];

        public List<string> Arguments { get; } = [];

        public List<string?> OperationIds { get; } = [];

        /// <summary>Calls (as <c>domain.op</c>) that fail instead of succeeding.</summary>
        public HashSet<string> Failing { get; } = [];

        /// <summary>When set, a call to one of these domains runs the op body against the real files instead of only being recorded.</summary>
        public IReadOnlyList<IStateDomain>? Executing { get; set; }

        /// <summary>The home whose database a db-only domain's executed op writes.</summary>
        public string? AhHome { get; set; }

        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
        {
            Calls.Add($"{domain}.{op}");
            Arguments.Add(Encoding.UTF8.GetString(argsJson.Span));
            OperationIds.Add(options?.OperationId);
            if (Executing?.FirstOrDefault(d => d.Name == domain) is { } executing && !Failing.Contains($"{domain}.{op}"))
            {
                var stateOp = executing.Ops.Single(o => o.Name == op);
                var call = new OpCall(op, argsJson, context, options?.OperationId);
                // a db-only domain's write, or an op whose mode key is on the database (the progress logs, per-dir-files-to-db S11c), runs its DbBody
                if ((executing.DbOnly || stateOp.ModeKey is not null) && stateOp.DbBody is { } body && AhHome is { } home)
                {
                    var outcome = Ah.StateDb.StateDbModule.CreateOpRunner(home).Run(new DbOpRequest(stateOp.ModeKey ?? domain, options?.OperationId ?? Guid.NewGuid().ToString(), "direct", tx => body(call, tx), DbOnly: executing.DbOnly));
                    return new StateResult(outcome.Status == DbOpStatus.Done, outcome.ResultJson, outcome.Error, outcome.ErrorCode);
                }

                var done = stateOp.Body(call, new DirectStoreWriteContext());
                return new StateResult(done.Ok, done.ResultJson, done.Error, done.ErrorCode);
            }

            if (Failing.Contains($"{domain}.{op}"))
            {
                return new StateResult(false, default, "refused for the test", "op_failed");
            }

            return new StateResult(true, Responses.TryGetValue($"{domain}.{op}", out var canned) ? Encoding.UTF8.GetBytes(canned) : "{}"u8.ToArray());
        }

        /// <summary>The JSON a call (as <c>domain.op</c>) answers with instead of <c>{}</c>.</summary>
        public Dictionary<string, string> Responses { get; } = [];

        /// <summary>What each update would have written, from running its read and its mutation against the real files; updates that would write nothing are absent.</summary>
        public List<string> Mutations { get; } = [];

        /// <summary>Thrown by every update when set, as a refusing authority would.</summary>
        public Exception? UpdateFailure { get; set; }

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null)
        {
            if (UpdateFailure is not null)
            {
                throw UpdateFailure;
            }

            try
            {
                if (plan.Mutate(plan.Read()) is { } bytes)
                {
                    Mutations.Add(Encoding.UTF8.GetString(bytes.Span));
                }
            }
            catch (Exception)
            {
                // an update the fake cannot evaluate is still answered as written
            }

            return new(true, new StateResult(true, "{}"u8.ToArray()));
        }
    }

    /// <summary>The real libraries over a sandbox, with a state writer that records instead of writing.</summary>
    internal sealed class Rig
    {
        public Rig(Dictionary<string, string>? extraEnvironment = null, string? pluginRoot = null, Func<string, Ah.Core.Json.JsonValue, bool>? memberUp = null, IProcessTable? processTable = null, SessionProofs? proofs = null, PeerRecordsRoute? peerRoute = null, MsgsRoute? msgsRoute = null, bool bare = false)
        {
            Sandbox = UnitSandbox.Create();
            var variables = new Dictionary<string, string>
            {
                ["HOME"] = Sandbox.Home,
                ["AH_HOME"] = Sandbox.Root + "/ah",
                ["AH_TEST"] = "1",
            };
            foreach (var pair in extraEnvironment ?? [])
            {
                variables[pair.Key] = pair.Value;
            }

            Directory.CreateDirectory(Sandbox.Root + "/proj");
            var env = new FakeEnvironment(variables, new PluginInfo(pluginRoot ?? "/ahroot", "wrangl", "0.0.0", "1"));
            var io = PlatformModule.CreateIo(null);
            Errors = new RecordingHookErrorLog();
            var clock = new FakeClock(new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero));
            var facts = new ProcessFacts(Sandbox.Root + "/proj", 4242);
            IRosterCore? roster = null;
            var config = ConfigModule.CreateCore(io.Files, env, () => roster!, facts, docs: Ah.StateDb.StateDbModule.CreateHomeDocs(env.AhHome));

            // A rig's sessions are in, as every session was before opt-in, unless a test stages a bare one. The file holds only a preference, so it configures nothing.
            if (!bare)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(config.UserConfigPath())!);
                File.WriteAllText(config.UserConfigPath(), """{"alwaysOrchestrator":true}""");
            }

            roster = RosterModule.CreateCore(io.Files, env, config, io.Processes, processTable, proofs);
            Writer = new RecordingWriter();
            var bootstrap = HierarchyModule.CreateBootstrap(io.Writer);
            var hierarchy = HierarchyModule.Create(
                new HierarchyEnvironment(io.Files, env, clock, io.Random, io.Processes, config, roster, Writer, facts, bootstrap, null, memberUp is null ? null : (d, m) => memberUp(d, m) ? "live" : "proven_dead", peerRoute, msgsRoute ?? TestMsgStore.SyncedRoute(env.AhHome, Sandbox.Root)) { Activity = Ah.StateDb.StateDbModule.CreateActivity(env.AhHome), Gates = Ah.StateDb.StateDbModule.CreateGates(env.AhHome), QaLogRows = Ah.StateDb.StateDbModule.CreateQaLog(env.AhHome), RosterRecords = Ah.StateDb.StateDbModule.CreateRosterRecords(env.AhHome), Jobs = Ah.StateDb.StateDbModule.CreateJobs(env.AhHome), ProgressLogs = Ah.StateDb.StateDbModule.CreateProgressLogs(env.AhHome), Checkpoints = Ah.StateDb.StateDbModule.CreateCheckpoints(env.AhHome), ProgressDocs = Ah.StateDb.StateDbModule.CreateProgressDocs(env.AhHome) });
            _domains = hierarchy.StateDomains;
            _ahHome = env.AhHome;
            Writer.AhHome = env.AhHome;
            Services = new GateServices(env, clock, io.Files, config, roster, hierarchy, bootstrap, Writer, Errors, facts, new NoMesh(), ConfigModule.CreateConventions(PlatformModule.CreateProcessRunner()), MemberUp: memberUp);
        }

        private readonly IReadOnlyList<IStateDomain> _domains;
        private readonly string _ahHome;

        public UnitSandbox Sandbox { get; }

        /// <summary>Seeds an activity record of <paramref name="subject"/> under <paramref name="dir"/> through <c>activity.put</c>.</summary>
        public void PutActivity(string dir, string subject, string body, long? updatedAtMs = null) => TestActivity.Put(_domains, _ahHome, dir, subject, body, updatedAtMs);

        /// <summary>Appends each JSON line of <paramref name="lines"/> to <paramref name="dir"/>'s gate log through <c>gates.put</c>.</summary>
        public void PutGates(string dir, string lines) => TestGates.Put(_domains, _ahHome, dir, lines);

        /// <summary>Appends each JSON line of <paramref name="lines"/> to <paramref name="dir"/>'s roster through <c>peers.put</c> (per-dir-files-to-db S7).</summary>
        public void PutPeers(string dir, string lines) => TestPeers.Put(_domains, _ahHome, dir, lines);

        /// <summary>The gate rows of <paramref name="dir"/>, one per line, as a <c>gates.jsonl</c> held them.</summary>
        public string GatesText(string dir) => TestGates.Text(_ahHome, dir);

        public RecordingHookErrorLog Errors { get; }

        public RecordingWriter Writer { get; }

        public GateServices Services { get; }

        // the state files a test planted are taken first, as an upgrade's ahd start would take them (a read no longer does, files-off-disk F2b)
        public HookOutcome Run(string name, string eventJson, bool gate = false, Func<GateServices>? services = null)
        {
            TestImport.Dir(_ahHome, Sandbox.Root);
            return HooksModule.Run(new HookRun(name, gate, Encoding.UTF8.GetBytes(eventJson), Errors, services ?? (() => Services)));
        }
    }

    private static string Text(HookOutcome o) => Encoding.UTF8.GetString(o.Stdout.Span);

    private static JsonValue Json(HookOutcome o) => JsonText.Parse(o.Stdout.Span);

    [Fact]
    public void AnUnimplementedGatePassesWithOneRowAndNeverExits70()
    {
        var rig = new Rig();

        var outcome = rig.Run("pretooluse-not-yet-written", "{}", gate: true);

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Equal("ah: hook pretooluse-not-yet-written is not implemented natively (no native implementation)", Assert.Single(rig.Errors.Records).Message);
    }

    [Fact]
    public void AnUnimplementedAdvisoryHookIsSilentWithOneHookErrorsRow()
    {
        var rig = new Rig();

        var outcome = rig.Run("some-future-advisory-hook", "{}");

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.Stdout.IsEmpty);
        var row = Assert.Single(rig.Errors.Records);
        Assert.Equal("ah: hook some-future-advisory-hook is not implemented natively (no native implementation)", row.Message);
    }

    private static readonly string HooksJson = Path.Combine(
        typeof(HooksHostTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "AhNativeRoot").Value!,
        "..",
        "hooks",
        "registry.json");

    /// <summary>The unit names hooks.json registers (the argument of <c>bin/ah-hook</c>), in file order and without repeats.</summary>
    private static IReadOnlyList<string> RegisteredScripts()
    {
        var events = JsonText.Parse(File.ReadAllText(HooksJson)).Member("hooks")!.Value.AsObject();
        return
        [
            .. events.Keys
                .SelectMany(ev => events[ev].AsArray())
                .SelectMany(matcher => matcher.Member("hooks")!.Value.AsArray())
                .Select(h => h.Member("command")!.Value.AsString().Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1])
                .Distinct(StringComparer.Ordinal),
        ];
    }

    [Fact]
    public void EveryScriptHooksJsonRegistersHasANativeUnit()
    {
        var unimplemented = RegisteredScripts().Where(n => !HooksModule.Implements(n)).ToList();

        Assert.Empty(unimplemented);
    }

    [Fact]
    public void EveryAdvisoryUnitRunsToExitZeroAndIsSilentWithoutItsServices()
    {
        var rig = new Rig();
        var advisory = RegisteredScripts().Where(n => !HooksModule.GateNames.Contains(n) && HooksModule.Implements(n)).ToList();

        Assert.NotEmpty(advisory);
        foreach (var name in advisory)
        {
            foreach (var eventJson in new[] { "{}", "null", "[]", """{"hook_event_name":"Stop","session_id":"s","agent_id":"a"}""" })
            {
                var before = rig.Errors.Records.Count;
                var outcome = rig.Run(name, eventJson);

                Assert.Equal(0, outcome.ExitCode);
                Assert.True(outcome.Stdout.IsEmpty, $"{name} wrote to stdout for {eventJson}");
                Assert.True(rig.Errors.Records.Count - before <= 1, $"{name} logged more than one row for {eventJson}");
            }
        }
    }

    [Fact]
    public void AnUnknownNamePassesWithOrWithoutTheGateFlag()
    {
        var rig = new Rig();

        Assert.True(rig.Run("some-future-gate", "{}", gate: true).Stdout.IsEmpty);
        Assert.True(rig.Run("some-future-gate", "{}").Stdout.IsEmpty);
        Assert.Equal(2, rig.Errors.Records.Count);
    }

    [Fact]
    public void AGateThatCannotBuildItsServicesDeniesAndLeavesARow()
    {
        var rig = new Rig();

        var outcome = rig.Run(
            "pretooluse-mail-claim",
            """{"hook_event_name":"PreToolUse","tool_name":"AskUserQuestion"}""",
            services: () => throw new IOException("the home is unreadable"));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("deny", Json(outcome).Member("hookSpecificOutput")!.Value.Member("permissionDecision")!.Value.AsString());
        Assert.Equal("\"the home is unreadable\"", Assert.Single(rig.Errors.Records).Message);
        Assert.Equal("pretooluse-mail-claim", rig.Errors.Records[0].Hook);
    }

    [Fact]
    public void ABusyStoreDeniesAtAClosedGateWithoutCallingATeamFileDamagedAndIsLogged()
    {
        var rig = new Rig();

        var outcome = rig.Run(
            "pretooluse-mail-claim",
            """{"hook_event_name":"PreToolUse","tool_name":"AskUserQuestion"}""",
            services: () => throw new Ah.Core.Json.StoreUnavailableException("the state database could not be read: busy"));

        Assert.Equal(0, outcome.ExitCode);
        Assert.Equal("deny", Json(outcome).Member("hookSpecificOutput")!.Value.Member("permissionDecision")!.Value.AsString());
        Assert.DoesNotContain("team file", Text(outcome), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("busy", Assert.Single(rig.Errors.Records).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeABusyStoreLeavesAnAdvisoryHookSilentAndLogged()
    {
        var rig = new Rig();

        var outcome = rig.Run(
            "userpromptsubmit-peer-tracking",
            "{}",
            services: () => throw new Ah.Core.Json.StoreUnavailableException("the state database could not be read: busy"));

        Assert.Equal(0, outcome.ExitCode);
        Assert.True(outcome.Stdout.IsEmpty);
        Assert.Contains("busy", Assert.Single(rig.Errors.Records).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullEventAndAnEmptyOneFailAGateClosed()
    {
        var rig = new Rig();

        var nullEvent = rig.Run("pretooluse-mail-claim", "null");
        var emptyEvent = rig.Run("pretooluse-mail-claim", string.Empty);

        Assert.Contains("\"permissionDecision\":\"deny\"", Text(nullEvent), StringComparison.Ordinal);
        Assert.Contains("\"permissionDecision\":\"deny\"", Text(emptyEvent), StringComparison.Ordinal);
        Assert.Equal(0, emptyEvent.ExitCode);
    }

    [Fact]
    public void AnUnparseableEventIsLoggedOnceAndDeniesAGate()
    {
        var rig = new Rig();

        var outcome = rig.Run("pretooluse-mail-claim", "{not json");

        Assert.Contains("\"permissionDecision\":\"deny\"", Text(outcome), StringComparison.Ordinal);
        Assert.DoesNotContain("not json", Text(outcome), StringComparison.Ordinal);
        Assert.Single(rig.Errors.Records);
    }

    [Fact]
    public void NoHookBodyCanReturnAnExitCodeOtherThanZero()
    {
        var rig = new Rig();
        var events = new[]
        {
            """{"hook_event_name":"PreToolUse","tool_name":"AskUserQuestion","agent_type":"wrangl:architect"}""",
            """{"hook_event_name":"PreToolUse","tool_name":"Bash","tool_input":{"command":"ls"}}""",
            """{"hook_event_name":"PreToolUse","tool_name":"Agent","tool_input":{"subagent_type":"wrangl:architect"}}""",
            "[]",
            "5",
            "\"text\"",
        };

        foreach (var name in HooksModule.GateNames)
        {
            foreach (var e in events)
            {
                Assert.Equal(0, rig.Run(name, e).ExitCode);
            }
        }
    }

    [Fact]
    public void EveryGateScriptIsImplemented()
    {
        var missing = HooksModule.GateNames.Where(n => !HooksModule.Implements(n)).ToList();

        Assert.Empty(missing);
    }

    private static string Reason(HookOutcome o) =>
        Json(o).Member("hookSpecificOutput")!.Value.Member("permissionDecisionReason")!.Value.AsString();
}
