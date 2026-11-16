using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Codex.Tests;

/// <summary>An in-memory app-server client: requests are recorded and answered from a table, notifications are pushed by the test.</summary>
internal sealed class ScriptedClient : IAppServerClient
{
    private readonly List<Action<CodexNotification>> _notes = [];
    private readonly List<Action> _closers = [];

    public List<(string Method, JsonValue? Params)> Requests { get; } = [];

    public Func<string, JsonValue?, JsonValue> Answer { get; set; } = (_, _) => new JsonObject();

    public bool IsOpen { get; private set; } = true;

    public bool WasClosed { get; private set; }

    public Task<JsonValue> RequestAsync(string method, JsonValue? parameters)
    {
        lock (Requests)
        {
            Requests.Add((method, parameters));
        }

        return Task.FromResult(Answer(method, parameters));
    }

    public void Notify(string method, JsonValue? parameters = null)
    {
    }

    public void OnNotification(Action<CodexNotification> handler) => _notes.Add(handler);

    private readonly List<Action<JsonObject>> _serverRequests = [];

    public List<(JsonValue Id, JsonValue Result)> Results { get; } = [];

    public List<(JsonValue Id, int Code, string Message)> Errors { get; } = [];

    public void OnServerRequest(Action<JsonObject> handler) => _serverRequests.Add(handler);

    public void Respond(JsonValue id, JsonValue result) => Results.Add((id, result));

    public void RespondError(JsonValue id, int code, string message) => Errors.Add((id, code, message));

    public void ServerRequest(JsonValue id, string method, string paramsJson)
    {
        var request = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = JsonText.Parse(paramsJson) };
        foreach (var h in _serverRequests)
        {
            h(request);
        }
    }

    public void OnClose(Action handler) => _closers.Add(handler);

    public void Close()
    {
        WasClosed = true;
        IsOpen = false;
        foreach (var c in _closers)
        {
            c();
        }
    }

    public void Push(string method, JsonValue parameters)
    {
        foreach (var n in _notes)
        {
            n(new CodexNotification(method, parameters));
        }
    }
}

internal sealed class FakeDependencies : ICodexDependencies
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.FromUnixTimeSeconds(1_000_000);

    public string AhHome { get; set; } = "/tmp/ah-adapter-test";

    public List<CodexTeam> TeamList { get; } = [];

    public HashSet<int> LivePids { get; } = [];

    public HashSet<string> Sockets { get; } = [];

    public CodexModes Modes { get; set; } = new("workspace-write", "on-request");

    public List<CodexRegistration> Registered { get; } = [];

    public List<string> Unregistered { get; } = [];

    public List<(string Box, CodexHeartbeat Beat)> Heartbeats { get; } = [];

    public List<CodexPendingEntry> PendingEntries { get; } = [];

    public List<(string Box, string Ids, string Session)> Acks { get; } = [];

    public List<string> Activities { get; } = [];

    public List<string> Refusals { get; } = [];

    public List<string> Logs { get; } = [];

    public bool RefuseRegistration { get; set; }

    public Func<IReadOnlyList<CodexTeam>>? TeamsOverride { get; set; }

    public IReadOnlyList<CodexTeam> Teams() => TeamsOverride is { } f ? f() : TeamList;

    public bool PidAlive(int pid) => LivePids.Contains(pid);

    public bool SocketExists(string path) => Sockets.Contains(path);

    public string BoxFor(string address, CodexMember member) => $"/box/{address}";

    public CodexModes ModesFor(CodexMember member) => Modes;

    public void Register(CodexRegistration registration)
    {
        if (RefuseRegistration)
        {
            throw new InvalidOperationException("the mailbox said no");
        }

        Registered.Add(registration);
    }

    public void Unregister(string sessionId) => Unregistered.Add(sessionId);

    public void Heartbeat(string box, CodexHeartbeat heartbeat) => Heartbeats.Add((box, heartbeat));

    public IReadOnlyList<CodexPendingEntry> Pending(string box) => [.. PendingEntries];

    public string Render(string address, IReadOnlyList<CodexPendingEntry> entries) => $"mail for {address}: {string.Join(",", entries.Select(e => e.Id))}";

    public void Ack(string box, IReadOnlyList<string> ids, string sessionId) => Acks.Add((box, string.Join(",", ids), sessionId));

    public void Activity(CodexMember member, string state, string? blockedBy) => Activities.Add($"{member.Key}:{state}:{blockedBy}");

    public void Refused(CodexMember member, string note) => Refusals.Add($"{member.Key}:{note}");

    public List<string> Raised { get; } = [];

    public List<string> Dropped { get; } = [];

    public List<string> Unsupported { get; } = [];

    public List<string?> TurnEnds { get; } = [];

    public void RequestRaised(CodexMember member, CodexPending pending) =>
        Raised.Add($"{member.Key}|{pending.RequestId}|{pending.Kind}|{pending.Secret}|{pending.Blocking}|{pending.Verbatim}");

    public void RequestDropped(CodexMember member, CodexPending pending, string why) => Dropped.Add($"{member.Key}|{pending.RequestId}|{why}");

    public void RequestUnsupported(CodexMember member, string method) => Unsupported.Add($"{member.Key}|{method}");

    public List<string> PermissionNotes { get; } = [];

    public void PermissionsDeclined(CodexMember member, string details) => PermissionNotes.Add($"{member.Key}|{details}");

    public void TurnEnded(CodexMember member, string? lastMessage) => TurnEnds.Add(lastMessage);

    public void Log(string message)
    {
        lock (Logs)
        {
            Logs.Add(message);
        }
    }

    public string SocketOf(string team, string name) => CodexModule.AppserverSocket(AhHome, team, name);

    /// <summary>Adds a team with one codex member whose server, socket and recorded thread all exist.</summary>
    public CodexMember AddMember(string team = "tm", string name = "alpha", int pid = 4242, string thread = "th-1", string? extraRowJson = null)
    {
        var row = JsonText.Parse(
            $"{{\"name\":\"{name}\",\"kind\":\"codex\",\"adapter\":\"appserver\",\"appserver_pid\":{pid},\"appserver_thread_id\":\"{thread}\"{extraRowJson}}}");
        TeamList.Add(new CodexTeam($"/h/teams/{team}.json", team, JsonText.Parse($"{{\"members\":[{JsonText.Stringify(row, 0)}]}}")));
        _ = LivePids.Add(pid);
        _ = Sockets.Add(SocketOf(team, name));
        return new CodexMember($"{team}.{name}", team, name, $"{name}@{team}", $"/h/teams/{team}.json", pid, SocketOf(team, name), thread, row);
    }
}

