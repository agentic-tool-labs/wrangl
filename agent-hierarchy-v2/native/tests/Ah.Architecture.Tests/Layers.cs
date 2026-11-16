namespace Ah.Architecture.Tests;

internal static class Layers
{
    private static readonly string[] Domain =
    [
        "Ah.Config", "Ah.Roster", "Ah.Hierarchy", "Ah.Mesh", "Ah.Codex", "Ah.Launch", "Ah.Watch",
    ];

    private static string[] Below(string project) =>
        ["Ah.Abstractions", "Ah.Core", .. Domain.TakeWhile(d => d != project)];

    private static readonly string[] AllDomain = ["Ah.Abstractions", "Ah.Core", .. Domain];

    internal static readonly IReadOnlyDictionary<string, string[]> AllowedReferences = new Dictionary<string, string[]>
    {
        ["Ah.Abstractions"] = [],
        ["Ah.Core"] = ["Ah.Abstractions"],
        ["Ah.Platform"] = ["Ah.Abstractions", "Ah.Core"],
        ["Ah.Config"] = Below("Ah.Config"),
        ["Ah.Roster"] = Below("Ah.Roster"),
        ["Ah.Hierarchy"] = Below("Ah.Hierarchy"),
        ["Ah.Mesh"] = Below("Ah.Mesh"),
        ["Ah.Codex"] = Below("Ah.Codex"),
        ["Ah.Launch"] = Below("Ah.Launch"),
        ["Ah.Watch"] = Below("Ah.Watch"),
        ["Ah.State"] = ["Ah.Abstractions", "Ah.Core"],
        ["Ah.StateDb"] = ["Ah.Abstractions", "Ah.Core"],
        ["Ah.DaemonClient"] = ["Ah.Abstractions", "Ah.Core"],
        ["Ah.Daemon"] = [.. AllDomain, "Ah.State", "Ah.DaemonClient"],
        ["Ah.Hooks"] = [.. AllDomain, "Ah.DaemonClient"],
        ["Ah.Commands"] = [.. AllDomain, "Ah.State", "Ah.DaemonClient"],
        ["Ah.Cli"] =
        [
            "Ah.Abstractions", "Ah.Core", "Ah.Platform", .. Domain, "Ah.State", "Ah.StateDb", "Ah.DaemonClient",
            "Ah.Daemon", "Ah.Hooks", "Ah.Commands",
        ],
        ["Ah.Tui"] = ["Ah.Abstractions", "Ah.Core", "Ah.Platform", "Ah.DaemonClient"],
        ["Ah.Generators"] = [],
    };

    /// <summary>Projects whose surface row does not require a <c>&lt;Name&gt;Module</c>.</summary>
    internal static readonly string[] WithoutModule = ["Ah.Abstractions", "Ah.Core", "Ah.Cli", "Ah.Tui", "Ah.Generators"];

    /// <summary>
    /// Assemblies that may name IRawFileWriter and IRecordAppender: the owners of the excluded stores, and the
    /// state engine, which implements the write context the other domains write through.
    /// </summary>
    internal static readonly string[] RawWriterUsers =
    [
        "Ah.Platform", "Ah.State", "Ah.DaemonClient", "Ah.Daemon", "Ah.Config", "Ah.Launch", "Ah.Codex",
        "Ah.Commands", "Ah.Cli",
    ];

