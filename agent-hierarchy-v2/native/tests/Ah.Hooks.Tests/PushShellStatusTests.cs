using Ah.Hooks.Gates;

namespace Ah.Hooks.Tests;

/// <summary>Which command lines have an exit status that is each command's own: one command, or commands joined only by <c>&amp;&amp;</c>.</summary>
public sealed class PushShellStatusTests
{
    private static bool Bearing(string line) => PushShell.StatusBearing(PushShell.Parse(line).Events);

    [Theory]
    [InlineData("git push origin v1")]
    [InlineData("git push origin v1 > /tmp/p.log 2>&1")]
    [InlineData("git push origin v1 2>&1")]
    [InlineData("git merge x && git push origin main")]
    [InlineData("cd /tmp && git push origin v1 &> /tmp/p.log")]
    [InlineData("git push origin v1;")]
    [InlineData("git push origin v1\n")]
    [InlineData("FOO=1 git push origin v1")]
    public void OneCommandOrCommandsJoinedOnlyByAndAreStatusBearing(string line)
    {
        Assert.True(Bearing(line));
    }

    [Theory]
    [InlineData("git push origin v1; echo exit=$?")]
    [InlineData("git push origin v1 2>&1 | tail -3")]
    [InlineData("git push origin v1 || true")]
    [InlineData("git push origin v1 &")]
    [InlineData("git push origin v1\ngit status")]
    [InlineData("git merge x && git push origin main; git status")]
    [InlineData("(git push origin v1)")]
    [InlineData("{ git push origin v1; }")]
    [InlineData("echo $(git push origin v1)")]
    [InlineData("bash -c 'git push origin v1'")]
    [InlineData("eval git push origin v1")]
    [InlineData("if git push origin v1; then echo ok; fi")]
    [InlineData("for t in a b; do git push origin $t; done")]
    [InlineData("! git push origin v1")]
    [InlineData("")]
    public void AnythingElseIsNot(string line)
    {
        Assert.False(Bearing(line));
    }

    private static List<ShellCall> Calls(string text, bool original)
    {
        var calls = new List<ShellCall>();
        PushShell.Walk(PushShell.Parse(text).Events, new PushState("/w"), "/w", calls.Add, original);
        return calls;
    }

    [Fact]
    public void TheProgramWordsOfACommandLineAreReadBackFromTheirOffsets()
    {
        const string text = "cd x && (for p in A; do /a/with-slot.sh --eta s dotnet t; done)";

        var programs = Calls(text, original: true).SelectMany(c => c.Programs!).Select(p => text[p.Start..p.End]);

        Assert.Equal(["cd", "for", "/a/with-slot.sh", "dotnet", "done"], programs);
    }

    [Fact]
    public void ACallNoTextOffsetsApplyToHasNoProgramWords()
    {
        const string text = "sh -c '/a/run.sh x'; echo `/b/run.sh`";

        var run = Calls(text, original: true).Where(c => c.Name.EndsWith("run.sh", StringComparison.Ordinal)).ToList();

        Assert.Equal(2, run.Count);
        Assert.All(run, c => Assert.Null(c.Programs));
        Assert.All(Calls(text, original: false), c => Assert.Null(c.Programs));
    }
}
