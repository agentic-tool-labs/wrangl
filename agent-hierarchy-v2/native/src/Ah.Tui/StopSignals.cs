using System.Runtime.InteropServices;

namespace Ah.Tui;

/// <summary>
/// SIGINT, SIGTERM and SIGHUP cancel <see cref="Token"/> so the client can stop in order and put the terminal back. A client
/// still running <see cref="Grace"/> after the first signal is exited with 128 + the signal number: once its terminal has gone
/// away the orderly stop never completes (the console reader spins on the hung-up descriptor), and the default termination
/// the handlers suppress would otherwise never come.
/// </summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class StopSignals : IDisposable
{
    internal static readonly TimeSpan Grace = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cancel = new();
    private readonly List<PosixSignalRegistration> _registrations = [];
    private int _first;

    internal StopSignals()
    {
        foreach (var (signal, number) in new[] { (PosixSignal.SIGINT, 2), (PosixSignal.SIGTERM, 15), (PosixSignal.SIGHUP, 1) })
        {
            _registrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                Stop(number);
            }));
        }
    }

    internal CancellationToken Token => _cancel.Token;

    private void Stop(int number)
    {
        if (Interlocked.CompareExchange(ref _first, number, 0) != 0)
        {
            return;
        }

        // Armed before cancelling: a cancellation callback that blocks must not keep the deadline from starting.
        var deadline = new Thread(() =>
        {
            Thread.Sleep(Grace);
            Environment.Exit(128 + number);
        })
        { IsBackground = true, Name = "ahd-tui stop deadline" };
        deadline.Start();
        _cancel.Cancel();
    }

    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }

        _cancel.Dispose();
    }
}
