using Ah.Core.Json;

namespace Ah.Watch.Tests;

public sealed class OverdueTimerTests
{
    private const string Session = "orch-1";
    private const double Eta = 300;
    private static readonly string[] Ids = ["a", "b", "c"];
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 8, 0, 0, TimeSpan.Zero);

    private static string Iso(double seconds) =>
        T0.AddSeconds(seconds).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static double Ms(double seconds) => T0.ToUnixTimeMilliseconds() + (seconds * 1000);

    private sealed class Host : IOverdueHost
    {
        public DateTimeOffset Now { get; set; } = T0;
        public List<OverdueDispatch> Open = [];
        public List<AhdEventRow> Events = [];
        public OverdueMemory Memory = new(new HashSet<string>(), new HashSet<string>(), null);
        public readonly List<string> Out = [];
        public readonly List<JsonObject> Journal_ = [];
        public readonly List<string> Health = [];
        public int Cleared;
        public Action<Host>? OnSleep;

        public void Sleep(TimeSpan span)
        {
            Now += span;
            OnSleep?.Invoke(this);
        }

        public List<OverdueQueueItem> Queue = [];

        public string? FirstLine { get; set; }

        public List<OverdueJobItem> Jobs = [];

        public List<OverdueLaneItem> Lanes = [];

        public OverdueScan Scan() => new(Open, Events, Queue, Jobs, Lanes);

        public OverdueMemory LoadMemory() => Memory;

        public void SaveMemory(OverdueMemory memory) => Memory = memory;

        public void WriteHealth(string requestId, string stage, double deadlineMs, double unhealthySinceMs) => Health.Add($"{requestId}|{stage}");

        public void ClearHealth() => Cleared++;

        public int Heals;

        public void StartHeal() => Heals++;

        public void Journal(JsonObject row) => Journal_.Add(row);

        public void Print(string line) => Out.Add(line);
    }

    private static OverdueDispatch Dispatch(string id, double sentSeconds = 0, double? held = null, IReadOnlyList<JsonObject>? gates = null) =>
        new(id, $"slug-{id}", "impl-29", "small", Eta, Iso(sentSeconds), "impl-29@tm", Ms(sentSeconds), Session, [], gates ?? [], held);

    private static JsonObject Nudge(string id, double at) =>
        new() { ["type"] = "liveness-nudge", ["session_id"] = Session, ["request_id"] = id, ["ts"] = Iso(at) };

    private static OverdueTimer Timer(Host host) =>
        new(host, OverdueSettings.Defaults, "ah roster stall-diagnose", "ah roster overdue --cwd /w");

    private static string Str(JsonObject o, string key) => o.Get(key) is { IsText: true } v ? v.AsString() : string.Empty;

    [Fact]
    public void AQueueStallAhdNeverDeliveredFiresTheFailsafeAtItsDueTimePlusTheMarginAfterTheFirstLine()
    {
        var host = new Host { FirstLine = "First: ah roster checklist show --cwd /w", Queue = [new OverdueQueueItem("_run/stab|impl-1|t", "_run/stab", "impl-1", Ms(0))] };

        var code = Timer(host).Run();

        Assert.Equal(0, code);
        Assert.Equal("First: ah roster checklist show --cwd /w", host.Out[0]);
        Assert.StartsWith("FAILSAFE HIT (ah bug): queue-stall _run/stab → impl-1 overdue by", host.Out[1], StringComparison.Ordinal);
        Assert.Contains("ahd queue-stall not delivered", host.Out[1], StringComparison.Ordinal);
        Assert.True((host.Now - T0).TotalSeconds >= 120);
        Assert.Contains("queue|_run/stab|impl-1|t", host.Memory.Reported);
        // the failsafe report is the one moment the host is asked to start a heal (it starts one only if ahd does not answer)
        Assert.Equal(1, host.Heals);
    }

    [Fact]
    public void AQueueStallAhdDeliveredAndAcknowledgedDoesNotFireTheFailsafe()
    {
        var host = new Host
        {
            Queue = [new OverdueQueueItem("_run/stab|impl-1|t", "_run/stab", "impl-1", Ms(0))],
            Events = [new AhdEventRow("e1", null, null, Ms(10), Iso(5), "_run/stab", "queue-stall")],
        };

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.Equal(0, host.Heals);
    }

    [Theory]
    [InlineData("unacked")]
    [InlineData("other-item")]
    [InlineData("older-than-stall")]
    [InlineData("reported-before")]
    public void NegativeAQueueStallWhoseNearestLookalikeIsNotADeliveryStillFiresOnlyWhenItShould(string kind)
    {
        var host = new Host { Queue = [new OverdueQueueItem("_run/stab|impl-1|t", "_run/stab", "impl-1", Ms(100))] };
        host.Events = kind switch
        {
            "unacked" => [new AhdEventRow("e1", null, null, null, Iso(105), "_run/stab", "queue-stall")],
            "other-item" => [new AhdEventRow("e1", null, null, Ms(120), Iso(105), "_run/other", "queue-stall")],
            "older-than-stall" => [new AhdEventRow("e1", null, null, Ms(120), Iso(50), "_run/stab", "queue-stall")],
            _ => [],
        };
        if (kind == "reported-before")
        {
            host.Memory = new OverdueMemory(new HashSet<string> { "queue|_run/stab|impl-1|t" }, new HashSet<string>(), null);
        }

        _ = Timer(host).Run();

        var fired = host.Out.Any(static l => l.StartsWith("FAILSAFE HIT (ah bug): queue-stall", StringComparison.Ordinal));
        Assert.Equal(kind != "reported-before", fired);
    }

    private const string LaneRequest = "lane:tm/s1";
    private const string Charter = "20261021-080000-chrt";

    private static OverdueLaneItem LaneItem(double epochSeconds = 0, string? charter = Charter) => new(LaneRequest, "so-s1", Ms(epochSeconds), Ms(epochSeconds), charter);

    [Theory]
    [InlineData("working", true)]
    [InlineData("blocked", true)]
    [InlineData("review", true)]
    [InlineData("done", false)]
    [InlineData("paused", false)]
    [InlineData(null, false)]
    public void ALaneIsOwedAnEscalationOnlyInAStateThatCanGoStaleAtTheSameLimitAsTheEscalationItself(string? state, bool owed)
    {
        var item = WatchModule.OverdueLaneItemOf("tm", "s1", "so-s1", state, 1_000, Charter);

        Assert.Equal(owed, item is not null);
        if (item is not null)
        {
            Assert.Equal(1_000 + LaneEscalation.StaleMs + JobEvents.MarginMs, item.AtMs);
            Assert.Equal(1_000, item.EpochMs);
            Assert.Equal("lane:tm/s1", item.RequestId);
        }

        Assert.Null(WatchModule.OverdueLaneItemOf("tm", "s1", "so-s1", "working", null, Charter));
    }

    [Fact]
    public void AStaleLaneAhdNeverEscalatedFiresTheFailsafeNamingTheLaneAndTheCharterRequestAndIsJournalled()
    {
        var host = new Host { FirstLine = "First: ah roster checklist show --cwd /w", Lanes = [LaneItem()] };

        var code = Timer(host).Run();

        Assert.Equal(0, code);
        Assert.Equal("First: ah roster checklist show --cwd /w", host.Out[0]);
        Assert.StartsWith("FAILSAFE HIT (ah bug): lane so-s1 silent", host.Out[1], StringComparison.Ordinal);
        Assert.Contains("ahd lane-escalated not delivered", host.Out[1], StringComparison.Ordinal);
        Assert.Contains($"ah roster stall-diagnose {Charter}", host.Out[2], StringComparison.Ordinal);
        Assert.True((host.Now - T0).TotalSeconds >= 120);
        Assert.Contains(host.Journal_, static r => Str(r, "kind") == "ahd-missed" && Str(r, "subject") == LaneRequest);
        Assert.Contains($"lane|{LaneRequest}|{Ms(0):F0}", host.Memory.Reported);
    }

    [Fact]
    public void AStaleLaneWithNoOpenCharterRequestSaysSoInsteadOfNamingOne()
    {
        var host = new Host { Lanes = [LaneItem(charter: null)] };

        _ = Timer(host).Run();

        Assert.Contains("no open charter request", host.Out[1], StringComparison.Ordinal);
        Assert.DoesNotContain("stall-diagnose", host.Out[1], StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleLaneAhdEscalatedAndTheTopAcknowledgedDoesNotFireTheFailsafe()
    {
        var host = new Host
        {
            Lanes = [LaneItem()],
            Events = [new AhdEventRow("e1", LaneRequest, null, Ms(10), Iso(5), null, "lane-escalated")],
        };

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unacked")]
    [InlineData("other-lane")]
    [InlineData("earlier-episode")]
    [InlineData("other-event")]
    [InlineData("reported-before")]
    public void NegativeAStaleLanesNearestLookalikeIsNotADeliveryAndFiresOnlyWhenItShould(string kind)
    {
        var host = new Host { Lanes = [LaneItem(epochSeconds: 100)] };
        host.Events = kind switch
        {
            "unacked" => [new AhdEventRow("e1", LaneRequest, null, null, Iso(105), null, "lane-escalated")],
            "other-lane" => [new AhdEventRow("e1", "lane:tm/s2", null, Ms(120), Iso(105), null, "lane-escalated")],
            "earlier-episode" => [new AhdEventRow("e1", LaneRequest, null, Ms(120), Iso(50), null, "lane-escalated")],
            "other-event" => [new AhdEventRow("e1", LaneRequest, null, Ms(120), Iso(105), null, "lane-recovered")],
            _ => [],
        };
        if (kind == "reported-before")
        {
            host.Memory = new OverdueMemory(new HashSet<string> { $"lane|{LaneRequest}|{Ms(100):F0}" }, new HashSet<string>(), null);
        }

        _ = Timer(host).Run();

        var fired = host.Out.Any(static l => l.StartsWith("FAILSAFE HIT (ah bug): lane so-s1 silent", StringComparison.Ordinal));
        Assert.Equal(kind != "reported-before", fired);
    }

    private static OverdueJobItem JobItem(double atSeconds, string step = "overdue") => new("20261021-080000-jjjj", step, "dotnet test Core.csproj", "orchestrator", Ms(atSeconds));

    [Fact]
    public void AJobOverdueAhdNeverDeliveredFiresTheFailsafeNamingTheJobAndTheStepAndIsJournalled()
    {
        var host = new Host { FirstLine = "First: ah roster checklist show --cwd /w", Jobs = [JobItem(0)] };

        var code = Timer(host).Run();

        Assert.Equal(0, code);
        Assert.Equal("First: ah roster checklist show --cwd /w", host.Out[0]);
        Assert.StartsWith("FAILSAFE HIT (ah bug): job-overdue 20261021-080000-jjjj (dotnet test Core.csproj) of orchestrator overdue", host.Out[1], StringComparison.Ordinal);
        Assert.Contains("ahd job-overdue not delivered", host.Out[1], StringComparison.Ordinal);
        Assert.Contains("only orchestrator can extend it: tell them to run ah roster job extend 20261021-080000-jjjj", host.Out[2], StringComparison.Ordinal);
        Assert.True((host.Now - T0).TotalSeconds >= 120);
        Assert.Contains("job|20261021-080000-jjjj|overdue", host.Memory.Reported);
        Assert.Equal("ahd-missed", Str(Assert.Single(host.Journal_), "kind"));
    }

    [Fact]
    public void AJobOverdueDeliveredAndAcknowledgedDoesNotFireTheFailsafeAndIsNotJournalledAsAnUnknownId()
    {
        var host = new Host
        {
            Jobs = [JobItem(0)],
            Events = [new AhdEventRow("e1", "20261021-080000-jjjj", "overdue", Ms(10), Iso(5), null, "job-overdue")],
        };

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.Empty(host.Journal_);
    }

    [Fact]
    public void AJobOverdueDeliveredForAJobIdTheFilesDoNotKnowIsJournaledAsUnknown()
    {
        var host = new Host
        {
            Jobs = [JobItem(3000)],
            Events = [new AhdEventRow("e1", "20261021-080000-nobody", "overdue", Ms(10), Iso(5), null, "job-overdue")],
        };

        _ = Timer(host).Run();

        var row = Assert.Single(host.Journal_);
        Assert.Equal("unknown-id", Str(row, "kind"));
        Assert.Equal("e1", Str(row, "subject"));
    }

    [Theory]
    [InlineData("unacked")]
    [InlineData("other-job")]
    [InlineData("other-step")]
    [InlineData("other-event")]
    [InlineData("reported-before")]
    public void NegativeAJobOverdueWhoseNearestLookalikeIsNotADeliveryStillFiresOnlyWhenItShould(string kind)
    {
        var host = new Host { Jobs = [JobItem(0)] };
        host.Events = kind switch
        {
            "unacked" => [new AhdEventRow("e1", "20261021-080000-jjjj", "overdue", null, Iso(5), null, "job-overdue")],
            "other-job" => [new AhdEventRow("e1", "20261021-080000-other", "overdue", Ms(10), Iso(5), null, "job-overdue")],
            "other-step" => [new AhdEventRow("e1", "20261021-080000-jjjj", "cap-near", Ms(10), Iso(5), null, "job-overdue")],
            "other-event" => [new AhdEventRow("e1", "20261021-080000-jjjj", "overdue", Ms(10), Iso(5), null, "member-down")],
            _ => [],
        };
        if (kind == "reported-before")
        {
            host.Memory = new OverdueMemory(new HashSet<string> { "job|20261021-080000-jjjj|overdue" }, new HashSet<string>(), null);
        }

        _ = Timer(host).Run();

        var fired = host.Out.Any(static l => l.StartsWith("FAILSAFE HIT (ah bug): job-overdue", StringComparison.Ordinal));
        Assert.Equal(kind != "reported-before", fired);
    }

    [Fact]
    public void AJobOverdueNotYetDueByTheMarginDoesNotFireTheFailsafeBeforeTheHeartbeat()
    {
        var host = new Host { Jobs = [JobItem(3000)] };

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.StartsWith("overdue timer: heartbeat", host.Out[0], StringComparison.Ordinal);
    }

    [Fact]
    public void AQueueStallNotYetDueByTheMarginDoesNotFireTheFailsafeBeforeTheHeartbeat()
    {
        var host = new Host { Queue = [new OverdueQueueItem("_run/stab|impl-1|t", "_run/stab", "impl-1", Ms(3000))] };

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.StartsWith("overdue timer: heartbeat", host.Out[0], StringComparison.Ordinal);
    }

    [Fact]
    public void WithAhdHealthyThroughCheckInAndSilentThreeDispatchesNeverFireTheFailsafe()
    {
        var host = new Host { Open = [Dispatch("a"), Dispatch("b"), Dispatch("c")] };
        host.OnSleep = h =>
        {
            var seconds = (h.Now - T0).TotalSeconds;
            if (seconds >= Eta + 15 && h.Events.Count == 0)
            {
                var gates = Ids.Select(id => Nudge(id, Eta)).ToList();
                h.Open = [Dispatch("a", gates: gates), Dispatch("b", gates: gates), Dispatch("c", gates: gates)];
                h.Events = [.. Ids.Select(id => new AhdEventRow($"e-{id}-ci", id, "check-in", Ms(seconds)))];
            }

            if (seconds >= Eta * 1.5 + 30 && h.Events.Count == 3)
            {
                h.Events = [.. h.Events, .. Ids.Select(id => new AhdEventRow($"e-{id}-si", id, "silent", Ms(seconds)))];
            }
        };

        var code = Timer(host).Run();

        Assert.Equal(0, code);
        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.StartsWith("overdue timer: heartbeat; open dispatches: 3", host.Out[0], StringComparison.Ordinal);
        Assert.Equal("restart: ah roster overdue --cwd /w", host.Out[1]);
        Assert.Empty(host.Health);
    }

    [Fact]
    public void WithAhdSilentTheFailsafeFiresAtTheStepsDeadlinePlusTheMarginNamingOnlyThatDispatch()
    {
        var host = new Host { Open = [Dispatch("a"), Dispatch("b", sentSeconds: 200)] };

        var code = Timer(host).Run();

        Assert.Equal(0, code);
        Assert.Equal(T0 + TimeSpan.FromSeconds(Eta + 120), host.Now);
        Assert.StartsWith("FAILSAFE HIT (ah bug): a slug-a → impl-29 eta small sent ", host.Out[0], StringComparison.Ordinal);
        Assert.EndsWith("overdue by 2m; ahd check-in not delivered", host.Out[0], StringComparison.Ordinal);
        Assert.DoesNotContain(host.Out, static l => l.Contains("slug-b", StringComparison.Ordinal));
        Assert.Equal(["a|check-in"], host.Health);
        var journal = Assert.Single(host.Journal_);
        Assert.Equal("ahd-missed", Str(journal, "kind"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void AnUnarmedPlainSendArmsNoFailsafe(bool plainSend, bool armed)
    {
        IReadOnlyList<JsonObject> peers = armed ? [new JsonObject { ["type"] = "plain-send-armed", ["session_id"] = Session, ["request_id"] = "a", ["at"] = Iso(0) }] : [];
        var host = new Host { Open = [Dispatch("a") with { PlainSend = plainSend, PeerRecords = peers }] };

        _ = Timer(host).Run();

        Assert.Equal(!plainSend || armed, host.Out.Any(static l => l.StartsWith("FAILSAFE HIT (ah bug): a ", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheWakeTextCarriesTheIdAndTheFiveIncidentStepsVerbatim()
    {
        var host = new Host { Open = [Dispatch("a")] };

        _ = Timer(host).Run();

        Assert.Equal(
            "1) ah roster stall-diagnose a  2) handle the late dispatch yourself  3) tell the user the diagnosis line (PushNotification if unattended)  4) add the diagnosis to the run's friction list as an ah bug  5) restart: ah roster overdue --cwd /w (run_in_background)",
            host.Out[^1]);
    }

    [Fact]
    public void ARestartedTimerDoesNotFireAStepItAlreadyReportedAndFiresForTheNextDispatch()
    {
        var host = new Host { Open = [Dispatch("a"), Dispatch("b", sentSeconds: 100)] };
        _ = Timer(host).Run();
        host.Out.Clear();
        host.Health.Clear();

        var second = Timer(host).Run();

        Assert.Equal(0, second);
        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE HIT (ah bug): b ", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE HIT (ah bug): a ", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEventWrittenButNeverAcknowledgedDoesNotResetTheDeadline()
    {
        var host = new Host { Open = [Dispatch("a", gates: [Nudge("a", Eta)])], Events = [new AhdEventRow("e1", "a", "check-in", null)] };

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.Contains("ahd check-in not delivered", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAcknowledgedEventForOneDispatchRearmsOnlyThatOneAndLeavesTheOtherToFire()
    {
        var gates = new[] { Nudge("a", Eta) };
        var host = new Host
        {
            Open = [Dispatch("a", gates: gates), Dispatch("b")],
            Events = [new AhdEventRow("e1", "a", "check-in", Ms(Eta + 5))],
        };

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE HIT (ah bug): b ", StringComparison.Ordinal));
        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE HIT (ah bug): a ", StringComparison.Ordinal) && l.Contains("check-in", StringComparison.Ordinal));
    }

    [Fact]
    public void ADeliveredEventWithAnUnknownOrMissingIdCancelsNothingAndIsJournalledOnce()
    {
        var host = new Host
        {
            Open = [Dispatch("a")],
            Events = [new AhdEventRow("e1", "zzz", "check-in", Ms(10)), new AhdEventRow("e2", null, null, Ms(10))],
        };

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE HIT (ah bug): a ", StringComparison.Ordinal));
        Assert.Equal(2, host.Journal_.Count(static j => Str(j, "kind") == "unknown-id"));
    }

    [Fact]
    public void AReplyThatLandsBeforeTheDeadlineMeansNoFire()
    {
        var host = new Host { Open = [Dispatch("a")] };
        host.OnSleep = h =>
        {
            if ((h.Now - T0).TotalSeconds >= Eta + 30)
            {
                h.Open = [];
            }
        };

        var code = Timer(host).Run();

        Assert.Equal(0, code);
        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
    }

    /// <summary>A checked-in dispatch whose silent step ahd keeps writing that it holds back, up to <paramref name="heldUntilSeconds"/> after the send.</summary>
    private static Host HeldHost(double heldUntilSeconds)
    {
        var gates = new List<JsonObject> { Nudge("a", Eta) };
        var host = new Host { Open = [Dispatch("a", gates: gates)], Events = [new AhdEventRow("e1", "a", "check-in", Ms(Eta))] };
        host.OnSleep = h =>
        {
            var at = (h.Now - T0).TotalSeconds;
            if (at >= Eta * 1.5 && at <= heldUntilSeconds)
            {
                gates.Add(new JsonObject { ["type"] = "silent-held", ["session_id"] = Session, ["request_id"] = "a", ["ts"] = Iso(at) });
            }
        };
        return host;
    }

    [Fact]
    public void WhileAhdHoldsTheSilentStepTheFailsafeStaysQuiet()
    {
        var host = HeldHost(double.PositiveInfinity);

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFailsafeCountsItsMarginFromTheLastHoldNotFromTheDueTime()
    {
        const double HeldUntil = Eta * 3;
        var host = HeldHost(HeldUntil);

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.True(host.Now >= T0.AddSeconds(HeldUntil + 100));
    }

    /// <summary>A hold up to <paramref name="heldUntilSeconds"/>, then ahd fires 15 seconds later; the lead acks <paramref name="ackAfterSeconds"/> after that, if at all.</summary>
    private static Host FiredAfterAHold(double heldUntilSeconds, double? ackAfterSeconds)
    {
        var host = HeldHost(heldUntilSeconds);
        var held = host.OnSleep!;
        var fired = false;
        host.OnSleep = h =>
        {
            held(h);
            var at = (h.Now - T0).TotalSeconds;
            if (!fired && at >= heldUntilSeconds + 15)
            {
                fired = true;
                h.Open = [h.Open[0] with { Gates = [.. h.Open[0].Gates, Nudge("a", at)] }];
            }

            if (ackAfterSeconds is { } after && at >= heldUntilSeconds + 15 + after && h.Events.Count == 1)
            {
                h.Events = [.. h.Events, new AhdEventRow("e2", "a", "silent", Ms(at))];
            }
        };
        return host;
    }

    [Fact]
    public void ALeadThatAcksAFiringWithinTheMarginGetsNoFalseHit()
    {
        // past the margin from the last hold row, inside it from the firing
        var host = FiredAfterAHold(Eta * 3, ackAfterSeconds: 110);

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAFiringNeverAckedStillHitsAfterTheMargin()
    {
        var host = FiredAfterAHold(Eta * 3, ackAfterSeconds: null);

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.True(host.Now >= T0.AddSeconds((Eta * 3) + 15 + 120));
    }

    [Fact]
    public void TheSilentStepWaitsOutAHoldWithAKnownEnd()
    {
        var host = new Host { Open = [Dispatch("a", held: Ms(1_000_000), gates: [Nudge("a", Eta)])], Events = [new AhdEventRow("e1", "a", "check-in", Ms(Eta))] };

        _ = Timer(host).Run();

        Assert.DoesNotContain(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeAHoldAlreadyOverDoesNotDelayTheSilentStep()
    {
        var host = new Host { Open = [Dispatch("a", held: Ms(Eta), gates: [Nudge("a", Eta)])], Events = [new AhdEventRow("e1", "a", "check-in", Ms(Eta))] };

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
    }

    [Fact]
    public void NegativeWithNoHoldRowTheSilentStepIsMissedAtItsDueTime()
    {
        var host = HeldHost(0);

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        Assert.True(host.Now <= T0.AddSeconds((Eta * 1.5) + 120 + 30));
    }

    /// <summary>Runs an undelivered dispatch with the host clock jumping <paramref name="jump"/> once, a minute in; returns how long after the send the failsafe hit.</summary>
    private static TimeSpan FailsafeAfterAJump(TimeSpan jump)
    {
        var host = new Host { Open = [Dispatch("a")] };
        var jumped = false;
        host.OnSleep = h =>
        {
            if (!jumped && h.Now >= T0.AddMinutes(1))
            {
                jumped = true;
                h.Now += jump;
            }
        };

        _ = Timer(host).Run();

        Assert.Contains(host.Out, static l => l.StartsWith("FAILSAFE", StringComparison.Ordinal));
        return host.Now - T0;
    }

    [Fact]
    public void TimeAsleepDoesNotCountTowardTheFailsafe()
    {
        Assert.True(FailsafeAfterAJump(TimeSpan.FromMinutes(280)) >= TimeSpan.FromMinutes(280) + TimeSpan.FromSeconds(Eta + 120));
    }

    [Fact]
    public void NegativeAGapUnderTheSleepLineStillCountsTowardTheFailsafe()
    {
        Assert.True(FailsafeAfterAJump(TimeSpan.FromMinutes(4)) < TimeSpan.FromSeconds(Eta + 120 + 60));
    }

    [Fact]
    public void ABusyHoldDefersTheDeadlineSoNothingFiresInsideIt()
    {
        var host = new Host { Open = [Dispatch("a", held: Ms(Eta * 3))] };

        _ = Timer(host).Run();

        Assert.Equal(T0 + TimeSpan.FromSeconds((Eta * 3) + 120), host.Now);
    }

    [Fact]
    public void ALaterAcknowledgedAhdEventAfterAMissWritesRecoveredAndClearsTheHealthFile()
    {
        var host = new Host
        {
            Memory = new OverdueMemory(new HashSet<string>(), new HashSet<string>(), Ms(-100)),
            Events = [new AhdEventRow("e1", "a", "check-in", Ms(50))],
            Open = [Dispatch("a", gates: [Nudge("a", Eta)])],
        };

        _ = Timer(host).Run();

        Assert.Contains(host.Journal_, static j => Str(j, "kind") == "ahd-recovered");
        Assert.Equal(1, host.Cleared);
    }

    [Fact]
    public void NothingOpenEndsAtTheHeartbeatWithNoFire()
    {
        var host = new Host();

        _ = Timer(host).Run();

        Assert.Equal(T0 + TimeSpan.FromMinutes(20), host.Now);
        Assert.StartsWith("overdue timer: heartbeat; open dispatches: 0; overdue: 0", host.Out[0], StringComparison.Ordinal);
    }
}
