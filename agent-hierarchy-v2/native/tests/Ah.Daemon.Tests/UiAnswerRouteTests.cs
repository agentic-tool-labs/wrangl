#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary><c>POST /ui/v1/answer</c> on a real listener, against a fake answer support: the status table, the method gate and the auth checks.</summary>
public sealed class UiAnswerRouteTests
{
    private const string DirId = "abcd1234";

    private const string Dir = "/repo/.claude/ah2";

    private sealed class FakeAnswers
    {
        public List<JsonObject> Calls { get; } = [];

        public UiAnswerSupport Support => new(id => id == DirId ? Dir : null, Answer, static (team, ev) => team == "t1" && ev == "e1" ? new JsonObject { ["seen"] = true, ["seen_at"] = 5, ["state"] = "acked" } : null, static (dir, q) => q.Id is { } id
            ? new JsonObject { ["threads"] = new JsonArray { new JsonObject { ["id"] = id } }, ["dir"] = dir }
            : new JsonObject { ["query"] = $"{q.Search}|{q.Team}|{q.Since}|{q.Answered}|{q.Questions}|{q.Before}|{q.Limit}", ["dir"] = dir });

        private JsonObject Answer(JsonObject input)
        {
            lock (Calls)
            {
                Calls.Add(input);
            }

            static JsonObject Refused(string reason, string detail) => new() { ["refused"] = new JsonObject { ["reason"] = reason, ["detail"] = detail } };
            return (input.Get("id") ?? input.Get("item"))!.Value.AsString() switch
            {
                "s:done" => Refused("not-waiting", "s:done no longer waits on you"),
                "s:past" => new JsonObject { ["bad_request"] = "d names no option of s:past" },
                "s:evt" => new JsonObject { ["id"] = "s:evt", ["answers"] = "s:evt", ["decision"] = "Ship", ["event_error"] = "unknown_address: nobody", ["event_id"] = "e9" },
                "s:ok" => new JsonObject { ["id"] = "s:ok", ["answers"] = "s:ok", ["decision"] = "Ship", ["event_id"] = "e1" },
                "closed" => Refused("already-closed", "decision closed is already closed by d9 (answered)"),
                "norun" => Refused("no-run", "no open pipeline-run exchange for this team"),
                "stale" => Refused("stale-run", "open run is 20261031-000000-aaaa"),
                "boom" => throw new IOException("disk gone"),
                "evt" => new JsonObject { ["id"] = "d9", ["answers"] = "evt", ["decision"] = "Yes", ["event_error"] = "unknown_address: nobody" },
                _ => new JsonObject { ["id"] = "d9", ["answers"] = input.Get("id")!.Value, ["decision"] = "Yes" },
            };
        }
    }

    private static async Task<(UiRig Rig, UiGrant Grant, FakeAnswers Answers)> OpenAsync(bool wired = true)
    {
        var answers = new FakeAnswers();
        var rig = UiRig.Create(answer: wired ? answers.Support : null);
        return (rig, await rig.LeaseAsync(), answers);
    }

    private static string Body(string id = "d1", string choice = "\"choice\":\"a\"") =>
        $$"""{"dir":"{{DirId}}","team":"t1","run":"20261031-000000-aaaa","id":"{{id}}",{{choice}}}""";

