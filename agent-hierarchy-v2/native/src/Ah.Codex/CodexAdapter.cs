using Ah.Core.Json;

namespace Ah.Codex;

/// <summary>A team file of a live registration, as the adapter reads it: its path, the team name and the parsed document.</summary>
public sealed record CodexTeam(string Path, string Team, JsonValue Data);

/// <summary>A member the adapter serves: a codex row with a live server, a socket file and a recorded thread.</summary>
public sealed record CodexMember(
    string Key,
    string Team,
    string Name,
    string Address,
    string TeamFile,
    int Pid,
    string Socket,
    string ThreadId,
    JsonValue Row);

/// <summary>One mailbox box a registration owns.</summary>
public sealed record CodexBox(string Address, string Dir);

/// <summary>
/// What the adapter registers a member under; the dependencies' <c>Register</c> throws to refuse it. <paramref name="HerdrFlavour"/> is the
/// member row's spawn-time tag, null when it has none.
/// </summary>
public sealed record CodexRegistration(string SessionId, IReadOnlyList<CodexBox> Boxes, string TeamFile, string Harness, int Pid, string? HerdrFlavour = null);

/// <summary>The heartbeat the adapter writes into a box on every poll.</summary>
public sealed record CodexHeartbeat(string Adapter, string SessionId, int PollMs);

/// <summary>One unacknowledged entry of a box, oldest first; <c>Entry</c> is whatever the mailbox keeps and is only handed to <c>Render</c>.</summary>
public sealed record CodexPendingEntry(string Id, JsonValue Entry);

/// <summary>
/// What the adapter takes from the rest of ahd, mirroring the dependencies the Node adapter is given: the team files, the
/// mailbox calls (heartbeat, register, unregister, ack, pending), liveness and file checks, and the member row's activity.
/// The composition root implements it over the mesh's mailbox facade; nothing here reaches a file or a process itself.
/// </summary>
public interface ICodexDependencies
{
    /// <summary>The current time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>The ah home, under which each member's server socket lives (<c>run/codex/&lt;team&gt;-&lt;name&gt;.sock</c>).</summary>
    string AhHome { get; }

    /// <summary>The team files of the live registrations, each once.</summary>
    IReadOnlyList<CodexTeam> Teams();

    bool PidAlive(int pid);

    bool SocketExists(string path);

    /// <summary>The absolute box directory of a member's address.</summary>
    string BoxFor(string address, CodexMember member);

    /// <summary>The values ah pinned for the member, which its server's report must show.</summary>
    CodexModes ModesFor(CodexMember member);

    void Register(CodexRegistration registration);

    void Unregister(string sessionId);

    void Heartbeat(string box, CodexHeartbeat heartbeat);

    IReadOnlyList<CodexPendingEntry> Pending(string box);

    /// <summary>The hand-over text for due entries.</summary>
    string Render(string address, IReadOnlyList<CodexPendingEntry> entries);

    void Ack(string box, IReadOnlyList<string> ids, string sessionId);

    /// <summary>A member's activity as the server reports it: <c>working</c>, <c>idle</c>, <c>failed</c> or <c>blocked</c> (with the approval label).</summary>
    void Activity(CodexMember member, string state, string? blockedBy);

    /// <summary>The gate failed on a connect: the daemon notes it on the member row and mails the orchestrator once.</summary>
    void Refused(CodexMember member, string note);

    /// <summary>A server request is now held for the member's Orchestrator.</summary>
    void RequestRaised(CodexMember member, CodexPending pending);

    /// <summary>A held request was dropped without an answer (member exit, turn end, restart).</summary>
    void RequestDropped(CodexMember member, CodexPending pending, string why);

    /// <summary>The member's server sent a request ah does not route; it was refused with method-not-found.</summary>
    void RequestUnsupported(CodexMember member, string method);

    /// <summary>The member asked for more permissions; ah refused it at once, fail-closed, until its response shape is established.</summary>
    void PermissionsDeclined(CodexMember member, string details);

