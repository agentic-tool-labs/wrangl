using Ah.Core.Json;

namespace Ah.Cli.Tests;

[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed partial class RosterDoctorVerbTests
{
    private const string Surface =
        """{"version":3,"entries":[{"id":"git-cherry-pick","kind":"place","match":{"argv0":"git","sub":["cherry-pick"]}},{"id":"git-push","kind":"routed","match":{"argv0":"git","sub":["push"]}}]}""";

    private string LaunchDir => Path.Combine(Hierarchy, "launch");

    private const string ConsentSource = "ah:R7;consent=member-launch-rules;cv=2";

    private static string AskRow(string source, string label, string at = "2026-10-21T09:00:00.000Z", bool withMetadata = true)
    {
        var q = Ah.Config.ConfigModule.MemberLaunchConsentQuestion;
        var asked = "[{\"question\":\"" + q + "\",\"header\":\"Member rules\",\"multiSelect\":false,\"options\":[{\"label\":\"Allow\",\"description\":\"d\"},{\"label\":\"Not now\",\"description\":\"d\"}]}]";
        return "{\"at\":\"" + at + "\",\"writer\":\"" + Ah.Config.ConfigModule.AskRecordWriter + "\",\"session_id\":\"sess-orch\",\"questions\":" + asked + ",\"response\":{\"questions\":" + asked + ",\"answers\":{\"" + q + "\":\"" + label + "\"},\"annotations\":{}}"
            + (withMetadata ? ",\"metadata\":{\"source\":\"" + source + "\"}" : string.Empty) + "}";
    }

    private void SeedAsk(params string[] lines)
    {
        Directory.CreateDirectory(Hierarchy);
        File.AppendAllText(Path.Combine(Hierarchy, "asks.jsonl"), string.Join('\n', lines) + "\n");
    }

    private void SeedDiagnoseTeam(bool consent = true)
    {
        if (consent)
        {
            SeedAsk(AskRow(ConsentSource, "Allow"));
        }

        _sandbox.PlantTeam(
            Path.Combine(Hierarchy, "teams", "proj.json"),
            "{\"version\":1,\"orchestrator\":{\"session_id\":\"o\",\"pid\":99999999},\"members\":[{\"role\":\"implementor\",\"name\":\"impl\",\"route\":\"peer\",\"transport_id\":\"%1\",\"session_id\":\"sess-impl\",\"expected_root\":\"/wt/impl\"}]}");
    }

    private void SeedSurface(string json = Surface)
    {
        Directory.CreateDirectory(Path.Combine(_pluginRoot, "policy"));
        File.WriteAllText(Path.Combine(_pluginRoot, "policy", "member-surface.json"), json);
    }

    private void SeedLaunch(bool sandbox, int builtAtSurface = 3)
    {
        Directory.CreateDirectory(LaunchDir);
        File.WriteAllText(Path.Combine(LaunchDir, "impl.settings.json"), $"{{\"sandbox\":{{\"enabled\":{(sandbox ? "true" : "false")}}}}}");
        File.WriteAllText(Path.Combine(LaunchDir, "impl.meta.json"), $"{{\"surface_version\":{builtAtSurface},\"plugin_version\":\"1.2.3\",\"generated_at\":\"2026-10-21T10:00:00.000Z\"}}");
    }

    private void SeedDenial(string command, string member = "impl", TimeSpan? age = null)
    {
        var ts = (DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(5))).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
        PlantGraph().Hierarchy.StateCalls.AppendPeerRecord(
            new JsonObject
            {
                ["type"] = "watch-event",
                ["kind"] = "CLASSIFIER-DENIED",
                ["member"] = member,
                ["label"] = "Auto-Mode Bypass",
                ["command"] = command,
                ["at"] = ts,
                ["ts"] = ts,
            });
    }

    private string Diagnose(params string[] extraArgs) =>
        Roster(null, ["permissions", "diagnose", "impl", .. extraArgs]).Stdout;

    private static string CauseLine(string stdout) => stdout.Split('\n').Single(l => l.StartsWith("cause:", StringComparison.Ordinal));

    [Fact]
    public void DiagnoseWithNoDenialOnRecordReportsCauseNoneAndExitsZero()
    {
        SeedDiagnoseTeam();
        var (code, stdout, _) = Roster(null, "permissions", "diagnose", "impl");

        Assert.Equal(0, code);
        Assert.Contains("last denial: none in the last 24 h", stdout, StringComparison.Ordinal);
        Assert.Equal("cause:       none", CauseLine(stdout));
        Assert.Contains("root: /wt/impl  session: sess-impl", stdout, StringComparison.Ordinal);
    }

    /// <summary>ah2-310 A3': a soft-gate bounce newer than the classifier denial is the last denial; another member's is not.</summary>
    [Fact]
    public void ANewerGateBounceIsTheLastDenialWithItsRuleReasonAndApproveFix()
    {
        SeedDiagnoseTeam();
        SeedDenial("git cherry-pick abc", age: TimeSpan.FromHours(1));
        var calls = PlantGraph().Hierarchy.StateCalls;
        calls.AppendGate(Hierarchy, new JsonObject { ["type"] = "soft-gate", ["rule"] = "worktree", ["outcome"] = "bounce", ["member"] = "other", ["tool"] = "Bash", ["reason"] = "not impl's" });
        // A7: a self-care clone bounce names the rule whose approval lifts it
        calls.AppendGate(Hierarchy, new JsonObject { ["type"] = "soft-gate", ["rule"] = "self-care", ["outcome"] = "bounce", ["member"] = "impl", ["tool"] = "Bash", ["reason"] = "self-care only.", ["approvable_by"] = "worktree" });

        var stdout = Diagnose();

        Assert.Matches(@"last denial: \S+ \[gate:self-care\] \(Bash\)", stdout);
        Assert.Equal("cause:       self-care: self-care only.; fix: ah roster approve impl worktree", CauseLine(stdout));
    }

    [Fact]
    public void NegativeAGateBounceNoApprovalLiftsGivesNoFix()
    {
        SeedDiagnoseTeam();
        PlantGraph().Hierarchy.StateCalls.AppendGate(Hierarchy, new JsonObject { ["type"] = "soft-gate", ["rule"] = "self-care", ["outcome"] = "bounce", ["member"] = "impl", ["tool"] = "Bash", ["reason"] = "self-care only." });

        Assert.Equal("cause:       self-care: self-care only.", CauseLine(Diagnose()));
    }

    [Fact]
    public void ADenialOlderThanADayOrOfAnotherMemberIsNotTheLastDenial()
    {
        SeedDiagnoseTeam();
        SeedDenial("git cherry-pick abc", age: TimeSpan.FromHours(25));
        SeedDenial("git cherry-pick abc", member: "other");
        PlantGraph().Hierarchy.StateCalls.AppendGate(Hierarchy, new JsonObject { ["type"] = "soft-gate", ["rule"] = "worktree", ["outcome"] = "bounce", ["member"] = "other", ["tool"] = "Bash", ["reason"] = "r" });
        PlantGraph().Hierarchy.StateCalls.AppendGate(Hierarchy, new JsonObject { ["type"] = "soft-gate", ["rule"] = "worktree", ["outcome"] = "retry-pass", ["member"] = "impl", ["tool"] = "Bash", ["reason"] = "r" });

        Assert.Equal("cause:       none", CauseLine(Diagnose()));
    }

    [Fact]
    public void CauseOneWhileTheLaunchFileAndTheSurfaceAreAbsentSaysPhaseOneBIsNotInstalled()
    {
        SeedDiagnoseTeam();
        SeedDenial("git cherry-pick abc");

        var stdout = Diagnose();

        Assert.Contains("launch file: absent", stdout, StringComparison.Ordinal);
        Assert.Equal("cause:       1 launch-file: absent: phase 1b not installed", CauseLine(stdout));
    }

    [Fact]
    public void CauseOneWithTheSurfaceInstalledButNoLaunchFileSaysToRestartTheMember()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedDenial("git cherry-pick abc");

        Assert.Equal("cause:       1 launch-file: restart or resume the member (launch files regenerate at start)", CauseLine(Diagnose()));
    }

    [Fact]
    public void CauseOneWhenTheLaunchFileWasBuiltFromAnOlderSurface()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true, builtAtSurface: 2);
        SeedDenial("git cherry-pick abc");

        var stdout = Diagnose();

        Assert.Contains("launch file: stale (surface v2 < v3)", stdout, StringComparison.Ordinal);
        Assert.StartsWith("cause:       1 launch-file:", CauseLine(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void CauseThreeWhenTheSandboxIsOffInACurrentLaunchFile()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: false);
        SeedDenial("git cherry-pick abc");

        var stdout = Diagnose();

        Assert.Contains("sandbox:     off", stdout, StringComparison.Ordinal);
        Assert.StartsWith("cause:       3 sandbox:", CauseLine(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void AnEarlierCauseWinsOverALaterOne()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: false);
        SeedDenial("curl https://example.test");

        Assert.StartsWith("cause:       3 sandbox:", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Fact]
    public void AShellCommandNoSurfaceEntryMatchesIsTheClassifiersDecision()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedDenial("curl https://example.test");

        var stdout = Diagnose();

        Assert.Contains("surface:     v3: no entry matches", stdout, StringComparison.Ordinal);
        Assert.StartsWith("cause:       5 classifier-decision:", CauseLine(stdout), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SendMessage")]
    [InlineData("Agent")]
    [InlineData("mcp__server__tool")]
    public void ADeniedToolWithNoAllowRuleIsNotTheClassifiersDecision(string tool)
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedDenial(tool);

        var stdout = Diagnose();

        Assert.Equal($"cause:       4 tool-not-allowed: {tool} has no allow rule in the member's generated settings", CauseLine(stdout));
    }

    [Fact]
    public void ADeniedToolTheMembersSettingsAllowIsTheClassifiersDecisionNotAMissingRuleOrAnAhBug()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        File.WriteAllText(Path.Combine(LaunchDir, "impl.settings.json"), """{"sandbox":{"enabled":true},"permissions":{"allow":["SendMessage"]}}""");
        SeedDenial("SendMessage");

        Assert.StartsWith("cause:       5 classifier-decision: SendMessage has an allow rule", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Fact]
    public void AGeneratedLaunchFileHasNoSandboxKeyAndThatIsNotTheCause()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        File.WriteAllText(Path.Combine(LaunchDir, "impl.settings.json"), """{"permissions":{"allow":["Bash(*)","SendMessage"]}}""");
        SeedDenial("SendMessage");

        var stdout = Diagnose();
        Assert.Contains("sandbox:     absent", stdout, StringComparison.Ordinal);
        Assert.StartsWith("cause:       5 classifier-decision:", CauseLine(stdout), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sendmessage")]
    [InlineData("./Agent")]
    [InlineData("FOO=1 curl https://example.test")]
    [InlineData("Rscript x")]
    [InlineData("MSBuild x.proj")]
    public void ACommandThatOnlyLooksLikeAToolNameIsNotAToolDenial(string command)
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedDenial(command);

        Assert.StartsWith("cause:       5 classifier-decision: the member's generated settings allow every shell command", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Fact]
    public void ACommandWithShellSyntaxMatchesNoEntry()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedDenial("git cherry-pick abc | tee out");

        Assert.Contains("surface:     v3: no entry matches", Diagnose(), StringComparison.Ordinal);
    }

    [Fact]
    public void CauseFiveDoesNotCallAClassifierStopAnAhBug()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedDenial("git cherry-pick abc");

        var stdout = Diagnose();

        Assert.Contains("last command matches git-cherry-pick (place)", stdout, StringComparison.Ordinal);
        Assert.StartsWith("cause:       5 classifier-decision:", CauseLine(stdout), StringComparison.Ordinal);
        Assert.DoesNotContain("ah bug", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnoseJsonCarriesTheSameKeys()
    {
        SeedDiagnoseTeam();
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedDenial("git cherry-pick abc");

        var json = JsonText.Parse(Diagnose("--json")).AsObject();

        Assert.Equal("impl", json["member"].AsString());
        Assert.Equal("on", json["sandbox"].AsString());
        Assert.Equal("classifier-decision", json["cause"].Member("name")!.Value.AsString());
        Assert.Equal("Auto-Mode Bypass", json["last_denial"].Member("label")!.Value.AsString());
    }

    [Fact]
    public void DiagnoseRunsInAnAgentSessionWhileEveryOtherActionStillRefuses()
    {
        SeedDiagnoseTeam();
        var agent = new Dictionary<string, string?> { ["AH_SPAWN_DEPTH"] = "1" };

        var diagnose = Roster(agent, "permissions", "diagnose", "impl");
        var add = Roster(agent, "permissions", "add");

        Assert.Equal(0, diagnose.Code);
        Assert.NotEqual(0, add.Code);
        Assert.Contains("started by an ah agent session", add.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NoConsentRowIsNotCauseTwo()
    {
        SeedDiagnoseTeam(consent: false);
        SeedDenial("git cherry-pick abc");

        Assert.DoesNotContain("no-consent", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Fact]
    public void CauseTwoDeclinedWhenTheNewestConsentRowIsNotNow()
    {
        SeedDiagnoseTeam(consent: false);
        SeedAsk(AskRow(ConsentSource, "Not now"));
        SeedDenial("git cherry-pick abc");

        Assert.StartsWith("cause:       2 no-consent: declined:", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    private string LinkedWorktree()
    {
        Directory.Delete(Path.Combine(Project, ".git"), recursive: true);
        MessagePoolResolutionTests.Git(Project, "init", "-q");
        MessagePoolResolutionTests.Git(Project, "-c", "user.name=T", "-c", "user.email=t@example.invalid", "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-qm", "base");
        var linked = Path.Combine(_sandbox.Root, "linked");
        MessagePoolResolutionTests.Git(Project, "worktree", "add", "-q", "-b", "member", linked);
        return linked;
    }

    /// <summary>Runs the command the alarm prints from <paramref name="cwd"/>, with the hierarchy directory derived from it as on a real machine.</summary>
    private string DiagnoseFrom(string cwd)
    {
        _cwdUnderTest = cwd;
        return Roster(new Dictionary<string, string?> { ["WRANGL_HIERARCHY_DIR"] = null }, "permissions", "diagnose", "impl", "--team", "proj").Stdout;
    }

    [Fact]
    public void TheCommandTheAlarmPrintsFromAMembersWorktreeReadsTheTeamAndTheDeclineInTheMainCheckout()
    {
        SeedDiagnoseTeam(consent: false);
        SeedAsk(AskRow(ConsentSource, "Not now"));
        SeedDenial("git cherry-pick abc");
        var linked = LinkedWorktree();

        var stdout = DiagnoseFrom(linked);

        Assert.Contains("(team proj)", stdout, StringComparison.Ordinal);
        Assert.StartsWith("cause:       2 no-consent: declined:", CauseLine(stdout), StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeADeclineOnlyInAWorktreesOwnLogIsNotCauseTwo()
    {
        SeedDiagnoseTeam(consent: false);
        SeedDenial("git cherry-pick abc");
        var linked = LinkedWorktree();
        var own = Path.Combine(linked, ".claude", "ah2");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "asks.jsonl"), AskRow(ConsentSource, "Not now") + "\n");

        Assert.DoesNotContain("no-consent", CauseLine(DiagnoseFrom(linked)), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOldConsentVersionIsNotCauseTwo()
    {
        SeedDiagnoseTeam(consent: false);
        SeedAsk(AskRow("ah:R7;consent=member-launch-rules;cv=0", "Allow"));
        SeedDenial("git cherry-pick abc");

        Assert.DoesNotContain("no-consent", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Fact]
    public void ALineThatIsMalformedOrCarriesNoSourceTagForgesNoDecline()
    {
        SeedDiagnoseTeam(consent: false);
        SeedAsk("{not json", AskRow(ConsentSource, "Not now", withMetadata: false), AskRow("ah:R7;consent=other;cv=1", "Not now"), AskRow("consent=member-launch-rules;cv=1", "Not now"));
        SeedDenial("git cherry-pick abc");

        Assert.DoesNotContain("no-consent", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Fact]
    public void TheNewestConsentRowDecidesNotTheOldest()
    {
        SeedDiagnoseTeam(consent: false);
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedAsk(AskRow(ConsentSource, "Not now", at: "2026-10-21T08:00:00.000Z"), AskRow(ConsentSource, "Allow", at: "2026-10-21T09:00:00.000Z"));
        SeedDenial("git cherry-pick abc");

        Assert.StartsWith("cause:       5 classifier-decision:", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Fact]
    public void WithAFreshLaunchFileMissingConsentIsNotTheCause()
    {
        SeedDiagnoseTeam(consent: false);
        SeedSurface();
        SeedLaunch(sandbox: true);
        SeedDenial("git cherry-pick abc");

        Assert.StartsWith("cause:       5 classifier-decision:", CauseLine(Diagnose()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("..")]
    [InlineData("a b")]
    public void AMemberNameThatIsNotAPlainWordIsRefusedAndNothingIsRead(string name)
    {
        SeedDiagnoseTeam();
        SeedLaunch(sandbox: true);

        var result = Roster(null, "permissions", "diagnose", name);

        Assert.NotEqual(0, result.Code);
        Assert.DoesNotContain("launch file:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberTheTeamDoesNotListIsReportedAndNoLaunchFileIsRead()
    {
        SeedDiagnoseTeam();
        SeedLaunch(sandbox: true);

        var result = Roster(null, "permissions", "diagnose", "ghost");

        Assert.Equal(0, result.Code);
        Assert.Contains("not in this team", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("launch file:", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnoseOfAnUnknownMemberOrWithoutAMemberNameDoesNotCrash()
    {
        SeedDiagnoseTeam();

        var unknown = Roster(null, "permissions", "diagnose", "ghost");
        var bare = Roster(null, "permissions", "diagnose");

        Assert.Equal(0, unknown.Code);
        Assert.Contains("(not in this team)", unknown.Stdout, StringComparison.Ordinal);
        Assert.Equal(2, bare.Code);
        Assert.Contains("diagnose needs one member name", bare.Stderr, StringComparison.Ordinal);
    }
}
