namespace Ah.Codex.Tests;

/// <summary>
/// The spawn's conversation with a member's own server, through the facade a launcher calls: the thread it created, or one fixed
/// phrase that names the step that failed and carries nothing the server said.
/// </summary>
public sealed class BringUpFacadeTests
{
    private static readonly CodexModes Modes = new("workspace-write", "on-request");

    private static (bool Ok, string? ThreadId, string? Note) Run(string options, string? socketOverride = null)
    {
        using var sandbox = new TestSandbox();
        var tmp = Path.Combine(sandbox.Root, "run", "codex", "tmp", "team-member");
        using var fake = FakeCodexServer.Start(sandbox, options, CodexModule.ServerArgs(sandbox.Path("fake.sock"), null, tmp));
        return CodexModule.BringUp(socketOverride ?? fake.Socket, Modes, "/tmp/x");
    }

    [Fact]
    public void ARunningServerGivesTheThreadAndNoNote()
    {
        var up = Run("{}");

        Assert.True(up.Ok);
        Assert.Matches("^th-[0-9]+$", up.ThreadId);
        Assert.Null(up.Note);
    }

    [Fact]
    public void ASocketNobodyListensOnIsAThreadStartFailure()
    {
        using var sandbox = new TestSandbox();

        var up = CodexModule.BringUp(sandbox.Path("nobody.sock"), Modes, "/tmp/x");

        Assert.Equal((false, (string?)null, (string?)"thread/start failed"), up);
    }

    [Fact]
    public void AServerThatRefusesThreadStartIsAThreadStartFailureAndItsTextIsNotKept()
    {
        var up = Run("{\"threadStartError\":true,\"sentinel\":\"SENTINEL-TOKEN\"}");

        Assert.False(up.Ok);
        Assert.Equal("thread/start failed", up.Note);
        Assert.DoesNotContain("SENTINEL", up.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AServerThatDidNotTakeThePinsIsRefusedByTheGate()
    {
        var up = Run("{\"sandboxType\":\"dangerFullAccess\"}");

        Assert.False(up.Ok);
        Assert.Null(up.ThreadId);
        Assert.Equal("the app-server did not apply ah's pins", up.Note);
    }

    [Theory]
    [InlineData("th_1.2-x", true)]
    [InlineData("th 1", false)]
    [InlineData("th;1", false)]
    [InlineData("th$(x)", false)]
    [InlineData("th/1", false)]
    [InlineData("th1\n", false)]
    [InlineData("th1\r", false)]
    public void AThreadIdIsAcceptedOnlyAsAPlainTokenBecauseItGoesIntoACommandLine(string id, bool accepted)
    {
        var up = Run("{\"threadIdText\":\"" + id.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal) + "\"}");

        Assert.Equal(accepted, up.Ok);
        Assert.Equal(accepted ? id : null, up.ThreadId);
        Assert.Equal(accepted ? null : "thread/start failed", up.Note);
    }
}
