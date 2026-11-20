using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;

namespace Ah.Cli.Tests;

/// <summary>The tmp every member is given: what the team file records and what <c>doctor</c> says of it.</summary>
public sealed partial class RosterTeamVerbTests
{
    private static string TmpMember(string name, string pane, string? tmp) =>
        $"{{\"role\":\"implementor\",\"name\":\"{name}\",\"route\":\"peer\",\"transport_id\":\"{pane}\",\"model\":\"sonnet\"{(tmp is null ? string.Empty : $",\"tmp\":\"{tmp}\"")}}}";

    private JsonObject MemberTmpRow(params string[] members)
    {
        var home = SpawnTeamHome();
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), LiveTeamJson("tmux", string.Join(",", members)));
        var (code, stdout, stderr) = Roster("doctor", "--team", "proj");
        Assert.True(code is 0 or 1, stdout + stderr);
        return JsonText.Parse(Encoding.UTF8.GetBytes(stdout)).AsObject()["rows"].AsArray()
            .Single(r => r.Member("name")!.Value.AsString() == "member-tmp").AsObject();
    }

    private string TmpDir(string name) => Directory.CreateDirectory(Path.Combine(_sandbox.Root, "tmps", name)).FullName;

    [Fact]
    public void DoctorSaysMemberTmpIsOkWhenEveryMemberHasItsOwnExistingIgnoredTmp()
    {
        var row = MemberTmpRow(TmpMember("a", "%1", TmpDir("a")), TmpMember("b", "%2", TmpDir("b")));

        Assert.Equal("ok", row["status"].AsString());
    }

    [Fact]
    public void DoctorWarnsOfAMemberRecordedWithNoTmpAndNamesTheReason()
    {
        var row = MemberTmpRow(TmpMember("a", "%1", "none (disk full)"));

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("a: none (disk full)", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorWarnsOfAMissingAnUnignoredAndASharedTmpAndOfAMemberSpawnedBeforeTmpWasRecorded()
    {
        var shared = TmpDir("shared");
        var missing = MemberTmpRow(TmpMember("a", "%1", Path.Combine(_sandbox.Root, "tmps", "gone")));
        var sharing = MemberTmpRow(TmpMember("a", "%1", shared), TmpMember("b", "%2", shared));
        var unknown = MemberTmpRow(TmpMember("a", "%1", null));
        _runner.Exit = argv => argv.Contains("check-ignore") ? 1 : 0;
        var unignored = MemberTmpRow(TmpMember("a", "%1", TmpDir("u")));

        Assert.Contains("a: tmp", missing["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("is missing", missing["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("shared with b", sharing["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("a: unknown (spawned before tmp was recorded)", unknown["detail"].AsString(), StringComparison.Ordinal);
        Assert.Contains("is not ignored by git", unignored["detail"].AsString(), StringComparison.Ordinal);
        Assert.All(new[] { missing, sharing, unknown, unignored }, r => Assert.Equal("warn", r["status"].AsString()));
    }

    [Fact]
    public void ASpawnRecordsTheMembersTmpPathOnItsTeamFileRow()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);

        var (code, spawned, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        var name = spawned["member"].AsObject()["name"].AsString();
        var row = JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", "proj.json"))).Member("members")!.Value.AsArray()
            .Single(m => m.Member("name")!.Value.AsString() == name);
        Assert.Equal(RecordedTmp(home, name)["root"].AsString(), row.Member("tmp")!.Value.AsString());
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static bool InsideGitWorkTree(string dir) =>
        PlatformModule.CreateProcessRunner().Run(new ProcessRequest(
            ["git", "-C", dir, "rev-parse", "--show-toplevel"],
            Environment: Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .Where(entry => !((string)entry.Key).StartsWith("GIT_", StringComparison.Ordinal))
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value, StringComparer.Ordinal),
            Timeout: TimeSpan.FromSeconds(10))).ExitCode == 0;

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void TheTmpdirAMemberIsGivenIsNotInsideAGitWorkTreeNorALinkIntoOne()
    {
        var home = SpawnTeamHome();
        WriteHomeDocument(home);
        MessagePoolResolutionTests.Git(Project, "init", "-q");
        Assert.True(InsideGitWorkTree(Project)); // negative: the check does see a work tree

        var (code, spawned, err) = SpawnInWorkTree(MakeWorkTree("t1"));

        Assert.True(code == 0, err);
        var tmp = RecordedTmp(home, spawned["member"].AsObject()["name"].AsString());
        var exported = tmp["alias"].AsString() is { Length: > 0 } alias ? alias : tmp["root"].AsString();
        Assert.True(Directory.Exists(exported));
        Assert.Null(new DirectoryInfo(exported).LinkTarget);
        Assert.False(InsideGitWorkTree(exported), exported);
    }

    [Fact]
    public void AResumeGivesTheRelaunchedMemberItsOwnTmpAndExportsTmpdir()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rs2");
        var snapshot = TakeStreamSnapshot(home, worktree);
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("fresh", row["status"].AsString());
        var member = JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", "proj.json"))).Member("members")!.Value.AsArray()[^1];
        var recorded = member.Member("tmp")!.Value.AsString();
        Assert.DoesNotContain("none (", recorded, StringComparison.Ordinal);
        Assert.True(Directory.Exists(recorded));
        Assert.Contains(_runner.Calls, argv => argv.Any(a => a.Contains("TMPDIR", StringComparison.Ordinal)));
    }

    [Fact]
    public void AResumeWhoseTmpCannotBeMadeGoesOnWithNoTmpdirAndRecordsNoneWithTheReason()
    {
        var home = ResumeHome();
        var worktree = FamilyWorkTree("rs1");
        var snapshot = TakeStreamSnapshot(home, worktree);
        _sandbox.PlantTeam(Path.Combine(home, "teams", "proj.json"), StreamTeamJson(string.Empty, worktree));
        DeleteGrants(home);
        File.WriteAllText(Path.Combine(home, "tmp"), "in the way");
        _runner.Calls.Clear();

        var (code, row, text) = ResumeYes(snapshot);

        Assert.True(code == 0, text);
        Assert.Equal("fresh", row["status"].AsString());
        var member = JsonText.Parse(_sandbox.TeamText(Path.Combine(home, "teams", "proj.json"))).Member("members")!.Value.AsArray()[0];
        Assert.StartsWith("none (", member.Member("tmp")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Any(a => a.Contains("TMPDIR", StringComparison.Ordinal)));
        Assert.Contains("cannot create member tmp", text, StringComparison.Ordinal);
    }

    private (int Code, string Stdout, string Stderr) CommitOnePeer() =>
        Roster(
            "create", "--commit", "--verified",
            "[{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"expected_root\":\"" + Project + "\"}]",
            "--transport", "terminal", "--roster-level", "repo", "--orchestrator-pid", Self);

    [Fact]
    public void ACreateWhoseOneMembersTmpFailsKeepsTheCompleteTeamAndRecordsThatMemberAtNone()
    {
        File.WriteAllText(Path.Combine(_sandbox.AhHome, "t"), "in the way");

        var (code, stdout, stderr) = CommitOnePeer();

        Assert.True(code == 0, stdout + stderr);
        var row = JsonText.Parse(_sandbox.TeamText(TeamPath("proj"))).Member("members")!.Value.AsArray()[0];
        Assert.StartsWith("none (", row.Member("tmp")!.Value.AsString(), StringComparison.Ordinal);
        Assert.True(HasGrants(Hierarchy));
        Assert.DoesNotContain("\"root\":\"\"", GrantsText(Hierarchy)!, StringComparison.Ordinal);
        Assert.Contains("started with no TMPDIR", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("resumed with no TMPDIR", stdout + stderr, StringComparison.Ordinal);
    }

    private JsonObject StreamEmptyRow(string members, string state, string opened)
    {
        var home = SpawnTeamHome();
        _sandbox.PlantTeam(
            Path.Combine(home, "teams", "proj.json"),
            LiveTeamJson("tmux", members).TrimEnd('}')
            + ",\"streams\":{\"s1\":{\"state\":\"" + state + "\",\"branch\":\"feat/s1\",\"base\":\"main\",\"worktree\":null,\"tab_id\":null,\"opened\":\"" + opened + "\"}}}");
        var (code, stdout, stderr) = Roster("doctor", "--team", "proj");
        Assert.True(code is 0 or 1, stdout + stderr);
        return JsonText.Parse(Encoding.UTF8.GetBytes(stdout)).AsObject()["rows"].AsArray()
            .Single(r => r.Member("name")!.Value.AsString() == "stream-empty").AsObject();
    }

    private static string StreamMember(string stream) =>
        $"{{\"role\":\"implementor\",\"name\":\"proj-implementor\",\"route\":\"peer\",\"transport_id\":\"%1\",\"model\":\"sonnet\",\"stream\":\"{stream}\"}}";

    [Fact]
    public void DoctorWarnsOfAnOpenStreamOlderThanThirtyMinutesWithNoMemberAndNamesStreamDone()
    {
        var row = StreamEmptyRow(string.Empty, "open", "2026-10-11T00:00:00.000Z");

        Assert.Equal("warn", row["status"].AsString());
        Assert.Contains("ah roster stream-done s1", row["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DoctorDoesNotWarnOfAStreamThatHasAMemberIsYoungerThanThirtyMinutesOrIsDone()
    {
        var young = StreamEmptyRow(string.Empty, "open", DateTimeOffset.Now.AddMinutes(-5).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        var joined = StreamEmptyRow(StreamMember("s1"), "open", "2026-10-11T00:00:00.000Z");
        var done = StreamEmptyRow(string.Empty, "done", "2026-10-11T00:00:00.000Z");

        Assert.All(new[] { young, joined, done }, r => Assert.Equal("ok", r["status"].AsString()));
    }
}
