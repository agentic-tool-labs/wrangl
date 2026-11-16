using Ah.Cli.Verbs;
using Ah.Config;

namespace Ah.Cli.Tests;

/// <summary><c>ah rules</c>: the argument handling and the step table's output; the send decision itself is covered where the mesh is.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
public sealed class RulesVerbTests : IDisposable
{
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private (int Code, string Stdout, string Stderr) Run(params string[] args)
    {
        var context = new CliContext(
            new FakeEnv(_sandbox.Home, _sandbox.AhHome, new Dictionary<string, string?> { ["AH_HOME"] = _sandbox.AhHome, ["HOME"] = _sandbox.Home }),
            new RecordingExec(),
            new FakeRunner(),
            "ah",
            _sandbox.Executable("install/ah"),
            _sandbox.Home,
            Ah.Platform.PlatformModule.CurrentUid());
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = RulesVerb.Run(context, args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Theory]
    [InlineData("implement-done")]
    [InlineData("review-impl-defect")]
    [InlineData("review-spec-defect")]
    public void NextStepPrintsTheTablesStepForAnEvent(string eventName)
    {
        var run = Run("next-step", eventName);

        Assert.Equal(0, run.Code);
        Assert.Equal(ConfigModule.NextStepFor(eventName) + "\n", run.Stdout.ReplaceLineEndings("\n"));
        Assert.Equal(string.Empty, run.Stderr);
    }

    [Theory]
    [InlineData("review-pass")]
    [InlineData("blocked")]
    [InlineData("spec-done")]
    [InlineData("nonsense")]
    public void NegativeAnUnknownEventExitsNonZeroAndListsTheEventsItKnows(string eventName)
    {
        var run = Run("next-step", eventName);

        Assert.NotEqual(0, run.Code);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.Contains($"unknown event '{eventName}'", run.Stderr, StringComparison.Ordinal);
        Assert.Contains(string.Join(", ", ConfigModule.NextStepEvents()), run.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void NegativeNoVerbIsAUsageError() => Assert.Equal(2, Run().Code);

    [Theory]
    [InlineData("frobnicate")]
    [InlineData("next-step")]
    [InlineData("next-step", "implement-done", "extra")]
    [InlineData("who-may-message", "a@tm")]
    [InlineData("who-may-message", "a", "b@tm")]
    [InlineData("who-may-message", "a@tm", "b@tm", "--bogus")]
    [InlineData("who-may-message", "a@tm", "b@tm", "--slug")]
    public void NegativeAMalformedQueryIsAUsageErrorAndAnswersNothing(params string[] args)
    {
        var run = Run(args);

        Assert.Equal(2, run.Code);
        Assert.Equal(string.Empty, run.Stdout);
        Assert.NotEqual(string.Empty, run.Stderr);
    }

    [Fact]
    public void AWellFormedWhoMayMessageQueryPassesTheArgumentChecksAndNeedsComposedServices()
    {
        var run = Run("who-may-message", "a@tm", "user", "--slug", "lock");

        Assert.Equal(70, run.Code);
        Assert.Contains("services have not been composed", run.Stderr, StringComparison.Ordinal);
    }
}
