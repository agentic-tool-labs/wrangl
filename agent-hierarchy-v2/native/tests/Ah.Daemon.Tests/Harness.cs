#pragma warning disable xUnit1051 // the harness owns its own deadlines

using System.Net.Sockets;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Platform;
using Ah.State;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("macos")]
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

namespace Ah.Daemon.Tests;

/// <summary>A short sandbox under /private/tmp/ahprobe.*; the socket path limit is 103 bytes. Removed when the test process exits.</summary>
internal sealed class Sandbox
{
    private static int s_counter;

    private Sandbox(string root)
    {
        Root = root;
        Ah.TestProcess.ProcessSandbox.RemoveAtExit(root);
        AhHome = Path.Combine(root, "ah");
        Directory.CreateDirectory(Path.Combine(AhHome, "run"));
        Directory.CreateDirectory(Path.Combine(AhHome, "logs"));
    }

    internal string Root { get; }

    internal string AhHome { get; }

    internal string Sock => Path.Combine(AhHome, "run", "ahd.sock");

    internal string Info => Path.Combine(AhHome, "run", "ahd.json");

    internal static Sandbox Create()
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";
        var rng = new Random(Environment.TickCount ^ Interlocked.Increment(ref s_counter) * 7919);
        var name = new string([.. Enumerable.Range(0, 6).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
        var root = $"/private/tmp/ahprobe.{name}";
        if (Directory.Exists(root))
        {
            return Create();
        }

        return new Sandbox(root);
    }

}

internal sealed class TestEnv(string ahHome, IReadOnlyDictionary<string, string?> vars, PluginInfo? plugin) : IAhEnvironment
{
    public string? GetVariable(string name) => vars.TryGetValue(name, out var v) ? v : null;

    public bool TestMode => GetVariable("AH_TEST") == "1";

    public string? GetTestOnly(string name) => TestMode ? GetVariable(name) : null;

    public string HomeDirectory => Path.GetDirectoryName(ahHome)!;

    public string AhHome => ahHome;

