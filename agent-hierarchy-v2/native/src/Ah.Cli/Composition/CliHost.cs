using Ah.Abstractions;
using Ah.Platform;

namespace Ah.Cli.Composition;

internal static class CliHost
{
    internal static CliContext Create()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("ahd runs on macOS and Linux");
        }

        return Create(WithPlugin(PlatformModule.CreateEnvironment(null)), PlatformModule.CreateProcessRunner(), DaemonComposition.Graph) with
        {
            StdoutIsTerminal = PlatformModule.CreateTerminalMode().IsTerminal(1),
        };
    }

    /// <summary>The process environment with the plugin resolved on first use, so a verb run from any shell sees the plugin root.</summary>
    internal static IAhEnvironment WithPlugin(IAhEnvironment process) => new DaemonComposition.PluginAwareEnvironment(process, reportsBinaryVersion: false);

    /// <summary>A context for this process (its command line, path, passwd home and uid) over the given environment, runner and service graph.</summary>
    internal static CliContext Create(IAhEnvironment environment, IProcessRunner runner, Lazy<ServiceGraph>? services) =>
        new(
            environment,
            PlatformModule.CreateExec(),
            runner,
            Environment.GetCommandLineArgs()[0],
            Environment.ProcessPath ?? string.Empty,
            PlatformModule.PasswdHome(),
            PlatformModule.CurrentUid(),
            Services: services);
}
