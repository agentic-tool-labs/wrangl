using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Ah.Core.Json;

namespace Ah.Codex;

/// <summary>An error from a Codex app-server or its socket; <c>Code</c> is the JSON-RPC code or the system error code, as the server or the socket gave it.</summary>
internal sealed class CodexClientException : Exception
{
    public CodexClientException(string message, JsonValue? code = null, JsonValue? data = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        ErrorData = data;
    }

    public JsonValue? Code { get; }

    public JsonValue? ErrorData { get; }
}

/// <summary>One server notification: the method and its <c>params</c> (null when it had none).</summary>
internal readonly record struct CodexNotification(string Method, JsonValue Params);

internal sealed record AppServerClientOptions
{
    public string ClientName { get; init; } = "ahd";

    public string ClientVersion { get; init; } = "0";

    /// <summary>How long the upgrade and each request may take before the client gives up and closes.</summary>
    public int TimeoutMs { get; init; } = 10_000;

    public int MaxFrameBytes { get; init; } = WsFraming.MaxFrameBytes;

    public int MaxHeaderBytes { get; init; } = WsFraming.MaxHeaderBytes;
}

/// <summary>A JSON-RPC client over one WebSocket on a Unix socket, as the adapter and the spawn use it.</summary>
internal interface IAppServerClient
{
    bool IsOpen { get; }

    /// <summary>Resolves with the result, or throws a <see cref="CodexClientException"/> carrying the server's code and data.</summary>
    Task<JsonValue> RequestAsync(string method, JsonValue? parameters);

    void Notify(string method, JsonValue? parameters = null);

    void OnNotification(Action<CodexNotification> handler);

    /// <summary>A request the server sent; the handlers hold it and reply with <see cref="Respond"/> or <see cref="RespondError"/>.</summary>
    void OnServerRequest(Action<JsonObject> handler);

    /// <summary>Replies to a server request with a result; <paramref name="id"/> is the request's own id, unchanged.</summary>
    void Respond(JsonValue id, JsonValue result);

    /// <summary>Replies to a server request with a JSON-RPC error.</summary>
    void RespondError(JsonValue id, int code, string message);

    /// <summary>Fires once when the connection ends.</summary>
    void OnClose(Action handler);

    void Close();
}

/// <summary>
/// The Codex app-server client: a hand-written WebSocket upgrade (the request is the bytes Node's client sends), masked client
/// frames, ping answered with pong, close honoured, a declared length over the cap closing the connection when the header is
/// read, response headers capped, and a deadline on the upgrade and on every request.
/// </summary>
internal sealed class AppServerClient : IAppServerClient
{
    private readonly string _path;
    private readonly AppServerClientOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<long, Pending> _pending = [];
    private readonly List<Action<CodexNotification>> _notes = [];
    private readonly List<Action<JsonObject>> _serverRequests = [];
    private readonly List<Action> _closers = [];
    private readonly object _sendLock = new();
    private Socket? _socket;
    private long _nextId;
    private bool _closed;
    private bool _isOpen;

