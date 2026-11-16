using System.Diagnostics;
using System.Globalization;

namespace Ah.Core.Time;

/// <summary>
/// Per-phase wall time of one CLI call, printed to stderr as <c>ah-trace &lt;phase&gt; &lt;ms&gt;</c> when the environment variable
/// <c>AH_TRACE_SPANS</c> is <c>1</c>. With it unset nothing is read from a clock and nothing is allocated: <see cref="Begin"/> returns 0
/// and <see cref="End"/> and <see cref="SinceProcessStart"/> return at once.
/// </summary>
public static class PhaseTrace
{
    /// <summary>Whether spans are recorded; fixed for the life of the process.</summary>
    public static bool On { get; } = Environment.GetEnvironmentVariable("AH_TRACE_SPANS") == "1";

    /// <summary>The start mark for a span; pass it to <see cref="End"/>.</summary>
    public static long Begin() => On ? Stopwatch.GetTimestamp() : 0;

    /// <summary>Prints the time since <paramref name="begin"/> under <paramref name="phase"/>.</summary>
    public static void End(string phase, long begin)
    {
        if (On)
        {
            Print(phase, Stopwatch.GetElapsedTime(begin).TotalMilliseconds);
        }
    }

    /// <summary>Prints the time from the operating-system start of this process until now under <paramref name="phase"/>.</summary>
    public static void SinceProcessStart(string phase)
    {
        if (On)
        {
            using var self = Process.GetCurrentProcess();
            Print(phase, (DateTime.Now - self.StartTime).TotalMilliseconds);
        }
    }

    internal static string Line(string phase, double milliseconds) =>
        "ah-trace " + phase + " " + milliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private static void Print(string phase, double milliseconds) => Console.Error.WriteLine(Line(phase, milliseconds));
}
