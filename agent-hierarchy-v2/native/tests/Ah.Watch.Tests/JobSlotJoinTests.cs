using Ah.Abstractions;

namespace Ah.Watch.Tests;

/// <summary>ah2-296 L4': an <c>ah test run</c> job is joined to its slot (its <c>ah-test-run</c> process picked) whatever comes before it on the command line.</summary>
public sealed class JobSlotJoinTests
{
    private static readonly DateTimeOffset Sent = DateTimeOffset.Parse("2026-11-19T17:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    private const int Runner = 4102;

    /// <summary>The processes below the owner: the tool call's shell, the bucket script (the slot's holder) and the command it runs.</summary>
    private static readonly ProcessNode[] Below =
    [
        new(4100, Sent, "/bin/zsh -c T=/tmp/x; cd /w; TMPDIR=$T ah test run --eta small -- python3 -I s.py a", 4000),
        new(Runner, Sent.AddSeconds(1), "/bin/bash /p/bin/ah-test-run --eta small -- python3 -I s.py a", 4100),
        new(4300, Sent.AddMinutes(5), "python3 -I s.py a", Runner),
    ];

    private static JobFact Job(string label) =>
        new("j1", "t", "impl", "m1", "slot", label, null, "small", Sent.ToUnixTimeMilliseconds(), null, null, false, false, []);

    [Theory]
    [InlineData("T=/tmp/x; cd /w; TMPDIR=$T ah test run --eta small -- python3 -I s.py a")]
    [InlineData("dotnet build && ah test run --eta small -- python3 -I s.py a")]
    [InlineData("ah test run --eta small -- python3 -I s.py a")]
    public void ACompoundLineReachingAhTestRunPicksTheBucketScript(string label) =>
        Assert.Equal(Runner, WatchModule.JobPick(Job(label), Below)?.Pid);

    [Theory]
    [InlineData("echo 'ah test run --eta small -- python3 -I s.py a'")]
    [InlineData("python3 -I s.py a # ah test run --eta small -- python3")]
    public void NegativeALineThatOnlyMentionsAhTestRunDoesNotPickTheBucketScript(string label) =>
        Assert.NotEqual(Runner, WatchModule.JobPick(Job(label), Below)?.Pid);

    [Fact]
    public void NegativeABucketScriptAnotherJobHoldsIsNotPicked() =>
        Assert.Null(WatchModule.JobPick(Job("T=/tmp/x; cd /w; ah test run --eta small -- python3 -I s.py a"), Below, new HashSet<int> { Runner }));
}
