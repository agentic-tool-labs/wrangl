namespace Ah.Core.Time;

/// <summary>
/// <see cref="LivenessRules.ObserverSeesAReference"/> for one observer, starting from the process that proved it last time. That process
/// still shows the start it had: then the full check would be true too, and one start read answers it. Else the full check runs: it reads
/// the parent chain and then the start of every registered pid, which a home with hundreds of registrations pays on every liveness judgement.
/// </summary>
public sealed class ObserverReference
{
    private sealed record Seen(long Pid, string Start, bool Parent);

    private Seen? _seen;

    /// <summary>Whether the observer <paramref name="observerPid"/> sees another process; the same answer as <see cref="LivenessRules.ObserverSeesAReference"/>.</summary>
    public bool Sees(Ah.Abstractions.IProcessTable table, int observerPid, IReadOnlyCollection<(long Pid, string? Start)> registered, long? exceptPid = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(registered);
        // a parent is a parent until it exits, and the full check takes it whatever the subject; a registered one must still be listed and not be the subject
        if (Volatile.Read(ref _seen) is { } last && (last.Parent || (last.Pid != exceptPid && registered.Contains((last.Pid, last.Start))))
            && table.StartTime(last.Pid) is { } now && ProcessStart.IsSame(ProcessStart.Compare(now, static () => null, last.Start)))
        {
            return true;
        }

        var found = LivenessRules.ReferenceSeen(table, observerPid, registered, exceptPid);
        Volatile.Write(ref _seen, found is { } f ? new Seen(f.Pid, f.Start, f.Parent) : null);
        return found is not null;
    }
}
