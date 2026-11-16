using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.Core.Time;
using static Ah.Mesh.Tests.MailDoorbellTests;

namespace Ah.Mesh.Tests;

/// <summary>Mail a relay carries is handed over once: the doorbell neither rings for it nor pulls it, until the relay has plainly not landed.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailDoorbellRelayTests
{
    private const double Later = T0 + 61_000;

    private static Rig WithClaim(string state, string carrier = "relay", double at = T0)
    {
        var rig = new Rig();
        rig.Add("e1", T0);
        var id = rig.Full("e1");
        var iso = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)at));
        var claim = new JsonObject
        {
            ["v"] = 1d,
            ["id"] = id,
            ["state"] = state,
            ["carrier"] = carrier,
            ["from"] = "someone",
            ["to"] = "orchestrator@tm",
            ["sender_session"] = "s-a",
            ["receiver_session"] = Session,
            ["l0_ms"] = (double)CarrierSettings.DefaultRelayLeaseMs,
            ["l1_ms"] = (double)CarrierSettings.DefaultClaimLeaseMs,
            ["created"] = iso,
            ["at"] = iso,
        };
        Directory.CreateDirectory(rig.Dir("orchestrator") + "/claims");
        File.WriteAllText($"{rig.Dir("orchestrator")}/claims/{id}.json", JsonText.Stringify(claim, 0));
        return rig;
    }

    private static void ClockAt(Rig rig, double ms) => rig.Fx.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);

    [Fact]
    public void AReportARelaySentNeitherRingsNorPulls()
    {
        var rig = WithClaim(ClaimStates.RelaySent);

        Assert.Null(rig.Decide(T0 + 20_000).Ring);
        Assert.True(rig.Pull(T0 + 20_000).Empty);
        Assert.False(rig.Acked("e1"));
    }

    [Theory]
    [InlineData(ClaimStates.Pending)]
    [InlineData(ClaimStates.RelayClaimed)]
    public void AReportARelayIsStillCarryingNeitherRingsNorPulls(string state)
    {
        var rig = WithClaim(state);

        Assert.Null(rig.Decide(T0 + 20_000).Ring);
        Assert.True(rig.Pull(T0 + 20_000).Empty);
    }

    [Fact]
    public void AnIdleSessionIsToldWhenTheHiddenReportComesBack()
    {
        var rig = WithClaim(ClaimStates.RelaySent);

        Assert.Equal(T0 + IMailCore.ResubmitMs, rig.Decide(T0 + 20_000).NextDeadlineMs);
    }

    [Fact]
    public void NegativeARelaySentReportAnIdleSessionDidNotTakeRingsAfterTheShortHold()
    {
        var rig = WithClaim(ClaimStates.RelaySent);

        Assert.NotNull(rig.Decide(Later).Ring);
    }

    [Fact]
    public void ABusySessionKeepsTheLongHoldForARelayThatDrainsAtItsStop()
    {
        var rig = WithClaim(ClaimStates.RelaySent);

        Assert.Null(rig.Decide(Later, busyAt: Later - 1000, carrier: DoorbellCarrier.Tool).Ring);
        Assert.True(rig.Pull(Later).Empty);
    }

    [Fact]
    public void NegativeARelaySentReportStillUnreadPastTheLongHoldIsPulled()
    {
        var rig = WithClaim(ClaimStates.RelaySent);

        Assert.False(rig.Pull(T0 + IMailCore.DoorbellRemindMs + 1).Empty);
        Assert.True(rig.Acked("e1"));
    }

    [Fact]
    public void NegativeAFailedRelayIsPulledAtOnce()
    {
        var rig = WithClaim(ClaimStates.RelayFailed);

        Assert.False(rig.Pull(T0 + 1000).Empty);
    }

    [Fact]
    public void NegativeARelayNeverStartedIsPulledOnceItsLeaseRunsOut()
    {
        var rig = WithClaim(ClaimStates.Pending);
        var past = T0 + CarrierSettings.DefaultRelayLeaseMs + 1;
        ClockAt(rig, past);

        Assert.False(rig.Pull(past).Empty);
    }

    [Fact]
    public void NegativeMailThePluginCarriesIsPulledAsBefore()
    {
        var rig = WithClaim(ClaimStates.Pending, carrier: "prompt");

        Assert.False(rig.Pull(T0 + 1000).Empty);
        Assert.Equal(ClaimStates.Pending, rig.Fx.Mailbox.ReadClaim(rig.Dir("orchestrator"), rig.Full("e1"))!.Get("state")!.Value.AsString());
    }

    [Fact]
    public void NegativeMailWithNoClaimIsPulledAsBefore()
    {
        var rig = new Rig();
        rig.Add("e1", T0);

        Assert.False(rig.Pull(T0 + 1000).Empty);
    }
}
