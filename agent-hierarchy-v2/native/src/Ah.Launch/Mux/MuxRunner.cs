using Ah.Abstractions;
using Ah.Core.Text;

namespace Ah.Launch;

/// <summary>
/// What one multiplexer call came to, as <c>execFileSync</c> reports it: <paramref name="Ok"/> is a zero exit; <paramref name="Signalled"/> is
/// a child the timeout or the output cap ended; <paramref name="Status"/> is the exit code (null when it did not exit); and
/// <paramref name="ErrorMessage"/> is the message of the error that call throws, null on success.
/// </summary>
public sealed record MuxResult(bool Ok, string Stdout, string Stderr, int? Status, bool Signalled, string? ErrorMessage);

/// <summary>
/// The one place a terminal multiplexer binary (herdr, tmux) is executed: the guard first, then the process runner. The binary is
/// never reached through a shell.
/// </summary>
public interface IMuxRunner
{
    /// <summary>
    /// Runs <paramref name="binary"/> with <paramref name="args"/>, waiting at most <paramref name="timeoutMs"/> milliseconds. With
    /// <paramref name="environment"/> the child's environment is exactly that map (a null value leaves the name out); without it, this
    /// process's own.
    /// </summary>
    MuxResult Run(string binary, IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null);
}

/// <summary>The runner over <paramref name="Processes"/>, with <paramref name="Guard"/> in front.</summary>
public sealed record MuxRunner(IProcessRunner Processes, IMuxGuard Guard) : IMuxRunner
{
    public MuxResult Run(string binary, IReadOnlyList<string> args, double timeoutMs, int outputCapBytes = 1 << 20, string? workingDirectory = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ArgumentNullException.ThrowIfNull(binary);
        ArgumentNullException.ThrowIfNull(args);
        Guard.Guard(binary, args);
        var argv = new List<string>(args.Count + 1) { binary };
        argv.AddRange(args);
        TimeSpan? timeout = timeoutMs > 0 ? TimeSpan.FromMilliseconds(Math.Min(timeoutMs, int.MaxValue)) : null;
        var result = Processes.Run(new ProcessRequest(argv, workingDirectory, environment, default, timeout, outputCapBytes));
        var stdout = Utf8Text.Decode(result.Stdout.Span);
        var stderr = Utf8Text.Decode(result.Stderr.Span);
        if (result.TimedOut || result.Truncated || result.Signal is not null)
        {
            return new MuxResult(false, stdout, stderr, null, true, $"spawnSync {binary} {(result.TimedOut ? "ETIMEDOUT" : "ENOBUFS")}");
        }

        if (result.ExitCode == 0)
        {
            return new MuxResult(true, stdout, stderr, 0, false, null);
        }

        // The runner reports a binary it could not start as exit 127 with nothing on either stream.
        if (result.ExitCode == 127 && stdout.Length == 0 && stderr.Length == 0)
        {
            return new MuxResult(false, stdout, stderr, null, false, $"spawnSync {binary} ENOENT");
        }

        var message = $"Command failed: {binary} {string.Join(' ', args)}".TrimEnd(' ');
        if (stderr.Length > 0)
        {
            message += "\n" + stderr;
        }

        return new MuxResult(false, stdout, stderr, result.ExitCode, false, message);
    }
}
