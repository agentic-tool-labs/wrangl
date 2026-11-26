using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>
/// ah2-570 G3: the Claude environment a spawn launches a member in. A member's own recorded row wins; else the team's lead's own
/// environment, which the team record then takes; else the team record's; else the caller's.
/// </summary>
public sealed partial class RosterTeamVerbTests
{
    private const string ConfigField = "claude_config_dir";

    /// <summary>A live tmux team owned by this process whose record holds <paramref name="teamValue"/>.</summary>
    private string TeamWithClaudeConfig(string teamValue) =>
        LiveTeamJson("tmux", string.Empty).TrimEnd('}') + ",\"" + ConfigField + "\":" + JsonText.Quote(teamValue) + "}";

    /// <summary>The text tmux was told to type for the spawn: the launch line's single-quoted word, as the shell reads it.</summary>
    private string TypedLaunch()
    {
        var line = Assert.Single(_runner.Calls, argv => string.Join(' ', argv).Contains("send-keys", StringComparison.Ordinal))[^1];
        var from = line.IndexOf('\'', StringComparison.Ordinal);
        var to = line.LastIndexOf("' Enter", StringComparison.Ordinal);
        Assert.True(from >= 0 && to > from, line);
        return line[(from + 1)..to].Replace("'\\''", "'", StringComparison.Ordinal);
    }

