using System.Globalization;
using Ah.Core.Json;
using Ah.Mesh;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary>
/// The sub-orchestrator's close rules hold whatever the environment marker says: they are keyed on what the walk and the
/// registrations prove, and the marker only ever refuses. Every case runs with the marker unset, blank and agreeing.
/// </summary>
public sealed partial class RosterTeamVerbTests
{
    private void B1Env(string? marker)
    {
        _realWalk = true;
        _callers ??= new RecordingCallers();
        _runner.PsPassthrough = OperatingSystem.IsWindows() ? null : PlatformModule.CreateProcessRunner();
        _extraEnv.Remove(MeshModule.MemberMarkerEnv);
        _extraEnv.Remove("WRANGL_TEAM_FILE");
        if (marker is not null)
        {
            _extraEnv[MeshModule.MemberMarkerEnv] = marker;
        }

        if (!string.IsNullOrEmpty(marker))
        {
            _extraEnv["WRANGL_TEAM_FILE"] = TeamPath("mo");
        }
    }

    private static string B1Grant(string token, string verb, int moPid, string moStart) =>
        "{\"verb\":\"" + verb + "\",\"team\":\"" + ChildName + "\",\"plan_token\":\"" + token + "\",\"targets\":[],\"granted_at\":\"2026-10-16T01:00:00.000-04:00\","
        + "\"expires_at\":\"2999-01-01T00:00:00.000Z\",\"by\":{\"session_id\":\"sess-mo\",\"pid\":" + moPid.ToString(CultureInfo.InvariantCulture) + ",\"pid_start\":\"" + moStart + "\"}}";

    /// <summary>The sub-orchestrator that owns its child with one implementor; <paramref name="live"/> gives the implementor a live peer row.</summary>
    private (int Mo, string MoStart) ArrangeB1(string? marker, bool live, bool withMember = true)
    {
        var p = ParentPid();
        var start = StartOfPid(p);
        var mo = LivePidOfAnotherProcess();
        var moStart = StartOfPid(mo);
        WriteTeam(
            "mo",
            "{\"version\":1,\"team_id\":\"t-mo\",\"transport\":\"terminal\",\"orchestrator\":" + OwnerJson(mo, moStart, "sess-mo") + ",\"members\":["
            + "{\"name\":\"" + WalkSo + "\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"},{\"name\":\"mo-so-ws2\",\"role\":\"sub-orchestrator\",\"route\":\"peer\"}],"
            + "\"streams\":{\"ws1\":{\"state\":\"open\",\"sub_orchestrator\":\"" + WalkSo + "\"},\"ws2\":{\"state\":\"open\",\"sub_orchestrator\":\"mo-so-ws2\"}}}");
        var child = LiveTeamJson("tmux", withMember ? OneImplementor : string.Empty, p.ToString(CultureInfo.InvariantCulture))
            .Replace("\"orchestrator\":{\"session_id\":null,\"pid\":" + p.ToString(CultureInfo.InvariantCulture) + "}", "\"orchestrator\":" + OwnerJson(p, start, "sess-so"), StringComparison.Ordinal)
            .Replace("\"version\":1,", "\"version\":1,\"parent\":{\"team\":\"mo\",\"stream\":\"ws1\"},", StringComparison.Ordinal);
        WriteTeam(ChildName, child);
        WalkRegister(p, start, "sess-so", "orchestrator@mo-ws1", WalkSo + "@mo");
        ScriptGit();
        if (live)
        {
            PeerUp(Impl, "%1");
        }

        B1Env(marker);
        return (mo, moStart);
    }

    private string B1Token(string verb = "disband") =>
        JsonText.Parse(Roster(verb == "disband" ? ["disband", "--team", ChildName] : ["dismiss", Impl, "--plan", "--team", ChildName]).Stdout).Member("close_token")!.Value.AsString();

