namespace Ah.DaemonClient.Tests;

public sealed class DaemonClientModuleTests
{
    [Fact]
    public void RegistryIsTheGeneratedOne() =>
        Assert.Same(GeneratedRegistry.Instance, DaemonClientModule.Registry);
}
