using System.Diagnostics;
using System.Globalization;
using Ah.Abstractions;
using Ah.Cli.Verbs;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary><c>ah proc</c>: who may stop which process, the pinned TERM-then-KILL stop, and the dry run.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class ProcVerbTests
{
    private const int Session = 100;
    private const int Self = 200;
    private const int Member = 300;
    private const int OtherMember = 500;

    private static readonly DateTimeOffset Epoch = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A process table over a fixed tree: pid → (parent, start offset in seconds).</summary>
    private sealed class Table(Dictionary<int, (int Parent, int Start)> rows) : IProcessTable
    {
        public Dictionary<int, (int Parent, int Start)> Rows { get; } = rows;

        public IReadOnlyList<int>? Ancestors(int pid)
        {
            var chain = new List<int>();
            while (Rows.TryGetValue(pid, out var row) && row.Parent > 0)
            {
                chain.Add(row.Parent);
                pid = row.Parent;
            }

            return chain;
        }

        public bool? HasAncestor(int pid, int candidate) => Ancestors(pid)?.Contains(candidate);

        public string? StartTime(long pid) => Rows.TryGetValue((int)pid, out var row) ? Start(row.Start) : null;

        public IReadOnlyList<ProcessNode>? All() =>
            [.. Rows.Select(r => new ProcessNode(r.Key, Epoch.AddSeconds(r.Value.Start), "cmd " + r.Key, r.Value.Parent))];
    }

    private static string Start(int offset) => Epoch.AddSeconds(offset).ToString("ddd MMM d HH:mm:ss yyyy", CultureInfo.InvariantCulture);

    /// <summary>launchd → orchestrator session 100 → this ah call 200; member 300 with child 301; member 500 with child 501; foreign 400.</summary>
    private static Table Tree() => new(new()
    {
        [1] = (0, 0),
        [Session] = (1, 100),
        [Self] = (Session, 200),
        [150] = (Session, 150),
        [151] = (150, 151),
        [Member] = (1, 300),
        [301] = (Member, 301),
        [OtherMember] = (1, 500),
        [501] = (OtherMember, 501),
        [400] = (1, 400),
    });

    private static JsonValue TeamRecord(int memberStart) => JsonText.Parse(
        $$"""{"orchestrator":{"pid":{{Session}},"pid_start":"{{Start(100)}}"},"members":[{"name":"impl","pid":{{Member}},"pid_start":"{{Start(memberStart)}}"}]}""");

    private const string Trusted = "/home/.claude/ah2";

    private static Func<int, bool> AliveIn(Table table) => pid => table.Rows.ContainsKey(pid);

    private static IReadOnlyList<ProcRoot> OrchestratorRoots(Table table, int memberStart = 300)
    {
        var owned = ProcOwnership.OwnedTeams(
            table,
            null,
            Self,
            [(Trusted, new TeamOrchestrator("t", Session, Start(100)), TeamRecord(memberStart))],
            [Trusted],
            [new RegisteredSession(Session, Start(100), "orchestrator@t")],
            AliveIn(table));
        return ProcOwnership.Roots(table, new NearestSession(NearestKind.Orchestrator, "t", "orchestrator", Session), owned, _ => true);
    }

    private static (ProcRoot? Owner, string? Why) Decide(Table table, IReadOnlyList<ProcRoot> roots, int pid) =>
        ProcOwnership.Decide(table.All()!, roots, Self, pid);

    [Fact]
    public void OwnSessionDescendantIsAllowedDownTheTree()
    {
        var table = Tree();
        Assert.Equal("session", Decide(table, OrchestratorRoots(table), 151).Owner?.Owner);
    }

    [Fact]
    public void LiveMemberDescendantIsAllowedForTheTeamsOrchestrator()
    {
        var table = Tree();
        Assert.Equal("impl@t", Decide(table, OrchestratorRoots(table), 301).Owner?.Owner);
    }

    [Fact]
    public void ForeignProcessIsRefused()
    {
        var table = Tree();
        var (owner, why) = Decide(table, OrchestratorRoots(table), 400);
        Assert.Null(owner);
        Assert.Contains("not a descendant", why, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberWhosePidWasReusedOwnsNothing()
    {
        // the row records start 299, the process at pid 300 started at 300: the pid now belongs to another process
        var table = Tree();
        var roots = OrchestratorRoots(table, memberStart: 299);
        Assert.DoesNotContain(roots, r => r.Pid == Member);
        Assert.Null(Decide(table, roots, 301).Owner);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(Session)]
    [InlineData(Self)]
    [InlineData(Member)]
    public void PidOneTheCallersSessionItsOwnCallAndASessionItselfAreRefused(int pid)
    {
        var table = Tree();
        Assert.Null(Decide(table, OrchestratorRoots(table), pid).Owner);
    }

    [Fact]
    public void MemberCallerOwnsOnlyItsOwnDescendants()
    {
        var table = Tree();
        table.Rows[MemberCall] = (Member, 310);
        var roots = MemberRoots(table, [(Trusted, new TeamOrchestrator("t", Session, Start(100)), TeamRecord(300))], "impl@t");
        Assert.Equal([Member], roots.Select(r => r.Pid));
        Assert.Equal("session", ProcOwnership.Decide(table.All()!, roots, MemberCall, 301).Owner?.Owner);
        Assert.Null(ProcOwnership.Decide(table.All()!, roots, MemberCall, 151).Owner);
        Assert.Null(ProcOwnership.Decide(table.All()!, roots, MemberCall, 501).Owner);
    }

    private const int MemberCall = 310;

    /// <summary>The roots of member pid 300 (calling as 310), registered as <paramref name="address"/> with its team file in <see cref="Trusted"/>.</summary>
    private static IReadOnlyList<ProcRoot> MemberRoots(
        Table table, IReadOnlyList<(string Dir, TeamOrchestrator Orchestrator, JsonValue Record)> teams, string address, IReadOnlyList<string>? trusted = null)
    {
        var nearest = new NearestSession(NearestKind.Member, "t", "impl", Member);
        trusted ??= ProcOwnership.TrustedDirs(table, nearest, [(Member, Start(300), [Trusted + "/teams/t.json"])]);
        var owned = ProcOwnership.OwnedTeams(table, null, MemberCall, teams, trusted, [new RegisteredSession(Member, Start(300), address)], AliveIn(table));
        return ProcOwnership.Roots(table, nearest, owned, _ => true);
    }

    /// <summary>A team file the member forged in a directory it chose, naming itself as Orchestrator and a sibling as member.</summary>
    private static (string Dir, TeamOrchestrator Orchestrator, JsonValue Record) Forged() => (
        "/tmp/x/.claude/ah2",
        new TeamOrchestrator("x", Member, Start(300)),
        JsonText.Parse($$"""{"orchestrator":{"pid":{{Member}},"pid_start":"{{Start(300)}}"},"members":[{"name":"sib","pid":{{OtherMember}},"pid_start":"{{Start(500)}}"}]}"""));

    [Fact(Skip = "pruned 2026-11-14: attacker already runs as the user; not applicable to a local daemon")]
    public void ForgedTeamDirNamingTheCallerAsOrchestratorIsIgnored()
    {
        var table = Tree();
        table.Rows[MemberCall] = (Member, 310);
        var roots = MemberRoots(table, [Forged()], "impl@t");
        Assert.Null(ProcOwnership.Decide(table.All()!, roots, MemberCall, 501).Owner);

        // the same forged file read from a trusted directory would pass the owner proof: the directory is what refuses it
        var control = MemberRoots(table, [Forged()], "impl@t", trusted: [Forged().Dir]);
        Assert.Equal("sib@x", ProcOwnership.Decide(table.All()!, control, MemberCall, 501).Owner?.Owner);
    }

    [Fact]
    public void TrustedDirsComeOnlyFromTheCallersOwnLiveRegistration()
    {
        var table = Tree();
        var nearest = new NearestSession(NearestKind.Member, "t", "impl", Member);
        var dirs = ProcOwnership.TrustedDirs(table, nearest, [
            (Member, Start(300), ["/a/teams/t.json"]),
            (Member, Start(299), ["/reused/teams/t.json"]),
            (OtherMember, Start(500), ["/other/teams/t.json"]),
        ]);
        Assert.Equal(["/a"], dirs);
        Assert.Empty(ProcOwnership.TrustedDirs(table, new NearestSession(NearestKind.None), [(Member, Start(300), ["/a/teams/t.json"])]));
    }

    [Fact]
    public void OrchestratorNamedRegistrationOnAnotherPidGetsNoMemberRoots()
    {
        // pid 300 registered as orchestrator@t, but t's team file records pid 100 as its Orchestrator
        var table = Tree();
        table.Rows[MemberCall] = (Member, 310);
        var record = JsonText.Parse(
            $$"""{"orchestrator":{"pid":{{Session}},"pid_start":"{{Start(100)}}"},"members":[{"name":"sib","pid":{{OtherMember}},"pid_start":"{{Start(500)}}"}]}""");
        var roots = MemberRoots(table, [(Trusted, new TeamOrchestrator("t", Session, Start(100)), record)], "orchestrator@t");
        Assert.Equal([Member], roots.Select(r => r.Pid));
        Assert.Null(ProcOwnership.Decide(table.All()!, roots, MemberCall, 501).Owner);
    }

    [Fact]
    public void MemberThatIsNotAHarnessSessionIsNoRoot()
    {
        var table = Tree();
        var owned = ProcOwnership.OwnedTeams(
            table, null, Self, [(Trusted, new TeamOrchestrator("t", Session, Start(100)), TeamRecord(300))], [Trusted],
            [new RegisteredSession(Session, Start(100), "orchestrator@t")], AliveIn(table));
        Assert.Single(owned);
        var roots = ProcOwnership.Roots(table, new NearestSession(NearestKind.Orchestrator, "t", "orchestrator", Session), owned, _ => false);
        Assert.Null(Decide(table, roots, 301).Owner);
    }

    [Fact]
    public void ALaneLeadReachesItsChildTeamsMembersButNotATopTeams()
    {
        var table = Tree();
        var nearest = new NearestSession(NearestKind.Member, "t", "so-lane", Session);
        var lead = new TeamOrchestrator("t-lane", Session, Start(100));
        var child = JsonText.Parse($$"""{"parent":{"team":"t","stream":"lane"},"members":[{"name":"impl","pid":{{Member}},"pid_start":"{{Start(300)}}"}]}""");
        var roots = ProcOwnership.Roots(table, nearest, ProcOwnership.LedLanes(table, nearest, [(Trusted, lead, child)], [Trusted]), _ => true);
        Assert.Equal("impl@t-lane", Decide(table, roots, 301).Owner?.Owner);

        // the same record with no parent link is a top team, which only the owner proof may grant
        Assert.Empty(ProcOwnership.LedLanes(table, nearest, [(Trusted, new TeamOrchestrator("t", Session, Start(100)), TeamRecord(300))], [Trusted]));
    }

    [Fact]
    public void ALaneLeadReachesAMemberLentToItsLaneButNotOneLentToAnother()
    {
        var table = Tree();
        var nearest = new NearestSession(NearestKind.Member, "t", "so-lane", Session);
        var child = JsonText.Parse("""{"parent":{"team":"t","stream":"lane"},"members":[]}""");
        var home = JsonText.Parse($$$"""
            {"members":[
              {"name":"lent","pid":{{{Member}}},"pid_start":"{{{Start(300)}}}","lead":{"team":"t-lane","name":"so-lane"}},
              {"name":"other","pid":{{{OtherMember}}},"pid_start":"{{{Start(500)}}}","lead":{"team":"t-other","name":"so-other"}}
            ]}
            """);
        (string, TeamOrchestrator, JsonValue)[] teams = [(Trusted, new TeamOrchestrator("t-lane", Session, Start(100)), child), (Trusted, new TeamOrchestrator("t", 999, Start(1)), home)];
        var roots = ProcOwnership.Roots(table, nearest, ProcOwnership.LedLanes(table, nearest, teams, [Trusted]), _ => true);
        Assert.Equal("lent@t-lane", Decide(table, roots, 301).Owner?.Owner);
        Assert.Null(Decide(table, roots, 501).Owner);
    }

    [Theory]
    [InlineData(777, "i1", true)] // the lead's process is gone
    [InlineData(Member, "i1", false)] // the lead is alive: the lane stays with it
    [InlineData(777, "old", false)] // a lane of an earlier incarnation of the team
    public void TheTopReachesALaneOnlyWhenItsLeadIsGone(int leadPid, string teamId, bool reached)
    {
        var table = Tree();
        var top = JsonText.Parse("""{"team_id":"i1","members":[]}""");
        var child = JsonText.Parse($$"""{"parent":{"team":"t","stream":"lane","team_id":"{{teamId}}"},"members":[{"name":"impl","pid":{{OtherMember}},"pid_start":"{{Start(500)}}"}]}""");
        var lanes = ProcOwnership.LeaderlessLanes(
            table, [(new TeamOrchestrator("t", Session, Start(100)), top)], [(Trusted, new TeamOrchestrator("t-lane", leadPid, Start(300)), child)], [Trusted]);
        Assert.Equal(reached, lanes.Count == 1);
    }

    [Fact]
    public void MemberMarkerGetsNoTeamPowers()
    {
        var table = Tree();
        var owned = ProcOwnership.OwnedTeams(
            table, "impl@t", Self, [(Trusted, new TeamOrchestrator("t", Session, Start(100)), TeamRecord(300))], [Trusted],
            [new RegisteredSession(Session, Start(100), "orchestrator@t")], AliveIn(table));
        Assert.Empty(owned);
    }

    [Fact]
    public void ChildForkedAfterTheSnapshotIsKilledToo()
    {
        // 150 ignores TERM and forks 152 after the decision snapshot; KILL removes a process
        var table = Tree();
        var tree = ProcOwnership.Tree(table.All()!, table.All()!.Single(n => n.Pid == 150));
        var signals = new List<(int Pid, int Signal)>();
        bool Signal(int pid, int sig)
        {
            signals.Add((pid, sig));
            if (sig == ProcStopper.SigTerm && pid == 150)
            {
                table.Rows[152] = (150, 152);
            }
            else if (sig == ProcStopper.SigTerm || sig == ProcStopper.SigKill)
            {
                _ = table.Rows.Remove(pid);
            }

            return true;
        }

        var outcome = new ProcStopper(table.All, Signal, _ => { }).Stop(tree, TimeSpan.FromMilliseconds(300));
        Assert.Equal("killed", outcome);
        Assert.Contains((152, ProcStopper.SigKill), signals);
        Assert.False(table.Rows.ContainsKey(152));
    }

    [Fact]
    public void ParentExitingOnTermStillSignalsAChildForkedAfterTheSnapshot()
    {
        // 152 was forked by 150 after the decision snapshot; 150 and 151 exit on TERM, 152 ignores it
        var table = Tree();
        var tree = ProcOwnership.Tree(table.All()!, table.All()!.Single(n => n.Pid == 150));
        table.Rows[152] = (150, 152);
        var signals = new List<(int Pid, int Signal)>();
        bool Signal(int pid, int sig)
        {
            signals.Add((pid, sig));
            if (sig == ProcStopper.SigKill || pid != 152)
            {
                _ = table.Rows.Remove(pid);
            }

            return true;
        }

        var outcome = new ProcStopper(table.All, Signal, _ => { }).Stop(tree, TimeSpan.FromMilliseconds(300));
        Assert.Equal("stopped", outcome);
        Assert.Contains((152, ProcStopper.SigTerm), signals);
        Assert.Contains((152, ProcStopper.SigKill), signals);
        Assert.False(table.Rows.ContainsKey(152));
    }

    [Fact]
    public void ListTeamReachesOnlyATeamTheCallerOwns()
    {
        var table = Tree();
        var owned = ProcOwnership.OwnedTeams(
            table, null, Self, [(Trusted, new TeamOrchestrator("t", Session, Start(100)), TeamRecord(300))], [Trusted],
            [new RegisteredSession(Session, Start(100), "orchestrator@t")], AliveIn(table));
        Assert.Equal(["impl@t"], ProcOwnership.TeamRoots(table, owned, "t", _ => true)!.Select(r => r.Owner));
        Assert.Null(ProcOwnership.TeamRoots(table, owned, "x", _ => true));
        Assert.Null(ProcOwnership.TeamRoots(table, [], "t", _ => true));
    }

    [Fact]
    public void WaitEndsWhenEveryPidIsGoneAndTimesOutOnOneThatStaysAlive()
    {
        var polls = 0;
        bool Alive(int pid) => pid == 7 || (pid == 5 && polls < 2);
        var gone = new StringWriter();
        Assert.Equal(0, ProcVerb.Wait([5], Alive, TimeSpan.FromSeconds(10), _ => polls++, gone));
        Assert.Equal("{\"gone\":[5],\"alive\":[]}\n", gone.ToString());

        var timedOut = new StringWriter();
        Assert.Equal(1, ProcVerb.Wait([5, 7], Alive, TimeSpan.FromSeconds(1), _ => polls++, timedOut));
        Assert.Equal("{\"gone\":[5],\"alive\":[7]}\n", timedOut.ToString());
        Assert.False(ProcVerb.AcceptsPid("wait", 0));
        Assert.False(ProcVerb.AcceptsPid("wait", 1));
        Assert.True(ProcVerb.AcceptsPid("wait", 2));
    }

    [Fact(Skip = "out of scope: members are trusted (owner's call, 2026-11-01)")]
    public void ForgedRegistrationTeamFileInATempDirOwnsNothing()
    {
        // Today a member that registers its own pid with a team_file it wrote under a temp dir makes that dir trusted, and the
        // forged team (naming the member as Orchestrator) passes the owner proof, so the sibling's child IS owned. This asserts
        // the refusal the open threat-model question would require.
        var table = Tree();
        table.Rows[MemberCall] = (Member, 310);
        var nearest = new NearestSession(NearestKind.Member, "t", "impl", Member);
        var trusted = ProcOwnership.TrustedDirs(table, nearest, [(Member, Start(300), [Forged().Dir + "/teams/x.json"])]);
        var roots = MemberRoots(table, [Forged()], "impl@t", trusted);
        Assert.Null(ProcOwnership.Decide(table.All()!, roots, MemberCall, 501).Owner);
    }

    [Fact]
    public void PidAbsentFromTheSnapshotIsGoneNotRefused()
    {
        var table = Tree();
        var signals = 0;
        var stopper = new ProcStopper(table.All, (_, _) => { signals++; return true; }, _ => { });
        var row = ProcVerb.StopOne(stopper, table.All()!, OrchestratorRoots(table), Self, 777, TimeSpan.FromSeconds(1), dryRun: false, new Dictionary<int, long>());
        Assert.Equal("gone", row.Get("outcome")?.AsString());
        Assert.Equal(0, signals);
    }

    [Fact]
    public void NoSessionAboveTheCallerOwnsNothing()
    {
        var table = Tree();
        var roots = ProcOwnership.Roots(table, new NearestSession(NearestKind.None), [], _ => true);
        Assert.Empty(roots);
        Assert.Null(Decide(table, roots, 151).Owner);
    }

    [Fact]
    public void AReusedPidIsNeverSignalled()
    {
        var table = Tree();
        var tree = ProcOwnership.Tree(table.All()!, table.All()!.Single(n => n.Pid == 150));
        table.Rows[150] = (Session, 9150);
        table.Rows[151] = (150, 9151);
        var signals = new List<(int, int)>();
        var outcome = new ProcStopper(table.All, (p, s) => { signals.Add((p, s)); return true; }, _ => { }).Stop(tree, TimeSpan.FromSeconds(1));
        Assert.Equal("gone", outcome);
        Assert.Empty(signals);
    }

    [Fact]
    public void DryRunSignalsNothing()
    {
        var table = Tree();
        var signals = 0;
        var stopper = new ProcStopper(table.All, (_, _) => { signals++; return true; }, _ => { });
        var row = ProcVerb.StopOne(stopper, table.All()!, OrchestratorRoots(table), Self, 301, TimeSpan.FromSeconds(1), dryRun: true, new Dictionary<int, long> { [301] = 2048 });
        Assert.Equal("dry-run", row.Get("outcome")?.AsString());
        Assert.Equal("impl@t", row.Get("owner")?.AsString());
        Assert.Equal(2.0, row.Get("rss_mb")?.AsNumber());
        Assert.Equal(0, signals);
    }

    [Fact]
    public void RefusedPidIsNotSignalled()
    {
        var table = Tree();
        var signals = 0;
        var stopper = new ProcStopper(table.All, (_, _) => { signals++; return true; }, _ => { });
        var row = ProcVerb.StopOne(stopper, table.All()!, OrchestratorRoots(table), Self, 400, TimeSpan.FromSeconds(1), dryRun: false, new Dictionary<int, long>());
        Assert.Equal("refused", row.Get("outcome")?.AsString());
        Assert.Equal(0, signals);
    }

    [Fact]
    public void ListShowsOnlyOwnedDescendantsLargestFirst()
    {
        var table = Tree();
        var list = ProcVerb.List(table.All()!, OrchestratorRoots(table), Self, new Dictionary<int, long> { [301] = 10240, [151] = 20480, [400] = 99999 }, Epoch.AddSeconds(1000));
        Assert.Equal([151, 301, 150], list.Select(r => (int)r.Member("pid")!.Value.AsNumber()));
        Assert.Equal(20.0, list[0].Member("rss_mb")!.Value.AsNumber());
    }

    [Fact]
    public void ListMatchKeepsOwnedCommandsContainingTheTextAndNeverAForeignOne()
    {
        var table = Tree();
        var rss = new Dictionary<int, long> { [151] = 20480 };
        Assert.Equal([151, 150], ProcVerb.List(table.All()!, OrchestratorRoots(table), Self, rss, Epoch, "cmd 15").Select(r => (int)r.Member("pid")!.Value.AsNumber()));
        Assert.Empty(ProcVerb.List(table.All()!, OrchestratorRoots(table), Self, rss, Epoch, "cmd 400"));
    }

    [Fact]
    public void SleepingChildStopsOnTerm() => Assert.Equal("stopped", StopReal("sleep 30"));

    [Fact]
    public void TermIgnoringTreeIsKilledAfterTheGrace() => Assert.Equal("killed", StopReal("trap '' TERM; sleep 30; true"));

    private static string StopReal(string script)
    {
        using var child = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", script]))!;
        try
        {
            var table = PlatformModule.CreateProcessTable(PlatformModule.CreateProcessRunner());
            IReadOnlyList<ProcessNode> snapshot = [];
            for (var i = 0; i < 50 && !(snapshot.Any(n => n.Pid == child.Id) && snapshot.Any(n => n.Parent == child.Id || script == "sleep 30")); i++)
            {
                Thread.Sleep(50);
                snapshot = table.All() ?? [];
            }

            var tree = ProcOwnership.Tree(snapshot, snapshot.Single(n => n.Pid == child.Id));
            var outcome = new ProcStopper(table.All, PlatformModule.SignalProcess, Thread.Sleep).Stop(tree, TimeSpan.FromMilliseconds(500));
            Assert.True(child.WaitForExit(5000));
            Assert.All(tree, n => Assert.DoesNotContain(table.All() ?? [], m => m.Pid == n.Pid && m.Started == n.Started));
            return outcome;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }
}
