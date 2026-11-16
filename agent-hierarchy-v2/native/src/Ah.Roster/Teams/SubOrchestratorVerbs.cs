namespace Ah.Roster;

/// <summary>
/// The owner verbs a sub-orchestrator may run on its own child team. The command's owner check and the hook that guards the CLI
/// both read this one list.
/// </summary>
internal static class SubOrchestratorVerbs
{
    internal static readonly IReadOnlySet<string> Carved = new HashSet<string>(StringComparer.Ordinal)
    {
        "create", "spawn-one", "spawn-ad-hoc", "adopt", "untrack", "dismiss", "disband", "progress", "move", "stream-open", "stream-done",
    };

    /// <summary>The only owner verbs the owner of a lane's parent team may run on that lane's child team.</summary>
    internal static readonly IReadOnlyList<string> ParentOwned = ["spawn-one", "spawn-ad-hoc", "dismiss", "move"];
}
