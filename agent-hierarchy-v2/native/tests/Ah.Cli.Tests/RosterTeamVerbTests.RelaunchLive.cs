using Ah.Commands;
using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>Manual <c>resume --checkpoint --relaunch</c>: a bare member is told the working path, and every other member is taken from the live team file.</summary>
public sealed partial class RosterTeamVerbTests
{
    private string AllLaunchCalls() => string.Join("\n", _runner.Calls.Select(argv => string.Join(' ', argv)));

    private static string GoneMark(string session) =>
        MarkOf(999_999, SelfStart).Replace("\"s1\"", "\"" + session + "\"", StringComparison.Ordinal);

    private void SetRowField(string key, JsonValue value)
    {
        var team = JsonText.Parse(_sandbox.TeamText(ProjectTeamFile)).AsObject().Clone();
        var rows = new JsonArray();
        foreach (var row in team.Get("members")!.Value.AsArray())
        {
            var copy = row.AsObject().Clone();
            if (copy.Get("name")!.Value.AsString() == Impl)
            {
                copy[key] = value;
            }

            rows.Add(copy);
        }

        team["members"] = rows;
        _sandbox.PlantTeam(ProjectTeamFile, JsonText.Stringify(team, 2));
    }

    private string BareMemberRecord()
    {
        var row = OneImplementor.TrimEnd('}') + ",\"bare\":" + MarkOf(Environment.ProcessId, SelfStart) + "}";
        WriteTeam("proj", LiveTeamJson("tmux", row));
        ScriptCheckpointTrees();
        _panes.Default = PaneKnowledge.Present;
        Registered(ImplSession, live: true);
        _runner.Ps = argv => SelfStartOnly(argv) ?? (argv.Contains("command=") && argv.Contains(Self) ? "claude --resume s1\n" : null);
        return WriteRecord(null);
    }

    private static string RecordNaming(string record, string other)
    {
        File.WriteAllText(record, File.ReadAllText(record).Replace(Impl, other, StringComparison.Ordinal));
        return record;
    }

