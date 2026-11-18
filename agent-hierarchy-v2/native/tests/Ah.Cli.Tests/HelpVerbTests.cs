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

    private static string Source([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;

    private static string PluginFile(params string[] parts) =>
        Path.GetFullPath(Path.Combine([Path.GetDirectoryName(Source())!, "..", "..", "..", .. parts]));

    /// <summary>The <c>case</c> labels of <c>CliApp.RunAh</c>'s switch: the groups <c>ah</c> dispatches.</summary>
    private static List<string> DispatchedGroups()
    {
        var source = File.ReadAllText(PluginFile("native", "src", "Ah.Cli", "CliApp.cs"));
        var body = source[source.IndexOf("private static int RunAh(", StringComparison.Ordinal)..];
        return System.Text.RegularExpressions.Regex.Matches(body, "case \"([a-z-]+)\"").Select(m => m.Groups[1].Value).ToList();
    }

    [Fact]
    public void TheUsageListsEveryGroupAhDispatchesAndNoOther()
    {
        // gate and push-guard only say they were removed; `ahd` is only `ah ahd heal`, which the ahd usage lists
        string[] notGroups = ["gate", "push-guard", "ahd"];
        var dispatched = DispatchedGroups();

        Assert.Contains("msg", dispatched);
        Assert.Equal(dispatched.Except(notGroups).Order(StringComparer.Ordinal), CliApp.AhGroups.Order(StringComparer.Ordinal));
        Assert.Equal(CliApp.AhUsage + "\n", Run("/opt/ah/ahd", "ah").Stderr.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void AGroupMissingFromTheUsageIsCaught()
    {
        string[] notGroups = ["gate", "push-guard", "ahd"];

        Assert.NotEqual(DispatchedGroups().Except(notGroups).Order(StringComparer.Ordinal), CliApp.AhGroups.Where(g => g != "where").Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheShimPrintsTheBinarysUsageWordForWord()
    {
        var shim = File.ReadAllText(PluginFile("bin", "ah"));

        Assert.Contains($"echo \"{CliApp.AhUsage}\" >&2", shim, StringComparison.Ordinal);
    }

    /// <summary>Spec numbers, section marks, code names and backlog ids: labels a user cannot look up.</summary>
    private static readonly System.Text.RegularExpressions.Regex InternalLabel =
        new(@"spec [0-9]|§|ah-simplify|\bb[0-9]+\b|ah2-[0-9]+");

    /// <summary>
    /// The help text a user reads, from source (running each group's <c>--help</c> here composes services a test may not): the four
    /// usage blocks whole, every <c>usage:</c> line in the native sources, and the shim's and CliApp's own lines; comment lines aside.
    /// </summary>
    private static IEnumerable<(string Where, string Line)> HelpLines()
    {
        string[] blocks =
        [
            PluginFile("native", "src", "Ah.DaemonClient", "UsageText.cs"),
            PluginFile("native", "src", "Ah.Cli", "Verbs", "Msg", "MsgUsage.cs"),
            PluginFile("native", "src", "Ah.Cli", "Verbs", "Qa", "QaUsage.cs"),
            PluginFile("native", "src", "Ah.Commands", "Roster", "Common", "RosterUsage.cs"),
            PluginFile("native", "src", "Ah.Cli", "CliApp.cs"),
            PluginFile("bin", "ah"),
        ];
        var usageLines = Directory.EnumerateFiles(PluginFile("native", "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadLines(f).Select(l => (f, l)))
            .Where(x => x.l.Contains("\"usage:", StringComparison.Ordinal));
        return blocks.SelectMany(f => File.ReadLines(f).Select(l => (f, l)))
            .Concat(usageLines)
            .Where(x => !x.l.TrimStart().StartsWith("//", StringComparison.Ordinal) && !x.l.TrimStart().StartsWith('#'));
    }

    [Fact]
    public void NoHelpTextCarriesAnInternalLabel()
    {
        var lines = HelpLines().ToList();

        Assert.Contains(lines, x => x.Line.Contains("ah msg forward", StringComparison.Ordinal));
        Assert.Empty(lines.Where(x => InternalLabel.IsMatch(x.Line)).Select(x => $"{x.Where}: {x.Line.Trim()}"));
    }

    [Theory]
    [InlineData("see spec 0044 for details")]
    [InlineData("the gate (§1.10)")]
    [InlineData("removed in ah-simplify")]
    [InlineData("fixed in b430")]
    [InlineData("as ah2-61 says")]
    public void TheLabelPatternCatchesEachKindOfLabel(string text) => Assert.Matches(InternalLabel, text);

    [Theory]
    [InlineData("the spec file")]
    [InlineData("a b-tree and sha256")]
    [InlineData("the ah2 home")]
    public void TheLabelPatternLeavesPlainWordsAlone(string text) => Assert.DoesNotMatch(InternalLabel, text);

    [Fact]
    public void AhdDashHIsAhdDashDashHelp()
    {
        var help = Run("/opt/ah/ahd", "--help");

        Assert.Equal(help, Run("/opt/ah/ahd", "-h"));
        Assert.DoesNotContain("unknown command", help.Stderr, StringComparison.Ordinal);
    }
}
