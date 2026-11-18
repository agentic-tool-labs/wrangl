using Ah.Abstractions;
using Ah.Mesh;
using Ah.Config;
using Ah.Hierarchy;
using Ah.Launch;
using Ah.Roster;

namespace Ah.Commands;

/// <summary>
/// What the roster CLI's verbs run on. The composition root builds one when the first roster verb runs; a verb never builds
/// a service of its own, and takes every environment variable through <see cref="Environment"/>. A service a new verb needs
/// is added here and supplied by the composition root; a verb reaches this interface only through its <c>RosterContext</c>.
/// </summary>
public interface IRosterCliServices
{
    IConfigCore Config { get; }

    /// <summary>The daemon, for a verb that asks it to act; null where there is none.</summary>
    IDaemonClient? Daemon => null;

    IRosterCore Roster { get; }

    HierarchyServices Hierarchy { get; }

    IAhEnvironment Environment { get; }

    IClock Clock { get; }

    /// <summary>Waits while a verb polls; a test advances its clock here instead.</summary>
    void Sleep(TimeSpan span) => Thread.Sleep(span);

    IFileReader Files { get; }

    /// <summary>The grants tables (per-dir-files-to-db S11e); null when the grants are read from files only, as in a test rig.</summary>
    IGrantSource? Grants => null;

    /// <summary>The home documents (b397 group 2), where the permissions ledger lives; null in a rig with no database, where it reads as never asked.</summary>
    IHomeDocs? HomeDocs => null;
    /// <summary>The bare-member recovery records (<c>recover_records</c>); null when this composition has no state database, as in a test rig.</summary>
    IRecoverRecordStore? RecoverRecords => null;

    /// <summary>What <c>process.cwd()</c> is: the directory a verb without <c>--cwd</c> works in.</summary>
    string CurrentDirectory { get; }

    /// <summary>What <c>process.ppid</c> is: the pid of the process that started this one, 0 when it cannot be read.</summary>
    int ParentPid { get; }

    /// <summary>The teardown and live-registry reads over herdr and the peer records, or null while the launch layer is not composed.</summary>
    ITeardown? Teardown { get; }

    /// <summary>The launch layer (launcher, shapes, layout, mux, member rules), or null while it is not composed.</summary>
    IRosterLaunch? Launch { get; }

    /// <summary>The mailbox registration of the calling session, or null while that module has not provided it.</summary>
    IRosterCallerRegistry? Callers { get; }

    /// <summary>The daemon's spawn pacing, or null when none is composed (then spawns are not paced).</summary>
    IRosterSpawnAdmission? SpawnAdmission => null;

    /// <summary>The services of the verbs that type into or read a member's pane, or null while they are not composed.</summary>
    IRosterPanes? Panes => null;

    /// <summary>The reader and planner of Codex's trust that <c>codex-trust</c> uses in place of the one built over the real config; null in production.</summary>
    ICodexTrust? CodexTrust => null;

    /// <summary>The <c>stream/slug</c> a work id names in <paramref name="team"/>'s checklist; null when it names none there, or no registry is composed.</summary>
    string? WorkAlias(string pool, string team, string id) => null;

    /// <summary>Closes the open backlog row <paramref name="id"/> as shipped at <paramref name="sha"/> (the release watch's own close); the failure, or null. Nothing is closed where none is composed.</summary>
    string? CloseShippedBacklog(string pool, string id, string sha) => null;

    /// <summary>Closes the open backlog row <paramref name="id"/> done with <paramref name="commits"/> and <paramref name="note"/>; the failure, or null. Nothing is closed where none is composed.</summary>
    string? CloseDoneBacklog(string pool, string id, IReadOnlyList<string> commits, string note) => null;

    /// <summary>The backlog row (<c>b&lt;N&gt;</c>) a work id names; null when it names none, or no registry is composed.</summary>
    string? WorkParent(string pool, string team, string id) => null;

    /// <summary>
    /// The team goal that applies to <paramref name="team"/> as the rules verb resolves it from <paramref name="cwd"/> (ah2-53): <c>{text, done_when, team}</c>, <c>team</c>
    /// naming whose row it is. Null with none, when it cannot be read, or where no rules read is composed.
    /// </summary>
    Ah.Core.Json.JsonObject? TeamGoal(string cwd, string team) => null;

    /// <summary>The reads only <c>roster doctor</c> makes (the directive's status text, git), or null while they are not composed.</summary>
    IRosterDoctor? Doctor => null;

    /// <summary>
    /// Starts the shell command line <paramref name="commandLine"/> detached from this process in <paramref name="cwd"/>, its output
    /// appended to <paramref name="log"/>. False when nothing was started: none is composed, or a test runs.
    /// </summary>
    bool StartDetached(string commandLine, string cwd, string log) => false;

    /// <summary>The mailbox send of events to other sessions, or null while the mail module is not composed.</summary>
    IRosterEventMail? EventMail => null;

