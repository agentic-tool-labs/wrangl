using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>The job verbs, driven through the roster command line as the Orchestrator of team tm.</summary>
public sealed partial class RosterOwnerGuardTests
{
    private string JobsFolder => Path.Combine(Hierarchy, "jobs", "tm");

    // per-dir-files-to-db S8: the records are team tm's jobs rows, each named by its old file path
    private string[] JobFiles() => [.. Ah.Watch.WatchModule.JobRecordFiles(StoreGraph().Hierarchy.Status, JobsFolder).Select(f => Path.Combine(JobsFolder, f.Id + ".json"))];

    private JsonObject JobRecord(string id) => Ah.Watch.WatchModule.JobRecordFiles(StoreGraph().Hierarchy.Status, JobsFolder).Single(f => f.Id == id).Record;

    private string JobText(string id) => JsonText.Stringify(JobRecord(id), 0);

    private void AsSession(string session)
    {
        TeamOwnedBy(Self);
        _env["CLAUDE_CODE_SESSION_ID"] = session;
    }

    [Fact]
    public void AStartWritesOneRecordOfTheCallersAndPrintsItsId()
    {
        AsSession("sess-a");

        var run = Run("job", "start", "--kind", "test", "--label", "dotnet test Core", "--team", "tm");

        Assert.True(run.Code == 0, run.Stderr + run.Stdout);
        var id = run.Stdout.Trim();
        var file = Assert.Single(JobFiles());
        Assert.Equal(id + ".json", Path.GetFileName(file));
        var record = JobRecord(id);
        Assert.Equal("orchestrator", record["owner"].AsString());
        Assert.Equal("sess-a", record["session_id"].AsString());
        Assert.Equal("test", record["kind"].AsString());
        Assert.Equal("large", record["eta"].AsString());
        Assert.False(record["background"].AsBool());
        Assert.True(record.Get("sent_at") is { IsText: true });
    }

    [Fact]
    public void ABuildDefaultsToMediumAndABackgroundKindIsBackground()
    {
        AsSession("sess-a");

        var build = Run("job", "start", "--kind", "build", "--label", "dotnet build", "--team", "tm").Stdout.Trim();
        var bg = Run("job", "start", "--kind", "bg", "--label", "sleep", "--team", "tm").Stdout.Trim();

        Assert.Equal("medium", JobRecord(build)["eta"].AsString());
        Assert.False(JobRecord(build)["background"].AsBool());
        Assert.True(JobRecord(bg)["background"].AsBool());
    }

    [Fact]
    public void AnEndStampsTheRecordAndAnExtendMovesItsEta()
    {
        AsSession("sess-a");
        var id = Run("job", "start", "--kind", "test", "--label", "t", "--eta", "small", "--team", "tm").Stdout.Trim();

        var extend = Run("job", "extend", id, "--eta", "large", "--team", "tm");
        var end = Run("job", "end", id, "--exit", "0", "--team", "tm");

        Assert.Equal(0, extend.Code);
        Assert.Equal(0, end.Code);
        var record = JobRecord(id);
        Assert.Equal("large", record["eta"].AsString());
        Assert.True(record.Get("extended_at") is { IsText: true });
        Assert.True(record.Get("ended_at") is { IsText: true });
        Assert.Equal("0", record["exit"].AsString());
    }

    [Fact]
    public void AnotherSessionCannotEndOrExtendTheJobAndNothingIsChanged()
    {
        AsSession("sess-a");
        var id = Run("job", "start", "--kind", "test", "--label", "t", "--team", "tm").Stdout.Trim();
        var before = JobText(id);
        _env["CLAUDE_CODE_SESSION_ID"] = "sess-b";

        var end = Run("job", "end", id, "--team", "tm");
        var extend = Run("job", "extend", id, "--eta", "small", "--team", "tm");

        Assert.NotEqual(0, end.Code);
        Assert.NotEqual(0, extend.Code);
        Assert.Contains("not this session's job", end.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, JobText(id));
    }

    [Theory]
    [InlineData("--kind", "tst")]
    [InlineData("--kind", "")]
    public void AnUnknownKindIsRefusedAndNothingIsWritten(string flag, string value)
    {
        AsSession("sess-a");

        var run = Run("job", "start", flag, value, "--label", "t", "--team", "tm");

        Assert.NotEqual(0, run.Code);
        Assert.Empty(JobFiles());
    }

    [Fact]
    public void AStartWithNoLabelOrABadEtaIsRefusedAndNothingIsWritten()
    {
        AsSession("sess-a");

        var noLabel = Run("job", "start", "--kind", "test", "--team", "tm");
        var badEta = Run("job", "start", "--kind", "test", "--label", "t", "--eta", "huge", "--team", "tm");

        Assert.NotEqual(0, noLabel.Code);
        Assert.NotEqual(0, badEta.Code);
        Assert.Empty(JobFiles());
    }

