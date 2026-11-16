using System.Text;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.State;

namespace Ah.Hooks.Tests;

public sealed partial class HooksHostTests
{
    /// <summary>A writer over the real state engine, so the executed-ops ledger the hook's writes meet in production is in play.</summary>
    private sealed class LedgerWriter : IStateWriter
    {
        internal IReadOnlyList<IStateDomain> Domains { get; set; } = [];

        internal Func<string, string, ReadOnlyMemory<byte>, RequestContext, string, TimeSpan, StateResult>? Engine { get; set; }

        /// <summary>Fails every write to <c>asks.jsonl</c> the way the state client does when a hook's time budget is spent.</summary>
        internal bool BudgetSpent { get; set; }

        /// <summary>The budget each write asked for, in order.</summary>
        internal List<TimeSpan?> Budgets { get; } = [];

        public StateResult Invoke(string domain, string op, ReadOnlyMemory<byte> argsJson, RequestContext context, StateCallOptions? options = null)
        {
            Budgets.Add(options?.Budget);
            if (BudgetSpent && Encoding.UTF8.GetString(argsJson.Span).Contains("/asks.jsonl\"", StringComparison.Ordinal))
            {
                return new StateResult(false, default, "the hook's time budget ran out before the write was attempted", "state_timeout");
            }

            return Engine!(domain, op, argsJson, context, options?.OperationId ?? Guid.NewGuid().ToString("D"), TimeSpan.FromSeconds(5));
        }

        public StateUpdateResult Update(string domain, StateUpdatePlan plan, RequestContext context, StateCallOptions? options = null) =>
            throw new NotSupportedException();
    }

    private static (Rig Rig, GateServices Services, string Dir) LedgerAskRig(bool budgetSpent = false)
    {
        var rig = new Rig();
        var s = rig.Services;
        var io = PlatformModule.CreateIo(null);
        var writer = new LedgerWriter { BudgetSpent = budgetSpent };
        var hierarchy = HierarchyModule.Create(new HierarchyEnvironment(
            s.Files, s.Environment, s.Clock, io.Random, io.Processes, s.Config, s.Roster, writer, s.Process, s.Bootstrap));
        writer.Domains = hierarchy.StateDomains;
        writer.Engine = StateModule.CreateEngine(io.Files, io.Writer, io.Appender, io.Locks, s.Clock, hierarchy.StateDomains, 4242);
        Directory.CreateDirectory(ProjOf(rig) + "/.git");
        var dir = s.Config.HierarchyDir(ProjOf(rig));
        var services = s with
        {
            Hierarchy = hierarchy,
            State = writer,
            Advisory = new AdvisoryServices(BindingAttestationTests.MailboxFor(rig), null!, new DeciderCallers(true, dir), null!, null!, null!, null!, null!, null!, null!, null!),
        };
        return (rig, services, dir);
    }

    private const string ConsentQuestion = ConfigModule.MemberLaunchConsentQuestion;

    private const string ConsentAllow = "Allow";

    private const string ConsentNotNow = "Not now";

    private static string ConsentEvent(Rig rig, string hookEvent, string answer)
    {
        var tool = new JsonObject
        {
            ["questions"] = new JsonArray
            {
                new JsonObject
                {
                    ["question"] = ConsentQuestion,
                    ["header"] = "Member rules",
                    ["multiSelect"] = false,
                    ["options"] = new JsonArray
                    {
                        new JsonObject { ["label"] = ConsentAllow },
                        new JsonObject { ["label"] = ConsentNotNow },
                    },
                },
            },
            ["metadata"] = new JsonObject { ["source"] = "ah:R7;consent=member-launch-rules;cv=2" },
        };
        var input = new JsonObject
        {
            ["hook_event_name"] = hookEvent,
            ["session_id"] = "sess-1",
            ["cwd"] = ProjOf(rig),
            ["tool_use_id"] = "toolu_consent",
            ["tool_name"] = "AskUserQuestion",
            ["tool_input"] = tool,
        };
        if (hookEvent == "PostToolUse")
        {
            input["tool_response"] = new JsonObject { ["answers"] = new JsonObject { [ConsentQuestion] = answer } };
        }

        return JsonText.Stringify(input, 0);
    }

    [Fact]
    public void EveryRunLogsOneEntryLineNamingTheCallTheSessionTheCwdAndWhetherTheTagWasSeen()
    {
        var (rig, services, _) = LedgerAskRig();

        _ = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);

