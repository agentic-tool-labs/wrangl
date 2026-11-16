using Ah.Abstractions;

namespace Ah.Platform;

public static partial class PlatformModule
{
    /// <summary>Reads the pid of the process that connected an accepted local socket.</summary>
    public static IPeerIdentity CreatePeerIdentity() => new PeerIdentity();
}
