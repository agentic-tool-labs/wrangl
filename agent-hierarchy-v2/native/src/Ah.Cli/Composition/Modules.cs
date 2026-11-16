using Ah.Abstractions;
using Ah.Codex;
using Ah.Commands;
using Ah.Config;
using Ah.Daemon;
using Ah.DaemonClient;
using Ah.Hierarchy;
using Ah.Hooks;
using Ah.Launch;
using Ah.Mesh;
using Ah.Roster;
using Ah.State;
using Ah.Watch;

namespace Ah.Cli.Composition;

internal static class Modules
{
    internal static IReadOnlyList<ModuleRegistry> Registries { get; } =
    [
        GeneratedRegistry.Instance,
        ConfigModule.Registry,
        RosterModule.Registry,
        HierarchyModule.Registry,
        MeshModule.Registry,
        CodexModule.Registry,
        LaunchModule.Registry,
        WatchModule.Registry,
        StateModule.Registry,
        DaemonClientModule.Registry,
        DaemonModule.Registry,
        HooksModule.Registry,
        CommandsModule.Registry,
    ];
}
