using Ah.Abstractions;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Core.Time;

namespace Ah.Roster;

/// <summary>The team-file reads of the Node <c>lib-roster</c> that more than one area shares. The same object is the <see cref="ITeamFiles"/> Ah.Config reads through.</summary>
public interface IRosterCore : ITeamFiles
{
    /// <summary><c>teams/&lt;team&gt;.json</c> (or <c>team.json</c> for the default team) in the team home, falling back to the pool's own legacy copy.</summary>
    string TeamPath(string dir, string? team = null);

    /// <summary><c>teams/&lt;team&gt;.json</c> in <paramref name="dir"/> itself, with no team-home lookup: where a linked tree's own record lives.</summary>
    string TeamPathAt(string dir, string team);

    /// <summary>Whether the team of <paramref name="path"/> exists: its row once the database holds the teams, else its file. A store that cannot answer throws.</summary>
    bool TeamExistsAt(string path);

    /// <summary>The counter that moves on any team write once the database holds the teams; null while the files do.</summary>
    long? TeamsChangeToken();

    /// <summary>Drops the team documents held by (path, digest), so each is parsed again on its next read (the on-demand rebuild).</summary>
    void ForgetTeamDocs()
    {
    }

    /// <summary>What changes when the team of <paramref name="path"/> may have: the change token once the database holds the teams, else the file's stamp.</summary>
    string TeamStamp(string path);

    /// <summary>The team names in <paramref name="dir"/> itself once the database holds the teams; null while the files do.</summary>
    IReadOnlyList<string>? TeamNamesIn(string dir);

    IReadOnlyList<string> ListTeamNames(string dir);

    /// <summary>The team file's record, or <see cref="JsonValue.Null"/> when it is absent or holds no members array.</summary>
    JsonValue ReadTeamRecord(string dir, string? team = null);

    /// <summary>
    /// The teams session <paramref name="sessionId"/> owns, as a fact about that session: its record names the session, or its
    /// recorded pid and start are the session's, and the session has a live registration. It says nothing about who is asking,
    /// so it grants nothing; an authority check is <c>OwnedTeams</c> with the caller's own pid.
    /// </summary>
    IReadOnlyList<string?> TeamsOfSession(string hierarchyDirectory, string sessionId);

    /// <summary>The directory that holds a team's hold file, its resume plans, stop evidence and archived holds.</summary>
    string HoldDir(string dir, string? team);

    /// <summary>The path of the team's own <c>hold.json</c>, whether or not it exists.</summary>
    string HoldPath(string dir, string? team);

    /// <summary>
    /// The one hold predicate: the hold that governs <paramref name="team"/> (its own file, else the nearest ancestor's), or null when
    /// the team is running. A file that exists but cannot be read holds the team as a <c>quit</c> (fail closed).
    /// </summary>
    EffectiveHold? HoldOf(string dir, string? team);

    /// <summary>The team's own hold (never an ancestor's), read as <see cref="HoldOf"/> reads it, or null when it has none.</summary>
    EffectiveHold? OwnHold(string dir, string? team);

    /// <summary>
    /// The one write of a team's own hold: in one transaction <paramref name="decide"/> gets the hold's body (null when there is none) and
    /// returns what to write, or null to write nothing. Throws <see cref="InvalidOperationException"/> when it cannot be written.
    /// </summary>
    void ChangeHold(string dir, string? team, Func<string?, HoldWrite?> decide);

    /// <summary>The member's pause (b456) in the team of <paramref name="dir"/>, or null when it was never paused or the store cannot answer.</summary>
    MemberPause? PauseOf(string dir, string? team, string member) => null;

    /// <summary>Records <paramref name="member"/> paused or resumed at <paramref name="atMs"/>; throws <see cref="InvalidOperationException"/> when it is not written.</summary>
    void SetPause(string dir, string? team, string member, bool paused, long atMs) =>
        throw new InvalidOperationException("the member pause needs the state database, which this composition does not provide");

    /// <summary>The team plus every lane below it, the teams one hold on <paramref name="team"/> covers.</summary>
    IReadOnlyList<string?> HoldCovered(string dir, string? team);

    /// <summary>The team's <see cref="TeamLife"/> by this core's registrations and process table.</summary>
    TeamLife TeamLifeOf(JsonValue team);

    /// <summary>The team's <see cref="TeamLife"/> with the sentence that says why, for a refusal to print.</summary>
    (TeamLife Life, string Why) TeamLifeWhy(JsonValue team);

