using System.Text;
using System.Diagnostics;
using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>A box of a registration: its address (<c>name@team</c>) and directory.</summary>
/// <param name="Address">The box address.</param>
/// <param name="Dir">The box directory.</param>
public sealed record WatchBox(string Address, string Dir);

/// <summary>A live registration the dispatch clock may keep time for.</summary>
/// <param name="SessionId">The registered session.</param>
/// <param name="Harness">The harness the session runs under, e.g. <c>claude</c>.</param>
/// <param name="Pid">The session's process id, or null.</param>
/// <param name="At">When the registration was written (ISO text), or null.</param>
/// <param name="Boxes">Every box the registration names.</param>
/// <param name="HierarchyDir">The hierarchy dir the registration's team file sits in.</param>
public sealed record WatchRegistration(string SessionId, string? Harness, long? Pid, string? At, IReadOnlyList<WatchBox> Boxes, string HierarchyDir);

/// <summary>One event mail the daemon sends to an Orchestrator's box.</summary>
/// <param name="To">The box address.</param>
/// <param name="HierarchyDir">The hierarchy dir the mail is sent under.</param>
/// <param name="Cwd">The session's working directory.</param>
/// <param name="Event">The mailbox event name.</param>
/// <param name="Text">The mail text.</param>
/// <param name="Data">The event's data object.</param>
/// <param name="ToSession">The session the mail is for.</param>
/// <param name="Id">The entry id to send under, so a repeat adds nothing; null mints one.</param>
/// <param name="CreatedAt">The entry's creation time; null is now.</param>
public sealed record WatchMail(string To, string HierarchyDir, string Cwd, string Event, string Text, JsonObject Data, string ToSession, string? Id = null, DateTimeOffset? CreatedAt = null);

/// <summary>What the daemon exposes about the clock it keeps, for health.</summary>
/// <param name="Timers">The sessions it keeps time for, in order of first sight.</param>
/// <param name="TimerState">Per session: <c>{last_tick, last_event}</c>.</param>
/// <param name="Ladder">The delivery ladder's counts from its last pass.</param>
public sealed record WatchSnapshot(IReadOnlyList<string> Timers, JsonObject TimerState, JsonObject Ladder);

/// <summary>
/// Everything the watch tick reads and writes outside its own logic, named after the oracle function behind each member. The
/// composition root implements it over the services that own each store.
/// </summary>
public interface IWatchHost : IRecoveryHost
{

    /// <summary>A tick starts at <paramref name="nowMs"/>: what the host keeps for one tick is dropped, and every read of the tick shares the new set.</summary>
    void BeginTick(double nowMs) { }

    /// <summary>A status dir the daemon has not seen yet: rebuilds its persisted status entries when their <c>meta</c> is missing or stale (ah-msgs-fast-2 §3.8 item 6).</summary>
    void StatusDirFirstSeen(string dir) { }

    /// <summary>Rebuilds what a crashed database write left marked dirty (each status-dir refresh).</summary>
    void HealDirtyStatus() { }

    /// <summary>Whether the attributed roster says peer <paramref name="name"/> is busy (the record <c>ah roster status</c> reads), false when it has no such record.</summary>
    bool PeerBusy(string dir, string name, string? team);

    /// <summary>When the roster row <see cref="PeerBusy"/> read was written, in epoch milliseconds, or null when it does not say.</summary>
    double? PeerBusySeenMs(string dir, string name, string? team) => null;

    /// <summary>The slot (the e4 pool, the test pool, any pool) the member of <paramref name="session"/> holds or waits for, or null. A pid in a slot record counts for the session that is its nearest registered Claude ancestor.</summary>
    SlotState? SlotStateOf(string session, double nowMs) => null;

    /// <summary>Whether <paramref name="team"/> (null for the default team) of the hierarchy dir <paramref name="home"/> is under a hold, its own or an ancestor's.</summary>
    bool TeamHeld(string home, string? team) => false;

    /// <summary>The checklist of <paramref name="team"/> (null for the default team) as the session <paramref name="session"/> sees it: every item of its plan streams and of its loose-end stream. Empty when none is readable.</summary>
    IReadOnlyList<ChecklistEntry> Checklist(string session, string home, string? team) => [];

    /// <summary>The ready backlog items in wave <c>now</c> or <c>next</c> of the hierarchy dir the session <paramref name="session"/> sees. Read-only; empty when none is readable.</summary>
    IReadOnlyList<ReadyBacklogItem> ReadyBacklog(string session, string home) => [];

    /// <summary>Every job of the daemon's QA queue, read only. Empty by default and when it is not readable.</summary>
    IReadOnlyList<QaJob> QaJobs() => [];

    /// <summary>The most live members <paramref name="team"/> (null for the default team) may hold; null means no limit. The medium budget when no run anchor applies.</summary>
    int? SessionBudgetOf(string home, string? team) => SessionBudget.Default;

    /// <summary>
    /// ah2-514 LB-2: the live member rows of the team tree whose budget bounds <paramref name="team"/>, lanes included, and the teams of it whose file
    /// could not be read. <paramref name="ownedLive"/> gives the count the session already holds for each team it owns (<c>""</c> for the default
    /// team). Null when the host cannot count the tree: the team's own count is used, as before.
    /// </summary>
    (int Live, IReadOnlyList<string> Unreadable)? SessionTreeLive(string home, string? team, IReadOnlyDictionary<string, int> ownedLive) => null;

    /// <summary>The tracked jobs of <paramref name="team"/> (null for the default team), as the session <paramref name="session"/> sees them. Empty by default.</summary>
    IReadOnlyList<JobFact> Jobs(string session, string home, string? team) => [];

    /// <summary>
    /// Brings the open jobs of <paramref name="team"/> up to date with the process table: a job whose process is gone is ended, and one
    /// with no process yet gets the one below its owner's session that started it. Answers the jobs still open, as they now are.
    /// </summary>
    IReadOnlyList<JobFact> ReconcileJobs(string session, string home, string? team, IReadOnlyList<JobFact> jobs) => jobs;

    /// <summary>The cap kills the with-slot journal records since ahd's first start on this build; empty when it cannot be read.</summary>
    IReadOnlyList<JobCap> CapKills() => [];

    /// <summary>Writes the journal's <c>cap-watch</c> mark when it has none (ahd's first start on this build); true once the mark is there.</summary>
    bool EnsureCapWatchMark() => true;

    /// <summary>Marks job <paramref name="jobId"/> of <paramref name="team"/> as having had its cap kill told, on the job's own record, so it is never told again.</summary>
    void MarkCapTold(string session, string home, string? team, string jobId)
    {
    }

    /// <summary>What the files show of each child lane of the teams <paramref name="session"/> orchestrates: a team whose file names one of them as its parent.</summary>
    IReadOnlyList<LaneFact> Lanes(string session, string home, IReadOnlyList<OwnedTeam> owned) => [];

    /// <summary>Whether the box already holds this event of this request at this interval of its clock, so it is not mailed twice, across an ahd restart too.</summary>
    bool AlreadyMailed(WatchMail mail) => false;

    /// <summary>
    /// Withdraws the unacked member resume entries in <paramref name="address"/>'s box (<c>IMailbox.WithdrawRewakes</c>): those to
    /// <paramref name="session"/> when given, never <paramref name="keepId"/>, those created before <paramref name="createdBeforeMs"/> when given.
    /// </summary>
    void WithdrawRewakes(string address, string? session, string? keepId, double? createdBeforeMs, string reason)
    {
    }

    /// <summary>The member verdict of an activity record read for an overdue dispatch (so a dispatch is open to it); <paramref name="busy"/> is the ListAgents busy-or-shell tag and <paramref name="debounceMs"/> how long a compaction must have gone unanswered to count as a stall. A null record gives <c>unknown</c>.</summary>
    string RecordVerdict(JsonObject? record, double nowMs, bool busy, double debounceMs);

    /// <summary>
    /// Whether the chain of command of the member registered as <paramref name="member"/> holds. A host without a chain reader never
    /// proves one down.
    /// </summary>
    Ah.Roster.ChainState ChainOf(WatchRegistration member) => new(Ah.Roster.ChainKind.Unknown, null, "no chain reader");

    /// <summary>The text of a <c>wind-down</c> mail: the link that broke, why, and the Orchestrator address the member sends its resume note to.</summary>
    string WindDownText(string link, string why, string orchestratorAddress) => string.Empty;

