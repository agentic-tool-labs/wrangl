using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.TestKit;

namespace Ah.Watch.Tests;

/// <summary>While a lane is escalated each event its sub-orchestrator's session mails is also mailed to the parent, and the copy climbs while each parent's own lane is escalated.</summary>
public sealed class LaneCopyUpTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
    private static readonly double StartMs = Start.ToUnixTimeMilliseconds();
    private const double Minute = 60_000;

    private sealed class Rig
    {
        internal FakeHost Host { get; } = new();

        internal FakeClock Clock { get; } = new(Start);

        /// <summary>Adds a session that owns <paramref name="team"/> and polls its box.</summary>
        internal Rig Session(string sid, int pid, string team)
        {
            Host.Registrations.Add(new WatchRegistration(sid, "claude", pid, "2026-03-01T11:00:00.000Z", [new WatchBox($"orchestrator@{team}", $"/boxes/{team}")], "/private/tmp/ahprobe.h"));
            Host.Owned[sid] = [new OwnedTeam(team, new JsonObject(), [])];
            return this;
        }

        internal WatchTick Tick()
        {
            var env = new MapEnv([]);
            var recovery = HierarchyModule.CreateRecovery(new NullFiles(), env, Clock);
            return new WatchTick(new WatchDependencies(Host, env, Clock, new NullFiles(), recovery, WatchModule.CreateStatusDirs(), () => true, static _ => { }, static _ => { }));
        }

        internal async Task Pass(WatchTick tick, int polls = 5)
        {
            for (var i = 0; i < polls; i++)
            {
                await tick.RunAsync(CancellationToken.None);
                Clock.UtcNow = Clock.UtcNow.AddMilliseconds(15_000);
            }
        }

        internal List<WatchMail> Copies(string box) => [.. Host.Mails.Where(m => m.To == box && Str(m, "escalated_from") is not null)];
    }

    private static string? Str(WatchMail m, string key) => m.Data.Get(key) is { IsText: true } v ? v.AsString() : null;

    private static LaneFact Stale(string lane, string sub, string parent, string state = "working") =>
        new(lane, sub, false, false, null, state, StartMs - 40 * Minute, [], parent, lane);

    private static LaneFact Fresh(string lane, string sub, string parent) =>
        new(lane, sub, false, false, null, "working", StartMs + 10 * Minute, [], parent, lane);

    [Fact]
    public async Task ADeadSubOrchestratorsEscalationTellsTheOrchestratorToAskTheUser()
    {
        var rig = new Rig().Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid == "sM" ? [new LaneFact("b", "so-b", true, false, null, null, null, [], "mid", "b", DeadSinceMs: StartMs - 60 * Minute)] : [];

        await rig.Pass(rig.Tick());

        var mail = Assert.Single(rig.Host.Mails, static m => m.To == "orchestrator@mid" && m.Event == "lane-escalated");
        Assert.Contains("E-a", mail.Text, StringComparison.Ordinal);
        Assert.Contains("Ask the user whether to respawn", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeAStaleLanesEscalationDoesNotTellTheOrchestratorToAskTheUser()
    {
        var rig = new Rig().Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid == "sM" ? [Stale("b", "so-b", "mid")] : [];

        await rig.Pass(rig.Tick());

        var mail = Assert.Single(rig.Host.Mails, static m => m.To == "orchestrator@mid" && m.Event == "lane-escalated");
        Assert.DoesNotContain("Ask the user", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEventOfAnEscalatedLanesSubOrchestratorIsAlsoMailedToTheParentNamingTheSubOrchestrator()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [Stale("mid", "so-m", "top")],
            "sM" => [Stale("b", "so-b", "mid")],
            _ => [],
        };
        await rig.Pass(rig.Tick());

        var own = Assert.Single(rig.Host.Mails, static m => m.To == "orchestrator@mid" && m.Event == "lane-escalated");
        Assert.Equal("b", Str(own, "lane"));
        var copy = Assert.Single(rig.Copies("orchestrator@top"));
        Assert.Equal("lane-escalated", copy.Event);
        Assert.Equal("so-m", Str(copy, "escalated_from"));
        Assert.Equal("b", Str(copy, "lane"));
        Assert.Equal("mid", Str(copy, "team"));
    }

    [Fact]
    public async Task AnEventOfALaneThatIsNotEscalatedIsNotCopied()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [Fresh("mid", "so-m", "top")],
            "sM" => [Stale("b", "so-b", "mid")],
            _ => [],
        };
        await rig.Pass(rig.Tick());

        Assert.Single(rig.Host.Mails, static m => m.To == "orchestrator@mid" && m.Event == "lane-escalated");
        Assert.Empty(rig.Copies("orchestrator@top"));
    }

    [Fact]
    public async Task AnEventOfAPausedLaneIsNotCopied()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [Stale("mid", "so-m", "top", "paused")],
            "sM" => [Stale("b", "so-b", "mid")],
            _ => [],
        };
        await rig.Pass(rig.Tick());

        Assert.Empty(rig.Copies("orchestrator@top"));
    }

    [Fact]
    public async Task EventsAfterTheLaneRecoveredAreNotCopied()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        var tickState = new { Mid = Stale("mid", "so-m", "top") };
        var midFact = tickState.Mid;
        var below = new List<LaneFact> { Stale("b", "so-b", "mid") };
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [midFact],
            "sM" => [.. below],
            _ => [],
        };
        var tick = rig.Tick();
        await rig.Pass(tick);
        var before = rig.Copies("orchestrator@top").Count;
        Assert.Equal(1, before);

        midFact = Fresh("mid", "so-m", "top");
        await rig.Pass(tick, 3);
        below.Add(Stale("b2", "so-b2", "mid"));
        await rig.Pass(tick, 3);

        Assert.Contains(rig.Host.Mails, static m => m.To == "orchestrator@mid" && Str(m, "lane") == "b2");
        Assert.Equal(before, rig.Copies("orchestrator@top").Count);
    }

    [Fact]
    public async Task AnEventClimbsEveryLevelWhoseLaneIsEscalatedAndReachesEachRecipientOnce()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid").Session("sB", 102, "b");
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [Stale("mid", "so-m", "top")],
            "sM" => [Stale("b", "so-b", "mid")],
            "sB" => [Stale("c", "so-c", "b")],
            _ => [],
        };
        await rig.Pass(rig.Tick(), 8);

        var inMid = rig.Copies("orchestrator@mid");
        var copyOfC = Assert.Single(inMid);
        Assert.Equal("c", Str(copyOfC, "lane"));
        Assert.Equal("so-b", Str(copyOfC, "escalated_from"));

        var atTop = rig.Copies("orchestrator@top");
        Assert.Equal(2, atTop.Count);
        var fromB = Assert.Single(atTop, static m => Str(m, "lane") == "b");
        Assert.Equal("so-m", Str(fromB, "escalated_from"));
        var climbed = Assert.Single(atTop, static m => Str(m, "lane") == "c");
        Assert.Equal("so-m", Str(climbed, "escalated_from"));
        Assert.Empty(rig.Copies("orchestrator@b"));
    }

    [Fact]
    public async Task TheSameEventMailedByTwoSessionsOfTheLaneReachesTheParentOnce()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid").Session("sM2", 102, "mid");
        rig.Host.NothingAlreadyMailed = true;
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [Stale("mid", "so-m", "top")],
            "sM" or "sM2" => [Stale("b", "so-b", "mid")],
            _ => [],
        };
        await rig.Pass(rig.Tick());

        Assert.Equal(2, rig.Host.Mails.Count(static m => m.To == "orchestrator@mid" && m.Event == "lane-escalated"));
        Assert.Single(rig.Copies("orchestrator@top"));
    }

    [Fact]
    public async Task AJobOverdueMailedToTheLanesBoxAndToItsOwnerReachesTheParentOnce()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid == "sT" ? [Stale("mid", "so-m", "top")] : [];
        var sent = StartMs - 90 * Minute;
        rig.Host.JobsOf = team => team == "mid" ? [new JobFact("20261025-120000-aaaa", "mid", "impl", "sess-impl", "test", "dotnet test", 48847, "large", sent, null, null, false, true, [])] : [];
        await rig.Pass(rig.Tick());

        Assert.Single(rig.Host.Mails, static m => m.To == "orchestrator@mid" && m.Event == "job-overdue");
        Assert.Single(rig.Host.Mails, static m => m.To == "impl@mid");
        Assert.Single(rig.Copies("orchestrator@top"));
        Assert.Empty(rig.Copies("impl@top"));
    }

    [Fact]
    public async Task AJobOverdueOfAHealthyLaneStaysWithItsSubOrchestrator()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid == "sT" ? [Fresh("mid", "so-m", "top")] : [];
        var sent = StartMs - 90 * Minute;
        rig.Host.JobsOf = team => team == "mid" ? [new JobFact("20261025-120000-aaaa", "mid", "impl", "sess-impl", "test", "dotnet test", 48847, "large", sent, null, null, false, true, [])] : [];
        await rig.Pass(rig.Tick());

        Assert.Single(rig.Host.Mails, static m => m.To == "orchestrator@mid" && m.Event == "job-overdue");
        Assert.DoesNotContain(rig.Host.Mails, static m => m.To == "orchestrator@top");
    }

    [Fact]
    public async Task TwentyEventsOfOneEscalatedLaneReachTheTopUnderTheirOwnEventName()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        var lanes = Enumerable.Range(0, 20).Select(static i => Stale($"b{i}", $"so-b{i}", "mid")).ToList();
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [Stale("mid", "so-m", "top")],
            "sM" => lanes,
            _ => [],
        };
        await rig.Pass(rig.Tick());

        var copies = rig.Copies("orchestrator@top");
        Assert.Equal(20, copies.Count);
        Assert.All(copies, static m => Assert.Equal("lane-escalated", m.Event));
    }

    [Fact]
    public async Task CopyingUpOnlyMailsEventsToOrchestratorBoxesAndNeverResumesOrReAddressesAnything()
    {
        var rig = new Rig().Session("sT", 100, "top").Session("sM", 101, "mid");
        rig.Host.LanesOf = sid => sid switch
        {
            "sT" => [Stale("mid", "so-m", "top")],
            "sM" => [Stale("b", "so-b", "mid")],
            _ => [],
        };
        await rig.Pass(rig.Tick());

        Assert.NotEmpty(rig.Copies("orchestrator@top"));
        Assert.All(rig.Host.Mails, static m =>
        {
            Assert.StartsWith("orchestrator@", m.To, StringComparison.Ordinal);
            Assert.StartsWith("lane-", m.Event, StringComparison.Ordinal);
        });
    }
}
