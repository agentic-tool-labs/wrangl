using Ah.Core.Json;
using Ah.TestKit;

namespace Ah.Hierarchy.Tests;

/// <summary>
/// b459 C1 (ADR adr-22): a status document computed from the entries carries a witness of what it read outside them and the horizon of
/// its clock rules. Inside the horizon, with the witness unchanged, the held document with its clock stamps rewritten is the document
/// a fresh compute gives.
/// </summary>
public sealed class StatusWitnessTests
{
    private const long T0 = StatusEntriesFixture.T0;
    private const long Day = 86_400_000;

    /// <summary>Liveness the test moves: the registration of each <c>name@team</c>, and the teams whose Orchestrator is gone.</summary>
    private sealed class Liveness : IMemberLiveness
    {
        internal Dictionary<string, (string Session, bool Live, string? Cwd)> Members { get; } = new(StringComparer.Ordinal);

        internal HashSet<string> Orphaned { get; } = new(StringComparer.Ordinal);

        internal bool Throws { get; set; }

        /// <summary>The liveness clock, and the age past which a member last seen at <see cref="SeenAtMs"/> no longer reads live: liveness by age alone, the process table untouched.</summary>
        internal Func<long> Now { get; set; } = static () => 0;

        internal long SeenAtMs { get; set; }

        internal long? MaxAgeMs { get; set; }

        private bool Fresh => MaxAgeMs is not { } max || Now() - SeenAtMs <= max;

        public MemberRegistrationLiveness? ForMember(string memberName, string team) => Throws
            ? throw new InvalidOperationException("ps failed")
            : Members.TryGetValue($"{memberName}@{team}", out var m) && (m.Live && Fresh) is var live
                ? new MemberRegistrationLiveness(m.Session, live, live ? "running" : "pid gone", null, m.Cwd, !live) : null;

        public Ah.Roster.TeamLife? TeamLifeOf(JsonValue team, IReadOnlyList<JsonObject> registrations) =>
            team.Member("team") is { IsText: true } t && Orphaned.Contains(t.AsString()) ? Ah.Roster.TeamLife.Orphaned : null;

        public bool PidIdentityLive(int pid, string? recordedStart) => true;
    }

    private sealed record Rig(StatusEntriesFixture Fixture, HierLib Lib, FakeClock Clock, Liveness Liveness, IDisposable Db) : IDisposable
    {
        internal string Main => Fixture.StatusDirs[0];

        internal (JsonObject Doc, StatusWitness? Witness) Read(string dir, long now)
        {
            Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(now);
            var doc = Lib.ReadStatusWitnessed(dir, now, out var witness);
            return (doc, witness);
        }

        /// <summary>A compute at <paramref name="now"/> through no witness.</summary>
        internal string Fresh(string dir, long now)
        {
            Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(now);
            return JsonText.Stringify(Lib.ReadStatus(dir, now, dir), 0);
        }

        public void Dispose() => Db.Dispose();
    }

    /// <summary>The backlog facts of a dir, as the entries record them: one row per open item, the last groom, and whether the backlog has an event.</summary>
    private static JsonValue Facts(string dir, long? lastGroomMs, bool events, params (string Wave, string State, long UpdatedMs, string Item)[] open) => Backlog.HealthFacts(
        (sql, _) => sql.Contains("GROUP BY 1, 2", StringComparison.Ordinal) ? [.. open.GroupBy(static o => (o.Wave, o.State)).Select(static g => new object?[] { g.Key.Wave, g.Key.State, (long)g.Count() })]
            : sql.Contains("state NOT IN", StringComparison.Ordinal) ? [.. open.Select(static o => new object?[] { o.Wave, o.State, o.UpdatedMs, null, o.Item })]
            : sql.Contains("FROM meta", StringComparison.Ordinal) ? lastGroomMs is { } at ? [new object?[] { "{\"ms\":" + at + ",\"summary\":null}" }] : []
            : sql.Contains("plan_events", StringComparison.Ordinal) && events ? [new object?[] { T0 - 1000 }]
            : [],
        dir);

    private static Rig Create(Func<StatusEntriesFixture, Func<string, JsonValue?>>? backlog = null, bool entries = true)
    {
        var fixture = StatusEntriesFixture.Create();
        // a member idle with background work open: it reads as working until an hour after it went idle
        fixture.Activity(fixture.StatusDirs[0], "s-impl", $$"""{"activity":"idle","bg_open":1,"at":"{{StatusEntriesFixture.Iso(T0 - 50_000)}}","state_since":"{{StatusEntriesFixture.Iso(T0 - 1_800_000)}}"}""");
        var db = Ah.StateDb.StateDbModule.Open(fixture.Home, mayCreate: true);
        var clock = new FakeClock(DateTimeOffset.FromUnixTimeMilliseconds(T0 + 60_000));
        var services = fixture.Services(entries: entries ? Ah.StateDb.StateDbModule.CreateStatusEntries(fixture.Home) : null, backlogFacts: backlog?.Invoke(fixture), clock: clock);
        var liveness = new Liveness { Now = () => clock.UtcNow.ToUnixTimeMilliseconds() };
        liveness.Members["t-implementor@t"] = ("s-impl", true, fixture.R);
        liveness.Members["t-reviewer@t"] = ("s-rev", false, null);
        services.Core.Liveness = liveness;
        var lib = (HierLib)services.Status;
        if (entries)
        {
            foreach (var dir in fixture.StatusDirs)
            {
                Assert.True(lib.RebuildStatusEntriesOf(dir).Ok);
            }
        }

        return new Rig(fixture, lib, clock, liveness, db);
    }