    private static async Task<(HttpStatusCode Status, JsonValue Body)> PostAsync(UiRig rig, UiGrant grant, string body, string path = "/ui/v1/answer")
    {
        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Post, path, request => request.Content = UiRig.Json(body));
        var text = await reply.Content.ReadAsStringAsync();
        return (reply.StatusCode, JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(text), out var parsed) ? parsed : JsonValue.Null);
    }

    private static string Text(JsonValue body, string key) => body.Member(key)!.Value.AsString();

    [Fact]
    public async Task AnAnswerIsAppendedThroughTheSupportWithTheResolvedDirAndAnswers200()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.True(body.Member("ok")!.Value.AsBool());
        Assert.Equal(("d9", "d1", "Yes"), (Text(body, "id"), Text(body, "answers"), Text(body, "decision")));
        var call = Assert.Single(answers.Calls);
        Assert.Equal((Dir, "t1", "20261031-000000-aaaa", "d1", "a"), (call.Get("dir")!.Value.AsString(), call.Get("team")!.Value.AsString(), call.Get("run")!.Value.AsString(), call.Get("id")!.Value.AsString(), call.Get("choice")!.Value.AsString()));
        Assert.False(call.ContainsKey("choices"));
        Assert.Contains($"ui answer: route=/ui/v1/answer dir={DirId} team=t1 id=d1 outcome=answered", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SeveralChoicesArePassedAsChoices()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, Body(choice: "\"choices\":[\"a\",\"c\",\"other: x\"]"));

        Assert.Equal(HttpStatusCode.OK, status);
        var call = Assert.Single(answers.Calls);
        Assert.Equal(["a", "c", "other: x"], call.Get("choices")!.Value.AsArray().Select(static c => c.AsString()));
        Assert.False(call.ContainsKey("choice"));
    }

    [Theory]
    [InlineData("closed", "already-closed", "decision closed is already closed by d9 (answered)")]
    [InlineData("norun", "no-run", "no open pipeline-run exchange for this team")]
    [InlineData("stale", "stale-run", "open run is 20261031-000000-aaaa")]
    public async Task ARefusalIsPassedThroughAs409(string id, string reason, string detail)
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body(id));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.True(body.Member("refused")!.Value.AsBool());
        Assert.Equal((reason, detail), (Text(body, "reason"), Text(body, "detail")));
        Assert.Contains($"id={id} outcome={reason}", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownDirIdIs409UnknownDirAndTheSupportIsNotAsked()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body().Replace(DirId, "ffffffff", StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(("unknown-dir", "ffffffff"), (Text(body, "reason"), Text(body, "detail")));
        Assert.Empty(answers.Calls);
        Assert.Contains("dir=ffffffff team=t1 id=d1 outcome=unknown-dir", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAnswerThatThrowsIsALogged500()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body("boom"));

        Assert.Equal(HttpStatusCode.InternalServerError, status);
        Assert.Equal("internal", Text(body, "error"));
        Assert.Single(answers.Calls);
        Assert.Contains("id=boom outcome=error detail=IOException", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANonJsonContentTypeIs415AndLogged()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Post, "/ui/v1/answer", request =>
        {
            request.Content = new StringContent(Body(), System.Text.Encoding.UTF8, "text/plain");
        });

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, reply.StatusCode);
        Assert.Empty(answers.Calls);
        Assert.Contains("ui answer: route=/ui/v1/answer dir=- team=- id=- outcome=content_type", rig.LogText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/ui/v1/answer")]
    public async Task ARebindingHostIsRefusedWith421AndTheSupportIsNotAsked(string path)
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Post, path, request =>
        {
            request.Content = UiRig.Json(Body());
            request.Headers.Host = "127.0.0.2:" + grant.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        });

        Assert.Equal(HttpStatusCode.MisdirectedRequest, reply.StatusCode);
        Assert.Empty(answers.Calls);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1]")]
    [InlineData("""{"dir":"abcd1234","team":"t1","run":"r","choice":"a"}""")]
    [InlineData("""{"dir":"abcd1234","team":"","run":"r","id":"d1","choice":"a"}""")]
    [InlineData("""{"dir":"abcd1234","team":"t1","run":"r","id":"d1"}""")]
    [InlineData("""{"dir":"abcd1234","team":"t1","run":"r","id":"d1","choice":"a","choices":["a"]}""")]
    [InlineData("""{"dir":"abcd1234","team":"t1","run":"r","id":"d1","choices":[]}""")]
    [InlineData("""{"dir":"abcd1234","team":"t1","run":"r","id":"d1","choices":["a",3]}""")]
    [InlineData("""{"dir":"abcd1234","team":"t1","run":"r","id":"d1","choice":""}""")]
    public async Task AMalformedBodyIs400AndTheSupportIsNotAsked(string body)
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, reply) = await PostAsync(rig, grant, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("bad-request", Text(reply, "error"));
        Assert.Empty(answers.Calls);
        Assert.Contains("outcome=bad-request detail=", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadRequestLogsTheFieldsThatDidParse()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        _ = await PostAsync(rig, grant, """{"dir":"abcd1234","team":"t1","run":"r","choice":"a"}""");

        Assert.Contains("ui answer: route=/ui/v1/answer dir=abcd1234 team=t1 id=- outcome=bad-request detail=id must be a non-empty string", rig.LogText, StringComparison.Ordinal);
    }

    private static string ItemBody(string item = "s:ok", string rest = "\"choice\":\"a\"") =>
        $$"""{"dir":"{{DirId}}","team":"t1","item":"{{item}}","since":"2026-11-02T11:00:00Z",{{rest}}}""";

    [Fact]
    public async Task AnItemAnswerReachesTheSupportWithItsFieldsAndAnswers200()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, ItemBody());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Ship", Text(body, "decision"));
        Assert.Null(body.Member("event_error"));
        Assert.Equal("e1", Text(body, "event_id"));
        var call = Assert.Single(answers.Calls);
        Assert.Equal((Dir, "t1", "s:ok", "2026-11-02T11:00:00Z", "a"), (call.Get("dir")!.Value.AsString(), call.Get("team")!.Value.AsString(), call.Get("item")!.Value.AsString(), call.Get("since")!.Value.AsString(), call.Get("choice")!.Value.AsString()));
        Assert.False(call.ContainsKey("run"));
        Assert.Contains("id=s:ok outcome=answered", rig.LogText, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\"choice\":\"a\",\"id\":\"d1\"")]
    [InlineData("\"choice\":\"a\",\"run\":\"r\"")]
    [InlineData("\"choices\":[\"a\"]")]
    [InlineData("\"choice\":\"\"")]
    public async Task NegativeAnItemAnswerWithADecisionFieldOrNoChoiceIs400(string rest)
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, ItemBody(rest: rest));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(answers.Calls);
    }

    [Theory]
    [InlineData("item", "\"dismiss\":\"Already done\"", HttpStatusCode.OK)]
    [InlineData("decision", "\"dismiss\":\"No longer needed\"", HttpStatusCode.OK)]
    [InlineData("item", "\"dismiss\":\"Already done\",\"choice\":\"a\"", HttpStatusCode.BadRequest)]
    [InlineData("item", "\"dismiss\":\" \"", HttpStatusCode.BadRequest)]
    [InlineData("decision", "\"dismiss\":\"xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\"", HttpStatusCode.BadRequest)]
    public async Task ADismissReachesTheSupportInPlaceOfAChoiceAndNegativeABadOneIs400(string form, string rest, HttpStatusCode expected)
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, form == "item" ? ItemBody(rest: rest) : Body(choice: rest));

        Assert.Equal(expected, status);
        if (expected == HttpStatusCode.OK)
        {
            var call = Assert.Single(answers.Calls);
            Assert.Equal(form == "item" ? "Already done" : "No longer needed", call.Get("dismiss")!.Value.AsString());
            Assert.False(call.ContainsKey("choice"));
        }
        else
        {
            Assert.Empty(answers.Calls);
        }
    }

    [Theory]
    [InlineData("noseparator")]
    [InlineData(":a")]
    [InlineData("s:")]
    public async Task NegativeAnItemKeyThatIsNotStreamAndItemIs400(string item)
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, ItemBody(item));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(answers.Calls);
    }

    [Fact]
    public async Task AnItemRefusalIs409AndAChoiceNamingNoOptionIs400()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        var (refused, refusedBody) = await PostAsync(rig, grant, ItemBody("s:done"));
        var (past, pastBody) = await PostAsync(rig, grant, ItemBody("s:past", "\"choice\":\"d\""));

        Assert.Equal((HttpStatusCode.Conflict, "not-waiting"), (refused, Text(refusedBody, "reason")));
        Assert.Equal((HttpStatusCode.BadRequest, "bad-request"), (past, Text(pastBody, "error")));
    }

    [Fact]
    public async Task AFailedEventOnAnItemIsInThe200()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, ItemBody("s:evt"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("unknown_address: nobody", Text(body, "event_error"));
        Assert.Null(body.Member("event_id"));
    }

    [Fact]
    public async Task AnswerStatusIsTheSupportsReadAndEveryOtherIdOrDirIsTheSame404()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        async Task<(HttpStatusCode Status, string Body)> GetAsync(string query)
        {
            using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/answer-status?" + query);
            return (reply.StatusCode, await reply.Content.ReadAsStringAsync());
        }

        var seen = await GetAsync($"dir={DirId}&team=t1&event=e1");
        var otherEvent = await GetAsync($"dir={DirId}&team=t1&event=e2");
        var otherTeam = await GetAsync($"dir={DirId}&team=t2&event=e1");
        var otherDir = await GetAsync("dir=ffff0000&team=t1&event=e1");
        var noEvent = await GetAsync($"dir={DirId}&team=t1");

        Assert.Equal((HttpStatusCode.OK, """{"seen":true,"seen_at":5,"state":"acked"}"""), seen);
        Assert.All([otherEvent, otherTeam, otherDir, noEvent], r => Assert.Equal((HttpStatusCode.NotFound, """{"error":"not-found"}"""), r));
    }

    [Fact]
    public async Task HistoryIsTheSupportsReadForAKnownDirAndA404OtherwiseAndTheHostGuardStillApplies()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        async Task<(HttpStatusCode Status, string Body)> GetAsync(string query, string? host = null)
        {
            using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/history?" + query, host is null ? null : r => r.Headers.Host = host);
            return (reply.StatusCode, await reply.Content.ReadAsStringAsync());
        }

        Assert.Equal((HttpStatusCode.OK, $$"""{"threads":[{"id":"p1"}],"dir":"{{Dir}}"}"""), await GetAsync($"dir={DirId}&id=p1"));
        Assert.All([await GetAsync("dir=ffff0000&id=p1"), await GetAsync($"dir={DirId}&id=")], r => Assert.Equal((HttpStatusCode.NotFound, """{"error":"not-found"}"""), r));
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await GetAsync($"dir={DirId}&id=p1", "evil.example")).Status);
    }

    [Fact]
    public async Task HistoryWithNoIdIsTheListReadWhoseParametersReachTheFilterAndAMalformedOneIs400()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        async Task<(HttpStatusCode Status, string Body)> GetAsync(string query, string? host = null)
        {
            using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/history?" + query, host is null ? null : r => r.Headers.Host = host);
            return (reply.StatusCode, await reply.Content.ReadAsStringAsync());
        }

        // every parameter reaches the filter; `q` is plain text, so a pattern or a quote is passed on as given; a limit past 100 is the filter's to cap
        Assert.Equal((HttpStatusCode.OK, $$"""{"query":"50%_.*\"x|t1|1700000000000|True|True|1700000000000:p9|500","dir":"{{Dir}}"}"""),
            await GetAsync($"dir={DirId}&q=50%25_.*%22x&team=t1&since=1700000000000&state=answered&questions=1&before=1700000000000:p9&limit=500"));
        Assert.Equal((HttpStatusCode.OK, $$"""{"query":"||||False||20","dir":"{{Dir}}"}"""), await GetAsync($"dir={DirId}&q="));
        Assert.Equal((HttpStatusCode.OK, $$"""{"query":"|||False|False||20","dir":"{{Dir}}"}"""), await GetAsync($"dir={DirId}&state=unanswered&questions=false"));

        // negative: a dirId nobody knows, a path, or no dir is the same 404 as the id read, whatever else is wrong
        Assert.All(
            [await GetAsync("dir=ffff0000"), await GetAsync("dir=%2Frepo%2F.claude%2Fah2"), await GetAsync("dir=..%2F"), await GetAsync("q=x"), await GetAsync("dir=ffff0000&limit=0")],
            r => Assert.Equal((HttpStatusCode.NotFound, """{"error":"not-found"}"""), r));

        // negative: each malformed parameter, and one given twice, is a 400
        Assert.All(
            [
                await GetAsync($"dir={DirId}&since=yesterday"), await GetAsync($"dir={DirId}&since=-5"), await GetAsync($"dir={DirId}&state=all"),
                await GetAsync($"dir={DirId}&questions=yes"), await GetAsync($"dir={DirId}&before=p9"), await GetAsync($"dir={DirId}&before=x:p9"),
                await GetAsync($"dir={DirId}&limit=0"), await GetAsync($"dir={DirId}&limit=ten"), await GetAsync($"dir={DirId}&q=a&q=b"),
            ],
            r => Assert.Equal((HttpStatusCode.BadRequest, """{"error":"bad-query"}"""), r));

        // the request guard still stands in front of the list read
        Assert.Equal(HttpStatusCode.MisdirectedRequest, (await GetAsync($"dir={DirId}", "evil.example")).Status);
    }

    [Fact]
    public async Task ListReadsRunOneAtATimeAndOneWhoseClientLeftWhileWaitingNeverRuns()
    {
        var running = 0;
        var most = 0;
        var ran = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var release = new ManualResetEventSlim();
        JsonObject History(string dir, Ah.Hierarchy.QaHistoryQuery q)
        {
            if (q.Id is { } id)
            {
                return new JsonObject { ["threads"] = new JsonArray { new JsonObject { ["id"] = id } }, ["dir"] = dir };
            }

            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref most, now);
            ran.Enqueue(q.Search!);
            if (q.Search == "a")
            {
                _ = release.Wait(Until.Deadline);
            }

            _ = Interlocked.Decrement(ref running);
            return new JsonObject { ["threads"] = new JsonArray(), ["dir"] = dir, ["next"] = JsonValue.Null };
        }

        await using var rig = UiRig.Create(answer: new UiAnswerSupport(id => id == DirId ? Dir : null, static _ => new JsonObject(), null, History));
        var grant = await rig.LeaseAsync();
        string Url(string query) => $"http://127.0.0.1:{grant.Port}/ui/v1/history?{query}";

        // a holds the one slot
        var a = rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/history?dir={DirId}&q=a");
        await Until.TrueAsync(() => ran.Contains("a"));

        // the id read is not behind the slot
        using (var byId = await rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/history?dir={DirId}&id=p1"))
        {
            Assert.Equal(HttpStatusCode.OK, byId.StatusCode);
        }

        // b waits, and its client goes away before the slot frees; c waits behind it
        using var leaving = UiRig.NewClient();
        using var gone = new CancellationTokenSource();
        var b = leaving.GetAsync(Url($"dir={DirId}&q=b"), gone.Token);
        await Task.Delay(200);
        gone.Cancel();
        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        await Task.Delay(300);
        var c = rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/history?dir={DirId}&q=c");
        await Task.Delay(200);
        Assert.Equal(["a"], ran);

        release.Set();
        using var first = await a;
        using var third = await c;
        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (first.StatusCode, third.StatusCode));
        Assert.Equal(["a", "c"], ran);
        Assert.Equal(1, most);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        var seen = Volatile.Read(ref target);
        while (value > seen && Interlocked.CompareExchange(ref target, value, seen) is var was && was != seen)
        {
            seen = was;
        }
    }

    [Fact]
    public async Task AFailedEventStillAnswers200AndIsLogged()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body("evt"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("Yes", Text(body, "decision"));
        Assert.Contains("id=evt outcome=answered event=failed: unknown_address: nobody", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnwiredRouteIs503Unavailable()
    {
        var (rig, grant, _) = await OpenAsync(wired: false);
        await using var owner = rig;

        var (status, body) = await PostAsync(rig, grant, Body());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal(("unavailable", "answer not wired"), (Text(body, "error"), Text(body, "detail")));
    }

    [Fact]
    public async Task AGetToTheAnswerRouteAndAPostToTheViewRouteAre405()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        using var get = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/answer");
        var (viewStatus, _) = await PostAsync(rig, grant, Body(), "/ui/v1/view");
        var (listStatus, _) = await PostAsync(rig, grant, Body(), "/ui/v1/read-model");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
        Assert.Equal("POST", get.Content.Headers.Allow.Single());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, viewStatus);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, listStatus);
        Assert.Empty(answers.Calls);
    }

    [Fact]
    public async Task AForeignOriginIs403AndTheSupportIsNotAsked()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        using var foreign = await rig.SendAsync(grant.Port, HttpMethod.Post, "/ui/v1/answer", request =>
        {
            request.Content = UiRig.Json(Body());
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        });

        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Empty(answers.Calls);
    }

    [Fact]
    public async Task ABodyOverTheCapIs413AndTheSupportIsNotAsked()
    {
        var (rig, grant, answers) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, Body(choice: "\"choice\":\"other: " + new string('x', UiSession.MaxBodyBytes) + "\""));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Empty(answers.Calls);
    }
}
