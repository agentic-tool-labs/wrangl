using Ah.Abstractions;

namespace Ah.Core.Time;

/// <summary>What the process table proves about the process a session row registered.</summary>
public enum SessionLife
{
    /// <summary>Nothing proves the process gone or replaced, so the session may still be running code.</summary>
    Counted,

    /// <summary>The pid no longer names any process.</summary>
    Gone,

    /// <summary>A live process holds the pid, but it is provably not the registered one.</summary>
    Reused,
}

/// <summary>A session row's liveness answer and the reason that decided it.</summary>
public sealed record SessionLiveness(SessionLife Life, string Reason)
{
    /// <summary>Whether the row may be left out of a check that asks if a session is still running: only proof excludes it.</summary>
    public bool Ignored => Life != SessionLife.Counted;
}

/// <summary>
/// The one judgement of whether a registered session's process is still the one that registered. Only proof excludes a row; every
/// doubt (no pid, a start time that cannot be read, a pid the user may not signal) counts it as live.
/// </summary>
public static class SessionLivenessJudge
{
    private const long NoPidStartSlackMs = 2000;
    private const long ZoneStepMs = 15 * 60 * 1000;
    private const long ZoneSpanMs = 14 * 60 * 60 * 1000;

    /// <param name="inspector">Answers whether the pid names a process at all.</param>
    /// <param name="table">Gives the start time of the process holding the pid.</param>
    /// <param name="pid">The registered pid, or null.</param>
    /// <param name="pidStart">The start time recorded beside the pid, or null.</param>
    /// <param name="updatedAt">When the row was last written, or null; it bounds when the registering process could have started.</param>
    /// <param name="bootTime">When the machine last booted, or null when unknown; a row recorded before it names a process of an earlier boot.</param>
    public static SessionLiveness Judge(IProcessInspector inspector, IProcessTable table, long? pid, string? pidStart, DateTimeOffset? updatedAt, DateTimeOffset? bootTime = null)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        ArgumentNullException.ThrowIfNull(table);
        if (pid is not { } p || p <= 0)
        {
            return new SessionLiveness(SessionLife.Counted, "no pid is recorded");
        }

        if (p > int.MaxValue)
        {
            return new SessionLiveness(SessionLife.Counted, "the recorded pid is out of range");
        }

        if (!inspector.PidAlive((int)p))
        {
            return new SessionLiveness(SessionLife.Gone, "no process has the pid");
        }

        if (bootTime is { } boot && LivenessRules.RecordedBeforeBoot(pidStart, updatedAt, boot))
        {
            return new SessionLiveness(SessionLife.Reused, LivenessRules.BeforeBootWhy(boot));
        }

        if (!string.IsNullOrEmpty(pidStart))
        {
            return JudgeRecordedStart(table, p, pidStart);
        }

        if (updatedAt is { } at && table.StartTime(p) is { } current && ProcessStart.TryParseUtc(current, out var started)
            && started.ToUnixTimeMilliseconds() > at.ToUnixTimeMilliseconds() + NoPidStartSlackMs)
        {
            return new SessionLiveness(SessionLife.Reused, "no start time was recorded, and the process started after the row was last written");
        }

        return new SessionLiveness(SessionLife.Counted, "no start time was recorded, and the process started before the row was last written or its start cannot be read");
    }

    /// <summary>
    /// Reused only when the recorded start parses and is provably another instant than the process's own. Text that differs for any
    /// other reason (another spacing, a rounded second, the local form written in another zone) is a doubt, and a doubt counts.
    /// </summary>
    private static SessionLiveness JudgeRecordedStart(IProcessTable table, long pid, string recorded)
    {
        if (ProcessStart.IsSame(ProcessStart.Compare(table, pid, recorded)))
        {
            return new SessionLiveness(SessionLife.Counted, "the process is the registered one");
        }

        if (table.StartTime(pid) is not { } nowText || !ProcessStart.TryParseUtc(nowText, out var current))
        {
            return new SessionLiveness(SessionLife.Counted, "the process's start cannot be read");
        }

        if (!ProcessStart.TryParseUtc(recorded, out var then))
        {
            return new SessionLiveness(SessionLife.Counted, "the recorded start cannot be read");
        }

        var skew = Math.Abs(then.ToUnixTimeMilliseconds() - current.ToUnixTimeMilliseconds());
        if (skew <= NoPidStartSlackMs)
        {
            return new SessionLiveness(SessionLife.Counted, "the recorded start is the process's within rounding");
        }

        var offStep = skew % ZoneStepMs;
        if (skew <= ZoneSpanMs && (offStep <= NoPidStartSlackMs || ZoneStepMs - offStep <= NoPidStartSlackMs))
        {
            return new SessionLiveness(SessionLife.Counted, "the recorded start differs by a whole time-zone step, so it may be the local form written in another zone");
        }

        return new SessionLiveness(SessionLife.Reused, "the pid now has another start time");
    }
}