    /// <summary>True only when <see cref="TeamLifeOf"/> proves the team orphaned; an indeterminate team is not.</summary>
    bool TeamIsOrphaned(JsonValue team);

    /// <summary>
    /// <c>paneResolver</c>: every team file read once, then a function from a pane id to the team and member row that hold it.
    /// Null for no pane id, no match, or a pane id that two rows claim. With <paramref name="liveOnly"/>, teams whose
    /// orchestrator is gone are left out.
    /// </summary>
    Func<string?, PaneMatch?> PaneResolver(string dir, bool liveOnly = false);

    /// <summary>The team that lists <paramref name="name"/> as a member: the default team first, then each named team.</summary>
    MemberTeamLookup ResolveMemberTeam(string dir, string? name);

    /// <summary>The member row of the team whose derived name is <paramref name="name"/>, or <see cref="JsonValue.Null"/>.</summary>
    JsonValue TeamMemberByName(string dir, string? name, string? team = null);

    /// <summary>A team file read by its own path with no home mapping; <see cref="JsonValue.Null"/> when absent or not a record.</summary>
    JsonValue ReadTeamFileAt(string path);

    /// <summary>A team file's record and the digest of the bytes it was parsed from: <c>{doc, digest}</c>.</summary>
    JsonObject ReadTeamWithDigest(string path);

    /// <summary><c>{state, path, team}</c>, state <c>absent</c>, <c>unusable</c> (a file that holds no record) or <c>usable</c>.</summary>
    JsonObject TeamFileState(string dir, string? team = null);

    /// <summary>
    /// The three-way read of the team file at <paramref name="path"/> (<c>{state, path, team}</c>): <c>absent</c> when there is no file,
    /// or it was removed before it could be read; <c>usable</c> when it holds an object with an array <c>members</c>; <c>unusable</c> when
    /// it exists but cannot be read, is not valid JSON, or is not such an object. A gate treats <c>unusable</c> as a refusal and <c>absent</c> as no team.
    /// </summary>
    JsonObject TeamFileStateAt(string path);

    /// <summary>The team home of a hierarchy pool dir: the main checkout's when <paramref name="dir"/> is a linked worktree's.</summary>
    string TeamHome(string dir);

    /// <summary>The pool's own copy of a team file, ignoring the home.</summary>
    string PoolTeamPath(string dir, string? team = null);

    /// <summary>The snapshot files of a team, newest first: <c>{path, file, ts}</c>.</summary>
    IReadOnlyList<JsonObject> ListSnapshots(string dir, string teamName);

    /// <summary>A snapshot document, or <see cref="JsonValue.Null"/> when absent or not a snapshot.</summary>
    JsonValue ReadSnapshot(string path);

    /// <summary>The directory holding a pool's snapshot files.</summary>
    string SnapshotsHome(string dir);

    /// <summary>The member that was renamed away from <paramref name="name"/>, or <see cref="JsonValue.Null"/>.</summary>
    JsonValue TeamMemberRenamedFrom(string dir, string? name, string? team = null);

    /// <summary>The named-slot members (peer and pane routes) of a role.</summary>
    JsonArray TeamMembersForRole(string dir, string? role, string? team = null);

    /// <summary>The distinct non-empty member names of one team.</summary>
    JsonArray TeamMemberNameSet(string dir, string? team = null);

    /// <summary>The teams among <paramref name="teams"/> (null for the default team) whose members include <paramref name="name"/>.</summary>
    JsonArray TeamsWithMember(string dir, IReadOnlyList<string?> teams, string? name);

    /// <summary>The naming prefix of a derived member name <c>&lt;prefix&gt;-&lt;role&gt;[-N]</c>, or null.</summary>
    string? MemberNamePrefixOf(JsonValue? name, JsonValue? role);

    /// <summary>
    /// The teams a sub-orchestrator name <c>&lt;team&gt;-so-&lt;stream&gt;</c> can be read as: every split at a <c>-so-</c> whose
    /// team exists and has that stream open. One entry is a name that reads one way; more is ambiguous; none is unknown.
    /// </summary>
    IReadOnlyList<string> SubOrchestratorTeams(string hierarchyDirectory, string memberName);

    /// <summary>
    /// The team whose <c>parent</c> is <c>{manager, stream}</c>, found by scanning the team files of the main pool. With several (an
    /// old child left behind), the one whose owner is not gone, then the newest <c>created</c>. Null when there is none.
    /// </summary>
    string? FindChildTeam(string hierarchyDirectory, string manager, string stream);

