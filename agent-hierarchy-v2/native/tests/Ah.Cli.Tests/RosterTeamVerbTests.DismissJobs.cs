using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Hierarchy.StateCalls;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>dismiss --close stops the dismissed member's own verified background jobs and ends their records; nobody else's.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const int JobPid = 99991;

    private const string JobPidStart = "Fri Oct 30 01:00:00 2026";

    /// <summary>A table where the given pids live with start <see cref="JobPidStart"/> until signalled; no ancestry.</summary>
    private sealed class JobTable(HashSet<int> alive) : Ah.Abstractions.IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => null;

        public bool? HasAncestor(int pid, int candidate) => null;

        public string? StartTime(long pid) => alive.Contains((int)pid) ? JobPidStart : null;
    }

    /// <summary>State calls whose PutJob fails; every other call goes to the real engine.</summary>
    public class FailingPutJob : System.Reflection.DispatchProxy
    {
        internal IHierarchyStateCalls Inner { get; set; } = null!;

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(IHierarchyStateCalls.PutJob))
            {
                throw new IOException("state engine down");
            }

            try
            {
                return targetMethod.Invoke(Inner, args);
            }
            catch (System.Reflection.TargetInvocationException e) when (e.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }
    }

    // per-dir-files-to-db S8: a job is team proj's jobs row, put through jobs.put and read back by id
    private ServiceGraph JobGraph() => OperatingSystem.IsWindows()
        ? throw new PlatformNotSupportedException()
        : ServiceGraph.Build(
            PlatformModule.CreateIo(null),
            new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_TEST"] = "1", ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home, ["WRANGL_HIERARCHY_DIR"] = Hierarchy }, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1")),
            _runner,
            new FakeDaemonClient());

    private string PutJobRow(string id, string owner, bool background, int pid, bool verified = true)
    {
        var start = verified ? $",\"pid_start\":\"{JobPidStart}\"" : string.Empty;
        var record = JsonText.Parse($$"""{"id":"{{id}}","team":"proj","owner":"{{owner}}","session_id":"s-1","kind":"bg","label":"ah roster wake","pid":{{pid}}{{start}},"eta":"small","sent_at":"2026-10-30T01:00:01.000Z","background":{{(background ? "true" : "false")}}}""").AsObject();
        JobGraph().Hierarchy.StateCalls.PutJob(Hierarchy, "proj", id, record);
        return id;
    }

    private JsonValue Row(string id) => JsonValue.FromObject(Ah.Watch.WatchModule.JobRecordFiles(JobGraph().Hierarchy.Status, Path.Combine(Hierarchy, "jobs", "proj")).Single(f => f.Id == id).Record);

    private (int Code, JsonValue Out, string Stderr) DismissA(HashSet<int> alive, List<(int Pid, int Signal)> signals)
    {
        WriteTeam("proj", LiveTeamJson("tmux", string.Join(",", Member("implementor", "proj-a", "%1"), Member("implementor", "proj-b", "%2"))));
        ScriptGit();
        _withPanes = true;
        _callers = new RecordingCallers
        {
            ProcessTable = new JobTable(alive),
            Signal = (pid, signal) =>
            {
                signals.Add((pid, signal));
                _ = alive.Remove(pid);
                return true;
            },
        };
        var token = TokenOf(PlanMany("proj-a").Out);
        var (code, stdout, stderr) = CloseMany(token, "proj-a");
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout) : JsonValue.Null, stderr + stdout);
    }

    [Fact]
    public void DismissCloseStopsTheMembersLiveBackgroundJobAndEndsItsRecordButNotAnothersOrAForegroundOne()
    {
        var alive = new HashSet<int> { JobPid, JobPid + 1, JobPid + 2 };
        var mine = PutJobRow("j-mine", "proj-a", background: true, JobPid);
        var other = PutJobRow("j-other", "proj-b", background: true, JobPid + 1);
        var foreground = PutJobRow("j-fg", "proj-a", background: false, JobPid + 2);
        var signals = new List<(int Pid, int Signal)>();

        var (code, result, stderr) = DismissA(alive, signals);

        Assert.True(code == 0, stderr);
        var stopped = Assert.Single(result.Member("jobs_stopped")!.Value.AsArray());
        Assert.Equal("j-mine", stopped.Member("job_id")!.Value.AsString());
        Assert.Equal("TERM", stopped.Member("signal")!.Value.AsString());
        Assert.True(stopped.Member("row_ended")!.Value.AsBool());
        Assert.Equal([(JobPid, 15)], signals);
        Assert.True(Row(mine).Member("ended_at") is { IsText: true });
        Assert.Equal("dismissed", Row(mine).Member("exit")!.Value.AsString());
        Assert.Null(Row(other).Member("ended_at"));
        Assert.Null(Row(foreground).Member("ended_at"));
        Assert.Null(result.Member("jobs_unverified"));
    }

    [Fact]
    public void DismissCloseListsOnlyTheMembersUnverifiedJobsAndSignalsNone()
    {
        var mine = PutJobRow("j-mine", "proj-a", background: true, JobPid, verified: false);
        _ = PutJobRow("j-other", "proj-b", background: true, JobPid + 1, verified: false);
        var signals = new List<(int Pid, int Signal)>();

        var (code, result, stderr) = DismissA([JobPid, JobPid + 1], signals);

        Assert.True(code == 0, stderr);
        var listed = Assert.Single(result.Member("jobs_unverified")!.Value.AsArray());
        Assert.Equal("j-mine", listed.Member("job_id")!.Value.AsString());
        Assert.Null(result.Member("jobs_stopped"));
        Assert.Empty(signals);
        Assert.Null(Row(mine).Member("ended_at"));
    }

    [Fact]
    public void ARecordThatCannotBeEndedIsAWarningAndTheDismissStillSucceeds()
    {
        var mine = PutJobRow("j-mine", "proj-a", background: true, JobPid);
        _wrapStateCalls = inner =>
        {
            var proxy = System.Reflection.DispatchProxy.Create<IHierarchyStateCalls, FailingPutJob>();
            ((FailingPutJob)(object)proxy).Inner = inner;
            return proxy;
        };
        var signals = new List<(int Pid, int Signal)>();

        var (code, result, stderr) = DismissA([JobPid], signals);

        Assert.True(code == 0, stderr);
        Assert.False(Assert.Single(result.Member("jobs_stopped")!.Value.AsArray()).Member("row_ended")!.Value.AsBool());
        Assert.Contains(result.Member("warnings")!.Value.AsArray(), w => w.AsString().Contains("j-mine", StringComparison.Ordinal));
        Assert.False(_sandbox.TeamExists(TeamPath("proj")) && _sandbox.TeamText(TeamPath("proj")).Contains("\"proj-a\"", StringComparison.Ordinal));
        Assert.Null(Row(mine).Member("ended_at"));
    }

    [Fact]
    public void DismissCloseOfAMemberWithNoJobIsUnchanged()
    {
        var signals = new List<(int Pid, int Signal)>();

        var (code, result, stderr) = DismissA([], signals);

        Assert.True(code == 0, stderr);
        Assert.Null(result.Member("jobs_stopped"));
        Assert.Null(result.Member("jobs_unverified"));
        Assert.Empty(signals);
    }
}
