using System.Runtime.Versioning;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The daemon's team-retire job (ah-ui-focus §6): a failure logs once per team and error text, and the next pass tries again.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class TeamRetireJobTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private sealed class Jobs : IJobRegistry
    {
        public Dictionary<string, TimeSpan> Periods { get; } = new(StringComparer.Ordinal);

        public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler) => Periods[name] = period;
    }

    private DaemonServices Services(List<string> log)
    {
        var env = new FakeEnv(
            _sandbox.Home,
            _sandbox.AhHome,
            new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home },
            new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.0.0", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        return new DaemonServices(new Lazy<ServiceGraph>(graph), env, tmuxTmpdir: Path.Combine(_sandbox.Home, "tmuxtmp"), log: log.Add);
    }

    private static (IReadOnlyList<(string Dir, string Team, long LastWorkMs)>, IReadOnlyList<string>) Pass(params string[] teams) =>
        ([.. teams.Select(static t => ("/d", t, 1L))], []);

    [Fact]
    public void TheJobRunsEveryMinute()
    {
        var jobs = new Jobs();

        Services([]).RegisterJobs(jobs);

        Assert.Equal(TimeSpan.FromMinutes(1), jobs.Periods["team-retire"]);
    }

    [Fact]
    public void AFailureLogsOncePerTeamAndErrorAndEveryPassTriesAgain()
    {
        var log = new List<string>();
        var services = Services(log);
        var tries = 0;
        var error = "refused";

        for (var pass = 0; pass < 3; pass++)
        {
            Assert.Equal(0, services.ApplyRetirePass(Pass("a"), (_, _, _) => { tries++; return error; }, static _ => false));
        }

        Assert.Equal(3, tries);
        Assert.Single(log);

        error = "another reason";
        _ = services.ApplyRetirePass(Pass("a"), (_, _, _) => error, static _ => false);
        _ = services.ApplyRetirePass(Pass("b"), (_, _, _) => error, static _ => false);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void AThrownWriteFailsThatTeamAndNotTheOthers()
    {
        var log = new List<string>();

        var written = Services(log).ApplyRetirePass(Pass("a", "b"), static (_, team, _) => team == "a" ? throw new IOException("disk") : null, static _ => false);

        Assert.Equal(1, written);
        Assert.Equal("team-retire a failed: disk", Assert.Single(log));
    }

    [Fact]
    public void AnUnreadableFileLogsOnceAndOneThatReadsOrIsGoneDoesNot()
    {
        var log = new List<string>();
        var services = Services(log);
        (IReadOnlyList<(string Dir, string Team, long LastWorkMs)>, IReadOnlyList<string>) pass = ([], ["/d/bad.json", "/d/new.json"]);

        _ = services.ApplyRetirePass(pass, static (_, _, _) => null, static p => p == "/d/bad.json");
        _ = services.ApplyRetirePass(pass, static (_, _, _) => null, static p => p == "/d/bad.json");

        Assert.Contains("/d/bad.json", Assert.Single(log), StringComparison.Ordinal);
    }
}
