using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Platform;
using Ah.Roster;

namespace Ah.Hooks.Tests;

/// <summary>
/// Whose team a StopFailure answers for: the hook's parent process (the harness). <c>CLAUDE_PID</c> can only veto that parent,
/// never name another process as the Orchestrator.
/// </summary>
public sealed class StopFailureParentTests
{
    private const int Parent = 4242;

    private const int Other = 7777;

    /// <summary>A daemon whose health lists one session among those whose timers it owns.</summary>
    private sealed class OwningDaemon(string session) : IDaemonClient
    {
        public Task<DaemonInfo?> DiscoverAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Task.FromResult<DaemonInfo?>(null);

        public Task<DaemonInfo?> EnsureDaemonAsync(TimeSpan budget, CancellationToken cancellationToken = default) => Task.FromResult<DaemonInfo?>(null);

        public Task<DaemonReply> RequestAsync(DaemonRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DaemonReply(200, Encoding.UTF8.GetBytes("{\"owns\":{\"timers\":[\"" + session + "\"]}}")));

        public Task DrainAsync(DaemonInfo daemon, string reason, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void RecordFallback(string verb, string reason)
        {
        }
    }

    /// <summary>Registers <c>sf-1</c> with one orchestrator box and, when <paramref name="pulling"/>, leaves a heartbeat in it as a mod pulling the session's mail does.</summary>
    private static IMailbox MailboxWithRegistration(HooksHostTests.Rig rig, bool pulling)
    {
        var mailbox = BindingAttestationTests.MailboxFor(rig);
        var box = rig.Sandbox.Root + "/box";
        _ = Directory.CreateDirectory(box + "/adapters");
        var registration = mailbox.RegistrationFile("sf-1");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(registration)!);
        File.WriteAllText(registration, "{\"address\":\"orchestrator@tm\",\"session_id\":\"sf-1\",\"boxes\":{\"orchestrator@tm\":\"" + box + "\"}}");
        if (pulling)
        {
            var at = rig.Services.Clock.UtcNow.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            File.WriteAllText(box + "/adapters/sf-1.json", "{\"at\":\"" + at + "\",\"session_id\":\"sf-1\"}");
        }

        return mailbox;
    }

    private static HookOutcome Run(int orchestratorPid, string? claudePid, IDaemonClient? daemon = null, string error = "authentication_failed", bool? pulling = null)
    {
        var rig = new HooksHostTests.Rig(claudePid is null ? null : new Dictionary<string, string> { ["CLAUDE_PID"] = claudePid });
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        _ = Directory.CreateDirectory(dir + "/teams");
        File.WriteAllText(dir + "/teams/tm.json", $$"""{"team_id":"t-tm","orchestrator":{"pid":{{orchestratorPid}},"pid_start":"{{HarnessSessions.Start}}"},"members":[]}""");
        var recovery = HierarchyModule.CreateRecovery(rig.Services.Files, rig.Services.Environment, rig.Services.Clock);
        var roster = RosterModule.CreateCore(rig.Services.Files, rig.Services.Environment, rig.Services.Config, PlatformModule.CreateIo(null).Processes, new HarnessSessions().Table);
        var services = rig.Services with { Roster = roster, Advisory = new AdvisoryServices(pulling is null ? null! : MailboxWithRegistration(rig, pulling.Value), null!, null!, null!, null!, null!, null!, recovery, daemon!, null!, null!) };
        var json = $$"""{"hook_event_name":"StopFailure","session_id":"sf-1","cwd":"{{rig.Sandbox.Root}}/proj","error":"{{error}}"}""";
        var outcome = rig.Run("stopfailure-recovery", json, services: () => services);
        Assert.Empty(rig.Errors.Records.Select(r => r.Message));
        return outcome;
    }

