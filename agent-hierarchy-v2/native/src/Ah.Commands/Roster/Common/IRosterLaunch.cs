using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// The launch layer the team verbs run on: the member launcher, the launch shapes, the layout rules, the mux transport
/// and herdr readers, the teardown reads, and the member rules and registry they all judge by. The composition root
/// builds one when the first verb that needs it asks.
/// </summary>
public interface IRosterLaunch
{
    IMemberLauncher Launcher { get; }

    ISpawnShapes Shapes { get; }

    ILayoutRules Layout { get; }

    IMuxTransports Transports { get; }

    IHerdr Herdr { get; }

    IHerdrTopology Topology { get; }

    ITeardown Teardown { get; }

    IMemberRules Rules { get; }

    IMemberRegistry Registry { get; }

    IKindHarnesses Harnesses { get; }

    /// <summary>Whether a harness has been told to trust a working directory.</summary>
    ICodexTrust Trust { get; }

    /// <summary>The snapshot documents of teams and the process reads that tell whether a recorded session is alive.</summary>
    ISnapshotBuilder Snapshots { get; }

    /// <summary>What a resume does with each member of a snapshot, and the names a snapshot file goes by.</summary>
    ISnapshotRules SnapshotRules { get; }

    /// <summary>The team file's member rows, rewritten with their side effects: the links and activity records of members that left.</summary>
    ITeamRowStore TeamRows { get; }

    /// <summary>Ends the mesh links of members that left a team.</summary>
    IRosterLinks Links { get; }

    /// <summary>The pure stream rules: a team's streams, their members' statuses, the glyph, and the open exchanges.</summary>
    IStreamRules Streams { get; }

    /// <summary>Starts a terminal program (git, for a stream's worktree) and collects its output.</summary>
    IProcessRunner Processes { get; }

    /// <summary>The locks a resume takes, one per team, taken over as stale after five minutes.</summary>
    ILockProvider ResumeLocks { get; }

    /// <summary>What the multiplexer and the process table say about a member's pane; null where the composition has no such read.</summary>
    IPaneState? PaneState => null;

    /// <summary>ahd only: <see cref="PaneState"/> read through the herdr flavour <paramref name="teamRecord"/> stores, not the env's.</summary>
    IPaneState? PaneStateFor(JsonValue? teamRecord) => PaneState;

    /// <summary><see cref="PaneState"/> read through herdr flavour <paramref name="flavour"/>; null is the env's.</summary>
    IPaneState? PaneStateFor(string? flavour) => PaneState;

    /// <summary>The herdr adapter for flavour <paramref name="flavour"/>; null is the env's <see cref="Herdr"/>.</summary>
    IHerdr HerdrFor(string? flavour) => Herdr;

    /// <summary>
    /// The herdr adapters of every server <paramref name="team"/>'s members live on, once each: <see cref="Herdr"/> first when one of them
    /// is its flavour, then the team's own (<see cref="Ah.Core.Mux.HerdrFlavour.RecordedOfTeam"/>, where its untagged members resolve), then one
    /// per member-row tag (<see cref="Ah.Core.Mux.HerdrFlavour.OfMember"/>). A team that names no flavour hosts its untagged members (or, with
    /// none, itself) on <paramref name="sessionFlavour"/> (<see cref="Ah.Core.Mux.HerdrFlavour.SessionFlavour"/>); null there adds no server.
    /// </summary>
    static IReadOnlyList<IHerdr> TeamHerdrs(IRosterLaunch launch, JsonValue team, string? sessionFlavour)
    {
        var rows = team.Member("members") is { IsArray: true } members ? members.AsArray() : [];
        var ofTeam = team.IsMap ? Ah.Core.Mux.HerdrFlavour.RecordedOfTeam(team) : null;
        var ofMembers = rows.Select(static r => Ah.Core.Mux.HerdrFlavour.OfMember(r)).ToList();

        // ah2-143: an untagged team says nothing about its server, so its untagged members live on the session's own herdr, and a call goes
        // to no herdr that neither the session nor a stamp names (b483: a desktop team is not asked about on the TUI herdr).
        var untaggedHost = ofTeam ?? (ofMembers.Count == 0 || ofMembers.Contains(null) ? sessionFlavour : null);
        var flavours = ofMembers.Prepend(untaggedHost).Where(static f => f is not null).Distinct(StringComparer.Ordinal).ToList();
        List<IHerdr> all = flavours.Contains(launch.Herdr.Flavour, StringComparer.Ordinal) ? [launch.Herdr] : [];
        var seen = new HashSet<string?>(StringComparer.Ordinal) { launch.Herdr.Flavour };
        foreach (var flavour in flavours)
        {
            if (seen.Add(flavour))
            {
                all.Add(launch.HerdrFor(flavour));
            }
        }

        return all;
    }