public sealed class AdapterTests
{
    private static readonly string[] ServedKeys = ["tm.off", "tm.ok"];

    private static JsonValue GoodReport() => JsonText.Parse(
        "{\"thread\":{\"id\":\"th-1\"},\"sandbox\":{\"type\":\"workspaceWrite\",\"networkAccess\":false,\"excludeSlashTmp\":true,\"excludeTmpdirEnvVar\":true},\"approvalPolicy\":\"on-request\"}");

    private static CodexAdapter Adapter(FakeDependencies deps, ScriptedClient client, Func<IAppServerClient, CodexMember, Task<JsonValue?>>? resume = null) =>
        new(deps, _ => Task.FromResult<IAppServerClient>(client), resume ?? ((_, _) => Task.FromResult<JsonValue?>(GoodReport())));

    [Fact]
    public void AMemberIsServedOnlyWhenEveryConditionHolds()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember(name: "ok");
        _ = deps.AddMember(name: "dead", pid: 11);
        _ = deps.LivePids.Remove(11);
        _ = deps.AddMember(name: "nothread", thread: string.Empty);
        _ = deps.AddMember(name: "nosock", pid: 12);
        _ = deps.Sockets.Remove(deps.SocketOf("tm", "nosock"));
        _ = deps.AddMember(name: "dotted.name", pid: 13);
        _ = deps.AddMember(name: "off", pid: 14, extraRowJson: ",\"adapter2\":1");
        deps.TeamList.Add(new CodexTeam("/h/teams/other.json", "other", JsonText.Parse("{\"members\":[{\"name\":\"claude\",\"kind\":\"claude\",\"adapter\":\"appserver\",\"appserver_pid\":99}]}")));
        deps.TeamList.Add(new CodexTeam("/h/teams/optout.json", "optout", JsonText.Parse("{\"members\":[{\"name\":\"x\",\"kind\":\"codex\",\"adapter\":\"off\",\"appserver_pid\":98}]}")));
        deps.TeamList.Add(new CodexTeam("/h/teams/a.b.json", "a.b", JsonText.Parse("{\"members\":[{\"name\":\"y\",\"kind\":\"codex\",\"adapter\":\"appserver\",\"appserver_pid\":97}]}")));
        _ = deps.LivePids.Add(98);
        _ = deps.LivePids.Add(99);
        _ = deps.LivePids.Add(97);

        var wanted = CodexAdapter.WantedMembers(deps.Teams(), deps);

