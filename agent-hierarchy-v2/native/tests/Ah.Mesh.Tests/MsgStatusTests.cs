using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>
/// Where an entry stands, read by <c>ah msg status</c> and by the send gate through one function: the state, whether a relay is owed,
/// who may ask, and what a SendMessage that carries the entry's marker is told.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class MsgStatusTests
{
    private const double Now = 1_790_000_000_000;

    private static readonly DateTimeOffset Clock = DateTimeOffset.FromUnixTimeMilliseconds((long)Now);

    private const string Marker = "[hierarchy-msg /m/e1--request.md] [ah-entry e1 to m1@tm]";

    /// <summary>Entry <c>e1</c> from the orchestrator waits in the box of <c>m1@tm</c>.</summary>
    private sealed class Rig
    {
        internal Rig(string from = "orchestrator@tm")
        {
            var sandbox = UnitSandbox.Create();
            Root = sandbox.Root;
            var home = sandbox.Root + "/ah";
            Directory.CreateDirectory(home + "/registrations");
            Carrier = home + "/mailbox/tm~h/orch";
            Member = home + "/mailbox/tm~h/m1";
            Directory.CreateDirectory(Carrier);
            Directory.CreateDirectory(Member);
            Fx = MeshFixture.Create(new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = home }, Clock, sandbox.Root, singleCarrier: true);
            File.WriteAllText(
                home + "/registrations/s1.json",
                "{\"session_id\":\"s1\",\"address\":\"orchestrator@tm\",\"addresses\":[\"orchestrator@tm\"],\"boxes\":{\"orchestrator@tm\":\"" + Carrier + "\"}}");
            File.WriteAllText(
                home + "/registrations/s2.json",
                "{\"session_id\":\"s2\",\"address\":\"m1@tm\",\"addresses\":[\"m1@tm\"],\"boxes\":{\"m1@tm\":\"" + Member + "\"}}");
            File.AppendAllText(Member + "/entries.jsonl", "{\"id\":\"e1\",\"kind\":\"note\",\"from\":\"" + from + "\",\"to\":\"m1@tm\",\"created\":\"2026-10-12T00:00:00.000Z\",\"text\":\"hi\",\"route\":\"pull\"}\n");
        }

        internal string Root { get; }

        internal MeshFixture Fx { get; }

        internal string Carrier { get; }

        internal string Member { get; }

        internal CallerBox Orchestrator => new("orchestrator@tm", Carrier);

        internal CallerBox Recipient => new("m1@tm", Member);

        internal RequestContext Context => new(Root, Root, string.Empty, string.Empty);

        internal void Ladder(string state, int rung = 3, string? by = null, string? reason = null)
        {
            var row = new JsonObject { ["id"] = "e1", ["rung"] = rung, ["state"] = state };
            if (by is not null)
            {
                row["by"] = by;
            }

            if (reason is not null)
            {
                row["reason"] = reason;
            }

            Assert.True(Fx.Mailbox.AppendLadderRow(Member, row, Fx.Mailbox.LadderRowsOf(Member, "e1").Count + 1L));
        }

        internal JsonObject Status(params CallerBox[] callers) => Fx.Mailbox.EntryStatus("e1", callers)!;

        internal string? Gate(out string? warning) => Fx.Hooks.SendDenial("hello " + Marker, "s1", Context, out warning);
    }

    [Fact]
    public void S1AnAckedEntryIsAckedByWhoAckedItAndNoRelayIsOwed()
    {
        var rig = new Rig();
        rig.Ladder("pending", 2);
        _ = rig.Fx.Mailbox.AckIds(rig.Member, ["e1"], "prompt-hook", "s2");

        var status = rig.Status(rig.Orchestrator);

        Assert.Equal("acked", status["state"].AsString());
        Assert.Equal("prompt-hook", status["acked"].AsObject()["by"].AsString());
        Assert.False(string.IsNullOrEmpty(status["acked"].AsObject()["at"].AsString()));
        Assert.Equal("prompt-hook", status["delivered"].AsObject()["via"].AsString());
        Assert.False(status["relay_needed"].AsBool());
        Assert.StartsWith("acked by prompt-hook at ", status["relay_reason"].AsString(), StringComparison.Ordinal);
        Assert.Equal(("e1", "note", "orchestrator@tm", "m1@tm"), (status["entry"].AsString(), status["kind"].AsString(), status["from"].AsString(), status["to"].AsString()));
    }

    [Fact]
    public void S2AnEntryTheLadderHasStartedIsPendingAndOneItHasNotIsRoutedAndNeitherOwesARelay()
    {
        var rig = new Rig();

        var routed = rig.Status(rig.Orchestrator);
        rig.Ladder("pending", 2);
        var pending = rig.Status(rig.Orchestrator);

        Assert.Equal(("routed", false), (routed["state"].AsString(), routed["relay_needed"].AsBool()));
        Assert.Equal(("pending", false), (pending["state"].AsString(), pending["relay_needed"].AsBool()));
        Assert.True(pending["relay_reason"].IsNull);
        Assert.True(pending["acked"].IsNull);
        Assert.True(pending["delivered"].IsNull);
    }

    [Fact]
    public void AReleasedEntryOwesNoRelayStaysPendingAndTheGateSaysReleased()
    {
        var rig = new Rig();
        rig.Ladder("pending");
        Directory.CreateDirectory(rig.Member + "/claims");
        File.WriteAllText(rig.Member + "/claims/e1.json", "{\"state\":\"released\",\"carrier\":\"prompt\",\"at\":\"2026-10-12T00:00:30.000Z\"}");

        var status = rig.Status(rig.Orchestrator);

        Assert.Equal(("pending", false), (status["state"].AsString(), status["relay_needed"].AsBool()));
        Assert.Equal("released to the delivery ladder", status["relay_reason"].AsString());
        Assert.Equal("ah: entry e1 was released to the delivery ladder (no poller); do not resend it.", rig.Gate(out _));
    }

    [Fact]
    public void NegativeAnAckedEntryWithAReleasedClaimIsRefusedAsAcked()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Member + "/claims");
        File.WriteAllText(rig.Member + "/claims/e1.json", "{\"state\":\"released\",\"carrier\":\"prompt\",\"at\":\"2026-10-12T00:00:30.000Z\"}");
        _ = rig.Fx.Mailbox.AckIds(rig.Member, ["e1"], "prompt-hook", "s2");

        Assert.StartsWith("ah: entry e1 was already read (acked)", rig.Gate(out _), StringComparison.Ordinal);
    }

    [Fact]
    public void S3AnEntryWhoseRelayWasAskedForOwesItUntilSomethingVoidsIt()
    {
        var rig = new Rig();
        rig.Ladder("pending");

        Assert.True(rig.Status(rig.Orchestrator)["relay_needed"].AsBool());

        _ = rig.Fx.Mailbox.AckIds(rig.Member, ["e1"], "prompt-hook", "s2");

        Assert.False(rig.Status(rig.Orchestrator)["relay_needed"].AsBool());
    }

    [Fact]
    public void S4ASupersededEntryNamesWhatSupersededItAndADeclinedOneNamesWhoDeclinedAndWhy()
    {
        var superseded = new Rig();
        superseded.Ladder("superseded", by: "e9");
        var declined = new Rig();
        declined.Ladder("declined", by: "orchestrator@tm", reason: "withdrawn");

        var s = superseded.Status(superseded.Orchestrator);
        var d = declined.Status(declined.Orchestrator);

        Assert.Equal(("superseded", "e9", false), (s["state"].AsString(), s["superseded_by"].AsString(), s["relay_needed"].AsBool()));
        Assert.True(s["declined"].IsNull);
        Assert.Equal("declined", d["state"].AsString());
        Assert.Equal(("orchestrator@tm", "withdrawn"), (d["declined"].AsObject()["by"].AsString(), d["declined"].AsObject()["reason"].AsString()));
        Assert.True(d["superseded_by"].IsNull);
    }

    [Fact]
    public void NegativeAnEntryTheCallerHasNoClaimToIsUnknownJustAsAnIdThatDoesNotExistIs()
    {
        var rig = new Rig();
        var stranger = new CallerBox("z@other", rig.Carrier);

        Assert.Null(rig.Fx.Mailbox.EntryStatus("e1", [stranger]));
        Assert.Null(rig.Fx.Mailbox.EntryStatus("e1", []));
        Assert.Null(rig.Fx.Mailbox.EntryStatus("nope", [rig.Orchestrator]));
    }

    [Fact]
    public void S5TheRecipientTheSenderAndTheTeamsOrchestratorMayAskAndAnotherMemberMayNot()
    {
        var rig = new Rig(from: "n1@tm");

        Assert.NotNull(rig.Fx.Mailbox.EntryStatus("e1", [rig.Recipient]));
        Assert.NotNull(rig.Fx.Mailbox.EntryStatus("e1", [new CallerBox("n1@tm", rig.Carrier)]));
        Assert.NotNull(rig.Fx.Mailbox.EntryStatus("e1", [rig.Orchestrator]));
        Assert.Null(rig.Fx.Mailbox.EntryStatus("e1", [new CallerBox("q1@tm", rig.Carrier)]));
        Assert.Null(rig.Fx.Mailbox.EntryStatus("e1", [new CallerBox("orchestrator@other", rig.Carrier)]));
    }

    [Fact]
    public void G1ADeliveredEntryIsRefusedWithWhoGotItAndWhen()
    {
        var rig = new Rig();
        rig.Ladder("delivered", 2);

        var denial = rig.Gate(out var warning);

        Assert.Null(warning);
        Assert.StartsWith("ah: entry e1 was already delivered to m1@tm at ", denial, StringComparison.Ordinal);
        Assert.EndsWith("; not resent. Check with ah msg status e1.", denial, StringComparison.Ordinal);
    }

    [Fact]
    public void G2AnAckedEntryIsRefusedAsAlreadyRead()
    {
        var rig = new Rig();
        rig.Ladder("pending");
        _ = rig.Fx.Mailbox.AckIds(rig.Member, ["e1"], "prompt-hook", "s2");

        var denial = rig.Gate(out _);

        Assert.StartsWith("ah: entry e1 was already read (acked) by prompt-hook at ", denial, StringComparison.Ordinal);
        Assert.EndsWith("; not resent. Check with ah msg status e1.", denial, StringComparison.Ordinal);
    }

    [Fact]
    public void G3AnEntryWaitingForPullDeliveryWithNoRelayAskedIsRefusedAsQueued()
    {
        var rig = new Rig();
        rig.Ladder("pending", 2);

        Assert.Equal("ah: entry e1 is queued for pull delivery; the plugin prompt carries it. Do not SendMessage it.", rig.Gate(out _));
        Assert.StartsWith("ah: entry e1 is queued for pull delivery", new Rig().Gate(out _), StringComparison.Ordinal);
    }

    [Fact]
    public void G4AnEntryWhoseRelayIsOwedIsLetThroughToTheClaim()
    {
        var rig = new Rig();
        rig.Ladder("pending");

        Assert.Null(rig.Gate(out var warning));
        Assert.Null(warning);
    }

    [Fact]
    public void NegativeSupersededAndDeclinedEntriesAreRefusedWithTheirOwnWords()
    {
        var superseded = new Rig();
        superseded.Ladder("superseded", by: "e9");
        var declined = new Rig();
        declined.Ladder("declined", by: "orchestrator@tm", reason: "withdrawn");

        Assert.Equal("ah: entry e1 was superseded by e9; not resent. Check with ah msg status e1.", superseded.Gate(out _));
        Assert.Equal("ah: entry e1 was declined (withdrawn); not resent. Check with ah msg status e1.", declined.Gate(out _));
    }

    [Fact]
    public void G5AMessageWithNoMarkerAndOneNamingAnEntryNoBoxHoldsAreNotTheGates()
    {
        var rig = new Rig();
        rig.Ladder("delivered", 2);

        Assert.Null(rig.Fx.Hooks.SendDenial("hello there", "s1", rig.Context, out var warning));
        Assert.Null(warning);
        Assert.Null(rig.Fx.Hooks.SendDenial("hello [ah-entry nope to m1@tm]", "s1", rig.Context, out _));
    }

    [Fact]
    public void NegativeTwoEntriesOneVoidAndOneLiveAreRefusedNamingTheVoidOne()
    {
        var rig = new Rig();
        File.AppendAllText(rig.Member + "/entries.jsonl", "{\"id\":\"e2\",\"kind\":\"note\",\"from\":\"orchestrator@tm\",\"to\":\"m1@tm\",\"created\":\"2026-10-12T00:00:01.000Z\",\"text\":\"hi\",\"route\":\"pull\"}\n");
        Assert.True(rig.Fx.Mailbox.AppendLadderRow(rig.Member, new JsonObject { ["id"] = "e2", ["rung"] = 3, ["state"] = "pending" }, 1));
        rig.Ladder("delivered", 2);

        var denial = rig.Fx.Hooks.SendDenial("both [ah-entry e2 to m1@tm] [ah-entry e1 to m1@tm]", "s1", rig.Context, out _);

        Assert.StartsWith("ah: entry e1 was already delivered", denial, StringComparison.Ordinal);
        Assert.Null(rig.Fx.Hooks.SendDenial("live only [ah-entry e2 to m1@tm]", "s1", rig.Context, out _));
    }

    [Fact]
    public void G6RecordsThatCannotBeReadLetTheSendGoAheadWithAWarningNamingTheEntry()
    {
        var rig = new Rig();
        Directory.CreateDirectory(rig.Member + "/attempts.jsonl");

        var denial = rig.Gate(out var warning);

        Assert.Null(denial);
        Assert.Equal("ah: could not check entry e1; sending", warning);
    }

    [Fact]
    public void NothingIsWrittenByAskingForTheStatusOrByTheGate()
    {
        var rig = new Rig();
        rig.Ladder("pending");
        var before = Directory.GetFiles(rig.Member, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).OrderBy(static t => t.f, StringComparer.Ordinal).ToList();

        _ = rig.Status(rig.Orchestrator);
        _ = rig.Gate(out _);

        var after = Directory.GetFiles(rig.Member, "*", SearchOption.AllDirectories).Select(f => (f, File.GetLastWriteTimeUtc(f), new FileInfo(f).Length)).OrderBy(static t => t.f, StringComparer.Ordinal).ToList();
        Assert.Equal(before, after);
    }

    private static void Claim(Rig rig, string json)
    {
        Directory.CreateDirectory(rig.Member + "/claims");
        File.WriteAllText(rig.Member + "/claims/e1.json", json);
    }

    [Fact]
    public void AnEntryTheLadderEscalatedAtRung5IsDeliveryFailedAndTheGateLetsItsRelayThrough()
    {
        var rig = new Rig();
        rig.Ladder("pending", 4);
        Claim(rig, "{\"state\":\"released\",\"carrier\":\"prompt\",\"at\":\"2026-10-12T00:00:30.000Z\"}");
        var onRung4 = rig.Status(rig.Orchestrator);
        var bouncedOnRung4 = rig.Gate(out _);
        rig.Ladder("escalated", 5, reason: "no poller");
        var claimBefore = File.ReadAllText(rig.Member + "/claims/e1.json");

        var failed = rig.Status(rig.Orchestrator);

        Assert.Equal("pending", onRung4["state"].AsString());
        Assert.NotNull(bouncedOnRung4);
        Assert.Equal("delivery-failed", failed["state"].AsString());
        Assert.Null(rig.Gate(out _));
        Assert.Equal(claimBefore, File.ReadAllText(rig.Member + "/claims/e1.json"));
    }

    [Fact]
    public void AnEscalatedEntryOverAFallbackClaimIsDeliveryFailedButOneWithoutTheEscalationReadsDelivered()
    {
        var rig = new Rig();
        rig.Ladder("pending", 4);
        Claim(rig, "{\"state\":\"fallback-claimed\",\"carrier\":\"prompt\",\"at\":\"2026-10-12T00:00:30.000Z\",\"lease_until\":\"2099-01-01T00:00:00.000Z\"}");
        var pasted = rig.Status(rig.Orchestrator)["state"].AsString();
        rig.Ladder("escalated", 5);

        Assert.Equal("delivered", pasted);
        Assert.Equal("delivery-failed", rig.Status(rig.Orchestrator)["state"].AsString());
    }

    [Fact]
    public void NegativeAnEscalatedEntryTheRecipientAckedIsStillBounced()
    {
        var rig = new Rig();
        rig.Ladder("escalated", 5);
        _ = rig.Fx.Mailbox.AckIds(rig.Member, ["e1"], "prompt-hook", "s2");

        Assert.StartsWith("ah: entry e1 was already read (acked)", rig.Gate(out _), StringComparison.Ordinal);
    }
}
