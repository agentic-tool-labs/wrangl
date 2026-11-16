using System.Globalization;

namespace Ah.Platform.Tests;

/// <summary>A <c>perl</c> one-liner that prints the <c>sa_flags</c> its own process finds on a signal, and the number it printed.</summary>
internal static class SignalProbe
{
    internal const int SaSigInfo = 0x40;

    internal static IReadOnlyList<string> PerlArgv(string signalName) =>
        ["/usr/bin/perl", "-MPOSIX", "-e", $"my $o = POSIX::SigAction->new; sigaction({signalName}, undef, $o); print 'flags=', $o->flags, \"\\n\";"];

    internal static int FlagsOf(string output)
    {
        Assert.StartsWith("flags=", output, StringComparison.Ordinal);
        return int.Parse(output["flags=".Length..].Trim(), CultureInfo.InvariantCulture);
    }
}
