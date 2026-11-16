using Ah.Abstractions;
using Ah.Core.Json;

namespace Ah.Watch;

/// <summary>
/// What is said to a person about a member that runs without ah's launch (a bare member), and the recovery switch that decides it.
/// Manual relaunch never stops a bare process; only the daemon's re-adoption does, so every hint names that working path.
/// </summary>
public static partial class WatchModule
{
    /// <summary>The types of peer record the tick's consumers read: the outage state, the dispatch clock's own rows, and the rows the liveness clock reads. A consumer that reads another type must add it here.</summary>
    public static IReadOnlySet<string> PeerSnapshotTypes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "outage", "outage-ack", "outage-probe-failed", "overdue-resume", "stalled-after-compact", "idle-without-report", "report-acked", "heard", "unreviewed-done",
    };

    /// <summary>What recovery does when no switch row exists. This is the one place to change the default.</summary>
    public const bool RecoveryDefaultOn = true;

    /// <summary>The <c>meta</c> row of the state database that holds the recovery switch (b397).</summary>
    public const string RecoverySwitchKey = "home.recover-switch";

    /// <summary>The sentence said to the Orchestrator's own bare row, which cannot relaunch itself.</summary>
    public const string BareOrchestratorSentence = "this session is the bare Orchestrator; nothing re-adopts it";

    /// <summary>
    /// Whether recovery is on, from the <see cref="RecoverySwitchKey"/> row (no row, or no database, is <see cref="RecoveryDefaultOn"/>).
    /// A row whose <c>on</c> is a boolean says so; a row that is not a map with a boolean <c>on</c>, or a database that cannot answer,
    /// switches recovery off and the problem says why.
    /// </summary>
    public static (bool On, string? Problem) ReadRecoverySwitch(IHomeDocs? docs)
    {
        string? text = null;
        if (docs is not null && !docs.TryRead(RecoverySwitchKey, out text))
        {
            return (false, $"the {RecoverySwitchKey} row cannot be read from the state database; recovery is off");
        }

        if (text is null)
        {
            return (RecoveryDefaultOn, null);
        }

        try
        {
            return JsonText.Parse(text) is { IsMap: true } doc && doc.Member("on") is { Kind: JsonKind.Bool } on
                ? (on.IsTruthy, null)
                : (false, $"the {RecoverySwitchKey} row is not a map with a boolean \"on\"; recovery is off");
        }
        catch (JsonParseException e)
        {
            return (false, $"the {RecoverySwitchKey} row is not JSON ({e.Message}); recovery is off");
        }
    }

    /// <summary>The one sentence that says what happens to a bare member of <paramref name="team"/> and how to get it re-adopted.</summary>
    public static string BareSentence(bool recoveryOn, string team, string member) => recoveryOn
        ? $"the daemon re-adopts it once it is idle; to ask now: `ah roster recover now --team {team} {member}`"
        : "recovery is off, so nothing re-adopts it; only the user can switch it on (`ah roster recover enable`), then the daemon re-adopts it once it is idle";

    /// <summary>True for the Orchestrator's own row: by role, or by the address the team records for its Orchestrator.</summary>
    public static bool IsBareOrchestrator(string? role, string name, string? orchestratorAddress) =>
        role == "orchestrator"
        || (orchestratorAddress is not null && orchestratorAddress.StartsWith(name + "@", StringComparison.Ordinal));
}