    /// <summary>
    /// Records that <paramref name="server"/> lists tab <paramref name="tabId"/>. A tab id another server also lists maps to null: herdr
    /// servers number tabs on their own, so that id names no one tab and is acted on nowhere.
    /// </summary>
    static void NoteTab(Dictionary<string, IHerdr?> tabs, string tabId, IHerdr server)
    {
        if (!tabs.TryAdd(tabId, server) && !ReferenceEquals(tabs[tabId], server))
        {
            tabs[tabId] = null;
        }
    }

    /// <summary>
    /// The herdr flavour of the existing agent whose session is <paramref name="session"/> on team <paramref name="teamFile"/>:
    /// <see cref="Ah.Core.Mux.HerdrFlavour.Resolve"/> over its registration, the row of member <paramref name="name"/>, the team record, and the env's flavour.
    /// </summary>
    static string? FlavourOf(IRosterCliServices services, string dir, string? teamFile, string? session, string? name = null) =>
        Ah.Core.Mux.HerdrFlavour.Resolve(
            string.IsNullOrEmpty(session) ? null : services.Mailbox?.ReadRegistration(session),
            services.Roster.ReadTeamRecord(dir, teamFile),
            services.Launch?.Herdr.Flavour,
            string.IsNullOrEmpty(name) ? null : (JsonValue?)services.Roster.TeamMemberByName(dir, name, teamFile));
}

/// <summary>What a multiplexer listing says about one pane. <c>Unknown</c> means the listing could not be had; it is never the same as <c>Absent</c>.</summary>
public enum PaneKnowledge
{
    Present,
    Absent,
    Unknown,
}

/// <summary>One pane's state, and the pid of the process it started when it is present and the listing carried it.</summary>
public sealed record PaneReading(PaneKnowledge State, int? RootPid, string? Reason = null);

/// <summary>One pane of a tab, with the pid of the process it started when the listing carried it.</summary>
public sealed record TabPane(string PaneId, int? RootPid);

/// <summary>
/// Read-only questions about a member's pane. The multiplexer is listed at most once per transport per verb run, and the process
/// table at most once, however many members ask.
/// </summary>
public interface IPaneState
{
    /// <summary>The state of pane <paramref name="transportId"/> on <paramref name="transport"/>.</summary>
    PaneReading Pane(string transport, string transportId);

    /// <summary>The panes of tab <paramref name="tabId"/> on <paramref name="transport"/>; null when the listing could not be had.</summary>
    IReadOnlyList<TabPane>? TabPanes(string transport, string tabId);

    /// <summary>Whether the process <paramref name="rootPid"/> or any process below it is a harness session, by its command line; null when the process table could not be read.</summary>
    bool? HarnessRunning(int rootPid);

    /// <summary>The rows of <c>herdr agent list</c>, read fresh; null when they cannot be read.</summary>
    JsonArray? Agents();

    /// <summary>Drops the multiplexer listings read so far, so the next question lists again. The relaunch step calls it once, before its first relaunch.</summary>
    void Refresh();
}