    public AppServerClient(string path, AppServerClientOptions? options = null)
    {
        _path = path;
        _options = options ?? new AppServerClientOptions();
    }

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _isOpen;
            }
        }
    }

    public void OnNotification(Action<CodexNotification> handler)
    {
        lock (_gate)
        {
            _notes.Add(handler);
        }
    }

    public void OnServerRequest(Action<JsonObject> handler)
    {
        lock (_gate)
        {
            _serverRequests.Add(handler);
        }
    }

    public void OnClose(Action handler)
    {
        lock (_gate)
        {
            _closers.Add(handler);
        }
    }

    private sealed class Pending(TaskCompletionSource<JsonValue> source, Timer timer)
    {
        public TaskCompletionSource<JsonValue> Source { get; } = source;

        public Timer Timer { get; } = timer;
    }

    private void Fail(Exception error)
    {
        List<Pending> rejected;
        Action[] closers;
        lock (_gate)
        {
            rejected = [.. _pending.Values];
            _pending.Clear();
            if (_closed)
            {
                closers = [];
            }
            else
            {
                _closed = true;
                _isOpen = false;
                closers = [.. _closers];
            }
        }

        foreach (var p in rejected)
        {
            p.Timer.Dispose();
            p.Source.TrySetException(error);
        }

        foreach (var close in closers)
        {
            Guard(close);
        }
    }

    private static void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // a handler's failure must not end the read loop
        }
    }

    public void Close()
    {
        var socket = _socket;
        if (socket is not null)
        {
            Destroy(socket);
        }

        Fail(new CodexClientException("closed"));
    }

    private static void Destroy(Socket socket)
    {
        try
        {
            socket.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // already gone
        }
    }

    private void Send(JsonObject message)
    {
        var frame = WsFraming.Encode(JsonText.Stringify(message, 0), mask: true);
        var socket = _socket ?? throw new CodexClientException("not connected");
        lock (_sendLock)
        {
            var sent = 0;
            while (sent < frame.Length)
            {
                sent += socket.Send(frame.AsSpan(sent));
            }
        }
    }

    public void Respond(JsonValue id, JsonValue result) => Send(new JsonObject { ["id"] = id, ["result"] = result });

    public void RespondError(JsonValue id, int code, string message) =>
        Send(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    public Task<JsonValue> RequestAsync(string method, JsonValue? parameters)
    {
        bool open;
        lock (_gate)
        {
            open = _isOpen;
        }

        if (!open && method != "initialize")
        {
            return Task.FromException<JsonValue>(new CodexClientException("not connected"));
        }

        var id = Interlocked.Increment(ref _nextId);
        var source = new TaskCompletionSource<JsonValue>(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = new Timer(
            _ =>
            {
                Pending? removed;
                lock (_gate)
                {
                    _ = _pending.Remove(id, out removed);
                }

                if (removed is null)
                {
                    return;
                }

                removed.Timer.Dispose();
                removed.Source.TrySetException(new CodexClientException($"{method} timed out after {_options.TimeoutMs} ms"));
                Close();
            },
            null,
            _options.TimeoutMs,
            Timeout.Infinite);
        lock (_gate)
        {
            _pending[id] = new Pending(source, timer);
        }

        try
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
            };
            if (parameters is { } p)
            {
                message["params"] = p;
            }

            Send(message);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or CodexClientException)
        {
            lock (_gate)
            {
                _ = _pending.Remove(id);
            }

            timer.Dispose();
            source.TrySetException(e switch
            {
                SocketException se => FromSocket(se),
                CodexClientException ce => ce,
                _ => new CodexClientException("connection closed"),
            });
        }

        return source.Task;
    }

    public void Notify(string method, JsonValue? parameters = null)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
        };
        if (parameters is { } p)
        {
            message["params"] = p;
        }

        Send(message);
    }

    private void OnMessage(string text)
    {
        JsonValue parsed;
        try
        {
            parsed = JsonText.Parse(text);
        }
        catch (JsonParseException)
        {
            return;
        }

        if (!parsed.IsMap)
        {
            return;
        }

        var m = parsed.AsObject();
        var id = m.Get("id");
        var result = m.Get("result");
        var error = m.Get("error");
        var method = m.Get("method");
        if (id is { IsNumber: true } numeric && (result is not null || error is not null) && numeric.AsNumber() == Math.Floor(numeric.AsNumber()))
        {
            Pending? entry;
            lock (_gate)
            {
                _ = _pending.Remove((long)numeric.AsNumber(), out entry);
            }

            if (entry is not null)
            {
                entry.Timer.Dispose();
                if (error is { IsTruthy: true } e)
                {
                    var message = "rpc error";
                    JsonValue? code = null;
                    JsonValue? data = null;
                    if (e.IsMap)
                    {
                        if (e.AsObject().Get("message") is { IsTruthy: true } detail && detail.IsText)
                        {
                            message = detail.AsString();
                        }

                        code = e.AsObject().Get("code");
                        data = e.AsObject().Get("data");
                    }

                    entry.Source.TrySetException(new CodexClientException(message, code, data));
                }
                else
                {
                    entry.Source.TrySetResult(result ?? JsonValue.Null);
                }

                return;
            }
        }

        if (method is { IsTruthy: true } && id is not null)
        {
            Action<JsonObject>[] handlers;
            lock (_gate)
            {
                handlers = [.. _serverRequests];
            }

            foreach (var h in handlers)
            {
                Guard(() => h(m));
            }
        }
        else if (method is { IsTruthy: true } methodName)
        {
            Action<CodexNotification>[] handlers;
            lock (_gate)
            {
                handlers = [.. _notes];
            }

            var note = new CodexNotification(methodName.IsText ? methodName.AsString() : string.Empty, m.Get("params") ?? JsonValue.Null);
            foreach (var h in handlers)
            {
                Guard(() => h(note));
            }
        }
    }

    private static string? ErrnoLabel(SocketException e) => e.SocketErrorCode switch
    {
        SocketError.ConnectionRefused => "ECONNREFUSED",
        SocketError.ConnectionReset => "ECONNRESET",
        SocketError.TimedOut => "ETIMEDOUT",
        SocketError.AccessDenied => "EACCES",
        SocketError.NotSocket => "ENOTSOCK",
        SocketError.Shutdown => "EPIPE",
        SocketError.AddressNotAvailable => "ENOENT",
        _ => null,
    };

    private static CodexClientException FromSocket(SocketException e)
    {
        var label = ErrnoLabel(e);
        return new CodexClientException(e.Message, label is null ? (JsonValue?)null : JsonValue.FromString(label), null, e);
    }

    /// <summary>Connects, upgrades, and runs the <c>initialize</c> handshake; resolves with this client once the server answered it.</summary>
    public async Task<IAppServerClient> OpenAsync()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket = socket;
        using var upgradeDeadline = new CancellationTokenSource(_options.TimeoutMs);
        byte[] leftover;
        try
        {
            leftover = await UpgradeAsync(socket, key, upgradeDeadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Destroy(socket);
            var timeout = new CodexClientException($"websocket upgrade timed out after {_options.TimeoutMs} ms");
            Fail(timeout);
            throw timeout;
        }
        catch (SocketException e)
        {
            Destroy(socket);
            var failure = FromSocket(e);
            Fail(failure);
            throw failure;
        }
        catch (CodexClientException e)
        {
            Destroy(socket);
            Fail(new CodexClientException("connection closed"));
            throw new CodexClientException(e.Message);
        }

        lock (_gate)
        {
            _isOpen = true;
        }

        var reader = new WsFrameReader(_options.MaxFrameBytes);
        var initialize = RequestAsync("initialize", Params("clientInfo", ClientInfo()));
        _ = Task.Run(() => ReadLoopAsync(socket, reader, leftover));
        try
        {
            _ = await initialize.ConfigureAwait(false);
        }
        catch (CodexClientException)
        {
            Close();
            throw;
        }

        Notify("initialized");
        lock (_gate)
        {
            _isOpen = true;
        }

        return this;
    }

    private JsonObject ClientInfo() => new() { ["name"] = _options.ClientName, ["version"] = _options.ClientVersion };

    private static JsonObject Params(string name, JsonValue value) => new() { [name] = value };

    private async Task<byte[]> UpgradeAsync(Socket socket, string key, CancellationToken deadline)
    {
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(_path), deadline).ConfigureAwait(false);
        var request = $"GET / HTTP/1.1\r\nHost: localhost\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(request);
        var sent = 0;
        while (sent < bytes.Length)
        {
            sent += await socket.SendAsync(bytes.AsMemory(sent), SocketFlags.None, deadline).ConfigureAwait(false);
        }

        var head = new List<byte>();
        var buffer = new byte[4096];
        while (true)
        {
            var n = await socket.ReceiveAsync(buffer, SocketFlags.None, deadline).ConfigureAwait(false);
            if (n == 0)
            {
                throw new CodexClientException("connection closed");
            }

            head.AddRange(buffer.AsSpan(0, n).ToArray());
            var end = IndexOfHeadEnd(head);
            if (end < 0)
            {
                if (head.Count > _options.MaxHeaderBytes)
                {
                    throw new CodexClientException($"websocket handshake head larger than {_options.MaxHeaderBytes} bytes");
                }

                continue;
            }

            var text = Encoding.UTF8.GetString([.. head.Take(end)]);
            var accept = $"sec-websocket-accept: {WsFraming.AcceptKey(key).ToLowerInvariant()}";
            if (!text.StartsWith("HTTP/1.1 101", StringComparison.Ordinal) || !text.ToLowerInvariant().Contains(accept, StringComparison.Ordinal))
            {
                throw new CodexClientException("websocket upgrade refused");
            }

            return [.. head.Skip(end + 4)];
        }
    }

    private static int IndexOfHeadEnd(List<byte> head)
    {
        for (var i = 0; i + 3 < head.Count; i++)
        {
            if (head[i] == 13 && head[i + 1] == 10 && head[i + 2] == 13 && head[i + 3] == 10)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task ReadLoopAsync(Socket socket, WsFrameReader reader, byte[] first)
    {
        var buffer = new byte[16 * 1024];
        var chunk = first;
        try
        {
            while (true)
            {
                if (chunk.Length > 0)
                {
                    foreach (var f in reader.Push(chunk))
                    {
                        switch (f.Opcode)
                        {
                            case WsFraming.OpText:
                                OnMessage(Encoding.UTF8.GetString(f.Data));
                                break;
                            case WsFraming.OpPing:
                                Guard(() => SendRaw(socket, WsFraming.Encode(f.Data, WsFraming.OpPong, mask: true)));
                                break;
                            case WsFraming.OpClose:
                                Destroy(socket);
                                break;
                        }
                    }
                }

                var n = await socket.ReceiveAsync(buffer, SocketFlags.None).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                chunk = buffer.AsSpan(0, n).ToArray();
            }

            Fail(new CodexClientException("connection closed"));
        }
        catch (WsFrameTooLargeException e)
        {
            Destroy(socket);
            Fail(new CodexClientException(e.Message));
        }
        catch (SocketException e)
        {
            Fail(FromSocket(e));
        }
        catch (ObjectDisposedException)
        {
            Fail(new CodexClientException("connection closed"));
        }
    }

    private void SendRaw(Socket socket, byte[] frame)
    {
        lock (_sendLock)
        {
            var sent = 0;
            while (sent < frame.Length)
            {
                sent += socket.Send(frame.AsSpan(sent));
            }
        }
    }
}
