using Ah.Core.Json;
using Ah.Core.Time;
using Ah.Hierarchy;

namespace Ah.Watch;

/// <summary>A team the stall watch walks: one with a live Orchestrator or an open run anchor.</summary>
/// <param name="Name">The team's name.</param>
/// <param name="HierarchyDir">The hierarchy dir the team file lives under.</param>
/// <param name="Cwd">The Orchestrator session's working directory.</param>
/// <param name="OrchestratorAddress">The Orchestrator's mailbox address, or null when none is registered.</param>
/// <param name="OrchestratorSession">The session the Orchestrator's mail is for.</param>
public sealed record StallTeam(string Name, string HierarchyDir, string Cwd, string? OrchestratorAddress, string OrchestratorSession)
{
    /// <summary>Listed only because its Orchestrator is down with work queued and no run open: the user alarm sees it, the member sweep and stuck escalation never do.</summary>
    public bool QueuedOnly { get; init; }
}

/// <summary>A lead's box holds <paramref name="Unread"/> unread entries and is marked uncarried since they arrived: the daemon's wake failed for <paramref name="Reason"/>.</summary>
public sealed record LeadUncarried(int Unread, string Reason);

/// <summary>A tracked member row. <paramref name="Exempt"/> marks one the sweeper keeps (<c>sweep_keep</c>).</summary>
public sealed record StallMember(string Name, string? Role, string? PaneId, bool Exempt);

/// <summary>A member's verdict, the session id of the registration it rests on, and when that registration last showed the member alive (null when unknown).</summary>
public sealed record StallJudgement(MemberVerdict Verdict, string RegistrationSession, DateTimeOffset? LastEvidence = null);

/// <summary>A user alarm episode as recorded: what it was told for, when it was first and last told, and whether its first line got through.</summary>
public sealed record UserAlarm(string Episode, DateTimeOffset FirstToldAt, DateTimeOffset LastToldAt, bool Delivered)
{
    /// <summary>The alarm's kind (<c>owner-down</c>, <c>owner-asking</c>, <c>owner-stalled</c>); null on a record written before it was kept.</summary>
    public string? Kind { get; init; }

    /// <summary>The line told to the user, served as the team's alert while the alarm is raised (ah2-180 Amendment 2); null on an older record.</summary>
    public string? Line { get; init; }
}

/// <summary>What the stall watch reads and writes. It never signals a process, closes a pane or edits a request.</summary>
public interface IStallHost
{
    /// <summary>Whether this daemon still owns the fence; an unfenced daemon acts on nothing.</summary>
    bool Owner();

    IReadOnlyList<StallTeam> Teams();

    /// <summary>The teams of <see cref="Teams"/> plus the <see cref="StallTeam.QueuedOnly"/> ones; only the stall sweep reads it, and only the user alarm acts on those.</summary>
    IReadOnlyList<StallTeam> AlarmTeams() => Teams();

    IReadOnlyList<StallMember> Members(StallTeam team);

    StallJudgement Judge(StallTeam team, StallMember member);

    /// <summary>Whether the bare-member recovery owns the member (a matched bare mark, or a recovery under way or held), so its pre-restart registration being dead is not an alarm.</summary>
    bool RecoveryCovers(StallTeam team, StallMember member) => false;

    /// <summary>The open work of a member, one line each: raw-open requests addressed to it, and what else the host counts.</summary>
    IReadOnlyList<string> OpenWork(StallTeam team, StallMember member);

    /// <summary>Mails an event to the team's Orchestrator; throws when it cannot be sent.</summary>
    void Mail(WatchMail mail);

    void Journal(JsonObject row);

    /// <summary>The session whose death was last alarmed for <paramref name="key"/> (<c>team/member</c>), or null when none was; it outlives a daemon restart.</summary>
    string? ReportedSession(string key);

    /// <summary>
    /// Records the session whose death was alarmed for <paramref name="key"/> and that it was seen now, or forgets it when
    /// <paramref name="session"/> is null. Recording the session already held only refreshes when it was seen.
    /// </summary>
    void SetReportedSession(string key, string? session);

    /// <summary>The peer rows on record (<c>watch-event</c> rows among them); none when the host keeps no peer record.</summary>
    IReadOnlyList<JsonValue> PeerRows() => [];

    /// <summary>Records one peer row.</summary>
    void RecordPeerRow(JsonObject row)
    {
    }

