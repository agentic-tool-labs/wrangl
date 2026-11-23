using Ah.Core.Json;

namespace Ah.Hooks.Tests;

/// <summary>The soft-gate channel, one row per rule: the bad call bounces once, the fixed call passes, and the bad call never loops.</summary>
public sealed partial class HooksHostTests
{
    private static Rig SoftRig(Dictionary<string, string>? env = null)
    {
        var rig = new Rig(env);
        rig.Writer.Executing = rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "gates").ToList();
        return rig;
    }

    private static List<JsonValue> SoftRows(Rig rig, string? outcome = null) =>
        [.. rig.Services.Hierarchy.Roster.ReadGates(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"))
            .Where(r => r.Member("type")?.NonEmptyText == "soft-gate" && (outcome is null || r.Member("outcome")?.NonEmptyText == outcome))];

    private static string AsSubagent(string eventJson, string agentId = "a-1")
    {
        var e = JsonText.Parse(eventJson).AsObject();
        e["agent_id"] = agentId;
        return JsonText.Stringify(e, 0);
    }

    private static string Chain(Rig rig, string eventJson) => Text(rig.Run("pretooluse-chain", eventJson));

    private static string Ask() => Event("AskUserQuestion", string.Empty, new JsonObject { ["questions"] = new JsonArray() });

    private static string WriteAs(string agentType, string path) =>
        Event("Write", agentType, new JsonObject { ["file_path"] = path, ["content"] = "x" });

    /// <summary>A bounce is a nudge: <c>[ah-nudge] NOT DONE:</c> and at most one <c>Fix:</c> line.</summary>
    private static void AssertTwoLineBounce(string reason)
    {
        Assert.StartsWith("[ah-nudge] NOT DONE: ", reason, StringComparison.Ordinal);
        var lines = reason.Split('\n');
        Assert.InRange(lines.Length, 1, 2);
        Assert.True(lines.Length == 1 || lines[1].StartsWith("Fix: ", StringComparison.Ordinal));
    }

    /// <summary>Bounces twice, then the third identical call passes as giveup: a hold rule never loops.</summary>
    private static void AssertHoldsThenGivesUp(Rig rig, string rule, string bad)
    {
        var first = rig.Run("pretooluse-chain", bad);
        Assert.True(IsDenied(first));
        AssertTwoLineBounce(Reason(first));
        Assert.Equal(rule, Assert.Single(SoftRows(rig, "bounce")).Member("rule")!.Value.AsString());
        Assert.True(IsDenied(rig.Run("pretooluse-chain", bad)));
        Assert.Equal(string.Empty, Chain(rig, bad));
        Assert.Equal(rule, Assert.Single(SoftRows(rig, "giveup")).Member("rule")!.Value.AsString());
    }

    /// <summary>Conduit never gives up: every identical repeat is denied and leaves its own bounce row.</summary>
    [Fact]
    public void ConduitBouncesARoleSubagentsQuestionAndNeverLoops()
    {
        var rig = SoftRig();
        var ask = AsSubagent(Ask());
        for (var i = 0; i < 3; i++)
        {
            var outcome = rig.Run("pretooluse-chain", ask);
            Assert.True(IsDenied(outcome));
            AssertTwoLineBounce(Reason(outcome));
        }

        Assert.Equal(3, SoftRows(rig, "bounce").Count(r => r.Member("rule")!.Value.AsString() == "conduit"));
        Assert.Empty(SoftRows(rig, "giveup"));
    }

    /// <summary>The soft-rules unit stays hard on <c>ConduitFires</c>, which is right only while Conduit is the first rule.</summary>
    [Fact]
    public void ConduitIsTheFirstSoftRule() =>
        Assert.Equal("Conduit", Ah.Hooks.Gates.SoftRules.Rows[0].Method.Name);

    public static TheoryData<string, string, bool, bool> ConduitCallers()
    {
        var data = new TheoryData<string, string, bool, bool>();
        foreach (var tool in new[] { "AskUserQuestion", "ExitPlanMode", "SendUserFile", "PushNotification" })
        {
            // tool, agent_type, member session (WRANGL_TEAM_FILE), subagent (agent_id)
            data.Add(tool, "wrangl:ux-engineer", true, false);
            data.Add(tool, "wrangl:architect", true, false);
            data.Add(tool, "wrangl:sub-orchestrator", true, false);
            data.Add(tool, "acme:ui-implementor", true, false);
            data.Add(tool, "general-purpose", false, true);
        }

        return data;
    }

    /// <summary>Under the classifier with no gate_mode, conduit's deny is not turned into advisory context, three times running.</summary>
    [Theory]
    [MemberData(nameof(ConduitCallers))]
    public void ConduitDeniesEveryNonLeadCallerUnderAutoAndNeverGivesUp(string tool, string agentType, bool member, bool subagent)
    {
        var rig = SoftRig(member ? new() { ["WRANGL_TEAM_FILE"] = "/x/team.json" } : null);
        var call = Event(tool, agentType, new JsonObject(), "auto");
        call = subagent ? AsSubagent(call) : call;
        for (var i = 0; i < 3; i++)
        {
            var outcome = rig.Run("pretooluse-chain", call);
            Assert.True(IsDenied(outcome), Text(outcome));
            Assert.Contains("You never reach the user.", Reason(outcome), StringComparison.Ordinal);
        }

        Assert.Equal(3, SoftRows(rig, "bounce").Count);
    }

    [Theory]
    [InlineData("AskUserQuestion")]
    [InlineData("ExitPlanMode")]
    [InlineData("SendUserFile")]
    [InlineData("PushNotification")]
    public void ConduitPassesTheLeadUnderAuto(string tool)
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, Event(tool, string.Empty, new JsonObject(), "auto")));
        Assert.Empty(SoftRows(rig));
    }

    /// <summary>Negative: another soft rule on a member under auto is still advisory context, not a deny.</summary>
    [Fact]
    public void AnotherSoftRuleStaysAdvisoryUnderAuto()
    {
        var rig = SoftRig(new() { ["WRANGL_TEAM_FILE"] = "/x/team.json" });
        var outcome = rig.Run("pretooluse-chain", Event("Bash", "wrangl:reviewer", new JsonObject { ["command"] = "grep -n x f" }, "auto"));
        Assert.DoesNotContain("permissionDecision", Text(outcome), StringComparison.Ordinal);
        Assert.Contains("ah advisory (pretooluse-soft-rules)", Text(outcome), StringComparison.Ordinal);
    }

    /// <summary>Negative: a member's tool outside the four is not touched by conduit under auto.</summary>
    [Fact]
    public void ConduitIgnoresAMembersOtherToolsUnderAuto()
    {
        var rig = SoftRig(new() { ["WRANGL_TEAM_FILE"] = "/x/team.json" });
        Assert.Equal(string.Empty, Chain(rig, Event("SendMessage", "wrangl:implementor", new JsonObject { ["to"] = "orchestrator", ["message"] = "x" }, "auto")));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void ConduitBouncesAPlainSubagentAndAMemberSession()
    {
        var rig = SoftRig();
        Assert.True(IsDenied(rig.Run("pretooluse-chain", AsSubagent(Event("AskUserQuestion", "general-purpose", new JsonObject())))));
        var member = SoftRig(new() { ["WRANGL_TEAM_FILE"] = "/x/team.json" });
        Assert.True(IsDenied(member.Run("pretooluse-chain", Ask())));
    }

    [Fact]
    public void ConduitPassesTheOrchestratorWithNoRow()
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, Ask()));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void ReviewerWriteBouncesASourceEditAndNeverLoops() =>
        AssertHoldsThenGivesUp(SoftRig(), "reviewer-write", WriteAs("wrangl:reviewer", "/w/src/Foo.cs"));

    private static string HierDir(Rig rig) => rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj");

    [Fact]
    public void SelfCareBouncesAReviewersGrepAndAChainAndNeverLoops()
    {
        AssertHoldsThenGivesUp(SoftRig(), "self-care", BashAs("wrangl:reviewer", "grep -n x f"));
        AssertHoldsThenGivesUp(SoftRig(), "self-care", BashAs("wrangl:reviewer", "ah msg list; grep x f"));
        AssertHoldsThenGivesUp(SoftRig(), "self-care", BashAs("wrangl:reviewer", "git -C /w add src/Foo.cs"));
    }

    [Fact]
    public void AnArchitectOrReviewerMayLogWorkButNotChangeTheRegistry()
    {
        var rig = SoftRig();
        foreach (var verb in new[] { "list", "show ah-1", "log ah-1", "mine", "record ah-1 --type design --title done", "attach ah-1 docs/specs/a.md", "add thing" })
        {
            Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:architect", "ah work " + verb)));
        }

        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:reviewer", "ah work record ah-1 --type review --title ok")));
        Assert.Empty(SoftRows(rig));

        // the negatives: what §6.6 leaves out stays out
        foreach (var verb in new[] { "set ah-1 --title x", "link ah-1 --blocks ah-2", "prefix ah", "export" })
        {
            AssertHoldsThenGivesUp(SoftRig(), "self-care", BashAs("wrangl:reviewer", "ah work " + verb));
        }
    }

    [Fact]
    public void SelfCarePassesASelfCareCallASpecCommitAndOtherRoles()
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:architect", "/home/u/.local/bin/ah msg new --type response --id 1 --cwd /w")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:architect", "git -C /w add docs/specs/a.md")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:architect", "git -C /w commit -m \"spec: a\" -- docs/specs/a.md")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:implementor", "grep -n x f")));
        Assert.Equal(string.Empty, Chain(rig, BashAs(string.Empty, "grep -n x f")));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void SelfCarePassesABriefReadAndNamesNoVerbThatDoesNotExist()
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:architect", "ah msg show 20261102-093333-1hxh --cwd /w")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:reviewer", "/home/u/.local/bin/ah msg show 20261102-093333-1hxh --section 1 --cwd /w")));
        Assert.Empty(SoftRows(rig));

        var wait = SoftRig();
        var bounce = Reason(wait.Run("pretooluse-chain", BashAs("wrangl:reviewer", "ah msg wait 1")));
        Assert.StartsWith("[ah-nudge] NOT DONE: ", bounce, StringComparison.Ordinal);
        Assert.Contains("index|show|send", bounce, StringComparison.Ordinal);
        Assert.DoesNotContain("wait", bounce, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfCareSendsAChainedBriefReadBackToItsOwnCallNotToAGopher()
    {
        var bounce = Reason(SoftRig().Run("pretooluse-chain", BashAs("wrangl:reviewer", "pwd; ah msg show 1")));

        Assert.Contains("as a call of its own", bounce, StringComparison.Ordinal);
        Assert.DoesNotContain("task-gopher", bounce, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeSelfCareStillDelegatesAChainWithNoBriefReadAndAGitShow()
    {
        var chain = Reason(SoftRig().Run("pretooluse-chain", BashAs("wrangl:reviewer", "ah msg list; grep x f")));
        var gitShow = Reason(SoftRig().Run("pretooluse-chain", BashAs("wrangl:reviewer", "git show HEAD; grep x f")));

        Assert.Contains("task-gopher", chain, StringComparison.Ordinal);
        Assert.Contains("task-gopher", gitShow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ah msg show 1 --out /w/f")]
    [InlineData("ah msg show 1 --out=/w/f")]
    [InlineData("ah msg show 1 --out /w/f --force")]
    public void SelfCareBouncesABriefReadThatWritesAFileAndDelegatesIt(string command)
    {
        var bounce = Reason(SoftRig().Run("pretooluse-chain", BashAs("wrangl:reviewer", command)));

        Assert.Contains("task-gopher", bounce, StringComparison.Ordinal);
        Assert.DoesNotContain("as a call of its own", bounce, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeSelfCarePassesAShowWhoseArgumentOnlyStartsLikeOut()
    {
        var rig = SoftRig();

        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:reviewer", "ah msg show 1 --section --outline")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:reviewer", "ah msg show 1 --section out")));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void SelfCarePassesMsgFillAndReadOnlyGitForAReviewerAndACommitMessageIsNotAPath()
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:reviewer", "ah msg fill --body-file /w/b.md")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:reviewer", "git -C /w diff HEAD~1")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:reviewer", "git log --oneline")));
        Assert.Equal(string.Empty, Chain(rig, BashAs("wrangl:architect", "git -C /w commit -am \"spec: a\"")));
        Assert.Empty(SoftRows(rig));

        AssertHoldsThenGivesUp(SoftRig(), "self-care", BashAs("wrangl:reviewer", "git -C /w push"));
        AssertHoldsThenGivesUp(SoftRig(), "self-care", BashAs("wrangl:reviewer", "git -C /w commit -am \"x\" src/Foo.cs"));
    }

    /// <summary>A rig whose session <c>sess-1</c> carries <paramref name="role"/> as its persisted role, as a role session does.</summary>
    private static Rig SessionRoleRig(string role)
    {
        var rig = SoftRig();
        Ah.TestKit.TestHomeDocs.Put(rig.Sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles, $"{{\"version\":1,\"sessions\":{{\"sess-1\":{{\"role\":\"{role}\"}}}}}}");
        return rig;
    }

    [Fact]
    public void SelfCareNudgesAnArchitectSessionOnceAndNeverDeniesIt()
    {
        var rig = SessionRoleRig("architect");
        var first = Chain(rig, BashAs(string.Empty, "cat foo"));
        Assert.DoesNotContain("permissionDecision", first, StringComparison.Ordinal);
        Assert.Contains(Ah.Hooks.Gates.SoftRules.ArchitectNudge, first, StringComparison.Ordinal);

        Assert.Equal(string.Empty, Chain(rig, BashAs(string.Empty, "cat foo")));
    }

    [Fact]
    public void NegativeSelfCareStillBouncesAReviewerSessionsOwnEcho() =>
        AssertHoldsThenGivesUp(SessionRoleRig("reviewer"), "self-care", BashAs(string.Empty, "echo ok"));

    [Fact]
    public void SelfCarePassesALegworkRunnerAnArchitectDispatched()
    {
        var rig = SessionRoleRig("architect");
        Assert.Equal(string.Empty, Chain(rig, BashAsSubagent("task-gopher:task-gopher", "git rev-parse HEAD")));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void NegativeSelfCareStillBouncesAReviewersSubagentThatIsNoLegworkRunner() =>
        AssertHoldsThenGivesUp(SessionRoleRig("reviewer"), "self-care", BashAsSubagent("general-purpose", "echo ok"));

    [Fact]
    public void SelfCarePassesAReviewerSessionsGitGrepAndAdrShow()
    {
        var rig = SessionRoleRig("reviewer");
        Assert.Equal(string.Empty, Chain(rig, BashAs(string.Empty, "git grep -n foo HEAD")));
        Assert.Equal(string.Empty, Chain(rig, BashAs(string.Empty, "ah adr show adr-1")));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void AMembersApprovalIsFoundBySessionIdFromTheRosterNotTheEnvironment()
    {
        var rig = SoftRig(new() { ["WRANGL_TEAM_FILE"] = "/x/team.json", ["AH_MEMBER_NAME"] = "wrong-name" });
        var sw = BashAs(string.Empty, "git switch x");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        UpRow(rig);
        rig.Services.Hierarchy.Roster.AppendGateRouted(HierDir(rig), new JsonObject { ["type"] = "soft-gate", ["rule"] = "worktree", ["outcome"] = "approve", ["member"] = "impl-1" });

        Assert.Equal(string.Empty, Chain(rig, sw));
        Assert.Equal("impl-1", Assert.Single(SoftRows(rig, "approved")).Member("member")!.Value.AsString());
    }

    [Fact]
    public void RosterSkillBouncesOnceThenTheRetryPassesAndALoadedSkillPassesUntilAReset()
    {
        var rig = SoftRig();
        var close = BashAs(string.Empty, "ah roster disband tm --close --cwd /w");

        Assert.True(IsDenied(rig.Run("pretooluse-chain", close)));
        Assert.Equal(string.Empty, Chain(rig, close));
        Assert.Single(SoftRows(rig, "retry-pass"));

        var loaded = SoftRig();
        Assert.Equal(string.Empty, Chain(loaded, Event("Skill", string.Empty, new JsonObject { ["skill"] = "wrangl:agent-team" })));
        Assert.Equal(string.Empty, Chain(loaded, close));
        loaded.Services.Hierarchy.Roster.AppendGateRouted(HierDir(loaded), new JsonObject { ["type"] = "soft-gate", ["outcome"] = "reset", ["session_id"] = "sess-1" });
        Assert.True(IsDenied(loaded.Run("pretooluse-chain", close)));
    }

    [Fact]
    public void RosterSkillPassesAPlanAReadAndASubagent()
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, BashAs(string.Empty, "ah roster disband tm --plan")));
        Assert.Equal(string.Empty, Chain(rig, BashAs(string.Empty, "ah roster status")));
        Assert.Equal(string.Empty, Chain(rig, AsSubagent(BashAs("general-purpose", "ah roster dismiss m --close"))));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void HerdrNameBouncesABadNameOnceAndPassesAGoodOne()
    {
        var rig = SoftRig();
        var bad = BashAs(string.Empty, "herdr agent start Bad_Name --pane p1 -- claude");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", bad)));
        Assert.Equal(string.Empty, Chain(rig, bad));
        Assert.Equal(string.Empty, Chain(rig, BashAs(string.Empty, "herdr agent start good-name --pane p1 -- claude")));
        Assert.Single(SoftRows(rig, "bounce"));
    }

    private static void WriteAnchor(Rig rig, string answer, string bullet = "")
    {
        var dir = HierDir(rig) + "/msgs";
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            $"{dir}/20261030-120000-ab12--orchestrator--pipeline-run-anchor--request.md",
            $"---\nid: 20261030-120000-ab12\ntype: request\nto: orchestrator\nfrom: orchestrator\nslug: pipeline-run-anchor\ncreated: 2026-10-30T12:00:00.000Z\n---\n## [0] tldr\n- run\n{bullet}ultra-advisor: {answer}\n");
    }

    [Fact]
    public void ABulletedNoAnchorLineBouncesAndABulletedYesPasses()
    {
        var no = SoftRig();
        WriteAnchor(no, "no", "- ");
        Assert.True(IsDenied(no.Run("pretooluse-chain", BashAs(string.Empty, "ah roster team spawn-one ultra-advisor"))));
        var yes = SoftRig();
        WriteAnchor(yes, "yes", "- ");
        Assert.Equal(string.Empty, Chain(yes, BashAs(string.Empty, "ah roster team spawn-one ultra-advisor")));
    }

    [Theory]
    [InlineData("no", true)]
    [InlineData("yes", false)] // negative
    public void UltraAdvisorReadsTheAnchorFromTheStoreWithNoFile(string answer, bool denied)
    {
        var rig = SoftRig();
        WriteAnchor(rig, answer);
        Ah.TestKit.TestMsgStore.DropFiles(rig.Sandbox.Root + "/ah", rig.Sandbox.Root);

        Assert.Equal(denied, IsDenied(rig.Run("pretooluse-chain", BashAs(string.Empty, "ah roster team spawn-one ultra-advisor --cwd /w"))));
    }

    [Fact]
    public void UltraAdvisorBouncesADispatchWhileTheAnchorSaysNoThenTheRetryPasses()
    {
        var rig = SoftRig();
        WriteAnchor(rig, "no");
        var spawn = BashAs(string.Empty, "ah roster team spawn-one ultra-advisor --cwd /w");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", spawn)));
        Assert.Equal(string.Empty, Chain(rig, spawn));
        Assert.Equal("ultra-advisor", Assert.Single(SoftRows(rig, "retry-pass")).Member("rule")!.Value.AsString());
    }

    [Fact]
    public void UltraAdvisorPassesWithAYesAnchorOrNone()
    {
        var none = SoftRig();
        Assert.Equal(string.Empty, Chain(none, BashAs(string.Empty, "ah roster team spawn-one ultra-advisor")));
        var yes = SoftRig();
        WriteAnchor(yes, "yes");
        Assert.Equal(string.Empty, Chain(yes, BashAs(string.Empty, "ah roster team spawn-one ultra-advisor")));
        Assert.Empty(SoftRows(none));
        Assert.Empty(SoftRows(yes));
    }

    private static Rig MemberSoftRig()
    {
        var rig = SoftRig(new() { ["WRANGL_TEAM_FILE"] = "/x/team.json" });
        UpRow(rig);
        return rig;
    }

    private static void UpRow(Rig rig)
    {
        Directory.CreateDirectory(HierDir(rig));
        rig.PutPeers(HierDir(rig), "{\"type\":\"peer\",\"status\":\"up\",\"session_id\":\"sess-1\",\"name\":\"impl-1\",\"role\":\"implementor\",\"ts\":\"" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + "\"}\n");
    }

    [Fact]
    public void WorktreeBouncesAMembersSwitchAndNeverLoops() =>
        AssertHoldsThenGivesUp(MemberSoftRig(), "worktree", BashAs(string.Empty, "git -C t switch feature"));

    /// <summary>b502: the forms the detector used to miss bounce like a plain switch; a read and a file restore still pass.</summary>
    [Theory]
    [InlineData("git -c core.x=1 switch feature", true)]
    [InlineData("git --work-tree=/w checkout feature", true)]
    [InlineData("git --work-tree /w checkout feature", true)]
    [InlineData("git --git-dir=/w/.git switch feature", true)]
    [InlineData("/usr/bin/git switch feature", true)]
    [InlineData("env FOO=1 git checkout feature", true)]
    [InlineData("env -u FOO /usr/bin/git -c a=b switch feature", true)]
    [InlineData("sh -c \"git switch feature\"", true)]
    [InlineData("bash -c 'cd t && git checkout feature'", true)]
    [InlineData("git -c core.x=1 status", false)]
    [InlineData("env FOO=1 git checkout -- file.txt", false)]
    public void WorktreeSeesASwitchThroughOptionsEnvAPathAndAShellString(string line, bool bounces)
    {
        var rig = MemberSoftRig();
        var call = BashAs(string.Empty, line);
        if (bounces)
        {
            AssertHoldsThenGivesUp(rig, "worktree", call);
        }
        else
        {
            Assert.Equal(string.Empty, Chain(rig, call));
        }
    }

    [Fact]
    public void AnApprovalLetsOneSwitchThroughAndTheNextBouncesAgain()
    {
        var rig = MemberSoftRig();
        var sw = BashAs(string.Empty, "git switch x");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        rig.Services.Hierarchy.Roster.AppendGateRouted(HierDir(rig), new JsonObject { ["type"] = "soft-gate", ["rule"] = "worktree", ["outcome"] = "approve", ["member"] = "impl-1" });

        Assert.Equal(string.Empty, Chain(rig, sw));
        Assert.Equal("impl-1", Assert.Single(SoftRows(rig, "approved")).Member("member")!.Value.AsString());
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
    }

    /// <summary>
    /// ah2-310: the up row SessionStart writes carries <c>team</c> and never <c>name</c>; the member's name is on its team row, stamped
    /// with the session id. The session is sess-1; <paramref name="sessionOfImpl1"/> other than sess-1 makes it impl-2's.
    /// </summary>
    private static Rig StartedMemberRig(string sessionOfImpl1 = "sess-1")
    {
        var rig = SoftRig(new() { ["WRANGL_TEAM_FILE"] = "/x/team.json" });
        AutoTrackTests.WriteTeam(rig, new JsonObject
        {
            ["members"] = new JsonArray
            {
                new JsonObject { ["name"] = "impl-1", ["role"] = "implementor", ["session_id"] = sessionOfImpl1 },
                new JsonObject { ["name"] = "impl-2", ["role"] = "implementor", ["session_id"] = sessionOfImpl1 == "sess-1" ? "sess-2" : "sess-1" },
            },
        });
        rig.PutPeers(HierDir(rig), "{\"type\":\"peer\",\"status\":\"up\",\"session_id\":\"sess-1\",\"team\":\"tm\",\"role\":\"implementor\",\"ts\":\"" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + "\"}\n");
        return rig;
    }

    private static void Approve(Rig rig, string member) =>
        rig.Services.Hierarchy.Roster.AppendGateRouted(HierDir(rig), new JsonObject { ["type"] = "soft-gate", ["rule"] = "worktree", ["outcome"] = "approve", ["member"] = member });

    [Fact]
    public void AnApprovalLiftsTheSwitchOfAMemberWhoseUpRowHasNoName()
    {
        var rig = StartedMemberRig();
        var sw = BashAs(string.Empty, "git switch -c b abc123");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        Approve(rig, "impl-1");

        Assert.Equal(string.Empty, Chain(rig, sw));
        Assert.Equal("impl-1", Assert.Single(SoftRows(rig, "approved")).Member("member")!.Value.AsString());
    }

    [Fact]
    public void NegativeWithNoApprovalTheSwitchIsStillRefused()
    {
        var rig = StartedMemberRig();
        var sw = BashAs(string.Empty, "git switch -c b abc123");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        Assert.Empty(SoftRows(rig, "approved"));
    }

    [Fact]
    public void NegativeAnotherMembersApprovalDoesNotLiftThisOne()
    {
        // this session (sess-1) is impl-2; only impl-1 was approved
        var rig = StartedMemberRig(sessionOfImpl1: "sess-2");
        var sw = BashAs(string.Empty, "git switch -c b abc123");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        Approve(rig, "impl-1");

        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        Assert.Empty(SoftRows(rig, "approved"));
    }

    private static Rig StartedReviewerRig()
    {
        var rig = StartedMemberRig();
        Ah.TestKit.TestHomeDocs.Put(rig.Sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles, "{\"version\":1,\"sessions\":{\"sess-1\":{\"role\":\"reviewer\"}}}");
        return rig;
    }

    [Fact]
    public void AWorktreeApprovalLiftsTheReviewersCloneAndTheBounceRowNamesTheMember()
    {
        var rig = StartedReviewerRig();
        var clone = BashAs(string.Empty, "git clone /w/repo /tmp/rv");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", clone)));
        Assert.Equal("impl-1", Assert.Single(SoftRows(rig, "bounce")).Member("member")!.Value.AsString());
        Approve(rig, "impl-1");

        Assert.Equal(string.Empty, Chain(rig, clone));
        Assert.Equal("worktree", Assert.Single(SoftRows(rig, "approved")).Member("rule")!.Value.AsString());
    }

    /// <summary>A4: the team file sits in another tree's hierarchy dir, named by the up row's <c>team_home</c>.</summary>
    [Fact]
    public void AnApprovalLiftsAMemberWhoseTeamHomeIsAnotherTree()
    {
        var rig = SoftRig(new() { ["WRANGL_TEAM_FILE"] = "/x/team.json" });
        var other = rig.Sandbox.Root + "/main";
        Directory.CreateDirectory(other + "/.git");
        Directory.CreateDirectory(other + "/.claude/ah2");
        File.WriteAllText(other + "/.claude/ah2/agent-hierarchy.json", """{"version":1}""");
        var home = rig.Services.Config.HierarchyDir(other);
        Assert.NotEqual(HierDir(rig), home);
        Directory.CreateDirectory(Path.GetDirectoryName(rig.Services.Roster.TeamPath(home, "tm"))!);
        File.WriteAllBytes(rig.Services.Roster.TeamPath(home, "tm"), JsonText.ToUtf8(new JsonObject
        {
            ["members"] = new JsonArray { new JsonObject { ["name"] = "impl-1", ["role"] = "implementor", ["session_id"] = "sess-1" } },
        }, 0));
        Directory.CreateDirectory(HierDir(rig));
        rig.PutPeers(HierDir(rig), "{\"type\":\"peer\",\"status\":\"up\",\"session_id\":\"sess-1\",\"team\":\"tm\",\"team_home\":\"" + home + "\",\"role\":\"implementor\",\"ts\":\"" + DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'") + "\"}\n");
        var sw = BashAs(string.Empty, "git switch -c b abc123");
        Assert.True(IsDenied(rig.Run("pretooluse-chain", sw)));
        Approve(rig, "impl-1");

        Assert.Equal(string.Empty, Chain(rig, sw));
    }

    [Theory]
    [InlineData("git clone -u /tmp/x /w/repo /tmp/rv")]
    [InlineData("git clone --upload-pack=/tmp/x /w/repo /tmp/rv")]
    [InlineData("git clone --template=/tmp/t /w/repo /tmp/rv")]
    [InlineData("git clone -c core.hooksPath=/tmp/h /w/repo /tmp/rv")]
    [InlineData("git clone --config core.hooksPath=/tmp/h /w/repo /tmp/rv")]
    [InlineData("git -c core.hooksPath=/tmp/h clone /w/repo /tmp/rv")]
    public void NegativeAWorktreeApprovalDoesNotLiftACloneWithAnOptionThatRunsCode(string line)
    {
        var rig = StartedReviewerRig();
        Approve(rig, "impl-1");

        Assert.True(IsDenied(rig.Run("pretooluse-chain", BashAs(string.Empty, line))));
        Assert.Empty(SoftRows(rig, "approved"));
    }

    [Fact]
    public void NegativeAWorktreeApprovalLiftsNoOtherSelfCareCall()
    {
        var rig = StartedReviewerRig();
        Approve(rig, "impl-1");

        Assert.True(IsDenied(rig.Run("pretooluse-chain", BashAs(string.Empty, "grep -n x f"))));
        Assert.True(IsDenied(rig.Run("pretooluse-chain", BashAs(string.Empty, "git clone /w/repo /tmp/rv && grep x f"))));
        Assert.Empty(SoftRows(rig, "approved"));
    }

    [Fact]
    public void WorktreePassesAPathRestoreAndTheOrchestrator()
    {
        var member = MemberSoftRig();
        Assert.Equal(string.Empty, Chain(member, BashAs(string.Empty, "git checkout -- file.cs")));
        Assert.Empty(SoftRows(member));
        var orchestrator = SoftRig();
        Assert.Equal(string.Empty, Chain(orchestrator, BashAs(string.Empty, "git switch x")));
        Assert.Equal(string.Empty, Chain(orchestrator, Event("EnterWorktree", string.Empty, new JsonObject())));
        Assert.Empty(SoftRows(orchestrator));
    }

    /// <summary>
    /// A reviewer session (or, with <paramref name="member"/>, an implementor member session) whose cwd <c>proj</c> is a checkout, with
    /// <c>link</c> pointing into it and <c>clone</c> a plain scratch clone outside it.
    /// </summary>
    private static Rig TreeRig(bool member = false, bool git = true, Dictionary<string, string>? env = null)
    {
        var rig = member ? MemberSoftRig() : SoftRig(env);
        if (!member)
        {
            Ah.TestKit.TestHomeDocs.Put(rig.Sandbox.Root + "/ah", Ah.TestKit.TestHomeDocs.SessionRoles, "{\"version\":1,\"sessions\":{\"sess-1\":{\"role\":\"reviewer\"}}}");
        }

        if (git)
        {
            Directory.CreateDirectory(rig.Sandbox.Root + "/proj/.git");
        }

        Directory.CreateSymbolicLink(rig.Sandbox.Root + "/link", rig.Sandbox.Root + "/proj");
        Directory.CreateDirectory(rig.Sandbox.Root + "/clone/.git");
        File.WriteAllText(rig.Sandbox.Root + "/clone/.git/config", "[core]\n\tbare = false\n[remote \"origin\"]\n\turl = /src\n");
        return rig;
    }

    private const string ScratchCheckout = "git -C {clone} checkout --detach abc";

    private static string Line(Rig rig, string template) =>
        template.Replace("{root}", rig.Sandbox.Root + "/proj", StringComparison.Ordinal)
            .Replace("{link}", rig.Sandbox.Root + "/link", StringComparison.Ordinal)
            .Replace("{clone}", rig.Sandbox.Root + "/clone", StringComparison.Ordinal);

    [Fact]
    public void WorktreePassesAReviewersLegworkRunnerCheckingOutAScratchClone()
    {
        var rig = TreeRig();
        Assert.Equal(string.Empty, Chain(rig, BashAsSubagent("task-gopher:task-gopher", Line(rig, ScratchCheckout))));
        Assert.Empty(SoftRows(rig));
    }

    [Theory]
    [InlineData("worktree")]
    [InlineData("include")]
    [InlineData("gitfile")]
    [InlineData("link")]
    [InlineData("noconfig")]
    [InlineData("commondir")]
    [InlineData("configworktree")]
    public void NegativeWorktreeStillBouncesAScratchCloneWhoseGitCouldWriteElsewhere(string shape)
    {
        var rig = TreeRig();
        var git = rig.Sandbox.Root + "/clone/.git";
        switch (shape)
        {
            case "worktree":
                File.AppendAllText(git + "/config", "[core]\n\tworktree = " + rig.Sandbox.Root + "/proj\n");
                break;
            case "include":
                File.AppendAllText(git + "/config", "[include]\n\tpath = /elsewhere/config\n");
                break;
            case "noconfig":
                File.Delete(git + "/config");
                break;
            case "commondir":
                File.WriteAllText(git + "/commondir", rig.Sandbox.Root + "/proj/.git\n");
                break;
            case "configworktree":
                File.WriteAllText(git + "/config.worktree", "[core]\n\tbare = false\n");
                break;
            default:
                Directory.Delete(git, recursive: true);
                if (shape == "gitfile")
                {
                    File.WriteAllText(git, "gitdir: " + rig.Sandbox.Root + "/proj/.git\n");
                }
                else
                {
                    Directory.CreateSymbolicLink(git, rig.Sandbox.Root + "/proj/.git");
                }

                break;
        }

        AssertHoldsThenGivesUp(rig, "worktree", BashAsSubagent("task-gopher:task-gopher", Line(rig, ScratchCheckout)));
    }

    [Theory]
    [InlineData("git -C {root} checkout main")]
    [InlineData("git checkout --detach abc")]
    [InlineData("git -C {link} checkout main")]
    [InlineData("git -C $TMPDIR/clone checkout main")]
    [InlineData(ScratchCheckout + " && git checkout main")]
    [InlineData("GIT_WORK_TREE={root} " + ScratchCheckout)]
    [InlineData("export GIT_WORK_TREE={root}; " + ScratchCheckout)]
    [InlineData("A=1 " + ScratchCheckout)]
    public void NegativeWorktreeStillBouncesALegworkRunnersCheckoutInTheTreeOrUnresolved(string command)
    {
        var rig = TreeRig();
        AssertHoldsThenGivesUp(rig, "worktree", BashAsSubagent("task-gopher:task-gopher", Line(rig, command)));
    }

    [Fact]
    public void NegativeWorktreeStillBouncesATreePathSpelledInAnotherCaseOnMacOs()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var rig = TreeRig();
        AssertHoldsThenGivesUp(rig, "worktree", BashAsSubagent("task-gopher:task-gopher", "git -C " + rig.Sandbox.Root + "/PROJ checkout main"));
    }

    [Fact]
    public void NegativeWorktreeStillBouncesAScratchCheckoutFromANonLegworkSubagentOrTheMemberItself()
    {
        var gp = TreeRig(member: true);
        AssertHoldsThenGivesUp(gp, "worktree", BashAsSubagent("general-purpose", Line(gp, ScratchCheckout)));
        var self = TreeRig(member: true);
        AssertHoldsThenGivesUp(self, "worktree", BashAs(string.Empty, Line(self, ScratchCheckout)));
    }

    [Fact]
    public void WorktreeFallsBackToTheExpectedRootAndBouncesWhenNoRootIsKnown()
    {
        var none = TreeRig(git: false);
        AssertHoldsThenGivesUp(none, "worktree", BashAsSubagent("task-gopher:task-gopher", Line(none, ScratchCheckout)));
        // With no checkout above the cwd, WRANGL_EXPECTED_ROOT is the tree: `/` holds the clone, /tmp/x does not.
        var all = TreeRig(git: false, env: new() { ["WRANGL_EXPECTED_ROOT"] = "/" });
        AssertHoldsThenGivesUp(all, "worktree", BashAsSubagent("task-gopher:task-gopher", Line(all, ScratchCheckout)));
        var rig = TreeRig(git: false, env: new() { ["WRANGL_EXPECTED_ROOT"] = "/tmp/x" });
        Assert.Equal(string.Empty, Chain(rig, BashAsSubagent("task-gopher:task-gopher", Line(rig, ScratchCheckout))));
        Assert.Empty(SoftRows(rig));
    }

    private static string Dispatch(string subagentType, string prompt) =>
        Event("Agent", string.Empty, new JsonObject { ["subagent_type"] = subagentType, ["prompt"] = prompt, ["description"] = "d" });

    [Fact]
    public void ALegworkDispatchPassesWithNoRow()
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, Dispatch("wrangl:task-runner", "run the suite")));
        Assert.Empty(SoftRows(rig));
    }

    [Theory]
    [InlineData("wrangl:qa")]
    [InlineData("wrangl:backlog-groomer")]
    public void AQaOrLegworkSubagentDispatchGetsNoRouteOrMsgNudge(string subagentType)
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, Dispatch(subagentType, "run the full suite at abc1234")));
        Assert.Empty(SoftRows(rig));
    }

    [Fact]
    public void ReviewerWritePassesAnImplementor()
    {
        var rig = SoftRig();
        Assert.Equal(string.Empty, Chain(rig, WriteAs("wrangl:implementor", "/w/src/Foo.cs")));
        Assert.Empty(SoftRows(rig));
    }

    /// <summary>ah2-562: the guard keys on class review, not the name; a custom implement-class role is the nearest non-match.</summary>
    [Fact]
    public void ReviewerWriteBouncesACustomReviewClassRoleButNotACustomImplementor()
    {
        var rig = SoftRig();
        Directory.CreateDirectory(rig.Sandbox.Root + "/proj/.claude/ah2");
        File.WriteAllText(rig.Sandbox.Root + "/proj/.claude/ah2/agent-hierarchy.json",
            """{"version":1,"roles":{"critic":{"class":"review","agent":"acme:critic"},"builder":{"class":"implement","agent":"acme:builder"}}}""");

        var first = rig.Run("pretooluse-chain", WriteAs("acme:critic", "/w/src/Foo.cs"));
        Assert.True(IsDenied(first));
        Assert.Contains("--from critic", Reason(first), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Chain(rig, WriteAs("acme:builder", "/w/src/Foo.cs")));
    }

    /// <summary>ah2-562 Amendment 1: a review-class role's write folders let through only paths inside its work tree that match a glob.</summary>
    [Fact]
    public void ReviewerWriteLetsAReviewRoleWriteOnlyItsDeclaredFolders()
    {
        // the member's own tree is WRANGL_EXPECTED_ROOT, so it must exist before the rig: a temp dir of its own
        var proj = Directory.CreateTempSubdirectory("ah-writes-").FullName;
        try
        {
            var rig = SoftRig(new() { ["WRANGL_EXPECTED_ROOT"] = proj });
            Directory.CreateDirectory(proj + "/.claude/ah2");
            Directory.CreateDirectory(proj + "/.git");
            File.WriteAllText(proj + "/.claude/ah2/agent-hierarchy.json",
                """{"version":1,"roles":{"tester":{"class":"review","agent":"acme:tester","writes":["tests/automation/**"]}}}""");
            string Write(string path) => InCwd(WriteAs("acme:tester", path), proj);

            Assert.Equal(string.Empty, Chain(rig, Write(proj + "/tests/automation/a.py")));
            var bounced = rig.Run("pretooluse-chain", Write(proj + "/src/Foo.cs"));
            Assert.True(IsDenied(bounced));
            Assert.Contains("tester writes only its own response and: tests/automation/**", Reason(bounced), StringComparison.Ordinal);
            Assert.True(IsDenied(rig.Run("pretooluse-chain", Write(proj + "/tests/automation/../../src/Bar.cs"))));
            Assert.True(IsDenied(rig.Run("pretooluse-chain", Write("/elsewhere/tests/automation/a.py"))));

            // a link is followed before the glob is matched; a relative path is taken against the cwd
            Directory.CreateDirectory(proj + "/src");
            Directory.CreateDirectory(proj + "/tests/automation");
            Directory.CreateSymbolicLink(proj + "/tests/automation/out", proj + "/src");
            Assert.True(IsDenied(rig.Run("pretooluse-chain", Write(proj + "/tests/automation/out/Foo.cs"))));
            Assert.Equal(string.Empty, Chain(rig, Write("tests/automation/b.py")));
        }
        finally
        {
            Directory.Delete(proj, recursive: true);
        }
    }

    /// <summary>
    /// ah2-562 V10: a cd into another checkout does not lend it the member's globs (its own tree is WRANGL_EXPECTED_ROOT); with no
    /// expected root and no recorded tree, the cwd's checkout is no root at all.
    /// </summary>
    [Fact]
    public void ReviewerWriteKeepsTheWriteFoldersToTheMembersOwnTree()
    {
        var config = """{"version":1,"roles":{"tester":{"class":"review","agent":"acme:tester","writes":["tests/automation/**"]}}}""";
        static void Tree(string tree, string config)
        {
            Directory.CreateDirectory(tree + "/.git");
            Directory.CreateDirectory(tree + "/.claude/ah2");
            File.WriteAllText(tree + "/.claude/ah2/agent-hierarchy.json", config);
        }

        var rig = SoftRig(new() { ["WRANGL_EXPECTED_ROOT"] = "/ah-test/own-tree" });
        var other = rig.Sandbox.Root + "/other";
        Tree(other, config);
        Assert.True(IsDenied(rig.Run("pretooluse-chain", InCwd(WriteAs("acme:tester", other + "/tests/automation/a.py"), other))));

        var unrecorded = SoftRig();
        var proj = unrecorded.Sandbox.Root + "/proj";
        Tree(proj, config);
        Assert.True(IsDenied(unrecorded.Run("pretooluse-chain", InCwd(WriteAs("acme:tester", proj + "/tests/automation/a.py"), proj))));
    }

    private static string InCwd(string eventJson, string cwd)
    {
        var e = JsonText.Parse(eventJson).AsObject();
        e["cwd"] = cwd;
        return JsonText.Stringify(e, 0);
    }
}
