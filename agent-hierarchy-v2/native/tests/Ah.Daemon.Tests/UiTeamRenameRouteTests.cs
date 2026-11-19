#pragma warning disable xUnit1051 // the rig owns its own deadlines

using System.Net;
using Ah.Core.Json;

namespace Ah.Daemon.Tests;

/// <summary><c>POST /ui/v1/team-rename</c> (ah2-218 A4) on a real listener against a fake core: the status table, under the bridge's request guard.</summary>
public sealed class UiTeamRenameRouteTests
{
    private const string DirId = "abcd1234";
    private const string Dir = "/repo/.claude/ah2";

    private sealed class FakeRename
    {
        public List<JsonObject> Calls { get; } = [];

        public UiTeamCloseSupport Support(bool withRename = true) => new(
            id => id == DirId ? Dir : null,
            _ => throw new InvalidOperationException("the close core is not this route's"),
            withRename
                ? input =>
                {
                    lock (Calls)
                    {
                        Calls.Add(input);
                    }

                    return input.Get("team")!.Value.AsString() switch
                    {
                        "gone" => new JsonObject { ["refused"] = new JsonObject { ["reason"] = "unknown-team", ["detail"] = "no team gone" } },
                        "long" => new JsonObject { ["refused"] = new JsonObject { ["reason"] = "invalid", ["detail"] = "the display name is 61 characters; at most 60" } },
                        _ => new JsonObject { ["ok"] = true, ["team"] = input.Get("team")!.Value, ["display_name"] = input.Get("display_name")!.Value },
                    };
                }
                : null);
    }

    private static async Task<(HttpStatusCode Status, JsonValue Body)> PostAsync(UiRig rig, UiGrant grant, string body, Action<HttpRequestMessage>? tweak = null)
    {
        using var reply = await rig.SendAsync(grant.Port, HttpMethod.Post, "/ui/v1/team-rename", request =>
        {
            request.Content = UiRig.Json(body);
            tweak?.Invoke(request);
        });
        var text = await reply.Content.ReadAsStringAsync();
        return (reply.StatusCode, JsonText.TryParse(System.Text.Encoding.UTF8.GetBytes(text), out var parsed) ? parsed : JsonValue.Null);
    }

    private static async Task<(UiRig Rig, UiGrant Grant, FakeRename Fake)> OpenAsync(bool withRename = true)
    {
        var fake = new FakeRename();
        var rig = UiRig.Create(teamClose: fake.Support(withRename));
        return (rig, await rig.LeaseAsync(), fake);
    }

    [Fact]
    public async Task TheBridgeResolvesTheDirRecordsTheUiAndSetsOrClears()
    {
        var (rig, grant, fake) = await OpenAsync();
        await using var owner = rig;

        var (set, setBody) = await PostAsync(rig, grant, $$"""{"dir":"{{DirId}}","team":"t1","display_name":"Ops"}""");
        var (cleared, _) = await PostAsync(rig, grant, $$"""{"dir":"{{DirId}}","team":"t1","display_name":null}""");

        Assert.Equal((HttpStatusCode.OK, HttpStatusCode.OK), (set, cleared));
        Assert.Equal("Ops", setBody.Member("display_name")!.Value.AsString());
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal((Dir, "t1", "ui", "Ops"), (fake.Calls[0].Get("dir")!.Value.AsString(), fake.Calls[0].Get("team")!.Value.AsString(), fake.Calls[0].Get("by")!.Value.AsString(), fake.Calls[0].Get("display_name")!.Value.AsString()));
        Assert.True(fake.Calls[1].Get("display_name")!.Value.IsNull);
    }

    [Fact]
    public async Task AnInvalidNameIs400WithTheReasonAndAnUnknownTeamIs409()
    {
        var (rig, grant, _) = await OpenAsync();
        await using var owner = rig;

        var (invalid, invalidBody) = await PostAsync(rig, grant, $$"""{"dir":"{{DirId}}","team":"long","display_name":"x"}""");
        var (gone, goneBody) = await PostAsync(rig, grant, $$"""{"dir":"{{DirId}}","team":"gone","display_name":"x"}""");

        Assert.Equal(HttpStatusCode.BadRequest, invalid);
        Assert.Contains("at most 60", invalidBody.Member("detail")!.Value.AsString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.Conflict, gone);
        Assert.Equal("unknown-team", goneBody.Member("reason")!.Value.AsString());
    }

    [Theory]
    [InlineData("""{"dir":"abcd1234","team":"t1"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"dir":"abcd1234","team":"t1","display_name":7}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"dir":"abcd1234","display_name":"x"}""", HttpStatusCode.BadRequest)]
    [InlineData("""{"dir":"nope","team":"t1","display_name":"x"}""", HttpStatusCode.Conflict)]
    public async Task NegativeABadBodyOrAnUnknownDirReachesNoCore(string body, HttpStatusCode expected)
    {
        var (rig, grant, fake) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, body);

        Assert.Equal(expected, status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task NegativeAForeignOriginIsRefusedAndReachesNoCore()
    {
        var (rig, grant, fake) = await OpenAsync();
        await using var owner = rig;

        var (status, _) = await PostAsync(rig, grant, $$"""{"dir":"{{DirId}}","team":"t1","display_name":"x"}""", request => request.Headers.TryAddWithoutValidation("Origin", "http://evil.example"));

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task NegativeAHostWithACloseCoreButNoRenameCoreAnswers503()
    {
        var (rig, grant, _) = await OpenAsync(withRename: false);
        await using var owner = rig;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await PostAsync(rig, grant, $$"""{"dir":"{{DirId}}","team":"t1","display_name":"x"}""")).Status);
    }
}