    /// <summary><c>liveRegistrations()</c>, as the registrations' fields the clock needs.</summary>
    IReadOnlyList<WatchRegistration> LiveRegistrations();

    /// <summary>The hierarchy dirs of every live registration (for the status-dir backstop).</summary>
    IReadOnlyList<string> LiveHierarchyDirs();

    /// <summary><c>sessionPullFresh(boxDir, sessionId, now)</c>: the session's own heartbeat in the box is fresh.</summary>
    bool PullFresh(string boxDir, string sessionId, double nowMs);

    /// <summary>The time of the session's newest pull heartbeat in the box, in epoch milliseconds; 0 when there is none.</summary>
    double PullBeatMs(string boxDir, string sessionId, double nowMs);

    /// <summary><c>sessionCwd(sessionId)</c>, or null.</summary>
    string? SessionCwd(string sessionId);

    /// <summary>The check-in threshold, in seconds, of an eta word (<c>thresholdFor</c>).</summary>
    double ThresholdSeconds(string eta);

    /// <summary>The session's unreported dispatches, resolved from their request files and exchanges (<c>latestDispatchRows</c>).</summary>
    IReadOnlyList<DispatchCandidate> Dispatches(string sessionId, double nowMs);

    /// <summary>
    /// A token that moves whenever a write could give <see cref="Dispatches"/> a new answer (messages, peer records, session pids, teams),
    /// or null when the host cannot say; the tick then asks <see cref="Dispatches"/> every time.
    /// </summary>
    string? DispatchInputsStamp(string sessionId, double nowMs) => null;

    /// <summary>The implement-class DONE reports of the last hour on requests the session's owned teams' Orchestrator sent, each with whether a review exchange exists now (b487).</summary>
    IReadOnlyList<DoneReport> DoneReports(string sessionId, double nowMs) => [];

    /// <summary>The peer records (<c>readPeerRecords</c>).</summary>
    IReadOnlyList<JsonObject> PeerRecords();

    /// <summary>The gate rows of a hierarchy dir (<c>readGates</c>).</summary>
    IReadOnlyList<JsonObject> Gates(string hierarchyDir);

    /// <summary>As <see cref="Gates"/>, but null, with the cause in <paramref name="why"/>, when the gates cannot be read, so a failed read is not taken for no rows.</summary>
    IReadOnlyList<JsonObject>? GatesOrNull(string hierarchyDir, out string? why)
    {
        why = null;
        return Gates(hierarchyDir);
    }

    /// <summary>The sessions whose context fill is watched: the Orchestrator and each member of an owned team.</summary>
    IReadOnlyList<ContextSubject> ContextSubjects(string sessionId, string hierarchyDir);

    /// <summary>The teams the session owns, with their described members.</summary>
    IReadOnlyList<OwnedTeam> OwnedTeams(string sessionId, string hierarchyDir);

    /// <summary>Writes <c>k=v</c> labels (an empty value removes the key) on the herdr-core terminal <paramref name="address"/>; false when the write failed. A host without herdr core writes nothing.</summary>
    bool WriteTerminalLabels(string address, IReadOnlyList<string> pairs) => true;

    /// <summary>The herdr-core terminal address of session <paramref name="sessionId"/> as the roster of <paramref name="home"/> records it, or null.</summary>
    string? SessionTerminal(string home, string sessionId) => null;

    /// <summary><c>herdrAgentList()</c> as rows of <c>{pane_id, agent_status}</c>, or null when herdr cannot answer.</summary>
    IReadOnlyList<JsonObject>? HerdrAgents();

    /// <summary><c>readPromptCore(row, "blocked")</c>, on the herdr server of the member (its registration, else <paramref name="team"/>'s).</summary>
    PromptRead ReadPrompt(JsonObject memberRow, JsonObject team);

    /// <summary><c>resolveKind(row)</c>.</summary>
    string KindOf(JsonObject memberRow);

    /// <summary><c>rowAdapted(row)</c>.</summary>
    bool RowAdapted(JsonObject memberRow);

    /// <summary>The heading of a recognised prompt of a harness kind, or null.</summary>
    string? PromptHeading(string kind, string? recognized);

    /// <summary>When the open dispatch to <paramref name="member"/> began, else 0.</summary>
    double DispatchBaseMs(string sessionId, string member);

    /// <summary>The roster CLI command line a cancel command runs.</summary>
    string RosterCommand { get; }

    /// <summary>Appends gate rows (<c>appendGateRouted</c>).</summary>
    void AppendGates(IReadOnlyList<GateAppend> gates);

    /// <summary>
    /// Records what the watcher read on the activity record of session <paramref name="sessionId"/> under <paramref name="home"/> and its
    /// other copy, at <paramref name="atMs"/>: <paramref name="clear"/> (its screen was read and shows no prompt) sets <c>screen_clear_at</c>
    /// and removes <c>screen_prompt_at</c>, else the reverse (ah2-74, ah2-102 E1). A mark already set is kept; nothing else in the record changes.
    /// </summary>
    void MarkScreenClear(string home, string sessionId, bool clear, double atMs);

    /// <summary>Appends peer-record rows.</summary>
    void AppendPeerRows(IReadOnlyList<PeerAppend> rows);

    /// <summary>Marks the dispatches' reports as surfaced (<c>appendReportRecord("surfaced", ...)</c>).</summary>
    void MarkSurfaced(string sessionId, IReadOnlyList<string> requestIds);

    /// <summary>Records watch-log rows (<c>recordWatchEvents</c>).</summary>
    void RecordWatchEvents(string sessionId, IReadOnlyList<WatchEvent> events);

    /// <summary>Mails an event; throws when the mail cannot be sent.</summary>
    void Mail(WatchMail mail);

    /// <summary>The link sweep of one session's boxes.</summary>
    Task SweepLinksAsync(IReadOnlyList<WatchBox> boxes, string cwd, string hierarchyDir, double nowMs, CancellationToken cancellation);

    /// <summary>One delivery-ladder pass; returns its counts <c>{pending, sending, paused, escalated}</c>.</summary>
    Task<JsonObject> RunLadderAsync(double nowMs, double pollMs, CancellationToken cancellation);
}

/// <summary>What <see cref="WatchModule.Register"/> runs on.</summary>
/// <param name="Host">The reads and writes of the tick.</param>
/// <param name="Environment">Where the poll period comes from.</param>
/// <param name="Clock">The wall clock.</param>
/// <param name="Files">A file reader; unused by the tick since status left the disk (files-off-disk F3a).</param>
/// <param name="Recovery">The recovery rules (streaks, delays, transcript reading).</param>
/// <param name="StatusDirs">The set of watched status dirs the views read; created by <see cref="WatchModule.CreateStatusDirs"/>.</param>
/// <param name="StillOwner">False when this daemon no longer owns its files; a tick then does nothing. Null means always.</param>
/// <param name="Log">The daemon's log line writer, or null.</param>
/// <param name="Observe">Called after each tick with the state health reports, or null.</param>
/// <param name="TurnEnds">The turn-end trace the recovery decisions are written to, or null.</param>
/// <param name="Stamp">Called with the job name after a job run completes, or null.</param>
public sealed record WatchDependencies(
    IWatchHost Host,
    IAhEnvironment Environment,
    IClock Clock,
    IFileReader Files,
    IHierarchyRecovery Recovery,
    IStatusDirs StatusDirs,
    Func<bool>? StillOwner = null,
    Action<string>? Log = null,
    Action<WatchSnapshot>? Observe = null,
    ITurnEndLog? TurnEnds = null,
    Action<string>? Stamp = null)
{
    /// <summary>Adds up the thread CPU of the tick's synchronous stretches for the jobs line's <c>cpu=</c>; null leaves the tick unmetered.</summary>
    public ICpuMeter? Cpu { get; init; }

    /// <summary>The gap between two polls that the daemon reads as the machine having slept.</summary>
    public const double DefaultSleepGapMs = 5 * 60 * 1000;

    /// <summary>The gap between two polls that counts as sleep and is left out of the dispatch clock. The default never does; the daemon sets <see cref="DefaultSleepGapMs"/>.</summary>
    public double SleepGapMs { get; init; } = double.PositiveInfinity;

    /// <summary>Where each tick publishes the session ids its blocked pass reads (ah2-102 F2), or null to publish nowhere.</summary>
    public IWatchCoverage? Coverage { get; init; }
}