    private void B1Grant(string token, int mo, string moStart, string verb = "disband")
    {
        var text = _sandbox.TeamText(TeamPath("mo")).Replace(
            "\"sub_orchestrator\":\"" + WalkSo + "\"",
            "\"sub_orchestrator\":\"" + WalkSo + "\",\"close_grants\":[" + B1Grant(token, verb, mo, moStart) + "]",
            StringComparison.Ordinal);
        _sandbox.PlantTeam(TeamPath("mo"),text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(WalkSo)]
    public void B1UntrackOfALiveMemberIsRefusedWhateverTheMarkerSays(string? marker)
    {
        ArrangeB1(marker, live: true);

        var (code, _, stderr) = Roster("untrack", Impl, "--team", ChildName, "--commit");

        Assert.NotEqual(0, code);
        Assert.Contains("still running", stderr, StringComparison.Ordinal);
        Assert.Contains(Impl, _sandbox.TeamText(TeamPath(ChildName)), StringComparison.Ordinal);
    }

    [Fact]
    public void B1UntrackOfAMemberWhoseLivenessIsUnknownProceedsWithTheNote()
    {
        ArrangeB1(WalkSo, live: false);
        _registrations = new() { [$"{Impl}@{ChildName}"] = new Ah.Hierarchy.MemberRegistrationLiveness("sess-impl", false, "no usable observation of its process") };

        var (code, stdout, stderr) = Roster("untrack", Impl, "--team", ChildName, "--commit");

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("liveness unknown", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain(Impl, _sandbox.TeamText(TeamPath(ChildName)), StringComparison.Ordinal);
    }

    [Fact]
    public void B1TheSubOrchestratorUntrackingTheLastDeadMemberKeepsTheChildFileWithItsRecords()
    {
        ArrangeB1(WalkSo, live: false);

        var (code, stdout, stderr) = Roster("untrack", Impl, "--team", ChildName, "--commit");

        Assert.True(code == 0, stdout + stderr);
        var child = JsonText.Parse(_sandbox.TeamText(TeamPath(ChildName))).AsObject();
        Assert.Empty(child["members"].AsArray());
        Assert.NotNull(child.Get("parent"));
        Assert.NotNull(child.Get("orchestrator"));
    }

    [Fact]
    public void UntrackPlanSaysNothingChangedAndNamesTheCommitCommandAndACommitReportsNoPlanMarker()
    {
        ArrangeB1(null, live: false);

        var (code, stdout, stderr) = Roster("untrack", Impl, "--team", ChildName);

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout).AsObject();
        Assert.True(plan["plan_only"].AsBool());
        Assert.False(plan["changed"].AsBool());
        Assert.Contains("nothing was changed", plan["note"].AsString(), StringComparison.Ordinal);
        Assert.Matches($"^\\S.* untrack {Impl} --team {ChildName} --cwd \\S+ --commit$", plan["commit"].AsString());
        Assert.DoesNotMatch("^ah roster untrack", plan["commit"].AsString());
        Assert.Null(plan.Get("remaining"));
        Assert.Contains(Impl, _sandbox.TeamText(TeamPath(ChildName)), StringComparison.Ordinal);

        var done = Roster(PlanCommitArgs(plan));
        Assert.True(done.Item1 == 0, done.Item2 + done.Item3);
        Assert.DoesNotContain("plan_only", done.Item2, StringComparison.Ordinal);
    }

    [Fact]
    public void UntrackAllPlanCarriesKeepSessionsAndItsCommitCommandUntracksTheTeamWithNoPlanMarker()
    {
        ArrangeB1(null, live: false);

        var (code, stdout, stderr) = Roster("untrack", "--all", "--keep-sessions", "--team", ChildName);

        Assert.True(code == 0, stdout + stderr);
        var plan = JsonText.Parse(stdout).AsObject();
        Assert.Equal("untrack-all", plan["plan"].AsString());
        Assert.True(plan["plan_only"].AsBool());
        Assert.False(plan["changed"].AsBool());
        Assert.Matches($"^\\S.* untrack --all --keep-sessions --team {ChildName} --cwd \\S+ --commit$", plan["commit"].AsString());
        Assert.True(_sandbox.TeamExists(TeamPath(ChildName)));

        var done = Roster(PlanCommitArgs(plan));
        Assert.True(done.Item1 == 0, done.Item2 + done.Item3);
        Assert.DoesNotContain("plan_only", done.Item2, StringComparison.Ordinal);
        Assert.False(_sandbox.TeamExists(TeamPath(ChildName)));
    }

    [Fact]
    public void UntrackPlanCommitCommandCarriesAlsoConfigAndLevel()
    {
        ArrangeB1(null, live: false);

        var (code, stdout, stderr) = Roster("untrack", Impl, "--also-config", "--level", "project", "--team", ChildName);

        Assert.True(code == 0, stdout + stderr);
        var commit = JsonText.Parse(stdout).AsObject()["commit"].AsString();
        Assert.Matches($"^\\S.* untrack {Impl} --also-config --level project --team {ChildName} --cwd \\S+ --commit$", commit);
    }

    // The words of a plan's commit command after the roster CLI prefix, as the roster verb takes them.
    private static string[] PlanCommitArgs(JsonObject plan)
    {
        var commit = plan["commit"].AsString();
        return commit[(commit.IndexOf(" untrack ", StringComparison.Ordinal) + 1)..].Split(' ');
    }

    [Fact]
    public void NegativeB1AnUnmarkedOwnerUntrackingTheLastDeadMemberStillRemovesTheFile()
    {
        ArrangeB1(null, live: false);

        var (code, stdout, stderr) = Roster("untrack", Impl, "--team", ChildName, "--commit");

        Assert.True(code == 0, stdout + stderr);
        Assert.False(_sandbox.TeamExists(TeamPath(ChildName)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(WalkSo)]
    public void B1AGrantDoesNotLetAForcingFlagThrough(string? marker)
    {
        var (mo, moStart) = ArrangeB1(marker, live: false);
        var token = B1Token();
        B1Grant(token, mo, moStart);

        Assert.Equal("close-flag-refused", RefusalCode("disband", "--close", "--confirm", "--plan-token", token, "--team", ChildName, "--ignore-release"));
        Assert.Equal("close-flag-refused", RefusalCode("disband", "--close", "--confirm", "--plan-token", token, "--team", ChildName, "--ignore-release", "some-member"));
        Assert.Equal("close-flag-refused", RefusalCode("disband", "--close", "--confirm", "--plan-token", token, "--team", ChildName, "--kill"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(WalkSo)]
    public void B1ACloseWithNoGrantIsRefused(string? marker)
    {
        ArrangeB1(marker, live: false);
        var token = B1Token();

        Assert.Equal("close-no-green-light", RefusalCode("disband", "--close", "--confirm", "--plan-token", token, "--team", ChildName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(WalkSo)]
    public void B1AnEmptyCloseSetIsRefused(string? marker)
    {
        ArrangeB1(marker, live: false, withMember: false);
        var token = B1Token();

        Assert.Equal("close-empty-set", RefusalCode("disband", "--close", "--confirm", "--plan-token", token, "--team", ChildName));
    }

    [Fact]
    public void B1AnotherMembersMarkerIsRefused()
    {
        ArrangeB1("mo-so-ws2", live: false);

        Assert.Equal("member-marker-disagrees", RefusalCode("disband", "--team", ChildName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(WalkSo)]
    public void B1AGoneMemberMayBeUntrackedAndAGrantedCloseWithoutForcingFlagsProceeds(string? marker)
    {
        var (mo, moStart) = ArrangeB1(marker, live: false);
        var token = B1Token();
        B1Grant(token, mo, moStart);

        var closed = Roster("disband", "--close", "--confirm", "--plan-token", token, "--team", ChildName);

        Assert.True(closed.Code == 0, closed.Stdout + closed.Stderr);

        ArrangeB1(marker, live: false);
        var untracked = Roster("untrack", Impl, "--team", ChildName, "--commit");

        Assert.True(untracked.Code == 0, untracked.Stdout + untracked.Stderr);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(WalkSo)]
    public void B1TheSubOrchestratorCannotApproveItsOwnClose(string? marker)
    {
        ArrangeB1(marker, live: false);

        var (code, stdout, _) = Roster("close-approve", "--team", ChildName, "--verb", "disband");

        Assert.NotEqual(0, code);
        Assert.DoesNotContain("\"approve\"", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("close_grants", _sandbox.TeamText(TeamPath("mo")), StringComparison.Ordinal);
    }

    [Fact]
    public void B1ATopLevelOwnerIsNotHeldToTheSubOrchestratorRules()
    {
        var p = ParentPid();
        var start = StartOfPid(p);
        WriteTeam("solo", LiveTeamJson("tmux", string.Empty, p.ToString(CultureInfo.InvariantCulture)).Replace("\"orchestrator\":{\"session_id\":null,\"pid\":" + p.ToString(CultureInfo.InvariantCulture) + "}", "\"orchestrator\":" + OwnerJson(p, start, "sess-t"), StringComparison.Ordinal));
        ScriptGit();
        B1Env(null);

        var token = JsonText.Parse(Roster("disband", "--team", "solo").Stdout).Member("close_token")!.Value.AsString();
        var forced = RefusalCode("disband", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", "solo");
        var empty = RefusalCode("disband", "--close", "--confirm", "--plan-token", token, "--team", "solo");

        Assert.NotEqual("close-flag-refused", forced);
        Assert.NotEqual("close-empty-set", empty);
        Assert.NotEqual("close-no-green-light", empty);
    }

    [Fact]
    public void B1TheOwnerOfAChildWhoseRowIsNotRegisteredToItIsNotHeldToTheRules()
    {
        var (mo, moStart) = ArrangeB1(null, live: false);
        var p = ParentPid();
        WalkRegister(p, StartOfPid(p), "sess-so", "orchestrator@mo-ws1");
        var token = B1Token();
        B1Grant(token, mo, moStart);

        Assert.NotEqual("close-flag-refused", RefusalCode("disband", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", ChildName));
    }

    /// <summary>P also owns a team the walk returns first: an unrelated top-level team (<paramref name="second"/> false) or the child of a second stream the same row serves.</summary>
    private (int Mo, string MoStart) ArrangeB1TwoTeams(string? marker, bool second)
    {
        var (mo, moStart) = ArrangeB1(marker, live: true);
        var p = ParentPid();
        var start = StartOfPid(p);
        if (second)
        {
            _sandbox.PlantTeam(
                TeamPath("mo"),
                _sandbox.TeamText(TeamPath("mo")).Replace("\"sub_orchestrator\":\"mo-so-ws2\"", "\"sub_orchestrator\":\"" + WalkSo + "\"", StringComparison.Ordinal));
            WriteTeam(
                "mo-ws2",
                LiveTeamJson("tmux", OneImplementor, p.ToString(CultureInfo.InvariantCulture))
                    .Replace("\"orchestrator\":{\"session_id\":null,\"pid\":" + p.ToString(CultureInfo.InvariantCulture) + "}", "\"orchestrator\":" + OwnerJson(p, start, "sess-so"), StringComparison.Ordinal)
                    .Replace("\"version\":1,", "\"version\":1,\"parent\":{\"team\":\"mo\",\"stream\":\"ws2\"},", StringComparison.Ordinal));
        }
        else
        {
            WriteTeam("aaa-top", LiveTeamJson("tmux", string.Empty, p.ToString(CultureInfo.InvariantCulture)).Replace("\"orchestrator\":{\"session_id\":null,\"pid\":" + p.ToString(CultureInfo.InvariantCulture) + "}", "\"orchestrator\":" + OwnerJson(p, start, "sess-so"), StringComparison.Ordinal));
        }

        return (mo, moStart);
    }

    [Theory]
    [InlineData(null, false, ChildName)]
    [InlineData("", false, ChildName)]
    [InlineData(WalkSo, false, ChildName)]
    [InlineData(null, true, ChildName)]
    [InlineData("", true, ChildName)]
    [InlineData(WalkSo, true, ChildName)]
    [InlineData(null, true, "mo-ws2")]
    [InlineData("", true, "mo-ws2")]
    [InlineData(WalkSo, true, "mo-ws2")]
    public void B2TheRestrictionsHoldOnEveryTeamTheSubOrchestratorOwns(string? marker, bool second, string target)
    {
        var (mo, moStart) = ArrangeB1TwoTeams(marker, second);

        var untrack = Roster("untrack", Impl, "--team", target, "--commit");
        Assert.NotEqual(0, untrack.Code);
        Assert.Contains("still running", untrack.Stderr, StringComparison.Ordinal);

        _sandbox.ClearPeers(Hierarchy);
        AppendGoneRow(Impl, target);
        var plan = JsonText.Parse(Roster("disband", "--team", target).Stdout).Member("close_token")!.Value.AsString();
        Assert.Equal("close-no-green-light", RefusalCode("disband", "--close", "--confirm", "--plan-token", plan, "--team", target));
        var grant = _sandbox.TeamText(TeamPath("mo")).Replace(
            "\"state\":\"open\",\"sub_orchestrator\":\"" + WalkSo + "\"",
            "\"state\":\"open\",\"sub_orchestrator\":\"" + WalkSo + "\",\"close_grants\":[" + B1Grant(plan, "disband", mo, moStart).Replace(ChildName, target, StringComparison.Ordinal) + "]",
            StringComparison.Ordinal);
        _sandbox.PlantTeam(TeamPath("mo"),grant);
        Assert.Equal("close-flag-refused", RefusalCode("disband", "--close", "--confirm", "--ignore-release", "--plan-token", plan, "--team", target));
    }

    [Fact]
    public void B2AnUnrelatedTopLevelTeamTheSameProcessOwnsIsStillThePlainOwnerPath()
    {
        ArrangeB1TwoTeams(null, second: false);
        var token = JsonText.Parse(Roster("disband", "--team", "aaa-top").Stdout).Member("close_token")!.Value.AsString();

        var forced = RefusalCode("disband", "--close", "--confirm", "--ignore-release", "--plan-token", token, "--team", "aaa-top");

        Assert.NotEqual("close-flag-refused", forced);
        Assert.NotEqual("close-no-green-light", RefusalCode("disband", "--close", "--confirm", "--plan-token", token, "--team", "aaa-top"));
    }
}
