using Ah.Abstractions;
using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Watch;

/// <summary>
/// What a team-file row records of a Claude found in the member's pane that ah did not launch.
/// <see cref="Matched"/> is the state a member reads Live (bare) in; <see cref="Unmatched"/> is recorded for the doctor and never acted on.
/// </summary>
/// <param name="State">Either <see cref="Matched"/> or <see cref="Unmatched"/>.</param>
/// <param name="Pid">The Claude process.</param>
/// <param name="PidStart">Its start time.</param>
/// <param name="SessionId">The session on its <c>--resume</c>.</param>
/// <param name="Since">When this state was first recorded for this process.</param>
/// <param name="Failed">The match checks that failed, empty for a matched one.</param>
public sealed record BareMark(string State, int? Pid, string? PidStart, string? SessionId, DateTimeOffset Since, IReadOnlyList<string> Failed)
{
    /// <summary>A bare Claude that matches its member on every check.</summary>
    public const string Matched = "bare";

    /// <summary>A bare Claude that fails a check.</summary>
    public const string Unmatched = "bare-unmatched";

    /// <summary>A Claude ah launched whose agent, model or effort differ from the row; <see cref="Failed"/> holds the line that says how. Reported only.</summary>
    public const string Drift = "drift";

    /// <summary>The mark a row's <c>bare</c> field holds; null when the field is absent or not a mark.</summary>
    public static BareMark? From(JsonValue? field)
    {
        if (field is not { IsMap: true } map || map.Member("state") is not { IsText: true } state)
        {
            return null;
        }

        return new BareMark(
            state.AsString(),
            map.Member("pid") is { Kind: JsonKind.Number } p ? (int)p.AsNumber() : null,
            map.Member("pid_start") is { IsText: true } start ? start.AsString() : null,
            map.Member("session_id") is { IsText: true } session ? session.AsString() : null,
            map.Member("since") is { IsText: true } since
                && DateTimeOffset.TryParse(since.AsString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)
                    ? at
                    : DateTimeOffset.MinValue,
            map.Member("failed") is { IsArray: true } failed ? failed.AsArray().Where(f => f.IsText).Select(f => f.AsString()).ToList() : []);
    }

    /// <summary>The value of a row's <c>bare</c> field for this mark.</summary>
    public JsonObject ToJson()
    {
        var checks = new JsonArray();
        foreach (var check in Failed)
        {
            checks.Add(JsonValue.FromString(check));
        }

        return new JsonObject
        {
            ["state"] = State,
            ["pid"] = Pid is { } pid ? pid : JsonValue.Null,
            ["pid_start"] = PidStart is { } start ? start : JsonValue.Null,
            ["session_id"] = SessionId is { } session ? session : JsonValue.Null,
            ["since"] = Since.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["failed"] = checks,
        };
    }
}

/// <summary>The key of a hierarchy directory for a member's recovery rows (<c>recover_records</c>).</summary>
internal static class RecoverPaths
{
    /// <summary>A short, fixed name for a hierarchy directory, so two projects' teams and members of the same names never share a file.</summary>
    public static string DirKey(string hierarchyDir) => Keys.GetOrAdd(hierarchyDir, static dir => Hash(MsgRows.PoolKey(dir)));

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Keys = new(StringComparer.Ordinal);

    private static string Hash(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
}

/// <summary>What the stall watch may ask about a bare-member recovery record without knowing its shape.</summary>
internal static class RecoveryState
{
    /// <summary>What a relaunch error starts with when the member's pane could not be read; the recovery asks the Orchestrator rather than retry.</summary>
    public const string PaneUnknownPrefix = "pane state unknown: ";

    /// <summary>What a relaunch error starts with when herdr refused the launch; retrying cannot change the answer, so the recovery asks the Orchestrator.</summary>
    public const string RefusalPrefix = "launch refused: ";

    /// <summary>The state of a recovery record whose team was held when the member was found.</summary>
    public const string HeldState = "held";

