using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Watch.Tests;

/// <summary>The machine-load watch: sampled once per bare-watch sweep, one alarm per change to live teams' Orchestrators, spawn pacing under one lock.</summary>
public sealed class LoadWatchTests
{
    private static readonly StallTeam Live = new("alpha", "/p/.claude/ah2", "/p", "alpha-orchestrator@alpha", "orch-a");

    private static readonly StallTeam Held = new("beta", "/p/.claude/ah2", "/p", "beta-orchestrator@beta", "orch-b");

    private static readonly StallTeam NoOrchestrator = new("gamma", "/p/.claude/ah2", "/p", null, string.Empty);

    private sealed class Source : ILoadSource
    {
        public int Cpus => 4;

        public double Load1Value { get; set; } = 0.5;

        public bool CpuReadable { get; set; } = true;

        public double? CpuShare { get; set; } = 0.2;

        public bool Fails { get; set; }

        public string? Memory { get; set; } = "normal";

        public string? Config { get; set; }

        public string? State { get; set; }

        public int Writes { get; private set; }

        public double? Load1() => Fails ? throw new InvalidOperationException("no load average") : Load1Value;

        public (bool Readable, double? Share) CpuBusy() => CpuReadable ? (true, CpuShare) : (false, null);

        public string? MemoryPressure() => Memory;

        public int Harnesses() => 7;

        public string? ConfigText() => Config;

        public string? ReadState() => State;

        public void WriteState(string text)
        {
            State = text;
            Writes++;
        }
    }

    private sealed class Host : IBareHost
    {
        public IReadOnlyList<StallTeam> TeamList { get; set; } = [Live, Held, NoOrchestrator];

        public List<WatchMail> Mails { get; } = [];

        public List<string> JournalKinds { get; } = [];

        public List<ApiErrorSighting> Pending { get; } = [];

        public bool Owner() => true;

        public IReadOnlyList<StallTeam> Teams() => TeamList;

        public bool TeamHeld(StallTeam team) => team.Name == Held.Name;

        public IReadOnlyList<ApiErrorSighting> ApiErrors(IReadOnlyList<StallTeam> teams, DateTimeOffset notBefore)
        {
            var found = Pending.Where(e => e.At >= notBefore).ToList();
            Pending.Clear();
            return found;
        }

        public IReadOnlyList<BareRow> Rows(StallTeam team) => [];

        public BareVerdict Check(StallTeam team, BareRow row, BareSighting? earlier) => throw new InvalidOperationException("no member is judged");

        public void SetMark(StallTeam team, string member, BareMark? mark)
        {
        }

        public void Mail(WatchMail mail) => Mails.Add(mail);

        public List<JsonObject> JournalRows { get; } = [];

        public void Journal(JsonObject row)
        {
            JournalKinds.Add(row["kind"].AsString());
            JournalRows.Add(row);
        }

        public JsonValue? ReadRecord(StallTeam team, string member) => null;

        public void WriteRecord(StallTeam team, string member, JsonObject record)
        {
        }

        public bool RecoveryEnabled => false;

        public void DisableRecovery(StallTeam team, string member, string reason)
        {
        }

        public JsonValue? ReadControl(StallTeam team, string member) => null;

        public string? AgentStatus(StallTeam team, BareRow row) => null;

        public IReadOnlyList<string> OpenWork(StallTeam team, BareRow row) => [];

        public int BackgroundOpen(StallTeam team, string? sessionId) => 0;

        public string? ReBrief(StallTeam team, BareRow row) => null;

        public bool Terminate(int pid, string? pidStart) => throw new InvalidOperationException("load never signals a process");

        public bool WaitExit(int pid, string? pidStart, TimeSpan wait) => true;

        public string? Relaunch(StallTeam team, BareRow row, string sessionId) => throw new InvalidOperationException("load never relaunches");

        public bool Verified(StallTeam team, BareRow row, int oldPid, string sessionId) => false;
    }

    private sealed class Rig
    {
        public Source Source { get; } = new();

        public Host Host { get; } = new();

        public DateTimeOffset Clock { get; set; } = DateTimeOffset.Parse("2026-10-25T01:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

        public LoadWatch Load { get; private set; } = null!;

        public BareWatch Watch { get; private set; } = null!;

        /// <param name="sustained">False: <c>cpu_busy_seconds</c> is 0, so one busy sample is a trigger and the older tests keep their tick counts.</param>
        public Rig(bool sustained = false)
        {
            Source.Config = sustained ? null : "{\"cpu_busy_seconds\":0}";
            Restart();
        }

        public void Restart()
        {
            Load = new LoadWatch(Source, () => Clock);
            Watch = new BareWatch(Host, TimeSpan.FromSeconds(30), () => Clock, load: Load);
        }

        public void Sweeps(int count, double? cpuBusy, double perCpu = 0.5)
        {
            Source.CpuShare = cpuBusy;
            Source.Load1Value = perCpu * Source.Cpus;
            for (var i = 0; i < count; i++)
            {
                Clock += TimeSpan.FromSeconds(30);
                Watch.Sweep();
            }
        }
    }

