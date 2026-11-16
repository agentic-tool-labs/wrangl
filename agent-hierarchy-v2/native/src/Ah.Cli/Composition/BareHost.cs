using Ah.Abstractions;
using Ah.Commands;
using Ah.Config;
using Ah.Core.Json;
using Ah.Core.Text;
using Ah.Hierarchy;
using Ah.Hierarchy.StateCalls;
using Ah.Launch;
using Ah.Platform;
using Ah.Watch;

namespace Ah.Cli.Composition;

/// <summary>The bare watch's reads and writes over the daemon's service graph; teams, mail and the journal are the stall host's.</summary>
internal sealed class BareHost(ServiceGraph g, StallHost stall, Func<IReadOnlyList<StallTeam>>? teamsToScan = null, Func<int, string?>? commandLineOf = null, IMemberRelaunch? memberRelaunch = null, Ah.Mesh.IHeardProcesses? heard = null) : IBareHost
{
    public bool Owner() => stall.Owner();

    public bool TeamHeld(StallTeam team) => g.Roster.HoldOf(team.HierarchyDir, team.Name) is not null;

    /// <summary>The teams for one sweep. It is called once at the start of a sweep, and the reads the sweep's claim checks need are taken here, once.</summary>
    public IReadOnlyList<StallTeam> Teams()
    {
        var teams = stall.TeamsIncludingHeld();
        Volatile.Write(ref _sweep, ReadForSweep(teamsToScan is null ? teams : teamsToScan()));
        return teams;
    }

    public void Mail(WatchMail mail) => stall.Mail(mail);

    public void Journal(JsonObject row) => stall.Journal(row);

    public IReadOnlyList<BareRow> Rows(StallTeam team)
    {
        var rows = new List<BareRow>();
        if (g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name) is not { IsMap: true } record || record.AsObject().Get("members") is not { IsArray: true } list)
        {
            return rows;
        }

        var teamTransport = record.AsObject().Get("transport") is { IsText: true } tt ? tt.AsString() : null;
        var context = LaunchContextOf(team);
        foreach (var row in list.AsArray().Where(r => r.IsMap))
        {
            var o = row.AsObject();
            var transport = o.Get("transport") is { IsText: true } rt ? rt.AsString() : teamTransport;
            if (o.Get("name") is not { IsText: true } name
                || o.Get("transport_id") is not { IsText: true } pane || pane.AsString().Length == 0
                || global::Ah.Abstractions.HeadlessTransport.IsId(pane.AsString())
                || transport != "herdr"
                || (o.Get("kind") is { IsText: true } kind && kind.AsString() != g.Launch.Registry.KindDefault))
            {
                continue;
            }

            var expected = new AhLaunchExpectation(
                g.Launch.Shapes.AgentRefOf(row, context),
                name.AsString(),
                g.Roster.TeamPath(team.HierarchyDir, team.Name),
                o.Get("model") is { IsText: true } model ? model.AsString() : null,
                o.Get("effort") is { IsText: true } effort ? effort.AsString() : null);
            var root = o.Get("expected_root") is { IsText: true } er ? er.AsString() : null;
            rows.Add(new BareRow(
                name.AsString(),
                o.Get("role") is { IsText: true } role ? role.AsString() : null,
                new BareMember(team.Name, name.AsString(), pane.AsString(), root, expected),
                BareMark.From(o.Get("bare")),
                o.Get("spawned_at") is { IsText: true } sa && Ah.Core.Time.Timestamps.TryParseMs(sa.AsString(), out var spawnedMs) ? DateTimeOffset.FromUnixTimeMilliseconds((long)spawnedMs) : null,
                o.Get("pid") is { IsNumber: true }));
        }

