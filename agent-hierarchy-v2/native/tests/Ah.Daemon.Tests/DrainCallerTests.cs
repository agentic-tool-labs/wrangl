using Ah.Abstractions;

namespace Ah.Daemon.Tests;

/// <summary>A drain from a caller the daemon cannot name: a test-mode daemon stops, any other refuses.</summary>
public sealed class DrainCallerTests
{
    [Theory]
    [InlineData(true, 200)]
    [InlineData(false, 403)] // negative: outside test mode the anonymous caller is still refused
    public async Task AnAnonymousDrainStopsOnlyATestModeDaemon(bool testMode, int status)
    {
        await using var h = await Harness.StartAsync(Sandbox.Create(), o =>
        {
            o.Principal = _ => PrincipalKind.Anonymous;
            if (!testMode)
            {
                o.Vars.Remove("AH_TEST");
            }
        });

        var reply = await h.PostAsync("/v1/drain", "{}");

        Assert.Equal(status, reply.Status);
        if (testMode)
        {
            await h.Exit.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        }
        else
        {
            Assert.Contains("a drain needs a caller the daemon can name", reply.Body, StringComparison.Ordinal);
            Assert.False(h.Exited);
        }
    }
}
