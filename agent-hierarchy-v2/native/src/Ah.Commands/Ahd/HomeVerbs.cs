using Ah.Abstractions;

namespace Ah.Commands;

/// <summary><c>ahd home trust|untrust &lt;path&gt; [--hierarchy]</c>: the only writers of the two trust files, run from a terminal.</summary>
internal static class HomeVerbs
{
    /// <summary>The exit code of a verb that ran before the composition root provided the trust services.</summary>
    internal const int NotComposed = 70;

    [Verb("home")]
    internal static int Home(string[] rest, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(rest);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        if (CommandsModule.HomeTrustServices() is not { } services)
        {
            stderr.WriteLine("ahd home: the trust store has not been composed");
            return NotComposed;
        }

        return HomeTrust.Run(services, rest, stdout, stderr);
    }
}