    /// <summary>When the session last made a tool call, as its activity record under <paramref name="hierarchyDir"/> has it; null when unknown.</summary>
    DateTimeOffset? LastToolAt(string hierarchyDir, string session) => null;

    /// <summary>When the session began waiting on a question it asked in its terminal; null when it is not waiting on one.</summary>
    DateTimeOffset? AskingSince(string hierarchyDir, string session) => null;

    /// <summary>The team Orchestrator's unread mail the daemon could not wake it for (ah2-180 C1, the mark of B6); null when its box is read or not marked.</summary>
    LeadUncarried? Uncarried(StallTeam team) => null;

    /// <summary>
    /// Starts the user's <c>alarm.command</c> with <paramref name="line"/> without waiting for it, and calls <paramref name="exited"/> with its
    /// exit status once it ends, on any thread; false, and no call, when none is set. <paramref name="team"/> and <paramref name="kind"/>
    /// name the alarm in the row the host journals itself when <paramref name="exited"/> throws.
    /// </summary>
    bool StartAlarmCommand(string line, string team, string kind, Action<int> exited) => false;

    /// <summary>Where <see cref="StartAlarmCommand"/> sends the line: <c>alarm.command</c>, else <c>herdr</c> (local notifications) when it is unset.</summary>
    string AlarmChannel() => "alarm.command";

    /// <summary>
    /// The user alarm recorded for <paramref name="key"/>, or null when none is. A host that keeps only the episode reads it as delivered
    /// and never due again, so it tells once per episode.
    /// </summary>
    UserAlarm? Alarm(string key) => ReportedSession(key) is { } episode ? new UserAlarm(episode, DateTimeOffset.MaxValue, DateTimeOffset.MaxValue, true) : null;

    /// <summary>Records the user alarm for <paramref name="key"/>, or forgets it when <paramref name="alarm"/> is null.</summary>
    void SetAlarm(string key, UserAlarm? alarm) => SetReportedSession(key, alarm?.Episode);

    /// <summary>The keys of every user alarm recorded (<c>alarm/&lt;team&gt;/&lt;kind&gt;</c>), whatever team; none when the host keeps no record.</summary>
    IReadOnlyList<string> AlarmKeys() => [];

    /// <summary>Whether the user ordered a hold on the team named <paramref name="team"/> in any hierarchy dir the watch walks.</summary>
    bool TeamHeld(string team) => false;

    /// <summary>The unacked ahd events in the team's Orchestrator box, counted by event name; none when the host cannot read the box.</summary>
    IReadOnlyDictionary<string, int> WaitingEvents(StallTeam team) => new Dictionary<string, int>();

    /// <summary>The last lines of the member's pane as plain text; null when it has no pane or the pane cannot be read.</summary>
    string? PaneTail(StallTeam team, StallMember member) => null;

    /// <summary>Whether <paramref name="session"/> is a session id a resume line may name.</summary>
    bool IsSessionId(string session) => false;

    /// <summary>Whether <paramref name="role"/> has the shape of a role name a resume line may name; a team record's role is otherwise any text.</summary>
    bool IsRoleName(string role) => false;

    /// <summary>A sweep is starting: the host may read once what it needs to judge every member in it.</summary>
    void BeginSweep();

    /// <summary>Forgets every recorded alarm not recorded or refreshed within <paramref name="unseenFor"/>, and every one that records no time.</summary>
    void ForgetStaleReported(TimeSpan unseenFor);
}

/// <summary>
/// The daemon job that notices a tracked member's session ending. A member that reads Dead on every sweep for
/// <see cref="StallSettings.Down"/> raises one <c>member-down</c> event to the Orchestrator; when it reads Live again, one
/// <c>member-recovered</c>. A member that reads Unknown breaks the run of Dead sweeps and raises nothing.
/// </summary>
internal sealed class StallWatch
{
    public const string JobName = "stall-watch";

    /// <summary>How long a recorded alarm is kept for a member the sweeps no longer list; a short listing must not wipe live entries.</summary>
    public static readonly TimeSpan ReportedKeep = TimeSpan.FromDays(7);

    /// <summary>The head of a user alarm's record key, <c>alarm/&lt;team&gt;/&lt;kind&gt;</c>.</summary>
    internal const string AlarmKeyPrefix = "alarm/";

    private readonly Action<string>? _stamp;
    private readonly IStallHost _host;
    private readonly StallSettings _settings;
    private readonly Func<DateTimeOffset> _now;
    private readonly DateTimeOffset _started;
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _ownerDownSince = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _alarmRunning = new(StringComparer.Ordinal);

