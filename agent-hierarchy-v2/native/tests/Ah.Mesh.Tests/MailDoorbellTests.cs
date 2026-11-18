using System.Globalization;
using System.Runtime.Versioning;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.TestKit;

namespace Ah.Mesh.Tests;

/// <summary>The doorbell: one ring per burst, re-rings until the session reads, reminders after, and the pull that drains the queue.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class MailDoorbellTests
{
    internal const double T0 = 1_790_000_000_000;

    internal const string Session = "s1";

    private static readonly DateTimeOffset Clock = DateTimeOffset.FromUnixTimeMilliseconds((long)T0);

    internal sealed class Rig
    {
        internal Rig(int pid = -1, Func<IMailboxHooks, IMailboxHooks>? wrapHooks = null)
        {
            var sandbox = UnitSandbox.Create();
            Root = sandbox.Root;
            Home = Root + "/ah";
            Directory.CreateDirectory(Home + "/registrations");
            var vars = new Dictionary<string, string> { ["HOME"] = sandbox.Home, ["AH_HOME"] = Home };
            Fx = MeshFixture.Create(vars, Clock, Root);
            Bell = MeshModule.CreateMailDoorbell(Fx.Mailbox, Fx.Core, Fx.Files, Fx.Environment, wrapHooks?.Invoke(Fx.Hooks));
            Directory.CreateDirectory(Dir("orchestrator"));
            var boxes = new JsonObject { ["orchestrator@tm"] = Dir("orchestrator") };
            var reg = new JsonObject
            {
                ["session_id"] = Session,
                ["address"] = "orchestrator@tm",
                ["addresses"] = new JsonArray { "orchestrator@tm" },
                ["boxes"] = boxes,
                ["pid"] = (double)(pid < 0 ? Environment.ProcessId : pid),
            };
            File.WriteAllText($"{Home}/registrations/{Session}.json", JsonText.Stringify(reg, 0));
        }

        private readonly Dictionary<string, string> _ids = [];

        internal MeshFixture Fx { get; }

        internal IMailDoorbell Bell { get; }

        internal string Root { get; }

        internal string Home { get; }

        internal string Dir(string name) => $"{Home}/mailbox/tm~h/{name}";

        internal static string Iso(double ms) => Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)ms));

        // entry ids sort by arrival, as real ones do: the read and told marks compare them as strings
        internal string Full(string id) => _ids.TryGetValue(id, out var full) ? full : id;

        // Plain mail defaults to a report: a note is a digest item (rank 5) that never rings by itself.
        internal void Add(string id, double createdMs, string kind = "report",string text = "hi", string? ev = null, string? reference = null, string? requestId = null, string? from = null)
        {
            var sortable = ((long)createdMs).ToString("D13", CultureInfo.InvariantCulture) + "-" + id;
            _ids[id] = sortable;
            var entry = new JsonObject { ["id"] = sortable, ["kind"] = kind, ["from"] = "someone", ["to"] = "orchestrator@tm", ["created"] = Iso(createdMs), ["text"] = text, ["route"] = "pull" };
            if (ev is not null)
            {
                entry["event"] = ev;
                entry["from"] = from ?? "ahd";
            }

            if (reference is not null)
            {
                entry["ref"] = reference;
            }

            if (requestId is not null)
            {
                entry["data"] = new JsonObject { ["request_id"] = requestId };
            }

            File.AppendAllText(Dir("orchestrator") + "/entries.jsonl", JsonText.Stringify(entry, 0) + "\n");
        }

        internal bool Acked(string id) => File.Exists(Dir("orchestrator") + "/acks/" + Full(id));

        internal DoorbellDecision Decide(double now, double? busyAt = null, DoorbellCarrier carrier = DoorbellCarrier.Prompt, Dictionary<string, HandedState>? handed = null, string? hier = null) =>
            Bell.Decide(new DoorbellRequest(Session, now, busyAt, handed ?? [], carrier) { HierarchyDir = hier });

        internal NextResult Pull(double now, bool peek = false, bool again = false, string? team = null) => Bell.Pull(Session, now, peek, again, team);

        internal string Blocked(string name)
        {
            var path = Root + "/" + name + ".md";
            File.WriteAllText(path, "## [0] tldr\n- x\n\n## [1] status\n- BLOCKED: waiting on a decision\n");
            return path;
        }
    }

    private const double PastLimit = T0 - IMailCore.DoorbellRemindMs - 60_000;

    /// <summary>The session the ladder should wake for mail gone unread: a fresh poll holds the doorbell live either way.</summary>
    private static string? GoneUnread(Rig rig, double now = T0)
    {
        rig.Fx.Mailbox.WriteHeartbeat(rig.Dir("orchestrator"), MailDue.Adapter, Session, 5000);
        Assert.True(rig.Fx.Mailbox.DoorbellLive(rig.Dir("orchestrator"), "orchestrator@tm", null, now));
        return rig.Fx.Mailbox.DoorbellGoneUnread(rig.Dir("orchestrator"), "orchestrator@tm", null, now);
    }

    private static DoorbellDecision DecideHeld(Rig rig, double now, bool held = true) =>
        rig.Bell.Decide(new DoorbellRequest(Session, now, null, new Dictionary<string, HandedState>(), DoorbellCarrier.Prompt) { Held = held });

    [Fact]
    public void RingsLeftUnreadPastTheLimitAskForAWake()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(PastLimit).Ring);

        Assert.Equal(Session, GoneUnread(rig));
    }

    [Fact]
    public void NegativeRingsUnreadForLessThanTheLimitAskForNoWake()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(T0 - IMailCore.DoorbellRemindMs + 1000).Ring);

        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void NegativeAReReadQueueAsksForNoWake()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(PastLimit).Ring);
        _ = rig.Pull(T0 - 1000, peek: true);

        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void NegativeASessionThatWentBusyAsksForNoWake()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(PastLimit).Ring);
        _ = rig.Decide(T0 - 1000, busyAt: T0 - 2000);

        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void NegativeATurnBetweenRestartsTheClock()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(PastLimit).Ring);
        _ = rig.Decide(T0 - 2000, busyAt: T0 - 3000);
        _ = rig.Decide(T0 - 1000);

        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void AHeldSessionIsNotRungButItsOldMailAsksForTheWake()
    {
        // ah2-180 B3 (was: asks for no wake): a held session's prompt rings are dropped, so its mail goes to the pane wake.
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(PastLimit).Ring);
        rig.Add("s", T0 - 120_000);

        var held = DecideHeld(rig, T0);

        Assert.Null(held.Ring);
        Assert.Equal(Session, GoneUnread(rig));
    }

    // A state from before the clock, or with its reminders run out: nothing would ring again.
    private static void RemindersSpent(Rig rig, double ackedAt = 0, string rangTurn = "")
    {
        Directory.CreateDirectory(rig.Home + "/mailbox-notes/doorbell");
        File.WriteAllText(
            rig.Home + "/mailbox-notes/doorbell/" + Session + ".json",
            "{\"v\":1,\"seq\":9,\"ring_id\":\"doorbell:9\",\"rang_at\":1,\"rang_turn\":\"" + rangTurn + "\",\"told_mark\":\"9\",\"remind_step\":6,\"escalated\":" + (ackedAt > 0 ? "false" : "true")
            + ",\"ring_acked_at\":" + ackedAt.ToString("R", CultureInfo.InvariantCulture) + "}");
    }

    [Fact]
    public void MailWithNoRingDueGetsOneTriedRingBeforeAnyWake()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        RemindersSpent(rig);
        Assert.Null(rig.Decide(PastLimit).Ring);
        Assert.Null(rig.Decide(T0 - 61_000).Ring);
        Assert.Null(GoneUnread(rig, T0 - 60_500));

        Assert.NotNull(rig.Decide(T0 - 60_000).Ring);
        Assert.Null(GoneUnread(rig, T0 - 60_000 + IMailCore.DoorbellReringMs - 1));

        Assert.Equal(Session, GoneUnread(rig, T0 - 60_000 + IMailCore.DoorbellReringMs));
    }

    [Fact]
    public void NegativeARingThatEnteredForcesNoRingAndAsksForNoWake()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        RemindersSpent(rig, ackedAt: 2);
        _ = rig.Decide(PastLimit);

        Assert.Null(rig.Decide(T0).Ring);
        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void NegativeARingWhoseTurnRanUnreadForcesNoRing()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        RemindersSpent(rig, rangTurn: "1700000000000");
        _ = rig.Decide(PastLimit);

        Assert.Null(rig.Decide(T0).Ring);
        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void NegativeARingCarriedByAStopAsksForNoWake()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(PastLimit).Ring);
        rig.Add("s", T0 - 90_000);

        Assert.NotNull(rig.Decide(T0 - 60_000, carrier: DoorbellCarrier.Stop).Ring);

        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void NegativeABusySpellThatTheRingDidNotStartRestartsTheClock()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.NotNull(rig.Decide(PastLimit).Ring);

        // A turn from before the ring is still running, so the ring is not marked entered; only the busy spell itself clears the clock.
        _ = rig.Decide(T0 - 2000, busyAt: PastLimit - 1000);
        Assert.NotNull(rig.Decide(T0 - 1000).Ring);

        Assert.Null(GoneUnread(rig, T0 - 1000 + IMailCore.DoorbellReringMs));
    }

    [Fact]
    public void ARingThatDropsGoesToThePaneWake()
    {
        // ah2-180 B3 (was: asks for no wake): the drop marks the session held, and held mail this old goes to the pane wake.
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        RemindersSpent(rig);
        _ = rig.Decide(PastLimit);
        Assert.NotNull(rig.Decide(T0 - 60_000).Ring);

        _ = DecideHeld(rig, T0 - 55_000);

        Assert.Equal(Session, GoneUnread(rig));
    }

    [Fact]
    public void AlarmsAloneStartTheClockForAnOrchestrator()
    {
        // ah2-180 D3 (Amendment 3): an Orchestrator's alarm left unread reaches the wake as a report does.
        var rig = new Rig();
        rig.Add("ev", PastLimit - 120_000, kind: "event", text: "check-in-due: w", ev: "check-in-due");
        _ = rig.Decide(PastLimit);

        Assert.Equal(Session, GoneUnread(rig));
    }

    // ah2-180 B1/B3: a held lead with one alarm and nothing else is not rung, and goes to the wake once that alarm is HeldWakeMs old.
    [Fact]
    public void AHeldLeadWithOnlyAnAlarmIsWokenOnceTheAlarmIsHeldWakeMsOld()
    {
        var rig = new Rig();
        var at = T0 - MailDoorbell.HeldWakeMs - 10_000;
        rig.Add("ev", at, kind: "event", text: "check-in-due: w", ev: "check-in-due");

        Assert.Null(DecideHeld(rig, at + 1000).Ring);
        Assert.Null(GoneUnread(rig, at + MailDoorbell.HeldWakeMs - 1000));
        Assert.Equal(Session, GoneUnread(rig, at + MailDoorbell.HeldWakeMs));
    }

    // ah2-180 Amendment 3 (review N2): a held session seen in a turn has resumed and is not woken as held.
    [Fact]
    public void NegativeAHeldSessionSeenInATurnIsNotWokenAsHeld()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        _ = rig.Bell.Decide(new DoorbellRequest(Session, T0 - 1000, T0 - 2000, new Dictionary<string, HandedState>(), DoorbellCarrier.Prompt) { Held = true });

        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void NegativeAStopThatLiftedIsNotWokenAsHeld()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        _ = DecideHeld(rig, PastLimit);
        _ = DecideHeld(rig, PastLimit + 1000, held: false);

        Assert.Null(GoneUnread(rig, PastLimit + 1000 + MailDoorbell.HeldWakeMs));
    }

    // ah2-180 C1's reader: the last uncarried mark, or nothing.
    [Fact]
    public void TheUncarriedMarkIsReadBackAndASessionWithoutOneHasNone()
    {
        var rig = new Rig();
        Assert.Null(rig.Fx.Mailbox.DoorbellUncarriedMark(Session));

        rig.Fx.Mailbox.MarkDoorbellUncarried(Session, T0, "no-pane");

        Assert.Equal((T0, "no-pane"), rig.Fx.Mailbox.DoorbellUncarriedMark(Session));
    }

    // ah2-180 A4: the told-again work alarms are alarm-class: a busy turn's tool carrier does not ring them, as it rings a report.
    [Theory]
    [InlineData("report-waiting", false)]
    [InlineData("work-still-waiting", false)]
    [InlineData(null, true)]
    public void TheWorkAlarmsAreAlarmClass(string? ev, bool toolRings)
    {
        var rig = new Rig();
        if (ev is null)
        {
            rig.Add("r", T0 - 60_000);
        }
        else
        {
            rig.Add("w", T0 - 60_000, kind: "event", text: ev + ": x", ev: ev);
        }

        Assert.Equal(toolRings, rig.Decide(T0, busyAt: T0 - 1000, carrier: DoorbellCarrier.Tool).Ring is not null);
    }

    [Fact]
    public void NegativeAnAlarmInAMembersBoxStartsNoWake()
    {
        var rig = new Rig();
        var member = rig.Home + "/mailbox/tm~h/m1";
        Directory.CreateDirectory(member);
        rig.Fx.Mailbox.WriteHeartbeat(member, MailDue.Adapter, Session, 5000);

        Assert.Null(rig.Fx.Mailbox.DoorbellGoneUnread(member, "m1@tm", null, T0));
    }

    [Fact]
    public void AHeldAnswerCountsItsUnreadAndANegativeOneCountsNothing()
    {
        var rig = new Rig();
        rig.Add("r", T0 - 120_000);
        rig.Add("ev", T0 - 110_000, kind: "event", text: "check-in-due: w", ev: "check-in-due");

        Assert.Equal(2, DecideHeld(rig, T0).Unread);
        Assert.Null(DecideHeld(rig, T0 + 1000, held: false).Unread);
    }

    [Fact]
    public void ReadingTheRingInlineStopsTheClock()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.True(rig.Decide(PastLimit).Ring!.Inline);
        _ = rig.Decide(PastLimit + 500);
        Directory.CreateDirectory(rig.Dir("orchestrator") + "/acks");
        File.WriteAllText(rig.Dir("orchestrator") + "/acks/" + rig.Full("r"), string.Empty);
        rig.Add("s", PastLimit + 600);

        _ = rig.Decide(PastLimit + 1000);

        Assert.Null(GoneUnread(rig));
    }

    [Fact]
    public void AStoppedSessionIsMarkedOnceAfterTheLimit()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);

        Assert.Null(DecideHeld(rig, PastLimit).Escalation);
        Assert.Null(DecideHeld(rig, T0 - 120_000).Escalation);
        var told = DecideHeld(rig, T0).Escalation;
        Assert.NotNull(told);
        Assert.Contains("is stopped with 1 unread", told!.Text, StringComparison.Ordinal);
        Assert.Null(DecideHeld(rig, T0 + 1000).Escalation);
        Assert.Null(DecideHeld(rig, T0 + IMailCore.DoorbellRemindMs + 1000).Escalation);
    }

    [Fact]
    public void NegativeAStopThatLiftedRestartsItsMark()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);

        _ = DecideHeld(rig, PastLimit);
        _ = DecideHeld(rig, T0 - 1000, held: false);

        Assert.Null(DecideHeld(rig, T0).Escalation);
    }

    [Fact]
    public void AMarkOlderThanTheUnreadSpellToastsNothing()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        rig.Fx.Mailbox.MarkDoorbellUncarried(Session, PastLimit - 1000, "no-pane");

        Assert.Null(rig.Decide(PastLimit).Escalation);
        rig.Fx.Mailbox.MarkDoorbellUncarried(Session, PastLimit + 1000, "no-pane");
        var told = rig.Decide(PastLimit + 2000).Escalation;
        Assert.Contains("no-pane", told!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeTheSameMailRingsWhenTheSessionIsNotHeld()
    {
        var rig = new Rig();
        rig.Add("r", T0 - 120_000);

        Assert.NotNull(DecideHeld(rig, T0, held: false).Ring);
    }

    [Fact]
    public void AStoppedSessionWithOnlyAnAlarmIsMarkedAfterTheLimit()
    {
        var rig = new Rig();
        rig.Add("ev", PastLimit - 120_000, kind: "event", text: "check-in-due: w", ev: "check-in-due");

        Assert.Null(DecideHeld(rig, PastLimit).Escalation);
        Assert.Null(DecideHeld(rig, T0 - 120_000).Escalation);
        var told = DecideHeld(rig, T0).Escalation;
        Assert.NotNull(told);
        Assert.Equal("mail-unacked", told!.Kind);
        Assert.Contains("is stopped with 1 unread; type a message to continue", told.Text, StringComparison.Ordinal);
        Assert.Null(DecideHeld(rig, T0 + 1000).Escalation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStoppedMarkSaysWhenTheWakeCouldNotType(bool failedInEpisode)
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        _ = DecideHeld(rig, PastLimit);
        rig.Fx.Mailbox.MarkDoorbellUncarried(Session, failedInEpisode ? PastLimit + 1000 : PastLimit - 1000, "no-pane");

        var told = DecideHeld(rig, T0).Escalation!.Text;

        Assert.Equal(failedInEpisode, told.Contains("; ah could not wake it", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAnAlarmAloneIsNotMarkedWhenTheSessionIsNotHeld()
    {
        var rig = new Rig();
        rig.Add("ev", PastLimit - 120_000, kind: "event", text: "check-in-due: w", ev: "check-in-due");

        _ = DecideHeld(rig, PastLimit, held: false);
        Assert.Null(DecideHeld(rig, T0, held: false).Escalation);
        Assert.Null(DecideHeld(rig, T0 + IMailCore.DoorbellRemindMs, held: false).Escalation);
    }

    // ah2-180 D1: stopped first, then a report, then an alarm: one mark past the limit, counting both.
    [Fact]
    public void AStopThenAReportThenAnAlarmIsMarkedOnceCountingBoth()
    {
        var rig = new Rig();
        rig.Add("r", PastLimit - 120_000);
        Assert.Null(DecideHeld(rig, PastLimit).Escalation);
        rig.Add("ev", PastLimit + 60_000, kind: "event", text: "check-in-due: w", ev: "check-in-due");
        Assert.Null(DecideHeld(rig, PastLimit + 61_000).Escalation);

        var told = DecideHeld(rig, T0).Escalation;

        Assert.Contains("is stopped with 2 unread", told!.Text, StringComparison.Ordinal);
        Assert.Null(DecideHeld(rig, T0 + 1000).Escalation);
    }

    [Fact]
    public void TheTraceRecordsAHeldSessionAndWhyItWasNotRung()
    {
        var rig = new Rig();
        rig.Add("r", T0 - 120_000);

        _ = DecideHeld(rig, T0);

        var row = JsonText.Parse(File.ReadAllLines(Mailbox.TraceFile(rig.Home, Session)).Last(l => l.Contains("doorbell-trace", StringComparison.Ordinal))).AsObject();
        Assert.True(row.Get("held")!.Value.AsBool());
        Assert.Equal("held", row.Get("not_rung")!.Value.AsString());
    }

    [Fact]
    public void NegativeTheTraceOfASessionNotHeldGivesNoHeldReason()
    {
        var rig = new Rig();
        rig.Add("r", T0 - 120_000);

        Assert.NotNull(DecideHeld(rig, T0, held: false).Ring);

        var row = JsonText.Parse(File.ReadAllLines(Mailbox.TraceFile(rig.Home, Session)).Last(l => l.Contains("doorbell-trace", StringComparison.Ordinal))).AsObject();
        Assert.False(row.Get("held")!.Value.AsBool());
        Assert.True(row.Get("not_rung")!.Value.IsNull);
    }

    private static void Notes(Rig rig, int count, double createdMs = T0 - 60_000, string prefix = "n")
    {
        for (var i = 1; i <= count; i++)
        {
            rig.Add(prefix + i.ToString("D2", CultureInfo.InvariantCulture), createdMs + i);
        }
    }

    [Fact]
    public void TenQueuedFiveReadFourArriveGivesOneNoticeAndFooter()
    {
        var rig = new Rig();
        Notes(rig, 10);

        var first = rig.Decide(T0);
        Assert.NotNull(first.Ring);
        Assert.Equal("ah: You have 10 messages (10 reports). Run `ah msg next`.", first.Ring!.Text);

        for (var i = 1; i <= 5; i++)
        {
            var read = rig.Pull(T0 + (i * 1000));
            Assert.Equal(10 - i, read.Left);
        }

        Notes(rig, 4, T0 + 20_000, "m");
        Assert.Null(rig.Decide(T0 + 23_000).Ring);
        var second = rig.Decide(T0 + 61_000);
        Assert.Equal("ah: 4 new; 9 unread (9 reports). Run `ah msg next`.", second.Ring!.Text);
        Assert.Null(rig.Decide(T0 + 62_000).Ring);
        Assert.Null(rig.Decide(T0 + 63_000, handed: new Dictionary<string, HandedState> { [second.Ring.Id] = new("pending", T0 + 61_000) }).Ring);

        var next = rig.Pull(T0 + 64_000);
        Assert.EndsWith("queue: 8 left · 4 new since your last read", next.Text, StringComparison.Ordinal);
        Assert.Equal(8, next.Left);
        Assert.Equal(4, next.New);
    }

    [Fact]
    public void AFootersNewCountOnlyCountsArrivalsSinceTheLastRead()
    {
        var rig = new Rig();
        Notes(rig, 3);

        _ = rig.Pull(T0);
        var second = rig.Pull(T0 + 1000);

        Assert.Equal(0, second.New);
        Assert.EndsWith("queue: 1 left · 0 new since your last read", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoPendingAnswersTakeTwoPullsEachShowingAndAckingOne()
    {
        var rig = new Rig();
        rig.Add("qa1", T0 - 6000, kind: "event", text: "_run:a answered: Yes", ev: "question-answered", from: "user");
        rig.Add("qa2", T0 - 1000, kind: "event", text: "_run:b answered: No", ev: "question-answered", from: "user");

        var first = rig.Pull(T0);
        Assert.Contains("_run:a answered: Yes", first.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("_run:b", first.Text, StringComparison.Ordinal);
        Assert.True(rig.Acked("qa1"));
        Assert.False(rig.Acked("qa2"));
        Assert.Equal(1, first.Left);

        var second = rig.Pull(T0 + 1000);
        Assert.Contains("_run:b answered: No", second.Text, StringComparison.Ordinal);
        Assert.True(rig.Acked("qa2"));
        Assert.True(rig.Pull(T0 + 2000).Empty);
    }

    [Fact]
    public void NegativeAnotherEventKindBesideAnAnswerStillGroups()
    {
        var rig = new Rig();
        rig.Add("qa1", T0 - 6000, kind: "event", text: "_run:a answered: Yes", ev: "question-answered", from: "user");
        rig.Add("mb1", T0 - 5000, kind: "event", text: "member-bare: worker-1", ev: "member-bare");
        rig.Add("mb2", T0 - 4000, kind: "event", text: "member-bare: worker-2", ev: "member-bare");

        Assert.Contains("member-bare x2", rig.Pull(T0, peek: true).Text, StringComparison.Ordinal);
        var pulled = rig.Pull(T0 + 1000);

        Assert.Contains("member-bare: 2 entries", pulled.Text, StringComparison.Ordinal);
        Assert.True(rig.Acked("qa1") && rig.Acked("mb1") && rig.Acked("mb2"));
        Assert.True(rig.Pull(T0 + 2000).Empty);
    }

    [Fact]
    public void ABurstOfThirtyEventsRingsOnceAndIsOneItemThatOnePullDrains()
    {
        var rig = new Rig();
        for (var i = 0; i < 30; i++)
        {
            rig.Add("ev" + i.ToString("D2", CultureInfo.InvariantCulture), T0 - 60_000 + (i * 60), kind: "event", text: "member-bare: worker-" + i, ev: "member-bare");
        }

        var ring = rig.Decide(T0).Ring;

        Assert.Equal("ah: You have 1 message (1 alarm). Run `ah msg next`.", ring!.Text);
        var pulled = rig.Pull(T0 + 1000);
        Assert.Contains("worker-0", pulled.Text, StringComparison.Ordinal);
        Assert.Contains("worker-29", pulled.Text, StringComparison.Ordinal);
        Assert.True(Enumerable.Range(0, 30).All(i => rig.Acked("ev" + i.ToString("D2", CultureInfo.InvariantCulture))));
        Assert.True(rig.Pull(T0 + 2000).Empty);
    }

    [Fact]
    public void TwentyCopiedUpLaneEscalationsAreOneGroupedItem()
    {
        var rig = new Rig();
        for (var i = 0; i < 20; i++)
        {
            rig.Add("ev" + i.ToString("D2", CultureInfo.InvariantCulture), T0 - 60_000 + i, kind: "event", text: "escalated from so-m: lane b" + i, ev: "lane-escalated");
        }

        Assert.Equal("ah: You have 1 message (1 alarm). Run `ah msg next`.", rig.Decide(T0).Ring!.Text);
    }

    [Fact]
    public void ThirtyEventsOfFourKindsAreFourItemsAndOneRing()
    {
        var rig = new Rig();
        var kinds = new[] { "member-bare", "stalled-after-compact", "unread-at-idle", "queue-stall" };
        for (var i = 0; i < 30; i++)
        {
            rig.Add("ev" + i.ToString("D2", CultureInfo.InvariantCulture), T0 - 60_000 + i, kind: "event", text: "x", ev: kinds[i % 4]);
        }

        Assert.Equal("ah: You have 4 messages (4 alarms). Run `ah msg next`.", rig.Decide(T0).Ring!.Text);
    }

    [Fact]
    public void ABlockedReportRingsAtOnceEvenInsideTheQuietWindowAndAfterARing()
    {
        var rig = new Rig();
        Notes(rig, 2);
        Assert.NotNull(rig.Decide(T0).Ring);

        rig.Add("b1", T0 + 1000, kind: "report", reference: rig.Blocked("resp"));
        var ring = rig.Decide(T0 + 1500).Ring;

        Assert.NotNull(ring);
        Assert.True(ring!.Urgent);
        Assert.Contains("1 new", ring.Text, StringComparison.Ordinal);
        Assert.Contains("1 blocked", ring.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonUrgentArrivalInsideTheMinimumGapWaitsForTheGap()
    {
        var rig = new Rig();
        Notes(rig, 2);
        Assert.NotNull(rig.Decide(T0).Ring);
        rig.Add("late", T0 + 1000);

        var early = rig.Decide(T0 + 5000);
        var later = rig.Decide(T0 + 61_000);

        Assert.Null(early.Ring);
        Assert.Equal(T0 + 60_000, early.NextDeadlineMs);
        Assert.NotNull(later.Ring);
    }

    [Fact]
    public void ABurstOfThreeNonUrgentItemsOver25SecondsGivesOneRingAfterTheQuietWindow()
    {
        var rig = new Rig();
        rig.Add("a", T0);
        rig.Add("b", T0 + 10_000);
        rig.Add("c", T0 + 25_000);

        // nearest non-match: just before newest + 20 s quiet, and the first item alone is not enough to ring at 30 s
        Assert.Null(rig.Decide(T0 + 44_000).Ring);
        var ring = rig.Decide(T0 + 45_000).Ring;

        Assert.NotNull(ring);
        Assert.StartsWith("ah: You have 3 messages", ring!.Text, StringComparison.Ordinal);
        Assert.Null(rig.Decide(T0 + 46_000, handed: new Dictionary<string, HandedState> { [ring.Id] = new("pending", T0 + 45_000) }).Ring);
    }

    [Fact]
    public void ANonUrgentTrickleRingsAtTheMaximumWaitFromTheOldestItem()
    {
        var rig = new Rig();
        for (var i = 0; i < 5; i++)
        {
            rig.Add("t" + i, T0 + (i * 15_000));
        }

        // the quiet window keeps sliding, so only the 60 s maximum wait from the oldest item rings
        Assert.Null(rig.Decide(T0 + 59_000).Ring);
        Assert.NotNull(rig.Decide(T0 + 60_000).Ring);
    }

    [Fact]
    public void AnUrgentItemInsideTheWindowRingsAtOnceAndCarriesTheQueuedNonUrgentItems()
    {
        var rig = new Rig();
        rig.Add("a", T0);
        Assert.Null(rig.Decide(T0 + 4000).Ring);
        rig.Add("b1", T0 + 5000, kind: "report", reference: rig.Blocked("resp"));

        var ring = rig.Decide(T0 + 5500).Ring;

        Assert.NotNull(ring);
        Assert.True(ring!.Urgent);
        Assert.StartsWith("ah: You have 2 messages", ring.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("delivery-failed", true)]
    [InlineData("api-failed", true)]
    [InlineData("member-resume", false)]
    [InlineData("member-down", true)]
    [InlineData("check-in-due", false)]
    [InlineData("lane-escalated", false)]
    public void TheRecoveryAndDeliveryEventsAreUrgentAndTheAlarmsAreNot(string ev, bool urgent)
    {
        var rig = new Rig();
        rig.Add("e1", T0, kind: "event", ev: ev, requestId: "r1");

        var ring = rig.Decide(T0 + 1000).Ring;

        if (urgent)
        {
            Assert.NotNull(ring);
            Assert.True(ring!.Urgent);
        }
        else
        {
            Assert.Null(ring);
        }
    }

    [Fact]
    public void HasUrgentSeesAnUrgentItemButNotANonUrgentOneAndClaimsNoRing()
    {
        var rig = new Rig();
        rig.Add("a", T0);
        Assert.False(rig.Bell.HasUrgent(Session, T0 + 1000));

        rig.Add("b1", T0 + 2000, kind: "report", reference: rig.Blocked("resp"));

        Assert.True(rig.Bell.HasUrgent(Session, T0 + 3000));
        Assert.NotNull(rig.Decide(T0 + 3500).Ring);
    }

    [Fact]
    public void ASecondRingInTheSameTurnIsNotRungAtTurnEndAndANewTurnRings()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var ring = rig.Decide(T0).Ring;
        Assert.NotNull(ring);

        // the ring's prompt starts a turn, which the mod's next poll reports as busy
        _ = rig.Decide(T0 + 2000, busyAt: T0 + 1000);
        rig.Add("during", T0 + 3000);

        Assert.Null(rig.Decide(T0 + 20_000, carrier: DoorbellCarrier.Stop).Ring);

        _ = rig.Decide(T0 + 41_000, busyAt: T0 + 40_000);
        Assert.NotNull(rig.Decide(T0 + 50_000, carrier: DoorbellCarrier.Stop).Ring);
    }

    [Fact]
    public void ABusySessionIsToldOfNewMailOnceByTheToolCarrier()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var busy = T0 - 5000;

        var first = rig.Decide(T0 + 20_000, busyAt: busy, carrier: DoorbellCarrier.Tool);
        var second = rig.Decide(T0 + 21_000, busyAt: busy, carrier: DoorbellCarrier.Tool);

        Assert.NotNull(first.Ring);
        Assert.StartsWith("ah: You have 2 messages", first.Ring!.Text, StringComparison.Ordinal);
        Assert.Null(second.Ring);
    }

    [Theory]
    [InlineData("check-in-due")]
    [InlineData("peer-silent")]
    [InlineData("job-overdue")]
    [InlineData("unlinked-dispatch")]
    public void AnAlarmNeverRingsTheToolCarrierMidTurnButRingsOnceTheSessionIsIdle(string alarm)
    {
        var rig = new Rig();
        rig.Add("a1", T0, kind: "event", ev: alarm, requestId: "r1");
        var busy = T0 + 19_000;

        Assert.Null(rig.Decide(T0 + 20_000, busyAt: busy, carrier: DoorbellCarrier.Tool).Ring);
        Assert.Null(rig.Decide(T0 + 40_000, busyAt: busy, carrier: DoorbellCarrier.Tool).Ring);
        Assert.NotNull(rig.Decide(T0 + 3_000_000).Ring);
    }

    [Fact]
    public void ADecisionWritesOneTraceRowPerChangeOfItsInputs()
    {
        var rig = new Rig();
        List<JsonObject> Rows() => [.. File.ReadAllLines(Mailbox.TraceFile(rig.Home, Session)).Select(static l => JsonText.Parse(l).AsObject())];

        _ = rig.Decide(T0 + 1_000);
        _ = rig.Decide(T0 + 2_000);
        Assert.Single(Rows());

        rig.Add("a1", T0 + 3_000, kind: "event", ev: "some-event");
        _ = rig.Decide(T0 + 4_000);

        var row = Rows()[^1];
        Assert.Equal(2, Rows().Count);
        Assert.Equal(1, row["new_items"].AsNumber());
        Assert.True(row["idle"].AsBool());
        Assert.False(row["ring_pending"].AsBool());
    }

    [Fact]
    public void NegativeADecisionWritesNothingIntoTheBox()
    {
        var rig = new Rig();

        _ = rig.Decide(T0 + 1_000);

        Assert.True(File.Exists(Mailbox.TraceFile(rig.Home, Session)));
        Assert.False(File.Exists(rig.Dir("orchestrator") + "/attempts.jsonl"));
        Assert.False(File.Exists(rig.Dir("orchestrator") + "/.executed-ops.jsonl"));
    }

    [Fact]
    public void TheTraceFileRollsOverOnlyPastItsLimit()
    {
        var rig = new Rig();
        var mailbox = (Mailbox)rig.Fx.Mailbox;
        var file = Mailbox.TraceFile(rig.Home, Session);

        mailbox.AppendTrace(Session, new JsonObject { ["i"] = 1 });
        mailbox.AppendTrace(Session, new JsonObject { ["i"] = 2 });
        Assert.False(File.Exists(file + ".old"));
        Assert.Equal(2, File.ReadAllLines(file).Length);

        mailbox.AppendTrace(Session, new JsonObject { ["big"] = new string('x', (int)Mailbox.TraceFileLimit) });
        mailbox.AppendTrace(Session, new JsonObject { ["i"] = 3 });

        Assert.Equal(3, File.ReadAllLines(file + ".old").Length);
        Assert.Single(File.ReadAllLines(file));
    }

    [Fact]
    public void AUsersAnswerRingsAnIdleSessionAtOnceWithNoQuietWait()
    {
        var rig = new Rig();
        rig.Add("a1", T0, kind: "event", ev: "question-answered");

        Assert.NotNull(rig.Decide(T0 + 1_000).Ring);
    }

    [Fact]
    public void NegativeAnOrdinaryEventStillWaitsForTheQuietPeriod()
    {
        var rig = new Rig();
        rig.Add("a1", T0, kind: "event", ev: "some-event");

        Assert.Null(rig.Decide(T0 + 1_000).Ring);
    }

    [Fact]
    public void NegativeAnOrdinaryEventStillRingsTheToolCarrierMidTurn()
    {
        var rig = new Rig();
        rig.Add("e1", T0, kind: "event", ev: "context-high", requestId: "r1");

        Assert.NotNull(rig.Decide(T0 + 20_000, busyAt: T0 - 5000, carrier: DoorbellCarrier.Tool).Ring);
    }

    [Fact]
    public void ANewerAlarmOfTheSameKindAndRequestReplacesThePendingOneAndTheOlderIsAcked()
    {
        var rig = new Rig();
        rig.Add("a1", T0, kind: "event", ev: "check-in-due", requestId: "r1");
        rig.Add("a2", T0 + 1000, kind: "event", ev: "check-in-due", requestId: "r1");

        var ring = rig.Decide(T0 + 3_000_000).Ring;

        Assert.NotNull(ring);
        Assert.Single(ring!.EntryIds);
        Assert.EndsWith("a2", ring.EntryIds[0], StringComparison.Ordinal);
        Assert.True(rig.Acked("a1"));
    }

    [Fact]
    public void NegativeAlarmsOfTheSameKindForAnotherRequestAreBothKept()
    {
        var rig = new Rig();
        rig.Add("a1", T0, kind: "event", ev: "check-in-due", requestId: "r1");
        rig.Add("a2", T0 + 1000, kind: "event", ev: "check-in-due", requestId: "r2");

        var ring = rig.Decide(T0 + 3_000_000).Ring;

        Assert.NotNull(ring);
        Assert.False(ring!.Inline);
        Assert.False(rig.Acked("a1"));
        Assert.False(rig.Acked("a2"));
    }

    [Fact]
    public void NegativeTheToolCarrierNeverRerings()
    {
        var rig = new Rig();
        Notes(rig, 2);
        Assert.NotNull(rig.Decide(T0).Ring);

        var late = rig.Decide(T0 + 120_000, busyAt: T0 + 110_000, carrier: DoorbellCarrier.Tool);

        Assert.Null(late.Ring);
    }

    [Fact]
    public void NegativeTheToolCarrierDoesNotRingTwiceInOneTurnForLaterNonUrgentMail()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var busy = T0 - 5000;
        Assert.NotNull(rig.Decide(T0 + 20_000, busyAt: busy, carrier: DoorbellCarrier.Tool).Ring);
        rig.Add("later", T0 + 21_000);

        Assert.Null(rig.Decide(T0 + 60_000, busyAt: busy, carrier: DoorbellCarrier.Tool).Ring);
    }

    [Fact]
    public void AnUrgentItemRingsTheToolCarrierAtOnceEvenAfterARingInTheSameTurn()
    {
        var rig = new Rig();
        Notes(rig, 1);
        var busy = T0 - 5000;
        Assert.NotNull(rig.Decide(T0 + 20_000, busyAt: busy, carrier: DoorbellCarrier.Tool).Ring);
        rig.Add("blocked", T0 + 20_500, kind: "report", reference: rig.Blocked("tool-blocked"));

        Assert.NotNull(rig.Decide(T0 + 20_600, busyAt: busy, carrier: DoorbellCarrier.Tool).Ring);
    }

    [Fact]
    public void NegativeAnIdleSessionIsNotRungByTheToolCarrier()
    {
        var rig = new Rig();
        Notes(rig, 2);

        Assert.Null(rig.Decide(T0 + 20_000, busyAt: null, carrier: DoorbellCarrier.Tool).Ring);
    }

    [Fact]
    public void AnUnackedRingAtTurnEndIsShownOnceByTheStopCarrier()
    {
        var rig = new Rig();
        Notes(rig, 2);
        _ = rig.Decide(T0, busyAt: T0 - 5000);

        var stop = rig.Decide(T0 + 1000, carrier: DoorbellCarrier.Stop);
        var again = rig.Decide(T0 + 2000, carrier: DoorbellCarrier.Stop);

        Assert.StartsWith("ah: You have 2 messages", stop.Ring!.Text, StringComparison.Ordinal);
        Assert.Null(again.Ring);
    }

    [Fact]
    public void ARingStateFileThatIsDeletedGivesOneRingWithCountsAndNoBody()
    {
        var rig = new Rig();
        Notes(rig, 3);
        _ = rig.Decide(T0);
        _ = rig.Pull(T0 + 1000, peek: true);
        File.Delete(rig.Home + "/mailbox-notes/doorbell/" + Session + ".json");

        var ring = rig.Decide(T0 + 60_000).Ring;

        Assert.Equal("ah: You have 3 messages (3 reports). Run `ah msg next`.", ring!.Text);
        Assert.DoesNotContain("hi", ring.Text.Replace("ah: You have", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnackedRingToAnIdleSessionIsRungAgainAfterThirtySecondsWithTheCurrentCounts()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var first = rig.Decide(T0).Ring!;
        Assert.Null(rig.Decide(T0 + 29_000).Ring);

        var again = rig.Decide(T0 + 30_000).Ring;

        Assert.Equal("rering", again!.Kind);
        Assert.Equal(first.Text, again.Text);
    }

    [Fact]
    public void ANextPeekAcksTheRingWithoutConsumingAndStopsTheReRing()
    {
        var rig = new Rig();
        Notes(rig, 2);
        _ = rig.Decide(T0);

        var peek = rig.Pull(T0 + 5000, peek: true);

        Assert.Equal(2, peek.Left);
        Assert.False(rig.Acked("n01"));
        Assert.Null(rig.Decide(T0 + 35_000).Ring);
        Assert.Null(rig.Decide(T0 + 65_000).Ring);
    }

    [Fact]
    public void ARingWhosePromptHasNotStartedATurnIsNeverStackedWithAnother()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var ring = rig.Decide(T0).Ring!;

        var pending = rig.Decide(T0 + 31_000, handed: new Dictionary<string, HandedState> { [ring.Id] = new("pending", T0) });

        Assert.Null(pending.Ring);
    }

    [Fact]
    public void ARingALaterRingReplacedIsReleasedFromTheClaimMap()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var first = rig.Decide(T0).Ring!;
        var second = rig.Decide(T0 + 61_000, handed: new Dictionary<string, HandedState> { [first.Id] = new("resolved", T0) }).Ring!;
        Assert.NotEqual(first.Id, second.Id);

        var after = rig.Decide(T0 + 62_000, handed: new Dictionary<string, HandedState> { [first.Id] = new("resolved", T0), [second.Id] = new("resolved", T0 + 61_000) });

        Assert.Contains(first.Id, after.Acked);
    }

    [Fact]
    public void NegativeTheCurrentRingAndOrdinaryMailStayInTheClaimMap()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var ring = rig.Decide(T0).Ring!;

        var after = rig.Decide(T0 + 1_000, handed: new Dictionary<string, HandedState> { [ring.Id] = new("resolved", T0), ["20990101-000000-mail"] = new("resolved", T0) });

        Assert.Empty(after.Acked);
    }

    [Fact]
    public void ABusySessionIsNeverReRung()
    {
        var rig = new Rig();
        Notes(rig, 2);
        _ = rig.Decide(T0);

        Assert.Null(rig.Decide(T0 + 40_000, busyAt: T0 + 35_000).Ring);
    }

    [Fact]
    public void ThreeUnackedReRingsRaiseOneMailUnackedAndTheReRingingStops()
    {
        var rig = new Rig();
        Notes(rig, 2);
        _ = rig.Decide(T0);
        for (var i = 1; i <= 3; i++)
        {
            Assert.Equal("rering", rig.Decide(T0 + (i * 30_000)).Ring!.Kind);
        }

        var escalated = rig.Decide(T0 + 120_000);
        var after = rig.Decide(T0 + 150_000);

        Assert.Null(escalated.Ring);
        Assert.Equal("mail-unacked", escalated.Escalation!.Kind);
        Assert.Equal("orchestrator@tm", escalated.Escalation.Address);
        Assert.Null(after.Ring);
        Assert.Null(after.Escalation);
    }

    [Fact]
    public void AnUnackedAlarmInALeadsBoxIsReRungThenEscalatedAsMailUnacked()
    {
        // ah2-180 B1 (was: rung once, never re-rung or escalated).
        var rig = new Rig();
        rig.Add("a1", T0 - 30_000, kind: "event", ev: "check-in-due", requestId: "r1");
        Assert.NotNull(rig.Decide(T0).Ring);

        var rerings = 0;
        DoorbellEscalation? told = null;
        for (var i = 1; i <= 6; i++)
        {
            var later = rig.Decide(T0 + (i * IMailCore.DoorbellReringMs));
            rerings += later.Ring?.Kind == "rering" ? 1 : 0;
            told ??= later.Escalation;
        }

        Assert.Equal(IMailCore.DoorbellReringMax, rerings);
        Assert.Equal("mail-unacked", told?.Kind);
    }

    private static DoorbellEscalation? BusyFor(Rig rig, int minutes, Action<List<DoorbellEscalation>>? each = null)
    {
        var told = new List<DoorbellEscalation>();
        for (var m = 0; m <= minutes; m += 5)
        {
            var now = T0 + (m * 60_000);
            var decided = rig.Decide(now, busyAt: now - 1000, carrier: DoorbellCarrier.Tool);
            if (decided.Escalation is { } e)
            {
                told.Add(e);
            }

            each?.Invoke(told);
        }

        return told.Count == 1 ? told[0] : null;
    }

    [Fact]
    public void AReportWaitingBehindASessionBusyForMoreThanThirtyMinutesIsToldToTheUserOnce()
    {
        var rig = new Rig();
        rig.Add("r1", T0, kind: "report");

        var told = BusyFor(rig, 60);

        Assert.NotNull(told);
        Assert.Equal("mail-unacked", told!.Kind);
        Assert.Contains("busy for 3", told.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeABusyUnderThirtyMinutesRaisesNothing()
    {
        var rig = new Rig();
        rig.Add("r1", T0, kind: "report");
        var raised = false;

        _ = BusyFor(rig, 25, told => raised |= told.Count > 0);

        Assert.False(raised);
    }

    [Fact]
    public void NegativeAnAlarmOnlyQueueNeverRaisesTheLongBusyToast()
    {
        var rig = new Rig();
        rig.Add("a1", T0, kind: "event", ev: "check-in-due", requestId: "r1");
        var raised = false;

        _ = BusyFor(rig, 90, told => raised |= told.Count > 0);

        Assert.False(raised);
    }

    private static string Teams(Rig rig, string tm, string? top)
    {
        var teams = rig.Root + "/h/teams";
        Directory.CreateDirectory(teams);
        File.WriteAllText(teams + "/tm.json", tm);
        if (top is not null)
        {
            File.WriteAllText(teams + "/top.json", top);
        }

        return rig.Root + "/h";
    }

    private const string ChildTeam = "{\"members\":[],\"parent\":{\"team\":\"top\",\"stream\":\"s1\"},\"orchestrator\":{\"pid\":1,\"session_id\":\"o2\"}}\n";
    private const string TopTeam = "{\"members\":[],\"orchestrator\":{\"pid\":1,\"session_id\":\"o1\"}}\n";

    private static List<JsonValue> LeadMail(Rig rig, string hier) =>
        [.. rig.Fx.Mailbox.ReadEntries(rig.Fx.Mailbox.BoxDirFor(hier + "/teams/top.json", "orchestrator")).Entries];

    [Fact]
    public void AMailUnackedEscalationIsSentToTheParentLeadsBoxAndLeavesTheAnswer()
    {
        var rig = new Rig();
        var hier = Teams(rig, ChildTeam, TopTeam);
        Notes(rig, 2);
        _ = rig.Decide(T0, hier: hier);
        for (var i = 1; i <= 3; i++)
        {
            _ = rig.Decide(T0 + (i * 30_000), hier: hier);
        }

        var escalated = rig.Decide(T0 + 120_000, hier: hier);

        Assert.Null(escalated.Escalation);
        var mail = Assert.Single(LeadMail(rig, hier));
        Assert.Equal("mail-unacked", mail.Member("event")!.Value.AsString());
        Assert.Contains("has not read its doorbell", mail.Member("text")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAnEscalationWithNoParentTeamStaysInTheAnswerAndSendsNothing()
    {
        var rig = new Rig();
        var hier = Teams(rig, TopTeam, TopTeam);
        Notes(rig, 2);
        _ = rig.Decide(T0, hier: hier);
        for (var i = 1; i <= 3; i++)
        {
            _ = rig.Decide(T0 + (i * 30_000), hier: hier);
        }

        var escalated = rig.Decide(T0 + 120_000, hier: hier);

        Assert.Equal("mail-unacked", escalated.Escalation!.Kind);
        Assert.False(Directory.Exists(rig.Fx.Mailbox.BoxDirFor(hier + "/teams/top.json", "orchestrator")) && LeadMail(rig, hier).Count > 0);
    }

    [Fact]
    public void NegativeAnEscalationWithoutAHierarchyDirStaysInTheAnswer()
    {
        var rig = new Rig();
        Notes(rig, 2);
        _ = rig.Decide(T0);
        for (var i = 1; i <= 3; i++)
        {
            _ = rig.Decide(T0 + (i * 30_000));
        }

        Assert.Equal("mail-unacked", rig.Decide(T0 + 120_000).Escalation!.Kind);
    }

    [Fact]
    public void AMemberDownEscalationIsSentToTheParentLeadsBox()
    {
        var rig = new Rig(pid: 2_000_000_000);
        var hier = Teams(rig, ChildTeam, TopTeam);
        Notes(rig, 2);

        var first = rig.Decide(T0, hier: hier);

        Assert.Null(first.Escalation);
        Assert.Equal("member-down", Assert.Single(LeadMail(rig, hier)).Member("event")!.Value.AsString());
    }

    [Theory]
    [InlineData("step-needs-user", true)]
    [InlineData("classifier-denied", true)]
    [InlineData("step-unfinished", false)]
    public void AStepNeedsUserEventIsBlockedUnderBothNamesAndAStepUnfinishedOneIsOnlyAnAlarm(string name, bool first)
    {
        var rig = new Rig();
        rig.Add("idle", T0 - 61_000, kind: "event", ev: "member-idle", text: "y");
        rig.Add("esc", T0 - 60_000, kind: "event", ev: name, text: "x");

        var text = rig.Decide(T0).Ring!.Text;

        Assert.Equal(first, text.Contains("1 blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void AMailUnackedEventIsRankedBlocked()
    {
        var rig = new Rig();
        rig.Add("esc", T0 - 60_000, kind: "event", ev: "mail-unacked", text: "x");
        rig.Add("idle", T0 - 59_000, kind: "event", ev: "member-idle", text: "y");

        var text = rig.Decide(T0).Ring!.Text;

        Assert.Contains("1 blocked", text, StringComparison.Ordinal);
        Assert.Contains("1 alarm", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARecipientWhoseProcessIsGoneIsNeverRungAndMemberDownIsRaisedOnce()
    {
        var rig = new Rig(pid: 2_000_000_000);
        Notes(rig, 2);

        var first = rig.Decide(T0);
        var second = rig.Decide(T0 + 60_000);

        Assert.Null(first.Ring);
        Assert.Equal("member-down", first.Escalation!.Kind);
        Assert.Contains("2 unread", first.Escalation.Text, StringComparison.Ordinal);
        Assert.Null(second.Escalation);
        Assert.False(rig.Acked("n01"));
    }

    [Fact]
    public void AnAckedRingWithUnreadMailIsRemindedAtTenTwentyFortySixtyAndSixtyMinutes()
    {
        var rig = new Rig();
        Notes(rig, 2);
        _ = rig.Decide(T0);
        var at = T0;
        foreach (var minutes in new[] { 10, 20, 40, 60, 60 })
        {
            at += 5000;
            _ = rig.Pull(at, peek: true);
            var due = at + (minutes * 60_000);
            Assert.Null(rig.Decide(due - 1).Ring);
            var reminder = rig.Decide(due).Ring;
            Assert.Equal("reminder", reminder!.Kind);
            Assert.Equal("ah: reminder: 2 unread (2 reports). Run `ah msg next`.", reminder.Text);
            at = due;
        }
    }

    [Fact]
    public void AnUnreadBlockedItemIsRemindedAfterTwoMinutes()
    {
        var rig = new Rig();
        Notes(rig, 1);
        rig.Add("b1", T0 - 1000, kind: "report", reference: rig.Blocked("resp"));
        _ = rig.Decide(T0);
        _ = rig.Pull(T0 + 1000, peek: true);

        Assert.Null(rig.Decide(T0 + 1000 + 119_999).Ring);
        Assert.Equal("reminder", rig.Decide(T0 + 1000 + 120_000).Ring!.Kind);
    }

    [Fact]
    public void AReadOfAnItemResetsTheReminderScheduleToTenMinutes()
    {
        var rig = new Rig();
        Notes(rig, 3);
        _ = rig.Decide(T0);
        _ = rig.Pull(T0 + 1000, peek: true);
        _ = rig.Decide(T0 + 601_000);

        _ = rig.Pull(T0 + 602_000);

        Assert.Null(rig.Decide(T0 + 602_000 + 599_999).Ring);
        Assert.Equal("reminder", rig.Decide(T0 + 602_000 + 600_000).Ring!.Kind);
    }

    [Fact]
    public void AReminderThatFallsDueWhileBusyWaitsUntilTheSessionIsIdle()
    {
        var rig = new Rig();
        Notes(rig, 2);
        _ = rig.Decide(T0);
        _ = rig.Pull(T0 + 1000, peek: true);
        var due = T0 + 1000 + 600_000;

        Assert.Null(rig.Decide(due + 5000, busyAt: due).Ring);
        Assert.Equal("reminder", rig.Decide(due + 10_000).Ring!.Kind);
    }

    [Fact]
    public void AQueueOfExactlyOneItemIsRungInlineAsTheEntryItself()
    {
        var rig = new Rig();
        rig.Add("only", T0 - 60_000, text: "please review");

        var ring = rig.Decide(T0).Ring!;

        Assert.True(ring.Inline);
        Assert.Equal([rig.Full("only")], ring.EntryIds);
        Assert.Contains("please review", ring.Text, StringComparison.Ordinal);
        Assert.Contains("[ah-entry " + rig.Full("only"), ring.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKillSwitchLeavesTheDoorbellOffForTheSession()
    {
        var rig = new Rig();
        Notes(rig, 2);
        var off = MeshModule.CreateMailDoorbell(
            rig.Fx.Mailbox,
            rig.Fx.Core,
            rig.Fx.Files,
            MeshFixture.Create(new Dictionary<string, string> { ["HOME"] = rig.Root, ["AH_HOME"] = rig.Home, ["AH_MAIL_DOORBELL"] = "0" }, Clock, rig.Root).Environment);

        Assert.False(off.Serves(Session));
        Assert.True(rig.Bell.Serves(Session));
        Assert.Null(off.Decide(new DoorbellRequest(Session, T0, null, new Dictionary<string, HandedState>(), DoorbellCarrier.Prompt)).Ring);
    }

    [Fact]
    public void ASessionWithNoOrchestratorBoxIsNotServed()
    {
        var rig = new Rig();
        File.WriteAllText(
            $"{rig.Home}/registrations/{Session}.json",
            JsonText.Stringify(new JsonObject { ["session_id"] = Session, ["address"] = "imp@tm", ["addresses"] = new JsonArray { "imp@tm" }, ["boxes"] = new JsonObject { ["imp@tm"] = rig.Dir("imp") } }, 0));

        Assert.False(rig.Bell.Serves(Session));
    }

    [Fact]
    public void TwoCarriersAskingAtOnceClaimExactlyOneRing()
    {
        var rig = new Rig();
        Notes(rig, 2);

        var rings = Enumerable.Range(0, 8).AsParallel().Select(_ => rig.Decide(T0).Ring).Count(r => r is not null);

        Assert.Equal(1, rings);
    }

    [Fact]
    public void TheDueAnswerHandsTheDoorbellLineInsteadOfTheBodiesForAnOrchestratorBox()
    {
        var rig = new Rig();
        Notes(rig, 3);
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);

        var answer = due.Compute(new DueRequest(Session, T0, null, 5000, new Dictionary<string, HandedState>(), null));

        Assert.Equal("ah: You have 3 messages (3 reports). Run `ah msg next`.", answer.Prompt);
        Assert.Single(answer.Ids);
        Assert.StartsWith("doorbell:", answer.Ids[0], StringComparison.Ordinal);
    }

    // The idle ring's loss: a poll claims the ring, so asking again before handing it over finds nothing due.
    [Fact]
    public void TheDueAnswerMarksTheRingItClaimedAndTheNextAskHasNoRing()
    {
        var rig = new Rig();
        Notes(rig, 1);
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);

        var first = due.Compute(new DueRequest(Session, T0, null, 5000, new Dictionary<string, HandedState>(), null));
        var second = due.Compute(new DueRequest(Session, T0 + 1000, null, 5000, new Dictionary<string, HandedState>(), null));

        Assert.True(first.Ring);
        Assert.NotNull(first.Prompt);
        Assert.True(due.AnswerJson(first, null)["ring"].AsBool());
        Assert.False(second.Ring);
        Assert.Null(second.Prompt);
        Assert.False(due.AnswerJson(second, null).ContainsKey("ring"));
    }

    [Fact]
    public void TheDueAnswerForTheToolCarrierRingsABusySessionAndTheDefaultCarrierDoesNot()
    {
        var rig = new Rig();
        Notes(rig, 3);
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);
        var busy = T0 - 5000;

        var plain = due.Compute(new DueRequest(Session, T0, busy, 5000, new Dictionary<string, HandedState>(), null));
        var tool = due.Compute(new DueRequest(Session, T0, busy, 5000, new Dictionary<string, HandedState>(), null) { Carrier = DoorbellCarrier.Tool });

        Assert.Null(plain.Prompt);
        Assert.Equal("ah: You have 3 messages (3 reports). Run `ah msg next`.", tool.Prompt);
    }

    [Fact]
    public void TheDueRequestParsesTheToolCarrierAndRejectsAnythingElse()
    {
        var rig = new Rig();
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);
        JsonValue Body(string carrier) => JsonText.Parse("{\"session\":\"s\",\"now\":1,\"carrier\":\"" + carrier + "\"}");

        Assert.Equal(DoorbellCarrier.Tool, due.ParseRequest(Body("tool"), out _)!.Carrier);
        Assert.Null(due.ParseRequest(Body("stop"), out var problem));
        Assert.Contains("carrier", problem, StringComparison.Ordinal);
        Assert.Equal(DoorbellCarrier.Prompt, due.ParseRequest(JsonText.Parse("{\"session\":\"s\",\"now\":1}"), out _)!.Carrier);
    }

    [Fact]
    public void ARingedDoorbellIdIsReportedAckedOnceTheSessionHasRead()
    {
        var rig = new Rig();
        Notes(rig, 3);
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);
        var id = due.Compute(new DueRequest(Session, T0, null, 5000, new Dictionary<string, HandedState>(), null)).Ids[0];
        _ = rig.Pull(T0 + 1000, peek: true);

        var answer = due.Compute(new DueRequest(Session, T0 + 2000, null, 5000, new Dictionary<string, HandedState> { [id] = new("resolved", T0) }, null));

        Assert.Contains(id, answer.Acked);
        Assert.Empty(answer.Ids);
    }

    // ---------------------------------------------------------------- priority: the two tiers

    [Theory]
    [InlineData("FAIL", true)]
    [InlineData("FAIL: 3 tests", true)]
    [InlineData("SYSTEMIC", true)]
    [InlineData("ERROR", true)]
    [InlineData("FLAKY-PASS", false)]
    [InlineData("FAILOVER", false)]
    [InlineData("PASS", false)]
    [InlineData("CHANGES REQUIRED", false)]
    public void AReportIsUrgentOnlyWhenItsStatusFirstWordIsARedBuild(string status, bool urgent)
    {
        var rig = new Rig();
        var path = rig.Root + "/resp--response.md";
        File.WriteAllText(path, $"## [0] tldr\n- x\n\n## [1] status\n- {status}\n");
        rig.Add("r1", T0 - 1000, kind: "report", reference: path);
        Notes(rig, 1);

        Assert.Equal(urgent, rig.Bell.HasUrgent(Session, T0));
    }

    [Fact]
    public void AReportWithNoStatusHeadingIsNotUrgent()
    {
        var rig = new Rig();
        var path = rig.Root + "/resp--response.md";
        File.WriteAllText(path, "## [0] tldr\n- FAIL\n");
        rig.Add("r1", T0 - 1000, kind: "report", reference: path);

        Assert.False(rig.Bell.HasUrgent(Session, T0));
    }

    [Theory]
    [InlineData("member-stuck-escalated", true)]
    [InlineData("member-waiting", false)]
    public void TheStuckEscalationIsUrgentAndAWaitingMemberIsNot(string name, bool urgent)
    {
        var rig = new Rig();
        rig.Add("e1", T0 - 1000, kind: "event", ev: name);

        Assert.Equal(urgent, rig.Bell.HasUrgent(Session, T0));
    }

    // ---------------------------------------------------------------- priority: urgent items say what they are

    [Fact]
    public void AQueueWithNoUrgentItemRingsTheCountsLineAlone()
    {
        var rig = new Rig();
        Notes(rig, 2);

        Assert.Equal("ah: You have 2 messages (2 reports). Run `ah msg next`.", rig.Decide(T0).Ring!.Text);
    }

    [Fact]
    public void AnUrgentItemIsNamedBelowTheCountsLineAndStaysQueued()
    {
        var rig = new Rig();
        Notes(rig, 1);
        rig.Add("u1", T0 - 500, kind: "event", ev: "step-needs-user", text: "reviewer-3 paused on a classifier denial");

        var ring = rig.Decide(T0).Ring!;

        Assert.Equal("ah: You have 2 messages (1 blocked, 1 report). Run `ah msg next`.\n- step-needs-user: reviewer-3 paused on a classifier denial", ring.Text);
        Assert.False(rig.Acked("u1"));
    }

    [Fact]
    public void AnUrgentReportIsNamedByItsKindAndRef()
    {
        var rig = new Rig();
        Notes(rig, 1);
        var path = rig.Blocked("resp");
        rig.Add("b1", T0 - 500, kind: "report", text: string.Empty, reference: path);

        Assert.EndsWith("\n- report: " + path, rig.Decide(T0).Ring!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FiveUrgentItemsShowThreeLinesAndCountTheRest()
    {
        var rig = new Rig();
        for (var i = 1; i <= 5; i++)
        {
            rig.Add("u" + i, T0 - 5000 + i, kind: "brief", text: "ask " + i);
        }

        var lines = rig.Decide(T0).Ring!.Text.Split('\n');

        Assert.Equal(5, lines.Length);
        Assert.Equal("+2 more urgent", lines[4]);
        Assert.Equal("- brief: ask 1", lines[1]);
    }

    [Fact]
    public void AnUrgentEntryOfManyLinesIsOneLineOfTwoHundredCharacters()
    {
        var rig = new Rig();
        Notes(rig, 1);
        rig.Add("u1", T0 - 500, kind: "brief", text: string.Join('\n', Enumerable.Repeat(new string('x', 99), 5)));

        var lines = rig.Decide(T0).Ring!.Text.Split('\n');

        Assert.Equal(2, lines.Length);
        Assert.Equal(200, lines[1].Length);
    }

    [Fact]
    public void AnUrgentItemIsNamedAtTheNextToolCallAndNotRungAgain()
    {
        var rig = new Rig();
        rig.Add("u1", T0 - 500, kind: "event", ev: "member-blocked", text: "impl-1 is at a prompt");

        var ring = rig.Decide(T0, busyAt: T0 - 1000, carrier: DoorbellCarrier.Tool).Ring!;

        Assert.Contains("- member-blocked: impl-1 is at a prompt", ring.Text, StringComparison.Ordinal);
        Assert.Null(rig.Decide(T0 + 5000, busyAt: T0 - 1000, carrier: DoorbellCarrier.Tool).Ring);
    }

    [Fact]
    public void AnIdleRingSubmitsTheUrgentLineBecauseItHasNoCompactForm()
    {
        var rig = new Rig();
        Notes(rig, 1);
        rig.Add("u1", T0 - 500, kind: "brief", text: "ask");
        var due = MeshModule.CreateMailDue(rig.Fx.Mailbox, rig.Fx.Core, rig.Fx.Files, rig.Fx.Environment);

        var answer = due.Compute(new DueRequest(Session, T0, null, 5000, new Dictionary<string, HandedState>(), null));

        Assert.Null(answer.Compact);
        Assert.Contains("- brief: ask", answer.Prompt, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- priority: reminders stop

    private static double RemindSixTimes(Rig rig)
    {
        Notes(rig, 2);
        _ = rig.Decide(T0);
        var at = T0;
        foreach (var minutes in new[] { 10, 20, 40, 60, 60, 60 })
        {
            at += 5000;
            _ = rig.Pull(at, peek: true);
            at += minutes * 60_000;
            Assert.Equal("reminder", rig.Decide(at).Ring!.Kind);
        }

        return at;
    }

    [Fact]
    public void AnUnreadQueueGetsNoSeventhReminder()
    {
        var rig = new Rig();
        var at = RemindSixTimes(rig);

        _ = rig.Pull(at + 5000, peek: true);

        Assert.Null(rig.Decide(at + (24 * 3_600_000)).Ring);
    }

    [Fact]
    public void NewMailRestartsTheRemindersAfterTheCap()
    {
        var rig = new Rig();
        var at = RemindSixTimes(rig);
        _ = rig.Pull(at + 5000, peek: true);
        at += 3_600_000;
        rig.Add("late", at, kind: "report");

        Assert.Equal("ring", rig.Decide(at + 60_000).Ring!.Kind);

        _ = rig.Pull(at + 61_000, peek: true);
        Assert.Equal("reminder", rig.Decide(at + 61_000 + 600_000).Ring!.Kind);
    }

    // ---------------------------------------------------------------- priority: alarms clear themselves

    private const string OwnTeam =
        "{\"members\":[{\"name\":\"a\",\"route\":\"pane\",\"kind\":\"codex\"},{\"name\":\"d\",\"dismissed\":true}],\"orchestrator\":{\"pid\":1,\"session_id\":\"s1\"}}\n";

    /// <summary>A rig whose session is registered under a team file, with the hooks that revalidate events.</summary>
    private static (Rig Rig, string Home) WithTeam(string? team = OwnTeam)
    {
        var rig = new Rig(wrapHooks: static h => h);
        var home = rig.Root + "/h";
        Directory.CreateDirectory(home + "/teams");
        if (team is not null)
        {
            File.WriteAllText(home + "/teams/tm.json", team);
            Ah.TestKit.TestImport.Planted(rig.Fx.Environment.AhHome, [home + "/teams/tm.json"]);
        }

        var path = $"{rig.Home}/registrations/{Session}.json";
        var registration = JsonText.Parse(File.ReadAllText(path)).AsObject();
        registration["team_files"] = new JsonObject { ["orchestrator@tm"] = home + "/teams/tm.json" };
        File.WriteAllText(path, JsonText.Stringify(registration, 0));
        return (rig, home);
    }

    private static void Event(Rig rig, string id, string name, JsonObject? data)
    {
        var entry = new JsonObject { ["id"] = id, ["kind"] = "event", ["event"] = name, ["from"] = "ahd", ["to"] = "orchestrator@tm", ["created"] = Rig.Iso(T0 - 1000), ["text"] = name };
        if (data is not null)
        {
            entry["data"] = data;
        }

        File.AppendAllText(rig.Dir("orchestrator") + "/entries.jsonl", JsonText.Stringify(entry, 0) + "\n");
    }

    private static bool Kept(Rig rig) => !rig.Pull(T0, peek: true).Empty;

    private static string Reason(Rig rig, string id) => File.ReadAllText(rig.Dir("orchestrator") + "/acks/" + id);

    private static void IdleAlarm(Rig rig, string id, params string[] members)
    {
        var list = new JsonArray();
        foreach (var m in members)
        {
            list.Add(m);
        }

        var entry = new JsonObject
        {
            ["id"] = id, ["kind"] = "event", ["event"] = "idle-capacity", ["from"] = "ahd", ["to"] = "orchestrator@tm", ["created"] = Rig.Iso(T0 - 1000),
            ["text"] = $"ahd alarm: idle-capacity — {members.Length} members idle 10+ min with no open request: {string.Join(", ", members)}. Ready backlog items nobody holds: b1 X.",
            ["data"] = new JsonObject { ["members"] = list },
        };
        File.AppendAllText(rig.Dir("orchestrator") + "/entries.jsonl", JsonText.Stringify(entry, 0) + "\n");
    }

    [Fact]
    public void B336APulledIdleCapacityAlarmLeavesOutADismissedMember()
    {
        var (rig, _) = WithTeam();
        IdleAlarm(rig, "i1", "a", "d");

        var text = rig.Pull(T0).Text;

        Assert.Contains("1 member idle 10+ min with no open request: a.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a, d", text, StringComparison.Ordinal);
    }

    [Fact]
    public void B336ANewerIdleCapacityAlarmReplacesTheUnreadOneSoItIsDeliveredOnce()
    {
        var (rig, _) = WithTeam();
        IdleAlarm(rig, "i1", "a");
        IdleAlarm(rig, "i2", "a");

        var text = rig.Pull(T0).Text;

        Assert.True(rig.Acked("i1"));
        Assert.DoesNotContain("2 entries", text, StringComparison.Ordinal);
        Assert.True(rig.Pull(T0 + 1000).Empty);
    }

    public static TheoryData<string> MemberAlarms =>
    [
        "check-in-due", "peer-silent", "idle-without-report", "job-overdue", "queue-stall", "stalled-after-compact", "member-blocked", "member-down", "member-stuck", "step-needs-user", "member-waiting",
    ];

    [Theory]
    [MemberData(nameof(MemberAlarms))]
    public void AnAlarmForADismissedMemberIsResolved(string name)
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", name, new JsonObject { ["member"] = "d@tm" });

        Assert.False(Kept(rig));
        Assert.Contains(EventRevalidationReason, Reason(rig, "e1"), StringComparison.Ordinal);
    }

    private const string EventRevalidationReason = "resolved";

    private static void RetiredInLedger(Rig rig, string home, string name)
    {
        Directory.CreateDirectory(home + "/progress/tm");
        File.AppendAllText(home + "/progress/tm/ledger.jsonl", $"{{\"kind\":\"member\",\"text\":\"retired {name} (implementor)\"}}\n");
        Ah.TestKit.TestImport.Planted(rig.Fx.Environment.AhHome, [home + "/progress/tm/ledger.jsonl"]);
    }

    [Theory]
    [MemberData(nameof(MemberAlarms))]
    public void AnAlarmForAMemberTheDismissTookOutOfTheFileIsResolved(string name)
    {
        var (rig, home) = WithTeam();
        RetiredInLedger(rig, home,"gone");
        Event(rig, "e1", name, new JsonObject { ["member"] = "gone@tm" });

        Assert.False(Kept(rig));
    }

    [Theory]
    [InlineData("nobody")]
    [InlineData("nobody@tm")]
    [InlineData("a@other-team")]
    [InlineData("gone@other-team")]
    [InlineData("gone-too")]
    [InlineData("uds:/tmp/cc-socks/1.sock")]
    public void AnAlarmForANameNotProvedGoneStays(string member)
    {
        var (rig, home) = WithTeam();
        RetiredInLedger(rig, home,"gone");
        Event(rig, "e1", "peer-silent", new JsonObject { ["member"] = member, ["request_id"] = "r1" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void ARetiredNameBackOnTheTeamStays()
    {
        var (rig, home) = WithTeam();
        RetiredInLedger(rig, home,"a");
        Event(rig, "e1", "member-down", new JsonObject { ["member"] = "a@tm" });

        Assert.True(Kept(rig));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("a@tm")]
    [InlineData("orchestrator")]
    [InlineData("orchestrator@tm")]
    [InlineData("the owner")]
    public void AnAlarmForALiveMemberTheOrchestratorOrTheOwnerStays(string member)
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "member-down", new JsonObject { ["member"] = member });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AnAlarmWhoseMemberIsNullStays()
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "check-in-due", new JsonObject { ["request_id"] = "r1", ["member"] = JsonValue.Null });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AnAlarmStaysWhenTheTeamRecordCannotBeRead()
    {
        var (rig, _) = WithTeam("{not json");
        Event(rig, "e1", "member-down", new JsonObject { ["member"] = "d" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AnAlarmStaysWhenTheTeamRecordIsGoneWithNoRecordOfTheMembersRetirement()
    {
        var (rig, _) = WithTeam(null);
        Event(rig, "e1", "member-down", new JsonObject { ["member"] = "d" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AnAlarmForTheLastMemberDismissedIsResolvedThoughTheTeamFileWentWithIt()
    {
        var (rig, home) = WithTeam(null);
        RetiredInLedger(rig, home,"d");
        Event(rig, "e1", "member-down", new JsonObject { ["member"] = "d" });

        Assert.False(Kept(rig));
    }

    [Fact]
    public void AnAlarmStaysWhenTheRecordIsUnreadableEvenWithARetirement()
    {
        var (rig, home) = WithTeam("{not json");
        RetiredInLedger(rig, home,"d");
        Event(rig, "e1", "member-down", new JsonObject { ["member"] = "d" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AMemberWaitingAlarmStaysWhenTheChecklistIsEmptyAndTheRequestUnanswered()
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = "a", ["item"] = "_run/i1", ["request_id"] = "r1" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AMemberWaitingAlarmIsResolvedOnAnEmptyChecklistOnceTheRequestIsAcked()
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = "a", ["item"] = "_run/i1", ["request_id"] = "r1" });
        rig.Fx.Hierarchy.Peers.AppendOrchestratorAck(Session, "r1", "tm");

        Assert.False(Kept(rig));
    }

    [Fact]
    public void AnAlarmStaysWhenTheRegistrationNamesNoTeamFile()
    {
        var rig = new Rig(wrapHooks: static h => h);
        Event(rig, "e1", "member-down", new JsonObject { ["member"] = "d" });

        Assert.True(Kept(rig));
    }

    [Theory]
    [InlineData("member-down")]
    [InlineData("member-waiting")]
    [InlineData("idle-without-report")]
    [InlineData("member-blocked")]
    public void AnEventWithoutDataApplies(string name)
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", name, null);

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AnEventWithNoCheckStaysEvenForAGoneMember()
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "member-stuck-escalated", new JsonObject { ["member"] = "d" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void ANoteIsNotRevalidated()
    {
        var (rig, _) = WithTeam();
        rig.Add("n1", T0 - 1000, kind: "note", text: "fyi");

        Assert.True(Kept(rig));
    }

    private static void Plan(Rig rig, string home, string item, string state)
    {
        Directory.CreateDirectory(home + "/progress/tm");
        string Row(string ev, string extra = "") =>
            $"{{\"v\":1,\"ts\":\"2026-01-01T00:00:00Z\",\"team\":\"tm\",\"stream\":\"_run\",\"item\":\"{item}\",\"event\":\"{ev}\"{extra},\"by\":\"o\",\"session\":null}}";
        File.WriteAllText(home + "/progress/tm/_run.plan.jsonl", string.Join('\n', Row("add", ",\"title\":\"t\""), Row(state, ",\"note\":\"n\"")) + "\n");
        Ah.TestKit.TestImport.Planted(rig.Fx.Environment.AhHome, [home + "/progress/tm/_run.plan.jsonl"]);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("the owner")]
    public void AMemberWaitingAlarmStaysWhileItsItemWaitsOnTheOrchestrator(string member)
    {
        var (rig, home) = WithTeam();
        Plan(rig, home,"i1", "wait-orch");
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = member, ["item"] = "_run/i1" });

        Assert.True(Kept(rig));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("the owner")]
    public void AMemberWaitingAlarmIsResolvedOnceItsItemIsDone(string member)
    {
        var (rig, home) = WithTeam();
        Plan(rig, home,"i1", "done");
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = member, ["item"] = "_run/i1" });

        Assert.False(Kept(rig));
    }

    [Fact]
    public void AMemberWaitingAlarmIsResolvedWhenItsItemIsAbsentFromAGoodRead()
    {
        var (rig, home) = WithTeam();
        Plan(rig, home,"other", "wait-orch");
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = "a", ["item"] = "_run/i1" });

        Assert.False(Kept(rig));
    }

    [Fact]
    public void AMemberWaitingAlarmStaysWhenTheProgressReadFails()
    {
        var (rig, home) = WithTeam();
        Directory.CreateDirectory(home + "/progress/tm/_run.plan.jsonl");
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = "a", ["item"] = "_run/i1" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void TwoMemberWaitingAlarmsForOneSubjectKeepTheNewest()
    {
        var (rig, home) = WithTeam();
        Plan(rig, home,"i1", "wait-orch");
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = "a", ["item"] = "_run/i1", ["request_id"] = "r1" });
        Event(rig, "e2", "member-waiting", new JsonObject { ["member"] = "a", ["item"] = "_run/i1", ["request_id"] = "r1" });

        Assert.Equal(1, rig.Pull(T0, peek: true).Left);
        Assert.True(rig.Acked("e1"));
        Assert.False(rig.Acked("e2"));
    }

    [Fact]
    public void AMemberWaitingAlarmDoesNotRingTheToolCarrierAndIsReRungInALeadsBox()
    {
        var rig = new Rig();
        rig.Add("w1", T0 - 60_000, kind: "event", ev: "member-waiting");

        Assert.Null(rig.Decide(T0, busyAt: T0 - 1000, carrier: DoorbellCarrier.Tool).Ring);
        Assert.Equal("ring", rig.Decide(T0).Ring!.Kind);
        // ah2-180 B1 (was: never re-rung)
        Assert.Equal("rering", rig.Decide(T0 + IMailCore.DoorbellReringMs).Ring?.Kind);
    }

    private static string GateRow(string type, string session = Session, string member = "a", string extra = "") =>
        $"{{\"type\":\"{type}\",\"session_id\":\"{session}\",\"team\":\"tm\",\"member\":\"{member}\"{extra}}}\n";

    [Theory]
    [InlineData("{\"activity\":\"blocked\",\"at\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"activity\":\"working\",\"at\":\"2026-01-01T00:00:00Z\"}")]
    [InlineData("{\"activity\":\"idle\",\"at\":\"2026-01-01T00:00:00Z\"}")]
    public void AMemberBlockedAlarmDoesNotClearFromActivityAlone(string activity)
    {
        var (rig, home) = WithTeam();
        Ah.TestKit.TestActivity.Put(rig.Fx.Hierarchy.StateDomains, rig.Fx.Environment.AhHome, home, "pane-a", activity);
        Ah.TestKit.TestGates.Put(rig.Fx.Hierarchy.StateDomains, rig.Fx.Environment.AhHome, home, GateRow("blocked-wake"));
        Event(rig, "e1", "member-blocked", new JsonObject { ["member"] = "a@tm" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AMemberBlockedAlarmClearsWhenTheWatcherClosedTheEpisode()
    {
        var (rig, home) = WithTeam();
        Ah.TestKit.TestGates.Put(rig.Fx.Hierarchy.StateDomains, rig.Fx.Environment.AhHome, home, GateRow("blocked-wake") + GateRow("blocked-clear"));
        Event(rig, "e1", "member-blocked", new JsonObject { ["member"] = "a@tm" });

        Assert.False(Kept(rig));
    }

    [Fact]
    public void AMemberBlockedAlarmClearsWhenAnAnswerClosedTheEpisode()
    {
        var (rig, home) = WithTeam();
        Ah.TestKit.TestGates.Put(rig.Fx.Hierarchy.StateDomains, rig.Fx.Environment.AhHome, home, GateRow("blocked-wake") + GateRow("blocked-clear", session: "someone-else", extra: ",\"by\":\"answer\""));
        Event(rig, "e1", "member-blocked", new JsonObject { ["member"] = "a@tm" });

        Assert.False(Kept(rig));
    }

    [Theory]
    [InlineData("blocked-clear", "blocked-wake", Session, "a")] // a new episode opened after the clear
    [InlineData("blocked-wake", "blocked-clear", "another-session", "a")]
    [InlineData("blocked-wake", "blocked-clear", Session, "another-member")]
    public void AMemberBlockedAlarmStaysWithoutItsOwnClear(string first, string second, string session, string member)
    {
        var (rig, home) = WithTeam();
        Ah.TestKit.TestGates.Put(rig.Fx.Hierarchy.StateDomains, rig.Fx.Environment.AhHome, home, GateRow(first) + GateRow(second, session, member));
        Event(rig, "e1", "member-blocked", new JsonObject { ["member"] = "a@tm" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AnIdleWithoutReportAlarmIsResolvedOnceTheReportIsAcked()
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "idle-without-report", new JsonObject { ["member"] = "a@tm", ["request_id"] = "r1" });
        rig.Fx.Hierarchy.Peers.AppendOrchestratorAck(Session, "r1", "tm");

        Assert.False(Kept(rig));
    }

    [Theory]
    [InlineData("check-in-due")]
    [InlineData("peer-silent")]
    public void AnOverdueAlarmIsResolvedOnceTheReportIsAcked(string name)
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", name, new JsonObject { ["member"] = "a@tm", ["request_id"] = "r1" });
        rig.Fx.Hierarchy.Peers.AppendOrchestratorAck(Session, "r1", "tm");

        Assert.False(Kept(rig));
    }

    private const string Charter = "20261016-120000-aa01";

    /// <summary>A running lane led by so-1, whose open charter <see cref="Charter"/> this session dispatched, with an alarm <paramref name="name"/> about it.</summary>
    private static Rig LaneAlarm(string name, bool answered = false, string state = "working", bool dispatchRow = true, string? emptyResponseMark = null)
    {
        var (rig, home) = WithTeam("{\"team_id\":\"t-tm\",\"members\":[{\"role\":\"sub-orchestrator\",\"name\":\"so-1\",\"route\":\"peer\"}],\"streams\":{\"s1\":{\"sub_orchestrator\":\"so-1\",\"state\":\"" + state + "\",\"opened\":\"2026-10-16T11:00:00Z\"}}}");
        Directory.CreateDirectory(home + "/msgs");
        var request = $"{home}/msgs/{Charter}--sub-orchestrator--t1--request.md";
        File.WriteAllText(request, $"---\nid: {Charter}\ntype: request\nto: sub-orchestrator\nfrom: orchestrator\nslug: t1\nparent: null\nreason: null\neta: small\nto_name: so-1\nfrom_name: null\nteam: tm\ncreated: 2026-10-16T12:00:00+00:00\n---\n\n## [0] tldr\n- x\n");
        if (answered)
        {
            File.WriteAllText($"{home}/msgs/{Charter}--orchestrator--t1--response.md", $"---\nid: {Charter}\ntype: response\nto: orchestrator\nfrom: sub-orchestrator\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: so-1\nteam: tm\ncreated: 2026-10-16T12:30:00+00:00\n---\n\nDone: the work is finished.\n");
        }

        if (emptyResponseMark is not null)
        {
            File.WriteAllText($"{home}/msgs/{Charter}--orchestrator--t1--response.md", $"---\nid: {Charter}\ntype: response\nto: orchestrator\nfrom: sub-orchestrator\nslug: t1\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: so-1\nteam: tm\ncreated: 2026-10-16T12:30:00+00:00\n{emptyResponseMark}\n---\n");
        }

        if (dispatchRow)
        {
            PeerRow(rig, new JsonObject { ["type"] = "dispatch", ["session_id"] = Session, ["request_id"] = Charter, ["path"] = request, ["to_addr"] = "so-1@tm" });
        }

        Event(rig, "e1", name, new JsonObject { ["member"] = "so-1@tm", ["request_id"] = Charter });
        return rig;
    }

    private static void PeerRow(Rig rig, JsonObject row)
    {
        var path = rig.Fx.Hierarchy.Peers.PeerPendingPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonText.Stringify(row, 0) + "\n");
    }

    private static void ReadReport(Rig rig) => rig.Fx.Hierarchy.Peers.AppendOrchestratorAck(Session, Charter, "tm");

    [Theory]
    [InlineData("check-in-due", false, true)]
    [InlineData("check-in-due", true, true)]
    [InlineData("peer-silent", false, true)]
    [InlineData("peer-silent", true, true)]
    [InlineData("check-in-due", false, false)]
    public void ARunningLaneWhoseReportWasReadStillGetsItsAlarm(string name, bool answered, bool dispatchRow)
    {
        var rig = LaneAlarm(name, answered, dispatchRow: dispatchRow);
        ReadReport(rig);

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AnIdleAlarmOfALaneIsResolvedOnceItsReportIsRead()
    {
        var rig = LaneAlarm("idle-without-report");
        ReadReport(rig);

        Assert.False(Kept(rig));
    }

    [Fact]
    public void ALanesCheckInIsResolvedWhileItsLandedReportIsUnread()
    {
        Assert.False(Kept(LaneAlarm("check-in-due", answered: true)));
    }

    [Fact]
    public void ALanesCheckInIsResolvedOnceTheLaneHasEnded()
    {
        var rig = LaneAlarm("check-in-due", state: "done");
        ReadReport(rig);

        Assert.False(Kept(rig));
    }

    [Fact]
    public void ALanesCheckInIsResolvedOnceTheLaneHasBeenHeardFrom()
    {
        var rig = LaneAlarm("check-in-due");
        ReadReport(rig);
        PeerRow(rig, new JsonObject { ["type"] = "heard", ["session_id"] = Session, ["from"] = "so-1@tm", ["ts"] = Rig.Iso(T0) });

        Assert.False(Kept(rig));
    }

    [Theory]
    [InlineData("check-in-due", "withdrawn: true")]
    [InlineData("peer-silent", "withdrawn: true")]
    [InlineData("peer-silent", "closed: true")]
    public void AnAlarmOnARequestRetiredBeforeItWasDeliveredIsDropped(string name, string mark)
    {
        Assert.False(Kept(LaneAlarm(name, emptyResponseMark: mark)));
    }

    [Theory]
    [InlineData("withdrawn: false")]
    [InlineData("closed: false")]
    public void AnAlarmStaysWhenTheResponseIsEmptyAndRetiresNothing(string mark)
    {
        Assert.True(Kept(LaneAlarm("peer-silent", emptyResponseMark: mark)));
    }

    [Theory]
    [InlineData("withdrawn: true", false)]
    [InlineData("closed: true", false)]
    [InlineData("withdrawn: false", true)]
    public void AnAlarmOnARequestWithNoDispatchRowIsJudgedByTheExchangeInItsPool(string mark, bool kept)
    {
        Assert.Equal(kept, Kept(LaneAlarm("peer-silent", dispatchRow: false, emptyResponseMark: mark)));
    }

    [Theory]
    [InlineData("check-in-due")]
    [InlineData("peer-silent")]
    public void AnOverdueAlarmStaysWhenOnlyAnotherRequestsReportIsAcked(string name)
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", name, new JsonObject { ["member"] = "a@tm", ["request_id"] = "r1" });
        rig.Fx.Hierarchy.Peers.AppendOrchestratorAck(Session, "r-other", "tm");

        Assert.True(Kept(rig));
    }

    private static void RegisterSession(Rig rig, string session, string address) =>
        File.WriteAllText(
            $"{rig.Home}/registrations/{session}.json",
            JsonText.Stringify(new JsonObject { ["session_id"] = session, ["address"] = address, ["addresses"] = new JsonArray { address } }, 0));

    [Theory]
    [InlineData("idle-without-report")]
    [InlineData("member-waiting")]
    public void AnAlarmIsResolvedByAnAckFromTheSameOrchestratorBeforeAResumeAndNotByAnotherSessions(string kind)
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", kind, new JsonObject { ["member"] = "a@tm", ["item"] = "_run/i1", ["request_id"] = "r1" });
        RegisterSession(rig, "stranger", "other@tm");
        rig.Fx.Hierarchy.Peers.AppendOrchestratorAck("stranger", "r1", "elsewhere");
        Assert.True(Kept(rig));

        RegisterSession(rig, "before-resume", "orchestrator@tm");
        rig.Fx.Hierarchy.Peers.AppendOrchestratorAck("before-resume", "r1", "tm");
        Assert.False(Kept(rig));
    }

    [Fact]
    public void AnAckedMemberWaitingAlarmStaysUntilTheItemLeavesWaitingOnTheOrchestrator()
    {
        var (rig, home) = WithTeam();
        Plan(rig, home,"i1", "wait-orch");
        Event(rig, "e1", "member-waiting", new JsonObject { ["member"] = "a@tm", ["item"] = "_run/i1", ["request_id"] = "r1" });
        rig.Fx.Hierarchy.Peers.AppendOrchestratorAck(Session, "r1", "tm");
        Assert.True(Kept(rig));

        // the plan file was taken into the database at the first read, so the move goes in as a plan write
        Assert.Null(rig.Fx.Hierarchy.Progress.RecordChecklist(home, "tm", "_run", "o", [new Ah.Hierarchy.PlanInput("i1", "done", Note: "n")]).Refused);
        Assert.False(Kept(rig));
    }

    [Fact]
    public void AnIdleWithoutReportAlarmStaysWhileTheExchangeIsOpenAndTheMemberLive()
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "idle-without-report", new JsonObject { ["member"] = "a@tm", ["request_id"] = "r1" });

        Assert.True(Kept(rig));
    }

    [Fact]
    public void AReportLandedEventIsNotJudgedByItsMember()
    {
        var (rig, _) = WithTeam();
        Event(rig, "e1", "report-landed", new JsonObject { ["member"] = "d@tm" });

        Assert.True(Kept(rig));
    }
}
