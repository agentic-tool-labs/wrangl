using System.Globalization;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.TestKit;

namespace Ah.Cli.Tests;

/// <summary>The release check <c>dismiss</c> and <c>disband --close</c> run before they close a member.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";

    private const string Impl = "proj-implementor";

    private string OneImplementor => Member("implementor", Impl, "%1");

    private string Member(string role, string name, string pane) =>
        $"{{\"role\":\"{role}\",\"name\":\"{name}\",\"route\":\"peer\",\"transport_id\":\"{pane}\",\"model\":\"sonnet\",\"expected_root\":\"{Project}\"}}";

    private string _head = Sha;

    private string _porcelain = string.Empty;

    /// <summary>The branch git reports; a detached HEAD by default, so a live member with no release request is not idle and clean.</summary>
    private string _branch = "HEAD";

    private void ScriptGit()
    {
        Directory.CreateDirectory(Path.Combine(Project, ".git"));
        _runner.Output = argv => argv.Contains("--show-toplevel") ? Project + "\n"
            : argv.Contains("--abbrev-ref") ? _branch + "\n"
            : argv.Contains("rev-list") ? "0\n"
            : argv.Contains("rev-parse") && argv.Contains("HEAD") ? _head + "\n"
            : argv.Contains("status") ? _porcelain
            : string.Empty;
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private void PeerUp(string name, string pane, bool withCwd = true, string session = "sess-impl", string? at = null, string? cwdPath = null)
    {
        var cwd = withCwd ? $",\"cwd\":\"{cwdPath ?? Project}\"" : string.Empty;
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"up\",\"name\":\"{name}\",\"role\":\"implementor\",\"session_id\":\"{session}\",\"pid\":{LivePidOfAnotherProcess()},\"pane_id\":\"{pane}\"{cwd},\"ts\":\"{at ?? Stamp(DateTimeOffset.UtcNow)}\"}}\n");
    }

    /// <summary>One implementor on tmux; <paramref name="live"/> gives it a live peers row.</summary>
    private void ArrangeImplementor(bool live = true)
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor));
        if (live)
        {
            PeerUp(Impl, "%1");
        }

        ScriptGit();
    }

    private string MsgPath(string id, string to, string slug, string type, string? hier = null) => Path.Combine(hier ?? Hierarchy, "msgs", $"{id}--{to}--{slug}--{type}.md");

    private string AskRelease(string id, string member = Impl, string from = "orchestrator", string slug = "release", string? hier = null)
    {
        var path = MsgPath(id, "implementor", slug, "request", hier);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ = TestMsgStore.Plant(
            _sandbox.AhHome,
            path,
            $"---\nid: {id}\ntype: request\nto: implementor\nfrom: {from}\nslug: {slug}\nparent: null\nreason: null\neta: null\nto_name: {member}\nfrom_name: null\nteam: proj\ncreated: 2026-10-16T01:00:00-04:00\n---\n\n## [0] tldr\nRelease.\n");
        return path;
    }

    private string Answer(string id, string body, string slug = "release", DateTimeOffset? written = null, string? hier = null)
    {
        var path = MsgPath(id, "orchestrator", slug, "response", hier);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(
            path,
            $"---\nid: {id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: {slug}\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: {Impl}\nteam: proj\ncreated: 2026-10-16T01:05:00-04:00\n---\n\n{body}");
        File.SetLastWriteTimeUtc(path, (written ?? DateTimeOffset.UtcNow.AddMinutes(-30)).UtcDateTime);
        TestMsgStore.Store(_sandbox.AhHome, path);
        return path;
    }

    private static string StatusSection(string line) => $"## [0] tldr\n- ok\n\n## [1] status\n{line}\n\n## [2] changes\n- none\n";

    private static string ReadyLine(string sha = Sha) => $"Release: ready branch=ah/feature sha={sha[..7]} inflight=none";

    private void Activity(string file, string state, DateTimeOffset at)
    {
        _sandbox.PlantActivity(Hierarchy, Path.GetFileNameWithoutExtension(file), $"{{\"activity\":\"{state}\",\"at\":\"{Stamp(at)}\"}}");
    }

    private (int Code, string Status, string Detail, JsonValue Plan) PlanOfImplementor(params string[] extra)
    {
        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--plan", .. extra, "--team", "proj"]);
        Assert.True(code == 0, stderr);
        var plan = JsonText.Parse(stdout);
        var row = plan.Member("release")!.Value.Member("statuses")!.Value.AsArray().Single(s => s.Member("member")!.Value.AsString() == Impl);
        var status = row.Member("status")!.Value.AsString();
        var closes = status != "busy";
        Assert.Equal(closes, row.Member("closable")!.Value.AsBool());
        Assert.Equal(closes ? 0 : 1, plan.Member("held")!.Value.AsArray().Count);
        return (code, status, row.Member("detail")!.Value.AsString(), plan);
    }

    private (int Code, string Stdout, string Stderr) CloseImplementor(params string[] extra)
    {
        var plan = Roster(["dismiss", Impl, "--plan", .. extra, "--team", "proj"]);
        var token = JsonText.Parse(plan.Stdout).Member("close_token") is { IsText: true } t ? t.AsString() : "none";
        return Roster(["dismiss", Impl, "--close", "--confirm", .. extra, "--plan-token", token, "--team", "proj"]);
    }

    private bool Killed(string pane) => _runner.Calls.Any(argv => argv.Contains("kill-pane") && argv.Contains(pane));

    // ---------------------------------------------------------------- the answer

    private const string Synth = "implementor@sess-imp";

    private void ArrangeNamelessLivePeer()
    {
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"up\",\"role\":\"implementor\",\"session_id\":\"sess-impl\",\"pid\":{LivePidOfAnotherProcess()},\"pane_id\":\"%1\",\"team\":\"proj\",\"cwd\":\"{Project}\",\"ts\":\"{Stamp(DateTimeOffset.UtcNow)}\"}}\n");
        ScriptGit();
    }

    private (string Status, string Detail) PlanOfNamelessPeer()
    {
        var (code, stdout, stderr) = Roster(["dismiss", Synth, "--plan", "--team", "proj"]);
        Assert.True(code == 0, stderr);
        var row = JsonText.Parse(stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray().Single(s => s.Member("member")!.Value.AsString() == Synth);
        return (row.Member("status")!.Value.AsString(), row.Member("detail")!.Value.AsString());
    }

    [Fact]
    public void ALiveSessionKnownOnlyByItsRoleAtSessionNameIsJudgedLiveNotNotRunning()
    {
        ArrangeNamelessLivePeer();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (busy, _) = PlanOfNamelessPeer();
        Assert.Equal("busy", busy);

        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow);

        var (status, detail) = PlanOfNamelessPeer();
        Assert.Equal("ready", status);
        Assert.Equal("idle-clean", detail);
    }

    [Fact]
    public void ANamedLivePeerOutsideTheTeamFileIsReadAtTheCwdOfItsOwnRow()
    {
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));
        _sandbox.PlantPeers(
            Hierarchy,
            $"{{\"type\":\"peer\",\"status\":\"up\",\"name\":\"{Impl}\",\"role\":\"implementor\",\"session_id\":\"sess-impl\",\"pid\":{LivePidOfAnotherProcess()},\"pane_id\":\"%1\",\"team\":\"proj\",\"cwd\":\"{Project}\",\"ts\":\"{Stamp(DateTimeOffset.UtcNow)}\"}}\n");
        ScriptGit();
        AskRelease("20261016-010000-cccc");
        Answer("20261016-010000-cccc", StatusSection(ReadyLine()));

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--plan", "--team", "proj"]);
        Assert.True(code == 0, stderr);
        var row = JsonText.Parse(stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray().Single(s => s.Member("member")!.Value.AsString() == Impl);
        Assert.Equal("ready", row.Member("status")!.Value.AsString());
    }

    [Fact]
    public void ANamelessPeerWithTwoRowsForOneSessionIsReadAtTheNewestCwd()
    {
        var elsewhere = Path.Combine(Path.GetTempPath(), "no-such-work-tree-" + Guid.NewGuid().ToString("N"));
        WriteTeam("proj", LiveTeamJson("tmux", string.Empty));
        string Row(string cwd, DateTimeOffset at) =>
            $"{{\"type\":\"peer\",\"status\":\"up\",\"role\":\"implementor\",\"session_id\":\"sess-impl\",\"pid\":{LivePidOfAnotherProcess()},\"pane_id\":\"%1\",\"team\":\"proj\",\"cwd\":\"{cwd}\",\"ts\":\"{Stamp(at)}\"}}\n";
        _sandbox.PlantPeers(Hierarchy, Row(elsewhere, DateTimeOffset.UtcNow.AddMinutes(-5)) + Row(Project, DateTimeOffset.UtcNow));
        ScriptGit();
        AskRelease("20261016-010000-dddd", Synth);
        Answer("20261016-010000-dddd", StatusSection(ReadyLine()));

        var (status, _) = PlanOfNamelessPeer();

        Assert.Equal("ready", status);
    }

    [Fact]
    public void AnIdleCleanMemberOnABranchWithNoReleaseRequestClosesWithoutOne()
    {
        _branch = "ah/feature";
        ArrangeImplementor();

        var (_, status, detail, _) = PlanOfImplementor();
        var (code, _, stderr) = CloseImplementor();

        Assert.Equal("ready", status);
        Assert.Equal("idle-clean", detail);
        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void AMemberWorkingNowIsHeldAndNothingCloses()
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (_, status, _, _) = PlanOfImplementor();
        var (code, _, _) = CloseImplementor();

        Assert.Equal("busy", status);
        Assert.Equal(3, code);
        Assert.False(Killed("%1"));
    }

    [Theory]
    [InlineData("dirty")]
    [InlineData("open-request")]
    public void AnIdleMemberWithSomethingLeftBehindClosesAndTheOutputWarns(string why)
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        if (why == "dirty")
        {
            _porcelain = " M src/a.cs\n";
        }
        else
        {
            AskRelease("20261016-010000-cccc", slug: "work");
        }

        var (_, status, _, plan) = PlanOfImplementor();
        var (code, stdout, stderr) = CloseImplementor();

        Assert.Equal(why, status);
        Assert.Equal(why, Assert.Single(plan.Member("release")!.Value.Member("warnings")!.Value.AsArray()).Member("status")!.Value.AsString());
        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.Contains(why, JsonText.Parse(stdout).Member("release")!.Value.Member("summary")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberWithUnshippedCommitsClosesAndTheOutputNamesTheBranchAndSha()
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        var scripted = _runner.Output;
        _runner.Output = argv => argv.Contains("rev-list") ? "2\n" : scripted!(argv);

        var (code, stdout, stderr) = CloseImplementor();

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        var warning = Assert.Single(JsonText.Parse(stdout).Member("release")!.Value.Member("warnings")!.Value.AsArray());
        Assert.Equal("ahead", warning.Member("status")!.Value.AsString());
        Assert.Contains($"2 commit(s) in {Project}", warning.Member("detail")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Contains($"ah/feature at {_head}", warning.Member("detail")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdleCleanMemberClosesWithNoWarnings()
    {
        ArrangeImplementor();

        var (code, stdout, stderr) = CloseImplementor();

        Assert.True(code == 0, stdout + stderr);
        Assert.False(JsonText.Parse(stdout).Member("release")!.Value.AsObject().ContainsKey("warnings"));
    }

    [Theory]
    [InlineData("clean")]
    [InlineData("dirty")]
    [InlineData("busy")]
    [InlineData("open-request")]
    public void TheDismissNudgeCheckReturnsOnlyAnIdleCleanMember(string why)
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        if (why == "dirty")
        {
            _porcelain = " M src/a.cs\n";
        }
        else if (why == "busy")
        {
            Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        }
        else if (why == "open-request")
        {
            AskRelease("20261016-010000-cccc", slug: "work");
        }

        var ready = Ah.Commands.CommandsModule.ReadyToDismiss(ComposeServices(), Hierarchy, Project, "proj", [Impl]);

        Assert.Equal(why == "clean" ? new[] { Impl } : [], ready);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void TheDismissNudgeCheckLeavesOutAMemberTheReleaseCheckPassesButABareDismissWouldNotCloseAsClean()
    {
        ArrangeCleanImplementor(streamState: "open");
        Assert.Equal("ready", PlanOfImplementor().Status);

        var ready = Ah.Commands.CommandsModule.ReadyToDismiss(ComposeServices(), Hierarchy, Project, "proj", [Impl]);

        Assert.Empty(ready);
    }

    /// <summary>A herdr team of two idle clean implementors and a third whose stream is not done, every pane live in herdr.</summary>
    private string[] ArrangeHerdrTeamOfThree()
    {
        string[] names = [Impl, "proj-implementor-2", "proj-implementor-3"];
        var members = string.Join(",", Member("implementor", names[0], "%1"), Member("implementor", names[1], "%2"), CleanMember(names[2], "%3", "s1"));
        WriteTeam("proj", LiveTeamJson("herdr", members).TrimEnd('}') + ",\"streams\":{\"s1\":{\"state\":\"open\"}}}");
        for (var i = 0; i < names.Length; i++)
        {
            PeerUp(names[i], $"%{i + 1}", session: $"sess-{i + 1}");
        }

        _branch = "ah/feature";
        ScriptGit();
        var git = _runner.Output;
        var agents = string.Join(",", names.Select((n, i) => $"{{\"name\":\"{n}\",\"pane_id\":\"%{i + 1}\",\"tab_id\":\"t1\",\"workspace_id\":\"w1\"}}"));
        _runner.Output = argv => argv.Contains("agent") && argv.Contains("list") ? $"{{\"result\":{{\"agents\":[{agents}]}}}}" : git!(argv);
        return names;
    }

    [Fact]
    public void TheDismissNudgeCheckOfSeveralIdleMembersNamesTheCleanOnesAndQueriesHerdrOnce()
    {
        var names = ArrangeHerdrTeamOfThree();

        var ready = Ah.Commands.CommandsModule.ReadyToDismiss(ComposeServices(), Hierarchy, Project, "proj", names);

        Assert.Equal(names[..2], ready);
        Assert.Equal(1, _runner.Calls.Count(argv => argv.Contains("agent") && argv.Contains("list")));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("close"));
    }

    [Fact]
    public void TheDismissNudgeCheckNamesNobodyWhenTheMessageStoreCannotBeRead()
    {
        var names = ArrangeHerdrTeamOfThree();
        BreakMessageStore();

        var ready = Ah.Commands.CommandsModule.ReadyToDismiss(ComposeServices(), Hierarchy, Project, "proj", names);

        Assert.Empty(ready);
    }

    [Fact]
    public void TheBareCloseTheDismissNudgePrintsClosesAnIdleCleanMemberInOneCall()
    {
        _branch = "ah/feature";
        ArrangeImplementor();

        var (code, _, stderr) = Roster(["dismiss", Impl]);

        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void AnOpenRequestToAnotherMemberDoesNotHoldThisOne()
    {
        ArrangeImplementor();
        AskRelease("20261016-010000-aaaa", member: "proj-reviewer");

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("ready", status);
    }

    // ---------------------------------------------------------------- the tree against the world

    [Fact]
    public void PermissionsDiagnoseFindsTheOwnedTeamOfTheMemberItNamesLikeDismissDoes()
    {
        ArrangeImplementor();
        WriteTeam("two", LiveTeamJson("tmux", Member("implementor", "two-implementor", "%2")));
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, stdout, stderr) = Roster("permissions", "diagnose", "two-implementor", "--cwd", Project, "--orchestrator-pid", Self);

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("two-implementor (team two)", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("not in this team", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PermissionsDiagnoseOfANameNoOwnedTeamHoldsStillAsksForTheTeam()
    {
        ArrangeImplementor();
        WriteTeam("two", LiveTeamJson("tmux", Member("implementor", "two-implementor", "%2")));
        _callers ??= new RecordingCallers { StartText = SelfStart };

        var (code, _, stderr) = Roster("permissions", "diagnose", "nobody", "--cwd", Project, "--orchestrator-pid", Self);

        Assert.NotEqual(0, code);
        Assert.Contains("pass --team <name> to say which", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AStatusListingPastTheOutputCapReadsAsADirtyTreeNotAsAFailureToReadIt()
    {
        ArrangeImplementor();
        _porcelain = "?? scratch/a\n";
        _runner.Truncated = argv => argv.Contains("status");

        var (_, status, detail, _) = PlanOfImplementor();

        Assert.Equal("dirty", status);
        Assert.StartsWith("uncommitted work in " + Project, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("git could not read", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberIsStaleWhenGitCannotReadTheTree()
    {
        ArrangeImplementor();
        _runner.Exit = argv => argv.Contains("status") ? 128 : 0;

        var (_, status, detail, _) = PlanOfImplementor();

        Assert.Equal("stale", status);
        Assert.StartsWith("git could not read the work tree", detail, StringComparison.Ordinal);
    }

    private void ActivityRecord(string file, string json)
    {
        _sandbox.PlantActivity(Hierarchy, Path.GetFileNameWithoutExtension(file), json);
    }

    private (string Status, string Detail) ReleaseWithRecord(string extraFields)
    {
        ArrangeImplementor();
        ActivityRecord("sess-impl.json", $"{{\"activity\":\"idle\",\"at\":\"{Stamp(DateTimeOffset.UtcNow.AddMinutes(-5))}\"{extraFields}}}");
        var (_, status, detail, _) = PlanOfImplementor();
        return (status, detail);
    }

    private static string Ago(int minutes) => $"\"{Stamp(DateTimeOffset.UtcNow.AddMinutes(-minutes))}\"";

    [Fact]
    public void AnIdleMemberWithABackgroundTaskOpenIsHeldAndOneWithNoneIsReleased()
    {
        var (waiting, waitingDetail) = ReleaseWithRecord(",\"bg_open\":1");
        var (none, _) = ReleaseWithRecord(",\"bg_open\":0");
        var (lost, _) = ReleaseWithRecord($",\"bg_open\":1,\"state_since\":{Ago(61)}");

        Assert.Equal("busy", waiting);
        Assert.Equal("busy now", waitingDetail);
        Assert.Equal("ready", none);
        Assert.Equal("ready", lost);
    }

    [Fact]
    public void AnIdleMemberAfterACompactionIsNotHeldWhateverTheOrderOfItsStamps()
    {
        var (noStateSince, _) = ReleaseWithRecord($",\"working_at\":{Ago(40)},\"compacted_at\":{Ago(30)}");
        var (soon, _) = ReleaseWithRecord($",\"working_at\":{Ago(40)},\"compacted_at\":{Ago(1)}");
        var (worked, _) = ReleaseWithRecord($",\"working_at\":{Ago(20)},\"compacted_at\":{Ago(30)}");

        Assert.Equal("ready", noStateSince);
        Assert.Equal("ready", soon);
        Assert.Equal("ready", worked);
    }

    [Fact]
    public void AMemberCompactedWhileIdleOrThatWentIdleAfterTheCompactionIsNotHeld()
    {
        var (whileIdle, _) = ReleaseWithRecord($",\"working_at\":{Ago(40)},\"state_since\":{Ago(39)},\"compacted_at\":{Ago(30)}");
        var (afterIt, _) = ReleaseWithRecord($",\"working_at\":{Ago(40)},\"compacted_at\":{Ago(30)},\"state_since\":{Ago(29)}");

        Assert.Equal("ready", whileIdle);
        Assert.Equal("ready", afterIt);
    }

    [Fact]
    public void AWorkingMemberWhoseLivenessCannotBeDecidedIsHeldAndAnIdleOneWarns()
    {
        ArrangeImplementor(live: false);
        ReplacePeerRows($"{{\"type\":\"peer\",\"status\":\"seen\",\"name\":\"{Impl}\",\"role\":\"implementor\",\"session_id\":\"sess-seen\",\"team\":\"proj\",\"ts\":\"{Stamp(DateTimeOffset.UtcNow.AddHours(-2))}\"}}");
        var (_, idle, _, _) = PlanOfImplementor();
        Activity("sess-seen.json", "working", DateTimeOffset.UtcNow);

        var (_, working, _, _) = PlanOfImplementor();
        var (code, _, _) = CloseImplementor();

        Assert.Equal("liveness-unknown", idle);
        Assert.Equal("busy", working);
        Assert.Equal(3, code);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void IgnoreReleaseNamingAWorkingMemberClosesItWithAWarningAndNamingAnotherDoesNot()
    {
        ArrangeImplementor();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (otherCode, _, _) = CloseImplementor("--ignore-release", "someone-else");
        Assert.Equal(3, otherCode);
        Assert.False(Killed("%1"));

        var (code, stdout, stderr) = CloseImplementor("--ignore-release", Impl);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        var warning = Assert.Single(JsonText.Parse(stdout).Member("release")!.Value.Member("warnings")!.Value.AsArray());
        Assert.Equal("ignored", warning.Member("status")!.Value.AsString());
        Assert.Contains("busy now", warning.Member("detail")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABareIgnoreReleaseOverridesNoHold()
    {
        ArrangeImplementor();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (code, _, _) = CloseImplementor("--ignore-release");

        Assert.Equal(3, code);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void DisbandTakesTheOverrideByNameAndHoldsAWorkingMemberWithoutIt()
    {
        ArrangeImplementor();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (heldCode, _, _) = Roster(["disband", "--close", "--confirm", "--team", "proj"]);
        Assert.Equal(3, heldCode);
        Assert.False(Killed("%1"));

        var (code, stdout, stderr) = Roster(["disband", "--close", "--confirm", "--ignore-release", Impl, "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.Contains("\"ignored\"", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void ACommitAheadWhoseBranchCannotBeReadSaysUnknownNotABlank()
    {
        ArrangeImplementor();
        var scripted = _runner.Output;
        _runner.Output = argv => argv.Contains("rev-list") ? "1\n" : argv.Contains("--abbrev-ref") ? string.Empty : scripted!(argv);
        _runner.Exit = argv => argv.Contains("--abbrev-ref") ? 128 : 0;

        var (_, status, detail, _) = PlanOfImplementor();

        Assert.Equal("ahead", status);
        Assert.EndsWith($": unknown at {_head}", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeadMemberWithCommitsAheadClosesAndTheWarningNamesThem()
    {
        _branch = "ah/feature";
        ArrangeImplementor(live: false);
        var scripted = _runner.Output;
        _runner.Output = argv => argv.Contains("rev-list") ? "2\n" : scripted!(argv);

        var (code, stdout, stderr) = CloseImplementor();

        Assert.True(code == 0, stdout + stderr);
        var warning = Assert.Single(JsonText.Parse(stdout).Member("release")!.Value.Member("warnings")!.Value.AsArray());
        Assert.Equal("not-running", warning.Member("status")!.Value.AsString());
        Assert.Contains($"2 commit(s) in {Project}", warning.Member("detail")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Contains($"ah/feature at {_head}", warning.Member("detail")!.Value.AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheResolvedReleaseRefCountsAsReleasedNotAnyReleaseBranch()
    {
        ArrangeImplementor();
        var scripted = _runner.Output;
        _runner.Output = argv => !argv.Contains("rev-list") ? scripted!(argv)
            : argv.Any(a => a.StartsWith("--branches", StringComparison.Ordinal) || a.StartsWith("--remotes", StringComparison.Ordinal)) ? "0\n" : "1\n";
        var (_, onSomeReleaseBranch, _, _) = PlanOfImplementor();
        _runner.Output = argv => !argv.Contains("rev-list") ? scripted!(argv) : argv.Contains("origin/main") ? "0\n" : "1\n";
        var (_, onTheReleaseRef, _, _) = PlanOfImplementor();

        Assert.Equal("ahead", onSomeReleaseBranch);
        Assert.Equal("ready", onTheReleaseRef);
    }

    [Fact]
    public void ALiveMemberWhoseRecordSaysWorkingOrBlockedIsHeldWhateverTheTimeOfTheRecord()
    {
        ArrangeImplementor();

        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow.AddMinutes(-5));
        var (_, working, workingDetail, _) = PlanOfImplementor();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow.AddMinutes(-60));
        var (_, earlierWorking, _, _) = PlanOfImplementor();
        Activity("sess-impl.json", "blocked", DateTimeOffset.UtcNow.AddMinutes(-60));
        var (_, earlierBlocked, _, _) = PlanOfImplementor();
        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow.AddMinutes(-5));
        var (_, idle, _, _) = PlanOfImplementor();
        Activity("sess-impl.json", "failed", DateTimeOffset.UtcNow.AddMinutes(-5));
        var (_, failed, _, _) = PlanOfImplementor();

        Assert.Equal("busy", working);
        Assert.Equal("busy now", workingDetail);
        Assert.Equal("busy", earlierWorking);
        Assert.Equal("busy", earlierBlocked);
        Assert.Equal("ready", idle);
        Assert.Equal("ready", failed);
    }

    [Fact]
    public void ARegisteredMemberTheRegistrationDoesNotProveDeadIsHeldAsLivenessUnknownAndNeverNotRunning()
    {
        ArrangeImplementor(live: false);
        _registrations = new() { [Impl + "@proj"] = new Ah.Hierarchy.MemberRegistrationLiveness(ImplSession, false, "registration pid 1 has no readable command line and no recorded start time to show it is the session") };

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("liveness-unknown", status);
    }

    [Fact]
    public void ARealRegistrationOfAProcessThatLooksUnlikeTheHarnessIsHeldAsLivenessUnknownThroughTheRealLiveness()
    {
        ArrangeImplementor(live: false);
        var pid = LivePidOfAnotherProcess();
        _seedGraph = graph => Assert.True(graph.Mailbox.WriteRegistration(new RegistrationWrite(
            ImplSession,
            [new BoxRef(Impl + "@proj", graph.Mailbox.BoxDirFor(TeamPath("proj"), Impl))],
            TeamFile: TeamPath("proj"),
            Harness: "claude",
            Pid: pid)));

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("liveness-unknown", status);
    }

    private void ReplacePeerRows(string rows)
    {
        _sandbox.ClearPeers(Hierarchy);
        _sandbox.PlantPeers(Hierarchy, rows);
    }

    [Fact]
    public void AnUnregisteredMemberWithNoRowIsHeldAsLivenessUnknownNeverClosedAsGone()
    {
        ArrangeImplementor(live: false);
        _sandbox.ClearPeers(Hierarchy);

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("liveness-unknown", status);
    }

    [Fact]
    public void AnUnregisteredMemberWhoseOnlyRowIsAStaleSeenRowIsHeldAsLivenessUnknown()
    {
        ArrangeImplementor(live: false);
        ReplacePeerRows($"{{\"type\":\"peer\",\"status\":\"seen\",\"name\":\"{Impl}\",\"role\":\"implementor\",\"session_id\":\"s\",\"team\":\"proj\",\"ts\":\"{DateTimeOffset.UtcNow.AddHours(-2):yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"}}\n");

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("liveness-unknown", status);
    }

    [Fact]
    public void AnUnregisteredMemberWhoseRowIsDownButNamesNoProcessIsHeldAsLivenessUnknown()
    {
        ArrangeImplementor(live: false);
        ReplacePeerRows($"{{\"type\":\"peer\",\"status\":\"down\",\"name\":\"{Impl}\",\"role\":\"implementor\",\"session_id\":\"s\",\"team\":\"proj\",\"ts\":\"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"}}\n");

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("liveness-unknown", status);
    }

    [Fact]
    public void NegativeAnUnregisteredMemberWhoseRowIsDownAndNamesAnExitedProcessIsNotRunning()
    {
        ArrangeImplementor(live: false);
        ReplacePeerRows(string.Empty);
        AppendGoneRow(Impl, "proj");

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("not-running", status);
    }

    [Fact]
    public void NegativeARegisteredMemberTheRegistrationProvesDeadIsNotRunning()
    {
        ArrangeImplementor(live: false);
        _registrations = new() { [Impl + "@proj"] = new Ah.Hierarchy.MemberRegistrationLiveness(ImplSession, false, "registration pid 1 now belongs to another process (its start time differs)", ProvenDead: true) };

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("not-running", status);
    }

    [Fact]
    public void ADeadMemberWithAWorkingRecordIsNotHeldByTheBusyCheck()
    {
        WriteTeam("proj", LiveTeamJson("tmux", OneImplementor.Replace("\"model\"", "\"session_id\":\"sess-impl\",\"model\"", StringComparison.Ordinal)));
        ScriptGit();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow.AddMinutes(-5));

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("not-running", status);
    }

    // ---------------------------------------------------------------- a member that is not running

    [Fact]
    public void ADeadMemberClosesWhetherItsTreeIsCleanOrDirty()
    {
        ArrangeImplementor(live: false);

        var (_, clean, _, _) = PlanOfImplementor();
        var (cleanCode, _, cleanErr) = CloseImplementor();
        Assert.True(cleanCode == 0, cleanErr);
        Assert.True(Killed("%1"));

        ArrangeImplementor(live: false);
        _porcelain = "?? scratch.txt\n";
        _runner.Calls.Clear();
        var (_, dirty, _, _) = PlanOfImplementor();
        var (dirtyCode, _, dirtyErr) = CloseImplementor();

        Assert.Equal("not-running", clean);
        Assert.Equal("not-running-dirty", dirty);
        Assert.True(dirtyCode == 0, dirtyErr);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void ADeadMemberWhoseTreeGitCannotReadIsHeldAndOneWithALiveRowIsNotTreatedAsDead()
    {
        ArrangeImplementor(live: false);
        _runner.Exit = argv => argv.Contains("status") ? 128 : 0;

        var (_, unreadable, detail, _) = PlanOfImplementor();
        PeerUp(Impl, "%1");
        var (_, live, _, _) = PlanOfImplementor();

        Assert.Equal("stale", unreadable);
        Assert.StartsWith("git could not read the work tree", detail, StringComparison.Ordinal);
        Assert.Equal("stale", live);
    }

    // ---------------------------------------------------------------- disband

    [Fact]
    public void DisbandClosesTheReadyMembersAndHoldsTheRestWithExitThree()
    {
        var members = OneImplementor + "," + Member("reviewer", "proj-reviewer", "%2");
        WriteTeam("proj", LiveTeamJson("tmux", members));
        PeerUp(Impl, "%1");
        PeerUp("proj-reviewer", "%2", session: "sess-rev");
        ScriptGit();
        Activity("sess-rev.json", "working", DateTimeOffset.UtcNow);

        var plan = Roster("disband", "--team", "proj");
        var token = TokenOf(plan.Stdout);
        var (code, stdout, stderr) = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.Equal(0, plan.Code);
        Assert.True(Killed("%1"));
        Assert.False(Killed("%2"));
        Assert.Equal(3, code);
        var result = JsonText.Parse(stdout);
        Assert.False(result.Member("closed")!.Value.AsBool());
        var held = Assert.Single(result.Member("held")!.Value.AsArray());
        Assert.Equal("proj-reviewer", held.Member("member")!.Value.AsString());
        Assert.Equal("busy", held.Member("status")!.Value.AsString());
        Assert.StartsWith("release: 1 may close, 1 held: proj-reviewer busy", result.Member("release")!.Value.Member("summary")!.Value.AsString(), StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(stderr) || !stderr.Contains("error", StringComparison.OrdinalIgnoreCase), stderr);
    }

    [Fact]
    public void DisbandWithEveryMemberReadyClosesAllAndExitsZero()
    {
        var members = OneImplementor + "," + Member("reviewer", "proj-reviewer", "%2");
        WriteTeam("proj", LiveTeamJson("tmux", members));
        PeerUp(Impl, "%1");
        PeerUp("proj-reviewer", "%2", session: "sess-rev");
        ScriptGit();
        AskRelease("20261016-010000-aaaa");
        Answer("20261016-010000-aaaa", StatusSection(ReadyLine()));
        AskRelease("20261016-010000-bbbb", member: "proj-reviewer");
        Answer("20261016-010000-bbbb", StatusSection(ReadyLine()));

        var token = TokenOf(Roster("disband", "--team", "proj").Stdout);
        var (code, stdout, stderr) = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", "proj");

        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
        Assert.True(Killed("%2"));
        Assert.True(JsonText.Parse(stdout).Member("closed")!.Value.AsBool());
    }

    [Fact]
    public void DisbandPlanShowsEveryStatusAndClosesNothing()
    {
        ArrangeImplementor();

        var (code, stdout, _) = Roster("disband", "--team", "proj");

        Assert.Equal(0, code);
        var statuses = JsonText.Parse(stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray();
        Assert.Equal("ready", Assert.Single(statuses).Member("status")!.Value.AsString());
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("kill-pane"));
        Assert.True(_sandbox.TeamExists(TeamPath("proj")));
    }

    // ---------------------------------------------------------------- --ignore-release

    [Fact]
    public void IgnoreReleaseStillParsesAndChangesNothing()
    {
        ArrangeImplementor();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (heldCode, heldOut, heldErr) = CloseImplementor("--ignore-release");

        Assert.True(heldCode == 3, heldOut + heldErr);
        Assert.False(Killed("%1"));

        Activity("sess-impl.json", "idle", DateTimeOffset.UtcNow.AddMinutes(-5));
        var (code, stdout, stderr) = CloseImplementor("--ignore-release");

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.False(JsonText.Parse(stdout).Member("release")!.Value.AsObject().ContainsKey("ignored"));
    }

    // ---------------------------------------------------------------- members with no team row

    [Fact]
    public void AMemberWithNoTeamRowIsCheckedInTheWorkTreeOfItsLatestPeersCwd()
    {
        PeerUp("solo-implementor", "%5");
        ScriptGit();

        var plan = Roster("disband");
        _porcelain = " M src/a.cs\n";
        var dirty = Roster("disband");

        var first = JsonText.Parse(plan.Stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray()[0];
        var second = JsonText.Parse(dirty.Stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray()[0];
        Assert.Equal("ready", first.Member("status")!.Value.AsString());
        Assert.Equal("dirty", second.Member("status")!.Value.AsString());
        Assert.Contains(_runner.Calls, argv => argv.Contains("-C") && argv.Contains(Project) && argv.Contains("status"));
    }

    [Fact]
    public void AMemberWithNoTeamRowAndNoCwdOnItsPeersRowIsReadyForWantOfAWorkTree()
    {
        PeerUp("solo-implementor", "%5", withCwd: false);
        ScriptGit();

        var (code, stdout, _) = Roster("disband");

        Assert.Equal(0, code);
        var row = JsonText.Parse(stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray()[0];
        Assert.Equal("ready", row.Member("status")!.Value.AsString());
        Assert.Equal("idle, no work tree", row.Member("detail")!.Value.AsString());
    }

    [Fact]
    public void AMemberWithNoTeamRowIsCheckedInTheCwdOfItsNewestPeersRowNotAnOlderOne()
    {
        var elsewhere = Path.Combine(_sandbox.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        PeerUp("solo-implementor", "%5", at: Stamp(DateTimeOffset.UtcNow.AddHours(-2)), cwdPath: elsewhere, session: "sess-old");
        PeerUp("solo-implementor", "%6", at: Stamp(DateTimeOffset.UtcNow), session: "sess-new");
        ScriptGit();
        AskRelease("20261016-010000-aaaa", member: "solo-implementor");
        Answer("20261016-010000-aaaa", StatusSection(ReadyLine()));

        var (_, stdout, _) = Roster("disband");

        var row = JsonText.Parse(stdout).Member("release")!.Value.Member("statuses")!.Value.AsArray()[0];
        Assert.Equal("ready", row.Member("status")!.Value.AsString());
    }

    [Fact]
    public void TheTreeIsReadWithEveryUntrackedFileCountedAndWithoutTheCallersGitEnvironment()
    {
        ArrangeImplementor();
        AskRelease("20261016-010000-aaaa");
        Answer("20261016-010000-aaaa", StatusSection(ReadyLine()));

        PlanOfImplementor();

        var reads = _runner.Calls.Where(a => a.Contains("git") && (a.Contains("status") || (a.Contains("rev-parse") && a.Contains("HEAD")))).ToList();
        Assert.True(reads.Count >= 3, reads.Count.ToString());
        foreach (var argv in reads)
        {
            Assert.Equal(["env", "-u", "GIT_DIR", "-u", "GIT_WORK_TREE", "-u", "GIT_INDEX_FILE", "-u", "GIT_COMMON_DIR", "-u", "GIT_CONFIG_PARAMETERS", "-u", "GIT_CONFIG_COUNT", "git", "-C", Project], argv.Take(16));
        }

        var statuses = reads.Where(a => a.Contains("status")).ToList();
        Assert.NotEmpty(statuses);
        Assert.All(statuses, status =>
        {
            Assert.Contains("--untracked-files=all", status);
            Assert.Contains("--ignore-submodules=none", status);
        });
    }

    private static Ah.Abstractions.IProcessRunner RealRunner() =>
        OperatingSystem.IsWindows() ? throw new PlatformNotSupportedException() : Ah.Platform.PlatformModule.CreateProcessRunner();

    private string RealGit(params string[] args)
    {
        var result = RealRunner().Run(new Ah.Abstractions.ProcessRequest(
            ["git", "-C", Project, "-c", "user.name=t", "-c", "user.email=t@example.test", "-c", "commit.gpgsign=false", .. args], Timeout: TimeSpan.FromSeconds(30)));
        Assert.True(result.ExitCode == 0, string.Join(' ', args));
        return System.Text.Encoding.UTF8.GetString(result.Stdout.Span).Trim();
    }

    [Fact]
    public void AnUntrackedFileMakesTheTreeDirtyEvenWhenTheRepoHidesUntrackedFiles()
    {
        ArrangeImplementor();
        Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        RealGit("init", "-q");
        File.WriteAllText(Path.Combine(Project, "a.txt"), "a");
        RealGit("add", "a.txt");
        RealGit("commit", "-q", "-m", "one");
        RealGit("config", "status.showUntrackedFiles", "no");
        _runner.GitPassthrough = RealRunner();

        var (_, clean, _, _) = PlanOfImplementor();
        File.WriteAllText(Path.Combine(Project, "new.cs"), "class X {}");
        var (_, dirty, dirtyDetail, _) = PlanOfImplementor();

        Assert.Equal("ready", clean);
        Assert.Equal("dirty", dirty);
        Assert.StartsWith("uncommitted work in ", dirtyDetail, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberWhoseLivenessCannotBeDecidedIsHeldAsLivenessUnknownNeverClosedAsGone()
    {
        var herdrKind = OneImplementor.Replace("\"model\"", "\"kind\":\"codex\",\"model\"", StringComparison.Ordinal);
        WriteTeam("proj", LiveTeamJson("tmux", herdrKind));
        ReplacePeerRows($"{{\"type\":\"peer\",\"status\":\"down\",\"name\":\"{Impl}\",\"role\":\"implementor\",\"session_id\":\"s\",\"team\":\"proj\",\"ts\":\"{DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\"}}\n");
        ScriptGit();

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("liveness-unknown", status);
    }

    [Fact]
    public void NegativeAMemberOfAnotherKindWhoseDownRowNamesAnExitedProcessIsNotRunning()
    {
        var herdrKind = OneImplementor.Replace("\"model\"", "\"kind\":\"codex\",\"model\"", StringComparison.Ordinal);
        WriteTeam("proj", LiveTeamJson("tmux", herdrKind));
        ScriptGit();

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("not-running", status);
    }

    // ---------------------------------------------------------------- a request filed from the member's own work tree

    private string LinkedTree => Path.Combine(_sandbox.Root, "linked");

    private string TreeHierarchy => Path.Combine(LinkedTree, ".claude", "ah2");

    /// <summary>
    /// The orchestrator's pool is <c>proj/.claude/ah2</c> (the test's hierarchy directory behind a link, with no directory
    /// override) and the member works in the linked work tree <c>linked</c>, whose own pool is <c>linked/.claude/ah2</c>.
    /// </summary>
    private void ArrangeLinkedImplementor()
    {
        _extraEnv["WRANGL_HIERARCHY_DIR"] = string.Empty;
        Directory.CreateDirectory(Path.Combine(Project, ".claude"));
        Directory.CreateSymbolicLink(Path.Combine(Project, ".claude", "ah2"), Hierarchy);
        Directory.CreateDirectory(TreeHierarchy);
        File.WriteAllText(Path.Combine(LinkedTree, ".git"), $"gitdir: {Project}/.git/worktrees/linked\n");
        var member = Member("implementor", Impl, "%1").Replace(Project, LinkedTree, StringComparison.Ordinal);
        WriteTeam("proj", LiveTeamJson("tmux", member));
        PeerUp(Impl, "%1", cwdPath: LinkedTree);
        ScriptGit();
    }

    [Fact]
    public void ARequestAndItsResponseFiledInTheMembersWorkTreeAreFoundAndCloseTheMember()
    {
        ArrangeLinkedImplementor();
        AskRelease("20261016-010000-aaaa", hier: TreeHierarchy);
        Answer("20261016-010000-aaaa", StatusSection(ReadyLine()), hier: TreeHierarchy);

        var (_, status, detail, _) = PlanOfImplementor();

        Assert.Equal("ready", status);
        Assert.Equal("idle-clean", detail);
        Assert.Contains(_runner.Calls, argv => argv.Contains("-C") && argv.Contains(LinkedTree) && argv.Contains("status"));
    }

    [Fact]
    public void ARequestInTheMembersWorkTreeWithNoResponseHoldsAsOpenRequest()
    {
        ArrangeLinkedImplementor();
        AskRelease("20261016-010000-aaaa", hier: TreeHierarchy);

        var (_, status, detail, _) = PlanOfImplementor();

        Assert.Equal("open-request", status);
        Assert.Equal("request 20261016-010000-aaaa has no response yet", detail);
    }

    [Fact]
    public void ARequestFiledInAnotherWorkTreeOrForAnotherMemberIsNotThisMembersRequest()
    {
        ArrangeLinkedImplementor();
        var other = Path.Combine(_sandbox.Root, "other", ".claude", "ah2");
        AskRelease("20261016-010000-aaaa", hier: other);
        AskRelease("20261016-010000-bbbb", member: "proj-someone-else", hier: TreeHierarchy);

        var (_, status, _, _) = PlanOfImplementor();

        Assert.Equal("ready", status);
    }

    [Fact]
    public void AnIdleCleanMemberClosesInOneCallWithNoPlanToken()
    {
        _branch = "ah/feature";
        ArrangeImplementor();

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--close", "--confirm", "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.Empty(JsonText.Parse(stdout).Member("held")!.Value.AsArray());
    }

    [Fact]
    public void ADirtyTreeClosesAndTheWarningNamesTheTree()
    {
        _branch = "ah/feature";
        ArrangeImplementor();
        _porcelain = " M src/a.cs\n";

        var (code, stdout, stderr) = Roster(["dismiss", Impl, "--close", "--confirm", "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        var warning = Assert.Single(JsonText.Parse(stdout).Member("release")!.Value.Member("warnings")!.Value.AsArray());
        Assert.Equal("dirty", warning.Member("status")!.Value.AsString());
        Assert.Equal("uncommitted work in " + Project, warning.Member("detail")!.Value.AsString());
    }
}
