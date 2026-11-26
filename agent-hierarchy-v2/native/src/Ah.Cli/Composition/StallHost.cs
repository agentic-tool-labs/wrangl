using System.Text;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Mesh;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>The stall watch's reads and writes over the daemon's service graph.</summary>
internal sealed class StallHost(ServiceGraph g, string ahHome, Func<bool> owner, Func<IReadOnlyList<string>?> knownDirs, Func<int, string?, bool>? processIsThere = null, Func<IReadOnlyList<string>, IReadOnlyList<string>>? admitDirs = null, Func<Ah.Launch.IHerdr, string, string, string?>? paneTail = null, Ah.Commands.IMemberRemoval? memberRemoval = null, Action<string>? log = null) : IStallHost
{
    private readonly Lazy<Ah.Commands.IMemberRemoval> _removal = new(
        () => memberRemoval ?? Ah.Commands.CommandsModule.CreateMemberRemoval(new RosterComposition(g, g.Io.Clock, g.Environment)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// ah2-592: a member proven dead is removed through dismiss's close, unless it is an orchestrator, a sub-orchestrator or the deputy on
    /// duty, its team is held or its checkpoint lists it, a relaunch covers it, or it reads alive or under another session when looked at again.
    /// </summary>
    public StallRemoval? RemoveDead(StallTeam team, StallMember member, StallJudgement judged, DateTimeOffset decidedAt)
    {
        if (judged.Verdict.Status != Verdict.Dead || member.Role is "orchestrator" or "sub-orchestrator"
            || g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name) is not { IsMap: true } record
            || (record.Member("duty")?.Member("deputy") is { IsText: true } deputy && deputy.AsString() == member.Name)
            || g.Roster.HoldOf(team.HierarchyDir, team.Name) is not null || CheckpointLists(team, member.Name) || RecoveryCovers(team, member)
            || (team.Cwd.Length > 0 ? team.Cwd : AhPaths.RepoRootOf(team.HierarchyDir)) is not { } projectRoot)
        {
            return null;
        }

        // the session came back unless every sign of life, read now, predates the verdict (WorkTreeAssignment's told_at rule)
        _lastSeen = ReadLastSeen();
        var again = Judge(team, member);
        if (again.Verdict.Status != Verdict.Dead || again.RegistrationSession != judged.RegistrationSession || again.LastEvidence is not { } life || life >= decidedAt)
        {
            return null;
        }

        // R6: the work is on record before anything is closed, so it outlives a mail that fails afterwards
        var removed = _removal.Value.Remove(team.HierarchyDir, projectRoot, team.Name, member.Name, read => Journal(new JsonObject
        {
            ["at"] = Ah.Core.Time.Timestamps.ToIsoString(g.Io.Clock.UtcNow),
            ["team"] = team.Name,
            ["kind"] = "member-remove",
            ["subject"] = member.Name,
            ["capture"] = read,
        }));
        if (removed is { Removed: true, Capture: { } capture })
        {
            return new StallRemoval(capture, removed.Abandoned);
        }

        // a close that returned but whose re-read of the team threw may have removed the row: only the R7 log line below says so
        if (removed.Capture is not null && !removed.Unverified)
        {
            // the member-remove row is written: record that the close then failed, so the journal does not read as a removal
            try
            {
                Journal(new JsonObject
                {
                    ["at"] = Ah.Core.Time.Timestamps.ToIsoString(g.Io.Clock.UtcNow),
                    ["team"] = team.Name,
                    ["kind"] = "member-remove-failed",
                    ["subject"] = member.Name,
                    ["why"] = removed.Why ?? "no reason given",
                });
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // the R7 log line below still says the removal failed
            }
        }

        // R7: the stall watch tries once per episode, so this is logged once per episode
        log?.Invoke($"stall-watch: {team.Name}/{member.Name} proven dead but not removed: {removed.Why ?? "no reason given"}");
        return null;
    }

    /// <summary>The pane's recent screen, read through the herdr server the member lives on (<see cref="LaunchServices.HerdrFor(JsonValue?, string?, JsonObject?)"/>).</summary>
    public string? PaneTail(StallTeam team, StallMember member)
    {
        if (paneTail is not { } read || member.PaneId is not { } pane)
        {
            return null;
        }

        var record = g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name);
        return record.Member("transport") is { IsText: true } transport ? read(g.Launch.HerdrFor(record, member.Name), transport.AsString(), pane) : null;
    }

