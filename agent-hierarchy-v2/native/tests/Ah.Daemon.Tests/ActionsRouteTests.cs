#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Watch;

namespace Ah.Daemon.Tests;

/// <summary><c>GET /v1/actions</c> and <c>GET /v1/actions/{*id}</c>: the body of the view builder's own actions document, in the service and through the UiSession pipeline.</summary>
public sealed class ActionsRouteTests
{
    private const string Doc = "{\"node\":{\"kind\":\"member\",\"path\":\"/o/o.1/t/k/m/dev\"},\"actions\":[{\"id\":\"member.detail\"}]}";

    private sealed class FakeRevs : IStatusRevs
    {
        public StatusReading? Read(string dir) => null;

        public void Forget(string dir)
        {
        }
    }

    private sealed class Recording(int status, string? body) : IViewModel
    {
        public List<string> Paths { get; } = [];

        public bool UsedHerdr => false;

        public ViewAnswer Build(string path)
        {
            Paths.Add(path);
            return new ViewAnswer(status, new JsonObject { ["view_schema"] = 1, ["rev"] = "r1", ["body"] = body is null ? JsonValue.Null : JsonText.Parse(Encoding.UTF8.GetBytes(body)) });
        }
    }

    private static (ViewService Service, Recording Model) Make(int status = 200, string? body = Doc)
    {
        var model = new Recording(status, body);
        var service = new ViewService(
            new ViewServiceDependencies(null!, new FakeRevs(), WatchModule.CreateStatusDirs(), new FakeClock(), static (_, _) => Task.FromResult(new JsonArray())),
            models: (_, _, _) => model);
        return (service, model);
    }

    private static RouteRequest Request(string? id = null) =>
        new("GET", id is null ? "/v1/actions" : "/v1/actions/" + id, string.Empty, new Dictionary<string, string>(), default, id is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["id"] = id });

    [Fact]
    public async Task TheRootAsksTheBuilderForTheRootActionsAndAnswersItsBodyUnchangedNoStore()
    {
        var (service, model) = Make();

        var reply = await service.ActionsAsync(Request(), CancellationToken.None);

        Assert.Equal(200, reply.Status);
        Assert.Equal("no-store", reply.Headers["Cache-Control"]);
        Assert.Equal(["/actions"], model.Paths);
        Assert.Equal(JsonText.ToUtf8(JsonText.Parse(Encoding.UTF8.GetBytes(Doc)), 0), reply.Body.ToArray());
    }

    [Theory]
    [InlineData("member:d1/alpha/dev", "/actions/member/d1/alpha/dev")]
    [InlineData("member:d1/alpha/@orchestrator", "/actions/member/d1/alpha/%40orchestrator")]
    [InlineData("member:d1/default/qa-1.x", "/actions/member/d1/default/qa-1.x")]
    public async Task AMemberIdIsMappedToTheBuildersMemberPathAndNothingIsComposedHere(string id, string expected)
    {
        var (service, model) = Make();

        var reply = await service.ActionsAsync(Request(id), CancellationToken.None);

        Assert.Equal(200, reply.Status);
        Assert.Equal([expected], model.Paths);
    }

    [Theory]
    [InlineData("dev")]
    [InlineData("member:d1/alpha")]
    [InlineData("member:d1/alpha/dev/extra")]
    [InlineData("member:d1//dev")]
    [InlineData("member:/alpha/dev")]
    [InlineData("team:d1/alpha/dev")]
    [InlineData("Member:d1/alpha/dev")]
    public async Task AnIdThatIsNotAFullMemberIdIs400BadMemberAndAsksTheBuilderNothing(string id)
    {
        var (service, model) = Make();

        var reply = await service.ActionsAsync(Request(id), CancellationToken.None);

        Assert.Equal(400, reply.Status);
        Assert.Equal("no-store", reply.Headers["Cache-Control"]);
        Assert.Equal("{\"error\":\"bad_member\"}", Encoding.UTF8.GetString(reply.Body.Span));
        Assert.Empty(model.Paths);
    }

    [Theory]
    [InlineData(404, Doc)]
    [InlineData(200, null)]
    public async Task ANodeTheBuilderDoesNotFindOrADocumentWithNoBodyIs404NoNode(int status, string? body)
    {
        var (service, _) = Make(status, body);

        var reply = await service.ActionsAsync(Request("member:d1/alpha/ghost"), CancellationToken.None);

        Assert.Equal(404, reply.Status);
        Assert.Equal("no-store", reply.Headers["Cache-Control"]);
        Assert.Equal("{\"error\":\"no_node\"}", Encoding.UTF8.GetString(reply.Body.Span));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task AStatusOtherThanNotFoundPassesThroughAndIsNotMappedToNoNode(int status)
    {
        var (service, _) = Make(status, Doc);

        var reply = await service.ActionsAsync(Request("member:d1/alpha/dev"), CancellationToken.None);

        Assert.Equal(status, reply.Status);
        Assert.Equal("no-store", reply.Headers["Cache-Control"]);
        Assert.DoesNotContain("no_node", Encoding.UTF8.GetString(reply.Body.Span), StringComparison.Ordinal);
    }

    private static async Task<(UiRig Rig, UiGrant Grant)> OpenAsync()
    {
        var rig = UiRig.Create();
        return (rig, await rig.LeaseAsync());
    }

    [Theory]
    [InlineData("/ui/v1/actions", "/v1/actions")]
    [InlineData("/ui/v1/actions/", "/v1/actions")]
    [InlineData("/ui/v1/actions/member:d1/alpha/dev", "/v1/actions/member:d1/alpha/dev")]
    [InlineData("/ui/v1/actions/member:d1/alpha/@orchestrator", "/v1/actions/member:d1/alpha/@orchestrator")]
    public async Task TheActionsRoutesGoThroughTheUiSessionPipelineToTheDaemonRoute(string uiPath, string daemonPath)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        RouteRequest? seen = null;
        rig.View = (request, _) =>
        {
            seen = request;
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, uiPath);

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(("GET", daemonPath), (seen!.Method, seen.Path));
        Assert.DoesNotContain("authorization", seen.Headers.Keys);
    }

    [Theory]
    [InlineData("/ui/v1/actions")]
    [InlineData("/ui/v1/actions/member:d1/alpha/dev")]
    public async Task TheActionsRoutesRefuseAForeignOriginAForeignHostAndAWrongMethod(string path)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var calls = 0;
        rig.View = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var foreignOrigin = await rig.SendAsync(grant.Port, HttpMethod.Get, path, request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        });
        using var foreignHost = await rig.SendAsync(grant.Port, HttpMethod.Get, path, request =>
        {
            request.Headers.Host = "evil.example";
        });
        using var post = await rig.SendAsync(grant.Port, HttpMethod.Post, path);

        Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, foreignHost.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, post.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task APathThatOnlyStartsLikeTheActionsRouteIsNotIt()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var calls = 0;
        rig.View = (_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/actionsx");

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        Assert.Equal(0, calls);
    }
}
