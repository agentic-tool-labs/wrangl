namespace Ah.Roster.Tests;

/// <summary>b458: only a real prompt's option block classifies as a prompt; the watch raises member-blocked on nothing less.</summary>
public sealed class BlockedScreenTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Theory]
    [InlineData("blocked")]
    [InlineData("working")]
    [InlineData(null)]
    public void TheSpinnerOverAnEmptyComposerFrom2118IsNoPrompt(string? agentStatus)
    {
        var seen = RosterModule.CreateScreenRules().RecognizeScreen("claude", Fixture("claude-spinner-empty-composer.txt"), agentStatus);

        Assert.True(seen["prompt"].IsNull);
    }

    [Fact]
    public void TheRealPermissionPromptStillClassifiesAsPermission()
    {
        var seen = RosterModule.CreateScreenRules().RecognizeScreen("claude", Fixture("tmux-4-permission.txt"), "blocked");

        Assert.Equal("permission", seen["prompt"].AsString());
    }
}