    public PluginInfo? Plugin => plugin;
}

/// <summary>An <see cref="IUnixHttpTransport"/> over HttpClient, one connection per request.</summary>
internal sealed class UnixTransport : IUnixHttpTransport
{
    public async Task<HttpReply> SendAsync(
        string socketPath, string method, string pathAndQuery, ReadOnlyMemory<byte> body, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                try
                {
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct).ConfigureAwait(false);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            },
        };
        using var http = new HttpClient(handler) { Timeout = timeout };
        using var request = new HttpRequestMessage(new HttpMethod(method), "http://ahd" + pathAndQuery);
        if (body.Length > 0)
        {
            request.Content = new ByteArrayContent(body.ToArray());
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return new HttpReply((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
    }
}

internal sealed record Reply(int Status, string Body, IReadOnlyDictionary<string, string> Headers)
{
    internal JsonObject Json => JsonText.Parse(Body).AsObject();
}

/// <summary>Raw requests over the unix socket, for exact headers, timing and slow clients.</summary>
internal static class Raw
{
    /// <summary>
    /// Connects, waiting up to <paramref name="wait"/> (2 s by default) for the socket path to appear: on macOS .NET reports a
    /// path that does not exist as AddressNotAvailable. Every other failure, a refused connection included, is final.
    /// </summary>
    internal static Socket Connect(string sock, TimeSpan? wait = null)
    {
        var end = Environment.TickCount64 + (long)(wait ?? TimeSpan.FromSeconds(2)).TotalMilliseconds;
        while (true)
        {
            var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                s.Connect(new UnixDomainSocketEndPoint(sock));
                return s;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressNotAvailable && Environment.TickCount64 < end)
            {
                s.Dispose();
                Thread.Sleep(10);
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }
    }

    internal static async Task<string> ReadAllAsync(Socket s, TimeSpan limit)
    {
        using var cts = new CancellationTokenSource(limit);
        var buf = new byte[8192];
        var sb = new StringBuilder();
        try
        {
            while (true)
            {
                var n = await s.ReceiveAsync(buf, cts.Token);
                if (n == 0)
                {
                    break;
                }

                _ = sb.Append(Encoding.Latin1.GetString(buf, 0, n));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException)
        {
            // what arrived
        }

        return sb.ToString();
    }

    /// <summary>One request with <c>Connection: close</c>; the whole response text.</summary>
    internal static async Task<string> ExchangeAsync(string sock, string requestText, TimeSpan? limit = null)
    {
        using var s = Connect(sock);
        _ = await s.SendAsync(Encoding.Latin1.GetBytes(requestText));
        return await ReadAllAsync(s, limit ?? TimeSpan.FromSeconds(5));
    }

    internal static Reply Parse(string text)
    {
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? string.Empty : text[(split + 4)..];
        var lines = head.Split("\r\n");
        var status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in lines.Skip(1))
        {
            var c = l.IndexOf(':', StringComparison.Ordinal);
            if (c > 0)
            {
                headers[l[..c]] = l[(c + 1)..].Trim();
            }
        }

        return new Reply(status, body, headers);
    }
}

internal sealed class HarnessOptions
{
    internal List<RouteRegistration> Routes { get; } = [];

    internal List<JobRegistration> Jobs { get; } = [];

    /// <summary>Registers collaborator-bound jobs the way a module's <c>Register</c> does.</summary>
    internal Action<IJobRegistry>? RegisterJobs { get; set; }

    /// <summary>Registers collaborator-bound routes the way a module's <c>Register</c> does.</summary>
    internal Action<IRouteRegistry>? RegisterRoutes { get; set; }

    internal Dictionary<string, string?> Vars { get; } = new() { ["AH_TEST"] = "1" };

    internal DaemonTimings Timings { get; set; } = DaemonTimings.Default;

    internal string? LoginJson { get; set; }

    /// <summary>The <c>--idle-exit-ms</c> option of <c>serve</c>, when the test names one.</summary>
    internal long? IdleExitMs { get; set; }

    /// <summary>The wall clock the daemon reads; null is the system clock.</summary>
    internal IClock? Clock { get; set; }

    /// <summary>
    /// The monotonic clock the daemon's idle exit and drain deadline measure by; null is the system's. A test that sets it owns both:
    /// the hard drain deadline never passes unless the test advances it, so a drain that waits on a stuck request must advance it too.
    /// </summary>
    internal ManualMonotonic? Monotonic { get; set; }

    internal string Version { get; set; } = "9.9.9";

    internal Func<bool>? Veto { get; set; }

    internal IStateService? State { get; set; }

    /// <summary>The seam that reads the connector's pid from an accepted socket; null reads none.</summary>
    internal IPeerIdentity? Peers { get; set; }

    internal IRejectionLog? Rejections { get; set; }

    /// <summary>Who each peer is for drain and the UI lease; by default every caller is a session, as a participant's client is.</summary>
    internal Func<int?, PrincipalKind> Principal { get; set; } = _ => PrincipalKind.Session;

    /// <summary>The parent of a process, for the service lease; null reads none.</summary>
    internal Func<int, int?>? ParentOf { get; set; }

    /// <summary>What `POST /v1/ui/start` starts the page with; null leaves the route unable to start it.</summary>
    internal UiStartSupport? UiStart { get; set; }

    /// <summary>The close core behind <c>POST /v1/team/close</c>; null answers 503.</summary>
    internal UiTeamCloseSupport? UiTeamClose { get; set; }

    /// <summary>The herdr-app-child proof <c>POST /v1/team/close</c> also accepts; null accepts none.</summary>
    internal Func<int, bool>? HerdrAppChild { get; set; }

    /// <summary>The pid the daemon takes as its own; null is a made-up one. A real holder needs the real one to be its parent.</summary>
    internal int? ProcessId { get; set; }

    /// <summary>Where the health body reads the fields other modules own.</summary>
    internal IHealthExtras? Health { get; set; }

    internal Action? OnStop { get; set; }

    internal Action? HardExit { get; set; }

    /// <summary>The plugin root the daemon reports (where the participation contract prose is read from).</summary>
    internal string PluginRoot { get; set; } = "/private/tmp/ahprobe.plugin";
}

/// <summary>A daemon running in this process on a sandbox socket, under its own fake pid.</summary>
internal sealed class Harness : IAsyncDisposable
{
    private static int s_pid = 7_000_000;

    private Harness(Sandbox box, DaemonServer server, int pid, StringWriter log)
    {
        Box = box;
        Server = server;
        Pid = pid;
        Log = log;
    }

    internal Sandbox Box { get; }

    internal DaemonServer Server { get; }

    internal int Pid { get; }

    internal StringWriter Log { get; }

    internal Task<int> Exit { get; private set; } = null!;

    internal bool Exited => Exit.IsCompleted;

    internal static async Task<Harness> StartAsync(Sandbox box, Action<HarnessOptions>? configure = null)
    {
        var harness = Launch(box, configure);
        var end = Environment.TickCount64 + (long)Until.Deadline.TotalMilliseconds;
        while (Environment.TickCount64 < end && !harness.Exited)
        {
            if (File.Exists(box.Info))
            {
                return harness;
            }

            await Task.Delay(10);
        }

        return harness;
    }

    /// <summary>Starts the daemon and returns at once, without waiting for it to listen.</summary>
    internal static Harness Launch(Sandbox box, Action<HarnessOptions>? configure = null)
    {
        var o = new HarnessOptions();
        configure?.Invoke(o);
        o.Vars["AH_HOME"] = box.AhHome;
        if (o.LoginJson is not null)
        {
            File.WriteAllText(Path.Combine(box.AhHome, "login.json"), o.LoginJson);
        }

        var io = PlatformModule.CreateIo(null);
        var env = new TestEnv(box.AhHome, o.Vars, new PluginInfo(o.PluginRoot, "wrangl", o.Version, "1"));
        var pid = o.ProcessId ?? Interlocked.Increment(ref s_pid);
        var deps = new DaemonDependencies(io.Files, io.Writer, o.Clock ?? io.Clock, env, null, pid, o.State, o.Peers, o.Rejections, o.Principal, o.Monotonic is { } monotonic ? monotonic.Read : null, o.ParentOf, o.UiStart)
        {
            HardExit = o.HardExit,
            ThreadCpuNs = Ah.Platform.PlatformModule.ThreadCpuNs,
            UiTeamClose = o.UiTeamClose,
            HerdrAppChild = o.HerdrAppChild,
        };
        var registry = new ModuleRegistry([], [], o.Routes, o.Jobs, []);
        var log = new StringWriter();
        var server = new DaemonServer(
            deps, new DaemonSettings(o.Timings, HandleSignals: false, o.IdleExitMs), [registry], log, JobRegistry.Collect([registry], o.RegisterJobs), RouteRegistry.Collect([registry], o.RegisterRoutes));
        server.HealthExtrasSource = o.Health is null ? null : o.Health.Read;
        server.OnStop = o.OnStop;
        var harness = new Harness(box, server, pid, log);
        harness.Exit = server.RunAsync();
        if (o.Veto is { } veto)
        {
            server.AddIdleVeto(veto);
        }

        return harness;
    }

    internal async Task<Reply> GetAsync(string path, TimeSpan? timeout = null) => await SendAsync("GET", path, null, timeout);

    internal async Task<Reply> PostAsync(string path, string json, TimeSpan? timeout = null) => await SendAsync("POST", path, json, timeout);

    internal async Task<Reply> SendAsync(string method, string path, string? json, TimeSpan? timeout = null)
    {
        var text = $"{method} {path} HTTP/1.1\r\nHost: ahd\r\nConnection: close\r\n"
            + (json is null ? string.Empty : $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(json)}\r\n")
            + "\r\n" + json;
        try
        {
            return Raw.Parse(await Raw.ExchangeAsync(Box.Sock, text, timeout ?? Until.Deadline));
        }
        catch (SocketException e)
        {
            var exit = !Exit.IsCompleted ? "running" : Exit.IsFaulted ? Exit.Exception!.GetBaseException().Message : Exit.Result.ToString(System.Globalization.CultureInfo.InvariantCulture);
            throw new InvalidOperationException(
                $"no connection to {Box.Sock}: {e.Message}; daemon {exit}; socket file {File.Exists(Box.Sock)}; ahd.json {File.Exists(Box.Info)}; log: {Log}", e);
        }
    }

    internal async Task<bool> ExitsWithinAsync(TimeSpan limit) => await Task.WhenAny(Exit, Task.Delay(limit)) == Exit;

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
    }
}

/// <summary>Routes and jobs for tests.</summary>
/// <summary>Waits for a condition on real time under a generous deadline, so a test counts events and never time.</summary>
internal static class Until
{
    /// <summary>
    /// The generous deadline as a span: the hang guard for one awaited event. A bridge start alone was measured at up to 4.7 s
    /// under load, so a guard of a few seconds fails a test that is only slow; a gate at load 4 ran this suite 8x slower and failed 30 s guards.
    /// </summary>
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(60);

    internal static async Task TrueAsync(Func<bool> condition, int deadlineMs = 60_000, Func<string>? because = null)
    {
        var end = Environment.TickCount64 + deadlineMs;
        while (!condition() && Environment.TickCount64 < end)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), because?.Invoke() ?? "the condition did not hold within its deadline");
    }
}

internal static class TestRoutes
{
    internal static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();

    internal static RouteResponse Json(int status, string body) =>
        new(status, NoHeaders, Encoding.UTF8.GetBytes(body));

    internal static RouteRegistration Echo(string pattern) => new(pattern, (r, _) =>
        Task.FromResult(Json(200, $"{{\"bytes\":{r.Body.Length}}}")));
}