        var entry = Assert.Single(rig.Errors.Traces, r => r.Message.StartsWith("trace: ask-record: entry ", StringComparison.Ordinal));
        Assert.Contains("tool_use_id=toolu_consent", entry.Message, StringComparison.Ordinal);
        Assert.Contains("session=sess-1", entry.Message, StringComparison.Ordinal);
        Assert.Contains("cwd=" + ProjOf(rig), entry.Message, StringComparison.Ordinal);
        Assert.Contains("metadata_source=seen", entry.Message, StringComparison.Ordinal);
        Assert.Contains(rig.Errors.Traces, r => r.Message == "trace: ask-record: recorded the answer in 1 directories");
    }

    [Fact]
    public void ARunWhoseSessionIsNoOrchestratorLogsItsEntryAndTheReasonItWroteNothing()
    {
        var (rig, services, dir) = LedgerAskRig();
        services = services with { Advisory = services.Advisory! with { Callers = new DeciderCallers(false, dir) } };

        _ = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);

        Assert.Contains(rig.Errors.Traces, r => r.Message.StartsWith("trace: ask-record: entry ", StringComparison.Ordinal));
        Assert.Contains(rig.Errors.Traces, r => r.Message == "trace: ask-record: exit without recording: the session is not the live Orchestrator of any team");
        Assert.Empty(rig.Errors.Records);
        Assert.False(File.Exists(Path.Combine(dir, "asks.jsonl")));
    }

    [Fact]
    public void TheAnswerWriteAsksForTenSecondsNotTheHookDefault()
    {
        var (rig, services, _) = LedgerAskRig();

        _ = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);

        Assert.Equal(TimeSpan.FromSeconds(10), ((LedgerWriter)services.State).Budgets.First());
    }

    [Fact]
    public void ARunForAnotherToolLogsNothing()
    {
        var (rig, services, _) = LedgerAskRig();
        var other = ConsentEvent(rig, "PostToolUse", ConsentAllow).Replace("AskUserQuestion", "Bash", StringComparison.Ordinal);

        _ = rig.Run("posttooluse-ask-record", other, services: () => services);

        Assert.Empty(rig.Errors.Records);
        Assert.Empty(rig.Errors.Traces);
    }

    [Fact]
    public void AHookThatFiresTwiceForOneToolUseIdRecordsTheAnswerOnce()
    {
        var (rig, services, dir) = LedgerAskRig();

        _ = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);
        _ = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);

        _ = Assert.Single(File.ReadAllLines(Path.Combine(dir, "asks.jsonl")));
    }

    [Fact]
    public void AnAllowAnswerRecordedAfterTheGateAuditedTheSameCallIsEffectiveConsent()
    {
        var (rig, services, dir) = LedgerAskRig();

        _ = rig.Run("pretooluse-conduit-gate", ConsentEvent(rig, "PreToolUse", string.Empty), gate: true, services: () => services);
        _ = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);

        var verdict = ConfigModule.EvaluateMemberLaunchConsent(services.Files, Path.Combine(dir, "asks.jsonl"), enabled: true);
        Assert.True(verdict.Effective, verdict.Reason);
    }

    [Fact]
    public void ANotNowAnswerRecordedAfterTheGateIsNotEffectiveConsent()
    {
        var (rig, services, dir) = LedgerAskRig();

        _ = rig.Run("pretooluse-conduit-gate", ConsentEvent(rig, "PreToolUse", string.Empty), gate: true, services: () => services);
        _ = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentNotNow), services: () => services);

        var verdict = ConfigModule.EvaluateMemberLaunchConsent(services.Files, Path.Combine(dir, "asks.jsonl"), enabled: true);
        Assert.False(verdict.Effective);
        Assert.Equal("declined", verdict.Reason);
    }

    [Fact]
    public void AnAnswerWhoseWriteRanOutOfTimeIsReportedToTheOrchestratorAsNotRecorded()
    {
        var (rig, services, dir) = LedgerAskRig(budgetSpent: true);

        var outcome = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);

        var said = Encoding.UTF8.GetString(outcome.Stdout.Span);
        Assert.Contains("\"additionalContext\"", said, StringComparison.Ordinal);
        Assert.Contains("NOT recorded", said, StringComparison.Ordinal);
        Assert.Contains("\"hookEventName\":\"PostToolUse\"", said, StringComparison.Ordinal);
        Assert.Equal(0, outcome.ExitCode);
        Assert.False(File.Exists(Path.Combine(dir, "asks.jsonl")));
        Assert.NotEmpty(rig.Errors.Records);
        Assert.Contains(rig.Errors.Traces, r => r.Message.StartsWith("trace: ask-record: exit with the answer not recorded", StringComparison.Ordinal));
    }

    [Fact]
    public void ARecordedConsentAnswerTellsTheOrchestratorWhereItWasRecorded()
    {
        var (rig, services, _) = LedgerAskRig();

        var outcome = rig.Run("posttooluse-ask-record", ConsentEvent(rig, "PostToolUse", ConsentAllow), services: () => services);

        Assert.Contains("ah: consent recorded in", System.Text.Encoding.UTF8.GetString(outcome.Stdout.Span), StringComparison.Ordinal);
    }
}
