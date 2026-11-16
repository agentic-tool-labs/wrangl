using Microsoft.Extensions.Hosting;

namespace Ah.Daemon;

/// <summary>
/// Replaces the console lifetime so the host installs no signal handling of its own and never stops the listener on
/// SIGTERM: the daemon's own drain keeps answering 503 <c>draining</c> until it exits.
/// </summary>
internal sealed class HostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
