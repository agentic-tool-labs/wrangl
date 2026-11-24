using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The clean-close token of <c>dismiss</c>: minted only for a clean member of a run the user handed over, re-derived at the close.</summary>
public sealed partial class RosterTeamVerbTests
{
    private const string OrchSession = "sess-orch";

    private void CloseAnchor(string id) =>
        Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(Hierarchy, "msgs", id + "--orchestrator--pipeline-run-anchor--response.md"),
            $"---\nid: {id}\ntype: response\nto: orchestrator\nfrom: orchestrator\nslug: pipeline-run-anchor\nteam: proj\ncreated: {DateTimeOffset.UtcNow:yyyy-MM-dd'T'HH:mm:ss.fff'Z'}\n---\n\n## [1] status\n- done\n");

    private const string CleanAnchorId = "20261016-020000-anch";

    private string _origin = string.Empty;

    private static string StampNow(int minutesAgo) =>
        DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>An open run anchor of team proj with the given <c>authority:</c> line; <paramref name="answered"/> records the user's surrogate answer.</summary>
    private void CleanAnchor(string authority = "surrogate", bool answered = true, string id = CleanAnchorId)
    {
        var msgs = Path.Combine(Hierarchy, "msgs");
        Directory.CreateDirectory(msgs);
        _ = Ah.TestKit.TestMsgStore.Plant(
            _sandbox.AhHome,
            Path.Combine(msgs, id + "--orchestrator--pipeline-run-anchor--request.md"),
            $"---\nid: {id}\ntype: request\nto: orchestrator\nfrom: orchestrator\nslug: pipeline-run-anchor\nteam: proj\ncreated: {StampNow(3)}\n---\n\n## constraints\n   authority: {authority}\n");
        if (answered)
        {
            var question = "Who decides during this run?";
            var row = JsonText.Stringify(
                new JsonObject
                {
                    ["at"] = StampNow(5),
                    ["session_id"] = OrchSession,
                    ["questions"] = new JsonArray { new JsonObject { ["header"] = "Authority", ["question"] = question } },
                    ["response"] = new JsonObject { ["answers"] = new JsonObject { [question] = "Orchestrator acts for me (this run)" } },
                },
                0);
            Directory.CreateDirectory(Hierarchy);
            File.AppendAllText(Path.Combine(Hierarchy, "asks.jsonl"), row + "\n");
        }
    }

    private string CleanMember(string name, string pane, string? stream) =>
        Member("implementor", name, pane).TrimEnd('}') + (stream is null ? string.Empty : $",\"stream\":\"{stream}\"") + "}";

    private void WriteCleanTeam(string members, string streamState = "done") =>
        WriteTeam(
            "proj",
            LiveTeamJson("tmux", members).Replace("\"session_id\":null", $"\"session_id\":\"{OrchSession}\"", StringComparison.Ordinal).TrimEnd('}')
            + ",\"streams\":{\"s1\":{\"state\":\"" + streamState + "\"}}}");

    /// <summary>A real repository at the project with an <c>origin</c> remote, whose HEAD is pushed to <paramref name="pushedTo"/> (nothing pushed when null).</summary>
    private string ArrangeCleanRepo(string? pushedTo = "refs/heads/main", bool withOrigin = true)
    {
        Directory.CreateDirectory(Project);
        if (Directory.Exists(Path.Combine(Project, ".git")))
        {
            Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        }

        RealGit("init", "-q");
        File.WriteAllText(Path.Combine(Project, "a.txt"), "a");
        RealGit("add", "a.txt");
        RealGit("commit", "-q", "-m", "one");
        if (withOrigin)
        {
            _origin = Path.Combine(_sandbox.Root, "origin.git");
            RealGit("init", "-q", "--bare", _origin);
            RealGit("remote", "add", "origin", _origin);
            if (pushedTo is not null)
            {
                RealGit("push", "-q", "origin", "HEAD:" + pushedTo);
            }
        }

        _runner.GitPassthrough = RealRunner();
        return RealGit("rev-parse", "HEAD");
    }

    /// <summary>One live implementor of stream s1 that answered ready at the repository's HEAD, in a run whose anchor says <paramref name="authority"/>.</summary>
    private void ArrangeCleanImplementor(string streamState = "done", string authority = "surrogate", bool answered = true, string? pushedTo = "refs/heads/main", bool withOrigin = true)
    {
        WriteCleanTeam(CleanMember(Impl, "%1", "s1"), streamState);
        PeerUp(Impl, "%1");
        var head = ArrangeCleanRepo(pushedTo, withOrigin);
        AskRelease("20261016-010000-aaaa");
        Answer("20261016-010000-aaaa", StatusSection(ReadyLine(head)));
        CleanAnchor(authority, answered);
    }

    private (int Code, JsonValue Out, string Stderr) PlanOne(string name = Impl)
    {
        var (code, stdout, stderr) = Roster(["dismiss", name, "--plan", "--team", "proj"]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout) : JsonValue.Null, stderr);
    }

    private (int Code, string Stdout, string Stderr) CloseOne(string token, string name = Impl, params string[] extra) =>
        Roster(["dismiss", name, "--close", "--confirm", "--plan-token", token, .. extra, "--team", "proj"]);

    private static string CleanReasons(JsonValue plan) =>
        string.Join(" | ", plan.Member("clean_close")!.Value.Member("reasons")!.Value.AsArray().Select(r => r.AsString()));

    private static bool Minted(JsonValue plan) => plan.Member("clean_close")!.Value.Member("minted")!.Value.AsBool();

    [Fact]
    public void CT1AReleasedIdleUnqueuedDoneAndShippedMemberOfASurrogateRunGetsACleanTokenThatCloses()
    {
        ArrangeCleanImplementor();

        var (code, plan, stderr) = PlanOne();
        var token = TokenOf(plan);
        var (closeCode, stdout, closeErr) = CloseOne(token);

        Assert.True(code == 0, stderr);
        Assert.True(Minted(plan), CleanReasons(plan));
        Assert.Matches("^cc1-[0-9a-f]{16}$", token);
        Assert.Contains("--plan-token " + token, plan.Member("next")!.Value.AsString(), StringComparison.Ordinal);
        Assert.True(closeCode == 0, closeErr + stdout);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void CT2AnAnchorThatSaysUserStillGetsACleanTokenThatCloses()
    {
        ArrangeCleanImplementor(authority: "user");

        var (_, plan, _) = PlanOne();
        var token = TokenOf(plan);
        var (code, _, stderr) = CloseOne(token);

        Assert.True(Minted(plan), CleanReasons(plan));
        Assert.Matches("^cc1-[0-9a-f]{16}$", token);
        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void CT3NoAnchorStillGetsACleanToken()
    {
        ArrangeCleanImplementor();
        CloseAnchor(CleanAnchorId);

        var (_, none, _) = PlanOne();

        Assert.True(Minted(none), CleanReasons(none));
        Assert.Matches("^cc1-[0-9a-f]{16}$", TokenOf(none));
    }

    [Fact]
    public void CT3NegativeAnUnfinishedStreamUnderUserAuthorityGetsAnOrdinaryToken()
    {
        ArrangeCleanImplementor(streamState: "open", authority: "user");

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Matches("^[0-9a-f]{16}$", TokenOf(plan));
    }

    [Fact]
    public void CT5AMemberThatBecameBusyAfterThePlanIsHeldAndNothingCloses()
    {
        ArrangeCleanImplementor();
        var token = TokenOf(PlanOne().Out);
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (code, _, _) = CloseOne(token);

        Assert.NotEqual(0, code);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void CT6AStreamThatIsNotDoneGivesAnOrdinaryTokenNamingIt()
    {
        ArrangeCleanImplementor(streamState: "open");

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Matches("^[0-9a-f]{16}$", TokenOf(plan));
        Assert.Contains($"{Impl}: stream s1 is not done", CleanReasons(plan), StringComparison.Ordinal);
    }

    /// <summary>The release status of a member whose own work tree holds one commit past the run's branch, pushed to <paramref name="pushedTo"/>.</summary>
    private string StatusOfACommitPushedTo(string? pushedTo, string? releaseRef = null, bool empty = false)
    {
        ArrangeCleanImplementor();
        var tree = Path.Combine(_sandbox.Root, "member-tree");
        RealGit("worktree", "add", "-q", "-b", "member-branch", tree);
        if (!empty)
        {
            File.WriteAllText(Path.Combine(tree, "member.txt"), "work");
            RealGit("-C", tree, "add", "member.txt");
        }

        RealGit("-C", tree, "commit", "-q", "--allow-empty", "-m", "member work");
        if (pushedTo is not null)
        {
            RealGit("-C", tree, "push", "-q", "origin", "HEAD:" + pushedTo);
        }

        if (releaseRef is not null)
        {
            RealGit("config", "ah.releaseRef", releaseRef);
        }

        WriteCleanTeam(CleanMember(Impl, "%1", "s1").TrimEnd('}') + $",\"expected_root\":\"{tree}\"}}");
        var (_, plan, _) = PlanOne();
        return plan.Member("release")!.Value.Member("statuses")!.Value.AsArray().Single(s => s.Member("member")!.Value.AsString() == Impl).Member("status")!.Value.AsString();
    }

    [Fact]
    public void ACommitOnAReleaseBranchIsNotAheadThoughTheDefaultBranchLacksIt() =>
        Assert.NotEqual("ahead", StatusOfACommitPushedTo("refs/heads/ah/release-2.5.0"));

    [Fact]
    public void B372AnEmptyWipResumeCommitIsNotAhead() =>
        Assert.NotEqual("ahead", StatusOfACommitPushedTo(null, empty: true));

    [Fact]
    public void NegativeACommitOnAnotherBranchIsStillAhead() =>
        Assert.Equal("ahead", StatusOfACommitPushedTo("refs/heads/ah/releases"));

    [Fact]
    public void NegativeWithAReleaseRefSetAReleaseBranchDoesNotCount() =>
        Assert.Equal("ahead", StatusOfACommitPushedTo("refs/heads/ah/release-2.5.0", "origin/main"));

    /// <summary>The plan for a live member whose own branch holds one commit, merged --no-ff into a pushed train when <paramref name="merged"/>.</summary>
    private JsonValue PlanOfAMemberCommit(bool merged)
    {
        ArrangeCleanImplementor();
        var tree = Path.Combine(_sandbox.Root, "member-tree");
        RealGit("worktree", "add", "-q", "-b", "member-branch", tree);
        File.WriteAllText(Path.Combine(tree, "member.txt"), "work");
        RealGit("-C", tree, "add", "member.txt");
        RealGit("-C", tree, "commit", "-q", "-m", "member work");
        if (merged)
        {
            RealGit("checkout", "-q", "-b", "train");
            RealGit("merge", "-q", "--no-ff", "-m", "ship: merge member-branch", "member-branch");
            RealGit("push", "-q", "origin", "HEAD:refs/heads/ah/train-4d");
            RealGit("checkout", "-q", "-");
        }

        WriteCleanTeam(CleanMember(Impl, "%1", "s1").TrimEnd('}') + $",\"expected_root\":\"{tree}\"}}");
        return PlanOne().Out;
    }

    [Fact]
    public void B450AMemberBranchShippedByANoFfMergeIntoThePushedTrainGetsACleanToken()
    {
        var plan = PlanOfAMemberCommit(merged: true);

        Assert.True(Minted(plan), CleanReasons(plan));
    }

    [Fact]
    public void B450NegativeAnUnmergedMemberCommitIsStillUnshipped()
    {
        var plan = PlanOfAMemberCommit(merged: false);

        Assert.False(Minted(plan));
        Assert.Contains("unshipped commits (1 not on origin)", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void InALocalOnlyRunAnUnmergedMemberCommitIsANoteAndTheTokenIsMinted()
    {
        _localOnly = true;

        var plan = PlanOfAMemberCommit(merged: false);

        Assert.True(Minted(plan), CleanReasons(plan));
        var notes = plan.Member("clean_close")!.Value.Member("notes")!.Value.AsArray().Select(n => n.AsString()).ToList();
        Assert.Contains(notes, n => n.Contains("unshipped commits (1 not on origin), kept: the run is local-only", StringComparison.Ordinal));
    }

    [Fact]
    public void CT6ACommitNotOnOriginGivesAnOrdinaryTokenNamingTheUnshippedCommits()
    {
        ArrangeCleanImplementor();
        File.WriteAllText(Path.Combine(Project, "b.txt"), "b");
        RealGit("add", "b.txt");
        RealGit("commit", "-q", "-m", "two");
        var head = RealGit("rev-parse", "HEAD");
        AskRelease("20261016-040000-cccc");
        Answer("20261016-040000-cccc", StatusSection(ReadyLine(head)));

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Contains("unshipped commits (1 not on origin)", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void ALiveMemberTheReleaseCheckHoldsIsReportedAsHeldNotAsNothingToClose()
    {
        ArrangeCleanImplementor();
        AskRelease("20261016-040000-dddd");

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Contains($"{Impl}: release ", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberWithNoPaneStillHasNothingToClose()
    {
        WriteCleanTeam(CleanMember(Impl, "%1", "s1").Replace("\"transport_id\":\"%1\",", string.Empty, StringComparison.Ordinal));
        ArrangeCleanRepo();

        var (_, plan, _) = PlanOne();

        Assert.Equal("nothing to close", CleanReasons(plan));
    }

    [Fact]
    public void CT6ADirtyTreeIsHeldByTheReleaseCheckAndGetsNoCleanToken()
    {
        ArrangeCleanImplementor();
        File.WriteAllText(Path.Combine(Project, "new.cs"), "class X {}");

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.DoesNotMatch("^cc1-", TokenOf(plan));
    }

    [Fact]
    public void CT9TheAuthorityAnswerIsNotConsultedForACleanToken()
    {
        ArrangeCleanImplementor(answered: false);

        var (_, plan, _) = PlanOne();

        Assert.True(Minted(plan), CleanReasons(plan));
        Assert.Matches("^cc1-[0-9a-f]{16}$", TokenOf(plan));
    }

    [Fact]
    public void CT11NegativeOnlyAMemberTheOverrideActuallyCarriesLosesTheCleanToken()
    {
        ArrangeCleanImplementor(authority: "user");
        var bare = JsonText.Parse(Roster(["dismiss", Impl, "--plan", "--ignore-release", "--team", "proj"]).Stdout);
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);
        var named = JsonText.Parse(Roster(["dismiss", Impl, "--plan", "--ignore-release", Impl, "--team", "proj"]).Stdout);
        var token = TokenOf(named);

        var (code, _, stderr) = CloseOne(token, Impl, "--ignore-release", Impl);

        Assert.StartsWith("cc1-", TokenOf(bare), StringComparison.Ordinal);
        Assert.False(Minted(named));
        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
    }

    [Fact]
    public void CT13EveryMemberHeldGivesNoCleanTokenAndTheCloseReachesNothingMayClose()
    {
        ArrangeCleanImplementor();
        Activity("sess-impl.json", "working", DateTimeOffset.UtcNow);

        var (_, plan, _) = PlanOne();
        var (code, stdout, _) = CloseOne(TokenOf(plan));

        Assert.False(Minted(plan));
        Assert.Equal(3, code);
        Assert.False(JsonText.Parse(stdout).Member("closed")!.Value.AsBool());
        Assert.False(Killed("%1"));
    }

    private (int Code, JsonValue Out, string Stderr) PlanDisband()
    {
        var (code, stdout, stderr) = Roster(["disband", "--team", "proj"]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout) : JsonValue.Null, stderr);
    }

    [Theory]
    [InlineData("surrogate")]
    [InlineData("user")]
    public void CT21AReleaseCleanTeamDisbandGetsACleanTokenThatCloses(string authority)
    {
        ArrangeCleanImplementor(authority: authority);

        var (_, plan, planErr) = PlanDisband();
        var token = TokenOf(plan);
        var (code, _, stderr) = Roster(["disband", "--close", "--confirm", "--plan-token", token, "--team", "proj"]);

        Assert.True(Minted(plan), CleanReasons(plan) + planErr);
        Assert.Matches("^cc1-[0-9a-f]{16}$", token);
        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
    }

    /// <summary>ah2-54 D10: the plan carries the retro text naming the team; the close does not.</summary>
    [Fact]
    public void ADisbandPlanCarriesTheRetroAndTheCloseDoesNot()
    {
        ArrangeCleanImplementor();

        var (_, plan, _) = PlanDisband();
        Assert.Contains("ah lessons list --team proj", plan.Member("retro")!.Value.AsString(), StringComparison.Ordinal);
        Assert.StartsWith("Retro, optional and never blocking", plan.Member("retro")!.Value.AsString(), StringComparison.Ordinal);

        var (_, stdout, _) = Roster(["disband", "--close", "--confirm", "--plan-token", TokenOf(plan), "--team", "proj"]);
        Assert.DoesNotContain("retro", stdout, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ah lessons", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void CT21NegativeADisbandWhoseMemberHasAStreamNotDoneGetsAnOrdinaryToken()
    {
        ArrangeCleanImplementor(streamState: "open");

        var (_, plan, _) = PlanDisband();

        Assert.False(Minted(plan));
        Assert.Matches("^[0-9a-f]{16}$", TokenOf(plan));
        Assert.Contains("stream s1 is not done", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void CT15OneUncleanMemberOfSeveralGivesOneOrdinaryTokenNamingIt()
    {
        var names = new[] { "proj-a", "proj-b", "proj-c" };
        WriteCleanTeam(string.Join(",", names.Select((n, i) => CleanMember(n, $"%{i + 1}", n == "proj-b" ? "s2" : "s1"))));
        ArrangeCleanRepo();
        CleanAnchor();

        var (code, plan, stderr) = PlanMany(names);

        Assert.True(code == 0, stderr);
        Assert.False(Minted(plan));
        Assert.Matches("^[0-9a-f]{16}$", TokenOf(plan));
        Assert.Contains("proj-b: stream s2 is not done", CleanReasons(plan), StringComparison.Ordinal);
        Assert.DoesNotContain("proj-a:", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void CT15AllCleanMembersOfSeveralGetOneCleanTokenThatClosesThem()
    {
        var names = new[] { "proj-a", "proj-b" };
        WriteCleanTeam(string.Join(",", names.Select((n, i) => CleanMember(n, $"%{i + 1}", "s1"))));
        ArrangeCleanRepo();
        CleanAnchor();

        var (_, plan, _) = PlanMany(names);
        var (code, _, stderr) = CloseMany(TokenOf(plan), names);

        Assert.True(Minted(plan), CleanReasons(plan));
        Assert.Matches("^cc1-[0-9a-f]{16}$", TokenOf(plan));
        Assert.True(code == 0, stderr);
        Assert.True(Killed("%1"));
        Assert.True(Killed("%2"));
    }

    [Theory]
    [InlineData("refs/heads/ah/wip/x")]
    [InlineData("refs/heads/ah/wip/t/m/abc123def456")]
    public void CT17CommitsOnlyOnAWipBackupRefAreNotShipped(string backupRef)
    {
        ArrangeCleanImplementor(pushedTo: backupRef);

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Contains("unshipped commits", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void CT17NegativeACommitOnANonWipRefThatSharesTheWipPrefixWordsIsShipped()
    {
        ArrangeCleanImplementor(pushedTo: "refs/heads/ah/wipe/t/m/abc123def456");

        var (_, plan, _) = PlanOne();

        Assert.True(Minted(plan), CleanReasons(plan));
    }

    [Fact]
    public void B1ARepeatedPlanTokenIsRefusedAndClosesNothing()
    {
        ArrangeCleanImplementor();
        var token = TokenOf(PlanOne().Out);

        var (code, _, stderr) = Roster(["dismiss", Impl, "--close", "--confirm", "--plan-token", "cc1-0000000000000000", "--plan-token", token, "--team", "proj"]);
        var (sameCode, _, _) = Roster(["dismiss", Impl, "--close", "--confirm", "--plan-token", token, "--plan-token", token, "--team", "proj"]);

        Assert.NotEqual(0, code);
        Assert.Contains("give --plan-token once", stderr, StringComparison.Ordinal);
        Assert.NotEqual(0, sameCode);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void R2ARepeatedFlagIsRefusedByDismissAndDisbandAndClosesNothing()
    {
        ArrangeCleanImplementor();
        var token = TokenOf(PlanOne().Out);

        var repeatedClose = Roster(["dismiss", Impl, "--close", "--close", "--confirm", "--plan-token", token, "--team", "proj"]);
        var repeatedConfirm = Roster(["dismiss", Impl, "--close", "--confirm", "--confirm", "--plan-token", token, "--team", "proj"]);
        var repeatedTeam = Roster(["dismiss", Impl, "--team", "proj", "--team", "proj"]);
        var disband = Roster(["disband", "--close", "--close", "--confirm", "--plan-token", token, "--team", "proj"]);

        Assert.NotEqual(0, repeatedClose.Code);
        Assert.Contains("give --close once", repeatedClose.Stderr, StringComparison.Ordinal);
        Assert.Contains("give --confirm once", repeatedConfirm.Stderr, StringComparison.Ordinal);
        Assert.Contains("give --team once", repeatedTeam.Stderr, StringComparison.Ordinal);
        Assert.Contains("give --close once", disband.Stderr, StringComparison.Ordinal);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void R2NegativeIgnoreReleaseStillRepeatsOncePerMemberItNames()
    {
        ArrangeCleanImplementor();

        var (code, _, stderr) = Roster(["dismiss", Impl, "--plan", "--ignore-release", "a", "--ignore-release", "b", "--team", "proj"]);

        Assert.True(code == 0, stderr);
    }

    [Fact]
    public void B1NegativeASingleTokenStillCloses()
    {
        ArrangeCleanImplementor();

        var (code, _, stderr) = CloseOne(TokenOf(PlanOne().Out));

        Assert.True(code == 0, stderr);
    }

    [Fact]
    public void S2AnOpenRequestToTheMembersRoleWithNoNameCountsAsQueued()
    {
        ArrangeCleanImplementor();
        var id = "20261016-030000-dddd";
        var path = MsgPath(id, "implementor", "brief", "request");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ = Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, path, $"---\nid: {id}\ntype: request\nto:implementor\nfrom: orchestrator\nslug: brief\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: proj\ncreated: 2026-10-16T01:00:00-04:00\n---\n\n## [0] tldr\nWork.\n");

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Contains($"{Impl}: open request {id}", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void S2NegativeAnOpenRequestToAnotherRoleWithNoNameIsNotQueuedForTheMember()
    {
        ArrangeCleanImplementor();
        var id = "20261016-030000-eeee";
        var path = MsgPath(id, "reviewer", "brief", "request");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _ = Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, path, $"---\nid: {id}\ntype: request\nto:reviewer\nfrom: orchestrator\nslug: brief\nparent: null\nreason: null\neta: null\nto_name: null\nfrom_name: null\nteam: proj\ncreated: 2026-10-16T01:00:00-04:00\n---\n\n## [0] tldr\nReview.\n");

        var (_, plan, _) = PlanOne();

        Assert.True(Minted(plan), CleanReasons(plan));
    }

    /// <summary>
    /// A request to the member answered by a <c>--part</c> report (the exchange stays open); <paramref name="acked"/> records its sender's ack
    /// now, and <paramref name="laterPart"/> adds a second part filed after that ack.
    /// </summary>
    private string PartReportedRequest(bool acked, bool laterPart = false)
    {
        var id = "20261016-030000-ffff";
        var request = MsgPath(id, "implementor", "brief", "request");
        Directory.CreateDirectory(Path.GetDirectoryName(request)!);
        _ = Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, request, $"---\nid: {id}\ntype: request\nto: implementor\nfrom: orchestrator\nslug: brief\nparent: null\nreason: null\neta: null\nto_name: {Impl}\nfrom_name: null\nteam: proj\ncreated: 2026-10-16T01:00:00-04:00\n---\n\n## [0] tldr\nWork.\n");
        _ = Ah.TestKit.TestMsgStore.Plant(_sandbox.AhHome, MsgPath(id, "orchestrator", "brief", "response"), $"---\nid: {id}\ntype: response\nto: orchestrator\nfrom: implementor\nslug: brief\npartial: true\nparts: {(laterPart ? 2 : 1)}\nteam: proj\ncreated: 2026-10-16T01:10:00-04:00\n---\n\n## part 1 · 2026-10-16T01:10:00-04:00\n- sha abc1234\n"
            + (laterPart ? $"\n## part 2 · {StampNow(-5)}\n- still working\n" : string.Empty));
        if (acked)
        {
            _seedGraph = graph => graph.Hierarchy.Peers.AppendOrchestratorAck(JsonValue.FromString(OrchSession), JsonValue.FromString(id), "proj");
        }

        return id;
    }

    [Fact]
    public void B430ARequestWithAReportItsSenderAckedIsNotQueued()
    {
        ArrangeCleanImplementor();
        _ = PartReportedRequest(acked: true);

        var (_, plan, _) = PlanOne();

        Assert.True(Minted(plan), CleanReasons(plan));
    }

    [Fact]
    public void B430NegativeAPartFiledAfterTheAckKeepsTheRequestOpen()
    {
        ArrangeCleanImplementor();
        var id = PartReportedRequest(acked: true, laterPart: true);

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Contains($"{Impl}: open request {id}", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void B430NegativeAnUnackedPartReportStillCountsAsAnOpenRequest()
    {
        ArrangeCleanImplementor();
        var id = PartReportedRequest(acked: false);

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Contains($"{Impl}: open request {id}", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void CT18WithNoOriginAHeadOnTheLocalMainLineIsClean()
    {
        ArrangeCleanImplementor(withOrigin: false);

        var (_, plan, _) = PlanOne();

        Assert.True(Minted(plan), CleanReasons(plan));
    }

    [Fact]
    public void CT18NegativeWithNoOriginACommitOffTheLocalMainLineIsUnshipped()
    {
        ArrangeCleanImplementor(withOrigin: false);
        var main = RealGit("rev-parse", "--abbrev-ref", "HEAD");
        RealGit("checkout", "-q", "-b", "ah/feature-x");
        File.WriteAllText(Path.Combine(Project, "b.txt"), "b");
        RealGit("add", "b.txt");
        RealGit("commit", "-q", "-m", "two");
        RealGit("checkout", "-q", main);
        var tree = Path.Combine(_sandbox.Root, "feature-tree");
        RealGit("worktree", "add", "-q", tree, "ah/feature-x");
        WriteCleanTeam(CleanMember(Impl, "%1", "s1").Replace($"\"expected_root\":\"{Project}\"", $"\"expected_root\":\"{tree}\"", StringComparison.Ordinal));

        var (_, plan, _) = PlanOne();

        Assert.False(Minted(plan));
        Assert.Contains($"unshipped commits (1 not on {main})", CleanReasons(plan), StringComparison.Ordinal);
    }

    [Fact]
    public void CT19AMemberTreeWhoseHeadWasShippedOntoTheShippedBranchIsClean()
    {
        WriteCleanTeam(CleanMember(Impl, "%1", "s1"));
        PeerUp(Impl, "%1");
        ArrangeCleanRepo();
        RealGit("checkout", "-q", "-b", "ah/ship/x");
        File.WriteAllText(Path.Combine(Project, "b.txt"), "b");
        RealGit("add", "b.txt");
        RealGit("commit", "-q", "-m", "two");
        RealGit("push", "-q", "origin", "ah/ship/x:main");
        var head = RealGit("rev-parse", "HEAD");
        AskRelease("20261016-010000-aaaa");
        Answer("20261016-010000-aaaa", StatusSection(ReadyLine(head)));
        CleanAnchor();

        var (_, plan, _) = PlanOne();

        Assert.True(Minted(plan), CleanReasons(plan));
    }
}
