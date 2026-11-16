namespace Ah.Daemon.Tests;

/// <summary>The minute route line names a state request's op, and only an op the service registers.</summary>
public sealed class RouteStatKeyTests
{
    private const string Post = "POST /v1/state/{domain}/{op}";

    private static readonly Func<string, string, bool> Known = static (domain, op) => domain == "mail" && op == "heartbeat";

    private static Dictionary<string, string> Pair(string domain, string op) => new(StringComparer.Ordinal) { ["domain"] = domain, ["op"] = op };

    [Fact]
    public void AStateRequestIsCountedUnderItsDomainAndOp()
    {
        Assert.Equal("POST state mail.heartbeat", DaemonServer.RouteStatKey(Post, Pair("mail", "heartbeat"), Known));
        Assert.Equal("GET state mail.heartbeat", DaemonServer.RouteStatKey("GET /v1/state/{domain}/{op}", Pair("mail", "heartbeat"), Known));
    }

    [Fact]
    public void AnUnregisteredPairAndOtherRoutesKeepTheRouteKey()
    {
        // an unknown op is keyed by lookup, whatever status the service answered it with
        Assert.Equal(Post, DaemonServer.RouteStatKey(Post, Pair("mail", "made-up"), Known));
        Assert.Equal(Post, DaemonServer.RouteStatKey(Post, Pair("made-up", "heartbeat"), Known));
        Assert.Equal(Post, DaemonServer.RouteStatKey(Post, Pair("mail", "heartbeat"), null));
        Assert.Equal("GET /v1/due", DaemonServer.RouteStatKey("GET /v1/due", Pair("mail", "heartbeat"), Known));
    }
}
