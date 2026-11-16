using Ah.Hierarchy;

namespace Ah.Cli.Composition;

/// <summary>
/// ah2-52 item 3: the herdr answer for the pane of a member whose registration is not live, kept for <see cref="StoppedProbeReuseMs"/> per
/// (dir, team, member, pane id) and asked again at once when the member's registration or its team's record changed, or after
/// <see cref="Forget"/> (the doctor's rebuild). A stopped member that comes back without registering is noticed up to that long later; one
/// that registers is noticed at once, since a live registration is never probed. An answer herdr could not give is never held, except that
/// its server is not running, which is a definite fact about the herdr instance. Only the
/// processes lane's member facts use it; every other liveness reading asks herdr itself.
/// </summary>
internal sealed class StoppedProbeReuse(Func<long> nowMs)
{
    internal const long StoppedProbeReuseMs = 60_000;

    private sealed record Held(long AtMs, MemberRegistrationLiveness? Registration, string Team, PaneProbe Answer);

    private readonly Dictionary<(string Dir, string Team, string Member, string Pane), Held> _held = [];

    /// <summary>How many answers are held, for the tests of the bound.</summary>
    internal int Count
    {
        get
        {
            lock (_held)
            {
                return _held.Count;
            }
        }
    }

    /// <summary>
    /// The held answer for the member's pane when it is younger than the window (an age below zero is not) and was taken with this
    /// registration and team record; else <paramref name="probe"/>'s, held when it says it may be (a definite answer, or herdr's server not
    /// running). <paramref name="teamStamp"/> is asked only here.
    /// </summary>
    internal PaneProbe Probe(string dir, string team, string member, string pane, MemberRegistrationLiveness? registration, Func<string> teamStamp, Func<(PaneProbe Answer, bool Holdable)> probe)
    {
        var now = nowMs();
        var key = (dir, team, member, pane);
        var stamp = teamStamp();
        lock (_held)
        {
            if (_held.TryGetValue(key, out var held) && Fresh(held, now) && held.Registration == registration && held.Team == stamp)
            {
                return held.Answer;
            }
        }

        var (answer, holdable) = probe();
        lock (_held)
        {
            if (!holdable)
            {
                _ = _held.Remove(key);
            }
            else
            {
                _held[key] = new Held(now, registration, stamp, answer);
            }
        }

        return answer;
    }

    private static bool Fresh(Held held, long now) => now - held.AtMs is >= 0 and < StoppedProbeReuseMs;

    /// <summary>Drops the answers of <paramref name="dir"/> for (team, member, pane) its pass did not ask about, and every answer no longer fresh.</summary>
    internal void Keep(string dir, IReadOnlySet<(string Team, string Member, string Pane)> asked)
    {
        var now = nowMs();
        lock (_held)
        {
            foreach (var key in _held.Where(h => (h.Key.Dir == dir && !asked.Contains((h.Key.Team, h.Key.Member, h.Key.Pane))) || !Fresh(h.Value, now)).Select(static h => h.Key).ToList())
            {
                _ = _held.Remove(key);
            }
        }
    }

    /// <summary>Drops every held answer, so the next pass asks herdr about each member again: the doctor's rebuild.</summary>
    internal void Forget()
    {
        lock (_held)
        {
            _held.Clear();
        }
    }
}
