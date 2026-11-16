using System.Diagnostics;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Commands;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>What the roster verbs are told a registration holds: only addresses the session's registration really names.</summary>
[Collection("RosterServices")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class RosterCallerRegistryTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly List<Process> _children = [];
    private ServiceGraph? _graph;

    private string Project => Path.Combine(_sandbox.Root, "proj");

    private string Hierarchy => Path.Combine(_sandbox.Root, "hier");

    public void Dispose()
    {
        foreach (var child in _children)
        {
            try
            {
                child.Kill();
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            child.Dispose();
        }

        _sandbox.Dispose();
    }

    private int Sleeper()
    {
        var child = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false, RedirectStandardOutput = true })!;
        _children.Add(child);
        return child.Id;
    }

    /// <summary>Records <paramref name="pid"/> (this process when null) with its start as the Orchestrator of team <c>proj</c>.</summary>
    private void OwnedBy(string orchestratorSession, int? pid = null)
    {
        var start = pid is { } p ? PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()).StartTime(p) : FakeRunner.SelfStart;
        _sandbox.PlantTeam(
            Path.Combine(Hierarchy, "teams", "proj.json"),
            "{\"version\":1,\"orchestrator\":{\"session_id\":\"" + orchestratorSession + "\",\"pid\":" + (pid ?? Environment.ProcessId) + ",\"pid_start\":\"" + start + "\"},\"members\":[]}");
    }

    private IRosterCallerRegistry Registry(string orchestratorSession, int? owner = null)
    {
        Directory.CreateDirectory(Project);
        OwnedBy(orchestratorSession, owner);
        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
        };
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var io = PlatformModule.CreateIo(null);
        var runner = FakeRunner.KnowingSelf();
        if (owner is not null)
        {
            runner.PsPassthrough = PlatformModule.CreateProcessRunner();
        }

        var graph = ServiceGraph.Build(io, env, runner, new FakeDaemonClient());
        _graph = graph;
        return new RosterComposition(graph, io.Clock, env).Callers!;
    }

    [Fact]
    public void ARegistrationThatWasWrittenIsReported()
    {
        var registry = Registry("s1");

        var written = registry.Register(Project, "s1", Environment.ProcessId, null, null, false);

        Assert.Equal(["orchestrator@proj"], written);
    }

    [Fact]
    public void AClaimWithNoSessionIdReportsNothing()
    {
        var registry = Registry("s1");

        Assert.Empty(registry.Register(Project, null, Environment.ProcessId, null, null, false));
        Assert.Empty(registry.Register(Project, string.Empty, Environment.ProcessId, null, null, false));
    }

    [Fact]
    public void AClaimTheMailboxRefusedBecauseAnotherLiveProcessHoldsTheSessionReportsNothing()
    {
        var first = Sleeper();
        var registry = Registry("s1", first);
        Assert.Equal(["orchestrator@proj"], registry.Register(Project, "s1", first, null, null, false));
        var other = Sleeper();
        OwnedBy("s1", other);

        var second = registry.Register(Project, "s1", other, null, null, false);

        Assert.Empty(second);
    }

    [Fact]
    public void AnUnboundRegistrationWithAMatchingPidIsNotProcessTied()
    {
        var other = Sleeper();
        var registry = Registry("s1", other);
        Assert.Equal(["orchestrator@proj"], registry.Register(Project, "s1", other, null, null, false));

        Assert.Empty(registry.BoundSessions(other));
    }

    /// <summary>A bound registration of <paramref name="session"/> for <c>orchestrator@proj</c>, through the real <c>mailbox register</c> op of the graph <see cref="Registry"/> built.</summary>
    private void WriteBoundRegistration(string session, int pid, string? pidStart)
    {
        var args = new Ah.Core.Json.JsonObject
        {
            ["session_id"] = session,
            ["boxes"] = new Ah.Core.Json.JsonArray { new Ah.Core.Json.JsonObject { ["address"] = "orchestrator@proj", ["dir"] = _graph!.Mailbox.BoxDirFor(Path.Combine(Hierarchy, "teams", "proj.json"), "orchestrator") } },
            ["harness"] = "claude",
            ["pid"] = pid,
            ["bound"] = true,
        };
        if (pidStart is not null)
        {
            args["pid_start"] = pidStart;
        }

        Assert.True(_graph.Writer.Invoke("mailbox", "register", Ah.Core.Json.JsonText.ToUtf8(args, 0), new RequestContext(Project, Project, string.Empty, string.Empty)).Ok);
    }

    [Fact]
    public void ABoundRegistrationWhoseRecordedStartTimeIsNotThePidsIsNotProcessTied()
    {
        var registry = Registry("s1");
        var pid = Sleeper();
        WriteBoundRegistration("s-reused", pid, "Thu Jan  1 00:00:00 1970");

        Assert.Empty(registry.BoundSessions(pid));
    }

    [Fact]
    public void ABoundRegistrationThatRecordedNoStartTimeStillCounts()
    {
        var registry = Registry("s1");
        var pid = Sleeper();
        WriteBoundRegistration("s-old-format", pid, null);

        Assert.Equal(["s-old-format"], registry.BoundSessions(pid).Select(b => b.SessionId));
    }
}