/// <summary>The hierarchy dirs of live registrations: what <c>/v1/status</c> serves.</summary>
public interface IStatusDirs
{
    /// <summary>Whether <paramref name="dir"/> is watched.</summary>
    bool Contains(string dir);

    /// <summary>The watched dirs now.</summary>
    IReadOnlyList<string> Snapshot();

    /// <summary>Stops watching a dir (its document is gone or unreadable).</summary>
    void Remove(string dir);

    /// <summary>Replaces the whole set.</summary>
    void Replace(IEnumerable<string> dirs);
}

internal sealed class StatusDirs : IStatusDirs
{
    private readonly object _gate = new();
    private HashSet<string> _dirs = new(StringComparer.Ordinal);

    public bool Contains(string dir)
    {
        lock (_gate)
        {
            return _dirs.Contains(dir);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return [.. _dirs];
        }
    }

    public void Remove(string dir)
    {
        lock (_gate)
        {
            _ = _dirs.Remove(dir);
        }
    }

    public void Replace(IEnumerable<string> dirs)
    {
        var next = new HashSet<string>(dirs, StringComparer.Ordinal);
        lock (_gate)
        {
            _dirs = next;
        }
    }
}

/// <summary>One pass of the dispatch clock over every live Orchestrator, then one delivery-ladder pass.</summary>
internal sealed class WatchTick
{
    /// <summary>A text over this many characters cannot be mailed whole.</summary>
    internal const int MaxEventTextChars = 3000;

    private sealed class Timer(double announced, IReadOnlyList<WatchBox> boxes)
    {
        internal double Announced { get; } = announced;

        internal IReadOnlyList<WatchBox> Boxes { get; set; } = boxes;

        /// <summary>The newest pull-heartbeat time of each box the session is polling now, by address; a box that is not polled is absent.</summary>
        internal Dictionary<string, double> Beats { get; set; } = [];

        /// <summary>The host's inputs stamp when the session's dispatches were last read and were none; null when they were some, or never read.</summary>
        internal string? QuietStamp { get; set; }

        /// <summary>When <see cref="QuietStamp"/> was taken.</summary>
        internal double QuietAt { get; set; }

        /// <summary>Every orchestrator box the registration names, polled or not.</summary>
        internal IReadOnlyList<WatchBox> Registered { get; set; } = boxes;

        internal string? LastTick { get; set; }

        internal string? LastEventKind { get; set; }

        internal string? LastEventAt { get; set; }
    }

    private readonly WatchDependencies _d;
    private readonly ChainDownSweep _chainDown;
    private readonly double _pollMs;
    private readonly Dictionary<string, Timer> _timers = new(StringComparer.Ordinal);
    private readonly BlockedEpisodes _episodes = new();
    private readonly Dictionary<string, string> _transcriptSeen = new(StringComparer.Ordinal);

    /// <summary>The recovery decisions already traced, so one reached on every pass (held, backoff) is traced once. Only the trace reads it; it decides nothing.</summary>
    private readonly HashSet<string> _tracedDecisions = new(StringComparer.Ordinal);
    private JsonObject _ladder = LadderZero();
    private int _ladderRunning;
    private int _ladderSkipTold;

    internal WatchTick(WatchDependencies dependencies)
    {
        _d = dependencies;
        _chainDown = new ChainDownSweep(dependencies);
        _pollMs = Liveness.PollMs(dependencies.Environment);
        _downMs = StallSettings.From(dependencies.Environment).Down.TotalMilliseconds;
        _sleepGapMs = Math.Max(dependencies.SleepGapMs, 2 * _pollMs);
        _awake = new AwakeClock(_sleepGapMs);
        _startedMs = dependencies.Clock.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>When this watch started: a DONE report filed before it never raises <c>unreviewed-done</c>.</summary>
    private readonly double _startedMs;

    private readonly AwakeClock _awake;

    /// <summary>The gap between two polls that counts as the machine having slept: the one test behind <see cref="_awake"/>.</summary>
    private readonly double _sleepGapMs;

    internal double PollMs => _pollMs;

    private static JsonObject LadderZero() =>
        new() { ["pending"] = 0, ["sending"] = 0, ["paused"] = 0, ["escalated"] = 0 };

    /// <summary>
    /// The live Orchestrator registrations whose clock is kept: every one with an <c>orchestrator@</c> box, or a <c>chief@</c> box (a
    /// chief whose deputy is on duty still owns its teams by the record). Among live <c>claude</c>
    /// registrations that share a pid only the newest counts (a <c>/clear</c> gives the same process a new session id); other
    /// harnesses' registrations are untouched.
    /// </summary>
    internal static IReadOnlyList<WatchRegistration> TimerRegistrations(IReadOnlyList<WatchRegistration> live)
    {
        var newest = new Dictionary<long, WatchRegistration>();
        foreach (var r in live)
        {
            if (r.Harness != "claude" || r.Pid is not { } pid)
            {
                continue;
            }

            if (!newest.TryGetValue(pid, out var current) || Newer(r, current))
            {
                newest[pid] = r;
            }
        }

        return
        [
            .. live.Where(r =>
                !string.IsNullOrEmpty(r.SessionId)
                && r.Boxes.Any(static b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal) || b.Address.StartsWith("chief@", StringComparison.Ordinal))
                && (r.Harness != "claude" || r.Pid is not { } pid || ReferenceEquals(newest[pid], r))),
        ];
    }

    private static bool Newer(WatchRegistration a, WatchRegistration b) =>
        Timestamps.TryParseMs(a.At ?? string.Empty, out var x) && Timestamps.TryParseMs(b.At ?? string.Empty, out var y) && x > y;

    /// <summary>
    /// What the mailbox carries for event <paramref name="ev"/> under event name <paramref name="name"/>, and whether the full text
    /// then belongs in the watch log: a text that fits goes whole; one that does not goes to the log and the mail is the event's
    /// compact form when that fits, else a short pointer.
    /// </summary>
    internal static (string Text, bool TooLong, string Pointer) EventMailText(WatchEvent ev, string name)
    {
        var pointer = $"{name}: full text is in the watch log (too long to mail)";
        if (ev.Text.Length <= MaxEventTextChars)
        {
            return (ev.Text, false, pointer);
        }

        var compact = ev.Compact is { } c && c.Length <= MaxEventTextChars ? c : null;
        return (compact ?? pointer, true, pointer);
    }

