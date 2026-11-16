using System.Net.Http.Headers;
using System.Net.Sockets;
using Ah.Abstractions;

namespace Ah.Platform.Net;

/// <summary>
/// One HTTP/1.1 request over a unix socket per call, on a connection of its own (as Node's <c>agent: false</c> does).
/// The request carries exactly one <c>Host</c> header (<c>ahd</c>) and the client's version in <c>Ah-Client-Version</c>.
/// </summary>
internal sealed class UnixHttpTransport(string clientVersion) : IUnixHttpTransport
{
    /// <summary>The longest socket path, in bytes, either runtime binds.</summary>
    private const int MaxSocketPathBytes = 103;

    private const string Authority = "http://ahd";

    public async Task<HttpReply> SendAsync(
        string socketPath,
        string method,
        string pathAndQuery,
        ReadOnlyMemory<byte> body,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socketPath);
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(pathAndQuery);

        // A path past the limit never reaches the endpoint constructor, which would throw an argument error.
        if (System.Text.Encoding.UTF8.GetByteCount(socketPath) > MaxSocketPathBytes)
        {
            throw new IOException($"socket path is longer than {MaxSocketPathBytes} bytes");
        }

        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(new HttpMethod(method), Authority + pathAndQuery);
        _ = request.Headers.TryAddWithoutValidation("Ah-Client-Version", clientVersion);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body.ToArray());
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return new HttpReply((int)response.StatusCode, bytes);
    }
}
