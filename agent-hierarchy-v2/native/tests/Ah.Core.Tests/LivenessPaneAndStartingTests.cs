using Ah.Core.Time;

namespace Ah.Core.Tests;

/// <summary>The pane and starting rows of the one-liveness-truth table, over the one judge.</summary>
public sealed class LivenessPaneAndStartingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);

    private static LivenessEvidence Registered(ProcessObservation observation, string? paneWhy = null, int? starting = null) => new()
    {
        Now = Now,
        MaxObservationAge = TimeSpan.FromSeconds(LivenessRules.ObservationAgeSeconds),
        HasRegistration = true,
        RegistrationPid = 100,
        RegistrationStart = "Wed Nov  4 11:00:00 2026",
        Observation = observation,
        ObservedStartUtc = "2026-11-04T11:00:00Z",
        ObservedAt = Now,
        ObserverCanSeeReference = true,
        Cmdline = HarnessCmdline.Harness,
        PaneAgentWhy = paneWhy,
        StartingAgeSeconds = starting,
    };

    [Fact]
    public void ARunningPaneAgentRaisesADeadPidToLive()
    {
        Assert.Equal(LivenessVerdict.ProvenDead, LivenessRules.Judge(Registered(ProcessObservation.Absent)).Verdict);
        Assert.Equal((LivenessVerdict.Live, "pane"), LivenessRules.Judge(Registered(ProcessObservation.Absent, "pane")));
    }

    [Fact]
    public void NoPaneAgentNeverLowersALiveVerdict() =>
        Assert.Equal(LivenessVerdict.Live, LivenessRules.Judge(Registered(ProcessObservation.Present)).Verdict);

    [Fact]
    public void InsideTheStartingWindowADeadVerdictIsUnknown() =>
        Assert.Equal((LivenessVerdict.Unknown, "starting (spawned 5s ago)"), LivenessRules.Judge(Registered(ProcessObservation.Absent, starting: 5)));

    [Fact]
    public void TheStartingWindowDoesNotLowerALiveVerdict() =>
        Assert.Equal(LivenessVerdict.Live, LivenessRules.Judge(Registered(ProcessObservation.Present, starting: 5)).Verdict);

    [Fact]
    public void OutsideTheStartingWindowTheVerdictStands() =>
        Assert.Equal(LivenessVerdict.ProvenDead, LivenessRules.Judge(Registered(ProcessObservation.Absent, starting: null)).Verdict);

    [Fact]
    public void APaneAgentOutranksTheStartingWindow() =>
        Assert.Equal(LivenessVerdict.Live, LivenessRules.Judge(Registered(ProcessObservation.Absent, "pane", 5)).Verdict);

    [Theory]
    [InlineData("working", "claude", true)]
    [InlineData("blocked", null, true)]
    [InlineData("idle", "codex", true)]
    [InlineData("done", "claude", true)]
    [InlineData("idle", "shell", false)]
    [InlineData("working", "SHELL", false)]
    [InlineData("unknown", "claude", false)]
    [InlineData(null, "claude", false)]
    public void APaneShowsAnAgentOnlyWithARunningStatusAndNotAShell(string? status, string? type, bool shows) =>
        Assert.Equal(shows, LivenessRules.PaneShowsAgent(status, type));

    [Fact]
    public void SettleIsTheSameStepForACallerThatHoldsAVerdict()
    {
        Assert.Equal((LivenessVerdict.ProvenDead, "held"), LivenessRules.Settle((LivenessVerdict.ProvenDead, "held"), null, null));
        Assert.Equal((LivenessVerdict.Live, "pane"), LivenessRules.Settle((LivenessVerdict.ProvenDead, "held"), "pane", null));
        Assert.Equal((LivenessVerdict.Unknown, "starting (spawned 9s ago)"), LivenessRules.Settle((LivenessVerdict.Unknown, "held"), null, 9));
    }
}