    /// <summary>
    /// Static fields permitted to be mutable, by <c>Namespace.Type.Field</c>, each with the reason it is allowed and what retires it.
    /// An entry is a debt: nothing else may hold state in a static.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> MutableStaticReasons = new Dictionary<string, string>
    {
        ["Ah.Core.Json.JsonReaderImpl._scratch"] = "ah2-1: the per-thread lists one parse builds its containers from and the short ASCII names it shares between documents; the lists are emptied after every parse and the name table holds strings only, so no value is carried between calls",
        ["Ah.Core.Json.JsonText._writer"] = "ah2-1: the per-thread builder ToUtf8 writes into, emptied after every call and dropped past 1M characters, so it carries no value between calls; retired when the writer encodes to bytes directly",
        ["Ah.Daemon.DaemonModule.s_dependencies"] = "set once by the composition root before serve runs; retired when serve is handed its dependencies",
        ["Ah.Daemon.DaemonModule.s_registerJobs"] = "set once with s_dependencies; retired with it",
        ["Ah.Daemon.DaemonModule.s_registerRoutes"] = "set once with s_dependencies; retired with it",
        ["Ah.Daemon.DaemonModule.s_healthExtras"] = "set once with s_dependencies; retired with it",
        ["Ah.Daemon.DaemonModule.s_onStop"] = "set once with s_dependencies; retired with it",
        ["Ah.Daemon.DaemonModule.s_registries"] = "set once with s_dependencies; retired with it",
        ["Ah.DaemonClient.DaemonClientModule.s_lifecycle"] = "set once by the composition root before a verb runs; retired when the verbs are handed their dependencies",
        ["Ah.State.StateModule.s_service"] = "set once by the composition root before the one-shot state verb runs; retired when the verbs are handed their dependencies",
        ["Ah.Cli.Composition.DaemonComposition.s_graph"] = "set once by the composition root before a verb runs; retired when the verbs are handed their dependencies",
        ["Ah.Commands.CommandsModule.s_standardInput"] = "set once by the composition root (a test's stream, or the process's stdin) before a verb reads it; retired when the verbs are handed their dependencies",
        ["Ah.Core.Text.PathGuard.s_nameOnDisk"] = "set by the platform layer when it composes, to the same function each time: Core may declare no P/Invoke, and a read before it lists the parent, with the same answer",
        ["Ah.Commands.CommandsModule.s_homeTrustServices"] = "set by the composition root before the trust verbs run; retired when the verbs are handed their dependencies",
        ["Ah.Cli.Program.s_realBinary"] = "ah2-22: set once, first thing in Main, so only the real binary (never a test host) installs the heal's direct-write trigger",
        ["Ah.StateDb.StateDbModule.s_directWriteHung"] = "ah2-22: set once by a command line process's composition root so a direct write that gave up on a busy database can start ahd heal; retired when the direct writers are handed their dependencies",
        ["Ah.StateDb.StateDbModule.s_sharedReads"] = "set by OpenPooledStores, which the daemon's composition calls once, so every read of its database reuses one bounded pool (b434); retired when each store is handed the pool",
        ["Ah.Commands.CommandsModule.s_rosterServices"] = "set by the composition root before a roster verb runs (tests re-set it; classes that do are in the RosterServices collection); retired when the verbs are handed their dependencies",
    };

    internal static readonly IReadOnlySet<string> MutableStaticAllowlist = new HashSet<string>(MutableStaticReasons.Keys);

    /// <summary>
    /// Read-only static fields holding something writable that a method writes after initialisation, by <c>Namespace.Type.Field</c>,
    /// each with why it is allowed and what retires it. An entry is a debt, like <see cref="MutableStaticReasons"/>.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> MutableCellReasons = new Dictionary<string, string>
    {
        ["Ah.Core.Diagnostics.Census.Entries"] = "the health census (b459: a Native AOT heap has no gcdump): each long-lived cache adds itself once, held weakly, and only /v1/health reads it; retired when the caches are handed a census instance",
        ["Ah.Watch.Model.ActivityLane.DirIds"] = "b459 step 11: the memo of a pure hash of a directory string, capped at 256 entries; retired when the lane is handed a dir-id instance",
    };

    internal static readonly IReadOnlySet<string> MutableCellAllowlist = new HashSet<string>(MutableCellReasons.Keys);

    internal static readonly IReadOnlyList<string> SourceProjects = [.. AllowedReferences.Keys];

    internal static string AssemblyFile(string project) => project switch
    {
        "Ah.Cli" => "ahd",
        "Ah.Tui" => "ahd-tui",
        _ => project,
    };
}