    /// <summary>Whether a recovery record in <paramref name="state"/> means the member is being brought back (or waits on a hold), so a dead registration is expected.</summary>
    public static bool Covers(string? state) => state is not null && RecoverRecord.IsPending(state);
}

/// <summary>
/// A member the bare watch examines: its pane facts and the mark its row holds now. With or without a <c>pid</c> it is
/// still settling for <c>LaunchPatience</c> after <c>SpawnedAt</c>, its <c>spawned_at</c>.
/// </summary>
public sealed record BareRow(string Name, string? Role, BareMember Member, BareMark? Stored, DateTimeOffset? SpawnedAt = null, bool HasPid = true);

/// <summary>What the bare watch reads and writes. It never signals a process, types into a pane or closes one.</summary>
public interface IBareHost
{
    /// <summary>Whether this daemon still owns the fence; an unfenced daemon acts on nothing.</summary>
    bool Owner();

    IReadOnlyList<StallTeam> Teams();

    /// <summary>The Claude members of <paramref name="team"/> that run in a herdr pane and are tracked.</summary>
    IReadOnlyList<BareRow> Rows(StallTeam team);

    /// <summary>The detector's answer for <paramref name="row"/>, given what the previous tick saw there.</summary>
    BareVerdict Check(StallTeam team, BareRow row, BareSighting? earlier);

    /// <summary>Writes <paramref name="mark"/> on the member's row, or removes the mark when null.</summary>
    void SetMark(StallTeam team, string member, BareMark? mark);

    void Mail(WatchMail mail);

    void Journal(JsonObject row);

    /// <summary>The member's recovery record, or null when none was written.</summary>
    JsonValue? ReadRecord(StallTeam team, string member);

    void WriteRecord(StallTeam team, string member, JsonObject record);

    /// <summary>Whether recovery is on. Off, the watch still detects, marks and mails, and never signals or relaunches a process.</summary>
    bool RecoveryEnabled { get; }

    /// <summary>Switches recovery off because <paramref name="member"/> was left dead; the same writer as <c>recover disable</c>, attributed to the daemon. Never switches it on.</summary>
    void DisableRecovery(StallTeam team, string member, string reason);

    /// <summary>The session id the member last ran under, from the same source the detector uses; null when unknown.</summary>
    string? LastSessionId(StallTeam team, BareRow row) => null;

    /// <summary>Whether the team is held (its own hold or an ancestor's); a held team's members are never stopped or relaunched.</summary>
    bool TeamHeld(StallTeam team) => false;

    /// <summary>
    /// The API-error records that appeared in the transcripts of the teams' registered sessions since the last call, none older than
    /// <paramref name="notBefore"/>. A transcript seen for the first time is read back only that far.
    /// </summary>
    IReadOnlyList<ApiErrorSighting> ApiErrors(IReadOnlyList<StallTeam> teams, DateTimeOffset notBefore) => [];

    /// <summary>The pane the member's row names now, or null; read after a relaunch to see whether it moved.</summary>
    string? PaneOf(StallTeam team, string member) => null;

    /// <summary>What the daemon holds for the member from the owner's <c>recover hold|release|now</c> requests (<c>hold</c>, <c>now_at</c>), or null when none was made.</summary>
    JsonValue? ReadControl(StallTeam team, string member);

    /// <summary>Herdr's <c>agent_status</c> for the member's pane (<c>idle</c>, <c>working</c>, …), or null when unreadable.</summary>
    string? AgentStatus(StallTeam team, BareRow row);

    /// <summary>What keeps the member from being stopped: an open request addressed to it, a running job or background work. Empty when there is none.</summary>
    IReadOnlyList<string> OpenWork(StallTeam team, BareRow row);

    /// <summary>The open background tasks the session's activity record counts when its turn last ended; 0 when it records none.</summary>
    int BackgroundOpen(StallTeam team, string? sessionId);

    /// <summary>Sends a relaunched member, and only it, its resume brief from the team's newest checkpoint. Null when sent or there is nothing to send, else why not.</summary>
    string? ReBrief(StallTeam team, BareRow row);

    /// <summary>Sends SIGTERM to <paramref name="pid"/> when it still has start time <paramref name="pidStart"/>; false when it does not or the signal failed. Never sends any other signal.</summary>
    bool Terminate(int pid, string? pidStart);

    /// <summary>Whether the process (same pid and start time) is gone within <paramref name="wait"/>.</summary>
    bool WaitExit(int pid, string? pidStart, TimeSpan wait);

    /// <summary>Starts the member again in its own pane with <c>--resume</c>; null on success, else the error.</summary>
    string? Relaunch(StallTeam team, BareRow row, string sessionId);

