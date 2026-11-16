#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Daemon.Tests;

/// <summary>The read model's two routes: <c>GET /v1/view/{name}</c> and the <c>sub</c> form of the events long-poll, in the service and through the UiSession pipeline.</summary>
public sealed class ReadModelRouteTests
{
    private sealed class FakeRevs : IStatusRevs
    {
        public StatusReading? Read(string dir) => null;

        public void Forget(string dir)
        {
        }
    }

    private sealed class FakeModel : IReadModel
    {
        public string Epoch => "e-1";

        public List<(long Since, string? Epoch, string[] Sub, TimeSpan Wait)> Polls { get; } = [];

        public List<string> Manifests { get; } = [];

        public void Emit(string lane, string key)
        {
        }

        public void EmitKeySet(string lane)
        {
        }

        public byte[]? Manifest(string view)
        {
            Manifests.Add(view);
            return view == "members" ? Encoding.UTF8.GetBytes("{\"epoch\":\"e-1\",\"seq\":3,\"slices\":[],\"pending\":[]}") : null;
        }

        public List<string[]> Fresh { get; } = [];

        public List<(string SliceId, Dictionary<string, string> Query)> Lists { get; } = [];

        public Task<(int Status, byte[] Body)> ListAsync(string sliceId, IReadOnlyDictionary<string, string> query, CancellationToken ct)
        {
            Lists.Add((sliceId, new Dictionary<string, string>(query)));
            return Task.FromResult((200, Encoding.UTF8.GetBytes("{\"rows\":[]}")));
        }

        public Task<byte[]> PollAsync(long since, string? epoch, IReadOnlyCollection<string>? subscribed, TimeSpan wait, CancellationToken ct, IReadOnlyCollection<string>? fresh = null)
        {
            Polls.Add((since, epoch, [.. subscribed ?? []], wait));
            Fresh.Add([.. fresh ?? []]);
            return Task.FromResult(Encoding.UTF8.GetBytes("{\"epoch\":\"e-1\",\"seq\":3,\"resync\":false,\"slices\":[],\"removed\":[]}"));
        }

        public void Dispose()
        {
        }
    }

    private static (ViewService Service, FakeModel Model, int[] Builds) Make(bool withModel = true)
    {
        var model = new FakeModel();
        var builds = new int[1];
        var service = new ViewService(
            new ViewServiceDependencies(
                null!,
                new FakeRevs(),
                WatchModule.CreateStatusDirs(),
                new FakeClock(),
                static (_, _) => Task.FromResult(new JsonArray()),
                BuildLog: () => Interlocked.Increment(ref builds[0]),
                Model: withModel ? model : null),
            models: (_, _, _) => throw new InvalidOperationException("a model of the hierarchy was asked for"));
        return (service, model, builds);
    }

