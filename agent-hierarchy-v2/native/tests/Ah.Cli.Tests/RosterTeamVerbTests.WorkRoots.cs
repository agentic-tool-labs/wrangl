using System.Globalization;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>Live members that share a work root: the doctor's <c>work-roots</c> row and the spawn warning.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string Reviewer = "proj-reviewer";

    private void PeerRow(string pool, string pane, string role, string status, bool withPid = true)
    {
        Directory.CreateDirectory(pool);
        var ts = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        var pid = withPid ? $",\"pid\":{(status == "down" ? GonePid() : LivePidOfAnotherProcess())}{(status == "down" ? ",\"pid_start\":\"2026-01-01T00:00:00.000Z\"" : string.Empty)}" : string.Empty;
        _sandbox.PlantPeers(
            pool,
            $"{{\"type\":\"peer\",\"status\":\"{status}\",\"session_id\":\"sess-{pane.TrimStart('%')}\",\"role\":\"{role}\"{pid},\"pane_id\":\"{pane}\",\"ts\":\"{ts}\"}}\n");
    }

    private static string RootGrant(string root, string role, string? stream = null) =>
        $"{{\"work_root\":\"{root}\",\"stream\":{(stream is null ? "null" : $"\"{stream}\"")},\"role\":\"{role}\",\"extra\":[]}}";

    /// <summary>A team of an implementor and a reviewer, both registered up, whose grants name the given roots.</summary>
    private (string Status, string Detail) WorkRootsRow(string implementorRoot, string reviewerRoot, string reviewerStatus = "up", string? implementorStream = null, string? reviewerStream = null)
    {
        WriteTeam("proj", LiveTeamJson("tmux", Member("implementor", Impl, "%1") + "," + Member("reviewer", Reviewer, "%2")));
        PeerRow(Hierarchy, "%1", "implementor", "up");
        PeerRow(Hierarchy, "%2", "reviewer", reviewerStatus);
        WriteGrants("enforce", $"{{\"{Impl}\":{RootGrant(implementorRoot, "implementor", implementorStream)},\"{Reviewer}\":{RootGrant(reviewerRoot, "reviewer", reviewerStream)}}}");
        var row = RowNamed(DoctorRows(), "work-roots")!;
        return (row["status"].AsString(), row["detail"].AsString());
    }

    [Fact]
    public void DoctorWarnsWhenTwoLiveMembersShareAWorkRoot()
    {
        var (status, detail) = WorkRootsRow(Project, Project);

        Assert.Equal("warn", status);
        Assert.Contains(Impl, detail, StringComparison.Ordinal);
        Assert.Contains(Reviewer, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorDoesNotWarnWhenOneOfTheSharersIsNotLive()
    {
        var (status, detail) = WorkRootsRow(Project, Project, reviewerStatus: "down");

        Assert.Equal("ok", status);
        Assert.DoesNotContain(Reviewer, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorDoesNotWarnWhenLiveMembersHoldDifferentRoots()
    {
        var other = Path.Combine(_sandbox.Root, "other-root");
        Directory.CreateDirectory(other);

        Assert.Equal("ok", WorkRootsRow(Project, other).Status);
    }

    [Fact]
    public void DoctorOnlyInformsWhenTheSharersGotTheRootFromTheSameStream()
    {
        var (status, detail) = WorkRootsRow(Project, Project, implementorStream: "s1", reviewerStream: "s1");

        Assert.Equal("ok", status);
        Assert.Contains("shared by stream s1", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorListsALiveMemberWithNoWorkRootInTheGrantsAsUnknownAndADeadOneNot()
    {
        WriteTeam("proj", LiveTeamJson("tmux", Member("implementor", Impl, "%1") + "," + Member("reviewer", Reviewer, "%2")));
        PeerRow(Hierarchy, "%1", "implementor", "up");
        PeerRow(Hierarchy, "%2", "reviewer", "down");
        WriteGrants("enforce", "{}");

        var detail = RowNamed(DoctorRows(), "work-roots")!["detail"].AsString();

        Assert.Contains("unknown root", detail, StringComparison.Ordinal);
        Assert.Contains(Impl, detail, StringComparison.Ordinal);
        Assert.DoesNotContain(Reviewer, detail, StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWarnsWhenOnlyOneOfTheSharersCameFromAStream()
    {
        Assert.Equal("warn", WorkRootsRow(Project, Project, implementorStream: "s1").Status);
    }

    [Fact]
    public void DoctorWarnsWhenTheSharersCameFromDifferentStreams()
    {
        Assert.Equal("warn", WorkRootsRow(Project, Project, implementorStream: "s1", reviewerStream: "s2").Status);
    }

    private List<string> ProjTeamsWarnings(string[]? extra = null)
    {
        var (code, stdout, stderr) = Roster(["teams", .. extra ?? []]);
        Assert.True(code == 0, stdout + stderr);
        var row = JsonText.Parse(stdout).Member("teams")!.Value.AsArray().Single(r => r.Member("name") is { IsText: true } n && n.AsString() == "proj");
        return row.Member("warnings") is { IsArray: true } w ? [.. w.AsArray().Select(x => x.AsString())] : [];
    }

    [Fact]
    public void TeamsWarnsOnceNamingTheRootAndBothMembersWhenTwoLiveMembersShareAWorkRoot()
    {
        WorkRootsRow(Project, Project);

        var warnings = ProjTeamsWarnings();

        var shared = Assert.Single(warnings);
        Assert.Contains(Project, shared, StringComparison.Ordinal);
        Assert.Contains(Impl, shared, StringComparison.Ordinal);
        Assert.Contains(Reviewer, shared, StringComparison.Ordinal);
    }

    [Fact]
    public void TeamsDoesNotWarnWhenOneOfTheSharersIsNotLive()
    {
        WorkRootsRow(Project, Project, reviewerStatus: "down");

        Assert.Empty(ProjTeamsWarnings());
    }

    [Fact]
    public void TeamsDoesNotWarnWhenLiveMembersHoldDifferentRoots()
    {
        var other = Path.Combine(_sandbox.Root, "other-root");
        Directory.CreateDirectory(other);
        WorkRootsRow(Project, other);

        Assert.Empty(ProjTeamsWarnings());
    }

    [Fact]
    public void TeamsDoesNotWarnWhenTheSharersGotTheRootFromTheSameStream()
    {
        WorkRootsRow(Project, Project, implementorStream: "s1", reviewerStream: "s1");

        Assert.Empty(ProjTeamsWarnings());
    }

    // fewer-turns S5.1: T-1 (4 live members, no warning) and T-2 (5 live: a warning); a dead row is not counted.
    [Theory]
    [InlineData(4, null)]
    [InlineData(5, "team-size: 5 live members (rule: 4, +1 briefly)")]
    public void TeamsWarnsWhenLiveMembersPassTheSizeRule(int live, string? expected)
    {
        var rows = Enumerable.Range(1, live).Select(i => $"{{\"role\":\"implementor\",\"name\":\"m{i}\",\"route\":\"peer\",\"transport_id\":\"%{i}\",\"pid\":{Self}}}")
            .Append("{\"role\":\"reviewer\",\"name\":\"dead\",\"route\":\"peer\",\"transport_id\":\"%9\",\"pid\":2000000000}");
        WriteTeam("proj", LiveTeamJson("tmux", string.Join(",", rows)));

        var warnings = ProjTeamsWarnings().Where(w => w.StartsWith("team-size:", StringComparison.Ordinal));

        Assert.Equal(expected is null ? [] : [expected], warnings);
    }

    [Fact]
    public void TeamsWarnsWhenOnlyOneOfTheSharersCameFromAStream()
    {
        WorkRootsRow(Project, Project, implementorStream: "s1");

        Assert.Single(ProjTeamsWarnings());
    }

    [Fact]
    public void TeamsKeepsTheSplitCopyWarningBesideTheSharedRootWarning()
    {
        var home = SpawnTeamHome();
        var worktree = MakeWorkTree("t1");
        var team = LiveTeamJson("tmux", Member("implementor", Impl, "%1") + "," + Member("reviewer", Reviewer, "%2"));
        var pool = Path.Combine(worktree, ".claude", "ah2");
        Directory.CreateDirectory(Path.Combine(pool, "teams"));
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), team);
        _sandbox.PlantTeam(Path.Combine(pool, "teams", "proj.json"), team);
        foreach (var dir in new[] { home, pool })
        {
            PeerRow(dir, "%1", "implementor", "up");
            PeerRow(dir, "%2", "reviewer", "up");
        }

        WriteHomeDocument(home, $"{{\"{Impl}\":{RootGrant(Project, "implementor")},\"{Reviewer}\":{RootGrant(Project, "reviewer")}}}");

        var warnings = ProjTeamsWarnings(["--cwd", worktree]);

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.StartsWith("Team proj has records in both ", StringComparison.Ordinal) && w.EndsWith("(left by an older release). Both were read.", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains(" is shared by ", StringComparison.Ordinal));
    }

    private static readonly string[] ReplaceLive = ["--replace-live"];

    private (int Code, JsonObject Out, string Err) SpawnOntoTheRootOf(string reviewerStatus, string reviewerRoot, bool replaceLive = false, string holderRole = "docs-writer", string spawnRole = "implementor")
    {
        var home = SpawnTeamHome();
        var worktree = MakeWorkTree("t1");
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", Member(holderRole, Reviewer, "%2")));
        PeerRow(home, "%2", holderRole, reviewerStatus);
        WriteHomeDocument(home, $"{{\"{Reviewer}\":{RootGrant(reviewerRoot == "TREE" ? RealPathOf(worktree) : reviewerRoot, holderRole)}}}");
        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", spawnRole, "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--cwd", worktree, .. replaceLive ? ReplaceLive : []]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject(), stdout + stderr);
    }

    [Theory]
    [InlineData("docs-writer", "implementor", 2)]
    [InlineData("implementor", "docs-writer", 2)]
    [InlineData("reviewer", "implementor", 0)]
    [InlineData("docs-writer", "reviewer", 0)]
    [InlineData("reviewer", "reviewer", 0)]
    public void OnlyALiveWriterOwnsATreeAndOnlyAWriterIsRefusedForIt(string holderRole, string spawnRole, int expected)
    {
        var (code, refused, err) = SpawnOntoTheRootOf("up", "TREE", holderRole: holderRole, spawnRole: spawnRole);

        Assert.True(code == expected, err);
        if (expected == 2)
        {
            Assert.Equal("live-member", refused["refused"].AsString());
        }
    }

    private (int Code, JsonObject Out, string Err) SpawnIntoAStreamTreeHeldBy(string holderRole, string spawnRole, string holderStatus = "up", string? orchestratorSession = null)
    {
        var home = SpawnTeamHome();
        var worktree = RealPathOf(MakeWorkTree("s1"));
        var row = $"{{\"role\":\"{holderRole}\",\"name\":\"{Reviewer}\",\"route\":\"peer\",\"transport_id\":\"%2\",\"model\":\"sonnet\",\"stream\":\"s1\",\"expected_root\":\"{worktree}\"}}";
        var teamJson = LiveTeamJson("tmux", row);
        teamJson = orchestratorSession is null ? teamJson : teamJson.Replace("\"session_id\":null", $"\"session_id\":\"{orchestratorSession}\"", StringComparison.Ordinal);
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj.json"),
            teamJson.TrimEnd('}')
            + ",\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":\"" + worktree + "\",\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}}}");
        PeerRow(home, "%2", holderRole, holderStatus);
        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", spawnRole, "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1"]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject(), stdout + stderr);
    }

    [Theory]
    [InlineData("implementor", "implementor", "up", 2)]
    [InlineData("docs-writer", "implementor", "up", 2)]
    [InlineData("implementor", "reviewer", "up", 0)]
    [InlineData("reviewer", "implementor", "up", 0)]
    [InlineData("implementor", "implementor", "down", 0)]
    public void ASecondWriterIntoAStreamTreeIsRefusedWhileAReaderOrADeadOwnerIsNot(string holderRole, string spawnRole, string holderStatus, int expected)
    {
        var (code, refused, err) = SpawnIntoAStreamTreeHeldBy(holderRole, spawnRole, holderStatus);

        Assert.True(code == expected, err);
        if (expected == 2)
        {
            Assert.Equal("live-member", refused["refused"].AsString());
        }
    }

    /// <summary>
    /// The stream holder's registration (written a minute ago) says <paramref name="holder"/>; the team's orchestrator session is
    /// sess-orch; ahd's stall record holds <paramref name="stall"/> (none when null).
    /// </summary>
    private (int Code, JsonObject Out, string Err) SpawnPastAStallReportedHolder(Ah.Hierarchy.MemberRegistrationLiveness holder, string? stall)
    {
        _registrations = new() { [Reviewer + "@proj"] = holder };
        _stallReported = stall;
        return SpawnIntoAStreamTreeHeldBy("implementor", "implementor", orchestratorSession: "sess-orch");
    }

    /// <summary>A stall record of one entry: <paramref name="key"/> told for <paramref name="episode"/> <paramref name="toldSecondsAgo"/> seconds ago.</summary>
    private static string StallEntry(string? key = null, string episode = "sess-2", int toldSecondsAgo = 10) =>
        $"{{\"{key ?? $"proj/{Reviewer}@sess-orch"}\":{{\"episode\":\"{episode}\",\"at\":\"{Stamp(5)}\",\"told_at\":\"{Stamp(toldSecondsAgo)}\"}}}}";

    private static string Stamp(int secondsAgo) => Stamp(DateTimeOffset.UtcNow.AddSeconds(-secondsAgo));

    private static Ah.Hierarchy.MemberRegistrationLiveness Undecided =>
        new("sess-2", false, "no usable observation of its process", DateTimeOffset.UtcNow.AddSeconds(-60));

    [Fact]
    public void AnUndecidedHolderAhdReportedDownForItsCurrentSessionYieldsItsStreamToAFlaglessSpawn()
    {
        var (code, spawned, err) = SpawnPastAStallReportedHolder(Undecided, StallEntry());

        Assert.True(code == 0, err);
        var superseded = Assert.Single(spawned["superseded"].AsArray()).AsObject();
        Assert.Equal(Reviewer, superseded["name"].AsString());
        Assert.Equal("sess-2", superseded["session_id"].AsString());
        Assert.Equal("feat/s1", superseded["branch"].AsString());
        Assert.EndsWith(Path.Combine("worktrees", "s1"), superseded["work_root"].AsString(), StringComparison.Ordinal);
        Assert.Equal("stall-reported", superseded["evidence"].AsString());
    }

    [Fact]
    public void AProvenDeadHolderIsListedAsSupersededByTheSpawnIntoItsStream()
    {
        var (code, spawned, err) = SpawnPastAStallReportedHolder(new("sess-2", false, "registration pid is not running", DateTimeOffset.UtcNow.AddSeconds(-60), ProvenDead: true), null);

        Assert.True(code == 0, err);
        Assert.Equal("proven-dead", Assert.Single(spawned["superseded"].AsArray()).AsObject()["evidence"].AsString());
    }

    [Theory]
    [InlineData("other-session")]
    [InlineData("none")]
    [InlineData("earlier-orchestrator")]
    [InlineData("older-than-registration")]
    [InlineData("other-member")]
    [InlineData("other-team")]
    [InlineData("legacy-string")]
    [InlineData("no-told-at")]
    public void NegativeAStallRecordThatDoesNotShowTheHolderDownSinceItsLastLifeLeavesItRefusedWithItsWhy(string record)
    {
        var stall = record switch
        {
            "other-session" => StallEntry(episode: "sess-1"),
            "none" => null,
            "earlier-orchestrator" => StallEntry(key: $"proj/{Reviewer}@sess-old-orch"),
            "older-than-registration" => StallEntry(toldSecondsAgo: 120),
            "other-member" => StallEntry(key: "proj/proj-implementor@sess-orch"),
            "other-team" => StallEntry(key: $"other/{Reviewer}@sess-orch"),
            "legacy-string" => $"{{\"proj/{Reviewer}@sess-orch\":\"sess-2\"}}",
            _ => $"{{\"proj/{Reviewer}@sess-orch\":{{\"episode\":\"sess-2\",\"at\":\"{Stamp(5)}\"}}}}",
        };

        var (code, refused, err) = SpawnPastAStallReportedHolder(Undecided, stall);

        Assert.True(code == 2, err);
        Assert.Equal("live-member", refused["refused"].AsString());
        Assert.Contains("its liveness could not be checked: no usable observation of its process", err, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeALiveHolderIsRefusedEvenWhenAhdReportedItsSessionDown()
    {
        var (code, refused, err) = SpawnPastAStallReportedHolder(new("sess-2", true, "registration live", DateTimeOffset.UtcNow.AddSeconds(-60)), StallEntry());

        Assert.True(code == 2, err);
        Assert.Equal("live-member", refused["refused"].AsString());
        Assert.Contains("is owned by live " + Reviewer, err, StringComparison.Ordinal);
    }

    private (int Code, JsonObject Out, string Err, string TeamFile) MoveIntoStreamTree(string ownerRole, string ownerStatus, string moverRole = "implementor", string stream = "s1", params string[] extra)
    {
        var home = SpawnTeamHome();
        var worktree = RealPathOf(MakeWorkTree("s1"));
        var owner = $"{{\"role\":\"{ownerRole}\",\"name\":\"{Reviewer}\",\"route\":\"peer\",\"transport_id\":\"%2\",\"model\":\"sonnet\",\"stream\":\"s1\",\"expected_root\":\"{worktree}\"}}";
        var mover = $"{{\"role\":\"{moverRole}\",\"name\":\"mover\",\"route\":\"peer\",\"transport_id\":\"%3\",\"model\":\"sonnet\"}}";
        var teamPath = Path.Combine(home, "teams", "proj.json");
        _sandbox.PlantTeam(
            teamPath,
            LiveTeamJson("tmux", owner + "," + mover).TrimEnd('}')
            + ",\"streams\":{\"s1\":{\"state\":\"open\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":\"" + worktree + "\",\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"}}}");
        PeerRow(home, "%2", ownerRole, ownerStatus);
        var (code, stdout, stderr) = Roster(["move", "mover", "--stream", stream, "--team", "proj", .. extra]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject(), stdout + stderr, _sandbox.TeamText(teamPath));
    }

    [Fact]
    public void MovingAMemberIntoAFreeStreamTreeSetsItsStreamAndRootInOneStep()
    {
        var (code, moved, err, team) = MoveIntoStreamTree("reviewer", "up");

        Assert.True(code == 0, err);
        Assert.True(moved["moved"].AsBool());
        var mover = JsonText.Parse(team).Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "mover");
        Assert.Equal("s1", mover.Member("stream")!.Value.AsString());
        Assert.Equal(moved["work_root"].AsString(), mover.Member("expected_root")!.Value.AsString());
    }

    [Theory]
    [InlineData("implementor", "up", "implementor", 2)]
    [InlineData("docs-writer", "up", "implementor", 2)]
    [InlineData("implementor", "down", "implementor", 0)]
    [InlineData("implementor", "up", "reviewer", 0)]
    public void MovingAWriterIntoAStreamTreeWithALiveWriterOwnerIsRefused(string ownerRole, string ownerStatus, string moverRole, int expected)
    {
        var (code, refused, err, team) = MoveIntoStreamTree(ownerRole, ownerStatus, moverRole);

        Assert.True(code == expected, err);
        var mover = JsonText.Parse(team).Member("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "mover");
        if (expected == 2)
        {
            Assert.Equal("live-member", refused["refused"].AsString());
            Assert.Null(mover.Member("stream") is { IsText: true } s ? s.AsString() : null);
        }
        else
        {
            Assert.Equal("s1", mover.Member("stream")!.Value.AsString());
        }
    }

    [Fact]
    public void MovingWithReplaceLiveOverridesTheOwnerRuleAndAnUnknownStreamIsRefused()
    {
        var (overridden, _, overrideErr, _) = MoveIntoStreamTree("implementor", "up", extra: "--replace-live");
        var (unknown, refused, _, _) = MoveIntoStreamTree("reviewer", "up", stream: "nope");

        Assert.True(overridden == 0, overrideErr);
        Assert.Equal(2, unknown);
        Assert.Equal("unknown-stream", refused["refused"].AsString());
    }

    private string TwoStreams(string first, string second) =>
        TeamWithStream(first).Replace(
            "\"streams\":{\"s1\":",
            "\"streams\":{\"s2\":{\"state\":\"open\",\"branch\":\"feat/s2\",\"base\":\"main\",\"worktree\":\"" + second + "\",\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"},\"s1\":",
            StringComparison.Ordinal);

    [Fact]
    public void ASpawnIntoOneStreamWithAnExplicitRootThatIsAnotherStreamsWorkTreeIsRefused()
    {
        var first = MakeWorkTree("s1");
        var second = MakeWorkTree("s2");
        WriteTeam("proj", TwoStreams(first, second));

        var (code, refused, err) = SpawnWith(["--dry-run", "--stream", "s1", "--cwd", second]);

        Assert.True(code == 2, err);
        Assert.Equal("cross-stream-root", refused["refused"].AsString());
        Assert.Contains("is stream s2's worktree; use --stream s2, or omit the root", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpawnRootedAtTheMainCheckoutIsNotRefusedWhenAnotherStreamsWorkTreeWasDeleted()
    {
        var first = MakeWorkTree("s1");
        var second = MakeWorkTree("s2");
        WriteTeam("proj", TwoStreams(first, second));
        Directory.Delete(second, recursive: true);

        var (code, _, err) = SpawnWith(["--dry-run", "--stream", "s1", "--cwd", Project]);

        Assert.True(code == 0, err);
    }

    [Fact]
    public void ASpawnRootedAtTheMainCheckoutIsStillRefusedWhenAnotherStreamsWorkTreeExists()
    {
        var first = MakeWorkTree("s1");
        var second = MakeWorkTree("s2");
        WriteTeam("proj", TwoStreams(first, second));

        var (code, refused, err) = SpawnWith(["--dry-run", "--stream", "s1", "--cwd", second]);

        Assert.True(code == 2, err);
        Assert.Equal("cross-stream-root", refused["refused"].AsString());
    }

    [Fact]
    public void RecoverAndTeamsReportAnOpenStreamWhoseWorkTreeWasDeletedAndNotOneThatExists()
    {
        var first = MakeWorkTree("s1");
        var second = MakeWorkTree("s2");
        RecoverTeamWithStreams(TwoStreams(first, second));
        Directory.Delete(second, recursive: true);

        var (code, stdout, stderr) = Recover();
        var (teamsCode, teams, teamsErr) = Roster("teams", "--json");

        Assert.True(code == 0, stderr);
        Assert.Contains("stale-stream-worktree: stream s2", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("stream s1", stdout, StringComparison.Ordinal);
        Assert.True(teamsCode == 0, teamsErr);
        Assert.Contains("\"stale-stream-worktree\"", teams, StringComparison.Ordinal);
        Assert.Contains(second.Replace("\\", "\\\\", StringComparison.Ordinal), teams, StringComparison.Ordinal);
    }

    private void RecoverTeamWithStreams(string teamJson)
    {
        var doc = JsonText.Parse(teamJson).AsObject();
        doc["orchestrator"] = new JsonObject { ["pid"] = Environment.ProcessId, ["pid_start"] = SelfStart };
        WriteTeam("proj", JsonText.Stringify(JsonValue.FromObject(doc), 0));
    }

    [Theory]
    [InlineData("own")]
    [InlineData("none")]
    public void ASpawnIntoAStreamWithItsOwnWorkTreeOrAnUnrelatedRootIsNotRefusedAsCrossStream(string root)
    {
        var first = MakeWorkTree("s1");
        var second = MakeWorkTree("s2");
        WriteTeam("proj", TwoStreams(first, second));

        var (code, _, err) = SpawnWith(["--dry-run", "--stream", "s1", "--cwd", root == "own" ? first : MakeWorkTree("other")]);

        Assert.True(code == 0, err);
    }

    private static List<string> WarningsOf(JsonObject spawned) =>
        spawned.Get("warnings") is { IsArray: true } list ? [.. list.AsArray().Select(w => w.AsString())] : [];

    [Fact]
    public void ASpawnOntoALiveMembersWorkRootIsRefusedAndLaunchesNothing()
    {
        var (code, refused, err) = SpawnOntoTheRootOf("up", "TREE");

        Assert.Equal(2, code);
        Assert.Equal("live-member", refused["refused"].AsString());
        Assert.Equal(Reviewer, refused["member"].AsString());
        Assert.Contains("--replace-live", err, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Any(a => a.Contains("claude", StringComparison.Ordinal)));
    }

    [Fact]
    public void ASpawnOntoALiveMembersWorkRootWithReplaceLiveSucceedsAndWarns()
    {
        var (code, spawned, err) = SpawnOntoTheRootOf("up", "TREE", replaceLive: true);

        Assert.True(code == 0, err);
        Assert.Contains(WarningsOf(spawned), w => w.Contains("is shared with live", StringComparison.Ordinal) && w.Contains(Reviewer, StringComparison.Ordinal));
    }

    [Fact]
    public void ASpawnDoesNotWarnWhenTheMemberOnItsRootIsNotLive()
    {
        var (code, spawned, err) = SpawnOntoTheRootOf("down", "TREE");

        Assert.True(code == 0, err);
        Assert.DoesNotContain(WarningsOf(spawned), w => w.Contains("is shared with live", StringComparison.Ordinal));
    }

    [Fact]
    public void ASpawnDoesNotWarnWhenTheLiveMemberHoldsAnotherRoot()
    {
        var (code, spawned, err) = SpawnOntoTheRootOf("up", Project);

        Assert.True(code == 0, err);
        Assert.DoesNotContain(WarningsOf(spawned), w => w.Contains("is shared with live", StringComparison.Ordinal));
    }

    private (int Code, JsonObject Out, string Err) SpawnWithNoGrants(string reviewerStatus, string expectedRoot, string kind = "")
    {
        var home = SpawnTeamHome();
        var worktree = MakeWorkTree("t1");
        var root = expectedRoot == "TREE" ? RealPathOf(worktree) : expectedRoot;
        var row = $"{{\"role\":\"docs-writer\",\"name\":\"{Reviewer}\",\"route\":\"peer\",\"transport_id\":\"%2\",{kind}\"model\":\"sonnet\",\"expected_root\":\"{root}\"}}";
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", row));
        PeerRow(expectedRoot == "TREE" ? Path.Combine(worktree, ".claude", "ah2") : home, "%2", "docs-writer", reviewerStatus, withPid: !kind.Contains("codex", StringComparison.Ordinal));
        return SpawnWith(["--cwd", worktree]);
    }

    [Fact]
    public void ASpawnOntoTheExpectedRootOfALiveMemberOfATeamWithNoGrantsIsRefused()
    {
        var (code, refused, err) = SpawnWithNoGrants("up", "TREE");

        Assert.True(code == 2, err);
        Assert.Equal("live-member", refused["refused"].AsString());
        Assert.Contains("is owned by live " + Reviewer + " (docs-writer); open a stream for a new worktree, or remove " + Reviewer + " first", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpawnWithNoGrantsProceedsWhenTheLiveMembersExpectedRootIsAnotherTree()
    {
        var (code, _, err) = SpawnWithNoGrants("up", Project);

        Assert.True(code == 0, err);
    }

    [Fact]
    public void ASpawnWithNoGrantsProceedsWhenTheMemberOnTheExpectedRootIsNotLive()
    {
        var (code, _, err) = SpawnWithNoGrants("down", "TREE");

        Assert.True(code == 0, err);
    }

    [Fact]
    public void ASpawnOntoTheRootOfAMemberWhoseLivenessCannotBeDecidedIsRefusedAsMayBeLive()
    {
        var (code, refused, err) = SpawnWithNoGrants("up", "TREE", "\"kind\":\"codex\",");

        Assert.Equal(2, code);
        Assert.Equal("live-member", refused["refused"].AsString());
        Assert.Contains("may be live", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ASpawnWithNoGrantsIsRefusedForAStreamMembersOwnExpectedRoot()
    {
        var (code, refused, err) = SpawnWithNoGrants("up", "TREE", "\"stream\":\"s1\",");

        Assert.True(code == 2, err);
        Assert.Equal("live-member", refused["refused"].AsString());
    }

    [Fact]
    public void ASpawnWithNoGrantsIgnoresAStreamMemberWhoseOwnRootIsTheSamePathAsTheTeams()
    {
        var home = SpawnTeamHome();
        var worktree = MakeWorkTree("t1");
        var root = RealPathOf(worktree);
        var row = $"{{\"role\":\"reviewer\",\"name\":\"{Reviewer}\",\"route\":\"peer\",\"transport_id\":\"%2\",\"stream\":\"s1\",\"model\":\"sonnet\",\"expected_root\":\"{root}\"}}";
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", row).Replace($"\"expected_root\":\"{Project}\",\"roster\"", $"\"expected_root\":\"{root}\",\"roster\""));
        PeerRow(Path.Combine(worktree, ".claude", "ah2"), "%2", "reviewer", "up");

        var (code, _, err) = SpawnWith(["--cwd", worktree]);

        Assert.True(code == 0, err);
    }

    [Fact]
    public void ASpawnWithNoGrantsIgnoresAStreamMemberWhoseOwnRootIsASymlinkToTheTeamsRoot()
    {
        var home = SpawnTeamHome();
        var worktree = MakeWorkTree("t1");
        var link = Path.Combine(Path.GetDirectoryName(worktree)!, "link-t1");
        Directory.CreateSymbolicLink(link, worktree);
        var row = $"{{\"role\":\"reviewer\",\"name\":\"{Reviewer}\",\"route\":\"peer\",\"transport_id\":\"%2\",\"stream\":\"s1\",\"model\":\"sonnet\",\"expected_root\":\"{link}\"}}";
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", row).Replace($"\"expected_root\":\"{Project}\",\"roster\"", $"\"expected_root\":\"{RealPathOf(worktree)}\",\"roster\""));
        PeerRow(Path.Combine(worktree, ".claude", "ah2"), "%2", "reviewer", "up");

        var (code, _, err) = SpawnWith(["--cwd", worktree]);

        Assert.True(code == 0, err);
    }

    [Fact]
    public void ASpawnWithNoGrantsIgnoresAStreamMemberWhoseRootIsOnlyTheTeams()
    {
        var home = SpawnTeamHome();
        var worktree = MakeWorkTree("t1");
        var row = $"{{\"role\":\"reviewer\",\"name\":\"{Reviewer}\",\"route\":\"peer\",\"transport_id\":\"%2\",\"stream\":\"s1\",\"model\":\"sonnet\"}}";
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", row).Replace($"\"expected_root\":\"{Project}\"", $"\"expected_root\":\"{RealPathOf(worktree)}\""));
        PeerRow(Path.Combine(worktree, ".claude", "ah2"), "%2", "reviewer", "up");

        var (code, _, err) = SpawnWith(["--cwd", worktree]);

        Assert.True(code == 0, err);
    }

    [Fact]
    public void ASpawnWithNoGrantsIsRefusedForANonStreamMemberWhoseRootIsOnlyTheTeams()
    {
        var home = SpawnTeamHome();
        var worktree = MakeWorkTree("t1");
        var row = $"{{\"role\":\"docs-writer\",\"name\":\"{Reviewer}\",\"route\":\"peer\",\"transport_id\":\"%2\",\"model\":\"sonnet\"}}";
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", row).Replace($"\"expected_root\":\"{Project}\"", $"\"expected_root\":\"{RealPathOf(worktree)}\""));
        PeerRow(Path.Combine(worktree, ".claude", "ah2"), "%2", "docs-writer", "up");

        var (code, refused, err) = SpawnWith(["--cwd", worktree]);

        Assert.True(code == 2, err);
        Assert.Equal("live-member", refused["refused"].AsString());
    }
}