    private static string[] Transitions(Rig rig) =>
        rig.Host.JournalRows.Where(static r => r["kind"].AsString() == "load-transition").Select(static r => r["state"].AsString()).ToArray();

    [Fact]
    public void ThreeLoadedSweepsJournalOneTransitionWithItsTriggersAndMailNobody()
    {
        var rig = new Rig();

        rig.Sweeps(2, 0.95);
        Assert.Empty(Transitions(rig));
        rig.Sweeps(1, 0.95);

        Assert.Equal(["overloaded"], Transitions(rig));
        var row = rig.Host.JournalRows.Single(static r => r["kind"].AsString() == "load-transition");
        Assert.Equal(["at", "kind", "state", "triggers"], row.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("cpu busy 0.95", Assert.Single(row["triggers"].AsArray()).AsString(), StringComparison.Ordinal);
        Assert.Empty(rig.Host.Mails);
    }

    [Fact]
    public void NoTeamLiveHeldOrWithoutAnOrchestratorIsMailedOnATransition()
    {
        var rig = new Rig();
        rig.Sweeps(3, 0.95);

        Assert.Single(Transitions(rig));
        Assert.Empty(rig.Host.Mails);
    }

    [Fact]
    public void AFlappingMachineJournalsEachTransitionAndMailsNobody()
    {
        var rig = new Rig();
        rig.Sweeps(3, 0.95);
        rig.Sweeps(50, 0.95);
        Assert.Single(Transitions(rig));

        rig.Sweeps(2, 0.5);
        Assert.Single(Transitions(rig));
        rig.Sweeps(1, 0.5);
        rig.Sweeps(10, 0.5);
        rig.Sweeps(3, 0.95);
        rig.Sweeps(3, 0.5);

        Assert.Equal(["overloaded", "ok", "overloaded", "ok"], Transitions(rig));
        Assert.Empty(rig.Host.Mails);
    }

    [Fact]
    public void CriticalMemoryPressureJournalsATransitionAndMailsNobody()
    {
        var rig = new Rig();
        rig.Source.Memory = "critical";
        rig.Sweeps(3, 0.2);

        Assert.Equal(["overloaded"], Transitions(rig));
        Assert.Empty(rig.Host.Mails);
    }

    [Fact]
    public void LoadBetweenEightyPercentAndTheThresholdKeepsTheStateAndJournalsNothingMore()
    {
        var rig = new Rig();
        rig.Sweeps(3, 0.95);

        rig.Sweeps(10, 0.8);

        Assert.Single(Transitions(rig));
        Assert.Equal("overloaded", rig.Load.Status().Get("state")!.Value.AsString());
    }

    [Fact]
    public void ARestartedDaemonThatAlreadyReportedOverloadedJournalsNothingMore()
    {
        var rig = new Rig();
        rig.Sweeps(3, 0.95);
        Assert.Single(Transitions(rig));

        rig.Restart();
        rig.Sweeps(10, 0.95);

        Assert.Single(Transitions(rig));
        Assert.Equal("overloaded", rig.Load.Status().Get("state")!.Value.AsString());
    }

    [Fact]
    public void AnInvalidThresholdsFileGivesTheDefaultsAndSaysSoInTheStatus()
    {
        var rig = new Rig();
        rig.Source.Config = "{\"ticks\":0}";

        // the defaults need 180 s of busy samples (7 sweeps 30 s apart) and then 3 ticks
        rig.Sweeps(9, 0.95);

        var status = rig.Load.Status();
        Assert.Contains("ahd-load.json", status.Get("problem")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(0.9, status.Get("thresholds")!.Value.Member("cpu_busy")!.Value.AsNumber());
        Assert.Single(Transitions(rig));
    }

    [Fact]
    public void TheStatusCarriesEveryKeyBeforeAndAfterTheFirstSweep()
    {
        var rig = new Rig();
        rig.Source.Memory = "warn";

        foreach (var status in new[] { rig.Load.Status(), Sweep(rig) })
        {
            foreach (var key in new[] { "state", "since", "triggers", "cpu_busy", "load1_per_cpu", "cpus", "mem_pressure", "harnesses", "api_errors_10m", "thresholds" })
            {
                Assert.True(status.Get(key) is not null, key);
            }
        }

        Assert.Equal(7, Sweep(rig).Get("harnesses")!.Value.AsNumber());
        Assert.Equal("warn", Sweep(rig).Get("mem_pressure")!.Value.AsString());
    }

    private static JsonObject Sweep(Rig rig)
    {
        rig.Sweeps(1, 0.2);
        return rig.Load.Status();
    }

    [Fact]
    public void TenTransportErrorsInThreeSessionsOverloadAndInTwoSessionsDoNot()
    {
        var three = new Rig();
        three.Host.Pending.AddRange(Errors(three.Clock + TimeSpan.FromSeconds(10), 10, "s1", "s2", "s3"));
        three.Sweeps(3, 0.2);
        Assert.Single(Transitions(three));

        var two = new Rig();
        two.Host.Pending.AddRange(Errors(two.Clock + TimeSpan.FromSeconds(10), 10, "s1", "s2"));
        two.Sweeps(10, 0.2);
        Assert.Empty(Transitions(two));
    }

    [Fact]
    public void RateLimitErrorsInManySessionsAreNeverATrigger()
    {
        var rig = new Rig();
        rig.Host.Pending.AddRange(Errors(rig.Clock + TimeSpan.FromSeconds(10), 30, "s1", "s2", "s3", "s4").Select(e => e with { Kind = "rate_limit" }));

        rig.Sweeps(10, 0.2);

        Assert.Empty(Transitions(rig));
        Assert.Equal(30, rig.Load.Status().Get("api_errors_10m")!.Value.Member("rate_limit")!.Value.AsNumber());
    }

    [Fact]
    public void ErrorsOlderThanTenMinutesFallOutAndTheMachineRecovers()
    {
        var rig = new Rig();
        rig.Host.Pending.AddRange(Errors(rig.Clock + TimeSpan.FromSeconds(10), 10, "s1", "s2", "s3"));
        rig.Sweeps(3, 0.2);
        Assert.Single(Transitions(rig));

        rig.Clock += TimeSpan.FromMinutes(11);
        rig.Sweeps(3, 0.2);

        Assert.Equal("ok", Transitions(rig)[^1]);
        Assert.Equal(0, rig.Load.Status().Get("api_errors_10m")!.Value.Member("total")!.Value.AsNumber());
    }

    private static IEnumerable<ApiErrorSighting> Errors(DateTimeOffset at, int count, params string[] sessions) =>
        Enumerable.Range(0, count).Select(i => new ApiErrorSighting(sessions[i % sessions.Length], "transport", at));

    [Fact]
    public void AFailedSampleNeverStopsTheSweepAndIsJournaled()
    {
        var rig = new Rig();
        rig.Source.Fails = true;

        rig.Watch.Sweep();

        Assert.Contains("load-error", rig.Host.JournalKinds);
        Assert.Empty(rig.Host.Mails);
    }

    [Fact]
    public void OnlyCpuUseSustainedForItsSecondsOverloadsAndNeitherTheLoadAverageNorAnUnreadablePlatformDoes()
    {
        var loadAverage = new Rig(sustained: true);
        loadAverage.Sweeps(20, 0.2, perCpu: 3.0);
        Assert.Empty(Transitions(loadAverage));
        Assert.Empty(loadAverage.Load.Status().Get("triggers")!.Value.AsArray());

        var brief = new Rig(sustained: true);
        brief.Sweeps(3, 0.95);
        brief.Sweeps(20, 0.3);
        Assert.Empty(Transitions(brief));

        var broken = new Rig(sustained: true);
        broken.Sweeps(6, 0.95);
        broken.Sweeps(1, null);
        broken.Sweeps(6, 0.95);
        broken.Sweeps(1, null);
        broken.Sweeps(6, 0.95);
        Assert.Empty(Transitions(broken));

        var unreadable = new Rig(sustained: true);
        unreadable.Source.CpuReadable = false;
        unreadable.Sweeps(20, 0.95, perCpu: 3.0);
        Assert.Empty(Transitions(unreadable));
        Assert.Equal("cpu use not read on this platform", unreadable.Load.Status().Get("cpu_note")!.Value.AsString());
        Assert.Null(new Rig(sustained: true).Load.Status().Get("cpu_note"));

        var sustained = new Rig(sustained: true);
        sustained.Sweeps(6, 0.95);
        Assert.Empty(sustained.Load.Status().Get("triggers")!.Value.AsArray());
        sustained.Sweeps(1, 0.95);
        Assert.Equal("cpu busy 0.95 for 180 s (threshold 0.90)", Assert.Single(sustained.Load.Status().Get("triggers")!.Value.AsArray()).AsString());
        sustained.Sweeps(2, 0.95);
        Assert.Equal(["overloaded"], Transitions(sustained));
    }

    [Fact]
    public void TheOldLoadKeyIsAcceptedAndIgnoredAndACpuBusyOutOfRangeGivesTheDefaults()
    {
        var (old, oldProblem) = LoadConfig.Parse("{\"load1_per_cpu\": 100, \"api_errors\": 100000}");
        Assert.Null(oldProblem);
        Assert.Equal(new LoadThresholds() with { ApiErrors = 100000 }, old);

        Assert.Equal((new LoadThresholds() with { CpuBusy = 1, CpuBusySeconds = 0 }, (string?)null), LoadConfig.Parse("{\"cpu_busy\": 1, \"cpu_busy_seconds\": 0}"));

        foreach (var text in new[] { "{\"cpu_busy\": 0, \"ticks\": 5}", "{\"cpu_busy\": 1.01, \"ticks\": 5}", "{\"cpu_busy\": \"0.9\", \"ticks\": 5}", "{\"cpu_busy_seconds\": -1, \"ticks\": 5}", "{\"cpu_busy_seconds\": 1.5, \"ticks\": 5}" })
        {
            var (thresholds, problem) = LoadConfig.Parse(text);
            Assert.Equal(new LoadThresholds(), thresholds);
            Assert.Contains("cpu_busy", problem, StringComparison.Ordinal);
        }
    }

    private static void Overload(Rig rig) => rig.Sweeps(3, 0.95);

    [Fact]
    public void AMachineThatIsNotOverloadedAdmitsEverySpawn()
    {
        var rig = new Rig();

        Assert.All(Enumerable.Range(0, 5), _ => Assert.True(rig.Load.AdmitSpawn(dryRun: false).Admitted));
    }

    [Fact]
    public void ADryRunIsAnsweredAndNeverRecorded()
    {
        var rig = new Rig();
        Overload(rig);

        Assert.True(rig.Load.AdmitSpawn(dryRun: true).Admitted);
        Assert.True(rig.Load.AdmitSpawn(dryRun: true).Admitted);
        Assert.True(rig.Load.AdmitSpawn(dryRun: false).Admitted);
        Assert.False(rig.Load.AdmitSpawn(dryRun: true).Admitted);
    }

    [Fact]
    public void ConcurrentRequestsInOneGapAdmitExactlyOne()
    {
        var rig = new Rig();
        Overload(rig);

        var admitted = Enumerable.Range(0, 32).AsParallel().WithDegreeOfParallelism(16).Select(_ => rig.Load.AdmitSpawn(dryRun: false).Admitted).ToList();

        Assert.Single(admitted, a => a);
    }

    [Fact]
    public void TheLastAdmittedTimeSurvivesARestart()
    {
        var rig = new Rig();
        Overload(rig);
        Assert.True(rig.Load.AdmitSpawn(dryRun: false).Admitted);

        rig.Restart();

        Assert.False(rig.Load.AdmitSpawn(dryRun: false).Admitted);
    }

    [Fact]
    public void ASpawnGapFromTheThresholdsFileIsUsedAndKeepsTheState()
    {
        var rig = new Rig();
        Overload(rig);

        rig.Source.Config = "{\"spawn_gap_s\":10}";
        Assert.True(rig.Load.AdmitSpawn(dryRun: false).Admitted);
        rig.Clock += TimeSpan.FromSeconds(11);

        Assert.True(rig.Load.AdmitSpawn(dryRun: false).Admitted);
        Assert.Equal("overloaded", rig.Load.Status().Get("state")!.Value.AsString());
    }

    [Fact]
    public void AnOverloadedMachineAdmitsOneSpawnPerGap()
    {
        var rig = new Rig();
        Overload(rig);

        var first = rig.Load.AdmitSpawn(dryRun: false);
        rig.Clock += TimeSpan.FromSeconds(100);
        var second = rig.Load.AdmitSpawn(dryRun: false);
        rig.Clock += TimeSpan.FromSeconds(300);
        var third = rig.Load.AdmitSpawn(dryRun: false);

        Assert.True(first.Admitted);
        Assert.False(second.Admitted);
        Assert.Equal(rig.Clock - TimeSpan.FromSeconds(100), second.NextAt);
        Assert.NotEmpty(second.Triggers);
        Assert.True(third.Admitted);
    }
}
