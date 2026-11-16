using Ah.Abstractions;

namespace Ah.Core.Time;

/// <summary>
/// The one question every identity check asks of a recorded pid: is it still the process that was recorded? A pid is reused once
/// its process ends, so a pid alone proves nothing; the start time recorded beside it tells the two apart.
/// </summary>
public static class ProcessIdentity
{
    /// <summary>The recorded start is the one the process holding <paramref name="pid"/> has now (UTC, or the local form older records hold). False when nothing was recorded or the table cannot say.</summary>
    public static bool Same(IProcessTable table, long pid, string? recordedStart) =>
        ProcessStart.IsSame(ProcessStart.Compare(table, pid, recordedStart));

    /// <summary>The table says the pid now has another start time: it was reused. False when nothing was recorded or the table cannot say.</summary>
    public static bool Reused(IProcessTable table, long pid, string? recordedStart) =>
        ProcessStart.Compare(table, pid, recordedStart) == StartMatch.Different;
}
