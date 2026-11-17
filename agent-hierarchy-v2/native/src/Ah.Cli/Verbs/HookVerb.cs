using System.Text;
using Ah.Abstractions;
using Ah.Cli.Composition;
using Ah.Hooks;

namespace Ah.Cli.Verbs;

internal static class HookVerb
{
    /// <summary>Whether the arguments are <c>hook …</c> or <c>ah hook …</c>: the one path that must fail closed before anything is built.</summary>
    internal static bool IsHookInvocation(string[] args) =>
        (args.Length > 0 && args[0] == "hook") || (args.Length > 1 && args[0] == "ah" && args[1] == "hook");

    /// <summary>The arguments after <c>hook</c> (or <c>ah hook</c>).</summary>
    internal static string[] AfterHook(string[] args) => args[0] == "ah" ? args[2..] : args[1..];

    /// <summary>
    /// <c>hook [--gate] &lt;name&gt;</c>: runs the named hook natively on the event read whole from standard input and writes
    /// its stdout. The name must be lowercase letters, digits and hyphens; only a malformed name is a usage error (exit 2).
    /// Nothing else escapes: the whole path, including building the host (<paramref name="context"/> null), composing the
    /// services, reading standard input and writing the answer, runs under one handler. A hook that cannot complete before a
    /// session is identified is silent with one hook-errors row (exit 0), the chain included. The one case left is a
    /// gate whose deny cannot be written: it exits 2 with the reason on stderr, which Claude Code reads as a block.
    /// </summary>
    internal static int Run(
        CliContext? context,
        string[] rest,
        TextWriter stdout,
        TextWriter stderr,
        Func<CliContext>? makeContext = null,
        Func<CliContext, HookBundle>? compose = null,
        Func<IHookErrorLog?>? fallbackLog = null,
        TimeSpan? bound = null)
    {
        if (!TryParse(rest, out var gate, out var name))
        {
            stderr.WriteLine($"ahd: unknown hook {name}");
            return 2;
        }

        var isGate = HooksModule.FailsClosed(name, gate);
        IHookErrorLog? errors = null;
        HookOutcome Body()
        {
            try
            {
                var ctx = context ?? (makeContext ?? CliHost.Create)();
                var bundle = (compose ?? HookComposition.Create)(ctx);
                errors = bundle.ErrorLog;
                if (bundle.HomeRefusal?.Invoke() is { } refused)
                {
                    return HooksModule.Refused(name, refused, errors);
                }

                ReadOnlyMemory<byte> stdin;
                try
                {
                    var timedOut = false;
                    stdin = ctx.ReadStdin is { } read ? read() : ReadProcessStdin(out timedOut);
                    if (timedOut)
                    {
                        errors.Log(name, "stdin not closed after 5 s; the hook ran on what had arrived");
                    }
                }
                catch (Exception) when (isGate)
                {
                    return HooksModule.GateInputUnusable(name, "unreadable", errors);
                }

                return HooksModule.Run(new HookRun(name, gate, stdin, errors, bundle.Services));
            }
            catch (Exception e)
            {
                // Before the composition there is no log to write to; the platform's own is tried, and if that is what failed
                // there is nowhere to report to.
                errors ??= TryFallbackLog(fallbackLog);
                return HooksModule.HookFailed(name, isGate, e, errors);
            }
        }

        // Every gate keeps its own deadline and verdict (a fail-open one included, so a slow chain is never read as an allow);
        // ask-record has a 10 s write budget for an answer that cannot be asked for again, and must still report when that write fails.
        if (HooksModule.IsGate(name, gate) || name is "posttooluse-ask-record")
        {
            return Emit(Body(), stdout, stderr);
        }

        HookOutcome? done = null;
        var worker = new Thread(() => done = Body(), maxStackSize: 16 * 1024 * 1024) { IsBackground = true };
        worker.Start();
        if (worker.Join(bound ?? Bound))
        {
            return Emit(done!, stdout, stderr);
        }

        // Past the bound the body is abandoned (the process exits on return) and the session goes on without this hook.
        var seconds = (int)(bound ?? Bound).TotalSeconds;
        var line = $"ah: the {name} hook gave up after {seconds} s and was skipped";
        try
        {
            (errors ?? TryFallbackLog(fallbackLog))?.Log(new HookErrorRecord(name, line));
        }
        catch (Exception)
        {
            // the line on stderr is the report
        }

        return Emit(new HookOutcome(ReadOnlyMemory<byte>.Empty, 0, Encoding.UTF8.GetBytes(line + "\n")), stdout, stderr);
    }

