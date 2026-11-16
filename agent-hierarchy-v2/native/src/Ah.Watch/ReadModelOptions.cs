using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>
/// What <see cref="WatchModule.CreateReadModel"/> builds the read model from besides its reader and directory list. Each part is optional and
/// switches its lanes on: <see cref="Processes"/> the process lane, <see cref="Timeline"/> the timeline lanes, <see cref="Home"/> and
/// <see cref="KnownDirsFile"/> the Tree lanes, and <see cref="TranscriptFiles"/> with <see cref="Usage"/> and <see cref="ValidTranscriptPath"/> the transcript lane.
/// </summary>
public sealed record ReadModelOptions
{
    /// <summary>The counter that moves on any team write once the database holds the teams, null while the files do; polled, because a row raises no file event.</summary>
    public Func<long?>? TeamsChangeToken { get; init; }

    /// <summary>The same counter for registrations once the database holds the sessions.</summary>
    public Func<long?>? SessionsChangeToken { get; init; }

    /// <summary>Reads the exchange id out of a message file name, null when the name is not a message file; switches the messages path rows on.</summary>
    public Func<string, string?>? MessageIdOf { get; init; }

    public IProcessTable? Processes { get; init; }

    public TimelineSettings? Timeline { get; init; }

    /// <summary>The hierarchy home directory (registrations live under it).</summary>
    public string? Home { get; init; }

    public string? KnownDirsFile { get; init; }

    /// <summary>The global peer-pending file, whose heard rows move the ETA state of a dispatch.</summary>
    public string? PeerPendingFile { get; init; }

    /// <summary>Where the hint watcher reports a fault it recovered from.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>The canonical hierarchy directories to watch for file changes, chosen from all of them; null watches every directory. A directory left out is still read, at the reconcile period.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<string>>? ActiveDirs { get; init; }

    /// <summary>The host's answers about lanes (confirmation, child names, routes), computed with the code the routing runs; null serves no lane and no route.</summary>
    public Ah.Watch.Model.ILaneFacts? LaneFacts { get; init; }

    /// <summary>The members' heartbeat and pane facts for a hierarchy directory, read once per processes-lane poll; null gives the lane no such facts.</summary>
    public Func<string, Ah.Watch.Model.MemberFacts>? MemberFacts { get; init; }

    /// <summary>The reader the transcript lane tails transcripts through; byte windows only.</summary>
    public IFileReader? TranscriptFiles { get; init; }

    public IHierarchyUsage? Usage { get; init; }

    /// <summary>The validated real path of a recorded transcript path, null when it is not a transcript of the signed-in user.</summary>
    public Func<string, string?>? ValidTranscriptPath { get; init; }

    /// <summary>The directory holding one subdirectory per project of the signed-in user's transcripts; a session with no activity record in the team's directory is looked up there by its id.</summary>
    public string? TranscriptProjects { get; init; }

    /// <summary>Runs the git status of a work root (the watch side's git request: no optional lock, 15 s timeout); switches the git lane on.</summary>
    public IProcessRunner? GitProcess { get; init; }

    /// <summary>Makes an empty fold of a stream's plan log; switches the progress lane on. The composition root supplies it, because the view calls no module factory.</summary>
    public Func<IPlanFold>? NewPlanFold { get; init; }

    /// <summary>
    /// The queued items of a team for the <c>queue:&lt;dirId&gt;/&lt;team&gt;</c> slice, given the canonical hierarchy directory, the team's progress folder and the time; it runs
    /// the same file-based queue rules as the failsafe and the Stop rule. It is asked only while a client holds the slice. Needs <see cref="NewPlanFold"/>.
    /// </summary>
    public Func<string, string, double, IReadOnlyList<QueueRow>>? QueueProbe { get; init; }