    public void Log(string line) => log?.Invoke(line);

    public bool IsSessionId(string session) => g.Launch.SnapshotRules.IsSessionId(session);

    public bool IsRoleName(string role) => g.Config.IsRoleName(role);

    public bool CheckpointLists(StallTeam team, string member)
    {
        try
        {
            var read = g.Hierarchy.StateCalls.Checkpoint("read", new JsonObject { ["dir"] = team.HierarchyDir, ["team"] = team.Name });
            return read.Member("found") is { IsTruthy: true } && Ah.Mesh.MeshModule.RecordListsMember(read.Member("record"), member, g.Roster);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Whether <paramref name="pid"/> still is the Claude process started at <paramref name="start"/>: the same test the registrations are judged by.</summary>
    private bool ProcessIsThere(int pid, string? start) =>
        processIsThere is { } override_ ? override_(pid, start) : g.Hierarchy.Core.Liveness is { } liveness && liveness.PidIdentityLive(pid, start);

    /// <summary>The matched <c>bare</c> marks (key: hierarchy dir, team, member) of the rows as the last <see cref="Members"/> read them.</summary>
    private readonly Dictionary<string, BareMark> _bare = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _starting = new(StringComparer.Ordinal);

    private static string BareKey(StallTeam team, string member) => team.HierarchyDir + "|" + team.Name + "/" + member;

    public bool Owner() => owner();

    public IReadOnlyList<StallTeam> Teams() => Enumerate(includeHeld: false);

    /// <summary>The teams of <see cref="Teams"/> and the queued-only teams (spec-b22 J1) only the user alarm may see.</summary>
    public IReadOnlyList<StallTeam> AlarmTeams() => Enumerate(includeHeld: false, includeQueued: true);

    /// <summary>The teams of <see cref="Teams"/> and the held teams it leaves out, by the same enumeration and the same held predicate.</summary>
    internal IReadOnlyList<StallTeam> TeamsIncludingHeld() => Enumerate(includeHeld: true);

    public IReadOnlyList<string> AlarmKeys() => Episodes.Keys("alarm/");

    public bool TeamHeld(string team) => Dirs().Any(dir => g.Roster.HoldOf(dir, team) is not null);

    private List<StallTeam> Enumerate(bool includeHeld, bool includeQueued = false)
    {
        var found = new List<StallTeam>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in Dirs())
        {
            // The anchor is looked up only for a team whose orchestrator is not live: a live one is a stall team whatever its anchors say.
            var home = g.Roster.TeamHome(dir);
            foreach (var name in g.Roster.ListTeamNames(dir))
            {
                if (!seen.Add(g.Roster.TeamPath(home, name)))
                {
                    continue;
                }

                if (g.Roster.ReadTeamRecord(dir, name) is not { IsMap: true } record)
                {
                    continue;
                }

                // A held team is never swept: no member-down, bare mark or relaunch is made for it. The bare watch asks for the held
                // teams too, to record that its members were restored; it writes nothing to the team and sends nothing.
                if (!includeHeld && g.Roster.HoldOf(dir, name) is not null)
                {
                    continue;
                }

                var orchestrating = Ah.Hierarchy.HierarchyModule.OrchestratingSessionOf(record, name);
                var session = orchestrating?.SessionId ?? string.Empty;
                var live = orchestrating is { Pid: { } pid } && g.Hierarchy.Core.Liveness is { } liveness
                    && liveness.PidVerdict(pid, orchestrating.PidStart) != Ah.Core.Time.LivenessVerdict.ProvenDead;
                var anchored = !live && g.Hierarchy.Decisions.OpenRunAnchor(dir, name).Get("id") is { IsText: true };

                // Queued work (spec-b22 J1) lists a team with neither for the user alarm only: an open exchange, else a checklist row waiting for dispatch.
                var queued = includeQueued && !live && !anchored
                    && (g.Hierarchy.Messages.OpenExchanges(dir, name).Count > 0 || QueueRows.Of(g, dir, name, g.Io.Clock.UtcNow.ToUnixTimeMilliseconds(), g.Checklists.Of).Count > 0);
                if (live || anchored || queued)
                {
                    var cwd = g.Mailbox.ReadRegistration(session)?.Get("cwd") is { IsText: true } c ? c.AsString() : string.Empty;
                    found.Add(new StallTeam(name, dir, cwd, live ? orchestrating!.Address : null, session) { QueuedOnly = queued });
                }
            }
        }

        return found;
    }

    /// <summary>The hierarchy dirs the watch walks, team homes first: those of live registrations and the known dirs, as admitted.</summary>
    private List<string> Dirs()
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var registration in g.Mailbox.LiveRegistrations())
        {
            foreach (var file in MeshModule.RegistrationTeamFiles(registration))
            {
                _ = dirs.Add(NodePath.Dirname(NodePath.Dirname(file)));
            }
        }

