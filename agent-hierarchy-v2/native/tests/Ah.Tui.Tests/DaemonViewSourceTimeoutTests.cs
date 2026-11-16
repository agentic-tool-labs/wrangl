using Ah.Abstractions;
using Ah.Core.Json;
using Ah.TestKit;
using Ah.Tui.Core;
using Ah.Tui.Adapters;

namespace Ah.Tui.Tests;

/// <summary>The view build can take several seconds, so the view fetch waits 15 s; the health probe stays at 500 ms.</summary>
public sealed class DaemonViewSourceTimeoutTests
{
    private sealed class Transport : IUnixHttpTransport
    {
        internal List<(string Path, TimeSpan Timeout)> Sent { get; } = [];

        public Task<HttpReply> SendAsync(
            string socketPath, string method, string pathAndQuery, ReadOnlyMemory<byte> body, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Sent.Add((pathAndQuery, timeout));
            return Task.FromResult(new HttpReply(200, "{}"u8.ToArray()));
        }
    }

    [Fact]
    public async Task TheViewFetchWaitsFifteenSecondsAndAHeldOneWaitsItsPollPlusFifteen()
    {
        var transport = new Transport();
        var source = new DaemonViewSource(new FakeDaemonClient(), transport);
        var probe = new DaemonProbe(JsonText.Parse("{}"), "/tmp/none.sock");

        _ = await source.GetViewAsync(probe, "/", null, 0, CancellationToken.None);
        _ = await source.GetViewAsync(probe, "/", "abc", 20, CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(15), transport.Sent[0].Timeout);
        Assert.Equal(TimeSpan.FromSeconds(35), transport.Sent[1].Timeout);
    }

    [Fact]
    public async Task ASlicePollSendsItsCursorAndWaitsItsHoldPlusFifteen()
    {
        var transport = new Transport();
        var source = new DaemonViewSource(new FakeDaemonClient(), transport);
        var probe = new DaemonProbe(JsonText.Parse("{}"), "/tmp/none.sock");

        _ = await source.GetSlicesAsync(probe, new SlicePoll(["team:d/t", "member:d/t/a"], ["team:d/t"], 0, null, 10), CancellationToken.None);
        _ = await source.GetSlicesAsync(probe, new SlicePoll(["team:d/t"], [], 7, "e1", 10), CancellationToken.None);

        Assert.Equal("/v1/view?path=%2Fevents&since=0&wait=10&sub=team%3Ad%2Ft%2Cmember%3Ad%2Ft%2Fa&fresh=team%3Ad%2Ft", transport.Sent[0].Path);
        Assert.Equal("/v1/view?path=%2Fevents&since=7&epoch=e1&wait=10&sub=team%3Ad%2Ft", transport.Sent[1].Path);
        Assert.Equal(TimeSpan.FromSeconds(25), transport.Sent[1].Timeout);
    }

    private sealed class HoldingEvents : IUnixHttpTransport
    {
        public async Task<HttpReply> SendAsync(string socketPath, string method, string pathAndQuery, ReadOnlyMemory<byte> body, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (pathAndQuery.Contains("%2Fevents", StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            }

            return new HttpReply(200, "{}"u8.ToArray());
        }
    }

    [Fact]
    public async Task HeldSlicePollsTakeNoSlotFromTheViewRequests()
    {
        var source = new DaemonViewSource(new FakeDaemonClient(), new HoldingEvents());
        var probe = new DaemonProbe(JsonText.Parse("{}"), "/tmp/none.sock");
        using var stop = new CancellationTokenSource();
        var held = Enumerable.Range(0, DaemonViewSource.MaxInFlight + 1).Select(_ => source.GetSlicesAsync(probe, new SlicePoll(["a"], [], 1, null, 10), stop.Token)).ToList();

        var view = source.GetViewAsync(probe, "/", null, 0, CancellationToken.None);
        Assert.Same(view, await Task.WhenAny(view, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)));

        await stop.CancelAsync();
        await Task.WhenAll(held);
    }

    [Fact]
    public async Task TheHealthProbeStaysAtFiveHundredMilliseconds()
    {
        var transport = new Transport();
        var client = new FakeDaemonClient { Info = FakeDaemonClient.Up() };
        var source = new DaemonViewSource(client, transport);

        _ = await source.DiscoverAsync(CancellationToken.None);

        var health = Assert.Single(transport.Sent);
        Assert.Equal("/v1/health", health.Path);
        Assert.Equal(TimeSpan.FromMilliseconds(500), health.Timeout);
    }
}
