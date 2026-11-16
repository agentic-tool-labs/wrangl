using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;

namespace Ah.Mesh.Tests;

/// <summary>
/// The daemon's side of single-carrier delivery: an entry is due as a plugin prompt only once the daemon holds its fallback
/// claim, a relay that was sent is never timed out, and the delivery ladder asks no one to relay.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailSingleCarrierTests
{
    private static readonly DateTimeOffset Instant = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static RequestContext ContextOf(MailClaimTests.Rig rig) => new(rig.Hierarchy, rig.Hierarchy, string.Empty, string.Empty);

    private static DueAnswer Due(MailClaimTests.Rig rig, string session, TimeSpan after, double? busyAt = null, Dictionary<string, HandedState>? handed = null)
    {
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);
        var now = Instant + after;
        rig.Fx.Clock.UtcNow = now;
        return due.Compute(new DueRequest(session, now.ToUnixTimeMilliseconds(), busyAt, 15000, handed ?? [], null));
    }

    private static void Relayed(MailClaimTests.Rig rig, string id)
    {
        var text = $"hello [ah-entry {id} to b@tm]";
        var claim = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", text, ContextOf(rig));
        Assert.False(claim.Deny, claim.Reason);
        rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", text, RelayResult.Sent, null, ContextOf(rig));
    }

    private static string State(MailClaimTests.Rig rig, string id) => rig.Claim(id)!.Get("state")!.Value.AsString();

    [Fact]
    public void ARelayThatArrivedWhileTheReceiverWasBusyIsNeverSubmittedAsAPluginPrompt()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var busyAt = (double)(Instant + TimeSpan.FromSeconds(5)).ToUnixTimeMilliseconds();

        var whileBusy = Due(rig, "s-b", TimeSpan.FromSeconds(10), busyAt);
        Relayed(rig, "e1");
        var afterIdle = Due(rig, "s-b", TimeSpan.FromMinutes(10));
        var muchLater = Due(rig, "s-b", TimeSpan.FromHours(5));

        Assert.Empty(whileBusy.Ids);
        Assert.Empty(afterIdle.Ids);
        Assert.Empty(muchLater.Ids);
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void WithNoRelayTheEntryIsDueOnceAfterTheRelayLeaseAndALateRelayIsDenied()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var before = Due(rig, "s-b", TimeSpan.FromSeconds(179));
        var waits = Due(rig, "s-b", TimeSpan.FromSeconds(181));
        var at = Due(rig, "s-b", TimeSpan.FromSeconds(196));
        var handed = new Dictionary<string, HandedState> { ["e1"] = new("pending", (Instant + TimeSpan.FromSeconds(196)).ToUnixTimeMilliseconds()) };
        var again = Due(rig, "s-b", TimeSpan.FromSeconds(220), handed: handed);
        var late = rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", "hello [ah-entry e1 to b@tm]", ContextOf(rig));

        Assert.Empty(before.Ids);
        Assert.Empty(waits.Ids);
        Assert.Equal(["e1"], at.Ids);
        Assert.Empty(again.Ids);
        Assert.True(late.Deny);
        Assert.Equal("fallback-claimed", State(rig, "e1"));
    }

    [Fact]
    public void ACarrierPromptEntryIsDueAtOnceWithNoGrace()
    {
        var rig = MailClaimTests.Create(receiverHarness: "codex");
        rig.Send("e1");

        var answer = Due(rig, "s-b", TimeSpan.FromSeconds(1));

        Assert.Equal(["e1"], answer.Ids);
        Assert.Equal("fallback-claimed", State(rig, "e1"));
    }

    [Fact]
    public void ASentRelayIsNeverTimedOutWhateverTheReceiverIsDoing()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Relayed(rig, "e1");

        var atPrompt = Due(rig, "s-b", TimeSpan.FromHours(3), (double)(Instant + TimeSpan.FromHours(3)).ToUnixTimeMilliseconds());
        var idleLater = Due(rig, "s-b", TimeSpan.FromHours(30));

        Assert.Empty(atPrompt.Ids);
        Assert.Empty(idleLater.Ids);
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void ASentRelayWhoseReceiverSessionIsGoneIsNotFallenBackToTheNextSessionOfTheAddress()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Relayed(rig, "e1");

        Assert.True(rig.Fx.Mailbox.RemoveRegistration("s-b"));
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s-b2", [new BoxRef("b@tm", rig.BoxB)], rig.TeamFile, "claude", Environment.ProcessId)));
        var next = Due(rig, "s-b2", TimeSpan.FromSeconds(20));
        var much = Due(rig, "s-b2", TimeSpan.FromHours(2));

        Assert.Empty(next.Ids);
        Assert.Empty(much.Ids);
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void ASentRelayWhoseReceiverProcessIsDeadIsNotFallenBack()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Relayed(rig, "e1");
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s-b", [new BoxRef("b@tm", rig.BoxB)], rig.TeamFile, "claude", 4_000_000)));

        var answer = Due(rig, "s-b", TimeSpan.FromSeconds(10));

        Assert.Empty(answer.Ids);
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void ASentRelayWhoseReceiverRegisteredWithAnotherStartTimeIsNotFallenBack()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Relayed(rig, "e1");
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("s-b", [new BoxRef("b@tm", rig.BoxB)], rig.TeamFile, "claude", Environment.ProcessId, PidStart: "Mon Jan  1 00:00:00 1990")));

        var answer = Due(rig, "s-b", TimeSpan.FromSeconds(10));

        Assert.Empty(answer.Ids);
        Assert.Equal("relay-sent", State(rig, "e1"));
    }

    [Fact]
    public void ARelayThatFailedStillFallsBackToTheNextSessionOfTheAddress()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var text = "hello [ah-entry e1 to b@tm]";
        Assert.False(rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", text, ContextOf(rig), "toolu_1").Deny);
        rig.Fx.Hooks.ConfirmRelay("s-a", "b@tm", text, RelayResult.Failed, "no inbox", ContextOf(rig));

        var next = Due(rig, "s-b", TimeSpan.FromSeconds(20));

        Assert.Equal(["e1"], next.Ids);
        Assert.Equal("fallback-claimed", State(rig, "e1"));
    }

    [Fact]
    public void TheNextDeadlineIsTheRelayLeaseThenTheClaimLeaseAndItsGrace()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");

        var pending = Due(rig, "s-b", TimeSpan.FromSeconds(10));
        rig.Fx.Hooks.ClaimRelay("s-a", "b@tm", "hello [ah-entry e1 to b@tm]", ContextOf(rig));
        var claimed = Due(rig, "s-b", TimeSpan.FromSeconds(20));

        Assert.Equal((Instant + TimeSpan.FromSeconds(180)).ToUnixTimeMilliseconds(), pending.NextDeadlineMs);
        Assert.Equal((Instant + TimeSpan.FromSeconds(130)).ToUnixTimeMilliseconds(), claimed.NextDeadlineMs);
    }

    [Fact]
    public void AnEntryWithoutAClaimRecordIsDueByTheOldRulesAndGetsNoClaim()
    {
        var rig = MailClaimTests.Create();
        rig.Fx.Mailbox.AppendEntry(
            rig.BoxB,
            new JsonObject { ["v"] = 1, ["id"] = "old", ["from"] = "a@tm", ["to"] = "b@tm", ["kind"] = "note", ["route"] = "fallback", ["created"] = "2026-01-01T12:00:00.000Z", ["text"] = "t" },
            null);

        var early = Due(rig, "s-b", TimeSpan.FromSeconds(100));
        var late = Due(rig, "s-b", TimeSpan.FromSeconds(200));

        Assert.Empty(early.Ids);
        Assert.Equal(["old"], late.Ids);
        Assert.Null(rig.Claim("old"));
    }

    private sealed class Mailer : IDeliveryMail
    {
        internal List<JsonObject> Sent { get; } = [];

        public void Send(string home, JsonObject request) => Sent.Add(request);
    }

    private sealed class NoPanes : IDeliveryPaste
    {
        internal int Pastes;

        internal bool Mailed;

        internal string? Refuse;

        internal List<string> Payloads { get; } = [];

        public JsonObject PlacementOf(JsonValue member, string? teamTransport) =>
            new() { ["pasteable"] = true, ["transport"] = "tmux", ["pane"] = "%1" };

        public PasteOutcome Paste(JsonValue member, string teamName, string? teamTransport, string payload, string entryId, string boxDir, int headerChars = 0)
        {
            // A wake names a pane or nothing, as the real paste would refuse.
            if (entryId.Length == 0 && member.Member("target") is not { IsText: true })
            {
                return new PasteOutcome("refused", "no-pane");
            }

            Pastes++;
            Payloads.Add(payload);
            if (Refuse is not null)
            {
                return new PasteOutcome("refused", Refuse);
            }

            return Mailed ? new PasteOutcome("mailed", "no-status") : new PasteOutcome("confirmed", null);
        }
    }

    /// <summary>The rig's mailbox, withdrawing entry <c>e1</c> when the ladder builds its paste line: after its sending row, before the paste.</summary>
    public class WithdrawWhilePasting : System.Reflection.DispatchProxy
    {
        internal IMailbox Inner = null!;

        internal string Dir = string.Empty;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IMailbox.RelayLine))
            {
                _ = Inner.Withdraw(Dir, "e1", "ahd", "superseded", null);
            }

            return targetMethod.Invoke(Inner, args);
        }
    }

    private static (DeliveryLadder Ladder, Mailer Mail, NoPanes Panes) LadderOf(MailClaimTests.Rig rig, IMailbox? mailbox = null)
    {
        var mail = new Mailer();
        var panes = new NoPanes();
        var config = ConfigModule.CreateCore(rig.Fx.Files, rig.Fx.Environment, () => rig.Fx.Roster, new ProcessFacts(rig.Hierarchy, 4242));
        return (new DeliveryLadder(mailbox ?? rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Roster, config, rig.Fx.Hierarchy, panes, rig.Fx.Clock, mail), mail, panes);
    }

    private static async Task Pass(MailClaimTests.Rig rig, DeliveryLadder ladder, TimeSpan after)
    {
        rig.Fx.Clock.UtcNow = Instant + after;
        _ = await ladder.RunAsync((Instant + after).ToUnixTimeMilliseconds(), 15000, CancellationToken.None);
    }

    /// <summary>A running daemon's passes up to <paramref name="to"/>, a minute apart: one long jump would read as the machine waking, which holds back a wake for a while.</summary>
    private static async Task PassSteadily(MailClaimTests.Rig rig, DeliveryLadder ladder, TimeSpan from, TimeSpan to)
    {
        for (var at = from + TimeSpan.FromMinutes(1); at < to; at += TimeSpan.FromMinutes(1))
        {
            await Pass(rig, ladder, at);
        }

        await Pass(rig, ladder, to);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AMembersSendToAnExitedSocketIsRefusedOnlyWhenItsOrchestratorRunsElsewhere(bool orchestratorRuns)
    {
        var rig = MailClaimTests.Create();
        var orchestratorPid = orchestratorRuns ? Environment.ProcessId : 99999997;
        File.WriteAllText(rig.TeamFile, File.ReadAllText(rig.TeamFile).Replace("\"orchestrator\":{\"pid\":1", $"\"orchestrator\":{{\"pid\":{orchestratorPid}", StringComparison.Ordinal));

        var outcome = rig.Fx.Gate.Check(new MeshGateRequest(ContextOf(rig), "s-a", "uds:/tmp/cc-socks/99999998.sock"));

        Assert.Equal(orchestratorRuns, outcome.Value is { Verdict.Allow: false });
        if (orchestratorRuns)
        {
            Assert.Contains($"/{orchestratorPid}.sock", outcome.Value!.Verdict.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void AMembersSendToALiveUnregisteredSocketGoesThrough()
    {
        var rig = MailClaimTests.Create();
        using var other = System.Diagnostics.Process.Start("/bin/sleep", "30");
        try
        {
            Assert.Null(rig.Fx.Gate.Check(new MeshGateRequest(ContextOf(rig), "s-a", $"uds:/tmp/cc-socks/{other.Id}.sock")).Value);
        }
        finally
        {
            other.Kill();
        }
    }

    [Theory]
    [InlineData(",\"bg_open\":1", 0)]
    [InlineData("", 1)]
    public async Task AMemberIdleOnBackgroundWorkRaisesNoUnreadAtIdleAlarm(string fields, int alarms)
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        Relayed(rig, "e1");
        Ah.TestKit.TestActivity.Put(rig.Fx.Hierarchy.StateDomains, rig.Fx.Environment.AhHome, rig.Hierarchy, "s-b", $"{{\"activity\":\"idle\",\"at\":\"2026-01-01T12:00:00Z\",\"state_since\":\"2026-01-01T12:00:00Z\"{fields}}}");
        var (ladder, mail, _) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(5));

        Assert.Equal(alarms, mail.Sent.Count(static m => m.Get("event") is { IsText: true } e && e.AsString() == "unread-at-idle"));
    }

    // b466: a brief handed over by the plugin prompt, then reported on, then left idle. The nearest inputs that are no report: the
    // template response, a request to another member (a brief to b with c's --ref), and a report created before the brief (b466-r1).
    [Theory]
    [InlineData("\n- DONE abc1234\n", "b", "2026-01-01T07:00:05-05:00", 0)]
    [InlineData("", "b", "2026-01-01T07:00:05-05:00", 1)]
    [InlineData("\n- DONE abc1234\n", "c", "2026-01-01T07:00:05-05:00", 1)]
    [InlineData("\n- DONE abc1234\n", "b", "2026-01-01T06:59:00-05:00", 1)]
    public async Task ABriefReportedOnAfterThePluginPromptIsNotDeliveredAgainOrAlarmedAsUnread(string body, string toName, string reportedAt, int alarms)
    {
        var rig = MailClaimTests.Create();
        Directory.CreateDirectory(rig.Hierarchy + "/msgs");
        var request = rig.Hierarchy + "/msgs/20260101-120000-abcd--b--task--request.md";
        File.WriteAllText(request, $"---\nid: 20260101-120000-abcd\nto_name: {toName}\nteam: tm\n---\n");
        var sent = rig.Fx.Mailbox.Send(new JsonObject { ["to"] = "b@tm", ["from"] = "a@tm", ["id"] = "br1", ["ref"] = request, ["cwd"] = rig.Hierarchy, ["hierarchy_dir"] = rig.Hierarchy });
        Assert.True(sent.Ok, sent.Failure?.Message);
        Assert.Equal(["br1"], Due(rig, "s-b", TimeSpan.FromSeconds(10)).Ids);
        Assert.Equal("fallback-claimed", State(rig, "br1"));
        File.WriteAllText(rig.Hierarchy + "/msgs/20260101-120000-abcd--orchestrator--task--response.md", $"---\nid: 20260101-120000-abcd\ntype: response\ncreated: {reportedAt}\n---\n" + body);
        Ah.TestKit.TestActivity.Put(rig.Fx.Hierarchy.StateDomains, rig.Fx.Environment.AhHome, rig.Hierarchy, "s-b", "{\"activity\":\"idle\",\"at\":\"2026-01-01T12:00:00Z\",\"state_since\":\"2026-01-01T12:00:00Z\"}");
        var (ladder, mail, panes) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(5));
        await Pass(rig, ladder, TimeSpan.FromMinutes(30));

        Assert.Equal(alarms, mail.Sent.Count(static m => m.Get("event") is { IsText: true } e && e.AsString() == "unread-at-idle"));
        Assert.DoesNotContain(mail.Sent, static m => m.Get("event") is { IsText: true } e && e.AsString() == "delivery-failed");
        Assert.Equal(0, panes.Pastes);
        if (alarms == 0)
        {
            var last = rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "br1")[^1];
            Assert.Equal("delivered", last.Get("state")!.Value.AsString());
            Assert.Equal("reported", last.Get("reason")!.Value.AsString());

            // no resolved record (the mod restarted): the ack alone keeps the poll from handing it over
            Assert.True(rig.Fx.Mailbox.IsAcked(rig.BoxB, "br1"));
            Assert.Empty(Due(rig, "s-b", TimeSpan.FromHours(1)).Ids);
        }
        else
        {
            Assert.False(rig.Fx.Mailbox.IsAcked(rig.BoxB, "br1"));
        }
    }

    // b466: the mod's record turns resolved once the engine took the prompt; a record still pending is the nearest that is not.
    [Theory]
    [InlineData("resolved", true)]
    [InlineData("pending", false)]
    public void AnEntryInAPromptTheEngineTookIsAckedAndNotHandedOverAgain(string handed, bool acked)
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1", session: null);
        Assert.Equal(["e1"], Due(rig, "s-b", TimeSpan.FromSeconds(10)).Ids);
        var at = (double)(Instant + TimeSpan.FromSeconds(10)).ToUnixTimeMilliseconds();

        var later = Due(rig, "s-b", TimeSpan.FromHours(1), handed: new Dictionary<string, HandedState> { ["e1"] = new(handed, at) });

        Assert.Equal(acked, rig.Fx.Mailbox.IsAcked(rig.BoxB, "e1"));
        Assert.Equal(acked, later.Acked.Contains("e1"));
        if (acked)
        {
            Assert.Empty(later.Ids);
        }
    }

    [Fact]
    public async Task TheLadderNeverAsksAnyoneToRelayAnEntryThatHasAClaim()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1");
        var (ladder, mail, panes) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.Zero);
        await Pass(rig, ladder, TimeSpan.FromMinutes(11));
        await Pass(rig, ladder, TimeSpan.FromMinutes(30));

        Assert.Empty(mail.Sent);
        Assert.Empty(rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1"));
        Assert.Equal(0, panes.Pastes);
    }

    /// <summary>b459 step 9: a settled ladder gets no row whether its entry is acked or not; an open one and one with no ladder are evaluated as before.</summary>
    [Fact]
    public async Task ASettledLadderIsLeftAloneAndAnOpenOrMissingOneIsEvaluated()
    {
        var rig = MailClaimTests.Create();
        var box = rig.Fx.Mailbox;
        foreach (var id in new[] { "acked-delivered", "unacked-withdrawn", "acked-pending", "acked-bare" })
        {
            rig.Send(id);
        }

        Assert.True(box.AppendLadderRow(rig.BoxB, new JsonObject { ["id"] = "acked-delivered", ["rung"] = 2, ["state"] = "delivered" }, 1));
        Assert.True(box.AppendLadderRow(rig.BoxB, new JsonObject { ["id"] = "unacked-withdrawn", ["rung"] = 2, ["state"] = "withdrawn" }, 1));
        Assert.True(box.AppendLadderRow(rig.BoxB, new JsonObject { ["id"] = "acked-pending", ["rung"] = 2, ["state"] = "pending" }, 1));
        _ = box.AckIds(rig.BoxB, ["acked-delivered", "acked-pending", "acked-bare"], "test");
        var (ladder, _, _) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.Zero);

        Assert.Single(box.LadderRowsOf(rig.BoxB, "acked-delivered"));
        Assert.Single(box.LadderRowsOf(rig.BoxB, "unacked-withdrawn"));
        Assert.Equal(["pending", "delivered"], box.LadderRowsOf(rig.BoxB, "acked-pending").Select(static r => r.Get("state")!.Value.AsString()));
        Assert.Equal(["delivered"], box.LadderRowsOf(rig.BoxB, "acked-bare").Select(static r => r.Get("state")!.Value.AsString()));
    }

    [Fact]
    public async Task WithTheSwitchOffTheLadderAsksForARelayAsBefore()
    {
        var rig = MailClaimTests.Create("{\"single_carrier\":false}\n");
        rig.Send("e1");
        var (ladder, _, _) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.Zero);
        await Pass(rig, ladder, TimeSpan.FromMinutes(11));

        Assert.Null(rig.Claim("e1"));
        Assert.NotEmpty(rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1"));
    }

    [Fact]
    public async Task AnOldEntryWithoutAClaimStillGoesThroughTheLadder()
    {
        var rig = MailClaimTests.Create();
        rig.Fx.Mailbox.AppendEntry(
            rig.BoxB,
            new JsonObject { ["v"] = 1, ["id"] = "old", ["from"] = "a@tm", ["to"] = "b@tm", ["kind"] = "note", ["route"] = "fallback", ["created"] = "2026-01-01T12:00:00.000Z", ["text"] = "t" },
            null);
        var (ladder, _, _) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(11));

        Assert.NotEmpty(rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "old"));
    }

    [Theory]
    [InlineData("note", false)]
    [InlineData("brief", true)]
    public async Task TheLadderLeavesAMembersPlainNoteAloneButNegativeStillCarriesABrief(string kind, bool laddered)
    {
        var rig = MailClaimTests.Create();
        rig.Fx.Mailbox.AppendEntry(
            rig.BoxB,
            new JsonObject { ["v"] = 1, ["id"] = "m1", ["from"] = "a@tm", ["to"] = "b@tm", ["kind"] = kind, ["route"] = "pull", ["created"] = "2026-01-01T12:00:00.000Z", ["text"] = "t" },
            null);
        var (ladder, _, _) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(11));

        Assert.Equal(laddered, rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "m1").Any());
    }

    [Fact]
    public async Task ANonClaudeReceiverOfAClaimEntryIsPastedToAfterThePullTimeout()
    {
        var rig = MailClaimTests.Create(receiverHarness: "codex");
        rig.Send("e1");
        var (ladder, mail, _) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromSeconds(10));
        var early = rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1").Count;
        await Pass(rig, ladder, TimeSpan.FromMinutes(3));

        Assert.Equal(0, early);
        Assert.Empty(mail.Sent);
        var rows = rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1").Select(r => $"{r.Get("rung")!.Value.AsNumber()}:{r.Get("state")!.Value.AsString()}").ToList();
        Assert.Contains("4:pending", rows);
    }

    [Fact]
    public async Task AnEntryReleasedForNoPollerIsStillPastedWithNoRelayFromAnyone()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1", from: "user", session: null);
        var (ladder, _, panes) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        Assert.Equal(ClaimStates.Released, rig.Claim("e1")!.Get("state")!.Value.AsString());
        await Pass(rig, ladder, TimeSpan.FromMinutes(14));
        await Pass(rig, ladder, TimeSpan.FromMinutes(15));

        Assert.Equal(1, panes.Pastes);
        Assert.Contains(panes.Payloads, p => p.Contains("[ah-entry e1 to b@tm]", StringComparison.Ordinal));
    }

    /// <summary>b456: a paused member's box walks only the member-paused note: the same entry as the test above is never pasted (negative: that test, unpaused, pastes it).</summary>
    [Fact]
    public async Task APausedMembersBoxIsNotWalkedButItsPauseNoteIs()
    {
        var rig = MailClaimTests.Create(withHolds: true);
        using (Ah.StateDb.StateDbModule.Open(rig.Fx.Environment.AhHome, mayCreate: true))
        {
        }

        rig.Fx.Roster.SetPause(Ah.Roster.RosterModule.TeamFileHome(rig.TeamFile) ?? string.Empty, "tm", "b", true, Instant.ToUnixTimeMilliseconds());
        rig.Send("e1", from: "user", session: null);
        var note = MeshModule.EventRequest("b@tm", "ah", rig.Hierarchy, rig.Hierarchy, DeliveryLadder.MemberPausedEvent, "you are paused");
        note["id"] = "p1";
        Assert.True(rig.Fx.Mailbox.Send(note).Ok);
        var (ladder, _, panes) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        await Pass(rig, ladder, TimeSpan.FromMinutes(14));
        await Pass(rig, ladder, TimeSpan.FromMinutes(15));

        Assert.DoesNotContain(panes.Payloads, static p => p.Contains("[ah-entry e1 ", StringComparison.Ordinal));
        Assert.Contains(panes.Payloads, static p => p.Contains("[ah-entry p1 ", StringComparison.Ordinal));
    }

    /// <summary>b413: an entry withdrawn after its sending row is not pasted (negative: the test above, not withdrawn, is pasted once).</summary>
    [Fact]
    public async Task AnEntryWithdrawnMidPasteIsNotPasted()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1", from: "user", session: null);
        var proxy = System.Reflection.DispatchProxy.Create<IMailbox, WithdrawWhilePasting>();
        ((WithdrawWhilePasting)(object)proxy).Inner = rig.Fx.Mailbox;
        ((WithdrawWhilePasting)(object)proxy).Dir = rig.BoxB;
        var (ladder, _, panes) = LadderOf(rig, proxy);

        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        await Pass(rig, ladder, TimeSpan.FromMinutes(14));
        await Pass(rig, ladder, TimeSpan.FromMinutes(15));

        Assert.Equal(0, panes.Pastes);
        Assert.Equal("withdrawn", rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1")[^1].Get("state")!.Value.AsString());
    }

    /// <summary>b353: a paste left to the mailbox (no herdr status) is delivered by mail: one paste try, never escalated as delivery-failed.</summary>
    [Fact]
    public async Task AMailedPasteIsDeliveredByMailAndNeverEscalated()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1", from: "user", session: null);
        var (ladder, mail, panes) = LadderOf(rig);
        panes.Mailed = true;

        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        await PassSteadily(rig, ladder, TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(60));

        Assert.Equal(1, panes.Pastes);
        var rows = rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1");
        Assert.Contains(rows, r => r.Get("state") is { IsText: true } s && s.AsString() == "delivered");
        Assert.DoesNotContain(rows, r => r.Get("state") is { IsText: true } s && s.AsString() == "escalated");
        Assert.DoesNotContain(mail.Sent, m => m.Get("event") is { IsText: true } ev && ev.AsString() == "delivery-failed");
    }

    /// <summary>
    /// b403/b404: a member whose fresh activity says working, or waiting on background work, keeps an entry queued however often its
    /// pane cannot be read. A working stint keeps <c>at</c> and advances only tool_at/working_at, as the writer does, and still holds.
    /// Stale: a working record not rewritten past the believed age spends attempts and is delivery-failed.
    /// Negative: the same refusals to an idle member are delivery-failed, with every rung tried named, claim rows included.
    /// </summary>
    [Theory]
    [InlineData("working", "", "stint", false)]
    [InlineData("idle", ",\"bg_open\":1", "all", false)]
    [InlineData("working", "", "none", true)]
    [InlineData("idle", "", "all", true)]
    public async Task ARefusedPasteToABusyMemberStaysQueuedWhileItsActivityIsFresh(string activity, string fields, string refresh, bool failed)
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1", from: "user", session: null);
        // claim rows carry no rung of their own, as in b308
        rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);
        Assert.True(rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Release, new JsonObject { ["reason"] = ClaimMachine.UnreadAtIdleReason, ["actor"] = "daemon" }).Won);
        void Record(DateTimeOffset at, DateTimeOffset toolAt) => Ah.TestKit.TestActivity.Put(
            rig.Fx.Hierarchy.StateDomains,
            rig.Fx.Environment.AhHome,
            rig.Hierarchy,
            "s-b",
            $"{{\"activity\":\"{activity}\",\"at\":\"{at:yyyy-MM-ddTHH:mm:ssZ}\",\"state_since\":\"{at:yyyy-MM-ddTHH:mm:ssZ}\","
            + $"\"tool_at\":\"{toolAt:yyyy-MM-ddTHH:mm:ssZ}\",\"working_at\":\"{toolAt:yyyy-MM-ddTHH:mm:ssZ}\"{fields}}}");
        Record(Instant, Instant);
        var (ladder, mail, panes) = LadderOf(rig);
        panes.Refuse = "indeterminate";

        for (var at = TimeSpan.FromMinutes(3); at <= TimeSpan.FromMinutes(70); at += TimeSpan.FromMinutes(1))
        {
            if (refresh == "stint")
            {
                Record(Instant, Instant + at);
            }
            else if (refresh == "all")
            {
                Record(Instant + at, Instant + at);
            }

            await Pass(rig, ladder, at);
        }

        Assert.True(failed || panes.Pastes > 4, $"pastes: {panes.Pastes}");
        var rows = rig.Fx.Mailbox.LadderRowsOf(rig.BoxB, "e1");
        Assert.Equal(failed, rows.Any(r => r.Get("state") is { IsText: true } s && s.AsString() == "escalated"));
        var notices = mail.Sent.Where(m => m.Get("event") is { IsText: true } ev && ev.AsString() == "delivery-failed").ToList();
        Assert.Equal(failed ? 1 : 0, notices.Count);
        Assert.All(notices, n => Assert.Contains("rung 3 claim-relay-failed", n.Get("text")!.Value.AsString(), StringComparison.Ordinal));
        Assert.All(notices, n => Assert.DoesNotContain("rung undefined", n.Get("text")!.Value.AsString(), StringComparison.Ordinal));
    }

    /// <summary>b308: a released or relay-failed entry is pasted, never handed to the Orchestrator as a relay the mail-claim gate then refuses. Negative: the same entry with no claim record still gets its relay request.</summary>
    [Theory]
    [InlineData(ClaimStates.Released)]
    [InlineData(ClaimStates.RelayFailed)]
    [InlineData(null)]
    public async Task AnEntryReleasedForNoPollerGetsNoRelayRequestAndAnUnclaimedOneDoes(string? state)
    {
        var rig = MailClaimTests.Create(state is null ? "{\"single_carrier\":false}\n" : null);
        // A member with no kind is relayed by SendMessage; one with a kind is delivered by the roster instead.
        File.WriteAllText(rig.TeamFile, File.ReadAllText(rig.TeamFile).Replace("\"role\":\"reviewer\",\"kind\":\"claude\"", "\"role\":\"reviewer\"", StringComparison.Ordinal));
        rig.Send("e1", from: "user", session: null);
        if (state == ClaimStates.RelayFailed)
        {
            // the unread-at-idle release: a fallback-claimed entry whose session sat idle without taking it
            rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Advance);
            Assert.True(rig.Fx.Mailbox.Claim(rig.BoxB, "e1", ClaimActions.Release, new JsonObject { ["reason"] = ClaimMachine.UnreadAtIdleReason, ["actor"] = "daemon" }).Won);
        }

        var (ladder, mail, _) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        await Pass(rig, ladder, TimeSpan.FromMinutes(14));
        await Pass(rig, ladder, TimeSpan.FromMinutes(15));

        Assert.Equal(state is null, mail.Sent.Any(m => m.Get("event") is { IsText: true } ev && ev.AsString() == "relay-needed"));
    }

    private static async Task<(MailClaimTests.Rig Rig, Mailer Mail, NoPanes Panes, string Box)> ReleasedToTheOrchestrator(string from)
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1", from: from, to: "orchestrator@tm", session: null);
        var box = rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "orchestrator");
        var (ladder, mail, panes) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        Assert.Equal(ClaimStates.Released, rig.Claim("e1", box)!.Get("state")!.Value.AsString());
        await Pass(rig, ladder, TimeSpan.FromMinutes(14));
        await Pass(rig, ladder, TimeSpan.FromMinutes(15));
        await Pass(rig, ladder, TimeSpan.FromMinutes(30));

        Assert.Contains(rig.Fx.Mailbox.LadderRowsOf(box, "e1"), r => r.Get("state") is { IsText: true } s && s.AsString() == "escalated");
        return (rig, mail, panes, box);
    }

    [Fact]
    public async Task AReleasedEntryToABoxWithNoMemberRowTellsItsSenderOnceAndStaysUnacked()
    {
        var (rig, mail, panes, box) = await ReleasedToTheOrchestrator("a@tm");

        var told = Assert.Single(mail.Sent, m => m.Get("event") is { IsText: true } ev && ev.AsString() == "delivery-failed");
        Assert.Equal("a@tm", told["to"].AsString());
        Assert.Contains("delivery-failed: note e1 from a@tm to orchestrator@tm", told["text"].AsString(), StringComparison.Ordinal);
        Assert.Equal("e1", told["data"].AsObject()["entry"].AsString());
        Assert.False(rig.Fx.Mailbox.IsAcked(box, "e1"));
        Assert.Equal(0, panes.Pastes);
    }

    [Fact]
    public async Task NegativeAnEntryFromTheUserToABoxWithNoMemberRowTellsNoOne()
    {
        var (rig, mail, _, box) = await ReleasedToTheOrchestrator("user");

        Assert.Empty(mail.Sent);
        Assert.False(rig.Fx.Mailbox.IsAcked(box, "e1"));
    }

    [Fact]
    public async Task ANonClaudeReceiverIsPastedTheEntryUnchangedEvenWhenANewerNoteFromTheSameSenderExists()
    {
        var rig = MailClaimTests.Create(receiverHarness: "codex");
        rig.Send("e1");
        rig.Send("e2", text: "a newer note");
        var (ladder, mail, panes) = LadderOf(rig);

        await Pass(rig, ladder, TimeSpan.Zero);
        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        await Pass(rig, ladder, TimeSpan.FromMinutes(6));

        Assert.DoesNotContain(mail.Sent, m => m.Get("event") is { IsText: true } ev && ev.AsString() == "relay-needed");
        Assert.Contains("ah mail from a@tm to b@tm (note): hello e1 [ah-entry e1 to b@tm]", panes.Payloads);
        Assert.All(panes.Payloads, p => Assert.DoesNotContain("(relayed:", p, StringComparison.Ordinal));
        Assert.All(panes.Payloads, p => Assert.DoesNotContain("no longer needed", p, StringComparison.Ordinal));
    }

    private static string OrchestratorSession(MailClaimTests.Rig rig)
    {
        var box = rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "orchestrator");
        Assert.True(rig.Fx.Mailbox.WriteRegistration(new RegistrationWrite("o1", [new BoxRef("orchestrator@tm", box)], rig.TeamFile, "claude", Environment.ProcessId)));
        return box;
    }

    private static async Task PollAndPass(MailClaimTests.Rig rig, DeliveryLadder ladder, string box, TimeSpan after, string adapter = "claude-mod", string session = "o1")
    {
        rig.Fx.Clock.UtcNow = Instant + after;
        rig.Fx.Mailbox.WriteHeartbeat(box, adapter, session, 5000);
        await Pass(rig, ladder, after);
    }

    private static bool Escalated(MailClaimTests.Rig rig, string box, string id = "e1") =>
        rig.Fx.Mailbox.LadderRowsOf(box, id).Any(r => r.Get("state") is { IsText: true } s && s.AsString() == "escalated");

    private static bool ToldFailed(Mailer mail) =>
        mail.Sent.Any(m => m.Get("event") is { IsText: true } ev && ev.AsString() == "delivery-failed");

    [Fact]
    public async Task AFreshDoorbellHeartbeatHoldsAnOrchestratorEntryWithNoRungAndNoNotice()
    {
        var rig = MailClaimTests.Create();
        var box = OrchestratorSession(rig);
        var (ladder, mail, panes) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero);
        rig.Send("e1", to: "orchestrator@tm");

        foreach (var minutes in new[] { 3d, 14, 15, 30 })
        {
            await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(minutes));
        }

        Assert.Empty(rig.Fx.Mailbox.LadderRowsOf(box, "e1"));
        Assert.Equal(ClaimStates.Pending, rig.Claim("e1", box)!.Get("state")!.Value.AsString());
        Assert.Empty(mail.Sent);
        Assert.Equal(0, panes.Pastes);
        Assert.False(rig.Fx.Mailbox.IsAcked(box, "e1"));
    }

    [Fact]
    public async Task AnEntryTheSessionReadWhileHeldReadsAckedNotPending()
    {
        var rig = MailClaimTests.Create();
        var box = OrchestratorSession(rig);
        var (ladder, _, _) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero);
        rig.Send("e1", to: "orchestrator@tm");
        await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(3));
        CallerBox[] caller = [new CallerBox("orchestrator@tm", box)];
        Assert.Equal("routed", rig.Fx.Mailbox.EntryStatus("e1", caller)!["state"].AsString());

        _ = rig.Fx.Mailbox.AckIds(box, ["e1"], "next", "o1");
        await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(4));

        Assert.Equal("acked", rig.Fx.Mailbox.EntryStatus("e1", caller)!["state"].AsString());
        Assert.Equal("delivered", rig.Fx.Mailbox.LadderRowsOf(box, "e1")[^1].Get("state")!.Value.AsString());
    }

    [Fact]
    public async Task NegativeAStaleDoorbellHeartbeatEscalatesAsBefore()
    {
        var rig = MailClaimTests.Create();
        var box = OrchestratorSession(rig);
        var (ladder, mail, _) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero);
        rig.Send("e1", to: "orchestrator@tm");

        // A limit of quiet is waited out with no rows; only then, with no pane to wake, does the walk start.
        await Pass(rig, ladder, TimeSpan.FromMinutes(3));
        Assert.Empty(rig.Fx.Mailbox.LadderRowsOf(box, "e1"));
        foreach (var minutes in new[] { 14d, 15, 30, 31, 32 })
        {
            await Pass(rig, ladder, TimeSpan.FromMinutes(minutes));
        }

        Assert.True(Escalated(rig, box));
        Assert.True(ToldFailed(mail));
    }

    [Fact]
    public async Task TheHoldEndsOnlyOnceTheHeartbeatIsOlderThanALimit()
    {
        var rig = MailClaimTests.Create();
        var box = OrchestratorSession(rig);
        var (ladder, _, _) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero);
        rig.Send("e1", to: "orchestrator@tm");
        await PollAndPass(rig, ladder, box, TimeSpan.FromSeconds(60));

        await PassSteadily(rig, ladder, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(659));
        Assert.Equal(ClaimStates.Pending, rig.Claim("e1", box)!.Get("state")!.Value.AsString());

        await Pass(rig, ladder, TimeSpan.FromSeconds(661));
        Assert.Equal(ClaimStates.Released, rig.Claim("e1", box)!.Get("state")!.Value.AsString());
    }

    [Fact]
    public async Task ASessionThatComesBackMidLadderStopsItBeforeAnyFailure()
    {
        var rig = MailClaimTests.Create();
        var box = OrchestratorSession(rig);
        var (ladder, mail, _) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero);
        rig.Send("e1", to: "orchestrator@tm");
        await PassSteadily(rig, ladder, TimeSpan.Zero, TimeSpan.FromMinutes(11));
        Assert.NotEmpty(rig.Fx.Mailbox.LadderRowsOf(box, "e1"));

        foreach (var minutes in new[] { 14d, 15, 30 })
        {
            await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(minutes));
        }

        Assert.False(Escalated(rig, box));
        Assert.False(ToldFailed(mail));
    }

    [Fact]
    public async Task NegativeAFreshHeartbeatOfAnotherAdapterDoesNotHold()
    {
        var rig = MailClaimTests.Create();
        var box = OrchestratorSession(rig);
        var (ladder, mail, _) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero, adapter: "codex");
        rig.Send("e1", to: "orchestrator@tm");

        foreach (var minutes in new[] { 14d, 15, 30, 31, 32 })
        {
            await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(minutes), adapter: "codex");
        }

        Assert.True(Escalated(rig, box));
        Assert.True(ToldFailed(mail));
    }

    [Fact]
    public async Task NegativeAFreshHeartbeatOfASessionNotRegisteredForTheBoxDoesNotHold()
    {
        var rig = MailClaimTests.Create();
        var box = rig.Fx.Mailbox.BoxDirFor(rig.TeamFile, "orchestrator");
        var (ladder, _, _) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero, session: "s-b");
        rig.Send("e1", to: "orchestrator@tm");

        foreach (var minutes in new[] { 3d, 14, 15, 30 })
        {
            await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(minutes), session: "s-b");
        }

        Assert.True(Escalated(rig, box));
    }

    [Fact]
    public async Task NegativeAnEntryForAnotherSessionIsNotHeldByThisSessionsHeartbeat()
    {
        var rig = MailClaimTests.Create();
        var box = OrchestratorSession(rig);
        rig.Fx.Mailbox.AppendEntry(
            box,
            new JsonObject { ["v"] = 1, ["id"] = "old", ["from"] = "a@tm", ["to"] = "orchestrator@tm", ["to_session"] = "gone", ["kind"] = "note", ["route"] = "fallback", ["created"] = "2026-01-01T12:00:00.000Z", ["text"] = "t" },
            null);
        var (ladder, _, _) = LadderOf(rig);

        await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(3));

        Assert.NotEmpty(rig.Fx.Mailbox.LadderRowsOf(box, "old"));
    }

    [Fact]
    public async Task NegativeWithTheDoorbellSwitchedOffAFreshHeartbeatDoesNotHold()
    {
        var rig = MailClaimTests.Create(doorbell: false);
        var box = OrchestratorSession(rig);
        var (ladder, _, _) = LadderOf(rig);
        await PollAndPass(rig, ladder, box, TimeSpan.Zero);
        rig.Send("e1", from: "user", to: "orchestrator@tm", session: null);

        await PollAndPass(rig, ladder, box, TimeSpan.FromMinutes(3));

        Assert.Equal(ClaimStates.Released, rig.Claim("e1", box)!.Get("state")!.Value.AsString());
    }

    [Fact]
    public async Task NegativeAMemberBoxWithAFreshModHeartbeatIsStillReleasedAndPasted()
    {
        var rig = MailClaimTests.Create();
        rig.Send("e1", from: "user", session: null);
        var (ladder, _, panes) = LadderOf(rig);

        await PollAndPass(rig, ladder, rig.BoxB, TimeSpan.FromMinutes(3), session: "s-b");
        Assert.Equal(ClaimStates.Released, rig.Claim("e1")!.Get("state")!.Value.AsString());
        await PollAndPass(rig, ladder, rig.BoxB, TimeSpan.FromMinutes(14), session: "s-b");
        await PollAndPass(rig, ladder, rig.BoxB, TimeSpan.FromMinutes(15), session: "s-b");

        Assert.Equal(1, panes.Pastes);
    }
}