    /// <summary>
    /// The launch <see cref="Relaunch"/> would make, checked while the member still runs and with nothing started or written: null when it
    /// would go ahead, else why it would be refused.
    /// </summary>
    string? PreflightRelaunch(StallTeam team, BareRow row, string sessionId) => null;

    /// <summary>
    /// Whether the member's row now holds a process other than <paramref name="oldPid"/> with a start time (what <c>RecordMemberProcess</c>
    /// writes for an ah-launched Claude), and that process runs <c>--resume</c> <paramref name="sessionId"/> with ah's launch.
    /// </summary>
    bool Verified(StallTeam team, BareRow row, int oldPid, string sessionId);

    /// <summary>The output of <c>git worktree list --porcelain</c> run in <paramref name="cwd"/>; null when git failed.</summary>
    string? WorktreeList(string cwd) => null;
}

/// <summary>
/// The daemon job that finds a member whose Claude was started without ah's flags (a terminal multiplexer restoring its panes) and
/// marks the row so the member reads Live (bare). It acts only by writing the mark, one journal row and one event per change.
/// </summary>
internal sealed partial class BareWatch(IBareHost host, TimeSpan period, Func<DateTimeOffset> now, Action<string>? stamp = null, Func<Action, Task>? run = null, ILoadWatch? load = null, WorktreeWatch? worktrees = null)
{
    public const string JobName = "bare-watch";

    /// <summary>The recovery task each member has in flight; the sweep only looks at whether it is still running.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task> _inFlight = new(StringComparer.Ordinal);
    private int _counted;

    private readonly Dictionary<string, BareSighting> _seen = new(StringComparer.Ordinal);

    /// <summary>When this daemon first found the pane of a <c>stopping</c> or <c>launching</c> record unreadable, since its last good read. After a reboot a record's age says nothing about the pane.</summary>
    private readonly Dictionary<string, DateTimeOffset> _unreadableSince = new(StringComparer.Ordinal);

    public void Register(IJobRegistry jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        // The job is ticked twice per period, and a tick runs a sweep once a period has passed since the last one STARTED, so sweeps
        // keep a fixed cadence and one that overran is followed by the next at the next tick; never two at once. Gating on the last
        // END pushed every later sweep by the sweep's length plus a skipped tick (45 s apart at a 30 s period). Half a tick of slack
        // keeps a timer that fires a hair early from skipping a whole tick. The sweep runs on the thread pool: the daemon's loop is
        // shared with every other job and the routes.
        var tick = Math.Max(1, period.Ticks / 2);
        jobs.Add(JobName, TimeSpan.FromTicks(tick), async cancellation =>
        {
            var at = now().UtcTicks;
            var started = Interlocked.Read(ref _lastStartTicks);
            if (started != 0 && at - started < period.Ticks - (tick / 2))
            {
                return;
            }

            if (Interlocked.Exchange(ref _sweeping, 1) != 0)
            {
                return;
            }

            _ = Interlocked.Exchange(ref _lastStartTicks, at);
            var sweep = Task.Run(
                () =>
                {
                    try
                    {
                        SweepUntilDrained(cancellation);
                    }
                    finally
                    {
                        // Cleared before the task completes, so a tick that runs once the job has awaited it never sees the flag still set.
                        _ = Interlocked.Exchange(ref _sweeping, 0);
                    }
                },
                CancellationToken.None);

            // On a drain the sweep stops at its next team boundary; the job waits for that, but only so long.
            var bound = new TaskCompletionSource();
            await using var registration = cancellation.Register(() => _ = Task.Delay(DrainWait, CancellationToken.None).ContinueWith(_ => bound.TrySetResult(), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default));
            if (await Task.WhenAny(sweep, bound.Task).ConfigureAwait(false) != sweep)
            {
                return;
            }

            await sweep.ConfigureAwait(false);
            stamp?.Invoke(JobName);
        });
    }