    /// <summary>The roster block a live team was built from: the key its file records (null: the default block).</summary>
    string? TeamRosterKey(string dir, string? teamName);

    /// <summary>
    /// Which team file a command with no <c>--team</c> operates on: <c>{team, defaulted}</c>, plus <c>unnamable</c> and
    /// <c>suggested</c> when the prefix cannot name a team, and <c>unreadable</c> when the legacy file holds no record.
    /// </summary>
    JsonObject DefaultTeamScope(string dir, JsonValue prefix, Func<JsonValue, string?> suggestTeamAlias);

    /// <summary>
    /// The member row of a team that sits in pane <paramref name="paneId"/>, or <see cref="JsonValue.Null"/>. Without a pane match, a
    /// <paramref name="marker"/> finds the row of its name when that row is starting and unregistered, has no pane or this one, and no process or this one.
    /// </summary>
    JsonValue SessionMemberRow(JsonValue team, string? paneId, MemberMarker? marker = null, string? teamName = null);

    /// <summary>The team that holds a session by role: <c>{teamName, team}</c>, or <see cref="JsonValue.Null"/> when none or ambiguous.</summary>
    JsonValue ResolveSessionTeam(string dir, string? role, string? explicitTeam = null);

    /// <summary>The team attribution of a session: the launch file, then its pane, then a role scan. Null when none.</summary>
    JsonValue AttributeSessionTeam(string dir, string? role, string? explicitTeam = null, string? paneId = null, IReadOnlyList<string>? homes = null);

    /// <summary>Which team and member row hold a session, and why not when none does.</summary>
    JsonObject LocateSession(string dir, IReadOnlyList<string> homes, string? teamArg, bool teamDefaultExplicit, string? paneId, MemberMarker? marker = null);

    /// <summary>The checkout a session is expected to run in: its row's recorded root, else the launcher's, else the team's.</summary>
    JsonValue ExpectedRootFor(JsonValue team, JsonValue member, string? envRoot = null);

    /// <summary>The pool's own copy of a team when a copy also sits in the team home: <c>{path, team}</c>, or null.</summary>
    JsonValue SplitTeamCopy(string dir, string? team = null);

    /// <summary>The team file a session was launched into (<c>WRANGL_TEAM_FILE</c> or <paramref name="value"/>): <c>{home, teamName}</c>, <c>{invalid, kind, value}</c>, or null.</summary>
    JsonValue EnvTeamFileFor(IReadOnlyList<string> homes, string? value = null);

    /// <summary>Whether <paramref name="invoker"/> owns the team: the same session, or the same process with no conflicting session.</summary>
    bool IsTeamOwnedBy(JsonValue team, TeamInvoker invoker);

    /// <summary><c>team-history.json</c> of the team home.</summary>
    string HistoryPath(string dir);

    /// <summary><c>{version, teams}</c>, always: a missing or corrupt file reads back as empty.</summary>
    JsonObject ReadHistory(string dir);

    /// <summary>The config-only fingerprint of a roster, eight hex digits.</summary>
    string Fingerprint(JsonValue rosterLevel, JsonValue transport, JsonValue? members);

    /// <summary>Whether a history entry is the one behind the currently live team of its alias.</summary>
    bool HistoryEntryIsActive(string dir, JsonValue entry, DateTimeOffset now);

    /// <summary>
    /// A team is live when <paramref name="invoker"/> owns it, or when its orchestrator pid is alive and the team is no more than
    /// a day old at <paramref name="now"/>.
    /// </summary>
    bool TeamIsLive(JsonValue team, TeamInvoker? invoker, DateTimeOffset now);
}

/// <summary>The team and member row that hold a pane. <see cref="TeamName"/> is null for the default team.</summary>
public sealed record PaneMatch(string? TeamName, JsonValue Team, JsonValue Member);

/// <summary>A lane's leader as the team records name it: the managing team, the stream, the sub-orchestrator's member name, and whether the lane is open to spawning.</summary>
public sealed record LaneLeader(string Manager, string Stream, string Member, bool Open);

/// <summary>A member row of <paramref name="Home"/> whose lead names <paramref name="Lane"/>; <paramref name="Dangling"/> when the lane no longer backs that lead.</summary>
public sealed record LentRow(string Home, string Name, string? Role, string Lane, JsonValue Row, bool Dangling);

