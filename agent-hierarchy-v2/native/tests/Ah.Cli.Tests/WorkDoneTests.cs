using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Cli.Verbs;
using Ah.Commands;
using Ah.Hierarchy;
using Ah.Platform;
using Ah.StateDb;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary><c>ah work done &lt;id&gt;</c> closes an item through the verb its placement has (ah2-322).</summary>
[Collection("RosterServices")]
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class WorkDoneTests : IDisposable
{
    private readonly Sandbox _sandbox = new();
    private readonly ServiceGraph _graph;
    private readonly FakeEnv _env;
    private readonly string _cwd;
    private readonly string _dir;

    public WorkDoneTests()
    {
        _cwd = Path.Combine(_sandbox.Root, "proj");
        _dir = Path.Combine(_cwd, ".claude", "ah2");
        Directory.CreateDirectory(_dir);
        using (var open = StateDbModule.Open(_sandbox.AhHome, mayCreate: true))
        {
            Assert.Equal(StateDbAvailability.Ready, open.Status.Availability);
        }

        var variables = new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = _dir };
        _env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "1.2.3", "1"));
        var io = PlatformModule.CreateIo(null);
        _graph = ServiceGraph.Build(io, _env, new FakeRunner(), new FakeDaemonClient { Reply = _ => new DaemonReply(0, default, "down") });
        var services = new RosterComposition(_graph, io.Clock, _env);
        CommandsModule.ComposeRoster(() => services);
    }

    public void Dispose()
    {
        CommandsModule.ComposeRoster(() => null!);
        _sandbox.Dispose();
    }

    private (int Code, string Out, string Err) Work(params string[] args)
    {
        using var stdout = new StringWriter(CultureInfo.InvariantCulture);
        using var stderr = new StringWriter(CultureInfo.InvariantCulture);
        var context = new CliContext(_env, new RecordingExec(), new FakeRunner(), "ah", _sandbox.Executable("install/ah"), _sandbox.Home, 501, null, new Lazy<ServiceGraph>(_graph));
        var code = WorkVerb.Run(context, [.. args, "--cwd", _cwd], stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string State(string id)
    {
        var show = Work("show", id);
        Assert.True(show.Code == 0, show.Err);
        return show.Out.Split('\n')[1];
    }

    [Fact]
    public void ABacklogItemEndsDoneThroughBacklogDone()
    {
        var added = Work("add", "Fix the widget", "--body", "Why");
        Assert.True(added.Code == 0, added.Err);
        var id = added.Out.Trim();

        var done = Work("done", id, "--note", "shipped");

        Assert.True(done.Code == 0, done.Err);
        Assert.Equal($"{id} done (backlog)\n", done.Out);
        Assert.StartsWith("state: done", State(id), StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamItemEndsDoneThroughChecklistDone()
    {
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "tm", "_run", "o", [new PlanInput("real", "add", "Do real")]).Refused);

        var done = Work("done", "tm/_run/real");

        Assert.True(done.Code == 0, done.Err);
        Assert.EndsWith(" done (checklist tm/_run)\n", done.Out, StringComparison.Ordinal);
        Assert.Single(done.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("state: done", State("tm/_run/real"), StringComparison.Ordinal);
    }

    [Fact]
    public void ADefaultTeamStreamItemEndsDoneThroughChecklistDone()
    {
        Assert.Null(_graph.Hierarchy.Progress.RecordChecklist(_dir, "_default", "_run", "o", [new PlanInput("plain", "add", "Do plain")]).Refused);

        var done = Work("done", "_default/_run/plain");

        Assert.True(done.Code == 0, done.Err);
        Assert.EndsWith(" done (checklist _default/_run)\n", done.Out, StringComparison.Ordinal);
        Assert.StartsWith("state: done", State("_default/_run/plain"), StringComparison.Ordinal);
    }

    [Fact]
    public void CommitIsTakenWhenDoneFollowsGlobalFlags()
    {
        var added = Work("add", "Fix the widget", "--body", "Why");
        Assert.True(added.Code == 0, added.Err);
        var id = added.Out.Trim();

        var done = Work("--by", "me", "done", id, "--commit", "abc1234");

        Assert.True(done.Code == 0, done.Err);
        Assert.Equal($"{id} done (backlog)\n", done.Out);
        Assert.StartsWith("state: done", State(id), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownIdExitsOneAndChangesNothing()
    {
        var added = Work("add", "Fix the widget", "--body", "Why");
        Assert.True(added.Code == 0, added.Err);
        var id = added.Out.Trim();
        var before = State(id);

        var done = Work("done", "nope-999");

        Assert.Equal(1, done.Code);
        Assert.Equal("ah work: no work item nope-999; nothing changed\n", done.Err);
        Assert.Empty(done.Out);
        Assert.Equal(before, State(id));
    }

    [Fact]
    public void CommitIsStillAnUnknownFlagOutsideDone()
    {
        var show = Work("show", "x", "--commit", "abc");

        Assert.Equal(2, show.Code);
        Assert.Contains("unrecognized flag --commit", show.Err, StringComparison.Ordinal);
    }
}
