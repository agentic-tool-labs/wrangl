using System.Text;
using Ah.Core.Json;
using Ah.Hooks.Lifecycle;
using Ah.Hooks.Plumbing;
using Ah.TestKit;

namespace Ah.Hooks.Tests;

/// <summary>The Stop rule that winds a member down on two observations of a Down chain a grace apart, with no ahd involved.</summary>
public sealed class StopChainDownTests
{
    private const string Session = "s-a";

    private static HooksHostTests.Rig Member(bool unclaimed)
    {
        var rig = AutoTrackTests.MemberRig("p1");
        if (unclaimed)
        {
            AutoTrackTests.WriteTeam(
                rig,
                new JsonObject
                {
                    ["members"] = new JsonArray { new JsonObject { ["name"] = "implementor-1", ["role"] = "implementor", ["transport_id"] = "p1" } },
                });
        }

        return rig;
    }

    /// <summary>
    /// A manager team <c>tm</c> with a live orchestrator and the lane lead <c>c-so-s</c> (pane p1, this session), and the lane team <c>c</c> that
    /// lead owns, recorded as running in <paramref name="recordedPid"/>. The lead is registered live in pid <see cref="HarnessSessions.Pid"/>,
    /// whose command line is not an ah launch.
    /// </summary>
    private static HooksHostTests.Rig Lane(int recordedPid, out HarnessSessions sessions)
    {
        var rig = new HooksHostTests.Rig(new Dictionary<string, string> { ["HERDR_PANE_ID"] = "p1" });
        AutoTrackTests.WriteTeam(
            rig,
            JsonText.Parse(Encoding.UTF8.GetBytes(
                "{\"version\":1,\"team_id\":\"tm\",\"created\":\"2026-10-20T09:00:00Z\","
                + $"\"orchestrator\":{{\"session_id\":null,\"pid\":{HarnessSessions.Pid},\"pid_start\":\"{HarnessSessions.Start}\"}},"
                + "\"members\":[{\"name\":\"c-so-s\",\"role\":\"sub-orchestrator\",\"transport_id\":\"p1\"}],"
                + "\"streams\":{\"s\":{\"sub_orchestrator\":\"c-so-s\",\"state\":\"open\"}}}")).AsObject());
        var dir = rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");
        File.WriteAllText(
            rig.Services.Roster.TeamPath(dir, "c"),
            "{\"version\":1,\"members\":[],\"parent\":{\"team\":\"tm\",\"stream\":\"s\",\"team_id\":\"tm\"},"
            + $"\"orchestrator\":{{\"session_id\":\"{Session}\",\"pid\":{recordedPid},\"pid_start\":\"{(recordedPid == HarnessSessions.Pid ? HarnessSessions.Start : "Mon Jan  1 00:00:00 2001")}\"}}}}");
        sessions = new HarnessSessions(new Ah.Mesh.SessionRegistration(Session, HarnessSessions.Pid, HarnessSessions.Start, ["orchestrator@c"]));
        return rig;
    }