    [Fact]
    public void RelaunchOfABareMemberFailsAndNamesTheAskNowCommandWhenRecoveryIsOn()
    {
        var record = BareMemberRecord();

        var (code, _, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.NotEqual(0, code);
        Assert.Contains("ah roster recover now --team proj " + Impl, text, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window") || argv.Contains("send-keys") || argv.Contains("kill"));
    }

    [Fact]
    public void RelaunchOfABareMemberFailsAndSaysOnlyTheUserCanSwitchRecoveryOnWhenItIsOff()
    {
        var record = BareMemberRecord();
        Ah.TestKit.TestHomeDocs.Put(_sandbox.AhHome, Ah.Watch.WatchModule.RecoverySwitchKey, "{\"on\":false}");

        var (code, _, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.NotEqual(0, code);
        Assert.Contains("recovery is off, so nothing re-adopts it; only the user can switch it on (`ah roster recover enable`)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("recover now", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void NegativeAPlanOfABareMemberWithoutRelaunchStillSucceedsAndCarriesTheSameSentence()
    {
        var record = BareMemberRecord();

        var (code, result, text) = ResumeCheckpointOf(record);

        Assert.True(code == 0, text);
        Assert.Equal("bare", StatusOfMember(result));
        Assert.Contains("ah roster recover now --team proj " + Impl, text, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberSpawnedAfterTheCheckpointIsRelaunchedFromItsRowAndOneNotAskedForIsLeftOutOfTheAnswer()
    {
        var record = RecordNaming(PendingMemberWithASnapshot(PaneKnowledge.Absent), "proj-other");

        var (planCode, plan, planText) = ResumeCheckpointOf(record);
        Assert.True(planCode == 0, planText);
        Assert.DoesNotContain(plan.Member("members")!.Value.AsArray(), m => m.Member("name")!.Value.AsString() == Impl);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AStaleSessionInTheEntryLosesToTheTeamFilesAndTheAnswerNamesTheStaleEntry()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        SetRowField("bare", JsonText.Parse(GoneMark("sess-new")));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        var launched = AllLaunchCalls();
        Assert.Contains("--resume sess-new", launched, StringComparison.Ordinal);
        Assert.DoesNotContain("--resume " + ImplSession, launched, StringComparison.Ordinal);
        var stale = Assert.Single(result.Member("stale_entries")!.Value.AsArray()).AsString();
        Assert.Contains($"checkpoint entry for {Impl} is stale (session", stale, StringComparison.Ordinal);
        Assert.Contains("used the team file", stale, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeAnEntryWhoseSessionIsTheRowsIsNotStale()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        SetRowField("bare", JsonText.Parse(GoneMark(ImplSession)));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Null(result.Member("stale_entries"));
        Assert.Contains("--resume " + ImplSession, AllLaunchCalls(), StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleRootInTheEntryLosesToTheRowsRoot()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        DeleteGrants(ProjectHierarchyDir);
        SetRowField("bare", JsonText.Parse(GoneMark(ImplSession)));
        RecordExpectedRoot(FamilyWorkTree("rc3"));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Contains("expected_root", Assert.Single(result.Member("stale_entries")!.Value.AsArray()).AsString(), StringComparison.Ordinal);
        var launched = AllLaunchCalls();
        Assert.Contains("rc3", launched, StringComparison.Ordinal);
        Assert.DoesNotContain("rc2", launched, StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleEntryWhoseRowRecordsNoSessionIsRefusedOnceAndNothingIsLaunched()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);
        RecordExpectedRoot(FamilyWorkTree("rc3"));

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunch-refused", StatusOfMember(result));
        Assert.Contains("records no session", result.Member("members")!.Value.AsArray().Single().Member("why")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void AMemberInNeitherTheRecordNorTheTeamFileIsRefusedByName()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Absent);

        var (code, _, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", "proj-ghost");

        Assert.NotEqual(0, code);
        Assert.Contains("team proj has no member proj-ghost", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));
    }

    [Fact]
    public void TheDaemonRelaunchTakesTheLiveRowAndTheGivenSessionEvenWithANewerPoorCheckpointThatLacksTheMember()
    {
        _ = RestoredHerdrMember();
        var folder = Path.Combine(ProjectHierarchyDir, "checkpoints", "proj");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "2099-01-01T00-00-00.000Z.json"),
            "{\"team\":\"proj\",\"snapshot\":\"/nowhere/gone.json\",\"members\":[{\"name\":\"proj-other\",\"session\":\"sess-old\",\"open_requests\":[]}]}");

        var failure = DaemonRelaunch();

        Assert.True(failure is null, failure);
        var launch = Assert.Single(LaunchLines());
        Assert.Contains("--resume " + RelaunchSession, launch, StringComparison.Ordinal);
        Assert.Contains("--pane pA", launch, StringComparison.Ordinal);
        Assert.DoesNotContain("sess-old", launch, StringComparison.Ordinal);
    }

    /// <summary>
    /// A relaunch never types into the recorded pane (b238 S1.c): one still there with no harness is untied on herdr TUI, so it is
    /// neither split nor closed and the member takes normal placement; one gone by launch time is placed as any spawn.
    /// </summary>
    [Theory]
    [InlineData(PaneKnowledge.Present, "untied, left open")]
    [InlineData(PaneKnowledge.Absent, "absent")]
    public void NegativeTheRecordedHerdrPaneIsNeverLaunchedIntoOrClosedWhetherOrNotItIsThereAtLaunchTime(PaneKnowledge atLaunch, string oldPane)
    {
        _ = RestoredHerdrMember();
        _panes.Default = PaneKnowledge.Present;
        _panes.AfterRefresh = atLaunch;
        var record = WriteRecord(null, session: null);

        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.DoesNotContain(LaunchLines(), l => l.Contains("--pane pA", StringComparison.Ordinal));
        Assert.DoesNotContain(_runner.Calls, argv => (argv.Contains("close") || argv.Contains("split")) && argv.Contains("pA"));
        Assert.Contains($"\"old_pane\": \"{oldPane}\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeadMemberLeftBehindByARefusedRelaunchIsLaunchedByHandOnceItsPaneIsAPlainShell()
    {
        var record = PendingMemberWithASnapshot(PaneKnowledge.Present);
        _panes.Harness = true;
        var (firstCode, first, firstText) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);
        Assert.True(firstCode == 0, firstText);
        Assert.Equal("relaunch-refused", StatusOfMember(first));
        Assert.DoesNotContain(_runner.Calls, argv => argv.Contains("new-window"));

        _panes.Harness = false;
        var (code, result, text) = ResumeCheckpointOf(record, "--yes", "--relaunch", Impl);

        Assert.True(code == 0, text);
        Assert.Equal("relaunched", StatusOfMember(result));
        Assert.Contains(_runner.Calls, argv => argv.Contains("new-window"));
    }
}
