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

    [Theory]
    [InlineData("replaced", 200)]
    [InlineData("same", 403)] // negative: the binary it runs is still the installed one
    [InlineData("gone", 403)] // negative: the identity cannot be read at the drain
    [InlineData("unread", 403)] // negative: the identity could not be read at start
    public async Task AnAnonymousDrainStopsADaemonWhoseBinaryWasReplaced(string binary, int status)
    {
        var box = Sandbox.Create();
        var path = Path.Combine(box.Root, "ahd");
        if (binary != "unread")
        {
            await File.WriteAllTextAsync(path, "old", TestContext.Current.CancellationToken);
        }

        await using var h = await Harness.StartAsync(box, o =>
        {
            o.Principal = _ => PrincipalKind.Anonymous;
            o.Vars.Remove("AH_TEST");
            o.BinaryPath = path;
        });

        switch (binary)
        {
            case "replaced":
                // the installer places through a temporary file and a rename, so the path names a new inode
                await File.WriteAllTextAsync(path + ".tmp", "new build", TestContext.Current.CancellationToken);
                File.Move(path + ".tmp", path, overwrite: true);
                break;
            case "gone":
                File.Delete(path);
                break;
            case "unread":
                await File.WriteAllTextAsync(path, "new", TestContext.Current.CancellationToken);
                break;
        }

        var reply = await h.PostAsync("/v1/drain", "{\"reason\":\"ignored\"}");

        Assert.Equal(status, reply.Status);
        if (status == 200)
        {
            await h.Exit.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Contains("drain accepted: the installed binary changed (caller pid", h.Log.ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("\"anonymous\"", reply.Body, StringComparison.Ordinal);
            Assert.Contains("a drain needs a caller the daemon can name", reply.Body, StringComparison.Ordinal);
            Assert.False(h.Exited);
        }
    }
}
