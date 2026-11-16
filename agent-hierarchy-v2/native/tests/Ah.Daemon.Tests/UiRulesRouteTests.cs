#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary><c>GET</c> and <c>POST /ui/v1/rules</c> on a real listener against a fake core: the status table, and that nothing reaches the core without a bridge session.</summary>
public sealed class UiRulesRouteTests
{
    private sealed class FakeCore
    {
        public List<JsonObject> Writes { get; } = [];

        public List<string> Reads { get; } = [];

        public UiRulesSupport Support => new(
            team =>
            {
                lock (Reads)
                {
                    Reads.Add(team);
                }

                return team == "ghost" ? new JsonObject { ["code"] = 2, ["message"] = "no team ghost" } : new JsonObject { ["code"] = 0, ["rows"] = new JsonArray() };
            },
            input =>
            {
                lock (Writes)
                {
                    Writes.Add(input);
                }

                return input.Str("key") switch
                {
                    "taken" => new JsonObject { ["code"] = 3, ["message"] = "key taken" },
                    "missing" => new JsonObject { ["code"] = 1, ["message"] = "no rule" },
                    "bad" => new JsonObject { ["code"] = 2, ["message"] = "bad key" },
                    "boom" => throw new IOException("disk gone"),
                    _ => new JsonObject { ["code"] = 0, ["message"] = "added" },
                };
            });
    }

    private static string Body(string key = "k", string op = "add") => $$"""{"op":"{{op}}","team":"t1","level":"repo","key":"{{key}}","text":"x"}""";

    private static async Task<(UiRig Rig, UiGrant Grant, FakeCore Core)> OpenAsync(bool wired = true)
    {
        var core = new FakeCore();
        var rig = UiRig.Create(rules: wired ? core.Support : null);
        return (rig, await rig.LeaseAsync(), core);
    }

    private static async Task<(HttpStatusCode Status, JsonValue Body)> PostAsync(UiRig rig, UiGrant grant, string body)
    {
        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Post, "/ui/v1/rules", request => request.Content = UiRig.Json(body));
        var text = await reply.Content.ReadAsStringAsync();
        return (reply.StatusCode, JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(text), out var parsed) ? parsed : JsonValue.Null);
    }

    private static async Task<HttpStatusCode> GetAsync(UiRig rig, UiGrant grant, string query)
    {
        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Get, "/ui/v1/rules" + query);
        return reply.StatusCode;
    }

    [Theory]
    [InlineData("k", HttpStatusCode.OK)]
    [InlineData("taken", HttpStatusCode.Conflict)]
    [InlineData("missing", HttpStatusCode.NotFound)]
    [InlineData("bad", HttpStatusCode.BadRequest)]
    [InlineData("boom", HttpStatusCode.InternalServerError)]
    public async Task AWriteAnswersTheCoresCodeAsAStatus(string key, HttpStatusCode expected)
    {
        var (rig, grant, core) = await OpenAsync();
        await using (rig)
        {
            var (status, _) = await PostAsync(rig, grant, Body(key));

            Assert.Equal(expected, status);
            Assert.Single(core.Writes);
        }
    }


    [Theory]
    [InlineData("not json")]
    [InlineData("""{"team":"t1","level":"repo","key":"k"}""")]
    [InlineData("""{"op":"add","level":"repo","key":"k"}""")]
    [InlineData("""{"op":"add","team":"t1","key":"k"}""")]
    [InlineData("""{"op":"add","team":"t1","level":"repo"}""")]
    [InlineData("""{"op":"add","team":"t1","level":"repo","key":"k","text":5}""")]
    public async Task AMalformedBodyIs400AndNeverReachesTheCore(string body)
    {
        var (rig, grant, core) = await OpenAsync();
        await using (rig)
        {
            var (status, reply) = await PostAsync(rig, grant, body);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal(2, reply.Member("code")!.Value.AsNumber());
            Assert.Empty(core.Writes);
        }
    }

    [Fact]
    public async Task OnlyTheFieldsTheRouteKnowsReachTheCore()
    {
        var (rig, grant, core) = await OpenAsync();
        await using (rig)
        {
            _ = await PostAsync(rig, grant, """{"op":"add","team":"t1","level":"repo","key":"k","text":"x","by":"forged","said":"forged","dir":"/elsewhere"}""");

            var input = Assert.Single(core.Writes);
            Assert.Null(input.Get("by"));
            Assert.Null(input.Get("said"));
            Assert.Null(input.Get("dir"));
        }
    }

    [Fact]
    public async Task AReadNeedsATeamAndAnUnknownOneIsRefused()
    {
        var (rig, grant, core) = await OpenAsync();
        await using (rig)
        {
            Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(rig, grant, string.Empty));
            Assert.Empty(core.Reads);
            Assert.Equal(HttpStatusCode.BadRequest, await GetAsync(rig, grant, "?team=ghost"));
            Assert.Equal(HttpStatusCode.OK, await GetAsync(rig, grant, "?team=t1"));
        }
    }

    [Fact]
    public async Task UnwiredIs503()
    {
        var (rig, grant, _) = await OpenAsync(wired: false);
        await using (rig)
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(rig, grant, Body())).Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, await GetAsync(rig, grant, "?team=t1"));
        }
    }

    [Fact]
    public async Task AnotherMethodIs405()
    {
        var (rig, grant, _) = await OpenAsync();
        await using (rig)
        {
            using var reply = await rig.SendAsync(grant.Port, HttpMethod.Delete, "/ui/v1/rules");

            Assert.Equal(HttpStatusCode.MethodNotAllowed, reply.StatusCode);
        }
    }
}
