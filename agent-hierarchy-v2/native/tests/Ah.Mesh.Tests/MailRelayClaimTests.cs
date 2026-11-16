using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>
/// The sender side of single-carrier delivery: the SendMessage PreToolUse takes the relay claim or denies the call with the text the
/// model is told, the PostToolUse confirms or fails it, and a relay is never a delivery proof for an entry that has a claim.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailRelayClaimTests
{
    private static RequestContext ContextOf(MailClaimTests.Rig rig) => new(rig.Hierarchy, rig.Hierarchy, string.Empty, string.Empty);

    private static string Relay(string id, string to = "b@tm", string text = "hello") => $"{text} [ah-entry {id} to {to}]";

    private static string State(MailClaimTests.Rig rig, string id, string? box = null) =>
        rig.Fx.Mailbox.ReadClaim(box ?? rig.BoxB, id)!.Get("state")!.Value.AsString();

    private static void Elapse(MailClaimTests.Rig rig, TimeSpan by) => rig.Fx.Clock.UtcNow += by;

    [Fact]
    public void TheSendersSendMessageTakesTheClaimAndRecordsTheHolder()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig));

        Assert.False(decision.Deny, decision.Reason);
        Assert.Equal(["e1"], decision.Claimed);
        var claim = rig.Claim("e1")!;
        Assert.Equal("relay-claimed", claim.Get("state")!.Value.AsString());
        var holder = claim.Get("holder")!.Value;
        Assert.Equal("s-a", holder.Member("session_id")!.Value.AsString());
        Assert.Equal(Environment.ProcessId, holder.Member("pid")!.Value.AsNumber());
        Assert.Equal($"uds:/tmp/cc-socks/{Environment.ProcessId}.sock", holder.Member("socket")!.Value.AsString());
    }

    [Fact]
    public void TheSocketNamesTheRequestsSocketDirectory()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), new RequestContext(rig.Hierarchy, rig.Hierarchy, string.Empty, "/run/socks"));

        Assert.Equal(
            $"uds:/run/socks/{Environment.ProcessId}.sock",
            rig.Claim("e1")!.Get("holder")!.Value.Member("socket")!.Value.AsString());
    }

    [Fact]
    public void ASuccessfulSendMovesTheClaimToRelaySent()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var context = ContextOf(rig);
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);

        var moved = rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Sent, null, context);

        Assert.Equal(["e1"], moved);
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void ASendThatErroredMovesTheClaimToRelayFailedWithTheReason()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var context = ContextOf(rig);
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);

        rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Failed, "no inbox for that session", context);

        Assert.Equal("relay-failed", State(rig, "e1"));
        Assert.Equal("no inbox for that session", rig.Claim("e1")!.Get("reason")!.Value.AsString());
    }

    [Fact]
    public void AnotherSessionsToolResultCannotConfirmOrFailTheClaim()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var context = ContextOf(rig);
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);

        var confirmed = rig.Fx.Hooks.ConfirmRelay("s-other", "b@tm", Relay("e1"), RelayResult.Sent, null, context);
        var failed = rig.Fx.Hooks.ConfirmRelay("s-other", "b@tm", Relay("e1"), RelayResult.Failed, "x", context);

        Assert.Empty(confirmed);
        Assert.Empty(failed);
        Assert.Equal("relay-claimed", State(rig, "e1"));
    }

    [Fact]
    public void ALateRelayAfterTheFallbackClaimIsDeniedWithTheAlreadyDeliveredText()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Elapse(rig, TimeSpan.FromSeconds(181));
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig));

        Assert.True(decision.Deny);
        Assert.Equal("ah: entry e1 was already delivered to b@tm; do not resend it.", decision.Reason);
        Assert.Empty(decision.Claimed);
        Assert.Equal("fallback-claimed", State(rig, "e1"));
    }

    [Fact]
    public void ARelayOfADeliveredEntryIsDeniedWithTheSameText()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var context = ContextOf(rig);
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);
        rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Sent, null, context);
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Deliver);

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);

        Assert.True(decision.Deny);
        Assert.Equal("ah: entry e1 was already delivered to b@tm; do not resend it.", decision.Reason);
    }

    [Fact]
    public void ARelayOfAnEntryAnotherSessionIsDeliveringIsDeniedNamingTheHolder()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var context = ContextOf(rig);
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);

        var decision = rig.Fx.Hooks.ClaimRelay("s-other", "b@tm", Relay("e1"), context);

        Assert.True(decision.Deny);
        Assert.Equal("ah: entry e1 is already being delivered by s-a; do not send it.", decision.Reason);
        Assert.Equal("relay-claimed", State(rig, "e1"));
    }

    [Fact]
    public void ARelayOfAFailedRelayIsDeniedAndAhDeliversItAnotherWay()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var context = ContextOf(rig);
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);
        rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Failed, "no inbox", context);

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), context);

        Assert.True(decision.Deny);
        Assert.Equal("ah: the relay of entry e1 failed; ah is delivering it another way; do not resend it.", decision.Reason);
    }

    [Fact]
    public void TheClaimIsNotLostBeforeTheClaimLeaseHasElapsed()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig));
        Elapse(rig, TimeSpan.FromSeconds(59));

        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        Assert.Equal("relay-claimed", State(rig, "e1"));
    }

    [Fact]
    public void AnEntryNotYetPastTheRelayLeaseIsNotFallenBack()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Elapse(rig, TimeSpan.FromSeconds(179));

        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        Assert.Equal("pending", State(rig, "e1"));
    }

    [Fact]
    public void ACarrierPromptEntryIsDeniedWithThePluginPromptText()
    {
        var rig = MailClaimTests.Create(receiverHarness: "codex");
        rig.Send("e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig));

        Assert.True(decision.Deny);
        Assert.Equal("ah: entry e1 is delivered by the plugin prompt; do not send it by SendMessage.", decision.Reason);
    }

    [Fact]
    public void AMarkerForAnEntryOfAnotherSenderIsDeniedAsNotThisSessionsToRelay()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-b", "b@tm", Relay("e1"), ContextOf(rig));

        Assert.True(decision.Deny);
        Assert.Equal("ah: entry e1 is not this session's to relay; do not send it.", decision.Reason);
        Assert.Equal("pending", State(rig, "e1"));
        Assert.Contains(rig.Fx.Mailbox.ReadAttempts(rig.BoxB), r => r.Get("state")?.AsString() == "relay_denied" && r.Get("reason")?.AsString() == decision.Reason);
    }

    [Fact]
    public void AMessageWithNoMarkerAndNoTokenIsLeftAlone()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", "just saying hello", ContextOf(rig));

        Assert.False(decision.Deny);
        Assert.Empty(decision.Claimed);
        Assert.Equal("pending", State(rig, "e1"));
    }

    [Fact]
    public void AMarkerForAnEntryThatDoesNotExistIsAllowedAndClaimsNothing()
    {
        var rig = MailClaimTests.Create();

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("ghost"), ContextOf(rig));

        Assert.False(decision.Deny);
        Assert.Empty(decision.Claimed);
    }

    [Fact]
    public void AnEntryWithNoClaimRecordIsLeftToTheOldRules()
    {
        var rig = MailClaimTests.Create();
        rig.Fx.Mailbox.AppendEntry(
            rig.BoxB,
            new JsonObject { ["v"] = 1, ["id"] = "old", ["from"] = "a@tm", ["to"] = "b@tm", ["kind"] = "note", ["route"] = "fallback", ["created"] = "2026-01-01T11:00:00.000Z", ["text"] = "t" },
            null);

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("old"), ContextOf(rig));

        Assert.False(decision.Deny);
        Assert.Empty(decision.Claimed);
        Assert.Null(rig.Claim("old"));
    }

    [Fact]
    public void AMessageThatNamesAnUnclaimableEntryTakesNoClaimAtAll()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        rig.Send("e2");
        Elapse(rig, TimeSpan.FromSeconds(181));
        rig.Fx.Mailbox.Claim(rig.BoxB, "e2", ClaimActions.Advance);
        var message = "both [ah-entry e1 to b@tm] [ah-entry e2 to b@tm]";

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", message, ContextOf(rig));

        Assert.True(decision.Deny);
        Assert.Equal("fallback-claimed", State(rig, "e2"));
        Assert.Equal("pending", State(rig, "e1"));
    }

    [Fact]
    public void TwoClaimableMarkersInOneMessageAreBothClaimed()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        rig.Send("e2");

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", "both [ah-entry e1 to b@tm] [ah-entry e2 to b@tm]", ContextOf(rig));

        Assert.False(decision.Deny, decision.Reason);
        Assert.Equal(["e1", "e2"], decision.Claimed);
        Assert.Equal("relay-claimed", State(rig, "e1"));
        Assert.Equal("relay-claimed", State(rig, "e2"));
    }

    [Fact]
    public void AMarkerNamingAnotherAddressThanTheRecipientIsDenied()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "a@tm", Relay("e1"), ContextOf(rig));

        Assert.True(decision.Deny);
        Assert.Equal("ah: entry e1 is addressed to b@tm, not a@tm; do not relay it there.", decision.Reason);
        Assert.Equal("pending", State(rig, "e1"));
    }

    [Fact]
    public void ARelayToTheReceiversSocketAddressIsTheEntrysRecipient()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", $"uds:/tmp/cc-socks/{Environment.ProcessId}.sock", Relay("e1"), ContextOf(rig));

        Assert.False(decision.Deny, decision.Reason);
        Assert.Equal("relay-claimed", State(rig, "e1"));
    }

    [Fact]
    public void ARelayToAnotherSessionsSocketIsNotTheEntrysRecipient()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "uds:/tmp/cc-socks/4242.sock", Relay("e1"), ContextOf(rig));

        Assert.True(decision.Deny);
        Assert.Equal("pending", State(rig, "e1"));
    }

    [Fact]
    public void ABriefIsClaimedByItsRequestTokenWhenTheMarkerIsLeftOut()
    {
        var rig = MailClaimTests.Create();
        Directory.CreateDirectory(rig.Hierarchy + "/msgs");
        var request = rig.Hierarchy + "/msgs/20260101-120000-abcd--b--task--request.md";
        File.WriteAllText(request, "---\nid: 20260101-120000-abcd\n---\n");
        var sent = rig.Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["session_id"] = "s-a", ["id"] = "br1", ["ref"] = request, ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy });
        Assert.True(sent.Ok, sent.Failure?.Message);

        var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", $"[hierarchy-msg {request}]", ContextOf(rig));

        Assert.False(decision.Deny, decision.Reason);
        Assert.Equal(["br1"], decision.Claimed);
        Assert.Equal("relay-claimed", State(rig, "br1"));
    }

    [Fact]
    public void ARequestTokenOfAnotherSenderIsNotClaimedByThisSession()
    {
        var rig = MailClaimTests.Create();
        Directory.CreateDirectory(rig.Hierarchy + "/msgs");
        var request = rig.Hierarchy + "/msgs/20260101-120000-abcd--b--task--request.md";
        File.WriteAllText(request, "---\nid: 20260101-120000-abcd\n---\n");
        rig.Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["session_id"] = "s-a", ["id"] = "br1", ["ref"] = request, ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy });

        var decision = rig.Fx.Hooks.ClaimRelay("s-b", "b@tm", $"[hierarchy-msg {request}]", ContextOf(rig));

        Assert.True(decision.Deny);
        Assert.Equal("pending", State(rig, "br1"));
    }

    [Fact]
    public void AClaimTheMailboxCannotRecordDeniesTheRelayWithTheDeliverItselfText()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var claims = Path.Combine(rig.BoxB, "claims");
        File.SetUnixFileMode(claims, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var decision = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig));

            Assert.True(decision.Deny);
            Assert.Equal("ah could not record this relay; do not send it; ah will deliver the entry itself.", decision.Reason);
        }
        finally
        {
            File.SetUnixFileMode(claims, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.Equal("pending", State(rig, "e1"));
    }

    [Fact]
    public void ASenderSideSendMessageAckNoLongerCountsForAnEntryWithAClaim()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var acked = rig.Fx.Hooks.AckFromSendMessage("s-a", "b@tm", Relay("e1"), ContextOf(rig));

        Assert.Empty(acked);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxB, "e1"));
    }

    [Fact]
    public void ASenderSideSendMessageAckStillCountsForAnEntryWithoutAClaim()
    {
        var rig = MailClaimTests.Create();
        rig.Fx.Mailbox.AppendEntry(
            rig.BoxB,
            new JsonObject { ["v"] = 1, ["id"] = "old", ["from"] = "a@tm", ["to"] = "b@tm", ["kind"] = "note", ["route"] = "fallback", ["created"] = "2026-01-01T11:00:00.000Z", ["text"] = "t" },
            null);

        var acked = rig.Fx.Hooks.AckFromSendMessage("s-a", "b@tm", Relay("old"), ContextOf(rig));

        Assert.Equal(["old"], acked);
    }
}