        return rows;
    }

    /// <summary>A short, fixed name for a hierarchy directory, so two projects' teams and members of the same names never share a file.</summary>
    internal static string DirKey(string hierarchyDir) => WatchModule.RecoverDirKey(hierarchyDir);

    /// <summary>The member's recovery row (<paramref name="control"/>: the owner's requests) as JSON, or null when there is none or it does not parse.</summary>
    internal static JsonValue? ReadRow(IRecoverRecordStore store, StallTeam team, string member, bool control)
    {
        try
        {
            return store.Read(DirKey(team.HierarchyDir), team.Name, member, control) is { } text ? JsonText.Parse(text) : (JsonValue?)null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    public JsonValue? ReadRecord(StallTeam team, string member) => ReadRow(g.RecoverRecords, team, member, false);

    public JsonValue? ReadControl(StallTeam team, string member) => ReadRow(g.RecoverRecords, team, member, true);

    private void WriteRow(StallTeam team, string member, bool control, JsonObject doc) =>
        g.RecoverRecords.Write(DirKey(team.HierarchyDir), team.Name, member, control, JsonText.Stringify(JsonValue.FromObject(doc), 2));

    private readonly object _controlLock = new();

    /// <summary>
    /// The owner's request for one member, already proven by the caller: <c>hold</c> and <c>release</c> set or clear the hold,
    /// <c>now</c> stamps the time, <c>show</c> only reads. The team is the one this daemon watches in that hierarchy directory.
    /// Null, with the reason in <paramref name="problem"/>, when the daemon does not watch such a team or the team has no such member.
    /// </summary>
    internal JsonObject? Ask(string hierarchyDir, string teamName, string member, string op, string by, string? sessionId, out string problem)
    {
        problem = string.Empty;
        var team = TeamAt(hierarchyDir, teamName);
        if (team is null)
        {
            problem = $"the daemon does not watch a team \"{teamName}\" in {hierarchyDir}";
            return null;
        }

        if (op is "enable" or "disable" or "status")
        {
            if (op != "status")
            {
                SetEnabled(op == "enable", new SwitchAuthor(by, by == "orchestrator" ? teamName : null, by == "orchestrator" ? sessionId : null, null, null));
            }

            var status = new JsonObject { ["action"] = "recover " + op, ["recovery_enabled"] = JsonValue.FromBool(RecoveryEnabled) };
            if (RecoveryProblem is { } problemText)
            {
                status["recovery_problem"] = problemText;
            }

            foreach (var key in SwitchAttributionKeys)
            {
                if (key == "session_id" && by == "member")
                {
                    continue;
                }

                if (SwitchValue(key) is { } value)
                {
                    status[key] = value;
                }
            }

            return status;
        }

        if (op == "now" && !RecoveryEnabled)
        {
            problem = "recovery is switched off; run `ah roster recover enable` first";
            return null;
        }

        if (!stall.Members(team).Any(m => m.Name == member))
        {
            problem = $"team {teamName} has no member \"{member}\"";
            return null;
        }

        lock (_controlLock)
        {
            try
            {
                return AskLocked(team, teamName, member, op);
            }
            catch (StoreUnavailableException e)
            {
                problem = e.Message;
                return null;
            }
        }
    }

    private JsonObject AskLocked(StallTeam team, string teamName, string member, string op)
    {
        var current = ReadControl(team, member);
        var hold = current is { IsMap: true } c && c.Member("hold") is { IsTruthy: true };
        var nowAt = current is { IsMap: true } n && n.Member("now_at") is { IsText: true } at ? at.AsString() : null;
        if (op != "show")
        {
            hold = op == "hold" || (hold && op != "release");
            nowAt = op == "now" ? g.Io.Clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : nowAt;
            WriteRow(team, member, true, new JsonObject { ["hold"] = JsonValue.FromBool(hold), ["now_at"] = nowAt is null ? JsonValue.Null : nowAt });
        }

        var shown = new JsonObject
        {
            ["team"] = teamName,
            ["member"] = member,
            ["action"] = "recover " + op,
            ["record"] = ReadRecord(team, member) is { } record ? record : JsonValue.Null,
            ["control"] = new JsonObject { ["hold"] = JsonValue.FromBool(hold), ["now_at"] = nowAt is null ? JsonValue.Null : nowAt },
            ["recovery_enabled"] = JsonValue.FromBool(RecoveryEnabled),
        };
        if (RecoveryProblem is { } switchProblem)
        {
            shown["recovery_problem"] = switchProblem;
        }

        return shown;
    }

    /// <summary>What recovery does when no switch row exists. This is the one place to change the default.</summary>
    internal const bool RecoveryDefaultOn = WatchModule.RecoveryDefaultOn;

    /// <summary>
    /// Whether recovery is on. With no switch row it is <see cref="RecoveryDefaultOn"/>; a row whose <c>on</c> is a boolean says so
    /// (whoever wrote it, since switching off is the safe direction); a row that cannot be read, parsed or is not a map switches
    /// recovery off and <see cref="RecoveryProblem"/> says why.
    /// </summary>
    public bool RecoveryEnabled => ReadSwitch().On;

    /// <summary>Why the switch row could not be used, or null when it is absent or valid.</summary>
    public string? RecoveryProblem => ReadSwitch().Problem;

    private (bool On, string? Problem) ReadSwitch() => WatchModule.ReadRecoverySwitch(g.HomeDocs);

    /// <summary>Who wrote the switch: <c>user</c>, <c>orchestrator</c> (with team and session) or <c>daemon</c> (with team, member and the reason).</summary>
    private sealed record SwitchAuthor(string By, string? Team, string? SessionId, string? Member, string? Reason);

    private static readonly string[] SwitchAttributionKeys = ["by", "team", "session_id", "member", "reason"];

    private string? SwitchValue(string key)
    {
        try
        {
            return g.HomeDocs.Read(WatchModule.RecoverySwitchKey) is { } text && JsonText.Parse(text) is { IsMap: true } doc && doc.Member(key) is { IsText: true } v ? v.AsString() : null;
        }
        catch (JsonParseException)
        {
            return null;
        }
    }

    /// <summary>The sweep's own brake: switches recovery off after a member was left dead. There is no way for the daemon to switch it on.</summary>
    public void DisableRecovery(StallTeam team, string member, string reason) =>
        SetEnabled(false, new SwitchAuthor("daemon", team.Name, null, member, reason));

    private void SetEnabled(bool on, SwitchAuthor author)
    {
        lock (_controlLock)
        {
            var doc = new JsonObject
            {
                ["on"] = JsonValue.FromBool(on),
                ["by"] = author.By,
                ["at"] = g.Io.Clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["req_id"] = Guid.NewGuid().ToString("N"),
            };
            if (author.Team is { } t) { doc["team"] = t; }
            if (author.SessionId is { } sid) { doc["session_id"] = sid; }
            if (author.Member is { } m) { doc["member"] = m; }
            if (author.Reason is { } r) { doc["reason"] = r; }
            g.HomeDocs.Write(WatchModule.RecoverySwitchKey, JsonText.Stringify(JsonValue.FromObject(doc), 0));
        }
    }

    /// <summary>The team this daemon watches under that name in that hierarchy directory, or null.</summary>
    internal StallTeam? Team(string hierarchyDir, string teamName) => TeamAt(hierarchyDir, teamName);

    private StallTeam? TeamAt(string hierarchyDir, string teamName)
    {
        var wanted = SamePath(hierarchyDir);
        foreach (var team in teamsToScan is null ? stall.Teams() : teamsToScan())
        {
            if (team.Name == teamName && SamePath(team.HierarchyDir) == wanted)
            {
                return team;
            }
        }

        return null;
    }

    private string SamePath(string path)
    {
        try
        {
            return g.Io.Files.RealPath(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    public void WriteRecord(StallTeam team, string member, JsonObject record)
    {
        lock (_controlLock)
        {
            WriteRow(team, member, false, record);
        }
    }

    public string? AgentStatus(StallTeam team, BareRow row)
    {
        JsonArray agents;
        try
        {
            agents = g.Launch.HerdrFor(g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name)).Agents();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }

        var inPane = agents.Where(a => a.Member("pane_id") is { IsText: true } p && p.AsString() == row.Member.PaneId).ToList();
        var key = team.HierarchyDir + "|" + team.Name + "/" + row.Name;
        if (inPane.Count == 1)
        {
            lock (_paneMissing)
            {
                _ = _paneMissing.Remove(key);
            }

            return inPane[0].Member("agent_status") is { IsText: true } status ? status.AsString() : null;
        }

        bool first;
        lock (_paneMissing)
        {
            first = _paneMissing.Add(key);
        }

        if (first)
        {
            stall.Journal(new JsonObject
            {
                ["at"] = g.Io.Clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["team"] = team.Name,
                ["kind"] = "bare-pane-missing",
                ["member"] = row.Name,
                ["pane"] = row.Member.PaneId,
                ["reason"] = $"herdr's agent list has {inPane.Count} agents in pane {row.Member.PaneId}, so {row.Name} cannot be seen idle and is not relaunched automatically",
            });
        }

        return null;
    }

    private readonly HashSet<string> _paneMissing = new(StringComparer.Ordinal);

    public IReadOnlyList<string> OpenWork(StallTeam team, BareRow row) =>
        stall.OpenWork(team, new StallMember(row.Name, row.Role, row.Member.PaneId, false));

    public int BackgroundOpen(StallTeam team, string? sessionId) =>
        sessionId is { Length: > 0 } && g.Hierarchy.Status.ReadActivityRecord(team.HierarchyDir, Ah.Core.Json.JsonValue.FromString(sessionId)).Member("bg_open") is { IsNumber: true } open
            ? (int)open.AsNumber()
            : 0;

    public string? ReBrief(StallTeam team, BareRow row) =>
        AhPaths.RepoRootOf(team.HierarchyDir) is { } root
            ? _relaunch.Value.ReBrief(team.HierarchyDir, root, team.Name, row.Name)
            : $"the project root of {team.HierarchyDir} is unknown";

    public bool Terminate(int pid, string? pidStart)
    {
        if (!LaunchModule.IsSignalablePid(pid) || pidStart is null || !StillThere(pid, pidStart))
        {
            return false;
        }

        // a copied home's rows name another daemon's live sessions; only a process this daemon has heard is its own (b267)
        if (heard?.Contains(pid, pidStart) != true)
        {
            stall.Journal(new JsonObject
            {
                ["at"] = g.Io.Clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["kind"] = "bare-term-skipped",
                ["pid"] = pid,
                ["reason"] = $"pid {pid} has not registered or heartbeat with this daemon since it started; not signalled",
            });
            return false;
        }

        return PlatformModule.SignalProcess(pid, SigTerm);
    }

    private const int SigTerm = 15;

    /// <summary>Whether <paramref name="pid"/> still is the Claude process that started at <paramref name="pidStart"/>.</summary>
    private bool StillThere(int pid, string? pidStart) => g.Hierarchy.Core.Liveness is { } liveness && liveness.PidIdentityLive(pid, pidStart);

    /// <summary>The process has exited only when its pid is gone or now belongs to another process; a command line that is not the harness is never taken as an exit, and neither is a pid with no recorded start.</summary>
    private bool Exited(int pid, string? pidStart) =>
        g.Hierarchy.Core.Liveness is { } liveness && liveness.PidVerdict(pid, pidStart) == Ah.Core.Time.LivenessVerdict.ProvenDead;

    public bool WaitExit(int pid, string? pidStart, TimeSpan wait)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            if (Exited(pid, pidStart))
            {
                return true;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                return false;
            }

            Thread.Sleep(250);
        }
    }

    private readonly Lazy<IMemberRelaunch> _relaunch = new(
        () => memberRelaunch ?? CommandsModule.CreateMemberRelaunch(new RosterComposition(g, g.Io.Clock, g.Environment)),
        LazyThreadSafetyMode.ExecutionAndPublication);

    public string? Relaunch(StallTeam team, BareRow row, string sessionId)
    {
        if (AhPaths.RepoRootOf(team.HierarchyDir) is not { } root)
        {
            return $"the project root of {team.HierarchyDir} is unknown";
        }

        var before = PaneOf(team, row.Name);
        var error = _relaunch.Value.Relaunch(team.HierarchyDir, root, team.Name, row.Name, sessionId);
        var after = PaneOf(team, row.Name);
        if (error is null && before is not null && after is not null && before != after)
        {
            stall.Journal(new JsonObject
            {
                ["at"] = g.Io.Clock.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                ["team"] = team.Name,
                ["kind"] = "bare-recover",
                ["member"] = row.Name,
                ["reason"] = $"restored pane {before} was gone; launched in a new pane {after}",
            });
        }

        return error;
    }

    public string? PreflightRelaunch(StallTeam team, BareRow row, string sessionId) =>
        AhPaths.RepoRootOf(team.HierarchyDir) is { } root
            ? _relaunch.Value.Preflight(team.HierarchyDir, root, team.Name, row.Name, sessionId)
            : $"the project root of {team.HierarchyDir} is unknown";

    public string? PaneOf(StallTeam team, string member) =>
        g.Roster.TeamMemberByName(team.HierarchyDir, member, team.Name) is { IsMap: true } row && row.Member("transport_id") is { IsText: true } id ? id.AsString() : null;

    public string? WorktreeList(string cwd)
    {
        var result = g.Runner.Run(new ProcessRequest(["git", "-C", cwd, "worktree", "list", "--porcelain"], Timeout: TimeSpan.FromSeconds(15)));
        return result.ExitCode == 0 ? Utf8Text.Decode(result.Stdout.Span) : null;
    }

    public bool Verified(StallTeam team, BareRow row, int oldPid, string sessionId)
    {
        if (g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name) is not { IsMap: true } record || record.AsObject().Get("members") is not { IsArray: true } list)
        {
            return false;
        }

        foreach (var r in list.AsArray())
        {
            if (r.IsMap && r.Member("name") is { IsText: true } n && n.AsString() == row.Name
                && r.Member("pid") is { Kind: JsonKind.Number } p && (int)p.AsNumber() != oldPid
                && r.Member("pid_start") is { IsText: true } s && s.AsString().Length > 0)
            {
                return RunsOnSessionWithAhLaunch((int)p.AsNumber(), row, sessionId);
            }
        }

        return false;
    }

    private bool RunsOnSessionWithAhLaunch(int pid, BareRow row, string sessionId)
    {
        if (sessionId.Length == 0 || (commandLineOf is null ? g.Io.Processes.CommandLine(pid) : commandLineOf(pid)) is not { } line)
        {
            return false;
        }

        var words = LaunchModule.WordsOf(line).ToList();
        var at = words.IndexOf("--resume");
        return at >= 0 && at + 1 < words.Count && words[at + 1] == sessionId
            && LaunchModule.ClassifyLaunch(words, row.Member.Expected, ReadLaunchFile).Verdict == AhLaunchVerdict.AhLaunch;
    }

    private string? ReadLaunchFile(string path) => g.Io.Files.Exists(path) ? g.Io.Files.ReadText(path) : null;

    public BareVerdict Check(StallTeam team, BareRow row, BareSighting? earlier) => LaunchModule.CheckBare(Facts(team), row.Member, earlier);

    public string? LastSessionId(StallTeam team, BareRow row) => Facts(team).LastSessionId(row.Name, team.Name);

    private readonly Lazy<Ah.Hierarchy.IHierarchyRecovery> _recovery = new(() => Ah.Hierarchy.HierarchyModule.CreateRecovery(g.Io.Files, g.Environment, g.Io.Clock));

    /// <summary>Where the last read of each transcript ended; kept in memory only, so a restarted daemon reads each transcript back from its end again.</summary>
    private readonly Dictionary<string, long> _transcriptOffsets = new(StringComparer.Ordinal);

    public IReadOnlyList<ApiErrorSighting> ApiErrors(IReadOnlyList<StallTeam> teams, DateTimeOffset notBefore)
    {
        var sessions = new HashSet<(string Dir, string Session)>();
        foreach (var team in teams)
        {
            if (team.OrchestratorSession.Length > 0)
            {
                _ = sessions.Add((team.HierarchyDir, team.OrchestratorSession));
            }

            foreach (var row in Rows(team))
            {
                if (LastSessionId(team, row) is { Length: > 0 } session)
                {
                    _ = sessions.Add((team.HierarchyDir, session));
                }
            }
        }

        var found = new List<ApiErrorSighting>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (dir, session) in sessions)
        {
            if (g.Hierarchy.Status.ReadActivityRecord(dir, JsonValue.FromString(session)).Member("transcript_path") is not { IsText: true } path)
            {
                continue;
            }

            _ = live.Add(path.AsString());
            if (_recovery.Value.ApiErrorsFrom(path.AsString(), _transcriptOffsets.TryGetValue(path.AsString(), out var known) ? known : null, notBefore) is not { } tail)
            {
                continue;
            }

            _transcriptOffsets[path.AsString()] = tail.Offset;
            found.AddRange(tail.Errors.Select(e => new ApiErrorSighting(session, e.Kind, e.At)));
        }

        foreach (var gone in _transcriptOffsets.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _ = _transcriptOffsets.Remove(gone);
        }

        return found;
    }

    public void SetMark(StallTeam team, string member, BareMark? mark)
    {
        if (!stall.Owner())
        {
            return;
        }

        _ = g.Hierarchy.StateCalls.UpdateTeamFile(
            g.Roster.TeamPath(team.HierarchyDir, team.Name),
            team.HierarchyDir,
            doc => Write(doc, member, mark));
    }

    /// <summary>The team record with <paramref name="member"/>'s <c>bare</c> field set to <paramref name="mark"/> (removed when null); null when nothing changes.</summary>
    internal static TeamFileOutcome? Write(JsonValue doc, string member, BareMark? mark)
    {
        if (doc.Member("members") is not { IsArray: true } members)
        {
            return null;
        }

        var next = new JsonArray();
        var changed = false;
        foreach (var row in members.AsArray())
        {
            if (row.IsMap && row.Member("name") is { IsText: true } n && n.AsString() == member)
            {
                var copy = row.AsObject().Clone();
                if (mark is null)
                {
                    changed |= copy.Remove("bare");
                }
                else
                {
                    copy["bare"] = JsonValue.FromObject(mark.ToJson());
                    changed = true;
                }

                next.Add(JsonValue.FromObject(copy));
            }
            else
            {
                next.Add(row);
            }
        }

        if (!changed)
        {
            return null;
        }

        var record = doc.AsObject().Clone();
        record["members"] = JsonValue.FromArray(next);
        return TeamFileOutcome.Write(JsonValue.FromObject(record));
    }

    private LaunchContext LaunchContextOf(StallTeam team)
    {
        var registry = g.Config.ResolveConfig(team.Cwd, new ResolveOptions());
        return new LaunchContext(team.Cwd, team.Name, Path.GetFileName(team.Cwd.TrimEnd('/')), registry);
    }

    private readonly IProcessTable _table = PlatformModule.CreateProcessTable(g.Runner);

    private BareFacts Facts(StallTeam team) => new(
        g.Launch.HerdrFor(g.Roster.ReadTeamRecord(team.HierarchyDir, team.Name)),
        (teamName, name) => g.Launch.Snapshots.SessionIdOf(team.HierarchyDir, teamName, new JsonObject { ["name"] = name }) is { IsText: true } sid ? sid.AsString() : null,
        (session, pid, pidStart, teamName, name) => Claimed(session, pid, pidStart, teamName, name, team.HierarchyDir),
        pid => _table.StartTime(pid),
        path => g.Io.Files.RealPath(path))
    {
        ReadFile = ReadLaunchFile,
    };

    /// <summary>What one sweep reads once for every member's claim check: the live registrations and each team's (team, member, pid, start) rows.</summary>
    private sealed record SweepReads(IReadOnlyList<JsonObject> Registrations, IReadOnlyList<(string Team, string Member, int Pid, string Start)> Processes);

    private SweepReads? _sweep;

    private SweepReads ReadForSweep(IReadOnlyList<StallTeam> teams)
    {
        var processes = new List<(string, string, int, string)>();
        foreach (var other in teams)
        {
            if (g.Roster.ReadTeamRecord(other.HierarchyDir, other.Name) is not { IsMap: true } record || record.AsObject().Get("members") is not { IsArray: true } rows)
            {
                continue;
            }

            foreach (var row in rows.AsArray().Where(r => r.IsMap))
            {
                if (row.Member("name") is { IsText: true } n && row.Member("pid") is { Kind: JsonKind.Number } p && row.Member("pid_start") is { IsText: true } s)
                {
                    processes.Add((other.Name, n.AsString(), (int)p.AsNumber(), s.AsString()));
                }
            }
        }

        return new SweepReads(g.Mailbox.LiveRegistrations().ToList(), processes);
    }

    internal bool Claimed(string? sessionId, int pid, string? pidStart, string team, string member, string? hierarchyDir = null)
    {
        var reads = Volatile.Read(ref _sweep);
        if (sessionId is not null)
        {
            foreach (JsonValue r in reads?.Registrations ?? g.Mailbox.LiveRegistrations().ToList())
            {
                if (r.Member("session_id") is { IsText: true } sid && sid.AsString() == sessionId
                    && AddressesOf(r).Any(a => a != member + "@" + team && !IsOwnChildTeam(a, sessionId, team, member, hierarchyDir)))
                {
                    return true;
                }
            }
        }

        if (pidStart is null)
        {
            return false;
        }

        var processes = (reads ?? ReadForSweep(teamsToScan is null ? stall.Teams() : teamsToScan())).Processes;
        return processes.Any(x => !(x.Team == team && x.Member == member) && x.Pid == pid && x.Start == pidStart);
    }

    /// <summary>
    /// Whether <paramref name="address"/> is the orchestrator address of the lane child team of the member that holds the session: a
    /// sub-orchestrator is a row of its manager's team and the orchestrator of its own, with one session and two addresses. The
    /// address must be exactly the one the manager's lane would give, the manager's own file must record the lane for this member, and
    /// the child team's file, found under the hierarchy dir by its derived name and never by a path a registration states, must
    /// name this session as its orchestrator. A caller that does not know the hierarchy dir exempts nothing.
    /// </summary>
    private bool IsOwnChildTeam(string address, string sessionId, string team, string member, string? hierarchyDir)
    {
        if (hierarchyDir is null
            || g.Roster.ReadTeamRecord(hierarchyDir, team) is not { IsMap: true } manager
            || manager.AsObject().Get("members") is not { IsArray: true } rows
            || rows.AsArray().Where(r => r.IsMap && r.Member("name") is { IsText: true } n && n.AsString() == member).Cast<JsonValue?>().FirstOrDefault() is not { } row
            || row.Member("stream") is not { IsText: true } streamValue || streamValue.AsString().Length == 0)
        {
            return false;
        }

        var stream = streamValue.AsString();
        var child = Ah.Hierarchy.HierarchyModule.LaneChildName(team, stream);
        if (address != HierarchyModule.OrchestratorAddress(child)
            || manager.Member("streams")?.Member(stream)?.Member("sub_orchestrator") is not { IsText: true } sub || sub.AsString() != member
            || g.Roster.ReadTeamRecord(hierarchyDir, child) is not { IsMap: true } childRecord
            || Ah.Hierarchy.HierarchyModule.ConfirmedLaneStream(team, manager, child, childRecord) != stream)
        {
            return false;
        }

        return childRecord.Member("orchestrator")?.Member("session_id") is { IsText: true } orchestrator && orchestrator.AsString() == sessionId;
    }

    private static IEnumerable<string> AddressesOf(JsonValue registration)
    {
        if (registration.Member("addresses") is { IsArray: true } list)
        {
            return list.AsArray().Where(a => a.IsText).Select(a => a.AsString());
        }

        return registration.Member("address") is { IsText: true } one ? [one.AsString()] : [];
    }
}
