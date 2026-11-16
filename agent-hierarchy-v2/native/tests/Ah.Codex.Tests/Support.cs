using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Ah.Core.Json;

namespace Ah.Codex.Tests;

/// <summary>A <c>/tmp/ahprobe.XXXXXX</c> directory (short, so a socket path in it stays under the platform limit). It removes only the files it was told about, then the directory itself if it is empty.</summary>
internal sealed partial class TestSandbox : IDisposable
{
    private static readonly System.Text.RegularExpressions.Regex Shape = ShapePattern();
    private readonly List<string> _files = [];

    public TestSandbox()
    {
        var suffix = Convert.ToHexString(RandomNumberGenerator.GetBytes(3));
        Root = $"/tmp/ahprobe.{suffix}";
#pragma warning disable CA1416 // these tests run on macOS and Linux only
        Directory.CreateDirectory(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        if (!Shape.IsMatch(Root))
        {
            throw new InvalidOperationException($"unexpected sandbox {Root}");
        }
    }

    public string Root { get; }

    public string Path(string name)
    {
        var path = System.IO.Path.Combine(Root, name);
        _files.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var file in _files)
        {
            if (file.StartsWith(Root + "/", StringComparison.Ordinal) && File.Exists(file))
            {
                File.Delete(file);
            }
        }

        if (Shape.IsMatch(Root) && Directory.Exists(Root) && Directory.GetFileSystemEntries(Root).Length == 0)
        {
            Directory.Delete(Root, recursive: false);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^/tmp/ahprobe\.[0-9A-F]{6}$")]
    private static partial System.Text.RegularExpressions.Regex ShapePattern();
}

/// <summary>The stand-in Codex app-server under tests/ (fake-codex-appserver.mjs), run as a child process on a socket in the sandbox.</summary>
internal sealed class FakeCodexServer : IDisposable
{
    private readonly Process _process;

    private FakeCodexServer(Process process, string socket, string log)
    {
        _process = process;
        Socket = socket;
        LogPath = log;
    }

    public string Socket { get; }

    public string LogPath { get; }

    public static string FakeScript([CallerFilePath] string here = "") =>
        System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(here)!, "..", "..", "..", "tests", "fake-codex-appserver.mjs"));

    public static FakeCodexServer Start(TestSandbox sandbox, string optionsJson = "{}", IEnumerable<string>? serverArgv = null)
    {
        var socket = sandbox.Path("fake.sock");
        var log = sandbox.Path("fake.log");
        var info = new ProcessStartInfo("node") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add(FakeScript());
        info.ArgumentList.Add(socket);
        info.ArgumentList.Add(log);
        info.ArgumentList.Add(optionsJson);
        foreach (var a in serverArgv ?? [])
        {
            info.ArgumentList.Add(a);
        }

        var process = Process.Start(info) ?? throw new InvalidOperationException("node did not start");
        var line = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
        if (line != "listening")
        {
            process.Kill();
            throw new InvalidOperationException($"the fake app-server said '{line}'");
        }

        return new FakeCodexServer(process, socket, log);
    }

    public string[] LogLines() =>
        File.Exists(LogPath) ? File.ReadAllLines(LogPath).Where(l => l.Length > 0).ToArray() : [];

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            _process.Kill();
            _process.WaitForExit(5000);
        }

        _process.Dispose();
    }
}

/// <summary>A listener on a Unix socket that hands the test the raw accepted connection, for framing and handshake checks.</summary>
internal sealed class RawUnixServer : IDisposable
{
    private readonly Socket _listener;

    public RawUnixServer(string path)
    {
        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(path));
        _listener.Listen(1);
    }

    public Task<Socket> AcceptAsync() => _listener.AcceptAsync();

    public void Dispose() => _listener.Dispose();

    /// <summary>Reads until the blank line that ends an HTTP head; returns the head text.</summary>
    public static async Task<string> ReadHeadAsync(Socket connection)
    {
        var all = new List<byte>();
        var buffer = new byte[1024];
        while (true)
        {
            var n = await connection.ReceiveAsync(buffer, SocketFlags.None);
            if (n == 0)
            {
                throw new InvalidOperationException("closed before the head ended");
            }

            all.AddRange(buffer.AsSpan(0, n).ToArray());
            var text = Encoding.UTF8.GetString([.. all]);
            var end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (end >= 0)
            {
                return text[..(end + 4)];
            }
        }
    }

    /// <summary>Answers the upgrade a client sent in <paramref name="head"/> correctly.</summary>
    public static async Task AcceptUpgradeAsync(Socket connection, string head)
    {
        var key = System.Text.RegularExpressions.Regex.Match(head, @"Sec-WebSocket-Key: (\S+)").Groups[1].Value;
        var accept = WsFraming.AcceptKey(key);
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        _ = await connection.SendAsync(Encoding.UTF8.GetBytes(response), SocketFlags.None);
    }

    /// <summary>Reads exactly one frame from the client and returns its first two header bytes and its unmasked payload.</summary>
    public static async Task<(byte First, byte Second, byte[] Payload)> ReadFrameAsync(Socket connection)
    {
        var header = await ReadExactAsync(connection, 2);
        var masked = (header[1] & 0x80) != 0;
        long len = header[1] & 0x7f;
        if (len == 126)
        {
            var ext = await ReadExactAsync(connection, 2);
            len = (ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            var ext = await ReadExactAsync(connection, 8);
            len = 0;
            foreach (var b in ext)
            {
                len = (len << 8) | b;
            }
        }

        var key = masked ? await ReadExactAsync(connection, 4) : [];
        var payload = await ReadExactAsync(connection, (int)len);
        if (masked)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= key[i % 4];
            }
        }

        return (header[0], header[1], payload);
    }

    private static async Task<byte[]> ReadExactAsync(Socket connection, int count)
    {
        var buffer = new byte[count];
        var got = 0;
        while (got < count)
        {
            var n = await connection.ReceiveAsync(buffer.AsMemory(got), SocketFlags.None);
            if (n == 0)
            {
                throw new InvalidOperationException("closed mid-frame");
            }

            got += n;
        }

        return buffer;
    }
}

internal static class Wire
{
    public static JsonObject Golden(string name = "wire-goldens.json") =>
        JsonText.Parse(File.ReadAllBytes(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name))).AsObject();
}
