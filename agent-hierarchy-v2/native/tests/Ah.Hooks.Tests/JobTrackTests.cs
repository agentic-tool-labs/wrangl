using System.Text;
using Ah.Core.Json;
using Ah.Hooks.Activity;
using Ah.Hooks.Plumbing;
using Ah.Watch;

namespace Ah.Hooks.Tests;

/// <summary>The job registrar: which Bash calls become tracked jobs, whose they are, and that it never gets in the way.</summary>
public sealed class JobTrackTests
{
    private static HooksHostTests.Rig Exec(HooksHostTests.Rig rig)
    {
        rig.Writer.Executing = rig.Services.Hierarchy.StateDomains.Where(d => d.Name == "jobs").ToList();
        return rig;
    }

    private static GateContext Ctx(HooksHostTests.Rig rig, string phase, string command, string toolUse = "toolu_1", bool background = false, string? agentId = null)
    {
        var input = new JsonObject { ["command"] = command, ["run_in_background"] = background };
        var evt = new JsonObject
        {
            ["hook_event_name"] = phase,
            ["session_id"] = "s-a",
            ["cwd"] = rig.Sandbox.Root + "/proj",
            ["tool_name"] = "Bash",
            ["tool_use_id"] = toolUse,
            ["tool_input"] = input,
        };
        if (agentId is not null)
        {
            evt["agent_id"] = agentId;
        }

        return new GateContext(rig.Services, JsonText.Parse(Encoding.UTF8.GetBytes(JsonText.Stringify(evt, 0))), "job-track");
    }

    private static string JobsDir(HooksHostTests.Rig rig) => Path.Combine(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "jobs", "tm");

    // per-dir-files-to-db S8: the records are the team's jobs rows, seeded through jobs.put
    private static JsonObject[] Records(HooksHostTests.Rig rig) => [.. WatchModule.JobRecordFiles(rig.Services.Hierarchy.Status, JobsDir(rig)).Select(static f => f.Record)];

    private static JsonObject Only(HooksHostTests.Rig rig) => Assert.Single(Records(rig));

    private static void Seed(HooksHostTests.Rig rig, JsonObject record) =>
        rig.Services.Hierarchy.StateCalls.PutJob(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "tm", record["id"].AsString(), record);

    private static bool Ended(JsonObject job) => job.Get("ended_at") is { IsText: true };

    [Fact]
    public void ACatalogueCommandOfTheOrchestratorBecomesAJobAndItsPostToolUseEndsIt()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x.csproj"));
        var started = Only(rig);
        Assert.Equal("orchestrator", started["owner"].AsString());
        Assert.Equal("test", started["kind"].AsString());
        Assert.Equal("large", started["eta"].AsString());
        Assert.Equal("toolu_1", started["tool_use_id"].AsString());
        Assert.Equal("s-a", started["session_id"].AsString());
        Assert.False(Ended(started));

