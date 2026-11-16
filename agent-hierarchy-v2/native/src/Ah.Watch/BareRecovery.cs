using System.Globalization;
using Ah.Core.Json;
using Ah.Launch;

namespace Ah.Watch;

/// <summary>
/// Where one member's recovery stands. Written only by the daemon, in a row keyed by the hierarchy directory, the team and the member;
/// the owner's hold and now requests are kept by the daemon in a separate row so the two never overwrite each other.
/// </summary>
internal sealed record RecoverRecord
{
    public const string Detected = "detected";
    public const string WaitingIdle = "waiting-idle";
    public const string Stopping = "stopping";
    public const string Launching = "launching";
    public const string Done = "done";
    public const string Asked = "asked";
    public const string Failed = "failed";
    public const string Cleared = "cleared";

    /// <summary>The member's team is held: nothing is stopped or launched until the hold is lifted.</summary>
    public const string Held = RecoveryState.HeldState;

    /// <summary>The states a recovery ends in until something changes; the digest reports each once.</summary>
    public static bool IsTerminal(string state) => state is Done or Failed or Asked;

    /// <summary>The states of a recovery still under way, which the digest waits for; a held member waits for its hold to lift.</summary>
    public static bool IsPending(string state) => state is Detected or WaitingIdle or Stopping or Launching or Held;

    public string State { get; init; } = Detected;

    /// <summary>When the record last moved into a terminal state; the digest's wait for the rest of the team runs from here.</summary>
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Why a waiting member has not started yet (it is queued behind another relaunch); not an ask, and cleared when it starts.</summary>
    public string? Note { get; init; }

    /// <summary>Set when the relaunch had to use a different pane than the one the member was found in; the digest repeats it.</summary>
    public string? PaneNote { get; init; }

    /// <summary>Whether the member was held while its team was held; the digest says so.</summary>
    public bool WasHeld { get; init; }

    /// <summary>Whether a digest has named this record in its current terminal state; cleared on every move into a terminal state.</summary>
    public bool Reported { get; init; }

    public string? SessionId { get; init; }

    public int Pid { get; init; }

    public string? PidStart { get; init; }

    public string PaneId { get; init; } = string.Empty;

    public DateTimeOffset DetectedAt { get; init; }

    public DateTimeOffset? IdleSince { get; init; }

    public int IdleTicks { get; init; }

    public int Attempt { get; init; }

    public string? LastError { get; init; }

    public string? AskedReason { get; init; }

    public DateTimeOffset? LaunchedAt { get; init; }

    public DateTimeOffset? NowSeen { get; init; }

    public bool BusyTold { get; init; }

    /// <summary>The process SIGTERM is about to be, or was, sent to; written before the signal. A record that names a process never signals it again.</summary>
    public int TermPid { get; init; }

    public string? TermPidStart { get; init; }

    /// <summary>When the relaunch was begun; written before it starts, so a daemon that died in the middle of it does not launch a second time while the pane is still coming up.</summary>
    public DateTimeOffset? LaunchStartedAt { get; init; }

    public JsonObject ToJson()
    {
        var o = new JsonObject
        {
            ["state"] = State,
            ["session_id"] = SessionId is { } s ? s : JsonValue.Null,
            ["pid"] = Pid,
            ["pid_start"] = PidStart is { } ps ? ps : JsonValue.Null,
            ["pane_id"] = PaneId,
            ["detected_at"] = Text(DetectedAt),
            ["idle_since"] = IdleSince is { } i ? Text(i) : JsonValue.Null,
            ["idle_ticks"] = IdleTicks,
            ["attempt"] = Attempt,
            ["last_error"] = LastError is { } e ? e : JsonValue.Null,
            ["asked_reason"] = AskedReason is { } a ? a : JsonValue.Null,
            ["launched_at"] = LaunchedAt is { } l ? Text(l) : JsonValue.Null,
            ["now_seen"] = NowSeen is { } n ? Text(n) : JsonValue.Null,
            ["busy_told"] = JsonValue.FromBool(BusyTold),
            ["term_pid"] = TermPid,
            ["term_pid_start"] = TermPidStart is { } ts ? ts : JsonValue.Null,
            ["launch_started_at"] = LaunchStartedAt is { } ls ? Text(ls) : JsonValue.Null,
            ["reported"] = JsonValue.FromBool(Reported),
            ["was_held"] = JsonValue.FromBool(WasHeld),
            ["finished_at"] = FinishedAt is { } fin ? Text(fin) : JsonValue.Null,
            ["note"] = Note is { } note ? note : JsonValue.Null,
            ["pane_note"] = PaneNote is { } pn ? pn : JsonValue.Null,
        };
        return o;
    }

