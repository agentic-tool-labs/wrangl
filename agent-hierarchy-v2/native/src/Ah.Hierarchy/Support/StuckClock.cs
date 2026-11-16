namespace Ah.Hierarchy;

/// <summary>Whether an open task is stuck at an instant and, if so, since when and for how long.</summary>
/// <param name="Stuck">True when the task is stuck.</param>
/// <param name="SinceMs">When it became stuck (the base plus the stall limit), in epoch milliseconds; null when not stuck.</param>
/// <param name="IdleForSeconds">Whole seconds since the base; null when not stuck.</param>
public sealed record StuckResult(bool Stuck, double? SinceMs, double? IdleForSeconds);

/// <summary>
/// The one rule for a stuck task: its assignee is live and working or idle, and the later of the task's creation and the assignee's last
/// activity is at least the stall limit old. It reads no clock, file or process; <see cref="Compute"/> is the one place that says.
/// </summary>
internal static class StuckClock
{
    internal static StuckResult Compute(bool assigneeLive, string? activity, double createdMs, double lastActivityMs, double nowMs, double stallMs)
    {
        var baseMs = Math.Max(createdMs, lastActivityMs);
        if (!assigneeLive || activity is not ("working" or "idle") || baseMs <= 0 || nowMs - baseMs < stallMs)
        {
            return new StuckResult(false, null, null);
        }

        return new StuckResult(true, baseMs + stallMs, Math.Floor((nowMs - baseMs) / 1000));
    }
}
