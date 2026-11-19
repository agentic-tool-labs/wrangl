using Ah.Watch;

namespace Ah.Commands;

/// <summary>What <c>roster wake</c> and <c>roster overdue</c> read and write. None of it makes a daemon request, except <see cref="LaunchHeal"/>, which asks for the health once before it starts anything.</summary>
public interface IRosterWake
{
    /// <summary>The user-level ah directory the wake files live under.</summary>
    string AhHome { get; }

    /// <summary>Starts <c>ahd heal</c> detached when the daemon is there and does not answer; the heal's pid, or null when nothing was started.</summary>
    int? LaunchHeal(string trigger) => null;

    /// <summary>The address and directory of the <c>orchestrator@</c> box the session's registration holds; null when it holds none.</summary>
    (string Address, string Dir)? OrchestratorBox(string sessionId);

    /// <summary>The address and directory of the member's own box (<c>name@team</c>) the session's registration holds; null when it holds none.</summary>
    (string Address, string Dir)? MemberBox(string sessionId);

    /// <summary>The tracked jobs of <paramref name="team"/> under <paramref name="hierarchyDir"/>.</summary>
    IReadOnlyList<JobFact> Jobs(string hierarchyDir, string team);

    /// <summary>The unacknowledged ahd events in the box addressed to nobody else, oldest first.</summary>
    IReadOnlyList<WakeEvent> Unacked(string boxDir, string sessionId);

    /// <summary>Every ahd event in the box with the dispatch and step it names and when it was acknowledged, oldest first.</summary>
    IReadOnlyList<AhdEventRow> Events(string boxDir, string sessionId);

    /// <summary>The open dispatches of <paramref name="team"/> read from the request files in <paramref name="hierarchyDir"/>, with the gate and peer rows the ETA state needs.</summary>
    IReadOnlyList<OverdueDispatch> OpenDispatches(string hierarchyDir, string team, string sessionId);

    /// <summary>The queued items of <paramref name="team"/> that the files show stalled (a live idle member of the item's role, no open dispatch to it), each with when ahd owed its alarm. <paramref name="pool"/> is where the team's progress lives.</summary>
    IReadOnlyList<OverdueQueueItem> QueueStalls(string pool, string hierarchyDir, string team, double nowMs);

    /// <summary>The alarm steps ahd owes for the open jobs of <paramref name="team"/> (open by the one shared rule), each with when it was owed.</summary>
    IReadOnlyList<OverdueJobItem> JobStalls(string hierarchyDir, string team, double nowMs);

    /// <summary>The lanes of <paramref name="team"/> in a state that can go stale, each with what ahd owes once its status record is stale and the Orchestrator's open charter request to its sub-orchestrator.</summary>
    IReadOnlyList<OverdueLaneItem> LaneStalls(string hierarchyDir, string team, string sessionId) => [];

    /// <summary>The queued items of <paramref name="team"/> that the files show waiting for a role with no live member while the team is under its session budget, as role and item key.</summary>
    IReadOnlyList<(string Role, string Item)> QueueUnstaffed(string pool, string hierarchyDir, string team, double nowMs) => [];

    /// <summary>Appends one row to the stall journal in the state database.</summary>
    void Journal(Ah.Core.Json.JsonObject row);

    /// <summary>The stall journal's rows, oldest first, as JSON text; none when the database cannot be read.</summary>
    IReadOnlyList<string> JournalRows();

    /// <summary>The stall, overdue or wake document stored under <paramref name="key"/> in the state database; null when there is none.</summary>
    string? ReadState(string key);

    /// <summary>Stores <paramref name="value"/> under <paramref name="key"/> (a <c>stall.</c>, <c>overdue.</c> or <c>wake.printed.</c> key); null deletes it.</summary>
    void WriteState(string key, string? value);

    /// <summary>The <c>orchestrator@</c> box of <paramref name="team"/>, found through the team file's Orchestrator session; null when it has none.</summary>
    (string Address, string Dir, string Session)? OrchestratorBoxOfTeam(string hierarchyDir, string team);

    /// <summary>Whether <paramref name="job"/> is open by the one shared rule: not ended, and running, or with no process yet and not past the unseen grace.</summary>
    bool JobOpen(JobFact job);

    /// <summary>Whether pid <paramref name="pid"/> is running and is not a reused pid for the recorded start.</summary>
    bool PidLive(int pid, string? recordedStart);

    /// <summary>The §3 verdict of member <paramref name="member"/> of <paramref name="team"/> as one line: status and why.</summary>
    string MemberVerdict(string hierarchyDir, string team, string member);

    /// <summary>Whether the daemon the run file names is a running process.</summary>
    bool DaemonUp();

    int Pid { get; }

    /// <summary>This process, its ancestors and its descendants, as far as the process table says.</summary>
    IReadOnlySet<int> Kin() => new HashSet<int> { Pid };

    /// <summary>The recorded start of process <paramref name="pid"/>, or null when it cannot be read.</summary>
    string? StartOf(int pid);

    string? ReadText(string path);

    void WriteText(string path, string text);

    void Remove(string path);
}