    public static RecoverRecord? From(JsonValue? value)
    {
        if (value is not { IsMap: true } map || map.Member("state") is not { IsText: true } state)
        {
            return null;
        }

        return new RecoverRecord
        {
            State = state.AsString(),
            SessionId = Str(map, "session_id"),
            Pid = map.Member("pid") is { Kind: JsonKind.Number } p ? (int)p.AsNumber() : 0,
            PidStart = Str(map, "pid_start"),
            PaneId = Str(map, "pane_id") ?? string.Empty,
            DetectedAt = Time(map, "detected_at") ?? DateTimeOffset.MinValue,
            IdleSince = Time(map, "idle_since"),
            IdleTicks = map.Member("idle_ticks") is { Kind: JsonKind.Number } t ? (int)t.AsNumber() : 0,
            Attempt = map.Member("attempt") is { Kind: JsonKind.Number } a ? (int)a.AsNumber() : 0,
            LastError = Str(map, "last_error"),
            AskedReason = Str(map, "asked_reason"),
            LaunchedAt = Time(map, "launched_at"),
            NowSeen = Time(map, "now_seen"),
            BusyTold = map.Member("busy_told") is { IsTruthy: true },
            TermPid = map.Member("term_pid") is { Kind: JsonKind.Number } tp ? (int)tp.AsNumber() : 0,
            TermPidStart = Str(map, "term_pid_start"),
            LaunchStartedAt = Time(map, "launch_started_at"),
            Reported = map.Member("reported") is { } told ? told.IsTruthy : IsTerminal(state.AsString()),
            Note = Str(map, "note"),
            PaneNote = Str(map, "pane_note"),
            WasHeld = map.Member("was_held") is { IsTruthy: true },
            FinishedAt = Time(map, "finished_at"),
        };
    }

    private static string Text(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    private static string? Str(JsonValue map, string key) => map.Member(key) is { IsText: true } v ? v.AsString() : null;

    private static DateTimeOffset? Time(JsonValue map, string key) =>
        Str(map, key) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
}

/// <summary>What the owner asked the daemon for (hold, release, now); the daemon writes it only for a request whose sender it has proven.</summary>
internal sealed record RecoverControl(bool Hold, DateTimeOffset? NowAt)
{
    public static RecoverControl From(JsonValue? value)
    {
        if (value is not { IsMap: true } map)
        {
            return new RecoverControl(false, null);
        }

        return new RecoverControl(
            map.Member("hold") is { IsTruthy: true },
            map.Member("now_at") is { IsText: true } at && DateTimeOffset.TryParse(at.AsString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null);
    }
}

internal sealed partial class BareWatch
{
    /// <summary>A bare member that has been busy this long gets one event.</summary>
    internal static readonly TimeSpan BusyTell = TimeSpan.FromMinutes(30);

    /// <summary>How long the stopped process has to exit after SIGTERM.</summary>
    internal static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(15);

    /// <summary>How long a relaunched member has to show an ah-launched process on its row.</summary>
    internal static readonly TimeSpan VerifyWithin = TimeSpan.FromSeconds(60);

    /// <summary>How long a member must have been idle, and for how many sweeps, before it may be stopped.</summary>
    internal static readonly TimeSpan IdleStable = TimeSpan.FromSeconds(60);

    /// <summary>How long after a relaunch began a pane with no Claude in it is still taken to be coming up.</summary>
    internal static readonly TimeSpan LaunchPatience = TimeSpan.FromSeconds(90);

    /// <summary>The least time between the starts of two relaunches anywhere in the daemon, so a restore wave does not start every member at once.</summary>
    internal static readonly TimeSpan LaunchGap = TimeSpan.FromSeconds(30);

    /// <summary>How long a finished recovery waits for the rest of its team before the digest is sent without them.</summary>
    internal static readonly TimeSpan DigestWait = TimeSpan.FromMinutes(10);

    internal const string QueuedReason = "queued: one relaunch at a time";

    internal const int IdleTicksNeeded = 2;

    internal const int MaxAttempts = 2;

