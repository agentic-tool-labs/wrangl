namespace Ah.Cli.Tests;

/// <summary>Sandbox.MsgFile takes an id or a `msg new|fill --plain` line, `&lt;path&gt;  &lt;id&gt;[  existing]`.</summary>
public sealed class SandboxMsgFileTests : IDisposable
{
    private const string Id = "20260101-000000-abcd";
    private readonly Sandbox _sandbox = new();

    public void Dispose() => _sandbox.Dispose();

    private string Plant() => _sandbox.Write($"proj/.claude/ah2/msgs/{Id}--implementor--probe--request.md", "---\nid: x\n---\n");

    [Fact]
    public void APlainLineResolvesByItsIdField()
    {
        var path = Plant();

        Assert.Equal(path, _sandbox.MsgFile($"{path}  {Id}\n"));
        Assert.Equal(path, _sandbox.MsgFile($"{path}  {Id}  existing\n"));
    }

    [Fact]
    public void NegativeABareIdStillResolvesAndAnotherIdDoesNot()
    {
        var path = Plant();

        Assert.Equal(path, _sandbox.MsgFile(Id));
        Assert.Throws<InvalidOperationException>(() => _sandbox.MsgFile($"{path}  20260101-000000-zzzz"));
    }
}
