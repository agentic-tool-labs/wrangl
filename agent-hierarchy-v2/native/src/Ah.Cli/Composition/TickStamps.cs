using Ah.Core.Json;
using Ah.Core.Time;

namespace Ah.Cli.Composition;

/// <summary>
/// The tick stamps (<see cref="StallStore.Ticks"/>): <c>{job: {last_run_at, daemon_pid}}</c>, stamped by each daemon job on every run so that
/// <c>roster stall-diagnose</c> can tell a stopped job from a stopped daemon without asking the daemon. Each stamp merges its one field in
/// the database's own transaction, so stamps from different threads never drop each other's row.
/// </summary>
internal sealed class TickStamps(ServiceGraph g)
{
    public void Stamp(string job)
    {
        try
        {
            StallStore.Merge(g, StallStore.Ticks, job, new JsonObject { ["last_run_at"] = Timestamps.ToIsoString(g.Io.Clock.UtcNow), ["daemon_pid"] = Environment.ProcessId });
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // a stamp that cannot be written only makes the diagnosis say the job looks stopped
        }
    }
}