        foreach (var dir in knownDirs() ?? [])
        {
            _ = dirs.Add(dir);
        }

        if (admitDirs is { } admit)
        {
            dirs = new HashSet<string>(admit([.. dirs]), StringComparer.Ordinal);
        }

        // Every work tree's hierarchy dir reads the main checkout's team files, so one team is listed under all of them; it is one team,
        // taken once, under the dir that is the team home itself when that dir is known.
        foreach (var listed in dirs.ToList())
        {
            _ = dirs.Add(g.Roster.TeamHome(listed));
        }

        return [.. dirs.OrderBy(d => g.Roster.TeamHome(d) == d ? 0 : 1).ThenBy(d => d, StringComparer.Ordinal)];
    }

    public IReadOnlyList<StallMember> Members(StallTeam team)
    {
        var members = new List<StallMember>();
        if (g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name) is not { IsMap: true } record
            || record.AsObject().Get("members") is not { IsArray: true } rows)
        {
            return members;
        }

        foreach (var row in rows.AsArray())
        {
            if (row is not { IsMap: true } map || map.AsObject().Get("name") is not { IsText: true } name)
            {
                continue;
            }

            var o = map.AsObject();
            var pane = o.Get("transport_id") is { IsText: true } t ? global::Ah.Abstractions.HeadlessTransport.PaneOf(t.AsString()) : null;
            members.Add(new StallMember(
                name.AsString(), o.Get("role") is { IsText: true } role ? role.AsString() : null, pane, o.Get("sweep_keep") is { IsTruthy: true },
                o.Get("stream") is { IsText: true } s && global::Ah.Config.ConfigModule.IsStreamName(s.AsString()) ? s.AsString() : null));
            if (global::Ah.Roster.RosterModule.StartingAgeSeconds(map, g.Io.Clock.UtcNow) is { } startingAge)
            {
                _starting[BareKey(team, name.AsString())] = startingAge;
            }
            else
            {
                _ = _starting.Remove(BareKey(team, name.AsString()));
            }

            if (BareMark.From(o.Get("bare")) is { State: BareMark.Matched } mark)
            {
                _bare[BareKey(team, name.AsString())] = mark;
            }
            else
            {
                _ = _bare.Remove(BareKey(team, name.AsString()));
            }
        }

        return members;
    }

    public bool RecoveryCovers(StallTeam team, StallMember member)
    {
        if (_bare.ContainsKey(BareKey(team, member.Name)))
        {
            return true;
        }

        try
        {
            return BareHost.ReadRow(g.RecoverRecords, team, member.Name, false) is { IsMap: true } record
                && WatchModule.RecoveryCovers(record.Member("state") is { IsText: true } state ? state.AsString() : null);
        }
        catch (StoreUnavailableException)
        {
            return false;
        }
    }

    public StallJudgement Judge(StallTeam team, StallMember member)
    {
        if (_bare.TryGetValue(BareKey(team, member.Name), out var bare) && bare.Pid is { } bpid && ProcessIsThere(bpid, bare.PidStart))
        {
            return new StallJudgement(new MemberVerdict(Verdict.Live, "Live (bare): running without ah's launch"), "pane:" + member.PaneId, g.Io.Clock.UtcNow);
        }

        var registration = g.Hierarchy.Core.Liveness?.ForMember(member.Name, team.Name);
        var verdict = Ah.Hierarchy.HierarchyModule.JudgeMember(registration ?? FromPeerRow(team, member.Name), member.PaneId, () => Probe(team, member.Name, registration?.SessionId),_starting.TryGetValue(BareKey(team, member.Name), out var startingAge) ? startingAge : null);
        var session = registration?.SessionId ?? (member.PaneId is { Length: > 0 } pane ? "pane:" + pane : string.Empty);

        // last_seen_at moves only when the session row is written, usually at its start, so the activity record is what says when a member was last alive.
        var life = verdict.Status == Verdict.Live ? null : g.Hierarchy.Roster.ActivityLifeOf(team.HierarchyDir, registration?.SessionId, member.Name);
        return new StallJudgement(verdict, session, Evidence(registration?.At, registration is null ? null : LastSeen(registration.SessionId), life));
    }

    /// <summary>The verdict for a member with no registration, by its newest peers row, as every other reader judges it; null when it has no row.</summary>
    private Ah.Hierarchy.MemberRegistrationLiveness? FromPeerRow(StallTeam team, string member)
    {
        var judged = g.Hierarchy.Roster.AttributedLivenessIn(team.HierarchyDir, JsonValue.FromString(member), team.Name);
        return judged.Get("rec") is { IsMap: true }
            ? new Ah.Hierarchy.MemberRegistrationLiveness(string.Empty, judged.Get("live") is { IsTruthy: true }, judged.Get("why") is { IsText: true } why ? why.AsString() : string.Empty, ProvenDead: judged.Get("proven_dead") is { IsTruthy: true })
            : null;
    }

    /// <summary>The latest sign of life: the newest of when the registration was written, when the session was last seen and its activity record's own time.</summary>
    internal static DateTimeOffset? Evidence(DateTimeOffset? registered, DateTimeOffset? lastSeen, DateTimeOffset? activity = null) =>
        new[] { registered, lastSeen, activity }.Max();

    private Dictionary<string, DateTimeOffset> _lastSeen = new(StringComparer.Ordinal);

    public void BeginSweep() => _lastSeen = ReadLastSeen();

    private DateTimeOffset? LastSeen(string sessionId) => _lastSeen.TryGetValue(sessionId, out var seen) ? seen : null;

    private Dictionary<string, DateTimeOffset> ReadLastSeen()
    {
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        if (g.OpenReader(ahHome).Reader is not { } reader)
        {
            return seen;
        }

        using (reader)
        {
            try
            {
                foreach (var row in reader.Query("SELECT session_id, last_seen_at FROM sessions WHERE last_seen_at IS NOT NULL"))
                {
                    if (row[0] is string id && row[1] is long ms)
                    {
                        seen[id] = DateTimeOffset.FromUnixTimeMilliseconds(ms);
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                seen.Clear();
            }
        }

        return seen;
    }

    private Lazy<IAlarmEpisodes>? _episodes;

    internal IAlarmEpisodes Episodes => LazyInitializer.EnsureInitialized(ref _episodes, () => new Lazy<IAlarmEpisodes>(() => WatchModule.CreateAlarmEpisodes(LoadReported, SaveReported, () => g.Io.Clock.UtcNow, KeepUnreadable))).Value;

    public string? ReportedSession(string key) => Episodes.Told(key);

    public void SetReportedSession(string key, string? session)
    {
        if (session is null)
        {
            Episodes.Clear(key);
        }
        else
        {
            Episodes.Tell(key, session);
        }
    }

    /// <summary>
    /// The user alarm kept for <paramref name="key"/> in the reported episodes. An entry written before the alarm kept its times reads as
    /// delivered and told now, and is written back so, so an upgrade neither pages at once nor keeps postponing the repeat.
    /// </summary>
    public UserAlarm? Alarm(string key)
    {
        if (Episodes.Entry(key) is not { } entry || entry.Get("episode") is not { IsText: true } episode)
        {
            return null;
        }

        if (At(entry, "first_told_at") is { } first && At(entry, "last_told_at") is { } last)
        {
            return new UserAlarm(episode.AsString(), first, last, entry.Get("delivered") is not { } d || d.IsTruthy)
            {
                Kind = entry.Get("kind") is { IsText: true } kind ? kind.AsString() : null,
                Line = entry.Get("line") is { IsText: true } line ? line.AsString() : null,
                StartedAt = At(entry, "started_at"),
            };
        }

        var now = g.Io.Clock.UtcNow;
        var upgraded = new UserAlarm(episode.AsString(), now, now, true);
        SetAlarm(key, upgraded);
        return upgraded;
    }

    public void SetAlarm(string key, UserAlarm? alarm)
    {
        if (alarm is null)
        {
            Episodes.Clear(key);
            return;
        }

        var row = new JsonObject
        {
            ["episode"] = alarm.Episode,
            ["first_told_at"] = Ah.Core.Time.Timestamps.ToIsoString(alarm.FirstToldAt),
            ["last_told_at"] = Ah.Core.Time.Timestamps.ToIsoString(alarm.LastToldAt),
            ["delivered"] = alarm.Delivered,
        };
        // ah2-180 Amendment 2 (a): a raised alarm keeps its kind and line, which the user-alarms lane serves as the team's alert
        if (alarm.Kind is { } kind && alarm.Line is { } line)
        {
            row["kind"] = kind;
            row["line"] = line;
            row["raised_at"] = Ah.Core.Time.Timestamps.ToIsoString(alarm.FirstToldAt);
        }

        if (alarm.StartedAt is { } started)
        {
            row["started_at"] = Ah.Core.Time.Timestamps.ToIsoString(started);
        }

        Episodes.Put(key, row);
    }

    private static DateTimeOffset? At(JsonObject entry, string field) =>
        entry.Get(field) is { IsText: true } at && Ah.Core.Time.Timestamps.TryParseMs(at.AsString(), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms) : null;

    /// <summary>The unacked events ahd mailed to the team's Orchestrator, by event name, over every box at its address.</summary>
    public IReadOnlyDictionary<string, int> WaitingEvents(StallTeam team)
    {
        var address = Ah.Hierarchy.HierarchyModule.OrchestratorAddress(team.Name);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var box in g.Mailbox.ListBoxes().Where(b => b.Address == address))
        {
            foreach (var entry in g.Mailbox.Pending(box.Dir))
            {
                if (entry.Member("kind") is { IsText: true } kind && kind.AsString() == "event"
                    && entry.Member("from") is { IsText: true } from && from.AsString() == "ahd"
                    && entry.Member("event") is { IsText: true } name && name.AsString().Length > 0)
                {
                    counts[name.AsString()] = counts.GetValueOrDefault(name.AsString()) + 1;
                }
            }
        }

        return counts;
    }

    /// <summary>
    /// ah2-180 C1: the unread entries at the team's <c>orchestrator@</c> address, when the Orchestrator session's uncarried mark is not older than the
    /// oldest of them (the wake failed for this mail, not for mail since read); null when there is no unread entry or no such mark.
    /// </summary>
    public LeadUncarried? Uncarried(StallTeam team)
    {
        if (team.OrchestratorSession.Length == 0 || g.Mailbox.DoorbellUncarriedMark(team.OrchestratorSession) is not { } mark)
        {
            return null;
        }

        var address = Ah.Hierarchy.HierarchyModule.OrchestratorAddress(team.Name);
        var unread = g.Mailbox.ListBoxes().Where(b => b.Address == address).SelectMany(b => g.Mailbox.Pending(b.Dir)).ToList();
        if (unread.Count == 0)
        {
            return null;
        }

        var oldest = unread.Min(static e => e.Member("created") is { IsText: true } c && Ah.Core.Time.Timestamps.TryParseMs(c.AsString(), out var ms) ? ms : double.MaxValue);
        return mark.At >= oldest ? new LeadUncarried(unread.Count, mark.Reason) : null;
    }

    internal string AhHome => ahHome;

    public void ForgetStaleReported(TimeSpan unseenFor) => Episodes.Forget(unseenFor);

    private string? LoadReported() => StallStore.Read(ahHome, StallStore.Reported);

    private void KeepUnreadable(string text) => StallStore.Put(g, StallStore.ReportedUnreadable, text);

    private void SaveReported(string json) => StallStore.Put(g, StallStore.Reported, json);

    public IReadOnlyList<string> OpenWork(StallTeam team, StallMember member)
    {
        var lines = new List<string>();
        foreach (var exchange in g.Hierarchy.Messages.OpenExchanges(team.HierarchyDir, team.Name))
        {
            if (exchange.Member("request")?.Member("path") is not { IsText: true } path
                || g.Hierarchy.Core.ReadMsgFile(path.AsString()).Member("fm") is not { IsMap: true } fm
                || fm.AsObject().Get("to_name") is not { IsText: true } toName || toName.AsString() != member.Name)
            {
                continue;
            }

            lines.Add($"request {Field(fm, "slug")} ({g.Hierarchy.Core.ParseMsgFilename(path.AsString())?.Id}) from {Field(fm, "from")}");
        }

        return lines;
    }

    public IReadOnlyList<JsonValue> PeerRows() => g.Hierarchy.Core.PeerRecordsWhere(new PeerQuery(Types: new HashSet<string>(StringComparer.Ordinal) { "watch-event" }));

    public void RecordPeerRow(JsonObject row) => g.Hierarchy.Peers.RecordPeerRow(JsonValue.FromObject(row));

    public DateTimeOffset? LastToolAt(string hierarchyDir, string session) =>
        g.Hierarchy.Status.ReadActivityRecord(hierarchyDir, JsonValue.FromString(session)).Member("tool_at") is { IsText: true } at
        && Ah.Core.Time.Timestamps.TryParseMs(at.AsString(), out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : null;

    public DateTimeOffset? AskingSince(string hierarchyDir, string session)
    {
        var record = g.Hierarchy.Status.ReadActivityRecord(hierarchyDir, JsonValue.FromString(session));
        return record.Member("activity") is { IsText: true } activity && activity.AsString() == "blocked"
            && record.Member("blocked_by") is { IsText: true } by && by.AsString() == "question"
            && record.Member("state_since") is { IsText: true } since && Ah.Core.Time.Timestamps.TryParseMs(since.AsString(), out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
                : null;
    }

    public bool StartAlarmCommand(string line, string team, string kind, Action<int> exited)
    {
        if (AlarmSender() is not { } send)
        {
            return false;
        }

        var channel = AlarmChannel();
        _ = Task.Run(() =>
        {
            int exit;
            try
            {
                exit = send(line);
            }
            catch (OperationCanceledException)
            {
                exit = -1;
            }

            try
            {
                exited(exit);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // The caller's own row failed; this one says the command ran and how it ended.
                try
                {
                    Journal(new JsonObject { ["at"] = Ah.Core.Time.Timestamps.ToIsoString(g.Io.Clock.UtcNow), ["team"] = team, ["kind"] = kind, ["delivered_to"] = channel, ["exit"] = exit, ["error"] = e.GetType().Name });
                }
                catch (Exception again) when (again is not OutOfMemoryException)
                {
                    // the journal itself cannot be written: nothing left to record it in
                }
            }
        });
        return true;
    }

    /// <summary>Runs the user's <c>alarm.command</c>, else a herdr notification, and waits for it; null when neither is there.</summary>
    internal int? RunAlarmCommand(string line) => AlarmSender() is { } send ? send(line) : null;

    public string AlarmChannel() => AlarmCommand() is null && NotificationHerdr() is not null ? "herdr" : "alarm.command";

    private Func<string, int>? AlarmSender() =>
        AlarmCommand() is { } argv ? line => RunAlarmCommand(argv, line)
        : NotificationHerdr() is { } herdr ? line => Notify(herdr, line)
        : null;

    /// <summary>The absolute <c>herdr</c> on ah's PATH, which has <c>notification show</c> (herdr core has none); null when it is not there.</summary>
    private string? NotificationHerdr() => g.Launch.HerdrFor(Ah.Core.Mux.HerdrFlavour.Tui).FindOnPath("herdr");

    private int Notify(string herdr, string line)
    {
        try
        {
            var result = g.Launch.Mux.Run(herdr, ["notification", "show", "ah alarm", "--body", line], 2000);
            return result.Ok ? 0 : result.Status ?? -1;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return -1;
        }
    }

    private int RunAlarmCommand(IReadOnlyList<string> argv, string line)
    {
        var environment = new Dictionary<string, string?> { ["AH_ALARM"] = line, ["HOME"] = g.Environment.HomeDirectory, ["PATH"] = g.Environment.GetVariable("PATH") };
        try
        {
            return g.Runner.Run(new Ah.Abstractions.ProcessRequest(argv, Environment: environment, Stdin: Encoding.UTF8.GetBytes(line + "\n"), Timeout: TimeSpan.FromSeconds(10))).ExitCode ?? -1;
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            return -1;
        }
    }

    /// <summary>The argv of <c>alarm.command</c> in the user's own config, never a repo's; null when it is unset or not an array of strings.</summary>
    private IReadOnlyList<string>? AlarmCommand()
    {
        try
        {
            var path = g.Config.UserConfigPath();
            return g.Io.Files.Exists(path) ? WatchModule.AlarmCommandOf(g.Io.Files.ReadText(path)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private readonly object _sendLock = new();

    public void Mail(WatchMail mail)
    {
        lock (_sendLock)
        {
            SendMail(mail);
        }
    }

    private void SendMail(WatchMail mail)
    {
        var request = MeshModule.EventRequest(mail.To, "ahd", mail.Cwd, mail.HierarchyDir, mail.Event, mail.Text);
        request["session_id"] = JsonValue.Null;
        request["data"] = mail.Data;
        request["to_session"] = mail.ToSession;
        if (mail.Id is not null)
        {
            request["id"] = mail.Id;
        }

        var outcome = g.Mailbox.Send(request, mail.CreatedAt ?? g.Io.Clock.UtcNow);
        if (!outcome.Ok)
        {
            throw new InvalidOperationException(outcome.Failure?.ToString() ?? "the mail was refused");
        }
    }

    public void Journal(JsonObject row)
    {
        lock (_sendLock)
        {
            StallStore.Journal(g, row);
        }
    }

    private static string Field(JsonValue map, string key) => map.AsObject().Get(key) is { IsText: true } v ? v.AsString() : string.Empty;

    // ah2-156: the server the member lives on, by its registration, its row and its team
    private PaneProbe Probe(StallTeam team, string name, string? session)
    {
        var registration = session is null ? null : g.Mailbox.ReadRegistration(session);
        var state = g.Launch.HerdrFor(g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name), name, registration).AgentState(name);
        return new PaneProbe(state.Indeterminate, state.AgentStatus, state.AgentType, state.Why);
    }
}
