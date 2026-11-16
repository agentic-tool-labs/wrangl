using Ah.Abstractions;

namespace Ah.Mesh.Tests;

/// <summary>Rule (b) of the herdr answer verb's caller check: the direct parent is <c>herdr-app</c> and no harness is on the line.</summary>
public sealed class HerdrAppChildTests
{
    /// <summary>Caller 300 under 200 under 100 under pid 1; every command readable.</summary>
    private sealed class Line(string c300, string c200, string c100) : IProcessTable
    {
        public IReadOnlyList<int>? Ancestors(int pid) => pid == 300 ? [200, 100, 1] : null;

        public bool? HasAncestor(int pid, int candidate) => pid == 300 && candidate is 200 or 100 or 1;

        public string? StartTime(long pid) => null;

        public string? LocalStartTime(long pid) => null;

        public string? Command(int pid) => pid switch { 300 => c300, 200 => c200, 100 => c100, _ => null };
    }

    private const string App = "/Applications/Herdr App.app/Contents/MacOS/herdr-app";

    private const string Bundle = "/Applications/Herdr.app/Contents/MacOS/Herdr";

    [Theory]
    [InlineData("ah msg decision ui-answer", App, "launchd-child", true)] // a mod exec: herdr-app spawned ah directly
    [InlineData("ah msg decision ui-answer", Bundle, "launchd-child", true)] // the installed app: Herdr.app/Contents/MacOS/Herdr
    [InlineData("claude --resume", App, "launchd-child", false)] // negative: a harness on the line (an agent's own process)
    [InlineData("ah msg decision ui-answer", Bundle, "claude --resume", false)] // negative: the bundle app, but a harness on the line
    [InlineData("ah msg decision ui-answer", "/usr/local/bin/herdr", "launchd-child", false)] // negative: a parent named close to herdr-app
    [InlineData("ah msg decision ui-answer", "/bin/zsh", App, false)] // negative: herdr-app is a grandparent, not the direct parent
    public void OnlyADirectHerdrAppParentWithNoHarnessOnTheLineIsAccepted(string caller, string parent, string grandparent, bool expected)
    {
        var table = new Line(caller, parent, grandparent);

        var accepted = MeshModule.IsHerdrAppChild(table, 300, pid => pid switch { 200 => parent, 100 => grandparent, _ => null });

        Assert.Equal(expected, accepted);
    }
}
