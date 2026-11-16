using System.Text.RegularExpressions;
using Ah.Core.Json;

namespace Ah.Codex;

/// <summary>Arms a one-shot timer; disposing the result cancels it.</summary>
internal delegate IDisposable TimerStarter(TimeSpan delay, Action fire);

/// <summary>
/// One member's hand-over state. <c>HandOver(ids, text)</c> goes out as one <c>turn/start</c> when no turn is current and as
/// one <c>turn/steer</c> otherwise, one after the other. An entry id is acked when the server reports the user message item
/// carrying that exact text, and never if it does not within the ack wait. Activity follows <c>turn/started</c>,
/// <c>turn/completed</c> and the server requests it holds. A recognised server request is held until <see cref="AnswerAsync"/> replies
/// to it or the turn ends; an unrecognised one is refused at once with a JSON-RPC method-not-found error. This object never re-sends.
/// </summary>
internal sealed partial class CodexMemberSession
{
    private readonly IAppServerClient _client;
    private readonly string _threadId;
    private readonly Action<IReadOnlyList<string>> _ack;
    private readonly Action<string, string?> _activity;
    private readonly TimerStarter _startTimer;
    private readonly TimeSpan _ackWait;
    private readonly CodexRequestEvents? _events;
    private readonly Dictionary<string, HeldRequest> _held = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Gone> _gone = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _fileItems = new(StringComparer.Ordinal);
    private string? _lastQuestions;
    private string? _lastMessage;
    private readonly object _gate = new();
    private readonly List<Waiter> _waiting = [];
    private JsonValue? _turnId;
    private Task _tail = Task.CompletedTask;

    public CodexMemberSession(
        IAppServerClient client,
        string threadId,
        Action<IReadOnlyList<string>> ack,
        Action<string, string?> activity,
        TimerStarter? startTimer = null,
        TimeSpan? ackWait = null,
        CodexRequestEvents? events = null)
    {
        _events = events;
        _client = client;
        _threadId = threadId;
        _ack = ack;
        _activity = activity;
        _startTimer = startTimer ?? DefaultTimer;
        _ackWait = ackWait ?? TimeSpan.FromMilliseconds(CodexProtocol.AckWaitMs);
        client.OnNotification(OnNotification);
        client.OnServerRequest(OnServerRequest);
    }

    private sealed record HeldRequest(CodexPending Pending, JsonValue Id, JsonValue Params);

    private sealed record Gone(string Kind, IReadOnlyDictionary<string, string> Headers, bool Secret);

    private sealed class Waiter(IReadOnlyList<string> ids, string text)
    {
        public IReadOnlyList<string> Ids { get; } = ids;

        public string Text { get; } = text;

        public IDisposable? Timer { get; set; }
    }

    private static Timer DefaultTimer(TimeSpan delay, Action fire) =>
        new Timer(_ => fire(), null, delay, Timeout.InfiniteTimeSpan);

    private void Settle(Waiter waiter, bool acked)
    {
        lock (_gate)
        {
            if (!_waiting.Remove(waiter))
            {
                return;
            }
        }

        waiter.Timer?.Dispose();
        if (acked)
        {
            _ack(waiter.Ids);
        }
    }

    private static string? UserMessageText(JsonValue? item)
    {
        if (item is not { IsMap: true } it || it.Member("type") is not { IsText: true } type || type.AsString() != "userMessage")
        {
            return null;
        }

        var parts = it.Member("content") is { IsArray: true } content ? content : it.Member("input") is { IsArray: true } input ? input : default;
        if (!parts.IsArray)
        {
            return string.Empty;
        }

        var text = new System.Text.StringBuilder();
        foreach (var p in parts.AsArray())
        {
            if (p.Member("type") is { IsText: true } t && t.AsString() == "text")
            {
                text.Append(p.Member("text") is { IsText: true } s ? s.AsString() : string.Empty);
            }
        }

        return text.ToString();
    }

    private static JsonValue? Truthy(JsonValue? value) => value is { IsTruthy: true } v ? v : (JsonValue?)null;

