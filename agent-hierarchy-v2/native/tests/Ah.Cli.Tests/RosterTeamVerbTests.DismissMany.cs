using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary><c>dismiss &lt;a&gt; &lt;b&gt; … --close</c>: several members, one plan token, one confirm.</summary>
public sealed partial class RosterTeamVerbTests
{
    private static readonly string[] Trio = ["proj-a", "proj-b", "proj-c"];

    private static readonly string[] Panes = ["%1", "%2", "%3"];

    /// <summary>Three implementors on tmux (%1 to %3), none live, so each judges <c>not-running</c> and may close.</summary>
    private void ArrangeTrio()
    {
        WriteTeam("proj", LiveTeamJson("tmux", string.Join(",", Trio.Select((n, i) => Member("implementor", n, $"%{i + 1}")))));
        ScriptGit();
    }

    private (int Code, JsonValue Out, string Stderr) PlanMany(params string[] names)
    {
        var (code, stdout, stderr) = Roster(["dismiss", .. names, "--plan", "--team", "proj"]);
        return (code, stdout.Length > 0 ? JsonText.Parse(stdout) : JsonValue.Null, stderr);
    }

    private static string TokenOf(JsonValue plan) => plan.Member("close_token") is { IsText: true } t ? t.AsString() : "none";

    private (int Code, string Stdout, string Stderr) CloseMany(string token, params string[] names) =>
        Roster(["dismiss", .. names, "--close", "--confirm", "--plan-token", token, "--team", "proj"]);