    /// <summary>A turn ended; <paramref name="lastMessage"/> is its final agent message (at most 2 KB), or null if it had none.</summary>
    void TurnEnded(CodexMember member, string? lastMessage);

    void Log(string message);
}

/// <summary>
/// The ahd side of the Codex app-server adapter: which members to serve, one connection each with backoff, the member's
/// registration and heartbeat, and the hand-over of due mail through a member session. <see cref="TickAsync"/> is idempotent.
/// </summary>
internal sealed class CodexAdapter
{
    public const int PollMs = 5000;
    public const int RescanMs = 30 * 1000;
    public const int BackoffFirstMs = 1000;
    public const int BackoffMaxMs = 30 * 1000;

    private readonly ICodexDependencies _deps;
    private readonly Func<string, Task<IAppServerClient>> _connect;
    private readonly Func<IAppServerClient, CodexMember, Task<JsonValue?>> _resume;
    private readonly TimerStarter? _timers;
    private readonly object _gate = new();
    private readonly List<MemberState> _live = [];
    private long _lastScan;

    public CodexAdapter(
        ICodexDependencies deps,
        Func<string, Task<IAppServerClient>>? connect = null,
        Func<IAppServerClient, CodexMember, Task<JsonValue?>>? resume = null,
        TimerStarter? timers = null)
    {
        _deps = deps;
        _connect = connect ?? (async socket => await new AppServerClient(socket).OpenAsync().ConfigureAwait(false));
        _resume = resume ?? ResumeRecordedAsync;
        _timers = timers;
    }

    private sealed class MemberState(CodexMember member, string sessionId)
    {
        public CodexMember M { get; } = member;

        public string SessionId { get; } = sessionId;

        public IAppServerClient? Client { get; set; }

        public CodexMemberSession? Session { get; set; }

        public string? Box { get; set; }

        public HashSet<string> Handed { get; } = [];

        public long Backoff { get; set; } = BackoffFirstMs;

        public long NextTry { get; set; }

        public bool Connecting { get; set; }

        public bool Rejected { get; set; }

        public bool RefusedNoted { get; set; }

        public Task? Busy { get; set; }
    }

    /// <summary>The synthetic session id a member is registered under; <c>.</c> separators fit the mailbox's name charset.</summary>
    public static string MemberSessionId(string team, string name) => $"{CodexModule.Harness}.{team}.{name}";

    /// <summary>A team or member name the synthetic id could not tell apart from another (it contains the separator).</summary>
    public static bool AmbiguousName(string? value) => string.IsNullOrEmpty(value) || value.Contains('.', StringComparison.Ordinal);

    /// <summary>Resume the member's recorded thread: the spawn created it and recorded its id, so nothing is searched for and no thread is created here.</summary>
    public static async Task<JsonValue?> ResumeRecordedAsync(IAppServerClient client, CodexMember member)
    {
        if (string.IsNullOrEmpty(member.ThreadId))
        {
            return null;
        }

        try
        {
            var report = await client.RequestAsync("thread/resume", new JsonObject { ["threadId"] = member.ThreadId }).ConfigureAwait(false);
            return report.IsMap || report.IsArray ? report : (JsonValue?)null;
        }
        catch (CodexClientException)
        {
            return null;
        }
    }

    public sealed record Wanted(IReadOnlyList<CodexMember> Members, IReadOnlyList<(string Key, string Why)> Skipped);

