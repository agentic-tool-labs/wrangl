using Ah.Abstractions;

namespace Ah.Core.Time;

/// <summary>How a recorded process start time stands against the process now holding the pid.</summary>
public enum StartMatch
{
    /// <summary>No start time was recorded, or the process table cannot say what it is now.</summary>
    Unknown,

    /// <summary>The pid now has another start time: it was reused.</summary>
    Different,

    /// <summary>The recorded text is the UTC start time the process has now.</summary>
    Same,

    /// <summary>The recorded text is the process's start time in the caller's local zone, the form older records hold.</summary>
    Legacy,
}

public static class ProcessStart
{
    /// <summary>Compares <paramref name="recorded"/> with the start time of the process now holding <paramref name="pid"/>.</summary>
    public static StartMatch Compare(IProcessTable table, long pid, string? recorded)
    {
        ArgumentNullException.ThrowIfNull(table);
        return Compare(table.StartTime(pid), () => table.LocalStartTime(pid), recorded);
    }

    /// <summary>
    /// The same comparison for a caller that holds the process's current UTC start time and a way to print its local one;
    /// <paramref name="local"/> is asked only when the UTC text differs from <paramref name="recorded"/>.
    /// </summary>
    public static StartMatch Compare(string? current, Func<string?> local, string? recorded)
    {
        ArgumentNullException.ThrowIfNull(local);
        if (string.IsNullOrEmpty(recorded) || current is null)
        {
            return StartMatch.Unknown;
        }

        if (current == recorded)
        {
            return StartMatch.Same;
        }

        return local() == recorded ? StartMatch.Legacy : StartMatch.Different;
    }

    /// <summary>The instant a UTC start time (<see cref="IProcessTable.StartTime"/>) names; false for any other shape.</summary>
    public static bool TryParseUtc(string? text, out DateTimeOffset started)
    {
        started = default;
        if (!Timestamps.TryParseProcessStart(text, TimeZoneInfo.Utc, out var ms))
        {
            return false;
        }

        started = DateTimeOffset.FromUnixTimeMilliseconds((long)ms);
        return true;
    }

    /// <summary>
    /// The start time to hand to a comparison against <paramref name="recorded"/>: the recorded text itself when it is the
    /// legacy local form of the process's start, so that it still compares equal, otherwise the UTC start time now.
    /// </summary>
    public static string? CurrentAs(IProcessTable table, long pid, string? recorded) =>
        Compare(table, pid, recorded) == StartMatch.Legacy ? recorded : table.StartTime(pid);

    /// <summary>Whether the recorded start is the process's own, in either form.</summary>
    public static bool IsSame(StartMatch match) => match is StartMatch.Same or StartMatch.Legacy;
}
