using System.Collections.Concurrent;

namespace Ah.Mesh;

/// <summary>
/// The (pid, start) pairs one daemon process has heard over its own socket since it started: a bound register, or a heartbeat of a
/// caller the peer pid proves. Held in memory only and never loaded from the store, so a daemon on a copied home, whose rows name the
/// original home's live sessions, has heard none of them and signals none of them (b267).
/// </summary>
public interface IHeardProcesses
{
    void Add(int pid, string? start);

    bool Contains(int pid, string? start);
}

internal sealed class HeardProcesses : IHeardProcesses
{
    // ponytail: only grows; one entry per session start, bounded by the sessions a daemon lifetime sees
    private readonly ConcurrentDictionary<(int Pid, string Start), byte> _heard = new();
    private int _counted;

    public void Add(int pid, string? start)
    {
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            Ah.Core.CoreModule.CensusAdd(this, "mesh.heard_processes", static h => h._heard.Count);
        }

        if (pid > 1 && start is { Length: > 0 })
        {
            _heard[(pid, start)] = 0;
        }
    }

    public bool Contains(int pid, string? start) => start is { Length: > 0 } && _heard.ContainsKey((pid, start));
}