        _ = JobTrack.Run(Ctx(rig, "PostToolUse", "dotnet test x.csproj"));
        Assert.True(Ended(Only(rig)));
    }

    [Fact]
    public void AMembersJobIsFiledUnderTheMemberAndItsTeam()
    {
        var rig = Exec(AutoTrackTests.MemberRig("p1"));

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet build src/x.csproj"));

        var job = Only(rig);
        Assert.Equal("implementor-1", job["owner"].AsString());
        Assert.Equal("tm", job["team"].AsString());
        Assert.Equal("build", job["kind"].AsString());
    }

    [Fact]
    public void AJobsReadThatCannotAnswerRegistersNothingAndIsLoggedNeverADuplicate()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x.csproj"));
        _ = Only(rig);

        // a database that exists but cannot be read stands in for one still busy after the retry: the same throw
        var db = Path.Join(rig.Services.Environment.AhHome, "state", "ah.db");
        File.Delete(db + "-wal");
        File.Delete(db + "-shm");
        File.WriteAllText(db, "not a database");

        var outcome = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test y.csproj", toolUse: "toolu_2"));

        Assert.Equal(HookEmit.Silent, outcome);
        Assert.StartsWith("\"the state database is busy; try again", Assert.Single(rig.Errors.Records).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("echo dotnet test")]
    [InlineData("git status")]
    public void AnOrdinaryCommandMakesNoRecord(string command)
    {
        var rig = Exec(AutoTrackTests.OwnerRig());

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", command));

        Assert.Empty(Records(rig));
    }

    [Fact]
    public void AnyBackgroundCommandIsABackgroundJobAndItsPostToolUseLeavesItOpen()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "sleep 300", background: true));
        _ = JobTrack.Run(Ctx(rig, "PostToolUse", "sleep 300", background: true));

        var job = Only(rig);
        Assert.True(job["background"].AsBool());
        Assert.Equal("bg", job["kind"].AsString());
        Assert.False(Ended(job));
    }

    [Fact]
    public void APostToolUseOfAnotherToolCallEndsNothing()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x", toolUse: "toolu_1"));

        _ = JobTrack.Run(Ctx(rig, "PostToolUse", "dotnet test x", toolUse: "toolu_2"));

        Assert.False(Ended(Only(rig)));
    }

    [Fact]
    public void APostToolUseEndsTheRowOfItsToolCallWhateverOwnerAndSessionTheRowRecords()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x", toolUse: "toolu_9"));
        var row = Only(rig).Clone();
        row["owner"] = "implementor-9";
        row["session_id"] = "s-old";
        Seed(rig, row);

        _ = JobTrack.Run(Ctx(rig, "PostToolUse", "dotnet test x", toolUse: "toolu_9"));

        Assert.True(Ended(Only(rig)));
    }

    [Fact]
    public void ASubagentsCallIsNotTheSessionsJob()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x", agentId: "agent-9"));

        Assert.Empty(Records(rig));
    }

    [Fact]
    public void ASessionThatIsNeitherAMemberNorAnOrchestratorMakesNoRecord()
    {
        var rig = Exec(AutoTrackTests.OwnerRig(owns: false));

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x"));

        Assert.Empty(Records(rig));
    }

    [Fact]
    public void ARepeatedPreToolUseOfTheSameToolCallMakesOneRecord()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x"));
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x"));

        _ = Assert.Single(Records(rig));
    }

    [Fact]
    public void AFailureNeverBlocksTheCall()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        var jobs = Path.Combine(rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "jobs");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(jobs)!);
        File.WriteAllText(jobs, "a file where the jobs directory should be");

        var outcome = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x"));

        Assert.Equal(HookEmit.Silent, outcome);
    }

    [Theory]
    [InlineData("/bin/sleep 600", false)]
    [InlineData("/bin/sleep 600 && make", true)]
    [InlineData("@AH roster wake --team tm", false)]
    [InlineData("@AH roster wake; dotnet test", true)]
    [InlineData("@AH roster resource claim x", false)]
    [InlineData("@AH roster resource list", true)]
    public void ABackgroundWakeSourceOrBareSleepMakesNoRecordAndALookalikeOrChainDoes(string command, bool becomesAJob)
    {
        var rig = Exec(AutoTrackTests.OwnerRig());

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", command.Replace("@AH", Environment.ProcessPath, StringComparison.Ordinal), background: true));

        Assert.Equal(becomesAJob ? 1 : 0, Records(rig).Length);
    }


    [Fact]
    public void TheOwnersStopOrNextPromptEndsItsOpenForegroundJobsAsTurnEndedAndLeavesBackgroundOnesAndOthersAlone()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x.csproj", toolUse: "toolu_fg"));
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "sleep 900 && echo", toolUse: "toolu_bg", background: true));

        _ = JobTrack.Run(Ctx(rig, "Stop", string.Empty));

        var byTool = Records(rig).ToDictionary(static j => j["tool_use_id"].AsString());
        Assert.True(Ended(byTool["toolu_fg"]));
        Assert.Equal("turn-ended", byTool["toolu_fg"]["exit"].AsString());
        Assert.False(Ended(byTool["toolu_bg"]));

        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet build x.csproj", toolUse: "toolu_fg2"));
        _ = JobTrack.Run(Ctx(rig, "UserPromptSubmit", string.Empty));
        Assert.True(Records(rig).Single(static j => j["tool_use_id"].AsString() == "toolu_fg2").Get("ended_at") is { IsText: true });
    }


    [Fact]
    public void AnotherSessionsOpenForegroundJobIsNotEndedByThisSessionsStop()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x.csproj", toolUse: "toolu_mine"));
        var other = new JsonObject
        {
            ["id"] = "20261021-120000-other", ["team"] = "tm", ["owner"] = "orchestrator", ["session_id"] = "s-b", ["kind"] = "test", ["label"] = "dotnet test",
            ["pid"] = JsonValue.Null, ["eta"] = "large", ["sent_at"] = "2026-10-21T12:00:00.000Z", ["background"] = false, ["tool_use_id"] = "toolu_other",
        };
        Seed(rig, other);

        _ = JobTrack.Run(Ctx(rig, "Stop", string.Empty));

        var byTool = Records(rig).ToDictionary(static j => j["tool_use_id"].AsString());
        Assert.True(Ended(byTool["toolu_mine"]));
        Assert.False(Ended(byTool["toolu_other"]));
    }


    [Fact]
    public void AFailureEventEndsThatForegroundJobAsFailedAndNoOtherAndABackgroundOneIsNotEnded()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x.csproj", toolUse: "toolu_a"));
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet build x.csproj", toolUse: "toolu_b"));
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "sleep 900 && echo", toolUse: "toolu_bg", background: true));

        _ = JobTrack.Run(Ctx(rig, "PostToolUseFailure", "dotnet test x.csproj", toolUse: "toolu_a"));
        _ = JobTrack.Run(Ctx(rig, "PostToolUseFailure", "sleep 900 && echo", toolUse: "toolu_bg", background: true));

        var byTool = Records(rig).ToDictionary(static j => j["tool_use_id"].AsString());
        Assert.True(Ended(byTool["toolu_a"]));
        Assert.Equal("failed", byTool["toolu_a"]["exit"].AsString());
        Assert.False(Ended(byTool["toolu_b"]));
        Assert.False(Ended(byTool["toolu_bg"]));
    }


    [Theory]
    [InlineData("@D", false)]
    [InlineData(":@D", true)]
    [InlineData("rel/bin:@D", true)]
    [InlineData("/no/such/dir:@D", false)]
    public void ABareAhIsAWakeSourceOnlyWhenNoRelativeOrEmptyPathEntryComesBeforeTheRealOne(string path, bool becomesAJob)
    {
        var bin = Path.Combine(Path.GetTempPath(), "ahprobe.n5." + Guid.NewGuid().ToString("N")[..8]);
        _ = Directory.CreateDirectory(bin);
        try
        {
            File.CreateSymbolicLink(Path.Combine(bin, "ah"), Environment.ProcessPath!);
            var rig = Exec(AutoTrackTests.OwnerRig(env: new Dictionary<string, string> { ["PATH"] = path.Replace("@D", bin, StringComparison.Ordinal) }));

            _ = JobTrack.Run(Ctx(rig, "PreToolUse", "ah roster wake --team tm", background: true));

            Assert.Equal(becomesAJob ? 1 : 0, Records(rig).Length);
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    [Theory]
    [InlineData("@ROOT/bin/ah roster wake --cwd /p", false)]
    [InlineData("\"@ROOT/bin/ah\" roster overdue --team tm", false)]
    [InlineData("@ROOT/bin/ah roster status --cwd /p", true)]
    [InlineData("@OTHER/bin/ah roster wake --cwd /p", true)]
    [InlineData("sleep 900 && make", true)]
    public void ThePluginsOwnLauncherWakesWithNoJobOrAlarmAndARealOrLookalikeCommandStillAlarms(string command, bool alarms)
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "ahprobe.wake." + Guid.NewGuid().ToString("N")[..8]);
        var root = Path.Combine(sandbox, "plugin");
        var other = Path.Combine(sandbox, "other");
        foreach (var dir in new[] { root, other })
        {
            _ = Directory.CreateDirectory(Path.Combine(dir, "bin"));
            File.WriteAllText(Path.Combine(dir, "bin", "ah"), "#!/bin/sh\n");
        }

        try
        {
            var rig = Exec(AutoTrackTests.OwnerRig(pluginRoot: root));

            _ = JobTrack.Run(Ctx(rig, "PreToolUse", command.Replace("@ROOT", root, StringComparison.Ordinal).Replace("@OTHER", other, StringComparison.Ordinal), background: true));

            Assert.Equal(alarms ? 1 : 0, Records(rig).Length);
            var jobs = WatchModule.JobsFromFiles(rig.Services.Hierarchy.Status,rig.Services.Config.HierarchyDir(rig.Sandbox.Root + "/proj"), "tm");
            var dayLater = rig.Services.Clock.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
            var overdue = WatchModule.JobOverdueOf(jobs, "orchestrator", eta => rig.Services.Hierarchy.Messages.ThresholdFor(JsonValue.FromString(eta)), dayLater, 30000);
            Assert.Equal(alarms, overdue is not null);
        }
        finally
        {
            Directory.Delete(sandbox, recursive: true);
        }
    }

    [Fact]
    public void AForegroundRecordOfKindBgWithNoFlagIsBackgroundAndSurvivesTheStop()
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        _ = JobTrack.Run(Ctx(rig, "PreToolUse", "dotnet test x.csproj", toolUse: "toolu_fg"));
        var bg = new JsonObject
        {
            ["id"] = "20261021-120000-kindbg", ["team"] = "tm", ["owner"] = "orchestrator", ["session_id"] = "s-a", ["kind"] = "bg", ["label"] = "sleep 9",
            ["pid"] = JsonValue.Null, ["eta"] = "large", ["sent_at"] = "2026-10-21T12:00:00.000Z", ["tool_use_id"] = "toolu_kindbg",
        };
        Seed(rig, bg);

        _ = JobTrack.Run(Ctx(rig, "Stop", string.Empty));

        var byTool = Records(rig).ToDictionary(static j => j["tool_use_id"].AsString());
        Assert.True(Ended(byTool["toolu_fg"]));
        Assert.False(Ended(byTool["toolu_kindbg"]));
    }


    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public void ASelfRegisteredForegroundRecordWithALivePidSurvivesTheStopAndOneWithADeadPidEnds(bool alive, bool toolUse, bool endsAtStop)
    {
        var rig = Exec(AutoTrackTests.OwnerRig());
        var record = new JsonObject
        {
            ["id"] = "20261021-120000-selfreg", ["team"] = "tm", ["owner"] = "orchestrator", ["session_id"] = "s-a", ["kind"] = "test", ["label"] = "dotnet test",
            ["pid"] = alive ? Environment.ProcessId : 2_000_000_000, ["eta"] = "large", ["sent_at"] = "2026-10-21T12:00:00.000Z", ["background"] = false,
        };
        if (toolUse)
        {
            record["tool_use_id"] = "toolu_x";
        }

        Seed(rig, record);

        _ = JobTrack.Run(Ctx(rig, "Stop", string.Empty));

        Assert.Equal(endsAtStop, Ended(Only(rig)));
    }

}
