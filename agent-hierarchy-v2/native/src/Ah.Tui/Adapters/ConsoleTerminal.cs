using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Ah.Abstractions;
using Ah.Tui.Core;

namespace Ah.Tui.Adapters;

/// <summary>The process's own terminal: standard input and output, the console's size, SIGWINCH, and raw mode through the platform.</summary>
[System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
internal sealed class ConsoleTerminal : ITuiTerminal, IDisposable
{
    private const int StdinFd = 0;

    // Standard input as plain bytes: the console's own input stream line-edits and echoes on a terminal.
    private readonly Stream _input = new FileStream(new SafeFileHandle(StdinFd, ownsHandle: false), FileAccess.Read, bufferSize: 1, isAsync: false);
    private readonly Stream _output = Console.OpenStandardOutput();
    private readonly ITerminalMode _mode;
    private readonly PosixSignalRegistration _winch;

    internal ConsoleTerminal(ITerminalMode mode)
    {
        _mode = mode;
        // The console initialises its own terminal handling on first use; do it before raw mode is entered so it cannot undo it.
        _ = Console.WindowWidth;
        _winch = PosixSignalRegistration.Create(PosixSignal.SIGWINCH, _ => Resized?.Invoke());
    }

    public event Action? Resized;

    public int Columns => Console.WindowWidth > 0 ? Console.WindowWidth : 80;

    public int Rows => Console.WindowHeight > 0 ? Console.WindowHeight : 24;

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) => _input.ReadAsync(buffer, cancellationToken);

    public void Write(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _output.Write(bytes, 0, bytes.Length);
        _output.Flush();
    }

    public IDisposable? EnterRaw() => _mode.EnterRaw(StdinFd);

    public void Dispose() => _winch.Dispose();
}

/// <summary>The wall clock and real waiting.</summary>
internal sealed class SystemTuiClock : ITuiClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan duration, CancellationToken cancellationToken) => Task.Delay(duration, cancellationToken);
}
