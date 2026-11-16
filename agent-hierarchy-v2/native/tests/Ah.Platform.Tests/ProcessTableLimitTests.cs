using Ah.Abstractions;

namespace Ah.Platform.Tests;

/// <summary>How long a <c>ps</c> may take is the composition's choice; a client keeps the short limit.</summary>
public sealed class ProcessTableLimitTests
{
    private sealed class Runner : IProcessRunner
    {
        public List<TimeSpan?> Limits { get; } = [];

        public ProcessResult Run(ProcessRequest request)
        {
            Limits.Add(request.Timeout);
            return new ProcessResult(1, null, false, false, default, default);
        }
    }

    /// <summary>The reading of every process times out; a one-pid ask answers when <paramref name="oneAnswers"/>.</summary>
    private sealed class SlowTable(bool oneAnswers) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request) =>
            request.Argv.Contains("-axww") || !oneAnswers
                ? new ProcessResult(null, 9, true, false, default, default)
                : new ProcessResult(0, null, false, false, System.Text.Encoding.UTF8.GetBytes("claude\n"), default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACommandIsAskedOfTheOnePidWhenTheScopesReadingTimesOut(bool shared)
    {
        using var scope = Ah.Core.Time.ProcessSnapshotScope.Begin(shared);

        Assert.Equal("claude", PlatformModule.CreateProcessTable(new SlowTable(oneAnswers: true)).Command(Environment.ProcessId));
    }

    private sealed class Counting : IProcessRunner
    {
        public int Readings { get; private set; }

        public ProcessResult Run(ProcessRequest request)
        {
            Readings += request.Argv.Contains("-axww") ? 1 : 0;
            return new ProcessResult(0, null, false, false, System.Text.Encoding.UTF8.GetBytes("claude\n"), default);
        }
    }

    [Fact]
    public void AScopesFirstQuestionsNeverWaitOnAReadingOfEveryProcessButALaterOneTakesIt()
    {
        var runner = new Counting();
        var table = PlatformModule.CreateProcessTable(runner);
        using var scope = Ah.Core.Time.ProcessSnapshotScope.Begin();

        for (var i = 0; i < 4; i++)
        {
            _ = table.Command(Environment.ProcessId);
        }

        Assert.Equal(0, runner.Readings);
        _ = table.Command(Environment.ProcessId);
        Assert.Equal(1, runner.Readings);
    }

    [Fact]
    public void ACommandNoPsCanReadStaysUnknown()
    {
        using var scope = Ah.Core.Time.ProcessSnapshotScope.Begin(shared: true);

        Assert.Null(PlatformModule.CreateProcessTable(new SlowTable(oneAnswers: false)).Command(Environment.ProcessId));
    }

    private sealed class TtyAnswer(string? text) : IProcessRunner
    {
        public ProcessResult Run(ProcessRequest request) => text is null
            ? new ProcessResult(1, null, false, false, default, default)
            : new ProcessResult(0, null, false, false, System.Text.Encoding.UTF8.GetBytes(text + "\n"), default);
    }

    [Theory]
    [InlineData("??", "")] // no terminal is a known none
    [InlineData("ttys003", "ttys003")]
    [InlineData(null, null)] // negative: a failed ps is unknown, not none
    public void ATerminalPlaceholderReadsAsNoneAndAFailedPsAsUnknown(string? printed, string? expected)
    {
        Assert.Equal(expected, PlatformModule.CreateProcessTable(new TtyAnswer(printed)).Tty(Environment.ProcessId));
    }

    [Fact]
    public void ALongerLimitReachesEveryPs()
    {
        var runner = new Runner();

        _ = PlatformModule.CreateProcessTable(runner, TimeSpan.FromSeconds(8)).Tty(Environment.ProcessId);

        Assert.NotEmpty(runner.Limits);
        Assert.All(runner.Limits, static l => Assert.Equal(TimeSpan.FromSeconds(8), l));
    }

    [Fact]
    public void WithoutOneTheLimitStaysTwoSeconds()
    {
        var runner = new Runner();

        _ = PlatformModule.CreateProcessTable(runner).Tty(Environment.ProcessId);

        Assert.NotEmpty(runner.Limits);
        Assert.All(runner.Limits, static l => Assert.Equal(TimeSpan.FromSeconds(2), l));
    }
}