    public StallWatch(IStallHost host, StallSettings settings, Func<DateTimeOffset> now, Action<string>? stamp = null)
    {
        _stamp = stamp;
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _now = now ?? throw new ArgumentNullException(nameof(now));
        _started = now();
    }

    public void Register(Ah.Abstractions.IJobRegistry jobs)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        jobs.Add(JobName, _settings.Period, _ =>
        {
            Sweep();
            _stamp?.Invoke(JobName);
            return Task.CompletedTask;
        });
    }

    public void Sweep()
    {
        if (_settings.Mode == StallMode.Off || !_host.Owner())
        {
            return;
        }

        var now = _now();
        var warm = now - _started < _settings.WarmUp;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        _host.BeginSweep();
        var listed = _host.AlarmTeams();
        var teams = listed.Where(static t => !t.QueuedOnly).ToList();
        foreach (var team in teams)
        {
            foreach (var member in _host.Members(team))
            {
                if (member.Exempt)
                {
                    continue;
                }

                var key = $"{team.Name}/{member.Name}";
                seen.Add(key);
                var told = $"{key}@{team.OrchestratorSession}";
                var judged = _host.Judge(team, member);
                var track = _tracks.TryGetValue(key, out var held) ? held : _tracks[key] = new Track { Reported = _host.ReportedSession(told) };
                switch (judged.Verdict.Status)
                {
                    case Verdict.Live:
                        track.DeadSince = null;
                        if (track.Reported is not null)
                        {
                            track.Reported = null;
                            _host.SetReportedSession(told, null);
                            Emit(team, member, "member-recovered", $"ahd alarm: member-recovered {member.Name} — live again ({judged.Verdict.Why})",
                                "Nothing to do unless you re-briefed or re-spawned it meanwhile.", [], false, now, judged.Verdict.Why);
                        }

                        break;
                    case Verdict.Unknown:
                        track.DeadSince = null;
                        break;
                    default:
                        if (_host.RecoveryCovers(team, member))
                        {
                            track.DeadSince = null;
                            break;
                        }

                        track.DeadSince ??= now;
                        if (!warm && team.OrchestratorSession.Length > 0 && now - track.DeadSince.Value >= _settings.Down && track.Reported != judged.RegistrationSession)
                        {
                            var work = _host.OpenWork(team, member);
                            var deadSince = judged.LastEvidence is { } evidence && evidence < track.DeadSince.Value ? evidence : track.DeadSince.Value;
                            var since = Timestamps.ToIsoString(deadSince);
                            var cause = PaneCrash.Cause(PaneTail(team, member));
                            // no session to resume: relaunch it fresh under its name, so the alarm never ends on a placeholder it cannot fill
                            var resume = $"ah roster spawn-one {(member.Role is { } role && _host.IsRoleName(role) ? role : "<role>")} {(_host.IsSessionId(judged.RegistrationSession) ? $"--resume {judged.RegistrationSession}" : $"--member {member.Name}")} --team {team.Name}";
                            if (Emit(
                                team, member, "member-down",
                                $"ahd alarm: member-down {member.Name} — {(cause is null ? string.Empty : $"its pane shows {cause}; ")}{judged.Verdict.Why}; dead since {since}",
                                $"{(_host.IsSessionId(judged.RegistrationSession) ? "Resume" : "Relaunch")} it: `{resume}`\nCheck the member (`ah roster status`). Re-brief, re-spawn or close its requests. If the run is unattended, PushNotification the user.",
                                work, work.Count > 0, deadSince, cause is null ? judged.Verdict.Why : $"{judged.Verdict.Why}; pane shows {cause}", cause, resume))
                            {
                                track.Reported = judged.RegistrationSession;
                                _host.SetReportedSession(told, track.Reported);
                            }
                        }

                        break;
                }

                if (track.Reported is not null)
                {
                    _host.SetReportedSession(told, track.Reported);
                }
            }
        }

        EscalateStuck(teams, now);
        AlarmUser(listed, now, warm);
        _host.ForgetStaleReported(ReportedKeep);

        foreach (var gone in _tracks.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _tracks.Remove(gone);
        }
    }

    /// <summary>
    /// The two stalls no mail can reach anyone about: a team's Orchestrator down with a run open, and one waiting on a question in its terminal.
    /// Each runs the user's <c>alarm.command</c> when its episode starts, again every <see cref="StallSettings.AlarmRepeat"/> while it lasts,
    /// and once more when it ends; with none set, nothing happens.
    /// </summary>
    private void AlarmUser(IReadOnlyList<StallTeam> teams, DateTimeOffset now, bool warm)
    {
        // A team off the list has no open run: its episode ends, and its next run is a new one, even under the same dead session.
        foreach (var gone in _ownerDownSince.Keys.Where(name => teams.All(t => t.Name != name)).ToList())
        {
            _ = _ownerDownSince.Remove(gone);
        }

        // ah2-180 Amendment 4: every raised alarm of a team no longer watched ends, read from the record so a daemon start clears them too;
        // a team the user put on hold is told nothing.
        foreach (var key in _host.AlarmKeys())
        {
            var cut = key.LastIndexOf('/');
            if (!key.StartsWith(AlarmKeyPrefix, StringComparison.Ordinal) || cut <= AlarmKeyPrefix.Length || cut == key.Length - 1)
            {
                continue;
            }

            var name = key[AlarmKeyPrefix.Length..cut];
            if (teams.Any(t => t.Name == name))
            {
                continue;
            }

            if (_host.TeamHeld(name))
            {
                _host.SetAlarm(key, null);
            }
            else
            {
                TellUser(new StallTeam(name, string.Empty, string.Empty, null, string.Empty), key[(cut + 1)..], null, string.Empty, false, false, now);
            }
        }

        foreach (var team in teams)
        {
            var down = team.OrchestratorAddress is null;
            if (down)
            {
                _ = _ownerDownSince.TryAdd(team.Name, now);
            }
            else
            {
                _ = _ownerDownSince.Remove(team.Name);
            }

            // Warm-up holds both alarms without ending an episode, so a restart does not page again for one already told.
            if (warm)
            {
                continue;
            }

            var downLong = down && now - _ownerDownSince[team.Name] >= _settings.Down;

            // The down timer starts again with the daemon: an episode already told is held until it matures, never ended by it.
            if (!(down && !downLong && _host.Alarm($"alarm/{team.Name}/owner-down") is not null))
            {
                // A queued-only team (no run open) is told once and cleared, never repeated (spec-b22 J1b).
                TellUser(
                    team, "owner-down", downLong ? team.OrchestratorSession : null,
                    team.QueuedOnly ? $"ah: team {team.Name}: its Orchestrator is down and its team has work waiting." : $"ah: team {team.Name}: its Orchestrator is down and a run is open.",
                    true, !team.QueuedOnly, now);
            }

            var asked = down ? null : _host.AskingSince(team.HierarchyDir, team.OrchestratorSession);
            var askedLong = asked is { } since && now - since >= _settings.AskAlarmAfter;
            TellUser(team, "owner-asking", askedLong ? Timestamps.ToIsoString(asked!.Value) : null, $"ah: team {team.Name}: its Orchestrator is waiting for your answer in its terminal, and its team cannot move.", false, true, now);

            // ah2-180 C1: a live lead with unread mail ah could not wake it for.
            var stalled = down || team.QueuedOnly ? null : _host.Uncarried(team);
            TellUser(
                team, "owner-stalled", stalled is null ? null : team.OrchestratorSession,
                stalled is null ? string.Empty : $"{HierarchyModule.OrchestratorAddress(team.Name)} has {stalled.Unread} unread and ah could not wake it ({stalled.Reason}). Type anything in its pane.",
                false, true, now);
        }
    }

    /// <summary>
    /// Tells the user of an episode, again every repeat period, and that it cleared once a null <paramref name="episode"/> ends one whose
    /// first line got through. Only the team's name, the kind and, with <paramref name="digest"/>, event names and counts leave the machine.
    /// </summary>
    private void TellUser(StallTeam team, string kind, string? episode, string line, bool digest, bool repeat, DateTimeOffset now)
    {
        var key = $"{AlarmKeyPrefix}{team.Name}/{kind}";
        var held = _host.Alarm(key);
        if (episode is null)
        {
            if (held is null)
            {
                return;
            }

            if (!held.Delivered || _settings.Mode != StallMode.On)
            {
                _host.SetAlarm(key, null);
                return;
            }

            // A cleared line that cannot start because the last command still runs keeps the episode, so the next sweep sends it.
            if (Reserve(team, kind, now))
            {
                _host.SetAlarm(key, null);
                _ = Run(team, kind, $"ah: team {team.Name}: {kind} cleared ({Duration(now - held.FirstToldAt)}).", now, null);
            }

            return;
        }

        if (_settings.Mode != StallMode.On)
        {
            return;
        }

        string Told() => digest ? line + Digest(team) : line;
        UserAlarm next;
        if (held is null || held.Episode != episode || !held.Delivered)
        {
            next = new UserAlarm(episode, held is not null && held.Episode == episode ? held.FirstToldAt : now, now, false) { Kind = kind, Line = Told() };
        }
        else if (repeat && now - held.LastToldAt >= _settings.AlarmRepeat)
        {
            next = held with { LastToldAt = now, Kind = kind, Line = Told() };
        }
        else
        {
            return;
        }

        // The team is reserved before the record is written, so no sweep writes it while a command (and its exit callback) runs.
        if (!Reserve(team, kind, now))
        {
            return;
        }

        // Recorded before the start: the command may end, and mark its first line delivered, before the start returns.
        _host.SetAlarm(key, next);
        var first = !next.Delivered;
        var told = next.Line!;
        var start = Run(team, kind, told, now, exit =>
        {
            if (first && exit == 0 && _host.Alarm(key) is { } recorded && recorded.Episode == episode)
            {
                _host.SetAlarm(key, recorded with { Delivered = true });
            }
        });
        if (start != Start.Started)
        {
            // ah2-180 Amendment 2 (b): no channel carried it, but it is raised: the record stays (delivered as it was), so the alerts serve it and the next sweep tries a channel again
            _host.SetAlarm(key, held is not null && held.Episode == episode ? held with { Kind = kind, Line = told } : next);
        }
    }

    private enum Start
    {
        Started,
        Unset,
    }

    /// <summary>Unacked ahd events in the team's Orchestrator box, most frequent first: <c> N waiting: kind×n, ….</c>; empty when none or unreadable.</summary>
    private string Digest(StallTeam team)
    {
        List<KeyValuePair<string, int>> counts;
        try
        {
            counts = [.. _host.WaitingEvents(team).Where(static kv => kv.Value > 0).OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return string.Empty;
        }

        if (counts.Count == 0)
        {
            return string.Empty;
        }

        var shown = string.Join(", ", counts.Take(5).Select(static kv => $"{kv.Key}×{kv.Value}"));
        return $" {counts.Sum(static kv => kv.Value)} waiting: {shown}{(counts.Count > 5 ? $", +{counts.Count - 5} more" : string.Empty)}.";
    }

    private static string Duration(TimeSpan d) =>
        d <= TimeSpan.Zero ? "0m" : d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{(int)d.TotalMinutes}m";

    private JsonObject Row(StallTeam team, string kind, DateTimeOffset now) => new()
    {
        ["at"] = Timestamps.ToIsoString(now),
        ["team"] = team.Name,
        ["kind"] = kind,
        ["subject"] = team.OrchestratorSession,
        ["delivered_to"] = _host.AlarmChannel(),
    };

    /// <summary>
    /// Takes the team's one command slot; false, journalled, while the last command still runs. The fire is not told, so a later sweep
    /// makes it once the last one has ended. A taken slot is freed by <see cref="Run"/>.
    /// </summary>
    private bool Reserve(StallTeam team, string kind, DateTimeOffset now)
    {
        if (_alarmRunning.TryAdd(team.Name, 0))
        {
            return true;
        }

        var skipped = Row(team, kind, now);
        skipped["skipped"] = "still running";
        _host.Journal(skipped);
        return false;
    }

    /// <summary>Starts the user's command with <paramref name="line"/> in the slot <see cref="Reserve"/> took, journalling how it ended.</summary>
    private Start Run(StallTeam team, string kind, string line, DateTimeOffset now, Action<int>? exited)
    {
        JsonObject Row() => this.Row(team, kind, now);

        // The command runs off the sweep: every daemon job shares one executor, so waiting on it would hold them all.
        bool started;
        try
        {
            started = _host.StartAlarmCommand(line, team.Name, kind, exit =>
            {
                try
                {
                    var row = Row();
                    row["exit"] = exit;
                    _host.Journal(row);
                    exited?.Invoke(exit);
                }
                finally
                {
                    _ = _alarmRunning.TryRemove(team.Name, out _);
                }
            });
        }
        catch
        {
            // A start that throws never calls back, so the team is freed here or no later fire would run.
            _ = _alarmRunning.TryRemove(team.Name, out _);
            throw;
        }

        if (!started)
        {
            _ = _alarmRunning.TryRemove(team.Name, out _);
        }

        return started ? Start.Started : Start.Unset;
    }

    /// <summary>
    /// A stuck report a full window old whose member has made no tool call since is copied once to the Orchestrator of the team above its
    /// lane. A tool call since, or no such team in the walk, leaves it; the escalation is recorded only once it was mailed.
    /// </summary>
    private void EscalateStuck(IReadOnlyList<StallTeam> teams, DateTimeOffset now)
    {
        foreach (var due in StuckEscalation.Evaluate(_host.PeerRows(), now, _settings.StuckAfter))
        {
            if (teams.FirstOrDefault(t => t.Name == due.TopTeam) is not { OrchestratorAddress: { } to } top
                || _host.LastToolAt(top.HierarchyDir, due.Session) is { } toolAt && toolAt > due.StuckAt)
            {
                continue;
            }

            var delivered = "none";
            var exit = 0;
            var text = $"{due.Text}\nNo tool call from {due.Member} since the report; the lead has not cleared it.";
            if (_settings.Mode == StallMode.On)
            {
                try
                {
                    var data = new JsonObject { ["member"] = due.Member, ["session_id"] = due.Session, ["code"] = due.Code, ["stuck_at"] = Timestamps.ToIsoString(due.StuckAt) };
                    _host.Mail(new WatchMail(to, top.HierarchyDir, top.Cwd, StuckEscalation.EventName, text, data, top.OrchestratorSession));
                    delivered = "orchestrator";
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    exit = 1;
                }
            }

            if (exit == 0)
            {
                _host.RecordPeerRow(new JsonObject
                {
                    ["type"] = "watch-event",
                    ["session_id"] = due.Session,
                    ["request_id"] = JsonValue.Null,
                    ["kind"] = StuckEscalation.EscalatedKind,
                    ["text"] = text,
                    ["ts"] = Timestamps.ToIsoString(now),
                    ["mailed"] = delivered != "none",
                    ["code"] = due.Code,
                    ["stuck_at"] = Timestamps.ToIsoString(due.StuckAt),
                });
            }

            _host.Journal(new JsonObject
            {
                ["at"] = Timestamps.ToIsoString(now),
                ["team"] = top.Name,
                ["kind"] = StuckEscalation.EventName,
                ["subject"] = due.Member,
                ["evidence"] = due.Code,
                ["delivered_to"] = delivered,
                ["exit"] = exit,
            });
        }
    }

    /// <summary>The end of the member's pane; a read that fails costs the alarm its cause, never the alarm.</summary>
    private string? PaneTail(StallTeam team, StallMember member)
    {
        try
        {
            return _host.PaneTail(team, member);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private bool Emit(StallTeam team, StallMember member, string kind, string first, string second, IReadOnlyList<string> work, bool urgent, DateTimeOffset since, string why, string? cause = null, string? resume = null)
    {
        var text = new System.Text.StringBuilder().Append(first).Append('\n').Append(second);
        if (work.Count > 0)
        {
            text.Append("\nOpen work:");
            foreach (var line in work)
            {
                text.Append("\n- ").Append(line);
            }
        }

        var openWork = new JsonArray();
        openWork.AddRange(work.Select(static w => (JsonValue)w));
        var data = new JsonObject
        {
            ["kind"] = kind,
            ["member"] = member.Name,
            ["role"] = member.Role is { } role ? role : JsonValue.Null,
            ["since"] = Timestamps.ToIsoString(since),
            ["urgent"] = urgent,
            ["open_work"] = openWork,
        };
        if (cause is not null)
        {
            data["cause"] = cause;
        }

        if (resume is not null)
        {
            data["resume"] = resume;
        }

        var delivered = "none";
        var exit = 0;
        if (_settings.Mode == StallMode.On && team.OrchestratorAddress is { } to)
        {
            try
            {
                _host.Mail(new WatchMail(to, team.HierarchyDir, team.Cwd, kind, text.ToString(), data, team.OrchestratorSession));
                delivered = "orchestrator";
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                exit = 1;
                why = $"{why}; not mailed: {e.Message}";
            }
        }

        _host.Journal(new JsonObject
        {
            ["at"] = Timestamps.ToIsoString(_now()),
            ["team"] = team.Name,
            ["kind"] = kind,
            ["subject"] = member.Name,
            ["evidence"] = why,
            ["delivered_to"] = delivered,
            ["exit"] = exit,
        });
        return exit == 0;
    }

    private sealed class Track
    {
        public DateTimeOffset? DeadSince;
        public string? Reported;
    }
}
