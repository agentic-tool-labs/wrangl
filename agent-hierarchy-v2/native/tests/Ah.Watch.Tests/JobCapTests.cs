namespace Ah.Watch.Tests;

/// <summary>A run that ah test run killed at its cap raises one job-overdue event, step capped, to the Orchestrator and the owner.</summary>
public sealed class JobCapTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static double Ms(double minutesAfterT0) => T0.AddMinutes(minutesAfterT0).ToUnixTimeMilliseconds();

    private static long Sec(double minutesAfterT0) => T0.AddMinutes(minutesAfterT0).ToUnixTimeSeconds();

    // a foreground job sent at 0m that got its slot at 2m and that its hook ended at 31m, as the PostToolUse of the capped call does
    private static readonly JobFact Ended =
        new("20261119-120000-aaaa", "tm", "impl", "sess-impl", "test", "ah test run --eta medium -- dotnet test", 4321, "medium", Ms(0), null, Ms(32), true, false, [], EndedAtMs: Ms(31),
            Slot: new JobSlot("running", Ms(2), 120000, "test", string.Empty));

    private static string Mark(double minutes) => $"{{\"kind\": \"cap-watch\", \"at\": {Sec(minutes)}}}";

    private static string Line(string slot, string killed, double minutes = 30.5) =>
        $"{{\"kind\": \"capped\", \"at\": {Sec(minutes)}, \"pool\": \"test\", \"slot\": \"{slot}\", \"holder\": \"with-slot dotnet\", \"eta\": 600, \"cap\": 1800, \"killed\": [{killed}]}}";

    private static readonly string[] Journal = [Mark(-1), Line("1795128878734745-9999", "9998"), Line("1795128878734745-4321", "4400,4401")];

    private static IReadOnlyList<JobCap> Caps(IReadOnlyList<string> lines) => WatchModule.CapKills(lines, wholeFile: true, out _);

    private static JobOutcome Run(JobFact job, ISet<string> told, out IReadOnlyDictionary<WatchEvent, string> keys, IReadOnlyList<string>? journal = null) =>
        JobEvents.Evaluate("s1", Ms(32), 30000, ["tm"], _ => WatchModule.JobsCapped([job], () => Caps(journal ?? Journal)), static _ => 600, told, out keys);

    [Fact]
    public void ACapKillRaisesOneCappedEventToTheOrchestratorAndTheOwnerCountingFromTheSlot()
    {
        var first = Run(Ended, new HashSet<string>(StringComparer.Ordinal), out _);

        var ev = Assert.Single(first.Events);
        Assert.Equal("job-overdue", ev.Name);
        Assert.Equal("capped", ev.Data.Str("step"));
        Assert.Contains("job 20261119-120000-aaaa was killed at its hard cap after running 28m (cap 30m); killed pids 4400 4401", ev.Text, StringComparison.Ordinal);
        var owner = Assert.Single(first.OwnerMails);
        Assert.Equal("impl", owner.Owner);
        Assert.True(owner.Data.Get("owner_copy")!.Value.AsBool());
        Assert.Null(ev.Data.Get("owner_copy"));
    }

    [Fact]
    public void NegativeARunThatEndedByItselfRaisesNothingThoughTheJournalHoldsAnotherRunsCap()
    {
        var outcome = Run(Ended with { Pid = 5555 }, new HashSet<string>(StringComparer.Ordinal), out _);

        Assert.Empty(outcome.Events);
        Assert.Empty(outcome.OwnerMails);
    }

    [Fact]
    public void AToldCapIsNeverRetoldAfterAFailedReadOrARestartAndNegativeAnUntoldOneIs()
    {
        var told = new HashSet<string>(StringComparer.Ordinal);
        var first = Run(Ended, told, out var keys);
        _ = told.Add(keys[Assert.Single(first.Events)]);

        Assert.Empty(Run(Ended, told, out _, journal: []).Events);
        Assert.Empty(Run(Ended, told, out _).Events);
        Assert.Empty(Run(Ended with { CapTold = true }, new HashSet<string>(StringComparer.Ordinal), out _).Events);
        Assert.Single(Run(Ended, new HashSet<string>(StringComparer.Ordinal), out _).Events);
    }

    [Fact]
    public void AKillJournaledBeforeTheCapWatchMarkIsNotToldAndNegativeOneAtTheMarkIs()
    {
        Assert.Empty(Caps([Line("1-4321", "4321", 29), Mark(30)]));
        Assert.Empty(WatchModule.CapKills([Line("1-4321", "4321", 29)], wholeFile: true, out var missing));
        Assert.True(missing);
        Assert.Single(WatchModule.CapKills([Line("1-4321", "4321", 29)], wholeFile: false, out missing));
        Assert.False(missing);
        Assert.Single(Caps([Mark(30), Line("1-4321", "4321", 30)]));
    }
}
