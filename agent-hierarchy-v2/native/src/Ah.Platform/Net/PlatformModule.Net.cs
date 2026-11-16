using Ah.Abstractions;
using Ah.Platform.Net;

namespace Ah.Platform;

public static partial class PlatformModule
{
    /// <summary>
    /// A transport that sends one request per connection over a daemon's unix socket and names
    /// <paramref name="clientVersion"/> in <c>Ah-Client-Version</c>.
    /// </summary>
    public static IUnixHttpTransport CreateUnixHttpTransport(string clientVersion)
    {
        ArgumentNullException.ThrowIfNull(clientVersion);
        return new UnixHttpTransport(clientVersion);
    }

    /// <summary>
    /// Sends one request over a daemon's unix socket and returns once the first line of the body has arrived, keeping the
    /// connection open: the reply stays held until the daemon ends it or the result is disposed. A reply that does not carry its
    /// first line within <paramref name="timeout"/> throws, as does a socket path that cannot be reached.
    /// </summary>
    public static Task<IHeldReply> OpenHeldAsync(
        string socketPath, string method, string pathAndQuery, string clientVersion, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socketPath);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);
        ArgumentNullException.ThrowIfNull(clientVersion);
        return HeldRequest.OpenAsync(socketPath, method, pathAndQuery, clientVersion, timeout, cancellationToken);
    }
}
