using Ah.Cli.Composition;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The daemon's ownership check logs why the watch tick skips, once per change of reason, and when it owns again (ah2-620).</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class StillOwnerLogTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly List<string> _log = [];

    public void Dispose() => _sandbox.Dispose();

    private string Info => Path.Combine(_sandbox.AhHome, "run", "ahd.json");

    private DaemonServices Services()
    {
        Directory.CreateDirectory(Path.Combine(_sandbox.AhHome, "run"));
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home });
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), env, new FakeRunner(), new FakeDaemonClient());
        return new DaemonServices(new Lazy<ServiceGraph>(graph), env, _log.Add);
    }

    [Fact]
    public void AnOwnerLogsNothing()
    {
        var services = Services();
        File.WriteAllText(Info, $"{{\"pid\":{Environment.ProcessId}}}\n");

        Assert.True(services.StillOwner());
        Assert.True(services.StillOwner());
        Assert.Empty(_log); // negative: the daemon ahd.json names logs no skip
    }

    [Fact]
    public void EachChangeOfReasonLogsOneLineAndRepeatsLogNone()
    {
        var services = Services();

        Assert.False(services.StillOwner()); // no ahd.json
        Assert.False(services.StillOwner());
        File.WriteAllText(Info, "{\"pid\":1}\n");
        Assert.False(services.StillOwner());
        Assert.False(services.StillOwner());
        File.WriteAllText(Info, "{\"v\":1}\n");
        Assert.False(services.StillOwner());
        File.WriteAllText(Info, $"{{\"pid\":{Environment.ProcessId}}}\n");
        Assert.True(services.StillOwner());
        Assert.True(services.StillOwner());

        Assert.Equal(4, _log.Count);
        Assert.StartsWith("not the owner: run/ahd.json is unreadable (", _log[0], StringComparison.Ordinal);
        Assert.Equal($"not the owner: run/ahd.json names pid 1, not pid {Environment.ProcessId}; the watch tick and stall watch skip", _log[1]);
        Assert.Equal($"not the owner: run/ahd.json names no pid, not pid {Environment.ProcessId}; the watch tick and stall watch skip", _log[2]);
        Assert.Equal("owner again: run/ahd.json names this daemon; the watch tick and stall watch run", _log[3]);
    }
}
