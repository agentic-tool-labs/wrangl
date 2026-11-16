using System.Net.Sockets;
using Ah.Abstractions;
using Ah.Platform.Interop;

namespace Ah.Platform;

/// <summary>
/// The connector of an accepted Unix socket. macOS answers <c>getsockopt(SOL_LOCAL, LOCAL_PEERPID)</c>; other platforms answer
/// nothing (no peer pid means the registration is stored unbound).
/// </summary>
internal sealed class PeerIdentity : IPeerIdentity
{
    private const int SolLocal = 0;
    private const int LocalPeerPid = 2;

    public int? PeerPid(Socket accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        if (!OperatingSystem.IsMacOS())
        {
            return null;
        }

        try
        {
            unsafe
            {
                var pid = 0;
                var length = (uint)sizeof(int);
                var result = Libc.GetSockOptInt((int)accepted.Handle, SolLocal, LocalPeerPid, &pid, ref length);
                return result == 0 ? pid : null;
            }
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }
}