        Assert.Equal(ServedKeys, wanted.Members.Select(m => m.Key).Order().ToArray());
        var why = wanted.Skipped.ToDictionary(s => s.Key, s => s.Why);
        Assert.Equal("no live appserver_pid", why["tm.dead"]);
        Assert.Equal("no recorded thread id", why["tm.nothread"]);
        Assert.Equal("no socket file", why["tm.nosock"]);
        Assert.Equal("a name contains '.'", why["tm.dotted.name"]);
        Assert.Equal("a name contains '.'", why["a.b.y"]);
        Assert.DoesNotContain("other.claude", why.Keys);
        Assert.DoesNotContain("optout.x", why.Keys);
    }

    [Fact]
    public async Task AMemberRowsHerdrFlavourIsStampedOnItsRegistration()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember(extraRowJson: ",\"herdr_flavour\":\"core\"");
        var adapter = Adapter(deps, new ScriptedClient { Answer = (_, _) => new JsonObject() });

        await adapter.TickAsync(TestContext.Current.CancellationToken);

        Assert.Equal("core", Assert.Single(deps.Registered).HerdrFlavour);
    }

    [Fact]
    public async Task AServedMemberIsRegisteredHeartbeatsAndGetsDueMailOnce()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember();
        deps.PendingEntries.Add(new CodexPendingEntry("m1", JsonValue.Null));
        var client = new ScriptedClient { Answer = (method, _) => method == "turn/start" ? JsonText.Parse("{\"turn\":{\"id\":\"tu-1\"}}") : new JsonObject() };
        var adapter = Adapter(deps, client);

        await adapter.TickAsync(TestContext.Current.CancellationToken);
        await adapter.TickAsync(TestContext.Current.CancellationToken);

        var registration = Assert.Single(deps.Registered);
        Assert.Equal("codex-appserver.tm.alpha", registration.SessionId);
        Assert.Equal("codex-appserver", registration.Harness);
        Assert.Null(registration.HerdrFlavour); // an untagged row registers no flavour, as before
        Assert.Equal(new CodexBox("alpha@tm", "/box/alpha@tm"), Assert.Single(registration.Boxes));
        Assert.Equal(2, deps.Heartbeats.Count);
        Assert.Equal(new CodexHeartbeat("codex-appserver", "codex-appserver.tm.alpha", 5000), deps.Heartbeats[0].Beat);
        var starts = client.Requests.Where(r => r.Method == "turn/start").ToArray();
        Assert.Single(starts);
        Assert.Equal("mail for alpha@tm: m1", starts[0].Params!.Value.AsObject()["input"].AsArray()[0].AsObject()["text"].AsString());
        Assert.Equal(1, adapter.HandedCount("tm.alpha"));
        Assert.True(adapter.IsConnected("tm.alpha"));
    }

    [Fact]
    public async Task AnEntryIsAckedWhenTheServerReportsItsMessageAndNeverBefore()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember();
        deps.PendingEntries.Add(new CodexPendingEntry("m1", JsonValue.Null));
        var client = new ScriptedClient();
        var adapter = Adapter(deps, client);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Empty(deps.Acks);

        client.Push("item/completed", JsonText.Parse("{\"threadId\":\"th-1\",\"item\":{\"type\":\"userMessage\",\"content\":[{\"type\":\"text\",\"text\":\"mail for alpha@tm: other\"}]}}"));
        Assert.Empty(deps.Acks);
        client.Push("item/completed", JsonText.Parse("{\"threadId\":\"th-OTHER\",\"item\":{\"type\":\"userMessage\",\"content\":[{\"type\":\"text\",\"text\":\"mail for alpha@tm: m1\"}]}}"));
        Assert.Empty(deps.Acks);
        client.Push("item/completed", JsonText.Parse("{\"threadId\":\"th-1\",\"item\":{\"type\":\"userMessage\",\"content\":[{\"type\":\"text\",\"text\":\"mail for alpha@tm: m1\"}]}}"));

        var ack = Assert.Single(deps.Acks);
        Assert.Equal(("/box/alpha@tm", "m1", "codex-appserver.tm.alpha"), ack);
    }

    [Fact]
    public async Task AServerThatDidNotApplyThePinsGetsNoTurnAndIsRefusedOnce()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember();
        deps.PendingEntries.Add(new CodexPendingEntry("m1", JsonValue.Null));
        var client = new ScriptedClient();
        var weak = JsonText.Parse("{\"thread\":{\"id\":\"th-1\"},\"sandbox\":{\"type\":\"workspaceWrite\",\"networkAccess\":true,\"excludeSlashTmp\":true,\"excludeTmpdirEnvVar\":true},\"approvalPolicy\":\"on-request\"}");
        var adapter = Adapter(deps, client, (_, _) => Task.FromResult<JsonValue?>(weak));

        await adapter.TickAsync(TestContext.Current.CancellationToken);

        Assert.True(client.WasClosed);
        Assert.Empty(deps.Registered);
        Assert.DoesNotContain(client.Requests, r => r.Method.StartsWith("turn/", StringComparison.Ordinal));
        Assert.Equal(["tm.alpha:the app-server did not apply ah's pins"], deps.Refusals);
        Assert.Contains(deps.Logs, l => l.Contains("(network)", StringComparison.Ordinal));
        Assert.False(adapter.IsConnected("tm.alpha"));
    }

    [Fact]
    public async Task ANonResumableThreadBacksOffTwoSecondsFirstAndTheRetryWaits()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember();
        var connects = 0;
        var adapter = new CodexAdapter(deps, _ =>
        {
            connects++;
            return Task.FromResult<IAppServerClient>(new ScriptedClient());
        }, (_, _) => Task.FromResult<JsonValue?>(null));

        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, connects);
        deps.UtcNow += TimeSpan.FromMilliseconds(1999);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, connects);
        deps.UtcNow += TimeSpan.FromMilliseconds(1);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, connects);
        deps.UtcNow += TimeSpan.FromMilliseconds(3999);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, connects);
        deps.UtcNow += TimeSpan.FromMilliseconds(1);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(3, connects);
        Assert.Empty(deps.Registered);
    }

    [Fact]
    public async Task TheBackoffIsCappedAtThirtySeconds()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember();
        var connects = 0;
        var adapter = new CodexAdapter(deps, _ =>
        {
            connects++;
            throw new CodexClientException("ECONNREFUSED stand-in", JsonValue.FromString("ECONNREFUSED"));
        });

        var attempts = new List<DateTimeOffset>();
        var seen = 0;
        for (var step = 0; step < 1200 && attempts.Count < 9; step++)
        {
            await adapter.TickAsync(TestContext.Current.CancellationToken);
            if (connects > seen)
            {
                seen = connects;
                attempts.Add(deps.UtcNow);
            }

            deps.UtcNow += TimeSpan.FromMilliseconds(500);
        }

        var gaps = attempts.Zip(attempts.Skip(1), (a, b) => (b - a).TotalMilliseconds).ToArray();
        Assert.Equal([2000d, 4000d, 8000d, 16000d, 30000d, 30000d, 30000d, 30000d], gaps);
        Assert.Contains(deps.Logs, l => l.EndsWith("connect failed (ECONNREFUSED)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedRegistrationIsNotRetriedBeforeTheNextScan()
    {
        var deps = new FakeDependencies { RefuseRegistration = true };
        _ = deps.AddMember();
        var connects = 0;
        var adapter = new CodexAdapter(deps, _ =>
        {
            connects++;
            return Task.FromResult<IAppServerClient>(new ScriptedClient());
        }, (_, _) => Task.FromResult<JsonValue?>(GoodReport()));

        await adapter.TickAsync(TestContext.Current.CancellationToken);
        deps.UtcNow += TimeSpan.FromSeconds(10);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, connects);

        deps.UtcNow += TimeSpan.FromSeconds(25);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, connects);
    }

    [Fact]
    public async Task AMemberThatLeavesTheTeamFilesIsUnregisteredAndItsConnectionClosedOnTheNextScan()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember();
        var client = new ScriptedClient();
        var adapter = Adapter(deps, client);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(adapter.IsConnected("tm.alpha"));

        deps.TeamList.Clear();
        deps.UtcNow += TimeSpan.FromSeconds(29);
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.True(adapter.IsConnected("tm.alpha"));

        deps.UtcNow += TimeSpan.FromSeconds(1);
        await adapter.TickAsync(TestContext.Current.CancellationToken);

        Assert.Empty(adapter.Members());
        Assert.True(client.WasClosed);
        Assert.Equal(["codex-appserver.tm.alpha"], deps.Unregistered);
    }

    [Fact]
    public async Task AStalledMemberDoesNotDelayAnotherAndIsNotStartedTwice()
    {
        var deps = new FakeDependencies();
        _ = deps.AddMember(name: "slow", pid: 21);
        _ = deps.AddMember(name: "fast", pid: 22);
        var release = new TaskCompletionSource<IAppServerClient>();
        var slowConnects = 0;
        var fastClient = new ScriptedClient();
        var adapter = new CodexAdapter(
            deps,
            socket =>
            {
                if (socket.Contains("tm-slow", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref slowConnects);
                    return release.Task;
                }

                return Task.FromResult<IAppServerClient>(fastClient);
            },
            (_, _) => Task.FromResult<JsonValue?>(GoodReport()));

        var first = adapter.TickAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 1500 && !adapter.IsConnected("tm.fast"); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(adapter.IsConnected("tm.fast"));
        Assert.False(first.IsCompleted);

        release.SetResult(new ScriptedClient());
        await first;
        await adapter.TickAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, slowConnects);
    }

    private sealed class RecordingJobs : IJobRegistry
    {
        public List<(string Name, TimeSpan Period, Func<CancellationToken, Task> Handler)> Added { get; } = [];

        public void Add(string name, TimeSpan period, Func<CancellationToken, Task> handler) => Added.Add((name, period, handler));
    }

    [Fact]
    public async Task TheJobIsRegisteredEveryFiveSecondsAndRunsItsWorkOffTheCallingThread()
    {
        var deps = new FakeDependencies();
        using var release = new ManualResetEventSlim();
        bool? onPool = null;
        deps.TeamsOverride = () =>
        {
            onPool = Thread.CurrentThread.IsThreadPoolThread;
            _ = release.Wait(TimeSpan.FromSeconds(10));
            return [];
        };
        var jobs = new RecordingJobs();

        CodexModule.Register(jobs, deps);

        var job = Assert.Single(jobs.Added);
        Assert.Equal("codex-adapter", job.Name);
        Assert.Equal(TimeSpan.FromSeconds(5), job.Period);
        var started = job.Handler(CancellationToken.None);
        Assert.False(started.IsCompleted);
        release.Set();
        await started;
        Assert.True(onPool);
    }

    [Fact]
    public async Task AFailureInsideTheJobIsLoggedNotThrown()
    {
        var deps = new FakeDependencies { TeamsOverride = () => throw new InvalidOperationException("team files unreadable") };
        var jobs = new RecordingJobs();
        CodexModule.Register(jobs, deps);

        await jobs.Added[0].Handler(CancellationToken.None);

        Assert.Contains(deps.Logs, l => l == "codex-appserver tick failed (error)");
    }

    private sealed class ForeignRelay : ICodexRelay
    {
        public IReadOnlyList<CodexPending> Pending() => [];

        public CodexMember? MemberOf(string memberKey) => null;

        public Task<CodexAnswerResult> AnswerAsync(string memberKey, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text, string? idKey = null) =>
            Task.FromResult(new CodexAnswerResult("answered"));
    }

    [Fact]
    public void ARelayFromTheModuleIsAcceptedAndAForeignOneIsRefused()
    {
        var jobs = new RecordingJobs();

        CodexModule.Register(jobs, new FakeDependencies(), CodexModule.CreateRelay());
        var refused = Assert.Throws<ArgumentException>(() => CodexModule.Register(jobs, new FakeDependencies(), new ForeignRelay()));

        Assert.Single(jobs.Added);
        Assert.Equal("relay", refused.ParamName);
    }

    [Fact]
    public void ARelayWhoseJobHasNotStartedHoldsNothing()
    {
        var relay = CodexModule.CreateRelay();

        Assert.Empty(relay.Pending());
        Assert.Null(relay.MemberOf("team.member"));
    }

    [Fact]
    public void ARegistrationNeedsBothCollaborators()
    {
        Assert.Throws<ArgumentNullException>(() => CodexModule.Register(null!, new FakeDependencies()));
        Assert.Throws<ArgumentNullException>(() => CodexModule.Register(new RecordingJobs(), null!));
    }

    [Fact]
    public void TheModesOfARowComeFromItsAutoModeMappingThenItsOwnArgs()
    {
        var files = new IdentityFiles();
        var row = JsonText.Parse("{\"kind\":\"codex\",\"args\":[\"--sandbox\",\"read-only\"]}");

        var modes = CodexModule.ModesOfRow(row, ["--ask-for-approval", "never"], "/tmp/ah", files);
        var defaults = CodexModule.ModesOfRow(JsonText.Parse("{\"kind\":\"codex\"}"), null, "/tmp/ah", files);
        var violating = CodexModule.ModesOfRow(JsonText.Parse("{\"args\":[\"--sandbox\",\"danger-full-access\"]}"), null, "/tmp/ah", files);

        Assert.Equal(new CodexModes("read-only", "never"), modes);
        Assert.Equal(new CodexModes("workspace-write", "on-request"), defaults);
        Assert.Equal(new CodexModes("workspace-write", "on-request"), violating);
    }
}
