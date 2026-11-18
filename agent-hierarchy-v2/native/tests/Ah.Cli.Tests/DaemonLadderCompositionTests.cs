using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The seams the daemon's watch host is composed with carry a ladder that walks: an unacked entry of a member that takes no paste
/// climbs from rung 3 to rung 5 on successive passes, the Orchestrator is mailed the relay request and the delivery failure, and
/// the counts the daemon reports are not zero on the way. The control is the host without the launch layer, whose ladder never runs.
/// </summary>
public sealed class DaemonLadderCompositionTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private static double Count(JsonObject counts, string key) => counts[key].AsNumber();

    [Fact]
    public async Task AnUnackedEntryOfAMemberThatTakesNoPasteClimbsToRungFiveOnTheDaemonsSeams()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var hier = Path.Combine(_sandbox.Home, "proj", ".claude", "ah2");
        var teamFile = Path.Combine(hier, "teams", "tm.json");
        // The ladder under test is the one a switched-off install runs: rung 3 relay requests exist only without single-carrier mode.
        Directory.CreateDirectory(_sandbox.AhHome);
        File.WriteAllText(Path.Combine(_sandbox.AhHome, "agent-hierarchy.json"), "{\"single_carrier\":false}\n");
        var variables = new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables);
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        TestPlant.Team(
            graph.Roster,
            graph.Writer,
            teamFile,
            "{\"team_id\":\"t-tm\",\"transport\":\"tmux\",\"mesh\":{\"policy\":\"open\"},"
            + $"\"orchestrator\":{{\"pid\":{Environment.ProcessId},\"session_id\":\"orch-tm\"}},"
            + "\"members\":[{\"role\":\"implementor\",\"name\":\"m1\",\"transport_id\":\"%1\",\"paste\":false}]}\n");
        var orchBox = graph.Mailbox.BoxDirFor(teamFile, "orchestrator");
        var memberBox = graph.Mailbox.BoxDirFor(teamFile, "m1");
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite("orch-tm", [new BoxRef("orchestrator@tm", orchBox)], teamFile, "stub", Environment.ProcessId)));
        var sent = graph.Mailbox.Send(
            new JsonObject
            {
                ["to"] = "m1@tm",
                ["from"] = "orchestrator@tm",
                ["session_id"] = JsonValue.Null,
                ["cwd"] = Path.Combine(_sandbox.Home, "proj"),
                ["hierarchy_dir"] = hier,
                ["kind"] = "note",
                ["text"] = "to m1",
                ["id"] = "e-1",
            });
        Assert.True(sent.Ok, sent.Failure?.ToString());
        var now = (double)graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds();

        var control = await WatchHostSeams.Unavailable.Ladder.RunAsync(now, 15000, TestContext.Current.CancellationToken);
        Assert.Equal(0, Count(control, "pending") + Count(control, "sending") + Count(control, "escalated"));
        Assert.Empty(graph.Mailbox.LadderRowsByEntry(memberBox));

        var seams = DaemonServices.SeamsOf(graph);
        var first = await seams.Ladder.RunAsync(now, 15000, TestContext.Current.CancellationToken);
        Assert.Equal(1, Count(first, "pending"));
        var second = await seams.Ladder.RunAsync(now, 15000, TestContext.Current.CancellationToken);
        Assert.Equal(1, Count(second, "sending"));
        var relay = graph.Mailbox.ReadEntries(orchBox).Entries.Select(static e => e.AsObject()).Single(static e => e["from"].AsString() == "ahd");
        Assert.Equal("relay-needed", relay["event"].AsString());

        JsonObject last = second;
        // Attempt rows are stamped by the mailbox's wall clock, not by the pass's moment, so the rung-3 window opens when the row was
        // written; a moment taken from the clock now is after that however long the passes above took.
        var later = (double)graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds() + 601_000;
        for (var pass = 0; pass < 6 && Count(last, "escalated") == 0; pass++)
        {
            last = await seams.Ladder.RunAsync(later, 15000, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, Count(last, "escalated"));
        var states = graph.Mailbox.LadderRowsByEntry(memberBox).Single(static r => r.Id == "e-1").Rows.Select(static r => r["state"].AsString()).ToList();
        Assert.Equal("escalated", states[^1]);
        var failed = graph.Mailbox.ReadEntries(orchBox).Entries.Select(static e => e.AsObject())
            .Where(static e => e["from"].AsString() == "ahd" && e["event"].AsString() == "delivery-failed").ToList();
        _ = Assert.Single(failed);
        Assert.Contains("m1@tm", failed[0]["text"].AsString(), StringComparison.Ordinal);
    }

    /// <summary>The wall clock, except while <see cref="Pin"/> is set.</summary>
    private sealed class PinClock(Ah.Abstractions.IClock real) : Ah.Abstractions.IClock
    {
        internal DateTimeOffset? Pin { get; set; }

        public DateTimeOffset UtcNow => Pin ?? real.UtcNow;

        public TimeSpan LocalOffset(DateTimeOffset instant) => real.LocalOffset(instant);
    }

    private const string WakeText = "ah: mail waiting, run ah msg next";

    private const string IdleClaude = "  earlier output\n\n────────────────────────────────────────\n❯ \n────────────────────────────────────────\n  ? for shortcuts";

    /// <summary>A team whose Orchestrator box holds two filed reports, on the daemon's seams, with the doorbell's real decider writing its state.</summary>
    private sealed class WakeRig
    {
        internal required ServiceGraph Graph { get; init; }

        internal required PinClock Clock { get; init; }

        internal required FakeRunner Runner { get; init; }

        internal required System.Runtime.CompilerServices.StrongBox<string> Status { get; init; }

        internal required string OrchBox { get; init; }

        internal required string MemberBox { get; init; }

        internal required string TeamFile { get; init; }

        internal required string AhHome { get; init; }

        /// <summary>A moment past the unread limit, measured from the moment the doorbell first saw the mail.</summary>
        internal required double Later { get; init; }

        internal List<string[]> Prompts => [.. Runner.Calls.Where(static a => a.Contains("agent") && a.Contains("prompt"))];

        internal string[] Rows => [.. Graph.Mailbox.LadderRowsByEntry(OrchBox).SelectMany(static r => r.Rows).Select(static r => r["state"].AsString())];

        internal DoorbellDecision Decide(double now, bool held = false, double? busyAt = null) =>
            Graph.MailDoorbell.Decide(new DoorbellRequest("orch-tm", now, busyAt, new Dictionary<string, HandedState>(), DoorbellCarrier.Prompt) { Held = held });

        /// <summary>A poll as recent as <paramref name="now"/>, so only the doorbell's own state can hold the mail back.</summary>
        internal void Poll(double now)
        {
            // The store stamps a heartbeat with the mailbox's clock, so the poll's moment is pinned on it for the one write.
            Clock.Pin = DateTimeOffset.FromUnixTimeMilliseconds((long)now);
            try
            {
                Graph.Mailbox.WriteHeartbeat(OrchBox, "claude-mod", "orch-tm", 5000);
            }
            finally
            {
                Clock.Pin = null;
            }
        }

        /// <summary>A note whose reminders have run out: nothing rings again without being forced.</summary>
        internal void RemindersSpent()
        {
            var args = new JsonObject
            {
                ["kind"] = "doorbell",
                ["name"] = "orch-tm",
                ["data"] = JsonText.Parse("{\"v\":1,\"seq\":9,\"ring_id\":\"doorbell:9\",\"rang_at\":1,\"told_mark\":\"z\",\"remind_step\":6,\"escalated\":true}"),
            };
            Assert.True(Graph.Writer.Invoke("mailbox", "note", JsonText.ToUtf8(args, 0), new Ah.Abstractions.RequestContext(AhHome, AhHome, string.Empty, string.Empty)).Ok);
        }

        internal async Task PassAsync(double now, bool poll = true)
        {
            if (poll)
            {
                Poll(now);
            }

            _seams ??= DaemonServices.SeamsOf(Graph);
            _ = await _seams.Ladder.RunAsync(now, 15000, TestContext.Current.CancellationToken);
        }

        // One daemon, one ladder: its throttle lives across passes.
        private WatchHostSeams? _seams;
    }

    private WakeRig TwoReports(string? pane, string status = "idle", int promptExit = 0, long? pid = null, bool alarmOnly = false)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var hier = Path.Combine(_sandbox.Home, "proj", ".claude", "ah2");
        var teamFile = Path.Combine(hier, "teams", "tm.json");
        var typed = false;
        var surface = new System.Runtime.CompilerServices.StrongBox<string>(status);
        var runner = new FakeRunner
        {
            Output = argv =>
            {
                if (argv.Contains("prompt"))
                {
                    typed = promptExit == 0;
                }

                if (argv.Contains("get"))
                {
                    // The wake starts a turn, seen once: the next pass finds the session idle again, so only the throttle can hold it back.
                    var now = typed ? "working" : surface.Value!;
                    typed = false;
                    return "{\"result\":{\"agent\":{\"agent_status\":\"" + now + "\"}}}";
                }

                return argv.Contains("read") ? IdleClaude : string.Empty;
            },
            Exit = argv => argv.Contains("prompt") ? promptExit : 0,
        };
        var variables = new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home };
        var io = PlatformModule.CreateIo(null);
        var clock = new PinClock(io.Clock);
        var graph = ServiceGraph.Build(io with { Clock = clock }, new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables), runner, new FakeDaemonClient());
        TestPlant.Team(
            graph.Roster,
            graph.Writer,
            teamFile,
            "{\"team_id\":\"t-tm\",\"transport\":\"herdr\",\"mesh\":{\"policy\":\"open\"},"
            + $"\"orchestrator\":{{\"pid\":{Environment.ProcessId},\"session_id\":\"orch-tm\"}},"
            + "\"members\":[{\"role\":\"implementor\",\"name\":\"m1\",\"transport_id\":\"w1:p9\",\"paste\":false}]}\n");
        var orchBox = graph.Mailbox.BoxDirFor(teamFile, "orchestrator");
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite("orch-tm", [new BoxRef("orchestrator@tm", orchBox)], teamFile, "claude", pid ?? Environment.ProcessId, PaneId: pane)));
        graph.Mailbox.WriteHeartbeat(orchBox, "claude-mod", "orch-tm", 5000);
        Directory.CreateDirectory(Path.Combine(hier, "msgs"));
        if (alarmOnly)
        {
            // ah2-180 B2: one daemon alarm, which the entry walk never reaches.
            var alarm = graph.Mailbox.Send(new JsonObject
            {
                ["to"] = "orchestrator@tm",
                ["from"] = "ahd",
                ["cwd"] = hier,
                ["hierarchy_dir"] = hier,
                ["event"] = "queue-stall",
                ["text"] = "queue-stall: an item waits",
            });
            Assert.True(alarm.Ok, alarm.Failure?.ToString());
        }

        foreach (var id in alarmOnly ? Array.Empty<string>() : new[] { "e-1", "e-2" })
        {
            var messageId = "20260101-000000-aa0" + id[^1];
            var response = Ah.TestKit.TestMsgStore.Plant(
                _sandbox.AhHome,
                Path.Combine(hier, "msgs", messageId + "--orchestrator--report--response.md"),
                $"---\nid: {messageId}\ntype: response\nto: orchestrator\nfrom: m1\nslug: report\n---\n\nreport\n");
            var sent = graph.Mailbox.Send(
                new JsonObject
                {
                    ["to"] = "orchestrator@tm",
                    ["from"] = "m1@tm",
                    ["session_id"] = JsonValue.Null,
                    ["cwd"] = Path.Combine(_sandbox.Home, "proj"),
                    ["hierarchy_dir"] = hier,
                    ["kind"] = "report",
                    ["ref"] = response,
                    ["id"] = id,
                });
            Assert.True(sent.Ok, sent.Failure?.ToString());
        }

        var seen = (double)graph.Io.Clock.UtcNow.ToUnixTimeMilliseconds() + 1000;
        var rig = new WakeRig
        {
            Graph = graph,
            Clock = clock,
            Runner = runner,
            Status = surface,
            OrchBox = orchBox,
            MemberBox = graph.Mailbox.BoxDirFor(teamFile, "m1"),
            TeamFile = teamFile,
            AhHome = _sandbox.AhHome,
            Later = seen + IMailCore.DoorbellRemindMs + 1000,
        };
        rig.Poll(seen);
        _ = rig.Decide(seen);
        var ring = rig.Decide(seen + 120_000).Ring;
        Assert.True(pid is not null || ring is not null);
        return rig;
    }

    private static void TheMailIsStillQueuedAndNoSenderWasTold(WakeRig rig)
    {
        Assert.False(File.Exists(Path.Combine(rig.OrchBox, "acks", "e-1")));
        Assert.False(File.Exists(Path.Combine(rig.OrchBox, "acks", "e-2")));
        Assert.Equal(2, rig.Graph.Mailbox.ReadEntries(rig.OrchBox).Entries.Count(static e => e.AsObject()["from"].AsString() == "m1@tm"));
        Assert.DoesNotContain(rig.Graph.Mailbox.ReadEntries(rig.OrchBox).Entries, static e => e.AsObject()["from"].AsString() == "ahd");
        Assert.Empty(rig.Graph.Mailbox.ReadEntries(rig.MemberBox).Entries);
        Assert.Empty(rig.Rows);
    }

    [Fact]
    public async Task AnOrchestratorWhoseMailWentUnreadIsWokenOnceInItsOwnPaneAndKeepsItsMail()
    {
        var rig = TwoReports("w1K:pDG");

        await rig.PassAsync(rig.Later);
        await rig.PassAsync(rig.Later + 1000);

        var prompt = Assert.Single(rig.Prompts);
        Assert.Equal(["agent", "prompt", "w1K:pDG", WakeText], prompt[^4..]);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
        Assert.Null(rig.Decide(rig.Later + 2000).Escalation);
    }

    [Fact]
    public async Task AWakeIsTriedAgainAfterTheLimitWhileTheMailStaysUnread()
    {
        var rig = TwoReports("w1K:pDG");

        await rig.PassAsync(rig.Later);
        await rig.PassAsync(rig.Later + IMailCore.DoorbellRemindMs + 1000);

        Assert.Equal(2, rig.Prompts.Count);
    }

    [Fact]
    public async Task NegativeMailUnreadForLessThanTheLimitIsNotPasted()
    {
        var rig = TwoReports("w1K:pDG");

        await rig.PassAsync(rig.Later - 2000);

        Assert.Empty(rig.Prompts);
        Assert.Empty(rig.Rows);
    }

    [Fact]
    public async Task AHeldSessionWhoseMailIsOldIsWokenAndNotOnceTheStopLifts()
    {
        var rig = TwoReports("w1K:pDG");
        var heldAt = rig.Later - 500;
        _ = rig.Decide(heldAt, held: true);

        await rig.PassAsync(rig.Later);
        var prompt = Assert.Single(rig.Prompts);
        Assert.Equal(["agent", "prompt", "w1K:pDG", WakeText], prompt[^4..]);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);

        _ = rig.Decide(rig.Later + 1000);
        _ = rig.Decide(rig.Later + 2000, busyAt: rig.Later + 1500);
        await rig.PassAsync(rig.Later + IMailCore.DoorbellRemindMs + 1000);
        Assert.Single(rig.Prompts);
    }

    // ah2-180 Amendment 3: mail a `stop` hold drained is not unread and wakes nothing; the same mail with no hold is woken.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DrainedMailUnderAStopHoldIsNotUnreadAndWakesNothing(bool stopped)
    {
        var rig = TwoReports("w1K:pDG");
        if (stopped)
        {
            var home = Ah.Roster.RosterModule.TeamFileHome(rig.TeamFile)!;
            rig.Graph.Roster.ChangeHold(home, "tm", static _ => new Ah.Abstractions.HoldWrite("{\"kind\":\"stop\",\"since\":\"2026-01-01T00:00:00.000Z\"}"));
            var hold = rig.Graph.Roster.HoldOf(home, "tm");
            Assert.Equal("stopped", hold?.Phase);
            _ = rig.Graph.Mailbox.DrainTeam(rig.TeamFile, "tm", hold!);
        }

        var held = rig.Decide(rig.Later - 500, held: true);
        await rig.PassAsync(rig.Later);

        Assert.Equal(stopped ? 0 : 2, held.Unread);
        Assert.Equal(stopped ? 0 : 1, rig.Prompts.Count);
    }

    // ah2-180 B1/B2/B3: a held lead with one alarm and nothing else is not rung and is woken through the fake herdr.
    [Fact]
    public async Task AHeldLeadWithOnlyAnAlarmIsWokenInItsPane()
    {
        var rig = TwoReports("w1K:pDG", alarmOnly: true);
        var held = rig.Decide(rig.Later - 500, held: true);
        Assert.Null(held.Ring);

        await rig.PassAsync(rig.Later);

        var prompt = Assert.Single(rig.Prompts);
        Assert.Equal(["agent", "prompt", "w1K:pDG", WakeText], prompt[^4..]);
        // ah2-180 D1: the wake attempt is in the session's doorbell trace with its result
        Assert.Contains(File.ReadAllLines(Path.Combine(rig.AhHome, "mailbox-notes", "doorbell-trace", "orch-tm.jsonl")), static l => l.Contains("\"state\":\"doorbell-wake\"", StringComparison.Ordinal) && l.Contains("\"outcome\":\"confirmed\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithRemindersSpentARingIsTriedFirstAndADropStillGoesToThePaneWake(bool drops)
    {
        var rig = TwoReports("w1K:pDG");
        rig.RemindersSpent();
        var start = rig.Later;
        var limit = start + IMailCore.DoorbellRemindMs;
        _ = rig.Decide(start);
        await rig.PassAsync(limit);
        Assert.Empty(rig.Prompts);

        Assert.NotNull(rig.Decide(limit).Ring);
        await rig.PassAsync(limit + 1000);
        Assert.Empty(rig.Prompts);

        if (drops)
        {
            _ = rig.Decide(limit + 2000, held: true);
        }

        await rig.PassAsync(limit + 60_000);
        // ah2-180 B3 (was: a drop means no paste): a dropped ring marks the session held, and its old mail goes to the pane wake.
        Assert.Single(rig.Prompts);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
    }

    [Fact]
    public async Task ARingThatEnteredMeansNoPaste()
    {
        var rig = TwoReports("w1K:pDG");
        var turn = rig.Later - 2000;
        _ = rig.Decide(turn, busyAt: turn);
        _ = rig.Decide(turn + 1000, busyAt: turn);

        await rig.PassAsync(turn + IMailCore.DoorbellRemindMs + 2000);

        Assert.Empty(rig.Prompts);
    }

    [Fact]
    public async Task AQuietModIsWokenByItsPaneAndItsMailWalksNoRung()
    {
        var rig = TwoReports("w1K:pDG");
        var quiet = rig.Later + IMailCore.DoorbellRemindMs;

        await rig.PassAsync(quiet, poll: false);
        await rig.PassAsync(quiet + 1000, poll: false);

        var prompt = Assert.Single(rig.Prompts);
        Assert.Equal(["agent", "prompt", "w1K:pDG", WakeText], prompt[^4..]);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
    }

    [Fact]
    public async Task NegativeAGapInPollsShorterThanTheLimitTakesNoWakeAndWalksNoRung()
    {
        var rig = TwoReports("w1K:pDG");

        rig.Poll(rig.Later);
        await rig.PassAsync(rig.Later + IMailCore.DoorbellRemindMs - 1000, poll: false);

        Assert.Empty(rig.Prompts);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
    }

    /// <summary>Passes with no poll until the entries are escalated, so the walk's rows and the sender's mail are there to read.</summary>
    private static async Task WalkQuietAsync(WakeRig rig)
    {
        var at = rig.Later + IMailCore.DoorbellRemindMs;
        for (var pass = 0; pass < 8 && !rig.Rows.Contains("escalated"); pass++)
        {
            await rig.PassAsync(at, poll: false);
        }
    }

    [Fact]
    public async Task AQuietModWithNoPaneWalksToATruthfulFailure()
    {
        var rig = TwoReports(pane: null);

        await WalkQuietAsync(rig);

        Assert.Empty(rig.Prompts);
        Assert.Contains("escalated", rig.Rows);
        Assert.Contains(rig.Graph.Mailbox.ReadEntries(rig.MemberBox).Entries, static e => e.AsObject()["event"].AsString() == "delivery-failed");
    }

    [Fact]
    public async Task NegativeADeadSessionTakesNoWake()
    {
        var rig = TwoReports("w1K:pDG", pid: 99_999_999);
        // The ladder walks only a team with a live registration, so a member's keeps the box in reach.
        Assert.True(rig.Graph.Mailbox.WriteRegistration(new RegistrationWrite("m1-s", [new BoxRef("m1@tm", rig.MemberBox)], rig.TeamFile, "claude", Environment.ProcessId)));

        await WalkQuietAsync(rig);

        Assert.Empty(rig.Prompts);
        Assert.NotEmpty(rig.Rows);
    }

    [Fact]
    public async Task AnEntryAlreadyEscalatedStillGetsAWake()
    {
        var rig = TwoReports(pane: null);
        await WalkQuietAsync(rig);
        Assert.True(rig.Graph.Mailbox.WriteRegistration(new RegistrationWrite("orch-tm", [new BoxRef("orchestrator@tm", rig.OrchBox)], rig.TeamFile, "claude", Environment.ProcessId, PaneId: "w1K:pDG")));

        var woken = rig.Later + (3 * IMailCore.DoorbellRemindMs);
        await rig.PassAsync(woken, poll: false);
        await rig.PassAsync(woken + 60_001, poll: false);

        _ = Assert.Single(rig.Prompts);
    }

    [Fact]
    public async Task ALidOpenGivesTheModTimeToPollAndADeafModIsStillWoken()
    {
        var rig = TwoReports("w1K:pDG");
        await rig.PassAsync(rig.Later - 200_000, poll: false);
        Assert.Empty(rig.Prompts);

        await rig.PassAsync(rig.Later, poll: false);
        Assert.Empty(rig.Prompts);

        await rig.PassAsync(rig.Later + 60_001, poll: false);
        _ = Assert.Single(rig.Prompts);
    }

    [Fact]
    public async Task NegativeAPassesGapUnderTheSleepGapGivesNoGrace()
    {
        var rig = TwoReports("w1K:pDG");
        await rig.PassAsync(rig.Later - 100_000, poll: false);
        Assert.Empty(rig.Prompts);

        await rig.PassAsync(rig.Later, poll: false);

        _ = Assert.Single(rig.Prompts);
    }

    [Fact]
    public async Task ALimitOfRefusalsMarksThePerson()
    {
        var rig = TwoReports("w1K:pDG", status: "working");

        await rig.PassAsync(rig.Later);
        Assert.Null(rig.Decide(rig.Later + 1000).Escalation);
        await rig.PassAsync(rig.Later + 301_000);
        Assert.Null(rig.Decide(rig.Later + 302_000).Escalation);
        await rig.PassAsync(rig.Later + 602_000);

        Assert.Empty(rig.Prompts);
        Assert.Contains("could not be woken (busy)", rig.Decide(rig.Later + 603_000).Escalation!.Text, StringComparison.Ordinal);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
    }

    [Fact]
    public async Task ABlockedEntryDoesNotSpendTheLimit()
    {
        var rig = TwoReports("w1K:pDG");
        var hier = Path.GetDirectoryName(Path.GetDirectoryName(rig.TeamFile))!;
        var iso = DateTimeOffset.FromUnixTimeMilliseconds((long)rig.Later).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
        _sandbox.PlantActivity(hier, "orch-tm", "{\"activity\":\"blocked\",\"at\":\"" + iso + "\"}\n");

        await rig.PassAsync(rig.Later);
        Assert.Empty(rig.Prompts);
        _sandbox.PlantActivity(hier, "orch-tm", "{\"activity\":\"idle\",\"at\":\"" + iso + "\"}\n");

        await rig.PassAsync(rig.Later + 1000);

        _ = Assert.Single(rig.Prompts);
    }

    [Fact]
    public async Task NegativeABusyPaneTakesNoWakeAndMarksNothing()
    {
        var rig = TwoReports("w1K:pDG", status: "working");

        await rig.PassAsync(rig.Later);

        Assert.Empty(rig.Prompts);
        var wake = Assert.Single(rig.Graph.Mailbox.ReadAttempts(rig.OrchBox), static r => r.TryGetValue("state", out var s) && s.AsString() == "doorbell-wake");
        Assert.Equal("busy", wake["reason"].AsString());
        Assert.Null(rig.Decide(rig.Later + 1000).Escalation);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
    }

    [Fact]
    public async Task ABusyPaneIsTriedAgainSoonNotAfterTheWholeLimit()
    {
        var rig = TwoReports("w1K:pDG", status: "working");

        await rig.PassAsync(rig.Later);
        rig.Status.Value = "idle";
        await rig.PassAsync(rig.Later + 301_000);

        _ = Assert.Single(rig.Prompts);
    }

    [Fact]
    public async Task WithNoPaneThePersonIsToldOnceAndTheMailStaysQueued()
    {
        var rig = TwoReports(pane: null);

        await rig.PassAsync(rig.Later);

        Assert.Empty(rig.Prompts);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
        var told = rig.Decide(rig.Later + 1000).Escalation;
        Assert.NotNull(told);
        Assert.Contains("could not be woken", told!.Text, StringComparison.Ordinal);
        Assert.Null(rig.Decide(rig.Later + 2000).Escalation);
    }

    [Fact]
    public async Task AFailedPasteTellsThePersonAndTheMailStaysQueued()
    {
        var rig = TwoReports("w1K:pDG", promptExit: 1);

        await rig.PassAsync(rig.Later);

        _ = Assert.Single(rig.Prompts);
        TheMailIsStillQueuedAndNoSenderWasTold(rig);
        Assert.Contains("could not be woken", rig.Decide(rig.Later + 1000).Escalation!.Text, StringComparison.Ordinal);
    }
}
