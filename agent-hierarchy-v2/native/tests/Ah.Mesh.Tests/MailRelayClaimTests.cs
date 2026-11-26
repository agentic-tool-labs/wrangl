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

    private static string Printed(JsonObject? sent) => sent!.Get("sendmessage")!.Value.Member("text")!.Value.AsString();

    /// <summary>ah2-483 A1: the relay ah printed, sent after the fallback delivered the entry, is answered done and takes no claim.</summary>
    [Fact]
    public void ThePrintedRelayOfAnEntryDeliveredMeanwhileIsAnsweredDoneAndSendsNothing()
    {
        var rig = MailClaimTests.Create();
        var line = Printed(rig.Send("e1"));
        Elapse(rig, TimeSpan.FromSeconds(181));
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        var done = rig.Fx.Hooks.PrintedRelayDone(line, "s-a", ContextOf(rig));

        Assert.Equal("ah: entry e1 already delivered; nothing to send. Done: this call sent nothing, and nothing more is owed for it.", done);
        Assert.DoesNotContain("NOT DONE", done, StringComparison.Ordinal);
        Assert.Equal("fallback-claimed", State(rig, "e1"));
    }

    /// <summary>The same line as the owed-relay reminder prints it, led by its "(relayed: …)" prefix, is answered done too.</summary>
    [Fact]
    public void TheRemindersPrefixedRelayOfAnEntryDeliveredMeanwhileIsAnsweredDone()
    {
        var rig = MailClaimTests.Create();
        var line = RelayInstruction.Relayed("e1", "2026-01-01T12:00:00.000Z", Printed(rig.Send("e1")));
        Elapse(rig, TimeSpan.FromSeconds(181));
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        Assert.NotNull(rig.Fx.Hooks.PrintedRelayDone(line, "s-a", ContextOf(rig)));
    }

    /// <summary>Negative (Amendment 1): a superseded entry whose claim is past pending keeps its old answer, never "already delivered".</summary>
    [Fact]
    public void ThePrintedRelayOfASupersededEntryIsNotAnsweredDone()
    {
        var rig = MailClaimTests.Create();
        var line = Printed(rig.Send("e1"));
        Elapse(rig, TimeSpan.FromSeconds(181));
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);
        var row = new JsonObject { ["id"] = "e1", ["rung"] = 3, ["state"] = "superseded", ["by"] = "e9" };
        Assert.True(rig.Fx.Mailbox.AppendLadderRow(rig.BoxB, row, rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1").Count + 1L));

        Assert.Null(rig.Fx.Hooks.PrintedRelayDone(line, "s-a", ContextOf(rig)));
        Assert.StartsWith("ah: entry e1 was superseded by e9", rig.Fx.Hooks.SendDenial(line, "s-a", ContextOf(rig), out _), StringComparison.Ordinal);
    }

    /// <summary>Negative: a relay still owed is not answered done; it goes on to the claim, which it takes.</summary>
    [Fact]
    public void ThePrintedRelayOfAnEntryStillOwedIsNotAnsweredDone()
    {
        var rig = MailClaimTests.Create();
        var line = Printed(rig.Send("e1"));

        Assert.Null(rig.Fx.Hooks.PrintedRelayDone(line, "s-a", ContextOf(rig)));
        Assert.False(rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", line, ContextOf(rig)).Deny);
    }

    /// <summary>Negative: a message that is not exactly the printed relay is not answered done, so the existing refusal stands.</summary>
    [Fact]
    public void AnEditedRelayOfADeliveredEntryIsNotAnsweredDone()
    {
        var rig = MailClaimTests.Create();
        var line = Printed(rig.Send("e1"));
        Elapse(rig, TimeSpan.FromSeconds(181));
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        Assert.Null(rig.Fx.Hooks.PrintedRelayDone("again: " + line, "s-a", ContextOf(rig)));
    }

    /// <summary>Negative: a marker for an entry that was never created is not answered done.</summary>
    [Fact]
    public void ARelayForAnEntryThatWasNeverCreatedIsNotAnsweredDone()
    {
        var rig = MailClaimTests.Create();

        Assert.Null(rig.Fx.Hooks.PrintedRelayDone(Relay("nope"), "s-a", ContextOf(rig)));
    }

    /// <summary>ah2-483 R4: a send that prints after the relay lease ran out asks for no SendMessage and says ah delivers it.</summary>
    [Fact]
    public void ARepeatSendPastTheRelayLeaseAsksForNoSendMessage()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Elapse(rig, TimeSpan.FromSeconds(181));

        var again = rig.Send("e1", text: "hello e1, again");

        Assert.Null(again!.Get("sendmessage"));
        Assert.Contains("delivery: ah delivers it itself — no SendMessage; nothing more to do", rig.Fx.Mailbox.RenderSend(again), StringComparison.Ordinal);
    }

    /// <summary>R8: a repeat send of an entry the receiver already acked says no SendMessage is needed, not that ah delivers it.</summary>
    [Fact]
    public void ARepeatSendOfAnAckedEntrySaysNoSendMessageNeeded()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        _ = rig.Fx.Mailbox.AckIds(rig.BoxB, ["e1"], "prompt-hook");

        var again = rig.Send("e1", text: "hello e1, again");
        var text = rig.Fx.Mailbox.RenderSend(again!);

        Assert.Null(again!.Get("sendmessage"));
        Assert.Contains("delivery: no SendMessage needed — nothing more to do", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ah delivers it itself", text, StringComparison.Ordinal);
    }

    /// <summary>Negative: the same repeat inside the relay lease still asks for the relay.</summary>
    [Fact]
    public void ARepeatSendInsideTheRelayLeaseStillAsksForTheRelay()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Elapse(rig, TimeSpan.FromSeconds(60));

        var again = rig.Send("e1", text: "hello e1, again");

        Assert.NotNull(again!.Get("sendmessage"));
        Assert.StartsWith("delivery: relay", rig.Fx.Mailbox.RenderSend(again).Split('\n')[1], StringComparison.Ordinal);
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

    private static void Escalate(MailClaimTests.Rig rig, string id) =>
        Assert.True(rig.Fx.Mailbox.AppendLadderRow(rig.BoxB, new JsonObject { ["id"] = id, ["rung"] = 5, ["state"] = "escalated" }, rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, id).Count + 1L));

    [Fact]
    public void AnyoneMayRelayAnEntryTheLadderEscalatedAndItsClaimIsLeftAsItWas()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var beforeEscalation = rig.Fx.Hooks.ClaimRelay("s-x", "b@tm", Relay("e1"), ContextOf(rig), "toolu_x");
        Escalate(rig, "e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-x", "b@tm", Relay("e1"), ContextOf(rig), "toolu_y");

        Assert.True(beforeEscalation.Deny);
        Assert.False(decision.Deny, decision.Reason);
        Assert.Empty(decision.Claimed);
        Assert.Equal("pending", State(rig, "e1"));
    }

    [Fact]
    public void NegativeAnEscalatedEntryWhoseRelayAnotherSessionHoldsIsStillBounced()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Assert.False(rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig), "toolu_a").Deny);
        Escalate(rig, "e1");

        var decision = rig.Fx.Hooks.ClaimRelay("s-x", "b@tm", Relay("e1"), ContextOf(rig), "toolu_x");

        Assert.True(decision.Deny);
        Assert.Equal("relay-claimed", State(rig, "e1"));
    }
}
