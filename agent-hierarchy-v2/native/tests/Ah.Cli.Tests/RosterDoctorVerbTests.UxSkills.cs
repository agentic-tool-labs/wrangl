using Ah.Core.Json;

namespace Ah.Cli.Tests;

public sealed partial class RosterDoctorVerbTests
{
    private JsonObject UxSkillsRow(params string[] present)
    {
        _doctorOverride = new FixedDoctor([], designSources: present);
        var (code, stdout, stderr) = Roster(null, "doctor");
        Assert.True(code == 0, stderr);
        return Row(Parse(stdout), "ux-skills");
    }

    [Fact]
    public void TheUxSkillsRowIsOkWhenAllFourSourcesAreInstalled()
    {
        var row = UxSkillsRow("frontend-design", "vercel-labs", "jakubkrehel", "addyosmani");

        Assert.Equal("ok", row["status"].AsString());
        Assert.Equal("4/4 design sources installed", row["detail"].AsString());
    }

    [Fact]
    public void TheUxSkillsRowWarnsNamingEachMissingSourceWithItsHintAndSaysTheRoleWorksWithoutThem()
    {
        var row = UxSkillsRow("frontend-design", "addyosmani");

        Assert.Equal("warn", row["status"].AsString());
        var detail = row["detail"].AsString();
        Assert.StartsWith("2/4 design sources installed; missing: ", detail, StringComparison.Ordinal);
        Assert.Contains("vercel-labs (install by hand: npx skills add vercel-labs/agent-skills)", detail, StringComparison.Ordinal);
        Assert.Contains("jakubkrehel (/plugin marketplace add jakubkrehel/skills, then /plugin install interfaces@interfaces)", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("addyosmani (", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("frontend-design (", detail, StringComparison.Ordinal);
        Assert.EndsWith("ux-engineer works without them", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void TheUxSkillsRowWarnsWithNoneInstalledAndIsNeverRedOrOkWithThreeOfFour()
    {
        var none = UxSkillsRow();
        Assert.Equal("warn", none["status"].AsString());
        Assert.StartsWith("0/4 design sources installed; missing: ", none["detail"].AsString(), StringComparison.Ordinal);

        var three = UxSkillsRow("frontend-design", "vercel-labs", "jakubkrehel");
        Assert.Equal("warn", three["status"].AsString());
        Assert.StartsWith("3/4 design sources installed; missing: addyosmani (", three["detail"].AsString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDefaultDoctorOfAHostWithNoDesignSourcesStillAnswersAndIsNeverRed()
    {
        var (code, stdout, stderr) = Roster(null, "doctor");

        Assert.True(code == 0, stderr);
        Assert.NotEqual("red", Row(Parse(stdout), "ux-skills")["status"].AsString());
    }
}
