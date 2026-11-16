using Ah.Abstractions;
using Ah.Core.Time;

namespace Ah.Mesh;

/// <summary>
/// Decides whether a registration's claimed process is authentic: the registrant's own pid or one of its ancestors, still the same
/// process it was when the registration named it.
/// </summary>
public interface IRegistrationBinder
{
    /// <summary>
    /// Whether <paramref name="claimedPid"/> is above 1 and alive, is <paramref name="registrantPid"/> or one of its ancestors
    /// (a walk of at most 64 parents), and has the start time <paramref name="claimedPidStart"/> now. False when any of those
    /// cannot be established, including when the registrant is unknown.
    /// </summary>
    bool Binds(long? claimedPid, string? claimedPidStart, int? registrantPid);

    /// <summary>The start time of a live process as the system <c>ps</c> printed it, or null when it cannot answer.</summary>
    string? StartOf(long pid);

    /// <summary>How <paramref name="recorded"/> stands against the start time <paramref name="pid"/> has now, the legacy local form counted.</summary>
    StartMatch StartMatches(long pid, string? recorded) =>
        string.IsNullOrEmpty(recorded) || StartOf(pid) is not { } now ? StartMatch.Unknown : now == recorded ? StartMatch.Same : StartMatch.Different;

    /// <summary>The start time to compare against <paramref name="recorded"/>: the recorded text itself when it is the legacy local form of the process's start.</summary>
    string? StartAs(long pid, string? recorded) => StartOf(pid);

    /// <summary>
    /// Whether no process from <paramref name="pid"/> itself up to pid 1 runs the Claude harness: true when the whole line was read
    /// and none does, false when one does, null when the line could not be read to its end (a link unreadable, a parent pid of 0,
    /// or more than 64 hops). A caller treats null as a refusal, never as "no harness".
    /// </summary>
    bool? NoHarnessFrom(long pid) => null;

    /// <summary>Whether <paramref name="pid"/> itself runs the Claude harness, by its command line alone; false when it cannot be read.</summary>
    bool IsHarnessProcess(long pid) => false;
}

internal sealed class RegistrationBinder(Func<int, bool> alive, IProcessTable table) : IRegistrationBinder
{
    public bool Binds(long? claimedPid, string? claimedPidStart, int? registrantPid)
    {
        if (claimedPid is not { } claimed || claimed <= 1 || claimed > int.MaxValue
            || registrantPid is not { } registrant || registrant <= 1
            || string.IsNullOrEmpty(claimedPidStart))
        {
            return false;
        }

        var pid = (int)claimed;
        if (!alive(pid))
        {
            return false;
        }

        if (pid != registrant && table.HasAncestor(registrant, pid) != true)
        {
            return false;
        }

        return Ah.Core.Time.ProcessIdentity.Same(table, pid, claimedPidStart);
    }

    public string? StartOf(long pid) => table.StartTime(pid);

    public StartMatch StartMatches(long pid, string? recorded) => ProcessStart.Compare(table, pid, recorded);

    public string? StartAs(long pid, string? recorded) => ProcessStart.CurrentAs(table, pid, recorded);

    public bool? NoHarnessFrom(long pid)
    {
        if (pid <= 1 || pid > int.MaxValue)
        {
            return null;
        }

        return !CallerIdentity.HarnessOnLine(table, (int)pid);
    }

    public bool IsHarnessProcess(long pid) => pid > 1 && pid <= int.MaxValue && HarnessCommand.IsHarnessProcess((int)pid, table.Command);
}