    private void OnNotification(CodexNotification note)
    {
        var p = note.Params;
        var turn = p.Member("turn");
        var item = p.Member("item");
        var eventThread = Truthy(p.Member("threadId")) ?? Truthy(turn?.Member("threadId")) ?? Truthy(item?.Member("threadId"));
        if (eventThread is not { IsText: true } thread || thread.AsString() != _threadId)
        {
            return;
        }

        if (note.Method == "turn/started")
        {
            lock (_gate)
            {
                _turnId = Truthy(turn?.Member("id")) ?? Truthy(p.Member("turnId"));
                _lastMessage = null;
                _lastQuestions = null;
            }

            _activity("working", null);
        }
        else if (note.Method == "turn/completed")
        {
            lock (_gate)
            {
                _turnId = null;
            }

            var status = Truthy(turn?.Member("status"));
            var failed = status is { } s && FailPattern().IsMatch(s.IsText ? s.AsString() : JsonText.Stringify(s, 0));
            DropAll("the turn ended");
            _activity(failed ? "failed" : "idle", null);
            string? last;
            string? questions;
            lock (_gate)
            {
                last = _lastMessage;
                questions = _lastQuestions;
                _lastMessage = null;
                _lastQuestions = null;
                _fileItems.Clear();
            }

            var said = last is null ? null : CodexRequestKinds.Truncate(last, CodexRequestKinds.LastMessageLimit);
            _events?.TurnEnded?.Invoke(questions is null ? said : $"{said}\n\nQuestions attached to it:\n{questions}");
        }
        else if (note.Method is "item/started" or "item/updated" or "item/completed")
        {
            if (item is { IsMap: true } fileItem && fileItem.Member("type") is { IsText: true } fileType && fileType.AsString() == "fileChange"
                && fileItem.Member("id") is { IsText: true } fileId)
            {
                var paths = fileItem.Member("changes") is { IsArray: true } changes
                    ? changes.AsArray().Select(c => c.Member("path") is { IsText: true } path ? path.AsString() : null).OfType<string>().ToList()
                    : [];
                lock (_gate)
                {
                    _fileItems[fileId.AsString()] = paths;
                }

                return;
            }

            if (note.Method == "item/completed" && item is { IsMap: true } done
                && done.Member("type") is { IsText: true } kind && kind.AsString() == "agentMessage"
                && done.Member("text") is { IsText: true } said)
            {
                lock (_gate)
                {
                    _lastMessage = said.AsString();
                    _lastQuestions = QuestionLines(done.Member("questions"));
                }

                return;
            }

            var text = UserMessageText(item);
            if (text is null)
            {
                return;
            }

            Waiter? waiter;
            lock (_gate)
            {
                waiter = _waiting.Find(w => w.Text == text);
            }

            if (waiter is not null)
            {
                Settle(waiter, acked: true);
            }
        }
    }