    /// <summary>
    /// One member's recovery for this sweep. A member is stopped only when every rung holds, and the rungs that can change within a
    /// sweep are read again immediately before the signal. Only SIGTERM is ever sent.
    /// </summary>
    private void Recover(StallTeam team, BareRow row, BareVerdict verdict)
    {
        if (_inFlight.TryGetValue(InFlightKey(team, row), out var running) && !running.IsCompleted)
        {
            return;
        }

        var record = RecoverRecord.From(host.ReadRecord(team, row.Name));
        var at = now();
        if (record is not null
            && RecoverRecord.IsPending(record.State)
            && record.SessionId is { Length: > 0 } recorded
            && host.LastSessionId(team, row) is { Length: > 0 } last
            && last != recorded)
        {
            Save(team, row, record, record with { State = RecoverRecord.Cleared }, $"superseded: the member now runs session {last}");
            return;
        }

        if (record is { State: RecoverRecord.Stopping or RecoverRecord.Launching })
        {
            Continue(team, row, verdict, record, at);
            return;
        }

        if (verdict.Kind != BareKind.Bare)
        {
            if (record is { State: RecoverRecord.Detected or RecoverRecord.WaitingIdle or RecoverRecord.Asked or RecoverRecord.Held })
            {
                Save(team, row, record, record with { State = RecoverRecord.Cleared }, $"the bare process is gone: {verdict.Why}");
            }

            return;
        }

        if (IsOrchestrator(team, row) || verdict.Pid is not { } pid || verdict.SessionId is not { } session)
        {
            return;
        }

        var fresh = record is null || record.Pid != pid || record.PidStart != verdict.PidStart || record.State == RecoverRecord.Done;
        var revived = !fresh && record!.State == RecoverRecord.Cleared;
        var current = fresh
            ? new RecoverRecord { State = RecoverRecord.Detected, SessionId = session, Pid = pid, PidStart = verdict.PidStart, PaneId = row.Member.PaneId, DetectedAt = at }
            : revived ? record! with { State = record.LastError == TermStuck ? RecoverRecord.Asked : RecoverRecord.Detected, IdleSince = null, IdleTicks = 0, SessionId = session } : record!;
        if (fresh || revived)
        {
            Save(team, row, record, current, "a matched bare process was found");
        }

        if (host.RecoveryEnabled && current.State != RecoverRecord.Failed)
        {
            if (host.TeamHeld(team))
            {
                if (current.State != RecoverRecord.Held)
                {
                    Save(team, row, current, current with { State = RecoverRecord.Held }, "team held; waits for resume --hold");
                }

                return;
            }

            if (current.State == RecoverRecord.Held)
            {
                var lifted = current with { State = RecoverRecord.Detected, IdleSince = null, IdleTicks = 0, WasHeld = true };
                Save(team, row, current, lifted, "hold lifted");
                current = lifted;
            }
        }

        if (current.State == RecoverRecord.Failed || !host.RecoveryEnabled)
        {
            return;
        }

        var control = RecoverControl.From(host.ReadControl(team, row.Name));
        var wantNow = control.NowAt is { } requested && (current.NowSeen is null || requested > current.NowSeen);
        if (control.Hold && !wantNow)
        {
            return;
        }

        var status = host.AgentStatus(team, row);
        var idle = IsIdleStatus(status);
        var next = current with
        {
            IdleSince = idle ? current.IdleSince ?? at : null,
            IdleTicks = idle ? current.IdleTicks + 1 : 0,
            Note = null,
        };
        var blocked = Blocked(team, row, next, control, wantNow, status, at, out var asking);
        if (blocked is not null)
        {
            var state = asking ? RecoverRecord.Asked : RecoverRecord.WaitingIdle;
            Save(team, row, current, next with { State = state, AskedReason = asking ? blocked : null }, blocked, askEvent: asking && current.AskedReason != blocked);
            TellBusy(team, row, next with { State = state }, status, at);
            return;
        }

        if (QueuedBehindOther(team, row, at))
        {
            Save(team, row, current, next with { State = RecoverRecord.WaitingIdle, AskedReason = null, Note = QueuedReason }, QueuedReason);
            return;
        }

        var again = host.Check(team, row, new BareSighting(pid, verdict.PidStart));
        var statusAgain = host.AgentStatus(team, row);
        if (again.Kind != BareKind.Bare || again.Pid != pid || again.PidStart != verdict.PidStart || !IsIdleStatus(statusAgain) || BlockingWork(team, row, next).Count > 0)
        {
            Save(team, row, current, next with { State = RecoverRecord.WaitingIdle, IdleSince = null, IdleTicks = 0 }, "the member changed between the check and the signal; nothing was sent");
            return;
        }

        if (host.PreflightRelaunch(team, row, session) is { } refused)
        {
            if (refused.StartsWith(Ah.Roster.RosterModule.HoldRefusalCode, StringComparison.Ordinal))
            {
                Save(team, row, current, next with { State = RecoverRecord.WaitingIdle, IdleSince = null, IdleTicks = 0 }, "held before the signal; nothing was sent");
                return;
            }

            // Nothing was stopped, so this is not a dead member and the brake stays off; the stored error keeps later sweeps from asking again.
            var why = $"relaunch would be refused: {refused}; left running";
            Save(team, row, current, next with { State = RecoverRecord.Asked, AskedReason = why, LastError = why }, why, askEvent: true);
            return;
        }

        var stopping = next with
        {
            State = RecoverRecord.Stopping,
            AskedReason = null,
            Attempt = next.Attempt + 1,
            NowSeen = wantNow ? control.NowAt : next.NowSeen,
            TermPid = pid,
            TermPidStart = verdict.PidStart,
        };
        Save(
            team,
            row,
            current,
            stopping,
            wantNow ? "stopping on request: matched, idle, no open work" : "stopping: matched, idle for 2 sweeps and 60 s, no open work, not held, attempts left");
        Run(team, row, () =>
        {
            // A hold that landed after the sweep read the control, but before the signal, still stops the signal.
            var latest = RecoverControl.From(host.ReadControl(team, row.Name));
            var latestNow = latest.NowAt is { } asked && (stopping.NowSeen is null || asked > stopping.NowSeen);
            if ((latest.Hold && !wantNow && !latestNow) || host.TeamHeld(team))
            {
                Save(team, row, stopping, next with { State = RecoverRecord.WaitingIdle, IdleSince = null, IdleTicks = 0 }, "held before the signal; nothing was sent");
                return;
            }

            if (!host.Terminate(pid, verdict.PidStart))
            {
                Save(team, row, stopping, stopping with { State = RecoverRecord.Asked, AskedReason = "the process is not the one recorded, or SIGTERM could not be sent", LastError = "term not sent", TermPid = 0, TermPidStart = null }, "term not sent", askEvent: true);
                return;
            }

            if (!host.WaitExit(pid, verdict.PidStart, ExitWait))
            {
                Save(team, row, stopping, stopping with { State = RecoverRecord.Asked, AskedReason = TermStuck, LastError = TermStuck }, TermStuck, askEvent: true);
                return;
            }

            Launch(team, row, stopping with { State = RecoverRecord.Launching, LaunchedAt = null });
        });
    }