    /// <summary>
    /// The members the adapter serves, from the team files: kind codex, adapter appserver, the <c>appserver_thread_id</c> the
    /// spawn recorded, an <c>appserver_pid</c> that is alive and a socket file that exists, with names that keep the id
    /// unambiguous. A row without a thread id is not adapted: its mail stays on the paste rung.
    /// </summary>
    public static Wanted WantedMembers(IReadOnlyList<CodexTeam> teams, ICodexDependencies deps)
    {
        var members = new List<CodexMember>();
        var skipped = new List<(string, string)>();
        foreach (var team in teams)
        {
            var rows = team.Data.Member("members");
            if (rows is not { IsArray: true } array)
            {
                continue;
            }

            foreach (var row in array.AsArray())
            {
                if (!CodexVersions.RowAdapted(row))
                {
                    continue;
                }

                var name = row.Member("name") is { IsText: true } n ? n.AsString() : null;
                var key = $"{team.Team}.{name ?? "undefined"}";
                if (AmbiguousName(team.Team) || AmbiguousName(name))
                {
                    skipped.Add((key, "a name contains '.'"));
                    continue;
                }

                var pidValue = row.Member("appserver_pid");
                if (pidValue is not { IsNumber: true } pidNumber || pidNumber.AsNumber() != Math.Floor(pidNumber.AsNumber())
                    || Math.Abs(pidNumber.AsNumber()) > int.MaxValue || !deps.PidAlive((int)pidNumber.AsNumber()))
                {
                    skipped.Add((key, "no live appserver_pid"));
                    continue;
                }

                var thread = row.Member("appserver_thread_id") is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;
                if (thread is null)
                {
                    skipped.Add((key, "no recorded thread id"));
                    continue;
                }

                var socket = CodexLaunch.AppserverSocket(deps.AhHome, team.Team, name!);
                if (!deps.SocketExists(socket))
                {
                    skipped.Add((key, "no socket file"));
                    continue;
                }

                members.Add(new CodexMember(key, team.Team, name!, $"{name}@{team.Team}", team.Path, (int)pidNumber.AsNumber(), socket, thread, row));
            }
        }

        return new Wanted(members, skipped);
    }

    private long Now() => _deps.UtcNow.ToUnixTimeMilliseconds();

    private void Report(MemberState state, Action report)
    {
        try
        {
            report();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _deps.Log($"codex-appserver {state.M.Key}: not reported ({CodexProtocol.ErrorClass(e)})");
        }
    }

    /// <summary>The server requests held for every connected member.</summary>
    public IReadOnlyList<CodexPending> PendingRequests()
    {
        MemberState[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _live];
        }

