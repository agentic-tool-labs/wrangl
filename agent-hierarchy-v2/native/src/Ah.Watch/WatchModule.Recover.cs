namespace Ah.Watch;

public static partial class WatchModule
{
    /// <summary>The state of a bare-member recovery record whose team was held when the member was found.</summary>
    public const string RecoveryHeldState = RecoveryState.HeldState;

    /// <summary>What a relaunch error starts with when the member's pane could not be read; the recovery asks the Orchestrator rather than retry.</summary>
    public const string RecoveryPaneUnknownPrefix = RecoveryState.PaneUnknownPrefix;

    /// <summary>What a relaunch error starts with when herdr refused the launch; the recovery asks the Orchestrator rather than retry.</summary>
    public const string RecoveryRefusalPrefix = RecoveryState.RefusalPrefix;

    /// <summary>Whether a recovery record in <paramref name="state"/> means the member is being brought back (or waits on a hold), so a dead registration is expected.</summary>
    public static bool RecoveryCovers(string? state) => RecoveryState.Covers(state);

    /// <summary>A short, fixed name for a hierarchy directory, the same for every spelling of it.</summary>
    public static string RecoverDirKey(string hierarchyDir) => RecoverPaths.DirKey(hierarchyDir);
}
