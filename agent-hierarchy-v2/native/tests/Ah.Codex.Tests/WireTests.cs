using System.Diagnostics;
using System.Text;
using Ah.Core.Json;

namespace Ah.Codex.Tests;

/// <summary>The client against the fake app-server and against a raw listener: what it sends, and how it ends a connection.</summary>
public sealed class WireTests
{
    private static readonly CodexModes Modes = new("workspace-write", "on-request");

    private static string[] ServerArgv(string socket, string tmp) => [.. CodexModule.ServerArgs(socket, null, tmp)];

    [Fact]
    public async Task TheUpgradeRequestIsByteForByteTheOneNodesClientSends()
    {
        using var sandbox = new TestSandbox();
        var path = sandbox.Path("raw.sock");
        using var server = new RawUnixServer(path);
        var client = new AppServerClient(path, new AppServerClientOptions { TimeoutMs = 30_000 });
        var opening = client.OpenAsync();

        using var connection = await server.AcceptAsync();
        var head = await RawUnixServer.ReadHeadAsync(connection);
        connection.Dispose();
        await Assert.ThrowsAsync<CodexClientException>(() => opening);

        var key = System.Text.RegularExpressions.Regex.Match(head, @"Sec-WebSocket-Key: (\S+)").Groups[1].Value;
        Assert.Equal(24, key.Length);
        Assert.Equal(16, Convert.FromBase64String(key).Length);
        var golden = Wire.Golden()["handshake"].AsString();
        Assert.Equal(golden, head.Replace(key, "<KEY>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRequestsOfOneScenarioAreTheLinesNodeLogsAtTheSameServer()
    {
        using var sandbox = new TestSandbox();
        var tmp = Path.Combine(sandbox.Root, "run", "codex", "tmp", "team-member");
        var socketPath = Path.Combine(sandbox.Root, "fake.sock");
        using var fake = FakeCodexServer.Start(sandbox, "{\"turnMs\":400}", ServerArgv(socketPath, tmp));
        var client = new AppServerClient(fake.Socket);
        await client.OpenAsync();

        var up = await CodexProtocol.BringUpThreadAsync(client, "/tmp/x", Modes, warmupMs: 60_000);
        Assert.True(up.Ok);

        var acked = new List<string>();
        var activity = new List<string>();
        var session = new CodexMemberSession(
            client,
            up.ThreadId!,
            ids => acked.Add(string.Join(",", ids)),
            (state, by) => activity.Add($"{state}:{by}"),
            events: new CodexRequestEvents("wire.member"));
        await session.HandOverAsync(["e1"], "first mail");
        await session.HandOverAsync(["e2"], "second mail");
        _ = await client.RequestAsync("fake/approval", new JsonObject { ["threadId"] = up.ThreadId! });
        var golden = Wire.Golden();
        var scenario = golden["scenario"].AsObject();
        var wantLines = golden["transcript"].AsArray().Count;
        var wantAcked = scenario["acked"].AsArray().Count;
        var wantActivity = scenario["activity"].AsArray().Count;
        var settled = Stopwatch.StartNew();
        while ((fake.LogLines().Length < wantLines || acked.Count < wantAcked || activity.Count < wantActivity) && settled.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        client.Close();

        Assert.Equal(golden["transcript"].AsArray().Select(l => l.AsString()).ToArray(), fake.LogLines());
        Assert.Equal(scenario["bringUp"].AsObject()["threadId"].AsString(), up.ThreadId);
        Assert.Equal(scenario["acked"].AsArray().Select(a => string.Join(",", a.AsArray().Select(i => i.AsString()))).ToArray(), acked.ToArray());
        Assert.Equal(
            scenario["activity"].AsArray().Select(a => $"{a.AsArray()[0].AsString()}:{(a.AsArray()[1].IsNull ? null : a.AsArray()[1].AsString())}").ToArray(),
            activity.ToArray());
    }

    [Fact]
    public async Task AFourGibibyteDeclaredLengthClosesTheConnectionAtOnce()
    {
        using var sandbox = new TestSandbox();
        using var fake = FakeCodexServer.Start(sandbox, "{\"hugeLength\":true}");
        var client = new AppServerClient(fake.Socket, new AppServerClientOptions { TimeoutMs = 30_000 });
        var clock = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<CodexClientException>(() => client.OpenAsync());

        Assert.True(clock.ElapsedMilliseconds < 20_000, $"took {clock.ElapsedMilliseconds} ms");
        Assert.Equal($"websocket message larger than {WsFraming.MaxFrameBytes} bytes", error.Message);
        Assert.False(client.IsOpen);
    }

    [Fact]
    public async Task AnEndlessResponseHeadIsRefusedAtSixteenKibibytes()
    {
        using var sandbox = new TestSandbox();
        using var fake = FakeCodexServer.Start(sandbox, "{\"hugeHeader\":true}");
        var client = new AppServerClient(fake.Socket);

        var error = await Assert.ThrowsAsync<CodexClientException>(() => client.OpenAsync());

        Assert.Equal("websocket handshake head larger than 16384 bytes", error.Message);
        Assert.Equal("handshake", CodexProtocol.ErrorClass(error));
    }

    [Fact]
    public async Task AnUpgradeThatIsNeverAnsweredTimesOut()
    {
        using var sandbox = new TestSandbox();
        using var fake = FakeCodexServer.Start(sandbox, "{\"stallUpgrade\":true}");
        var client = new AppServerClient(fake.Socket, new AppServerClientOptions { TimeoutMs = 300 });

        var error = await Assert.ThrowsAsync<CodexClientException>(() => client.OpenAsync());

        Assert.Equal("websocket upgrade timed out after 300 ms", error.Message);
        Assert.Equal("timeout", CodexProtocol.ErrorClass(error));
    }

    [Fact]
    public async Task AStalledRequestTimesOutAndClosesTheConnection()
    {
        using var sandbox = new TestSandbox();
        using var fake = FakeCodexServer.Start(sandbox, "{\"stall\":\"turn/start\"}");
        var client = new AppServerClient(fake.Socket, new AppServerClientOptions { TimeoutMs = 1500 });
        var closed = new TaskCompletionSource();
        client.OnClose(() => closed.TrySetResult());
        await client.OpenAsync();

        var error = await Assert.ThrowsAsync<CodexClientException>(() => client.RequestAsync("turn/start", new JsonObject { ["threadId"] = "t" }));

        Assert.Equal("turn/start timed out after 1500 ms", error.Message);
        Assert.Equal("timeout", CodexProtocol.ErrorClass(error));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(client.IsOpen);
        var after = await Assert.ThrowsAsync<CodexClientException>(() => client.RequestAsync("thread/list", new JsonObject()));
        Assert.Equal("not connected", after.Message);
        Assert.Equal("closed", CodexProtocol.ErrorClass(after));
    }

    [Fact]
    public async Task AnErrorAnswerCarriesTheServersCodeAndIsLabelledWithoutItsWords()
    {
        using var sandbox = new TestSandbox();
        using var fake = FakeCodexServer.Start(sandbox, "{\"initializeError\":true,\"sentinel\":\"SECRET-WORDS\"}");
        var client = new AppServerClient(fake.Socket);

        var error = await Assert.ThrowsAsync<CodexClientException>(() => client.OpenAsync());

        Assert.Contains("SECRET-WORDS", error.Message, StringComparison.Ordinal);
        Assert.Equal(-32600, error.Code!.Value.AsNumber());
        Assert.Equal("rpc-error -32600", CodexProtocol.ErrorClass(error));
    }

    [Fact]
    public async Task ASocketThatIsNotThereIsLabelledENOENT()
    {
        using var sandbox = new TestSandbox();
        var client = new AppServerClient(sandbox.Path("missing.sock"));

        var error = await Assert.ThrowsAsync<CodexClientException>(() => client.OpenAsync());

        Assert.Equal("ENOENT", CodexProtocol.ErrorClass(error));
    }

    [Fact]
    public async Task ClientFramesAreMaskedAPingIsAnsweredAndACloseEndsTheConnection()
    {
        using var sandbox = new TestSandbox();
        var path = sandbox.Path("raw.sock");
        using var server = new RawUnixServer(path);
        var client = new AppServerClient(path);
        var closed = new TaskCompletionSource();
        client.OnClose(() => closed.TrySetResult());
        var opening = client.OpenAsync();

        using var connection = await server.AcceptAsync();
        var head = await RawUnixServer.ReadHeadAsync(connection);
        await RawUnixServer.AcceptUpgradeAsync(connection, head);

        var (first, second, payload) = await RawUnixServer.ReadFrameAsync(connection);
        Assert.Equal(0x81, first);
        Assert.NotEqual(0, second & 0x80);
        var request = JsonText.Parse(payload).AsObject();
        Assert.Equal("initialize", request["method"].AsString());
        var reply = Encoding.UTF8.GetBytes($"{{\"jsonrpc\":\"2.0\",\"id\":{request["id"].AsNumber()},\"result\":{{}}}}");
        _ = await connection.SendAsync(WsFraming.Encode(reply), System.Net.Sockets.SocketFlags.None);
        await opening;

        _ = await RawUnixServer.ReadFrameAsync(connection);
        _ = await connection.SendAsync(WsFraming.Encode(Encoding.UTF8.GetBytes("abc"), WsFraming.OpPing), System.Net.Sockets.SocketFlags.None);
        var (pongFirst, pongSecond, pongPayload) = await RawUnixServer.ReadFrameAsync(connection);
        Assert.Equal(0x80 | WsFraming.OpPong, pongFirst);
        Assert.NotEqual(0, pongSecond & 0x80);
        Assert.Equal("abc", Encoding.UTF8.GetString(pongPayload));

        _ = await connection.SendAsync(WsFraming.Encode([], WsFraming.OpClose), System.Net.Sockets.SocketFlags.None);
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.False(client.IsOpen);
    }

    [Fact]
    public async Task ARefusedUpgradeIsRefused()
    {
        using var sandbox = new TestSandbox();
        var path = sandbox.Path("raw.sock");
        using var server = new RawUnixServer(path);
        var client = new AppServerClient(path);
        var opening = client.OpenAsync();

        using var connection = await server.AcceptAsync();
        _ = await RawUnixServer.ReadHeadAsync(connection);
        _ = await connection.SendAsync(Encoding.UTF8.GetBytes("HTTP/1.1 101 Switching Protocols\r\nSec-WebSocket-Accept: wrong\r\n\r\n"), System.Net.Sockets.SocketFlags.None);

        var error = await Assert.ThrowsAsync<CodexClientException>(() => opening);
        Assert.Equal("websocket upgrade refused", error.Message);
    }

    [Fact]
    public async Task ThePinsGateRefusesAServerThatDidNotApplyThem()
    {
        using var sandbox = new TestSandbox();
        var socketPath = Path.Combine(sandbox.Root, "fake.sock");
        var tmp = Path.Combine(sandbox.Root, "run", "codex", "tmp", "team-member");
        using var fake = FakeCodexServer.Start(sandbox, "{\"networkAccess\":true}", ServerArgv(socketPath, tmp));
        var client = new AppServerClient(fake.Socket);
        await client.OpenAsync();

        var result = await CodexProtocol.BringUpThreadAsync(client, "/tmp/x", Modes, warmupMs: 2000);

        Assert.False(result.Ok);
        Assert.Equal("gate", result.Step);
        Assert.Equal(CodexProtocol.BringUpNotes.Pins, result.Note);
        client.Close();
    }

    [Fact]
    public async Task ABringUpFailureCarriesOnlyAFixedNote()
    {
        using var sandbox = new TestSandbox();
        using var fake = FakeCodexServer.Start(sandbox, "{\"threadStartError\":true,\"sentinel\":\"SECRET-WORDS\"}");
        var client = new AppServerClient(fake.Socket);
        await client.OpenAsync();

        var result = await CodexProtocol.BringUpThreadAsync(client, "/tmp/x", Modes, warmupMs: 2000);

        Assert.Equal(new BringUpResult(false, null, "start", CodexProtocol.BringUpNotes.Start), result);
        client.Close();
    }

    [Fact]
    public async Task AWarmUpThatNeverCompletesIsATimeoutAndAMissingRolloutIsRefused()
    {
        using var sandbox = new TestSandbox();
        var socketPath = Path.Combine(sandbox.Root, "fake.sock");
        var tmp = Path.Combine(sandbox.Root, "run", "codex", "tmp", "team-member");
        using (var never = FakeCodexServer.Start(sandbox, "{\"neverComplete\":true}", ServerArgv(socketPath, tmp)))
        {
            var client = new AppServerClient(never.Socket);
            await client.OpenAsync();
            var result = await CodexProtocol.BringUpThreadAsync(client, "/tmp/x", Modes, warmupMs: 400);
            Assert.Equal("warmup", result.Step);
            Assert.Equal(CodexProtocol.WarmupNote("timeout"), result.Note);
            client.Close();
        }

        using var norollout = FakeCodexServer.Start(sandbox, "{\"noRollout\":true}", ServerArgv(socketPath, tmp));
        var second = new AppServerClient(norollout.Socket);
        await second.OpenAsync();
        var refused = await CodexProtocol.BringUpThreadAsync(second, "/tmp/x", Modes, warmupMs: 60_000);
        Assert.Equal("rollout", refused.Step);
        Assert.Equal(CodexProtocol.BringUpNotes.Rollout, refused.Note);
        second.Close();
    }
}