        return [.. snapshot.Select(s => s.Session).OfType<CodexMemberSession>().SelectMany(s => s.Held())];
    }

    /// <summary>A served member, or null.</summary>
    public CodexMember? MemberOf(string memberKey)
    {
        lock (_gate)
        {
            return _live.Find(s => s.M.Key == memberKey)?.M;
        }
    }

    /// <summary>Replies to one held request of the member <paramref name="memberKey"/>; see <see cref="CodexAnswerResult"/> for the outcomes.</summary>
    public Task<CodexAnswerResult> AnswerAsync(string memberKey, string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text, string? idKey = null)
    {
        CodexMemberSession? session;
        lock (_gate)
        {
            session = _live.Find(s => s.M.Key == memberKey)?.Session;
        }

        return session is null
            ? Task.FromResult(new CodexAnswerResult("disconnected", "the member has no live app-server connection"))
            : session.AnswerAsync(requestId, choice, answers, text, idKey);
    }

    public IReadOnlyList<string> Members()
    {
        lock (_gate)
        {
            return [.. _live.Select(s => s.M.Key)];
        }
    }

    public int HandedCount(string key)
    {
        lock (_gate)
        {
            return _live.Find(s => s.M.Key == key)?.Handed.Count ?? 0;
        }
    }

    public bool IsConnected(string key)
    {
        lock (_gate)
        {
            return _live.Find(s => s.M.Key == key)?.Client is { IsOpen: true };
        }
    }

    private void Drop(MemberState state, string why, bool unregister)
    {
        IAppServerClient? client;
        CodexMemberSession? session;
        lock (_gate)
        {
            client = state.Client;
            session = state.Session;
            _ = _live.Remove(state);
        }

        session?.DropAll(why);
        client?.Close();
        if (unregister)
        {
            _deps.Unregister(state.SessionId);
        }

        _deps.Log($"codex-appserver {state.M.Key}: {why}");
    }

    private long Backoff(MemberState state)
    {
        lock (_gate)
        {
            state.Backoff = Math.Min(BackoffMaxMs, state.Backoff * 2);
            state.NextTry = Now() + state.Backoff;
            return state.Backoff;
        }
    }

    private async Task ConnectOneAsync(MemberState state)
    {
        lock (_gate)
        {
            state.Connecting = true;
        }

        try
        {
            var client = await _connect(state.M.Socket).ConfigureAwait(false);
            JsonValue? report;
            try
            {
                report = await _resume(client, state.M).ConfigureAwait(false);
            }
            catch (CodexClientException)
            {
                report = null;
            }

            if (report is null)
            {
                client.Close();
                _ = Backoff(state);
                _deps.Log($"codex-appserver {state.M.Key}: the recorded thread cannot be resumed yet");
                return;
            }

            // The gate, on every (re)connect: a server that is not running with ah's pins gets no turn.
            var gate = CodexProtocol.GateReport(report, _deps.ModesFor(state.M));
            if (!gate.Ok)
            {
                client.Close();
                _ = Backoff(state);
                _deps.Log($"codex-appserver {state.M.Key}: refused, the thread's mode does not match ah's pins ({string.Join(", ", gate.Failed)})");
                if (!state.RefusedNoted)
                {
                    state.RefusedNoted = true;
                    try
                    {
                        _deps.Refused(state.M, "the app-server did not apply ah's pins");
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        _deps.Log($"codex-appserver {state.M.Key}: refusal not reported: {e.Message}");
                    }
                }

                return;
            }

            state.RefusedNoted = false;
            var box = _deps.BoxFor(state.M.Address, state.M);
            try
            {
                _deps.Register(new CodexRegistration(state.SessionId, [new CodexBox(state.M.Address, box)], state.M.TeamFile, CodexModule.Harness, state.M.Pid, Ah.Core.Mux.HerdrFlavour.OfMember(state.M.Row)));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                client.Close();
                lock (_gate)
                {
                    state.Rejected = true;
                }

                _deps.Log($"codex-appserver {state.M.Key}: registration refused, not retried before the next scan: {e.Message}");
                return;
            }

            var session = new CodexMemberSession(
                client,
                state.M.ThreadId,
                ids => _deps.Ack(box, ids, state.SessionId),
                (activityState, by) => _deps.Activity(state.M, activityState, by),
                _timers,
                events: new CodexRequestEvents(state.M.Key)
                {
                    Raised = p => Report(state, () => _deps.RequestRaised(state.M, p)),
                    Dropped = (p, why) => Report(state, () => _deps.RequestDropped(state.M, p, why)),
                    Unsupported = method => Report(state, () => _deps.RequestUnsupported(state.M, method)),
                    PermissionsDeclined = details => Report(state, () => _deps.PermissionsDeclined(state.M, details)),
                    TurnEnded = last => Report(state, () => _deps.TurnEnded(state.M, last)),
                });
            lock (_gate)
            {
                state.Client = client;
                state.Box = box;
                state.Session = session;
                state.Backoff = BackoffFirstMs;
            }

            client.OnClose(() =>
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(state.Client, client))
                    {
                        return;
                    }

                    state.Client = null;
                    state.Session = null;
                    state.Backoff = BackoffFirstMs;
                    state.NextTry = Now() + BackoffFirstMs;
                }

                session.DropAll("the connection to the member ended");
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _ = Backoff(state);
            _deps.Log($"codex-appserver {state.M.Key}: connect failed ({CodexProtocol.ErrorClass(e)})");
        }
        finally
        {
            lock (_gate)
            {
                state.Connecting = false;
            }
        }
    }

    private async Task PollAsync(MemberState state)
    {
        IAppServerClient? client;
        CodexMemberSession? session;
        string? box;
        lock (_gate)
        {
            client = state.Client;
            session = state.Session;
            box = state.Box;
        }

        if (client is not { IsOpen: true } || session is null || box is null)
        {
            return;
        }

        _deps.Heartbeat(box, new CodexHeartbeat(CodexModule.Harness, state.SessionId, PollMs));
        var pending = _deps.Pending(box);
        var stillPending = pending.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        List<CodexPendingEntry> due;
        lock (_gate)
        {
            _ = state.Handed.RemoveWhere(id => !stillPending.Contains(id));
            due = [.. pending.Where(e => !state.Handed.Contains(e.Id))];
            foreach (var entry in due)
            {
                _ = state.Handed.Add(entry.Id);
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        var ids = due.Select(e => e.Id).ToArray();
        try
        {
            await session.HandOverAsync(ids, _deps.Render(state.M.Address, due)).ConfigureAwait(false);
        }
        catch (CodexClientException e)
        {
            _deps.Log($"codex-appserver {state.M.Key}: hand-over failed ({CodexProtocol.ErrorClass(e)})");
        }
    }

    /// <summary>One scan-and-serve pass; the connect, heartbeat and hand-over of each member run on their own, so one stalled server never delays another.</summary>
    public async Task TickAsync(CancellationToken cancellationToken = default)
    {
        var t = Now();
        bool rescan;
        lock (_gate)
        {
            rescan = t - _lastScan >= RescanMs || _lastScan == 0;
            if (rescan)
            {
                _lastScan = t;
            }
        }

        if (rescan)
        {
            var wanted = WantedMembers(_deps.Teams(), _deps);
            foreach (var (key, why) in wanted.Skipped)
            {
                _deps.Log($"codex-appserver {key}: not served ({why})");
            }

            var want = wanted.Members.ToDictionary(m => m.Key, StringComparer.Ordinal);
            MemberState[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _live];
            }

            foreach (var state in snapshot.Where(s => !want.ContainsKey(s.M.Key)))
            {
                Drop(state, "member gone or its server died", unregister: true);
            }

            lock (_gate)
            {
                foreach (var state in _live)
                {
                    state.Rejected = false;
                }

                foreach (var m in wanted.Members.Where(m => _live.All(s => s.M.Key != m.Key)))
                {
                    _live.Add(new MemberState(m, MemberSessionId(m.Team, m.Name)));
                }
            }
        }

        var jobs = new List<Task>();
        MemberState[] states;
        lock (_gate)
        {
            states = [.. _live];
        }

        foreach (var state in states)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? job;
            lock (_gate)
            {
                if (state.Busy is not null)
                {
                    continue;
                }

                bool connect = state.Client is null && !state.Connecting && !state.Rejected && t >= state.NextTry;
                var captured = state;
                job = Task.Run(async () =>
                {
                    try
                    {
                        if (connect)
                        {
                            await ConnectOneAsync(captured).ConfigureAwait(false);
                        }

                        await PollAsync(captured).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        _deps.Log($"codex-appserver {captured.M.Key}: tick failed ({CodexProtocol.ErrorClass(e)})");
                    }
                    finally
                    {
                        lock (_gate)
                        {
                            captured.Busy = null;
                        }
                    }
                }, CancellationToken.None);
                state.Busy = job;
            }

            jobs.Add(job);
        }

        await Task.WhenAll(jobs).ConfigureAwait(false);
    }

    /// <summary>
    /// The job the daemon runs every <see cref="PollMs"/>: one tick, entirely on a worker (the daemon's loop only starts it), returning
    /// when the tick has finished so two ticks never overlap, with any failure caught and logged here.
    /// </summary>
    public Task RunJobAsync(CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                try
                {
                    await TickAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // the daemon is draining
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _deps.Log($"codex-appserver tick failed ({CodexProtocol.ErrorClass(e)})");
                }
            },
            CancellationToken.None);

    /// <summary>Closes every connection; registrations are left to expire, as the Node adapter leaves them.</summary>
    public void Stop()
    {
        MemberState[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _live];
        }

        foreach (var state in snapshot)
        {
            Drop(state, "adapter stopped", unregister: false);
        }
    }
}