    /// <summary>The error kind the hook records for a payload of <paramref name="error"/> and <paramref name="details"/>, with <paramref name="transcriptText"/> as the transcript's last API-error message.</summary>
    private static string RecordedKind(string error, string? details = null, string? transcriptText = null)
    {
        var rig = new HooksHostTests.Rig();
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        _ = Directory.CreateDirectory(dir + "/teams");
        var recovery = HierarchyModule.CreateRecovery(rig.Services.Files, rig.Services.Environment, rig.Services.Clock);
        var turnEnds = new TurnEnds();
        var services = rig.Services with { Advisory = new AdvisoryServices(null!, null!, null!, null!, null!, null!, null!, recovery, null!, null!, null!, turnEnds) };
        var fields = new JsonObject { ["hook_event_name"] = "StopFailure", ["session_id"] = "sf-1", ["cwd"] = rig.Sandbox.Root + "/proj", ["error"] = error };
        if (details is not null)
        {
            fields["error_details"] = details;
        }

        if (transcriptText is not null)
        {
            var folder = rig.Sandbox.Home + "/.claude/projects/c";
            _ = Directory.CreateDirectory(folder);
            var line = new JsonObject { ["type"] = "assistant", ["isApiErrorMessage"] = true, ["timestamp"] = "2026-11-04T10:00:00.000Z", ["uuid"] = "u1", ["message"] = new JsonObject { ["content"] = transcriptText } };
            File.WriteAllText(folder + "/s.jsonl", JsonText.Stringify(line, 0) + "\n");
            fields["transcript_path"] = folder + "/s.jsonl";
        }

        _ = rig.Run("stopfailure-recovery", JsonText.Stringify(fields, 0), services: () => services);
        return turnEnds.Seen!.Error!;
    }

    private sealed class TurnEnds : ITurnEndLog
    {
        internal TurnEndRecord? Seen { get; private set; }

        public void Record(TurnEndRecord record) => Seen = record;
    }

    [Fact]
    public void AWeeklyLimitInTheTranscriptIsRecordedAsThatWhateverThePayloadCallsIt() =>
        Assert.Equal("weekly_limit", RecordedKind("rate_limit", transcriptText: "You've hit your weekly limit"));

    [Fact]
    public void AWeeklyLimitInThePayloadsTextIsRecordedAsThat() =>
        Assert.Equal("weekly_limit", RecordedKind("rate_limit", details: "You've hit your weekly limit"));

    [Fact]
    public void NegativeAPlainRateLimitStaysARateLimit() =>
        Assert.Equal("rate_limit", RecordedKind("rate_limit", details: "429 too many requests", transcriptText: "API Error: 429"));

    [Fact]
    public void NegativeATranscriptNamingNoKnownTextDoesNotReplaceThePayloadsKind() =>
        Assert.Equal("server_error", RecordedKind("server_error", transcriptText: "API Error: 500"));

    private static bool Notified(HookOutcome o) => Encoding.UTF8.GetString(o.Stdout.Span).Contains("terminalSequence", StringComparison.Ordinal);

    [Fact]
    public void TheParentThatIsTheTeamsOrchestratorIsNotified()
    {
        Assert.True(Notified(Run(Parent, null)));
    }

    [Fact]
    public void ACLAUDEPIDThatIsTheParentChangesNothing()
    {
        Assert.True(Notified(Run(Parent, Parent.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Fact]
    public void ACLAUDEPIDNamingAnotherTeamsOrchestratorDoesNotMakeThisSessionIt()
    {
        Assert.False(Notified(Run(Other, Other.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    [Theory]
    [InlineData("overloaded")]
    [InlineData("authentication_failed")]
    public void AnOrchestratorWhoseTimersAnotherSessionOwnsIsStillNotifiedWhateverTheError(string error)
    {
        Assert.True(Notified(Run(Parent, null, new OwningDaemon("other-session"), error)));
    }

    [Theory]
    [InlineData("overloaded")]
    [InlineData("authentication_failed")]
    public void AnOrchestratorTheDaemonOwnsAndWhoseMailIsBeingPulledIsNotNotifiedForAnyError(string error)
    {
        Assert.False(Notified(Run(Parent, null, new OwningDaemon("sf-1"), error, pulling: true)));
    }

    [Fact]
    public void AnOrchestratorTheDaemonOwnsButWhoseMailNothingPullsIsStillNotified()
    {
        Assert.True(Notified(Run(Parent, null, new OwningDaemon("sf-1"), "overloaded", pulling: false)));
    }

    [Fact]
    public void ACLAUDEPIDThatIsNotTheParentVetoesTheParent()
    {
        Assert.False(Notified(Run(Parent, Other.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }
}
