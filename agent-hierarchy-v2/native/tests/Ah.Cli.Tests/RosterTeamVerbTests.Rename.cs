using Ah.Core.Json;
using Ah.Mesh;

namespace Ah.Cli.Tests;

/// <summary><c>roster rename</c> (ah2-218 A1, A2): the team's display name on its record, set by the user or its Orchestrator, never by a member.</summary>
public sealed partial class RosterTeamVerbTests
{
    private void ArrangeRename() => WriteTeam("proj", LiveTeamJson("tmux", ""));

    private JsonValue? DisplayName() => TeamFile("proj").Member("display_name");

    [Fact]
    public void TheOrchestratorSetsTheDisplayNameTrimmedAndTheAnswerNamesTheRealName()
    {
        ArrangeRename();

        var (code, stdout, stderr) = Roster("rename", "  Night shift  ", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var answer = JsonText.Parse(stdout);
        Assert.Equal(("proj", "Night shift"), (answer.Member("team")!.Value.AsString(), answer.Member("display_name")!.Value.AsString()));
        Assert.True(answer.Member("previous")!.Value.IsNull);
        Assert.Equal("Night shift", DisplayName()!.Value.AsString());
        Assert.Equal("t-1", TeamFile("proj").Member("team_id")!.Value.AsString());
    }

    [Fact]
    public void ClearRemovesTheFieldAndTheAnswerCarriesThePreviousName()
    {
        ArrangeRename();
        _ = Roster("rename", "Night shift", "--team", "proj");

        var (code, stdout, stderr) = Roster("rename", "--clear", "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        var answer = JsonText.Parse(stdout);
        Assert.True(answer.Member("display_name")!.Value.IsNull);
        Assert.Equal("Night shift", answer.Member("previous")!.Value.AsString());
        Assert.Null(DisplayName());
    }

    [Fact]
    public void TheUserAtAPlainTerminalRenamesTheFoldersOneTeamWithNoTeamFlag()
    {
        ArrangeRename();
        _nearest = new NearestSession(NearestKind.None);
        _plainUser = true;

        var (code, stdout, stderr) = Roster("rename", "Ops");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal("Ops", DisplayName()!.Value.AsString());
    }

    [Theory]
    [InlineData("   ", "empty after trimming")]
    [InlineData("a\nb", "control character or a line break")]
    [InlineData("a<LS>b", "control character or a line break")]
    [InlineData("0123456789012345678901234567890123456789012345678901234567890", "61 characters; at most 60")]
    public void AValueThatBreaksTheRuleIsNotStoredAndTheReasonIsOneLine(string value, string why)
    {
        ArrangeRename();
        _ = Roster("rename", "Kept", "--team", "proj");

        // <LS> is U+2028, a line break that is no control character
        var (code, stdout, stderr) = Roster("rename", value.Replace("<LS>", char.ConvertFromUtf32(0x2028), StringComparison.Ordinal), "--team", "proj");

        Assert.Equal(2, code);
        Assert.Contains(why, stderr, StringComparison.Ordinal);
        Assert.Single(stderr.Trim().Split('\n'));
        Assert.Equal("Kept", DisplayName()!.Value.AsString());
        Assert.DoesNotContain("display_name", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeSixtyCharactersCountedAsScalarsAreStored()
    {
        ArrangeRename();
        // 59 ASCII plus one astral character: 61 UTF-16 units, 60 characters
        var sixty = new string('a', 59) + char.ConvertFromUtf32(0x1F680);

        var (code, stdout, stderr) = Roster("rename", sixty, "--team", "proj");

        Assert.True(code == 0, stdout + stderr);
        Assert.Equal(sixty, DisplayName()!.Value.AsString());
    }

    [Fact]
    public void NegativeAMemberIsRefusedAndNothingIsWritten()
    {
        ArrangeRename();
        _nearest = new NearestSession(NearestKind.Member, "proj", "proj-implementor", Environment.ProcessId);
        var before = _sandbox.TeamText(TeamPath("proj"));

        var (code, stdout, stderr) = Roster("rename", "Mine now", "--team", "proj");

        Assert.True(code != 0, stdout + stderr);
        Assert.Equal(before, _sandbox.TeamText(TeamPath("proj")));
    }

    [Fact]
    public void NegativeASessionlessAgentThatIsNotThePlainUserIsRefused()
    {
        ArrangeRename();
        _nearest = new NearestSession(NearestKind.None);

        var (code, stdout, _) = Roster("rename", "Ops", "--team", "proj");

        Assert.Equal(2, code);
        Assert.Equal("owner-unproven", JsonText.Parse(stdout).Member("refused")!.Value.AsString());
        Assert.Null(DisplayName());
    }

    [Fact]
    public void NegativeBothANameAndClearIsABadCallAndNothingIsWritten()
    {
        ArrangeRename();

        var (code, _, stderr) = Roster("rename", "Ops", "--clear", "--team", "proj");

        Assert.Equal(2, code);
        Assert.Contains("one display name", stderr, StringComparison.Ordinal);
        Assert.Null(DisplayName());
    }
}
