using Ah.Abstractions;
using Ah.Commands;
using Ah.Platform;

namespace Ah.Cli.Composition;

/// <summary>
/// The services <c>ahd home trust|untrust</c> run on, handed to <see cref="CommandsModule"/> without building anything: the trust
/// store comes from the service graph, built the first time a trust verb asks.
/// </summary>
internal sealed class HomeTrustComposition(ServiceGraph graph, IAhEnvironment environment) : IHomeTrustServices
{
    internal static void Compose(Lazy<ServiceGraph> graph, IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(environment);
        var services = new Lazy<IHomeTrustServices>(
            () => new HomeTrustComposition(graph.Value, environment),
            LazyThreadSafetyMode.ExecutionAndPublication);
        CommandsModule.ComposeHomeTrust(() => services.Value);
    }

    public ITrustStore Trust => graph.Trust;

    public IFileReader Files => graph.Io.Files;

    public IRawFileWriter Writer => graph.Io.Writer;

    public IAhEnvironment Environment => environment;

    public uint? CurrentUserId => OperatingSystem.IsWindows() ? null : PlatformModule.CurrentUid();

    public bool IsTerminal()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        var mode = PlatformModule.CreateTerminalMode();
        return mode.IsTerminal(0) && mode.IsTerminal(2);
    }
}
