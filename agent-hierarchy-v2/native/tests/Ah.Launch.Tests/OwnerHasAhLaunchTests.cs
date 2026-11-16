namespace Ah.Launch.Tests;

public sealed class OwnerHasAhLaunchTests
{
    private const string Named = """{"env":{"WRANGL_TEAM_FILE":"/t.json","AH_MEMBER_NAME":"n"}}""";

    private static bool? Judge(string? line, Func<string, string?>? readFile = null) =>
        LaunchModule.OwnerHasAhLaunch(line, "a", "n", "/t.json", null, null, readFile ?? (static _ => null));

    [Fact]
    public void NoCommandLineCannotBeJudged()
    {
        Assert.Null(Judge(null));
        Assert.Null(Judge("   "));
    }

    [Fact]
    public void ACommandLineWithoutAhsSettingsIsNotAnAhLaunch()
    {
        Assert.False(Judge("claude --resume abc"));
    }

    [Fact]
    public void ALaunchSettingsFileNamingTheMemberIsAnAhLaunch()
    {
        Assert.True(Judge("claude --settings /launch.json", static _ => Named));
    }

    [Fact]
    public void NegativeALaunchSettingsFileNamingAnotherMemberIsNotAnAhLaunch()
    {
        Assert.False(Judge("claude --settings /launch.json", static _ => Named.Replace("\"n\"", "\"other\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void NegativeASettingsFileThatCannotBeReadIsNoProofEitherWay()
    {
        Assert.Null(Judge("claude --settings /gone.json"));
        Assert.Null(Judge("claude --settings /gone.json", static _ => throw new IOException("denied")));
    }
}
