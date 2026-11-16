using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>
/// A relay that drains into a busy receiver never reaches a prompt hook. The receiver's Stop hook reads its own transcript for the
/// host's cross-session rows and acks, with the wrapper's sender checked as it is for a prompt, so the entry is delivered, the
/// plugin prompt never follows it, and a row that cannot be read acks nothing.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailTranscriptAckTests
{
    private static readonly int Pid = Environment.ProcessId;

    private static readonly DateTimeOffset Instant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static RequestContext ContextOf(MailClaimTests.Rig rig) => new(rig.Hierarchy, rig.Hierarchy, string.Empty, string.Empty);

    private static string State(MailClaimTests.Rig rig, string id) => rig.Claim(id)!.Get("state")!.Value.AsString();

    private static string TranscriptOf(MailClaimTests.Rig rig) => Path.Combine(Path.GetDirectoryName(rig.Hierarchy)!, "transcript.jsonl");

    private static MailClaimTests.Rig Relayed(params string[] ids)
    {
        var rig = MailClaimTests.Create(withPidStart: true);
        foreach (var id in ids)
        {
            rig.Send(id);
            var text = $"hello [ah-entry {id} to b@tm]";
            Assert.False(rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", text, ContextOf(rig), "toolu_" + id).Deny);
            rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", text, RelayResult.Sent, null, ContextOf(rig));
        }

        return rig;
    }

    private static string MetaRow(string wrapped, bool isMeta = true, string type = "user") =>
        JsonText.Stringify(
            new JsonObject
            {
                ["type"] = type,
                ["isMeta"] = isMeta,
                ["message"] = new JsonObject { ["role"] = "user", ["content"] = "Another Claude session sent a message:\n" + wrapped + "\n\nThis came from another Claude session." },
            },
            0);

    private static string Wrapper(string body, long? pid = null) =>
        $"<cross-session-message from=\"uds:/tmp/cc-socks/{pid ?? Pid}.sock\" from-name=\"agent\" from-mode=\"prompting\">\n{body}\n</cross-session-message>";

    [Fact]
    public void ARelayDrainedWhileBusyIsAckedByTheStopScanAndTheNextSessionGetsNoPluginPrompt()
    {
        var rig = Relayed("e1");
        var queue = JsonText.Stringify(new JsonObject { ["type"] = "queue-operation", ["operation"] = "enqueue", ["content"] = Wrapper("hello [ah-entry e1 to b@tm]") }, 0);
        File.WriteAllLines(TranscriptOf(rig), [queue, MetaRow(Wrapper("hello [ah-entry e1 to b@tm]"))]);

        var acked = rig.Fx.Hooks.AckFromTranscript("s-b", TranscriptOf(rig));

        Assert.Equal(["e1"], acked);
        Assert.Equal("stop-hook", rig.Fx.Mailbox.ReadAck(rig.BoxB, "e1")!.Value.AsObject().Get("by")!.Value.AsString());
        Assert.Equal("delivered", State(rig, "e1"));
        Assert.True(rig.Fx.Mailbox.RemoveRegistration("s-b"));
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s-b2", [new BoxRef("b@tm", rig.BoxB)], rig.TeamFile, "claude", Pid)));
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);
        rig.Fx.Clock.UtcNow = Instant + TimeSpan.FromHours(3);
        var answer = due.Compute(new DueRequest("s-b2", rig.Fx.Clock.UtcNow.ToUnixTimeMilliseconds(), null, 15000, new Dictionary<string, HandedState>(), null));
        Assert.Empty(answer.Ids);
    }

    [Fact]
    public void AQueueOperationRowAloneAcksNothing()
    {
        var rig = Relayed("e1");
        File.WriteAllLines(TranscriptOf(rig), [JsonText.Stringify(new JsonObject { ["type"] = "queue-operation", ["operation"] = "enqueue", ["content"] = Wrapper("hello [ah-entry e1 to b@tm]") }, 0)]);

        var acked = rig.Fx.Hooks.AckFromTranscript("s-b", TranscriptOf(rig));

        Assert.Empty(acked);
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void AUserRowThatIsNotMetaAcksNothing()
    {
        var rig = Relayed("e1");
        File.WriteAllLines(TranscriptOf(rig), [MetaRow(Wrapper("hello [ah-entry e1 to b@tm]"), isMeta: false)]);

        Assert.Empty(rig.Fx.Hooks.AckFromTranscript("s-b", TranscriptOf(rig)));
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void AWrapperFromAnotherProcessInTheTranscriptAcksNothing()
    {
        var rig = Relayed("e1");
        File.WriteAllLines(TranscriptOf(rig), [MetaRow(Wrapper("hello [ah-entry e1 to b@tm]", 4242))]);

        Assert.Empty(rig.Fx.Hooks.AckFromTranscript("s-b", TranscriptOf(rig)));
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void ARowThatCannotBeParsedAcksNothingAndTheRowsAfterItStillCount()
    {
        var rig = Relayed("e1");
        File.WriteAllLines(TranscriptOf(rig), ["{\"type\":\"user\",\"isMeta\":true,\"message\":{\"content\":\"Another Claude session sent a message: <cross-session-message from=", MetaRow(Wrapper("hello [ah-entry e1 to b@tm]"))]);

        var acked = rig.Fx.Hooks.AckFromTranscript("s-b", TranscriptOf(rig));

        Assert.Equal(["e1"], acked);
    }

    [Fact]
    public void ASecondScanStartsWhereTheFirstEndedAndALastLineWithoutANewlineWaits()
    {
        var rig = Relayed("e1", "e2");
        var path = TranscriptOf(rig);
        File.WriteAllText(path, MetaRow(Wrapper("hello [ah-entry e1 to b@tm]")) + "\n");
        Assert.Equal(["e1"], rig.Fx.Hooks.AckFromTranscript("s-b", path));

        File.AppendAllText(path, MetaRow(Wrapper("hello [ah-entry e2 to b@tm]")));
        Assert.Empty(rig.Fx.Hooks.AckFromTranscript("s-b", path));
        Assert.Equal("relay-sent", State(rig, "e2"));

        File.AppendAllText(path, "\n");
        Assert.Equal(["e2"], rig.Fx.Hooks.AckFromTranscript("s-b", path));
    }

    [Fact]
    public void AnotherSessionsBoxesAreNotAckedFromThisTranscript()
    {
        var rig = Relayed("e1");
        var other = rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "c");
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s-c", [new BoxRef("c@tm", other)], rig.TeamFile, "claude", Pid)));
        File.WriteAllLines(TranscriptOf(rig), [MetaRow(Wrapper("hello [ah-entry e1 to b@tm]"))]);

        Assert.Empty(rig.Fx.Hooks.AckFromTranscript("s-c", TranscriptOf(rig)));
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void AMailedBriefAckedByTheStopScanCarriesTheStopHookWriterAndTheSession()
    {
        var rig = MailClaimTests.Create(withPidStart: true);
        Directory.CreateDirectory(rig.Hierarchy + "/msgs");
        var request = rig.Hierarchy + "/msgs/20260101-120000-abcd--b--task--request.md";
        File.WriteAllText(request, "---\nid: 20260101-120000-abcd\ntype: request\nto: b\nfrom: a\nslug: task\n---\n");
        Assert.True(rig.Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["session_id"] = "s-a", ["id"] = "br1", ["ref"] = request, ["kind"] = "brief", ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy }).Ok);
        var text = $"[hierarchy-msg {request}] [ah-entry br1 to b@tm]";
        Assert.False(rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", text, ContextOf(rig), "toolu_1").Deny);
        rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", text, RelayResult.Sent, null, ContextOf(rig));
        File.WriteAllLines(TranscriptOf(rig), [MetaRow(Wrapper(text))]);

        rig.Fx.Hooks.AckFromTranscript("s-b", TranscriptOf(rig));

        var ack = rig.Fx.Mailbox.ReadAck(rig.BoxB, "br1")!.Value.AsObject();
        Assert.Equal("stop-hook", ack.Get("by")!.Value.AsString());
        Assert.Equal("s-b", ack.Get("session_id")!.Value.AsString());
        Assert.Equal("delivered", State(rig, "br1"));
    }
}
