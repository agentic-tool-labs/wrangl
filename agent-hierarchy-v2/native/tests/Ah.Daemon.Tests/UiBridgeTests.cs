#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary>The loopback bridge against a real listener on an ephemeral port: the request checks, the auth exchange and the stop sequence.</summary>
public sealed class UiBridgeTests
{
    private static readonly string[] SecurityHeaders =
    [
        "Content-Security-Policy", "X-Frame-Options", "Referrer-Policy", "Cache-Control", "X-Content-Type-Options",
    ];

    private static async Task<string> BodyAsync(HttpResponseMessage reply) => await reply.Content.ReadAsStringAsync();

    private static async Task<(UiRig Rig, UiGrant Grant)> OpenAsync()
    {
        var rig = UiRig.Create();
        return (rig, await rig.LeaseAsync());
    }

    private static string HostWithPort(string template, int port) => template.Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);

    // ---------------------------------------------------------------- host, origin, fetch metadata

    [Theory]
    [InlineData("evil.example")]
    [InlineData("localhost:1")]
    [InlineData("localhost.:{port}")]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1.:{port}")]
    [InlineData("127.0.0.1:1")]
    [InlineData("127.0.0.2:{port}")]
    [InlineData("[::1]:1")]
    [InlineData("[::2]:{port}")]
    public async Task ARebindingHostIsRefusedWith421AndNoViewBytes(string host)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.Host = HostWithPort(host, grant.Port);
        });

        Assert.Equal(HttpStatusCode.MisdirectedRequest, reply.StatusCode);
        Assert.DoesNotContain(UiRig.ViewMarker, await BodyAsync(reply), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheExactHostIsServed()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.Host = $"127.0.0.1:{grant.Port}";
        });

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains(UiRig.ViewMarker, await BodyAsync(reply), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://evil.example")]
    [InlineData("http://127.0.0.1:1")]
    [InlineData("null")]
    [InlineData("http://127.0.0.1:{port}/")]
    [InlineData("HTTP://127.0.0.1:{port}")]
    [InlineData("https://127.0.0.1:{port}")]
    [InlineData("http://localhost:1")]
    [InlineData("http://localhost.:{port}")]
    public async Task AForeignOriginIsRefusedWith403(string origin)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", HostWithPort(origin, grant.Port));
        });

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.DoesNotContain(UiRig.ViewMarker, await BodyAsync(reply), StringComparison.Ordinal);
    }

    [Fact]
    public async Task APlainPageLoadAndAViewReadWithNoCookieAreServed()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var page = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/", request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none"));
        using var view = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal(HttpStatusCode.OK, view.StatusCode);
        Assert.False(page.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task APageLoadFromAnotherSiteIsRefused()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var page = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/", request => request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
    }

    [Fact]
    public async Task TheLocalhostNameWithTheListenersPortIsServedAsHostAndAsOrigin()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.Host = $"localhost:{grant.Port}";
            request.Headers.TryAddWithoutValidation("Origin", $"http://localhost:{grant.Port}");
        });

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }

    [Fact]
    public async Task TheListenersOwnOriginIsServed()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", $"http://127.0.0.1:{grant.Port}");
        });

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }

    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("")]
    public async Task ACrossSiteFetchIsRefusedWith403(string site)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
        });

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
    }

    [Theory]
    [InlineData("same-origin")]
    [InlineData("none")]
    public async Task ASameOriginOrUserInitiatedFetchIsServed(string site)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
        });

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
    }

    [Fact]
    public async Task ANoCorsDriveByYieldsNoViewBytes()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
        });

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.DoesNotContain(UiRig.ViewMarker, await BodyAsync(reply), StringComparison.Ordinal);
        Assert.DoesNotContain(reply.Headers, static h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    // ---------------------------------------------------------------- routes

    [Fact]
    public void TheTcpRouteTableIsExactlyTheSpecifiedRoutes() =>
        Assert.Equal(["GET /ui/", "GET /ui/{file}", "POST /ui/v1/answer", "GET /ui/v1/answer-status", "GET /ui/v1/history", "POST /ui/v1/team-close", "POST /ui/v1/team-rename", "GET /ui/v1/rules", "POST /ui/v1/rules", "GET /ui/v1/looks", "POST /ui/v1/looks", "POST /ui/v1/looks/merge", "GET /ui/v1/view", "GET /ui/v1/view/{name}", "GET /ui/v1/read-model", "GET /ui/v1/list/{*id}", "GET /ui/v1/actions", "GET /ui/v1/actions/{*id}", "GET /ui/v1/evidence/{id}/{ek}"], UiSession.RouteTable);

    [Theory]
    [InlineData("/ui/v1/status")]
    [InlineData("/v1/status")]
    [InlineData("/v1/pane")]
    [InlineData("/v1/inspect/timers")]
    [InlineData("/v1/inspect/events")]
    [InlineData("/v1/view")]
    [InlineData("/v1/view/")]
    [InlineData("/v1/health")]
    [InlineData("/v1/ui/lease")]
    [InlineData("/ui/v1/events")]
    [InlineData("/ui/v1/view/graph/extra")]
    [InlineData("/ui")]
    [InlineData("/")]
    public async Task APathOutsideTheTableIs404(string path)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, path);

        Assert.Equal(HttpStatusCode.NotFound, reply.StatusCode);
        Assert.DoesNotContain(UiRig.ViewMarker, await BodyAsync(reply), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/ui/v1/view")]
    [InlineData("/ui/v1/view/")]
    public async Task TheViewRouteReachesTheSharedHandler(string path)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, path + "?path=/");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains(UiRig.ViewMarker, await BodyAsync(reply), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheReadModelRouteReachesTheSharedHandlerAsTheDaemonsRoute()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        RouteRequest? seen = null;
        rig.View = (request, _) =>
        {
            seen = request;
            return Task.FromResult(UiRig.JsonResponse(200, "{\"counters\":{}}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/read-model");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(("GET", "/v1/read-model"), (seen!.Method, seen.Path));
        Assert.DoesNotContain(seen.Headers.Keys, static k => k.Equals("authorization", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheReadModelRouteTakesOnlyGet()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var calls = 0;
        rig.View = (_, _) =>
        {
            calls++;
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var posted = await rig.SendAsync(grant.Port, HttpMethod.Post, "/ui/v1/read-model");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, posted.StatusCode);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task TheViewHandlerIsGivenTheQueryAndNoAuthorizationHeader()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        RouteRequest? seen = null;
        rig.View = (request, _) =>
        {
            seen = request;
            return Task.FromResult(UiRig.JsonResponse(200, "{}"));
        };

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/graph/x&wait=0");

        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Equal(("GET", "/v1/view", "path=/graph/x&wait=0"), (seen!.Method, seen.Path, seen.Query));
        Assert.DoesNotContain("authorization", seen.Headers.Keys);
        Assert.Null(seen.PeerPid);
    }

    [Theory]
    [InlineData("OPTIONS")]
    [InlineData("HEAD")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("POST")]
    public async Task OnlyTheSpecifiedMethodIsAllowedOnTheViewRoute(string method)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, new HttpMethod(method), "/ui/v1/view?path=/");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, reply.StatusCode);
    }

    [Fact]
    public async Task APreflightGets405AndNoAccessControlHeaders()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Options, "/ui/v1/view", request =>
        {
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization");
        });

        Assert.Equal(HttpStatusCode.MethodNotAllowed, reply.StatusCode);
        Assert.DoesNotContain(reply.Headers.Concat(reply.Content.Headers), static h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AForeignOriginPreflightIsRefusedAndStillGetsNoAccessControlHeaders()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Options, "/ui/v1/view", request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
            request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "authorization");
        });

        Assert.Equal(HttpStatusCode.Forbidden, reply.StatusCode);
        Assert.DoesNotContain(reply.Headers, static h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NoResponseCarriesAnAccessControlHeader()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        foreach (var (method, path, tweak) in new (HttpMethod, string, Action<HttpRequestMessage>?)[]
        {
            (HttpMethod.Get, "/ui/", null),
            (HttpMethod.Get, "/ui/v1/view?path=/", null),
            (HttpMethod.Get, "/nope", null),
        })
        {
            using var reply = await rig.SendAsync(grant.Port, method, path, tweak);
            Assert.DoesNotContain(reply.Headers.Concat(reply.Content.Headers), static h => h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static StringContent WithType(string body, string? mediaType)
    {
        var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = mediaType is null ? null : MediaTypeHeaderValue.Parse(mediaType);
        return content;
    }

    // ---------------------------------------------------------------- static files

    [Fact]
    public async Task ThePageAndItsAllowedFilesAreServed()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        using var root = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/");
        using var index = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/index.html");
        using var script = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/ui.js");

        Assert.Contains(UiRig.PageMarker, await BodyAsync(root), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, index.StatusCode);
        Assert.Equal("text/javascript; charset=utf-8", script.Content.Headers.ContentType?.ToString());
    }

    [Theory]
    [InlineData("GET /ui/../ui.json HTTP/1.1")]
    [InlineData("GET /ui/%2e%2e/ui.json HTTP/1.1")]
    [InlineData("GET /ui/%2E%2E/secret.html HTTP/1.1")]
    [InlineData("GET /ui/..%2fsecret.html HTTP/1.1")]
    [InlineData("GET /ui/..%5csecret.html HTTP/1.1")]
    [InlineData("GET /ui/../../secret.html HTTP/1.1")]
    [InlineData("GET /ui/x.json HTTP/1.1")]
    [InlineData("GET /ui/data.json HTTP/1.1")]
    [InlineData("GET /ui//secret.html HTTP/1.1")]
    [InlineData("GET /ui/missing.html HTTP/1.1")]
    public async Task APathThatEscapesTheDirectoryOrNamesADisallowedFileIs404(string requestLine)
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var text = await UiRig.RawAsync(grant.Port, $"{requestLine}\r\nHost: 127.0.0.1:{grant.Port}\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 404", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADotSegmentThatStaysInsideTheDirectoryIsNormalisedByTheServer()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var text = await UiRig.RawAsync(grant.Port, $"GET /ui/index.html/.. HTTP/1.1\r\nHost: 127.0.0.1:{grant.Port}\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200", text, StringComparison.Ordinal);
        Assert.Contains(UiRig.PageMarker, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANulInThePathIsRefusedBeforeTheHandlerSeesIt()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var text = await UiRig.RawAsync(grant.Port, $"GET /ui/%00.html HTTP/1.1\r\nHost: 127.0.0.1:{grant.Port}\r\nConnection: close\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 4", text, StringComparison.Ordinal);
        Assert.DoesNotContain(UiRig.PageMarker, text, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- headers

    [Fact]
    public async Task EveryResponseCarriesTheSecurityHeadersAndNoServerHeader()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var requests = new (HttpMethod, string, Action<HttpRequestMessage>?)[]
        {
            (HttpMethod.Get, "/ui/", null),
            (HttpMethod.Get, "/ui/v1/view?path=/", null),
            (HttpMethod.Get, "/nope", null),
            (HttpMethod.Options, "/ui/v1/view", null),
            (HttpMethod.Post, "/ui/v1/looks/merge", request => request.Content = WithType("{}", "text/plain")),
            (HttpMethod.Get, "/ui/v1/view?path=/", request => request.Headers.Host = "evil.example"),
            (HttpMethod.Get, "/ui/v1/view?path=/", request => request.Headers.TryAddWithoutValidation("Origin", "null")),
        };

        foreach (var (method, path, tweak) in requests)
        {
            using var reply = await rig.SendAsync(grant.Port, method, path, tweak);
            foreach (var name in SecurityHeaders)
            {
                Assert.True(reply.Headers.Contains(name) || reply.Content.Headers.Contains(name), $"{method} {path}: {name} missing");
            }

            Assert.False(reply.Headers.Contains("Server"), $"{method} {path}: Server header present");
            Assert.Equal("no-store", reply.Headers.CacheControl?.ToString());
            Assert.Equal("default-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'", reply.Headers.GetValues("Content-Security-Policy").Single());
        }
    }

    [Fact]
    public async Task ThePlaceholderPageHasNoInlineScriptOrStyle()
    {
        var page = await File.ReadAllTextAsync(Path.Combine(PluginRoot(), "web", "ui", "index.html"));
        var normalised = page.ToLowerInvariant();

        Assert.DoesNotContain("<style", normalised, StringComparison.Ordinal);
        Assert.DoesNotContain("style=", normalised, StringComparison.Ordinal);
        Assert.DoesNotContain("onclick", normalised, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript:", normalised, StringComparison.Ordinal);
        Assert.All(
            System.Text.RegularExpressions.Regex.Matches(normalised, "<script[^>]*>").Select(static m => m.Value),
            static tag => Assert.Contains(" src=", tag, StringComparison.Ordinal));
    }

    private static string PluginRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "web", "ui", "index.html")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("web/ui/index.html was not found above the test binaries");
    }

    // ---------------------------------------------------------------- logging

    [Fact]
    public async Task ARejectedHostAndOriginAreLoggedWithTheirRouteAndStatus()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;

        using var host = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request => request.Headers.Host = "evil.example");
        using var origin = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request =>
        {
            request.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        });

        Assert.Contains("ui request rejected: route=/ui/v1/view status=421", rig.LogText, StringComparison.Ordinal);
        Assert.Contains("ui request rejected: route=/ui/v1/view status=403", rig.LogText, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- bind and lifecycle

    [Fact]
    public async Task TheListenerIsBoundToTheIpv4LoopbackAndNothingElse()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        var listeners = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == grant.Port).ToList();

        Assert.Single(listeners);
        Assert.Equal(IPAddress.Loopback, listeners[0].Address);
        Assert.Equal(grant.Port, rig.Bridge.Port);
    }

    [Fact]
    public async Task NothingListensAndNoInfoFileExistsBeforeTheFirstLease()
    {
        await using var rig = UiRig.Create();

        Assert.False(rig.Bridge.Active);
        Assert.Equal(0, rig.Bridge.Port);
        Assert.False(File.Exists(rig.InfoPath));
    }

    [Fact]
    public async Task TheInfoFileNamesThePortAndTheFirstLeasePidAndIsOwnerOnly()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync(111);
        _ = await rig.LeaseAsync(222);

        var doc = JsonText.Parse(await File.ReadAllTextAsync(rig.InfoPath)).AsObject();

        Assert.Equal(1, (int)doc["v"].AsNumber());
        Assert.Equal(first.Port, (int)doc["port"].AsNumber());
        Assert.Equal(111, (int)doc["lease_pid"].AsNumber());
        Assert.Contains("T", doc.Str("started"), StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(rig.InfoPath));
    }

    [Fact]
    public async Task WhenTheFirstLeaseDropsTheInfoFileNamesTheNextLeasesPidAndTheListenerStays()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync(111);
        var second = await rig.LeaseAsync(222);

        await rig.Bridge.DropLeaseAsync(first.Lease);

        var doc = JsonText.Parse(await File.ReadAllTextAsync(rig.InfoPath)).AsObject();
        Assert.Equal(222, (int)doc["lease_pid"].AsNumber());
        Assert.True(UiRig.Listening(second.Port));
        Assert.Equal(1, rig.Bridge.Leases);
    }

    [Fact]
    public async Task TheLastLeaseDroppingClosesThePortAndRemovesTheInfoFile()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        await rig.Bridge.DropLeaseAsync(grant.Lease);

        await Until.TrueAsync(() => !UiRig.Listening(grant.Port), 2_000, () => $"port {grant.Port} still listening 2 s after the drop");
        Assert.True(await UiRig.Refuses(grant.Port), $"port {grant.Port} accepted a connection after the drop");
        Assert.False(File.Exists(rig.InfoPath));
        Assert.False(rig.Bridge.Active);
        Assert.True(grant.Lease.Ended.Task.IsCompleted);
    }

    [Fact]
    public async Task DroppingALeaseTwiceIsHarmless()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync();
        var second = await rig.LeaseAsync();

        await rig.Bridge.DropLeaseAsync(first.Lease);
        await rig.Bridge.DropLeaseAsync(first.Lease);

        Assert.True(UiRig.Listening(second.Port));
        Assert.Equal(1, rig.Bridge.Leases);
    }

    [Fact]
    public async Task TheKillSwitchRefusesEveryLeaseAndBindsNothing()
    {
        await using var rig = UiRig.Create();
        rig.Disabled = true;

        await Assert.ThrowsAsync<UiDisabledException>(() => rig.LeaseAsync());

        Assert.False(rig.Bridge.Active);
        Assert.False(File.Exists(rig.InfoPath));
    }

    [Fact]
    public async Task AStoppedBridgeThatIsToldToRefuseTakesNoLease()
    {
        await using var rig = UiRig.Create();
        _ = await rig.LeaseAsync();

        await rig.Bridge.StopAsync("daemon stop", refuseNewLeases: true);

        await Assert.ThrowsAsync<UiClosedException>(() => rig.LeaseAsync());
        Assert.False(File.Exists(rig.InfoPath));
    }

    // ---------------------------------------------------------------- idle

    [Fact]
    public async Task AListenerWithNoApiRequestForFifteenMinutesClosesAndApiRequestsKeepItOpen()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        rig.Clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));
        await rig.Bridge.CheckIdleAsync();
        Assert.True(rig.Bridge.Active);

        using (var seen = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/"))
        {
            Assert.Equal(HttpStatusCode.OK, seen.StatusCode);
        }

        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await rig.Bridge.CheckIdleAsync();
        Assert.True(rig.Bridge.Active);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.Bridge.CheckIdleAsync();

        Assert.False(rig.Bridge.Active);
        Assert.True(await UiRig.Refuses(grant.Port));
        Assert.False(File.Exists(rig.InfoPath));
        Assert.True(grant.Lease.Ended.Task.IsCompleted);
        Assert.Contains("bridge close (idle)", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AServiceLeaseKeepsTheListenerOpenPastTheIdleLimitUntilItIsDropped()
    {
        await using var rig = UiRig.Create();
        var service = await rig.Bridge.OpenLeaseAsync(31337, default, service: true);

        rig.Clock.Advance(TimeSpan.FromMinutes(30));
        await rig.Bridge.CheckIdleAsync();
        Assert.True(rig.Bridge.Active);

        await rig.Bridge.DropLeaseAsync(service.Lease);
        Assert.False(rig.Bridge.Active);
    }

    [Fact]
    public async Task OnceTheServiceLeaseIsGoneAnOrdinaryLeaseIsIdledOutAsBefore()
    {
        await using var rig = UiRig.Create();
        _ = await rig.LeaseAsync();
        var service = await rig.Bridge.OpenLeaseAsync(31338, default, service: true);

        rig.Clock.Advance(TimeSpan.FromMinutes(30));
        await rig.Bridge.CheckIdleAsync();
        Assert.True(rig.Bridge.Active);

        await rig.Bridge.DropLeaseAsync(service.Lease);
        await rig.Bridge.CheckIdleAsync();

        Assert.False(rig.Bridge.Active);
        Assert.Contains("bridge close (idle)", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusedRequestsAndPageLoadsDoNotKeepTheListenerOpen()
    {
        await using var rig = UiRig.Create();
        var grant = await rig.LeaseAsync();

        rig.Clock.Advance(TimeSpan.FromMinutes(10));
        using (var foreign = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/view?path=/", request => request.Headers.TryAddWithoutValidation("Origin", "http://evil.example")))
        {
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        }

        using (var page = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/"))
        {
            Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        }

        rig.Clock.Advance(TimeSpan.FromMinutes(5));
        await rig.Bridge.CheckIdleAsync();

        Assert.False(rig.Bridge.Active);
    }

    [Fact]
    public async Task TheFifteenMinutesBeforeTheFirstExchangeRunFromTheBind()
    {
        await using var rig = UiRig.Create();
        _ = await rig.LeaseAsync();

        rig.Clock.Advance(TimeSpan.FromMinutes(14));
        await rig.Bridge.CheckIdleAsync();
        Assert.True(rig.Bridge.Active);

        rig.Clock.Advance(TimeSpan.FromMinutes(1));
        await rig.Bridge.CheckIdleAsync();
        Assert.False(rig.Bridge.Active);
    }

    // ---------------------------------------------------------------- stop sequence

    [Fact]
    public async Task ADropThatCannotRewriteTheInfoFileLogsItAndLeavesTheOtherLeaseServing()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync(101);
        var second = await rig.LeaseAsync(102);
        rig.FailWrites = true;

        await rig.Bridge.DropLeaseAsync(first.Lease);

        Assert.Contains("bridge info file not rewritten: IOException", rig.LogText, StringComparison.Ordinal);
        Assert.True(UiRig.Listening(second.Port));
    }

    [Fact]
    public async Task ADropThatRewritesTheInfoFileLogsNothingAboutIt()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync(101);
        _ = await rig.LeaseAsync(102);

        await rig.Bridge.DropLeaseAsync(first.Lease);

        Assert.DoesNotContain("not rewritten", rig.LogText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeldLongPollGets503WhenTheLastLeaseDropsAndThePortIsFreeBeforeThePollWouldEnd()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        rig.View = (request, cancellation) =>
        {
            _ = entered.TrySetResult();
            return UiRig.HoldUntilCancelled(request, cancellation);
        };
        const int pollWaitSeconds = 25;
        var poll = rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/view?path=/events&since=1.0&wait={pollWaitSeconds}");
        await entered.Task.WaitAsync(Until.Deadline);
        var watch = Stopwatch.StartNew();

        await rig.Bridge.DropLeaseAsync(grant.Lease);

        using var reply = await poll.WaitAsync(Until.Deadline);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
        // the stop cut the held poll short: it did not wait it out
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(pollWaitSeconds), $"took {watch.Elapsed}");
        Assert.True(await UiRig.Refuses(grant.Port));
        Assert.False(File.Exists(rig.InfoPath));
    }

    [Fact]
    public async Task AHandlerThatIgnoresCancellationStillLetsTheStopFinishBeforeThePollWouldEnd()
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ignore = rig.IgnoreCancellation;
        rig.View = (request, cancellation) =>
        {
            _ = entered.TrySetResult();
            return ignore(request, cancellation);
        };
        const int pollWaitSeconds = 25;
        var poll = rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/view?path=/events&since=1.0&wait={pollWaitSeconds}");
        await entered.Task.WaitAsync(Until.Deadline);
        var watch = Stopwatch.StartNew();

        var stop = rig.Bridge.DropLeaseAsync(grant.Lease);
        await stop.WaitAsync(Until.Deadline);

        // the stop did not wait out the handler that ignores its cancellation
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(pollWaitSeconds), $"took {watch.Elapsed}");
        Assert.True(await UiRig.Refuses(grant.Port));
        Assert.False(File.Exists(rig.InfoPath));
        using var reply = await poll.WaitAsync(Until.Deadline);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, reply.StatusCode);
    }

    [Fact]
    public async Task ARequestThatArrivesAfterTheCloseBeginsGets503AndAWrongHostStillGets421()
    {
        await using var rig = UiRig.Create();
        var session = await UiSession.StartAsync(rig.Options);
        try
        {
            using (var before = await rig.SendAsync(session.Port, HttpMethod.Get, "/ui/v1/view?path=/"))
            {
                Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            }

            session.BeginClose();

            using var closing = await rig.SendAsync(session.Port, HttpMethod.Get, "/ui/v1/view?path=/");
            using var page = await rig.SendAsync(session.Port, HttpMethod.Get, "/ui/");
            using var rebinding = await rig.SendAsync(session.Port, HttpMethod.Get, "/ui/v1/view?path=/", request => request.Headers.Host = "evil.example");
            using var foreign = await rig.SendAsync(session.Port, HttpMethod.Get, "/ui/v1/view?path=/", request => request.Headers.TryAddWithoutValidation("Origin", "null"));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, closing.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, page.StatusCode);
            Assert.Equal(HttpStatusCode.MisdirectedRequest, rebinding.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        }
        finally
        {
            await session.StopAsync(TimeSpan.FromSeconds(1));
            session.Dispose();
        }
    }

    [Theory]
    [InlineData(12_000, true)]
    [InlineData(31_000, true)]
    [InlineData(40_000, false)]
    public async Task TheListenerTakesARequestLineUpToTheCapAndRefusesOneBeyondIt(int subChars, bool taken)
    {
        var (rig, grant) = await OpenAsync();
        await using var owner = rig;
        var sub = string.Join(',', Enumerable.Range(0, subChars / 40).Select(static i => $"member:9f3a1c7e5b2d4a68/ship-ui/m{i:D5}"));

        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, $"/ui/v1/view?path=/events&since=0&sub={sub}&wait=0");

        Assert.Equal(taken, reply.StatusCode != HttpStatusCode.RequestUriTooLong && (int)reply.StatusCode != 431);
    }

    [Fact]
    public async Task ALeaseAfterAFullCloseBindsAFreshListenerOnTheSamePort()
    {
        var (rig, first) = await OpenAsync();
        await using var owner = rig;
        await rig.Bridge.DropLeaseAsync(first.Lease);
        await Until.TrueAsync(() => !UiRig.Listening(first.Port), 2_000, () => $"port {first.Port} still listening 2 s after the full close");

        var second = await rig.LeaseAsync();

        Assert.True(first.Port == second.Port, $"rebound on {second.Port}, not {first.Port} (moved from {second.MovedFrom}); log: {rig.LogText}");
        using var fresh = await rig.SendAsync(second.Port, HttpMethod.Get, "/ui/v1/view?path=/");
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        Assert.Equal(second.Port, (int)JsonText.Parse(await File.ReadAllTextAsync(rig.InfoPath)).AsObject()["port"].AsNumber());
    }

    [Fact]
    public async Task ALeaseIssuedWhileTheStopRunsWaitsForItAndThenGetsAFreshHost()
    {
        var (rig, first) = await OpenAsync();
        await using var owner = rig;
        rig.UnlinkGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = Task.Run(() => rig.Bridge.DropLeaseAsync(first.Lease));
        await Until.TrueAsync(() => !UiRig.Listening(first.Port));

        var lease = rig.Bridge.OpenLeaseAsync(55, default);
        await Task.Delay(200);
        Assert.False(lease.IsCompleted, "the lease did not wait for the stop");
        Assert.False(stop.IsCompleted);

        rig.UnlinkGate.SetResult();
        var second = await lease.WaitAsync(Until.Deadline);
        await stop;

        Assert.Equal(first.Port, second.Port);
        Assert.True(UiRig.Listening(second.Port));
        Assert.Single(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), e => e.Port == second.Port);
        Assert.Equal(second.Port, (int)JsonText.Parse(await File.ReadAllTextAsync(rig.InfoPath)).AsObject()["port"].AsNumber());
    }

    [Fact]
    public async Task ALeaseIssuedWhileTheStopRunsIsNotLostWhenTheStopFinishes()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync();
        rig.UnlinkGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = Task.Run(() => rig.Bridge.DropLeaseAsync(first.Lease));
        await Until.TrueAsync(() => !UiRig.Listening(first.Port));
        var lease = rig.Bridge.OpenLeaseAsync(56, default);
        rig.UnlinkGate.SetResult();

        var second = await lease.WaitAsync(Until.Deadline);
        await stop;

        Assert.Equal(1, rig.Bridge.Leases);
        Assert.True(rig.Bridge.Active);
        Assert.Equal(second.Port, rig.Bridge.Port);
    }

    [Fact]
    public async Task ALeaseAndAnIdleExpiryAtTheSameInstantNeverLeaveAHalfStoppedHost()
    {
        await using var rig = UiRig.Create();
        var first = await rig.LeaseAsync();
        rig.Clock.Advance(TimeSpan.FromMinutes(15));

        var lease = rig.Bridge.OpenLeaseAsync(9, default);
        var idle = rig.Bridge.CheckIdleAsync();
        var grant = await lease;
        await idle;

        Assert.True(rig.Bridge.Active);
        Assert.True(UiRig.Listening(grant.Port));
        Assert.Single(System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners(), e => e.Port == grant.Port);
        Assert.Equal(grant.Port, (int)JsonText.Parse(await File.ReadAllTextAsync(rig.InfoPath)).AsObject()["port"].AsNumber());
    }
}