/// <summary>Which team lists a member name. <see cref="Team"/> is null for the default team; branch on <see cref="Found"/>.</summary>
public sealed record MemberTeamLookup(bool Found, string? Team);

public static class RosterModule
{
    public static ModuleRegistry Registry => GeneratedRegistry.Instance;

    /// <summary><c>{deputy, since, cause}</c> of the team's duty when its deputy names a member row; null otherwise (the views' read of <c>duty</c>).</summary>
    public static JsonObject? DutyOf(JsonValue team) => DutyView.Of(team);

    /// <summary>The pane rule of <c>IRosterCore.PaneResolver</c> over team records the caller already holds (status entries).</summary>
    public static Func<string?, PaneMatch?> PaneResolverOver(IReadOnlyList<(string? Name, JsonValue Team)> teams) => RosterCore.PaneResolverOver(teams);

    /// <summary>The code of the refusal every launch path gives for a held team; a failure text that starts with it is not a launch failure.</summary>
    public const string HoldRefusalCode = TeamHoldFiles.RefusalCode;

    /// <summary><c>yyyyMMdd'T'HHmmssfff'Z'</c>, the stamp checkpoint and hold records use.</summary>
    public static string HoldStamp(DateTimeOffset at) => TeamHoldFiles.Stamp(at);

    /// <summary>What the files show about a member the classifier stopped: its launch file, sandbox and the member surface.</summary>
    public static PermissionFacts ReadPermissionFacts(IFileReader files, string? pluginRoot, string teamHome, string member, string? deniedCommand) =>
        PermissionCause.ReadFacts(files, pluginRoot, teamHome, member, deniedCommand);

    /// <summary>The settings scopes that merge into a member session (the launch file, the work tree's settings and local settings, the user's), and whether a managed-settings path exists.</summary>
    public static SessionSettings ReadSessionSettings(IFileReader files, string home, string teamHome, string member, string workRoot) =>
        MemberPermissionsInputs.ReadSession(files, home, teamHome.TrimEnd('/') + "/launch/" + member + ".settings.json", workRoot);

    /// <summary>The first cause that holds for a denial given the facts and the member-launch consent.</summary>
    public static PermissionVerdict PermissionCauseOf(PermissionFacts facts, MemberLaunchVerdict consent) => PermissionCause.CauseOf(facts, consent);

    /// <summary>The cause of a denial of <paramref name="deniedCommand"/> for <paramref name="member"/>, read from the files.</summary>
    public static PermissionVerdict DiagnosePermission(
        IFileReader files, string? pluginRoot, string teamHome, string asksPath, bool memberAllowEnabled, string member, string deniedCommand, out PermissionFacts facts, string? memberAllowOnAt = null) =>
        PermissionCause.Diagnose(files, pluginRoot, teamHome, asksPath, memberAllowEnabled, member, deniedCommand, out facts, memberAllowOnAt);

    /// <summary><c>member_allow.reask_at</c> of the user's config file (when the user last ran <c>ah allow on</c> over a decline), or null.</summary>
    public static string? MemberAllowOnAt(IFileReader files, string? userConfigPath) => PermissionCause.MemberAllowOnAt(files, userConfigPath);

    /// <summary><c>member_allow.enabled</c> of the first of the given config files that sets it; true unless it is set to false.</summary>
    public static bool MemberAllowEnabled(IFileReader files, IEnumerable<string?> configPaths) => PermissionCause.MemberAllowEnabled(files, configPaths);

    /// <summary>The permissions request for a member, built from its own row and the files that describe its checkout.</summary>
    public static MemberPermissionsRequest BuildMemberPermissionsRequest(
        IFileReader files,
        string home,
        string launchCwd,
        string mainCheckout,
        string pluginRoot,
        string ahPath,
        string member,
        string role,
        string workRoot,
        string? memberTmp) =>
        MemberPermissionsInputs.Build(files, home, launchCwd, mainCheckout, pluginRoot, ahPath, member, role, workRoot, memberTmp);

    /// <summary>The <c>--team</c> value that names the default team (<c>team.json</c>).</summary>
    public const string DefaultTeamArg = "@default";

    /// <summary>A team name as the user passes it back with <c>--team</c>: the default team is <c>@default</c>.</summary>
    public static string TeamArgName(string? team) => team ?? DefaultTeamArg;

    /// <summary>How a list of team names reads in a refusal: <c>"foo", "bar"</c>, the default team as <c>"@default"</c>.</summary>
    public static string TeamListText(IEnumerable<string?> teams) => string.Join(", ", teams.Select(n => $"\"{TeamArgName(n)}\""));

