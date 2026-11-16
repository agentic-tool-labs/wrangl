using System.Runtime.Versioning;

namespace Ah.Mesh.Tests;

/// <summary>b478: a member's own digest notes reach a busy turn at its next tool call, once; every other kind keeps its carrier.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailDueMidTurnNoteTests
{
    private static readonly DateTimeOffset Instant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static MailClaimTests.Rig Rig()
    {
        var rig = MailClaimTests.Create();
        // a live mail poll on b's box: a peer's plain note to b is then a pull-routed digest note
        rig.Fx.Mailbox.WriteHeartbeat(rig.BoxB, "claude-mod", "s-b", 5000);
        return rig;
    }

    private static DueAnswer Due(MailClaimTests.Rig rig, TimeSpan after, DoorbellCarrier carrier)
    {
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);
        var now = Instant + after;
        rig.Fx.Clock.UtcNow = now;
        var busyAt = (double)(Instant + TimeSpan.FromSeconds(1)).ToUnixTimeMilliseconds();
        return due.Compute(new DueRequest("s-b", now.ToUnixTimeMilliseconds(), busyAt, 15000, new Dictionary<string, HandedState>(), null) { Carrier = carrier });
    }

    private static IReadOnlyList<DigestEntry> Digest(MailClaimTests.Rig rig) =>
        MeshModule.CreateMailDoorbell(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment).DigestEntries("s-b", rig.Fx.Clock.UtcNow.ToUnixTimeMilliseconds());

    [Fact]
    public void ADigestNoteSentMidTurnShowsInTheNextToolCallOnceAndTheNextDigestDoesNotRepeatIt()
    {
        var rig = Rig();
        rig.Send("n1", text: "amend step 3");
        Assert.True(rig.Fx.Mailbox.IsMemberDigestNote(rig.Fx.Mailbox.ReadEntries(rig.BoxB).Entries.Single(), "b@tm"));

        var prompted = Due(rig, TimeSpan.FromSeconds(5), DoorbellCarrier.Prompt);
        var first = Due(rig, TimeSpan.FromSeconds(10), DoorbellCarrier.Tool);
        var second = Due(rig, TimeSpan.FromSeconds(15), DoorbellCarrier.Tool);

        Assert.Empty(prompted.Ids);
        Assert.Equal(["n1"], first.Ids);
        Assert.StartsWith("[ah note, delivered mid-turn]\nah digest (1):\n- note from a@tm: amend step 3", first.Prompt, StringComparison.Ordinal);
        Assert.False(first.Ring);
        Assert.Empty(second.Ids);
        Assert.Null(second.Prompt);
        Assert.Empty(Digest(rig));
        Assert.True(rig.Fx.Mailbox.IsAcked(rig.BoxB, "n1"));
    }

    [Fact]
    public void WithNoNoteTheToolCallGetsNoContext()
    {
        var rig = Rig();

        var answer = Due(rig, TimeSpan.FromSeconds(10), DoorbellCarrier.Tool);

        Assert.Empty(answer.Ids);
        Assert.Null(answer.Prompt);
    }

    [Fact]
    public void AQuestionNoteIsNotADigestNoteAndIsNotInToolContext()
    {
        var rig = Rig();
        rig.Send("q1", text: "QUESTION: which file?");

        var answer = Due(rig, TimeSpan.FromSeconds(10), DoorbellCarrier.Tool);

        Assert.Empty(answer.Ids);
        Assert.Null(answer.Prompt);
        Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxB, "q1"));
    }
}
