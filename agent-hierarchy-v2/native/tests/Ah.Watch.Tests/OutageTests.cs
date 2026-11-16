using System.Globalization;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.TestKit;

namespace Ah.Watch.Tests;

/// <summary>When two or more sessions fail together the daemon holds every resume, probes with one session, and releases the rest one after another.</summary>
public sealed class OutageTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-03-01T12:00:00Z", CultureInfo.InvariantCulture);

    private sealed class Rig
    {
        internal FakeHost Host { get; } = new() { MemberPicksUpResumes = false };

        internal FakeClock Clock { get; } = new(Start);

        internal WatchTick Tick()
        {
            var files = new NullFiles();
            var env = new MapEnv();
            return new WatchTick(new WatchDependencies(
                Host, env, Clock, files, HierarchyModule.CreateRecovery(files, env, Clock), WatchModule.CreateStatusDirs(), () => true));
        }

        /// <summary>One poll, then the clock moves on.</summary>
        internal async Task PollAsync(WatchTick tick, int stepMs = 15_000)
        {
            await tick.RunAsync(CancellationToken.None);
            Clock.UtcNow = Clock.UtcNow.AddMilliseconds(stepMs);
        }

        internal IEnumerable<WatchMail> Outage => Host.Mails.Where(static m => m.Event == "outage");

        internal IEnumerable<WatchMail> Entries => Host.Mails.Where(static m => m.Event == "resume");

        internal IEnumerable<JsonObject> OutageRows => Host.AppendedPeers.Where(static r => r.Str("type") == "outage");
    }

    private static readonly int[] SeedMinutes = [5, 10];

    private static readonly int[] ProbeWaits = [10, 60, 300, 600, 600];

    private static string Iso(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static JsonObject Failed(DateTimeOffset at, string error = "overloaded") => new() { ["activity"] = "failed", ["failed_at"] = Iso(at), ["error"] = error };

    private static JsonObject Normal(string activity, DateTimeOffset at) => new() { ["activity"] = activity, ["at"] = Iso(at) };

    /// <summary>Orchestrator sessions, each with one team and live members. Member <c>a1</c> is session <c>s-a1</c>.</summary>
    private static Rig Fleet(params (string Orchestrator, string Team, string[] Members)[] orchestrators)
    {
        var rig = new Rig();
        var pid = 100;
        foreach (var (orchestrator, team, members) in orchestrators)
        {
            rig.Host.Registrations.Add(new WatchRegistration(
                orchestrator, "claude", pid++, "2026-03-01T11:00:00.000Z", [new WatchBox($"orchestrator@{team}", $"/boxes/{team}")], "/private/tmp/ahprobe.h"));
            rig.Host.Owned[orchestrator] =
            [
                new OwnedTeam(
                    team,
                    new JsonObject(),
                    [.. members.Select(m => new MemberView(m, new JsonObject { ["role"] = "implementor" }, true, null, null, null, $"s-{m}"))]),
            ];
            foreach (var m in members)
            {
                rig.Host.Activity[$"s-{m}"] = Normal("working", Start);
            }

            rig.Host.Activity[orchestrator] = Normal("working", Start);
        }

        rig.Host.FeedBackGates = true;
        rig.Host.Stamp = () => Iso(rig.Clock.UtcNow);
        return rig;
    }

    /// <summary>Announces the sessions, then fails the given ones a few seconds apart ending just before the next poll.</summary>
    private static async Task<WatchTick> FailAsync(Rig rig, params string[] sessions)
    {
        var tick = rig.Tick();
        await rig.PollAsync(tick);
        var t = rig.Clock.UtcNow;
        for (var i = 0; i < sessions.Length; i++)
        {
            rig.Host.Activity[sessions[i]] = Failed(t.AddSeconds(-2 * (sessions.Length - i)));
        }

        return tick;
    }

    [Fact]
    public async Task FourSessionsFailingTogetherOpenOneOutageAndTheOrchestratorIsToldOnce()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2", "s-a3");

        await rig.PollAsync(tick);

        var start = Assert.Single(rig.Outage);
        Assert.Equal("orchestrator@alpha", start.To);
        Assert.Equal("ah: connection outage: 4 sessions held (this session, a1, a2, a3); probing at 10 s, 60 s, 5 min, then every 10 min.", start.Text);
        var row = Assert.Single(rig.OutageRows);
        Assert.Equal("open", row.Str("status"));
        Assert.Equal(4, row.Get("held")!.Value.AsArray().Count);
        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event is "resume" or "member-resume");
    }

    private static JsonArray Dropped(Rig rig) => rig.OutageRows.Last().Get("dropped_probes")!.Value.AsArray();

    private static IEnumerable<WatchMail> Ceiling(Rig rig) =>
        rig.Host.Mails.Where(static m => m.Event == "api-failed" && m.Text.Contains("kept failing", StringComparison.Ordinal));

    /// <summary>Polls on, and makes each session that was just resumed fail again straight away.</summary>
    private static async Task PollFailingAgainAsync(Rig rig, WatchTick tick, int stepMs, params string[] sessions)
    {
        var entries = rig.Entries.Count();
        await rig.PollAsync(tick, stepMs);
        var now = rig.Clock.UtcNow;
        if (rig.Entries.Count() > entries)
        {
            foreach (var session in sessions)
            {
                rig.Host.Activity[session] = Failed(now.AddSeconds(-1));
            }
        }
    }

    [Fact]
    public async Task ASessionFailingEveryTurnForMoreThanTwoHoursIsNeverGivenUpOn()
    {
        var rig = Fleet(("o1", "alpha", ["a1"]));
        var tick = await FailAsync(rig, "s-a1");
        for (var i = 0; i < 400 && (rig.Clock.UtcNow - Start).TotalHours < 2; i++)
        {
            await PollFailingAgainAsync(rig, tick, 30_000, "s-a1");
        }

        Assert.True((rig.Clock.UtcNow - Start).TotalHours >= 2);
        var resumed = rig.Entries.Count();
        Assert.InRange(resumed, 10, 20);

        for (var i = 0; i < 30; i++)
        {
            await PollFailingAgainAsync(rig, tick, 600_000, "s-a1");
        }

        Assert.True(rig.Entries.Count() > resumed);
        Assert.Empty(Ceiling(rig));
        Assert.DoesNotContain(rig.Host.Mails, static m => m.Text.Contains("Needs a person", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASessionThatFailsLessThanTwoHoursIsStillResumed()
    {
        var rig = Fleet(("o1", "alpha", ["a1"]));
        var tick = await FailAsync(rig, "s-a1");
        for (var i = 0; i < 200 && (rig.Clock.UtcNow - Start).TotalMinutes < 100; i++)
        {
            await PollFailingAgainAsync(rig, tick, 30_000, "s-a1");
        }

        Assert.Empty(Ceiling(rig));
        Assert.True(rig.Entries.Count() >= 8);
    }

    [Fact]
    public async Task AHeldSessionIsNotGivenTheEpisodeCeilingWhileTheOutageStaysOpen()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2");
        for (var i = 0; i < 17; i++)
        {
            await PollFailingAgainAsync(rig, tick, 600_000, "o1");
        }

        Assert.Empty(Ceiling(rig));
        Assert.Contains(rig.Outage, static m => m.Text.StartsWith("ah: outage ongoing for 2 h", StringComparison.Ordinal));
        Assert.Equal("open", rig.OutageRows.Last().Str("status"));
    }

    [Fact]
    public async Task AProbeWhoseEntryCannotBeWrittenIsReplacedByTheNextHeldSession()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "s-a1", "s-a2");
        rig.Host.FailMail = static mail => mail.Event == "resume" && mail.To == "a1@alpha";

        for (var i = 0; i < 6; i++)
        {
            await rig.PollAsync(tick);
        }

        Assert.Equal(["a2@alpha"], rig.Entries.Select(static m => m.To));
        var dropped = Assert.Single(Dropped(rig)).AsObject();
        Assert.Equal("s-a1", dropped.Str("session_id"));
        Assert.Equal("write-failed", dropped.Str("reason"));
        Assert.Equal("s-a2", rig.OutageRows.Last().Str("probe_session"));
        Assert.Equal(2, (int)rig.OutageRows.Last().Get("probes")!.Value.AsNumber());
    }

    [Fact]
    public async Task AProbeSessionThatLeftTheWatchIsReplacedByTheNextHeldSession()
    {
        var rig = Fleet(("o1", "alpha", ["a1"]), ("o2", "beta", ["b1"]));
        var tick = await FailAsync(rig, "o1", "o2");
        await rig.PollAsync(tick);
        Assert.Equal("o1", rig.OutageRows.Last().Str("probe_session"));
        _ = rig.Host.Registrations.RemoveAll(static r => r.SessionId == "o1");

        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        var dropped = Assert.Single(Dropped(rig)).AsObject();
        Assert.Equal("o1", dropped.Str("session_id"));
        Assert.Equal("left-watch", dropped.Str("reason"));
        Assert.Equal("o2", rig.OutageRows.Last().Str("probe_session"));
        Assert.Contains(rig.Host.Mails, static m => m.Event == "resume" && m.To == "orchestrator@beta");
    }

    [Fact]
    public async Task AProbeThatStaysFailedWithTheSameFailureAcrossTwoProbeTimesIsReplaced()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "s-a1", "s-a2");

        for (var i = 0; i < 60; i++)
        {
            await rig.PollAsync(tick);
        }

        Assert.Equal(["a1@alpha", "a1@alpha", "a2@alpha"], rig.Entries.Select(static m => m.To).Take(3));
        var dropped = Assert.Single(Dropped(rig)).AsObject();
        Assert.Equal("s-a1", dropped.Str("session_id"));
        Assert.Equal("unanswered", dropped.Str("reason"));
    }

    [Fact]
    public async Task WhenNoHeldSessionCanBeProbedTheOutageEndsAndEachSessionIsResumedOnItsOwn()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "s-a1", "s-a2");
        rig.Host.FailMail = static mail => mail.Event == "resume" && mail.Text.Contains("of a connection outage", StringComparison.Ordinal);

        for (var i = 0; i < 6; i++)
        {
            await rig.PollAsync(tick);
        }

        var last = rig.OutageRows.Last();
        Assert.Equal("over", last.Str("status"));
        Assert.Equal("no-probe", last.Str("reason"));
        var stopped = Assert.Single(rig.Outage, static m => m.Text.StartsWith("ah: outage handling stopped", StringComparison.Ordinal));
        Assert.Equal("ah: outage handling stopped: no held session could be probed (a1, a2); resuming each on its own schedule.", stopped.Text);
        rig.Host.FailMail = null;

        for (var i = 0; i < 12; i++)
        {
            await rig.PollAsync(tick);
        }

        Assert.Equal(["a1@alpha", "a2@alpha"], rig.Entries.Select(static m => m.To).Distinct().Order(StringComparer.Ordinal));
        Assert.Single(rig.OutageRows.Select(static r => r.Str("started_at")).Distinct());
    }

    [Fact]
    public async Task AProbeThatFailsAgainWithANewFailureIsNotReplaced()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2");

        for (var i = 0; i < 120 && rig.Host.Mails.Count(static m => m.Event == "resume") < 4; i++)
        {
            await PollFailingAgainAsync(rig, tick, 15_000, "o1");
        }

        Assert.Empty(Dropped(rig));
        Assert.Equal("o1", rig.OutageRows.Last().Str("probe_session"));
        Assert.Equal("open", rig.OutageRows.Last().Str("status"));
        Assert.InRange(rig.OutageRows.Last().Get("probes")!.Value.AsNumber(), 4, 100);
    }

    [Fact]
    public async Task AFailureAlreadyResumedStillCountsTowardAnOutageAndOnlyTheNewOneIsHeld()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "s-a1");
        await rig.PollAsync(tick);
        await rig.PollAsync(tick);
        Assert.Equal(["a1@alpha"], rig.Entries.Select(static m => m.To));
        rig.Host.Activity["s-a2"] = Failed(rig.Clock.UtcNow.AddSeconds(-2));

        await rig.PollAsync(tick);

        var start = Assert.Single(rig.Outage);
        Assert.StartsWith("ah: connection outage: 1 sessions held (a2);", start.Text, StringComparison.Ordinal);
        var held = Assert.Single(rig.OutageRows).Get("held")!.Value.AsArray();
        Assert.Equal("s-a2", Assert.Single(held).AsObject().Str("session_id"));
        Assert.Single(rig.Entries);
    }

    [Fact]
    public async Task TwoFailuresThatWereBothAlreadyResumedOpenNoOutage()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = rig.Tick();
        await rig.PollAsync(tick);
        var t = rig.Clock.UtcNow;
        foreach (var (subject, age) in new[] { ("s-a1", 12), ("s-a2", 6) })
        {
            var failedAt = Iso(t.AddSeconds(-age));
            rig.Host.Activity[subject] = Failed(t.AddSeconds(-age));
            rig.Host.SeededGates.Add(new JsonObject { ["type"] = "resume", ["session_id"] = "o1", ["subject"] = subject, ["failed_at"] = failedAt, ["n"] = 1, ["episode"] = failedAt });
        }

        await rig.PollAsync(tick);

        Assert.Empty(rig.OutageRows);
        Assert.Empty(rig.Outage);
    }

    [Fact]
    public async Task AnOutageEventThatCouldNotBeMailedIsSentAgainAndOnlyOnceItWentOut()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2");
        rig.Host.FailMail = static mail => mail.Event == "outage";

        await rig.PollAsync(tick);

        Assert.Empty(rig.Outage);
        Assert.DoesNotContain(rig.Host.AppendedPeers, static r => r.Str("type") == "outage-ack");
        rig.Host.FailMail = null;

        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        Assert.Single(rig.Outage);
        Assert.Single(rig.Host.AppendedPeers, static r => r.Str("type") == "outage-ack");
    }

    [Fact]
    public async Task TwoSessionsFailingTwoMinutesApartAreNotAnOutageAndAreResumedOnTheirOwn()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = rig.Tick();
        await rig.PollAsync(tick);
        var t = rig.Clock.UtcNow;
        rig.Host.Activity["s-a1"] = Failed(t.AddSeconds(-130));
        rig.Host.Activity["s-a2"] = Failed(t.AddSeconds(-20));

        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        Assert.Empty(rig.OutageRows);
        Assert.Empty(rig.Outage);
        Assert.Equal(["a1@alpha", "a2@alpha"], rig.Entries.Select(static m => m.To).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OneFailedSessionIsNotAnOutage()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "s-a1");

        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        Assert.Empty(rig.OutageRows);
        Assert.Single(rig.Entries);
    }

    [Fact]
    public async Task TheProbeIsTheOrchestratorAndGoesOutAtTenSecondsThenSixtyThenFiveMinutesThenEveryTenMinutes()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2");
        var expected = ProbeWaits;
        var failedAt = ((DateTimeOffset?)null);

        foreach (var wait in expected)
        {
            var before = rig.Entries.Count();
            var from = failedAt ?? rig.Clock.UtcNow.AddSeconds(-6);
            DateTimeOffset? sentAt = null;
            for (var i = 0; i < 80 && sentAt is null; i++)
            {
                var at = rig.Clock.UtcNow;
                await rig.PollAsync(tick);
                if (rig.Entries.Count() > before)
                {
                    sentAt = at;
                }
            }

            Assert.NotNull(sentAt);
            var waited = (sentAt.Value - from).TotalSeconds;
            Assert.InRange(waited, wait, wait + 30);
            failedAt = rig.Clock.UtcNow.AddSeconds(-1);
            rig.Host.Activity["o1"] = Failed(failedAt.Value);
        }

        Assert.All(rig.Entries, static m => Assert.StartsWith("ah: Your last turn ended on an API error", m.Text, StringComparison.Ordinal));
        Assert.DoesNotContain(rig.Host.Mails, static m => m.To is "a1@alpha" or "a2@alpha");
        Assert.Equal(5, (int)rig.OutageRows.Last().Get("probes")!.Value.AsNumber());
    }

    [Fact]
    public async Task WithNoOrchestratorHeldTheEarliestFailedMemberIsTheProbe()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        var tick = await FailAsync(rig, "s-a2", "s-a1", "s-a3");

        for (var i = 0; i < 3; i++)
        {
            await rig.PollAsync(tick);
        }

        var entry = Assert.Single(rig.Entries);
        Assert.Equal("a2@alpha", entry.To);
        Assert.Equal("s-a2", entry.ToSession);
    }

    [Fact]
    public async Task WhenTheProbesTurnEndsNormallyTheOutageIsOverAndTheRestAreReleasedFifteenSecondsApart()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2", "s-a3");
        for (var i = 0; i < 3; i++)
        {
            await rig.PollAsync(tick);
        }

        Assert.Single(rig.Entries);
        rig.Host.Activity["o1"] = Normal("idle", rig.Clock.UtcNow);
        var perPoll = new List<string[]>();
        for (var i = 0; i < 4; i++)
        {
            var before = rig.Host.Mails.Count;
            await rig.PollAsync(tick);
            perPoll.Add([.. rig.Host.Mails.Skip(before).Where(static m => m.Event == "resume").Select(static m => m.To)]);
        }

        Assert.Equal([["a1@alpha"], ["a2@alpha"], ["a3@alpha"], []], perPoll);
        var over = Assert.Single(rig.Outage, static m => m.Text.Contains("outage over", StringComparison.Ordinal));
        Assert.StartsWith("ah: outage over after ", over.Text, StringComparison.Ordinal);
        Assert.EndsWith("; resuming 3 sessions, 15 s apart.", over.Text, StringComparison.Ordinal);
        Assert.Equal(2, rig.Outage.Count());
        Assert.Equal("over", rig.OutageRows.Last().Str("status"));
    }

    [Fact]
    public async Task NoResumeIsSentToAMemberWhileAnOutageHoldsItAndTheFirstIsSentOnTheRelease()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2", "s-a3");
        for (var i = 0; i < 6; i++)
        {
            await rig.PollAsync(tick);
        }

        Assert.DoesNotContain(rig.Host.Mails, static m => m.Event == "resume" && m.To is "a1@alpha" or "a2@alpha" or "a3@alpha");

        rig.Host.Activity["o1"] = Normal("idle", rig.Clock.UtcNow);
        for (var i = 0; i < 4; i++)
        {
            await rig.PollAsync(tick);
        }

        var first = rig.Host.Mails.First(static m => m.Event == "resume" && m.To == "a1@alpha");
        Assert.Contains("automatic resume 1.", first.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrchestratorsAreReleasedBeforeMembers()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "s-a1", "o1", "s-a2");
        rig.Host.Activity["s-a3"] = Normal("working", Start);
        for (var i = 0; i < 2; i++)
        {
            await rig.PollAsync(tick);
        }

        string[] names = ["a1", "a2", "a3"];
        rig.Host.Owned["o1"] = [new OwnedTeam("alpha", new JsonObject(), [.. names.Select(m => new MemberView(m, new JsonObject { ["role"] = "implementor" }, true, null, null, null, $"s-{m}"))])];
        rig.Host.Activity["s-a3"] = Normal("idle", rig.Clock.UtcNow);
        var order = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var before = rig.Host.Mails.Count;
            await rig.PollAsync(tick);
            order.AddRange(rig.Host.Mails.Skip(before).Where(static m => m.Event == "resume").Select(static m => m.To));
        }

        Assert.Equal(["orchestrator@alpha", "a1@alpha", "a2@alpha"], order.Where(static to => to != "a3@alpha"));
    }

    [Fact]
    public async Task AnotherSessionEndingATurnNormallyEndsTheOutage()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        rig.Host.Activity["s-a3"] = Normal("idle", Start.AddMinutes(-10));
        var tick = await FailAsync(rig, "s-a1", "s-a2");
        await rig.PollAsync(tick);
        Assert.Equal("open", rig.OutageRows.Last().Str("status"));

        rig.Host.Activity["s-a3"] = Normal("idle", rig.Clock.UtcNow);
        await rig.PollAsync(tick);

        Assert.Equal("over", rig.OutageRows.Last().Str("status"));
    }

    [Fact]
    public async Task ASessionGoingBlockedEndsTheOutage()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        var tick = await FailAsync(rig, "s-a1", "s-a2");
        await rig.PollAsync(tick);

        rig.Host.Activity["s-a3"] = Normal("blocked", rig.Clock.UtcNow);
        await rig.PollAsync(tick);

        Assert.Equal("over", rig.OutageRows.Last().Str("status"));
    }

    [Fact]
    public async Task ASessionGoingWorkingDoesNotEndTheOutage()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        var tick = await FailAsync(rig, "s-a1", "s-a2");
        await rig.PollAsync(tick);

        rig.Host.Activity["s-a3"] = Normal("working", rig.Clock.UtcNow);
        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        Assert.Equal("open", rig.OutageRows.Last().Str("status"));
        Assert.Single(rig.Outage);
    }

    [Fact]
    public async Task ASessionThatEndedATurnBeforeTheOutageStartedDoesNotEndIt()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        rig.Host.Activity["s-a3"] = Normal("idle", Start.AddMinutes(-1));
        var tick = await FailAsync(rig, "s-a1", "s-a2");

        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        Assert.Equal("open", rig.OutageRows.Last().Str("status"));
    }

    [Fact]
    public async Task FiveFailuresOfTheProbeInsideOneOutageAreOneEpisodeOfTheRelease()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2", "a3"]));
        foreach (var minutesBefore in SeedMinutes)
        {
            var episode = Start.AddMinutes(-minutesBefore);
            rig.Host.SeededGates.Add(new JsonObject { ["type"] = "resume", ["session_id"] = "o1", ["subject"] = "s-a1", ["failed_at"] = Iso(episode), ["episode"] = Iso(episode), ["ts"] = Iso(episode.AddSeconds(10)) });
            rig.Host.SeededGates.Add(new JsonObject { ["type"] = "episode-end", ["session_id"] = "o1", ["subject"] = "s-a1", ["episode"] = Iso(episode), ["ts"] = Iso(episode.AddMinutes(1)) });
        }

        var tick = await FailAsync(rig, "s-a1", "s-a2");
        var firstFailure = ((string)rig.Host.Activity["s-a1"]!.Str("failed_at")!);
        for (var failures = 0; failures < 5; failures++)
        {
            var before = rig.Entries.Count();
            for (var i = 0; i < 80 && rig.Entries.Count() == before; i++)
            {
                await rig.PollAsync(tick);
            }

            rig.Host.Activity["s-a1"] = Failed(rig.Clock.UtcNow.AddSeconds(-1));
        }

        rig.Host.Activity["s-a3"] = Normal("idle", rig.Clock.UtcNow);
        await rig.PollAsync(tick);
        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        var row = Assert.Single(rig.Host.Gates, g => g.Row.Str("type") == "resume" && g.Row.Str("subject") == "s-a1" && g.Row.Str("episode") == firstFailure);
        Assert.Equal(firstFailure, row.Row.Str("episode"));
        var entries = rig.Entries.Where(static m => m.To == "a1@alpha" && m.Text.Contains("automatic resume", StringComparison.Ordinal)).ToList();
        Assert.Contains("automatic resume 1.", Assert.Single(entries).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOutageOfTwoHoursIsReportedOnceAndProbingContinues()
    {
        var rig = Fleet(("o1", "alpha", ["a1", "a2"]));
        var tick = await FailAsync(rig, "o1", "s-a1", "s-a2");

        var probes = 0;
        for (var i = 0; i < 26; i++)
        {
            await rig.PollAsync(tick, stepMs: 300_000);
            if (rig.Entries.Count() > probes)
            {
                probes = rig.Entries.Count();
                rig.Host.Activity["o1"] = Failed(rig.Clock.UtcNow.AddSeconds(-299));
            }
        }

        Assert.Single(rig.Outage, static m => m.Text.Contains("outage ongoing for 2 h", StringComparison.Ordinal));
        Assert.Equal("open", rig.OutageRows.Last().Str("status"));
        Assert.True(rig.Entries.Count() >= 8, $"{rig.Entries.Count()} probes");
        Assert.DoesNotContain(rig.Host.Mails, static m => m.To is "a1@alpha" or "a2@alpha");
    }

    [Fact]
    public async Task EachOrchestratorWatchingAHeldSessionIsToldAboutItsOwnOnly()
    {
        var rig = Fleet(("o1", "alpha", ["a1"]), ("o2", "beta", ["b1"]));
        var tick = await FailAsync(rig, "s-a1", "s-b1");

        await rig.PollAsync(tick);

        Assert.Equal(2, rig.Outage.Count());
        var alpha = Assert.Single(rig.Outage, static m => m.To == "orchestrator@alpha");
        var beta = Assert.Single(rig.Outage, static m => m.To == "orchestrator@beta");
        Assert.Contains("1 sessions held (a1)", alpha.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("b1", alpha.Text, StringComparison.Ordinal);
        Assert.Contains("1 sessions held (b1)", beta.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("a1", beta.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheOutageEndIsToldToEachOrchestratorOnce()
    {
        var rig = Fleet(("o1", "alpha", ["a1"]), ("o2", "beta", ["b1"]));
        var tick = await FailAsync(rig, "s-a1", "s-b1");
        await rig.PollAsync(tick);
        rig.Host.Activity["o1"] = Normal("idle", rig.Clock.UtcNow);

        await rig.PollAsync(tick);
        await rig.PollAsync(tick);

        Assert.Equal(2, rig.Outage.Count(static m => m.Text.Contains("outage over", StringComparison.Ordinal)));
        Assert.Equal(2, rig.Outage.Count(static m => m.Text.Contains("connection outage", StringComparison.Ordinal)));
    }

}