    /// <summary>What a record holds when SIGTERM was sent and the process stayed. A pid is signalled once; nothing, not even <c>recover now</c>, signals it again.</summary>
    internal const string TermStuck = "did not exit on TERM";

    private static string InFlightKey(StallTeam team, BareRow row) => team.HierarchyDir + "|" + team.Name + "/" + row.Name;

    /// <summary>Starts the slow part of a recovery (the wait for the exit, the launch) apart from the sweep; the sweep then only asks whether it is still running.</summary>
    private void Run(StallTeam team, BareRow row, Action work)
    {
        if (Interlocked.Exchange(ref _counted, 1) == 0)
        {
            Ah.Core.CoreModule.CensusAdd(this, "watch.bare_in_flight", static w => w._inFlight.Count);
        }

        _inFlight[InFlightKey(team, row)] = (run ?? (static a => Task.Run(a)))(() =>
        {
            try
            {
                work();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Crashed(team, row, e);
            }
        });
    }

    private void Crashed(StallTeam team, BareRow row, Exception error)
    {
        try
        {
            var record = RecoverRecord.From(host.ReadRecord(team, row.Name));
            if (record is not null)
            {
                Save(team, row, record, record with { State = RecoverRecord.Asked, AskedReason = "the recovery stopped on an error: " + error.Message, LastError = "recovery error" }, "the recovery stopped on an error: " + error.Message, askEvent: true);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // the record cannot be written either; the next sweep reads whatever state was last saved
        }
    }

    // Any orchestrate-class role (a lane lead too) is never stopped by this sweep, so the role test stays a substring match here.
    private static bool IsOrchestrator(StallTeam team, BareRow row) =>
        row.Role?.Contains("orchestrator", StringComparison.Ordinal) == true || WatchModule.IsBareOrchestrator(row.Role, row.Name, team.OrchestratorAddress);

    /// <summary>The first rung that does not hold, or null when the member may be stopped; <paramref name="asking"/> says the Orchestrator is to be asked.</summary>
    private string? Blocked(StallTeam team, BareRow row, RecoverRecord record, RecoverControl control, bool wantNow, string? status, DateTimeOffset at, out bool asking)
    {
        asking = false;
        if (record.TermPid != 0 && record.TermPid == record.Pid && record.TermPidStart == record.PidStart)
        {
            asking = true;
            return TermStuck;
        }

        if (!wantNow && control.Hold)
        {
            return "held by the Orchestrator";
        }

        if (record.Attempt >= MaxAttempts)
        {
            return "attempts used up";
        }

        if (record.State == RecoverRecord.Asked && record.LastError is { } stuck && (stuck == TermStuck || !wantNow))
        {
            asking = true;
            return stuck;
        }

        if (!IsIdleStatus(status))
        {
            return $"not idle (herdr says {status ?? "unknown"})";
        }

        if (!wantNow && !(record.IdleTicks >= IdleTicksNeeded && record.IdleSince is { } since && at - since >= IdleStable))
        {
            return "idle, but not yet stable for 2 sweeps and 60 s";
        }

        var work = BlockingWork(team, row, record);
        if (work.Count > 0)
        {
            asking = true;
            return "open work: " + string.Join("; ", work);
        }

        return null;
    }

    /// <summary>
    /// What keeps the member from being stopped. Its team's quit asked it to stop and the reboot ended its process, so for a member that was
    /// held an open request is not work in flight, and only open background work counts.
    /// </summary>
    private IReadOnlyList<string> BlockingWork(StallTeam team, BareRow row, RecoverRecord record) =>
        !record.WasHeld ? host.OpenWork(team, row)
            : host.BackgroundOpen(team, record.SessionId) is > 0 and var open ? [$"{open} background task(s) open"] : [];

    /// <summary>herdr reports a member at an empty prompt as <c>idle</c> or, once it has finished a turn, <c>done</c>; both are ready for input, while <c>working</c> and <c>blocked</c> are not.</summary>
    private static bool IsIdleStatus(string? status) => status is "idle" or "done";

    private void TellBusy(StallTeam team, BareRow row, RecoverRecord record, string? status, DateTimeOffset at)
    {
        if (IsIdleStatus(status) || record.BusyTold || at - record.DetectedAt < BusyTell || team.OrchestratorAddress is not { } to)
        {
            return;
        }

        host.WriteRecord(team, row.Name, (record with { BusyTold = true }).ToJson());
        host.Mail(new WatchMail(
            to,
            team.HierarchyDir,
            team.Cwd,
            "member-bare-busy",
            $"{row.Name} has run without ah's launch and stayed busy for over {(int)BusyTell.TotalMinutes} min, so it has not been relaunched. It is never interrupted; it will be relaunched when idle. Hold it with: ah roster recover hold {row.Name} --team {team.Name}",
            new JsonObject { ["member"] = row.Name },
            team.OrchestratorSession));
    }

    /// <summary>Carries on a recovery a previous sweep left in <c>stopping</c> or <c>launching</c>; nothing is signalled or launched twice.</summary>
    private void Continue(StallTeam team, BareRow row, BareVerdict verdict, RecoverRecord record, DateTimeOffset at)
    {
        if (record.State == RecoverRecord.Stopping)
        {
            if (verdict.Kind == BareKind.Bare && verdict.Pid == record.Pid && verdict.PidStart == record.PidStart)
            {
                Save(team, row, record, record with { State = RecoverRecord.Asked, AskedReason = TermStuck, LastError = TermStuck }, "still running after an earlier TERM; not signalled again", askEvent: true);
                return;
            }

            switch (verdict.Kind)
            {
                case BareKind.NoClaude:
                    Run(team, row, () => Launch(team, row, record with { State = RecoverRecord.Launching, LaunchedAt = null }));
                    break;
                case BareKind.Launched:
                    var launched = record with { State = RecoverRecord.Launching, LaunchedAt = record.LaunchedAt ?? at };
                    Save(team, row, record, launched, "the pane already holds an ah-launched process");
                    Verify(team, row, launched, at);
                    break;
                default:
                    Save(team, row, record, record with { State = RecoverRecord.Cleared }, "another process is in the pane; the recovery starts over: " + verdict.Why);
                    break;
            }

            return;
        }

        if (record.LaunchedAt is null)
        {
            if (verdict.Kind == BareKind.NoClaude)
            {
                if (record.LaunchStartedAt is { } begun && at - begun < LaunchPatience)
                {
                    return;
                }

                if (record.LaunchStartedAt is not null)
                {
                    if (record.Attempt >= MaxAttempts)
                    {
                        Fail(team, row, record with { LastError = "the launch left no process in the pane" }, "the launch left no process in the pane", retry: false);
                        return;
                    }

                    record = record with { Attempt = record.Attempt + 1 };
                }

                Run(team, row, () => Launch(team, row, record));
            }
            else if (verdict.Kind == BareKind.Launched)
            {
                var started = record with { LaunchedAt = at };
                Save(team, row, record, started, "the pane already holds an ah-launched process");
                Verify(team, row, started, at);
            }
            else
            {
                FailStuckLaunch(team, row, record, verdict, at);
            }

            return;
        }

        if (verdict.Kind == BareKind.Launched && host.Verified(team, row, record.Pid, record.SessionId ?? string.Empty))
        {
            Verify(team, row, record, at);
            return;
        }

        if (at - record.LaunchedAt < VerifyWithin)
        {
            return;
        }

        Fail(team, row, record with { LastError = "no ah-launched process was recorded within 60 s" }, "no ah-launched process was recorded within 60 s", verdict.Kind == BareKind.NoClaude);
    }

    /// <summary>A stopping or launching record whose pane cannot be read stays where it is for this sweep; past the patience it fails, so a member is never left there with no error.</summary>
    private void PendingOnUnreadablePane(StallTeam team, BareRow row, BareVerdict verdict)
    {
        var key = InFlightKey(team, row);
        if (_inFlight.TryGetValue(key, out var running) && !running.IsCompleted)
        {
            return;
        }

        if (RecoverRecord.From(host.ReadRecord(team, row.Name)) is { State: RecoverRecord.Stopping or RecoverRecord.Launching } record)
        {
            var at = now();
            _unreadableSince.TryAdd(key, at);
            if (at - _unreadableSince[key] >= LaunchPatience)
            {
                _unreadableSince.Remove(key);
                var why = (record.State == RecoverRecord.Stopping ? "the stop" : "the launch") + " could not be confirmed: " + verdict.Why;
                Fail(team, row, record with { LastError = why }, why, retry: false);
            }
        }
    }

    private void FailStuckLaunch(StallTeam team, BareRow row, RecoverRecord record, BareVerdict verdict, DateTimeOffset at)
    {
        if (at - (record.LaunchedAt ?? record.LaunchStartedAt ?? record.DetectedAt) >= LaunchPatience)
        {
            var why = "the launch could not be confirmed: " + verdict.Why;
            Fail(team, row, record with { LastError = why }, why, retry: false);
        }
    }

    private void Launch(StallTeam team, BareRow row, RecoverRecord record)
    {
        if (!host.Owner())
        {
            return;
        }

        if (!host.RecoveryEnabled)
        {
            var off = $"recovery is switched off; relaunch by hand: {ByHandCommand(team, row)}";
            var pending = record with { State = RecoverRecord.Launching };
            Save(team, row, pending, pending with { State = RecoverRecord.Asked, AskedReason = off, LastError = off }, off, askEvent: true);
            return;
        }

        var marked = record with { State = RecoverRecord.Launching, LaunchedAt = null, LaunchStartedAt = now() };
        host.WriteRecord(team, row.Name, marked.ToJson());
        var error = host.Relaunch(team, row, record.SessionId ?? string.Empty);
        if (error is not null && error.StartsWith(Ah.Roster.RosterModule.HoldRefusalCode, StringComparison.Ordinal))
        {
            // The hold landed while this launch was in flight: the record goes back as it was, with no attempt, error or event.
            host.WriteRecord(team, row.Name, record.ToJson());
            return;
        }

        if (error is not null && (error.StartsWith(RecoveryState.PaneUnknownPrefix, StringComparison.Ordinal) || error.StartsWith(RecoveryState.RefusalPrefix, StringComparison.Ordinal)))
        {
            var asked = marked with { State = RecoverRecord.Asked, AskedReason = error, LastError = error };
            Save(team, row, marked, asked, error);
            TellRelaunchFailed(team, row, asked);
            return;
        }

        if (error is not null)
        {
            Fail(team, row, marked with { LastError = error }, "the launch failed: " + error);
            return;
        }

        var after = host.PaneOf(team, row.Name);
        var paneNote = record.PaneId.Length > 0 && after is not null && after != record.PaneId
            ? $"restored pane {record.PaneId} was gone; launched in a new pane {after}"
            : null;
        host.WriteRecord(team, row.Name, (marked with { LaunchedAt = now(), PaneNote = paneNote }).ToJson());
        Journal(team, row, record.State, RecoverRecord.Launching, record, "relaunched with --resume " + record.SessionId);
    }

    private void Verify(StallTeam team, BareRow row, RecoverRecord record, DateTimeOffset at)
    {
        if (!host.Verified(team, row, record.Pid, record.SessionId ?? string.Empty))
        {
            if (record.LaunchedAt is { } launched && at - launched >= VerifyWithin)
            {
                Fail(team, row, record with { LastError = "no ah-launched process was recorded within 60 s" }, "no ah-launched process was recorded within 60 s", retry: false);
            }

            return;
        }

        Save(team, row, record, record with { State = RecoverRecord.Done, AskedReason = null }, "the relaunched member recorded its process");
        if (record.WasHeld && host.ReBrief(team, row) is { } unsent && team.OrchestratorAddress is { } told)
        {
            host.Mail(new WatchMail(
                told,
                team.HierarchyDir,
                team.Cwd,
                "member-relaunch-failed",
                $"{row.Name} was relaunched and is up, but its resume brief was not sent: {unsent}. Send it by hand: ah roster resume --checkpoint --team {team.Name} --yes",
                new JsonObject { ["member"] = row.Name, ["error"] = unsent },
                team.OrchestratorSession));
        }

        if (!host.RecoveryEnabled && team.OrchestratorAddress is { } to)
        {
            host.Mail(new WatchMail(
                to,
                team.HierarchyDir,
                team.Cwd,
                "member-relaunched",
                $"{row.Name} was running without ah's launch and has been relaunched in its pane with --resume {record.SessionId}; its context is kept.",
                new JsonObject { ["member"] = row.Name },
                team.OrchestratorSession));
        }
    }

    private void Fail(StallTeam team, BareRow row, RecoverRecord record, string reason, bool retry = true)
    {
        if (retry && record.Attempt < MaxAttempts)
        {
            Save(team, row, record, record with { State = RecoverRecord.Launching, LaunchedAt = null, LaunchStartedAt = null, Attempt = record.Attempt + 1 }, reason + "; one more attempt");
            return;
        }

        Save(team, row, record, record with { State = RecoverRecord.Failed }, reason);
        TellRelaunchFailed(team, row, record);
    }

    private static string ByHandCommand(StallTeam team, BareRow row) =>
        $"ah roster resume --checkpoint --team {team.Name} --relaunch {row.Name} --yes";

    /// <summary>The one mail for a member left dead; it fires only once recovery is off (the daemon's own brake sees to that), so the digest does not repeat it.</summary>
    private void TellRelaunchFailed(StallTeam team, BareRow row, RecoverRecord record)
    {
        if (!host.RecoveryEnabled && team.OrchestratorAddress is { } to)
        {
            host.Mail(new WatchMail(
                to,
                team.HierarchyDir,
                team.Cwd,
                "member-relaunch-failed",
                $"{row.Name} could not be relaunched after {record.Attempt} attempts ({record.LastError}). It is not tried again. Do it by hand, from the project root: {ByHandCommand(team, row)}. The daemon switched recovery off; only the user can turn it back on (ah roster recover enable).",
                new JsonObject { ["member"] = row.Name, ["error"] = record.LastError is { } e ? e : JsonValue.Null },
                team.OrchestratorSession));
        }
    }

    private void Save(StallTeam team, BareRow row, RecoverRecord? from, RecoverRecord to, string reason, bool askEvent = false)
    {
        if (RecoverRecord.IsTerminal(to.State) && from?.State != to.State)
        {
            to = to with { Reported = false, FinishedAt = now() };
        }

        host.WriteRecord(team, row.Name, to.ToJson());
        if (from?.State != to.State || from?.AskedReason != to.AskedReason || from?.Note != to.Note)
        {
            Journal(team, row, from?.State ?? "none", to.State, to, reason);
        }

        if (from?.State == RecoverRecord.Launching && to.State is RecoverRecord.Asked or RecoverRecord.Failed && host.RecoveryEnabled)
        {
            Brake(team, row.Name, to.LastError ?? reason);
        }

        if (askEvent && team.OrchestratorAddress is { } address)
        {
            host.Mail(new WatchMail(
                address,
                team.HierarchyDir,
                team.Cwd,
                "member-bare-asked",
                $"{row.Name} runs without ah's launch and was not relaunched: {reason}. {WatchModule.BareSentence(host.RecoveryEnabled, team.Name, row.Name)}",
                new JsonObject { ["member"] = row.Name, ["reason"] = reason },
                team.OrchestratorSession));
        }
    }

    /// <summary>The brakes whose switch write failed; each is tried again at the start of the next sweep while recovery is still on.</summary>
    private readonly List<(StallTeam Team, string Member, string Reason)> _brakesOwed = [];

    private void Brake(StallTeam team, string member, string reason)
    {
        try
        {
            host.DisableRecovery(team, member, reason);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _brakesOwed.Add((team, member, reason));
            host.Journal(new JsonObject
            {
                ["at"] = now().ToString("O", CultureInfo.InvariantCulture),
                ["kind"] = "bare-recover-brake-failed",
                ["team"] = team.Name,
                ["member"] = member,
                ["error"] = e.Message,
            });
        }
    }

    private void RetryOwedBrakes()
    {
        var owed = _brakesOwed.ToArray();
        _brakesOwed.Clear();
        foreach (var (team, member, reason) in owed)
        {
            if (host.RecoveryEnabled)
            {
                Brake(team, member, reason);
            }
        }
    }

    private Dictionary<string, IReadOnlyList<BareRow>> _sweepRows = new(StringComparer.Ordinal);

    private IReadOnlyList<StallTeam> _sweepTeams = [];

    private IReadOnlyList<BareRow> RowsOf(StallTeam team)
    {
        var key = team.HierarchyDir + "|" + team.Name;
        if (!_sweepRows.TryGetValue(key, out var rows))
        {
            rows = host.Rows(team);
            _sweepRows[key] = rows;
        }

        return rows;
    }

    /// <summary>
    /// Whether another member anywhere is between the start of its stop and its end, or the last relaunch began less than
    /// <see cref="LaunchGap"/> ago. The records on disk decide, so a daemon that restarted mid-wave keeps the pace.
    /// </summary>
    private bool QueuedBehindOther(StallTeam team, BareRow row, DateTimeOffset at)
    {
        var self = InFlightKey(team, row);
        if (_inFlight.Any(f => f.Key != self && !f.Value.IsCompleted))
        {
            return true;
        }

        foreach (var other in _sweepTeams)
        {
            foreach (var r in other == team ? RowsOf(team) : host.Rows(other))
            {
                if (RecoverRecord.From(host.ReadRecord(other, r.Name)) is not { } record)
                {
                    continue;
                }

                var isSelf = InFlightKey(other, r) == self;
                if (!isSelf && record.State is RecoverRecord.Stopping or RecoverRecord.Launching)
                {
                    return true;
                }

                if (record.LaunchStartedAt is { } begun && at - begun < LaunchGap)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// One <c>team-readopt</c> mail for the team once its recoveries have ended (or the oldest finished one has waited
    /// <see cref="DigestWait"/>), naming each member once. Only while recovery is on; off, the per-member mails are the report.
    /// </summary>
    private void Digest(StallTeam team, DateTimeOffset at)
    {
        if (!host.RecoveryEnabled || team.OrchestratorAddress is not { } to || host.TeamHeld(team))
        {
            return;
        }

        var records = new List<(BareRow Row, RecoverRecord Record)>();
        foreach (var r in RowsOf(team).Where(r => !IsOrchestrator(team, r)))
        {
            if (RecoverRecord.From(host.ReadRecord(team, r.Name)) is not { } record)
            {
                continue;
            }

            // A finished record of an earlier session says nothing about the member now running; it stays on disk and is never reported.
            if (RecoverRecord.IsTerminal(record.State) && !record.Reported
                && record.SessionId is { Length: > 0 } recorded && host.LastSessionId(team, r) is { Length: > 0 } current && current != recorded)
            {
                host.WriteRecord(team, r.Name, (record with { Reported = true, Note = $"superseded by session {current}" }).ToJson());
                continue;
            }

            records.Add((r, record));
        }

        var unreported = records.Where(x => RecoverRecord.IsTerminal(x.Record.State) && !x.Record.Reported).ToList();
        if (unreported.Count == 0)
        {
            return;
        }

        var pending = records.Count(x => RecoverRecord.IsPending(x.Record.State));
        if (pending > 0 && at - unreported.Min(x => x.Record.FinishedAt ?? x.Record.DetectedAt) < DigestWait)
        {
            return;
        }

        var all = unreported;
        string Session(RecoverRecord r) => r.SessionId is { Length: > 4 } s ? s[..4] + "…" : r.SessionId ?? "?";
        string Names(string state, bool withSession) =>
            string.Join(", ", all.Where(x => x.Record.State == state).Select(x => (withSession ? $"{x.Row.Name} --resume {Session(x.Record)}" : x.Row.Name) + (x.Record.WasHeld ? " (restored during the hold)" : string.Empty)));
        var counts = new[] { RecoverRecord.Done, RecoverRecord.Failed, RecoverRecord.Asked }.ToDictionary(s => s, s => all.Count(x => x.Record.State == s));
        var text = new System.Text.StringBuilder()
            .Append($"ahd: {team.Name}: bare members found ({string.Join(", ", all.Select(x => $"{x.Row.Name} at {x.Record.DetectedAt.UtcDateTime:HH:mm}Z"))}) — re-adopted {counts[RecoverRecord.Done]}")
            .Append(counts[RecoverRecord.Done] > 0 ? $" ({Names(RecoverRecord.Done, true)})" : string.Empty)
            .Append(string.Concat(all.Where(x => x.Record.PaneNote is not null).Select(x => $". {x.Row.Name}: {x.Record.PaneNote}")))
            .Append($"; failed {counts[RecoverRecord.Failed]}")
            .Append($"; asked {counts[RecoverRecord.Asked]}")
            .Append(counts[RecoverRecord.Asked] > 0 ? $" ({Names(RecoverRecord.Asked, false)})" : string.Empty);
        if (pending > 0)
        {
            text.Append($"; still pending {pending}");
        }

        foreach (var failed in all.Where(x => x.Record.State == RecoverRecord.Failed))
        {
            text.Append($". {failed.Row.Name} could not be relaunched ({failed.Record.LastError}); by hand, from the project root: ah roster resume --checkpoint --team {team.Name} --relaunch {failed.Row.Name} --yes");
        }

        var members = new JsonArray();
        foreach (var (r, record) in all)
        {
            members.Add(new JsonObject { ["member"] = r.Name, ["state"] = record.State, ["session_id"] = record.SessionId is { } sid ? sid : JsonValue.Null });
        }

        host.Mail(new WatchMail(to, team.HierarchyDir, team.Cwd, "team-readopt", text.ToString(), new JsonObject { ["team"] = team.Name, ["members"] = members }, team.OrchestratorSession));
        foreach (var (r, record) in unreported)
        {
            host.WriteRecord(team, r.Name, (record with { Reported = true }).ToJson());
        }
    }

    private void Journal(StallTeam team, BareRow row, string from, string to, RecoverRecord record, string reason) =>
        host.Journal(new JsonObject
        {
            ["at"] = now().ToString("O", CultureInfo.InvariantCulture),
            ["team"] = team.Name,
            ["kind"] = "bare-recover",
            ["member"] = row.Name,
            ["from"] = from,
            ["to"] = to,
            ["pid"] = record.Pid,
            ["pid_start"] = record.PidStart is { } s ? s : JsonValue.Null,
            ["session_id"] = record.SessionId is { } sid ? sid : JsonValue.Null,
            ["reason"] = reason,
            ["attempt"] = record.Attempt,
        });
}