    [Fact]
    public void ALaneLeadRestoredWithoutAhsLaunchIsSuspectedThenToldToWindDownAtItsOwnTeam()
    {
        var rig = Lane(recordedPid: 99999, out var sessions);

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));
        Assert.Equal(["suspect"], States(rig));

        Advance(rig, 121);
        var second = Stop(rig, sessions: sessions);

        Assert.Contains("\"decision\":\"block\"", second, StringComparison.Ordinal);
        Assert.Contains("your chain of command is down (c: restored-bare)", second, StringComparison.Ordinal);
        Assert.Equal(["suspect", "active"], States(rig));
    }

    [Fact]
    public void NegativeALaneLeadStillRunningInTheProcessItsTeamRecordNamesIsNotRestored()
    {
        var rig = Lane(recordedPid: HarnessSessions.Pid, out var sessions);

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));
        Assert.Empty(States(rig));
    }

    private static string Stop(HooksHostTests.Rig rig, bool withSessions = true, HarnessSessions? sessions = null)
    {
        var e = new JsonObject { ["hook_event_name"] = "Stop", ["session_id"] = Session, ["cwd"] = rig.Sandbox.Root + "/proj" };
        var services = rig.Services with { Advisory = new Ah.Hooks.AdvisoryServices(null!, null!, null!, null!, null!, StopDirective.Instance, null!, null!, null!, null!, null!) };
        if (withSessions)
        {
            services = services with { Sessions = sessions ?? new HarnessSessions() };
        }

        var ctx = new GateContext(services, JsonText.Parse(Encoding.UTF8.GetBytes(JsonText.Stringify(e, 0))), "stop-chain-down");
        var output = Encoding.UTF8.GetString(StopChainDown.Run(ctx).Stdout.Span);
        PersistPeerRows(rig);
        return output;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HooksHostTests.Rig, int[]> Persisted = new();

    /// <summary>The rig's state writer only records a peer row; a later Stop reads rows from the file, so the new ones are appended to it.</summary>
    private static void PersistPeerRows(HooksHostTests.Rig rig)
    {
        var done = Persisted.GetValue(rig, static _ => new int[1]);
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        var calls = rig.Writer.Calls.Zip(rig.Writer.Arguments).Where(p => p.First == "peer-records.append").Select(p => p.Second).ToList();
        foreach (var argument in calls.Skip(done[0]))
        {
            File.AppendAllText(pending, JsonText.Stringify(JsonText.Parse(Encoding.UTF8.GetBytes(argument)).Member("rec")!.Value, 0) + "\n");
        }

        done[0] = calls.Count;
    }

    private static void Advance(HooksHostTests.Rig rig, int seconds) => ((FakeClock)rig.Services.Clock).UtcNow += TimeSpan.FromSeconds(seconds);

    private static List<string> States(HooksHostTests.Rig rig)
    {
        var path = rig.Services.Hierarchy.Peers.PeerPendingPath();
        return File.Exists(path)
            ? [.. File.ReadAllLines(path).Select(l => JsonText.Parse(Encoding.UTF8.GetBytes(l)))
                .Where(r => r.Member("type") is { IsText: true } t && t.AsString() == "chain-down" && r.Member("session_id") is { IsText: true } s && s.AsString() == Session)
                .Select(r => r.Member("state")!.Value.AsString())]
            : [];
    }

    [Fact]
    public void TheFirstObservationOfADownChainOnlyWritesASuspectRowAndTheSecondAGraceLaterBlocksOnce()
    {
        var rig = Member(unclaimed: true);

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Equal(["suspect"], States(rig));

        Advance(rig, 121);
        var second = Stop(rig);

        Assert.Contains("\"decision\":\"block\"", second, StringComparison.Ordinal);
        Assert.Contains("your chain of command is down (tm: unclaimed)", second, StringComparison.Ordinal);
        Assert.Equal(["suspect", "active"], States(rig));
        Assert.Equal(string.Empty, Stop(rig));
        Assert.Equal(["suspect", "active"], States(rig));
    }

    [Fact]
    public void NegativeADownChainSeenTwiceLessThanAGraceApartDoesNotBlock()
    {
        var rig = Member(unclaimed: true);

        Assert.Equal(string.Empty, Stop(rig));
        Advance(rig, 60);

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Equal(["suspect"], States(rig));
    }

    [Fact]
    public void ADownChainThatIsNoLongerDownDropsTheSuspicionAndTheNextDownStartsOver()
    {
        var rig = Member(unclaimed: true);
        _ = Stop(rig);
        var team = Member(unclaimed: false);
        File.Copy(team.Services.Roster.TeamPath(team.Services.Config.HierarchyDir(team.Sandbox.Root + "/proj"), "tm"), rig.Services.Roster.TeamPath(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "tm"), overwrite: true);
        Advance(rig, 121);

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Equal(["suspect", "cleared"], States(rig));
    }

    [Fact]
    public void NegativeAChainThatIsNotProvenDownWritesNothing()
    {
        var rig = Member(unclaimed: false);

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Empty(States(rig));
    }

    [Fact]
    public void NegativeAnActiveRowPassesAndWritesNothingMore()
    {
        var rig = Member(unclaimed: true);
        StopPeerNudgeReportTests.ChainDownRow(rig, Session);
        Advance(rig, 300);

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Equal(["active"], States(rig));
    }

    [Fact]
    public void NegativeNoSessionDirectoryMeansNothingIsJudged()
    {
        var rig = Member(unclaimed: true);

        Assert.Equal(string.Empty, Stop(rig, withSessions: false));
        Assert.Empty(States(rig));
    }

    private static string LaneTeamPath(HooksHostTests.Rig rig) =>
        rig.Services.Roster.TeamPath(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "c");

    /// <summary>The rig's state writer only records an update; this writes the team record the last one would have written.</summary>
    private static void ApplyLastTeamWrite(HooksHostTests.Rig rig) =>
        File.WriteAllText(LaneTeamPath(rig), JsonText.Stringify(JsonText.Parse(Encoding.UTF8.GetBytes(rig.Writer.Mutations[^1])).Member("write")!.Value, 0));

    private static void AppendRow(HooksHostTests.Rig rig, string state, string why, string link, int? pid = null)
    {
        var row = new JsonObject
        {
            ["type"] = "chain-down", ["state"] = state, ["session_id"] = Session, ["member"] = "c-so-s", ["team"] = "tm", ["link"] = link, ["why"] = why, ["at"] = "2026-10-11T11:00:00.000Z",
        };
        if (pid is { } p)
        {
            row["pid"] = (double)p;
            row["pid_start"] = HarnessSessions.Start;
        }

        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        _ = Directory.CreateDirectory(Path.GetDirectoryName(pending)!);
        File.AppendAllText(pending, JsonText.Stringify(row, 0) + "\n");
    }

    private static HooksHostTests.Rig WoundDownLaneLead(out HarnessSessions sessions)
    {
        var rig = Lane(recordedPid: 99999, out sessions);
        _ = Stop(rig, sessions: sessions);
        Advance(rig, 121);
        _ = Stop(rig, sessions: sessions);
        return rig;
    }

    [Fact]
    public void T7hTheBlockStopDoesNotReleaseAndTheNextStopMarksTheTeamAndWritesTheReleasedRowSilently()
    {
        var rig = WoundDownLaneLead(out var sessions);

        Assert.Equal(["suspect", "active"], States(rig));
        Assert.Empty(rig.Writer.Mutations);

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));

        Assert.Equal(["suspect", "active", "released"], States(rig));
        var marker = JsonText.Parse(Encoding.UTF8.GetBytes(Assert.Single(rig.Writer.Mutations))).Member("write")!.Value.Member("orchestrator")!.Value.Member("released")!.Value;
        Assert.Equal(HarnessSessions.Pid, (int)marker.Member("pid")!.Value.AsNumber());
        Assert.Equal(HarnessSessions.Start, marker.Member("pid_start")!.Value.AsString());
    }

    [Fact]
    public void T7aTheReleasedLeadsLaneTeamReadsOrphanedWhileItsRegistrationIsLiveAndAFreshIncarnationOwnsItAgain()
    {
        var rig = WoundDownLaneLead(out var sessions);
        var before = rig.Services.Roster.ReadTeamRecord(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "c");
        _ = Stop(rig, sessions: sessions);
        ApplyLastTeamWrite(rig);
        var released = rig.Services.Roster.ReadTeamRecord(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "c");
        Func<string, (long Pid, string Start)?> live = s => s == Session ? (HarnessSessions.Pid, HarnessSessions.Start) : null;
        Func<string, (long Pid, string Start)?> fresh = s => s == Session ? (HarnessSessions.Pid + 1, HarnessSessions.Start) : null;
        var table = new HarnessSessions { Starts = { [HarnessSessions.Pid + 1] = HarnessSessions.Start } }.Table;

        Assert.Equal(Ah.Roster.TeamLife.OwnedLive, Ah.Roster.RosterModule.TeamLifeOf(before, live, sessions.Table));
        Assert.Equal(Ah.Roster.TeamLife.Orphaned, Ah.Roster.RosterModule.TeamLifeOf(released, live, sessions.Table));
        Assert.Equal(Ah.Roster.TeamLife.OwnedLive, Ah.Roster.RosterModule.TeamLifeOf(released, fresh, table));
    }

    [Fact]
    public void NegativeT7eAChildMembersRestoredBareRowIsNeverReleasedAndTheTeamStaysAsItWas()
    {
        var rig = Lane(recordedPid: 99999, out var sessions);
        File.WriteAllText(
            LaneTeamPath(rig),
            File.ReadAllText(LaneTeamPath(rig)).Replace($"\"session_id\":\"{Session}\"", "\"session_id\":\"s-lead\"", StringComparison.Ordinal));
        AppendRow(rig, "active", "restored-bare", "c");
        var before = File.ReadAllText(LaneTeamPath(rig));

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));

        Assert.Equal(["active"], States(rig));
        Assert.Empty(rig.Writer.Mutations);
        Assert.Equal(before, File.ReadAllText(LaneTeamPath(rig)));
    }

    [Fact]
    public void NegativeT7AnActiveRowWhoseWhyIsNotRestoredBareIsNeverReleased()
    {
        var rig = Lane(recordedPid: 99999, out var sessions);
        AppendRow(rig, "active", "orphaned", "c");

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));

        Assert.Equal(["active"], States(rig));
        Assert.Empty(rig.Writer.Mutations);
    }

    [Fact]
    public void NegativeT7dATopOrchestratorWithAnActiveRestoredBareRowIsNeverReleased()
    {
        var rig = AutoTrackTests.OwnerRig();
        AppendRow(rig, "active", "restored-bare", "c");

        Assert.Equal(string.Empty, Stop(rig));

        Assert.Equal(["active"], States(rig));
        Assert.Empty(rig.Writer.Mutations);
    }

    [Fact]
    public void T7gAFailedReleasedRowAfterTheMarkerIsCompletedByTheNextStopWithOneMarker()
    {
        var rig = WoundDownLaneLead(out var sessions);
        rig.Writer.Failing.Add("peer-records.append");

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));

        // the rig copies every attempted append to the file, the refused one too; the real store keeps none of them
        var pending = rig.Services.Hierarchy.Peers.PeerPendingPath();
        File.WriteAllLines(pending, File.ReadAllLines(pending)[..^1]);
        Assert.Equal(["suspect", "active"], States(rig));
        _ = Assert.Single(rig.Writer.Mutations);
        ApplyLastTeamWrite(rig);
        rig.Writer.Failing.Clear();

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));

        Assert.Equal(["suspect", "active", "released"], States(rig));
        _ = Assert.Single(rig.Writer.Mutations);
    }

    [Fact]
    public void AfterTheReleaseLaterStopsOfTheSameIncarnationWriteNothing()
    {
        var rig = WoundDownLaneLead(out var sessions);
        _ = Stop(rig, sessions: sessions);
        ApplyLastTeamWrite(rig);
        Advance(rig, 300);

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));

        Assert.Equal(["suspect", "active", "released"], States(rig));
        _ = Assert.Single(rig.Writer.Mutations);
    }

    [Fact]
    public void NegativeT8aReleasedRowOfAnotherIncarnationIsNotWoundDownAndTheChainIsJudgedAgain()
    {
        var rig = Lane(recordedPid: 99999, out var sessions);
        AppendRow(rig, "released", "restored-bare", "c", pid: 1111);

        Assert.Equal(string.Empty, Stop(rig, sessions: sessions));

        Assert.Equal(["released", "suspect"], States(rig));
    }

    [Fact]
    public void NegativeATopOrchestratorIsNeverJudged()
    {
        var rig = AutoTrackTests.OwnerRig();

        Assert.Equal(string.Empty, Stop(rig));
        Assert.Empty(States(rig));
    }
}
