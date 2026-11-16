using Ah.Abstractions;
using Ah.DaemonClient;

namespace Ah.Commands;

/// <summary>The daemon's lifecycle verbs: <c>ahd start|stop|restart|drain|status|health|version|logs|heal</c>.</summary>
internal static class LifecycleVerbs
{
    /// <summary>The exit code of a verb that ran before the composition root built the daemon client.</summary>
    internal const int NotComposed = 70;

    [Verb("start")]
    internal static int Start(string[] rest, TextWriter stdout, TextWriter stderr) => Run("start", rest, stdout, stderr);

    [Verb("stop")]
    internal static int Stop(string[] rest, TextWriter stdout, TextWriter stderr) => Run("stop", rest, stdout, stderr);

    [Verb("restart")]
    internal static int Restart(string[] rest, TextWriter stdout, TextWriter stderr) => Run("restart", rest, stdout, stderr);

    [Verb("drain")]
    internal static int Drain(string[] rest, TextWriter stdout, TextWriter stderr) => Run("drain", rest, stdout, stderr);

    [Verb("status")]
    internal static int Status(string[] rest, TextWriter stdout, TextWriter stderr) => Run("status", rest, stdout, stderr);

    [Verb("health")]
    internal static int Health(string[] rest, TextWriter stdout, TextWriter stderr) => Run("health", rest, stdout, stderr);

    [Verb("version")]
    internal static int Version(string[] rest, TextWriter stdout, TextWriter stderr) => Run("version", rest, stdout, stderr);

    [Verb("logs")]
    internal static int Logs(string[] rest, TextWriter stdout, TextWriter stderr) => Run("logs", rest, stdout, stderr);

    [Verb("heal")]
    internal static int Heal(string[] rest, TextWriter stdout, TextWriter stderr) => Run("heal", rest, stdout, stderr);

    /// <summary>
    /// The CLI's synchronous entry boundary: a verb is a plain function of its arguments, and nothing else is running on
    /// this thread, so it waits for the lifecycle's task here.
    /// </summary>
    private static int Run(string verb, string[] rest, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (DaemonClientModule.Lifecycle is not { } lifecycle)
        {
            stderr.WriteLine($"ahd {verb}: the daemon client has not been composed");
            return NotComposed;
        }

        return lifecycle.RunAsync(verb, rest, stdout, stderr).GetAwaiter().GetResult();
    }
}
