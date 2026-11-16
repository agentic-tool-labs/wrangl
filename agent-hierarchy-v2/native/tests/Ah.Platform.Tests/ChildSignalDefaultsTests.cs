using System.Runtime.InteropServices;
using System.Text;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform.Tests;

/// <summary>Every way this process starts another puts the signals in <see cref="Libc.ChildDefaultSignals"/> back to their default for it.</summary>
public sealed class ChildSignalDefaultsTests
{
    private static readonly IReadOnlyDictionary<string, string?> SystemPath = new Dictionary<string, string?> { ["PATH"] = "/usr/bin:/bin" };

    [Fact]
    public void TheSharedListHoldsSigpipeAndSigusr1AndNothingElse() =>
        Assert.Equal([Libc.SigPipe, Libc.SigUsr1], Libc.ChildDefaultSignals);

    // No test here may replace or drop this host's SIGUSR1 action. At the default action a GC that must interrupt a running thread
    // ends the host with exit 158 (#381, #405), so the attempts below reset another signal; the shared list is pinned above.
    [Fact]
    public void ARunChildDoesNotInheritSigInfoOnSigusr1WhileThisProcessHandlesIt()
    {
        if (OperatingSystem.IsMacOS())
        {
            Assert.Contains(ActionOf(Libc.SigUsr1), static b => b != 0);
        }

        var result = PlatformModule.CreateProcessRunner().Run(new ProcessRequest(SignalProbe.PerlArgv("SIGUSR1"), null, SystemPath, default, null, 1 << 20));

        Assert.Equal(0, SignalProbe.FlagsOf(Encoding.UTF8.GetString(result.Stdout.Span)) & SignalProbe.SaSigInfo);
    }

    [Fact]
    public void ARunChildKeepsSigInfoOnAnotherHandledSignal()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using var handled = PosixSignalRegistration.Create((PosixSignal)31, static context => context.Cancel = true);

        var result = PlatformModule.CreateProcessRunner().Run(new ProcessRequest(SignalProbe.PerlArgv("SIGUSR2"), null, SystemPath, default, null, 1 << 20));

        Assert.Equal(SignalProbe.SaSigInfo, SignalProbe.FlagsOf(Encoding.UTF8.GetString(result.Stdout.Span)) & SignalProbe.SaSigInfo);
    }

    private const int FailedExecErrno = 7;

    private const int Einval = 22;

    private static int OtherSignal => OperatingSystem.IsMacOS() ? 31 : 12;

    private static IReadOnlyList<int> Listed => [Libc.SigPipe, OtherSignal];

    private static unsafe byte[] ActionOf(int signal)
    {
        var action = new byte[Libc.SigactionBytes];
        fixed (byte* pointer = action)
        {
            Assert.Equal(0, Libc.Sigaction(signal, null, pointer));
        }

        return action;
    }

    [Fact]
    public void AnExecAttemptRunsWithTheListedSignalsAtDefaultAndAFailedOneRestoresEachExactlyAsItWas()
    {
        using var other = PosixSignalRegistration.Create((PosixSignal)OtherSignal, static context => context.Cancel = true);
        var before = Listed.ToDictionary(s => s, ActionOf);
        var during = new Dictionary<int, byte[]>();

        var errno = PosixExec.ExecWithDefaultSignals(
            Listed,
            () =>
            {
                foreach (var signal in Listed)
                {
                    during[signal] = ActionOf(signal);
                }

                return FailedExecErrno;
            });

        Assert.Equal(FailedExecErrno, errno);
        foreach (var signal in Listed)
        {
            Assert.All(during[signal], b => Assert.Equal(0, b));
            Assert.Equal(before[signal], ActionOf(signal));
        }

        Assert.NotEqual(before[OtherSignal], during[OtherSignal]);
    }

    [Fact]
    public void AnExecOfAMissingProgramThrowsAndLeavesTheListedSignalsAsTheyWere()
    {
        using var other = PosixSignalRegistration.Create((PosixSignal)OtherSignal, static context => context.Cancel = true);
        var before = Listed.ToDictionary(s => s, ActionOf);

        Assert.Throws<IOException>(() => new PosixExec(Listed).Exec(["/nonexistent-dir/no-such-program"]));

        foreach (var signal in Listed)
        {
            Assert.Equal(before[signal], ActionOf(signal));
        }
    }

    [Fact]
    public void AnExecAttemptLeavesASignalOutsideTheListAlone()
    {
        var before = ActionOf(Libc.SigUsr1);
        byte[]? during = null;

        PosixExec.ExecWithDefaultSignals(Listed, () =>
        {
            during = ActionOf(Libc.SigUsr1);
            return FailedExecErrno;
        });

        Assert.Equal(before, during);
        Assert.Equal(before, ActionOf(Libc.SigUsr1));
    }

    [Fact]
    public void ASignalThatCannotBeResetSkipsTheExecReturnsItsErrnoAndRestoresTheOnesAlreadyReset()
    {
        using var other = PosixSignalRegistration.Create((PosixSignal)OtherSignal, static context => context.Cancel = true);
        var before = ActionOf(OtherSignal);
        var called = false;

        var errno = PosixExec.ExecWithDefaultSignals([OtherSignal, 0], () =>
        {
            called = true;
            return FailedExecErrno;
        });

        Assert.Equal(Einval, errno);
        Assert.False(called);
        Assert.Equal(before, ActionOf(OtherSignal));
    }
}