    /// <summary>The route to the daemon that holds a Codex member's pending requests, or null while it is not composed.</summary>
    IRosterCodexAnswers? CodexAnswers => null;

    /// <summary>The route to the daemon that runs the bare-member recovery, or null while it is not composed.</summary>
    IRosterBareRecover? BareRecover => null;

    /// <summary>The proof that an owner-only verb runs through a team's Orchestrator, or null while it is not composed.</summary>
    IRosterOwnerProof? OwnerProof => null;

    /// <summary>What the waiter and the overdue timer read; null where the composition has no mailbox.</summary>
    IRosterWake? Wake => null;

    /// <summary>The effective user id of this process; null where there is none to read (Windows) or the composition does not provide it.</summary>
    uint? CurrentUserId => null;

    /// <summary>The member tmp directory operations; null where the composition has none (no member tmp is made).</summary>
    IRosterScratch? Scratch => null;

    /// <summary>The hook-error log, for the verbs that leave a row where a hook would.</summary>
    IHookErrorLog? HookErrors => null;

    /// <summary>The atomic file writer the hold verbs write hold files, plans and evidence with; null where the composition has none.</summary>
    IRawFileWriter? Writer => null;

    /// <summary>The mailbox the hold verbs drain; null where the composition has no mail.</summary>
    IMailbox? Mailbox => null;

    /// <summary>The directory that holds one mailbox box directory per member of the team whose file is <paramref name="teamFile"/>; null where the composition has no mailbox.</summary>
    string? MemberBoxRoot(string teamFile) => null;

    /// <summary>
    /// Submits the brief file <paramref name="refPath"/> to <paramref name="member"/> of <paramref name="team"/> as mail, exactly as
    /// <c>ah msg send &lt;member&gt;@&lt;team&gt; --ref &lt;file&gt; --kind brief</c> run in <paramref name="cwd"/> does. Null while the composition
    /// has no mail; throws <see cref="InvalidOperationException"/> with the command's own error when the send is refused.
    /// </summary>
    BriefSent? SendBrief(string cwd, string member, string team, string refPath) => null;
}

/// <summary>A submitted brief: the mail entry's id (null when the send exited 0 but printed no <c>entry</c> line) and the <c>delivery:</c> line it printed (null when it printed none).</summary>
public sealed record BriefSent(string? EntryId, string? DeliveryLine);

/// <summary>The scratch-directory operations plus the one git question a member tmp needs.</summary>
public interface IRosterScratch : IScratchDirectories
{
    /// <summary>What <see cref="EnsureIgnored"/> answers, as the start of its reason, when the directory is in no git repository.</summary>
    public const string NotGitRepository = "not a git repository";

    /// <summary>Whether git, run in <paramref name="repoDir"/>, ignores <paramref name="path"/>; null when git could not answer.</summary>
    bool? IsIgnored(string repoDir, string path);

    /// <summary>
    /// Makes git, run in <paramref name="repoDir"/>, ignore <paramref name="dir"/> by appending one line to the repository's own
    /// <c>info/exclude</c> when nothing ignores it already; <c>.gitignore</c> is never written. Null on success, else why not.
    /// </summary>
    string? EnsureIgnored(string repoDir, string dir);

    /// <summary>Makes <paramref name="dir"/> if it is missing and proves a file can be written in it. Null on success, else why not.</summary>
    string? EnsureWritable(string dir);
}

/// <summary>The caller of an owner-only verb as the audit row records it: its pid and start time, the ancestor chain, and the command line.</summary>
public sealed record OwnerActor(int Pid, string? Start, IReadOnlyList<ChainHop> Chain, string CommandLine);

/// <summary>
/// Proves that the <c>ah</c> process asking is the team's Orchestrator, by the nearest registered session above it. The caller
/// supplies no identity of its own: the proof reads the process table, the team files and the registrations.
/// </summary>
public interface IRosterOwnerProof
{
    /// <summary>The proof for <paramref name="team"/> of the pool <paramref name="dir"/>, against its recorded Orchestrator.</summary>
    OwnerProofResult Check(string dir, string? team);

    /// <summary>The calling process as an audit row records it.</summary>
    OwnerActor Actor();

    /// <summary>Whether the caller is the user at a plain terminal: no Claude harness among its ancestors, a controlling terminal no live harness holds. False when this proof cannot tell.</summary>
    bool IsUser() => false;

    /// <summary>
    /// The nearest ancestor of the calling process that is a team's recorded Orchestrator, a registered session, or a Claude
    /// harness with a session-pids row in one of <paramref name="rowDirs"/> written since the process started (and, when the
    /// environment names a harness pid, that pid). Only <c>resume</c> asks for the last kind.
    /// </summary>
    NearestSession NearestAncestor(string dir, IReadOnlyList<string> rowDirs);

    /// <summary>
    /// The ancestor of the calling process that the owner guard registers when no walk proves a session (ah2-204): <paramref name="stated"/>
    /// when it is on the ancestor line and a Claude harness by its command alone (no session record read), else the nearest such ancestor;
    /// null when none is or the process table cannot say.
    /// </summary>
    int? HarnessAncestor(int? stated = null) => null;