    private static RouteRequest Request(string path, string query = "", string? name = null) =>
        new("GET", path, query, new Dictionary<string, string>(), default, name is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["name"] = name });

    private static ViewService WithStats(Func<JsonObject?>? stats) =>
        new(
            new ViewServiceDependencies(null!, new FakeRevs(), WatchModule.CreateStatusDirs(), new FakeClock(), static (_, _) => Task.FromResult(new JsonArray()), ReadModelStats: stats),
            models: (_, _, _) => throw new InvalidOperationException("a model of the hierarchy was asked for"));

    [Fact]
    public async Task TheReadModelRouteReturnsTheCountersTheParkedListAndAnAsOfComputedAtTheRequest()
    {
        var drift = 1;
        var service = WithStats(() => new JsonObject { ["drift"] = drift, ["coalesce_ms"] = 100, ["parked"] = JsonValue.FromArray(new JsonArray { "slice-a" }) });

        var first = JsonText.Parse(Encoding.UTF8.GetString((await service.ReadModelAsync(Request("/v1/read-model"), CancellationToken.None)).Body.Span));
        drift = 5;
        var reply = await service.ReadModelAsync(Request("/v1/read-model"), CancellationToken.None);
        var second = JsonText.Parse(Encoding.UTF8.GetString(reply.Body.Span));

        Assert.Equal(200, reply.Status);
        Assert.Equal(["as_of", "counters", "parked"], second.AsObject().Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, (int)first.Member("counters")!.Value.Member("drift")!.Value.AsNumber());
        Assert.Equal(5, (int)second.Member("counters")!.Value.Member("drift")!.Value.AsNumber());
        Assert.Equal(100, (int)second.Member("counters")!.Value.Member("coalesce_ms")!.Value.AsNumber());
        Assert.False(second.Member("counters")!.Value.AsObject().ContainsKey("parked"));
        Assert.Equal("slice-a", second.Member("parked")!.Value.AsArray()[0].AsString());
        Assert.True(second.Member("as_of")!.Value.IsNumber);
    }

    [Fact]
    public async Task TheReadModelRouteIs404NoReadModelWithoutAModelOrWhenTheStatsAreNull()
    {
        foreach (var service in new[] { WithStats(null), WithStats(() => null) })
        {
            var reply = await service.ReadModelAsync(Request("/v1/read-model"), CancellationToken.None);

            Assert.Equal(404, reply.Status);
            Assert.Equal("{\"error\":\"no_read_model\"}", Encoding.UTF8.GetString(reply.Body.Span));
        }
    }

    [Fact]
    public async Task AKnownViewNameServesTheManifestAndAnUnknownOneIs404WithNoBuild()
    {
        var (service, model, builds) = Make();

        var known = await service.ManifestAsync(Request("/v1/view/members", name: "members"), CancellationToken.None);
        var unknown = await service.ManifestAsync(Request("/v1/view/nope", name: "nope"), CancellationToken.None);

        Assert.Equal(200, known.Status);
        Assert.Contains("\"epoch\":\"e-1\"", Encoding.UTF8.GetString(known.Body.Span), StringComparison.Ordinal);
        Assert.Equal(404, unknown.Status);
        Assert.Equal(["members", "nope"], model.Manifests);
        Assert.Equal(0, builds[0]);
    }

    [Fact]
    public async Task WithoutAReadModelTheManifestRouteIs404()
    {
        var (service, _, _) = Make(withModel: false);

        var reply = await service.ManifestAsync(Request("/v1/view/members", name: "members"), CancellationToken.None);

        Assert.Equal(404, reply.Status);
    }

    [Fact]
    public async Task TheSubFormOfTheEventsPollAsksTheModelWithSinceEpochAndTheSubscriptionAndStartsNoBuild()
    {
        var (service, model, builds) = Make();

        var reply = await service.ViewAsync(Request("/v1/view", "path=/events&since=41&epoch=e-1&sub=view:members,activity:ab12cd34/s1&wait=0"), CancellationToken.None);

        Assert.Equal(200, reply.Status);
        var poll = Assert.Single(model.Polls);
        Assert.Equal((41L, "e-1", TimeSpan.Zero), (poll.Since, poll.Epoch, poll.Wait));
        Assert.Equal(["view:members", "activity:ab12cd34/s1"], poll.Sub);
        Assert.Equal(0, builds[0]);
    }

    [Fact]
    public async Task FreshStarStandsForEveryExactSliceIdOfTheSubscriptionAndNothingElse()
    {
        var (service, model, _) = Make();

        _ = await service.ViewAsync(Request("/v1/view", "path=/events&sub=view:summary,team:d/t,member:d/t/a,list:open:team:d/t,activity:d/s1&fresh=*&wait=0"), CancellationToken.None);

        Assert.Equal(["team:d/t", "member:d/t/a", "activity:d/s1"], Assert.Single(model.Fresh));
    }

    [Fact]
    public async Task FreshStarKeepsTheExplicitIdsBesideItAndRepeatsNone()
    {
        var (service, model, _) = Make();

        _ = await service.ViewAsync(Request("/v1/view", "path=/events&sub=view:summary,team:d/t,list:open:team:d/t&fresh=*,list:open:team:d/t,team:d/t&wait=0"), CancellationToken.None);

        Assert.Equal(["list:open:team:d/t", "team:d/t"], Assert.Single(model.Fresh));
    }

    [Fact]
    public async Task AnExplicitFreshListIsPassedAsItIs()
    {
        var (service, model, _) = Make();

        _ = await service.ViewAsync(Request("/v1/view", "path=/events&sub=view:summary,team:d/t,member:d/t/a&fresh=member:d/t/a&wait=0"), CancellationToken.None);

        Assert.Equal(["member:d/t/a"], Assert.Single(model.Fresh));
    }

    [Fact]
    public async Task ASubPollWaitsForAsLongAsTheClientAsksAndNeedsNoCursor()
    {
        var (service, model, _) = Make();

        _ = await service.ViewAsync(Request("/v1/view", "path=/events&sub=view:members&wait=7"), CancellationToken.None);

        var poll = Assert.Single(model.Polls);
        Assert.Equal((0L, (string?)null, TimeSpan.FromSeconds(7)), (poll.Since, poll.Epoch, poll.Wait));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("abc")]
    [InlineData("41.7")]
    public async Task ACursorThatIsNotAPublishCounterIs400AndAsksNothing(string since)
    {
        var (service, model, _) = Make();

        var reply = await service.ViewAsync(Request("/v1/view", "path=/events&sub=view:members&since=" + since), CancellationToken.None);

        Assert.Equal(400, reply.Status);
        Assert.Empty(model.Polls);
    }

    [Fact]
    public async Task WithNoSubTheEventsPollKeepsTodaysBehaviourAndNeverTouchesTheModel()
    {
        var (service, model, _) = Make();

        var reply = await service.ViewAsync(Request("/v1/view", "path=/events&since=garbage"), CancellationToken.None);

        Assert.Equal(400, reply.Status); // today's cursor check, not the slice form's
        Assert.Contains("cursor", Encoding.UTF8.GetString(reply.Body.Span), StringComparison.Ordinal);
        Assert.Empty(model.Polls);
    }

    [Fact]
    public async Task WithoutAReadModelASubIsIgnoredAndTheOldEventsPathAnswers()
    {
        var (service, _, _) = Make(withModel: false);

        var reply = await service.ViewAsync(Request("/v1/view", "path=/events&since=garbage&sub=view:members"), CancellationToken.None);

        Assert.Equal(400, reply.Status);
        Assert.Contains("cursor", Encoding.UTF8.GetString(reply.Body.Span), StringComparison.Ordinal);
    }

    private static RouteRequest ListRequest(string id, string query = "") =>
        new("GET", "/v1/list/" + id, query, new Dictionary<string, string>(), default, new Dictionary<string, string> { ["id"] = id });

    [Theory]
    [InlineData("limit=5&limit=6", "limit")]
    [InlineData("member=a&limit=5&member=b", "member")]
    [InlineData("before=c&before=d", "before")]
    [InlineData("%6Cimit=5&limit=6", "limit")]
    public async Task ARepeatedQueryKeyIs400BadFilterNamingTheKeyAndAsksTheModelNothing(string query, string key)
    {
        var (service, model, _) = Make();

        var reply = await service.ListAsync(ListRequest("list:transcript:d1/alice", query), CancellationToken.None);

        Assert.Equal(400, reply.Status);
        Assert.Equal("no-store", reply.Headers["Cache-Control"]);
        Assert.Equal("{\"error\":\"bad_filter\",\"key\":\"" + key + "\"}", Encoding.UTF8.GetString(reply.Body.Span));
        Assert.Empty(model.Lists);
    }

    [Fact]
    public async Task DistinctKeysAndAnEmptyQueryAreNotRepeats()
    {
        var (service, model, _) = Make();

        _ = await service.ListAsync(ListRequest("list:transcript:d1/alice", "limit=5&before=c&member=m"), CancellationToken.None);
        _ = await service.ListAsync(ListRequest("list:transcript:d1/alice"), CancellationToken.None);

        Assert.Equal(2, model.Lists.Count);
    }

    [Fact]
    public async Task TheListRouteAsksTheModelForTheIdAndTheQueryAndAnswersNoStore()
    {
        var (service, model, builds) = Make();

        var reply = await service.ListAsync(ListRequest("list:transcript:d1/alice", "limit=5&before=c"), CancellationToken.None);

        Assert.Equal(200, reply.Status);
        Assert.Equal("no-store", reply.Headers["Cache-Control"]);
        var asked = Assert.Single(model.Lists);
        Assert.Equal("list:transcript:d1/alice", asked.SliceId);
        Assert.Equal(new Dictionary<string, string> { ["limit"] = "5", ["before"] = "c" }, asked.Query);
        Assert.Equal(0, builds[0]);
    }

    [Fact]
    public async Task TheListRouteIs404WithoutAReadModelOrWithoutAnId()
    {
        var (withoutModel, _, _) = Make(withModel: false);
        var (service, model, _) = Make();

        var none = await withoutModel.ListAsync(ListRequest("list:transcript:d1/alice"), CancellationToken.None);
        var noId = await service.ListAsync(Request("/v1/list/"), CancellationToken.None);

        Assert.Equal(404, none.Status);
        Assert.Equal(404, noId.Status);
        Assert.Empty(model.Lists);
    }

    [Fact]
    public async Task TheListRouteGoesThroughTheUiSessionPipelineWithTheRestOfThePathAsTheId()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        RouteRequest? seen = null;
        rig.View = (request, _) =>
        {
            seen = request;
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/list/list:transcript:d1/alice?limit=5");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(("GET", "/v1/list/list:transcript:d1/alice", "limit=5"), (seen!.Method, seen.Path, seen.Query));
        Assert.DoesNotContain("authorization", seen.Headers.Keys);
    }

    [Fact]
    public async Task TheListRouteRefusesAForeignOriginAForeignHostAndAWrongMethod()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var calls = 0;
        rig.View = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };
        const string Path = "/ui/v1/list/list:exchanges:team:d1/alpha";

        using var foreignOrigin = await rig.SendAsync(grant.Port, HttpMethod.Get, Path, request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        });
        using var foreignHost = await rig.SendAsync(grant.Port, HttpMethod.Get, Path, request =>
        {
            request.Headers.Host = "evil.example";
        });
        using var post = await rig.SendAsync(grant.Port, HttpMethod.Post, Path);

        Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, foreignHost.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("/ui/v1/list/")]
    [InlineData("/ui/v1/list")]
    [InlineData("/ui/v1/listx/a")]
    public async Task APathWithNoListIdIsNotTheListRoute(string path)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var calls = 0;
        rig.View = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        Assert.Equal(0, calls);
    }

    private static async Task<(UiRig Rig, UiGrant Grant)> OpenAsync()
    {
        var rig = UiRig.Create();
        return (rig, await rig.LeaseAsync());
    }

    [Fact]
    public async Task TheManifestRouteGoesThroughTheUiSessionPipelineWithTheViewNameAsAPathParameter()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        RouteRequest? seen = null;
        rig.View = (request, _) =>
        {
            seen = request;
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view/members?x=1");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(("GET", "/v1/view/members", "x=1"), (seen!.Method, seen.Path, seen.Query));
        Assert.Equal("members", seen.PathParameters["name"]);
        Assert.DoesNotContain("authorization", seen.Headers.Keys);
    }

    [Fact]
    public async Task TheManifestRouteRefusesAForeignOriginAForeignHostAndAWrongMethod()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var calls = 0;
        rig.View = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var foreignOrigin = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view/members", request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        });
        using var foreignHost = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view/members", request =>
        {
            request.Headers.Host = "evil.example";
        });
        using var post = await rig.SendAsync(grant.Port, HttpMethod.Post, "/ui/v1/view/members");

        Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, foreignHost.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("/ui/v1/view/")]
    [InlineData("/ui/v1/view/a/b")]
    [InlineData("/ui/v1/view/bad%20name")]
    [InlineData("/ui/v1/view/..")]
    [InlineData("/ui/v1/viewmembers")]
    public async Task APathThatIsNotOneViewNameIsNotTheManifestRoute(string path)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        RouteRequest? seen = null;
        rig.View = (request, _) =>
        {
            seen = request;
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, path);

        Assert.True(seen is null || seen.PathParameters.Count == 0, "no view name was taken from " + path);
        Assert.NotEqual("members", seen?.PathParameters.GetValueOrDefault("name"));
    }
}