    [Fact]
    public void ADismissPlanOfSeveralMembersGivesOneTokenAndARowPerMember()
    {
        ArrangeTrio();

        var (code, plan, stderr) = PlanMany(Trio);

        Assert.True(code == 0, stderr);
        Assert.Matches("^(cc1-)?[0-9a-f]{16}$", TokenOf(plan));
        Assert.Equal(3, plan.Member("members")!.Value.AsArray().Count);
        Assert.Equal(3, (int)plan.Member("expected")!.Value.AsNumber());
        var next = plan.Member("next")!.Value.AsString();
        Assert.All(Trio, n => Assert.Contains(n, next, StringComparison.Ordinal));
        Assert.Contains("--plan-token " + TokenOf(plan), next, StringComparison.Ordinal);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void ADismissPlanWithOneFailingMemberIssuesNoTokenAndNamesTheFailure()
    {
        ArrangeTrio();
        PeerUp("proj-b", "%2", session: "sess-b");
        Activity("sess-b.json", "working", DateTimeOffset.UtcNow);

        var (code, plan, _) = PlanMany(Trio);

        Assert.Equal(0, code);
        Assert.True(plan.Member("close_token")!.Value.IsNull);
        Assert.Null(plan.Member("next"));
        var failure = Assert.Single(plan.Member("failures")!.Value.AsArray());
        Assert.Equal("proj-b", failure.Member("member")!.Value.AsString());
        Assert.Equal("busy", failure.Member("status")!.Value.AsString());
    }

    [Fact]
    public void ConfirmingTheSameSetInAnyOrderClosesEveryMember()
    {
        ArrangeTrio();
        var token = TokenOf(PlanMany(Trio).Out);

        var (code, stdout, stderr) = CloseMany(token, "proj-c", "proj-a", "proj-b");

        Assert.True(code == 0, stderr);
        Assert.All(Panes, pane => Assert.True(Killed(pane)));
        var result = JsonText.Parse(stdout);
        Assert.True(result.Member("closed")!.Value.AsBool());
        Assert.Equal(3, result.Member("results")!.Value.AsArray().Count);
        Assert.True(_sandbox.TeamExists(TeamPath("proj")));
    }

    [Fact]
    public void DismissingTheLastMembersKeepsTheOwnedTeamWithItsFieldsAndNoMembers()
    {
        ArrangeTrio();
        var before = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        var token = TokenOf(PlanMany(Trio).Out);

        var (code, stdout, stderr) = CloseMany(token, Trio);

        Assert.True(code == 0, stderr);
        Assert.All(JsonText.Parse(stdout).Member("results")!.Value.AsArray(), row => Assert.Null(row.Member("team_removed")));
        var kept = JsonText.Parse(_sandbox.TeamText(TeamPath("proj")));
        Assert.Empty(kept.Member("members")!.Value.AsArray());
        foreach (var field in new[] { "team_id", "transport", "created", "orchestrator" })
        {
            Assert.Equal(JsonText.Stringify(before.Member(field)!.Value, 0), JsonText.Stringify(kept.Member(field)!.Value, 0));
        }
    }

    [Fact]
    public void EveryClosedMemberRowReportsItsConfigRemovalWhenAskedForAndNoneOtherwise()
    {
        ArrangeTrio();
        var token = TokenOf(PlanMany(Trio).Out);

        var (code, stdout, stderr) = Roster(["dismiss", .. Trio, "--close", "--confirm", "--plan-token", token, "--also-config", "--team", "proj"]);

        Assert.True(code == 0, stderr);
        Assert.All(JsonText.Parse(stdout).Member("results")!.Value.AsArray(), row => Assert.NotNull(row.Member("config")));

        ArrangeTrio();
        var plain = CloseMany(TokenOf(PlanMany(Trio).Out), Trio);
        Assert.True(plain.Code == 0, plain.Stderr);
        Assert.All(JsonText.Parse(plain.Stdout).Member("results")!.Value.AsArray(), row => Assert.Null(row.Member("config")));
    }

    [Fact]
    public void AMidBatchFailureStopsTheBatchReportsEachMemberAndExitsNonZero()
    {
        ArrangeTrio();
        var token = TokenOf(PlanMany(Trio).Out);
        _runner.Exit = argv => argv.Contains("kill-pane") && argv.Contains("%2") ? 1 : 0;

        var (code, stdout, _) = CloseMany(token, Trio);

        Assert.NotEqual(0, code);
        Assert.True(Killed("%1"));
        Assert.False(Killed("%3"));
        var results = JsonText.Parse(stdout).Member("results")!.Value.AsArray()
            .ToDictionary(r => r.Member("member")!.Value.AsString(), r => r.Member("result")!.Value.AsString());
        Assert.Equal("closed", results["proj-a"]);
        Assert.Equal("failed", results["proj-b"]);
        Assert.Equal("not-attempted", results["proj-c"]);
        Assert.DoesNotContain("proj-a", _sandbox.TeamText(TeamPath("proj")), StringComparison.Ordinal);
        Assert.Contains("proj-b", _sandbox.TeamText(TeamPath("proj")), StringComparison.Ordinal);
    }

    [Fact]
    public void SeveralNamesWithIgnoreReleasePlanAndTheConfirmCommandCarriesTheFlag()
    {
        ArrangeTrio();

        var (code, stdout, stderr) = Roster(["dismiss", "proj-a", "proj-b", "--ignore-release", "--team", "proj"]);
        var (_, plainOut, _) = Roster(["dismiss", "proj-a", "proj-b", "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains("--ignore-release", JsonText.Parse(stdout).Member("next")!.Value.AsString(), StringComparison.Ordinal);
        Assert.DoesNotContain("--ignore-release", JsonText.Parse(plainOut).Member("next")!.Value.AsString(), StringComparison.Ordinal);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void SeveralNamesTakeTheOverridePerNameAndAnUnnamedWorkingMemberStillHolds()
    {
        ArrangeTrio();
        PeerUp("proj-a", "%1", session: "s-a");
        PeerUp("proj-b", "%2", session: "s-b");
        Activity("s-a.json", "working", DateTimeOffset.UtcNow);
        Activity("s-b.json", "working", DateTimeOffset.UtcNow);

        var (heldCode, _, heldErr) = Roster(["dismiss", "proj-a", "proj-b", "--close", "--ignore-release", "proj-a", "--team", "proj"]);

        Assert.NotEqual(0, heldCode);
        Assert.False(Killed("%1"));
        Assert.Contains("proj-b may not close", heldErr, StringComparison.Ordinal);

        var (code, stdout, stderr) = Roster(["dismiss", "proj-a", "proj-b", "--close", "--ignore-release", "proj-a", "--ignore-release", "proj-b", "--team", "proj"]);

        Assert.True(code == 0, stdout + stderr);
        Assert.True(Killed("%1"));
        Assert.True(Killed("%2"));
    }

    [Fact]
    public void AOneNameAndAWholeTeamPlanWithIgnoreReleaseCarryTheFlagInTheirNextCommand()
    {
        ArrangeTrio();

        foreach (var verb in new[] { new[] { "dismiss", "proj-a", "--plan" }, new[] { "disband" } })
        {
            var (code, stdout, stderr) = Roster([.. verb, "--ignore-release", "--team", "proj"]);
            var (_, plainOut, _) = Roster([.. verb, "--team", "proj"]);

            Assert.True(code == 0, stdout + stderr);
            Assert.Contains("--ignore-release", JsonText.Parse(stdout).Member("next")!.Value.AsString(), StringComparison.Ordinal);
            Assert.DoesNotContain("--ignore-release", JsonText.Parse(plainOut).Member("next")!.Value.AsString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SeveralNamesWithAnUnknownOrRepeatedNameAreRefused()
    {
        ArrangeTrio();

        Assert.NotEqual(0, Roster(["dismiss", "proj-a", "proj-zzz", "--team", "proj"]).Code);
        Assert.NotEqual(0, Roster(["dismiss", "proj-a", "proj-a", "--team", "proj"]).Code);
        Assert.False(Killed("%1"));
    }

    [Fact]
    public void ASingleNameDismissStillPlansExactlyOneMember()
    {
        ArrangeTrio();

        var (code, plan, _) = PlanMany("proj-a");

        Assert.Equal(0, code);
        Assert.Null(plan.Member("members"));
        Assert.Equal("proj-a", plan.Member("member")!.Value.Member("name")!.Value.AsString());
    }
}