    /// <summary>Whether <paramref name="pid"/> is an ancestor of the calling process; null when the process table cannot say.</summary>
    bool? IsAncestor(int pid) => null;

    /// <summary>The live registrations of <paramref name="pid"/>: for each box address it holds, the start time and session the registration recorded. None when this proof cannot tell.</summary>
    IReadOnlyList<OwnerRegistration> RegistrationsOf(int pid) => [];

    /// <summary>Every live session registration, with the pane it runs in and the box addresses it holds. None when this proof cannot tell.</summary>
    IReadOnlyList<LiveSession> LiveSessions() => [];
}

/// <summary>A live session registration: its process, the pane it runs in (null when it records none) and the addresses of the boxes it holds.</summary>
public sealed record LiveSession(string SessionId, int Pid, string? PidStart, string? PaneId, IReadOnlyList<string> Addresses);

/// <summary>One box a live registration holds, with the process start time and the session id the registration recorded.</summary>
public sealed record OwnerRegistration(string Address, string? PidStart, string? SessionId);

/// <summary>The mesh's link revocation: a member left a team file, so every active link it was on ends.</summary>
public interface IRosterLinks
{
    /// <summary>Ends the links of <paramref name="names"/> at the team file <paramref name="teamPath"/>, or of the whole team when null.</summary>
    void RevokeLinksFor(string teamPath, IReadOnlyList<string>? names);
}

/// <summary>Records the calling session under its mailbox addresses: the client side of the mailbox registration.</summary>
/// <summary>A live registration bound to a process: its session and when it was registered.</summary>
public sealed record BoundSession(string SessionId, DateTimeOffset At);

public interface IRosterCallerRegistry
{
    /// <summary>
    /// Resolves who the caller is and writes its registration. Returns the addresses it holds (none: nothing was written).
    /// <paramref name="teamArg"/> and <paramref name="teamDefaultExplicit"/> pin the team as <c>whoami --team</c> does.
    /// </summary>
    IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit);

    /// <summary>
    /// <see cref="Register(string, string?, int, string?, string?, bool)"/>, also recording <paramref name="herdrFlavour"/>, the herdr server
    /// the session's pane lives on; pass it only when <paramref name="paneId"/> came from the session's own env.
    /// </summary>
    IReadOnlyList<string> Register(string cwd, string? sessionId, int pid, string? paneId, string? teamArg, bool teamDefaultExplicit, string? herdrFlavour) =>
        Register(cwd, sessionId, pid, paneId, teamArg, teamDefaultExplicit);

    /// <summary>
    /// The addresses <paramref name="sessionId"/>'s live registration holds when it is for <paramref name="pid"/> (and, when given,
    /// <paramref name="paneId"/>); null when there is none or it is for another process or pane. Reads only.
    /// </summary>
    IReadOnlyList<string>? HeldAddresses(string? sessionId, int pid, string? paneId) => null;

    /// <summary>
    /// The named teams whose team file names this session or process as its Orchestrator, wherever the team's repo is. Reads
    /// only; nothing is registered.
    /// </summary>
    IReadOnlyList<string> OwnedTeams(string cwd, string? sessionId, int pid);

    /// <summary>
    /// The session ids of the live registrations that are bound to <paramref name="pid"/> (the daemon verified the registrant
    /// descends from it and its start time matches), newest first. Reads only.
    /// </summary>
    IReadOnlyList<BoundSession> BoundSessions(int pid) => [];

    /// <summary>The working directories that the live registrations of <paramref name="pid"/> recorded, bound or not. Reads only.</summary>
    IReadOnlyList<string> RegistrationCwds(int pid) => [];

    /// <summary>When <paramref name="pid"/>'s process started, or null when that cannot be read or parsed.</summary>
    DateTimeOffset? ProcessStart(int pid) => null;

    /// <summary>
    /// Whether the process a team record names is gone: not alive, or alive with a start time other than
    /// <paramref name="recordedStart"/>. False when this registry cannot tell.
    /// </summary>
    bool OwnerProcessIsGone(double pid, string? recordedStart) => false;

    /// <summary>The start time of <paramref name="pid"/> as <c>ps -o lstart=</c> prints it, or null when that cannot be read.</summary>
    string? ProcessStartText(int pid) => null;

    /// <summary>The process table, or null when this registry has none.</summary>
    Ah.Abstractions.IProcessTable? ProcessTable => null;

    /// <summary>Sends <paramref name="signal"/> to <paramref name="pid"/>; false when the pid is not one that may be signalled or the call failed.</summary>
    bool SignalProcess(int pid, int signal) => false;

    /// <summary>
    /// Repairs the Orchestrator record (session id, or pid and start time) of the named teams that this process and session own
    /// or owned, for a pair the caller has proven process-tied. Returns the teams it changed.
    /// </summary>
    IReadOnlyList<string> RebindTeams(string cwd, string sessionId, int pid) => [];
}
