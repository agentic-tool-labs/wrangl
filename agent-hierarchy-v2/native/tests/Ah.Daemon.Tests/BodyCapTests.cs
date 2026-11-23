namespace Ah.Daemon.Tests;

/// <summary>ah2-519: the whole-team-record write takes up to 4 MiB; every other route keeps its own cap.</summary>
public sealed class BodyCapTests
{
    private static string Body(int bytes) => $"{{\"pad\":\"{new string('x', bytes)}\"}}";

    private static Task<Harness> StartAsync() => Harness.StartAsync(Sandbox.Create(), o =>
    {
        o.Routes.Add(TestRoutes.Echo("POST /v1/state/team/write"));
        o.Routes.Add(TestRoutes.Echo("POST /v1/state/team/history"));
    });

    [Fact]
    public async Task ATeamRecordWriteOf200KiBIsAcceptedAndOneOverTheCapIsRefusedNamingTheCap()
    {
        await using var h = await StartAsync();

        var accepted = await h.PostAsync("/v1/state/team/write", Body(200 * 1024));
        var over = Raw.Parse(await Raw.ExchangeAsync(
            h.Box.Sock,
            $"POST /v1/state/team/write HTTP/1.1\r\nHost: ahd\r\nContent-Type: application/json\r\nContent-Length: {BodyCaps.TeamRecordWrite + 1}\r\nConnection: close\r\n\r\n"));

        Assert.Equal(200, accepted.Status);
        Assert.Equal(413, over.Status);
        Assert.Contains($"the limit is {BodyCaps.TeamRecordWrite}", over.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeAnotherTeamOpKeepsThe64KiBCap()
    {
        await using var h = await StartAsync();

        var reply = await h.PostAsync("/v1/state/team/history", Body(200 * 1024));

        Assert.Equal(413, reply.Status);
        Assert.Contains($"the limit is {BodyCaps.Default}", reply.Body, StringComparison.Ordinal);
    }
}