    /// <summary>A groomed backlog with three clock rules ahead: the groom falls due in an hour, an in-progress item goes stale in a day, a waiting one in a day and a half.</summary>
    private static Func<string, JsonValue?> Groomed(StatusEntriesFixture f) => dir => dir == f.StatusDirs[0]
        ? (JsonValue?)Facts(dir, T0 - (23 * 3_600_000L), true, ("now", PlanStates.InProgress, T0 - (2 * Day), "i1"), ("next", PlanStates.NotStarted, T0 - (12 * Day) - (Day / 2), "i2"))
        : null;

    [Fact]
    public void ARestampEqualsAFreshComputeOnBothSidesOfEveryHorizon()
    {
        using var rig = Create(Groomed);
        foreach (var dir in rig.Fixture.StatusDirs)
        {
            var t = T0 + 60_000;
            var (crossed, moved) = (0, 0);
            for (var step = 0; step < 80; step++)
            {
                var (doc, witness) = rig.Read(dir, t);
                Assert.NotNull(witness);
                Assert.Equal(t, witness.ComputedAtMs);
                // every clock rule of this fixture names its instant, and each lies ahead of the compute
                Assert.True(witness.HorizonMs > t, $"{dir} @{t - T0}: horizon {witness.HorizonMs - T0}");
                var end = double.IsPositiveInfinity(witness.HorizonMs) ? t + (3 * Day) : (long)witness.HorizonMs;
                foreach (var at in new[] { t + 1, t + ((end - t) / 2), end - 1 }.Where(at => at > t && at < end).Distinct())
                {
                    var fresh = rig.Fresh(dir, at);
                    Assert.Null(witness.Replay());
                    Assert.Equal(fresh, JsonText.Stringify(StatusWitness.Restamp(doc, at), 0));
                }

                if (double.IsPositiveInfinity(witness.HorizonMs))
                {
                    break;
                }

                // at the horizon the restamp path is not taken; where the rule shows, a restamp there would be wrong
                moved += rig.Fresh(dir, end) != JsonText.Stringify(StatusWitness.Restamp(doc, end), 0) ? 1 : 0;
                crossed++;
                t = end;
            }

            // the main dir holds the dispatches, the idle member and the backlog: its clock rules are crossed one after another
            Assert.True(dir != rig.Main || (crossed >= 6 && moved >= 6), $"{dir}: crossed {crossed}, moved {moved}");
        }
    }

    [Fact]
    public void NegativeAnObservationThatMovedFailsTheReplayAndTheHeldDocumentIsNeverChanged()
    {
        using var rig = Create(Groomed);
        var (doc, witness) = rig.Read(rig.Main, T0 + 60_000);
        Assert.NotNull(witness);
        var before = JsonText.Stringify(doc, 0);
        Assert.Null(witness.Replay());

        // a member's process died (liveness alone: no entry was written)
        rig.Liveness.Members["t-implementor@t"] = ("s-impl", false, rig.Fixture.R);
        Assert.Equal("replay-differed", witness.Replay());
        rig.Liveness.Members["t-implementor@t"] = ("s-impl", true, rig.Fixture.R);
        Assert.Null(witness.Replay());

        // a team's Orchestrator is proven gone
        _ = rig.Liveness.Orphaned.Add("u");
        Assert.Equal("replay-differed", witness.Replay());
        rig.Liveness.Orphaned.Clear();

        // the member's work tree is on another branch
        var head = Path.Combine(rig.Fixture.R, ".git", "HEAD");
        File.WriteAllText(head, "ref: refs/heads/other\n");
        Assert.Equal("replay-differed", witness.Replay());
        File.WriteAllText(head, "ref: refs/heads/main\n");
        Assert.Null(witness.Replay());

        // the config turns the hierarchy off
        var config = Path.Combine(rig.Fixture.Home, ".claude", "ah2", "agent-hierarchy.json");
        File.WriteAllText(config, "{\"enabled\":false}\n");
        Assert.Equal("replay-differed", witness.Replay());
        File.WriteAllText(config, "{\"enabled\":true}\n");
        Assert.Null(witness.Replay());

        // a replayed read throws
        rig.Liveness.Throws = true;
        Assert.Equal("replay-threw", witness.Replay());
        rig.Liveness.Throws = false;

        // a restamp is a new document; the held one keeps every byte
        var restamped = StatusWitness.Restamp(doc, T0 + 70_000);
        Assert.NotSame(doc, restamped.AsObject());
        Assert.Equal(before, JsonText.Stringify(doc, 0));
        Assert.Contains(StatusEntriesFixture.Iso(T0 + 70_000), JsonText.Stringify(restamped, 0), StringComparison.Ordinal);

        // the doctor's rebuild drops the hold: the entries are no longer the ones this compute read
        rig.Lib.ForgetStatusHolds();
        Assert.Equal("entries-not-current", witness.Replay());

        // the dir is removed
        var (again, second) = rig.Read(rig.Main, T0 + 80_000);
        Assert.NotNull(second);
        Assert.Null(second.Replay());
        Directory.Delete(rig.Main, recursive: true);
        Assert.Equal("replay-differed", second.Replay());
        Assert.NotEmpty(again["teams"].AsArray());
    }

