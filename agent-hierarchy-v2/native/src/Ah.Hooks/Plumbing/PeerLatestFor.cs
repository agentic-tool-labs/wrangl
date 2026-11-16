using Ah.Hierarchy;
using Ah.Hooks.Gates;

namespace Ah.Hooks.Plumbing;

/// <summary>Chooses the backing of the peer-log seam for one hook event.</summary>
internal static class PeerLatestFor
{
    /// <summary>How long a hook waits for the daemon's answer before it reads the peer log itself.</summary>
    internal static readonly TimeSpan DaemonWait = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// The daemon-backed seam for the event's hierarchy dir when a daemon client is wired and the daemon is not switched off, else the
    /// local one.
    /// </summary>
    internal static IPeerLatest Of(GateContext c)
    {
        var local = HierarchyModule.CreateLocalPeerLatest(c.Services.Hierarchy.Peers);
        return c.Services.Advisory is { } advisory && c.Services.Environment.GetVariable("AH_DAEMON") != "off"
            ? new DaemonPeerLatest(advisory.Daemon, c.Services.Config.HierarchyDir(c.Cwd()), local, DaemonWait)
            : local;
    }
}