    internal async Task RunAsync(CancellationToken cancellation)
    {
        // ah2-102 F3: a daemon that stops (its jobs' token is cancelled when it drains) or loses the watch publishes no coverage
        if (_d.Coverage is { } stopping && Interlocked.Exchange(ref _stopHooked, 1) == 0)
        {
            _ = cancellation.Register(() => stopping.Publish(NoSessions));
        }

        if (_d.StillOwner is { } owns && !owns())
        {
            _d.Coverage?.Publish(NoSessions);
            return;
        }

        _covered.Clear();
        _coverageFailed = false;
        var now = (double)_d.Clock.UtcNow.ToUnixTimeMilliseconds();
        _ = _awake.Poll(now);
        _d.Host.BeginTick(now);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _ = _laps.Clear();
        var clock = Stopwatch.StartNew();
        _d.Cpu?.OpenStretch();
        using var processGroups = ProcessSnapshotScope.BeginGroups();
        PrepareOutage(now);
        var prepared = clock.ElapsedMilliseconds;
        var live = TimerRegistrations(_d.Host.LiveRegistrations());
        var listed = clock.ElapsedMilliseconds;
        long fetching = 0;
        long evaluating = 0;
        foreach (var reg in live)
        {
            var step = clock.ElapsedMilliseconds;
            var boxes = reg.Boxes.Where(static b => b.Address.StartsWith("orchestrator@", StringComparison.Ordinal)).ToList();
            var fresh = boxes.Where(b => _d.Host.PullFresh(b.Dir, reg.SessionId, now)).ToList();
            _ = seen.Add(reg.SessionId);
            if (!_timers.TryGetValue(reg.SessionId, out var timer))
            {
                timer = new Timer(now, boxes);
                _timers[reg.SessionId] = timer;
            }

            timer.Boxes = fresh.Count > 0 ? fresh : boxes;
            timer.Registered = boxes;
            timer.Beats = fresh.ToDictionary(static b => b.Address, b => _d.Host.PullBeatMs(b.Dir, reg.SessionId, now), StringComparer.Ordinal);
            fetching += clock.ElapsedMilliseconds - step;
            if (now - timer.Announced < _pollMs - 100)
            {
                continue;
            }

            step = clock.ElapsedMilliseconds;

            try
            {
                var evaluation = EvaluateSessionAsync(reg, timer, now, cancellation);
                _d.Cpu?.CloseStretch();
                await evaluation.ConfigureAwait(false);
                _d.Cpu?.OpenStretch();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _coverageFailed = true;
                _d.Log?.Invoke($"tick failed for {reg.SessionId}: {e}");
            }

            timer.LastTick = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now));
            evaluating += clock.ElapsedMilliseconds - step;
        }

        // ah2-102 F2/F3: the union of every evaluated session's coverage, once per tick; a session that threw leaves the last set standing
        // and the publish time unmoved, so a session that throws every tick lets the backstop clear the set
        if (!_coverageFailed)
        {
            _d.Coverage?.Publish(new HashSet<string>(_covered, StringComparer.Ordinal));
            Interlocked.Exchange(ref _coverageAtMs, _d.Clock.UtcNow.ToUnixTimeMilliseconds());
        }

        foreach (var sid in _timers.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _ = _timers.Remove(sid);
            lock (_laneState)
            {
                _ = _copyUps.Remove(sid);
            }
        }

        try
        {
            _chainDown.Run(now);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"chain-down sweep failed: {e}");
        }

        var beforeLadder = clock.ElapsedMilliseconds;
        var ladder = LadderPassAsync(now, cancellation);
        _d.Cpu?.CloseStretch();
        await ladder.ConfigureAwait(false);
        _d.Cpu?.OpenStretch();
        var afterLadder = clock.ElapsedMilliseconds;
        _d.Observe?.Invoke(Snapshot());
        _d.Cpu?.CloseStretch();
        lock (_tickDurations)
        {
            _tickDurations.Add(clock.ElapsedMilliseconds);
        }

        if (clock.ElapsedMilliseconds >= SlowTickMs)
        {
            _d.Log?.Invoke($"watch-tick slow: {clock.ElapsedMilliseconds} ms over {live.Count} sessions: outage {prepared}, registrations {listed - prepared}, fresh-pull {fetching}, evaluate {evaluating}, ladder {afterLadder - beforeLadder}, observe {clock.ElapsedMilliseconds - afterLadder}; evaluate laps:{_laps}");
        }
    }

    private const long SlowTickMs = 250;

    private readonly List<long> _tickDurations = [];

    /// <summary>Ticks of at least <see cref="SlowTickMs"/> and the 95th-percentile duration over the ticks since the last call, as <c>slow=3 p95=410ms</c>; null when none ran. Starts the window over.</summary>
    internal string? TakeTickReport()
    {
        long[] window;
        lock (_tickDurations)
        {
            window = [.. _tickDurations];
            _tickDurations.Clear();
        }

        if (window.Length == 0)
        {
            return null;
        }

        Array.Sort(window);
        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"slow={window.Count(ms => ms >= SlowTickMs)} p95={window[(int)Math.Ceiling(window.Length * 0.95) - 1]}ms");
    }

    private static readonly IReadOnlySet<string> NoSessions = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>This tick's coverage so far: the session ids each evaluated session's blocked pass reads (ah2-102 F1).</summary>
    private readonly HashSet<string> _covered = new(StringComparer.Ordinal);

    private bool _coverageFailed;

    /// <summary>When a tick last published its coverage, by the clock at the publish, in epoch ms; 0 before the first.</summary>
    private long _coverageAtMs;

    private int _stopHooked;

    /// <summary>The publish time a sleep gap was seen after, and when the backstop first saw it (ah2-102 F4).</summary>
    private long _wakeAfterMs;

    private double _wakeSeenMs;

    /// <summary>
    /// ah2-102 F4: run by the status-dir backstop job, which ahd schedules apart from the tick: no coverage is published while no tick has
    /// published for three poll periods, so a tick stuck awaiting or a session that throws every tick cannot keep a pending read showing
    /// working. A gap longer than the tick's sleep test (<see cref="_sleepGapMs"/>) is a sleep: the first tick after the wake republishes, so
    /// the backstop waits for it, at most one poll period after it saw the wake.
    /// </summary>
    internal void ExpireCoverage(double now)
    {
        var at = Interlocked.Read(ref _coverageAtMs);
        if (at == 0 || now - at <= 3 * _pollMs)
        {
            return;
        }

        if (now - at > _sleepGapMs)
        {
            if (_wakeAfterMs != at)
            {
                _wakeAfterMs = at;
                _wakeSeenMs = now;
            }

            if (now - _wakeSeenMs <= _pollMs)
            {
                return;
            }
        }

        _d.Coverage?.Publish(NoSessions);
    }

    private OutagePlan _outage = new(OutageView.None, [], new Dictionary<string, List<WatchEvent>>(), []);

    private readonly Dictionary<string, IReadOnlyList<OwnedTeam>> _ownedByPrepass = new(StringComparer.Ordinal);

    /// <summary>
    /// The daemon-wide step ahead of the per-session loop: reads every watched session once, opens, updates or closes the shared
    /// outage, and records its rows. The sessions' recovery then reads what it decided.
    /// </summary>
    private void PrepareOutage(double now)
    {
        var host = _d.Host;
        _ownedByPrepass.Clear();
        try
        {
            var inputs = new List<OutageInput>();
            foreach (var reg in TimerRegistrations(host.LiveRegistrations()))
            {
                // A session still being announced is not evaluated yet, so it could not mail what the pre-pass decides for it.
                if (host.SessionCwd(reg.SessionId) is null || !_timers.TryGetValue(reg.SessionId, out var timer) || now - timer.Announced < _pollMs - 100)
                {
                    continue;
                }

                var sid = reg.SessionId;
                var home = reg.HierarchyDir;
                var gates = host.Gates(home);
                var owned = host.OwnedTeams(sid, home);
                _ownedByPrepass[sid] = owned;
                if (AllTeamsHeld(reg, owned))
                {
                    continue;
                }

                foreach (var sub in RecoveryEvents.SubjectsFor(sid, owned, host.KindOf, host.RowAdapted))
                {
                    if (host.TeamHeld(home, sub.Team))
                    {
                        continue;
                    }

                    var id = sub.Id;
                    inputs.Add(new OutageInput(
                        new OutageSubject(id, sid, sub.Own, sub.Name, sub.Role, sub.Team),
                        host.ReadActivity(home, id),
                        sub.NoResume,
                        failedAt => gates.Any(g => g.Str("session_id") == sid && g.Str("subject") == id && g.Str("failed_at") == failedAt && g.Str("type") is "resume" or "api-failed")));
                }
            }

            _outage = OutageWatch.Evaluate(now, inputs, host.PeerRecords(), _d.Recovery);
            host.AppendPeerRows(_outage.Rows);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"outage check failed: {e.Message}");
            _outage = new OutagePlan(OutageView.None, [], new Dictionary<string, List<WatchEvent>>(), []);
        }
    }

    internal WatchSnapshot Snapshot()
    {
        var state = new JsonObject();
        foreach (var (sid, t) in _timers)
        {
            JsonValue lastEvent = JsonValue.Null;
            if (t.LastEventKind is not null)
            {
                lastEvent = JsonValue.FromObject(new JsonObject { ["kind"] = t.LastEventKind, ["at"] = t.LastEventAt ?? string.Empty });
            }

            state[sid] = JsonValue.FromObject(new JsonObject
            {
                ["last_tick"] = t.LastTick is null ? JsonValue.Null : JsonValue.FromString(t.LastTick),
                ["last_event"] = lastEvent,
            });
        }

        return new WatchSnapshot([.. _timers.Keys], state, _ladder);
    }

    /// <summary>Whether every team the registration is a member of or owns is held; a registration with no team is not.</summary>
    private bool AllTeamsHeld(WatchRegistration reg, IReadOnlyList<OwnedTeam> owned)
    {
        var teams = new HashSet<string?>(owned.Select(static t => t.Name));
        foreach (var box in reg.Boxes)
        {
            if (box.Address.Split('@', 2) is [_, var team])
            {
                teams.Add(team);
            }
        }

        return teams.Count > 0 && teams.All(t => _d.Host.TeamHeld(reg.HierarchyDir, t));
    }

    private bool AddressHeld(string home, string? address) =>
        address is not null && address.Split('@', 2) is [_, var team] && _d.Host.TeamHeld(home, team);

    /// <summary>
    /// How long a session with no unreported dispatch is not asked for them again while the host's inputs stamp holds. J3 (b459): was
    /// every tick (the poll period, 15 s). Nothing is due for such a session, so no deadline can pass unseen: a new dispatch moves the
    /// stamp and is read on the next tick, and this cap bounds an input the stamp does not cover. A session with any candidate is read
    /// every tick, as before.
    /// </summary>
    internal const double DispatchQuietMs = 60_000;

    /// <summary>The session's dispatches: read from the host, unless the last read found none, the inputs stamp has not moved and <see cref="DispatchQuietMs"/> has not passed.</summary>
    private IReadOnlyList<DispatchCandidate> DispatchesOf(Timer timer, string sid, double now)
    {
        // the stamp is read before the dispatches, so a write between the two moves it and the next tick reads again
        var stamp = _d.Host.DispatchInputsStamp(sid, now);
        if (stamp is not null && stamp == timer.QuietStamp && now - timer.QuietAt is >= 0 and < DispatchQuietMs)
        {
            return [];
        }

        var candidates = _d.Host.Dispatches(sid, now);
        timer.QuietStamp = candidates.Count == 0 ? stamp : null;
        timer.QuietAt = now;
        return candidates;
    }

    private async Task EvaluateSessionAsync(WatchRegistration reg, Timer timer, double now, CancellationToken cancellation)
    {
        var host = _d.Host;
        var sid = reg.SessionId;
        if (host.SessionCwd(sid) is not { } cwd)
        {
            return;
        }

        var home = reg.HierarchyDir;
        var events = new List<WatchEvent>();
        var lap = Stopwatch.StartNew();
        void Lap(string name)
        {
            _laps.Append(' ').Append(name).Append('=').Append(lap.ElapsedMilliseconds);
            lap.Restart();
        }

        var owned = _ownedByPrepass.TryGetValue(sid, out var read) ? read : host.OwnedTeams(sid, home);
        if (AllTeamsHeld(reg, owned))
        {
            return;
        }

        var candidates = DispatchesOf(timer, sid, now);
        var peerRecords = host.PeerRecords();
        var dispatch = DispatchClock.Evaluate(sid, now, _pollMs, candidates, peerRecords, host.Gates, host.ThresholdSeconds, OverdueTargetMemo.Wrap(c => OverdueTargetOf(owned, home, c, now, peerRecords)), _awake.Of);
        host.AppendGates(dispatch.Gates);
        host.AppendPeerRows(dispatch.PeerRows);

        host.MarkSurfaced(sid, dispatch.Surfaced);
        var dispatchEvents = dispatch.Events.ToList();
        foreach (var overdue in dispatch.Resumes)
        {
            WriteOverdueResume(sid, cwd, home, overdue, dispatchEvents);
        }

        events.AddRange(dispatchEvents);
        Lap("dispatch");

        var unreviewed = UnreviewedDone.Evaluate(sid, now, _startedMs, host.DoneReports(sid, now), peerRecords);
        host.AppendPeerRows(unreviewed.Rows);
        events.AddRange(unreviewed.Events);
        Lap("unreviewed");

        // An unarmed plain send ("thanks") holds nobody: the queue, board and unlinked-dispatch readers see only the requests whose clock runs.
        candidates = [.. candidates.Where(c => !Liveness.UnarmedPlainSend(c, peerRecords, sid))];
        try
        {
            Labels.Reconcile(owned, candidates, team => host.Checklist(sid, home, team), host.SessionTerminal(home, sid), now);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"work labels of {sid} not reconciled ({e.Message})");
        }

        IReadOnlyDictionary<WatchEvent, string> queueKeys;
        lock (_queueTold)
        {
            // TODO: a member whose session has live descendant processes (a running test or build) is also blocked on a job; add it once IProcessTable can list descendants.
            var ownedLive = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var t in owned)
            {
                ownedLive[t.Name ?? string.Empty] = t.Members.Count(static m => m.Live != false); // the Live flag the queue facts use
            }

            events.AddRange(QueueEvents.Evaluate(
                sid, now, [.. owned.Where(t => !host.TeamHeld(home, t.Name))], candidates, team => host.Checklist(sid, home, team), m => !string.IsNullOrEmpty(m.SessionId) && host.SlotStateOf(m.SessionId, now) is not null, _queueTold, out queueKeys,
                budgetOf: team => host.SessionBudgetOf(home, team), treeLiveOf: team => host.SessionTreeLive(home, team, ownedLive), unstaffedAt: _unstaffedAt, readyBacklog: _ => host.ReadyBacklog(sid, home), idleCapacityAt: _idleCapacityAt, remindAt: _remindAt, qaJobs: host.QaJobs()));
        }

        IReadOnlyDictionary<WatchEvent, IReadOnlyList<string>> unlinkedKeys;
        lock (_unlinkedTold)
        {
            events.AddRange(UnlinkedDispatchEvents.Evaluate(sid, now, owned, candidates, team => host.Checklist(sid, home, team), _unlinkedTold, _unlinkedMailedAt, out unlinkedKeys));
        }

        Lap("queue");

        IReadOnlyDictionary<WatchEvent, string> jobKeys;
        JobOutcome jobs;
        lock (_jobTold)
        {
            _capMarked = _capMarked || host.EnsureCapWatchMark();
            // the journal is read and parsed once per tick, for every team
            var caps = new Lazy<IReadOnlyList<JobCap>>(host.CapKills);
            jobs = JobEvents.Evaluate(sid, now, _pollMs, [.. owned.Select(static t => t.Name)], team => WatchModule.JobsCapped(host.ReconcileJobs(sid, home, team, host.Jobs(sid, home, team)), () => caps.Value), host.ThresholdSeconds, _jobTold, out jobKeys);
        }

        events.AddRange(jobs.Events);
        Lap("jobs");

        IReadOnlyDictionary<WatchEvent, Action> laneCommits = new Dictionary<WatchEvent, Action>();
        try
        {
            lock (_laneState)
            {
                events.AddRange(LaneEscalation.Evaluate(
                    sid, now, _downMs, host.Lanes(sid, home, owned), _laneState, out laneCommits, ev => EventBoxAddress(timer, owned, TeamOf(ev)) is { } address && host.AlreadyMailed(new WatchMail(address, home, cwd, ev.Name ?? ev.Kind, ev.Text, ev.Data, sid))));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"lane escalation of {sid} not evaluated ({e.Message})");
        }

        Lap("lanes");

        var context = ContextAlerts.Evaluate(home, host.Gates(home), host.ContextSubjects(sid, home), now);
        host.AppendGates(context.Gates);
        events.AddRange(context.Events);
        Lap("context");

        var blocked = BlockedEvents.Evaluate(
            sid,
            cwd,
            now,
            home,
            host.RosterCommand,
            owned,
            host.Gates(home),
            host.HerdrAgents,
            host.ReadPrompt,
            host.KindOf,
            host.PromptHeading,
            member => host.DispatchBaseMs(sid, member),
            _episodes);
        host.AppendGates(blocked.Gates);
        _covered.UnionWith(blocked.Covered);
        foreach (var (session, clear) in blocked.ScreenClear ?? [])
        {
            host.MarkScreenClear(home, session, clear, now);
        }

        events.AddRange(blocked.Events);
        Lap("blocked");

        var subjects = RecoveryEvents.SubjectsFor(sid, owned, host.KindOf, host.RowAdapted).Where(s => s.Team is null || !host.TeamHeld(home, s.Team)).ToList();
        var ownedNames = owned.Select(static t => t.Name).OfType<string>().ToList();
        var openRequestTeam = candidates
            .Where(c => c.Open && c.Team is not null && ownedNames.Contains(c.Team))
            .OrderByDescending(static c => c.Created, StringComparer.Ordinal)
            .Select(static c => c.Team)
            .FirstOrDefault();
        foreach (var member in subjects.Where(static s => !s.Own && s.Team is not null))
        {
            host.WithdrawRewakes($"{member.Name}@{member.Team}", null, null, now - Ah.Core.Digests.RewakePaths.Ceiling.TotalMilliseconds, "expired");
        }

        // b412: with no gates the episode count reads as k=1, whose resume id already exists; the next tick recovers instead.
        // b426: an outage of the gates read past RecoveryPausedAfterMs is told to the Orchestrator once, as an ordinary alarm.
        WatchEvent? pausedEvent = null;
        var pausedKey = sid + "|" + home;
        if (host.GatesOrNull(home, out var gatesError) is not { } recoveryGates)
        {
            _d.Log?.Invoke($"recovery of {sid} skipped this tick (the gates could not be read)");
            lock (_recoveryPaused)
            {
                if (!_recoveryPaused.TryGetValue(pausedKey, out var paused))
                {
                    _recoveryPaused[pausedKey] = paused = (now, false);
                }

                if (!paused.Told && now - paused.SinceMs >= RecoveryPausedAfterMs)
                {
                    var reason = gatesError ?? "unknown";
                    pausedEvent = new WatchEvent(null, "RECOVERY-PAUSED", $"ah: rewake recovery is paused: the state database cannot be read ({reason})", new JsonObject { ["home"] = home, ["reason"] = reason })
                    {
                        Name = RecoveryPausedName,
                        Team = RecoveryEvents.OwnEventTeam(ownedNames, openRequestTeam),
                    };
                    events.Add(pausedEvent);
                }
            }
        }
        else
        {
            lock (_recoveryPaused)
            {
                _ = _recoveryPaused.Remove(pausedKey);
            }

            var recovery = RecoveryEvents.Evaluate(sid, now, home, recoveryGates, subjects, _d.Recovery, host, _transcriptSeen, ownedNames, openRequestTeam, _outage.View, _tracedDecisions);
            host.AppendGates(recovery.Gates);
            foreach (var trace in recovery.Traces ?? [])
            {
                _d.TurnEnds?.Record(trace);
            }

            host.AppendPeerRows(recovery.PeerRows);
            events.AddRange(recovery.Events);
            foreach (var resume in recovery.Resumes ?? [])
            {
                var note = WriteResume(sid, cwd, home, resume);
                if (!resume.QuietWhenWritten || !ReferenceEquals(note, resume.Written))
                {
                    events.Add(note);
                }
            }
        }

        Lap("recovery");
        var ownTeam = RecoveryEvents.OwnEventTeam(ownedNames, openRequestTeam);
        var outageEvents = _outage.Events.TryGetValue(sid, out var told) ? told.Select(ev => ev with { Team = ownTeam }).ToList() : [];
        events.AddRange(outageEvents);

        var ownProbes = new Dictionary<WatchEvent, OutageProbe>();
        foreach (var probe in _outage.Probes.Where(p => p.Subject.Owner == sid))
        {
            if (probe.Subject.Own)
            {
                var probeEvent = new WatchEvent(null, "RESUME", $"ah: {probe.Body}", probe.Data) { Name = "resume", Team = ownTeam, Extra = probe.Data };
                ownProbes[probeEvent] = probe;
                events.Add(probeEvent);
            }
            else
            {
                WriteProbe(sid, cwd, home, probe);
            }
        }

        events.AddRange(TakeCopyUps(sid));
        var acks = new List<PeerAppend>();
        foreach (var ev in events)
        {
            var mailed = MailEvent(sid, timer, owned, ev, cwd, home, now);
            if (mailed && ev.Name == QueueEvents.IdleCapacityName)
            {
                lock (_queueTold)
                {
                    _idleCapacityAt[QueueEvents.UnstaffedTeamKey(sid, ev.Team)] = (now, QueueEvents.IdleCapacitySignature(ev));
                }
            }

            if (mailed && ev.Name == QueueEvents.StillWaitingName)
            {
                lock (_queueTold)
                {
                    _remindAt[QueueEvents.UnstaffedTeamKey(sid, ev.Team)] = now;
                }
            }

            if (mailed && queueKeys.TryGetValue(ev, out var episode))
            {
                lock (_queueTold)
                {
                    _ = _queueTold.Add(episode);
                    if (ev.Name == QueueEvents.UnstaffedName)
                    {
                        _unstaffedAt[QueueEvents.UnstaffedTeamKey(sid, ev.Team)] = now;
                    }
                }
            }

            if (mailed && unlinkedKeys.TryGetValue(ev, out var unlinked))
            {
                lock (_unlinkedTold)
                {
                    _unlinkedTold.UnionWith(unlinked);
                    _unlinkedMailedAt[UnlinkedDispatchEvents.CooldownKey(sid, ev.Team)] = now;
                }
            }

            if (mailed && laneCommits.TryGetValue(ev, out var commitLane))
            {
                lock (_laneState)
                {
                    commitLane();
                }
            }

            if (mailed && jobKeys.TryGetValue(ev, out var step))
            {
                lock (_jobTold)
                {
                    _ = _jobTold.Add(step);
                }

                if (ev.Data.Str("step") == JobEvents.CappedStep && ev.RequestId is { } capped)
                {
                    try
                    {
                        host.MarkCapTold(sid, home, ev.Team, capped);
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        _d.Log?.Invoke($"cap kill of job {capped} not marked told ({e.Message})");
                    }
                }

                MailOwners(sid, cwd, home, jobs.OwnerMails.Where(m => m.Data.Str("job") == ev.RequestId && m.Data.Str("step") == ev.Data.Str("step")));
            }

            if (mailed && ReferenceEquals(ev, pausedEvent))
            {
                lock (_recoveryPaused)
                {
                    if (_recoveryPaused.TryGetValue(pausedKey, out var paused))
                    {
                        _recoveryPaused[pausedKey] = (paused.SinceMs, true);
                    }
                }
            }

            if (!mailed && ownProbes.TryGetValue(ev, out var unsent))
            {
                RecordProbeFailed(unsent, now);
            }

            if (mailed && outageEvents.Contains(ev) && ev.Extra?.Str("outage") is { } kind && ev.Extra.Str("started_at") is { } startedAt)
            {
                acks.Add(new PeerAppend(new JsonObject
                {
                    ["type"] = "outage-ack",
                    ["ts"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
                    ["started_at"] = startedAt,
                    ["owner"] = sid,
                    ["event"] = kind,
                }));
            }
        }

        host.AppendPeerRows(acks);
        Lap("mail");

        try
        {
            await host.SweepLinksAsync(timer.Boxes, cwd, home, now, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"link sweep failed for {sid}: {e.Message}");
        }

        Lap("sweep");
    }

    private readonly StringBuilder _laps = new();

    private WorkLabels? _workLabels;

    private WorkLabels Labels => _workLabels ??= new WorkLabels(_d.Host.WriteTerminalLabels, _d.Log);

    /// <summary>The queue and waiting alarms already told, so each is told once per episode.</summary>
    private readonly HashSet<string> _queueTold = new(StringComparer.Ordinal);

    /// <summary>When each team last had a <c>queue-stall-unstaffed</c> mailed, by session and team. Every read, prune and write holds the lock of <see cref="_queueTold"/>: the evaluation that reads and prunes it runs inside that lock, and so does the write after a successful mail.</summary>
    private readonly Dictionary<string, double> _unstaffedAt = new(StringComparer.Ordinal);

    /// <summary>When each team last had an <c>idle-capacity</c> mailed and what it named, by session and team; held under the lock of <see cref="_queueTold"/> like <see cref="_unstaffedAt"/>.</summary>
    private readonly Dictionary<string, (double At, string Signature)> _idleCapacityAt = new(StringComparer.Ordinal);

    /// <summary>When each team's <c>work-still-waiting</c> clock last started or fired, by session and team; held under the lock of <see cref="_queueTold"/>.</summary>
    private readonly Dictionary<string, double> _remindAt = new(StringComparer.Ordinal);

    /// <summary>The <c>unlinked-dispatch</c> requests already told, by session, team and request id.</summary>
    private readonly HashSet<string> _unlinkedTold = new(StringComparer.Ordinal);

    /// <summary>When each (session, team) last had an <c>unlinked-dispatch</c> mailed. Every read and write holds the lock of <see cref="_unlinkedTold"/>.</summary>
    private readonly Dictionary<string, double> _unlinkedMailedAt = new(StringComparer.Ordinal);

    /// <summary>The mailbox event name of the alarm that rewake recovery stays paused.</summary>
    internal const string RecoveryPausedName = "recovery-paused";

    /// <summary>How long the gates read stays failed before the Orchestrator is told recovery is paused.</summary>
    internal const double RecoveryPausedAfterMs = 10 * 60 * 1000;

    /// <summary>Since when each (session, home) has had its gates unreadable, and whether that outage was told. Held under its own lock.</summary>
    private readonly Dictionary<string, (double SinceMs, bool Told)> _recoveryPaused = new(StringComparer.Ordinal);

    /// <summary>The (job, step) overrun alarms already told.</summary>
    private readonly HashSet<string> _jobTold = new(StringComparer.Ordinal);

    private bool _capMarked;
    private readonly LaneEscalation.State _laneState = new();
    private readonly double _downMs;

    /// <summary>Copies of an escalated lane's events waiting for the parent session's next pass, which mails them as ordinary events, by parent session.</summary>
    private readonly Dictionary<string, List<WatchEvent>> _copyUps = new(StringComparer.Ordinal);

    /// <summary>When each event copy was queued, by its identity, so one event reaches a recipient once.</summary>
    private readonly Dictionary<string, double> _copied = new(StringComparer.Ordinal);

    private const double CopiedKeepMs = 24 * 60 * 60 * 1000;

    /// <summary>
    /// While the lane whose box <paramref name="address"/> is stays escalated, queues a copy of the event just mailed there for the parent's
    /// orchestrating session, and for each further parent whose own lane is escalated in turn. A copy keeps the event's name and names the
    /// sub-orchestrator of the lane it left in <c>escalated_from</c>; a copy is never copied again here, the climb is walked once.
    /// </summary>
    private void CopyUp(string sid, string address, WatchEvent ev, string name, double now)
    {
        if (ev.Data.ContainsKey("escalated_from") || address.Split('@', 2) is not ["orchestrator", var lane])
        {
            return;
        }

        lock (_laneState)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var from = sid;
            while (visited.Add(lane) && OpenEpisodeOf(from, lane) is { } found && found.Episode.ParentTeam is { } parent && found.Episode.SubOrchestrator is { } sub)
            {
                var recipient = HierarchyModule.OrchestratorAddress(parent);
                var data = ev.Data.Clone();
                _ = data.Remove("member");
                data["escalated_from"] = JsonValue.FromString(sub);
                data["team"] = data.Get("team") ?? JsonValue.FromString(lane);
                var key = string.Join('|', name, ev.RequestId ?? data.Str("request_id"), data.Str("due_slot") ?? data.Str("step"), recipient, found.Episode.Slot, ev.Text);
                if (_copied.TryAdd(key, now))
                {
                    var copy = ev with { Text = $"escalated from {sub}: {ev.Text}", Data = data, Team = parent, Name = name };
                    if (!_copyUps.TryGetValue(found.Session, out var queue))
                    {
                        queue = [];
                        _copyUps[found.Session] = queue;
                    }

                    queue.Add(copy);
                }

                from = found.Session;
                lane = parent;
            }

            foreach (var old in _copied.Where(c => now - c.Value > CopiedKeepMs).Select(static c => c.Key).ToList())
            {
                _ = _copied.Remove(old);
            }
        }
    }

    /// <summary>The open escalation of <paramref name="lane"/> and the session that holds it, when that session owns the episode's parent team and is not <paramref name="notSession"/>.</summary>
    private (string Session, LaneEscalation.Episode Episode)? OpenEpisodeOf(string notSession, string lane)
    {
        var suffix = "|" + lane;
        foreach (var (key, episode) in _laneState.Escalated)
        {
            if (!key.EndsWith(suffix, StringComparison.Ordinal))
            {
                continue;
            }

            var session = key[..^suffix.Length];
            if (session != notSession && episode.ParentTeam is { } parent && _ownedByPrepass.TryGetValue(session, out var owned) && owned.Any(t => t.Name == parent))
            {
                return (session, episode);
            }
        }

        return null;
    }

    private List<WatchEvent> TakeCopyUps(string sid)
    {
        lock (_laneState)
        {
            if (!_copyUps.Remove(sid, out var queue))
            {
                return [];
            }

            return queue;
        }
    }

    private void MailOwners(string sid, string cwd, string home, IEnumerable<JobOwnerMail> mails)
    {
        foreach (var m in mails)
        {
            try
            {
                _d.Host.Mail(new WatchMail($"{m.Owner}@{m.Team}", home, cwd, m.Event, m.Text, m.Data, m.Session));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _d.Log?.Invoke($"job alert for {m.Owner}@{m.Team} (session {sid}) not mailed ({e.Message})");
            }
        }
    }

    /// <summary>How long a compaction must have gone unanswered before it is a stall, when two polls are shorter.</summary>
    private const double StallDebounceMs = 5 * 60 * 1000;

    /// <summary>The member an overdue dispatch is for, found among the teams the session owns; null when none of them has it.</summary>
    private OverdueTarget? OverdueTargetOf(IReadOnlyList<OwnedTeam> owned, string home, DispatchCandidate c, double now, IReadOnlyList<JsonObject> peerRecords)
    {
        var name = DispatchClock.NameOf(c.ToName, c.ToAddr);
        var addressTeam = c.ToAddr is { } to && to.Contains('@', StringComparison.Ordinal) ? to[(to.IndexOf('@', StringComparison.Ordinal) + 1)..] : c.Team;
        foreach (var team in owned.Where(t => addressTeam is null || t.Name == addressTeam))
        {
            if (team.Members.FirstOrDefault(m => m.Name == name) is not { } member)
            {
                continue;
            }

            var record = string.IsNullOrEmpty(member.SessionId) ? null : _d.Host.ReadActivity(home, member.SessionId);
            var address = string.IsNullOrEmpty(member.SessionId) || team.Name is null ? null : $"{member.Name}@{team.Name}";
            double? Instant(string field) => record?.Str(field) is { } text && Timestamps.TryParseMs(text, out var ms) ? ms : null;
            var busySeen = _d.Host.PeerBusySeenMs(home, member.Name, team.Name);

            // A member that went idle after the last look at the roster has ended the turn that look saw.
            var busy = _d.Host.PeerBusy(home, member.Name, team.Name)
                && !(record?.Str("activity") is "idle" or "done" && Instant("state_since") is { } idleSince && busySeen < idleSince);
            var verdict = _d.Host.RecordVerdict(record, now, busy, Math.Max(StallDebounceMs, 2 * _pollMs));

            // fewer-turns S3.1: idle on its own background work holds the nudges, for at most 2 x the eta threshold from the oldest launch,
            // since a lost completion must not silence them for good; past that the member reads as idle again.
            var bgOpen = record is not null && HierarchyModule.IdleOnBackgroundWork(JsonValue.FromObject(record), now);
            var bgWaiting = bgOpen && now - (Instant("bg_since") ?? Instant("state_since") ?? now) < 2 * _d.Host.ThresholdSeconds(c.Eta) * 1000;
            if (bgOpen && !bgWaiting && verdict == "waiting")
            {
                verdict = "idle";
            }

            return new OverdueTarget(
                address,
                member.SessionId ?? string.Empty,
                verdict == "idle",
                record is not null,
                verdict is "working" or "waiting",
                Instant("state_since") ?? Instant("at"),
                busy,
                verdict is "blocked" or "failed" or "stalled-after-compact",
                verdict == "stalled-after-compact",
                record?.Str("compacted_at"),
                string.IsNullOrEmpty(member.SessionId) ? null : _d.Host.SlotStateOf(member.SessionId, now),
                Instant("working_since"),
                Instant("working_at"),
                !string.IsNullOrEmpty(member.SessionId) && Ah.Roster.RosterModule.ChainDownActive(peerRecords.Select(static r => (JsonValue)r), member.SessionId),
                busySeen,
                bgWaiting);
        }

        return null;
    }

    /// <summary>Writes the probe resume of an outage to a member's own box. No note goes to the Orchestrator: the outage events say it.</summary>
    private void WriteProbe(string sid, string cwd, string home, OutageProbe probe)
    {
        if (probe.Subject.Team is null)
        {
            _d.Log?.Invoke($"outage probe for {probe.Subject.Id} (session {sid}) not written (its team is unknown)");
            RecordProbeFailed(probe, _d.Clock.UtcNow.ToUnixTimeMilliseconds());
            return;
        }

        if (_d.Host.TeamHeld(home, probe.Subject.Team))
        {
            return;
        }

        try
        {
            _d.Host.Mail(new WatchMail($"{probe.Subject.Name}@{probe.Subject.Team}", home, cwd, "resume", probe.Body, probe.Data, probe.Subject.Id));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"outage probe for {probe.Subject.Id} (session {sid}) not written ({e.Message})");
            RecordProbeFailed(probe, _d.Clock.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    /// <summary>Records that a probe could not be delivered, so the next pre-pass picks another probe session.</summary>
    private void RecordProbeFailed(OutageProbe probe, double now) =>
        _d.Host.AppendPeerRows([
            new PeerAppend(new JsonObject
            {
                ["type"] = "outage-probe-failed",
                ["ts"] = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now)),
                ["started_at"] = probe.Data.Str("started_at") ?? string.Empty,
                ["session_id"] = probe.Subject.Id,
                ["probe"] = probe.Data.Get("probe") ?? JsonValue.Null,
            }),
        ]);

    /// <summary>Writes the resume entry of an overdue brief and, if that fails, says so in the event that reported it.</summary>
    private void WriteOverdueResume(string sid, string cwd, string home, OverdueResume overdue, List<WatchEvent> events)
    {
        if (AddressHeld(home, overdue.Address))
        {
            return;
        }

        try
        {
            _d.Host.Mail(new WatchMail(overdue.Address, home, cwd, "resume", overdue.Body, overdue.Data, overdue.Session));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"overdue resume for {overdue.Address} (session {sid}) not written ({e.Message})");
            var ev = events[overdue.EventIndex];
            events[overdue.EventIndex] = ev with { Text = ev.Text.Replace(overdue.Written, overdue.NotWritten($"the entry was not written ({e.Message})"), StringComparison.Ordinal) };
        }
    }

    /// <summary>
    /// Writes a member's resume to its own box and returns the event the Orchestrator is told: that the member was resumed
    /// directly, or, when the entry could not be written, the text to send it by hand.
    /// </summary>
    private WatchEvent WriteResume(string sid, string cwd, string home, MemberResume resume)
    {
        if (resume.Address is null)
        {
            return resume.NotWritten("its team is unknown");
        }

        if (AddressHeld(home, resume.Address))
        {
            return resume.NotWritten("its team is held");
        }

        try
        {
            if (resume.Id is not null)
            {
                _d.Host.WithdrawRewakes(resume.Address, resume.Session, resume.Id, null, "superseded");
            }

            _d.Host.Mail(new WatchMail(resume.Address, home, cwd, "resume", resume.Body, resume.Data, resume.Session, resume.Id));
            return resume.Written;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"resume for {resume.Address} (session {sid}) not written ({e.Message})");
            return resume.NotWritten($"the entry was not written ({e.Message})");
        }
    }

    /// <summary>
    /// The address an event for <paramref name="team"/> is mailed to. The team's registered box when the session polls it. When it
    /// does not, the freshest polled box of a team the session owns, else the team's own box (the mail layer finds or creates it)
    /// when it is registered or the session owns the team, else null. An event with no team goes to a registered box of an owned
    /// named team (the freshest, else the first), else to the freshest registered box, else the first. A named team's event never
    /// falls back to the box of a team the session does not own.
    /// </summary>
    private static string? EventBoxAddress(Timer timer, IReadOnlyList<OwnedTeam> owned, string? team)
    {
        var ownedNames = owned.Select(static t => t.Name).OfType<string>().ToHashSet(StringComparer.Ordinal);
        bool IsOwnedBox(string address) => address.Split('@', 2) is [_, var boxTeam] && ownedNames.Contains(boxTeam);
        var freshestOwned = timer.Beats.Where(b => IsOwnedBox(b.Key)).OrderByDescending(static b => b.Value).ThenBy(static b => b.Key, StringComparer.Ordinal).Select(static b => b.Key).FirstOrDefault();
        if (team is null)
        {
            var registeredOwned = timer.Registered.Where(b => IsOwnedBox(b.Address)).OrderByDescending(b => timer.Beats.GetValueOrDefault(b.Address)).FirstOrDefault();
            var freshest = timer.Beats.OrderByDescending(static b => b.Value).ThenBy(static b => b.Key, StringComparer.Ordinal).Select(static b => b.Key).FirstOrDefault();
            return registeredOwned?.Address ?? freshest ?? (timer.Boxes.Count > 0 ? timer.Boxes[0].Address : null);
        }

        var own = timer.Registered.FirstOrDefault(b => b.Address.EndsWith($"@{team}", StringComparison.Ordinal))?.Address;
        if (own is not null && timer.Beats.ContainsKey(own))
        {
            return own;
        }

        if (own is null)
        {
            return ownedNames.Contains(team) ? HierarchyModule.OrchestratorAddress(team) : null;
        }

        return freshestOwned ?? own;
    }

    private static string? TeamOf(WatchEvent ev)
    {
        var member = ev.Data.Str("member");
        return member is not null && member.Contains('@', StringComparison.Ordinal) ? member.Split('@', 2)[1] : ev.Team;
    }

    private bool MailEvent(string sid, Timer timer, IReadOnlyList<OwnedTeam> owned, WatchEvent ev, string cwd, string home, double now)
    {
        var host = _d.Host;
        var team = TeamOf(ev);
        var name = ev.Name ?? DispatchClock.EventName.GetValueOrDefault(ev.Kind) ?? ev.Kind;
        // while T's deputy is on duty the chief holds chief@T: T's alarms go to orchestrator@T, the deputy's box
        var onDuty = team is not null && owned.FirstOrDefault(t => t.Name == team) is { } duty && QueueEvents.DeputyLive(duty);
        if ((ev.To ?? (onDuty ? HierarchyModule.OrchestratorAddress(team) : EventBoxAddress(timer, owned, team))) is not { } address)
        {
            _d.Log?.Invoke($"event {name} for team {team} has no box in session {sid}; not delivered");
            return true;
        }

        var (text, tooLong, pointer) = EventMailText(ev, name);
        if (team is not null && !address.EndsWith($"@{team}", StringComparison.Ordinal))
        {
            // The event went to another owned team's box: it keeps its own team field and its text names the team it is for.
            ev.Data["team"] = ev.Data.Get("team") ?? team;
            text = $"for team {team}: {text}";
            pointer = $"for team {team}: {pointer}";
        }

        void Mail(string body) => host.Mail(new WatchMail(address, home, cwd, name, body, ev.Data, sid));

        if (tooLong)
        {
            host.RecordWatchEvents(sid, [ev]);
        }

        var mailed = true;
        try
        {
            Mail(text);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"event {ev.Kind} for {sid} not mailed ({e.Message})");
            if (!tooLong)
            {
                host.RecordWatchEvents(sid, [ev]);
            }

            try
            {
                Mail(pointer);
            }
            catch (Exception e2) when (e2 is not OperationCanceledException)
            {
                mailed = false;
                _d.Log?.Invoke($"pointer for {ev.Kind} to {sid} not mailed either ({e2.Message}); the full text is in the watch log");
            }
        }

        timer.LastEventKind = name;
        timer.LastEventAt = Timestamps.ToIsoString(DateTimeOffset.FromUnixTimeMilliseconds((long)now));
        if (mailed)
        {
            CopyUp(sid, address, ev, name, now);
        }

        return mailed;
    }

    private async Task LadderPassAsync(double now, CancellationToken cancellation)
    {
        if (Interlocked.Exchange(ref _ladderRunning, 1) != 0)
        {
            if (Interlocked.Exchange(ref _ladderSkipTold, 1) == 0)
            {
                _d.Log?.Invoke("delivery ladder pass skipped: the previous pass is still running");
            }

            return;
        }

        _ = Interlocked.Exchange(ref _ladderSkipTold, 0);
        try
        {
            _ladder = await _d.Host.RunLadderAsync(now, _pollMs, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _d.Log?.Invoke($"delivery ladder failed: {e}");
        }
        finally
        {
            _ = Interlocked.Exchange(ref _ladderRunning, 0);
        }
    }

    // every status dir this daemon has seen; refresh-status-dirs runs on one timer, never concurrently with itself
    private readonly HashSet<string> _seenStatusDirs = new(StringComparer.Ordinal);

    /// <summary>The 2 s backstop: every live registration's hierarchy dir (files-off-disk F3a: the document is served from the database).</summary>
    internal void RefreshStatusDirs()
    {
        var dirs = _d.Host.LiveHierarchyDirs().Where(static dir => dir.Length > 0).ToList();

        // the daemon's first pass sees every dir as new, so this is also the start-up rebuild
        var seen = _seenStatusDirs;
        foreach (var dir in dirs.Where(seen.Add))
        {
            _d.Host.StatusDirFirstSeen(dir);
        }

        // a database write that crashed between its commit and its status delta left a mark: one rebuild heals it
        _d.Host.HealDirtyStatus();
        _d.StatusDirs.Replace(dirs);
    }
}
