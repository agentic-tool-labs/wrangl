using System.Net.Sockets;

namespace Ah.Platform;

/// <summary>
/// A reply a daemon holds open after its first line: the connection is the lease, and the reply ends when the daemon ends it.
/// Disposing it closes the connection.
/// </summary>
public interface IHeldReply : IAsyncDisposable
{
    /// <summary>The HTTP status of the reply.</summary>
    int Status { get; }

    /// <summary>The first line of the body, without its newline; for a status other than 200, the whole body.</summary>
    ReadOnlyMemory<byte> FirstLine { get; }

    /// <summary>Completes when the daemon ends the reply, the connection fails, or this reply is disposed. Never faults.</summary>
    Task Ended { get; }
}

internal sealed class HeldReply : IHeldReply
{
    private readonly HttpClient _http;
    private readonly HttpResponseMessage _response;
    private readonly CancellationTokenSource _hold;
    private int _disposed;

    internal HeldReply(int status, ReadOnlyMemory<byte> firstLine, Task ended, HttpClient http, HttpResponseMessage response, CancellationTokenSource hold)
    {
        Status = status;
        FirstLine = firstLine;
        Ended = ended;
        _http = http;
        _response = response;
        _hold = hold;
    }

    public int Status { get; }

    public ReadOnlyMemory<byte> FirstLine { get; }

    public Task Ended { get; }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _hold.CancelAsync().ConfigureAwait(false);
        await Ended.ConfigureAwait(false);
        _response.Dispose();
        _http.Dispose();
        _hold.Dispose();
    }
}

/// <summary>Opens a <see cref="HeldReply"/> over a daemon's unix socket.</summary>
internal static class HeldRequest
{
    private const int MaxFirstLineBytes = 4096;

    private const int MaxErrorBodyBytes = 64 * 1024;

    internal static async Task<IHeldReply> OpenAsync(
        string socketPath, string method, string pathAndQuery, string clientVersion, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var handler = new SocketsHttpHandler
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
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var hold = new CancellationTokenSource();
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), "http://ahd" + pathAndQuery);
            _ = request.Headers.TryAddWithoutValidation("Ah-Client-Version", clientVersion);
            request.Content = new ByteArrayContent([]);
            using var opening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, hold.Token);
            opening.CancelAfter(timeout);
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, opening.Token).ConfigureAwait(false);
            var stream = await response.Content.ReadAsStreamAsync(opening.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status != 200)
            {
                var body = await ReadUpToAsync(stream, MaxErrorBodyBytes, stopAtNewline: false, opening.Token).ConfigureAwait(false);
                return new HeldReply(status, body, Task.CompletedTask, http, response, hold);
            }

            var first = await ReadUpToAsync(stream, MaxFirstLineBytes, stopAtNewline: true, opening.Token).ConfigureAwait(false);
            return new HeldReply(status, first, Drain(stream, hold.Token), http, response, hold);
        }
        catch
        {
            response?.Dispose();
            http.Dispose();
            hold.Dispose();
            throw;
        }
    }

    /// <summary>Reads to the first newline (not included) or, without <paramref name="stopAtNewline"/>, to the end of the body, at most <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]> ReadUpToAsync(Stream stream, int limit, bool stopAtNewline, CancellationToken cancellationToken)
    {
        using var collected = new MemoryStream();
        var one = new byte[1];
        while (collected.Length < limit)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
            {
                if (stopAtNewline)
                {
                    throw new IOException("the daemon closed the reply before its first line");
                }

                break;
            }

            if (stopAtNewline && one[0] == (byte)'\n')
            {
                break;
            }

            collected.WriteByte(one[0]);
        }

        return collected.ToArray();
    }

    private static async Task Drain(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        try
        {
            while (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) > 0)
            {
                // the daemon sends nothing more; this only notices the end
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or HttpRequestException or ObjectDisposedException)
        {
            // the connection ended
        }
    }
}