    [GeneratedRegex("error|fail", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FailPattern();

    [GeneratedRegex("expected|mismatch|no active turn", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SteerMismatchPattern();

    private void OnServerRequest(JsonObject request)
    {
        var method = request.Get("method") is { IsText: true } m ? m.AsString() : string.Empty;
        if (_events is null || request.Get("id") is not { } id || CodexRequestKinds.IsLeftUnanswered(method))
        {
            return;
        }

        var kind = CodexRequestKinds.KindOf(method);
        if (kind == CodexRequestKinds.Permissions)
        {
            try
            {
                _client.RespondError(id, CodexRequestKinds.MethodNotFound, "method not found");
            }
            catch (CodexClientException)
            {
                // the connection ended; the server no longer waits
            }

            _events.PermissionsDeclined?.Invoke(CodexRequestKinds.Truncate(JsonText.Stringify(WithoutIds(request.Get("params")), 0), CodexRequestKinds.VerbatimLimit));
            return;
        }

        if (kind is null)
        {
            try
            {
                _client.RespondError(id, CodexRequestKinds.MethodNotFound, "method not found");
            }
            catch (CodexClientException)
            {
                // the connection ended; the server no longer waits
            }

            _events.Unsupported?.Invoke(method);
            return;
        }

        var parameters = request.Get("params") ?? new JsonObject();
        var secret = kind == CodexRequestKinds.Question && AnySecret(parameters);
        string? verbatim = secret ? null : CodexRequestKinds.Truncate(JsonText.Stringify(WithoutIds(parameters), 0), CodexRequestKinds.VerbatimLimit);
        var blocking = !(parameters.Member("isBlocking") is { IsBool: true } b && !b.AsBool());
        var itemId = parameters.Member("itemId") is { IsText: true } iid ? iid.AsString() : null;
        IReadOnlyList<string>? paths = null;
        if (kind == CodexRequestKinds.FileChangeApproval && itemId is not null)
        {
            lock (_gate)
            {
                paths = _fileItems.GetValueOrDefault(itemId);
            }
        }

        var requestId = DisplayId(id);
        var pending = new CodexPending(
            _events.MemberKey,
            requestId,
            kind,
            method,
            verbatim,
            secret,
            blocking,
            CodexRequestKinds.ChoicesOf(kind),
            itemId,
            parameters.Member("cwd") is { IsText: true } cwd ? cwd.AsString() : null,
            parameters.Member("grantRoot") is { IsText: true } root ? root.AsString() : null,
            paths,
            IdKey(id));
        lock (_gate)
        {
            _held[IdKey(id)] = new HeldRequest(pending, id, parameters);
        }

        _activity("blocked", kind);
        _events.Raised?.Invoke(pending);
    }

    /// <summary>A JSON-RPC id is a number or a string; the held table keeps the two apart, so 12 and "12" are different requests.</summary>
    private static string IdKey(JsonValue id) => id.IsNumber ? "n:" + DisplayId(id) : "s:" + DisplayId(id);

    private static string DisplayId(JsonValue id) => id.IsText ? id.AsString() : JsonText.Stringify(id, 0);

    private static JsonValue WithoutIds(JsonValue? parameters)
    {
        var shown = parameters is { IsMap: true } p ? p.AsObject().Clone() : new JsonObject();
        foreach (var key in (string[])["threadId", "turnId", "itemId"])
        {
            _ = shown.Remove(key);
        }

        return shown;
    }

    private static string? QuestionLines(JsonValue? questions)
    {
        if (questions is not { IsArray: true } list || list.AsArray().Count == 0)
        {
            return null;
        }

        return string.Join(
            "\n",
            list.AsArray().Select(q => $"- {JsonText.Stringify(q, 0)}"));
    }

    private static bool AnySecret(JsonValue parameters) =>
        parameters.Member("questions") is { IsArray: true } qs
        && qs.AsArray().Any(q => q.Member("isSecret") is { IsBool: true } flag && flag.AsBool());

    /// <summary>The requests held right now.</summary>
    public IReadOnlyList<CodexPending> Held()
    {
        lock (_gate)
        {
            return [.. _held.Values.Select(h => h.Pending)];
        }
    }

    /// <summary>Forgets every held request, reporting each as dropped; the server is not told, since the turn or connection that owned them is over.</summary>
    public void DropAll(string why)
    {
        List<HeldRequest> dropped;
        lock (_gate)
        {
            dropped = [.. _held.Values];
            _held.Clear();
            foreach (var h in dropped)
            {
                _gone[IdKey(h.Id)] = new Gone(h.Pending.Kind, QuestionHeaders(h.Params), h.Pending.Secret);
            }

            while (_gone.Count > 64)
            {
                _ = _gone.Remove(_gone.Keys.First());
            }
        }

        foreach (var h in dropped)
        {
            _events?.Dropped?.Invoke(h.Pending, why);
        }
    }

    private static Dictionary<string, string> QuestionHeaders(JsonValue parameters)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (parameters.Member("questions") is { IsArray: true } qs)
        {
            foreach (var q in qs.AsArray())
            {
                if (q.Member("id") is { IsText: true } qid)
                {
                    headers[qid.AsString()] = q.Member("header") is { IsText: true } h ? h.AsString() : qid.AsString();
                }
            }
        }

        return headers;
    }

    /// <summary>
    /// Replies to the held request <paramref name="requestId"/>. An approval or elicitation takes a <paramref name="choice"/>; a question
    /// takes <paramref name="answers"/> (question id, text). A question whose request is already gone is delivered as a steer, unless it was
    /// a secret one. <paramref name="idKey"/> names the request exactly (<see cref="CodexPending.IdKey"/>); without it the display id is
    /// looked up, and an id that names both a number and a string request is <c>ambiguous</c> and answers neither.
    /// </summary>
    public async Task<CodexAnswerResult> AnswerAsync(string requestId, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text, string? idKey = null)
    {
        HeldRequest? held = null;
        Gone? gone = null;
        string? heldKey = null;
        var ambiguous = false;
        lock (_gate)
        {
            string[] keys = idKey is not null ? [idKey] : ["n:" + requestId, "s:" + requestId];
            var found = keys.Where(_held.ContainsKey).ToList();
            ambiguous = found.Count > 1;
            if (found.Count == 1)
            {
                heldKey = found[0];
                held = _held[heldKey];
            }
            else if (found.Count == 0)
            {
                var goneFound = keys.Where(_gone.ContainsKey).ToList();
                ambiguous = goneFound.Count > 1;
                if (goneFound.Count == 1)
                {
                    gone = _gone[goneFound[0]];
                }
            }
        }

        if (ambiguous)
        {
            return new CodexAnswerResult("ambiguous", "both a number and a string request carry this id");
        }

        if (held is null)
        {
            if (gone is { Secret: true })
            {
                return new CodexAnswerResult("secret", "a secret question is answered in the member's pane");
            }

            if (gone is { Kind: CodexRequestKinds.Question } && answers.Count > 0)
            {
                var lines = answers.Select(a => $"Answer to your earlier question {gone.Headers.GetValueOrDefault(a.Key, a.Key)}: {a.Value}");
                try
                {
                    await DeliverAsync(string.Join("\n", lines)).ConfigureAwait(false);
                }
                catch (CodexClientException e)
                {
                    return new CodexAnswerResult("disconnected", e.Message);
                }

                return new CodexAnswerResult("steered");
            }

            return new CodexAnswerResult("unknown-request");
        }

        var pending = held.Pending;
        if (pending.Secret)
        {
            return new CodexAnswerResult("secret", "a secret question is answered in the member's pane");
        }

        var built = BuildResponse(held, choice, answers, text);
        if (built.Result is null)
        {
            return built.Failure!;
        }

        try
        {
            _client.Respond(held.Id, built.Result.Value);
        }
        catch (CodexClientException e)
        {
            return new CodexAnswerResult("disconnected", e.Message);
        }

        bool anyLeft;
        lock (_gate)
        {
            _ = _held.Remove(heldKey!);
            anyLeft = _held.Count > 0;
        }

        if (!anyLeft)
        {
            _activity("working", null);
        }

        return new CodexAnswerResult("answered");
    }

    private static (JsonValue? Result, CodexAnswerResult? Failure) BuildResponse(HeldRequest held, string? choice, IReadOnlyList<KeyValuePair<string, string>> answers, string? text)
    {
        var kind = held.Pending.Kind;
        CodexAnswerResult Bad(string detail) => new("bad-choice", detail);
        switch (kind)
        {
            case CodexRequestKinds.CommandApproval or CodexRequestKinds.FileChangeApproval:
                return choice is not null && held.Pending.Choices.Contains(choice)
                    ? (new JsonObject { ["decision"] = choice }, null)
                    : (null, Bad($"choose one of: {string.Join(", ", held.Pending.Choices)}"));
            case CodexRequestKinds.Elicitation:
                if (choice is null || !held.Pending.Choices.Contains(choice))
                {
                    return (null, Bad($"choose one of: {string.Join(", ", held.Pending.Choices)}"));
                }

                return choice == "accept"
                    ? (null, new CodexAnswerResult("needs-evidence", "the content an accepted elicitation carries is not established"))
                    : (new JsonObject { ["action"] = choice, ["content"] = JsonValue.Null }, null);
            case CodexRequestKinds.Question:
                var headers = QuestionHeaders(held.Params);
                if (answers.Count == 0 || headers.Keys.Any(q => answers.All(a => a.Key != q)) || answers.Any(a => !headers.ContainsKey(a.Key)))
                {
                    return (null, Bad($"answer every question, as qid=text: {string.Join(", ", headers.Keys)}"));
                }

                var byId = new JsonObject();
                foreach (var (qid, answer) in answers)
                {
                    byId[qid] = new JsonObject { ["answers"] = new JsonArray { answer } };
                }

                return (new JsonObject { ["answers"] = byId }, null);
            default:
                return (null, new CodexAnswerResult("needs-evidence", "the permissions response shape is not established"));
        }
    }

    private Task DeliverAsync(string text)
    {
        JsonValue? current;
        lock (_gate)
        {
            current = _turnId;
        }

        return current is { IsTruthy: true } ? SteerAsync(text) : StartAsync(text);
    }

    private async Task StartAsync(string text)
    {
        var result = await _client.RequestAsync("turn/start", new JsonObject { ["threadId"] = _threadId, ["input"] = CodexProtocol.TextInput(text) }).ConfigureAwait(false);
        var id = result.Member("turn") is { IsMap: true } turn ? Truthy(turn.Member("id")) : (JsonValue?)null;
        if (id is not null)
        {
            lock (_gate)
            {
                _turnId ??= id;
            }
        }
    }

    private async Task SteerAsync(string text)
    {
        JsonValue? expected;
        lock (_gate)
        {
            expected = _turnId;
        }

        try
        {
            _ = await _client.RequestAsync(
                "turn/steer",
                new JsonObject { ["threadId"] = _threadId, ["expectedTurnId"] = expected ?? JsonValue.Null, ["input"] = CodexProtocol.TextInput(text) }).ConfigureAwait(false);
        }
        catch (CodexClientException e) when (SteerMismatchPattern().IsMatch(e.Message))
        {
            lock (_gate)
            {
                _turnId = null;
            }

            await StartAsync(text).ConfigureAwait(false);
        }
    }

    /// <summary>Queue one hand-over; completes once the server answered it (not once it is acked).</summary>
    public Task HandOverAsync(IReadOnlyList<string> ids, string text)
    {
        var waiter = new Waiter(ids, text);
        Task previous;
        lock (_gate)
        {
            _waiting.Add(waiter);
            waiter.Timer = _startTimer(_ackWait, () => Settle(waiter, acked: false));
            previous = _tail;
        }

        async Task Run()
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (CodexClientException)
            {
                // an earlier hand-over's failure is that hand-over's own
            }

            await DeliverAsync(text).ConfigureAwait(false);
        }

        var task = Run();
        lock (_gate)
        {
            _tail = task;
        }

        return task.ContinueWith(
            t =>
            {
                if (t.IsFaulted)
                {
                    Settle(waiter, acked: false);
                }

                return t;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default).Unwrap();
    }
}