    /// <summary>
    /// Whether a team record's Orchestrator is provably live: its session's live registration (<paramref name="liveRegistration"/>,
    /// pid and start) is that process now, or its recorded pid still has its recorded start. A pid that is merely alive proves
    /// nothing. Indeterminate when there is no record or no <paramref name="table"/>.
    /// </summary>
    public static TeamLife TeamLifeOf(JsonValue team, Func<string, (long Pid, string Start)?>? liveRegistration, IProcessTable? table, IEnumerable<(long Pid, string? Start)>? registered = null) =>
        TeamLives.Of(team, liveRegistration, table, DateTimeOffset.UtcNow, Environment.ProcessId, registered);

    /// <summary>Whether a team record's <c>orchestrator</c> carries any claim on the team: an absent, null or empty one does not.</summary>
    public static bool OrchestratorIsClaimed(JsonValue? orchestrator) => TeamLives.IsClaimed(orchestrator);

    /// <summary>The hierarchy dir a team file path sits in (the inverse of <c>TeamPath</c>), or null when it is not shaped like one.</summary>
    public static string? TeamFileHome(string path) => RosterCore.TeamFileHome(path);

    /// <summary>Whether a sub-orchestrator may run this owner verb on its own child team.</summary>
    public static bool IsSubOrchestratorVerb(string verb) => SubOrchestratorVerbs.Carved.Contains(verb);

    /// <summary>Every owner verb a sub-orchestrator may run on its own child team.</summary>
    public static IReadOnlyCollection<string> CarvedVerbs => SubOrchestratorVerbs.Carved;

    /// <summary>The only owner verbs the owner of a lane's parent team may run on that lane's child team.</summary>
    public static IReadOnlyList<string> ParentOwnedVerbs => SubOrchestratorVerbs.ParentOwned;

    /// <summary>The member and open-stream caps of the lane run on <paramref name="stream"/> of <paramref name="manager"/>; the defaults when the stream has no lane record.</summary>
    public static (int Members, int Streams) LaneCaps(JsonValue manager, string stream) => LaneRecord.Caps(manager, stream);

    /// <summary>The member rows of a team document, live or not: a member counts from the moment its row is written until it is dismissed.</summary>
    public static int LaneMemberRows(JsonValue team) => team.Member("members") is { IsArray: true } list ? list.AsArray().Count(m => m.IsMap) : 0;

    /// <summary>Whether a lane holding <paramref name="rows"/> member rows has no room for another when its cap is <paramref name="max"/>.</summary>
    public static bool LaneRowsAtCap(int rows, int max) => rows >= max;

    /// <summary>The smallest and largest cap a lane may be given, and the caps a lane without a record gets.</summary>
    public const int LaneCapMin = LaneRecord.CapMin;

    public const int LaneCapMax = LaneRecord.CapMax;

    public const int LaneDefaultMembers = LaneRecord.DefaultMembers;

    public const int LaneDefaultStreams = LaneRecord.DefaultStreams;

    /// <summary>The most live sub-orchestrators <paramref name="team"/> may have, whether that is the default, and the text of a recorded value that was out of range (read as the default); null when none.</summary>
    public static (int Value, bool IsDefault, string? Rejected) MaxLanes(JsonValue team) => LaneRecord.MaxLanes(team);

    /// <summary>
    /// The cap that holds for <paramref name="team"/>, with its source (<c>team</c>, <c>roster &lt;block&gt;</c> or <c>default</c>), and the text of an out-of-range value
    /// skipped at the team record or in <paramref name="roster"/> (the roster block the team was built from, as the roster resolution returns it, or null).
    /// </summary>
    public static (int Value, string Source, string? RejectedTeam, string? RejectedRoster) EffectiveMaxLanes(JsonValue team, JsonValue roster) =>
        LaneRecord.Effective(team, roster);

    /// <summary>How long a chain must be seen Down before a member is told to wind down.</summary>
    public const double ChainDownGraceMs = 120_000;

    /// <summary>
    /// Whether the latest <c>chain-down</c> peer row of <paramref name="sessionId"/> in <paramref name="peerRecords"/> (in append order)
    /// is <c>active</c>: the session has been told to wind down, and nothing may nudge it to keep working.
    /// </summary>
    public static bool ChainDownActive(IEnumerable<JsonValue> peerRecords, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(peerRecords);
        var active = false;
        foreach (var r in peerRecords)
        {
            if (r is { IsMap: true } && r.Member("type") is { IsText: true } t && t.AsString() == "chain-down"
                && r.Member("session_id") is { IsText: true } s && s.AsString() == sessionId)
            {
                active = r.Member("state") is { IsText: true } st && st.AsString() == "active";
            }
        }

        return active;
    }