    /// <summary>The longest a drain waits for a sweep that is part-way through a team.</summary>
    internal TimeSpan DrainWait { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>When the last sweep started, in UTC ticks, 0 before the first; read and written across threads, so only by interlock.</summary>
    private long _lastStartTicks;

    private int _sweeping;

    public void Sweep() => SweepUntilDrained(CancellationToken.None);

    /// <summary>One pass over every watched member; a drain stops it before the next team.</summary>
    internal void SweepUntilDrained(CancellationToken drain)
    {
        if (!host.Owner())
        {
            return;
        }

        RetryOwedBrakes();
        _sweepTeams = host.Teams();
        SampleLoad(_sweepTeams);
        SampleWorktrees(_sweepTeams);
        _sweepRows = new Dictionary<string, IReadOnlyList<BareRow>>(StringComparer.Ordinal);
        foreach (var team in _sweepTeams)
        {
            if (drain.IsCancellationRequested)
            {
                return;
            }

            foreach (var row in RowsOf(team))
            {
                Guarded(team, row.Name, () => SweepMember(team, row));
            }

            Guarded(team, null, () => Digest(team, now()));
        }
    }

    /// <summary>
    /// Runs one member's step (or the team's digest, <paramref name="member"/> null): a record that cannot be read or written (a busy
    /// store, or a refused write) skips it this pass with nothing written for it, journalled, and the sweep goes on.
    /// The store's refused record write is an InvalidOperationException no different in type from a code bug's, so the
    /// journal row names the exception type to keep a bug visible.
    /// </summary>
    private void Guarded(StallTeam team, string? member, Action step)
    {
        try
        {
            step();
        }
        catch (Exception e) when (e is StoreUnavailableException or IOException or InvalidOperationException)
        {
            try
            {
                host.Journal(new JsonObject
                {
                    ["at"] = now().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    ["kind"] = "recover-error",
                    ["team"] = team.Name,
                    ["member"] = member is null ? JsonValue.Null : member,
                    ["error_type"] = e.GetType().FullName ?? e.GetType().Name,
                    ["error"] = e.Message,
                });
            }
            catch (Exception j) when (j is not OperationCanceledException)
            {
                // the journal refused the recover-error row too: nothing else can record it, and the sweep goes on
            }
        }
    }

    private void SweepMember(StallTeam team, BareRow row)
    {
        // a fresh spawn is still settling, pid or not: no bare state and no recovery until the patience is up. A relaunch
        // writes spawned_at too, but the member it launched is checked at once: the recovery waits on that check.
        if (row.SpawnedAt is { } spawned && now() - spawned < LaunchPatience
            && RecoverRecord.From(host.ReadRecord(team, row.Name)) is not { State: RecoverRecord.Launching })
        {
            return;
        }

        var key = team.HierarchyDir + "|" + team.Name + "/" + row.Name;
        var earlier = _seen.GetValueOrDefault(key) ?? (row.Stored is { Pid: { } storedPid } stored ? new BareSighting(storedPid, stored.PidStart) : null);
        var verdict = host.Check(team, row, earlier);
        if (verdict.Kind == BareKind.Unreadable)
        {
            PendingOnUnreadablePane(team, row, verdict);
            return;
        }

        _unreadableSince.Remove(InFlightKey(team, row));

        if (verdict.Pid is { } pid)
        {
            _seen[key] = new BareSighting(pid, verdict.PidStart);
        }
        else
        {
            _ = _seen.Remove(key);
        }

        if (!host.TeamHeld(team))
        {
            Apply(team, row, verdict);
        }

        Recover(team, row, verdict);
    }

    /// <summary>
    /// Takes the machine-load sample of this pass and, when the state changed, journals one <c>load-transition</c> row. Nobody is
    /// mailed (ah2-249), and a failed sample never stops the sweep.
    /// </summary>
    private void SampleLoad(IReadOnlyList<StallTeam> teams)
    {
        if (load is null)
        {
            return;
        }

        LoadAlarm? alarm;
        try
        {
            alarm = load.Tick(from => host.ApiErrors(teams, from));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            host.Journal(new JsonObject
            {
                ["at"] = now().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["kind"] = "load-error",
                ["error"] = e.Message,
            });
            return;
        }

        if (alarm is null)
        {
            return;
        }

        host.Journal(new JsonObject
        {
            ["at"] = now().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["kind"] = "load-transition",
            ["state"] = alarm.Data["state"],
            ["triggers"] = alarm.Data["load"].Member("triggers") ?? (JsonValue)new JsonArray(),
        });
    }

    /// <summary>
    /// Counts each watched repo's work trees when a sample is due and, when a count starts a <c>worktrees-piling-up</c> episode, mails
    /// the Orchestrator of each of that repo's teams that is not held. A repo whose git call fails is skipped, with no mail.
    /// </summary>
    private void SampleWorktrees(IReadOnlyList<StallTeam> teams)
    {
        if (worktrees is null || !worktrees.Due())
        {
            return;
        }

        var byRepo = new Dictionary<string, (int Linked, List<StallTeam> Teams)>(StringComparer.Ordinal);
        foreach (var team in teams)
        {
            if (host.WorktreeList(team.Cwd) is not { } text || WorktreeWatch.Parse(text) is not { } parsed)
            {
                continue;
            }

            if (!byRepo.TryGetValue(parsed.Repo, out var entry))
            {
                entry = (parsed.Linked, []);
                byRepo[parsed.Repo] = entry;
            }

            entry.Teams.Add(team);
        }

        foreach (var (repo, (linked, repoTeams)) in byRepo)
        {
            if (worktrees.Sample(repo, linked) is not { } text)
            {
                continue;
            }

            foreach (var team in repoTeams)
            {
                if (team.OrchestratorAddress is { } to && !host.TeamHeld(team))
                {
                    host.Mail(new WatchMail(to, team.HierarchyDir, team.Cwd, WorktreeWatch.AlarmKind, text, new JsonObject { ["repo"] = repo, ["count"] = linked }, team.OrchestratorSession));
                }
            }
        }
    }

    private void Apply(StallTeam team, BareRow row, BareVerdict verdict)
    {
        var state = verdict.Kind switch
        {
            BareKind.Bare => BareMark.Matched,
            BareKind.Unmatched => BareMark.Unmatched,
            BareKind.Drift => BareMark.Drift,
            _ => null,
        };
        var stored = row.Stored;
        if (stored?.State == state && (state is null || (stored!.Pid == verdict.Pid && stored.PidStart == verdict.PidStart)))
        {
            return;
        }

        var at = now();
        var since = stored is not null && stored.State == state && stored.Pid == verdict.Pid ? stored.Since : at;
        IReadOnlyList<string> detail = state == BareMark.Drift ? [verdict.Why] : verdict.Failed;
        host.SetMark(team, row.Name, state is null ? null : new BareMark(state, verdict.Pid, verdict.PidStart, verdict.SessionId, since, detail));
        host.Journal(new JsonObject
        {
            ["at"] = at.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["team"] = team.Name,
            ["kind"] = "bare-state",
            ["member"] = row.Name,
            ["from"] = stored?.State ?? "ok",
            ["to"] = state ?? "ok",
            ["pid"] = verdict.Pid is { } p ? p : JsonValue.Null,
            ["pid_start"] = verdict.PidStart is { } s ? s : JsonValue.Null,
            ["session_id"] = verdict.SessionId is { } sid ? sid : JsonValue.Null,
            ["reason"] = verdict.Why,
            ["checks"] = Checks(verdict.Failed),
        });
        var matchedMail = state == BareMark.Matched && !(host.RecoveryEnabled && !IsOrchestrator(team, row));
        if ((matchedMail || state == BareMark.Unmatched) && team.OrchestratorAddress is { } to)
        {
            var matched = state == BareMark.Matched;
            var text = matched
                ? $"{row.Name} runs a Claude that ah did not launch (no --agent, --model, --effort or --settings): its model, effort and hooks were not applied. It reads Live (bare). {(IsOrchestrator(team, row) ? WatchModule.BareOrchestratorSentence : WatchModule.BareSentence(host.RecoveryEnabled, team.Name, row.Name))}"
                : $"a Claude without ah's launch is running in {row.Name}'s pane; it does not match the member: {string.Join(", ", verdict.Failed)}. Nothing is done about it.";
            host.Mail(new WatchMail(
                to,
                team.HierarchyDir,
                team.Cwd,
                matched ? "member-bare" : "member-bare-unmatched",
                text,
                new JsonObject { ["member"] = row.Name, ["checks"] = Checks(verdict.Failed) },
                team.OrchestratorSession));
        }
    }

    private static JsonArray Checks(IReadOnlyList<string> failed)
    {
        var list = new JsonArray();
        foreach (var check in failed)
        {
            list.Add(JsonValue.FromString(check));
        }

        return list;
    }
}
