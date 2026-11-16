using System.Text;
using Ah.Abstractions;

namespace Ah.Cli.Tests;

/// <summary>A real <c>ps</c> about this process or its ancestors is asked once; anything else still reaches the machine every time.</summary>
public sealed class FakeRunnerSelfChainTests
{
    private sealed class Counting(Func<string[], ProcessResult> answer) : IProcessRunner
    {
        internal int Calls { get; private set; }

        public ProcessResult Run(ProcessRequest request)
        {
            Calls++;
            return answer([.. request.Argv]);
        }
    }

    private static readonly string Self = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static ProcessResult Said(string text, int code = 0) => new(code, null, false, false, Encoding.UTF8.GetBytes(text), default);

    // a TZ of its own, so an answer another test in this process has kept is not this test's
    private static ProcessRequest Ps(string field, string pid, string zone) =>
        new(["/bin/ps", "-o", field, "-p", pid], Environment: new Dictionary<string, string?> { ["TZ"] = zone });

    private static (FakeRunner Runner, Counting Real) Rig(Func<string[], ProcessResult> answer)
    {
        var real = new Counting(answer);
        return (new FakeRunner { PsPassthrough = real }, real);
    }

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void AnAnswerAboutThisProcessIsAskedOnceAndStillRecordedEachTime()
    {
        var (runner, real) = Rig(_ => Said("1\n"));
        var zone = Guid.NewGuid().ToString("N");

        Assert.Equal("1\n", Encoding.UTF8.GetString(runner.Run(Ps("ppid=", Self, zone)).Stdout.Span));
        Assert.Equal("1\n", Encoding.UTF8.GetString(runner.Run(Ps("ppid=", Self, zone)).Stdout.Span));

        Assert.Equal(1, real.Calls);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void TheParentItNamedIsPartOfTheChain()
    {
        const string parent = "424242";
        var (runner, real) = Rig(argv => argv[^1] == Self ? Said(parent + "\n") : Said("Mon\n"));
        var zone = Guid.NewGuid().ToString("N");

        _ = runner.Run(Ps("ppid=", Self, zone));
        _ = runner.Run(Ps("lstart=", parent, zone));
        _ = runner.Run(Ps("lstart=", parent, zone));

        Assert.Equal(2, real.Calls);
    }

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void NegativeAnotherProcessIsAskedEveryTime()
    {
        var (runner, real) = Rig(_ => Said("1\n"));
        var zone = Guid.NewGuid().ToString("N");

        _ = runner.Run(Ps("ppid=", "99999991", zone));
        _ = runner.Run(Ps("ppid=", "99999991", zone));

        Assert.Equal(2, real.Calls);
    }

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void NegativeAnotherFieldOfThisProcessIsAskedEveryTime()
    {
        var (runner, real) = Rig(_ => Said("sh\n"));
        var zone = Guid.NewGuid().ToString("N");

        _ = runner.Run(Ps("command=", Self, zone));
        _ = runner.Run(Ps("command=", Self, zone));

        Assert.Equal(2, real.Calls);
    }

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void NegativeAFailedAnswerIsNotKept()
    {
        var (runner, real) = Rig(_ => Said(string.Empty, code: 1));
        var zone = Guid.NewGuid().ToString("N");

        Assert.Equal(1, runner.Run(Ps("lstart=", Self, zone)).ExitCode);
        Assert.Equal(1, runner.Run(Ps("lstart=", Self, zone)).ExitCode);

        Assert.Equal(2, real.Calls);
    }

    [Fact(Skip = "pruned 2026-11-14: a test of a test helper or of message wording; disabled for later removal")]
    public void NegativeAnAnswerInOneZoneDoesNotAnswerAnother()
    {
        var (runner, real) = Rig(_ => Said("Mon\n"));

        _ = runner.Run(Ps("lstart=", Self, Guid.NewGuid().ToString("N")));
        _ = runner.Run(Ps("lstart=", Self, Guid.NewGuid().ToString("N")));

        Assert.Equal(2, real.Calls);
    }
}