    /// <summary>The longest a hook that is not a gate may run, its daemon discovery, spawn, lock and drain waits included.</summary>
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Answers <c>hook [--gate] &lt;name&gt;</c> without running it, for a caller that has already decided the hook cannot run (an
    /// invalid <c>--plugin-root</c>): silent with one hook-errors row, exit 0. A malformed name is the same usage error as <see cref="Run"/>.
    /// </summary>
    internal static int Refuse(
        string[] rest,
        string reason,
        TextWriter stdout,
        TextWriter stderr,
        Func<IHookErrorLog?>? fallbackLog = null)
    {
        if (!TryParse(rest, out var gate, out var name))
        {
            stderr.WriteLine($"ahd: unknown hook {name}");
            return 2;
        }

        return Emit(HooksModule.Refused(name, reason, TryFallbackLog(fallbackLog)), stdout, stderr);
    }

    private static bool TryParse(string[] rest, out bool gate, out string name)
    {
        var args = rest;
        gate = false;
        if (args.Length > 0 && args[0] == "--gate")
        {
            gate = true;
            args = args[1..];
        }

        name = args.Length > 0 ? args[0] : string.Empty;
        return name.Length > 0 && name.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
    }

    private static IHookErrorLog? TryFallbackLog(Func<IHookErrorLog?>? fallback)
    {
        try
        {
            return fallback is not null ? fallback() : Ah.Platform.PlatformModule.CreateIo(null).HookErrors;
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static int Emit(HookOutcome outcome, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            if (!outcome.Stdout.IsEmpty)
            {
                stdout.Write(Encoding.UTF8.GetString(outcome.Stdout.Span));
                stdout.Flush();
            }

            if (!outcome.Stderr.IsEmpty)
            {
                stderr.Write(Encoding.UTF8.GetString(outcome.Stderr.Span));
                stderr.Flush();
            }

            return outcome.ExitCode;
        }
        catch (Exception)
        {
            // The answer could not be written. A gate's silence would read as an allow, so the only
            // blocking channel left is the exit code with the reason on stderr.
            if (outcome.Stdout.IsEmpty)
            {
                return outcome.ExitCode;
            }

            try
            {
                stderr.WriteLine("ah: a gate's answer could not be written, so the call was held.");
            }
            catch (Exception)
            {
                // nothing left to report to
            }

            return 2;
        }
    }

    /// <summary>How long a hook waits for its standard input to close.</summary>
    internal static readonly TimeSpan StdinBound = TimeSpan.FromSeconds(5);

    private static ReadOnlyMemory<byte> ReadProcessStdin(out bool timedOut)
    {
        try
        {
            return ReadBounded(Console.OpenStandardInput(), StdinBound, out timedOut);
        }
        catch (IOException)
        {
            // a closed standard input is an empty event
            timedOut = false;
            return ReadOnlyMemory<byte>.Empty;
        }
    }

    /// <summary>Reads <paramref name="input"/> to its end, or for <paramref name="bound"/> at most. A parent that never
    /// closes the pipe would otherwise hold the hook until the harness kills it. On expiry the bytes read so far are
    /// returned; the reading thread is left behind, and the process exits with the hook.</summary>
    internal static ReadOnlyMemory<byte> ReadBounded(Stream input, TimeSpan bound, out bool timedOut)
    {
        var buffer = new MemoryStream();
        var gate = new object();
        var reader = Task.Run(() =>
        {
            var chunk = new byte[81920];
            int n;
            while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                lock (gate)
                {
                    buffer.Write(chunk, 0, n);
                }
            }
        });
        try
        {
            timedOut = !reader.Wait(bound);
        }
        catch (AggregateException e) when (e.InnerException is IOException or ObjectDisposedException)
        {
            // a closed standard input is an empty event
            timedOut = false;
        }

        lock (gate)
        {
            return buffer.ToArray();
        }
    }
}
