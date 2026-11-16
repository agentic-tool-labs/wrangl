using System.Diagnostics;

namespace Ah.Mesh;

/// <summary>
/// The laps of one <c>POST /v1/send</c>, for the daemon's "send slow" line: the route laps its own steps and hands <see cref="Lap"/> to
/// the mailbox send for its steps. Laps are taken one after another, never at once. Logging only: nothing reads them but the line.
/// </summary>
internal sealed class SendPhases
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly List<string> _laps = [];
    private long _last;

    internal SendPhases() => _last = _started;

    internal void Lap(string name)
    {
        var now = Stopwatch.GetTimestamp();
        _laps.Add(name + "=" + Ms(now - _last));
        _last = now;
    }

    internal long TotalMs => Ms(Stopwatch.GetTimestamp() - _started);

    internal string Text => string.Join(' ', _laps);

    private static long Ms(long ticks) => (long)(ticks * 1000.0 / Stopwatch.Frequency);
}