    [Fact]
    public void ARuleWithNoInstantHasNoHorizonAheadAndNegativeAComputeOnTheStoresLeavesNoWitness()
    {
        // never groomed, no event, and nothing else that fixes when the groom fell due: due_since is the clock itself
        using (var rig = Create(f => dir => dir == f.StatusDirs[0] ? (JsonValue?)Facts(dir, null, false, ("later", PlanStates.Done, T0 - Day, "i1")) : null))
        {
            var (doc, witness) = rig.Read(rig.Main, T0 + 60_000);
            Assert.NotNull(witness);
            Assert.True(doc.ContainsKey("backlog"));
            Assert.True(witness.HorizonMs <= witness.ComputedAtMs);
        }

        // no entries store: the compute reads the stores, and nothing is witnessed
        using var stores = Create(entries: false);
        var (computed, none) = stores.Read(stores.Main, T0 + 60_000);
        Assert.NotEmpty(computed["teams"].AsArray());
        Assert.Null(none);
    }

    [Fact]
    public void ANeverGroomedBacklogWhoseDueSinceAnItemFixesIsRestampedLikeAnyOtherAndOneThatIsNotShownHasNoSay()
    {
        // never groomed, no event, one untriaged item: due_since is when the item was added, so the clock moves nothing
        using (var rig = Create(f => dir => dir == f.StatusDirs[0] ? (JsonValue?)Facts(dir, null, false, ("untriaged", PlanStates.NotStarted, T0 - Day, "i1")) : null))
        {
            var (doc, witness) = rig.Read(rig.Main, T0 + 60_000);
            Assert.NotNull(witness);
            Assert.True(doc.ContainsKey("backlog"));
            Assert.True(witness.HorizonMs > witness.ComputedAtMs);
            var end = double.IsPositiveInfinity(witness.HorizonMs) ? T0 + Day : (long)witness.HorizonMs;
            foreach (var at in new[] { T0 + 60_001, T0 + 60_000 + ((end - T0 - 60_000) / 2), end - 1 })
            {
                var fresh = rig.Fresh(rig.Main, at);
                Assert.Null(witness.Replay());
                Assert.Equal(fresh, JsonText.Stringify(StatusWitness.Restamp(doc, at), 0));
            }
        }

        // never groomed and empty: the status shows no backlog, so its clock rule (due_since = now) does not end the horizon
        using var empty = Create(f => dir => dir == f.StatusDirs[0] ? (JsonValue?)Facts(dir, null, false) : null);
        var (shown, held) = empty.Read(empty.Main, T0 + 60_000);
        Assert.NotNull(held);
        Assert.False(shown.ContainsKey("backlog"));
        Assert.True(held.HorizonMs > held.ComputedAtMs);
        Assert.Equal(empty.Fresh(empty.Main, T0 + 61_000), JsonText.Stringify(StatusWitness.Restamp(shown, T0 + 61_000), 0));
    }

    [Fact]
    public void TwoComputesOfOneDirInTheSameMillisecondEachGetTheirOwnWitness()
    {
        using var rig = Create(Groomed);
        var (first, a) = rig.Read(rig.Main, T0 + 60_000);
        // the member's process dies between the two computes; both write the same written_at
        rig.Liveness.Members["t-implementor@t"] = ("s-impl", false, rig.Fixture.R);
        var (second, b) = rig.Read(rig.Main, T0 + 60_000);

        Assert.Equal(first["written_at"].AsString(), second["written_at"].AsString());
        Assert.NotEqual(JsonText.Stringify(first, 0), JsonText.Stringify(second, 0));
        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.NotSame(a, b);
        // the earlier document cannot be vouched for by what the later compute saw
        Assert.Equal("replay-differed", a.Replay());
        Assert.Null(b.Replay());
    }

    [Fact]
    public void NegativeLivenessThatFlipsByAgeAloneFailsTheReplay()
    {
        using var rig = Create(Groomed);
        // the member reads live for 30 s after it was last seen; nothing in the process table or the entries changes
        rig.Liveness.SeenAtMs = T0 + 60_000;
        rig.Liveness.MaxAgeMs = 30_000;
        var (_, witness) = rig.Read(rig.Main, T0 + 60_000);
        Assert.NotNull(witness);

        rig.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(T0 + 90_000);
        Assert.Null(witness.Replay());
        rig.Clock.UtcNow = DateTimeOffset.FromUnixTimeMilliseconds(T0 + 90_001);
        Assert.Equal("replay-differed", witness.Replay());
    }
}
