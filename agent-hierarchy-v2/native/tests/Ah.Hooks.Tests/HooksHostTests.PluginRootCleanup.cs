namespace Ah.Hooks.Tests;

/// <summary>A plugin root a test makes is gone once that test is disposed, so a run leaves no <c>ahprobe.plugin.*</c> directory.</summary>
public sealed partial class HooksHostTests
{
    [Fact]
    public void APluginRootIsRemovedWhenTheTestThatMadeItIsDisposed()
    {
        var top = Path.GetDirectoryName(MakePluginRoot())!;
        Assert.True(Directory.Exists(top));

        new HooksHostTests().Dispose();

        Assert.False(Directory.Exists(top));
    }
}