    /// <summary>
    /// The newest moment a member of <paramref name="team"/> was told to wind down: the latest <c>at</c> of its <c>active</c> rows. A later
    /// <c>cleared</c> row does not cancel one, since the wind-down still happened. A row whose <c>at</c> cannot be parsed is ignored.
    /// </summary>
    public static DateTimeOffset? LastChainDownAt(IEnumerable<JsonValue> peerRecords, string team)
    {
        ArgumentNullException.ThrowIfNull(peerRecords);
        DateTimeOffset? last = null;
        foreach (var r in peerRecords)
        {
            if (r is { IsMap: true } && r.Member("type") is { IsText: true } t && t.AsString() == "chain-down"
                && r.Member("state") is { IsText: true } st && st.AsString() == "active"
                && r.Member("team") is { IsText: true } rowTeam && rowTeam.AsString() == team
                && r.Member("at") is { IsText: true } at
                && DateTimeOffset.TryParse(at.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                && (last is null || when > last))
            {
                last = when;
            }
        }

        return last;
    }

    /// <summary>
    /// Whether the chain of command of the members of <paramref name="memberTeam"/> holds: <see cref="ChainKind.Down"/> only on proof
    /// (an orphaned or unclaimed Orchestrator, or a restored session <paramref name="ownerHasAhLaunch"/> shows ah did not start),
    /// <see cref="ChainKind.Unknown"/> when a link cannot be judged. A lane child is followed through its lead to the manager team.
    /// </summary>
    public static ChainState ChainOf(
        IRosterCore roster,
        string hierarchyDirectory,
        string memberTeam,
        Func<string, (long Pid, string Start)?>? liveRegistration,
        IProcessTable table,
        DateTimeOffset now,
        int observerPid,
        Func<int, string, string, string, string?, string?, bool?> ownerHasAhLaunch,
        Func<string, bool> isOrchestrateRole,
        IEnumerable<(long Pid, string? Start)>? references = null) =>
        ChainWalk.Of(roster, hierarchyDirectory, memberTeam, liveRegistration, table, now, observerPid, ownerHasAhLaunch, isOrchestrateRole, references);

    /// <summary>
    /// Whether a lane child belongs to the current incarnation of its manager team. A child made with the manager's <c>team_id</c> in its
    /// <c>parent</c> must match it; an older child is accepted only when both teams carry a <c>created</c> time and the child is not
    /// older than the manager. Anything else is not the same incarnation: a child outlives <c>disband</c> and <c>reap</c> of its manager.
    /// </summary>
    public static bool SameIncarnation(JsonValue child, JsonValue manager)
    {
        static string? Text(JsonValue? v) => v is { IsText: true } t && t.AsString().Length > 0 ? t.AsString() : null;

        if (child.Member("parent")?.Member("team_id") is { IsNull: false } recorded)
        {
            return Text(recorded) is { } id && Text(manager.Member("team_id")) == id;
        }

        return Text(child.Member("created")) is { } made && Text(manager.Member("created")) is { } born
            && DateTimeOffset.TryParse(made, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var madeAt)
            && DateTimeOffset.TryParse(born, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var bornAt)
            && madeAt >= bornAt;
    }

    /// <summary>
    /// The lane a child team is, judged from the team records alone: its manager, its stream and the manager's sub-orchestrator row
    /// for that stream. Null unless the child's <c>parent</c> link and the manager's lookup of the child agree, and the manager's
    /// row for the stream names a member whose role <paramref name="isOrchestrateRole"/> accepts. <paramref name="read"/> reads a
    /// team record by name.
    /// </summary>
    public static LaneLeader? LaneLeaderOf(IRosterCore roster, string hierarchyDirectory, string childTeam, Func<string, JsonValue> read, Func<string, bool> isOrchestrateRole)
    {
        static string? Text(JsonValue? value) => value is { IsText: true } text ? text.AsString() : null;

        var child = read(childTeam);
        if (child.IsNull || child.Member("parent") is not { IsMap: true } link || Text(link.Member("team")) is not { } managerName
            || Text(link.Member("stream")) is not { } stream || roster.FindChildTeam(hierarchyDirectory, managerName, stream) != childTeam)
        {
            return null;
        }

        var manager = read(managerName);
        var streamRecord = manager.Member("streams")?.Member(stream);
        if (Text(streamRecord?.Member("sub_orchestrator")) is not { } member
            || manager.Member("members") is not { IsArray: true } rows
            || rows.AsArray().FirstOrDefault(m => Text(m.Member("name")) == member) is not { IsMap: true } row
            || Text(row.Member("role")) is not { } role || !isOrchestrateRole(role))
        {
            return null;
        }

        var open = Text(streamRecord?.Member("state")) == "open" && Text(child.Member("workstream")?.Member("state")) != "done";
        return new LaneLeader(managerName, stream, member, open);
    }

    /// <summary>
    /// Every member row, in any team of <paramref name="hierarchyDirectory"/>, whose <c>lead</c> names a lane: the one join behind
    /// every view of lent members. A row is lent while the lane is confirmed by <see cref="LaneLeaderOf"/>, open, run by the member the
    /// lead names, and managed by the row's own team; otherwise the lead is <see cref="LentRow.Dangling"/>.
    /// </summary>
    public static IReadOnlyList<LentRow> LentMembers(IRosterCore roster, string hierarchyDirectory, Func<string, bool> isOrchestrateRole)
    {
        static string? Text(JsonValue? value) => value is { IsText: true } text ? text.AsString() : null;

        var found = new List<LentRow>();
        foreach (var home in roster.ListTeamNames(hierarchyDirectory))
        {
            if (roster.ReadTeamRecord(hierarchyDirectory, home).Member("members") is not { IsArray: true } rows)
            {
                continue;
            }

            foreach (var row in rows.AsArray())
            {
                if (!row.IsMap || Text(row.Member("name")) is not { } name || row.Member("lead") is not { IsMap: true } lead
                    || Text(lead.Member("team")) is not { } lane || Text(lead.Member("name")) is not { } leadName)
                {
                    continue;
                }

                var leader = LaneLeaderOf(roster, hierarchyDirectory, lane, team => roster.ReadTeamRecord(hierarchyDirectory, team), isOrchestrateRole);
                var lent = leader is { Open: true } && leader.Member == leadName && leader.Manager == home;
                found.Add(new LentRow(home, name, Text(row.Member("role")), lane, row, !lent));
            }
        }

        return found;
    }

    /// <summary>The ref the lane stream was opened from, or null.</summary>
    public static string? LaneBase(JsonValue manager, string stream) => LaneRecord.Base(manager, stream);

    /// <summary>The team-file row key naming the worktree a member's session is isolated in.</summary>
    public const string IsolatedTreeKey = MemberIsolation.TreeKey;

    /// <summary>The team-file row key naming the pane the isolated session ran in.</summary>
    public const string IsolatedPaneKey = MemberIsolation.PaneKey;

    /// <summary>The tree the member's session is isolated in, or null when its row records none or records it for another pane.</summary>
    public static string? IsolatedTreeOf(Ah.Core.Json.JsonValue memberRow) => MemberIsolation.TreeOf(memberRow);

    /// <summary>The member validators, reading role and kind facts through <paramref name="registry"/>.</summary>
    public static IMemberRules CreateMemberRules(IMemberRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return new MemberRules(registry);
    }

    /// <summary>The screen recognizers for Claude Code and the harnesses in the kind table.</summary>
    public static IScreenRules CreateScreenRules() => new ScreenRules();

    /// <summary>The member registry over the config library, with declared model tiers read from the user's global config file.</summary>
    public static IMemberRegistry CreateMemberRegistry(IConfigCore config, IFileReader files, IAhEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        return new ConfigMemberRegistry(config, files, environment);
    }

    /// <summary>How the Orchestrator reaches a member: peer, subagent, pane or mail.</summary>
    public static IReadOnlyList<string> RouteValues => RosterValues.RouteValues;

    /// <summary><c>--effort</c> levels.</summary>
    public static IReadOnlyList<string> EffortValues => RosterValues.EffortValues;

    /// <summary>Permission modes, the recommended one first; <c>bypassPermissions</c> is accepted only for old configs.</summary>
    public static IReadOnlyList<string> AutoModeValues => RosterValues.AutoModeValues;

    /// <summary>What happens when a member has no live instance.</summary>
    public static IReadOnlyList<string> OnMissingValues => RosterValues.OnMissingValues;

    /// <summary>The default of <see cref="OnMissingValues"/>.</summary>
    public const string OnMissingDefault = RosterValues.OnMissingDefault;

    /// <summary>The tiers an advise-class member's model must be declared at.</summary>
    public static IReadOnlyList<string> AdviseTiers => RosterValues.AdviseTiers;

    /// <summary>Snapshot files kept per team.</summary>
    public const int SnapshotKeep = RosterValues.SnapshotKeep;

    /// <summary>The age past which a team is not live by age alone, in seconds.</summary>
    public const int TeamStaleAgeSeconds = RosterValues.TeamStaleAgeSeconds;

    /// <summary>How long after its <c>spawned_at</c> a member row without a live registration is still starting rather than dead, in seconds.</summary>
    public const int StartingWindowSeconds = RosterValues.StartingWindowSeconds;

    /// <summary>
    /// How many whole seconds ago <paramref name="member"/> was spawned when that is inside the starting window, else null: the row has
    /// no <c>spawned_at</c>, it does not parse, or it is the window's age or older. The caller adds the "has no live registration" half.
    /// </summary>
    public static int? StartingAgeSeconds(JsonValue member, DateTimeOffset now)
    {
        if (member.Member("spawned_at") is not { IsText: true } at || !Timestamps.TryParseMs(at.AsString(), out var spawnedMs))
        {
            return null;
        }

        var age = (now.ToUnixTimeMilliseconds() - spawnedMs) / 1000;
        return age >= 0 && age < StartingWindowSeconds ? (int)age : null;
    }

    /// <summary>Per kind, a Claude <c>--permission-mode</c> as that kind's own flags, in the order its table lists them.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>>> KindAutoModeArgs => RosterValues.KindAutoModeArgs;

    /// <summary>The reader of Codex's trust records. <paramref name="checkoutRoot"/> gives a directory's checkout root, or null.</summary>
    public static ICodexTrust CreateCodexTrust(IFileReader files, IAhEnvironment environment, Func<string, string?> checkoutRoot)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(checkoutRoot);
        return new CodexTrust(files, environment, checkoutRoot);
    }

