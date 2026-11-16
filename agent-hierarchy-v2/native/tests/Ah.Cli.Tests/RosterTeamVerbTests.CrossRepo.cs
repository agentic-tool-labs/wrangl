using Ah.Core.Json;

namespace Ah.Cli.Tests;

/// <summary>b418: <c>--team T</c> from a <c>--cwd</c> whose homes hold no T acts on the live T this session owns, found by its registration.</summary>
public sealed partial class RosterTeamVerbTests
{
    private string OtherHome => Path.Combine(_sandbox.Root, "other-hier");

    private void ArrangeTeamElsewhere(string? ownerPid = null)
    {
        var path = Path.Combine(OtherHome, "teams", "ic.json");
        _sandbox.PlantTeam(path, LiveTeamJson("tmux", Member("implementor", "ic-implementor", "%1"), pid: ownerPid, pidStart: ownerPid is null ? null : GoneStart));
        _exposeMailbox = true;
        _seedGraph = graph => FakeRunner.RegisterSelf(graph.Mailbox, "sess-ic", path, "ic-implementor@ic");
    }

    [Fact]
    public void DismissWithTeamFindsTheOwnedTeamHomedInAnotherRepo()
    {
        ArrangeTeamElsewhere();

        var (code, stdout, stderr) = Roster(["dismiss", "ic-implementor", "--plan", "--team", "ic"]);

        Assert.True(code is 0 or 3, stdout + stderr);
        Assert.True(JsonText.Parse(stdout).Member("member") is { } member && member.Member("name")?.AsString() == "ic-implementor", stdout + stderr);
    }

    [Fact]
    public void DismissWithTeamNegativeLeavesATeamElsewhereThisSessionDoesNotOwn()
    {
        ArrangeTeamElsewhere(ownerPid: GonePid().ToString(System.Globalization.CultureInfo.InvariantCulture));

        var (_, stdout, stderr) = Roster(["dismiss", "ic-implementor", "--plan", "--team", "ic"]);

        Assert.Contains("no active team and no live peers", stdout + stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void SpawnWithTeamJoinsTheOwnedTeamHomedInAnotherRepo()
    {
        ArrangeTeamElsewhere();

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "reviewer", "--team", "ic", "--model", "sonnet", "--orchestrator-pid", Self, "--dry-run");

        Assert.True(code == 0, stdout + stderr);
        Assert.Contains(Path.Combine(OtherHome, "teams", "ic.json"), stdout, StringComparison.Ordinal);
        Assert.False(File.Exists(TeamPath("ic")), "a second team named ic was created in the cwd's home");
    }

    /// <summary>b421: the launch file and the consent read come from the team's home; the cwd home's "Not now" must not reach this launch.</summary>
    [Fact]
    public void SpawnWithTeamElsewhereReadsTheTeamHomesLaunchSettingsAndConsent()
    {
        ArrangeTeamElsewhere();
        OnTmux();
        ConsentToMemberRules(Hierarchy, "Not now");
        ConsentToMemberRules(OtherHome);

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "reviewer", "--team", "ic", "--model", "sonnet", "--orchestrator-pid", Self, "--allow-main");

        Assert.True(code == 0, stdout + stderr);
        var file = JsonText.Parse(stdout).Member("permissions")?.Member("file");
        Assert.True(file is { IsText: true } f && f.AsString().StartsWith(Path.Combine(OtherHome, "launch") + "/", StringComparison.Ordinal), stdout + stderr);
    }

    /// <summary>b421: the member_allow switch comes from the team's repo; the cwd repo's opt-in must not widen a member the team's repo opted out.</summary>
    [Fact]
    public void SpawnWithTeamElsewhereReadsTheMemberAllowSwitchFromTheTeamsRepo()
    {
        ArrangeTeamElsewhere();
        OnTmux();
        ConsentToMemberRules(OtherHome);
        File.WriteAllText(Path.Combine(Hierarchy, "agent-hierarchy.json"), "{\"member_allow\":{\"enabled\":true}}");
        File.WriteAllText(Path.Combine(OtherHome, "agent-hierarchy.json"), "{\"member_allow\":{\"enabled\":false}}");

        var (code, stdout, stderr) = Roster("spawn-ad-hoc", "reviewer", "--team", "ic", "--model", "sonnet", "--orchestrator-pid", Self, "--allow-main");

        Assert.True(code == 0, stdout + stderr);
        Assert.True(JsonText.Parse(stdout).Member("permissions")?.Member("file") is { IsNull: true }, stdout + stderr);
    }

    /// <summary>b428: <c>allow list --team</c> reports the team repo's switch, the one launch reads, not the cwd repo's.</summary>
    [Fact]
    public void AllowListWithTeamElsewhereReadsTheSwitchFromTheTeamsRepo()
    {
        ArrangeTeamElsewhere();
        File.WriteAllText(Path.Combine(Hierarchy, "agent-hierarchy.json"), "{\"member_allow\":{\"enabled\":true}}");
        File.WriteAllText(Path.Combine(OtherHome, "agent-hierarchy.json"), "{\"member_allow\":{\"enabled\":false}}");

        var (code, stdout, stderr) = Roster("member-allow", "list", "--team", "ic", "--json");

        Assert.True(code == 0, stdout + stderr);
        Assert.True(JsonText.Parse(stdout).Member("enabled") is { IsBool: true } e && !e.AsBool(), stdout + stderr);
    }
}
