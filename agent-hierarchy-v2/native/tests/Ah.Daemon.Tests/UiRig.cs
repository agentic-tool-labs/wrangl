#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using System.Net.Sockets;
using System.Text;
using Ah.Abstractions;
using Ah.Platform;

namespace Ah.Daemon.Tests;

/// <summary>A clock a test moves by hand; read from server threads, so the instant is one atomic value.</summary>
internal sealed class FakeClock : IClock
{
    private long _ticks = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).UtcTicks;

    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public TimeSpan LocalOffset(DateTimeOffset instant) => TimeSpan.Zero;

    internal void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    /// <summary>Jumps the clock to <paramref name="instant"/>, forward or back, as a wall clock does when it is set.</summary>
    internal void Set(DateTimeOffset instant) => Interlocked.Exchange(ref _ticks, instant.UtcTicks);
}

/// <summary>A monotonic millisecond clock a test moves by hand; it only moves forward, and is read from server threads.</summary>
internal sealed class ManualMonotonic
{
    private long _ms = 1_000;

    internal long Read() => Interlocked.Read(ref _ms);

    internal void Advance(TimeSpan by) => Interlocked.Add(ref _ms, (long)by.TotalMilliseconds);
}

/// <summary>A bridge on a sandbox with a fake clock, a stand-in view handler and a page directory, for tests that drive the loopback listener.</summary>
internal sealed class UiRig : IAsyncDisposable
{
    internal const string PageMarker = "PLACEHOLDER-PAGE";
    internal const string ViewMarker = "VIEW-BYTES";

    private readonly List<string> _log = [];
    private readonly HttpClient _http = NewClient();

    private UiRig(Sandbox box, TimeSpan stopDeadline, UiAnswerSupport? answer = null, UiTeamCloseSupport? teamClose = null, UiRulesSupport? rules = null)
    {
        Box = box;
        RunDir = Path.Combine(box.AhHome, "run");
        StaticDir = Path.Combine(box.Root, "plugin", "web", "ui");
        Directory.CreateDirectory(StaticDir);
        File.WriteAllText(Path.Combine(StaticDir, "index.html"), $"<!doctype html><title>{PageMarker}</title>");
        File.WriteAllText(Path.Combine(StaticDir, "ui.js"), "export {};");
        File.WriteAllText(Path.Combine(box.Root, "plugin", "web", "secret.html"), "SECRET");
        File.WriteAllText(Path.Combine(box.Root, "plugin", "secret.html"), "SECRET");
        File.WriteAllText(Path.Combine(StaticDir, "data.json"), "{}");
        var io = PlatformModule.CreateIo(null);
        var gated = new GatedWriter(io.Writer, this);
        View = static (_, _) => Task.FromResult(JsonResponse(200, $"{{\"{ViewMarker}\":true}}"));
        Options = new UiBridgeOptions(
            Clock,
            io.Files,
            gated,
            RunDir,
            StaticDir,
            Line,
            (request, ct) => View(request, ct),
            4242,
            () => Disabled,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(6),
            TimeSpan.FromSeconds(10),
            default)
        {
            StopDeadline = stopDeadline,
            Answer = answer,
            TeamClose = teamClose,
            Rules = rules,
        };
        Bridge = new UiBridge(Options);
    }

    internal Sandbox Box { get; }

    internal string RunDir { get; }

    internal string StaticDir { get; }

    internal string InfoPath => Path.Combine(RunDir, "ui.json");

    internal FakeClock Clock { get; } = new();

    internal UiBridgeOptions Options { get; }

    internal UiBridge Bridge { get; }

    internal bool Disabled { get; set; }

    /// <summary>What the bridge's view route answers. A test swaps it to hold or ignore cancellation.</summary>
    internal Func<RouteRequest, CancellationToken, Task<RouteResponse>> View { get; set; }

    /// <summary>While set, removing <c>ui.json</c> waits for it: a test uses it to hold a stop open between its steps.</summary>
    internal TaskCompletionSource? UnlinkGate { get; set; }

    /// <summary>While set, writing <c>ui.json</c> fails with an <see cref="IOException"/>.</summary>
    internal bool FailWrites { get; set; }