    /// <summary>The <c>meta</c> row of the state database that records the user's Codex trust pre-approvals (b397).</summary>
    public const string CodexTrustPreapprovalKey = "home.codex-trust-preapproval";

    /// <summary>Where every use of the Codex trust auto-answer is logged.</summary>
    public static string CodexTrustAutoAnswerLogPath(IAhEnvironment environment) => NodePath.Join(environment.AhHome, "codex-trust-autoanswer.jsonl");

    /// <summary>The decision of whether a Codex trust screen may be answered under the user's recorded pre-approvals.</summary>
    public static ICodexTrustAutoAnswer CreateCodexTrustAutoAnswer(IFileReader files, IAhEnvironment environment, ICodexTrust trust, IClock clock, IHomeDocs? docs)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(trust);
        ArgumentNullException.ThrowIfNull(clock);
        return new CodexTrustAutoAnswer(files, environment, trust, clock, docs);
    }

    /// <summary>
    /// The <c>team</c> state domain: write, clear, read, snapshot and history. <paramref name="refresher"/> is called after a team
    /// write or clear, outside the operation.
    /// </summary>
    public static IStateDomain CreateStateDomain(IRosterCore core, IClock clock, IRandom random, IStatusRefresher? refresher)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(random);
        return new TeamStateDomain(core as RosterCore ?? throw new ArgumentException("not a roster core made by CreateCore", nameof(core)), clock, random, refresher);
    }

    /// <summary>
    /// The roster core. A team's ownership is proven by its recorded Orchestrator session (through <paramref name="proofs"/>) or by its
    /// recorded pid with that process's start time (through <paramref name="processTable"/>); without either, nothing proves it.
    /// </summary>
    public static IRosterCore CreateCore(
        IFileReader files, IAhEnvironment environment, IConfigCore config, IProcessInspector processes, IProcessTable? processTable = null, SessionProofs? proofs = null, ITeamAuthority? teamAuthority = null, IHoldSource? holds = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(processes);
        return new RosterCore(files, environment, config, processes, processTable, proofs, teamAuthority, holds);
    }
}

/// <summary>
/// The member name a launcher set in a session's environment, with the process the session runs as (<paramref name="Pid"/> 0 when unknown),
/// the instant to judge the starting window at, and whether a (team, member) already holds a live registration.
/// </summary>
public sealed record MemberMarker(string Name, int Pid, string? PidStart, DateTimeOffset Now, Func<string, string, bool>? Registered = null);
