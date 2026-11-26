using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>Spawning a sub-orchestrator: the rules that are checked before any pane opens, its exact name, and its record on the stream.</summary>
public sealed partial class RosterTeamVerbTests
{
    private static readonly string[] BreadthStreams = ["a", "b", "c", "s1"];

    private string SoTeam(string streams, string members = "", string extra = "") =>
        LiveTeamJson("terminal", members).TrimEnd('}') + extra + ",\"streams\":{" + streams + "}}";

    private static string OpenStreamJson(string ws, string? so = null) =>
        $"\"{ws}\":{{\"state\":\"open\",\"branch\":null,\"base\":\"main\",\"worktree\":null,\"tab_id\":null,\"opened\":\"2026-10-11T00:00:00.000Z\"{(so is null ? string.Empty : $",\"sub_orchestrator\":\"{so}\"")}}}";

    private string SoMember(string ws, bool live)
    {
        var name = "proj-so-" + ws;
        if (live)
        {
            PeerUp(name, "%" + ws.GetHashCode(StringComparison.Ordinal) % 90 + 10);
        }

        return $"{{\"role\":\"sub-orchestrator\",\"name\":\"{name}\",\"route\":\"peer\",\"transport_id\":\"%1{ws.Length}\",\"model\":\"inherit\",\"expected_root\":\"{Project}\",\"stream\":\"{ws}\"}}";
    }

    private (int Code, JsonObject Out) SpawnSo(params string[] extra)
    {
        var (code, stdout, _) = Roster(["spawn-ad-hoc", "sub-orchestrator", "--model", "inherit", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", .. extra]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout).AsObject() : new JsonObject());
    }

    [Fact]
    public void SpawnOneOfASubOrchestratorNeedsNoRosterMemberAndAnotherRoleStillDoes()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, stdout, _) = Roster(["spawn-one", "sub-orchestrator", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", "--stream", "s1"]);
        var (customCode, customOut, customErr) = Roster(["spawn-one", "tech-writer", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);

        Assert.Equal(0, code);
        Assert.Equal("proj-so-s1", JsonText.Parse(stdout).AsObject()["name"].AsString());
        Assert.NotEqual(0, customCode);
        Assert.Empty(customOut);
        Assert.Contains("role must be one of", customErr, StringComparison.Ordinal);
        Assert.Contains("tech-writer", customErr, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeputyChiefSpawnsAsAnOrdinaryMemberWithNoStreamWhileASubOrchestratorStillNeedsOne()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", "deputy-chief", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);
        var (soCode, soOut) = SpawnSo();

        Assert.True(code == 0, stderr);
        var plan = JsonText.Parse(stdout).AsObject();
        Assert.EndsWith("-deputy-chief", plan["name"].AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("-so-", plan["name"].AsString(), StringComparison.Ordinal);
        Assert.False(plan.TryGetValue("stream", out var ws) && ws.IsTruthy, stdout);
        Assert.NotEqual(0, soCode);
        Assert.Equal("stream-required", soOut["refused"].AsString());
    }

    private string QaRow(bool live)
    {
        if (live)
        {
            PeerUp("proj-qa", "%77");
        }

        return $"{{\"role\":\"qa\",\"name\":\"proj-qa\",\"route\":\"peer\",\"transport_id\":\"%77\",\"model\":\"sonnet\",\"expected_root\":\"{Project}\"}}";
    }

    private void WriteProjectRoster(string members)
    {
        var config = Path.Combine(Project, ".claude", "ah2", "agent-hierarchy.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, "{\"version\":1,\"roster\":{\"route\":\"peer\",\"members\":[" + members + "]}}\n");
    }

    private const string ReviewerRosterRow = "{\"role\":\"reviewer\",\"model\":\"sonnet\"}";

    [Fact]
    public void SpawnOneOfABuiltInRoleTheRosterLacksLaunchesItThroughSpawnAdHocAndQaNeedsNoModel()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));
        WriteProjectRoster(ReviewerRosterRow);

        var (code, stdout, stderr) = Roster(["spawn-one", "qa", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);
        var (noModelCode, noModelOut, noModelErr) = Roster(["spawn-one", "qa", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);

        Assert.True(code == 0, stderr);
        Assert.Contains("--agent wrangl:qa", stdout, StringComparison.Ordinal);
        Assert.Equal("sonnet", Ah.Config.ConfigModule.MemberDefaultModel("qa"));
        Assert.True(noModelCode == 0, noModelErr);
        Assert.DoesNotContain("member-model-undefined", noModelOut, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnOneOfABuiltInRoleWithNoRosterAtAllStillPointsToTheAdHocVerb()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, _, stderr) = Roster(["spawn-one", "qa", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);

        Assert.NotEqual(0, code);
        Assert.Contains("run `spawn-ad-hoc qa` instead", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASecondSpawnOneOfAnUnrosteredRoleWhileOneIsLiveIsRefusedAsRoleLive()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1"), QaRow(live: true)));
        WriteProjectRoster(ReviewerRosterRow);

        var (code, stdout, stderr) = Roster(["spawn-one", "qa", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);

        Assert.NotEqual(0, code);
        Assert.Contains("role-live", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("--agent wrangl:qa", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrosteredRoleWhoseRecordIsNotLiveIsSpawnedAndARoleTheRosterDefinesIsUnchanged()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1"), QaRow(live: false)));
        WriteProjectRoster(ReviewerRosterRow + ",{\"role\":\"qa\",\"model\":\"sonnet\"}");

        var (code, stdout, stderr) = Roster(["spawn-one", "qa", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);

        Assert.True(code == 0, stderr);
        Assert.Contains("--agent wrangl:qa", stdout, StringComparison.Ordinal);
        Assert.Contains("sonnet", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnOneOfASubOrchestratorRefusesMemberInSpawnOneTerms()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, stdout, stderr) = Roster(["spawn-one", "sub-orchestrator", "--member", "x", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", "--stream", "s1"]);

        Assert.NotEqual(0, code);
        Assert.Empty(stdout);
        Assert.Contains("spawn-one: --member does not apply to a sub-orchestrator", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("spawn-ad-hoc", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorLaunchesTheSubOrchestratorAgentAndAnArchitectItsOwn()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, stdout, _) = Roster(["spawn-ad-hoc", "sub-orchestrator", "--model", "inherit", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", "--stream", "s1"]);
        var (architectCode, architectOut, _) = Roster(["spawn-ad-hoc", "architect", "--model", "opus", "--team", "proj", "--orchestrator-pid", Self, "--dry-run"]);

        Assert.Equal(0, code);
        Assert.Contains("--agent wrangl:sub-orchestrator", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("--agent wrangl:orchestrator", stdout, StringComparison.Ordinal);
        Assert.Equal(0, architectCode);
        Assert.Contains("--agent wrangl:architect", architectOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubOrchestratorNeedsAStreamAndIsNamedTeamSoStream()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (noStreamCode, noStream) = SpawnSo();
        var (code, planned) = SpawnSo("--stream", "s1");

        Assert.Equal(2, noStreamCode);
        Assert.Equal("stream-required", noStream["refused"].AsString());
        Assert.Equal(0, code);
        Assert.Equal("proj-so-s1", planned["name"].AsString());
    }

    [Fact]
    public void ASubOrchestratorIsRefusedInATeamThatHasAParentAndNotInOneThatDoesNot()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1"), extra: ",\"parent\":{\"team\":\"top\",\"stream\":\"s1\"}"));

        var (code, refused) = SpawnSo("--stream", "s1");
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));
        var (topCode, _) = SpawnSo("--stream", "s1");

        Assert.Equal(2, code);
        Assert.Equal("lane-owner-unproven", refused["refused"].AsString());
        Assert.Equal(0, topCode);
    }

    [Fact]
    public void AStreamWithALiveSubOrchestratorTakesNoSecondOneAndAGoneOneIsReplaceable()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1", "proj-so-s1"), SoMember("s1", live: true)));

        var (liveCode, live) = SpawnSo("--stream", "s1");
        _sandbox.ClearPeers(Hierarchy);
        var (goneCode, _) = SpawnSo("--stream", "s1");
        WriteTeam("proj", SoTeam(OpenStreamJson("s1", "proj-so-vanished")));
        var (strayCode, _) = SpawnSo("--stream", "s1");

        Assert.Equal(2, liveCode);
        Assert.Equal("sub-orchestrator-exists", live["refused"].AsString());
        Assert.Equal(0, goneCode);
        Assert.Equal(0, strayCode);
    }

    [Fact]
    public void AnOverTheCapSubOrchestratorIsRefusedAndAGoneOneDoesNotCount()
    {
        string Streams() => string.Join(",", BreadthStreams.Select(w => OpenStreamJson(w, w == "s1" ? null : "proj-so-" + w)));
        WriteTeam("proj", SoTeam(Streams(), string.Join(",", new[] { SoMember("a", true), SoMember("b", true), SoMember("c", true) }), ",\"max_lanes\":3"));

        var (fourthCode, fourth) = SpawnSo("--stream", "s1");
        _sandbox.ClearPeers(Hierarchy);
        WriteTeam("proj", SoTeam(Streams(), string.Join(",", new[] { SoMember("a", true), SoMember("b", true), SoMember("c", false) }), ",\"max_lanes\":3"));
        var (thirdCode, _) = SpawnSo("--stream", "s1");

        Assert.Equal(2, fourthCode);
        Assert.Equal("sub-orchestrator-breadth", fourth["refused"].AsString());
        Assert.Equal(0, thirdCode);
    }

    [Fact]
    public void ANameOverTheLimitIsRefusedWithTheLongestStreamThatFitsAndOneAtTheLimitIsNot()
    {
        var atLimit = new string('w', 24);
        var over = new string('w', 25);
        WriteTeam("proj", SoTeam(OpenStreamJson(atLimit) + "," + OpenStreamJson(over)));

        var (okCode, planned) = SpawnSo("--stream", atLimit);
        var (code, refused) = SpawnSo("--stream", over);

        Assert.Equal(0, okCode);
        Assert.Equal(32, planned["name"].AsString().Length);
        Assert.Equal(2, code);
        Assert.Equal("sub-orchestrator-name", refused["refused"].AsString());
        Assert.Contains("is 33 characters, over the limit of 32", refused["message"].AsString(), StringComparison.Ordinal);
        Assert.Contains("is 24 characters", refused["message"].AsString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("architect")]
    [InlineData("x-reviewer")]
    [InlineData("x-implementor-2")]
    [InlineData("x-7")]
    public void AStreamNamedLikeADerivedMemberNameTakesNoSubOrchestrator(string stream)
    {
        WriteTeam("proj", SoTeam(OpenStreamJson(stream)));

        var (code, refused) = SpawnSo("--stream", stream);

        Assert.Equal(2, code);
        Assert.Equal("sub-orchestrator-name", refused["refused"].AsString());
    }

    [Fact]
    public void ADigitSuffixedStreamRefusalStatesTheRuleTheDerivedNameAndARename()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("lane-434") + "," + OpenStreamJson("lane-434a")));

        var (code, refused) = SpawnSo("--stream", "lane-434");
        var (okCode, _) = SpawnSo("--stream", "lane-434a");

        Assert.Equal(2, code);
        Assert.Equal("sub-orchestrator-name", refused["refused"].AsString());
        var message = refused["message"].AsString();
        Assert.Contains("reserved for numbered members", message, StringComparison.Ordinal);
        Assert.Contains("proj-so-lane-434", message, StringComparison.Ordinal);
        Assert.Contains("lane-434a", message, StringComparison.Ordinal);
        Assert.Equal(0, okCode);
    }

    [Fact]
    public void ARoleSuffixedStreamRefusalStatesTheRuleTheDerivedNameAndARename()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("x-reviewer")));

        var (code, refused) = SpawnSo("--stream", "x-reviewer");

        Assert.Equal(2, code);
        Assert.Equal("sub-orchestrator-name", refused["refused"].AsString());
        var message = refused["message"].AsString();
        Assert.Contains("reserved for a role's derived member names", message, StringComparison.Ordinal);
        Assert.Contains("proj-so-x-reviewer", message, StringComparison.Ordinal);
        Assert.Contains("x-reviewera", message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("feature")]
    [InlineData("x-reviewers")]
    [InlineData("v7a")]
    public void AStreamThatOnlyLooksNearlyLikeADerivedNameIsAccepted(string stream)
    {
        WriteTeam("proj", SoTeam(OpenStreamJson(stream)));

        Assert.Equal(0, SpawnSo("--stream", stream).Code);
    }

    [Fact]
    public void ASpawnRecordsTheSubOrchestratorOnItsStreamAndAnotherRoleDoesNot()
    {
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;

        var (architectCode, _, architectErr) = Roster("spawn-ad-hoc", "architect", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1");
        var afterArchitect = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "sub-orchestrator", "--model", "inherit", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1");

        Assert.True(architectCode == 0, architectErr);
        Assert.Null(afterArchitect.Member("streams")!.Value.Member("s1")!.Value.Member("sub_orchestrator"));
        Assert.True(code == 0, stdout + stderr);
        var team = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Equal("proj-so-s1", team.Member("streams")!.Value.Member("s1")!.Value.Member("sub_orchestrator")!.Value.AsString());
        var row = team.Member("members")!.Value.AsArray().Single(m => m.Member("role")!.Value.AsString() == "sub-orchestrator");
        Assert.Equal("proj-so-s1", row.Member("name")!.Value.AsString());
        Assert.Equal("s1", row.Member("stream")!.Value.AsString());
        var lane = team.Member("streams")!.Value.Member("s1")!.Value.Member("lane")!.Value;
        Assert.Equal(4, (int)lane.Member("max_members")!.Value.AsNumber());
        Assert.Equal(8, (int)lane.Member("max_streams")!.Value.AsNumber());
    }

    private void TmuxStreamTeam()
    {
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
    }

    private (int Code, string Output) SpawnOneSo()
    {
        var (code, stdout, stderr) = Roster("spawn-one", "sub-orchestrator", "--model", "inherit", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1");
        return (code, stdout + stderr);
    }

    private JsonObject ReadProj() => JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).AsObject();

    private void EditProj(Action<JsonObject> change)
    {
        var team = ReadProj().Clone();
        change(team);
        _sandbox.PlantTeam(TeamPath("proj"),JsonText.Stringify(team, 0));
    }

    [Fact]
    public void ASpawnOneOfASubOrchestratorHasItsRowAndLaneRecordedBeforeThePaneOpensAndMergesThePaneAfter()
    {
        TmuxStreamTeam();
        JsonObject? atLaunch = null;
        _runner.Output = argv =>
        {
            if (!argv.Contains("new-window"))
            {
                return string.Empty;
            }

            atLaunch = ReadProj();
            return "%7\n";
        };

        var (code, output) = SpawnOneSo();

        Assert.True(code == 0, output);
        var early = atLaunch!.Get("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "proj-so-s1").AsObject();
        Assert.True(early.ContainsKey("spawned_at"));
        Assert.False(early.ContainsKey("transport_id"));
        Assert.Equal("proj-so-s1", atLaunch.Get("streams")!.Value.Member("s1")!.Value.Member("sub_orchestrator")!.Value.AsString());
        var final = ReadProj().Get("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "proj-so-s1").AsObject();
        Assert.Equal("%7", final["transport_id"].AsString());
        Assert.Equal(early["spawned_at"].AsString(), final["spawned_at"].AsString());
    }

    [Fact]
    public void ASpawnOneOfASubOrchestratorWhoseLaunchFailsTakesItsRowAndLaneBackAndKeepsAnotherLanesAssignment()
    {
        TmuxStreamTeam();
        var before = ReadProj();
        _runner.Output = argv =>
        {
            if (!argv.Contains("new-window"))
            {
                return string.Empty;
            }

            EditProj(team =>
            {
                var streams = team.Get("streams")!.Value.AsObject().Clone();
                streams["s2"] = JsonText.Parse("{\"state\":\"open\",\"sub_orchestrator\":\"proj-so-s2\"}");
                team["streams"] = streams;
            });
            throw new InvalidOperationException("tmux refused the new window");
        };

        var (code, output) = SpawnOneSo();

        Assert.NotEqual(0, code);
        var after = ReadProj();
        Assert.DoesNotContain(after.Get("members")!.Value.AsArray(), m => m.Member("name")!.Value.AsString() == "proj-so-s1");
        Assert.Null(after.Get("streams")!.Value.Member("s1")!.Value.Member("sub_orchestrator"));
        Assert.Equal("proj-so-s2", after.Get("streams")!.Value.Member("s2")!.Value.Member("sub_orchestrator")!.Value.AsString());
        Assert.Equal(before.Get("members")!.Value.AsArray().Count, after.Get("members")!.Value.AsArray().Count);
        _ = output;
    }

    [Fact]
    public void ASpawnOneOfASubOrchestratorWhoseLaneEntryWasChangedMeanwhileLeavesThatEntryAlone()
    {
        TmuxStreamTeam();
        _runner.Output = argv =>
        {
            if (!argv.Contains("new-window"))
            {
                return string.Empty;
            }

            EditProj(team =>
            {
                var streams = team.Get("streams")!.Value.AsObject().Clone();
                var s1 = streams.Get("s1")!.Value.AsObject().Clone();
                s1["sub_orchestrator"] = "proj-so-other";
                streams["s1"] = s1;
                team["streams"] = streams;
            });
            throw new InvalidOperationException("tmux refused the new window");
        };

        var (code, _) = SpawnOneSo();

        Assert.NotEqual(0, code);
        Assert.Equal("proj-so-other", ReadProj().Get("streams")!.Value.Member("s1")!.Value.Member("sub_orchestrator")!.Value.AsString());
    }

    [Fact]
    public void NegativeASpawnOneOfASubOrchestratorWhoseRowWasReplacedDuringTheLaunchFailsWithSpawnRowReplaced()
    {
        TmuxStreamTeam();
        _runner.Output = argv =>
        {
            if (!argv.Contains("new-window"))
            {
                return string.Empty;
            }

            EditProj(team =>
            {
                var kept = new JsonArray();
                foreach (var m in team.Get("members")!.Value.AsArray().Where(m => m.Member("name")!.Value.AsString() != "proj-so-s1"))
                {
                    kept.Add(m);
                }

                kept.Add(JsonText.Parse("{\"role\":\"sub-orchestrator\",\"name\":\"proj-so-s1\",\"route\":\"peer\",\"spawned_at\":\"2026-01-01T00:00:00.000Z\"}"));
                team["members"] = kept;
            });
            return "%7\n";
        };

        var (code, output) = SpawnOneSo();

        Assert.NotEqual(0, code);
        Assert.Contains("spawn-row-replaced", output, StringComparison.Ordinal);
        var row = ReadProj().Get("members")!.Value.AsArray().Single(m => m.Member("name")!.Value.AsString() == "proj-so-s1").AsObject();
        Assert.Equal("2026-01-01T00:00:00.000Z", row["spawned_at"].AsString());
        Assert.False(row.ContainsKey("transport_id"));
    }

    [Fact]
    public void TheLaneFlagsAreWrittenToTheManagersStreamRecordAndAnArchitectGetsNoLaneRecord()
    {
        var worktree = MakeWorkTree("s1");
        WriteTeam("proj", TeamWithStream(worktree).Replace("\"transport\":\"terminal\"", "\"transport\":\"tmux\"", StringComparison.Ordinal));
        _extraEnv["TMUX"] = "/tmp/tmux-1/default,1,0";
        _extraEnv["TMUX_PANE"] = "%0";
        _runner.Output = argv => argv.Contains("new-window") ? "%7\n" : string.Empty;

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "sub-orchestrator", "--model", "inherit", "--team", "proj", "--orchestrator-pid", Self, "--stream", "s1", "--lane-members", "2", "--lane-streams", "16");

        Assert.True(code == 0, stdout + stderr);
        var lane = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("streams")!.Value.Member("s1")!.Value.Member("lane")!.Value;
        Assert.Equal(2, (int)lane.Member("max_members")!.Value.AsNumber());
        Assert.Equal(16, (int)lane.Member("max_streams")!.Value.AsNumber());
    }

    [Theory]
    [InlineData("--lane-members", "0")]
    [InlineData("--lane-members", "17")]
    [InlineData("--lane-members", "-1")]
    [InlineData("--lane-members", "2.5")]
    [InlineData("--lane-members", "many")]
    [InlineData("--lane-streams", "0")]
    [InlineData("--lane-streams", "17")]
    public void ALaneCapOutsideOneToSixteenIsRefusedBeforeAnyPaneOpens(string flag, string value)
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        var (code, refused) = SpawnSo("--stream", "s1", flag, value);

        Assert.Equal(2, code);
        Assert.Equal("lane-cap-invalid", refused["refused"].AsString());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("16")]
    public void ALaneCapAtTheEdgeOfTheRangeIsAccepted(string value)
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));

        Assert.Equal(0, SpawnSo("--stream", "s1", "--lane-members", value, "--lane-streams", value).Code);
    }

    [Fact]
    public void TheLaneFlagsAreRefusedForAnyRoleButASubOrchestrator()
    {
        WriteTeam("proj", TeamWithStream(Project));

        var (code, _, stderr) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", "--stream", "s1", "--lane-members", "3");
        var (plainCode, _, _) = Roster("spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self, "--dry-run", "--stream", "s1");

        Assert.NotEqual(0, code);
        Assert.Contains("--lane-members and --lane-streams apply only to a sub-orchestrator spawn", stderr, StringComparison.Ordinal);
        Assert.Equal(0, plainCode);
    }

    [Fact]
    public void ASubOrchestratorOfAStreamWithNoBaseIsRefusedAndOneWithABaseIsNot()
    {
        WriteTeam("proj", SoTeam(OpenStreamJson("s1").Replace("\"base\":\"main\"", "\"base\":null", StringComparison.Ordinal)));

        var (code, refused) = SpawnSo("--stream", "s1");
        WriteTeam("proj", SoTeam(OpenStreamJson("s1")));
        var (okCode, _) = SpawnSo("--stream", "s1");

        Assert.Equal(2, code);
        Assert.Equal("lane-base-missing", refused["refused"].AsString());
        Assert.Contains("open s1 with --base", refused["message"].AsString(), StringComparison.Ordinal);
        Assert.Equal(0, okCode);
    }
}
