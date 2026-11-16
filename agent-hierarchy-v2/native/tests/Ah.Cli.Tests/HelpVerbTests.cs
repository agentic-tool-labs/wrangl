namespace Ah.Cli.Tests;

/// <summary><c>ah --help</c>, <c>ah -h</c> and <c>ah help</c> print the no-argument usage on stdout and exit 0; <c>ahd -h</c> is <c>ahd --help</c>.</summary>
public sealed class HelpVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private (int Code, string Stdout, string Stderr) Run(string argv0, params string[] args)
    {
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_TEST"] = "1" });
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), argv0, "/opt/ah", null, 501);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var code = CliApp.Run(args, context, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("help")]
    [InlineData("help", "msg")]
    public void EveryHelpFormPrintsTheNoArgumentUsageOnStdoutAndExitsZero(params string[] help)
    {
        var (bareCode, _, usage) = Run("/opt/ah/ahd", "ah");
        Assert.Equal(2, bareCode);
        Assert.StartsWith("usage: ah <", usage, StringComparison.Ordinal);

        var (code, stdout, stderr) = Run("/opt/ah/ahd", ["ah", .. help]);

        Assert.Equal(0, code);
        Assert.Equal(usage, stdout);
        Assert.Empty(stderr);
    }

    [Fact]
    public void AWordThatOnlyStartsLikeHelpIsStillAnUnknownCommand()
    {
        var (code, stdout, stderr) = Run("/opt/ah/ahd", "ah", "helpx");

        Assert.Equal(2, code);
        Assert.Empty(stdout);
        Assert.Contains("unknown command 'helpx'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AhdDashHIsAhdDashDashHelp()
    {
        var help = Run("/opt/ah/ahd", "--help");

        Assert.Equal(help, Run("/opt/ah/ahd", "-h"));
        Assert.DoesNotContain("unknown command", help.Stderr, StringComparison.Ordinal);
    }
}