    [Fact]
    public void ASecondStartForTheSameToolCallMergesIntoOneRecordWhileAnotherToolCallMakesAnother()
    {
        AsSession("sess-a");

        var first = Run("job", "start", "--kind", "test", "--label", "t", "--tool-use-id", "toolu_1", "--team", "tm").Stdout.Trim();
        var again = Run("job", "start", "--kind", "test", "--label", "t", "--tool-use-id", "toolu_1", "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--team", "tm").Stdout.Trim();
        Assert.Equal(first, again);
        _ = Assert.Single(JobFiles());
        Assert.Equal(Environment.ProcessId, (int)JobRecord(first)["pid"].AsNumber());
        Assert.Equal("Wed Oct 21 08:00:05 2026", JobRecord(first)["pid_start"].AsString());

        var other = Run("job", "start", "--kind", "test", "--label", "t", "--tool-use-id", "toolu_2", "--team", "tm").Stdout.Trim();
        Assert.NotEqual(first, other);
        Assert.Equal(2, JobFiles().Length);
    }

    [Fact]
    public void AnEndedJobIsNotMergedIntoByALaterStartOfTheSameToolCall()
    {
        AsSession("sess-a");
        var first = Run("job", "start", "--kind", "test", "--label", "t", "--tool-use-id", "toolu_1", "--team", "tm").Stdout.Trim();
        _ = Run("job", "end", first, "--team", "tm");

        var second = Run("job", "start", "--kind", "test", "--label", "t", "--tool-use-id", "toolu_1", "--team", "tm").Stdout.Trim();

        Assert.NotEqual(first, second);
    }

    [Theory]
    [InlineData("20261021-120000-zzzz")]
    [InlineData("../x")]
    public void AnUnknownOrEscapingIdIsRefused(string id)
    {
        AsSession("sess-a");

        var run = Run("job", "end", id, "--team", "tm");

        Assert.NotEqual(0, run.Code);
        Assert.Empty(JobFiles());
    }

    [Fact]
    public void AStartWithAPidRecordsWhenThatProcessStarted()
    {
        AsSession("sess-a");

        var id = Run("job", "start", "--kind", "test", "--label", "t", "--pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--team", "tm").Stdout.Trim();

        Assert.Equal(Environment.ProcessId, (int)JobRecord(id)["pid"].AsNumber());
        Assert.Equal("Wed Oct 21 08:00:05 2026", JobRecord(id)["pid_start"].AsString());
    }

    [Fact]
    public void APidThatIsNotRunningIsRefusedOnStartAndOnMergeAndNothingIsWritten()
    {
        AsSession("sess-a");

        var fresh = Run("job", "start", "--kind", "test", "--label", "t", "--pid", "2000000000", "--team", "tm");
        var first = Run("job", "start", "--kind", "test", "--label", "t", "--tool-use-id", "toolu_1", "--team", "tm").Stdout.Trim();
        var before = JobText(first);
        var merge = Run("job", "start", "--kind", "test", "--label", "t", "--tool-use-id", "toolu_1", "--pid", "2000000000", "--team", "tm");

        Assert.NotEqual(0, fresh.Code);
        Assert.NotEqual(0, merge.Code);
        Assert.Contains("not a running process", fresh.Stderr, StringComparison.Ordinal);
        Assert.Equal(before, JobText(first));
        _ = Assert.Single(JobFiles());
    }

    [Fact]
    public void TeamsMembersGivesEachMemberItsIdsAndOneVerdictAndPlainTeamsDoesNot()
    {
        TeamOwnedBy(Self);
        var path = Path.Combine(Hierarchy, "teams", "tm.json");
        _sandbox.PlantTeam(path, StoredTeam(path).Replace("\"members\":[]", "\"members\":[{\"name\":\"m1\",\"role\":\"implementor\",\"kind\":\"claude\",\"transport_id\":\"w1-1\"}]", StringComparison.Ordinal));

        var row = JsonText.Parse(Run("teams", "--members").Stdout).Member("teams")!.Value.AsArray()[0];
        var member = row.Member("member_status")!.Value.AsArray()[0];

        Assert.Equal("m1", member.Member("name")!.Value.AsString());
        Assert.Equal("w1-1", member.Member("pane_id")!.Value.AsString());
        Assert.Contains(member.Member("verdict")!.Value.AsString(), (string[])["live", "dead", "unknown"]);
        Assert.Null(JsonText.Parse(Run("teams").Stdout).Member("teams")!.Value.AsArray()[0].Member("member_status"));
    }

    [Fact]
    public void ASessionThatIsNeitherARegisteredMemberNorAnOrchestratorCannotRunTheWaiterAndWritesNoWakeFile()
    {
        AsSession("sess-a");

        var run = Run("wake", "--team", "tm");

        Assert.NotEqual(0, run.Code);
        Assert.Contains("only a registered member or a team's Orchestrator runs the waiter", run.Stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.Combine(_sandbox.AhHome, "wake")) && Directory.GetFiles(Path.Combine(_sandbox.AhHome, "wake")).Length > 0);
    }
}
