using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch.Tests;

/// <summary>ah2-334: a QA entry that waits QueueStallMs with no live qa member on its team raises queue-stall-unstaffed once per entry.</summary>
public sealed class QaUnstaffedTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static double Ms(double minutesAfterT0) => T0.AddMinutes(minutesAfterT0).ToUnixTimeMilliseconds();

    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private static QaJob Job(string id = "q1", string state = "queued", string? team = "tm") =>
        new(id, Sha, null, "full", "/repo", false, "adhoc", ["impl@" + team], team, (long)Ms(0), state, null, null, null, null, null, null);

    private static MemberView Member(string name, string role, bool? live = null) =>
        new(name, new JsonObject { ["role"] = role }, live, "idle", null, Timestamps.ToIsoString(T0), "sess-" + name);

    private static OwnedTeam Team(params MemberView[] members) => new("tm", new JsonObject(), members);

    private static IReadOnlyList<WatchEvent> Run(double minutes, OwnedTeam team, IReadOnlyList<QaJob> jobs, HashSet<string> told)
    {
        var events = QueueEvents.Evaluate("s1", Ms(minutes), [team], [], _ => [], _ => false, told, out var keys, qaJobs: jobs);
        foreach (var key in keys.Values)
        {
            _ = told.Add(key);
        }

        return events;
    }

    [Fact]
    public void AnEntryWaitingPastTheThresholdWithNoQaMemberRaisesOneAlarmNamingTheEntryAndTheFix()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var team = Team(Member("impl-1", "implementor"));

        var ev = Assert.Single(Run(11, team, [Job()], told));

        Assert.Equal("queue-stall-unstaffed", ev.Name);
        Assert.Equal("tm", ev.Team);
        Assert.Equal("q1", ev.Data.Str("qa_entry"));
        Assert.Equal("/repo", ev.Data.Str("repo"));
        Assert.Equal(Sha, ev.Data.Str("sha"));
        Assert.Equal("full", ev.Data.Str("set"));
        Assert.Null(ev.Data.Str("item"));
        Assert.Contains("queued 11m", ev.Text, StringComparison.Ordinal);
        Assert.Contains("`ah roster spawn-ad-hoc qa --team tm --cwd /repo`", ev.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("withdraw", ev.Text, StringComparison.Ordinal);

        // once per entry: the next tick, still waiting, tells nothing
        Assert.Empty(Run(12, team, [Job()], told));
    }

    [Fact]
    public void NegativeALiveQaMemberMeansNoAlarmWhateverTheWait()
    {
        Assert.Empty(Run(60, Team(Member("qa-1", "qa")), [Job()], []));
        Assert.Empty(Run(60, Team(Member("qa-1", "qa", live: true)), [Job()], []));
    }

    [Fact]
    public void AQaMemberWhoseRowSaysNotLiveCountsAsNoMember()
    {
        Assert.Single(Run(11, Team(Member("qa-1", "qa", live: false)), [Job()], []));
    }

    [Fact]
    public void NegativeAnEntryUnderTheThresholdIsNotAlarmed()
    {
        Assert.Empty(Run(9.9, Team(), [Job()], []));
    }

    [Fact]
    public void NegativeAClaimedOrEndedEntryOrAnotherTeamsEntryIsNotAlarmed()
    {
        Assert.Empty(Run(30, Team(), [Job(state: "claimed"), Job("q2", "done"), Job("q3", "failed"), Job("q4", team: "other")], []));
    }

    [Fact]
    public void AnEntryIsToldAgainOnlyAfterAQaMemberCameAndWent()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        Assert.Single(Run(11, Team(), [Job()], told));
        Assert.Empty(Run(12, Team(), [Job()], told));

        // a QA member came: nothing told, and the episode ends
        Assert.Empty(Run(13, Team(Member("qa-1", "qa")), [Job()], told));

        // it went and the entry still waits: told again
        Assert.Single(Run(14, Team(), [Job()], told));
    }
}
