using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The daemon sweeps dismissed and dead-team scratch on its own, so the scratch tree cannot grow without bound.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class ScratchSweepJobTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Jobs : IJobRegistry
    {
        public Dictionary<string, TimeSpan> Periods { get; } = new(StringComparer.Ordinal);

        public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler) => Periods[name] = period;
    }

    [Fact]
    public void TheScratchSweepJobRunsEveryHourAndTheActivityPruneJobIsStillRegisteredBesideIt()
    {
        var env = new FakeEnv(
            _sandbox.Home,
            _sandbox.AhHome,
            new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home },
            new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var jobs = new Jobs();

        new DaemonServices(new Lazy<ServiceGraph>(graph), env, tmuxTmpdir: Path.Combine(_sandbox.Home, "tmuxtmp")).RegisterJobs(jobs);

        Assert.Equal(TimeSpan.FromHours(1), jobs.Periods["scratch-sweep"]);
        Assert.Equal(TimeSpan.FromMinutes(10), jobs.Periods["activity-prune"]);
    }

    [Fact]
    public void TwoHierarchyDirsThatResolveToOneTeamHomeAreSweptOnceAndTwoUnrelatedHomesTwice()
    {
        var env = new FakeEnv(
            _sandbox.Home,
            _sandbox.AhHome,
            new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home },
            new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var services = new DaemonServices(new Lazy<ServiceGraph>(graph), env, tmuxTmpdir: Path.Combine(_sandbox.Home, "tmuxtmp"));
        var main = Path.Combine(_sandbox.Root, "main", ".claude", "ah2");
        var other = Path.Combine(_sandbox.Root, "other", ".claude", "ah2");
        foreach (var dir in new[] { main, other })
        {
            Directory.CreateDirectory(dir);
        }

        Assert.Equal(2, services.SweepScratchHomes([main, other]));
        Assert.Equal(1, services.SweepScratchHomes([main, main]));
    }

    [Theory]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    public void ADaemonOnAnotherHomeLeavesScratchInsideTheRealHomeAlone(bool otherAhHome, bool dirInsideHome, bool swept, bool otherCase)
    {
        Assert.SkipWhen(otherCase && !OperatingSystem.IsMacOS(), "only macOS volumes are case-insensitive by default");
        var ahHome = _sandbox.AhHome;
        if (otherAhHome)
        {
            ahHome = Path.Combine(_sandbox.Root, "test-ah-home");
            DatabaseTemplate.CopyTo(ahHome);
        }

        var env = new FakeEnv(
            _sandbox.Home,
            ahHome,
            new Dictionary<string, string?> { ["AH_HOME"] = ahHome, ["HOME"] = _sandbox.Home },
            new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        var log = new List<string>();
        var services = new DaemonServices(new Lazy<ServiceGraph>(graph), env, log.Add, tmuxTmpdir: Path.Combine(_sandbox.Home, "tmuxtmp"));
        var dir = Path.Combine(dirInsideHome ? _sandbox.Home : _sandbox.Root, "repo", ".claude", "ah2");
        var member = Path.Combine(dir, "tmp", "goneteam", "ghost");
        var file = Path.Combine(member, "scratch.bin");
        Directory.CreateDirectory(member);
        File.WriteAllText(file, "x");
        var old = DateTime.UtcNow.AddDays(-10);
        File.SetLastWriteTimeUtc(file, old);
        Directory.SetLastWriteTimeUtc(member, old);

        Assert.Equal(swept ? 1 : 0, services.SweepScratchHomes([otherCase ? dir.ToUpperInvariant() : dir]));
        Assert.Equal(!swept, Directory.Exists(member));
        Assert.Equal(!swept, log.Exists(l => l.Contains("skipped", StringComparison.Ordinal)));
    }
}
