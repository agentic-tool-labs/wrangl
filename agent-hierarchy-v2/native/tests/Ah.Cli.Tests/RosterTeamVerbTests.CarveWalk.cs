using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>
/// The sub-orchestrator carve over the production ancestor walk. The test process's parent plays the sub-orchestrator's Claude
/// process: team files and live registrations in the sandbox name it, and the walk finds it by its real start time.
/// </summary>
public sealed partial class RosterTeamVerbTests
{
    private const string WalkSo = "mo-so-ws1";

    private static string Ps(string field, int pid)
    {
        var start = new ProcessStartInfo("/bin/ps", $"-ww -o {field}= -p {pid.ToString(CultureInfo.InvariantCulture)}")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        start.Environment["LC_ALL"] = "C";
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return text;
    }

    private static int ParentPid() => int.Parse(Ps("ppid", Environment.ProcessId), CultureInfo.InvariantCulture);

    /// <summary>The start time as the process table prints it, which is the form a record holds.</summary>
    private static string StartOfPid(int pid) =>
        OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner()).StartTime(pid)!;

    private void WalkRegister(int pid, string? start, string session, params string[] addresses)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException();
        }

        var variables = new Dictionary<string, string?>
        {
            ["AH_TEST"] = "1",
            ["AH_HOME"] = _sandbox.AhHome,
            ["HOME"] = _sandbox.Home,
            ["WRANGL_HIERARCHY_DIR"] = Hierarchy,
        };
        var fake = new FakeEnv(_sandbox.Home, _sandbox.AhHome, variables, new PluginInfo(_sandbox.PluginRoot, "wrangl", "0.134.10", "1"));
        var graph = ServiceGraph.Build(PlatformModule.CreateIo(null), fake, _runner, new FakeDaemonClient());
        Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite(
            session,
            [.. addresses.Select(static a => new BoxRef(a, "/ah/mailbox/" + a.Replace('@', '~') + "/box"))],
            Pid: pid,
            PidStart: start)));
    }

    private static string OwnerJson(int pid, string? start, string session) =>
        "{\"session_id\":\"" + session + "\",\"pid\":" + pid.ToString(CultureInfo.InvariantCulture) + (start is null ? string.Empty : ",\"pid_start\":\"" + start + "\"") + "}";

    /// <summary>X ("mo", owned by the first argument) with an SO row for ws1 and ws2, and the child of ws1 owned by the second.</summary>
    private void WalkTeams(string moOwner, string childOwner, string soRole = "sub-orchestrator", bool soRow = true, string? childParent = "mo")
    {
        var rows = soRow
            ? "{\"name\":\"" + WalkSo + "\",\"role\":\"" + soRole + "\",\"route\":\"peer\"},{\"name\":\"mo-so-ws2\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}"
            : "{\"name\":\"mo-so-ws2\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}";
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":" + moOwner + ",\"members\":[" + rows + "],"
            + "\"streams\":{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"" + WalkSo + "\"},\"ws2\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws2\"}}}");
        var parent = childParent is null ? string.Empty : ",\"parent\":{\"team\":\"" + childParent + "\",\"stream\":\"ws1\"}";
        WriteTeam(ChildName, "{\"version\":1,\"team_id\":\"t-c\",\"transport\":\"terminal\",\"orchestrator\":" + childOwner + ",\"members\":[]" + parent + "}");
    }

    private void WalkAs(string marker = WalkSo)
    {
        _realWalk = true;
        _callers ??= new RecordingCallers();
        _runner.PsPassthrough = OperatingSystem.IsWindows() ? null : PlatformModule.CreateProcessRunner();
        _extraEnv[MeshModule.MemberMarkerEnv] = marker;
        _extraEnv["WRANGL_TEAM_FILE"] = TeamPath("mo");
    }

    /// <summary>The sub-orchestrator as it is when it owns its child: the walk's nearest process owns C and is registered as the row of X.</summary>
    private int ArrangeOwningSo(string? childStart = "", string? registeredStart = "")
    {
        var p = ParentPid();
        var start = StartOfPid(p);
        var moPid = LivePidOfAnotherProcess();
        WalkTeams(OwnerJson(moPid, StartOfPid(moPid), "sess-mo"), OwnerJson(p, childStart == string.Empty ? start : childStart, "sess-so"));
        WalkRegister(p, registeredStart == string.Empty ? start : registeredStart, "sess-so", "orchestrator@mo-ws1", WalkSo + "@mo");
        WalkAs();
        return p;
    }

    private bool RefusedByGuard(params string[] args) => RefusalCode(args) != "none";

    private string RefusalCode(params string[] args)
    {
        var stdout = Roster(args).Stdout;
        return stdout.StartsWith('{') && JsonText.Parse(stdout) is { } o && o.Member("status") is { IsText: true } st && st.AsString() == "refused"
            && o.Member("refused") is { IsText: true } code
            ? code.AsString()
            : "none";
    }

    [Theory]
    [InlineData("disband", null)]
    [InlineData("dismiss", "nobody")]
    [InlineData("untrack", "nobody")]
    [InlineData("spawn-one", "reviewer")]
    [InlineData("spawn-ad-hoc", "reviewer")]
    public void W1ASubOrchestratorThatOwnsItsChildIsCarvedByTheRealWalk(string verb, string? positional)
    {
        ArrangeOwningSo();
        string[] args = positional is null ? [verb, "--team", ChildName] : [verb, positional, "--team", ChildName];

        Assert.False(RefusedByGuard(args), RefusalCode(args));
    }

    [Fact]
    public void W2AMarkedShellWithNoRegisteredAncestorIsRefused()
    {
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(LivePidOfAnotherProcess(), null, "sess-x"));
        WalkAs();

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W3TheManagingOrchestratorWithTheSubOrchestratorsMarkerIsRefused()
    {
        var p = ParentPid();
        var start = StartOfPid(p);
        WalkTeams(OwnerJson(p, start, "sess-mo"), OwnerJson(LivePidOfAnotherProcess(), null, "sess-x"));
        WalkAs();

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W4TheSubOrchestratorOfOneStreamIsRefusedOnASiblingsChild()
    {
        ArrangeOwningSo();
        WriteTeam("mo-ws2", "{\"version\":1,\"team_id\":\"t-2\",\"transport\":\"terminal\",\"orchestrator\":" + OwnerJson(LivePidOfAnotherProcess(), null, "sess-y") + ",\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ws2\"}}");

        Assert.Equal("not-child-team", RefusalCode("disband", "--team", "mo-ws2"));
    }

    [Fact]
    public void W5AnOwnerOfATopLevelTeamWithTheMarkerIsRefused()
    {
        ArrangeOwningSo();
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(ParentPid(), StartOfPid(ParentPid()), "sess-so"), childParent: null);

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W5bAChildTheLookupDoesNotReturnForTheStreamIsRefused()
    {
        var p = ArrangeOwningSo();
        var other = LivePidOfAnotherProcess();
        WriteTeam("mo-ws1", "{\"version\":1,\"team_id\":\"t-a\",\"created\":\"2030-01-01T00:00:00.000Z\",\"transport\":\"terminal\",\"orchestrator\":" + OwnerJson(other, StartOfPid(other), "sess-a") + ",\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"}}");
        WriteTeam("mo-ws1-b", "{\"version\":1,\"team_id\":\"t-b\",\"created\":\"2020-01-01T00:00:00.000Z\",\"transport\":\"terminal\",\"orchestrator\":" + OwnerJson(p, StartOfPid(p), "sess-so") + ",\"members\":[],\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"}}");

        Assert.True(RefusedByGuard("disband", "--team", "mo-ws1-b"));
    }

    [Fact]
    public void W6APidReusedWithAnotherStartIsRefused()
    {
        ArrangeOwningSo(childStart: "Mon Jan  1 00:00:00 2001");

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W6bARegistrationWithAnotherStartIsRefused()
    {
        ArrangeOwningSo(registeredStart: "Mon Jan  1 00:00:00 2001");

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W7AChildCreatedByAnotherProcessIsRefused()
    {
        var p = ArrangeOwningSo();
        var other = LivePidOfAnotherProcess();
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(other, StartOfPid(other), "sess-x"));
        WalkRegister(p, StartOfPid(p), "sess-so", WalkSo + "@mo");

        Assert.Equal("not-child-team", RefusalCode("disband", "--team", ChildName));
    }

    [Fact]
    public void W8AnUntrackedSubOrchestratorRowIsRefused()
    {
        var p = ArrangeOwningSo();
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(p, StartOfPid(p), "sess-so"), soRow: false);

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W9ARowOfAnotherClassIsRefused()
    {
        var p = ArrangeOwningSo();
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(p, StartOfPid(p), "sess-so"), soRole: "implementor");

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W10AMarkerNamingAnotherRowIsRefused()
    {
        ArrangeOwningSo();
        WalkAs("mo-so-ws2");

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W11AnOwnerVerbOnTheManagersTeamOutsideTheCarveIsRefused()
    {
        ArrangeOwningSo();

        Assert.Equal("not-child-team", RefusalCode("spawn-one", "reviewer", "--team", "mo"));
        Assert.Equal("not-child-team", RefusalCode("dismiss", WalkSo, "--team", "mo"));
    }

    [Fact]
    public void W12ASubOrchestratorBeforeItOwnsAChildMayCreateIt()
    {
        var p = ParentPid();
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(LivePidOfAnotherProcess(), null, "sess-x"));
        _sandbox.ClearTeam(TeamPath(ChildName));
        WalkRegister(p, StartOfPid(p), "sess-so", WalkSo + "@mo");
        WalkAs();

        Assert.False(RefusedByGuard("create", "--parent", "mo", "--stream", "ws1"));
    }

    [Fact]
    public void W13ALegacyStartIsRefused()
    {
        var p = ParentPid();
        var legacy = Ps("lstart", p);
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(p, legacy, "sess-so"));
        WalkRegister(p, legacy, "sess-so", "orchestrator@mo-ws1", WalkSo + "@mo");
        WalkAs();

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W7bARegistrationAsTheChildsOrchestratorDoesNotMakeItsOwnerWhenTheRecordNamesAnotherProcess()
    {
        var p = ParentPid();
        var other = LivePidOfAnotherProcess();
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(other, StartOfPid(p), "sess-x"));
        WalkRegister(p, StartOfPid(p), "sess-so", "orchestrator@mo-ws1", WalkSo + "@mo");
        WalkAs();

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void W14TheSecondBoxOfTwoIsStillTheSubOrchestratorsRegistration()
    {
        var p = ParentPid();
        WalkTeams(OwnerJson(LivePidOfAnotherProcess(), null, "sess-mo"), OwnerJson(p, StartOfPid(p), "sess-so"));
        WalkRegister(p, StartOfPid(p), "sess-so", "orchestrator@mo-ws1", "extra@mo-ws1", WalkSo + "@mo");
        WalkAs();

        Assert.False(RefusedByGuard("disband", "--team", ChildName));
    }

    [Fact]
    public void AnOwnerThatIsGoneDoesNotCarve()
    {
        ArrangeOwningSo();
        _callers = new RecordingCallers { OwnerGone = true };

        Assert.True(RefusedByGuard("disband", "--team", ChildName));
    }
}