    private JsonValue TeamRecord() => JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));

    private static string? Recorded(JsonValue? doc) => doc?.Member(ConfigField) is { IsText: true } v ? v.AsString() : null;

    private JsonValue ImplementorRow() =>
        TeamRecord().Member("members")!.Value.AsArray().Single(m => m.Member("role")?.AsString() == "implementor");

    private void SpawnFor(string expectDescription)
    {
        AgeSpawnedRows();
        var (code, stdout, stderr) = Roster(["spawn-ad-hoc", "implementor", "--model", "sonnet", "--team", "proj", "--orchestrator-pid", Self]);
        Assert.True(code == 0, expectDescription + ": " + stdout + stderr);
    }

    [Fact]
    public void TheLeadsOwnEnvironmentBeatsTheTeamRecordAndUpdatesIt()
    {
        OnTmux();
        WriteTeam("proj", TeamWithClaudeConfig("/team/cfg"));
        _extraEnv["CLAUDE_CONFIG_DIR"] = "/lead/cfg";

        SpawnFor("lead spawn");

        Assert.Contains("export CLAUDE_CONFIG_DIR='/lead/cfg'; unset CLAUDE_SECURESTORAGE_CONFIG_DIR; claude ", TypedLaunch(), StringComparison.Ordinal);
        Assert.Equal("/lead/cfg", Recorded(TeamRecord()));
        Assert.Equal("/lead/cfg", Recorded(ImplementorRow()));
    }

    [Fact]
    public void ALeadWithNoValueUnsetsItForTheMemberAndRecordsTheEmptyValue()
    {
        OnTmux();
        WriteTeam("proj", TeamWithClaudeConfig("/team/cfg"));

        SpawnFor("lead spawn without CLAUDE_CONFIG_DIR");

        Assert.Contains("unset CLAUDE_CONFIG_DIR; unset CLAUDE_SECURESTORAGE_CONFIG_DIR; claude ", TypedLaunch(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, Recorded(TeamRecord()));
        Assert.Equal(string.Empty, Recorded(ImplementorRow()));
    }

    [Fact]
    public void ALeadWithOnlyTheLoginVariableCarriesItAndUnsetsTheConfigDirectory()
    {
        // claude-env Amendment 4: a team record written by 2.0.55 holds only claude_config_dir; the login variable is the lead's
        OnTmux();
        WriteTeam("proj", TeamWithClaudeConfig(string.Empty));
        _extraEnv["CLAUDE_SECURESTORAGE_CONFIG_DIR"] = "/login/a b'c";

        SpawnFor("lead spawn with only the login variable");

        Assert.Contains("unset CLAUDE_CONFIG_DIR; export CLAUDE_SECURESTORAGE_CONFIG_DIR='/login/a b'\\''c'; claude ", TypedLaunch(), StringComparison.Ordinal);
        Assert.Equal("/login/a b'c", TeamRecord().Member("claude_securestorage_config_dir")?.AsString());
        Assert.Equal("/login/a b'c", ImplementorRow().Member("claude_securestorage_config_dir")?.AsString());
    }

    /// <summary>
    /// The daemon's relaunch of the restored herdr implementor, the daemon's own environment holding <c>/daemon/cfg</c>; the row and the
    /// team record are given their values first, and the team is owned by another process when <paramref name="otherOwner"/>.
    /// </summary>
    private List<string> DaemonRelaunchWith(string? rowValue, string? teamValue, bool otherOwner)
    {
        _ = RestoredHerdrMember();
        var text = _sandbox.TeamText(ProjectTeamFile);
        if (rowValue is not null)
        {
            text = text.Replace("\"transport_id\":\"pA\"", "\"transport_id\":\"pA\",\"" + ConfigField + "\":" + JsonText.Quote(rowValue), StringComparison.Ordinal);
        }

        if (teamValue is not null)
        {
            text = text.TrimEnd('}') + ",\"" + ConfigField + "\":" + JsonText.Quote(teamValue) + "}";
        }

        if (otherOwner)
        {
            text = text.Replace("\"pid\":" + Self, "\"pid\":" + LivePidOfAnotherProcess().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        _sandbox.PlantTeam(ProjectTeamFile, text);
        _extraEnv["CLAUDE_CONFIG_DIR"] = "/daemon/cfg";

        var failure = DaemonRelaunch();

        Assert.True(failure is null, failure);
        return _runner.Calls.Select(argv => string.Join(' ', argv)).ToList();
    }

    private static void ExportedBeforeTheStart(List<string> calls, string exported)
    {
        var export = calls.IndexOf("herdr pane run pA export CLAUDE_CONFIG_DIR=" + exported + "; unset CLAUDE_SECURESTORAGE_CONFIG_DIR");
        var start = calls.FindIndex(c => c.Contains("agent start " + Impl, StringComparison.Ordinal));
        Assert.True(export >= 0 && start > export, string.Join(" | ", calls));
    }

    [Fact]
    public void AResumeOfARecordedMemberUsesItsRowsValueNotTheCallersOrTheTeams()
    {
        // E2: the daemon's relaunch resumes the member in its recorded pane, in the Claude environment its row records
        var calls = DaemonRelaunchWith("/row/cfg", "/team/cfg", otherOwner: true);

        ExportedBeforeTheStart(calls, "/row/cfg");
        Assert.DoesNotContain(calls, c => c.Contains("/daemon/cfg", StringComparison.Ordinal) || c.Contains("/team/cfg", StringComparison.Ordinal));
    }

    [Fact]
    public void ACallerWhoIsNotTheLeadGetsTheTeamRecordsValueAndLeavesItAlone()
    {
        var calls = DaemonRelaunchWith(null, "/team/cfg", otherOwner: true);

        ExportedBeforeTheStart(calls, "/team/cfg");
        Assert.DoesNotContain(calls, c => c.Contains("/daemon/cfg", StringComparison.Ordinal));
        Assert.Equal("/team/cfg", Recorded(JsonText.Parse(_sandbox.TeamText(ProjectTeamFile))));
    }

    [Fact]
    public void ADaemonRelaunchWithNothingRecordedLaunchesWithTheDaemonsValueAndRecordsNothing()
    {
        // ah2-578 E1: the daemon is not the lead, so its own value is used for this launch only
        var calls = DaemonRelaunchWith(null, null, otherOwner: true);

        ExportedBeforeTheStart(calls, "/daemon/cfg");
        var team = JsonText.Parse(_sandbox.TeamText(ProjectTeamFile));
        var row = team.Member("members")!.Value.AsArray().Single(m => m.Member("name")?.AsString() == Impl);
        Assert.Null(Recorded(row));
        Assert.Null(row.Member("claude_securestorage_config_dir"));
        Assert.Null(Recorded(team));
    }

    // ---------------------------------------------------------------- E5: the doctor names a live member in another environment

    /// <summary>The <c>claude-env</c> doctor row for a team whose lead records <c>/lead/cfg</c> and whose one member records <paramref name="memberValue"/>; null when there is none.</summary>
    private JsonValue? ClaudeEnvRow(string memberValue, bool live, string field = ConfigField)
    {
        const string Name = "proj-implementor";
        if (live)
        {
            PeerUp(Name, "%3");
        }

        var row = "{\"role\":\"implementor\",\"name\":\"" + Name + "\",\"route\":\"peer\",\"transport_id\":\"%3\",\"model\":\"sonnet\",\"expected_root\":\"" + Project + "\",\"" + field + "\":" + JsonText.Quote(memberValue) + "}";
        WriteTeam("proj", LiveTeamJson("tmux", row).TrimEnd('}') + ",\"" + field + "\":\"/lead/cfg\"}");
        var (_, stdout, stderr) = Roster("doctor", "--team", "proj", "--orchestrator-pid", Self);
        var rows = JsonText.Parse(stdout).Member("rows")?.AsArray() ?? throw new InvalidOperationException(stdout + stderr);
        return rows.Where(r => r.Member("name")?.AsString() == "claude-env").Select(r => (JsonValue?)r).SingleOrDefault();
    }

    [Fact]
    public void TheDoctorNamesALiveMemberWhoseClaudeEnvironmentDiffersFromTheLeadsAsInformation()
    {
        var row = ClaudeEnvRow("/other/cfg", live: true);

        Assert.NotNull(row);
        Assert.Equal("ok", row.Value.Member("status")!.Value.AsString());
        Assert.Equal("proj-implementor runs with CLAUDE_CONFIG_DIR=\"/other/cfg\", the lead's is CLAUDE_CONFIG_DIR=\"/lead/cfg\"", row.Value.Member("detail")!.Value.AsString());
    }

    [Fact]
    public void TheDoctorNamesALiveMemberWhoseLoginVariableDiffersFromTheLeads()
    {
        var row = ClaudeEnvRow(string.Empty, live: true, field: "claude_securestorage_config_dir");

        Assert.NotNull(row);
        Assert.Equal("proj-implementor runs with CLAUDE_SECURESTORAGE_CONFIG_DIR unset, the lead's is CLAUDE_SECURESTORAGE_CONFIG_DIR=\"/lead/cfg\"", row.Value.Member("detail")!.Value.AsString());
    }

    [Fact]
    public void NegativeTheDoctorSaysNothingForAMemberInTheLeadsEnvironment()
    {
        Assert.Null(ClaudeEnvRow("/lead/cfg", live: true));
    }

    [Fact]
    public void NegativeTheDoctorSaysNothingForADeadMemberInAnotherEnvironment()
    {
        Assert.Null(ClaudeEnvRow("/other/cfg", live: false));
    }

    [Fact]
    public void ANewTeamRecordsTheCallersValue()
    {
        OnTmux();
        _extraEnv["CLAUDE_CONFIG_DIR"] = "/new/cfg";

        SpawnFor("a spawn that creates the team");

        Assert.Contains("export CLAUDE_CONFIG_DIR='/new/cfg'; unset CLAUDE_SECURESTORAGE_CONFIG_DIR; claude ", TypedLaunch(), StringComparison.Ordinal);
        Assert.Equal("/new/cfg", Recorded(TeamRecord()));
    }
}
