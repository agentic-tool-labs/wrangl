using System.Runtime.Versioning;
using Ah.Abstractions;

namespace Ah.Mesh.Tests;

/// <summary>
/// A relay whose tool result could not be classified, or whose confirm could not be written, is assumed sent: only positive
/// evidence of failure sends the entry to the plugin prompt, and a clock alone never does.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailUnconfirmedRelayTests
{
    private static RequestContext ContextOf(MailClaimTests.Rig rig) => new(rig.Hierarchy, rig.Hierarchy, string.Empty, string.Empty);

    private static string Relay(string id) => $"hello [ah-entry {id} to b@tm]";

    private static string State(MailClaimTests.Rig rig, string id) => rig.Claim(id)!.Get("state")!.Value.AsString();

    private static MailClaimTests.Rig Claimed()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Assert.False(rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig), "toolu_1").Deny);
        return rig;
    }

    private static string NoteFile(MailClaimTests.Rig rig, string id) =>
        Path.Combine(Path.GetDirectoryName(rig.Hierarchy)!, "ah", "mailbox-notes", "relay-confirm", id + ".json");

    [Fact]
    public void AToolResultThatCannotBeReadIsAssumedSent()
    {
        var rig = Claimed();

        var moved = rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Unknown, null, ContextOf(rig));

        Assert.Equal(["e1"], moved);
        Assert.Equal("relay-sent", State(rig, "e1"));
        Assert.Equal("assumed", rig.Claim("e1")!.Get("reason")!.Value.AsString());
    }

    [Fact]
    public void AnExplicitErrorStillFailsTheRelay()
    {
        var rig = Claimed();

        rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Failed, "no inbox", ContextOf(rig));

        Assert.Equal("relay-failed", State(rig, "e1"));
    }

    [Fact]
    public void ARelayNobodyConfirmedIsNotFailedByTheClaimLeaseAndBecomesUnconfirmedAfterTheGrace()
    {
        var rig = Claimed();
        rig.Fx.Clock.UtcNow += TimeSpan.FromSeconds(61);
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);
        Assert.Equal("relay-claimed", State(rig, "e1"));

        rig.Fx.Clock.UtcNow += TimeSpan.FromSeconds(60);
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        Assert.Equal("relay-unconfirmed", State(rig, "e1"));
        rig.Fx.Clock.UtcNow += TimeSpan.FromHours(5);
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);
        Assert.Equal("relay-unconfirmed", State(rig, "e1"));
    }

    [Fact]
    public void AnUnconfirmedRelayIsDeniedToASecondCallLikeASentOne()
    {
        var rig = Claimed();
        rig.Fx.Clock.UtcNow += TimeSpan.FromSeconds(121);
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);

        var again = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", Relay("e1"), ContextOf(rig), "toolu_2");

        Assert.True(again.Deny);
        Assert.Equal("ah: entry e1 was already delivered to b@tm; do not resend it.", again.Reason);
    }

    [Fact]
    public void EvidenceThatTurnsUpLaterMovesAnUnconfirmedRelayToSentOrFailed()
    {
        var sent = Claimed();
        sent.Fx.Clock.UtcNow += TimeSpan.FromSeconds(121);
        sent.Fx.Mailbox.Claim(sent.BoxB, "e1", ClaimActions.Advance);
        sent.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Sent, null, ContextOf(sent));
        Assert.Equal("relay-sent", State(sent, "e1"));

        var failed = Claimed();
        failed.Fx.Clock.UtcNow += TimeSpan.FromSeconds(121);
        failed.Fx.Mailbox.Claim(failed.BoxB, "e1", ClaimActions.Advance);
        failed.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Failed, "denied", ContextOf(failed));
        Assert.Equal("relay-failed", State(failed, "e1"));
    }

    [Fact]
    public void AConfirmThatCannotBeWrittenIsKeptAsANoteAndTheLeaseAppliesItInsteadOfFallingBack()
    {
        var rig = Claimed();
        var claims = Path.Combine(rig.BoxB, "claims");
        File.SetUnixFileMode(claims, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", Relay("e1"), RelayResult.Sent, null, ContextOf(rig));
        }
        finally
        {
            File.SetUnixFileMode(claims, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Assert.True(File.Exists(NoteFile(rig, "e1")));
        Assert.Equal("relay-claimed", State(rig, "e1"));
        rig.Fx.Clock.UtcNow += TimeSpan.FromSeconds(61);

        rig.Fx.Mailbox.AdvanceClaim(rig.BoxB, "e1");

        Assert.Equal("relay-sent", State(rig, "e1"));
        Assert.False(File.Exists(NoteFile(rig, "e1")));
    }

    [Fact]
    public void ANoteWrittenByAnotherSessionIsNotApplied()
    {
        var rig = Claimed();
        Directory.CreateDirectory(Path.GetDirectoryName(NoteFile(rig, "e1"))!);
        File.WriteAllText(NoteFile(rig, "e1"), $"{{\"dir\":\"{rig.BoxB}\",\"id\":\"e1\",\"session_id\":\"s-other\",\"result\":\"sent\"}}\n");

        rig.Fx.Mailbox.AdvanceClaim(rig.BoxB, "e1");

        Assert.Equal("relay-claimed", State(rig, "e1"));
    }

    [Fact]
    public void AFailureNoteWrittenByAnotherSessionDoesNotFailTheClaimOrClearTheNote()
    {
        var rig = Claimed();
        Directory.CreateDirectory(Path.GetDirectoryName(NoteFile(rig, "e1"))!);
        File.WriteAllText(NoteFile(rig, "e1"), $"{{\"dir\":\"{rig.BoxB}\",\"id\":\"e1\",\"session_id\":\"s-other\",\"result\":\"failed\",\"reason\":\"no inbox\"}}\n");

        rig.Fx.Mailbox.AdvanceClaim(rig.BoxB, "e1");

        Assert.Equal("relay-claimed", State(rig, "e1"));
        Assert.True(File.Exists(NoteFile(rig, "e1")));
    }

    [Fact]
    public void ANoteOfAFailedSendIsAppliedAsAFailureAndTheFallbackFollows()
    {
        var rig = Claimed();
        Directory.CreateDirectory(Path.GetDirectoryName(NoteFile(rig, "e1"))!);
        File.WriteAllText(NoteFile(rig, "e1"), $"{{\"dir\":\"{rig.BoxB}\",\"id\":\"e1\",\"session_id\":\"s-a\",\"result\":\"failed\",\"reason\":\"no inbox\"}}\n");

        rig.Fx.Mailbox.AdvanceClaim(rig.BoxB, "e1");

        Assert.Equal("fallback-claimed", State(rig, "e1"));
    }
}