    /// <summary>Completed by the rig at the end, to free any handler a test made ignore cancellation.</summary>
    internal TaskCompletionSource Free { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal string LogText
    {
        get
        {
            lock (_log)
            {
                return string.Join('\n', _log);
            }
        }
    }

    internal static UiRig Create(TimeSpan? stopDeadline = null, UiAnswerSupport? answer = null, UiTeamCloseSupport? teamClose = null, UiRulesSupport? rules = null) => new(Sandbox.Create(), stopDeadline ?? TimeSpan.FromSeconds(1), answer, teamClose, rules);

    internal static HttpClient NewClient() => new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Until.Deadline };

    internal static RouteResponse JsonResponse(int status, string body) => new(status, new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body));

    private void Line(string line)
    {
        lock (_log)
        {
            _log.Add(line);
        }
    }

    /// <summary>A view handler that waits as a long-poll does and answers only when its token is cancelled, as the real events handler does: no hold of its own can outlast a slow stop.</summary>
    internal static async Task<RouteResponse> HoldUntilCancelled(RouteRequest request, CancellationToken cancellation)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        }
        catch (OperationCanceledException)
        {
            // the real handler answers its page at once
        }

        return JsonResponse(200, "{\"unchanged\":true}");
    }

    /// <summary>A view handler that never looks at its token: it answers when the rig is disposed.</summary>
    internal Func<RouteRequest, CancellationToken, Task<RouteResponse>> IgnoreCancellation => async (_, _) =>
    {
        await Free.Task;
        return JsonResponse(200, "{\"unchanged\":true}");
    };

    internal async Task<UiGrant> LeaseAsync(int pid = 31337) => await Bridge.OpenLeaseAsync(pid, default);

    internal static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Listens on <paramref name="port"/> so the bridge cannot take it. Address reuse lets it bind past a TIME_WAIT the bridge's own last connection left there; a live listener on the port still blocks the bridge.</summary>
    internal static TcpListener Hold(int port)
    {
        var holder = new TcpListener(IPAddress.Loopback, port);
        holder.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        holder.Start();
        return holder;
    }

    /// <summary>Holds a port the kernel picks and writes it as the bridge's remembered port, so the next lease must move off it. Re-holding a port the bridge just freed races every parallel test for it.</summary>
    internal TcpListener HoldRemembered(out int port)
    {
        var holder = Hold(0);
        port = ((IPEndPoint)holder.LocalEndpoint).Port;
        Remember(port);
        return holder;
    }

    /// <summary>Writes <paramref name="port"/> as the bridge's remembered port.</summary>
    internal void Remember(int port)
    {
        _ = Directory.CreateDirectory(RunDir);
        File.WriteAllText(Path.Combine(RunDir, "ui-port"), port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
    }

    internal async Task<HttpResponseMessage> SendAsync(int port, HttpMethod method, string path, Action<HttpRequestMessage>? tweak = null, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(method, $"http://127.0.0.1:{port}{path}");
        tweak?.Invoke(request);
        return await (client ?? _http).SendAsync(request, HttpCompletionOption.ResponseContentRead);
    }

    /// <summary>One request as exact text on a loopback socket, so a path reaches the server as written. The whole response.</summary>
    internal static async Task<string> RawAsync(int port, string requestText)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port));
        _ = await socket.SendAsync(Encoding.Latin1.GetBytes(requestText));
        return await Ah.Daemon.Tests.Raw.ReadAllAsync(socket, TimeSpan.FromSeconds(5));
    }

    internal static bool Listening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    internal static async Task<bool> Refuses(int port)
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cts.Token);
            return false;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _ = Free.TrySetResult();
        _ = UnlinkGate?.TrySetResult();
        await Bridge.DisposeAsync();
        _http.Dispose();
        Ah.TestProcess.SandboxDirectory.Remove(Box.Root);
    }
}

/// <summary>A writer that waits on the rig's gate before it removes a file.</summary>
internal sealed class GatedWriter(IRawFileWriter inner, UiRig rig) : IRawFileWriter
{
    public void WriteAtomic(string path, string tmpPath, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null)
    {
        if (rig.FailWrites)
        {
            throw new IOException("write refused by the test");
        }

        inner.WriteAtomic(path, tmpPath, bytes, mode);
    }

    public bool CreateExclusive(string path, ReadOnlySpan<byte> bytes, UnixFileMode? mode = null) => inner.CreateExclusive(path, bytes, mode);

    public void Rename(string fromPath, string toPath) => inner.Rename(fromPath, toPath);

    public bool Unlink(string path)
    {
        rig.UnlinkGate?.Task.GetAwaiter().GetResult();
        return inner.Unlink(path);
    }

    public void Mkdir(string path, UnixFileMode? mode = null) => inner.Mkdir(path, mode);
}
