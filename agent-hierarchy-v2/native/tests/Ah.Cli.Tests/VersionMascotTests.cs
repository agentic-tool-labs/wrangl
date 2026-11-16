namespace Ah.Cli.Tests;

/// <summary><c>--version</c> shows the mascot to a person at a terminal and only the version line to anything else.</summary>
public sealed class VersionMascotTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Version(bool terminal, string? noColor = null)
    {
        var env = new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_TEST"] = "1", ["NO_COLOR"] = noColor });
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), "ah", "/opt/ah", null, 501, StdoutIsTerminal: terminal);
        using var stdout = new StringWriter();
        Assert.Equal(0, CliApp.Run(["--version"], context, stdout, new StringWriter()));
        return stdout.ToString();
    }

    [Fact]
    public void ATerminalGetsTheMascotAboveAnUnchangedVersionLineAndAPipeOrNoColorGetsTheLineAlone()
    {
        var line = Version(terminal: false);
        Assert.Matches(@"\Aah \S+ \(contract \S+\)\n\z", line);
        Assert.Equal(line, Version(terminal: true, noColor: "1"));

        var art = File.ReadAllText(Path.Combine(Ah.TestKit.NodeStateRegistry.FindPluginRoot()!, "assets", "mascot.ansi"));
        Assert.NotEmpty(art);
        Assert.Equal(art + line, Version(terminal: true));
    }

    private static string VersionLine(string argv0, params string[] args)
    {
        var env = new FakeEnv("/nohome", "/nohome/.claude/ah2", new Dictionary<string, string?> { ["AH_TEST"] = "1" });
        var context = new CliContext(env, new RecordingExec(), new FakeRunner(), argv0, "/opt/ah", null, 501);
        using var stdout = new StringWriter();
        Assert.Equal(0, CliApp.Run(args, context, stdout, new StringWriter()));
        return stdout.ToString();
    }

    [Fact]
    public void TheVersionLineNamesTheCommandTheUserTyped()
    {
        // bin/ah runs the one binary as `<binary> ah --version`; bin/ahd as `<binary> --version`
        Assert.Matches(@"\Aah \S+ \(contract \S+\)\n\z", VersionLine("/home/u/.claude/ah2/bin/ahd", "ah", "--version"));
        Assert.Matches(@"\Aahd \S+ \(contract \S+\)\n\z", VersionLine("/home/u/.claude/ah2/bin/ahd", "--version"));
    }
}
