using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Ah.TestKit;

/// <summary>
/// A stand-in for a daemon's socket that answers one request by hand: it reads the request head, then sends a held reply (a chunked
/// 200 with one first line that it never ends on its own) or a plain refusal, and can end or drop the connection when the test says.
/// </summary>
public sealed class FakeLeaseServer : IDisposable
{
    private readonly Socket _listener;
    private readonly string _directory;
    private Socket? _connection;

    private FakeLeaseServer(string directory)
    {
        _directory = directory;
        SocketPath = Path.Combine(directory, "s.sock");
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(4);
    }

    public string SocketPath { get; }

    /// <summary>The request line and headers the client sent, once <see cref="AcceptAsync"/> has completed.</summary>
    public string RequestHead { get; private set; } = string.Empty;

    /// <summary>Starts a server on a socket in a new <c>/tmp/ahprobe.*</c> directory.</summary>
    public static FakeLeaseServer Start()
    {
        var name = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
        var directory = $"/tmp/ahprobe.{name}";
        _ = Directory.CreateDirectory(directory);
        return new FakeLeaseServer(directory);
    }

    /// <summary>Waits for one connection and reads its request head.</summary>
    public async Task AcceptAsync()
    {
        _connection = await _listener.AcceptAsync().ConfigureAwait(false);
        var text = new StringBuilder();
        var buffer = new byte[2048];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!text.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
        {
            var n = await _connection.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            _ = text.Append(Encoding.Latin1.GetString(buffer, 0, n));
        }

        RequestHead = text.ToString();
    }

    /// <summary>Sends a 200 whose body begins with <paramref name="firstLine"/> and a newline, and leaves the reply open.</summary>
    public async Task HoldAsync(string firstLine)
    {
        var line = firstLine + "\n";
        var head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n";
        await SendRawAsync(head + Encoding.UTF8.GetByteCount(line).ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "\r\n" + line + "\r\n").ConfigureAwait(false);
    }

    /// <summary>Sends a complete reply with <paramref name="body"/> and the given status.</summary>
    public async Task RefuseAsync(int status, string body)
    {
        var bytes = Encoding.UTF8.GetByteCount(body);
        await SendRawAsync($"HTTP/1.1 {status.ToString(System.Globalization.CultureInfo.InvariantCulture)} X\r\nContent-Type: application/json\r\nContent-Length: {bytes.ToString(System.Globalization.CultureInfo.InvariantCulture)}\r\n\r\n{body}").ConfigureAwait(false);
    }

    /// <summary>Ends the held reply the way the daemon does: the chunked body's last chunk. The connection stays open.</summary>
    public Task EndReplyAsync() => SendRawAsync("0\r\n\r\n");

    public async Task SendRawAsync(string text) =>
        _ = await _connection!.SendAsync(Encoding.Latin1.GetBytes(text)).ConfigureAwait(false);

    /// <summary>Drops the connection without ending the reply.</summary>
    public void Drop() => _connection?.Dispose();

    /// <summary>Whether the client closes its end within <paramref name="within"/>.</summary>
    public async Task<bool> ClientClosedAsync(TimeSpan within)
    {
        var buffer = new byte[256];
        using var cts = new CancellationTokenSource(within);
        try
        {
            while (true)
            {
                if (await _connection!.ReceiveAsync(buffer, cts.Token).ConfigureAwait(false) == 0)
                {
                    return true;
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException)
        {
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _listener.Dispose();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // a leftover socket file in /tmp is harmless
        }
    }
}