    /// <summary>
    /// The Sitrep of a team for the <c>sitrep:&lt;dirId&gt;/&lt;team&gt;</c> slice (ah-ui-ia §3.3), given the canonical hierarchy directory, the team's progress folder and the time:
    /// the slice body and the progress folders it read (the team and its confirmed lane teams). It is asked only while a client holds the slice. Needs <see cref="NewPlanFold"/>.
    /// The last argument answers, for a team name, who is on its work, its open parked decisions and each work item's owner by key, from the read model.
    /// </summary>
    public Func<string, string, double, Func<string, (Ah.Hierarchy.ChecklistLiveness? Live, IReadOnlyList<JsonValue> Decisions, IReadOnlyDictionary<string, string> Owners)>, (JsonValue Body, IReadOnlyList<string> Teams)>? SitrepProbe { get; init; }

    /// <summary>Whether a team is held, given its hierarchy directory and name: the summary's <c>held</c>. Null reads every team as not held.</summary>
    public Func<string, string, bool>? TeamHeld { get; init; }

    /// <summary>A named team's own goal (ah2-53), given its hierarchy directory and name: <c>{text, done_when, team}</c>, or null with none or when it cannot be read. Null reads no goals.</summary>
    public Func<string, string, JsonObject?>? TeamGoal { get; init; }

    /// <summary>
    /// The jobs of a team for the <c>jobs:&lt;dirId&gt;/&lt;team&gt;</c> slice, given the canonical hierarchy directory, the team's progress folder and the time: the open jobs and those
    /// that ended in the last ten minutes. It is asked only while a client holds the slice, and again on each reconcile tick.
    /// </summary>
    public Func<string, string, double, IReadOnlyList<Model.JobRow>>? JobsProbe { get; init; }

    /// <summary>The job records' write counter, or null when it cannot be read: <see cref="JobsProbe"/> is asked again only after it moves (b459 step 11).</summary>
    public Func<long?>? JobsVersion { get; init; }

    /// <summary>
    /// A token of everything a directory's recorded pids are read from (the session pids, the dir's roster rows and the team records), or null
    /// when it cannot be read: the processes lane reads a directory's pids again only after it moves (b459 step 28).
    /// </summary>
    public Func<string, string?>? PidsStamp { get; init; }

    /// <summary>
    /// The backlog of a hierarchy directory for the <c>backlog:&lt;dirId&gt;</c> slice, given the canonical hierarchy directory and the time: its items in list order and its groom health.
    /// It is asked only while a client holds the slice, on each backlog write, on each watcher overflow and once a minute while a client is connected.
    /// </summary>
    public Func<string, long, Model.BacklogFacts>? BacklogProbe { get; init; }

    /// <summary>
    /// The questions parked for the user in a hierarchy directory (<c>q&lt;k&gt;</c>, open and answered), given the canonical directory: the store <c>ah msg decision park</c> writes for a team
    /// whose home it is; null when the store cannot be read. Asked on each status refresh of the directory and on a reconcile; without it no parked question waits on the user in the model (b517).
    /// </summary>
    public Func<string, IReadOnlyList<Ah.Hierarchy.ParkedQuestion>?>? ParkedProbe { get; init; }

    /// <summary>The board fields of one stream's registered items, by item key, given the canonical hierarchy directory, the progress folder and the stream. Asked only while a client holds the stream.</summary>
    public Func<string, string, string, IReadOnlyDictionary<string, Model.WorkCard>>? WorkCardsProbe { get; init; }

    /// <summary>The <c>item-log:&lt;dirId&gt;/&lt;n&gt;</c> body, given the canonical hierarchy directory and the item number; null when there is no such item. Asked only while a client holds the slice.</summary>
    public Func<string, long, Ah.Core.Json.JsonObject?>? ItemLogProbe { get; init; }

    /// <summary>The <c>item-desc:</c> body of a work item, <c>{id, description}</c>, or null when there is no registry.</summary>
    public Func<string, long, Ah.Core.Json.JsonObject?>? ItemDescProbe { get; init; }
}
